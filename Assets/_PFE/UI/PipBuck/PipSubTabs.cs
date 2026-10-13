using System;
using System.Collections.Generic;

namespace PFE.UI.PipBuck
{
    /// <summary>
    /// The contextual inputs the Oracle's sub-tab rules branch on — one field per variable those
    /// branches actually read, and nothing else.
    ///
    /// <para><b>Why a struct and not six more parameters.</b> Four of the nine pages have a sub-tab
    /// whose visibility depends on <i>who opened the device</i> rather than on the page. Threading
    /// that through as loose arguments makes every call site read
    /// <c>IsVisible(page, sub, "", "", "", false)</c>, which is unreadable and, worse, silently
    /// wrong the moment a fifth variable appears. The struct also lets a caller write
    /// <see cref="Default"/> and say "I have no context" instead of guessing at empty strings.</para>
    ///
    /// <para><b>Every field is empty in the shipped port.</b> The four contextual pages (4, 6, 7, 9)
    /// are opened by world interactions that do not exist yet, so nothing populates these. They are
    /// modelled anyway because the rules are where the Oracle's branches are, and a branch that is
    /// not written down is a branch that gets re-derived wrongly later.</para>
    /// </summary>
    public readonly struct PipSubTabContext
    {
        public PipSubTabContext(string npcId, string npcInter, string workTip, bool light)
        {
            NpcId = npcId ?? string.Empty;
            NpcInter = npcInter ?? string.Empty;
            WorkTip = workTip ?? string.Empty;
            Light = light;
        }

        /// <summary>
        /// <c>PipBuck.npcId</c> — which NPC owns the open vendor page. Empty means "no NPC", and
        /// <c>PipPageVend.as:89-93</c> then refuses to show the quest sub-tab at all.
        /// </summary>
        public string NpcId { get; }

        /// <summary>
        /// <c>PipBuck.npcInter</c> — the <i>kind</i> of interaction that opened the page:
        /// <c>"v"</c> a plain vending machine, <c>"vr"</c> a vendor who repairs, <c>"doc"</c> a
        /// doctor who also trades, <c>"adoc"</c>/<c>"vdoc"</c> a doctor paid in gel or in goods.
        /// </summary>
        public string NpcInter { get; }

        /// <summary>
        /// <c>PipBuck.workTip</c> — which kind of workbench is open: <c>"mworkbench"</c>,
        /// <c>"stove"</c>, <c>"lab"</c>, <c>"expl"</c>. See <c>PipPageWork.as:44-58</c>.
        /// </summary>
        public string WorkTip { get; }

        /// <summary>
        /// <c>PipBuck.light</c> — the shell is hidden and only page content draws. It changes which
        /// options sub-tabs exist (<c>PipPageOpt.as:500-513</c>), so it is a sub-tab input too.
        /// </summary>
        public bool Light { get; }

        /// <summary>No context at all — the state every page in the shipped port is in.</summary>
        public static PipSubTabContext Default => new PipSubTabContext(null, null, null, false);
    }

    /// <summary>
    /// The per-tick values behind the Oracle's sub-tab <b>sign</b> markers.
    ///
    /// <para><b>What a sign is.</b> <c>PipPage.setButtons</c> (<c>PipPage.as:894-915</c>) draws the
    /// current sub-tab at frame 2 and a signed one at <c>signs[n] + 2</c>. So a sub-tab button can
    /// carry a marker without being the current one — the character sheet puts one on
    /// <b>Skills</b> when a skill point is unspent, on <b>Perks</b> when a perk point is, and a
    /// two- or three-level one on <b>Health</b> as the player's limbs degrade.</para>
    ///
    /// <para><b>Why the flags are explicit rather than zero-valued.</b> "No player yet" and "the
    /// player is at zero health" are both <c>0</c> as a fraction, and the Health rule fires at
    /// <c>&lt; 0.25</c> — so a struct that only carried the number would light a red Health marker on
    /// every page before the player exists. <see cref="HasVitals"/> is what separates them.</para>
    /// </summary>
    public readonly struct PipSubTabSignals
    {
        public PipSubTabSignals(int skillPoints, int perkPoints, bool hasVitals,
            float lowestVitalHpFraction, bool vendorHasReportableQuest)
        {
            SkillPoints = skillPoints;
            PerkPoints = perkPoints;
            HasVitals = hasVitals;
            LowestVitalHpFraction = lowestVitalHpFraction;
            VendorHasReportableQuest = vendorHasReportableQuest;
        }

        /// <summary><c>Pers.skillPoint</c> — unspent skill points.</summary>
        public int SkillPoints { get; }

        /// <summary><c>Pers.perkPoint</c> — unspent perk points.</summary>
        public int PerkPoints { get; }

        /// <summary>False when there is no player to read; see the struct summary.</summary>
        public bool HasVitals { get; }

        /// <summary>
        /// The lowest of head / torso / legs / blood, each over <c>inMaxHP</c> — the four terms the
        /// Oracle's Health rule ORs together (<c>PipPageStat.as:465-470</c>).
        /// </summary>
        public float LowestVitalHpFraction { get; }

        /// <summary>
        /// Whether the open vendor has a quest it can report or hand over
        /// (<c>PipPageVend.as:439-465</c>). False also covers "there is no vendor", which is the
        /// Oracle's own early return.
        /// </summary>
        public bool VendorHasReportableQuest { get; }

        /// <summary>Nothing to report — no marker is drawn on any sub-tab.</summary>
        public static PipSubTabSignals None => new PipSubTabSignals(0, 0, false, 0f, false);
    }

    /// <summary>
    /// The Oracle's sub-tab table: which sub-tabs each page has, what they are called, when they are
    /// visible, which one a page is forced onto, and what a sub-tab click does.
    ///
    /// <para><b>Where the sub-tabs come from.</b> They are not per-page inventions. Every page
    /// subclass extends <c>PipPage</c>, whose constructor builds the same five buttons in a loop and
    /// labels them from the page's own identifier (<c>PipPage.as:143-152</c>):</para>
    /// <code>
    /// _loc3_.text.text = Res.pipText(this.pp + _loc4_);   // pp = "stat", "inv", … _loc4_ = 1..5
    /// </code>
    /// <para>So <c>pp</c> — the second constructor argument at <c>PipBuck.as:111</c>, already carried
    /// by <see cref="PipBuckPages.IdentifierFor"/> — <i>is</i> the label key prefix. The only thing
    /// each subclass changes is which of the five are <c>visible</c>, and the only thing that changes
    /// at runtime is which is current.</para>
    ///
    /// <para><b>A second, independent source agrees with the ActionScript.</b> The shipped
    /// <c>text_en.xml</c> contains <b>exactly</b> the sub-tab captions that can ever be shown, and
    /// none of the ones the constructors hide: <c>vend5</c>, <c>med3</c>, <c>med4</c>, <c>med5</c>,
    /// <c>work4</c>, <c>work5</c>, <c>app1</c>..<c>app5</c>, <c>vault4</c>, <c>vault5</c> are all
    /// absent, and every other <c>&lt;prefix&gt;&lt;n&gt;</c> is present. That is the localisation
    /// document's author making the same statement the code makes, from the other side —
    /// <c>PipSubTabsTests</c> pins both halves so neither can drift alone.</para>
    ///
    /// <para><b>Unity-free on purpose</b>, like <see cref="PipInventoryList"/>: everything here is a
    /// function of its arguments, so it runs against the shipped assembly from a plain shell. That is
    /// what makes the visibility branches testable at all — they are exactly the sort of rule a
    /// compile cannot check.</para>
    /// </summary>
    public static class PipSubTabs
    {
        // ── The Oracle's page ids ─────────────────────────────────────────────
        //
        // Named here rather than reached for through PipBuckPages so this file has no view
        // dependency. PipBuckManager declares the same table (PipBuck.as:111).

        private const int StatPageId = 1;
        private const int InvPageId = 2;
        private const int InfoPageId = 3;
        private const int VendPageId = 4;
        private const int OptPageId = 5;
        private const int MedPageId = 6;
        private const int WorkPageId = 7;
        private const int AppPageId = 8;
        private const int VaultPageId = 9;

        /// <summary>
        /// How many sub-tab buttons a page has. <c>PipPage.as:143-152</c> builds exactly five
        /// (<c>while (_loc4_ &lt;= 5)</c>) for every page, including the ones that hide some — a hidden
        /// button is still a button, and still consumes its slot.
        /// </summary>
        public const int MaxSubTabs = 5;

        /// <summary>Frame 1 of a sub-tab button — the resting state. <c>PipPage.as:911</c>.</summary>
        public const int NormalFrame = 1;

        /// <summary>Frame 2 — the current sub-tab. <c>PipPage.as:901</c>.</summary>
        public const int ActiveFrame = 2;

        // ── Labels ────────────────────────────────────────────────────────────

        /// <summary>
        /// The Oracle string-table key a sub-tab's caption comes from — <c>Res.pipText(pp + n)</c>,
        /// i.e. the page identifier with the sub-page number appended (<c>PipPage.as:148</c>).
        /// </summary>
        /// <returns>
        /// The key, or <c>"?"</c> when the page or sub-page is outside the table. <c>"?"</c> is
        /// deliberately not a key that can collide with a real one.
        /// </returns>
        public static string LabelKey(int pageId, int subPage)
        {
            if (subPage < 1 || subPage > MaxSubTabs) return "?";
            string prefix = PipBuckPages.IdentifierFor(pageId);
            return prefix == "?" ? "?" : prefix + subPage;
        }

        /// <summary>
        /// The caption a sub-tab shows, from the Oracle's own localisation document.
        ///
        /// <para><b>The fallback is a real table, not a placeholder.</b> With no document loaded this
        /// returns the port's own English from <see cref="FallbackCaptions"/> — the same arrangement
        /// <see cref="PipBuckPages.TabLabelFor(int, PipTextTable)"/> uses for the main tabs, and for
        /// the same reason: a checkout without the document should still show a readable device.
        /// <c>PipSubTabsTests</c> asserts that every entry in that table is <b>identical</b> to the
        /// shipped document's, so it can never quietly become a different word.</para>
        /// </summary>
        public static string Label(int pageId, int subPage, PipTextTable text)
        {
            string key = LabelKey(pageId, subPage);
            if (key == "?") return key;

            // Res.pipText(key) — the authoritative source.
            if (text != null && text.TryGet("p", key, 0, out string localised)) return localised;

            if (FallbackCaptions.TryGetValue(key, out string caption)) return caption;

            // A key with no caption in either source. The Oracle's own answer to a missing key is the
            // marker `*pip_<id>` (Res.as:120-130) — which says exactly what is wrong, instead of an
            // invented word that would read as a translation.
            return "*pip_" + key;
        }

        /// <summary>
        /// The port's own English for the sub-tabs that can be shown, keyed by the Oracle's own key.
        ///
        /// <para><b>Only the showable ones.</b> There is deliberately no entry for <c>vend5</c>,
        /// <c>med3</c>..<c>med5</c>, <c>work4</c>/<c>work5</c>, <c>app1</c>..<c>app5</c> or
        /// <c>vault4</c>/<c>vault5</c> — those buttons are hidden by their page's constructor and the
        /// document has no caption for them either, so inventing one here would create a word the
        /// game never shows. The test that walks the table asserts both halves.</para>
        /// </summary>
        private static readonly Dictionary<string, string> FallbackCaptions =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // PipPageStat — character sheet. All five are always visible.
                { "stat1", "Main" },
                { "stat2", "Skills" },
                { "stat3", "Perks" },
                { "stat4", "Effects" },
                { "stat5", "Health" },

                // PipPageInv — inventory. All five are always visible.
                { "inv1", "Weapons" },
                { "inv2", "Armor" },
                { "inv3", "Equipment" },
                { "inv4", "Other" },
                { "inv5", "Ammunition" },

                // PipPageInfo — map / quests / world / notes / enemies. All five visible.
                { "info1", "Map" },
                { "info2", "Quests" },
                { "info3", "World" },
                { "info4", "Notes" },
                { "info5", "Enemies" },

                // PipPageVend — `vend4` exists but is only shown for a real NPC (see IsVisible).
                { "vend1", "Buy" },
                { "vend2", "Sell" },
                { "vend3", "Repair" },
                { "vend4", "Quests" },

                // PipPageOpt — `opt3` is where light mode parks the page.
                { "opt1", "Load" },
                { "opt2", "Save" },
                { "opt3", "Options" },
                { "opt4", "Control" },
                { "opt5", "Log" },

                // PipPageMed — a doctor sells healing and trades.
                { "med1", "Healing" },
                { "med2", "Trading" },

                // PipPageWork — create / enhance / repair.
                { "work1", "Create" },
                { "work2", "Enhance" },
                { "work3", "Repair" },

                // PipPageVault — container transfer.
                { "vault1", "Equipment" },
                { "vault2", "Ammunition" },
                { "vault3", "Stuff" },
            };

        // ── Visibility ────────────────────────────────────────────────────────

        /// <summary>
        /// Whether sub-tab <paramref name="subPage"/> of <paramref name="pageId"/> is on screen.
        ///
        /// <para><b>Read straight off each page's constructor and <c>setSubPages</c>.</b> The base
        /// constructor makes all five visible; a subclass hides some, and the four contextual pages
        /// hide more depending on what opened them. There is no other source of truth — the Oracle
        /// never re-shows a button a constructor hid, because <c>setSubPages</c> is only ever called
        /// on the page that is already current.</para>
        /// </summary>
        public static bool IsVisible(int pageId, int subPage, in PipSubTabContext context)
        {
            if (subPage < 1 || subPage > MaxSubTabs) return false;

            switch (pageId)
            {
                // PipPage.as:143-152 — the base constructor builds all five and hides none. These
                // three pages never override that.
                case StatPageId:
                case InvPageId:
                case InfoPageId:
                    return true;

                case VendPageId:
                    // PipPageVend.as:40 — the constructor hides but5. (There is no `vend5` caption in
                    // the document either: the same statement from the data side.)
                    if (subPage == 5) return false;

                    // PipPageVend.as:89-93 — no NPC, no quest log. The page also moves itself off
                    // sub-page 4 in that case; see Coerce.
                    if (subPage == 4 && context.NpcId.Length == 0) return false;

                    // PipPageVend.as:103-107 — a plain vending machine has nothing to repair.
                    if (subPage == 3 && context.NpcInter == "v") return false;

                    return true;

                case OptPageId:
                    // PipPageOpt.as:500-513 — light mode keeps only Options and Control; Load, Save
                    // and Log all need a shell to draw them in.
                    if (context.Light && (subPage == 1 || subPage == 2 || subPage == 5)) return false;
                    return true;

                case MedPageId:
                    // PipPageMed.as:41 — the constructor hides but3, but4 and but5.
                    if (subPage >= 3) return false;

                    // PipPageMed.as:48-57 — a doctor paid in gel (`adoc`) or in goods (`vdoc`) has no
                    // caps price list, so the Trading tab goes away with it.
                    if (subPage == 2 && (context.NpcInter == "adoc" || context.NpcInter == "vdoc"))
                    {
                        return false;
                    }

                    return true;

                case WorkPageId:
                    // PipPageWork.as:25 — the constructor hides but4 and but5.
                    if (subPage >= 4) return false;

                    // PipPageWork.as:48-58 — all three are re-shown, then narrowed by the tool. A
                    // workbench only repairs; a stove, a lab and an explosive bench only create.
                    if (context.WorkTip == "mworkbench") return subPage == 3;

                    if (context.WorkTip == "stove" || context.WorkTip == "lab" ||
                        context.WorkTip == "expl")
                    {
                        return subPage == 1;
                    }

                    return true;

                case AppPageId:
                    // PipPageApp.as:12 — all five hidden. The page *is* the appearance customiser;
                    // it has no sub-pages at all.
                    return false;

                case VaultPageId:
                    // PipPageVault.as:24 — the constructor hides but4 and but5.
                    return subPage <= 3;

                default:
                    return false;
            }
        }

        /// <summary>The sub-tabs of a page that are visible, ascending. Used by the bar and by tests.</summary>
        public static int[] VisibleSubPages(int pageId, in PipSubTabContext context)
        {
            var visible = new List<int>(MaxSubTabs);
            for (int subPage = 1; subPage <= MaxSubTabs; subPage++)
            {
                if (IsVisible(pageId, subPage, context)) visible.Add(subPage);
            }

            return visible.ToArray();
        }

        /// <summary>How many of a page's sub-tabs are visible.</summary>
        public static int VisibleCount(int pageId, in PipSubTabContext context)
        {
            int count = 0;
            for (int subPage = 1; subPage <= MaxSubTabs; subPage++)
            {
                if (IsVisible(pageId, subPage, context)) count++;
            }

            return count;
        }

        // ── Which sub-page the page ends up on ────────────────────────────────

        /// <summary>
        /// The sub-page a page is actually on, given the one that was asked for.
        ///
        /// <para><b>These are not fallbacks — they are assignments the Oracle makes.</b> Three pages
        /// overwrite <c>page2</c> outright while their <c>setSubPages</c> runs:
        /// <c>PipPageVend.as:89-91</c> (no NPC ⇒ back to Buy), <c>PipPageWork.as:51-58</c> (the tool
        /// decides the sub-page: a workbench repairs, a stove/lab/explosive bench creates), and
        /// <c>PipPageOpt.as:502-507</c> (light mode has no Load/Save, so the page moves to Options).
        /// Because the write is unconditional once the branch is taken, the requested value is
        /// genuinely discarded — which is why this returns a value rather than a bool.</para>
        ///
        /// <para><b>There is deliberately no generic "fall back to the first visible one" rule.</b>
        /// The Oracle does not have one. Inventing it here would make the port disagree with the game
        /// in exactly the case that is hardest to notice: a page parked on a hidden sub-tab, where
        /// both the Oracle and the port draw no lit button, but only the port would move.</para>
        /// </summary>
        public static int Coerce(int pageId, int requested, in PipSubTabContext context)
        {
            // PipPage.as:53 — `internal var page2:int = 1;`. Anything outside 1..5 is not a
            // sub-page, so it lands on the page's own default.
            if (requested < 1 || requested > MaxSubTabs) return 1;

            switch (pageId)
            {
                case VendPageId:
                    if (requested == 4 && context.NpcId.Length == 0) return 1;
                    return requested;

                case WorkPageId:
                    if (context.WorkTip == "mworkbench") return 3;
                    if (context.WorkTip == "stove" || context.WorkTip == "lab" ||
                        context.WorkTip == "expl")
                    {
                        return 1;
                    }

                    return requested;

                case OptPageId:
                    if (context.Light && (requested == 1 || requested == 2)) return 3;
                    return requested;

                default:
                    return requested;
            }
        }

        /// <summary>The sub-page a page opens on — <c>Coerce</c> against the Oracle's own default of 1.</summary>
        public static int DefaultSubPage(int pageId, in PipSubTabContext context)
        {
            return Coerce(pageId, 1, context);
        }

        // ── Cross-page sub-tabs ───────────────────────────────────────────────

        /// <summary>
        /// The page a sub-tab click actually opens, or <b>0</b> when the click stays on this page.
        ///
        /// <para><b>Two sub-tabs are not sub-tabs.</b> A doctor's "Trading" is the merchant page, and
        /// a doc-vendor's "Repair" is the doctor page — so clicking either leaves the current page
        /// entirely. <c>PipPageMed.as:247-252</c> sets <c>page2 = 1</c> and calls
        /// <c>pip.onoff(4)</c>; <c>PipPageVend.as:478-481</c> sets <c>page2 = 1</c> and calls
        /// <c>pip.onoff(6)</c>. Note the reset first: the page it returns to must not be left parked
        /// on the tab that navigated away.</para>
        ///
        /// <para><b>Recorded, not yet wired.</b> No port path opens page 4 or page 6 — both are
        /// stubs reached only from world interactions that do not exist — so
        /// <see cref="PipBuckManager.SubPageClick"/> does not consult this. The rule is written down
        /// and tested anyway, because a reader who finds it here will not re-derive it wrongly when
        /// the merchant page lands.</para>
        /// </summary>
        public static int NavigatesToPage(int pageId, int subPage, in PipSubTabContext context)
        {
            if (pageId == MedPageId && subPage == 2) return VendPageId;
            if (pageId == VendPageId && subPage == 3 && context.NpcInter == "doc") return MedPageId;
            return 0;
        }

        // ── Frame selection ───────────────────────────────────────────────────

        /// <summary>
        /// Which frame a sub-tab button draws — the port of <c>PipPage.setButtons</c>
        /// (<c>PipPage.as:894-915</c>).
        ///
        /// <para>The order matters and is the Oracle's: the current sub-tab wins over its own sign.
        /// A player standing on Health with a bleeding limb sees the plain active frame, not the
        /// warning marker — the marker is there to draw them <i>to</i> a tab they are not on.</para>
        /// </summary>
        /// <param name="subPage">The button being drawn, 1..5.</param>
        /// <param name="currentSubPage">The page's <c>page2</c>.</param>
        /// <param name="sign">The value from <see cref="SignFor"/>; 0 means no marker.</param>
        public static int FrameFor(int subPage, int currentSubPage, int sign)
        {
            if (subPage == currentSubPage) return ActiveFrame;
            if (sign > 0) return sign + 2;
            return NormalFrame;
        }

        /// <summary>
        /// The sign marker a sub-tab carries, or 0 — the port of the <c>setSigns</c> overrides.
        ///
        /// <para>Only two pages ever sign a sub-tab. <c>PipPageStat.as:453-471</c> marks
        /// <b>Skills</b> when a skill point is unspent, <b>Perks</b> when a perk point is, and
        /// <b>Health</b> at 2 or 3 as the player's lowest vital drops below half or below a quarter
        /// of maximum. <c>PipPageVend.as:439-465</c> marks <b>Quests</b> when the open vendor has a
        /// quest to report or to hand over. Every other page leaves all five at 0.</para>
        /// </summary>
        public static int SignFor(int pageId, int subPage, in PipSubTabSignals signals)
        {
            if (pageId == StatPageId)
            {
                if (subPage == 2 && signals.SkillPoints > 0) return 1;
                if (subPage == 3 && signals.PerkPoints > 0) return 1;

                if (subPage == 5 && signals.HasVitals)
                {
                    // `headHP / inMaxHP < 0.25 || torsHP … || legsHP … || bloodHP …` — four ORs over
                    // the same threshold, so the lowest of the four decides.
                    if (signals.LowestVitalHpFraction < 0.25f) return 3;
                    if (signals.LowestVitalHpFraction < 0.5f) return 2;
                }

                return 0;
            }

            if (pageId == VendPageId && subPage == 4 && signals.VendorHasReportableQuest) return 1;

            return 0;
        }
    }
}
