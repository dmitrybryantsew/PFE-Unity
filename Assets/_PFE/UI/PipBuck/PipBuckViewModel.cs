using System;
using R3;

namespace PFE.UI.PipBuck
{
    /// <summary>
    /// The PipBuck device's observable state — one reactive property per field of <c>fe.inter.PipBuck</c>
    /// that a view needs to render.
    ///
    /// <para><b>Why a view-model at all, when the Oracle keeps these on the device object.</b> The
    /// Oracle's <c>PipBuck</c> is both the state machine and the view: it holds <c>page</c> and also
    /// calls <c>but0.gotoAndStop(2)</c>. In the port the two are separated because the shell has to be
    /// rebuilt when the canvas resizes while the state must survive that — and because a view that
    /// owns its own state cannot be tested without a <c>RectTransform</c>, which is the constraint
    /// this project's offline verification keeps running into.</para>
    ///
    /// <para><b>Names track the Oracle's fields</b>, not Unity's conventions, so a reader can diff this
    /// against <c>PipBuck.as</c> line by line. Where a name would be meaningless outside the Oracle
    /// (<c>noAct</c>, <c>reqKey</c>) the Oracle's name is kept and the doc comment explains it.</para>
    /// </summary>
    public sealed class PipBuckViewModel : IDisposable
    {
        // ── Visibility ────────────────────────────────────────────────────────

        /// <summary>Oracle <c>active</c> — the device is on screen and the world is suspended.</summary>
        public readonly ReactiveProperty<bool> Active = new(false);

        /// <summary>
        /// Oracle <c>light</c> — <b>true means the shell is hidden</b>: <c>skin</c> and <c>fon</c> are
        /// invisible and only the page content draws.
        ///
        /// <para>It reads backwards, and that is the Oracle's own naming (<c>PipBuck.as:17</c>). It is
        /// also <b>false in the shipped game</b>: <c>World.as:862</c> calls <c>pip.toNormalMode()</c>
        /// unconditionally during world construction, and <c>toNormalMode</c> sets
        /// <c>light = false</c> and reveals the shell. So the full bezel and tab bar are the authentic
        /// presentation, and the port defaults to the same.</para>
        /// </summary>
        public readonly ReactiveProperty<bool> Light = new(false);

        // ── Navigation ────────────────────────────────────────────────────────

        /// <summary>
        /// Oracle <c>page</c> — the active top-level page, <b>1..9</b>. Note it is a device-wide
        /// notion, not a tab index: pages 6..9 have no tab of their own and are reached contextually,
        /// which is why <see cref="ContextualTab"/> exists.
        /// </summary>
        public readonly ReactiveProperty<int> Page = new(1);

        /// <summary>
        /// Oracle <c>PipPage.page2</c> — the sub-page inside the active page. Assigned by
        /// <c>setPage(param2)</c> only when <c>param2 &gt; 0</c>, so a plain <c>onoff(1)</c> leaves the
        /// page wherever it was.
        /// </summary>
        public readonly ReactiveProperty<int> SubPage = new(1);

        /// <summary>
        /// Oracle <c>but4</c>'s label — <b>0 when the tab is hidden</b>, otherwise the page id it
        /// stands for. Tab 4 is the one tab whose meaning changes: it is relabelled to Vault (9),
        /// Vendor (4), Workbench (7) or Doctor (6) by <c>onoff</c> (<c>PipBuck.as:275-281</c>), and is
        /// hidden for every other page.
        /// </summary>
        public readonly ReactiveProperty<int> ContextualTab = new(0);

        /// <summary>
        /// The id of the tab currently drawn in its active frame, or <b>0</b> when no permanent tab
        /// owns the current page.
        ///
        /// <para>Two different things light tab 4, and the Oracle distinguishes them
        /// (<c>PipBuck.as:214-225</c>): <see cref="ContextualTab"/> decides whether tab 4 is
        /// <i>visible and labelled</i>, while this decides whether it is <i>lit</i>. A Vault page
        /// lights tab 4 without tab 4 being the current page id, so the two cannot be collapsed into
        /// one field.</para>
        /// </summary>
        public readonly ReactiveProperty<int> ActiveTab = new(1);

        /// <summary>
        /// Oracle <c>travel</c> — armed while the device is open and the player is standing in a
        /// <c>base</c> land, disarmed otherwise (<c>PipBuck.as:271-273</c>, <c>:342-345</c>).
        ///
        /// <para><b>The Oracle's flag has one writer, and in the port that writer is the campaign.</b>
        /// <c>CampaignManager.TravelUnlocked</c> is the copy the travel-map page gates its clicks on, and
        /// <c>RefreshTravelUnlockedForCurrentLand</c> is where the rule lives. This field mirrors it for
        /// the device's own shape; it is deliberately <i>not</i> read anywhere, so a caller that wants
        /// "can the player travel" must ask the campaign rather than this.</para>
        /// </summary>
        public readonly ReactiveProperty<bool> Travel = new(false);

        // ── Panel text ────────────────────────────────────────────────────────

        /// <summary>Oracle <c>helpText</c> — shown by the "?" button.</summary>
        public readonly ReactiveProperty<string> HelpText = new(string.Empty);

        /// <summary>
        /// Oracle <c>vis.butHelp.visible</c> — whether the "?" button is on screen at all.
        ///
        /// <para><b>It is not the same as "the tooltip is showing".</b> The Oracle sets this per page:
        /// <c>PipPage.setSubPages</c> hides it on entry to every page and only page 3's travel sub-page
        /// re-shows it (<c>PipPageInfo.as:89</c>, <c>:204</c>), because <c>helpWorld</c> is the only help
        /// string page 3 owns. The <i>tooltip</i> is a separate state, driven by the pointer being over
        /// the button.</para>
        /// </summary>
        public readonly ReactiveProperty<bool> HelpVisible = new(false);

        /// <summary>Oracle <c>massText</c> — shown by the weight button.</summary>
        public readonly ReactiveProperty<string> MassText = new(string.Empty);

        /// <summary>Oracle <c>toptext</c> — the page's title strip.</summary>
        public readonly ReactiveProperty<string> TopText = new(string.Empty);

        /// <summary>Oracle <c>supply(-1)</c>'s state — the "no power" overlay is showing.</summary>
        public readonly ReactiveProperty<bool> SupplyError = new(false);

        // ── Flags the pages read ──────────────────────────────────────────────

        /// <summary>Oracle <c>showHidden</c> — the inventory page's "show unavailable" toggle.</summary>
        public readonly ReactiveProperty<bool> ShowHidden = new(false);

        /// <summary>
        /// Oracle <c>reqKey</c> — the page is waiting for a key press to assign as a favourite.
        /// <c>Ctr.as:718-748</c> checks it before treating a keystroke as gameplay input.
        /// </summary>
        public readonly ReactiveProperty<bool> ReqKey = new(false);

        /// <summary>
        /// Oracle <c>noAct</c> — item actions are refused. Set while the device is open during a
        /// rat-race or a cutscene (<c>PipBuck.as:309-313</c>); the pages answer it with
        /// <c>gui.infoText("noAct")</c>.
        /// </summary>
        public readonly ReactiveProperty<bool> NoAct = new(false);

        /// <summary>
        /// Oracle <c>pipVol</c> (<c>PipBuck.as:77</c>) — the device's own volume, applied to all three
        /// of its sounds. Deliberately not 1.0: the PipBuck beeps are meant to sit under the game.
        /// </summary>
        public readonly ReactiveProperty<float> Volume = new(0.25f);

        /// <summary>
        /// The page id the <b>current</b> interaction opened, or 0 if the device was opened by Tab.
        /// The port's record of <c>onoff</c>'s <c>param1</c>, for the shell's title and for tests.
        /// </summary>
        public readonly ReactiveProperty<int> OpenContext = new(0);

        public void Dispose()
        {
            Active.Dispose();
            Light.Dispose();
            Page.Dispose();
            SubPage.Dispose();
            ContextualTab.Dispose();
            ActiveTab.Dispose();
            Travel.Dispose();
            HelpText.Dispose();
            HelpVisible.Dispose();
            MassText.Dispose();
            TopText.Dispose();
            SupplyError.Dispose();
            ShowHidden.Dispose();
            ReqKey.Dispose();
            NoAct.Dispose();
            Volume.Dispose();
            OpenContext.Dispose();
        }
    }
}
