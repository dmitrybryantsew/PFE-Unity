using UnityEngine;
using PFE.Data.Definitions;
using PFE.Entities.Weapons;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// Factory interface for creating projectile instances.
    /// Preferred call: <see cref="Create(WeaponDefinition,Vector3,Vector2)"/>.
    /// The factory resolves the correct prefab from <see cref="ProjectilePrefabRegistry"/>
    /// using the weapon's <see cref="ProjectileArchetype"/>, so callers never hold prefab references.
    /// </summary>
    public interface IProjectileFactory
    {
        /// <summary>
        /// Spawn a projectile for the given weapon at a world position.
        /// Archetype, speed, gravity, and damage all come from the definition.
        /// </summary>
        Projectile Create(WeaponDefinition weapon, Vector3 position, Vector2 direction);

        /// <summary>
        /// Low-level overload: explicit prefab, for cases where the registry cannot be used
        /// (e.g. editor tooling, tests). Prefer the WeaponDefinition overload at runtime.
        /// </summary>
        Projectile Create(Projectile prefab, Vector3 position, Quaternion rotation,
                          float damage, float speed, Vector2 direction,
                          float gravityScale = 0f);
    }
}
