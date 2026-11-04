using System;
using System.Collections.Generic;
using HarmonyLib;
using Keen.Game2.Client.GameSystems.BlockPlacement.BlockPlacer;
using Keen.Game2.Simulation.GameSystems.BlockPlacement;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.GameSystems.PCU;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.VRage.Core.Game.Extensions;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.DCS.ObjectBuilders;
using Keen.VRage.Library.Diagnostics;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Multiplayer.Replications.FailureStrategies;
using Keen.VRage.Multiplayer.Utils;

namespace ClientPlugin.Patches;

using PlacementCheck = (BuildResultEnum Result, bool? NewGrid, bool? SupportedInVoxel);
using TransformsData = BlockPlacerEntityComponent.TransformsData;

// The paste tool refuses to place a blueprint where any of its cells is taken.
// These patches snap the paste preview onto the ship the blueprint was made from, accept it
// there if some of its blocks are missing, then send only the missing blocks to the server.
// The server runs its usual checks on what it gets, so a vanilla host accepts the paste too.
[HarmonyPatch]
public static class PastePatches
{
    private const BuildResultEnum OverlapFailures =
        BuildResultEnum.FailedOccupiedGridCells
        | BuildResultEnum.FailedMissingMountPoint
        | BuildResultEnum.FailedOccupiedCollision;

    private static readonly AccessTools.FieldRef<BlockPlacementValidation, IPCU> PcuRef =
        AccessTools.FieldRefAccess<BlockPlacementValidation, IPCU>("_pcu");

    private static readonly AccessTools.FieldRef<
        BlockPlacementValidation,
        IOwnerProvider
    > OwnerRef = AccessTools.FieldRefAccess<BlockPlacementValidation, IOwnerProvider>("_owner");

    // The paste tool whose preview is being updated on this thread, or null
    [ThreadStatic]
    private static BlockPlacerEntityComponent pasteTool;

    // Grid the paste tool aims at, only while the paste would go onto a real grid
    private static GridPlacementTarget TargetGrid(PlacementTarget alignedGrid) =>
        pasteTool?.AlignedBlock.Target is CubeBlockPlacementTarget
        && alignedGrid is GridPlacementTarget grid
            ? grid
            : null;

    private static MatrixI Placement(in TransformsData transforms)
    {
        var placement = transforms.Rotation;
        placement.Translation = transforms.GetMountedPosition();
        return placement;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(BlockPlacerEntityComponent), "BlockPlacerActive")]
    private static void BlockPlacerActivePrefix(BlockPlacerEntityComponent __instance)
    {
        pasteTool = Config.Current.Enabled && __instance.IsPasteMode ? __instance : null;
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(BlockPlacerEntityComponent), "BlockPlacerActive")]
    private static void BlockPlacerActiveFinalizer()
    {
        pasteTool = null;
    }

    // Moves the preview onto the blocks of the grid which match the blueprint, so a damaged ship
    // can be repaired from its own blueprint. Placing it over the grid by hand is not practical,
    // the vanilla snapping always puts the preview next to the aimed block.
    [HarmonyPrefix]
    [HarmonyPatch(
        typeof(BlockPlacementValidation),
        nameof(BlockPlacementValidation.ValidatePlacement)
    )]
    private static void ValidatePlacementPrefix(
        ActiveBundleData activePrefab,
        ref TransformsData transforms,
        PlacementTarget alignedGrid
    )
    {
        try
        {
            // Not while placing: the placement keeps the position it started with
            if (pasteTool?.Building != null || TargetGrid(alignedGrid) is not { } target)
                return;

            var grid = target.RootEntity.Get<CubeGridComponent>();
            if (Overlay.FindAlignment(activePrefab, grid) is not { } placement)
                return;

            // Only while aiming at the part of the grid the aligned blueprint covers
            var covered = BoundingBoxI.TransformMaxInclusive(activePrefab.AABB, placement);
            covered = new BoundingBoxI(covered.Min - Vector3I.One, covered.Max + Vector3I.One);
            if (covered.ContainsMaxInclusive(transforms.RayPosition) != ContainmentType.Contains)
                return;

            var rotation = placement;
            rotation.Translation = Vector3I.Zero;
            transforms.Rotation = rotation;
            transforms.PositionOffset += placement.Translation - transforms.GetMountedPosition();
        }
        catch (Exception e)
        {
            Log.Default.WriteLine(LogSeverity.Error, $"[{Plugin.Name}] Alignment failed: {e}");
        }
    }

    // Accepts an overlapping paste if the blueprint is aligned with the grid and some of its blocks are missing
    [HarmonyPostfix]
    [HarmonyPatch(
        typeof(BlockPlacementValidation),
        nameof(BlockPlacementValidation.CheckPlacementIsPossible)
    )]
    private static void CheckPlacementIsPossiblePostfix(
        BlockPlacementValidation __instance,
        ActiveBundleData activePrefab,
        ref TransformsData transforms,
        PlacementTarget alignedGrid,
        bool earlyExitSkipCollision,
        ref PlacementCheck __result
    )
    {
        try
        {
            // The early exit calls only search for a position, the alignment overrides that anyway
            if (
                earlyExitSkipCollision
                || (__result.Result & BuildResultEnum.FailedOccupiedGridCells) == 0
                || TargetGrid(alignedGrid) is not { } target
            )
                return;

            if (Overlay.CheckRepair(activePrefab, target, Placement(transforms)) is not { } free)
                return;

            var result = __result.Result & ~OverlapFailures;
            if (
                (result & BuildResultEnum.FailedPCU) != 0
                && PcuRef(__instance).HasPCU(OwnerRef(__instance).Owner, PCUExtensions.GetPCU(free))
            )
                result &= ~BuildResultEnum.FailedPCU;

            // The server checks the mount points and collisions of the blocks it actually gets
            __result = (result, false, __result.SupportedInVoxel);
        }
        catch (Exception e)
        {
            Log.Default.WriteLine(
                LogSeverity.Error,
                $"[{Plugin.Name}] Placement check failed: {e}"
            );
        }
    }

    // Sends only the blueprint blocks which land on empty cells
    [HarmonyPrefix]
    [HarmonyPatch(typeof(BlockPlacerNetworkStories), nameof(BlockPlacerNetworkStories.PlaceOnGrid))]
    private static void PlaceOnGridPrefix(
        SkipOnReplicationFail<CubeGridComponent> grid,
        ref Detached<EntityBundle> detachedBundle,
        BlockPlacerNetworkStories.VolumePlacementArgs[] volumes
    )
    {
        try
        {
            if (
                !Config.Current.Enabled
                || volumes is not { Length: 1 }
                || volumes[0].SignedVolume != Vector3I.Zero
            )
                return;

            var bundle = detachedBundle.Value;
            if (grid.Value is not { } gridComponent || bundle == null || bundle.Roots == 0)
                return;

            var blocks = Overlay.GetBlocks(bundle.RootBuilders[0]);
            var target = new GridPlacementTarget(gridComponent);
            var placement = new MatrixI(volumes[0].Mountpoint, volumes[0].Rotation);
            var occupied = new List<int>();
            for (var i = 0; i < blocks.Length; i++)
            {
                if (!Overlay.IsFree(blocks[i], target, placement))
                    occupied.Add(i);
            }

            // Nothing to leave out, or nothing would be left: the game decides as usual
            if (occupied.Count == 0 || occupied.Count == blocks.Length)
                return;

            // The bundle is the paste tool's own, it can be pasted again
            var filtered = bundle.CloneBundle();
            var children = filtered.RootBuilders[0].GetChildren();
            for (var i = occupied.Count - 1; i >= 0; i--)
            {
                var block = children[occupied[i]];
                children.RemoveAt(occupied[i]);
                if (filtered.IndexOf(block) >= 0)
                    filtered.Remove(block);
            }

            // Sub-grids would collide with the ones the grid already has
            for (var i = filtered.Roots - 1; i > 0; i--)
                filtered.RemoveAt(i);

            detachedBundle = filtered;
            Log.Default.WriteLine(
                $"[{Plugin.Name}] Pasting {blocks.Length - occupied.Count} of {blocks.Length} blocks, {occupied.Count} are already there"
            );
        }
        catch (Exception e)
        {
            Log.Default.WriteLine(
                LogSeverity.Error,
                $"[{Plugin.Name}] Filtering the pasted blocks failed: {e}"
            );
        }
    }
}
