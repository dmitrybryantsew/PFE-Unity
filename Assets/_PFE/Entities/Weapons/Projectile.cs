using UnityEngine;
using VContainer;
using PFE.Systems.Audio;
using PFE.Systems.Combat;
using PFE.Systems.Weapons;
using PFE.Data.Definitions;
using PFE.Core.Messages;
using MessagePipe;
using System;
using System.Text;
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
    /// </summary>
    [RequireComponent(typeof(Rigidbody2D))]
    public class Projectile : MonoBehaviour
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
        ///   gravityScale → ddy per flash-frame from gravity (0 = no gravity)
        ///   accel        → ddx/ddy per flash-frame forward thrust (rockets)
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

            // ── Per-second acceleration components ────────────────────────────
            // AS3 worked in pixels/frame at 30fps. We keep frame-cadence by
            // computing per-frame deltas then multiplying by FlashFps for Unity.

            // Gravity: ddy += World.ddy * grav each flash frame.
            // World.ddy ≈ 0.6 px/frame² in AS3.  Unity: negate Y (screen→world).
            _ddy = -gravityScale * 0.6f * FlashFps;  // units/s² downward

            // Flame lift overrides gravity (ddy -= liftAmount each frame).
            if (flame == 1)
            {
                _ddy          = 0.8f * FlashFps;      // strong upward
                _lifetimeTimer = FlameLifetime1;
            }
            else if (flame == 2)
            {
                _ddy          = 0.2f * FlashFps;      // weak upward
                _lifetimeTimer = FlameLifetime2;
            }
            else
            {
                _lifetimeTimer = DefaultLifetime;
            }

            // Accel: forward thrust along aim dir.
            // AS3: dx += cos(rot)*accel, dy += sin(rot)*accel each frame.
            _ddx = dir.x * accel * FlashFps;
            _ddy += dir.y * accel * FlashFps;   // add to existing vertical (gravity+flame already set)

            _rb.linearVelocity = _velocity;
            _rb.angularVelocity = 0f;
            _rb.simulated = true;
            _rb.WakeUp();

            float angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
            transform.rotation = Quaternion.Euler(0f, 0f, angle);
            _rb.rotation       = angle;

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

        private void FixedUpdate()
        {
            if (!_isInitialized) return;

            float dt = Time.fixedDeltaTime;

            // ── Manual velocity integration (AS3: dx+=ddx, dy+=ddy, x+=dx, y+=dy) ──
            _velocity.x += _ddx * dt;
            _velocity.y += _ddy * dt;

            // ── Homing (navod) ────────────────────────────────────────────────
            if (_navod > 0f)
                ApplyHoming(dt);

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

        private void Update()
        {
            if (!_isInitialized) return;
            UpdateVisualAnimation();
        }

        private void OnTriggerEnter2D(Collider2D other)
        {
            if (!_isInitialized) return;
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
        /// </summary>
        private void ApplyHoming(float dt)
        {
            // Find nearest IDamageable in scene (simple OverlapCircle approach).
            // navod strength controls how sharply we turn per second.
            Collider2D[] hits = Physics2D.OverlapCircleAll(transform.position, 20f);
            Transform best = null;
            float bestDist = float.MaxValue;

            foreach (var hit in hits)
            {
                if (hit.isTrigger) continue;
                var dmg = hit.GetComponent<IDamageable>();
                if (dmg == null || !dmg.IsAlive) continue;

                // Skip the owner if we ever track it — for now skip same-layer objects.
                float d = Vector2.Distance(transform.position, hit.transform.position);
                if (d < bestDist)
                {
                    bestDist = d;
                    best     = hit.transform;
                }
            }

            if (best == null) return;

            Vector2 toTarget = ((Vector2)best.position - (Vector2)transform.position).normalized;
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
                if (_piercing > 0f && UnityEngine.Random.value < _piercing)
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
                ReturnToPool();
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
