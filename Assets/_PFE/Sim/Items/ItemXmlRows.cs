using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using PFE.Systems.Weapons;

namespace PFE.Systems.Items
{
    /// <summary>
    /// One <c>&lt;item&gt;</c> row of <c>AllData.as</c> — its identity attributes, copied verbatim.
    ///
    /// <para><see cref="Tip"/> and <see cref="Tip2"/> are the <b>raw</b> AS3 strings, not the port's
    /// <c>ItemType</c> enum. That distinction is load-bearing: the loot tables are keyed on the raw
    /// strings, and <c>FixDataImport.GetItemTypeFromSource</c> collapses five of them
    /// (<c>compa</c>/<c>compm</c>/<c>compe</c>/<c>compp</c>/<c>compw</c>) into a single
    /// <c>ItemType.Component</c>. A consumer that only had the enum could not tell those five apart,
    /// which is exactly what <c>LootGen</c>'s pool keys need.</para>
    /// </summary>
    public readonly struct ItemXmlRow
    {
        /// <summary><c>@id</c>. Empty when the row carries none.</summary>
        public readonly string Id;

        /// <summary>
        /// <c>@tip</c> — the primary classification. Empty when absent (measured: none of the 500
        /// shipped rows is missing one).
        /// </summary>
        public readonly string Tip;

        /// <summary>
        /// <c>@tip2</c> — a <b>second</b> classification that opens a second loot pool
        /// (<c>LootGen.as:98-112</c>). Empty for the 442 of 500 rows that have none.
        /// </summary>
        public readonly string Tip2;

        /// <summary>
        /// The whole root attribute run — the text between <c>&lt;item</c> and its closing <c>&gt;</c>,
        /// with its leading whitespace intact so <see cref="WeaponXmlAttrs"/>'s <c>(?:^|\s)</c>
        /// boundary applies.
        /// </summary>
        public readonly string Attrs;

        /// <summary>
        /// The row's body — everything between the root tag's <c>&gt;</c> and its <c>&lt;/item&gt;</c>,
        /// verbatim. Empty for a self-closing row. Measured on the shipped file: <b>393</b> of the 500
        /// rows are self-closing, and the <b>107</b> with bodies are exactly the 32 carrying
        /// <c>&lt;sk&gt;</c> children plus the 75 <c>tip='scheme'</c> rows carrying their recipe's
        /// component list — no third shape.
        ///
        /// <para><b>Why the body is captured at all.</b> The <c>&lt;sk&gt;</c> rows are the
        /// <c>Pers.setInvParameters</c> payload that lets a HELD item rewrite the holder's stats
        /// (<c>screwdriver</c>'s <c>possLockPick</c> is the one the lockpicking gate lives and dies
        /// by). Those are the only multi-line rows that had no reader; without the body the importer
        /// could store identity attributes only and the lockpick would stay a dead keychain.</para>
        /// </summary>
        public readonly string Body;

        public ItemXmlRow(string id, string tip, string tip2, string attrs, string body = null)
        {
            Id = id;
            Tip = tip;
            Tip2 = tip2;
            Attrs = attrs;
            Body = body ?? string.Empty;
        }
    }

    /// <summary>
    /// Reads every <c>&lt;item&gt;</c> row out of <c>AllData.as</c>.
    ///
    /// <para><b>Why this exists rather than a regex at each call site.</b> Two importers previously
    /// carried their own item regex, so a fix to one did not reach the other. This is the single
    /// reader; the editor importer calls it and so does the offline fixture, which means the thing
    /// that is tested is the thing that runs.</para>
    ///
    /// <para><b>Attributes are read independently, never positionally.</b> The obvious
    /// <c>&lt;item\s+id='([^']+)'</c> shape assumes <c>id</c> comes first, and in the shipped file it
    /// does for only <b>456 of the 500</b> rows: the other <b>44</b> lead with <c>base=</c>
    /// (<c>&lt;item base='p9' id='p9' tip='a' …/&gt;</c>) and every one of them is an ammunition
    /// variant — the very rows <c>SimpleDataImporter</c> documents having had to rescue once already.
    /// A positional reader drops all 44 in silence, so the row match here captures the whole
    /// attribute run and each attribute is then searched for on its own.</para>
    ///
    /// <para><b>Single-quoted values only.</b> <see cref="WeaponXmlAttrs"/> matches
    /// <c>name='value'</c>. That is safe on this file because <b>zero</b> of the 500 <c>&lt;item&gt;</c>
    /// root tags contains a double-quoted attribute — a fact the fixture pins, so it cannot go stale
    /// without a test going red. The previous importer regex accepted both quote styles, so this is
    /// stricter by construction; the pinning test is what makes the strictness measured rather than
    /// assumed.</para>
    ///
    /// <para><b>No comment stripping, deliberately.</b> <c>&lt;armor&gt;</c> needed it —
    /// <c>ArmourDataParserTests</c> carries a control for a commented-out element. For
    /// <c>&lt;item&gt;</c> it is not needed: the file contains <b>0</b> block comments and <b>0</b>
    /// lines where <c>//</c> precedes an <c>&lt;item</c>, and the count of <c>&lt;item</c> occurrences
    /// equals the count of parsed rows, so nothing is hiding in a comment. Stripping would also risk
    /// eating an apostrophe in a display string, which is a worse failure than the one it prevents.</para>
    /// </summary>
    public static class ItemXmlRows
    {
        /// <summary>
        /// The whole root attribute run. <c>[^&gt;]</c> cannot cross the tag's own <c>&gt;</c>, so a
        /// row with a body stops at its root tag and never swallows a child element.
        ///
        /// <para><c>\b</c> rather than <c>\s+</c>: it forbids <c>&lt;itemtip=…</c> while still
        /// accepting a bare <c>&lt;item&gt;</c>. Every real row has whitespace after <c>item</c>.</para>
        /// </summary>
        private static readonly Regex Pattern = new Regex(@"<item\b([^>]*)>", RegexOptions.Singleline);

        /// <summary>
        /// Every <c>&lt;item&gt;</c> row in the source, in file order. Never null; an empty or null
        /// source yields an empty list. Rows are <b>not</b> filtered — a row with no <c>@tip</c> is
        /// still returned, so a caller that skips it does so visibly rather than by silent omission.
        /// </summary>
        public static List<ItemXmlRow> Parse(string allData)
        {
            var rows = new List<ItemXmlRow>();
            if (string.IsNullOrEmpty(allData)) return rows;

            foreach (Match m in Pattern.Matches(allData))
            {
                string attrs = m.Groups[1].Value;

                // Body capture. A self-closing root tag has no body of its own — and must NOT scan
                // forward, or it would swallow the next row's `</item>` together with that row's
                // `<sk>` children (exactly the mis-attribution a first draft of this reader made:
                // `p9`'s self-closing row claimed `screwdriver`'s lockpicking rows). Items do not
                // nest, so the first `</item>` after the root tag closes this row.
                string body = string.Empty;
                if (!attrs.TrimEnd().EndsWith("/"))
                {
                    int close = allData.IndexOf("</item>", m.Index + m.Length, StringComparison.Ordinal);
                    if (close >= 0)
                        body = allData.Substring(m.Index + m.Length, close - (m.Index + m.Length));
                }

                rows.Add(new ItemXmlRow(
                    WeaponXmlAttrs.Attr(attrs, "id"),
                    WeaponXmlAttrs.Attr(attrs, "tip"),
                    WeaponXmlAttrs.Attr(attrs, "tip2"),
                    attrs,
                    body));
            }

            return rows;
        }

        /// <summary>
        /// Parses the <c>&lt;sk …/&gt;</c> children out of a row body. Empty body → empty list.
        /// See <see cref="PersSkillRow"/> for the semantics being lifted.
        /// </summary>
        private static readonly Regex SkillRowPattern = new Regex(@"<sk\b([^>]*)>", RegexOptions.Singleline);

        public static List<PersSkillRow> ParseSkillRows(string body)
        {
            var rows = new List<PersSkillRow>();
            if (string.IsNullOrEmpty(body)) return rows;

            foreach (Match m in SkillRowPattern.Matches(body))
            {
                string attrs = m.Groups[1].Value;
                var row = new PersSkillRow
                {
                    Id = WeaponXmlAttrs.Attr(attrs, "id"),
                    Tip = WeaponXmlAttrs.Attr(attrs, "tip"),
                };

                string refAttr = WeaponXmlAttrs.Attr(attrs, "ref");
                if (refAttr == "add") row.Op = SkillRowOp.Add;
                else if (refAttr == "mult") row.Op = SkillRowOp.Mult;
                else row.Op = SkillRowOp.Set;

                float parsed;
                string v0 = WeaponXmlAttrs.Attr(attrs, "v0");
                if (!string.IsNullOrEmpty(v0) &&
                    float.TryParse(v0, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                {
                    row.V0 = parsed;
                    row.HasV0 = true;
                }

                string v1 = WeaponXmlAttrs.Attr(attrs, "v1");
                if (!string.IsNullOrEmpty(v1) &&
                    float.TryParse(v1, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                {
                    row.V1 = parsed;
                    row.HasV1 = true;
                }

                string vd = WeaponXmlAttrs.Attr(attrs, "vd");
                if (!string.IsNullOrEmpty(vd) &&
                    float.TryParse(vd, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                {
                    row.Vd = parsed;
                    row.HasVd = true;
                }

                rows.Add(row);
            }

            return rows;
        }
    }

    /// <summary>AS3 <c>&lt;sk @ref&gt;</c> — the three write modes, with <b>absent = Set</b>.</summary>
    public enum SkillRowOp
    {
        /// <summary>No <c>ref</c> (or anything else): <c>this[id] = v</c>. The dangerous default.</summary>
        Set = 0,
        /// <summary><c>ref='add'</c>: <c>this[id] += v</c>.</summary>
        Add = 1,
        /// <summary><c>ref='mult'</c>: <c>this[id] *= v</c>.</summary>
        Mult = 2,
    }

    /// <summary>
    /// One <c>&lt;sk&gt;</c> child of an <c>&lt;item&gt;</c> row — a stat write the holder gains by
    /// <b>carrying</b> the item. Lifted from <c>Pers.setSkillParam</c> (<c>Pers.as:1468</c>), which
    /// <c>Pers.setInvParameters</c> (<c>Pers.as:2290-2307</c>) calls with level <c>1</c> for every
    /// held item whose row has children.
    ///
    /// <para><b>Why this lives beside the reader and not in Data.</b> The row is produced by
    /// <see cref="ItemXmlRows.ParseSkillRows"/> (Sim) and consumed by <c>CharacterStats</c> (View) —
    /// both of which see it; <c>ItemDefinition</c> (Data) cannot, because <c>PFE.Sim</c> does not
    /// reference <c>PFE.Data</c> and the dependency must not be added for one struct. The definition
    /// therefore stores the row's verbatim <c>&lt;sk&gt;</c> run as a string
    /// (<c>skillRowsSource</c>, the same "keep the source payload raw" reasoning as
    /// <c>legacyTip</c>), and this struct is the parsed form that only the consuming side materialises.</para>
    ///
    /// <para><b>The value chain</b> (<c>Pers.as:1479-1495</c>): the base is <c>v0</c> when present,
    /// else <c>1</c> for <c>ref='mult'</c> and <c>0</c> otherwise; at level 0 the base is written
    /// as-is; with <c>vd</c> the value is <c>base + level·vd</c>; otherwise the row's own
    /// <c>v{level}</c> — for items always level 1, i.e. <c>v1</c> — and the oracle's final fallback
    /// re-reads <c>@v1</c> (absent → the base here, where AS3 would produce NaN; no shipped row
    /// reaches that branch: all 32 rows carry <c>v1</c> or <c>vd</c>).</para>
    ///
    /// <para><c>Tip</c> is kept verbatim rather than as a flag set, because the consumer
    /// (<c>ApplyNamedStat</c>) dispatches on the same strings the oracle does — <c>"weap"</c>,
    /// <c>"res"</c>, <c>"m"</c> — and a port-side enum would be a second vocabulary to keep in step.
    /// Measured on the shipped file (32 items carrying <b>53</b> <c>&lt;sk&gt;</c> rows):
    /// <c>tip</c> is <c>'m'</c> (bag1/bag2), <c>'res'</c> (blackbook), or absent; <c>ref</c> is
    /// <c>'add'</c> (116 of the file's sk rows carry it), <c>'mult'</c> (the twelve ×0.9/×1.1
    /// rows — mod_holo, stat_fl/rr/pp/rd, amul_al, the four talismans), or absent; every row has
    /// <c>v1</c> or <c>vd</c>; none carries <c>dop</c> (the attribute that would redirect the level
    /// argument, Pers.as:1476-1483 — dead for items, whose level argument is always 1).</para>
    /// </summary>
    public struct PersSkillRow
    {
        /// <summary><c>@id</c> — the stat id written. Empty on a malformed row; the consumer skips it.</summary>
        public string Id;

        /// <summary><c>@tip</c>, verbatim. Empty for the plain stat rows.</summary>
        public string Tip;

        /// <summary>The write mode — <see cref="SkillRowOp.Set"/> when <c>@ref</c> is absent.</summary>
        public SkillRowOp Op;

        public float V0;
        public bool HasV0;
        public float V1;
        public bool HasV1;
        public float Vd;
        public bool HasVd;

        /// <summary>
        /// The value <c>Pers.setSkillParam</c> writes at <paramref name="level"/>. Items are always
        /// read at level 1 (<c>setSkillParam(xml, 1)</c>, Pers.as:2298/2307); level 0 exists because
        /// the oracle's <c>_loc6_ == 0</c> branch is how a removal pass zeroes a row.
        /// </summary>
        public float ValueFor(int level)
        {
            float baseValue = HasV0 ? V0 : (Op == SkillRowOp.Mult ? 1f : 0f);
            if (level <= 0) return baseValue;
            if (HasVd) return baseValue + level * Vd;
            return HasV1 ? V1 : baseValue;
        }
    }
}
