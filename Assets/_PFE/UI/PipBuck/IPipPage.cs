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
}
