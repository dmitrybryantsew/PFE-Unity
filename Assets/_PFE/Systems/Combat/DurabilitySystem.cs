using UnityEngine;
using PFE.Data.Definitions;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// Manages weapon durability, degradation, jamming, and misfire.
    /// Based on docs/task1_core_mechanics/02_combat_logic.md (lines 750-850)
    /// Now non-static to support dependency injection and testing.
    /// </summary>
    public class DurabilitySystem : IDurabilitySystem
    {
        private readonly ICombatCalculator _combatCalculator;

        public DurabilitySystem(ICombatCalculator combatCalculator)
        {
            _combatCalculator = combatCalculator;
        }

        /// <summary>
        /// Calculate current breaking status (-1 to 1).
        /// breaking = (maxHp - hp) / maxHp * 2 - 1
        /// At 100% hp: breaking = -1
        /// At 50% hp: breaking = 0
        /// At 0% hp: breaking = 1
        /// </summary>
        public float CalculateBreaking(int maxHp, int currentHp)
        {
            return _combatCalculator.CalculateBreaking(maxHp, currentHp);
        }

        /// <summary>
        /// Calculate durability percentage (0 to 1).
        /// </summary>
        public float CalculateDurabilityPercentage(int maxHp, int currentHp)
        {
            return (float)currentHp / maxHp;
        }

        /// <summary>
        /// Calculate deviation multiplier based on durability.
        /// At 0% durability: deviation triples
        /// </summary>
        public float CalculateDeviationMultiplier(float breaking)
        {
            return _combatCalculator.CalculateDeviationMultiplier(breaking);
        }

        /// <summary>
        /// Calculate damage penalty based on durability.
        /// At 0% durability: 30% damage reduction
        /// </summary>
        public float CalculateDamageMultiplier(float breaking)
        {
            return _combatCalculator.CalculateDurabilityDamageMultiplier(breaking);
        }

        /// <summary>
        /// Calculate jam chance.
        /// jamChance = breaking / max(20, holder) * jamMultiplier
        /// </summary>
        public float CalculateJamChance(float breaking, int magazineSize, float jamMultiplier = 1f)
        {
            return _combatCalculator.CalculateJamChance(breaking, magazineSize, jamMultiplier);
        }

        /// <summary>
        /// Calculate misfire chance.
        /// misfireChance = breaking / 5 * jamMultiplier
        /// At 0% durability: 20% misfire chance
        /// </summary>
        public float CalculateMisfireChance(float breaking, float jamMultiplier = 1f)
        {
            return _combatCalculator.CalculateMisfireChance(breaking, jamMultiplier);
        }

        /// <summary>
        /// Roll for weapon jam.
        /// Returns true if weapon jams.
        /// </summary>
        public bool RollJam(float jamChance)
        {
            return Random.value < jamChance;
        }

        /// <summary>
        /// Roll for weapon misfire.
        /// Returns true if weapon misfires.
        /// </summary>
        public bool RollMisfire(float misfireChance)
        {
            return Random.value < misfireChance;
        }

        /// <summary>
        /// Calculate durability cost per shot.
        /// Each shot costs 1 + ammoHP durability.
        /// </summary>
        public int CalculateDurabilityCost(int baseCost = 1, int ammoHp = 0)
        {
            return baseCost + ammoHp;
        }

        /// <summary>
        /// Apply durability damage.
        /// Returns remaining durability.
        /// </summary>
        public int ApplyDurabilityDamage(int currentDurability, int damage)
        {
            return Mathf.Max(0, currentDurability - damage);
        }

        /// <summary>
        /// Check if weapon is broken.
        /// </summary>
        public bool IsBroken(int currentDurability)
        {
            return currentDurability <= 0;
        }

        /// <summary>
        /// Calculate repair amount.
        /// </summary>
        public int CalculateRepair(int currentDurability, int maxDurability, int repairAmount)
        {
            return Mathf.Min(maxDurability, currentDurability + repairAmount);
        }
    }
}
