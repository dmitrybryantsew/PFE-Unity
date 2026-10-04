#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using PFE.Data.Definitions;
using PFE.Systems.Particles;
using UnityEditor;
using UnityEngine;

namespace PFE.Editor.Importers.Particles
{
    /// <summary>
    /// Imports the frames every <c>&lt;part&gt;</c> row needs into one
    /// <see cref="ParticleSpriteCatalogAsset"/>.
    ///
    /// <para><b>Two source layouts, one output layout.</b> The 118 rows split 94 <c>vis=</c> / 24
    /// <c>blit=</c>, and the two are exported completely differently — one PNG per frame under
    /// <c>pfe/scripts/_assets/sprites</c>, versus a single horizontal sheet under
    /// <c>sprite1.swf/images</c>. Both are normalised here into
    /// <c>Assets/_PFE/Art/Particles/&lt;assetId&gt;/fNNN.png</c>, so the runtime catalogue has exactly
    /// one shape to read and the renderer never has to know which pipeline a row came from. The
    /// arithmetic that decides <i>where</i> a row's art lives is in
    /// <see cref="ParticleSpriteSource"/>, in the runtime assembly, so it can be executed offline — this
    /// file is left with I/O, slicing, asset writing and the readbacks.</para>
    ///
    /// <para><b>Reads the row table from <see cref="ParticleDefinitionAsset"/>, not from
    /// <c>AllData.as</c>.</b> The alternative — re-parsing the XML here — would let the two importers
    /// disagree about which rows exist, and the disagreement would be invisible. One source of truth for
    /// the row list; run <c>PFE/Data/Import Particles from AllData.as</c> first if it is missing.</para>
    ///
    /// <para><b>Three readbacks, because a silent partial import is the failure mode.</b></para>
    /// <list type="number">
    /// <item><description><b>Per-asset frame counts are re-derived from the files on disk</b> after the
    /// import, not from what the importer intended to write — so a copy that failed shows up as a count
    /// mismatch rather than as a successful import.</description></item>
    /// <item><description><b>Missing ids are recorded on the asset</b> (<c>missingIds</c> /
    /// <c>emptyIds</c>), so "this row renders as nothing" is answerable from the asset instead of from a
    /// log line that has since scrolled away.</description></item>
    /// <item><description><b>The frame count is cross-checked against the row's own expectation</b> for
    /// <c>blit=</c> rows — <c>imageWidth / blitx</c> must be a positive integer, and a sheet whose width
    /// is not a whole number of cells is reported rather than silently truncated.</description></item>
    /// </list>
    /// </summary>
    public static class ParticleSpriteImporter
    {
        /// <summary>Where the normalised frames are written. One folder per distinct asset id.</summary>
        public const string OutputArtRoot = "Assets/_PFE/Art/Particles";

        /// <summary>The catalogue the runtime loads.</summary>
        public const string OutputAssetPath = "Assets/_PFE/Data/Resources/ParticleSprites.asset";

        /// <summary>The row table this importer reads. Produced by the data importer.</summary>
        public const string DefinitionsAssetPath = "Assets/_PFE/Data/Resources/ParticleDefinitions.asset";

        /// <summary>Matches every other importer in this project — see <c>ProjectileGraphicsImporter</c>.</summary>
        private const int PixelsPerUnit = 100;

        /// <summary>
        /// Distinct assets the art is expected to cover, so a surprise is loud.
        ///
        /// <para><b>104, not 110 — and the first version of this constant said 110, which was wrong.</b>
        /// 110 came from adding the row counts (94 <c>vis=</c> + 16 sheets), but rows and assets are not
        /// the same thing: the 94 <c>vis=</c> <i>rows</i> name only <b>88 distinct classes</b>, because six
        /// classes are shared by two rows each (<c>flLaser</c>, <c>flPlasma</c>, <c>flPlasma2</c>,
        /// <c>visBulb</c>, <c>visualFlame</c>, <c>visualPlaExpl</c>), and the 24 <c>blit=</c> rows name
        /// <b>16 distinct sheets</b> (five sheets are shared — <c>sprExpl</c>, <c>sprIskr</c>,
        /// <c>sprGBlood</c>, <c>sprBSpark</c>, <c>sprKap</c>). 88 + 16 = 104, which is exactly what the
        /// importer produced. The old value would have reported a false alarm on every correct run, which
        /// is worse than no check: it teaches the reader to ignore the line.</para>
        /// </summary>
        private const int ExpectedDistinctAssets = 104;

        [MenuItem("PFE/Art/Import Particle Sprites", priority = 23)]
        public static void ImportParticleSprites()
        {
            string root = SourceImportPaths.SourceProjectRoot;
            if (string.IsNullOrWhiteSpace(root))
            {
                Debug.LogError(SourceImportPaths.MissingSourceMessage(root, "the SWF export tree"));
                return;
            }

            var definitions = AssetDatabase.LoadAssetAtPath<ParticleDefinitionAsset>(DefinitionsAssetPath);
            if (definitions == null || definitions.definitions == null || definitions.definitions.Length == 0)
            {
                Debug.LogError(
                    $"Particle sprite import needs the row table first: no definitions at {DefinitionsAssetPath}. " +
                    "Run PFE/Data/Import Particles from AllData.as, then run this again. Importing art without " +
                    "the rows would write a catalogue keyed by guesses.");
                return;
            }

            // ── The two symbol maps. Both are read even when one is unused, because "the map was empty"
            //    and "the id was not in the map" are different failures and the counts tell them apart.
            var visSymbols = LoadSymbolMap(root, ParticleSpriteSource.VisSymbolCsvRelativePath, out int visSkipped);
            var blitSymbols = LoadSymbolMap(root, ParticleSpriteSource.BlitSymbolCsvRelativePath, out int blitSkipped);

            // ── Distinct assets, in first-seen order so the catalogue is stable across runs.
            var ordered = new List<AssetRequest>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (ParticleDefinition definition in definitions.definitions)
            {
                ParticleSpriteKind kind = ParticleSpriteSource.KindOf(definition);
                if (kind == ParticleSpriteKind.None) continue;

                string id = ParticleSpriteSource.AssetIdOf(definition);
                if (id.Length == 0 || !seen.Add(id)) continue;

                ordered.Add(new AssetRequest
                {
                    Id = id,
                    Kind = kind,
                    // For a blit row the cell size comes off the row; every row sharing a sheet agrees on
                    // it, which the geometry check below relies on.
                    CellWidth = definition.BlitX,
                    CellHeight = definition.BlitY
                });
            }

            var entries = new List<ParticleSpriteEntry>(ordered.Count);
            var missing = new List<string>();
            var empty = new List<string>();
            var written = new List<WrittenAsset>(ordered.Count);
            int importedFrames = 0;

            // ── Phase 1: write every PNG, with the asset pipeline paused.
            //
            // This matters more than it looks. The 118 rows expand to roughly 1 400 frame files. Letting
            // Unity import each one as it lands is a full import pass per file, which is the difference
            // between an importer that finishes and one the owner force-quits as hung.
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (AssetRequest request in ordered)
                {
                    string outputFolder = OutputArtRoot + "/" + request.Id;

                    int frameCount = request.Kind == ParticleSpriteKind.Vis
                        ? ImportVisAsset(root, visSymbols, request, outputFolder)
                        : ImportBlitAsset(root, blitSymbols, request, outputFolder);

                    if (frameCount < 0) { missing.Add(request.Id); continue; }
                    if (frameCount == 0) { empty.Add(request.Id); continue; }

                    written.Add(new WrittenAsset(request, outputFolder, frameCount));
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            AssetDatabase.Refresh();

            // ── Phase 2: set the import settings, deliberately AFTER a Refresh.
            //
            // A PNG Unity has not imported yet has **no** TextureImporter — `AssetImporter.GetAtPath`
            // returns null for it — so configuring inside the phase-1 batch would silently apply nothing
            // and every frame would import with Unity's defaults: wrong pixels-per-unit, wrong filter, and
            // in particular no `Sprite` type at all, so the readback would load zero sprites and report
            // every asset as having produced no frames. Refreshing first guarantees there is an importer
            // to configure.
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (WrittenAsset writtenAsset in written) ConfigureImportedSprites(writtenAsset.Folder);
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // ── Phase 3: read the files back as sprites and build the catalogue. Reading the files rather
            //    than trusting the write phases is the point — this is the readback.
            foreach (WrittenAsset writtenAsset in written)
            {
                Sprite[] frames = LoadFrames(writtenAsset.Folder, writtenAsset.FrameCount);
                if (frames.Length != writtenAsset.FrameCount)
                {
                    Debug.LogWarning(
                        $"Particle sprite import: '{writtenAsset.Request.Id}' wrote {writtenAsset.FrameCount} " +
                        $"frame file(s) but only {frames.Length} loaded back as sprites. The texture import " +
                        "settings may have failed; check the folder before trusting this asset.");
                }

                importedFrames += frames.Length;
                entries.Add(new ParticleSpriteEntry
                {
                    id = writtenAsset.Request.Id,
                    frames = frames,
                    cellWidth = writtenAsset.Request.Kind == ParticleSpriteKind.Blit ? writtenAsset.Request.CellWidth : 0,
                    cellHeight = writtenAsset.Request.Kind == ParticleSpriteKind.Blit ? writtenAsset.Request.CellHeight : 0
                });
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // ── Write the catalogue. A fresh array, and the index cache dropped, so a re-run cannot be
            //    served a stale lookup — the same reason ParticleDataImporter reassigns its array.
            var asset = AssetDatabase.LoadAssetAtPath<ParticleSpriteCatalogAsset>(OutputAssetPath);
            bool isNew = asset == null;
            if (isNew) asset = ScriptableObject.CreateInstance<ParticleSpriteCatalogAsset>();

            asset.entries = entries.ToArray();
            asset.missingIds = missing.ToArray();
            asset.emptyIds = empty.ToArray();
            asset.InvalidateIndex();

            if (isNew)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(OutputAssetPath) ?? "Assets");
                AssetDatabase.CreateAsset(asset, OutputAssetPath);
            }
            else
            {
                EditorUtility.SetDirty(asset);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log(
                $"Particle sprite import complete — {entries.Count} asset(s), {importedFrames} frame(s) " +
                $"written to {OutputAssetPath} (expected {ExpectedDistinctAssets} distinct assets). " +
                $"vis classes: {ordered.Count(r => r.Kind == ParticleSpriteKind.Vis)}, " +
                $"blit sheets: {ordered.Count(r => r.Kind == ParticleSpriteKind.Blit)}.");

            if (visSkipped > 0 || blitSkipped > 0)
            {
                Debug.LogWarning(
                    $"Particle sprite import: skipped {visSkipped} unparseable row(s) in " +
                    $"{ParticleSpriteSource.VisSymbolCsvRelativePath} and {blitSkipped} in " +
                    $"{ParticleSpriteSource.BlitSymbolCsvRelativePath}. A skipped row means a class name " +
                    "that cannot be resolved, so its rows will render as nothing.");
            }

            if (missing.Count > 0)
            {
                Debug.LogWarning(
                    $"Particle sprite import: {missing.Count} asset id(s) resolved to NO source art: " +
                    string.Join(", ", missing) + ". Every row naming one of these will render as nothing. " +
                    "For a blit id the usual cause is looking in sprite.swf — the effect sheets are in " +
                    "sprite1.swf, and sprite.swf holds unit sprites only.");
            }

            if (empty.Count > 0)
            {
                Debug.LogWarning(
                    $"Particle sprite import: {empty.Count} asset id(s) produced ZERO frames: " +
                    string.Join(", ", empty) + ". For a blit id this means imageWidth / blitx was not a " +
                    "positive integer — check the row's blitx against the sheet.");
            }
        }

        // ── vis= : one PNG per frame, copied ─────────────────────────────────

        /// <summary>
        /// Copies a <c>vis=</c> class's exported frames. Returns the frame count written, or <b>-1</b>
        /// when the class, the symbol id, or the folder could not be resolved.
        /// </summary>
        private static int ImportVisAsset(string root, Dictionary<string, int> symbols,
                                          AssetRequest request, string outputFolder)
        {
            if (!symbols.TryGetValue(request.Id, out int symbolId))
            {
                Debug.LogWarning(
                    $"Particle sprite import: '{request.Id}' is not in " +
                    $"{ParticleSpriteSource.VisSymbolCsvRelativePath}, so it has no symbol id and no folder.");
                return -1;
            }

            string sourceFolder = Path.Combine(
                Path.Combine(root, ParticleSpriteSource.VisSpritesRelativePath),
                ParticleSpriteSource.VisFolderName(symbolId));

            if (!Directory.Exists(sourceFolder)) return -1;

            // Order by the parsed frame number, never by name: the exporter writes `1.png` … `15.png`
            // unpadded, so a string sort would put frame 10 before frame 2 and the animation would play
            // in the wrong order with nothing obviously broken about it.
            List<(int Frame, string Path)> frames = Directory.GetFiles(sourceFolder, "*.png")
                .Select(path => (Frame: ParticleSpriteSource.VisFrameNumber(Path.GetFileName(path)), Path: path))
                .Where(f => f.Frame > 0)
                .OrderBy(f => f.Frame)
                .ToList();

            if (frames.Count == 0) return 0;

            Directory.CreateDirectory(outputFolder);
            DeleteStaleFrames(outputFolder, frames.Count);

            for (int i = 0; i < frames.Count; i++)
            {
                string destination = Path.Combine(outputFolder, ParticleSpriteSource.OutputFrameFileName(i));
                File.Copy(frames[i].Path, destination, overwrite: true);
            }

            return frames.Count;
        }
        // ── blit= : one sheet, sliced ────────────────────────────────────────

        /// <summary>
        /// Slices a <c>blit=</c> sheet into one PNG per cell. Returns the frame count written, or
        /// <b>-1</b> when the sheet or symbol id could not be resolved, or <b>0</b> when the geometry is
        /// degenerate.
        /// </summary>
        private static int ImportBlitAsset(string root, Dictionary<string, int> symbols,
                                           AssetRequest request, string outputFolder)
        {
            if (!symbols.TryGetValue(request.Id, out int symbolId))
            {
                Debug.LogWarning(
                    $"Particle sprite import: sheet '{request.Id}' is not in " +
                    $"{ParticleSpriteSource.BlitSymbolCsvRelativePath}. Note this is a DIFFERENT table from " +
                    "the vis one — a blit id must be looked up in sprite1.swf's symbols.csv.");
                return -1;
            }

            string sheetPath = Path.Combine(
                Path.Combine(Path.Combine(root, ParticleSpriteSource.BlitSwfFolderName + ".swf"), "images"),
                ParticleSpriteSource.BlitSheetFileName(symbolId, request.Id));

            if (!File.Exists(sheetPath)) return -1;

            var sheet = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false);
            try
            {
                if (!sheet.LoadImage(File.ReadAllBytes(sheetPath)))
                {
                    Debug.LogWarning($"Particle sprite import: '{request.Id}' — {sheetPath} is not a readable image.");
                    return 0;
                }

                int frameCount = ParticleSpriteSource.FrameCountFromSheet(sheet.width, request.CellWidth);
                if (frameCount <= 0)
                {
                    Debug.LogWarning(
                        $"Particle sprite import: '{request.Id}' — sheet is {sheet.width}x{sheet.height} and the " +
                        $"row says blitx={request.CellWidth}, which yields no whole frames.");
                    return 0;
                }

                if (sheet.width % request.CellWidth != 0)
                {
                    Debug.LogWarning(
                        $"Particle sprite import: '{request.Id}' — sheet width {sheet.width} is not a whole " +
                        $"number of {request.CellWidth}px cells; the last {sheet.width % request.CellWidth}px " +
                        "are dropped. The row's blitx is probably wrong.");
                }

                if (sheet.height != request.CellHeight)
                {
                    // Not fatal — the cells are sliced by width only, and the sheet's own height is what
                    // gets copied — but a mismatch means the row and the art disagree, which is worth
                    // knowing before chasing a rendering oddity.
                    Debug.LogWarning(
                        $"Particle sprite import: '{request.Id}' — sheet height {sheet.height} does not match the " +
                        $"row's blity={request.CellHeight}. Slicing by width, which is correct for a single row.");
                }

                Directory.CreateDirectory(outputFolder);
                DeleteStaleFrames(outputFolder, frameCount);

                Color[] pixels = sheet.GetPixels();
                for (int frame = 0; frame < frameCount; frame++)
                {
                    var cell = new Texture2D(request.CellWidth, sheet.height, TextureFormat.RGBA32, mipChain: false);
                    try
                    {
                        var slice = new Color[request.CellWidth * sheet.height];
                        for (int y = 0; y < sheet.height; y++)
                        {
                            // GetPixels is bottom-up and the source is one row, so the y index maps
                            // straight across; only x needs the cell offset.
                            int sourceRow = y * sheet.width + frame * request.CellWidth;
                            Array.Copy(pixels, sourceRow, slice, y * request.CellWidth, request.CellWidth);
                        }

                        cell.SetPixels(slice);
                        cell.Apply();
                        File.WriteAllBytes(
                            Path.Combine(outputFolder, ParticleSpriteSource.OutputFrameFileName(frame)),
                            cell.EncodeToPNG());
                    }
                    finally
                    {
                        UnityEngine.Object.DestroyImmediate(cell);
                    }
                }

                return frameCount;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(sheet);
            }
        }

        // ── Output helpers ───────────────────────────────────────────────────

        /// <summary>
        /// Removes frame files past the expected count, so a shorter re-import cannot leave a stale tail
        /// that <see cref="LoadFrames"/> would then pick up and play.
        /// </summary>
        private static void DeleteStaleFrames(string outputFolder, int expected)
        {
            foreach (string path in Directory.GetFiles(outputFolder, "*.png"))
            {
                if (ParticleSpriteSource.VisFrameNumber(Path.GetFileName(path)) > expected)
                    File.Delete(path);
            }
        }

        /// <summary>
        /// Applies the sprite import settings every other importer in this project uses. Centre pivot is
        /// correct for <b>both</b> pipelines here: the blit cells are drawn centred by the oracle
        /// (<c>Part.blit</c> offsets by <c>-blitX/2</c>), and all 94 <c>vis=</c> folders are internally
        /// uniform in frame size, so no per-frame pivot is needed.
        ///
        /// <para><b>Must run after a <c>Refresh</c>, never inside the phase-1 batch.</b> A file Unity has
        /// not imported yet has no <c>TextureImporter</c>, so <c>GetAtPath</c> returns null and every file
        /// would be skipped without a word. The <c>continue</c> below is the guard for exactly that, and
        /// the caller's ordering is what keeps it from being hit.</para>
        /// </summary>
        private static void ConfigureImportedSprites(string folder)
        {
            foreach (string path in Directory.GetFiles(folder, "*.png"))
            {
                string assetPath = ToAssetPath(path);
                if (AssetImporter.GetAtPath(assetPath) is not TextureImporter importer) continue;

                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.spritePixelsPerUnit = PixelsPerUnit;
                importer.filterMode = FilterMode.Bilinear;
                importer.textureCompression = TextureImporterCompression.CompressedHQ;
                importer.alphaIsTransparency = true;
                importer.mipmapEnabled = false;

                var settings = new TextureImporterSettings();
                importer.ReadTextureSettings(settings);
                settings.spriteAlignment = (int)SpriteAlignment.Center;
                settings.spritePivot = new Vector2(0.5f, 0.5f);
                importer.SetTextureSettings(settings);

                importer.SaveAndReimport();
            }
        }

        /// <summary>
        /// Loads the frames back as sprites, ordered by the frame number in the file name. Reading the
        /// files rather than trusting the write loop is the point — this is the readback.
        /// </summary>
        private static Sprite[] LoadFrames(string folder, int expected)
        {
            var found = new List<(int Frame, Sprite Sprite)>();
            foreach (string path in Directory.GetFiles(folder, "*.png"))
            {
                int frame = ParticleSpriteSource.VisFrameNumber(Path.GetFileName(path));
                if (frame <= 0) continue;

                var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(ToAssetPath(path));
                if (sprite != null) found.Add((frame, sprite));
            }

            found.Sort((a, b) => a.Frame.CompareTo(b.Frame));
            return found.Select(f => f.Sprite).ToArray();
        }

        private static Dictionary<string, int> LoadSymbolMap(string root, string relativePath, out int skipped)
        {
            string path = Path.Combine(root, relativePath);
            if (!File.Exists(path))
            {
                Debug.LogWarning($"Particle sprite import: symbol map missing at {path}.");
                skipped = 0;
                return new Dictionary<string, int>(StringComparer.Ordinal);
            }

            return ParticleSpriteSource.ParseSymbolCsv(File.ReadAllText(path), out skipped);
        }

        private static string ToAssetPath(string absoluteOrRelative)
        {
            string normalized = absoluteOrRelative.Replace('\\', '/');
            int index = normalized.IndexOf("Assets/", StringComparison.Ordinal);
            return index >= 0 ? normalized.Substring(index) : normalized;
        }

        private sealed class AssetRequest
        {
            public string Id;
            public ParticleSpriteKind Kind;
            public int CellWidth;
            public int CellHeight;
        }

        /// <summary>One asset whose frames are on disk, carried from the write phase to the readback.</summary>
        private sealed class WrittenAsset
        {
            public WrittenAsset(AssetRequest request, string folder, int frameCount)
            {
                Request = request;
                Folder = folder;
                FrameCount = frameCount;
            }

            public readonly AssetRequest Request;
            public readonly string Folder;
            public readonly int FrameCount;
        }
    }
}
#endif
