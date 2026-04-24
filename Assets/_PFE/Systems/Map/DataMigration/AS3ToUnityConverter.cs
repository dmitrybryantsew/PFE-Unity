using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using PFE.Systems.Map.DataMigration;
using PFE.Data.Definitions;

namespace PFE.Systems.Map.DataMigration
{
    /// <summary>
    /// Converts AS3 room data to Unity RoomTemplate ScriptableObjects.
    /// Handles tile encoding, object placement, and asset creation.
    /// 
    /// IMPORTANT: Tile data is stored as dot-separated multi-character codes.
    /// Each tile can be "C", "_E", "CА", "_Б-", etc. The dots are the delimiter.
    /// This is decoded at runtime by TileDecoder using the TileFormDatabase.
    /// </summary>
    public class AS3ToUnityConverter
    {
        private AS3ObjectMapping objectMapping;
        private readonly AS3LandDefaultsDatabase landDefaults;
        private readonly MapObjectCatalog objectCatalog;

        // Characters that indicate open/passable space for door detection
        private static readonly HashSet<string> DoorOpenCodes = new HashSet<string>
        {
            "", "_", ".", "_E", "_K"
        };

        public AS3ToUnityConverter(AS3ObjectMapping mapping, AS3LandDefaultsDatabase landDefaults = null, MapObjectCatalog objectCatalog = null)
        {
            this.objectMapping = mapping;
            this.landDefaults = landDefaults;
            this.objectCatalog = objectCatalog;
            if (objectMapping != null)
            {
                objectMapping.InitializeCache();
            }

            if (this.objectCatalog != null)
            {
                this.objectCatalog.RebuildIndex();
            }
        }

        /// <summary>
        /// Convert AS3 room to RoomTemplate.
        /// </summary>
        public RoomTemplate ConvertRoom(AS3RoomData as3Room)
        {
            RoomTemplate template = ScriptableObject.CreateInstance<RoomTemplate>();

            // Basic info
            template.id = as3Room.name;
            template.name = as3Room.name;
            template.type = GetRoomType(as3Room);
            template.fixedPosition = new Vector3Int(as3Room.x, as3Room.y, 0);
            template.backgroundRoomId = ResolveBackgroundRoomId(as3Room);
            template.backgroundDecorations = ParseBackgroundDecorations(as3Room);

            // Store raw dot-separated tile rows (NOT single-char flattened)
            template.tileDataString = ConvertTileRows(as3Room);

            // Parse objects
            template.objects = ParseObjects(as3Room);

            // Parse spawn points
            template.spawnPoints = FindSpawnPoints(as3Room);

            // Parse door configuration from original XML door data
            template.doorQuality = ParseDoorConfiguration(as3Room);

            // Environment from options
            template.environment = ParseEnvironment(as3Room);

            // Difficulty
            if (int.TryParse(as3Room.GetOption("level", "0"), out int parsedLevel))
            {
                template.difficultyLevel = Mathf.Clamp(parsedLevel, 0, 20);
            }
            else
            {
                template.difficultyLevel = Mathf.Clamp(
                    Mathf.FloorToInt(Mathf.Sqrt(as3Room.x * as3Room.x + as3Room.y * as3Room.y) / 2f), 0, 20);
            }

            // Generation
            template.allowRandom = true;
            template.maxInstances = 2;

            return template;
        }

        /// <summary>
        /// Convert multiple rooms at once.
        /// </summary>
        public List<RoomTemplate> ConvertRooms(List<AS3RoomData> as3Rooms)
        {
            List<RoomTemplate> templates = new List<RoomTemplate>();

            foreach (var as3Room in as3Rooms)
            {
                try
                {
                    templates.Add(ConvertRoom(as3Room));
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Error converting room {as3Room.name}: {ex.Message}");
                }
            }

            return templates;
        }

        // =====================================================
        //  TILE DATA — preserve dot-separated format verbatim
        // =====================================================

        /// <summary>
        /// Store tile rows preserving the original dot-separated multi-character format.
        /// Each row is stored as-is. Rows are joined by newlines.
        /// At runtime, TileDecoder.ParseRoom() splits by dots and decodes each token.
        /// </summary>
        private string ConvertTileRows(AS3RoomData as3Room)
        {
            List<string> rows = new List<string>(WorldConstants.ROOM_HEIGHT);

            for (int y = 0; y < WorldConstants.ROOM_HEIGHT; y++)
            {
                string sourceRow = y < as3Room.tileLayers.Count
                    ? as3Room.tileLayers[y]?.TrimEnd('\r', '\n') ?? ""
                    : "";

                if (string.IsNullOrEmpty(sourceRow))
                {
                    // Empty row: generate 48 air tiles
                    rows.Add(string.Join(".", Enumerable.Repeat("_", WorldConstants.ROOM_WIDTH)));
                    continue;
                }

                // Validate tile count by splitting on dots
                // Use RemoveEmptyEntries to handle trailing dots (e.g., "C._E._E.")
                string[] tokens = sourceRow.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length != WorldConstants.ROOM_WIDTH)
                {
                    Debug.LogWarning(
                        $"[AS3ToUnityConverter] Row {y} has {tokens.Length} tiles, expected {WorldConstants.ROOM_WIDTH}. " +
                        $"Adjusting.");

                    if (tokens.Length < WorldConstants.ROOM_WIDTH)
                    {
                        // Pad with air tiles
                        var padded = new string[WorldConstants.ROOM_WIDTH];
                        Array.Copy(tokens, padded, tokens.Length);
                        for (int i = tokens.Length; i < WorldConstants.ROOM_WIDTH; i++)
                            padded[i] = "_";
                        sourceRow = string.Join(".", padded);
                    }
                    else
                    {
                        // Truncate extra tiles
                        sourceRow = string.Join(".", tokens.Take(WorldConstants.ROOM_WIDTH));
                    }
                }

                rows.Add(sourceRow);
            }

            return string.Join("\n", rows);
        }

        // =====================================================
        //  ROOM TYPE
        // =====================================================

        private string GetRoomType(AS3RoomData room)
        {
            string explicitType = room.GetOption("tip", "");
            if (!string.IsNullOrEmpty(explicitType))
                return explicitType;

            string name = room.name ?? "";
            if (name.Contains("beg", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("start", StringComparison.OrdinalIgnoreCase))
                return "beg0";
            if (name.Contains("roof", StringComparison.OrdinalIgnoreCase)) return "roof";
            if (name.Contains("vert", StringComparison.OrdinalIgnoreCase)) return "vert";
            if (name.Contains("surf", StringComparison.OrdinalIgnoreCase)) return "surf";
            if (name.Contains("end", StringComparison.OrdinalIgnoreCase)) return "end";

            return "pass";
        }

        // =====================================================
        //  OBJECTS
        // =====================================================

        private List<ObjectSpawnData> ParseObjects(AS3RoomData as3Room)
        {
            List<ObjectSpawnData> placements = new List<ObjectSpawnData>();

            foreach (var as3Obj in as3Room.objects)
            {
                MapObjectDefinition definition = ResolveDefinition(as3Obj.id);
                ObjectSpawnData placement = new ObjectSpawnData
                {
                    id = as3Obj.id,
                    definitionId = as3Obj.id,
                    definition = definition,
                    type = GetObjectTypeFromId(as3Obj.id, as3Obj.attributes, definition),
                    tileCoord = new Vector2Int(as3Obj.x, as3Obj.y),
                    code = as3Obj.code,
                    uid = as3Obj.GetAttribute("uid", string.Empty),
                    attributes = MapObjectDataUtility.BuildAttributes(
                        as3Obj.attributes,
                        "x", "y", "id", "code", "uid"),
                    items = ConvertItems(as3Obj.items),
                    scripts = ConvertScripts(as3Obj.scripts)
                };

                placement.ApplyResolvedDefinition(definition);
                placement.RefreshLegacyParameters();
                placements.Add(placement);
            }

            return placements;
        }

        private MapObjectDefinition ResolveDefinition(string objectId)
        {
            if (objectCatalog == null || string.IsNullOrWhiteSpace(objectId))
            {
                return null;
            }

            return objectCatalog.GetDefinition(objectId);
        }

        private string GetObjectTypeFromId(
            string objectId,
            IReadOnlyDictionary<string, string> attributes,
            MapObjectDefinition definition)
        {
            if (string.IsNullOrEmpty(objectId)) return "obj";
            if (objectId.Equals("player", StringComparison.OrdinalIgnoreCase)) return "player";

            if (TryGetMappedObjectType(objectId, out string mappedType))
            {
                return mappedType;
            }

            if (definition != null)
            {
                return definition.GetResolvedPlacementType();
            }

            if (objectId == "tarakan" || objectId == "enemy") return "unit";
            MapObjectDefinitionClassifier.ResolveFamily(objectId, attributes, out string placementType, out _);
            return placementType;
        }

        private bool TryGetMappedObjectType(string objectId, out string mappedType)
        {
            mappedType = string.Empty;
            if (objectMapping == null || string.IsNullOrWhiteSpace(objectId))
            {
                return false;
            }

            if (!objectMapping.IsKnown(objectId))
            {
                return false;
            }

            AS3ObjectMapping.ObjectMapping mapping = objectMapping.GetMapping(objectId);
            if (mapping == null)
            {
                return false;
            }

            mappedType = mapping.category switch
            {
                ObjectCategory.Player => "player",
                ObjectCategory.Enemy => "unit",
                ObjectCategory.NPC => "unit",
                ObjectCategory.Container => "box",
                ObjectCategory.Door => "door",
                ObjectCategory.Trigger => "area",
                _ => string.Empty
            };

            return !string.IsNullOrEmpty(mappedType);
        }

        private static List<MapObjectItemData> ConvertItems(List<AS3Item> sourceItems)
        {
            List<MapObjectItemData> items = new List<MapObjectItemData>();
            if (sourceItems == null)
            {
                return items;
            }

            for (int i = 0; i < sourceItems.Count; i++)
            {
                AS3Item item = sourceItems[i];
                if (item == null)
                {
                    continue;
                }

                items.Add(new MapObjectItemData
                {
                    id = item.id,
                    attributes = MapObjectDataUtility.BuildAttributes(item.attributes)
                });
            }

            return items;
        }

        private static List<MapObjectScriptData> ConvertScripts(List<AS3Script> sourceScripts)
        {
            List<MapObjectScriptData> scripts = new List<MapObjectScriptData>();
            if (sourceScripts == null)
            {
                return scripts;
            }

            for (int i = 0; i < sourceScripts.Count; i++)
            {
                AS3Script sourceScript = sourceScripts[i];
                if (sourceScript == null)
                {
                    continue;
                }

                MapObjectScriptData script = new MapObjectScriptData
                {
                    eventName = sourceScript.eventName
                };

                if (sourceScript.actions != null)
                {
                    for (int actionIndex = 0; actionIndex < sourceScript.actions.Count; actionIndex++)
                    {
                        AS3ScriptAction sourceAction = sourceScript.actions[actionIndex];
                        if (sourceAction == null)
                        {
                            continue;
                        }

                        script.actions.Add(new MapObjectScriptActionData
                        {
                            act = sourceAction.act,
                            targ = sourceAction.targ,
                            val = sourceAction.val
                        });
                    }
                }

                scripts.Add(script);
            }

            return scripts;
        }

        // =====================================================
        //  SPAWN POINTS
        // =====================================================

        private List<SpawnPointData> FindSpawnPoints(AS3RoomData as3Room)
        {
            List<SpawnPointData> spawnPoints = new List<SpawnPointData>();

            AS3Object playerObj = as3Room.objects.FirstOrDefault(o => o.id == "player");
            if (playerObj != null)
            {
                spawnPoints.Add(new SpawnPointData
                {
                    tileCoord = new Vector2Int(playerObj.x, playerObj.y),
                    type = SpawnType.Player
                });
            }
            else
            {
                spawnPoints.Add(new SpawnPointData
                {
                    tileCoord = new Vector2Int(2, 15),
                    type = SpawnType.Player
                });
            }

            return spawnPoints;
        }

        private List<BackgroundDecorationData> ParseBackgroundDecorations(AS3RoomData as3Room)
        {
            List<BackgroundDecorationData> decorations = new List<BackgroundDecorationData>();

            foreach (var background in as3Room.backgrounds)
            {
                decorations.Add(new BackgroundDecorationData
                {
                    id = background.id,
                    tileCoord = new Vector2Int(background.x, background.y)
                });
            }

            return decorations;
        }

        private string ResolveBackgroundRoomId(AS3RoomData as3Room)
        {
            if (as3Room == null)
            {
                return string.Empty;
            }

            // Background-room linkage is separate from <back> decoration placement.
            // Only use an explicit option if the source data provides one.
            return as3Room.GetOption("back", "").Trim();
        }

        // =====================================================
        //  DOORS — using tile codes, not single chars
        // =====================================================

        /// <summary>
        /// Parse door configuration. Uses GetTileCode() for dot-separated format.
        /// </summary>
        private int[] ParseDoorConfiguration(AS3RoomData as3Room)
        {
            int[] doorQuality = new int[24];

            // Right side: doors 0-5
            for (int i = 0; i < 6; i++)
            {
                int y = 4 + i * 3;
                if (y < WorldConstants.ROOM_HEIGHT)
                {
                    string edge = as3Room.GetTileCode(WorldConstants.ROOM_WIDTH - 1, y);
                    string inner = as3Room.GetTileCode(WorldConstants.ROOM_WIDTH - 2, y);
                    if (IsDoorCandidate(edge) || IsDoorCandidate(inner))
                        doorQuality[i] = (int)DoorQuality.Narrow;
                }
            }

            // Bottom side: doors 6-11
            for (int i = 0; i < 6; i++)
            {
                int x = 4 + i * 7;
                if (x < WorldConstants.ROOM_WIDTH)
                {
                    string edge = as3Room.GetTileCode(x, WorldConstants.ROOM_HEIGHT - 1);
                    string inner = as3Room.GetTileCode(x, WorldConstants.ROOM_HEIGHT - 2);
                    if (IsDoorCandidate(edge) || IsDoorCandidate(inner))
                        doorQuality[6 + i] = (int)DoorQuality.Narrow;
                }
            }

            // Left side: doors 12-17
            for (int i = 0; i < 6; i++)
            {
                int y = 4 + i * 3;
                if (y < WorldConstants.ROOM_HEIGHT)
                {
                    string edge = as3Room.GetTileCode(0, y);
                    string inner = as3Room.GetTileCode(1, y);
                    if (IsDoorCandidate(edge) || IsDoorCandidate(inner))
                        doorQuality[12 + i] = (int)DoorQuality.Narrow;
                }
            }

            // Top side: doors 18-23
            for (int i = 0; i < 6; i++)
            {
                int x = 4 + i * 7;
                if (x < WorldConstants.ROOM_WIDTH)
                {
                    string edge = as3Room.GetTileCode(x, 0);
                    string inner = as3Room.GetTileCode(x, 1);
                    if (IsDoorCandidate(edge) || IsDoorCandidate(inner))
                        doorQuality[18 + i] = (int)DoorQuality.Narrow;
                }
            }

            return doorQuality;
        }

        private static bool IsDoorCandidate(string tileCode)
        {
            if (string.IsNullOrEmpty(tileCode)) return true;

            string trimmed = tileCode.Trim();
            if (trimmed == "" || trimmed == "_") return true;

            // The first character determines the primary form type.
            // fForms are A-T (Latin uppercase) — all are walls (phis=1).
            // If the first char is a letter (Latin or Cyrillic), the tile has structure.
            // Only pure air/empty codes are door candidates.
            char first = trimmed[0];

            // Any letter as first char means a form applies — likely solid
            // (fForms A-T are walls; Cyrillic chars in first position also indicate structure)
            if (char.IsLetter(first))
                return false;

            // Underscore-prefixed codes are overlays on air (_E, _K, etc.) — passable
            if (first == '_')
                return true;

            return DoorOpenCodes.Contains(trimmed);
        }

        // =====================================================
        //  ENVIRONMENT
        // =====================================================

        private RoomEnvironmentData ParseEnvironment(AS3RoomData as3Room)
        {
            var env = new RoomEnvironmentData();

            IReadOnlyDictionary<string, string> inheritedOptions = GetInheritedLandOptions(as3Room);

            env.musicTrack = ResolveOption(as3Room, inheritedOptions, "music", "");
            env.colorScheme = ResolveOption(as3Room, inheritedOptions, "color", "");
            env.backgroundColorScheme = ResolveOption(as3Room, inheritedOptions, "colorfon", "");
            env.backgroundWall = ResolveOption(as3Room, inheritedOptions, "backwall", "");
            if (int.TryParse(ResolveOption(as3Room, inheritedOptions, "backform", "0"), out int backform))
                env.backgroundForm = backform;
            env.transparentBackground = ResolveFlag(as3Room, inheritedOptions, "transpfon");
            env.hasSky = ResolveFlag(as3Room, inheritedOptions, "sky");
            if (int.TryParse(ResolveOption(as3Room, inheritedOptions, "ramka", "1"), out int borderType))
                env.borderType = borderType;
            env.noBlackReveal = ResolveFlag(as3Room, inheritedOptions, "noblack");
            if (float.TryParse(ResolveOption(as3Room, inheritedOptions, "vis", "1"), out float vis))
                env.visibilityMultiplier = vis;
            if (int.TryParse(ResolveOption(as3Room, inheritedOptions, "lon", "0"), out int lon))
                env.lightsOn = lon;
            env.returnsToDarkness = ResolveFlag(as3Room, inheritedOptions, "retdark");
            if (int.TryParse(ResolveOption(as3Room, inheritedOptions, "wlevel", "100"), out int wl))
                env.waterLevel = wl;
            if (int.TryParse(ResolveOption(as3Room, inheritedOptions, "wtip", "0"), out int wt))
                env.waterType = wt;
            if (float.TryParse(ResolveOption(as3Room, inheritedOptions, "wopac", "0"), out float wo))
                env.waterOpacity = wo;
            if (float.TryParse(ResolveOption(as3Room, inheritedOptions, "wdam", "0"), out float wd))
                env.waterDamage = wd;
            if (int.TryParse(ResolveOption(as3Room, inheritedOptions, "wtipdam", "7"), out int wtd))
                env.waterDamageType = wtd;
            if (float.TryParse(ResolveOption(as3Room, inheritedOptions, "rad", "0"), out float rad))
                env.radiation = rad;
            if (int.TryParse(ResolveOption(as3Room, inheritedOptions, "dark", "0"), out int dark))
                env.darkness = dark;

            return env;
        }

        private IReadOnlyDictionary<string, string> GetInheritedLandOptions(AS3RoomData as3Room)
        {
            if (as3Room == null || landDefaults == null || string.IsNullOrWhiteSpace(as3Room.sourceCollectionId))
            {
                return null;
            }

            landDefaults.TryGetOptions(as3Room.sourceCollectionId, out IReadOnlyDictionary<string, string> options);
            return options;
        }

        private static string ResolveOption(AS3RoomData room, IReadOnlyDictionary<string, string> inheritedOptions, string key, string fallback)
        {
            if (room != null && room.options.TryGetValue(key, out string roomValue))
            {
                return roomValue;
            }

            if (inheritedOptions != null && inheritedOptions.TryGetValue(key, out string inheritedValue))
            {
                return inheritedValue;
            }

            return fallback;
        }

        private static bool ResolveFlag(AS3RoomData room, IReadOnlyDictionary<string, string> inheritedOptions, string key)
        {
            if (room != null && room.options.ContainsKey(key))
            {
                return true;
            }

            return inheritedOptions != null && inheritedOptions.ContainsKey(key);
        }

        // =====================================================
        //  VALIDATION (post-conversion sanity check)
        // =====================================================

        /// <summary>
        /// Validate a converted RoomTemplate. Returns error message or null if valid.
        /// </summary>
        public static string ValidateTemplate(RoomTemplate template)
        {
            if (template == null) return "Template is null";
            if (string.IsNullOrEmpty(template.id)) return "Empty ID";
            if (string.IsNullOrEmpty(template.tileDataString)) return "Empty tile data";

            string[] rows = template.tileDataString.Split('\n');
            if (rows.Length != WorldConstants.ROOM_HEIGHT)
                return $"Expected {WorldConstants.ROOM_HEIGHT} rows, got {rows.Length}";

            for (int y = 0; y < rows.Length; y++)
            {
                string[] tokens = rows[y].Split('.');
                if (tokens.Length != WorldConstants.ROOM_WIDTH)
                    return $"Row {y}: expected {WorldConstants.ROOM_WIDTH} tiles, got {tokens.Length}";
            }

            if (template.doorQuality == null || template.doorQuality.Length != 24)
                return $"Door quality array: expected 24 entries, got {template.doorQuality?.Length ?? 0}";

            return null; // Valid
        }

        /// <summary>
        /// Save template to asset database.
        /// </summary>
        public void SaveTemplate(RoomTemplate template, string path)
        {
#if UNITY_EDITOR
            string assetPath = $"{path}/{template.id}.asset";
            UnityEditor.AssetDatabase.CreateAsset(template, assetPath);
            UnityEditor.AssetDatabase.SaveAssets();
#endif
        }
    }
}
