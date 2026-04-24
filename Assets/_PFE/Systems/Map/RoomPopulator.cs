using UnityEngine;
using System.Collections.Generic;
using System.Globalization;

namespace PFE.Systems.Map
{
    /// <summary>
    /// Populates rooms with objects, enemies, and interactive elements.
    /// Port of AS3 Location.setObjects(), createUnit(), createObj(), setRandomUnits().
    ///
    /// This bridges the gap between your RoomTemplate data (which has ObjectSpawnData
    /// and SpawnPointData) and the runtime RoomInstance (which has empty lists).
    ///
    /// Call flow (mirrors AS3):
    ///   1. RoomGenerator.GenerateRoom() - creates tiles, copies spawn data
    ///   2. DoorCarver.CarveAllDoors() - opens door passages
    ///   3. DoorCarver.ApplyBorder() - adds ramka border walls
    ///   4. RoomPopulator.PopulateRoom() - spawns all entities <-- THIS CLASS
    /// </summary>
    public class RoomPopulator
    {
        /// <summary>
        /// Populate a room with all entities from its template data.
        /// Mirrors AS3 Location.setObjects().
        /// </summary>
        public static void PopulateRoom(RoomInstance room, RoomTemplate template, RoomDifficulty difficulty)
        {
            if (room == null || template == null) return;

            // Phase 1: Process template objects (from XML obj elements)
            foreach (var objData in template.objects)
            {
                ProcessObjectSpawn(room, objData, difficulty);
            }

            // Phase 2: Place random enemies at spawn points
            PlaceRandomEnemies(room, template, difficulty);

            // Phase 3: Place XP bonuses
            // AS3: createXpBonuses() places collectible XP orbs
            PlaceXpBonuses(room, 5);
        }

        /// <summary>
        /// Process a single object spawn from template data.
        /// Mirrors AS3 Location.setObjects() inner loop + createObj() + createUnit().
        /// </summary>
        private static void ProcessObjectSpawn(RoomInstance room, ObjectSpawnData spawnData, RoomDifficulty difficulty)
        {
            if (spawnData == null) return;

            spawnData.EnsureStructuredData();

            Vector2Int placementTileCoord = ResolveLegacyPlacementTileCoord(room, spawnData.tileCoord);
            int nx = placementTileCoord.x;
            int ny = placementTileCoord.y;

            // Check if placement is valid (AS3: noHolesPlace check)
            var tile = room.GetTileAtCoord(new Vector2Int(nx, ny));
            if (tile != null && !tile.canPlaceObjects)
                return;

            // Imported room objects currently preserve Flash's top-down tile coordinates.
            // Convert them into the room's bottom-up pixel space here so existing imports stay valid.
            Vector2 bottomAnchorPixels = ResolveLegacyBottomAnchorPixels(room, spawnData);
            float pixelX = bottomAnchorPixels.x;
            float pixelY = bottomAnchorPixels.y;
            //float liftedObjectPixelY = pixelY + WorldConstants.TILE_SIZE; //prob need to make adjustable in map editor or offset

            switch (spawnData.type)
            {
                case "unit":
                    CreateUnit(room, spawnData.id, pixelX, pixelY, difficulty);
                    break;

                case "box":
                case "door":
                    CreateObject(room, spawnData, pixelX, pixelY);
                    break;

                case "checkpoint":
                    CreateCheckpoint(room, spawnData, pixelX, pixelY);
                    break;

                case "area":
                    CreateArea(room, spawnData, placementTileCoord);
                    break;

                case "bonus":
                    CreateBonus(room, spawnData, pixelX, pixelY);
                    break;

                case "trap":
                    CreateTrap(room, spawnData, pixelX, pixelY);
                    break;

                default:
                    // Generic object
                    CreateObject(room, spawnData, pixelX, pixelY);
                    break;
            }
        }

        /// <summary>
        /// Create a unit (enemy, NPC, etc.) in the room.
        /// Simplified port of AS3 Location.createUnit().
        /// </summary>
        private static void CreateUnit(RoomInstance room, string unitId, float x, float y, RoomDifficulty difficulty)
        {
            var unit = new UnitInstance
            {
                unitId = unitId,
                unitType = unitId,
                position = new Vector2(x, y),
                isDead = false,
                maxHealth = CalculateUnitHealth(unitId, Mathf.RoundToInt(difficulty.enemyLevel)),
                currentHealth = CalculateUnitHealth(unitId, Mathf.RoundToInt(difficulty.enemyLevel))
            };

            room.units.Add(unit);
        }

        /// <summary>
        /// Create a box/door/interactive object.
        /// Simplified port of AS3 Location.createObj() for "box" and "door" types.
        /// </summary>
        private static void CreateObject(RoomInstance room, ObjectSpawnData spawnData, float x, float y)
        {
            spawnData?.EnsureStructuredData();

            var obj = new ObjectInstance
            {
                objectId = spawnData?.id ?? string.Empty,
                objectType = spawnData?.type ?? "obj",
                definitionId = spawnData?.GetResolvedDefinitionId() ?? string.Empty,
                definition = spawnData?.definition,
                code = spawnData?.code ?? string.Empty,
                uid = spawnData?.uid ?? string.Empty,
                attributes = MapObjectDataUtility.CloneAttributes(spawnData?.attributes),
                items = MapObjectDataUtility.CloneItems(spawnData?.items),
                scripts = MapObjectDataUtility.CloneScripts(spawnData?.scripts),
                parameters = spawnData?.parameters ?? string.Empty,
                position = new Vector2(x, y),
                isActive = true,
                runtimeState = new MapObjectRuntimeStateData()
            };

            obj.runtimeState.InitializeFromAttributes(obj.attributes);
            obj.InitializeDynamicRuntimeState();
            obj.RefreshLegacyParameters();
            room.AddObject(obj);
        }

        /// <summary>
        /// Create a checkpoint/save point.
        /// From AS3: Location.createCheck() + CheckPoint class.
        /// </summary>
        private static void CreateCheckpoint(RoomInstance room, ObjectSpawnData spawnData, float x, float y)
        {
            spawnData?.EnsureStructuredData();

            var obj = new ObjectInstance
            {
                objectId = spawnData?.id ?? string.Empty,
                objectType = "checkpoint",
                definitionId = spawnData?.GetResolvedDefinitionId() ?? string.Empty,
                definition = spawnData?.definition,
                code = spawnData?.code ?? string.Empty,
                uid = spawnData?.uid ?? string.Empty,
                attributes = MapObjectDataUtility.CloneAttributes(spawnData?.attributes),
                items = MapObjectDataUtility.CloneItems(spawnData?.items),
                scripts = MapObjectDataUtility.CloneScripts(spawnData?.scripts),
                parameters = spawnData?.parameters ?? string.Empty,
                position = new Vector2(x, y),
                isActive = true,
                runtimeState = new MapObjectRuntimeStateData()
            };

            obj.runtimeState.InitializeFromAttributes(obj.attributes);
            obj.InitializeDynamicRuntimeState();
            obj.RefreshLegacyParameters();
            room.AddObject(obj);

            // Add as spawn point for player
            room.spawnPoints.Add(new SpawnPoint
            {
                tileCoord = WorldCoordinates.PixelToTile(new Vector2(x, y)),
                type = SpawnType.Player
            });
        }

        /// <summary>
        /// Create area trigger.
        /// </summary>
        private static void CreateArea(RoomInstance room, ObjectSpawnData data)
        {
            CreateArea(room, data, ResolveLegacyPlacementTileCoord(room, data.tileCoord));
        }

        private static void CreateArea(RoomInstance room, ObjectSpawnData data, Vector2Int placementTileCoord)
        {
            if (data == null)
            {
                return;
            }

            data.EnsureStructuredData();

            var obj = new ObjectInstance
            {
                objectId = data.id,
                objectType = "area",
                definitionId = data.GetResolvedDefinitionId(),
                definition = data.definition,
                code = data.code ?? string.Empty,
                uid = data.uid ?? string.Empty,
                attributes = MapObjectDataUtility.CloneAttributes(data.attributes),
                items = MapObjectDataUtility.CloneItems(data.items),
                scripts = MapObjectDataUtility.CloneScripts(data.scripts),
                parameters = data.parameters ?? string.Empty,
                position = WorldCoordinates.TileToPixel(placementTileCoord),
                isActive = true,
                runtimeState = new MapObjectRuntimeStateData()
            };

            obj.runtimeState.InitializeFromAttributes(obj.attributes);
            obj.InitializeDynamicRuntimeState();
            obj.RefreshLegacyParameters();
            room.AddObject(obj);
        }

        /// <summary>
        /// Create bonus pickup.
        /// </summary>
        private static void CreateBonus(RoomInstance room, ObjectSpawnData spawnData, float x, float y)
        {
            spawnData?.EnsureStructuredData();

            var obj = new ObjectInstance
            {
                objectId = spawnData?.id ?? string.Empty,
                objectType = "bonus",
                definitionId = spawnData?.GetResolvedDefinitionId() ?? string.Empty,
                definition = spawnData?.definition,
                code = spawnData?.code ?? string.Empty,
                uid = spawnData?.uid ?? string.Empty,
                attributes = MapObjectDataUtility.CloneAttributes(spawnData?.attributes),
                items = MapObjectDataUtility.CloneItems(spawnData?.items),
                scripts = MapObjectDataUtility.CloneScripts(spawnData?.scripts),
                parameters = spawnData?.parameters ?? string.Empty,
                position = new Vector2(x, y),
                isActive = true,
                runtimeState = new MapObjectRuntimeStateData()
            };

            obj.runtimeState.InitializeFromAttributes(obj.attributes);
            obj.InitializeDynamicRuntimeState();
            obj.RefreshLegacyParameters();
            room.AddObject(obj);
        }

        /// <summary>
        /// Create trap object.
        /// </summary>
        private static void CreateTrap(RoomInstance room, ObjectSpawnData spawnData, float x, float y)
        {
            spawnData?.EnsureStructuredData();

            var obj = new ObjectInstance
            {
                objectId = spawnData?.id ?? string.Empty,
                objectType = "trap",
                definitionId = spawnData?.GetResolvedDefinitionId() ?? string.Empty,
                definition = spawnData?.definition,
                code = spawnData?.code ?? string.Empty,
                uid = spawnData?.uid ?? string.Empty,
                attributes = MapObjectDataUtility.CloneAttributes(spawnData?.attributes),
                items = MapObjectDataUtility.CloneItems(spawnData?.items),
                scripts = MapObjectDataUtility.CloneScripts(spawnData?.scripts),
                parameters = spawnData?.parameters ?? string.Empty,
                position = new Vector2(x, y),
                isActive = true,
                runtimeState = new MapObjectRuntimeStateData()
            };

            obj.runtimeState.InitializeFromAttributes(obj.attributes);
            obj.InitializeDynamicRuntimeState();
            obj.RefreshLegacyParameters();
            room.AddObject(obj);
        }

        /// <summary>
        /// Place random enemies from spawn point data.
        /// Mirrors AS3 Location.setRandomUnits().
        /// </summary>
        private static void PlaceRandomEnemies(RoomInstance room, RoomTemplate template, RoomDifficulty difficulty)
        {
            // Use spawn points marked as enemy spawns
            var enemySpawns = new List<SpawnPoint>();
            foreach (var sp in room.spawnPoints)
            {
                if (sp.type == SpawnType.Enemy || sp.type == SpawnType.Boss)
                {
                    enemySpawns.Add(sp);
                }
            }

            if (enemySpawns.Count == 0) return;

            // Determine enemy count based on difficulty
            int totalDesired = difficulty.GetTotalEnemyCount();
            if (totalDesired <= 0) totalDesired = 3; // Fallback default
            int enemyCount = Mathf.Min(totalDesired, enemySpawns.Count);

            // Shuffle spawn points
            for (int i = enemySpawns.Count - 1; i > 0; i--)
            {
                int j = Random.Range(0, i + 1);
                var temp = enemySpawns[i];
                enemySpawns[i] = enemySpawns[j];
                enemySpawns[j] = temp;
            }

            for (int i = 0; i < enemyCount && i < enemySpawns.Count; i++)
            {
                var spawn = enemySpawns[i];
                string unitId = !string.IsNullOrEmpty(spawn.unitId) ? spawn.unitId : "raider";

                float px = (spawn.tileCoord.x + 0.5f) * WorldConstants.TILE_SIZE;
                float py = (spawn.tileCoord.y + 1f) * WorldConstants.TILE_SIZE - 1;

                CreateUnit(room, unitId, px, py, difficulty);
            }
        }

        /// <summary>
        /// Place XP bonus collectibles.
        /// Mirrors AS3 Location.createXpBonuses().
        /// Tries to place bonuses in empty air tiles across room quadrants.
        /// </summary>
        private static void PlaceXpBonuses(RoomInstance room, int maxBonuses)
        {
            int placed = 0;
            int quadrant = 4; // Start top-left, cycle through quadrants

            for (int attempt = 0; attempt < 100 && placed < maxBonuses; attempt++)
            {
                // Calculate quadrant bounds (AS3 cycles through 4 quadrants)
                int minX = 2, maxX = room.width - 2;
                int minY = 2, maxY = room.height - 2;

                switch (quadrant)
                {
                    case 4: maxX = room.width / 2; maxY = room.height / 2; break;
                    case 3: minX = room.width / 2; maxY = room.height / 2; break;
                    case 2: maxX = room.width / 2; minY = room.height / 2; break;
                    case 1: minX = room.width / 2; minY = room.height / 2; break;
                }

                int tx = Random.Range(minX, maxX);
                int ty = Random.Range(minY, maxY);

                var tile = room.GetTileAtCoord(new Vector2Int(tx, ty));
                if (tile != null && tile.physicsType == TilePhysicsType.Air)
                {
                    // Check adjacent tile is also air (AS3 checks left/right neighbor)
                    var leftTile = room.GetTileAtCoord(new Vector2Int(tx - 1, ty));
                    var rightTile = room.GetTileAtCoord(new Vector2Int(tx + 1, ty));
                    if ((leftTile != null && leftTile.physicsType == TilePhysicsType.Air) ||
                        (rightTile != null && rightTile.physicsType == TilePhysicsType.Air))
                    {
                        CreateBonus(room, new ObjectSpawnData
                        {
                            id = "xp",
                            type = "bonus",
                            tileCoord = new Vector2Int(tx, ty)
                        },
                            (tx + 0.5f) * WorldConstants.TILE_SIZE,
                            (ty + 0.5f) * WorldConstants.TILE_SIZE);
                        placed++;
                        if (quadrant > 0) quadrant--;
                    }
                }
            }
        }

        /// <summary>
        /// Calculate unit health based on type and level.
        /// Placeholder - should eventually use your GameDatabase definitions.
        /// </summary>
        private static float CalculateUnitHealth(string unitId, int level)
        {
            // Base health values (rough estimates from AS3 data)
            float baseHp = unitId switch
            {
                "raider" => 50f,
                "zombie" => 80f,
                "robot" => 120f,
                "bloat" => 40f,
                "turret" => 60f,
                "slime" => 30f,
                "ant" => 25f,
                "rat" => 15f,
                "mine" => 10f,
                _ => 50f
            };

            // Scale with level (roughly matches AS3 scaling)
            return baseHp * (1f + level * 0.15f);
        }

        private static Vector2Int ResolveLegacyPlacementTileCoord(RoomInstance room, Vector2Int legacyTileCoord)
        {
            int borderOffset = room != null ? Mathf.Max(0, room.borderOffset) : 0;
            int roomHeight = room != null ? room.height : WorldConstants.ROOM_HEIGHT;

            return new Vector2Int(
                legacyTileCoord.x + borderOffset,
                roomHeight - borderOffset - 1 - legacyTileCoord.y);
        }

        private static Vector2 ResolveLegacyBottomAnchorPixels(RoomInstance room, ObjectSpawnData spawnData)
        {
            int borderOffset = room != null ? Mathf.Max(0, room.borderOffset) : 0;
            int roomHeight = room != null ? room.height : WorldConstants.ROOM_HEIGHT;
            float sizeTiles = Mathf.Max(1f, ResolvePlacementSizeTiles(spawnData));

            return new Vector2(
                (spawnData.tileCoord.x + borderOffset + 0.5f * sizeTiles) * WorldConstants.TILE_SIZE,
                (roomHeight - borderOffset - spawnData.tileCoord.y - 1) * WorldConstants.TILE_SIZE + 1f);
        }

        private static int ResolvePlacementSizeTiles(ObjectSpawnData spawnData)
        {
            if (spawnData?.definition != null)
            {
                return Mathf.Max(1, spawnData.definition.size);
            }

            string rawSize = spawnData?.GetAttribute("size", string.Empty);
            if (int.TryParse(rawSize, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedSize))
            {
                return Mathf.Max(1, parsedSize);
            }

            return 1;
        }
    }
}
