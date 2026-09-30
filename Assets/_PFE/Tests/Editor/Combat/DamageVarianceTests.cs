using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using PFE.Core;
using PFE.Core.Rng;
using PFE.Systems.Combat;

namespace PFE.Tests.Editor.Combat
{
    /// <summary>
    /// Pins <see cref="DamageVariance"/> — the port of <c>Unit.as:4085</c>,
    /// <c>param1.damage * (Math.random() * 0.6 + 0.7)</c>.
    ///
    /// <para>The end-to-end behaviour (where the spread sits relative to armour, and that a blast does
    /// not get one) is pinned in <c>DamageSystemTests</c>. What is pinned here is the shape of the
    /// function itself: the literals, the range, and — the one that is easy to get wrong and impossible
    /// to see — that the draw happens even when the result is thrown away.</para>
    /// </summary>
    [TestFixture]
    public class DamageVarianceTests
    {
        // ── Helpers ──────────────────────────────────────────────────────────

        /// <summary>
        /// A scripted stream that counts each accessor separately. The separate counters are the point:
        /// <see cref="DamageVariance"/> must draw with <c>NextFloat</c> (a uniform <c>[0, 1)</c>), and
        /// an implementation that reached for <c>Chance</c>, <c>Range</c> or <c>NextInt</c> instead would
        /// still compile and still look random — so the accessor itself is asserted.
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
        public void Roll_IsTheOraclesFormula_AtBothEndsAndTheMiddle()
        {
            // `random() * 0.6 + 0.7`, checked at three points rather than one, because a plausible
            // mis-transcription — `0.7 + random() * 0.3`, say, or `1 + random() * 0.6 - 0.3` — agrees at
            // the middle and differs at the ends.
            Assert.AreEqual(0.7f, DamageVariance.Roll(new CountingRng(0f)),   1e-6f, "floor");
            Assert.AreEqual(1.0f, DamageVariance.Roll(new CountingRng(0.5f)), 1e-6f, "midpoint");
            Assert.AreEqual(1.3f, DamageVariance.Roll(new CountingRng(1f)),   1e-6f, "ceiling");
        }

        [Test]
        public void TheLiteralsAreTheOracles_NotRounded()
        {
            // The two constants are load-bearing and a "tidy up" to 0.8 / 0.4 or 0.75 / 0.5 would change
            // every damage number in the game while still looking like a reasonable spread.
            Assert.AreEqual(0.7f, DamageVariance.MinMultiplier, 1e-6f, "AS3's literal 0.7");
            Assert.AreEqual(0.6f, DamageVariance.Spread,        1e-6f, "AS3's literal 0.6");
            Assert.AreEqual(1f,   DamageVariance.NoVariance,    1e-6f);
        }

        [Test]
        public void Roll_StaysInsideTheRange_OverManyDraws()
        {
            // A real stream, so this is a property of the implementation rather than of a script. The
            // bounds are half-open at the top — `NextFloat()` is `[0, 1)` — which is also what AS3's
            // `Math.random()` gives, so 1.3 itself is unreachable and the assertion is `< 1.3`.
            var rng = new PcgRngService(0x5EEDUL);

            float min = float.MaxValue;
            float max = float.MinValue;

            for (int i = 0; i < 20000; i++)
            {
                float value = DamageVariance.Roll(rng);
                min = Mathf.Min(min, value);
                max = Mathf.Max(max, value);
            }

            Assert.GreaterOrEqual(min, DamageVariance.MinMultiplier, "never below the floor");
            Assert.Less(max, DamageVariance.MinMultiplier + DamageVariance.Spread, "never at or above 1.3");

            // The range must actually be *used*, not merely respected: a function that always returned
            // 1.0 would satisfy both bounds above and still be wrong.
            Assert.Less(min, 0.72f, "the floor is reached, not just approached");
            Assert.Greater(max, 1.28f, "the ceiling is reached, not just approached");
        }

        [Test]
        public void Roll_UsesNextFloat_NotAnotherAccessor()
        {
            // See CountingRng. `Chance`, `Range` and `NextInt` are all plausible ways to write a spread
            // and all wrong: Chance is boolean, Range(float,float) is a different contract, and NextInt
            // quantises. The accessor is part of the port.
            var rng = new CountingRng(0.5f);

            DamageVariance.Roll(rng);

            Assert.AreEqual(1, rng.NextFloatCalls, "one uniform draw");
            Assert.AreEqual(0, rng.OtherCalls, "and no other accessor is touched");
        }

        // ── The debug override ───────────────────────────────────────────────

        [Test]
        public void Deterministic_ReturnsExactlyOne_NotTheDrawnValue()
        {
            var rng = new CountingRng(1f);   // would be 1.3 if the draw were reported

            Assert.AreEqual(1f, DamageVariance.Roll(rng, deterministic: true), 1e-6f);
        }

        [Test]
        public void Deterministic_StillConsumesTheDraw()
        {
            // THE guard for this file. AS3's `World.w.testDam` overwrites the result on the line AFTER
            // the roll (`Unit.as:4085-4089`), so the random draw happens either way. The combat stream is
            // shared with the armour-reliability and crit rolls, so an implementation that skipped the
            // draw under the toggle would shift every later roll in the same tick — a replication bug
            // dressed up as a debug convenience, and invisible without this assertion.
            var rng = new CountingRng(0.5f);

            DamageVariance.Roll(rng, deterministic: true);

            Assert.AreEqual(1, rng.NextFloatCalls,
                "the toggle changes the reported number, never the stream position");
        }

        [Test]
        public void Deterministic_AndNot_ConsumeTheSameNumberOfDraws()
        {
            // The same property stated as an equality, so it cannot be satisfied by "always draws twice"
            // or any other fixed count that happens to be non-zero.
            var live  = new CountingRng(0.5f);
            var fixedRng = new CountingRng(0.5f);

            DamageVariance.Roll(live, deterministic: false);
            DamageVariance.Roll(fixedRng, deterministic: true);

            Assert.AreEqual(live.NextFloatCalls, fixedRng.NextFloatCalls);
        }

        [Test]
        public void Apply_MultipliesTheDamageByTheRoll()
        {
            Assert.AreEqual(7f,   DamageVariance.Apply(10f, new CountingRng(0f)),   1e-5f, "10 * 0.7");
            Assert.AreEqual(10f,  DamageVariance.Apply(10f, new CountingRng(0.5f)), 1e-5f, "10 * 1.0");
            Assert.AreEqual(13f,  DamageVariance.Apply(10f, new CountingRng(1f)),   1e-5f, "10 * 1.3");
        }

        [Test]
        public void Apply_UnderTheToggle_LeavesTheDamageExactlyAsListed()
        {
            Assert.AreEqual(10f, DamageVariance.Apply(10f, new CountingRng(1f), deterministic: true), 1e-6f);
        }

        // ── The shipped default ──────────────────────────────────────────────

        [Test]
        public void ProductionDefault_LeavesTheSpreadOn()
        {
            // The one test that reaches outside this class, and it earns its place: `DamageSystemTests`
            // builds its settings with the spread disabled so the formula tests keep exact numbers, which
            // means nothing in that file would notice if the *asset* shipped with the spread off. This is
            // that guard. AS3's `World.w.testDam` defaults to false (`World.as:202`), i.e. the spread is
            // the normal case.
            var settings = ScriptableObject.CreateInstance<PfeDebugSettings>();

            Assert.IsFalse(settings.TestDamage,
                "PfeDebugSettings.testDamage must default to false — the spread is AS3's normal behaviour, " +
                "and TestDamage is the debug override that discards it");
        }

        [Test]
        public void TestDamageIsReadFromTheSerializedField_NotASeparateOne()
        {
            // Guards the wiring between the property and the [SerializeField] field, so a rename of
            // either half fails here rather than silently leaving the toggle inert.
            var settings = ScriptableObject.CreateInstance<PfeDebugSettings>();

            FieldInfo field = typeof(PfeDebugSettings).GetField(
                "testDamage", BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.That(field, Is.Not.Null, "the serialized field was renamed; update this guard");

            field.SetValue(settings, true);
            Assert.IsTrue(settings.TestDamage, "the property must project the serialized field");
        }
    }
}
