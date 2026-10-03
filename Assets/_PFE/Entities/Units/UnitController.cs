using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using VContainer;
using PFE.Data.Definitions;
using PFE.Systems.Combat;
using PFE.Systems.Map;
using PFE.Systems.Map.TileQuery;
using PFE.Systems.Physics;
using PFE.Systems.Weapons;
namespace PFE.Entities.Units
{
    /// <summary>
    /// Base unit controller with custom physics.
    /// Replaces Unit.as from ActionScript - handles movement, collision, and physics.
    ///
    /// Key differences from AS3:
    /// - Uses Rigidbody2D in Kinematic mode for Unity collision integration
    /// - Vector2 instead of dx/dy variables
    /// - FixedDeltaTime instead of frame-based timing
    /// - Trigger-based collision instead of manual tile checking
    ///
    /// Original AS3 physics:
    /// - dx, dy for velocity
    /// - brake for friction
    /// - accel for acceleration
    /// - grav for gravity
    /// </summary>
    [RequireComponent(typeof(Rigidbody2D))]
    public class UnitController : MonoBehaviour, IDamageable, PFE.Core.ISimTickable
    {
        [Header("Configuration")]
        [SerializeField]
        protected UnitDefinition _stats;

        [Header("Debug")]
        [SerializeField]
        protected bool _showDebugInfo = false;

        // State
        protected Vector2 _velocity;
        protected bool _isGrounded;
        protected int _facingDirection = 1; // 1 = Right, -1 = Left

        // Components
        protected Rigidbody2D _rb;
        protected Collider2D _collider;
        private TilePhysicsController _cachedTilePhysics;
        private bool _hasTilePhysics;

        // ── Which clock owns this unit's step ────────────────────────────────
        //
        // Exactly ONE driver may own a unit's step, and _hasTilePhysics decides which one:
        //
        //   motor-driven  (a TilePhysicsController is present) -> the MOTOR's SimTick, at
        //                   SimTickOrder.PlayerMotor. This class contributes nothing.
        //   motor-less    (no TilePhysicsController)           -> this class, either from
        //                   SimTick at SimTickOrder.UnitsAndAi or from FixedUpdate.
        //
        // Two drivers running at once is the classic double-step bug, so both entry points read
        // the SAME predicate rather than each keeping its own copy of the rule.
        private PFE.Core.SimClock _simClock;
        private PFE.Core.SimLoop _simLoop;
        private bool _simAttached;

        /// <summary>
        /// True when <c>SimLoop</c> owns this unit's step and <see cref="FixedUpdate"/> must stand
        /// down. Mirrors <c>TilePhysicsController.SimDriven</c>; see the block comment above.
        /// </summary>
        protected bool SimDriven => _simAttached && _simClock != null;

        /// <summary>
        /// Wall-clock seconds covered by one step of this unit.
        ///
        /// <para><b>The only thing the driver changes about the maths.</b> Every rate in the
        /// motor-less step is already per <i>second</i> — <c>UnitFallPhysics</c> takes a
        /// <c>deltaTime</c> and <see cref="Move"/> multiplies velocity by one — so unlike the motor
        /// (whose AS3 constants are per 30 Hz frame) there is no frame scaling to apply here, only a
        /// different <c>dt</c>. Legacy is <c>Time.fixedDeltaTime</c>, i.e. exactly the value the
        /// expression used before this migration, so the legacy path is unchanged by construction.</para>
        ///
        /// <para>Reuses <see cref="PFE.Systems.Physics.TilePhysicsStepMath.StepSeconds"/> rather than
        /// writing the ternary again: the motor already asks the same question, and two copies of
        /// "which dt is this step" is how the two paths would silently start disagreeing.</para>
        /// </summary>
        protected float StepSeconds => PFE.Systems.Physics.TilePhysicsStepMath.StepSeconds(
            SimDriven,
            UnityEngine.Time.fixedDeltaTime,
            _simClock != null ? _simClock.SimDt : 0f);

        /// <summary>
        /// The room's tile query — AS3's <c>loc</c>, and the authority for <see cref="_isGrounded"/>.
        /// Assigned by the spawner (<c>RoomUnitSpawner</c>), which is the layer that knows the room.
        /// Null for a unit built without one (a bare test spawn), in which case the collision callbacks
        /// below remain the only source of groundedness.
        /// </summary>
        protected ITileQueryService _tileQuery;

        /// <summary>
        /// The room's prop physics layer — AS3's <c>loc.objs</c>, the list <c>Unit.checkShelf</c>
        /// iterates (<c>Unit.as:2717</c>). Assigned by the spawner alongside
        /// <see cref="SetTileQuery"/>, and null for a unit with no room.
        ///
        /// <para><b>Why a unit needs the props at all.</b> Groundedness is a tile question for flat
        /// ground, but a crate is not a tile: it is an <c>ObjectInstance</c> in a separate list that the
        /// tile query cannot see. Without this, a unit standing on a crate is ungrounded, gravity
        /// applies, and it sinks through the crate — the same failure shape as the floor bug
        /// <see cref="UnitGroundProbe"/> fixed, one layer out.</para>
        /// </summary>
        protected RoomObjectPhysicsLayer _objectPhysicsLayer;

        /// <summary>
        /// The prop this unit is standing on, and the room-local pixel height of its top edge — the
        /// resolved answer from <see cref="ResolveGroundState"/>. Null when the unit is on a tile, in
        /// the air, or has no room.
        /// </summary>
        protected ObjectInstance _groundProp;
        protected float? _groundPropSurfaceRoomLocalY;

        /// <summary>
        /// How far that prop moved last tick, in room-local pixels — AS3's <c>osndx</c>/<c>osndy</c>
        /// (<c>Unit.as:248</c>, <c>:250</c>), read from the support's <c>cdx</c>/<c>cdy</c>
        /// (<c>:2022-2023</c>) and added to the unit's motion so a unit riding a moving crate moves
        /// with it. Zero when there is no prop, or when the support moved further than
        /// <see cref="UnitCheckShelfMath.SupportFollowMaxDeltaPixels"/> in one tick (the oracle's
        /// detach, <c>:2016</c>).
        /// </summary>
        protected Vector2 _groundPropCarryPixels;

        /// <summary>
        /// Colliders currently providing upward support, keyed by the <i>other</i> collider. Only used
        /// on the callback fallback path — see <see cref="OnCollisionExit2D"/> for why a set rather than
        /// a bool.
        /// </summary>
        readonly HashSet<Collider2D> _supportingColliders = new HashSet<Collider2D>();

        // Stats (optional - subclasses like PlayerController will provide their own)
        protected UnitStats _unitStats;

        /// <summary>
        /// The port's damage authority, used by <see cref="ReportPropImpact"/>.
        /// </summary>
        /// <remarks>
        /// <para><b>Two ways in, because there are two ways a unit comes to exist.</b> The player is
        /// built from a prefab whose GameObject is listed in the scene scope's
        /// <c>autoInjectGameObjects</c>, so <c>[Inject]</c> fills this. A room unit is built by
        /// <c>RoomUnitSpawner</c> with <c>AddComponent</c>, which VContainer never sees, so the
        /// spawner has to hand it over through <see cref="SetDamageSystem"/> instead. Without the
        /// second path every spawned enemy would silently fall back to the unarmoured branch below.</para>
        /// </remarks>
#pragma warning disable CS0649 // assigned by VContainer, or by SetDamageSystem
        [Inject] private DamageSystem _damageSystem;
#pragma warning restore CS0649

        /// <summary>One-shot guard for the missing-<see cref="DamageSystem"/> warning.</summary>
        private bool _warnedMissingDamageSystem;

        // PFE physics constants (from Unit.as in AS3).
        //
        // These are no longer literals here. They were the last site of the gravity census: GRAVITY
        // was a hand-derived 30.0f, documented as "'grav' * World.ddy", which is the per-frame
        // VELOCITY idiom applied to an ACCELERATION slot — 1 px/frame² × 30 frames/s instead of
        // × 30² / 100 px-per-unit, i.e. 3.33× too strong. FRICTION_GROUND had the mirror-image
        // error: 1.0f scaled by `deltaTime * 60f`, so the frame rate was wrong (60, not AS3's 30)
        // AND the value was used as a px/frame velocity rather than a px/frame² acceleration,
        // netting 6.67× too strong. Both now come from UnitFallPhysics, which carries the AS3
        // file:line citations and is asserted directly by UnitFallPhysicsTests.
        //
        // The unit conversion is TileQueryConstants.PixelToUnit (0.01f) — never a hand-written
        // literal, which is how the port ended up with a `100f` and a `0.01f` in places that had
        // already drifted apart from the canonical pair.

        protected virtual void Awake()
        {
            _rb = GetComponent<Rigidbody2D>();
            _collider = GetComponent<Collider2D>();

            // Use Kinematic mode - we control movement manually but Unity handles collision
            _rb.bodyType = RigidbodyType2D.Kinematic;
            _rb.useFullKinematicContacts = true; // Enable collision detection
            _rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous; // Prevent tunneling

            _cachedTilePhysics = GetComponent<TilePhysicsController>();
            _hasTilePhysics = _cachedTilePhysics != null;

            ApplyDefinitionToCollider();
        }

        /// <summary>
        /// Re-register with <c>SimLoop</c> across a disable/enable cycle, so a pooled or
        /// streamed-out-then-back unit does not silently stop ticking. <c>SimLoop.Register</c>
        /// de-duplicates, so a double call is harmless.
        /// </summary>
        protected virtual void OnEnable()
        {
            if (SimDriven)
            {
                _simLoop.Register(this);
            }
        }

        /// <summary>
        /// Drop this unit's registration when it goes away.
        ///
        /// <para><b>Why this must exist.</b> <c>SimLoop</c> holds a plain <c>List&lt;ISimTickable&gt;</c>,
        /// not a Unity-aware container, so a destroyed unit left in it is a fake-null entry that
        /// <c>SimTick</c> is still called on — a <c>MissingReferenceException</c> on the first field
        /// access, once per tick, for every unit a room streaming pass destroyed. Units are destroyed
        /// on every room change, so this is the normal path, not an edge case.</para>
        ///
        /// <para><b><c>virtual</c> and <c>protected</c> deliberately.</b> A subclass that declares its
        /// own <c>private void OnDestroy()</c> <i>hides</i> this one — Unity's message dispatch finds
        /// the most-derived declaration and calls only that — so the base's cleanup would silently
        /// stop running. <see cref="PFE.Entities.Player.PlayerController"/> is exactly that case and
        /// overrides this with a <c>base.OnDestroy()</c> call. Any future subclass must do the
        /// same.</para>
        /// </summary>
        protected virtual void OnDestroy()
        {
            if (SimDriven)
            {
                _simLoop.Unregister(this);
            }
        }

        /// <summary>
        /// Size the collider from the unit definition's footprint. AS3 sizes a unit's box from its
        /// <c>&lt;phis sX sY&gt;</c> pair, so <see cref="UnitDefinition.Width"/> /
        /// <see cref="UnitDefinition.Height"/> are the port's equivalent.
        /// </summary>
        protected void ApplyDefinitionToCollider()
        {
            if (_stats == null || _collider == null)
            {
                return;
            }

            if (_collider is BoxCollider2D boxCollider)
            {
                boxCollider.size = new Vector2(_stats.Width, _stats.Height);
            }
        }

        /// <summary>
        /// Inject this unit's definition and stats — the seam a runtime spawner needs.
        ///
        /// <para><b>Why a method and not a public field.</b> <see cref="_stats"/> and
        /// <see cref="_unitStats"/> are <c>protected</c> serialized fields that <b>no code assigns
        /// anywhere in the repo</b>, and no unit prefab exists to assign them (<c>Assets/_PFE/Prefabs</c>
        /// has no subdirectories at all). Without this seam a spawned unit reads
        /// <see cref="VulnerabilityData.Neutral"/> for its <c>&lt;vulner&gt;</c> table, has no health,
        /// and logs <c>"TakeDamage called but no UnitStats assigned!"</c> on every hit — precisely the
        /// "correct-but-empty until a spawner assigns a definition" state the
        /// <see cref="Vulnerabilities"/> doc comment describes.</para>
        ///
        /// <para><b>Call it after the component exists.</b> <c>AddComponent</c> runs <see cref="Awake"/>
        /// immediately, i.e. with <c>_stats == null</c>, so the collider sizing done there is repeated
        /// here rather than assumed — otherwise every spawned unit would keep the collider's default
        /// size and a 2×2 dummy would be as wide as a raider.</para>
        /// </summary>
        public virtual void Initialize(UnitDefinition stats, UnitStats unitStats)
        {
            _stats = stats;
            _unitStats = unitStats;
            ApplyDefinitionToCollider();
            SeedEvasionFromDefinition();
        }

        /// <summary>
        /// Copy the definition's authored evasion onto the live stats — the producer for
        /// <c>@dexter</c>.
        ///
        /// <para>AS3 sets <c>dexter</c> in the <i>base</i> <c>Unit</c> constructor from the unit node
        /// (<c>Unit.as:1170-1172</c>), so it is a property of the unit, not of a subclass — which is why
        /// this lives here and not in a per-controller override. 71 units in <c>AllData.as</c> carry the
        /// attribute (up to <c>dexter='100'</c> on the stationary <c>npc</c>), and without this copy the
        /// imported value would sit on the definition and never reach the hit test — the same
        /// computed-then-dropped shape as the sprite pivot and the armoured dummy's <c>skin</c>.</para>
        ///
        /// <para>Only <c>dexter</c> is seeded. <c>dexterPlus</c> and <c>dodge</c> have no definition
        /// field because the oracle has no data for them — they are runtime, player-only values driven
        /// by the armour/RPG bridge. Seeding them here would invent a source.</para>
        ///
        /// <para><b>Data caveat.</b> Unit assets imported before <c>UnitDataImporter</c> stopped writing
        /// its defaults last carry <c>dexterity = 1</c> regardless of the template (the importer's own
        /// comment records that <c>dexter='100'</c> was being overwritten). So a stale asset reads as
        /// baseline evasion; a re-import is what makes the authored values live.</para>
        /// </summary>
        void SeedEvasionFromDefinition()
        {
            if (_stats == null || _unitStats == null)
            {
                return;
            }

            _unitStats.dexterity = _stats.dexterity;
        }

        /// <summary>
        /// The unit's evasion projection, read by the hit-avoidance test — AS3
        /// <c>Unit.dexter</c>/<c>dexterPlus</c>/<c>dodge</c>.
        ///
        /// <para>Falls back to <see cref="EvasionState.Default"/> when no stats are assigned, which is
        /// the oracle's own field defaults (dexter 1, the rest 0) — <b>not</b> all-zeroes, which would
        /// mean <c>dexter &lt;= 0</c>, i.e. "hit by everything".</para>
        /// </summary>
        public virtual EvasionState Evasion => _unitStats?.Evasion ?? EvasionState.Default;

        /// <summary>
        /// Give this unit the room's tile query, so groundedness can be answered the way AS3 answers
        /// it — <c>isLaz</c> (<c>Unit.as:1962</c>) — instead of from Unity collision callbacks.
        ///
        /// <para><b>Why this is the fix for "units fall through the floor when I walk past them".</b>
        /// A unit is seated 1 px above the tile surface, which is exactly Box2D's contact tolerance, so
        /// a resting unit's floor contact is a coin flip on float rounding. Meanwhile
        /// <c>OnCollisionExit2D</c> cleared <c>_isGrounded</c> for <b>any</b> collider — including the
        /// player, who only interpenetrates because both bodies are Kinematic. Once gravity starts on a
        /// Kinematic body, <c>MovePosition</c> is not stopped by static geometry and the unit walks out
        /// of the room. <see cref="UnitGroundProbe"/> documents the measurement;
        /// <c>ITileQueryService.IsOnGround</c> has a 10 px band and is not boundary-sensitive.</para>
        ///
        /// <para>Idempotent, and deliberately not called from <see cref="Awake"/>: <c>AddComponent</c>
        /// runs <c>Awake</c> before the spawner has a definition or a room, which is the same ordering
        /// trap <see cref="Initialize"/> exists for.</para>
        /// </summary>
        public virtual void SetTileQuery(ITileQueryService tileQuery)
        {
            _tileQuery = tileQuery;
        }

        /// <summary>
        /// Hand this unit the room's prop physics layer — AS3's <c>loc.objs</c>, the list
        /// <c>Unit.checkShelf</c> walks (<c>Unit.as:2717</c>). Assigned by the same spawner that calls
        /// <see cref="SetTileQuery"/>, because the same layer knows the room.
        ///
        /// <para>Idempotent, and a unit with no room keeps <c>null</c> here — in which case groundedness
        /// is a tile-only question, which is the correct answer for a unit standing on a bare floor.</para>
        /// </summary>
        public virtual void SetObjectPhysicsLayer(RoomObjectPhysicsLayer objectPhysicsLayer)
        {
            _objectPhysicsLayer = objectPhysicsLayer;
        }

        /// <summary>
        /// Hand this unit the port's damage authority — the seam <c>RoomUnitSpawner</c> needs, since
        /// <c>AddComponent</c> never goes through VContainer and so never satisfies
        /// <c>[Inject]</c>. See the field's remarks.
        /// </summary>
        public virtual void SetDamageSystem(DamageSystem damageSystem)
        {
            _damageSystem = damageSystem;
        }

        /// <summary>
        /// Hand this unit the fixed-step simulation, so <c>SimLoop</c> drives its step instead of
        /// Unity's <c>FixedUpdate</c>.
        ///
        /// <para><b>Why the step has to move.</b> <c>FixedUpdate</c> runs at Unity's fixed rate (50 Hz
        /// by default, so 1.67x AS3's 30) and, critically, Unity <i>catches up</i>: a frame that costs
        /// more than one fixed step runs the fixed step repeatedly inside it, with no cap but
        /// <c>Time.maximumDeltaTime</c>. Measured in the camp, that is 24-26 unit steps inside a single
        /// frame against 5 in a healthy one, so the units' own cost becomes the thing that makes the
        /// frame long — the spiral that produced 2 FPS. <c>SimLoop</c> structurally cannot do that: it
        /// clamps the accumulator to <c>SimDt * MaxCatchupTicks</c> and runs at most that many ticks
        /// per frame, dropping the rest. Moving onto it is what puts a ceiling on the worst frame.</para>
        ///
        /// <para><b>It also fixes a rate error, which is a behaviour change and is intended.</b> One
        /// tick is <c>SimClock.SimDt</c> = 1/30 s, so gravity and braking now accumulate 30 times a
        /// second rather than 50 — AS3's cadence. This is the work <see cref="ApplyGravity"/>'s remarks
        /// previously deferred by name ("moving this path onto the sim clock is separate work"); it is
        /// gated behind <c>PfeDebugSettings.SimTickUnits</c> so the two cadences can be A/B'd.</para>
        ///
        /// <para><b>Not called means the legacy path</b>, which is what every unit did before this
        /// existed. <c>MapBridge</c> is the caller, gated on the flag, and it reaches here through the
        /// spawner chain because a spawned unit is built with <c>AddComponent</c> and so never passes
        /// through VContainer.</para>
        ///
        /// <para>Idempotent: <c>SimLoop.Register</c> de-duplicates, so calling it twice — as a
        /// retroactive handover after an earlier one — registers once.</para>
        /// </summary>
        public virtual void AttachSimulation(PFE.Core.SimClock clock, PFE.Core.SimLoop loop)
        {
            if (clock == null || loop == null)
            {
                Debug.LogWarning(
                    "[PFE] UnitController.AttachSimulation called with a null clock or loop; staying " +
                    "on the legacy FixedUpdate path.", this);
                return;
            }

            _simClock = clock;
            _simLoop = loop;
            _simAttached = true;
            _simLoop.Register(this);
        }

        /// <summary>
        /// Execution order within a tick — <see cref="PFE.Core.SimTickOrder.UnitsAndAi"/>, which is
        /// after <c>PlayerMotor</c> (the player and the motor-driven units have moved) and before
        /// <c>Projectiles</c>.
        ///
        /// <para><b>Why after <c>RoomState</c>.</b> The room's own heartbeat — <c>RoomObjectPhysicsLayer</c>,
        /// which steps the props — runs at <c>SimTickOrder.RoomState</c> (10) on this same clock, so by
        /// the time a unit steps, this tick's prop motion has already been integrated. That ordering is
        /// load-bearing for the prop half of the step: <see cref="ResolveGroundState"/> reads the
        /// support's <c>cdx</c>/<c>cdy</c> carry and <see cref="SweepPropImpacts"/> reads prop
        /// velocities, and both must be this tick's numbers rather than the previous tick's.</para>
        /// </summary>
        public int TickOrder => PFE.Core.SimTickOrder.UnitsAndAi;

        /// <summary>
        /// The legacy driver: one step per Unity fixed step, for a unit with no motor and no sim.
        ///
        /// <para><b>Not <c>virtual</c>, deliberately.</b> It used to be, and
        /// <c>TrainingDummyController</c> overrode it to add its <c>hp = maxhp</c> tick — which meant
        /// the dummy's extra work ran on Unity's clock while the movement under it ran on the sim's,
        /// splitting one oracle frame across two clocks. The hook for "extra work in this unit's step"
        /// is <see cref="StepUnit"/>, which both drivers call, so a subclass cannot reintroduce that
        /// split.</para>
        /// </summary>
        private void FixedUpdate()
        {
            // A unit with a motor is owned by the motor; a sim-driven unit is owned by SimLoop. Both
            // predicates are the ones the sim path reads, so the two drivers cannot disagree about who
            // owns the step.
            if (_hasTilePhysics || SimDriven)
            {
                return;
            }

            using (PFE.Core.Profiling.PfeProfiler.Region("unit.fixedUpdate",
                "physics: one motor-less unit's step on Unity's fixed clock (ground state + gravity + friction + move + prop sweep)."))
            {
                StepUnit();
            }
        }

        /// <summary>
        /// The sim driver: one step per <c>SimLoop</c> tick, for a unit with no motor.
        /// </summary>
        /// <remarks>
        /// <para><b>A unit with a motor returns immediately.</b> The motor owns that unit's entire step
        /// (<c>TilePhysicsController.StepMotor</c>) and runs the contact countdown there, so stepping it
        /// here as well would decrement twice per step and halve every invulnerability window — a
        /// failure that presents as "the crate damages me twice as often as it should", i.e. as a
        /// damage-tuning bug rather than as a double tick. Same predicate as
        /// <see cref="FixedUpdate"/>, and the same reason.</para>
        ///
        /// <para><b>The region id differs from the legacy one on purpose.</b> The whole point of
        /// migrating is to be able to tell which driver is running, and a capture that reported
        /// <c>unit.fixedUpdate</c> for both would hide the answer. Read
        /// <c>unit.simTick</c> / <c>unit.fixedUpdate</c> call counts against <c>sim.tick</c> to confirm
        /// the flag took effect: a motor-less unit contributes one <c>unit.simTick</c> per
        /// <c>sim.tick</c>.</para>
        /// </remarks>
        public void SimTick(int tickIndex)
        {
            if (_hasTilePhysics)
            {
                return;
            }

            using (PFE.Core.Profiling.PfeProfiler.Region("unit.simTick",
                "physics: one motor-less unit's step on the sim clock. calls/calls(sim.tick) == motor-less unit count."))
            {
                StepUnit();
            }
        }

        /// <summary>
        /// One motor-less unit's step, shared by both drivers so they cannot drift apart. Its children
        /// are <c>unit.groundState</c> (tile query, then prop query) and <c>unit.propSweep</c> (the
        /// prop-impact sweep).
        ///
        /// <para><b>This is the hook a subclass overrides to add work to its own step.</b> AS3 puts
        /// such work in <c>control()</c>, which runs once per unit frame alongside <c>run()</c>;
        /// overriding a driver instead would put it on whichever clock that driver happens to be,
        /// which is the split this method exists to prevent.</para>
        /// </summary>
        protected virtual void StepUnit()
        {
            TickContactInvulnerability();

            ResolveGroundState();
            ApplyGravity();
            ApplyFriction();
            Move();

            // After the move, matching the oracle: `Box.as:632` calls attDrop at the end of the box's
            // own update, after `run()` has resolved that frame's collisions. Sweeping before the move
            // would read a velocity the step has not yet acted on.
            SweepPropImpacts();
        }

        /// <summary>
        /// Ask the room whether this unit is standing on something, before gravity reads the answer.
        ///
        /// <para>Runs every physics step rather than being event-driven, because the question is about
        /// the <i>current</i> surface under the feet, not about a contact that happened. This is what
        /// makes a stale collision exit harmless: whatever the callbacks left in
        /// <see cref="_isGrounded"/>, it is overwritten here from the tile data.</para>
        ///
        /// <para><b>Two surfaces, in the oracle's order.</b> The tile grid is asked first; only if there
        /// is no tile under the feet is a <i>prop</i> a candidate. That is AS3's order inside <c>run()</c>
        /// — the tile loop (<c>Unit.as:2317-2330</c>), then <c>checkDiagon</c>, then <c>checkShelf</c>
        /// (<c>:2340</c>) — and it is also the only sane reading: a crate sitting on a floor must not
        /// shadow the floor. The prop half is what lets a unit stand on a crate at all, since a crate is
        /// an <c>ObjectInstance</c> in a list the tile query cannot see.</para>
        ///
        /// <para>No query means no room (a unit spawned outside one) — leave
        /// <see cref="_isGrounded"/> to the collision callbacks rather than forcing it false, which
        /// would make every such unit fall. A unit with a room but no prop layer gets the tile-only
        /// answer, which is correct for a bare floor and is the state a spawner that has not been
        /// updated yet leaves it in.</para>
        /// </summary>
        protected void ResolveGroundState()
        {
            // Wraps the WHOLE body, including its early returns, so the region's self time is the
            // tile query plus the rect maths and its child is the prop query. A unit on a bare floor
            // returns before the prop query, which is why this and `phys.prop.groundQuery` have
            // different call counts in a room with a floor everywhere.
            using (PFE.Core.Profiling.PfeProfiler.Region("unit.groundState",
                "physics: 'am I standing on something' — tile query first, prop query only if no tile. Per unit per step."))
            {
            // Clear the previous answer BEFORE any early return. A unit that loses its room, its
            // collider or its prop layer must not keep a stale support alive: the carry in Move() reads
            // `_groundPropCarryPixels` unconditionally, and a stale one would drag the unit sideways
            // every step for a crate it is no longer standing on.
            _groundProp = null;
            _groundPropSurfaceRoomLocalY = null;
            _groundPropCarryPixels = Vector2.zero;

            if (_tileQuery == null || _collider == null)
            {
                return;
            }

            Rect probe = UnitGroundProbe.ToProbeRectPixels(
                _collider.bounds, TileQueryConstants.PixelToUnit);

            _isGrounded = _tileQuery.IsOnGround(probe);

            // A tile under the feet wins outright — AS3 asks the tile grid first (the `while` loop at
            // Unit.as:2317-2330, then checkDiagon, then checkShelf last at :2340), and a floor is a
            // floor. Only when there is no tile is a prop a candidate for being the ground.
            if (_isGrounded || _objectPhysicsLayer == null)
            {
                return;
            }

            // A unit moving UP is not standing on anything. AS3 gets this for free: `checkShelf` is
            // called only from the DOWNWARD branch of run() (`dy + osndy > 0`, Unit.as:2340 / :2422),
            // so a jump never consults a shelf at all. The port resolves groundedness BEFORE the step
            // and therefore has no direction to read — so it reads the velocity instead. Without this
            // gate a unit could never jump off a crate: the feet are still in the band on the tick the
            // jump starts, the prop branch would report grounded, and ApplyGravity would cancel the
            // upward velocity it is deliberately leaving alone.
            //
            // `<= 0`, not `< 0`: the resting case is `_velocity.y == 0`, and that is exactly the state
            // that has to keep reporting grounded.
            if (_velocity.y > 0f)
            {
                return;
            }

            if (!_objectPhysicsLayer.TryFindGroundPropUnder(
                    RoomLocalFeetBoundsPixels(),
                    TileQueryConstants.PorogGrounded,
                    out ObjectInstance support,
                    out float surfaceRoomLocalY))
            {
                return;
            }

            _groundProp = support;
            _groundPropSurfaceRoomLocalY = surfaceRoomLocalY;
            _isGrounded = true;

            // AS3 Unit.as:2014-2026 — the carry is the support's LAST tick displacement, and only when
            // it is small enough to still be a floor. See UnitCheckShelfMath.IsSupportStillCarrying.
            MapObjectDynamicStateData supportState = support.runtimeState?.dynamicState;
            if (supportState != null &&
                UnitCheckShelfMath.IsSupportStillCarrying(supportState.cdx, supportState.cdy))
            {
                _groundPropCarryPixels = new Vector2(supportState.cdx, supportState.cdy);
            }
            }
        }

        /// <summary>
        /// This unit's collider AABB in <b>room-local pixels</b>, with <c>yMin</c> at the feet.
        ///
        /// <para><b>Why not <see cref="UnitGroundProbe.ToProbeRectPixels"/>.</b> That rect deliberately
        /// shifts <c>yMin</c> 1 px <i>down</i> onto the tile surface, because <c>IsOnGround</c> samples a
        /// single point there. A prop query compares the feet against the prop's top edge, so it needs
        /// the feet, not the surface — handing it the probe rect would report every prop as 1 px higher
        /// than it is and, worse, hide the convention behind a helper whose name says "ground probe".
        /// Two rects, two questions.</para>
        ///
        /// <para>The room origin is <c>ITileQueryService.OriginPixel</c> — world pixels — so it is
        /// <b>subtracted</b> to reach room-local. <c>ObjectInstance.GetApproximateBounds()</c> is already
        /// room-local, and <c>Collider2D.bounds</c> is world units, so this is the one place the two
        /// spaces meet.</para>
        /// </summary>
        protected Rect RoomLocalFeetBoundsPixels()
        {
            Bounds worldBounds = _collider.bounds;
            float unitsToPixels = 1f / TileQueryConstants.PixelToUnit;
            Vector2 origin = _tileQuery != null ? _tileQuery.OriginPixel : Vector2.zero;

            float width = worldBounds.size.x * unitsToPixels;
            float height = worldBounds.size.y * unitsToPixels;

            // Rect.y IS yMin, so the feet go in the `y` argument directly.
            return new Rect(
                worldBounds.min.x * unitsToPixels - origin.x,
                worldBounds.min.y * unitsToPixels - origin.y,
                width,
                height);
        }

        /// <summary>
        /// Apply gravity if not grounded.
        /// Replaces: if (!levit) dy += World.ddy * grav (from Unit.as)
        ///
        /// <para>AS3's gate is <c>!levit &amp;&amp; this.isLaz == 0</c> (<c>Unit.as:1962</c>), where
        /// <c>isLaz</c> means "standing on something"; <see cref="_isGrounded"/> is this port's
        /// equivalent and is now resolved from the room's tile query by
        /// <see cref="ResolveGroundState"/> (the collision callbacks below are the fallback for a unit
        /// with no room). The magnitude and the terminal clamp live in
        /// <see cref="UnitFallPhysics.FallSpeed"/>, which is pure and tested — the
        /// value was the wrong part, not the shape of the branch.</para>
        ///
        /// <para><b>The cadence is no longer fixed to FixedUpdate.</b> This used to carry a "known
        /// divergence" note: it ran in <c>FixedUpdate</c> against <c>Time.fixedDeltaTime</c> (Unity's
        /// default 50 Hz) rather than on <c>SimLoop</c> at the clock's 30 Hz tick. That is now the
        /// thing <see cref="AttachSimulation"/> changes — the <c>dt</c> comes from
        /// <see cref="StepSeconds"/>, which is <c>SimClock.SimDt</c> when the sim owns the step. The
        /// census's "one value, 1 px/frame², everywhere that falls" still holds either way, because an
        /// acceleration integrated against any fixed delta produces the same speed after the same
        /// wall-clock time; only the step count per second differs.</para>
        /// </summary>
        protected void ApplyGravity()
        {
            if (_isGrounded)
            {
                // Grounded: AS3 integrates nothing while `isLaz` (Unit.as:1962), so dy does not grow.
                // Cancelling a DOWNWARD dy is the part that matters — a residual negative velocity is
                // still applied by Move(), and MovePosition on a Kinematic body is not blocked by static
                // geometry, so even a small one walks the unit into the tile it is standing on. Upward
                // velocity is left alone so a jump is not swallowed.
                if (_velocity.y < 0f)
                {
                    PushSupportDownIfUnsettled();
                    _velocity.y = 0f;
                }

                return;
            }

            _velocity.y = UnitFallPhysics.FallSpeed(_velocity.y, StepSeconds);
        }

        /// <summary>
        /// Hand the prop under the feet a share of the unit's downward speed — AS3
        /// <c>Unit.as:2736</c>, the tail of <c>checkShelf</c>. A unit landing on a crate pushes it down.
        ///
        /// <para><b>This runs where the landing is resolved, not where it is detected.</b> The oracle
        /// performs it inside <c>checkShelf</c>, which is called from the downward branch of <c>run()</c>
        /// — after <c>forces()</c> has already added this frame's gravity, and before the position
        /// update. <see cref="ResolveGroundState"/> runs <i>before</i> <see cref="ApplyGravity"/> here
        /// (it has to: gravity reads the groundedness it produces), so doing it there would use a
        /// velocity one tick stale. Doing it in the grounded branch of <see cref="ApplyGravity"/> uses
        /// the same number the oracle does — the speed the unit arrived with — and the tick's own
        /// gravity increment is skipped anyway because the unit is grounded. The residual difference is
        /// at most one frame of gravity (1 px/frame), and the port's version is the honest impact
        /// speed.</para>
        ///
        /// <para><b>Why no "just landed" latch.</b> The oracle gates this on <c>if(!_loc4_.stay)</c>, so
        /// a settled crate is not pushed and a settling one is pushed every tick until it rests. The
        /// gate does the work a latch would, and it is the oracle's own. See
        /// <see cref="UnitCheckShelfMath.MomentumShareDownwardPixelsPerSecond"/>.</para>
        ///
        /// <para><c>_loc4_.fixPlav = false</c> (<c>:2737</c>) has no port counterpart: <c>fixPlav</c> is
        /// the buoyancy-equilibrium latch (<c>Box.as:28</c>, <c>:625-628</c>, <c>:949-953</c>) and this
        /// port has no buoyancy state machine to clear. Recorded rather than invented — a field written
        /// and never read is a decoy, which is the failure this port already has four of.</para>
        /// </summary>
        void PushSupportDownIfUnsettled()
        {
            if (_groundProp == null)
            {
                return;
            }

            MapObjectDynamicStateData supportState = _groundProp.runtimeState?.dynamicState;
            if (supportState == null || supportState.stay)
            {
                return;
            }

            float unitDownwardPixelsPerSecond = -_velocity.y * TileQueryConstants.UnitToPixel;

            float share = UnitCheckShelfMath.MomentumShareDownwardPixelsPerSecond(
                unitDownwardPixelsPerSecond,
                Mass,
                _groundProp.GetAs3Massa());

            supportState.velocity = UnitCheckShelfMath.SupportVelocityAfterLanding(
                supportState.velocity, share);
        }

        /// <summary>
        /// Apply friction to slow down horizontal movement.
        /// Replaces: dx -= brake (from Unit.as)
        ///
        /// <para><b>The invented air-friction branch is gone.</b> <c>FRICTION_AIR = 0.1f</c> had no
        /// AS3 counterpart: the only damping AS3 applies outside the <c>stay</c> branch is the water
        /// branch <c>dx *= 0.5</c> (<c>Unit.as:1958</c>) and the flight branch's speed cap, neither
        /// of which is "air friction". This is the same removal the prop census made when it deleted
        /// <c>RoomObjectPhysicsLayer.AirDrag</c>, and for the same reason: a force AS3 does not have
        /// is not a tuning choice, it is a different game.</para>
        ///
        /// <para><b>Still divergent, and named rather than silently changed:</b> AS3 applies braking
        /// only while <c>stay</c> and branches on the walk input toward <c>maxSpeed</c>
        /// (<c>Unit.as:1970-1990</c>). This path has no walk input — it exists for units with no
        /// motor — so it brakes toward rest unconditionally. See
        /// <see cref="UnitFallPhysics.GroundBrake"/>.</para>
        /// </summary>
        protected void ApplyFriction()
        {
            _velocity.x = UnitFallPhysics.GroundBrake(_velocity.x, StepSeconds);
        }

        /// <summary>
        /// Apply calculated velocity to the Rigidbody.
        /// Replaces the position update logic from Unit.as step()
        /// Uses MovePosition for proper kinematic collision detection.
        /// </summary>
        protected void Move()
        {
            // AS3 wraps the whole position integration in `if(!this.fixed)` (Unit.as:1809): the
            // `forces()` and `control()` calls sit ABOVE that gate and still run, but `run()` — the
            // function holding `X += dx` — is never called, so X/Y never change. MovePosition is this
            // port's only write to position, so it is the single call the gate has to cover.
            //
            // Two consequences, both the oracle's behaviour rather than a shortcut:
            //   - a fixed unit is immune to knockback DISPLACEMENT. `otbros` has no `fixed` gate
            //     (only `invulner`), so a shot still adds to dx — the value simply never lands.
            //   - it takes no collision response, because run() also owns wall resolution
            //     (turnX / kray / wall damage) and skipping MovePosition skips the contacts.
            // Its immunity to being MOVED is therefore not a new rule layered on top; it falls out of
            // gating the same single write the oracle gates.
            if (!IsFixed)
            {
                // Read the feet BEFORE the write. MovePosition's effect on Collider2D.bounds within the
                // same step is not something to rely on, and the snap below needs an absolute
                // pre-move reference rather than a post-move one.
                float feetBeforeWorldPixelY = _collider != null
                    ? _collider.bounds.min.y * TileQueryConstants.UnitToPixel
                    : 0f;

                Vector2 deltaUnits = _velocity * StepSeconds;

                // AS3 adds the support's last-tick displacement to dx/dy BEFORE the run
                // (`X += (dx + osndx) / param1`, Unit.as:2063) so a unit riding a moving crate is
                // translated with it and is still stopped by walls. Same here: added before the
                // horizontal resolve, so the wall sweep sees the total motion rather than the unit's
                // own velocity alone. Zero when there is no support, so this is a no-op for a unit on
                // a tile or in the air.
                deltaUnits += _groundPropCarryPixels * TileQueryConstants.PixelToUnit;

                // Horizontal motion is resolved against the room's tiles; vertical is not.
                //
                // MovePosition on a Kinematic body is not blocked by static geometry, so before this
                // the only thing that ever stopped a unit was the ground probe below the feet. A unit
                // walked and was knocked straight through walls. See UnitWallMotion.
                deltaUnits.x = ResolveHorizontalMotion(deltaUnits.x);

                // AS3 snaps Y to the shelf top (`Y = _loc5_`, Unit.as:2358). The porog band is a
                // step-up allowance, not a resting height, so without this a unit can read as grounded
                // while hovering up to 10 px above the crate. Resolved as an absolute delta rather than
                // a second position write, so the unit is placed once per step.
                if (_groundProp != null && _groundPropSurfaceRoomLocalY.HasValue)
                {
                    float surfaceWorldPixelY = UnitCheckShelfMath.FeetWorldPixelY(
                        _groundPropSurfaceRoomLocalY.Value, _tileQuery.OriginPixel.y);

                    deltaUnits.y = (surfaceWorldPixelY - feetBeforeWorldPixelY) * TileQueryConstants.PixelToUnit;
                }

                _rb.MovePosition(transform.position + (Vector3)deltaUnits);
            }

            // Facing is deliberately OUTSIDE the gate. AS3 sets `storona` in control(), which runs
            // before :1809, and setVisPos()/animate() still run for a fixed unit — so a pinned turret
            // that turns to face what it is shooting at is the oracle's behaviour, not a leak. Gating
            // this would be the tempting "obviously right" move and it would be wrong.
            //
            // Update facing direction based on velocity
            if (_velocity.x > 0.1f) _facingDirection = 1;
            else if (_velocity.x < -0.1f) _facingDirection = -1;

            ApplyFacingToTransform();

            // Debug info
            if (_showDebugInfo)
            {
                Debug.DrawRay(transform.position, _velocity, Color.green);
            }
        }

        /// <summary>
        /// Sweeps this step's horizontal motion against the room's tiles and returns the distance the
        /// unit may actually travel. See <see cref="UnitWallMotion"/> for why this exists.
        /// </summary>
        /// <remarks>
        /// <para><b>Blocked means stopped, not bounced.</b> AS3 answers a wall with
        /// <c>dx = Math.abs(dx) * this.elast</c> (<c>Unit.as:2144</c> / <c>:2221</c>), and
        /// <c>elast</c> is <c>0</c> for every shipped unit — <c>Unit.as:242</c> initialises it to zero
        /// and no <c>&lt;move&gt;</c> node in <c>AllData.as</c> authors one. So the response is a dead
        /// stop, which is exactly "cancel the velocity". Only <c>dx</c> is touched: AS3's wall branch
        /// never writes <c>dy</c>, so a unit sliding down a wall keeps falling.</para>
        ///
        /// <para>Three no-op paths, all of them "there is nothing to ask": no room (a unit spawned
        /// outside one), no collider, and no horizontal motion at all — the last is worth the branch
        /// because it is the common case for a unit that is standing still or only falling.</para>
        /// </remarks>
        protected float ResolveHorizontalMotion(float deltaXUnits)
        {
            if (_tileQuery == null || _collider == null || deltaXUnits == 0f)
            {
                return deltaXUnits;
            }

            TileBox boxPx = UnitWallMotion.ToMoveBoxPixels(
                _collider.bounds, TileQueryConstants.PixelToUnit);

            UnitWallResolution resolution = UnitWallMotion.Resolve(
                _tileQuery, boxPx, deltaXUnits * TileQueryConstants.UnitToPixel);

            if (resolution.Blocked)
            {
                _velocity.x = 0f;
            }

            return resolution.AppliedDeltaXPx * TileQueryConstants.PixelToUnit;
        }

        /// <summary>
        /// Mirror the sprite for <see cref="_facingDirection"/>.
        /// Note: in PFE sprites always face right, so left is the flipped case.
        /// </summary>
        protected void ApplyFacingToTransform()
        {
            if (transform.localScale.x != _facingDirection)
            {
                transform.localScale = new Vector3(_facingDirection, 1, 1);
            }
        }

        /// <summary>
        /// Read this unit's authored placement — AS3's <c>Unit</c> constructor reading its <c>node</c>.
        ///
        /// <para><b>Facing is base behaviour, not a per-controller one.</b> <c>Unit.as:596-611</c>
        /// resolves <c>@turn</c> in the <i>base</i> constructor: positive → right, negative → left, and
        /// <b>absent → a coin flip</b> (<c>storona = this.isrnd() ? 1 : -1</c>). <c>UnitTrain.as:16-30</c>
        /// repeats all three cases, which is exactly why an earlier plan put this in the dummy — the
        /// subclass duplicates the base rather than owning the rule.</para>
        ///
        /// <para><b>The value is resolved once, at population time.</b> The coin flip needs the spawn
        /// stream, which belongs to <c>RoomPopulator</c> — the layer that generated the room — not to the
        /// presenter that draws it. So the facing arrives on <see cref="UnitInstance.facingDirection"/>
        /// and this method only applies it. See <see cref="ResolveFacing"/> for the rule.</para>
        /// </summary>
        public virtual void ApplyPlacement(UnitInstance placement)
        {
            if (placement == null)
            {
                return;
            }

            _facingDirection = placement.facingDirection;
            ApplyFacingToTransform();
        }

        /// <summary>
        /// AS3's <c>@turn</c> resolution (<c>Unit.as:596-611</c>), as a pure function so the only
        /// varying input is the coin flip the caller supplies.
        ///
        /// <para>A <b>present but non-positive</b> <c>turn</c> is not the same as an absent one: AS3
        /// only tests <c>&gt; 0</c> and <c>&lt; 0</c>, so <c>turn="0"</c> leaves <c>storona</c> at
        /// whatever it was (<c>Obj.as:24</c> initialises it to 1) and never reaches the coin flip — the
        /// flip lives in the attribute's <c>else</c> branch. Collapsing those two cases would make a
        /// <c>turn="0"</c> unit face a random direction.</para>
        ///
        /// <para><b>Parsed as a float, not an int, because AS3 compares against a coerced Number.</b>
        /// <c>@turn &gt; 0</c> on <c>"1.5"</c> is true in AS3, so an integer parse would send a
        /// fractional turn down the fall-through path instead of the positive one. Non-numeric values
        /// coerce to <c>NaN</c>, whose comparisons are both false — the same fall-through as
        /// <c>"0"</c>, which <c>float.TryParse</c> reproduces exactly.</para>
        /// </summary>
        public static int ResolveFacing(string turn, int currentFacing, PFE.Core.Rng.IRngService rng)
        {
            if (!string.IsNullOrEmpty(turn))
            {
                if (float.TryParse(turn, NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
                {
                    if (value > 0f) return 1;
                    if (value < 0f) return -1;
                }

                return currentFacing;
            }

            if (rng == null)
            {
                return currentFacing;
            }

            return rng.Chance(0.5f) ? 1 : -1;
        }

        /// <summary>
        /// Add external force (explosions, knockback, etc.).
        /// Replaces Unit.as forces() function.
        /// </summary>
        public void AddForce(Vector2 force)
        {
            _velocity += force;
        }

        /// <summary>
        /// Set horizontal velocity directly.
        /// </summary>
        public void SetVelocityX(float velocityX)
        {
            _velocity.x = velocityX;
        }

        /// <summary>
        /// Set vertical velocity directly (for jumping).
        /// </summary>
        public void SetVelocityY(float velocityY)
        {
            _velocity.y = velocityY;
        }

        /// <summary>
        /// Ground detection using collision normals — the <b>fallback</b> path, used only when this
        /// unit has no room and therefore no tile query (see <see cref="SetTileQuery"/>).
        /// Replaces the tile-based ground checking from AS3.
        /// </summary>
        private void OnCollisionEnter2D(Collision2D collision)
        {
            if (HasUpwardContact(collision))
            {
                _supportingColliders.Add(collision.collider);
                _isGrounded = true;
                _velocity.y = 0; // Stop falling
            }
        }

        private void OnCollisionStay2D(Collision2D collision)
        {
            if (HasUpwardContact(collision))
            {
                _supportingColliders.Add(collision.collider);
                _isGrounded = true;
                _velocity.y = Mathf.Min(_velocity.y, 0); // Don't fall through floor
            }
            else
            {
                // A side contact: it never made this unit grounded, so it must not keep it that way.
                _supportingColliders.Remove(collision.collider);
            }
        }

        /// <summary>
        /// Clear groundedness only when the collider that left was one of the supports.
        ///
        /// <para><b>This was unconditional, and that was a real defect.</b> Any collider leaving
        /// cleared the flag — including the player, who only interpenetrates a unit because both
        /// Rigidbody2D bodies are Kinematic (<c>m_BodyType: 1</c>) and neither can push the other. So
        /// brushing past a unit cancelled its groundedness, gravity started, and because a Kinematic
        /// body's <c>MovePosition</c> is not blocked by static geometry the unit then sank out of the
        /// room. The user's report — "I pass through them, they start to fall down" — is this line.</para>
        ///
        /// <para>A set rather than a bool, because a unit can rest on more than one collider and only
        /// the last support's exit may unground it. Entries are dropped by
        /// <see cref="OnCollisionStay2D"/> when a contact stops being upward, so a support that
        /// silently disappears cannot strand a stale entry.</para>
        /// </summary>
        private void OnCollisionExit2D(Collision2D collision)
        {
            _supportingColliders.Remove(collision.collider);

            if (_supportingColliders.Count == 0)
            {
                _isGrounded = false;
            }
        }

        /// <summary>
        /// Whether any contact in this collision has a normal pointing up — the port's stand-in for
        /// AS3's <c>isLaz</c> on the no-tile-query path.
        /// </summary>
        private static bool HasUpwardContact(Collision2D collision)
        {
            foreach (ContactPoint2D contact in collision.contacts)
            {
                if (contact.normal.y > 0.7f)
                {
                    return true;
                }
            }

            return false;
        }

        // Public getters

        public bool IsGrounded => _isGrounded;
        public Vector2 Velocity => _velocity;
        public int FacingDirection => _facingDirection;
        public UnitDefinition Stats => _stats;
        public UnitStats UnitStats => _unitStats;

        /// <summary>
        /// This unit's faction — the team id that decides who may damage whom
        /// (<see cref="PFE.Systems.Weapons.FactionRule"/>).
        ///
        /// <para>AS3 keeps it on the unit and sets the player's <i>in code</i>, not in data:
        /// <c>UnitPlayer.as:385</c> assigns <c>fraction = F_PLAYER</c>, and <c>littlepip</c> carries no
        /// <c>fraction</c> attribute at all. <see cref="PFE.Entities.Player.PlayerController"/>
        /// mirrors that with an override rather than by writing to <c>_stats</c>, which is a
        /// ScriptableObject shared by every instance of the unit — mutating it at runtime would leak
        /// a value into the project asset.</para>
        ///
        /// <para><b>Data caveat, read before relying on this for NPC-vs-NPC.</b> The imported unit
        /// assets predate the parent-chain resolution added to <c>UnitDataImporter</c>, so a spawnable
        /// that inherits its faction currently reads as <c>Raider</c> whatever its template says —
        /// monsters and robots included. Player-versus-everyone is unaffected, because the player's
        /// value comes from the override; the finer distinctions need a re-import.</para>
        /// </summary>
        public virtual FactionType Faction => _stats != null ? _stats.fraction : FactionType.Neutral;

        /// <summary>
        /// Whether this unit is player-controlled.
        /// Enemies don't degrade weapons, have different recoil, etc.
        /// </summary>
        public virtual bool IsPlayer => false;

        // === IDamageable Implementation ===

        /// <summary>
        /// Apply damage to this unit.
        /// Base implementation uses UnitStats if available.
        /// Subclasses can override for custom behavior (e.g., PlayerController).
        /// </summary>
        public virtual void TakeDamage(float damage)
        {
            if (_unitStats != null)
            {
                _unitStats.Damage(damage);

                // Handle death if applicable
                if (!IsAlive)
                {
                    RaiseDeath();
                }
            }
            else
            {
                Debug.LogWarning($"[{GetType().Name}] TakeDamage called but no UnitStats assigned!");
            }
        }

        /// <summary>
        /// Apply an already-resolved outcome: armour integrity first, then health.
        /// Base implementation delegates to <see cref="UnitStats"/>; subclasses can override.
        /// </summary>
        /// <returns><c>true</c> if this hit broke the armour.</returns>
        public virtual bool ApplyDamage(in DamageOutcome outcome)
        {
            if (_unitStats == null)
            {
                Debug.LogWarning($"[{GetType().Name}] ApplyDamage called but no UnitStats assigned!");
                return false;
            }

            bool broke = _unitStats.ApplyDamage(outcome);

            // Handle death if applicable
            if (!IsAlive)
            {
                RaiseDeath();
            }

            return broke;
        }

        /// <summary>
        /// The unit's armour projection, read by the damage resolver.
        /// <see cref="ArmourState.None"/> when unarmoured or when no stats are assigned.
        /// </summary>
        public virtual ArmourState Armour => _unitStats?.armour ?? ArmourState.None;

        /// <summary>
        /// The unit's vulnerability table, read by the damage resolver.
        /// <see cref="VulnerabilityData.Neutral"/> when nothing is assigned.
        /// </summary>
        /// <remarks>
        /// <para><b>The baseline comes from the definition; the live table comes from
        /// <see cref="UnitStats"/>.</b> That is AS3's own split, and an earlier revision of this comment
        /// collapsed it by claiming the table "belongs where the importer put it" because nothing writes
        /// it. <c>begvulner</c> is a unit's static <c>&lt;vulner&gt;</c> element
        /// (<c>Unit.as:977-984</c>); <c>vulner</c> is <i>derived</i> from it — and for the player the
        /// derivation is not the identity, because <c>Pers.armorParameters():2067</c> folds the equipped
        /// armour's <c>resist</c> in. A unit with no <see cref="UnitStats"/> has no derivation to run, so
        /// its baseline <i>is</i> its live table; that is why the fallback is the definition itself and
        /// not an error.</para>
        ///
        /// <para><b>Neutral, not identity, when there is no definition</b> — and that is the value, not a
        /// placeholder. AS3's baseline for a unit with no element is <c>1</c> everywhere except
        /// <c>emp = 0</c> (<c>Unit.as:583-590</c>), and the player has no <c>&lt;unit&gt;</c> in
        /// <c>AllData.as</c> at all, so neutral is exactly right for the player.</para>
        ///
        /// <para><b>What is live today.</b> <c>_stats</c> is a serialized field that <b>no code
        /// assigns</b>, so unless a prefab carries a reference an NPC reads
        /// <see cref="VulnerabilityData.Neutral"/> — correct-but-empty. The imported tables become live
        /// the moment a spawner (or a prefab) assigns a definition, with no change needed here.</para>
        /// </remarks>
        public virtual VulnerabilityData Vulnerabilities
        {
            get
            {
                if (_unitStats != null)
                    return _unitStats.Vulnerabilities;

                return _stats != null ? _stats.vulnerabilities : VulnerabilityData.Neutral;
            }
        }

        /// <summary>
        /// The unit's natural resistance, read by the damage resolver — AS3 <c>Unit.skin</c>.
        /// <c>0</c> when no stats are assigned, which is AS3's own default.
        /// </summary>
        /// <remarks>
        /// <para><b>Read from <see cref="UnitStats"/> and not from the definition, deliberately.</b>
        /// <c>skin</c> is not a constant: <c>UnitTrain</c> raises it for the armoured variant
        /// (<c>UnitTrain.as:41-45</c>, <c>skin = 20</c>) and <c>Unit.setLevel()</c> scales it by level
        /// (<c>Unit.as:1611</c>, <c>this.skin *= 1 + this.level * 0.05</c>). So the definition's value is
        /// the <i>baseline</i> and the live value belongs on the mutable stats object — the same split
        /// as <see cref="Armour"/> and <see cref="Vulnerabilities"/>.</para>
        ///
        /// <para><b>Nothing was reading this, which is why the armoured dummy looked unarmoured.</b>
        /// <see cref="TrainingDummyController"/> has written <c>skinResistance = 20</c> for the
        /// <c>tr='1'</c> variant since the unit slice landed, and <c>DamageCalculator</c> has applied a
        /// <c>skinResistance</c> argument for longer than that — but the only production caller passed a
        /// literal <c>0f</c>. Exposing it here is what lets <c>DamageSystem</c> read it instead.</para>
        /// </remarks>
        public virtual float SkinResistance => _unitStats?.skinResistance ?? 0f;

        /// <summary>
        /// AS3 <c>Unit.knocked</c> — this unit's susceptibility to being thrown. Authored on the
        /// definition's <c>&lt;move&gt;</c> node; AS3's own default is <c>1</c>.
        /// </summary>
        /// <remarks>
        /// <b>From the definition, not <see cref="UnitStats"/>,</b> unlike <see cref="Armour"/> and
        /// <see cref="SkinResistance"/> — nothing scales it at runtime in AS3, so there is no live copy
        /// to prefer. A unit with no definition answers <c>1</c> rather than <c>0</c>, because <c>0</c>
        /// is not "no data" here: it is the authored "cannot be moved" flag that turrets, <c>fixed</c>
        /// units and <c>UnitBossNecr</c>'s shadow all carry.
        /// </remarks>
        public virtual float Knocked => _stats != null ? _stats.knocked : 1f;

        /// <summary>
        /// AS3 <c>Unit.massa</c> — the weight divisor, already divided by 50 as AS3 does. See
        /// <see cref="UnitDefinition.Massa"/> for why the raw attribute is not this number.
        /// </summary>
        /// <remarks>
        /// Falls back to AS3's field default of <c>1</c> when no definition is assigned — the same value
        /// <see cref="KnockbackMath"/> substitutes for a non-positive mass — so a unit the spawner has
        /// not initialised yet is knocked back normally rather than not at all.
        /// </remarks>
        public virtual float Mass => _stats != null ? _stats.Massa : 1f;

        /// <summary>
        /// AS3 <c>Unit.invulner</c>, from the definition's authored flag.
        /// </summary>
        /// <remarks>
        /// <b>The runtime toggles are not modelled.</b> AS3 raises this on <c>UnitBossNecr</c> for the
        /// duration of its shadow phase and clears it after; the port has the authored
        /// <c>isInvulnerable</c> and nothing that changes it, so such a unit is either always
        /// invulnerable or never. Recorded rather than quietly approximated, because the knockback gate
        /// reads it and because it is also one half of AS3's other bullet pass-through branch.
        /// </remarks>
        public virtual bool IsInvulnerable => _stats != null && _stats.isInvulnerable;

        /// <summary>
        /// AS3 <c>Unit.fixed</c> — this unit is pinned in place and its position integration is
        /// skipped entirely (<c>Unit.as:1809</c>).
        /// </summary>
        /// <remarks>
        /// <b>What a fixed unit still does.</b> Only the <c>run()</c> call is gated. <c>forces()</c>
        /// and <c>control()</c> run above the gate, so velocity still accumulates and facing still
        /// updates; <c>checkWater()</c>, <c>actions()</c>, <c>setVisPos()</c> and <c>animate()</c> run
        /// below it. The visible result is a statue that can still aim, still take damage, and still
        /// be shot at — it just never changes position. See <see cref="UnitDefinition.isFixed"/> for
        /// the two knockback consequences and the box-wall gate at <c>:4223</c>.
        ///
        /// <para><b>From the definition, and deliberately <c>virtual</c>.</b> AS3's <c>fixed</c> is a
        /// runtime-mutable field that eleven unit subclasses flip — <c>UnitTurret.as:462</c> and
        /// <c>UnitZombie.as:455</c> clear it, <c>Unit.as:3130</c> clears it on a unit that has
        /// levitated for 75 ticks. None of those subclasses is ported, so there is no runtime writer
        /// to model today and adding a mutable backing field would be dead code. <c>virtual</c> is the
        /// hook: a ported <c>UnitTurretController</c> overrides this with its own flag rather than
        /// making the definition mutable. Same shape as <see cref="Knocked"/> and <see cref="Mass"/>,
        /// which read the definition for the same reason.</para>
        /// </remarks>
        public virtual bool IsFixed => _stats != null && _stats.isFixed;

        /// <summary>
        /// Adds a knockback impulse to this unit's velocity — AS3's <c>dx += …; dy += …</c>.
        /// </summary>
        /// <remarks>
        /// Delegates to <see cref="AddForce"/>, which is the port of AS3's <c>Unit.forces()</c> and has
        /// carried a "explosions, knockback, etc." doc comment since long before anything called it —
        /// the producer for this existed and had no consumer until now.
        /// </remarks>
        public virtual void ApplyKnockback(Vector2 impulse) => AddForce(impulse);

        // === Timed contact invulnerability — AS3 `neujaz` ===================================
        //
        // AS3's `neujaz` is the cooldown on CONTACT damage. It is not a general invulnerability and
        // it is not a health mechanic; it is what stops a crate resting on a unit, or a sword
        // sweeping through one, from applying its damage on every single frame it overlaps.
        //
        // Every gate in the oracle, exhaustively (19 sites, `grep -rn neujaz` over the AS3 scripts):
        //
        //   Unit.as:392   `public var neujaz:int = 0;`
        //   Unit.as:394   `public var neujazMax:int = 20;`
        //   Unit.as:3061  `if(this.neujaz > 0) { --this.neujaz; }`   -- inside actions()
        //   Unit.as:4131  udarUnit  -- `if(this.neujaz > 0) return false;`
        //   Unit.as:4135  udarUnit  -- `this.neujaz = this.neujazMax;`
        //   Unit.as:3273  attKorp   -- `|| param1.neujaz > 0` in the refusal (it calls udarUnit)
        //   Unit.as:4213  udarBox   -- `if(this.neujaz > 0 || this.noBox || param1.loc != loc)`
        //   Unit.as:4222  udarBox   -- `this.neujaz = this.neujazMax;`
        //   Box.as:926    attDrop   -- refuses a target that is already neujaz'd
        //   Box.as:930    attDrop   -- `_loc1_.neujaz = 12;`  (the throw grace; a LITERAL, not max)
        //   Trap.as:182/189/194      -- the same refuse-then-grant pattern for a floor trap
        //   Pers.as:381   `neujazMax:int = 30`  -- the PLAYER's value, by difficulty (:820 -> 20
        //                                          for difficulty 3, :837 -> 15 for difficulty 4)
        //   UnitPlayer.as:466 `neujazMax = this.pers.neujazMax;`
        //
        // ⚠ `udarBullet` (Unit.as:4067) has NO neujaz gate — verified by reading the function, not
        // by grep. That is why this state must NOT be folded into TakeDamage/ApplyDamage or into
        // IDamageable: those are the shared entry points a projectile also uses, so a gate there
        // would make bullets pass through a unit that had just been hit by anything else. neujaz
        // gates MELEE, CONTACT and PROP paths only.
        //
        // ⚠ One path is deliberately left un-gated, and this is a divergence: the port's melee
        // (`MeleeHitVolume`) fires on Unity's `OnTriggerEnter2D`, so it is already once-per-entry
        // rather than AS3's once-per-frame sweep. Adding this gate there would make a second swing
        // inside 20 ticks miss, which AS3 does but the port's trigger does not reproduce. Left as
        // is rather than silently changed; see the open list in TOPIC_prop_impact_damage.

        /// <summary>
        /// AS3 <c>Unit.neujaz</c> (<c>Unit.as:392</c>) — remaining ticks of contact invulnerability.
        ///
        /// <para><b>A frame counter, and it stays one.</b> <see cref="PFE.Core.ISimTickable"/>'s rule
        /// is explicit — "do not convert AS3 frame counts into seconds inside a tick" — so this
        /// decrements by 1 per unit step and is never scaled by <c>deltaTime</c>. Its wall-clock
        /// duration therefore depends on the step rate, exactly as every other counter in this class
        /// does.</para>
        /// </summary>
        protected int _contactInvulnerabilityTicks;

        /// <summary>
        /// AS3 <c>Unit.neujazMax</c> (<c>Unit.as:394</c>) — the value a landed contact hit writes into
        /// <see cref="_contactInvulnerabilityTicks"/>.
        /// </summary>
        /// <remarks>
        /// <para><b>AS3's field default is 20, and that is what a non-player unit uses.</b> The
        /// <i>player</i> does not: <c>UnitPlayer.as:466</c> copies <c>Pers.neujazMax</c>, which is
        /// <c>30</c> at <c>Pers.as:381</c> and is then rewritten by difficulty — <c>20</c> at
        /// <c>:820</c> (difficulty 3) and <c>15</c> at <c>:837</c> (difficulty 4). So the player's
        /// window is difficulty-dependent and an NPC's is a flat 20. <c>virtual</c> is the hook: a
        /// ported <c>PlayerController</c> overrides this with its difficulty value rather than this
        /// class learning about difficulties.</para>
        ///
        /// <para>Note it is <b>not</b> the throw grace. <c>Box.as:930</c> assigns a literal <c>12</c>
        /// for that, which is a different window and 40% shorter.</para>
        /// </remarks>
        protected virtual int ContactInvulnerabilityMaxTicks => ContactInvulnerabilityMath.DefaultMaxTicks;

        /// <summary>
        /// Whether this unit is currently refusing contact damage — AS3's <c>neujaz &gt; 0</c>.
        ///
        /// <para>Read by the contact/prop paths before they apply anything. It must <b>not</b> be
        /// consulted by anything that models a projectile: see the block comment above
        /// <see cref="_contactInvulnerabilityTicks"/> for why.</para>
        /// </summary>
        public virtual bool IsContactInvulnerable => ContactInvulnerabilityMath.IsActive(_contactInvulnerabilityTicks);

        /// <summary>
        /// Grant <paramref name="ticks"/> of contact invulnerability — AS3's <c>neujaz = …</c>.
        /// </summary>
        /// <remarks>
        /// <b>Assigns, it does not take a maximum.</b> AS3 writes the field directly at every grant
        /// site (<c>Unit.as:4135</c>, <c>:4222</c>, <c>Box.as:930</c>), so a shorter grant would
        /// <i>shorten</i> a longer window already running. That is safe in the oracle only because
        /// every grant site is guarded by a <c>neujaz &gt; 0</c> refusal immediately above it, so a
        /// grant is always from zero. This method does not repeat that guard — the caller owns it,
        /// exactly as in the oracle — so a caller that grants unconditionally can truncate a window.
        /// </remarks>
        public virtual void GrantContactInvulnerability(int ticks)
        {
            _contactInvulnerabilityTicks = ticks;
        }

        /// <summary>
        /// One tick of the contact-invulnerability countdown — AS3 <c>Unit.actions()</c>
        /// (<c>Unit.as:3061-3064</c>).
        /// </summary>
        /// <remarks>
        /// <para><b>Call it once per unit step, from whichever driver owns the step.</b> The oracle's
        /// <c>actions()</c> runs once per frame from <c>Unit.as:1827</c>, <i>after</i> the movement
        /// block (<c>:1809-1825</c>) — so a unit hit during its own move is decremented in the same
        /// frame. This port ticks the counter <i>before</i> the step's own logic instead, which grants
        /// the full window rather than window-minus-one. Recorded rather than hidden: the difference
        /// is a single tick, and it depends on room object order in the oracle anyway, since a box's
        /// <c>attDrop</c> runs in the box's frame and the unit's <c>actions()</c> in the unit's.</para>
        ///
        /// <para><b>Both drivers must call it exactly once.</b> The player's step is owned by
        /// <c>TilePhysicsController.StepMotor</c>; an NPC's by <see cref="SimTick"/> when the sim owns
        /// the step and by <see cref="FixedUpdate"/> when it does not. Each calls this from its own
        /// path and no unit is stepped by two of them, so a unit cannot be decremented twice in one
        /// step — which would halve every window.</para>
        /// </remarks>
        public virtual void TickContactInvulnerability()
        {
            _contactInvulnerabilityTicks = ContactInvulnerabilityMath.Tick(_contactInvulnerabilityTicks);
        }

        // === Prop impacts — AS3 Box.attDrop (Box.as:914-940) + Unit.udarBox (Unit.as:4209-4240) ===
        //
        // End to end the feature is: a prop moving faster than sqrt(50) ~ 7.07 px/frame that overlaps a
        // unit hits it ONCE, for `massa * (vel2 - 50) * 0.2`, and the unit is then immune to contact
        // for `neujazMax` ticks. The halves live in RoomObjectPhysicsLayer.TryFindImpactingPropFor
        // (which prop) and here (what happens to it and to the unit).
        //
        // ⚠ It is driven from the UNIT and not from the prop, which inverts the oracle's loop nest. See
        // TryFindImpactingPropFor for why, and for why "the hardest overlapping prop" is equivalent to
        // AS3's "every overlapping prop".
        //
        // ⚠ Two oracle branches are deliberately NOT ported, both because their inputs are not
        // imported — recorded rather than silently dropped:
        //   * `Unit.as:4217-4221` — `if(param1.molnDam > 0) { this.damage(param1.molnDam, D_SPARK);
        //     return 1; }`. A prop carrying `moln` (lightning) damage zaps instead of crushing, does
        //     NOT grant neujaz and does NOT exchange momentum. The data has one such prop
        //     (`AllData.as`: `id='moln1' … moln='400' period='40' wall='2'`), and `moln` is not in
        //     MapObjectDefinition; the periodic discharge it also drives (`Box.as:554-569` ->
        //     `attMoln()`) is the larger half of that feature and is unported too.
        //   * `Unit.as:4238` — `this.priorUnit = null;`. `priorUnit` is written at `:4205` by
        //     `udarUnit` and read by nothing the port models.

        /// <summary>
        /// This unit's velocity in AS3's own units — <b>pixels per 30 Hz frame</b> — whichever
        /// component actually owns it.
        /// </summary>
        /// <remarks>
        /// <b>Two homes, one question.</b> A unit with a motor (<c>TilePhysicsController</c>) keeps its
        /// velocity there, already in px/frame; a unit without one keeps it in <see cref="_velocity"/>,
        /// in Unity units per second. The oracle's momentum exchange (<c>Unit.as:4225-4230</c>) is in
        /// px/frame, so the conversion has to happen on exactly one side of this property and not the
        /// other. Putting the choice here means a wrong unit is a compile-time mismatch at the call
        /// site rather than a silent 0.3× or 3.33×.
        /// </remarks>
        protected virtual Vector2 As3VelocityPixelsPerFrame
        {
            get
            {
                if (_cachedTilePhysics != null)
                {
                    return _cachedTilePhysics.VelocityPixelsPerFrame;
                }

                return _velocity / TileQueryConstants.PerFrameVelocityToUnitsPerSecond;
            }
            set
            {
                if (_cachedTilePhysics != null)
                {
                    _cachedTilePhysics.VelocityPixelsPerFrame = value;
                    return;
                }

                _velocity = value * TileQueryConstants.PerFrameVelocityToUnitsPerSecond;
            }
        }

        /// <summary>
        /// Run this unit's side of AS3 <c>Box.attDrop</c> (<c>Box.as:914-940</c>) and
        /// <c>Unit.udarBox</c> (<c>Unit.as:4209-4240</c>): find the hardest prop overlapping this unit
        /// that is moving fast enough to hurt, and either damage the unit with it or hand it the throw
        /// grace.
        ///
        /// <para><b>Call once per unit step, from the driver that owns the step</b> — after the move,
        /// matching the oracle, where <c>attDrop</c> runs at the end of the box's own update
        /// (<c>Box.as:632</c>) and after <c>run()</c> has resolved the frame's collisions. Running it
        /// before the move would read a velocity the step has not yet acted on.</para>
        ///
        /// <para><b>Safe to call for a unit with no room or no props.</b> Both are ordinary states
        /// (a unit spawned outside a room, a room with nothing dynamic in it) and both return
        /// immediately, so no caller needs to pre-check.</para>
        /// </summary>
        public virtual void SweepPropImpacts()
        {
            // Wraps the whole sweep so its self time is the grants/momentum exchange and its child is
            // `phys.prop.impactQuery`. The two early returns (no layer, contact-invulnerable) are
            // INSIDE the region on purpose: a sweep that is skipped every step is a different finding
            // from one that runs every step, and only the call count distinguishes them.
            using (PFE.Core.Profiling.PfeProfiler.Region("unit.propSweep",
                "physics: the 'boxes damage units' sweep (Unit.udarBox) — once per unit per step."))
            {
            if (_objectPhysicsLayer == null || _collider == null)
            {
                return;
            }

            // AS3 `Box.as:926` — `_loc1_.neujaz > 0` refuses the hit. Checked before the query so an
            // already-immune unit never pays for the scan, and so the throw-grace grant below can never
            // be immediately overwritten by a damage grant in the same step.
            if (IsContactInvulnerable)
            {
                return;
            }

            if (!_objectPhysicsLayer.TryFindImpactingPropFor(
                    RoomLocalFeetBoundsPixels(),
                    out ObjectInstance prop,
                    out float velocitySquaredPixelsPerFrame))
            {
                return;
            }

            MapObjectDynamicStateData propState = prop.runtimeState?.dynamicState;
            if (propState == null)
            {
                return;
            }

            // AS3 `Box.as:928-931` — a prop still inside its throw window does not damage. It hands the
            // unit the fixed 12-tick grace instead, and crucially does NOT clear the prop's own
            // `isThrow`: a crate still in flight keeps its grace for every unit it passes through.
            if (propState.isThrown)
            {
                GrantContactInvulnerability(PropImpactMath.ThrowGraceInvulnerabilityTicks);
                return;
            }

            float propMassaAs3 = prop.GetAs3Massa();
            Vector2 propVelocityPerFrame = PropImpactMath.ToPixelsPerFrame(propState.velocity);

            PropImpactExchange exchange = PropImpactMath.ResolveExchange(
                As3VelocityPixelsPerFrame,
                Mass,
                Knocked,
                IsFixed,
                propVelocityPerFrame,
                propMassaAs3);

            // Both sides are written back, at `Unit.as:4227-4235`, BEFORE the damage at `:4237` — the
            // order matters only because the damage must not be able to read the post-exchange
            // velocity, which it cannot in any case since `vel2` was captured at `Box.as:917`.
            As3VelocityPixelsPerFrame = exchange.UnitVelocityPixelsPerFrame;
            propState.velocity = PropImpactMath.FromPixelsPerFrame(exchange.PropVelocityPixelsPerFrame);

            // AS3 `Box.as:934` — `this.isThrow = false`. A prop that has landed a damaging hit stops
            // being a throw, so it damages the next thing it touches rather than granting grace.
            propState.isThrown = false;

            // AS3 `Unit.as:4222`. Granted before the damage is reported so a re-entrant sweep in the
            // same step cannot double-hit.
            GrantContactInvulnerability(ContactInvulnerabilityMaxTicks);

            // AS3 `Unit.as:4237`. The `vel2` is the one the query's gate accepted — pre-exchange, which
            // is what the oracle reads; see PropImpactMath.ImpactDamage.
            ReportPropImpact(PropImpactMath.ImpactDamage(propMassaAs3, velocitySquaredPixelsPerFrame));
            }
        }

        /// <summary>
        /// Hand a prop impact's damage to the port's damage authority.
        /// </summary>
        /// <remarks>
        /// <para><b>Why not <see cref="TakeDamage"/>.</b> <c>UnitStats.Damage(amount)</c> is a raw HP
        /// subtraction: no vulnerability table, no <c>skin</c>, no armour pool. AS3's
        /// <c>Unit.damage()</c> applies all three — and does so on the <c>param3 == null</c> path a
        /// prop impact takes (<c>:3527-3530</c>, <c>:3611-3637</c>). Routing a crate through
        /// <c>TakeDamage</c> would make it hurt an armoured unit exactly as much as a bare one, which
        /// is the kind of silent wrongness this port keeps finding.</para>
        ///
        /// <para><b>Why <see cref="PendingDamage.Contact"/> and not <c>Direct</c>.</b> <c>udarBox</c>
        /// calls <c>damage()</c> directly, so the hit takes neither the avoidance roll nor the
        /// ±30% damage spread that <c>udarBullet</c> applies (<c>:4085</c>). <c>Direct</c> would take
        /// both.</para>
        /// </remarks>
        protected virtual void ReportPropImpact(float damage)
        {
            if (damage <= 0f)
            {
                return;
            }

            if (_damageSystem != null)
            {
                _damageSystem.Report(PendingDamage.Contact(
                    DamageContext.Contact(damage, DamageType.PhysicalMelee),
                    this,
                    transform.position));
                return;
            }

            // Unarmoured fallback. Loud rather than silent: an unarmed fallback that looks like a
            // working feature is how a missing injection survives to release. Same shape as
            // MeleeHitVolume's.
            if (!_warnedMissingDamageSystem)
            {
                _warnedMissingDamageSystem = true;
                Debug.LogWarning(
                    $"[{GetType().Name}] prop impact has no DamageSystem, so it is being applied as " +
                    "raw HP damage — no armour, no vulnerabilities. The spawner must call " +
                    "SetDamageSystem, or the unit's GameObject must be in the scene scope's " +
                    "autoInjectGameObjects.", this);
            }

            TakeDamage(damage);
        }

        /// <summary>
        /// Current health from stats.
        /// Returns 0 if no stats assigned.
        /// </summary>
        public virtual float CurrentHealth => _unitStats?.CurrentHp.Value ?? 0f;

        /// <summary>
        /// Maximum health from stats.
        /// Returns 1 if no stats assigned (to avoid divide by zero).
        /// </summary>
        public virtual float MaxHealth => _unitStats?.MaxHp.Value ?? 1f;

        /// <summary>
        /// Whether this unit is alive.
        /// Returns false if no stats assigned.
        /// </summary>
        public virtual bool IsAlive => _unitStats?.IsAlive ?? false;

        /// <summary>
        /// Whether this instance is allowed to act on a death it has just observed.
        ///
        /// <para><b>This is the multiplayer authority seam, and it is unconditionally <c>true</c>
        /// today</b> — the project has no networking layer (no netcode package, no
        /// <c>NetworkBehaviour</c>). It exists so the decision has a name and exactly one home:
        /// <see cref="RaiseDeath"/> is the only place a death is acted on, so a host-authoritative
        /// pass has one member to override rather than three call sites to hunt down.</para>
        ///
        /// <para><b>Why it is not cosmetic.</b> <see cref="OnDeath"/> awards XP to the player
        /// (<c>AS3 Unit.as:4397</c>), and subclasses drop loot and play death animations — all
        /// shared-world facts. In a host-authoritative session a client that ran the same damage code
        /// locally would award itself XP for a kill the host never agreed to.</para>
        ///
        /// <para><b>Scope, stated honestly.</b> This gates the <i>reaction</i>, not the whole rule. The
        /// complete rule is that damage is applied on the host at all, which would be gated at
        /// <see cref="TakeDamage"/>, <see cref="ApplyDamage"/> and
        /// <c>CharacterStats.ApplyOrganDamage</c>. Those are deliberately left ungated: gating them now
        /// changes single-player behaviour for no benefit, and where the split falls is still an open
        /// design decision.</para>
        /// </summary>
        protected virtual bool IsAuthoritativeForDeath => true;

        /// <summary>
        /// The single funnel for "this unit has died". Every death path goes through here, so the
        /// authority check cannot be forgotten at a new call site.
        ///
        /// <para>Deliberately <i>not</i> named <c>OnDeath</c>: <see cref="OnDeath"/> is the overridable
        /// reaction, and a subclass that overrode it without calling <c>base</c> would bypass this
        /// gate.</para>
        /// </summary>
        /// <returns><c>true</c> when the death was acted on.</returns>
        protected bool RaiseDeath()
        {
            if (!IsAuthoritativeForDeath) return false;
            OnDeath();
            return true;
        }

        /// <summary>
        /// Called when this unit dies.
        /// Base implementation logs death. Subclasses can override for death effects.
        /// </summary>
        protected virtual void OnDeath()
        {
            Debug.Log($"[{GetType().Name}] has died!");

            // Reward XP to player (AS3 Unit.as:4397 loc.takeXP(this.xp, X, Y, true))
            if (_stats != null && _stats.xpReward > 0)
            {
                var player = FindFirstObjectByType<PFE.Entities.Player.PlayerController>();
                if (player != null && player.CharacterStats != null)
                {
                    player.CharacterStats.AddXp(_stats.xpReward, transform.position.x, transform.position.y);
                }
            }

            // Base class doesn't destroy the GameObject.
            // Subclasses can override to play death animations, drop loot, etc.
        }
    }
}
