using System;
using System.Collections.Generic;
using NUnit.Framework;
using PFE.Core.Rng;
using PFE.Systems.Particles;

namespace PFE.Tests.EditMode.Systems.Particles
{
    /// <summary>
    /// Pins <see cref="EffectVisualRules"/> — the particle half of AS3's <c>Effect.stepEffect()</c>
    /// (<c>Effect.as:474-488</c>) and <c>Effect.secEffect()</c> (<c>:405-472</c>).
    ///
    /// <para><b>Why this is worth a fixture.</b> These emits live inside a private method on an AS3
    /// class the port reaches only through a live unit, so before the split <i>none</i> of them were
    /// reachable by any test, offline or otherwise. The failure mode is the project's recurring one: an
    /// arm that emits the wrong id, or nothing, looks exactly like a missing sprite. So every "emits
    /// nothing" assertion here is paired with a positive control on a neighbouring input.</para>
    ///
    /// <para><b>The two cadences are the thing most likely to be quietly merged.</b> <c>stepEffect</c>
    /// runs every tick and <c>secEffect</c> runs once per 30 canonical frames (<c>:492</c>). One test
    /// below asserts the flame is on the <i>step</i> path and <i>not</i> the payload path, because moving
    /// it would draw the flame thirty times too rarely — and a flame that stutters reads as an art
    /// problem, not a scheduling one.</para>
    /// </summary>
    [TestFixture]
    public class EffectVisualRulesTests
    {
        private readonly List<ParticleEmit> _emits = new List<ParticleEmit>();

        // ── Harness ──────────────────────────────────────────────────────────────────────────

        private bool Step(string id, bool isInWorld = true, float spriteHeight = 80f)
            => EffectVisualRules.PlanStepVisual(
                Ctx(id, isInWorld: isInWorld, spriteHeight: spriteHeight), _emits);

        private bool Payload(string id, IRngService rng = null,
                             bool isPlayer = false, bool isInWorld = true, bool isFloating = false,
                             int level = 1, float x = 100f, float y = 200f, float spriteHeight = 80f,
                             float facing = 1f, float fetterX = 150f, float fetterY = 180f)
            => EffectVisualRules.PlanPayloadVisual(
                Ctx(id, isPlayer, isInWorld, isFloating, level, x, y, spriteHeight, facing,
                    fetterX, fetterY), rng, _emits);

        private static EffectVisualContext Ctx(
            string id, bool isPlayer = false, bool isInWorld = true, bool isFloating = false,
            int level = 1, float x = 100f, float y = 200f, float spriteHeight = 80f,
            float facing = 1f, float fetterX = 150f, float fetterY = 180f)
            => new EffectVisualContext(id, level, isPlayer, isInWorld, isFloating, x, y,
                                       spriteHeight, facing, fetterX, fetterY);

        private List<string> Ids()
        {
            var ids = new List<string>(_emits.Count);
            foreach (ParticleEmit e in _emits) ids.Add(e.Id);
            return ids;
        }

        // ── Fakes ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Counts the <c>Range(float,float)</c> draws it is asked for, so "the blindness arm is the only
        /// one that rolls" is testable. <b>It counts the float overload specifically</b> — the sibling
        /// explosion fixture counts the int one, and a copy of that fake here would report zero draws for
        /// every arm and read as a passing test.
        /// </summary>
        private sealed class CountingRng : IRngService
        {
            private readonly IRngService _inner = new PcgRngService(12345UL);
            public int Draws;

            public uint NextUInt() => _inner.NextUInt();
            public float NextFloat() => _inner.NextFloat();
            public int NextInt(int maxExclusive) => _inner.NextInt(maxExclusive);
            public int Range(int minInclusive, int maxExclusive) => _inner.Range(minInclusive, maxExclusive);
            public float Range(float min, float max) { Draws++; return _inner.Range(min, max); }
            public bool Chance(float probability) => _inner.Chance(probability);
            public void Shuffle<T>(IList<T> list) => _inner.Shuffle(list);
            public IRngService GetStream(RngStream stream, int? salt = null) => _inner.GetStream(stream, salt);
        }

        /// <summary>
        /// Returns queued values from <c>Range(float,float)</c>, so the two blindness draws can be given
        /// distinct values and their destinations checked independently.
        /// </summary>
        private sealed class QueueRng : IRngService
        {
            private readonly Queue<float> _queue = new Queue<float>();
            public int Draws;

            public QueueRng(params float[] values) { foreach (float v in values) _queue.Enqueue(v); }

            public uint NextUInt() => 0u;
            public float NextFloat() => 0f;
            public int NextInt(int maxExclusive) => 0;
            public int Range(int minInclusive, int maxExclusive) => minInclusive;
            public float Range(float min, float max) { Draws++; return _queue.Dequeue(); }
            public bool Chance(float probability) => false;
            public void Shuffle<T>(IList<T> list) { }
            public IRngService GetStream(RngStream stream, int? salt = null) => this;
        }

        // ── stepEffect(): the flame, every tick ──────────────────────────────────────────────

        [Test]
        public void Burning_InTheWorld_EmitsTheFlameAtTheBodysMidHeight()
        {
            Assert.IsTrue(Step(EffectVisualRules.BurningId));
            CollectionAssert.AreEqual(new[] { EffectVisualRules.FlameId }, Ids());

            // `Y - scY / 2` (:480) — a negative offset in a space whose Y runs DOWN = upward.
            Assert.AreEqual(0f, _emits[0].OffsetX, "the flame is centred on the owner, not offset sideways");
            Assert.AreEqual(-40f, _emits[0].OffsetY, "80 / 2 = 40 upward");
        }

        [Test]
        public void Burning_TheFlameTracksTheSpriteHeight_ItIsNotAConstant()
        {
            // A unit twice as tall hangs its flame twice as far up. A hardcoded -40 would pass the test
            // above and fail this one.
            Assert.IsTrue(Step(EffectVisualRules.BurningId, spriteHeight: 160f));
            Assert.AreEqual(-80f, _emits[0].OffsetY);
        }

        [Test]
        public void Burning_OfARemovedUnit_EmitsNothing_AndThatIsDistinguishableFromAMissingArm()
        {
            // `if(this.owner.sost < 4)` (:478). The gate is sost, not "alive" — a dying unit still burns.
            Assert.IsFalse(Step(EffectVisualRules.BurningId, isInWorld: false));
            Assert.IsEmpty(_emits);

            // Positive control: the same call with the unit in the world emits, so the `false` above is a
            // real decision rather than the flame arm having been deleted.
            Assert.IsTrue(Step(EffectVisualRules.BurningId, isInWorld: true));
            Assert.IsNotEmpty(_emits);
        }

        [Test]
        public void AnIdWithNoStepVisual_EmitsNothing()
        {
            Assert.IsFalse(Step(EffectVisualRules.PoisonId));
            Assert.IsEmpty(_emits);

            // Control: a neighbouring id on the same helper DOES emit.
            Assert.IsTrue(Step(EffectVisualRules.BurningId));
            Assert.IsNotEmpty(_emits);
        }

        // ── The two cadences are not one cadence ─────────────────────────────────────────────

        [Test]
        public void TheFlameIsOnTheStepPath_NotThePayloadPath()
        {
            // `burning` also appears in secEffect (:409), but that arm damages and resets t — it emits no
            // particle. If the flame were moved onto the payload path it would fire once per 30 frames
            // instead of every frame, and the effect would merely look sparse.
            Assert.IsFalse(Payload(EffectVisualRules.BurningId));
            Assert.IsEmpty(_emits);

            // Positive control: the flame IS emitted by the step path for the same id.
            Assert.IsTrue(Step(EffectVisualRules.BurningId));
            CollectionAssert.AreEqual(new[] { EffectVisualRules.FlameId }, Ids());
        }

        // ── secEffect(): blindness ───────────────────────────────────────────────────────────

        [Test]
        public void Blindness_OnThePlayer_ScattersABlindOverTheOwnersNeighbourhood()
        {
            var rng = new QueueRng(123.5f, -45.25f);

            Assert.IsTrue(Payload(EffectVisualRules.BlindnessId, rng, isPlayer: true));
            CollectionAssert.AreEqual(new[] { EffectVisualRules.BlindId }, Ids());

            // The two draws are the X and the Y of the scatter, in that order (:429). Swapping them
            // would still land "somewhere near the unit", so the distinct values matter.
            Assert.AreEqual(123.5f, _emits[0].OffsetX, "the first draw is the horizontal scatter");
            Assert.AreEqual(-45.25f, _emits[0].OffsetY, "the second draw is the vertical scatter");
        }

        [Test]
        public void Blindness_OnANonPlayer_EmitsNothing_AndThatIsDistinguishableFromAMissingArm()
        {
            // `if(this.id == "blindness" && this.player)` (:425) — the burst is a full-view effect, so it
            // is gated on the owner being the player.
            Assert.IsFalse(Payload(EffectVisualRules.BlindnessId, new QueueRng(0f, 0f), isPlayer: false));
            Assert.IsEmpty(_emits);

            // Positive control: the SAME call with the player flag set emits.
            Assert.IsTrue(Payload(EffectVisualRules.BlindnessId, new QueueRng(0f, 0f), isPlayer: true));
            Assert.IsNotEmpty(_emits);
        }

        [Test]
        public void Blindness_OnARemovedPlayer_EmitsNothing()
        {
            Assert.IsFalse(Payload(EffectVisualRules.BlindnessId, new QueueRng(0f, 0f),
                                   isPlayer: true, isInWorld: false));
            Assert.IsEmpty(_emits);

            // Positive control on the world flag alone, player flag held true.
            Assert.IsTrue(Payload(EffectVisualRules.BlindnessId, new QueueRng(0f, 0f),
                                  isPlayer: true, isInWorld: true));
        }

        [Test]
        public void TheBlindScatterBoundsAreTheOraclesLiterals_NotWhateverTheConstantHappensToBe()
        {
            // `X - 300 + Math.random() * 600` and `Y - 200 + Math.random() * 400` (:429). These are
            // written as literals on purpose: the distribution test below and the no-RNG fallback test
            // both read BlindScatterX to build their expectation, so a change to BlindScatterX would
            // move the expectation with it and neither would go red. This is the only assertion that
            // pins the numbers the oracle actually wrote.
            Assert.AreEqual(300f, EffectVisualRules.BlindScatterX, "AS3 `-300 + rand*600`");
            Assert.AreEqual(200f, EffectVisualRules.BlindScatterY, "AS3 `-200 + rand*400`");
        }

        [Test]
        public void TheBlindScatterSpansMinusThreeHundredToThreeHundred_AndMinusTwoHundredToTwoHundred()
        {
            // A constant wearing a range's clothes would pass every other blindness test here. The
            // thresholds are literals, not `BlindScatterX * 0.9f` — the latter would slide with the
            // very constant under test and could never fail.
            var rng = new PcgRngService(2026UL);
            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;

            for (int i = 0; i < 2000; i++)
            {
                Assert.IsTrue(Payload(EffectVisualRules.BlindnessId, rng, isPlayer: true));
                minX = Math.Min(minX, _emits[0].OffsetX);
                maxX = Math.Max(maxX, _emits[0].OffsetX);
                minY = Math.Min(minY, _emits[0].OffsetY);
                maxY = Math.Max(maxY, _emits[0].OffsetY);
            }

            Assert.Less(minX, -299f, "the floor (-300) is reachable");
            Assert.Greater(maxX, 299f, "the ceiling (+300) is reachable");
            Assert.Less(minY, -199f, "the floor (-200) is reachable");
            Assert.Greater(maxY, 199f, "the ceiling (+200) is reachable");

            // And nothing escapes the interval — the scatter is bounded, not merely wide.
            Assert.GreaterOrEqual(minX, -300f);
            Assert.Less(maxX, 300f);
            Assert.GreaterOrEqual(minY, -200f);
            Assert.Less(maxY, 200f);
        }

        [Test]
        public void Blindness_WithNoRng_FallsBackToTheOraclesLowerBound()
        {
            // An offline host injects no RNG; the arm must still emit rather than throw. The expected
            // values are literals so this stays a check on the rule, not on the constant it reads.
            Assert.IsTrue(Payload(EffectVisualRules.BlindnessId, rng: null, isPlayer: true));
            Assert.AreEqual(-300f, _emits[0].OffsetX);
            Assert.AreEqual(-200f, _emits[0].OffsetY);
        }

        // ── secEffect(): drunk ───────────────────────────────────────────────────────────────

        [Test]
        public void Drunk_PastLevelThree_DripsPoisonOffTheFacingSide()
        {
            Assert.IsTrue(Payload(EffectVisualRules.DrunkId, level: EffectVisualRules.DrunkPoisonLevel + 1));
            CollectionAssert.AreEqual(new[] { EffectVisualRules.PoisonId }, Ids());

            // `X + storona * 20, Y - 40` (:439).
            Assert.AreEqual(20f, _emits[0].OffsetX, "facing +1 hangs the drip to the right");
            Assert.AreEqual(-40f, _emits[0].OffsetY);
        }

        [Test]
        public void Drunk_AtOrBelowLevelThree_DripsNothing_AndThatIsDistinguishableFromAMissingArm()
        {
            // `this.lvl > 3` (:436) is strictly greater: level 3 is sober enough to show nothing.
            Assert.IsFalse(Payload(EffectVisualRules.DrunkId, level: EffectVisualRules.DrunkPoisonLevel));
            Assert.IsEmpty(_emits);
            Assert.IsFalse(Payload(EffectVisualRules.DrunkId, level: 1));
            Assert.IsEmpty(_emits);

            // Positive control: one level higher emits.
            Assert.IsTrue(Payload(EffectVisualRules.DrunkId, level: EffectVisualRules.DrunkPoisonLevel + 1));
            Assert.IsNotEmpty(_emits);
        }

        [Test]
        public void Drunk_FacingLeft_MirrorsTheDrip()
        {
            Assert.IsTrue(Payload(EffectVisualRules.DrunkId, level: 4, facing: -1f));
            Assert.AreEqual(-20f, _emits[0].OffsetX, "facing -1 hangs the drip to the left");
        }

        [Test]
        public void Drunk_IgnoresThePlayerAndWorldGates_ItReadsOnlyTheLevel()
        {
            // The gates are per-arm, not uniform: `drunk` has no sost or player test in the oracle. If
            // the blindness gates were applied here by reflex the drip would silently stop for monsters
            // and for dying units — both plausible, neither correct.
            Assert.IsTrue(Payload(EffectVisualRules.DrunkId, level: 4,
                                  isPlayer: false, isInWorld: false, isFloating: true));
            CollectionAssert.AreEqual(new[] { EffectVisualRules.PoisonId }, Ids());
        }

        // ── secEffect(): namok ───────────────────────────────────────────────────────────────

        [Test]
        public void Namok_OnAWetButNotSubmergedUnit_DripsAQuarterWayUpTheBody()
        {
            Assert.IsTrue(Payload(EffectVisualRules.NamokId));
            CollectionAssert.AreEqual(new[] { EffectVisualRules.KapId }, Ids());

            // `Y - scY * 0.25` (:445) — 80 * 0.25 = 20 upward.
            Assert.AreEqual(0f, _emits[0].OffsetX);
            Assert.AreEqual(-20f, _emits[0].OffsetY);
        }

        [Test]
        public void Namok_CarriesTheTenthSpeedOverride()
        {
            // The oracle passes `{"md":0.1}` (:445), and `md` scales the part's dx/dy
            // (Emitter.as:254-257). Dropping the spec would leave the drip at full row speed, which
            // reads as a fast drip rather than as a bug. The expected value is a literal so this stays
            // a check on the rule rather than on the constant it reads.
            Assert.IsTrue(Payload(EffectVisualRules.NamokId));
            Assert.IsNotNull(_emits[0].Spec, "the drip carries an explicit md");
            Assert.AreEqual(0.1f, _emits[0].Spec.Md);
        }

        [Test]
        public void Namok_OnASubmergedUnit_DripsNothing_AndThatIsDistinguishableFromAMissingArm()
        {
            // `if(!this.owner.isPlav && this.owner.sost < 4)` (:443) — a unit already under water needs
            // no droplet.
            Assert.IsFalse(Payload(EffectVisualRules.NamokId, isFloating: true));
            Assert.IsEmpty(_emits);

            // Positive control: the same call, not submerged, emits.
            Assert.IsTrue(Payload(EffectVisualRules.NamokId, isFloating: false));
            Assert.IsNotEmpty(_emits);
        }

        [Test]
        public void Namok_OnARemovedUnit_DripsNothing()
        {
            Assert.IsFalse(Payload(EffectVisualRules.NamokId, isInWorld: false));
            Assert.IsEmpty(_emits);

            Assert.IsTrue(Payload(EffectVisualRules.NamokId, isInWorld: true));
        }

        // ── secEffect(): fetter ──────────────────────────────────────────────────────────────

        [Test]
        public void Fetter_AnchorsOnTheRingsOwnPosition_NotTheOwners()
        {
            // `Emitter.emit("slow", loc, (owner as UnitPlayer).fetX, (owner as UnitPlayer).fetY)` (:470)
            // — an ABSOLUTE position, so the rule carries the delta back to the caller's anchor.
            Assert.IsTrue(Payload(EffectVisualRules.FetterId, isPlayer: true, x: 100f, y: 200f,
                                  fetterX: 150f, fetterY: 180f));
            CollectionAssert.AreEqual(new[] { EffectVisualRules.SlowId }, Ids());
            Assert.AreEqual(50f, _emits[0].OffsetX);
            Assert.AreEqual(-20f, _emits[0].OffsetY);
        }

        [Test]
        public void Fetter_OnANonPlayer_EmitsNothing_AndThatIsDistinguishableFromAMissingArm()
        {
            // `(owner as UnitPlayer).fetX` (:470) THROWS for a non-player owner, so AS3 cannot reach
            // this emit for a monster. The port substitutes an IsPlayer test. Without it the rule would
            // emit `slow` at (0 - X, 0 - Y) — the mirror of the owner's own position, a ring somewhere
            // else in the room that looks like a real effect rather than an error.
            Assert.IsFalse(Payload(EffectVisualRules.FetterId, isPlayer: false));
            Assert.IsEmpty(_emits);

            // Positive control: the same call for the player DOES emit, so the `false` above is a real
            // decision rather than the arm having been deleted.
            Assert.IsTrue(Payload(EffectVisualRules.FetterId, isPlayer: true));
            CollectionAssert.AreEqual(new[] { EffectVisualRules.SlowId }, Ids());
        }

        [Test]
        public void Fetter_IgnoresTheWorldAndSubmersionGates_ItReadsOnlyTheOwnerType()
        {
            // The gates are per-arm. `fetter` has no `sost` or `isPlav` test in the oracle — the ring
            // hangs on the player's own position, which is meaningful whether or not the body is
            // submerged. Every other flag is deliberately hostile here.
            Assert.IsTrue(Payload(EffectVisualRules.FetterId, isPlayer: true,
                                  isInWorld: false, isFloating: true, level: 0));
            CollectionAssert.AreEqual(new[] { EffectVisualRules.SlowId }, Ids());
        }

        // ── The non-particle arms ────────────────────────────────────────────────────────────

        [Test]
        public void ADamageOnlyEffectId_EmitsNothing()
        {
            // `chemburn` (:432), `pinkcloud` (:421) and `hydra` (:448) damage or heal; `inhibitor`
            // (:458) slows neighbours. None emits a particle, and none should fall through to an emit.
            Assert.IsFalse(Payload("chemburn"));
            Assert.IsFalse(Payload("pinkcloud"));
            Assert.IsFalse(Payload("hydra"));
            Assert.IsFalse(Payload("inhibitor"));
            Assert.IsEmpty(_emits);

            // Positive control: a neighbouring id on the same helper DOES emit.
            Assert.IsTrue(Payload(EffectVisualRules.FetterId, isPlayer: true));
            Assert.IsNotEmpty(_emits);
        }

        // ── Draw accounting ──────────────────────────────────────────────────────────────────

        [Test]
        public void OnlyTheBlindnessArmDraws_AndItDrawsExactlyTwice()
        {
            var rng = new CountingRng();

            Payload(EffectVisualRules.DrunkId, rng, level: 4);
            Assert.AreEqual(0, rng.Draws, "drunk consumes no draw");

            Payload(EffectVisualRules.NamokId, rng);
            Assert.AreEqual(0, rng.Draws, "namok consumes no draw");

            Payload(EffectVisualRules.FetterId, rng);
            Assert.AreEqual(0, rng.Draws, "fetter consumes no draw");

            // Positive control: the blindness arm DOES draw, exactly twice — one per scatter axis.
            Payload(EffectVisualRules.BlindnessId, rng, isPlayer: true);
            Assert.AreEqual(2, rng.Draws);
        }

        [Test]
        public void AGatedOutBlindness_DrawsNothing()
        {
            // The draws must sit INSIDE the gates. If they were hoisted above the player check the
            // presentation stream would advance for every blinded monster in the room, desynchronising
            // it from the oracle without any visible symptom.
            var rng = new CountingRng();
            Assert.IsFalse(Payload(EffectVisualRules.BlindnessId, rng, isPlayer: false));
            Assert.AreEqual(0, rng.Draws);

            // Positive control: the same call as the player does draw.
            Assert.IsTrue(Payload(EffectVisualRules.BlindnessId, rng, isPlayer: true));
            Assert.AreEqual(2, rng.Draws);
        }

        [Test]
        public void EmitsIsClearedBetweenCalls()
        {
            // The rules own the list; a caller reusing one buffer must not accumulate.
            Assert.IsTrue(Payload(EffectVisualRules.FetterId, isPlayer: true));
            Assert.AreEqual(1, _emits.Count);
            Assert.IsFalse(Payload("chemburn"));
            Assert.IsEmpty(_emits);
        }

        [Test]
        public void ANullEmitList_IsRejectedRatherThanSilentlyIgnored()
        {
            Assert.Throws<ArgumentNullException>(
                () => EffectVisualRules.PlanStepVisual(Ctx(EffectVisualRules.BurningId), null));
            Assert.Throws<ArgumentNullException>(
                () => EffectVisualRules.PlanPayloadVisual(Ctx(EffectVisualRules.FetterId), null, null));
        }
    }
}
