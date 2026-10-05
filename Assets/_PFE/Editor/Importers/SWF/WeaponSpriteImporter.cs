#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
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

            // Step 2: read AllData.as — which ids are weapons, and which symbols a weapon names
            // explicitly. This now runs BEFORE the symbol filter, because the filter's question ("is
            // this symbol a weapon's art?") is answered by the weapon-id set and cannot be asked
            // without it.
            var scan = ScanWeaponVis(pfeRoot, result);

            // An empty weapon-id set cannot classify a single symbol, so the filter below would
            // reject everything, nothing would be imported, and the run would then report every
            // weapon as "No visual def" — one cause wearing N costumes. Stop here and say so. This
            // guard is the general fix; the <all> tag above is the specific one.
            if (scan.WeaponIds.Count == 0)
            {
                result.Warn("No <weapon> ids were read from AllData.as — aborting, because every " +
                            "symbol would be rejected and every weapon reported as 'No visual def'.");
                return result;
            }

            // Step 3: extract vis{weaponId} symbols (held weapon sprites only)
            var weaponSymbols = FilterWeaponSymbols(symbolTable, scan, result);
            var visOverrides  = scan.Overrides;

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
        //
        // The decision itself lives in PFE.Data.Definitions.WeaponVisSymbolRule — a RUNTIME type, so
        // the offline test wall can pin it. This method is only the adapter: it hands the rule the
        // weapon ids and the explicit `vweap` symbol names read out of AllData.as.
        //
        // It used to be a hand-maintained blocklist of excluded prefixes, and that blocklist was
        // silently wrong for the whole throwable and mine family: `vismolotov`, `visacidgr`,
        // `visbomb`, `vismine`, `vismercgr` and 40 more were listed as "environment/unit objects, not
        // held weapons", so no WeaponVisualDefinition was ever created for them,
        // WeaponDefinition.weaponVisual stayed null, and a thrown grenade and a placed mine were
        // simulated with no sprite at all. Prefix collisions made it worse — `visbal` swallowed
        // `visbalemine`, `viscry` swallowed `viscryomine`, `visdin` swallowed `visdinamit`. See
        // WeaponVisSymbolRule for the rule that replaced it and why it cannot drift.
        static List<(int symbolId, string symbolName)> FilterWeaponSymbols(
            Dictionary<int, string> table,
            WeaponVisScan scan,
            ImportResult result)
        {
            var list = new List<(int symbolId, string symbolName)>();
            foreach (var kvp in table)
            {
                if (!WeaponVisSymbolRule.IsWeaponVisSymbol(kvp.Value, scan.WeaponIds, scan.OverrideSymbols))
                    continue;
                list.Add((kvp.Key, kvp.Value));
            }
            // Sort by symbol ID for deterministic output
            list.Sort((a, b) => a.symbolId.CompareTo(b.symbolId));
            result.Info($"Weapon vis symbols to import: {list.Count}");
            return list;
        }

        // ── Step 3: Parse AllData.as for the weapon set and the vis overrides ─
        public class WeaponVisOverride
        {
            public string VWeap;   // vis.@vweap — override symbol name
            public string Flare;   // vis.@flare
            public bool   HasShell;
            public int    ShineRadius;
        }

        /// <summary>
        /// Everything the symbol filter needs out of AllData.as: which ids are weapons, and which
        /// symbols a weapon names explicitly. Gathered in one pass because both come from the same
        /// <c>&lt;weapon&gt;</c> walk.
        /// </summary>
        public class WeaponVisScan
        {
            public readonly Dictionary<string, WeaponVisOverride> Overrides =
                new Dictionary<string, WeaponVisOverride>(StringComparer.OrdinalIgnoreCase);

            /// <summary>Every <c>&lt;weapon id&gt;</c> — the set that decides which <c>vis&lt;id&gt;</c> symbols are weapon art.</summary>
            public readonly HashSet<string> WeaponIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            /// <summary>Every non-empty <c>vis.@vweap</c> — a symbol named instead of <c>vis</c> + id.</summary>
            public readonly HashSet<string> OverrideSymbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        static WeaponVisScan ScanWeaponVis(string pfeRoot, ImportResult result)
        {
            string allDataPath = Path.Combine(pfeRoot, "scripts", "fe", "AllData.as");
            var scan = new WeaponVisScan();

            if (!File.Exists(allDataPath))
            {
                result.Warn($"AllData.as not found at {allDataPath}. No weapon ids, so nothing would " +
                            "be imported.");
                return scan;
            }

            // The extraction lives in a RUNTIME type so the offline wall can pin it. It used to live
            // right here, and it searched for a root element "<alldata>" that does not occur in the
            // file at all — 0 matches, while <all> and </all> each occur exactly once. It therefore
            // returned an EMPTY weapon-id set on every run, which was harmless while the symbol filter
            // was a hand-maintained blocklist that never consulted AllData.as, and became fatal the
            // moment the filter began classifying from that set: the filter rejected every symbol,
            // nothing was imported, and all 213 weapons reported "No visual def" — one bug wearing 213
            // costumes. See AllDataXml for the full account and its tests.
            var outcome = AllDataXml.TryRead(
                File.ReadAllText(allDataPath), out XElement root, out HashSet<string> weaponIds, out string error);

            if (outcome != AllDataXml.Outcome.Ok)
            {
                result.Warn($"AllData.as could not be read ({outcome}: {error}) — no weapon ids, so " +
                            "nothing would be imported.");
                return scan;
            }

            foreach (string id in weaponIds) scan.WeaponIds.Add(id);

            // Walk all <weapon> elements for the vis overrides
            foreach (var weaponEl in root.Descendants("weapon"))
            {
                string weaponId = (string)weaponEl.Attribute("id");
                if (string.IsNullOrEmpty(weaponId)) continue;

                // The id set itself was already collected by AllDataXml.TryRead, for EVERY weapon — a
                // weapon with no <vis> child still has a `vis<id>` symbol of its own to import.

                var visEl = weaponEl.Element("vis");
                if (visEl == null) continue;

                var ov = new WeaponVisOverride
                {
                    VWeap      = (string)visEl.Attribute("vweap"),
                    Flare      = (string)visEl.Attribute("flare"),
                    HasShell   = visEl.Attribute("shell") != null,
                    ShineRadius = (int?)visEl.Attribute("shine") ?? 0,
                };

                // A named symbol has to be importable even though it is not `vis` + id, or the
                // override in WireWeaponDefinitions resolves to a definition that was never created.
                if (!string.IsNullOrEmpty(ov.VWeap)) scan.OverrideSymbols.Add(ov.VWeap);

                // Only store if there's actually something interesting
                if (ov.VWeap != null || ov.Flare != null || ov.HasShell || ov.ShineRadius > 0)
                    scan.Overrides[weaponId] = ov;
            }

            result.Info($"AllData.as: {scan.WeaponIds.Count} weapons, {scan.Overrides.Count} with vis overrides.");
            return scan;
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
        //
        // Deliberately TWO passes.
        //
        // The obvious shape — loop the frames and call importer.SaveAndReimport() on each — is what
        // silently lost 2,574 pivots on 10-05. SaveAndReimport runs an import per asset, and Unity 6
        // imports asynchronously: the Editor log showed `Start importing <asset> (TextureImporter) ->
        // (artifact id: …)` immediately followed by `Cannot open file '<asset>.meta' for write` — the
        // writer colliding with its own reader, hundreds of times, because ~4,000 of those passes were
        // fired back to back. Nothing outside Unity held the files (an exclusive CreateFile probe on the
        // failures returns "free"), so it is an internal race, not a lock.
        //
        // StartAssetEditing/StopAssetEditing defers the imports and flushes them in ONE ordered pass, so
        // that collision cannot arise. The price is that nothing is imported while the batch is open, so
        // AssetDatabase.LoadAssetAtPath<Sprite> cannot be called until afterwards — hence pass 1 sets the
        // settings, pass 2 reads the results back.
        static Dictionary<string, Sprite[]> ConfigureSprites(
            List<(int symbolId, string symbolName, List<string> destPaths)> copiedFiles,
            SWFFile swfData,
            ImportResult result)
        {
            var output = new Dictionary<string, Sprite[]>(StringComparer.OrdinalIgnoreCase);

            // Target pivot per symbol — a pivot is a property of the symbol, not of a frame.
            var pivotBySymbol = new Dictionary<string, Vector2>(StringComparer.OrdinalIgnoreCase);
            foreach (var (symbolId, symbolName, _) in copiedFiles)
                pivotBySymbol[symbolName] = ComputePivot(symbolId, swfData);

            // ── Pass 1: set every importer inside a single batch ──────────────
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var (_, symbolName, destPaths) in copiedFiles)
                {
                    Vector2 pivot = pivotBySymbol[symbolName];
                    foreach (string dest in destPaths)
                    {
                        string assetPath = ToAssetPath(dest);
                        if (AssetImporter.GetAtPath(assetPath) is not TextureImporter importer) continue;
                        ApplySpriteSettings(importer, assetPath, pivot);
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();   // one import pass over everything we touched
            }

            // ── Pass 2: verify each .meta landed and collect the stragglers ──────
            //
            // Verify against the DISK. Even a batched flush can drop a .meta, and Unity logs "Failed to
            // write meta file" without throwing — so a silent no-op is possible and must be caught
            // rather than trusted.
            var failures = new List<(string assetPath, Vector2 pivot)>();
            foreach (var (_, symbolName, destPaths) in copiedFiles)
            {
                Vector2 pivot = pivotBySymbol[symbolName];
                var sprites = new Sprite[destPaths.Count];

                for (int i = 0; i < destPaths.Count; i++)
                {
                    string assetPath = ToAssetPath(destPaths[i]);
                    if (!MetaFileHasPivot(assetPath, pivot)) failures.Add((assetPath, pivot));
                    sprites[i] = AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
                }

                output[symbolName] = sprites;
            }

            // ── Pass 3: repair the stragglers in BATCHES, pausing between rounds ─
            //
            // The first version of this repair walked the failures and called SaveAndReimport on each —
            // i.e. it reintroduced, inside the fix, the exact per-asset import race the batch above
            // exists to avoid. On 10-05 that produced 902 "Failed to write meta" errors from the repair
            // path itself and left 25 pivots stuck. Repairing the whole set in one batch, and waiting
            // before retrying, is both cheaper and the same shape as the pass that works.
            for (int round = 0; round < RepairAttempts && failures.Count > 0; round++)
            {
                if (round > 0 && RepairDelayMs > 0)
                    System.Threading.Thread.Sleep(RepairDelayMs);   // let Unity's in-flight import settle

                AssetDatabase.StartAssetEditing();
                try
                {
                    foreach (var (assetPath, pivot) in failures)
                    {
                        if (AssetImporter.GetAtPath(assetPath) is not TextureImporter importer) continue;

                        ApplySpriteSettings(importer, assetPath, pivot);

                        // Applying the settings is NOT enough on its own, and this is the subtle one.
                        // Unity caches the importer in memory, and for a frame whose earlier write failed
                        // the cached pivot is ALREADY the target — so SetTextureSettings is a no-op, the
                        // asset is never marked dirty, and no import (hence no .meta write) is ever queued.
                        // Run 2 showed precisely that: zero write errors AND zero sprite imports. The
                        // forced import below is what actually makes Unity re-serialise the .meta.
                        AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
                    }
                }
                finally
                {
                    AssetDatabase.StopAssetEditing();
                }

                failures.RemoveAll(f => MetaFileHasPivot(f.assetPath, f.pivot));
            }

            foreach (var (assetPath, pivot) in failures)
                result.Warn($"Pivot did not persist for {assetPath} (wanted " +
                            $"{pivot.x:0.####}, {pivot.y:0.####}) — Unity could not write the .meta.");

            return output;
        }

        /// <summary>
        /// The registration point for a symbol, taken from the SWF bounds, or <c>(0.5, 0.5)</c> when the
        /// symbol has no usable bounds (the honest answer for art that genuinely has none).
        /// </summary>
        static Vector2 ComputePivot(int symbolId, SWFFile swfData)
        {
            if (swfData == null) return new Vector2(0.5f, 0.5f);

            Rect bounds = default;
            bool hasBounds = false;

            if (swfData.Frame1Bounds.TryGetValue(symbolId, out var f1b) && f1b.width > 0)
            { bounds = f1b; hasBounds = true; }
            else if (swfData.ShapeBounds.TryGetValue(symbolId, out var sb) && sb.width > 0)
            { bounds = sb; hasBounds = true; }
            else if (swfData.Symbols.TryGetValue(symbolId, out var sym) && sym.Bounds.width > 0)
            { bounds = sym.Bounds; hasBounds = true; }

            if (!hasBounds) return new Vector2(0.5f, 0.5f);

            // Registration point (0,0) in Flash coords → pivot in Unity sprite space.
            // bounds.x = xMin (Flash Y-down), pivot Y needs flipping.
            float px = bounds.width  > 0 ? (0f - bounds.x) / bounds.width  : 0.5f;
            float py = bounds.height > 0 ? 1f - ((0f - bounds.y) / bounds.height) : 0.5f;
            return new Vector2(px, py);
        }

        /// <summary>Destination path → the project-relative "Assets/…" path Unity addresses assets by.</summary>
        static string ToAssetPath(string destPath)
        {
            string assetPath = destPath.Replace('\\', '/');
            int assetsIdx = assetPath.IndexOf("Assets/", StringComparison.OrdinalIgnoreCase);
            return assetsIdx >= 0 ? assetPath.Substring(assetsIdx) : assetPath;
        }

        /// <summary>
        /// Applies sprite / PPU / filter / pivot settings to one importer and reports whether anything
        /// actually changed, so a re-run over already-correct art stays a no-op.
        ///
        /// <para>The pivot decision is made from the <c>.meta</c> on disk, not from the importer, because
        /// Unity updates the in-memory copy even when the write to disk failed — comparing against the
        /// in-memory value is what made the original run skip the very frames it had just failed to
        /// write.</para>
        /// </summary>
        static bool ApplySpriteSettings(TextureImporter importer, string assetPath, Vector2 pivot)
        {
            bool changed = false;

            if (importer.textureType != TextureImporterType.Sprite)
            { importer.textureType = TextureImporterType.Sprite; changed = true; }
            if (importer.spritePixelsPerUnit != PixelsPerUnit)
            { importer.spritePixelsPerUnit = PixelsPerUnit; changed = true; }

            var settings = new TextureImporterSettings();
            importer.ReadTextureSettings(settings);

            if (settings.spriteAlignment != (int)SpriteAlignment.Custom ||
                Mathf.Abs(settings.spritePivot.x - pivot.x) > 0.001f ||
                Mathf.Abs(settings.spritePivot.y - pivot.y) > 0.001f ||
                !MetaFileHasPivot(assetPath, pivot))
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

            return changed;
        }

        /// <summary>
        /// How many batched repair rounds to run over the stragglers left by the main pass. Configurable.
        /// Each round re-checks the disk and only re-touches what is still wrong, so raising this is
        /// cheap; it is not a per-asset retry count.
        /// </summary>
        public static int RepairAttempts = 3;

        /// <summary>
        /// Pause between repair ROUNDS, in milliseconds. Deliberately a round-level pause, not a
        /// per-asset sleep: the collision is with Unity's own asynchronous import, which needs a moment
        /// to release the file, and only the (rare) stragglers should pay for it. A sleep here blocks the
        /// editor's main thread, so keep it small. 0 disables the pause.
        /// </summary>
        public static int RepairDelayMs = 150;

        /// <summary>
        /// True when the asset's <c>.meta</c> on disk already records <paramref name="pivot"/> as the
        /// sprite pivot.
        ///
        /// <para>Reads the file rather than the importer on purpose. <see cref="TextureImporter"/>
        /// exposes Unity's in-memory state, which is updated even when the write to disk failed, so it
        /// cannot be used either to decide whether a write is needed or to confirm one succeeded. Only
        /// the file on disk can answer that — see the note in <see cref="ConfigureSprites"/>.</para>
        /// </summary>
        static bool MetaFileHasPivot(string assetPath, Vector2 pivot)
        {
            string metaPath = Path.GetFullPath(assetPath) + ".meta";
            if (!File.Exists(metaPath)) return false;

            try
            {
                foreach (string line in File.ReadLines(metaPath))
                {
                    // Top-level "spritePivot: {x: .., y: ..}". Sub-sprite entries inside a spriteSheet
                    // are spelled "pivot:" alone, so this cannot accidentally match one of those.
                    int key = line.IndexOf("spritePivot:", StringComparison.Ordinal);
                    if (key < 0) continue;

                    string body = line.Substring(key + "spritePivot:".Length);
                    int xs = body.IndexOf("x:", StringComparison.Ordinal);
                    int ys = body.IndexOf("y:", StringComparison.Ordinal);
                    if (xs < 0 || ys < 0) return false;

                    int comma = body.IndexOf(',', xs);
                    if (comma < 0) return false;

                    string sx = body.Substring(xs + 2, comma - (xs + 2)).Trim();
                    string sy = body.Substring(ys + 2).Trim().TrimEnd('}').Trim();

                    if (float.TryParse(sx, NumberStyles.Float, CultureInfo.InvariantCulture, out float px) &&
                        float.TryParse(sy, NumberStyles.Float, CultureInfo.InvariantCulture, out float py))
                        return Mathf.Abs(px - pivot.x) < 0.001f && Mathf.Abs(py - pivot.y) < 0.001f;

                    return false;
                }
            }
            catch (Exception)
            {
                // Unreadable or vanished file: treat as "not on disk" so the caller (re)writes it.
            }
            return false;
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

                // Recover the muzzle from the idle frame's art. AS3's `vis.emit` marker is not in the
                // extracted data and the source SWF is not in the repository, so the barrel tip has to
                // be measured — see WeaponMuzzleOffsetBaker. Done here rather than as a follow-up pass
                // so a freshly imported weapon never spends any time with a (0,0) muzzle, which is the
                // state that made shots leave the grip.
                if (WeaponMuzzleOffsetBaker.TryBake(def, out Vector2 muzzle, out string muzzleDetail))
                    result.Info($"  muzzle ({muzzle.x:0.####}, {muzzle.y:0.####}) — {muzzleDetail}");
                else
                    result.Info($"  muzzle NOT measured — {muzzleDetail}");

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
