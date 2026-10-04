using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace PFE.Systems.Particles
{
    /// <summary>
    /// Everything one pass over <c>AllData.as</c>'s <c>&lt;part&gt;</c> block produces: the rows, and the
    /// two things a reader would otherwise lose silently.
    /// </summary>
    public sealed class ParticleXmlParseResult
    {
        /// <summary>The 118 rows, in file order. Never null.</summary>
        public IReadOnlyList<ParticleDefinition> Definitions { get; }

        /// <summary>
        /// Attribute names that appear on a row and that <c>Emitter</c> has <b>no field for</b>, so the
        /// oracle drops them. Distinct and sorted; empty when the data and the code agree.
        ///
        /// <para><b>Why this exists.</b> <c>Emitter(param1)</c> assigns an attribute only
        /// <c>if (this.hasOwnProperty(name))</c> (<c>Emitter.as:111</c>). An unknown name is therefore
        /// discarded without a warning, a log line, or any other trace. That is the exact shape of the
        /// failure this project keeps hitting — a belief that goes stale while nothing goes red — and a
        /// reader that silently dropped them too would hide it a second time. Against the shipped data
        /// this reports <c>rr</c> and <c>rd</c>.</para>
        /// </summary>
        public IReadOnlyList<string> IgnoredAttributes { get; }

        /// <summary>
        /// Attributes that were present but whose value could not be parsed as the declared type, so the
        /// field fell back to its default. Distinct and sorted; empty for the shipped data.
        /// </summary>
        public IReadOnlyList<string> MalformedAttributes { get; }

        public ParticleXmlParseResult(
            IReadOnlyList<ParticleDefinition> definitions,
            IReadOnlyList<string> ignoredAttributes,
            IReadOnlyList<string> malformedAttributes)
        {
            Definitions = definitions ?? Array.Empty<ParticleDefinition>();
            IgnoredAttributes = ignoredAttributes ?? Array.Empty<string>();
            MalformedAttributes = malformedAttributes ?? Array.Empty<string>();
        }
    }

    /// <summary>
    /// Reads the <c>&lt;part&gt;</c> block out of <c>AllData.as</c> — the port of AS3's
    /// <c>Emitter(param1)</c> constructor and <c>Emitter.init()</c> (<c>Emitter.as:103-139</c>).
    ///
    /// <para><b>Why this lives in the runtime assembly rather than in the importer.</b> <c>PFE.Tests</c>
    /// references <c>PFE.Core</c> but not <c>PFE.Editor</c>, so anything that lives in the importer
    /// cannot be exercised by the offline wall at all. This is the same placement, and the same reason,
    /// as <c>WeaponXmlBlocks</c>, <c>WeaponSpreadMath</c> and <c>ArmourDataParser</c>.</para>
    ///
    /// <para><b>Regex, not an XML parser</b>, for the same reason those three use regex: the elements
    /// are embedded in an ActionScript source file, not in a well-formed document. Comments are stripped
    /// first — the rows are preceded by an XML comment (<c>AllData.as:6871-6888</c>) that spells out
    /// <c>alph='1'</c>, <c>ctrans='1'</c>, <c>rot='1'</c>, <c>water='1'</c>, <c>water='2'</c> and
    /// <c>anim='1'</c> as documentation, and that comment's <c>water='1'</c> is the <b>only</b>
    /// <c>water='1'</c> anywhere in the file. A reader that saw the documentation as data would invent a
    /// water rule no row has.</para>
    ///
    /// <para><b>Attributes are parsed into a map, not searched for by name.</b> That is the second
    /// defence, and it is structural: a per-name search
    /// (<c>Regex.Match(attrs, name + "='([^']*)'")</c>) matches a name that is the <b>suffix</b> of
    /// another, and this block has five such pairs. Measured against the shipped data, the naive reader
    /// would have been wrong on:</para>
    /// <list type="bullet">
    /// <item><description><c>dx</c> inside <c>rdx</c> — <c>dx</c> is set by <b>no row at all</b>, while
    /// 5 rows set <c>rdx</c> (<c>gilza</c>, <c>flame</c>, <c>arson</c>, <c>plakap</c>,
    /// <c>die_spark</c>). An unbounded read invents a <c>dx</c> of 1..4 on all five — a flat sideways
    /// velocity the oracle does not have, on the one particle family (flame) where it would be most
    /// visible. <c>radx</c> in the <c>&lt;upd&gt;</c> armour rows collides the same way, six more
    /// times.</description></item>
    /// <item><description><c>dy</c> inside <c>rdy</c> — 9 rows.</description></item>
    /// <item><description><c>alph</c> inside <c>prealph</c> — 4 rows, and the worse direction: the naive
    /// read <b>loses</b> the fade-in, because <c>prealph</c> is written after <c>alph</c> in every row
    /// that has both and the last match wins.</description></item>
    /// <item><description><c>grav</c> inside <c>rgrav</c> — 2 rows (<c>plakap</c>, <c>die_spark</c>),
    /// turning a gravity <i>jitter</i> into a fixed gravity.</description></item>
    /// <item><description><c>scale</c> inside <c>camscale</c> — 6 rows, all camera-scaled text or marker
    /// parts; the naive read would take <c>scale='1'</c> from <c>camscale='1'</c>.</description></item>
    /// </list>
    /// </summary>
    public static class ParticleXmlReader
    {
        /// <summary>
        /// One <c>&lt;part …/&gt;</c> row. All 118 rows in the shipped data are self-closing and carry a
        /// unique <c>id</c>, so there is no body to capture and no closing tag to find — unlike the
        /// <c>&lt;weapon&gt;</c> blocks, which come in both forms.
        ///
        /// <para>The attribute run is <c>[^&gt;]*</c> rather than <c>.*?</c> so a match can never cross a
        /// tag boundary, which also stops it running past the end of the row if the file contains a
        /// malformed one.</para>
        /// </summary>
        private static readonly Regex RowPattern =
            new Regex(@"<part\s+id='([^']+)'([^>]*)/>", RegexOptions.Singleline | RegexOptions.Compiled);

        /// <summary>Every <c>name='value'</c> pair in an attribute run. Accepts either quote style.</summary>
        private static readonly Regex AttributePattern =
            new Regex(@"([A-Za-z_][A-Za-z0-9_]*)\s*=\s*['""]([^'""]*)['""]", RegexOptions.Compiled);

        private static readonly Regex XmlCommentPattern =
            new Regex(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);

        /// <summary>
        /// The names <c>Emitter</c> declares, and therefore the names
        /// <c>if (this.hasOwnProperty(name))</c> accepts. Anything outside this set is dropped by the
        /// oracle, and reported through <see cref="ParticleXmlParseResult.IgnoredAttributes"/>.
        ///
        /// <para><c>frame</c>, <c>dframe</c> and <c>move</c> are in the set because <c>Emitter</c>
        /// declares them — the oracle accepts the attribute and then never reads it (<c>cast</c> resets
        /// <c>frame</c>/<c>dframe</c> at <c>:180</c>, and <c>move</c> is never read at all). They are
        /// recognised-but-dead rather than ignored, and no shipped row sets any of them.</para>
        /// </summary>
        private static readonly HashSet<string> KnownAttributes = new HashSet<string>(StringComparer.Ordinal)
        {
            "id", "vis", "sloy", "imp", "blit", "blitx", "blity", "blitf", "blitd", "ctrans",
            "alph", "prealph", "anim", "blend", "rsc", "scale", "frame", "dframe", "otklad",
            "filter", "move", "minliv", "rliv", "minv", "rv", "rx", "ry", "rdx", "rdy", "rdr",
            "dx", "dy", "rot", "brake", "grav", "rgrav", "water", "maxkol", "camscale",
        };

        /// <summary>
        /// Reads every <c>&lt;part&gt;</c> row in the source. Never returns null; an empty or row-less
        /// source yields an empty definition list.
        /// </summary>
        public static ParticleXmlParseResult Parse(string allData)
        {
            var definitions = new List<ParticleDefinition>();
            var ignored = new SortedSet<string>(StringComparer.Ordinal);
            var malformed = new SortedSet<string>(StringComparer.Ordinal);

            if (string.IsNullOrEmpty(allData))
                return new ParticleXmlParseResult(definitions, null, null);

            string source = XmlCommentPattern.Replace(allData, string.Empty);

            foreach (Match row in RowPattern.Matches(source))
            {
                string id = row.Groups[1].Value;
                Dictionary<string, string> attrs = ReadAttributes(row.Groups[2].Value);

                definitions.Add(ReadRow(id, attrs, malformed));

                foreach (string name in attrs.Keys)
                {
                    if (!KnownAttributes.Contains(name)) ignored.Add(name);
                }
            }

            return new ParticleXmlParseResult(
                definitions,
                new List<string>(ignored),
                new List<string>(malformed));
        }

        /// <summary>
        /// Every attribute on one row, as a map. A later duplicate of the same name overwrites an
        /// earlier one, which is what AS3's constructor does too — it assigns per attribute node in
        /// document order (<c>Emitter.as:108-122</c>).
        /// </summary>
        private static Dictionary<string, string> ReadAttributes(string attrs)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match m in AttributePattern.Matches(attrs))
            {
                map[m.Groups[1].Value] = m.Groups[2].Value;
            }
            return map;
        }

        /// <summary>
        /// One row, with the oracle's constructor semantics: start from the declared defaults and
        /// overwrite only what the row carries.
        /// </summary>
        private static ParticleDefinition ReadRow(
            string id, Dictionary<string, string> a, ISet<string> malformed)
        {
            var def = new ParticleDefinition { Id = id };

            def.Vis = String(a, "vis");
            def.Blit = String(a, "blit");
            def.BlitX = Int(a, "blitx", def.BlitX, malformed);
            def.BlitY = Int(a, "blity", def.BlitY, malformed);
            def.BlitLoopFrames = Int(a, "blitf", def.BlitLoopFrames, malformed);
            def.BlitDelta = Float(a, "blitd", def.BlitDelta, malformed);
            def.Anim = Int(a, "anim", def.Anim, malformed);
            def.Sloy = Int(a, "sloy", def.Sloy, malformed);
            def.Imp = Int(a, "imp", def.Imp, malformed);
            def.MaxKol = Int(a, "maxkol", def.MaxKol, malformed);

            def.MinLiv = Int(a, "minliv", def.MinLiv, malformed);
            def.RLiv = Int(a, "rliv", def.RLiv, malformed);

            def.MinV = Float(a, "minv", def.MinV, malformed);
            def.RV = Float(a, "rv", def.RV, malformed);
            def.RX = Float(a, "rx", def.RX, malformed);
            def.RY = Float(a, "ry", def.RY, malformed);
            def.RDX = Float(a, "rdx", def.RDX, malformed);
            def.RDY = Float(a, "rdy", def.RDY, malformed);
            def.DX = Float(a, "dx", def.DX, malformed);
            def.DY = Float(a, "dy", def.DY, malformed);
            def.RDR = Float(a, "rdr", def.RDR, malformed);
            def.Rot = Int(a, "rot", def.Rot, malformed);
            def.Brake = Float(a, "brake", def.Brake, malformed);

            def.Grav = Float(a, "grav", def.Grav, malformed);
            def.RGrav = Float(a, "rgrav", def.RGrav, malformed);

            def.Scale = Float(a, "scale", def.Scale, malformed);
            def.Rsc = Float(a, "rsc", def.Rsc, malformed);
            def.Alph = Bool(a, "alph");
            def.PreAlph = Bool(a, "prealph");
            def.Ctrans = Bool(a, "ctrans");
            def.Blend = String(a, "blend") ?? def.Blend;
            def.Filter = String(a, "filter");
            def.CamScale = Bool(a, "camscale");

            def.Water = Int(a, "water", def.Water, malformed);
            def.Otklad = Int(a, "otklad", def.Otklad, malformed);

            return def;
        }

        // ── Typed access ──────────────────────────────────────────────────────
        //
        // All parsing uses CultureInfo.InvariantCulture. The default overloads accept thousands
        // separators, so on a locale where '.' is a group separator (de-DE) '0.4' parses as 400 — a
        // silent ×1000 error on every rsc and rgrav. The XML always uses '.'.

        private static string String(Dictionary<string, string> a, string name) =>
            a.TryGetValue(name, out string v) ? v : null;

        /// <summary>
        /// Presence, not value. <b>AS3 sets a boolean field to <c>true</c> whenever the attribute is
        /// present at all</b>, because the constructor's test is <c>if (this[name] is Boolean)</c>
        /// rather than a parse (<c>Emitter.as:113-116</c>) — so <c>alph='0'</c> would mean
        /// <b>true</b>, not false. No shipped row sets a boolean to <c>'0'</c> (verified for
        /// <c>ctrans</c>, <c>alph</c>, <c>prealph</c>, <c>camscale</c> and <c>move</c>), so the quirk is
        /// unreachable in practice; it is reproduced anyway because a reader that parsed <c>'0'</c> as
        /// false would silently disagree with the oracle the moment someone wrote one.
        /// </summary>
        private static bool Bool(Dictionary<string, string> a, string name) => a.ContainsKey(name);

        private static int Int(Dictionary<string, string> a, string name, int fallback, ISet<string> malformed)
        {
            if (!a.TryGetValue(name, out string raw)) return fallback;
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)) return value;
            malformed?.Add(name);
            return fallback;
        }

        private static float Float(Dictionary<string, string> a, string name, float fallback, ISet<string> malformed)
        {
            if (!a.TryGetValue(name, out string raw)) return fallback;
            if (float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)) return value;
            malformed?.Add(name);
            return fallback;
        }
    }
}
