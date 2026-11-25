using UnityEngine;

namespace PFE.Systems.Map
{
    /// <summary>
    /// World constants matching AS3 World.as values.
    /// All values are from the original ActionScript 3 codebase.
    /// </summary>
    public static class WorldConstants
    {
        // Tile dimensions (pixels) - from AS3: World.tileX, World.tileY
        public const float TILE_SIZE = 40f;

        // Room dimensions (tiles) - from AS3: World.cellsX, World.cellsY
        public const int ROOM_WIDTH = 48;
        public const int ROOM_HEIGHT = 25;

        // Room dimensions (pixels)
        public static readonly Vector2 ROOM_SIZE_PIXELS = new Vector2(
            ROOM_WIDTH * TILE_SIZE,
            ROOM_HEIGHT * TILE_SIZE
        );

        // ── The LIVE land cell size ─────────────────────────────────────────────────────────────
        //
        // `ROOM_WIDTH/HEIGHT` and `ROOM_SIZE_PIXELS` above are the authored AS3 grid — the size of
        // every imported room, and the value `LandFonMath.RoomRectPixels` and the room editor are
        // defined against. A mod land may declare its own per-land canvas (RandomRooms rolls one
        // per land), and every room in that land shares it. Because only ONE land is ever live in
        // world space at a time (a land's rooms start at landPosition 0,0, so two lands cannot
        // coexist without overlapping), the live cell size is land-scoped state, set when a land is
        // initialized and reset to the authored default by the same call.
        //
        // Read it through `CellSizePixels`, never through ROOM_SIZE_PIXELS, on any path that maps a
        // land position to world pixels.

        /// <summary>Width in tiles of the land currently live in world space.</summary>
        public static int CellWidth { get; private set; } = ROOM_WIDTH;

        /// <summary>Height in tiles of the land currently live in world space.</summary>
        public static int CellHeight { get; private set; } = ROOM_HEIGHT;

        /// <summary>The live land's cell size in pixels. See the section remarks.</summary>
        public static Vector2 CellSizePixels => new Vector2(CellWidth * TILE_SIZE, CellHeight * TILE_SIZE);

        /// <summary>
        /// Declare the live land's room canvas. Ignored for a degenerate size so a caller that has no
        /// dimensions in hand cannot silently collapse the world to 0x0.
        /// </summary>
        public static void SetCellSize(int widthTiles, int heightTiles)
        {
            if (widthTiles <= 0 || heightTiles <= 0) return;
            CellWidth = widthTiles;
            CellHeight = heightTiles;
        }

        /// <summary>Restore the authored 48x25 grid. Called when a land is (re)initialized.</summary>
        public static void ResetCellSize()
        {
            CellWidth = ROOM_WIDTH;
            CellHeight = ROOM_HEIGHT;
        }

        // Maximum step height - from AS3: World.maxdy
        public const float MAX_STEP_HEIGHT = 20f;

        // Interaction reach - from AS3: World.actionDist (World.as:264)
        //
        // AS3 stores this as a SQUARED distance and compares it against (dx*dx + dy*dy)
        // (UnitPlayer.as:1926, :1931), so 40000 means 200 source pixels. Source pixels are world
        // units in this project and art is imported at 100 px per unit, so 200 px = 2.0 units.
        // Both the player's target search and each interactable's own reach check read it from
        // here, so the two can never drift apart again - they were 2.5 in both places, 25% over.
        public const float ACTION_REACH_PIXELS = 200f;
        public const float ACTION_REACH = ACTION_REACH_PIXELS * 0.01f;

        // Layer constants for rendering
        public const int LAYER_BACKGROUND = 0;
        public const int LAYER_MAIN = 1;
        public const int LAYER_FOREGROUND = 2;
        public const int LAYER_UI = 3;

        // Tags for Unity GameObjects
        public const string TAG_PLAYER = "Player";
        public const string TAG_ENEMY = "Enemy";
        public const string TAG_OBJECT = "Interactable";
        public const string TAG_DOOR = "Door";
        public const string TAG_TILE = "Tile";

        // Physics layers
        public const int PHYSICS_LAYER_DEFAULT = 0;
        public const int PHYSICS_LAYER_PLAYER = 6;
        public const int PHYSICS_LAYER_ENEMY = 7;
        public const int PHYSICS_LAYER_PROPS = 8;
        public const int PHYSICS_LAYER_TRIGGERS = 9;

        // World generation constants
        public const int MIN_LOC_X = 0;
        public const int MIN_LOC_Y = 0;
        public const int MIN_LOC_Z = 0;
        public const int MAX_LOC_Z = 2;  // Supports background layers (0-1)

        // Door configuration - from AS3: 12 doors per side, 24 total (with mirrored)???
        public const int DOORS_PER_ROOM = 24;
        public const int DOOR_SIDE_COUNT = 6;  // 6 doors per side
    }
}
