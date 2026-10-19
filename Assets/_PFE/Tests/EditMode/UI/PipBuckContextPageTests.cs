using NUnit.Framework;
using PFE.Core;
using PFE.UI.Common;
using PFE.UI.PipBuck;
using PFE.UI.PipBuck.Pages;
using UnityEngine;

namespace PFE.Tests.EditMode.UI
{
    /// <summary>
    /// Pins the two events the PipBuck keeps separate and that the trade page used to conflate —
    /// <b>a page switch</b> and <b>the device closing</b>.
    ///
    /// <para><b>What this is a guard for.</b> The play-test reported "buy, sell, repair show no traders
    /// and there is no quest tab" over a page that had just opened with a shelf. The cause was not the
    /// vendor and not the catalogue: <see cref="PipPageVendView.SetVisible"/> called
    /// <see cref="PipPageVendView.Close"/>, and <see cref="PipBuckManager.SetPage"/> hides
    /// <b>every</b> page before showing the target (<c>PipBuck.as:417-423</c>). So the sequence
    /// <c>Open(vendor, npcId, npcInter)</c> then <c>OnOff(4)</c> — the port's own <c>NPC.pip(4)</c> —
    /// hid page 4 first and dropped the context that had just been written to it. The page rebuilt
    /// with no vendor (the "No trader." empty state) and, because
    /// <see cref="PipSubTabs.IsVisible"/> hides <c>vend4</c> when <c>npcId</c> is empty, no Quests
    /// tab at all.</para>
    ///
    /// <para><b>Why the guard is here and not on the page.</b> <c>PipPageVendView</c> is plumbing: its
    /// <c>_root</c> is a <see cref="RectTransform"/> under a <c>Canvas</c>, and both
    /// <c>SetVisible</c> and <c>Rebuild</c> begin with <c>if (_root == null) return;</c>. A fake
    /// root is an ECall, so an offline fixture cannot drive the real page far enough to observe
    /// whether <c>SetVisible(false)</c> cleared its fields. The <i>ordering</i> that broke it lives in
    /// the manager, which is pure logic and is therefore what this fixture holds still.</para>
    ///
    /// <para><b>The rule, in one line.</b> Hiding a page must never end that page's context; only
    /// closing the device may. That is the Oracle's split too — <c>vis.visible</c> is a page switch
    /// (<c>PipBuck.as:417-423</c>) while <c>pip.vendor = null; pip.npcId = null</c> runs in the
    /// closing arm of <c>onoff</c> (<c>PipBuck.as:323-324</c>).</para>
    /// </summary>
    [TestFixture]
    public class PipBuckContextPageTests
    {
        /// <summary>
        /// A page the manager can hold with no engine at all: it records the three events that
        /// distinguish the two cases and does nothing else.
        /// </summary>
        private sealed class FakePage : IPipPage, IPipContextPage
        {
            public FakePage(int pageId) => PageId = pageId;

            public int PageId { get; }

            /// <summary>How many times the manager asked this page to draw.</summary>
            public int ShowCount { get; private set; }

            /// <summary>How many times the manager asked this page to stop drawing.</summary>
            public int HideCount { get; private set; }

            /// <summary>How many times the manager told this page the device had closed.</summary>
            public int ClosedCount { get; private set; }

            /// <summary>The last sub-page the manager asked for, or 0 when it never asked.</summary>
            public int LastSubPage { get; private set; }

            /// <summary>
            /// What opened this page. Settable here (the real pages set it in <c>Open</c>) so a fixture
            /// can stand up "a doc-vendor" without building the trade page's 1400 lines.
            /// </summary>
            public PipSubTabContext Context { get; set; } = PipSubTabContext.Default;

            public bool IsImplemented => true;

            public void SetStatus(int subPage) => LastSubPage = subPage;

            public void Step() { }

            public void Build(RectTransform pageRoot) { }

            public void SetVisible(bool visible)
            {
                if (visible) ShowCount++;
                else HideCount++;
            }

            public void OnDeviceClosed() => ClosedCount++;
        }

        private PipBuckManager _manager;
        private FakePage _stat;
        private FakePage _vend;
        private FakePage _med;

        /// <summary>
        /// A page that implements only <see cref="IPipPage"/> — no context to drop. The manager's close
        /// sweep is a pattern match, so nothing should throw and nothing should be recorded.
        /// </summary>
        private sealed class PlainPage : IPipPage
        {
            public PlainPage(int pageId) => PageId = pageId;

            public int PageId { get; }

            public bool IsImplemented => true;

            public void SetStatus(int subPage) { }

            public void Step() { }

            public void Build(RectTransform pageRoot) { }

            public void SetVisible(bool visible) { }
        }

        [SetUp]
        public void SetUp()
        {
            // A null sound service is deliberate and safe: PipBuckManager.Play returns immediately on
            // one, so the fixture does not need an audio engine to click a tab.
            _manager = new PipBuckManager(new PipBuckViewModel(), null, new InterfacePauseGate());

            _stat = new FakePage(1);
            _vend = new FakePage(4);
            _med = new FakePage(PipPageMedView.MedPageId);
            _manager.RegisterPage(_stat);
            _manager.RegisterPage(_vend);
            _manager.RegisterPage(_med);
        }

        [TearDown]
        public void TearDown()
        {
            _manager?.ViewModel?.Dispose();
        }

        /// <summary>The reported bug, reduced to its one assertion: <c>OnOff(4)</c> — what
        /// <c>PipBuckBootstrapper.BindVendorPage</c> calls straight after
        /// <c>PipPageVendView.Open</c> — must not close page 4.</summary>
        [Test]
        public void OnOff_OpeningPage4_DoesNotCloseIt()
        {
            _manager.OnOff(PipPageVendView.VendorPageId);

            Assert.That(_manager.IsOpen, Is.True, "The device should be open.");
            Assert.That(_manager.CurrentPage, Is.EqualTo(4), "The device should be on the trade page.");
            Assert.That(_vend.ClosedCount, Is.EqualTo(0),
                "Opening page 4 closed it. That is the bug: SetPage hides every page before showing the " +
                "target, and fronting a page must not end its context — only closing the device may.");
        }

        /// <summary>The same rule for an ordinary tab click, which also routes through
        /// <see cref="PipBuckManager.SetPage"/>'s hide-everything loop.</summary>
        [Test]
        public void PageClick_SwitchingPages_DoesNotCloseTheOneBeingLeft()
        {
            _manager.OnOff(1);

            _manager.PageClick(4);

            Assert.That(_vend.ClosedCount, Is.EqualTo(0),
                "A tab click that reaches page 4 closed it while switching to it.");
            Assert.That(_vend.HideCount, Is.GreaterThanOrEqualTo(1),
                "Page 4 should still have been hidden and shown — the hide is not what broke, the " +
                "close that was riding along with it was.");
        }

        /// <summary>The "no power" overlay hides the current page
        /// (<c>PipBuck.as:386-402</c>) and must not end its context either.</summary>
        [Test]
        public void Supply_ShowingTheErrorOverlay_DoesNotCloseTheCurrentPage()
        {
            _manager.OnOff(4);

            _manager.Supply(-1);

            Assert.That(_vend.HideCount, Is.GreaterThanOrEqualTo(1), "The overlay hides the page.");
            Assert.That(_vend.ClosedCount, Is.EqualTo(0),
                "The power overlay hid the trade page and killed its vendor with it.");
        }

        /// <summary>The other half, so the guard is not satisfied by a reset that never fires: closing
        /// the device <b>must</b> reach every contextual page, including one that was not on screen.</summary>
        [Test]
        public void Close_ReachesEveryContextualPage_IncludingOneNotShowing()
        {
            _manager.OnOff(1);

            _manager.Close();

            Assert.That(_stat.ClosedCount, Is.EqualTo(1), "The device close must reach page 1.");
            Assert.That(_vend.ClosedCount, Is.EqualTo(1),
                "The device close must reach page 4 even though page 1 was showing — a stale trader " +
                "context from an interrupted camp visit has to go somewhere.");
        }

        /// <summary>
        /// How the open/close cycle adds up. This is the Oracle's own arithmetic, asserted so a later
        /// "optimisation" cannot quietly change it: <c>PipBuck.as:274-335</c> runs the close work in
        /// the <c>else</c> arm of <c>if (this.active)</c>, which a close request reaches whether or not
        /// the device <i>was</i> open — so two close requests are two deliveries, and each open/close
        /// cycle is exactly one.
        /// </summary>
        [Test]
        public void Close_DeliversOneEventPerCycle()
        {
            _manager.OnOff(4);
            _manager.Close();
            _manager.Close();
            Assert.That(_vend.ClosedCount, Is.EqualTo(2),
                "Two close requests should be two deliveries (PipBuck.as:318-335 runs the close work " +
                "unconditionally for a close request, not only when the device had been open).");

            _manager.OnOff(4);
            _manager.Close();
            Assert.That(_vend.ClosedCount, Is.EqualTo(3), "A further open/close cycle adds exactly one.");
        }

        /// <summary>A plain <see cref="IPipPage"/> that is not contextual must be left alone by the
        /// close sweep — it has no context to drop, and the manager must not need one.</summary>
        [Test]
        public void Close_LeavesNonContextualPagesAlone()
        {
            _manager.OnOff(1);

            var plain = new PlainPage(5);
            _manager.RegisterPage(plain);

            Assert.DoesNotThrow(() => _manager.Close(), "A page with no context must be a no-op to the sweep.");
            Assert.That(_manager.PageFor(5), Is.SameAs(plain), "The registration must still be intact.");
        }

        // ── The two sub-tabs that are not sub-tabs ────────────────────────────
        //
        // PipPageMed.as:247-252 and PipPageVend.as:478-484. PipSubTabs.NavigatesToPage states the rule
        // and PipSubTabsTests pins it as a pure function; these four pin that the DEVICE executes it,
        // which is the half that was missing — the rule was written down, tested, and consulted by
        // nobody, so clicking a doctor's Trading tab set page2 = 2 on a page with one sub-page and did
        // nothing at all.

        /// <summary>
        /// A doctor's "Trading" opens the merchant page — and opens it on <b>sub-page 1</b>, not on
        /// whatever tab the merchant page was last left on.
        /// </summary>
        [Test]
        public void SubPageClick_OnTheDoctor_TradingTab_OpensTheTradePage()
        {
            _manager.OnOff(PipPageMedView.MedPageId);

            _manager.SubPageClick(2);

            Assert.That(_manager.CurrentPage, Is.EqualTo(PipPageVendView.VendorPageId),
                "A doctor's second sub-tab is the merchant page (PipPageMed.as:247-252).");
            Assert.That(_vend.LastSubPage, Is.EqualTo(1),
                "The page navigated TO must land on its first sub-page: PipPageMed.as:250 resets page2 " +
                "to 1 before the onoff, so the trade page cannot open on the tab that navigated away.");
            Assert.That(_med.LastSubPage, Is.EqualTo(0),
                "The clinic page must not have been asked to draw sub-page 2 — it has only one.");
        }

        /// <summary>
        /// A <c>doc</c>'s "Repair" opens the clinic — the mirror image, and the doctor's only route back
        /// to its own treatment list once the player is on the trade page.
        /// </summary>
        [Test]
        public void SubPageClick_OnADocVendor_RepairTab_OpensTheClinic()
        {
            _vend.Context = new PipSubTabContext("doc_npc", "doc", null, false);
            _manager.OnOff(PipPageVendView.VendorPageId);

            _manager.SubPageClick(3);

            Assert.That(_manager.CurrentPage, Is.EqualTo(PipPageMedView.MedPageId),
                "A doc's Repair tab is the clinic (PipPageVend.as:478-484).");
            Assert.That(_med.LastSubPage, Is.EqualTo(1), "The clinic opens on its first sub-page.");
        }

        /// <summary>
        /// The same click on a <b>plain</b> vendor stays put. Without this the redirect could be
        /// "everyone's Repair tab opens the clinic", which would be a worse bug than the one it fixed.
        /// </summary>
        [Test]
        public void SubPageClick_OnAPlainVendor_RepairTab_StaysOnTheVendor()
        {
            _vend.Context = new PipSubTabContext("vend_npc", "v", null, false);
            _manager.OnOff(PipPageVendView.VendorPageId);

            _manager.SubPageClick(3);

            Assert.That(_manager.CurrentPage, Is.EqualTo(PipPageVendView.VendorPageId),
                "Only `npcInter == \"doc\"` redirects; a plain vending machine repairs in place.");
            Assert.That(_vend.LastSubPage, Is.EqualTo(3));
        }

        /// <summary>
        /// A page with no context at all — a stub, or a page that is not contextual — must fall through
        /// to the ordinary sub-page path rather than throwing on the pattern match.
        /// </summary>
        [Test]
        public void SubPageClick_OnANonContextualPage_DoesNotRedirect()
        {
            _manager.OnOff(1);

            _manager.SubPageClick(3);

            Assert.That(_manager.CurrentPage, Is.EqualTo(1), "The character sheet's Perks tab is Perks.");
            Assert.That(_stat.LastSubPage, Is.EqualTo(3));
        }
    }
}
