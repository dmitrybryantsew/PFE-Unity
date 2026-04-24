# Room Import Notes

## Findings

- Some original rooms do not define `backwall` in the room XML itself.
- In the original AS3 game, room environment starts from the active land defaults and only then applies room-local `<options ...>` overrides.
- Unity was importing only room-local options:
  - `backgroundWall`
  - `color`
  - `colorfon`
  - `music`
  - `vis`
  - `dark`
  - water-related options
- Because of that, inherited land defaults were lost during import.

## Example

- `Base/room_1_0` has empty room `<options/>`.
- In AS3, it still gets a backdrop because `Location.buildLoc()` first copies `land.act.backwall`.
- The Base land (`rbl`) defines `backwall="tStConcrete"` in `GameData.as`.
- Unity imported `room_1_0` with empty `environment.backgroundWall`, so the backdrop renderer had nothing to draw.

## Importer Fix

The room importer now:

- parses the source room collection id for each room, for example `rooms_rbl` or `rooms_stable`
- parses land-level options from `GameData.as`
- merges inherited land options into room environment import
- keeps room-local options as the final override
- only applies inherited defaults that are unambiguous for a shared room collection

This means shared collections such as `rooms_pi` will only inherit keys that agree across all lands using that collection. Conflicting land defaults are intentionally not baked into room assets.

## What To Reimport

Reimport room templates, not graphics assets.

At minimum, reimport any room collections that rely on inherited land options:

- `Base`
- `Stable`
- `Plant`
- `Sewer`
- `Mane`
- `Canter`
- `Mbase`
- `Encl`
- `Pi`
- `Prob`
- `Camp`
- `Serial`
- `Serial2`

If you only care about the currently observed bug, reimporting `Base` is enough to fix `room_1_0`.

## Reimport Steps

1. Open `PFE/Map/Room Template Importer`.
2. Set the room source folder to the original AS3 room scripts/XML folder.
3. Make sure `GameData.as` is detected in the `GameData Path` field.
4. Enable `Overwrite Existing`.
5. Import the affected room collections again.

## After Reimport

Verify these examples:

- `Assets/_PFE/Data/Resources/Rooms/Base/room_1_0.asset`
  - `environment.backgroundWall` should now be `tStConcrete`
- rooms with explicit room-local `backwall` should keep their own value
- rooms with inherited `color`, `music`, `dark`, or `vis` should now reflect those defaults in the imported environment
