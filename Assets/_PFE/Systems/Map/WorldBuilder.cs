using System;
using System.Collections.Generic;
using UnityEngine;
using PFE.Core;
using PFE.Data;
using PFE.Data.Definitions.Campaign;
using PFE.Systems.Map.Generation;

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
        private PFE.Core.Rng.IRngService _rng;

        /// <summary>
        /// Prob rooms already built this run, by id — AS3 <c>this.probs[id] != null</c>
        /// (<c>Land.as:821</c>). Reset per land build, because AS3's <c>probs</c> map is per
        /// <c>Land</c> instance and a land is rebuilt on every descent.
        /// </summary>
        private readonly HashSet<string> _builtProbRooms = new HashSet<string>(StringComparer.Ordinal);

        private bool _loggedMissingProbContext;

        /// <summary>
        /// Supplies the detached prob-room collection and the run's completion record — the two things
        /// <c>Land.newRandomProb</c> needs that a land does not carry. See <see cref="IProbDoorContext"/>.
        ///
        /// <para><b>Left null by default, and that is the safe direction.</b> With no context no prob door
        /// is placed, which is exactly the behaviour before this existed. A caller that has both the
        /// <c>prob</c> land's rooms and the trigger store sets it; the alternative — a door placed with no
        /// room behind it — is an interactive object that visibly does nothing.</para>
        /// </summary>
        public IProbDoorContext ProbContext { get; set; }

        [VContainer.Inject]
        public WorldBuilder(PFE.Core.Rng.IRngService rng = null)
        {
            _rng = rng != null ? rng.GetStream(PFE.Core.Rng.RngStream.Spawn) : new PFE.Core.Rng.PcgRngService().GetStream(PFE.Core.Rng.RngStream.Spawn);
        }

        /// <summary>
        /// Initialize world builder.
        /// </summary>
        public void Initialize(LandMap map, RoomGenerator generator, List<RoomTemplate> templates, PfeDebugSettings debugSettings = null, PFE.Core.Rng.IRngService rng = null)
        {
            landMap = map;
            roomGenerator = generator;
            allTemplates = templates;
            _debugSettings = debugSettings;
            if (rng != null) _rng = rng.GetStream(PFE.Core.Rng.RngStream.Spawn);

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
            MapIntegrityDiagnostics.LogBuildPath("Random", allTemplates.Count, minBounds, maxBounds, _debugSettings);

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
                    MapIntegrityDiagnostics.LogRoomSnapshot("GeneratedRandomRoom", room, template, _debugSettings);

                    // Create background room if specified
                    if (!string.IsNullOrEmpty(template.backgroundRoomId))
                    {
                        RoomTemplate backTemplate = FindTemplateById(template.backgroundRoomId, template);
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
            RoomSetup.FinalizeAllRooms(landMap, allTemplates, _debugSettings);
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
        public bool BuildSpecificWorld(List<RoomTemplate> levelTemplates, Vector3Int? preferredStartPosition = null)
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
                if (HasFixedPosition(template))
                {
                    calcMin.x = Mathf.Min(calcMin.x, template.fixedPosition.x);
                    calcMin.y = Mathf.Min(calcMin.y, template.fixedPosition.y);
                    calcMin.z = Mathf.Min(calcMin.z, template.fixedPosition.z);

                    calcMax.x = Mathf.Max(calcMax.x, template.fixedPosition.x + 1);
                    calcMax.y = Mathf.Max(calcMax.y, template.fixedPosition.y + 1);
                    calcMax.z = Mathf.Max(calcMax.z, template.fixedPosition.z + 1);
                }
            }

            if (calcMin.x == int.MaxValue)
            {
                Debug.LogError("[WorldBuilder] No fixed-position templates provided");
                return false;
            }

            minBounds = calcMin;
            maxBounds = calcMax;

            // Initialize land map
            landMap.Initialize(minBounds, maxBounds);
            MapIntegrityDiagnostics.LogBuildPath("Specific", levelTemplates.Count, minBounds, maxBounds, _debugSettings);

            // Place rooms at fixed positions
            foreach (var template in levelTemplates)
            {
                if (HasFixedPosition(template))
                {
                    RoomInstance room = roomGenerator.GenerateRoom(template, template.fixedPosition);
                    landMap.AddRoom(room, template.fixedPosition);
                    MapIntegrityDiagnostics.LogRoomSnapshot("GeneratedSpecificRoom", room, template, _debugSettings);

                    // Create background room if specified
                    if (!string.IsNullOrEmpty(template.backgroundRoomId))
                    {
                        RoomTemplate backTemplate = FindTemplateById(template.backgroundRoomId, template);
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

            if (preferredStartPosition.HasValue && landMap.HasRoom(preferredStartPosition.Value))
            {
                startPosition = preferredStartPosition.Value;
            }
            else if (!landMap.HasRoom(startPosition))
            {
                foreach (var template in levelTemplates)
                {
                    if (HasFixedPosition(template))
                    {
                        startPosition = template.fixedPosition;
                        break;
                    }
                }
            }

            // Build door connections for fixed/authored rooms (NO synthetic fallback doors through solid walls, connect all authored matching doors)
            BuildDoorConnections(allowFallback: false, connectAllMatching: true);
            RoomSetup.FinalizeSpecificAllRooms(landMap, allTemplates, _debugSettings);
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
        private void BuildDoorConnections(bool allowFallback = true, bool connectAllMatching = false)
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
                            if (BuildConnection(room1, room2, DoorSide.Right, pos, new Vector3Int(x + 1, y, 0), out bool usedFallback, allowFallback, connectAllMatching))
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
                            if (BuildConnection(room1, room2, DoorSide.Bottom, pos, new Vector3Int(x, y + 1, 0), out bool usedFallback, allowFallback, connectAllMatching))
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
        private bool BuildConnection(
            RoomInstance room1,
            RoomInstance room2,
            DoorSide side,
            Vector3Int pos1,
            Vector3Int pos2,
            out bool usedFallback,
            bool allowFallback = true,
            bool connectAllMatching = false)
        {
            usedFallback = false;

            // Get available doors from both rooms
            List<DoorConnection> possibleConnections = FindMatchingDoors(room1, room2, side);

            if (possibleConnections.Count == 0)
            {
                if (allowFallback && roomGenerator != null && roomGenerator.IsPrototypeMode)
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
            int doorCount = connectAllMatching
                ? possibleConnections.Count
                : ((side == DoorSide.Right) ? _rng.Range(2, 4) : 1);

            // Activate doors
            for (int i = 0; i < doorCount && possibleConnections.Count > 0; i++)
            {
                int idx = connectAllMatching ? 0 : _rng.Range(0, possibleConnections.Count);
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
                // Right side of room1 (0-5) with left side of room2 (11-16)
                start1 = 0; end1 = 5;
                start2 = 11;
            }
            else // Bottom
            {
                // Bottom of room1 (6-10) with top of room2 (17-21)
                start1 = 6; end1 = 10;
                start2 = 17;
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
        private RoomTemplate FindTemplateById(string id, RoomTemplate context = null)
        {
            if (string.IsNullOrEmpty(id)) return null;

            foreach (var template in allTemplates)
            {
                if (template.GetContentId() == id)
                    return template;
            }

            if (context != null && !string.IsNullOrWhiteSpace(context.sourceCollectionId))
            {
                foreach (var template in allTemplates)
                {
                    if (template.id == id && template.sourceCollectionId == context.sourceCollectionId)
                        return template;
                }
            }

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
                index2 = 19;  // Top side mirrored middle slot.
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
            if (doorIndex >= 0 && doorIndex <= 5) return DoorSide.Right;
            if (doorIndex >= 6 && doorIndex <= 10) return DoorSide.Bottom;
            if (doorIndex >= 11 && doorIndex <= 16) return DoorSide.Left;
            return DoorSide.Top;
        }

        private Vector2Int GetDoorTilePosition(int doorIndex)
        {
            int width = WorldConstants.ROOM_WIDTH;   // 48
            int height = WorldConstants.ROOM_HEIGHT; // 27

            if (doorIndex >= 0 && doorIndex <= 5)
            {
                // RIGHT side: AS3 formula = index * 4 + 3
                int y = height - 1 - (doorIndex * 4 + 3);
                return new Vector2Int(width - 1, y);
            }
            else if (doorIndex >= 6 && doorIndex <= 10)
            {
                // BOTTOM side: AS3 formula = (index - 6) * 9 + 4
                int x = (doorIndex - 6) * 9 + 4;
                return new Vector2Int(x, 0);
            }
            else if (doorIndex >= 11 && doorIndex <= 16)
            {
                // LEFT side: AS3 formula = (index - 11) * 4 + 3
                int y = height - 1 - ((doorIndex - 11) * 4 + 3);
                return new Vector2Int(0, y);
            }
            else if (doorIndex >= 17 && doorIndex <= 21)
            {
                // TOP side: AS3 formula = (index - 17) * 9 + 4
                int x = (doorIndex - 17) * 9 + 4;
                return new Vector2Int(x, height - 1);
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

        private static bool HasFixedPosition(RoomTemplate template)
        {
            return template != null && template.fixedPosition.z >= 0;
        }

        // =====================================================================
        // PROCEDURAL LAND BUILD — AS3 Land.buildRandomLand (Land.as:163-682)
        // =====================================================================

        /// <summary>
        /// Build a land from its definition, taking the procedural or the authored path the way AS3 does
        /// (<c>Land.as:118</c>: the <c>rnd</c> attribute selects <c>buildRandomLand</c>, else
        /// <c>buildSpecifLand</c>).
        /// </summary>
        /// <remarks>
        /// <para><b>This is the seam that was missing.</b> Before it existed, every land — procedural or
        /// authored — went through <see cref="BuildSpecificWorld"/>, and because an AS3 procedural room
        /// carries no <c>x</c>/<c>y</c>/<c>z</c> the importer gives every procedural template
        /// <c>fixedPosition == (0,0,0)</c>. <see cref="HasFixedPosition"/> therefore accepts all of them,
        /// the bounds collapse to a single cell, and every room is stacked on (0,0,0): a one-room land with
        /// no neighbour to walk to. That is the "a random land builds nothing and its rooms never
        /// transition" symptom.</para>
        /// </remarks>
        /// <param name="land">The land. Its <c>isProcedural</c> flag picks the path.</param>
        /// <param name="templates">The land's own room collection (see <c>MapBridge.ResolveCollectionName</c>).</param>
        /// <param name="landStage">AS3 <c>LandAct.landStage</c> — runtime state, never land data.</param>
        /// <param name="visited">AS3 <c>LandAct.visited</c> — gates the <c>beg*</c> entry room.</param>
        /// <param name="forceRegenerate">AS3 <c>Game.crea</c>. The plan is rebuilt every call today, so
        /// this is carried for the wiring's sake rather than consumed.</param>
        /// <param name="mbaseVisited">AS3 <c>triggers["mbase_visited"] &gt; 0</c> (conf 4's entry room).</param>
        public bool BuildLand(LandDefinition land, List<RoomTemplate> templates, int landStage,
            bool visited, bool forceRegenerate = false, bool mbaseVisited = false)
        {
            if (land == null)
            {
                Debug.LogError("[WorldBuilder] BuildLand: null land definition");
                return false;
            }

            if (templates == null || templates.Count == 0)
            {
                Debug.LogError($"[WorldBuilder] BuildLand: land '{land.landId}' has no room templates");
                return false;
            }

            this.landStage = landStage;

            // Re-point the shared selection pool at THIS land's collection. The generator is injected as a
            // singleton, and a procedural land must never draw a room out of another land's file.
            allTemplates = templates;
            roomGenerator.Initialize(templates);

            if (!land.isProcedural)
            {
                // Authored (AS3 buildSpecifLand): x/y/z come from the room XML, so bounds and placement are
                // the templates' own. The entry cell is the land's declared locx/locy.
                var entry = new Vector3Int(land.entryCoordinates.x, land.entryCoordinates.y, 0);
                return BuildSpecificWorld(templates, entry);
            }

            LandLayoutPlan plan = LandLayoutPlanner.Plan(land, landStage, visited, _rng, mbaseVisited);
            return BuildProceduralLand(land, plan);
        }

        /// <summary>
        /// AS3 <c>Land.buildRandomLand()</c> (<c>Land.as:163-682</c>): fill the grid from the plan, carve the
        /// doors between neighbours, then finalise every room and place its exit / checkpoint.
        /// </summary>
        private bool BuildProceduralLand(LandDefinition land, LandLayoutPlan plan)
        {
            minBounds = new Vector3Int(plan.BoundsMin.x, plan.BoundsMin.y, 0);
            maxBounds = new Vector3Int(plan.BoundsMax.x, plan.BoundsMax.y, 1);

            landMap.Initialize(minBounds, maxBounds);
            roomGenerator.ResetUsageCounts();

            // AS3's `probs` map is per Land instance and a land is rebuilt on each descent, so a prob
            // room built during the previous visit is not "already built" this time.
            _builtProbRooms.Clear();
            hasRoofTemplates = HasTemplateType("roof");
            loggedMissingRoofWarning = false;
            MapIntegrityDiagnostics.LogBuildPath("Procedural", plan.CellCount, minBounds, maxBounds, _debugSettings);

            var rooms = new Dictionary<Vector2Int, RoomInstance>();
            var cells = new Dictionary<Vector2Int, LandCellPlan>();
            var templatesByCell = new Dictionary<Vector2Int, RoomTemplate>();
            var doorSlots = new Dictionary<Vector2Int, int[]>();

            // ---- Pass 1: one room per planned cell (Land.as:184-410) ----
            //
            // Column-major, exactly as AS3: `_loc4_` (x) is the OUTER loop and `_loc5_` (y) the inner one
            // (Land.as:185-190). That is not cosmetic — `newRandomLoc` excludes the LEFT (x-1) and UP (y-1)
            // neighbour, and the draws come off one shared stream, so a row-major walk would pick a
            // different room for every cell that has both neighbours.
            for (int x = 0; x < plan.GridSize.x; x++)
            {
                for (int y = 0; y < plan.GridSize.y; y++)
                {
                    if (!plan.TryGetCell(x, y, out LandCellPlan cell)) continue;

                    var key = new Vector2Int(x, y);
                    var pos = new Vector3Int(x, y, 0);

                    RoomTemplate template = SelectTemplateForCell(
                        cell, ExcludedNeighbourTemplates(templatesByCell, x, y));
                    if (template == null)
                    {
                        Debug.LogWarning($"[WorldBuilder] '{land.landId}': no room for cell ({x},{y}) " +
                                         $"fill={cell.Fill} tip='{cell.Tip}' stage={cell.SelectionStage}");
                        continue;
                    }

                    RoomInstance room = roomGenerator.GenerateRoom(template, pos);
                    landMap.AddRoom(room, pos);

                    // AS3 mirrors the door array per cell with p = 0.5 (Land.as:192), and never on a beg* cell.
                    int[] slots = DoorSlotsOf(room);
                    if (cell.Mirror) DoorMatchMath.MirrorInPlace(slots);

                    rooms[key] = room;
                    cells[key] = cell;
                    templatesByCell[key] = template;
                    doorSlots[key] = slots;
                }
            }

            if (rooms.Count == 0)
            {
                Debug.LogError($"[WorldBuilder] Procedural land '{land.landId}' produced no rooms");
                return false;
            }

            // ---- Pass 2: doors between neighbours (Land.as:411-494) ----
            int carvedSlots = 0;
            foreach (var kv in rooms)
            {
                int x = kv.Key.x;
                int y = kv.Key.y;

                if (rooms.TryGetValue(new Vector2Int(x + 1, y), out RoomInstance right))
                {
                    carvedSlots += CarveNeighbourPair(
                        kv.Value, doorSlots[kv.Key], right, doorSlots[new Vector2Int(x + 1, y)],
                        DoorWall.Right, new Vector3Int(x + 1, y, 0), new Vector3Int(x, y, 0));
                }

                if (rooms.TryGetValue(new Vector2Int(x, y + 1), out RoomInstance below))
                {
                    carvedSlots += CarveNeighbourPair(
                        kv.Value, doorSlots[kv.Key], below, doorSlots[new Vector2Int(x, y + 1)],
                        DoorWall.Bottom, new Vector3Int(x, y + 1, 0), new Vector3Int(x, y, 0));
                }
            }

            // ---- Pass 3: finalise each room, then place its own objects ----
            foreach (var kv in rooms)
            {
                LandCellPlan cell = cells[kv.Key];
                RoomInstance room = kv.Value;

                // The conf's ramka must reach ApplyBorder, or the wrong edge cells become walls and the
                // carved doors land in the wrong place (or against solid rock).
                RoomSetup.FinalizeRoom(room, templatesByCell[kv.Key],
                    borderTypeOverride: cell.Ramka > 0 ? cell.Ramka : -1,
                    debugSettings: _debugSettings);

                // AS3 sets `water` per cell (conf 2 row 1, conf 5 row 2), independent of the template.
                if (cell.Water >= 0) DoorCarver.ApplyWaterLevel(room, cell.Water);

                PlaceCellObjects(land, room, cell);
            }

            // ---- Entry (AS3 `Land.as:1180-1181`, `enterLand`: `locX = act.begLocX`, `locY = act.begLocY`) ----
            //
            // The entry cell is the land's own declared `locx`/`locy`, NOT the grid origin. Three of the
            // seven procedural lands do not start at (0,0): `random_mane` (conf 3) starts at (0,4) and
            // `random_encl` (conf 6) at (0,7) — the bottom row in AS3 space. Starting every land at (0,0)
            // drops the player into a cell the conf's own rules never meant to be an entrance (conf 3
            // puts its `passroof` shaft at x==2, conf 6 puts two exits on the top row).
            startPosition = ResolveProceduralEntry(land.entryCoordinates, plan.GridSize);
            if (startPosition.x != land.entryCoordinates.x || startPosition.y != land.entryCoordinates.y)
            {
                Debug.LogWarning($"[WorldBuilder] '{land.landId}': entry {land.entryCoordinates} is outside " +
                                 $"the {plan.GridSize.x}x{plan.GridSize.y} grid; clamped to " +
                                 $"({startPosition.x},{startPosition.y}).");
            }

            if (!landMap.HasRoom(startPosition))
            {
                Debug.LogWarning($"[WorldBuilder] '{land.landId}': entry cell {land.entryCoordinates} " +
                                 "holds no room; falling back to the first built cell.");
                foreach (var kv in rooms)
                {
                    startPosition = new Vector3Int(kv.Key.x, kv.Key.y, 0);
                    break;
                }
            }
            landMap.SwitchRoom(startPosition);

            if (_debugSettings?.LogWorldBuilderSummary != false)
            {
                Debug.Log($"[WorldBuilder] Built procedural land '{land.landId}' (conf {plan.Conf}, stage " +
                          $"{plan.LandStage}): {rooms.Count} rooms on {plan.GridSize.x}x{plan.GridSize.y}, " +
                          $"{carvedSlots} door slots carved, entry {startPosition}");
            }
            return true;
        }

        /// <summary>
        /// The cell the player lands in: AS3 <c>act.begLocX</c>/<c>act.begLocY</c>, clamped into the grid.
        ///
        /// <para><b>Why the clamp exists.</b> <c>begLocX/begLocY</c> is authored land data and the grid size
        /// is authored land data, and the two are only guaranteed to agree for the land they were written
        /// for. A <c>locx</c> past <c>mx</c> would otherwise name a cell that was never filled and the
        /// player would spawn in the void — a silent black screen rather than a build error.</para>
        ///
        /// <para><b>Pure, and public, so it is assertable offline.</b> The surrounding build needs a live
        /// <c>LandMap</c> and therefore the editor; this arithmetic does not, and the seven procedural
        /// lands disagree about where they start, so it is worth a fixture. The caller reports the clamp.</para>
        /// </summary>
        /// <param name="entry">AS3 <c>locx</c>/<c>locy</c> — the land's declared entry cell.</param>
        /// <param name="gridSize">The planned grid, after any conf-specific clamp.</param>
        public static Vector3Int ResolveProceduralEntry(Vector2Int entry, Vector2Int gridSize)
        {
            int x = Mathf.Clamp(entry.x, 0, Mathf.Max(0, gridSize.x - 1));
            int y = Mathf.Clamp(entry.y, 0, Mathf.Max(0, gridSize.y - 1));
            return new Vector3Int(x, y, 0);
        }

        /// <summary>
        /// Pick the template for one cell: AS3 <c>newTipLoc</c> when the plan names a tip, else
        /// <c>newRandomLoc</c> (which also honours a tip when the conf sets one, e.g. conf 3's <c>roof</c>).
        /// </summary>
        private RoomTemplate SelectTemplateForCell(LandCellPlan cell, List<RoomTemplate> exclude)
        {
            if (cell.Fill == CellFill.Tip && !string.IsNullOrEmpty(cell.Tip))
            {
                RoomTemplate byTip = roomGenerator.SelectRoomByType(cell.Tip, exclude);
                if (byTip != null) return byTip;

                // AS3 newTipLoc traces "нет локации <tip>" and falls back to a random room — a silent art
                // failure. Reported here rather than swallowed.
                Debug.LogWarning($"[WorldBuilder] No room of tip '{cell.Tip}'; falling back to a random room.");
            }

            string requiredType = string.IsNullOrEmpty(cell.Tip) ? null : cell.Tip;
            return roomGenerator.SelectRandomRoom(cell.SelectionStage, requiredType, exclude);
        }

        /// <summary>
        /// AS3 <c>newRandomLoc</c> excludes the LEFT (<c>x-1</c>) and UP (<c>y-1</c>) neighbour's room
        /// (<c>Land.as:897-903</c>). Both are already placed because the grid fills row-major from the top.
        /// </summary>
        private static List<RoomTemplate> ExcludedNeighbourTemplates(
            Dictionary<Vector2Int, RoomTemplate> templatesByCell, int x, int y)
        {
            var exclude = new List<RoomTemplate>(2);
            if (templatesByCell.TryGetValue(new Vector2Int(x - 1, y), out RoomTemplate left)) exclude.Add(left);
            if (templatesByCell.TryGetValue(new Vector2Int(x, y - 1), out RoomTemplate up)) exclude.Add(up);
            return exclude;
        }

        /// <summary>The room's 22 door slots as a quality array (<c>0</c> where the room declares none).</summary>
        private static int[] DoorSlotsOf(RoomInstance room)
        {
            var slots = new int[DoorMatchMath.DoorSlotCount];
            if (room.doors == null) return slots;

            foreach (DoorInstance door in room.doors)
            {
                if (door.doorIndex >= 0 && door.doorIndex < slots.Length)
                {
                    slots[door.doorIndex] = (int)door.quality;
                }
            }
            return slots;
        }

        /// <summary>
        /// AS3 pass 3 (<c>Land.as:458-494</c>): match the shared wall, then draw the carves — 3 with
        /// replacement on the right, 1 on the lower side — and activate the slots on both rooms.
        /// </summary>
        private int CarveNeighbourPair(RoomInstance roomA, int[] slotsA, RoomInstance roomB, int[] slotsB,
            DoorWall wall, Vector3Int posB, Vector3Int posA)
        {
            List<DoorMatch> matches = DoorMatchMath.Match(slotsA, slotsB, wall);
            if (matches.Count == 0) return 0;

            List<DoorMatch> carves = DoorMatchMath.SelectCarves(matches, wall, _rng);

            foreach (DoorMatch m in carves)
            {
                ActivateDoor(roomA, m.AIndex, (DoorQuality)m.Quality, posB, m.BIndex);
                ActivateDoor(roomB, m.BIndex, (DoorQuality)m.Quality, posA, m.AIndex);
            }
            return carves.Count;
        }

        /// <summary>
        /// Mark one slot active on a room, creating the <see cref="DoorInstance"/> when the mirror moved a
        /// door onto a slot the room did not originally declare.
        /// </summary>
        private void ActivateDoor(RoomInstance room, int index, DoorQuality quality,
            Vector3Int targetRoom, int targetIndex)
        {
            DoorInstance door = GetDoorByIndex(room, index);
            if (door == null)
            {
                door = new DoorInstance
                {
                    doorIndex = index,
                    side = GetDoorSide(index),
                    tilePosition = GetDoorTilePosition(index),
                };
                room.doors.Add(door);
            }

            door.isActive = true;
            door.quality = quality;
            door.targetRoomPosition = targetRoom;
            door.targetDoorIndex = targetIndex;
        }

        /// <summary>
        /// The cell's own objects: the exit box (AS3 <c>createExit</c>), the checkpoint
        /// (AS3 <c>createCheck</c>) and the trial / battle door (AS3 <c>newRandomProb</c> →
        /// <c>createDoorProb</c>).
        /// </summary>
        private void PlaceCellObjects(LandDefinition land, RoomInstance room, LandCellPlan cell)
        {
            if (cell.SuppressPlacement) return;

            if (cell.Exit != ExitKind.None)
            {
                string suffix = cell.Exit == ExitKind.Deep ? "1" : string.Empty;
                RoomPopulator.PlaceExit(room, land.exitLandId, suffix);

                // AS3 does NOT have a special "build the exit room" step, and that is the whole trap here.
                // The exit box is created with `prob = exitProb + param` (Location.as:2119), and the tail
                // of Location.createObj pushes every non-empty `prob` it sees into `land.probIds`
                // (:2080-2082) — which Land.buildProbs then builds like any other prob (Land.as:752-768).
                // So the exit room is a prob room, reached by the same machinery as a trial door, and the
                // only reason it needs naming here is that this port has no `probIds` list to push into:
                // without this call the box points at a room nobody built, and the entry refuses.
                //
                // Note the id is `exitLandId + suffix` — the *room* is `exit_plant1` for a Deep exit while
                // the box's prefix is still `exit_plant`. Both exist in the prob collection.
                BuildAndRegisterProbRoom(room, (land.exitLandId ?? string.Empty) + suffix);
            }

            if (cell.Checkpoint != CheckpointKind.None)
            {
                RoomPopulator.PlaceCheckpointMarker(room, cell.Checkpoint == CheckpointKind.Begin);
            }

            if (cell.Prob != ProbKind.None)
            {
                PlaceProbDoor(land, room, cell);
            }
        }

        /// <summary>
        /// AS3 <c>Land.newRandomProb(loc, maxlevel, imp)</c> (<c>Land.as:809-863</c>): choose a prob room
        /// and drop a <c>doorprob</c> / <c>doorboss</c> into the cell.
        ///
        /// <para><b>The plan already decided <i>whether</i> this cell gets a prob door</b> — the conf
        /// branches record <see cref="ProbKind.Forced"/> (the <c>imp</c> call) or
        /// <see cref="ProbKind.Chance"/> (the plain one) — so all that is left here is <i>which</i> prob,
        /// which is <see cref="ProbSelection"/>'s job, and the placement, which is
        /// <see cref="RoomPopulator.PlaceProbDoor"/>'s.</para>
        ///
        /// <para><b>Two orderings are copied from the oracle rather than chosen.</b> The roll is only
        /// spent when <see cref="ProbSelection.NeedsRoll"/> says so (<c>:839-846</c> draws in one branch
        /// only), and the prob room is only built when the door was actually placed
        /// (<c>:857-861</c> returns early when <c>createDoorProb</c> fails). Both matter because the draw
        /// comes off the stream shared by every later placement in the land.</para>
        /// </summary>
        private void PlaceProbDoor(LandDefinition land, RoomInstance room, LandCellPlan cell)
        {
            if (land == null || land.probRooms == null || land.probRooms.Count == 0)
            {
                // A cell that the conf marked for a prob door in a land that declares no <prob> children.
                // AS3 returns false from newRandomProb and places nothing; there is nothing to build from.
                return;
            }

            if (ProbContext == null || ProbContext.ProbRoomTemplates == null ||
                ProbContext.ProbRoomTemplates.Count == 0)
            {
                if (!_loggedMissingProbContext)
                {
                    _loggedMissingProbContext = true;
                    Debug.LogWarning(
                        $"[WorldBuilder] '{land.landId}' has {land.probRooms.Count} <prob> room(s) and the " +
                        "plan places prob doors, but no IProbDoorContext is available, so no doorprob / " +
                        "doorboss is placed. Supply the `prob` land's rooms and the trigger store to " +
                        "enable them; the doors are withheld rather than placed without a room behind them.");
                }
                return;
            }

            bool wantImp = cell.Prob == ProbKind.Forced;

            List<ProbRoomDefinition> eligible =
                ProbSelection.Eligible(land.probRooms, landStage, _builtProbRooms, ProbContext.CompletedProbKeys);
            if (eligible.Count == 0) return;

            int roll = ProbSelection.NeedsRoll(eligible, wantImp) ? _rng.Range(0, eligible.Count) : 0;
            ProbSelection.Choice choice = ProbSelection.SelectFrom(eligible, wantImp, roll);
            if (choice == null) return;

            if (RoomPopulator.PlaceProbDoor(room, choice.doorObjectId, choice.probId) == null)
            {
                // No spawn point in this room: AS3 returns false and does NOT build the prob room, so the
                // room is not marked built and a later cell may still open it.
                return;
            }

            _builtProbRooms.Add(choice.probId);

            BuildAndRegisterProbRoom(room, choice.probId);
        }

        /// <summary>
        /// Build the detached room a prob id names and register it so the door can open into it — the
        /// second half of AS3's prob pipeline (<c>Land.buildProb</c>, <c>Land.as:771-807</c>), which
        /// <c>buildProbs</c> runs for every id collected in <c>probIds</c> (<c>:752-768</c>).
        ///
        /// <para><b>Shared by the two callers because AS3 shares it.</b> A trial/battle door and the
        /// bottom-row exit box reach this by the same route in the oracle — both place an object whose
        /// <c>prob</c> attribute is non-empty, and <c>Location.createObj</c>'s tail collects it
        /// (<c>:2080-2082</c>). The port has no collection step, so each placement site calls this
        /// directly; a second copy of the find/build/register sequence is how one of them silently stops
        /// registering.</para>
        ///
        /// <para><b>Idempotent by lookup, not by a flag.</b> <c>ProbDoorContext.TryGetRoom</c> answers
        /// "already built", which is the port of <c>buildProb</c>'s opening
        /// <c>if(this.probs[nprob] != null) return false;</c> (<c>:774-777</c>) — and unlike a separate
        /// set it cannot disagree with the registry the runtime actually reads.</para>
        /// </summary>
        /// <param name="room">
        /// The room the door stands in. Used only to name the failure: a prob room that cannot be built
        /// leaves a door that leads nowhere, and which door matters.
        /// </param>
        /// <param name="probId">AS3 <c>nprob</c>. Empty means the land declared no exit — nothing to do.</param>
        private void BuildAndRegisterProbRoom(RoomInstance room, string probId)
        {
            if (string.IsNullOrEmpty(probId)) return;

            if (ProbContext == null)
            {
                if (!_loggedMissingProbContext)
                {
                    _loggedMissingProbContext = true;
                    Debug.LogWarning(
                        "[WorldBuilder] No IProbDoorContext is available, so no prob room (including a " +
                        "land's exit room) can be built. Supply the `prob` land's rooms — see " +
                        "MapBridge, which builds the context before the land is built.");
                }
                return;
            }

            if (ProbContext.TryGetRoom(probId, out _)) return;

            RoomTemplate template = ProbRoomBuilder.FindRoom(ProbContext.ProbRoomTemplates, probId);
            if (template == null)
            {
                // The door exists and its room is missing from the collection. AS3's loop simply finds no
                // room and leaves `probs[id]` unset, so the door leads nowhere — reported rather than
                // silent, because the cause is always a data mismatch between the land's <prob> list (or
                // its `exit` attribute) and the rooms_prob collection.
                Debug.LogWarning(
                    $"[WorldBuilder] prob '{probId}' has no room in the prob collection, so the door placed " +
                    $"for it (in '{room?.id}') opens into nothing.");
                return;
            }

            RoomInstance probRoom = ProbRoomBuilder.Build(roomGenerator, template, probId);
            if (probRoom != null)
            {
                ProbContext.RegisterProbRoom(probId, probRoom);
            }
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
