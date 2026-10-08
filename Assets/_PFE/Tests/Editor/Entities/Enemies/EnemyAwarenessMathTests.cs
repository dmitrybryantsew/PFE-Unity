using NUnit.Framework;
using PFE.Entities.Enemies;

namespace PFE.Tests.Editor.Entities.Enemies
{
    /// <summary>
    /// Pins <see cref="EnemyAwarenessMath"/> — AS3 <c>aiSpok</c>, the budget that decides how long a
    /// unit keeps hunting after it stops seeing its target (<c>UnitZombie.as:621-676</c>).
    ///
    /// <para><b>Why this fixture exists.</b> The port shipped the two <i>speeds</i> of the oracle's
    /// awareness ladder — <c>_alertSpeed = runSpeed * 0.6</c> and <c>_chaseSpeed = runSpeed</c>, which
    /// are <c>UnitZombie.as:711-718</c>'s <c>aiState == 2</c> and <c>aiState == 3</c> — and none of the
    /// ladder that chooses between them. <c>TickCombatChase</c> exited on <c>TargetUnit == null</c>,
    /// which <c>EnemySensors.Evaluate</c> sets on the first tick the target is not visible, so a player
    /// who stepped out of line of sight was forgotten <b>on that tick</b>: the unit stopped running,
    /// stopped searching beyond a single state timer, and had to be re-acquired by sight or sound. That
    /// is the report — "it loses me too quickly … only if I go back into its LOS or make a sound".</para>
    ///
    /// <para><b>What is pinned here is the DURATION, because that is the property the player felt.</b>
    /// The wiring — that <c>Evaluate</c> arms the budget and <c>TickCombatChase</c> reads it instead of
    /// the dead 90-tick test — is a control-flow fact about two MonoBehaviour methods and cannot be
    /// asserted from a pure fixture; <c>EnemyAwarenessLintTests</c> guards it over the source text.</para>
    /// </summary>
    [TestFixture]
    public sealed class EnemyAwarenessMathTests
    {
        /// <summary><c>Unit.as:362</c> — <c>internal var maxSpok:int = 30;</c></summary>
        [Test]
        public void MaxSpok_IsTheOraclesThirty()
        {
            Assert.That(EnemyAwarenessMath.MaxSpok, Is.EqualTo(30),
                "Unit.as:362 declares `maxSpok:int = 30`. Every other constant here is derived from it, " +
                "so a wrong base moves the whole ladder silently.");
        }

        /// <summary>
        /// <c>aiSpok = maxSpok + 10</c> — <c>UnitZombie.as:657</c> (on a sighting), <c>:453</c>
        /// (<c>vykop</c>) and <c>:344</c> (<c>alarma</c>). Scaled by the oracle's once-per-ten-ticks
        /// decrement.
        /// </summary>
        [Test]
        public void FullAwareness_IsMaxSpokPlusTen_ConvertedToTicks()
        {
            Assert.That(EnemyAwarenessMath.FullAwarenessTicks, Is.EqualTo(400),
                "40 oracle decrements x 10 ticks each. UnitZombie.as:657 `aiSpok = maxSpok + 10`.");

            Assert.That(EnemyAwarenessMath.FullAwarenessTicks,
                Is.EqualTo((EnemyAwarenessMath.MaxSpok + 10) * EnemyAwarenessMath.AiCheckIntervalTicks),
                "Derived, not typed: if maxSpok or the check interval moves, this must move with them.");
        }

        /// <summary><c>if(aiSpok &gt;= maxSpok) { … aiState = 3; }</c> — <c>UnitZombie.as:633-640</c>.</summary>
        [Test]
        public void ChaseThreshold_IsMaxSpok_ConvertedToTicks()
        {
            Assert.That(EnemyAwarenessMath.ChaseThresholdTicks, Is.EqualTo(300),
                "30 oracle decrements x 10 ticks each — UnitZombie.as:633.");
        }

        /// <summary>
        /// <c>aiSpok = maxSpok - 1</c> — <c>UnitZombie.as:661</c>, the sound-only commit. It is
        /// deliberately below the chase threshold, which is the oracle's way of saying <b>hearing is a
        /// weaker commitment than seeing</b>.
        /// </summary>
        [Test]
        public void SoundOnlyAwareness_IsMaxSpokMinusOne_AndIsBelowTheChaseThreshold()
        {
            Assert.That(EnemyAwarenessMath.SoundOnlyAwarenessTicks, Is.EqualTo(290),
                "29 oracle decrements x 10 ticks each — UnitZombie.as:661.");

            Assert.That(EnemyAwarenessMath.SoundOnlyAwarenessTicks,
                Is.LessThan(EnemyAwarenessMath.ChaseThresholdTicks),
                "A sound-only commit must NOT reach the chase: 29 < 30 in the oracle is the whole point " +
                "of that value, and it is why a noise sends a unit to search rather than to run you down.");
        }

        /// <summary>
        /// The ladder's three bands, asserted at both sides of both boundaries so an off-by-one in either
        /// predicate is caught. A single mid-band sample would pass for `&gt;=` written as `&gt;`.
        /// </summary>
        [Test]
        public void TheLadder_BandsAtBothSidesOfBothBoundaries()
        {
            // Chasing at and above the threshold.
            Assert.That(EnemyAwarenessMath.IsChasing(EnemyAwarenessMath.ChaseThresholdTicks), Is.True,
                "`>= maxSpok` is inclusive — UnitZombie.as:633.");
            Assert.That(EnemyAwarenessMath.IsChasing(EnemyAwarenessMath.FullAwarenessTicks), Is.True,
                "A freshly-armed unit is chasing.");

            // Aware but not chasing, just below the threshold.
            Assert.That(EnemyAwarenessMath.IsChasing(EnemyAwarenessMath.ChaseThresholdTicks - 1), Is.False,
                "One tick below the threshold the unit is searching, not running.");
            Assert.That(EnemyAwarenessMath.IsAware(EnemyAwarenessMath.ChaseThresholdTicks - 1), Is.True,
                "…and it is still aware — that is the whole middle band.");

            // Calm exactly at zero, aware at one.
            Assert.That(EnemyAwarenessMath.IsAware(1), Is.True, "`aiSpok > 0` is inclusive — :629.");
            Assert.That(EnemyAwarenessMath.IsAware(0), Is.False, "`aiSpok == 0` returns to patrol — :623.");
            Assert.That(EnemyAwarenessMath.IsChasing(0), Is.False, "Calm is not chasing.");
        }

        /// <summary>
        /// The exhaustive version of the test above: sweep the whole band and assert the two predicates
        /// are exactly the oracle's comparisons. A boundary sample can be satisfied by a predicate that is
        /// wrong everywhere else.
        /// </summary>
        [Test]
        public void TheLadder_IsExactlyTheOraclesTwoComparisons_AcrossTheWholeSweep()
        {
            for (int ticks = -5; ticks <= EnemyAwarenessMath.FullAwarenessTicks + 5; ticks++)
            {
                Assert.That(EnemyAwarenessMath.IsAware(ticks), Is.EqualTo(ticks > 0),
                    $"IsAware({ticks}) must be `aiSpok > 0`.");
                Assert.That(EnemyAwarenessMath.IsChasing(ticks),
                    Is.EqualTo(ticks >= EnemyAwarenessMath.ChaseThresholdTicks),
                    $"IsChasing({ticks}) must be `aiSpok >= maxSpok`.");

                // Chasing is a strictly narrower band than aware, so it can never disagree by claiming
                // awareness the unit does not have.
                if (EnemyAwarenessMath.IsChasing(ticks))
                {
                    Assert.That(EnemyAwarenessMath.IsAware(ticks), Is.True,
                        "Chasing implies aware; a chasing-but-calm unit would be a contradiction.");
                }
            }
        }

        /// <summary>
        /// The player-visible number, stated as an assertion rather than left in a comment: how long a
        /// unit keeps hunting after it loses sight at full awareness.
        ///
        /// <para><b>Read the bands in the direction the counter actually drains.</b> Awareness starts at
        /// <see cref="EnemyAwarenessMath.FullAwarenessTicks"/> and falls, so the unit is <i>chasing</i>
        /// only for the first <c>400 - 300 = 100</c> ticks (~3.3 s) and then <i>searches</i> for the
        /// remaining <c>300</c> (~10 s). Writing it the other way round — "10 s of chase, 3 s of search"
        /// — is the easy inversion, which is why the two are asserted separately rather than as one
        /// total.</para>
        ///
        /// <para><b>The negative control is the old behaviour.</b> The port dropped out of the chase on
        /// the first obscured tick, so the quantity that matters is the ratio: ~400 ticks against 1.</para>
        /// </summary>
        [Test]
        public void LosingSightAtFullAwareness_BuysThreeSecondsOfChaseThenTenOfSearch()
        {
            float chaseSeconds = EnemyAwarenessMath.Seconds(
                EnemyAwarenessMath.FullAwarenessTicks - EnemyAwarenessMath.ChaseThresholdTicks);
            float searchSeconds = EnemyAwarenessMath.Seconds(EnemyAwarenessMath.ChaseThresholdTicks);
            float totalSeconds = EnemyAwarenessMath.Seconds(EnemyAwarenessMath.FullAwarenessTicks);

            Assert.That(chaseSeconds, Is.EqualTo(10f / 3f).Within(0.01f),
                "400 - 300 = 100 ticks at 30 Hz: the full-speed pursuit after the target leaves sight.");

            Assert.That(searchSeconds, Is.EqualTo(10f).Within(0.01f),
                "The 300 ticks below the threshold are spent searching at alert speed (runSpeed * 0.6).");

            Assert.That(totalSeconds, Is.EqualTo(40f / 3f).Within(0.01f),
                "The full budget is ~13.3 s — UnitZombie.as:657's 40 decrements at one per 10 ticks.");

            Assert.That(chaseSeconds, Is.LessThan(searchSeconds),
                "The chase band is the SHORT one: the counter starts at 400 and the threshold is 300, so " +
                "the unit spends most of its memory searching rather than running. Inverting these two " +
                "is the obvious mistake, and it would make the unit relentless.");
        }

        /// <summary>
        /// The arithmetic that makes the tick conversion honest: the oracle decrements once per ten ticks
        /// (<c>aiTCh % 10 == 1</c>, <c>UnitZombie.as:650</c>), and the port drains its counter every tick.
        /// If someone ever changes one without the other, every duration above moves 10x.
        /// </summary>
        [Test]
        public void TheCheckInterval_IsTheOraclesTen()
        {
            Assert.That(EnemyAwarenessMath.AiCheckIntervalTicks, Is.EqualTo(10),
                "UnitZombie.as:650 `aiTCh % 10 == 1` gates the `aiSpok` decrement. This is the factor " +
                "between the oracle's counter and the port's per-tick one; changing it rescales the ladder.");

            // `maxSpok + 10` minus `maxSpok` is the oracle's 10 decrements of headroom above the
            // threshold — the same 10 written literally at UnitZombie.as:657.
            Assert.That(
                EnemyAwarenessMath.FullAwarenessTicks - EnemyAwarenessMath.ChaseThresholdTicks,
                Is.EqualTo(10 * EnemyAwarenessMath.AiCheckIntervalTicks),
                "The headroom is exactly the oracle's `+ 10`, scaled by the check interval. This is what " +
                "makes `FullAwarenessTicks` and `ChaseThresholdTicks` impossible to move independently.");
        }
    }
}
