#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PFE.Data.Definitions;
using UnityEditor;
using UnityEditor.U2D.Sprites;
using UnityEngine;

namespace PFE.Editor.Importers
{
    /// <summary>
    /// Slices the AS3 unit sprite sheets into Unity sprites and imports the DisplayObject visuals,
    /// then wires the result onto each <see cref="UnitDefinition"/>.
    ///
    /// <para><b>Why a grid, and why one PNG per unit.</b> A unit's visual is a single bitmap holding
    /// every frame of every state laid out in a grid — <c>sprRaider5</c> is 2880x1080 = 24 columns by
    /// 9 rows of 120x120 cells. AS3 addresses a cell with two independent numbers, the state's row and
    /// its frame, and blits one cell at a time:</para>
    ///
    /// <code>blitRect.x = col * blitX; blitRect.y = row * blitY;   // Unit.as:2866-2867</code>
    ///
    /// <para>So the sheet is imported once, in <see cref="SpriteImportMode.Multiple"/>, with one named
    /// sprite per cell. Slicing into individual PNGs instead would produce roughly 15,000 files and
    /// lose the row/column addressing the runtime actually needs.</para>
    ///
    /// <para><b>The cell size is the unit's own.</b> <c>sprX</c>/<c>sprY</c> come from the unit's own
    /// <c>&lt;vis&gt;</c>, and <c>sprY</c> defaults to <c>sprX</c> when absent (<c>Unit.as:939</c>) —
    /// but it is a separate attribute and the data uses non-square cells (<c>ant1</c> 78x32,
    /// <c>tarakan</c> 60x30, <c>molerat</c> 85x58, <c>hellhound1</c> 200x170). The column count is
    /// therefore <c>width / sprX</c> and varies per unit (24, 25, 26, 15, 11, 8, 14 …); a global
    /// constant would silently mis-slice every sheet whose cell is not that constant.</para>
    ///
    /// <para><b>States that fall outside their sheet are reported, not clamped.</b> AS3's
    /// <c>copyPixels</c> with a source rect outside the bitmap copies nothing, so the oracle draws
    /// those states <i>empty</i>. Clamping them to the nearest real row would draw a different
    /// animation's frames, which is worse than drawing nothing. Eleven states are affected in the
    /// current data and every one is listed by <see cref="Result.OutOfRange"/>.</para>
    /// </summary>
    public static class UnitSpriteImporter
    {
        /// <summary>Sliced sheets, one PNG per unit, imported in Multiple mode.</summary>
        public const string SheetArtRoot = "Assets/_PFE/Art/Units/Sheets";

        /// <summary>DisplayObject visuals, one folder per symbol, one PNG per frame.</summary>
        public const string VisualArtRoot = "Assets/_PFE/Art/Units/Visuals";

        /// <summary>Matches the other graphics importers in this project (weapons, projectiles).</summary>
        public const int PixelsPerUnit = 100;

        const string UnitAssetRoot = "Assets/_PFE/Data/Resources/Units";

        /// <summary>
        /// Unity's ceiling for a single texture on the target platforms. The widest sheet in the data is
        /// 5200px (<c>sprGutsy</c>), so the importer must raise <c>maxTextureSize</c> above its 2048
        /// default — leaving it at the default silently downscales the sheet and every cell rect then
        /// lands in the wrong place.
        /// </summary>
        const int MaxTextureSize = 8192;

        /// <summary>
        /// How many times to re-issue a <c>SaveAndReimport</c> whose <c>.meta</c> write collided with the
        /// import already in flight, and the base backoff between attempts in milliseconds (it grows
        /// linearly, since the thing being waited on is a texture decode rather than a fixed-length lock).
        /// See <c>Reimport</c> for the run this cost.
        /// </summary>
        const int ReimportAttempts = 4;
        const int ReimportRetryDelayMs = 150;

        /// <summary>
        /// Units whose visual is chosen in the controller class rather than declared in data, so it
        /// cannot be derived from <c>AllData.as</c> alone.
        ///
        /// <para><c>training</c> is the training dummy. <c>UnitTrain.as:38-49</c> picks between two
        /// DisplayObjects on a flag that comes from the <b>room placement</b>, not the unit:
        /// <c>&lt;obj id="training" tr="1"/&gt;</c> gives <c>visualTrainArmor</c> (and <c>skin = 20</c>),
        /// anything else gives <c>visualTrain</c>. Both are therefore imported, and the spawner chooses.
        /// The values are verified against the source tree at import time — a missing folder is reported
        /// rather than silently producing a unit with no visual.</para>
        /// </summary>
        static readonly Dictionary<string, string[]> CodeAssignedVisuals = new Dictionary<string, string[]>
        {
            { "training", new[] { "visualTrain", "visualTrainArmor" } },
        };

        public class Result
        {
            public int SheetsImported;
            public int SheetsUnchanged;
            public int CellsSliced;
            public int VisualsImported;
            public int VisualFramesCopied;
            public int UnitsWired;
            public int UnitsWithNoVisual;

            /// <summary>States whose row or frame count falls outside the sheet they are drawn from.</summary>
            public readonly List<string> OutOfRange = new List<string>();

            /// <summary>Units for which no visual source could be found at all.</summary>
            public readonly List<string> Unresolved = new List<string>();

            public readonly List<string> Warnings = new List<string>();

            public string Describe()
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"sheets sliced      : {SheetsImported} ({CellsSliced} cells), {SheetsUnchanged} unchanged");
                sb.AppendLine($"visuals imported   : {VisualsImported} symbols, {VisualFramesCopied} frames");
                sb.AppendLine($"units wired        : {UnitsWired}");
                sb.AppendLine($"units with no visual: {UnitsWithNoVisual}");
                sb.AppendLine($"states out of range : {OutOfRange.Count}");
                sb.AppendLine($"warnings           : {Warnings.Count}");
                return sb.ToString();
            }
        }

        [MenuItem("PFE/Art/Import Unit Sprites", priority = 22)]
        public static void ImportUnitSprites()
        {
            if (!SourceImportPaths.IsResolved)
            {
                Debug.LogError(SourceImportPaths.MissingSourceMessage(
                    SourceImportPaths.SourceProjectRoot, "The AS3 source root"));
                return;
            }

            bool proceed = EditorUtility.DisplayDialog(
                "Import unit sprites?",
                "Slices every unit sprite sheet into a Multiple-mode grid and imports the DisplayObject\n" +
                "visuals, then rewrites the sprite fields on the unit assets in\n" +
                $"{UnitAssetRoot}\n\n" +
                "This can take a minute. The assets are git-tracked, so the change is recoverable.",
                "Import", "Cancel");

            if (!proceed) return;

            Result result = Run();
            Debug.Log("[UnitSpriteImporter]\n" + result.Describe() + DescribeDetail(result));
        }

        /// <summary>
        /// Run the import without the confirmation dialog, for callers that batch it
        /// (see <see cref="BatchCoreDataImport"/>).
        /// </summary>
        public static Result Run()
        {
            var result = new Result();

            var sheetSources = BuildSheetIndex();
            var units = LoadUnits();

            if (units.Count == 0)
            {
                result.Warnings.Add($"No UnitDefinition assets under {UnitAssetRoot}. " +
                                    "Run PFE/Data/Import Units from AllData.as first.");
                return result;
            }

            // ── Pass 1: sheet units ──────────────────────────────────────────
            foreach (UnitDefinition unit in units)
            {
                if (string.IsNullOrWhiteSpace(unit.spriteSheetId))
                {
                    continue;
                }

                if (!sheetSources.TryGetValue(unit.spriteSheetId, out string sourcePng))
                {
                    result.Warnings.Add($"{unit.id}: sheet '{unit.spriteSheetId}' has no PNG in " +
                                        "sprite.swf/images or sprite1.swf/images");
                    continue;
                }

                try
                {
                    if (SliceSheet(unit, sourcePng, result))
                    {
                        result.UnitsWired++;
                    }
                }
                catch (Exception e)
                {
                    // One unit must never be able to kill the run. This loop drives ~62 sheet imports
                    // and ~228 frame imports through the asset pipeline, and an unhandled throw anywhere
                    // inside it unwinds out of Run — so the summary never prints and a half-imported
                    // project looks like nothing happened at all. That is precisely what a .meta write
                    // collision did on 5 sheets. Reported, and the run continues.
                    result.Warnings.Add($"{unit.id}: sheet import threw {e.GetType().Name}: {e.Message}");
                }
            }

            // ── Pass 2: DisplayObject visuals ────────────────────────────────
            foreach (UnitDefinition unit in units)
            {
                if (!string.IsNullOrWhiteSpace(unit.spriteSheetId))
                {
                    continue;   // blit outranks vclass (Unit.as:886-890)
                }

                string[] visualNames = ResolveVisualNames(unit, result);
                if (visualNames == null || visualNames.Length == 0)
                {
                    result.UnitsWithNoVisual++;
                    result.Unresolved.Add($"{unit.id}" +
                                          (string.IsNullOrWhiteSpace(unit.visualClassName)
                                              ? " (no vis at all)"
                                              : $" (vclass='{unit.visualClassName}')"));
                    continue;
                }

                try
                {
                    if (ImportVisual(unit, visualNames, result))
                    {
                        result.UnitsWired++;
                    }
                }
                catch (Exception e)
                {
                    // Same reason as pass 1: the frame loop issues one import per frame, so it is the
                    // longest-running part of the run and the most likely place to lose a race.
                    result.Warnings.Add($"{unit.id}: visual import threw {e.GetType().Name}: {e.Message}");
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // Report the states the sheet cannot satisfy, after the sheets are known.
            ReportOutOfRangeStates(units, result);

            return result;
        }

        // ── Sheet pass ───────────────────────────────────────────────────────

        /// <summary>
        /// Slice one unit's sheet and write the result onto the definition. Returns true when the unit
        /// was wired to at least one sprite.
        /// </summary>
        static bool SliceSheet(UnitDefinition unit, string sourcePng, Result result)
        {
            string sheetId = unit.spriteSheetId;
            string destAssetPath = $"{SheetArtRoot}/{sheetId}.png";

            try
            {
                EnsureDirectory(SheetArtRoot);
                CopyIfNewer(sourcePng, destAssetPath);
                AssetDatabase.ImportAsset(destAssetPath, ImportAssetOptions.ForceUpdate);
            }
            catch (Exception e)
            {
                result.Warnings.Add($"{unit.id}: could not copy sheet '{sheetId}': {e.Message}");
                return false;
            }

            var importer = AssetImporter.GetAtPath(destAssetPath) as TextureImporter;
            if (importer == null)
            {
                result.Warnings.Add($"{unit.id}: no TextureImporter at {destAssetPath}");
                return false;
            }

            // Raise maxTextureSize BEFORE the first import.
            //
            // This used to import once at Unity's default 2048 in order to measure the texture, and only
            // raise the limit afterwards. That measured the DOWNSCALED texture: sprRaider1 came back
            // 2048x768 instead of 2880x1080, so its grid was computed as 17x6 instead of 24x9 and every
            // cell rect was wrong. The run reported "62 sheets sliced" while slicing 62 sheets
            // incorrectly, 405 states "out of range", and sprGutsy (5200px wide) and sprNecros dropped
            // altogether with a 15x0 grid. A too-low maxTextureSize never errors — it just resamples.
            ConfigureBase(importer, MaxTextureSize);
            importer.spriteImportMode = SpriteImportMode.Multiple;

            if (!Reimport(importer, destAssetPath, unit, result))
            {
                return false;
            }

            // The authoritative size is the FILE's, not the imported texture's. Reading the PNG header
            // makes the grid independent of every import setting, so no default can move it again.
            int width;
            int height;
            if (!UnitSheetLayout.TryReadPngSize(sourcePng, out width, out height))
            {
                result.Warnings.Add($"{unit.id}: could not read the PNG size of '{sheetId}' from {sourcePng}");
                return false;
            }

            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(destAssetPath);
            if (texture == null)
            {
                result.Warnings.Add($"{unit.id}: texture did not load at {destAssetPath}");
                return false;
            }

            // If Unity's copy is smaller than the file, something is downscaling it and every rect will
            // be misplaced. This is the assertion that would have caught the bug above on the first run.
            if (texture.width != width || texture.height != height)
            {
                result.Warnings.Add($"{unit.id}: sheet '{sheetId}' imported as {texture.width}x{texture.height} " +
                                    $"but the file is {width}x{height} — Unity is downscaling it, so the " +
                                    "cell rects will not line up.");
            }

            int needed = Mathf.NextPowerOfTwo(Mathf.Max(width, height));
            if (needed > MaxTextureSize)
            {
                result.Warnings.Add($"{unit.id}: sheet '{sheetId}' is {width}x{height}, which needs " +
                                    $"maxTextureSize {needed} but Unity's ceiling is {MaxTextureSize}. " +
                                    "The sheet will be downscaled and every cell rect will be wrong.");
            }

            Vector2Int cell = unit.spriteDimensions;
            if (cell.x <= 0 || cell.y <= 0)
            {
                result.Warnings.Add($"{unit.id}: sheet '{sheetId}' has no usable cell size " +
                                    $"({cell.x}x{cell.y})");
                return false;
            }

            int cols = width / cell.x;
            int rows = height / cell.y;

            // A non-exact division means the cell size does not belong to this sheet. Measured across
            // the current data this is exact for all 65 sheets, so a failure here is a real defect.
            if (width % cell.x != 0 || height % cell.y != 0)
            {
                result.Warnings.Add($"{unit.id}: sheet '{sheetId}' is {width}x{height}, which does not " +
                                    $"divide by the cell {cell.x}x{cell.y} " +
                                    $"({width}%{cell.x}={width % cell.x}, {height}%{cell.y}={height % cell.y}). " +
                                    $"Using {cols}x{rows} and leaving the remainder.");
            }

            if (cols <= 0 || rows <= 0)
            {
                result.Warnings.Add($"{unit.id}: sheet '{sheetId}' yields an empty grid " +
                                    $"({cols}x{rows}) for cell {cell.x}x{cell.y}");
                return false;
            }

            Vector2 pivot = PivotFor(unit, cell, result);

            var rects = new SpriteRect[cols * rows];
            for (int row = 0; row < rows; row++)
            {
                for (int col = 0; col < cols; col++)
                {
                    rects[row * cols + col] = new SpriteRect
                    {
                        name = UnitSheetLayout.CellName(sheetId, row, col),
                        rect = UnitSheetLayout.CellRect(row, col, rows, cell),
                        alignment = SpriteAlignment.Custom,
                        pivot = pivot,
                    };
                }
            }

            if (!ApplySpriteGrid(importer, destAssetPath, rects, unit, result))
            {
                return false;
            }

            // Read the sprites back by name. Unity sorts the asset list by name rather than by the
            // order they were supplied, so the flat row-major array is rebuilt from the names instead
            // of trusting LoadAllAssetsAtPath's ordering.
            var byName = new Dictionary<string, Sprite>();
            foreach (UnityEngine.Object asset in AssetDatabase.LoadAllAssetsAtPath(destAssetPath))
            {
                if (asset is Sprite sprite)
                {
                    byName[sprite.name] = sprite;
                }
            }

            var flat = new Sprite[cols * rows];
            int missing = 0;
            for (int row = 0; row < rows; row++)
            {
                for (int col = 0; col < cols; col++)
                {
                    if (byName.TryGetValue(UnitSheetLayout.CellName(sheetId, row, col), out Sprite sprite))
                    {
                        flat[row * cols + col] = sprite;
                    }
                    else
                    {
                        missing++;
                    }
                }
            }

            if (missing > 0)
            {
                result.Warnings.Add($"{unit.id}: {missing} of {cols * rows} cells of '{sheetId}' did " +
                                    "not come back as sprites after import");
            }

            SetPrivateField(unit, "spriteSheet", flat);
            SetPrivateField(unit, "spriteSheetColumns", cols);
            SetPrivateField(unit, "spriteSheetRows", rows);

            // The resting frame is lifted out so a spawner that draws nothing but `sprite` still
            // shows the unit standing. See AssignRestingFrame for why this is not the icon cell.
            AssignRestingFrame(unit, flat, cols, rows, result);

            EditorUtility.SetDirty(unit);

            result.SheetsImported++;
            result.CellsSliced += cols * rows;
            return flat.Any(s => s != null);
        }

        /// <summary>
        /// Write the cell grid through <c>ISpriteEditorDataProvider</c>.
        ///
        /// <para><b>Deliberately not through <c>TextureImporter.spritesheet</c>.</b> That property still
        /// exists and still compiles, so using it produces a green build — but on this editor
        /// (Unity 6000.3.10f1) it is obsolete with <i>"support for accessing sprite meta data through
        /// spritesheet has been removed"</i>, i.e. it reports success and changes nothing. The import
        /// would then log a clean run while every unit pointed at an unsliced texture. That is the same
        /// shape as this project's earlier defect where a channel was ported, tested, and then wired to a
        /// constant, so the supported API is used and the warning is not tolerated.</para>
        /// </summary>
        static bool ApplySpriteGrid(TextureImporter importer, string assetPath, SpriteRect[] rects,
                                    UnitDefinition unit, Result result)
        {
            // SaveAndReimport invalidates the importer instance we hold, so re-acquire it by path.
            var current = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (current == null)
            {
                result.Warnings.Add($"{unit.id}: importer disappeared after reimport of {assetPath}");
                return false;
            }

            // Deliberately NOT re-imported here. The import mode has to be Multiple before the
            // provider is built, or the provider exposes a single-sprite sheet and the whole grid is
            // discarded without complaint — but SliceSheet already ran ConfigureBase + Multiple and
            // persisted both with its own reimport, so the provider below is looking at a
            // multi-sprite sheet already. Repeating the reimport made this a THIRD consecutive full
            // import of the same texture, and on the large sheets the previous one was still in
            // flight when the grid write landed, so the .meta write failed and the exception
            // propagated out of Run — aborting an entire run, with no summary, on 5 sheets
            // (sprGriffon2/4, sprGutsy1, sprProtect1, sprRanger1). Asserted instead of re-set, so a
            // mode that somehow did not stick is reported rather than silently throwing the grid away.
            if (current.spriteImportMode != SpriteImportMode.Multiple)
            {
                result.Warnings.Add($"{unit.id}: {assetPath} is in {current.spriteImportMode} sprite " +
                                    "mode, not Multiple, so the cell grid will be discarded.");
            }

            try
            {
                var factory = new SpriteDataProviderFactories();
                factory.Init();

                ISpriteEditorDataProvider provider = factory.GetSpriteEditorDataProviderFromObject(current);
                if (provider == null)
                {
                    result.Warnings.Add($"{unit.id}: no sprite data provider for {assetPath}");
                    return false;
                }

                provider.InitSpriteEditorDataProvider();
                provider.SetSpriteRects(rects);
                provider.Apply();
            }
            catch (Exception e)
            {
                result.Warnings.Add($"{unit.id}: writing the sprite grid for {assetPath} failed: {e.Message}");
                return false;
            }

            return Reimport(current, assetPath, unit, result);
        }

        /// <summary>
        /// Persist an importer and reimport its asset, retrying a write that loses a race with the
        /// import already in flight.
        ///
        /// <para><b>Why a retry is not paranoia here.</b> <c>SaveAndReimport</c> starts an import and
        /// returns before it finishes, so a write issued straight afterwards can collide with it. On
        /// Windows that surfaces as <c>Cannot open file '…meta' for write</c> and then <c>Failed to
        /// write meta file</c>, and the exception unwinds all the way out of <c>Run</c>. It is purely
        /// timing-dependent, which is why a real run lost exactly the slow sheets — <c>sprGriffon2</c>,
        /// <c>sprGriffon4</c>, <c>sprGutsy1</c>, <c>sprProtect1</c>, <c>sprRanger1</c> — and printed no
        /// summary at all. A locked <c>.meta</c> must degrade to a reported warning, never to a dead run:
        /// a half-imported project that claims nothing happened is the worst of the available outcomes.</para>
        /// </summary>
        static bool Reimport(TextureImporter importer, string assetPath, UnitDefinition unit, Result result)
        {
            for (int attempt = 1; attempt <= ReimportAttempts; attempt++)
            {
                try
                {
                    importer.SaveAndReimport();
                    return true;
                }
                catch (Exception e)
                {
                    if (attempt == ReimportAttempts)
                    {
                        result.Warnings.Add($"{unit.id}: could not reimport {assetPath} after " +
                                            $"{ReimportAttempts} attempts: {e.Message}");
                        return false;
                    }

                    // Back off, so the in-flight import can finish and release the .meta. Grows each
                    // round because the thing being waited on is a large texture decode, not a lock
                    // that clears in a fixed time.
                    System.Threading.Thread.Sleep(ReimportRetryDelayMs * attempt);
                }
            }

            return false;
        }

        /// <summary>
        /// <see cref="UnitSheetLayout.PivotFor"/> for one unit, additionally reporting a registration
        /// point that Unity's 0-1 pivot range cannot represent.
        /// </summary>
        static Vector2 PivotFor(UnitDefinition unit, Vector2Int cell, Result result)
        {
            Vector2Int reg = unit.registrationPoint;

            Vector2 raw = UnitSheetLayout.PivotFor(reg, cell);
            float cx = Mathf.Clamp01(raw.x);
            float cy = Mathf.Clamp01(raw.y);

            // Unity requires a pivot inside the unit square. The oracle permits a registration point
            // outside the cell, so clamping is a real loss of information and is reported.
            if (!Mathf.Approximately(cx, raw.x) || !Mathf.Approximately(cy, raw.y))
            {
                result.Warnings.Add($"{unit.id}: registration point ({reg.x},{reg.y}) is outside the " +
                                    $"cell {cell.x}x{cell.y}; pivot clamped from ({raw.x:F3},{raw.y:F3}) to " +
                                    $"({cx:F3},{cy:F3}) because Unity requires 0-1.");
            }

            return new Vector2(cx, cy);
        }

        /// <summary>
        /// Point the single-sprite <c>sprite</c> field at the unit's <b>resting frame</b> — the first
        /// cell of its <c>stay</c> state.
        ///
        /// <para><b>Deliberately not the icon cell, which is what this method used to write.</b>
        /// <c>icoX</c>/<c>icoY</c> read as "the cell that is the unit's picture", but that is not what
        /// the oracle does with them. <c>Unit.initIco</c> (<c>Unit.as:910-948</c>) blits that cell into
        /// <c>Unit.arrIcos</c>, a <i>static table</i> whose only reader is <c>PipPageInfo.as:439-442</c>
        /// — the PipBoy unit list. Nothing on the world renderer touches it. The five units that
        /// declare <c>icoY</c> are <c>merc1</c>–<c>merc5</c> with <c>icoY='2'</c>, and the <c>merc</c>
        /// family puts <c>die</c> at row 2 — so the old code made those five render their <b>death
        /// frame</b> as a standing sprite.</para>
        ///
        /// <para>The world sprite is whatever <c>animState</c> holds, and every unit class initialises
        /// that to <c>"stay"</c> (<c>Unit.as:2860</c>, <c>UnitAlicorn.as:189</c>,
        /// <c>UnitAnt.as:52</c>, …), so the resting frame is <c>stay</c>'s first cell. All 26
        /// <c>stay</c> declarations in <c>AllData.as</c> resolve to row 0 — 23 by the absent-<c>y</c>
        /// default and 3 written explicitly as <c>y='0'</c> — but the row is read back out of the
        /// parsed state rather than assumed, because assuming it is the mistake being fixed here.</para>
        ///
        /// <para><c>iconCell</c> stays on the definition as data for a future PipBoy reader; this
        /// method no longer conflates the two.</para>
        /// </summary>
        static void AssignRestingFrame(UnitDefinition unit, Sprite[] flat, int cols, int rows,
                                       Result result)
        {
            // One implementation of "which cell is the resting frame", shared with the runtime and
            // covered by the EditMode tests in Tests/Editor/UnitSprite.
            Vector2Int rest = UnitSheetLayout.RestingCell(unit.animations);
            int col = rest.x;
            int row = rest.y;

            if (row >= rows || col >= cols)
            {
                result.Warnings.Add($"{unit.id}: resting cell ({col},{row}) is outside the " +
                                    $"{cols}x{rows} sheet; no resting sprite assigned.");
                return;
            }

            Sprite sprite = flat[row * cols + col];
            if (sprite != null)
            {
                SetPrivateField(unit, "sprite", sprite);
            }
        }

        // ── DisplayObject pass ───────────────────────────────────────────────

        /// <summary>
        /// Which DisplayObject names supply this unit's visual, or null when there is none.
        /// Order: the declared <c>vclass</c>, then the <c>visual{Id}</c> convention, then the
        /// code-assigned table.
        /// </summary>
        static string[] ResolveVisualNames(UnitDefinition unit, Result result)
        {
            if (!string.IsNullOrWhiteSpace(unit.visualClassName))
            {
                return new[] { unit.visualClassName };
            }

            // Several units leave the visual to their controller, but the name is still mechanical:
            // bloat7 -> visualBloat7, vendor -> visualVendor, doctor -> visualDoctor. Verified against
            // the source tree before use, so a convention that stops holding is reported, not guessed at.
            string derived = "visual" + char.ToUpperInvariant(unit.id[0]) + unit.id.Substring(1);
            if (FindVisualFolder(derived) != null)
            {
                return new[] { derived };
            }

            if (CodeAssignedVisuals.TryGetValue(unit.id, out string[] explicitNames))
            {
                return explicitNames;
            }

            return null;
        }

        static bool ImportVisual(UnitDefinition unit, string[] visualNames, Result result)
        {
            var frames = new List<Sprite>();
            int copied = 0;

            foreach (string visualName in visualNames)
            {
                string sourceFolder = FindVisualFolder(visualName);
                if (sourceFolder == null)
                {
                    result.Warnings.Add($"{unit.id}: no DefineSprite_*_{visualName} folder in pfe/sprites");
                    continue;
                }

                string targetFolder = $"{VisualArtRoot}/{visualName}";
                EnsureDirectory(targetFolder);

                // Frame files are named 1.png, 2.png, … — sort numerically, not lexically, or frame 10
                // lands between 1 and 2.
                string[] pngs = Directory.GetFiles(sourceFolder, "*.png")
                    .OrderBy(UnitSheetLayout.FrameNumberOf)
                    .ToArray();

                if (pngs.Length == 0)
                {
                    result.Warnings.Add($"{unit.id}: {visualName} has no PNG frames");
                    continue;
                }

                foreach (string png in pngs)
                {
                    int frame = UnitSheetLayout.FrameNumberOf(png);
                    string dest = $"{targetFolder}/f{frame:D3}.png";
                    try
                    {
                        CopyIfNewer(png, dest);
                        copied++;
                    }
                    catch (Exception e)
                    {
                        result.Warnings.Add($"{unit.id}: could not copy {Path.GetFileName(png)}: {e.Message}");
                    }
                }

                AssetDatabase.ImportAsset(targetFolder, ImportAssetOptions.ImportRecursive);
                result.VisualsImported++;
            }

            if (copied == 0)
            {
                return false;
            }

            // Configure and collect. The pivot here is a documented first pass: the DisplayObject
            // exports carry no registration point, and deriving one needs the SWF symbol bounds that
            // CharacterSpriteImporter reads through SWFParser. Centre is the honest placeholder.
            foreach (string visualName in visualNames)
            {
                string targetFolder = $"{VisualArtRoot}/{visualName}";
                if (!AssetDatabase.IsValidFolder(targetFolder))
                {
                    continue;
                }

                string[] assets = AssetDatabase.FindAssets("t:Sprite", new[] { targetFolder });
                var ordered = assets
                    .Select(AssetDatabase.GUIDToAssetPath)
                    .OrderBy(p => UnitSheetLayout.FrameNumberOf(p))
                    .ToArray();

                foreach (string assetPath in ordered)
                {
                    var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
                    if (importer == null)
                    {
                        continue;
                    }

                    ConfigureBase(importer, MaxTextureSize);
                    importer.spriteImportMode = SpriteImportMode.Single;

                    var settings = new TextureImporterSettings();
                    importer.ReadTextureSettings(settings);
                    settings.spriteAlignment = (int)SpriteAlignment.Center;
                    importer.SetTextureSettings(settings);

                    // Same retry as the sheet path: these are single frames, but there are ~228 of them
                    // and the loop issues one import per frame, so the same .meta collision applies.
                    Reimport(importer, assetPath, unit, result);

                    var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
                    if (sprite != null)
                    {
                        frames.Add(sprite);
                    }
                }
            }

            if (frames.Count == 0)
            {
                return false;
            }

            // One row of frames, so the runtime can index a DisplayObject visual with the same
            // row * columns + column arithmetic it uses for sheets.
            SetPrivateField(unit, "spriteSheet", frames.ToArray());
            SetPrivateField(unit, "spriteSheetColumns", frames.Count);
            SetPrivateField(unit, "spriteSheetRows", 1);

            // A DisplayObject visual is a code-defined animation, not a sheet with named states, so
            // there is no `stay` row to look up: frame 0 is what it shows at rest. Without this the
            // single-sprite field stayed null and RoomUnitSpawner logged "has no sprite" for every
            // vclass unit even though its frames had just been imported.
            if (frames[0] != null)
            {
                SetPrivateField(unit, "sprite", frames[0]);
            }

            EditorUtility.SetDirty(unit);

            result.VisualFramesCopied += copied;
            return true;
        }

        static string FindVisualFolder(string visualName)
        {
            string root = Path.Combine(SourceImportPaths.PfeSpritesRoot, string.Empty);
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return null;
            }

            // Exact suffix match on the export's DefineSprite_{id}_{name} folder.
            string[] hits = Directory.GetDirectories(root, $"DefineSprite_*_{visualName}");
            return hits.Length > 0 ? hits[0] : null;
        }

        // ── Reporting ────────────────────────────────────────────────────────

        /// <summary>
        /// List every declared state that cannot be satisfied by its sheet.
        ///
        /// <para>AS3 copies a cell rect that may lie outside the bitmap, and <c>copyPixels</c> then
        /// copies nothing — so these states render <i>empty</i> in the oracle. That makes them content
        /// gaps to surface rather than errors to fix: <c>raider1</c>–<c>raider6</c>, <c>raider8</c>,
        /// <c>raider9</c> and <c>slaver4</c> have a 9-row sheet but the family declares <c>walk</c> at
        /// <c>y='9'</c>, and only <c>raider7</c> ships a 10-row sheet.</para>
        /// </summary>
        static void ReportOutOfRangeStates(List<UnitDefinition> units, Result result)
        {
            foreach (UnitDefinition unit in units)
            {
                if (unit.spriteSheetColumns <= 0 || unit.spriteSheetRows <= 0)
                {
                    continue;
                }

                if (unit.animations == null)
                {
                    continue;
                }

                foreach (string as3Id in AnimationSet.As3Ids)
                {
                    AnimationFrame frame = unit.animations.Get(as3Id);
                    if (!frame.HasFrames)
                    {
                        continue;
                    }

                    if (frame.row >= unit.spriteSheetRows)
                    {
                        result.OutOfRange.Add(
                            $"{unit.id}.{as3Id}: row {frame.row} but the sheet has " +
                            $"{unit.spriteSheetRows} row(s) — AS3 draws this state empty");
                    }

                    if (frame.length > unit.spriteSheetColumns)
                    {
                        result.OutOfRange.Add(
                            $"{unit.id}.{as3Id}: {frame.length} frames but the sheet has " +
                            $"{unit.spriteSheetColumns} column(s) — frames beyond the last are empty");
                    }
                }
            }
        }

        static string DescribeDetail(Result result)
        {
            var sb = new System.Text.StringBuilder();

            if (result.OutOfRange.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"States the sheet cannot satisfy ({result.OutOfRange.Count}) — these draw " +
                              "empty in AS3, so they are a content gap, not an import failure:");
                foreach (string line in result.OutOfRange.Take(40))
                {
                    sb.AppendLine("    " + line);
                }
            }

            if (result.Unresolved.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"Units with no visual ({result.Unresolved.Count}) — the pure family " +
                              "templates are expected here, since their variants carry the visual:");
                sb.AppendLine("    " + string.Join(", ", result.Unresolved.Take(60)));
            }

            if (result.Warnings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"Warnings ({result.Warnings.Count}):");
                foreach (string warning in result.Warnings.Take(40))
                {
                    sb.AppendLine("    " + warning);
                }
            }

            return sb.ToString();
        }

        // ── Shared helpers ───────────────────────────────────────────────────

        static void ConfigureBase(TextureImporter importer, int maxTextureSize)
        {
            importer.textureType = TextureImporterType.Sprite;
            importer.spritePixelsPerUnit = PixelsPerUnit;
            importer.filterMode = FilterMode.Point;              // pixel art: no bilinear blur
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.mipmapEnabled = false;
            importer.alphaIsTransparency = true;
            importer.maxTextureSize = maxTextureSize;
        }

        /// <summary>sheet id -> source PNG path, from both sprite swf exports.</summary>
        static Dictionary<string, string> BuildSheetIndex()
        {
            var index = new Dictionary<string, string>();

            foreach (string dir in new[]
                     {
                         SourceImportPaths.TextureImagesRoot("sprite"),
                         SourceImportPaths.TextureImagesRoot("sprite1"),
                     })
            {
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                {
                    continue;
                }

                foreach (string png in Directory.GetFiles(dir, "*.png"))
                {
                    string name = UnitSheetLayout.SheetNameFromFile(png);

                    if (index.ContainsKey(name))
                    {
                        Debug.LogWarning($"[UnitSpriteImporter] sheet '{name}' exists in more than one " +
                                         $"export; using {index[name]} and ignoring {png}");
                        continue;
                    }

                    index[name] = png;
                }
            }

            return index;
        }

        static List<UnitDefinition> LoadUnits()
        {
            if (!Directory.Exists(UnitAssetRoot))
            {
                return new List<UnitDefinition>();
            }

            return AssetDatabase.FindAssets("t:UnitDefinition", new[] { UnitAssetRoot })
                .Select(AssetDatabase.GUIDToAssetPath)
                .OrderBy(p => p)
                .Select(AssetDatabase.LoadAssetAtPath<UnitDefinition>)
                .Where(u => u != null)
                .ToList();
        }

        static void CopyIfNewer(string source, string destAssetPath)
        {
            string destFullPath = Path.GetFullPath(destAssetPath);
            EnsureDirectory(Path.GetDirectoryName(destFullPath).Replace('\\', '/'));

            if (!File.Exists(destFullPath) ||
                File.GetLastWriteTimeUtc(source) > File.GetLastWriteTimeUtc(destFullPath))
            {
                File.Copy(source, destFullPath, true);
            }
        }

        static void EnsureDirectory(string assetPath)
        {
            string full = Path.GetFullPath(assetPath);
            if (!Directory.Exists(full))
            {
                Directory.CreateDirectory(full);
            }
        }

        /// <summary>
        /// Write a field on the definition. <see cref="UnitDefinition"/> keeps its sprite fields
        /// private-with-Tooltip in places, so the importers in this project set them reflectively —
        /// this matches <see cref="UnitDataImporter"/>'s existing helper rather than introducing a
        /// second convention.
        /// </summary>
        static void SetPrivateField(UnitDefinition unit, string fieldName, object value)
        {
            var field = typeof(UnitDefinition).GetField(
                fieldName,
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic);

            if (field == null)
            {
                Debug.LogError($"[UnitSpriteImporter] UnitDefinition has no field '{fieldName}'");
                return;
            }

            field.SetValue(unit, value);
        }
    }
}
#endif
