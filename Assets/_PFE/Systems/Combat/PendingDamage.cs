using UnityEngine;
using PFE.Systems.Weapons;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// One hit that a damage source has <b>reported</b>, waiting for the tick that resolves it.
    ///
    /// <para><b>Why a queue exists.</b> AS3 resolved damage inline, at the moment of contact, because
    /// <c>World.step()</c> was the only clock and everything in it was already ordered. The port runs a
    /// fixed-step simulation with an explicit system order, so a hit is a <i>fact to be recorded</i> and
    /// the reduction is a step to be executed — at <c>SimTickOrder.Damage</c>, after every damage
    /// source has finished moving for the tick. Resolving inline instead would make the result depend on
    /// the order the physics engine happened to report contacts in, which is neither reproducible nor
    /// replicable across peers.</para>
    ///
    /// <para>Everything here is a value or a reference that is safe to hold for one tick: the
    /// <see cref="DamageContext"/> is a readonly struct, and <see cref="Target"/> is re-checked for
    /// liveness at drain time because it may have died earlier in the same drain.</para>
    /// </summary>
    public readonly struct PendingDamage
    {
        /// <summary>Weapon payload — damage, type, pierce, crit, faction. Copied, not referenced.</summary>
        public readonly DamageContext Context;

        /// <summary>The unit this hit lands on. May be dead or destroyed by drain time; re-check.</summary>
        public readonly IDamageable Target;

        /// <summary>Where the hit is reported from, for the floating-number message.</summary>
        public readonly Vector3 ImpactPosition;

        /// <summary>
        /// True for an AoE blast. Blasts read <see cref="DamageContext.ExplosionDamage"/> and scale it by
        /// distance falloff and the faction multiplier; direct hits read
        /// <see cref="DamageContext.BaseDamage"/>.
        /// </summary>
        public readonly bool IsExplosion;

        /// <summary>Blast centre. Unused for a direct hit.</summary>
        public readonly Vector3 ExplosionCentre;

        /// <summary>Blast radius. Unused for a direct hit.</summary>
        public readonly float ExplosionRadius;

        /// <summary>
        /// Per-target friendly-fire scale, from <c>FactionRule.ExplosionMultiplier</c>. Applied to the
        /// blast damage <b>before</b> crit, so a same-faction target gets a quarter-strength crit rather
        /// than a full-strength one truncated afterwards. 1 is the no-faction case.
        /// </summary>
        public readonly float FactionMultiplier;

        /// <summary>
        /// How far the round had flown when it reported this hit, in <b>pixels</b> — AS3
        /// <c>Bullet.dist</c>. The one hit-time input to
        /// <see cref="HitAvoidance.RollsHit"/>; everything else about avoidance is a property of the
        /// shot and lives on <see cref="Context"/>. <c>0</c> for a melee sweep and for anything that
        /// does not travel, which is correct — the oracle's melee branch has no distance term.
        /// </summary>
        public readonly float TravelDistancePixels;

        /// <summary>
        /// This hit reaches the target through AS3's <c>Unit.damage()</c> <b>directly</b>, not through
        /// <c>udarBullet</c> — so the resolver must take <b>none</b> of the three things
        /// <c>udarBullet</c> owns: the hit-avoidance roll (<c>:4072</c>), the damage-spread roll
        /// (<c>:4085</c>), and the knockback throw <c>otbros</c> (<c>:4091</c>).
        ///
        /// <para><b>It is a fact about the call path, and it is NOT the same as "no spread".</b> AS3
        /// has a second spread with the identical shape at <c>Bullet.explGas():763</c>, applied inline
        /// before the blast calls <c>damage()</c>; and the <c>explTip 1</c> shape reaches the
        /// <c>udarBullet</c> spread anyway, through the child bullet <c>explBullet</c> spawns. So a
        /// blast skips <i>this</i> spread and still takes one. The variance question is asked
        /// separately, through <see cref="SkipsDamageVariance"/> — reading this flag as "no spread" is
        /// exactly the mistake this name used to invite.</para>
        /// </summary>
        /// <remarks>
        /// <para><b>AS3 has two damage entry shapes and this is the second one.</b> <c>udarBullet</c>
        /// (<c>Unit.as:4067</c>) is the shot path: it rolls <c>miss</c>/<c>precision</c>/<c>dodge</c>,
        /// then spreads the damage by <c>Math.random() * 0.6 + 0.7</c> (<c>:4085</c>), then calls
        /// <c>damage()</c>, then throws the target with <c>otbros()</c> (<c>:4091</c>). Everything that
        /// reaches <c>damage()</c> <i>without</i> going through <c>udarBullet</c> therefore skips all
        /// three: the explosion path (<c>Bullet.explRun</c>), a prop impact (<c>Unit.udarBox</c>,
        /// <c>:4237</c>) and a floor trap (<c>Trap.as:188</c>).</para>
        ///
        /// <para><b>One predicate, three gates, and that is the point.</b> <c>otbros</c> is called from
        /// exactly one place in the whole oracle — <c>:4091</c>, inside <c>udarBullet</c> — so "no
        /// <c>udarBullet</c>" and "no throw" are the same fact, not two rules that happen to agree. This
        /// flag is that fact; <c>DamageSystem</c> reads it at the avoidance and knockback gates. Gating
        /// the knockback on <c>IsExplosion</c> instead (which is what the code did before a second
        /// non-<c>udarBullet</c> caller existed) is correct for blasts and silently wrong for a crate
        /// impact: the impact would take a knockback draw the oracle never takes, and because its impulse
        /// is zero nothing visible would reveal it.</para>
        ///
        /// <para><b>Why a flag rather than "explosions skip both".</b> That was the previous shape of
        /// this code, and it read as a fact about explosions when the real rule is a fact about the
        /// <i>call path</i>. A contact hit written against <see cref="IsExplosion"/> would have had to
        /// claim to be a blast — which also switches the damage source to
        /// <c>ExplosionDamage</c> and applies distance falloff — so the two had to be separated before
        /// a second non-<c>udarBullet</c> caller could exist.</para>
        /// </remarks>
        public readonly bool ReachedDamageWithoutUdarBullet;

        /// <summary>
        /// Whether the damage spread (<c>Math.random() * 0.6 + 0.7</c>) is skipped for this hit.
        ///
        /// <para><b>Not the same question as
        /// <see cref="ReachedDamageWithoutUdarBullet"/>, and the difference is one caller.</b> AS3 has
        /// the spread at <b>two</b> sites with the same shape: <c>udarBullet():4085</c> and
        /// <c>explGas():763</c>. So:</para>
        /// <list type="bullet">
        ///   <item><description><b>Direct</b> — takes the <c>udarBullet</c> spread. <c>false</c>.</description></item>
        ///   <item><description><b>Contact</b> (a prop impact, <c>udarBox</c>) — reaches <c>damage()</c>
        ///     directly and <c>udarBox</c> has no spread of its own. <c>true</c>. This is the only path
        ///     in the oracle with no spread anywhere, which is why the flag exists at all.</description></item>
        ///   <item><description><b>Explosion</b> — <c>explGas</c> applies its own inline spread
        ///     (<c>:763</c>), and an <c>explTip 1</c> blast takes the <c>udarBullet</c> spread through
        ///     the child bullet <c>explBullet()</c> spawns. <c>false</c>.</description></item>
        /// </list>
        ///
        /// <para>The port models a blast as one reported hit rather than as a child bullet, so
        /// "the spread applies" has to be stated here instead of falling out of the second bullet's
        /// journey. Both of the oracle's routes lead to the same multiplier.</para>
        /// </summary>
        public readonly bool SkipsDamageVariance;

        private PendingDamage(
            in DamageContext context,
            IDamageable target,
            Vector3 impactPosition,
            bool isExplosion,
            Vector3 explosionCentre,
            float explosionRadius,
            float factionMultiplier,
            float travelDistancePixels,
            bool reachedDamageWithoutUdarBullet,
            bool skipsDamageVariance)
        {
            Context                       = context;
            Target                        = target;
            ImpactPosition                = impactPosition;
            IsExplosion                   = isExplosion;
            ExplosionCentre               = explosionCentre;
            ExplosionRadius               = explosionRadius;
            FactionMultiplier             = factionMultiplier;
            TravelDistancePixels          = travelDistancePixels;
            ReachedDamageWithoutUdarBullet = reachedDamageWithoutUdarBullet;
            SkipsDamageVariance           = skipsDamageVariance;
        }

        /// <summary>A single-target hit — a bullet, a melee sweep, anything that reads <c>BaseDamage</c>.</summary>
        /// <param name="travelDistancePixels">
        /// Distance flown, in pixels — AS3 <c>Bullet.dist</c>. Defaults to <c>0</c>, which is right for
        /// a melee sweep and for a hitscan; a projectile should pass its own.
        /// </param>
        public static PendingDamage Direct(
            in DamageContext context, IDamageable target, Vector3 impactPosition,
            float travelDistancePixels = 0f)
            => new PendingDamage(context, target, impactPosition,
                                 isExplosion: false,
                                 explosionCentre: impactPosition,
                                 explosionRadius: 0f,
                                 factionMultiplier: 1f,
                                 travelDistancePixels: travelDistancePixels,
                                 reachedDamageWithoutUdarBullet: false,
                                 skipsDamageVariance: false);

        /// <summary>
        /// A prop impact or any other hit that reaches <c>Unit.damage()</c> without a bullet — AS3
        /// <c>Unit.udarBox</c> (<c>Unit.as:4237</c>).
        /// </summary>
        /// <remarks>
        /// <b>Not an explosion and not a shot.</b> It reads <c>BaseDamage</c> like
        /// <see cref="Direct"/>, but takes neither the avoidance roll nor the damage-spread roll,
        /// because <c>udarBox</c> calls <c>damage()</c> directly and has no spread of its own. See
        /// <see cref="ReachedDamageWithoutUdarBullet"/> and <see cref="SkipsDamageVariance"/> for the
        /// citations — this is the only path in the oracle that skips the spread, so the two flags
        /// happen to agree here and diverge everywhere else.
        /// </remarks>
        public static PendingDamage Contact(
            in DamageContext context, IDamageable target, Vector3 impactPosition)
            => new PendingDamage(context, target, impactPosition,
                                 isExplosion: false,
                                 explosionCentre: impactPosition,
                                 explosionRadius: 0f,
                                 factionMultiplier: 1f,
                                 travelDistancePixels: 0f,
                                 reachedDamageWithoutUdarBullet: true,
                                 skipsDamageVariance: true);

        /// <summary>
        /// An AoE blast against one target in range. The caller has already enumerated the overlap and
        /// computed the per-target <paramref name="factionMultiplier"/>; the system owns the falloff.
        /// </summary>
        /// <remarks>
        /// <para><b>A blast is never subject to hit avoidance, and that is the oracle.</b> AS3's
        /// explosion path (<c>Bullet.explRun</c>, <c>weapon/Bullet.as:763-789</c>) calls
        /// <c>unit.damage()</c> directly; the <c>udarBullet</c> conjunction — and therefore every
        /// <c>miss</c>, <c>precision</c> and <c>dodge</c> term — is skipped entirely. So
        /// <see cref="TravelDistancePixels"/> is unused on this path and the resolver must not roll for
        /// it.</para>
        ///
        /// <para><b>But it DOES take the damage spread</b>, which is why
        /// <see cref="SkipsDamageVariance"/> is <c>false</c> here and not a copy of
        /// <see cref="ReachedDamageWithoutUdarBullet"/>. The oracle reaches the spread twice over:
        /// inline at <c>explGas():763</c>, and — for the <c>explTip 1</c> shape — through the child
        /// bullet's own <c>udarBullet</c> (<c>:4085</c>). Both routes produce the same
        /// <c>×0.7..1.3</c> multiplier, so modelling the blast as one reported hit does not change the
        /// answer.</para>
        /// </remarks>
        public static PendingDamage Explosion(
            in DamageContext context,
            IDamageable target,
            Vector3 targetPosition,
            Vector3 explosionCentre,
            float explosionRadius,
            float factionMultiplier = 1f)
            => new PendingDamage(context, target, targetPosition,
                                 isExplosion: true,
                                 explosionCentre: explosionCentre,
                                 explosionRadius: explosionRadius,
                                 factionMultiplier: factionMultiplier,
                                 travelDistancePixels: 0f,
                                 reachedDamageWithoutUdarBullet: true,
                                 skipsDamageVariance: false);
    }
}
