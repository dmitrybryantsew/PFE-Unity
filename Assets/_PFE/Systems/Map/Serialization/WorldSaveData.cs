using System;
using System.Collections.Generic;
using PFE.Data;
using UnityEngine;

namespace PFE.Systems.Map.Serialization
{
    /// <summary>
    /// Root save data structure for the entire world.
    /// Contains all room snapshots, player state, and world metadata.
    /// </summary>
    [Serializable]
    public class WorldSaveData
    {
        // Save metadata
        public string saveId;
        public long timestamp;  // Unix timestamp
        public string saveVersion = "1.0";
        public int gameVersion;

        // World bounds
        public int minX;
        public int minY;
        public int minZ;
        public int maxX;
        public int maxY;
        public int maxZ;

        // Current room
        public int currentRoomX;
        public int currentRoomY;
        public int currentRoomZ;

        // All rooms in the main grid
        public RoomStateSnapshot[] rooms;

        // Special rooms (probation, boss, etc.)
        public SpecialRoomEntry[] specialRooms;

        // Player state
        public PlayerStateSnapshot player;

        // World metadata
        public string worldSeed;
        public bool isRandomWorld;
        public int playTime;  // Seconds

        // Mod tracking - records which mods were active when this save was created
        public ModSaveMetadata modMetadata;

        /// <summary>
        /// Create save data from a LandMap.
        /// </summary>
        public static WorldSaveData CreateFromMap(LandMap landMap, PlayerStateSnapshot playerState)
        {
            if (landMap == null)
            {
                Debug.LogError("Cannot create save data from null map");
                return null;
            }

            var saveData = new WorldSaveData
            {
                saveId = Guid.NewGuid().ToString(),
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                gameVersion = 1,  // TODO: Get from game config
                minX = landMap.minBounds.x,
                minY = landMap.minBounds.y,
                minZ = landMap.minBounds.z,
                maxX = landMap.maxBounds.x,
                maxY = landMap.maxBounds.y,
                maxZ = landMap.maxBounds.z,
                currentRoomX = landMap.GetCurrentPosition().x,
                currentRoomY = landMap.GetCurrentPosition().y,
                currentRoomZ = landMap.GetCurrentPosition().z,
                player = playerState,
                isRandomWorld = true,  // TODO: Get from world builder
                worldSeed = "",  // TODO: Get from world builder
                playTime = 0  // TODO: Track play time
            };

            // Serialize all rooms
            var allRooms = new List<RoomStateSnapshot>();
            foreach (var room in landMap.GetAllRooms())
            {
                var snapshot = RoomStateSnapshot.CreateFromRoom(room);
                if (snapshot != null)
                {
                    allRooms.Add(snapshot);
                }
            }
            saveData.rooms = allRooms.ToArray();

            // TODO: Serialize special rooms when LandMap supports accessing them

            return saveData;
        }

        /// <summary>
        /// Restore a LandMap from this save data.
        /// Note: This requires an initialized LandMap and will populate it with saved data.
        /// </summary>
        public void RestoreToMap(LandMap landMap)
        {
            if (landMap == null)
            {
                Debug.LogError("Cannot restore save data to null map");
                return;
            }

            // Set bounds
            landMap.Initialize(
                new Vector3Int(minX, minY, minZ),
                new Vector3Int(maxX, maxY, maxZ)
            );

            // Restore all rooms
            if (rooms != null)
            {
                foreach (var roomSnapshot in rooms)
                {
                    if (roomSnapshot != null)
                    {
                        // Create or update room at position
                        RoomInstance room = landMap.GetRoom(roomSnapshot.landPosition);
                        if (room == null)
                        {
                            room = new RoomInstance
                            {
                                id = roomSnapshot.roomId,
                                templateId = roomSnapshot.templateId,
                                landPosition = roomSnapshot.landPosition
                            };
                            room.InitializeTiles();
                            landMap.AddRoom(room, roomSnapshot.landPosition);
                        }

                        roomSnapshot.RestoreToRoom(room);
                    }
                }
            }

            // Restore current room
            Vector3Int currentPos = new Vector3Int(currentRoomX, currentRoomY, currentRoomZ);
            landMap.SwitchRoom(currentPos);

            // TODO: Restore special rooms
        }

        /// <summary>
        /// Get save file name for this save data.
        /// </summary>
        public string GetSaveFileName()
        {
            return $"save_{saveId}.json";
        }

        /// <summary>
        /// Get display name for this save.
        /// </summary>
        public string GetDisplayName()
        {
            DateTime saveTime = DateTimeOffset.FromUnixTimeSeconds(timestamp).LocalDateTime;
            return $"Save {saveId.Substring(0, 8)} - {saveTime:yyyy-MM-dd HH:mm}";
        }
    }

    /// <summary>
    /// Entry for special rooms (probation, boss, etc.).
    /// </summary>
    [Serializable]
    public class SpecialRoomEntry
    {
        public string probId;
        public RoomStateSnapshot room;
    }

    /// <summary>
    /// Player state snapshot.
    /// </summary>
    [Serializable]
    public class PlayerStateSnapshot
    {
        // Position
        public float posX;
        public float posY;
        public int roomX;
        public int roomY;
        public int roomZ;

        // State
        public float health;
        public float maxHealth;
        public int level;
        public float experience;

        // Equipment and inventory would be serialized separately
        // through their respective systems

        /// <summary>
        /// Create snapshot from player controller.
        /// TODO: Implement when player controller exists.
        /// </summary>
        public static PlayerStateSnapshot CreateFromPlayer()
        {
            return new PlayerStateSnapshot
            {
                posX = 0,
                posY = 0,
                roomX = 0,
                roomY = 0,
                roomZ = 0,
                health = 100,
                maxHealth = 100,
                level = 1,
                experience = 0
            };
        }

        /// <summary>
        /// Get position as Vector2.
        /// </summary>
        public Vector2 GetPosition()
        {
            return new Vector2(posX, posY);
        }

        /// <summary>
        /// Get room position as Vector3Int.
        /// </summary>
        public Vector3Int GetRoomPosition()
        {
            return new Vector3Int(roomX, roomY, roomZ);
        }
    }
}
