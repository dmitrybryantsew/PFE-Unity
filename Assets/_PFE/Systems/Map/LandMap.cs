using System;
using System.Collections.Generic;
using PFE.Systems.Audio;
using UnityEngine;

namespace PFE.Systems.Map
{
    /// <summary>
    /// Land map - 3D world container for rooms.
    /// From AS3: Land class - manages room grid and streaming.
    /// </summary>
    public class LandMap
    {
        private readonly IMusicService _music;

        public LandMap(IMusicService music = null)
        {
            _music = music;
        }

        // Room storage
        private Dictionary<Vector3Int, RoomInstance> rooms = new Dictionary<Vector3Int, RoomInstance>();

        // Current state
        public RoomInstance currentRoom { get; private set; }
        public RoomInstance previousRoom { get; private set; }
        private Vector3Int currentCoord;

        // Bounds
        public Vector3Int minBounds;
        public Vector3Int maxBounds;

        // Special rooms storage (probation, boss rooms, etc.)
        private Dictionary<string, Dictionary<Vector3Int, RoomInstance>> specialRooms = new Dictionary<string, Dictionary<Vector3Int, RoomInstance>>();

        /// <summary>
        /// Initialize land map.
        /// </summary>
        public void Initialize(Vector3Int min, Vector3Int max)
        {
            minBounds = min;
            maxBounds = max;

            // Clear existing
            rooms.Clear();
            specialRooms.Clear();
            currentRoom = null;
            previousRoom = null;
        }

        /// <summary>
        /// Add a room to the map.
        /// </summary>
        public void AddRoom(RoomInstance room, Vector3Int position)
        {
            if (room == null)
            {
                Debug.LogError($"Attempted to add null room at position {position}");
                return;
            }

            room.landPosition = position;
            rooms[position] = room;
        }

        /// <summary>
        /// Get room at position.
        /// </summary>
        public RoomInstance GetRoom(Vector3Int position)
        {
            if (rooms.ContainsKey(position))
            {
                return rooms[position];
            }
            return null;
        }

        /// <summary>
        /// Check if room exists at position.
        /// </summary>
        public bool HasRoom(Vector3Int position)
        {
            return rooms.ContainsKey(position);
        }

        /// <summary>
        /// Switch to room at position.
        /// From AS3: Land.ativateLoc()
        /// </summary>
        public bool SwitchRoom(Vector3Int position, Vector2? spawnPos = null)
        {
            RoomInstance targetRoom = GetRoom(position);
            if (targetRoom == null)
            {
                Debug.LogError($"No room at position {position}");
                return false;
            }

            // Deactivate current room
            if (currentRoom != null)
            {
                previousRoom = currentRoom;
                currentRoom.Deactivate();
            }

            // Activate new room
            currentRoom = targetRoom;
            currentCoord = position;
            currentRoom.Activate();

            // Trigger ambient music for this room
            string track = currentRoom.environment.musicTrack;
            if (!string.IsNullOrEmpty(track))
                _music?.PlayAmbient(track);

            return true;
        }

        /// <summary>
        /// Get current room position.
        /// </summary>
        public Vector3Int GetCurrentPosition()
        {
            return currentCoord;
        }

        /// <summary>
        /// Get all rooms.
        /// </summary>
        public IEnumerable<RoomInstance> GetAllRooms()
        {
            return rooms.Values;
        }

        /// <summary>
        /// Get room count.
        /// </summary>
        public int GetRoomCount()
        {
            return rooms.Count;
        }

        /// <summary>
        /// Update active room.
        /// From AS3: Land.step()
        /// </summary>
        public void Update()
        {
            if (currentRoom != null)
            {
                currentRoom.Update();
            }

            // Optionally update previous room (limited updates)
            if (previousRoom != null && previousRoom != currentRoom)
            {
                previousRoom.UpdateLimited();
            }
        }

        /// <summary>
        /// Add a special room (probation, boss, etc.).
        /// From AS3: Land.buildProb()
        /// </summary>
        public void AddSpecialRoom(string probId, RoomInstance room, Vector3Int position)
        {
            if (!specialRooms.ContainsKey(probId))
            {
                specialRooms[probId] = new Dictionary<Vector3Int, RoomInstance>();
            }

            room.landPosition = position;
            specialRooms[probId][position] = room;
        }

        /// <summary>
        /// Get special room.
        /// </summary>
        public RoomInstance GetSpecialRoom(string probId, Vector3Int position)
        {
            if (specialRooms.ContainsKey(probId) && specialRooms[probId].ContainsKey(position))
            {
                return specialRooms[probId][position];
            }
            return null;
        }

        /// <summary>
        /// Check if position is within bounds.
        /// </summary>
        public bool IsInBounds(Vector3Int position)
        {
            return position.x >= minBounds.x && position.x < maxBounds.x &&
                   position.y >= minBounds.y && position.y < maxBounds.y &&
                   position.z >= minBounds.z && position.z < maxBounds.z;
        }

        /// <summary>
        /// Get adjacent room positions.
        /// </summary>
        public List<Vector3Int> GetAdjacentPositions(Vector3Int position)
        {
            List<Vector3Int> adjacent = new List<Vector3Int>();

            Vector3Int[] directions = new Vector3Int[]
            {
                new Vector3Int(1, 0, 0),   // Right
                new Vector3Int(-1, 0, 0),  // Left
                new Vector3Int(0, 1, 0),   // Down
                new Vector3Int(0, -1, 0),  // Up
            };

            foreach (var dir in directions)
            {
                Vector3Int adjacentPos = position + dir;
                if (IsInBounds(adjacentPos))
                {
                    adjacent.Add(adjacentPos);
                }
            }

            return adjacent;
        }

        /// <summary>
        /// Find path between two positions (simple BFS).
        /// </summary>
        public List<Vector3Int> FindPath(Vector3Int start, Vector3Int end)
        {
            if (!HasRoom(start) || !HasRoom(end))
            {
                return null;
            }

            Queue<Vector3Int> queue = new Queue<Vector3Int>();
            Dictionary<Vector3Int, Vector3Int> cameFrom = new Dictionary<Vector3Int, Vector3Int>();

            queue.Enqueue(start);
            cameFrom[start] = start;

            while (queue.Count > 0)
            {
                Vector3Int current = queue.Dequeue();

                if (current == end)
                {
                    // Reconstruct path
                    List<Vector3Int> path = new List<Vector3Int>();
                    while (current != start)
                    {
                        path.Add(current);
                        current = cameFrom[current];
                    }
                    path.Add(start);
                    path.Reverse();
                    return path;
                }

                foreach (var neighbor in GetAdjacentPositions(current))
                {
                    if (!HasRoom(neighbor) || cameFrom.ContainsKey(neighbor))
                    {
                        continue;
                    }

                    cameFrom[neighbor] = current;
                    queue.Enqueue(neighbor);
                }
            }

            return null; // No path found
        }

        /// <summary>
        /// Save all room states.
        /// </summary>
        public void SaveState()
        {
            foreach (var kvp in rooms)
            {
                kvp.Value.SaveState();
            }

            foreach (var probKvp in specialRooms)
            {
                foreach (var roomKvp in probKvp.Value)
                {
                    roomKvp.Value.SaveState();
                }
            }
        }

        /// <summary>
        /// Load room states.
        /// </summary>
        public void LoadState()
        {
            foreach (var kvp in rooms)
            {
                kvp.Value.LoadState();
            }

            foreach (var probKvp in specialRooms)
            {
                foreach (var roomKvp in probKvp.Value)
                {
                    roomKvp.Value.LoadState();
                }
            }
        }

        /// <summary>
        /// Clear all rooms.
        /// </summary>
        public void Clear()
        {
            rooms.Clear();
            specialRooms.Clear();
            currentRoom = null;
            previousRoom = null;
        }
    }
}
