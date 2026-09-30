using PFE.Data.Definitions;
using PFE.Entities.Units;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// Interface for entities that can take damage.
    /// Decouples damage sources from specific unit implementations.
    ///
    /// <para><b>Two entry points, on purpose.</b> <see cref="TakeDamage(float)"/> is the plain path —
    /// no armour, no crit, used by test doubles and by anything that has already done the maths.
    /// <see cref="ApplyDamage"/> takes a <see cref="DamageOutcome"/> that a resolver has already
    /// computed, and writes it down. <b>The target does not compute anything</b>: that keeps the
    /// formula pure and testable in one place, and keeps this interface free of any dependency on
    /// weapons.</para>
    ///
    /// <para><b>Why the resolver needs <see cref="Armour"/>.</b> The formula reads the target's armour
    /// projection, so it must be readable. But the target's stats must never cross a network boundary —
    /// so the resolver <i>reads</i> them locally and sends only the resulting
    /// <see cref="DamageOutcome"/>, which is a pure value type.</para>
    ///
    /// Design rationale:
    /// - Damage sources don't need to know about UnitController vs PlayerController
    /// - Enables future damageable objects (crates, doors, turrets) without modifying the source
    /// - Clean separation: the source reports a hit, the resolver computes, the target applies
    /// </summary>
    public interface IDamageable
    {
        /// <summary>
        /// Apply damage directly, bypassing armour and every other modifier.
        /// </summary>
        /// <param name="damage">Amount of damage to apply.</param>
        void TakeDamage(float damage);

        /// <summary>
        /// Apply an already-resolved outcome: armour integrity first, then health.
        /// </summary>
        /// <param name="outcome">The result of resolving a hit. The target only writes it down.</param>
        /// <returns><c>true</c> if this hit broke the armour.</returns>
        bool ApplyDamage(in DamageOutcome outcome);

        /// <summary>
        /// The target's armour projection, read by the resolver to compute an outcome.
        /// <see cref="ArmourState.None"/> when nothing is equipped.
        /// </summary>
        ArmourState Armour { get; }

        /// <summary>
        /// The target's vulnerability table — one multiplier per damage type — read by the resolver.
        /// <see cref="VulnerabilityData.Neutral"/> when the target has none.
        /// </summary>
        /// <remarks>
        /// <para>AS3 <c>Unit.vulner</c> (<c>Unit.as:72</c>), applied to every hit at
        /// <c>Unit.damage():3527-3530</c>. It sits on this interface for the same reason
        /// <see cref="Armour"/> does: the formula reads it, so it must be readable, but it must never
        /// cross a network boundary — the resolver reads it locally and sends only the resulting
        /// <see cref="DamageOutcome"/>.</para>
        ///
        /// <para><b><see cref="VulnerabilityData.Neutral"/>, not
        /// <see cref="VulnerabilityData.Unmodified"/>, is the right fallback.</b> AS3 fills the array
        /// with <c>1</c> and then unconditionally forces <c>vulner[D_EMP] = 0</c> at construction
        /// (<c>:583-590</c>), so a unit that declares no <c>&lt;vulner&gt;</c> element is
        /// <b>EMP-immune</b> rather than unmodified. Substituting the identity here would hand out EMP
        /// damage the oracle denies. (Note the two constants differ by exactly that one slot — which is
        /// why they are separate names and not one default.)</para>
        ///
        /// <para><b>Inherited, not resolved through <c>parent=</c>.</b> The importer slices the unit's
        /// own text, so an element declared only on an ancestor is not seen; AS3 does inherit. That is a
        /// recorded divergence on <c>UnitVulnerabilityParser</c>, not something this property can
        /// fix.</para>
        /// </remarks>
        VulnerabilityData Vulnerabilities { get; }

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
