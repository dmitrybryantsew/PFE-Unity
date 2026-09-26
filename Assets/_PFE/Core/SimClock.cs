namespace PFE.Core
{
    /// <summary>
    /// The simulation clock: canonical constants plus the configured tick rate.
    ///
    /// <para><b>The canonical unit is pixels per 30 Hz frame.</b> Every velocity and acceleration
    /// constant in PFE (AS3 <c>World.ddy = 1</c>, <c>World.maxdy = 20</c>, <c>maxdx = 8</c>,
    /// <c>jumpdy = 15</c>, <c>brake</c>, <c>accel</c>) is expressed in that unit, because the original
    /// ran on an <c>ENTER_FRAME</c> handler at <c>stage.frameRate = 30</c> with no delta time at all.
    /// AS3 <c>World.fps</c> is <b>not</b> a timestep — it is only a seconds&#8596;frames conversion for
    /// authored data (<c>culd * fps</c>, <c>t_culd / fps</c>, DPS readouts). The simulation step was
    /// implicitly one <c>ENTER_FRAME</c>.</para>
    ///
    /// <para><b>The tick rate is configurable.</b> The original game shipped a 30 FPS target, but the
    /// community build exposes an experimental multi-FPS mode (60 / 90 / 120). So the tick rate is a
    /// value, not a constant: <see cref="TicksPerSecond"/>. To keep one set of constants meaning one
    /// thing, a consumer never reads the tick rate directly — it scales canonical per-30Hz-frame rates
    /// by <see cref="StepScale"/> to get the advance for a single tick.</para>
    ///
    /// <code>
    /// // AS3:  dy += ddy;  y += dy;          (one 30 Hz frame per iteration)
    /// // Port: dy += ddy * clock.StepScale;  y += dy * clock.StepScale;
    /// </code>
    ///
    /// <para><b>What must NOT scale.</b> <see cref="StepScale"/> applies to <i>rates</i> (velocity,
    /// acceleration, friction-per-step). It does <b>not</b> apply to <i>geometric per-step limits</i>,
    /// which are distances in pixels and stay absolute at every tick rate:</para>
    /// <list type="bullet">
    ///   <item><description><c>World.maxdelta = 9</c> — max pixels a single collision sub-step may move.</description></item>
    ///   <item><description><c>porog</c> — step-up height (10 grounded / 4 airborne).</description></item>
    ///   <item><description><c>maxSubStepDistance = 9</c> — sub-step subdivision threshold.</description></item>
    /// </list>
    /// <para>Scaling those would let the player step up a taller wall at a higher tick rate, which is a
    /// behaviour change, not a timing change.</para>
    ///
    /// <para><b>Multiplayer.</b> This is a class, not a static holder, so the tick rate is owned per
    /// match rather than per process. A session agrees one rate at join; every peer steps the same
    /// tick indices. See <see cref="ISimTickable"/> for the determinism contract.</para>
    /// </summary>
    public sealed class SimClock
    {
        /// <summary>
        /// AS3 <c>World.fps</c>. The unit definition for every physics constant in the project.
        /// This is <b>not</b> the running tick rate — it never changes, and nothing may derive from
        /// a different value. See <see cref="StepScale"/>.
        /// </summary>
        public const int CanonicalTicksPerSecond = 30;

        /// <summary>
        /// The rate used when nothing configures one. Matches the original 30 FPS target, so the
        /// default build is the replica.
        /// </summary>
        public const int DefaultTicksPerSecond = 30;

        /// <summary>
        /// Spiral-of-death guard: the most simulation ticks a single rendered frame may run.
        /// If a frame takes longer than this many ticks' worth of wall time, the backlog is dropped
        /// rather than accumulated (see <see cref="SimLoop"/>).
        /// </summary>
        public const int MaxCatchupTicks = 5;

        /// <summary>
        /// Tick rates this build accepts, mirroring the community build's experimental
        /// multi-FPS options (30 default / 60 / 90 / 120).
        /// </summary>
        public static readonly int[] SupportedTicksPerSecond = { 30, 60, 90, 120 };

        private int _ticksPerSecond;

        // The rate actually asked for, kept separately because the setter snaps _ticksPerSecond to a
        // supported value. Without this, IsSupported could only ever report "true" (the snapped
        // result is by definition supported), which made it a tautology instead of the snap detector
        // its documentation promises.
        private int _requestedTicksPerSecond;

        /// <summary>Creates a clock at <see cref="DefaultTicksPerSecond"/>.</summary>
        public SimClock() : this(DefaultTicksPerSecond)
        {
        }

        /// <summary>
        /// Creates a clock at an explicit rate. An unsupported rate is snapped to the nearest
        /// supported one rather than thrown, so a bad save value or a hand-edited asset cannot
        /// brick startup — <see cref="IsSupported"/> reports whether a snap happened.
        /// </summary>
        public SimClock(int ticksPerSecond)
        {
            TicksPerSecond = ticksPerSecond;
        }

        /// <summary>
        /// Simulation ticks per wall-clock second. Setting an unsupported value snaps to the
        /// nearest supported rate.
        /// </summary>
        public int TicksPerSecond
        {
            get => _ticksPerSecond;
            set
            {
                _requestedTicksPerSecond = value;
                _ticksPerSecond = SnapToSupported(value);
            }
        }

        /// <summary>Seconds of wall time advanced by exactly one simulation tick.</summary>
        public float SimDt => 1f / _ticksPerSecond;

        /// <summary>
        /// Multiplier converting a canonical per-30Hz-frame rate into a per-tick advance.
        /// <c>30/30 = 1</c> at the canonical rate; <c>30/120 = 0.25</c> at 120 Hz.
        ///
        /// <para>Apply to rates (dx, dy, ddy, accel, brake). Do <b>not</b> apply to per-step pixel
        /// distances (maxdelta, porog, maxSubStepDistance).</para>
        /// </summary>
        public float StepScale => (float)CanonicalTicksPerSecond / _ticksPerSecond;

        /// <summary>
        /// True when running at <see cref="CanonicalTicksPerSecond"/>, i.e. one tick is one original
        /// AS3 frame and <see cref="StepScale"/> is exactly 1. This is the replica-exact mode.
        /// </summary>
        public bool IsCanonical => _ticksPerSecond == CanonicalTicksPerSecond;

        /// <summary>
        /// True when the <b>requested</b> rate was one of <see cref="SupportedTicksPerSecond"/>.
        /// False means the requested rate was snapped, which is worth surfacing once at boot.
        ///
        /// <para>This deliberately tests the requested rate, not the effective one: the setter always
        /// snaps, so <c>_ticksPerSecond</c> is <i>always</i> supported and testing it would make this
        /// property a tautology (it could never return false). Pair it with
        /// <see cref="RequestedTicksPerSecond"/> to build the "asked for 45, running at 30" message.</para>
        /// </summary>
        public bool IsSupported => System.Array.IndexOf(SupportedTicksPerSecond, _requestedTicksPerSecond) >= 0;

        /// <summary>
        /// The rate originally requested, before snapping. Equals <see cref="TicksPerSecond"/> when
        /// <see cref="IsSupported"/> is true; differs when a snap occurred.
        /// </summary>
        public int RequestedTicksPerSecond => _requestedTicksPerSecond;

        /// <summary>
        /// Snaps an arbitrary requested rate to the nearest supported one. Non-positive input
        /// returns <see cref="DefaultTicksPerSecond"/>.
        /// </summary>
        public static int SnapToSupported(int requested)
        {
            if (requested <= 0)
            {
                return DefaultTicksPerSecond;
            }

            int best = SupportedTicksPerSecond[0];
            int bestDistance = System.Math.Abs(requested - best);

            for (int i = 1; i < SupportedTicksPerSecond.Length; i++)
            {
                int distance = System.Math.Abs(requested - SupportedTicksPerSecond[i]);
                if (distance < bestDistance)
                {
                    best = SupportedTicksPerSecond[i];
                    bestDistance = distance;
                }
            }

            return best;
        }

        public override string ToString()
        {
            return IsCanonical
                ? _ticksPerSecond + " Hz (canonical / replica-exact)"
                : _ticksPerSecond + " Hz (step scale " + StepScale.ToString("0.###") + ")";
        }
    }
}
