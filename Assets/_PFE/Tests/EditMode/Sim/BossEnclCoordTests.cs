using System.Collections.Generic;
using NUnit.Framework;
using PFE.Systems.Combat;

namespace PFE.Tests.EditMode.Sim
{
    /// <summary>
    /// Offline fixtures for <see cref="BossEnclCoord"/> — AS3 <c>Coord</c>, the one timer the Enclave
    /// Commander's three bodies share (<c>Coord.as</c>).
    ///
    /// <para><b>This object is the encounter.</b> There is no phase table and no state ladder in
    /// <c>UnitBossEncl</c>; the fight's shape is "which commander may fire right now", and that answer
    /// lives here. A defect in this file does not look like a bug — it looks like a boss that shoots a
    /// bit too often, or an arena whose doors never telegraph.</para>
    ///
    /// <para><b>Randomness is a scripted delegate.</b> <c>Math.random()</c> has no place in a pure class,
    /// and a fixture that could not control the draw could not pin the rotation at all. The rolls are
    /// handed in as an inclusive-max function, and the sequences below say exactly what each draw is for
    /// so a failure names the arm rather than just a number.</para>
    /// </summary>
    [TestFixture]
    public class BossEnclCoordTests
    {
        /// <summary>A roll that always returns the bottom of its range.</summary>
        static int AlwaysZero(int max) => 0;

        /// <summary>A roll driven by a scripted queue, so each draw is attributable.</summary>
        sealed class ScriptedRolls
        {
            readonly Queue<int> _values;
            public int Draws { get; private set; }

            public ScriptedRolls(params int[] values) => _values = new Queue<int>(values);

            public int Roll(int max)
            {
                Draws++;
                if (_values.Count == 0) return 0;
                return _values.Dequeue();
            }
        }

        // ── the constructor (Coord.as:30-39) ──────────────────────────────────────────────────────

        /// <summary>
        /// <c>this.tr = 1; this.t1 = 100; this.t2 = 150;</c> then <c>rndOpened()</c>
        /// (<c>:35-38</c>).
        ///
        /// <para><b><c>t1</c> starts at 100 and not 150.</b> The first firing window is shorter than
        /// every later one, which is why the opening beat of the fight is quicker than the loop that
        /// follows — a port that seeded 150 would delay the first hand-off by 50 ticks.</para>
        /// </summary>
        [Test]
        public void Constructor_SeedsTr1AndTheTwoTimers()
        {
            var coord = new BossEnclCoord(AlwaysZero);

            Assert.AreEqual(1, coord.Tr, "this.tr = 1 (Coord.as:35)");
            Assert.AreEqual(100, coord.T1, "this.t1 = 100 (Coord.as:36)");
            Assert.AreEqual(150, coord.T2, "this.t2 = 150 (Coord.as:37)");
        }

        /// <summary>
        /// <c>rndOpened()</c> (<c>:41-55</c>): all six open, then <c>kolClosed = 3</c> draws shut one
        /// slot each.
        /// </summary>
        [Test]
        public void Constructor_ShutsThreeDoors()
        {
            // Three draws that land on distinct slots — the ordinary case.
            var coord = new BossEnclCoord(new ScriptedRolls(0, 1, 2).Roll);

            Assert.AreEqual(6, coord.Opened.Length, "kolAll = 6 (Coord.as:24)");

            int closed = 0;
            for (int i = 0; i < coord.Opened.Length; i++)
            {
                if (!coord.Opened[i]) closed++;
            }

            Assert.AreEqual(3, closed,
                "three draws of floor(random*6+1) close three slots when they land on distinct doors (Coord.as:50-54)");
        }

        /// <summary>
        /// <b>Duplicate draws are the oracle's, and they leave a door open.</b> Three draws from six slots
        /// are independent, so a cycle can shut one door rather than three. Reproducing that is a
        /// deliberate choice: "fixing" it to a shuffle would make the arena regularly sealed in a way the
        /// original never is.
        /// </summary>
        [Test]
        public void RandomizeDoors_AllowsDuplicates_AndThenClosesFewerThanThree()
        {
            var coord = new BossEnclCoord(AlwaysZero);

            int closed = 0;
            for (int i = 0; i < coord.Opened.Length; i++)
            {
                if (!coord.Opened[i]) closed++;
            }

            Assert.AreEqual(1, closed,
                "three draws that all land on slot 0 shut ONE door — `opened[floor(random*6+1)] = false` " +
                "is not a shuffle (Coord.as:50-54)");
            Assert.IsFalse(coord.Opened[0], "slot 0 is the one the three draws hit");
            for (int i = 1; i < coord.Opened.Length; i++)
            {
                Assert.IsTrue(coord.Opened[i], $"slot {i} was never drawn, so it stays open");
            }
        }

        // ── step(): the firing rotation (Coord.as:57-78) ──────────────────────────────────────────

        /// <summary>
        /// Every step decrements both timers, and nothing else happens while <c>t1 &gt; 0</c>.
        /// </summary>
        [Test]
        public void Step_DecrementsBothTimers()
        {
            var coord = new BossEnclCoord(AlwaysZero);

            BossEnclCoordEvents events = coord.Step();

            Assert.AreEqual(99, coord.T1, "--this.t1 (:60)");
            Assert.AreEqual(149, coord.T2, "--this.t2 (:79)");
            Assert.AreEqual(BossEnclCoordEvents.None, events, "no broadcast on an ordinary frame");
            Assert.AreEqual(1, coord.Tr, "the right has not moved yet");
        }

        /// <summary>
        /// <c>--t1; if(t1 &lt;= 0) { t1 = floor(random*60+150); ++tr ... }</c> (<c>:60-78</c>) — the
        /// window expires on the step that takes it to <b>zero</b>, not below it.
        /// </summary>
        [Test]
        public void Step_ExpiredWindow_RollsANewOneAndHandsTheRightOn()
        {
            var coord = new BossEnclCoord(AlwaysZero);
            coord.SetAlive(1, true);
            coord.SetAlive(2, true);
            coord.SetAlive(3, true);

            // t1 starts at 100, so the 100th step is the one that takes it to 0.
            for (int i = 0; i < 99; i++) coord.Step();
            Assert.AreEqual(1, coord.T1, "after 99 steps t1 == 1 and the right is still tr1's");
            Assert.AreEqual(1, coord.Tr, "no hand-off before the window expires");

            coord.Step();

            Assert.AreEqual(2, coord.Tr, "++tr on expiry: 1 -> 2 (Coord.as:67)");
            Assert.AreEqual(150, coord.T1, "t1 = floor(random*60+150) with a 0 roll -> 150 (Coord.as:63)");
        }

        /// <summary>
        /// The rotation skips a dead commander — <c>if(this["liv"+this.tr]) break;</c>
        /// (<c>:72-75</c>) — which is how a killed body stops receiving a turn it can never use.
        /// </summary>
        [Test]
        public void Step_ExpiredWindow_SkipsADeadCommander()
        {
            var coord = new BossEnclCoord(AlwaysZero);
            coord.SetAlive(1, true);
            coord.SetAlive(2, false);
            coord.SetAlive(3, true);

            for (int i = 0; i < 100; i++) coord.Step();

            Assert.AreEqual(3, coord.Tr,
                "1 -> 2 fails `liv2`, so the loop increments again and lands on 3 (Coord.as:64-77)");
        }

        /// <summary>
        /// <c>if(coord.tr == this.tr &amp;&amp; coord.t1 &gt; 45)</c> (<c>UnitBossEncl.as:306</c>) — the
        /// gate the brain asks this object for.
        /// </summary>
        [Test]
        public void CanFire_IsTheRightAndTheStrictGate()
        {
            var coord = new BossEnclCoord(AlwaysZero);
            coord.Tr = 2;
            coord.T1 = 46;

            Assert.IsTrue(coord.CanFire(2), "46 > 45 and tr2 holds the right (:306)");
            Assert.IsFalse(coord.CanFire(1), "tr1 does not hold the right");
            Assert.IsFalse(coord.CanFire(3), "tr3 does not hold the right");

            coord.T1 = 45;
            Assert.IsFalse(coord.CanFire(2), "45 is not > 45 (:306)");
        }

        // ── step(): the door cycle (Coord.as:79-99) ───────────────────────────────────────────────

        /// <summary>
        /// <c>if(this.t2 == 75)</c> raises the telegraph exactly once per cycle (<c>:80-88</c>).
        /// </summary>
        [Test]
        public void Step_SignalsDoorsAtT2SeventyFive()
        {
            var coord = new BossEnclCoord(AlwaysZero);

            // t2 starts at 150 and the signal fires on the step that takes it TO 75, i.e. step 75.
            for (int i = 0; i < 74; i++)
            {
                Assert.AreEqual(BossEnclCoordEvents.None, coord.Step(), $"no event before t2 == 75 (step {i + 1})");
            }

            Assert.AreEqual(76, coord.T2, "74 steps leave t2 at 76");
            Assert.AreEqual(BossEnclCoordEvents.SignalDoors, coord.Step(),
                "`if(this.t2 == 75)` fires the red/green telegraph (Coord.as:80-88)");
            Assert.AreEqual(75, coord.T2, "and the cycle keeps counting");

            Assert.AreEqual(BossEnclCoordEvents.None, coord.Step(), "the signal is a one-shot per cycle");
        }

        /// <summary>
        /// <c>if(this.t2 == 0) { ...open/close...; this.t2 = 300; this.rndOpened(); }</c>
        /// (<c>:89-99</c>) — the toggle, the restart and the re-roll, in that order.
        /// </summary>
        [Test]
        public void Step_TogglesDoorsAtT2Zero_AndRestartsTheCycle()
        {
            var scripted = new ScriptedRolls(0, 1, 2, 3);   // constructor: 3 draws; toggle: 3 more
            var coord = new BossEnclCoord(scripted.Roll);

            // Keep t1 from expiring during the run, so every draw after the constructor's is a door draw.
            coord.T1 = 100000;

            // Consume the constructor's three draws so the toggle's own rolls are distinguishable.
            Assert.AreEqual(3, scripted.Draws, "rndOpened() in the constructor draws kolClosed = 3 times");

            BossEnclCoordEvents events = BossEnclCoordEvents.None;
            for (int i = 0; i < 150; i++)
            {
                events = coord.Step();
            }

            Assert.AreEqual(BossEnclCoordEvents.ToggleDoors, events,
                "`if(this.t2 == 0)` is the open/close broadcast (Coord.as:89-96)");
            Assert.AreEqual(300, coord.T2, "this.t2 = 300 (Coord.as:97)");
            Assert.AreEqual(6, scripted.Draws,
                "rndOpened() runs AFTER the toggle, so the verbs use the set that just ended (Coord.as:98)");
        }

        /// <summary>
        /// The verbs are chosen by the same slot the toggle acts on: open set → <c>"red"</c>/<c>"open"</c>,
        /// shut set → <c>"green"</c>/<c>"close"</c>.
        ///
        /// <para><b>Both halves, from one table.</b> The telegraph and the command are read off
        /// <see cref="BossEnclCoord.Opened"/> at different times — 75 ticks apart — so a port that
        /// re-derived one of them from the other's state would flash the arena in reverse.</para>
        /// </summary>
        [Test]
        public void DoorVerbs_FollowTheOpenedTable()
        {
            var coord = new BossEnclCoord(AlwaysZero);
            coord.Opened[0] = true;
            coord.Opened[1] = false;

            Assert.AreEqual("red", coord.DoorSignalVerb(0),
                "opened[0] -> \"red\" (Coord.as:85) — red means 'in the open set', not 'closed'");
            Assert.AreEqual("green", coord.DoorSignalVerb(1), "!opened[1] -> \"green\" (Coord.as:85)");

            Assert.AreEqual("open", coord.DoorCommandVerb(0), "opened[0] -> \"open\" (Coord.as:94)");
            Assert.AreEqual("close", coord.DoorCommandVerb(1), "!opened[1] -> \"close\" (Coord.as:94)");
        }

        /// <summary>
        /// <c>"a" + i</c> with a <b>one-based</b> index (<c>Coord.as:85</c>), against the port's
        /// zero-based array — the only place the offset exists.
        /// </summary>
        [Test]
        public void DoorTag_IsOneBased()
        {
            Assert.AreEqual("a1", BossEnclCoord.DoorTag(0), "slot 0 is `a1` (Coord.as:85)");
            Assert.AreEqual("a6", BossEnclCoord.DoorTag(5), "slot 5 is `a6`, the last of kolAll = 6");
        }

        // ── the shared-timer guard ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Three commanders call in on the same frame; the timer must advance <b>once</b>.
        ///
        /// <para><b>This is the whole reason the guard exists.</b> AS3 steps <c>Coord</c> from
        /// <c>Location.step()</c>, once, outside every unit. The port has no such seam for a prob room, so
        /// each commander asks — and without <see cref="BossEnclCoord.StepIfDue"/> three bodies would
        /// drain the firing window at three times the oracle's rate, which would make the rotation
        /// visibly faster without changing a single literal.</para>
        /// </summary>
        [Test]
        public void StepIfDue_AdvancesOncePerTick()
        {
            var coord = new BossEnclCoord(AlwaysZero);

            coord.StepIfDue(1000);
            Assert.AreEqual(99, coord.T1, "the first caller advances the timer");

            coord.StepIfDue(1000);
            coord.StepIfDue(1000);
            Assert.AreEqual(99, coord.T1,
                "the second and third callers on the same tick must not decrement it again");

            coord.StepIfDue(1001);
            Assert.AreEqual(98, coord.T1, "the next tick advances it once more");
        }

        /// <summary>
        /// A tick index that repeats is ignored; one that goes backwards is not the same tick and does
        /// advance — the guard is equality, not ordering.
        /// </summary>
        [Test]
        public void StepIfDue_GuardsOnEquality_NotOrdering()
        {
            var coord = new BossEnclCoord(AlwaysZero);

            coord.StepIfDue(7);
            coord.StepIfDue(6);

            Assert.AreEqual(98, coord.T1, "tick 6 != tick 7, so it advances (the guard is `==`)");
        }

        // ── the liv table (UnitBossEncl.as:96-111) ───────────────────────────────────────────────

        /// <summary>
        /// <c>putLoc</c> writes <c>true</c> and <c>die()</c> writes <c>false</c> (<c>:99</c>,
        /// <c>:110</c>), and a <c>tr</c> outside 1..3 has no slot at all.
        /// </summary>
        [Test]
        public void SetAlive_WritesOnlyTheThreeRealSlots()
        {
            var coord = new BossEnclCoord(AlwaysZero);

            coord.SetAlive(1, true);
            coord.SetAlive(2, true);
            coord.SetAlive(3, true);
            Assert.IsTrue(coord.IsAlive(1) && coord.IsAlive(2) && coord.IsAlive(3), "all three placed");

            coord.SetAlive(2, false);
            Assert.IsTrue(coord.IsAlive(1), "tr1 is untouched");
            Assert.IsFalse(coord.IsAlive(2), "die() clears liv2 (:99)");
            Assert.IsTrue(coord.IsAlive(3), "tr3 is untouched");

            Assert.IsFalse(coord.IsAlive(4), "there is no liv4 — the table is three slots wide (:18-22)");
            Assert.IsFalse(coord.IsAlive(0), "and no liv0 either");
        }

        /// <summary>
        /// The live flags start <c>false</c> (<c>:18-22</c>), so a rotation that runs before any
        /// commander has been placed finds no candidate and leaves <c>tr</c> where the bound put it.
        /// </summary>
        [Test]
        public void Constructor_StartsWithEveryCommanderDead()
        {
            var coord = new BossEnclCoord(AlwaysZero);

            Assert.IsFalse(coord.IsAlive(1), "liv1:Boolean = false (Coord.as:18)");
            Assert.IsFalse(coord.IsAlive(2), "liv2:Boolean = false (Coord.as:19)");
            Assert.IsFalse(coord.IsAlive(3), "liv3:Boolean = false (Coord.as:20)");
        }
    }
}
