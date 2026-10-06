using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using PFE.Data.Definitions.Campaign;

namespace PFE.Editor.Importers
{
    /// <summary>
    /// Imports QuestDefinition assets from GameData.as into Unity.
    /// Creates ScriptableObjects for all main and side quests with their
    /// sub-stages, collection objectives, and reward payoffs.
    /// </summary>
    public static class QuestDataImporter
    {
        public const string QuestsDirectory = "Assets/_PFE/Data/Resources/Campaign/Quests";

        [MenuItem("PFE/Campaign/Import Quests from GameData.as")]
        public static void ImportAllMenu()
        {
            ImportAll(interactive: true);
        }

        public static List<QuestDefinition> ImportAll(bool interactive = false)
        {
            string content = CampaignDataImporter.LoadGameDataContent();
            if (string.IsNullOrEmpty(content))
            {
                Debug.LogError("[QuestDataImporter] Could not load GameData.as content!");
                return new List<QuestDefinition>();
            }

            EnsureDirectories();

            var quests = ImportQuestsFromContent(content);
            CampaignDataImporter.UpdateCatalog(null, quests);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[QuestDataImporter] Successfully imported {quests.Count} QuestDefinitions into {QuestsDirectory}.");

            if (interactive)
            {
                EditorUtility.DisplayDialog(
                    "Quest Import Complete",
                    $"Successfully imported {quests.Count} Quest Definitions into {QuestsDirectory}.",
                    "OK"
                );
            }

            return quests;
        }

        private static void EnsureDirectories()
        {
            if (!Directory.Exists("Assets/_PFE/Data/Resources/Campaign"))
            {
                Directory.CreateDirectory("Assets/_PFE/Data/Resources/Campaign");
            }
            if (!Directory.Exists(QuestsDirectory))
            {
                Directory.CreateDirectory(QuestsDirectory);
            }
        }

        public static List<QuestDefinition> ImportQuestsFromContent(string content)
        {
            var parsedQuests = CampaignDataParser.ParseQuests(content);
            var results = new List<QuestDefinition>();

            foreach (var parsed in parsedQuests)
            {
                string id = parsed.questId;
                if (string.IsNullOrEmpty(id)) continue;

                string assetPath = $"{QuestsDirectory}/{id}.asset";
                var questDef = AssetDatabase.LoadAssetAtPath<QuestDefinition>(assetPath);
                if (questDef == null)
                {
                    questDef = ScriptableObject.CreateInstance<QuestDefinition>();
                    AssetDatabase.CreateAsset(questDef, assetPath);
                }

                EditorUtility.CopySerialized(parsed, questDef);
                EditorUtility.SetDirty(questDef);
                results.Add(questDef);
            }

            return results;
        }
    }
}
