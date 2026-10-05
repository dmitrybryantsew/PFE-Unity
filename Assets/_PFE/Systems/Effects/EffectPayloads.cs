using PFE.Data.Definitions;
using PFE.Entities.Units;

namespace PFE.Systems.Effects
{
    /// <summary>
    /// The per-second payload an effect runs — the port of AS3 <c>Effect.secEffect</c>
    /// (<c>Effect.as:405-472</c>).
    ///
    /// <para><b>Why a dispatcher and not a table.</b> The oracle's payload set is a hardcoded list of
    /// ids with bespoke behaviour: <c>burning</c> deals fire damage and sets <c>shok = 33</c>,
    /// <c>pinkcloud</c> pink, <c>chemburn</c> acid, <c>drunk</c> above level 3 poison, <c>hydra</c>
    /// heals, <c>inhibitor</c> slows nearby enemies, and <c>namok</c>/<c>blindness</c>/<c>fetter</c>
    /// are emitter-only. There is <b>no</b> data-driven payload table in the oracle to port —
    /// <c>&lt;eff&gt;</c> carries no "what does this do each second" attribute — so inventing one would
    /// be a divergence, not a generalisation.</para>
    ///
    /// <para><b>What is ported and what is not.</b> The <i>damage</i> and <i>heal</i> payloads are:
    /// they are the ones the effect system's consumers can act on today. The emitter payloads
    /// (<c>flame</c>/<c>kap</c>/<c>blind</c>/<c>poison</c>/<c>slow</c>) are <b>visual</b> and are
    /// forwarded to <see cref="UnitStats.EffectVisualSink"/> when a unit has one; when it does not, they
    /// are recorded as unmapped on the set rather than silently dropped — the same discipline the param
    /// registry uses. That keeps the gap visible in the debug readback instead of pretending a burning
    /// unit is fully ported. <c>inhibitor</c> is the one payload that is neither: it is a
    /// <i>simulation</i> effect (it slows every nearby enemy) and is still unported, so it stays
    /// recorded even when a visual sink is installed.</para>
    /// </summary>
    public static class EffectPayloads
    {
        /// <summary>
        /// Run <paramref name="effect"/>'s payload against its owner, if the id has one.
        ///
        /// <para>Covers the damage and heal payloads. Emitter-only ids are named here so the caller can
        /// see them handled deliberately, and are reported as unmapped rather than run.</para>
        /// </summary>
        public static void Run(ActiveEffect effect, UnitStats owner)
        {
            if (effect == null || owner == null)
            {
                return;
            }

            switch (effect.Id)
            {
                // AS3 `:411-421` — the burning payload. `owner.damage(val, D_FIRE, null, true)`; the
                // fourth argument marks it as effect damage, which skips the hit-avoidance roll
                // (`Unit.damage():3540`, `param4`). `shok = 33` is a visual flinch, not modelled.
                //
                // ⚠ The water half of the oracle's branch is NOT here and must not be added here.
                // `Effect.as:411-414` extinguishes a burning unit that is `isPlav` (`this.t = 1`), and
                // that is a change to the EFFECT, not to its payload — it belongs in the tick, where
                // the duration lives. See the note on EffectVisualContext.IsFloating.
                case "burning":
                    ApplyDamage(owner, effect, effect.Value, DamageType.Fire);
                    break;

                // AS3 `:423-426` — pink cloud, D_PINK.
                case "pinkcloud":
                    ApplyDamage(owner, effect, effect.Value, DamageType.Pink);
                    break;

                // AS3 `:433-436` — acid burn, D_ACID.
                case "chemburn":
                    ApplyDamage(owner, effect, effect.Value, DamageType.Acid);
                    break;

                // AS3 `:438-442` — poison, but ONLY above level 3. `checkT` is what raises the level,
                // and only `drunk` carries lvl1..3, so this is reachable for that effect alone.
                case "drunk":
                    if (effect.Level > 3)
                    {
                        ApplyDamage(owner, effect, effect.Value, DamageType.Poison);
                    }
                    break;

                // AS3 `:398-408` — hydra heals. `owner.heal(val)` then, for the player, three more
                // organ heals. The organ half needs `CharacterStats`, which this class must not name,
                // so it is the player bootstrap's job (via the payload sink below).
                case "hydra":
                    owner.Heal(effect.Value);
                    break;

                // AS3 `:448-459` — inhibitor slows nearby enemies within val^2 distance. Needs the room
                // and the unit list; reported rather than approximated. This is NOT an emitter, so it is
                // still unmapped even with a visual sink installed.
                case "inhibitor":
                    owner.Effects.RecordUnmappedName("payload:" + effect.Id);
                    break;

                // AS3 `:428-431` (blindness), `:444-447` (namok), `:461-467` (fetter) — emitter-only,
                // and now emitted by the visual sink below. Recorded as unmapped only when no sink is
                // installed, so a unit with no presentation layer still shows the gap in the readback
                // instead of looking fully ported.
                case "blindness":
                case "namok":
                case "fetter":
                    if (owner.EffectVisualSink == null)
                    {
                        owner.Effects.RecordUnmappedName("payload:" + effect.Id);
                    }
                    break;

                // An id with no payload is the normal case — most effects are stat writes only.
                default:
                    break;
            }

            // The player-only extension, when one is installed. See EffectPayloadSink.
            owner.EffectPayloadSink?.Invoke(effect);

            // The effect-driven visuals for this payload tick — AS3 `secEffect`'s `Emitter.emit` calls
            // (`:429` blind, `:439` poison, `:445` kap, `:470` slow). Unconditional, because the rules
            // return false for every id that emits nothing here and the alternative is a second copy of
            // the id list at the call site.
            owner.EffectVisualSink?.OnEffectPayloadVisual(effect);
        }

        /// <summary>
        /// Hand an effect's payload damage to the unit's damage authority, or fall back to the raw
        /// subtraction and <b>record</b> that it did.
        ///
        /// <para><b>The whole point of routing it.</b> AS3's payload calls <c>owner.damage(val, type,
        /// null, true)</c> — the full <c>Unit.damage()</c> pipeline, with <c>param3 == null</c> meaning
        /// "no bullet" (no crit, no piercing, no armour multiplier, no knockback, no penetration) and
        /// <c>param4 == true</c> meaning "skip the hit-avoidance roll". Calling
        /// <see cref="UnitStats.Damage"/> instead — which is the pipeline's <i>receiving</i> end — drops
        /// vulnerability, armour and <c>skin</c>, the floating damage number, and the death check.</para>
        ///
        /// <para><b>The fallback is recorded, not silent.</b> A unit with no sink is a bare
        /// <c>UnitStats</c> in an offline fixture, and the pre-fix behaviour is the only thing available
        /// to it. Naming that in the effect set's unmapped readback keeps it distinguishable from a
        /// correctly-routed payload — the project's standing rule for an unported path.</para>
        /// </summary>
        private static void ApplyDamage(UnitStats owner, ActiveEffect effect, float amount, DamageType type)
        {
            if (owner.EffectDamageSink != null)
            {
                owner.EffectDamageSink(amount, type);
                return;
            }

            owner.Damage(amount);
            owner.Effects?.RecordUnmappedName("payload-damage-raw:" + effect.Id);
        }
    }
}
