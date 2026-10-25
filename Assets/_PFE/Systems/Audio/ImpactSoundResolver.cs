using UnityEngine;
using PFE.Data.Definitions;
using PFE.Systems.Combat;
using PFE.Systems.Weapons;

namespace PFE.Systems.Audio
{
    /// <summary>
    /// Stateless helper that resolves and plays impact sounds for a <b>bullet or a swing</b>.
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
    /// Usage — <b>two callers, and they are the two arms of the same AS3 function</b>:
    ///   <c>Projectile.HandleImpact</c> (a round stopping on a unit, a tile or a prop) and
    ///   <c>MeleeHitVolume</c> (a swing, which <i>is</i> a <c>Bullet</c> in the oracle —
    ///   <c>WClub.as:148-156</c>). No registration in VContainer needed — it is a pure static utility.
    ///
    /// <para><b>The melee arm applies one gate the projectile arm does not.</b> See
    /// <see cref="PlaysMaterialSound"/>: AS3 refuses the whole material block for a weapon whose
    /// decal is outside 1..6, and only the melee path honours it so far. That asymmetry is recorded
    /// there, not accidental.</para>
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
        ///
        /// <para>Callers that must also honour AS3's <b>decal</b> gate fold it in here, through
        /// <see cref="PlaysMaterialSound"/>. The melee path does; the projectile path does not, and
        /// that is a recorded divergence rather than an oversight — see <see cref="PlaysMaterialSound"/>.
        /// Keeping the decal test outside this method is what stops one call site's rule from silently
        /// changing the other's.</para>
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

        // ── The decal gate ────────────────────────────────────────────────────

        /// <summary>
        /// AS3's <b>decal gate</b> on the material half of <c>Bullet.sound()</c>:
        /// <c>if(this.tipDecal &lt;= 0 || this.tipDecal &gt; 6) return;</c>
        /// (<c>weapon/Bullet.as:612-615</c>).
        /// </summary>
        /// <remarks>
        /// <para><b>What it means.</b> Only the six <i>bullet-hole</i> decals (1..6) make a surface
        /// sound at all. A laser sword (<c>vis tipdec='12'</c>), a plasma bolt, an explosive or a
        /// fire weapon leaves a scorch, not a hole, and the oracle gives it <b>no</b> material sound —
        /// it relies on its own <c>&lt;snd hit&gt;</c> instead. The gate is on the whole material block,
        /// so it suppresses <c>hit_flesh</c> as well: a laser sword cutting a body is silent apart from
        /// its <c>hit</c> sample. Layer 1 (<see cref="Resolve"/>'s weapon hit sound) is <i>above</i> the
        /// gate in AS3 and therefore unaffected.</para>
        ///
        /// <para><b>Where it is applied, and where it is not.</b> The melee path applies it, because
        /// melee <i>is</i> a <c>Bullet</c> in the oracle (<c>WClub.as:148-156</c>) and this is the
        /// function being ported. The <b>projectile path does not</b> — it shipped before this gate was
        /// read, so every energy weapon in the port currently makes a surface sound the oracle would
        /// suppress. That is a real, recorded divergence, left standing on purpose: fixing it is a
        /// change to a path this work did not touch, and it makes weapons <i>quieter</i>, which is the
        /// kind of change that wants its own play-test rather than a silent ride-along. Do not "tidy"
        /// it into <see cref="Resolve"/> without deciding that separately — it would move every
        /// laser, plasma and explosive weapon's impact audio at once.</para>
        /// </remarks>
        public static bool PlaysMaterialSound(DecalType decal)
        {
            int value = (int)decal;
            return value >= 1 && value <= 6;
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
