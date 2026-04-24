using UnityEngine;
using System.Collections.Generic;
using VContainer;
using PFE.ModAPI;
using PFE.Data.Definitions;

namespace PFE.Systems.Map
{
    /// <summary>
    /// Room template ScriptableObject.
    /// Defines the layout and properties of a room type.
    /// From AS3: Room class with XML data.
    /// </summary>
    [CreateAssetMenu(fileName = "RoomTemplate", menuName = "PFE/Map/Room Template")]
    public class RoomTemplate : ScriptableObject, IGameContent
    {
        // Identification
        [Header("Identification")]
        public string id;

        // IGameContent
        string IGameContent.ContentId => id;
        ContentType IGameContent.ContentType => ContentType.RoomTemplate;
        [Tooltip("Room type: beg0, pass, roof, vert, surf, etc.")]
        public string type;  // beg0, pass, roof, etc.

        // Position (for specific levels)
        [Header("Position (Specific Levels Only)")]
        public Vector3Int fixedPosition = new Vector3Int(-1, -1, -1);

        // Difficulty
        [Header("Difficulty")]
        [Range(0, 20)]
        public int difficultyLevel = 0;

        // Instance limits
        [Header("Generation")]
        [Tooltip("Maximum instances in random world (kol in AS3)")]
        public int maxInstances = 2;

        [Tooltip("Can be randomly selected (rnd in AS3)")]
        public bool allowRandom = true;

        // Background layer
        [Header("Background Layer")]
        [Tooltip("Background room ID (creates parallax layer)")]
        public string backgroundRoomId = "";

        [Tooltip("Background decoration entries from AS3 <back> tags")]
        public List<BackgroundDecorationData> backgroundDecorations = new List<BackgroundDecorationData>();

        // Door configuration - 24 doors total
        // 0-5: Right side doors (top to bottom)
        // 6-11: Bottom side doors (left to right)
        // 12-17: Left side doors (mirrored)
        // 18-23: Top side doors (mirrored)
        [Header("Doors")]
        [Tooltip("Door quality for each of 24 door slots")]
        [Range(0, 5)]
        public int[] doorQuality = new int[24];

        // Tile data (stored as strings matching AS3 format)
        // Each row is a string of 48 characters
        [Header("Tiles")]
        [Tooltip("27 rows, each 48 characters representing tile types")]
        [TextArea(27, 50)]
        public string tileDataString = "";

        // Objects
        [Header("Objects")]
        public List<ObjectSpawnData> objects = new List<ObjectSpawnData>();

        // Spawn points
        [Header("Spawn Points")]
        public List<SpawnPointData> spawnPoints = new List<SpawnPointData>();

        // Environment
        [Header("Environment")]
        public RoomEnvironmentData environment = new RoomEnvironmentData();

        /// <summary>
        /// Parse tile data string into 2D array.
        /// Each character represents a tile type.
        /// </summary>

        public TileData[,] ParseTiles(TileFormDatabase formDb)
        {
            if (string.IsNullOrWhiteSpace(tileDataString))
                return new TileData[WorldConstants.ROOM_WIDTH, WorldConstants.ROOM_HEIGHT];

            string[] rows = tileDataString.Replace("\r\n", "\n").Split('\n');
            return TileDecoder.ParseRoom(rows, formDb, false,
                WorldConstants.ROOM_WIDTH, WorldConstants.ROOM_HEIGHT);
        }
        /// <summary>
        /// Parse single tile character into TileData.
        /// From AS3: Location.dec() function
        ///
        /// Tile Character Reference (from AS3 XML data):
        /// . = Air (walkable, empty)
        /// C = Ceiling/Wall (solid, blocks movement)
        /// E = Empty/Wall Air (air but visually distinct)
        /// K = Killer/Hazard (damages player)
        /// Б = Russian 'Be' - Wall/solid block
        /// А = Russian 'A' - Platform or special floor
        /// H = Heavy/Dense wall
        /// _ = Air (alternative empty space)
        /// * = Hazard/special
        /// - = Platform/walkable surface
        /// </summary>
        private TileData ParseTileChar(int x, int y, char tileChar)
        {
            TileData tile = new TileData
            {
                gridPosition = new Vector2Int(x, y)
            };

            // Handle underscore and dot as air (most common)
            if (tileChar == '_' || tileChar == '.')
            {
                tile.physicsType = TilePhysicsType.Air;
                return tile;
            }

            // Parse tile character (extended version for AS3 XML)
            switch (tileChar)
            {
                // Air/Empty tiles
                case 'A':  // Air (explicit)
                case 'E':  // Empty air variant
                    tile.physicsType = TilePhysicsType.Air;
                    break;

                // Wall/Solid tiles
                case 'C':  // Ceiling/Wall (most common wall type)
                case 'B':  // Block/Wall
                case 'H':  // Heavy wall
                    tile.physicsType = TilePhysicsType.Wall;
                    tile.indestructible = false;
                    tile.hitPoints = 1000;
                    tile.SetFrontGraphic("tWall1");
                    tile.visualId = 1;
                    break;

                // Russian characters (common in XML)
                case 'Б':  // Russian 'Be' - Wall/solid block
                    tile.physicsType = TilePhysicsType.Wall;
                    tile.indestructible = true;
                    tile.hitPoints = 5000;
                    tile.SetFrontGraphic("tWallHeavy");
                    tile.visualId = 2;
                    break;
                case 'А':  // Russian 'A' - Platform or special floor
                case 'Г':  // Russian 'G' - Platform variant
                    tile.physicsType = TilePhysicsType.Platform;
                    tile.SetFrontGraphic("tPlat1");
                    tile.visualId = 10;
                    break;

                // Platform tiles
                case 'P':  // Platform
                case '-':  // Platform variant
                case 'F':  // Floor variant
                case 'G':  // Ground/platform variant
                case 'J':  // Jump-through platform variant
                case 'I':  // Additional platform marker from AS3 dumps (Latin I)
                case 'І':  // Additional platform marker from AS3 dumps (Cyrillic І)
                    tile.physicsType = TilePhysicsType.Platform;
                    tile.SetFrontGraphic("tPlat1");
                    tile.visualId = 10;
                    break;

                case 'L':  // Ladder/line support tile (treated as wall for collision safety)
                case ';':  // Rare punctuation tile from AS3 dumps, treated as wall variant
                    tile.physicsType = TilePhysicsType.Wall;
                    tile.indestructible = false;
                    tile.hitPoints = 1000;
                    tile.SetFrontGraphic("tWall1");
                    tile.visualId = 1;
                    break;

                // Stair/Slope tiles
                case 'S':  // Stair
                    tile.physicsType = TilePhysicsType.Stair;
                    tile.SetFrontGraphic("tStair1");
                    tile.visualId = 20;
                    break;
                case '/':  // Slope up (low-left to high-right)
                    tile.physicsType = TilePhysicsType.Stair;
                    tile.slopeType = 1;
                    tile.SetFrontGraphic("tSlope1");
                    tile.visualId = 30;
                    break;
                case '\\':  // Slope down (high-left to low-right)
                    tile.physicsType = TilePhysicsType.Stair;
                    tile.slopeType = -1;
                    tile.SetFrontGraphic("tSlope2");
                    tile.visualId = 31;
                    break;

                // Hazard tiles
                case 'K':  // Killer/Hazard
                case '*':  // Hazard variant
                    tile.physicsType = TilePhysicsType.Wall;
                    tile.material = MaterialType.Default;  // TODO: Add Hazard material type
                    tile.SetFrontGraphic("tHazard");
                    tile.visualId = 50;
                    break;

                // Special tiles
                case 'D':  // Door
                    tile.physicsType = TilePhysicsType.Platform;
                    tile.SetFrontGraphic("tDoor");
                    tile.visualId = 100;
                    break;

                default:
                    // Default unknown tiles to air (safer than wall)
                    tile.physicsType = TilePhysicsType.Air;
                    Debug.LogWarning($"Unknown tile character '{tileChar}' at ({x}, {y}), defaulting to air");
                    break;
            }

            return tile;
        }

        /// <summary>
        /// Check if this is a starting room template.
        /// </summary>
        public bool IsStartingRoom()
        {
            return type == "beg0" || type == "beg1";
        }

        /// <summary>
        /// Check if this is an ending room template.
        /// </summary>
        public bool IsEndingRoom()
        {
            return type == "end" || type == "end1";
        }
    }

    /// <summary>
    /// Object spawn data for room templates.
    /// From AS3: XML obj elements
    /// </summary>
    [System.Serializable]
    public class ObjectSpawnData
    {
        public string type;  // box, unit, door, obj, decor, area
        public string id;    // Specific object ID
        public string definitionId;
        public MapObjectDefinition definition;
        public Vector2Int tileCoord;
        public string code;
        public string uid;
        public List<MapObjectAttributeData> attributes = new List<MapObjectAttributeData>();
        public List<MapObjectItemData> items = new List<MapObjectItemData>();
        public List<MapObjectScriptData> scripts = new List<MapObjectScriptData>();
        [TextArea(3, 20)]
        public string parameters;  // XML attributes as string

        public void EnsureStructuredData()
        {
            if (string.IsNullOrWhiteSpace(definitionId) && !string.IsNullOrWhiteSpace(id))
            {
                definitionId = id;
            }

            if ((attributes == null || attributes.Count == 0) && !string.IsNullOrWhiteSpace(parameters))
            {
                attributes = MapObjectDataUtility.ParseLegacyParameters(parameters, out string parsedCode, out string parsedUid);
                if (string.IsNullOrEmpty(code))
                {
                    code = parsedCode;
                }

                if (string.IsNullOrEmpty(uid))
                {
                    uid = parsedUid;
                }
            }

            attributes ??= new List<MapObjectAttributeData>();
            items ??= new List<MapObjectItemData>();
            scripts ??= new List<MapObjectScriptData>();
        }

        public string GetAttribute(string key, string defaultValue = "")
        {
            EnsureStructuredData();

            if (string.Equals(key, "code", System.StringComparison.OrdinalIgnoreCase))
            {
                return string.IsNullOrEmpty(code) ? defaultValue : code;
            }

            if (string.Equals(key, "uid", System.StringComparison.OrdinalIgnoreCase))
            {
                return string.IsNullOrEmpty(uid) ? defaultValue : uid;
            }

            return MapObjectDataUtility.GetAttribute(attributes, key, defaultValue);
        }

        public string GetResolvedDefinitionId()
        {
            if (definition != null && !string.IsNullOrWhiteSpace(definition.objectId))
            {
                return definition.objectId;
            }

            if (!string.IsNullOrWhiteSpace(definitionId))
            {
                return definitionId;
            }

            return id ?? string.Empty;
        }

        public void ApplyResolvedDefinition(MapObjectDefinition resolvedDefinition)
        {
            definition = resolvedDefinition;
            if (resolvedDefinition != null && !string.IsNullOrWhiteSpace(resolvedDefinition.objectId))
            {
                definitionId = resolvedDefinition.objectId;
                if (string.IsNullOrWhiteSpace(type))
                {
                    type = resolvedDefinition.GetResolvedPlacementType();
                }
            }
        }

        public void RefreshLegacyParameters()
        {
            EnsureStructuredData();
            parameters = MapObjectDataUtility.BuildLegacyParameters(code, uid, attributes);
        }
    }

    /// <summary>
    /// Spawn point data for room templates.
    /// </summary>
    [System.Serializable]
    public class SpawnPointData
    {
        public Vector2Int tileCoord;
        public SpawnType type;
        public string unitId = "";  // For enemy spawns
        public float facingDirection = 1f;
    }

    /// <summary>
    /// Room environment data for templates.
    /// </summary>
    [System.Serializable]
    public class RoomEnvironmentData
    {
        public string musicTrack = ""; // empty = keep current track; set to a valid ID from MusicCatalog
        public string colorScheme = "";
        public string backgroundColorScheme = "";
        public string backgroundWall = "";
        public int backgroundForm = 0;
        public bool transparentBackground = false;
        public bool hasSky = false;
        public int borderType = 1;
        public bool noBlackReveal = false;
        public float visibilityMultiplier = 1f;
        public int lightsOn = 0;
        public bool returnsToDarkness = false;
        public int waterLevel = 100;
        public int waterType = 0;
        public float waterOpacity = 0f;
        public float waterDamage = 0f;
        public int waterDamageType = 7;
        public float radiation = 0f;
        public float radiationDamage = 0f;
        public int darkness = 0;
    }

    /// <summary>
    /// Background decoration placement from AS3 <back> entries.
    /// </summary>
    [System.Serializable]
    public class BackgroundDecorationData
    {
        public string id;
        public Vector2Int tileCoord;
    }
}
