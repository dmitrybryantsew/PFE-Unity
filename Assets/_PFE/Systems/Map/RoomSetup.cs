using UnityEngine;
using System.Collections.Generic;

namespace PFE.Systems.Map
{
    /// <summary>
    /// Room setup pipeline - orchestrates the full room initialization sequence.
    /// 
    /// This class fills the gap between your WorldBuilder (which generates rooms
    /// and builds door connections) and having fully playable rooms.
    ///
    /// Call flow (matching AS3 Location lifecycle):
    ///   AS3: new Location(land, xml, noHoles, options)
    ///     1. buildLoc(xml)     -> Tiles parsed          [DONE: RoomGenerator.GenerateRoom()]
    ///     2. ramka border      -> Border applied         [NEW: DoorCarver.ApplyBorder()]
    ///     3. doors parsed      -> Door data stored       [DONE: WorldBuilder.BuildDoorConnections()]
    ///     4. setDoor() called  -> Holes carved in tiles  [NEW: DoorCarver.CarveAllDoors()]
    ///     5. setObjects()      -> Entities spawned       [NEW: RoomPopulator.PopulateRoom()]
    ///     6. colorFilter()     -> Visual tinting         [LATER: RoomVisualController]
    ///     7. reactivate()      -> Room becomes active    [DONE: RoomInstance.Activate()]
    ///
    /// Usage: Call RoomSetup.FinalizeRoom() after WorldBuilder.BuildDoorConnections()
    ///        for each room, or call RoomSetup.FinalizeAllRooms() for the whole map.
    /// </summary>
    public static class RoomSetup
    {
        /// <summary>
        /// Finalize a single room (carve doors, apply borders, populate).
        /// Call this after door connections have been built by WorldBuilder.
        /// </summary>
        public static void FinalizeRoom(RoomInstance room, RoomTemplate template, int borderTypeOverride = -1)
        {
            if (room == null || template == null) return;

            int borderType = borderTypeOverride >= 0
                ? borderTypeOverride
                : template.environment.borderType;

            // Step 1: Apply border (ramka)
            // AS3: constructor applies border based on noHolesPlace flag and options
            DoorCarver.ApplyBorder(room, borderType);

            // Step 2: Carve door openings
            // AS3: setDoor() called for each connected door by Land
            DoorCarver.CarveAllDoors(room);

            // Step 3: Apply water level
            if (template.environment.waterLevel < WorldConstants.ROOM_HEIGHT)
            {
                DoorCarver.ApplyWaterLevel(room, template.environment.waterLevel);
            }

            // Step 4: Populate with entities
            RoomPopulator.PopulateRoom(room, template, room.difficulty);

            // Step 5: Mark objects as unplaceable on carved tiles
            // AS3: noHolesPlace prevents objects from spawning where doors carved
            MarkCarvedTilesUnplaceable(room);
        }

        /// <summary>
        /// Finalize all rooms in a LandMap.
        /// Call this after WorldBuilder.BuildRandomWorld() or BuildSpecificWorld().
        /// </summary>
        public static void FinalizeAllRooms(LandMap landMap, List<RoomTemplate> templates)
        {
            if (landMap == null || templates == null) return;

            var templateLookup = new Dictionary<string, RoomTemplate>();
            foreach (var t in templates)
            {
                if (t != null && !string.IsNullOrEmpty(t.id))
                {
                    templateLookup[t.id] = t;
                }
            }

            foreach (var room in landMap.GetAllRooms())
            {
                if (room == null) continue;

                RoomTemplate template = null;
                if (!string.IsNullOrEmpty(room.templateId))
                {
                    templateLookup.TryGetValue(room.templateId, out template);
                }

                if (template != null)
                {
                    FinalizeRoom(room, template);
                }
                else
                {
                    // No template found - at minimum carve doors
                    DoorCarver.CarveAllDoors(room);
                }
            }
        }

        /// <summary>
        /// Mark tiles adjacent to carved doors as unplaceable for objects.
        /// Prevents enemies/boxes from spawning inside doorways.
        /// </summary>
        private static void MarkCarvedTilesUnplaceable(RoomInstance room)
        {
            if (room == null || room.tiles == null) return;

            // Mark border tiles as unplaceable (objects shouldn't spawn on edges)
            for (int x = 0; x < room.width; x++)
            {
                for (int y = 0; y < room.height; y++)
                {
                    if (x <= 1 || x >= room.width - 2 || y <= 1 || y >= room.height - 2)
                    {
                        var tile = room.tiles[x, y];
                        if (tile != null && tile.physicsType == TilePhysicsType.Air)
                        {
                            tile.canPlaceObjects = false;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Find a valid player spawn position in pixel coordinates.
        /// Searches for an air tile above a solid tile (standing position).
        /// Prefers spawn points, then checkpoint positions, then searches.
        /// </summary>
        public static Vector2 FindPlayerSpawnPixels(RoomInstance room)
        {
            if (room == null)
                return new Vector2(
                    WorldConstants.ROOM_WIDTH * WorldConstants.TILE_SIZE * 0.5f,
                    WorldConstants.ROOM_HEIGHT * WorldConstants.TILE_SIZE * 0.5f);

            // Priority 1: Use designated player spawn points
            foreach (var sp in room.spawnPoints)
            {
                if (sp.type == SpawnType.Player)
                {
                    return sp.GetWorldPosition();
                }
            }

            // Priority 2: Use any spawn point
            if (room.spawnPoints.Count > 0)
            {
                return room.spawnPoints[0].GetWorldPosition();
            }

            // Priority 3: Search for walkable position (air above wall/platform)
            // Start from center and spiral outward
            int cx = room.width / 2;
            int cy = room.height / 2;

            for (int radius = 0; radius < Mathf.Max(room.width, room.height); radius++)
            {
                for (int x = Mathf.Max(2, cx - radius); x <= Mathf.Min(room.width - 3, cx + radius); x++)
                {
                    for (int y = Mathf.Max(2, cy - radius); y <= Mathf.Min(room.height - 3, cy + radius); y++)
                    {
                        var tile = room.GetTileAtCoord(new Vector2Int(x, y));
                        var tileBelow = room.GetTileAtCoord(new Vector2Int(x, y - 1));

                        if (tile != null && tileBelow != null &&
                            tile.physicsType == TilePhysicsType.Air &&
                            (tileBelow.physicsType == TilePhysicsType.Wall ||
                             tileBelow.physicsType == TilePhysicsType.Platform))
                        {
                            // Found: air tile above solid ground
                            // Return pixel position at feet (center X, top of ground tile Y)
                            return new Vector2(
                                (x + 0.5f) * WorldConstants.TILE_SIZE,
                                (y) * WorldConstants.TILE_SIZE);
                        }
                    }
                }
            }

            // Fallback: room center
            return new Vector2(
                room.width * WorldConstants.TILE_SIZE * 0.5f,
                room.height * WorldConstants.TILE_SIZE * 0.5f);
        }

        /// <summary>
        /// Find player spawn as Unity world position.
        /// </summary>
        public static Vector3 FindPlayerSpawnUnity(RoomInstance room)
        {
            Vector2 pixelPos = FindPlayerSpawnPixels(room);
            return WorldCoordinates.PixelToUnity(pixelPos);
        }
    }
}
