using System;

namespace PFE.Systems.Weapons
{
    /// <summary>
    /// Which of AS3's two blast shapes a pulse runs — <c>Bullet.explRun()</c>'s multiplexer
    /// (<c>weapon/Bullet.as:711-717</c>).
    /// </summary>
    /// <remarks>
    /// <para><b>The port treats both shapes as the same reported AoE hit, and that is a deliberate
    /// simplification, not an oversight.</b> AS3's <c>explBlast()</c> (<c>:796-838</c>) spawns one
    /// child <c>Bullet</c> per unit — aimed at it, with knockback — while <c>explGas()</c>
    /// (<c>:747-794</c>) damages directly with no knockback. The port has no child bullets on the
    /// explosion path, and its <c>PendingDamage.Explosion</c> already carries
    /// <c>ReachedDamageWithoutUdarBullet: true</c>, which <c>DamageSystem.ApplyKnockback</c> returns
    /// on — so a blast applies <b>no</b> knockback today either way. The two shapes therefore differ
    /// only in the one refinement noted on <see cref="ExplosionShape.Gas"/>.</para>
    /// </remarks>
    [Flags]
    public enum ExplosionShape
    {
        /// <summary>This pulse deals no damage — <c>explTip</c> is neither 1, 2 nor 3.</summary>
        None = 0,

        /// <summary>AS3 <c>explBlast()</c> — radial child bullets with knockback.</summary>
        Blast = 1,

        /// <summary>
        /// AS3 <c>explGas()</c> — direct damage, no knockback.
        ///
        /// <para><b>One refinement is not modelled.</b> A gas pulse that belongs to an
        /// <c>explTip == 3</c> round skips any target that is <i>not</i> resting on the ground
        /// (<c>:758</c>, <c>if(!(this.explTip == 3 &amp;&amp; !_loc1_.stay))</c>) — a gas cloud catches
        /// things on the floor and misses things in the air. The port has no per-unit "resting on the
        /// floor" flag to test, so every gas pulse damages everything in radius. Only two weapons are
        /// affected (<c>acidgr</c>, <c>zombiacid</c>) and only against airborne targets.</para>
        /// </summary>
        Gas = 2,
    }

    /// <summary>
    /// The port of AS3's <b>multi-pulse explosion</b> — the countdown that makes <c>fgren</c>,
    /// <c>molotov</c>, <c>gasgr</c> and <c>acidgr</c> a damage <i>area</i> rather than a single burst.
    ///
    /// <para><b>The oracle.</b> <c>Bullet.explosion()</c> (<c>weapon/Bullet.as:665-691</c>) runs one
    /// pulse immediately and then, when <c>explKol</c> is above zero, sets
    /// <c>expl_t = (explKol - 1) * explPeriod</c>. <c>run()</c> (<c>:240-250</c>) then counts
    /// <c>expl_t</c> down once per frame and calls <c>explRun()</c> again every time it passes
    /// <c>expl_t % explPeriod == 1</c>. So the blast fires <b>exactly <c>explKol</c> times</b> — pulse 0
    /// at tick 0 and the rest at ticks 9, 19, 29 … (the <c>-1</c> is the decrement-then-test order; see
    /// <see cref="OffsetTicks"/>) — and the object stays alive for the whole train, because
    /// <c>PhisBullet.run</c> only decrements <c>liv</c> while <c>expl_t == 0</c> (<c>:108-115</c>).</para>
    ///
    /// <para><b>Unity-free on purpose.</b> The schedule is pure integer arithmetic and the shape
    /// dispatch is a lookup, so the whole thing is pinned by fixtures on a plain host — which is the
    /// only way to cover it, since its callers are <c>MonoBehaviour</c>s whose ticks cannot run
    /// offline.</para>
    ///
    /// <para><b>Why the port needed this at all.</b> <c>explTip</c> and <c>explKol</c> were never
    /// imported, so every explosion in the port was a single pulse of the default shape. The three
    /// weapons the owner reported (<c>fgren</c>, <c>molotov</c>, <c>gasgr</c>) are precisely the ones
    /// whose <c>explkol</c> is 10 or 12 — they lost 90% of their damage and their whole area
    /// behaviour. The other five with a train are <c>acidgr</c>, <c>zombivenom</c>, <c>zombiacid</c>,
    /// <c>zombipink</c> and <c>robogas</c>.</para>
    /// </summary>
    public static class ExplosionPulseRules
    {
        /// <summary>
        /// AS3 <c>Bullet.explPeriod</c> (<c>weapon/Bullet.as:121</c>) — frames between pulses.
        /// <b>A class constant, not data:</b> no <c>&lt;char&gt;</c> attribute carries it.
        /// </summary>
        public const int PeriodTicks = 10;

        /// <summary>
        /// How many pulses a blast with this <c>explKol</c> fires — AS3's <c>explKol &lt;= 0</c> branch
        /// runs <c>explRun()</c> once and never sets a countdown, so 0 and 1 are both "one pulse".
        /// </summary>
        public static int PulseCount(int explKol) => explKol < 2 ? 1 : explKol;

        /// <summary>
        /// Ticks from detonation to pulse <paramref name="pulseIndex"/>.
        ///
        /// <para><b>The <c>- 1</c> is the oracle, not a fudge.</b> AS3 <i>decrements first and tests
        /// after</i>: <c>run()</c> does <c>if (expl_t &gt; 0) --expl_t;</c> and only then
        /// <c>if (expl_t &gt; 0 &amp;&amp; expl_t % explPeriod == 1)</c> (<c>Bullet.as:240-250</c>).
        /// Starting from <c>expl_t = (explKol-1)*10</c>, the value hits <c>≡ 1 (mod 10)</c> on frames
        /// 9, 19, 29 … rather than 10, 20, 30 — so the second pulse lands at tick 9 and every later one
        /// a frame before its round number. The train is therefore 89 ticks long for
        /// <c>explKol='10'</c>, not 90. See <see cref="PulseDueAt"/> for the derivation and the
        /// project's "AS3 counts down, then fires" rule for the general shape of this trap.</para>
        /// </summary>
        public static int OffsetTicks(int pulseIndex)
            => pulseIndex <= 0 ? 0 : pulseIndex * PeriodTicks - 1;

        /// <summary>Ticks from detonation to the <b>last</b> pulse — 0 for a single-pulse blast.</summary>
        public static int LastPulseTick(int explKol) => OffsetTicks(PulseCount(explKol) - 1);

        /// <summary>
        /// Ticks from detonation to the frame the blast object is removed — AS3's <c>liv</c> reaching 0
        /// once <c>expl_t</c> has run out (<c>PhisBullet.as:108-115</c>). One tick after the last pulse,
        /// which is why the caller must run the final pulse <i>before</i> it releases the object.
        /// </summary>
        public static int TotalTicks(int explKol) => (PulseCount(explKol) - 1) * PeriodTicks;

        /// <summary>
        /// Whether this blast has a sustained phase at all. False means "one pulse, then release" —
        /// the path every explosion in the port took before this existed, and the correct one for 36 of
        /// the 44 explosive weapons.
        /// </summary>
        public static bool HasSustainedPhase(int explKol) => PulseCount(explKol) > 1;

        /// <summary>
        /// The pulse index due at <paramref name="ticksSinceDetonation"/>, or <c>-1</c> when none is.
        ///
        /// <para><b>Derived, not counted.</b> Pulse 0 is at tick 0; every later pulse is at a tick
        /// <c>≡ 9 (mod 10)</c>, which is the closed form of the oracle's countdown-then-test loop
        /// (see <see cref="OffsetTicks"/>). Deriving the index from the elapsed tick rather than
        /// decrementing a counter of our own keeps the callers free of the off-by-one that shape
        /// invites, and <c>ExplosionPulseRulesTests</c> pins it by replaying the oracle's loop
        /// directly.</para>
        /// </summary>
        public static int PulseDueAt(int explKol, int ticksSinceDetonation)
        {
            if (ticksSinceDetonation < 0) return -1;
            if (ticksSinceDetonation == 0) return 0;

            // `ticks ≡ 9 (mod 10)` ⇔ `ticks + 1 ≡ 0 (mod 10)`, without a negative-modulo concern.
            if ((ticksSinceDetonation + 1) % PeriodTicks != 0) return -1;

            int index = (ticksSinceDetonation + 1) / PeriodTicks;
            return index < PulseCount(explKol) ? index : -1;
        }

        /// <summary>
        /// Whether the train is over at <paramref name="ticksSinceDetonation"/> and the object may be
        /// released — the frame after the last pulse. See <see cref="TotalTicks"/>.
        /// </summary>
        public static bool IsFinished(int explKol, int ticksSinceDetonation)
            => ticksSinceDetonation >= TotalTicks(explKol);

        /// <summary>Whether this is the first pulse — AS3's <c>expl_t == 0</c>, before the countdown is set.</summary>
        public static bool IsInitialPulse(int pulseIndex) => pulseIndex <= 0;

        /// <summary>
        /// The shapes this pulse runs — AS3 <c>explRun()</c>'s two independent tests
        /// (<c>weapon/Bullet.as:711-717</c>):
        /// <code>
        /// if(explTip == 1 || explTip == 3 &amp;&amp; expl_t == 0) explBlast();
        /// if(explTip == 2 || explTip == 3 &amp;&amp; expl_t > 0) explGas();
        /// </code>
        /// <para><b>Note they are two <c>if</c>s, not an if/else</b> — so an <c>explTip</c> that somehow
        /// satisfied both would run both. No shipped value can, but the flags are returned rather than
        /// an exclusive choice so the oracle's shape is preserved.</para>
        /// <para><b>Pulse 0 is the <c>expl_t == 0</c> case.</b> AS3 calls <c>explRun()</c> <i>before</i>
        /// it assigns the countdown, so the first pulse always sees <c>expl_t == 0</c> — which is why
        /// <c>explTip == 3</c> means "one blast, then a gas cloud" rather than the reverse.</para>
        /// </summary>
        public static ExplosionShape ShapeFor(int explTip, int pulseIndex)
        {
            bool initial = IsInitialPulse(pulseIndex);

            ExplosionShape shape = ExplosionShape.None;
            if (explTip == 1 || (explTip == 3 && initial)) shape |= ExplosionShape.Blast;
            if (explTip == 2 || (explTip == 3 && !initial)) shape |= ExplosionShape.Gas;
            return shape;
        }

        /// <summary>Whether this pulse deals any damage at all.</summary>
        public static bool DamagesAt(int explTip, int pulseIndex)
            => ShapeFor(explTip, pulseIndex) != ExplosionShape.None;
    }
}
