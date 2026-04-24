namespace PFE.Systems.Combat
{
    /// <summary>
    /// Interface for entities that can take damage.
    /// Decouples projectile collision logic from specific unit implementations.
    /// Allows projectiles to damage any damageable entity (Player, Enemy, Destructible, etc.)
    /// without needing to know the concrete type.
    ///
    /// Design rationale:
    /// - Projectiles only need to call TakeDamage(), don't need to know about UnitController vs PlayerController
    /// - Enables future damageable objects (crates, doors, turrets) without modifying Projectile code
    /// - Clean separation of concerns: Projectile handles collision, IDamageable handles damage response
    ///
    /// Based on Phase 2, Step 3 from docs/task1_core_mechanics implementation plan:
    /// "The projectile shouldn't care if it hit a Player, an Enemy, or a crate.
    ///  It just needs to call target.TakeDamage(calculationResult)"
    /// </summary>
    public interface IDamageable
    {
        /// <summary>
        /// Apply damage to this entity.
        /// </summary>
        /// <param name="damage">Amount of damage to apply</param>
        void TakeDamage(float damage);

        /// <summary>
        /// Current health of this entity.
        /// Used for UI, death checks, and damage calculations.
        /// </summary>
        float CurrentHealth { get; }

        /// <summary>
        /// Maximum health of this entity.
        /// Used for health percentage calculations.
        /// </summary>
        float MaxHealth { get; }

        /// <summary>
        /// Whether this entity is alive (CurrentHealth > 0).
        /// Projectiles should not damage dead entities.
        /// </summary>
        bool IsAlive { get; }
    }
}
