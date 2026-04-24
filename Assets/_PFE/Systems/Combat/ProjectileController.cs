using UnityEngine;
using PFE.Data.Definitions;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// Controls projectile behavior including movement, gravity, collision, and penetration.
    /// Based on docs/task1_core_mechanics/02_combat_logic.md (lines 400-550)
    /// Now uses dependency injection for combat calculations.
    /// </summary>
    public class ProjectileController
    {
        private WeaponDefinition weaponDef;
        private Vector3 position;
        private Vector3 velocity;
        private float lifetime;
        private int penetrationCount;
        private bool isActive;
        private readonly ICombatCalculator _combatCalculator;

        public Vector3 Position => position;
        public bool IsActive => isActive;
        public float Damage { get; private set; }

        /// <summary>
        /// Create a new projectile with dependency injection.
        /// </summary>
        public ProjectileController(
            WeaponDefinition weaponDef,
            Vector3 origin,
            Vector3 direction,
            float damage,
            ICombatCalculator combatCalculator)
        {
            this.weaponDef = weaponDef;
            this.position = origin;
            this.Damage = damage;
            this._combatCalculator = combatCalculator;
            this.isActive = true;
            this.penetrationCount = 0;

            // Convert pixel speed to Unity units using injected calculator
            float unitySpeed = _combatCalculator.PixelSpeedToUnitySpeed(weaponDef.projectileSpeed);
            this.velocity = direction.normalized * unitySpeed;

            // Calculate lifetime (if not specified, default to 3 seconds)
            this.lifetime = 3f;
        }

        /// <summary>
        /// Update projectile position and physics.
        /// Call this every frame.
        /// </summary>
        public void Update(float deltaTime)
        {
            if (!isActive)
                return;

            // Apply velocity
            position += velocity * deltaTime;

            // Apply gravity (if applicable)
            // dy += ddy (gravity acceleration)
            // Most bullets have negligible gravity, but some projectiles fall
            // velocity.y -= gravity * deltaTime;

            // Decrease lifetime
            lifetime -= deltaTime;
            if (lifetime <= 0)
            {
                isActive = false;
            }
        }

        /// <summary>
        /// Check if projectile can penetrate target.
        /// Returns true if projectile should continue after hit.
        /// </summary>
        public bool CanPenetrate(float remainingDamage)
        {
            // probiv > 0 allows penetration
            // Continue if damage > 0 after hit
            if (weaponDef.armorPenetration <= 0)
                return false;

            if (remainingDamage <= 0)
                return false;

            penetrationCount++;
            return true;
        }

        /// <summary>
        /// Check if projectile creates explosion on impact.
        /// </summary>
        public bool HasExplosion()
        {
            return weaponDef.explRadius > 0;
        }

        /// <summary>
        /// Get explosion radius.
        /// </summary>
        public float GetExplosionRadius()
        {
            return weaponDef.explRadius;
        }

        /// <summary>
        /// Deactivate projectile.
        /// </summary>
        public void Deactivate()
        {
            isActive = false;
        }

        /// <summary>
        /// Get projectile velocity for collision detection.
        /// </summary>
        public Vector3 GetVelocity()
        {
            return velocity;
        }

        /// <summary>
        /// Check if projectile is instant-hit (laser).
        /// High speed projectiles simulate instant hit.
        /// </summary>
        public bool IsInstantHit()
        {
            // spring='2' enables visual stretch (laser)
            // Very high speed (2000+) simulates instant hit
            return weaponDef.projectileSpeed > 1000f;
        }
    }
}
