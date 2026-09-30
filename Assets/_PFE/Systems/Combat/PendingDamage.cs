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

        private PendingDamage(
            in DamageContext context,
            IDamageable target,
            Vector3 impactPosition,
            bool isExplosion,
            Vector3 explosionCentre,
            float explosionRadius,
            float factionMultiplier)
        {
            Context = context;
            Target = target;
            ImpactPosition = impactPosition;
            IsExplosion = isExplosion;
            ExplosionCentre = explosionCentre;
            ExplosionRadius = explosionRadius;
            FactionMultiplier = factionMultiplier;
        }

        /// <summary>A single-target hit — a bullet, a melee sweep, anything that reads <c>BaseDamage</c>.</summary>
        public static PendingDamage Direct(in DamageContext context, IDamageable target, Vector3 impactPosition)
            => new PendingDamage(context, target, impactPosition,
                                 isExplosion: false,
                                 explosionCentre: impactPosition,
                                 explosionRadius: 0f,
                                 factionMultiplier: 1f);

        /// <summary>
        /// An AoE blast against one target in range. The caller has already enumerated the overlap and
        /// computed the per-target <paramref name="factionMultiplier"/>; the system owns the falloff.
        /// </summary>
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
                                 factionMultiplier: factionMultiplier);
    }
}
