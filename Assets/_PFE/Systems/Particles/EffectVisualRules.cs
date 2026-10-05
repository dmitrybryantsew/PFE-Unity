using System;
using System.Collections.Generic;
using PFE.Core.Rng;

namespace PFE.Systems.Particles
{
    /// <summary>
    /// Everything AS3's <c>Effect</c> reads to decide what to draw — <c>fe/unit/Effect.as</c> — and
    /// nothing it does not.
    ///
    /// <para><b>This is the effect-driven half of the emitter, and it is a different family from the
    /// impact-driven one.</b> Blood, explosions and the unit-hit bursts are driven by <i>damage</i> and
    /// run once, at the moment of contact. These are driven by a <b>status effect ticking on a unit</b>
    /// and repeat for as long as it lasts. So the anchor is always the owner's own body, there is no
    /// second actor anywhere in this file, and the caller is the effect tick rather than a damage
    /// resolve.</para>
    ///
    /// <para><b>Positions are AS3 room-local pixels and <c>Y</c> is the unit's FEET.</b> Same convention
    /// as the rest of the particle system: <c>Unit.as:1875-1878</c> gives the sprite spanning
    /// <c>Y - scY .. Y</c>, so a "mid-height" is <c>Y - scY/2</c>. Every offset below is measured from
    /// the owner's origin, which is the anchor the caller supplies.</para>
    ///
    /// <para><b>Two entry points, because the oracle has two methods with different cadences.</b>
    /// <c>stepEffect()</c> (<c>:474-488</c>) runs <b>every tick</b> and <c>secEffect()</c>
    /// (<c>:405-472</c>) runs <b>once per 30 canonical frames</b>, gated by <c>t % 30 == 0</c> in
    /// <c>step()</c> (<c>:492</c>). Collapsing them into one call would either draw the flame 30× too
    /// rarely or run the blindness scatter 30× too often, and neither would look like a bug — a flame
    /// that stutters and a flicker that is merely busy are both plausible.</para>
    /// </summary>
    public readonly struct EffectVisualContext
    {
        /// <summary>AS3 <c>Effect.id</c>. The dispatcher's key on both paths.</summary>
        public readonly string EffectId;

        /// <summary>
        /// AS3 <c>Effect.lvl</c> — the escalation level. Read by exactly one rule here: <c>drunk</c>
        /// only emits while poisoned past level 3 (<c>:436</c>).
        /// </summary>
        public readonly int Level;

        /// <summary>
        /// AS3 <c>Effect.player</c> — whether the owner is the player. Read by the <c>blindness</c> rule
        /// (<c>:425</c>), and it is not a convenience: the oracle's blind burst is a full-screen scatter
        /// that only makes sense over the player's view.
        /// </summary>
        public readonly bool IsPlayer;

        /// <summary>
        /// AS3 <c>sost &lt; 4</c> — the unit is still in the world, as opposed to fully removed.
        ///
        /// <para><b>The port has no <c>sost</c>, so the caller supplies the predicate.</b> Three of the
        /// four payload rules test it (<c>burning</c>, <c>blindness</c>, <c>namok</c>). Do not pass
        /// <c>IDamageable.IsAlive</c> without thinking: AS3 still draws a <i>dying</i> unit
        /// (<c>sost == 3</c>, the 5 death assignments at <c>Unit.as</c>) and only stops at
        /// <c>sost == 4</c>, so <c>IsAlive</c> stops the flame one state too early.</para>
        /// </summary>
        public readonly bool IsInWorld;

        /// <summary>
        /// AS3 <c>Unit.isPlav</c> — the unit's <b>upper body</b> is submerged (<c>checkWater</c>,
        /// <c>Unit.as:2627-2641</c>: it samples the tile at <c>Y - scY*0.75</c>). The <c>namok</c>
        /// (soaked) drip is emitted only when this is <b>false</b> — a unit standing in deep water is
        /// already surrounded by it, so the droplet is for the wet-but-not-submerged case.
        /// </summary>
        public readonly bool IsFloating;

        /// <summary>The owner's origin — AS3 <c>X</c>.</summary>
        public readonly float X;

        /// <summary>The owner's origin — AS3 <c>Y</c>, its feet.</summary>
        public readonly float Y;

        /// <summary>The owner's authored sprite height — AS3 <c>scY</c>.</summary>
        public readonly float SpriteHeight;

        /// <summary>
        /// AS3 <c>storona</c> — the unit's facing as a sign, <c>+1</c> or <c>-1</c>
        /// (<c>Unit.as:602-611</c>). Only the <c>drunk</c> drip reads it, to hang the droplet off the
        /// side the unit is facing.
        /// </summary>
        public readonly float Facing;

        /// <summary>
        /// AS3 <c>(owner as UnitPlayer).fetX</c> — the <c>fetter</c> ring's own position, which is
        /// <b>not</b> the owner's. Only read by the <c>fetter</c> rule.
        /// </summary>
        /// <remarks>
        /// <c>fetter</c> is the one rule here whose anchor is elsewhere, and the one that is
        /// <b>player-only by construction</b>: <c>fetX</c>/<c>fetY</c> are declared on
        /// <c>UnitPlayer</c> (<c>UnitPlayer.as:117-119</c>), so the oracle's cast
        /// <c>(this.owner as UnitPlayer)</c> yields <c>null</c> for any other owner and the following
        /// <c>null.fetX</c> <b>throws</b> — a non-player owner cannot reach the emit at all. The port
        /// cannot reproduce a crash, so the <c>fetter</c> arm tests <see cref="IsPlayer"/> instead; see
        /// the note there.
        /// </remarks>
        public readonly float FetterX;

        /// <inheritdoc cref="FetterX"/>
        public readonly float FetterY;

        public EffectVisualContext(
            string effectId,
            int level,
            bool isPlayer,
            bool isInWorld,
            bool isFloating,
            float x,
            float y,
            float spriteHeight,
            float facing,
            float fetterX,
            float fetterY)
        {
            EffectId = effectId;
            Level = level;
            IsPlayer = isPlayer;
            IsInWorld = isInWorld;
            IsFloating = isFloating;
            X = x;
            Y = y;
            SpriteHeight = spriteHeight;
            Facing = facing;
            FetterX = fetterX;
            FetterY = fetterY;
        }
    }

    /// <summary>
    /// The particle half of AS3's effect visuals — the <c>Emitter.emit</c> calls in
    /// <c>Effect.stepEffect()</c> (<c>:474-488</c>) and <c>Effect.secEffect()</c> (<c>:405-472</c>).
    ///
    /// <para><b>Unity-free on purpose</b>, like its siblings <see cref="ExplosionVisualRules"/> and
    /// <see cref="BloodSprayRules"/>: every input is a string, an int, three bools and four floats, and
    /// the output is a list of ids. The methods they came out of are members of an AS3 class that the
    /// port reaches only through a live unit, so without the split none of this would be reachable by an
    /// offline fixture.</para>
    ///
    /// <para><b>Not here, deliberately.</b> <c>Effect.as</c> is mostly <i>not</i> particles, and the
    /// non-particle halves belong to other layers:
    /// <list type="bullet">
    /// <item><description><b>The <c>freezing</c> colour transform</b> (<c>visEff:271-279</c>) is a
    /// <c>ColorTransform</c> on the owner's sprite, not an emit — a sprite-tint concern.</description></item>
    /// <item><description><b>The <c>inhibitor</c> overlay</b> (<c>visEff:263-270</c>) shows and plays
    /// <c>vis.inh</c>, a clip on the character — and it is the same overlay the spell workstream calls
    /// its W2 <c>inh</c> case, so it lands with that.</description></item>
    /// <item><description><b><c>sacrifice</c></b> (<c>stepEffect:483-487</c>) calls
    /// <c>owner.newPart("blood",50)</c> — <c>Unit.newPart</c>, not <c>Emitter.emit</c>, so it is a
    /// different emitter seam.</description></item>
    /// <item><description><b>The <c>potion_*</c> / <c>stealth</c> / <c>reanim</c> filter flags</b> are
    /// player filter state, not particles.</description></item>
    /// </list>
    /// </para>
    /// </summary>
    public static class EffectVisualRules
    {
        /// <summary>AS3 <c>Effect.id == "burning"</c> — the burning unit's flame, <c>:476-482</c>.</summary>
        public const string BurningId = "burning";

        /// <summary>AS3 <c>Effect.id == "blindness"</c> — the blind scatter, <c>:425-431</c>.</summary>
        public const string BlindnessId = "blindness";

        /// <summary>AS3 <c>Effect.id == "drunk"</c> — the poison drip past level 3, <c>:436-440</c>.</summary>
        public const string DrunkId = "drunk";

        /// <summary>AS3 <c>Effect.id == "namok"</c> — the soaked drip, <c>:441-447</c>.</summary>
        public const string NamokId = "namok";

        /// <summary>AS3 <c>Effect.id == "fetter"</c> — the slow ring, <c>:468-471</c>.</summary>
        public const string FetterId = "fetter";

        /// <summary>The flame, for <c>burning</c> — AS3 <c>:480</c>.</summary>
        public const string FlameId = "flame";

        /// <summary>For <c>blindness</c> — AS3 <c>:429</c>.</summary>
        public const string BlindId = "blind";

        /// <summary>For <c>drunk</c> — AS3 <c>:439</c>.</summary>
        public const string PoisonId = "poison";

        /// <summary>For <c>namok</c> — AS3 <c>:445</c>.</summary>
        public const string KapId = "kap";

        /// <summary>For <c>fetter</c> — AS3 <c>:470</c>.</summary>
        public const string SlowId = "slow";

        /// <summary>
        /// The level <c>drunk</c> must <b>exceed</b> to drip — AS3 <c>this.lvl &gt; 3</c> (<c>:436</c>).
        /// Strictly greater: level 3 is sober enough to show nothing.
        /// </summary>
        public const int DrunkPoisonLevel = 3;

        /// <summary>
        /// The blind scatter's horizontal half-width — AS3 <c>-300 + Math.random() * 600</c>
        /// (<c>:429</c>), i.e. the half-open interval <c>[-300, 300)</c>.
        /// </summary>
        public const float BlindScatterX = 300f;

        /// <summary>
        /// The blind scatter's vertical half-height — AS3 <c>-200 + Math.random() * 400</c>
        /// (<c>:429</c>), i.e. <c>[-200, 200)</c>.
        /// </summary>
        public const float BlindScatterY = 200f;

        /// <summary>The <c>drunk</c> drip's facing offset — AS3 <c>storona * 20</c> (<c>:439</c>).</summary>
        public const float PoisonFacingOffsetX = 20f;

        /// <summary>The <c>drunk</c> drip's height — AS3 <c>Y - 40</c> (<c>:439</c>), a flat offset.</summary>
        public const float PoisonOffsetY = -40f;

        /// <summary>
        /// Where the <c>namok</c> droplet hangs — AS3 <c>Y - scY * 0.25</c> (<c>:445</c>), a quarter of
        /// the way up the body.
        /// </summary>
        public const float KapHeightFraction = 0.25f;

        /// <summary>
        /// The <c>{md:0.1}</c> the <c>namok</c> droplet passes (<c>:445</c>). <c>md</c> scales the
        /// part's <c>dx</c>/<c>dy</c> (<c>Emitter.as:254-257</c>), so this is the drip falling at a
        /// tenth of the row's velocity.
        /// </summary>
        public const float KapVelocityScale = 0.1f;

        /// <summary>
        /// The once-per-tick visuals — the port of <c>Effect.stepEffect()</c> (<c>:474-488</c>).
        /// Fills <paramref name="emits"/> (cleared first) and returns whether anything was emitted.
        /// </summary>
        /// <remarks>
        /// <b>Only <c>burning</c> lives here.</b> The oracle's method has two <c>if</c>s and the second
        /// (<c>sacrifice</c>, <c>:483</c>) emits no particle — it damages the owner and calls
        /// <c>newPart</c> — so the flame is the whole of this path.
        /// </remarks>
        public static bool PlanStepVisual(in EffectVisualContext ctx, List<ParticleEmit> emits)
        {
            if (emits == null) throw new ArgumentNullException(nameof(emits));

            emits.Clear();

            if (ctx.EffectId != BurningId || !ctx.IsInWorld)
            {
                return false;
            }

            // Emitter.emit("flame", loc, X, Y - scY / 2)  — :480, the body's mid-height.
            emits.Add(new ParticleEmit(FlameId, null, 0f, -ctx.SpriteHeight * 0.5f));
            return true;
        }

        /// <summary>
        /// The once-per-second visuals — the port of <c>Effect.secEffect()</c> (<c>:405-472</c>).
        /// Fills <paramref name="emits"/> (cleared first) and returns whether anything was emitted.
        /// </summary>
        /// <param name="rng">
        /// The presentation stream, for the blind scatter's two draws. <b>Drawn only on the blindness
        /// arm</b>, so a burning or fettered unit does not consume a draw the oracle would not have
        /// made. Null is legal (an offline host) and yields the interval's lower bound.
        /// </param>
        /// <remarks>
        /// <para><b>Four ids, and the conditions are not uniform.</b> <c>blindness</c> needs the owner to
        /// be the player <i>and</i> in the world; <c>drunk</c> needs only the level; <c>namok</c> needs
        /// the owner <b>not</b> to be submerged; <c>fetter</c> needs the owner to be the player — the
        /// port's stand-in for an oracle cast that would otherwise throw. Each arm carries its own gate
        /// because the oracle's do; applying one arm's gate to another would silently stop a drip for
        /// monsters or for dying units, and both read as plausible.</para>
        ///
        /// <para><b>These are separate <c>if</c>s in the oracle, not a chain</b> — but that is not
        /// observable, since an effect has exactly one id. The one place it would matter is the draw
        /// count, and only one arm draws.</para>
        /// </remarks>
        public static bool PlanPayloadVisual(in EffectVisualContext ctx, IRngService rng,
                                            List<ParticleEmit> emits)
        {
            if (emits == null) throw new ArgumentNullException(nameof(emits));

            emits.Clear();

            switch (ctx.EffectId)
            {
                case BlindnessId:
                    // `if(this.id == "blindness" && this.player)` then `if(owner.sost < 4)` (:425-431).
                    if (!ctx.IsPlayer || !ctx.IsInWorld)
                    {
                        return false;
                    }

                    // Emitter.emit("blind", loc, X - 300 + rand*600, Y - 200 + rand*400)
                    // The scatter is over the OWNER's neighbourhood, not the screen: 300 px each way
                    // horizontally, 200 vertically, which is roughly a room cell either side.
                    float blindX = rng != null ? rng.Range(-BlindScatterX, BlindScatterX) : -BlindScatterX;
                    float blindY = rng != null ? rng.Range(-BlindScatterY, BlindScatterY) : -BlindScatterY;
                    emits.Add(new ParticleEmit(BlindId, null, blindX, blindY));
                    return true;

                case DrunkId:
                    // `if(this.id == "drunk" && this.lvl > 3)` (:436-440). The damage that shares this
                    // branch belongs to the payload layer, not here.
                    if (ctx.Level <= DrunkPoisonLevel)
                    {
                        return false;
                    }

                    // Emitter.emit("poison", loc, X + storona * 20, Y - 40)
                    emits.Add(new ParticleEmit(PoisonId, null,
                        ctx.Facing * PoisonFacingOffsetX, PoisonOffsetY));
                    return true;

                case NamokId:
                    // `if(this.id == "namok")` then `if(!owner.isPlav && owner.sost < 4)` (:441-447).
                    if (ctx.IsFloating || !ctx.IsInWorld)
                    {
                        return false;
                    }

                    // Emitter.emit("kap", loc, X, Y - scY * 0.25, {"md":0.1})
                    emits.Add(new ParticleEmit(KapId,
                        new ParticleSpec { Md = KapVelocityScale },
                        0f,
                        -ctx.SpriteHeight * KapHeightFraction));
                    return true;

                case FetterId:
                    // `Emitter.emit("slow", loc, (owner as UnitPlayer).fetX, (owner as UnitPlayer).fetY)`
                    // — :470. An absolute position, so the offset is relative to the owner's origin.
                    //
                    // GATED ON IsPlayer, and that test IS the port of the oracle's own gate. AS3 does
                    // not ask the owner's type here: it relies on `as UnitPlayer` yielding null for a
                    // monster and the following `null.fetX` THROWING, so a non-player owner can never
                    // reach this line. A port cannot reproduce a crash, and without the test it would
                    // emit `slow` at `(0 - X, 0 - Y)` — the mirror of the owner's own position, i.e. a
                    // ring in a different part of the room that looks like a real effect. `IsPlayer` is
                    // exactly the predicate AS3's cast expresses.
                    if (!ctx.IsPlayer)
                    {
                        return false;
                    }

                    emits.Add(new ParticleEmit(SlowId, null,
                        ctx.FetterX - ctx.X, ctx.FetterY - ctx.Y));
                    return true;

                default:
                    return false;
            }
        }
    }
}
