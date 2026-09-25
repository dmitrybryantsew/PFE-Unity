using UnityEngine;

namespace PFE.Systems.Physics
{
    /// <summary>
    /// Pure arithmetic converting canonical pixels-per-30Hz-frame rates into a single motor step.
    ///
    /// <para>Extracted from <c>TilePhysicsController</c> so the numbers can be tested without a room,
    /// a GameObject or a running engine — the same reason <c>SimLoop.Advance</c> takes an explicit
    /// delta instead of reading <c>Time.unscaledDeltaTime</c>. These three functions are the entire
    /// difference between the legacy path (which runs ~2x fast) and the sim-driven path, so they are
    /// worth pinning down in tests.</para>
    ///
    /// <para>See <c>docs/Roadmap/02_P1_FIXED_SIMULATION_TICK.md</c>.</para>
    /// </summary>
    public static class TilePhysicsStepMath
    {
        /// <summary>
        /// The factor the legacy path multiplies <c>Time.fixedDeltaTime</c> by. It was written as
        /// "scale to ~60fps base", but the constants are px per <b>30</b> Hz frame, so this is the
        /// source of the 2x error: at the default 0.02 s FixedUpdate it yields 1.2 px per step at
        /// 50 steps/s = 60 px/s, against AS3's 30 px/s.
        /// </summary>
        public const float LegacyScaleFactor = 60f;

        /// <summary>
        /// Canonical 30 Hz frames of <b>position</b> advanced by one step.
        /// Legacy returns <c>fixedDeltaTime * 60</c> (the bug, preserved verbatim so the A/B is
        /// honest); sim returns <c>stepScale</c>, which is 30/rate.
        /// </summary>
        public static float PositionFramesPerStep(bool simDriven, float fixedDeltaTime, float stepScale)
        {
            return simDriven ? stepScale : fixedDeltaTime * LegacyScaleFactor;
        }

        /// <summary>
        /// Canonical 30 Hz frames of <b>rate accumulation</b> (gravity, acceleration, friction decay)
        /// applied by one step.
        ///
        /// <para>Legacy returns exactly <c>1f</c>: today these are applied once per FixedUpdate with no
        /// scaling, so at 50 Hz gravity accumulates 50 times a second instead of 30 — 1.67x too strong,
        /// on top of the 2x position error. The two errors do not cancel, which is why the game feels
        /// fast <i>and</i> heavy.</para>
        ///
        /// <para>Sim returns <c>stepScale</c>, so rates advance in proportion to the time a step
        /// covers. Semi-implicit Euler at a smaller step is not the identical trajectory to AS3's
        /// h=1, but it is the correct fixed-step form.</para>
        /// </summary>
        public static float RateFramesPerStep(bool simDriven, float stepScale)
        {
            return simDriven ? stepScale : 1f;
        }

        /// <summary>
        /// Wall-clock seconds covered by one step, for timers stored in seconds
        /// (dash duration, platform-drop duration) so their duration stops depending on the tick rate.
        /// </summary>
        public static float StepSeconds(bool simDriven, float fixedDeltaTime, float simDt)
        {
            return simDriven ? simDt : fixedDeltaTime;
        }

        /// <summary>
        /// Re-expresses a per-canonical-frame multiplicative decay over <paramref name="framesPerStep"/>
        /// frames.
        ///
        /// <para>AS3 applies <c>dx *= brake</c> exactly once per 30 Hz frame. Applying it once per step
        /// at a different tick rate would decay too fast or too slowly, because the number of steps per
        /// second changed; over <c>h</c> frames the correct factor is <c>brake^h</c>.</para>
        ///
        /// <para>Returns the factor untouched when <c>h == 1</c>, which is always true on the legacy
        /// path — so legacy behaviour is unchanged by construction.</para>
        /// </summary>
        public static float DecayOverStep(float perFrameFactor, float framesPerStep)
        {
            return framesPerStep == 1f ? perFrameFactor : Mathf.Pow(perFrameFactor, framesPerStep);
        }
    }
}
