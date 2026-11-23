using NUnit.Framework;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.Editor.Map
{
    /// <summary>
    /// Pins the boot order that keeps the <c>comein</c> (Z door) action registered.
    ///
    /// <para><b>The bug this exists to make unrepeatable.</b> <c>RoomObjectVisualManager</c> composes the
    /// <c>allact</c> dispatcher while it presents a room's objects — its admission test asks the
    /// dispatcher whether a script has a handler, and <c>ApplyPresenterVisual</c> then hands that same
    /// dispatcher to every <c>DoorPropPresenter</c>. <c>DoorPropPresenter.CreateSceneDispatcher</c>
    /// registers <c>comein</c> off <c>RoomTransitionManager.Instance</c>, and <c>MapBridge</c> creates
    /// that manager at boot. So if the visuals initialize <i>before</i> the manager exists, the
    /// dispatcher is composed with a null transition, left without <c>comein</c>, and cached for the
    /// room's whole lifetime: every Z door dispatches <c>comein</c>, gets <c>Unhandled</c>, falls through
    /// to <c>ToggleOpen()</c> — which a Z door cannot do, because <c>IsDoorBox</c> is false — and does
    /// nothing at all. Nothing threw and every fixture stayed green; the only evidence was a single
    /// warning line, "[ObjectAction] No IRoomLayerTransition is available, so `comein` is not
    /// registered".</para>
    ///
    /// <para><b>Why a source lint and not a behavioural test.</b> The behaviour needs a live scene and a
    /// running boot coroutine, so it cannot execute in this project's offline harness;
    /// <see cref="SourceLint"/> is the one form of guard that can actually fail here. The same reasoning
    /// is spelled out on <c>MotorGroundStateLintTests</c>.</para>
    ///
    /// <para><b>Thread G note.</b> The visuals-Initialize call moved behind
    /// <c>RoomPresentationCoordinator.InitializeRoom</c> in the Wave 2 MapBridge split; the invariant is
    /// unchanged — the transition manager must exist before the room is shown — so the lint's order
    /// assertion now spans the seam, with a forwarding test pinning that
    /// <c>InitializeRoom</c> really reaches <c>RoomVisualController.Initialize</c>.</para>
    /// </summary>
    [TestFixture]
    public sealed class MapBridgeBootOrderLintTests
    {
        /// <summary>The boot coroutine — the only place the dispatcher can be composed before the manager exists.</summary>
        private const string MapBridgePath = "Systems/Map/MapBridge.cs";

        /// <summary>The presentation half the boot coroutine now calls into (Wave 2, thread G).</summary>
        private const string CoordinatorPath = "Systems/Map/Rendering/RoomPresentationCoordinator.cs";

        [Test]
        public void Boot_CreatesTheRoomTransitionManager_BeforeTheVisualsThatComposeTheAllactDispatcher()
        {
            string mapBridge = SourceLint.ReadStripped(MapBridgePath);
            string boot = SourceLint.MethodBody(
                mapBridge, "private IEnumerator WaitForInitialization()");

            const string manager = "RoomTransitionManager.Instance";
            const string visuals = "_presentation.InitializeRoom(";

            // Both anchors must be present, or the order assertion below is satisfied by a -1 and the
            // lint silently becomes a no-op — the "green test that means nothing" this fixture exists
            // to avoid.
            Assert.That(managerAt(boot), Is.GreaterThanOrEqualTo(0),
                "MapBridge.WaitForInitialization no longer resolves RoomTransitionManager.Instance. If " +
                "that moved, move this guard with it; otherwise the order assertion below checks nothing.");

            Assert.That(visualsAt(boot), Is.GreaterThanOrEqualTo(0),
                "MapBridge.WaitForInitialization no longer shows the room through " +
                "RoomPresentationCoordinator.InitializeRoom. If that moved, move this guard with it; " +
                "otherwise the order assertion below checks nothing.");

            Assert.That(managerAt(boot), Is.LessThan(visualsAt(boot)),
                "MapBridge.WaitForInitialization now initializes the room BEFORE it creates the " +
                "RoomTransitionManager. RoomObjectVisualManager composes the `allact` dispatcher during " +
                "that Initialize, and DoorPropPresenter.CreateSceneDispatcher registers `comein` off " +
                "RoomTransitionManager.Instance — so with the manager absent the dispatcher is built " +
                "without `comein` and cached for the room's whole lifetime, and every Z door does " +
                "nothing. Create the manager first; the land-transition path already does.");

            int managerAt(string body) => body.IndexOf(manager, System.StringComparison.Ordinal);
            int visualsAt(string body) => body.IndexOf(visuals, System.StringComparison.Ordinal);
        }

        /// <summary>
        /// The order assertion above is only worth anything if <c>InitializeRoom</c> really forwards to
        /// <c>RoomVisualController.Initialize</c> — otherwise the seam could swallow the show call and
        /// the boot lint would keep passing on a room that never appears.
        /// </summary>
        [Test]
        public void InitializeRoom_ForwardsToTheVisualController()
        {
            string coordinator = SourceLint.ReadStripped(CoordinatorPath);

            string body = SourceLint.MethodBody(coordinator, "public void InitializeRoom(RoomInstance room)");

            // Null-conditional on purpose: the coordinator tolerates an unassigned visual controller
            // (MapBridge.Start reports it), so the forward is `_visuals?.Initialize(` — the lint
            // matches the call as written.
            Assert.That(body, Does.Contain("_visuals?.Initialize("),
                "RoomPresentationCoordinator.InitializeRoom no longer forwards to " +
                "RoomVisualController.Initialize. The boot-order lint in this fixture asserts on the " +
                "coordinator call, so a seam that stopped showing the room would leave that lint " +
                "checking a call that draws nothing — re-point this guard, do not delete it.");
        }
    }
}
