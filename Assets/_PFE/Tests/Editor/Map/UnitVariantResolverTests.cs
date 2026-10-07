using System.Collections.Generic;
using NUnit.Framework;
using PFE.Core.Rng;
using PFE.Systems.Map;

namespace PFE.Tests.Editor.Map
{
    /// <summary>
    /// Pins <see cref="UnitVariantResolver"/> — the port of <c>Location.randomCid()</c>
    /// (<c>Location.as:1728-1958</c>) plus the id composition each unit subclass does with its result
    /// (<c>UnitZombie.as:104</c> <c>id = "zombie" + this.tr</c>).
    ///
    /// <para><b>Why this needed pinning.</b> The port passed the room's authored id straight to the
    /// <c>UnitDefinition</c> lookup, so a room placing <c>zombie</c> spawned
    /// <c>Resources/Units/zombie.asset</c> — the content-free family template (one sprite, no
    /// <c>&lt;vis&gt;</c> art, no <c>&lt;comb hp&gt;</c>, no <c>&lt;move speed&gt;</c>). Every visible
    /// symptom of "the zombie is broken" descends from that one missing step.</para>
    ///
    /// <para><b>Half of these tests are about draw <i>counts</i>, not values.</b> The spawn stream is
    /// shared with the rest of room generation, so a branch that skips a draw the oracle takes
    /// desynchronises every later placement in the room — a bug whose only symptom is "the layout is
    /// different", with nothing to grep for. The counting RNG below is what makes that assertable.</para>
    /// </summary>
    [TestFixture]
    public class UnitVariantResolverTests
    {
        // ── Fixture ──────────────────────────────────────────────────────────

        /// <summary>
        /// A scripted <see cref="IRngService"/> that records which draws were taken and through which
        /// overload. An unconsumed script falls back to the range's <c>min</c>, which keeps a test that
        /// only cares about the draw count from having to script a value.
        /// </summary>
        private sealed class ScriptedRng : IRngService
        {
            private readonly Queue<int> _ints = new Queue<int>();
            private readonly Queue<float> _floats = new Queue<float>();

            public ScriptedRng(int[] ints = null, float[] floats = null)
            {
                if (ints != null) { foreach (int v in ints) _ints.Enqueue(v); }
                if (floats != null) { foreach (float v in floats) _floats.Enqueue(v); }
            }

            /// <summary>Draws taken through the integer <c>Range</c> — every <c>Math.floor(random*n+b)</c>.</summary>
            public int IntDraws { get; private set; }

            /// <summary>Draws taken through <see cref="NextFloat"/> — every probabilistic test.</summary>
            public int FloatDraws { get; private set; }

            public int TotalDraws => IntDraws + FloatDraws;

            /// <summary>The last integer range asked for, so a test can assert the <i>bounds</i> too.</summary>
            public int LastMinInclusive { get; private set; } = int.MinValue;

            public int LastMaxExclusive { get; private set; } = int.MinValue;

            public int Range(int minInclusive, int maxExclusive)
            {
                IntDraws++;
                LastMinInclusive = minInclusive;
                LastMaxExclusive = maxExclusive;
                return _ints.Count > 0 ? _ints.Dequeue() : minInclusive;
            }

            public float NextFloat()
            {
                FloatDraws++;
                return _floats.Count > 0 ? _floats.Dequeue() : 0f;
            }

            public IRngService GetStream(RngStream stream, int? salt = null) => this;

            public uint NextUInt() => 0u;

            public int NextInt(int maxExclusive) => Range(0, maxExclusive);

            public float Range(float min, float max) => min;

            public bool Chance(float probability) => NextFloat() < probability;

            public void Shuffle<T>(IList<T> list) { }
        }

        // ── The pinned bug ───────────────────────────────────────────────────

        /// <summary>
        /// The camp room places <c>zombie</c> at difficulty 0, and the oracle's ladder ends in a bare
        /// <c>_loc2_ = 0</c> for <c>locDifLevel &lt; 2</c> — no draw at all. So the only correct answer
        /// is <c>zombie0</c>, deterministically, whatever the seed.
        /// </summary>
        [Test]
        public void Zombie_AtDifficultyZero_ResolvesToVariantZero_WithoutDrawing()
        {
            var rng = new ScriptedRng();

            string cid = UnitVariantResolver.RandomCid("zombie", 0f, rng);
            Assert.AreEqual("0", cid, "Location.as:1866-1869 — the `else` arm is a bare 0.");
            Assert.AreEqual(0, rng.TotalDraws,
                "That arm takes no draw, and the spawn stream is shared — a draw here shifts every " +
                "later placement in the room.");

            string resolved = UnitVariantResolver.ResolveSpawnId("zombie", 0f, rng);
            Assert.AreEqual("zombie0", resolved,
                "UnitZombie.as:104 — `id = \"zombie\" + this.tr`, so cid \"0\" is zombie0.");
            Assert.AreNotEqual("zombie", resolved,
                "The family id is the content-free template (1 sprite, no art, no stats) — returning it " +
                "is the bug this class exists to fix.");
        }

        /// <summary>
        /// A difficulty above the ladder's first step must reach a <i>different</i> arm, or the ladder is
        /// decorative. <c>d &gt;= 2</c> is <c>Math.floor(random * 4)</c>, i.e. <c>Range(0, 4)</c>.
        /// </summary>
        [Test]
        public void Zombie_DifficultyLadder_UsesTheOraclesBoundsAtEachStep()
        {
            // d >= 2 -> Math.floor(random * 4)
            var atTwo = new ScriptedRng();
            UnitVariantResolver.RandomCid("zombie", 2f, atTwo);
            Assert.AreEqual(0, atTwo.LastMinInclusive);
            Assert.AreEqual(4, atTwo.LastMaxExclusive, "Location.as:1860 — `random() * 4`.");
            Assert.AreEqual(1, atTwo.IntDraws);

            // d >= 5 -> Math.floor(random * 5)
            var atFive = new ScriptedRng();
            UnitVariantResolver.RandomCid("zombie", 5f, atFive);
            Assert.AreEqual(5, atFive.LastMaxExclusive, "Location.as:1856 — `random() * 5`.");

            // d >= 8 with a biome -> Math.floor(random * 7). NoBiome must NOT take this arm.
            var atEightWithBiome = new ScriptedRng();
            UnitVariantResolver.RandomCid(
                "zombie", 8f, atEightWithBiome, new UnitVariantContext(biome: 3));
            Assert.AreEqual(7, atEightWithBiome.LastMaxExclusive,
                "Location.as:1850-1853 — the `biome >= 1 && locDifLevel >= 8` arm.");

            var atEightNoBiome = new ScriptedRng();
            UnitVariantResolver.RandomCid("zombie", 8f, atEightNoBiome, UnitVariantResolver.NoContext);
            Assert.AreEqual(5, atEightNoBiome.LastMaxExclusive,
                "With no biome the oracle falls through to the `locDifLevel >= 5` arm, not the 8 one. " +
                "NoBiome is negative so that `biome >= 1` is false — this is the control for it.");
        }

        /// <summary>
        /// Biome 5 is the one arm with a <b>second</b> draw inside it, taken before the tier roll. Getting
        /// the order wrong swaps a 10 % chance of variant 9 with a 10 % chance of shifting every later
        /// placement.
        /// </summary>
        [Test]
        public void Zombie_Biome5_TakesTheTierRollAndTheTenthChanceInTheOraclesOrder()
        {
            // First float 0.05 (< 0.1) -> variant 9, and the tier roll is NOT taken.
            var jackpot = new ScriptedRng(floats: new[] { 0.05f });
            Assert.AreEqual("9", UnitVariantResolver.RandomCid(
                "zombie", 20f, jackpot, new UnitVariantContext(biome: 5)));
            Assert.AreEqual(1, jackpot.FloatDraws);
            Assert.AreEqual(0, jackpot.IntDraws,
                "Location.as:1844-1848 — the 0.1 test is the `if`; the tier roll is the `else`.");

            // First float 0.5 (>= 0.1) -> falls to the tier roll, Math.floor(random * 4 + 5).
            var normal = new ScriptedRng(ints: new[] { 7 }, floats: new[] { 0.5f });
            Assert.AreEqual("7", UnitVariantResolver.RandomCid(
                "zombie", 20f, normal, new UnitVariantContext(biome: 5)));
            Assert.AreEqual(1, normal.FloatDraws);
            Assert.AreEqual(1, normal.IntDraws);
            Assert.AreEqual(5, normal.LastMinInclusive, "Location.as:1852 — `random() * 4 + 5`.");
            Assert.AreEqual(9, normal.LastMaxExclusive);
        }

        // ── The composition half ─────────────────────────────────────────────

        /// <summary>
        /// <c>UnitZombie.as:83-101</c> reads the placement node's <c>tr</c> before the cid, so an authored
        /// <c>tr</c> must win over the roll — and must not consume the roll's draws.
        /// </summary>
        [Test]
        public void PlacementTr_OverridesTheRolledCid_AndSkipsTheRoll()
        {
            var rng = new ScriptedRng();

            string resolved = UnitVariantResolver.ResolveSpawnId(
                "zombie", 0f, rng, UnitVariantResolver.NoContext, placementTr: "3");

            Assert.AreEqual("zombie3", resolved);
            Assert.AreEqual(0, rng.TotalDraws, "An authored tr means randomCid is never called.");
        }

        /// <summary>
        /// <c>UnitRoller.as:24-45</c> appends the number only when <c>tr &gt;= 2</c>, which is why the
        /// asset directory holds <c>roller</c> and <c>roller2</c> but no <c>roller1</c>. Composing
        /// <c>"roller" + 1</c> would produce an id that resolves to nothing.
        /// </summary>
        [Test]
        public void Roller_TrOne_YieldsTheBareFamilyId_NotRoller1()
        {
            Assert.AreEqual("roller", UnitVariantResolver.ResolveSpawnId(
                "roller", 0f, new ScriptedRng(), UnitVariantResolver.NoContext));
            Assert.AreEqual("roller2", UnitVariantResolver.ResolveSpawnId(
                "roller", 0f, new ScriptedRng(), UnitVariantResolver.NoContext, placementTr: "2"));
        }

        /// <summary>
        /// A family with no composition rule passes through untouched — correct for the subclasses that
        /// ignore the cid outright (<c>UnitTrain.as:13</c> <c>id = "training"</c>), and the documented
        /// boundary of what has been read against the oracle.
        /// </summary>
        [Test]
        public void FamilyWithoutACompositionRule_PassesThroughUnchanged()
        {
            var rng = new ScriptedRng();
            Assert.AreEqual("training", UnitVariantResolver.ResolveSpawnId("training", 0f, rng));
            Assert.AreEqual(0, rng.TotalDraws,
                "randomCid's `default:` returns null; it must not be reached by a non-family id, and " +
                "the pass-through must not draw either.");
        }

        /// <summary>
        /// <c>scorp</c> is the one family whose <c>randomCid</c> arm returns a <b>whole id</b>
        /// (<c>"scorp" + n</c>) rather than a bare number, so it deliberately has no composition rule.
        /// </summary>
        [Test]
        public void Scorp_ReturnsAWholeIdFromRandomCid()
        {
            var rng = new ScriptedRng(ints: new[] { 2 });
            Assert.AreEqual("scorp2", UnitVariantResolver.RandomCid("scorp", 5f, rng));
            Assert.AreEqual(1, rng.IntDraws);
        }

        // ── The default-struct trap ──────────────────────────────────────────

        /// <summary>
        /// <b>A negative control for a real hazard.</b> <c>default(UnitVariantContext)</c> zeroes
        /// <c>LandY</c>, and <c>LandY == 0</c> is a genuine land index — so a defaulted context makes
        /// <c>ranger</c> take the <c>landY == 0</c> arm and return <c>"1"</c> without drawing, where the
        /// oracle would roll. <see cref="UnitVariantResolver.NoContext"/> is the value to pass instead.
        ///
        /// <para>This test exists so that "simplify <c>NoContext</c> back to <c>default</c>" fails
        /// loudly rather than silently turning every ranger in the game into variant 1.</para>
        /// </summary>
        [Test]
        public void DefaultContext_IsNotNoContext_BecauseLandYZeroIsARealLand()
        {
            Assert.AreEqual(-1, UnitVariantResolver.NoContext.LandY);

            var rolled = new ScriptedRng();
            Assert.AreEqual("1", UnitVariantResolver.RandomCid(
                "ranger", 0f, rolled, UnitVariantResolver.NoContext));
            Assert.AreEqual(1, rolled.IntDraws,
                "With no context the oracle rolls `random() * 2 + 1`.");
            Assert.AreEqual(1, rolled.LastMinInclusive);
            Assert.AreEqual(3, rolled.LastMaxExclusive);

            var defaulted = new ScriptedRng();
            Assert.AreEqual("1", UnitVariantResolver.RandomCid("ranger", 0f, defaulted, default));
            Assert.AreEqual(0, defaulted.TotalDraws,
                "The trap: a defaulted struct has LandY == 0, so the `landY == 0` arm is taken and the " +
                "roll is skipped. Use UnitVariantResolver.NoContext.");
        }
    }
}
