using System;
using System.Collections.Generic;
using NUnit.Framework;
using PFE.Core.Rng;
using PFE.Systems.Particles;

namespace PFE.Tests.EditMode.Systems.Particles
{
    /// <summary>
    /// Pins <see cref="ParticleRules"/> — the spawn arithmetic of <c>Emitter.cast</c> and the per-tick
    /// integration of <c>Part.step</c>.
    ///
    /// <para><b>Why this fixture exists at all.</b> Every other consumer of the emitter is a
    /// <c>MonoBehaviour</c> or reaches one, so a test that drove them would need the editor. The rules
    /// were extracted to a pure static precisely so they could be executed here, in the offline wall —
    /// which is the only place this arithmetic can be checked at all. The split is the point, not a
    /// side effect.</para>
    ///
    /// <para><b>Expectations are written as expressions, not decimal literals.</b> A hand-computed
    /// literal for something like <c>minliv + floor(0.5 * rliv)</c> is a second, silent copy of the
    /// formula, and a wrong literal makes the test agree with a wrong implementation.</para>
    /// </summary>
    [TestFixture]
    public class ParticleRulesTests
    {
        private const float Tolerance = 1e-5f;

        /// <summary>
        /// A definition with the oracle's declared defaults and nothing else — <c>vis</c> set so the
        /// <c>vis=</c> path is taken, and the row otherwise empty.
        /// </summary>
        private static ParticleDefinition Def(Action<ParticleDefinition> configure = null)
        {
            var def = new ParticleDefinition { Id = "test", Vis = "visualTest" };
            configure?.Invoke(def);
            return def;
        }

        private static ParticleDefinition BlitDef(Action<ParticleDefinition> configure = null)
        {
            var def = new ParticleDefinition { Id = "test", Blit = "sprTest" };
            configure?.Invoke(def);
            return def;
        }

        /// <summary>
        /// An RNG that returns scripted values from a queue and counts how many were consumed. The count
        /// is load-bearing here: the oracle draws for branches whose result it then throws away, so
        /// "did this path roll at all" is part of the behaviour being ported, not an implementation
        /// detail. Once the queue is empty it returns 0.
        /// </summary>
        private sealed class ScriptedRng : IRngService
        {
            private readonly Queue<float> _values;

            public int RollsConsumed { get; private set; }

            public ScriptedRng(params float[] values) => _values = new Queue<float>(values);

            public float NextFloat()
            {
                RollsConsumed++;
                return _values.Count > 0 ? _values.Dequeue() : 0f;
            }

            public IRngService GetStream(RngStream stream, int? salt = null) => this;
            public uint NextUInt() => 0u;
            public int NextInt(int maxExclusive) => 0;
            public int Range(int minInclusive, int maxExclusive) => minInclusive;
            public float Range(float min, float max) => min;
            public bool Chance(float probability) => NextFloat() < probability;
            public void Shuffle<T>(IList<T> list) { }
        }

        private static ParticleState Spawn(ParticleDefinition def, ParticleSpec spec, IRngService rng,
                                           float x = 100f, float y = 200f, int frameCount = 4)
        {
            ParticleRules.SpawnOne(0, def, spec, x, y, rng, frameCount, out ParticleState state);
            return state;
        }

        // ── Position ──────────────────────────────────────────────────────────

        [Test]
        public void Spawn_WithNoJitter_LandsExactlyOnTheGivenPoint()
        {
            ParticleState s = Spawn(Def(), ParticleSpec.None, new ScriptedRng(0.5f));
            Assert.That(s.X, Is.EqualTo(100f).Within(Tolerance));
            Assert.That(s.Y, Is.EqualTo(200f).Within(Tolerance));
        }

        [Test]
        public void Spawn_RxJitter_IsHalfWidthEitherSide()
        {
            // (rand - 0.5) * rx: at rand 1 the offset is +rx/2, at rand 0 it is -rx/2.
            ParticleState high = Spawn(Def(d => d.RX = 20f), ParticleSpec.None, new ScriptedRng(1f, 0.5f));
            ParticleState low = Spawn(Def(d => d.RX = 20f), ParticleSpec.None, new ScriptedRng(0f, 0.5f));

            Assert.That(high.X, Is.EqualTo(110f).Within(Tolerance), "rand 1 gives +rx/2 = +10");
            Assert.That(low.X, Is.EqualTo(90f).Within(Tolerance), "rand 0 gives -rx/2 = -10");
            Assert.That(high.Y, Is.EqualTo(200f).Within(Tolerance), "ry is unset, so Y must not move");
        }

        [Test]
        public void Spawn_SpecJitter_IsAddedOnTopOfTheDefinitions()
        {
            var spec = new ParticleSpec { RX = 10f, RY = 10f };
            ParticleState s = Spawn(Def(d => d.RX = 20f), spec, new ScriptedRng(0.5f, 1f, 1f, 0.5f));

            // def.rx at 0.5 is 0; spec.rx at 1 is +5; def.ry unset; spec.ry at 1 is +5.
            Assert.That(s.X, Is.EqualTo(105f).Within(Tolerance));
            Assert.That(s.Y, Is.EqualTo(205f).Within(Tolerance));
        }

        // ── Life ──────────────────────────────────────────────────────────────

        [Test]
        public void Spawn_LifeIsMinLivPlusTheFlooredRoll()
        {
            ParticleState s = Spawn(Def(d => { d.MinLiv = 20; d.RLiv = 40; }), ParticleSpec.None,
                                    new ScriptedRng(0.5f, 0.5f));
            Assert.That(s.Liv, Is.EqualTo(40), "20 + floor(0.5 * 40)");
            Assert.That(s.MLiv, Is.EqualTo(s.Liv), "mliv is seeded from liv at spawn and never changes");
        }

        [Test]
        public void Spawn_LifeAtRollZero_IsExactlyMinLiv()
        {
            // The floor is what makes rliv a range of exactly [minliv, minliv+rliv-1].
            ParticleState s = Spawn(Def(d => { d.MinLiv = 20; d.RLiv = 40; }), ParticleSpec.None,
                                    new ScriptedRng(0f, 0.5f));
            Assert.That(s.Liv, Is.EqualTo(20));
        }

        [Test]
        public void Spawn_LifeRollJustUnderOne_DoesNotReachMinLivPlusRliv()
        {
            ParticleState s = Spawn(Def(d => { d.MinLiv = 20; d.RLiv = 40; }), ParticleSpec.None,
                                    new ScriptedRng(0.999f, 0.5f));
            Assert.That(s.Liv, Is.EqualTo(59), "floor(0.999 * 40) = 39 — rliv is exclusive at the top.");
        }

        // ── Burst velocity ────────────────────────────────────────────────────

        [Test]
        public void Spawn_BurstSpeedIsMinVPlusTheRolledRv()
        {
            ParticleState s = Spawn(Def(d => { d.MinV = 2f; d.RV = 10f; }),
                                    ParticleSpec.None, new ScriptedRng(0f, 0.5f, 0.5f));

            // angle 0, speed 0.5*10 + 2 = 7 → dx = sin(0)*7 = 0, dy = cos(0)*7 = 7.
            Assert.That(s.DX, Is.EqualTo(0f).Within(1e-4f));
            Assert.That(s.DY, Is.EqualTo(7f).Within(1e-4f));
        }

        [Test]
        public void Spawn_BurstAtAQuarterTurn_IsPurelyHorizontal()
        {
            ParticleState s = Spawn(Def(d => { d.MinV = 10f; d.RV = 0f; }),
                                    ParticleSpec.None, new ScriptedRng(0.25f, 0.5f));

            // angle = 0.25 * 2pi = pi/2 → dx = sin = 10, dy = cos = ~0.
            Assert.That(s.DX, Is.EqualTo(10f).Within(1e-4f));
            Assert.That(s.DY, Is.EqualTo(0f).Within(1e-4f));
        }

        [Test]
        public void Spawn_BurstIsSuppressedWhenMinVAndRvAreBothZero()
        {
            // The guard is `minv + rv > 0`, so a row with neither gets no angle burst AND no draw.
            var rng = new ScriptedRng(0.5f);
            ParticleState s = Spawn(Def(), ParticleSpec.None, rng);

            Assert.That(s.DX, Is.EqualTo(0f).Within(Tolerance));
            Assert.That(s.DY, Is.EqualTo(0f).Within(Tolerance));
            Assert.That(rng.RollsConsumed, Is.EqualTo(2),
                "only the life roll and the vis frame roll — no angle, no speed");
        }

        [Test]
        public void Spawn_FixedDyIsAddedAfterTheBurst()
        {
            // The debris rows all carry dy='-5'. At angle 0 the burst is entirely on Y (cos = 1), so the
            // two add rather than replace.
            ParticleState s = Spawn(Def(d => { d.DY = -5f; d.MinV = 2f; d.RV = 0f; }),
                                    ParticleSpec.None, new ScriptedRng(0f, 0.5f));

            Assert.That(s.DX, Is.EqualTo(0f).Within(1e-4f), "sin(0) puts nothing on X");
            Assert.That(s.DY, Is.EqualTo(-3f).Within(1e-4f), "burst 2 + dy -5");
        }

        [Test]
        public void Spawn_MdMultipliesBothVelocityComponents()
        {
            var spec = new ParticleSpec { Md = 0.5f };
            ParticleState s = Spawn(Def(d => { d.DX = 4f; d.DY = 8f; }), spec, new ScriptedRng(0.5f));

            Assert.That(s.DX, Is.EqualTo(2f).Within(Tolerance));
            Assert.That(s.DY, Is.EqualTo(4f).Within(Tolerance));
        }

        [Test]
        public void Spawn_MdOfZeroIsHonoured_AndKillsTheVelocity()
        {
            // The one field the oracle tests against null rather than truthiness, so a supplied 0 is
            // the only way to zero a definition's burst.
            var spec = new ParticleSpec { Md = 0f };
            ParticleState s = Spawn(Def(d => { d.DX = 4f; d.DY = 8f; d.Grav = 0f; }), spec, new ScriptedRng(0.5f));

            Assert.That(s.DX, Is.EqualTo(0f).Within(Tolerance));
            Assert.That(s.DY, Is.EqualTo(0f).Within(Tolerance));
            Assert.IsFalse(s.IsMove, "with no velocity and no gravity the particle is frozen");
        }

        // ── IsMove ────────────────────────────────────────────────────────────

        [Test]
        public void Spawn_IsMoveIsTrueForGravityAlone()
        {
            ParticleState s = Spawn(Def(d => d.Grav = 1f), ParticleSpec.None, new ScriptedRng(0.5f));
            Assert.IsTrue(s.IsMove, "ddy != 0 is enough; the particle is not frozen");
        }

        [Test]
        public void Spawn_IsMoveIsFalseWhenNothingMoves()
        {
            ParticleState s = Spawn(Def(), ParticleSpec.None, new ScriptedRng(0.5f));
            Assert.IsFalse(s.IsMove);
        }

        // ── Gravity ───────────────────────────────────────────────────────────

        [Test]
        public void Spawn_GravityIsWorldDdyTimesGrav()
        {
            Assert.That(ParticleRules.WorldDdy, Is.EqualTo(1f), "AS3 World.ddy is a static const of 1");

            ParticleState down = Spawn(Def(d => d.Grav = 1f), ParticleSpec.None, new ScriptedRng(0.5f));
            ParticleState up = Spawn(Def(d => d.Grav = -0.2f), ParticleSpec.None, new ScriptedRng(0.5f));

            Assert.That(down.DDY, Is.EqualTo(1f).Within(Tolerance));
            Assert.That(up.DDY, Is.EqualTo(-0.2f).Within(Tolerance));
        }

        [Test]
        public void Spawn_RgravAddsAScaledJitter_OnTopOfGrav()
        {
            ParticleState s = Spawn(Def(d => { d.Grav = -0.1f; d.RGrav = -0.2f; }),
                                    ParticleSpec.None, new ScriptedRng(0.5f, 0.5f));

            // ddy = 1 * grav + 1 * rgrav * rand = -0.1 + (-0.2 * 0.5) = -0.2
            Assert.That(s.DDY, Is.EqualTo(-0.2f).Within(Tolerance));
        }

        // ── Rotation and spin ─────────────────────────────────────────────────

        [Test]
        public void Spawn_Rot_GivesARotationUniformOnZeroToThreeSixty()
        {
            ParticleState s = Spawn(Def(d => d.Rot = 1), ParticleSpec.None, new ScriptedRng(0.5f, 0.5f));
            Assert.That(s.R, Is.EqualTo(180f).Within(1e-4f));
        }

        [Test]
        public void Spawn_WithoutRot_TheRotationIsZero()
        {
            ParticleState s = Spawn(Def(), ParticleSpec.None, new ScriptedRng(0.5f));
            Assert.That(s.R, Is.EqualTo(0f).Within(Tolerance));
        }

        [Test]
        public void Spawn_Rdr_GivesASymmetricSpin()
        {
            ParticleState high = Spawn(Def(d => d.RDR = 4f), ParticleSpec.None, new ScriptedRng(1f, 0.5f));
            ParticleState low = Spawn(Def(d => d.RDR = 4f), ParticleSpec.None, new ScriptedRng(0f, 0.5f));

            Assert.That(high.DR, Is.EqualTo(2f).Within(Tolerance), "(1 - 0.5) * 4");
            Assert.That(low.DR, Is.EqualTo(-2f).Within(Tolerance), "(0 - 0.5) * 4");
        }

        // ── Alpha and scale ───────────────────────────────────────────────────

        [Test]
        public void Spawn_PreAlph_StartsInvisible_AndWinsOverTheSpecAlpha()
        {
            // The oracle applies prealph LAST, after param4.alpha — so a prealph row is invisible at
            // spawn however the caller asked for it to look.
            var spec = new ParticleSpec { Alpha = 0.75f };
            ParticleState s = Spawn(Def(d => d.PreAlph = true), spec, new ScriptedRng(0.5f));

            Assert.That(s.Alpha, Is.EqualTo(0f).Within(Tolerance));
            Assert.IsTrue(s.IsPreAlph);
        }

        [Test]
        public void Spawn_SpecAlphaIsUsedWhenTheRowIsNotPreAlph()
        {
            var spec = new ParticleSpec { Alpha = 0.75f };
            ParticleState s = Spawn(Def(), spec, new ScriptedRng(0.5f));
            Assert.That(s.Alpha, Is.EqualTo(0.75f).Within(Tolerance));
        }

        [Test]
        public void Spawn_SpecAlphaOfZeroIsIgnored_BecauseTheOracleTestsTruthiness()
        {
            // AS3's `if (param4.alpha)` is false for 0, so the definition's alpha stands. Surprising,
            // and worth pinning: a caller who writes Alpha = 0 to hide a particle gets an opaque one.
            var spec = new ParticleSpec { Alpha = 0f };
            ParticleState s = Spawn(Def(), spec, new ScriptedRng(0.5f));
            Assert.That(s.Alpha, Is.EqualTo(1f).Within(Tolerance));
        }

        [Test]
        public void Spawn_RscSpreadsTheScaleBelowTheDefinitionScale()
        {
            // `scale - rsc + rand * rsc` — uniform on [scale-rsc, scale), NOT centred on scale.
            // The rsc roll is the LAST draw of the spawn, after the vis frame pick.
            ParticleState low = Spawn(Def(d => { d.Scale = 2f; d.Rsc = 0.5f; }),
                                      ParticleSpec.None, new ScriptedRng(0.5f, 0.5f, 0f));
            ParticleState high = Spawn(Def(d => { d.Scale = 2f; d.Rsc = 0.5f; }),
                                       ParticleSpec.None, new ScriptedRng(0.5f, 0.5f, 1f));

            Assert.That(low.Scale, Is.EqualTo(1.5f).Within(Tolerance), "2 - 0.5 + 0");
            Assert.That(high.Scale, Is.EqualTo(2.0f).Within(Tolerance), "2 - 0.5 + 0.5");
        }

        [Test]
        public void Spawn_RscOverwritesTheSpecScale_UsingTheDefinitionScaleNotTheRunningOne()
        {
            // The oracle writes `this.scale - this.rsc + rand*rsc` from the DEFINITION's scale, so a
            // param4.scale applied a few lines earlier is discarded. `ttexpl` is scale='2' rsc='0.5'.
            var spec = new ParticleSpec { Scale = 5f };
            ParticleState s = Spawn(Def(d => { d.Scale = 2f; d.Rsc = 0.5f; }),
                                    spec, new ScriptedRng(0f, 0.5f));

            Assert.That(s.Scale, Is.EqualTo(1.5f).Within(Tolerance),
                "not 5 - 0.5, and not 2 - 0.5 + anything from the spec");
        }

        [Test]
        public void Spawn_DefinitionScaleIsAppliedWhenItIsNotOne()
        {
            ParticleState s = Spawn(Def(d => d.Scale = 2f), ParticleSpec.None, new ScriptedRng(0.5f));
            Assert.That(s.Scale, Is.EqualTo(2f).Within(Tolerance));
        }

        // ── Blend and filter ──────────────────────────────────────────────────

        [Test]
        public void ParseBlend_CoversEveryValueTheDataUses()
        {
            Assert.That(ParticleRules.ParseBlend("screen"), Is.EqualTo(ParticleBlend.Screen));
            Assert.That(ParticleRules.ParseBlend("overlay"), Is.EqualTo(ParticleBlend.Overlay));
            Assert.That(ParticleRules.ParseBlend("multiply"), Is.EqualTo(ParticleBlend.Multiply));
            Assert.That(ParticleRules.ParseBlend("hardlight"), Is.EqualTo(ParticleBlend.HardLight));
            Assert.That(ParticleRules.ParseBlend("normal"), Is.EqualTo(ParticleBlend.Normal));
            Assert.That(ParticleRules.ParseBlend(null), Is.EqualTo(ParticleBlend.Normal));
            Assert.That(ParticleRules.ParseBlend("not-a-blend"), Is.EqualTo(ParticleBlend.Normal),
                "an unknown name falls back the way AS3's invalid blendMode does");
        }

        [Test]
        public void ParseFilter_KnowsOnlyTheTwoGlowPresets()
        {
            Assert.That(ParticleRules.ParseFilter("bur"), Is.EqualTo(ParticleFilter.Bur));
            Assert.That(ParticleRules.ParseFilter("plav"), Is.EqualTo(ParticleFilter.Plav));
            Assert.That(ParticleRules.ParseFilter(null), Is.EqualTo(ParticleFilter.None));
            Assert.That(ParticleRules.ParseFilter("cyan"), Is.EqualTo(ParticleFilter.None),
                "Emitter.fils defines exactly two, and the oracle's own lookup ignores the rest");
        }

        [Test]
        public void Spawn_BlendAndFilterAreResolvedFromTheRow()
        {
            ParticleState s = Spawn(Def(d => { d.Blend = "hardlight"; d.Filter = "bur"; }),
                                    ParticleSpec.None, new ScriptedRng(0.5f));

            Assert.That(s.Blend, Is.EqualTo(ParticleBlend.HardLight));
            Assert.That(s.Filter, Is.EqualTo(ParticleFilter.Bur));
        }

        [Test]
        public void Spawn_MirrComesFromTheSpec()
        {
            ParticleState mirrored = Spawn(Def(), new ParticleSpec { Mirr = true }, new ScriptedRng(0.5f));
            ParticleState plain = Spawn(Def(), ParticleSpec.None, new ScriptedRng(0.5f));

            Assert.IsTrue(mirrored.Mirr);
            Assert.IsFalse(plain.Mirr);
        }

        [Test]
        public void Spawn_SloyDefaultsToThree_AndIsCarried()
        {
            Assert.That(Spawn(Def(), ParticleSpec.None, new ScriptedRng(0.5f)).Sloy, Is.EqualTo(3));
            Assert.That(Spawn(Def(d => d.Sloy = 5), ParticleSpec.None, new ScriptedRng(0.5f)).Sloy, Is.EqualTo(5));
        }

        // ── Start frame ───────────────────────────────────────────────────────
        //
        // The oracle's draw sequence here is the subtlest part of the port: it draws for gotoAndStop
        // whenever the frame argument is 0, and then draws AGAIN for gotoAndPlay when anim == 2 — while
        // for anim == 1 the first draw is computed and then thrown away. Reproducing the draws, not just
        // the results, is what keeps a seeded run comparable with the oracle.

        [Test]
        public void Spawn_AnimZero_StopsOnARandomFrame()
        {
            ParticleState s = Spawn(Def(d => d.Anim = 0), ParticleSpec.None, new ScriptedRng(0.5f, 0.75f), frameCount: 4);
            Assert.That(s.DrawFrame, Is.EqualTo(3), "floor(0.75 * 4)");
            Assert.That(s.FrameIndex, Is.EqualTo(3));
        }

        [Test]
        public void Spawn_AnimOne_PlaysFromTheFrameArgument_ButStillConsumesTheDiscardedDraw()
        {
            var rng = new ScriptedRng(0.5f, 0.75f);
            ParticleState s = Spawn(Def(d => d.Anim = 1), ParticleSpec.None, rng, frameCount: 4);

            Assert.That(s.DrawFrame, Is.EqualTo(0), "gotoAndPlay(frame + 1) lands on 0-based frame 0");
            Assert.That(rng.RollsConsumed, Is.EqualTo(2),
                "the gotoAndStop draw happens and is discarded — it must still be consumed");
        }

        [Test]
        public void Spawn_AnimTwo_ConsumesTwoFrameDraws()
        {
            var rng = new ScriptedRng(0.5f, 0f, 0.75f);
            ParticleState s = Spawn(Def(d => d.Anim = 2), ParticleSpec.None, rng, frameCount: 4);

            Assert.That(s.DrawFrame, Is.EqualTo(3), "the second draw wins: floor(0.75 * 4)");
            Assert.That(rng.RollsConsumed, Is.EqualTo(3), "life + gotoAndStop + gotoAndPlay");
        }

        [Test]
        public void Spawn_SpecFrameOverridesTheRandomPick()
        {
            // A non-zero frame argument skips the gotoAndStop draw entirely, and the 1-based argument
            // becomes a 0-based index.
            var rng = new ScriptedRng(0.5f);
            ParticleState s = Spawn(Def(d => d.Anim = 0), new ParticleSpec { Frame = 3 }, rng, frameCount: 4);

            Assert.That(s.DrawFrame, Is.EqualTo(2), "AS3 frame 3 is index 2");
            Assert.That(rng.RollsConsumed, Is.EqualTo(1), "no frame draw at all");
        }

        [Test]
        public void Spawn_DframeJittersTheFrameArgument()
        {
            // `frame + floor(rand * dframe + 1)`, so the jitter is 1..dframe and never 0 — a dframe row
            // can never land back on the random pick.
            var rng = new ScriptedRng(0.5f, 0.99f);
            ParticleState s = Spawn(Def(d => d.Anim = 0), new ParticleSpec { DFrame = 3 }, rng, frameCount: 8);

            Assert.That(s.DrawFrame, Is.EqualTo(2), "frame arg 0 + floor(0.99*3 + 1) = 3, so index 2");
            Assert.That(rng.RollsConsumed, Is.EqualTo(2), "life, then the dframe jitter");
        }

        [Test]
        public void Spawn_BlitWithAnimZero_PicksARandomSheetFrame()
        {
            ParticleState s = Spawn(BlitDef(d => d.Anim = 0), ParticleSpec.None,
                                    new ScriptedRng(0.5f, 0.5f), frameCount: 8);
            Assert.That(s.DrawFrame, Is.EqualTo(4), "floor(0.5 * 8)");
            Assert.IsTrue(s.IsBlit);
        }

        [Test]
        public void Spawn_BlitLoopFramesSeedsTheCursor_AndIsNotOverwrittenWhenAnimating()
        {
            // `fire` is the only row with blitf: blit='sprFire' blitf='17' blitx='50' anim='2'.
            var rng = new ScriptedRng(0.5f, 0.5f);
            ParticleState s = Spawn(BlitDef(d => { d.BlitLoopFrames = 17; d.Anim = 2; }),
                                    ParticleSpec.None, rng, frameCount: 32);

            Assert.That(s.BlitLoopFrames, Is.EqualTo(17));
            Assert.That(s.DrawFrame, Is.EqualTo(8), "floor(0.5 * 17)");
            Assert.That(rng.RollsConsumed, Is.EqualTo(2),
                "life + the blitf draw; anim 2 on a blit row does not add a vis draw");
        }

        // ── Otklad ────────────────────────────────────────────────────────────

        [Test]
        public void Spawn_OtkladIsOneToN_AndConsumesADraw()
        {
            var rng = new ScriptedRng(0.5f, 0f);
            ParticleState s = Spawn(Def(d => d.Otklad = 15), ParticleSpec.None, rng);

            Assert.That(s.Otklad, Is.EqualTo(1), "floor(0 * 15 + 1)");
            Assert.That(s.Visible, Is.True, "the oracle's vis.visible starts true; the first step clears it");
        }

        [Test]
        public void Spawn_OtkladAtTheTopOfItsRange_IsN()
        {
            ParticleState s = Spawn(Def(d => d.Otklad = 15), ParticleSpec.None, new ScriptedRng(0.5f, 0.999f));
            Assert.That(s.Otklad, Is.EqualTo(15), "floor(0.999 * 15 + 1)");
        }

        // ── Step: integration ─────────────────────────────────────────────────

        [Test]
        public void Step_IntegratesPositionThenGravity_SoGravityLandsOnTheNextTick()
        {
            // AS3 order: X += dx; Y += dy; dy += ddy. The first tick therefore moves by the ORIGINAL
            // dy, and gravity only shows up from the second tick on.
            var def = Def(d => d.Grav = 1f);
            ParticleState s = Spawn(def, ParticleSpec.None, new ScriptedRng(0.5f));
            var budget = new ParticleBudget();

            ParticleRules.Step(ref s, 0, budget);
            Assert.That(s.Y, Is.EqualTo(200f).Within(Tolerance), "dy was 0 on the first tick");
            Assert.That(s.DY, Is.EqualTo(1f).Within(Tolerance), "gravity has been folded into dy");

            ParticleRules.Step(ref s, 0, budget);
            Assert.That(s.Y, Is.EqualTo(201f).Within(Tolerance), "the second tick moves by dy = 1");
            Assert.That(s.DY, Is.EqualTo(2f).Within(Tolerance));
        }

        [Test]
        public void Step_AFrozenParticleDoesNotMove_ButStillAgesAndDies()
        {
            ParticleState s = Spawn(Def(), ParticleSpec.None, new ScriptedRng(0.5f));
            var budget = new ParticleBudget();

            for (int i = 0; i < 5; i++) ParticleRules.Step(ref s, 0, budget);

            Assert.That(s.X, Is.EqualTo(100f).Within(Tolerance));
            Assert.That(s.Y, Is.EqualTo(200f).Within(Tolerance));
            Assert.That(s.Liv, Is.EqualTo(15), "it still ages: 20 ticks of life, 5 spent");
        }

        [Test]
        public void Step_BrakeDampsBothVelocityComponents()
        {
            var def = Def(d => { d.DX = 8f; d.DY = 0f; d.Brake = 0.5f; });
            ParticleState s = Spawn(def, ParticleSpec.None, new ScriptedRng(0.5f));
            var budget = new ParticleBudget();

            ParticleRules.Step(ref s, 0, budget);
            Assert.That(s.X, Is.EqualTo(108f).Within(Tolerance));
            Assert.That(s.DX, Is.EqualTo(4f).Within(Tolerance));

            ParticleRules.Step(ref s, 0, budget);
            Assert.That(s.X, Is.EqualTo(112f).Within(Tolerance));
            Assert.That(s.DX, Is.EqualTo(2f).Within(Tolerance));
        }

        [Test]
        public void Step_RotationAdvancesByDr()
        {
            ParticleState s = Spawn(Def(d => { d.RDR = 4f; d.DX = 1f; }), ParticleSpec.None,
                                    new ScriptedRng(1f, 0.5f));
            var budget = new ParticleBudget();

            float before = s.R;
            ParticleRules.Step(ref s, 0, budget);

            Assert.That(s.R, Is.EqualTo(before + s.DR).Within(Tolerance));
        }

        // ── Step: the alpha ladder ────────────────────────────────────────────

        [Test]
        public void Step_Alph_FadesOutOverTheLastNineTicks()
        {
            ParticleState s = Spawn(Def(d => { d.MinLiv = 20; d.Alph = true; }), ParticleSpec.None,
                                    new ScriptedRng(0.5f));
            var budget = new ParticleBudget();

            // liv 20 → 12 is the opaque stretch; the threshold is `liv < 9`.
            for (int i = 0; i < 12; i++) ParticleRules.Step(ref s, 0, budget);
            Assert.That(s.Liv, Is.EqualTo(8));
            Assert.That(s.Alpha, Is.EqualTo(1f).Within(Tolerance), "at liv 9 the particle is still opaque");

            ParticleRules.Step(ref s, 0, budget);
            Assert.That(s.Liv, Is.EqualTo(7));
            Assert.That(s.Alpha, Is.EqualTo(0.8f).Within(Tolerance), "at liv 8 the fade has begun: 8/10");
        }

        [Test]
        public void Step_PreAlph_FadesInOverTheFirstNineTicks()
        {
            ParticleState s = Spawn(Def(d => { d.MinLiv = 20; d.PreAlph = true; }), ParticleSpec.None,
                                    new ScriptedRng(0.5f));
            var budget = new ParticleBudget();

            Assert.That(s.Alpha, Is.EqualTo(0f).Within(Tolerance), "spawned invisible");

            ParticleRules.Step(ref s, 0, budget);
            Assert.That(s.Alpha, Is.EqualTo(0f).Within(Tolerance), "mliv - liv is still 0");

            ParticleRules.Step(ref s, 0, budget);
            Assert.That(s.Alpha, Is.EqualTo(0.1f).Within(Tolerance), "mliv - liv is 1");
        }

        [Test]
        public void Step_AlphaIsRestoredToOpaqueAboveTheThreshold()
        {
            // The third branch exists so a recycled pool slot cannot inherit the previous occupant's
            // alpha. Assert it directly rather than by inference.
            ParticleState s = Spawn(Def(d => { d.MinLiv = 20; d.Alph = true; }), ParticleSpec.None,
                                    new ScriptedRng(0.5f));
            var budget = new ParticleBudget();

            ParticleRules.Step(ref s, 0, budget);
            Assert.That(s.Alpha, Is.EqualTo(1f).Within(Tolerance));
        }

        // ── Step: expiry ──────────────────────────────────────────────────────

        [Test]
        public void Step_ExpiresOnTheTickLifeReachesZero()
        {
            ParticleState s = Spawn(Def(d => { d.MinLiv = 5; d.RLiv = 0; }), ParticleSpec.None,
                                    new ScriptedRng(0.5f));
            var budget = new ParticleBudget();

            for (int i = 0; i < 4; i++)
            {
                Assert.That(ParticleRules.Step(ref s, 0, budget), Is.EqualTo(ParticleStepResult.Alive),
                    $"tick {i + 1} of 5");
            }

            Assert.That(ParticleRules.Step(ref s, 0, budget), Is.EqualTo(ParticleStepResult.Expired));
            Assert.That(s.Liv, Is.EqualTo(0));
        }

        [Test]
        public void Step_ExpiryReleasesTheTypeBudgetSlot()
        {
            var def = Def(d => { d.MinLiv = 2; d.RLiv = 0; d.MaxKol = 1; });
            var budget = new ParticleBudget();
            ParticleState s = Spawn(def, ParticleSpec.None, new ScriptedRng(0.5f));
            s.MaxKol = budget.Reserve(def);

            Assert.That(budget.LiveOfType(1), Is.EqualTo(1));

            ParticleRules.Step(ref s, 0, budget);
            ParticleRules.Step(ref s, 0, budget);

            Assert.That(budget.LiveOfType(1), Is.EqualTo(0), "the slot is freed when the particle dies");
        }

        [Test]
        public void Step_CountsTowardTheBudgetTick_ButADelayedParticleDoesNot()
        {
            var budget = new ParticleBudget();
            // The otklad roll is the draw AFTER life, so a 0 gives the minimum delay of one tick.
            ParticleState s = Spawn(Def(d => d.Otklad = 15), ParticleSpec.None, new ScriptedRng(0.5f, 0f));
            Assert.That(s.Otklad, Is.EqualTo(1), "floor(0 * 15 + 1)");

            Assert.That(ParticleRules.Step(ref s, 0, budget), Is.EqualTo(ParticleStepResult.Delayed));
            Assert.That(budget.Kol1, Is.EqualTo(0), "AS3 returns before ++Emitter.kol1");

            ParticleRules.Step(ref s, 0, budget);
            Assert.That(budget.Kol1, Is.EqualTo(1), "the next tick counts normally");
        }

        [Test]
        public void Step_ADelayedParticleIsInvisibleAndDoesNotAge()
        {
            var budget = new ParticleBudget();
            ParticleState s = Spawn(Def(d => d.Otklad = 15), ParticleSpec.None, new ScriptedRng(0.5f, 0f));

            int livBefore = s.Liv;
            ParticleRules.Step(ref s, 0, budget);

            Assert.IsFalse(s.Visible);
            Assert.That(s.Liv, Is.EqualTo(livBefore), "no aging while delayed");
            Assert.That(s.Otklad, Is.EqualTo(0), "the delay is one tick, from floor(0 * 15 + 1)");
        }

        // ── Step: water ───────────────────────────────────────────────────────

        [Test]
        public void Step_WaterTwo_DiesTheMomentItLeavesWater()
        {
            var budget = new ParticleBudget();
            ParticleState s = Spawn(Def(d => { d.Water = 2; d.MinLiv = 60; }), ParticleSpec.None,
                                    new ScriptedRng(0.5f));

            Assert.That(ParticleRules.Step(ref s, tileWater: 1, budget), Is.EqualTo(ParticleStepResult.Alive));
            Assert.That(ParticleRules.Step(ref s, tileWater: 0, budget), Is.EqualTo(ParticleStepResult.Expired),
                "water=2 means 'only in water', so a dry tile kills it");
        }

        [Test]
        public void Step_WaterOne_DiesTheMomentItEntersWater()
        {
            var budget = new ParticleBudget();
            ParticleState s = Spawn(Def(d => { d.Water = 1; d.MinLiv = 60; }), ParticleSpec.None,
                                    new ScriptedRng(0.5f));

            Assert.That(ParticleRules.Step(ref s, tileWater: 0, budget), Is.EqualTo(ParticleStepResult.Alive));
            Assert.That(ParticleRules.Step(ref s, tileWater: 1, budget), Is.EqualTo(ParticleStepResult.Expired));
        }

        // ── Step: frame advance ───────────────────────────────────────────────

        [Test]
        public void Step_VisFramesAdvanceOnePerTick_AndLoop()
        {
            ParticleState s = Spawn(Def(d => { d.Anim = 1; d.MinLiv = 20; }), ParticleSpec.None,
                                    new ScriptedRng(0.5f, 0.5f), frameCount: 4);
            var budget = new ParticleBudget();

            Assert.That(s.DrawFrame, Is.EqualTo(0));

            for (int expected = 1; expected <= 3; expected++)
            {
                ParticleRules.Step(ref s, 0, budget);
                Assert.That(s.DrawFrame, Is.EqualTo(expected));
            }

            ParticleRules.Step(ref s, 0, budget);
            Assert.That(s.DrawFrame, Is.EqualTo(0), "frame 4 of 4 wraps back to 0");
        }

        [Test]
        public void Step_FrozenAnimation_NeverAdvancesTheFrame()
        {
            ParticleState s = Spawn(Def(d => { d.Anim = 0; d.MinLiv = 20; }), ParticleSpec.None,
                                    new ScriptedRng(0.5f, 0.5f), frameCount: 4);
            var budget = new ParticleBudget();

            int start = s.DrawFrame;
            for (int i = 0; i < 5; i++) ParticleRules.Step(ref s, 0, budget);

            Assert.That(s.DrawFrame, Is.EqualTo(start));
        }

        [Test]
        public void Step_BlitDrawsBeforeItAdvances_AndStopsAtTheEndOfTheSheet()
        {
            // Part.as:185-186 — `blit(floor(blitFrame)); blitFrame += blitDelta;`. The drawn frame is
            // therefore the pre-advance one, and when the cursor runs past the sheet the oracle stops
            // calling blit() entirely, leaving the last frame on screen.
            ParticleState s = Spawn(BlitDef(d => { d.Anim = 1; d.MinLiv = 20; }), ParticleSpec.None,
                                    new ScriptedRng(0.5f), frameCount: 3);
            var budget = new ParticleBudget();

            Assert.That(s.DrawFrame, Is.EqualTo(0), "the spawn frame");

            ParticleRules.Step(ref s, 0, budget);
            Assert.That(s.DrawFrame, Is.EqualTo(0), "the cursor moved to 1, but frame 0 is what gets drawn");
            ParticleRules.Step(ref s, 0, budget);
            Assert.That(s.DrawFrame, Is.EqualTo(1));
            ParticleRules.Step(ref s, 0, budget);
            Assert.That(s.DrawFrame, Is.EqualTo(2));

            ParticleRules.Step(ref s, 0, budget);
            Assert.That(s.DrawFrame, Is.EqualTo(2), "the sheet is exhausted; the last frame holds");
            Assert.That(s.BlitFrame, Is.EqualTo(3f).Within(Tolerance));
        }

        [Test]
        public void Step_BlitWrapsAtBlitLoopFrames()
        {
            // `fire` is the only row with blitf='17' against a 32-frame sheet — blitf is the LOOP
            // length, not the sheet size.
            ParticleState s = Spawn(BlitDef(d => { d.Anim = 1; d.BlitLoopFrames = 3; d.MinLiv = 20; }),
                                    ParticleSpec.None, new ScriptedRng(0.5f, 0f), frameCount: 32);
            var budget = new ParticleBudget();

            Assert.That(s.DrawFrame, Is.EqualTo(0));

            ParticleRules.Step(ref s, 0, budget);
            Assert.That(s.DrawFrame, Is.EqualTo(0));
            ParticleRules.Step(ref s, 0, budget);
            Assert.That(s.DrawFrame, Is.EqualTo(1));
            ParticleRules.Step(ref s, 0, budget);
            Assert.That(s.DrawFrame, Is.EqualTo(2));

            ParticleRules.Step(ref s, 0, budget);
            Assert.That(s.DrawFrame, Is.EqualTo(0), "wrapped at 3, well before the 32-frame sheet ends");
        }

        [Test]
        public void Step_BlitDeltaCanBeFractional()
        {
            ParticleState s = Spawn(BlitDef(d => { d.Anim = 1; d.BlitDelta = 0.5f; d.MinLiv = 20; }),
                                    ParticleSpec.None, new ScriptedRng(0.5f), frameCount: 8);
            var budget = new ParticleBudget();

            ParticleRules.Step(ref s, 0, budget);
            Assert.That(s.DrawFrame, Is.EqualTo(0), "cursor 0.5 floors to 0");
            ParticleRules.Step(ref s, 0, budget);
            Assert.That(s.DrawFrame, Is.EqualTo(0), "cursor 1.0 — drawn next tick");
            ParticleRules.Step(ref s, 0, budget);
            Assert.That(s.DrawFrame, Is.EqualTo(1), "cursor 1.5");
        }

        // ── Step: null tolerance ──────────────────────────────────────────────

        [Test]
        public void Step_ToleratesANullBudget_SoTheRulesCanBeDrivenWithoutOne()
        {
            ParticleState s = Spawn(Def(d => { d.MinLiv = 1; d.RLiv = 0; }), ParticleSpec.None,
                                    new ScriptedRng(0.5f));

            Assert.That(ParticleRules.Step(ref s, 0, null), Is.EqualTo(ParticleStepResult.Expired));
        }
    }

    /// <summary>
    /// Pins <see cref="ParticleBudget"/> — the two gates that decide whether a spawn happens at all.
    ///
    /// <para><b>These are the tests that matter most in the whole workstream</b>, because a port that
    /// gets them wrong does not crash and does not look broken: it either spawns 50 parts per pellet per
    /// tick, or it silently drops every explosion once a room is busy. Both failures are invisible in a
    /// unit test of the arithmetic and both are invisible in a quiet room.</para>
    /// </summary>
    [TestFixture]
    public class ParticleBudgetTests
    {
        private static ParticleDefinition Def(int imp = 0, int maxKol = 0) =>
            new ParticleDefinition { Id = "test", Vis = "visualTest", Imp = imp, MaxKol = maxKol };

        private static ParticleBudget BudgetWithPreviousTickCount(int count, int maxParts = ParticleBudget.DefaultMaxParts)
        {
            var budget = new ParticleBudget { MaxParts = maxParts };
            for (int i = 0; i < count; i++) budget.CountStep();
            budget.BeginTick();
            return budget;
        }

        // ── The global ceiling ────────────────────────────────────────────────

        [Test]
        public void Global_IsNotRefusedAtExactlyMaxParts()
        {
            // The oracle's comparison is `kol2 > maxParts`, not `>=`. Off-by-one here is a 1% budget
            // drift that no one would ever notice in play.
            var budget = BudgetWithPreviousTickCount(100);
            Assert.IsTrue(budget.AllowsGlobal(Def()));
        }

        [Test]
        public void Global_RefusesOnePartPastTheCeiling()
        {
            var budget = BudgetWithPreviousTickCount(101);
            Assert.IsFalse(budget.AllowsGlobal(Def()));
        }

        [Test]
        public void Global_ImpBypassesTheCeilingEntirely()
        {
            var budget = BudgetWithPreviousTickCount(5000);
            Assert.IsTrue(budget.AllowsGlobal(Def(imp: 1)),
                "imp is what keeps explosions rendering in a busy room");
        }

        [Test]
        public void Global_ReadsThePreviousTicksCount_NotThisOnes()
        {
            // `kol2 = kol1; kol1 = 0;` — a burst cannot make itself legal by draining the counter
            // mid-tick, because the gate never looks at the current tick at all.
            var budget = new ParticleBudget();
            for (int i = 0; i < 200; i++) budget.CountStep();

            Assert.IsTrue(budget.AllowsGlobal(Def()), "kol2 is still 0 — BeginTick has not run");
            budget.BeginTick();
            Assert.IsFalse(budget.AllowsGlobal(Def()), "now the previous tick's 200 is in kol2");
            Assert.That(budget.Kol1, Is.EqualTo(0), "and the current tick starts empty");
        }

        [Test]
        public void Global_UsesTheConfiguredMaxParts()
        {
            var budget = BudgetWithPreviousTickCount(11, maxParts: 10);
            Assert.IsFalse(budget.AllowsGlobal(Def()));
        }

        // ── The per-type cap ──────────────────────────────────────────────────

        [Test]
        public void Type_IsUncappedWhenMaxKolIsZero()
        {
            var budget = new ParticleBudget();
            var def = Def(maxKol: 0);

            for (int i = 0; i < 500; i++) budget.Reserve(def);
            Assert.IsTrue(budget.AllowsType(def), "maxkol 0 means no slot and therefore no cap");
        }

        [Test]
        public void Type_CapsAtTwelve()
        {
            var budget = new ParticleBudget();
            var def = Def(maxKol: 1);

            for (int i = 0; i < 11; i++)
            {
                Assert.IsTrue(budget.AllowsType(def), $"reservation {i + 1}");
                budget.Reserve(def);
            }

            Assert.IsTrue(budget.AllowsType(def), "the twelfth is still allowed");
            budget.Reserve(def);
            Assert.IsFalse(budget.AllowsType(def), "the thirteenth is not");
            Assert.That(budget.LiveOfType(1), Is.EqualTo(12));
        }

        [Test]
        public void Type_OutOfRangeSlotIsUncapped_AndDoesNotThrow()
        {
            // AS3's kols has six slots; kols[7] is undefined and `undefined >= 12` is false, so an
            // out-of-range maxkol silently means "no cap" rather than an exception.
            var budget = new ParticleBudget();
            var def = Def(maxKol: 99);

            for (int i = 0; i < 100; i++) budget.Reserve(def);

            Assert.IsTrue(budget.AllowsType(def));
            Assert.That(budget.LiveOfType(99), Is.EqualTo(0), "nothing was actually counted");
        }

        [Test]
        public void Release_FreesASlot_AndIsGuardedAgainstUnderflow()
        {
            var budget = new ParticleBudget();
            var def = Def(maxKol: 1);

            budget.Reserve(def);
            Assert.That(budget.LiveOfType(1), Is.EqualTo(1));

            budget.Release(1);
            Assert.That(budget.LiveOfType(1), Is.EqualTo(0));

            budget.Release(1);
            Assert.That(budget.LiveOfType(1), Is.EqualTo(0), "the oracle would go negative here; the port does not");
        }

        [Test]
        public void Reset_ClearsEverySlotAndCounter()
        {
            var budget = new ParticleBudget();
            var def = Def(maxKol: 1);
            budget.Reserve(def);
            budget.CountStep();
            budget.BeginTick();
            budget.NoteGlobalDrop();

            budget.Reset();

            Assert.That(budget.Kol1, Is.EqualTo(0));
            Assert.That(budget.Kol2, Is.EqualTo(0));
            Assert.That(budget.LiveOfType(1), Is.EqualTo(0));
            Assert.That(budget.DroppedByGlobalBudget, Is.EqualTo(0));
        }
    }
}
