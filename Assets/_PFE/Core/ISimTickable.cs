namespace PFE.Core
{
    /// <summary>
    /// A participant in the authoritative simulation step.
    ///
    /// <para>Called exactly <see cref="SimClock.TicksPerSecond"/> times per wall-clock second from
    /// <see cref="SimLoop"/>, always in ascending <see cref="TickOrder"/>. One call = one tick =
    /// one atomic step of game state. Nothing else may mutate authoritative state.</para>
    ///
    /// <para><b>Determinism contract (required for multiplayer).</b> Given the same
    /// <paramref name="tickIndex"/>, the same prior state, and the same gathered inputs, an
    /// implementation must produce the same result on every peer. In practice that means an
    /// implementation must NOT read:</para>
    /// <list type="bullet">
    ///   <item><description><c>Time.deltaTime</c> / <c>Time.fixedDeltaTime</c> / <c>Time.time</c> — the
    ///     tick index is the only time source. Derive per-tick advance from
    ///     <see cref="SimClock.StepScale"/>.</description></item>
    ///   <item><description><c>Input</c> / <c>InputReader</c> — input is gathered once on the frame
    ///     boundary into a snapshot struct and handed to the tick.</description></item>
    ///   <item><description><c>Transform</c> — the sim owns position in pixel space; a separate view
    ///     pass copies it out, interpolated by <see cref="SimLoop.Alpha"/>.</description></item>
    ///   <item><description><c>UnityEngine.Random</c> — a seeded, tick-indexed RNG only. (Seeded RNG
    ///     lands in P3; until then keep existing calls isolated so the swap is mechanical.)</description></item>
    /// </list>
    ///
    /// <para>Frame counters stay frames. Do not convert AS3 frame counts into seconds inside a tick —
    /// a 60-frame cooldown is 60 ticks. <c>CombatCalculator.FramesToSeconds</c> exists for the cases
    /// where a <i>display</i> value genuinely needs seconds.</para>
    /// </summary>
    public interface ISimTickable
    {
        /// <summary>
        /// Advances the simulation by exactly one tick.
        /// </summary>
        /// <param name="tickIndex">
        /// Monotonic tick index, starting at 0 for a session and never reset. The only time source a
        /// tick may use; also the seed input for deterministic RNG.
        /// </param>
        void SimTick(int tickIndex);

        /// <summary>
        /// Execution order within a tick. Lower runs first. Ordering must be total and stable;
        /// registration order breaks ties, so equal values are legal but discouraged.
        /// </summary>
        int TickOrder { get; }
    }

    /// <summary>
    /// Canonical <see cref="ISimTickable.TickOrder"/> values. Spacing of 10 leaves room to insert a
    /// system without renumbering the rest. Ordering rationale is the dependency chain: inputs feed
    /// movement, movement feeds collision, collision feeds triggers and destruction.
    /// </summary>
    public static class SimTickOrder
    {
        /// <summary>Snapshot input into a struct. Must precede everything that consumes it.</summary>
        public const int Input = 0;

        /// <summary>Room / streaming state — entities need a valid room before they can move.</summary>
        public const int RoomState = 10;

        /// <summary>Player motor: integrate velocity, resolve tile collision. Player moves first.</summary>
        public const int PlayerMotor = 20;

        /// <summary>Other units and AI.</summary>
        public const int UnitsAndAi = 30;

        /// <summary>Projectiles — need final positions from this tick.</summary>
        public const int Projectiles = 40;

        /// <summary>Weapons: cooldowns, jam, reload. Frame counters.</summary>
        public const int Weapons = 50;

        /// <summary>Damage resolution — after every damage source has moved.</summary>
        public const int Damage = 60;

        /// <summary>Triggers and doors — after movement has settled.</summary>
        public const int Triggers = 70;

        /// <summary>Destruction / tile state changes — may invalidate earlier queries.</summary>
        public const int Destruction = 80;

        /// <summary>Spawn / despawn. End of tick so spawns are visible to the next tick.</summary>
        public const int SpawnDespawn = 90;

        /// <summary>View-only systems that still want tick-aligned cadence. Always last.</summary>
        public const int ViewAligned = 100;
    }
}
