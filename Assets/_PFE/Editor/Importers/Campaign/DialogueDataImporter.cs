using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using PFE.Data.Definitions.Campaign;

namespace PFE.Editor.Importers
{
    /// <summary>
    /// Imports NPC persona definitions and campaign event scripts from GameData.as into Unity.
    /// Creates ScriptableObjects for all 54 NPCs (with their dialogue trees) and 73 stand-alone scripts.
    /// </summary>
    public static class DialogueDataImporter
    {
        public const string NpcsDirectory = "Assets/_PFE/Data/Resources/Campaign/NPCs";
        public const string ScriptsDirectory = "Assets/_PFE/Data/Resources/Campaign/Scripts";

        [MenuItem("PFE/Campaign/Import NPCs and Scripts from GameData.as")]
        public static void ImportAllMenu()
        {
            ImportAll(interactive: true);
        }

        public static (List<NpcDefinition> npcs, List<CampaignScriptDefinition> scripts) ImportAll(bool interactive = false)
        {
            string content = CampaignDataImporter.LoadGameDataContent();
            if (string.IsNullOrEmpty(content))
            {
                Debug.LogError("[DialogueDataImporter] Could not load GameData.as content!");
                return (new List<NpcDefinition>(), new List<CampaignScriptDefinition>());
            }

            EnsureDirectories();

            var (npcs, scripts) = ImportFromContent(content);
            CampaignDataImporter.UpdateCatalog(null, null, npcs, scripts);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[DialogueDataImporter] Successfully imported {npcs.Count} NPCs and {scripts.Count} Scripts into Campaign resources.");

            if (interactive)
            {
                EditorUtility.DisplayDialog(
                    "NPC & Script Import Complete",
                    $"Successfully imported:\n- {npcs.Count} NPC Definitions\n- {scripts.Count} Campaign Script Sequences",
                    "OK"
                );
            }

            return (npcs, scripts);
        }

        private static void EnsureDirectories()
        {
            if (!Directory.Exists("Assets/_PFE/Data/Resources/Campaign"))
            {
                Directory.CreateDirectory("Assets/_PFE/Data/Resources/Campaign");
            }
            if (!Directory.Exists(NpcsDirectory))
            {
                Directory.CreateDirectory(NpcsDirectory);
            }
            if (!Directory.Exists(ScriptsDirectory))
            {
                Directory.CreateDirectory(ScriptsDirectory);
            }
        }

        public static (List<NpcDefinition> npcs, List<CampaignScriptDefinition> scripts) ImportFromContent(string content)
        {
            var (parsedNpcs, parsedScripts) = CampaignDataParser.ParseNpcsAndScripts(content);
            var npcResults = new List<NpcDefinition>();
            var scriptResults = new List<CampaignScriptDefinition>();

            // 1. Import NPCs
            foreach (var parsed in parsedNpcs)
            {
                string id = parsed.npcId;
                if (string.IsNullOrEmpty(id)) continue;

                string assetPath = $"{NpcsDirectory}/{id}.asset";
                var npcDef = AssetDatabase.LoadAssetAtPath<NpcDefinition>(assetPath);
                if (npcDef == null)
                {
                    npcDef = ScriptableObject.CreateInstance<NpcDefinition>();
                    AssetDatabase.CreateAsset(npcDef, assetPath);
                }

                EditorUtility.CopySerialized(parsed, npcDef);
                EditorUtility.SetDirty(npcDef);
                npcResults.Add(npcDef);
            }

            // 2. Import Stand-Alone Scripts
            foreach (var parsed in parsedScripts)
            {
                string id = parsed.scriptId;
                if (string.IsNullOrEmpty(id)) continue;

                string assetPath = $"{ScriptsDirectory}/{id}.asset";
                var scriptDef = AssetDatabase.LoadAssetAtPath<CampaignScriptDefinition>(assetPath);
                if (scriptDef == null)
                {
                    scriptDef = ScriptableObject.CreateInstance<CampaignScriptDefinition>();
                    AssetDatabase.CreateAsset(scriptDef, assetPath);
                }

                EditorUtility.CopySerialized(parsed, scriptDef);
                EditorUtility.SetDirty(scriptDef);
                scriptResults.Add(scriptDef);
            }

            return (npcResults, scriptResults);
        }
    }
}
