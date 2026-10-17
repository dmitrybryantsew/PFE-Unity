#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PFE.Data.Definitions;
using PFE.UI.PipBuck;
using UnityEditor;
using UnityEngine;

namespace PFE.Editor.Importers.SWF
{
    /// <summary>
    /// Copies the in-game interface art, cursors and font out of the pfe extraction tree and into
    /// <c>Assets/_PFE/</c>, configures their import settings, and builds the
    /// <see cref="PipBuckCompositionDefinition"/> that the runtime interface reads.
    ///
    /// <para>UI <i>sounds</i> are deliberately not part of this pipeline — they are already imported and
    /// catalogued by the existing <c>SoundImporter</c>; see the note above
    /// <see cref="FontSourceFileName"/>.</para>
    ///
    /// <para><b>Follows <see cref="MainMenuSpriteImporter"/> deliberately.</b> That pipeline is the one
    /// already proven on this project — extract with JPEXS, copy deterministically into
    /// <c>Art/</c>, configure the importer, then emit a composition asset — so the interface pipeline is
    /// the same shape rather than a second, differently-behaved one. Anything that works there works
    /// here, including the idempotence rule below.</para>
    ///
    /// <para><b>Idempotent.</b> A PNG is copied only when the source is newer than the destination, and
    /// a texture importer is only re-saved when a setting actually differs. Re-running over a correct
    /// tree must be a no-op: an importer that rewrites everything on every run makes it impossible to
    /// tell "the import fixed it" from "the import re-ran".</para>
    ///
    /// <para><b>Every symbol id and frame count below was counted on disk</b> (2026-10-11) rather than
    /// copied from the specification, and four of the specification's figures were wrong. The counts are
    /// asserted at the end of a run so a re-export that changes one is reported instead of silently
    /// producing a short array — which is the failure mode the positional icon catalogs cannot survive.</para>
    /// </summary>
    public static class InterfaceAssetImporter
    {
        // ── Output locations ──────────────────────────────────────────────────

        public const string ArtUiRoot = "Assets/_PFE/Art/UI";
        public const string InterfaceRoot = ArtUiRoot + "/Interface";
        public const string IconsRoot = ArtUiRoot + "/Icons";
        public const string CursorsRoot = ArtUiRoot + "/Cursors";
        public const string FontsRoot = ArtUiRoot + "/Fonts";

        /// <summary>Where the generated composition asset lives. Mirrors the main menu's
        /// <c>Data/Resources/MainMenu/MainMenuComposition.asset</c>.</summary>
        public const string CompositionPath = "Assets/_PFE/Data/Resources/UI/PipBuckComposition.asset";

        /// <summary>Source pixels per world unit — the scale every other art import and the world itself use.</summary>
        public const int PixelsPerUnit = 100;

        // ── Specifications ────────────────────────────────────────────────────

        /// <summary>A single-frame symbol copied to one named PNG.</summary>
        public sealed class SingleSpriteSpec
        {
            public int SymbolId;
            public string DestFileName;
            public string Description;

            public SingleSpriteSpec(int symbolId, string destFileName, string description)
            {
                SymbolId = symbolId;
                DestFileName = destFileName;
                Description = description;
            }
        }

        /// <summary>A multi-frame symbol copied to <c>{DestFolder}/f{NNN}.png</c>.</summary>
        public sealed class SequenceSpec
        {
            public int SymbolId;
            public string DestFolder;
            public int ExpectedFrames;
            /// <summary>Frames to take from the front of the clip; 0 means all of them.</summary>
            public int TakeFrames;

            /// <summary>
            /// Frames to skip at the front before taking any. <c>0</c> (the default) starts at frame 1.
            ///
            /// <para>Exists because ONE symbol can hold two unrelated id spaces. <c>vskill</c> (383) is
            /// 16 skill frames followed by 79 perk frames, and the two catalogs must land in separate
            /// folders each indexed from zero. Without this the only options were one mixed catalog or
            /// a second copy of the clip on disk — and a second copy is exactly the kind of duplicate
            /// source that drifts.</para>
            /// </summary>
            public int SkipFrames;

            public string Description;

            public SequenceSpec(int symbolId, string destFolder, int expectedFrames, int takeFrames,
                                string description, int skipFrames = 0)
            {
                SymbolId = symbolId;
                DestFolder = destFolder;
                ExpectedFrames = expectedFrames;
                TakeFrames = takeFrames;
                SkipFrames = skipFrames;
                Description = description;
            }
        }

        /// <summary>A two-state button symbol whose frames get distinct file names.</summary>
        public sealed class TwoStateSpec
        {
            public int SymbolId;
            public string State1FileName;
            public string State2FileName;
            public string Description;

            public TwoStateSpec(int symbolId, string state1FileName, string state2FileName, string description)
            {
                SymbolId = symbolId;
                State1FileName = state1FileName;
                State2FileName = state2FileName;
                Description = description;
            }
        }

        /// <summary>
        /// Single-frame hardware chrome. Frame counts verified on disk 2026-10-11.
        /// </summary>
        public static readonly SingleSpriteSpec[] SingleSprites =
        {
            new(1155, "PipBuck_Bezel.png",       "visPipBuck — device casing and screen frame"),
            new(1227, "PipBuck_CRTScanlines.png","vispipe — CRT scanline overlay"),
            new(4651, "HUD_MasterLayout.png",    "visualGUI — master HUD container"),
            new(4180, "HUD_HPBar.png",           "hpBar — player health bar"),
            new(4360, "HUD_BossBar.png",         "hpBarBoss — top-centre boss bar"),
            new(4168, "SATS_Reticle.png",        "satsRadius — S.A.T.S. targeting circle"),
            new(4162, "SATS_Cell.png",           "satsCel — S.A.T.S. per-unit target cell"),
            new(1129, "PipBuck_MonitorRow.png",  "visPipRItem — right-side monitor row template"),
            new(990,  "PipBuck_InfoMap.png",     "visPipMap — information page map window (page 3)"),
            new(834,  "PipBuck_InfoWorldMap.png","visPipWMap — information page travel map (page 3)"),
            new(833,  "PipBuck_InfoLandNode.png","sim_wmap — travel node backdrop (page 3)"),
            new(508,  "PipBuck_InfoConfirm.png", "butOk — information page confirm button (page 3)"),
        };

        // ── Hand-authored inventory-page chrome ───────────────────────────────
        //
        // These four PNGs are NOT exported by Import(). They exist only because the exported art for
        // their symbols is unusable (see the long comment in GenerateComposition). They are named as
        // consts so GenerateComposition (which must ASSIGN them, or a re-import nulls the composition
        // fields) and Verify (which must CHECK them, or a re-import that loses them goes unnoticed)
        // cannot drift apart.
        public const string InventoryRowBackFile  = "PipBuck_InvRowBack.png";
        public const string InventoryRowFrameFile = "PipBuck_InvRowFrame.png";
        public const string ScrollBarTrackFile    = "PipBuck_ScrollBarTrack.png";
        public const string ScrollBarThumbFile    = "PipBuck_ScrollBarThumb.png";

        /// <summary>The four hand-authored inventory-page sprites, in composition-field order.</summary>
        public static readonly (string FileName, string What)[] HandAuthoredSprites =
        {
            (InventoryRowBackFile,  "inventoryRowBack — visPipInvItem.back (498)"),
            (InventoryRowFrameFile, "inventoryRowFrame — visPipInvItem.ramka (897)"),
            (ScrollBarTrackFile,    "scrollBarTrack — UIScrollBar (492) track"),
            (ScrollBarThumbFile,    "scrollBarThumb — UIScrollBar (492) thumb"),
        };

        /// <summary>
        /// Two-frame symbols. <c>ButPage</c> has exactly TWO states — normal (1) and active (2) —
        /// driven by <c>gotoAndStop(1|2)</c> in <c>PipBuck.setButtons</c>. The "four states" figure in
        /// the first edition of the specification was wrong; the folder holds two PNGs.
        ///
        /// <para><b>Symbol 1143 (<c>ButPage</c>) is deliberately NOT here.</b> Its export cannot be
        /// used: the symbol also carries two <c>DefineEditText</c> fields that the ActionScript fills
        /// in at runtime (<c>PipBuck.as:172-173</c>), and their design-time values — the author's
        /// Russian placeholders <c>Инвентарь</c> and <c>1</c>, SWF characters 1140 and 1141 — are
        /// composited into FFDec's flat PNG. Copying it put "Инвентарь1" on all six tabs. The button
        /// is rendered from the Oracle's own shapes instead; see
        /// <see cref="SynthesisedTabButtons"/>.</para>
        /// </summary>
        public static readonly TwoStateSpec[] TwoStateSprites =
        {
            new(1019, "PipBuck_SubCatButton_1.png",  "PipBuck_SubCatButton_2.png",  "butCatFon — sub-category tab (normal / active)"),
        };

        /// <summary>
        /// The PipBuck tab button's two states, <b>rendered from the Oracle's shapes</b> rather than
        /// copied out of FFDec's sprite export.
        ///
        /// <para>The source is <c>pfe/shapes/1139.svg</c> (frame 1) and <c>pfe/shapes/1142.svg</c>
        /// (frame 2) — the two <c>DefineShape</c> records the <c>ButPage</c> symbol places at depths 1
        /// of its two frames. Neither contains text, which is exactly the point: the symbol's text
        /// fields are separate characters that the export merges in. The parameters and the
        /// rasterisation live in <see cref="PipBuckTabArt"/>, which was diffed against FFDec's own
        /// export to confirm it reproduces the art rather than approximating it.</para>
        /// </summary>
        public static readonly (string FileName, bool Active)[] SynthesisedTabButtons =
        {
            ("PipBuck_TabButton_1.png", false),
            ("PipBuck_TabButton_2.png", true),
        };

        /// <summary>
        /// Frame sequences. <b>Frame counts are the on-disk counts, not the specification's</b>:
        /// <c>trol</c> is 39 frames (the spec did not state one) and only its first 15 are used, because
        /// <c>PipBuck.kolRItems = 15</c> and the clip is stepped with <c>gotoAndStop(i+1)</c>.
        ///
        /// <para><b><c>vskill</c> appears twice, deliberately.</b> It is one symbol holding two
        /// unrelated id spaces — frames 1..16 are the skills, 17..95 the perks — so it is split into two
        /// catalogs, each indexed from zero, with <see cref="SequenceSpec.SkipFrames"/> doing the split.
        /// The clip is 95 frames; the Oracle's own export of it has only the first 16, because the 79
        /// perk frames are art the Steam build added later. Those frames were copied into the extraction
        /// tree from the Steam <c>pfe.swf</c> on 2026-10-12, after verifying that frames 1..16 are
        /// <b>byte-identical</b> between the two builds (so the skills are the same art, not a
        /// replacement) and that all 95 render on one uniform 254x200 canvas.</para>
        ///
        /// <para><b>The dialogue portrait is <c>portret</c> (4638), not <c>visNPCIco</c> (4844).</b> The
        /// <c>Icons/Portraits</c> row was described as the "dialogue speaker portraits" but points at
        /// <c>visNPCIco</c>, which is the speech-bubble icon <c>UnitNPC</c> hangs above an NPC's head
        /// (<c>UnitNPC.as:157</c> — <c>this.ico = new visNPCIco()</c>); its two labels are <c>dial1</c>
        /// and <c>dial2</c>. The clip the Oracle actually drives with the localisation table's
        /// <c>@p</c> is <c>dial.portret</c> — sprite <b>4638</b>, 70 frames, 60 of them labelled
        /// (<c>lp1</c>…<c>men2</c>), and every one of the 41 distinct <c>@p</c> values in
        /// <c>text_en.xml</c> names one of those labels. The two catalogs are kept apart rather than
        /// merged: <c>visNPCIco</c> is still the NPC's overhead icon, and a future <c>UnitNPC</c> port
        /// needs it. The 70 frames land in a folder of their own so the two id spaces cannot be confused
        /// — which is the same reasoning <see cref="SequenceSpec.SkipFrames"/> exists for.</para>
        /// </summary>
        public static readonly SequenceSpec[] Sequences =
        {
            new(1112, "Icons/Items",      47,  0, "itemIco — item category icons (positional id contract)"),
            new(4453, "Icons/Effects",    56,  0, "_effectIco_174 — status effect icons"),
            new(4844, "Icons/Portraits",  31,  0, "visNPCIco — the NPC overhead dialogue indicator, NOT the dialogue portrait"),
            new(4638, "Icons/DialoguePortraits", 70, 0, "portret — the dialogue speaker portraits (60 named frame labels)"),
            new(876,  "Icons/Monitor",    15, 15, "trol — right-side monitor row icons (first 15 of 39)"),
            new(383,  "Icons/Skills",     16, 16, "vskill frames 1..16 — the 16 skill icons", skipFrames: 0),
            new(383,  "Icons/Perks",      79,  0, "vskill frames 17..95 — the 79 perk icons", skipFrames: 16),
            new(4166, "Interface/ReloadBar", 12, 0, "reloadBar — weapon reload / charge indicator"),
            new(4172, "Interface/PetBar",    20, 0, "hp_bar4 — pet / companion health bar"),
            new(997,  "Interface/InfoMarkers",     4,  0, "mainQuest — information page row marker (mq)"),
            new(803,  "Interface/InfoLandSelection", 2, 0, "zad — travel node selection frame"),
            new(829,  "Interface/InfoLandIcons",  26,  0, "sim — travel map per-land icons"),
            new(832,  "Interface/InfoSign",       40,  0, "visualSign1 — travel node 'new land' marker"),
        };

        /// <summary>
        /// Cursors, copied from <c>pfe/images/</c>. The expected dimensions are carried so
        /// <see cref="Verify"/> can catch a cursor imported from the wrong symbol.
        ///
        /// <para>The <b>hotspot is not here</b> — it lives in <c>PFE.UI.Common.CursorManager</c>, which
        /// passes it to <c>Cursor.SetCursor</c> at runtime. Keeping it in one place matters because the
        /// arrow's tip is its top-left corner <c>(0, 0)</c> while the 27×27 reticles are centred at
        /// <c>(13, 13)</c>; the specification originally gave <c>(16, 16)</c>, which is the centre of a
        /// 32-pixel texture and would put the crosshair's aim point 3 px off in both axes.</para>
        /// </summary>
        public sealed class CursorSpec
        {
            public string SourceFileName;
            public string DestFileName;
            public int ExpectedWidth;
            public int ExpectedHeight;
            public string Description;

            public CursorSpec(string sourceFileName, string destFileName, int expectedWidth, int expectedHeight, string description)
            {
                SourceFileName = sourceFileName;
                DestFileName = destFileName;
                ExpectedWidth = expectedWidth;
                ExpectedHeight = expectedHeight;
                Description = description;
            }
        }

        public static readonly CursorSpec[] Cursors =
        {
            new("349_visCurArrow.png",   "Cursor_Arrow.png",     32, 32, "UI pointer"),
            new("348_visCurTarget.png",  "Cursor_Aim.png",       27, 27, "real-time combat crosshairs"),
            new("347_visCurTarget1.png", "Cursor_Precision.png", 27, 27, "precision / sniper reticle"),
            new("346_visCurTarget2.png", "Cursor_Magic.png",     27, 27, "magic targeting reticle"),
        };

        // ── UI sounds: deliberately NOT part of this pipeline ──────────────────
        //
        // pip1/pip2/pip3/button are declared in Snd.as:288,302-304, and the existing SoundImporter has
        // already written them to Data/Resources/Sounds/sound/ and registered them in
        // SoundCatalog.asset — verified on disk 2026-10-11, all four ids present with a real clip.
        // Copying them a second time would put two AudioClips in the build for one sound and give the
        // interface a private copy that can drift from the one every other system plays. The interface
        // asks for them by id through PFE.UI.Common.UiSoundService instead.

        public const string FontSourceFileName = "1_Lucida Console.ttf";
        public const string FontDestFileName = "LucidaConsole.ttf";

        // ── Result ────────────────────────────────────────────────────────────

        public sealed class ImportResult
        {
            public int SpritesCopied;
            public int CursorsCopied;
            public bool FontCopied;
            public readonly List<string> Log = new();
            public readonly List<string> Warnings = new();
            public readonly List<string> Errors = new();

            /// <summary>Per-symbol counts actually found on disk, so the caller can assert them.</summary>
            public readonly Dictionary<int, int> FramesPerSymbol = new();

            public bool Succeeded => Errors.Count == 0;

            public void Info(string message) => Log.Add(message);
            public void Warn(string message) => Warnings.Add(message);
            public void Error(string message) => Errors.Add(message);
        }

        // ── Entry points ──────────────────────────────────────────────────────

        /// <summary>
        /// Copy every interface asset and configure its importer. Returns a result the caller must
        /// report; nothing here logs to the console itself, so a batch run produces one summary rather
        /// than three hundred lines of console spam.
        /// </summary>
        public static ImportResult Import()
        {
            var result = new ImportResult();

            string root = SourceImportPaths.SourceProjectRoot;
            if (string.IsNullOrWhiteSpace(root))
            {
                result.Error(SourceImportPaths.MissingSourceMessage(string.Empty, "AS3 source root"));
                return result;
            }

            string spritesRoot = FindSpritesRoot(root, result);

            AssetDatabase.StartAssetEditing();
            try
            {
                if (spritesRoot != null)
                {
                    CopySingleSprites(spritesRoot, result);
                    CopyTwoStateSprites(spritesRoot, result);
                    CopySequences(spritesRoot, result);
                }

                WriteSynthesisedTabButtons(result);

                CopyCursors(root, result);
                CopyFont(root, result);
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            AssetDatabase.Refresh();

            // Import settings must be applied AFTER the files exist on disk and the database has seen
            // them; an AssetImporter.GetAtPath on a file the database has not imported yet returns null.
            ConfigureSprites(result);
            ConfigureCursors(result);

            result.Info($"Imported {result.SpritesCopied} sprite PNG(s), {result.CursorsCopied} cursor(s), " +
                        $"font {(result.FontCopied ? "copied" : "unchanged")}.");
            return result;
        }

        /// <summary>
        /// Build (or refresh) <see cref="PipBuckCompositionDefinition"/> from what is on disk now.
        /// Safe to call on its own after an <see cref="Import"/> — every reference is resolved by path.
        /// </summary>
        public static PipBuckCompositionDefinition GenerateComposition(out List<string> problems)
        {
            // A local list, not the `out` parameter itself: the two local functions below capture it, and
            // C# forbids capturing an out/ref parameter in a lambda or local function (CS1628). The
            // parameter is assigned once, at the end.
            var found = new List<string>();

            EnsureFolder("Assets/_PFE/Data/Resources");
            EnsureFolder("Assets/_PFE/Data/Resources/UI");

            var def = AssetDatabase.LoadAssetAtPath<PipBuckCompositionDefinition>(CompositionPath);
            if (def == null)
            {
                def = ScriptableObject.CreateInstance<PipBuckCompositionDefinition>();
                AssetDatabase.CreateAsset(def, CompositionPath);
            }

            Sprite Load(string assetPath, string what)
            {
                var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
                if (sprite == null) found.Add($"{what}: no sprite at {assetPath}");
                return sprite;
            }

            Sprite[] LoadSequence(string folder, int expected, string what)
            {
                var sprites = LoadSequenceSprites(folder);
                if (sprites.Length != expected)
                    found.Add($"{what}: expected {expected} frames in {folder}, found {sprites.Length}");
                return sprites;
            }

            def.bezel = Load($"{InterfaceRoot}/PipBuck_Bezel.png", "bezel");
            def.crtScanlines = Load($"{InterfaceRoot}/PipBuck_CRTScanlines.png", "crtScanlines");
            def.tabButtonNormal = Load($"{InterfaceRoot}/PipBuck_TabButton_1.png", "tabButtonNormal");
            def.tabButtonActive = Load($"{InterfaceRoot}/PipBuck_TabButton_2.png", "tabButtonActive");
            def.subCategoryButton = Load($"{InterfaceRoot}/PipBuck_SubCatButton_1.png", "subCategoryButton");
            def.monitorRowTemplate = Load($"{InterfaceRoot}/PipBuck_MonitorRow.png", "monitorRowTemplate");
            def.hudMasterLayout = Load($"{InterfaceRoot}/HUD_MasterLayout.png", "hudMasterLayout");
            def.healthBar = Load($"{InterfaceRoot}/HUD_HPBar.png", "healthBar");
            def.bossBar = Load($"{InterfaceRoot}/HUD_BossBar.png", "bossBar");
            def.satsRadius = Load($"{InterfaceRoot}/SATS_Reticle.png", "satsRadius");
            def.satsCell = Load($"{InterfaceRoot}/SATS_Cell.png", "satsCell");

            // Inventory-page chrome. These four PNGs are HAND-AUTHORED, not exported: they are loaded
            // here so a re-import of this composition does not null them out, but the Import() half of
            // this class never writes them and must not start. The reasons are per-asset and specific:
            //   * `back`  (symbol 498) and `ramka` (symbol 897) are DefineSpriteTag frameCount=1 with NO
            //     pre-rendered export folder and no shapes/ or images/ entries — FFDec emitted nothing
            //     to copy, so a copy step would delete the only copy that exists.
            //   * `UIScrollBar` (symbol 492) DOES export, but as whole-component 300x168 composites of
            //     track+thumb+arrows. The game needs a 17x149 track and a 17x54 thumb as separate,
            //     9-sliced sprites, so the exported frames are the wrong shape and the wrong count.
            // The sizes are the on-disk truth (560x25, 559x26, 17x149, 17x54); the GUIDs in
            // PipBuckComposition.asset already point at exactly these four files.
            def.inventoryRowBack  = Load($"{InterfaceRoot}/{InventoryRowBackFile}",  "inventoryRowBack");
            def.inventoryRowFrame = Load($"{InterfaceRoot}/{InventoryRowFrameFile}", "inventoryRowFrame");
            def.scrollBarTrack    = Load($"{InterfaceRoot}/{ScrollBarTrackFile}",    "scrollBarTrack");
            def.scrollBarThumb    = Load($"{InterfaceRoot}/{ScrollBarThumbFile}",    "scrollBarThumb");

            // Information page (page 3). The four sequences land under Interface/ next to the two the
            // HUD already owns; the frame counts are the symbols' own and are re-checked in
            // PipBuckCompositionDefinition.Validate.
            def.infoMapWindow      = Load($"{InterfaceRoot}/PipBuck_InfoMap.png",      "infoMapWindow");
            def.infoWorldWindow    = Load($"{InterfaceRoot}/PipBuck_InfoWorldMap.png", "infoWorldWindow");
            def.infoLandNodeBack   = Load($"{InterfaceRoot}/PipBuck_InfoLandNode.png", "infoLandNodeBack");
            def.infoConfirmButton  = Load($"{InterfaceRoot}/PipBuck_InfoConfirm.png",  "infoConfirmButton");
            def.infoMarkerFrames        = LoadSequence($"{InterfaceRoot}/InfoMarkers",       PipBuckCompositionDefinition.InfoMarkerFrameCount,        "infoMarkerFrames");
            def.infoLandSelectionFrames = LoadSequence($"{InterfaceRoot}/InfoLandSelection", PipBuckCompositionDefinition.InfoLandSelectionFrameCount, "infoLandSelectionFrames");
            def.infoLandIcons           = LoadSequence($"{InterfaceRoot}/InfoLandIcons",     PipBuckCompositionDefinition.InfoLandIconCount,           "infoLandIcons");
            def.infoSignFrames          = LoadSequence($"{InterfaceRoot}/InfoSign",          PipBuckCompositionDefinition.InfoSignFrameCount,          "infoSignFrames");

            def.monitorIcons = LoadSequence($"{IconsRoot}/Monitor", PipBuckCompositionDefinition.MonitorIconCount, "monitorIcons");
            def.itemCategoryIcons = LoadSequence($"{IconsRoot}/Items", PipBuckCompositionDefinition.ItemIconCount, "itemCategoryIcons");
            def.statusEffectIcons = LoadSequence($"{IconsRoot}/Effects", PipBuckCompositionDefinition.StatusEffectIconCount, "statusEffectIcons");
            def.npcPortraits = LoadSequence($"{IconsRoot}/Portraits", PipBuckCompositionDefinition.NpcPortraitCount, "npcPortraits");
            def.dialoguePortraits = LoadSequence($"{IconsRoot}/DialoguePortraits", PipBuckCompositionDefinition.DialoguePortraitCount, "dialoguePortraits");
            def.skillIcons = LoadSequence($"{IconsRoot}/Skills", PipBuckCompositionDefinition.SkillIconCount, "skillIcons");
            def.perkIcons = LoadSequence($"{IconsRoot}/Perks", PipBuckCompositionDefinition.PerkIconCount, "perkIcons");
            def.reloadBarFrames = LoadSequence($"{InterfaceRoot}/ReloadBar", PipBuckCompositionDefinition.ReloadBarFrameCount, "reloadBarFrames");
            def.petBarFrames = LoadSequence($"{InterfaceRoot}/PetBar", PipBuckCompositionDefinition.PetBarFrameCount, "petBarFrames");

            def.cursorArrow = LoadTexture($"{CursorsRoot}/Cursor_Arrow.png", "cursorArrow", found);
            def.cursorAim = LoadTexture($"{CursorsRoot}/Cursor_Aim.png", "cursorAim", found);
            def.cursorPrecision = LoadTexture($"{CursorsRoot}/Cursor_Precision.png", "cursorPrecision", found);
            def.cursorMagic = LoadTexture($"{CursorsRoot}/Cursor_Magic.png", "cursorMagic", found);

            def.terminalFont = AssetDatabase.LoadAssetAtPath<Font>($"{FontsRoot}/{FontDestFileName}");
            if (def.terminalFont == null) found.Add($"terminalFont: no font at {FontsRoot}/{FontDestFileName}");

            EditorUtility.SetDirty(def);
            AssetDatabase.SaveAssets();

            problems = found;
            return def;
        }

        private static Texture2D LoadTexture(string assetPath, string what, List<string> problems)
        {
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
            if (texture == null) problems.Add($"{what}: no texture at {assetPath}");
            return texture;
        }

        /// <summary>
        /// Checks that every expected output exists with the expected frame count. Read-only.
        /// This is the guard the main-menu importer lacked: it verified that a <c>sprites/</c> folder
        /// existed, not that the right sprites were in it, and imported nothing while reporting success.
        /// </summary>
        public static bool Verify(out string report)
        {
            var problems = new List<string>();

            foreach (var spec in SingleSprites)
                if (!File.Exists(FullPath($"{InterfaceRoot}/{spec.DestFileName}")))
                    problems.Add($"missing single sprite: {InterfaceRoot}/{spec.DestFileName} ({spec.Description})");

            // Hand-authored chrome: nothing in this class writes these, so a re-import that does not
            // touch them is the only acceptable outcome. Checking them here is what turns "the
            // composition fields silently went null" into a red report.
            foreach ((string fileName, string what) in HandAuthoredSprites)
                if (!File.Exists(FullPath($"{InterfaceRoot}/{fileName}")))
                    problems.Add($"missing hand-authored sprite: {InterfaceRoot}/{fileName} ({what})");

            foreach (var spec in TwoStateSprites)
            {
                if (!File.Exists(FullPath($"{InterfaceRoot}/{spec.State1FileName}")))
                    problems.Add($"missing state 1: {InterfaceRoot}/{spec.State1FileName} ({spec.Description})");
                if (!File.Exists(FullPath($"{InterfaceRoot}/{spec.State2FileName}")))
                    problems.Add($"missing state 2: {InterfaceRoot}/{spec.State2FileName} ({spec.Description})");
            }

            // The rendered tab buttons are checked by CONTENT, not by existence. This is the one
            // asset in the pipeline whose entire point is the text it does NOT carry, so "a file is
            // there" answers the wrong question — FFDec's export is a file that is there, and it is
            // the thing this step exists to keep out.
            foreach ((string fileName, bool active) in SynthesisedTabButtons)
            {
                string path = $"{InterfaceRoot}/{fileName}";
                string full = FullPath(path);

                if (!File.Exists(full))
                {
                    problems.Add($"missing rendered sprite: {path} (PipBuck tab button)");
                    continue;
                }

                if (!MatchesRenderedArt(full, active, out int worstDelta))
                {
                    problems.Add($"rendered sprite {fileName} does not match PipBuckTabArt " +
                                 $"(worst channel delta {worstDelta}) — it is probably FFDec's " +
                                 "export, which has \"Инвентарь1\" baked into it. Re-run the import.");
                }
            }

            // The flat backdrop gets the same treatment for the same reason: the defect it can carry
            // is a design-time string FFDec rasterised in, and "the file is there" cannot tell the
            // cleaned export from the original.
            CheckBackdropHasNoBakedTabCaptions(problems);

            foreach (var spec in Sequences)
            {
                int found = CountPngs(FullPath(SequenceDestFolder(spec)));
                if (found != spec.ExpectedFrames)
                    problems.Add($"{spec.Description}: expected {spec.ExpectedFrames} frames, found {found}");
            }

            foreach (var spec in Cursors)
            {
                string cursorPath = $"{CursorsRoot}/{spec.DestFileName}";
                if (!File.Exists(FullPath(cursorPath)))
                {
                    problems.Add($"missing cursor: {cursorPath} ({spec.Description})");
                    continue;
                }

                // Dimensions are checked, not just existence: the hotspot is derived from them, and a
                // cursor copied from the wrong symbol still exists and still loads — it would simply aim
                // from the wrong pixel. The specification quoted these dimensions wrong twice.
                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(cursorPath);
                if (texture == null)
                {
                    problems.Add($"cursor not imported as a texture: {cursorPath}");
                }
                else if (texture.width != spec.ExpectedWidth || texture.height != spec.ExpectedHeight)
                {
                    problems.Add($"cursor {spec.Description}: expected {spec.ExpectedWidth}x{spec.ExpectedHeight}, " +
                                 $"found {texture.width}x{texture.height} in {cursorPath}");
                }
            }

            if (!File.Exists(FullPath($"{FontsRoot}/{FontDestFileName}")))
                problems.Add($"missing font: {FontsRoot}/{FontDestFileName}");

            if (!File.Exists(FullPath(CompositionPath)))
                problems.Add($"missing composition asset: {CompositionPath}");

            if (problems.Count == 0)
            {
                report = "Interface assets: all present.";
                return true;
            }

            report = $"Interface assets: {problems.Count} problem(s):\n  " + string.Join("\n  ", problems);
            return false;
        }

        // ── Copy steps ────────────────────────────────────────────────────────

        static void CopySingleSprites(string spritesRoot, ImportResult result)
        {
            EnsureFolder(InterfaceRoot);

            foreach (var spec in SingleSprites)
            {
                string sourceDir = FindSymbolFolder(spritesRoot, spec.SymbolId);
                if (sourceDir == null)
                {
                    result.Error($"symbol {spec.SymbolId} ({spec.Description}): no DefineSprite folder found");
                    continue;
                }

                string source = Path.Combine(sourceDir, "1.png");
                if (!File.Exists(source))
                {
                    result.Error($"symbol {spec.SymbolId} ({spec.Description}): {sourceDir} has no 1.png");
                    continue;
                }

                if (CopyIfNewer(source, $"{InterfaceRoot}/{spec.DestFileName}", result))
                    result.SpritesCopied++;
                result.FramesPerSymbol[spec.SymbolId] = 1;
                result.Info($"  {spec.SymbolId} {spec.Description} -> {spec.DestFileName}");
            }
        }

        static void CopyTwoStateSprites(string spritesRoot, ImportResult result)
        {
            EnsureFolder(InterfaceRoot);

            foreach (var spec in TwoStateSprites)
            {
                string sourceDir = FindSymbolFolder(spritesRoot, spec.SymbolId);
                if (sourceDir == null)
                {
                    result.Error($"symbol {spec.SymbolId} ({spec.Description}): no DefineSprite folder found");
                    continue;
                }

                string first = Path.Combine(sourceDir, "1.png");
                string second = Path.Combine(sourceDir, "2.png");

                if (!File.Exists(first) || !File.Exists(second))
                {
                    result.Error($"symbol {spec.SymbolId} ({spec.Description}): expected 1.png and 2.png in {sourceDir}");
                    continue;
                }

                if (CopyIfNewer(first, $"{InterfaceRoot}/{spec.State1FileName}", result))
                    result.SpritesCopied++;
                if (CopyIfNewer(second, $"{InterfaceRoot}/{spec.State2FileName}", result))
                    result.SpritesCopied++;
                result.FramesPerSymbol[spec.SymbolId] = 2;
                result.Info($"  {spec.SymbolId} {spec.Description} -> {spec.State1FileName} + {spec.State2FileName}");
            }
        }

        static void CopySequences(string spritesRoot, ImportResult result)
        {
            foreach (var spec in Sequences)
            {
                string sourceDir = FindSymbolFolder(spritesRoot, spec.SymbolId);
                if (sourceDir == null)
                {
                    result.Error($"symbol {spec.SymbolId} ({spec.Description}): no DefineSprite folder found");
                    continue;
                }

                var frames = Directory.GetFiles(sourceDir, "*.png")
                    .Select(path => (path, number: FrameNumber(path)))
                    .Where(t => t.number > 0)
                    .OrderBy(t => t.number)
                    .ToList();

                result.FramesPerSymbol[spec.SymbolId] = frames.Count;

                if (frames.Count == 0)
                {
                    result.Error($"symbol {spec.SymbolId} ({spec.Description}): no numbered PNGs in {sourceDir}");
                    continue;
                }

                int available = Math.Max(0, frames.Count - spec.SkipFrames);
                int take = spec.TakeFrames > 0 ? Math.Min(spec.TakeFrames, available) : available;

                if (take < spec.ExpectedFrames)
                    result.Error($"symbol {spec.SymbolId} ({spec.Description}): need {spec.ExpectedFrames} " +
                                 $"frames starting at frame {spec.SkipFrames + 1} but the clip has only " +
                                 $"{frames.Count} — the composition would be short");

                string destFolder = SequenceDestFolder(spec);
                EnsureFolder(destFolder);

                for (int i = 0; i < take; i++)
                {
                    if (CopyIfNewer(frames[spec.SkipFrames + i].path, $"{destFolder}/f{i + 1:D3}.png", result))
                        result.SpritesCopied++;
                }

                result.Info($"  {spec.SymbolId} {spec.Description}: {take} of {frames.Count} frame(s) " +
                            $"from frame {spec.SkipFrames + 1} -> {destFolder}");
            }
        }

        /// <summary>
        /// Full asset path for a sequence's output folder. <see cref="SequenceSpec.DestFolder"/> is
        /// relative to <see cref="ArtUiRoot"/> and already carries its group segment
        /// (<c>Icons/Items</c>, <c>Interface/ReloadBar</c>), so it is joined once — an earlier version
        /// prefixed the group again and wrote the item icons to <c>Art/UI/Icons/Icons/Items</c>.
        /// </summary>
        static string SequenceDestFolder(SequenceSpec spec) => $"{ArtUiRoot}/{spec.DestFolder}";


        /// <summary>
        /// Writes the two tab-button PNGs from <see cref="PipBuckTabArt"/>.
        ///
        /// <para>Idempotent the same way the copies are, but by <b>content</b> rather than by
        /// timestamp: a timestamp comparison would rewrite the file on every run (the destination is
        /// newer than any source), and rewriting it re-triggers Unity's texture import for a file
        /// that has not changed.</para>
        /// </summary>
        static void WriteSynthesisedTabButtons(ImportResult result)
        {
            EnsureFolder(InterfaceRoot);

            foreach ((string fileName, bool active) in SynthesisedTabButtons)
            {
                string destAssetPath = $"{InterfaceRoot}/{fileName}";
                string destFull = FullPath(destAssetPath);

                Texture2D texture = PipBuckTabArt.CreateTexture(active);
                byte[] png;
                try
                {
                    png = texture.EncodeToPNG();
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(texture);
                }

                if (File.Exists(destFull) && ByteArraysEqual(File.ReadAllBytes(destFull), png))
                {
                    result.Info($"  {fileName} — rendered art unchanged");
                    continue;
                }

                try
                {
                    File.WriteAllBytes(destFull, png);
                    result.SpritesCopied++;
                    result.Info($"  {fileName} — rendered from the Oracle's shape " +
                                $"({(active ? "active" : "normal")}, " +
                                $"{PipBuckTabArt.Width}x{PipBuckTabArt.Height})");
                }
                catch (IOException e)
                {
                    result.Error($"write failed: {destFull}: {e.Message}");
                }
            }
        }

        static bool ByteArraysEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        /// <summary>
        /// Fails when the flat backdrop still carries FFDec's baked tab captions.
        ///
        /// <para>FFDec's sprite export composites a symbol's whole display list, and <c>ButPage</c>
        /// (1143) contains two <c>DefineEditText</c> fields whose design-time values are the author's
        /// Russian placeholders. ActionScript overwrites them at runtime (<c>PipBuck.as:172-173</c>),
        /// so they exist only in a flattened export — and the port redraws all six tabs itself, so
        /// they are never wanted. The pipeline removes the six <c>ButPage</c> placements from sprite
        /// 1155 frame 1 before exporting
        /// (<c>.workbuddy-ai/tools/strip_pipbuck_tab_placements.py</c>); this is the check that the
        /// export actually came from that SWF.</para>
        ///
        /// <para>The test is the absence of the button's own stroke colour (<c>#00ff99</c>) inside the
        /// six footprints. It is the right probe because <c>#00ff99</c> is also the caption's colour,
        /// and the inset keeps the button's 1 px / 3 px border out of the count — so a footprint
        /// interior can only be stroke-coloured if a caption is drawn in it. Watched failing against
        /// the uncleaned export: 159/159/159/159/159/153 pixels per tab.</para>
        /// </summary>
        static void CheckBackdropHasNoBakedTabCaptions(List<string> problems)
        {
            string path = $"{InterfaceRoot}/PipBuck_Bezel.png";
            string full = FullPath(path);
            if (!File.Exists(full)) return;   // reported already, as a missing single sprite

            var loaded = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (!loaded.LoadImage(File.ReadAllBytes(full)))
                {
                    problems.Add($"backdrop {path} is not a readable image");
                    return;
                }

                int width = loaded.width;
                int height = loaded.height;
                Color32[] pixels = loaded.GetPixels32();
                Color32 stroke = PipBuckTabArt.StrokeColor;

                foreach (PipElement tab in PipBuckLayout.TabSlots)
                {
                    Rect interior = PipBuckLayout.BackdropFootprintInterior(tab);

                    int x0 = Mathf.Clamp(Mathf.RoundToInt(interior.xMin), 0, width);
                    int y0 = Mathf.Clamp(Mathf.RoundToInt(interior.yMin), 0, height);
                    int x1 = Mathf.Clamp(Mathf.RoundToInt(interior.xMax), 0, width);
                    int y1 = Mathf.Clamp(Mathf.RoundToInt(interior.yMax), 0, height);

                    int found = 0;
                    for (int y = y0; y < y1; y++)
                    {
                        // A texture's rows run bottom-up; the footprint's y runs down from the top.
                        int row = (height - 1 - y) * width;
                        for (int x = x0; x < x1; x++)
                        {
                            Color32 px = pixels[row + x];
                            if (px.r == stroke.r && px.g == stroke.g && px.b == stroke.b && px.a > 200)
                            {
                                found++;
                            }
                        }
                    }

                    if (found > 0)
                    {
                        problems.Add($"backdrop {path}: {found} stroke-coloured pixel(s) inside the " +
                                     $"{tab} footprint ({interior.xMin},{interior.yMin})-({interior.xMax},{interior.yMax}) " +
                                     "— this is FFDec's flat export, which has \"Инвентарь1\" baked " +
                                     "into it. Re-export sprite 1155 from a SWF with the six ButPage " +
                                     "placements removed (strip_pipbuck_tab_placements.py).");
                    }
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(loaded);
            }
        }

        /// <summary>
        /// Whether a file on disk is the art <see cref="PipBuckTabArt"/> renders, compared as
        /// <b>pixels</b> rather than bytes so that a different PNG encoder is not reported as a
        /// wrong image.
        /// </summary>
        static bool MatchesRenderedArt(string fullPath, bool active, out int worstDelta)
        {
            worstDelta = 255;

            var loaded = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (!loaded.LoadImage(File.ReadAllBytes(fullPath))) return false;
                if (loaded.width != PipBuckTabArt.Width || loaded.height != PipBuckTabArt.Height)
                {
                    return false;
                }

                Color32[] actual = loaded.GetPixels32();
                worstDelta = 0;

                for (int y = 0; y < PipBuckTabArt.Height; y++)
                {
                    // A texture's rows run bottom-up; the model's y runs down from the top.
                    int row = (PipBuckTabArt.Height - 1 - y) * PipBuckTabArt.Width;

                    for (int x = 0; x < PipBuckTabArt.Width; x++)
                    {
                        Color32 want = PipBuckTabArt.Sample(x, y, active);
                        Color32 have = actual[row + x];

                        worstDelta = Math.Max(worstDelta, Math.Abs(want.r - have.r));
                        worstDelta = Math.Max(worstDelta, Math.Abs(want.g - have.g));
                        worstDelta = Math.Max(worstDelta, Math.Abs(want.b - have.b));
                        worstDelta = Math.Max(worstDelta, Math.Abs(want.a - have.a));
                    }
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(loaded);
            }

            // A round trip through PNG is lossless, so the only slack needed is for an encoder
            // that rounds rather than truncates.
            return worstDelta <= 2;
        }

        static void CopyCursors(string root, ImportResult result)
        {
            EnsureFolder(CursorsRoot);
            string sourceRoot = Combine(root, "pfe", "images");

            foreach (var spec in Cursors)
            {
                string source = Path.Combine(sourceRoot, spec.SourceFileName);
                if (!File.Exists(source))
                {
                    result.Error($"cursor {spec.Description}: {source} not found");
                    continue;
                }

                if (CopyIfNewer(source, $"{CursorsRoot}/{spec.DestFileName}", result))
                    result.CursorsCopied++;
            }
        }

        static void CopyFont(string root, ImportResult result)
        {
            EnsureFolder(FontsRoot);
            string source = Combine(root, "pfe", "fonts", FontSourceFileName);

            if (!File.Exists(source))
            {
                result.Error($"font: {source} not found — the interface will fall back to an OS monospace font");
                return;
            }

            result.FontCopied = CopyIfNewer(source, $"{FontsRoot}/{FontDestFileName}", result);
        }

        // ── Import settings ───────────────────────────────────────────────────

        static void ConfigureSprites(ImportResult result)
        {
            var paths = new List<string>();

            foreach (var spec in SingleSprites) paths.Add($"{InterfaceRoot}/{spec.DestFileName}");
            foreach (var spec in TwoStateSprites)
            {
                paths.Add($"{InterfaceRoot}/{spec.State1FileName}");
                paths.Add($"{InterfaceRoot}/{spec.State2FileName}");
            }

            // The rendered tab buttons are not copied, but they still need sprite import settings.
            foreach ((string fileName, _) in SynthesisedTabButtons)
            {
                paths.Add($"{InterfaceRoot}/{fileName}");
            }

            foreach (var spec in Sequences)
                paths.AddRange(AssetPathsIn(SequenceDestFolder(spec)));

            int configured = 0;
            foreach (string path in paths)
            {
                if (ConfigureSpriteImporter(path)) configured++;
            }

            result.Info($"Configured {configured} sprite importer(s).");
        }

        // Sprite packing: NOT done here. Unity 6 removed TextureImporter.spritePackingTag
        // ("Support for packing sprites through spritePackingTag has been removed. Please use
        // SpriteAtlas instead."), so setting it is a silent no-op that still reads as if the icons were
        // being batched. Grouping the icon catalogs into SpriteAtlas assets is a real follow-up — worth
        // doing, because 47 + 56 + 31 + 15 small sprites each draw separately today — but it needs atlas
        // assets, not an importer flag, and it is not a correctness requirement.

        static bool ConfigureSpriteImporter(string assetPath)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null) return false;

            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.spritePixelsPerUnit = PixelsPerUnit;
            importer.filterMode = FilterMode.Bilinear;
            importer.mipmapEnabled = false;
            importer.alphaIsTransparency = true;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.textureCompression = TextureImporterCompression.CompressedHQ;

            var settings = new TextureImporterSettings();
            importer.ReadTextureSettings(settings);
            settings.spriteMode = (int)SpriteImportMode.Single;
            settings.spriteAlignment = (int)SpriteAlignment.Center;
            importer.SetTextureSettings(settings);

            importer.SaveAndReimport();
            return true;
        }

        static void ConfigureCursors(ImportResult result)
        {
            int configured = 0;

            foreach (var spec in Cursors)
            {
                string assetPath = $"{CursorsRoot}/{spec.DestFileName}";
                var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
                if (importer == null)
                {
                    result.Warn($"cursor {spec.DestFileName}: no TextureImporter — hotspot not set");
                    continue;
                }

                // The hotspot is NOT set here. `cursorHotspot` exists in UnityEditor.CoreModule but on no
                // type reachable from this assembly — it is not on TextureImporter and not on
                // TextureImporterSettings, both confirmed by the compiler. That costs nothing, because
                // the hotspot is a parameter of Cursor.SetCursor, and PFE.UI.Common.CursorManager passes
                // the right one per mode at runtime. The importer's job here is only to make the texture
                // usable as a cursor: uncompressed, point-filtered, no mipmaps.
                var settings = new TextureImporterSettings();
                importer.ReadTextureSettings(settings);
                settings.textureType = TextureImporterType.Cursor;
                importer.SetTextureSettings(settings);

                importer.filterMode = FilterMode.Point;
                importer.mipmapEnabled = false;
                importer.alphaIsTransparency = true;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.textureCompression = TextureImporterCompression.Uncompressed;

                importer.SaveAndReimport();
                configured++;
            }

            result.Info($"Configured {configured} cursor importer(s).");
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// Finds the JPEXS sprite export root. Two trees share the name <c>sprites</c> and only one has
        /// the folder naming this importer needs (<c>DefineSprite_{id}_{ClassName}</c>); the other uses
        /// <c>DefineSprite_{id}_symbol{id}</c>. Both are accepted, but the one actually used is logged,
        /// because pointing at the wrong tree imports nothing while reporting success.
        /// </summary>
        static string FindSpritesRoot(string root, ImportResult result)
        {
            var candidates = new[]
            {
                Combine(root, "pfe", "sprites"),
                Combine(root, "pfe", "scripts", "_assets", "sprites"),
            };

            foreach (string candidate in candidates)
            {
                if (!Directory.Exists(candidate)) continue;

                bool hasPipBuck = Directory.GetDirectories(candidate, "DefineSprite_1155_*").Length > 0;
                if (hasPipBuck)
                {
                    result.Info($"Sprite source: {candidate}");
                    return candidate;
                }

                result.Warn($"Sprite root {candidate} exists but has no DefineSprite_1155_* folder — skipping");
            }

            result.Error($"No usable sprite root found under {root} (looked for DefineSprite_1155_*). " +
                         "Check PFE/Data/Import Source Root….");
            return null;
        }

        static string FindSymbolFolder(string spritesRoot, int symbolId)
        {
            var dirs = Directory.GetDirectories(spritesRoot, $"DefineSprite_{symbolId}_*");
            return dirs.Length > 0 ? dirs[0] : null;
        }

        /// <summary>
        /// Copies only when the destination is absent or older. Returns true when a copy happened.
        ///
        /// <para>Deliberately does <b>not</b> touch any counter: it is used for sprites, cursors, the
        /// font and sounds, so counting here would credit every cursor to <c>SpritesCopied</c> and make
        /// the per-kind totals meaningless. Callers count their own kind.</para>
        /// </summary>
        static bool CopyIfNewer(string source, string destAssetPath, ImportResult result)
        {
            string destFull = FullPath(destAssetPath);
            string destDir = Path.GetDirectoryName(destFull);
            if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                Directory.CreateDirectory(destDir);

            if (File.Exists(destFull) &&
                File.GetLastWriteTimeUtc(source) <= File.GetLastWriteTimeUtc(destFull))
            {
                return false;
            }

            try
            {
                File.Copy(source, destFull, true);
                return true;
            }
            catch (IOException e)
            {
                result.Error($"copy failed: {source} -> {destFull}: {e.Message}");
                return false;
            }
        }

        static Sprite[] LoadSequenceSprites(string folderAssetPath)
        {
            string full = FullPath(folderAssetPath);
            if (!Directory.Exists(full)) return Array.Empty<Sprite>();

            return Directory.GetFiles(full, "f*.png")
                .Select(path => (path, number: FrameNumber(path)))
                .Where(t => t.number > 0)
                .OrderBy(t => t.number)
                .Select(t => AssetDatabase.LoadAssetAtPath<Sprite>(ToAssetPath(t.path)))
                .Where(s => s != null)
                .ToArray();
        }

        static IEnumerable<string> AssetPathsIn(string folderAssetPath)
        {
            string full = FullPath(folderAssetPath);
            if (!Directory.Exists(full)) yield break;

            foreach (string path in Directory.GetFiles(full, "*.png"))
                yield return ToAssetPath(path);
        }

        /// <summary>Frame number from a JPEXS <c>{n}.png</c> or an imported <c>f{n:D3}.png</c>.</summary>
        static int FrameNumber(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (name.StartsWith("f", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(1);

            return int.TryParse(name, out int n) ? n : 0;
        }

        static int CountPngs(string fullFolder)
        {
            return Directory.Exists(fullFolder) ? Directory.GetFiles(fullFolder, "*.png").Length : 0;
        }

        static string Combine(params string[] parts)
        {
            return Path.Combine(parts).Replace('\\', '/');
        }

        static string FullPath(string assetPath)
        {
            // assetPath is "Assets/..." relative to the project; Application.dataPath is the Assets folder.
            string relative = assetPath.StartsWith("Assets/", StringComparison.Ordinal)
                ? assetPath.Substring("Assets/".Length)
                : assetPath;
            return Path.GetFullPath(Path.Combine(Application.dataPath, relative));
        }

        static string ToAssetPath(string fullPath)
        {
            string normalized = fullPath.Replace('\\', '/');
            int index = normalized.IndexOf("/Assets/", StringComparison.OrdinalIgnoreCase);
            return index >= 0 ? normalized.Substring(index + 1) : normalized;
        }

        static void EnsureFolder(string assetFolder)
        {
            string full = FullPath(assetFolder);
            if (!Directory.Exists(full)) Directory.CreateDirectory(full);
        }
    }
}
#endif
