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
            var dispatcher = ObjectActionDispatcher.CreateDefault(new FakeLayerTransition(), reported.Add);

            Assert.IsTrue(dispatcher.Handles(ComeInAction.Id));
            Assert.AreEqual(0, reported.Count);
        }

        [Test]
        public void CreateDefault_WithNoTransition_LeavesComeInUnregisteredAndNamesTheReason()
        {
            // A scene with no RoomTransitionManager must not throw out of DoorPropPresenter.Interact.
            // `comein` stays unregistered, so the caller keeps its own fallback, and the missing wiring
            // is named rather than looking like an unported branch.
            var reported = new List<string>();
            var dispatcher = ObjectActionDispatcher.CreateDefault(null, reported.Add);

            Assert.IsFalse(dispatcher.Handles(ComeInAction.Id));
            Assert.AreEqual(1, reported.Count);
            StringAssert.Contains("IRoomLayerTransition", reported[0]);

            // And the door presenter's decision is unchanged from before this feature existed.
            Assert.AreEqual(ObjectActionOutcome.Unhandled, dispatcher.Dispatch(Context("comein")));
        }
    }
}
