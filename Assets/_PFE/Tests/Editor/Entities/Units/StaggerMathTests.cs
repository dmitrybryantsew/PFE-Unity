using NUnit.Framework;
using PFE.Entities.Units;
using UnityEngine;

namespace PFE.Tests.Editor.Entities.Units
{
    /// <summary>
    /// Pins <see cref="StaggerMath"/> — AS3 <c>Unit.shok</c>, the stagger timer.
    ///
    /// <para><b>Why this fixture exists at all.</b> <c>shok</c> is the field behind three separate
    /// symptoms that all read as unrelated tuning problems: "zombies hit for half forever" (a gate that
    /// never reaches zero), "every touch staggers" (a floor of 4 instead of 5), and "the stagger is a
    /// tick off at some damage values" (the rounding rule). None of them throws, and none is visible
    /// from a single play-through. The arithmetic lived inside two <c>MonoBehaviour</c> brains, where it
    /// could not be asserted offline; this fixture is the reason it no longer does.</para>
    ///
    /// <para><b>It also records a correction.</b> The port previously set this timer to a 3..7 range
    /// and labelled it "AS3 reaction pause" at a movement gate. The oracle's <c>shok</c> is not a
    /// movement gate and is not 3..7 — see <see cref="MaxTicks_IsThirty"/> and
    /// <see cref="AlarmTicks_SpansFiveToNineteen"/>. The movement pause is a stand-in for a
    /// <i>different</i> field (<c>aiSpok</c>) and is documented as such at its call site.</para>
    /// </summary>
    [TestFixture]
    public sealed class StaggerMathTests
    {
        // ── MaxTicks ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>Unit.maxShok:int = 30</c> (<c>fe/unit/Unit.as:388</c>).
        /// </summary>
        [Test]
        public void MaxTicks_IsThirty()
        {
            Assert.That(StaggerMath.MaxTicks, Is.EqualTo(30),
                "Unit.as:388 — `public var maxShok:int = 30;`.");

            Assert.That(StaggerMath.MaxTicks, Is.Not.EqualTo(20),
                "20 is Unit.as:394's neujazMax (ContactInvulnerabilityMath.DefaultMaxTicks). " +
                "Collapsing the two would make a stagger expire when a contact window does.");

            Assert.That(StaggerMath.MaxTicks, Is.Not.EqualTo(15),
                "15 is UnitZombie.as:559's levitation value — a value the field takes, not its ceiling.");

            Assert.That(StaggerMath.MaxTicks, Is.Not.EqualTo(45),
                "45 is UnitRaider.as:637, which assigns PAST this ceiling on purpose. MaxTicks is a " +
                "clamp on FromDamage, not an invariant of the field.");
        }

        // ── AlarmTicks ───────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>Math.floor(Math.random() * 15 + 5)</c> — the alarm assignment, <b>5..19</b> inclusive
        /// (<c>UnitZombie.as:346</c>, <c>UnitRaider.as:493</c>).
        /// </summary>
        /// <remarks>
        /// This is the number the port had wrong (3..7). The upper end matters more than it looks:
        /// 19 ticks is 0.63 s of half-damage at 30 Hz, where 7 is 0.23 s.
        /// </remarks>
        [Test]
        public void AlarmTicks_SpansFiveToNineteen()
        {
            Assert.That(StaggerMath.AlarmTicks(0), Is.EqualTo(5),
                "Roll 0 — the floor of `Math.floor(random()*15 + 5)`.");

            Assert.That(StaggerMath.AlarmTicks(StaggerMath.AlarmRollRange - 1), Is.EqualTo(19),
                "Roll 14 — the ceiling. 15 is NOT reachable: Math.random() is [0,1), so " +
                "Math.floor(...*15) is 0..14.");

            int min = int.MaxValue;
            int max = int.MinValue;
            for (int roll = 0; roll < StaggerMath.AlarmRollRange; roll++)
            {
                int ticks = StaggerMath.AlarmTicks(roll);
                if (ticks < min) min = ticks;
                if (ticks > max) max = ticks;
            }

            Assert.That(min, Is.EqualTo(5), "The lowest reachable alarm stagger.");
            Assert.That(max, Is.EqualTo(19), "The highest reachable alarm stagger.");

            Assert.That(StaggerMath.AlarmTicks(StaggerMath.AlarmRollRange - 1),
                Is.LessThan(StaggerMath.MaxTicks),
                "The whole alarm range sits BELOW MaxTicks. That is what makes the alarm's plain " +
                "assignment able to SHORTEN a damage stagger — see " +
                "AlarmAssignment_CanShortenADamageStagger.");
        }

        /// <summary>
        /// A roll outside <c>[0, 15)</c> is clamped rather than trusted. Callers are supposed to pass
        /// <c>Random.Range(0, AlarmRollRange)</c>; the guard exists because a <b>float</b>-range draw is
        /// the natural mistake and Unity's float overload is inclusive of its upper bound.
        /// </summary>
        [Test]
        public void AlarmTicks_OutOfRangeRoll_IsClamped()
        {
            Assert.That(StaggerMath.AlarmTicks(StaggerMath.AlarmRollRange), Is.EqualTo(19),
                "A draw of 15 must not produce 20. `Random.Range(0f, 15f)` CAN return 15, which is " +
                "exactly the mistake this clamp catches.");

            Assert.That(StaggerMath.AlarmTicks(-1), Is.EqualTo(5),
                "A negative draw clamps to the floor rather than producing 4.");
        }

        // ── FromDamage ───────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>if(_loc11_ &lt; 5) { _loc11_ = 0; }</c> (<c>Unit.as:3705</c>) — a hit below the floor
        /// grants <b>nothing</b>, not 5. The floor is on the result, not on the formula.
        /// </summary>
        /// <remarks>
        /// The two cases bracket the boundary instead of landing on it. The exact half — <c>raw = 4.5
        /// → 5</c> — is reachable (<c>r = 18.75 %</c>, exactly representable) but the product
        /// <c>0.2f * 120f * r</c> is not bit-exact, so an assertion sitting on the half would be
        /// testing float luck. 18 % must give 0 and 19 % must give 5; together they pin the threshold
        /// inside a 1 % band.
        /// </remarks>
        [Test]
        public void FromDamage_BelowTheFloor_GrantsNothing_AtOrAboveItIsGranted()
        {
            Assert.That(StaggerMath.FromDamage(18f, 100f, roll01: 0f), Is.EqualTo(0),
                "r = 18 %, low roll: raw = 4.32 -> rounds to 4 -> under the floor -> 0. It must NOT be " +
                "clamped up to 5; a swarm of small taps would then keep a unit permanently reeling.");

            Assert.That(StaggerMath.FromDamage(19f, 100f, roll01: 0f), Is.EqualTo(5),
                "r = 19 %, low roll: raw = 4.56 -> rounds to 5 -> granted as-is. Just above the floor " +
                "is 5, not 6.");
        }

        /// <summary>
        /// The formula saturates at <see cref="StaggerMath.MaxTicks"/> — it does not grow with damage.
        /// </summary>
        [Test]
        public void FromDamage_SaturatesAtMaxTicks()
        {
            Assert.That(StaggerMath.FromDamage(100f, 100f, roll01: 1f), Is.EqualTo(StaggerMath.MaxTicks),
                "r = 100 %, high roll: raw ~ 120 -> clamped to 30.");

            Assert.That(StaggerMath.FromDamage(500f, 100f, roll01: 1f), Is.EqualTo(StaggerMath.MaxTicks),
                "An overkill hit is still 30. Without the clamp this would be 600, and a unit would " +
                "stand reeling for 20 seconds.");

            Assert.That(StaggerMath.FromDamage(1000f, 100f, roll01: 1f), Is.EqualTo(StaggerMath.MaxTicks),
                "And it stays clamped no matter how large the hit.");
        }

        /// <summary>
        /// <c>roll</c> only ever moves the result <i>up</i>: the formula is monotonic in it. A negative
        /// coefficient would invert the curve and make a "lucky" roll a weaker stagger.
        /// </summary>
        [Test]
        public void FromDamage_IsMonotonicInTheRoll()
        {
            int previous = -1;

            for (int i = 0; i <= 20; i++)
            {
                int ticks = StaggerMath.FromDamage(30f, 100f, roll01: i / 20f);

                Assert.That(ticks, Is.GreaterThanOrEqualTo(previous),
                    $"roll = {i / 20f} produced {ticks}, below the previous {previous}. The roll is a " +
                    "positive coefficient (0.8) on the raw value, so this cannot legitimately decrease.");

                previous = ticks;
            }

            Assert.That(previous, Is.GreaterThan(0), "A 30 % hit staggers at every roll.");
        }

        /// <summary>
        /// The worked example from the <see cref="StaggerMath.FromDamage"/> docs, asserted: a 14-damage
        /// hit on a 50-hp zombie (<c>r = 28 %</c>) always staggers, for 7..30 ticks depending on the
        /// roll — never zero, never a fixed number.
        /// </summary>
        [Test]
        public void FromDamage_AZombieHit_AlwaysStaggers_ForSevenToThirtyTicks()
        {
            Assert.That(StaggerMath.FromDamage(14f, 50f, roll01: 0f), Is.EqualTo(7),
                "r = 28 %, low roll: raw = 6.72 -> 7. The floor is well clear of the 5-tick minimum, " +
                "so this hit can never fail to stagger.");

            Assert.That(StaggerMath.FromDamage(14f, 50f, roll01: 1f), Is.EqualTo(StaggerMath.MaxTicks),
                "r = 28 %, high roll: raw = 33.6 -> clamped to 30.");

            for (int i = 0; i <= 20; i++)
            {
                int ticks = StaggerMath.FromDamage(14f, 50f, roll01: i / 20f);

                Assert.That(ticks, Is.InRange(7, StaggerMath.MaxTicks),
                    $"roll = {i / 20f} gave {ticks}; the documented band for this hit is 7..30.");
            }
        }

        /// <summary>
        /// <c>param4</c> (<c>Unit.as:3611</c>) suppresses the stagger entirely, alongside the armour
        /// roll. A suppressed hit is 0 at every roll.
        /// </summary>
        [Test]
        public void FromDamage_WhenSuppressed_GrantsNothing()
        {
            Assert.That(StaggerMath.FromDamage(999f, 100f, roll01: 1f, suppress: true), Is.EqualTo(0),
                "param4 = true must give 0 regardless of the roll or the damage.");

            Assert.That(StaggerMath.FromDamage(999f, 100f, roll01: 1f, suppress: false),
                Is.EqualTo(StaggerMath.MaxTicks),
                "The positive control: the same call without suppression does stagger. Without this " +
                "pair the test above would pass on a method that always returns 0.");
        }

        /// <summary>
        /// Degenerate inputs return 0 instead of dividing by zero. <c>maxhp == 0</c> is reachable — a
        /// definition authored with no health — and the unguarded formula would produce
        /// <c>Infinity</c>, which <see cref="StaggerMath.As3Round"/> then turns into an undefined
        /// <c>int</c>.
        /// </summary>
        [Test]
        public void FromDamage_DegenerateInputs_GrantNothing()
        {
            Assert.That(StaggerMath.FromDamage(50f, 0f, roll01: 1f), Is.EqualTo(0),
                "maxHealth = 0 must not divide by zero.");

            Assert.That(StaggerMath.FromDamage(0f, 100f, roll01: 1f), Is.EqualTo(0),
                "Zero damage is not a stagger.");

            Assert.That(StaggerMath.FromDamage(-10f, 100f, roll01: 1f), Is.EqualTo(0),
                "Negative damage (a heal routed through the damage path) must not stagger. AS3 returns " +
                "early for exactly this case — Unit.as:3606-3609 calls heal() and returns 0.");
        }

        // ── Raise: the two writers' asymmetry ────────────────────────────────────────────────

        /// <summary>
        /// <c>if(this.shok &lt; _loc11_) { this.shok = _loc11_; }</c> (<c>Unit.as:3709-3712</c>) — the
        /// damage path may only <b>extend</b> a stagger.
        /// </summary>
        [Test]
        public void Raise_ExtendsButNeverShortens()
        {
            Assert.That(StaggerMath.Raise(10, 20), Is.EqualTo(20), "A bigger stagger replaces a smaller.");
            Assert.That(StaggerMath.Raise(20, 10), Is.EqualTo(20),
                "A smaller stagger is ignored. If this returned 10, a light hit landing during a heavy " +
                "one's stagger would CANCEL it — the unit would shake off a big hit by being tapped.");
            Assert.That(StaggerMath.Raise(20, 20), Is.EqualTo(20), "Equal keeps the current value.");
            Assert.That(StaggerMath.Raise(0, 0), Is.EqualTo(0), "No stagger stays no stagger.");
        }

        /// <summary>
        /// <b>The asymmetry, made observable.</b> The alarm <i>assigns</i>; damage <i>raises</i>. So a
        /// 5-tick alarm landing on a 30-tick damage stagger cuts it to 5, while a 5-tick damage roll
        /// landing on a 30-tick stagger does nothing. That looks like a bug in the oracle and is the
        /// oracle's behaviour — both halves are pinned so neither can be "fixed" by accident.
        /// </summary>
        [Test]
        public void AlarmAssignment_CanShortenADamageStagger_ButTheReverseCannot()
        {
            int damageStagger = StaggerMath.MaxTicks;              // 30, from a solid hit
            int alarm = StaggerMath.AlarmTicks(0);                 // 5, the weakest alarm

            Assert.That(alarm, Is.LessThan(damageStagger),
                "The alarm floor must be below the damage ceiling, or this asymmetry is unreachable " +
                "and the distinction between the two writers is untestable.");

            // Alarm: a plain assignment, so it overwrites downwards.
            int afterAlarm = alarm;
            Assert.That(afterAlarm, Is.EqualTo(5), "The alarm overwrites a running 30-tick stagger.");

            // Damage: a raise, so the same magnitude does nothing.
            int afterDamage = StaggerMath.Raise(damageStagger, StaggerMath.FromDamage(6f, 100f, 0f));
            Assert.That(afterDamage, Is.EqualTo(damageStagger),
                "A small damage roll against a running 30-tick stagger leaves it at 30.");
        }

        // ── The two predicates ───────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>shok &lt;= 0 ? 1 : 0.5</c> (<c>UnitZombie.as:940</c>) — strict, and the boundary is the
        /// whole point.
        /// </summary>
        [Test]
        public void HalvesOutgoingDamage_AtZero_IsFalse()
        {
            Assert.That(StaggerMath.HalvesOutgoingDamage(0), Is.False,
                "Zero is the resting state — the unit hits for full damage. A gate of `>= 0` would make " +
                "every zombie permanently weak, and the symptom would read as a damage-tuning bug.");

            Assert.That(StaggerMath.HalvesOutgoingDamage(1), Is.True,
                "One tick left still halves — `> 0`, not `> 1`.");

            Assert.That(StaggerMath.HalvesOutgoingDamage(-1), Is.False,
                "A negative is not 'even more halved'.");

            Assert.That(StaggerMath.HalvesOutgoingDamage(StaggerMath.MaxTicks), Is.True,
                "A full stagger halves.");
        }

        /// <summary>
        /// The raider's <c>shok &lt;= 0</c> attack gates (<c>UnitRaider.as:1486</c>, <c>:1498</c>,
        /// <c>:1521</c>) — same predicate, harsher consequence: a staggering raider is refused
        /// outright rather than weakened.
        /// </summary>
        [Test]
        public void BlocksAttack_SharesThePredicate()
        {
            for (int ticks = -2; ticks <= StaggerMath.MaxTicks + 2; ticks++)
            {
                Assert.That(StaggerMath.BlocksAttack(ticks),
                    Is.EqualTo(StaggerMath.HalvesOutgoingDamage(ticks)),
                    $"The two predicates diverged at ticks = {ticks}. They are the same `> 0` test in " +
                    "the oracle; if they are meant to differ, that is a deliberate change and this " +
                    "test should be updated with it, not left to drift.");
            }
        }

        // ── Tick ─────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>if(this.shok &gt; 0) { --this.shok; }</c> (<c>Unit.as:3065-3068</c>) — by one, clamped at
        /// zero.
        /// </summary>
        [Test]
        public void Tick_DecrementsByOne_AndClampsAtZero()
        {
            Assert.That(StaggerMath.Tick(30), Is.EqualTo(29), "30 -> 29.");
            Assert.That(StaggerMath.Tick(1), Is.EqualTo(0),
                "1 -> 0. This transition ends the stagger, so an off-by-one here is an off-by-one in " +
                "every stagger in the game.");
            Assert.That(StaggerMath.Tick(0), Is.EqualTo(0), "0 stays 0 — the oracle's decrement is guarded.");
            Assert.That(StaggerMath.Tick(-5), Is.EqualTo(0),
                "A negative is pulled back to 0, not decremented further.");
        }

        /// <summary>
        /// <b>Exactly N ticks, counted.</b> The property a single-step assertion cannot show: an alarm
        /// stagger of 19 must weaken the unit for 19 ticks and then stop — not 18, not 20. A unit that
        /// is one tick late releasing its stagger is a unit that lands one half-damage hit too many.
        /// </summary>
        [Test]
        public void Tick_AnAlarmStagger_LastsExactlyItsTicks()
        {
            int ticks = StaggerMath.AlarmTicks(StaggerMath.AlarmRollRange - 1); // 19
            int halvedTicks = 0;

            for (int i = 0; i < 100; i++)
            {
                ticks = StaggerMath.Tick(ticks);

                if (StaggerMath.HalvesOutgoingDamage(ticks))
                {
                    halvedTicks++;
                }
                else
                {
                    break;
                }
            }

            Assert.That(halvedTicks, Is.EqualTo(18),
                "Starting from 19 and ticking first: 18..1 are halved (18 of them) and the 19th tick " +
                "reaches 0. Same convention as ContactInvulnerabilityMath: the tick that grants the " +
                "stagger does not also consume one.");

            Assert.That(ticks, Is.EqualTo(0), "And it settles at exactly 0.");
        }

        // ── As3Round: the rounding rule ──────────────────────────────────────────────────────

        /// <summary>
        /// AS3 <c>Math.round</c> rounds a half <b>up</b>. .NET's <c>Math.Round</c> — and therefore
        /// <c>Mathf.RoundToInt</c> — defaults to banker's rounding and rounds to <b>even</b>.
        /// </summary>
        /// <remarks>
        /// This is the test that justifies <see cref="StaggerMath.As3Round"/> existing instead of the
        /// one-liner everyone reaches for. The two rules agree everywhere except on exact halves, which
        /// is why the divergence is invisible in play and would never be caught by a smoke test.
        /// </remarks>
        [Test]
        public void As3Round_RoundsHalfUp_NotToEven()
        {
            Assert.That(StaggerMath.As3Round(0.5f), Is.EqualTo(1), "AS3 Math.round(0.5) == 1.");
            Assert.That(StaggerMath.As3Round(1.5f), Is.EqualTo(2), "AS3 Math.round(1.5) == 2.");
            Assert.That(StaggerMath.As3Round(2.5f), Is.EqualTo(3),
                "AS3 Math.round(2.5) == 3. This is the case that differs.");

            // The wrong answer, asserted so the trap is on the record rather than in a comment.
            Assert.That(Mathf.RoundToInt(2.5f), Is.EqualTo(2),
                "Mathf.RoundToInt(2.5) is 2 — banker's rounding. If this ever becomes 3, Unity changed " +
                "its default and As3Round's hand-rolled version is worth revisiting.");

            Assert.That(StaggerMath.As3Round(2.5f), Is.Not.EqualTo(Mathf.RoundToInt(2.5f)),
                "The whole point: the obvious implementation disagrees with the oracle on this input.");
        }

        /// <summary>
        /// Away from the halves, <see cref="StaggerMath.As3Round"/> is ordinary rounding — so it is not
        /// accidentally flooring or ceiling everything.
        /// </summary>
        [Test]
        public void As3Round_OffTheHalf_IsOrdinaryRounding()
        {
            Assert.That(StaggerMath.As3Round(0.4f), Is.EqualTo(0), "Rounds down.");
            Assert.That(StaggerMath.As3Round(0.6f), Is.EqualTo(1), "Rounds up.");
            Assert.That(StaggerMath.As3Round(4.4f), Is.EqualTo(4), "Rounds down.");
            Assert.That(StaggerMath.As3Round(4.6f), Is.EqualTo(5), "Rounds up.");
            Assert.That(StaggerMath.As3Round(0f), Is.EqualTo(0), "Zero is zero.");
            Assert.That(StaggerMath.As3Round(7f), Is.EqualTo(7), "An integer is unchanged.");
        }

        // ── The constants that are values, not bounds ────────────────────────────────────────

        /// <summary>
        /// <c>UnitZombie.as:559</c> — <c>shok = 15</c> on levitation. Pinned because it is a literal
        /// the port must reproduce and it sits between the alarm floor and the damage ceiling, so a
        /// careless "unify the constants" refactor could silently fold it into either.
        /// </summary>
        [Test]
        public void LevitationTicks_IsFifteen_AndIsNotTheCeiling()
        {
            Assert.That(StaggerMath.LevitationTicks, Is.EqualTo(15),
                "UnitZombie.as:559 — `shok = 15;`.");

            Assert.That(StaggerMath.LevitationTicks, Is.LessThan(StaggerMath.MaxTicks),
                "15 is a value the field takes, not the ceiling — MaxTicks is 30.");

            Assert.That(StaggerMath.LevitationTicks, Is.GreaterThan(StaggerMath.AlarmMinTicks),
                "And it is above the alarm floor of 5, so it is not the floor either.");
        }
    }
}
