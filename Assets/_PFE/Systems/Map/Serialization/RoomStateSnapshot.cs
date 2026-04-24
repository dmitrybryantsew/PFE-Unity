using System;
using System.Collections.Generic;
using UnityEngine;

namespace PFE.Systems.Map.Serialization
{
    /// <summary>
    /// Serializable snapshot of a room's state for saving/loading.
    /// Contains all data needed to restore a room to a previous state.
    /// </summary>
    [Serializable]
    public class RoomStateSnapshot
    {
        // Identification
        public string roomId;
        public string templateId;
        public Vector3Int landPosition;

        // Room dimensions
        public int width;
        public int height;

        // State flags
        public bool isActive;
        public bool isVisited;

        // Room properties
        public RoomDifficultySnapshot difficulty;
        public RoomEnvironmentSnapshot environment;

        // Tile data - serialized as 1D array for JSON compatibility
        public TileStateSnapshot[] tiles;

        // Doors
        public DoorStateSnapshot[] doors;

        // Spawn points
        public SpawnPointSnapshot[] spawnPoints;

        // Background decorations
        public BackgroundDecorationSnapshot[] backgroundDecorations;

        // Units (enemies, NPCs)
        public UnitStateSnapshot[] units;

        // Objects (items, containers, etc.)
        public ObjectStateSnapshot[] objects;

        // Special features
        public bool hasBackgroundLayer;
        public string roomType;

        /// <summary>
        /// Create snapshot from a RoomInstance.
        /// </summary>
        public static RoomStateSnapshot CreateFromRoom(RoomInstance room)
        {
            if (room == null)
            {
                Debug.LogError("Cannot create snapshot from null room");
                return null;
            }

            var snapshot = new RoomStateSnapshot
            {
                roomId = room.id,
                templateId = room.templateId,
                landPosition = room.landPosition,
                width = room.width,
                height = room.height,
                isActive = room.isActive,
                isVisited = room.isVisited,
                difficulty = RoomDifficultySnapshot.CreateFrom(room.difficulty),
                environment = RoomEnvironmentSnapshot.CreateFrom(room.environment),
                hasBackgroundLayer = room.hasBackgroundLayer,
                roomType = room.roomType
            };

            // Serialize tiles
            snapshot.tiles = new TileStateSnapshot[room.width * room.height];
            for (int x = 0; x < room.width; x++)
            {
                for (int y = 0; y < room.height; y++)
                {
                    int index = y * room.width + x;
                    if (room.tiles[x, y] != null)
                    {
                        snapshot.tiles[index] = TileStateSnapshot.CreateFrom(room.tiles[x, y]);
                    }
                }
            }

            // Serialize doors
            snapshot.doors = new DoorStateSnapshot[room.doors.Count];
            for (int i = 0; i < room.doors.Count; i++)
            {
                snapshot.doors[i] = DoorStateSnapshot.CreateFrom(room.doors[i]);
            }

            // Serialize spawn points
            snapshot.spawnPoints = new SpawnPointSnapshot[room.spawnPoints.Count];
            for (int i = 0; i < room.spawnPoints.Count; i++)
            {
                snapshot.spawnPoints[i] = SpawnPointSnapshot.CreateFrom(room.spawnPoints[i]);
            }

            snapshot.backgroundDecorations = new BackgroundDecorationSnapshot[room.backgroundDecorations.Count];
            for (int i = 0; i < room.backgroundDecorations.Count; i++)
            {
                snapshot.backgroundDecorations[i] = BackgroundDecorationSnapshot.CreateFrom(room.backgroundDecorations[i]);
            }

            // Serialize units
            snapshot.units = new UnitStateSnapshot[room.units.Count];
            for (int i = 0; i < room.units.Count; i++)
            {
                snapshot.units[i] = UnitStateSnapshot.CreateFrom(room.units[i]);
            }

            // Serialize objects
            snapshot.objects = new ObjectStateSnapshot[room.objects.Count];
            for (int i = 0; i < room.objects.Count; i++)
            {
                snapshot.objects[i] = ObjectStateSnapshot.CreateFrom(room.objects[i]);
            }

            return snapshot;
        }

        /// <summary>
        /// Restore a RoomInstance from this snapshot.
        /// Note: This creates/updates data in the room but doesn't handle Unity objects.
        /// </summary>
        public void RestoreToRoom(RoomInstance room)
        {
            if (room == null)
            {
                Debug.LogError("Cannot restore snapshot to null room");
                return;
            }

            room.id = roomId;
            room.templateId = templateId;
            room.landPosition = landPosition;
            room.width = width;
            room.height = height;
            room.isActive = isActive;
            room.isVisited = isVisited;
            room.hasBackgroundLayer = hasBackgroundLayer;
            room.roomType = roomType;

            // Restore difficulty
            if (difficulty != null)
            {
                difficulty.RestoreTo(room.difficulty);
            }

            // Restore environment
            if (environment != null)
            {
                environment.RestoreTo(room.environment);
            }

            // Restore tiles
            if (tiles != null && room.tiles != null)
            {
                for (int x = 0; x < width && x < room.width; x++)
                {
                    for (int y = 0; y < height && y < room.height; y++)
                    {
                        int index = y * width + x;
                        if (index < tiles.Length && tiles[index] != null)
                        {
                            if (room.tiles[x, y] == null)
                            {
                                room.tiles[x, y] = new TileData();
                            }
                            tiles[index].RestoreTo(room.tiles[x, y]);
                        }
                    }
                }
            }

            // Restore doors
            room.doors.Clear();
            if (doors != null)
            {
                for (int i = 0; i < doors.Length; i++)
                {
                    if (doors[i] != null)
                    {
                        room.doors.Add(doors[i].ToDoorInstance());
                    }
                }
            }

            // Restore spawn points
            room.spawnPoints.Clear();
            if (spawnPoints != null)
            {
                for (int i = 0; i < spawnPoints.Length; i++)
                {
                    if (spawnPoints[i] != null)
                    {
                        room.spawnPoints.Add(spawnPoints[i].ToSpawnPoint());
                    }
                }
            }

            room.backgroundDecorations.Clear();
            if (backgroundDecorations != null)
            {
                for (int i = 0; i < backgroundDecorations.Length; i++)
                {
                    if (backgroundDecorations[i] != null)
                    {
                        room.backgroundDecorations.Add(backgroundDecorations[i].ToBackgroundDecorationInstance());
                    }
                }
            }

            // Restore units
            room.units.Clear();
            if (units != null)
            {
                for (int i = 0; i < units.Length; i++)
                {
                    if (units[i] != null)
                    {
                        room.units.Add(units[i].ToUnitInstance());
                    }
                }
            }

            // Restore objects
            room.ClearObjects();
            if (objects != null)
            {
                for (int i = 0; i < objects.Length; i++)
                {
                    if (objects[i] != null)
                    {
                        room.AddObject(objects[i].ToObjectInstance());
                    }
                }
            }

            room.RebuildRuntimeLayers();
        }
    }

    /// <summary>
    /// Snapshot of a single tile's state.
    /// </summary>
    [Serializable]
    public class TileStateSnapshot
    {
        public int gridX;
        public int gridY;
        public int physicsType;
        public bool indestructible;
        public int hitPoints;
        public int damageThreshold;
        public string frontGraphic;
        public string backGraphic;
        public int visualId;
        public int visualId2;
        public float opacity;
        public int heightLevel;
        public int slopeType;
        public int stairType;
        public bool isLedge;
        public bool hasWater;
        public int material;
        public bool canPlaceObjects;
        public string doorId;
        public string trapId;

        public static TileStateSnapshot CreateFrom(TileData tile)
        {
            if (tile == null) return null;

            return new TileStateSnapshot
            {
                gridX = tile.gridPosition.x,
                gridY = tile.gridPosition.y,
                physicsType = (int)tile.physicsType,
                indestructible = tile.indestructible,
                hitPoints = tile.hitPoints,
                damageThreshold = tile.damageThreshold,
                frontGraphic = tile.GetFrontGraphic(),
                backGraphic = tile.GetBackGraphic(),
                visualId = tile.visualId,
                visualId2 = tile.visualId2,
                opacity = tile.opacity,
                heightLevel = tile.heightLevel,
                slopeType = tile.slopeType,
                stairType = tile.stairType,
                isLedge = tile.isLedge,
                hasWater = tile.hasWater,
                material = (int)tile.material,
                canPlaceObjects = tile.canPlaceObjects,
                doorId = tile.doorId,
                trapId = tile.trapId
            };
        }

        public void RestoreTo(TileData tile)
        {
            if (tile == null) return;

            tile.gridPosition = new Vector2Int(gridX, gridY);
            tile.physicsType = (TilePhysicsType)physicsType;
            tile.indestructible = indestructible;
            tile.hitPoints = hitPoints;
            tile.damageThreshold = damageThreshold;
            tile.SetFrontGraphic(frontGraphic);
            tile.SetBackGraphic(backGraphic);
            tile.visualId = visualId;
            tile.visualId2 = visualId2;
            tile.opacity = opacity;
            tile.heightLevel = heightLevel;
            tile.slopeType = slopeType;
            tile.stairType = stairType;
            tile.isLedge = isLedge;
            tile.hasWater = hasWater;
            tile.material = (MaterialType)material;
            tile.canPlaceObjects = canPlaceObjects;
            tile.doorId = doorId;
            tile.trapId = trapId;
        }
    }

    /// <summary>
    /// Snapshot of door state.
    /// </summary>
    [Serializable]
    public class DoorStateSnapshot
    {
        public int doorIndex;
        public int side;
        public int tileX;
        public int tileY;
        public int targetRoomX;
        public int targetRoomY;
        public int targetRoomZ;
        public int targetDoorIndex;
        public int quality;
        public bool isLocked;
        public int lockLevel;
        public string keyItemId;
        public bool isActive;
        public string doorGraphic;

        public static DoorStateSnapshot CreateFrom(DoorInstance door)
        {
            if (door == null) return null;

            return new DoorStateSnapshot
            {
                doorIndex = door.doorIndex,
                side = (int)door.side,
                tileX = door.tilePosition.x,
                tileY = door.tilePosition.y,
                targetRoomX = door.targetRoomPosition.x,
                targetRoomY = door.targetRoomPosition.y,
                targetRoomZ = door.targetRoomPosition.z,
                targetDoorIndex = door.targetDoorIndex,
                quality = (int)door.quality,
                isLocked = door.isLocked,
                lockLevel = door.lockLevel,
                keyItemId = door.keyItemId,
                isActive = door.isActive,
                doorGraphic = door.doorGraphic
            };
        }

        public DoorInstance ToDoorInstance()
        {
            return new DoorInstance
            {
                doorIndex = doorIndex,
                side = (DoorSide)side,
                tilePosition = new Vector2Int(tileX, tileY),
                targetRoomPosition = new Vector3Int(targetRoomX, targetRoomY, targetRoomZ),
                targetDoorIndex = targetDoorIndex,
                quality = (DoorQuality)quality,
                isLocked = isLocked,
                lockLevel = lockLevel,
                keyItemId = keyItemId,
                isActive = isActive,
                doorGraphic = doorGraphic
            };
        }
    }

    /// <summary>
    /// Snapshot of spawn point state.
    /// </summary>
    [Serializable]
    public class SpawnPointSnapshot
    {
        public int tileX;
        public int tileY;
        public int spawnType;
        public string unitId;
        public float facingDirection;

        public static SpawnPointSnapshot CreateFrom(SpawnPoint spawn)
        {
            if (spawn == null) return null;

            return new SpawnPointSnapshot
            {
                tileX = spawn.tileCoord.x,
                tileY = spawn.tileCoord.y,
                spawnType = (int)spawn.type,
                unitId = spawn.unitId,
                facingDirection = spawn.facingDirection
            };
        }

        public SpawnPoint ToSpawnPoint()
        {
            return new SpawnPoint
            {
                tileCoord = new Vector2Int(tileX, tileY),
                type = (SpawnType)spawnType,
                unitId = unitId,
                facingDirection = facingDirection
            };
        }
    }

    /// <summary>
    /// Snapshot of room background decorations.
    /// </summary>
    [Serializable]
    public class BackgroundDecorationSnapshot
    {
        public string decorationId;
        public int tileX;
        public int tileY;

        public static BackgroundDecorationSnapshot CreateFrom(BackgroundDecorationInstance decoration)
        {
            if (decoration == null) return null;

            return new BackgroundDecorationSnapshot
            {
                decorationId = decoration.decorationId,
                tileX = decoration.tileCoord.x,
                tileY = decoration.tileCoord.y
            };
        }

        public BackgroundDecorationInstance ToBackgroundDecorationInstance()
        {
            return new BackgroundDecorationInstance
            {
                decorationId = decorationId,
                tileCoord = new Vector2Int(tileX, tileY)
            };
        }
    }

    /// <summary>
    /// Snapshot of unit state (enemy, NPC).
    /// </summary>
    [Serializable]
    public class UnitStateSnapshot
    {
        public string unitId;
        public string unitType;
        public float posX;
        public float posY;
        public bool isDead;
        public float currentHealth;
        public float maxHealth;

        public static UnitStateSnapshot CreateFrom(UnitInstance unit)
        {
            if (unit == null) return null;

            return new UnitStateSnapshot
            {
                unitId = unit.unitId,
                unitType = unit.unitType ?? "",
                posX = unit.position.x,
                posY = unit.position.y,
                isDead = unit.IsDead,
                currentHealth = unit.currentHealth,
                maxHealth = unit.maxHealth
            };
        }

        public UnitInstance ToUnitInstance()
        {
            return new UnitInstance
            {
                unitId = unitId,
                unitType = unitType,
                position = new Vector2(posX, posY),
                isDead = isDead,
                currentHealth = currentHealth,
                maxHealth = maxHealth
            };
        }
    }

    /// <summary>
    /// Snapshot of object state (item, container, etc.).
    /// </summary>
    [Serializable]
    public class ObjectStateSnapshot
    {
        public string objectId;
        public string objectType;
        public string definitionId;
        public string code;
        public string uid;
        public string parameters;
        public float posX;
        public float posY;
        public bool isActive;
        public MapObjectAttributeData[] attributes;
        public MapObjectItemSnapshot[] items;
        public MapObjectScriptSnapshot[] scripts;
        public MapObjectRuntimeStateSnapshot runtimeState;

        public static ObjectStateSnapshot CreateFrom(ObjectInstance obj)
        {
            if (obj == null) return null;

            obj.EnsureStructuredData();

            return new ObjectStateSnapshot
            {
                objectId = obj.objectId,
                objectType = obj.objectType ?? "",
                definitionId = obj.GetResolvedDefinitionId(),
                code = obj.code ?? "",
                uid = obj.uid ?? "",
                parameters = obj.parameters ?? "",
                posX = obj.position.x,
                posY = obj.position.y,
                isActive = obj.isActive,
                attributes = CreateAttributes(obj.attributes),
                items = MapObjectItemSnapshot.CreateArray(obj.items),
                scripts = MapObjectScriptSnapshot.CreateArray(obj.scripts),
                runtimeState = MapObjectRuntimeStateSnapshot.CreateFrom(obj.runtimeState)
            };
        }

        public ObjectInstance ToObjectInstance()
        {
            ObjectInstance instance = new ObjectInstance
            {
                objectId = objectId,
                objectType = objectType,
                definitionId = definitionId ?? objectId ?? "",
                code = code ?? "",
                uid = uid ?? "",
                parameters = parameters ?? "",
                position = new Vector2(posX, posY),
                isActive = isActive,
                attributes = RestoreAttributes(attributes),
                items = MapObjectItemSnapshot.RestoreArray(items),
                scripts = MapObjectScriptSnapshot.RestoreArray(scripts),
                runtimeState = runtimeState?.ToRuntimeState() ?? new MapObjectRuntimeStateData()
            };

            instance.EnsureStructuredData();
            return instance;
        }

        internal static MapObjectAttributeData[] CreateAttributes(List<MapObjectAttributeData> source)
        {
            if (source == null || source.Count == 0)
            {
                return Array.Empty<MapObjectAttributeData>();
            }

            MapObjectAttributeData[] result = new MapObjectAttributeData[source.Count];
            for (int i = 0; i < source.Count; i++)
            {
                MapObjectAttributeData attribute = source[i];
                result[i] = attribute == null
                    ? null
                    : new MapObjectAttributeData
                    {
                        key = attribute.key,
                        value = attribute.value
                    };
            }

            return result;
        }

        internal static List<MapObjectAttributeData> RestoreAttributes(MapObjectAttributeData[] source)
        {
            List<MapObjectAttributeData> result = new List<MapObjectAttributeData>();
            if (source == null)
            {
                return result;
            }

            for (int i = 0; i < source.Length; i++)
            {
                MapObjectAttributeData attribute = source[i];
                if (attribute == null)
                {
                    continue;
                }

                result.Add(new MapObjectAttributeData
                {
                    key = attribute.key,
                    value = attribute.value
                });
            }

            return result;
        }
    }

    [Serializable]
    public class MapObjectItemSnapshot
    {
        public string id;
        public MapObjectAttributeData[] attributes;

        public static MapObjectItemSnapshot[] CreateArray(List<MapObjectItemData> source)
        {
            if (source == null || source.Count == 0)
            {
                return Array.Empty<MapObjectItemSnapshot>();
            }

            MapObjectItemSnapshot[] result = new MapObjectItemSnapshot[source.Count];
            for (int i = 0; i < source.Count; i++)
            {
                MapObjectItemData item = source[i];
                if (item == null)
                {
                    continue;
                }

                result[i] = new MapObjectItemSnapshot
                {
                    id = item.id,
                    attributes = ObjectStateSnapshot.CreateAttributes(item.attributes)
                };
            }

            return result;
        }

        public static List<MapObjectItemData> RestoreArray(MapObjectItemSnapshot[] source)
        {
            List<MapObjectItemData> result = new List<MapObjectItemData>();
            if (source == null)
            {
                return result;
            }

            for (int i = 0; i < source.Length; i++)
            {
                MapObjectItemSnapshot item = source[i];
                if (item == null)
                {
                    continue;
                }

                result.Add(new MapObjectItemData
                {
                    id = item.id,
                    attributes = ObjectStateSnapshot.RestoreAttributes(item.attributes)
                });
            }

            return result;
        }
    }

    [Serializable]
    public class MapObjectScriptSnapshot
    {
        public string eventName;
        public MapObjectScriptActionSnapshot[] actions;

        public static MapObjectScriptSnapshot[] CreateArray(List<MapObjectScriptData> source)
        {
            if (source == null || source.Count == 0)
            {
                return Array.Empty<MapObjectScriptSnapshot>();
            }

            MapObjectScriptSnapshot[] result = new MapObjectScriptSnapshot[source.Count];
            for (int i = 0; i < source.Count; i++)
            {
                MapObjectScriptData script = source[i];
                if (script == null)
                {
                    continue;
                }

                result[i] = new MapObjectScriptSnapshot
                {
                    eventName = script.eventName,
                    actions = MapObjectScriptActionSnapshot.CreateArray(script.actions)
                };
            }

            return result;
        }

        public static List<MapObjectScriptData> RestoreArray(MapObjectScriptSnapshot[] source)
        {
            List<MapObjectScriptData> result = new List<MapObjectScriptData>();
            if (source == null)
            {
                return result;
            }

            for (int i = 0; i < source.Length; i++)
            {
                MapObjectScriptSnapshot script = source[i];
                if (script == null)
                {
                    continue;
                }

                result.Add(new MapObjectScriptData
                {
                    eventName = script.eventName,
                    actions = MapObjectScriptActionSnapshot.RestoreArray(script.actions)
                });
            }

            return result;
        }
    }

    [Serializable]
    public class MapObjectScriptActionSnapshot
    {
        public string act;
        public string targ;
        public string val;

        public static MapObjectScriptActionSnapshot[] CreateArray(List<MapObjectScriptActionData> source)
        {
            if (source == null || source.Count == 0)
            {
                return Array.Empty<MapObjectScriptActionSnapshot>();
            }

            MapObjectScriptActionSnapshot[] result = new MapObjectScriptActionSnapshot[source.Count];
            for (int i = 0; i < source.Count; i++)
            {
                MapObjectScriptActionData action = source[i];
                if (action == null)
                {
                    continue;
                }

                result[i] = new MapObjectScriptActionSnapshot
                {
                    act = action.act,
                    targ = action.targ,
                    val = action.val
                };
            }

            return result;
        }

        public static List<MapObjectScriptActionData> RestoreArray(MapObjectScriptActionSnapshot[] source)
        {
            List<MapObjectScriptActionData> result = new List<MapObjectScriptActionData>();
            if (source == null)
            {
                return result;
            }

            for (int i = 0; i < source.Length; i++)
            {
                MapObjectScriptActionSnapshot action = source[i];
                if (action == null)
                {
                    continue;
                }

                result.Add(new MapObjectScriptActionData
                {
                    act = action.act,
                    targ = action.targ,
                    val = action.val
                });
            }

            return result;
        }
    }

    [Serializable]
    public class MapObjectRuntimeStateSnapshot
    {
        public bool isDestroyed;
        public bool isOpen;
        public bool isExploded;
        public int lootState;
        public bool hasLockValue;
        public float lockValue;
        public bool hasLockLevel;
        public int lockLevel;
        public bool hasMineValue;
        public int mineValue;
        public bool hasDifficultyState;
        public int difficultyState;
        public bool hasSignState;
        public int signState;
        public MapObjectDynamicStateSnapshot dynamicState;

        public static MapObjectRuntimeStateSnapshot CreateFrom(MapObjectRuntimeStateData state)
        {
            state ??= new MapObjectRuntimeStateData();
            return new MapObjectRuntimeStateSnapshot
            {
                isDestroyed = state.isDestroyed,
                isOpen = state.isOpen,
                isExploded = state.isExploded,
                lootState = state.lootState,
                hasLockValue = state.hasLockValue,
                lockValue = state.lockValue,
                hasLockLevel = state.hasLockLevel,
                lockLevel = state.lockLevel,
                hasMineValue = state.hasMineValue,
                mineValue = state.mineValue,
                hasDifficultyState = state.hasDifficultyState,
                difficultyState = state.difficultyState,
                hasSignState = state.hasSignState,
                signState = state.signState,
                dynamicState = MapObjectDynamicStateSnapshot.CreateFrom(state.dynamicState)
            };
        }

        public MapObjectRuntimeStateData ToRuntimeState()
        {
            return new MapObjectRuntimeStateData
            {
                isDestroyed = isDestroyed,
                isOpen = isOpen,
                isExploded = isExploded,
                lootState = lootState,
                hasLockValue = hasLockValue,
                lockValue = lockValue,
                hasLockLevel = hasLockLevel,
                lockLevel = lockLevel,
                hasMineValue = hasMineValue,
                mineValue = mineValue,
                hasDifficultyState = hasDifficultyState,
                difficultyState = difficultyState,
                hasSignState = hasSignState,
                signState = signState,
                dynamicState = dynamicState?.ToDynamicState() ?? new MapObjectDynamicStateData()
            };
        }
    }

    [Serializable]
    public class MapObjectDynamicStateSnapshot
    {
        public bool isDynamic;
        public bool isGrounded;
        public bool isHeldByTelekinesis;
        public bool isThrown;
        public bool hasTelekineticTarget;
        public float velocityX;
        public float velocityY;
        public float telekineticTargetX;
        public float telekineticTargetY;
        public float throwGraceTime;
        public float lastImpactSpeed;

        public static MapObjectDynamicStateSnapshot CreateFrom(MapObjectDynamicStateData state)
        {
            state ??= new MapObjectDynamicStateData();
            return new MapObjectDynamicStateSnapshot
            {
                isDynamic = state.isDynamic,
                isGrounded = state.isGrounded,
                isHeldByTelekinesis = state.isHeldByTelekinesis,
                isThrown = state.isThrown,
                hasTelekineticTarget = state.hasTelekineticTarget,
                velocityX = state.velocity.x,
                velocityY = state.velocity.y,
                telekineticTargetX = state.telekineticTarget.x,
                telekineticTargetY = state.telekineticTarget.y,
                throwGraceTime = state.throwGraceTime,
                lastImpactSpeed = state.lastImpactSpeed
            };
        }

        public MapObjectDynamicStateData ToDynamicState()
        {
            return new MapObjectDynamicStateData
            {
                isDynamic = isDynamic,
                isGrounded = isGrounded,
                isHeldByTelekinesis = isHeldByTelekinesis,
                isThrown = isThrown,
                hasTelekineticTarget = hasTelekineticTarget,
                velocity = new Vector2(velocityX, velocityY),
                telekineticTarget = new Vector2(telekineticTargetX, telekineticTargetY),
                throwGraceTime = throwGraceTime,
                lastImpactSpeed = lastImpactSpeed
            };
        }
    }

    /// <summary>
    /// Snapshot of room difficulty.
    /// </summary>
    [Serializable]
    public class RoomDifficultySnapshot
    {
        public float baseDifficulty;
        public float enemyLevel;
        public float lockLevel;
        public float mechLevel;
        public float weaponLevel;
        public int[] enemyCounts;
        public int hiddenEnemyCount;

        public static RoomDifficultySnapshot CreateFrom(RoomDifficulty difficulty)
        {
            if (difficulty == null) return new RoomDifficultySnapshot();

            return new RoomDifficultySnapshot
            {
                baseDifficulty = difficulty.baseDifficulty,
                enemyLevel = difficulty.enemyLevel,
                lockLevel = difficulty.lockLevel,
                mechLevel = difficulty.mechLevel,
                weaponLevel = difficulty.weaponLevel,
                enemyCounts = (int[])difficulty.enemyCounts.Clone(),
                hiddenEnemyCount = difficulty.hiddenEnemyCount
            };
        }

        public void RestoreTo(RoomDifficulty difficulty)
        {
            if (difficulty == null) return;

            difficulty.baseDifficulty = baseDifficulty;
            difficulty.enemyLevel = enemyLevel;
            difficulty.lockLevel = lockLevel;
            difficulty.mechLevel = mechLevel;
            difficulty.weaponLevel = weaponLevel;
            difficulty.enemyCounts = (int[])enemyCounts.Clone();
            difficulty.hiddenEnemyCount = hiddenEnemyCount;
        }
    }

    /// <summary>
    /// Snapshot of room environment.
    /// </summary>
    [Serializable]
    public class RoomEnvironmentSnapshot
    {
        public string musicTrack;
        public string colorScheme;
        public string backgroundColorScheme;
        public string backgroundWall;
        public int backgroundForm;
        public bool transparentBackground;
        public bool hasSky;
        public int borderType;
        public bool noBlackReveal;
        public float visibilityMultiplier;
        public int lightsOn;
        public bool returnsToDarkness;
        public int waterLevel;
        public int waterType;
        public float waterOpacity;
        public float waterDamage;
        public int waterDamageType;
        public float radiation;
        public float radiationDamage;
        public int darkness;

        public static RoomEnvironmentSnapshot CreateFrom(RoomEnvironment environment)
        {
            if (environment == null) return new RoomEnvironmentSnapshot();

            return new RoomEnvironmentSnapshot
            {
                musicTrack = environment.musicTrack,
                colorScheme = environment.colorScheme,
                backgroundColorScheme = environment.backgroundColorScheme,
                backgroundWall = environment.backgroundWall,
                backgroundForm = environment.backgroundForm,
                transparentBackground = environment.transparentBackground,
                hasSky = environment.hasSky,
                borderType = environment.borderType,
                noBlackReveal = environment.noBlackReveal,
                visibilityMultiplier = environment.visibilityMultiplier,
                lightsOn = environment.lightsOn,
                returnsToDarkness = environment.returnsToDarkness,
                waterLevel = environment.waterLevel,
                waterType = environment.waterType,
                waterOpacity = environment.waterOpacity,
                waterDamage = environment.waterDamage,
                waterDamageType = environment.waterDamageType,
                radiation = environment.radiation,
                radiationDamage = environment.radiationDamage,
                darkness = environment.darkness
            };
        }

        public void RestoreTo(RoomEnvironment environment)
        {
            if (environment == null) return;

            environment.musicTrack = musicTrack;
            environment.colorScheme = colorScheme;
            environment.backgroundColorScheme = backgroundColorScheme;
            environment.backgroundWall = backgroundWall;
            environment.backgroundForm = backgroundForm;
            environment.transparentBackground = transparentBackground;
            environment.hasSky = hasSky;
            environment.borderType = borderType;
            environment.noBlackReveal = noBlackReveal;
            environment.visibilityMultiplier = visibilityMultiplier;
            environment.lightsOn = lightsOn;
            environment.returnsToDarkness = returnsToDarkness;
            environment.waterLevel = waterLevel;
            environment.waterType = waterType;
            environment.waterOpacity = waterOpacity;
            environment.waterDamage = waterDamage;
            environment.waterDamageType = waterDamageType;
            environment.radiation = radiation;
            environment.radiationDamage = radiationDamage;
            environment.darkness = darkness;
        }
    }
}
