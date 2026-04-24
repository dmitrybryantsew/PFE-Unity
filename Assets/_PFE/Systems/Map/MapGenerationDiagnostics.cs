using System.Collections.Generic;
using UnityEngine;
using PFE.Core;

namespace PFE.Systems.Map
{
    /// <summary>
    /// Logs room-template health before world generation.
    /// </summary>
    public static class MapGenerationDiagnostics
    {
        public static void LogTemplateSummary(
            List<RoomTemplate> templates,
            int stage,
            Vector3Int minBounds,
            Vector3Int maxBounds,
            bool prototypeMode,
            PfeDebugSettings debugSettings = null)
        {
            if (templates == null || templates.Count == 0)
            {
                Debug.LogWarning("[MapDiagnostics] No room templates to analyze.");
                return;
            }

            int total = templates.Count;
            int targetRooms = Mathf.Max(0, (maxBounds.x - minBounds.x) * (maxBounds.y - minBounds.y) * (maxBounds.z - minBounds.z));
            int emptyTiles = 0;
            int zeroDoors = 0;
            int missingId = 0;
            int roughCapacity = 0;

            Dictionary<string, int> byType = new Dictionary<string, int>();

            foreach (var template in templates)
            {
                if (template == null)
                {
                    continue;
                }

                string type = string.IsNullOrEmpty(template.type) ? "<empty>" : template.type;
                if (!byType.ContainsKey(type))
                {
                    byType[type] = 0;
                }
                byType[type]++;

                if (string.IsNullOrEmpty(template.id))
                {
                    missingId++;
                }

                if (string.IsNullOrWhiteSpace(template.tileDataString))
                {
                    emptyTiles++;
                }

                if (CountUsableDoors(template) == 0)
                {
                    zeroDoors++;
                }

                if (!prototypeMode && template.allowRandom && template.difficultyLevel <= stage)
                {
                    roughCapacity += Mathf.Max(0, template.maxInstances);
                }
            }

            string capacityLabel = prototypeMode ? "unbounded(prototype)" : roughCapacity.ToString();

            if (debugSettings?.LogMapGenerationDiagnostics != false)
            {
                Debug.Log(
                    $"[MapDiagnostics] templates={total}, targetRooms={targetRooms}, stage={stage}, prototypeMode={prototypeMode}, " +
                    $"roughCapacity={capacityLabel}, emptyTiles={emptyTiles}, zeroDoorTemplates={zeroDoors}, missingId={missingId}");

                foreach (var kv in byType)
                {
                    Debug.Log($"[MapDiagnostics] type '{kv.Key}': {kv.Value}");
                }
            }

            if (!prototypeMode && roughCapacity < targetRooms)
            {
                Debug.LogWarning(
                    $"[MapDiagnostics] Effective random capacity ({roughCapacity}) is lower than requested room count ({targetRooms}). " +
                    "Expect gaps unless you add more templates or raise maxInstances.");
            }
        }

        private static int CountUsableDoors(RoomTemplate template)
        {
            if (template.doorQuality == null)
            {
                return 0;
            }

            int count = 0;
            for (int i = 0; i < template.doorQuality.Length; i++)
            {
                if (template.doorQuality[i] >= (int)DoorQuality.Narrow)
                {
                    count++;
                }
            }
            return count;
        }
    }
}
