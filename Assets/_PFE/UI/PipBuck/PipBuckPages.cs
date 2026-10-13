using System;
using System.Collections.Generic;

namespace PFE.UI.PipBuck
{
    /// <summary>
    /// The Oracle's page table — the port of the <c>pages</c> array literal at <c>PipBuck.as:111</c> —
    /// plus the captions its tabs show.
    ///
    /// <para><b>Why this is a separate type and not an enum.</b> Two callers need it and neither may
    /// own it: <see cref="PipBuckManager"/> needs the id-to-identifier mapping to label tabs, and
    /// <see cref="PipBuckView"/> needs it to say which page is missing. An enum would also have to
    /// carry the string, and the string is the Oracle's own constructor argument.</para>
    /// </summary>
    public static class PipBuckPages
    {
        /// <summary>
        /// Index is the page id; the value is the second constructor argument the Oracle passes to
        /// each page (<c>new PipPageInv(this, "inv")</c>). Slot 0 is unused because <c>pages[0]</c> is
        /// <c>null</c>.
        ///
        /// <para>This string is also the page's sub-tab label key prefix: <c>PipPage.as:148</c> writes
        /// <c>_loc3_.text.text = Res.pipText(this.pp + _loc4_)</c>, so the inventory's five sub-tabs
        /// look up <c>inv1</c>..<c>inv5</c>.</para>
        /// </summary>
        private static readonly string[] Identifiers =
        {
            string.Empty,
            "stat",   // 1  PipPageStat  — character sheet, skills, perks, effects, treatment
            "inv",    // 2  PipPageInv   — inventory
            "info",   // 3  PipPageInfo  — map, quests, travel
            "vend",   // 4  PipPageVend  — merchant (contextual)
            "opt",    // 5  PipPageOpt   — options / save / load
            "med",    // 6  PipPageMed   — doctor (contextual)
            "work",   // 7  PipPageWork  — workbench (contextual)
            "app",    // 8  PipPageApp   — appearance (contextual)
            "vault",  // 9  PipPageVault — container transfer (contextual)
        };

        /// <summary>
        /// The <b>fallback</b> captions, keyed by the Oracle's own string-table key — used only when
        /// no localisation document is loaded. The authoritative source is
        /// <see cref="TabLabelFor(int, PipTextTable)"/> against the shipped
        /// <c>text_en.xml</c>.
        ///
        /// <para><b>Why a fallback survives now that the document has been found.</b> The document is
        /// a <c>TextAsset</c> under <c>Resources/</c>; a build that strips it, or a checkout that
        /// predates it, would otherwise show six blank tabs. The fallback keeps the device readable
        /// and says so through <see cref="PipBuckBootstrapper"/>'s log.</para>
        ///
        /// <para><b>Where these strings came from.</b> They were read off the shipped game's own
        /// screen — the reference capture of the running Pip-Buck the port was asked to match —
        /// before the localisation file was known to exist. They are keyed by the Oracle's key so the
        /// two sources are comparable, and they agree with <c>text_en.xml</c> for every entry here
        /// <i>except</i> <c>mainclose</c>: the table gives <c>Close</c>, the screenshot gave
        /// <c>Close [TAB]</c>. The screenshot was right about the screen and wrong about the key —
        /// <c>mainclose</c> is written by the constructor (<c>PipBuck.as:110</c>) and by
        /// <c>updateLang</c> (<c>:150</c>), but <c>toNormalMode</c> (<c>:177</c>) overwrites it with
        /// <c>main0</c>, and <c>World.as:862</c> calls that unconditionally. So the visible caption is
        /// <c>main0</c>'s, which is what <see cref="LabelKeyFor"/> returns and what this table
        /// carries.</para>
        /// </summary>
        private static readonly Dictionary<string, string> Captions =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // PipBuck.as:110 — the constructor's write, and :177's in toNormalMode. Both are
                // the Close button; the port shows one caption, so both keys carry it.
                { "mainclose", "Close [TAB]" },
                { "main0",     "Close [TAB]" },

                { "main1", "Status" },       // pages[1] PipPageStat
                { "main2", "Inventory" },    // pages[2] PipPageInv
                { "main3", "Information" },  // pages[3] PipPageInfo

                // pages[4] PipPageVend. Formerly absent, because PipBuck.as:275 hides tab 4 unless a
                // contextual page owns it and :279 relabels it to that page's own caption — so its
                // default caption never reaches the screen and no capture of it existed. It is in the
                // table (`text_en.xml`, `<pip id="main4">` = "Trading"), so the gap is closed.
                { "main4", "Trading" },

                { "main5", "Main menu" },    // pages[5] PipPageOpt
            };

        /// <summary>The Oracle's identifier for a page id, or <c>"?"</c> for an unknown id.</summary>
        public static string IdentifierFor(int pageId)
        {
            if (pageId < 1 || pageId >= Identifiers.Length) return "?";
            return Identifiers[pageId];
        }

        /// <summary>
        /// The text a tab shows.
        ///
        /// <para>Resolved through <see cref="LabelKeyFor"/> against <see cref="Captions"/>. A page
        /// whose caption is not in the table falls back to the Oracle's own <b>page identifier</b>,
        /// upper-cased — Oracle data (the constructor argument at <c>PipBuck.as:111</c>), not an
        /// invented English word. The fallback exists so that a page added to the table without a
        /// caption shows something truthful rather than a blank tab.</para>
        ///
        /// <para><b>Prefer <see cref="TabLabelFor(int, PipTextTable)"/>.</b> This overload is the
        /// no-document path, kept so that a caller with no table (a fixture, a tool) still gets a
        /// caption instead of having to construct one.</para>
        /// </summary>
        public static string TabLabelFor(int pageId)
        {
            return TabLabelFor(pageId, null);
        }

        /// <summary>
        /// The text a tab shows, from the Oracle's own localisation document.
        ///
        /// <para><b>The Oracle's chain, in order.</b> <c>PipBuck.as:172</c> writes
        /// <c>butN.text.text = Res.pipText("main" + n)</c> for <c>but1..but5</c>, and <c>:110</c> /
        /// <c>:177</c> write <c>Res.pipText("mainclose")</c> / <c>Res.pipText("main0")</c> for
        /// <c>but0</c>. <c>Res.pipText(id)</c> is <c>Res.txt("p", id)</c> (<c>Res.as:144-147</c>),
        /// which resolves <c>d.pip.(@id == id).n</c> (<c>Res.as:70-71</c>) — the <c>pip</c> table,
        /// child <c>n</c>, <c>razd</c> 0. That is exactly the lookup performed here.</para>
        ///
        /// <para><b>What the document changes.</b> Six of the seven permanent captions are unchanged
        /// from the screenshot-derived fallback; <c>main4</c> was previously unobservable and now
        /// reads <c>Trading</c>, and the four contextual pages (6..9) now have captions at all —
        /// <c>Healing</c>, <c>Craft</c>, <c>Appearance</c>, <c>Storage</c> — instead of the upper-cased
        /// identifiers the fallback would have shown.</para>
        /// </summary>
        /// <param name="pageId">The Oracle's page id.</param>
        /// <param name="text">
        /// The localisation document, or null to use <see cref="Captions"/>. An empty table is
        /// treated as no table: it misses every key, so the fallback runs anyway.
        /// </param>
        public static string TabLabelFor(int pageId, PipTextTable text)
        {
            string key = LabelKeyFor(pageId);

            // Res.pipText(key) — the authoritative source.
            if (text != null && text.TryGet("p", key, 0, out string localised)) return localised;

            if (Captions.TryGetValue(key, out string caption)) return caption;

            string identifier = IdentifierFor(pageId);
            return identifier == "?" ? identifier : identifier.ToUpperInvariant();
        }

        /// <summary>
        /// The Oracle string-table key a tab's label comes from — the key
        /// <see cref="TabLabelFor(int, PipTextTable)"/> looks up in the <c>pip</c> table.
        ///
        /// <para><b>Page 0's key is <c>main0</c>, not <c>mainclose</c>.</b> The Oracle writes both to
        /// the same button — <c>mainclose</c> in the constructor (<c>PipBuck.as:110</c>) and in
        /// <c>updateLang</c> (<c>:150</c>), then <c>main0</c> in <c>toNormalMode</c> (<c>:177</c>),
        /// which <c>World.as:862</c> calls unconditionally during world construction. The last write
        /// is the one the player sees, so that is the key an importer should fetch.</para>
        /// </summary>
        public static string LabelKeyFor(int pageId)
        {
            return pageId == 0 ? "main0" : "main" + pageId;
        }

        /// <summary>Every page id the Oracle defines, in order.</summary>
        public static int[] AllPageIds()
        {
            var ids = new int[Identifiers.Length - 1];
            for (int i = 1; i < Identifiers.Length; i++) ids[i - 1] = i;
            return ids;
        }

        /// <summary>The highest page id, <c>PipPageVault</c>.</summary>
        public static int MaxPageId => Identifiers.Length - 1;
    }
}
