using PFE.Data.Definitions;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// Implemented by any MonoBehaviour that wraps a destructible tile cell —
    /// typically a component on the same GameObject as a TilemapCollider2D.
    ///
    /// When a projectile hits the Walls physics layer it checks for this interface
    /// rather than IDamageable, so tile destruction is handled separately from
    /// unit damage (different HP pools, material resistances, debris FX, etc.).
    ///
    /// Implementation lives in the map system (e.g. DestructibleTilemap.cs).
    /// </summary>
    public interface IDestructibleTile
    {
        /// <summary>
        /// Apply destruction damage to the tile at a world-space position.
        /// The implementor converts world position to tilemap cell coordinates.
        /// </summary>
        /// <param name="worldPosition">Impact point in world space.</param>
        /// <param name="destroyAmount">
        ///   Destruction power from the weapon (WeaponDefinition.destroyTiles).
        ///   Compared against the tile's material HP.
        /// </param>
        /// <param name="damageType">
        ///   Damage type for material resistance lookup
        ///   (e.g. Explosive ignores Metal resistance that stops Ballistic).
        /// </param>
        void ApplyDestruction(UnityEngine.Vector3 worldPosition,
                              float destroyAmount,
                              DamageType damageType);

        /// <summary>
        /// Apply destruction damage to every tile within a radius (AoE explosion).
        /// Used by Explosive archetype projectiles on impact.
        /// </summary>
        /// <param name="worldPosition">Explosion centre.</param>
        /// <param name="radius">Explosion radius in Unity world units.</param>
        /// <param name="destroyAmount">Destruction power.</param>
        /// <param name="damageType">Damage type.</param>
        void ApplyDestructionRadius(UnityEngine.Vector3 worldPosition,
                                    float radius,
                                    float destroyAmount,
                                    DamageType damageType);
    }
}
