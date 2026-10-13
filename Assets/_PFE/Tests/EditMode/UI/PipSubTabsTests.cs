using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using PFE.Tests.Editor.Common;
using PFE.UI.PipBuck;

namespace PFE.Tests.EditMode.UI
{
    /// <summary>
    /// Pins <see cref="PipSubTabs"/> — the Oracle's sub-tab table.
    ///
    /// <para><b>Why this exists at all.</b> The sub-tab row is the one part of a PipBuck page that is
    /// knowable before the page is written: the Oracle derives it from the page's own identifier
    /// (<c>PipPage.as:143-152</c>), and the only per-page variation is which of the five are visible.
    /// That makes it exactly the sort of thing that gets re-derived — wrongly — by each of the nine
    /// page work items, because none of them can see the others' source. So the rules are pinned
    /// here, once, in a Unity-free type that runs offline.</para>
    ///
    /// <para><b>Two halves, and the second is the one that matters.</b> The first half runs against
    /// no document and pins the <i>rules</i>: label keys, visibility, coercion, sign frames, the two
    /// cross-page sub-tabs. The second half runs against the <b>shipped <c>text_en.xml</c></b> and
    /// pins the <i>data</i> — including the cross-check that makes the whole thing trustworthy: the
    /// document carries a caption for <b>exactly</b> the sub-tabs the code can ever show, and none of
    /// the ones it can never show. That is the localisation author making the same statement the
    /// ActionScript makes, from the other side, and it is the reason a rule error here cannot pass
    /// unnoticed.</para>
    ///
    /// <para><b>The file is read from disk, not from a <c>TextAsset</c>.</b> <c>Resources.Load</c> is
    /// a Unity ECall and throws when this assembly runs outside the editor, which is how the offline
    /// harness executes it. <c>SourceLint.ProjectRoot</c> already solves that for the lints.</para>
    /// </summary>
    [TestFixture]
    public class PipSubTabsTests
    {
        // ── The Oracle's page ids (PipBuck.as:111) ────────────────────────────

        private const int Stat = 1;
        private const int Inv = 2;
        private const int Info = 3;
        private const int Vend = 4;
        private const int Opt = 5;
        private const int Med = 6;
        private const int Work = 7;
        private const int App = 8;
        private const int Vault = 9;

        // ── Label keys ────────────────────────────────────────────────────────

        [Test]
        public void MaxSubTabs_IsTheOraclesFive()
        {
            // PipPage.as:143-152 — `_loc4_ = 1; while (_loc4_ <= 5)`. Every page gets five buttons,
            // including the ones that hide some: a hidden button still consumes its slot.
            Assert.That(PipSubTabs.MaxSubTabs, Is.EqualTo(5));
        }

        [Test]
        public void LabelKey_IsThePageIdentifierPlusTheSubPage()
        {
            // PipPage.as:148 — `_loc3_.text.text = Res.pipText(this.pp + _loc4_)`, where `pp` is the
            // second constructor argument at PipBuck.as:111.
            Assert.That(PipSubTabs.LabelKey(Stat, 1), Is.EqualTo("stat1"));
            Assert.That(PipSubTabs.LabelKey(Inv, 5), Is.EqualTo("inv5"));
            Assert.That(PipSubTabs.LabelKey(Info, 3), Is.EqualTo("info3"));
            Assert.That(PipSubTabs.LabelKey(Vend, 4), Is.EqualTo("vend4"));
            Assert.That(PipSubTabs.LabelKey(Opt, 5), Is.EqualTo("opt5"));
            Assert.That(PipSubTabs.LabelKey(Med, 2), Is.EqualTo("med2"));
            Assert.That(PipSubTabs.LabelKey(Work, 1), Is.EqualTo("work1"));
            Assert.That(PipSubTabs.LabelKey(App, 1), Is.EqualTo("app1"));
            Assert.That(PipSubTabs.LabelKey(Vault, 3), Is.EqualTo("vault3"));
        }

        [Test]
        public void LabelKey_AgreesWithThePageTableForEveryPage()
        {
            // The prefix IS PipBuckPages.IdentifierFor — two tables that must not drift apart, since
            // one labels the main tab and the other the sub-tabs of the same page.
            foreach (int pageId in PipBuckPages.AllPageIds())
            {
                string prefix = PipBuckPages.IdentifierFor(pageId);
                Assert.That(prefix, Is.Not.EqualTo("?"), "every page id in the table has an identifier");

                for (int subPage = 1; subPage <= PipSubTabs.MaxSubTabs; subPage++)
                {
                    Assert.That(PipSubTabs.LabelKey(pageId, subPage), Is.EqualTo(prefix + subPage),
                        $"page {pageId} sub-page {subPage}");
                }
            }
        }

        [Test]
        public void LabelKey_RefusesASubPageOutsideOneToFive()
        {
            Assert.That(PipSubTabs.LabelKey(Stat, 0), Is.EqualTo("?"));
            Assert.That(PipSubTabs.LabelKey(Stat, 6), Is.EqualTo("?"));
            Assert.That(PipSubTabs.LabelKey(0, 1), Is.EqualTo("?"), "page 0 is pages[0] — the null slot");
            Assert.That(PipSubTabs.LabelKey(10, 1), Is.EqualTo("?"));
        }

        // ── Visibility: the pages that never narrow ───────────────────────────

        [Test]
        public void IsVisible_StatInvAndInfo_ShowAllFive()
        {
            // The base constructor builds all five (PipPage.as:143-152) and these three never hide
            // one. The document agrees: every stat/inv/info key is present (see the data half).
            foreach (int pageId in new[] { Stat, Inv, Info })
            {
                for (int subPage = 1; subPage <= PipSubTabs.MaxSubTabs; subPage++)
                {
                    Assert.That(PipSubTabs.IsVisible(pageId, subPage, PipSubTabContext.Default), Is.True,
                        $"page {pageId} sub-page {subPage}");
                }

                Assert.That(PipSubTabs.VisibleCount(pageId, PipSubTabContext.Default), Is.EqualTo(5));
            }
        }

        [Test]
        public void IsVisible_Vault_KeepsEquipmentAmmunitionAndStuff()
        {
            // PipPageVault.as:24 — the constructor hides but4 and but5.
            Assert.That(PipSubTabs.VisibleSubPages(Vault, PipSubTabContext.Default),
                Is.EqualTo(new[] { 1, 2, 3 }));
        }

        [Test]
        public void IsVisible_Work_HidesTheTwoTheConstructorHides()
        {
            // PipPageWork.as:25 — the constructor hides but4 and but5. With no tool named, all three
            // of the rest stay up (the narrowing below is the tool's doing, not the page's).
            Assert.That(PipSubTabs.VisibleSubPages(Work, PipSubTabContext.Default),
                Is.EqualTo(new[] { 1, 2, 3 }));
        }

        [Test]
        public void IsVisible_Med_HidesEverythingFromHealingUp()
        {
            // PipPageMed.as:41 — the constructor hides but3, but4 and but5.
            Assert.That(PipSubTabs.VisibleSubPages(Med, PipSubTabContext.Default),
                Is.EqualTo(new[] { 1, 2 }));
        }

        [Test]
        public void IsVisible_App_ShowsNothing()
        {
            // PipPageApp.as:12 — all five hidden. The page is the appearance customiser itself; it
            // has no sub-pages at all. This is the only page where the row is empty, and it is why
            // `VisibleSubPages` returns an empty array rather than being impossible.
            Assert.That(PipSubTabs.VisibleSubPages(App, PipSubTabContext.Default), Is.Empty);
            Assert.That(PipSubTabs.VisibleCount(App, PipSubTabContext.Default), Is.EqualTo(0));
        }

        // ── Visibility: the contextual branches ───────────────────────────────

        [Test]
        public void IsVisible_Vend_HidesTheQuestTabUntilAnNpcOwnsThePage()
        {
            // PipPageVend.as:89-93 — `if (this.npcId == "") { if (page2 == 4) page2 = 1;
            // vis.but4.visible = false; }`.
            var noNpc = new PipSubTabContext(null, null, null, false);
            var withNpc = new PipSubTabContext("trader_01", null, null, false);

            Assert.That(PipSubTabs.IsVisible(Vend, 4, noNpc), Is.False);
            Assert.That(PipSubTabs.IsVisible(Vend, 4, withNpc), Is.True);

            // ...and the tab is not merely hidden, it is moved off. See Coerce.
            Assert.That(PipSubTabs.Coerce(Vend, 4, noNpc), Is.EqualTo(1));
        }

        [Test]
        public void IsVisible_Vend_HidesRepairForAPlainVendingMachine()
        {
            // PipPageVend.as:103-107 — `if (this.npcInter == "v") { vis.but3.visible = false; … }`.
            // A machine cannot repair, and the document has no `vend3` for it to show either — except
            // that it does, because a `"vr"` vendor can. The caption is shared, the visibility is not.
            var machine = new PipSubTabContext("vm_01", "v", null, false);
            var repairer = new PipSubTabContext("vm_01", "vr", null, false);

            Assert.That(PipSubTabs.IsVisible(Vend, 3, machine), Is.False);
            Assert.That(PipSubTabs.IsVisible(Vend, 3, repairer), Is.True);

            // The rest of the row is unaffected by the kind of vendor.
            Assert.That(PipSubTabs.VisibleSubPages(Vend, machine), Is.EqualTo(new[] { 1, 2, 4 }));
        }

        [Test]
        public void IsVisible_Vend_AlwaysHidesTheFifthSlot()
        {
            // PipPageVend.as:40 — `vis.but5.visible = false;` in the constructor, for every vendor.
            // There is no `vend5` caption in the document, which is the same statement from the data.
            foreach (var context in Contexts())
            {
                Assert.That(PipSubTabs.IsVisible(Vend, 5, context), Is.False);
            }
        }

        [Test]
        public void IsVisible_Opt_LightModeKeepsOptionsAndControl()
        {
            // PipPageOpt.as:500-513 — `if (pip.light) { vis.but5.visible = vis.but1.visible =
            // vis.but2.visible = false; … }`. Load, Save and Log all need a shell to draw in.
            var normal = new PipSubTabContext(null, null, null, false);
            var light = new PipSubTabContext(null, null, null, true);

            Assert.That(PipSubTabs.VisibleSubPages(Opt, normal), Is.EqualTo(new[] { 1, 2, 3, 4, 5 }));
            Assert.That(PipSubTabs.VisibleSubPages(Opt, light), Is.EqualTo(new[] { 3, 4 }));
        }

        [Test]
        public void IsVisible_Med_HidesTradingForADoctorPaidInSomethingElse()
        {
            // PipPageMed.as:48-57 — `adoc` is paid in gel and `vdoc` in goods, so neither has a caps
            // price list for a Trading tab to show.
            foreach (string kind in new[] { "adoc", "vdoc" })
            {
                var context = new PipSubTabContext("doc_01", kind, null, false);
                Assert.That(PipSubTabs.IsVisible(Med, 2, context), Is.False, kind);
                Assert.That(PipSubTabs.IsVisible(Med, 1, context), Is.True,
                    "Healing is the whole point of the page and is never hidden");
            }

            var caps = new PipSubTabContext("doc_01", "doc", null, false);
            Assert.That(PipSubTabs.IsVisible(Med, 2, caps), Is.True);
        }

        [Test]
        public void IsVisible_Work_NarrowsToTheToolThatIsOpen()
        {
            // PipPageWork.as:48-58 — all three are re-shown, then narrowed: a workbench only repairs,
            // and a stove, a lab and an explosive bench only create.
            var bench = new PipSubTabContext("wb_01", null, "mworkbench", false);
            Assert.That(PipSubTabs.VisibleSubPages(Work, bench), Is.EqualTo(new[] { 3 }));

            foreach (string tool in new[] { "stove", "lab", "expl" })
            {
                var context = new PipSubTabContext("wb_01", null, tool, false);
                Assert.That(PipSubTabs.VisibleSubPages(Work, context), Is.EqualTo(new[] { 1 }), tool);
            }

            var unknown = new PipSubTabContext("wb_01", null, "somethingElse", false);
            Assert.That(PipSubTabs.VisibleSubPages(Work, unknown), Is.EqualTo(new[] { 1, 2, 3 }));
        }

        [Test]
        public void IsVisible_RefusesASubPageOutsideOneToFive()
        {
            Assert.That(PipSubTabs.IsVisible(Stat, 0, PipSubTabContext.Default), Is.False);
            Assert.That(PipSubTabs.IsVisible(Stat, 6, PipSubTabContext.Default), Is.False);
            Assert.That(PipSubTabs.IsVisible(0, 1, PipSubTabContext.Default), Is.False,
                "page 0 is pages[0] — the null slot, and it has no row at all");
            Assert.That(PipSubTabs.IsVisible(10, 1, PipSubTabContext.Default), Is.False);
        }

        [Test]
        public void VisibleSubPages_ListsThemAscending()
        {
            foreach (int pageId in PipBuckPages.AllPageIds())
            {
                foreach (var context in Contexts())
                {
                    int[] visible = PipSubTabs.VisibleSubPages(pageId, context);
                    Assert.That(visible.Length, Is.EqualTo(PipSubTabs.VisibleCount(pageId, context)));

                    for (int i = 1; i < visible.Length; i++)
                    {
                        Assert.That(visible[i], Is.GreaterThan(visible[i - 1]),
                            $"page {pageId}: the visible sub-pages come out in order");
                    }
                }
            }
        }

        // ── Coercion ──────────────────────────────────────────────────────────

        [Test]
        public void Coerce_LeavesAnOrdinaryRequestAlone()
        {
            // Nothing about the stat, inventory, info or vault pages moves `page2`. A rule that
            // "helpfully" fell back to the first visible sub-page would disagree with the Oracle in
            // the one case that is hardest to notice — a page parked on a hidden sub-tab, where both
            // draw no lit button and only the port would move.
            foreach (int pageId in new[] { Stat, Inv, Info, Vault })
            {
                for (int subPage = 1; subPage <= PipSubTabs.MaxSubTabs; subPage++)
                {
                    Assert.That(PipSubTabs.Coerce(pageId, subPage, PipSubTabContext.Default),
                        Is.EqualTo(subPage), $"page {pageId} sub-page {subPage}");
                }
            }
        }

        [Test]
        public void Coerce_Work_ForcesTheToolSubPage()
        {
            // PipPageWork.as:51-58 — the page assigns `page2` outright, discarding what was asked for.
            var bench = new PipSubTabContext("wb", null, "mworkbench", false);
            var stove = new PipSubTabContext("wb", null, "stove", false);

            Assert.That(PipSubTabs.Coerce(Work, 1, bench), Is.EqualTo(3));
            Assert.That(PipSubTabs.Coerce(Work, 2, bench), Is.EqualTo(3));
            Assert.That(PipSubTabs.Coerce(Work, 3, stove), Is.EqualTo(1));
            Assert.That(PipSubTabs.Coerce(Work, 2, PipSubTabContext.Default), Is.EqualTo(2),
                "with no tool named the request stands");
        }

        [Test]
        public void Coerce_Opt_MovesLoadAndSaveToOptionsInLightMode()
        {
            // PipPageOpt.as:502-507 — `if (page2 == 1 || page2 == 2) { page2 = 3; }`. Note Log (5) is
            // hidden in light mode but is NOT moved: the Oracle's condition names 1 and 2 only, and a
            // "tidy up" that also moved 5 would be a divergence in a state the player can reach.
            var light = new PipSubTabContext(null, null, null, true);

            Assert.That(PipSubTabs.Coerce(Opt, 1, light), Is.EqualTo(3));
            Assert.That(PipSubTabs.Coerce(Opt, 2, light), Is.EqualTo(3));
            Assert.That(PipSubTabs.Coerce(Opt, 3, light), Is.EqualTo(3));
            Assert.That(PipSubTabs.Coerce(Opt, 5, light), Is.EqualTo(5),
                "the Oracle's condition names 1 and 2, not 5");

            Assert.That(PipSubTabs.Coerce(Opt, 1, PipSubTabContext.Default), Is.EqualTo(1));
        }

        [Test]
        public void Coerce_RefusesSomethingThatIsNotASubPage()
        {
            // PipPage.as:53 — `internal var page2:int = 1;`.
            Assert.That(PipSubTabs.Coerce(Stat, 0, PipSubTabContext.Default), Is.EqualTo(1));
            Assert.That(PipSubTabs.Coerce(Stat, -3, PipSubTabContext.Default), Is.EqualTo(1));
            Assert.That(PipSubTabs.Coerce(Stat, 6, PipSubTabContext.Default), Is.EqualTo(1));
        }

        [Test]
        public void DefaultSubPage_IsCoerceAgainstOne()
        {
            foreach (int pageId in PipBuckPages.AllPageIds())
            {
                foreach (var context in Contexts())
                {
                    Assert.That(PipSubTabs.DefaultSubPage(pageId, context),
                        Is.EqualTo(PipSubTabs.Coerce(pageId, 1, context)), $"page {pageId}");
                }
            }
        }

        // ── The two sub-tabs that are not sub-tabs ────────────────────────────

        [Test]
        public void NavigatesToPage_DoctorTradingIsTheMerchantPage()
        {
            // PipPageMed.as:247-252 — `if (page2 == 2) { page2 = 1; pip.onoff(4); … }`. The doctor's
            // second sub-tab opens the vendor page; it is a sub-tab in appearance only.
            Assert.That(PipSubTabs.NavigatesToPage(Med, 2, PipSubTabContext.Default), Is.EqualTo(Vend));
            Assert.That(PipSubTabs.NavigatesToPage(Med, 1, PipSubTabContext.Default), Is.EqualTo(0),
                "Healing stays on the page");
        }

        [Test]
        public void NavigatesToPage_DocVendorRepairIsTheDoctorPage()
        {
            // PipPageVend.as:478-481 — `if (page2 == 3 && this.npcInter == "doc") { page2 = 1;
            // pip.onoff(6); }`. The mirror of the rule above, and gated on the interaction kind: a
            // plain `vr` vendor's Repair really is a repair sub-page.
            var doc = new PipSubTabContext("doc_01", "doc", null, false);
            var repairer = new PipSubTabContext("vr_01", "vr", null, false);

            Assert.That(PipSubTabs.NavigatesToPage(Vend, 3, doc), Is.EqualTo(Med));
            Assert.That(PipSubTabs.NavigatesToPage(Vend, 3, repairer), Is.EqualTo(0));
        }

        [Test]
        public void NavigatesToPage_IsZeroForEveryOtherSubTab()
        {
            foreach (int pageId in PipBuckPages.AllPageIds())
            {
                foreach (var context in Contexts())
                {
                    for (int subPage = 1; subPage <= PipSubTabs.MaxSubTabs; subPage++)
                    {
                        int target = PipSubTabs.NavigatesToPage(pageId, subPage, context);
                        if (target == 0) continue;

                        Assert.That(target == Vend || target == Med, Is.True,
                            $"page {pageId} sub-page {subPage} navigates to {target}, which is not a " +
                            "page that any sub-tab opens");

                        Assert.That(pageId == Med || pageId == Vend, Is.True,
                            "only the doctor and the vendor have a navigating sub-tab");
                    }
                }
            }
        }

        // ── Frame selection and signs ─────────────────────────────────────────

        [Test]
        public void FrameFor_TheCurrentSubTabWinsOverItsOwnSign()
        {
            // PipPage.as:894-915 — `if (page2 == n) gotoAndStop(2); else if (signs[n] > 0)
            // gotoAndStop(signs[n] + 2); else gotoAndStop(1);`. The order matters: a player standing
            // on Health with a bleeding limb sees the plain active frame, because the marker exists to
            // draw them TO a tab they are not on.
            Assert.That(PipSubTabs.FrameFor(3, 3, 0), Is.EqualTo(PipSubTabs.ActiveFrame));
            Assert.That(PipSubTabs.FrameFor(3, 3, 2), Is.EqualTo(PipSubTabs.ActiveFrame),
                "the active frame beats the tab's own sign");
        }

        [Test]
        public void FrameFor_SignedTabDrawsItsSignFrame()
        {
            Assert.That(PipSubTabs.FrameFor(2, 1, 0), Is.EqualTo(PipSubTabs.NormalFrame));
            Assert.That(PipSubTabs.FrameFor(2, 1, 1), Is.EqualTo(3), "signs[n] + 2");
            Assert.That(PipSubTabs.FrameFor(2, 1, 3), Is.EqualTo(5), "signs[n] + 2");
        }

        [Test]
        public void SignFor_Stat_MarksTheTwoPointTabs()
        {
            // PipPageStat.as:453-463 — `if (this.pers.skillPoint > 0) signs[2] = 1;` and the same for
            // perkPoint on signs[3]. Skills is sub-tab 2 and Perks sub-tab 3.
            var unspent = new PipSubTabSignals(4, 2, false, 0f, false);
            var spent = new PipSubTabSignals(0, 0, false, 0f, false);

            Assert.That(PipSubTabs.SignFor(Stat, 2, unspent), Is.EqualTo(1), "Skills");
            Assert.That(PipSubTabs.SignFor(Stat, 3, unspent), Is.EqualTo(1), "Perks");

            Assert.That(PipSubTabs.SignFor(Stat, 2, spent), Is.EqualTo(0));
            Assert.That(PipSubTabs.SignFor(Stat, 3, spent), Is.EqualTo(0));

            Assert.That(PipSubTabs.SignFor(Stat, 1, unspent), Is.EqualTo(0), "Main is never signed");
            Assert.That(PipSubTabs.SignFor(Stat, 4, unspent), Is.EqualTo(0), "Effects is never signed");
        }

        [Test]
        public void SignFor_Stat_HealthSignGrowsAsTheLowestVitalDrops()
        {
            // PipPageStat.as:465-470 — four ORs over the same threshold, so the lowest of head, torso,
            // legs and blood decides: below a quarter is sign 3, below a half is sign 2.
            Assert.That(PipSubTabs.SignFor(Stat, 5, new PipSubTabSignals(0, 0, true, 0.9f, false)), Is.EqualTo(0));
            Assert.That(PipSubTabs.SignFor(Stat, 5, new PipSubTabSignals(0, 0, true, 0.49f, false)), Is.EqualTo(2));
            Assert.That(PipSubTabs.SignFor(Stat, 5, new PipSubTabSignals(0, 0, true, 0.24f, false)), Is.EqualTo(3));

            // The boundaries themselves. The Oracle uses strict `<`, so exactly a half and exactly a
            // quarter are the milder side — an off-by-one here is invisible on screen.
            Assert.That(PipSubTabs.SignFor(Stat, 5, new PipSubTabSignals(0, 0, true, 0.5f, false)), Is.EqualTo(0));
            Assert.That(PipSubTabs.SignFor(Stat, 5, new PipSubTabSignals(0, 0, true, 0.25f, false)), Is.EqualTo(2));
        }

        [Test]
        public void SignFor_Stat_DoesNotSignHealthWhenThereIsNoPlayer()
        {
            // The control for the rule above. "No player yet" and "a player at zero health" are both
            // 0 as a fraction, and the rule fires below 0.25 — so without HasVitals every page before
            // the player exists would show a red Health marker. PipSubTabSignals.None is what the
            // shipped port passes.
            Assert.That(PipSubTabs.SignFor(Stat, 5, PipSubTabSignals.None), Is.EqualTo(0));
            Assert.That(PipSubTabs.SignFor(Stat, 5, new PipSubTabSignals(0, 0, false, 0f, false)),
                Is.EqualTo(0));
        }

        [Test]
        public void SignFor_Vend_MarksTheQuestTabOnly()
        {
            // PipPageVend.as:439-465 — signs[4] = 1 when the open vendor has a quest to report or to
            // hand over. The early `if (this.vend == null) return;` folds into the flag.
            var quests = new PipSubTabSignals(0, 0, false, 0f, true);

            Assert.That(PipSubTabs.SignFor(Vend, 4, quests), Is.EqualTo(1));
            Assert.That(PipSubTabs.SignFor(Vend, 4, PipSubTabSignals.None), Is.EqualTo(0));
            Assert.That(PipSubTabs.SignFor(Vend, 1, quests), Is.EqualTo(0), "Buy is never signed");
        }

        [Test]
        public void SignFor_EveryOtherPageNeverSignsASubTab()
        {
            // Only two `setSigns` overrides exist in the whole interface (PipPageStat and PipPageVend).
            // Every other page leaves all five at 0.
            var everything = new PipSubTabSignals(9, 9, true, 0.01f, true);

            foreach (int pageId in new[] { Inv, Info, Opt, Med, Work, App, Vault })
            {
                for (int subPage = 1; subPage <= PipSubTabs.MaxSubTabs; subPage++)
                {
                    Assert.That(PipSubTabs.SignFor(pageId, subPage, everything), Is.EqualTo(0),
                        $"page {pageId} sub-page {subPage}");
                }
            }
        }

        // ── The shipped document ──────────────────────────────────────────────

        private static PipTextTable _shipped;

        /// <summary>
        /// The shipped <c>text_en.xml</c>, parsed once.
        ///
        /// <para><b>Lazy, not <c>[OneTimeSetUp]</c>.</b> The offline wall's runner dispatches only
        /// <c>SetUpAttribute</c> and <c>TearDownAttribute</c>, so a one-time set-up silently never
        /// runs there and every test in the fixture then reads a null table. Same trap
        /// <c>PipTextTableTests</c> records.</para>
        /// </summary>
        private static PipTextTable Shipped
        {
            get
            {
                if (_shipped != null) return _shipped;

                string path = Path.Combine(
                    SourceLint.ProjectRoot(), "Assets", "_PFE", "Data", "Resources", "Localisation", "text_en.xml");

                Assert.That(File.Exists(path), Is.True, "The localisation document is missing: " + path);

                _shipped = PipTextTable.Parse(File.ReadAllText(path));

                Assert.That(_shipped.IsEmpty, Is.False, "The document parsed to zero entries: " + path);

                return _shipped;
            }
        }

        /// <summary>
        /// Whether the document carries a caption for a sub-tab — the <c>TryGet</c> answer, not the
        /// <c>Text</c> one, so a missing key is <b>false</b> rather than the marker string.
        /// </summary>
        private static bool DocumentHasCaption(int pageId, int subPage)
        {
            return Shipped.TryGet("p", PipSubTabs.LabelKey(pageId, subPage), 0, out _);
        }

        /// <summary>
        /// The contexts the sweep below varies. One field at a time, every value the Oracle compares
        /// against — see <see cref="PipSubTabContext"/> for where each comes from.
        /// </summary>
        private static IEnumerable<PipSubTabContext> Contexts()
        {
            yield return PipSubTabContext.Default;
            yield return new PipSubTabContext("npc", null, null, false);
            yield return new PipSubTabContext("npc", "v", null, false);
            yield return new PipSubTabContext("npc", "vr", null, false);
            yield return new PipSubTabContext("npc", "doc", null, false);
            yield return new PipSubTabContext("npc", "adoc", null, false);
            yield return new PipSubTabContext("npc", "vdoc", null, false);
            yield return new PipSubTabContext("npc", null, "mworkbench", false);
            yield return new PipSubTabContext("npc", null, "stove", false);
            yield return new PipSubTabContext("npc", null, "lab", false);
            yield return new PipSubTabContext("npc", null, "expl", false);
            yield return new PipSubTabContext(null, null, null, true);
        }

        /// <summary>Whether any context at all makes a sub-tab visible.</summary>
        private static bool ShowableInSomeContext(int pageId, int subPage)
        {
            foreach (var context in Contexts())
            {
                if (PipSubTabs.IsVisible(pageId, subPage, context)) return true;
            }

            return false;
        }

        [Test]
        public void Shipped_GivesTheSubTabCaptions()
        {
            Assert.That(Shipped.Pip("stat1"), Is.EqualTo("Main"));
            Assert.That(Shipped.Pip("stat2"), Is.EqualTo("Skills"));
            Assert.That(Shipped.Pip("stat3"), Is.EqualTo("Perks"));
            Assert.That(Shipped.Pip("stat4"), Is.EqualTo("Effects"));
            Assert.That(Shipped.Pip("stat5"), Is.EqualTo("Health"));

            Assert.That(Shipped.Pip("inv3"), Is.EqualTo("Equipment"),
                "sub-page 3 is Equipment — not the port's old prose 'Aid'");
            Assert.That(Shipped.Pip("inv5"), Is.EqualTo("Ammunition"),
                "sub-page 5 is Ammunition — not 'Ammo'");

            Assert.That(Shipped.Pip("info1"), Is.EqualTo("Map"));
            Assert.That(Shipped.Pip("info5"), Is.EqualTo("Enemies"));

            Assert.That(Shipped.Pip("vend4"), Is.EqualTo("Quests"));
            Assert.That(Shipped.Pip("opt5"), Is.EqualTo("Log"));
            Assert.That(Shipped.Pip("med2"), Is.EqualTo("Trading"));
            Assert.That(Shipped.Pip("work3"), Is.EqualTo("Repair"));
            Assert.That(Shipped.Pip("vault3"), Is.EqualTo("Stuff"));
        }

        [Test]
        public void Shipped_CarriesACaptionForExactlyTheSubTabsThatCanBeShown()
        {
            // THE cross-check. Two independent sources have to agree here:
            //
            //   * the ActionScript, which decides visibility (PipPage*.as constructors and
            //     setSubPages), and
            //   * text_en.xml, which was written by hand and carries a caption only for the buttons
            //     the author could see.
            //
            // So the document has `stat1`..`stat5` and no `app1`, has `vend4` (a vendor with an NPC
            // shows a quest log) and no `vend5` (hidden for every vendor, unconditionally). If either
            // side drifts — a hidden button that regains a caption, or a visible one that loses it —
            // exactly one of the two loops below goes red and names the slot.
            var mismatches = new List<string>();

            foreach (int pageId in PipBuckPages.AllPageIds())
            {
                for (int subPage = 1; subPage <= PipSubTabs.MaxSubTabs; subPage++)
                {
                    bool showable = ShowableInSomeContext(pageId, subPage);
                    bool captioned = DocumentHasCaption(pageId, subPage);

                    if (showable != captioned)
                    {
                        mismatches.Add($"{PipSubTabs.LabelKey(pageId, subPage)}: " +
                                       $"code says {(showable ? "showable" : "never shown")}, " +
                                       $"document says {(captioned ? "captioned" : "no caption")}");
                    }
                }
            }

            Assert.That(mismatches, Is.Empty,
                "Every sub-tab the code can show must have a caption, and every sub-tab it can never " +
                "show must not:\n  " + string.Join("\n  ", mismatches));

            // The count, so a rule that hides *everything* cannot satisfy the equality above by
            // making both sides empty.
            int showableTotal = 0;
            foreach (int pageId in PipBuckPages.AllPageIds())
            {
                showableTotal += CountShowable(pageId);
            }

            Assert.That(showableTotal, Is.EqualTo(32),
                "5 stat + 5 inv + 5 info + 4 vend + 5 opt + 2 med + 3 work + 0 app + 3 vault");
        }

        private static int CountShowable(int pageId)
        {
            int count = 0;
            for (int subPage = 1; subPage <= PipSubTabs.MaxSubTabs; subPage++)
            {
                if (ShowableInSomeContext(pageId, subPage)) count++;
            }

            return count;
        }

        [Test]
        public void Shipped_NamesTheSubTabsTheConstructorsHide()
        {
            // The other half of the cross-check, stated positively so the absence is not an accident
            // of the loop above. These are the thirteen slots no constructor ever reveals; the
            // document has no caption for any of them.
            var hidden = new (int Page, int Sub)[]
            {
                (Vend, 5),                                          // PipPageVend.as:40
                (Med, 3), (Med, 4), (Med, 5),                        // PipPageMed.as:41
                (Work, 4), (Work, 5),                                // PipPageWork.as:25
                (App, 1), (App, 2), (App, 3), (App, 4), (App, 5),    // PipPageApp.as:12
                (Vault, 4), (Vault, 5),                              // PipPageVault.as:24
            };

            Assert.That(hidden.Length, Is.EqualTo(13));

            foreach ((int page, int sub) in hidden)
            {
                string key = PipSubTabs.LabelKey(page, sub);
                Assert.That(DocumentHasCaption(page, sub), Is.False,
                    key + " is hidden by its page's constructor, so the document must not caption it");
            }
        }

        [Test]
        public void Label_PrefersTheDocumentOverTheFallback()
        {
            // The positive control for the fallback table, and it is not optional. `Label` was built
            // so that its fallback agrees with the shipped document (see the test below), which means
            // every other assertion here would still pass if `Label` ignored its `text` argument
            // entirely and always returned the fallback. A document that *disagrees* is the only way
            // to tell "resolved from the document" apart from "never looked".
            var other = PipTextTable.Parse(
                "<all><pip id=\"stat2\"><n>Fertigkeiten</n></pip>" +
                "<pip id=\"inv3\"><n>Ausrüstung</n></pip></all>");

            Assert.That(PipSubTabs.Label(Stat, 2, other), Is.EqualTo("Fertigkeiten"));
            Assert.That(PipSubTabs.Label(Inv, 3, other), Is.EqualTo("Ausrüstung"));

            // A key that document does not carry still falls back to the port's own word...
            Assert.That(PipSubTabs.Label(Stat, 1, other), Is.EqualTo("Main"));

            // ...and a key neither source carries gets the Oracle's own missing-key marker
            // (Res.as:120-130) rather than an invented word. The App page is the only page whose
            // slots are all in that state.
            Assert.That(PipSubTabs.Label(App, 1, Shipped), Is.EqualTo("*pip_app1"));
            Assert.That(PipSubTabs.Label(App, 5, Shipped), Is.EqualTo("*pip_app5"));
        }

        [Test]
        public void Label_FallsBackToWordsThatMatchTheDocument()
        {
            // The port's own English is a table, not a placeholder — and this is what stops it from
            // quietly becoming a *different* word. Every fallback must be identical to the shipped
            // caption, so a checkout without the document reads the same device, and a caption that
            // is later re-translated cannot leave the fallback behind.
            var disagreements = new List<string>();

            foreach (int pageId in PipBuckPages.AllPageIds())
            {
                for (int subPage = 1; subPage <= PipSubTabs.MaxSubTabs; subPage++)
                {
                    if (!DocumentHasCaption(pageId, subPage)) continue;

                    string key = PipSubTabs.LabelKey(pageId, subPage);
                    string fallback = PipSubTabs.Label(pageId, subPage, null);
                    string shipped = Shipped.Pip(key);

                    if (fallback != shipped) disagreements.Add($"{key}: '{fallback}' vs '{shipped}'");
                }
            }

            Assert.That(disagreements, Is.Empty,
                "the fallback captions must agree with the document:\n  " +
                string.Join("\n  ", disagreements));
        }

        [Test]
        public void Label_WithNoDocument_StillHasAWordForEveryShowableSubTab()
        {
            // A build that strips Resources, or a checkout that predates the document, must still
            // show a readable row — not `*pip_stat1` five times. The marker is the answer only for a
            // slot that is hidden anyway (see Shipped_NamesTheSubTabsTheConstructorsHide), which is
            // why this walks the showable set and not all 45.
            foreach (int pageId in PipBuckPages.AllPageIds())
            {
                for (int subPage = 1; subPage <= PipSubTabs.MaxSubTabs; subPage++)
                {
                    if (!ShowableInSomeContext(pageId, subPage)) continue;

                    string caption = PipSubTabs.Label(pageId, subPage, null);

                    Assert.That(caption, Is.Not.Null.And.Not.Empty);
                    Assert.That(caption, Does.Not.StartWith("*"),
                        $"{PipSubTabs.LabelKey(pageId, subPage)} has no fallback word");
                }
            }
        }

        [Test]
        public void Label_WithAnEmptyTable_FallsBackRatherThanShowingMarkers()
        {
            // An empty table is the "document failed to load" state — PipTextTable.Empty misses every
            // key. It must take the same path as null, or a stripped build would show `*pip_*`.
            foreach (int pageId in PipBuckPages.AllPageIds())
            {
                for (int subPage = 1; subPage <= PipSubTabs.MaxSubTabs; subPage++)
                {
                    Assert.That(PipSubTabs.Label(pageId, subPage, PipTextTable.Empty),
                        Is.EqualTo(PipSubTabs.Label(pageId, subPage, null)),
                        PipSubTabs.LabelKey(pageId, subPage));
                }
            }
        }

        // ── The row the shipped port actually draws ───────────────────────────

        [Test]
        public void TabLabels_ResolveFromTheDocumentForEveryPage()
        {
            // The main tabs, pinned here alongside the sub-tabs because the two are the same
            // question asked one level apart and a page whose tab is right and whose row is wrong is
            // as broken as the reverse. `PipBuckInventoryTests` pins the no-document fallback
            // (`TabLabelFor(pageId)`); this pins the path the device actually uses, which is
            // `Res.pipText("main" + n)` (PipBuck.as:172) against the shipped table.
            var expected = new Dictionary<int, string>
            {
                [0] = "Close [TAB]",   // main0, written by toNormalMode (PipBuck.as:177)
                [1] = "Status",
                [2] = "Inventory",
                [3] = "Information",
                [4] = "Trading",       // the caption that was unobservable until the document was found
                [5] = "Main menu",
                [6] = "Healing",
                [7] = "Craft",
                [8] = "Appearance",
                [9] = "Storage",
            };

            foreach (var pair in expected)
            {
                string key = PipBuckPages.LabelKeyFor(pair.Key);
                Assert.That(PipBuckPages.TabLabelFor(pair.Key, Shipped), Is.EqualTo(pair.Value),
                    $"page {pair.Key} ({key})");
            }
        }

        [Test]
        public void DefaultContext_GivesEveryPageItsRowFromTheDocument()
        {
            // What the play-test sees, page by page, with the shipped document and no context — the
            // state every page in the port is in. Written out in full because this is the assertion
            // that would have caught the original bug (a page with no sub-tabs at all): it names the
            // captions, so a row that draws nothing or draws the wrong ones cannot pass.
            var expected = new Dictionary<int, string[]>
            {
                [Stat]  = new[] { "Main", "Skills", "Perks", "Effects", "Health" },
                [Inv]   = new[] { "Weapons", "Armor", "Equipment", "Other", "Ammunition" },
                [Info]  = new[] { "Map", "Quests", "World", "Notes", "Enemies" },
                [Vend]  = new[] { "Buy", "Sell", "Repair" },
                [Opt]   = new[] { "Load", "Save", "Options", "Control", "Log" },
                [Med]   = new[] { "Healing", "Trading" },
                [Work]  = new[] { "Create", "Enhance", "Repair" },
                [App]   = new string[0],
                [Vault] = new[] { "Equipment", "Ammunition", "Stuff" },
            };

            foreach (int pageId in PipBuckPages.AllPageIds())
            {
                var captions = new List<string>();
                foreach (int subPage in PipSubTabs.VisibleSubPages(pageId, PipSubTabContext.Default))
                {
                    captions.Add(PipSubTabs.Label(pageId, subPage, Shipped));
                }

                Assert.That(captions, Is.EqualTo(expected[pageId]),
                    $"page {pageId} ({PipBuckPages.IdentifierFor(pageId)})");
            }
        }

        [Test]
        public void DefaultContext_LightsTheFirstSubTabOnEveryPageThatHasOne()
        {
            // PipPage.as:53 — `page2 = 1`, and no page's default coercion moves it away from 1 except
            // the workbench (which has no tool named here) — so the row opens on its first button.
            foreach (int pageId in PipBuckPages.AllPageIds())
            {
                int subPage = PipSubTabs.DefaultSubPage(pageId, PipSubTabContext.Default);
                Assert.That(subPage, Is.EqualTo(1), $"page {pageId}");

                if (PipSubTabs.VisibleCount(pageId, PipSubTabContext.Default) == 0) continue;

                Assert.That(PipSubTabs.IsVisible(pageId, subPage, PipSubTabContext.Default), Is.True,
                    $"page {pageId} opens on sub-page {subPage}, which must be one of its own");
            }
        }

        [Test]
        public void DefaultContext_HasExactlyOneLitButtonPerPage()
        {
            // The port of setButtons: exactly one button draws frame 2 per page (or none, when the
            // row is empty). Two lit buttons would mean the current sub-page matched twice.
            foreach (int pageId in PipBuckPages.AllPageIds())
            {
                int subPage = PipSubTabs.DefaultSubPage(pageId, PipSubTabContext.Default);
                int active = 0;

                for (int slot = 1; slot <= PipSubTabs.MaxSubTabs; slot++)
                {
                    if (!PipSubTabs.IsVisible(pageId, slot, PipSubTabContext.Default)) continue;
                    if (PipSubTabs.FrameFor(slot, subPage, 0) == PipSubTabs.ActiveFrame) active++;
                }

                Assert.That(active, Is.EqualTo(PipSubTabs.VisibleCount(pageId, PipSubTabContext.Default) > 0 ? 1 : 0),
                    $"page {pageId}");
            }
        }
    }
}
