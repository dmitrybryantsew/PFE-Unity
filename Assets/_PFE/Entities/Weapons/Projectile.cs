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
using System.Text;
using PFE.Core.Rng;
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
    ///   piercing     > 0  → on hit roll: if pass, bullet continues (probiv in AS3)
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

        // ── Runtime state ────────────────────────────────────────────────────

        private bool          _hasDamageContext;
        private DamageContext _damageContext;

        private float      _damage;
        private float      _destroyTiles;
        private float      _explRadius;
        private float      _explDamage;
        private DamageType _damageType;
        private float      _lifetimeTimer;
        private float      _piercing;       // probiv: chance 0–1 to pass through on hit
        private static IRngService s_projectileRng;
        private static IRngService ProjectileRng => s_projectileRng ??= new PcgRngService().GetStream(RngStream.Combat);

        // ── Manual velocity integration (mirrors AS3 dx/dy/ddx/ddy) ──────────

        private Vector2 _velocity;          // current velocity (unity units/s)
        private float   _ddx;               // per-second X acceleration (accel along aim)
        private float   _ddy;               // per-second Y acceleration (gravity / flame lift)
        private float   _navod;             // homing strength (0=no homing)

        // Scaled to Unity units from AS3 pixel values where needed by caller.
        private const float FlashFps        = 30f;
        private const float DefaultLifetime = 30f;
        private const float FlameLifetime1  = 0.7f;   // flame==1 short lifetime (AS3 ~21 frames)
        private const float FlameLifetime2  = 1.2f;   // flame==2 medium lifetime
        private static readonly Vector2 PoolParkingPosition = new(10000f, 10000f);

        private Rigidbody2D   _rb;
        private Collider2D    _triggerCollider;   // cached trigger — disabled during impact anim
        private bool          _isInitialized;
        private bool          _hasDetonated;
        private bool          _isImpacting;       // true while playing impact frames before pool

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
        [Inject] private PFE.Core.PfeDebugSettings      _debugSettings;
        [Inject] private ISoundService                  _soundService;
        [Inject] private ImpactSoundTable               _impactSoundTable;

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

            if (TryTileContact(fromPx, deltaPx, out Vector2 contactPx))
            {
                // contactPx comes back in world pixels; the sim's own position is in units.
                _simPosition = contactPx * TileQueryConstants.PixelToUnit;
                _viewDirty   = true;
                ResolveTileImpact(contactPx);
                return;
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
        ///   piercing     → probiv chance 0–1
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
                               float piercing     = 0f)
        {
            _damage       = damage;
            _destroyTiles = destroyTiles;
            _explRadius   = explRadius;
            _explDamage   = explDamage;
            _damageType   = damageType;
            _piercing     = Mathf.Clamp01(piercing);
            _navod        = navod;
            _isInitialized = true;
            _hasDetonated  = false;

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

            // ── Per-second acceleration ───────────────────────────────────────
            // AS3 `Weapon.as:1524` zeroes `b.ddx`/`b.ddy`, then 1536-1537 (thrust), 1545/1551
            // (flame lift) and 1558 (gravity) each `+=` into them. All three are px/frame²
            // accelerations and they ACCUMULATE — assigning instead of adding silently drops a
            // term (a flame weapon would lose gravity).
            //
            // The conversion lives in ProjectilePhysicsMath, which is a pure function so the numbers
            // can be pinned by tests. Do not reintroduce a local `* FlashFps` here: that is the
            // per-frame VELOCITY idiom, 3.33× too strong for a per-frame² acceleration.
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
        /// When set, impact damage is resolved via DamageResolver instead of raw _damage float.
        /// Call after Initialize().
        /// </summary>
        public void SetDamageContext(DamageContext ctx)
        {
            _damageContext    = ctx;
            _hasDamageContext = true;

            if (_debugSettings?.LogProjectileLifecycle == true)
            {
                Debug.Log(
                    $"[Projectile] SetDamageContext instance='{name}' weapon='{ctx.Weapon?.weaponId ?? "null"}' " +
                    $"baseDamage={ctx.BaseDamage} damageType={ctx.DamageType}.");
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
        }

        private void OnTriggerEnter2D(Collider2D other)
        {
            if (!_isInitialized) return;

            // A tile collider only stops a bullet when the tile is a WALL.
            //
            // AS3's bullet has exactly one tile test and it reads `phis` alone — `_loc3_.phis == 1 ||
            // _loc3_.phis == 2` against the cell the bullet occupies (weapon/Bullet.as:476). `shelf`,
            // `diagon` and `stair` are never consulted, and every one of those forms carries
            // `phis = 0`. In the port `phis` 1 and 2 both decode to TilePhysicsType.Wall, so `Wall`
            // is the whole rule. A catwalk decodes to Platform and a ladder to Stair, and BOTH get a
            // real collider from TileCollider — so the legacy path used to stop bullets in mid-air on
            // every catwalk and ladder. (Slopes decode to Air, so they were already passing.)
            //
            // This must stay the same rule as RoomChainGeometry.IsSolidAt, which is the flipped
            // path's half of it; the two are the only places a bullet asks "is this tile in my way".
            TileCollider tileCollider = other.GetComponent<TileCollider>();
            if (tileCollider != null)
            {
                // With the flip running, tile contacts are the chain sweep's job. Without this the
                // per-tile collider would report the same surface and every wall hit would be
                // resolved twice — two impact sounds, two damage rolls, two destruction calls, two
                // AoE detonations.
                if (FlipActive) return;

                TileData tile = tileCollider.GetTileData();
                if (tile != null && tile.physicsType != TilePhysicsType.Wall) return;
            }
            // Entity hits are deliberately NOT filtered: enemies, the player and destructible props
            // are still Unity colliders in both modes.

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

                // Skip the owner if we ever track it — for now skip same-layer objects.
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
            float turnSpeed = _navod * FlashFps * dt;   // radians/sec
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

        private void HandleImpact(Collider2D other, Vector3 impactPos)
        {
            if (_debugSettings?.LogProjectileLifecycle == true)
            {
                Debug.Log(
                    $"[Projectile] HandleImpact instance='{name}' impactPos={impactPos} " +
                    $"selfPos={transform.position} rbPos={(_rb != null ? (Vector3)_rb.position : transform.position)} " +
                    $"velocity={_velocity} other={DescribeCollider(other)}.");
            }

            // ── Impact sound (weapon hit + surface material) ──────────────────
            ImpactSoundResolver.Resolve(other, _hasDamageContext, _damageContext,
                impactPos, _soundService, _impactSoundTable);

            // ── 1. IDamageable ───────────────────────────────────────────────
            var damageable = other.GetComponent<IDamageable>();
            if (damageable != null && damageable.IsAlive)
            {
                // Penetration (probiv): roll against piercing chance before applying damage.
                // If bullet passes through, do not stop — continue moving.
                if (_piercing > 0f && ProjectileRng.Chance(_piercing))
                {
                    // Graze: apply damage but don't stop.
                    ApplyDirectDamage(damageable, other.transform.position);
                    // No ReturnToPool — bullet continues.
                    return;
                }

                ApplyDirectDamage(damageable, other.transform.position);
            }

            // ── 2. IDestructibleTile ─────────────────────────────────────────
            var tile = other.GetComponent<IDestructibleTile>();
            if (tile != null && _destroyTiles > 0f)
                tile.ApplyDestruction(impactPos, _destroyTiles, _damageType);

            // ── 3. AoE explosion ─────────────────────────────────────────────
            if (_explRadius > 0f && !_hasDetonated)
                Detonate(impactPos);

            StartImpactAnimation();
        }

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
        private void Detonate(Vector3 centre)
        {
            _hasDetonated = true;

            float aoeHitDamage = _explDamage > 0f ? _explDamage : _damage;

            Collider2D[] hits = Physics2D.OverlapCircleAll(centre, _explRadius);
            foreach (var hit in hits)
            {
                if (hit.isTrigger) continue;

                var damageable = hit.GetComponent<IDamageable>();
                if (damageable != null && damageable.IsAlive)
                    ApplyDirectDamage(damageable, hit.transform.position, aoeHitDamage);

                var tile = hit.GetComponent<IDestructibleTile>();
                if (tile != null)
                    tile.ApplyDestructionRadius(centre, _explRadius, _destroyTiles, _damageType);
            }
        }

        private void ApplyDirectDamage(IDamageable target, Vector3 targetPos,
                                        float overrideDamage = -1f)
        {
            if (_hasDamageContext && overrideDamage < 0f)
            {
                DamageResolver.Resolve(_damageContext, target, targetPos, _damageDealtPublisher);
                return;
            }

            float finalDamage = overrideDamage >= 0f ? overrideDamage : _damage;
            target.TakeDamage(finalDamage);

            _damageDealtPublisher?.Publish(new DamageDealtMessage
            {
                damage    = finalDamage,
                position  = targetPos,
                isCritical = false,
                isMiss    = false
            });
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
            _lastContactNormal   = Vector2.zero;
            _simPosition         = Vector2.zero;

            if (_triggerCollider != null) _triggerCollider.enabled = true;
            _lifetimeTimer    = DefaultLifetime;
            _damage = _destroyTiles = _explRadius = _explDamage = _piercing = 0f;
            _velocity = Vector2.zero;
            _ddx      = 0f;
            _ddy      = 0f;
            _navod    = 0f;
            _damageType    = DamageType.PhysicalBullet;
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
        }

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
