using PFE.Core;

namespace PFE.Entities.Enemies
{
    /// <summary>
    /// AS3's <c>aiSpok</c> ladder — how long a unit stays committed to a hunt after it stops seeing its
    /// target (<c>UnitZombie.as:621-676</c>, <c>maxSpok</c> at <c>Unit.as:362</c>).
    ///
    /// <para><b>Why this type exists: the port had the two speeds of this ladder and none of the
    /// ladder.</b> <c>ZombieBrain</c> already sets <c>_alertSpeed = runSpeed * 0.6</c> and
    /// <c>_chaseSpeed = runSpeed</c>, which are exactly AS3's <c>aiState == 2</c> and <c>aiState == 3</c>
    /// writes (<c>UnitZombie.as:711-718</c>). What was missing is the field that decides which of the two
    /// applies — and without it the chase was abandoned on the <i>first</i> tick the target stopped being
    /// visible, so a player who stepped behind a wall (or onto a ledge above the unit) was forgotten
    /// instantly instead of being searched for.</para>
    ///
    /// <para><b>The oracle's shape.</b> <c>aiSpok</c> is a single counter armed to
    /// <c>maxSpok + 10</c> when the target is <i>seen</i> (<c>:657</c>), to <c>maxSpok - 1</c> on a
    /// sound-only commit (<c>:661</c>), and <c>maxSpok + 10</c> again by <c>vykop</c> (<c>:453</c>) and
    /// <c>alarma</c> (<c>:344</c>). It is <b>decremented once per 10 ticks</b> — the decrement sits inside
    /// the <c>aiTCh % 10 == 1</c> block (<c>:650-675</c>) — and it drives the state:
    /// <c>aiSpok &gt;= maxSpok</c> ⇒ <c>aiState = 3</c> (chase, full run), <c>aiSpok &gt; 0</c> ⇒
    /// <c>aiState = 2</c> (alert, <c>runSpeed * 0.6</c>), <c>aiSpok == 0</c> ⇒ back to idle/patrol.</para>
    ///
    /// <para><b>This class counts in TICKS, not in oracle decrements.</b> That is the one deliberate
    /// translation: the port decrements its <c>AlertTimerTicks</c> every tick, so each oracle decrement is
    /// worth <see cref="AiCheckIntervalTicks"/> ticks and every threshold is scaled by it. The result is
    /// observably identical — the drain <i>rate</i> is the only thing that differs, and it is folded into
    /// the constants — while avoiding a second cadence counter that could drift from the sensors'.</para>
    ///
    /// <para><b>The number that matters to a player.</b> The budget starts at
    /// <see cref="FullAwarenessTicks"/> (400) and drains, so the two bands are the <i>distance</i> it has
    /// to fall through, not the thresholds themselves: the unit is above
    /// <see cref="ChaseThresholdTicks"/> (300) for the first <b>100 ticks ≈ 3.3 s</b> — full-speed
    /// pursuit — and then above zero for the remaining <b>300 ticks = 10 s</b> of searching at alert
    /// speed before it gives up. <b>~13.3 s in total</b>, against the port's previous <i>one tick</i>.
    /// The shorter band is the chase: the unit commits hard and then searches patiently.</para>
    /// </summary>
    public static class EnemyAwarenessMath
    {
        /// <summary>AS3 <c>Unit.maxSpok</c> (<c>Unit.as:362</c>) — in oracle decrements, not ticks.</summary>
        public const int MaxSpok = 30;

        /// <summary>
        /// The oracle decrements <c>aiSpok</c> inside <c>if(World.w.enemyAct &gt; 1 &amp;&amp; aiTCh % 10 == 1 …)</c>
        /// (<c>UnitZombie.as:650</c>) — once every ten ticks. Every tick-valued constant below is this
        /// factor times the oracle's decrement-valued one.
        /// </summary>
        public const int AiCheckIntervalTicks = 10;

        /// <summary>
        /// <c>aiSpok = maxSpok + 10</c> — the awareness a unit has when it is <i>looking at</i> its
        /// target (<c>UnitZombie.as:657</c>), and the value <c>vykop</c> (<c>:453</c>) and
        /// <c>alarma</c> (<c>:344</c>) arm.
        /// </summary>
        public const int FullAwarenessTicks = (MaxSpok + 10) * AiCheckIntervalTicks;

        /// <summary>
        /// <c>aiSpok = maxSpok - 1</c> (<c>UnitZombie.as:661</c>) — the awareness a <b>sound-only</b>
        /// commit leaves behind. Deliberately <i>less</i> than
        /// <see cref="FullAwarenessTicks"/>: hearing something is a weaker commitment than seeing it, and
        /// the oracle expresses that as a shorter budget rather than a different behaviour.
        /// </summary>
        public const int SoundOnlyAwarenessTicks = (MaxSpok - 1) * AiCheckIntervalTicks;

        /// <summary>
        /// <c>if(aiSpok &gt;= maxSpok) { … aiState = 3; }</c> (<c>UnitZombie.as:633-640</c>) — at or above
        /// this the unit runs its target down; below it, and above zero, it only searches at alert speed.
        /// </summary>
        public const int ChaseThresholdTicks = MaxSpok * AiCheckIntervalTicks;

        /// <summary>
        /// <c>aiState = 3</c> — full-speed pursuit. AS3's <c>aiSpok &gt;= maxSpok</c>, and the reason a
        /// unit keeps <i>running</i> at a target it can no longer see.
        /// </summary>
        public static bool IsChasing(int awarenessTicks) => awarenessTicks >= ChaseThresholdTicks;

        /// <summary>
        /// <c>aiState == 2</c> — searching. AS3's <c>aiSpok &gt; 0</c>; below this the unit is calm and
        /// returns to patrol/idle.
        /// </summary>
        public static bool IsAware(int awarenessTicks) => awarenessTicks > 0;

        /// <summary>
        /// The same quantity in seconds, for the debug overlay and for anyone reading a symptom like
        /// "it forgets me too quickly" — the unit of the complaint rather than of the counter.
        /// </summary>
        public static float Seconds(int ticks) => ticks / (float)SimClock.CanonicalTicksPerSecond;
    }
}
