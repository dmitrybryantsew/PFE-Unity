using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using PFE.Core.Rng;
using PFE.Systems.Combat;

namespace PFE.Tests.Editor.Combat
{
    /// <summary>
    /// Pins <see cref="KnockbackMath"/> — the port of <c>Unit.otbros()</c>, <c>unit/Unit.as:4242-4258</c>:
    ///
    /// <code>
    /// if(this.invulner) return;
    /// _loc2_ = Math.random() * 0.4 + 0.8;
    /// _loc2_ *= this.knocked / massa;
    /// if(_loc2_ > 3) _loc2_ = 3;
    /// dx += param1.knockx * param1.otbros * _loc2_;
    /// dy += param1.knocky * param1.otbros * _loc2_;
    /// </code>
    ///
    /// <para>The end-to-end wiring (that <c>DamageSystem</c> calls this at all, and where in the damage
    /// order) belongs to <c>DamageSystemTests</c>. What is pinned here is the shape of the function: the
    /// literals, the order of the multiply and the clamp, and — the two that are invisible in a passing
    /// game — that the draw happens even when it is multiplied by zero, and that a zero mass does not
    /// produce <c>Infinity</c> or <c>NaN</c>.</para>
    ///
    /// <para><b>Why the invulnerable case is absent.</b> AS3 returns <i>before</i> the draw
    /// (<c>:4245-4248</c>), so an invulnerable target consumes no roll. That gate therefore lives in the
    /// caller, and this class must not be handed an invulnerable target at all — a guard here would be
    /// asserting a contract the caller owns. <c>DamageSystemTests</c> is where it is pinned.</para>
    /// </summary>
    [TestFixture]
    public class KnockbackMathTests
    {
        // ── Helpers ──────────────────────────────────────────────────────────

        /// <summary>
        /// A scripted stream that counts each accessor separately. The separate counters are the point:
        /// the jitter must come from <c>NextFloat</c> (a uniform <c>[0, 1)</c>), and an implementation that
        /// reached for <c>Chance</c>, <c>Range</c> or <c>NextInt</c> would still compile and still look
        /// random — so the accessor itself is asserted.
        /// </summary>
        private sealed class CountingRng : IRngService
        {
            private readonly Queue<float> _values;

            public int NextFloatCalls;
            public int OtherCalls;

            public CountingRng(params float[] values) => _values = new Queue<float>(values);

            public float NextFloat()
            {
                NextFloatCalls++;
                return _values.Count > 0 ? _values.Dequeue() : 0f;
            }

            public uint NextUInt() { OtherCalls++; return 0u; }
            public int NextInt(int maxExclusive) { OtherCalls++; return 0; }
            public int Range(int minInclusive, int maxExclusive) { OtherCalls++; return minInclusive; }
            public float Range(float min, float max) { OtherCalls++; return min; }
            public bool Chance(float probability) { OtherCalls++; return false; }
            public void Shuffle<T>(IList<T> list) { }
            public IRngService GetStream(RngStream stream, int? salt = null) => this;
        }

        // ── The formula ──────────────────────────────────────────────────────

        [Test]
        public void Scale_IsTheOraclesFormula_AtBothEndsAndTheMiddle()
        {
            // `(random() * 0.4 + 0.8) * (knocked / massa)`, checked at three points rather than one,
            // because a plausible mis-transcription — `0.8 + random() * 1.2`, say, or `random() * 0.4 + 1.2`
            // — agrees at the middle and differs at the ends. knocked and massa are both 1 here, so the
            // second factor drops out and only the jitter is under test.
            Assert.AreEqual(0.8f, KnockbackMath.Scale(0f,   1f, 1f), 1e-6f, "floor");
            Assert.AreEqual(1.0f, KnockbackMath.Scale(0.5f, 1f, 1f), 1e-6f, "midpoint");
            Assert.AreEqual(1.2f, KnockbackMath.Scale(1f,   1f, 1f), 1e-6f, "ceiling");
        }

        [Test]
        public void TheLiteralsAreTheOracles_NotRounded()
        {
            // A "tidy up" of 0.8/0.4 to 0.7/0.6 (DamageVariance's pair) or 0.75/0.5 would change how far
            // every hit throws its target while still looking like a reasonable jitter.
            Assert.AreEqual(0.8f, KnockbackMath.MinScale,    1e-6f, "AS3's literal 0.8");
            Assert.AreEqual(0.4f, KnockbackMath.ScaleSpread, 1e-6f, "AS3's literal 0.4");
            Assert.AreEqual(3f,   KnockbackMath.MaxScale,    1e-6f, "AS3's literal 3");
            Assert.AreEqual(1f,   KnockbackMath.DefaultMass, 1e-6f, "Obj.as:28, `massa = 1`");
        }

        [Test]
        public void Scale_DividesByMass_AndMultipliesByKnocked()
        {
            // The target factors, with the jitter held at its midpoint (1.0) so they are readable.
            // turret1's real numbers: knocked='0.3', massafix='500' -> massa 10.
            Assert.AreEqual(0.03f, KnockbackMath.Scale(0.5f, 0.3f, 10f), 1e-6f, "0.3 / 10");
            Assert.AreEqual(0.5f,  KnockbackMath.Scale(0.5f, 1f,   2f),  1e-6f, "1 / 2");
            Assert.AreEqual(2.0f,  KnockbackMath.Scale(0.5f, 1f,   0.5f), 1e-6f, "1 / 0.5");
        }

        [Test]
        public void KnockedZero_MakesTheTargetImmovable()
        {
            // AS3's "cannot be moved" flag: `knocked = 0` is authored on fixed things — turrets, and
            // UnitBossNecr's shadow phase. It is NOT a separate boolean anywhere in the oracle; the
            // multiplication by zero is the whole mechanism.
            Assert.AreEqual(0f, KnockbackMath.Scale(0f,   0f, 1f), 1e-6f);
            Assert.AreEqual(0f, KnockbackMath.Scale(1f,   0f, 1f), 1e-6f, "even at the top of the jitter");
            Assert.AreEqual(0f, KnockbackMath.Scale(0.5f, 0f, 0.1f), 1e-6f, "and regardless of mass");
        }

        [Test]
        public void TheClamp_CapsTheProduct_NotEitherInput()
        {
            // THE ordering guard. `if(_loc2_ > 3) _loc2_ = 3;` runs AFTER the knocked/massa multiply
            // (:4251), so it caps the whole factor. Clamping the jitter instead would be a no-op — the
            // jitter is already inside [0.8, 1.2] — and clamping knocked/massa instead would let the two
            // combine past 3. This case distinguishes all three: the product is 24, both inputs are small.
            Assert.AreEqual(3f, KnockbackMath.Scale(1f, 10f, 0.5f), 1e-6f, "1.2 * 20 = 24, capped to 3");

            // And the clamp is a ceiling, not a floor: a product below 3 is untouched.
            Assert.AreEqual(2.4f, KnockbackMath.Scale(1f, 2f, 1f), 1e-6f, "1.2 * 2 = 2.4, uncapped");
        }

        [Test]
        public void TheClamp_IsReachable_FromRealAuthoredNumbers()
        {
            // The cap is not theoretical: `bel` carries knock=150, and a light unit is knocked=1.5 with a
            // small massa. Without the clamp this is a launch, not a shove. Stated with real magnitudes so
            // a future "the clamp never fires" comment is contradicted by an executed number.
            float scale = KnockbackMath.Scale(1f, 1.5f, 0.2f);   // 1.2 * 7.5 = 9
            Assert.AreEqual(3f, scale, 1e-6f, "bel (150) on a knocked=1.5, massa=0.2 target");
        }

        // ── The draw, and the stream ─────────────────────────────────────────

        [Test]
        public void Roll_UsesNextFloat_NotAnotherAccessor()
        {
            // `Chance`, `Range` and `NextInt` are all plausible ways to write a jitter and all wrong:
            // Chance is boolean, Range(float,float) is a different contract, and NextInt quantises.
            var rng = new CountingRng(0.5f);

            KnockbackMath.Roll(rng, 1f, 1f);

            Assert.AreEqual(1, rng.NextFloatCalls, "one uniform draw");
            Assert.AreEqual(0, rng.OtherCalls, "and no other accessor is touched");
        }

        [Test]
        public void Roll_ConsumesTheDraw_EvenWhenTheTargetCannotBeMoved()
        {
            // THE guard for this file, and the reason Roll exists as a separate method from Scale.
            //
            // The draw is line 1 of the formula and the multiply by `knocked` is line 2, so a target with
            // knocked=0 still consumes a roll in AS3. A caller (or an optimiser) that short-circuits on
            // "this weapon has otbros=0" or "this target has knocked=0" — both extremely tempting, and
            // both of which look like they change nothing — would hand every later hit in the same tick a
            // stream one draw out of step. That is a replication bug, not a performance win, and it is
            // invisible without this assertion.
            var immovable = new CountingRng(0.5f);

            KnockbackMath.Roll(immovable, 0f, 1f);

            Assert.AreEqual(1, immovable.NextFloatCalls,
                "a knocked=0 target still takes its roll — the multiply happens after the draw");
        }

        [Test]
        public void Roll_DrawCountDoesNotDependOnAnyInput()
        {
            // The same property stated as an equality across the whole input space, so it cannot be
            // satisfied by "always draws twice" or any other fixed count that happens to be non-zero.
            var baseline = new CountingRng(0.5f);
            KnockbackMath.Roll(baseline, 1f, 1f);

            foreach (float knocked in new[] { 0f, 0.1f, 1f, 1.5f })
            {
                foreach (float mass in new[] { 0f, 0.2f, 1f, 10f, 200f })
                {
                    var rng = new CountingRng(0.5f);
                    KnockbackMath.Roll(rng, knocked, mass);

                    Assert.AreEqual(baseline.NextFloatCalls, rng.NextFloatCalls,
                        $"knocked={knocked} mass={mass} changed the draw count");
                }
            }
        }

        [Test]
        public void Roll_StaysInsideTheRange_OverManyDraws()
        {
            // A real stream, so this is a property of the implementation rather than of a script. The
            // bounds are half-open at the top — `NextFloat()` is `[0, 1)` — which is also what AS3's
            // `Math.random()` gives, so 1.2 itself is unreachable and the assertion is `< 1.2`.
            var rng = new PcgRngService(0x5EEDUL);

            float min = float.MaxValue;
            float max = float.MinValue;

            for (int i = 0; i < 20000; i++)
            {
                float value = KnockbackMath.Roll(rng, 1f, 1f);
                min = Mathf.Min(min, value);
                max = Mathf.Max(max, value);
            }

            Assert.GreaterOrEqual(min, KnockbackMath.MinScale, "never below the floor");
            Assert.Less(max, KnockbackMath.MinScale + KnockbackMath.ScaleSpread, "never at or above 1.2");

            // The range must actually be *used*, not merely respected: a function that always returned
            // 1.0 would satisfy both bounds above and still be wrong.
            Assert.Less(min, 0.82f, "the floor is reached, not just approached");
            Assert.Greater(max, 1.18f, "the ceiling is reached, not just approached");
        }

        // ── The zero-mass guard ──────────────────────────────────────────────

        [Test]
        public void ZeroOrNegativeMass_FallsBackToTheOracleDefault()
        {
            // AS3 cannot reach a zero divisor: `massa` starts at 1 (Obj.as:28) and only ever holds
            // `@massa / 50` or `@massafix / 50`. The port stores the raw attribute, so a malformed row
            // could. What the fallback prevents is not a large number but a poisoned one: `knocked / 0`
            // is Infinity (clamped to 3, so merely wrong) or, when knocked is also 0, NaN — and a NaN
            // added to a unit's velocity is not a behaviour, it is permanent corruption of that unit's
            // movement that no later frame recovers from.
            Assert.AreEqual(1.0f, KnockbackMath.Scale(0.5f, 1f, 0f),  1e-6f, "mass 0 -> divisor 1");
            Assert.AreEqual(1.0f, KnockbackMath.Scale(0.5f, 1f, -5f), 1e-6f, "negative mass too");

            float value = KnockbackMath.Scale(0.5f, 0f, 0f);
            Assert.IsFalse(float.IsNaN(value), "0/0 must not produce NaN");
            Assert.IsFalse(float.IsInfinity(value), "and must not produce Infinity");
        }
    }
}
