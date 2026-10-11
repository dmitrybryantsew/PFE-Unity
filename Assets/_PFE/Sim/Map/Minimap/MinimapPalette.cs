using UnityEngine;

namespace PFE.Systems.Map.Minimap
{
    /// <summary>
    /// The one tile's worth of state the minimap palette colours by — a primitive-only view of
    /// <see cref="PFE.Systems.Map.TileData"/>.
    ///
    /// <para><b>Why primitives and not the tile itself.</b> The palette is the oracle's colour table and
    /// it must be verifiable offline; <see cref="PFE.Systems.Map.TileData"/> lives in <c>PFE.Data</c>,
    /// which this assembly (<c>PFE.Sim</c>) does not reference. Taking the handful of booleans the
    /// oracle actually branches on also keeps the palette a faithful transcription rather than a
    /// re-reading of the tile model.</para>
    /// </summary>
    public readonly struct MinimapTileFacts
    {
        /// <summary>AS3 <c>phis == 1</c> — a real wall.</summary>
        public readonly bool IsWall;

        /// <summary>AS3 <c>shelf</c> — the one-way catwalk. The port writes
        /// <c>TilePhysicsType.Platform</c> only for a shelf form, so the two are the same fact.</summary>
        public readonly bool IsShelf;

        /// <summary>AS3 <c>diagon != 0</c> — a slope/ramp.</summary>
        public readonly bool IsSlope;

        /// <summary>AS3 <c>stair != 0</c> — a ladder.</summary>
        public readonly bool IsStair;

        /// <summary>AS3 <c>water</c>.</summary>
        public readonly bool HasWater;

        /// <summary>AS3 <c>indestruct</c> — only meaningful on a wall.</summary>
        public readonly bool IsIndestructible;

        /// <summary>AS3 <c>door</c> — a door covers this tile.</summary>
        public readonly bool HasDoor;

        /// <summary>AS3 <c>hp</c> — read only for a wall, and only for the <c>&lt; 100</c> branch.</summary>
        public readonly int HitPoints;

        public MinimapTileFacts(
            bool isWall,
            bool isShelf,
            bool isSlope,
            bool isStair,
            bool hasWater,
            bool isIndestructible,
            bool hasDoor,
            int hitPoints)
        {
            IsWall = isWall;
            IsShelf = isShelf;
            IsSlope = isSlope;
            IsStair = isStair;
            HasWater = hasWater;
            IsIndestructible = isIndestructible;
            HasDoor = hasDoor;
            HitPoints = hitPoints;
        }
    }

    /// <summary>
    /// The original game's minimap palette, transcribed from the oracle, plus the handful of colours the
    /// port's own tool needs that the oracle has no counterpart for.
    ///
    /// <para><b>Where the oracle's colours come from.</b> <c>Location.drawMap</c>
    /// (<c>Location.as:2711-2817</c>) is a per-tile painter: for every tile of a room it picks a packed
    /// 24-bit RGB value and calls <c>BitmapData.setPixel32</c>, then paints object overlays on top. The
    /// decimal literals below are that function's, kept verbatim in a comment beside each colour so the
    /// transcription can be checked without opening the oracle.</para>
    ///
    /// <para><b>Alpha.</b> The oracle folds visibility into the top byte
    /// (<c>_loc6_ += Math.floor(_loc2_ * 255) * 16777216</c>, <c>:2786</c>) — it is a per-<i>tile</i>
    /// fade, from the tile's own <c>visi</c> and its right/below neighbours'. The port has no per-tile
    /// visibility (its fog is a separate mask, and <c>RoomInstance</c> carries only a per-room
    /// <c>isVisited</c>), so every colour here is opaque and the <i>room</i> is the visibility unit: a
    /// room is drawn or it is not. See <c>LandMinimapComposer</c>.</para>
    ///
    /// <para><b>What the oracle draws that this does not.</b> The <c>phis == 2</c> branch
    /// (<c>:2757-2760</c>, grate doors → <c>104794</c>) has no port input: <c>TileDecoder.MapPhysicsType</c>
    /// folds every non-zero <c>phis</c> into <c>TilePhysicsType.Wall</c>, and the port's own remarks on
    /// <c>TileData.IsSolidOrShelf</c> record that <c>phis == 2</c> reaches no shipped tile. There is no
    /// branch for it rather than a permanently-false flag.</para>
    /// </summary>
    public static class MinimapPalette
    {
        // ── Tile colours — Location.as:2724-2755, in the oracle's own branch order ──────────────

        /// <summary>Default / open ground. Oracle literal <c>13091</c> = <c>0x003323</c>.</summary>
        public static readonly Color32 Air = new Color32(0x00, 0x33, 0x23, 0xFF);

        /// <summary>Water. Oracle <c>26367</c> = <c>0x0066FF</c>.</summary>
        public static readonly Color32 Water = new Color32(0x00, 0x66, 0xFF, 0xFF);

        /// <summary>A shelf or a slope. Oracle <c>8079407</c> = <c>0x7B482F</c>.</summary>
        public static readonly Color32 ShelfOrSlope = new Color32(0x7B, 0x48, 0x2F, 0xFF);

        /// <summary>A ladder/stair. Oracle <c>6710886</c> = <c>0x666666</c>.</summary>
        public static readonly Color32 Stair = new Color32(0x66, 0x66, 0x66, 0xFF);

        /// <summary>A wall that cannot be destroyed. Oracle <c>16777215</c> = <c>0xFFFFFF</c>.</summary>
        public static readonly Color32 SolidIndestructible = new Color32(0xFF, 0xFF, 0xFF, 0xFF);

        /// <summary>A wall a door covers. Oracle <c>6525188</c> = <c>0x639104</c>.</summary>
        public static readonly Color32 SolidDoor = new Color32(0x63, 0x91, 0x04, 0xFF);

        /// <summary>A wall already below 100 hp. Oracle <c>104794</c> = <c>0x01995A</c>.</summary>
        public static readonly Color32 SolidDamaged = new Color32(0x01, 0x99, 0x5A, 0xFF);

        /// <summary>A healthy wall. Oracle <c>65433</c> = <c>0x00FF99</c>.</summary>
        public static readonly Color32 Solid = new Color32(0x00, 0xFF, 0x99, 0xFF);

        // ── Object overlays — Location.as:2792-2816 ─────────────────────────────────────────────

        /// <summary>An interactable that holds something. Oracle <c>16763904</c> = <c>0xFFCC00</c>.</summary>
        public static readonly Color32 MarkerInteractable = new Color32(0xFF, 0xCC, 0x00, 0xFF);

        /// <summary>An object that enters a prob room. Oracle <c>16711799</c> = <c>0xFF0077</c>.</summary>
        public static readonly Color32 MarkerProb = new Color32(0xFF, 0x00, 0x77, 0xFF);

        /// <summary>A checkpoint. Oracle <c>16711935</c> = <c>0xFF00FF</c>.</summary>
        public static readonly Color32 MarkerCheckpoint = new Color32(0xFF, 0x00, 0xFF, 0xFF);

        /// <summary>An NPC. Oracle <c>5570815</c> = <c>0x5500FF</c>.</summary>
        public static readonly Color32 MarkerNpc = new Color32(0x55, 0x00, 0xFF, 0xFF);

        // ── Port-side colours — no oracle counterpart ───────────────────────────────────────────

        /// <summary>No room at this cell. The oracle's <c>BitmapData</c> is allocated exactly to the
        /// land's own extent, so it has no "outside" — a debug panel that draws the whole grid needs
        /// one.</summary>
        public static readonly Color32 EmptyCell = new Color32(0x10, 0x10, 0x10, 0xFF);

        /// <summary>A visited room whose tile array is missing (a domain reload drops Unity's
        /// multidimensional arrays). Distinct from <see cref="EmptyCell"/> on purpose: "the room exists
        /// but I cannot read it" is a different answer from "there is no room here".</summary>
        public static readonly Color32 NoTileData = new Color32(0x50, 0x20, 0x60, 0xFF);

        /// <summary>The outline of the room the player is standing in. Not in the oracle — the oracle
        /// draws a <c>plTag</c> sprite at the player's position instead, which this panel also does
        /// (see <see cref="Player"/>); the outline is the coarse answer for a room whose tile grid is
        /// unavailable.</summary>
        public static readonly Color32 CurrentRoomOutline = new Color32(0xFF, 0xFF, 0x00, 0xFF);

        /// <summary>The player. The oracle's <c>plTag</c> is an art sprite; this is the debug stand-in.</summary>
        public static readonly Color32 Player = new Color32(0xFF, 0x30, 0x30, 0xFF);

        /// <summary>A door opening, stamped from <c>DoorInstance.tilePosition</c>. The oracle gets this
        /// from the tile's own <c>door</c> flag, which a <i>closed</i> door writes; an open door's tiles
        /// are plain ground in both, so this marker is what keeps a door visible when it is open.</summary>
        public static readonly Color32 MarkerDoor = new Color32(0x00, 0xE0, 0xFF, 0xFF);

        /// <summary>An exit box. The oracle has no dedicated colour for it (it is an ordinary
        /// interactable, so it paints gold); the port splits it out because "where is the exit" is the
        /// question the descent loop turns on.</summary>
        public static readonly Color32 MarkerExit = new Color32(0xFF, 0x80, 0x00, 0xFF);

        /// <summary>
        /// The colour for one tile, branching in exactly the oracle's order so the last write wins as it
        /// does there (<c>Location.as:2724-2760</c>).
        /// </summary>
        public static Color32 TileColor(MinimapTileFacts tile)
        {
            Color32 color = Air;

            if (tile.HasWater)
            {
                color = Water;
            }

            if (tile.IsShelf || tile.IsSlope)
            {
                color = ShelfOrSlope;
            }

            if (tile.IsStair)
            {
                color = Stair;
            }

            if (tile.IsWall)
            {
                if (tile.IsIndestructible)
                {
                    color = SolidIndestructible;
                }
                else if (tile.HasDoor)
                {
                    color = SolidDoor;
                }
                else if (tile.HitPoints < 100)
                {
                    color = SolidDamaged;
                }
                else
                {
                    color = Solid;
                }
            }

            return color;
        }
    }
}
