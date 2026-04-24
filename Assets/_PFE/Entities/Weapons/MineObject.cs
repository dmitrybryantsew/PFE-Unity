using System.Collections.Generic;
using UnityEngine;
using VContainer;
using PFE.Systems.Combat;
using PFE.Systems.Weapons;
using PFE.Core.Messages;
using MessagePipe;
using System;

namespace PFE.Entities.Weapons
{
    /// <summary>
    /// Placed mine — stationary trigger that arms after a delay then detonates on proximity.
    /// Mirrors Mine.as from AS3.
    ///
    /// Lifecycle:
    ///   1. Placed at thrower's feet by ProjectileSpawner.
    ///   2. Arming delay (reloadTime in AS3, default 75 frames ~2.5s).
    ///      During this time the mine is visible but not active.
    ///   3. After armed: proximity trigger activates countdown.
    ///   4. Countdown reaches 0 → Detonate().
    ///
    /// Radio detonation:
    ///   MineObject.DetonateAll(weaponId) is called by ProjectileSpawner when it
    ///   receives a detonation ShotPlan (Kind=Mine, FuseFrames=0, IsMine=false).
    ///   All live mines matching that weaponId immediately start their explTime countdown.
    ///
    /// Static registry:
    ///   All active MineObjects register themselves so DetonateAll can find them
    ///   without a scene search.
    /// </summary>
    [RequireComponent(typeof(Collider2D))]
    public sealed class MineObject : MonoBehaviour
    {
        // ── Static registry for radio detonation ─────────────────────────────

        private static readonly List<MineObject> _allMines = new();

        /// <summary>
        /// Radio detonation: activate all armed mines with the given weapon ID.
        /// Called by ProjectileSpawner when a detonation ShotPlan is received.
        /// AS3: detonator() iterates loc.units, calls mine.activate().
        /// </summary>
        public static void DetonateAll(string weaponId)
        {
            foreach (var mine in _allMines)
            {
                if (mine != null && mine._weaponId == weaponId && mine._isArmed)
                    mine.Activate();
            }
        }

        // ── Constants ─────────────────────────────────────────────────────────

        private const float FlashFps      = 30f;
        private const float ArmingFrames  = 75f;
        private const float ProximityRange = 0.5f;  // Unity units; AS3 uses collision overlap

        // ── Configuration (set at spawn) ──────────────────────────────────────

        private string  _weaponId;
        private float   _explRadius;
        private float   _armingTimer;    // seconds until armed
        private float   _explTimer;      // seconds until detonation once activated

        // ── State machine ─────────────────────────────────────────────────────

        private enum MineState { Arming, Armed, Countdown, Detonated }
        private MineState _state = MineState.Arming;

        private bool _isArmed => _state == MineState.Armed || _state == MineState.Countdown;

        // ── Damage ────────────────────────────────────────────────────────────

        private bool          _hasDamageContext;
        private DamageContext _damageContext;

        // ── Injected ─────────────────────────────────────────────────────────

#pragma warning disable CS0649
        [Inject] private IPublisher<DamageDealtMessage> _damageDealtPublisher;
#pragma warning restore CS0649

        public Action<MineObject> OnReturnToPool { get; set; }

        // ── Initialization ────────────────────────────────────────────────────

        private void OnEnable()  => _allMines.Add(this);
        private void OnDisable() => _allMines.Remove(this);

        /// <summary>Called by ProjectileSpawner when a Mine ShotPlan is received.</summary>
        public void Initialize(string weaponId, float explRadius, int fuseFrames, int armingFrames = 75)
        {
            _weaponId    = weaponId;
            _explRadius  = explRadius;
            _armingTimer = armingFrames / FlashFps;
            _explTimer   = fuseFrames / FlashFps * 0.3f;   // AS3: explTime *= 0.3 after radio
            _state       = MineState.Arming;
        }

        public void SetDamageContext(DamageContext ctx)
        {
            _damageContext    = ctx;
            _hasDamageContext = true;
        }

        // ── Unity lifecycle ───────────────────────────────────────────────────

        private void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;

            switch (_state)
            {
                case MineState.Arming:
                    _armingTimer -= dt;
                    if (_armingTimer <= 0f)
                        _state = MineState.Armed;
                    break;

                case MineState.Armed:
                    // Proximity check — activate if enemy enters range.
                    CheckProximity();
                    break;

                case MineState.Countdown:
                    _explTimer -= dt;
                    if (_explTimer <= 0f)
                        Detonate();
                    break;

                case MineState.Detonated:
                    break;
            }
        }

        private void OnTriggerEnter2D(Collider2D other)
        {
            if (_state != MineState.Armed) return;
            if (other.isTrigger) return;

            var dmg = other.GetComponent<IDamageable>();
            if (dmg != null && dmg.IsAlive)
                Activate();
        }

        // ── Activation ────────────────────────────────────────────────────────

        /// <summary>
        /// Begin countdown to detonation.
        /// AS3 Mine.activate(): aiState = 2, vis.play().
        /// </summary>
        public void Activate()
        {
            if (_state == MineState.Detonated) return;
            _state = MineState.Countdown;
        }

        // ── Detonation ────────────────────────────────────────────────────────

        public void Detonate()
        {
            if (_state == MineState.Detonated) return;
            _state = MineState.Detonated;

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

        // ── Helpers ───────────────────────────────────────────────────────────

        private void CheckProximity()
        {
            Collider2D[] hits = Physics2D.OverlapCircleAll(transform.position, ProximityRange);
            foreach (var hit in hits)
            {
                if (hit.isTrigger) continue;
                var dmg = hit.GetComponent<IDamageable>();
                if (dmg != null && dmg.IsAlive)
                {
                    Activate();
                    return;
                }
            }
        }

        private void ReturnToPool()
        {
            if (OnReturnToPool != null) OnReturnToPool(this);
            else Destroy(gameObject);
        }

        public void ResetMine()
        {
            _state            = MineState.Arming;
            _hasDamageContext = false;
            _armingTimer      = 0f;
            _explTimer        = 0f;
            _explRadius       = 0f;
            _weaponId         = null;
        }

        // ── Debug ─────────────────────────────────────────────────────────────

        private void OnDrawGizmos()
        {
            if (_explRadius <= 0f) return;
            Gizmos.color = _state == MineState.Armed
                ? new Color(1f, 1f, 0f, 0.2f)
                : new Color(1f, 0.2f, 0f, 0.3f);
            Gizmos.DrawWireSphere(transform.position, _explRadius);
            Gizmos.color = new Color(0f, 1f, 0f, 0.15f);
            Gizmos.DrawWireSphere(transform.position, ProximityRange);
        }
    }
}
