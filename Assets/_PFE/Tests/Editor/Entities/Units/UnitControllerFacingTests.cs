using System.Collections.Generic;
using NUnit.Framework;
using PFE.Core.Rng;
using PFE.Entities.Units;

namespace PFE.Tests.Editor.Entities.Units
{
    /// <summary>
    /// Pins AS3's <c>@turn</c> → facing rule (<c>Unit.as:596-613</c>) to the oracle.
    ///
    /// <para>The rule has three cases and the third is the one that gets "simplified" away: a
    /// <b>present but non-positive</b> <c>turn</c> is not the same as an <b>absent</b> one. AS3 only
    /// tests <c>&gt; 0</c> and <c>&lt; 0</c>, so <c>turn="0"</c> falls through and leaves <c>storona</c>
    /// at its field initialiser (<c>Obj.as:24</c> <c>public var storona:int = 1;</c>) — it never reaches
    /// the coin flip, which lives in the attribute's <c>else</c> branch (<c>:609-613</c>).</para>
    ///
    /// <para>So the tests that matter here are the ones that would <i>fail</i> if the non-positive case
    /// were merged into the absent case: they are given an RNG that would flip the other way, and the
    /// result must ignore it.</para>
    /// </summary>
    [TestFixture]
    public class UnitControllerFacingTests
    {
        /// <summary>Every roll reports <paramref name="value"/>, so the coin flip is decided by the test.</summary>
        private sealed class FixedRng : IRngService
        {
            private readonly float _value;
            public FixedRng(float value) => _value = value;

            /// <summary>How many times the coin flip was actually consulted.</summary>
            public int ChanceCalls;

            public uint  NextUInt() => 0u;
            public float NextFloat() => _value;
            public int   NextInt(int maxExclusive) => 0;
            public int   Range(int minInclusive, int maxExclusive) => minInclusive;
            public float Range(float min, float max) => min;
            public bool  Chance(float probability) { ChanceCalls++; return _value < probability; }
            public void  Shuffle<T>(IList<T> list) { }
            public IRngService GetStream(RngStream stream, int? salt = null) => this;
        }

        // ── the two unambiguous cases ────────────────────────────────────────────────────────────

        [TestCase("1")]
        [TestCase("7")]
        [TestCase("+3")]
        [TestCase("1.5")]   // AS3 compares a coerced Number, so a fractional turn is still positive
        [TestCase("1e3")]
        public void ResolveFacing_PositiveTurn_FacesRight(string turn)
        {
            var rng = new FixedRng(0.99f); // would flip left if it were ever consulted

            Assert.AreEqual(1, UnitController.ResolveFacing(turn, -1, rng));
            Assert.AreEqual(0, rng.ChanceCalls, "A present turn must not consult the RNG at all.");
        }

        [TestCase("-1")]
        [TestCase("-9")]
        [TestCase("-0.5")]
        public void ResolveFacing_NegativeTurn_FacesLeft(string turn)
        {
            var rng = new FixedRng(0.01f); // would flip right if it were ever consulted

            Assert.AreEqual(-1, UnitController.ResolveFacing(turn, 1, rng));
            Assert.AreEqual(0, rng.ChanceCalls, "A present turn must not consult the RNG at all.");
        }

        // ── the absent case: the only one that rolls ─────────────────────────────────────────────

        [Test]
        public void ResolveFacing_AbsentTurn_IsTheCoinFlip()
        {
            // isrnd() is `Math.random() < 0.5` (Unit.as:4855-4858), i.e. Chance(0.5).
            Assert.AreEqual(-1, UnitController.ResolveFacing(null, 1, new FixedRng(0.99f)));
            Assert.AreEqual(1, UnitController.ResolveFacing(null, 1, new FixedRng(0.01f)));
        }

        [Test]
        public void ResolveFacing_EmptyTurn_IsAlsoAbsent()
        {
            // `param3.@turn.length()` is 0 for both a missing attribute and `turn=""`, so the
            // attribute reads the same way in AS3 for both. The port must agree.
            var rng = new FixedRng(0.99f);

            Assert.AreEqual(-1, UnitController.ResolveFacing(string.Empty, 1, rng));
        }

        [Test]
        public void ResolveFacing_AbsentTurnWithNoRng_KeepsCurrentFacingInsteadOfThrowing()
        {
            // The random-enemy path can be reached without a spawn stream in tests; a null RNG must
            // degrade to the field initialiser, not NullReferenceException.
            Assert.AreEqual(1, UnitController.ResolveFacing(null, 1, null));
        }

        // ── the case that discriminates: present-but-non-positive ────────────────────────────────

        [Test]
        public void ResolveFacing_ZeroTurn_KeepsCurrentFacing_AndDoesNotCoinFlip()
        {
            // This is the complement of ResolveFacing_AbsentTurn_IsTheCoinFlip. Without it, folding
            // the non-positive case into the absent case still passes every test above, and a
            // turn="0" unit — which AS3 pins to a fixed facing — starts facing a random direction.
            var rng = new FixedRng(0.99f); // would flip left

            Assert.AreEqual(1, UnitController.ResolveFacing("0", 1, rng));
            Assert.AreEqual(0, rng.ChanceCalls, "turn=\"0\" must never reach the coin flip.");
        }

        [Test]
        public void ResolveFacing_ZeroTurn_ReturnsTheGivenFacing_NotAHardcodedOne()
        {
            // Distinguishes "keeps currentFacing" from "returns 1". AS3 leaves `storona` at whatever
            // it already held, so a caller that supplies -1 must get -1 back.
            Assert.AreEqual(-1, UnitController.ResolveFacing("0", -1, new FixedRng(0.01f)));
        }

        [TestCase("abc")]
        [TestCase(" ")]
        public void ResolveFacing_UnparseableTurn_KeepsCurrentFacing_AndDoesNotCoinFlip(string turn)
        {
            // AS3 coerces the attribute to a Number for the comparisons; a non-numeric value makes
            // both comparisons false, which is the same fall-through as "0" — NOT the else branch.
            var rng = new FixedRng(0.99f);

            Assert.AreEqual(1, UnitController.ResolveFacing(turn, 1, rng));
            Assert.AreEqual(0, rng.ChanceCalls, $"turn=\"{turn}\" must never reach the coin flip.");
        }
    }
}
