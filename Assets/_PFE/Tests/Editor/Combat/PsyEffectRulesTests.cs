using System.Collections.Generic;
using NUnit.Framework;
using PFE.Systems.Combat;

namespace PFE.Tests.Editor.Combat
{
    /// <summary>
    /// Pins the psionic branch — AS3 <c>UnitPlayer.damage():3253-3268</c>, reached by the <c>alipsy</c>
    /// row's <c>&lt;dop effect='psy' ch='1'/&gt;</c> (<c>AllData.as:695</c>).
    ///
    /// <para><b>Why the roll is a rule and not a controller test.</b> The branch lives in
    /// <c>PlayerController</c>'s damage hook, which is a <c>MonoBehaviour</c> and cannot be constructed
    /// offline. <see cref="PsyEffectRules"/> is the arithmetic half, so the <i>outcomes</i> and — more
    /// importantly — the <i>number of RNG draws</i> can be pinned. What no offline fixture can see is
    /// whether the hook is reached by a real projectile, or whether the four stat fields move the player
    /// the way AS3 does; both are recorded as owed.</para>
    /// </summary>
    [TestFixture]
    public class PsyEffectRulesTests
    {
        /// <summary>A draw source that records how many times it was consumed.</summary>
        private sealed class Draws
        {
            private readonly Queue<float> _values;
            public int Count { get; private set; }

            public Draws(params float[] values) => _values = new Queue<float>(values);

            public float Next()
            {
                Count++;
                Assert.IsTrue(_values.Count > 0, "the code under test drew more values than the fixture supplied");
                return _values.Dequeue();
            }
        }

        // ── the three outcomes (:3255-3266) ───────────────────────────────────────────────────────

        [Test]
        public void Horror_OnAFirstDrawBelowTheThreshold()
        {
            var draws = new Draws(0f, 0f);
            Assert.AreEqual(PsyEffectRules.Horror, PsyEffectRules.RollEffect(draws.Next));
        }

        [Test]
        public void Horror_JustBelowTheThreshold()
        {
            var draws = new Draws(0.3299f, 0f);
            Assert.AreEqual(PsyEffectRules.Horror, PsyEffectRules.RollEffect(draws.Next));
        }

        [Test]
        public void Vote_WhenTheFirstFailsAndTheSecondIsBelowAHalf()
        {
            var draws = new Draws(0.33f, 0.4999f);
            Assert.AreEqual(PsyEffectRules.Vote, PsyEffectRules.RollEffect(draws.Next));
        }

        [Test]
        public void Disorient_WhenBothDrawsFail()
        {
            var draws = new Draws(0.33f, 0.5f);
            Assert.AreEqual(PsyEffectRules.Disorient, PsyEffectRules.RollEffect(draws.Next));
        }

        [Test]
        public void Disorient_OnTheHighestDraws()
        {
            var draws = new Draws(0.9999f, 0.9999f);
            Assert.AreEqual(PsyEffectRules.Disorient, PsyEffectRules.RollEffect(draws.Next));
        }

        // ── the draw COUNT is part of the behaviour ───────────────────────────────────────────────

        [Test]
        public void TheSecondDraw_IsNotTakenWhenTheFirstSucceeds()
        {
            // `if(isrnd(0.33)) addEffect("horror") else if(isrnd()) …` — the second Math.random() is
            // inside the else. Drawing it anyway would consume one extra value per horror hit and shift
            // every later roll in the tick.
            var draws = new Draws(0f, 123f);   // the second value is a tripwire: it must never be read
            PsyEffectRules.RollEffect(draws.Next);
            Assert.AreEqual(1, draws.Count);
        }

        [Test]
        public void TheSecondDraw_IsTakenExactlyOnceWhenTheFirstFails()
        {
            var draws = new Draws(0.33f, 0.5f);
            PsyEffectRules.RollEffect(draws.Next);
            Assert.AreEqual(2, draws.Count);
        }

        // ── the id that reaches the branch ───────────────────────────────────────────────────────

        [Test]
        public void IsPsyHit_MatchesOnlyThePsyId()
        {
            Assert.IsTrue(PsyEffectRules.IsPsyHit("psy"));
            Assert.IsFalse(PsyEffectRules.IsPsyHit("stun"));
            Assert.IsFalse(PsyEffectRules.IsPsyHit("cut"));
            Assert.IsFalse(PsyEffectRules.IsPsyHit(""));
            Assert.IsFalse(PsyEffectRules.IsPsyHit(null));
            Assert.IsFalse(PsyEffectRules.IsPsyHit("PSY"), "the oracle compares with ==, case-sensitively");
        }

        [Test]
        public void TheEffectIds_AreTheOnesTheImportedRowsUse()
        {
            // `AllData.as:5960` / `:5963` / `:5966`, and the same ids as `Resources/Effects/*.asset`.
            Assert.AreEqual("horror", PsyEffectRules.Horror);
            Assert.AreEqual("vote", PsyEffectRules.Vote);
            Assert.AreEqual("disorient", PsyEffectRules.Disorient);
        }

        [Test]
        public void TheThresholds_AreTheOracles()
        {
            // `isrnd(0.33)` and the parameterless `isrnd()` default of 0.5 (`Unit.as:4855`).
            Assert.AreEqual(0.33f, PsyEffectRules.HorrorChance);
            Assert.AreEqual(0.5f, PsyEffectRules.VoteChance);
        }

        [Test]
        public void RollEffect_RejectsAMissingDrawSource()
        {
            Assert.Throws<System.ArgumentNullException>(() => PsyEffectRules.RollEffect(null));
        }
    }
}
