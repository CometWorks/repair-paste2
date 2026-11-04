using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Keen.Game2.Client.GameSystems.BlockPlacement.BlockPlacer;
using Keen.Game2.Simulation.GameSystems.BlockPlacement;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Core.Game.Extensions;
using Keen.VRage.DCS.Components;
using Keen.VRage.DCS.ObjectBuilders;
using Keen.VRage.Library.Collections;
using Keen.VRage.Library.Diagnostics;
using Keen.VRage.Library.Mathematics;

namespace ClientPlugin;

/// <summary>
/// Block of a blueprint (paste bundle) in the bundle's own grid space.
/// </summary>
public readonly struct PrefabBlock(
    EntityObjectBuilder builder,
    CubeBlockDefinition definition,
    BoundingBoxI box,
    BoundingBoxI[] cells,
    Vector3I forward,
    Vector3I up
)
{
    public readonly EntityObjectBuilder Builder = builder;
    public readonly CubeBlockDefinition Definition = definition;

    /// <summary>Bounding box of the occupied cells (max inclusive)</summary>
    public readonly BoundingBoxI Box = box;

    /// <summary>Occupied cell groups (max inclusive)</summary>
    public readonly BoundingBoxI[] Cells = cells;

    public readonly Vector3I Forward = forward;
    public readonly Vector3I Up = up;
}

/// <summary>
/// Placing a blueprint over a grid which already has some of its blocks.
/// </summary>
public static class Overlay
{
    private static readonly ConditionalWeakTable<ActiveBundleData, PrefabBlock[]> PrefabCache =
        new();

    public static PrefabBlock[] GetBlocks(ActiveBundleData prefab) =>
        PrefabCache.GetValue(prefab, p => GetBlocks(p.Bundle.GetRoot()));

    /// <summary>
    /// Blocks of the root (main) grid of a bundle, sub-grids are not included.
    /// </summary>
    public static PrefabBlock[] GetBlocks(EntityObjectBuilder gridBuilder)
    {
        var children = gridBuilder.GetChildren();
        var blocks = new PrefabBlock[children.Count];
        using var cells = new PooledList<BoundingBoxI>();
        for (var i = 0; i < blocks.Length; i++)
        {
            var builder = children[i];
            cells.Clear();
            var box = BoundingBoxI.CreateInvalid();
            ComplexTopologyAggregator.GetOccupiedCellsGroup(builder, int.MaxValue, cells, ref box);
            var orientation = builder
                .OB<ChildTransformComponent, ChildTransformComponentObjectBuilder>()
                .Transform.Orientation;
            blocks[i] = new PrefabBlock(
                builder,
                builder.Definition<CubeBlockComponent, CubeBlockDefinition>(),
                box,
                cells.Span.ToArray(),
                new Vector3I(Vector3.SnappedToNearestAxis(orientation.GetForward())),
                new Vector3I(Vector3.SnappedToNearestAxis(orientation.GetUp()))
            );
        }

        return blocks;
    }

    /// <summary>
    /// Whether the block would land on empty cells of the target with the placement transformation applied.
    /// </summary>
    public static bool IsFree(in PrefabBlock block, PlacementTarget target, in MatrixI placement)
    {
        foreach (var cells in block.Cells)
        {
            if (!target.IsAreaEmpty(BoundingBoxI.TransformMaxInclusive(cells, placement)))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Whether the grid has the same block (same definition, cells and orientation) where the blueprint block would land.
    /// </summary>
    public static bool Matches(in PrefabBlock block, CubeGridComponent grid, in MatrixI placement)
    {
        if (block.Cells.Length == 0)
            return false;

        // The corner of the bounding box is not always an occupied cell, the first cell group's is
        var cell = BoundingBoxI.TransformMaxInclusive(block.Cells[0], placement).Min;
        var existing = grid.CubeBlockStorage.TryGetCubeBlock(cell);
        if (
            existing == null
            || existing.Definition != block.Definition
            || existing.AABB != BoundingBoxI.TransformMaxInclusive(block.Box, placement)
        )
            return false;

        var orientation = existing.Data.GetRelativeTransform().Orientation;
        return Vector3I.TransformNormal(block.Forward, placement)
                == new Vector3I(Vector3.SnappedToNearestAxis(orientation.GetForward()))
            && Vector3I.TransformNormal(block.Up, placement)
                == new Vector3I(Vector3.SnappedToNearestAxis(orientation.GetUp()));
    }

    /// <summary>
    /// Whether the blueprint can be pasted over the grid: some of its blocks land on empty cells,
    /// and the ones landing on taken cells are mostly the same blocks as the grid has there.
    /// A blueprint which is not aligned with the ship it was made from is refused like in the vanilla game,
    /// otherwise it would fill the empty cells of the grid with blocks in the wrong places.
    /// </summary>
    public static bool CanRepair(
        PrefabBlock[] blocks,
        PlacementTarget target,
        CubeGridComponent grid,
        in MatrixI placement,
        List<EntityObjectBuilder> free = null
    )
    {
        int freeCount = 0,
            matching = 0,
            taken = 0;
        foreach (var block in blocks)
        {
            if (IsFree(block, target, placement))
            {
                freeCount++;
                free?.Add(block.Builder);
                continue;
            }

            taken++;
            if (Matches(block, grid, placement))
                matching++;
        }

        return freeCount > 0 && matching > 0 && (taken - matching) * 10 <= taken;
    }

    private record struct RepairKey(
        ActiveBundleData Prefab,
        Entity Grid,
        int GridBlocks,
        MatrixI Placement
    );

    private static RepairKey lastRepairKey;
    private static List<EntityObjectBuilder> lastRepair;
    private static long lastRepairTimestamp;
    private const double RepairRefreshSeconds = 2.0;

    /// <summary>
    /// The blueprint blocks to place if the blueprint can be pasted over the grid with this placement, otherwise null.
    /// Cached while the grid does not change, the check runs every frame while aiming and big blueprints have many blocks.
    /// </summary>
    public static List<EntityObjectBuilder> CheckRepair(
        ActiveBundleData prefab,
        GridPlacementTarget target,
        in MatrixI placement
    )
    {
        var grid = target.RootEntity.Get<CubeGridComponent>();
        var key = new RepairKey(
            prefab,
            grid.Entity,
            grid.CubeBlockStorage.CubeBlockCount,
            placement
        );
        if (
            key == lastRepairKey
            && Stopwatch.GetElapsedTime(lastRepairTimestamp).TotalSeconds < RepairRefreshSeconds
        )
            return lastRepair;

        var free = new List<EntityObjectBuilder>();
        lastRepair = CanRepair(GetBlocks(prefab), target, grid, placement, free) ? free : null;
        lastRepairKey = key;
        lastRepairTimestamp = Stopwatch.GetTimestamp();
        return lastRepair;
    }

    private record struct AlignmentKey(ActiveBundleData Prefab, Entity Grid, int GridBlocks);

    private static AlignmentKey lastKey;
    private static MatrixI? lastAlignment;
    private static long lastAlignmentTimestamp;
    private const double AlignmentRefreshSeconds = 2.0;
    private const int MaxCandidates = 64;
    private const int SampleSize = 32;

    private static readonly MatrixI[] Rotations = GetRotations();

    private static MatrixI[] GetRotations()
    {
        var rotations = new List<MatrixI>();
        foreach (var forward in Base6Directions.EnumDirections)
        {
            foreach (var up in Base6Directions.EnumDirections)
            {
                if (up.GetAxis() != forward.GetAxis())
                    rotations.Add(new MatrixI(forward, up));
            }
        }

        return rotations.ToArray();
    }

    /// <summary>
    /// Finds the rotation and position where the blueprint matches the existing blocks of the grid best.
    /// Returns the placement (rotation and the grid position of the blueprint's origin), or null if there is no good match.
    /// </summary>
    public static MatrixI? FindAlignment(ActiveBundleData prefab, CubeGridComponent grid)
    {
        var key = new AlignmentKey(prefab, grid.Entity, grid.CubeBlockStorage.CubeBlockCount);
        if (
            key == lastKey
            && Stopwatch.GetElapsedTime(lastAlignmentTimestamp).TotalSeconds
                < AlignmentRefreshSeconds
        )
            return lastAlignment;

        var start = Stopwatch.GetTimestamp();
        var alignment = ComputeAlignment(GetBlocks(prefab), grid, out var matches);
        var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        if (key != lastKey || !Nullable.Equals(alignment, lastAlignment))
            Log.Default.WriteLine(
                alignment is { } placement
                    ? $"[{Plugin.Name}] Blueprint aligned to the grid at {placement.Translation} facing {placement.Forward}/{placement.Up}, {matches} blocks match ({elapsed:F1} ms)"
                    : $"[{Plugin.Name}] No alignment of the blueprint to the grid ({elapsed:F1} ms)"
            );

        lastKey = key;
        lastAlignmentTimestamp = Stopwatch.GetTimestamp();
        lastAlignment = alignment;
        return alignment;
    }

    private static MatrixI? ComputeAlignment(
        PrefabBlock[] blocks,
        CubeGridComponent grid,
        out int matches
    )
    {
        matches = 0;
        if (blocks.Length < 2)
            return null;

        var gridBlocks = new Dictionary<CubeBlockDefinition, List<CubeBlockComponent>>();
        var gridBlockCount = 0;
        grid.VisitAllBlocksWithComponent<CubeBlockComponent>(
            block =>
            {
                if (!gridBlocks.TryGetValue(block.Definition, out var list))
                    gridBlocks[block.Definition] = list = new List<CubeBlockComponent>();
                list.Add(block);
                gridBlockCount++;
            },
            includeSubgrids: false
        );

        // Anchor on the definition which gives the fewest candidate placements
        var prefabCounts = new Dictionary<CubeBlockDefinition, int>();
        foreach (var block in blocks)
            prefabCounts[block.Definition] = prefabCounts.GetValueOrDefault(block.Definition) + 1;

        CubeBlockDefinition anchor = null;
        var anchorCost = long.MaxValue;
        foreach (var (definition, count) in prefabCounts)
        {
            if (!gridBlocks.TryGetValue(definition, out var list))
                continue;

            var cost = (long)count * list.Count;
            if (cost < anchorCost)
            {
                anchor = definition;
                anchorCost = cost;
            }
        }

        if (anchor == null)
            return null;

        // Placements which put a blueprint block of the anchor type exactly onto one in the grid
        var candidates = new HashSet<MatrixI>();
        foreach (var rotation in Rotations)
        {
            foreach (var block in blocks)
            {
                if (block.Definition != anchor || candidates.Count >= MaxCandidates)
                    continue;

                var rotated = BoundingBoxI.TransformMaxInclusive(block.Box, rotation);
                foreach (var existing in gridBlocks[anchor])
                {
                    var placement = rotation;
                    placement.Translation = existing.AABB.Min - rotated.Min;
                    if (Matches(block, grid, placement))
                        candidates.Add(placement);
                }
            }
        }

        // Blocks spread over the blueprint for a quick first check of each candidate
        var step = Math.Max(1, blocks.Length / SampleSize);
        MatrixI? best = null;
        var bestScore = 0;
        foreach (var placement in candidates)
        {
            var sampled = 0;
            var sampleScore = 0;
            for (var i = 0; i < blocks.Length; i += step, sampled++)
            {
                if (Matches(blocks[i], grid, placement))
                    sampleScore++;
            }

            if (sampleScore * 4 < sampled)
                continue;

            var score = 0;
            foreach (var block in blocks)
            {
                if (Matches(block, grid, placement))
                    score++;
            }

            if (score > bestScore)
            {
                best = placement;
                bestScore = score;
            }
        }

        // At least half of the smaller of the two has to match, otherwise it is not the same ship
        matches = bestScore;
        if (bestScore < 2 || bestScore * 2 < Math.Min(blocks.Length, gridBlockCount))
            return null;

        return best;
    }
}
