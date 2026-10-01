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
        /// <param name="surfaceSound">
        /// Layer 2. <c>false</c> for an <b>evaded</b> hit, where AS3 passes the verdict <c>-1</c> to
        /// <c>Bullet.sound()</c> and matches none of its material cases (<c>weapon/Bullet.as:606-663</c>)
        /// — so only the weapon's own hit sound plays. Layer 1 is unaffected either way.
        /// </param>
        /// <remarks>
        /// <b>A recorded approximation.</b> AS3 also stamps <c>Snd.t_hit</c> on a suppressed hit — the
        /// assignment at <c>:661</c> sits outside the <c>param1</c> chain — whereas this throttle is
        /// only stamped when a surface sound actually plays. So an evaded round consumes AS3's
        /// anti-spam window and not this one. Pre-existing, narrow (it needs a real material hit inside
        /// the same ~0.1-0.2 s), and left alone rather than half-fixed.
        /// </remarks>
        public static void Resolve(
            UnityEngine.Collider2D hitCollider,
            bool                   hasDamageContext,
            DamageContext          ctx,
            Vector2                worldPos,
            ISoundService          snd,
            ImpactSoundTable       table,
            bool                   surfaceSound = true)
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
            if (!surfaceSound) return;
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
