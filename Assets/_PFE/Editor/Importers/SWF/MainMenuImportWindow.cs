#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using PFE.Data.Definitions;
using UnityEditor;
using UnityEngine;

namespace PFE.Editor.Importers.SWF
{
    /// <summary>
    /// Editor window for importing main menu visual data from the AS3 SWF file.
    /// Runs the full pipeline: Parse SWF → Extract/Import Sprites → Generate Composition SO.
    /// </summary>
    public class MainMenuImportWindow : EditorWindow
    {
        static readonly string DefaultSwfPath = SourceImportPaths.AssetsSwfPath;

        static readonly string DefaultJpexsRoot = SourceImportPaths.AssetsExportRoot;

        string _swfPath = DefaultSwfPath;
        string _jpexsRoot = DefaultJpexsRoot;
        Vector2 _scrollPos;
        List<string> _logMessages = new();
        bool _isRunning;

        // Pipeline results
        SWFFile _parsedSwf;
        MainMenuSpriteImporter.ImportResult _importResult;
        MainMenuCompositionDefinition _compositionDef;

        // Step toggles
        bool _stepParse = true;
        bool _stepImport = true;
        bool _stepGenerate = true;

        [MenuItem("PFE/Art/Import Main Menu From SWF")]
        public static void ShowWindow()
        {
            var window = GetWindow<MainMenuImportWindow>("Main Menu Import");
            window.minSize = new Vector2(550, 500);
        }

        void OnGUI()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Main Menu SWF Importer", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Imports main menu background visuals from the AS3 assets.swf.\n" +
                "Extracts background layers, animated sprites, and generates a composition ScriptableObject.",
                MessageType.Info);
            EditorGUILayout.Space(4);

            // ─── Paths ──────────────────────────────────────────────
            EditorGUILayout.LabelField("Source Paths", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            _swfPath = EditorGUILayout.TextField("SWF File", _swfPath);
            if (GUILayout.Button("...", GUILayout.Width(30)))
            {
                string path = EditorUtility.OpenFilePanel("Select assets.swf",
                    Path.GetDirectoryName(_swfPath), "swf");
                if (!string.IsNullOrEmpty(path)) _swfPath = path;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            _jpexsRoot = EditorGUILayout.TextField("JPEXS Export", _jpexsRoot);
            if (GUILayout.Button("...", GUILayout.Width(30)))
            {
                string path = EditorUtility.OpenFolderPanel("Select JPEXS export folder",
                    _jpexsRoot, "");
                if (!string.IsNullOrEmpty(path)) _jpexsRoot = path;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);

            // ─── Step toggles ───────────────────────────────────────
            EditorGUILayout.LabelField("Pipeline Steps", EditorStyles.boldLabel);
            _stepParse = EditorGUILayout.Toggle("1. Parse SWF", _stepParse);
            _stepImport = EditorGUILayout.Toggle("2. Import Sprites", _stepImport);
            _stepGenerate = EditorGUILayout.Toggle("3. Generate Composition", _stepGenerate);

            EditorGUILayout.Space(8);

            // ─── Run button ─────────────────────────────────────────
            GUI.enabled = !_isRunning;
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Run Full Pipeline", GUILayout.Height(32)))
                RunPipeline();

            if (_compositionDef != null && GUILayout.Button("Open Preview", GUILayout.Height(32)))
                MainMenuCompositionPreviewWindow.ShowWindow();

            EditorGUILayout.EndHorizontal();
            GUI.enabled = true;

            // ─── Status ─────────────────────────────────────────────
            if (_parsedSwf != null)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField(
                    $"SWF: {_parsedSwf.Symbols.Count} sprites, {_parsedSwf.ShapeBounds.Count} shapes",
                    EditorStyles.miniLabel);
            }
            if (_importResult != null)
            {
                EditorGUILayout.LabelField(
                    $"Import: {_importResult.TotalSpritesImported} sprites, " +
                    $"{_importResult.Warnings.Count} warnings",
                    EditorStyles.miniLabel);
            }
            if (_compositionDef != null)
            {
                EditorGUILayout.LabelField(
                    $"Composition: {AssetDatabase.GetAssetPath(_compositionDef)}",
                    EditorStyles.miniLabel);
            }

            // ─── Log ────────────────────────────────────────────────
            if (_logMessages.Count > 0)
            {
                EditorGUILayout.Space(8);
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Log", EditorStyles.boldLabel);
                if (GUILayout.Button("Copy Log", GUILayout.Width(80)))
                {
                    GUIUtility.systemCopyBuffer = string.Join("\n", _logMessages);
                    Debug.Log("[MainMenuImport] Log copied to clipboard");
                }
                EditorGUILayout.EndHorizontal();
                _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos, GUILayout.MaxHeight(300));
                foreach (string msg in _logMessages)
                {
                    var style = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true };
                    if (msg.StartsWith("[WARN]"))
                        style.normal.textColor = new Color(1f, 0.7f, 0.2f);
                    else if (msg.StartsWith("[ERR]"))
                        style.normal.textColor = new Color(1f, 0.3f, 0.3f);
                    else if (msg.StartsWith("[OK]"))
                        style.normal.textColor = new Color(0.3f, 0.9f, 0.3f);
                    EditorGUILayout.LabelField(msg, style);
                }
                EditorGUILayout.EndScrollView();
            }
        }

        void RunPipeline()
        {
            _isRunning = true;
            _logMessages.Clear();

            try
            {
                // ─── Step 1: Parse SWF ──────────────────────────────
                if (_stepParse)
                {
                    Log("[...] Parsing SWF...");
                    EditorUtility.DisplayProgressBar("Main Menu Import", "Parsing SWF...", 0.1f);

                    if (!File.Exists(_swfPath))
                    {
                        Log($"[ERR] SWF file not found: {_swfPath}");
                        return;
                    }

                    var parser = new SWFParser();
                    _parsedSwf = parser.Parse(_swfPath);
                    Log($"[OK] Parsed SWF: {_parsedSwf.Symbols.Count} sprites, " +
                        $"{_parsedSwf.ShapeBounds.Count} shapes");
                }

                if (_parsedSwf == null)
                {
                    Log("[ERR] No parsed SWF data. Run step 1 first.");
                    return;
                }

                // ─── Step 2: Import Sprites ─────────────────────────
                if (_stepImport)
                {
                    Log("[...] Importing sprites...");
                    EditorUtility.DisplayProgressBar("Main Menu Import", "Importing sprites...", 0.4f);

                    _importResult = MainMenuSpriteImporter.Import(_jpexsRoot, _parsedSwf);

                    foreach (string msg in _importResult.Log)
                        Log($"  {msg}");
                    foreach (string warn in _importResult.Warnings)
                        Log($"[WARN] {warn}");

                    Log($"[OK] Imported {_importResult.TotalSpritesImported} sprites " +
                        $"({_importResult.SingleSprites.Count} singles, " +
                        $"{_importResult.FrameSequences.Count} sequences)");
                }

                if (_importResult == null)
                {
                    Log("[ERR] No import result. Run step 2 first.");
                    return;
                }

                // ─── Step 3: Generate Composition ───────────────────
                if (_stepGenerate)
                {
                    Log("[...] Generating composition...");
                    EditorUtility.DisplayProgressBar("Main Menu Import", "Generating composition...", 0.8f);

                    _compositionDef = MainMenuCompositionGenerator.Generate(_parsedSwf, _importResult);
                    Log($"[OK] Composition generated at {AssetDatabase.GetAssetPath(_compositionDef)}");

                    // Validate
                    int missing = 0;
                    if (def_null(_compositionDef.sky)) { Log("[WARN] Missing: sky sprite"); missing++; }
                    if (def_null(_compositionDef.cityScene)) { Log("[WARN] Missing: cityScene sprite"); missing++; }
                    if (def_null(_compositionDef.logo)) { Log("[WARN] Missing: logo sprite"); missing++; }
                    if (def_null(_compositionDef.magicKrug)) { Log("[WARN] Missing: magicKrug sprite"); missing++; }
                    if (_compositionDef.eyeFrames == null || _compositionDef.eyeFrames.Length == 0)
                    { Log("[WARN] Missing: eye blink frames"); missing++; }
                    if (_compositionDef.hornSparkFrames == null || _compositionDef.hornSparkFrames.Length == 0)
                    { Log("[WARN] Missing: horn spark frames"); missing++; }
                    if (_compositionDef.pistolSparkFrames == null || _compositionDef.pistolSparkFrames.Length == 0)
                    { Log("[WARN] Missing: pistol spark frames"); missing++; }

                    if (missing == 0)
                        Log("[OK] All required sprites present!");
                    else
                        Log($"[WARN] {missing} sprite references missing — check import warnings above");
                }

                Log("[OK] Pipeline complete!");
            }
            catch (Exception ex)
            {
                Log($"[ERR] Pipeline failed: {ex.Message}");
                Debug.LogException(ex);
            }
            finally
            {
                _isRunning = false;
                EditorUtility.ClearProgressBar();
                Repaint();
            }
        }

        static bool def_null(Sprite s) => s == null;

        void Log(string msg)
        {
            _logMessages.Add(msg);
            if (msg.StartsWith("[ERR]"))
                Debug.LogError($"[MainMenuImport] {msg}");
        }
    }
}
#endif
