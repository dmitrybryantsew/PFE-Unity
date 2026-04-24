using System;
using System.Collections.Generic;
using UnityEngine;
using PFE.Core;
using PFE.Data;

namespace PFE.Systems.Map
{
    /// <summary>
    /// World builder - procedural world generation.
    /// From AS3: Land.buildRandomLand(), Land.buildSpecifLand()
    /// </summary>
    public class WorldBuilder
    {
        private LandMap landMap;
        private RoomGenerator roomGenerator;
        private List<RoomTemplate> allTemplates;
        private PfeDebugSettings _debugSettings;

        // World configuration
        private Vector3Int minBounds;
        private Vector3Int maxBounds;
        private int landStage;
        private bool hasRoofTemplates;
        private bool loggedMissingRoofWarning;

        // Starting position
        private Vector3Int startPosition;

        /// <summary>
        /// Initialize world builder.
        /// </summary>
        public void Initialize(LandMap map, RoomGenerator generator, List<RoomTemplate> templates, PfeDebugSettings debugSettings = null)
        {
            landMap = map;
            roomGenerator = generator;
            allTemplates = templates;
            _debugSettings = debugSettings;

            // Set default bounds (4x6x1 world like AS3)
            minBounds = new Vector3Int(0, 0, 0);
            maxBounds = new Vector3Int(4, 6, 1);
            startPosition = new Vector3Int(0, 0, 0);
            landStage = 1;
            hasRoofTemplates = false;
            loggedMissingRoofWarning = false;
        }

        /// <summary>
        /// Build random world (procedural generation).
        /// From AS3: Land.buildRandomLand()
        /// </summary>
        public bool BuildRandomWorld(int stage = 1, Vector3Int? customMin = null, Vector3Int? customMax = null)
        {
            if (allTemplates == null || allTemplates.Count == 0)
            {
                Debug.LogError("[WorldBuilder] No room templates available");
                return false;
            }

            // Apply custom bounds if provided
            minBounds = customMin ?? minBounds;
            maxBounds = customMax ?? maxBounds;
            landStage = stage;
            loggedMissingRoofWarning = false;
            hasRoofTemplates = HasTemplateType("roof");

            // Initialize land map
            landMap.Initialize(minBounds, maxBounds);

            // Reset room generator usage counts
            roomGenerator.ResetUsageCounts();

            MapGenerationDiagnostics.LogTemplateSummary(
                allTemplates,
                stage,
                minBounds,
                maxBounds,
                roomGenerator != null && roomGenerator.IsPrototypeMode,
                _debugSettings);

            // Generate rooms for each position
            for (int x = minBounds.x; x < maxBounds.x; x++)
            {
                for (int y = minBounds.y; y < maxBounds.y; y++)
                {
                    Vector3Int position = new Vector3Int(x, y, 0);

                    // Determine room type based on position
                    RoomTemplate template = SelectRoomForPosition(position, stage);

                    if (template == null)
                    {
                        Debug.LogWarning($"[WorldBuilder] Could not select room for position {position}");
                        continue;
                    }

                    // Generate room
                    RoomInstance room = roomGenerator.GenerateRoom(template, position);
                    landMap.AddRoom(room, position);

                    // Create background room if specified
                    if (!string.IsNullOrEmpty(template.backgroundRoomId))
                    {
                        RoomTemplate backTemplate = FindTemplateById(template.backgroundRoomId);
                        if (backTemplate != null)
                        {
                            RoomInstance backRoom = roomGenerator.GenerateRoom(backTemplate, position);
                            backRoom.roomType = "back";
                            landMap.AddSpecialRoom("background", backRoom, position);
                            room.backgroundRoom = backRoom;
                            room.hasBackgroundLayer = true;
                        }
                        else
                        {
                            Debug.LogWarning($"[WorldBuilder] Background room template '{template.backgroundRoomId}' was not found for room template '{template.id}'.");
                        }
                    }
                }
                
            }

            // Build door connections
            BuildDoorConnections();
            RoomSetup.FinalizeAllRooms(landMap, allTemplates);
            // Activate starting room
            if (landMap.HasRoom(startPosition))
            {
                landMap.SwitchRoom(startPosition);
            }
            else
            {
                Debug.LogError($"[WorldBuilder] Starting room at {startPosition} was not created");
                return false;
            }

            if (_debugSettings?.LogWorldBuilderSummary != false)
                Debug.Log($"[WorldBuilder] Built random world: {landMap.GetRoomCount()} rooms, bounds {minBounds} to {maxBounds}");
            return true;
        }

        /// <summary>
        /// Build specific world (hand-crafted level).
        /// From AS3: Land.buildSpecifLand()
        /// </summary>
        public bool BuildSpecificWorld(List<RoomTemplate> levelTemplates)
        {
            if (levelTemplates == null || levelTemplates.Count == 0)
            {
                Debug.LogError("[WorldBuilder] No level templates provided");
                return false;
            }

            // Calculate bounds from template positions
            Vector3Int calcMin = new Vector3Int(int.MaxValue, int.MaxValue, int.MaxValue);
            Vector3Int calcMax = new Vector3Int(int.MinValue, int.MinValue, int.MinValue);

            foreach (var template in levelTemplates)
            {
                if (template.fixedPosition.x >= 0)
                {
                    calcMin.x = Mathf.Min(calcMin.x, template.fixedPosition.x);
                    calcMin.y = Mathf.Min(calcMin.y, template.fixedPosition.y);
                    calcMin.z = Mathf.Min(calcMin.z, template.fixedPosition.z);

                    calcMax.x = Mathf.Max(calcMax.x, template.fixedPosition.x + 1);
                    calcMax.y = Mathf.Max(calcMax.y, template.fixedPosition.y + 1);
                    calcMax.z = Mathf.Max(calcMax.z, template.fixedPosition.z + 1);
                }
            }

            minBounds = calcMin;
            maxBounds = calcMax;

            // Initialize land map
            landMap.Initialize(minBounds, maxBounds);

            // Place rooms at fixed positions
            foreach (var template in levelTemplates)
            {
                if (template.fixedPosition.x >= 0)
                {
                    RoomInstance room = roomGenerator.GenerateRoom(template, template.fixedPosition);
                    landMap.AddRoom(room, template.fixedPosition);

                    // Create background room if specified
                    if (!string.IsNullOrEmpty(template.backgroundRoomId))
                    {
                        RoomTemplate backTemplate = FindTemplateById(template.backgroundRoomId);
                        if (backTemplate != null)
                        {
                            RoomInstance backRoom = roomGenerator.GenerateRoom(backTemplate, template.fixedPosition);
                            backRoom.roomType = "back";
                            Vector3Int backPos = new Vector3Int(template.fixedPosition.x, template.fixedPosition.y, template.fixedPosition.z + 1);
                            landMap.AddRoom(backRoom, backPos);
                            room.backgroundRoom = backRoom;
                            room.hasBackgroundLayer = true;
                        }
                        else
                        {
                            Debug.LogWarning($"[WorldBuilder] Background room template '{template.backgroundRoomId}' was not found for room template '{template.id}'.");
                        }
                    }

                    // Track starting position
                    if (template.type == "beg0" || template.type == "beg1")
                    {
                        startPosition = template.fixedPosition;
                    }
                }
            }

            // Build door connections
            BuildDoorConnections();
            RoomSetup.FinalizeAllRooms(landMap, allTemplates);
            // Activate starting room
            if (landMap.HasRoom(startPosition))
            {
                landMap.SwitchRoom(startPosition);
            }

            if (_debugSettings?.LogWorldBuilderSummary != false)
                Debug.Log($"[WorldBuilder] Built specific world: {landMap.GetRoomCount()} rooms");
            return true;
        }

        /// <summary>
        /// Select room template for a position.
        /// From AS3: Land.newRandomLoc(), Land.newTipLoc()
        /// </summary>
        private RoomTemplate SelectRoomForPosition(Vector3Int position, int stage)
        {
            // Get adjacent rooms to avoid repetition
            List<RoomTemplate> exclude = new List<RoomTemplate>();
            RoomInstance leftRoom = landMap.GetRoom(new Vector3Int(position.x - 1, position.y, position.z));
            RoomInstance upRoom = landMap.GetRoom(new Vector3Int(position.x, position.y - 1, position.z));

            if (leftRoom != null)
            {
                RoomTemplate leftTemplate = FindTemplateById(leftRoom.templateId);
                if (leftTemplate != null) exclude.Add(leftTemplate);
            }

            if (upRoom != null)
            {
                RoomTemplate upTemplate = FindTemplateById(upRoom.templateId);
                if (upTemplate != null) exclude.Add(upTemplate);
            }

            // Check if this is the starting position
            if (position == startPosition)
            {
                return roomGenerator.SelectRoomByType("beg0");
            }

            // Check if this is top row (rooftop rooms)
            if (position.y == maxBounds.y - 1)
            {
                if (hasRoofTemplates)
                {
                    RoomTemplate roofRoom = roomGenerator.SelectRoomByType("roof", exclude);
                    if (roofRoom != null) return roofRoom;
                }
                else if (!loggedMissingRoofWarning)
                {
                    loggedMissingRoofWarning = true;
                    Debug.LogWarning("[WorldBuilder] No 'roof' templates available. Top row will use random fallback rooms.");
                }
            }

            // Select random room with exclusion
            return roomGenerator.SelectRandomRoom(stage, null, exclude);
        }

        /// <summary>
        /// Build door connections between adjacent rooms.
        /// From AS3: Land door connection system (lines 418-494)
        /// </summary>
        private void BuildDoorConnections()
        {
            int attemptedConnections = 0;
            int connectedConnections = 0;
            int noMatchConnections = 0;
            int fallbackConnections = 0;

            // Clear existing door connections
            foreach (var room in landMap.GetAllRooms())
            {
                foreach (var door in room.doors)
                {
                    door.isActive = false;
                }
            }

            // Build connections for each room
            for (int x = minBounds.x; x < maxBounds.x; x++)
            {
                for (int y = minBounds.y; y < maxBounds.y; y++)
                {
                    Vector3Int pos = new Vector3Int(x, y, 0);
                    RoomInstance room1 = landMap.GetRoom(pos);

                    if (room1 == null) continue;

                    // Check right neighbor
                    if (x < maxBounds.x - 1)
                    {
                        RoomInstance room2 = landMap.GetRoom(new Vector3Int(x + 1, y, 0));
                        if (room2 != null)
                        {
                            attemptedConnections++;
                            if (BuildConnection(room1, room2, DoorSide.Right, pos, new Vector3Int(x + 1, y, 0), out bool usedFallback))
                            {
                                connectedConnections++;
                                if (usedFallback)
                                {
                                    fallbackConnections++;
                                }
                            }
                            else
                            {
                                noMatchConnections++;
                            }
                        }
                    }

                    // Check bottom neighbor
                    if (y < maxBounds.y - 1)
                    {
                        RoomInstance room2 = landMap.GetRoom(new Vector3Int(x, y + 1, 0));
                        if (room2 != null)
                        {
                            attemptedConnections++;
                            if (BuildConnection(room1, room2, DoorSide.Bottom, pos, new Vector3Int(x, y + 1, 0), out bool usedFallback))
                            {
                                connectedConnections++;
                                if (usedFallback)
                                {
                                    fallbackConnections++;
                                }
                            }
                            else
                            {
                                noMatchConnections++;
                            }
                        }
                    }
                }
            }

            if (_debugSettings?.LogWorldBuilderSummary != false)
                Debug.Log(
                    $"[WorldBuilder] Door connection summary: attempted={attemptedConnections}, connected={connectedConnections}, " +
                    $"fallback={fallbackConnections}, unmatched={noMatchConnections}");
        }

        /// <summary>
        /// Build door connection between two adjacent rooms.
        /// </summary>
        private bool BuildConnection(RoomInstance room1, RoomInstance room2, DoorSide side, Vector3Int pos1, Vector3Int pos2, out bool usedFallback)
        {
            usedFallback = false;

            // Get available doors from both rooms
            List<DoorConnection> possibleConnections = FindMatchingDoors(room1, room2, side);

            if (possibleConnections.Count == 0)
            {
                if (roomGenerator != null && roomGenerator.IsPrototypeMode)
                {
                    if (TryBuildPrototypeFallbackConnection(room1, room2, side, pos1, pos2))
                    {
                        usedFallback = true;
                        return true;
                    }
                }

                return false;
            }

            // Determine number of doors to activate
            // From AS3: 2-3 horizontal, 1 vertical
            int doorCount = (side == DoorSide.Right) ? UnityEngine.Random.Range(2, 4) : 1;

            // Activate random doors
            for (int i = 0; i < doorCount && possibleConnections.Count > 0; i++)
            {
                int idx = UnityEngine.Random.Range(0, possibleConnections.Count);
                DoorConnection connection = possibleConnections[idx];
                possibleConnections.RemoveAt(idx);

                // Activate door in both rooms
                DoorInstance door1 = GetDoorByIndex(room1, connection.door1Index);
                DoorInstance door2 = GetDoorByIndex(room2, connection.door2Index);

                if (door1 != null && door2 != null)
                {
                    door1.isActive = true;
                    door2.isActive = true;

                    // Set connection data
                    door1.targetRoomPosition = pos2;
                    door1.targetDoorIndex = connection.door2Index;

                    door2.targetRoomPosition = pos1;
                    door2.targetDoorIndex = connection.door1Index;

                    door1.quality = connection.quality;
                    door2.quality = connection.quality;
                }
            }

            return true;
        }

        /// <summary>
        /// Find matching doors between two adjacent rooms.
        /// </summary>
        private List<DoorConnection> FindMatchingDoors(RoomInstance room1, RoomInstance room2, DoorSide side)
        {
            List<DoorConnection> connections = new List<DoorConnection>();

            int start1, end1, start2;

            // Determine door index ranges based on side
            if (side == DoorSide.Right)
            {
                // Right side of room1 (0-5) with left side of room2 (12-17)
                start1 = 0; end1 = 5;
                start2 = 12;
            }
            else // Bottom
            {
                // Bottom of room1 (6-11) with top of room2 (18-23)
                start1 = 6; end1 = 11;
                start2 = 18;
            }

            // Find matching doors
            for (int i = start1; i <= end1; i++)
            {
                DoorInstance door1 = GetDoorByIndex(room1, i);
                if (door1 == null || door1.quality == DoorQuality.None) continue;

                // Calculate corresponding door index in room2
                int offset = i - start1;
                int j = start2 + offset;

                DoorInstance door2 = GetDoorByIndex(room2, j);
                if (door2 == null || door2.quality == DoorQuality.None) continue;

                // Use minimum quality of both doors
                DoorQuality quality = (DoorQuality)Mathf.Min((int)door1.quality, (int)door2.quality);

                // Only connect if quality is at least Narrow
                if (quality >= DoorQuality.Narrow)
                {
                    connections.Add(new DoorConnection
                    {
                        door1Index = i,
                        door2Index = j,
                        quality = quality
                    });
                }
            }

            return connections;
        }

        /// <summary>
        /// Get door by index from room.
        /// </summary>
        private DoorInstance GetDoorByIndex(RoomInstance room, int index)
        {
            foreach (var door in room.doors)
            {
                if (door.doorIndex == index)
                    return door;
            }
            return null;
        }

        /// <summary>
        /// Find template by ID.
        /// </summary>
        private RoomTemplate FindTemplateById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;

            foreach (var template in allTemplates)
            {
                if (template.id == id)
                    return template;
            }
            return null;
        }

        private bool TryBuildPrototypeFallbackConnection(
            RoomInstance room1,
            RoomInstance room2,
            DoorSide side,
            Vector3Int pos1,
            Vector3Int pos2)
        {
            int index1;
            int index2;

            if (side == DoorSide.Right)
            {
                index1 = 2;   // Right side middle slot.
                index2 = 14;  // Left side mirrored middle slot.
            }
            else
            {
                index1 = 8;   // Bottom side middle slot.
                index2 = 20;  // Top side mirrored middle slot.
            }

            DoorInstance door1 = AddOrGetDoor(room1, index1);
            DoorInstance door2 = AddOrGetDoor(room2, index2);

            if (door1 == null || door2 == null)
            {
                return false;
            }

            door1.isActive = true;
            door2.isActive = true;

            door1.quality = DoorQuality.Narrow;
            door2.quality = DoorQuality.Narrow;

            door1.targetRoomPosition = pos2;
            door1.targetDoorIndex = index2;

            door2.targetRoomPosition = pos1;
            door2.targetDoorIndex = index1;

            return true;
        }

        private DoorInstance AddOrGetDoor(RoomInstance room, int index)
        {
            DoorInstance existing = GetDoorByIndex(room, index);
            if (existing != null)
            {
                return existing;
            }

            DoorInstance created = new DoorInstance
            {
                doorIndex = index,
                side = GetDoorSide(index),
                quality = DoorQuality.Narrow,
                isActive = false,
                tilePosition = GetDoorTilePosition(index)
            };

            room.doors.Add(created);
            return created;
        }

        private DoorSide GetDoorSide(int doorIndex)
        {
            if (doorIndex >= 0 && doorIndex < 6) return DoorSide.Right;
            if (doorIndex >= 6 && doorIndex < 12) return DoorSide.Bottom;
            if (doorIndex >= 12 && doorIndex < 18) return DoorSide.Left;
            return DoorSide.Top;
        }

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

        private bool HasTemplateType(string type)
        {
            if (allTemplates == null || allTemplates.Count == 0 || string.IsNullOrEmpty(type))
            {
                return false;
            }

            foreach (var template in allTemplates)
            {
                if (template != null && template.type == type)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Door connection data.
        /// </summary>
        private struct DoorConnection
        {
            public int door1Index;
            public int door2Index;
            public DoorQuality quality;
        }
    }
}
