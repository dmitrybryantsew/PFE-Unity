namespace PFE.Systems.Combat
{
    /// <summary>
    /// What became of a hit a damage source reported — the answer to the one question a
    /// <b>projectile</b> has to ask before it can decide whether to stop.
    ///
    /// <para><b>Why this exists.</b> In AS3 the bullet decides its own fate: <c>Bullet.run</c> calls
    /// <c>unit.udarBullet(this)</c> and reads the return — <c>if(_loc4_ &gt;= 0) { this.popadalo(_loc4_);
    /// ...; break; }</c> (<c>weapon/Bullet.as:529-554</c>). <c>popadalo</c> is the only thing that sets
    /// <c>babah</c>, the "I have stopped" flag the sub-step loop tests, so a <c>-1</c> return leaves the
    /// round flying and it can strike whatever stands behind. The port moved the damage formula into
    /// <see cref="DamageSystem"/> for reproducibility — correctly — but <c>Report</c> returned nothing,
    /// so the projectile could not see the outcome and stopped unconditionally. An <b>evaded</b> round
    /// was consumed; in the oracle it flies on. That is the whole of the divergence this type closes.</para>
    ///
    /// <para><b>It reports, it does not decide.</b> Nothing here is computed by the projectile. The
    /// verdict is produced by the one place that already ran the avoidance conjunction, so no roll is
    /// duplicated and the formula stays single-sourced. The projectile only branches on the answer.</para>
    ///
    /// <para><b>Every value here is known at report time, in both timing modes.</b>
    /// <c>PfeDebugSettings.SimTickDamage</c> defers the <i>application</i> of a hit to
    /// <c>SimTickOrder.Damage</c>, and it used to defer the avoidance roll with it — which made this
    /// type's answer unknowable at the one moment a projectile needs it, so an evaded round was
    /// consumed. The roll is now taken at report time in both modes (see
    /// <see cref="DamageSystem.Report"/>), so <see cref="Queued"/> means "it landed; the damage is
    /// applied at the drain", and <see cref="Evaded"/> means the same thing it does in immediate mode.
    /// No caller has to know which timing is in force.</para>
    /// </summary>
    public enum DamageVerdict
    {
        /// <summary>
        /// The hit landed and was recorded for <c>SimTickOrder.Damage</c>; the damage has not been
        /// applied yet. Only produced when <c>SimTickDamage</c> is on. <b>The avoidance roll has already
        /// been taken</b>, so this is not "unknown" — it means "it hit", and a caller that must decide
        /// now should stop the round exactly as it would for <see cref="Landed"/>.
        /// </summary>
        Queued = 0,

        /// <summary>
        /// The hit reached the target and was not evaded — damage was resolved, or the target was
        /// simply not hurt by it. AS3's <c>udarBullet</c> returns <c>0</c>, <c>1</c>, <c>10</c> or
        /// <c>12</c> for these, and every one of them is <c>&gt;= 0</c>, so the round stops. Note this
        /// includes a hit whose damage was fully absorbed: absorbing a round is not passing through it.
        /// </summary>
        Landed = 1,

        /// <summary>
        /// The hit landed on a live target and was <b>evaded</b> — AS3's <c>udarBullet</c> returned
        /// <c>-1</c> from the four-term conjunction at <c>Unit.as:4072</c>. Nothing happened: no damage,
        /// no wear, no crit, no knockback. The round is <b>not</b> consumed and carries on.
        /// </summary>
        Evaded = 2,

        /// <summary>
        /// There was nothing to resolve against — no target, or the target was already dead or
        /// destroyed by the time the hit was resolved. Distinct from <see cref="Evaded"/>: an ignored
        /// hit says nothing about the shot's accuracy, and callers that stop on <see cref="Evaded"/>
        /// should not also stop on this.
        /// </summary>
        Ignored = 3,
    }
}
