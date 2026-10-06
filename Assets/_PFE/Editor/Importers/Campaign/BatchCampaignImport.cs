using System;
using UnityEditor;
using UnityEngine;
using PFE.Data.Definitions.Campaign;

namespace PFE.Editor.Importers
{
    /// <summary>
    /// Master batch importer for the entire PFE Campaign.
    /// Runs CampaignDataImporter, QuestDataImporter, and DialogueDataImporter in sequence,
    /// generating all 31 Lands, 59 Quests, 54 NPCs, and 73 Scripts, and wiring them into
    /// the master CampaignCatalog.
    /// </summary>
    public static class BatchCampaignImport
    {
        [MenuItem("PFE/Campaign/Import Entire Campaign", priority = 0)]
        public static void ImportEntireCampaign()
        {
            try
            {
                EditorUtility.DisplayProgressBar("PFE Campaign Importer", "Importing Lands...", 0.1f);
                var lands = CampaignDataImporter.ImportAll(interactive: false);

                EditorUtility.DisplayProgressBar("PFE Campaign Importer", "Importing Quests...", 0.4f);
                var quests = QuestDataImporter.ImportAll(interactive: false);

                EditorUtility.DisplayProgressBar("PFE Campaign Importer", "Importing NPCs & Scripts...", 0.7f);
                var (npcs, scripts) = DialogueDataImporter.ImportAll(interactive: false);

                EditorUtility.DisplayProgressBar("PFE Campaign Importer", "Saving Campaign Catalog...", 0.95f);
                CampaignDataImporter.UpdateCatalog(lands, quests, npcs, scripts);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                EditorUtility.ClearProgressBar();

                string message = $"Successfully imported entire campaign into Unity!\n\n" +
                                 $"• Lands: {lands.Count} assets\n" +
                                 $"• Quests: {quests.Count} assets\n" +
                                 $"• NPCs: {npcs.Count} assets\n" +
                                 $"• Scripts: {scripts.Count} assets\n\n" +
                                 $"Master Catalog saved to:\n{CampaignDataImporter.CatalogPath}";

                EditorUtility.DisplayDialog("Campaign Import Complete", message, "OK");
                Debug.Log($"[BatchCampaignImport] Completed: {lands.Count} lands, {quests.Count} quests, {npcs.Count} NPCs, {scripts.Count} scripts.");
            }
            catch (Exception ex)
            {
                EditorUtility.ClearProgressBar();
                Debug.LogException(ex);
                EditorUtility.DisplayDialog("Import Failed", $"Campaign import encountered an error:\n{ex.Message}", "OK");
            }
        }
    }
}
