using NUnit.Framework;
using PFE.Systems.Map.Rendering;

namespace PFE.Tests.Editor.Map.Rendering
{
    /// <summary>
    /// Tests for <see cref="RoomObjectVisualManager.UsesDoorPresenter"/> — the rule that decides whether a
    /// room object is <b>interactable at all</b>.
    ///
    /// <para><b>Why this rule gets its own fixture.</b> <c>DoorPropPresenter</c> is the project's only
    /// interaction surface, so anything this predicate rejects cannot be interacted with — it gets an
    /// <c>ObjectColliderDebugPresenter</c>, which implements nothing. The rule has been wrong twice:</para>
    /// <list type="number">
    /// <item>it admitted only <c>objectType=="door"</c> / family Door / a visual id starting with
    /// <c>"door"</c>, so the seven <c>allact='comein'</c> Z doors missed all three and pressing E on the
    /// camp's backroom door did nothing;</item>
    /// <item>family Transition was added for those, but the camp's wall map (<c>wmap</c>,
    /// <c>allact='map'</c>, <c>tip='box'</c>, family Furniture, visual <c>viswmap</c>) missed all three of
    /// the same tests — so the camp could not send the player to a land.</item>
    /// </list>
    ///
    /// <para>Both times the answer was a list of <i>shapes</i> and the question was "does this object carry
    /// a script we can run". Nothing went red either time. The predicate is now a pure function taking
    /// strings and bools precisely so a test can make it go red.</para>
    /// </summary>
    [TestFixture]
    public class DoorPresenterAdmissionTests
    {
        /// <summary>A dispatcher that registers exactly <paramref name="ids"/>.</summary>
        private static System.Func<string, bool> Handles(params string[] ids)
        {
            var dispatcher = new PFE.Systems.Map.Actions.ObjectActionDispatcher(_ => { });
            foreach (string id in ids)
            {
                dispatcher.Register(new StubAction(id));
            }

            return dispatcher.Handles;
        }

        private sealed class StubAction : PFE.Systems.Map.Actions.IObjectAction
        {
            public string ActionId { get; }

            public StubAction(string id) { ActionId = id; }

            public bool CanExecute(in PFE.Systems.Map.Actions.ObjectActionContext context) => true;

            public bool Execute(in PFE.Systems.Map.Actions.ObjectActionContext context) => true;
        }

        [Test]
        public void ADoorObjectTypeIsAdmitted()
        {
            Assert.IsTrue(RoomObjectVisualManager.UsesDoorPresenter("door", false, null, null, null));
        }

        [Test]
        public void FamilyDoorOrTransitionIsAdmitted()
        {
            // The seven Z doors: type "box", family Transition, visual visindoor2, allact comein. The
            // dispatcher clause is deliberately told "nothing is registered" so this test can only pass
            // on the family clause.
            Assert.IsTrue(RoomObjectVisualManager.UsesDoorPresenter("box", true, "visindoor2", "comein", Handles()));
        }

        [Test]
        public void AVisualIdStartingWithDoorIsAdmitted()
        {
            Assert.IsTrue(RoomObjectVisualManager.UsesDoorPresenter("box", false, "doorboss", null, null));
        }

        [Test]
        public void TheCampsWallMapIsAdmittedByItsScript()
        {
            // The regression guard for the second failure. wmap's shape is exactly the one the other three
            // clauses reject: type "box", family Furniture, visual "viswmap". If this goes red, the camp
            // cannot send the player to a land.
            Assert.IsTrue(RoomObjectVisualManager.UsesDoorPresenter("box", false, "viswmap", "map", Handles("comein", "map")));
        }

        [Test]
        public void TheWallMapIsNotAdmittedWithoutARegisteredHandler()
        {
            // Positive control for the test above: it proves the wall map is admitted by the *dispatcher*
            // clause and not by something else in the chain. With `map` unregistered the answer flips.
            Assert.IsFalse(RoomObjectVisualManager.UsesDoorPresenter("box", false, "viswmap", "map", Handles("comein")));
        }

        [Test]
        public void AnUnportedAllActIsNotAdmitted()
        {
            // This is the assertion that keeps the change narrow. Admitting "has any allact" would give
            // these objects a presenter, Dispatch would report them unhandled, and Interact would fall
            // through to ToggleOpen() — so E on a terminal or a bench would start toggling it open.
            Assert.IsFalse(RoomObjectVisualManager.UsesDoorPresenter("box", false, "visterm", "hack_robot", Handles("comein", "map")));
            Assert.IsFalse(RoomObjectVisualManager.UsesDoorPresenter("box", false, "visstand", "stand", Handles("comein", "map")));
        }

        [Test]
        public void AnObjectWithNeitherShapeNorScriptIsNotAdmitted()
        {
            // A plain prop: a container, a piece of scenery. It must keep the debug presenter.
            Assert.IsFalse(RoomObjectVisualManager.UsesDoorPresenter("box", false, "vissafe", null, Handles("comein", "map")));
            Assert.IsFalse(RoomObjectVisualManager.UsesDoorPresenter("box", false, "vissafe", string.Empty, Handles("comein", "map")));
        }

        [Test]
        public void ACheckpointIsAdmittedByItsFamily()
        {
            // AS3 gives a checkpoint its own Interact with `actFun = activate` (CheckPoint.as:86-90) — it
            // carries no allact, and its visual id (`vischeckpoint`) does not start with "door". So only
            // the family clause can admit it; without that it fell to ObjectColliderDebugPresenter and
            // pressing E did nothing at all.
            Assert.IsTrue(RoomObjectVisualManager.UsesDoorPresenter(
                "checkpoint", true, "vischeckpoint", null, Handles("comein", "map")));
        }

        [Test]
        public void ACheckpointWithNoFamilyIsNotAdmitted()
        {
            // Control for the test above: the *family* admits a checkpoint, not the objectType string.
            // "checkpoint" is not in the objectType clause and the visual id does not start with "door",
            // so without the family the object keeps the debug presenter — the state it was actually in.
            Assert.IsFalse(RoomObjectVisualManager.UsesDoorPresenter(
                "checkpoint", false, "vischeckpoint", null, Handles("comein", "map")));
        }

        [Test]
        public void TheDispatcherIsNotConsultedWhenAShapeClauseAlreadyAdmits()
        {
            // Laziness is load-bearing: the caller passes a lambda that builds its dispatcher on first
            // use, and every door in every room would otherwise pay for one it never consults.
            bool consulted = false;

            bool admitted = RoomObjectVisualManager.UsesDoorPresenter(
                "door", false, null, "map",
                _ => { consulted = true; return true; });

            Assert.IsTrue(admitted);
            Assert.IsFalse(consulted, "A shape clause admitted it, so the dispatcher must not have been asked.");
        }

        [Test]
        public void TheDispatcherIsNotConsultedWhenThereIsNoScript()
        {
            bool consulted = false;

            bool admitted = RoomObjectVisualManager.UsesDoorPresenter(
                "box", false, "vissafe", null,
                _ => { consulted = true; return true; });

            Assert.IsFalse(admitted);
            Assert.IsFalse(consulted, "No allact means no question to ask.");
        }
    }
}
