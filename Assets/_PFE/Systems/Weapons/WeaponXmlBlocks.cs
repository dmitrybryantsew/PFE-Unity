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
        /// </summary>
        public static float AttrF(string src, string name, float fallback = 0f)
        {
            var s = Attr(src, name);
            return string.IsNullOrEmpty(s) ? fallback : float.Parse(s, CultureInfo.InvariantCulture);
        }

        /// <summary>Attribute as an int, or <paramref name="fallback"/> when absent.</summary>
        public static int AttrI(string src, string name, int fallback = 0)
        {
            var s = Attr(src, name);
            return string.IsNullOrEmpty(s) ? fallback : int.Parse(s, CultureInfo.InvariantCulture);
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
