using UnityEngine;
using VContainer;
using PFE.Core;
using PFE.Data.Definitions;
using PFE.Systems.Audio;
using PFE.Systems.Combat;
using PFE.Systems.Weapons;
using PFE.Core.Messages;
using PFE.Systems.Map.Rendering;
using PFE.Systems.Map.TileQuery;
using MessagePipe;
using System;
using System.Collections.Generic;

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
    /// mirror exists to sweep the projectile's 93×6 needle hitbox (<c>ProjectileTemplateSpec.ColliderSize</c>)
    /// — a shape a grenade does not
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
    /// <para><b>The legacy path's bounce axis is a port invention, but it is NOT the "dinamit jiggle".</b>
    /// It derives the axis from <c>|diff.x| &gt; |diff.y|</c> against the collider's <i>bounds centre</i>,
    /// which is not a contact normal. That looked like the obvious cause of the owner's report, so it was
    /// measured (10-04) before being changed: sweeping all 18 304 points on the contact circle around one
    /// 40 px tile — radius 0.5, per <c>Throwable.prefab</c> — a true circle-vs-AABB normal classifier
    /// disagrees with this test at <b>exactly one</b> point, a corner tie. On a flat floor the two never
    /// disagree: a floor contact puts the object's centre ~0.7 units from the tile centre vertically, and
    /// no reachable horizontal offset (at most the tile's own half-width, 0.2) can exceed that. The
    /// proposed normal-based rewrite was therefore <b>reverted as a no-op</b> rather than landed with a
    /// wrong story. See <c>TOPIC_open_work.md</c> row 2 for what was ruled out and what is still open.</para>
    ///
    /// <para><b>The real divergence from AS3 is point-versus-circle, not the axis test.</b> AS3's
    /// <c>PhisBullet.run()</c> moves a <b>point</b> and asks whether the cell that point now occupies is
    /// solid (<c>PhisBullet.as:242/263/286/315</c>), resolving X before Y and choosing the axis from the
    /// <i>direction of travel</i> plus that test order — there is no contact normal anywhere in the
    /// oracle. This path instead lets a 0.5-unit (100 px) circle trigger against 40 px tiles, so it
    /// detects a contact up to 50 px before AS3's point would, and comes to rest with its centre 50 px
    /// above the surface where AS3's point sits 1 px above it (<c>:325</c>). The faithful model already
    /// exists as <see cref="ThrownObjectPhysics"/>, which reproduces the point sweep with sub-steps and
    /// is covered by fixtures; it is reached by turning <c>ThrownObjectsUseTileSeam</c> on, which defaults
    /// off. Whether to flip that default is the owner's call, not a silent change.</para>
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
        private bool    _sticky;             // AS3 `lip` — latch on first tile contact (throwTip == 2)
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

        // ── Explosion presentation (the blast a thrown object draws when it goes off) ─────────
        //
        // AS3's thrown object IS a bullet: `PhisBullet extends Bullet`, and it detonates through the
        // inherited chain `explosion() → explRun() → explVis()` (`Bullet.as:665,705,878`) — the very
        // path `Projectile.EmitExplosionVisuals` already mirrors. Reached from `PhisBullet.as:122`
        // (fuse) and `:196` (`popadalo`, i.e. contact / settle). So a grenade must draw the same blast
        // a fired round does; before this it applied its damage and vanished silently.

        /// <summary>AS3 <c>weap.visexpl</c> — the per-weapon override <c>explVis()</c> reads first.</summary>
        private string     _visExpl;

        /// <summary>Selects the fallback arm of the explosion table when <see cref="_visExpl"/> is empty.</summary>
        private DamageType _damageType;

        /// <summary>
        /// The weapon's explosion <b>shape</b> and <b>pulse count</b> — AS3 <c>explTip</c> /
        /// <c>explKol</c> (<c>weapon/Weapon.as:152/154</c>, imported from <c>char.@expltip</c> /
        /// <c>char.@explkol</c> and copied to the bullet by <c>setBullet</c>, <c>:1676/:1678</c>).
        /// </summary>
        /// <remarks>
        /// <c>_explKol</c> is what makes <c>fgren</c>, <c>molotov</c>, <c>gasgr</c> and <c>acidgr</c> a
        /// damage <i>area</i>: 10 or 12 pulses spread over ~3 s instead of one burst. The schedule is
        /// <see cref="ExplosionPulseRules"/>; a value below 2 means one pulse and no train, which is the
        /// state of every other throwable.
        /// </remarks>
        private int _explTip = 1;
        private int _explKol;

        /// <summary>True while the multi-pulse train is running — the object must not be released yet.</summary>
        private bool _pulseActive;

        /// <summary>Canonical ticks since the detonation; drives <see cref="ExplosionPulseRules.PulseDueAt"/>.</summary>
        private int _pulsesElapsed;

        /// <summary>
        /// A pulse the sim tick has scheduled for the view pass, or <c>-1</c> for none. The same
        /// deferral <see cref="_pendingDetonate"/> uses and for the same reason: a tick may not resolve
        /// damage or emit a particle while the tickable list is being iterated.
        /// </summary>
        private int _pendingPulse = -1;

        /// <summary>
        /// Set once the blast has gone off. Distinct from <see cref="_isInitialized"/>, which must stay
        /// true for the whole pulse train — a sustained blast is still a live object, and gating its tick
        /// on the initialization flag would end the train after its first pulse.
        /// </summary>
        private bool _detonated;

        /// <summary>
        /// AS3 <c>PhisBullet.sndHit</c>, which <c>WThrow</c> fills from the weapon's
        /// <c>&lt;snd fall&gt;</c> (<c>WThrow.as:196</c>, value from <c>:64-66</c>) — the sound of the
        /// object striking a tile, played by <c>PhisBullet.run()</c> at five sites. Null or empty
        /// means the object lands silently, which is the correct state for any weapon whose
        /// <c>&lt;snd&gt;</c> block has no <c>fall</c>. The rule (and its volume) is
        /// <see cref="ThrownContactSound"/>.
        /// </summary>
        private string _soundFall;

        /// <summary>Scratch list for the planned emits — reused so a burst allocates nothing.</summary>
        private readonly List<PFE.Systems.Particles.ParticleEmit> _explosionEmits =
            new List<PFE.Systems.Particles.ParticleEmit>();

        /// <summary>The unseeded presentation stream, resolved once (see <see cref="PresentationRng"/>).</summary>
        private PFE.Core.Rng.IRngService _presentationRng;

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

        // ── Visual ────────────────────────────────────────────────────────────
        //
        // AS3's thrown object is a PhisBullet, and a PhisBullet always has a view: `WThrow.as:38`
        // overrides `vBullet = vWeapon`, so the thing in flight is drawn with the WEAPON'S OWN art
        // (`vis<weaponId>`), not with a `visbul*` bullet class. `WThrow.as:194` then calls
        // `b.vis.play()` — the 5-frame symbols are a lit fuse, so the view animates — and
        // `PhisBullet.as:100` spins the whole view by `dr` every frame.
        //
        // The port had none of that: no SpriteRenderer on this prefab, on any child, or in this file.
        // `Assets/_PFE/Prefabs/Throwable.prefab` carries only Transform + Rigidbody2D + Collider2D +
        // this component, so a grenade was simulated correctly and drawn nowhere.

        [Header("Visual")]
        [Tooltip("Sprite renderer for the thrown object. Leave empty to have one created on a child at " +
                 "runtime (the prefab ships without one).")]
        [SerializeField] private SpriteRenderer _visualRenderer;

        [Tooltip("Sorting layer for the created child renderer. Matches the projectile template " +
                 "(ProjectileTemplateSpec.SortingLayerName), which is on " +
                 "Foreground; the project's 'Default' layer sits BELOW the map tiles, so leaving this " +
                 "empty draws the object behind the floor.")]
        [SerializeField] private string _visualSortingLayer = MapSortingLayers.Foreground;

        /// <summary>The child the spin rotates. Never this transform — see <see cref="EnsureVisualRenderer"/>.</summary>
        private Transform _visualTransform;

        private WeaponVisualDefinition _currentVisual;
        private float _visualFrameTimer;
        private int   _visualFrameIndex;

        /// <summary>Accumulated view rotation in degrees — AS3's <c>vis.rotation</c>.</summary>
        private float _spinDegrees;

        /// <summary>
        /// AS3 <c>b.dx</c> as it was at spawn, in px/frame — the value <c>dr</c> holds for the whole
        /// flight (<c>WThrow.as:192</c>). Captured rather than read live because a bounce flips the
        /// live sign and the oracle deliberately does not follow it.
        /// </summary>
        private float _spinDxAtSpawnPx;

        /// <summary>Name of the child that carries the sprite, created on demand.</summary>
        public const string VisualChildName = "visual";

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
        [Inject] private PFE.Systems.Combat.DamageSystem _damageSystem;
        [Inject] private PfeDebugSettings               _debugSettings;

        // The blast's presentation. Same trio Projectile injects, for the same reason: the emitter
        // resolves the room's origin/height (so a world position can be converted to the AS3-local
        // space `EmitAt` wants), the tile-water query answers AS3's `inWater` at the impact point, and
        // the sound service plays the table's chosen id. All three are optional — a fixture built with
        // `new` has none, and the emit simply does nothing.
        [Inject] private ISoundService                                   _soundService;
        [Inject] private PFE.Systems.Particles.Adapters.RoomParticleEmitter _particles;
        [Inject] private PFE.Systems.Particles.IParticleTileWater            _particleTileWater;
        [Inject] private PFE.Core.Rng.IRngService                            _rng;

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
            float   brake     = ProjectilePhysicsMath.BrakePxPerFrame2,
            bool    sticky    = false,
            // The blast's presentation inputs, carried exactly as Projectile carries them. Optional so
            // existing editor/test callers keep working; the spawner always passes both.
            DamageType damageType = DamageType.PhysicalBullet,
            string  visExpl   = null,
            // AS3 `WThrow.as:196 (b as PhisBullet).sndHit = this.sndFall`, i.e. the weapon's
            // `<snd fall>`. Optional for the same reason the two above are.
            string  soundFall = null,
            // The blast's SHAPE and PULSE COUNT — `char.@expltip` / `char.@explkol`. Optional so
            // existing editor/test callers keep working; the spawner always passes both. The defaults
            // are AS3's declaration defaults (Weapon.as:152/154), i.e. one pulse of the blast shape.
            int     explTip   = 1,
            int     explKol   = 0)
        {
            _velocity         = initialVelocity;
            _explRadius       = explRadius;
            _damageType       = damageType;
            _visExpl          = visExpl;
            _soundFall        = soundFall;
            _explTip          = explTip;
            _explKol          = explKol;
            _bumc             = bumc;
            _sticky           = sticky;
            _skok             = skok;
            _tormoz           = tormoz;
            _brake            = ProjectilePhysicsMath.SlidingFriction(brake);
            _brakePxPerFrame2 = brake;
            _stay             = false;
            _armed            = true;
            _isInitialized    = true;
            _detonated        = false;
            _pulseActive      = false;
            _pulsesElapsed    = 0;
            _pendingPulse     = -1;
            _spawnPosition    = transform.position;

            // Two fuses from one argument — see the field comments. AS3's explosion lead-in is why
            // they differ: `liv == 3` fires it, so 3 of the requested frames are the explosion.
            _fuseTimer = fuseFrames / SimClock.FramesPerSecond;
            _fuseTicks = Mathf.Max(0, fuseFrames - ThrownObjectPhysics.ExplosionLeadInFrames);

            // AS3 `WThrow.as:192 (b as PhisBullet).dr = this.throwTip == 2 ? 0 : b.dx`. `b.dx` is a
            // px/frame velocity, and this field is a velocity in units/s, so the conversion is the
            // velocity factor — not the acceleration one, and not the identity.
            _spinDxAtSpawnPx = _velocity.x / ProjectilePhysicsMath.VelocityScale;

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

            // ── The sustained blast ───────────────────────────────────────────────────────────────
            //
            // A detonated object with a pulse train still to run is a live object, but not a moving
            // one: it is frozen at the blast point and its only job is to keep detonating. It is
            // deliberately stepped by neither the physics nor the fuse — AS3 gets the same result by
            // only decrementing `liv` while `expl_t == 0` (`PhisBullet.as:108-115`).
            if (_pulseActive)
            {
                _pulsesElapsed++;

                int due = ExplosionPulseRules.PulseDueAt(_explKol, _pulsesElapsed);
                if (due >= 0) _pendingPulse = due;

                // Checked after the pulse, so the last pulse runs on the same tick the train ends.
                if (ExplosionPulseRules.IsFinished(_explKol, _pulsesElapsed))
                    _pendingReturnToPool = true;

                return;
            }

            // No dt anywhere in here, and that is the point: ThrownObjectPhysics is per-frame
            // arithmetic, so a "delta time" would be a unit error, not a refinement. The rate comes
            // from SimLoop stepping this once per canonical frame.
            ThrownObjectPhysics.Step(
                ref _simState, _probe, _skok, _tormoz, _brakePxPerFrame2, _bumc, _sticky);

            // AS3's five tile-contact sound sites (PhisBullet.as:246/267/290/321/336). The step
            // records the request instead of emitting it — a tick may no more play a sound than it
            // may resolve damage — and the read has to happen BEFORE the `Detonated` early-return
            // below, because a `bumc` object sounds on its way into the blast (`:319-322`).
            if (_simState.ContactSoundSpeedPxPerFrame > 0f)
                PlayContactSound(_simState.ContactSoundSpeedPxPerFrame);

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

            // The legacy twin of SimTick's pulse branch. The legacy path has no deferred view pass for
            // this, so the pulse runs inline — FixedUpdate is a Unity callback, not a sim tick, so
            // resolving damage and emitting particles here is safe. `Time.fixedDeltaTime` is not used:
            // the pulse schedule is in canonical 30 Hz frames, exactly as it is on the flipped path.
            if (_pulseActive)
            {
                _pulsesElapsed++;

                int due = ExplosionPulseRules.PulseDueAt(_explKol, _pulsesElapsed);
                if (due >= 0) RunPulse(due);

                if (ExplosionPulseRules.IsFinished(_explKol, _pulsesElapsed))
                {
                    _pulseActive = false;
                    ReturnToPool();
                }
                return;
            }

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

            // A pulse of a sustained blast, scheduled by SimTick. Runs BEFORE the pool return below,
            // because the last pulse and the release are scheduled on the same tick and the blast has
            // to land before the object goes back.
            if (_pendingPulse >= 0)
            {
                int pulse = _pendingPulse;
                _pendingPulse = -1;
                RunPulse(pulse);
            }

            if (_pendingReturnToPool)
            {
                _pendingReturnToPool = false;
                ReturnToPool();
            }
        }

        // ── View: the sprite and its spin ─────────────────────────────────────

        /// <summary>
        /// The view pass that runs on <b>both</b> paths.
        ///
        /// <para>Deliberately not folded into <see cref="LateUpdate"/>, which returns early unless
        /// <see cref="FlipActive"/>: the fuse flicker and the spin are the same behaviour whichever
        /// loop is integrating the flight, so keying them off the Stage C flag would make the sprite
        /// stop moving whenever the flag is off.</para>
        /// </summary>
        private void Update()
        {
            if (!_isInitialized) return;

            UpdateThrownAnimation();
            UpdateThrownSpin();
        }

        /// <summary>
        /// Applies the throwing weapon's own art — <c>WeaponDefinition.weaponVisual</c>, i.e.
        /// <c>vis&lt;weaponId&gt;</c>.
        ///
        /// <para><b>Not <c>projectileVisual</c>.</b> That is the <c>visbul*</c> family — the art of a
        /// round in flight — and a throwable has none: every <c>throwtip</c> weapon's <c>&lt;vis&gt;</c>
        /// block carries only <c>tipdec</c>/<c>icomult</c>, no <c>vbul</c>. AS3 says so explicitly by
        /// <i>overwriting</i> the bullet class with the weapon class (<c>WThrow.as:38</c>
        /// <c>vBullet = vWeapon</c>), which is also why the grenade you see in hand is the grenade you
        /// see in flight.</para>
        /// </summary>
        public void ApplyVisual(WeaponVisualDefinition visual)
        {
            EnsureVisualRenderer();

            _currentVisual    = visual;
            _visualFrameTimer = 0f;
            _visualFrameIndex = 0;

            if (_visualRenderer == null) return;

            if (visual == null || visual.frames == null || visual.frames.Length == 0)
            {
                // Honest empty state: no art imported for this weapon yet, so draw nothing. The
                // renderer stays disabled rather than showing a white box.
                _visualRenderer.enabled = false;
                return;
            }

            _visualRenderer.enabled = true;
            _visualRenderer.sprite  = visual.frames[0];
        }

        /// <summary>
        /// Finds the sprite renderer, creating the child if the prefab has none.
        ///
        /// <para><b>Why it creates one.</b> The prefab ships without a renderer anywhere, and that is
        /// a port gap rather than an art-less case — see the Visual region's note. Creating the child
        /// here means the sprite appears the moment a visual is assigned, with no prefab edit, which
        /// matters because prefab edits are the owner's to make.</para>
        ///
        /// <para><b>Why a child and not this transform.</b> This transform is the physics body: the
        /// trigger collider that resolves <c>IDamageable</c> contacts and the overlap query in
        /// <see cref="Detonate"/> both read it. Rotating it would rotate the collider too, so a
        /// non-uniform box would change its own bounds every frame. AS3 draws the view as a separate
        /// child object for the same reason (<c>Bullet.as:158</c> builds <c>vis</c> alongside
        /// <c>this</c>), and the spin below is applied to the child.</para>
        /// </summary>
        private void EnsureVisualRenderer()
        {
            if (_visualRenderer != null)
            {
                if (_visualTransform == null) _visualTransform = _visualRenderer.transform;
                return;
            }

            _visualRenderer = GetComponentInChildren<SpriteRenderer>();
            if (_visualRenderer != null)
            {
                _visualTransform = _visualRenderer.transform;
                return;
            }

            var visualObject = new GameObject(VisualChildName);
            _visualTransform = visualObject.transform;
            _visualTransform.SetParent(transform, false);

            _visualRenderer         = visualObject.AddComponent<SpriteRenderer>();
            _visualRenderer.enabled = false;

            // The project's "Default" sorting layer sits BELOW the map tiles, so a renderer left on it
            // draws behind the floor. Fall back to the projectile's own layer when the serialized field
            // is empty — which is what a prefab authored before this field existed deserialises to.
            _visualRenderer.sortingLayerName = string.IsNullOrEmpty(_visualSortingLayer)
                ? MapSortingLayers.Foreground
                : _visualSortingLayer;
        }

        /// <summary>
        /// Runs the fuse flicker — AS3 <c>b.vis.play()</c> (<c>WThrow.as:194</c>).
        ///
        /// <para><b>The whole timeline loops, not a shoot/reload clip.</b> A throwable's
        /// <c>WeaponVisualDefinition</c> is the weapon's <i>held</i> art, whose frames are a lit fuse
        /// (5 frames on all but <c>visacidgr</c>, which has 1), and AS3 plays the symbol's own
        /// timeline at the SWF rate. So the importer's shoot/reload split — which exists for guns —
        /// does not describe this animation and is not consulted.</para>
        ///
        /// <para>The advance is clamped to one full cycle per frame so a long hitch (or the first
        /// frame after a load, where <c>deltaTime</c> can be large) cannot spin the loop for an
        /// unbounded number of iterations.</para>
        /// </summary>
        private void UpdateThrownAnimation()
        {
            if (_currentVisual == null || _visualRenderer == null || !_visualRenderer.enabled) return;

            var frames = _currentVisual.frames;
            if (frames == null || frames.Length <= 1) return;   // a still sprite has nothing to advance

            _visualFrameTimer += Time.deltaTime;

            float frameDuration = 1f / SimClock.FramesPerSecond;
            if (_visualFrameTimer < frameDuration) return;

            int advance = Mathf.Min(Mathf.FloorToInt(_visualFrameTimer / frameDuration), frames.Length);
            _visualFrameTimer -= advance * frameDuration;
            _visualFrameIndex  = (_visualFrameIndex + advance) % frames.Length;

            _visualRenderer.sprite = frames[_visualFrameIndex];
        }

        /// <summary>
        /// Spins the view — AS3 <c>PhisBullet.as:100 vis.rotation += this.dr</c>.
        ///
        /// <para>Reads the live flight state but never writes it, and writes only the <b>child's</b>
        /// local rotation. The two source fields differ per path because the flip keeps its position
        /// and velocity in AS3 units (<see cref="ThrownObjectState"/>) while the legacy path keeps
        /// them in Unity units, so the legacy read converts once — the same conversion, and the same
        /// direction, as <see cref="RegisterOnSimLoop"/>.</para>
        /// </summary>
        private void UpdateThrownSpin()
        {
            if (_visualTransform == null) return;

            bool  resting = FlipActive ? _simState.Stay : _stay;
            float dxNowPxPerFrame = FlipActive
                ? _simState.VelocityPxPerFrame.x
                : _velocity.x / ProjectilePhysicsMath.VelocityScale;

            _spinDegrees += ProjectilePhysicsMath.ThrownSpinDeltaDegrees(
                _sticky, resting, _spinDxAtSpawnPx, dxNowPxPerFrame);

            _visualTransform.localRotation = Quaternion.Euler(0f, 0f, _spinDegrees);
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
            // The contact axis, from the object's position relative to the collider's centre.
            //
            // MEASURED 10-04, and the measurement says this test is NOT the defect the class note used
            // to blame it for. Sweeping every point on the contact circle around one 40 px tile
            // (18 304 points, radius 0.5 as per Throwable.prefab) a true circle-vs-AABB normal
            // classifier disagrees with this test at exactly ONE point — a corner tie. On a flat floor
            // it disagrees nowhere: a floor contact puts the centre ~0.7 units from the tile centre
            // vertically, which no reachable horizontal offset (max 0.2 for the tile beneath) can
            // exceed. So a floor contact was never read as a wall, and swapping in a normal-based
            // classifier changes nothing measurable. See the class note for where the real divergence
            // from AS3 lies.
            Vector2 pos    = transform.position;
            Vector2 center = other.bounds.center;
            Vector2 diff   = pos - (Vector2)center;

            bool hitHorizontal = Mathf.Abs(diff.x) > Mathf.Abs(diff.y);

            // AS3 plays the landing sound FIRST in every contact branch — before `if(bumc)
            // popadalo()` and before the bounce rewrites the velocity (PhisBullet.as:244-248) — so a
            // contact detonation sounds on its way into the blast rather than instead of it. `_velocity`
            // is units/s here and the oracle's volume is |d|/10 with `d` in px/frame, hence the one
            // conversion. The legacy path cannot tell a floor bounce from a settle the way the oracle
            // does, so it sounds on any contact; see the class note on this path's known roughness.
            float axisSpeedPxPerFrame =
                (hitHorizontal ? Mathf.Abs(_velocity.x) : Mathf.Abs(_velocity.y))
                / ProjectilePhysicsMath.VelocityScale;
            PlayContactSound(axisSpeedPxPerFrame);

            if (_bumc)
            {
                Detonate();
                return;
            }

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

        /// <summary>
        /// Fires the blast, and either releases the object or starts its multi-pulse train.
        /// </summary>
        /// <remarks>
        /// <para><b>AS3 <c>PhisBullet.run</c> at <c>liv == 3</c> (<c>:122-128</c>) and
        /// <c>popadalo</c> (<c>:190-204</c>) both funnel into the inherited
        /// <c>explosion() → explRun() → explVis()</c> chain.</b> <c>explosion()</c> runs one pulse
        /// immediately and, when <c>explKol &gt; 0</c>, sets <c>expl_t = (explKol - 1) * explPeriod</c>
        /// (<c>Bullet.as:683-691</c>) — the object then lives on, detonating every
        /// <c>explPeriod</c> frames until the countdown runs out. That train is the whole of the
        /// owner-reported "damage area", and <see cref="ExplosionPulseRules"/> owns its schedule.</para>
        ///
        /// <para><b>Two behaviour changes against the old single-pulse version.</b> First, the guard is
        /// <see cref="_detonated"/> rather than <c>_isInitialized</c>: the latter has to stay true
        /// through the train, or the object would stop ticking after pulse 0. Second, a detonation with
        /// a train <b>freezes the object</b> at the blast point instead of releasing it. Freezing is
        /// exactly what AS3's <c>popadalo</c> does (<c>dx = dy = 0</c>, <c>:194</c>) and is
        /// indistinguishable for a fuse detonation in practice, because a grenade that has bounced and
        /// settled is already at rest when its fuse expires.</para>
        /// </remarks>
        public void Detonate()
        {
            if (_detonated) return;
            _detonated = true;

            if (_explRadius > 0f)
            {
                RunPulse(0);

                if (ExplosionPulseRules.HasSustainedPhase(_explKol))
                {
                    _pulseActive   = true;
                    _pulsesElapsed = 0;
                    _velocity      = Vector2.zero;
                    if (_rb != null) _rb.linearVelocity = Vector2.zero;
                    return;
                }
            }

            _pulseActive = false;
            ReturnToPool();
        }

        /// <summary>
        /// Runs one pulse of the blast: the damage it deals, then the presentation it draws — AS3
        /// <c>explRun()</c> (<c>weapon/Bullet.as:705-720</c>), which calls <c>explBlast()</c> and/or
        /// <c>explGas()</c> and then always <c>explVis()</c>.
        /// </summary>
        /// <param name="pulseIndex">
        /// 0-based pulse number. Decides the <i>shape</i> (<see cref="ExplosionPulseRules.ShapeFor"/> —
        /// an <c>explTip == 3</c> blast opens with a blast and continues as gas) and whether the
        /// first-pulse-only presentation runs.
        /// </param>
        /// <remarks>
        /// <para><b>Damage before presentation, and both before the release.</b> That is the oracle's
        /// order and it matters for the train: the last pulse must land before the object is returned to
        /// the pool, or the final detonation would be dropped.</para>
        ///
        /// <para><b>A pulse with no shape deals no damage but still draws.</b> AS3's two
        /// <c>if</c>s in <c>explRun</c> are independent of <c>explVis</c>, which always runs — so an
        /// <c>explTip</c> outside 1/2/3 still emits its visuals. No shipped weapon is in that state.</para>
        /// </remarks>
        private void RunPulse(int pulseIndex)
        {
            Vector3 centre = transform.position;

            if (ExplosionPulseRules.DamagesAt(_explTip, pulseIndex))
            {
                Collider2D[] hits = Physics2D.OverlapCircleAll(centre, _explRadius);
                foreach (var hit in hits)
                {
                    if (hit.isTrigger) continue;

                    var damageable = hit.GetComponent<IDamageable>();
                    if (damageable == null || !damageable.IsAlive) continue;

                    if (_hasDamageContext && _damageSystem != null)
                    {
                        // Report, do not resolve. The per-target faction multiplier is computed here
                        // because it needs this collider; the falloff and the formula live in
                        // DamageSystem.
                        _damageSystem.Report(PendingDamage.Explosion(
                            _damageContext, damageable,
                            hit.transform.position, centre, _explRadius,
                            factionMultiplier: ExplosionMultiplierFor(hit)));
                    }
                    else
                    {
                        damageable.TakeDamage(10f);
                    }
                }
            }

            // The blast's visuals and sound, as a SIBLING of the damage loop — AS3 runs them last
            // within the same routine (`explRun` → `explVis`, `Bullet.as:705-720`), which is exactly
            // this position. Kept out of the loop so one blast emits once, not once per target.
            EmitExplosionVisuals(centre, ExplosionPulseRules.IsInitialPulse(pulseIndex));
        }

        /// <summary>
        /// Plays the landing sound for one tile contact — AS3 <c>Snd.ps(this.sndHit, X, Y, 0,
        /// Math.abs(d / 10))</c> at <c>PhisBullet.as:246/267/290/321/336</c>, where <c>sndHit</c> is
        /// the weapon's <c>&lt;snd fall&gt;</c> (<c>WThrow.as:196</c>).
        ///
        /// <para><b>Two callers, one for each integration path.</b> The flipped path reads
        /// <see cref="ThrownObjectState.ContactSoundSpeedPxPerFrame"/>, which
        /// <see cref="ThrownObjectPhysics.Run"/> fills at the five oracle sites; the legacy path calls
        /// it from <see cref="HandleSurfaceCollision"/>. Both pass a <b>px/frame</b> speed, because
        /// that is the unit the oracle's <c>/10</c> divisor assumes — see
        /// <see cref="ThrownContactSound.VolumeScale"/>.</para>
        ///
        /// <para><b>Silent when the weapon has no <c>fall</c></b>, which is most of them: only thrown
        /// weapons carry the attribute. There is deliberately no generic fallback thud — AS3 plays
        /// nothing, and a substitute would be a sound the game never made.</para>
        /// </summary>
        private void PlayContactSound(float axisSpeedPxPerFrame)
        {
            if (!ThrownContactSound.ShouldPlay(_soundFall)) return;

            _soundService?.Play(_soundFall, transform.position,
                ThrownContactSound.VolumeScale(axisSpeedPxPerFrame));
        }

        /// <summary>
        /// Emits the blast's particles and plays its sound — the port's mirror of AS3
        /// <c>Bullet.explVis()</c> (<c>weapon/Bullet.as:878-1000</c>), reached by a thrown object through
        /// the inherited <c>explosion() → explRun() → explVis()</c> chain (<c>PhisBullet.as:122,196</c>).
        ///
        /// <para><b>Why this lives here and not in the damage loop.</b> The damage AoE is per-target; the
        /// blast is not. AS3 draws it once, after the damage, from the same routine — and the visual is a
        /// property of the impact point, not of any one victim.</para>
        ///
        /// <para><b>Same seam as <see cref="Projectile"/>.</b> The table itself is Unity-free
        /// (<see cref="PFE.Systems.Particles.ExplosionVisualRules.Plan"/>, pinned by fixtures), so this
        /// method only converts the position and forwards the emits. <c>visExpl</c> wins over the
        /// damage-type table; <c>inWater</c> is derived at the impact point rather than stored, because a
        /// ported thrown object has no <c>inWater</c> flag to refresh each tick the way AS3's bullet does.
        /// </para>
        ///
        /// <para><b>Silent by design for most damage types.</b> The table has no default arm, so a thrown
        /// object with neither a <c>visexpl</c> nor a table arm emits nothing and plays nothing — which is
        /// AS3's own behaviour, not a fallback.</para>
        ///
        /// <para><b><paramref name="isInitialPulse"/> is AS3's <c>expl_t == 0</c>.</b> The venom and pink
        /// sounds, the acid arm and the fire arm are all gated on it, so a sustained blast draws them on
        /// its first pulse only while a gas cloud keeps puffing on every one.</para>
        /// </summary>
        private void EmitExplosionVisuals(Vector3 centre, bool isInitialPulse)
        {
            if (_particles == null) return;

            // No room pushed ⇒ no origin and no height ⇒ no correct position. Refusing is the point of
            // the adapter's contract: emitting anyway would place the blast somewhere plausible and wrong.
            if (!_particles.TryToAs3Local(centre, out Vector2 as3Local)) return;

            int water = _particleTileWater != null
                ? _particleTileWater.WaterAt(as3Local.x, as3Local.y)
                : 0;

            if (!PFE.Systems.Particles.ExplosionVisualRules.Plan(
                    _visExpl, _damageType, water > 0, isInitialPulse,
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
            _sticky           = false;

            // A recycled throwable must not inherit the previous shot's blast, or a sparkle grenade
            // would make the next plain one emit sparkle visuals. Same rule as Projectile.ResetForPool.
            _visExpl          = null;
            _damageType       = DamageType.PhysicalBullet;

            // Same reason, for the landing sound: a recycled object that kept the last weapon's
            // `<snd fall>` would thud with the wrong material for whatever is thrown next.
            _soundFall        = null;

            // And the blast's shape and train. A recycled object that kept `explKol` would run a
            // phantom multi-pulse blast on its next throw — and, worse, would stay alive for three
            // seconds after detonating with nothing to detonate.
            _explTip          = 1;
            _explKol          = 0;
            _detonated        = false;
            _pulseActive      = false;
            _pulsesElapsed    = 0;
            _pendingPulse     = -1;

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

            // View state. `_currentVisual` is deliberately NOT cleared — the next shot re-applies its
            // own — but the accumulated rotation must be, or a reused instance would start its flight
            // already spun up and mid-fuse-flicker.
            _spinDegrees      = 0f;
            _spinDxAtSpawnPx  = 0f;
            _visualFrameTimer = 0f;
            _visualFrameIndex = 0;
            if (_visualTransform != null)
                _visualTransform.localRotation = Quaternion.identity;

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
