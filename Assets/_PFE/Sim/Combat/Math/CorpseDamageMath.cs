namespace PFE.Systems.Combat
{
    /// <summary>
    /// The corpse budget — AS3's <c>hp</c> below zero, which is what eventually destroys a body.
    ///
    /// <para><b>The oracle's mechanism, in three lines.</b> <c>Unit.damage()</c> does
    /// <c>this.hp -= param1</c> (<c>unit/Unit.as:3699</c>) with <b>no clamp</b>, and then
    /// <c>if(this.hp &lt;= 0)</c> re-enters <c>die()</c> (<c>:3713-3745</c>) — on every hit, including
    /// hits on a body that is already dead. <c>die()</c>'s second arm keeps the corpse only while
    /// <c>this.trup &amp;&amp; this.hp &gt; -this.maxhp * 2</c> (<c>:4333</c>); once the HP has gone far
    /// enough past zero that test fails and the body falls through to
    /// <c>initBurn(4 + blood)</c> (a 40-frame dissolve, <c>:4347-4353</c>) or
    /// <c>expl(); exterminate()</c> (bloodless, <c>:4360-4367</c>).</para>
    ///
    /// <para><b>Why this is a separate class.</b> The port's <c>UnitStats.CurrentHp</c> is clamped to
    /// <c>[0, maxHp]</c> for the HUD, so the threshold <c>hp &lt;= -maxhp * 2</c> is arithmetically
    /// unreachable there. Rather than un-clamp a field every UI read depends on, the missing half is
    /// tracked as an explicit <i>overkill</i> accumulator, and the three rules that turn damage into
    /// overkill and overkill into "the body is gone" live here — Unity-free, so they can be executed
    /// against deliberately-wrong variants outside the editor, like <see cref="PenetrationMath"/>.</para>
    ///
    /// <para><b>The accumulator is equivalent to the oracle's negative HP, and that is not an
    /// approximation.</b> <c>hp</c> only ever moves down through damage and up through healing; the
    /// port's clamp at 0 loses exactly the part below zero, which is
    /// <c>max(0, amount - hpBefore)</c> per hit. Summing that is <c>-hp</c> for any sequence with no
    /// heal in it, and <see cref="OverkillAfterHeal"/> gives the heal back its first claim on the
    /// budget — which is what AS3's <c>hp += param1</c> does when <c>hp</c> is negative.</para>
    /// </summary>
    public static class CorpseDamageMath
    {
        /// <summary>
        /// AS3's destroy threshold factor — <c>hp &lt;= -maxhp * 2</c> (<c>Unit.as:4333</c>). A body
        /// survives until it has taken twice its own maximum health <i>past</i> death.
        /// </summary>
        public const float DestroyOverkillFactor = 2f;

        /// <summary>
        /// How long a bloody corpse takes to dissolve — <c>Desintegr.burnTime1 + burnTime2</c>
        /// (<c>serv/Desintegr.as:38-39</c>: 10 + 30), in canonical 30 Hz frames. The oracle removes the
        /// unit at <c>Unit.as:1839-1843</c> when <c>burn.vse</c> goes true, i.e. on frame 40.
        /// </summary>
        public const int DissolveFrames = 40;

        /// <summary>
        /// Frames between dissolve particles. AS3 emits from <c>Desintegr.step</c> on every frame of
        /// the second phase (<c>:124-133</c>), gated on the unit's mass; the port thins it to keep the
        /// particle budget for a whole room of bodies.
        /// </summary>
        public const int DissolveParticleIntervalFrames = 4;

        /// <summary>
        /// How much of <paramref name="damage"/> lands <i>below</i> zero HP — the part AS3's unclamped
        /// <c>hp</c> would have kept.
        /// </summary>
        /// <param name="hpBefore">The target's HP before the hit, as the port stores it (clamped).</param>
        /// <param name="damage">The hit, after every multiplier and reduction.</param>
        /// <remarks>
        /// A negative <paramref name="damage"/> is healing and adds no overkill; so does a hit that does
        /// not reach zero. <c>Math.Max(hpBefore, 0)</c> is not defensive noise: the accumulator is also
        /// read for a target that is already a corpse, whose stored HP is exactly 0, and a stale
        /// negative would silently discount the next hit.
        /// </remarks>
        public static float OverkillAdded(float hpBefore, float damage)
        {
            if (damage <= 0f)
                return 0f;

            float aboveZero = damage - (hpBefore > 0f ? hpBefore : 0f);
            return aboveZero > 0f ? aboveZero : 0f;
        }

        /// <summary>
        /// Whether the body has taken enough to be destroyed — AS3's
        /// <c>hp &lt;= -maxhp * 2</c>, expressed as overkill <c>&gt;= maxhp * 2</c>.
        /// </summary>
        /// <remarks>
        /// Written as the negation of the oracle's <i>survive</i> test rather than as a separate
        /// threshold, so the boundary matches: AS3 keeps the corpse while <c>hp &gt; -maxhp*2</c>
        /// strictly, so exactly <c>2 * maxhp</c> of overkill destroys it. A zero or negative max HP
        /// therefore destroys immediately, which is the oracle's own answer and not a special case.
        /// </remarks>
        public static bool IsDestroyed(float overkill, float maxHp)
            => overkill >= maxHp * DestroyOverkillFactor;

        /// <summary>
        /// The overkill left after a heal, with the remainder reported for the HP pool.
        /// </summary>
        /// <param name="overkill">The body's current overkill.</param>
        /// <param name="healAmount">The heal, positive.</param>
        /// <param name="leftoverForHp">
        /// What is left of the heal once the negative-HP part has been paid off. Zero when the heal was
        /// entirely spent climbing back out of the grave — AS3's <c>hp += param1</c> starting from a
        /// negative <c>hp</c>.
        /// </param>
        /// <returns>The new overkill, never negative.</returns>
        public static float OverkillAfterHeal(float overkill, float healAmount, out float leftoverForHp)
        {
            leftoverForHp = 0f;

            if (healAmount <= 0f)
                return overkill > 0f ? overkill : 0f;

            if (overkill <= 0f)
            {
                leftoverForHp = healAmount;
                return 0f;
            }

            if (healAmount <= overkill)
                return overkill - healAmount;

            leftoverForHp = healAmount - overkill;
            return 0f;
        }
    }
}
