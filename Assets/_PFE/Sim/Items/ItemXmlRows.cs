using System.Collections.Generic;
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

        public ItemXmlRow(string id, string tip, string tip2, string attrs)
        {
            Id = id;
            Tip = tip;
            Tip2 = tip2;
            Attrs = attrs;
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
                rows.Add(new ItemXmlRow(
                    WeaponXmlAttrs.Attr(attrs, "id"),
                    WeaponXmlAttrs.Attr(attrs, "tip"),
                    WeaponXmlAttrs.Attr(attrs, "tip2"),
                    attrs));
            }

            return rows;
        }
    }
}
