using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using PFE.Core;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace PFE.Systems.Map
{
    public static class MapIntegrityDiagnostics
    {
        public static void LogTemplateRegistryIntegrity(
            IReadOnlyList<RoomTemplate> loadedTemplates,
            IReadOnlyList<RoomTemplate> rawResourceTemplates,
            PfeDebugSettings debugSettings)
        {
            if (debugSettings == null || !debugSettings.LogMapIntegrityDiagnostics)
            {
                return;
            }

            int loadedCount = loadedTemplates?.Count ?? 0;
            int rawCount = rawResourceTemplates?.Count ?? 0;
            int loadedSpecific = loadedTemplates?.Count(t => t != null && t.specificMapOnly) ?? 0;
            int rawSpecific = rawResourceTemplates?.Count(t => t != null && t.specificMapOnly) ?? 0;

            Debug.Log(
                $"[MapIntegrity] Templates: loaded={loadedCount}, rawResources={rawCount}, " +
                $"loadedSpecificOnly={loadedSpecific}, rawSpecificOnly={rawSpecific}");

            if (loadedTemplates != null)
            {
                int duplicateContentIds = loadedTemplates
                    .Where(t => t != null)
                    .GroupBy(t => t.GetContentId())
                    .Count(g => g.Count() > 1);

                if (duplicateContentIds > 0)
                {
                    Debug.LogWarning($"[MapIntegrity] Loaded templates still contain {duplicateContentIds} duplicate content IDs.");
                }
                else
                {
                    Debug.Log("[MapIntegrity] Loaded template content IDs are unique.");
                }
            }

            if (rawResourceTemplates == null)
            {
                return;
            }

            var duplicateGroups = rawResourceTemplates
                .Where(t => t != null && !string.IsNullOrEmpty(t.id))
                .GroupBy(t => t.id)
                .Where(g => g.Count() > 1)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key)
                .ToList();

            if (duplicateGroups.Count == 0)
            {
                Debug.Log("[MapIntegrity] No duplicate raw RoomTemplate IDs found under Resources/Rooms.");
                return;
            }

            Debug.Log(
                $"[MapIntegrity] Found {duplicateGroups.Count} duplicate bare RoomTemplate IDs under Resources/Rooms. " +
                "Content IDs are collection-qualified, so this is expected.");

            foreach (var group in duplicateGroups.Take(3))
            {
                var entries = group
                    .Select(DescribeTemplateAsset)
                    .ToArray();

                Debug.Log(
                    $"[MapIntegrity] Duplicate room id '{group.Key}' count={group.Count()} :: " +
                    string.Join(" | ", entries));
            }
        }

        public static void LogBuildPath(string path, int templateCount, Vector3Int minBounds, Vector3Int maxBounds, PfeDebugSettings debugSettings)
        {
            if (debugSettings == null || !debugSettings.LogMapIntegrityDiagnostics)
            {
                return;
            }

            Debug.Log($"[MapIntegrity] BuildPath={path}, templates={templateCount}, bounds={minBounds}->{maxBounds}");
        }

        public static void LogRoomSnapshot(string label, RoomInstance room, RoomTemplate template, PfeDebugSettings debugSettings)
        {
            if (debugSettings == null || !debugSettings.LogMapIntegrityDiagnostics || room == null)
            {
                return;
            }

            Debug.Log(
                $"[MapIntegrity] {label}: pos={room.landPosition}, room={room.id}, template={DescribeTemplate(template)}, " +
                $"activeDoors={CountActiveDoors(room)}, edgeAir={BuildEdgeAirSummary(room)}");
        }

        public static void LogRoomMutation(string label, RoomInstance beforeRoom, RoomInstance afterRoom, RoomTemplate template, PfeDebugSettings debugSettings)
        {
            if (debugSettings == null || !debugSettings.LogMapIntegrityDiagnostics || afterRoom == null)
            {
                return;
            }

            string before = beforeRoom == null ? "n/a" : BuildEdgeAirSummary(beforeRoom);
            string after = BuildEdgeAirSummary(afterRoom);
            Debug.Log(
                $"[MapIntegrity] {label}: pos={afterRoom.landPosition}, template={DescribeTemplate(template)}, " +
                $"edgeAirBefore={before}, edgeAirAfter={after}, activeDoors={CountActiveDoors(afterRoom)}");
        }

        public static RoomEdgeSnapshot Capture(RoomInstance room)
        {
            if (room == null)
            {
                return default;
            }

            return new RoomEdgeSnapshot
            {
                leftAir = CountEdgeAir(room, Edge.Left),
                rightAir = CountEdgeAir(room, Edge.Right),
                bottomAir = CountEdgeAir(room, Edge.Bottom),
                topAir = CountEdgeAir(room, Edge.Top)
            };
        }

        public static string Format(RoomEdgeSnapshot snapshot)
        {
            return $"L{snapshot.leftAir}/R{snapshot.rightAir}/B{snapshot.bottomAir}/T{snapshot.topAir}";
        }

        public static string BuildEdgeAirSummary(RoomInstance room)
        {
            return $"{Format(Capture(room))} samples={BuildEdgeAirSamples(room, 12)}";
        }

        private static int CountActiveDoors(RoomInstance room)
        {
            if (room.doors == null)
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

        private static int CountEdgeAir(RoomInstance room, Edge edge)
        {
            if (room.tiles == null)
            {
                return 0;
            }

            int count = 0;
            switch (edge)
            {
                case Edge.Left:
                    for (int y = 0; y < room.height; y++)
                    {
                        if (IsAir(room.tiles[0, y])) count++;
                    }
                    break;
                case Edge.Right:
                    for (int y = 0; y < room.height; y++)
                    {
                        if (IsAir(room.tiles[room.width - 1, y])) count++;
                    }
                    break;
                case Edge.Bottom:
                    for (int x = 0; x < room.width; x++)
                    {
                        if (IsAir(room.tiles[x, 0])) count++;
                    }
                    break;
                case Edge.Top:
                    for (int x = 0; x < room.width; x++)
                    {
                        if (IsAir(room.tiles[x, room.height - 1])) count++;
                    }
                    break;
            }

            return count;
        }

        private static bool IsAir(TileData tile)
        {
            return tile == null || tile.physicsType == TilePhysicsType.Air;
        }

        private static string BuildEdgeAirSamples(RoomInstance room, int maxSamples)
        {
            if (room == null || room.tiles == null)
            {
                return "none";
            }

            var samples = new List<string>();
            AddEdgeSamples(room, Edge.Left, samples, maxSamples);
            AddEdgeSamples(room, Edge.Right, samples, maxSamples);
            AddEdgeSamples(room, Edge.Bottom, samples, maxSamples);
            AddEdgeSamples(room, Edge.Top, samples, maxSamples);

            return samples.Count == 0 ? "none" : string.Join(",", samples);
        }

        private static void AddEdgeSamples(RoomInstance room, Edge edge, List<string> samples, int maxSamples)
        {
            if (samples.Count >= maxSamples)
            {
                return;
            }

            switch (edge)
            {
                case Edge.Left:
                    for (int y = 0; y < room.height && samples.Count < maxSamples; y++)
                    {
                        AddSampleIfAir(room, 0, y, samples);
                    }
                    break;
                case Edge.Right:
                    for (int y = 0; y < room.height && samples.Count < maxSamples; y++)
                    {
                        AddSampleIfAir(room, room.width - 1, y, samples);
                    }
                    break;
                case Edge.Bottom:
                    for (int x = 0; x < room.width && samples.Count < maxSamples; x++)
                    {
                        AddSampleIfAir(room, x, 0, samples);
                    }
                    break;
                case Edge.Top:
                    for (int x = 0; x < room.width && samples.Count < maxSamples; x++)
                    {
                        AddSampleIfAir(room, x, room.height - 1, samples);
                    }
                    break;
            }
        }

        private static void AddSampleIfAir(RoomInstance room, int x, int y, List<string> samples)
        {
            if (IsAir(room.tiles[x, y]))
            {
                samples.Add($"({x},{y})");
            }
        }

        private static string DescribeTemplate(RoomTemplate template)
        {
            if (template == null)
            {
                return "<null>";
            }

            return $"{DescribeTemplateAsset(template)} type={template.type} fixed={template.fixedPosition} " +
                   $"allowRandom={template.allowRandom} specificOnly={template.specificMapOnly} border={template.environment?.borderType}";
        }

        private static string DescribeTemplateAsset(RoomTemplate template)
        {
            if (template == null)
            {
                return "<null>";
            }

            var builder = new StringBuilder();
            builder.Append(template.name);
            builder.Append("(id=");
            builder.Append(template.id);
            builder.Append(", contentId=");
            builder.Append(template.GetContentId());
            builder.Append(")");

#if UNITY_EDITOR
            string path = AssetDatabase.GetAssetPath(template);
            if (!string.IsNullOrEmpty(path))
            {
                builder.Append(" path=");
                builder.Append(path);
            }
#endif

            return builder.ToString();
        }

        private enum Edge
        {
            Left,
            Right,
            Bottom,
            Top
        }
    }

    public struct RoomEdgeSnapshot
    {
        public int leftAir;
        public int rightAir;
        public int bottomAir;
        public int topAir;
    }
}
