using UnityEngine;
using VContainer;
using PFE.Systems.Audio;
using PFE.Systems.Combat;
using PFE.Systems.Weapons;
using PFE.Data.Definitions;
using PFE.Core;
using PFE.Core.Messages;
using MessagePipe;
using System;
using System.Collections.Generic;
using System.Text;
using PFE.Systems.Map;
using PFE.Systems.Map.Rendering;
using PFE.Systems.Map.TileQuery;
namespace PFE.Entities.Weapons
{
    /// <summary>
    /// Runtime projectile / bullet.
    /// Replaces Bullet.as from ActionScript (1,037 lines).
    ///
    /// Physics model: always Kinematic Rigidbody2D.
    /// Velocity is integrated manually each FixedUpdate matching AS3 frame-by-frame simulation:
    ///   dx += ddx  (acceleration)
    ///   dy += ddy  (gravity / flame lift)
    ///   x  += dx
    ///   y  += dy
    /// rb.linearVelocity is set from the result so ContinuousCollisionDetection still fires triggers.
    ///
    /// Supported behaviors (all from AS3 Weapon.shoot()):
    ///   gravityScale > 0  → downward acceleration (grenades, thrown, heavy rounds)
    ///   accel        > 0  → forward thrust along aim dir (rockets)
    ///   flame == 1        → strong upward arc, short lifetime (flamer)
    ///   flame == 2        → weak upward arc (flame2 type)
    ///   navod        > 0  → homing: steers toward nearest IDamageable each tick
    ///   penetration  > 0  → penetrator: does NOT stop on a unit, spends damage and carries on
    ///                       (AS3 `probiv`, weapon/Bullet.as:533 + Unit.as:3684-3696)
    ///
    /// Handles three hit cases:
    ///   1. IDamageable  — enemies, player, destructible props.
    ///   2. IDestructibleTile — tilemap walls that can be blown apart.
    ///   3. Everything else — solid non-destructible surface; bullet stops.
    ///
    /// AoE (Explosive archetype): on any impact, additionally overlaps a circle and
    /// damages all IDamageable + IDestructibleTile within explRadius.
    ///
    /// <para><b>Stage C — two tile-collision paths, selected by
    /// <c>PfeDebugSettings.ProjectilesUseLowLevelPhysics</c>.</b> With the flag OFF (default) nothing
    /// here changed: tile hits arrive through <see cref="OnTriggerEnter2D"/> from Unity's per-tile
    /// <c>BoxCollider2D</c> grid, and the integration runs in <see cref="FixedUpdate"/> against
    /// <c>Time.fixedDeltaTime</c>. With it ON, the tile-hit <i>decision</i> comes from a swept query
    /// against the LowLevelPhysics2D chain mirror (single-sourced from <c>ITileQueryService</c>, so it
    /// cannot drift from the motor's notion of solid, and a chain has no seam to produce ghost
    /// collisions), and the integration runs on <see cref="SimLoop"/> at the clock's own tick rate —
    /// <c>SimClock.SimDt</c> per tick, which is exactly AS3's 1/30 s at the default canonical rate, so
    /// a 0.7 s flame lifetime is 21 ticks rather than the 35 that <c>FixedUpdate</c>'s 50 Hz produced.
    /// </para>
    ///
    /// <para><b>What the flip deliberately does NOT change.</b> Entity hits. Enemies, the player and
    /// destructible props are still Unity colliders, so <c>IDamageable</c> resolution keeps coming
    /// from <see cref="OnTriggerEnter2D"/> in both modes. Moving entities into the Box2D world is not
    /// Stage C's job, and doing it here would have coupled the flip to every entity type at once.</para>
    /// </summary>
    [RequireComponent(typeof(Rigidbody2D))]
    public class Projectile : MonoBehaviour, ISimTickable
    {
        [Header("Visual")]
        [SerializeField] private SpriteRenderer _visualRenderer;

        /// <summary>
        /// The stretched beam child, for a <c>spring='2'</c> laser — AS3's <c>vis.laser</c>
        /// (<c>Bullet.as:213-215</c>). Left unassigned it is found by name under the visual renderer;
        /// see <see cref="BeamChildName"/>. Null is legal and means "this prefab has no beam", which is
        /// the state of every prefab in the project today.
        /// </summary>
        [Tooltip("Optional child stretched along the shot for spring='2' lasers. Auto-found as a " +
                 "child named 'laser' when left empty — AS3's vis.laser, Bullet.as:213.")]
        [SerializeField] private Transform _beamTransform;

        // ── Runtime state ────────────────────────────────────────────────────

        private bool          _hasDamageContext;
        private DamageContext _damageContext;

        /// <summary>
        /// The direction this shot was launched on, as a unit vector — AS3's <c>knockx</c>/<c>knocky</c>
        /// (<c>Weapon.as:1507-1508</c>). Stamped once in <see cref="Initialize"/> and never updated, so a
        /// round that curves, homes or falls still throws its target along the <i>launch</i> line rather
        /// than the arrival line. <see cref="SetDamageContext"/> writes it onto the damage context,
        /// because <c>DamageContext.FromWeapon</c> has the weapon but not the shot and therefore cannot
        /// know it.
        /// </summary>
        private Vector2 _spawnDirection = Vector2.right;

        private float      _damage;
        private float      _destroyTiles;
        private float      _explRadius;
        private float      _explDamage;
        private DamageType _damageType;

        /// <summary>
        /// The weapon's <c>vis.@visexpl</c> override, or null/empty for none — AS3
        /// <c>Bullet.explVis()</c>'s first branch (<c>weapon/Bullet.as:878-909</c>), which is the ONLY
        /// arm gated on it. Non-empty means the whole damage-type table below it is skipped, so this is
        /// the highest-precedence decision in the explosion's visuals. Held as a string rather than an
        /// enum because one of its five shipped values (<c>sparkle</c>) is a magic instruction and the
        /// other four are particle ids the data names directly.
        /// </summary>
        private string _visExpl;
        private float      _lifetimeTimer;

        /// <summary>
        /// AS3 <c>Bullet.probiv</c> — the round's penetration budget, <b>not</b> a probability.
        /// </summary>
        /// <remarks>
        /// <para>A round whose probiv is above zero does not stop on the unit it hits
        /// (<c>weapon/Bullet.as:533</c> gates the whole stop path on
        /// <c>!(probiv &gt; 0 &amp;&amp; damage &gt; 0)</c>) and <c>Unit.damage()</c> spends its damage
        /// down as it goes. See <see cref="SpendPenetration"/>.</para>
        ///
        /// <para><b>This used to be fed the weapon's <c>@pier</c></b> — a flat armour figure in the
        /// 5..70 range — and consumed as a 0..1 pass-through <i>chance</i>, which was an invented
        /// mechanic on top of a wrong quantity: <c>Clamp01</c> turned every one of the 29 weapons
        /// carrying <c>@pier</c> into a 100% penetrator. The two figures are separate fields now
        /// (<c>WeaponDefinition.piercing</c> vs <c>.penetration</c>).</para>
        /// </remarks>
        private float      _penetration;

        /// <summary>
        /// What is left of this round's damage, spent down by each target it passes through — AS3
        /// <c>Bullet.damage</c>, which <c>Unit.damage()</c> mutates in place (<c>Unit.as:3646-3648</c>,
        /// <c>:3684-3696</c>). Equal to <see cref="_damage"/> until the round penetrates something.
        /// </summary>
        private float      _remainingDamage;

        // ── Manual velocity integration (mirrors AS3 dx/dy/ddx/ddy) ──────────

        private Vector2 _velocity;          // current velocity (unity units/s)
        private float   _ddx;               // per-second X acceleration (accel along aim)
        private float   _ddy;               // per-second Y acceleration (gravity / flame lift)
        private float   _navod;             // homing strength (0=no homing)

        /// <summary>
        /// Arc length flown since spawn, in <b>pixels</b> — AS3 <c>Bullet.dist</c>
        /// (<c>weapon/Bullet.as:416</c>, <c>this.dist += this.vel / param1</c>). Feeds
        /// <see cref="HitAvoidance.Accuracy"/>, where a weapon's <c>prec * 40</c> is divided by it.
        ///
        /// <para><b>Accumulated along the path, not measured start-to-target.</b> The two differ for
        /// anything that curves — a homing round, a grenade, a flame arc — and AS3 accumulates, so a
        /// straight-line measurement would make exactly those weapons <i>more</i> accurate than the
        /// oracle. Both integration paths add to it, because only one of them runs at a time.</para>
        /// </summary>
        private float   _traveledDistancePixels;

        // Scaled to Unity units from AS3 pixel values where needed by caller.
        private const float DefaultLifetime = 30f;
        private const float FlameLifetime1  = 0.7f;   // flame==1 short lifetime (AS3 ~21 frames)
        private const float FlameLifetime2  = 1.2f;   // flame==2 medium lifetime
        // The parking spot comes from the template spec, which is where the old prefab's values now
        // live — one source for "where a released round sits", read by both the template's initial
        // placement and ResetProjectile below.
        private static readonly Vector2 PoolParkingPosition = ProjectileTemplateSpec.ParkingPosition;

        private Rigidbody2D   _rb;
        private Collider2D    _triggerCollider;   // cached trigger — disabled during impact anim
        private bool          _isInitialized;
        private bool          _hasDetonated;
        private bool          _isImpacting;       // true while playing impact frames before pool

        // ── Unit collision — the sim's sub-stepped probe (Stage C) ──────────────────────────────
        // AS3's bullet keeps `parr`: the units it has already struck (`Bullet.as:69`, and `udar()` at
        // `:392-409` refuses a unit already in it), so one bullet hits each unit at most once. The port
        // got that for free from Unity's enter-only trigger semantics; an explicit probe does not, so
        // the list has to exist. Cleared per shot in ResetProjectile — a pooled instance would
        // otherwise carry the previous shot's victims into the next one.
        private readonly List<Collider2D> _struckUnits   = new List<Collider2D>(4);

        /// <summary>
        /// Scratch list for <see cref="EmitExplosionVisuals"/> — filled by
        /// <see cref="PFE.Systems.Particles.ExplosionVisualRules.Plan"/> and drained immediately, so it
        /// is reused rather than reallocated on a per-impact event.
        /// </summary>
        private readonly List<PFE.Systems.Particles.ParticleEmit> _explosionEmits =
            new List<PFE.Systems.Particles.ParticleEmit>(4);

        // Parallel lists, index-for-index: UnitSweepMath takes only the boxes, and the caller needs the
        // collider back to hand to HandleImpact. Reused rather than rebuilt so a held trigger does not
        // allocate per tick.
        private readonly List<UnitBoxPx>  _unitBoxes     = new List<UnitBoxPx>(8);
        private readonly List<Collider2D> _unitColliders = new List<Collider2D>(8);

        /// <summary>
        /// Broad-phase results. Fixed rather than grown: a bullet's swept box is at most ~40 px on a
        /// side, so 32 overlapping colliders is far past anything a room produces, and a silently
        /// truncated list would look exactly like the tunnelling this probe exists to fix.
        /// </summary>
        private readonly Collider2D[]     _unitBuffer    = new Collider2D[32];

        private static ContactFilter2D    _unitFilter;
        private static bool               _unitFilterBuilt;

        // ── Stage C: sim-owned state (only used when ProjectilesUseLowLevelPhysics is on) ──────
        // The simulation owns the position; the Transform is a *view* of it, written in LateUpdate.
        // ISimTickable's contract forbids a tick from reading Transform, so SimTick touches neither
        // transform nor _rb — everything it produces goes through the deferred write below.

        /// <summary>Authoritative position in world units while the flip is on.</summary>
        private Vector2 _simPosition;

        /// <summary>True while this instance is registered on SimLoop and must be unregistered.</summary>
        private bool _registeredOnSimLoop;

        /// <summary>Set by <see cref="SimTick"/>, consumed by <see cref="LateUpdate"/>.</summary>
        private bool _viewDirty;

        /// <summary>Set by <see cref="SimTick"/>, consumed by <see cref="LateUpdate"/> — a tick must
        /// not destroy a GameObject, and the pool's release path is a Unity-side operation.</summary>
        private bool _pendingReturnToPool;

        /// <summary>
        /// The projectile's collision capsule as (length, thickness) in pixels, read from the prefab's
        /// <c>CapsuleCollider2D</c> at <see cref="Awake"/>. Read rather than hardcoded so that editing
        /// the prefab cannot silently desync the tile query from the hitbox that damages entities.
        /// </summary>
        private Vector2 _shapeSizePx = new Vector2(93f, 6f);

        /// <summary>
        /// Contact latch. The legacy path is enter-only — Unity reports a surface once on entry — so a
        /// per-tick sweep would otherwise re-report the same surface every tick, and a piercing round
        /// that keeps flying while overlapping would re-roll its damage chance each time.
        /// </summary>
        private bool _tileContactActive;

        /// <summary>
        /// Normal of the most recent chain contact, in world units. Zero when the contact came from an
        /// initial overlap — <c>CastResult</c> documents the normal as degenerate in exactly that case.
        /// </summary>
        private Vector2 _lastContactNormal;

        // ── Visual ───────────────────────────────────────────────────────────

        private ProjectileVisualDefinition _currentVisual;
        private Transform   _visualTransform;
        private Sprite      _defaultSprite;
        private Color       _defaultColor;
        private Vector3     _defaultLocalPosition;
        private Quaternion  _defaultLocalRotation;
        private Vector3     _defaultLocalScale;
        private int         _defaultSortingOrder;
        private bool        _defaultRendererEnabled;
        private bool        _visualDefaultsCached;
        private float       _visualFrameTimer;
        private int         _visualFrameIndex;
        private Vector3     _spawnPosition;
        private Quaternion  _spawnRotation = Quaternion.identity;

        // ── Injected dependencies ────────────────────────────────────────────

#pragma warning disable CS0649
        [Inject] private IPublisher<DamageDealtMessage> _damageDealtPublisher;
        [Inject] private PFE.Systems.Combat.DamageSystem _damageSystem;
        [Inject] private PFE.Core.PfeDebugSettings      _debugSettings;
        [Inject] private ISoundService                  _soundService;
        [Inject] private ImpactSoundTable               _impactSoundTable;

        // The particle side of an explosion. `RoomParticleEmitter` owns the Unity-world → AS3
        // room-local conversion — every emit site in the game needs the same one, so it is written
        // once there rather than here; `IParticleTileWater` answers the `inWater` question the D_EXPL
        // branch decides on. Both are null-tolerant: a scene without the particle registrations still
        // fires bullets, and the emit is skipped rather than placed at a wrong position.
        [Inject] private PFE.Systems.Particles.Adapters.RoomParticleEmitter _particles;
        [Inject] private PFE.Systems.Particles.IParticleTileWater            _particleTileWater;

        // The unseeded presentation stream. The only jitter the explosion visuals need is the acid
        // blast's `kol` — AS3's `Math.floor(Math.random() * 5 + 30)`. Null-tolerant on purpose: a
        // fixture that builds a projectile with `new` never injects one, and the acid arm then falls
        // back to the oracle's minimum of 30 rather than throwing.
        [Inject] private PFE.Core.Rng.IRngService _rng;
        private PFE.Core.Rng.IRngService _presentationRng;

        // Stage C. Fully qualified: this file is not in the PFE.Systems.Physics namespace, but being
        // explicit here keeps the "which IPhysicsWorldService" question answerable at a glance.
        [Inject] private PFE.Systems.Physics.IPhysicsWorldService _physicsWorld;
        [Inject] private SimLoop                        _simLoop;
        [Inject] private SimClock                       _simClock;
#pragma warning restore CS0649

        /// <summary>Called by GameObjectPool to return the instance after use.</summary>
        public Action<Projectile> OnReturnToPool { get; set; }

        // ── Initialization ───────────────────────────────────────────────────

        private void Awake()
        {
            EnsureVisualRenderer();
            CacheVisualDefaults();
            if (_rb == null)
                _rb = GetComponent<Rigidbody2D>();
            _triggerCollider = GetComponent<Collider2D>();
            CacheShapeSize();
        }

        /// <summary>
        /// Captures the collision capsule's local (length, thickness) in pixels. The prefab uses a
        /// <c>CapsuleCollider2D</c> with a horizontal axis, and the projectile rotates to face travel,
        /// so <c>size.x</c> is the length along the flight direction and <c>size.y</c> the thickness
        /// across it. Box colliders are accepted too so a future archetype cannot silently fall back
        /// to the default without the log telling someone.
        /// </summary>
        private void CacheShapeSize()
        {
            if (_triggerCollider == null) return;

            if (_triggerCollider is CapsuleCollider2D capsule)
            {
                _shapeSizePx = capsule.size * TileQueryConstants.UnitToPixel;
                return;
            }

            if (_triggerCollider is BoxCollider2D box)
            {
                _shapeSizePx = box.size * TileQueryConstants.UnitToPixel;
                return;
            }

            // Anything else (polygon, circle) has no local size to read. Fall back to the world AABB
            // rather than the built-in default, and say so — a silent mismatch here would look like a
            // collision bug much later.
            _shapeSizePx = (Vector2)_triggerCollider.bounds.size * TileQueryConstants.UnitToPixel;
            if (_debugSettings?.LogProjectileLifecycle == true)
            {
                Debug.LogWarning(
                    $"[Projectile] '{name}' has a {_triggerCollider.GetType().Name} rather than a " +
                    $"Capsule/Box collider; the tile sweep is using its world AABB " +
                    $"({_shapeSizePx.x:F1}x{_shapeSizePx.y:F1} px), which is rotation-dependent.");
            }
        }

        /// <summary>
        /// Projectiles move in the <c>Projectiles</c> slot, after the player motor and units have
        /// finalised their positions for this tick.
        ///
        /// <para>That slot is shared with <c>PhysicsWorldService</c>, which is harmless: the chain
        /// mirror is <b>static</b> geometry, and a query against static geometry does not depend on
        /// whether <c>Simulate()</c> has run. That is measured, not assumed — Stage B1's
        /// <c>ChainMirror_AnswersQueries_BeforeTheFirstStep</c> asserts the mirror answers queries with
        /// no step ever taken. So no write/step/read split is needed here, contrary to what
        /// <c>PhysicsWorldService</c>'s "open for Stage C" note anticipated: that note was written for
        /// a consumer that would own <i>bodies</i>, and this consumer owns none.</para>
        /// </summary>
        public int TickOrder => SimTickOrder.Projectiles;

        // ── Stage C: sim registration and tick ───────────────────────────────────────────────

        /// <summary>
        /// True when this instance is actually running the flipped path.
        ///
        /// <para>Keyed off the registration rather than off the settings flag on purpose. If the flag
        /// is on but SimLoop was unavailable, <see cref="RegisterOnSimLoop"/> leaves this false and the
        /// instance stays wholly on the legacy path. Reading the flag instead would leave such an
        /// instance driven by <i>neither</i> loop — it would hang motionless in mid-air, which is a far
        /// worse failure than quietly staying on the old path.</para>
        /// </summary>
        private bool FlipActive => _registeredOnSimLoop;

        /// <summary>
        /// Registers this instance on <see cref="SimLoop"/> so <see cref="SimTick"/> drives it.
        ///
        /// <para>Called from <see cref="Initialize"/>, by which point injection has already happened:
        /// <c>ProjectileFactory.Spawn</c> runs <c>_resolver.Inject(p)</c> inside the pool's onGet
        /// callback, and <c>Initialize</c> only runs after that. Registering from <see cref="Awake"/>
        /// would be too early — the injected fields are still null there.</para>
        ///
        /// <para><c>SimLoop.Register</c> de-duplicates, so a pooled instance re-initialised without a
        /// matching reset cannot end up ticked twice.</para>
        /// </summary>
        private void RegisterOnSimLoop()
        {
            if (_simLoop == null || _simClock == null)
            {
                if (_debugSettings?.LogProjectileLifecycle == true)
                {
                    Debug.LogWarning(
                        "[Projectile] ProjectilesUseLowLevelPhysics is on but SimLoop/SimClock is " +
                        "unavailable; staying on the FixedUpdate path.");
                }
                return;
            }

            _simLoop.Register(this);
            _registeredOnSimLoop = true;
        }

        /// <summary>Releases the SimLoop registration. Idempotent, and safe before any registration.</summary>
        private void UnregisterFromSimLoop()
        {
            if (!_registeredOnSimLoop) return;

            _simLoop?.Unregister(this);
            _registeredOnSimLoop = false;
        }

        /// <summary>
        /// One AS3 frame of projectile flight: integrate, sweep for tiles, then advance or resolve.
        ///
        /// <para>Reads no <c>Time</c>, no <c>Transform</c> and no <c>Input</c> — the step comes from
        /// <c>SimClock.SimDt</c> and the position from <see cref="_simPosition"/>. The Transform write
        /// is deferred to <see cref="LateUpdate"/>, which is the "separate view pass" the
        /// <see cref="ISimTickable"/> contract describes.</para>
        /// </summary>
        public void SimTick(int tickIndex)
        {
            if (!_isInitialized || _isImpacting) return;

            // The per-tick advance is SimDt (a duration in seconds), NOT StepScale, and the two are
            // NOT interchangeable here. StepScale converts a rate stored per canonical 30 Hz frame
            // into a per-tick advance; this file does not store rates that way. _velocity is Unity
            // units/s and _ddx/_ddy are units/s², because FixedUpdate hands _velocity straight to
            // Rigidbody2D.linearVelocity, which is units/s. For a units/s rate the advance is simply
            // the tick's duration. Substituting StepScale would be a 30x error at the canonical rate
            // (SimDt is 1/30 where StepScale is 1) — the velocity-vs-acceleration confusion
            // TileQueryConstants warns about. ISimTickable's "derive the advance from StepScale" note
            // is written for the per-canonical-frame convention, not for this one.
            float dt = _simClock.SimDt;

            // AS3: dx += ddx, dy += ddy, then x += dx, y += dy — once per 30 Hz frame.
            _velocity.x += _ddx * dt;
            _velocity.y += _ddy * dt;

            if (_navod > 0f)
            {
                ApplyHoming(dt, _simPosition);
            }

            // Two unit systems meet here, and the conversion has to happen exactly once. The sim
            // integrates in UNITY UNITS because _velocity is units/s (the legacy path hands it
            // straight to Rigidbody2D.linearVelocity), but the tile seam takes WORLD PIXELS, because
            // that is the space ITileQueryService uses. Converting on the way out AND on the way back
            // is the whole of it — passing units straight through put every query at 1/100 scale near
            // the world origin, where no room has geometry, so every projectile flew through every
            // wall. See TileQueryConstants.UnitToPixel.
            Vector2 fromUnits  = _simPosition;
            Vector2 deltaUnits = _velocity * dt;

            Vector2 fromPx  = fromUnits  * TileQueryConstants.UnitToPixel;
            Vector2 deltaPx = deltaUnits * TileQueryConstants.UnitToPixel;

            // Both collision questions are asked over the SAME segment, and the nearer answer wins.
            // The tile side is one swept test over the whole segment; the unit side is AS3's sub-step
            // loop (UnitSweepMath). AS3 decides by testing both at each sub-step and acting on the
            // first that fires, which for a segment is the same thing as taking the nearer contact —
            // the two agree everywhere except within a single 9 px sub-step.
            //
            // Asking the tile question first and returning on its answer would be wrong, not merely
            // less accurate: a unit standing in front of a wall is the ordinary case in a room, the
            // sweep would report the wall, and the unit would never be hit. That is precisely the
            // "bullet went through the enemy" report this probe exists to fix.
            bool  tileHit        = TryTileContact(fromPx, deltaPx, out Vector2 contactPx);
            float tileDistancePx = tileHit ? (contactPx - fromPx).magnitude : float.PositiveInfinity;

            // Unit contacts are a LOOP, not a single test. AS3's sub-step loop carries on after a unit
            // it did not stop on: `udarBullet` returning -1 falls through to the next unit in
            // `loc.units` (`weapon/Bullet.as:535-553`), and the unit it just tested is already in
            // `parr`, so the next sub-step cannot test it again. The port asks that question once per
            // segment instead of once per sub-step, so the equivalent is to re-sweep the segment with
            // the tested unit excluded — which ResolveUnitImpact has already arranged by recording it.
            // Bounded, because every pass removes one candidate from GatherUnitBoxes.
            bool  unitHit            = false;
            float lastUnitDistancePx = 0f;

            while (TryUnitContact(fromPx, deltaPx, out Collider2D unit,
                                  out Vector2 unitHitPx, out float unitDistancePx)
                   && unitDistancePx < tileDistancePx)
            {
                unitHit = true;

                // Accumulated up to each event, so the hit-avoidance distance term (AS3 Bullet.dist)
                // reads the distance to the hit being resolved rather than the segment end.
                _traveledDistancePixels += unitDistancePx - lastUnitDistancePx;
                lastUnitDistancePx       = unitDistancePx;

                _simPosition = unitHitPx * TileQueryConstants.PixelToUnit;
                _viewDirty   = true;

                // Stopped: impact frames playing, or already handed back to the pool.
                if (ResolveUnitImpact(unit, unitHitPx)) return;

                // Survived — the round pierced through, the faction gate declined it (AS3's
                // `Bullet.as:515` fails for a same-faction unit), or the target evaded it and AS3's
                // `-1` left the round in flight. A wall further along the segment is deliberately NOT
                // resolved here: TryTileContact's enter-only latch is per-tick state, so re-running it
                // inside one tick would be told "already in contact" and skip the wall entirely. The
                // cost is bounded by one tick of travel, and the next tick's sweep starts from the far
                // side.
            }

            if (unitHit)
            {
                _traveledDistancePixels += deltaPx.magnitude - lastUnitDistancePx;
            }
            else if (tileHit)
            {
                // contactPx comes back in world pixels; the sim's own position is in units.
                _simPosition = contactPx * TileQueryConstants.PixelToUnit;
                _viewDirty   = true;
                _traveledDistancePixels += tileDistancePx;
                ResolveTileImpact(contactPx);
                return;
            }
            else
            {
                _traveledDistancePixels += deltaPx.magnitude;
            }

            _simPosition = fromUnits + deltaUnits;
            _viewDirty   = true;

            _lifetimeTimer -= dt;
            if (_lifetimeTimer <= 0f)
            {
                // Not ReturnToPool() here: the pool release disables a GameObject, and a tick must not
                // do that mid-iteration over the tickable list. LateUpdate performs it.
                _pendingReturnToPool = true;
            }
        }

        /// <summary>
        /// Asks the chain mirror whether this step's sweep reaches a tile.
        ///
        /// <para><b>Coordinates here are WORLD PIXELS, not the sim's Unity units.</b> This method is
        /// the boundary, and <see cref="SimTick"/> does the conversion — keeping the seam's own unit
        /// at this edge means there is exactly one place to get it wrong, and the parameter names say
        /// which unit they want. They did not, once: units were passed to a pixel seam, every query
        /// landed at 1/100 scale near the world origin, and projectiles flew through walls.</para>
        ///
        /// <para>Applies the enter-only latch, so a surface is reported on the tick its contact begins
        /// rather than on every tick it persists — which is what <c>OnTriggerEnter2D</c> gives the
        /// legacy path. Without it, a piercing round overlapping a wall would re-roll its pass-through
        /// chance on every tick of the overlap.</para>
        /// </summary>
        /// <param name="fromPx">Sweep origin, world pixels.</param>
        /// <param name="deltaPx">Translation over this step, world pixels.</param>
        /// <param name="contactPx">Contact point in world pixels — the same unit the seam reports.</param>
        private bool TryTileContact(Vector2 fromPx, Vector2 deltaPx, out Vector2 contactPx)
        {
            contactPx          = default;
            _lastContactNormal = Vector2.zero;

            if (_physicsWorld == null || !_physicsWorld.IsWorldValid)
            {
                // No chain geometry to consult — an unbuilt room, or the world already torn down.
                // Answer "nothing in the way" rather than "hit": stopping at an invisible wall would
                // be a much louder failure than flying on.
                _tileContactActive = false;
                return false;
            }

            Vector2 facing = _velocity.sqrMagnitude > 0.0001f ? _velocity.normalized : Vector2.right;

            bool touching = _physicsWorld.TrySweepTiles(
                fromPx, deltaPx, _shapeSizePx, facing, out Vector2 point, out Vector2 normal);

            if (!touching)
            {
                _tileContactActive = false;
                return false;
            }

            if (_tileContactActive) return false;

            _tileContactActive = true;
            _lastContactNormal = normal;
            contactPx          = point;
            return true;
        }

        /// <summary>
        /// Asks whether this tick's segment reaches a unit, using AS3's sub-step loop rather than an
        /// overlap sample.
        ///
        /// <para><b>Why the tile sweep's approach does not transfer.</b> <c>TrySweepTiles</c> can be a
        /// swept shape because a chain is zero-thickness and a sampled test would step over it. Units
        /// are the opposite problem: AS3's test <i>is</i> a point (<c>Bullet.as:515</c>), so what
        /// prevents tunnelling there is the advance per test, not the shape. AS3 advances at most
        /// <c>World.maxdelta</c> = 9 px (<c>World.as:52</c>, loop at <c>Bullet.as:199-205</c>) and
        /// tests the point against each unit's bounds after every sub-step. A tick here covers 40 px
        /// against a 12–24 px box, so one sample is a coin flip — the reported "sometimes".</para>
        ///
        /// <para>Candidates come from one broad-phase query over the segment's box; the sub-step
        /// arithmetic lives in <see cref="UnitSweepMath"/>, which has no Unity dependency and is
        /// exercised offline.</para>
        /// </summary>
        /// <param name="unit">First unit struck, in broad-phase order.</param>
        /// <param name="hitPx">Sub-step position that struck it, world pixels.</param>
        /// <param name="distancePx">How far along the segment that is, for the tile-versus-unit comparison.</param>
        private bool TryUnitContact(Vector2 fromPx, Vector2 deltaPx,
                                    out Collider2D unit, out Vector2 hitPx, out float distancePx)
        {
            unit       = null;
            hitPx      = default;
            distancePx = 0f;

            if (GatherUnitBoxes(fromPx, deltaPx) == 0) return false;

            if (!UnitSweepMath.TryFirstHit(
                    fromPx.x, fromPx.y, deltaPx.x, deltaPx.y, _unitBoxes,
                    UnitSweepMath.MaxDeltaPx, out int index, out float hitX, out float hitY))
            {
                return false;
            }

            unit       = _unitColliders[index];
            hitPx      = new Vector2(hitX, hitY);
            distancePx = (hitPx - fromPx).magnitude;
            return true;
        }

        /// <summary>
        /// Collects the units this tick's segment could reach into <see cref="_unitBoxes"/> /
        /// <see cref="_unitColliders"/>, index-for-index. Returns the count.
        ///
        /// <para>The filters mirror <see cref="OnTriggerEnter2D"/>'s so the probe sees the set the
        /// trigger would have seen: triggers are skipped (that callback returns on them), the tile grid
        /// is skipped (the chain sweep owns tiles whenever this probe runs), and units already struck
        /// are skipped — that last one is AS3's <c>parr</c>, and without it a piercing round would
        /// damage the same unit on every tick it spent inside the box.</para>
        ///
        /// <para>Bounds come from the collider's world AABB, the closest thing the port has to AS3's
        /// <c>X1..X2</c>/<c>Y1..Y2</c> logical box. That is a Unity read, like the tile-identity lookup
        /// in <see cref="FindTileCollider"/> — a read, not a decision, and Stage D is what moves it.</para>
        /// </summary>
        private int GatherUnitBoxes(Vector2 fromPx, Vector2 deltaPx)
        {
            _unitBoxes.Clear();
            _unitColliders.Clear();

            // The broad phase speaks world UNITS; everything else in this method is pixels.
            Vector2 endPx = fromPx + deltaPx;
            var minPx = new Vector2(Mathf.Min(fromPx.x, endPx.x), Mathf.Min(fromPx.y, endPx.y));
            var maxPx = new Vector2(Mathf.Max(fromPx.x, endPx.x), Mathf.Max(fromPx.y, endPx.y));

            int count = Physics2D.OverlapArea(
                minPx * TileQueryConstants.PixelToUnit,
                maxPx * TileQueryConstants.PixelToUnit,
                UnitFilter, _unitBuffer);

            for (int i = 0; i < count; i++)
            {
                Collider2D candidate = _unitBuffer[i];
                if (candidate == null) continue;
                if (candidate.isTrigger) continue;
                if (candidate == _triggerCollider) continue;
                if (candidate.GetComponent<TileCollider>() != null) continue;
                if (_struckUnits.Contains(candidate)) continue;

                Bounds bounds = candidate.bounds;
                _unitBoxes.Add(new UnitBoxPx(
                    bounds.min.x * TileQueryConstants.UnitToPixel,
                    bounds.max.x * TileQueryConstants.UnitToPixel,
                    bounds.min.y * TileQueryConstants.UnitToPixel,
                    bounds.max.y * TileQueryConstants.UnitToPixel));
                _unitColliders.Add(candidate);
            }

            return _unitBoxes.Count;
        }

        /// <summary>
        /// Resolves a unit contact found by the probe, reusing the legacy impact path verbatim so
        /// damage, armour, sound, AoE and the impact animation all behave as they do on the trigger
        /// path — the same single-sourcing <see cref="ResolveTileImpact"/> does for tiles.
        /// </summary>
        /// <returns><c>true</c> when the round stopped on this unit; <c>false</c> when it flew on.</returns>
        private bool ResolveUnitImpact(Collider2D unit, Vector2 hitPx)
        {
            // Recorded before the impact, matching AS3: `udar()` pushes the unit onto `parr` and only
            // then does the caller apply damage (`Bullet.as:519-521`), so a round that pierces is
            // already immune to re-hitting the unit it pierced.
            //
            // It is also what makes "fly on through an evaded unit" terminate: `udar()` runs *before*
            // `udarBullet()` in the oracle, so an evaded unit is in `parr` too and is never tested
            // again. GatherUnitBoxes skips this list, so each sweep of the remaining segment excludes
            // one more candidate and the loop in SimTick cannot spin.
            if (!_struckUnits.Contains(unit)) _struckUnits.Add(unit);

            // _spawnPosition.z rather than transform.position.z: this runs inside a tick, and a tick
            // may not read Transform.
            var impactWorld = new Vector3(
                hitPx.x * TileQueryConstants.PixelToUnit,
                hitPx.y * TileQueryConstants.PixelToUnit,
                _spawnPosition.z);

            return HandleImpact(unit, impactWorld);
        }

        /// <summary>
        /// The broad-phase filter for <see cref="GatherUnitBoxes"/>. Triggers are excluded because
        /// <see cref="OnTriggerEnter2D"/> ignores them too; every other dimension is left unfiltered so
        /// the probe cannot be narrower than the trigger it replaces — which would present as hits that
        /// quietly stopped working in the flip.
        /// </summary>
        private static ContactFilter2D UnitFilter
        {
            get
            {
                if (!_unitFilterBuilt)
                {
                    _unitFilter = new ContactFilter2D
                    {
                        useTriggers           = false,
                        useLayerMask          = false,
                        useDepth              = false,
                        useNormalAngle        = false,
                        useOutsideDepth       = false,
                        useOutsideNormalAngle = false,
                    };
                    _unitFilterBuilt = true;
                }

                return _unitFilter;
            }
        }

        /// <summary>
        /// Resolves a tile contact found by the chain sweep, reusing the legacy impact path verbatim so
        /// sound, damage, tile destruction, AoE and the impact animation all behave identically.
        ///
        /// <para><b>The one step that is not single-sourced.</b> The chain decides <i>whether</i> a tile
        /// was hit and <i>where</i>; the tile's identity still comes from Unity. That is not laziness:
        /// <c>ImpactSoundResolver</c> reads the collider's <c>TileSurface</c> material, and
        /// <c>IDestructibleTile</c> is implemented on <c>TileCollider</c>, which lives on the tile's
        /// GameObject. Neither is reachable from a chain. Keeping the lookup on Unity is deliberate —
        /// it is a lookup, not a decision — and Stage D is what removes the per-tile colliders.</para>
        ///
        /// <para>If no collider can be identified the projectile still stops. Stopping is the safe
        /// failure; continuing would fly it through a wall it demonstrably hit.</para>
        /// </summary>
        private void ResolveTileImpact(Vector2 contactPx)
        {
            // _spawnPosition.z rather than transform.position.z: this runs inside a tick, and a tick
            // may not read Transform.
            var impactWorld = new Vector3(
                contactPx.x * TileQueryConstants.PixelToUnit,
                contactPx.y * TileQueryConstants.PixelToUnit,
                _spawnPosition.z);

            Collider2D tile = FindTileCollider(contactPx);
            if (tile != null)
            {
                HandleImpact(tile, impactWorld);
                return;
            }

            if (_debugSettings?.LogProjectileLifecycle == true)
            {
                Debug.LogWarning(
                    $"[Projectile] '{name}' chain contact at {contactPx} px identified no tile " +
                    "collider; stopping without surface sound or tile destruction.");
            }

            StartImpactAnimation();
        }

        /// <summary>
        /// Maps a world-pixel contact point back to the tile collider that owns it, supplying the
        /// identity the chain cannot carry (surface material, destructibility).
        ///
        /// <para>The probe is offset slightly <i>behind</i> the contact along the surface normal,
        /// because the contact point lies exactly on the chain — the boundary of the solid — where a
        /// point overlap is ambiguous. With no normal (the initial-overlap case) it probes the contact
        /// point itself. The probe box is 6 px, comfortably inside a 40 px tile and inside the 10 px
        /// one-way platform slab.</para>
        /// </summary>
        private Collider2D FindTileCollider(Vector2 contactPx)
        {
            Vector2 probePx = _lastContactNormal.sqrMagnitude > 0f
                ? contactPx - _lastContactNormal * 2f
                : contactPx;

            var centre = new Vector2(
                probePx.x * TileQueryConstants.PixelToUnit,
                probePx.y * TileQueryConstants.PixelToUnit);
            Collider2D[] hits = Physics2D.OverlapBoxAll(centre, new Vector2(0.06f, 0.06f), 0f);

            for (int i = 0; i < hits.Length; i++)
            {
                Collider2D hit = hits[i];
                if (hit == null || hit.isTrigger) continue;

                // Identified by component rather than by layer number: the tile layers are private
                // constants inside TileCollider, and a lookup that silently stops matching if those
                // numbers are ever renumbered would present as "bullets no longer break tiles".
                if (hit.GetComponent<TileCollider>() == null) continue;

                return hit;
            }

            return null;
        }

        /// <summary>
        /// Prepare a pooled projectile for reuse before Initialize().
        /// Sets the Rigidbody2D pose directly so physics does not keep a stale
        /// body position from the previous lifetime.
        /// </summary>
        public void PrepareForSpawn(Vector3 position, Quaternion rotation)
        {
            _spawnPosition = position;
            _spawnRotation = rotation;

            if (_rb == null)
                _rb = GetComponent<Rigidbody2D>();

            // Set transform while still inactive (pool calls this before SetActive(true)).
            // Unity reads transform.position when re-adding the body to the simulation on
            // SetActive(true), so this is what determines the entry position — no CCD sweep.
            // Do NOT touch rb.simulated here: toggling simulated while inactive causes Box2D
            // to lose track of the body position, making Physics2D.SyncTransforms ineffective.
            transform.position = position;
            transform.rotation = rotation;
        }

        /// <summary>
        /// Full initialization from a WeaponDefinition.
        /// Called by ProjectileFactory after getting from pool.
        ///
        /// Parameters match AS3 Weapon.shoot() locals:
        ///   speed        → initial |velocity| in Unity units/s
        ///   gravityScale → AS3 `grav`, a MULTIPLIER on World.ddy (0 = no gravity, 1 = full).
        ///                  Pass the raw `phis.@grav`; the px/frame² → units/s² conversion happens here.
        ///   accel        → AS3 `phis.@accel`, forward thrust in px/frame² (rockets). Pass the raw
        ///                  data value, exactly like `gravityScale` — the px/frame² → units/s²
        ///                  conversion happens in `ProjectilePhysicsMath.BulletAcceleration`.
        ///   flame        → 0=none, 1=strong up arc, 2=weak up arc
        ///   navod        → homing strength per flash-frame
        ///   penetration  → AS3 probiv: the penetration budget. > 0 makes this a penetrator, which does
        ///                  not stop on a unit. NOT a probability, and not the weapon's `@pier`.
        ///   visExpl      → AS3 `weap.visexpl`: the per-weapon explosion-visual override, passed through
        ///                  untouched. Empty/null is the common case and selects the damage-type table.
        /// </summary>
        public void Initialize(float damage, float speed, Vector2 direction,
                               float gravityScale = 0f,
                               float destroyTiles = 0f,
                               float explRadius   = 0f,
                               float explDamage   = 0f,
                               DamageType damageType = DamageType.PhysicalBullet,
                               float accel        = 0f,
                               int   flame        = 0,
                               float navod        = 0f,
                               float penetration  = 0f,
                               string visExpl     = null)
        {
            _damage       = damage;
            _destroyTiles = destroyTiles;
            _explRadius   = explRadius;
            _explDamage   = explDamage;
            _damageType   = damageType;
            _visExpl      = visExpl;
            _penetration  = Mathf.Clamp01(penetration);
            _remainingDamage = damage;
            _navod        = navod;
            _isInitialized = true;
            _hasDetonated  = false;
            _traveledDistancePixels = 0f;

            if (_rb == null) _rb = GetComponent<Rigidbody2D>();

            // Reassert the intended spawn pose now that the object is active.
            // PrepareForSpawn sets transform.position before SetActive(true), so the body
            // already entered the simulation here at the correct position. We reassert
            // both the transform and rb.position, then call SyncTransforms as belt-and-
            // suspenders to make Box2D adopt this as the authoritative position before
            // we set linearVelocity (so CCD never sweeps from the old parking position).
            transform.position = _spawnPosition;
            transform.rotation = _spawnRotation;
            _rb.position       = _spawnPosition;
            _rb.rotation       = _spawnRotation.eulerAngles.z;
            Physics2D.SyncTransforms();

            // Always kinematic — we drive velocity manually.
            _rb.bodyType              = RigidbodyType2D.Kinematic;
            _rb.gravityScale          = 0f;
            _rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;

            Vector2 dir = direction.normalized;
            _velocity = dir * speed;

            // AS3 stamps the knock direction here and nowhere else: `Weapon.as:1507-1508` sets
            // `b.knockx = b.dx / b.vel; b.knocky = b.dy / b.vel` at fire time, and no later code updates
            // it — not `ApplyHoming`, not gravity. Held rather than re-derived at the hit, so the port
            // reproduces that instead of throwing along the direction the round happened to arrive on.
            _spawnDirection = dir;

            // ── Per-second acceleration ───────────────────────────────────────
            // AS3 `Weapon.as:1524` zeroes `b.ddx`/`b.ddy`, then 1536-1537 (thrust), 1545/1551
            // (flame lift) and 1558 (gravity) each `+=` into them. All three are px/frame²
            // accelerations and they ACCUMULATE — assigning instead of adding silently drops a
            // term (a flame weapon would lose gravity).
            //
            // The conversion lives in ProjectilePhysicsMath, which is a pure function so the numbers
            // can be pinned by tests. Do not reintroduce a local `* SimClock.FramesPerSecond` here:
            // that is the per-frame VELOCITY idiom, 3.33× too strong for a per-frame² acceleration.
            Vector2 acceleration = ProjectilePhysicsMath.BulletAcceleration(dir, gravityScale, accel, flame);
            _ddx = acceleration.x;
            _ddy = acceleration.y;

            // Lifetime depends on flame type; the lift itself is handled above.
            if (flame == 1)
            {
                _lifetimeTimer = FlameLifetime1;
            }
            else if (flame == 2)
            {
                _lifetimeTimer = FlameLifetime2;
            }
            else
            {
                _lifetimeTimer = DefaultLifetime;
            }

            _rb.linearVelocity = _velocity;
            _rb.angularVelocity = 0f;
            _rb.simulated = true;
            _rb.WakeUp();

            float angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
            transform.rotation = Quaternion.Euler(0f, 0f, angle);
            _rb.rotation       = angle;

            // ── Stage C: hand this flight to the sim, or leave it on the legacy path ───────────
            _tileContactActive   = false;
            _viewDirty           = false;
            _pendingReturnToPool = false;
            _simPosition         = _spawnPosition;   // world units — the sim's origin for this flight

            if (_debugSettings?.ProjectilesUseLowLevelPhysics == true)
            {
                RegisterOnSimLoop();
            }

            if (_debugSettings?.LogProjectileLifecycle == true)
            {
                Debug.Log(
                    $"[Projectile] Initialize instance='{name}' activeSelf={gameObject.activeSelf} activeInHierarchy={gameObject.activeInHierarchy} " +
                    $"spawnPos={_spawnPosition} pos={transform.position} rbPos={(Vector3)_rb.position} vel={_velocity} gravity={gravityScale:0.###} accel={accel:0.###} " +
                    $"rendererEnabled={_visualRenderer != null && _visualRenderer.enabled}.");
            }
        }

        /// <summary>
        /// Attach a pre-computed DamageContext to this projectile.
        /// When set, impact damage is reported to DamageSystem instead of applied as raw _damage.
        /// Call after Initialize().
        /// </summary>
        public void SetDamageContext(DamageContext ctx)
        {
            if (!_isInitialized)
            {
                // Loud, because the failure would be silent and directional: an unstamped context keeps
                // DamageContext.FromWeapon's zero direction, so this shot would knock nothing back while
                // every other weapon did.
                Debug.LogWarning(
                    "[Projectile] SetDamageContext called before Initialize; this shot's knockback " +
                    "direction is unknown and will be left unstamped. Call Initialize first.");
            }

            // AS3's `knockx`/`knocky`, stamped onto the context. WithScaledDamage(1, 1, dir) leaves the
            // damage and the knock magnitude untouched and replaces only the direction.
            _damageContext    = ctx.WithScaledDamage(1f, 1f, _spawnDirection);
            _hasDamageContext = true;

            if (_debugSettings?.LogProjectileLifecycle == true)
            {
                Debug.Log(
                    $"[Projectile] SetDamageContext instance='{name}' weapon='{ctx.Weapon?.weaponId ?? "null"}' " +
                    $"baseDamage={ctx.BaseDamage} damageType={ctx.DamageType} " +
                    $"knockback={_damageContext.Knockback} knockDir={_damageContext.KnockbackDir}.");
            }
        }

        /// <summary>
        /// Applies imported projectile art to the pooled projectile instance.
        /// </summary>
        public void ApplyVisual(ProjectileVisualDefinition visual)
        {
            EnsureVisualRenderer();
            CacheVisualDefaults();

            _currentVisual    = visual;
            _visualFrameTimer = 0f;
            _visualFrameIndex = 0;

            if (_visualRenderer == null) return;

            if (visual == null || visual.frames == null || visual.frames.Length == 0)
            {
                RestoreDefaultVisual();
                if (_debugSettings?.LogProjectileLifecycle == true)
                    Debug.Log($"[Projectile] ApplyVisual instance='{name}' using default visual.");
                return;
            }

            _visualRenderer.enabled      = true;
            _visualRenderer.sprite       = visual.frames[0];
            _visualRenderer.color        = visual.colorTint;
            _visualRenderer.sortingOrder = visual.sortingOrder;
            //_visualRenderer.sortingLayerName = "Foreground";
            if (_visualTransform != null)
            {
                _visualTransform.localPosition = visual.localOffset;
                _visualTransform.localRotation = Quaternion.Euler(0f, 0f, visual.localRotation);
                _visualTransform.localScale    = visual.localScale;
            }

            if (_debugSettings?.LogProjectileLifecycle == true)
            {
                Debug.Log(
                    $"[Projectile] ApplyVisual instance='{name}' visual='{visual.name}' frames={visual.frames.Length} " +
                    $"sprite='{_visualRenderer.sprite?.name ?? "null"}' rendererEnabled={_visualRenderer.enabled}.");
            }
        }

        // ── Unity lifecycle ──────────────────────────────────────────────────

        /// <summary>
        /// Legacy integration path. Active only when the Stage C flip is <i>not</i> running for this
        /// instance — see <see cref="FlipActive"/>, which is keyed off the SimLoop registration rather
        /// than off the settings flag so an instance whose registration failed is still driven.
        /// </summary>
        private void FixedUpdate()
        {
            if (!_isInitialized) return;

            // With the flip running, SimLoop owns integration. Standing down here is essential:
            // leaving both drivers active would step the projectile twice per frame, at two rates.
            if (FlipActive) return;

            float dt = Time.fixedDeltaTime;

            // ── Manual velocity integration (AS3: dx+=ddx, dy+=ddy, x+=dx, y+=dy) ──
            _velocity.x += _ddx * dt;
            _velocity.y += _ddy * dt;

            // ── Homing (navod) ────────────────────────────────────────────────
            if (_navod > 0f)
                ApplyHoming(dt, transform.position);

            _rb.linearVelocity = _velocity;

            // AS3 accumulates `dist` from the step it just took (`Bullet.as:416`); the legacy path's
            // step is the velocity handed to the rigidbody for this frame. See
            // _traveledDistancePixels for why this is arc length rather than start-to-target.
            _traveledDistancePixels += _velocity.magnitude * dt * TileQueryConstants.UnitToPixel;

            // ── Rotate sprite to face direction of travel ─────────────────────
            if (!_isImpacting && _velocity.sqrMagnitude > 0.0001f)
            {
                float angle = Mathf.Atan2(_velocity.y, _velocity.x) * Mathf.Rad2Deg;
                _rb.rotation       = angle;
                transform.rotation = Quaternion.Euler(0f, 0f, angle);
            }

            // ── Lifetime ──────────────────────────────────────────────────────
            _lifetimeTimer -= dt;
            if (_lifetimeTimer <= 0f) ReturnToPool();
        }

        /// <summary>
        /// The flipped path's view pass: writes sim state out to the Transform and the Rigidbody, and
        /// performs the pool return that <see cref="SimTick"/> deferred.
        ///
        /// <para><b>Position is written and velocity is zeroed, never both set.</b> A kinematic body
        /// given both a velocity and an explicit position would advance twice — once from our own
        /// integration and once from Box2D sweeping the velocity we handed it. The Rigidbody still has
        /// to be moved, because Unity's trigger detection (which resolves <c>IDamageable</c> hits in
        /// <i>both</i> modes) reads the body's position, not the Transform.</para>
        /// </summary>
        private void LateUpdate()
        {
            if (!FlipActive) return;

            if (_pendingReturnToPool)
            {
                _pendingReturnToPool = false;
                ReturnToPool();
                return;
            }

            if (!_viewDirty) return;
            _viewDirty = false;

            transform.position = new Vector3(_simPosition.x, _simPosition.y, _spawnPosition.z);

            if (_rb != null)
            {
                _rb.position       = _simPosition;
                _rb.linearVelocity = Vector2.zero;
            }

            // Facing is a view concern, so it lives here rather than in the tick.
            if (!_isImpacting && _velocity.sqrMagnitude > 0.0001f)
            {
                float angle = Mathf.Atan2(_velocity.y, _velocity.x) * Mathf.Rad2Deg;
                transform.rotation = Quaternion.Euler(0f, 0f, angle);
                if (_rb != null) _rb.rotation = angle;
            }
        }

        private void Update()
        {
            if (!_isInitialized) return;
            UpdateVisualAnimation();
            UpdateBeamStretch();
        }

        /// <summary>
        /// The shot's <c>vis.@spring</c> — imported into <c>WeaponDefinition.springMode</c>. Set by the
        /// spawner rather than carried on the plan: see the "Where projectile physics went" note in
        /// <c>ShotPlan</c>, which removed the per-shot copy because the spawner already builds from the
        /// definition. Default 1 is AS3's own declaration default (<c>Bullet.as:57</c>,
        /// <c>Weapon.as:222</c>).
        /// </summary>
        public void SetSpringMode(int springMode) => _springMode = springMode;

        private int _springMode = 1;

        /// <summary>
        /// The port's mirror of AS3's whole view-stretch block — <c>Bullet.as:213-227</c>, all three
        /// branches, not just the beam.
        ///
        /// <para><b>Three arms, and they do not share a target.</b> A beam (<c>spring &gt;= 2</c> with a
        /// <c>laser</c> child) stretches the <i>child</i> by distance from the flight's origin; a fast
        /// round (<c>spring == 1 &amp;&amp; vel &gt; 100</c>) smears the <i>whole view</i> by its own
        /// speed; everything else resets the view to its natural size. AS3 assigns <c>scaleX</c>
        /// <b>absolutely</b> every frame, so there is no "base" to preserve — which is why writing the
        /// raw factor is right, and why the natural-size arm can be an unconditional <c>1</c> (every
        /// projectile visual definition in the project carries <c>localScale: {1,1,1}</c>, so the reset
        /// and the untouched default are the same picture).</para>
        ///
        /// <para><b>The branch choice lives in the rules type, not here.</b>
        /// <see cref="PFE.Systems.Weapons.ProjectilePhysicsMath.BulletViewScaleTarget"/> owns the order
        /// <i>and</i> the one arm that writes nothing, so both are pinned by fixtures instead of being
        /// reachable only through a private member of a <c>MonoBehaviour</c>. This method only converts
        /// units, picks the transform and writes.</para>
        ///
        /// <para><b>Still inert for the beam until the template has the child.</b> The code-built
        /// template (<see cref="ProjectileTemplateBuilder"/>) creates no child named <c>laser</c>, so the
        /// beam arm is a no-op today — deliberately. It is now a one-line change in the builder rather
        /// than a prefab edit, which is the whole reason building in code was worth doing. The smear arm,
        /// by contrast, is <b>live now</b>:
        /// it applies to any <c>spring == 1</c> weapon whose round exceeds 100 px/frame, which is the
        /// owner's "rail — I am not sure but worth to check".</para>
        /// </summary>
        private void UpdateBeamStretch()
        {
            // AS3's `this.vel` is px/frame; `_velocity` is units/s. The conversion is the velocity
            // factor (×0.3), not the identity and not the acceleration one.
            float velocityPxPerFrame = _velocity.magnitude / ProjectilePhysicsMath.VelocityScale;

            ProjectilePhysicsMath.ViewScaleTarget target = ProjectilePhysicsMath.BulletViewScaleTarget(
                hasBeamChild:        _beamTransform != null,
                spring:              _springMode,
                velocityPxPerFrame:  velocityPxPerFrame,
                hasDetonated:        _hasDetonated);

            // `babah` inside the smear arm: AS3 writes nothing, so neither do we. Returning rather than
            // writing 1 is the whole reason the target is a three-way choice.
            if (target == ProjectilePhysicsMath.ViewScaleTarget.None) return;

            bool beam = target == ProjectilePhysicsMath.ViewScaleTarget.BeamChild;
            Transform targetTransform = beam ? _beamTransform : _visualTransform;
            if (targetTransform == null) return;

            // Only the beam arm measures distance, and only the smear arm measures speed — each is the
            // other's 0, because `BulletViewScaleTarget` has already decided which arm is live.
            float distancePx = beam
                ? Vector2.Distance(_spawnPosition, transform.position) / TileQueryConstants.PixelToUnit
                : 0f;

            float scaleX = ProjectilePhysicsMath.BulletViewScaleX(
                hasBeamChild:         beam,
                spring:               _springMode,
                velocityPxPerFrame:   velocityPxPerFrame,
                distanceFromOriginPx: distancePx);

            Vector3 scale = targetTransform.localScale;
            scale.x = scaleX;
            targetTransform.localScale = scale;
        }

        private void OnTriggerEnter2D(Collider2D other)
        {
            if (!_isInitialized) return;

            // A tile collider only stops a bullet when the tile is a WALL — see
            // ProjectileOcclusionRule for the AS3 citations and for why this is not
            // `TileData.IsSolid()` (that helper includes Platform and Stair, which is how bullets
            // came to stop in mid-air on every catwalk and ladder).
            //
            // The rule lives in one shared place because RoomChainGeometry.IsSolidAt is the flipped
            // path's half of it: the two must agree or the Stage C flag would change gameplay rather
            // than only the implementation.
            TileCollider tileCollider = other.GetComponent<TileCollider>();
            if (tileCollider != null)
            {
                // With the flip running, tile contacts are the chain sweep's job. Without this the
                // per-tile collider would report the same surface and every wall hit would be
                // resolved twice — two impact sounds, two damage rolls, two destruction calls, two
                // AoE detonations.
                if (FlipActive) return;

                if (!ProjectileOcclusionRule.BlocksProjectile(tileCollider.GetTileData())) return;
            }
            // Entity hits are NOT filtered by surface kind — enemies, the player and destructible props
            // are still Unity colliders in both modes. They ARE filtered by which path owns them, and
            // that gate sits below the trigger diagnostic so the log keeps working in both modes.

            if (other.isTrigger)
            {
                if (_debugSettings?.LogProjectileLifecycle == true)
                {
                    Debug.Log(
                        $"[Projectile] Ignored trigger hit instance='{name}' " +
                        $"selfPos={transform.position} rbPos={(_rb != null ? (Vector3)_rb.position : transform.position)} " +
                        $"other={DescribeCollider(other)}.");
                }
                return;
            }

            // With the flip running, the sim's sub-stepped unit probe owns entity contacts — see
            // TryUnitContact and its call site in SimTick. Resolving them here as well would fire every
            // impact twice: two damage rolls, two impact sounds, two AoE detonations. Same argument as
            // the tile branch above, and safe for the same reason — FlipActive IS the SimLoop
            // registration, so the probe runs exactly when this returns, and the legacy path is
            // untouched when it does not.
            if (FlipActive) return;

            // The return is deliberately discarded. On this path a stop is expressed by
            // StartImpactAnimation zeroing the velocity and disabling the trigger, so `false` simply
            // means no stop was requested and the Rigidbody carries the round on through the unit —
            // which is exactly what AS3's `-1` does.
            HandleImpact(other, transform.position);
        }

        // ── Homing ───────────────────────────────────────────────────────────

        /// <summary>
        /// Steers velocity toward the nearest IDamageable each tick.
        /// AS3: finds nearest enemy, rotates dx/dy toward it by navod strength.
        ///
        /// <para><b>Accepted Stage C divergence.</b> This is the one part of the tick that is not
        /// deterministic under <c>ISimTickable</c>'s contract: it reads other objects'
        /// <c>Transform</c>s, and targets (enemies, props) are Unity objects that the simulation does
        /// not own. The projectile's <i>own</i> position is passed in so at least this side of the
        /// calculation comes from sim state. Making homing deterministic needs targets in the sim,
        /// which is a later stage's problem — it is recorded here rather than quietly ignored.</para>
        /// </summary>
        /// <param name="selfPosition">This projectile's position in world units, from sim state.</param>
        private void ApplyHoming(float dt, Vector2 selfPosition)
        {
            // Find nearest IDamageable in scene (simple OverlapCircle approach).
            // navod strength controls how sharply we turn per second.
            Collider2D[] hits = Physics2D.OverlapCircleAll(selfPosition, 20f);
            Transform best = null;
            float bestDist = float.MaxValue;

            foreach (var hit in hits)
            {
                if (hit.isTrigger) continue;
                var dmg = hit.GetComponent<IDamageable>();
                if (dmg == null || !dmg.IsAlive) continue;

                // Do not lock onto a unit the round could not damage anyway — otherwise a homing
                // round fired at close range picks its own shooter (or an ally) as the nearest
                // IDamageable and steers back into the muzzle. Same predicate as the hit test, so
                // "will not be hit" and "will not be tracked" cannot drift apart.
                var candidate = hit.GetComponent<PFE.Entities.Units.UnitController>();
                if (candidate != null && !FactionRule.CanHitDirectly(OwnerFaction, candidate.Faction))
                    continue;

                float d = Vector2.Distance(selfPosition, hit.transform.position);
                if (d < bestDist)
                {
                    bestDist = d;
                    best     = hit.transform;
                }
            }

            if (best == null) return;

            Vector2 toTarget = ((Vector2)best.position - selfPosition).normalized;
            // AS3: rotate dx/dy toward target by navod radians per frame.
            float turnSpeed = _navod * SimClock.FramesPerSecond * dt;   // radians/sec
            float speed     = _velocity.magnitude;
            if (speed < 0.001f) return;

            float currentAngle = Mathf.Atan2(_velocity.y, _velocity.x);
            float targetAngle  = Mathf.Atan2(toTarget.y, toTarget.x);
            float newAngle     = Mathf.MoveTowardsAngle(
                currentAngle * Mathf.Rad2Deg,
                targetAngle  * Mathf.Rad2Deg,
                turnSpeed    * Mathf.Rad2Deg) * Mathf.Deg2Rad;
            _velocity = new Vector2(Mathf.Cos(newAngle), Mathf.Sin(newAngle)) * speed;
        }

        // ── Impact handling ──────────────────────────────────────────────────

        // ── Faction (friendly fire) ──────────────────────────────────────────

        /// <summary>
        /// Faction of the unit that fired this projectile — the attacker side of
        /// <see cref="FactionRule"/>, carried in from the damage context.
        ///
        /// <para>Neutral when no context was set. That is AS3's own default (<c>Unit.as:454</c>) and
        /// it means an unowned projectile hits everyone — the same behaviour the port had
        /// unconditionally before this existed, so a caller that never sets a context is not silently
        /// given a side.</para>
        /// </summary>
        private FactionType OwnerFaction =>
            _hasDamageContext ? _damageContext.OwnerFaction : FactionType.Neutral;

        /// <summary>
        /// Whether the faction rule permits a <i>direct</i> hit on <paramref name="other"/>.
        ///
        /// <para>Non-unit damageables — crates and other destructible props — always pass: AS3's
        /// bullet faction test iterates <c>loc.units</c> only (<c>weapon/Bullet.as:505</c>), and a
        /// crate has no <c>fraction</c> to compare against.</para>
        ///
        /// <para>The guided-target exemption (AS3 <c>this.targetObj</c>) is not modelled: the port's
        /// homing re-picks its target every tick without storing one, so there is no <c>targetObj</c>
        /// to test. The behaviour that actually matters — a round not locking onto, or damaging, the
        /// side that fired it — is enforced by the faction comparison itself.</para>
        /// </summary>
        private bool FactionAllowsHit(Collider2D other)
        {
            var unit = other.GetComponent<PFE.Entities.Units.UnitController>();
            if (unit == null) return true;
            return FactionRule.CanHitDirectly(OwnerFaction, unit.Faction);
        }

        /// <summary>
        /// Damage multiplier for an explosion of this projectile's faction hitting
        /// <paramref name="other"/>. AS3 <c>weapon/Bullet.as:764-786</c>; 1 for a non-unit, and 1 for
        /// a unit of a different faction.
        /// </summary>
        private float ExplosionMultiplierFor(Collider2D other)
        {
            var unit = other.GetComponent<PFE.Entities.Units.UnitController>();
            if (unit == null) return 1f;
            return FactionRule.ExplosionMultiplier(OwnerFaction, unit.Faction, unit.IsPlayer);
        }

        /// <summary>
        /// The one impact path, shared by the trigger, the chain sweep and the unit probe.
        /// </summary>
        /// <returns>
        /// <c>true</c> when the round <b>stopped</b> here. <c>false</c> when it carried on and the
        /// caller should keep sweeping: the faction gate declined it, it pierced, or the target
        /// <b>evaded</b> it. A tile always stops.
        /// </returns>
        private bool HandleImpact(Collider2D other, Vector3 impactPos)
        {
            // Faction gate, before any effect at all. AS3's bullet test (weapon/Bullet.as:515) simply
            // fails for a same-faction unit, so the bullet does not "hit with zero damage" — it does
            // not hit, plays no sound, triggers no explosion, and carries on through. Returning here
            // reproduces that pass-through rather than merely zeroing the damage.
            if (!FactionAllowsHit(other)) return false;

            if (_debugSettings?.LogProjectileLifecycle == true)
            {
                Debug.Log(
                    $"[Projectile] HandleImpact instance='{name}' impactPos={impactPos} " +
                    $"selfPos={transform.position} rbPos={(_rb != null ? (Vector3)_rb.position : transform.position)} " +
                    $"velocity={_velocity} other={DescribeCollider(other)}.");
            }

            // ── 1. IDamageable ───────────────────────────────────────────────
            var damageable = other.GetComponent<IDamageable>();
            bool evaded      = false;
            bool soundPlayed = false;

            if (damageable != null && damageable.IsAlive)
            {
                // `impactPos`, not `other.transform.position`, is the bullet's own contact point — and
                // the two are deliberately different arguments. The second is the TARGET's position and
                // becomes the floating number's anchor; the first is AS3's `param3.X/Y`, which is what
                // the blood spray throws from. Passing the target's position as the bullet position
                // would spray from the unit's boots instead of the wound. Both callers of this branch
                // hand a genuine bullet position: ResolveUnitImpact passes the sub-step contact, and
                // OnTriggerEnter2D passes the projectile's own transform.
                DamageVerdict verdict = ApplyDirectDamage(
                    damageable, other.transform.position, bulletPosition: impactPos);
                evaded = verdict == DamageVerdict.Evaded;

                // AS3 calls `sound(_loc4_)` once, unconditionally, with whatever `udarBullet` returned
                // (`weapon/Bullet.as:532`) — and a miss returns -1 (`Unit.as:4109`), which matches none
                // of that method's material cases, so an evaded hit plays nothing. Playing one would
                // announce a hit the game just decided did not happen.
                PlayImpactSound(other, impactPos, surfaceSound: !evaded);
                soundPlayed = true;

                if (_penetration > 0f)
                {
                    // ── A penetrator does not stop here ─────────────────────────────────────────────
                    // AS3 gates the entire stop path on `!(this.probiv > 0 && this.damage > 0)`
                    // (`weapon/Bullet.as:533`), so a round carrying a penetration budget damages the
                    // unit, spends budget on it, and carries on to whatever stands behind — and it is
                    // the SPEND that eventually stops it, when the budget reaches zero.
                    //
                    // What used to be here was `ProjectileRng.Chance(_piercing)` — an invented
                    // pass-through roll fed by the weapon's `@pier`, which is a flat armour figure in
                    // the 5..70 range. `Clamp01` turned every one of the 29 weapons carrying `@pier`
                    // into a 100% penetrator, and the minigun (which carries no `@pier`) into a round
                    // that could never pass through anything.
                    //
                    // The AVOIDANCE roll still happens, and that is deliberate: `udarBullet` is called
                    // at `weapon/Bullet.as:531`, BEFORE the gate at `:533`, so a penetrator is evadable
                    // exactly like any other round. What the gate removes is only the STOP — and that
                    // is why the verdict above is computed for both kinds of round and used here.
                    //
                    // The SPEND lives inside `Unit.damage()` (`Unit.as:3684-3696`), which `udarBullet`
                    // reaches only on the landed path — a miss returns -1 at `:4109` without ever calling
                    // `damage()`. So an evaded hit must cost the round NO budget; spending
                    // unconditionally drains a penetrator on the units it missed, and it then stops
                    // sooner than the oracle's would. The rule lives in `PenetrationMath.SpendOnHit` so
                    // it can be guarded outside the editor.
                    SpendPenetration(damageable.MaxHealth, landed: !evaded);

                    // Budget left: fly on. AS3 reaches this by falling out of the `if` without ever
                    // calling `popadalo` — so no explosion, no decal, no impact frames.
                    if (PenetrationMath.KeepsFlying(_penetration, _remainingDamage)) return false;

                    // Exhausted on this unit: the oracle's gate now fails, so the round takes the stop
                    // path below — but only if it LANDED. `popadalo` is guarded by `if(_loc4_ >= 0)`
                    // (`weapon/Bullet.as:535`), so an exhausted round that was evaded still flies on,
                    // which the `if (evaded)` test below takes care of.
                }
            }

            // The sound for a unit impact was played in the block above, once, with the material code
            // `udarBullet` returned. This fallback therefore covers only an impact against something
            // that is not a live `IDamageable` — a tile, or a corpse — where AS3 reaches `popadalo`
            // through the tile/box arms instead.
            if (!soundPlayed)
                PlayImpactSound(other, impactPos, surfaceSound: !evaded);

            if (evaded)
            {
                // AS3's `-1` skips `popadalo` (`weapon/Bullet.as:535-553`), and `popadalo` owns the
                // explosion, the decal and `babah` — the stop flag the sub-step loop tests. So an
                // evaded round reaches no part of the stop path: no explosion, no tile destruction, no
                // impact frames. It simply flies on. The unit is already recorded (AS3's `parr`, here
                // <c>_struckUnits</c>), so it cannot be struck twice on the way through.
                return false;
            }

            // ── 2. IDestructibleTile ─────────────────────────────────────────
            var tile = other.GetComponent<IDestructibleTile>();
            if (tile != null && _destroyTiles > 0f)
                tile.ApplyDestruction(impactPos, _destroyTiles, _damageType);

            // ── 3. AoE explosion ─────────────────────────────────────────────
            if (_explRadius > 0f && !_hasDetonated)
            {
                Detonate(impactPos);

                // The blast's visuals, as a SIBLING of Detonate rather than a line inside it. AS3 runs
                // them last within the same routine (`explRun` → `explVis`, Bullet.as:705-720), which is
                // exactly this position; keeping them out of Detonate means the damage AoE and the
                // presentation stay separable, so a change to one cannot silently move the other.
                EmitExplosionVisuals(impactPos);
            }

            // ── 4. Stop the round ────────────────────────────────────────────
            // OUTSIDE the explosion block and UNCONDITIONAL. `return true` only tells the caller to
            // stop sweeping for the rest of THIS tick; it does not stop the round. This call is what
            // does: it zeroes the velocity, disables the trigger, and either plays the impact frames or
            // hands the instance back to the pool.
            //
            // It was briefly folded into the `if` above while the explosion visuals were added, which
            // removed it from the path of every NON-explosive round — and from every tile hit, because
            // ResolveTileImpact routes a resolved collider through here too. The symptoms are exactly
            // what that predicts and all three were reported: a bullet passed through units while still
            // dealing damage (the damage is dealt above, the stop is not), then through the whole line
            // of them, then through the wall — TryTileContact's enter-only latch reads the next tick's
            // contact as "already in contact", so the round is never stopped and sails into the void.
            // It also delivers its knockback to every unit in the line instead of only the first, which
            // is why the pushback read as far stronger. Do not fold this into a branch.
            StartImpactAnimation();
            return true;
        }

        /// <summary>
        /// Both impact-sound layers for one contact. <paramref name="surfaceSound"/> is false only for
        /// an evaded hit, where AS3's material lookup finds nothing.
        /// </summary>
        private void PlayImpactSound(Collider2D other, Vector3 impactPos, bool surfaceSound)
        {
            ImpactSoundResolver.Resolve(other, _hasDamageContext, _damageContext,
                impactPos, _soundService, _impactSoundTable, surfaceSound);
        }

        /// <summary>
        /// The damage context carrying this round's <b>current</b> damage rather than its listed damage.
        /// </summary>
        /// <remarks>
        /// AS3 spends <c>Bullet.damage</c> in place as a round penetrates (<c>Unit.as:3646-3648</c> and
        /// <c>:3684-3696</c>), and that one field is both the stop condition and the damage of the next
        /// hit — so a penetrator that has already gone through a target hits the next one for less. The
        /// port's context is a readonly struct built once at fire time, so the spend is expressed as a
        /// scale through <see cref="DamageContext.WithScaledDamage"/> instead of a mutation.
        ///
        /// <para>The unscaled path is the ordinary one and returns the same struct, so a
        /// non-penetrating round is byte-for-byte unchanged.</para>
        /// </remarks>
        private DamageContext EffectiveDamageContext()
        {
            if (_damage <= 0f || _remainingDamage >= _damage)
                return _damageContext;

            return _damageContext.WithScaledDamage(_remainingDamage / _damage, 1f);
        }

        /// <summary>
        /// Spends this round's penetration budget on a unit it has just hit — AS3
        /// <c>Unit.damage():3684-3696</c>. The arithmetic, including the rule that an <i>evaded</i> hit
        /// spends nothing, lives in <see cref="PenetrationMath.SpendOnHit"/> so it can be executed
        /// outside the editor.
        /// </summary>
        /// <param name="landed"><c>false</c> when the target evaded this hit.</param>
        private void SpendPenetration(float targetMaxHealth, bool landed)
            => _remainingDamage = PenetrationMath.SpendOnHit(
                _remainingDamage, _penetration, targetMaxHealth, landed);

        /// <summary>
        /// If the visual definition has impact frames, freeze the projectile and play them.
        /// Otherwise return to pool immediately.
        /// </summary>
        private void StartImpactAnimation()
        {
            if (_currentVisual != null &&
                _currentVisual.flightFrameCount > 0 &&
                _currentVisual.frames != null &&
                _currentVisual.flightFrameCount < _currentVisual.frames.Length)
            {
                // Stop physics — projectile stays visible while impact frames play.
                _velocity          = Vector2.zero;
                _rb.linearVelocity = Vector2.zero;
                if (_triggerCollider != null) _triggerCollider.enabled = false;

                _isImpacting      = true;
                _visualFrameIndex = _currentVisual.flightFrameCount;   // first impact frame
                _visualFrameTimer = 0f;
                _visualRenderer.sprite = _currentVisual.frames[_visualFrameIndex];
            }
            else
            {
                // A tick must not deactivate a GameObject — the tickable list is being iterated while
                // this runs. With the flip active the release is deferred to LateUpdate; on the legacy
                // path (called from OnTriggerEnter2D) it is safe to do immediately.
                if (FlipActive) _pendingReturnToPool = true;
                else            ReturnToPool();
            }
        }

        /// <summary>
        /// AoE: damages all IDamageable and destroys all IDestructibleTile in radius.
        /// </summary>
        /// <remarks>
        /// <para><b>This used to be the one explosion path in the port that bypassed the resolver, and
        /// it was wrong in three ways at once.</b> Recorded in
        /// <c>docs/AUDIT_throwable_and_explosive_2026-10-03.md</c>; <b>closed 10-04</b>. The three
        /// were:</para>
        /// <list type="number">
        /// <item><description><b>No distance falloff.</b> <c>aoeHitDamage * factionMult</c> was flat to
        ///     the rim. AS3 is <c>1</c> inside <c>r/2</c> then <c>2 − 2d/r</c>, and nothing at or
        ///     beyond <c>r</c> (<c>Bullet.explGas():768-773</c>).</description></item>
        /// <item><description><b>No damage spread.</b> AS3 gives a blast <c>×0.7..1.3</c> at
        ///     <c>:763</c>.</description></item>
        /// <item><description><b>It bypassed the resolver entirely</b> — <see cref="ApplyDirectDamage"/>
        ///     with an explicit override went straight to <c>IDamageable.TakeDamage</c>, a raw HP
        ///     subtraction. So a rocket blast applied no vulnerability, no skin, no armour and no crit,
        ///     where AS3's <c>unit.damage()</c> applies all four.</description></item>
        /// </list>
        ///
        /// <para><b>The third is also why the blast drew no damage number.</b> The live overlay
        /// (<c>FloatingDamageOverlay</c>) reads <c>DamageEventFeed</c>, and only the resolver writes
        /// that feed. A grenade launcher is a <c>&lt;vis phisbul='1'&gt;</c> weapon, so its round is a
        /// <see cref="Projectile"/> and its blast took the bypass — while a thrown grenade
        /// (<c>ThrownObject.Detonate</c>) and a mine (<c>MineObject</c>) already reported through
        /// <c>PendingDamage.Explosion</c> and therefore drew their numbers normally. That asymmetry is
        /// exactly the reported symptom, and routing here through the same factory is the fix: all
        /// three explosion paths now make the same call.</para>
        ///
        /// <para><b>The <c>_damage</c> fallback is gone with it.</b> AS3's two blast shapes
        /// (<c>explGas</c>, <c>explBlast</c>) both read <c>damageExpl</c> and nothing else, so a round
        /// that carries a radius and no blast damage deals <i>nothing</i> — it does not fall back to
        /// the direct-hit damage. That fallback was a port invention, and it is dead on the shipped
        /// data: of the 213 weapons, 44 carry <c>expl &gt; 0</c> and every one of those also carries
        /// <c>damexpl &gt; 0</c>, and no weapon has <c>damexpl &gt; 0</c> without <c>expl &gt; 0</c>.
        /// So dropping it changes no shipped weapon's numbers; it only stops the port inventing
        /// one.</para>
        ///
        /// <para><b>Known gap — a projectile fires one pulse, even when its weapon carries a train.</b>
        /// <c>explTip</c>/<c>explKol</c> are imported (<see cref="WeaponDefinition.explKol"/>) and
        /// <c>ThrownObject</c> runs the full train, but a round cannot: <see cref="HandleImpact"/> calls
        /// this and then <see cref="StartImpactAnimation"/>, which returns the instance to the pool in
        /// the same call stack, so there is no object left to fire pulse 1. Four weapons are affected —
        /// <c>zombivenom</c>, <c>zombiacid</c>, <c>zombipink</c> and <c>robogas</c>, all
        /// <c>tip='0'</c> monster weapons with <c>explkol='12'</c>. Fixing it needs the round to survive
        /// its own detonation (or a detached blast entity), which is its own workstream; recorded in
        /// <c>TOPIC_open_work.md</c>. Every weapon the owner reported is <i>thrown</i>, so this gap does
        /// not affect any of them.</para>
        /// </remarks>
        private void Detonate(Vector3 centre)
        {
            _hasDetonated = true;

            Collider2D[] hits = Physics2D.OverlapCircleAll(centre, _explRadius);
            foreach (var hit in hits)
            {
                if (hit.isTrigger) continue;

                var damageable = hit.GetComponent<IDamageable>();
                if (damageable != null && damageable.IsAlive)
                {
                    if (_hasDamageContext && _damageSystem != null)
                    {
                        // Report, do not resolve — the identical call a thrown grenade and a mine make,
                        // so the three explosion paths finally agree. The per-target faction multiplier
                        // is computed here because it needs this collider; the falloff, the spread and
                        // the vulnerability/armour/crit terms all live in DamageSystem.
                        //
                        // AS3 Bullet.explGas (weapon/Bullet.as:763-789) scales the blast per target —
                        // ×0.25 when the target shares the firer's fraction (and is not the player),
                        // then ×pers.autoExpl when the firer is the player and the target is the
                        // player. The second gate multiplies *after* the first and the first excludes
                        // F_PLAYER entirely, so the player's own explosion is full damage by default
                        // (pers.autoExpl defaults to 1) — the oracle's behaviour, not a bug.
                        _damageSystem.Report(PendingDamage.Explosion(
                            _damageContext, damageable,
                            hit.transform.position, centre, _explRadius,
                            factionMultiplier: ExplosionMultiplierFor(hit)));
                    }
                    else
                    {
                        // No damage context (the low-level factory overload) or no resolver injected.
                        // Still reads _explDamage, never _damage — see the remark above. _explDamage and
                        // _damageContext.ExplosionDamage are the same weapon attribute
                        // (ProjectileFactory passes weapon.explosionDamage to both), so the two branches
                        // cannot disagree about the number, only about how it is reduced.
                        ApplyDirectDamage(damageable, hit.transform.position,
                                          _explDamage * ExplosionMultiplierFor(hit));
                    }
                }

                var tile = hit.GetComponent<IDestructibleTile>();
                if (tile != null)
                    tile.ApplyDestructionRadius(centre, _explRadius, _destroyTiles, _damageType);
            }
        }

        /// <summary>
        /// The visuals an explosion throws — the port of AS3 <c>Bullet.explVis()</c>
        /// (<c>weapon/Bullet.as:876-1005</c>). This is the <i>thin</i> half: it converts the impact
        /// point, asks the tile query whether the blast landed in water, and forwards what
        /// <see cref="PFE.Systems.Particles.ExplosionVisualRules"/> decides. The decision table itself
        /// lives there, Unity-free, so all nine damage-type arms and the per-weapon override are pinned
        /// by fixtures rather than being reachable only through a private member of a MonoBehaviour.
        ///
        /// <para><b>Reached only when the round has a blast radius.</b> That is the call site's gate, and
        /// it is the oracle's: <c>explRun</c> is reached from <c>popadalo</c>, which AS3 gates on
        /// <c>if (this.explRadius)</c> (<c>Bullet.as:331</c>), and from the lifetime-expiry path, which
        /// gates the same way (<c>:252</c>). So a plain kinetic round never gets here — and when one did,
        /// the type table would give it nothing anyway.</para>
        ///
        /// <para><b><c>inWater</c> is derived here, not stored.</b> AS3's <c>Bullet.inWater</c> is a flag
        /// the bullet refreshes every tick from <c>loc.getAbsTile(X,Y).water &gt; 0</c>
        /// (<c>Bullet.as:433-474</c>) and reads at the moment of the blast. A ported projectile has no
        /// such flag, so the same tile query is asked once, at the impact point — the value the oracle
        /// would have been holding. It is asked in AS3 room-local pixels, the space
        /// <see cref="PFE.Systems.Particles.IParticleTileWater.WaterAt"/> documents, so the conversion
        /// comes from the emitter adapter rather than being repeated here.</para>
        ///
        /// <para><b>Position converted once.</b> <c>EmitAt</c> takes AS3 room-local pixels, which is
        /// exactly what <see cref="PFE.Systems.Particles.Adapters.RoomParticleEmitter.TryToAs3Local"/>
        /// already produced, so no further conversion happens here — only the per-emit offsets the
        /// table carries. Converting per emit is how the mirror gets applied twice.</para>
        /// </summary>
        private void EmitExplosionVisuals(Vector3 centre)
        {
            if (_particles == null) return;

            // No room pushed ⇒ no origin and no height ⇒ no correct position. Refusing is the whole
            // point of the adapter's contract: emitting anyway would place the blast somewhere
            // plausible and wrong, which is the failure this seam exists to remove.
            if (!_particles.TryToAs3Local(centre, out Vector2 as3Local)) return;

            int water = _particleTileWater != null
                ? _particleTileWater.WaterAt(as3Local.x, as3Local.y)
                : 0;

            if (!PFE.Systems.Particles.ExplosionVisualRules.Plan(
                    _visExpl, _damageType, water > 0,
                    // Always the initial pulse — see the class note on the projectile pulse-train gap:
                    // a round that has detonated is released by StartImpactAnimation in the same call
                    // stack, so it cannot host the sustained train that would make a later pulse
                    // possible. `explTip`/`explKol` are still imported and carried on the definition;
                    // this is the one place they are not yet consumed.
                    isInitialPulse: true,
                    PresentationRng(), _explosionEmits,
                    out string soundId))
            {
                return;
            }

            foreach (PFE.Systems.Particles.ParticleEmit emit in _explosionEmits)
            {
                _particles.EmitAt(emit.Id,
                    new Vector2(as3Local.x + emit.OffsetX, as3Local.y + emit.OffsetY),
                    emit.Spec);
            }

            if (soundId != null) _soundService?.Play(soundId, centre);
        }

        /// <summary>
        /// The unseeded presentation stream, resolved once — the only jitter these visuals need is the
        /// acid arm's <c>kol</c>. Null when nothing injected an <see cref="PFE.Core.Rng.IRngService"/>
        /// (an offline fixture built with <c>new</c>), which the rules treat as "use the oracle's
        /// minimum" rather than an error.
        /// </summary>
        private PFE.Core.Rng.IRngService PresentationRng()
        {
            if (_presentationRng == null && _rng != null)
                _presentationRng = _rng.GetStream(PFE.Core.Rng.RngStream.Presentation);
            return _presentationRng;
        }

        /// <summary>
        /// Hands one hit to the damage pipeline and reports what became of it.
        ///
        /// <para><b>The return is the whole point.</b> AS3's bullet reads its fate off
        /// <c>udarBullet</c>'s return (<c>weapon/Bullet.as:535</c>) and only stops when it is
        /// <c>&gt;= 0</c>. The port resolves damage in <see cref="PFE.Systems.Combat.DamageSystem"/>
        /// instead, so this is the one place that can carry that answer back to the projectile — and
        /// it carries it rather than recomputing it, so the avoidance roll still happens exactly once.
        /// See <see cref="PFE.Systems.Combat.DamageVerdict"/>.</para>
        /// </summary>
        /// <param name="bulletPosition">
        /// This round's own contact point, when the hit came off a bullet — AS3's <c>param3.X/Y</c>,
        /// which the blood spray anchors on. It is deliberately separate from
        /// <paramref name="targetPos"/>: that one is the <i>target's</i> position and feeds the
        /// floating number, and for a unit the two differ by the target's half-height. Null means "no
        /// bullet object", which is the truth for the AoE override below.
        /// </param>
        private DamageVerdict ApplyDirectDamage(IDamageable target, Vector3 targetPos,
                                                float overrideDamage = -1f,
                                                Vector3? bulletPosition = null)
        {
            if (_hasDamageContext && overrideDamage < 0f && _damageSystem != null)
            {
                // Report, do not resolve. DamageSystem owns the formula, and owns the tick that runs
                // it when PfeDebugSettings.SimTickDamage is on. A source that resolved its own hit
                // would make the result depend on the order the physics engine reported contacts.
                //
                // The travel distance goes with it: it is the one input the hit-avoidance roll needs
                // that is a property of this hit rather than of the shot (AS3 Bullet.dist). Melee and
                // hitscan pass nothing, because their branch of the oracle has no distance term.
                //
                // EffectiveDamageContext, not _damageContext: a penetrator that has already passed
                // through one target hits the next one for less — AS3 spends `Bullet.damage` in place.
                return _damageSystem.Report(PendingDamage.Direct(
                    EffectiveDamageContext(), target, targetPos, _traveledDistancePixels,
                    bulletPosition));
            }

            if (_hasDamageContext && overrideDamage < 0f)
            {
                // Injection failed. Loud on purpose: falling through silently would drop armour and
                // crit from every hit and look like a balance change rather than a wiring fault.
                Debug.LogWarning(
                    "[Projectile] DamageSystem was never injected; applying raw damage with no " +
                    "armour, crit or durability terms.");
            }

            float finalDamage = overrideDamage >= 0f ? overrideDamage : _remainingDamage;
            target.TakeDamage(finalDamage);

            _damageDealtPublisher?.Publish(new DamageDealtMessage
            {
                damage    = finalDamage,
                position  = targetPos,
                isCritical = false,
                isMiss    = false
            });

            // Either an explicit override (the AoE path, which AS3 resolves with a direct
            // `unit.damage()` and never rolls avoidance at all — see PendingDamage.Explosion) or the
            // degraded no-injection path. Both applied damage, so the round is spent.
            return DamageVerdict.Landed;
        }

        // ── Pool support ─────────────────────────────────────────────────────

        private void ReturnToPool()
        {
            if (_debugSettings?.LogProjectileLifecycle == true)
                Debug.Log($"[Projectile] ReturnToPool instance='{name}' pos={transform.position} initialized={_isInitialized}.");
            if (OnReturnToPool != null) OnReturnToPool(this);
            else Destroy(gameObject);
        }

        public void ResetProjectile()
        {
            if (_debugSettings?.LogProjectileLifecycle == true)
                Debug.Log($"[Projectile] ResetProjectile instance='{name}'.");
            _isInitialized    = false;
            _hasDetonated     = false;
            _hasDamageContext = false;
            _isImpacting      = false;

            // Stage C: a pooled instance must not stay registered after release. SimLoop.Register
            // de-duplicates, so leaving it registered would not double-register — it would keep the
            // released instance ticked, which is worse: a parked projectile at the pool position
            // would still integrate and could report tile contacts from inside the parking corner.
            UnregisterFromSimLoop();
            _viewDirty           = false;
            _pendingReturnToPool = false;
            _tileContactActive   = false;
            // AS3's `parr` dies with the bullet because AS3 allocates a new one per shot. This instance
            // is pooled, so the equivalent list has to be cleared explicitly or a recycled bullet
            // inherits the previous shot's victims and flies through them.
            _struckUnits.Clear();
            _lastContactNormal   = Vector2.zero;
            _simPosition         = Vector2.zero;

            if (_triggerCollider != null) _triggerCollider.enabled = true;
            _lifetimeTimer    = DefaultLifetime;
            _damage = _destroyTiles = _explRadius = _explDamage = _penetration = _remainingDamage = 0f;
            _velocity = Vector2.zero;
            _ddx      = 0f;
            _ddy      = 0f;
            _navod    = 0f;
            _damageType    = DamageType.PhysicalBullet;
            // A recycled bullet must not inherit the previous shot's weapon override, or a sparkle
            // rocket would make the next plain grenade emit sparkle visuals.
            _visExpl       = null;
            _currentVisual = null;
            _visualFrameTimer = 0f;
            _visualFrameIndex = 0;
            _spawnPosition = Vector3.zero;
            _spawnRotation = Quaternion.identity;

            if (_rb != null)
            {
                _rb.linearVelocity  = Vector2.zero;
                _rb.angularVelocity = 0f;
                _rb.bodyType        = RigidbodyType2D.Kinematic;
                _rb.gravityScale    = 0f;
                _rb.rotation        = 0f;
                // Do NOT set simulated=false — SetActive(false) already removes the body
                // from Box2D. Toggling simulated explicitly corrupts Box2D's position
                // tracking and causes phantom CCD sweeps on the next spawn.
            }

            // Park the transform so that if the object is somehow activated without
            // PrepareForSpawn it appears far from the map, not at scene origin.
            transform.position = new Vector3(PoolParkingPosition.x, PoolParkingPosition.y, 0f);
            transform.rotation = Quaternion.identity;
            RestoreDefaultVisual();
        }

        // ── Debug ────────────────────────────────────────────────────────────

        private void OnDrawGizmos()
        {
            if (_rb == null || !_isInitialized) return;
            Gizmos.color = Color.yellow;
            Gizmos.DrawRay(transform.position, _velocity.normalized * 2f);
            if (_explRadius > 0f)
            {
                Gizmos.color = new Color(1f, 0.4f, 0f, 0.3f);
                Gizmos.DrawWireSphere(transform.position, _explRadius);
            }
            if (_navod > 0f)
            {
                Gizmos.color = new Color(0f, 1f, 1f, 0.2f);
                Gizmos.DrawWireSphere(transform.position, 20f);
            }
        }

        // ── Visual helpers ───────────────────────────────────────────────────

        private void EnsureVisualRenderer()
        {
            if (_visualRenderer != null)
            {
                if (_visualTransform == null)
                    _visualTransform = _visualRenderer.transform;
                return;
            }
            _visualRenderer  = GetComponentInChildren<SpriteRenderer>();
            _visualTransform = _visualRenderer != null ? _visualRenderer.transform : null;

            // AS3's beam is a CHILD of the bullet view (`vis.laser`, `Bullet.as:213`), so the port
            // looks for the same child by name. Absent is the normal state today: no prefab in the
            // project has one, which is exactly why `spring='2'` weapons draw an ordinary round. The
            // lookup is by name rather than a serialized field so an art change activates the stretch
            // without a code change — but a serialized override still wins, for prefabs that name it
            // differently.
            if (_beamTransform == null && _visualTransform != null)
            {
                _beamTransform = _visualTransform.Find(BeamChildName);
            }
        }

        /// <summary>
        /// The name of the beam child AS3 stretches — <c>vis.laser</c> (<c>Bullet.as:213</c>).
        /// </summary>
        public const string BeamChildName = "laser";

        private void CacheVisualDefaults()
        {
            if (_visualDefaultsCached) return;
            EnsureVisualRenderer();
            if (_visualRenderer == null) return;

            _defaultSprite          = _visualRenderer.sprite;
            _defaultColor           = _visualRenderer.color;
            _defaultSortingOrder    = _visualRenderer.sortingOrder;
            _defaultRendererEnabled = _visualRenderer.enabled;

            if (_visualTransform != null)
            {
                _defaultLocalPosition = _visualTransform.localPosition;
                _defaultLocalRotation = _visualTransform.localRotation;
                _defaultLocalScale    = _visualTransform.localScale;
            }

            _visualDefaultsCached = true;
        }

        private void RestoreDefaultVisual()
        {
            CacheVisualDefaults();
            if (!_visualDefaultsCached || _visualRenderer == null) return;

            _visualRenderer.enabled      = _defaultRendererEnabled;
            _visualRenderer.sprite       = _defaultSprite;
            _visualRenderer.color        = _defaultColor;
            _visualRenderer.sortingOrder = _defaultSortingOrder;

            if (_visualTransform != null)
            {
                _visualTransform.localPosition = _defaultLocalPosition;
                _visualTransform.localRotation = _defaultLocalRotation;
                _visualTransform.localScale    = _defaultLocalScale;
            }
        }

        private void UpdateVisualAnimation()
        {
            if (_currentVisual == null || _visualRenderer == null ||
                _currentVisual.frames == null || _currentVisual.frameRate <= 0f)
                return;

            var    frames        = _currentVisual.frames;
            int    flightCount   = _currentVisual.flightFrameCount;
            bool   splitAnim     = flightCount > 0 && flightCount < frames.Length;

            _visualFrameTimer += Time.deltaTime;
            float frameDuration = 1f / _currentVisual.frameRate;

            while (_visualFrameTimer >= frameDuration)
            {
                _visualFrameTimer -= frameDuration;

                if (_isImpacting)
                {
                    // ── Impact frames: play once then return to pool ───────────
                    int lastImpact = frames.Length - 1;
                    if (_visualFrameIndex < lastImpact)
                    {
                        _visualFrameIndex++;
                        _visualRenderer.sprite = frames[_visualFrameIndex];
                    }
                    else
                    {
                        // Last impact frame shown — done.
                        ReturnToPool();
                        return;
                    }
                }
                else
                {
                    // ── Flight frames: loop within [0 .. flightCount-1] ───────
                    int rangeEnd = splitAnim ? flightCount - 1 : frames.Length - 1;
                    if (rangeEnd <= 0) break;   // single flight frame, nothing to advance

                    if (_visualFrameIndex < rangeEnd)
                        _visualFrameIndex++;
                    else if (_currentVisual.loop)
                        _visualFrameIndex = 0;

                    _visualRenderer.sprite = frames[_visualFrameIndex];
                }
            }
        }

        private static string DescribeCollider(Collider2D collider)
        {
            if (collider == null) return "null";

            var go = collider.gameObject;
            var attachedBody = collider.attachedRigidbody;

            var sb = new StringBuilder(192);
            sb.Append("name='").Append(go.name).Append('\'');
            sb.Append(" path='").Append(GetHierarchyPath(go.transform)).Append('\'');
            sb.Append(" type=").Append(collider.GetType().Name);
            sb.Append(" layer=").Append(LayerMask.LayerToName(go.layer)).Append('(').Append(go.layer).Append(')');
            sb.Append(" tag='").Append(go.tag).Append('\'');
            sb.Append(" isTrigger=").Append(collider.isTrigger);
            sb.Append(" pos=").Append(go.transform.position);

            if (attachedBody != null)
            {
                sb.Append(" bodyType=").Append(attachedBody.bodyType);
                sb.Append(" rbPos=").Append(attachedBody.position);
                sb.Append(" simulated=").Append(attachedBody.simulated);
            }

            return sb.ToString();
        }

        private static string GetHierarchyPath(Transform transform)
        {
            if (transform == null) return string.Empty;

            var sb = new StringBuilder(transform.name);
            var current = transform.parent;

            while (current != null)
            {
                sb.Insert(0, '/');
                sb.Insert(0, current.name);
                current = current.parent;
            }

            return sb.ToString();
        }
    }
}
