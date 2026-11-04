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

        /// <summary>
        /// Loot lying on this room's floor — <see cref="RoomInstance.droppedItems"/>.
        /// </summary>
        /// <remarks>
        /// <para><b>Without this a save silently deleted every item the player had dropped.</b> Loot was
        /// a bare GameObject in a static list, so it was in no room and in no snapshot: leaving the land
        /// destroyed it, and loading a save never restored it. It is now a record on the room like a prop
        /// or a unit, and a record that is not here is a record that does not survive the file.</para>
        ///
        /// <para>Empty rather than null for a save written before this field existed, so
        /// <see cref="RestoreToRoom"/> can iterate without a null test at every call site.</para>
        /// </remarks>
        public DroppedItemSnapshot[] droppedItems;

        // Destroyed tiles (for delta persistence)
        public Vector2Int[] destroyedTiles;

        // Per-entity unit overrides
        public UnitOverrideSnapshot[] unitOverrides;

        // Special features
        public bool hasBackgroundLayer;
        public string roomType;

        /// <summary>
        /// AS3 <c>Location.noMap</c> — round-tripped so a reloaded <c>z = 1</c> interior stays off the
        /// minimap. Without it a save/load would resurrect the interior into the cell it shares with its
        /// exterior, because the minimap keys rooms on <c>x</c>/<c>y</c> only. See
        /// <see cref="RoomInstance.noMap"/>.
        /// </summary>
        public bool noMap;

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
                roomType = room.roomType,
                noMap = room.noMap
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

            // Serialize floor loot. A record is the only place a dropped stack exists, so this loop is
            // the difference between "the player's drop survives the file" and "it does not".
            if (room.droppedItems != null && room.droppedItems.Count > 0)
            {
                var loot = new List<DroppedItemSnapshot>(room.droppedItems.Count);
                for (int i = 0; i < room.droppedItems.Count; i++)
                {
                    DroppedItemSnapshot item = DroppedItemSnapshot.CreateFrom(room.droppedItems[i]);
                    if (item != null)
                    {
                        loot.Add(item);
                    }
                }
                snapshot.droppedItems = loot.ToArray();
            }
            else
            {
                snapshot.droppedItems = Array.Empty<DroppedItemSnapshot>();
            }

            // Collect destroyed tiles
            var destroyed = new List<Vector2Int>();
            if (room.tiles != null)
            {
                for (int x = 0; x < room.width; x++)
                {
                    for (int y = 0; y < room.height; y++)
                    {
                        var tile = room.tiles[x, y];
                        if (tile != null && tile.IsDestroyed())
                        {
                            destroyed.Add(new Vector2Int(x, y));
                        }
                    }
                }
            }
            snapshot.destroyedTiles = destroyed.ToArray();

            // Collect unit overrides
            var overrides = new List<UnitOverrideSnapshot>();
            if (room.units != null)
            {
                foreach (var u in room.units)
                {
                    if (u != null && !string.IsNullOrEmpty(u.entityId))
                    {
                        overrides.Add(new UnitOverrideSnapshot
                        {
                            entityId = u.entityId,
                            isDead = u.IsDead,
                            health = u.currentHealth,
                            position = u.position
                        });
                    }
                }
            }
            snapshot.unitOverrides = overrides.ToArray();

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
            room.noMap = noMap;

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

            // Restore floor loot. The list is rebuilt wholesale rather than merged: a room restored from
            // a file is the authority on what is lying in it, and any live view for the old list is torn
            // down by `RoomVisualController.ClearVisuals` before `Initialize` rebuilds from these records.
            room.droppedItems ??= new List<DroppedItemInstance>();
            room.droppedItems.Clear();
            if (droppedItems != null)
            {
                for (int i = 0; i < droppedItems.Length; i++)
                {
                    DroppedItemInstance item = droppedItems[i]?.ToDroppedItemInstance();
                    if (item != null)
                    {
                        room.droppedItems.Add(item);
                    }
                }
            }

            // Re-derive every area's lift from the objects just restored.
            // `TileData.gravityMultiplier` is not part of the snapshot on purpose: it is a *derived*
            // value (area `@grav` × the area's enabled state), so persisting it would let a stale tile
            // outvote the object the player can actually see and toggle. Restoring is the moment the
            // tiles have been overwritten from the file, so the derivation has to run again here —
            // otherwise a save taken while standing in a lift shaft loads with the shaft switched off.
            if (room.objects != null)
            {
                for (int i = 0; i < room.objects.Count; i++)
                {
                    PFE.Systems.Map.AreaLift.ApplyCurrent(room, room.objects[i]);
                }
            }

            // Apply destroyed tiles if present
            if (destroyedTiles != null && room.tiles != null)
            {
                foreach (var coord in destroyedTiles)
                {
                    if (coord.x >= 0 && coord.x < room.width && coord.y >= 0 && coord.y < room.height)
                    {
                        var tile = room.tiles[coord.x, coord.y];
                        if (tile != null)
                        {
                            tile.Destroy();
                        }
                    }
                }
            }

            // Apply unit overrides if present
            if (unitOverrides != null && unitOverrides.Length > 0 && room.units != null)
            {
                var overrideMap = new Dictionary<string, UnitOverrideSnapshot>();
                foreach (var o in unitOverrides)
                {
                    if (o != null && !string.IsNullOrEmpty(o.entityId))
                        overrideMap[o.entityId] = o;
                }

                foreach (var u in room.units)
                {
                    if (u != null && !string.IsNullOrEmpty(u.entityId) && overrideMap.TryGetValue(u.entityId, out var ov))
                    {
                        u.isDead = ov.isDead;
                        u.currentHealth = ov.health;
                        u.position = ov.position;
                    }
                }
            }

            room.RebuildRuntimeLayers();
        }
    }

    /// <summary>
    /// Snapshot of one stack of floor loot — <see cref="DroppedItemInstance"/>.
    /// </summary>
    /// <remarks>
    /// One field per record field and nothing derived, which is what makes the round-trip exact: the
    /// position is room-local pixels (the record's own space), and <c>isWeapon</c> is what tells the
    /// loader which table <c>itemId</c> belongs to. A loader that guessed would put a weapon id through
    /// the item table and drop it — the bug <c>PlayerInventory.ResolveWeapon</c> documents.
    /// </remarks>
    [Serializable]
    public class DroppedItemSnapshot
    {
        public string itemId;
        public bool isWeapon;
        public int quantity;
        public int variant;
        public float condition;
        public bool autoCollect;
        public float posX;
        public float posY;

        public static DroppedItemSnapshot CreateFrom(DroppedItemInstance item)
        {
            if (item == null || string.IsNullOrEmpty(item.itemId))
            {
                return null;
            }

            return new DroppedItemSnapshot
            {
                itemId = item.itemId,
                isWeapon = item.isWeapon,
                quantity = item.quantity,
                variant = item.variant,
                condition = item.condition,
                autoCollect = item.autoCollect,
                posX = item.position.x,
                posY = item.position.y,
            };
        }

        public DroppedItemInstance ToDroppedItemInstance()
        {
            if (string.IsNullOrEmpty(itemId))
            {
                return null;
            }

            return new DroppedItemInstance
            {
                itemId = itemId,
                isWeapon = isWeapon,
                // A save written by a bug that stored a zero count would otherwise produce a record that
                // `RestoreForRoom` skips silently; one is what AS3's constructor defaults to.
                quantity = quantity > 0 ? quantity : 1,
                variant = variant,
                condition = condition > 0f ? condition : 1f,
                autoCollect = autoCollect,
                position = new Vector2(posX, posY),
            };
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

        /// <summary>
        /// AS3 <c>phis == 2</c> — see <see cref="TileData.levelTwoSolid"/>. Snapshot because the tile
        /// grid is restored wholesale and a grate's tiles are only re-stamped when its door presenter
        /// initialises; without this a save taken with grates closed would come back with ordinary
        /// walls. A save written before the field existed deserialises it as <c>false</c>, which is the
        /// same answer an unauthored door gives.
        /// </summary>
        public bool levelTwoSolid;

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
                trapId = tile.trapId,
                levelTwoSolid = tile.levelTwoSolid
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
            tile.levelTwoSolid = levelTwoSolid;
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
    /// <remarks>
    /// <para><b>Why this carries the whole placement and not just the position and the health.</b> A
    /// <see cref="UnitInstance"/> is not only "an id at a point": it is also the placement's
    /// <c>attributes</c> (which is where <c>npc</c>, <c>turn</c>, <c>qid</c>, <c>scr</c> and every other
    /// authored key live), its <c>&lt;item&gt;</c> children (the unit's own death drop) and its
    /// <c>&lt;scr&gt;</c> children (its death and alarm scripts). The snapshot used to write only the
    /// first eight fields, so a loaded save produced units that could not drop loot, could not run their
    /// death script, and — for an NPC — had no persona at all, because the id it was rebuilt from says
    /// <c>UnitNPC</c> and the persona is on the placement. The object half of the same file has always
    /// carried all three; this is the unit half catching up.</para>
    /// </remarks>
    [Serializable]
    public class UnitStateSnapshot
    {
        public string unitId;
        public string entityId;
        public string unitType;
        public float posX;
        public float posY;
        public bool isDead;
        public float currentHealth;
        public float maxHealth;

        /// <summary>See <see cref="UnitInstance.hasRuntimeHealth"/> — false means "the two above are defaults".</summary>
        public bool hasRuntimeHealth;

        public string uid;
        public string code;
        public string controllerId;

        /// <summary>The placement's authored keys — <c>npc</c>, <c>turn</c>, <c>qid</c>, <c>scr</c>, …</summary>
        public MapObjectAttributeData[] attributes;

        /// <summary>The unit's authored death drop.</summary>
        public MapObjectItemSnapshot[] items;

        /// <summary>The unit's <c>&lt;scr&gt;</c> children — <c>eve="die"</c> and <c>eve="alarm"</c>.</summary>
        public MapObjectScriptSnapshot[] scripts;

        /// <summary>
        /// <c>UnitInstance.facingDirection</c>, which is <c>±1</c> — a sign, not a yaw, so an <c>int</c>
        /// is the whole value.
        /// </summary>
        public int facingDirection;

        /// <summary>
        /// <c>UnitInstance.digger</c> — the ambush flag AS3 reads in <c>UnitZombie.setPos</c>.
        /// </summary>
        public int digger;

        /// <summary>
        /// <c>UnitInstance.ambushArmed</c> — resolved once at population, and one-way in the oracle
        /// (<c>aiState</c> never returns to <c>Buried</c>), so it must survive a save or a risen ambusher
        /// can re-bury itself by re-evaluating the same tiles.
        /// </summary>
        public bool ambushArmed;

        /// <summary>
        /// <c>UnitInstance.fraction</c> — a <c>FactionType?</c>, stored as <c>-1</c> for <c>null</c>
        /// because <c>JsonUtility</c> has no nullable-enum representation.
        /// </summary>
        public int fraction = -1;

        public static UnitStateSnapshot CreateFrom(UnitInstance unit)
        {
            if (unit == null) return null;

            return new UnitStateSnapshot
            {
                unitId = unit.unitId,
                entityId = unit.entityId ?? "",
                unitType = unit.unitType ?? "",
                posX = unit.position.x,
                posY = unit.position.y,
                isDead = unit.IsDead,
                currentHealth = unit.currentHealth,
                maxHealth = unit.maxHealth,
                hasRuntimeHealth = unit.hasRuntimeHealth,
                uid = unit.uid ?? "",
                code = unit.code ?? "",
                controllerId = unit.controllerId ?? "",
                attributes = ObjectStateSnapshot.CreateAttributes(unit.attributes),
                items = MapObjectItemSnapshot.CreateArray(unit.items),
                scripts = MapObjectScriptSnapshot.CreateArray(unit.scripts),
                facingDirection = unit.facingDirection,
                digger = unit.digger,
                ambushArmed = unit.ambushArmed,
                fraction = unit.fraction.HasValue ? (int)unit.fraction.Value : -1,
            };
        }

        public UnitInstance ToUnitInstance()
        {
            return new UnitInstance
            {
                unitId = unitId,
                entityId = entityId ?? "",
                unitType = unitType,
                position = new Vector2(posX, posY),
                isDead = isDead,
                currentHealth = currentHealth,
                maxHealth = maxHealth,
                hasRuntimeHealth = hasRuntimeHealth,
                uid = uid ?? "",
                code = code ?? "",
                controllerId = controllerId ?? "",
                attributes = ObjectStateSnapshot.RestoreAttributes(attributes),
                items = MapObjectItemSnapshot.RestoreArray(items),
                scripts = MapObjectScriptSnapshot.RestoreArray(scripts),
                facingDirection = facingDirection == 0 ? 1 : facingDirection,
                digger = digger,
                ambushArmed = ambushArmed,
                fraction = fraction < 0 ? (PFE.Data.Definitions.FactionType?)null : (PFE.Data.Definitions.FactionType)fraction,
            };
        }
    }

    /// <summary>
    /// Lightweight override for a unit identified by its stable EntityId.
    /// </summary>
    [Serializable]
    public class UnitOverrideSnapshot
    {
        public string entityId;
        public bool isDead;
        public float health;
        public Vector2 position;
    }

    /// <summary>
    /// Snapshot of object state (item, container, etc.).
    /// </summary>
    [Serializable]
    public class ObjectStateSnapshot
    {
        public string objectId;
        public string entityId;
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
                entityId = obj.entityId ?? "",
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
                entityId = entityId ?? "",
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
        public string n;
        public string opt1;
        public string opt2;
        public string t;

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
                    val = action.val,
                    n = action.n,
                    opt1 = action.opt1,
                    opt2 = action.opt2,
                    t = action.t
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
                    val = action.val,
                    n = action.n,
                    opt1 = action.opt1,
                    opt2 = action.opt2,
                    t = action.t
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

        // Security state — see MapObjectRuntimeStateData's block. Saved because the profile is
        // randomised at build time (Interact.as:505-516) and a reload that re-rolled it would hand the
        // player a different lock than the one they were working.
        public bool hasSecurityState;
        public int securityLock;
        public int securitySaveLock;
        public int securityLockLevel;
        public int securityLockTip;
        public float securityLockHp;
        public float securityLockAtt;
        public int securityMine;
        public int securityMineTip;

        /// <summary>The trap's blast, rolled once at construction (<c>Interact.as:546</c>). Saved for the
        /// same reason the lock is: re-deriving it would change what the same trap does after a reload.</summary>
        public float securityTrapDamage;
        public bool securityHackable;
        public int securityAllDif;

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
                hasSecurityState = state.hasSecurityState,
                securityLock = state.securityLock,
                securitySaveLock = state.securitySaveLock,
                securityLockLevel = state.securityLockLevel,
                securityLockTip = state.securityLockTip,
                securityLockHp = state.securityLockHp,
                securityLockAtt = state.securityLockAtt,
                securityMine = state.securityMine,
                securityMineTip = state.securityMineTip,
                securityTrapDamage = state.securityTrapDamage,
                securityHackable = state.securityHackable,
                securityAllDif = state.securityAllDif,
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
                hasSecurityState = hasSecurityState,
                securityLock = securityLock,
                securitySaveLock = securitySaveLock,
                securityLockLevel = securityLockLevel,
                securityLockTip = securityLockTip,
                securityLockHp = securityLockHp,
                securityLockAtt = securityLockAtt,
                securityMine = securityMine,
                securityMineTip = securityMineTip,
                securityTrapDamage = securityTrapDamage,
                securityHackable = securityHackable,
                securityAllDif = securityAllDif,
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
        public int throwGraceTicks;
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
                throwGraceTicks = state.throwGraceTicks,
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
                throwGraceTicks = throwGraceTicks,
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

        /// <summary>AS3 <c>Location.kolEnSpawn</c> — see <see cref="RoomDifficulty.spawnEnemyCount"/>.</summary>
        public int spawnEnemyCount;

        /// <summary>AS3 <c>Location.tipEnemy</c> — see <see cref="RoomDifficulty.tipEnemy"/>.</summary>
        public int tipEnemy = -1;

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
                hiddenEnemyCount = difficulty.hiddenEnemyCount,
                spawnEnemyCount = difficulty.spawnEnemyCount,
                tipEnemy = difficulty.tipEnemy
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

            // A save written before `enemyCounts` existed deserialises the field to null (Unity leaves
            // it unset rather than running the field initialiser). Cloning null threw inside RestoreTo,
            // i.e. loading an old save crashed the room rebuild. Keep the live array in that case.
            if (enemyCounts != null)
            {
                difficulty.enemyCounts = (int[])enemyCounts.Clone();
            }

            difficulty.hiddenEnemyCount = hiddenEnemyCount;
            difficulty.spawnEnemyCount = spawnEnemyCount;
            difficulty.tipEnemy = tipEnemy;
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
        public float waterRadiation = 1f;
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
                waterRadiation = environment.waterRadiation,
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
            environment.waterRadiation = waterRadiation;
            environment.darkness = darkness;
        }
    }
}
