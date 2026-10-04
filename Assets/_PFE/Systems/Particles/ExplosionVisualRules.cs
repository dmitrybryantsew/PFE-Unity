using System;
using System.Collections.Generic;
using PFE.Core.Rng;
using PFE.Data.Definitions;

namespace PFE.Systems.Particles
{
    /// <summary>
    /// The port of AS3 <c>Bullet.explVis()</c> (<c>weapon/Bullet.as:876-1005</c>) — a pure decision
    /// table: given the weapon's override, the round's damage type and whether the impact was in water,
    /// which particles are emitted and which sound is played.
    ///
    /// <para><b>Unity-free on purpose.</b> Every input is a string, an enum, a bool and an RNG, and the
    /// output is a list of ids — so the whole nine-arm table plus the override can be pinned by fixtures
    /// on a plain host. That matters more here than usual: the method it came out of is a private member
    /// of a <c>MonoBehaviour</c>, and its predecessor (the <c>D_EXPL</c>-only arm) shipped with no
    /// coverage at all, so the other eight arms would have been unreachable by any offline test.</para>
    ///
    /// <para><b>Two layers, in order.</b> A per-weapon override (<c>weap.visexpl</c>) comes first and,
    /// when set, ends the decision — AS3 gates only that first <c>if</c> on the override, so the entire
    /// damage-type chain runs <i>precisely when the override is empty</i>. Everything after is an
    /// <c>else if</c> on <c>tipDamage</c>, and there is deliberately <b>no default arm</b>: a round of
    /// any other type (<c>D_BUL</c>, <c>D_PHIS</c>, <c>D_BLADE</c>, <c>D_LASER</c> …) emits nothing at
    /// all. Falling through to the explosive visuals would make every bullet in the game spark.</para>
    ///
    /// <para><b>Every <c>expl_t</c> guard is absent on purpose.</b> <c>expl_t</c> counts down a cluster
    /// munition's sub-explosions (<c>Bullet.as:240-250</c>, set at <c>:690</c>), and the oracle gates
    /// the venom/pink sounds, the whole acid arm and the fire arm on <c>expl_t == 0</c> — "the final
    /// blast". The port models no cluster, so <c>expl_t</c> is always 0 and every one of those guards is
    /// already satisfied; reproducing the test would be reproducing a constant.</para>
    ///
    /// <para><b>Deliberately absent: <c>loc.budilo()</c></b>, the combat-AI noise alert
    /// (<c>Location.as:2861-2886</c> → <c>unit.alarma</c> → <c>setCel</c>). The port tracks no per-unit
    /// look-at (<c>DamageCalculator.cs:407</c>), so it is an unported AI workstream, not a visual.</para>
    ///
    /// <para><b>Deliberately absent: <c>explLiquid()</c></b> (<c>Bullet.as:1007-1035</c>), which the acid
    /// and fire arms call. It is not a burst: it scans every tile in the blast radius and emits one pool
    /// particle per <c>phis</c>/<c>shelf</c> surface with clear air above it, at that tile's own
    /// <c>phY1</c> height. That needs per-tile surface geometry the port's tile query does not expose,
    /// so it is its own workstream.</para>
    /// </summary>
    public static class ExplosionVisualRules
    {
        /// <summary>
        /// AS3 <c>weap.visexpl == "sparkle"</c> — the one override value that is an instruction rather
        /// than a particle id (<c>Bullet.as:880</c>): the ordinary blast visuals, but with
        /// <c>sparkleexpl</c> in place of the damage-type arm's second emitter.
        /// </summary>
        public const string Sparkle = "sparkle";

        /// <summary>The <c>{kol:16}</c> every spark burst in <c>explVis</c> passes.</summary>
        public const int SparkKol = 16;

        /// <summary>AS3 <c>{kol:30, rx:100, ry:100}</c> for the underwater bubble burst.</summary>
        public const int BubbleKol = 30;
        public const float BubbleRadius = 100f;

        /// <summary>AS3 emits <c>balefire</c> 60 px ABOVE the impact (<c>Y - 60</c>; AS3 Y runs down).</summary>
        public const float BalefireOffsetY = -60f;

        /// <summary>The acid blast's <c>kol</c> range: <c>Math.floor(Math.random()*5 + 30)</c>.</summary>
        public const int AcidKolMin = 30;
        public const int AcidKolMaxExclusive = 35;

        /// <summary>
        /// Builds the plan for one explosion. Fills <paramref name="emits"/> (cleared first) and returns
        /// the sound id in <paramref name="soundId"/> (<c>null</c> when the arm plays nothing).
        /// </summary>
        /// <param name="visExpl">The weapon's override, or null/empty for none.</param>
        /// <param name="damageType">The round's damage type; only consulted when the override is empty.</param>
        /// <param name="inWater">Whether the impact point was in water.</param>
        /// <param name="rng">
        /// The presentation stream for the acid arm's <c>kol</c> jitter. <b>Drawn only on the acid arm</b>,
        /// so a non-acid explosion does not consume a draw the oracle would not have made. Null is legal
        /// (an offline host) and yields the oracle's minimum.
        /// </param>
        /// <param name="emits">Receives the emits. Never null.</param>
        /// <param name="soundId">The sound id, or null.</param>
        /// <returns>True when at least one particle is emitted.</returns>
        public static bool Plan(string visExpl, DamageType damageType, bool inWater, IRngService rng,
                                List<ParticleEmit> emits, out string soundId)
        {
            if (emits == null) throw new ArgumentNullException(nameof(emits));

            emits.Clear();
            soundId = null;

            // ── 1. The per-weapon override, first and highest precedence (Bullet.as:878-909) ────────
            if (!string.IsNullOrEmpty(visExpl))
            {
                if (visExpl == Sparkle)
                {
                    // The underwater half is identical to the D_EXPL arm's (:882-894 vs :964-976).
                    if (inWater) AddUnderwaterBlast(emits, "expl_uw", out soundId);
                    else
                    {
                        emits.Add(new ParticleEmit("expl"));
                        emits.Add(new ParticleEmit("sparkleexpl"));
                        emits.Add(new ParticleEmit("iskr", SparkSpec()));
                        soundId = "bale_e";
                    }
                }
                else
                {
                    // Any other value names a particle id directly (ttexpl, ttplaexpl, react, eclipse).
                    // AS3 emits the id and nothing else — no second emitter, no sound.
                    emits.Add(new ParticleEmit(visExpl));
                }
                return emits.Count > 0;
            }

            // ── 2. The damage-type table (Bullet.as:910-1000) ──────────────────────────────────────
            switch (damageType)
            {
                case DamageType.EMP:
                    emits.Add(new ParticleEmit("impexpl"));
                    soundId = "emp_e";
                    break;

                case DamageType.Cryo:
                    emits.Add(new ParticleEmit("iceexpl"));
                    emits.Add(new ParticleEmit("snow", SparkSpec()));
                    soundId = "cryo_e";
                    break;

                case DamageType.Plasma:
                    emits.Add(new ParticleEmit("plaexpl"));
                    soundId = "exppla_e";
                    break;

                case DamageType.Venom:
                    // No inWater test in the oracle's gas arms — gas is emitted either way.
                    emits.Add(new ParticleEmit("gas"));
                    soundId = "gas_e";
                    break;

                case DamageType.Pink:
                    emits.Add(new ParticleEmit("pinkgas"));
                    soundId = "gas_e";
                    break;

                case DamageType.Acid:
                    emits.Add(new ParticleEmit("acidexpl"));
                    emits.Add(new ParticleEmit("acidkap", new ParticleSpec
                    {
                        Kol = rng != null ? rng.Range(AcidKolMin, AcidKolMaxExclusive) : AcidKolMin,
                    }));
                    soundId = "acid_e";
                    break;

                case DamageType.Balefire:
                    emits.Add(new ParticleEmit("balefire", null, 0f, BalefireOffsetY));
                    emits.Add(new ParticleEmit("baleblast"));
                    soundId = "bale_e";
                    break;

                case DamageType.Explosive:
                    if (inWater) AddUnderwaterBlast(emits, "expl_uw", out soundId);
                    else
                    {
                        emits.Add(new ParticleEmit("expl"));
                        emits.Add(new ParticleEmit("flare"));
                        emits.Add(new ParticleEmit("iskr", SparkSpec()));
                        soundId = "expl_e";
                    }
                    break;

                case DamageType.Fire:
                    // AS3 guards the fire arm on `inWater <= 0` as well as `expl_t == 0`: a fire blast
                    // that lands underwater does nothing at all, not even a hiss.
                    if (!inWater)
                    {
                        emits.Add(new ParticleEmit("fireexpl"));
                        emits.Add(new ParticleEmit("flare"));
                        emits.Add(new ParticleEmit("iskr", SparkSpec()));
                        soundId = "fire_e";
                    }
                    break;

                // No default arm — every other damage type is silent by design.
            }

            return emits.Count > 0;
        }

        private static void AddUnderwaterBlast(List<ParticleEmit> emits, string sound, out string soundId)
        {
            emits.Add(new ParticleEmit("explw"));
            emits.Add(new ParticleEmit("bubble", new ParticleSpec
            {
                Kol = BubbleKol, RX = BubbleRadius, RY = BubbleRadius,
            }));
            soundId = sound;
        }

        /// <summary>
        /// A fresh <c>{kol:16}</c> spec. A new instance per call rather than a shared one because
        /// <see cref="ParticleSpec"/> is a mutable class — one stray write to a shared instance would
        /// change every spark burst in the game.
        /// </summary>
        private static ParticleSpec SparkSpec() => new ParticleSpec { Kol = SparkKol };
    }
}
