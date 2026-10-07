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
    /// An <see cref="ISimTickable"/> that is <b>always on</b>: a container-registered service that wants
    /// to be on <see cref="SimLoop"/> for the whole session, with no lifecycle of its own.
    ///
    /// <para><b>Why this marker exists rather than plain <see cref="ISimTickable"/> discovery.</b>
    /// <c>SimTickRegistrar</c> registers every tickable it can see, so "who is on the loop" has to be a
    /// deliberate declaration. Of the twelve <see cref="ISimTickable"/> implementers in the project,
    /// <b>nine register themselves dynamically</b> — per-instance, and unregistering again:</para>
    /// <list type="bullet">
    /// <item><description><c>Projectile</c>, <c>ThrownObject</c>, <c>UnitController</c> — one per spawned
    /// object, unregistered on despawn.</description></item>
    /// <item><description><c>TilePhysicsController</c> — attached by the room, re-registered
    /// <c>OnEnable</c> across pooling and room streaming.</description></item>
    /// <item><description><c>PlayerManaTicker</c>, <c>PlayerActionInteractor</c>,
    /// <c>PlayerSpellCaster</c> — registered on attach, unregistered in <c>OnDestroy</c> (a dead
    /// <c>MonoBehaviour</c> left on the loop ticks forever, which presents as "the sim gets slower the
    /// longer the session runs").</description></item>
    /// </list>
    /// <para>And <c>GameLoopManager</c> registers <b>only when <c>SimTickRoom</c> is on</b> — with the flag
    /// off it deliberately stays on the per-frame <c>ITickable</c> path. Auto-registering it would silently
    /// flip a documented opt-in behaviour switch for every session.</para>
    ///
    /// <para>So discovery by <see cref="ISimTickable"/> alone is wrong: it would catch
    /// <c>GameLoopManager</c> and turn a debug flag into a lie. The marker makes the always-on set
    /// explicit, which is the whole point — it replaces "remember to write a driver" with a declaration
    /// the compiler enforces.</para>
    /// </summary>
    public interface IAutoRegisteredSimTickable : ISimTickable
    {
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

        /// <summary>
        /// Advance the particle population (age, move, expire, release budget slots) <b>before</b> any
        /// system that emits.
        ///
        /// <para><b>Not an optimisation — it is the oracle's frame model.</b> In AS3 a <c>Part</c> is a
        /// MovieClip and its <c>ENTER_FRAME</c> listener fires on the <b>next</b> frame, so a particle
        /// cast during a tick is not stepped until the following one. Emitters live all over the tick
        /// (projectiles at <see cref="Projectiles"/>, impact blood in <see cref="Damage"/>, effects in
        /// <see cref="UnitsAndAi"/>), so the population has to be rolled and stepped ahead of all of
        /// them. A port that stepped after the emitters would advance every fresh particle one tick
        /// early, and the only symptom would be a burst that looks very slightly too fast.</para>
        ///
        /// <para>Sits after <see cref="Input"/> (nothing here reads input) and before
        /// <see cref="RoomState"/>, so a room change that clears the population does so after the step
        /// that belonged to the old room.</para>
        /// </summary>
        public const int PreTick = 5;

        /// <summary>Room / streaming state — entities need a valid room before they can move.</summary>
        public const int RoomState = 10;

        /// <summary>Player motor: integrate velocity, resolve tile collision. Player moves first.</summary>
        public const int PlayerMotor = 20;

        /// <summary>
        /// A <b>spawned unit's</b> motor — <c>TilePhysicsController</c> when it is driving an NPC
        /// rather than the player.
        ///
        /// <para><b>Why not <see cref="PlayerMotor"/>.</b> Every motor used to report
        /// <see cref="PlayerMotor"/>, so "the player moves first" was true only by registration luck:
        /// the player's motor and N units' motors shared one order value, and
        /// <see cref="SimLoop"/>'s tie-break is registration order. That is legal but it is not the
        /// contract — AS3 steps the player and then the units — and it stops being harmless the moment
        /// a unit's step can read or write anything the player's step touched. Giving the NPC motor its
        /// own slot makes the ordering structural instead of incidental.</para>
        ///
        /// <para>Sits after <see cref="PlayerMotor"/> and before <see cref="UnitsAndAi"/>: the motor is
        /// the integration half of a unit's frame, and a brain's decision must be applied by it in the
        /// <i>same</i> tick (AS3 runs <c>control()</c> then <c>run()</c> in one pass), so a future
        /// brain belongs in a slot at or below this one — never above it.</para>
        /// </summary>
        public const int UnitMotor = 25;

        /// <summary>Other units and AI.</summary>
        public const int UnitsAndAi = 30;

        /// <summary>
        /// The LowLevelPhysics2D world's step — its own slot, so write → step → read is explicit.
        ///
        /// <para><c>PhysicsWorldService</c> is the world's single owner and was originally registered
        /// in <see cref="Projectiles"/>, the slot of its first consumer. One
        /// <see cref="ISimTickable"/> occupies one slot, so the world's <c>Simulate()</c> and a
        /// consumer's sweep shared an order value and were separated only by registration order. That
        /// is harmless while the mirror is static — a sweep is a pure query, so the step cannot change
        /// what it sees — but it becomes load-bearing the moment a dynamic body enters the world: a
        /// body read before it was written, or written after the consumer read it, is a one-tick lag
        /// that will be misdiagnosed as physics tuning.</para>
        ///
        /// <para>Sits after <see cref="UnitsAndAi"/> (the last systems that will write bodies) and
        /// before <see cref="Projectiles"/> (the first that reads them), so the bracket is explicit
        /// rather than incidental.</para>
        /// </summary>
        public const int PhysicsWorld = 35;

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
