using UnityEngine;
using PFE.Data.Definitions;
using PFE.Entities.Weapons;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// Factory interface for creating projectile instances.
    /// Preferred call: <see cref="Create(WeaponDefinition,Vector3,Vector2)"/>.
    /// The factory builds its own template in code (<c>ProjectileTemplateBuilder</c>), so callers never
    /// hold a prefab or registry reference. The weapon's <see cref="ProjectileArchetype"/> is carried on
    /// the definition but does not select the template — all nine archetypes share one.
    /// </summary>
    public interface IProjectileFactory
    {
        /// <summary>
        /// Spawn a projectile for the given weapon at a world position.
        /// Archetype, speed, gravity, and damage all come from the definition.
        /// </summary>
        /// <param name="penetrationOverride">
        /// The shot's penetration budget (AS3 <c>probiv</c>), already summed with the round's own and
        /// clamped — <see cref="PFE.Systems.Weapons.DamageContext.PenetrationChance"/>. When supplied
        /// it is used instead of the definition's own <c>penetration</c>, so a round that carries a
        /// budget (AP, sabot) penetrates at the value the shot actually has. <c>null</c> falls back to
        /// the definition, which keeps every editor/test caller unchanged.
        /// </param>
        Projectile Create(WeaponDefinition weapon, Vector3 position, Vector2 direction,
                          float? penetrationOverride = null);

        /// <summary>
        /// Low-level overload: explicit template, for editor tooling and tests. Prefer the
        /// WeaponDefinition overload at runtime.
        /// </summary>
        Projectile Create(Projectile prefab, Vector3 position, Quaternion rotation,
                          float damage, float speed, Vector2 direction,
                          float gravityScale = 0f);
    }
}
