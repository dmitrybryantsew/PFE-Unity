using UnityEngine;
using VContainer;
using PFE.Core;
using PFE.Data.Definitions;
using PFE.Entities.Weapons;
using PFE.Core.Pooling;
using System.Collections.Generic;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// Creates and pools projectiles.
    /// Uses <see cref="ProjectilePrefabRegistry"/> to resolve the prefab from
    /// the weapon's <see cref="ProjectileArchetype"/>; callers never hold prefab refs.
    /// </summary>
    public class ProjectileFactory : IProjectileFactory
    {
        private readonly IObjectResolver          _resolver;
        private readonly ProjectilePrefabRegistry _registry;
        private readonly Dictionary<Projectile, GameObjectPool<Projectile>> _pools;
        private readonly PfeDebugSettings _debugSettings;
        private Transform _poolRoot;

        private const int DefaultPoolSize = 20;
        private const int MaxPoolSize     = 100;

        public ProjectileFactory(IObjectResolver resolver, ProjectilePrefabRegistry registry, PfeDebugSettings debugSettings = null)
        {
            _resolver      = resolver;
            _registry      = registry;
            _debugSettings = debugSettings;
            _pools         = new Dictionary<Projectile, GameObjectPool<Projectile>>();
        }

        // ── IProjectileFactory ───────────────────────────────────────────────

        /// <summary>
        /// Primary path: spawn and fully initialize from a WeaponDefinition.
        /// Resolves prefab by archetype, converts AS3 speeds, passes all hit data.
        /// </summary>
        public Projectile Create(WeaponDefinition weapon, Vector3 position, Vector2 direction)
        {
            if (weapon == null)
            {
                Debug.LogError("[ProjectileFactory] weapon is null.");
                return null;
            }

            Projectile prefab = _registry != null
                ? _registry.Get(weapon.projectileArchetype)
                : null;

            if (prefab == null)
            {
                Debug.LogError($"[ProjectileFactory] No prefab for archetype " +
                               $"'{weapon.projectileArchetype}' (weapon '{weapon.weaponId}'). " +
                               "Register it in the ProjectilePrefabRegistry asset.");
                return null;
            }

            // AS3 pixel-speed → Unity units/s  (PPU=100, 30fps source)
            // Formula: speed_u_per_s = speed_px_per_frame * FlashFps / PPU
            // Example: sniper speed=500 px/frame → 500 * 30 / 100 = 150 u/s
            const float FlashFps = 30f;
            float unitySpeed = Mathf.Max(weapon.projectileSpeed * FlashFps / 100f, 2f);
            float unityExplRad = weapon.explRadius / 100f;

            var proj = Spawn(prefab, position, Quaternion.identity);
            if (proj == null) return null;

            proj.Initialize(
                weapon.baseDamage,
                unitySpeed,
                direction,
                weapon.bulletGravity,
                weapon.destroyTiles,
                unityExplRad,
                weapon.explosionDamage,
                weapon.damageType,
                accel:    weapon.bulletAccel / 100f,
                flame:    weapon.bulletFlame,
                navod:    weapon.bulletNavod,
                piercing: weapon.piercing);
            proj.ApplyVisual(weapon.projectileVisual);

            if (_debugSettings?.LogProjectileSpawning == true)
            {
                Debug.Log(
                    $"[ProjectileFactory] Created projectile instance='{proj.name}' weapon='{weapon.weaponId}' " +
                    $"archetype={weapon.projectileArchetype} pos={position} dir={direction.normalized} speed={unitySpeed:0.###} " +
                    $"activeSelf={proj.gameObject.activeSelf} activeInHierarchy={proj.gameObject.activeInHierarchy}.");
            }

            proj.OnReturnToPool = p => GetOrCreatePool(prefab).Release(p);
            return proj;
        }

        /// <summary>
        /// Low-level overload: explicit prefab.
        /// Used by tests and editor tooling where no WeaponDefinition is available.
        /// </summary>
        public Projectile Create(Projectile prefab, Vector3 position, Quaternion rotation,
                                 float damage, float speed, Vector2 direction,
                                 float gravityScale = 0f)
        {
            if (prefab == null)
            {
                Debug.LogError("[ProjectileFactory] prefab is null.");
                return null;
            }

            var proj = Spawn(prefab, position, rotation);
            if (proj == null) return null;

            proj.Initialize(damage, speed, direction, gravityScale);
            proj.OnReturnToPool = p => GetOrCreatePool(prefab).Release(p);
            return proj;
        }

        // ── Internal helpers ─────────────────────────────────────────────────

        /// <summary>
        /// Gets a pooled instance, injects dependencies, and positions it.
        /// Does NOT call Initialize — callers do that immediately after.
        /// </summary>
        private Projectile Spawn(Projectile prefab, Vector3 position, Quaternion rotation)
        {
            var pool = GetOrCreatePool(prefab);
            int activeBefore = pool.ActiveCount;
            int inactiveBefore = pool.InactiveCount;
            var proj = pool.Get(p =>
            {
                _resolver.Inject(p);
                p.PrepareForSpawn(position, rotation);
            });

            if (_debugSettings?.LogProjectileSpawning == true)
            {
                Debug.Log(
                    $"[ProjectileFactory] Pool fetch prefab='{prefab.name}' instance='{proj.name}' " +
                    $"active {activeBefore}->{pool.ActiveCount} inactive {inactiveBefore}->{pool.InactiveCount} " +
                    $"pos={position}.");
            }
            return proj;
        }

        private GameObjectPool<Projectile> GetOrCreatePool(Projectile prefab)
        {
            if (!_pools.TryGetValue(prefab, out var pool))
            {
                Transform poolRoot = GetOrCreatePoolRoot();
                pool = new GameObjectPool<Projectile>(
                    prefab,
                    initialSize: DefaultPoolSize,
                    maxSize:     MaxPoolSize,
                    parent:      poolRoot,
                    onGet:       null,
                    onRelease:   p => p.ResetProjectile(),
                    onCreate:    p => p.ResetProjectile());
                _pools[prefab] = pool;
            }
            return pool;
        }

        public void ClearAllPools()
        {
            foreach (var pool in _pools.Values) pool.Clear();
            _pools.Clear();

            if (_poolRoot != null)
            {
                #if UNITY_EDITOR
                if (!Application.isPlaying)
                {
                    Object.DestroyImmediate(_poolRoot.gameObject);
                }
                else
                {
                    Object.Destroy(_poolRoot.gameObject);
                }
                #else
                Object.Destroy(_poolRoot.gameObject);
                #endif
                _poolRoot = null;
            }
        }

        private Transform GetOrCreatePoolRoot()
        {
            if (_poolRoot != null)
                return _poolRoot;

            var poolRootObject = new GameObject("ProjectilePool");
            _poolRoot = poolRootObject.transform;
            return _poolRoot;
        }
    }
}
