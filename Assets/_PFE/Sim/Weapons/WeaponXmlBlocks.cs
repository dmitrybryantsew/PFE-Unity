using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using PFE.Data.Definitions;

// AllData.as XML readers: splitting the file into <weapon> blocks (WeaponXmlBlocks), reading one
// attribute off a block or sub-node (WeaponXmlAttrs), and reading a spell's attributes off an <item>
// row (SpellItemXml). All live here, in the RUNTIME assembly, rather than in PFE.Editor where they are
// used — PFE.Tests does not reference PFE.Editor, so importer-side code cannot be exercised by the
// offline wall at all, and these readers have had silent-wrong-value bugs that lived in exactly that
// blind spot.
//
// The file name is historical: SpellItemXml reads <item> rows, not <weapon> blocks. It lives here
// because this file is the project's established home for "AllData.as readers that must stay
// offline-testable", and because a new file is not compiled until Unity regenerates the generated
// .csproj (PFE.Core.csproj lists one <Compile Include> per file), which would break the offline build
// for every session until then. Rename it when that constraint stops mattering.

namespace PFE.Systems.Weapons
{
    /// <summary>
    /// One <c>&lt;weapon …&gt;</c> block lifted out of <c>AllData.as</c>, before any attribute is
    /// interpreted. <see cref="Body"/> is the text between the open and close tags; a self-closing
    /// block (<c>&lt;weapon … /&gt;</c>) has an empty body, which is the honest answer — it has none.
    /// </summary>
    public readonly struct WeaponXmlBlock
    {
        /// <summary>Value of the block's <c>id</c> attribute.</summary>
        public readonly string Id;

        /// <summary>Everything between <c>id='…'</c> and the end of the opening tag — the root attributes.</summary>
        public readonly string RootAttrs;

        /// <summary>Text between the tags. Empty for a self-closing block.</summary>
        public readonly string Body;

        /// <summary>True for <c>&lt;weapon … /&gt;</c> — no body, and no closing tag in the source.</summary>
        public readonly bool SelfClosing;

        public WeaponXmlBlock(string id, string rootAttrs, string body, bool selfClosing)
        {
            Id          = id;
            RootAttrs   = rootAttrs;
            Body        = body;
            SelfClosing = selfClosing;
        }
    }

    /// <summary>
    /// Splits <c>AllData.as</c> into its <c>&lt;weapon&gt;</c> blocks.
    ///
    /// <para><b>Why this is a separate, pure type rather than a few lines inside
    /// <c>WeaponDataImporter</c>.</b> <c>PFE.Tests</c> references <c>PFE.Core</c> but <i>not</i>
    /// <c>PFE.Editor</c>, so a pattern that lives in the importer cannot be exercised by the offline
    /// wall at all — and this pattern had a bug that nothing could catch, in exactly that blind spot.
    /// Living in the runtime assembly makes it testable with no editor and no Unity. Same reason and
    /// same placement as <see cref="WeaponSpreadMath"/>.</para>
    ///
    /// <para><b>The bug this exists to prevent.</b> The importer used to match
    /// <c>&lt;weapon\s+id='([^']+)'([^&gt;]*)&gt;(.*?)&lt;/weapon&gt;</c> — a pattern that requires a
    /// literal closing tag. Nine weapons in <c>AllData.as</c> are written self-closing
    /// (<c>&lt;weapon id='sp_slow' tip='5' skill='6' perslvl='3' spell='1'/&gt;</c>, lines
    /// 4017-4025), and they are the <b>last</b> weapon entries in the file, so there is no later
    /// <c>&lt;/weapon&gt;</c> for the pattern to latch onto and the match simply never happens. The
    /// importer therefore reported 204 weapons, silently skipped those nine, and left their
    /// <c>.asset</c> files frozen at an earlier run — measured 2026-09-29 against 2026-10-03 for the
    /// other 204. Nothing went red: the import succeeded, and the missing assets already existed.</para>
    ///
    /// <para><b>How the replacement was checked.</b> On the real file: old pattern 204 matches, new
    /// pattern 213 == the count of literal <c>&lt;weapon id='</c> occurrences; no duplicate ids; all
    /// six sampled closed blocks (<c>p10mm</c>, <c>fireball</c>, <c>eclipse</c>, <c>mray</c>,
    /// <c>hmine</c>, <c>paint</c>) still match; and <b>zero</b> closed blocks changed body text.</para>
    /// </summary>
    public static class WeaponXmlBlocks
    {
        /// <summary>
        /// Matches both <c>&lt;weapon …&gt;…&lt;/weapon&gt;</c> and <c>&lt;weapon …/&gt;</c>.
        ///
        /// <para>The attribute run is non-greedy so that a self-closing tag terminates on its own
        /// <c>/&gt;</c>; for a block with a body it can only fall through to the <c>&gt;</c>
        /// alternative, because no weapon root attribute contains a literal <c>/&gt;</c>. Group 3
        /// (the body) is therefore <b>unmatched</b> — not empty — for a self-closing block, which is
        /// how <see cref="WeaponXmlBlock.SelfClosing"/> is derived.</para>
        ///
        /// <para><c>Singleline</c> is required: the body spans newlines.</para>
        /// </summary>
        private static readonly Regex Pattern = new Regex(
            @"<weapon\s+id='([^']+)'([^>]*?)(?:/>|>(.*?)</weapon>)",
            RegexOptions.Singleline);

        /// <summary>
        /// Every <c>&lt;weapon&gt;</c> block in the source, in file order. Never null; an empty
        /// source yields an empty list.
        /// </summary>
        public static List<WeaponXmlBlock> Parse(string allData)
        {
            var blocks = new List<WeaponXmlBlock>();
            if (string.IsNullOrEmpty(allData)) return blocks;

            foreach (Match m in Pattern.Matches(allData))
            {
                bool hasBody = m.Groups[3].Success;
                blocks.Add(new WeaponXmlBlock(
                    m.Groups[1].Value,
                    m.Groups[2].Value,
                    hasBody ? m.Groups[3].Value : "",
                    !hasBody));
            }

            return blocks;
        }

        // ── Cached id lookup ──────────────────────────────────────────────
        //
        // `Parse` walks a 360 KB document. That is fine once, not once per row of a screen that
        // repaints on every hover, so the index is memoised against the source string.
        //
        // The cache is keyed on REFERENCE identity, not on `==`: two different AllData texts that
        // happen to compare equal would otherwise be treated as the same document. `TextAsset.text`
        // is stable for the lifetime of the asset, which is what makes reference identity enough.

        private static string _indexedSource;
        private static Dictionary<string, WeaponXmlBlock> _index;

        /// <summary>
        /// The block for one weapon id, or false when the source has no such row.
        ///
        /// <para>Thread-safety is by "last writer wins": the two fields are assigned together and a
        /// torn read can only produce a fresh index built from a different source, which this class
        /// treats as a miss on the next call. The interface reads it from the main thread only.</para>
        /// </summary>
        public static bool TryGetBlock(string allData, string id, out WeaponXmlBlock block)
        {
            block = default;
            if (string.IsNullOrEmpty(allData) || string.IsNullOrEmpty(id)) return false;

            if (!ReferenceEquals(_indexedSource, allData))
            {
                var built = new Dictionary<string, WeaponXmlBlock>(StringComparer.Ordinal);
                foreach (WeaponXmlBlock b in Parse(allData))
                {
                    // Last wins, matching AS3 `d.weapon` where a later row shadows an earlier one.
                    built[b.Id] = b;
                }

                _index = built;
                _indexedSource = allData;
            }

            return _index != null && _index.TryGetValue(id, out block);
        }
    }

    /// <summary>
    /// The <c>AllData.as</c> values the PipBuck's weapon pane needs that <see cref="WeaponXmlAttrs"/>
    /// cannot reach, because <b>none of them is a field of <c>WeaponDefinition</c></b> and the importer
    /// does not write one.
    ///
    /// <para>Measured 2026-10-13 against all 213 <c>&lt;weapon&gt;</c> rows: <c>skill</c> and <c>lvl</c>
    /// ARE already on the asset (<c>WeaponDefinition.skillLevel</c> is AllData's <c>@skill</c> and
    /// <c>weaponLevel</c> is <c>@lvl</c>, 0 mismatches on all 213), which is why they are not read
    /// here. These are not. <c>@perslvl</c> used to be in this list too; it became a
    /// <c>WeaponDefinition</c> field on 2026-10-14, so it is read off the asset now, not here.</para>
    ///
    /// <list type="bullet">
    /// <item><description><c>satsCons</c> — <c>&lt;sats cons='25'/&gt;</c>. S.A.T.S. action-point cost.
    /// <c>Weapon.as:294</c> defaults it to <b>10</b>, so "no <c>&lt;sats&gt;</c> node" and
    /// "<c>cons</c> absent" are two different answers and the node's presence is tracked
    /// separately.</description></item>
    /// <item><description><c>satsQue</c> — <c>&lt;sats que='3'/&gt;</c>. Shots covered per AP charge;
    /// <c>Weapon.as:292</c> defaults it to 1.</description></item>
    /// <item><description><c>noSats</c> — <c>&lt;sats no/&gt;</c> present. Suppresses the AP line
    /// entirely; presence-based (<c>Weapon.as:447</c>).</description></item>
    /// <item><description><c>perk</c> — the root attribute <c>perk='stunning'</c>, printed as
    /// <c>Res.txt("e", perk)</c>. Absent means no Perk line at all.</description></item>
    /// <item><description><c>mass</c> — <c>&lt;phis m='2'/&gt;</c>. This is the one the Oracle's
    /// <c>Weapon.mass</c> really reads; <c>WeaponDefinition.Mass</c> is the documented
    /// type-based approximation that is wrong for the 74 rows carrying an explicit
    /// <c>m</c>.</description></item>
    /// </list>
    /// </summary>
    public readonly struct WeaponXmlExtras
    {
        /// <summary>S.A.T.S. action-point cost — <c>&lt;sats cons&gt;</c>, or the AS3 default 10 when the node is absent.</summary>
        public readonly float SatsCons;

        /// <summary>False when the row has no <c>&lt;sats&gt;</c> node, in which case <see cref="SatsCons"/> is the default.</summary>
        public readonly bool HasSats;

        /// <summary>
        /// <c>&lt;sats que&gt;</c> — the number of S.A.T.S. queued shots the AP cost covers, AS3 default
        /// 1 (<c>Weapon.as:292</c>, read at <c>:439</c>). The info pane appends <c>(xN)</c> when it
        /// exceeds 1 (<c>PipPage.as:570-573</c>).
        /// </summary>
        public readonly int SatsQue;

        /// <summary>
        /// <c>&lt;sats no&gt;</c> present — the weapon cannot be used in S.A.T.S. at all, so the whole
        /// AP line is omitted (<c>Weapon.as:447</c> sets <c>noSats</c>; <c>PipPage.as:557</c> gates on
        /// it). Presence-based, not value-based: AS3 tests <c>@no.length()</c>, so <c>no='0'</c> is
        /// also "true". Distinct from <see cref="HasSats"/>, which is the node's own presence.
        /// </summary>
        public readonly bool NoSats;

        /// <summary>The <c>perk</c> root attribute, or empty when the weapon declares none.</summary>
        public readonly string Perk;

        /// <summary><c>&lt;phis m&gt;</c>, the author's own weight. Only meaningful when <see cref="HasMass"/>.</summary>
        public readonly float Mass;

        /// <summary>False when the row has no <c>&lt;phis m&gt;</c>, leaving <see cref="WeaponXmlAttrs"/> users to fall back.</summary>
        public readonly bool HasMass;

        public WeaponXmlExtras(float satsCons, bool hasSats, int satsQue, bool noSats,
                               string perk, float mass, bool hasMass)
        {
            SatsCons = satsCons;
            HasSats = hasSats;
            SatsQue = satsQue;
            NoSats = noSats;
            Perk = perk ?? string.Empty;
            Mass = mass;
            HasMass = hasMass;
        }

        /// <summary>No row resolved: the AS3 defaults, with both presence flags false.</summary>
        public static WeaponXmlExtras Empty =>
            new WeaponXmlExtras(SatsConsDefault, false, SatsQueDefault, false, "", 0f, false);

        /// <summary><c>Weapon.as:294</c> — <c>public var satsCons:Number = 10;</c>.</summary>
        public const float SatsConsDefault = 10f;

        /// <summary><c>Weapon.as:292</c> — <c>public var satsQue:int = 1;</c>.</summary>
        public const int SatsQueDefault = 1;
    }

    /// <summary>
    /// Reads <see cref="WeaponXmlExtras"/> off one <c>&lt;weapon&gt;</c> block.
    ///
    /// <para>The sub-node attributes are read out of the two node runs directly rather than through
    /// <see cref="WeaponVariantXml.Read"/>, because a variant override is not what a screen wants:
    /// <c>Weapon.as:443</c> reads <c>node.sats[0].@cons</c> off the merged node, and this reader is
    /// asked for the base row.</para>
    /// </summary>
    public static class WeaponXmlExtrasReader
    {
        /// <summary>
        /// Matches <c>&lt;sats …/&gt;</c> and <c>&lt;phis …/&gt;</c> wherever they appear in the body,
        /// self-closing or not — AllData.as writes both forms (<c>&lt;phis …/&gt;</c> for a weapon,
        /// <c>&lt;phis …&gt;&lt;/phis&gt;</c> never appears, but the alternation costs nothing and
        /// removes an assumption).
        /// </summary>
        private static string Node(string body, string tag)
        {
            if (string.IsNullOrEmpty(body)) return null;
            var m = Regex.Match(body, $@"<{tag}\s([^>]*?)/?>", RegexOptions.Singleline);
            return m.Success ? m.Groups[1].Value : null;
        }

        public static WeaponXmlExtras Read(in WeaponXmlBlock block)
        {
            string sats = Node(block.Body, "sats");
            string phis = Node(block.Body, "phis");

            float cons = sats != null
                ? WeaponXmlAttrs.AttrF(sats, "cons", WeaponXmlExtras.SatsConsDefault)
                : WeaponXmlExtras.SatsConsDefault;

            // Weapon.as:439 / :447 — both presence-gated on the attribute existing, so an absent
            // `que` keeps the 1 default and an absent `no` keeps false. `AttrPresent` (not AttrBool)
            // for `no`: AS3 tests `@no.length()`, so `no='0'` is still "cannot use S.A.T.S.".
            int que = sats != null
                ? WeaponXmlAttrs.AttrI(sats, "que", WeaponXmlExtras.SatsQueDefault)
                : WeaponXmlExtras.SatsQueDefault;
            bool noSats = sats != null && WeaponXmlAttrs.AttrPresent(sats, "no");

            bool hasMass = phis != null && WeaponXmlAttrs.AttrPresent(phis, "m");
            float mass = hasMass ? WeaponXmlAttrs.AttrF(phis, "m", 0f) : 0f;

            return new WeaponXmlExtras(cons, sats != null, que, noSats, block.RootAttrs == null
                ? string.Empty
                : WeaponXmlAttrs.Attr(block.RootAttrs, "perk", string.Empty), mass, hasMass);
        }
    }

    /// <summary>
    /// Attribute access on an AllData.as element — the four readers the weapon importer uses.
    ///
    /// <para><b>The bug this exists to prevent: an attribute name that is the SUFFIX of another
    /// name.</b> The readers used to be <c>Regex.Match(src, name + "='([^']*)'")</c> with no boundary,
    /// so <c>Attr(char, "expl")</c> matched inside <c>damexpl='750'</c> whenever <c>damexpl</c> was
    /// written first. Three pairs collide in the shipped data, and all three were silently wrong:</para>
    ///
    /// <list type="bullet">
    /// <item><description><c>lvl</c> inside <c>perslvl</c> — 24 weapons. <c>balemine</c> is
    /// <c>&lt;weapon … lvl='5'&gt;</c> and is unaffected, but <c>fireball</c> is
    /// <c>&lt;weapon … perslvl='12'&gt;</c> with no <c>lvl</c>, so <c>weaponLevel</c> read 12 instead
    /// of 0 — and <c>weaponLevel</c> feeds the <c>checkAvail</c> skill gate, which then refuses a
    /// weapon the owner is allowed to use.</description></item>
    /// <item><description><c>expl</c> inside <c>damexpl</c> — 55 weapons. <c>balemine</c>'s
    /// <c>&lt;char … damexpl='750' … expl='300'/&gt;</c> gave <c>explRadius</c> 750 instead of 300: a
    /// plasma mine with a 2.5× blast.</description></item>
    /// <item><description><c>kol</c> inside <c>dkol</c> — 13 weapons. <c>dronlaser</c> has
    /// <c>&lt;char … dkol='15' …/&gt;</c> and no <c>kol</c>, so <c>projectilesPerShot</c> read 15
    /// instead of the fallback 1 — a drone laser firing fifteen rounds per shot.</description></item>
    /// </list>
    ///
    /// <para><b>Measured blast radius of the boundary fix:</b> 81 attribute reads across 72 of the
    /// 213 weapons change, and exactly three (node, attribute) pairs are involved — <c>(root, lvl)</c>,
    /// <c>(char, kol)</c>, <c>(char, expl)</c>. A control set of 27 non-colliding reads (<c>tip</c>,
    /// <c>prec</c>, <c>damexpl</c>, <c>magic</c>, <c>mana</c>, <c>vbul</c>, <c>probiv</c>, …) is
    /// identical on all 213 weapons, which is what rules out the boundary over-reaching.</para>
    ///
    /// <para>The boundary is <c>(?:^|\s)</c>: a name must start the string or follow whitespace. Every
    /// string passed here begins with whitespace — the importer builds them from <c>(\s[^&gt;]*)</c>
    /// captures — so no legitimate read is affected.</para>
    /// </summary>
    public static class WeaponXmlAttrs
    {
        /// <summary>
        /// The search pattern for <paramref name="name"/>. <c>Regex.Escape</c> is belt-and-braces
        /// (every name here is a plain identifier); the boundary is the load-bearing part, so it is
        /// built in exactly one place.
        /// </summary>
        private static string Pattern(string name, string valuePattern)
            => $@"(?:^|\s){Regex.Escape(name)}={valuePattern}";

        /// <summary>Raw attribute text, or <paramref name="fallback"/> when the attribute is absent.</summary>
        public static string Attr(string src, string name, string fallback = "")
        {
            if (string.IsNullOrEmpty(src)) return fallback;
            var m = Regex.Match(src, Pattern(name, @"'([^']*)'"));
            return m.Success ? m.Groups[1].Value : fallback;
        }

        /// <summary>
        /// Attribute as a float, or <paramref name="fallback"/> when absent.
        ///
        /// <para><c>InvariantCulture</c> is deliberate. AllData.as writes every decimal with a
        /// <c>.</c> and no attribute value anywhere in the file contains a comma (checked: 0 matches
        /// for <c>='[0-9]*,[0-9]*'</c>), so the invariant parse is a no-op today — but parsing under
        /// the ambient culture would make every float depend on the machine's locale, i.e. right on
        /// one workstation and 10× off on the next with no error either way.</para>
        ///
        /// <para><b>Never throws.</b> AS3's <c>Number(x)</c> answers <c>NaN</c> for a non-numeric
        /// string and the attribute is still "present", so a throw here would be a divergence the
        /// Oracle cannot have. A value that will not parse falls back instead.</para>
        /// </summary>
        public static float AttrF(string src, string name, float fallback = 0f)
        {
            var s = Attr(src, name);
            return string.IsNullOrEmpty(s) || !float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
                ? fallback
                : v;
        }

        /// <summary>
        /// Attribute as an int, or <paramref name="fallback"/> when absent.
        ///
        /// <para><b>Never throws, and a fractional value is truncated</b> — that is AS3's
        /// <c>int(x)</c>, which coerces through Number and rounds toward zero. Both halves are load
        /// bearing on this data: <c>&lt;item&gt;</c> rows carry <c>price='0.3'</c>, <c>m='0.1'</c> and
        /// <c>sell='0.5'</c>, and <c>&lt;mat&gt;</c> rows carry <c>m='А'</c> (a Cyrillic index letter,
        /// not a number at all). A <c>Parse</c> that throws on either is not the Oracle's behaviour —
        /// it is a crash the Oracle cannot have.</para>
        ///
        /// <para><b>Do not use this for an AS3 <c>@attr &gt; 0</c> test.</b> That comparison is a
        /// <i>Number</i> comparison, so <c>m='0.1'</c> is positive; truncating first would read it as
        /// zero and drop the line. Use <see cref="AttrF"/> for those, as
        /// <c>PipPageInvView</c> does.</para>
        /// </summary>
        public static int AttrI(string src, string name, int fallback = 0)
        {
            var s = Attr(src, name);
            if (string.IsNullOrEmpty(s)) return fallback;
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i)) return i;
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)
                ? (int)f
                : fallback;
        }

        /// <summary>
        /// True when the attribute is present with value <c>'1'</c>. AS3's <i>presence</i> tests
        /// (<c>Boolean(node.@radio.length())</c>) are reproduced by the caller, which knows the field
        /// default; this only reads the value, and an absent attribute is false.
        /// </summary>
        public static bool AttrBool(string src, string name)
            => !string.IsNullOrEmpty(src) && Regex.IsMatch(src, Pattern(name, "'1'"));

        /// <summary>
        /// True when the attribute is present <b>at all</b>, whatever its value — AS3's
        /// <c>Boolean(node.@name.length())</c> idiom, which is how the oracle reads the boolean flags
        /// on an <c>&lt;item&gt;</c> row (<c>Spell.as:116-126</c>: <c>prod</c>, <c>tele</c>, <c>atk</c>).
        ///
        /// <para><b>A different question from <see cref="AttrBool"/>, and the difference is real.</b>
        /// <c>atk='0'</c> is <b>true</b> here and false there; AS3 only ever asks this one, because it
        /// never compares the value — <c>@atk.length()</c> is 1 for any one-character value. Reproduced
        /// rather than "corrected", since the shipped rows all write <c>'1'</c> and a future row
        /// writing <c>'0'</c> must still mean "present". Same boundary anchoring as
        /// <see cref="Attr"/>, for the same reason.</para>
        /// </summary>
        public static bool AttrPresent(string src, string name)
            => !string.IsNullOrEmpty(src) && Regex.IsMatch(src, Pattern(name, @"'[^']*'"));
    }

    /// <summary>
    /// Reads a spell's attributes off an <c>AllData.as</c> <c>&lt;item tip='spell' …&gt;</c> row into a
    /// <see cref="SpellData"/> — the port of <c>Spell.as:83-131</c>.
    ///
    /// <para><b>Why this is a runtime type and not a few lines in <c>SimpleDataImporter</c>.</b> Same
    /// reason as everything else in this file: <c>PFE.Tests</c> does not reference <c>PFE.Editor</c>, so
    /// a mapping written inside the importer cannot be exercised by the offline wall at all. The
    /// attribute-name-to-field mapping is precisely where this project's silent-wrong-value bugs live —
    /// the <c>lvl</c>⊂<c>perslvl</c> family documented on <see cref="WeaponXmlAttrs"/> — so it is the
    /// last place to accept an untestable blind spot.</para>
    ///
    /// <para><b>Presence, not value.</b> AS3 guards every read with <c>if(this.xml.@x.length())</c>, so
    /// a missing attribute leaves the field alone. Three fields are <i>pure</i> presence flags whose
    /// value AS3 never inspects — <c>prod</c>, <c>tele</c>, <c>atk</c> (<c>:116-126</c>) — and they use
    /// <see cref="WeaponXmlAttrs.AttrPresent"/> rather than <c>AttrBool</c> for exactly that reason.
    /// <c>line</c> is a flag on the same footing.</para>
    ///
    /// <para><b>Deliberately not read here:</b> <c>price</c>, <c>mess</c> and <c>pet_info</c>. They are
    /// general <c>&lt;item&gt;</c> attributes, not spell ones — see <see cref="SpellData"/>'s remarks.
    /// <c>price</c> is applied to <c>basePrice</c> by the importer, which is where a general item field
    /// belongs.</para>
    /// </summary>
    public static class SpellItemXml
    {
        /// <summary>
        /// Parse one spell row. <paramref name="itemAttrs"/> is the text inside the
        /// <c>&lt;item …&gt;</c> tag (its root attributes, with no angle brackets). A row carrying none
        /// of these attributes yields a default <see cref="SpellData"/> whose
        /// <see cref="SpellData.IsPopulated"/> is false — which is what an unimported asset looks like.
        /// </summary>
        public static SpellData Read(string itemAttrs)
        {
            return new SpellData
            {
                hp    = WeaponXmlAttrs.AttrF(itemAttrs, "hp"),
                mana  = WeaponXmlAttrs.AttrF(itemAttrs, "mana"),
                magic = WeaponXmlAttrs.AttrF(itemAttrs, "magic"),
                culd  = WeaponXmlAttrs.AttrF(itemAttrs, "culd"),
                dist  = WeaponXmlAttrs.AttrF(itemAttrs, "dist"),
                rad   = WeaponXmlAttrs.AttrF(itemAttrs, "rad"),
                dam   = WeaponXmlAttrs.AttrF(itemAttrs, "dam"),

                // `null`, NOT Attr's `""` default, and the difference is the oracle's.
                // Spell.as:69 declares `public var snd:String;` with no initializer, so an absent
                // attribute leaves the field null; :129 assigns only `if(this.xml.@snd.length())`.
                // AS3's `.length()` is 1 for a present-but-empty `snd=''` and 0 for an absent one, so
                // the two cases are genuinely distinct there and must stay distinct here: absent ->
                // null (never assigned), `snd=''` -> "" (assigned an empty string). Passing null as
                // the fallback is what reproduces that; Attr's "" default would collapse them.
                snd   = WeaponXmlAttrs.Attr(itemAttrs, "snd", null),

                // Presence, not value — see the class remarks. `atk='0'` must read true.
                line  = WeaponXmlAttrs.AttrPresent(itemAttrs, "line"),
                prod  = WeaponXmlAttrs.AttrPresent(itemAttrs, "prod"),
                tele  = WeaponXmlAttrs.AttrPresent(itemAttrs, "tele"),
                atk   = WeaponXmlAttrs.AttrPresent(itemAttrs, "atk"),
            };
        }
    }
}

namespace PFE.Data.Definitions
{
    /// <summary>
    /// The index-N override of one <c>&lt;weapon&gt;</c> block — the port's carrier for AS3's
    /// <c>Weapon.variant</c>.
    ///
    /// <para><b>Why this file, and why this namespace.</b> The namespace is
    /// <c>PFE.Data.Definitions</c> because this is a <i>data definition</i> — the same treatment
    /// <c>SpellData</c> gets in <c>Sim/Data/SpellData.cs</c>, which is likewise a data type that lives in
    /// the <c>PFE.Sim</c> assembly so the offline wall can reach it. The file is
    /// <c>Sim/Weapons/WeaponXmlBlocks.cs</c> rather than a new one for the reason recorded at the top of
    /// it: a new <c>.cs</c> is compiled by nothing until Unity regenerates the generated
    /// <c>.csproj</c>, which breaks the offline build for every session until the editor next takes
    /// focus.</para>
    ///
    /// <para><b>What the feature is.</b> "Unique" / "II" weapons are not a separate weapon: a weapon
    /// <i>has</i> a variant when its block carries a <b>second</b> <c>&lt;char&gt;</c>
    /// (<c>Stand.as:212</c>, <c>weap.char.length() &gt; 1</c>), and the repeated child elements
    /// (<c>char phis vis snd ammo dop com</c>) are <b>arrays indexed by variant</b>. Measured on the
    /// shipped <c>AllData.as</c>: 71 of 213 weapons have one, and <b>all seven</b> kinds repeat —
    /// <c>&lt;char&gt;</c> on 71, <c>&lt;ammo&gt;</c> on 27, <c>&lt;phis&gt;</c> on 11, <c>&lt;com&gt;</c> on 9,
    /// <c>&lt;dop&gt;</c> on 6, <c>&lt;vis&gt;</c> on 3, <c>&lt;snd&gt;</c> on 1. An importer that read only
    /// <c>&lt;char&gt;[1]</c> would silently drop 27 ammo overrides and 11 phis ones.</para>
    ///
    /// <para><b>Why the override is held as raw attribute runs rather than as typed nullable fields.</b>
    /// AS3's merge (<c>Weapon.getXmlParam</c>) is <i>presence-gated</i>: <c>getCharParam</c>
    /// (<c>Weapon.as:777-861</c>) tests <c>param1.@x.length()</c> per attribute and only then assigns, so
    /// tier 1 is a <b>delta</b> over tier 0 — <c>oldshot</c> goes 6→7 damage but <i>keeps</i>
    /// <c>kol=5</c> and <c>knock=3</c>. Flattening it into typed fields would turn "absent" into
    /// "inherit 0" and quietly change every stat the variant node does not mention. Keeping the runs raw
    /// also means the merge reuses <see cref="PFE.Systems.Weapons.WeaponXmlAttrs"/> unchanged, so there
    /// is exactly <b>one</b> attribute-name→field mapping in the project rather than a second one to
    /// drift.</para>
    ///
    /// <para><b>Empty string, not null, is the absent state</b> — every reader here treats <c>""</c> and
    /// <c>null</c> alike, and Unity zero-fills a field absent from an existing asset.</para>
    /// </summary>
    [System.Serializable]
    public struct WeaponVariantData
    {
        /// <summary>
        /// Which variant index this describes. <b>0 means "this weapon has no variant"</b>, which is the
        /// answer for 142 of the 213 weapons and the state every pre-existing asset loads as.
        /// </summary>
        public int Variant;

        /// <summary>Index-N <c>&lt;char&gt;</c> attribute run, or <c>""</c> when that node does not repeat.</summary>
        public string CharAttrs;

        /// <summary>Index-N <c>&lt;phis&gt;</c> attribute run, or <c>""</c>.</summary>
        public string PhisAttrs;

        /// <summary>Index-N <c>&lt;vis&gt;</c> attribute run, or <c>""</c>.</summary>
        public string VisAttrs;

        /// <summary>Index-N <c>&lt;snd&gt;</c> attribute run, or <c>""</c>.</summary>
        public string SndAttrs;

        /// <summary>Index-N <c>&lt;ammo&gt;</c> attribute run, or <c>""</c>.</summary>
        public string AmmoAttrs;

        /// <summary>Index-N <c>&lt;dop&gt;</c> attribute run, or <c>""</c>.</summary>
        public string DopAttrs;

        /// <summary>Index-N <c>&lt;com&gt;</c> attribute run, or <c>""</c>. In the shipped data this
        /// node repeats only to carry a bespoke <c>@price</c>.</summary>
        public string ComAttrs;

        /// <summary>True when this weapon actually has the variant (see <see cref="Variant"/>).</summary>
        public bool IsPresent => Variant > 0;

        /// <summary>
        /// The index-N run for one child kind, by its AllData name. <c>""</c> for an unknown kind rather
        /// than a throw: this is read in a loop over <see cref="PFE.Systems.Weapons.WeaponVariantXml.NodeKinds"/>,
        /// and an unrecognised kind must be visible as "no override", not as an exception inside an
        /// importer.
        /// </summary>
        public string AttrsFor(string nodeKind)
        {
            switch (nodeKind)
            {
                case "char": return CharAttrs ?? "";
                case "phis": return PhisAttrs ?? "";
                case "vis":  return VisAttrs  ?? "";
                case "snd":  return SndAttrs  ?? "";
                case "ammo": return AmmoAttrs ?? "";
                case "dop":  return DopAttrs  ?? "";
                case "com":  return ComAttrs  ?? "";
                default:     return "";
            }
        }
    }
}

namespace PFE.Systems.Weapons
{
    /// <summary>
    /// Reads the variant half of a <c>&lt;weapon&gt;</c> block, and merges an override over a base node.
    ///
    /// <para><b>Why this is here and not in <c>WeaponDataImporter</c>.</b> Same reason as the rest of this
    /// file: <c>PFE.Tests</c> does not reference <c>PFE.Editor</c>, so anything written inside the
    /// importer cannot be exercised by the offline wall — and the merge rule is exactly the kind of thing
    /// that goes silently wrong (a stat that should have been inherited reads 0). In the runtime assembly
    /// it is covered by <c>WeaponVariantXmlTests</c> with no editor and no Unity.</para>
    /// </summary>
    public static class WeaponVariantXml
    {
        /// <summary>
        /// The seven child kinds that repeat per variant, in the order AS3's <c>getXmlParam</c> reads
        /// them (<c>Weapon.as:454-568</c>: <c>com</c>, <c>vis</c>, <c>snd</c>, <c>phis</c>, <c>ammo</c>,
        /// <c>dop</c>, <c>char</c>). Order matters only for diagnostics; the merge is per-kind.
        /// </summary>
        public static readonly string[] NodeKinds = { "char", "phis", "vis", "snd", "ammo", "dop", "com" };

        /// <summary>
        /// The variant index the shipped data actually uses. <c>AllData.as</c> has <b>no</b> weapon with
        /// more than two <c>&lt;char&gt;</c> nodes, so 1 is the only tier that exists — but nothing here is
        /// hard-coded to it: <see cref="Read"/> takes the index.
        /// </summary>
        public const int FirstVariant = 1;

        /// <summary>
        /// Attribute names, per node kind, that the port's importer has a field for and reads. Used only
        /// to <b>report</b> what an override carries that the port would otherwise drop in silence — the
        /// failure mode recorded as constraint 12 in <c>TOPIC_workflow_constraints.md</c>
        /// (<c>Emitter</c> discards every attribute it has no property for, with no trace).
        ///
        /// <para><b>This list mirrors <c>WeaponDataImporter.ApplyWeaponData</c>; it is a lint target, not
        /// a source of truth.</b> A name added to the importer and not here reports a false "unread"; a
        /// name read here and not in the importer hides a real drop. <c>UnreadAttributeNames</c> on the
        /// shipped file currently returns exactly five names — see its remarks.</para>
        /// </summary>
        private static readonly Dictionary<string, string[]> PortReads = new Dictionary<string, string[]>
        {
            ["char"] = new[] { "maxhp", "damage", "rapid", "pow", "combo", "auto", "prec", "antiprec",
                               "knock", "destroy", "pier", "kol", "dkol", "expl", "damexpl", "expltip",
                               "explkol", "prep", "time", "radio", "sens", "tipdam", "crit" },
            ["phis"] = new[] { "speed", "deviation", "grav", "accel", "flame", "navod", "long", "minlong",
                               "bumc" },
            ["vis"]  = new[] { "vweap", "vbul", "visexpl", "spring", "bulanim", "shell", "phisbul",
                               "shine", "tipdec", "flare" },
            ["snd"]  = new[] { "shoot", "reload", "hit", "prep", "t1", "t2", "noise", "fall", "sens" },
            ["ammo"] = new[] { "holder", "reload", "magic", "mana", "rashod", "recharg" },
            ["dop"]  = new[] { "probiv", "effect", "damage", "ch" },
            ["com"]  = new[] { "price", "uniq", "chance", "stage", "rep", "worth" },
        };

        /// <summary>
        /// One attribute, matched anywhere in a run. <c>\s*=\s*'…'</c> tolerates the spacing AS3 authors
        /// use; the name is anchored at a word start so <c>expl</c> cannot match inside <c>damexpl</c> —
        /// the collision documented on <see cref="WeaponXmlAttrs"/>, avoided here by parsing the whole run
        /// into a map instead of searching for one name (constraint 11).
        /// </summary>
        private static readonly Regex AttrPair =
            new Regex(@"([A-Za-z_][A-Za-z0-9_\-]*)\s*=\s*'([^']*)'", RegexOptions.Singleline);

        /// <summary>
        /// Every <c>&lt;tag …&gt;</c> child attribute run in <paramref name="body"/>, in document order.
        /// Self-closing and open-tag forms both match; an open tag's run stops at its <c>&gt;</c>, so a
        /// nested element cannot leak into its parent's run.
        /// </summary>
        public static List<string> Runs(string body, string tag)
        {
            var runs = new List<string>();
            if (string.IsNullOrEmpty(body) || string.IsNullOrEmpty(tag)) return runs;

            foreach (Match m in Regex.Matches(body, "<" + tag + @"(\s[^>]*?)(?:/>|>)",
                                              RegexOptions.Singleline))
                runs.Add(m.Groups[1].Value);

            return runs;
        }

        /// <summary>The <paramref name="index"/>-th run, or <c>""</c> when that node does not repeat.</summary>
        private static string At(List<string> runs, int index)
            => runs != null && index >= 0 && index < runs.Count ? runs[index] : "";

        /// <summary>
        /// Read the <paramref name="variant"/> tier of a weapon body.
        ///
        /// <para><b>The existence test is <c>&lt;char&gt;</c>, and only <c>&lt;char&gt;</c></b> —
        /// <c>Stand.as:212</c> asks <c>weap.char.length() &gt; 1</c> and nothing else. Gating on
        /// <c>&lt;char&gt;</c> rather than on "any node repeats" is not cosmetic: it is what makes
        /// <see cref="WeaponVariantData.IsPresent"/> agree with the collection page and with
        /// <c>Weapon.updVariant</c>. Measured on the shipped file, no weapon repeats any other kind
        /// without also repeating <c>&lt;char&gt;</c>, so the two tests currently coincide — but the
        /// shipped file is not the rule.</para>
        ///
        /// <para>A body with no second <c>&lt;char&gt;</c> yields <c>Variant == 0</c> and empty runs,
        /// which is the correct answer for the other 142 weapons rather than an error.</para>
        /// </summary>
        public static WeaponVariantData Read(string body, int variant)
        {
            var data = new WeaponVariantData
            {
                Variant = 0,
                CharAttrs = "", PhisAttrs = "", VisAttrs = "", SndAttrs = "",
                AmmoAttrs = "", DopAttrs = "", ComAttrs = "",
            };

            if (string.IsNullOrEmpty(body) || variant <= 0) return data;

            var chars = Runs(body, "char");
            if (chars.Count <= variant) return data;

            data.Variant   = variant;
            data.CharAttrs = At(chars, variant);
            data.PhisAttrs = At(Runs(body, "phis"), variant);
            data.VisAttrs  = At(Runs(body, "vis"),  variant);
            data.SndAttrs  = At(Runs(body, "snd"),  variant);
            data.AmmoAttrs = At(Runs(body, "ammo"), variant);
            data.DopAttrs  = At(Runs(body, "dop"),  variant);
            data.ComAttrs  = At(Runs(body, "com"),  variant);
            return data;
        }

        /// <summary>
        /// Apply <paramref name="overrideRun"/> over <paramref name="baseRun"/> and return one run — the
        /// port of AS3 calling <c>getCharParam(node.char[0])</c> and then
        /// <c>getCharParam(node.char[variant])</c> on the same accumulated object
        /// (<c>Weapon.as:564-568</c>).
        ///
        /// <para><b>Presence-gated, in both directions.</b> An attribute the override does not carry keeps
        /// the base value (so <c>oldshot</c> keeps <c>kol=5</c>); an attribute it carries <i>replaces</i>
        /// it, <b>including when the value is <c>'0'</c></b> — <c>knock='0'</c> is a real override meaning
        /// "no knockback", not "inherit". Collapsing the two is the trap this whole type exists to avoid.</para>
        ///
        /// <para>The result always begins with a space and keeps base order first, appending any attribute
        /// the base lacked. That leading space is load-bearing: <see cref="WeaponXmlAttrs"/>'s boundary is
        /// <c>(?:^|\s)</c>, and every run the importer passes it starts with whitespace.</para>
        /// </summary>
        public static string MergeRuns(string baseRun, string overrideRun)
        {
            if (string.IsNullOrEmpty(overrideRun)) return baseRun ?? "";

            var order  = new List<string>();
            var values = new Dictionary<string, string>(System.StringComparer.Ordinal);

            Collect(baseRun, order, values);
            Collect(overrideRun, order, values);

            var sb = new System.Text.StringBuilder();
            foreach (string name in order)
                sb.Append(' ').Append(name).Append("='").Append(values[name]).Append('\'');

            return sb.ToString();
        }

        /// <summary>
        /// Fold one run into the accumulated name→value map. First occurrence fixes the position, last
        /// occurrence fixes the value — the same "later assignment wins" order AS3 gets from running its
        /// two <c>getXParam</c> calls in sequence.
        /// </summary>
        private static void Collect(string run, List<string> order, Dictionary<string, string> values)
        {
            if (string.IsNullOrEmpty(run)) return;

            foreach (Match m in AttrPair.Matches(run))
            {
                string name = m.Groups[1].Value;
                if (!values.ContainsKey(name)) order.Add(name);
                values[name] = m.Groups[2].Value;
            }
        }

        /// <summary>
        /// Attribute names carried by this override that the port has <b>no reader for</b>, as
        /// <c>"node.name"</c>, sorted. Empty when the override is fully understood.
        ///
        /// <para><b>Why this is reported rather than ignored.</b> An attribute with no matching field is
        /// dropped with no trace at all (constraint 12) — that is how a whole subsystem went missing
        /// before. Naming them turns a silent drop into a line in the import log.</para>
        ///
        /// <para><b>On the shipped <c>AllData.as</c> this returns exactly five names, all on
        /// <c>&lt;phis&gt;</c>:</b> <c>drot</c>, <c>drot2</c>, <c>m</c>, <c>massa</c> and <c>recoil</c>.
        /// Four of them (<c>drot</c>, <c>drot2</c>, <c>m</c>, <c>massa</c>) are attributes the port reads
        /// nowhere, variant or not — AS3's rotation/gravity variants and its <c>&lt;phis m&gt;</c> mass
        /// override, both already recorded as unimported on <c>WeaponDefinition</c>. <c>recoil</c> is the
        /// interesting one: <c>WeaponDefinition.recoilFrames</c> <i>exists</i> and the importer never
        /// fills it, so this list surfaces a pre-existing gap on the base tier too. That the list is
        /// <b>non-empty</b> is what makes this report provably not vacuous — a control, not a
        /// decoration.</para>
        /// </summary>
        public static List<string> UnreadAttributeNames(WeaponVariantData data)
        {
            var unread = new List<string>();
            if (!data.IsPresent) return unread;

            foreach (string kind in NodeKinds)
            {
                string run = data.AttrsFor(kind);
                if (string.IsNullOrEmpty(run)) continue;

                string[] known = PortReads[kind];
                foreach (Match m in AttrPair.Matches(run))
                {
                    string name = m.Groups[1].Value;
                    if (System.Array.IndexOf(known, name) < 0)
                        unread.Add(kind + "." + name);
                }
            }

            unread.Sort(System.StringComparer.Ordinal);
            return unread;
        }
    }

    /// <summary>
    /// One element lifted out of <c>AllData.as</c>'s <c>&lt;all&gt;</c> container, before any attribute is
    /// interpreted. The body is the text between the tags; a self-closing element has none.
    /// </summary>
    /// <remarks>
    /// Deliberately the same shape as <see cref="WeaponXmlBlock"/>, because it answers the same question
    /// for a different element. It exists as a separate type rather than a generalisation of that one
    /// because <c>WeaponXmlBlocks</c> is keyed on <c>&lt;weapon&gt;</c> by name and its own callers depend
    /// on that; widening it would put this reader's behaviour under a cache whose invalidation rule is
    /// about a different document.
    /// </remarks>
    public readonly struct AllDataElement
    {
        /// <summary>The element's tag name, e.g. <c>eff</c>. Empty only for a malformed scan.</summary>
        public readonly string Tag;

        /// <summary>The <c>id</c> attribute, or empty when the element declares none.</summary>
        public readonly string Id;

        /// <summary>Everything after the tag name up to the end of the opening tag.</summary>
        public readonly string Attrs;

        /// <summary>Text between the tags. Empty for a self-closing element.</summary>
        public readonly string Body;

        /// <summary>True for <c>&lt;eff …/&gt;</c> — no body, and no closing tag in the source.</summary>
        public readonly bool SelfClosing;

        public AllDataElement(string tag, string id, string attrs, string body, bool selfClosing)
        {
            Tag = tag ?? string.Empty;
            Id = id ?? string.Empty;
            Attrs = attrs ?? string.Empty;
            Body = body ?? string.Empty;
            SelfClosing = selfClosing;
        }
    }

    /// <summary>
    /// Reads the <b>direct children of <c>&lt;all&gt;</c></b> out of <c>AllData.as</c> — the port of
    /// <c>AllData.d.&lt;tag&gt;</c>.
    ///
    /// <para><b>Why "direct children" is the whole point and not a detail.</b> The Oracle indexes these
    /// lists, e.g. <c>AllData.d.eff.(@id == id)[0]</c> (<c>PipPage.as:265</c>), and <c>d.eff</c> is an
    /// <c>XMLList</c> of the <c>&lt;all&gt;</c> element's own <c>&lt;eff&gt;</c> children. An
    /// <c>&lt;eff&gt;</c> row may itself contain <c>&lt;eff&gt;</c> children — measured 2026-10-14: 118
    /// <c>&lt;eff&gt;</c> elements in the file, <b>72</b> at the top level and <b>46</b> nested inside
    /// another one (<c>burning</c> carries <c>&lt;eff id='crio' n1='+50%'/&gt;</c>). So a flat
    /// <c>&lt;eff id='crio'</c> regex can return a <i>grandchild</i> where the Oracle returns the
    /// top-level row, and the two carry different <c>n1</c> values. This reader tracks depth for exactly
    /// that reason.</para>
    ///
    /// <para><b>Why it lives in this file.</b> The same reason <c>SpellItemXml</c> does: this is the
    /// project's established home for "AllData.as readers that must stay offline-testable", because
    /// <c>PFE.Tests</c> does not reference <c>PFE.Editor</c> — an importer-side reader cannot be exercised
    /// by the offline wall at all. (A new file would also need a hand-added <c>&lt;Compile Include&gt;</c>
    /// in a generated csproj before anything could see it.)</para>
    /// </summary>
    public static class AllDataElements
    {
        private static readonly Regex ContainerPattern = new Regex(@"<all\b([^>]*)>", RegexOptions.Compiled);

        // ── Cached id lookup, per (source, tag) ──────────────────────────────────────────────────────
        //
        // Same reasoning as WeaponXmlBlocks' cache: the document is 360 KB and the caller repaints on
        // every hover. Keyed on REFERENCE identity of the source plus the tag name, so two equal-but-
        // distinct texts cannot be conflated. A handful of tags is all this ever sees.
        private static string _indexedSource;
        private static readonly Dictionary<string, Dictionary<string, AllDataElement>> Index =
            new Dictionary<string, Dictionary<string, AllDataElement>>(StringComparer.Ordinal);

        /// <summary>
        /// The top-level element of the given <paramref name="tag"/> with the given id, or <c>false</c>
        /// when the document has none. Only <c>&lt;all&gt;</c>'s own children are considered.
        /// </summary>
        public static bool TryGet(string allData, string tag, string id, out AllDataElement element)
        {
            element = default;
            if (string.IsNullOrEmpty(allData) || string.IsNullOrEmpty(tag) || string.IsNullOrEmpty(id))
                return false;

            Dictionary<string, AllDataElement> byId = IndexFor(allData, tag);
            return byId.TryGetValue(id, out element);
        }

        /// <summary>
        /// Every top-level element of the given tag, in file order. Never null. The first row wins for a
        /// duplicated id, matching <c>.(@id == id)[0]</c>.
        /// </summary>
        public static List<AllDataElement> TopLevel(string allData, string tag)
        {
            var result = new List<AllDataElement>();
            if (string.IsNullOrEmpty(allData) || string.IsNullOrEmpty(tag)) return result;

            if (!TryContainerBody(allData, out string body)) return result;

            Scan(body, tag, result);
            return result;
        }

        /// <summary>
        /// The elements of the given tag among <paramref name="body"/>'s <b>direct</b> children — the
        /// port of <c>dp.eff</c> / <c>dp.sk</c> / <c>dp.req</c> / <c>dp.textvar</c>.
        /// </summary>
        public static List<AllDataElement> Children(string body, string tag)
        {
            var result = new List<AllDataElement>();
            if (string.IsNullOrEmpty(body) || string.IsNullOrEmpty(tag)) return result;

            Scan(body, tag, result);
            return result;
        }

        /// <summary>The first direct child of the given tag, or false. <c>textvar[0]</c>'s shape.</summary>
        public static bool TryFirstChild(string body, string tag, out AllDataElement element)
        {
            List<AllDataElement> children = Children(body, tag);
            if (children.Count == 0)
            {
                element = default;
                return false;
            }

            element = children[0];
            return true;
        }

        private static Dictionary<string, AllDataElement> IndexFor(string allData, string tag)
        {
            if (!ReferenceEquals(_indexedSource, allData))
            {
                _indexedSource = allData;
                Index.Clear();
            }

            if (Index.TryGetValue(tag, out Dictionary<string, AllDataElement> byId)) return byId;

            byId = new Dictionary<string, AllDataElement>(StringComparer.Ordinal);
            foreach (AllDataElement element in TopLevel(allData, tag))
                if (!string.IsNullOrEmpty(element.Id) && !byId.ContainsKey(element.Id))
                    byId[element.Id] = element;

            Index[tag] = byId;
            return byId;
        }

        /// <summary>The text between <c>&lt;all&gt;</c> and its matching <c>&lt;/all&gt;</c>.</summary>
        private static bool TryContainerBody(string allData, out string body)
        {
            body = null;

            Match open = ContainerPattern.Match(allData);
            if (!open.Success) return false;

            int close = FindClose(allData, open.Index + open.Length, "all");
            if (close < 0) return false;

            body = allData.Substring(open.Index + open.Length, close - (open.Index + open.Length));
            return true;
        }

        /// <summary>
        /// Collect every <paramref name="tag"/> element that is a <b>direct child</b> of
        /// <paramref name="body"/>, skipping comments and never descending into a matched element.
        ///
        /// <para>Hand-written rather than a regex because the nesting is real (see the class note) and
        /// because XML comments may contain angle brackets — the file has 127 of them and one of them
        /// documents attribute meanings in prose.</para>
        /// </summary>
        private static void Scan(string body, string tag, List<AllDataElement> into)
        {
            int depth = 0;
            int i = 0;

            while (i < body.Length)
            {
                int lt = body.IndexOf('<', i);
                if (lt < 0) break;

                if (IsCommentStart(body, lt))
                {
                    int commentEnd = body.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                    i = commentEnd < 0 ? body.Length : commentEnd + 3;
                    continue;
                }

                int nameStart = body[lt + 1] == '/' ? lt + 2 : lt + 1;
                int nameEnd = nameStart;
                while (nameEnd < body.Length && IsNameChar(body[nameEnd])) nameEnd++;

                string name = body.Substring(nameStart, nameEnd - nameStart);
                int gt = body.IndexOf('>', nameEnd);
                if (gt < 0) break;

                if (body[lt + 1] == '/')
                {
                    depth--;
                    i = gt + 1;
                    continue;
                }

                bool selfClosing = gt > lt && body[gt - 1] == '/';

                if (selfClosing)
                {
                    if (depth == 0 && name == tag)
                        into.Add(new AllDataElement(name, IdOf(body, nameEnd, gt - 1),
                                                    body.Substring(nameEnd, gt - 1 - nameEnd), "", true));
                    i = gt + 1;
                    continue;
                }

                if (depth == 0 && name == tag)
                {
                    int closeLt = FindClose(body, gt + 1, name);
                    string inner = closeLt < 0
                        ? body.Substring(gt + 1)
                        : body.Substring(gt + 1, closeLt - (gt + 1));

                    string attrs = body.Substring(nameEnd, gt - nameEnd);
                    into.Add(new AllDataElement(name, WeaponXmlAttrs.Attr(attrs, "id"), attrs, inner, false));

                    if (closeLt < 0) { i = body.Length; continue; }
                    int closeGt = body.IndexOf('>', closeLt);
                    i = closeGt < 0 ? body.Length : closeGt + 1;
                    continue;
                }

                depth++;
                i = gt + 1;
            }
        }

        private static string IdOf(string body, int from, int to)
            => WeaponXmlAttrs.Attr(body.Substring(from, to - from), "id");

        private static bool IsCommentStart(string body, int lt)
            => lt + 3 < body.Length && body[lt + 1] == '!' && body[lt + 2] == '-' && body[lt + 3] == '-';

        private static bool IsNameChar(char c)
            => char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '-';

        /// <summary>
        /// The index of the <c>&lt;</c> that opens the closing tag matching the element whose body starts
        /// at <paramref name="from"/>, or <c>-1</c>. Counts nested same-name elements, so an
        /// <c>&lt;eff&gt;</c> inside an <c>&lt;eff&gt;</c> does not end the outer one early.
        /// </summary>
        private static int FindClose(string body, int from, string name)
        {
            int depth = 1;
            int i = from;

            while (i < body.Length)
            {
                int lt = body.IndexOf('<', i);
                if (lt < 0) return -1;

                if (IsCommentStart(body, lt))
                {
                    int commentEnd = body.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                    i = commentEnd < 0 ? body.Length : commentEnd + 3;
                    continue;
                }

                bool closing = lt + 1 < body.Length && body[lt + 1] == '/';
                int nameStart = closing ? lt + 2 : lt + 1;
                int nameEnd = nameStart;
                while (nameEnd < body.Length && IsNameChar(body[nameEnd])) nameEnd++;

                string found = body.Substring(nameStart, nameEnd - nameStart);
                int gt = body.IndexOf('>', nameEnd);
                if (gt < 0) return -1;

                bool selfClosing = gt > lt && body[gt - 1] == '/';

                if (found == name)
                {
                    if (closing)
                    {
                        depth--;
                        if (depth == 0) return lt;
                    }
                    else if (!selfClosing)
                    {
                        depth++;
                    }
                }

                i = gt + 1;
            }

            return -1;
        }
    }
}
