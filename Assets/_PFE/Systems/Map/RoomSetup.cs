using UnityEngine;
using System.Collections.Generic;
using PFE.Core;

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
    ///     2. ramka border      -> Border applied         [DoorCarver.ApplyBorder()]
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
        public static void FinalizeRoom(RoomInstance room, RoomTemplate template, int borderTypeOverride = -1, PfeDebugSettings debugSettings = null)
        {
            if (room == null || template == null) return;

            RoomEdgeSnapshot before = MapIntegrityDiagnostics.Capture(room);

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

            // Step 4: Mark objects as unplaceable on carved tiles, BEFORE spawning.
            //
            // AS3 marks the doorway tiles `place = false` inside setDoor (Location.setNoObj,
            // Location.as:800-960), which runs while the Location is still being built and therefore
            // strictly before setObjects(). Running the mark *after* PopulateRoom — as this did — let an
            // object spawn in the doorway it was supposed to be kept out of, and then marked a tile that
            // had already been populated. That is one way a carver appears to "break" a room.
            MarkCarvedTilesUnplaceable(room);

            // Step 5: Populate with entities
            RoomPopulator.PopulateRoom(room, template, room.difficulty);

            if (debugSettings != null && debugSettings.LogMapIntegrityDiagnostics)
            {
                Debug.Log(
                    $"[MapIntegrity] FinalizeRandomRoom: pos={room.landPosition}, template={template.id}, " +
                    $"specificOnly={template.specificMapOnly}, border={borderType}, " +
                    $"edgeAirBefore={MapIntegrityDiagnostics.Format(before)}, " +
                    $"edgeAirAfter={MapIntegrityDiagnostics.BuildEdgeAirSummary(room)}, activeDoors={CountActiveDoors(room)}");
            }
        }

        /// <summary>
        /// Finalize all rooms in a LandMap.
        /// Call this after random-world door connections have been built.
        /// </summary>
        public static void FinalizeAllRooms(LandMap landMap, List<RoomTemplate> templates, PfeDebugSettings debugSettings = null)
        {
            if (landMap == null || templates == null) return;

            var templateLookup = new Dictionary<string, RoomTemplate>();
            foreach (var t in templates)
            {
                if (t != null && !string.IsNullOrEmpty(t.id))
                {
                    templateLookup[t.GetContentId()] = t;
                    if (!templateLookup.ContainsKey(t.id))
                    {
                        templateLookup[t.id] = t;
                    }
                }
            }

            foreach (var room in landMap.GetAllRooms())
            {
                if (room == null) continue;

                // The z = 1 interior of a cell (`noMap`) is already finished: WorldBuilder builds,
                // registers and populates it when it reads the template's `backgroundRoomId`. It must be
                // skipped here rather than finalised like a grid room, because AS3 never runs `setDoor()`
                // on it — the door pass reads `locs[x][y][0]` only (Land.as:417-494) — while
                // FinalizeRoom would apply a border and carve its whole candidate mask, punching openings
                // in the interior's walls that lead nowhere. See WorldBuilder.BuildBackgroundLayer.
                if (room.noMap) continue;

                RoomTemplate template = null;
                if (!string.IsNullOrEmpty(room.templateId))
                {
                    templateLookup.TryGetValue(room.templateId, out template);
                }

                if (template != null)
                {
                    FinalizeRoom(room, template, debugSettings: debugSettings);
                }
                else
                {
                    RoomEdgeSnapshot before = MapIntegrityDiagnostics.Capture(room);
                    // No template found - at minimum carve doors
                    DoorCarver.CarveAllDoors(room);
                    if (debugSettings != null && debugSettings.LogMapIntegrityDiagnostics)
                    {
                        Debug.LogWarning(
                            $"[MapIntegrity] FinalizeRandomRoomMissingTemplate: pos={room.landPosition}, templateId={room.templateId}, " +
                            $"edgeAirBefore={MapIntegrityDiagnostics.Format(before)}, " +
                            $"edgeAirAfter={MapIntegrityDiagnostics.BuildEdgeAirSummary(room)}, activeDoors={CountActiveDoors(room)}");
                    }
                }
            }
        }

        /// <summary>
        /// Finalize hand-authored story/specific rooms.
        /// AS3 buildSpecifLand() does not run random-land door matching, setDoor(),
        /// or mainFrame(); authored tiles already contain the intended edge material.
        /// </summary>
        public static void FinalizeSpecificAllRooms(LandMap landMap, List<RoomTemplate> templates, PfeDebugSettings debugSettings = null)
        {
            if (landMap == null || templates == null) return;

            var templateLookup = new Dictionary<string, RoomTemplate>();
            foreach (var t in templates)
            {
                if (t != null && !string.IsNullOrEmpty(t.id))
                {
                    templateLookup[t.GetContentId()] = t;
                    if (!templateLookup.ContainsKey(t.id))
                    {
                        templateLookup[t.id] = t;
                    }
                }
            }

            foreach (var room in landMap.GetAllRooms())
            {
                if (room == null) continue;

                // Already finished at registration — see the same skip in FinalizeAllRooms.
                if (room.noMap) continue;

                RoomTemplate template = null;
                if (!string.IsNullOrEmpty(room.templateId))
                {
                    templateLookup.TryGetValue(room.templateId, out template);
                }

                if (template != null)
                {
                    FinalizeSpecificRoom(room, template, debugSettings);
                }
            }
        }

        public static void FinalizeSpecificRoom(RoomInstance room, RoomTemplate template, PfeDebugSettings debugSettings = null)
        {
            if (room == null || template == null) return;

            RoomEdgeSnapshot before = MapIntegrityDiagnostics.Capture(room);

            if (template.environment.waterLevel < WorldConstants.ROOM_HEIGHT)
            {
                DoorCarver.ApplyWaterLevel(room, template.environment.waterLevel);
            }

            RoomPopulator.PopulateRoom(room, template, room.difficulty);

            if (debugSettings != null && debugSettings.LogMapIntegrityDiagnostics)
            {
                Debug.Log(
                    $"[MapIntegrity] FinalizeSpecificRoom: pos={room.landPosition}, template={template.id}, " +
                    $"specificOnly={template.specificMapOnly}, edgeAirBefore={MapIntegrityDiagnostics.Format(before)}, " +
                    $"edgeAirAfter={MapIntegrityDiagnostics.BuildEdgeAirSummary(room)}, activeDoors={CountActiveDoors(room)}");
            }
        }

        /// <summary>
        /// <b>Debug / editor-preview only.</b> Mark every candidate door slot active so the carver opens
        /// the room's whole door mask.
        ///
        /// <para><b>Gameplay must never call this.</b> AS3 carves a <i>draw</i> from the candidate mask —
        /// three slots with replacement on the right wall, one on the lower side
        /// (<c>Land.as:471-488</c>) — and activating the mask wholesale is precisely the door-carve bug
        /// (see <c>docs/LandGameplayLoop/06_DOOR_CARVE_AUDIT.md</c>).</para>
        ///
        /// <para>It exists because two tools build <i>one</i> room and never run the neighbour pass:
        /// <c>RoomVisualController.BuildPreviewRoom</c> (editor-only) and the single-room branch of
        /// <c>MapBridge.FinalizeOverrideRoom</c>. With no neighbour there is no drawn door, so both would
        /// otherwise render a room with no openings at all. The multi-room branch of the override goes
        /// through <c>WorldBuilder.BuildSpecificWorld</c> and does not need this.</para>
        /// </summary>
        public static void ActivateAllCandidateDoors(RoomInstance room)
        {
            if (room == null || room.doors == null) return;

            foreach (var door in room.doors)
            {
                if (door.quality >= DoorQuality.Narrow)
                {
                    door.isActive = true;
                }
            }
        }

        private static int CountActiveDoors(RoomInstance room)
        {
            if (room == null || room.doors == null)
            {
                return 0;
            }

            int count = 0;
            foreach (var door in room.doors)
            {
                if (door != null && door.isActive)
                {
                    count++;
                }
            }

            return count;
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
        /// Find a valid player spawn position in <b>room-local</b> pixel coordinates.
        /// Searches for an air tile above a solid tile (standing position).
        /// Prefers spawn points, then checkpoint positions, then searches.
        /// </summary>
        /// <remarks>
        /// <para><b>Spawn points are stored in AS3 row space, so they are resolved, not read.</b>
        /// <see cref="SpawnPoint.tileCoord"/> is the AS3 tile of the authored <c>player</c> marker —
        /// row 0 is the ceiling — so Priority 1/2 go through
        /// <see cref="SpawnPoint.GetRoomLocalPixel"/>, which mirrors the row and applies AS3's own
        /// bottom-centre anchor (<c>Land.as:1229</c>). Reading it as a port row put the player at the
        /// room's vertical mirror image: measured over the shipped rooms, 26.6 % spawned inside a wall
        /// and 2.0 % inside the indestructible <c>A</c> frame — the owner's "ceiling above
        /// indestructible tile".</para>
        ///
        /// <para><b>A spawn point that names a wall is refused, and that guard is not padding.</b> The
        /// importer writes an invented constant for the rooms whose AS3 source carries no <c>player</c>
        /// object (234 of 639), and mirroring does not rescue it — that constant is solid in 37.6 % of
        /// them, and the oracle's own <c>(3,3)</c> fallback is solid in 38.9 %. Priority 3's spiral
        /// finds a genuinely standable tile in 226 of those 234, so falling through to it is the only
        /// reading that puts the player on a floor. Authored spawn points are untouched by the guard:
        /// after the mirror, 0 of 405 resolve inside a solid tile.</para>
        /// </remarks>
        public static Vector2 FindPlayerSpawnPixels(RoomInstance room)
        {
            if (room == null)
                return new Vector2(
                    WorldConstants.ROOM_WIDTH * WorldConstants.TILE_SIZE * 0.5f,
                    WorldConstants.ROOM_HEIGHT * WorldConstants.TILE_SIZE * 0.5f);

            // Priority 1: Use designated player spawn points
            foreach (var sp in room.spawnPoints)
            {
                if (sp.type == SpawnType.Player && TryResolveSpawnPoint(room, sp, out Vector2 playerPixel))
                {
                    return playerPixel;
                }
            }

            // Priority 2: Use any spawn point
            if (room.spawnPoints.Count > 0 &&
                TryResolveSpawnPoint(room, room.spawnPoints[0], out Vector2 anyPixel))
            {
                return anyPixel;
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
        /// A spawn point resolved to room-local pixels, or refused when it names a tile the player
        /// cannot be standing in.
        /// </summary>
        /// <remarks>
        /// The tile tested is the one the <i>author</i> named — the marker's column, with the AS3 row
        /// mirrored — not the tile the anchor pixel happens to fall in. The anchor sits on the
        /// boundary one column right of the marker (AS3's <c>size='2'</c> offset), so testing the
        /// anchor's own tile would interrogate the neighbour instead of the point that was authored.
        /// </remarks>
        private static bool TryResolveSpawnPoint(RoomInstance room, SpawnPoint spawn, out Vector2 pixel)
        {
            pixel = default;
            if (spawn == null) return false;

            Vector2Int portTile = new Vector2Int(
                spawn.tileCoord.x,
                WorldCoordinates.As3RowToUnityRow(spawn.tileCoord.y, room.height));

            // `null` means both "off the room" and "no tile grid yet" — GetTileAtCoord's contract, so
            // this one test covers a spawn point parked outside the room as well.
            TileData tile = room.GetTileAtCoord(portTile);
            if (tile == null || tile.physicsType != TilePhysicsType.Air) return false;

            pixel = spawn.GetRoomLocalPixel(room);
            return true;
        }

        /// <summary>
        /// Find player spawn as a Unity <b>world</b> position — the room-local spawn plus the room's
        /// origin (<see cref="WorldCoordinates.RoomOriginPixel"/>).
        /// </summary>
        /// <remarks>
        /// The origin is not optional. <c>TilePhysicsController.SetRoom</c> stores the room origin and
        /// every collision path subtracts it, so the motor consumes world pixels; a room-local position
        /// is a world position only in a room at land (0,0) — which is every procedural land and every
        /// prob room, i.e. every room a quick test visits. Authored lands whose entry cell is off-origin
        /// (<c>rooms_surf</c> → <c>(0,1,0)</c>) were the ones that could tell.
        /// </remarks>
        public static Vector3 FindPlayerSpawnUnity(RoomInstance room)
        {
            Vector2 pixelPos = FindPlayerSpawnPixels(room);
            if (room != null) pixelPos += WorldCoordinates.RoomOriginPixel(room);
            return WorldCoordinates.PixelToUnity(pixelPos);
        }
    }
}
