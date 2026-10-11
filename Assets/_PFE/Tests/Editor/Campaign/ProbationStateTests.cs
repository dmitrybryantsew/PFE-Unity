using NUnit.Framework;
using PFE.Data.Definitions.Campaign;
using PFE.Systems.Map.Generation;

namespace PFE.Tests.Editor.Campaign
{
    /// <summary>
    /// <see cref="ProbationState"/> — the pure port of AS3 <c>Probation.as</c>.
    ///
    /// <para><b>Why these run offline.</b> The fixture touches no <c>ScriptableObject</c>, no
    /// <c>GameObject</c> and no <c>Object.DestroyImmediate</c> — the state machine reads a
    /// <see cref="ProbRoomView"/> of plain records and returns <see cref="ProbEffects"/>. So unlike
    /// <c>CampaignDataTests</c> (0/N, every case <c>ECall</c>) this fixture is green in a plain shell.
    /// See lesson #120.</para>
    /// </summary>
    [TestFixture]
    public class ProbationStateTests
    {
        // ── Builders ───────────────────────────────────────────────────────────────────────

        static ProbationState State(params ProbContentData[] cons)
        {
            var state = new ProbationState { id = "p1", nazv = "Test Prob" };
            state.contents = new System.Collections.Generic.List<ProbContentData>(cons);
            return state;
        }

        static ProbContentData Con(string tip, string uid = null, string qid = null)
        {
            return new ProbContentData { tip = tip, uid = uid ?? string.Empty, qid = qid ?? string.Empty };
        }

        static ProbRoomView Room(bool isActive = true)
        {
            return new ProbRoomView { IsActive = isActive };
        }

        static ProbRoomView RoomWithBox(string uid, bool interactable, bool open, bool emptied, bool prize = false)
        {
            var room = Room();
            room.Boxes.Add(new ProbBoxView
            {
                Uid = uid,
                IsInteractable = interactable,
                IsOpen = open,
                IsEmptied = emptied,
                IsPrize = prize,
            });
            return room;
        }

        static ProbRoomView RoomWithUnit(string uid, bool standing, string qid = null, bool wave = false)
        {
            var room = Room();
            room.Units.Add(new ProbUnitView
            {
                Uid = uid,
                QuestId = qid ?? string.Empty,
                IsStanding = standing,
                IsWaveUnit = wave,
            });
            return room;
        }

        static ProbRoomView RoomWithDoors(params (string uid, bool visible)[] doors)
        {
            var room = Room();
            foreach (var (uid, visible) in doors)
            {
                room.Doors.Add(new ProbDoorView { Uid = uid, IsVisible = visible });
            }
            return room;
        }

        static ProbWaveView Wave(bool exists, int size, bool hasDelay = false, int delaySeconds = 0)
        {
            return new ProbWaveView
            {
                Exists = exists,
                Size = size,
                HasDelay = hasDelay,
                DelaySeconds = delaySeconds,
            };
        }

        // ── checkAllCon: the box branch ────────────────────────────────────────────────────

        [Test]
        public void NoCons_IsSatisfied_SoAProbWithNoContentsClosesAtOnce()
        {
            // Probation.as:158-194 — the loop returns true, so an empty <con> list is "cleared".
            Assert.IsTrue(State().CheckAllCon(Room()));
        }

        [Test]
        public void BoxCon_ClosedUnexploredBox_BlocksTheClear()
        {
            var state = State(Con("box", "b1"));
            Assert.IsFalse(state.CheckAllCon(RoomWithBox("b1", interactable: true, open: false, emptied: false)));
        }

        [Test]
        public void BoxCon_OpenedBox_NoLongerBlocks()
        {
            var state = State(Con("box", "b1"));
            Assert.IsTrue(state.CheckAllCon(RoomWithBox("b1", interactable: true, open: true, emptied: false)));
        }

        [Test]
        public void BoxCon_EmptiedBox_NoLongerBlocks()
        {
            var state = State(Con("box", "b1"));
            Assert.IsTrue(state.CheckAllCon(RoomWithBox("b1", interactable: true, open: false, emptied: true)));
        }

        [Test]
        public void BoxCon_MatchingPropIsNotInteractable_DoesNotBlock()
        {
            // Probation.as:169 tests `_loc2_.inter` before anything else. A plain prop that happens to
            // share the uid is not a container.
            var state = State(Con("box", "b1"));
            Assert.IsTrue(state.CheckAllCon(RoomWithBox("b1", interactable: false, open: false, emptied: false)));
        }

        [Test]
        public void BoxCon_WithNoUid_IsSatisfiedUnconditionally()
        {
            // The chain is `(tip == "box" || tip empty) && uid.length()`, so a box con with no uid falls
            // through every branch. Six of the 97 cons in the data are shaped like this.
            var state = State(Con("box"));
            Assert.IsTrue(state.CheckAllCon(RoomWithBox("b1", interactable: true, open: false, emptied: false)));
        }

        [Test]
        public void AbsentTip_IsTreatedAsBox()
        {
            var state = State(Con(string.Empty, "b1"));
            Assert.IsFalse(state.CheckAllCon(RoomWithBox("b1", interactable: true, open: false, emptied: false)));
        }

        [Test]
        public void UnknownTip_ContributesNothingAndIsSatisfied()
        {
            var state = State(Con("nonsense", "b1"));
            Assert.IsTrue(state.CheckAllCon(RoomWithBox("b1", interactable: true, open: false, emptied: false)));
        }

        // ── checkAllCon: the unit branch ───────────────────────────────────────────────────

        [Test]
        public void UnitCon_StandingUnit_BlocksTheClear()
        {
            var state = State(Con("unit", "u1"));
            Assert.IsFalse(state.CheckAllCon(RoomWithUnit("u1", standing: true)));
        }

        [Test]
        public void UnitCon_DeadUnit_NoLongerBlocks()
        {
            var state = State(Con("unit", "u1"));
            Assert.IsTrue(state.CheckAllCon(RoomWithUnit("u1", standing: false)));
        }

        [Test]
        public void UnitCon_MatchesByQuestIdAsWellAsUid()
        {
            // Probation.as:179 — `uid.length() && u.uid == uid || qid.length() && u.questId == qid`.
            var state = State(Con("unit", null, "q7"));
            Assert.IsFalse(state.CheckAllCon(RoomWithUnit("u1", standing: true, qid: "q7")));
        }

        [Test]
        public void UnitCon_WithBothIds_MatchesAUnitSatisfyingEither()
        {
            var state = State(Con("unit", "u1", "q7"));
            Assert.IsFalse(state.CheckAllCon(RoomWithUnit("u9", standing: true, qid: "q7")));
        }

        [Test]
        public void UnitCon_WithBothIds_DoesNotMatchAUnitSatisfyingNeither()
        {
            var state = State(Con("unit", "u1", "q7"));
            Assert.IsTrue(state.CheckAllCon(RoomWithUnit("u9", standing: true, qid: "q8")));
        }

        [Test]
        public void UnitCon_WithNeitherId_IsSatisfied()
        {
            var state = State(Con("unit"));
            Assert.IsTrue(state.CheckAllCon(RoomWithUnit("u1", standing: true)));
        }

        // ── checkAllCon: the wave branch ───────────────────────────────────────────────────

        [Test]
        public void WaveCon_BlocksWhileWavesRemain()
        {
            var state = State(Con("wave"));
            state.SetMaxWave(2);
            state.nwave = 1;
            state.kolEn = 3;
            state.killEn = 3;
            Assert.IsFalse(state.CheckAllCon(Room()));
        }

        [Test]
        public void WaveCon_BlocksWhileSpawnedEnemiesAreAlive()
        {
            var state = State(Con("wave"));
            state.SetMaxWave(2);
            state.nwave = 2;
            state.kolEn = 3;
            state.killEn = 2;
            Assert.IsFalse(state.CheckAllCon(Room()));
        }

        [Test]
        public void WaveCon_IsSatisfiedWhenAllWavesSpawnedAndAllEnemiesDead()
        {
            var state = State(Con("wave"));
            state.SetMaxWave(2);
            state.nwave = 2;
            state.kolEn = 3;
            state.killEn = 3;
            Assert.IsTrue(state.CheckAllCon(Room()));
        }

        // ── closeProb ──────────────────────────────────────────────────────────────────────

        [Test]
        public void CloseProb_IncrementsTheCounter_RatherThanSettingIt()
        {
            // Probation.as:201-208. ProbSelection only asks whether the key is present, but writing 1
            // unconditionally would make the counter's own name a lie.
            var state = State();
            state.CloseProb(Room());
            Assert.AreEqual(1, state.CompletionCounter);

            state.CloseProb(Room());
            Assert.AreEqual(2, state.CompletionCounter);

            state.CompletionCounter = 7;
            state.CloseProb(Room());
            Assert.AreEqual(8, state.CompletionCounter);
        }

        [Test]
        public void CloseProb_MarksClosedAndUnsealed()
        {
            var state = State();
            state.active = true;
            state.CloseProb(Room());
            Assert.IsTrue(state.closed);
            Assert.IsFalse(state.active);
        }

        [Test]
        public void CloseProb_UnlocksPrizes_OnlyWhenTheRoomDeclaresNoPrize()
        {
            // prepare locks them when prizeActive; closeProb unlocks them when NOT prizeActive.
            var withPrize = State();
            withPrize.prizeActive = true;
            Assert.IsFalse(withPrize.CloseProb(Room()).UnlockAllPrizes);

            var withoutPrize = State();
            withoutPrize.prizeActive = false;
            Assert.IsTrue(withoutPrize.CloseProb(Room()).UnlockAllPrizes);
        }

        [Test]
        public void CloseProb_ShowsEveryReturnDoor()
        {
            var state = State();
            var room = RoomWithDoors(("begin", false), ("side", false));
            ProbEffects effects = state.CloseProb(room);
            CollectionAssert.AreEquivalent(new[] { "begin", "side" }, effects.ShowDoorUids);
            Assert.AreEqual(0, effects.HideDoorUids.Count);
        }

        [Test]
        public void Check_IsANoOpOnceClosed()
        {
            var state = State(Con("unit", "u1"));
            state.closed = true;
            Assert.IsTrue(state.Check(RoomWithUnit("u1", standing: true)).IsEmpty);
        }

        [Test]
        public void Check_ClosesTheRoomWhenEveryConIsSatisfied()
        {
            var state = State(Con("unit", "u1"));
            ProbEffects effects = state.Check(RoomWithUnit("u1", standing: false));
            Assert.IsTrue(state.closed);
            Assert.IsTrue(effects.ShowCloseMessage);
        }

        // ── doorsOnOff ─────────────────────────────────────────────────────────────────────

        [Test]
        public void DoorsOnOff_ShowAll_ShowsEveryDoor()
        {
            var state = State();
            var room = RoomWithDoors(("begin", false), ("side", false));
            ProbEffects effects = state.DoorsOnOff(room, ProbDoorMode.ShowAll);
            CollectionAssert.AreEquivalent(new[] { "begin", "side" }, effects.ShowDoorUids);
            Assert.AreEqual(0, effects.HideDoorUids.Count);
        }

        [Test]
        public void DoorsOnOff_HideAll_HidesEveryDoor()
        {
            var state = State();
            var room = RoomWithDoors(("begin", true), ("side", true));
            ProbEffects effects = state.DoorsOnOff(room, ProbDoorMode.HideAll);
            CollectionAssert.AreEquivalent(new[] { "begin", "side" }, effects.HideDoorUids);
            Assert.AreEqual(0, effects.ShowDoorUids.Count);
        }

        [Test]
        public void DoorsOnOff_Default_LeavesOnlyTheEntryDoorVisible()
        {
            var state = State();
            var room = RoomWithDoors(("begin", false), ("side", true));
            ProbEffects effects = state.DoorsOnOff(room, ProbDoorMode.Default);
            CollectionAssert.AreEquivalent(new[] { "begin" }, effects.ShowDoorUids);
            CollectionAssert.AreEquivalent(new[] { "side" }, effects.HideDoorUids);
        }

        [Test]
        public void DoorsOnOff_ShinesADoorThatIsAboutToAppear()
        {
            // Probation.as:295 — `!visible && param == 1`.
            var state = State();
            var room = RoomWithDoors(("begin", false), ("side", true));
            ProbEffects effects = state.DoorsOnOff(room, ProbDoorMode.ShowAll);
            CollectionAssert.AreEquivalent(new[] { "begin" }, effects.ShineDoorUids);
        }

        [Test]
        public void DoorsOnOff_ShinesADoorThatIsAboutToDisappear()
        {
            var state = State();
            var room = RoomWithDoors(("begin", true), ("side", false));
            ProbEffects effects = state.DoorsOnOff(room, ProbDoorMode.HideAll);
            CollectionAssert.AreEquivalent(new[] { "begin" }, effects.ShineDoorUids);
        }

        [Test]
        public void DoorsOnOff_Default_NeverShines()
        {
            var state = State();
            var room = RoomWithDoors(("begin", false), ("side", true));
            Assert.AreEqual(0, state.DoorsOnOff(room, ProbDoorMode.Default).ShineDoorUids.Count);
        }

        [Test]
        public void DoorBecomesHidden_MatchesTheOracleTable()
        {
            Assert.IsTrue(ProbationState.DoorBecomesHidden("begin", ProbDoorMode.HideAll));
            Assert.IsTrue(ProbationState.DoorBecomesHidden("side", ProbDoorMode.HideAll));
            Assert.IsFalse(ProbationState.DoorBecomesHidden("begin", ProbDoorMode.Default));
            Assert.IsTrue(ProbationState.DoorBecomesHidden("side", ProbDoorMode.Default));
            Assert.IsFalse(ProbationState.DoorBecomesHidden("begin", ProbDoorMode.ShowAll));
            Assert.IsFalse(ProbationState.DoorBecomesHidden("side", ProbDoorMode.ShowAll));
        }

        // ── activateProb / defaultProb / over / out ────────────────────────────────────────

        [Test]
        public void ActivateProb_SealsTheRoomAndHidesEveryDoor()
        {
            var state = State();
            var room = RoomWithDoors(("begin", true));
            ProbEffects effects = state.ActivateProb(room);
            Assert.IsTrue(state.active);
            CollectionAssert.AreEquivalent(new[] { "begin" }, effects.HideDoorUids);
        }

        [Test]
        public void ActivateProb_IsRefusedWhenTheRoomIsNotActiveInTheWorld()
        {
            // Probation.as:274 — `!this.loc.active` is the third refusal term.
            var state = State();
            Assert.IsTrue(state.ActivateProb(Room(isActive: false)).IsEmpty);
            Assert.IsFalse(state.active);
        }

        [Test]
        public void ActivateProb_IsRefusedWhenAlreadyClosedOrActive()
        {
            var closed = State();
            closed.closed = true;
            Assert.IsTrue(closed.ActivateProb(Room()).IsEmpty);

            var active = State();
            active.active = true;
            Assert.IsTrue(active.ActivateProb(Room()).IsEmpty);
        }

        [Test]
        public void Over_OnAnUnclearedRoom_RestoresTheRestingDoorState()
        {
            var state = State();
            var room = RoomWithDoors(("begin", false), ("side", true));
            ProbEffects effects = state.Over(room, playerAboveTopThreshold: true);
            CollectionAssert.AreEquivalent(new[] { "begin" }, effects.ShowDoorUids);
            Assert.IsTrue(effects.ShowEnterMessage);
            Assert.IsTrue(effects.EnterMessageAtTop);
            Assert.IsTrue(effects.SetBroom);
            Assert.IsFalse(effects.BroomValue);
        }

        [Test]
        public void Over_OnAClearedRoom_LeavesTheDoorsAlone()
        {
            var state = State();
            state.closed = true;
            var room = RoomWithDoors(("begin", true), ("side", true));
            ProbEffects effects = state.Over(room, playerAboveTopThreshold: false);
            Assert.AreEqual(0, effects.ShowDoorUids.Count);
            Assert.AreEqual(0, effects.HideDoorUids.Count);
            Assert.IsFalse(effects.EnterMessageAtTop);
        }

        [Test]
        public void Over_OnAClosingRoom_SealsIt()
        {
            // `over` applies TWO door modes in sequence — `defaultProb` (mode 0) then `activateProb`
            // (mode -1) — so the doors they share are commanded twice and the second wins. Asserting a
            // set here would hide that; the room is sealed because every door ends up in the hide list.
            var state = State();
            state.isClose = true;
            var room = RoomWithDoors(("begin", true), ("side", true));

            ProbEffects effects = state.Over(room, playerAboveTopThreshold: true);

            Assert.IsTrue(state.active, "a closing room is sealed on entry");
            CollectionAssert.Contains(effects.HideDoorUids, "begin");
            CollectionAssert.Contains(effects.HideDoorUids, "side");
            Assert.AreEqual(3, effects.HideDoorUids.Count,
                "`side` is hidden by defaultProb and again by activateProb; `begin` only by the latter");
            CollectionAssert.Contains(effects.ShowDoorUids, "begin",
                "defaultProb runs first and briefly shows the entry door");
        }

        [Test]
        public void Out_OnAClearedRoom_LootsAndSweeps()
        {
            var state = State();
            state.closed = true;
            ProbEffects effects = state.Out();
            Assert.IsTrue(effects.LootAllPrizes);
            Assert.IsTrue(effects.SetBroom);
            Assert.IsTrue(effects.BroomValue);
            Assert.IsFalse(effects.DisableAllWaveUnits);
        }

        [Test]
        public void Out_OnAnUnclearedWaveRoom_ResetsTheWave()
        {
            var state = State();
            state.onWave = true;
            ProbEffects effects = state.Out();
            Assert.IsTrue(effects.DisableAllWaveUnits);
            Assert.IsFalse(state.onWave);
            Assert.IsFalse(effects.LootAllPrizes);
        }

        [Test]
        public void Out_OnAnUnclearedRoomWithNoWave_AsksForNothing()
        {
            Assert.IsTrue(State().Out().IsEmpty);
        }

        // ── waves ──────────────────────────────────────────────────────────────────────────

        [Test]
        public void BeginWave_ArmsTheFirstDelayAndHidesTheDoors()
        {
            var state = State();
            var room = RoomWithDoors(("begin", true));
            ProbEffects effects = state.BeginWave(room);
            Assert.IsTrue(state.onWave);
            Assert.AreEqual(ProbationState.BegT, state.t_wave);
            Assert.AreEqual(0, state.nwave);
            CollectionAssert.AreEquivalent(new[] { "begin" }, effects.HideDoorUids);
        }

        [Test]
        public void BeginWave_IsIdempotentWhileRunning()
        {
            var state = State();
            state.BeginWave(RoomWithDoors(("begin", true)));
            state.t_wave = 5;
            Assert.IsTrue(state.BeginWave(RoomWithDoors(("begin", true))).IsEmpty);
            Assert.AreEqual(5, state.t_wave);
        }

        [Test]
        public void CreateWave_SpawnsThePayloadAndAdvances()
        {
            var state = State();
            ProbEffects effects = state.CreateWave(Wave(exists: true, size: 3));
            Assert.AreEqual(0, effects.SpawnWaveIndex);
            Assert.AreEqual(3, effects.SpawnWaveCount);
            Assert.AreEqual(3, state.kolEn);
            Assert.AreEqual(3, state.nspawn);
            Assert.AreEqual(1, state.nwave);
        }

        [Test]
        public void CreateWave_WithNoSuchWaveNode_DoesNotAdvance()
        {
            // Probation.as:331-334 returns before ++nwave when xml.wave[nwave] == null.
            var state = State();
            Assert.IsTrue(state.CreateWave(Wave(exists: false, size: 0)).IsEmpty);
            Assert.AreEqual(0, state.nwave);
        }

        [Test]
        public void CreateWave_WithAPresentButEmptyWave_StillAdvances()
        {
            // The mirror of the case above, and the one a naive `size == 0 -> return` gets wrong: the
            // oracle only bails on a MISSING node, so an empty payload still does ++nwave.
            var state = State();
            ProbEffects effects = state.CreateWave(Wave(exists: true, size: 0));
            Assert.AreEqual(1, state.nwave);
            Assert.AreEqual(0, state.kolEn);
            Assert.AreEqual(-1, effects.SpawnWaveIndex);
        }

        [Test]
        public void CreateWave_ScalesTheDelayAttributeByTheFrameRate()
        {
            var state = State();
            state.CreateWave(Wave(exists: true, size: 1, hasDelay: true, delaySeconds: 5));
            Assert.AreEqual(5 * ProbationState.Fps, state.t_wave);
        }

        [Test]
        public void CreateWave_WithoutADelayAttribute_LeavesTheCountdownAlone()
        {
            var state = State();
            state.t_wave = 1;
            state.CreateWave(Wave(exists: true, size: 1));
            Assert.AreEqual(1, state.t_wave);
        }

        [Test]
        public void CheckWave_DoesNothingWhileEnemiesRemain()
        {
            var state = State();
            state.SetMaxWave(2);
            state.kolEn = 3;
            state.CheckWave(killedOne: true);
            Assert.AreEqual(1, state.killEn);
            Assert.AreEqual(0, state.t_wave);
        }

        [Test]
        public void CheckWave_OnTheLastEnemyOfANonFinalWave_ArmsTheNextDelay()
        {
            var state = State();
            state.SetMaxWave(2);
            state.nwave = 1;
            state.kolEn = 1;
            state.CheckWave(killedOne: true);
            Assert.AreEqual(ProbationState.NextT, state.t_wave);
        }

        [Test]
        public void CheckWave_OnTheLastEnemyOfTheFinalWave_StopsTheTimer()
        {
            var state = State();
            state.SetMaxWave(2);
            state.nwave = 2;
            state.kolEn = 1;
            state.CheckWave(killedOne: true);
            Assert.AreEqual(0, state.t_wave);
        }

        [Test]
        public void Step_CountsDownThenFires_SoADelayOfNinetySpawnsOnTheEightyNinthStep()
        {
            // Probation.as:386-393 — decrement first, then test `== 1`. The same "counts down, then
            // fires" shape as the authored `time` attribute (lesson #4).
            var state = State();
            state.SetMaxWave(1);
            state.BeginWave(RoomWithDoors(("begin", true)));

            int steps = 0;
            ProbEffects effects = new ProbEffects();
            while (steps < 200)
            {
                steps++;
                effects = state.Step(Wave(exists: true, size: 2));
                if (effects.SpawnWaveIndex >= 0) break;
            }

            Assert.AreEqual(89, steps, "an armed delay of BegT frames fires on the BegT-1'th step");
            Assert.AreEqual(2, effects.SpawnWaveCount);
            Assert.AreEqual(1, state.nwave);
        }

        [Test]
        public void Step_PrintsTheCountdownOnceASecond()
        {
            // `t_wave % 30 == 1` is tested AFTER the decrement, so the tick that prints is the one that
            // takes t_wave to 31, 61, 91 … — not the one that leaves it at 30.
            var state = State();
            state.onWave = true;

            state.t_wave = 31;
            ProbEffects effects = state.Step(Wave(exists: false, size: 0));
            Assert.AreEqual(ProbEffects.NoMessage, effects.CountdownMessage,
                "31 decrements to 30, which is not a message tick");

            state.t_wave = 32;
            effects = state.Step(Wave(exists: false, size: 0));
            Assert.AreEqual(1, effects.CountdownMessage, "32 decrements to 31, which is");
        }

        [Test]
        public void Step_IsANoOpWhenNoWaveIsRunning()
        {
            var state = State();
            state.t_wave = 50;
            Assert.IsTrue(state.Step(Wave(exists: true, size: 1)).IsEmpty);
            Assert.AreEqual(50, state.t_wave);
        }

        [Test]
        public void ResetWave_DisablesEveryWaveUnit()
        {
            var state = State();
            state.onWave = true;
            ProbEffects effects = state.ResetWave();
            Assert.IsTrue(effects.DisableAllWaveUnits);
            Assert.IsFalse(state.onWave);
        }

        // ── prepare ────────────────────────────────────────────────────────────────────────

        [Test]
        public void Prepare_LocksThePrizesOnlyWhenTheRoomDeclaresOne()
        {
            var withPrize = State();
            withPrize.prizeActive = true;
            Assert.IsTrue(withPrize.Prepare().LockAllPrizes);

            var withoutPrize = State();
            withoutPrize.prizeActive = false;
            Assert.IsTrue(withoutPrize.Prepare().IsEmpty);
        }

        // ── FromDefinition ─────────────────────────────────────────────────────────────────

        [Test]
        public void FromDefinition_MapsTheAuthoredAttributes()
        {
            var prob = new ProbRoomDefinition
            {
                id = "p9",
                prize = true,
                close = true,
                tip = "2",
                contents = new System.Collections.Generic.List<ProbContentData> { Con("wave") },
            };

            ProbationState state = ProbationState.FromDefinition(prob, "Nice Name");

            Assert.AreEqual("p9", state.id);
            Assert.AreEqual("Nice Name", state.nazv);
            Assert.IsTrue(state.prizeActive);
            Assert.IsTrue(state.isClose);
            Assert.AreEqual(2, state.tip);
            Assert.AreEqual(1, state.contents.Count);
        }

        [Test]
        public void FromDefinition_FallsBackToTheIdForTheDisplayName()
        {
            var prob = new ProbRoomDefinition { id = "p9" };
            Assert.AreEqual("p9", ProbationState.FromDefinition(prob).nazv);
        }

        [Test]
        public void FromDefinition_ReadsANonNumericTipAsZero()
        {
            var prob = new ProbRoomDefinition { id = "p9", tip = "boss" };
            Assert.AreEqual(0, ProbationState.FromDefinition(prob).tip);
        }

        [Test]
        public void FromDefinition_OfNull_IsAnEmptyState()
        {
            ProbationState state = ProbationState.FromDefinition(null);
            Assert.AreEqual(string.Empty, state.id);
            Assert.IsFalse(state.closed);
        }

        [Test]
        public void SetMaxWave_ClampsNegativeCounts()
        {
            var state = State();
            state.SetMaxWave(-3);
            Assert.AreEqual(0, state.maxwave);
        }
    }
}
