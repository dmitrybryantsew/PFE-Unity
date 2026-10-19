using UnityEngine;

namespace PFE.UI.PipBuck
{
    /// <summary>
    /// One page of the PipBuck — the port's seam for <c>fe.inter.PipPage</c> and its nine subclasses.
    ///
    /// <para><b>Why an interface and not nine concrete fields.</b> The Oracle holds
    /// <c>pages[1..9]</c> and drives the active one through three calls (<c>setStatus</c>,
    /// <c>step</c>, <c>vis.visible</c>). The port needs the same shape for two reasons: the shell has
    /// to work before any page is written (so the device opens and the tabs light up), and pages land
    /// in separate work items (I9 inventory, I10 stats, I11 info) — a shell that named
    /// <c>PipPageInvView</c> directly would have to be edited by each of them.</para>
    ///
    /// <para><b>Page ids are the Oracle's, not tab indices.</b> <c>pages</c> is
    /// <c>[null, Stat, Inv, Info, Vend, Opt, Med, Work, App, Vault]</c> (<c>PipBuck.as:111</c>), so
    /// <b>1</b> is the character sheet, <b>2</b> the inventory, <b>3</b> info/map/quests, <b>4</b> the
    /// vendor, <b>5</b> options, <b>6</b> the doctor, <b>7</b> the workbench, <b>8</b> appearance and
    /// <b>9</b> the container vault. Only 1..5 have a permanent tab.</para>
    /// </summary>
    public interface IPipPage
    {
        /// <summary>The Oracle's page index, 1..9. See the interface summary for the mapping.</summary>
        int PageId { get; }

        /// <summary>
        /// Whether this page exists yet. A page that is registered but not implemented reports false,
        /// and the shell then shows <see cref="PipBuckView"/>'s "not implemented" notice rather than an
        /// empty screen — an empty screen is indistinguishable from a page that failed to load.
        /// </summary>
        bool IsImplemented { get; }

        /// <summary>
        /// The port of <c>PipPage.setStatus()</c> — rebuild the page's contents. Called by
        /// <c>setPage</c> every time the page becomes active, and by the pages themselves after an
        /// action changes the data.
        /// </summary>
        /// <param name="subPage">
        /// The requested sub-page, or 0 to leave it where it was. The Oracle's rule
        /// (<c>PipBuck.as:427-430</c>): <c>if (param1 &gt; 0) currentPage.page2 = param1;</c>.
        /// </param>
        void SetStatus(int subPage);

        /// <summary>The port of <c>PipPage.step()</c> — one 30 Hz tick while the device is open.</summary>
        void Step();

        /// <summary>Show or hide the page's root. The Oracle's <c>vis.visible</c>.</summary>
        void SetVisible(bool visible);

        /// <summary>Attach the page's GameObjects under the device's page root.</summary>
        void Build(RectTransform pageRoot);
    }

    /// <summary>
    /// A page whose contents depend on <i>who opened the device</i> — the four contextual pages
    /// (4 vendor, 6 doctor, 7 workbench, 9 vault), which a world interaction opens with a context and
    /// which must drop that context when the device closes.
    ///
    /// <para><b>Why this is not folded into <see cref="IPipPage.SetVisible"/>.</b> It was, and the
    /// result was the reported "trading shows no traders, and no Quests tab". <c>PipBuck.setPage</c>
    /// hides <b>every</b> page before showing the target (<c>PipBuck.as:417-423</c>), so opening page 4
    /// through <c>NPC.pip(4)</c> ran <c>SetVisible(false)</c> on page 4 itself — and that call cleared
    /// the vendor, the npcId and the npcInter that had just been handed to it. The page then rebuilt
    /// with no context: an empty shelf and, because <see cref="PipSubTabs.IsVisible"/> hides
    /// <c>vend4</c> when <c>npcId</c> is empty, no Quests tab at all.</para>
    ///
    /// <para>Visibility and lifetime are two different events. <c>vis.visible</c> is a page switch,
    /// which must preserve state; the device closing is what the Oracle's
    /// <c>pip.vendor = null; pip.npcId = null</c> (<c>PipBuck.as:323-324</c>) actually means.</para>
    /// </summary>
    public interface IPipContextPage
    {
        /// <summary>
        /// What opened the page — <c>pip.npcId</c> / <c>pip.npcInter</c> / <c>pip.workTip</c>.
        ///
        /// <para><b>Read by the device, not by the page.</b> <see cref="PipSubTabs.NavigatesToPage"/>
        /// needs the interaction kind to decide whether a sub-tab click leaves the page entirely — a
        /// <c>doc</c>'s Repair tab is the clinic, and any other vendor's is a repair list
        /// (<c>PipPageVend.as:478-484</c>). The page owns the context; the device owns the navigation
        /// that depends on it, so the value has to be readable from outside.</para>
        /// </summary>
        PipSubTabContext Context { get; }

        /// <summary>
        /// The device closed — drop the context that was handed to this page. Called from
        /// <c>PipBuckManager.OnClosed</c>, the port of <c>PipBuck.as:316-335</c>.
        /// </summary>
        void OnDeviceClosed();
    }
}
