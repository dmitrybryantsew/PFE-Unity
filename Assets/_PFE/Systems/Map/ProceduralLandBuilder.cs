using System;
using System.Collections.Generic;
using UnityEngine;
using PFE.ModAPI;
using PFE.Core.Rng;

namespace PFE.Systems.Map
{
    /// <summary>
    /// Host-side adapter that translates pure-ModAPI <see cref="ProceduralLandBuildResult"/> and
    /// <see cref="ProceduralLandExpansionResult"/> blueprints into runtime <see cref="RoomTemplate"/>
    /// and <see cref="RoomInstance"/> objects on a <see cref="LandMap"/>.
    /// Keeps mods completely isolated from PFE.View map internals.
    /// </summary>
    public static class ProceduralLandBuilder
    {
        public static bool ApplyBuildResult(
            LandMap landMap,
            ProceduralLandBuildResult result,
            RoomGenerator roomGenerator,
            TileFormDatabase formDatabase,
            Dictionary<Vector3Int, RoomTemplate> outTemplateMap = null,
            IRngService rng = null)
        {
            if (landMap == null || result == null || !result.Success)
            {
                return false;
            }

            TileFormDatabase resolvedFormDb = ResolveFormDatabase(roomGenerator, formDatabase);
            var templateMap = outTemplateMap ?? new Dictionary<Vector3Int, RoomTemplate>();
            templateMap.Clear();

            landMap.Initialize(result.BoundsMin, result.BoundsMax);

            // Declare this land's room canvas before anything is positioned in world space. A mod land
            // may build bigger rooms than the authored 48x25; every consumer of
            // WorldConstants.CellSizePixels (room origins, camera, door transitions, land row math)
            // reads it live, so this single call is what makes a per-land canvas work.
            if (result.Rooms != null && result.Rooms.Count > 0)
            {
                ProceduralRoomBlueprint first = result.Rooms[0];
                if (first != null && first.Width > 0 && first.Height > 0)
                {
                    WorldConstants.SetCellSize(first.Width, first.Height);
                }
            }

            var builtRooms = new Dictionary<Vector3Int, RoomInstance>();
            if (result.Rooms != null)
            {
                for (int i = 0; i < result.Rooms.Count; i++)
                {
                    ProceduralRoomBlueprint bp = result.Rooms[i];
                    if (bp == null) continue;

                    RoomTemplate template = ToRoomTemplate(bp);
                    RoomInstance room = CreateRoomInstance(template, bp.Position, roomGenerator, resolvedFormDb);
                    if (room == null) continue;

                    landMap.AddRoom(room, bp.Position);
                    templateMap[bp.Position] = template;
                    builtRooms[bp.Position] = room;
                }
            }

            // Link symmetric doors between adjacent grid cells
            for (int x = result.BoundsMin.x; x < result.BoundsMax.x; x++)
            {
                for (int y = result.BoundsMin.y; y < result.BoundsMax.y; y++)
                {
                    var pos = new Vector3Int(x, y, 0);
                    if (!builtRooms.TryGetValue(pos, out RoomInstance currentRoom)) continue;
                    if (!templateMap.TryGetValue(pos, out RoomTemplate currentTemplate)) continue;

                    var rightPos = new Vector3Int(x + 1, y, 0);
                    if (builtRooms.TryGetValue(rightPos, out RoomInstance rightRoom) &&
                        templateMap.TryGetValue(rightPos, out RoomTemplate rightTemplate))
                    {
                        LinkRooms(currentRoom, rightRoom, currentTemplate, rightTemplate, vertical: false);
                    }

                    var downPos = new Vector3Int(x, y + 1, 0);
                    if (builtRooms.TryGetValue(downPos, out RoomInstance downRoom) &&
                        templateMap.TryGetValue(downPos, out RoomTemplate downTemplate))
                    {
                        LinkRooms(currentRoom, downRoom, currentTemplate, downTemplate, vertical: true);
                    }
                }
            }

            foreach (var kvp in builtRooms)
            {
                if (templateMap.TryGetValue(kvp.Key, out RoomTemplate template))
                {
                    RoomPopulator.PopulateRoom(kvp.Value, template, kvp.Value.difficulty, rng);
                }
            }

            if (landMap.HasRoom(result.StartRoomPosition))
            {
                landMap.SwitchRoom(result.StartRoomPosition);
            }
            else if (builtRooms.Count > 0)
            {
                foreach (var pos in builtRooms.Keys)
                {
                    landMap.SwitchRoom(pos);
                    break;
                }
            }

            return true;
        }

        public static bool ApplyExpansion(
            LandMap landMap,
            ProceduralLandExpansionResult expansion,
            RoomGenerator roomGenerator,
            TileFormDatabase formDatabase,
            Dictionary<Vector3Int, RoomTemplate> templateMap,
            IRngService rng = null)
        {
            if (landMap == null || expansion == null || !expansion.Expanded || templateMap == null)
            {
                return false;
            }

            if (expansion.NewRooms == null || expansion.NewRooms.Count == 0)
            {
                return false;
            }

            TileFormDatabase resolvedFormDb = ResolveFormDatabase(roomGenerator, formDatabase);
            var newlyBuilt = new List<(Vector3Int pos, RoomInstance room, RoomTemplate template)>();

            for (int i = 0; i < expansion.NewRooms.Count; i++)
            {
                ProceduralRoomBlueprint bp = expansion.NewRooms[i];
                if (bp == null || landMap.HasRoom(bp.Position)) continue;

                RoomTemplate template = ToRoomTemplate(bp);
                RoomInstance room = CreateRoomInstance(template, bp.Position, roomGenerator, resolvedFormDb);
                if (room == null) continue;

                landMap.AddRoom(room, bp.Position);
                templateMap[bp.Position] = template;
                newlyBuilt.Add((bp.Position, room, template));
            }

            if (newlyBuilt.Count == 0)
            {
                return false;
            }

            // Stitch new rooms against all existing/new neighbors without modifying existing room entities.
            for (int i = 0; i < newlyBuilt.Count; i++)
            {
                (Vector3Int pos, RoomInstance room, RoomTemplate template) = newlyBuilt[i];

                var leftPos = new Vector3Int(pos.x - 1, pos.y, pos.z);
                RoomInstance leftRoom = landMap.GetRoom(leftPos);
                if (leftRoom != null && templateMap.TryGetValue(leftPos, out RoomTemplate leftTemplate))
                {
                    LinkRooms(leftRoom, room, leftTemplate, template, vertical: false);
                }

                var rightPos = new Vector3Int(pos.x + 1, pos.y, pos.z);
                RoomInstance rightRoom = landMap.GetRoom(rightPos);
                if (rightRoom != null && templateMap.TryGetValue(rightPos, out RoomTemplate rightTemplate))
                {
                    LinkRooms(room, rightRoom, template, rightTemplate, vertical: false);
                }

                var abovePos = new Vector3Int(pos.x, pos.y - 1, pos.z);
                RoomInstance aboveRoom = landMap.GetRoom(abovePos);
                if (aboveRoom != null && templateMap.TryGetValue(abovePos, out RoomTemplate aboveTemplate))
                {
                    LinkRooms(aboveRoom, room, aboveTemplate, template, vertical: true);
                }

                var belowPos = new Vector3Int(pos.x, pos.y + 1, pos.z);
                RoomInstance belowRoom = landMap.GetRoom(belowPos);
                if (belowRoom != null && templateMap.TryGetValue(belowPos, out RoomTemplate belowTemplate))
                {
                    LinkRooms(room, belowRoom, template, belowTemplate, vertical: true);
                }
            }

            for (int i = 0; i < newlyBuilt.Count; i++)
            {
                RoomPopulator.PopulateRoom(newlyBuilt[i].room, newlyBuilt[i].template, newlyBuilt[i].room.difficulty, rng);
            }

            landMap.minBounds = expansion.NewBoundsMin;
            landMap.maxBounds = expansion.NewBoundsMax;
            return true;
        }

        public static RoomTemplate ToRoomTemplate(ProceduralRoomBlueprint bp)
        {
            if (bp == null) throw new ArgumentNullException(nameof(bp));

            var template = ScriptableObject.CreateInstance<RoomTemplate>();
            string roomName = !string.IsNullOrEmpty(bp.RoomId)
                ? bp.RoomId
                : $"mod_{bp.Archetype}_{bp.Position.x}_{bp.Position.y}";
            template.id = roomName;
            template.name = roomName;
            template.type = !string.IsNullOrEmpty(bp.Archetype) ? bp.Archetype : "pass";
            template.fixedPosition = bp.Position;
            template.difficultyLevel = Mathf.Clamp(bp.Danger, 0, 20);
            template.allowRandom = false;
            template.maxInstances = 1;
            template.tileDataString = bp.TileDataString ?? string.Empty;

            // The provider's canvas, or the authored grid when it declares none. Every blueprint in
            // one land must carry the same size; the host's door slots are anchored to the room's own
            // walls, so unequal neighbours would carve at different world positions.
            template.width = bp.Width > 0 ? bp.Width : WorldConstants.ROOM_WIDTH;
            template.height = bp.Height > 0 ? bp.Height : WorldConstants.ROOM_HEIGHT;
            int roomHeight = template.height;

            template.doorQuality = new int[24];
            if (bp.DoorPorts != null)
            {
                for (int i = 0; i < bp.DoorPorts.Length && i < 22; i++)
                {
                    template.doorQuality[i] = bp.DoorPorts[i];
                }
            }

            template.objects = new List<ObjectSpawnData>();
            if (bp.Objects != null)
            {
                for (int i = 0; i < bp.Objects.Count; i++)
                {
                    ProceduralObjectSpawn obj = bp.Objects[i];
                    if (obj == null) continue;
                    int unityY = WorldCoordinates.As3RowToUnityRow(obj.As3TileCoord.y, roomHeight);
                    template.objects.Add(new ObjectSpawnData
                    {
                        id = obj.Id,
                        type = obj.Type,
                        tileCoord = new Vector2Int(obj.As3TileCoord.x, unityY),
                        parameters = obj.Parameters ?? string.Empty
                    });
                }
            }

            template.spawnPoints = new List<SpawnPointData>();
            if (bp.HasPlayerSpawn)
            {
                int unitySpawnY = WorldCoordinates.As3RowToUnityRow(bp.As3PlayerSpawn.y, roomHeight);
                template.spawnPoints.Add(new SpawnPointData
                {
                    tileCoord = new Vector2Int(bp.As3PlayerSpawn.x, unitySpawnY),
                    type = SpawnType.Player
                });
            }
            else
            {
                template.spawnPoints.Add(new SpawnPointData
                {
                    tileCoord = new Vector2Int(4, WorldCoordinates.As3RowToUnityRow(roomHeight - 5, roomHeight)),
                    type = SpawnType.Player
                });
            }

            template.backgroundDecorations = new List<BackgroundDecorationData>();
            if (bp.BackgroundDecorations != null)
            {
                for (int i = 0; i < bp.BackgroundDecorations.Count; i++)
                {
                    ProceduralBackDecoration back = bp.BackgroundDecorations[i];
                    if (back == null) continue;
                    int unityBackY = WorldCoordinates.As3RowToUnityRow(back.As3TileCoord.y, roomHeight);
                    template.backgroundDecorations.Add(new BackgroundDecorationData
                    {
                        id = back.Id,
                        tileCoord = new Vector2Int(back.As3TileCoord.x, unityBackY)
                    });
                }
            }

            template.environment = new RoomEnvironmentData();
            if (bp.Environment != null)
            {
                template.environment.borderType = bp.Environment.BorderType;
                template.environment.musicTrack = bp.Environment.MusicTrack ?? "music_0";
                template.environment.colorScheme = bp.Environment.ColorScheme ?? string.Empty;
                template.environment.backgroundWall = bp.Environment.BackgroundWall ?? string.Empty;
                template.environment.waterType = bp.Environment.WaterType;
                template.environment.radiation = bp.Environment.Radiation;
                template.environment.visibilityMultiplier = bp.Environment.VisibilityMultiplier > 0f
                    ? bp.Environment.VisibilityMultiplier
                    : 1f;
                template.environment.darkness = bp.Environment.Darkness;
            }

            return template;
        }

        private static RoomInstance CreateRoomInstance(
            RoomTemplate template,
            Vector3Int position,
            RoomGenerator roomGenerator,
            TileFormDatabase formDatabase)
        {
            if (roomGenerator != null && roomGenerator.FormDatabase != null)
            {
                return roomGenerator.GenerateRoom(template, position);
            }

            var generator = new RoomGenerator(formDatabase);
            return generator.GenerateRoom(template, position);
        }

        private static TileFormDatabase ResolveFormDatabase(RoomGenerator roomGenerator, TileFormDatabase explicitDb)
        {
            if (explicitDb != null)
            {
                explicitDb.Initialize();
                return explicitDb;
            }

            if (roomGenerator?.FormDatabase != null)
            {
                roomGenerator.FormDatabase.Initialize();
                return roomGenerator.FormDatabase;
            }

            TileFormDatabase fallback = ScriptableObject.CreateInstance<TileFormDatabase>();
            fallback.Initialize();
            return fallback;
        }

        public static void LinkRooms(
            RoomInstance roomA,
            RoomInstance roomB,
            RoomTemplate templateA,
            RoomTemplate templateB,
            bool vertical)
        {
            int startP = vertical ? 6 : 0;
            int endP = vertical ? 10 : 5;

            for (int p = startP; p <= endP; p++)
            {
                int pTarget = p + 11;
                int qualityA = templateA.doorQuality != null && p < templateA.doorQuality.Length ? templateA.doorQuality[p] : 0;
                int qualityB = templateB.doorQuality != null && pTarget < templateB.doorQuality.Length ? templateB.doorQuality[pTarget] : 0;
                int amount = Math.Min(qualityA, qualityB);
                if (amount < 2) continue;

                DoorInstance doorA = roomA.doors.Find(d => d.doorIndex == p);
                DoorInstance doorB = roomB.doors.Find(d => d.doorIndex == pTarget);

                if (doorA != null && doorB != null)
                {
                    doorA.isActive = true;
                    doorA.quality = (DoorQuality)amount;
                    doorA.targetRoomPosition = roomB.landPosition;
                    doorA.targetDoorIndex = pTarget;

                    doorB.isActive = true;
                    doorB.quality = (DoorQuality)amount;
                    doorB.targetRoomPosition = roomA.landPosition;
                    doorB.targetDoorIndex = p;

                    DoorCarver.CarveDoor(roomA, p, amount);
                    DoorCarver.CarveDoor(roomB, pTarget, amount);
                }
            }
        }
    }
}
