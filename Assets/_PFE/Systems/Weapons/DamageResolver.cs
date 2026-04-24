using UnityEngine;
using PFE.Systems.Combat;
using MessagePipe;
using PFE.Core.Messages;

namespace PFE.Systems.Weapons
{
    /// <summary>
    /// Single point of final damage computation.
    ///
    /// Replaces the current pattern where Projectile calls target.TakeDamage(rawDamage)
    /// directly, bypassing armour, crit, ammo multipliers, and tile destruction.
    ///
    /// Usage:
    ///   DamageResolver.Resolve(context, target, impactPos, publisher);
    ///
    /// AS3 equivalent: the chain inside Bullet.crash() → Unit.addHP() that reads
    /// pier, armorMult, critCh, critM, tipDamage from the bullet.
    ///
    /// Kept as a static helper (no DI required) so Projectile and MeleeHitVolume
    /// can both call it without needing a service locator.
    /// </summary>
    public static class DamageResolver
    {
        /// <summary>
        /// Compute final damage from a DamageContext and apply it to a target.
        /// Returns the final damage dealt (after all modifiers).
        /// </summary>
        public static float Resolve(
            in DamageContext ctx,
            IDamageable target,
            Vector3 impactPos,
            IPublisher<DamageDealtMessage> publisher = null)
        {
            if (target == null || !target.IsAlive) return 0f;

            // ── Armour reduction ──────────────────────────────────────────────
            // AS3: effectiveArmour = target.getArmour(tipDamage) * armorMult (ammoArmor)
            // Piercing reduces armour before subtraction.
            // For now: simplified — full armour system wired in when ICharacterStats
            // exposes GetArmour(DamageType).
            float armour        = 0f;                       // TODO: target.GetArmour(ctx.DamageType)
            float reducedArmour = armour * ctx.ArmorMultiplier * (1f - Mathf.Clamp01(ctx.Piercing));
            float afterArmour   = Mathf.Max(0f, ctx.BaseDamage - reducedArmour);

            // ── Critical hit ─────────────────────────────────────────────────
            // AS3: if (rnd < critCh) finalDam *= critDamMult + critM
            bool isCrit = Random.value < ctx.CritChance;
            float critMultiplier = isCrit ? (ctx.CritMultiplier) : 1f;
            float finalDamage = afterArmour * critMultiplier;

            // Clamp to non-negative.
            finalDamage = Mathf.Max(0f, finalDamage);

            // ── Apply ────────────────────────────────────────────────────────
            target.TakeDamage(finalDamage);

            // ── Publish event for HUD / floating text ─────────────────────────
            publisher?.Publish(new DamageDealtMessage
            {
                damage    = finalDamage,
                position  = impactPos,
                isCritical = isCrit,
                isMiss    = false,
            });

            return finalDamage;
        }

        /// <summary>
        /// AoE variant — resolve explosion damage (damageExpl) against a target.
        /// Distance falloff is linear from centre to explRadius edge.
        /// </summary>
        public static float ResolveExplosion(
            in DamageContext ctx,
            IDamageable target,
            Vector3 targetPos,
            Vector3 explosionCentre,
            float explRadius,
            IPublisher<DamageDealtMessage> publisher = null)
        {
            if (target == null || !target.IsAlive || ctx.ExplosionDamage <= 0f) return 0f;

            // Distance falloff: 1 at centre, 0 at edge.
            float dist    = Vector3.Distance(targetPos, explosionCentre);
            float falloff = explRadius > 0f ? Mathf.Clamp01(1f - dist / explRadius) : 1f;
            float scaled  = ctx.ExplosionDamage * falloff;

            bool isCrit = Random.value < ctx.CritChance;
            float final  = Mathf.Max(0f, scaled * (isCrit ? ctx.CritMultiplier : 1f));

            target.TakeDamage(final);

            publisher?.Publish(new DamageDealtMessage
            {
                damage     = final,
                position   = targetPos,
                isCritical = isCrit,
                isMiss     = false,
            });

            return final;
        }
    }
}
