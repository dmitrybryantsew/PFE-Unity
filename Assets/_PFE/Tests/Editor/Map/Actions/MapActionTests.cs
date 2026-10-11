using System;
using NUnit.Framework;
using UnityEngine;
using PFE.Systems.Map;
using PFE.Systems.Map.Actions;

namespace PFE.Tests.Editor.Map.Actions
{
    /// <summary>
    /// Tests for <c>allact == "map"</c> — the camp's wall map (<c>wmap</c>).
    ///
    /// <para>Oracle: <c>Interact.as:1636-1641</c>. Three lines, and the third is the interesting one —
    /// <c>pip.travel = true; pip.onoff(3,3); pip.travel = true;</c>. <c>onoff</c> ends in
    /// <c>setButtons()</c>, which recomputes <c>travel</c> from the land you are standing in
    /// (<c>PipBuck.as:342-345</c>), so the middle call <i>clears</i> the first line's grant whenever the
    /// wall map is not in a base land.</para>
    ///
    /// <para>These tests pin the <b>verb</b> — that <see cref="MapAction"/> calls the composite
    /// three-step member and not two single ones. <c>TravelMapHostTests</c> pins the other half: that the
    /// real host's composite really does leave the flag set outside a base land.</para>
    /// </summary>
    [TestFixture]
    public class MapActionTests
    {
        private static ObjectActionContext Context()
        {
            var obj = new ObjectInstance { objectId = "wmap", definitionId = "wmap" };
            obj.attributes.Add(new MapObjectAttributeData { key = "allact", value = MapAction.Id });

            var room = new RoomInstance
            {
                id = "room_0_0",
                width = WorldConstants.ROOM_WIDTH,
                height = WorldConstants.ROOM_HEIGHT
            };

            return new ObjectActionContext(room, obj, null, default);
        }

        [Test]
        public void Constructor_WithNoHost_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new MapAction(null));
        }

        [Test]
        public void ActionId_IsMap()
        {
            Assert.AreEqual("map", new MapAction(new FakeTravelMapHost()).ActionId);
        }

        [Test]
        public void CanExecute_WithAHost_IsTrue()
        {
            // AS3 puts no guard on the `map` branch: the map opens from anywhere, and the base-tip
            // recompute inside setButtons is what takes the grant back afterwards.
            Assert.IsTrue(new MapAction(new FakeTravelMapHost()).CanExecute(Context()));
        }

        [Test]
        public void Execute_UsesTheThreeStepVerbNotTheSingleGrant()
        {
            // The one assertion that catches the "tidy-up": `UnlockTravel()` followed by
            // `OpenTravelMap()` also *calls* the grant and the open, so a test that only counted
            // "something happened" would pass on the broken version too.
            var host = new FakeTravelMapHost();

            Assert.IsTrue(new MapAction(host).Execute(Context()));

            Assert.AreEqual(1, host.GrantAndOpenCalls);
            Assert.AreEqual(0, host.UnlockCalls,
                "Execute must not grant on its own — the grant and the re-grant are the host's job.");
            Assert.AreEqual(0, host.OpenCalls,
                "Execute must not open on its own — opening is what clears the grant.");
        }

        [Test]
        public void Execute_LeavesTravelUnlocked()
        {
            // The observable consequence of the verb choice. The double's composite models the
            // recompute, so two single calls would land here with TravelUnlocked == false.
            var host = new FakeTravelMapHost();

            new MapAction(host).Execute(Context());

            Assert.IsTrue(host.TravelUnlocked,
                "Interact.as:1640 — the trailing `travel = true` is what survives the recompute.");
        }

        [Test]
        public void ThroughTheDispatcher_TheWallMapIsHandled()
        {
            // The end-to-end shape the door presenter relies on. Before this action existed, `map` was
            // reported as unhandled and interacting with the camp's wall map did nothing.
            var host = new FakeTravelMapHost();
            var reported = new System.Collections.Generic.List<string>();
            var dispatcher = new ObjectActionDispatcher(reported.Add);
            dispatcher.Register(new MapAction(host));

            Assert.AreEqual(ObjectActionOutcome.Handled, dispatcher.Dispatch(Context()));
            Assert.AreEqual(1, host.GrantAndOpenCalls);
            Assert.AreEqual(0, reported.Count, "A handled id must not be reported as unhandled.");
        }
    }
}
