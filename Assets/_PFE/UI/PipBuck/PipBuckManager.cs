using System;
using System.Collections.Generic;
using UnityEngine;
using PFE.Core;
using PFE.UI.Common;

namespace PFE.UI.PipBuck
{
    /// <summary>
    /// The PipBuck device's state machine — the port of <c>fe.inter.PipBuck</c>.
    ///
    /// <para><b>What this class is, and what it is not.</b> It owns navigation and nothing else: which
    /// page is showing, which tab is lit, whether the world is suspended. It does <b>not</b> own a
    /// <c>RectTransform</c>, a sprite or a <c>Text</c> — <see cref="PipBuckView"/> renders whatever
    /// this class publishes. That split is what makes the navigation rules (the ones with the fiddly
    /// branches) testable without an editor, which matters because the editor is owner-only here.</para>
    ///
    /// <para><b>It ticks, and it must keep ticking while the world is frozen.</b> The Oracle calls
    /// <c>pip.step()</c> from <c>World.step()</c> under <c>allStat &gt;= 1</c>
    /// (<c>World.as:1315-1320</c>) while the room step needs <c>allStat == 1</c> — so an open PipBuck
    /// freezes the world and keeps its own animation running. This class therefore registers at
    /// <see cref="SimTickOrder.ViewAligned"/>, the bucket <see cref="SimLoop"/> deliberately does not
    /// gate. See <see cref="InterfacePauseGate"/>.</para>
    /// </summary>
    public sealed class PipBuckManager : IAutoRegisteredSimTickable, IDisposable
    {
        // ── The Oracle's page table ───────────────────────────────────────────
        //
        // PipBuck.as:111 —
        //   pages = [null, Stat, Inv, Info, Vend, Opt, Med, Work, App, Vault]
        // Index is the page id. Kept as a named map rather than an enum so a page can be missing
        // (not yet ported) without the table lying about what id it would occupy.

        /// <summary><c>PipBuck.kolPages</c> (<c>PipBuck.as:41</c>) — pages 1..5 have a permanent tab.</summary>
        public const int TopPageCount = 5;

        /// <summary>The highest page id in the Oracle's table, <c>PipPageVault</c>.</summary>
        public const int MaxPageId = 9;

        /// <summary>
        /// The "open, but do not disturb an already-open device" argument. <c>PipBuck.as:238-241</c>
        /// returns early when the device is active, and otherwise falls through to the
        /// <c>param1 &gt; 0</c> branch — so <c>onoff(11)</c> opens the device and <c>onoff(11)</c> on an
        /// open device does nothing. The Oracle uses it from <c>onDeactivate</c> (<c>World.as:771</c>)
        /// and <c>resizeScreen</c> (<c>World.as:808</c>), both of which want "make sure it is open"
        /// without stealing the player's current page.
        /// </summary>
        public const int OpenIfClosed = 11;

        private readonly PipBuckViewModel _vm;
        private readonly UiSoundService _sound;
        private readonly InterfacePauseGate _pause;

        /// <summary>Pages by id, 1..9. A null slot is a page that has not been ported yet.</summary>
        private readonly IPipPage[] _pages = new IPipPage[MaxPageId + 1];

        private readonly Dictionary<int, string> _tabLabels = new Dictionary<int, string>();

        public PipBuckManager(PipBuckViewModel viewModel, UiSoundService sound, InterfacePauseGate pause)
        {
            _vm = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            _sound = sound;
            _pause = pause ?? throw new ArgumentNullException(nameof(pause));
        }

        /// <summary>The state this device publishes. Read by <see cref="PipBuckView"/>.</summary>
        public PipBuckViewModel ViewModel => _vm;

        /// <summary>Oracle <c>active</c>.</summary>
        public bool IsOpen => _vm.Active.Value;

        /// <summary>Oracle <c>page</c>.</summary>
        public int CurrentPage => _vm.Page.Value;

        /// <summary>
        /// The world gate, so a caller (and a test) can assert that opening the device suspends the
        /// simulation. Exposed rather than hidden because "the game froze when I pressed Tab" is a
        /// behaviour the play-test checklist has to be able to confirm.
        /// </summary>
        public InterfacePauseGate PauseGate => _pause;

        // ── ISimTickable ──────────────────────────────────────────────────────

        /// <summary>
        /// <see cref="SimTickOrder.ViewAligned"/> — the interface bucket. This is the one order value
        /// <see cref="SimLoop"/> does not suppress while the world is suspended, which is precisely
        /// what <c>World.as:1315</c> gives <c>pip.step()</c>.
        /// </summary>
        public int TickOrder => SimTickOrder.ViewAligned;

        /// <summary>The port of <c>PipBuck.step()</c> (<c>PipBuck.as:613-619</c>).</summary>
        public void SimTick(int tickIndex)
        {
            Step();
        }

        /// <summary>
        /// One interface tick. Runs whether or not the world is suspended — that asymmetry is the
        /// Oracle's, not an oversight.
        /// </summary>
        public void Step()
        {
            // PipBuck.as:615 — `if(this.currentPage) this.currentPage.step();`
            CurrentPageInstance?.Step();
        }

        // ── Page registry ─────────────────────────────────────────────────────

        /// <summary>
        /// Registers a page. Called by <see cref="PipBuckView"/> while it builds, and safe to call
        /// repeatedly — the last registration for an id wins, so a rebuilt shell does not double up.
        /// </summary>
        public void RegisterPage(IPipPage page)
        {
            if (page == null) return;

            if (page.PageId < 1 || page.PageId > MaxPageId)
            {
                Debug.LogError($"[PipBuckManager] Page id {page.PageId} is outside 1..{MaxPageId}. " +
                               "See IPipPage for the Oracle's page table (PipBuck.as:111).");
                return;
            }

            _pages[page.PageId] = page;
        }

        /// <summary>Sets the tab label for a page id, from the Oracle's string table.</summary>
        public void SetTabLabel(int pageId, string label)
        {
            _tabLabels[pageId] = label;
        }

        /// <summary>The label a tab shows, or empty when none has been set.</summary>
        public string TabLabel(int pageId)
        {
            return _tabLabels.TryGetValue(pageId, out string label) ? label : string.Empty;
        }

        /// <summary>The page for an id, or null when that page is not ported.</summary>
        public IPipPage PageFor(int pageId)
        {
            if (pageId < 1 || pageId > MaxPageId) return null;
            return _pages[pageId];
        }

        private IPipPage CurrentPageInstance => PageFor(_vm.Page.Value);

        // ── The state machine ─────────────────────────────────────────────────

        /// <summary>
        /// The port of <c>PipBuck.onoff</c> (<c>PipBuck.as:235-355</c>) — the single entry point every
        /// caller uses, including the hotkeys, the tab clicks and the contextual interactions.
        /// </summary>
        /// <param name="page">
        /// <b>0</b> toggles the device; <b>&gt; 0</b> opens it (a value &lt; 10 selects that page, and
        /// 10..11 means "open, but reset to page 1 if we were on a contextual page");
        /// <b>&lt; 0</b> closes it. <c>PipBuck.as:242-270</c>.
        /// </param>
        /// <param name="subPage">
        /// The sub-page to land on, or 0 to leave the current one. Forwarded to
        /// <see cref="SetPage"/>.
        /// </param>
        public void OnOff(int page = 0, int subPage = 0)
        {
            // PipBuck.as:237 — the device is not waiting for a favourite key once anything happens.
            _vm.ReqKey.Value = false;

            // PipBuck.as:238-241 — the early return that makes `OpenIfClosed` mean what it says.
            if (IsOpen && page == OpenIfClosed)
            {
                return;
            }

            bool opening;
            bool wasOpen = IsOpen;

            if (page == 0)
            {
                // PipBuck.as:242-250 — toggle.
                opening = !wasOpen;
                if (_vm.Page.Value >= 4 && !_vm.Light.Value)
                {
                    // A contextual page does not survive a close/reopen cycle.
                    _vm.Page.Value = 1;
                }
                Play(3);
            }
            else if (page > 0)
            {
                // PipBuck.as:251-262.
                if (page < 10)
                {
                    _vm.Page.Value = page;
                }
                else if (_vm.Page.Value >= 4)
                {
                    _vm.Page.Value = 1;
                }
                opening = true;
            }
            else
            {
                // PipBuck.as:263-270 — closing. The sound only plays if it was actually open, so a
                // spurious close is silent. The Oracle plays it here and NOT in the toggle branch's
                // close half, which is why `onoff(-1)` and `onoff(0)` do not sound the same.
                if (wasOpen)
                {
                    Play(3);
                }
                opening = false;
            }

            // PipBuck.as:271-274 — opening the device in a non-base land arms the travel flag.
            if (!_vm.Light.Value)
            {
                _vm.Travel.Value = true;
            }

            // PipBuck.as:275-281 — the contextual tab. Tab 4 is hidden by default and relabelled to
            // the page that opened the device. Note the condition: page 4 itself, or 6..9. Page 5
            // (options) is NOT contextual, so it never takes the tab.
            bool contextual = page == 4 || (page >= 6 && page <= 9);
            _vm.ContextualTab.Value = contextual ? page : 0;

            _vm.OpenContext.Value = opening ? page : 0;
            _vm.Active.Value = opening;

            if (opening)
            {
                // PipBuck.as:283-315.
                _vm.ShowHidden.Value = false;

                // The Oracle clears the controller's held keys and closes the stand/terminal here
                // (World.w.ctr.clearAll(), World.w.stand.onoff(-1)) so a key held from gameplay does
                // not leak into the menu. Ported as a hook: the port's input layer reads edges, not a
                // held-key table, so there is nothing to clear yet. See the I7 notes.
                OnOpened();

                SetPage(subPage);
            }
            else
            {
                // PipBuck.as:316-335.
                OnClosed();
            }

            // PipBuck.as:336-340 — in normal mode the HUD effects strip is refreshed and the status
            // panel is shown. `pr` is hidden only in light mode.
            if (!_vm.Light.Value)
            {
                _vm.SupplyError.Value = false;
            }

            SetButtons();

            // PipBuck.as:342-345.
            if (!_vm.Light.Value)
            {
                _vm.Travel.Value = false;
            }

            // PipBuck.as:346-353 — the error overlay. `pipOff` means the device has no power; the port
            // has no power system yet, so this always takes the `supply(1)` branch.
            Supply(1);

            // PipBuck.as:354 — the Oracle drops the "a key was pressed" latch so the keystroke that
            // opened the device is not also consumed as a page action.
            _vm.ReqKey.Value = false;

            // ── The world freeze ──────────────────────────────────────────────
            //
            // Not in `onoff` as a call — the Oracle sets `allStat` one step later, in World.step
            // (World.as:1385), and the room step then skips (World.as:1257-1270). Applied here so the
            // freeze is atomic with the state change rather than one tick late.
            if (opening)
            {
                _pause.Acquire(this);
            }
            else
            {
                _pause.Release(this);
            }
        }

        /// <summary>
        /// The port of <c>PipBuck.setPage</c> (<c>PipBuck.as:404-438</c>).
        /// </summary>
        public void SetPage(int subPage = 0)
        {
            // PipBuck.as:407-416 — in normal mode the page re-reads the player's live inventory, so a
            // page shown after a weapon swap shows the new numbers. The port's pages read the live
            // inventory themselves, so there is no cached `money` to refresh here.

            // The title strip is cleared BEFORE the page runs, not after.
            //
            // PipPage.setStatus clears it as its first act (`this.pip.vis.toptext.visible = false`,
            // PipPage.as:937) and then the page's own setStatus calls setTopText(...) to write it
            // (`PipPage.as:1496-1511`, e.g. PipPageInv.as:60). So in the Oracle the page has the LAST
            // word. An earlier version of this method wrote the title after `current.SetStatus(...)`,
            // which meant every page's title was immediately overwritten with the empty string
            // BuildTopText returns — no page could title itself, and the failure looked exactly like
            // "this page has no title" rather than "the shell ate it".
            _vm.TopText.Value = BuildTopText();

            // PipBuck.as:417-423 — every page is hidden, then the active one is shown. Hiding all of
            // them first is what makes a page's own visibility bookkeeping unnecessary.
            for (int id = 1; id <= MaxPageId; id++)
            {
                _pages[id]?.SetVisible(false);
            }

            IPipPage current = CurrentPageInstance;

            // PipBuck.as:424-432.
            if (current != null)
            {
                if (subPage > 0)
                {
                    _vm.SubPage.Value = subPage;
                }

                current.SetStatus(subPage);
                current.SetVisible(true);
            }
            else
            {
                // A page that has not been ported. The shell draws its "not implemented" notice; the
                // navigation still works, so the tab bar is testable before the pages exist.
                if (subPage > 0)
                {
                    _vm.SubPage.Value = subPage;
                }
            }
        }

        /// <summary>
        /// The port of <c>PipBuck.pageClick</c> (<c>PipBuck.as:182-196</c>) — a top tab was clicked.
        /// </summary>
        /// <param name="pageId">The tab's id, which is also the page id.</param>
        public void PageClick(int pageId)
        {
            // PipBuck.as:184-191 — two refusals. `ctr.setkeyOn` means the key-capture popup is up, and
            // `gg.pipOff` means the player's device is disabled. Neither exists in the port yet, so
            // both are hooks rather than conditions; see OnOpened for the same note.
            if (IsInputCaptureActive())
            {
                return;
            }

            if (IsDeviceDisabled())
            {
                return;
            }

            _vm.Page.Value = pageId;
            SetPage();
            SetButtons();
            Play(2);
        }

        /// <summary>Closes the device. <c>PipBuck.pipClose</c> (<c>PipBuck.as:198-205</c>).</summary>
        public void Close()
        {
            if (IsInputCaptureActive())
            {
                return;
            }

            OnOff(-1);
        }

        /// <summary>
        /// The port of <c>PipPage.page2Click</c> (<c>PipPage.as:883-892</c>) — a sub-tab was clicked.
        ///
        /// <para><b>This is not <see cref="SetPage"/>, and the difference is load-bearing.</b>
        /// <c>page2Click</c> assigns <c>page2</c>, calls <c>setStatus()</c> and plays sound 2. It does
        /// <i>not</i> go through <c>PipBuck.setPage</c>: no page is hidden or re-shown, the title strip
        /// is not reset, and the main tab frames are not recomputed — the player has not changed page,
        /// only moved inside one. Routing a sub-tab click through <see cref="SetPage"/> would work by
        /// accident and would silently reset the device's title on every sub-tab press.</para>
        ///
        /// <para><b>The scroll reset is the Oracle's, not the port's.</b> <c>setStatus()</c> is called
        /// with no argument, so its <c>param1 = true</c> default applies and the page's scroll returns
        /// to the top (<c>PipPage.as:927-930</c>). Keeping the offset would land the player mid-list on
        /// a sub-page with different contents. Each page applies that to its own scroll; the manager
        /// only has to pass the argument along.</para>
        /// </summary>
        /// <param name="subPage">The sub-tab's number, 1..5.</param>
        public void SubPageClick(int subPage)
        {
            if (subPage < 1 || subPage > PipSubTabs.MaxSubTabs) return;

            // PipPage.page2Click's own guard, and the same one pageClick has: the key-capture popup
            // owns the keyboard while it is up. Always false until the popup lands; see
            // IsInputCaptureActive.
            if (IsInputCaptureActive())
            {
                return;
            }

            _vm.SubPage.Value = subPage;

            // The page's own setStatus, not this class's SetPage — see the method summary.
            CurrentPageInstance?.SetStatus(subPage);

            // PipPage.as:891 — `this.pip.snd(2)`, the item click, which is the same sound a tab press
            // makes. Note the Oracle plays it *before* the page's work on the two pages that navigate
            // away; the port plays it after, because a page that navigated would have replaced the
            // current page by then.
            Play(2);
        }

        /// <summary>
        /// The port of <c>PipBuck.setButtons</c> (<c>PipBuck.as:207-228</c>) — publishes which tab is
        /// lit. The Oracle drives <c>gotoAndStop(2)</c> on the active tab's clip; the port publishes
        /// the active id and lets the view pick the frame.
        /// </summary>
        public void SetButtons()
        {
            // PipBuck.as:211-227. The loop runs 0..kolPages inclusive, so it covers but0 (Close) as
            // well as but1..but5 — and but0 lights when the page is 0, which never happens, so the
            // Close tab is permanently on frame 1. The special case below is the interesting one.
            _vm.ContextualTab.Value = ComputeContextualTab();

            // The active tab, or 0 when no tab owns the current page.
            _vm.ActiveTab.Value = _vm.Page.Value <= TopPageCount ? _vm.Page.Value : 0;
        }

        private int ComputeContextualTab()
        {
            // PipBuck.as:222-225 — tab 4 lights when the current page is one of the contextual ones,
            // even though the lit tab's id (4) is not the page id. Preserved exactly: `but4` shows
            // active while the Vault/Vendor/Workbench/Doctor page is up.
            int page = _vm.Page.Value;
            if (page == 4 || (page >= 6 && page <= 9))
            {
                return 4;
            }

            return 0;
        }

        /// <summary>
        /// The port of <c>PipBuck.supply</c> (<c>PipBuck.as:386-402</c>) — the "no power" overlay.
        /// </summary>
        /// <param name="state">Negative shows the error, non-negative clears it.</param>
        public void Supply(int state = -1)
        {
            if (state < 0)
            {
                _vm.SupplyError.Value = true;
                CurrentPageInstance?.SetVisible(false);
            }
            else
            {
                _vm.SupplyError.Value = false;
            }
        }

        /// <summary>
        /// The port of <c>PipBuck.snd</c> (<c>PipBuck.as:230-234</c>): <c>Snd.ps("pip" + n, …, pipVol)</c>.
        ///
        /// <para>The three ids are not interchangeable — <b>1</b> is the navigation blip, <b>2</b> the
        /// click, <b>3</b> the device open/close clunk — and <see cref="UiSoundService"/> already names
        /// them by role. Routing through the named helpers rather than a raw id keeps that mapping in
        /// one place, which is where the Oracle's <c>Snd.as</c> table lives too.</para>
        /// </summary>
        public void Play(int index)
        {
            if (_sound == null) return;

            switch (index)
            {
                case 1: _sound.PlayTabBeep(); break;
                case 2: _sound.PlayItemClick(); break;
                case 3: _sound.PlayDeviceToggle(); break;
                default:
                    Debug.LogWarning($"[PipBuckManager] No PipBuck sound for index {index}. " +
                                     "The Oracle only defines pip1, pip2 and pip3 (PipBuck.as:232).");
                    break;
            }
        }

        private string BuildTopText()
        {
            // PipBuck.as:937 and :1502-1509 — the title strip is written by the page, not by the
            // device: PipPage.setTopText hides it, and the page's own setStatus decides the string.
            // The device only clears it. Returned empty here so the strip starts hidden rather than
            // showing a stale title from the previous page.
            return string.Empty;
        }

        // ── Hooks for Oracle behaviour the port cannot yet express ────────────
        //
        // These are methods rather than inline comments so the gap is greppable and each one has a
        // single place to land. Every one of them corresponds to real Oracle work, and none of them
        // is a no-op "for safety": they exist because the dependency is genuinely absent.

        /// <summary>
        /// <c>PipBuck.as:285-314</c> — on open the Oracle switches the software cursor, resets the
        /// vendor, clears the controller's held keys, closes the stand/terminal, hides the world
        /// interaction label, caps the message timer and zeroes the dialogue and notice alphas.
        ///
        /// <para><b>Ported: nothing yet.</b> Each item needs a system the port does not have wired:
        /// the cursor is Item I1's <c>CursorManager</c> (imported, not yet driven from here), the
        /// interaction label is I5, and the dialogue window is I13. Recorded rather than silently
        /// skipped because "the cursor did not change when I opened the PipBuck" is otherwise
        /// indistinguishable from a broken cursor manager.</para>
        /// </summary>
        private void OnOpened()
        {
        }

        /// <summary>
        /// <c>PipBuck.as:316-335</c> — on close the Oracle writes the config if a save was pending,
        /// clears the vendor and NPC id, detaches the appearance customiser and restores the dialogue
        /// and notice alphas.
        ///
        /// <para><b>Ported: nothing yet</b>, for the same reason as <see cref="OnOpened"/> — every one
        /// of those owners is a later work item. The one that matters for I9 is the vendor/NPC reset,
        /// which is a no-op while those pages are stubs.</para>
        /// </summary>
        private void OnClosed()
        {
        }

        /// <summary>
        /// <c>Ctr.as:718</c> — <c>World.w.pip.reqKey</c> means the device is waiting for a keystroke to
        /// bind as a favourite, and the controller must not treat that keystroke as gameplay input.
        ///
        /// <para>Always false for now: the port has no key-capture popup (<c>visSetKey</c>, Item I7's
        /// own leftover), so the state can never be entered. Reading the view-model rather than
        /// returning a literal keeps this correct the moment the popup lands.</para>
        /// </summary>
        private bool IsInputCaptureActive()
        {
            return _vm.ReqKey.Value;
        }

        /// <summary>
        /// <c>PipBuck.as:188</c> — <c>World.w.gg.pipOff</c>: the player's device is disabled (a scripted
        /// sequence, or the rat-race). The port has no such flag, so this is always false.
        /// </summary>
        private bool IsDeviceDisabled()
        {
            return false;
        }

        public void Dispose()
        {
            // Never leave the world suspended because the device was destroyed while open. A hold
            // leaked here would freeze the simulation with nothing on screen to explain it — the
            // failure mode InterfacePauseGate's holder set makes recoverable.
            _pause?.Release(this);
        }
    }
}
