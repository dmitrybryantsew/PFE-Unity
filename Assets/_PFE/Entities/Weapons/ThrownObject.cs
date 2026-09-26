using UnityEngine;
using VContainer;
using PFE.Systems.Combat;
using PFE.Systems.Weapons;
using PFE.Core.Messages;
using MessagePipe;
using System;
using PFE.Systems.Map.TileQuery;

namespace PFE.Entities.Weapons
{
    /// <summary>
    /// Physics-based thrown projectile — grenades, bottles, sticky bombs.
    /// Mirrors PhisBullet.as from AS3.
    ///
    /// Physics model: Kinematic Rigidbody2D, manual velocity integration matching AS3:
    ///   dx += ddx  (no horizontal accel for thrown objects)
    ///   dy += ddy  (gravity each frame)
    ///   x  += dx;  y  += dy
    ///
    /// Bounce: on solid tile collision, flip velocity component × skok.
    ///   If dy < 2 on floor hit → settle (stay = true, dy = 0).
    ///   Sliding friction: dx approaches 0 at brake rate while stay == true.
    ///
    /// bumc=true: detonate on any solid contact (no bounce).
    ///
    /// Fuse: FuseFrames countdown at 30fps → Detonate() when reaches 0.
    ///
    /// Pooling: OnReturnToPool callback set by ThrownObjectPool.
    /// </summary>
    [RequireComponent(typeof(Rigidbody2D), typeof(Collider2D))]
    public sealed class ThrownObject : MonoBehaviour
    {
        // ── Constants ─────────────────────────────────────────────────────────

        private const float FlashFps = 30f;

        /// <summary>
        /// Gravity as a per-second acceleration, in Unity units/s².
        ///
        /// <para>AS3 <c>WThrow.as:240</c> <c>trasser.ddy = World.ddy</c> (and <c>1651-1653</c>
        /// <c>trasser.ddy += World.ddy</c> when the weapon's <c>grav</c> is set), so the value is
        /// exactly <c>World.ddy</c> = 1 px/frame² (<c>World.as:46</c>) — not the 0.6 the old
        /// literal assumed, which made this 2× too strong. Converted by the canonical
        /// px/frame² → units/s² factor; see <c>REPLICA_BEHAVIOR_CONTRACT.md</c> §6.</para>
        /// </summary>
        private const float GravityPerSec = TileQueryConstants.GravityUnitsPerSecondSquared;

        // ── Physics params (set from ShotPlan at spawn) ───────────────────────

        private Vector2 _velocity;

        // Bounce retention and floor damping. AS3's effective values for a thrown object come from
        // WThrow's own fields, which OVERRIDE the bullet class's defaults (PhisBullet.as:23/25 say
        // 0.5/0.7, but WThrow.as:188/189 assign 0.4/0.6). See ProjectilePhysicsMath for citations.
        private float   _skok      = ProjectilePhysicsMath.ThrowBounceRetention;
        private float   _tormoz    = ProjectilePhysicsMath.ThrowFloorDamping;

        /// <summary>
        /// Sliding friction while resting, in units/s².
        ///
        /// <para>AS3 <c>Trasser.as:78/82</c> applies <c>dx -= brake</c> once per 30 Hz frame, so
        /// <c>brake</c> (2 px/frame, <c>Trasser.as:45</c>) is a px/frame² acceleration and converts
        /// by the squared-frame-rate factor → 18 units/s². The old
        /// <c>brake / FlashFps * 60f</c> (= 4) was neither idiom and left friction 4.5× too weak.</para>
        /// </summary>
        private float   _brake     = ProjectilePhysicsMath.SlidingFriction(ProjectilePhysicsMath.BrakePxPerFrame2);
        private bool    _bumc;               // detonate on contact
        private bool    _stay;               // resting on floor

        // ── Fuse ──────────────────────────────────────────────────────────────

        private float _fuseTimer;            // seconds remaining before detonation
        private bool  _armed;

        // ── Damage ────────────────────────────────────────────────────────────

        private bool          _hasDamageContext;
        private DamageContext _damageContext;
        private float         _explRadius;

        // ── Runtime ───────────────────────────────────────────────────────────

        private Rigidbody2D _rb;
        private bool        _isInitialized;

        // ── Injected ─────────────────────────────────────────────────────────

#pragma warning disable CS0649
        [Inject] private IPublisher<DamageDealtMessage> _damageDealtPublisher;
#pragma warning restore CS0649

        public Action<ThrownObject> OnReturnToPool { get; set; }

        // ── Initialization ────────────────────────────────────────────────────

        private void Awake()
        {
            _rb = GetComponent<Rigidbody2D>();
            _rb.bodyType              = RigidbodyType2D.Kinematic;
            _rb.gravityScale          = 0f;
            _rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
        }

        /// <summary>
        /// Called by ProjectileSpawner from a ThrownObject ShotPlan.
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
            _velocity      = initialVelocity;
            _fuseTimer     = fuseFrames / FlashFps;
            _explRadius    = explRadius;
            _bumc          = bumc;
            _skok          = skok;
            _tormoz        = tormoz;
            _brake         = ProjectilePhysicsMath.SlidingFriction(brake);
            _stay          = false;
            _armed         = true;
            _isInitialized = true;

            _rb.linearVelocity = _velocity;
        }

        public void SetDamageContext(DamageContext ctx)
        {
            _damageContext    = ctx;
            _hasDamageContext = true;
        }

        // ── Unity lifecycle ───────────────────────────────────────────────────

        private void FixedUpdate()
        {
            if (!_isInitialized) return;

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

        private void OnTriggerEnter2D(Collider2D other)
        {
            if (!_isInitialized || other.isTrigger) return;

            // Damageable — detonate on contact if bumc, otherwise ignore (fuse handles it).
            var dmg = other.GetComponent<IDamageable>();
            if (dmg != null)
            {
                if (_bumc) Detonate();
                return;
            }

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
                    // Hitting floor. AS3 Trasser.as:204 bounces only while `dy > 2` px/frame; below
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
                                _damageDealtPublisher);
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
            _explRadius       = 0f;
            _bumc             = false;

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
    }
}
