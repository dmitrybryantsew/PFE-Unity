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
    /// Editor window for importing character animation data from the AS3 SWF file.
    /// Parses the SWF, imports sprites from JPEXS export, and generates animation ScriptableObjects.
    /// </summary>
    public class SWFCharacterImportWindow : EditorWindow
    {
        static readonly string DefaultSwfPath = SourceImportPaths.AssetsSwfPath;

        static readonly string DefaultJpexsExportPath = SourceImportPaths.AssetsExportRoot;

        string _swfPath = DefaultSwfPath;
        string _jpexsExportPath = DefaultJpexsExportPath;

        Vector2 _scrollPos;
        List<string> _logMessages = new();
        bool _isRunning;

        // Parse results for inspection
        SWFFile _parsedSwf;
        int _parsedSymbolCount;
        int _parsedShapeCount;

        // Import step toggles
        bool _stepParseSWF = true;
        bool _stepImportSprites = true;
        bool _stepGenerateData = true;
        bool _stepValidateOnly;

        // Sprite import settings
        int _spriteZoomFactor = 1;

        [MenuItem("PFE/Art/Import Character From SWF")]
        public static void ShowWindow()
        {
            var window = GetWindow<SWFCharacterImportWindow>("SWF Character Import");
            window.minSize = new Vector2(500, 600);
        }

        void OnGUI()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("SWF Character Importer", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Imports character animation data from the AS3 assets.swf file.\n" +
                "Reads SWF binary for placement transforms, uses JPEXS PNG export for sprite art.",
                MessageType.Info);
            EditorGUILayout.Space(4);

            // ─── Paths ───────────────────────────────────────────
            EditorGUILayout.LabelField("Source Paths", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            _swfPath = EditorGUILayout.TextField("SWF File", _swfPath);
            if (GUILayout.Button("...", GUILayout.Width(30)))
            {
                string path = EditorUtility.OpenFilePanel("Select assets.swf", Path.GetDirectoryName(_swfPath), "swf");
                if (!string.IsNullOrEmpty(path))
                    _swfPath = path;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            _jpexsExportPath = EditorGUILayout.TextField("JPEXS Export", _jpexsExportPath);
            if (GUILayout.Button("...", GUILayout.Width(30)))
            {
                string path = EditorUtility.OpenFolderPanel("Select JPEXS export folder", _jpexsExportPath, "");
                if (!string.IsNullOrEmpty(path))
                    _jpexsExportPath = path;
            }
            EditorGUILayout.EndHorizontal();

            // Validation
            bool swfExists = File.Exists(_swfPath);
            bool jpexsExists = Directory.Exists(_jpexsExportPath);
            bool spritesExist = Directory.Exists(Path.Combine(_jpexsExportPath, "sprites"));

            if (!swfExists)
                EditorGUILayout.HelpBox("SWF file not found!", MessageType.Error);
            if (!jpexsExists)
                EditorGUILayout.HelpBox("JPEXS export folder not found!", MessageType.Error);
            else if (!spritesExist)
                EditorGUILayout.HelpBox("JPEXS export folder has no 'sprites' subfolder!", MessageType.Warning);

            // ─── Steps ───────────────────────────────────────────
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Import Steps", EditorStyles.boldLabel);

            _stepParseSWF = EditorGUILayout.ToggleLeft("1. Parse SWF (extract timelines, placements, bounds)", _stepParseSWF);
            _stepImportSprites = EditorGUILayout.ToggleLeft("2. Import Sprites (copy PNGs, set pivots, configure import settings)", _stepImportSprites);
            _stepGenerateData = EditorGUILayout.ToggleLeft("3. Generate Animation Data (create ScriptableObjects)", _stepGenerateData);

            EditorGUILayout.Space(4);
            _stepValidateOnly = EditorGUILayout.ToggleLeft("Validate Only (parse + log, don't import)", _stepValidateOnly);

            EditorGUILayout.Space(4);
            _spriteZoomFactor = EditorGUILayout.IntSlider(
                "JPEXS Export Zoom", _spriteZoomFactor, 1, 10);
            if (_spriteZoomFactor > 1)
                EditorGUILayout.HelpBox(
                    $"Zoom {_spriteZoomFactor}x: You MUST re-export from JPEXS at {_spriteZoomFactor}x zoom BEFORE importing!\n" +
                    $"JPEXS CLI: ffdec.bat -export sprite <outdir> assets.swf -zoom {_spriteZoomFactor}\n" +
                    $"PPU will be set to {100 * _spriteZoomFactor} so visual size stays the same but with higher resolution.\n" +
                    $"Setting zoom here without re-exporting from JPEXS will make sprites {_spriteZoomFactor}x too small!",
                    MessageType.Warning);

            // ─── Actions ─────────────────────────────────────────
            EditorGUILayout.Space(8);
            GUI.enabled = !_isRunning && swfExists;

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Parse SWF Only", GUILayout.Height(28)))
                RunParseSWF();

            GUI.enabled = !_isRunning && swfExists && spritesExist;
            if (GUILayout.Button("Run Full Import", GUILayout.Height(28)))
                RunFullImport();
            EditorGUILayout.EndHorizontal();

            GUI.enabled = !_isRunning && _parsedSwf != null;
            if (GUILayout.Button("Inspect Parsed Data", GUILayout.Height(24)))
                InspectParsedData();

            GUI.enabled = true;

            // ─── Parse Results ───────────────────────────────────
            if (_parsedSwf != null)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Parse Results", EditorStyles.boldLabel);
                EditorGUILayout.LabelField($"  Version: {_parsedSwf.Version}");
                EditorGUILayout.LabelField($"  Frame Rate: {_parsedSwf.FrameRate}");
                EditorGUILayout.LabelField($"  Symbols: {_parsedSymbolCount}");
                EditorGUILayout.LabelField($"  Shape Bounds: {_parsedShapeCount}");

                // Show known player symbols found
                int foundStates = 0;
                foreach (var kvp in AS3StateMapping.StateLabels)
                {
                    if (_parsedSwf.Symbols.ContainsKey(kvp.Value))
                        foundStates++;
                }
                EditorGUILayout.LabelField($"  Player States Found: {foundStates}/{AS3StateMapping.StateLabels.Count}");

                bool osnFound = _parsedSwf.Symbols.ContainsKey(AS3StateMapping.OsnRootSymbolId);
                EditorGUILayout.LabelField($"  OSN Root (3679): {(osnFound ? "Found" : "NOT FOUND")}");
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
                if (GUILayout.Button("Copy Log to Clipboard"))
                {
                    GUIUtility.systemCopyBuffer = string.Join("\n", _logMessages);
                    Debug.Log("[SWFImport] Log copied to clipboard");
                }
                if (GUILayout.Button("Clear Log"))
                    _logMessages.Clear();
                EditorGUILayout.EndHorizontal();
            }
        }

        void RunParseSWF()
        {
            _logMessages.Clear();
            _isRunning = true;
            try
            {
                Log($"Parsing SWF: {_swfPath}");
                var sw = Stopwatch.StartNew();

                var parser = new SWFParser();
                _parsedSwf = parser.Parse(_swfPath);

                sw.Stop();
                _parsedSymbolCount = _parsedSwf.Symbols.Count;
                _parsedShapeCount = _parsedSwf.ShapeBounds.Count;

                Log($"Parsed in {sw.ElapsedMilliseconds}ms");
                Log($"  Symbols: {_parsedSymbolCount}");
                Log($"  Shape bounds: {_parsedShapeCount}");
                Log($"  Stage: {_parsedSwf.StageRect}");
                Log($"  FPS: {_parsedSwf.FrameRate}");
                Log($"  Version: {_parsedSwf.Version}");

                ValidatePlayerSymbols();
            }
            catch (Exception e)
            {
                Log($"[ERR] Parse failed: {e.Message}");
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
            var totalSw = Stopwatch.StartNew();

            try
            {
                // Step 1: Parse SWF
                if (_stepParseSWF || _parsedSwf == null)
                {
                    Log("=== Step 1: Parsing SWF ===");
                    var sw = Stopwatch.StartNew();

                    var parser = new SWFParser();
                    _parsedSwf = parser.Parse(_swfPath);

                    sw.Stop();
                    _parsedSymbolCount = _parsedSwf.Symbols.Count;
                    _parsedShapeCount = _parsedSwf.ShapeBounds.Count;
                    Log($"  Parsed {_parsedSymbolCount} symbols, {_parsedShapeCount} shapes in {sw.ElapsedMilliseconds}ms");

                    ValidatePlayerSymbols();
                }

                if (_stepValidateOnly)
                {
                    Log("=== Validate-only mode, stopping here ===");
                    return;
                }

                // Step 1.5: Extract head armor variant symbols from morda_armor(273)
                CharacterAnimationDataGenerator.HeadArmorVariantData headVariants = null;
                if (_parsedSwf != null)
                {
                    headVariants = CharacterAnimationDataGenerator.ExtractHeadArmorVariants(_parsedSwf);
                    if (headVariants.AllVariantSymbolIds.Count > 0)
                        Log($"  Found {headVariants.AllVariantSymbolIds.Count} head armor variant symbols to import");
                }

                // Step 2: Import Sprites
                CharacterSpriteImporter.ImportResult spriteResult = null;
                if (_stepImportSprites)
                {
                    Log("=== Step 2: Importing Sprites ===");
                    var sw = Stopwatch.StartNew();

                    // Include head armor variant symbols in the import
                    HashSet<int> extraSymbolIds = headVariants?.AllVariantSymbolIds;
                    spriteResult = CharacterSpriteImporter.Import(_jpexsExportPath, _parsedSwf,
                        extraSymbolIds: extraSymbolIds, zoomFactor: _spriteZoomFactor);

                    sw.Stop();
                    Log($"  Imported {spriteResult.TotalSpritesImported} sprites from {spriteResult.TotalSymbolsProcessed} symbols in {sw.ElapsedMilliseconds}ms");

                    foreach (string warn in spriteResult.Warnings)
                        Log($"[WARN] {warn}");
                }

                // Step 3: Generate Animation Data
                if (_stepGenerateData && spriteResult != null)
                {
                    Log("=== Step 3: Generating Animation Data ===");
                    var sw = Stopwatch.StartNew();

                    var genResult = CharacterAnimationDataGenerator.Generate(_parsedSwf, spriteResult, headVariants);

                    sw.Stop();
                    Log($"  Generated {genResult.StatesGenerated} states, {genResult.PartsRegistered} parts in {sw.ElapsedMilliseconds}ms");
                    Log($"  Asset: {AssetDatabase.GetAssetPath(genResult.Asset)}");

                    foreach (string warn in genResult.Warnings)
                        Log($"[WARN] {warn}");
                }

                totalSw.Stop();
                Log($"=== Import complete in {totalSw.ElapsedMilliseconds}ms ===");
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

        void ValidatePlayerSymbols()
        {
            // Check osn root
            if (_parsedSwf.Symbols.TryGetValue(AS3StateMapping.OsnRootSymbolId, out var osn))
            {
                Log($"  OSN root (3679): {osn.Frames.Count} frames");

                // Check frame labels match expected state mapping
                foreach (var frame in osn.Frames)
                {
                    if (frame.Label != null)
                        Log($"    Frame {frame.FrameNumber}: label='{frame.Label}', placements={frame.Placements.Count}");
                }
            }
            else
            {
                Log("[WARN] OSN root symbol 3679 not found!");
            }

            // Check each state body symbol
            int found = 0, missing = 0;
            foreach (var kvp in AS3StateMapping.StateLabels)
            {
                if (_parsedSwf.Symbols.TryGetValue(kvp.Value, out var sym))
                {
                    found++;
                    Log($"  State '{kvp.Key}' (symbol {kvp.Value}): {sym.Frames.Count} frames");

                    // Validate frame 1 placements
                    if (sym.Frames.Count > 0)
                    {
                        var f1 = sym.Frames[0];
                        var partNames = new List<string>();
                        foreach (var p in f1.Placements)
                        {
                            if (AS3StateMapping.BodyPartNames.TryGetValue(p.CharacterId, out string name))
                                partNames.Add(name);
                            else
                                partNames.Add($"sym{p.CharacterId}");
                        }
                        Log($"    Frame 1 parts: [{string.Join(", ", partNames)}]");
                    }
                }
                else
                {
                    missing++;
                    Log($"[WARN] State '{kvp.Key}' body symbol {kvp.Value} not found");
                }
            }
            Log($"  States: {found} found, {missing} missing");

            // Check body part bounds for pivot data (shape bounds + computed sprite bounds)
            int shapeBoundsFound = 0;
            int computedBoundsFound = 0;
            int noBoundsCount = 0;
            foreach (var kvp in AS3StateMapping.BodyPartNames)
            {
                int symId = kvp.Key;
                string partName = kvp.Value;
                if (_parsedSwf.ShapeBounds.ContainsKey(symId))
                {
                    shapeBoundsFound++;
                    var b = _parsedSwf.ShapeBounds[symId];
                    Log($"  {partName} (sym{symId}): shape bounds ({b.x:F1}, {b.y:F1}, {b.width:F1}x{b.height:F1})");
                }
                else if (_parsedSwf.Symbols.TryGetValue(symId, out var sym) &&
                         sym.Bounds.width > 0 && sym.Bounds.height > 0)
                {
                    computedBoundsFound++;
                    var b = sym.Bounds;
                    Log($"  {partName} (sym{symId}): computed bounds ({b.x:F1}, {b.y:F1}, {b.width:F1}x{b.height:F1})");
                }
                else
                {
                    noBoundsCount++;
                    Log($"[WARN] {partName} (sym{symId}): NO bounds — pivot will default to center");
                }
            }
            Log($"  Body part bounds: {shapeBoundsFound} shape, {computedBoundsFound} computed, {noBoundsCount} missing");
        }

        void InspectParsedData()
        {
            if (_parsedSwf == null) return;

            Log("=== Detailed Symbol Inspection ===");

            // Inspect the run state as a reference
            if (_parsedSwf.Symbols.TryGetValue(3655, out var runSym))
            {
                Log($"RUN (symbol 3655): {runSym.Frames.Count} frames");
                for (int i = 0; i < runSym.Frames.Count; i++)
                {
                    var frame = runSym.Frames[i];
                    Log($"  Frame {frame.FrameNumber}{(frame.Label != null ? $" [{frame.Label}]" : "")}:");
                    foreach (var p in frame.Placements)
                    {
                        string name = AS3StateMapping.BodyPartNames.TryGetValue(p.CharacterId, out var n) ? n : $"sym{p.CharacterId}";
                        Log($"    depth={p.Depth} {name}(sym{p.CharacterId}) pos=({p.Position.x:F1},{p.Position.y:F1}) rot={p.Rotation:F1} scale=({p.Scale.x:F2},{p.Scale.y:F2})");
                    }
                }
            }

            // Check which symbols are DefineSprite vs DefineShape and their bounds
            Log("=== Bounds Available ===");
            foreach (var kvp in AS3StateMapping.BodyPartNames)
            {
                bool hasShape = _parsedSwf.ShapeBounds.ContainsKey(kvp.Key);
                bool hasSprite = _parsedSwf.Symbols.ContainsKey(kvp.Key);
                string boundsInfo = "none";
                if (hasShape)
                {
                    var b = _parsedSwf.ShapeBounds[kvp.Key];
                    boundsInfo = $"shape ({b.x:F1},{b.y:F1} {b.width:F1}x{b.height:F1})";
                }
                else if (hasSprite && _parsedSwf.Symbols[kvp.Key].Bounds.width > 0)
                {
                    var b = _parsedSwf.Symbols[kvp.Key].Bounds;
                    boundsInfo = $"computed ({b.x:F1},{b.y:F1} {b.width:F1}x{b.height:F1})";
                }
                Log($"  {kvp.Value} (sym{kvp.Key}): sprite={hasSprite}, bounds={boundsInfo}");
            }

            Repaint();
        }

        void Log(string message)
        {
            _logMessages.Add(message);
            Debug.Log($"[SWFImport] {message}");
        }
    }
}
#endif
