using PFE.Core.Rng;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// The per-hit damage spread — the port of one line, <c>Unit.as:4085</c>:
    /// <c>_loc4_ = param1.damage * (Math.random() * 0.6 + 0.7);</c>
    ///
    /// <para><b>Range is 0.7 .. 1.3, uniform.</b> A hit does not do its listed damage; it does
    /// <c>[0.7, 1.3)</c> times it. This is the last unimplemented term of the damage chain, and the
    /// only one that is not conditional on anything: with it missing, every weapon in the port did
    /// <i>exactly</i> its listed damage, which is a state AS3 never produces.</para>
    ///
    /// <para><b>Where it sits in the order, which is the whole port.</b> AS3 computes the spread in
    /// <c>udarBullet</c> — <i>after</i> the hit lands and <i>before</i> it calls <c>this.damage()</c>,
    /// whose body applies vulnerability (<c>:3527-3530</c>), then the armour reduction
    /// (<c>:3611-3645</c>), then crit (<c>:3652-3656</c>). So the chain is
    /// <b>variance → vulnerability → armour → crit</b>, and the spread therefore lands on the
    /// <i>pre-armour</i> number. That is not a detail: putting it after the armour subtraction would
    /// make a flat reduction absorb proportionally more of a low roll, so armour would appear to get
    /// stronger exactly when the dice were bad — and crit, which multiplies whatever it is handed,
    /// would amplify a different base than the oracle's.</para>
    ///
    /// <para><b>Blasts do not roll.</b> AS3's explosion path (<c>Bullet.explRun</c>,
    /// <c>weapon/Bullet.as:763-789</c>) calls <c>unit.damage()</c> directly and never enters
    /// <c>udarBullet</c>, so an explosion's damage is its falloff value exactly. The port's
    /// <c>DamageSystem</c> mirrors that by rolling only on the non-explosion path.</para>
    ///
    /// <para><b>A zero-damage shot consumes no roll.</b> The spread lives inside AS3's
    /// <c>if (param1.damage &gt; 0)</c> block (<c>:4079-4109</c>), so a shot with no damage exits at
    /// <c>return 0</c> before <c>Math.random()</c> is reached. Reproduced, because the combat stream is
    /// shared with the armour-reliability and crit rolls — an extra draw here would shift every later
    /// roll in the same tick.</para>
    ///
    /// <para><b>The debug override still rolls.</b> AS3's console toggle <c>World.w.testDam</c>
    /// (<c>World.as:202</c>, flipped by <c>Consol.as:475</c>) replaces the result with the raw damage
    /// — but it does so on the line <i>after</i> the roll, so <b>the random draw happens either
    /// way</b> (<c>:4085-4089</c>). <see cref="Roll"/> reproduces that deliberately: it always draws
    /// and only then decides whether to report the roll or the identity. A future reader who "optimises"
    /// the draw away would desynchronise the stream for the rest of the tick, so the behaviour is
    /// pinned by a guard rather than left as a comment.</para>
    /// </summary>
    public static class DamageVariance
    {
        /// <summary>The multiplier's floor. AS3's literal <c>0.7</c>.</summary>
        public const float MinMultiplier = 0.7f;

        /// <summary>
        /// The multiplier's width. AS3's literal <c>0.6</c>, so the range is
        /// <c>[0.7, 1.3)</c> — <c>NextFloat()</c> is half-open, and the top of the range is therefore
        /// excluded, exactly as AS3's <c>Math.random()</c> excludes 1.0.
        /// </summary>
        public const float Spread = 0.6f;

        /// <summary>The multiplier a fully deterministic hit uses — no spread.</summary>
        public const float NoVariance = 1f;

        /// <summary>
        /// One draw from the spread, as a multiplier. Always consumes exactly one
        /// <see cref="IRngService.NextFloat"/> — see the class remarks on the debug override.
        /// </summary>
        /// <param name="rng">The combat stream. Required; there is no static fallback, because a hidden
        /// global generator is precisely what a lockstep peer cannot reproduce.</param>
        /// <param name="deterministic">
        /// AS3's <c>World.w.testDam</c>. <c>true</c> returns <see cref="NoVariance"/> — the raw damage —
        /// <b>after</b> the draw has already been taken.
        /// </param>
        public static float Roll(IRngService rng, bool deterministic = false)
        {
            float roll = rng.NextFloat() * Spread + MinMultiplier;

            return deterministic ? NoVariance : roll;
        }

        /// <summary>
        /// The spread applied to a damage figure — AS3's <c>param1.damage * (…)</c> in one call.
        /// </summary>
        /// <remarks>
        /// Takes <paramref name="damage"/> as a value rather than reading it from a context so the
        /// call site stays a plain multiply: the caller has already decided the shot does damage (the
        /// oracle's <c>&gt; 0</c> gate), and folding that test in here would hide where the roll is
        /// consumed.
        /// </remarks>
        public static float Apply(float damage, IRngService rng, bool deterministic = false)
            => damage * Roll(rng, deterministic);
    }
}
