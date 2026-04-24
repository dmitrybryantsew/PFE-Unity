using System.Collections.Generic;
using UnityEngine;
using VContainer;
using PFE.Core;
using PFE.Systems.Combat;
using PFE.Data.Definitions;
using PFE.Entities.Weapons;

namespace PFE.Systems.Weapons
{
    /// <summary>
    /// Reads ShotPlans produced by the active IWeaponController each tick and
    /// spawns the corresponding scene objects.
    ///
    /// Routing:
    ///   Projectile   → IProjectileFactory (pooled Projectile MonoBehaviour)
    ///   ThrownObject → ThrownObjectPrefab (pooled ThrownObject MonoBehaviour)
    ///   Mine         → MinePrefab (pooled MineObject MonoBehaviour)
    ///                  Special case: FuseFrames==0 && IsMine==false → radio detonation signal
    ///   Hitscan      → TODO Stage 3+ (raycast, direct DamageResolver call)
    ///   MeleeSweep   → ignored here (handled by MeleeHitVolume trigger)
    ///
    /// Called explicitly by PlayerWeaponLoadout.FixedUpdate — not via Unity messages.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ProjectileSpawner : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────────

        [Header("Thrown / Mine Prefabs")]
        [SerializeField]
        [Tooltip("Prefab with ThrownObject component. Required for throwTip==0/2 weapons.")]
        private ThrownObject _thrownObjectPrefab;

        [SerializeField]
        [Tooltip("Prefab with MineObject component. Required for throwTip==1 weapons.")]
        private MineObject _minePrefab;

        // ── Dependencies ──────────────────────────────────────────────────────

        private IProjectileFactory _factory;
        private IObjectResolver    _resolver;
        private WeaponDefinition   _currentDef;
        private PfeDebugSettings   _debugSettings;

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>Initialize with DI dependencies. Called by PlayerWeaponLoadout.Start().</summary>
        public void Initialize(IProjectileFactory factory, IObjectResolver resolver = null, PfeDebugSettings debugSettings = null)
        {
            _factory  = factory;
            _resolver = resolver;
            _debugSettings = debugSettings;
        }

        /// <summary>Called by PlayerWeaponLoadout after Equip().</summary>
        public void SetWeapon(WeaponDefinition def)
        {
            _currentDef = def;
            if (_debugSettings?.LogProjectileSpawning == true && def != null)
                Debug.Log($"[ProjectileSpawner] SetWeapon('{def.weaponId}') archetype={def.projectileArchetype} visual='{def.projectileVisual?.name ?? "null"}'.");
        }

        /// <summary>
        /// Spawn objects for all plans. Called from PlayerWeaponLoadout.FixedUpdate.
        /// </summary>
        public void SpawnFromPlans(IReadOnlyList<ShotPlan> plans)
        {
            if (_currentDef == null)
            {
                if (_debugSettings?.LogProjectileSpawning == true)
                    Debug.LogWarning("[ProjectileSpawner] SpawnFromPlans called with no current weapon.");
                return;
            }

            if (plans.Count == 0) return;

            if (_debugSettings?.LogProjectileSpawning == true)
                Debug.Log($"[ProjectileSpawner] SpawnFromPlans weapon='{_currentDef.weaponId}' count={plans.Count}.");

            foreach (var plan in plans)
                SpawnOne(plan);
        }

        // ── Internal ──────────────────────────────────────────────────────────

        private void SpawnOne(ShotPlan plan)
        {
            switch (plan.Kind)
            {
                case ShotKind.Projectile:
                    SpawnProjectile(plan);
                    break;

                case ShotKind.ThrownObject:
                    SpawnThrownObject(plan);
                    break;

                case ShotKind.Mine:
                    SpawnMineOrDetonate(plan);
                    break;

                case ShotKind.Hitscan:
                    // TODO Stage 3+: cast ray, call DamageResolver directly.
                    Debug.Log("[ProjectileSpawner] Hitscan not yet implemented.");
                    break;

                case ShotKind.MeleeSweep:
                    // MeleeHitVolume handles its own trigger — nothing to spawn here.
                    break;
            }
        }

        private void SpawnProjectile(ShotPlan plan)
        {
            if (_factory == null)
            {
                Debug.LogWarning("[ProjectileSpawner] SpawnProjectile aborted because IProjectileFactory is null.");
                return;
            }

            Vector2 dir      = new Vector2(Mathf.Cos(plan.AngleRad), Mathf.Sin(plan.AngleRad));
            Vector3 spawnPos = new Vector3(plan.WorldPosition.x, plan.WorldPosition.y, 0f);

            if (_debugSettings?.LogProjectileSpawning == true)
            {
                Debug.Log(
                    $"[ProjectileSpawner] Spawning projectile weapon='{_currentDef.weaponId}' " +
                    $"origin={plan.Origin} kind={plan.Kind} pos={spawnPos} dir={dir} speed={plan.Speed:0.###}.");
            }

            Projectile proj = _factory.Create(_currentDef, spawnPos, dir);
            if (proj == null)
            {
                if (_debugSettings?.LogProjectileSpawning == true)
                    Debug.LogWarning($"[ProjectileSpawner] Factory returned null for weapon '{_currentDef.weaponId}'.");
                return;
            }

            proj.SetDamageContext(plan.Damage);
        }

        private void SpawnThrownObject(ShotPlan plan)
        {
            if (_thrownObjectPrefab == null)
            {
                Debug.LogWarning("[ProjectileSpawner] ThrownObject prefab not assigned.");
                return;
            }

            Vector3 spawnPos = new Vector3(plan.WorldPosition.x, plan.WorldPosition.y, 0f);
            ThrownObject obj = Instantiate(_thrownObjectPrefab, spawnPos, Quaternion.identity);

            _resolver?.Inject(obj);

            obj.Initialize(
                initialVelocity: plan.ThrowVelocity,
                fuseFrames:      plan.FuseFrames,
                explRadius:      _currentDef.explRadius / 100f,
                bumc:            _currentDef.isPhysBullet,  // bumc reuses isPhysBullet flag for now
                skok:            0.5f,
                tormoz:          0.7f,
                brake:           2f);

            obj.SetDamageContext(plan.Damage);
        }

        private void SpawnMineOrDetonate(ShotPlan plan)
        {
            // Radio detonation signal: FuseFrames==0 && IsMine==false.
            if (plan.FuseFrames == 0 && !plan.IsMine)
            {
                // AS3: detonator() calls activate() on all matching mines.
                MineObject.DetonateAll(_currentDef.weaponId);
                return;
            }

            // New mine placement.
            if (_minePrefab == null)
            {
                Debug.LogWarning("[ProjectileSpawner] Mine prefab not assigned.");
                return;
            }

            Vector3 spawnPos = new Vector3(plan.WorldPosition.x, plan.WorldPosition.y, 0f);
            MineObject mine  = Instantiate(_minePrefab, spawnPos, Quaternion.identity);

            _resolver?.Inject(mine);

            mine.Initialize(
                weaponId:     _currentDef.weaponId,
                explRadius:   _currentDef.explRadius / 100f,
                fuseFrames:   plan.FuseFrames,
                armingFrames: 75);

            mine.SetDamageContext(plan.Damage);
        }
    }
}
