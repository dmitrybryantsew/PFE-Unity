#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using PFE.Editor.Importers.SWF;
using UnityEditor;
using UnityEngine;

namespace PFE.Editor.Importers
{
    /// <summary>
    /// Editor window: PFE > Art > Import Weapon Graphics
    ///
    /// Imports held-weapon sprites from the pfe JPEXS export, creates WeaponVisualDefinition
    /// ScriptableObjects, and wires them into matching WeaponDefinition assets.
    ///
    /// Pipeline:
    ///   1. Read pfe/symbolClass/symbols.csv — find vis{weaponId} symbols
    ///   2. Parse pfe/scripts/fe/AllData.as  — extract vis.@vweap and vis.@flare overrides
    ///   3. (Optional) Parse pfe.swf binary  — extract exact "shoot"/"reload"/"ready" frame labels
    ///   4. Copy frame PNGs → Assets/_PFE/Art/Weapons/Sprites/{symbolName}/f001.png ...
    ///   5. Configure TextureImporter (Sprite, PPU=100, correct pivot from SWF bounds)
    ///   6. Create WeaponVisualDefinition assets → Assets/_PFE/Data/Definitions/Weapons/Visual/
    ///   7. Wire WeaponDefinition.weaponVisual references in Assets/_PFE/Data/Resources/Weapons/
    /// </summary>
    public class WeaponGraphicsImportWindow : EditorWindow
    {
        // ── Default paths ──────────────────────────────────────────────────────
        static readonly string DefaultPfeRoot = SourceImportPaths.PfeRoot;
        static readonly string DefaultSwfPath = SourceImportPaths.PfeSwfPath;

        // ── State ──────────────────────────────────────────────────────────────
        string _pfeRoot  = DefaultPfeRoot;
        string _swfPath  = DefaultSwfPath;

        bool _useSwfForLabels = true;  // parse SWF binary for exact frame labels
        bool _isRunning;

        Vector2 _scrollPos;
        readonly List<string> _log = new();

        // ── Menu item ─────────────────────────────────────────────────────────
        [MenuItem("PFE/Art/Import Weapon Graphics")]
        public static void ShowWindow()
        {
            var win = GetWindow<WeaponGraphicsImportWindow>("Weapon Graphics Import");
            win.minSize = new Vector2(600f, 520f);
        }

        // ── GUI ───────────────────────────────────────────────────────────────
        void OnGUI()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Weapon Graphics Importer", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Copies held-weapon sprites from the JPEXS pfe export into Unity, " +
                "creates WeaponVisualDefinition assets (frame data, animation ranges), " +
                "and wires them into matching WeaponDefinition assets.\n\n" +
                "Sources: pfe/sprites/  ·  pfe/symbolClass/symbols.csv  ·  pfe/scripts/fe/AllData.as",
                MessageType.Info);

            EditorGUILayout.Space(6);

            // ── Source paths ─────────────────────────────────────────────────
            EditorGUILayout.LabelField("Source Paths", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            _pfeRoot = EditorGUILayout.TextField(
                new GUIContent("PFE Export Root",
                    "Path to the pfe/ folder produced by JPEXS export of the main pfe SWF.\n" +
                    "Must contain: sprites/, symbolClass/symbols.csv, scripts/fe/AllData.as"),
                _pfeRoot);
            if (GUILayout.Button("...", GUILayout.Width(30f)))
            {
                string folder = EditorUtility.OpenFolderPanel("Select pfe export folder", _pfeRoot, "");
                if (!string.IsNullOrEmpty(folder)) _pfeRoot = folder;
            }
            EditorGUILayout.EndHorizontal();

            if (!Directory.Exists(_pfeRoot))
                EditorGUILayout.HelpBox($"Folder not found: {_pfeRoot}", MessageType.Warning);

            EditorGUILayout.Space(4);

            // ── SWF binary (optional) ────────────────────────────────────────
            _useSwfForLabels = EditorGUILayout.Toggle(
                new GUIContent("Use SWF for Frame Labels",
                    "Parse the raw pfe.swf binary to get exact 'shoot', 'reload', 'ready' " +
                    "frame label positions.\n" +
                    "When disabled, default frame ranges are inferred from total frame count."),
                _useSwfForLabels);

            if (_useSwfForLabels)
            {
                EditorGUILayout.BeginHorizontal();
                _swfPath = EditorGUILayout.TextField(
                    new GUIContent("pfe.swf Path",
                        "Uncompressed (FWS) pfe.swf binary. Leave empty if you want to browse manually."),
                    _swfPath);
                if (GUILayout.Button("...", GUILayout.Width(30f)))
                {
                    string f = EditorUtility.OpenFilePanel("Select pfe.swf", Path.GetDirectoryName(_swfPath), "swf");
                    if (!string.IsNullOrEmpty(f)) _swfPath = f;
                }
                EditorGUILayout.EndHorizontal();

                if (!File.Exists(_swfPath))
                    EditorGUILayout.HelpBox(
                        $"SWF not found: {_swfPath}\nFrame labels will use default ranges.",
                        MessageType.Warning);
            }

            EditorGUILayout.Space(8);

            // ── Output paths (info only) ─────────────────────────────────────
            EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.TextField("Sprites",      WeaponSpriteImporter.SpritesOutputRoot);
                EditorGUILayout.TextField("Visual Defs",  WeaponSpriteImporter.VisualDefsRoot);
                EditorGUILayout.TextField("Weapon Defs",  WeaponSpriteImporter.WeaponDefsRoot);
            }

            EditorGUILayout.Space(8);

            // ── Run button ───────────────────────────────────────────────────
            bool canRun = !_isRunning && Directory.Exists(_pfeRoot);
            using (new EditorGUI.DisabledScope(!canRun))
            {
                if (GUILayout.Button("Import Weapon Graphics", GUILayout.Height(36f)))
                    RunImport();
            }

            // ── Log ──────────────────────────────────────────────────────────
            if (_log.Count > 0)
            {
                EditorGUILayout.Space(8);
                EditorGUILayout.LabelField("Log", EditorStyles.boldLabel);
                _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos, GUILayout.MaxHeight(280f));
                foreach (string line in _log)
                {
                    Color prev = GUI.contentColor;
                    if (line.StartsWith("WARN") || line.StartsWith("[WARN]"))
                        GUI.contentColor = new Color(1f, 0.8f, 0.2f);
                    EditorGUILayout.LabelField(line, EditorStyles.wordWrappedLabel);
                    GUI.contentColor = prev;
                }
                EditorGUILayout.EndScrollView();

                if (GUILayout.Button("Clear Log", GUILayout.Width(90f)))
                    _log.Clear();
            }
        }

        // ── Import ────────────────────────────────────────────────────────────
        void RunImport()
        {
            _log.Clear();
            _isRunning = true;

            string swfArg = (_useSwfForLabels && File.Exists(_swfPath)) ? _swfPath : "";
            _log.Add($"Starting import from: {_pfeRoot}");
            if (!string.IsNullOrEmpty(swfArg))
                _log.Add($"SWF frame labels: {swfArg}");
            else
                _log.Add("SWF not provided — using default frame ranges.");

            WeaponSpriteImporter.ImportResult result;
            try
            {
                result = WeaponSpriteImporter.Run(_pfeRoot, swfArg);
            }
            finally
            {
                _isRunning = false;
            }

            // Copy result log into the window
            foreach (string line in result.Log)     _log.Add(line);
            foreach (string warn in result.Warnings) _log.Add($"WARN: {warn}");

            _log.Add("─────────────────────────────────────────────");
            _log.Add($"Sprites imported : {result.SpritesImported}");
            _log.Add($"Visual defs      : {result.VisualDefsCreated}");
            _log.Add($"Weapons wired    : {result.WeaponDefsWired}");
            _log.Add($"Warnings         : {result.Warnings.Count}");

            Repaint();
        }
    }
}
#endif
