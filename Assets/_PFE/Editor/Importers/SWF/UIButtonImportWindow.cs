#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace PFE.Editor.Importers.SWF
{
    /// <summary>
    /// Editor window for importing UI button and control sprites from the AS3 SWF.
    /// Walks the SWF tree to discover button backgrounds, then imports from JPEXS export.
    /// </summary>
    public class UIButtonImportWindow : EditorWindow
    {
        static readonly string DefaultSwfPath = SourceImportPaths.AssetsSwfPath;

        static readonly string DefaultJpexsExportPath = SourceImportPaths.AssetsExportRoot;

        string _swfPath = DefaultSwfPath;
        string _jpexsExportPath = DefaultJpexsExportPath;

        Vector2 _scrollPos;
        List<string> _logMessages = new();
        bool _isRunning;

        SWFFile _parsedSwf;
        UIButtonSpriteImporter.ImportResult _lastResult;

        bool _showDiscovered = true;
        bool _showMissing = true;

        [MenuItem("PFE/Art/Import UI Button Sprites")]
        public static void ShowWindow()
        {
            var window = GetWindow<UIButtonImportWindow>("UI Button Import");
            window.minSize = new Vector2(520, 550);
        }

        void OnGUI()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("UI Button Sprite Importer", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Imports UI sprites for menu buttons, dialog controls, checkboxes, sliders, etc.\n" +
                "Walks the SWF tree to discover button background (.fon) symbols,\n" +
                "then imports from JPEXS DefineSprite and DefineButton2 exports.",
                MessageType.Info);
            EditorGUILayout.Space(4);

            // ─── Paths ───────────────────────────────────────────
            EditorGUILayout.LabelField("Source Paths", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            _swfPath = EditorGUILayout.TextField("SWF File", _swfPath);
            if (GUILayout.Button("...", GUILayout.Width(30)))
            {
                string path = EditorUtility.OpenFilePanel("Select assets.swf", Path.GetDirectoryName(_swfPath), "swf");
                if (!string.IsNullOrEmpty(path)) _swfPath = path;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            _jpexsExportPath = EditorGUILayout.TextField("JPEXS Export", _jpexsExportPath);
            if (GUILayout.Button("...", GUILayout.Width(30)))
            {
                string path = EditorUtility.OpenFolderPanel("Select JPEXS export folder", _jpexsExportPath, "");
                if (!string.IsNullOrEmpty(path)) _jpexsExportPath = path;
            }
            EditorGUILayout.EndHorizontal();

            bool swfExists = File.Exists(_swfPath);
            bool jpexsExists = Directory.Exists(_jpexsExportPath);

            if (!swfExists)
                EditorGUILayout.HelpBox("SWF file not found!", MessageType.Error);
            if (!jpexsExists)
                EditorGUILayout.HelpBox("JPEXS export folder not found!", MessageType.Error);

            // ─── Actions ─────────────────────────────────────────
            EditorGUILayout.Space(8);
            EditorGUILayout.BeginHorizontal();

            GUI.enabled = !_isRunning && swfExists;
            if (GUILayout.Button("Discover Only", GUILayout.Height(28)))
                RunDiscoverOnly();

            GUI.enabled = !_isRunning && swfExists && jpexsExists;
            if (GUILayout.Button("Import UI Sprites", GUILayout.Height(28)))
                RunFullImport();

            EditorGUILayout.EndHorizontal();
            GUI.enabled = true;

            // ─── Discovered Elements ─────────────────────────────
            if (_lastResult != null && _lastResult.Elements.Count > 0)
            {
                EditorGUILayout.Space(8);

                // Elements with exports
                var withExport = _lastResult.Elements.Where(e => e.HasJpexsExport).ToList();
                var withoutExport = _lastResult.Elements.Where(e => !e.HasJpexsExport).ToList();

                _showDiscovered = EditorGUILayout.Foldout(_showDiscovered,
                    $"Importable ({withExport.Count})", true);
                if (_showDiscovered)
                {
                    EditorGUI.indentLevel++;
                    foreach (var group in withExport.GroupBy(e => e.Category))
                    {
                        EditorGUILayout.LabelField(group.Key, EditorStyles.miniBoldLabel);
                        foreach (var elem in group)
                        {
                            EditorGUILayout.LabelField(
                                $"  {elem.Name}",
                                $"sym {elem.SymbolId} | {elem.FrameCount} frames | {elem.SourceType}");
                        }
                    }
                    EditorGUI.indentLevel--;
                }

                if (withoutExport.Count > 0)
                {
                    _showMissing = EditorGUILayout.Foldout(_showMissing,
                        $"No JPEXS Export ({withoutExport.Count}) — may need manual export or are vector-only", true);
                    if (_showMissing)
                    {
                        EditorGUI.indentLevel++;
                        foreach (var elem in withoutExport)
                        {
                            EditorGUILayout.LabelField(
                                $"  {elem.Name}",
                                $"sym {elem.SymbolId} | {elem.SourceType}");
                        }
                        EditorGUI.indentLevel--;
                    }
                }
            }

            // ─── Log ─────────────────────────────────────────────
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Log", EditorStyles.boldLabel);

            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos, GUILayout.ExpandHeight(true));
            foreach (string msg in _logMessages)
            {
                if (msg.StartsWith("[ERR]"))
                    EditorGUILayout.HelpBox(msg, MessageType.Error);
                else if (msg.StartsWith("[WARN]"))
                    EditorGUILayout.HelpBox(msg, MessageType.Warning);
                else
                    EditorGUILayout.LabelField(msg, EditorStyles.miniLabel);
            }
            EditorGUILayout.EndScrollView();

            if (_logMessages.Count > 0)
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Copy Log"))
                    GUIUtility.systemCopyBuffer = string.Join("\n", _logMessages);
                if (GUILayout.Button("Clear Log"))
                    _logMessages.Clear();
                EditorGUILayout.EndHorizontal();
            }
        }

        void RunDiscoverOnly()
        {
            _logMessages.Clear();
            _isRunning = true;
            try
            {
                ParseSWFIfNeeded();
                if (_parsedSwf == null) return;

                Log("=== Discovery Only ===");
                _lastResult = UIButtonSpriteImporter.Import(_jpexsExportPath, _parsedSwf);

                foreach (string msg in _lastResult.Log) Log(msg);
                foreach (string warn in _lastResult.Warnings) Log($"[WARN] {warn}");

                var withExport = _lastResult.Elements.Count(e => e.HasJpexsExport);
                var withoutExport = _lastResult.Elements.Count - withExport;
                Log($"=== {_lastResult.Elements.Count} elements: {withExport} importable, {withoutExport} missing export ===");
            }
            catch (Exception e)
            {
                Log($"[ERR] {e.Message}");
                Debug.LogException(e);
            }
            finally
            {
                _isRunning = false;
                Repaint();
            }
        }

        void RunFullImport()
        {
            _logMessages.Clear();
            _isRunning = true;
            var sw = Stopwatch.StartNew();

            try
            {
                ParseSWFIfNeeded();
                if (_parsedSwf == null) return;

                Log("=== Importing UI Sprites ===");
                _lastResult = UIButtonSpriteImporter.Import(_jpexsExportPath, _parsedSwf);

                foreach (string msg in _lastResult.Log) Log(msg);
                foreach (string warn in _lastResult.Warnings) Log($"[WARN] {warn}");

                sw.Stop();
                Log($"=== Import complete: {_lastResult.TotalSpritesImported} sprites " +
                    $"from {_lastResult.TotalElementsDiscovered} elements in {sw.ElapsedMilliseconds}ms ===");

                // Summary by category
                foreach (var group in _lastResult.Elements.GroupBy(e => e.Category))
                {
                    int imported = group.Count(e => e.HasJpexsExport);
                    int total = group.Count();
                    Log($"  {group.Key}: {imported}/{total} imported");
                }
            }
            catch (Exception e)
            {
                Log($"[ERR] Import failed: {e.Message}");
                Debug.LogException(e);
            }
            finally
            {
                _isRunning = false;
                Repaint();
            }
        }

        void ParseSWFIfNeeded()
        {
            if (_parsedSwf != null) return;

            Log($"Parsing SWF: {_swfPath}");
            var sw = Stopwatch.StartNew();
            var parser = new SWFParser();
            _parsedSwf = parser.Parse(_swfPath);
            sw.Stop();
            Log($"Parsed {_parsedSwf.Symbols.Count} symbols in {sw.ElapsedMilliseconds}ms");
        }

        void Log(string msg)
        {
            _logMessages.Add(msg);
            Debug.Log($"[UIImport] {msg}");
        }
    }
}
#endif
