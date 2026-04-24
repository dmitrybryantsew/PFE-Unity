#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PFE.Data.Definitions;
using UnityEditor;
using UnityEngine;

namespace PFE.Editor.Importers.SWF
{
    /// <summary>
    /// Imports held-weapon sprites from the original pfe SWF JPEXS export into Unity.
    ///
    /// Source layout (JPEXS export of pfe main SWF):
    ///   pfeRoot/sprites/DefineSprite_{symbolId}_{symbolName}/1.png, 2.png, ... N.png
    ///   pfeRoot/symbolClass/symbols.csv  — semicolon-separated: symbolId;"symbolName"
    ///   pfeRoot/scripts/fe/AllData.as    — weapon XML for vweap/flare overrides
    ///
    /// Output layout (Unity project):
    ///   Assets/_PFE/Art/Weapons/Sprites/{symbolName}/f001.png ...
    ///   Assets/_PFE/Data/Definitions/Weapons/Visual/{symbolName}.asset
    ///
    /// Frame label data is extracted from the pfe SWF binary (pfe.swf) when provided.
    /// Without it, default frame ranges are inferred from total frame count.
    /// </summary>
    public static class WeaponSpriteImporter
    {
        // ── Paths ─────────────────────────────────────────────────────────────
        public const string SpritesOutputRoot = "Assets/_PFE/Art/Weapons/Sprites";
        public const string VisualDefsRoot    = "Assets/_PFE/Data/Definitions/Weapons/Visual";
        public const string WeaponDefsRoot    = "Assets/_PFE/Data/Resources/Weapons";
        const int PixelsPerUnit = 100;

        // ── Result ────────────────────────────────────────────────────────────
        public class ImportResult
        {
            public int SpritesImported;
            public int VisualDefsCreated;
            public int WeaponDefsWired;
            public List<string> Warnings = new();
            public List<string> Log      = new();

            public void Info(string msg)    { Log.Add(msg); Debug.Log($"[WeaponImport] {msg}"); }
            public void Warn(string msg)    { Warnings.Add(msg); Debug.LogWarning($"[WeaponImport] {msg}"); }
        }

        // ── Entry point ───────────────────────────────────────────────────────
        /// <summary>
        /// Full import pipeline. Call from the EditorWindow.
        /// </summary>
        /// <param name="pfeRoot">Path to the pfe/ JPEXS export folder (contains sprites/, symbolClass/, scripts/).</param>
        /// <param name="swfPath">Path to the raw pfe.swf binary for frame-label extraction. Empty = use defaults.</param>
        public static ImportResult Run(string pfeRoot, string swfPath)
        {
            var result = new ImportResult();

            // Step 1: read symbol table
            var symbolTable = ReadSymbolTable(pfeRoot, result);
            if (symbolTable == null) return result;

            // Step 2: extract vis{weaponId} symbols (held weapon sprites only)
            var weaponSymbols = FilterWeaponSymbols(symbolTable, result);

            // Step 3: parse AllData.as for vweap + flare overrides
            var visOverrides = ParseVisOverrides(pfeRoot, result);

            // Step 4: parse SWF binary for frame labels (optional)
            SWFFile swfData = null;
            if (!string.IsNullOrEmpty(swfPath) && File.Exists(swfPath))
            {
                try
                {
                    swfData = new SWFParser().Parse(swfPath);
                    result.Info($"Parsed SWF: {swfData.Symbols.Count} symbols, {swfData.ShapeBounds.Count} shapes.");
                }
                catch (Exception ex)
                {
                    result.Warn($"SWF parse failed ({ex.Message}). Using default frame ranges.");
                }
            }
            else
            {
                result.Info("No SWF path provided — will use default frame ranges.");
            }

            // Step 5: copy PNGs and import sprites
            EnsureDirectories();
            AssetDatabase.StartAssetEditing();
            var copiedFiles = new List<(int symbolId, string symbolName, List<string> destPaths)>();
            try
            {
                foreach (var (symbolId, symbolName) in weaponSymbols)
                {
                    var paths = CopyFramePngs(pfeRoot, symbolId, symbolName, result);
                    if (paths != null)
                        copiedFiles.Add((symbolId, symbolName, paths));
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }
            AssetDatabase.Refresh();

            // Step 6: configure TextureImporter + load Sprite references
            var spritesBySymbol = ConfigureSprites(copiedFiles, swfData, result);

            // Step 7: create WeaponVisualDefinition assets
            var visualDefs = CreateVisualDefinitions(
                weaponSymbols, spritesBySymbol, swfData, visOverrides, result);

            result.VisualDefsCreated = visualDefs.Count;

            // Step 8: wire WeaponDefinition.weaponVisual
            WireWeaponDefinitions(visualDefs, visOverrides, result);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            result.Info($"Done. {result.SpritesImported} sprites, {result.VisualDefsCreated} visual defs, {result.WeaponDefsWired} weapons wired.");
            return result;
        }

        // ── Step 1: Symbol table ──────────────────────────────────────────────
        /// <summary>Reads pfe/symbolClass/symbols.csv → symbolId → symbolName.</summary>
        static Dictionary<int, string> ReadSymbolTable(string pfeRoot, ImportResult result)
        {
            string csvPath = Path.Combine(pfeRoot, "symbolClass", "symbols.csv");
            if (!File.Exists(csvPath))
            {
                result.Warn($"Symbol table not found: {csvPath}");
                return null;
            }

            var table = new Dictionary<int, string>();
            foreach (string line in File.ReadAllLines(csvPath))
            {
                // Format: 1516;"visp10mm"
                var parts = line.Split(';');
                if (parts.Length < 2) continue;
                if (!int.TryParse(parts[0].Trim(), out int id)) continue;
                string name = parts[1].Trim().Trim('"');
                table[id] = name;
            }
            result.Info($"Symbol table: {table.Count} entries.");
            return table;
        }

        // ── Step 2: Filter weapon vis symbols ────────────────────────────────
        // Pattern: starts with "vis" followed by lower-case letters/digits/underscore.
        // Excludes: visual*, visbul*, visNPC*, visConsol*, visError*, visInform*, visSpec*,
        //           vis trigger/trap/box/mwall/robo* (environment/unit objects, not held weapons).
        static readonly Regex VisWeaponPattern = new(@"^vis[a-z_0-9]+$", RegexOptions.Compiled);
        static readonly HashSet<string> ExcludePrefixes = new(StringComparer.OrdinalIgnoreCase)
        {
            "visual", "visbul", "visnpc", "visconsol", "viserror", "visinform",
            "visjmp", "vislift", "vischeckpoint", "vismtrap", "vistrig", "vistrap",
            "visbox", "vismwall", "visjmp", "visstand", "vispip", "visbody",
            "vismain", "visset", "viswturret", "visrobo", "vistt", "visdron",
            "visvzz", "visscene", "vissign", "visrobop", "visrobol", "visrobog",
            "visrobom", "visrobos", "visrobogatl", "visrobogatp", "visrobolaser",
            "visrobonova", "visrobosparkl", "visrobominigun", "visroboplasma",
            "visroboplam", "visrobospark", "visdamexpl", "visdamgren", "visdamshot",
            "visacidgr", "visbal", "visbomb", "viscry", "visdbomb", "visdin",
            "visexc", "visfgren", "visgasgr", "visgren", "vishgren", "vishmine",
            "visimpgr", "visimpmine", "vismercgr", "vismine", "vismolotov",
            "visplagr", "visplamine", "visroboplagr", "visspgren", "visx37",
            "viszebmine", "viscur",
        };

        static List<(int symbolId, string symbolName)> FilterWeaponSymbols(
            Dictionary<int, string> table, ImportResult result)
        {
            var list = new List<(int symbolId, string symbolName)>();
            foreach (var kvp in table)
            {
                string name = kvp.Value;
                if (!VisWeaponPattern.IsMatch(name)) continue;
                if (ExcludePrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;
                list.Add((kvp.Key, name));
            }
            // Sort by symbol ID for deterministic output
            list.Sort((a, b) => a.symbolId.CompareTo(b.symbolId));
            result.Info($"Weapon vis symbols to import: {list.Count}");
            return list;
        }

        // ── Step 3: Parse AllData.as for vweap/flare overrides ───────────────
        public class WeaponVisOverride
        {
            public string VWeap;   // vis.@vweap — override symbol name
            public string Flare;   // vis.@flare
            public bool   HasShell;
            public int    ShineRadius;
        }

        static Dictionary<string, WeaponVisOverride> ParseVisOverrides(string pfeRoot, ImportResult result)
        {
            string allDataPath = Path.Combine(pfeRoot, "scripts", "fe", "AllData.as");
            var overrides = new Dictionary<string, WeaponVisOverride>(StringComparer.OrdinalIgnoreCase);

            if (!File.Exists(allDataPath))
            {
                result.Warn($"AllData.as not found at {allDataPath}. Skipping vweap/flare overrides.");
                return overrides;
            }

            // Extract embedded XML from AllData.as: everything between <alldata> ... </alldata>
            string raw = File.ReadAllText(allDataPath);
            int xmlStart = raw.IndexOf("<alldata", StringComparison.OrdinalIgnoreCase);
            int xmlEnd   = raw.LastIndexOf("</alldata>", StringComparison.OrdinalIgnoreCase);
            if (xmlStart < 0 || xmlEnd < 0)
            {
                result.Warn("Could not find <alldata> block in AllData.as.");
                return overrides;
            }
            string xmlText = raw.Substring(xmlStart, xmlEnd - xmlStart + "</alldata>".Length);

            XElement root;
            try { root = XElement.Parse(xmlText); }
            catch (Exception ex)
            {
                result.Warn($"AllData.as XML parse failed: {ex.Message}");
                return overrides;
            }

            // Walk all <weapon> elements
            foreach (var weaponEl in root.Descendants("weapon"))
            {
                string weaponId = (string)weaponEl.Attribute("id");
                if (string.IsNullOrEmpty(weaponId)) continue;

                var visEl = weaponEl.Element("vis");
                if (visEl == null) continue;

                var ov = new WeaponVisOverride
                {
                    VWeap      = (string)visEl.Attribute("vweap"),
                    Flare      = (string)visEl.Attribute("flare"),
                    HasShell   = visEl.Attribute("shell") != null,
                    ShineRadius = (int?)visEl.Attribute("shine") ?? 0,
                };
                // Only store if there's actually something interesting
                if (ov.VWeap != null || ov.Flare != null || ov.HasShell || ov.ShineRadius > 0)
                    overrides[weaponId] = ov;
            }

            result.Info($"AllData.as: {overrides.Count} weapons with vis overrides.");
            return overrides;
        }

        // ── Step 5: Copy frame PNGs ───────────────────────────────────────────
        static List<string> CopyFramePngs(string pfeRoot, int symbolId, string symbolName, ImportResult result)
        {
            // Source: pfeRoot/sprites/DefineSprite_{id}_{name}/
            string sourceDir = Path.Combine(pfeRoot, "sprites", $"DefineSprite_{symbolId}_{symbolName}");
            if (!Directory.Exists(sourceDir))
            {
                result.Warn($"Sprite folder not found for {symbolName} (id={symbolId}): {sourceDir}");
                return null;
            }

            string[] pngFiles = Directory.GetFiles(sourceDir, "*.png")
                .OrderBy(GetFrameNumber)
                .ToArray();

            if (pngFiles.Length == 0)
            {
                result.Warn($"No PNGs in {sourceDir}");
                return null;
            }

            string destDir = Path.GetFullPath(Path.Combine(SpritesOutputRoot, symbolName));
            Directory.CreateDirectory(destDir);

            var destPaths = new List<string>();
            foreach (string src in pngFiles)
            {
                int frameNum = GetFrameNumber(src);
                string dest = Path.Combine(destDir, $"f{frameNum:D3}.png");
                if (!File.Exists(dest) || File.GetLastWriteTimeUtc(src) > File.GetLastWriteTimeUtc(dest))
                    File.Copy(src, dest, true);
                destPaths.Add(dest);
                result.SpritesImported++;
            }
            return destPaths;
        }

        static int GetFrameNumber(string filePath)
        {
            string name = Path.GetFileNameWithoutExtension(filePath);
            // Handle "f001" format (already dest-side) or "1" format (source JPEXS)
            string digits = Regex.Replace(name, @"[^0-9]", "");
            return int.TryParse(digits, out int n) ? n : 0;
        }

        // ── Step 6: Configure TextureImporter + collect Sprite refs ───────────
        static Dictionary<string, Sprite[]> ConfigureSprites(
            List<(int symbolId, string symbolName, List<string> destPaths)> copiedFiles,
            SWFFile swfData,
            ImportResult result)
        {
            var output = new Dictionary<string, Sprite[]>(StringComparer.OrdinalIgnoreCase);

            foreach (var (symbolId, symbolName, destPaths) in copiedFiles)
            {
                // Compute pivot from SWF bounds if available
                Vector2 pivot = new(0.5f, 0.5f);
                if (swfData != null)
                {
                    Rect bounds = default;
                    bool hasBounds = false;

                    if (swfData.Frame1Bounds.TryGetValue(symbolId, out var f1b) && f1b.width > 0)
                    { bounds = f1b; hasBounds = true; }
                    else if (swfData.ShapeBounds.TryGetValue(symbolId, out var sb) && sb.width > 0)
                    { bounds = sb; hasBounds = true; }
                    else if (swfData.Symbols.TryGetValue(symbolId, out var sym) && sym.Bounds.width > 0)
                    { bounds = sym.Bounds; hasBounds = true; }

                    if (hasBounds)
                    {
                        // Registration point (0,0) in Flash coords → pivot in Unity sprite space.
                        // bounds.x = xMin (Flash Y-down), pivot Y needs flipping.
                        float px = bounds.width  > 0 ? (0f - bounds.x) / bounds.width  : 0.5f;
                        float py = bounds.height > 0 ? 1f - ((0f - bounds.y) / bounds.height) : 0.5f;
                        pivot = new Vector2(px, py);
                    }
                }

                var sprites = new Sprite[destPaths.Count];
                for (int i = 0; i < destPaths.Count; i++)
                {
                    // Convert to project-relative asset path
                    string assetPath = destPaths[i].Replace('\\', '/');
                    int assetsIdx = assetPath.IndexOf("Assets/", StringComparison.OrdinalIgnoreCase);
                    if (assetsIdx >= 0)
                        assetPath = assetPath.Substring(assetsIdx);

                    var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
                    if (importer == null) continue;

                    bool changed = false;
                    if (importer.textureType != TextureImporterType.Sprite)
                    { importer.textureType = TextureImporterType.Sprite; changed = true; }
                    if (importer.spritePixelsPerUnit != PixelsPerUnit)
                    { importer.spritePixelsPerUnit = PixelsPerUnit; changed = true; }

                    var settings = new TextureImporterSettings();
                    importer.ReadTextureSettings(settings);
                    if (settings.spriteAlignment != (int)SpriteAlignment.Custom ||
                        Mathf.Abs(settings.spritePivot.x - pivot.x) > 0.001f ||
                        Mathf.Abs(settings.spritePivot.y - pivot.y) > 0.001f)
                    {
                        settings.spriteAlignment = (int)SpriteAlignment.Custom;
                        settings.spritePivot = pivot;
                        importer.SetTextureSettings(settings);
                        changed = true;
                    }
                    if (importer.filterMode != FilterMode.Bilinear)
                    { importer.filterMode = FilterMode.Bilinear; changed = true; }
                    if (!importer.alphaIsTransparency)
                    { importer.alphaIsTransparency = true; changed = true; }

                    if (changed)
                        importer.SaveAndReimport();

                    sprites[i] = AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
                }
                output[symbolName] = sprites;
            }
            return output;
        }

        // ── Step 7: Create WeaponVisualDefinition assets ──────────────────────
        static Dictionary<string, WeaponVisualDefinition> CreateVisualDefinitions(
            List<(int symbolId, string symbolName)> weaponSymbols,
            Dictionary<string, Sprite[]> spritesBySymbol,
            SWFFile swfData,
            Dictionary<string, WeaponVisOverride> visOverrides,
            ImportResult result)
        {
            string defsDirFull = Path.GetFullPath(VisualDefsRoot);
            Directory.CreateDirectory(defsDirFull);

            var created = new Dictionary<string, WeaponVisualDefinition>(StringComparer.OrdinalIgnoreCase);

            foreach (var (symbolId, symbolName) in weaponSymbols)
            {
                if (!spritesBySymbol.TryGetValue(symbolName, out var sprites) || sprites.Length == 0)
                    continue;

                string assetPath = $"{VisualDefsRoot}/{symbolName}.asset";
                var def = AssetDatabase.LoadAssetAtPath<WeaponVisualDefinition>(assetPath);
                bool isNew = def == null;
                if (isNew)
                    def = ScriptableObject.CreateInstance<WeaponVisualDefinition>();

                def.symbolName    = symbolName;
                def.sourceSymbolId = symbolId;
                def.frames        = sprites;
                def.pixelsPerUnit = PixelsPerUnit;

                // Extract frame labels from SWF, or fall back to inferred defaults
                ApplyFrameLabels(def, symbolId, sprites.Length, swfData, result);

                if (isNew)
                {
                    AssetDatabase.CreateAsset(def, assetPath);
                    result.Info($"Created: {assetPath}");
                }
                else
                {
                    EditorUtility.SetDirty(def);
                }

                created[symbolName] = def;
            }
            return created;
        }

        /// <summary>
        /// Sets shootFrameStart/Count, reloadFrameStart/Count, readyFrame, prepFrameStart/Count
        /// from SWF frame labels when available, otherwise uses inferred defaults per frame count.
        /// </summary>
        static void ApplyFrameLabels(WeaponVisualDefinition def, int symbolId, int frameCount,
            SWFFile swfData, ImportResult result)
        {
            // Reset
            def.idleFrame        = 0;
            def.shootFrameStart  = -1;
            def.shootFrameCount  = 0;
            def.reloadFrameStart = -1;
            def.reloadFrameCount = 0;
            def.prepFrameStart   = -1;
            def.prepFrameCount   = 0;
            def.readyFrame       = -1;

            if (frameCount == 1) return; // static weapon, no animation

            // Try to get exact labels from SWF parse
            if (swfData != null && swfData.Symbols.TryGetValue(symbolId, out var symbol))
            {
                // Build label → 0-based frame index map
                var labels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var frame in symbol.Frames)
                {
                    if (!string.IsNullOrEmpty(frame.Label))
                        labels[frame.Label] = frame.FrameNumber - 1; // convert to 0-based
                }

                if (labels.Count > 0)
                {
                    // "shoot" label: shoot animation starts here
                    if (labels.TryGetValue("shoot", out int shootIdx))
                    {
                        def.shootFrameStart = shootIdx;
                        // shoot clip runs until the next labeled frame (or end)
                        def.shootFrameCount = NextLabelAfter(labels, shootIdx, frameCount) - shootIdx;
                    }

                    // "reload" label
                    if (labels.TryGetValue("reload", out int reloadIdx))
                    {
                        def.reloadFrameStart = reloadIdx;
                        def.reloadFrameCount = NextLabelAfter(labels, reloadIdx, frameCount) - reloadIdx;
                    }

                    // "ready" label — single frame destination
                    if (labels.TryGetValue("ready", out int readyIdx))
                        def.readyFrame = readyIdx;

                    // prep frames: frame 2 up to first non-idle labeled frame
                    // (t_prep goes from 1..prep, mapped to gotoAndStop(t_prep))
                    // Prep frames start at frame index 1 (flash frame 2) when no dedicated label
                    int firstNamedFrame = labels.Values.Min();
                    if (firstNamedFrame > 1)
                    {
                        def.prepFrameStart = 1;
                        def.prepFrameCount = firstNamedFrame - 1;
                    }

                    result.Info($"  {def.symbolName}: shoot={def.shootFrameStart} " +
                                $"reload={def.reloadFrameStart} ready={def.readyFrame} " +
                                $"prep={def.prepFrameStart}..{def.prepFrameCount}");
                    return;
                }
            }

            // ── Fallback: infer from total frame count ─────────────────────────
            // Conventions derived from AS3 analysis:
            //   40-frame guns (most firearms)  : idle=0, shoot=1..7, reload=8..35, ready=-1
            //   60-frame heavy guns            : idle=0, shoot=1..7, reload=8..55, ready=-1
            //   5-7 frame special/magic        : idle=0, shoot=1..N-1, no reload
            //   3-4 frame (simple animated)    : idle=0, shoot=1..N-1
            //   1 frame                        : static (handled above)
            switch (frameCount)
            {
                case 40:
                    def.shootFrameStart  = 1;  def.shootFrameCount  = 7;
                    def.reloadFrameStart = 8;  def.reloadFrameCount = 32;
                    break;
                case 60:
                    def.shootFrameStart  = 1;  def.shootFrameCount  = 7;
                    def.reloadFrameStart = 8;  def.reloadFrameCount = 52;
                    break;
                case 70: // visminigun
                    def.shootFrameStart  = 1;  def.shootFrameCount  = 14;
                    def.reloadFrameStart = 15; def.reloadFrameCount = 55;
                    break;
                default:
                    // Generic: first half = shoot, second half = reload (if enough frames)
                    if (frameCount >= 6)
                    {
                        int half = frameCount / 2;
                        def.shootFrameStart  = 1;  def.shootFrameCount  = half - 1;
                        def.reloadFrameStart = half; def.reloadFrameCount = frameCount - half;
                    }
                    else if (frameCount >= 2)
                    {
                        def.shootFrameStart = 1; def.shootFrameCount = frameCount - 1;
                    }
                    break;
            }
        }

        /// <summary>Returns the next labeled frame index after <paramref name="after"/>, or frameCount if none.</summary>
        static int NextLabelAfter(Dictionary<string, int> labels, int after, int frameCount)
        {
            int next = frameCount;
            foreach (int idx in labels.Values)
                if (idx > after && idx < next)
                    next = idx;
            return next;
        }

        // ── Step 8: Wire WeaponDefinition.weaponVisual ────────────────────────
        static void WireWeaponDefinitions(
            Dictionary<string, WeaponVisualDefinition> visualDefs,
            Dictionary<string, WeaponVisOverride> visOverrides,
            ImportResult result)
        {
            string[] weaponAssets = AssetDatabase.FindAssets("t:WeaponDefinition", new[] { WeaponDefsRoot });
            foreach (string guid in weaponAssets)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                var weaponDef = AssetDatabase.LoadAssetAtPath<WeaponDefinition>(assetPath);
                if (weaponDef == null) continue;

                string weaponId = weaponDef.weaponId;
                if (string.IsNullOrEmpty(weaponId)) continue;

                // Apply vis overrides from AllData.as
                if (visOverrides.TryGetValue(weaponId, out var ov))
                {
                    bool changed = false;
                    if (!string.IsNullOrEmpty(ov.VWeap) && weaponDef.visualOverrideId != ov.VWeap)
                    { weaponDef.visualOverrideId = ov.VWeap; changed = true; }
                    if (!string.IsNullOrEmpty(ov.Flare) && weaponDef.muzzleFlareId != ov.Flare)
                    { weaponDef.muzzleFlareId = ov.Flare; changed = true; }
                    if (changed) EditorUtility.SetDirty(weaponDef);
                }

                // Determine which symbol this weapon uses
                string symbolName = string.IsNullOrEmpty(weaponDef.visualOverrideId)
                    ? "vis" + weaponId
                    : weaponDef.visualOverrideId;

                if (!visualDefs.TryGetValue(symbolName, out var visDef))
                {
                    // Also try with underscored variant (e.g. "visassr_1")
                    bool found = false;
                    foreach (var suffix in new[] { "_1", "_2" })
                    {
                        if (visualDefs.TryGetValue(symbolName + suffix, out visDef))
                        { found = true; break; }
                    }
                    if (!found)
                    {
                        result.Warn($"No visual def for weapon '{weaponId}' (tried symbol '{symbolName}')");
                        continue;
                    }
                }

                if (weaponDef.weaponVisual != visDef)
                {
                    weaponDef.weaponVisual = visDef;
                    visDef.weaponId = weaponId; // back-reference
                    EditorUtility.SetDirty(weaponDef);
                    EditorUtility.SetDirty(visDef);
                    result.WeaponDefsWired++;
                }
            }
        }

        // ── Utilities ─────────────────────────────────────────────────────────
        static void EnsureDirectories()
        {
            EnsureDir(SpritesOutputRoot);
            EnsureDir(VisualDefsRoot);
        }

        static void EnsureDir(string unityRelPath)
        {
            // Create each folder segment under Assets/
            string[] parts = unityRelPath.Split('/');
            string current = parts[0]; // "Assets"
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }
    }
}
#endif
