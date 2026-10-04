using System;
using System.Collections.Generic;
using PFE.Data.Definitions;

namespace PFE.Systems.Particles
{
    /// <summary>
    /// Everything AS3's unit-hit impact dispatcher reads (<c>fe/unit/Unit.as:4168-4204</c>, the tail of
    /// <c>udarUnit</c>) — and nothing it does not.
    ///
    /// <para><b>Both actors are needed, which is what makes this block different from the blood
    /// spray.</b> The blood block hangs off the <i>target</i> alone. Here the anchor is a function of
    /// <b>two</b> units: the defender's own mid-height and the attacker's, averaged. So the attacker's
    /// position and sprite height are inputs, and a caller that has only the defender cannot produce a
    /// faithful emit.</para>
    ///
    /// <para><b>Positions are AS3 room-local pixels.</b> <c>X</c>/<c>Y</c> are the defender's origin and
    /// <c>Y</c> is its <b>feet</b> — <c>Unit.as:1875-1878</c> gives <c>Y1 = Y - scY</c> (top) and
    /// <c>Y2 = Y</c> (bottom), so the sprite spans <c>Y - scY .. Y</c>. Every "mid-height" below is
    /// therefore <c>Y - scY/2</c>, and the emitted offsets are measured from the defender's origin.</para>
    /// </summary>
    public readonly struct UnitHitImpactContext
    {
        /// <summary>
        /// The <b>attacker's</b> tip damage type — AS3 <c>param1.tipDamage</c>. This is the dispatcher's
        /// key, and note it is the <i>attacker's</i>, not the damage that landed: the visual follows what
        /// the weapon is made of, so a flaming sword shows a flesh hit if its tip damage is not fire.
        /// </summary>
        public readonly DamageType TipDamage;

        /// <summary>
        /// The attacker's raw damage — AS3 <c>param1.dam</c>. Read <b>only</b> to size the effect, and
        /// this is a divergence worth knowing: AS3 computes the scale from <c>param1.dam</c> even on the
        /// branch where the damage <i>dealt</i> is <c>currentWeapon.damage * 0.5 + param1.dam</c>
        /// (<c>:4162</c>). So a weapon-tip hit deals more damage than its effect is scaled for.
        /// </summary>
        public readonly float AttackerDamage;

        /// <summary>
        /// The per-hit damage variance roll — AS3 <c>_loc3_</c>, drawn at <c>:4150</c> as
        /// <c>Math.random() * 0.4 + 0.8</c> (so 0.8 .. 1.2).
        /// </summary>
        /// <remarks>
        /// <para><b>This is a COMBAT number, and it is an input rather than a draw on purpose.</b>
        /// <c>_loc3_</c> multiplies the damage actually dealt at <c>:4162/:4166</c> <i>before</i> the
        /// impact block reads it at <c>:4168</c>, so the effect's size is a function of a value the
        /// damage path already produced. Drawing it here would be a second, unsynchronised roll and the
        /// effect would disagree with the number over the target's head.</para>
        /// </remarks>
        public readonly float DamageVariance;

        /// <summary>The damage multiplier the collision was called with — AS3 <c>param2</c>.</summary>
        public readonly float DamageMultiplier;

        /// <summary>The attacker's origin — AS3 <c>param1.X</c>.</summary>
        public readonly float AttackerX;

        /// <summary>The attacker's origin — AS3 <c>param1.Y</c>, its feet.</summary>
        public readonly float AttackerY;

        /// <summary>The attacker's authored sprite height — AS3 <c>param1.scY</c>.</summary>
        public readonly float AttackerSpriteHeight;

        /// <summary>The defender's origin — AS3 <c>X</c>.</summary>
        public readonly float DefenderX;

        /// <summary>The defender's origin — AS3 <c>Y</c>, its feet.</summary>
        public readonly float DefenderY;

        /// <summary>The defender's authored sprite height — AS3 <c>scY</c>.</summary>
        public readonly float DefenderSpriteHeight;

        public UnitHitImpactContext(
            DamageType tipDamage,
            float attackerDamage,
            float damageVariance,
            float damageMultiplier,
            float attackerX,
            float attackerY,
            float attackerSpriteHeight,
            float defenderX,
            float defenderY,
            float defenderSpriteHeight)
        {
            TipDamage = tipDamage;
            AttackerDamage = attackerDamage;
            DamageVariance = damageVariance;
            DamageMultiplier = damageMultiplier;
            AttackerX = attackerX;
            AttackerY = attackerY;
            AttackerSpriteHeight = attackerSpriteHeight;
            DefenderX = defenderX;
            DefenderY = defenderY;
            DefenderSpriteHeight = defenderSpriteHeight;
        }
    }

    /// <summary>
    /// AS3's unit-hit impact dispatcher — <c>Unit.udarUnit</c>'s tail, <c>fe/unit/Unit.as:4168-4204</c>.
    ///
    /// <para><b>It is a five-way switch that emits exactly one part, every time.</b> Unlike the blood
    /// block there is no gate and no probability: a hit that reaches here always produces a visual. The
    /// switch is on the attacker's tip damage type and picks both a part id and a sound id — and the two
    /// do <b>not</b> have the same shape, which is why they are two functions rather than one lookup.</para>
    ///
    /// <para><b>The <c>moln</c> branch is the odd one out, in two ways at once.</b> Every other branch
    /// anchors at the midpoint between the two units' mid-heights and carries a <c>scale</c>; <c>moln</c>
    /// anchors at the <b>defender's own mid-height</b> and carries <c>celx</c>/<c>cely</c> — the
    /// attacker's mid-height — instead. That is not a quirk to smooth over: <c>moln</c> is the electric
    /// arc, so it is drawn <i>from</i> the defender <i>to</i> the attacker, whereas the others are a
    /// contact burst centred between them. It is also the only branch that carries no <c>scale</c>.</para>
    ///
    /// <para><b>Sound ids are carried, not played.</b> The five <c>Snd.ps</c> calls sit inside this same
    /// switch, so they belong to this oracle block; the port's positional-sound seam
    /// (<c>ISoundService.Play(id, worldPos, vol)</c>) is a separate concern and is not called here.</para>
    /// </summary>
    public static class UnitHitImpactRules
    {
        /// <summary>The electric arc, for <c>D_SPARK</c> — AS3 <c>:4179</c>.</summary>
        public const string SparkId = "moln";

        /// <summary>For <c>D_ACID</c> — AS3 <c>:4187</c>.</summary>
        public const string AcidId = "buma";

        /// <summary>For <c>D_NECRO</c> — AS3 <c>:4192</c>.</summary>
        public const string NecroId = "bumn";

        /// <summary>
        /// The flesh burst, for <c>D_FANG</c> <b>and</b> for everything else — AS3 <c>:4197</c> and
        /// <c>:4202</c>, which are the same call with a different sound. A fang hit is a flesh hit with
        /// its own noise.
        /// </summary>
        public const string FleshId = "bum";

        /// <summary>The sound for the <c>D_SPARK</c> branch — AS3 <c>:4183</c>.</summary>
        public const string SparkSoundId = "electro";

        /// <inheritdoc cref="AcidId"/>
        public const string AcidSoundId = "acid";

        /// <inheritdoc cref="NecroId"/>
        public const string NecroSoundId = "hit_necr";

        /// <summary>The sound for the <c>D_FANG</c> branch — AS3 <c>:4198</c>.</summary>
        public const string FangSoundId = "fang_hit";

        /// <summary>The sound for the fall-through branch — AS3 <c>:4203</c>.</summary>
        public const string FleshSoundId = "hit_flesh";

        /// <summary>The divisor in <c>_loc4_ = param1.dam * _loc3_ * param2 / 20</c> — AS3 <c>:4168</c>.</summary>
        public const float ScaleDamageDivisor = 20f;

        /// <summary>The lower clamp on the scale — AS3 <c>:4169-4172</c>.</summary>
        public const float ScaleFloor = 0.5f;

        /// <summary>The upper clamp on the scale — AS3 <c>:4173-4176</c>.</summary>
        public const float ScaleCeiling = 3f;

        /// <summary>
        /// The part id for an attacker's tip damage type. Four ids for five branches: <c>D_FANG</c> and
        /// the fall-through share <see cref="FleshId"/>.
        /// </summary>
        public static string IdFor(DamageType tipDamage)
        {
            switch (tipDamage)
            {
                case DamageType.Spark:    return SparkId;
                case DamageType.Acid:     return AcidId;
                case DamageType.Necrotic: return NecroId;
                default:                  return FleshId;
            }
        }

        /// <summary>
        /// The sound id for an attacker's tip damage type. <b>Five ids for five branches</b> — so this is
        /// deliberately not <see cref="IdFor"/>: <c>D_FANG</c> and the fall-through share a part but not
        /// a sound, and collapsing the two would lose the fang's own noise.
        /// </summary>
        public static string SoundIdFor(DamageType tipDamage)
        {
            switch (tipDamage)
            {
                case DamageType.Spark:    return SparkSoundId;
                case DamageType.Acid:     return AcidSoundId;
                case DamageType.Necrotic: return NecroSoundId;
                case DamageType.Fang:     return FangSoundId;
                default:                  return FleshSoundId;
            }
        }

        /// <summary>
        /// The effect's size — AS3 <c>_loc4_</c>, <c>:4168-4176</c>:
        /// <c>param1.dam * _loc3_ * param2 / 20</c>, clamped into 0.5 .. 3.
        /// </summary>
        public static float ScaleFor(in UnitHitImpactContext ctx)
        {
            float scale = ctx.AttackerDamage * ctx.DamageVariance * ctx.DamageMultiplier
                          / ScaleDamageDivisor;

            if (scale < ScaleFloor) return ScaleFloor;
            if (scale > ScaleCeiling) return ScaleCeiling;
            return scale;
        }

        /// <summary>
        /// The one emit this hit produces. Offsets are measured from the <b>defender's</b> origin, which
        /// is the anchor the caller supplies.
        /// </summary>
        /// <remarks>
        /// A single <see cref="ParticleEmit"/> rather than a list and a <c>bool</c>: every branch of the
        /// oracle's switch emits, so "did it emit" has no answer that is ever <c>false</c> and a list
        /// would only invite a caller to handle a case that cannot happen.
        /// </remarks>
        public static ParticleEmit Plan(in UnitHitImpactContext ctx)
        {
            float defenderMidY = ctx.DefenderY - ctx.DefenderSpriteHeight * 0.5f;
            float attackerMidY = ctx.AttackerY - ctx.AttackerSpriteHeight * 0.5f;

            if (ctx.TipDamage == DamageType.Spark)
            {
                // Emitter.emit("moln", loc, X, Y - scY / 2, {celx: param1.X, cely: param1.Y - param1.scY / 2})
                // Anchored at the DEFENDER's mid-height; the attacker's is carried as the part's target,
                // because the arc is drawn between them. No scale.
                return new ParticleEmit(
                    SparkId,
                    new ParticleSpec { CelX = ctx.AttackerX, CelY = attackerMidY },
                    0f,
                    -ctx.DefenderSpriteHeight * 0.5f);
            }

            // Every other branch shares one anchor — the midpoint of the two mid-heights — and a scale:
            //   Emitter.emit(<id>, loc, (X + param1.X) / 2,
            //                (Y - scY / 2 + param1.Y - param1.scY / 2) / 2, {scale: _loc4_})
            float midX = (ctx.DefenderX + ctx.AttackerX) * 0.5f;
            float midY = (defenderMidY + attackerMidY) * 0.5f;

            return new ParticleEmit(
                IdFor(ctx.TipDamage),
                new ParticleSpec { Scale = ScaleFor(in ctx) },
                midX - ctx.DefenderX,
                midY - ctx.DefenderY);
        }
    }
}
