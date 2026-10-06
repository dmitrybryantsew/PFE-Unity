#if UNITY_EDITOR
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using PFE.Data.Definitions.Campaign;
using Debug = UnityEngine.Debug;

namespace PFE.Editor.Importers
{
    /// <summary>
    /// Headless batchmode test and import verification suite for the PFE Campaign.
    /// Runs via:
    /// -executeMethod PFE.Editor.Importers.CampaignHeadlessVerification.RunHeadlessTests
    /// </summary>
    public static class CampaignHeadlessVerification
    {
        public static void RunHeadlessTests()
        {
            int exitCode = 0;
            var summary = new StringBuilder();
            var totalWatch = Stopwatch.StartNew();

            summary.AppendLine("================================================================================");
            summary.AppendLine("PFE CAMPAIGN PIPELINE HEADLESS VERIFICATION & TEST REPORT");
            summary.AppendLine("Unity Version : " + Application.unityVersion);
            summary.AppendLine("Timestamp     : " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'"));
            summary.AppendLine("================================================================================");
            summary.AppendLine();

            Debug.Log("[CAMPAIGN_TEST] Starting Campaign headless verification...");

            try
            {
                int passed = 0;
                int failed = 0;

                // ────────────────────────────────────────────────────────────────
                // SECTION 1: GameData.as Content Loading
                // ────────────────────────────────────────────────────────────────
                summary.AppendLine("--- SECTION 1: SOURCE GAMEDATA.AS DISCOVERY ---");
                string gameDataContent = CampaignDataImporter.LoadGameDataContent();
                if (string.IsNullOrEmpty(gameDataContent))
                {
                    summary.AppendLine("Result: FAILED - GameData.as content could not be located via SourceImportPaths.");
                    Debug.LogError("[CAMPAIGN_TEST] Failed to locate GameData.as!");
                    failed++;
                }
                else
                {
                    summary.AppendLine($"Result: PASSED - Loaded GameData.as ({gameDataContent.Length:N0} bytes).");
                    Debug.Log($"[CAMPAIGN_TEST] Loaded GameData.as ({gameDataContent.Length:N0} bytes).");
                    passed++;
                }
                summary.AppendLine();

                // ────────────────────────────────────────────────────────────────
                // SECTION 2: Campaign XML Parsing (Pure C# Engine)
                // ────────────────────────────────────────────────────────────────
                summary.AppendLine("--- SECTION 2: XML ENGINE PARSING ---");
                var lands = CampaignDataParser.ParseLands(gameDataContent);
                var quests = CampaignDataParser.ParseQuests(gameDataContent);
                var (npcs, scripts) = CampaignDataParser.ParseNpcsAndScripts(gameDataContent);

                summary.AppendLine($"Parsed Lands   : {lands.Count} (expected >= 29)");
                summary.AppendLine($"Parsed Quests  : {quests.Count} (expected >= 50)");
                summary.AppendLine($"Parsed NPCs    : {npcs.Count} (expected >= 50)");
                summary.AppendLine($"Parsed Scripts : {scripts.Count} (expected >= 50)");

                if (lands.Count >= 29 && quests.Count >= 50 && npcs.Count >= 50 && scripts.Count >= 50)
                {
                    summary.AppendLine("Result: PASSED - All campaign entities parsed cleanly.");
                    Debug.Log($"[CAMPAIGN_TEST] Parsing PASSED: {lands.Count} lands, {quests.Count} quests, {npcs.Count} npcs, {scripts.Count} scripts.");
                    passed++;
                }
                else
                {
                    summary.AppendLine("Result: FAILED - One or more entity counts below expected minimums.");
                    Debug.LogError("[CAMPAIGN_TEST] Entity counts below expected threshold!");
                    failed++;
                }
                summary.AppendLine();

                // ────────────────────────────────────────────────────────────────
                // SECTION 3: Asset Import & ScriptableObject Serialization
                // ────────────────────────────────────────────────────────────────
                summary.AppendLine("--- SECTION 3: ASSET DATABASE IMPORT ---");
                var importedLands = CampaignDataImporter.ImportAll(interactive: false);
                var importedQuests = QuestDataImporter.ImportAll(interactive: false);
                var (importedNpcs, importedScripts) = DialogueDataImporter.ImportAll(interactive: false);

                summary.AppendLine($"Imported Lands   : {importedLands.Count} assets");
                summary.AppendLine($"Imported Quests  : {importedQuests.Count} assets");
                summary.AppendLine($"Imported NPCs    : {importedNpcs.Count} assets");
                summary.AppendLine($"Imported Scripts : {importedScripts.Count} assets");

                // Check starting and hub lands
                bool hasStart = importedLands.Exists(l => l.landId == "begin");
                bool hasHub = importedLands.Exists(l => l.landId == "rbl");

                summary.AppendLine($"Starting Land ('begin') : {(hasStart ? "PRESENT" : "MISSING")}");
                summary.AppendLine($"Hub Safe Haven ('rbl')  : {(hasHub ? "PRESENT" : "MISSING")}");

                if (importedLands.Count >= 29 && hasStart && hasHub)
                {
                    summary.AppendLine("Result: PASSED - Assets generated and registered.");
                    Debug.Log("[CAMPAIGN_TEST] Asset generation PASSED.");
                    passed++;
                }
                else
                {
                    summary.AppendLine("Result: FAILED - Asset generation incomplete.");
                    Debug.LogError("[CAMPAIGN_TEST] Asset generation FAILED.");
                    failed++;
                }
                summary.AppendLine();

                // ────────────────────────────────────────────────────────────────
                // SECTION 4: Campaign Catalog Resolution
                // ────────────────────────────────────────────────────────────────
                summary.AppendLine("--- SECTION 4: CAMPAIGN CATALOG LOOKUP ---");
                var catalog = AssetDatabase.LoadAssetAtPath<CampaignCatalog>(CampaignDataImporter.CatalogPath);
                if (catalog == null)
                {
                    summary.AppendLine("Result: FAILED - CampaignCatalog.asset not found.");
                    Debug.LogError("[CAMPAIGN_TEST] CampaignCatalog not found!");
                    failed++;
                }
                else
                {
                    catalog.Initialize();
                    bool canResolveBegin = catalog.HasLand("begin") && catalog.GetLand("begin") != null;
                    bool canResolveCase = catalog.HasLand("BEGIN"); // case insensitive
                    bool canResolveQuest = catalog.HasQuest("toExit");
                    bool canResolveNpc = catalog.HasNpc("calam");

                    summary.AppendLine($"Lookup 'begin' (case-sensitive)   : {(canResolveBegin ? "OK" : "FAIL")}");
                    summary.AppendLine($"Lookup 'BEGIN' (case-insensitive) : {(canResolveCase ? "OK" : "FAIL")}");
                    summary.AppendLine($"Lookup Quest 'toExit'             : {(canResolveQuest ? "OK" : "FAIL")}");
                    summary.AppendLine($"Lookup NPC 'calam'                : {(canResolveNpc ? "OK" : "FAIL")}");

                    if (canResolveBegin && canResolveCase && canResolveQuest && canResolveNpc)
                    {
                        summary.AppendLine("Result: PASSED - All catalog lookups verified.");
                        Debug.Log("[CAMPAIGN_TEST] Catalog lookups PASSED.");
                        passed++;
                    }
                    else
                    {
                        summary.AppendLine("Result: FAILED - Catalog lookup failed.");
                        Debug.LogError("[CAMPAIGN_TEST] Catalog lookup failed!");
                        failed++;
                    }
                }
                summary.AppendLine();

                // ────────────────────────────────────────────────────────────────
                // SECTION 5: Summary
                // ────────────────────────────────────────────────────────────────
                totalWatch.Stop();
                summary.AppendLine("================================================================================");
                summary.AppendLine($"FINAL STATUS : {(failed == 0 ? "PASSED" : "FAILED")}");
                summary.AppendLine($"Passed Checks: {passed}");
                summary.AppendLine($"Failed Checks: {failed}");
                summary.AppendLine($"Total Time   : {totalWatch.Elapsed.TotalMilliseconds:F1} ms");
                summary.AppendLine("================================================================================");

                if (failed > 0)
                {
                    exitCode = 1;
                    Debug.LogError($"[CAMPAIGN_TEST] Verification finished with {failed} failures!");
                }
                else
                {
                    Debug.Log($"[CAMPAIGN_TEST] ALL {passed} VERIFICATION CHECKS PASSED SUCCESSFULLY in {totalWatch.Elapsed.TotalMilliseconds:F1}ms!");
                }
            }
            catch (Exception ex)
            {
                exitCode = 2;
                summary.AppendLine("\nFATAL EXCEPTION: " + ex);
                Debug.LogError("[CAMPAIGN_TEST] FATAL EXCEPTION: " + ex);
            }

            // Write report to Logs
            string logsDir = Path.Combine(Application.dataPath, "..", "Logs");
            try
            {
                if (!Directory.Exists(logsDir)) Directory.CreateDirectory(logsDir);
                string outPath = Path.Combine(logsDir, "campaign_headless_test_summary.txt");
                File.WriteAllText(outPath, summary.ToString());
                Debug.Log($"[CAMPAIGN_TEST] Summary written to {outPath}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CAMPAIGN_TEST] Failed writing summary file: {ex.Message}");
            }

            EditorApplication.Exit(exitCode);
        }
    }
}
#endif
