using System;
using System.Collections.Generic;
using System.Globalization;

namespace PFE.Systems.Particles
{
    /// <summary>
    /// Which of the two art pipelines a particle's frames come from.
    /// </summary>
    public enum ParticleSpriteKind
    {
        /// <summary>No frames — the row is a text part, or its art is missing.</summary>
        None = 0,

        /// <summary><c>vis=</c> — a MovieClip class, exported as one PNG per frame.</summary>
        Vis = 1,

        /// <summary><c>blit=</c> — a single-row sheet, sliced by <c>blitx</c>×<c>blity</c>.</summary>
        Blit = 2
    }

    /// <summary>
    /// Where a particle's frames live in the exported SWF trees, and how many there are — the whole of
    /// the art-pipeline arithmetic, with <b>no Unity type in any signature</b>.
    ///
    /// <para><b>Why this is separated from the importer.</b> The two source layouts are completely
    /// different and both are easy to get subtly wrong, and the failure is silent in the worst way: a
    /// mis-derived folder name yields <i>zero</i> frames, which renders as "this effect does nothing" —
    /// indistinguishable from a definition that emits nothing, or from a budget refusal. Keeping the
    /// derivation here lets the offline wall execute it (see <c>ParticleSpriteSourceTests</c>) instead
    /// of it living in an editor-only class that nothing offline can reach.</para>
    ///
    /// <para><b>The two layouts, both measured against the real trees on 2026-10-04:</b></para>
    /// <list type="bullet">
    /// <item><description><b><c>vis=</c></b> — the definition names a class (<c>visualBlast</c>), not a
    /// path. The class is mapped to a symbol id by <c>pfe/symbolClass/symbols.csv</c>, and the frames
    /// are one PNG per file at <c>&lt;sprites&gt;/DefineSprite_&lt;symbolId&gt;_symbol&lt;symbolId&gt;/</c>,
    /// named <c>1.png</c>, <c>2.png</c>, … <b>All 94 <c>vis=</c> rows resolve this way</b>, and every
    /// folder is internally uniform in frame size — which is why a plain centre pivot is correct here
    /// and the per-frame SWF-bounds pivot <c>CharacterSpriteImporter</c> needs is not.</description></item>
    /// <item><description><b><c>blit=</c></b> — the definition names a sheet (<c>sprExplW</c>), mapped
    /// through <b>a different</b> csv (<c>sprite1.swf/symbolClass/symbols.csv</c>) to a symbol id, and
    /// the sheet is <b>one image</b> at <c>sprite1.swf/images/&lt;symbolId&gt;_&lt;sheetId&gt;.png</c>.
    /// It is a <b>single horizontal row</b> — the image height equals the row's <c>blity</c> exactly —
    /// so the frame count is <c>imageWidth / blitx</c> with no vertical stacking.</description></item>
    /// </list>
    ///
    /// <para><b>Two traps this type exists to pin down.</b></para>
    /// <list type="number">
    /// <item><description><b>The sheet root is <c>sprite1.swf</c>, not <c>sprite.swf</c>.</b> Both exist
    /// and both have an <c>images/</c> folder of <c>&lt;id&gt;_&lt;Class&gt;.png</c>. <c>sprite.swf</c>
    /// holds <i>unit</i> sprites (<c>10_sprSlaver3.png</c>, <c>16_sprRat.png</c>) and contains <b>none</b>
    /// of the effect sheets — so a lookup against it finds nothing and reports every blit row as
    /// missing art. <c>Grafon.texUrl</c> lists four candidate roots
    /// (<c>texture.swf</c>, <c>texture1.swf</c>, <c>sprite.swf</c>, <c>sprite1.swf</c>), so picking the
    /// wrong one is the natural mistake.</description></item>
    /// <item><description><b>Several <c>blit=</c> rows share one sheet.</b> <c>expl</c>,
    /// <c>fireexpl</c> and <c>ttexpl</c> all name <c>sprExpl</c>; the four <c>sprIskr</c> rows share
    /// <c>sprIskr</c>. <see cref="FrameCountFromSheet"/> is therefore a function of the <i>sheet</i>, not
    /// of the row, and the importer must slice each sheet once rather than once per row.</description></item>
    /// </list>
    /// </summary>
    public static class ParticleSpriteSource
    {
        /// <summary>Prefix the exporter gives every sprite folder. Matches <c>CharacterSpriteImporter</c>.</summary>
        public const string DefineSpritePrefix = "DefineSprite_";

        /// <summary>Where <c>vis=</c> frames live, relative to the source root.</summary>
        public const string VisSpritesRelativePath = "pfe/scripts/_assets/sprites";

        /// <summary>The <c>vis=</c> class → symbol-id map, relative to the source root.</summary>
        public const string VisSymbolCsvRelativePath = "pfe/symbolClass/symbols.csv";

        /// <summary>
        /// The SWF folder holding the <c>blit=</c> sheets. <b>Not <c>sprite.swf</c></b> — see the type
        /// doc. <c>SourceImportPaths.TextureSpritesRoot("sprite1")</c> derives this same root.
        /// </summary>
        public const string BlitSwfFolderName = "sprite1";

        /// <summary>The <c>blit=</c> sheet id → symbol-id map, relative to the source root.</summary>
        public const string BlitSymbolCsvRelativePath = "sprite1.swf/symbolClass/symbols.csv";

        // ── Which pipeline does a row use? ───────────────────────────────────

        /// <summary>
        /// The pipeline a resolved row uses. <c>vis=</c> wins when both are present — the two are
        /// mutually exclusive in the shipped data (0 of 118 rows carry both, 0 carry neither), so this
        /// ordering is only a tie-break for hand-edited data, and it matches <c>ParticleRules.SpawnOne</c>,
        /// which tests <c>Blit</c> for emptiness only after the vis cursor has been set up.
        /// </summary>
        public static ParticleSpriteKind KindOf(ParticleDefinition definition)
        {
            if (definition == null) return ParticleSpriteKind.None;
            if (!string.IsNullOrEmpty(definition.Vis)) return ParticleSpriteKind.Vis;
            if (!string.IsNullOrEmpty(definition.Blit)) return ParticleSpriteKind.Blit;
            return ParticleSpriteKind.None;
        }

        /// <summary>
        /// The id a row's frames are filed under — the <c>vis=</c> class or the <c>blit=</c> sheet,
        /// whichever the row uses. <see cref="string.Empty"/> when the row has neither.
        /// </summary>
        public static string AssetIdOf(ParticleDefinition definition) => KindOf(definition) switch
        {
            ParticleSpriteKind.Vis  => definition.Vis,
            ParticleSpriteKind.Blit => definition.Blit,
            _                       => string.Empty
        };

        // ── vis= layout ──────────────────────────────────────────────────────

        /// <summary>
        /// The exported folder name for a <c>vis=</c> symbol. The exporter writes the symbol id twice —
        /// once as the raw id and once prefixed with <c>symbol</c> — and both halves must match or the
        /// lookup silently finds nothing.
        /// </summary>
        public static string VisFolderName(int symbolId) =>
            DefineSpritePrefix + symbolId.ToString(CultureInfo.InvariantCulture) +
            "_symbol" + symbolId.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// The 1-based frame number a frame file encodes, or <b>-1</b> when the name is not a plain frame
        /// number.
        ///
        /// <para><b>The two namings use different bases, and this method is where that is reconciled.</b>
        /// The exporter writes <c>1.png</c>, <c>2.png</c>, … — <b>1-based and unpadded</b> — while this
        /// importer's own output is <c>f000.png</c>, <c>f001.png</c>, … — <b>0-based and padded</b>. The
        /// <c>f</c> form therefore has to be shifted by one on the way in, so that <c>1.png</c> and
        /// <c>f000.png</c> both report frame 1. They are the same frame in two namings, and a parser that
        /// returned the raw digits would call them 1 and 0.</para>
        ///
        /// <para><b>Why the number matters and not just the ordering.</b> A plain string sort puts
        /// <c>10.png</c> before <c>2.png</c>, so ordering must go through this method — but the importer
        /// also uses the number to delete stale frames past the expected count, and the frame-number/array
        /// index conversion (<c>-1</c>) is the same one the rest of the particle code uses for AS3's
        /// 1-based frames.</para>
        /// </summary>
        public static int VisFrameNumber(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return -1;

            string stem = fileName;
            int dot = stem.LastIndexOf('.');
            if (dot > 0) stem = stem.Substring(0, dot);

            // The importer's own output naming: fNNN, 0-based.
            if (stem.Length > 1 && (stem[0] == 'f' || stem[0] == 'F') && IsAllDigits(stem, 1))
                return TryParseDigits(stem, 1, out int padded) ? padded + 1 : -1;

            // The exporter's naming: N, 1-based.
            return IsAllDigits(stem, 0) && TryParseDigits(stem, 0, out int plain) ? plain : -1;
        }

        // ── blit= layout ─────────────────────────────────────────────────────

        /// <summary>
        /// The exported sheet file name for a <c>blit=</c> symbol — <c>&lt;symbolId&gt;_&lt;sheetId&gt;.png</c>.
        /// The numeric prefix is the symbol id, not a frame number.
        /// </summary>
        public static string BlitSheetFileName(int symbolId, string sheetId) =>
            symbolId.ToString(CultureInfo.InvariantCulture) + "_" + sheetId + ".png";

        /// <summary>
        /// How many frames a sheet holds: <c>imageWidth / blitx</c>, integer division.
        ///
        /// <para><b>Width, not height.</b> Every one of the 24 shipped sheets is a single horizontal row
        /// whose height equals the row's <c>blity</c>, so there is nothing to stack. Returns <b>0</b> for
        /// a degenerate input rather than 1, because 0 is the value <c>ParticleRules</c> already treats as
        /// "frame count unknown" and which suppresses the random-frame pick — a wrong 1 would silently
        /// freeze every animation on frame 0 instead.</para>
        /// </summary>
        public static int FrameCountFromSheet(int imageWidth, int blitX) =>
            blitX > 0 && imageWidth >= blitX ? imageWidth / blitX : 0;

        // ── The symbol maps ──────────────────────────────────────────────────

        /// <summary>
        /// Parses a <c>symbols.csv</c> — one <c>&lt;symbolId&gt;;"&lt;className&gt;"</c> row per line —
        /// into a class-name → symbol-id map.
        ///
        /// <para>Both trees ship one and they are <b>different tables</b>: <c>pfe/symbolClass</c> maps the
        /// <c>visual*</c> MovieClip classes, <c>sprite1.swf/symbolClass</c> maps the <c>spr*</c> sheets.
        /// Feeding a <c>blit=</c> id to the vis table is the second silent-nothing failure this type
        /// guards; the caller is responsible for using the right file, and
        /// <see cref="ParseSymbolCsv"/> reports what it actually saw so a mismatch is visible.</para>
        ///
        /// <para>Rows are tolerated rather than validated: a blank line, a row with no separator, or a
        /// row whose id is not an integer is skipped. The real files carry a
        /// <c>0;"sprite1_fla.MainTimeline"</c> row and a trailing newline, and an importer that threw on
        /// either would be useless. Skipped rows are <i>not</i> silently absorbed — they come back through
        /// <paramref name="skippedRows"/>.</para>
        /// </summary>
        /// <param name="csvText">The whole file, as text.</param>
        /// <param name="skippedRows">Count of lines that did not parse. Diagnostics only.</param>
        public static Dictionary<string, int> ParseSymbolCsv(string csvText, out int skippedRows)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            skippedRows = 0;

            if (string.IsNullOrEmpty(csvText)) return map;

            foreach (string rawLine in csvText.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0) continue;

                int separator = line.IndexOf(';');
                if (separator <= 0) { skippedRows++; continue; }

                string idText = line.Substring(0, separator).Trim();
                string name = line.Substring(separator + 1).Trim().Trim('"');
                if (name.Length == 0) { skippedRows++; continue; }

                if (!int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int symbolId))
                {
                    skippedRows++;
                    continue;
                }

                // First wins. The real tables have no duplicates, but a hand-edit could add one and a
                // last-wins rule would make the resolution order depend on file order.
                if (!map.ContainsKey(name)) map[name] = symbolId;
            }

            return map;
        }

        // ── Output layout ────────────────────────────────────────────────────

        /// <summary>
        /// The importer's frame file name for a 0-based index — <c>f000.png</c>. Matches the
        /// <c>CharacterSpriteImporter</c> / <c>ProjectileGraphicsImporter</c> convention, and being
        /// zero-padded it sorts correctly as text, so the readback does not need a numeric sort.
        /// </summary>
        public static string OutputFrameFileName(int frameIndex) =>
            "f" + frameIndex.ToString("D3", CultureInfo.InvariantCulture) + ".png";

        private static bool IsAllDigits(string text, int start)
        {
            if (start >= text.Length) return false;
            for (int i = start; i < text.Length; i++)
                if (text[i] < '0' || text[i] > '9') return false;
            return true;
        }

        /// <summary>
        /// Parses the digits of <paramref name="text"/> from <paramref name="start"/>, refusing rather than
        /// throwing.
        ///
        /// <para><b><c>int.Parse</c> would be a crash in an importer.</b> <see cref="IsAllDigits"/> bounds
        /// the <i>characters</i>, not the <i>value</i>, so a file named <c>99999999999.png</c> is "all
        /// digits" and <c>int.Parse</c> throws <c>OverflowException</c> — out of a scan of an arbitrary
        /// folder, which is exactly where an unhandled exception costs the whole import.</para>
        /// </summary>
        private static bool TryParseDigits(string text, int start, out int value)
        {
            value = -1;
            return int.TryParse(text.Substring(start), NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }
    }
}
