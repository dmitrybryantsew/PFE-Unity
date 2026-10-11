using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using PFE.Data.Definitions;
using PFE.Systems.Map;
using PFE.Systems.Map.Actions;

namespace PFE.Tests.Editor.Map.Actions
{
    /// <summary>
    /// Tests for the port of AS3's <c>Interact.act()</c> switch (<c>Interact.as:889</c>).
    ///
    /// <para>The property that matters most here is the <b>four-way</b> outcome, because the caller's
    /// fallback depends on telling them apart: an object with no script should open its door, an object
    /// whose script is unported should keep behaving as it does today, and an object whose script
    /// <i>declined</i> must do nothing at all.</para>
    /// </summary>
    [TestFixture]
    public class ObjectActionDispatcherTests
    {
        /// <summary>An action that records whether it ran, and can be told to decline.</summary>
        private sealed class RecordingAction : IObjectAction
        {
            public string ActionId { get; }
            public bool CanRun;
            public bool ExecuteResult;
            public int ExecuteCount;

            public RecordingAction(string id, bool canRun = true, bool executeResult = true)
            {
                ActionId = id;
                CanRun = canRun;
                ExecuteResult = executeResult;
            }

            public bool CanExecute(in ObjectActionContext context) => CanRun;

            public bool Execute(in ObjectActionContext context)
            {
                ExecuteCount++;
                return ExecuteResult;
            }
        }

        private static ObjectActionContext Context(string allact, string objectId = "indoor2")
        {
            var obj = new ObjectInstance { objectId = objectId, definitionId = objectId };
            if (allact != null)
            {
                obj.attributes.Add(new MapObjectAttributeData { key = "allact", value = allact });
            }

            var room = new RoomInstance
            {
                id = "room_0_0",
                width = WorldConstants.ROOM_WIDTH,
                height = WorldConstants.ROOM_HEIGHT
            };

            return new ObjectActionContext(room, obj, null, new Vector3(3f, 4f, 0f));
        }

        [Test]
        public void Dispatch_ObjectWithNoAllAct_IsNotApplicable()
        {
            var reported = new List<string>();
            var dispatcher = new ObjectActionDispatcher(reported.Add);

            Assert.AreEqual(ObjectActionOutcome.NotApplicable, dispatcher.Dispatch(Context(null)));
            Assert.AreEqual(0, reported.Count, "An object with no script is not a missing handler.");
        }

        [Test]
        public void Dispatch_ObjectWithEmptyAllAct_IsNotApplicable()
        {
            var dispatcher = new ObjectActionDispatcher(_ => { });

            Assert.AreEqual(ObjectActionOutcome.NotApplicable, dispatcher.Dispatch(Context("")));
            Assert.AreEqual(ObjectActionOutcome.NotApplicable, dispatcher.Dispatch(Context("   ")));
        }

        [Test]
        public void Dispatch_UnregisteredAllAct_IsUnhandledAndNamesTheId()
        {
            var reported = new List<string>();
            var dispatcher = new ObjectActionDispatcher(reported.Add);

            Assert.AreEqual(ObjectActionOutcome.Unhandled, dispatcher.Dispatch(Context("hack_robot", "term1")));

            Assert.AreEqual(1, reported.Count);
            StringAssert.Contains("hack_robot", reported[0]);
            StringAssert.Contains("term1", reported[0], "The notice should name where it was first seen.");
        }

        [Test]
        public void Dispatch_UnregisteredAllAct_IsReportedOncePerIdNotPerObject()
        {
            var reported = new List<string>();
            var dispatcher = new ObjectActionDispatcher(reported.Add);

            dispatcher.Dispatch(Context("hack_robot", "term1"));
            dispatcher.Dispatch(Context("hack_robot", "term2"));
            dispatcher.Dispatch(Context("hack_robot", "term3"));
            dispatcher.Dispatch(Context("hack_lock", "term1"));

            Assert.AreEqual(2, reported.Count, "One notice per distinct id — otherwise a room full of terminals floods the log.");
            CollectionAssert.AreEquivalent(new[] { "hack_robot", "hack_lock" }, dispatcher.ReportedUnhandled);
        }

        [Test]
        public void Dispatch_RegisteredAndRunnable_IsHandledAndExecutes()
        {
            var reported = new List<string>();
            var dispatcher = new ObjectActionDispatcher(reported.Add);
            var action = new RecordingAction("comein");
            dispatcher.Register(action);

            Assert.AreEqual(ObjectActionOutcome.Handled, dispatcher.Dispatch(Context("comein")));
            Assert.AreEqual(1, action.ExecuteCount);
            Assert.AreEqual(0, reported.Count);
        }

        [Test]
        public void Dispatch_ResolvesAllActFromTheDefinitionWhenThePlacementHasNone()
        {
            // The shipped camp case, and the regression this pins. Every Z door in RoomsCamp.as places
            // indoor2 with no allact — <obj id="indoor2" code="BJ1whp5k1LO1jk6w" x="16" y="15"
            // locktip="0" lock="1" uid="doorRBL1"/> (:115) — and authors it on the definition instead
            // (AllData.as:4917, allact='comein'). Reading the placement alone answered NotApplicable, so
            // DoorPropPresenter fell through to opening the door and the backroom stayed unreachable.
            //
            // Note the Context() helper above cannot express this: it always injects allact as a
            // placement attribute, which is exactly the assumption that hid the bug.
            var reported = new List<string>();
            var dispatcher = new ObjectActionDispatcher(reported.Add);
            var action = new RecordingAction("comein");
            dispatcher.Register(action);

            var obj = new ObjectInstance
            {
                objectId = "indoor2",
                definitionId = "indoor2",
                attributes = new List<MapObjectAttributeData>
                {
                    new MapObjectAttributeData { key = "lock", value = "1" },
                    new MapObjectAttributeData { key = "locktip", value = "0" }
                },
                definition = new MapObjectDefinition
                {
                    objectId = "indoor2",
                    legacyAttributes = new List<MapObjectAttributeData>
                    {
                        new MapObjectAttributeData { key = "allact", value = "comein" }
                    }
                }
            };

            var room = new RoomInstance
            {
                id = "room_0_0",
                width = WorldConstants.ROOM_WIDTH,
                height = WorldConstants.ROOM_HEIGHT
            };

            var context = new ObjectActionContext(room, obj, null, new Vector3(3f, 4f, 0f));

            Assert.AreEqual("comein", context.ActionId);
            Assert.AreEqual(ObjectActionOutcome.Handled, dispatcher.Dispatch(in context));
            Assert.AreEqual(1, action.ExecuteCount);
            Assert.AreEqual(0, reported.Count);
        }

        [Test]
        public void Dispatch_PlacementAllActStillOverridesTheDefinition()
        {
            // The complement, so the fix above cannot be "read the definition instead of the placement".
            // AS3 assigns the definition first (Interact.as:287-289) and the placed node last
            // (:383-385), so the placement wins when it is present — the doorboss case, where the room
            // XML itself says allact="comein" (RoomsSerial.as:127).
            var obj = new ObjectInstance
            {
                objectId = "doorboss",
                definitionId = "doorboss",
                attributes = new List<MapObjectAttributeData>
                {
                    new MapObjectAttributeData { key = "allact", value = "open" }
                },
                definition = new MapObjectDefinition
                {
                    objectId = "doorboss",
                    legacyAttributes = new List<MapObjectAttributeData>
                    {
                        new MapObjectAttributeData { key = "allact", value = "comein" }
                    }
                }
            };

            var room = new RoomInstance
            {
                id = "room_0_0",
                width = WorldConstants.ROOM_WIDTH,
                height = WorldConstants.ROOM_HEIGHT
            };

            Assert.AreEqual("open", new ObjectActionContext(room, obj, null, Vector3.zero).ActionId);
        }

        [Test]
        public void Dispatch_RegisteredButDeclining_IsRefusedAndNeverExecutes()
        {
            var dispatcher = new ObjectActionDispatcher(_ => { });
            var action = new RecordingAction("comein", canRun: false);
            dispatcher.Register(action);

            Assert.AreEqual(ObjectActionOutcome.Refused, dispatcher.Dispatch(Context("comein")));
            Assert.AreEqual(0, action.ExecuteCount);
        }

        [Test]
        public void Dispatch_ExecuteThatChangedNothing_IsRefusedNotHandled()
        {
            // A handler that ran but reported "nothing happened" must not be reported as Handled, or
            // the caller would skip the door's own behaviour on a transition that never occurred.
            var dispatcher = new ObjectActionDispatcher(_ => { });
            var action = new RecordingAction("comein", canRun: true, executeResult: false);
            dispatcher.Register(action);

            Assert.AreEqual(ObjectActionOutcome.Refused, dispatcher.Dispatch(Context("comein")));
            Assert.AreEqual(1, action.ExecuteCount);
        }

        [Test]
        public void Dispatch_Refused_IsNotReportedAsUnhandled()
        {
            // This is the distinction DoorPropPresenter branches on. If a refusal were also reported as
            // unhandled, the caller could not tell it from an unported script, and the door presenter's
            // fallback would open a Z door onto a layer that is not there.
            var reported = new List<string>();
            var dispatcher = new ObjectActionDispatcher(reported.Add);
            dispatcher.Register(new RecordingAction("comein", canRun: false));

            Assert.AreEqual(ObjectActionOutcome.Refused, dispatcher.Dispatch(Context("comein")));
            Assert.AreEqual(0, reported.Count);
        }

        [Test]
        public void Register_TwiceForTheSameId_ThrowsAndNamesTheId()
        {
            var dispatcher = new ObjectActionDispatcher(_ => { });
            dispatcher.Register(new RecordingAction("comein"));

            var error = Assert.Throws<InvalidOperationException>(
                () => dispatcher.Register(new RecordingAction("comein")));

            StringAssert.Contains("comein", error.Message);
        }

        [Test]
        public void Register_WithABlankActionId_Throws()
        {
            var dispatcher = new ObjectActionDispatcher(_ => { });

            Assert.Throws<ArgumentException>(() => dispatcher.Register(new RecordingAction("   ")));
        }

        [Test]
        public void Register_WithANullAction_Throws()
        {
            var dispatcher = new ObjectActionDispatcher(_ => { });

            Assert.Throws<ArgumentNullException>(() => dispatcher.Register(null));
        }

        [Test]
        public void Handles_TrimsSurroundingWhitespaceButDoesNotFoldCase()
        {
            // AS3 compares `allact` with == against lowercase literals (Interact.as:1619) and the
            // imported data is lowercase, so folding case would accept ids the oracle would not match.
            var dispatcher = new ObjectActionDispatcher(_ => { });
            dispatcher.Register(new RecordingAction("comein"));

            Assert.IsTrue(dispatcher.Handles("  comein  "));
            Assert.IsFalse(dispatcher.Handles("ComeIn"));
            Assert.IsFalse(dispatcher.Handles(null));
            Assert.IsFalse(dispatcher.Handles(""));
        }

        [Test]
        public void CreateDefault_RegistersComeIn()
        {
            var dispatcher = ObjectActionDispatcher.CreateDefault(new FakeLayerTransition());

            Assert.IsTrue(dispatcher.Handles(ComeInAction.Id));
            CollectionAssert.Contains(new List<string>(dispatcher.RegisteredActionIds), "comein");
        }

        [Test]
        public void CreateDefault_DispatchesComeInThroughTheLayerTransition()
        {
            var transition = new FakeLayerTransition();
            var dispatcher = ObjectActionDispatcher.CreateDefault(transition);

            Assert.AreEqual(ObjectActionOutcome.Handled, dispatcher.Dispatch(Context("comein")));
            Assert.AreEqual(1, transition.ToggleCalls);
        }

        [Test]
        public void CreateDefault_WithATransition_RegistersComeInAndReportsNothing()
        {
            var reported = new List<string>();
            var dispatcher = ObjectActionDispatcher.CreateDefault(
                new FakeLayerTransition(), reported.Add, new FakeTravelMapHost(),
                new FakeLandScriptHost(), new FakeProbRoomHost());

            Assert.IsTrue(dispatcher.Handles(ComeInAction.Id));
            Assert.AreEqual(0, reported.Count,
                "Every capability is supplied, so there is nothing to warn about.");
        }

        [Test]
        public void CreateDefault_WithNoTransition_LeavesComeInUnregisteredAndNamesTheReason()
        {
            // A scene with no RoomTransitionManager must not throw out of DoorPropPresenter.Interact.
            // `comein` stays unregistered, so the caller keeps its own fallback, and the missing wiring
            // is named rather than looking like an unported branch.
            //
            // Every OTHER capability IS supplied here so the count is about the transition alone —
            // otherwise this test would pass or fail on an unrelated warning (see the map and exit tests
            // below for those).
            var reported = new List<string>();
            var dispatcher = ObjectActionDispatcher.CreateDefault(
                null, reported.Add, new FakeTravelMapHost(),
                new FakeLandScriptHost(), new FakeProbRoomHost());

            Assert.IsFalse(dispatcher.Handles(ComeInAction.Id));
            Assert.AreEqual(1, reported.Count);
            StringAssert.Contains("IRoomLayerTransition", reported[0]);

            // And the door presenter's decision is unchanged from before this feature existed.
            Assert.AreEqual(ObjectActionOutcome.Unhandled, dispatcher.Dispatch(Context("comein")));
        }

        [Test]
        public void CreateDefault_WithBothCapabilities_RegistersComeInAndMap()
        {
            var dispatcher = ObjectActionDispatcher.CreateDefault(
                new FakeLayerTransition(), _ => { }, new FakeTravelMapHost());

            Assert.IsTrue(dispatcher.Handles(ComeInAction.Id));
            Assert.IsTrue(dispatcher.Handles(MapAction.Id));
            CollectionAssert.Contains(new List<string>(dispatcher.RegisteredActionIds), "map");
        }

        [Test]
        public void CreateDefault_DispatchesMapThroughTheTravelHost()
        {
            // The camp's wall map (`wmap`, allact='map') reaching the host is the whole of gap §1's
            // second half: before this, the object was reported as unhandled and interacting with it
            // did nothing at all.
            var host = new FakeTravelMapHost();
            var dispatcher = ObjectActionDispatcher.CreateDefault(
                new FakeLayerTransition(), _ => { }, host);

            Assert.AreEqual(ObjectActionOutcome.Handled, dispatcher.Dispatch(Context("map")));
            Assert.AreEqual(1, host.GrantAndOpenCalls);
            Assert.IsTrue(host.TravelUnlocked);
        }

        [Test]
        public void CreateDefault_WithNoTravelMapHost_LeavesMapUnregisteredAndNamesTheReason()
        {
            // Same degradation rule as `comein`: a scene without the campaign must not throw out of an
            // interaction, and the missing wiring must be named so it cannot read as an unported branch.
            // The other three capabilities are supplied so the count isolates the map host.
            var reported = new List<string>();
            var dispatcher = ObjectActionDispatcher.CreateDefault(
                new FakeLayerTransition(), reported.Add, travelMapHost: null,
                exitHost: new FakeLandScriptHost(), probHost: new FakeProbRoomHost());

            Assert.IsFalse(dispatcher.Handles(MapAction.Id));
            Assert.AreEqual(1, reported.Count);
            StringAssert.Contains("ITravelMapHost", reported[0]);

            Assert.AreEqual(ObjectActionOutcome.Unhandled, dispatcher.Dispatch(Context("map")));
        }

        // =============================================================================================
        //  `exit` — Interact.as:1646-1649 -> Game.gotoNextLevel (Game.as:464-472)
        // =============================================================================================

        [Test]
        public void CreateDefault_RegistersExitAndDispatchesItThroughTheLandScriptHost()
        {
            var host = new FakeLandScriptHost();
            var dispatcher = ObjectActionDispatcher.CreateDefault(
                new FakeLayerTransition(), _ => { }, new FakeTravelMapHost(), host);

            Assert.IsTrue(dispatcher.Handles(ExitAction.Id));

            Assert.AreEqual(ObjectActionOutcome.Handled, dispatcher.Dispatch(Context("exit", "exit")));
            Assert.AreEqual(1, host.GotoNextLevelCalls);
            Assert.AreEqual(0, host.UpLandLevelCalls,
                "`exit` is only the level advance. The landStage increment is the exit room's `upland` " +
                "(RoomsProb.as:exit_plant), a different object in a different room.");
        }

        [Test]
        public void CreateDefault_WithNoLandScriptHost_LeavesExitUnregisteredAndNamesTheReason()
        {
            // The honest degradation: an unwired `exit` must not throw out of an interaction, and it must
            // not read as an unported branch either — the exit box would otherwise open like an ordinary
            // door and the descent loop would look like it worked while never advancing.
            var reported = new List<string>();
            var dispatcher = ObjectActionDispatcher.CreateDefault(
                new FakeLayerTransition(), reported.Add, new FakeTravelMapHost(),
                exitHost: null, probHost: new FakeProbRoomHost());

            Assert.IsFalse(dispatcher.Handles(ExitAction.Id));
            Assert.AreEqual(1, reported.Count);
            StringAssert.Contains("ILandScriptHost", reported[0]);

            Assert.AreEqual(ObjectActionOutcome.Unhandled, dispatcher.Dispatch(Context("exit", "exit")));
        }

        // =============================================================================================
        //  The prob branch — Interact.as:1558-1565, tested BEFORE the allact switch
        // =============================================================================================

        /// <summary>
        /// An object that carries BOTH a <c>prob</c> and an <c>allact</c> — which is exactly the
        /// bottom-row exit box, and exactly the shape the ordering exists for.
        /// </summary>
        private static ObjectActionContext ProbContext(
            string probId, string allact = null, Vector3? worldPosition = null)
        {
            var obj = new ObjectInstance { objectId = "exit", definitionId = "exit" };
            obj.attributes.Add(new MapObjectAttributeData { key = "prob", value = probId });
            if (allact != null)
            {
                obj.attributes.Add(new MapObjectAttributeData { key = "allact", value = allact });
            }

            var room = new RoomInstance
            {
                id = "room_0_0",
                width = WorldConstants.ROOM_WIDTH,
                height = WorldConstants.ROOM_HEIGHT
            };

            return new ObjectActionContext(room, obj, null, worldPosition ?? new Vector3(7f, 9f, 0f));
        }

        [Test]
        public void Dispatch_ObjectWithAProb_EntersTheProbRoomInsteadOfRunningItsAllAct()
        {
            // THE regression this whole change exists for. The bottom-row exit box carries
            // prob='exit_plant' AND inherits allact='exit' (AllData.as:5016), so testing allact first
            // would call gotoNextLevel immediately, skip the detached exit room, skip its `upland`, and
            // rebuild the land at the SAME landStage for ever — while the level visibly regenerated, so
            // nothing would look wrong.
            var host = new FakeLandScriptHost();
            var probHost = new FakeProbRoomHost();
            var dispatcher = ObjectActionDispatcher.CreateDefault(
                new FakeLayerTransition(), _ => { }, new FakeTravelMapHost(), host, probHost);

            Assert.AreEqual(ObjectActionOutcome.Handled, dispatcher.Dispatch(ProbContext("exit_plant", "exit")));

            CollectionAssert.AreEqual(new[] { "exit_plant" }, probHost.EnteredProbIds);
            Assert.AreEqual(0, host.GotoNextLevelCalls,
                "The prob branch is the oracle's `if`, the allact chain is its `else` — they are exclusive.");
        }

        [Test]
        public void Dispatch_ObjectWithAProb_HandsOverTheDoorsOwnPositionNotThePlayers()
        {
            // AS3 passes this.owner.X / this.owner.Y as param2/param3 (Interact.as:1565), and
            // Land.gotoProb prefers that pair over gg.X/gg.Y when both are non-negative
            // (Land.as:1417-1424). The saved point is where the player is put back, so handing over the
            // player's position would drop them inside the door they just used.
            var probHost = new FakeProbRoomHost();
            var dispatcher = ObjectActionDispatcher.CreateDefault(
                new FakeLayerTransition(), _ => { }, new FakeTravelMapHost(),
                new FakeLandScriptHost(), probHost);

            var doorPosition = new Vector3(12.5f, -3.25f, 0f);
            dispatcher.Dispatch(ProbContext("labirint", worldPosition: doorPosition));

            CollectionAssert.AreEqual(new[] { doorPosition }, probHost.EnteredFromPositions);
        }

        [Test]
        public void Dispatch_EmptyProbAttribute_FallsThroughToTheAllAct()
        {
            // RoomPopulator.PlaceReturnDoor writes prob='' on the doorout box ON PURPOSE: AS3 guards the
            // assignment with `.length()` (Interact.as:391-393), so an empty value leaves the field null
            // and the entry branch is skipped, letting the box's own allact='probreturn' (AllData.as:5017)
            // run. Reading the attribute's presence instead of its length would send the return door into
            // a prob room named "" — the same trap as lesson #134.
            var probHost = new FakeProbRoomHost { IsInProbRoomValue = true };
            var dispatcher = ObjectActionDispatcher.CreateDefault(
                new FakeLayerTransition(), _ => { }, new FakeTravelMapHost(),
                new FakeLandScriptHost(), probHost);

            Assert.AreEqual(ObjectActionOutcome.Handled, dispatcher.Dispatch(ProbContext("", "probreturn")));

            Assert.AreEqual(0, probHost.EnteredProbIds.Count);
            Assert.AreEqual(1, probHost.ReturnCalls, "The return door's own allact is what runs.");
        }

        [Test]
        public void Dispatch_ProbWithNoHost_IsUnhandledAndReportedUnderTheProbId()
        {
            // An unwired prob host must not let a prob door fall through to opening like a box, and it
            // must not answer NotApplicable either — the object HAS a script (Interact.as:1315 gates on
            // `allact || prob != null`), so the honest answer is "unhandled, reported".
            var reported = new List<string>();
            var dispatcher = ObjectActionDispatcher.CreateDefault(
                new FakeLayerTransition(), reported.Add, new FakeTravelMapHost(),
                new FakeLandScriptHost(), probHost: null);

            // Entry 0 is the composition notice from CreateDefault; entry 1 is the dispatch notice.
            Assert.AreEqual(1, reported.Count);
            StringAssert.Contains("IProbRoomHost", reported[0]);

            Assert.AreEqual(ObjectActionOutcome.Unhandled, dispatcher.Dispatch(ProbContext("labirint")));

            Assert.AreEqual(2, reported.Count);
            StringAssert.Contains(ObjectActionDispatcher.ProbEntryReportId, reported[1]);
        }

        [Test]
        public void Dispatch_ProbHostThatCannotEnter_IsRefusedNotFallenThrough()
        {
            // AS3's ativateLoc guard (Land.as:1237-1240): a prob whose room was never built returns
            // false and moves nobody, and the caller must then NOT open the door — a prob door that
            // cannot open its room is a data problem, not an ordinary box.
            var probHost = new FakeProbRoomHost { CanEnterProbValue = false };
            var dispatcher = ObjectActionDispatcher.CreateDefault(
                new FakeLayerTransition(), _ => { }, new FakeTravelMapHost(),
                new FakeLandScriptHost(), probHost);

            Assert.AreEqual(ObjectActionOutcome.Refused, dispatcher.Dispatch(ProbContext("labirint")));
            Assert.AreEqual(0, probHost.EnteredProbIds.Count);
        }

        [Test]
        public void Dispatch_ProbReturn_IsRefusedWhenNothingWasSaved()
        {
            // The oracle guards the return on `this.loc.landProb != ""` (Interact.as:1569) — "the room I
            // am in IS a prob room". With no saved return point the door must decline, not teleport
            // anybody anywhere.
            var probHost = new FakeProbRoomHost { IsInProbRoomValue = false };
            var dispatcher = ObjectActionDispatcher.CreateDefault(
                new FakeLayerTransition(), _ => { }, new FakeTravelMapHost(),
                new FakeLandScriptHost(), probHost);

            Assert.AreEqual(ObjectActionOutcome.Refused, dispatcher.Dispatch(ProbContext("", "probreturn")));
            Assert.AreEqual(0, probHost.ReturnCalls);

            probHost.IsInProbRoomValue = true;
            Assert.AreEqual(ObjectActionOutcome.Handled, dispatcher.Dispatch(ProbContext("", "probreturn")));
            Assert.AreEqual(1, probHost.ReturnCalls);
        }

        [Test]
        public void Dispatch_Prob_DoesNotConsultTheAllActRegistryAtAll()
        {
            // The complement, so the ordering fix above cannot be "try the prob host and then also run
            // the allact". An object carrying an UNREGISTERED allact alongside a prob must produce no
            // "unhandled" notice — proving the switch was never reached.
            var reported = new List<string>();
            var probHost = new FakeProbRoomHost();
            var dispatcher = ObjectActionDispatcher.CreateDefault(
                new FakeLayerTransition(), reported.Add, new FakeTravelMapHost(),
                new FakeLandScriptHost(), probHost);

            dispatcher.Dispatch(ProbContext("labirint", "hack_robot"));

            Assert.AreEqual(0, reported.Count);
            Assert.IsEmpty(dispatcher.ReportedUnhandled);
        }
    }
}
