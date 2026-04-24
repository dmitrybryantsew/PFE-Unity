#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;
using PFE.Systems.Map.DataMigration;

namespace PFE.Editor
{
    /// <summary>
    /// Unified editor window for AllData.as parsing, importing, and querying.
    /// Replaces the scattered individual import windows.
    /// 
    /// Menu: PFE > Data > AllData Import and Query
    /// </summary>
    public class AllDataImportWindow : EditorWindow
    {
        private static readonly string DefaultPath = Importers.SourceImportPaths.AllDataAsPath;

        private string _sourcePath = "";
        private AllDataParser _parser;
        private Vector2 _scrollPos;

        // Query state
        private string _queryTag = "mat";
        private string _queryId = "";
        private ParsedEntry _queryResult;
        private List<ParsedEntry> _queryListResult;
        private bool _showListMode = false;

        [MenuItem("PFE/Data/AllData Import and Query")]
        public static void ShowWindow()
        {
            var window = GetWindow<AllDataImportWindow>("AllData Tools");
            window.minSize = new Vector2(550, 500);
            window._sourcePath = DefaultPath;
        }

        private void OnGUI()
        {
            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

            DrawParserSection();
            EditorGUILayout.Space(10);
            DrawImportSection();
            EditorGUILayout.Space(10);
            DrawQuerySection();

            EditorGUILayout.EndScrollView();
        }

        // ===== PARSER SECTION =====

        private void DrawParserSection()
        {
            EditorGUILayout.LabelField("1. Parse AllData.as", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            _sourcePath = EditorGUILayout.TextField("Path", _sourcePath);
            if (GUILayout.Button("...", GUILayout.Width(30)))
            {
                string path = EditorUtility.OpenFilePanel("Select AllData.as", "", "as");
                if (!string.IsNullOrEmpty(path))
                    _sourcePath = path;
            }
            EditorGUILayout.EndHorizontal();

            if (GUILayout.Button("Parse File", GUILayout.Height(25)))
            {
                _parser = new AllDataParser();
                _parser.LoadFile(_sourcePath);
            }

            if (_parser != null && _parser.IsLoaded)
            {
                EditorGUILayout.Space(5);
                EditorGUILayout.LabelField(_parser.GetSummary(), EditorStyles.wordWrappedLabel);

                // Show tag counts
                foreach (string tag in _parser.GetTagTypes())
                {
                    EditorGUILayout.LabelField($"  {tag}: {_parser.GetCount(tag)}");
                }
            }

            EditorGUILayout.EndVertical();
        }

        // ===== IMPORT SECTION =====

        private void DrawImportSection()
        {
            EditorGUILayout.LabelField("2. Import Data", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            bool canImport = _parser != null && _parser.IsLoaded;

            using (new EditorGUI.DisabledScope(!canImport))
            {
                if (GUILayout.Button("Import Material Render Data (for tile rendering)"))
                {
                    Importers.MaterialDataImporter.Import(_sourcePath,
                        "Assets/_PFE/Data/MaterialRenderDatabase.asset");
                }

                if (GUILayout.Button("Import Tile Forms (physics + overlays)"))
                {
                    // TileFormImporter uses its own JSON, not AllDataParser
                    EditorUtility.DisplayDialog("Tile Forms",
                        "Use PFE > Import Tile Forms JSON with tile_forms.json", "OK");
                }

                EditorGUILayout.Space(5);
                EditorGUILayout.LabelField("Existing importers (use menu PFE > Data):", EditorStyles.miniLabel);
                EditorGUILayout.LabelField("  Units, Weapons, Ammo, Effects, Perks", EditorStyles.miniLabel);
            }

            if (!canImport)
            {
                EditorGUILayout.HelpBox("Parse AllData.as first (step 1)", MessageType.Info);
            }

            EditorGUILayout.EndVertical();
        }

        // ===== QUERY SECTION =====

        private void DrawQuerySection()
        {
            EditorGUILayout.LabelField("3. Query Data", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            bool canQuery = _parser != null && _parser.IsLoaded;

            using (new EditorGUI.DisabledScope(!canQuery))
            {
                EditorGUILayout.BeginHorizontal();
                _queryTag = EditorGUILayout.TextField("Tag", _queryTag, GUILayout.Width(200));
                _queryId = EditorGUILayout.TextField("ID", _queryId, GUILayout.Width(200));

                if (GUILayout.Button("Search", GUILayout.Width(60)))
                {
                    if (string.IsNullOrEmpty(_queryId))
                    {
                        _queryListResult = _parser.GetAllEntries(_queryTag);
                        _queryResult = null;
                        _showListMode = true;
                    }
                    else
                    {
                        _queryResult = _parser.GetEntry(_queryTag, _queryId);
                        _queryListResult = null;
                        _showListMode = false;

                        // Also try with _ed suffix for mat entries
                        if (_queryResult == null && _queryTag == "mat")
                        {
                            _queryResult = _parser.GetEntry("mat", _queryId + "_ed1");
                            if (_queryResult == null)
                                _queryResult = _parser.GetEntry("mat", _queryId + "_ed2");
                        }
                    }
                }
                EditorGUILayout.EndHorizontal();

                // Show single result
                if (!_showListMode && _queryResult != null)
                {
                    EditorGUILayout.Space(5);
                    DrawEntry(_queryResult);
                }
                else if (!_showListMode && _queryResult == null && !string.IsNullOrEmpty(_queryId))
                {
                    EditorGUILayout.HelpBox($"No {_queryTag} with id='{_queryId}' found", MessageType.Warning);
                }

                // Show list result
                if (_showListMode && _queryListResult != null)
                {
                    EditorGUILayout.Space(5);
                    EditorGUILayout.LabelField($"Found {_queryListResult.Count} {_queryTag} entries:");
                    int shown = 0;
                    foreach (var entry in _queryListResult)
                    {
                        if (shown >= 50)
                        {
                            EditorGUILayout.LabelField($"  ... and {_queryListResult.Count - 50} more");
                            break;
                        }
                        DrawEntryCompact(entry);
                        shown++;
                    }
                }
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawEntry(ParsedEntry entry)
        {
            EditorGUILayout.LabelField($"<{entry.tagName} id='{entry.id}'>", EditorStyles.boldLabel);

            // Attributes
            foreach (var kvp in entry.attributes)
            {
                EditorGUILayout.LabelField($"  {kvp.Key} = '{kvp.Value}'");
            }

            // Children
            foreach (var child in entry.children)
            {
                string childLine = $"  <{child.tagName}";
                foreach (var kvp in child.attributes)
                {
                    childLine += $" {kvp.Key}='{kvp.Value}'";
                }
                if (!string.IsNullOrEmpty(child.innerText))
                {
                    childLine += $">{child.innerText}</{child.tagName}>";
                }
                else
                {
                    childLine += "/>";
                }
                EditorGUILayout.LabelField(childLine);
            }
        }

        private void DrawEntryCompact(ParsedEntry entry)
        {
            string line = $"  {entry.id}";
            string name = entry.GetAttr("n");
            if (!string.IsNullOrEmpty(name))
                line += $" ({name})";

            // Show key attributes based on tag type
            if (entry.tagName == "mat")
            {
                line += $"  ed={entry.GetAttr("ed")} mat={entry.GetAttr("mat")}";
                var main = entry.GetChild("main");
                if (main != null)
                    line += $" tex={main.GetAttr("tex")}";
            }
            else if (entry.tagName == "unit")
            {
                line += $"  hp={entry.GetAttr("hp")} fraction={entry.GetAttr("fraction")}";
            }
            else if (entry.tagName == "weapon")
            {
                line += $"  tip={entry.GetAttr("tip")}";
            }

            EditorGUILayout.LabelField(line);
        }
    }
}
#endif
