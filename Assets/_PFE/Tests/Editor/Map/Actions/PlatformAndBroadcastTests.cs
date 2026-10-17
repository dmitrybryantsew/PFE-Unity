using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using PFE.Data.Definitions;
using PFE.Systems.Map;
using PFE.Systems.Map.Actions;

namespace PFE.Tests.Editor.Map.Actions
{
    /// <summary>
    /// Fixtures for the platform-button mechanism: the placement <c>&lt;move&gt;</c> node, the state
    /// machine it drives, and the tagged broadcast that a button's <c>allact</c> lands on.
    ///
    /// <para><b>What these pin that a build cannot.</b> The oracle's default branch —
    /// <c>else { loc.allAct(owner, allact, allid) }</c> (<c>Interact.as:1645-1647</c>) — is reachable only
    /// through data: a button carrying <c>allact="move3" allid="d2"</c> and a platform carrying
    /// <c>allid="d2"</c> in the same room. Nothing in the type system connects them, so every rule here
    /// would be silent if wrong.</para>
    /// </summary>
    [TestFixture]
    public class PlatformAndBroadcastTests
    {
        private static RoomInstance MakeRoom()
        {
            return new RoomInstance
            {
                id = "test_room",
                width = WorldConstants.ROOM_WIDTH,
                height = WorldConstants.ROOM_HEIGHT
            };
        }

        private static ObjectInstance MakeObject(
            string id,
            Vector2 position,
            params (string key, string value)[] attrs)
        {
            var obj = new ObjectInstance { objectId = id, definitionId = id };
            obj.position = position;
            foreach ((string key, string value) in attrs)
            {
                obj.attributes.Add(new MapObjectAttributeData { key = key, value = value });
            }

            return obj;
        }

        // ── allid ─────────────────────────────────────────────────────────────────────────

        [Test]
        public void GetAllId_ReadsThePlacement()
        {
            ObjectInstance obj = MakeObject("door3", Vector2.zero, ("allid", "1"));

            Assert.AreEqual("1", obj.GetAllId());
        }

        [Test]
        public void GetAllId_Absent_IsEmpty()
        {
            ObjectInstance obj = MakeObject("door3", Vector2.zero, ("locktip", "0"));

            Assert.AreEqual(string.Empty, obj.GetAllId());
        }

        // ── move node ─────────────────────────────────────────────────────────────────────

        [Test]
        public void TryGetMoveNode_Absent_IsFalse()
        {
            ObjectInstance obj = MakeObject("door3", Vector2.zero);

            Assert.IsFalse(obj.TryGetMoveNode(out _));
        }

        [Test]
        public void TryGetMoveNode_FlattenedAttributes_AreRead()
        {
            // The shape the importer writes: <move dx="14" tstay="30" tmove="180"/> flattened.
            ObjectInstance obj = MakeObject(
                "platform2",
                new Vector2(9f * WorldConstants.TILE_SIZE, 5f * WorldConstants.TILE_SIZE),
                ("move_present", "1"),
                ("move_dx", "14"),
                ("move_tstay", "30"),
                ("move_tmove", "180"));

            Assert.IsTrue(obj.TryGetMoveNode(out MapObjectMoveData move));
            Assert.AreEqual(14f, move.Dx, 0.0001f);
            Assert.AreEqual(30, move.TStay);
            Assert.AreEqual(180, move.TMove);
            Assert.IsFalse(move.HasDy, "no @dy authored");
            Assert.IsFalse(move.StartsMoving, "no @on authored");
        }

        // ── platform state machine ────────────────────────────────────────────────────────

        private static MovingPlatformState MakePlatform(
            float x, float y, float dx, int tStay, int tMove)
        {
            ObjectInstance obj = MakeObject(
                "platform2",
                new Vector2(x, y),
                ("move_present", "1"),
                ("move_dx", dx.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("move_tstay", tStay.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("move_tmove", tMove.ToString(System.Globalization.CultureInfo.InvariantCulture)));

            obj.TryGetMoveNode(out MapObjectMoveData move);
            return new MovingPlatformState(obj, in move);
        }

        [Test]
        public void Platform_Idle_DoesNotMove()
        {
            MovingPlatformState platform = MakePlatform(100f, 100f, dx: 10f, tStay: 30, tMove: 90);

            Assert.AreEqual(0, platform.MoveSt);
            Assert.IsFalse(platform.Step(), "an idle platform writes nothing");
            Assert.AreEqual(100f, platform.Object.position.x, 0.0001f);
        }

        [Test]
        public void Platform_Move3_FromTheStart_SnapsToTheTravelPhase()
        {
            // Interact.as:1903-1908 — the first band assigns t_move = tStay.
            MovingPlatformState platform = MakePlatform(100f, 100f, dx: 14f, tStay: 30, tMove: 180);

            platform.TryCommand("move3");

            Assert.AreEqual(3, platform.MoveSt);
            Assert.AreEqual(30f, platform.ElapsedFrames, 0.0001f);
        }

        [Test]
        public void Platform_MidTravel_TravelsOutwardAndClearsAtTheEnd()
        {
            // tStay=1, tMove=2, dx=2 — a cycle long enough to observe every band. The phase map is
            // [0,1) parked, [1,3) travelling out, [3,4) parked at the end, [4,6) travelling back.
            // `move3` first snaps t_move from 0 into the stay band (tStay = 1), so step one is the
            // first frame of the outward leg and still shows p = 0.
            MovingPlatformState platform = MakePlatform(0f, 0f, dx: 2f, tStay: 1, tMove: 2);

            platform.TryCommand("move3");

            platform.Step();
            Assert.AreEqual(0f, platform.Object.position.x, 0.0001f, "p = 0 at the leg's first frame");

            platform.Step();
            Assert.AreEqual(WorldConstants.TILE_SIZE, platform.Object.position.x, 0.0001f,
                "halfway out — dx is authored in tiles, so half of two is one tile");

            platform.Step();
            Assert.AreEqual(2f * WorldConstants.TILE_SIZE, platform.Object.position.x, 0.0001f,
                "at the far end");
            Assert.AreEqual(0, platform.MoveSt, "moveSt 3 clears on arrival (Interact.as:1848-1852)");
        }

        [Test]
        public void Platform_Pop_TogglesBetweenIdleAndContinuous()
        {
            // Interact.as:1784-1793.
            MovingPlatformState platform = MakePlatform(0f, 0f, dx: 4f, tStay: 1, tMove: 1);

            platform.TryCommand("pop");
            Assert.AreEqual(4, platform.MoveSt, "pop from idle starts continuous travel");

            platform.TryCommand("pop");
            Assert.AreEqual(0, platform.MoveSt, "pop again stops it");
        }

        [Test]
        public void Platform_StartedByOn_MovesWithoutBeingTold()
        {
            ObjectInstance obj = MakeObject(
                "platform2",
                Vector2.zero,
                ("move_present", "1"),
                ("move_dy", "3"),
                ("move_tstay", "0"),
                ("move_tmove", "1"),
                ("move_on", "1"));
            obj.TryGetMoveNode(out MapObjectMoveData move);

            var platform = new MovingPlatformState(obj, in move);

            Assert.AreEqual(4, platform.MoveSt, "@on sets moveSt = 4 (Interact.as:465-467)");

            platform.Step();
            Assert.AreEqual(0f, platform.Object.position.y, 0.0001f,
                "the cycle's first frame is still p = 0 — it starts at the start, not mid-air");

            platform.Step();
            Assert.AreEqual(3f * WorldConstants.TILE_SIZE, platform.Object.position.y, 0.0001f,
                "the second frame is already at the far end (tStay = 0, tMove = 1)");
        }

        [Test]
        public void Platform_Mirror_FlipsTheSignOfDx()
        {
            ObjectInstance obj = MakeObject(
                "platform2",
                Vector2.zero,
                ("move_present", "1"),
                ("move_dx", "6"),
                ("move_tstay", "0"),
                ("move_tmove", "1"));
            obj.TryGetMoveNode(out MapObjectMoveData move);

            var mirrored = new MapObjectMoveData(
                move.Dx, move.Dy, move.HasDx, move.HasDy, move.TStay, move.TMove, move.StartsMoving,
                mirror: true);

            var platform = new MovingPlatformState(obj, in mirrored);

            Assert.AreEqual(-6f * WorldConstants.TILE_SIZE, platform.EndX, 0.0001f,
                "Interact.as:434-441 — mirrored rooms negate dx");
        }

        // ── the broadcast ─────────────────────────────────────────────────────────────────

        private static ObjectInteractionBroadcast MakeBroadcast(
            out ObjectVerbHost verbHost,
            out List<string> reported)
        {
            reported = new List<string>();
            verbHost = new ObjectVerbHost
            {
                Report = reported.Add,
                Platforms = room => room?.MovingPlatforms,
            };
            return new ObjectInteractionBroadcast(
                new ObjectVerbCommand(verbHost), unitVerbs: null, report: reported.Add);
        }

        [Test]
        public void Broadcast_OnlyMatchingAllid_ReceivesTheVerb()
        {
            RoomInstance room = MakeRoom();
            ObjectInstance button = MakeObject(
                "knop3", Vector2.zero, ("allact", "move3"), ("allid", "d2"));
            ObjectInstance platform = MakeObject(
                "platform2", new Vector2(10f, 10f), ("allid", "d2"), ("move_present", "1"),
                ("move_dx", "5"), ("move_tstay", "0"), ("move_tmove", "1"));
            ObjectInstance otherDoor = MakeObject("door3", new Vector2(20f, 20f), ("allid", "1"));

            room.AddObject(button);
            room.AddObject(platform);
            room.AddObject(otherDoor);

            ObjectInteractionBroadcast broadcast = MakeBroadcast(out ObjectVerbHost _, out List<string> _);

            int sent = broadcast.Run(room, button, "move3", "d2");

            Assert.AreEqual(1, sent, "the platform is the only d2-tagged target");
            Assert.AreEqual(3, room.MovingPlatforms.StateFor(platform).MoveSt,
                "the verb reached the platform as moveSt = 3");
            // tStay = 0 and tMove = 1, so the cycle is [0,1) out and [1,2) back: frame one is p = 0
            // (the leg's own first frame), frame two is p = 1 and the platform is at its far end.
            room.MovingPlatforms.Step();
            room.MovingPlatforms.Step();
            Assert.AreEqual(
                10f + 5f * WorldConstants.TILE_SIZE,
                platform.position.x, 0.0001f,
                "the platform travelled its dx over the leg");
        }

        [Test]
        public void Broadcast_ExcludesTheSender()
        {
            // Interact.as:2838 — `_loc4_ != param1`. A knop1 carrying both allact and allid would
            // otherwise act on itself.
            RoomInstance room = MakeRoom();
            ObjectInstance button = MakeObject(
                "knop1", Vector2.zero, ("allact", "open"), ("allid", "a1"));
            room.AddObject(button);

            ObjectInteractionBroadcast broadcast = MakeBroadcast(out _, out _);

            Assert.AreEqual(0, broadcast.Run(room, button, "open", "a1"));
            Assert.IsFalse(button.runtimeState.isOpen, "the button did not open itself");
        }

        [Test]
        public void Broadcast_EmptyTag_HitsEverythingIncludingTheSender()
        {
            // The oracle's area arm: `(area != sender && tag == "") || tag == null || area.allid == tag`.
            // With no tag the sweep is unfiltered — which is exactly what a definition-level allact with
            // no allid produces (p_luna's `take` is the only one).
            RoomInstance room = MakeRoom();
            ObjectInstance area = MakeObject("area", Vector2.zero, ("allact", "open"));
            area.objectType = "area";
            ObjectInstance door = MakeObject("door3", Vector2.one);
            room.AddObject(area);
            room.AddObject(door);

            ObjectInteractionBroadcast broadcast = MakeBroadcast(out _, out _);

            Assert.AreEqual(1, broadcast.Run(room, null, "open", string.Empty),
                "the area answers nothing (Area.command has no open arm), the door is the only hit");
        }

        [Test]
        public void Broadcast_AreaDoesNotReceiveObjectVerbs()
        {
            // Area.command handles only onoff/off/on/dam (Area.as:257-286) — so an `open` broadcast to an
            // area is refused, and the area arm passes no "13", so an area would otherwise re-enter its
            // own allAct for ever.
            RoomInstance room = MakeRoom();
            ObjectInstance area = MakeObject("area", Vector2.zero);
            area.objectType = "area";
            room.AddObject(area);

            ObjectInteractionBroadcast broadcast = MakeBroadcast(out _, out _);

            Assert.AreEqual(0, broadcast.Run(room, null, "open", string.Empty));
        }

        [Test]
        public void Broadcast_Open_IsRecordedOnTheTarget()
        {
            RoomInstance room = MakeRoom();
            ObjectInstance button = MakeObject("knop1", Vector2.zero, ("allact", "open"), ("allid", "a1"));
            ObjectInstance door = MakeObject("door3", new Vector2(5f, 5f), ("allid", "a1"));
            room.AddObject(button);
            room.AddObject(door);

            ObjectInteractionBroadcast broadcast = MakeBroadcast(out _, out _);

            broadcast.Run(room, button, "open", "a1");

            Assert.IsTrue(door.runtimeState.isOpen, "the door opened");
        }

        // ── the fallback ─────────────────────────────────────────────────────────────────

        [Test]
        public void Fallback_AcceptsObjectVerbs_AndDeclinesTheOracleBranches()
        {
            RoomInstance room = MakeRoom();
            ObjectInstance button = MakeObject("knop1", Vector2.zero, ("allact", "open"), ("allid", "a1"));
            ObjectInstance door = MakeObject("door3", new Vector2(5f, 5f), ("allid", "a1"));
            room.AddObject(button);
            room.AddObject(door);

            ObjectVerbHost verbHost;
            List<string> reported;
            ObjectInteractionBroadcast broadcast = MakeBroadcast(out verbHost, out reported);

            var dispatcher = new ObjectActionDispatcher(reported.Add)
            {
                Fallback = new BroadcastAction(broadcast)
            };
            verbHost.ReenterAllAct = (r, o) =>
            {
                var context = new ObjectActionContext(r, o, null, Vector3.zero);
                return dispatcher.Dispatch(in context) == ObjectActionOutcome.Handled;
            };

            // An oracle branch must NOT broadcast, even unwired — an unwired `comein` falling through to
            // the broadcast would silently change the Z-door degradation.
            foreach (string id in new[]
                     {
                         "comein", "exit", "map", "probreturn", "hack_robot", "hack_lock", "prob_help",
                         "electro_check", "bind", "work", "lab", "stove", "app", "stand", "robocell",
                         "alarm", "vault",
                     })
            {
                Assert.IsFalse(
                    new BroadcastAction(broadcast).Handles(id),
                    "the oracle handles '" + id + "' explicitly, so it must never reach the broadcast");
            }

            Assert.IsTrue(new BroadcastAction(broadcast).Handles("open"));
            Assert.IsTrue(new BroadcastAction(broadcast).Handles("move3"));
            Assert.IsTrue(new BroadcastAction(broadcast).Handles("swap"));

            // And end to end: the button's own allact dispatches through the fallback and opens the door.
            var buttonContext = new ObjectActionContext(room, button, null, Vector3.zero);
            Assert.AreEqual(ObjectActionOutcome.Handled, dispatcher.Dispatch(in buttonContext));
            Assert.IsTrue(door.runtimeState.isOpen);
        }

        /// <summary>
        /// The dispatcher's own <c>Handles</c> must agree with <see cref="BroadcastAction.Handles"/>
        /// about the broadcast ids, because <c>RoomObjectVisualManager.UsesDoorPresenter</c> asks it —
        /// and that question is the <b>only</b> clause that can admit a <c>knop</c> button.
        ///
        /// <para>The button imports as <c>type: box</c>, its definition family is <c>Furniture</c>
        /// (<c>AllData.as:4907</c> <c>ico='but'</c>) and its visual id starts with neither <c>door</c>
        /// nor a door family name, so with <c>Handles</c> consulting only the registered actions every
        /// button in the game answered false and fell to <c>ObjectColliderDebugPresenter</c>: no
        /// collider, no <c>IInteractable</c>, unreachable. That is "buttons do nothing — create or
        /// remove platforms, open doors, move platforms".</para>
        ///
        /// <para>The negative half matters as much: an oracle branch the fallback declines must still
        /// answer false, or an unported <c>hack_robot</c> terminal would start toggling open.</para>
        /// </summary>
        [Test]
        public void Handles_ConsultsTheFallback_SoAButtonWithABroadcastAllactIsAdmitted()
        {
            ObjectInteractionBroadcast broadcast = MakeBroadcast(out _, out _);
            var dispatcher = new ObjectActionDispatcher(_ => { })
            {
                Fallback = new BroadcastAction(broadcast)
            };

            foreach (string id in new[] { "open", "swap", "move3", "pop", "unlock", "onoff", "spawn" })
            {
                Assert.IsTrue(dispatcher.Handles(id),
                    "'" + id + "' is run by the broadcast, so the dispatcher does handle it");
            }

            foreach (string id in new[] { "hack_robot", "comein", "exit", "map", "vault", "stand" })
            {
                Assert.IsFalse(dispatcher.Handles(id),
                    "'" + id + "' is an oracle branch the fallback declines, so it must not be claimed");
            }

            Assert.IsFalse(dispatcher.Handles(null));
            Assert.IsFalse(dispatcher.Handles(""));

            // A dispatcher with no fallback is unchanged — every fixture built with a bare
            // `new ObjectActionDispatcher(...)` depends on that.
            var bare = new ObjectActionDispatcher(_ => { });
            Assert.IsFalse(bare.Handles("swap"));
        }

        // ── terminal actions ─────────────────────────────────────────────────────────────

        [Test]
        public void TerminalActions_RegisterThirteenBranches_AndReportTheFourGaps()
        {
            var dispatcher = new ObjectActionDispatcher(_ => { });
            var reported = new List<string>();
            TerminalActions.RegisterAll(dispatcher, new TerminalActionHost { Report = reported.Add });

            Assert.AreEqual(
                new HashSet<string>(TerminalActions.RegisteredIds).Count,
                TerminalActions.RegisteredIds.Distinct().Count(),
                "no id is registered twice");

            foreach (string id in TerminalActions.RegisteredIds)
            {
                Assert.IsTrue(dispatcher.Handles(id), "'" + id + "' must be registered");
            }

            Assert.AreEqual(4, reported.Count,
                "prob_help, electro_check, bind and stand are named gaps");
        }

        [Test]
        public void TerminalActions_HackLock_BroadcastsToTheWholeRoom()
        {
            // Interact.as:1589-1598 — `command("hack")` on every object with an Interact, no allid.
            RoomInstance room = MakeRoom();
            ObjectInstance term = MakeObject("term2", Vector2.zero, ("allact", "hack_lock"));
            ObjectInstance hackable = MakeObject("locked", new Vector2(5f, 5f), ("knop", "1"));
            room.AddObject(term);
            room.AddObject(hackable);

            var verbs = new ObjectVerbCommand(new ObjectVerbHost());
            var broadcast = new ObjectInteractionBroadcast(verbs);
            List<(string verb, string allId)> sent = new List<(string, string)>();

            var dispatcher = new ObjectActionDispatcher(_ => { })
            {
                Fallback = new BroadcastAction(broadcast)
            };
            TerminalActions.RegisterAll(dispatcher, new TerminalActionHost
            {
                Broadcast = (r, o, verb, allId) => { sent.Add((verb, allId)); },
            });

            var context = new ObjectActionContext(room, term, null, Vector3.zero);
            Assert.AreEqual(ObjectActionOutcome.Handled, dispatcher.Dispatch(in context));
            Assert.AreEqual(1, sent.Count);
            Assert.AreEqual("hack", sent[0].verb);
            Assert.AreEqual(string.Empty, sent[0].allId, "the sweep is untagged");
        }
    }
}
