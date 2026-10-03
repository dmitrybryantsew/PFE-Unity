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
    /// (<c>flame</c>/<c>kap</c>/<c>blind</c>/<c>poison</c>/<c>slow</c>) are <b>visual</b> and the port
    /// has no effect visual layer, so they are recorded as unmapped on the set rather than silently
    /// dropped — the same discipline the param registry uses. That keeps the gap visible in the
    /// debug readback instead of pretending a burning unit is fully ported.</para>
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
                case "burning":
                    owner.Damage(effect.Value);
                    break;

                // AS3 `:423-426` — pink cloud, D_PINK.
                case "pinkcloud":
                    owner.Damage(effect.Value);
                    break;

                // AS3 `:433-436` — acid burn, D_ACID.
                case "chemburn":
                    owner.Damage(effect.Value);
                    break;

                // AS3 `:438-442` — poison, but ONLY above level 3. `checkT` is what raises the level,
                // and only `drunk` carries lvl1..3, so this is reachable for that effect alone.
                case "drunk":
                    if (effect.Level > 3)
                    {
                        owner.Damage(effect.Value);
                    }
                    break;

                // AS3 `:398-408` — hydra heals. `owner.heal(val)` then, for the player, three more
                // organ heals. The organ half needs `CharacterStats`, which this class must not name,
                // so it is the player bootstrap's job (via the payload sink below).
                case "hydra":
                    owner.Heal(effect.Value);
                    break;

                // AS3 `:448-459` — inhibitor slows nearby enemies within val^2 distance. Needs the room
                // and the unit list; reported rather than approximated.
                case "inhibitor":
                // AS3 `:428-431` (blindness), `:444-447` (namok), `:461-467` (fetter) — emitter-only.
                case "blindness":
                case "namok":
                case "fetter":
                    owner.Effects.RecordUnmappedName("payload:" + effect.Id);
                    break;

                // An id with no payload is the normal case — most effects are stat writes only.
                default:
                    break;
            }

            // The player-only extension, when one is installed. See EffectPayloadSink.
            owner.EffectPayloadSink?.Invoke(effect);
        }
    }
}
