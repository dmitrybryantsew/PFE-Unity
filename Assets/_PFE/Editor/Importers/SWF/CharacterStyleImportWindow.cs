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
    /// Editor window for importing character style layers (mane/tail/eye style variants,
    /// primary/secondary color layers) from the AS3 SWF file.
    /// </summary>
    public class CharacterStyleImportWindow : EditorWindow
    {
        static readonly string DefaultSwfPath = SourceImportPaths.AssetsSwfPath;

        static readonly string DefaultJpexsExportPath = SourceImportPaths.AssetsExportRoot;

        string _swfPath = DefaultSwfPath;
        string _jpexsExportPath = DefaultJpexsExportPath;

        Vector2 _scrollPos;
        List<string> _logMessages = new();
        bool _isRunning;

        SWFFile _parsedSwf;
        CharacterStyleLayerImporter.ImportResult _lastResult;

        int _spriteZoomFactor = 1;
        bool _showDiscoveredLayers = true;

        [MenuItem("PFE/Art/Import Character Style Layers")]
        public static void ShowWindow()
        {
            var window = GetWindow<CharacterStyleImportWindow>("Character Style Import");
            window.minSize = new Vector2(520, 550);
        }

        void OnGUI()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Character Style Layer Importer", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Imports individual color layers (h0/h1) and style variants from composite body parts.\n" +
                "Discovers mane, tail, forelock, and eye layer symbols by walking the SWF tree.\n" +
                "Each layer can be independently tinted for character customization.",
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
            bool spritesExist = Directory.Exists(Path.Combine(_jpexsExportPath, "sprites"));

            if (!swfExists)
                EditorGUILayout.HelpBox("SWF file not found!", MessageType.Error);
            if (!spritesExist)
                EditorGUILayout.HelpBox("JPEXS sprites/ folder not found!", MessageType.Warning);

            // ─── Settings ────────────────────────────────────────
            EditorGUILayout.Space(8);
            _spriteZoomFactor = EditorGUILayout.IntSlider("JPEXS Export Zoom", _spriteZoomFactor, 1, 10);
            if (_spriteZoomFactor > 1)
                EditorGUILayout.HelpBox(
                    $"Zoom {_spriteZoomFactor}x: JPEXS must have been re-exported at {_spriteZoomFactor}x zoom!",
                    MessageType.Warning);

            // ─── Actions ─────────────────────────────────────────
            EditorGUILayout.Space(8);
            EditorGUILayout.BeginHorizontal();

            GUI.enabled = !_isRunning && swfExists;
            if (GUILayout.Button("Discover Only (parse SWF)", GUILayout.Height(28)))
                RunDiscoverOnly();

            GUI.enabled = !_isRunning && swfExists && spritesExist;
            if (GUILayout.Button("Import Style Layers", GUILayout.Height(28)))
                RunFullImport();

            EditorGUILayout.EndHorizontal();
            GUI.enabled = true;

            // ─── Discovered Layers ──────────────────────────────
            if (_lastResult != null && _lastResult.Layers.Count > 0)
            {
                EditorGUILayout.Space(8);
                _showDiscoveredLayers = EditorGUILayout.Foldout(_showDiscoveredLayers, $"Discovered Layers ({_lastResult.Layers.Count})", true);
                if (_showDiscoveredLayers)
                {
                    EditorGUI.indentLevel++;
                    foreach (var layer in _lastResult.Layers)
                    {
                        string status = layer.FrameCount > 0
                            ? $"{layer.FrameCount} style frames"
                            : "no JPEXS export";
                        string tint = layer.TintChannel >= 0
                            ? $"tint ch.{layer.TintChannel}"
                            : "no tint";

                        EditorGUILayout.LabelField(
                            $"{layer.PartName}/{layer.LayerRole}",
                            $"sym {layer.ChildSymbolId} | {status} | {tint} | pivot ({layer.Pivot.x:F2},{layer.Pivot.y:F2})");
                    }
                    EditorGUI.indentLevel--;
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

                string spritesRoot = Path.Combine(_jpexsExportPath, "sprites");
                var result = new CharacterStyleLayerImporter.ImportResult();

                // Use reflection-free approach: just call discover logic
                // We call the full Import but it won't copy files if sprites/ doesn't exist
                Log("=== Discovery Only ===");
                _lastResult = CharacterStyleLayerImporter.Import(_jpexsExportPath, _parsedSwf, _spriteZoomFactor);

                foreach (string msg in _lastResult.Log) Log(msg);
                foreach (string warn in _lastResult.Warnings) Log($"[WARN] {warn}");

                Log($"=== Found {_lastResult.TotalLayersDiscovered} layers, {_lastResult.TotalSpritesImported} sprites ===");
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

                Log("=== Importing Style Layers ===");
                _lastResult = CharacterStyleLayerImporter.Import(_jpexsExportPath, _parsedSwf, _spriteZoomFactor);

                foreach (string msg in _lastResult.Log) Log(msg);
                foreach (string warn in _lastResult.Warnings) Log($"[WARN] {warn}");

                sw.Stop();
                Log($"=== Import complete: {_lastResult.TotalLayersDiscovered} layers, " +
                    $"{_lastResult.TotalSpritesImported} sprites in {sw.ElapsedMilliseconds}ms ===");

                // Summary
                var byPart = _lastResult.Layers.GroupBy(l => l.PartName);
                foreach (var group in byPart)
                {
                    var layers = group.ToList();
                    int totalFrames = layers.Sum(l => Math.Max(0, l.FrameCount));
                    Log($"  {group.Key}: {layers.Count} layers, {totalFrames} total frames");
                }

                // Step 2: Generate CharacterStyleData ScriptableObject
                Log("=== Generating CharacterStyleData ===");
                var styleData = CharacterStyleDataGenerator.Generate(_lastResult);
                if (styleData != null)
                    Log($"  Generated at: {UnityEditor.AssetDatabase.GetAssetPath(styleData)}");
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
            Debug.Log($"[StyleImport] {msg}");
        }
    }
}
#endif
