using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using VContainer;
using PFE.Data.Definitions;
using PFE.Systems.Combat;
using PFE.Systems.Effects;
using PFE.Systems.Inventory;
using PFE.Systems.Loot;
using PFE.Systems.Map;
using PFE.Systems.Map.TileQuery;
using PFE.Systems.Physics;
using PFE.Systems.Particles;
using PFE.Systems.Particles.Adapters;
using PFE.Systems.Weapons;
using PFE.Core.Rng;
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
    public class UnitController : MonoBehaviour, IDamageable, PFE.Core.ISimTickable, IEffectReceiver, IBloodSpraySource, IEffectVisualSink
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

        /// <summary>
        /// AS3 <c>Unit.throu</c> (<c>Unit.as:296</c>) — "ignore one-way surfaces when asking what I am
        /// standing on".
        ///
        /// <para><b>A level, not a pulse, and that is the difference from the motor's player-side
        /// verb.</b> The oracle recomputes <c>throu</c> every frame (<c>UnitZombie.as:857-864</c>,
        /// <c>UnitPlayer.as:2640</c>) and the zombie's condition is <i>sustained</i> — it holds for as long
        /// as the target stays below. A pulse would drop the zombie once and re-ground it on the next
        /// tick, which reads as "the catwalk flickers".</para>
        ///
        /// <para><b>It is cleared by the writer, not by this class.</b> The owning brain asserts it every
        /// tick it runs, so a brain that stops ticking (a corpse, a buried ambusher) leaves the last value
        /// in place — which is harmless here because a corpse does not move and a buried unit is not
        /// standing on anything. A self-clearing timer would be a second rule about when the flag expires
        /// and would not be the oracle's.</para>
        /// </summary>
        protected bool _dropThroughPlatforms;

        protected int _facingDirection = 1; // 1 = Right, -1 = Left

        /// <summary>
        /// AS3 <c>Unit.shX1</c>/<c>shX2</c> — how much of this unit's body width hangs past the edge of
        /// whatever it is standing on, per side. Negative means that side is fully supported; above
        /// <c>0.5</c> means more than half the body is over the edge. Written by
        /// <see cref="ResolveGroundState"/>; see <see cref="PFE.Entities.Units.UnitOverhangMath"/> for
        /// the oracle's four writers, the formula, and why the "no support" value is <c>1</c> and not a
        /// sentinel.
        ///
        /// <para><b>Both start at "no support", which is the safe reading for every early return.</b>
        /// AS3 writes <c>shX1 = shX2 = 1</c> at the start of each descending pass
        /// (<c>Unit.as:2251</c>), so a unit with no room, no collider or no query would carry the same
        /// value the oracle's first write produces — rather than a stale fraction from a platform it
        /// left ten ticks ago.</para>
        /// </summary>
        protected float _overhangLeft = UnitOverhangMath.NoSupportOverhang;

        /// <inheritdoc cref="_overhangLeft"/>
        protected float _overhangRight = UnitOverhangMath.NoSupportOverhang;


        // Components
        protected Rigidbody2D _rb;
        protected Collider2D _collider;
        private TilePhysicsController _cachedTilePhysics;
        private bool _hasTilePhysics;

        /// <summary>
        /// The child <c>SpriteRenderer</c> the spawner drew this unit's body into, or <c>null</c> when
        /// nothing handed one over.
        ///
        /// <para><b>Why a seam rather than a <c>GetComponentInChildren</c>.</b> The sprite lives on a
        /// <i>child</i> of the unit — deliberately, because the unit's own transform must stay on the
        /// feet while the sprite is drawn 10 px below them (<c>RoomUnitSpawner.Spawn</c> documents the
        /// measurement). A component search would therefore find the <i>first</i> renderer under the
        /// unit, which is right today and silently wrong the moment a unit grows a second one (a held
        /// weapon, a shield dome, a muzzle flare). The spawner already holds the exact renderer it
        /// built, so it hands that one over — the same shape as <see cref="SetTileQuery"/> and
        /// <see cref="SetDamageSystem"/>, and for the same reason: <c>AddComponent</c> never goes
        /// through VContainer, so the owner is the only possible handover.</para>
        ///
        /// <para>Null is legal. A bare test spawn has no renderer, and a unit that never hides itself
        /// never reads this field.</para>
        /// </summary>
        protected SpriteRenderer _visualRenderer;

        /// <summary>
        /// Whether this unit's body is currently drawn — AS3 <c>Unit.isVis</c> (<c>Unit.as:188</c>).
        ///
        /// <para><b>Kept separate from the renderer's <c>enabled</c> flag on purpose.</b> The oracle's
        /// <c>isVis</c> is read back by behaviour (<c>UnitNPC.addVisual</c> recomputes it from
        /// <c>targNPC.hidden</c>; <c>UnitNPC.command("show")</c> sets it), and a hidden NPC is still
        /// targetable-but-ignored rather than absent. Deriving it from Unity would make a unit with no
        /// renderer read as permanently invisible, which is the opposite of true for a headless
        /// fixture.</para>
        /// </summary>
        protected bool _isVis = true;

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
        /// Protected access to the simulation loop for subclasses (e.g. enemy controllers).
        /// </summary>
        protected PFE.Core.SimLoop SimLoop => _simLoop;

        /// <summary>
        /// Protected access to the simulation clock for subclasses.
        /// </summary>
        protected PFE.Core.SimClock SimClock => _simClock;

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
        /// AS3 <c>Unit.lootIsDrop</c> (<c>Unit.as:4381-4383</c>) — the once-only guard on the death drop.
        ///
        /// <para>It is not decoration. <see cref="OnDeath"/> is reachable more than once for one unit:
        /// AS3 reaches its own drop block from the death handler, and a resurrected zombie
        /// (<c>UnitZombie</c>) or a re-entered death branch would otherwise roll the table twice and
        /// double the loot. AS3 gates on this flag and so does the port.</para>
        /// </summary>
        protected bool _lootDropped;

        /// <summary>
        /// Whether <see cref="OnDeath"/> has already run for the death this unit is currently in.
        ///
        /// <para>AS3 reaches <c>die()</c> more than once per death — every hit on a corpse re-enters it
        /// (<c>Unit.as:3713-3745</c>) — and guards the re-entry's <i>effects</i> individually
        /// (<c>lootIsDrop</c> for the drop, <c>sost == 1</c> for the death rattle). The port has one
        /// reaction (<see cref="OnDeath"/>) covering loot <i>and</i> XP, and only the loot half was
        /// guarded, so this flag is the missing half. See <see cref="RaiseDeath"/>.</para>
        /// </summary>
        protected bool _deathRaised;

        /// <summary>
        /// Whether this unit leaves loot when it dies. <c>true</c> for every unit in the world; the
        /// player overrides it to <c>false</c>.
        ///
        /// <para><b>Why it is a seam and not a special case inside <see cref="DropLootOnDeath"/>.</b> The
        /// player is a <see cref="UnitController"/> and its <c>OnDeath</c> calls <c>base</c>, so without
        /// this the player's own death would run the drop path: its id (<c>littlepip</c>) names no table,
        /// so it would roll nothing and log an "unmatched key" warning on every death — noise that reads
        /// like a content bug. AS3 never reaches here either: <c>UnitPlayer</c> does not drop loot, and a
        /// table keyed <c>littlepip</c> does not exist.</para>
        /// </summary>
        protected virtual bool DropsLootOnDeath => true;

        /// <summary>
        /// AS3 <c>Unit.hero</c> — the boss tier, read off the <b>placement</b> (<c>Unit.as:645</c>,
        /// <c>this.hero = param3.@hero</c>). <c>0</c> = an ordinary unit.
        ///
        /// <para>No row in <c>AllData.as</c> authors <c>hero</c>; it is a per-placement fact, which is why
        /// it arrives through <see cref="ApplyPlacement"/> and not through the definition. 33 room assets
        /// under <c>Resources/Rooms</c> author it (<c>hero="3"</c>, <c>hero="4"</c>).</para>
        /// </summary>
        protected int _heroTier;

        /// <summary>
        /// The faction this <b>placement</b> overrode, or <c>null</c> to fall back to the unit
        /// definition. Written by <see cref="ApplyPlacement"/>; see <see cref="Faction"/>.
        /// </summary>
        private FactionType? _instanceFaction;

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
            ResolvePhysicsComponents();
            ApplyDefinitionToBody();
        }

        /// <summary>
        /// Cache the physics components this controller drives, and put the body into the mode the manual
        /// mover expects.
        ///
        /// <para><b>Why this is a method and not just the body of <c>Awake</c>.</b> In play mode Unity runs
        /// <c>Awake</c> synchronously inside <c>AddComponent</c>, and <c>RoomUnitSpawner</c> depends on
        /// that: it adds the <c>Rigidbody2D</c> and <c>BoxCollider2D</c> <i>before</i> the controller
        /// (<c>:386</c>/<c>:389</c> against <c>:461</c>) precisely so the <c>GetComponent</c> calls below
        /// find them. But <b>EditMode never runs <c>Awake</c> for <c>AddComponent</c></b> — there is no
        /// <c>[ExecuteAlways]</c> anywhere in this project — so a fixture that builds a unit by hand and
        /// then drives <c>SimTick</c> gets a controller whose <c>_rb</c> is null, and <see cref="Move"/>
        /// dereferences it unguarded, so the first tick throws <c>NullReferenceException</c> out of
        /// <c>_rb.MovePosition</c>. Note the asymmetry that made this invisible: <c>Move</c>'s feet read
        /// guards <c>_collider</c> and nothing guards <c>_rb</c>, so a null collider degrades quietly
        /// while a null body throws.</para>
        ///
        /// <para><see cref="Initialize"/> is the documented "the definition has only just arrived" seam,
        /// and it runs after both components exist on every construction path, so resolving there as well
        /// closes the gap. Idempotent, so the play-mode double call costs nothing — the same reasoning as
        /// <c>EnemyController.Initialize</c>'s <c>_brain</c> re-resolve. The player is unaffected:
        /// <c>PlayerController</c> builds its own stats and deliberately does not pass through
        /// <see cref="Initialize"/> (<c>PlayerController.cs:440</c>).</para>
        /// </summary>
        protected void ResolvePhysicsComponents()
        {
            _rb = GetComponent<Rigidbody2D>();
            _collider = GetComponent<Collider2D>();

            // Use Kinematic mode - we control movement manually but Unity handles collision
            _rb.bodyType = RigidbodyType2D.Kinematic;
            _rb.useFullKinematicContacts = true; // Enable collision detection
            _rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous; // Prevent tunneling

            _cachedTilePhysics = GetComponent<TilePhysicsController>();
            _hasTilePhysics = _cachedTilePhysics != null;
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
        /// Size <b>both</b> descriptions of the unit's body from the definition: the Unity
        /// <see cref="Collider2D"/> (world units) and the tile motor's collision box (pixels).
        ///
        /// <para><b>AS3 has one body; the port has two, so both are written here.</b> The oracle sizes a
        /// unit from its <c>&lt;phis sX sY&gt;</c> pair and puts the box <i>entirely above</i> the origin —
        /// <c>Y1 = Y - scY; Y2 = Y</c> (<c>Unit.as:1872-1881</c>) — because the origin is the unit's
        /// feet and the body grows upward. Writing only <c>size</c> leaves the collider <i>centred</i> on
        /// the feet: half the body sits inside the floor tile the unit stands on and the top half (the
        /// head) is outside the box that <see cref="CollisionBoxesOverlap"/> tests against. That was the
        /// shipped state of <c>Player.prefab</c> — the one construction path that never set the offset
        /// itself, unlike <c>RoomUnitSpawner</c> and <c>PlayerRigBuilder</c>, which both do it by hand.</para>
        ///
        /// <para><b>The motor is the other half.</b> <c>TilePhysicsController.collisionWidth/Height</c>
        /// default to 30×50 px and were only ever written by <c>RoomUnitSpawner</c>, so any unit built
        /// another way — the player above all — resolved walls, ceilings and ground against a box that
        /// was neither its own size nor its definition's (AS3's <c>littlepip</c> is 50×70). Both
        /// consumers now come from the same pair, so the box the F6 overlay draws and the box the motor
        /// collides with cannot disagree again.</para>
        /// </summary>
        protected void ApplyDefinitionToBody()
        {
            if (_stats == null)
            {
                return;
            }

            if (_collider is BoxCollider2D boxCollider)
            {
                boxCollider.size = new Vector2(_stats.Width, _stats.Height);
                boxCollider.offset = new Vector2(0f, _stats.Height * 0.5f);
            }

            // Absent on the legacy Rigidbody2D path, so this is a skip rather than a failure: a unit
            // with no motor has no tile body to size. The fallback GetComponent covers a caller that
            // reaches this without ResolvePhysicsComponents having cached the motor first.
            TilePhysicsController motor = _cachedTilePhysics != null
                ? _cachedTilePhysics
                : GetComponent<TilePhysicsController>();
            if (motor != null)
            {
                motor.ConfigureCollisionSize(
                    UnitCollisionBoxMath.PixelsFromWorldUnits(_stats.Width),
                    UnitCollisionBoxMath.PixelsFromWorldUnits(_stats.Height),
                    UnitCollisionBoxMath.PixelsFromWorldUnits(_stats.sitHeight));
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

            // Resolve before ApplyDefinitionToBody, which sizes the collider and the motor and skips
            // whatever is missing. In play mode Awake has already done this; in EditMode it has not.
            // See ResolvePhysicsComponents for why both paths call it.
            ResolvePhysicsComponents();
            ApplyDefinitionToBody();
            SeedEvasionFromDefinition();
            SeedArmourFromDefinition();

            // The stats object now exists, so the effect payloads have somewhere to route their damage.
            AttachEffectDamageSink();
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
        /// Project the definition's authored armour onto the live stats — the producer for
        /// <c>@armor</c>/<c>@marmor</c>/<c>@armorhp</c>/<c>@aqual</c> and for <c>@skin</c>.
        ///
        /// <para><b>This is the seam the whole NPC armour mechanic was missing.</b> Everything
        /// downstream of it has been ported and tested for months: <c>ArmourWear.PoolIntegrityDamage</c>
        /// scales the wear (acid ×4, explosive ×2, divided by the weapon's <c>armorMult</c>),
        /// <c>DamageCalculator.ResolveDamage</c> runs the reliability roll and the flat reduction, and
        /// <c>UnitStats.ApplyDamage</c> writes the depleted pool back. But <b>nothing ever built an
        /// <see cref="ArmourState"/> from a unit's <c>&lt;comb&gt;</c> row</b> —
        /// <c>ArmourState.FromUnitPool</c> had exactly one kind of caller, a test — so every NPC
        /// answered <c>ArmourState.None</c> and took full damage, while 50 of the oracle's
        /// <c>&lt;comb&gt;</c> rows author a rating (robots, metal-clad raiders, the armoured monsters).
        /// The ported rules were unreachable, not wrong.</para>
        ///
        /// <para><b>Only the pool model, and only for non-player units.</b> AS3's block is guarded
        /// <c>!this.player</c> (<c>Unit.as:3578</c>): the player's armour is a projected <c>Armor</c>
        /// item written by <c>Armor.setArmor()</c>, and <c>PlayerController</c> builds its own
        /// <see cref="UnitStats"/> without ever calling this method. The <c>fraction</c> guard is
        /// therefore belt-and-braces rather than the mechanism — but it is also the faithful term, so a
        /// future spawn path that routes the player through <c>Initialize</c> cannot quietly give the
        /// player a second, item-less armour.</para>
        ///
        /// <para><b>The pool's capacity is not simply the unit's health.</b> See
        /// <see cref="UnitArmourMath.PoolCapacity"/>: an authored <c>@armorhp</c> ignores the difficulty
        /// cut, while the fallback arm reads the already-cut <c>this.hp</c>. <c>UnitStats.MaxHp</c> is
        /// the post-cut number, which is exactly what the fallback arm wants and what the authored arm
        /// must not use — so both are handed over separately.</para>
        ///
        /// <para><b><c>@skin</c> is the sibling term and was missing for the same reason.</b> The
        /// reduction block opens with <c>_loc8_ = this.skin</c> (<c>Unit.as:3615</c>, <c>:3623</c>), and
        /// <see cref="UnitStats.skinResistance"/> has been a live <i>reader</i> since the armoured
        /// training dummy landed — with <b>no writer from data</b>. 16 <c>&lt;comb&gt;</c> rows author
        /// <c>skin</c>; all 16 read 0. <c>TrainingDummyController</c> still overwrites this for its
        /// <c>tr='1'</c> variant, deliberately: AS3 raises the dummy's skin in its constructor
        /// (<c>UnitTrain.as:41-45</c>), and that runs after this seeding, exactly as before.</para>
        ///
        /// <para><b>Data caveat, and it is the honest kind.</b> <c>armourQuality</c> is a field the
        /// existing unit assets do not carry yet — the value used to be imported into a dead field
        /// called <c>waterAbility</c> (see its doc). So until <c>PFE/Data/Import Units from AllData.as</c>
        /// is run again, every asset answers <c>armourQuality = 0</c>, which means "the rating never
        /// applies": the pool still wears and the HUD/debug overlay shows it draining, but no damage is
        /// reduced. That is the pre-existing behaviour, not a regression — and a re-import is the whole
        /// fix, with no code change.</para>
        /// </summary>
        void SeedArmourFromDefinition()
        {
            if (_stats == null || _unitStats == null)
            {
                return;
            }

            _unitStats.skinResistance = _stats.skinThickness;

            if (_stats.fraction == FactionType.Player)
            {
                return;
            }

            ArmourState pool = UnitArmourMath.Build(
                armour: _stats.armor,
                magicArmour: _stats.magicArmor,
                authoredArmourHp: _stats.armorHealth,
                hpAfterDifficulty: _unitStats.MaxHp.Value,
                quality: _stats.armourQuality);

            // Nothing authored, or no capacity to wear — leave the projection at None rather than
            // publishing a zeroed one. `SetArmour` recomputes the vulnerability table, and an unarmoured
            // unit has nothing to recompute.
            if (!pool.IsEquipped)
            {
                return;
            }

            _unitStats.EquipArmour(pool);
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
        /// The room this unit currently belongs to — AS3 <c>Unit.loc</c>, read through the tile query
        /// the spawner handed over. <c>null</c> for a unit built without one (a bare test spawn).
        ///
        /// <para><b>Why a property rather than a field.</b> The room is not the unit's to own: it comes
        /// in with <see cref="SetTileQuery"/>, changes when the unit is re-spawned into another room,
        /// and is already reachable from the query. A second field would be a second answer that could
        /// disagree — the failure shape this project keeps finding.</para>
        /// </summary>
        public RoomInstance Room => _tileQuery != null ? _tileQuery.Room : null;

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
        /// Hand this unit the exact <c>SpriteRenderer</c> the spawner drew its body into — the seam that
        /// lets <see cref="SetVisible"/> and a subclass that swaps its own art reach the right object.
        /// </summary>
        /// <remarks>
        /// <para><b>Called by <c>RoomUnitSpawner.Spawn</c> before <c>Initialize</c></b>, because
        /// <c>Initialize</c> is where an NPC subclass applies its own sprite and that needs the
        /// renderer to already be here. Same handover shape and same reason as
        /// <see cref="SetTileQuery"/>: the spawner built the child GameObject, so it is the only thing
        /// that holds the reference.</para>
        ///
        /// <para>Null is accepted and means "no renderer" — see <see cref="_visualRenderer"/>.</para>
        /// </remarks>
        public virtual void SetVisualRenderer(SpriteRenderer visualRenderer)
        {
            _visualRenderer = visualRenderer;
            ApplyVisualVisibility();
        }

        /// <summary>
        /// AS3 <c>Unit.isVis</c> (<c>Unit.as:188</c>) — whether this unit's body is drawn. Read by
        /// behaviour; see <see cref="_isVis"/>.
        /// </summary>
        public bool IsVisible => _isVis;

        /// <summary>
        /// Show or hide the unit's body — AS3's <c>vis.visible = …</c> writes in
        /// <c>UnitNPC.hide</c> / <c>UnitNPC.command("show")</c> (<c>UnitNPC.as:283-295, 329-340</c>).
        /// </summary>
        /// <remarks>
        /// <para><b>This does not touch the collider, the brain or <c>IsAlive</c>, and that is
        /// deliberate.</b> AS3's <c>hide()</c> sets <c>inter.active = false</c> and the sprite's
        /// <c>visible</c>, and nothing else — a hidden NPC is still simulated, still shootable and still
        /// occupies its tile. An "invisible means gone" implementation would make the surf story's
        /// hidden Calamity unkillable by the raiders he flies in to fight, which is the opposite of the
        /// beat.</para>
        /// </remarks>
        public virtual void SetVisible(bool visible)
        {
            _isVis = visible;
            ApplyVisualVisibility();
        }

        /// <summary>
        /// Pushes <see cref="_isVis"/> onto the renderer, tolerating a null one.
        ///
        /// <para><b>A separate method because the two writes arrive in either order.</b> The spawner
        /// hands the renderer over at spawn and a script hides the unit later, but a subclass may also
        /// hide itself during <c>Initialize</c> — before any handover in a bare test. Routing both
        /// through one call means neither order leaves the sprite disagreeing with <see cref="IsVisible"/>.
        /// </para>
        /// </summary>
        protected void ApplyVisualVisibility()
        {
            if (_visualRenderer != null)
            {
                _visualRenderer.enabled = _isVis;
            }
        }

        /// <summary>
        /// Hand this unit the port's damage authority — the seam <c>RoomUnitSpawner</c> needs, since
        /// <c>AddComponent</c> never goes through VContainer and so never satisfies
        /// <c>[Inject]</c>. See the field's remarks.
        /// </summary>
        public virtual void SetDamageSystem(DamageSystem damageSystem)
        {
            _damageSystem = damageSystem;
            AttachEffectDamageSink();
        }

        public DamageSystem DamageSystem => _damageSystem;
        public UnitDefinition Definition => _stats;

        /// <summary>
        /// Point this unit's effect payload damage at <see cref="ReportEffectDamage"/>, so a burn, an
        /// acid tick or a poison tick goes through the damage pipeline rather than the raw HP
        /// subtraction.
        ///
        /// <para><b>Safe to call more than once, and called from every place the unit's stats
        /// appear.</b> The two assignment sites are unrelated — <see cref="Initialize"/> for a spawned
        /// unit, and the player's own <c>Awake</c>, which builds its <c>UnitStats</c> itself — so the
        /// install cannot hang off one of them. <see cref="SetDamageSystem"/> calls it too, because the
        /// damage authority and the stats arrive in either order.</para>
        /// </summary>
        protected void AttachEffectDamageSink()
        {
            if (_unitStats == null)
            {
                return;
            }

            _unitStats.EffectDamageSink = ReportEffectDamage;
        }

        /// <summary>
        /// Apply an effect's payload damage through the port's damage authority — AS3
        /// <c>owner.damage(val, type, null, true)</c> (<c>Effect.as:417/423/434/438</c>).
        ///
        /// <para><b>Why the payload cannot call <see cref="UnitStats.Damage"/> directly.</b> That method
        /// is the pipeline's <i>receiving</i> end — <see cref="TakeDamage"/> calls it after
        /// <c>DamageSystem</c> has computed the number — so using it as the entry point drops the
        /// vulnerability table, armour and <c>skin</c>, the floating damage number, and the death check.
        /// The owner saw the missing number as "they burn but no burn damage shown"; the death half is
        /// worse, because a burn could reach 0 HP and leave the unit standing.</para>
        ///
        /// <para><b>Why <see cref="PendingDamage.Contact"/> and not <c>Direct</c>.</b> The oracle's
        /// fourth argument (<c>true</c>) skips the hit-avoidance roll, and its third (<c>null</c>) means
        /// no bullet — no crit, no piercing, no armour multiplier, no knockback, no penetration. That is
        /// exactly <c>Contact</c>, the same shape <see cref="ReportPropImpact"/> uses for
        /// <c>udarBox</c>, and for the same two reasons.</para>
        /// </summary>
        protected virtual void ReportEffectDamage(float damage, DamageType type)
        {
            if (damage <= 0f)
            {
                return;
            }

            if (_damageSystem != null)
            {
                _damageSystem.Report(PendingDamage.Contact(
                    DamageContext.Contact(damage, type),
                    this,
                    transform.position));
                return;
            }

            // Unarmoured fallback. Loud rather than silent, for the same reason ReportPropImpact's is:
            // a raw subtraction that looks like a working payload is how a missing injection survives
            // to release.
            if (!_warnedMissingDamageSystem)
            {
                _warnedMissingDamageSystem = true;
                Debug.LogWarning(
                    $"[{GetType().Name}] effect payload damage has no DamageSystem, so it is being " +
                    "applied as raw HP damage — no armour, no vulnerabilities, no damage number. The " +
                    "spawner must call SetDamageSystem, or the unit's GameObject must be in the scene " +
                    "scope's autoInjectGameObjects.", this);
            }

            _unitStats?.Damage(damage);
        }

        /// <summary>
        /// Run AS3's <b>contact-attack pair</b> against another unit and report the hit: the attacker's
        /// <c>attKorp()</c> (<c>Unit.as:3267-3277</c>) followed by the target's <c>udarUnit()</c>
        /// (<c>:4125-4166</c>). An enemy brain reaches it the way <c>UnitZombie.control()</c> does —
        /// <c>attKorp(celUnit, shok &lt;= 0 ? 1 : 0.5)</c> (<c>UnitZombie.as:940</c>).
        ///
        /// <para><b>Why it must come through <see cref="DamageSystem"/> and not
        /// <c>target.TakeDamage</c>.</b> <c>TakeDamage</c> is the pipeline's <i>receiving</i> end — it is
        /// what <c>DamageSystem</c> calls once the number is computed — so entering there drops the
        /// target's vulnerability table, its <c>skin</c> resistance, its armour pool, the floating damage
        /// number and the death check. A zombie clawing a player through it would do exactly the
        /// unarmoured subtraction that <see cref="UnitStats.Damage"/> does, and nothing would look
        /// wrong until someone put armour on.</para>
        ///
        /// <para><b><see cref="PendingDamage.Contact"/>, not <c>Direct</c>.</b> <c>udarUnit</c> does not
        /// go through <c>udarBullet</c>, so the hit takes neither the hit-avoidance roll nor the
        /// <c>udarBullet</c> damage spread (<c>×0.7..1.3</c>, <c>:4085</c>) — the same shape
        /// <see cref="ReportEffectDamage"/> and <c>ReportPropImpact</c> use. It does have a spread of its
        /// own, <c>×0.8..1.2</c> at <c>:4150</c>, and that is drawn here, before the report, because the
        /// oracle draws it before <c>damage()</c> — see
        /// <see cref="DamageSystem.RollContactAttackVariance"/>.</para>
        ///
        /// <para><b>The rate limiter is the target's, not the attacker's.</b> There is no attack
        /// cooldown anywhere in this path: <c>attKorp</c> refuses a target whose <c>neujaz</c> is
        /// running (<c>:3273</c>), and <c>udarUnit</c> then grants that window (<c>:4135</c>). So an
        /// overlapping enemy lands a hit every <see cref="ContactInvulnerabilityMaxTicks"/> ticks and
        /// the attacker needs no timer of its own.</para>
        /// </summary>
        /// <param name="target">The unit being hit. The oracle's <c>param1</c>.</param>
        /// <param name="damageScale">
        /// AS3 <c>attKorp</c>'s <c>param2</c> — the caller's multiplier on the attacker's <c>dam</c>.
        /// <c>UnitZombie.as:940</c> passes <c>shok &lt;= 0 ? 1 : 0.5</c>, i.e. a half-strength hit while
        /// the attacker is still reacting.
        /// </param>
        /// <returns>True when a hit was reported. False covers every refusal — no target, a dead
        /// attacker, boxes that do not overlap, a target still inside its window, and a missing
        /// <see cref="DamageSystem"/>.</returns>
        public virtual bool TryContactAttack(UnitController target, float damageScale = 1f)
        {
            // AS3 attKorp:3269 — `this.sost > 1` refuses from a corpse, and a null target too.
            if (!IsAlive || target == null || damageScale <= 0f)
            {
                return false;
            }

            // AS3 attKorp:3273 — an AABB test on the two COLLISION boxes, not a distance threshold.
            // `param1.X1 > X2 || param1.X2 < X1 || param1.Y1 > Y2 || param1.Y2 < Y1`.
            if (!CollisionBoxesOverlap(this, target))
            {
                return false;
            }

            // AS3 attKorp:3273 — `|| param1.neujaz > 0`. Read on the TARGET.
            if (target.IsContactInvulnerable)
            {
                return false;
            }

            // AS3 udarUnit:4135 — granted before the damage, and before the dodge test, so a dodged
            // swing still spends the target's window. The port has no contact-dodge roll (that lives on
            // HitAvoidance and only the Direct path reaches it), so the grant sits here.
            target.GrantContactInvulnerability();

            // AS3 udarUnit:4150 — the spread, drawn before the damage is computed.
            // AS3 udarUnit:4166 — `this.damage(param1.dam * _loc3_ * param2, param1.tipDamage)`.
            // `_stats.damage` is the row's `<comb damage>`; `_stats.damageType` is its `<comb tipdam>`,
            // which Unit.as:186 declares as 2 (D_PHIS) and only an explicit attribute moves.
            float baseDamage = _stats != null ? _stats.damage : 0f;
            DamageType type = _stats != null ? _stats.damageType : DamageType.PhysicalMelee;

            // The draw comes first because the oracle's does, and it is the only RNG this path takes —
            // the exchange below consumes none, so the order is preserved for readability rather than
            // for the stream. A hit with no DamageSystem cannot draw at all; it still exchanges, since
            // the shove is not part of the damage pipeline and AS3 applies it either way.
            float damage = _damageSystem != null
                ? baseDamage * _damageSystem.RollContactAttackVariance() * damageScale
                : baseDamage * damageScale;

            // AS3 udarUnit:4151-4159 — the momentum exchange, BETWEEN the spread and damage().
            ApplyContactCollisionExchange(target);

            if (_damageSystem != null)
            {
                _damageSystem.Report(PendingDamage.Contact(
                    DamageContext.Contact(damage, type),
                    target,
                    target.transform.position));

                return true;
            }

            // Unarmoured fallback. Loud rather than silent, and the same shape ReportPropImpact uses:
            // a hit that quietly does nothing is indistinguishable from a hit that missed, and a raw
            // subtraction that looks like a working feature is how a missing injection survives to
            // release.
            if (!_warnedMissingDamageSystem)
            {
                _warnedMissingDamageSystem = true;
                Debug.LogWarning(
                    $"[{GetType().Name}] contact attack has no DamageSystem, so it is being applied as " +
                    "raw HP damage — no armour, no vulnerabilities, no damage number. The spawner must " +
                    "call SetDamageSystem, or the unit's GameObject must be in the scene scope's " +
                    "autoInjectGameObjects.", this);
            }

            target.TakeDamage(baseDamage * damageScale);
            return true;
        }

        /// <summary>
        /// AS3 <c>Unit.udarUnit()</c>'s momentum exchange (<c>Unit.as:4151-4159</c>) — the shove a
        /// contact attack delivers, which is a <b>different mechanic</b> from a weapon's
        /// <c>otbros</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>Why it is not <see cref="ApplyKnockback"/>.</b> That one is additive and comes from
        /// a bullet's <c>knockx/knocky · otbros</c> — "knocked back by the damage". This one is a
        /// <b>replacement</b> of both bodies' velocities about their common centre of mass, and a
        /// contact attack never calls <c>otbros</c> at all (<c>udarUnit</c> goes straight to
        /// <c>damage()</c>). Two units charging each other therefore <i>swap</i> velocity rather than
        /// each gaining an impulse, and a claw with no weapon still shoves.</para>
        ///
        /// <para><b>Both sides are written, and the attacker is not spared.</b> A zombie that runs into
        /// a standing player stops, because the exchange gives it the centre-of-mass velocity. Writing
        /// only the target's half would make every melee attacker a perpetual-motion ram.</para>
        ///
        /// <para><b>The gate is the attacker's <see cref="CollisionTip"/>.</b> Slimes, spectres,
        /// necroses, the megadron and thunderhead author it as <c>0</c> in AS3 and therefore shove
        /// nothing; everyone else does.</para>
        ///
        /// <para><b>It runs even when the target is a corpse.</b> <c>udarUnit</c> has no <c>sost</c>
        /// test — the same reason a blade damages a corpse — and the port's <c>CanBeHit</c> work
        /// already made that call for damage. A body is a body.</para>
        /// </remarks>
        private void ApplyContactCollisionExchange(UnitController target)
        {
            if (!CollisionTip)
            {
                return;
            }

            UnitCollisionExchange exchange = UnitCollisionImpulseMath.ResolveExchange(
                attackerVelocityPixelsPerFrame: VelocityPixelsPerFrame,
                attackerMassa:                 Mass,
                attackerKnocked:               Knocked,
                targetVelocityPixelsPerFrame:  target.VelocityPixelsPerFrame,
                targetMassa:                   target.Mass,
                targetKnocked:                 target.Knocked);

            SetVelocityPixelsPerFrame(exchange.AttackerVelocityPixelsPerFrame);
            target.SetVelocityPixelsPerFrame(exchange.TargetVelocityPixelsPerFrame);
        }

        /// <summary>
        /// This unit's collision box in world space — AS3's <c>X1</c>/<c>X2</c>/<c>Y1</c>/<c>Y2</c>
        /// (<c>Unit.as:1875-1878</c>). A unit with no collider degenerates to a point at its origin,
        /// which is the honest answer for "there is no box".
        /// </summary>
        private Bounds CollisionBoxWorld =>
            _collider != null ? _collider.bounds : new Bounds(transform.position, Vector3.zero);

        /// <summary>
        /// AS3's collision-box overlap test — <c>attKorp</c>'s
        /// <c>param1.X1 &gt; X2 || param1.X2 &lt; X1 || param1.Y1 &gt; Y2 || param1.Y2 &lt; Y1</c>
        /// (<c>Unit.as:3273</c>).
        /// </summary>
        /// <remarks>
        /// <para><b>The boxes are the Unity colliders, not a rectangle recomputed from the
        /// definition.</b> AS3's four edges are the unit's own body box, and the port's equivalent is the
        /// <see cref="Collider2D"/> that <c>ApplyDefinitionToBody</c> sizes from the same data —
        /// including the <b>feet-anchored</b> <c>offset</c> (<c>Y1 = Y - scY</c>, <c>Y2 = Y</c>), which a
        /// recomputed rectangle would have to duplicate and could get wrong. Reading the collider also
        /// means a unit whose definition is missing still has a box — Unity's default 1x1 — rather than
        /// none, so no test can pass or fail on whether a definition happened to be supplied.</para>
        ///
        /// <para>The oracle's other guard on this line, <c>param1.loc != loc</c>, has no port analogue:
        /// it refuses a target in a different <c>Location</c>, and the port's spawner builds one room's
        /// units at a time. Recorded rather than silently dropped.</para>
        /// </remarks>
        private static bool CollisionBoxesOverlap(UnitController a, UnitController b)
        {
            Bounds ba = a.CollisionBoxWorld;
            Bounds bb = b.CollisionBoxWorld;

            return ba.min.x <= bb.max.x && ba.max.x >= bb.min.x
                && ba.min.y <= bb.max.y && ba.max.y >= bb.min.y;
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

            // AS3 runs the effect half of `Unit.step` inside the unit's own step (`Unit.as:3144-3158`)
            // and it is a subset of `step()`, not a separate driver — so it belongs on the shared step
            // body rather than on either driver, for exactly the reason described above: a driver-level
            // hook would put a burn on whichever clock that unit happens to use.
            TickEffects();

            // ...and the corpse dissolve, which is the same block of AS3's `Unit.step` (`:1836-1843`:
            // `if(burn == null) animate(); else { burn.step(); if(burn.vse) exterminate(); }`). Both
            // drivers call it for the same reason they call TickEffects.
            TickCorpse();

            // ...and the acoustic half, for the same reason. It runs BEFORE the move because AS3's noise
            // block sits in the unit's own frame alongside `actions()`'s other countdowns, reading the
            // velocity the step is about to act on — a move that collides zeroes `dx`, so reading it
            // afterwards would silence every unit the moment it walked into a wall. `IsGrounded` is
            // likewise last step's answer, which is what AS3's `stay` is.
            TickNoise(IsGrounded, VelocityPixelsPerFrame);
            TickSuspicion();

            // ...and the target-side stealth half of the same frame. It reads the same velocity, and it
            // is the twin of the acoustic block: noise is how loud this unit is, stealth is how visible.
            // Both drivers must call it — see the remarks on TickStealth.
            TickStealth(VelocityPixelsPerFrame);

            ResolveGroundState();
            ApplyGravity();

            // AS3 `Unit.step` runs `forces()` then `control()` then `run()` (Unit.as:1806-1809), and
            // `run()` is where the brake and the position integration live. So the per-frame behaviour
            // hook sits between gravity and friction here, which is the same place: a `control()` that
            // adds to `dx`/`dy` (the NPC's fly thrust) is then damped and integrated by the same two
            // calls the oracle uses.
            TickControl();

            ApplyFriction();
            Move();

            // After the move, matching the oracle: `Box.as:632` calls attDrop at the end of the box's
            // own update, after `run()` has resolved that frame's collisions. Sweeping before the move
            // would read a velocity the step has not yet acted on.
            SweepPropImpacts();

            // AS3 `Trap.step` (`Trap.as:147-161`) runs from the trap's own object step, which this port
            // does not have — so the unit sweeps instead, immediately after the move for the same reason
            // the prop sweep is here: `attKorp` reads `dy`, and the move is what settles it.
            SweepTrapImpacts();
        }

        /// <summary>
        /// Advance this unit's status effects by one step — the effect half of AS3 <c>Unit.step</c>.
        ///
        /// <para><b>Public and idempotent-guarded by the caller, not by this method.</b> The motor-driven
        /// path (<c>TilePhysicsController.StepMotor</c>) calls this because it owns that unit's whole
        /// step; the motor-less paths reach it through <see cref="StepUnit"/>. Both pass the sim
        /// clock's <see cref="PFE.Core.SimClock.StepScale"/>, so the burn counts down in canonical 30 Hz
        /// frames whichever driver is running — see <c>ActiveEffectSet.Tick</c>.</para>
        ///
        /// <para>A unit with no <see cref="UnitStats"/> has no effects, which is the oracle's state for
        /// a target that is not a <c>Unit</c> at all.</para>
        /// </summary>
        public void TickEffects()
        {
            float scale = _simClock != null ? _simClock.StepScale : 1f;
            _unitStats?.TickEffects(scale);
        }

        /// <summary>
        /// This unit's own per-frame behaviour — the port's home for AS3 <c>Unit.control()</c>
        /// (<c>Unit.as:1820</c>), the virtual every unit subclass overrides to drive itself.
        ///
        /// <para><b>Why a named method and not just an override of <see cref="StepUnit"/>.</b>
        /// <c>StepUnit</c> runs only on the <i>motor-less</i> path: a unit with a
        /// <c>TilePhysicsController</c> is owned by <c>TilePhysicsController.StepMotor</c>, which is a
        /// separate driver, and <c>StepUnit</c> early-returns for it. A subclass that put its
        /// behaviour in <c>StepUnit</c> would therefore be dead the moment
        /// <c>PfeDebugSettings.UnitMotor</c> is switched on — a change that is invisible from the
        /// subclass and reads as "the NPC stopped working after an unrelated toggle". So the behaviour
        /// lives here and <b>both</b> drivers call it: <see cref="StepUnit"/> and
        /// <c>TilePhysicsController.StepMotor</c>, exactly as <see cref="TickEffects"/>,
        /// <c>TickNoise</c> and <c>TickStealth</c> already do.</para>
        ///
        /// <para><b>Order.</b> AS3 runs <c>forces()</c> → <c>control()</c> → <c>run()</c>
        /// (<c>Unit.as:1806-1809</c>), and <c>run()</c> is where the brake and the position write live.
        /// <see cref="StepUnit"/> therefore calls this between <see cref="ApplyGravity"/> and
        /// <see cref="ApplyFriction"/> — a subclass that adds to the velocity (the NPC's fly thrust)
        /// gets the same damping and the same integration the oracle gives it.</para>
        ///
        /// <para>The base implementation does nothing, which is the oracle's own base behaviour: the
        /// base <c>Unit</c> declares <c>control()</c> empty and every real behaviour is on a subclass.
        /// </para>
        /// </summary>
        public virtual void TickControl()
        {
        }

        // === Acoustic perception — AS3's `noise` / `makeNoise()` / `observation()` =================
        //
        // The arithmetic is all in NoiseMath; what lives here is the *state* — how loud this unit
        // currently is, and how suspicious of it the world has become.
        //
        // The state is on UnitController rather than on a component of its own for the same reason
        // `_isGrounded` and `_velocity` are: every unit has it, AS3 declares it on `Unit`, and a
        // separate component would need a lookup from the two places that already hold the unit (the
        // motor and the sensors). The *suspicion* meter is the one piece AS3 declares on a subclass
        // (`UnitPlayer`) rather than on `Unit`, and the note on `Suspicion` says why it is here anyway.

        /// <summary>
        /// AS3 <c>Unit.noise:int = 0</c> — how far this unit can currently be heard, in pixels before
        /// the listener's own <c>ear</c> is applied. Raised by <see cref="MakeNoise"/>, decays 20 per
        /// tick, and is <b>0 whenever the unit is not doing anything loud</b>.
        /// </summary>
        private int _noise;

        /// <summary>
        /// AS3 <c>Unit.noise_t:int = 30</c> — the cooldown on the visible "something made a sound here"
        /// ripple. Kept because it is the only stateful half of <c>makeNoise</c>, and read by the F3
        /// overlay so the mechanic is visible during a play-test.
        /// </summary>
        private int _noiseRippleTicks;

        /// <summary>
        /// AS3's <c>stay</c> as of the previous step, for detecting the landing edge. AS3 reads it
        /// directly because its <c>run()</c> sets it; the port resolves groundedness in two different
        /// places depending on the driver, so the edge is tracked here instead.
        /// </summary>
        private bool _noiseWasGrounded = true;

        /// <summary>
        /// The downward speed at the last airborne step, in px/frame — AS3's <c>dy</c> at the moment
        /// <c>checkShelf</c> fires. AS3 reads it inline inside <c>run()</c>; the port resolves the
        /// landing a step later (see <see cref="TickNoise"/>), so the value has to be carried.
        /// </summary>
        private float _noiseFallSpeedPixelsPerFrame;

        /// <summary>
        /// AS3's <c>UnitPlayer.obs</c> / <c>isObs</c> — the suspicion the world has accumulated about
        /// this unit.
        ///
        /// <para><b>Declared on <c>UnitPlayer</c> in the oracle, and deliberately on the base class
        /// here.</b> In AS3 only the player is ever the target of <c>findCel()</c>, so a per-enemy field
        /// would have been dead weight; in the port the sensor loop only ever sees a
        /// <see cref="UnitController"/> (<c>EnemySensors.Evaluate</c> takes
        /// <c>IReadOnlyList&lt;UnitController&gt;</c>), so the meter has to be reachable from there or
        /// the sensors would need a cast to a concrete player type. Non-player units simply never
        /// accumulate any, because nothing calls <see cref="AddSuspicion"/> for them — which is the same
        /// statement AS3 makes by declaring the field lower down the hierarchy.</para>
        /// </summary>
        private NoiseMath.Observation _suspicion;

        /// <summary>How loud this unit currently is — AS3 <c>Unit.noise</c>.</summary>
        public int Noise => _noise;

        /// <summary>
        /// This unit's base noise while running — AS3 <c>Unit.noiseRun</c>, from the definition's
        /// <c>noiseRun</c> (XML <c>&lt;vis noise='…'/&gt;</c>).
        /// </summary>
        public int NoiseRun => _stats != null ? _stats.noiseRun : NoiseMath.DefaultNoiseRun;

        /// <summary>
        /// This unit's hearing multiplier — AS3 <c>Unit.ear</c>. <b>0 means stone deaf</b>, which is what
        /// every small robot in the roster authors.
        /// </summary>
        public float Ear => _stats != null ? _stats.ear : NoiseMath.DefaultEar;

        /// <summary>
        /// This unit's observation power — AS3 <c>Unit.observ</c>, the <c>obs</c> attribute. Multiplies
        /// how fast this unit builds suspicion of a target; it is <b>not</b> a sight range.
        /// </summary>
        public float ObservationPower => _stats != null ? _stats.observationRange : 0f;

        /// <summary>Whether this unit is mechanical — AS3 <c>opt.robot</c>, the alarm-propagation exception.</summary>
        public bool IsMechanical => _stats != null && _stats.isMechanical;

        /// <summary>The suspicion meter — AS3's <c>obs</c>/<c>isObs</c> pair. See <see cref="_suspicion"/>.</summary>
        public NoiseMath.Observation Suspicion => _suspicion;

        /// <summary>
        /// AS3 <c>UnitPlayer.maxObs = 20</c> — the value <see cref="Suspicion"/> commits at.
        /// </summary>
        public float MaxSuspicion => NoiseMath.DefaultMaxObservation;

        /// <summary>
        /// Ticks remaining on the "this unit made a sound" ripple cooldown — AS3 <c>Unit.noise_t</c>.
        /// Read by the F3 overlay; the in-world ripple AS3 emits from <c>makeNoise</c> is still not
        /// ported (recorded as an open item rather than quietly dropped).
        /// </summary>
        public int NoiseRippleTicks => _noiseRippleTicks;

        /// <summary>
        /// AS3 <c>Unit.makeNoise(param1, param2)</c> — raise this unit's noise to at least
        /// <paramref name="amount"/>, and restart the ripple cooldown if it is due.
        ///
        /// <para><b>Public, because the two loudest things in the game are not movement.</b> Weapon fire
        /// (<c>Unit.crash()</c> → <c>makeNoise(weap.noise, true)</c>) and doors
        /// (<c>makeNoise(pers.noiseDoorOpen, true)</c>) both reach it from outside the unit's own step,
        /// and both are authored 300–800 in the data — louder than any sprint. Movement calls it from
        /// <see cref="TickNoise"/>.</para>
        /// </summary>
        /// <param name="amount">The sound's radius contribution. Non-positive is ignored entirely.</param>
        /// <param name="isEvent">
        /// AS3's <c>param2</c> — <c>true</c> for a discrete event (a gunshot, a landing, a door), which
        /// is allowed to restart the ripple inside the cooldown's last third. A continuous source
        /// (footsteps) passes <c>false</c> and gets one ripple per 30 ticks.
        /// </param>
        public void MakeNoise(int amount, bool isEvent = false)
        {
            if (amount <= 0)
            {
                // AS3 returns before touching noise_t, so a silent call is not even a quiet ripple.
                return;
            }

            _noise = NoiseMath.MakeNoise(_noise, amount);

            // Note the ripple cooldown is refreshed whether or not the value actually rose: AS3's
            // `if(this.noise < param1)` guard covers only the assignment, and the `noise_t` block below
            // it is unconditional. A quiet footstep during a gunshot therefore still re-arms the
            // ripple — which is what makes the ripple read as "sound here", not "loudest sound here".
            if (NoiseMath.ShouldRefreshNoiseRipple(_noiseRippleTicks, isEvent))
            {
                _noiseRippleTicks = NoiseMath.NoiseRippleTicks;
            }
        }

        /// <summary>
        /// One step of AS3's acoustic bookkeeping: the decay, the landing edge, and the movement noise.
        ///
        /// <para><b>Both facts are parameters because the two drivers disagree about where they live.</b>
        /// A motor-driven unit's groundedness and velocity belong to its
        /// <c>TilePhysicsController</c>; a motor-less unit's belong to this class. AS3 has one answer
        /// because it has one <c>run()</c>. Passing them in keeps the arithmetic in one place without
        /// making this class reach for a motor it may not have — and it is why this is not simply
        /// another parameterless <c>TickEffects()</c>.</para>
        ///
        /// <para><b>The landing is detected as an edge, one step late.</b> AS3 knows the impact
        /// <c>dy</c> because the shelf check that finds the ground is inside the same <c>run()</c> that
        /// integrated the fall. The port resolves groundedness before the move and zeroes the velocity
        /// during it, so by the time the edge is visible the speed is gone — hence
        /// <see cref="_noiseFallSpeedPixelsPerFrame"/>, which remembers the last airborne speed. One
        /// step of latency on a sound cue, and it means the value is the speed on the step <i>before</i>
        /// contact rather than at it, which is the same number for any constant-acceleration fall.</para>
        /// </summary>
        /// <param name="grounded">This driver's grounded state — AS3's <c>stay</c>.</param>
        /// <param name="velocityPixelsPerFrame">
        /// This driver's velocity in px/frame — the same quantity AS3's <c>dx</c>/<c>dy</c> hold. The
        /// sign convention differs between the drivers (the motor works in AS3's Y-down pixels, a
        /// motor-less unit's <c>_velocity</c> is Unity's Y-up units/s), which is why both thresholds
        /// compare magnitudes.
        /// </param>
        public void TickNoise(bool grounded, Vector2 velocityPixelsPerFrame)
        {
            if (grounded && !_noiseWasGrounded)
            {
                int landing = NoiseMath.LandingNoise(NoiseRun, _noiseFallSpeedPixelsPerFrame);
                if (landing > 0)
                {
                    MakeNoise(landing, isEvent: true);
                }
            }

            if (!grounded)
            {
                _noiseFallSpeedPixelsPerFrame = velocityPixelsPerFrame.y;
            }

            _noiseWasGrounded = grounded;

            _noise = NoiseMath.TickNoise(_noise);
            _noiseRippleTicks = NoiseMath.TickNoiseRipple(_noiseRippleTicks);

            int movement = NoiseMath.MovementNoise(NoiseRun, velocityPixelsPerFrame.x, grounded);
            if (movement > 0)
            {
                MakeNoise(movement);
            }
        }

        /// <summary>
        /// One step of the suspicion meter — AS3 <c>UnitPlayer</c>'s
        /// <c>if(obs &gt; 0 &amp;&amp; isObs &lt;= 0) obs -= minusObs;</c> and its hold countdown.
        ///
        /// <para>Called for every unit rather than only for the player, because "who is a target" is a
        /// question the sensors answer, not this class — see <see cref="_suspicion"/>. A unit nobody
        /// observes simply keeps a meter at zero, and ticking a zero costs two comparisons.</para>
        /// </summary>
        public void TickSuspicion()
        {
            _suspicion = NoiseMath.TickObservation(_suspicion);
        }

        /// <summary>
        /// Add to the suspicion meter — AS3 <c>UnitPlayer.observation(amount)</c>. Returns the meter's
        /// value after the add.
        /// </summary>
        /// <param name="amount">
        /// An already-scaled, non-negative intensity. The oracle's <c>observation()</c> does its
        /// <c>sneak</c>/<c>demask</c>/<c>stealthMult</c> scaling before this point; the port has no
        /// those inputs yet, so callers pass the raw sensor intensity and the scaling is an open item.
        /// </param>
        public float AddSuspicion(float amount)
        {
            _suspicion = NoiseMath.AddObservation(_suspicion, amount, MaxSuspicion);
            return _suspicion.Value;
        }

        /// <summary>
        /// AS3 <c>obs &gt;= maxObs</c> — whether the world has seen or heard enough of this unit to
        /// commit to it.
        /// </summary>
        /// <remarks>
        /// <b>There is deliberately no <c>ClearSuspicion()</c>.</b> The oracle never assigns <c>obs</c>
        /// back to zero anywhere — the only writes are the accumulator, the ceiling clamp and the decay
        /// (<c>:142187-142194</c>, <c>:140155-140157</c>). A reset method would look like housekeeping
        /// and would quietly delete a real mechanic: after a fight, the player stays "hot" for as long
        /// as the meter takes to drain (20 units at 0.1/tick ≈ 6.7 s), which is why the original is
        /// hard to re-hide from immediately after being seen. Draining is the reset.
        /// </remarks>
        public bool IsObserved => NoiseMath.IsObserved(_suspicion, MaxSuspicion);

        // === Stealth — AS3's target-side `visibility` / `stealthMult` / `demask` =====================
        //
        // The arithmetic is in StealthMath; what lives here is the *state*, for exactly the reason the
        // acoustic state does. AS3 declares the pieces on `Unit` (`visibility`, `stealthMult`,
        // `demask`, `detecting`) and on `UnitPlayer` (`lurked`, `isSit`), and the two readers already
        // hold a `UnitController`: `EnemySensors.Evaluate` takes `IReadOnlyList<UnitController>`, and
        // `AddSuspicion` is on this class.
        //
        // A unit that is not the player keeps the neutral state (visibility 2000, stealthMult 1,
        // demask 0, detecting 80). That is what makes the sensors' new per-target sight range equal the
        // old flat `VisionRangePixels` for every existing target — the calibration is named in
        // StealthMath.ReferenceVisibility rather than hidden here.

        /// <summary>
        /// This unit's target-side stealth state — AS3's <c>visibility</c>, <c>stealthMult</c>,
        /// <c>demask</c>, <c>detecting</c>, <c>lurked</c>, <c>isSit</c>. Advanced by
        /// <see cref="TickStealth"/>; read by <c>EnemySensors</c>.
        /// </summary>
        private StealthMath.StealthState _stealth = StealthMath.StealthState.Default;

        /// <summary>
        /// The whole target-side stealth state — AS3's <c>visibility</c>/<c>stealthMult</c>/<c>demask</c>
        /// group. Handed out as one value so a reader sees a consistent snapshot.
        /// </summary>
        public StealthMath.StealthState Stealth => _stealth;

        /// <summary>AS3 <c>Unit.visibility</c> — how large this unit looks to a searcher.</summary>
        public int Visibility => _stealth.Visibility;

        /// <summary>AS3 <c>Unit.stealthMult</c> — the cloak multiplier (1, or 0.2 while cloaked).</summary>
        public float StealthMult => _stealth.StealthMult;

        /// <summary>AS3 <c>Unit.demask</c> — the anti-stealth reveal from acting, 0…200.</summary>
        public float Demask => _stealth.Demask;

        /// <summary>
        /// AS3 <c>Unit.detecting</c> — the radius inside which an observer sees this unit regardless of
        /// facing. <b>0 while lurking or cloaked</b>, which deletes the point-blank tier.
        /// </summary>
        public int Detecting => _stealth.Detecting;

        /// <summary>AS3 <c>UnitPlayer.lurked</c> — in cover.</summary>
        public bool Lurked => _stealth.Lurked;

        /// <summary>AS3 <c>Unit.isSit</c> — resting.</summary>
        public bool IsSit => _stealth.IsSit;

        /// <summary>
        /// AS3 <c>Pers.visiMult</c> — the armour/perk concealment multiplier. <b>Scales the suspicion
        /// contribution only, never the sight range</b> (<c>visiMult</c> appears nowhere in
        /// <c>unit/Unit.as</c>). Overridden by the player once the <c>Armor.sneak</c> channel exists;
        /// neutral here.
        /// </summary>
        protected virtual float StealthVisiMultSource => StealthMath.DefaultVisiMult;

        /// <summary>
        /// AS3 <c>Unit.stealthMult</c> — the cloak multiplier.
        ///
        /// <para><b>Not yet overridden by anything.</b> The hook exists so <c>PlayerController</c> can
        /// read <c>CharacterStats.stealthMult</c> — which the <c>stealth</c> effect writes — but nothing
        /// overrides it today, so every unit is neutral (1) and the cloak effect does not reach
        /// perception yet. That is the `stealth`-effect work item, not an oversight; see
        /// <c>docs/StealthSystem/03_GAP_LEDGER.md</c> §0.</para>
        /// </summary>
        protected virtual float StealthMultSource => StealthMath.DefaultStealthMult;

        /// <summary>
        /// AS3 <c>Pers.sneakLurk</c> — the flat subtraction applied while lurking/sitting. Neutral here;
        /// the oracle's own default (<c>unit/Pers.as:311</c>) rather than
        /// <c>CharacterStats.sneakLurk</c>, whose initialiser is 0 where the oracle's is 5. Wiring that
        /// field is a data-channel item, not a silent behaviour change.
        /// </summary>
        protected virtual float SneakLurkSource => StealthMath.DefaultSneakLurk;

        /// <summary>
        /// AS3 <c>levit &gt; 0</c> — the player's levitation, one of the two per-tick <c>demask</c>
        /// writers (<c>unit/UnitPlayer.as:1113-1116</c>). Neutral here; <c>PlayerController</c> overrides
        /// it from <c>PlayerLocomotionController.IsLevitating</c>.
        /// </summary>
        protected virtual bool StealthLevitatingSource => false;

        /// <summary>
        /// AS3 <c>this.teleObj</c> — the player holding an object telekinetically, the other per-tick
        /// <c>demask</c> writer (<c>unit/UnitPlayer.as:1239-1242</c>). Neutral here;
        /// <c>PlayerController</c> overrides it from <c>PlayerTelekinesisController.IsHoldingObject</c>.
        /// </summary>
        protected virtual bool StealthTelekinesisSource => false;

        /// <summary>
        /// One step of the target-side stealth state — AS3's <c>UnitPlayer</c> per-tick block
        /// (<c>unit/UnitPlayer.as:1089-1132</c>) plus <c>Unit</c>'s demask decay
        /// (<c>unit/Unit.as:3139-3142</c>).
        ///
        /// <para><b>Called from the same two places <see cref="TickNoise"/> is, and for the same reason
        /// it takes its velocity as a parameter.</b> A motor-driven unit never reaches
        /// <c>StepUnit</c> — the motor owns that unit's whole step — so both drivers must call this, or
        /// it is dead on the live path. <c>MotorGroundStateLintTests</c> pins that pair, because that is
        /// exactly how the trap sweep came to deal no damage.</para>
        ///
        /// <para>The stat-derived multipliers are refreshed here rather than at their source because the
        /// effect system writes <c>CharacterStats</c> on its own schedule; reading them once per step is
        /// what makes a cloak take effect on the tick it is applied.</para>
        /// </summary>
        /// <param name="velocityPixelsPerFrame">
        /// This driver's velocity in AS3 px/frame — the quantity AS3's <c>dx</c>/<c>dy</c> hold. The
        /// sign convention differs between drivers, which is why every threshold in
        /// <see cref="StealthMath"/> compares magnitudes.
        /// </param>
        public void TickStealth(Vector2 velocityPixelsPerFrame)
        {
            _stealth.VisiMult = StealthVisiMultSource;
            _stealth.StealthMult = StealthMultSource;
            _stealth.SneakLurk = SneakLurkSource;

            _stealth = StealthMath.Tick(
                _stealth,
                velocityPixelsPerFrame.x,
                velocityPixelsPerFrame.y,
                levitating: StealthLevitatingSource,
                telekinesisHolding: StealthTelekinesisSource);
        }

        /// <summary>
        /// Raise this unit's <c>demask</c> — AS3's "you just did something loud" reveal. This is the
        /// <b>event</b> half of the demask writers, reached from their own call sites exactly as AS3
        /// reaches them: <c>weapon/Weapon.as:1575-1578</c> on firing, <c>unit/Unit.as:3765-3768</c> on
        /// taking a hit. (The per-tick writers — fast move, levitation, telekinesis — live in
        /// <see cref="StealthMath.Tick"/> instead, because AS3 runs them inside the step block.)
        ///
        /// <para><b>Raise-only, never assign.</b> Every oracle write is guarded by
        /// <c>if(demask &lt; X) demask = X;</c>, so a footstep during a gunshot cannot shorten the
        /// reveal — the same shape as <see cref="MakeNoise"/>, and for the same reason. See
        /// <see cref="StealthMath.RaiseDemask"/>.</para>
        /// </summary>
        /// <param name="amount">The value to raise to. Non-positive is ignored.</param>
        public void RaiseDemask(float amount)
        {
            _stealth.Demask = StealthMath.RaiseDemask(_stealth.Demask, amount);
        }

        // === IEffectReceiver — AS3's `this.addEffect(...)` reach =============================
        //
        // AS3's on-hit block lives inside `Unit.damage()` (`Unit.as:3763-3834`) and adds to
        // `this.effects`. The port's resolver runs the producers (`OnHitEffectProducers`) against the
        // target rather than the target's own damage body, so the target must expose its set. A
        // Damageable that is not a Unit — a crate — simply does not implement this interface, which is
        // how "no receiver means no effects" is expressed in the type system.

        /// <summary>
        /// This unit's live effects. Backed by <see cref="UnitStats"/>, which is where AS3's
        /// <c>Unit.effects</c> array lives in the port — see <c>UnitStats.Effects</c>.
        /// </summary>
        public ActiveEffectSet Effects => _unitStats?.Effects;

        /// <summary>
        /// Whether this unit may receive an effect of the given damage type — AS3's
        /// <c>this.vulner[D_X] &gt; 0.1</c> guard on every <c>dopEffect</c> branch
        /// (<c>Unit.as:3774-3808</c>).
        ///
        /// <para>Read from <see cref="Vulnerabilities"/>, which is the live table the damage path also
        /// uses — so a resistance an effect writes and the susceptibility test cannot disagree. The
        /// threshold is <c>0.1</c>, not <c>0</c>: a tiny-but-nonzero vulnerability is also refused,
        /// which is the oracle.</para>
        /// </summary>
        public bool IsSusceptibleTo(DamageType type)
            => Vulnerabilities.GetVulnerability(type) > 0.1f;

        /// <summary>
        /// Give this unit's effect set a resolver — the seam <c>RoomUnitSpawner</c> needs, because
        /// <c>AddComponent</c> never goes through VContainer and so cannot receive the content
        /// registry by injection. Idempotent.
        /// </summary>
        /// <remarks>
        /// Without this call the unit's set is resolver-less: every <c>addEffect</c> is refused and the
        /// unit reads as "no effects", which is indistinguishable from a working set on a unit nothing
        /// has hit. This is why the method exists on the controller rather than only on
        /// <c>UnitStats</c> — the spawner holds the controller.
        /// </remarks>
        public virtual void SetEffectResolver(IEffectDefinitionResolver resolver)
        {
            // A unit's params are written by the unit's own fields, so NPC mode. The player overrides
            // this to install CharacterStats as the sink and switch to player mode — see
            // PlayerController.
            _unitStats?.EnsureEffects(resolver, PersMode.Npc);
        }

        /// <summary>
        /// Hand this unit the room's particle emitter and install it as its effect-visual sink — the
        /// seam <c>RoomUnitSpawner</c> needs, because <c>AddComponent</c> never goes through
        /// VContainer and so cannot receive the emitter by injection. Idempotent.
        /// </summary>
        /// <remarks>
        /// <para><b>Same handover shape as <see cref="SetEffectResolver"/>, and it is the only path.</b>
        /// Without it the unit's effects still tick and still deal their damage — only the visuals are
        /// absent, which is why the failure is silent rather than loud. Installing the sink here rather
        /// than on <c>UnitStats</c> is deliberate: the stats object has no sprite, no transform and no
        /// room, so it cannot anchor a flame, and this controller is where all three live.</para>
        ///
        /// <para><b>Null is legal and means "show nothing".</b> A bare test spawn, or a scene with no
        /// particle world, leaves the sink unset and every effect-visual hook becomes a no-op. It is not
        /// an error: most units in a headless run are not on screen.</para>
        /// </remarks>
        public virtual void SetParticleEmitter(RoomParticleEmitter emitter)
        {
            _particleEmitter = emitter;

            if (_unitStats != null)
            {
                _unitStats.EffectVisualSink = emitter != null ? this : null;
            }
        }

        /// <summary>
        /// Emit a named particle at a Unity world position — the thin seam an archetype brain needs for
        /// the oracle's <c>Emitter.emit(id, loc, X, Y)</c> call sites.
        ///
        /// <para><b>Why a method on the unit rather than a field on the brain.</b> The emitter is
        /// injected into the <i>controller</i> by <c>RoomUnitSpawner</c> (<c>SetParticleEmitter</c>), the
        /// same handover that gives the effect visuals their sink; a brain has no DI access and would
        /// otherwise need a second, parallel wiring path that could be forgotten for exactly one
        /// archetype. Null-safe: a bare test spawn has no emitter, and <c>false</c> means "nothing was
        /// drawn", which is the honest answer there.</para>
        /// </summary>
        /// <returns><c>true</c> when the particle was accepted by the world.</returns>
        public bool EmitParticle(string particleId, Vector2 worldPositionPixels)
        {
            if (_particleEmitter == null || string.IsNullOrEmpty(particleId))
            {
                return false;
            }

            // The emitter works in Unity world units, not pixels — the conversion belongs to it, and
            // `Emit` does it (RoomParticleEmitter.TryToAs3Local). Passing pixels here would put every
            // quake flash a hundred times off screen.
            var worldUnits = new Vector3(
                worldPositionPixels.x * TileQueryConstants.PixelToUnit,
                worldPositionPixels.y * TileQueryConstants.PixelToUnit,
                transform.position.z);

            return _particleEmitter.Emit(particleId, worldUnits);
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
            //
            // The PREVIOUS support's displacement is read first, because it is the second argument of
            // the oracle's own landing test (`checkShelf(dy, osndy)`, Unit.as:2340 — `osndy` comes from
            // `stayOsn`, i.e. from the prop this unit stood on last tick, Unit.as:2014-2026). It is
            // what lets a platform that rose this tick still be found: without it the surface is above
            // the un-shifted band, the unit reads as airborne, and gravity wins every tick until the
            // feet are below the platform top and no query can see them.
            Vector2 supportCarry = Vector2.zero;
            ObjectInstance previousSupport = _groundProp;
            if (previousSupport != null)
            {
                MapObjectDynamicStateData previousState = previousSupport.runtimeState?.dynamicState;
                if (previousState != null &&
                    UnitCheckShelfMath.IsSupportStillCarrying(previousState.cdx, previousState.cdy))
                {
                    supportCarry = new Vector2(previousState.cdx, previousState.cdy);
                }
            }

            _groundProp = null;
            _groundPropSurfaceRoomLocalY = null;
            _groundPropCarryPixels = Vector2.zero;

            // AS3 `Unit.as:2251` — `shX1 = shX2 = 1` at the start of the pass, i.e. "nothing supports
            // either side yet". Every early return below therefore leaves the value the oracle's own
            // first write would have produced, instead of a stale fraction from a platform this unit
            // left ten ticks ago. `1` is maximal overhang, not "unknown" — see UnitOverhangMath.
            _overhangLeft = UnitOverhangMath.NoSupportOverhang;
            _overhangRight = UnitOverhangMath.NoSupportOverhang;

            if (_tileQuery == null || _collider == null)
            {
                return;
            }

            Rect probe = UnitGroundProbe.ToProbeRectPixels(
                _collider.bounds, TileQueryConstants.PixelToUnit);

            // AS3 `throu`: while the owner has asked to drop, a one-way platform is not ground. Walls
            // still are — the Wall branch of the ground probe returns before the platform branch is
            // consulted — so a unit holding this flag on a solid floor does not sink. That is what lets
            // the zombie hold it for as long as the target stays below.
            _isGrounded = _tileQuery.IsOnGround(
                probe,
                new TileQueryOptions(canFallThroughPlatforms: _dropThroughPlatforms));

            // AS3 `Unit.as:2301`/`:2305` — the tile loop writes the two overhang fractions as it
            // resolves the landing, and this is the same pass. The span is in world pixels and so is
            // `probe`, and the fraction is a *difference* of the two, so the room origin cancels and no
            // space conversion is needed here.
            ApplyOverhangIfGrounded(probe);

            // A tile under the feet wins outright — AS3 asks the tile grid first (the `while` loop at
            // Unit.as:2317-2330, then checkDiagon, then checkShelf last at :2340), and a floor is a
            // floor. Only when there is no tile is a prop a candidate for being the ground.
            //
            // `_dropThroughPlatforms` joins the early return for the same reason the tile answer wins:
            // AS3's `throu` skips `checkShelf` — the prop half — as well as the tile half
            // (Unit.as:2338-2341, `if(_loc5_ == 0 && !this.throu)`), so a crate must not become the floor
            // a unit asked to fall through.
            if (_isGrounded || _objectPhysicsLayer == null || _dropThroughPlatforms)
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

            // Hoisted into a local so the prop's overhang can be measured against the same feet rect the
            // search used — one definition of "where this unit's feet are", not two.
            Rect roomLocalFeet = RoomLocalFeetBoundsPixels();

            if (!_objectPhysicsLayer.TryFindGroundPropUnder(
                    roomLocalFeet,
                    TileQueryConstants.PorogGrounded,
                    out ObjectInstance support,
                    out float surfaceRoomLocalY,
                    supportCarry.y))
            {
                return;
            }

            _groundProp = support;
            _groundPropSurfaceRoomLocalY = surfaceRoomLocalY;
            _isGrounded = true;

            // AS3 `Unit.as:2722-2729` — `checkShelf` writes the *same* two fractions against the prop's
            // own `X1`/`X2`. It is the **last** writer in the oracle's order (tile loop, then
            // `checkDiagon`, then `checkShelf` at `:2340`), so on a crate the crate's edges are the
            // answer — which is exactly right, and is why a zombie that hopped onto a crate must read the
            // crate's edge and not the floor's. The prop's bounds are already room-local, and so is
            // `roomLocalFeet`, so the two sides of the subtraction share a space.
            Rect propBounds = support.GetApproximateBounds();
            ApplyOverhangFromSupport(roomLocalFeet, propBounds.xMin, propBounds.xMax);

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
        /// The ground/overhang half of AS3's <c>run()</c> ground pass, for a unit whose step is owned by
        /// its <c>TilePhysicsController</c> motor — the live path (<c>PfeDebugSettings.UnitMotor</c>).
        ///
        /// <para><b>Why this has to exist at all, and what its absence cost.</b> A motored unit makes
        /// <see cref="_hasTilePhysics"/> true, and <see cref="SimTick"/> and <c>FixedUpdate</c> both
        /// return on that predicate — so <see cref="StepUnit"/>, and with it
        /// <see cref="ResolveGroundState"/>, <b>never ran for a motored unit</b>. Two fields therefore
        /// never moved:</para>
        ///
        /// <list type="bullet">
        /// <item><description><see cref="OverhangLeft"/>/<see cref="OverhangRight"/> kept their field
        /// initialiser, <see cref="UnitOverhangMath.NoSupportOverhang"/> = <c>1</c> — which is
        /// <i>maximal</i> overhang, i.e. "nothing supports me". Every threshold in the family reads that
        /// as "I am standing on a lip", so a chasing zombie took its half hop 50 % of ticks and a
        /// patrolling one took it 10 % of ticks and turned around on the other 90 %.</description></item>
        /// <item><description><see cref="_isGrounded"/> was written by nothing but the Unity collision
        /// callbacks — whose answer <see cref="UnitGroundProbe"/>'s own remarks call marginal by
        /// construction at the 1 px seat — so it flickered, and
        /// <c>ZombieBrain.ResolveAnimState</c>'s <c>!IsGrounded</c> branch drew the <c>jump</c> row
        /// instead of <c>run</c>.</description></item>
        /// </list>
        ///
        /// <para><b>AS3 cannot be in that state, and that is the proof it is a port defect rather than a
        /// tuning problem.</b> <c>stay</c> and <c>shX1</c>/<c>shX2</c> are written by <i>one</i> pass
        /// (<c>Unit.as:2251</c> seeds <c>shX = 1</c>, the tile loop at <c>:2301</c>/<c>:2305</c> reduces
        /// it, and the same pass sets <c>stay</c>) — so "grounded with nothing under me" is unreachable
        /// there, and the port reached it by splitting one pass across two components and then never
        /// calling one half.</para>
        ///
        /// <para><b>The motor owns groundedness; this method does not re-decide it.</b> That is
        /// deliberate and it is the one thing that makes the two halves agree. The motor resolves
        /// <c>stay</c> inside its own sub-stepped collision walk, where it also knows the <i>direction</i>
        /// of the motion — it clears groundedness before the vertical branch whenever it moves up
        /// (<c>TilePhysicsController.MoveSingleStep</c>), which is AS3's own behaviour and which
        /// <c>IsOnGround</c> cannot reproduce, because that samples the feet and a unit one pixel into
        /// its first airborne tick still has its feet in the ground tile. Taking the flag as a parameter
        /// is therefore what keeps <see cref="_isGrounded"/> a single source of truth instead of a
        /// second opinion. <c>PlayerController</c> already reads the motor's flag the same way
        /// (<c>_isGrounded = _tilePhysics.IsGrounded</c>).</para>
        ///
        /// <para><b>The prop arm of the pass is deliberately not repeated here.</b> The motor has its own
        /// <c>checkShelf</c> — <c>TryLandOnShelfProp</c>/<c>RegisterSupportProp</c>, called from inside
        /// the substep that lands on the prop — and it syncs the transform itself, so
        /// <see cref="_groundProp"/>/<see cref="_groundPropCarryPixels"/> would be written for a
        /// <c>Move()</c> that a motored unit never runs. One half of the pass per owner.</para>
        /// </summary>
        /// <param name="grounded">The motor's <c>stay</c> for the step it has just completed — its own
        /// <c>TilePhysicsController.IsGrounded</c>. The motor must call this <i>after</i> its move, so
        /// both the flag and <c>Collider2D.bounds</c> describe the same position.</param>
        public void ResolveGroundStateFromMotor(bool grounded)
        {
            using (PFE.Core.Profiling.PfeProfiler.Region("unit.groundState.motor",
                "physics: the motor-driven half of the ground pass — AS3 shX1/shX2 only. Called by TilePhysicsController.StepMotor."))
            {
                // AS3 `Unit.as:2251` — `shX1 = shX2 = 1` at the start of the pass. Written before the
                // early return below for the same reason ResolveGroundState writes it before its own:
                // a unit that loses its room must not keep a stale ledge from one it left.
                _overhangLeft = UnitOverhangMath.NoSupportOverhang;
                _overhangRight = UnitOverhangMath.NoSupportOverhang;

                _isGrounded = grounded;

                if (_tileQuery == null || _collider == null)
                {
                    return;
                }

                Rect probe = UnitGroundProbe.ToProbeRectPixels(
                    _collider.bounds, TileQueryConstants.PixelToUnit);

                ApplyOverhangIfGrounded(probe);
            }
        }

        /// <summary>
        /// AS3 <c>Unit.as:2301</c>/<c>:2305</c> — turn the span of tiles holding this unit up into its two
        /// overhang fractions, when it is grounded.
        ///
        /// <para><b>Shared by both drivers</b> so the motor-less path and the motor path cannot compute
        /// the ledge differently — which is exactly the drift that made the zombie hop in the live build
        /// and not in the tests. The groundedness test is the caller's, already resolved, and reading
        /// <see cref="_isGrounded"/> here rather than re-deriving it keeps the two call sites' only
        /// difference the <i>source</i> of that flag.</para>
        /// </summary>
        private void ApplyOverhangIfGrounded(Rect probe)
        {
            if (!_isGrounded)
            {
                return;
            }

            if (_tileQuery.TryGetSupportSpan(
                    probe,
                    new TileQueryOptions(canFallThroughPlatforms: _dropThroughPlatforms),
                    out float supportLeftPx,
                    out float supportRightPx))
            {
                ApplyOverhangFromSupport(probe, supportLeftPx, supportRightPx);
            }
        }

        /// <summary>
        /// Turn a support span into this unit's two overhang fractions — AS3
        /// <c>shX1 = -(X1 - phX1)/scX</c> and <c>shX2 = (X2 - phX2)/scX</c>
        /// (<c>Unit.as:2301</c>/<c>:2305</c>).
        ///
        /// <para><b>Both arguments must be in the same pixel space, and it does not matter which.</b>
        /// The fraction is a difference over the body width, so room-local and world pixels give the
        /// identical answer — the room origin cancels. That is a convenience, not a licence: passing one
        /// side in world pixels and the other in room-local would offset every ledge in every room that
        /// is not at land (0,0), and look correct in every test built on an origin room. The two callers
        /// above are therefore deliberately consistent — the tile path passes world for both, the prop
        /// path room-local for both.</para>
        ///
        /// <para><b>The body width comes from the rect, not from <c>Stats</c>.</b> <c>scX</c> in the
        /// oracle <i>is</i> the collision box width (<c>Y1 = Y - scY</c>, <c>X1 = X - scX/2</c>), so the
        /// collider is the authority and a serialized stat could disagree with it.</para>
        /// </summary>
        /// <param name="feetRect">This unit's AABB, <c>xMin</c> at its left edge and <c>xMax</c> at its
        /// right.</param>
        /// <param name="supportLeftPx">The supporting tile's or prop's left edge, <c>phX1</c>.</param>
        /// <param name="supportRightPx">The supporting tile's or prop's right edge, <c>phX2</c>.</param>
        private void ApplyOverhangFromSupport(Rect feetRect, float supportLeftPx, float supportRightPx)
        {
            float width = feetRect.width;
            _overhangLeft = UnitOverhangMath.OverhangLeft(feetRect.xMin, supportLeftPx, width);
            _overhangRight = UnitOverhangMath.OverhangRight(feetRect.xMax, supportRightPx, width);
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
        /// Replaces: <c>dy += World.ddy * _loc1_.grav * this.grav</c> (from <c>Unit.as:1962-1965</c>)
        ///
        /// <para>AS3's gate is <c>!levit &amp;&amp; this.isLaz == 0</c> (<c>Unit.as:1962</c>), where
        /// <c>isLaz</c> means "standing on something"; <see cref="_isGrounded"/> is this port's
        /// equivalent and is now resolved from the room's tile query by
        /// <see cref="ResolveGroundState"/> (the collision callbacks below are the fallback for a unit
        /// with no room). The magnitude, the per-tile <c>grav</c> multiplier and the terminal clamp
        /// live in <see cref="UnitFallPhysics.FallSpeed"/> / <see cref="UnitFallPhysics.LiftSpeed"/>,
        /// which are pure and tested — the value was the wrong part, not the shape of the branch.</para>
        ///
        /// <para><b>The branch is a three-way, not a two-way.</b> The oracle's condition
        /// (<c>grav &gt; 0 &amp;&amp; dy &lt; maxdy*grav || grav &lt; 0 &amp;&amp; dy &gt; maxdy*grav</c>)
        /// reads as "integrate" but is really "integrate downward, integrate upward, or don't
        /// integrate": <c>grav &lt; 0</c> is the lift shaft that pushes a unit up, and
        /// <c>grav == 0</c> leaves <c>dy</c> untouched. The negative arm is therefore taken before the
        /// grounded check, because a lift must push off the floor.</para>
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
            // AS3 `forces()` (Unit.as:1915-1952) is `if(levit) … else if(isPlav) … else if(isFly) … else
            // gravity` — the levitation arm is FIRST and replaces every other one. An externally
            // levitated unit therefore takes neither gravity nor the flight brake: the thing holding it
            // up is the pull, and it is the pull that supplies its velocity.
            //
            // Without this the telekinetic's 2 px/frame² upward acceleration would fight a 1 px/frame²
            // downward one, and the victim would sink through the effect that is supposed to lift it.
            if (IsLevitated)
            {
                return;
            }

            // AS3 Unit.as:1915-1952 — `forces()` is a branch ladder, not a pair of additive steps, and
            // the two arms are mutually exclusive. `if(levit) … else if(isPlav) … else if(isFly) { … }
            // else { gravity }` — a flyer takes the `isFly` arm and never reaches `dy += World.ddy`.
            //
            // This gate is the reason a `fly="1"` placement (surfCalam, the camp's observer, the whole
            // bloat family) does not sink: before it existed, every one of them was spawned, told to fly
            // by its own script, and pulled straight back down by the same gravity a walking unit obeys.
            // The oracle's `isFly` arm also carries the flyer's own damping — the speed cap and the
            // brake deadzone — which is why this is `ApplyFlightBrake()` and not a bare `return`: with no
            // ground brake to bleed it off, a flyer whose thrust is never capped accelerates for ever.
            if (IsFlying)
            {
                ApplyFlightBrake();
                return;
            }

            // AS3 reads the tile under the unit's lower body on EVERY unit, every frame
            // (`loc.getAbsTile(X, Y - scY / 4).grav`, Unit.as:1960-1966, UnitPlayer.as:766-788) and
            // branches on its sign. This is the motor-less half of that read;
            // `TilePhysicsController.ApplyGravity` is the other.
            //
            // Both halves are required, and not for symmetry: `PfeDebugSettings.UnitMotor` is OFF by
            // default, so this is the LIVE path for every spawned unit while the player — whose rig
            // adds a motor unconditionally — is the only unit that takes the motor's copy. A read only
            // in the motor would make a lift shaft a player-only feature and leave every NPC standing
            // on the floor of a shaft that rises around them.
            float tileGravity = ResolveTileGravity();

            // The negative arm runs BEFORE the grounded check, because a lift has to push off the
            // floor: the oracle integrates gravity unconditionally, and a unit in a shaft is grounded
            // for as long as it takes to leave the tile it stands on.
            if (tileGravity < 0f)
            {
                _velocity.y = UnitFallPhysics.LiftSpeed(_velocity.y, StepSeconds, tileGravity);
                return;
            }

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

            _velocity.y = UnitFallPhysics.FallSpeed(_velocity.y, StepSeconds, tileGravity);
        }

        /// <summary>
        /// AS3 <c>loc.getAbsTile(X, Y - scY / 4).grav</c> — the gravity multiplier of the tile under the
        /// unit's <b>lower body</b>, not under its feet. The motor-less twin of
        /// <c>TilePhysicsController.ResolveTileGravity</c>; the two must answer the same question the
        /// same way, or <c>PfeDebugSettings.UnitMotor</c> decides whether a lift works.
        ///
        /// <para><b>Where the sample point comes from.</b> <c>UnitPlayer.as:767</c> and
        /// <c>Unit.as:1960</c> both probe a quarter of the body height above the feet (<c>Y</c> is the
        /// feet in the oracle, <c>scY</c> the body height). That offset is load-bearing for a lift
        /// shaft: the floor tile a unit stands on is <i>outside</i> the area (an area is stamped
        /// bottom-up over the air it lifts, not over the floor), so a probe at the feet would read 1
        /// and the unit would never leave the ground.</para>
        ///
        /// <para><b>Room-local pixels, because that is the space the tile grid lives in.</b>
        /// <see cref="RoomLocalFeetBoundsPixels"/> already subtracts the room origin, which is the one
        /// place the two spaces meet.</para>
        /// </summary>
        protected float ResolveTileGravity()
        {
            RoomInstance room = Room;
            if (room == null || _collider == null)
            {
                return TileData.DefaultGravity;
            }

            Rect feet = RoomLocalFeetBoundsPixels();
            float sampleX = feet.center.x;
            float sampleY = feet.y + feet.height * 0.25f;

            // The same +0.01 epsilon the motor's copy uses: a collider bound landing a hair under a
            // tile edge (39.9999 for 40) would floor into the previous tile, and the two drivers must
            // never disagree about which tile a unit is standing in.
            var coord = new Vector2Int(
                Mathf.FloorToInt(sampleX / TileQueryConstants.TileSize + 0.01f),
                Mathf.FloorToInt(sampleY / TileQueryConstants.TileSize + 0.01f));

            TileData tile = room.GetTileAtCoord(coord);

            // Missing tile answers the default, never 0: 0 means "weightless", so an out-of-room or
            // not-yet-filled tile would silently switch gravity off for whatever stands there.
            return tile != null ? tile.gravityMultiplier : TileData.DefaultGravity;
        }

        /// <summary>
        /// One step of AS3's <c>isFly</c> arm of <c>forces()</c> (<c>Unit.as:1934-1952</c>) — the
        /// replacement for gravity <i>and</i> for the ground brake, on a flying unit.
        ///
        /// <para><b>Why it is not folded into <see cref="ApplyGravity"/>'s body.</b> The two drivers
        /// disagree about where the unit's velocity lives — the motor keeps its own <c>dx</c>/<c>dy</c>
        /// and this class keeps <c>_velocity</c> in Unity units per second — so the read/write has to
        /// happen on the side that owns the value. This method is the motor-less side;
        /// <c>TilePhysicsController.ApplyGravity</c> is the other. Both route the arithmetic through
        /// <see cref="ApplyFlightBrakeToPixels"/>, so there is exactly one copy of it.</para>
        ///
        /// <para><b>Rate is 1 frame per step here.</b> This path's step is the sim clock's, which runs at
        /// <c>World.fps</c> = 30 — one step is one oracle frame, so the 0.7 decay applies whole. The
        /// motor passes its own <c>RateFramesPerStep</c>, because the legacy <c>FixedUpdate</c> path
        /// steps at 50 Hz and a whole 0.7 there would decay 1.67× too fast.</para>
        /// </summary>
        protected void ApplyFlightBrake()
        {
            As3VelocityPixelsPerFrame = ApplyFlightBrakeToPixels(As3VelocityPixelsPerFrame, 1f);
        }

        /// <summary>
        /// The flyer's damping for one step, as a pure function of an incoming velocity — the single
        /// place <b>both</b> drivers get it from.
        ///
        /// <para><b>Why the arithmetic lives here and not in each driver.</b> The two drivers disagree
        /// about where a unit's velocity lives — the motor keeps its own <c>dx</c>/<c>dy</c> in pixels
        /// per frame and this class keeps <c>_velocity</c> in Unity units per second — so each has to do
        /// its own read and write. But which <c>maxSpeed</c> and <c>brake</c> to resolve, and how to
        /// apply them, is one decision, and hand-writing it twice is exactly how the two would drift:
        /// the first draft had this class reading <c>_stats</c> and the motor reading
        /// <c>Unit.Definition</c>, which happen to be the same object today
        /// (<see cref="Definition"/>) and would not have to stay that way. The velocity is passed in and
        /// returned so neither driver needs to expose its storage.</para>
        ///
        /// <para><b>Rate.</b> The caller supplies the frame count its step covers: the sim clock runs at
        /// <c>World.fps</c> = 30, one step is one oracle frame, and the motor-less path passes
        /// <c>1f</c>. The motor passes its own <c>RateFramesPerStep</c>, because the legacy
        /// <c>FixedUpdate</c> path steps at 50 Hz and a whole 0.7 there would decay 1.67× too fast.</para>
        /// </summary>
        public Vector2 ApplyFlightBrakeToPixels(Vector2 velocityPixelsPerFrame, float rateFrames)
        {
            float maxSpeed = UnitFlyPhysics.ResolveMaxSpeed(_stats != null ? _stats.moveSpeed : 0f);
            float brake = UnitFlyPhysics.ResolveBrake(_stats != null ? _stats.braking : 0f);

            return UnitFlyPhysics.ApplyBrake(velocityPixelsPerFrame, maxSpeed, brake, rateFrames);
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
            // AS3 Unit.as:1967-1990 — the braking below is the `else` arm of the very branch whose
            // `isFly` arm ApplyGravity already took. Running both would apply two different damping laws
            // to one velocity: the flyer's 0.7 cap-and-deadzone, and then a constant ground deceleration
            // on x. The oracle applies exactly one, and for a flyer it is the former.
            if (IsFlying)
            {
                return;
            }

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
        /// Explicitly set the unit's facing direction (+1 for Right, -1 for Left).
        /// </summary>
        public virtual void SetFacing(int direction)
        {
            _facingDirection = direction >= 0 ? 1 : -1;
            ApplyFacingToTransform();
        }

        /// <summary>
        /// The placement's <c>ai</c> attribute, verbatim — AS3 <c>Unit.aiTip</c>
        /// (<c>Unit.as:404</c>, written once at <c>:619-621</c>: <c>if(param3.@ai.length()) this.aiTip =
        /// param3.@ai;</c>).
        /// </summary>
        /// <remarks>
        /// <para><b>Stored raw and interpreted by family, because that is the oracle's own shape.</b>
        /// AS3 keeps one string on the base class and lets each family's constructor decide what its
        /// spellings mean. <see cref="PlacementAi"/> is the interpreted form; this is the input, kept so a
        /// reader can tell "authored nothing" from "authored a spelling I do not know" — the difference
        /// between <c>ai</c> absent and <c>Rooms.as:1936</c>'s <c>ai="quet"</c> typo, which the oracle
        /// treats as absent and which any normalising match would silently repair.</para>
        /// </remarks>
        public string PlacementAiTip { get; private set; } = string.Empty;

        /// <summary>
        /// Which family's rules interpret this unit's placement <c>ai</c> — AS3's own discriminator.
        /// </summary>
        /// <remarks>
        /// <para><b>Why this has to be overridden rather than derived from the unit id.</b> The family is
        /// a <i>class</i> fact in AS3, and the port's class tree does not mirror AS3's:
        /// <c>NecrosController</c> derives from <c>ArmedShooterController</c> here because it shares the
        /// weapon-rolling controller, but <c>UnitNecros extends Unit</c> in the oracle and therefore reads
        /// only <c>stay</c>. A derived family would silently hand the necros the raider's
        /// <c>quiet</c>/<c>sniper</c> spellings. So each concrete controller states its own family, and
        /// the default is <see cref="UnitAiFamily.Generic"/> — "no <c>aiTip</c> reader at all", which is
        /// what the base <c>Unit</c> and <c>UnitPon</c> are.</para>
        /// </remarks>
        protected virtual UnitAiFamily AiFamily => UnitAiFamily.Generic;

        /// <summary>
        /// The interpreted form of <see cref="PlacementAiTip"/>, computed once in
        /// <see cref="ApplyPlacement"/>. Defaults to <see cref="PlacementAiEffect.None"/>, which is the
        /// correct answer for a unit that was never given a placement (a test spawn) and for every
        /// <see cref="UnitAiFamily.Generic"/> family.
        /// </summary>
        PlacementAiEffect _placementAi;

        // The placement `weap` pair — see ApplyPlacement for why presence and value are separate.
        bool _placementWeapPresent;
        string _placementWeapText = string.Empty;

        /// <summary>
        /// The interpreted placement <c>ai</c> — <see cref="UnitPlacementAi.Interpret"/> of
        /// <see cref="PlacementAiTip"/> for <see cref="AiFamily"/>.
        /// </summary>
        public PlacementAiEffect PlacementAi => _placementAi;

        /// <summary>
        /// Whether this placement pins the unit to its post — AS3's <c>stroll = false</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>Two sources, and only one family honours the second.</b> The placement's own
        /// <c>ai</c> spelling is the first (<c>UnitRaider.as:227-241</c> — <c>stay</c>, <c>quiet</c>,
        /// <c>sniper</c>; <c>UnitAlicorn.as:206-214</c> — <c>stay</c>, <c>quiet</c>;
        /// <c>UnitAIRobot.as:49-52</c> — <c>quiet</c> alone; <c>UnitNecros.as:102</c> — <c>stay</c>
        /// alone). The unit <i>row</i>'s own <c>&lt;un stay='1'/&gt;</c> is the second
        /// (<c>UnitRaider.as:351-354</c>), and <c>UnitRaider</c>'s constructor is its only reader in the
        /// whole tree — so the row half is gated on <see cref="UnitAiFamily.Raider"/> rather than applied
        /// to every unit. Exactly one unit authors it (<c>zebra3</c>, <c>AllData.as:361</c>).</para>
        ///
        /// <para><b>What it suppresses, and what it must not.</b> Only the idle <i>wander</i> roll:
        /// <c>if(this.stroll) aiState = Math.floor(Math.random() * 2); else aiState = 0;</c>
        /// (<c>UnitRaider.as:725-742</c>, and the same test at <c>UnitAIRobot.as:204</c> and
        /// <c>UnitAlicorn.as:609</c>). A pinned unit still turns to face, still hears, still sees, still
        /// raises the alarm and still shoots. Suppressing more would make <c>ai="stay"</c> mean "deaf and
        /// blind", which is not what the data says.</para>
        ///
        /// <para><b>Read lazily, not cached in <see cref="ApplyPlacement"/>.</b> The row half needs
        /// <see cref="Definition"/>, and the spawn order is definition → placement, but a caller that
        /// applies a placement before assigning a definition must not get a stale <c>false</c>. The
        /// property form is correct under either order.</para>
        /// </remarks>
        public bool PlacementStaysPut =>
            (_placementAi & PlacementAiEffect.PinsToPost) != 0
            || (AiFamily == UnitAiFamily.Raider && Definition != null && Definition.willStayInPlace);

        /// <summary>
        /// Whether this placement mutes the unit's idle voice barks — AS3's <c>quiet = true</c>
        /// (<c>UnitRaider.as:231-235</c>, <c>UnitAlicorn.as:210-214</c>,
        /// <c>UnitAIRobot.as:49-52</c>).
        /// </summary>
        /// <remarks>
        /// <para><b>Stored, but with no consumer in the port.</b> The flag's only readers in the oracle
        /// are two voice barks — <c>if(!this.quiet &amp;&amp; isrnd(0.001)) this.replic("neutral");</c> at
        /// <c>UnitRaider.as:1060</c> and <c>:1079</c> — and the port has no voice-line table for enemy
        /// units at all (the NPC <c>replic</c> verbs queue a script action and say so in
        /// <c>UnitNpcRules</c>). So this is data fidelity, not behaviour: it exists so that a future bark
        /// system reads the oracle's own switch instead of inventing one, and the gap is named here rather
        /// than left for someone to rediscover. Its <i>other</i> effect — clearing <c>stroll</c>, which the
        /// oracle's shared branch does at the same time — is modelled, as
        /// <see cref="PlacementStaysPut"/>.</para>
        /// </remarks>
        public bool PlacementIsQuiet => (_placementAi & PlacementAiEffect.Quiet) != 0;

        /// <summary>
        /// Whether this placement stops the unit closing distance — AS3's <c>moving = false</c>, set by
        /// the <c>sniper</c> spelling alone (<c>UnitRaider.as:236-241</c>).
        /// </summary>
        /// <remarks>
        /// <c>UnitRaider.as:768-771</c> is the reader that matters: <c>if(!this.moving) aiState = 4;</c>
        /// runs <i>after</i> the state has been chosen and before it executes, so it beats even the dash
        /// roll at <c>:765</c>. Six placements in the shipped rooms author <c>ai="sniper"</c> — two
        /// slavers, two rangers, one zebra, one raider.
        /// </remarks>
        public bool PlacementHoldsPosition => (_placementAi & PlacementAiEffect.HoldsPosition) != 0;

        /// <summary>
        /// Whether this placement raises the unit's weapon skill to 2 — AS3's <c>weaponSkill = 2</c>, set
        /// by the <c>sniper</c> spelling alone (<c>UnitRaider.as:236-241</c>).
        /// </summary>
        /// <remarks>
        /// <para><b>Named, not consumed.</b> <c>Unit.weaponSkill</c> (<c>Unit.as:314</c>, default 1,
        /// otherwise the unit row's <c>&lt;comb skill&gt;</c> at <c>:1184</c>) reaches the shot through
        /// <c>owner.weaponSkill</c>, where it <i>divides</i> the spread —
        /// <c>deviation * (1 + breaking * 2) / skillConf / (_loc1_ + 0.01)</c>
        /// (<c>Weapon.as:1454-1458</c>, and the same read in <c>WClub.as:575</c> and
        /// <c>WThrow.as:133</c>). So <c>2</c> means "half the spread".</para>
        ///
        /// <para>The port's enemy weapons get their skill from an <c>IWeaponStatSource</c>, and an enemy
        /// unit has none — <c>RangedWeaponController</c> resolves
        /// <c>HitAvoidance.UnknownOwnerSkillLevel</c> and the spread is the weapon's own. Feeding the
        /// flag into that chain is enemy weapon-skill plumbing, not import repair, and landing the read
        /// without the consumer would be exactly the "writer before reader" trap
        /// <c>UnitDefinition.canResurrect</c> was already in. Recorded here so the switch is not lost.</para>
        /// </remarks>
        public bool PlacementUsesSniperSkill => (_placementAi & PlacementAiEffect.SniperSkill) != 0;

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

            // AS3 `this.hero = param3.@hero` (Unit.as:645). The placement carries it, the definition
            // never does — see _heroTier. Absent or unparseable leaves the tier at 0, which is AS3's
            // field default and therefore an ordinary, non-hero unit.
            if (int.TryParse(placement.GetAttribute("hero", string.Empty), NumberStyles.Integer,
                             CultureInfo.InvariantCulture, out int hero))
            {
                _heroTier = hero;
            }

            // AS3 `if(param3 && param3.@fraction.length()) fraction = param3.@fraction;`
            // (UnitTurret.as:140-142, and the same read on every other placed unit). Assigned only
            // when the placement actually authored one — `fraction = null` on the placement means
            // "the row was silent", which must leave the definition's value standing. Writing the
            // nullable straight through would be wrong in the other direction: a second
            // ApplyPlacement call with a silent placement would erase an earlier override.
            if (placement.fraction.HasValue)
            {
                _instanceFaction = placement.fraction;
            }

            // AS3 `Unit.as:619-621` — the placement's `ai` attribute, read ONCE on the base unit into
            // `aiTip`, then interpreted by each family's own constructor (`UnitRaider.as:227-241`,
            // `UnitAlicorn.as:206-222`, `UnitAIRobot.as:49-52`, `UnitNecros.as:102`). The port had NO
            // reader for a non-NPC placement's `ai` at all (`UnitNPCController.SetPlacement` was the only
            // `GetAttribute("ai")` in the tree), so the surf room's seven raiders — six authored
            // `ai="stay"` and one `ai="quiet"` — patrolled where the oracle leaves them standing, and
            // three `ai="stay"` hellhounds would have been pinned by a global mapping even though the
            // oracle's hellhound reads none of it.
            //
            // One read, one copy: `AiFamily` is the discriminator and `UnitPlacementAi.Interpret` is the
            // table. A brain reads the answer back through the `Placement*` properties rather than
            // re-reading `GetAttribute("ai")`, which is how two units of the same family end up
            // disagreeing about their own data.
            PlacementAiTip = placement.GetAttribute("ai", string.Empty) ?? string.Empty;
            _placementAi = UnitPlacementAi.Interpret(AiFamily, PlacementAiTip);

            // AS3 `UnitRaider.as:170` — the placement's `weap` attribute, the middle arm of the raider
            // family's three-way weapon choice. Captured here, next to `ai`, for the same reason: it is a
            // placement fact, `ApplyPlacement` is the one place a placement is read, and a second reader
            // would be a second copy.
            //
            // TWO fields, not one, and that is not redundancy. `GetAttribute("weap", "")` answers "" for
            // both an absent attribute and an authored empty one, and the oracle tells the two apart —
            // `param3.@weap.length()` counts attributes, so `weap=""` is present and means "explicitly
            // unarmed", while absent means "roll the unit row". Two shipped zebras author `weap=""`
            // (RoomsMbase.as:4246, RoomsSerial.as:6253) and would be armed by a value-only read.
            //
            // Only `UnitRaider`'s own subclasses read this in AS3, so the reader gates on
            // `AiFamily == Raider` exactly as the `ai` mapping does; see UnitPlacementWeapon.
            _placementWeapPresent = MapObjectDataUtility.HasAttribute(
                placement.attributes, UnitPlacementWeapon.AttributeName);
            _placementWeapText = _placementWeapPresent
                ? (placement.GetAttribute(UnitPlacementWeapon.AttributeName, string.Empty) ?? string.Empty)
                : string.Empty;

            // AS3 `Unit.create` -> the base constructor's `inter` — the Interact built from the placed
            // node's own `<item>` children, which `Unit.dropLoot` opens with (`if(inter) inter.loot();`,
            // Unit.as:4450-4455). Kept by reference rather than copied: the `UnitInstance` is the room's
            // own record, it outlives the unit, and AS3's `inter` likewise holds the node.
            _placementItems = placement.items;
        }

        /// <summary>The placement's own <c>&lt;item&gt;</c> children, or <c>null</c>.</summary>
        public List<MapObjectItemData> PlacementItems => _placementItems;

        private List<MapObjectItemData> _placementItems;

        /// <summary>
        /// Whether this placement authored a <c>weap</c> attribute at all, regardless of its value —
        /// AS3's <c>param3.@weap.length()</c>. See <see cref="PlacementWeaponMode"/> for why the two
        /// questions are separate.
        /// </summary>
        public bool PlacementWeapPresent => _placementWeapPresent;

        /// <summary>
        /// The placement's <c>weap</c> text, or <c>""</c> when the attribute was absent.
        /// </summary>
        public string PlacementWeapText => _placementWeapText;

        /// <summary>
        /// What <see cref="PlacementWeapPresent"/>/<see cref="PlacementWeapText"/> mean for this unit's
        /// weapon — AS3's three-way choice, as a value.
        /// </summary>
        public PlacementWeaponMode PlacementWeaponMode =>
            UnitPlacementWeapon.Resolve(_placementWeapPresent, _placementWeapText);

        /// <summary>
        /// AS3 <c>Unit.command(param1, param2)</c> (<c>Unit.as:4784-4811</c>) — a script action
        /// addressed to this unit by uid or code.
        ///
        /// <para><b>This is the base class's vocabulary, and it is short on purpose.</b> The oracle's
        /// base <c>Unit.command</c> handles exactly two verbs, <c>activate</c> and <c>fraction</c>; the
        /// rest of the script-facing verbs live on <c>UnitNPC</c> (<c>show</c>, <c>fly</c>,
        /// <c>sign</c>, <c>ai</c>, <c>rep</c>, …) and on the other subclasses. Putting the NPC verbs
        /// here would make every raider answer a <c>show</c> it has no art for.</para>
        ///
        /// <para><b><c>activate</c> is the resurrection verb, not a spawn.</b>
        /// <c>Unit.as:4787-4798</c> clears <c>noAct</c>/<c>disabled</c>, re-runs <c>setNull(true)</c> +
        /// <c>addVisual()</c> and emits the <c>tele</c> particle — it is what brings a removed unit
        /// back. The port models the two flags it owns (<see cref="IsVisible"/> plus the effect-free
        /// path) and leaves the particle emitter to the effect system, which the spawner already
        /// wires.</para>
        ///
        /// <para><b><c>fraction</c> rewrites the unit's side at runtime</b> and, in the oracle, also
        /// re-arms <c>warn</c> — <c>warn = (fraction != F_PLAYER)</c>. That second half matters: it is
        /// what makes a captured turret stop shooting the player, and it is why this writes the
        /// per-instance override rather than the definition.</para>
        /// </summary>
        /// <returns>
        /// <c>true</c> when this class or a subclass recognised and ran the verb. <c>false</c> lets the
        /// caller try the object host and then report an unmatched uid — see
        /// <see cref="PFE.Systems.Map.Scripting.IUnitCommandHost"/>.
        /// </returns>
        public virtual bool Command(string verb, string value)
        {
            if (string.IsNullOrEmpty(verb))
            {
                return false;
            }

            switch (verb.Trim().ToLowerInvariant())
            {
                case "activate":
                    // Unit.as:4787-4798. The oracle clears `noAct` and `disabled` and re-adds the
                    // visual; the port's equivalent pair is the visibility flag and (for the callers
                    // that set it) the dead flag, which `IsAlive` derives from the stats.
                    SetVisible(true);
                    return true;

                case "fraction":
                    // Unit.as:4799-4810 — `this.fraction = int(param2)`, and `warn` follows. Non-numeric
                    // coerces to NaN in AS3 and every comparison against it is false, so the port
                    // treats an unparseable value as "leave it alone" rather than inventing Neutral —
                    // the same call RoomPopulator.ResolvePlacementFaction makes for the authored case.
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture,
                                     out int fraction))
                    {
                        _instanceFaction = (FactionType)fraction;
                    }

                    return true;

                default:
                    return false;
            }
        }

        /// <summary>
        /// AS3 <c>Unit.hero</c> — the boss tier this unit was placed with (<c>Unit.as:645</c>). <c>0</c>
        /// for an ordinary unit. Read by the death drop's essence gate
        /// (<see cref="UnitDeathDrop.ShouldDropEssence"/>).
        /// </summary>
        public int HeroTier => _heroTier;

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
        /// AS3 <c>Unit.levit</c> — a unit under someone else's levitation, i.e. the value <b>2</b>.
        /// The zombie telekinetic sets it on its victim (<c>UnitZombie.as:1178</c>) and clears it when
        /// the super ends (<c>:1216-1219</c>).
        ///
        /// <para><b>Only the external value lives here.</b> AS3's <c>levit == 1</c> is the unit's
        /// <i>own</i> levitation (the player's perk, a flyer's hover) and the port already models those
        /// in their own systems — <c>PlayerLocomotionController</c>'s levitation state and
        /// <c>IsFlying</c>. Reproducing them here as a second flag would give "can this unit float" two
        /// answers that could disagree. The telekinetic's check is <c>levit != 1</c>
        /// (<c>UnitZombie.as:1170</c>), i.e. "do not grab something that is already holding itself up";
        /// the port's equivalent is <see cref="IsFlyingUnit"/>, tested by the caller.</para>
        ///
        /// <para><b>Why it has to be a flag rather than a velocity.</b> Both gravity drivers branch on
        /// it — <c>UnitController.ApplyGravity</c> and <c>TilePhysicsController.ApplyGravity</c> — and
        /// the oracle's <c>forces()</c> puts <c>if(levit)</c> <i>first</i>, ahead of <c>isPlav</c> and
        /// <c>isFly</c>. Injecting velocity alone would leave gravity adding 1 px/frame² downward every
        /// tick, so a levitated player would sink through the pull that is supposed to lift them.</para>
        /// </summary>
        public bool IsLevitated { get; private set; }

        /// <summary>
        /// Set or clear <see cref="IsLevitated"/>. The caller owns the lifetime: AS3 clears it in
        /// <c>superSilaVse()</c> and on death (<c>UnitZombie.as:353, 1214-1221</c>), and a stale flag
        /// would leave a unit floating for ever with nothing on screen to explain it.
        /// </summary>
        public void SetLevitated(bool levitated)
        {
            IsLevitated = levitated;
        }

        /// <summary>
        /// Add a velocity to this unit in <b>AS3's units — pixels per 30 Hz frame</b>, routed to
        /// whichever movement layer is actually stepping it.
        ///
        /// <para><b>Why <see cref="AddForce"/> is not enough.</b> That method writes
        /// <c>_velocity</c>, the Unity-units-per-second field the <i>motor-less</i> path integrates. A
        /// motor-driven unit (the shipped default) keeps its velocity in <c>TilePhysicsController.dx/dy</c>
        /// and never reads <c>_velocity</c>, so a force added there is silently discarded — the same
        /// two-storage trap <c>EnemyBrain.MoveHorizontal</c> documents for horizontal speed.</para>
        ///
        /// <para><b>Additive, not absolute</b> — AS3's telekinetic writes
        /// <c>teleUnit.dx += l1</c> (<c>UnitZombie.as:1181</c>), so a pull composes with whatever the
        /// target is already doing instead of teleporting it to a speed.</para>
        /// </summary>
        /// <param name="velocityPixelsPerFrame">The delta to add, in AS3's px/frame.</param>
        public void AddVelocityPixelsPerFrame(Vector2 velocityPixelsPerFrame)
        {
            if (_cachedTilePhysics != null)
            {
                _cachedTilePhysics.AddForce(velocityPixelsPerFrame.x, velocityPixelsPerFrame.y);
                return;
            }

            _velocity += velocityPixelsPerFrame * TileQueryConstants.PerFrameVelocityToUnitsPerSecond;
        }

        /// <summary>
        /// AS3 <c>Unit.throu</c> (<c>Unit.as:296</c>) — ask this unit's ground probe to ignore one-way
        /// platforms, so it falls through whatever it is standing on.
        ///
        /// <para><b>The caller asserts it every tick it wants the drop</b>, exactly as
        /// <c>UnitZombie.control()</c> recomputes the oracle's flag every frame (<c>:857-864</c>). This is
        /// a level, not a one-shot: see <see cref="_dropThroughPlatforms"/> for why, and for why the
        /// clearing is the writer's job rather than a timer's.</para>
        ///
        /// <para><b>Nothing happens until the next step.</b> The flag is read by
        /// <see cref="ResolveGroundState"/>, which <see cref="StepUnit"/> runs once per step; the caller
        /// is the brain, whose tick is ordered before the unit's. Setting it after a step and reading the
        /// answer in the same tick would see the previous tick's groundedness — the same ordering trap
        /// <c>TilePhysicsController</c>'s motor-side verb has.</para>
        /// </summary>
        public virtual void SetDropThroughPlatforms(bool shouldDropThrough)
        {
            _dropThroughPlatforms = shouldDropThrough;
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
        public void SetGrounded(bool grounded) => _isGrounded = grounded;
        public Vector2 Velocity => _velocity;
        public int FacingDirection => _facingDirection;

        /// <summary>
        /// AS3 <c>Unit.shX1</c> — how much of this unit's body width hangs past the <b>left</b> edge of
        /// whatever it is standing on. Negative means that side is fully supported; above <c>0.5</c> means
        /// more than half the body is over the edge; <c>1</c> means nothing was found under it at all,
        /// which is <i>maximal</i> overhang and not a sentinel. Resolved by
        /// <see cref="ResolveGroundState"/>, so it lags the current tick's movement by one step — which is
        /// exactly what AS3 does, since <c>control()</c> reads what the previous frame's <c>run()</c>
        /// wrote. See <see cref="PFE.Entities.Units.UnitOverhangMath"/>.
        /// </summary>
        public float OverhangLeft => _overhangLeft;

        /// <inheritdoc cref="OverhangLeft"/>
        public float OverhangRight => _overhangRight;

        /// <summary>
        /// The overhang on the side this unit is facing — AS3's <c>aiNapr &lt; 0 ? shX1 : shX2</c>
        /// selection, which every consumer in the family writes out inline. Kept here rather than at each
        /// call site so "the edge ahead" has one definition and cannot be read as "the edge behind" in one
        /// archetype and not another.
        ///
        /// <para><b>It uses the unit's <i>actual</i> facing, not a desired direction.</b> AS3's
        /// <c>aiNapr</c> is the direction the AI wants to go this tick, which is aliased to <c>storona</c>
        /// everywhere either is written — so for a patrolling unit the two coincide. For a chasing one
        /// they do not: the port re-derives facing from the target's X every tick
        /// (<c>EnemyBrain.TickCombatChase</c>), so "I want to go left but I am stuck" is not
        /// representable. That is the honest reading of what the port has, and it is recorded rather than
        /// papered over — see <c>docs/OnEnemiesAndAi/26_…md</c> §2.2.</para>
        /// </summary>
        public float OverhangAhead => UnitOverhangMath.OverhangToward(
            _facingDirection, _overhangLeft, _overhangRight);
        public UnitDefinition Stats => _stats;
        public UnitStats UnitStats => _unitStats;

        /// <summary>
        /// This unit's <b>feet</b> in world units — AS3's <c>Y</c>, which is also this transform's own
        /// position. Anything that needs "how tall is this unit and where does it stand" — the weapon
        /// hold point, a mount, a muzzle height — starts here.
        ///
        /// <para><b>Why the transform and not the collider.</b> Three independent places agree that
        /// the unit's origin is the bottom of its body box, so <c>transform.position.y</c> <i>is</i>
        /// the feet and no collider arithmetic is needed:</para>
        /// <list type="bullet">
        /// <item><description><c>RoomSetup.FindPlayerSpawnPixels</c> returns the top of the ground tile
        /// and comments it "Return pixel position at feet".</description></item>
        /// <item><description><c>TilePhysicsController</c>'s gizmo calls its pixel position
        /// <c>colliderFeet</c> and grows the box <b>upward</b> from it
        /// (<c>TilePhysicsController.cs:1587-1590</c>).</description></item>
        /// <item><description>AS3 <c>Unit.as:1875-1878</c> — <c>Y1 = Y - scY</c> is the box's top and
        /// <c>Y2 = Y</c> its bottom — which <c>UnitSpriteAnchor</c> reproduces for spawned
        /// units.</description></item>
        /// </list>
        ///
        /// <para><b>Do not derive this from <c>collider.bounds.min.y</c>.</b> That is the feet only
        /// when the collider's offset is <c>+height/2</c>, which is what
        /// <c>RoomUnitSpawner.cs:330</c> gives every <i>spawned</i> unit — but the hand-authored
        /// <c>Player.prefab</c> ships a <c>BoxCollider2D</c> with offset <c>(0, 0)</c>, so its bounds
        /// hang half a body below the origin. Using the bounds would put a held weapon 0.35 units too
        /// low for the player and correct for everything else, which is the worst kind of
        /// inconsistency. <see cref="Move"/> uses the bounds deliberately, for the ground-prop probe
        /// only; that is a different question from "where are this unit's feet".</para>
        /// </summary>
        public float FeetWorldY => transform.position.y;

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
        /// <para><b>Three layers, in AS3's order.</b> <c>Unit.as:454</c> defaults the field to
        /// <c>0</c>; the unit's own <c>&lt;unit&gt;</c> row or its <c>parent</c> chain supplies the
        /// value (<c>UnitDataImporter</c> resolves that chain); and the <b>placed</b> <c>&lt;obj&gt;</c>
        /// row overrides both — <c>UnitTurret.as:140-142</c> <c>if(param3 &amp;&amp;
        /// param3.@fraction.length()) fraction = param3.@fraction;</c>. The placement layer is
        /// <see cref="_instanceFaction"/>, written by <see cref="ApplyPlacement"/>, and it is why the
        /// camp turrets are the player's: every one is authored <c>fraction="100"</c>
        /// (<c>RoomsCamp.as:86-90, 127-130, 794</c>) while the <c>turret</c> row
        /// (<c>AllData.as:1758</c>) carries no <c>fraction</c> at all, so the definition cannot supply
        /// it. <c>UnitTurret</c> does not default to <c>F_PLAYER</c> in its constructor either —
        /// only <c>hack()</c> (<c>:397-410</c>) assigns it.</para>
        ///
        /// <para><b>Read as the definition's value when the placement is silent.</b>
        /// <c>_instanceFaction ?? _stats.fraction</c> — not <c>?? Neutral</c>. A silent placement means
        /// "no override", and the definition is a real answer for the 132 units whose rows carry one.
        /// </para>
        ///
        /// <para><b>Not written back to <c>_stats</c>.</b> <c>UnitDefinition</c> is a ScriptableObject
        /// shared by every instance of the unit, so a runtime write would leak one placement's
        /// override into the project asset and into every other room that spawns the same id.</para>
        /// </summary>
        public virtual FactionType Faction =>
            _instanceFaction ?? (_stats != null ? _stats.fraction : FactionType.Neutral);

        /// <summary>
        /// Re-point this unit's faction at runtime — AS3's <c>fraction = Unit.F_PLAYER</c> inside
        /// <c>UnitTurret.hack()</c> (<c>UnitTurret.as:401</c>, <c>:410</c>).
        ///
        /// <para><b>Writes the placement layer, never the definition.</b> <c>UnitDefinition</c> is a
        /// ScriptableObject shared by every instance of the id, so writing the stat would reprogram
        /// every turret in the project the moment one terminal is used. This is the same rule
        /// <see cref="Faction"/> documents for the read side.</para>
        /// </summary>
        public void SetRuntimeFaction(FactionType faction)
        {
            _instanceFaction = faction;
        }

        /// <summary>
        /// AS3 <c>Unit.hack(param1 = 0)</c> (<c>Unit.as:4298-4300</c>) — the base is an <b>empty
        /// method</b>, so a hack terminal sweeping the room does nothing to an ordinary raider. Only
        /// units that override it react; in the shipped game that is <c>UnitTurret</c>.
        ///
        /// <para><b>0 = shut down, 1 = reprogram to the player, 2 = reprogram silently.</b> The three
        /// arms are <c>UnitTurret.as:385-414</c>. A <c>hack_robot</c> terminal passes
        /// <c>World.w.pers.security</c>, so <c>Pers.security == 0</c> turns turrets <i>off</i> and a
        /// security perk turns them <i>friendly</i> — the same verb, two very different outcomes.</para>
        /// </summary>
        public virtual void Hack(int mode = 0)
        {
        }

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
        /// <remarks>
        /// <para><b>A dead unit is not a no-op — it is a corpse, and AS3 keeps damaging it.</b>
        /// <c>Unit.damage()</c> has no <c>sost</c> guard (<c>Unit.as:3527-3995</c>), so a hit on a body
        /// still runs the whole reduction block, still does <c>hp -= param1</c>, and still re-enters
        /// <c>die()</c> (<c>:3713-3745</c>) — which is the mechanism that eventually destroys it. The
        /// <see cref="IsAlive"/> branch below is the death transition; the corpse branch is the
        /// <i>continuation</i> of the same field, and it must not run <see cref="RaiseDeath"/> again
        /// (see <see cref="RaiseDeath"/>).</para>
        /// </remarks>
        public virtual void TakeDamage(float damage)
        {
            if (_unitStats == null)
            {
                Debug.LogWarning($"[{GetType().Name}] TakeDamage called but no UnitStats assigned!");
                return;
            }

            if (!IsAlive)
            {
                ApplyCorpseDamage(damage);
                return;
            }

            _unitStats.Damage(damage);

            // Handle death if applicable
            if (!IsAlive)
            {
                RaiseDeath();
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

            // A corpse has no armour integrity left to wear and no shield to spend — the oracle's
            // armour pool was zeroed by `die()` (`Unit.as:4369`), and re-running the wear path here
            // would write condition onto an item the body is no longer holding. Only the HP half
            // applies, which is exactly what is left of AS3's `hp -= param1`.
            if (!IsAlive)
            {
                ApplyCorpseDamage(outcome.HpDamage);
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
        /// The unit's spell/boss shield pool, read by the damage resolver — AS3 <c>Unit.shithp</c>.
        /// <c>0</c> when no stats are assigned or no shield is up, which is AS3's own field default.
        /// </summary>
        /// <remarks>
        /// <para><b>The state lives on <see cref="UnitStats"/>, the rule lives in
        /// <c>SpellShield</c>, and this is only the read the resolver needs.</b> Same split as
        /// <see cref="SkinResistance"/>: the resolver reads it locally and hands back the post-hit pool
        /// on <c>DamageOutcome.ShieldHpAfter</c>, which <c>UnitStats.ApplyDamage</c> writes down.</para>
        ///
        /// <para><b>This is what makes the alicorn's shield actually absorb damage.</b>
        /// <c>AlicornController</c> used to keep its own private <c>_shieldHp</c> and subtract from it
        /// in an overridden <c>ApplyDamage</c> — a second, divergent absorption that the resolver knew
        /// nothing about, so the shield neither fed <c>shitArmor</c> into the reduction nor set
        /// <c>allVulnerMult</c>, and nothing else in the game could see it.</para>
        /// </remarks>
        public virtual float ShieldHp => _unitStats?.ShitHp ?? 0f;

        /// <summary>
        /// The shield's flat rating — AS3 <c>Unit.shitArmor</c> (<c>Unit.as:160</c>, default <c>20</c>).
        /// Read only while <see cref="ShieldHp"/> is positive.
        /// </summary>
        /// <remarks>
        /// Forwarded rather than answered with AS3's field default of <c>20</c>: the rating is
        /// per-unit (the alicorn sets <c>25</c>, or <c>50</c> on tr3 — <c>UnitAlicorn.as:152/172</c>) and
        /// <c>UnitTurret.as:507</c> sets it to <c>0</c> while its shield is up, so a constant here would
        /// be wrong for both. A unit with no stats answers <c>0</c>, which contributes nothing — the
        /// unshielded behaviour.
        /// </remarks>
        public virtual float ShieldArmor => _unitStats?.ShitArmor ?? 0f;

        /// <summary>
        /// The tier that selects this unit's shield <b>dome</b> — AS3's <c>tr</c> as the shield visual
        /// reads it (<c>UnitAlicorn.as:191</c>). <c>0</c> for every unit that is not an alicorn.
        /// </summary>
        /// <remarks>
        /// <para><b>It is a read on the controller rather than a field on the unit's data row because
        /// the oracle decides it by class.</b> <c>if(this.tr == 3) new visShit2() else new visShit()</c>
        /// lives in the alicorn's constructor, and the three dome classes are instantiated by three
        /// different unit classes. So the tier is a property of the controller, exactly like
        /// <see cref="IsPlayer"/> and <see cref="IsNonLiving"/>.</para>
        ///
        /// <para><b>Separate from <see cref="ShieldHp"/> on purpose.</b> The pool decides <i>whether</i>
        /// a dome shows; this decides <i>which</i>. A unit can have one without the other — the
        /// player's <c>sp_mshit</c> shield has a pool and its own dome that is not one of these three,
        /// and an un-imported <c>visShit2</c> leaves a tr3 alicorn with a pool and no exact dome.</para>
        /// </remarks>
        public virtual int ShieldOverlayTier => 0;

        /// <summary>
        /// Whether this unit draws <c>visShit3</c> — the dome <c>UnitBossAlicorn</c> builds
        /// (<c>UnitBossAlicorn.as:123-127</c>), the only place the oracle instantiates it.
        /// </summary>
        /// <remarks>
        /// <b>Nothing returns <c>true</c> today, and that is correct.</b> The boss alicorn's dome is a
        /// one-shot: <c>die():709-712</c> plays it from frame 2 on the first lethal hit and revives the
        /// boss, rather than the pool-driven show/hide every other dome uses. <c>BossAlicornController</c>
        /// therefore answers <c>false</c> — see the note there. This member is kept because the mapping
        /// and the <c>boss</c> clip slot are real; only the play-once driver is missing.
        /// </remarks>
        public virtual bool UsesBossShieldOverlay => false;

        /// <summary>
        /// Whether this unit is one of the classes that can ever have a positive
        /// <see cref="ShieldHp"/> — and therefore the only ones that need a shield <b>dome</b> built.
        /// </summary>
        /// <remarks>
        /// <para><b>AS3's own list, from the places that write <c>shithp</c>:</b> the <c>sp_mshit</c>
        /// spell (<c>Spell.as:309-316</c>, which targets the caster and is how the <i>player</i> gets
        /// one) and three bosses that set it directly — <c>UnitAlicorn.as:1090</c>,
        /// <c>UnitBossDron.as:275</c>, <c>UnitBossUltra.as:441/452</c>. <c>UnitBossAlicorn</c> is
        /// <b>not</b> on the list despite declaring <c>shitMaxHp</c>/<c>t_shit</c>/<c>shitArmor</c>: it
        /// never assigns <c>shithp</c>, so its pool is 0 for its whole life. An ordinary NPC is the same
        /// — building a renderer for one is a component and a poll that can never draw anything.</para>
        ///
        /// <para><b>Only the ordinary alicorn answers true today.</b> <c>BossDron</c> and
        /// <c>BossUltra</c> still carry private shield fields that the damage pipeline cannot see, so a
        /// dome for them would poll a pool that never moves. They are deliberately left out until that
        /// shield is moved onto <see cref="ShieldHp"/> like the alicorn's — recorded, not forgotten.</para>
        /// </remarks>
        public virtual bool HasSpellShieldOverlay => false;

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
        /// AS3 <c>Unit.collisionTip</c> — whether this unit <b>exchanges momentum</b> when its contact
        /// attack lands (<c>Unit.as:196</c>, default <c>1</c>).
        /// </summary>
        /// <remarks>
        /// <para><b>Read on the ATTACKER, in <see cref="TryContactAttack"/>.</b> AS3's test is
        /// <c>if(param1.collisionTip == 1)</c> (<c>Unit.as:4151</c>) inside the <i>target's</i>
        /// <c>udarUnit</c>, where <c>param1</c> is the striker — so this is the striker's flag. A
        /// <c>false</c> here means "this unit's claw does not shove", not "this unit cannot be shoved";
        /// the latter is <see cref="Knocked"/> and they are independent.</para>
        ///
        /// <para><b>Six AS3 subclasses set it to <c>0</c>, all in their constructors, and five of them
        /// are ported.</b> <c>UnitSlime.as:65</c>, <c>UnitSpectre.as:33</c>, <c>UnitNecros.as:29</c>,
        /// <c>UnitBossDron.as:60</c> and <c>UnitThunderHead.as:147</c> each override this to
        /// <c>false</c> on their controller. <c>UnitScythe.as:44</c> is the sixth and has no controller
        /// to override — the scythe is a self-propelled blade entity whose <c>forces()</c> is empty
        /// (<c>UnitScythe.as:56-58</c>), not an archetype — so it is a recorded gap.</para>
        ///
        /// <para><b><c>true</c> is the right fallback</b> and AS3's own field default. Answering
        /// <c>false</c> for a unit with no definition would silently disable the shove for the whole
        /// roster, which is the state the port was in before this landed.</para>
        ///
        /// <para><b>Virtual, like <see cref="IsFixed"/>, <see cref="Knocked"/> and
        /// <see cref="Mass"/>,</b> because AS3 assigns it per <i>class</i> and the port's importer has no
        /// attribute to read — <c>collisionTip</c> appears in no unit row. A controller is the class
        /// the port does have.</para>
        /// </remarks>
        public virtual bool CollisionTip => UnitCollisionImpulseMath.CollisionTipDefault;

        // === IBloodSpraySource — AS3 `Unit.blood` and `scX`/`scY` ==============================
        //
        // A second optional interface, for the same reason `IEffectReceiver` is one: AS3's blood block
        // lives inside `Unit.damage()` (`Unit.as:3844-3899`), so it is a Unit behaviour. A damageable
        // that is not a unit — a crate, a mine — does not implement this, and the damage path then
        // emits no blood for it, which is what the type system is being asked to say.

        /// <summary>
        /// AS3 <c>Unit.blood</c> — <c>0</c> = none, <c>1</c> = red, <c>2</c> = green, <c>3</c> = pink.
        /// </summary>
        /// <remarks>
        /// <b>From the definition, not <see cref="UnitStats"/>,</b> because AS3 never mutates it after
        /// construction. The fallback is <see cref="BloodType.None"/>, AS3's own field default
        /// (<c>Unit.as:416</c>) — not red. A unit whose spawner has not assigned a definition yet must
        /// not spray red blood, and <c>None</c> is also the flag that means "immune to bleed"
        /// (<c>:1417-1420</c>).
        /// </remarks>
        public virtual BloodType BloodType => _stats != null ? _stats.bloodType : BloodType.None;

        /// <summary>
        /// AS3 <c>scX</c>/<c>scY</c> in pixels — the authored <c>&lt;phis sX sY&gt;</c> pair.
        /// </summary>
        /// <remarks>
        /// <see cref="UnitDefinition.Width"/>/<see cref="UnitDefinition.Height"/> hold the same numbers
        /// divided by 100 (the importer's pixel → Unity-unit conversion), so this multiplies back. It is
        /// deliberately pixels and not Unity units: the caller is placing particles in AS3 room-local
        /// pixels, and a silent ×100 in the middle of that is exactly the coordinate mistake lesson #51
        /// records.
        /// </remarks>
        public virtual Vector2 SpriteSizePixels => _stats != null
            ? new Vector2(_stats.Width * 100f, _stats.Height * 100f)
            : Vector2.zero;

        /// <summary>
        /// This unit's origin in Unity world space — AS3's <c>X</c>/<c>Y</c>, which is the <b>bottom</b>
        /// of the body box and its horizontal centre (<c>Unit.as:1875-1878</c>:
        /// <c>Y1 = Y - scY</c>, <c>Y2 = Y</c>, <c>X1 = X - scX / 2</c>, <c>X2 = X + scX / 2</c>).
        /// </summary>
        /// <remarks>
        /// <para>See <see cref="IBloodSpraySource.WorldPosition"/> for why it is the origin rather than
        /// the sprite's centre — the blood block expresses every emission as an offset from
        /// <c>X</c>/<c>Y</c>, so handing in the centre would double-apply the half-height.</para>
        ///
        /// <para><b>A Unity read inside a tick, recorded rather than hidden.</b> The damage drain runs on
        /// <c>SimLoop</c>, and the sim's contract is that a tick reads no <c>Transform</c>. This one
        /// does, for the same reason <c>Projectile.ApplyHoming</c> does: the value is consumed by a
        /// presentation emit and never feeds back into a simulation decision, and units have no
        /// sim-owned position to read instead. The failure mode is a unit whose blood lags its body by
        /// one frame — a visual artefact, not a divergence that can compound.</para>
        /// </remarks>
        public virtual Vector3 WorldPosition => transform.position;

        // === IEffectVisualSink — AS3 `Effect.stepEffect` / `Effect.secEffect` ====================
        //
        // A third optional interface, for the same reason `IBloodSpraySource` is one: AS3's effect
        // visuals are emitted from inside `Effect`, but a flame needs a sprite, a transform and a room,
        // and none of those exist on `UnitStats`. So the stats object forwards its effect ticks here and
        // this class supplies the presentation reads and does the emitting.

        /// <summary>
        /// The room's emitter, handed over by <see cref="SetParticleEmitter"/>. Null means "show
        /// nothing" and is legal — see that method.
        /// </summary>
        private RoomParticleEmitter _particleEmitter;

        /// <summary>
        /// Reused between emits so a burning unit does not allocate a list on every tick. Safe because
        /// the rules clear it on entry and the emit loop is synchronous.
        /// </summary>
        private readonly List<ParticleEmit> _effectVisualEmits = new List<ParticleEmit>();

        /// <summary>
        /// AS3 <c>sost &lt; 4</c> — the unit is still in the world and still drawn.
        /// </summary>
        /// <remarks>
        /// <para><b>This used to be <c>=&gt; IsAlive</c>, and that was the corpse bug in one line.</b>
        /// AS3's <c>sost</c> is <c>1</c> alive, <c>2</c> dying, <c>3</c> corpse, <c>4</c> removed, and a
        /// corpse is inside the <c>&lt; 4</c> test: it is still in <c>loc.units</c>, still drawn, still
        /// solid and still takes damage. Collapsing <c>3</c> and <c>4</c> into "not alive" is what made
        /// a body absorb infinite rounds and stop a penetrator. See <see cref="CorpseState"/>.</para>
        /// </remarks>
        public virtual bool IsInWorld => IsAlive || IsCorpse;

        /// <summary>
        /// Whether this unit is a body rather than a live creature — AS3 <c>sost == 3</c>.
        /// </summary>
        public bool IsCorpse =>
            _corpseState == CorpseState.Corpse || _corpseState == CorpseState.Dissolving;

        /// <summary>
        /// Whether the body has been taken out of the world — AS3 <c>sost == 4</c>. A resurrecting
        /// zombie must check this: AS3 only counts its timer down while the corpse still exists
        /// (<c>UnitZombie.as:219</c>, <c>sost == 3 &amp;&amp; isRes &amp;&amp; t_res &lt; 20</c>), so a body the player
        /// destroyed can no longer get up.
        /// </summary>
        public bool IsCorpseRemoved => _corpseState == CorpseState.Removed;

        /// <summary>
        /// Where this unit sits in AS3's <c>sost</c> state machine, as far as the port models it.
        /// </summary>
        /// <remarks>
        /// <para><b>Only the states that change behaviour.</b> AS3's <c>2</c> (dying, on a
        /// <c>timerDie</c> countdown) is not modelled — nothing in the port delays a death — so a unit
        /// goes straight from <c>1</c> to <c>3</c>. The three that remain are the three that a damage
        /// source, a bullet's stop decision or a despawn can observe.</para>
        /// </remarks>
        public enum CorpseState
        {
            /// <summary><c>sost 1</c> — alive. No body has been left.</summary>
            None = 0,

            /// <summary>
            /// <c>sost 3</c> — a body. Solid, still a legal target, still accumulating overkill until
            /// <see cref="UnitStats.CorpseDestroyed"/>.
            /// </summary>
            Corpse = 1,

            /// <summary>
            /// <c>sost 3</c> with a live <c>Desintegr</c> — the 40-frame dissolve a bloody body goes
            /// through on its way out (<c>Unit.as:4347-4353</c>). Still solid and still targetable, as
            /// in the oracle: <c>initBurn</c> changes nothing about <c>X1..X2</c> or <c>sost</c>.
            /// </summary>
            Dissolving = 2,

            /// <summary><c>sost 4</c> — <c>exterminate()</c> ran. Out of the world for good.</summary>
            Removed = 3,
        }

        private CorpseState _corpseState = CorpseState.None;

        /// <summary>Canonical frames of dissolve elapsed, accumulated by <see cref="TickCorpse"/>.</summary>
        private float _corpseDissolveFrames;

        /// <summary>
        /// The renderer tint captured when the dissolve started, so the fade multiplies the sprite's
        /// own colour instead of forcing it to white.
        /// </summary>
        private Color _corpseBaseColor = Color.white;

        /// <summary>
        /// Whether a hit may still be resolved against this unit — AS3's bullet and blast filters,
        /// which test <c>sost == 4</c> and nothing else (<c>Bullet.as:513</c>, <c>:756</c>,
        /// <c>:809</c>).
        /// </summary>
        /// <remarks>
        /// <para><b>The whole corpse fix hangs off this member being different from
        /// <see cref="IsAlive"/>.</b> A live unit and a body both answer <c>true</c>; only the removed
        /// state answers <c>false</c>. Before it existed, every damage source in the port gated on
        /// <c>IsAlive</c> and therefore skipped a corpse entirely — no damage, no penetration spend,
        /// no pass-through, and (because the projectile's stop path is unconditional) a round that
        /// always died on the body. See <c>docs/AUDIT_corpse_damage_and_piercing_2026-10-18.md</c>.</para>
        ///
        /// <para>A dissolving body still answers <c>true</c>, which is deliberate: AS3 keeps it solid
        /// for the 40 frames of the burn.</para>
        /// </remarks>
        public virtual bool CanBeHit => IsAlive || IsCorpse;

        /// <summary>
        /// AS3 <c>Unit.isPlav</c> — the unit's <b>upper body</b> is submerged (<c>Unit.as:2627-2655</c>,
        /// the 75%-height sample). Read only by the <c>namok</c> droplet, which is suppressed when it is
        /// true.
        /// </summary>
        protected bool _isSubmergedOverride;
        public void SetSubmergedOverride(bool submerged) => _isSubmergedOverride = submerged;
        public virtual bool IsFullySubmerged => _cachedTilePhysics != null ? _cachedTilePhysics.IsFullySubmerged : _isSubmergedOverride;
        public virtual bool IsInWater => _cachedTilePhysics != null ? _cachedTilePhysics.IsInWater : _isSubmergedOverride;

        /// <summary>
        /// AS3 <c>Unit.allVulnerMult</c> (<c>Unit.as:146</c>, default <c>1</c>) — the last multiplier
        /// applied to every hit (<c>Unit.damage():3681</c>), and the target-side read
        /// <see cref="PFE.Systems.Combat.IDamageable.AllVulnerabilityMultiplier"/> exposes.
        ///
        /// <para><b>Why it is a plain settable field here rather than a projection.</b> The oracle has a
        /// single field on the unit and three writers: the skill/effect pipeline (the player's
        /// <c>CharacterStats.allVulnerMult</c>), <c>Pers.defaultParams</c> (1), and the boss shield
        /// (<c>UnitAlicorn.as:586-593</c>, <c>0.6</c>/<c>0.4</c> while <c>shithp &gt; 0</c>). The player
        /// path already owns <c>CharacterStats</c>; the shield path needs to write this from a
        /// controller. One settable value lets both, and keeps the arithmetic in
        /// <c>DamageCalculator</c> where the rest of the formula lives.</para>
        ///
        /// <para><b>Recorded divergence: the player's skill/effect contributions are still not
        /// wired.</b> <c>CharacterStats.allVulnerMult</c> exists and is maintained, but nothing copies it
        /// onto this field, so a player's <c>defense</c>/<c>survival</c> ranks do not yet blunt incoming
        /// damage. What this field does carry today is the alicorn shield. See
        /// <c>BloodSprayRules.cs:46</c> for the original note.</para>
        /// </summary>
        public float AllVulnerabilityMultiplier { get; set; } = 1f;

        /// <summary>
        /// AS3 <c>storona</c> — the unit's facing as a sign, <c>+1</c> or <c>-1</c>
        /// (<c>Unit.as:596-611</c>). Read only by the <c>drunk</c> drip, to hang the droplet off the
        /// side the unit faces.
        /// </summary>
        public virtual float Facing => _facingDirection;

        /// <summary>
        /// The <c>fetter</c> ring's own AS3 room-local position, for an owner that has one.
        /// </summary>
        /// <returns>
        /// <b>False on this base class</b>, because <c>fetX</c>/<c>fetY</c> are declared on
        /// <c>UnitPlayer</c> (<c>UnitPlayer.as:117-119</c>) and this controller is not the player. The
        /// player subclass overrides it. <see cref="EffectVisualRules"/> gates the whole <c>fetter</c>
        /// arm on <c>IsPlayer</c> for the same reason, so this returning false is consistent rather than
        /// the thing that stops the emit.
        /// </returns>
        public virtual bool TryGetFetterAnchor(out Vector2 as3Local)
        {
            as3Local = Vector2.zero;
            return false;
        }

        void IEffectVisualSink.OnEffectStepVisual(ActiveEffect effect)
            => EmitEffectVisuals(effect, payload: false);

        void IEffectVisualSink.OnEffectPayloadVisual(ActiveEffect effect)
            => EmitEffectVisuals(effect, payload: true);

        /// <summary>
        /// Anchor an effect's visuals on this unit and emit them — the thin half of AS3
        /// <c>Effect.stepEffect()</c>/<c>secEffect()</c>, exactly as <c>DamageSystem.EmitBloodSpray</c>
        /// is the thin half of the blood block.
        /// </summary>
        /// <remarks>
        /// <para><b>All the decisions live in <see cref="EffectVisualRules"/></b> — the ids, the gates,
        /// the offsets and the scatter. This method only reads the owner's presentation state, converts
        /// the anchor, and forwards. That split is what lets the rules be pinned by an offline fixture;
        /// a rule written here would be reachable only through a live scene.</para>
        ///
        /// <para><b>No RNG is passed, deliberately.</b> The only arm that draws is <c>blindness</c>,
        /// which is player-only and therefore unreachable while the player stack is unported. Passing
        /// null makes the rules fall back to the interval's lower bound rather than inventing a stream —
        /// and the moment a player override supplies one, this is the single line to change.</para>
        /// </remarks>
        private void EmitEffectVisuals(ActiveEffect effect, bool payload)
        {
            if (_particleEmitter == null || effect == null)
            {
                return;
            }

            // No room pushed means no origin and no height, so there is no correct position. The
            // adapter counts the refusal; see RoomParticleEmitter.RefusedWithoutRoom.
            if (!_particleEmitter.TryToAs3Local(WorldPosition, out Vector2 anchor))
            {
                return;
            }

            TryGetFetterAnchor(out Vector2 fetter);

            var ctx = new EffectVisualContext(
                effectId:     effect.Id,
                level:        effect.Level,
                isPlayer:     IsPlayer,
                isInWorld:    IsInWorld,
                isFloating:   IsFullySubmerged,
                x:            anchor.x,
                y:            anchor.y,
                spriteHeight: SpriteSizePixels.y,
                facing:       Facing,
                fetterX:      fetter.x,
                fetterY:      fetter.y);

            bool any = payload
                ? EffectVisualRules.PlanPayloadVisual(ctx, null, _effectVisualEmits)
                : EffectVisualRules.PlanStepVisual(ctx, _effectVisualEmits);

            if (!any)
            {
                return;
            }

            // EmitAt, not Emit: the rules already produced AS3 room-local pixels, so going back out to
            // Unity space and in again is how the Y mirror gets applied a second time.
            foreach (ParticleEmit emit in _effectVisualEmits)
            {
                _particleEmitter.EmitAt(
                    emit.Id,
                    new Vector2(anchor.x + emit.OffsetX, anchor.y + emit.OffsetY),
                    emit.Spec);
            }
        }

        /// <summary>
        /// AS3 <c>Unit.invulner</c> — the definition's authored flag, or the runtime one a script set.
        /// </summary>
        /// <remarks>
        /// <para><b>The runtime half is <see cref="RuntimeInvulnerable"/>, and it is the cutscene's whole
        /// protection.</b> AS3's <c>dialog</c> action calls <c>World.w.gg.controlOff()</c>
        /// (<c>Script.as:260-271</c>), and <c>UnitPlayer.controlOff()</c> (<c>:2037-2049</c>) sets
        /// <c>invulner = true</c>; <c>controlOn()</c> (<c>:2053-2065</c>) clears it. That flag is read as
        /// part of the admission test in <c>Unit.findCel</c>'s player arm — <c>Unit.as:4658</c> is
        /// <c>if(!(this.isMeet(loc.gg) &amp;&amp; !loc.gg.invulner &amp;&amp; this.fraction != F_PLAYER))</c>
        /// — so an enemy does not merely fail to damage the player during a dialogue, it never selects
        /// him as a target. The port had the authored flag and nothing that changed it, which is the gap
        /// this closes.</para>
        ///
        /// <para><b>Why the definition half is kept rather than replaced.</b> AS3 sets <c>invulner</c>
        /// from the unit node too (<c>Unit.as:1341</c>, <c>node.@invulner &gt; 0</c>) and
        /// <c>UnitBossNecr</c> raises it for the duration of its shadow phase. The two are the same
        /// field in the oracle; here the authored one is read from the definition and the runtime one is
        /// a settable bit, and a unit is invulnerable when either says so.</para>
        /// </remarks>
        public virtual bool IsInvulnerable =>
            RuntimeInvulnerable || (_stats != null && _stats.isInvulnerable);

        /// <summary>
        /// AS3 <c>Unit.invulner</c>'s runtime half — raised by <c>controlOff</c> and cleared by
        /// <c>controlOn</c> (<c>UnitPlayer.as:2037-2065</c>).
        /// </summary>
        /// <remarks>
        /// <b>Not serialised, and not read from data.</b> Nothing in the oracle's unit rows writes it
        /// (the importer pins <c>isInvulnerable = false</c>), so a serialised field here would be a
        /// switch no data could ever set. The only writer is the script lock —
        /// <c>PlayerScriptControlLock</c>, which is the port's <c>gg.controlOff()/controlOn()</c>.
        /// </remarks>
        public bool RuntimeInvulnerable { get; set; }

        /// <summary>
        /// Whether this unit is one of AS3's <c>doop</c> units — AS3 <c>Unit.doop</c>
        /// (<c>Unit.as:436</c>). Read by the hit resolver to suppress the stealth crit
        /// (<c>Unit.damage():3659</c>, <c>!this.doop</c>).
        /// </summary>
        /// <remarks>
        /// <para><b>Exposed through <see cref="UnitStats.isNonLiving"/>, and that is the port's one
        /// honest seam for a value it cannot yet import.</b> AS3 sets <c>doop</c> per <i>class</i> in
        /// each unit subclass's constructor, selected from the unit node's <c>@cl</c> attribute
        /// (<c>Unit.as:710-800</c>) — a class name the port's <c>UnitDataImporter</c> does not read.
        /// So <see cref="UnitStats.isNonLiving"/> is the projection that carries the value up to the
        /// resolver once a definition can supply it, and <see cref="UnitStats"/> answers
        /// <c>false</c> (living) for every unit today.</para>
        ///
        /// <para><b>Falling back to <c>false</c> is the safe direction.</b> Supplying the
        /// definition's own value first would be wrong only if a definition could carry a
        /// <c>doop</c> flag that the resolver should ignore — and it cannot, because the flag is a
        /// property of the unit's identity, exactly like <see cref="Faction"/> and
        /// <see cref="Knocked"/>.</para>
        /// </remarks>
        public virtual bool IsNonLiving => _unitStats != null && _unitStats.isNonLiving;

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
        /// <para><b>The impulse is in Unity units per second</b>, which is what
        /// <c>DamageSystem.ApplyKnockback</c> converts AS3's px/frame into. It must be routed through
        /// <see cref="AddVelocityPixelsPerFrame"/> and <b>not</b> written to <c>_velocity</c>
        /// directly.</para>
        ///
        /// <para><b>Why that is a fix and not a refactor.</b> This method used to be
        /// <c>AddForce(impulse)</c>, i.e. <c>_velocity += impulse</c>. <c>_velocity</c> is the
        /// <i>motor-less</i> storage — it is read by <see cref="StepUnit"/> and by nothing else. A unit
        /// with a <c>TilePhysicsController</c> keeps its velocity in that component's own
        /// <c>dx</c>/<c>dy</c> and never looks at <c>_velocity</c> at all (see
        /// <see cref="As3VelocityPixelsPerFrame"/>), so for every motor-driven unit the impulse was
        /// written and then silently dropped. The <b>player</b> is always motor-driven —
        /// <c>PlayerLocomotionController</c> declares <c>[RequireComponent(typeof(TilePhysicsController))]</c>
        /// and <c>PlayerRigBuilder</c> adds one — so no shot, blast or throw had ever moved the player,
        /// while every NPC (motor-less by default, <c>PfeDebugSettings.UnitMotor</c> off) was thrown
        /// correctly. That asymmetry is exactly what makes this class of bug survive a play-test: the
        /// mechanic visibly works on the thing you are shooting at.</para>
        ///
        /// <para><b>Both paths are unchanged in value.</b> For a motor-less unit
        /// <see cref="AddVelocityPixelsPerFrame"/> multiplies by
        /// <c>TileQueryConstants.PerFrameVelocityToUnitsPerSecond</c> and this method divides by it, so
        /// the net write is <c>_velocity += impulse</c> — the same quantity to within one rounding step
        /// (a divide and a multiply by the same constant, not an exact identity in binary floating
        /// point). Only the motor-driven path changes, and it changes from "discarded" to "applied in
        /// px/frame", which is the unit the motor's own <c>AddForce</c> expects.</para>
        /// </remarks>
        public virtual void ApplyKnockback(Vector2 impulse)
        {
            AddVelocityPixelsPerFrame(impulse / TileQueryConstants.PerFrameVelocityToUnitsPerSecond);
        }

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
        /// Grant this unit its own full window — AS3's <c>this.neujaz = this.neujazMax</c> at
        /// <c>Unit.as:4135</c> (the contact-attack grant) and <c>:4222</c> (the prop-impact one).
        ///
        /// <para><b>Why an overload rather than making <see cref="ContactInvulnerabilityMaxTicks"/>
        /// public.</b> That field is <c>virtual</c> precisely so a subclass can substitute its own value
        /// — <c>PlayerController</c> uses the difficulty-scaled <c>Pers.neujazMax</c> — and the grant
        /// site in the oracle is written on the <i>target</i>, from inside the attacker's call
        /// (<c>param1.udarUnit(this, …)</c>). So an attacker needs "give the target its own window"
        /// without knowing what that window is. Exposing the number would invite a caller to pass the
        /// wrong one.</para>
        /// </summary>
        public virtual void GrantContactInvulnerability()
        {
            _contactInvulnerabilityTicks = ContactInvulnerabilityMaxTicks;
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
        /// Public read-only view of <see cref="As3VelocityPixelsPerFrame"/>.
        ///
        /// <para><b>Added for the enemy brains, which are not subclasses.</b> An AS3 <c>animate()</c>
        /// branches on <c>dx</c> in <i>px/frame</i> (<c>UnitZombie.as:276-297</c>: <c>dx &lt; 1</c> is
        /// "stay", <c>|dx| &gt; 6</c> is "trot"), and <c>EnemyBrain</c> is a separate MonoBehaviour, so
        /// it cannot reach the protected member. Reading <c>Velocity</c> instead would be the silent
        /// 0.3× error the property above documents — a walking zombie would read 0.3 px/frame and a
        /// running one 3 px/frame, so every threshold in the oracle would be crossed at the wrong
        /// time.</para>
        /// </summary>
        public Vector2 VelocityPixelsPerFrame => As3VelocityPixelsPerFrame;

        /// <summary>
        /// Replace this unit's velocity, in AS3's own units — <b>pixels per 30 Hz frame</b> — through
        /// the same two-storage seam <see cref="VelocityPixelsPerFrame"/> reads.
        /// </summary>
        /// <remarks>
        /// <para><b>Replace, not add.</b> AS3 has both: <c>otbros</c> does <c>dx += …</c> (that is
        /// <see cref="AddVelocityPixelsPerFrame"/>) while <c>udarUnit</c>'s exchange <i>assigns</i>
        /// <c>dx = …</c>. Using the additive one for the exchange would let two bodies gain energy on
        /// every contact, so the two verbs have to stay separate.</para>
        ///
        /// <para><b>Why a method and not a settable property.</b> <see cref="As3VelocityPixelsPerFrame"/>
        /// is protected; a public setter on this class would also make
        /// <see cref="VelocityPixelsPerFrame"/> writable and every caller that reads it a candidate for
        /// accidentally writing it. A verb is greppable — <c>SetVelocityPixelsPerFrame</c> has exactly
        /// two callers, both in the contact exchange.</para>
        /// </remarks>
        public void SetVelocityPixelsPerFrame(Vector2 velocityPixelsPerFrame)
        {
            As3VelocityPixelsPerFrame = velocityPixelsPerFrame;
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
        /// Whether this unit passes over spike traps instead of into them — AS3 <c>Unit.isFly</c>, which
        /// both arms of <c>Trap.attKorp</c> test.
        ///
        /// <para><b>Two consumers, and they must not be allowed to disagree.</b> The trap rule above asks
        /// whether this unit passes over a spike field; <see cref="ApplyGravity"/> asks whether it falls
        /// at all. In the oracle those are one flag (<c>Unit.isFly</c>) read from two places
        /// (<c>Trap.attKorp</c> and <c>forces()</c>), so a subclass that overrode only one of them would
        /// produce a unit that hovers over the spikes and then lands in them. Both read this property;
        /// neither re-derives it.</para>
        ///
        /// <para>Defaults to <c>false</c>, which is the oracle's own field default
        /// (<c>Unit.as:284</c> <c>isFly:Boolean = false</c>).</para>
        /// </summary>
        protected virtual bool IsFlying => false;

        /// <summary>
        /// The physics-facing read of <see cref="IsFlying"/>, for the one caller that is not a subclass.
        ///
        /// <para><b>Why the motor cannot use <see cref="IsFlying"/> directly.</b>
        /// <c>TilePhysicsController</c> owns a motor-driven unit's whole step — it is a separate
        /// component, not a base class — so it cannot see a <c>protected</c> member. It has to ask the
        /// same question the motor-less path asks itself, and it has to get the same answer, or
        /// <c>PfeDebugSettings.UnitMotor</c> becomes a switch that changes whether a flyer falls.</para>
        /// </summary>
        public bool IsFlyingUnit => IsFlying;

        /// <summary>
        /// Run this unit's side of AS3 <c>Trap.step</c>/<c>Trap.attKorp</c> (<c>Trap.as:147-197</c>):
        /// find a spike trap this unit is falling or rising into, damage it, and start the contact
        /// immunity window.
        ///
        /// <para><b>Call once per unit step, after <see cref="SweepPropImpacts"/></b> — both read the
        /// velocity the move has just settled, and the oracle reads <c>dy</c> at that same point.</para>
        ///
        /// <para><b>Safe for a unit with no room, no layer or no traps.</b> All three are ordinary
        /// states and all three return immediately, so no caller needs to pre-check.</para>
        /// </summary>
        public virtual void SweepTrapImpacts()
        {
            if (_objectPhysicsLayer == null || _collider == null)
            {
                return;
            }

            // AS3 `Trap.attKorp:182` — `Boolean(param1.neujaz)` refuses the hit, and every firing arm
            // sets `neujaz = neujazMax` (`:189`/`:194`). Checking before the query means a unit already
            // inside a window never pays for the scan, and — because a prop impact can grant the same
            // window earlier in this step — the trap can never double-hit a crate's victim.
            if (IsContactInvulnerable)
            {
                return;
            }

            float difficulty = _tileQuery?.Room?.difficulty?.enemyLevel ?? 0f;

            if (!_objectPhysicsLayer.TryFindImpactingTrapFor(
                    RoomLocalFeetBoundsPixels(),
                    TrapTriggerMath.ToAs3Dy(VelocityPixelsPerFrame.y),
                    0f, // AS3 `osndy`: the port carries no support displacement for tile ground. See
                        // TrapTriggerMath.TryResolveDamage — the rising arm degenerates to `dy < 0`.
                    Mass,
                    difficulty,
                    IsFlying,
                    out RoomObjectPhysicsLayer.TrapEntry trap,
                    out float damage))
            {
                return;
            }

            // AS3 `Trap.as:189`/`:194` — `param1.neujaz = param1.neujazMax`. Granted before the damage
            // is reported, matching the prop sweep, so a re-entrant sweep in the same step cannot
            // double-hit.
            GrantContactInvulnerability(ContactInvulnerabilityMaxTicks);

            ReportTrapImpact(damage, trap);
        }

        /// <summary>
        /// Hand a spike trap's damage to the port's damage authority.
        /// </summary>
        /// <remarks>
        /// <para><b>Why <see cref="PendingDamage.Contact"/>, same as the prop impact.</b> AS3
        /// <c>Trap.attKorp</c> calls <c>param1.damage(amount, tipDamage)</c> directly — no avoidance
        /// roll, no ±30% spread — so <c>Direct</c> would be wrong in exactly the way the prop sweep's
        /// remarks describe.</para>
        ///
        /// <para><b>AS3's <c>@tipdam</c> is not mapped.</b> The oracle passes it as the damage
        /// <i>type</i>; no shipped <c>spikes</c>/<c>fspikes</c> row authors one (both rows carry only
        /// <c>@damage</c>), so every trap hit is <see cref="DamageType.PhysicalMelee"/>. If a row ever
        /// gains a <c>@tipdam</c>, this is the line that has to learn it.</para>
        /// </remarks>
        protected virtual void ReportTrapImpact(float damage, RoomObjectPhysicsLayer.TrapEntry trap)
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

            if (!_warnedMissingDamageSystem)
            {
                _warnedMissingDamageSystem = true;
                Debug.LogWarning(
                    $"[{GetType().Name}] a spike trap ('{trap?.Object?.objectId}') has no DamageSystem, " +
                    "so it is being applied as raw HP damage — no armour, no vulnerabilities. The " +
                    "spawner must call SetDamageSystem, or the unit's GameObject must be in the scene " +
                    "scope's autoInjectGameObjects.", this);
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

            // AS3 re-enters `die()` on every hit that lands on a corpse (`Unit.as:3713-3745`), and
            // `die()`'s tail is written to survive that: loot is guarded by `lootIsDrop` and the death
            // rattle by `sost == 1`. The port had only the loot half, so this latch is the missing
            // one — and it matters the moment corpses take damage, because `OnDeath` awards XP with no
            // guard at all: every hit on a body would have paid the player again.
            //
            // The latch is released when a subclass leaves the unit ALIVE after the reaction. The
            // training dummy does exactly that (`OnDeath` restores full HP — AS3 `UnitTrain.as:65-68`),
            // and a permanent latch would kill it on its second zero.
            if (_deathRaised && !IsAlive) return false;

            _deathRaised = true;
            OnDeath();

            if (IsAlive) _deathRaised = false;

            return true;
        }

        /// <summary>
        /// Whether <see cref="OnDeath"/> has been acted on for the current death. See
        /// <see cref="RaiseDeath"/>.
        /// </summary>
        protected bool DeathRaised => _deathRaised;

        /// <summary>
        /// Called when this unit dies.
        /// Base implementation logs death. Subclasses can override for death effects.
        /// </summary>
        protected virtual void OnDeath()
        {
            Debug.Log($"[{GetType().Name}] has died!");

            // AS3 Unit.as:4392-4394 — runScript(), dropLoot(), incStat(). The drop comes BEFORE the XP
            // award (:4397), and that order is observable: the gem chance reads `this.xp`, which the
            // award path does not clear until after it has run.
            DropLootOnDeath();

            // Reward XP to player (AS3 Unit.as:4397 loc.takeXP(this.xp, X, Y, true))
            if (_stats != null && _stats.xpReward > 0)
            {
                var player = FindFirstObjectByType<PFE.Entities.Player.PlayerController>();
                if (player != null && player.CharacterStats != null)
                {
                    player.CharacterStats.AddXp(_stats.xpReward, transform.position.x, transform.position.y);
                }
            }

            // AS3 `Unit.die()`'s branch table (`:4302-4368`) — what the body becomes. Last, because
            // the oracle runs `runScript`/`dropLoot`/`incStat` in `die()`'s tail and the branch table
            // at its head, and the drop must not be conditional on which arm was taken.
            EnterCorpse();

            // Base class doesn't destroy the GameObject.
            // Subclasses can override to play death animations, drop loot, etc.
        }

        // === Corpse lifecycle — AS3's `sost == 3` ==================================================
        //
        // The oracle's death is not an event, it is a state. `die()` runs at the moment HP reaches
        // zero and again on every hit after that, and what keeps a body around is `hp > -maxhp * 2`.
        // The port models that as `CorpseState` plus `UnitStats.Overkill`; the two together are
        // `sost == 3` and its remaining body. See docs/AUDIT_corpse_damage_and_piercing_2026-10-18.md.

        /// <summary>
        /// AS3 <c>Unit.die()</c>'s branch table (<c>Unit.as:4302-4368</c>) — what this unit's body
        /// becomes now that it is dead.
        /// </summary>
        /// <remarks>
        /// <para><b>Which arms the port can answer, and which it cannot.</b> AS3 tries, in order:
        /// <c>noDestr</c> (a body that can never be destroyed), <c>param1 &gt; 0</c> (the damage type
        /// asked for a burn), <c>trup &amp;&amp; hp &gt; -maxhp*2</c> (an ordinary body),
        /// <c>trup &amp;&amp; blood &gt; 0</c> (enough overkill — burn it away), then
        /// <c>expl(); exterminate()</c>. The port answers three of them: a body, a body already past
        /// its threshold, or nothing at all. <c>noDestr</c> is not imported anywhere and the
        /// damage-type death code is §6 step 5 of the audit — both are recorded gaps, not silent
        /// omissions.</para>
        /// </remarks>
        private void EnterCorpse()
        {
            // AS3 `UnitPlayer.die()` is the game-over path, not a body: the player leaves no corpse
            // and never becomes a target. Gating here rather than inside `OnDeath` keeps the subclass
            // override (which calls `base`) honest.
            if (IsPlayer || _corpseState != CorpseState.None)
            {
                return;
            }

            // `trup='0'` — AS3's "destroyed outright": no body, no death row. `expl(); exterminate()`
            // (`Unit.as:4366-4367`), which in the port is "stop being a target and stop being drawn".
            // Three units author it (`rat`, `tarakan`, `ant`) and they author no `die` row either.
            if (_stats != null && !_stats.leavesCorpse)
            {
                ExterminateCorpse();
                return;
            }

            // The death blow's own overkill can already be past the threshold — a plasma round that
            // lands for three times the body's health destroys it on the frame it dies, which AS3
            // reaches by falling through the `hp > -maxhp*2` arm on the same `die()` call.
            if (_unitStats != null && _unitStats.CorpseDestroyed)
            {
                BeginCorpseDestroy();
                return;
            }

            _corpseState = CorpseState.Corpse;
        }

        /// <summary>
        /// One hit on a body. AS3's <c>Unit.damage()</c> has no <c>sost</c> guard, so this is the same
        /// <c>hp -= param1</c> as a hit on a living unit — it just has nothing left to spend it on but
        /// the overkill budget, and that budget is what eventually destroys the body.
        /// </summary>
        private void ApplyCorpseDamage(float damage)
        {
            // `CorpseState.None` means "not alive, but not a body" — the player's game-over path, or a
            // death this peer is not authoritative for. Neither has anything to damage: the player's
            // controller must not lose its collider to a stray round after death, and a suppressed
            // death is not simulated here at all.
            if (damage <= 0f || _unitStats == null) return;
            if (_corpseState != CorpseState.Corpse && _corpseState != CorpseState.Dissolving) return;

            _unitStats.Damage(damage);

            if (_unitStats.CorpseDestroyed)
            {
                BeginCorpseDestroy();
            }
        }

        /// <summary>
        /// AS3's last two <c>die()</c> arms: a bloody body burns away over 40 frames
        /// (<c>initBurn(4 + blood)</c>, <c>Unit.as:4347-4353</c>), a bloodless one is gone at once
        /// (<c>expl(); exterminate()</c>, <c>:4360-4367</c>).
        /// </summary>
        private void BeginCorpseDestroy()
        {
            if (_corpseState == CorpseState.Removed || _corpseState == CorpseState.Dissolving)
            {
                return;
            }

            if (BloodType == BloodType.None)
            {
                ExterminateCorpse();
                return;
            }

            _corpseState = CorpseState.Dissolving;
            _corpseDissolveFrames = 0f;
            if (_visualRenderer != null)
            {
                _corpseBaseColor = _visualRenderer.color;
            }
        }

        /// <summary>
        /// Advance a dissolving body by one step — AS3's <c>Unit.step</c> arm that calls
        /// <c>burn.step()</c> and then <c>exterminate()</c> when <c>burn.vse</c> goes true
        /// (<c>Unit.as:1839-1843</c>).
        /// </summary>
        /// <remarks>
        /// <para><b>Called from both drivers, next to <see cref="TickEffects"/>.</b> A motor-driven unit
        /// never reaches <see cref="StepUnit"/> — <c>TilePhysicsController.StepMotor</c> owns its step —
        /// so a dissolve ticked only there would freeze for every spawned enemy in the shipped
        /// configuration. That is the same trap <c>TickNoise</c>/<c>TickStealth</c>/<c>TickControl</c>
        /// document at their call sites.</para>
        ///
        /// <para>The countdown is in <b>canonical 30 Hz frames</b>, accumulated through
        /// <see cref="PFE.Core.SimClock.StepScale"/>, so a 60 Hz build dissolves a body in the same
        /// wall-clock 1.33 s the oracle does rather than in half of it.</para>
        /// </remarks>
        public void TickCorpse()
        {
            if (_corpseState != CorpseState.Dissolving)
            {
                return;
            }

            float scale = _simClock != null ? _simClock.StepScale : 1f;
            float before = _corpseDissolveFrames;
            _corpseDissolveFrames += scale;

            // Emit on a fixed cadence rather than every frame: a room can hold a dozen bodies and
            // AS3's per-frame emit is gated on mass for exactly that reason (`Desintegr.as:126-131`).
            int interval = CorpseDamageMath.DissolveParticleIntervalFrames;
            if ((int)(_corpseDissolveFrames / interval) != (int)(before / interval))
            {
                EmitCorpseDissolveParticles();
            }

            if (_visualRenderer != null)
            {
                float t = Mathf.Clamp01(_corpseDissolveFrames / CorpseDamageMath.DissolveFrames);
                Color color = _corpseBaseColor;
                color.a *= 1f - t;
                _visualRenderer.color = color;
            }

            if (_corpseDissolveFrames >= CorpseDamageMath.DissolveFrames)
            {
                ExterminateCorpse();
            }
        }

        /// <summary>
        /// The dissolve's particles — AS3 <c>Desintegr.step</c>'s <c>Emitter.emit(burnPart, …)</c>,
        /// where <c>burnPart</c> is the blood colour for the <c>4 + blood</c> tips
        /// (<c>serv/Desintegr.as:80-98</c>).
        /// </summary>
        private void EmitCorpseDissolveParticles()
        {
            if (_particleEmitter == null)
            {
                return;
            }

            string id = BloodSprayRules.SprayIdFor(BloodType);
            if (string.IsNullOrEmpty(id))
            {
                return;
            }

            if (!_particleEmitter.TryToAs3Local(WorldPosition, out Vector2 anchor))
            {
                return;
            }

            Vector2 size = SpriteSizePixels;
            var spec = new ParticleSpec
            {
                Kol = 2,
                RX  = size.x * 0.35f,
                RY  = size.y * 0.25f,
            };

            // `Y - scY / 2`, the body's centre — the same anchor AS3's `Desintegr` emits from.
            _particleEmitter.EmitAt(id, new Vector2(anchor.x, anchor.y - size.y * 0.5f), spec);
        }

        /// <summary>
        /// AS3 <c>exterminate()</c> (<c>Unit.as:4411-4421</c>) — <c>loc.remObj(this)</c>,
        /// <c>sost = 4</c>, <c>disabled = true</c>.
        /// </summary>
        /// <remarks>
        /// The port has no room-local unit list, so "out of the world" is expressed as the three
        /// things that list membership bought: it is no longer a physics body (no collider, no
        /// simulated rigidbody), it is no longer drawn, and <see cref="CanBeHit"/> is false. The
        /// GameObject stays alive because the brain, the health overlay and the loot code all still
        /// hold references to it — destroying it here would turn every one of those into a null check
        /// the oracle does not have.
        /// </remarks>
        private void ExterminateCorpse()
        {
            if (_corpseState == CorpseState.Removed)
            {
                return;
            }

            _corpseState = CorpseState.Removed;
            _corpseDissolveFrames = 0f;

            if (_collider != null) _collider.enabled = false;
            if (_rb != null) _rb.simulated = false;

            SetVisible(false);
        }

        /// <summary>
        /// Put a body back on its feet — AS3 sets <c>sost = 1</c> directly when a boss takes its second
        /// life (<c>UnitBossNecr.as:571-584</c>) or a zombie resurrects (<c>UnitZombie.as:495-524</c>).
        /// </summary>
        /// <remarks>
        /// <para><b>Required, not optional.</b> <see cref="ExterminateCorpse"/> turns off the collider
        /// and the rigidbody, so a revive that only wrote HP would leave an invisible, intangible unit
        /// that every damage source would skip — the same class of bug as the corpse itself, one state
        /// over. <see cref="UnitStats.Overkill"/> clears itself when the HP goes positive, so the
        /// budget does not need resetting here.</para>
        /// </remarks>
        public void ReviveFromCorpse()
        {
            if (_corpseState == CorpseState.None)
            {
                return;
            }

            _corpseState = CorpseState.None;
            _corpseDissolveFrames = 0f;

            if (_collider != null) _collider.enabled = true;
            if (_rb != null) _rb.simulated = true;

            if (_visualRenderer != null)
            {
                Color color = _corpseBaseColor;
                color.a = 1f;
                _visualRenderer.color = color;
            }

            SetVisible(true);
        }

        /// <summary>
        /// AS3 <c>Unit.dropLoot()</c> (<c>Unit.as:4450-4467</c>) — the death drop. Split out of
        /// <see cref="OnDeath"/> so a subclass that overrides the reaction can still call it, and so the
        /// once-only guard has one home.
        ///
        /// <para><b>Three parts, in oracle order.</b></para>
        /// <list type="number">
        ///   <item><description>the <b>table</b> roll — <c>inter.loot()</c> → <c>LootGen.lootDrop</c>
        ///     (<c>Interact.as:2007-2010</c>), keyed by <see cref="UnitDeathDrop.ResolveLootTableKey"/>.
        ///     This is the substantive half: 47 unit tables.</description></item>
        ///   <item><description><c>essence</c> when <c>hero &gt; 0</c>, the unit is not a robot, and the
        ///     0.75 roll passes.</description></item>
        ///   <item><description><c>gem1..3</c> on the treasure-perk chance
        ///     <c>dropTre * xp / 4000</c>.</description></item>
        /// </list>
        ///
        /// <para><b>What is deliberately not wired.</b> The <b>table</b> roll still runs with no perk
        /// flags (<c>ctx.Pers == null</c>), so <c>freel</c>/<c>barahlo</c> do not add their extra rolls and
        /// money stacks do not scale by <c>capsMult</c> — that is the perk-flags seam (D10), which has no
        /// shipping implementation yet. The gem gate is the exception: <c>dropTre</c> is read straight off
        /// the player's <c>CharacterStats</c>, because it is the one perk flag <c>dropLoot</c> itself
        /// reads. A <c>LootContext</c> whose pools failed to load returns <c>null</c> and the drop is
        /// skipped loudly rather than silently.</para>
        ///
        /// <para><b>The outer gate is reduced, and here is why.</b> AS3's guard is
        /// <c>!lootIsDrop &amp;&amp; (!isRes || sost == 4 || burn)</c> (<c>Unit.as:4381</c>). The second
        /// half exists for <b>resurrecting</b> zombies: a unit with <c>&lt;un res='…'/&gt;</c> is buried
        /// rather than killed, so it must not drop until it is <i>finally</i> gone (<c>sost == 4</c>) or
        /// burnt. <b>The port has no resurrection</b> — <c>UnitZombie.as:495-524</c>'s 300-tick countdown
        /// is not ported, so <c>zombie7/8/9</c> (<c>UnitDefinition.canResurrect</c>, written by the
        /// importer and read by nothing) die once and are removed. This death <i>is</i> the final one, so
        /// the disjunction reduces to <c>true</c> and the gate is <c>!lootDropped</c> alone. Gating on
        /// <c>canResurrect</c> instead would be worse than the reduction: those three units would drop
        /// nothing for the whole game. Owner-verifiable: if resurrection is ever ported, this gate must
        /// grow its second half back.</para>
        /// </summary>
        protected virtual void DropLootOnDeath()
        {
            if (!DropsLootOnDeath) return;
            if (_lootDropped) return;                       // AS3 lootIsDrop (:4381-4383)
            _lootDropped = true;

            if (_stats == null) return;

            // Resolved but NOT gated on. `ResolveLootTableKey` falls back to the unit id, so this is only
            // empty when the unit has no id at all — but even that case must still drop its placement
            // items, because AS3's `if(inter) inter.loot();` (`Unit.as:4452-455`) runs before anything
            // that reads `cont`. The old `if (string.IsNullOrEmpty(key)) return;` therefore had to move
            // BELOW the placed-items block: it was the difference between "this boss drops its key" and
            // "this boss drops nothing", and for a unit whose id never made it into `UnitKeys` it decided
            // which by accident.
            string key = UnitDeathDrop.ResolveLootTableKey(_stats.lootTableKey, _stats.controllerId, _stats.id);

            LootContext ctx = LootRuntime.CreateContext(key);
            if (ctx == null)
            {
                // LootRuntime has already logged why (the AllData resource failed to parse).
                return;
            }

            PFE.Entities.Player.PlayerController player = FindFirstObjectByType<PFE.Entities.Player.PlayerController>();

            ctx.Hero = _heroTier;
            RoomInstance room = _tileQuery?.Room;
            RoomDifficulty difficulty = room?.difficulty;
            ctx.LocDifLevel = difficulty != null ? Mathf.RoundToInt(difficulty.baseDifficulty) : 0;
            ctx.WeaponLevel = difficulty != null ? difficulty.weaponLevel : 1f;
            ctx.AllDif = 50f;      // lootDrop forces lootBroken = false (LootGen.as:789)
            ctx.LootBroken = false;

            // The perks and the environment — the D9 wiring, the same two adapters the container sink
            // installs. Without them a unit drop rolled with no `freel`/`barahlo` extra rolls, money that
            // never scaled by `capsMult × difCapsMult`, no act id and `LacksWeapon` always true. See
            // ContainerPropPresenter.ApplyRoomDifficulty for the per-field oracle citations; the only
            // difference here is that the loot is dropped by a corpse rather than opened by a player, so
            // the perks still come from the player (they are `World.w.pers`, not the unit's).
            //
            // `GameStage`, `LootLimit` and the per-run limit store are already on the context —
            // LootRuntime.CreateContext filled them from the current land's published values.
            ctx.Pers = player != null ? player.CharacterStats : null;
            ctx.Env = new RoomLootEnvironment(
                biom: room != null ? room.biome : 0,
                itemsTip: string.Empty,   // AS3 loc.itemsTip; `@items` is not imported yet (see the adapter)
                actId: LootRuntime.LandActId,
                landRnd: LootRuntime.LandRnd,
                hasProb: room != null && !string.IsNullOrEmpty(room.probId),
                lacksWeapon: weaponId => WeaponLacks(player, weaponId));

            float centreY = transform.position.y + (_stats.Height * 0.5f);   // AS3 `Y - scY/2`

            // AS3 `Unit.dropLoot`'s FIRST statement — `if(inter) { inter.loot(); }`
            // (Unit.as:4452-4455), and inside `Interact.loot` the `<item>` children are looped before the
            // `cont` table is touched (Interact.as:1975-2000 then :2005-2010). So the placement's own items
            // come first, and the essence and gem rolls come last — that ordering is preserved here.
            //
            // This is the boss's `key_sewer`. `bossraider` at
            // `Assets/_PFE/Data/Resources/Rooms/rooms_nio/bossraider.asset:158-178` authors
            // `<item id="key_sewer" imp="1"/>`, and `imp` is what makes it drop-mode 2 rather than 1 —
            // which is what exempts a quest item from the per-run loot limit (LootGen.as:261).
            if (_placementItems != null && _placementItems.Count > 0)
            {
                LootRoller.RollPlacedItems(UnitLootRules.PlacedItems(_placementItems), ctx,
                                          nx: transform.position.x, ny: centreY);
            }

            // AS3's `Interact.loot` second half — `LootGen.lootDrop(…, this.cont, …)`
            // (Interact.as:2005-2010), which only runs for a `cont` the container actually carries.
            // Guarded here rather than above so the placed items drop first and regardless.
            if (!string.IsNullOrEmpty(key))
            {
                LootRoller.RollUnitDrop(key, ctx, transform.position.x, centreY);
            }

            IRngService rng = ctx.Rng;

            // AS3 :4456 — hero > 0 && opt.robot != true && isrnd(0.75).
            // IsMechanical is the port's `opt.robot` (UnitController.IsMechanical).
            if (UnitDeathDrop.ShouldDropEssence(_heroTier, IsMechanical, rng.NextFloat()))
            {
                LootRoller.RollPlacedItem(UnitDeathDrop.EssenceItemId, ctx, nx: transform.position.x, ny: centreY);
            }

            // AS3 :4460-4466 — pers.dropTre > 0 && xp > 0 && rnd < dropTre * xp / 4000.
            float dropTre = player != null && player.CharacterStats != null ? player.CharacterStats.dropTre : 0f;
            if (UnitDeathDrop.ShouldDropGem(dropTre, _stats.xpReward, rng.NextFloat()))
            {
                LootRoller.RollPlacedItem(UnitDeathDrop.RollGemId(rng.NextFloat()), ctx,
                                          nx: transform.position.x, ny: centreY);
            }

            // AS3 `UnitRaider.dropLoot` (:507-523) — the gun the corpse was holding, dropped
            // <b>after</b> `super.dropLoot()` returns, i.e. after the table, the essence and the gem.
            // A no-op for every family that does not override `dropLoot`, which is all of them except
            // the raider family (and the two bosses, whose port controllers hold no weapon at all).
            //
            // Rolled into `ctx` rather than spawned here so the dropped weapon goes through the same
            // item-construction rules as every other drop — the `kol = 0` condition band, the `^N`
            // variant split and the per-run `@limit` check are all the roller's, not a second copy's.
            DropWieldedWeapon(ctx, centreY);

            SpillDrops(ctx, player, centreY);
        }

        /// <summary>
        /// AS3 <c>UnitRaider.dropLoot</c>'s second half (<c>UnitRaider.as:507-523</c>) — drop the weapon
        /// the corpse was holding, in 5–20 % condition, as its own <c>^N</c> variant when it had one.
        ///
        /// <para><b>Why a virtual hook and not an override of <see cref="DropLootOnDeath"/>.</b> The
        /// oracle puts this <i>inside</i> <c>dropLoot</c>, after <c>super.dropLoot()</c>, so it sits
        /// behind the same once-only guard (<c>!lootIsDrop</c>, <c>Unit.as:4381</c>) as the table roll:
        /// a second <c>die()</c> on a body that is already a corpse must not drop a second gun. An
        /// override of <see cref="DropLootOnDeath"/> would have to re-implement that guard, and
        /// <c>_lootDropped</c> is private. The hook therefore runs exactly where the oracle runs it and
        /// inherits the guard for free.</para>
        ///
        /// <para><b>Default: nothing.</b> Only the raider family overrides this. The base <c>Unit</c> has
        /// no <c>isDropArm</c> and its <c>dropLoot</c> never looks at <c>currentWeapon</c>.</para>
        /// </summary>
        protected virtual void DropWieldedWeapon(LootContext ctx, float centreY)
        {
        }

        /// <summary>
        /// AS3 <c>World.w.invent.weapons[id] == null || weapons[id].variant == 0</c> — the gate on the
        /// four <c>specweap</c> unique-weapon candidates (<c>LootGen.as:745-755</c>).
        ///
        /// <para><b>A missing player or inventory answers <c>false</c>, deliberately.</b> The branch
        /// <i>hands out</i> the weapon when this is true, so "cannot answer" must not read as "lacks it" —
        /// that would drop <c>lsword^1</c>, <c>antidrak^1</c>, <c>quick^1</c> and <c>mlau^1</c> into the
        /// world on every corpse. <c>false</c> means "assume the player has it", which withholds a unique
        /// weapon rather than duplicating one. Named as an assumption rather than hidden behind a constant
        /// in the adapter. Same helper shape as <c>ContainerPropPresenter.WeaponLacks</c>.</para>
        /// </summary>
        private static bool WeaponLacks(PFE.Entities.Player.PlayerController player, string weaponId)
        {
            if (player == null || string.IsNullOrEmpty(weaponId)) return false;

            PlayerInventory inventory = player.GetComponent<PlayerInventory>();
            GameInventory gameInventory = inventory != null ? inventory.Inventory : null;
            if (gameInventory == null) return false;

            GameWeaponInstance weapon = gameInventory.GetWeapon(weaponId);
            return weapon == null || weapon.Variant == 0;
        }

        /// <summary>
        /// Put the rolled items into the world around the corpse — the port's stand-in for AS3's
        /// <c>Loot</c> props falling out (<c>LootGen.as:287-294</c>).
        ///
        /// <para><b>Walk-over collection, like the oracle.</b> AS3's <c>LootGen</c> call site omits the
        /// <c>nauto</c> argument, so the constructor default <c>true</c> applies (<c>Loot.as:33,238</c>) —
        /// a kill's loot is magnetic, unlike a player's own drop. <c>autoCollect: true</c> reproduces
        /// that.</para>
        ///
        /// <para>An id that resolves to no definition <b>in the table its tip names</b> is <b>named in a
        /// warning</b>, never dropped silently — a silent skip would read as "the table is thinner than it
        /// is".</para>
        /// </summary>
        private void SpillDrops(LootContext ctx, PFE.Entities.Player.PlayerController player, float centreY)
        {
            PlayerInventory inventory = player != null ? player.GetComponent<PlayerInventory>() : null;
            if (inventory == null)
            {
                if (ctx.Results.Count > 0)
                {
                    Debug.LogWarning($"[{GetType().Name}] '{ctx.TableKey}' rolled {ctx.Results.Count} " +
                                     "item(s) but there is no PlayerInventory to resolve them, so nothing " +
                                     "was spawned.");
                }
                return;
            }

            IRngService rng = ctx.Rng;
            int spawned = 0;

            for (int i = 0; i < ctx.Results.Count; i++)
            {
                LootRoll roll = ctx.Results[i];

                // A weapon roll's id lives in ContentType.Weapon, not ContentType.Item, so the item
                // resolver answers null for it — permanently, for every weapon. A kill's weapon drop used
                // to be lost here with a warning; asking the table the tip names is the fix.
                if (LootTips.IsWeaponTip(roll.Tip))
                {
                    WeaponDefinition weapon = inventory.ResolveWeapon(roll.ItemId);
                    if (weapon == null)
                    {
                        Debug.LogWarning($"[{GetType().Name}] '{ctx.TableKey}' rolled '{roll.ItemId}' (tip '" +
                                         roll.Tip + "') but no WeaponDefinition resolves it — it was not " +
                                         "spawned. The tip names the weapon table, so this id is absent " +
                                         "from ContentType.Weapon rather than from the item table.");
                        continue;
                    }

                    // One instance, never a stack: a weapon carries its own condition, magazine and
                    // respect state, and AS3 forces weapons to kol 1 (Item.as:176-178).
                    //
                    // `Variant` and `Condition` are carried, not dropped. Until this, both were computed
                    // by the roller and thrown away here, so a rolled `lsword^1` spawned as a base
                    // `lsword` and every dropped weapon arrived pristine — while AS3 hands
                    // `Invent.take` an item whose `sost` is 0.05..0.20 for a `kol = 0` roll
                    // (Item.as:185-188) and whose `variant` is the `^N` tier. `HpMultiplier` is AS3's
                    // `multHP` (0.4 for a broken container), which `take` multiplies into the same
                    // product: `hp = round(char[v].@maxhp * sost * multHP)` (Invent.as:1068-1075).
                    Vector3 weaponWhere = DropScatter(rng, centreY);
                    if (WorldItemPickup.Spawn(weapon, 1, weaponWhere, autoCollect: true,
                                              variant: roll.Variant,
                                              condition: roll.Condition * roll.HpMultiplier) != null)
                    {
                        spawned++;
                    }

                    continue;
                }

                ItemDefinition definition = inventory.ResolveItem(roll.ItemId);
                if (definition == null)
                {
                    Debug.LogWarning($"[{GetType().Name}] '{ctx.TableKey}' rolled '{roll.ItemId}' (tip '" +
                                     roll.Tip + "') but no ItemDefinition resolves it — it was not spawned.");
                    continue;
                }

                Vector3 where = DropScatter(rng, centreY);

                if (WorldItemPickup.Spawn(definition, roll.Quantity, where, autoCollect: true) != null)
                {
                    spawned++;
                }
            }

            for (int i = 0; i < ctx.UnmatchedKeys.Count; i++)
            {
                Debug.LogWarning($"[{GetType().Name}] drop key '{ctx.UnmatchedKeys[i]}' matched no loot " +
                                 "table — nothing was rolled. (AS3 is silent here; see Q1.)");
            }

            if (spawned == 0 && ctx.Results.Count == 0)
            {
                Debug.Log($"[{GetType().Name}] '{ctx.TableKey}' yielded nothing (AS3's dropLoot returned false).");
            }
        }

        /// <summary>
        /// A small deterministic scatter around the corpse, reusing the loot stream so a seeded test can
        /// predict it — the port's stand-in for AS3's fall and bounce.
        ///
        /// <para><b>Extracted so the two content types consume the RNG in the same order.</b> Both
        /// <c>NextFloat</c> calls happen here and nowhere else, so a weapon branch and an item branch
        /// scatter identically. Two hand-copied versions of this expression is precisely how a seeded
        /// sequence drifts the first time someone edits one of them.</para>
        /// </summary>
        private Vector3 DropScatter(IRngService rng, float centreY)
        {
            return new Vector3(
                transform.position.x + (rng.NextFloat() * 2f - 1f) * DropScatterRadius,
                centreY + (rng.NextFloat() * 2f - 1f) * DropScatterRadius,
                transform.position.z);
        }

        /// <summary>Half-extent of the eject scatter, in world units — the corpse-drop twin of
        /// <c>ContainerPropPresenter</c>'s. See that constant for why a scatter stands in for the
        /// oracle's fall.</summary>
        private const float DropScatterRadius = 0.3f;
    }
}
