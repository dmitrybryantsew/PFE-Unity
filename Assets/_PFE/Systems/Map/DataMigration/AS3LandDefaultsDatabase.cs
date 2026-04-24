using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using UnityEngine;

namespace PFE.Systems.Map.DataMigration
{
    [Serializable]
    public sealed class AS3LandOptions
    {
        public string landId = "";
        public string fileId = "";
        public Dictionary<string, string> options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parsed land-level options from GameData.as, merged by source room collection id.
    /// Only options that are identical across all lands sharing the same file id are kept.
    /// This avoids baking incorrect defaults into room templates when a room file is reused by multiple lands.
    /// </summary>
    public sealed class AS3LandDefaultsDatabase
    {
        private readonly Dictionary<string, Dictionary<string, string>> _mergedOptionsByFileId =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        public bool IsEmpty => _mergedOptionsByFileId.Count == 0;

        public bool TryGetOptions(string fileId, out IReadOnlyDictionary<string, string> options)
        {
            if (_mergedOptionsByFileId.TryGetValue(fileId ?? string.Empty, out Dictionary<string, string> merged))
            {
                options = merged;
                return true;
            }

            options = null;
            return false;
        }

        public static AS3LandDefaultsDatabase ParseFromFile(string filePath)
        {
            AS3LandDefaultsDatabase database = new AS3LandDefaultsDatabase();
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return database;
            }

            try
            {
                string content = File.ReadAllText(filePath);
                Match gameMatch = Regex.Match(content, @"<game>[\s\S]*?<\/game>", RegexOptions.IgnoreCase);
                if (!gameMatch.Success)
                {
                    Debug.LogWarning($"[AS3LandDefaultsDatabase] Could not find <game> XML in {filePath}");
                    return database;
                }

                XmlDocument doc = new XmlDocument();
                doc.LoadXml(gameMatch.Value);

                XmlNodeList landNodes = doc.SelectNodes("//land | //land1");
                Dictionary<string, List<AS3LandOptions>> grouped = new Dictionary<string, List<AS3LandOptions>>(StringComparer.OrdinalIgnoreCase);

                foreach (XmlNode landNode in landNodes)
                {
                    string fileId = landNode.Attributes?["file"]?.Value?.Trim() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(fileId))
                    {
                        continue;
                    }

                    AS3LandOptions land = new AS3LandOptions
                    {
                        landId = landNode.Attributes?["id"]?.Value?.Trim() ?? string.Empty,
                        fileId = fileId
                    };

                    XmlNode optionsNode = landNode.SelectSingleNode("./options");
                    if (optionsNode?.Attributes != null)
                    {
                        foreach (XmlAttribute attribute in optionsNode.Attributes)
                        {
                            land.options[attribute.Name] = attribute.Value;
                        }
                    }

                    if (!grouped.TryGetValue(fileId, out List<AS3LandOptions> list))
                    {
                        list = new List<AS3LandOptions>();
                        grouped[fileId] = list;
                    }

                    list.Add(land);
                }

                foreach (KeyValuePair<string, List<AS3LandOptions>> pair in grouped)
                {
                    database._mergedOptionsByFileId[pair.Key] = MergeSharedOptions(pair.Key, pair.Value);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AS3LandDefaultsDatabase] Failed to parse {filePath}: {ex.Message}");
            }

            return database;
        }

        private static Dictionary<string, string> MergeSharedOptions(string fileId, List<AS3LandOptions> lands)
        {
            Dictionary<string, string> merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (lands == null || lands.Count == 0)
            {
                return merged;
            }

            HashSet<string> allKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AS3LandOptions land in lands)
            {
                foreach (string key in land.options.Keys)
                {
                    allKeys.Add(key);
                }
            }

            foreach (string key in allKeys)
            {
                bool allHaveKey = lands.All(land => land.options.ContainsKey(key));
                if (!allHaveKey)
                {
                    continue;
                }

                string firstValue = lands[0].options[key];
                bool sameValue = lands.All(land => string.Equals(land.options[key], firstValue, StringComparison.Ordinal));
                if (sameValue)
                {
                    merged[key] = firstValue;
                }
                else
                {
                    string landIds = string.Join(", ", lands.Select(land => string.IsNullOrWhiteSpace(land.landId) ? "<unnamed>" : land.landId));
                    Debug.LogWarning(
                        $"[AS3LandDefaultsDatabase] Skipping conflicting inherited option '{key}' for '{fileId}' " +
                        $"because multiple lands disagree: {landIds}");
                }
            }

            return merged;
        }
    }
}
