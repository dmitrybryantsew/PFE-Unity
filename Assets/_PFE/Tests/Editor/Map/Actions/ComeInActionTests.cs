using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using PFE.Systems.Map;
using PFE.Systems.Map.Actions;

namespace PFE.Tests.Editor.Map.Actions
{
    /// <summary>
    /// Tests for <c>allact == "comein"</c> — the "Z door".
    ///
    /// <para>Oracle: <c>Interact.as:1619-1621</c> → <c>outLoc(5, this.X, this.Y)</c> →
    /// <c>Land.gotoLoc(5, x, y)</c>, where case 5 flips z only (<c>Land.as:1330-1332</c>) and takes the
    /// arrival point from the passed coordinates (<c>:1367-1371</c>).</para>
    /// </summary>
    [TestFixture]
    public class ComeInActionTests
    {
        private static ObjectActionContext Context(
            GameObject user = null,
            Vector3 worldPosition = default)
        {
            var obj = new ObjectInstance { objectId = "indoor2", definitionId = "indoor2" };
            obj.attributes.Add(new MapObjectAttributeData { key = "allact", value = ComeInAction.Id });

            var room = new RoomInstance
            {
                id = "room_0_0",
                width = WorldConstants.ROOM_WIDTH,
                height = WorldConstants.ROOM_HEIGHT
            };

            return new ObjectActionContext(room, obj, user, worldPosition);
        }

        [Test]
        public void Constructor_WithNoTransition_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new ComeInAction(null));
        }

        [Test]
        public void ActionId_IsComein()
        {
            Assert.AreEqual("comein", new ComeInAction(new FakeLayerTransition()).ActionId);
        }

        [Test]
        public void CanExecute_MirrorsWhetherTheOppositeLayerIsReachable()
        {
            var transition = new FakeLayerTransition { CanToggleLayerValue = false };
            var action = new ComeInAction(transition);

            Assert.IsFalse(action.CanExecute(Context()), "AS3 returns null from gotoLoc when locs[x][y][z] is empty.");

            transition.CanToggleLayerValue = true;
            Assert.IsTrue(action.CanExecute(Context()));
        }

        [Test]
        public void Execute_WhenTheLayerIsUnreachable_DoesNotTouchTheTransition()
        {
            var transition = new FakeLayerTransition { CanToggleLayerValue = false };

            Assert.IsFalse(new ComeInAction(transition).Execute(Context()));
            Assert.AreEqual(0, transition.ToggleCalls);
        }

        [Test]
        public void Execute_ArrivesAtTheTriggerNotAtThePlayer()
        {
            // The reason ObjectActionContext carries WorldPosition at all. AS3 spawns the player at the
            // door's own X/Y, which differs from the player's position by the interaction reach — so a
            // test that passed the player's position would pass with the wrong rule.
            var transition = new FakeLayerTransition();
            var player = new GameObject("ComeInTestPlayer");
            player.transform.position = new Vector3(99f, 99f, 0f);

            try
            {
                var context = Context(player, new Vector3(3.5f, 4.5f, 0f));

                Assert.IsTrue(new ComeInAction(transition).Execute(context));
                Assert.AreEqual(1, transition.ToggleCalls);
                Assert.AreSame(player, transition.LastPlayer);
                Assert.AreEqual(new Vector3(3.5f, 4.5f, 0f), transition.LastWorldPosition);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(player);
            }
        }

        [Test]
        public void Execute_ReportsWhatTheTransitionDid()
        {
            var transition = new FakeLayerTransition { ToggleResult = false };
            var action = new ComeInAction(transition);

            Assert.IsFalse(action.Execute(Context()), "A refused toggle must not report success.");

            transition.ToggleResult = true;
            Assert.IsTrue(action.Execute(Context()));
        }

        [Test]
        public void ThroughTheDispatcher_AnUnreachableLayerIsRefusedRatherThanOpened()
        {
            // The end-to-end shape the door presenter relies on: no handler ran, no toggle attempted,
            // and nothing reported as unhandled — so the caller stops instead of opening the door.
            var transition = new FakeLayerTransition { CanToggleLayerValue = false };
            var reported = new List<string>();
            var dispatcher = new ObjectActionDispatcher(reported.Add);
            dispatcher.Register(new ComeInAction(transition));

            Assert.AreEqual(ObjectActionOutcome.Refused, dispatcher.Dispatch(Context()));
            Assert.AreEqual(0, transition.ToggleCalls);
            Assert.AreEqual(0, reported.Count);
        }

        [Test]
        public void ThroughTheDispatcher_AReachableLayerIsHandled()
        {
            // Complement to the test above: without this, a dispatcher that refused everything would
            // pass the refusal test.
            var transition = new FakeLayerTransition();
            var dispatcher = new ObjectActionDispatcher(_ => { });
            dispatcher.Register(new ComeInAction(transition));

            Assert.AreEqual(ObjectActionOutcome.Handled, dispatcher.Dispatch(Context()));
            Assert.AreEqual(1, transition.ToggleCalls);
        }
    }
}
