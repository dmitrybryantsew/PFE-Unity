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

        // Maximum step height - from AS3: World.maxdy
        public const float MAX_STEP_HEIGHT = 20f;

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
