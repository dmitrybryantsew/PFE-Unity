#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PFE.Data.Definitions;
using PFE.Data.Definitions.Campaign;
using PFE.Editor.Importers.SWF;
using PFE.Entities.Enemies.Archetypes;
using PFE.Systems.Particles;
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
        /// The NPC persona assets. Every NPC in the game shares the one <c>npc</c> unit row and carries
        /// its own <c>vis</c> (<c>GameData.as:669</c> <c>&lt;npc id='calam' vis='Calam'&gt;</c> →
        /// <c>UnitNPC.as:101-108</c> <c>visualCalam</c>), so the persona — not the unit id — is what
        /// names the art. <see cref="ImportNpcVisuals"/> is the pass that follows that indirection.
        /// </summary>
        const string NpcAssetRoot = "Assets/_PFE/Data/Resources/Campaign/NPCs";

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

            /// <summary>NPC personas whose <c>visual&lt;vis&gt;</c> art was found and copied.</summary>
            public int NpcVisualsImported;

            /// <summary>NPC personas wired to a sprite.</summary>
            public int NpcsWired;

            /// <summary>NPC personas whose <c>vis</c> names no importable symbol.</summary>
            public int NpcsWithNoVisual;

            /// <summary>NPC personas whose <c>osn</c> animation clip was found and imported.</summary>
            public int NpcClipsImported;

            /// <summary>Total frames copied across those clips.</summary>
            public int NpcClipFrames;

            /// <summary>
            /// Griffin units wired to a separate aiming arm (AS3 <c>UnitMerc.arm</c>). Counted
            /// separately from <see cref="UnitsWired"/> because it is a limb on a unit that already
            /// has a body — a unit can be wired and still have no arm, and that is the state the
            /// griffin shipped in.
            /// </summary>
            public int GriffinArmsWired;

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
                sb.AppendLine($"npc personas wired : {NpcsWired} ({NpcVisualsImported} visuals), " +
                              $"{NpcsWithNoVisual} with no visual");
                sb.AppendLine($"npc persona clips  : {NpcClipsImported} personas, {NpcClipFrames} frames " +
                              "(the `osn` MovieClip `UnitNPC.animate()` drives)");
                sb.AppendLine($"griffin arms wired : {GriffinArmsWired} " +
                              "(the separate aiming limb `UnitMerc.arm`)");
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

            // ── Pass 3: NPC persona visuals ──────────────────────────────────
            //
            // A separate pass because the art is keyed on the PERSONA, not the unit: every NPC in the
            // game is the one `npc` unit row with a different `vis`. Pass 2 can only ever resolve
            // `npc` itself (there is no `visualNpc` symbol), so without this every NPC — Calamity
            // included — would spawn as a collider with no art however complete the rest of the port is.
            try
            {
                ImportNpcVisuals(result);
            }
            catch (Exception e)
            {
                // Same reason passes 1 and 2 catch per item: an unhandled throw here unwinds out of Run
                // and the summary never prints, which reads as "the import did nothing".
                result.Warnings.Add($"npc visuals: pass threw {e.GetType().Name}: {e.Message}");
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // Report the states the sheet cannot satisfy, after the sheets are known.
            ReportOutOfRangeStates(units, result);

            // ── Pass 4: the griffin's separate aiming arm ────────────────────
            //
            // Deliberately last and outside the sheet pass: the arm is not a state of the body sheet,
            // it is a limb drawn beside it, so it is neither a cell to slice nor a DisplayObject to
            // wire to `spriteSheet`. See ImportGriffinArms for why the oracle needs it at all.
            try
            {
                ImportGriffinArms(units, result);
            }
            catch (Exception e)
            {
                result.Warnings.Add($"griffin arms: pass threw {e.GetType().Name}: {e.Message}");
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            return result;
        }

        // ── NPC persona pass ─────────────────────────────────────────────────

        /// <summary>
        /// Import the body art for every NPC persona — the port of <c>UnitNPC</c>'s
        /// <c>Res.getClass("visual" + npcXML.@vis, …)</c> (<c>UnitNPC.as:101-108</c>) — <b>and the
        /// persona's own animation clip inside it</b>.
        ///
        /// <para><b>Why the unit pass cannot cover this.</b> <c>AllData.as:4926</c> declares one row,
        /// <c>&lt;obj id='npc' cl='UnitNPC'&gt;</c>, and 108 personas place it. The art lives on the
        /// persona (<c>GameData.as:669</c> <c>vis='Calam'</c> → <c>visualCalam</c>), so a pass keyed on
        /// unit ids resolves <c>visualNpc</c> — which does not exist — and reports <c>npc</c> as a unit
        /// with no visual. That is the state the port was in.</para>
        ///
        /// <para><b>A persona is a container, and the animation is its child.</b> <c>visualCalam</c>
        /// (symbol 3622) has <b>one</b> frame whose entire content is a single child MovieClip named
        /// <c>osn</c> (symbol 3621), and <i>that</i> clip is where the 12 frames live. The same shape
        /// holds for all nine animating NPC personas: <c>visualDoctor</c>, <c>visualVendor</c>,
        /// <c>visualVendorArmor</c>, <c>visualVendorExpl</c>, <c>visualVendorWeapon</c> (96 frames each),
        /// <c>visualCalamEncl</c> (5), <c>visualCalam</c> (12), <c>visualPatient</c> and
        /// <c>visualSpriteBot</c> (2 each). Importing the outer symbol alone — which is what this pass
        /// did — yields exactly one picture per persona, so <c>vis.osn.gotoAndStop("fly")</c> has nothing
        /// to stop on and Calamity stands in his walking pose for the whole cutscene.</para>
        ///
        /// <para><b>The clip's frames are the same pictures, at a different scale.</b> The <c>osn</c>
        /// placement carries a matrix — identity for none of the personas that matter: Calam's is
        /// <c>1.769</c>, the doctor's and every vendor's is <c>(-0.316, 0.316)</c>, i.e. <b>mirrored</b>
        /// as well as shrunk. The scale is stored on the definition
        /// (<see cref="NpcDefinition.visualScaleX"/>) and applied at draw time rather than resampled into
        /// the PNGs, so the texture stays at the SWF's native resolution. The placement's
        /// <i>translation</i> needs no handling at all: both exports are trimmed to their own content
        /// box, and translating the whole of a symbol's content before trimming is a no-op.</para>
        ///
        /// <para><b>114 of the 175 <c>visual*</c> symbols have no <c>osn</c> child.</b> Those are
        /// genuinely static props and effects, and a persona using one is genuinely static — so a missing
        /// clip is not reported as a failure. The outer symbol's own frames are imported instead, exactly
        /// as before.</para>
        /// </summary>
        static void ImportNpcVisuals(Result result)
        {
            List<NpcDefinition> npcs = LoadNpcs();
            if (npcs.Count == 0)
            {
                result.Warnings.Add($"No NpcDefinition assets under {NpcAssetRoot}. " +
                                    "Run PFE/Data/Import Campaign first.");
                return;
            }

            // Resolved once for the whole pass. The symbol table and the SWF timeline are the same for
            // all 54 personas, and re-parsing an 11 MB SWF per persona would turn a minute into an hour.
            Dictionary<string, int> symbols = ReadPfeSymbolTable(result);
            SWFFile swf = ParseAssetsSwf(result);

            foreach (NpcDefinition npc in npcs)
            {
                if (string.IsNullOrWhiteSpace(npc.visualId))
                {
                    // `vis` is optional in the oracle: with no `@vis` the constructor falls back to
                    // `visualVendor` (UnitNPC.as:107). That is a real fallback, not a gap, so it is not
                    // reported as a missing visual.
                    continue;
                }

                string visualName = VisualClassName(npc.visualId);

                try
                {
                    if (ImportNpcVisual(npc, visualName, symbols, swf, result))
                    {
                        result.NpcsWired++;
                    }
                    else
                    {
                        result.NpcsWithNoVisual++;
                    }
                }
                catch (Exception e)
                {
                    result.Warnings.Add($"{npc.npcId}: npc visual import threw {e.GetType().Name}: " +
                                        $"{e.Message}");
                }
            }
        }

        /// <summary>
        /// AS3's <c>"visual" + npcXML.@vis</c> (<c>UnitNPC.as:101</c>) — capitalising the first letter,
        /// because the data is mixed-case (<c>vis='calam'</c> is not used; <c>vis='Calam'</c> is) and a
        /// bare concatenation would miss every lower-case one.
        /// </summary>
        static string VisualClassName(string visualId)
        {
            return visualId.StartsWith("visual", StringComparison.Ordinal)
                ? visualId
                : "visual" + char.ToUpperInvariant(visualId[0]) + visualId.Substring(1);
        }

        /// <summary>
        /// One persona's frames, resolved from the export <b>before any file is touched</b>.
        ///
        /// <para>Split out so the "where do this persona's pictures come from" question has one answer
        /// that a fixture can reason about, instead of being interleaved with the copying.</para>
        /// </summary>
        sealed class PersonaClip
        {
            /// <summary>The source PNGs, ordered by frame number. Index <c>i</c> is timeline frame
            /// <c>i + 1</c>, which is what makes <c>gotoAndPlay</c> a list walk.</summary>
            public string[] FrameFiles;

            /// <summary>Frame number → label, for the frames that carry one.</summary>
            public readonly Dictionary<int, string> Labels = new Dictionary<int, string>();

            /// <summary>The <c>osn</c> placement's scale, applied at draw time. <c>1</c> when the frames
            /// are the outer symbol's own.</summary>
            public float ScaleX = 1f;
            public float ScaleY = 1f;

            /// <summary>True when these frames came from the persona's <c>osn</c> child.</summary>
            public bool HasOsn;
        }

        /// <summary>
        /// Find the frames a persona animates with — the <c>osn</c> child's timeline when it has one,
        /// the outer symbol's own frames otherwise.
        /// </summary>
        static PersonaClip ResolvePersonaClip(NpcDefinition npc, string visualName,
                                              Dictionary<string, int> symbols, SWFFile swf,
                                              Result result)
        {
            string visualFolder = FindVisualFolder(visualName);
            if (visualFolder == null)
            {
                result.Warnings.Add($"{npc.npcId}: no DefineSprite_*_{visualName} folder in pfe/sprites " +
                                    $"(vis='{npc.visualId}') — the persona spawns with no art.");
                return null;
            }

            PersonaClip clip = TryResolveOsnClip(npc, visualName, symbols, swf, result);
            if (clip != null)
            {
                return clip;
            }

            // No clip: the outer symbol's own frames. For the 114 personas with no `osn` child this is
            // the whole visual and the frame count is 1.
            string[] files = Directory.GetFiles(visualFolder, "*.png")
                .OrderBy(UnitSheetLayout.FrameNumberOf)
                .ToArray();

            if (files.Length == 0)
            {
                result.Warnings.Add($"{npc.npcId}: {visualName} has no PNG frames");
                return null;
            }

            return new PersonaClip { FrameFiles = files, HasOsn = false };
        }

        /// <summary>
        /// The persona's <c>osn</c> clip, or <c>null</c> when it has none — which is the common case and
        /// not a warning.
        /// </summary>
        static PersonaClip TryResolveOsnClip(NpcDefinition npc, string visualName,
                                             Dictionary<string, int> symbols, SWFFile swf,
                                             Result result)
        {
            if (symbols == null || swf == null)
            {
                // The table or the SWF could not be read. Already reported once by the readers; falling
                // back to the outer symbol keeps every persona's art present.
                return null;
            }

            if (!symbols.TryGetValue(visualName, out int visualSymbolId))
            {
                result.Warnings.Add($"{npc.npcId}: '{visualName}' is not in the symbol table, so its " +
                                    "`osn` clip cannot be located. Only the resting frame is imported.");
                return null;
            }

            if (!swf.Symbols.TryGetValue(visualSymbolId, out SWFSymbol visualSymbol))
            {
                result.Warnings.Add($"{npc.npcId}: '{visualName}' ({visualSymbolId}) is not a DefineSprite " +
                                    "in assets.swf, so its `osn` clip cannot be located.");
                return null;
            }

            SWFPlacement osn = FindPlacement(visualSymbol, "osn");
            if (osn == null)
            {
                // 114 of 175 `visual*` symbols are like this. Not a gap — see the pass summary.
                return null;
            }

            if (osn.CharacterId <= 0)
            {
                result.Warnings.Add($"{npc.npcId}: {visualName}'s `osn` placement names no character " +
                                    "(characterId " + osn.CharacterId + "), so it has no timeline.");
                return null;
            }

            string[] folders = SourceImportPaths.PfeSpriteFoldersById(osn.CharacterId);
            if (folders.Length != 1)
            {
                // 0 means the export is incomplete; >1 means the id is ambiguous. Both are reported
                // rather than guessed at, because picking the wrong folder silently animates the
                // persona with another character's frames.
                result.Warnings.Add($"{npc.npcId}: {visualName}'s `osn` is symbol {osn.CharacterId}, but " +
                                    $"pfe/sprites holds {folders.Length} folder(s) matching " +
                                    $"DefineSprite_{osn.CharacterId}*. Only the resting frame is imported.");
                return null;
            }

            string[] files = Directory.GetFiles(folders[0], "*.png")
                .OrderBy(UnitSheetLayout.FrameNumberOf)
                .ToArray();

            if (files.Length == 0)
            {
                result.Warnings.Add($"{npc.npcId}: {visualName}'s `osn` folder {folders[0]} holds no PNGs.");
                return null;
            }

            var clip = new PersonaClip
            {
                FrameFiles = files,
                ScaleX = osn.Scale.x,
                ScaleY = osn.Scale.y,
                HasOsn = true,
            };

            if (swf.Symbols.TryGetValue(osn.CharacterId, out SWFSymbol osnSymbol))
            {
                foreach (SWFFrame frame in osnSymbol.Frames)
                {
                    if (!string.IsNullOrEmpty(frame.Label))
                    {
                        clip.Labels[frame.FrameNumber] = frame.Label;
                    }
                }

                if (osnSymbol.FrameCount != files.Length)
                {
                    result.Warnings.Add($"{npc.npcId}: {visualName}'s `osn` ({osn.CharacterId}) declares " +
                                        $"{osnSymbol.FrameCount} frame(s) but the export holds " +
                                        $"{files.Length} PNG(s). The clip is imported as exported.");
                }
            }
            else
            {
                result.Warnings.Add($"{npc.npcId}: {visualName}'s `osn` ({osn.CharacterId}) has no PNG " +
                                    "export, so the clip's frames carry no labels.");
            }

            return clip;
        }

        /// <summary>
        /// The first placement named <paramref name="instanceName"/> across a symbol's frames.
        ///
        /// <para><b>Why every frame and not just frame 1.</b> A child can be placed on a later frame —
        /// <c>visualUltraSentinel2</c> places its <c>osn</c> alongside two shapes, and the placement tag
        /// may be a <c>move</c> that the parser resolves by cloning the depth's existing entry. Walking
        /// the frames finds it wherever it was first placed.</para>
        /// </summary>
        static SWFPlacement FindPlacement(SWFSymbol symbol, string instanceName)
        {
            if (symbol?.Frames == null)
            {
                return null;
            }

            foreach (SWFFrame frame in symbol.Frames)
            {
                foreach (SWFPlacement placement in frame.Placements)
                {
                    if (placement != null && string.Equals(placement.InstanceName, instanceName,
                                                            StringComparison.Ordinal))
                    {
                        return placement;
                    }
                }
            }

            return null;
        }

        /// <summary>Reads <c>pfe/symbolClass/symbols.csv</c> → symbol name → symbol id.</summary>
        static Dictionary<string, int> ReadPfeSymbolTable(Result result)
        {
            string path = SourceImportPaths.PfeSymbolTablePath;
            if (!File.Exists(path))
            {
                result.Warnings.Add(SourceImportPaths.MissingSourceMessage(path, "symbols.csv") +
                                    " Persona `osn` clips cannot be located without it; only the " +
                                    "resting frames will be imported.");
                return null;
            }

            Dictionary<string, int> table = ParticleSpriteSource.ParseSymbolCsv(
                File.ReadAllText(path), out int skipped);

            if (skipped > 0)
            {
                result.Warnings.Add($"{path}: {skipped} row(s) did not parse.");
            }

            if (table.Count == 0)
            {
                result.Warnings.Add($"{path} parsed to an empty symbol table.");
                return null;
            }

            return table;
        }

        /// <summary>Parses <c>pfe/scripts/_assets/assets.swf</c> once, for the persona clip pass.</summary>
        static SWFFile ParseAssetsSwf(Result result)
        {
            string path = SourceImportPaths.AssetsSwfPath;
            if (!File.Exists(path))
            {
                result.Warnings.Add(SourceImportPaths.MissingSourceMessage(path, "assets.swf") +
                                    " Persona `osn` clips cannot be located without it.");
                return null;
            }

            try
            {
                return new SWFParser().Parse(path);
            }
            catch (Exception e)
            {
                result.Warnings.Add($"Could not parse {path}: {e.GetType().Name}: {e.Message}. " +
                                    "Persona clips fall back to the resting frame.");
                return null;
            }
        }

        /// <summary>
        /// Copy one persona's frames and wire the clip and the resting frame onto the definition.
        /// </summary>
        /// <returns>True when a sprite was assigned.</returns>
        static bool ImportNpcVisual(NpcDefinition npc, string visualName,
                                    Dictionary<string, int> symbols, SWFFile swf, Result result)
        {
            PersonaClip clip = ResolvePersonaClip(npc, visualName, symbols, swf, result);
            if (clip == null)
            {
                return false;
            }

            string targetFolder = $"{VisualArtRoot}/{visualName}";
            EnsureDirectory(targetFolder);

            int copied = 0;
            foreach (string png in clip.FrameFiles)
            {
                int frame = UnitSheetLayout.FrameNumberOf(png);
                if (frame <= 0)
                {
                    // FrameNumberOf yields 0 for a name that is not a frame number. Copying it would
                    // write f000.png and then be dropped from the clip for having no number, i.e. a
                    // file on disk that nothing can ever address. Report instead.
                    result.Warnings.Add($"{npc.npcId}: {Path.GetFileName(png)} is not named after a frame " +
                                        "number and was skipped.");
                    continue;
                }

                string dest = $"{targetFolder}/f{frame:D3}.png";
                try
                {
                    CopyIfNewer(png, dest);
                    copied++;
                }
                catch (Exception e)
                {
                    result.Warnings.Add($"{npc.npcId}: could not copy {Path.GetFileName(png)}: {e.Message}");
                }
            }

            if (copied == 0)
            {
                return false;
            }

            AssetDatabase.ImportAsset(targetFolder, ImportAssetOptions.ImportRecursive);
            result.NpcVisualsImported++;

            // The frame list is built from the SOURCE files, not from an asset scan: a folder that still
            // holds a longer clip's leftovers would otherwise contribute frames this persona's timeline
            // does not have. Each expected asset is looked up by the name the copy above just wrote.
            var frames = new List<NpcVisualFrame>();
            foreach (string png in clip.FrameFiles)
            {
                int frameNumber = UnitSheetLayout.FrameNumberOf(png);
                string assetPath = $"{targetFolder}/f{frameNumber:D3}.png";

                var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
                if (importer == null)
                {
                    result.Warnings.Add($"{npc.npcId}: no TextureImporter at {assetPath}");
                    continue;
                }

                ConfigureBase(importer, MaxTextureSize);
                importer.spriteImportMode = SpriteImportMode.Single;

                var settings = new TextureImporterSettings();
                importer.ReadTextureSettings(settings);
                settings.spriteAlignment = (int)SpriteAlignment.Center;
                importer.SetTextureSettings(settings);

                Reimport(importer, assetPath, npc.npcId, result);

                var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
                if (sprite == null)
                {
                    result.Warnings.Add($"{npc.npcId}: frame {frameNumber} did not load as a Sprite");
                    continue;
                }

                clip.Labels.TryGetValue(frameNumber, out string label);

                frames.Add(new NpcVisualFrame
                {
                    frameNumber = frameNumber,
                    label = label ?? string.Empty,
                    sprite = sprite,
                });
            }

            if (frames.Count == 0)
            {
                return false;
            }

            // Assigned directly (the fields are public) and then marked dirty, because the import this
            // pass is running triggers a domain reload and an unmarked asset would lose the references.
            npc.visualFrames = frames;
            npc.visualScaleX = clip.ScaleX;
            npc.visualScaleY = clip.ScaleY;
            npc.visualSprite = npc.RestingVisualFrame;
            EditorUtility.SetDirty(npc);

            result.VisualFramesCopied += copied;

            if (clip.HasOsn)
            {
                result.NpcClipsImported++;
                result.NpcClipFrames += frames.Count;
            }

            return true;
        }

        static List<NpcDefinition> LoadNpcs()
        {
            if (!Directory.Exists(NpcAssetRoot))
            {
                return new List<NpcDefinition>();
            }

            return AssetDatabase.FindAssets("t:NpcDefinition", new[] { NpcAssetRoot })
                .Select(AssetDatabase.GUIDToAssetPath)
                .OrderBy(p => p)
                .Select(AssetDatabase.LoadAssetAtPath<NpcDefinition>)
                .Where(n => n != null)
                .ToList();
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
            => Reimport(importer, assetPath, unit.id, result);

        /// <summary>
        /// The same retry, addressed by a label rather than a unit — the NPC pass has an
        /// <see cref="NpcDefinition"/>, not a <see cref="UnitDefinition"/>, and the only thing the
        /// warning needs is a name to blame.
        /// </summary>
        static bool Reimport(TextureImporter importer, string assetPath, string label, Result result)
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
                        result.Warnings.Add($"{label}: could not reimport {assetPath} after " +
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

        // ── Griffin arm pass ─────────────────────────────────────────────────

        /// <summary>
        /// Import the five <c>visualGrifArm1..5</c> limbs and hang each one on its griffin.
        ///
        /// <para><b>Why the griffin needs a limb the body does not have.</b> Every other unit holds its
        /// weapon at a point on its own body. <c>UnitMerc</c> overrides <c>setWeaponPos</c> to hang the
        /// weapon on an <c>emit</c> marker inside a <b>separate rotating arm clip</b>
        /// (<c>UnitMerc.as:29</c>, <c>:95-109</c>), so the gun follows the hand as the griffin aims
        /// instead of sliding around its chest. Without the limb the weapon is drawn at the body hold
        /// point and does not look held at all — the reported "its weapon does not turn to the side".</para>
        ///
        /// <para><b>The pivot is baked from measurement, not left at centre.</b> The arm rotates about
        /// its registration point — the shoulder — and that point exists nowhere in the AS3 source: it
        /// is the symbol's own registration, recoverable only from the SWF. It was measured for all five
        /// arms (<see cref="GriffinArmMath.Variants"/>) by taking the union of each arm's <i>rendered</i>
        /// children's bounds through their placement matrices; each result matches the exported PNG's
        /// size to within a pixel, which is what makes the numbers trustworthy rather than plausible.</para>
        ///
        /// <para>Baking it here rather than compensating at runtime is deliberate: every other
        /// DisplayObject visual in this import gets <c>SpriteAlignment.Center</c> as a documented
        /// placeholder, and the runtime compensates for that
        /// (<see cref="PFE.Entities.Units.UnitSpriteAnchor"/> for bodies,
        /// <c>GriffinArmRig.BuildArt</c> for the arm). Writing the real pivot means the compensation
        /// computes to zero and the sprite is also correct in the editor, where nothing compensates.</para>
        /// </summary>
        static void ImportGriffinArms(List<UnitDefinition> units, Result result)
        {
            foreach (UnitDefinition unit in units)
            {
                int variant = GriffinArmVariantFor(unit);
                if (variant == 0)
                {
                    continue;
                }

                GriffinArmMath.GriffinArmVariant spec = GriffinArmMath.Variants[variant - 1];

                string sourceFolder = FindVisualFolder(spec.SymbolName);
                if (sourceFolder == null)
                {
                    result.Warnings.Add(
                        $"{unit.id}: no DefineSprite_*_{spec.SymbolName} folder in pfe/sprites, so the " +
                        "griffin keeps the body hold point instead of its hand");
                    continue;
                }

                string targetFolder = $"{VisualArtRoot}/{spec.SymbolName}";
                EnsureDirectory(targetFolder);

                string[] pngs = Directory.GetFiles(sourceFolder, "*.png")
                    .OrderBy(UnitSheetLayout.FrameNumberOf)
                    .ToArray();

                if (pngs.Length == 0)
                {
                    result.Warnings.Add($"{unit.id}: {spec.SymbolName} has no PNG frames");
                    continue;
                }

                string firstAssetPath = null;
                foreach (string png in pngs)
                {
                    int frame = UnitSheetLayout.FrameNumberOf(png);
                    string dest = $"{targetFolder}/f{frame:D3}.png";
                    CopyIfNewer(png, dest);
                    if (firstAssetPath == null)
                    {
                        firstAssetPath = dest;
                    }
                }

                AssetDatabase.ImportAsset(targetFolder, ImportAssetOptions.ImportRecursive);

                // The pivot needs the sprite's pixel size, which needs the texture to exist — hence the
                // import above, then a reimport once the custom pivot is written. The size comes off the
                // PNG header rather than off the imported texture so a stale .meta cannot feed a stale
                // size back into the pivot.
                Vector2 size = PngPixelSize(pngs[0]);
                if (size.x <= 0f || size.y <= 0f)
                {
                    result.Warnings.Add($"{unit.id}: could not read {spec.SymbolName}'s PNG size");
                    continue;
                }

                float pivotX = spec.OriginPixels.x / size.x;
                float pivotY = 1f - (spec.OriginPixels.y / size.y);

                foreach (string png in pngs)
                {
                    int frame = UnitSheetLayout.FrameNumberOf(png);
                    string assetPath = $"{targetFolder}/f{frame:D3}.png";

                    var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
                    if (importer == null)
                    {
                        continue;
                    }

                    ConfigureBase(importer, MaxTextureSize);
                    importer.spriteImportMode = SpriteImportMode.Single;

                    var settings = new TextureImporterSettings();
                    importer.ReadTextureSettings(settings);
                    settings.spriteAlignment = (int)SpriteAlignment.Custom;
                    settings.spritePivot = new Vector2(pivotX, pivotY);
                    importer.SetTextureSettings(settings);

                    Reimport(importer, assetPath, unit, result);
                }

                var armSprite = AssetDatabase.LoadAssetAtPath<Sprite>(firstAssetPath);
                if (armSprite == null)
                {
                    result.Warnings.Add($"{unit.id}: {spec.SymbolName} imported but no Sprite could be loaded");
                    continue;
                }

                SetPrivateField(unit, "armSprite", armSprite);
                EditorUtility.SetDirty(unit);
                result.GriffinArmsWired++;
            }
        }

        /// <summary>
        /// Which griffin arm belongs to a unit — AS3's <c>tr</c>, 1-based, or 0 for "no arm".
        ///
        /// <para>A one-line wrapper over <see cref="GriffinArmMath.VariantIndexForSpriteSheet"/> so the
        /// importer and the runtime cannot disagree about which sheet pairs with which limb. The rule
        /// itself lives in the math class, which is where it is documented and where it is testable
        /// without the editor.</para>
        /// </summary>
        static int GriffinArmVariantFor(UnitDefinition unit)
            => unit == null ? 0 : GriffinArmMath.VariantIndexForSpriteSheet(unit.spriteSheetId) + 1;

        /// <summary>
        /// A PNG's pixel size, read straight from the IHDR chunk (bytes 16-24) — no decode, no texture.
        ///
        /// <para>Read from the <i>source</i> rather than from the imported texture because the pivot is
        /// written in the same step that creates the texture: a first import would have to happen with
        /// no pivot, and a stale <c>.meta</c> from an earlier run would then feed its own size back in.
        /// Returns zero on a file that is not a readable PNG, which the caller reports.</para>
        /// </summary>
        static Vector2 PngPixelSize(string path)
        {
            try
            {
                using (var stream = File.OpenRead(path))
                {
                    var header = new byte[24];
                    if (stream.Read(header, 0, header.Length) < header.Length)
                    {
                        return Vector2.zero;
                    }

                    // 8-byte signature, 4-byte length, "IHDR", then width and height big-endian.
                    int width  = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
                    int height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
                    return new Vector2(width, height);
                }
            }
            catch (Exception)
            {
                return Vector2.zero;
            }
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

        /// <summary>
        /// Copy a source frame over its imported copy, unless the two already hold the same bytes.
        /// </summary>
        /// <remarks>
        /// <para><b>This used to compare timestamps, and that silently kept a WRONG frame.</b> The test
        /// was <c>source.mtime &gt; dest.mtime</c>, which answers "is the source newer", not "is the
        /// destination right". A destination written by an earlier, different import can easily be
        /// newer than the source it should be replaced from — both come out of the same export, so
        /// their timestamps are within seconds of each other.</para>
        ///
        /// <para><b>What it cost.</b> Before the persona clip pass existed, <c>f001.png</c> was written
        /// from the persona's <i>outer</i> symbol (<c>DefineSprite_3622_visualCalam</c>), which renders
        /// its <c>osn</c> child through the placement matrix — so the frame arrived with the persona's
        /// own <c>1.769</c> scale already baked into the pixels: 153×151 instead of the inner sprite's
        /// 86×85. The clip pass then wrote frames 2..12 from the inner symbol
        /// (<c>DefineSprite_3621</c>) at native size, and left frame 1 alone because its destination
        /// was not older. At runtime <see cref="PFE.Data.Definitions.Campaign.NpcDefinition.visualScaleX"/>
        /// is applied on top, so the resting frame was drawn at <c>1.769²</c> — <b>3.13× too big</b>,
        /// and visibly so the instant an NPC landed out of its <c>fly</c> pose into <c>stay</c>.
        /// The same shape affected <c>visualCalamEncl</c>, <c>visualDoctor</c> and the four vendors,
        /// whose composed frame 1 is scaled <i>down</i> by 0.316 and therefore drawn 10× too small.</para>
        ///
        /// <para>Comparing bytes is the honest test and it is cheap here: ~230 small PNGs, and a size
        /// mismatch short-circuits before any read. It also makes the pass idempotent in the way the
        /// old one claimed to be — re-running it now <i>repairs</i> a stale frame instead of preserving
        /// it, which is the only reason a fix to this importer reaches the folders that already
        /// exist.</para>
        /// </remarks>
        static void CopyIfNewer(string source, string destAssetPath)
        {
            string destFullPath = Path.GetFullPath(destAssetPath);
            EnsureDirectory(Path.GetDirectoryName(destFullPath).Replace('\\', '/'));

            if (File.Exists(destFullPath) && !Differs(source, destFullPath))
            {
                return;
            }

            File.Copy(source, destFullPath, true);
        }

        /// <summary>
        /// Whether two files hold different bytes. Size first, then a chunked compare — the size test
        /// alone would miss a same-length edit, and it is the size test that catches the frame-1 case
        /// before reading anything.
        /// </summary>
        static bool Differs(string a, string b)
        {
            var left = new FileInfo(a);
            var right = new FileInfo(b);

            if (left.Length != right.Length)
            {
                return true;
            }

            const int chunk = 8192;
            var bufferA = new byte[chunk];
            var bufferB = new byte[chunk];

            using (FileStream streamA = File.OpenRead(a))
            using (FileStream streamB = File.OpenRead(b))
            {
                while (true)
                {
                    int readA = streamA.Read(bufferA, 0, chunk);
                    int readB = streamB.Read(bufferB, 0, chunk);

                    if (readA != readB)
                    {
                        return true;
                    }

                    if (readA == 0)
                    {
                        return false;
                    }

                    for (int i = 0; i < readA; i++)
                    {
                        if (bufferA[i] != bufferB[i])
                        {
                            return true;
                        }
                    }
                }
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
