using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using PFE.Data.Definitions.Campaign;
using PFE.Systems.Map;

namespace PFE.Editor.Importers
{
    /// <summary>
    /// Imports LandDefinition assets from GameData.as into Unity.
    /// Generates ScriptableObjects for all 31 lands and associates them
    /// with their corresponding imported RoomTemplate assets.
    /// </summary>
    public static class CampaignDataImporter
    {
        public const string LandsDirectory = "Assets/_PFE/Data/Resources/Campaign/Lands";
        public const string CatalogPath = "Assets/_PFE/Data/Resources/CampaignCatalog.asset";

        [MenuItem("PFE/Campaign/Import Campaign Lands from GameData.as")]
        public static void ImportAllMenu()
        {
            ImportAll(interactive: true);
        }

        public static List<LandDefinition> ImportAll(bool interactive = false)
        {
            string content = LoadGameDataContent();
            if (string.IsNullOrEmpty(content))
            {
                Debug.LogError("[CampaignDataImporter] Could not load GameData.as content!");
                return new List<LandDefinition>();
            }

            EnsureDirectories();

            var lands = ImportLandsFromContent(content);
            UpdateCatalog(lands);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[CampaignDataImporter] Successfully imported {lands.Count} LandDefinitions into {LandsDirectory}.");

            if (interactive)
            {
                EditorUtility.DisplayDialog(
                    "Campaign Land Import Complete",
                    $"Successfully imported {lands.Count} Land Definitions.\nLinked to room collections in Assets/_PFE/Data/Resources/Rooms/.",
                    "OK"
                );
            }

            return lands;
        }

        public static string LoadGameDataContent()
        {
            string primaryPath = Path.Combine(SourceImportPaths.FeScriptsRoot, "GameData.as");
            if (File.Exists(primaryPath))
            {
                return File.ReadAllText(primaryPath);
            }

            string[] fallbacks = new[]
            {
                @"C:\Users\User\Documents\rustProjects\pfeToUnity\pfe\scripts\fe\GameData.as",
                @"E:\Games\UnityGames\backup from ralph\New folder\pfeToUnity\pfe\scripts\fe\GameData.as"
            };

            foreach (var fallback in fallbacks)
            {
                if (File.Exists(fallback))
                {
                    return File.ReadAllText(fallback);
                }
            }

            return null;
        }

        private static void EnsureDirectories()
        {
            if (!Directory.Exists("Assets/_PFE/Data/Resources/Campaign"))
            {
                Directory.CreateDirectory("Assets/_PFE/Data/Resources/Campaign");
            }
            if (!Directory.Exists(LandsDirectory))
            {
                Directory.CreateDirectory(LandsDirectory);
            }
        }

        public static List<LandDefinition> ImportLandsFromContent(string content)
        {
            var parsedLands = CampaignDataParser.ParseLands(content);
            var results = new List<LandDefinition>();

            foreach (var parsed in parsedLands)
            {
                string id = parsed.landId;
                if (string.IsNullOrEmpty(id)) continue;

                string assetPath = $"{LandsDirectory}/{id}.asset";
                var landDef = AssetDatabase.LoadAssetAtPath<LandDefinition>(assetPath);
                if (landDef == null)
                {
                    landDef = ScriptableObject.CreateInstance<LandDefinition>();
                    AssetDatabase.CreateAsset(landDef, assetPath);
                }

                EditorUtility.CopySerialized(parsed, landDef);

                // Link corresponding RoomTemplates
                landDef.roomTemplates = LinkRoomTemplates(landDef.sourceFileKey);

                EditorUtility.SetDirty(landDef);
                results.Add(landDef);
            }

            return results;
        }

        public static List<RoomTemplate> LinkRoomTemplates(string sourceFileKey)
        {
            var templates = new List<RoomTemplate>();
            if (string.IsNullOrWhiteSpace(sourceFileKey)) return templates;

            string roomFolder = $"Assets/_PFE/Data/Resources/Rooms/{sourceFileKey}";
            if (!Directory.Exists(roomFolder)) return templates;

            string[] guids = AssetDatabase.FindAssets("t:RoomTemplate", new[] { roomFolder });
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var template = AssetDatabase.LoadAssetAtPath<RoomTemplate>(path);
                if (template != null)
                {
                    templates.Add(template);
                }
            }

            templates.Sort((a, b) => string.CompareOrdinal(a.id ?? string.Empty, b.id ?? string.Empty));
            return templates;
        }

        public static void UpdateCatalog(
            List<LandDefinition> lands = null,
            List<QuestDefinition> quests = null,
            List<NpcDefinition> npcs = null,
            List<CampaignScriptDefinition> scripts = null)
        {
            var catalog = AssetDatabase.LoadAssetAtPath<CampaignCatalog>(CatalogPath);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<CampaignCatalog>();
                AssetDatabase.CreateAsset(catalog, CatalogPath);
            }

            List<LandDefinition> currentLands = lands != null ? lands : catalog.AllLands.ToList();
            List<QuestDefinition> currentQuests = quests != null ? quests : catalog.AllQuests.ToList();
            List<NpcDefinition> currentNpcs = npcs != null ? npcs : catalog.AllNpcs.ToList();
            List<CampaignScriptDefinition> currentScripts = scripts != null ? scripts : catalog.AllScripts.ToList();

            catalog.SetEntries(currentLands, currentQuests, currentNpcs, currentScripts);
            catalog.startingLandId = "begin";
            catalog.playerHubLandId = "rbl";

            EditorUtility.SetDirty(catalog);
        }
    }
}
