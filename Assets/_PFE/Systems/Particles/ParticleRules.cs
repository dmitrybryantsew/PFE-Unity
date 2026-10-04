using System;
using System.Collections.Generic;
using PFE.Core.Rng;

namespace PFE.Systems.Particles
{
    /// <summary>
    /// What happened when one particle was advanced by one tick — the port of the three distinct
    /// outcomes of <c>Part.step</c>.
    /// </summary>
    public enum ParticleStepResult
    {
        /// <summary>
        /// The particle is still waiting out its <c>otklad</c>. It is invisible, does not move, does not
        /// age, and — importantly — is <b>not</b> counted toward the budget tick, because AS3 returns
        /// before <c>++Emitter.kol1</c> (<c>Part.as:144-150</c>).
        /// </summary>
        Delayed = 0,

        /// <summary>Advanced normally and is still alive.</summary>
        Alive = 1,

        /// <summary>
        /// Expired this tick. The budget slot has already been released; the caller must clear the pool
        /// slot.
        /// </summary>
        Expired = 2
    }

    /// <summary>
    /// The arithmetic of AS3's <c>fe.graph.Emitter.cast</c> and <c>fe.graph.Part.step</c>, as a pure
    /// static — the port of the spawn maths and the per-tick integration.
    ///
    /// <para><b>Why every rule is here and not in the renderer.</b> An offline host cannot JIT a method
    /// whose IL mentions a Unity <c>ECall</c>, so anything that lives on the Unity side is permanently
    /// outside the reach of the test wall. A particle system is almost entirely arithmetic; putting the
    /// arithmetic here means the spawn distribution, the life and velocity ranges, both budgets and the
    /// <c>maxkol</c> early-return can all be executed and pinned with no editor and no Unity. The
    /// renderer is then left with nothing but "look up a sprite and set a transform".</para>
    ///
    /// <para><b>Numbers in, numbers out.</b> No Unity type appears in any signature. The randomness
    /// arrives through <see cref="IRngService"/> rather than a bare <c>Random</c>, because the oracle is
    /// <c>Math.random()</c>-heavy and an unseamed generator would make every fixture a coin toss.</para>
    ///
    /// <para><b>Draw order is preserved deliberately.</b> The oracle interleaves its <c>Math.random()</c>
    /// calls with its branches, and several of those draws are <i>thrown away</i> — an <c>anim=1</c>
    /// particle draws a random frame for <c>gotoAndStop</c> and then immediately overrides it with
    /// <c>gotoAndPlay</c> (<c>Part.as:114-133</c>). The port reproduces the draws, not just the results,
    /// so a seeded run stays comparable with the oracle call for call.</para>
    /// </summary>
    public static class ParticleRules
    {
        /// <summary>
        /// AS3 <c>World.ddy</c> (<c>World.as:46</c>) — a <c>static const</c> of <c>1</c>. Every gravity
        /// term in the emitter is this value times a per-row coefficient, so it is the unit of the
        /// <c>grav</c> and <c>rgrav</c> attributes rather than a tunable.
        /// </summary>
        public const float WorldDdy = 1f;

        /// <summary>
        /// AS3 <c>Math.floor(v)</c> as an <c>int</c>. Computed in <c>double</c> because AS3's
        /// <c>Number</c> is a double: flooring a <c>float</c> product can land on the other side of an
        /// integer boundary from flooring the double one.
        /// </summary>
        private static int FloorToInt(double value) => (int)Math.Floor(value);

        /// <summary>
        /// AS3's random frame pick — <c>Math.floor(Math.random() * totalFrames)</c>, giving a 0-based
        /// index into the port's sprite array for the 1-based <c>gotoAndStop</c>/<c>gotoAndPlay</c>
        /// argument (<c>Part.as:116</c>, <c>:128</c>).
        /// </summary>
        private static int RandomFrame(IRngService rng, int frameCount) =>
            frameCount > 0 ? FloorToInt((double)rng.NextFloat() * frameCount) : 0;

        /// <summary>
        /// Resolves a <c>blend</c> attribute. The oracle hands the raw string to
        /// <c>DisplayObject.blendMode</c> (<c>Emitter.as:327</c>); the port maps it to a value the
        /// renderer can switch on. An unrecognised name falls back to <c>normal</c> — the same thing AS3
        /// does with an invalid string.
        /// </summary>
        public static ParticleBlend ParseBlend(string blend) => blend switch
        {
            "screen" => ParticleBlend.Screen,
            "overlay" => ParticleBlend.Overlay,
            "multiply" => ParticleBlend.Multiply,
            "hardlight" => ParticleBlend.HardLight,
            _ => ParticleBlend.Normal,
        };

        /// <summary>
        /// Resolves a <c>filter</c> attribute against <c>Emitter.fils</c> (<c>Emitter.as:18-21</c>).
        /// Only <c>bur</c> and <c>plav</c> exist; anything else is ignored by the oracle's own
        /// <c>Emitter.fils[this.filter]</c> truthiness test at <c>:340</c>.
        /// </summary>
        public static ParticleFilter ParseFilter(string filter) => filter switch
        {
            "bur" => ParticleFilter.Bur,
            "plav" => ParticleFilter.Plav,
            _ => ParticleFilter.None,
        };

        /// <summary>
        /// Builds one particle — the port of the body of <c>Emitter.cast</c>'s per-particle loop
        /// (<c>Emitter.as:188-389</c>), everything except the budget reservation, which the caller owns
        /// because it is the only part that has to happen in a specific order relative to the batch.
        ///
        /// <para><b><paramref name="spriteFrameCount"/> is the visual's own frame count</b> — AS3's
        /// <c>vis.totalFrames</c> for a <c>vis=</c> row, or <c>blitData.width / blitX</c> for a
        /// <c>blit=</c> row. The rules cannot know it (it is a property of the loaded art, not of the
        /// definition) and it is needed because a start frame of "random" is one of the three
        /// <c>anim</c> modes. Every particle in one <c>cast</c> shares the definition, so one value
        /// covers the whole batch.</para>
        /// </summary>
        /// <param name="definitionIndex">
        /// Row index this particle belongs to, stored on the state so the renderer can find its sprites.
        /// </param>
        /// <param name="x">Spawn position. AS3 <c>cast</c>'s <c>param2</c> (<c>Emitter.as:154</c>).</param>
        /// <param name="y">Spawn position. AS3 <c>cast</c>'s <c>param3</c>.</param>
        /// <param name="spriteFrameCount">Frames available in the visual; 0 when unknown, which suppresses the random pick.</param>
        public static void SpawnOne(
            int definitionIndex,
            ParticleDefinition definition,
            ParticleSpec spec,
            float x,
            float y,
            IRngService rng,
            int spriteFrameCount,
            out ParticleState state)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            if (rng == null) throw new ArgumentNullException(nameof(rng));
            if (spec == null) spec = ParticleSpec.None;

            // ── 1. Spawn position and the definition's own jitter (Emitter.as:191-200) ──────────────
            if (definition.RX != 0f) x += (rng.NextFloat() - 0.5f) * definition.RX;
            if (definition.RY != 0f) y += (rng.NextFloat() - 0.5f) * definition.RY;

            // ── 2. Burst velocity at a uniform random angle (Emitter.as:207-223) ─────────────────────
            float dx = 0f;
            float dy = 0f;
            if (definition.MinV + definition.RV > 0f)
            {
                float angle = rng.NextFloat() * (float)Math.PI * 2f;
                float speed = rng.NextFloat() * definition.RV + definition.MinV;
                dx = MathF.Sin(angle) * speed;
                dy = MathF.Cos(angle) * speed;
            }
            if (definition.RDX != 0f) dx += (rng.NextFloat() - 0.5f) * definition.RDX;
            if (definition.RDY != 0f) dy += (rng.NextFloat() - 0.5f) * definition.RDY;
            dx += definition.DX;
            dy += definition.DY;

            // ── 3. Spin and initial rotation (Emitter.as:224-231) ───────────────────────────────────
            float dr = 0f;
            if (definition.RDR != 0f) dr = (rng.NextFloat() - 0.5f) * definition.RDR;
            float r = 0f;
            if (definition.Rot != 0) r = rng.NextFloat() * 360f;

            // ── 4. The caller's overrides (Emitter.as:232-271) ──────────────────────────────────────
            //
            // Every one of these is an AS3 truthiness test, so a supplied zero is treated as absent.
            // The single exception is `md`, which tests against null — see ParticleSpec's own notes.
            if (spec.RX != 0f) x += (rng.NextFloat() - 0.5f) * spec.RX;
            if (spec.RY != 0f) y += (rng.NextFloat() - 0.5f) * spec.RY;
            if (spec.DX != 0f) dx += spec.DX;
            if (spec.DY != 0f) dy += spec.DY;
            if (spec.DR != 0f) dr += spec.DR;
            if (spec.Md.HasValue)
            {
                dx *= spec.Md.Value;
                dy *= spec.Md.Value;
            }

            // ── 5. Gravity (Emitter.as:272-277) ─────────────────────────────────────────────────────
            float ddy = WorldDdy * definition.Grav;
            if (definition.RGrav != 0f) ddy += WorldDdy * definition.RGrav * rng.NextFloat();

            // ── 6. Life (Emitter.as:278) ────────────────────────────────────────────────────────────
            int liv = FloorToInt((double)rng.NextFloat() * definition.RLiv) + definition.MinLiv;

            // ── 7. Blit loop cursor (Emitter.as:296-300) ────────────────────────────────────────────
            int blitLoop = 0;
            float blitCursor = 0f;
            if (definition.BlitLoopFrames > 0)
            {
                blitLoop = definition.BlitLoopFrames;
                blitCursor = MathF.Floor(rng.NextFloat() * definition.BlitLoopFrames);
            }

            // ── 8. Spawn delay (Emitter.as:301-304) ─────────────────────────────────────────────────
            int otkladSource = spec.Otklad != 0 ? spec.Otklad : definition.Otklad;
            int otklad = otkladSource > 0
                ? FloorToInt((double)rng.NextFloat() * otkladSource + 1.0)
                : 0;

            // ── 9. Start frame (Emitter.as:305-312 → Part.as:109-139 and :95-99) ────────────────────
            //
            // The oracle draws for gotoAndStop whenever the frame argument is 0, and then draws AGAIN
            // for gotoAndPlay when anim == 2. For anim == 1 the first draw is thrown away. All three
            // draws are reproduced here.
            int frameArg = spec.Frame;
            if (spec.DFrame != 0) frameArg += FloorToInt((double)rng.NextFloat() * spec.DFrame + 1.0);

            bool isBlit = !string.IsNullOrEmpty(definition.Blit);
            int visCursor = 0;
            if (!string.IsNullOrEmpty(definition.Vis))
            {
                visCursor = frameArg == 0 ? RandomFrame(rng, spriteFrameCount) : frameArg - 1;
                if (definition.Anim == 2) visCursor = RandomFrame(rng, spriteFrameCount);
                else if (definition.Anim == 1) visCursor = frameArg;
            }
            else if (isBlit && definition.Anim == 0)
            {
                blitCursor = MathF.Floor(rng.NextFloat() * spriteFrameCount);
            }

            // ── 10. Alpha and scale (Emitter.as:313-335) ────────────────────────────────────────────
            float alpha = 1f;
            if (spec.Alpha.HasValue && spec.Alpha.Value != 0f) alpha = spec.Alpha.Value;

            float scale = 1f;
            if (spec.Scale.HasValue && spec.Scale.Value != 0f) scale = spec.Scale.Value;
            if (definition.Scale != 1f) scale = definition.Scale;
            // Note `definition.Scale`, not the running `scale` — the oracle overwrites both scale axes
            // from this.scale here, discarding any param4.scale it applied above.
            if (definition.Rsc != 0f)
                scale = definition.Scale - definition.Rsc + rng.NextFloat() * definition.Rsc;

            // ── 11. Rotation override (Emitter.as:323-326) ──────────────────────────────────────────
            float rotation = r;
            if (spec.Rotation.HasValue && spec.Rotation.Value != 0f) rotation = spec.Rotation.Value;

            // ── 12. Assemble ────────────────────────────────────────────────────────────────────────
            state = default;
            state.DefinitionIndex = definitionIndex;
            state.X = x;
            state.Y = y;
            state.DX = dx;
            state.DY = dy;
            state.DDY = ddy;
            state.Brake = definition.Brake;
            state.R = rotation;
            state.DR = dr;

            // Emitter.as:282 — computed once, from the post-override velocities.
            state.IsMove = dx != 0f || dy != 0f || ddy != 0f;

            state.Liv = liv;
            state.MLiv = liv;

            // Emitter.as:363-366 — prealph is applied LAST, so it wins over param4.alpha.
            state.IsAlph = definition.Alph;
            state.IsPreAlph = definition.PreAlph;
            state.Alpha = definition.PreAlph ? 0f : alpha;
            state.Scale = scale;

            state.Sloy = definition.Sloy;
            state.Mirr = spec.Mirr;
            state.Ctrans = definition.Ctrans;
            state.Blend = ParseBlend(definition.Blend);
            state.Filter = ParseFilter(definition.Filter);

            state.Anim = definition.Anim;
            state.IsBlit = isBlit;
            state.FrameCount = spriteFrameCount;
            state.FrameIndex = visCursor;
            state.BlitFrame = blitCursor;
            state.BlitDelta = definition.BlitDelta;
            state.BlitLoopFrames = blitLoop;

            // The frame shown on the tick the particle is created. Part.as:114-133 leaves the visual on
            // whatever gotoAndStop selected, and for anim != 0 the first step advances from there.
            state.DrawFrame = isBlit ? (int)blitCursor : visCursor;

            state.Otklad = otklad;
            state.Water = definition.Water;
            state.MaxKol = 0;      // the caller's budget.Reserve owns this
            state.Visible = true;  // AS3 vis.visible defaults true; the first step clears it if delayed
        }

        /// <summary>
        /// Spawns one <c>cast</c> — the port of <c>Emitter.cast</c>'s budget gates and loop
        /// (<c>Emitter.as:154-393</c>), minus the display work.
        ///
        /// <para>The two gates are <b>not</b> symmetric, and the asymmetry is the whole point of the
        /// method:</para>
        /// <list type="bullet">
        /// <item><description>The global ceiling is tested <b>once, before the batch</b> — a refused cast
        /// adds nothing at all (<c>:167</c>).</description></item>
        /// <item><description>The per-type cap is tested <b>per particle, inside the batch</b> — a
        /// refused particle ends the batch where it stands (<c>:184</c>). A <c>kol=30</c> acid burst with
        /// 5 of its 12 slots already taken spawns 7, not 0 and not 30.</description></item>
        /// </list>
        /// </summary>
        /// <returns>How many particles were actually added to <paramref name="sink"/>.</returns>
        public static int Cast(
            ParticleDefinition definition,
            int definitionIndex,
            ParticleSpec spec,
            float x,
            float y,
            IRngService rng,
            ParticleBudget budget,
            IList<ParticleState> sink,
            int spriteFrameCount)
        {
            if (definition == null || rng == null || budget == null || sink == null) return 0;
            if (spec == null) spec = ParticleSpec.None;

            if (!budget.AllowsGlobal(definition))
            {
                budget.NoteGlobalDrop();
                return 0;
            }

            // Emitter.as:171-179 — `kol` defaults to 1, and the 50 cap is applied after the merge.
            int count = spec.Kol != 0 ? spec.Kol : 1;
            if (count > ParticleBudget.CastCountCap) count = ParticleBudget.CastCountCap;

            int spawned = 0;
            for (int i = 0; i < count; i++)
            {
                if (!budget.AllowsType(definition))
                {
                    budget.NoteTypeDrop();
                    break;
                }

                SpawnOne(definitionIndex, definition, spec, x, y, rng, spriteFrameCount, out ParticleState state);
                state.MaxKol = budget.Reserve(definition);
                sink.Add(state);
                spawned++;
            }

            return spawned;
        }

        /// <summary>
        /// Advances one particle by one tick — the port of <c>Part.step</c> (<c>Part.as:141-206</c>).
        ///
        /// <para><b>The caller owns the pool slot.</b> On <see cref="ParticleStepResult.Expired"/> the
        /// budget slot has been released, but the state is deliberately <i>not</i> cleared: the pool
        /// decides when a slot is reusable, and clearing here would destroy the final position and
        /// rotation before the renderer has had a chance to draw the last frame.</para>
        /// </summary>
        /// <param name="tileWater">
        /// The water value of the tile the particle is standing on, for <c>water=</c> rows
        /// (<c>Part.as:194</c>). Pass 0 when the definition's <c>Water</c> is 0 — the value is then
        /// unread.
        /// </param>
        public static ParticleStepResult Step(ref ParticleState state, int tileWater, ParticleBudget budget)
        {
            // Part.as:144-150 — delayed particles are hidden, frozen, and NOT counted toward the budget
            // tick, because AS3 returns before the trailing ++Emitter.kol1.
            if (state.Otklad > 0)
            {
                state.Visible = false;
                state.Otklad--;
                return ParticleStepResult.Delayed;
            }

            // Part.as:151-158 — becoming visible again. The oracle's `if (isAnim > 0) vis.play()`
            // has no port equivalent: the renderer never runs a timeline, the frame advance below is
            // the only thing that moves a frame.
            state.Visible = true;

            // Part.as:159-170 — integration happens ONLY when isMove, which was decided at spawn. A
            // particle with no velocity, no jitter and no gravity stays exactly where it was born.
            if (state.IsMove)
            {
                state.X += state.DX;
                state.Y += state.DY;
                state.DY += state.DDY;
                state.R += state.DR;
                state.DX *= state.Brake;
                state.DY *= state.Brake;
            }

            // Part.as:171-182 — the three-branch alpha ladder. The final branch matters: a fading
            // particle whose life is still above the threshold is explicitly restored to opaque, so a
            // reused pool slot cannot inherit the previous occupant's alpha.
            if (state.IsAlph && state.Liv < 9)
            {
                state.Alpha = state.Liv / 10f;
            }
            else if (state.IsPreAlph && state.MLiv - state.Liv < 9)
            {
                state.Alpha = (state.MLiv - state.Liv) / 10f;
            }
            else if (state.IsAlph || state.IsPreAlph)
            {
                state.Alpha = 1f;
            }

            // Part.as:183-191 (blit) and :126-133 (vis) — the two paths advance at different moments,
            // and the difference is the oracle's, not a simplification.
            //
            //   blit  — `Part.step` draws and THEN advances, inside the same call (Part.as:185-186), so
            //           the frame drawn this tick is the one the cursor was already on.
            //   vis   — `Part.step` never touches the frame at all; the MovieClip's own timeline moves
            //           it, and a timeline advances BEFORE its frame is drawn. So the port advances
            //           first and draws where it landed.
            //
            // Collapsing the two into one order would either double the vis spawn frame or drop the
            // blit's first frame.
            if (state.Anim != 0)
            {
                if (state.IsBlit)
                {
                    if (state.BlitFrame < state.FrameCount)
                    {
                        state.DrawFrame = (int)state.BlitFrame;
                        state.BlitFrame += state.BlitDelta;
                        if (state.BlitLoopFrames > 0 && state.BlitFrame >= state.BlitLoopFrames)
                            state.BlitFrame = 0f;
                    }
                }
                else
                {
                    if (state.FrameCount > 0) state.FrameIndex = (state.FrameIndex + 1) % state.FrameCount;
                    state.DrawFrame = state.FrameIndex;
                }
            }

            // Part.as:192-199 — the water lifetime rule. Note it sets liv to 1 and lets the decrement
            // below finish the job, so the particle dies on this very tick.
            if (state.Water > 0)
            {
                if ((state.Water == 2 && tileWater == 0) || (state.Water == 1 && tileWater > 0))
                {
                    state.Liv = 1;
                }
            }

            // Part.as:200-205 — age, die, and count.
            state.Liv--;
            bool expired = state.Liv <= 0;
            if (expired && budget != null) budget.Release(state.MaxKol);
            budget?.CountStep();

            return expired ? ParticleStepResult.Expired : ParticleStepResult.Alive;
        }
    }
}
