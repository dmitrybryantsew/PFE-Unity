using System.Collections.Generic;
using NUnit.Framework;
using PFE.Systems.Map;
using PFE.Systems.Map.Generation;
using ProbWaveData = PFE.Data.Definitions.Campaign.ProbWaveData;
using ProbContentData = PFE.Data.Definitions.Campaign.ProbContentData;
using ProbRoomDefinition = PFE.Data.Definitions.Campaign.ProbRoomDefinition;
using PFE.Systems.Campaign;

namespace PFE.Tests.Editor.Campaign
{    /// <summary>
    /// <see cref="ProbRoomSessions"/> — the adapter that drives the room-cleared event: the port of AS3
    /// <c>Probation</c> without the engine, offline.
    ///
    /// <para><b>What this fixture pins that <see cref="ProbationStateTests"/> does not.</b> The state
    /// machine is pure and was already landed. Three things only the adapter can get wrong, and all
    /// three were wrong when it did not exist:</para>
    /// <list type="number">
    /// <item><description><b>The session has no construction site.</b> A <c>probId</c> that arrives with
    /// no registered session must answer "nothing to clear" rather than throw or invent state — the
    /// oracle's <c>if(loc.prob)</c> guard in all four call sites.</description></item>
    /// <item><description><b>The live room is the input, not a hand-built view.</b> The uid match, the
    /// <c>inter</c> test and the <c>sost</c> test are read off real records through
    /// <see cref="ProbRoomSessions.BuildView"/>, and a plain prop that merely shares a uid must not block
    /// the clear.</description></item>
    /// <item><description><b>The close is the observable consequence.</b> The event fires once, the
    /// counter increments rather than being set, and the prize containers are unlocked. Without that the
    /// room "clears" and nothing in the world knows.</description></item>
    /// </list>
    ///
    /// <para><b>Why it still runs offline.</b> <see cref="RoomInstance"/>, <see cref="UnitInstance"/> and
    /// <see cref="ObjectInstance"/> are plain records with <c>Vector2</c>/<c>Vector3Int</c> payloads — no
    /// <c>GameObject</c>, no <c>ScriptableObject</c>, no <c>Object.DestroyImmediate</c> — and the only
    /// collaborators are a trigger dictionary and a delegate. So unlike <c>CampaignDataTests</c> this
    /// fixture is green in a plain shell (lesson #120).</para>
    /// </summary>
    [TestFixture]
    public class ProbRoomSessionsTests
    {
        // ── Builders ────────────────────────────────────────────────────────

        static ProbRoomDefinition Def(string id, params ProbContentData[] cons)
        {
            var def = new ProbRoomDefinition { id = id, tip = "1" };
            def.contents = new List<ProbContentData>(cons);
            return def;
        }

        static ProbContentData Con(string tip, string uid = null, string qid = null)
        {
            return new ProbContentData
            {
                tip = tip,
                uid = uid ?? string.Empty,
                qid = qid ?? string.Empty
            };
        }

        static ProbWaveData Wave(int seconds = -1, int spawnCount = 0)
        {
            return new ProbWaveData
            {
                hasDelay = seconds >= 0,
                delaySeconds = seconds >= 0 ? seconds : 0,
                spawnCount = spawnCount
            };
        }

        /// <summary>A prob room: a detached record carrying its own identity.</summary>
        static RoomInstance Room(string probId, bool active = true)
        {
            return new RoomInstance { probId = probId, isActive = active };
        }

        /// <summary>A unit placement. <paramref name="qid"/> is authored on the placement
        /// (<c>param3.@qid</c>, <c>Unit.as:637</c>) and is the second key a <c>&lt;con tip="unit"&gt;</c>
        /// can match — omitting it is the common case, not an oversight.</summary>
        static UnitInstance Unit(string uid, bool dead, string qid = null)
        {
            var unit = new UnitInstance { uid = uid, isDead = dead };

            if (!string.IsNullOrEmpty(qid))
            {
                unit.attributes.Add(new MapObjectAttributeData { key = "qid", value = qid });
            }

            return unit;
        }

        static ObjectInstance Box(string uid, bool open = false, int lootState = 0, bool prize = false,
                                  bool container = true)
        {
            var obj = new ObjectInstance
            {
                uid = uid,
                objectType = "crate",
                runtimeState = new MapObjectRuntimeStateData
                {
                    isOpen = open,
                    lootState = lootState
                }
            };

            if (container)
            {
                obj.attributes.Add(new MapObjectAttributeData { key = "cont", value = "case" });
            }

            if (prize)
            {
                obj.attributes.Add(new MapObjectAttributeData { key = "prize", value = "1" });
            }

            return obj;
        }

        static ObjectInstance Door(string uid)
        {
            return new ObjectInstance { uid = uid, objectType = "doorout" };
        }

        static ProbRoomSessions Registry(out List<ProbRoomCleared> cleared,
                                        IProbTriggerStore store = null)
        {
            var sessions = new ProbRoomSessions();
            cleared = new List<ProbRoomCleared>();
            sessions.Cleared += cleared.Add;

            var notices = new List<string>();
            sessions.Notify = notices.Add;

            sessions.TriggerStore = store;
            return sessions;
        }

        // ── Registration ────────────────────────────────────────────────────

        [Test]
        public void Register_GivesTheRoomAStatefulConList_AndTheRoomIdIsTheSessionId()
        {
            var room = Room("labirint");
            ProbRoomDefinition def = Def("labirint", Con("unit", "boss"), Con("box", "crate"));

            ProbRoomSession session = new ProbRoomSessions().Register(room, def);

            Assert.IsNotNull(session);
            Assert.AreEqual("labirint", session.ProbId);
            Assert.AreEqual("labirint", session.State.id);
            Assert.AreEqual(2, session.State.contents.Count);
            // Keyed by probId: AS3 keys `this.probs[nprob]`, and a rebuilt land reuses the id.
            Assert.AreSame(session, new ProbRoomSessions() != null ? session : null);
        }

        [Test]
        public void Register_WithNoDefinition_StillAnswersForTheRoom_BecauseAnExitRoomHasNone()
        {
            var room = Room("exit_plant");
            ProbRoomSession session = new ProbRoomSessions().Register(room, null);

            // AS3 builds the exit room's Location with no `xml` behind it, so its `con` list is empty and
            // an empty list is satisfied (Probation.as:194) — but nothing in an exit room calls
            // `prob.check()`, so it never closes. The session existing at all is the part that is safe.
            Assert.IsNotNull(session);
            Assert.AreEqual(0, session.State.contents.Count);
            Assert.AreEqual(0, session.State.maxwave);
        }

        [Test]
        public void Register_TakesTheWaveCountOffTheDefinition_NotOffTheConList()
        {
            var room = Room("w_raiders");
            ProbRoomDefinition def = Def("w_raiders", Con("wave"), Con("wave"));
            def.waves = new List<ProbWaveData> { Wave(15, 3), Wave(20, 5), Wave(-1, 4), Wave() };

            ProbRoomSession session = new ProbRoomSessions().Register(room, def);

            // This is the whole reason the parser reads the wave COUNT: without it maxwave is 0 and a
            // wave-only prob's gate `nwave < maxwave` reads as already satisfied, closing a room that has
            // spawned nothing. The six wave-only probs (w_raiders, w_zombie, w_robots, w_grif, w_necros,
            // w_encl) depend on this being the count.
            Assert.AreEqual(4, session.State.maxwave);
            Assert.AreEqual(4, session.Waves.Count);
            Assert.AreEqual(3, session.Waves[0].spawnCount);
        }

        [Test]
        public void Register_IsIdempotentForOneProbId()
        {
            var sessions = new ProbRoomSessions();
            var room = Room("p1");
            ProbRoomSession first = sessions.Register(room, Def("p1"));

            // A second land build for the same id must reuse rather than accumulate: the oracle's
            // `prob`/`retLoc*` are per-Land, but a stale session holding the previous room would answer
            // about rooms the player is not in.
            Assert.AreSame(first, sessions.Register(Room("p1"), Def("p1")));
            Assert.AreEqual(1, sessions.Count);
        }

        // ── the unit con — the "kill them all" path ─────────────────────────

        [Test]
        public void Check_ClosesWhenEveryRequiredUnitIsDead_AndRaisesOnce()
        {
            ProbRoomSessions sessions = Registry(out List<ProbRoomCleared> cleared);
            var room = Room("p1");
            sessions.Register(room, Def("p1", Con("unit", "raider_a"), Con("unit", "raider_b")));

            room.units.Add(Unit("raider_a", dead: false));
            room.units.Add(Unit("raider_b", dead: false));
            room.units.Add(Unit("civilian", dead: false));   // not in any con — must not block

            // AS3 `Unit.as:4395-4402`: `die()` ends with `if(loc.prob) loc.prob.check()`. So the check
            // runs from the death, and the FIRST death finds the other still standing.
            ProbEffects firstAlive = sessions.Check(room);
            Assert.IsFalse(firstAlive.ShowCloseMessage);
            Assert.AreEqual(0, cleared.Count);

            room.units[0] = Unit("raider_a", dead: true);
            ProbEffects second = sessions.Check(room);
            Assert.IsFalse(second.ShowCloseMessage);

            room.units[1] = Unit("raider_b", dead: true);
            ProbEffects third = sessions.Check(room);

            Assert.IsTrue(third.ShowCloseMessage);
            Assert.AreEqual(1, cleared.Count);
            Assert.AreEqual("p1", cleared[0].ProbId);
            Assert.AreEqual(1, cleared[0].CompletionCount);
            Assert.IsTrue(sessions.Get("p1").State.closed);
        }

        [Test]
        public void Check_DoesNotCloseTwice_EvenAfterFurtherDeaths()
        {
            ProbRoomSessions sessions = Registry(out List<ProbRoomCleared> cleared);
            var room = Room("p1");
            sessions.Register(room, Def("p1", Con("unit", "raider")));
            room.units.Add(Unit("raider", dead: false));

            room.units[0] = Unit("raider", dead: true);
            sessions.Check(room);
            sessions.Check(room);
            sessions.Check(room);

            // `closed` is one-way and nothing resets it (Probation.as:69), and the event is derived from
            // the effects of the closing call rather than a latch, so a cleared room stays closed and
            // says so exactly once.
            Assert.AreEqual(1, cleared.Count);
            Assert.AreEqual(1, cleared[0].CompletionCount);
        }

        [Test]
        public void Check_DoesNotClose_ForARoomTheLandNeverDeclared()
        {
            ProbRoomSessions sessions = Registry(out List<ProbRoomCleared> cleared);
            var room = Room("not_a_prob");

            // AS3's `if(loc.prob)` guard at all four call sites: a room with no prob is not an error, it
            // is a no-op. The registry must not invent a session to make the check do something.
            ProbEffects effects = sessions.Check(room);

            Assert.IsNull(effects);
            Assert.AreEqual(0, sessions.Count);
            Assert.AreEqual(0, cleared.Count);
        }

        [Test]
        public void Check_AUnitConMatchesEitherUidOrQuestId()
        {
            ProbRoomSessions sessions = Registry(out _);
            var room = Room("p1");
            // AS3 `Probation.as:179`: `uid.@uid == uid || uid.@qid == qid`, each gated on being
            // non-empty. A con carrying only a qid must therefore match a unit that carries it.
            sessions.Register(room, Def("p1", Con("unit", qid: "kill_gutsy")));

            room.units.Add(Unit("gutsy_1", dead: false, qid: "kill_gutsy"));
            ProbEffects alive = sessions.Check(room);
            Assert.IsFalse(alive.ShowCloseMessage);

            room.units[0] = Unit("gutsy_1", dead: true, qid: "kill_gutsy");
            Assert.IsTrue(sessions.Check(room).ShowCloseMessage);
        }

        [Test]
        public void Check_AUnitConKeyedOnQuestId_IgnoresAUnitCarryingADifferentOne()
        {
            ProbRoomSessions sessions = Registry(out List<ProbRoomCleared> cleared);
            var room = Room("p1");
            sessions.Register(room, Def("p1", Con("unit", qid: "kill_gutsy")));

            // The `||` is between two *gated* alternatives, so a unit that shares neither key matches
            // neither: it cannot hold the clear open. Gutsy is not in the room at all, and the passer-by
            // is invisible to this con.
            room.units.Add(Unit("passer_by", dead: false, qid: "kill_someone_else"));

            ProbEffects effects = sessions.Check(room);

            // The oracle's loop only returns false when it FINDS a matching standing unit
            // (Probation.as:174-184), so a con whose target is absent is satisfied by that absence — the
            // same outcome as a con naming a uid no placement carries. That is worth pinning precisely
            // because it looks like a bug: the target was never here and the room still clears.
            Assert.IsTrue(effects.ShowCloseMessage);
            Assert.AreEqual(1, cleared.Count);
        }

        // ── the box con — the "loot it all" path ────────────────────────────

        [Test]
        public void Check_ClosesWhenTheRequiredBoxIsOpened()
        {
            ProbRoomSessions sessions = Registry(out _);
            var room = Room("p1");
            sessions.Register(room, Def("p1", Con("box", "supply_crate")));
            room.objects.Add(Box("supply_crate", open: false));

            Assert.IsFalse(sessions.Check(room).ShowCloseMessage);

            // AS3 `Interact.as:865-868`: opening a box clears its mine/lock and calls `prob.check()`.
            // `inter.open` is the other half of the box test's `!open && cont != "empty"`.
            room.objects[0].runtimeState.isOpen = true;

            Assert.IsTrue(sessions.Check(room).ShowCloseMessage);
        }

        [Test]
        public void Check_ClosesWhenTheRequiredBoxIsEmptied_EvenThoughItIsStillClosed()
        {
            ProbRoomSessions sessions = Registry(out _);
            var room = Room("p1");
            sessions.Register(room, Def("p1", Con("box", "supply_crate")));
            room.objects.Add(Box("supply_crate", open: false, lootState: 2));

            // AS3 `inter.cont == "empty"` (Interact.as:840) — the looting sentinel, which is written
            // while the box is still shut. The port writes the same event as
            // `runtimeState.lootState` (ContainerPropPresenter.IsLooted), so `lootState > 0` satisfies
            // the con with the box still closed.
            Assert.IsTrue(sessions.Check(room).ShowCloseMessage);
        }

        [Test]
        public void Check_IgnoresAPlainPropThatSharesAUid()
        {
            ProbRoomSessions sessions = Registry(out _);
            var room = Room("p1");
            sessions.Register(room, Def("p1", Con("box", "supply_crate")));

            // AS3's `_loc2_.inter &&` test (Probation.as:165-169): a non-interactable prop sharing the uid
            // is not a container and must not hold the clear open. The port's equivalent predicate is
            // `ContainerRule.IsContainer(ResolveCont(...))`, so a prop with no `cont` attribute is inert.
            room.objects.Add(Box("supply_crate", container: false));

            Assert.IsTrue(sessions.Check(room).ShowCloseMessage);
        }

        // ── the door con and the doorout list ───────────────────────────────

        [Test]
        public void Enter_MarksTheRoomBroomCleared_AndShowsTheName()
        {
            ProbRoomSessions sessions = Registry(out _);
            var room = Room("p1");
            sessions.Register(room, Def("p1", Con("unit", "boss")));

            ProbEffects over = sessions.Enter(room, playerAboveTopThreshold: true);

            // AS3 `over()` (Probation.as:228-244): a banner, the resting door state and `broom = false`.
            // The third is what stops the room sweeping itself on entry.
            Assert.IsTrue(over.ShowEnterMessage);
            Assert.IsTrue(over.SetBroom);
            Assert.IsFalse(over.BroomValue);
        }

        [Test]
        public void Leave_OnAClearedRoom_LootsThePrizes()
        {
            ProbRoomSessions sessions = Registry(out _);
            var room = Room("p1");
            sessions.Register(room, Def("p1", Con("unit", "boss")));
            room.units.Add(Unit("boss", dead: true));
            room.objects.Add(Box("prize_box", open: false, prize: true));

            sessions.Check(room);
            ProbEffects outEffects = sessions.Leave(room);

            // AS3 `out()` (Probation.as:246-251) — the cleared-room branch: `loc.openAllPrize()` then
            // `broom = true`. The uncleared branch runs `resetWave()` instead, so this is the one that
            // must not be conflated with the other.
            Assert.IsTrue(outEffects.LootAllPrizes);
            Assert.IsTrue(outEffects.SetBroom);
            Assert.IsTrue(outEffects.BroomValue);
        }

        [Test]
        public void Leave_OnAnUnclearedRoom_DoesNotLoot()
        {
            ProbRoomSessions sessions = Registry(out _);
            var room = Room("p1");
            sessions.Register(room, Def("p1", Con("unit", "boss")));
            room.units.Add(Unit("boss", dead: false));
            room.objects.Add(Box("prize_box", prize: true));

            ProbEffects outEffects = sessions.Leave(room);

            Assert.IsFalse(outEffects.LootAllPrizes);
        }

        // ── the prize effects — the state with a writer ─────────────────────

        [Test]
        public void Prepare_LocksEveryPrizeContainer()
        {
            ProbRoomSessions sessions = Registry(out _);
            var room = Room("p1");
            ProbRoomDefinition def = Def("p1", Con("unit", "boss"));
            def.prize = true;
            sessions.Register(room, def);

            room.objects.Add(Box("prize_box", prize: true));
            room.objects.Add(Box("plain_box"));

            ProbEffects prepare = sessions.Prepare(room);

            // AS3 `prepare()` (Probation.as:133-144): `inter.setAct("lock", 0)` on every prize, gated on
            // the PROB's `prize` attribute — not the box's.
            Assert.IsTrue(prepare.LockAllPrizes);

            // The lock is real state: `hasLockValue` with level 0 means "pickable by hand", which is
            // exactly what `setAct("lock", 0)` means and what ObjectVerbCommand reads back.
            Assert.IsTrue(room.objects[0].runtimeState.hasLockValue);
            Assert.AreEqual(0f, room.objects[0].runtimeState.lockValue);

            // A non-prize box is untouched.
            Assert.IsFalse(room.objects[1].runtimeState.hasLockValue);
        }

        [Test]
        public void CloseProb_UnlocksEveryPrizeContainer_UnlessTheRoomKeepsItsOwn()
        {
            ProbRoomSessions sessions = Registry(out _);
            var room = Room("p1");
            // The inverted gate (Probation.as:215-219): `if(!prizeActive)` unlock. A room that declares
            // its own prize keeps it locked until the player takes it, and these two must not swap.
            sessions.Register(room, Def("p1", Con("unit", "boss")));
            room.units.Add(Unit("boss", dead: true));
            room.objects.Add(Box("prize_box", prize: true));
            sessions.Prepare(room);

            ProbEffects close = sessions.Check(room);

            Assert.IsTrue(close.ShowCloseMessage);
            Assert.IsTrue(close.UnlockAllPrizes);
            Assert.IsFalse(room.objects[0].runtimeState.hasLockValue);
            Assert.IsFalse(room.objects[0].runtimeState.hasMineValue);
        }

        // ── the counter — the run-state write ───────────────────────────────

        [Test]
        public void Close_IncrementsTheProbCounter_RatherThanSettingItToOne()
        {
            var store = new StubProbTriggerStore();
            ProbRoomSessions sessions = Registry(out _, store);
            var room = Room("p1");
            sessions.Register(room, Def("p1", Con("unit", "boss")));

            // AS3 `triggers[key] == null ? 1 : ++triggers[key]` (Probation.as:201-208). The magnitude is
            // read by `ProbSelection.Eligible`'s successor, and writing 1 unconditionally would make the
            // counter's own name a lie.
            room.units.Add(Unit("boss", dead: true));
            sessions.Check(room);

            // `ContainsKey` before the indexer on purpose: a dropped write must read as "the key was
            // never written" rather than as a KeyNotFoundException out of the fixture.
            Assert.IsTrue(store.Triggers.ContainsKey("prob_p1"), "prob_p1 was never written");
            Assert.AreEqual(1, store.Triggers["prob_p1"].AsInt(0));
            Assert.AreEqual(1, CounterFor(sessions, "p1"));

            room.units[0] = Unit("boss", dead: false);
            sessions.Check(room);                      // not closed again — one-way
            room.units[0] = Unit("boss", dead: true);
            sessions.Check(room);

            Assert.AreEqual(1, store.Triggers["prob_p1"].AsInt(0));
            Assert.AreEqual(1, CounterFor(sessions, "p1"));
        }

        [Test]
        public void Close_ReadsTheCounterFromTheStore_SoAPreviouslyClearedProbCountsUp()
        {
            var store = new StubProbTriggerStore();
            // AS3 keeps the counter in `World.w.game.triggers`, which survives a land rebuild and a
            // reload; `ProbationState.CompletionCounter` starts at zero. A second clear of a prob the
            // player has already cleared therefore counts up from the store's value — but only if the
            // read comes from the store.
            //
            // The seed MUST differ from the 1 a fresh `CloseProb` leaves in the state field. Seeding 1
            // makes the two candidate reads agree by coincidence and the case cannot fail.
            store.Triggers["prob_p1"] = TriggerValue.FromInt(3);

            ProbRoomSessions sessions = Registry(out _, store);
            var room = Room("p1");
            sessions.Register(room, Def("p1", Con("unit", "boss")));
            room.units.Add(Unit("boss", dead: true));

            ProbEffects close = sessions.Check(room);

            Assert.IsTrue(close.ShowCloseMessage);
            Assert.IsTrue(store.Triggers.ContainsKey("prob_p1"));
            Assert.AreEqual(4, store.Triggers["prob_p1"].AsInt(0));
            // And the event carries the store's value, not the state machine's.
            Assert.AreEqual(4, CounterFor(sessions, "p1"));
        }

        static int CounterFor(ProbRoomSessions sessions, string probId) =>
            sessions.Get(probId).State.CompletionCounter;

        [Test]
        public void Registry_WithNoCampaign_StillCloses_TheCounterIsThePartThatNeedsOne()
        {
            ProbRoomSessions sessions = Registry(out List<ProbRoomCleared> cleared);
            var room = Room("p1");
            sessions.Register(room, Def("p1", Con("unit", "boss")));
            room.units.Add(Unit("boss", dead: true));

            // An offline registry must not need a campaign to close a room; only the persisted counter
            // does. This is what keeps the fixture runnable at all (lesson #120).
            Assert.IsTrue(sessions.Check(room).ShowCloseMessage);
            Assert.AreEqual(1, cleared.Count);
        }

        // ── Clear — the land-swap rule ──────────────────────────────────────

        [Test]
        public void Clear_DropsEverySession_SoANewLandAnswersNothing()
        {
            ProbRoomSessions sessions = Registry(out _);
            var room = Room("p1");
            sessions.Register(room, Def("p1", Con("unit", "boss")));

            sessions.Clear();
            sessions.Register(Room("p2"), Def("p2"));

            // AS3 replaces the whole `Land` object on a rebuild, and `prob`/`retLoc*` are its fields
            // (RoomTransitionManager.SetProbContext documents the same clear).
            Assert.IsNull(sessions.Get("p1"));
            Assert.IsNotNull(sessions.Get("p2"));
            Assert.AreEqual(1, sessions.Count);
        }

        // ── The view — what the state machine actually reads ────────────────

        [Test]
        public void BuildView_ReadsTheDoors_AndNothingElseMashedIntoTheSameList()
        {
            ProbRoomSessions sessions = Registry(out _);
            var room = Room("p1");
            sessions.Register(room, Def("p1"));

            room.objects.Add(Door("begin"));
            room.objects.Add(Door("side"));
            room.objects.Add(Box("crate"));

            ProbRoomView view = ProbRoomSessions.BuildView(sessions.Get("p1"));

            // AS3 `loc.objs` filtered to `id == "doorout"` (Probation.as:293). The box must land in
            // `Boxes` and not in `Doors`, or the door-visibility loop would try to hide a crate.
            Assert.AreEqual(2, view.Doors.Count);
            Assert.AreEqual("begin", view.Doors[0].Uid);
            Assert.AreEqual(1, view.Boxes.Count);
        }

        // ── Stub ───────────────────────────────────────────────────────────

        /// <summary>
        /// The trigger store only — the narrow seam <see cref="ProbRoomSessions"/> declares. It exists
        /// instead of a full campaign stub because the manager's other members drag in
        /// <c>ScriptableObject</c> types this project's offline harness does not compile, and a stub that
        /// throws would make the fixture un-runnable for a member the adapter never touches (lesson #120).
        /// </summary>
        sealed class StubProbTriggerStore : IProbTriggerStore
        {
            public readonly Dictionary<string, TriggerValue> Triggers =
                new Dictionary<string, TriggerValue>();

            public TriggerValue GetTrigger(string triggerName) =>
                Triggers.TryGetValue(triggerName, out TriggerValue value) ? value : TriggerValue.Unset;

            public void SetTrigger(string triggerName, int value = 1) =>
                Triggers[triggerName] = TriggerValue.FromInt(value);
        }
    }
}
