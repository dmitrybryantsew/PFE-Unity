using UnityEngine;
using PFE.Data.Definitions;
using PFE.Systems.Combat;
using PFE.Systems.Weapons;

namespace PFE.Systems.Audio
{
    /// <summary>
    /// Stateless helper that resolves and plays impact sounds for a projectile hit.
    ///
    /// Two-layer system matching AS3 Bullet.sound():
    ///   Layer 1 — Weapon-specific hit sound (WeaponDefinition.soundHit).
    ///             Always plays, no cooldown, no surface detection needed.
    ///   Layer 2 — Surface material sound (ImpactSoundTable lookup).
    ///             Gated by a global per-real-second cooldown to prevent sound spam
    ///             when many projectiles hit the same surface at once.
    ///             Requires the collider's GameObject (or an ancestor) to carry
    ///             IHasSurfaceMaterial, or to have IDamageable (→ Flesh).
    ///
    /// Usage:
    ///   Call ImpactSoundResolver.Resolve(...) from Projectile.HandleImpact.
    ///   No registration in VContainer needed — it is a pure static utility.
    /// </summary>
    public static class ImpactSoundResolver
    {
        // AS3 Snd.t_hit cooldown: random 3–6 flash frames → ~0.10–0.20 s at 30 fps.
        private const float CooldownMin = 3f / 30f;
        private const float CooldownMax = 6f / 30f;

        // Time.time stamp after which the next surface sound is allowed.
        private static float _nextAllowedTime;

        /// <summary>
        /// Play impact sounds for a projectile collision.
        /// Safe to call even when sound service or table are null — degrades silently.
        /// </summary>
        public static void Resolve(
            UnityEngine.Collider2D hitCollider,
            bool                   hasDamageContext,
            DamageContext          ctx,
            Vector2                worldPos,
            ISoundService          snd,
            ImpactSoundTable       table)
        {
            if (snd == null) return;

            // ── Layer 1: weapon-specific hit sound (always plays) ─────────────
            if (hasDamageContext)
            {
                var weaponHitSnd = ctx.Weapon?.soundHit;
                if (!string.IsNullOrEmpty(weaponHitSnd))
                    snd.Play(weaponHitSnd, worldPos);
            }

            // ── Layer 2: surface material sound (spam-throttled) ──────────────
            if (table == null) return;
            if (Time.time < _nextAllowedTime) return;

            SurfaceMaterial mat = DetectMaterial(hitCollider);
            if (mat == SurfaceMaterial.Default) return;

            DamageType dmgType = hasDamageContext ? ctx.DamageType : DamageType.PhysicalBullet;

            if (table.TryGetSound(mat, dmgType, out string soundId, out float volume))
            {
                snd.Play(soundId, worldPos, volume);
                _nextAllowedTime = Time.time + Random.Range(CooldownMin, CooldownMax);
            }
        }

        // ── Surface detection ─────────────────────────────────────────────────

        private static SurfaceMaterial DetectMaterial(UnityEngine.Collider2D col)
        {
            if (col == null) return SurfaceMaterial.Default;

            // 1. Explicit surface declaration on the collider's GameObject.
            var surf = col.GetComponent<IHasSurfaceMaterial>();
            if (surf != null) return surf.SurfaceMaterial;

            // 2. Living entity → Flesh (sound further qualified by DamageType in table).
            var damageable = col.GetComponent<PFE.Systems.Combat.IDamageable>();
            if (damageable != null) return SurfaceMaterial.Flesh;

            // 3. No material information available.
            return SurfaceMaterial.Default;
        }
    }
}
