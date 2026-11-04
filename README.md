# Repair Paste

Space Engineers 2 client plugin that pastes a blueprint over the ship it was made
from, so the blocks destroyed on the ship can be re-placed from its blueprint.

The game's paste tool refuses to place a blueprint where any of its blocks would
overlap an existing one. With this plugin, when the paste tool aims at a ship which
matches the blueprint:

- The preview snaps onto the ship, in the rotation and position where the most
  blueprint blocks match the ship's blocks (same block type, cells and orientation).
- Placing it keeps the blocks already there as they are (damage, color, settings)
  and places only the blueprint blocks whose cells are empty.
- Sub-grids of the blueprint (rotor, piston and hinge tops with their blocks) are
  left out of a repair paste.

## Usage

1. Have the ship as a blueprint, or copy it (Ctrl+C) while it is intact.
2. Load it into the paste tool (Place on the blueprint's details, or Ctrl+V for a copy).
3. Aim at the damaged ship. The preview jumps onto it.
4. Place it.

Aiming away from the ship turns the snapping off, so the blueprint can still be
pasted next to the ship as usual.

## Safety

A paste over existing blocks only goes ahead if the blueprint blocks landing on
taken cells are almost all (90%) the same blocks the ship has there. A blueprint of
another ship, or one not aligned with the ship, is refused like in the vanilla game.
Otherwise it would fill the empty cells of the ship with blocks in the wrong places.

## Settings

- **Enabled**: off means the vanilla paste tool.

## How it works

When the paste is sent, the blueprint blocks whose cells are taken are removed from
the pasted copy, so the server gets only the missing blocks and checks them like any
other paste (mount points, collisions, PCU, ownership). A server without the plugin
accepts it as well.

## Development

Built from the [client2-plugin-template](https://github.com/CometWorks/client2-plugin-template).
Run `setup.py` to point `Directory.Build.props.user` to the game, then add the
working copy to Pulsar as a development folder.
