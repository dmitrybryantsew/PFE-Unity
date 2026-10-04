using System;
using System.Collections.Generic;
using PFE.Core.Rng;
using PFE.Data.Definitions;

namespace PFE.Systems.Particles
{
    /// <summary>
    /// Everything AS3's blood block reads (<c>fe/unit/Unit.as:3844-3901</c>) — and nothing it does
    /// not.
    ///
    /// <para><b>Positions are AS3 room-local pixels and the anchor is the unit.</b> Two of the
    /// oracle's three emissions hang off the unit's own <c>X</c>/<c>Y</c> — the standing spray at
    /// <c>Y - scY/2</c> and the blood explosion — and only the bullet-path spray is anchored at the
    /// round's own contact point. So the unit is the reference and the impact point is carried
    /// beside it rather than replacing it, which is the oracle's own shape: the <c>bloodexpl</c>
    /// emit at <c>:3897</c> reads <c>X</c>/<c>Y</c> even on the branch where a bullet was
    /// supplied.</para>
    ///
    /// <para><b>Why a context rather than sixteen arguments.</b> The same reason
    /// <c>SpellCastContext</c> exists: every field here is a distinct primitive read straight out of
    /// the oracle, and a positional call site that long is where a silent transposition lives.</para>
    /// </summary>
    public readonly struct BloodSprayContext
    {
        /// <summary>
        /// The target's blood — AS3 <c>Unit.blood</c> (<c>Unit.as:416</c>), read from
        /// <c>IBloodSpraySource.BloodType</c>. <see cref="BloodType.None"/> is the authored "this
        /// creature does not bleed" and ends the decision immediately.
        /// </summary>
        public readonly BloodType Blood;

        /// <summary>The damage type that landed — AS3 <c>param2</c>.</summary>
        public readonly DamageType DamageType;

        /// <summary>
        /// The damage actually dealt — AS3's <c>param1</c> as it stands at <c>:3844</c>, i.e. after
        /// vulnerability, armour and crit. It is read three times: the spray's particle count
        /// (<c>/3</c> standing, <c>/5</c> from a bullet) and the blood explosion's probability
        /// (<c>/1000</c>).
        /// </summary>
        /// <remarks>
        /// <para><b>One number where the oracle has two, and that is recorded rather than hidden.</b>
        /// AS3 tests <c>if(param1 &gt; 0)</c> at <c>:3668</c> — <i>before</i> <c>param1 *=
        /// allVulnerMult</c> at <c>:3681</c> — and then does the blood arithmetic on the post-multiplied
        /// value. The port's damage path does not apply <c>allVulnerMult</c> at all
        /// (<c>CharacterStats.allVulnerMult</c> exists but no damage-path consumer reads it), so the
        /// single value the port has is the oracle's pre-multiplier one. It is therefore right for the
        /// gate and potentially wrong for the arithmetic by exactly that factor.</para>
        /// </remarks>
        public readonly float Damage;

        /// <summary>
        /// The target's weight — AS3 <c>massa</c>, read through <c>IDamageable.Mass</c>. Already the
        /// divided-by-50 figure, which is what the oracle compares against <c>0.2</c>: AS3 assigns
        /// <c>massa = massaFix</c> and <c>massaFix = @massafix / 50</c> (<c>Unit.as:1047-1058</c>).
        /// </summary>
        public readonly float Mass;

        /// <summary>
        /// Whether <b>either</b> crit channel fired — AS3's <c>_loc5_ &gt; 0</c>, supplied from
        /// <c>DamageOutcome.AnyCritical</c>. A crit makes the blood explosion <i>more</i> likely,
        /// which reads backwards until you see the comparison: the roll is on the right of
        /// <c>param1 / 1000 &gt; _loc12_</c>, so shrinking it raises the odds.
        /// </summary>
        public readonly bool AnyCritical;

        /// <summary>
        /// Whether the hit arrived on a bullet — AS3's <c>param3</c>. This is the branch selector: a
        /// bullet throws the spray from its own contact point with velocity, a standing hit drops it
        /// straight from the target's mid-height.
        /// </summary>
        public readonly bool HasBullet;

        /// <summary>The round's contact point in AS3 room-local pixels — AS3 <c>param3.X</c>.</summary>
        public readonly float ImpactX;

        /// <inheritdoc cref="ImpactX"/>
        public readonly float ImpactY;

        /// <summary>
        /// The round's travel direction as a unit vector — AS3 <c>param3.dx / param3.vel</c>, which is
        /// what <c>DamageContext.KnockbackDir</c> holds. The spray's velocity override is this times
        /// <see cref="BloodSprayRules.BulletThrowScale"/>.
        /// </summary>
        public readonly float DirectionX;

        /// <inheritdoc cref="DirectionX"/>
        public readonly float DirectionY;

        /// <summary>
        /// The target's own position in AS3 room-local pixels — AS3 <c>X</c>. The anchor for the
        /// standing spray and for the blood explosion, and the origin the returned
        /// <see cref="ParticleEmit"/> offsets are measured from.
        /// </summary>
        public readonly float UnitX;

        /// <inheritdoc cref="UnitX"/>
        public readonly float UnitY;

        /// <summary>
        /// The target's authored sprite width in AS3 pixels — AS3 <c>scX</c>. Used only as the blood
        /// explosion's lateral jitter span (<c>(rand - 0.5) * scX * 0.5</c>).
        /// </summary>
        public readonly float SpriteWidth;

        /// <summary>The target's authored sprite height in AS3 pixels — AS3 <c>scY</c>.</summary>
        public readonly float SpriteHeight;

        public BloodSprayContext(
            BloodType blood,
            DamageType damageType,
            float damage,
            float mass,
            bool anyCritical,
            bool hasBullet,
            float impactX,
            float impactY,
            float directionX,
            float directionY,
            float unitX,
            float unitY,
            float spriteWidth,
            float spriteHeight)
        {
            Blood = blood;
            DamageType = damageType;
            Damage = damage;
            Mass = mass;
            AnyCritical = anyCritical;
            HasBullet = hasBullet;
            ImpactX = impactX;
            ImpactY = impactY;
            DirectionX = directionX;
            DirectionY = directionY;
            UnitX = unitX;
            UnitY = unitY;
            SpriteWidth = spriteWidth;
            SpriteHeight = spriteHeight;
        }
    }

    /// <summary>
    /// The port of AS3 <c>Unit.damage()</c>'s blood block (<c>fe/unit/Unit.as:3844-3901</c>) — what a
    /// bleeding target throws when something hits it.
    ///
    /// <para><b>Unity-free on purpose</b>, for the reason the whole <c>Systems/Particles</c> tree is:
    /// every input is an enum, a bool, a float or an RNG and the output is a list of ids and offsets,
    /// so the gates and both anchor branches can be pinned on a plain host. The method it came out of
    /// is a private member of a <c>MonoBehaviour</c> that no offline test can reach.</para>
    ///
    /// <para><b>The gate is two conditions and they are not the same kind of thing.</b>
    /// <c>this.blood &gt; 0</c> is a property of the <i>target</i>; the damage type test
    /// (<c>D_BUL</c>, <c>D_BLADE</c>, <c>D_PHIS</c>, <c>D_BLEED</c>, <c>D_FANG</c>) is a property of
    /// the <i>hit</i>. So fire, poison, plasma and everything else draws no blood at all, and the
    /// three blood colours map to three different emitters — <c>blood</c>, <c>gblood</c>,
    /// <c>pblood</c> — all of which are real rows in <c>AllData.as</c>.</para>
    ///
    /// <para><b>The two anchors, and why the emit carries an offset rather than a position.</b> A
    /// bullet throws the spray from where it stopped and gives it the round's own velocity
    /// (<c>:3865-3869</c>); a hit with no bullet drops it from the target's mid-height with no
    /// velocity at all (<c>:3873</c>). Those are two different anchor points for the same emitter, so
    /// the rule returns each emit as a delta from the unit — which is the majority anchor and the one
    /// the blood explosion uses unconditionally.</para>
    ///
    /// <para><b>The blood explosion is the gory one, and its probability is not a probability.</b>
    /// <c>param1 / 1000 &gt; _loc12_</c> compares the damage against a random draw, so a 300-damage
    /// hit explodes roughly 30% of the time and a 30-damage hit roughly 3%. Two things scale the draw
    /// down and therefore the odds up: a blade hit squares it (<c>:3878-3881</c>, so blades gib far
    /// more readily than bullets), and a crit multiplies it by <c>0.3</c> (<c>:3882-3885</c>). The
    /// direction the gib is thrown is the round's own travel sign, or a coin flip when there was no
    /// round — and that sign is also the sprite mirror, so a gib thrown left is drawn mirrored.</para>
    ///
    /// <para><b>Draw order is load-bearing</b> and is the oracle's. Within one call the draws are: the
    /// bullet-path count jitter, then the explosion's probability roll, then the no-bullet direction
    /// coin flip, then the variant, the lateral jitter and the drop. Reproducing them in any other
    /// order would hand every later draw a different number.</para>
    ///
    /// <para><b>Deliberately absent: the <c>World.w.alicorn</c> guard.</b> AS3 wraps all three
    /// emissions in <c>if(!(this.player &amp;&amp; World.w.alicorn))</c> (<c>:3861</c>). The port models
    /// no playable alicorn, and says so in one place — <c>ISpellWorld.Alicorn</c>, which
    /// <c>LiveSpellWorld</c> returns <c>false</c> from with the reasoning recorded there. So the guard
    /// is already satisfied on every path and reproducing the test would be reproducing a constant.
    /// When a playable alicorn lands, this is one of the call sites to revisit.</para>
    ///
    /// <para><b>Not ported here: the combat half of <c>blood == 0</c>.</b> The same field that
    /// suppresses the spray also makes a target <b>immune to bleed</b>
    /// (<c>Unit.as:1417-1420</c>), and the port models the EMP half of that block but not the bleed
    /// half. That is a damage-path defect rather than a visual, so it is recorded separately instead
    /// of being smuggled in through a particle rule.</para>
    /// </summary>
    public static class BloodSprayRules
    {
        /// <summary>The red-blood emitter — AS3 <c>Emitter.arr["blood"]</c> (<c>Unit.as:3850</c>).</summary>
        public const string RedSprayId = "blood";

        /// <summary>The green-blood emitter — AS3 <c>Emitter.arr["gblood"]</c> (<c>Unit.as:3854</c>).</summary>
        public const string GreenSprayId = "gblood";

        /// <summary>The pink-blood emitter — AS3 <c>Emitter.arr["pblood"]</c> (<c>Unit.as:3858</c>).</summary>
        public const string PinkSprayId = "pblood";

        /// <summary>
        /// The blood-explosion ids are <c>bloodexpl1</c>..<c>bloodexpl3</c> (<c>Unit.as:3897</c>);
        /// this is the prefix, and <see cref="ExplosionVariantCount"/> the number of rows.
        /// </summary>
        public const string ExplosionIdPrefix = "bloodexpl";

        /// <summary>AS3 <c>Math.floor(Math.random() * 3 + 1)</c> — three variants, 1-based.</summary>
        public const int ExplosionVariantCount = 3;

        /// <summary>AS3 <c>param3.dx / param3.vel * 5</c> — the spray is thrown at five times the round's unit direction.</summary>
        public const float BulletThrowScale = 5f;

        /// <summary>AS3 <c>Math.random() * 5</c> in the bullet-path count.</summary>
        public const float BulletKolRandomSpread = 5f;

        /// <summary>AS3 <c>param1 / 5</c> in the bullet-path count.</summary>
        public const float BulletKolDamageDivisor = 5f;

        /// <summary>AS3 <c>param1 / 3</c> in the standing-hit count.</summary>
        public const float StandingKolDamageDivisor = 3f;

        /// <summary>
        /// AS3 <c>massa &gt; 0.2</c> — the weight floor below which a red-blooded target does not gib.
        /// </summary>
        public const float ExplosionMassFloor = 0.2f;

        /// <summary>AS3 <c>param1 / 1000</c> — the damage side of the gib comparison.</summary>
        public const float ExplosionDamageDivisor = 1000f;

        /// <summary>AS3 <c>_loc12_ *= 0.3</c> on a crit (<c>:3884</c>).</summary>
        public const float CritExplosionDrawScale = 0.3f;

        /// <summary>AS3 <c>80 * _loc13_</c> — how far sideways the gib is thrown (<c>:3897</c>).</summary>
        public const float ExplosionThrowOffsetX = 80f;

        /// <summary>AS3 <c>- 40</c> at the end of the gib's Y (<c>:3897</c>; AS3 Y runs down, so this is upward).</summary>
        public const float ExplosionOffsetY = -40f;

        /// <summary>AS3 <c>(Math.random() - 0.5) * scX * 0.5</c> — lateral jitter half-span, times <c>scX</c>.</summary>
        public const float ExplosionLateralJitterScale = 0.5f;

        /// <summary>AS3 <c>Math.random() * scY * 0.5</c> — drop jitter span, times <c>scY</c>.</summary>
        public const float ExplosionDropJitterScale = 0.5f;

        /// <summary>
        /// Whether this damage type draws blood at all — AS3's
        /// <c>param2 == D_BUL || D_BLADE || D_PHIS || D_BLEED || D_FANG</c> (<c>Unit.as:3844</c>).
        /// </summary>
        /// <remarks>
        /// Note what is <i>not</i> in the list: explosions, fire, plasma, laser, acid, cryo, venom,
        /// poison, necrotic, psionic, astral, pink, EMP and internal damage. A grenade does not
        /// produce a blood spray, only its shrapnel does.
        /// </remarks>
        public static bool IsBleedingDamageType(DamageType damageType)
        {
            switch (damageType)
            {
                case DamageType.PhysicalBullet:
                case DamageType.Blade:
                case DamageType.PhysicalMelee:
                case DamageType.Bleed:
                case DamageType.Fang:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// The emitter id for a blood colour, or <c>null</c> when the value names no emitter.
        /// </summary>
        /// <remarks>
        /// AS3's three <c>if</c>s (<c>:3848-3859</c>) leave <c>bloodEmit</c> at <c>null</c> for any
        /// value outside 1..3 and the next line dereferences it — so a fourth blood colour is a crash
        /// in the oracle, not a silent fallback. The port returns <c>null</c> and
        /// <see cref="Plan"/> emits nothing, which is a deliberate divergence: a data error should
        /// not take the damage path down.
        /// </remarks>
        public static string SprayIdFor(BloodType blood)
        {
            switch (blood)
            {
                case BloodType.Red:   return RedSprayId;
                case BloodType.Green: return GreenSprayId;
                case BloodType.Pink:  return PinkSprayId;
                default:              return null;
            }
        }

        /// <summary>
        /// Builds the plan for one hit. Fills <paramref name="emits"/> (cleared first) with the spray
        /// and, when it procs, the blood explosion — each as an offset from the unit's own position.
        /// </summary>
        /// <param name="ctx">The hit's blood-relevant values. See <see cref="BloodSprayContext"/>.</param>
        /// <param name="rng">
        /// The stream for the four to six <c>Math.random()</c> calls the oracle makes. Pass the
        /// <b>presentation</b> stream, not the combat one: every draw here selects a particle, a
        /// count or a jitter and none of them feeds back into damage. Null is legal (an offline host)
        /// and yields each expression's minimum.
        /// </param>
        /// <param name="emits">Receives the emits. Never null.</param>
        /// <returns>True when at least one particle is emitted.</returns>
        public static bool Plan(in BloodSprayContext ctx, IRngService rng, List<ParticleEmit> emits)
        {
            if (emits == null) throw new ArgumentNullException(nameof(emits));

            emits.Clear();

            // ── The gate (Unit.as:3844) ────────────────────────────────────────────────────────────
            if (ctx.Blood == BloodType.None) return false;
            if (!IsBleedingDamageType(ctx.DamageType)) return false;

            // AS3's `if(param1 > 0)` at :3668 wraps this whole block — a hit fully eaten by armour
            // draws no blood, and neither does one that vulnerability zeroed.
            if (ctx.Damage <= 0f) return false;

            string sprayId = SprayIdFor(ctx.Blood);
            if (sprayId == null) return false;

            // ── The spray (Unit.as:3863-3874) ──────────────────────────────────────────────────────
            //
            // AS3 caches the Emitter in `this.bloodEmit` on first use (:3846-3860). That is an
            // allocation trick with no behavioural effect — the lookup key is the id either way — so
            // the port resolves by id per call and skips the cache.
            if (ctx.HasBullet)
            {
                // `Math.floor(Math.random() * 5 + param1 / 5)` — one draw, and the count can come out
                // zero, which AS3's `if(param4.kol)` treats as absent and ParticleRules.Cast turns
                // back into one particle (Emitter.as:171-175). Both halves are reproduced by passing
                // the raw floor through.
                int kol = rng != null
                    ? (int)Math.Floor(rng.NextFloat() * BulletKolRandomSpread + ctx.Damage / BulletKolDamageDivisor)
                    : (int)Math.Floor(ctx.Damage / BulletKolDamageDivisor);

                emits.Add(new ParticleEmit(
                    sprayId,
                    new ParticleSpec
                    {
                        DX = ctx.DirectionX * BulletThrowScale,
                        DY = ctx.DirectionY * BulletThrowScale,
                        Kol = kol,
                    },
                    // Anchored at the round's contact point, expressed as a delta from the unit.
                    ctx.ImpactX - ctx.UnitX,
                    ctx.ImpactY - ctx.UnitY));
            }
            else
            {
                // `this.bloodEmit.cast(loc, X, Y - scY / 2, {"kol": Math.floor(param1 / 3)})` — the
                // target's own mid-height, no velocity override, no jitter draw.
                emits.Add(new ParticleEmit(
                    sprayId,
                    new ParticleSpec { Kol = (int)Math.Floor(ctx.Damage / StandingKolDamageDivisor) },
                    0f,
                    -ctx.SpriteHeight * 0.5f));
            }

            AddExplosion(in ctx, rng, emits);

            return true;
        }

        /// <summary>
        /// AS3 <c>Unit.as:3875-3899</c> — the gib. Three gates, then a damage-versus-draw comparison.
        /// </summary>
        private static void AddExplosion(in BloodSprayContext ctx, IRngService rng, List<ParticleEmit> emits)
        {
            // `this.blood == 1 && param2 != D_BLEED && massa > 0.2`. Red blood only: a green or pink
            // creature spatters but never bursts, and neither does a bleed tick on a red one — a
            // wounded target does not gib once per tick from the wound itself.
            if (ctx.Blood != BloodType.Red) return;
            if (ctx.DamageType == DamageType.Bleed) return;
            if (ctx.Mass <= ExplosionMassFloor) return;

            // `_loc12_ = Math.random()`, squared for a blade, cut to 30% on a crit.
            float draw = rng != null ? rng.NextFloat() : 0f;
            if (ctx.DamageType == DamageType.Blade) draw *= draw;
            if (ctx.AnyCritical) draw *= CritExplosionDrawScale;

            // `param1 / 1000 > _loc12_`
            if (!(ctx.Damage / ExplosionDamageDivisor > draw)) return;

            // Which way it is thrown, and the sprite mirror that follows it (`:3888-3896`). A bullet
            // decides it from its own travel sign; with no bullet it is a coin flip, and that flip is
            // a real draw the oracle takes — so it is taken here too, before the variant.
            float throwSign = 1f;
            if (ctx.HasBullet)
            {
                if (ctx.DirectionX < 0f) throwSign = -1f;
            }
            else if (rng != null && rng.NextFloat() < 0.5f)
            {
                throwSign = -1f;
            }

            int variant = rng != null ? rng.Range(1, ExplosionVariantCount + 1) : 1;

            float lateral = rng != null
                ? (rng.NextFloat() - 0.5f) * ctx.SpriteWidth * ExplosionLateralJitterScale
                : 0f;
            float drop = rng != null ? rng.NextFloat() * ctx.SpriteHeight * ExplosionDropJitterScale : 0f;

            float x = ctx.UnitX + ExplosionThrowOffsetX * throwSign + lateral;
            float y = ctx.UnitY - drop + ExplosionOffsetY;

            emits.Add(new ParticleEmit(
                ExplosionIdPrefix + variant,
                new ParticleSpec { Mirr = throwSign < 0f },
                x - ctx.UnitX,
                y - ctx.UnitY));
        }
    }
}
