using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using PFE.Data.Definitions.Campaign;
using PFE.Editor.Importers;

namespace PFE.Editor.Windows
{
    public enum ValidationSeverity
    {
        Info,
        Warning,
        Error
    }

    public class CampaignValidationIssue
    {
        public ValidationSeverity Severity;
        public string Category;
        public string SubjectId;
        public string Message;
        public UnityEngine.Object TargetAsset;

        public CampaignValidationIssue(ValidationSeverity severity, string category, string subjectId, string message, UnityEngine.Object asset = null)
        {
            Severity = severity;
            Category = category;
            SubjectId = subjectId;
            Message = message;
            TargetAsset = asset;
        }
    }

    /// <summary>
    /// Interactive QA validation suite for the PFE Campaign.
    /// Audits Lands, Quests, NPCs, and Scripts for broken linkages, missing transitions, and orphaned references.
    /// </summary>
    public class CampaignValidationWindow : EditorWindow
    {
        private CampaignCatalog _catalog;
        private List<CampaignValidationIssue> _issues = new List<CampaignValidationIssue>();
        private Vector2 _scrollPos;

        private string _searchFilter = "";
        private bool _showErrors = true;
        private bool _showWarnings = true;
        private bool _showInfo = false;

        private int _errorCount = 0;
        private int _warningCount = 0;
        private int _infoCount = 0;
        private bool _hasRun = false;

        [MenuItem("Window/PFE/Campaign Validation Suite", priority = 200)]
        [MenuItem("Tools/PFE/Campaign Validation Window", priority = 100)]
        public static void ShowWindow()
        {
            var window = GetWindow<CampaignValidationWindow>("Campaign Validator");
            window.minSize = new Vector2(700, 480);
            window.Show();
        }

        private void OnEnable()
        {
            FindCatalog();
        }

        private void FindCatalog()
        {
            _catalog = AssetDatabase.LoadAssetAtPath<CampaignCatalog>(CampaignDataImporter.CatalogPath);
        }

        private void OnGUI()
        {
            DrawHeader();
            EditorGUILayout.Space(6);
            DrawToolbar();
            EditorGUILayout.Space(6);
            DrawFilterControls();
            EditorGUILayout.Space(6);
            DrawResultsList();
        }

        private void DrawHeader()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("PFE Campaign Validation Suite", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Validates structural integrity, transitions, quests, and scripts against AS3 parity rules.", EditorStyles.miniLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                _catalog = (CampaignCatalog)EditorGUILayout.ObjectField("Catalog Asset:", _catalog, typeof(CampaignCatalog), false);
                if (GUILayout.Button("Locate", GUILayout.Width(70)))
                {
                    FindCatalog();
                }
            }

            if (_hasRun)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUI.color = _errorCount > 0 ? Color.red : Color.green;
                    EditorGUILayout.LabelField($"Errors: {_errorCount}", EditorStyles.boldLabel, GUILayout.Width(110));
                    GUI.color = _warningCount > 0 ? Color.yellow : Color.white;
                    EditorGUILayout.LabelField($"Warnings: {_warningCount}", EditorStyles.boldLabel, GUILayout.Width(130));
                    GUI.color = Color.white;
                    EditorGUILayout.LabelField($"Notices: {_infoCount}", EditorStyles.miniLabel, GUILayout.Width(110));
                }
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.backgroundColor = new Color(0.3f, 0.8f, 0.4f);
                if (GUILayout.Button("Run Full Validation", GUILayout.Height(30)))
                {
                    RunValidation();
                }
                GUI.backgroundColor = Color.white;

                if (GUILayout.Button("Import Entire Campaign", GUILayout.Height(30), GUILayout.Width(180)))
                {
                    BatchCampaignImport.ImportEntireCampaign();
                    FindCatalog();
                    RunValidation();
                }

                if (GUILayout.Button("Export Report (MD)", GUILayout.Height(30), GUILayout.Width(140)))
                {
                    ExportMarkdownReport();
                }

                if (GUILayout.Button("Clear", GUILayout.Height(30), GUILayout.Width(70)))
                {
                    _issues.Clear();
                    _errorCount = 0;
                    _warningCount = 0;
                    _infoCount = 0;
                    _hasRun = false;
                }
            }
        }

        private void DrawFilterControls()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                _searchFilter = EditorGUILayout.TextField(_searchFilter, EditorStyles.toolbarSearchField, GUILayout.Width(220));

                GUILayout.Space(10);
                _showErrors = GUILayout.Toggle(_showErrors, $"Errors ({_errorCount})", EditorStyles.toolbarButton, GUILayout.Width(100));
                _showWarnings = GUILayout.Toggle(_showWarnings, $"Warnings ({_warningCount})", EditorStyles.toolbarButton, GUILayout.Width(110));
                _showInfo = GUILayout.Toggle(_showInfo, $"Notices ({_infoCount})", EditorStyles.toolbarButton, GUILayout.Width(100));

                GUILayout.FlexibleSpace();
            }
        }

        private void DrawResultsList()
        {
            if (!_hasRun)
            {
                EditorGUILayout.HelpBox("Click 'Run Full Validation' to inspect all imported campaign assets and transitions.", MessageType.Info);
                return;
            }

            var filtered = _issues.Where(i =>
            {
                if (i.Severity == ValidationSeverity.Error && !_showErrors) return false;
                if (i.Severity == ValidationSeverity.Warning && !_showWarnings) return false;
                if (i.Severity == ValidationSeverity.Info && !_showInfo) return false;

                if (!string.IsNullOrEmpty(_searchFilter))
                {
                    return i.SubjectId.IndexOf(_searchFilter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                           i.Message.IndexOf(_searchFilter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                           i.Category.IndexOf(_searchFilter, StringComparison.OrdinalIgnoreCase) >= 0;
                }

                return true;
            }).ToList();

            EditorGUILayout.LabelField($"Showing {filtered.Count} of {_issues.Count} entries", EditorStyles.miniLabel);

            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);
            foreach (var issue in filtered)
            {
                DrawIssueRow(issue);
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawIssueRow(CampaignValidationIssue issue)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            using (new EditorGUILayout.HorizontalScope())
            {
                string iconPrefix = issue.Severity switch
                {
                    ValidationSeverity.Error => "[FAIL]",
                    ValidationSeverity.Warning => "[WARN]",
                    _ => "[INFO]"
                };

                GUI.color = issue.Severity switch
                {
                    ValidationSeverity.Error => Color.red,
                    ValidationSeverity.Warning => Color.yellow,
                    _ => Color.white
                };

                EditorGUILayout.LabelField(iconPrefix, EditorStyles.boldLabel, GUILayout.Width(55));
                GUI.color = Color.white;

                EditorGUILayout.LabelField($"[{issue.Category}]", EditorStyles.boldLabel, GUILayout.Width(110));
                EditorGUILayout.LabelField(issue.SubjectId, EditorStyles.boldLabel, GUILayout.Width(140));

                EditorGUILayout.LabelField(issue.Message, EditorStyles.wordWrappedLabel);

                if (issue.TargetAsset != null)
                {
                    if (GUILayout.Button("Select", GUILayout.Width(60)))
                    {
                        Selection.activeObject = issue.TargetAsset;
                        EditorGUIUtility.PingObject(issue.TargetAsset);
                    }
                }
            }
            EditorGUILayout.EndVertical();
        }

        public void RunValidation()
        {
            _issues.Clear();
            _errorCount = 0;
            _warningCount = 0;
            _infoCount = 0;

            if (_catalog == null)
            {
                FindCatalog();
                if (_catalog == null)
                {
                    _issues.Add(new CampaignValidationIssue(ValidationSeverity.Error, "Catalog", "Master", "CampaignCatalog.asset was not found. Please run 'Import Entire Campaign' first."));
                    _errorCount++;
                    _hasRun = true;
                    return;
                }
            }

            _catalog.Initialize();

            // 1. Audit Lands
            ValidateLands();

            // 2. Audit Quests
            ValidateQuests();

            // 3. Audit NPCs & Scripts
            ValidateNpcsAndScripts();

            // Summarize counts
            _errorCount = _issues.Count(i => i.Severity == ValidationSeverity.Error);
            _warningCount = _issues.Count(i => i.Severity == ValidationSeverity.Warning);
            _infoCount = _issues.Count(i => i.Severity == ValidationSeverity.Info);
            _hasRun = true;

            Debug.Log($"[CampaignValidationWindow] Audit complete. Errors: {_errorCount}, Warnings: {_warningCount}, Notices: {_infoCount}");
        }

        private void ValidateLands()
        {
            if (_catalog.AllLands == null || _catalog.AllLands.Count == 0)
            {
                _issues.Add(new CampaignValidationIssue(ValidationSeverity.Error, "Lands", "Catalog", "Catalog contains zero Land definitions."));
                return;
            }

            _issues.Add(new CampaignValidationIssue(ValidationSeverity.Info, "Lands", "Master", $"Registered Lands: {_catalog.AllLands.Count}"));

            if (!_catalog.HasLand(_catalog.startingLandId))
            {
                _issues.Add(new CampaignValidationIssue(ValidationSeverity.Error, "Lands", "StartLand", $"Starting land '{_catalog.startingLandId}' is not found in catalog."));
            }

            if (!_catalog.HasLand(_catalog.playerHubLandId))
            {
                _issues.Add(new CampaignValidationIssue(ValidationSeverity.Warning, "Lands", "HubLand", $"Hub land '{_catalog.playerHubLandId}' is not found in catalog."));
            }

            foreach (var land in _catalog.AllLands)
            {
                if (land == null)
                {
                    _issues.Add(new CampaignValidationIssue(ValidationSeverity.Error, "Lands", "Null", "Catalog contains a null Land reference."));
                    continue;
                }

                if (string.IsNullOrEmpty(land.landId))
                {
                    _issues.Add(new CampaignValidationIssue(ValidationSeverity.Error, "Lands", land.name, "Land has an empty landId.", land));
                }

                if (land.gridWidth <= 0 || land.gridHeight <= 0)
                {
                    _issues.Add(new CampaignValidationIssue(ValidationSeverity.Warning, "Lands", land.landId, $"Invalid grid dimensions ({land.gridWidth}x{land.gridHeight}).", land));
                }

                // Check exit destination
                if (!string.IsNullOrEmpty(land.exitLandId) && !_catalog.HasLand(land.exitLandId))
                {
                    _issues.Add(new CampaignValidationIssue(ValidationSeverity.Warning, "Transitions", land.landId, $"Exit destination '{land.exitLandId}' is not a registered land.", land));
                }

                // Check room templates
                if (!string.IsNullOrEmpty(land.sourceFileKey))
                {
                    if (land.roomTemplates == null || land.roomTemplates.Count == 0)
                    {
                        _issues.Add(new CampaignValidationIssue(ValidationSeverity.Warning, "Rooms", land.landId, $"Source file '{land.sourceFileKey}' resolved 0 RoomTemplates.", land));
                    }
                }
            }
        }

        private void ValidateQuests()
        {
            if (_catalog.AllQuests == null || _catalog.AllQuests.Count == 0)
            {
                _issues.Add(new CampaignValidationIssue(ValidationSeverity.Error, "Quests", "Catalog", "Catalog contains zero Quest definitions."));
                return;
            }

            _issues.Add(new CampaignValidationIssue(ValidationSeverity.Info, "Quests", "Master", $"Registered Quests: {_catalog.AllQuests.Count}"));

            foreach (var quest in _catalog.AllQuests)
            {
                if (quest == null)
                {
                    _issues.Add(new CampaignValidationIssue(ValidationSeverity.Error, "Quests", "Null", "Catalog contains a null Quest reference."));
                    continue;
                }

                if (string.IsNullOrEmpty(quest.questId))
                {
                    _issues.Add(new CampaignValidationIssue(ValidationSeverity.Error, "Quests", quest.name, "Quest has an empty questId.", quest));
                }

                if (quest.stages == null || quest.stages.Count == 0)
                {
                    _issues.Add(new CampaignValidationIssue(ValidationSeverity.Warning, "Quests", quest.questId, "Quest has no stages or objectives declared.", quest));
                }

                if (!string.IsNullOrEmpty(quest.nextQuestId) && !_catalog.HasQuest(quest.nextQuestId))
                {
                    _issues.Add(new CampaignValidationIssue(ValidationSeverity.Warning, "Quests", quest.questId, $"Chained nextQuestId '{quest.nextQuestId}' is not in catalog.", quest));
                }
            }
        }

        private void ValidateNpcsAndScripts()
        {
            if (_catalog.AllNpcs != null)
            {
                _issues.Add(new CampaignValidationIssue(ValidationSeverity.Info, "NPCs", "Master", $"Registered NPCs: {_catalog.AllNpcs.Count}"));

                foreach (var npc in _catalog.AllNpcs)
                {
                    if (npc == null) continue;

                    foreach (var dial in npc.dialogues)
                    {
                        if (!string.IsNullOrEmpty(dial.requiredLand) && !_catalog.HasLand(dial.requiredLand))
                        {
                            _issues.Add(new CampaignValidationIssue(ValidationSeverity.Warning, "NPC Dialogue", $"{npc.npcId}.{dial.dialogueId}", $"Required land '{dial.requiredLand}' is not in catalog.", npc));
                        }

                        if (!string.IsNullOrEmpty(dial.requiredQuest) && !_catalog.HasQuest(dial.requiredQuest))
                        {
                            _issues.Add(new CampaignValidationIssue(ValidationSeverity.Warning, "NPC Dialogue", $"{npc.npcId}.{dial.dialogueId}", $"Required quest '{dial.requiredQuest}' is not in catalog.", npc));
                        }

                        // Validate action commands
                        foreach (var act in dial.actions)
                        {
                            ValidateActionCommand(act, $"{npc.npcId}.{dial.dialogueId}", npc);
                        }
                    }
                }
            }

            if (_catalog.AllScripts != null)
            {
                _issues.Add(new CampaignValidationIssue(ValidationSeverity.Info, "Scripts", "Master", $"Registered Scripts: {_catalog.AllScripts.Count}"));

                foreach (var script in _catalog.AllScripts)
                {
                    if (script == null) continue;

                    foreach (var act in script.actions)
                    {
                        ValidateActionCommand(act, script.scriptId, script);
                    }
                }
            }
        }

        private void ValidateActionCommand(CampaignScriptAction act, string contextId, UnityEngine.Object asset)
        {
            if (act == null) return;

            if (act.action == "openland" || act.action == "gotoland")
            {
                if (!string.IsNullOrEmpty(act.value) && !_catalog.HasLand(act.value))
                {
                    _issues.Add(new CampaignValidationIssue(ValidationSeverity.Warning, "Transitions", contextId, $"Action '{act.action}' targets unregistered land '{act.value}'.", asset));
                }
            }
            else if (act.action == "stage" || act.action == "showstage")
            {
                if (!string.IsNullOrEmpty(act.value) && !_catalog.HasQuest(act.value))
                {
                    _issues.Add(new CampaignValidationIssue(ValidationSeverity.Warning, "Script Quest", contextId, $"Action '{act.action}' targets unregistered quest '{act.value}'.", asset));
                }
            }
        }

        private void ExportMarkdownReport()
        {
            string path = EditorUtility.SaveFilePanel("Export Campaign Validation Report", "docs/Campaign", "CAMPAIGN_VALIDATION_REPORT.md", "md");
            if (string.IsNullOrEmpty(path)) return;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# PFE Campaign Validation Report");
            sb.AppendLine($"Generated on: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();
            sb.AppendLine("## Summary");
            sb.AppendLine($"- **Errors:** {_errorCount}");
            sb.AppendLine($"- **Warnings:** {_warningCount}");
            sb.AppendLine($"- **Notices:** {_infoCount}");
            sb.AppendLine();
            sb.AppendLine("## Detailed Findings");
            sb.AppendLine("| Status | Category | Subject | Description |");
            sb.AppendLine("|---|---|---|---|");

            foreach (var issue in _issues)
            {
                string status = issue.Severity switch
                {
                    ValidationSeverity.Error => "❌ ERROR",
                    ValidationSeverity.Warning => "⚠️ WARN",
                    _ => "ℹ️ INFO"
                };
                sb.AppendLine($"| {status} | {issue.Category} | `{issue.SubjectId}` | {issue.Message} |");
            }

            File.WriteAllText(path, sb.ToString());
            Debug.Log($"[CampaignValidationWindow] Exported validation report to: {path}");
            EditorUtility.DisplayDialog("Report Exported", $"Validation report successfully saved to:\n{path}", "OK");
        }
    }
}
