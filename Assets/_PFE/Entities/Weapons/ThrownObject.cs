using UnityEngine;
using VContainer;
using PFE.Core;
using PFE.Data.Definitions;
using PFE.Systems.Combat;
using PFE.Systems.Weapons;
using PFE.Core.Messages;
using PFE.Systems.Map.Rendering;
using PFE.Systems.Map.TileQuery;
using MessagePipe;
using System;

namespace PFE.Entities.Weapons
{
    /// <summary>
    /// Physics-based thrown projectile — grenades, bottles, sticky bombs.
    /// Mirrors <c>PhisBullet.as</c> from AS3.
    ///
    /// <para><b>Stage C — two tile-collision paths, selected by
    /// <c>PfeDebugSettings.ThrownObjectsUseTileSeam</c>.</b> With the flag OFF (default) nothing here
    /// changed: tile hits arrive through <see cref="OnTriggerEnter2D"/> from Unity's per-tile
    /// <c>BoxCollider2D</c> grid, and the object integrates in <see cref="FixedUpdate"/> against
    /// <c>Time.fixedDeltaTime</c> — 50 Hz where AS3 runs at 30, so every bounce, settle and fuse
    /// runs 1.67× fast. With it ON, the integration and the tile decision both come from
    /// <see cref="ThrownObjectPhysics"/>, driven from <see cref="SimLoop"/> at exactly
    /// <c>SimClock.SimDt</c>.</para>
    ///
    /// <para><b>Why the flipped path is a point sweep and not the chain mirror.</b> AS3's thrown
    /// object has no collision shape: <c>PhisBullet.run()</c> moves a <b>point</b> and asks
    /// <c>loc.getAbsTile(X, Y)</c> whether the cell it landed in is <c>phis == 1</c>. Tunnelling is
    /// prevented by sub-stepping at <c>World.maxdelta</c> (9 px), not by a swept shape. The chain
    /// mirror exists to sweep the projectile prefab's 93×6 needle hitbox — a shape a grenade does not
    /// have — so sweeping one here would change the game, not just the implementation. What the flip
    /// takes from the same seam is the room's own <see cref="ITileQueryService"/>, so "is this tile in
    /// my way" still resolves through one definition of tile semantics.</para>
    ///
    /// <para><b>What the flip deliberately does NOT change.</b> Entity hits and the explosion.
    /// Enemies, the player and destructible props are still Unity colliders, so <c>IDamageable</c>
    /// resolution keeps coming from <see cref="OnTriggerEnter2D"/> and the AoE keeps coming from
    /// <see cref="Physics2D.OverlapCircleAll"/>, in both modes. Moving entities into the Box2D world
    /// is not Stage C's job.</para>
    ///
    /// <para><b>The legacy path's bounce arithmetic is left alone on purpose.</b> It derives the
    /// bounce axis from <c>|diff.x| &gt; |diff.y|</c> against the collider's centre, which is a port
    /// invention and misfires near tile corners. Rewriting it is a separate, independently
    /// play-testable change; folding it into this flip would make a rollback diff unable to say which
    /// change moved the behaviour. Recorded as a known gap — and Stage D deletes the path anyway.</para>
    /// </summary>
    [RequireComponent(typeof(Rigidbody2D), typeof(Collider2D))]
    public sealed class ThrownObject : MonoBehaviour, ISimTickable
    {
        /// <summary>
        /// Gravity as a per-second acceleration, in Unity units/s² — the <b>legacy</b> path's form.
        ///
        /// <para>AS3 <c>WThrow.as:240</c> <c>trasser.ddy = World.ddy</c>, and <c>PhisBullet.as:34</c>
        /// sets <c>ddy = World.ddy</c> in the constructor, so the value is exactly <c>World.ddy</c> =
        /// 1 px/frame² (<c>World.as:46</c>) — not the 0.6 the old literal assumed, which made this 2×
        /// too strong. Converted by the canonical px/frame² → units/s² factor; see
        /// <c>REPLICA_BEHAVIOR_CONTRACT.md</c> §6.</para>
        ///
        /// <para>The flipped path does not use this: <see cref="ThrownObjectPhysics"/> works in
        /// AS3's own units and adds <c>World.ddy</c> directly. Two representations, one value.</para>
        /// </summary>
        private const float GravityPerSec = TileQueryConstants.GravityUnitsPerSecondSquared;

        // ── Physics params (set from ShotPlan at spawn) ───────────────────────

        /// <summary>Legacy-path velocity, Unity units/s. Also the source of the flipped path's view.</summary>
        private Vector2 _velocity;

        // Bounce retention and floor damping. AS3's effective values for a thrown object come from
        // WThrow's own fields, which OVERRIDE the bullet class's defaults (PhisBullet.as:23/25 say
        // 0.5/0.7, but WThrow.as:188/189 assign 0.4/0.6). See ProjectilePhysicsMath for citations.
        private float   _skok      = ProjectilePhysicsMath.ThrowBounceRetention;
        private float   _tormoz    = ProjectilePhysicsMath.ThrowFloorDamping;

        /// <summary>
        /// Sliding friction while resting, in units/s² — the <b>legacy</b> path's form.
        ///
        /// <para>AS3 <c>PhisBullet.as:63/67</c> applies <c>dx -= brake</c> once per 30 Hz frame, so
        /// <c>brake</c> (2 px/frame, <c>WThrow.as:20</c>) is a px/frame² acceleration and converts by
        /// the squared-frame-rate factor → 18 units/s². The old <c>brake / SimClock.FramesPerSecond * 60f</c> (= 4)
        /// was neither idiom and left friction 4.5× too weak.</para>
        /// </summary>
        private float   _brake     = ProjectilePhysicsMath.SlidingFriction(ProjectilePhysicsMath.BrakePxPerFrame2);

        /// <summary>The same <c>brake</c>, unconverted, for <see cref="ThrownObjectPhysics"/>.</summary>
        private float   _brakePxPerFrame2 = ProjectilePhysicsMath.BrakePxPerFrame2;

        private bool    _bumc;               // detonate on contact
        private bool    _stay;               // resting on floor

        // ── Fuse ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Legacy fuse, in <b>seconds of wall clock</b>. Deliberately unchanged by the flip: the
        /// legacy path's defect is that its clock is wrong, and a wall-clock fuse reproduces the
        /// pre-flip duration exactly, so a flag-off/flag-on comparison isolates the clock change
        /// instead of confounding it with a fuse change.
        /// </summary>
        private float _fuseTimer;

        /// <summary>
        /// Flipped fuse, in <b>canonical 30 Hz frames</b>. AS3 <c>liv</c>
        /// (<c>PhisBullet.as:108-124</c>) counts down from <c>detTime</c> and fires the explosion when
        /// it reaches <b>3</b>, not 0 — so a 75-frame fuse burns for 72 frames, i.e. 2.4 s. Both
        /// values start from the same <c>fuseFrames</c> argument; only the unit differs.
        /// </summary>
        private int  _fuseTicks;

        private bool  _armed;

        // ── Damage ────────────────────────────────────────────────────────────

        private bool          _hasDamageContext;
        private DamageContext _damageContext;
        private float         _explRadius;

        // ── Faction (friendly fire) ───────────────────────────────────────────

        /// <summary>
        /// Faction of the unit that threw this object — the attacker side of <see cref="FactionRule"/>,
        /// carried in through the damage context. Neutral when no context was set, which is AS3's own
        /// default (<c>Unit.as:454</c>) and means an unowned throwable still hits everyone.
        /// </summary>
        private FactionType OwnerFaction =>
            _hasDamageContext ? _damageContext.OwnerFaction : FactionType.Neutral;

        /// <summary>
        /// Whether this object may <i>detonate on contact</i> with <paramref name="other"/>.
        ///
        /// <para>AS3 <c>PhisBullet.sensor()</c> (<c>weapon/PhisBullet.as:151</c>) fires the explosion
        /// only when <c>_loc1_.fraction != owner.fraction</c> — a same-faction unit is not a contact,
        /// so the object keeps flying through it. The port's trigger path already treats "damageable
        /// but not a contact" as a no-op (<c>return</c> without bouncing), so this is the same shape
        /// with the faction test added; the object neither detonates nor bounces off an ally.</para>
        ///
        /// <para>Non-units always pass, matching the direct-hit rule: AS3's sensor iterates
        /// <c>loc.units</c>, so a crate has no <c>fraction</c> to compare.</para>
        /// </summary>
        private bool FactionAllowsContact(Collider2D other)
        {
            var unit = other.GetComponent<PFE.Entities.Units.UnitController>();
            if (unit == null) return true;
            return FactionRule.CanHitDirectly(OwnerFaction, unit.Faction);
        }

        /// <summary>
        /// Per-target blast multiplier — AS3 <c>Bullet.explRun</c> (<c>weapon/Bullet.as:764-786</c>),
        /// reached from here because <c>PhisBullet</c> inherits <c>explosion()</c> from <c>Bullet</c>.
        /// 1 for a non-unit and for a unit of a different faction.
        /// </summary>
        private float ExplosionMultiplierFor(Collider2D other)
        {
            var unit = other.GetComponent<PFE.Entities.Units.UnitController>();
            if (unit == null) return 1f;
            return FactionRule.ExplosionMultiplier(OwnerFaction, unit.Faction, unit.IsPlayer);
        }

        // ── Runtime ───────────────────────────────────────────────────────────

        private Rigidbody2D _rb;
        private bool        _isInitialized;

        // ── Stage C: sim-owned state (only used when ThrownObjectsUseTileSeam is on) ──────────
        // The simulation owns the position; the Transform is a *view* of it, written in LateUpdate.
        // ISimTickable's contract forbids a tick from reading Transform, so SimTick touches neither
        // transform nor _rb — everything it produces goes through the deferred write below.

        /// <summary>Authoritative flight state, in AS3's own units — see <see cref="ThrownObjectState"/>.</summary>
        private ThrownObjectState _simState;

        /// <summary>The tile world, resolved once at spawn from the room the object was spawned in.</summary>
        private IThrownTileProbe _probe;

        /// <summary>True while this instance is registered on SimLoop and must be unregistered.</summary>
        private bool _registeredOnSimLoop;

        /// <summary>Set by <see cref="SimTick"/>, consumed by <see cref="LateUpdate"/>.</summary>
        private bool _viewDirty;

        /// <summary>Set by <see cref="SimTick"/>, consumed by <see cref="LateUpdate"/> — a tick must
        /// not resolve damage or destroy a GameObject.</summary>
        private bool _pendingDetonate;

        /// <summary>Set by <see cref="SimTick"/> when AS3's <c>vse</c> fired (left the room).</summary>
        private bool _pendingReturnToPool;

        /// <summary>Captured at spawn so the view write has a Z without reading Transform in a tick.</summary>
        private Vector3 _spawnPosition;

        // ── Injected ─────────────────────────────────────────────────────────

#pragma warning disable CS0649
        [Inject] private IPublisher<DamageDealtMessage> _damageDealtPublisher;
        [Inject] private PfeDebugSettings               _debugSettings;

        // Stage C. Fully qualified: this file is not in the PFE.Systems.Physics namespace, but being
        // explicit here keeps the "which IPhysicsWorldService" question answerable at a glance.
        [Inject] private PFE.Systems.Physics.IPhysicsWorldService _physicsWorld;
        [Inject] private SimLoop                        _simLoop;
        [Inject] private SimClock                       _simClock;
#pragma warning restore CS0649

        public Action<ThrownObject> OnReturnToPool { get; set; }

        // ── Stage C: registration and tick ───────────────────────────────────────────────────

        /// <summary>
        /// Thrown objects move in the <c>Projectiles</c> slot, after the player motor and units have
        /// finalised their positions for this tick. Shared with <see cref="Projectile"/>, which is
        /// harmless: both read only tile state, and neither reads the other's position.
        /// </summary>
        public int TickOrder => SimTickOrder.Projectiles;

        /// <summary>
        /// True when this instance is actually running the flipped path.
        ///
        /// <para>Keyed off the registration rather than off the settings flag on purpose. If the flag
        /// is on but SimLoop or the tile seam was unavailable, <see cref="RegisterOnSimLoop"/> leaves
        /// this false and the instance stays wholly on the legacy path. Reading the flag instead would
        /// leave such an instance driven by <i>neither</i> loop — motionless in mid-air — which is a
        /// far worse failure than quietly staying on the old path.</para>
        /// </summary>
        private bool FlipActive => _registeredOnSimLoop;

        // ── Initialization ────────────────────────────────────────────────────

        private void Awake()
        {
            _rb = GetComponent<Rigidbody2D>();
            _rb.bodyType              = RigidbodyType2D.Kinematic;
            _rb.gravityScale          = 0f;
            _rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
        }

        /// <summary>
        /// Called by <see cref="ProjectileSpawner"/> from a ThrownObject ShotPlan, after
        /// <c>IObjectResolver.Inject</c>, so the injected fields below are live.
        /// </summary>
        public void Initialize(
            Vector2 initialVelocity,
            int     fuseFrames,
            float   explRadius,
            bool    bumc      = false,
            float   skok      = ProjectilePhysicsMath.ThrowBounceRetention,
            float   tormoz    = ProjectilePhysicsMath.ThrowFloorDamping,
            float   brake     = ProjectilePhysicsMath.BrakePxPerFrame2)
        {
            _velocity         = initialVelocity;
            _explRadius       = explRadius;
            _bumc             = bumc;
            _skok             = skok;
            _tormoz           = tormoz;
            _brake            = ProjectilePhysicsMath.SlidingFriction(brake);
            _brakePxPerFrame2 = brake;
            _stay             = false;
            _armed            = true;
            _isInitialized    = true;
            _spawnPosition    = transform.position;

            // Two fuses from one argument — see the field comments. AS3's explosion lead-in is why
            // they differ: `liv == 3` fires it, so 3 of the requested frames are the explosion.
            _fuseTimer = fuseFrames / SimClock.FramesPerSecond;
            _fuseTicks = Mathf.Max(0, fuseFrames - ThrownObjectPhysics.ExplosionLeadInFrames);

            _rb.linearVelocity = _velocity;

            // ── Stage C: hand this flight to the sim, or leave it on the legacy path ───────────
            _simState            = default;
            _probe               = null;
            _viewDirty           = false;
            _pendingDetonate     = false;
            _pendingReturnToPool = false;

            if (_debugSettings?.ThrownObjectsUseTileSeam == true)
            {
                RegisterOnSimLoop();
            }
        }

        public void SetDamageContext(DamageContext ctx)
        {
            _damageContext    = ctx;
            _hasDamageContext = true;
        }

        /// <summary>
        /// Registers this instance on <see cref="SimLoop"/> and resolves the room's tile seam.
        ///
        /// <para>Both are all-or-nothing. A registered instance with no probe would step through
        /// every wall; a probe with no registration would never step at all. So a failure to resolve
        /// either one leaves <see cref="FlipActive"/> false and the instance on the legacy path,
        /// which is the failure mode that is merely <i>old</i> rather than <i>broken</i>.</para>
        ///
        /// <para>The room is resolved from the spawn point, once — which is also AS3's lifetime for
        /// the binding: <c>PhisBullet</c> holds one <c>loc</c> for its whole flight and is removed
        /// when it leaves (<c>PhisBullet.as:234-238</c>).</para>
        /// </summary>
        private void RegisterOnSimLoop()
        {
            if (_simLoop == null || _simClock == null)
            {
                WarnStayOnLegacyPath("SimLoop/SimClock is unavailable");
                return;
            }

            // `transform` is readable here: Initialize is called from the spawner, not from a tick.
            Vector2 spawnWorldPx = (Vector2)_spawnPosition * TileQueryConstants.UnitToPixel;

            if (_physicsWorld == null ||
                !_physicsWorld.TryGetRoomTileQueryAt(spawnWorldPx, out PFE.Systems.Physics.RoomTileQuery room))
            {
                WarnStayOnLegacyPath($"spawn point {spawnWorldPx} px is outside every mirrored room");
                return;
            }

            _probe = new ThrownTileProbe(room.Service, room.WorldBoundsPx);

            // AS3-native state: position in world pixels, velocity in px/frame. The conversion
            // happens exactly here and in LateUpdate, and nowhere else.
            _simState = new ThrownObjectState
            {
                PositionPx         = spawnWorldPx,
                VelocityPxPerFrame = _velocity / ProjectilePhysicsMath.VelocityScale,
                Stay               = false,
            };

            _simLoop.Register(this);
            _registeredOnSimLoop = true;
        }

        private void WarnStayOnLegacyPath(string reason)
        {
            if (_debugSettings?.LogProjectileLifecycle != true) return;

            Debug.LogWarning(
                $"[ThrownObject] '{name}' has ThrownObjectsUseTileSeam on but {reason}; " +
                "staying on the FixedUpdate path.");
        }

        /// <summary>Releases the SimLoop registration. Idempotent, and safe before any registration.</summary>
        private void UnregisterFromSimLoop()
        {
            if (!_registeredOnSimLoop) return;

            _simLoop?.Unregister(this);
            _registeredOnSimLoop = false;
        }

        /// <summary>
        /// One AS3 frame of flight: the fuse, then <see cref="ThrownObjectPhysics.Step"/>.
        ///
        /// <para>Reads no <c>Time</c>, no <c>Transform</c> and no <c>Input</c> — the tick index is the
        /// only clock, and the position lives in <see cref="_simState"/>. The Transform write, the
        /// explosion and the pool release are all deferred to <see cref="LateUpdate"/>, which is the
        /// "separate view pass" the <see cref="ISimTickable"/> contract describes.</para>
        /// </summary>
        public void SimTick(int tickIndex)
        {
            if (!_isInitialized) return;

            // Belt and braces: FlipActive and "has a probe" are established together in
            // RegisterOnSimLoop and torn down together in ResetThrownObject, so this cannot fire in
            // practice. It is here because the failure it prevents — dereferencing a null probe
            // inside a tick, mid-iteration of the tickable list — would take the whole loop down.
            if (!FlipActive || _probe == null) return;

            // No dt anywhere in here, and that is the point: ThrownObjectPhysics is per-frame
            // arithmetic, so a "delta time" would be a unit error, not a refinement. The rate comes
            // from SimLoop stepping this once per canonical frame.
            ThrownObjectPhysics.Step(
                ref _simState, _probe, _skok, _tormoz, _brakePxPerFrame2, _bumc);

            _viewDirty = true;

            // A contact detonation (`bumc`) is resolved by the view pass: it plays a sound, resolves
            // damage over an overlap query and may release a pooled object — none of which a tick may
            // do while the tickable list is being iterated.
            if (_simState.Detonated)
            {
                _pendingDetonate = true;
                return;
            }

            // AS3 `vse` (`PhisBullet.as:234-238`/`:309-313`): the object left the room.
            if (_simState.Removed)
            {
                _pendingReturnToPool = true;
                return;
            }

            // AS3 PhisBullet.as:108-124. The decrement is AFTER the move, so a 75-frame fuse gets 72
            // moves and then explodes — matching `liv` reaching 3 on the 72nd frame.
            _fuseTicks--;
            if (_fuseTicks <= 0)
            {
                _pendingDetonate = true;
            }
        }

        // ── Unity lifecycle ───────────────────────────────────────────────────

        /// <summary>
        /// Legacy integration path. Active only when the Stage C flip is <i>not</i> running for this
        /// instance — see <see cref="FlipActive"/>, which is keyed off the SimLoop registration rather
        /// than off the settings flag so an instance whose registration failed is still driven.
        /// </summary>
        private void FixedUpdate()
        {
            if (!_isInitialized) return;

            // With the flip running, SimLoop owns integration. Standing down here is essential:
            // leaving both drivers active would step the object twice per frame, at two rates.
            if (FlipActive) return;

            float dt = Time.fixedDeltaTime;

            // ── Fuse countdown ────────────────────────────────────────────────
            _fuseTimer -= dt;
            if (_fuseTimer <= 0f)
            {
                Detonate();
                return;
            }

            // ── Sliding friction while resting ────────────────────────────────
            if (_stay)
            {
                float sign = Mathf.Sign(_velocity.x);
                float reduction = _brake * dt;
                if (Mathf.Abs(_velocity.x) > reduction)
                    _velocity.x -= sign * reduction;
                else
                    _velocity.x = 0f;

                _rb.linearVelocity = _velocity;
                return;
            }

            // ── Gravity integration ───────────────────────────────────────────
            _velocity.y -= GravityPerSec * dt;

            _rb.linearVelocity = _velocity;
        }

        /// <summary>
        /// The flipped path's view pass: writes sim state out to the Transform and the Rigidbody, and
        /// performs the detonation and pool return that <see cref="SimTick"/> deferred.
        ///
        /// <para><b>Position is written and velocity is zeroed, never both set.</b> A kinematic body
        /// given both a velocity and an explicit position would advance twice — once from our own
        /// integration and once from Box2D sweeping the velocity we handed it. The Rigidbody still has
        /// to be moved, because Unity's trigger detection (which resolves <c>IDamageable</c> hits in
        /// <i>both</i> modes) reads the body's position, not the Transform.</para>
        ///
        /// <para>Order matters: the view write comes first, because <see cref="Detonate"/> centres its
        /// overlap query on <c>transform.position</c>.</para>
        /// </summary>
        private void LateUpdate()
        {
            if (!FlipActive) return;

            if (_viewDirty)
            {
                _viewDirty = false;

                var world = new Vector3(
                    _simState.PositionPx.x * TileQueryConstants.PixelToUnit,
                    _simState.PositionPx.y * TileQueryConstants.PixelToUnit,
                    _spawnPosition.z);

                transform.position = world;

                if (_rb != null)
                {
                    _rb.position       = world;
                    _rb.linearVelocity = Vector2.zero;
                }
            }

            if (_pendingDetonate)
            {
                _pendingDetonate = false;
                Detonate();
                return;
            }

            if (_pendingReturnToPool)
            {
                _pendingReturnToPool = false;
                ReturnToPool();
            }
        }

        /// <summary>
        /// A destroyed instance must leave the loop. <see cref="Projectile"/> is pooled, so its
        /// <c>ResetProjectile</c> is the unregister point; this object is Instantiated per shot and
        /// Destroyed, so <c>OnDestroy</c> is. Without it SimLoop keeps a reference to a dead
        /// MonoBehaviour and ticks it forever — a leak that presents as "SimLoop gets slower the more
        /// grenades you throw", not as an error.
        /// </summary>
        private void OnDestroy()
        {
            UnregisterFromSimLoop();
        }

        private void OnTriggerEnter2D(Collider2D other)
        {
            if (!_isInitialized || other.isTrigger) return;

            // Damageable — detonate on contact if bumc, otherwise ignore (fuse handles it).
            // A same-faction unit is not a contact at all: no detonation, no bounce (AS3
            // PhisBullet.sensor(), weapon/PhisBullet.as:151).
            var dmg = other.GetComponent<IDamageable>();
            if (dmg != null)
            {
                if (_bumc && FactionAllowsContact(other)) Detonate();
                return;
            }

            // With the flip running, tile contacts are the sim's own point sweep. Without this the
            // per-tile collider would report the same surface and every bounce would resolve twice —
            // two velocity flips, so the object would come off the wall with the wrong sign.
            if (FlipActive && other.GetComponent<TileCollider>() != null) return;

            // Solid surface.
            HandleSurfaceCollision(other);
        }

        // ── Collision / bounce ────────────────────────────────────────────────

        private void HandleSurfaceCollision(Collider2D other)
        {
            if (_bumc)
            {
                Detonate();
                return;
            }

            // Determine collision axis from relative positions.
            Vector2 pos    = transform.position;
            Vector2 center = other.bounds.center;
            Vector2 diff   = pos - (Vector2)center;

            bool hitHorizontal = Mathf.Abs(diff.x) > Mathf.Abs(diff.y);

            if (hitHorizontal)
            {
                // Wall bounce.
                _velocity.x = Mathf.Abs(_velocity.x) * _skok * Mathf.Sign(diff.x);
            }
            else
            {
                // Floor / ceiling bounce.
                if (_velocity.y < 0f)
                {
                    // Hitting floor. AS3 PhisBullet.as:330 bounces only while `dy > 2` px/frame; below
                    // that the object settles. `dy` is a VELOCITY, so this threshold converts by the
                    // velocity factor (→ 0.6 units/s), not the acceleration one.
                    if (Mathf.Abs(_velocity.y) > ProjectilePhysicsMath.SettleThresholdVelocity)
                    {
                        _velocity.y  = Mathf.Abs(_velocity.y) * _skok;
                        _velocity.x *= _tormoz;
                    }
                    else
                    {
                        // Settle.
                        _velocity.y = 0f;
                        _stay       = true;
                    }
                }
                else
                {
                    // Hitting ceiling.
                    _velocity.y = -Mathf.Abs(_velocity.y) * _skok;
                }
            }

            _rb.linearVelocity = _velocity;
        }

        // ── Detonation ────────────────────────────────────────────────────────

        public void Detonate()
        {
            if (!_isInitialized) return;
            _isInitialized = false;

            Vector3 centre = transform.position;

            if (_explRadius > 0f)
            {
                Collider2D[] hits = Physics2D.OverlapCircleAll(centre, _explRadius);
                foreach (var hit in hits)
                {
                    if (hit.isTrigger) continue;

                    var damageable = hit.GetComponent<IDamageable>();
                    if (damageable != null && damageable.IsAlive)
                    {
                        if (_hasDamageContext)
                            DamageResolver.ResolveExplosion(
                                _damageContext, damageable,
                                hit.transform.position, centre, _explRadius,
                                _damageDealtPublisher,
                                factionMultiplier: ExplosionMultiplierFor(hit));
                        else
                            damageable.TakeDamage(10f);
                    }
                }
            }

            ReturnToPool();
        }

        // ── Pool support ─────────────────────────────────────────────────────

        private void ReturnToPool()
        {
            if (OnReturnToPool != null) OnReturnToPool(this);
            else Destroy(gameObject);
        }

        public void ResetThrownObject()
        {
            _isInitialized    = false;
            _hasDamageContext = false;
            _stay             = false;
            _armed            = false;
            _velocity         = Vector2.zero;
            _fuseTimer        = 0f;
            _fuseTicks        = 0;
            _explRadius       = 0f;
            _bumc             = false;

            // Stage C: a released instance must not stay registered. SimLoop.Register de-duplicates,
            // so leaving it registered would not double-register — it would keep the released
            // instance ticked, which is worse: a parked object would keep integrating and could
            // report contacts from wherever it was parked.
            UnregisterFromSimLoop();
            _probe               = null;
            _simState            = default;
            _viewDirty           = false;
            _pendingDetonate     = false;
            _pendingReturnToPool = false;
            _spawnPosition       = Vector3.zero;

            if (_rb != null)
            {
                _rb.linearVelocity = Vector2.zero;
                _rb.bodyType       = RigidbodyType2D.Kinematic;
            }
        }

        // ── Debug ─────────────────────────────────────────────────────────────

        private void OnDrawGizmos()
        {
            if (!_isInitialized) return;
            if (_explRadius > 0f)
            {
                Gizmos.color = new Color(1f, 0.3f, 0f, 0.25f);
                Gizmos.DrawWireSphere(transform.position, _explRadius);
            }
        }

        // ── Stage C: the production tile probe ────────────────────────────────────────────────

        /// <summary>
        /// Adapter from <see cref="IThrownTileProbe"/> to the room's <see cref="ITileQueryService"/>.
        ///
        /// <para>Holds the room's origin so the step can stay in world pixels — the space the seam
        /// itself uses — and converts to room-local tile coordinates only at the two points where a
        /// tile coordinate is genuinely what is wanted: the solidity test and the cell rect.</para>
        ///
        /// <para><b>The solidity test goes through <see cref="ITileQueryService.IsSolidAt"/></b>, which
        /// resolves to <c>physicsType == Wall</c> — the same predicate as
        /// <see cref="ProjectileOcclusionRule.BlocksProjectile"/> and AS3's <c>phis == 1</c>. So a
        /// grenade passes through catwalks, slopes and ladders exactly as a bullet does, for the same
        /// reason, and <c>ProjectileOcclusionRuleTests</c> already pins the vocabulary.</para>
        /// </summary>
        private sealed class ThrownTileProbe : IThrownTileProbe
        {
            private readonly ITileQueryService _query;
            private readonly Vector2           _originPx;

            public Rect RoomBoundsPx { get; }

            public ThrownTileProbe(ITileQueryService query, Rect roomBoundsPx)
            {
                _query      = query;
                _originPx   = query.OriginPixel;
                RoomBoundsPx = roomBoundsPx;
            }

            public bool IsSolidAt(Vector2 worldPx)
            {
                return _query.IsSolidAt(CellAt(worldPx));
            }

            public Rect CellBoundsAt(Vector2 worldPx)
            {
                Vector2Int coord = CellAt(worldPx);
                float tileSize   = TileQueryConstants.TileSize;

                return new Rect(
                    _originPx.x + coord.x * tileSize,
                    _originPx.y + coord.y * tileSize,
                    tileSize,
                    tileSize);
            }

            /// <summary>
            /// World pixels → room-local tile coordinate, <b>exactly</b> the cell AS3's
            /// <c>loc.getAbsTile</c> would return.
            ///
            /// <para><b>X is a plain floor; Y is not.</b> AS3 floors both axes, but its Y runs the
            /// opposite way to the port's, and that matters at cell boundaries. AS3's cell row
            /// <c>r</c> occupies <c>as3Y ∈ [r*40, (r+1)*40]</c>, and a point exactly on a boundary
            /// floors to the row that <i>starts</i> there — which for a cell's top edge is the cell
            /// itself. That is load-bearing, not incidental: a resting thrown object sits at
            /// <c>phY1 - 1</c> and re-detects the floor by stepping onto <c>phY1</c> exactly
            /// (<c>PhisBullet.as:325-343</c>). Floor the Y-up coordinate instead and the point lands
            /// in the cell <i>above</i> the floor, the floor is never re-detected, and the object
            /// creeps downward one pixel per frame forever.</para>
            ///
            /// <para>The mirror is exact, not an epsilon. With <c>H</c> the room height in pixels and
            /// <c>as3Y = H - portY</c>:
            /// <c>r = floor((H - portY) / 40) = n - ceil(portY / 40)</c>, and the port's row index is
            /// <c>ty = (n - 1) - r</c>, so <c>ty = ceil(portY / 40) - 1</c>. Both give
            /// <c>portY = 40 → ty = 0</c> where a naive floor gives 1.</para>
            /// </summary>
            private Vector2Int CellAt(Vector2 worldPx)
            {
                Vector2 local = worldPx - _originPx;
                float tileSize = TileQueryConstants.TileSize;

                return new Vector2Int(
                    Mathf.FloorToInt(local.x / tileSize),
                    Mathf.CeilToInt(local.y / tileSize) - 1);
            }
        }
    }
}
