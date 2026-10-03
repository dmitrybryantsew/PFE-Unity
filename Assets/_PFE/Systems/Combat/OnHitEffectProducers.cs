using PFE.Data.Definitions;
using PFE.Systems.Effects;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// AS3's on-hit status-effect producers, extracted from <c>Unit.damage()</c> so they can be
    /// exercised without a live unit.
    ///
    /// <para><b>Three channels, and only one of them is what it looks like.</b> The oracle's block at
    /// <c>Unit.as:3769-3834</c> runs inside <c>damage()</c>, <i>after</i> the damage arithmetic and
    /// <i>before</i> the caller's <c>otbros()</c>. It has three separate producers:</para>
    /// <list type="number">
    /// <item><b><c>weap.dopEffect</c></b> — the weapon's own on-hit status, mapped from a short code
    /// to an effect id (and, for three of the eight, to a plain field instead — see
    /// <see cref="ApplyWeaponDop"/>).</item>
    /// <item><b><c>weap.ammoFire</c></b> — the loaded round burns. Always <c>burning</c>, with the
    /// ammo's <c>fire</c> attribute as the per-second damage (<c>:3830-3832</c>).</item>
    /// <item><b><c>contusion</c></b> — an explosive hit can stun regardless of the weapon's
    /// <c>dopEffect</c> (<c>:3763-3766</c>).</item>
    /// </list>
    ///
    /// <para><b>Why the order matters.</b> The three run in source order, and the damage-type
    /// susceptibility guard (<c>vulner[D_X] &gt; 0.1</c>) is evaluated per branch against the target's
    /// <i>current</i> table. Applying them out of order would not change the outcome for the data in
    /// this build (no producer here modifies vulnerability), but it is kept in order so a future
    /// vulnerability-writing effect cannot silently change which guard passes.</para>
    /// </summary>
    public static class OnHitEffectProducers
    {
        /// <summary>
        /// Run every on-hit producer for a landed hit.
        /// </summary>
        /// <param name="receiver">The target, or <c>null</c> for a target that carries no effects.</param>
        /// <param name="weaponDopEffect">AS3 <c>weap.dopEffect</c> — may be null/empty.</param>
        /// <param name="weaponDopDamage">AS3 <c>weap.dopDamage</c>.</param>
        /// <param name="weaponDopChance">AS3 <c>weap.dopCh</c> — <c>&gt;= 1</c> means certain.</param>
        /// <param name="dopChancePassed">
        /// The result of the <c>dopCh</c> roll for a chance below 1. Ignored — and the roll is treated
        /// as passed — when <paramref name="weaponDopChance"/> is <c>&gt;= 1</c>, because AS3 short-
        /// circuits that case (<c>dopCh &gt;= 1 || Math.random() &lt; dopCh</c>) and spends no draw.
        /// The draw itself is the caller's, so the combat RNG stream has exactly one owner: a roll
        /// taken here would be taken in a different order from the oracle's and would desynchronise
        /// every later draw in the tick.
        /// </param>
        /// <param name="ammoFireDamage">AS3 <c>weap.ammoFire</c> — the ammo's <c>fire</c> attribute.</param>
        /// <param name="damageType">The damage type of the hit that landed.</param>
        /// <param name="isExplosiveHit">
        /// <c>true</c> when the hit's damage type is <see cref="DamageType.Explosive"/>. The contusion
        /// branch is gated on the type, not on the weapon.
        /// </param>
        /// <param name="wouldDie">
        /// The contusion branch's <c>Math.random() &lt; param1 / maxhp</c> gate needs a draw. Passed in
        /// rather than rolled here so the RNG stream stays owned by the caller — the project's standing
        /// rule that a draw consumed in the wrong order desynchronises every later draw in the tick.
        /// <c>true</c> when the roll passed.
        /// </param>
        /// <param name="targetIsImmuneToContusion">
        /// <c>true</c> when the target is a robot, mech or <c>doop</c> — AS3's
        /// <c>!this.opt.robot &amp;&amp; !this.mech &amp;&amp; !this.doop</c> guard.
        /// </param>
        public static void Apply(
            IEffectReceiver receiver,
            string weaponDopEffect,
            float weaponDopDamage,
            float weaponDopChance,
            bool dopChancePassed,
            float ammoFireDamage,
            DamageType damageType,
            bool isExplosiveHit,
            bool wouldDie,
            bool targetIsImmuneToContusion)
        {
            if (receiver == null)
            {
                return;
            }

            ApplyWeaponDop(receiver, weaponDopEffect, weaponDopDamage, weaponDopChance, dopChancePassed);
            ApplyContusion(receiver, isExplosiveHit, wouldDie, targetIsImmuneToContusion);
            ApplyAmmoFire(receiver, ammoFireDamage);
        }

        /// <summary>
        /// The <c>dopEffect</c> channel — <c>Unit.as:3769-3827</c>.
        ///
        /// <para><b>Eight codes, and only five of them become effects.</b> The oracle's branches map
        /// the weapon's one-word <c>effect</c> attribute to an effect id, but three of the eight
        /// (<c>poison</c>, <c>cut</c>, <c>stun</c>) instead accumulate onto <b>plain unit fields</b>
        /// (<c>Unit.poison</c>, <c>Unit.cut</c>, <c>Unit.stun</c>). Those are not status effects in the
        /// oracle's model — they are counters with their own tick logic — so this method does
        /// <b>not</b> turn them into effects. It records them as unmapped so the gap is visible, which
        /// is the same discipline the param registry uses: a channel that is not ported is reported,
        /// never silently approximated.</para>
        ///
        /// <para>Measured census of <c>&lt;dop effect&gt;</c> in <c>AllData.as</c>: <c>igni</c> 19,
        /// <c>cut</c> 17, <c>blind</c> 15, <c>poison</c> 5, <c>ice</c> 3, <c>psy</c> 2,
        /// <c>acid</c> 2, <c>pink</c> 1 — and <b>16 <c>&lt;dop&gt;</c> rows with no <c>effect</c> at
        /// all</b>, which is why the null check is first rather than a fall-through to a default.</para>
        /// </summary>
        private static void ApplyWeaponDop(
            IEffectReceiver receiver,
            string dopEffect,
            float dopDamage,
            float dopChance,
            bool chancePassed)
        {
            if (string.IsNullOrEmpty(dopEffect))
            {
                return;
            }

            // The chance gate. AS3: `dopCh >= 1 || Math.random() < dopCh` (Unit.as:3771) — so a chance
            // of exactly 1 is CERTAIN and spends no draw. That is not a micro-optimisation: 62 of the
            // 80 rows carry ch='1', and a port that rolled anyway would shift the RNG stream for every
            // later hit in the tick.
            if (dopChance < 1f && !chancePassed)
            {
                return;
            }

            string effectId;
            DamageType susceptibility;
            switch (dopEffect)
            {
                case "igni":
                    effectId = "burning";
                    susceptibility = DamageType.Fire;
                    break;
                case "ice":
                    effectId = "freezing";
                    susceptibility = DamageType.Cryo;
                    break;
                case "blind":
                    effectId = "blindness";
                    susceptibility = DamageType.Laser;
                    break;
                case "acid":
                    effectId = "chemburn";
                    susceptibility = DamageType.Acid;
                    break;
                case "pink":
                    effectId = "pinkcloud";
                    susceptibility = DamageType.Pink;
                    break;

                // Not effects — plain fields the port has no home for yet. Reported, not approximated.
                case "poison":
                case "cut":
                case "stun":
                case "psy":
                    receiver.Effects.RecordUnmappedName("dopEffect:" + dopEffect);
                    return;

                default:
                    receiver.Effects.RecordUnmappedName("dopEffect:" + dopEffect);
                    return;
            }

            if (!receiver.IsSusceptibleTo(susceptibility))
            {
                return;
            }

            // `ice` and `blind` carry extra target-class guards in the oracle — `!this.mech` for both,
            // plus `!this.doop` for blindness (Unit.as:3780-3787). The port folds those into
            // IsSusceptibleTo, because "may I receive this effect" is the same question.
            receiver.Effects.AddEffect(effectId, dopDamage);
        }

        /// <summary>
        /// The contusion branch — <c>Unit.as:3763-3766</c>:
        /// <c>if(param2 == D_EXPL &amp;&amp; this.opt &amp;&amp; !this.opt.robot &amp;&amp; !this.mech &amp;&amp;
        /// !this.doop &amp;&amp; this.sost == 1 &amp;&amp; Math.random() &lt; param1 / this.maxhp)
        /// this.addEffect("contusion")</c>.
        ///
        /// <para><b>No payload.</b> <c>contusion</c> has no <c>val</c> — the effect id alone is the
        /// payload. It is also the one producer with a <i>probability</i> rather than a flat
        /// application, scaled by the hit's size relative to max HP, which is why the roll is a caller
        /// parameter.</para>
        /// </summary>
        private static void ApplyContusion(
            IEffectReceiver receiver,
            bool isExplosiveHit,
            bool wouldDie,
            bool targetIsImmune)
        {
            if (!isExplosiveHit || targetIsImmune || !wouldDie)
            {
                return;
            }

            receiver.Effects.AddEffect("contusion");
        }

        /// <summary>
        /// The <c>ammoFire</c> channel — <c>Unit.as:3830-3832</c>:
        /// <c>if(param3.weap.ammoFire) this.addEffect("burning", param3.weap.ammoFire)</c>.
        ///
        /// <para><b>Unconditional — no chance roll and no susceptibility guard.</b> Unlike the
        /// <c>dopEffect</c> <c>igni</c> branch, which tests <c>vulner[D_FIRE] &gt; 0.1</c>, this one
        /// just checks that the amount is non-zero. That asymmetry is the oracle's, and it means a
        /// fire-immune target can still be set burning by an incendiary round. Ported as-is.</para>
        ///
        /// <para>The value is the ammo's <c>fire</c> attribute, parsed into
        /// <c>AmmoDefinition.fireDamage</c> by <c>AmmoDataImporter</c>. This is the consumer that work
        /// left dangling.</para>
        /// </summary>
        private static void ApplyAmmoFire(IEffectReceiver receiver, float ammoFireDamage)
        {
            if (ammoFireDamage == 0f)
            {
                return;
            }

            receiver.Effects.AddEffect("burning", ammoFireDamage);
        }
    }
}
