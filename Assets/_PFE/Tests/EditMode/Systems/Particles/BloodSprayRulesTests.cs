using System;
using System.Collections.Generic;
using NUnit.Framework;
using PFE.Core.Rng;
using PFE.Data.Definitions;
using PFE.Systems.Particles;

namespace PFE.Tests.EditMode.Systems.Particles
{
    /// <summary>
    /// Pins <see cref="BloodSprayRules"/> — the port of AS3 <c>Unit.damage()</c>'s blood block
    /// (<c>fe/unit/Unit.as:3844-3901</c>).
    ///
    /// <para><b>Why this is worth a fixture.</b> Three things in this block are invisible to every
    /// other kind of check. The gate is a five-way damage-type list, so a wrong list sprays blood for
    /// a plasma hit and nobody notices until it is drawn. The two anchor branches produce emits that
    /// differ only in <i>where</i> they appear, which a screenshot cannot localise. And the gib's
    /// probability is a damage-versus-draw comparison with two scalings, so "it gibs" is true for a
    /// wide range of wrong code. Each of those gets a positive control beside it, because a test that
    /// asserts "nothing happened" also passes when the whole rule was deleted.</para>
    /// </summary>
    [TestFixture]
    public class BloodSprayRulesTests
    {
        private readonly List<ParticleEmit> _emits = new List<ParticleEmit>();

        // ── Builders ─────────────────────────────────────────────────────────────────────────

        private static BloodSprayContext Hit(
            BloodType blood = BloodType.Red,
            DamageType type = DamageType.PhysicalBullet,
            float damage = 30f,
            float mass = 1f,
            bool crit = false,
            bool hasBullet = true,
            float impactX = 200f,
            float impactY = 120f,
            float dirX = 1f,
            float dirY = 0f,
            float unitX = 100f,
            float unitY = 100f,
            float scX = 60f,
            float scY = 80f)
            => new BloodSprayContext(blood, type, damage, mass, crit, hasBullet,
                                     impactX, impactY, dirX, dirY, unitX, unitY, scX, scY);

        private bool Plan(BloodSprayContext ctx, IRngService rng = null)
            => BloodSprayRules.Plan(in ctx, rng, _emits);

        private ParticleEmit Last => _emits[_emits.Count - 1];

        private List<string> Ids()
        {
            var ids = new List<string>(_emits.Count);
            foreach (ParticleEmit e in _emits) ids.Add(e.Id);
            return ids;
        }

        private bool Gibbed() => _emits.Count > 0 && Last.Id.StartsWith(BloodSprayRules.ExplosionIdPrefix);

        // ── Fakes ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Replays a fixed sequence of <c>Math.random()</c> values, so a probability boundary can be
        /// tested exactly instead of statistically. Once the queue runs dry the last value repeats,
        /// which keeps a test that only cares about the first draw from having to enumerate the rest.
        /// </summary>
        private sealed class FixedRng : IRngService
        {
            private readonly Queue<float> _values;
            private float _last;

            public FixedRng(params float[] values) { _values = new Queue<float>(values); }

            public float NextFloat()
            {
                if (_values.Count > 0) _last = _values.Dequeue();
                return _last;
            }

            public int NextInt(int maxExclusive) => 0;
            public int Range(int minInclusive, int maxExclusive) => minInclusive;
            public float Range(float min, float max) => min;
            public uint NextUInt() => 0u;
            public bool Chance(float probability) => false;
            public void Shuffle<T>(IList<T> list) { }
            public IRngService GetStream(RngStream stream, int? salt = null) => this;
        }

        /// <summary>
        /// Records the <b>kind and order</b> of every draw. The oracle's draws are sequential on one
        /// global stream, so an emit that is right but takes its draws in the wrong order still
        /// desynchronises everything after it — which no assertion on the emitted ids can see.
        /// </summary>
        private sealed class RecordingRng : IRngService
        {
            private readonly IRngService _inner = new PcgRngService(90210UL);
            public readonly List<string> Calls = new List<string>();

            public float NextFloat() { Calls.Add("NextFloat"); return _inner.NextFloat(); }
            public int NextInt(int maxExclusive) { Calls.Add("NextInt"); return _inner.NextInt(maxExclusive); }
            public int Range(int minInclusive, int maxExclusive) { Calls.Add("Range"); return _inner.Range(minInclusive, maxExclusive); }
            public float Range(float min, float max) { Calls.Add("RangeF"); return _inner.Range(min, max); }
            public uint NextUInt() => _inner.NextUInt();
            public bool Chance(float probability) => _inner.Chance(probability);
            public void Shuffle<T>(IList<T> list) => _inner.Shuffle(list);
            public IRngService GetStream(RngStream stream, int? salt = null) => _inner.GetStream(stream, salt);
        }

        // ── The gate ─────────────────────────────────────────────────────────────────────────

        [Test]
        public void ABloodlessTarget_EmitsNothing_AndThatIsDistinguishableFromABrokenHelper()
        {
            Assert.IsFalse(Plan(Hit(blood: BloodType.None)), "blood == 0 is the authored 'does not bleed'");
            Assert.IsEmpty(_emits);

            // Positive control: the same hit on a red target DOES spray. Without this the assertion
            // above would pass just as well if Plan had been deleted.
            Assert.IsTrue(Plan(Hit(blood: BloodType.Red)));
            Assert.IsNotEmpty(_emits);
        }

        [Test]
        public void ANonBleedingDamageType_EmitsNothing()
        {
            // AS3's list is D_BUL, D_BLADE, D_PHIS, D_BLEED, D_FANG and nothing else. These are the
            // ones a player would most plausibly expect to spatter — and the oracle does not.
            DamageType[] silent =
            {
                DamageType.Fire, DamageType.Explosive, DamageType.Laser, DamageType.Plasma,
                DamageType.Acid, DamageType.Poison, DamageType.Necrotic, DamageType.Venom,
                DamageType.Cryo, DamageType.EMP, DamageType.Balefire, DamageType.Pink,
                DamageType.Psionic, DamageType.Astral, DamageType.Internal,
            };

            foreach (DamageType t in silent)
            {
                Assert.IsFalse(Plan(Hit(type: t)), $"{t} must not draw blood");
                Assert.IsEmpty(_emits, $"{t} emitted something");
            }

            // Positive control on the same helper.
            Assert.IsTrue(Plan(Hit(type: DamageType.PhysicalMelee)));
        }

        [Test]
        public void TheFiveBleedingTypes_AllSpray()
        {
            DamageType[] bleeding =
            {
                DamageType.PhysicalBullet, DamageType.Blade, DamageType.PhysicalMelee,
                DamageType.Bleed, DamageType.Fang,
            };

            foreach (DamageType t in bleeding)
            {
                Assert.IsTrue(Plan(Hit(type: t)), $"{t} is one of the oracle's five");
                Assert.AreEqual(BloodSprayRules.RedSprayId, _emits[0].Id);
            }
        }

        [Test]
        public void ZeroDamage_EmitsNothing()
        {
            // AS3's `if(param1 > 0)` at :3668 wraps the whole block: a hit fully eaten by armour draws
            // no blood at all.
            Assert.IsFalse(Plan(Hit(damage: 0f)));
            Assert.IsEmpty(_emits);

            Assert.IsTrue(Plan(Hit(damage: 1f)), "positive control: any positive damage does spray");
        }

        [Test]
        public void AnUnknownBloodValue_EmitsNothingRatherThanThrowing()
        {
            // AS3 leaves `bloodEmit` null for a value outside 1..3 and dereferences it on the next
            // line — a crash. The port emits nothing instead, which is a deliberate divergence.
            Assert.IsFalse(Plan(Hit(blood: (BloodType)4)));
            Assert.IsEmpty(_emits);

            Assert.IsTrue(Plan(Hit(blood: BloodType.Pink)), "positive control");
        }

        // ── The spray ────────────────────────────────────────────────────────────────────────

        [Test]
        public void TheSprayIdFollowsTheBloodColour()
        {
            Assert.IsTrue(Plan(Hit(blood: BloodType.Red)));
            Assert.AreEqual("blood", _emits[0].Id);

            Assert.IsTrue(Plan(Hit(blood: BloodType.Green)));
            Assert.AreEqual("gblood", _emits[0].Id);

            Assert.IsTrue(Plan(Hit(blood: BloodType.Pink)));
            Assert.AreEqual("pblood", _emits[0].Id);
        }

        [Test]
        public void ABulletThrowsTheSprayFromItsOwnContactPoint()
        {
            // AS3 `this.bloodEmit.cast(loc, param3.X, param3.Y, ...)` — the round's contact point, not
            // the target's centre. Returned as a delta from the unit's own position.
            Assert.IsTrue(Plan(Hit(hasBullet: true, impactX: 250f, impactY: 130f,
                                   unitX: 100f, unitY: 100f)));
            Assert.AreEqual(150f, _emits[0].OffsetX, 1e-4f);
            Assert.AreEqual(30f, _emits[0].OffsetY, 1e-4f);
        }

        [Test]
        public void AStandingHitDropsTheSprayFromMidHeight()
        {
            // AS3 `cast(loc, X, Y - scY / 2, {"kol": ...})` — the target's own mid-height. AS3 Y runs
            // down, so `- scY/2` is upward and the offset is negative.
            Assert.IsTrue(Plan(Hit(hasBullet: false, scY: 80f)));
            Assert.AreEqual(0f, _emits[0].OffsetX, 1e-4f);
            Assert.AreEqual(-40f, _emits[0].OffsetY, 1e-4f);
        }

        [Test]
        public void TheStandingCount_IsDamageOverThree()
        {
            // AS3 `{"kol": Math.floor(param1 / 3)}`.
            Assert.IsTrue(Plan(Hit(hasBullet: false, damage: 30f)));
            Assert.AreEqual(10, _emits[0].Spec.Kol);

            Assert.IsTrue(Plan(Hit(hasBullet: false, damage: 31f)));
            Assert.AreEqual(10, _emits[0].Spec.Kol, "floor, not round");
        }

        [Test]
        public void TheStandingCount_CanBeZero_WhichTheCastTreatsAsAbsent()
        {
            // A 2-damage hit asks for floor(2/3) == 0 particles. That is not "emit nothing": AS3 tests
            // `if(param4.kol)` (Emitter.as:172), so 0 is falsy and the count falls back to 1. The port
            // reproduces this by passing the raw floor through — ParticleRules.Cast turns 0 into 1.
            Assert.IsTrue(Plan(Hit(hasBullet: false, damage: 2f)));
            Assert.AreEqual(0, _emits[0].Spec.Kol);
        }

        [Test]
        public void TheBulletCount_AddsAFifthOfTheDamagePlusARandomSpread()
        {
            // AS3 `Math.floor(Math.random() * 5 + param1 / 5)`. At damage 100 the damage term is 20,
            // so the count lands in [20, 24] and the random spread is visible at both ends.
            Assert.IsTrue(Plan(Hit(damage: 100f), new FixedRng(0f)));
            Assert.AreEqual(20, _emits[0].Spec.Kol, "the low end is the damage term alone");

            Assert.IsTrue(Plan(Hit(damage: 100f), new FixedRng(0.9999f)));
            Assert.AreEqual(24, _emits[0].Spec.Kol, "the high end adds the spread's ceiling");
        }

        [Test]
        public void TheBulletThrow_IsFiveTimesTheRoundsUnitDirection()
        {
            // AS3 `{"dx": param3.dx / param3.vel * 5, "dy": param3.dy / param3.vel * 5}`.
            Assert.IsTrue(Plan(Hit(dirX: 0.6f, dirY: -0.8f)));
            Assert.AreEqual(3f, _emits[0].Spec.DX, 1e-4f);
            Assert.AreEqual(-4f, _emits[0].Spec.DY, 1e-4f);
        }

        [Test]
        public void AStandingHit_CarriesNoVelocityOverride()
        {
            // The no-bullet branch passes only `kol` — no dx/dy — so the emitter's own burst is used.
            Assert.IsTrue(Plan(Hit(hasBullet: false, dirX: 0.6f, dirY: -0.8f)));
            Assert.AreEqual(0f, _emits[0].Spec.DX);
            Assert.AreEqual(0f, _emits[0].Spec.DY);
        }

        // ── The gib ──────────────────────────────────────────────────────────────────────────

        [Test]
        public void TheGib_IsThrownSidewaysInTheDirectionTheRoundTravelled_AndMirroredWithIt()
        {
            // AS3 `_loc13_ = -1` when the round's dx is negative, then
            // `X + 80 * _loc13_ + (rand - 0.5) * scX * 0.5` with `{"mirr": (_loc13_ < 0 ? 1 : 0)}`.
            // Draw order on the bullet path is kol, then the gib's probability roll, then the variant,
            // the lateral jitter and the drop. The variant comes from Range, which takes no value off
            // the queue, so the third queued float is the lateral one — 0.5 centres the jitter on zero.
            Assert.IsTrue(Plan(Hit(dirX: -1f, damage: 500f), new FixedRng(0f, 0f, 0.5f, 0f)));
            Assert.IsTrue(Gibbed());
            Assert.AreEqual(-80f, Last.OffsetX, 1e-4f, "thrown left, with a zero-centred jitter");
            Assert.IsTrue(Last.Spec.Mirr, "and drawn mirrored, because it was thrown left");

            Assert.IsTrue(Plan(Hit(dirX: 1f, damage: 500f), new FixedRng(0f, 0f, 0.5f, 0f)));
            Assert.AreEqual(80f, Last.OffsetX, 1e-4f, "the same hit travelling right throws right");
            Assert.IsFalse(Last.Spec.Mirr);
        }

        [Test]
        public void WithNoRound_TheGibDirectionIsACoinFlip()
        {
            // AS3 `if(param3 == null && Math.random() < 0.5) _loc13_ = -1`. On the standing path the
            // flip is the second draw, after the probability roll and before the variant.
            Assert.IsTrue(Plan(Hit(hasBullet: false, damage: 500f), new FixedRng(0f, 0.25f, 0.5f, 0f)));
            Assert.IsTrue(Gibbed());
            Assert.AreEqual(-80f, Last.OffsetX, 1e-4f, "0.25 < 0.5, so the coin came up left");

            Assert.IsTrue(Plan(Hit(hasBullet: false, damage: 500f), new FixedRng(0f, 0.75f, 0.5f, 0f)));
            Assert.AreEqual(80f, Last.OffsetX, 1e-4f, "0.75 >= 0.5, so it went right");
        }

        [Test]
        public void TheGib_SitsFortyPixelsAboveTheTarget()
        {
            // AS3 `Y - Math.random() * scY * 0.5 - 40`. With a zero drop draw that is exactly -40 in a
            // space whose Y runs down, i.e. 40 px above the target.
            Assert.IsTrue(Plan(Hit(hasBullet: false, damage: 500f, scY: 80f), new FixedRng(0f, 0f, 0.5f, 0f)));
            Assert.IsTrue(Gibbed());
            Assert.AreEqual(BloodSprayRules.ExplosionOffsetY, Last.OffsetY, 1e-4f);

            // And the drop term is live, not dead: a 1.0 draw drops it by the full scY * 0.5.
            Assert.IsTrue(Plan(Hit(hasBullet: false, damage: 500f, scY: 80f), new FixedRng(0f, 0f, 0.5f, 0.9999f)));
            Assert.AreEqual(-40f - 40f, Last.OffsetY, 1e-2f);
        }

        [Test]
        public void AGibId_IsOneOfThreeVariants()
        {
            var rng = new PcgRngService(4242UL);
            bool sawFirst = false, sawLast = false;

            for (int i = 0; i < 300; i++)
            {
                // A huge damage figure makes the gib certain, so every iteration reaches the variant.
                Assert.IsTrue(Plan(Hit(hasBullet: false, damage: 1_000_000f), rng));
                Assert.IsTrue(Gibbed());
                if (Last.Id == "bloodexpl1") sawFirst = true;
                if (Last.Id == "bloodexpl3") sawLast = true;
            }

            Assert.IsTrue(sawFirst, "bloodexpl1 is reachable");
            Assert.IsTrue(sawLast, "bloodexpl3 is reachable — otherwise this is one variant wearing a range's clothes");
        }

        [Test]
        public void ABlade_SquaresTheGibDraw_SoBladesGibMoreReadilyThanBullets()
        {
            // AS3 `if(param2 == D_BLADE) _loc12_ *= _loc12_` (:3878-3881). The draw is on the RIGHT of
            // `param1 / 1000 > _loc12_`, so squaring it (0.5 -> 0.25) makes the gib MORE likely.
            const float draw = 0.5f;

            // A 500-damage blade: 0.5 > 0.25, so it gibs.
            Assert.IsTrue(Plan(Hit(hasBullet: false, type: DamageType.Blade, damage: 500f),
                               new FixedRng(draw)));
            Assert.IsTrue(Gibbed());

            // The same 500 damage and the same draw as a non-blade: 0.5 > 0.5 is false, so it does not.
            Assert.IsTrue(Plan(Hit(hasBullet: false, type: DamageType.PhysicalMelee, damage: 500f),
                               new FixedRng(draw)));
            Assert.IsFalse(Gibbed(), "the squaring is what made the difference, not the damage");
        }

        [Test]
        public void ACrit_CutsTheGibDrawToThirtyPercent_SoCritsGibMoreReadily()
        {
            // AS3 `if(_loc5_ > 0) _loc12_ *= 0.3` (:3882-3885). Same direction as the blade squaring.
            const float draw = 0.5f;

            Assert.IsTrue(Plan(Hit(hasBullet: false, damage: 500f, crit: true), new FixedRng(draw)));
            Assert.IsTrue(Gibbed(), "0.5 * 0.3 = 0.15, and 0.5 > 0.15");

            Assert.IsTrue(Plan(Hit(hasBullet: false, damage: 500f, crit: false), new FixedRng(draw)));
            Assert.IsFalse(Gibbed(), "without the crit the same draw does not clear the bar");
        }

        [Test]
        public void TheGibOdds_ScaleWithTheDamageDealt()
        {
            // AS3 `param1 / 1000 > _loc12_`. The threshold is damage/1000, so a 1000-damage hit gibs on
            // any draw below 1 — i.e. always — and a 1-damage hit essentially never.
            Assert.IsTrue(Plan(Hit(hasBullet: false, damage: 1000f), new FixedRng(0.9999f)));
            Assert.IsTrue(Gibbed(), "damage/1000 == 1 clears every draw");

            Assert.IsTrue(Plan(Hit(hasBullet: false, damage: 1f), new FixedRng(0.5f)));
            Assert.IsFalse(Gibbed(), "damage/1000 == 0.001 does not clear 0.5");

            Assert.IsTrue(Plan(Hit(hasBullet: false, damage: 1f), new FixedRng(0.0001f)));
            Assert.IsTrue(Gibbed(), "but it does clear 0.0001 — the term is live, not rounded away");
        }

        [Test]
        public void BleedDamage_SpraysButNeverGibs()
        {
            // AS3 `param2 != D_BLEED` (:3875): a wounded target does not gib once per bleed tick.
            Assert.IsTrue(Plan(Hit(hasBullet: false, type: DamageType.Bleed, damage: 500f),
                               new FixedRng(0f)));
            Assert.IsFalse(Gibbed(), "no gib from a bleed tick");
            Assert.AreEqual(BloodSprayRules.RedSprayId, _emits[0].Id, "but it still sprays");

            // Positive control: the identical hit as a melee blow DOES gib.
            Assert.IsTrue(Plan(Hit(hasBullet: false, type: DamageType.PhysicalMelee, damage: 500f),
                               new FixedRng(0f)));
            Assert.IsTrue(Gibbed());
        }

        [Test]
        public void GreenAndPinkBlood_SprayButNeverGib()
        {
            // AS3 `this.blood == 1` (:3875) — the gib is red-blooded only.
            Assert.IsTrue(Plan(Hit(blood: BloodType.Green, hasBullet: false, damage: 500f), new FixedRng(0f)));
            Assert.IsFalse(Gibbed());

            Assert.IsTrue(Plan(Hit(blood: BloodType.Pink, hasBullet: false, damage: 500f), new FixedRng(0f)));
            Assert.IsFalse(Gibbed());

            Assert.IsTrue(Plan(Hit(blood: BloodType.Red, hasBullet: false, damage: 500f), new FixedRng(0f)));
            Assert.IsTrue(Gibbed(), "positive control on red");
        }

        [Test]
        public void ALightTarget_BelowTheMassFloor_NeverGibs()
        {
            // AS3 `massa > 0.2` (:3875). Note the port's Mass is already the divided-by-50 figure, so
            // this floor is compared against a number "around 1" for a normal creature.
            Assert.IsTrue(Plan(Hit(hasBullet: false, damage: 500f, mass: 0.2f), new FixedRng(0f)));
            Assert.IsFalse(Gibbed(), "exactly at the floor is not above it");

            Assert.IsTrue(Plan(Hit(hasBullet: false, damage: 500f, mass: 0.21f), new FixedRng(0f)));
            Assert.IsTrue(Gibbed(), "just above it gibs");
        }

        // ── Draw order ───────────────────────────────────────────────────────────────────────

        [Test]
        public void TheSprayPaths_TakeExactlyTheDrawsTheOracleTakes()
        {
            // A green target cannot gib, so these two calls isolate the spray's own draws. This is the
            // assertion that a reordered or over-eager draw cannot survive: the oracle's bullet branch
            // takes ONE draw (the count's jitter) and the standing branch takes NONE.
            var bulletRng = new RecordingRng();
            Assert.IsTrue(Plan(Hit(blood: BloodType.Green, hasBullet: true), bulletRng));
            CollectionAssert.AreEqual(new[] { "NextFloat" }, bulletRng.Calls);

            var standingRng = new RecordingRng();
            Assert.IsTrue(Plan(Hit(blood: BloodType.Green, hasBullet: false), standingRng));
            CollectionAssert.IsEmpty(standingRng.Calls);
        }

        [Test]
        public void TheGibDrawOrder_IsTheOracles()
        {
            // AS3 takes, in this order: the probability roll, then (with no round) the direction coin
            // flip, then the variant, the lateral jitter and the drop. Reordering any pair still emits
            // a plausible-looking gib, but hands every later draw in the tick a different number.
            var standingRng = new RecordingRng();
            Assert.IsTrue(Plan(Hit(hasBullet: false, damage: 1_000_000f), standingRng));
            Assert.IsTrue(Gibbed());
            CollectionAssert.AreEqual(
                new[] { "NextFloat", "NextFloat", "Range", "NextFloat", "NextFloat" },
                standingRng.Calls,
                "roll, coin flip, variant, lateral, drop");

            // With a round there is no coin flip, and the count's jitter comes first.
            var bulletRng = new RecordingRng();
            Assert.IsTrue(Plan(Hit(hasBullet: true, damage: 1_000_000f), bulletRng));
            Assert.IsTrue(Gibbed());
            CollectionAssert.AreEqual(
                new[] { "NextFloat", "NextFloat", "Range", "NextFloat", "NextFloat" },
                bulletRng.Calls,
                "count jitter, roll, variant, lateral, drop");
        }

        [Test]
        public void WithNoRng_TheRuleStillEmitsAndDoesNotThrow()
        {
            // An offline host injects no RNG. Every draw site must degrade to the expression's minimum
            // rather than dereferencing null.
            Assert.IsTrue(Plan(Hit(damage: 1000f), rng: null));
            Assert.AreEqual(2, _emits.Count, "the spray and the gib");
            Assert.AreEqual(BloodSprayRules.ExplosionIdPrefix + 1, Last.Id, "the first variant, the oracle's minimum");
            // The default direction is +X and the coin flip needs an RNG, so the throw goes right and
            // the jitter sits on its zero point.
            Assert.AreEqual(BloodSprayRules.ExplosionThrowOffsetX, Last.OffsetX, 1e-4f);
            Assert.AreEqual(BloodSprayRules.ExplosionOffsetY, Last.OffsetY, 1e-4f);
        }
    }
}
