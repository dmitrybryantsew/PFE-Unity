using System;

namespace PFE.Core
{
    /// <summary>
    /// The one seam between "a world interaction wants a PipBuck page" and the device itself.
    ///
    /// <para><b>Why a router and not a message.</b> Two of the three callers have no injection point:
    /// <see cref="PFE.Systems.Map.Actions.TerminalActionHost"/> is built by a static factory in a
    /// MonoBehaviour that owns no container (<c>DoorPropPresenter.CreateTerminalHost</c>), and
    /// <see cref="PFE.Systems.Inventory.PlayerInventory"/> is a MonoBehaviour assembled in
    /// <c>Awake</c> with one constructor argument. MessagePipe's brokers are registered in the lifetime
    /// scope and reachable only by injection, so a message would have needed a publisher threaded
    /// through both — a wider change than the feature. The project already exposes exactly this kind of
    /// static seam for the same reason (<c>CampaignManager.Current</c>,
    /// <c>ObjectInteractionBroadcast.Current</c>, <c>SecurityRuntime.PlayerObject</c>).</para>
    ///
    /// <para><b>What it carries.</b> The Oracle's <c>pip.onoff(page, subPage)</c> plus the two context
    /// fields those pages read: <c>pip.workTip</c> for page 7. A page with no context leaves it empty,
    /// and the subscriber decides what an empty context means — page 7 with no station shows its
    /// sub-tab row and nothing else, which is the truth.</para>
    ///
    /// <para><b>Null is the offline/test state</b> and every call site is null-safe: a bench that
    /// cannot open a page reports it, it does not throw.</para>
    /// </summary>
    public static class PipPageRouter
    {
        /// <summary>
        /// Handler signature: <c>(page, subPage, workTip)</c>. <paramref name="page"/> is the Oracle's
        /// page id (1..9); <paramref name="subPage"/> is <c>pip.onoff</c>'s second argument, where
        /// <b>0 means "leave the sub-page where it was"</b> (<c>PipBuck.as:427-430</c>).
        /// </summary>
        public static Action<int, int, string> Request;

        /// <summary>
        /// Ask the device to open a page — <c>World.w.pip.onoff(page, subPage)</c> with
        /// <c>pip.workTip</c> already written.
        /// </summary>
        public static void Open(int page, int subPage = 0, string workTip = null)
        {
            Request?.Invoke(page, subPage, workTip ?? string.Empty);
        }

        /// <summary>Whether anything is listening — so a caller can report the gap instead of guessing.</summary>
        public static bool IsWired => Request != null;
    }
}
