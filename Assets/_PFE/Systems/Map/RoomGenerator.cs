using System;
using System.Collections.Generic;
using UnityEngine;
using VContainer;
namespace PFE.Systems.Map
{
    /// <summary>
    /// Room generator - procedural room generation.
    /// From AS3: Land.buildRandomLand(), Land.newRandomLoc()
    /// </summary>
    public class RoomGenerator
    {
        private List<RoomTemplate> allTemplates;
        private Dictionary<string, int> templateUsageCount = new Dictionary<string, int>();
        private bool prototypeMode;
        private bool excludeSpecialTypesInRandomSelection;
        private readonly TileFormDatabase _formDatabase;
        // Special-purpose room types that should not appear in generic random fill.
        private static readonly HashSet<string> SpecialRoomTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "beg0", "beg1", "roof", "vert", "surf", "back", "end", "end1"
        };

        [Inject]  // Add this constructor
        public RoomGenerator(TileFormDatabase formDb = null)
        {
            _formDatabase = formDb;
        }
        /// <summary>
        /// Initialize with all templates.
        /// </summary>
        public void Initialize(List<RoomTemplate> templates)
        {
            allTemplates = templates;
            // Initialize usage counts
            templateUsageCount.Clear();
            foreach (var template in templates)
            {
                if (!string.IsNullOrEmpty(template.id))
                {
                    templateUsageCount[template.id] = 0;
                }
            }
        }

        /// <summary>
        /// Configure generation behavior for migration/prototype runs.
        /// </summary>
        public void ConfigureGenerationMode(bool usePrototypeMode, bool excludeSpecialTypesInRandom = false)
        {
            prototypeMode = usePrototypeMode;
            excludeSpecialTypesInRandomSelection = excludeSpecialTypesInRandom;
        }

        public bool IsPrototypeMode => prototypeMode;

        /// <summary>
        /// Generate room from template.
        /// </summary>
        public RoomInstance GenerateRoom(RoomTemplate template, Vector3Int position)
        {
            if (template == null)
            {
                Debug.LogError("Attempted to generate room from null template");
                return null;
            }

            RoomInstance room = new RoomInstance
            {
                id = $"{template.id}_{position.x}_{position.y}_{position.z}",
                templateId = template.id,
                landPosition = position,
                width = WorldConstants.ROOM_WIDTH,
                height = WorldConstants.ROOM_HEIGHT,
                roomType = template.type
            };

            // Initialize and parse tiles
            room.InitializeTiles();
            TileData[,] templateTiles = template.ParseTiles(_formDatabase);
            for (int x = 0; x < room.width; x++)
            {
                for (int y = 0; y < room.height; y++)
                {
                    if (templateTiles[x, y] != null)
                    {
                        room.tiles[x, y] = templateTiles[x, y];
                    }
                }
            }

            // Copy spawn points
            foreach (var spawnData in template.spawnPoints)
            {
                room.spawnPoints.Add(new SpawnPoint
                {
                    tileCoord = spawnData.tileCoord,
                    type = spawnData.type,
                    unitId = spawnData.unitId,
                    facingDirection = spawnData.facingDirection
                });
            }

            foreach (var backgroundDecoration in template.backgroundDecorations)
            {
                room.backgroundDecorations.Add(new BackgroundDecorationInstance
                {
                    decorationId = backgroundDecoration.id,
                    tileCoord = backgroundDecoration.tileCoord
                });
            }

            // Copy door configuration
            room.doors = CreateDoors(template, room, position);

            // Set difficulty
            room.difficulty = new RoomDifficulty
            {
                baseDifficulty = template.difficultyLevel,
                enemyLevel = template.difficultyLevel
            };

            // Set environment
            room.environment = new RoomEnvironment
            {
                musicTrack = template.environment.musicTrack,
                colorScheme = template.environment.colorScheme,
                backgroundColorScheme = template.environment.backgroundColorScheme,
                backgroundWall = template.environment.backgroundWall,
                backgroundForm = template.environment.backgroundForm,
                transparentBackground = template.environment.transparentBackground,
                hasSky = template.environment.hasSky,
                borderType = template.environment.borderType,
                noBlackReveal = template.environment.noBlackReveal,
                visibilityMultiplier = template.environment.visibilityMultiplier,
                lightsOn = template.environment.lightsOn,
                returnsToDarkness = template.environment.returnsToDarkness,
                waterLevel = template.environment.waterLevel,
                waterType = template.environment.waterType,
                waterOpacity = template.environment.waterOpacity,
                waterDamage = template.environment.waterDamage,
                waterDamageType = template.environment.waterDamageType,
                radiation = template.environment.radiation,
                radiationDamage = template.environment.radiationDamage,
                darkness = template.environment.darkness
            };

            return room;
        }

        /// <summary>
        /// Create doors from template.
        /// </summary>
        private List<DoorInstance> CreateDoors(RoomTemplate template, RoomInstance room, Vector3Int position)
        {
            List<DoorInstance> doors = new List<DoorInstance>();

            for (int i = 0; i < template.doorQuality.Length && i < WorldConstants.DOORS_PER_ROOM; i++)
            {
                if (template.doorQuality[i] >= (int)DoorQuality.Narrow)
                {
                    DoorInstance door = new DoorInstance
                    {
                        doorIndex = i,
                        side = GetDoorSide(i),
                        quality = (DoorQuality)template.doorQuality[i],
                        isActive = true,
                        tilePosition = GetDoorTilePosition(i)
                    };

                    doors.Add(door);
                }
            }

            return doors;
        }

        /// <summary>
        /// Get door side from index.
        /// </summary>
        private DoorSide GetDoorSide(int doorIndex)
        {
            if (doorIndex >= 0 && doorIndex < 6) return DoorSide.Right;
            if (doorIndex >= 6 && doorIndex < 12) return DoorSide.Bottom;
            if (doorIndex >= 12 && doorIndex < 18) return DoorSide.Left;
            return DoorSide.Top; // 18-23
        }

        /// <summary>
        /// Get door tile position from index.
        /// </summary>
        private Vector2Int GetDoorTilePosition(int doorIndex)
        {
            int width = WorldConstants.ROOM_WIDTH;   // 48
            int height = WorldConstants.ROOM_HEIGHT; // 27

            if (doorIndex >= 0 && doorIndex <= 5)
            {
                // RIGHT side: AS3 formula = index * 4 + 3
                int y = doorIndex * 4 + 3;
                return new Vector2Int(width - 1, y);
            }
            else if (doorIndex >= 6 && doorIndex <= 10)
            {
                // BOTTOM side: AS3 formula = (index - 6) * 9 + 4
                int x = (doorIndex - 6) * 9 + 4;
                return new Vector2Int(x, height - 1);
            }
            else if (doorIndex >= 11 && doorIndex <= 16)
            {
                // LEFT side: AS3 formula = (index - 11) * 4 + 3
                int y = (doorIndex - 11) * 4 + 3;
                return new Vector2Int(0, y);
            }
            else if (doorIndex >= 17 && doorIndex <= 21)
            {
                // TOP side: AS3 formula = (index - 17) * 9 + 4
                int x = (doorIndex - 17) * 9 + 4;
                return new Vector2Int(x, 0);
            }

            return Vector2Int.zero;
        }

        /// <summary>
        /// Select random room template (weighted).
        /// From AS3: Land.newRandomLoc() - weighted selection with kol * kol
        /// </summary>
        public RoomTemplate SelectRandomRoom(int maxDifficulty, string requiredType = null, List<RoomTemplate> exclude = null)
        {
            return SelectRandomRoomInternal(maxDifficulty, requiredType, exclude, allowExcludeFallbackRetry: true);
        }

        private RoomTemplate SelectRandomRoomInternal(int maxDifficulty, string requiredType, List<RoomTemplate> exclude, bool allowExcludeFallbackRetry)
        {
            if (allTemplates == null || allTemplates.Count == 0)
            {
                Debug.LogWarning($"No valid room templates found (maxDifficulty: {maxDifficulty}, type: {requiredType})");
                return null;
            }

            List<RoomTemplate> candidates = new List<RoomTemplate>();
            List<int> weights = new List<int>();

            foreach (var template in allTemplates)
            {
                // Skip templates without ID
                if (string.IsNullOrEmpty(template.id))
                    continue;

                // Check exclusion list
                if (exclude != null && exclude.Contains(template)) continue;

                // Type and random rules
                if (!string.IsNullOrEmpty(requiredType))
                {
                    if (!string.Equals(template.type, requiredType, StringComparison.Ordinal))
                        continue;
                }
                else
                {
                    if (!template.allowRandom)
                        continue;

                    if (excludeSpecialTypesInRandomSelection && IsSpecialType(template.type))
                        continue;
                }

                // Check constraints
                if (template.difficultyLevel > maxDifficulty) continue;

                // Check usage count unless prototype mode ignores instance limits
                int currentUsage = templateUsageCount.ContainsKey(template.id) ? templateUsageCount[template.id] : 0;
                if (!prototypeMode && currentUsage >= template.maxInstances) continue;

                // Calculate weight (quadratic - kol * kol in AS3)
                int remaining = prototypeMode ? Mathf.Max(1, template.maxInstances) : (template.maxInstances - currentUsage);
                int weight = remaining * remaining;

                candidates.Add(template);
                weights.Add(weight);
            }

            if (candidates.Count == 0)
            {
                // Prototype mode fallback: if exclusion eliminated all candidates, retry without exclusion once.
                if (prototypeMode && allowExcludeFallbackRetry && exclude != null && exclude.Count > 0)
                {
                    Debug.LogWarning(
                        $"No valid room templates found with exclusion list (maxDifficulty: {maxDifficulty}, type: {requiredType}). " +
                        "Prototype mode retrying without exclusion.");
                    return SelectRandomRoomInternal(maxDifficulty, requiredType, null, allowExcludeFallbackRetry: false);
                }

                Debug.LogWarning($"No valid room templates found (maxDifficulty: {maxDifficulty}, type: {requiredType})");
                return null;
            }

            // Weighted random selection
            int totalWeight = 0;
            foreach (int w in weights) totalWeight += w;

            int random = UnityEngine.Random.Range(0, totalWeight);
            int cumulative = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                cumulative += weights[i];
                if (random < cumulative)
                {
                    // Increment usage count
                    string templateId = candidates[i].id;
                    if (!templateUsageCount.ContainsKey(templateId))
                        templateUsageCount[templateId] = 0;
                    templateUsageCount[templateId]++;

                    return candidates[i];
                }
            }

            // Fallback (shouldn't reach here)
            return candidates[candidates.Count - 1];
        }

        /// <summary>
        /// Select room by type.
        /// From AS3: Land.newTipLoc()
        /// </summary>
        public RoomTemplate SelectRoomByType(string roomType, List<RoomTemplate> exclude = null)
        {
            if (allTemplates == null || allTemplates.Count == 0)
            {
                Debug.LogWarning($"No valid rooms of type '{roomType}' found (after exclusion and usage check)");
                return null;
            }

            List<RoomTemplate> candidates = new List<RoomTemplate>();

            foreach (var template in allTemplates)
            {
                if (template.type == roomType)
                {
                    // Check exclusion list
                    if (exclude != null && exclude.Contains(template))
                        continue;

                    // Check usage count
                    int currentUsage = templateUsageCount.ContainsKey(template.id) ? templateUsageCount[template.id] : 0;
                    if (!prototypeMode && currentUsage >= template.maxInstances)
                        continue;

                    candidates.Add(template);
                }
            }

            if (candidates.Count == 0)
            {
                Debug.LogWarning($"No valid rooms of type '{roomType}' found (after exclusion and usage check)");
                return null;
            }

            // Select random candidate
            RoomTemplate selected = candidates[UnityEngine.Random.Range(0, candidates.Count)];

            // Increment usage count
            if (!string.IsNullOrEmpty(selected.id))
            {
                if (!templateUsageCount.ContainsKey(selected.id))
                    templateUsageCount[selected.id] = 0;
                templateUsageCount[selected.id]++;
            }

            return selected;
        }

        private static bool IsSpecialType(string type)
        {
            if (string.IsNullOrEmpty(type))
            {
                return false;
            }

            return SpecialRoomTypes.Contains(type);
        }

        /// <summary>
        /// Reset usage counts.
        /// </summary>
        public void ResetUsageCounts()
        {
            // Collect keys first to avoid modifying during iteration
            var keys = new List<string>(templateUsageCount.Keys);
            foreach (var key in keys)
            {
                templateUsageCount[key] = 0;
            }
        }

        /// <summary>
        /// Get current usage count for a template.
        /// </summary>
        public int GetUsageCount(string templateId)
        {
            if (templateUsageCount.ContainsKey(templateId))
            {
                return templateUsageCount[templateId];
            }
            return 0;
        }
    }
}
