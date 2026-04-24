using UnityEngine;
using PFE.Systems.RPG.Data;

namespace PFE.Systems.RPG
{
    /// <summary>
    /// Vendor inventory system that scales with player's barter skill.
    /// Based on AS3 Vendor.as logic:
    /// - Doctor vendor: items = 5 + 3 * barterLvl + random
    /// - Regular vendor: items = 10 + 6 * barterLvl + random
    /// </summary>
    public class VendorInventory : MonoBehaviour
    {
        [SerializeField] private CharacterStats playerStats;

        [Header("Vendor Settings")]
        [SerializeField] private bool isDoctor = false;
        [SerializeField] private bool isRandomVendor = false;
        [SerializeField] private int baseInventorySize = 10;
        [SerializeField] private float inventoryMultiplier = 6.0f;

        /// <summary>
        /// Set player stats (for testing and DI).
        /// </summary>
        public void SetPlayerStats(CharacterStats stats)
        {
            playerStats = stats;
        }

        /// <summary>
        /// Set vendor type.
        /// </summary>
        public void SetVendorType(bool doctor, bool randomVendor = false)
        {
            isDoctor = doctor;
            isRandomVendor = randomVendor;

            // Set base values based on vendor type
            if (randomVendor)
            {
                baseInventorySize = 30;
                inventoryMultiplier = 0;
            }
            else if (doctor)
            {
                baseInventorySize = 5;
                inventoryMultiplier = 3.0f;
            }
            else
            {
                baseInventorySize = 10;
                inventoryMultiplier = 6.0f;
            }
        }

        /// <summary>
        /// Get the inventory size based on player's barter skill.
        /// Based on AS3 Vendor.as setRndBuys().
        /// </summary>
        public int GetInventorySize()
        {
            if (playerStats == null)
            {
                Debug.LogWarning("[VendorInventory] No player stats assigned, using base inventory size");
                return baseInventorySize;
            }

            int barterLevel = GetBarterLevel();

            int calculatedItems;

            if (isRandomVendor)
            {
                calculatedItems = 30;
            }
            else if (isDoctor)
            {
                // Doctor: 5 + 3 * barterLvl
                calculatedItems = baseInventorySize + Mathf.RoundToInt(inventoryMultiplier * barterLevel);
            }
            else
            {
                // Regular: 10 + 6 * barterLvl
                calculatedItems = baseInventorySize + Mathf.RoundToInt(inventoryMultiplier * barterLevel);
            }

            // Add randomness (0-2 for doctor, 0-4 for regular)
            int randomBonus = Random.Range(0, isDoctor ? 3 : 5);
            calculatedItems += randomBonus;

            // Apply randomness multiplier (0.5 - 1.2 of calculated)
            float randomMultiplier = Random.Range(0.5f, 1.2f);
            calculatedItems = Mathf.RoundToInt(calculatedItems * randomMultiplier);

            return Mathf.Max(0, calculatedItems);
        }

        /// <summary>
        /// Get price multiplier based on player's barter skill.
        /// Based on AS3: barterMult = 1 - (barterLevel * 0.03)
        /// Higher barter skill = lower prices (better deals).
        /// </summary>
        public float GetPriceMultiplier()
        {
            if (playerStats == null)
            {
                Debug.LogWarning("[VendorInventory] No player stats assigned, using default price multiplier");
                return 1.0f;
            }

            int barterLevel = GetBarterLevel();

            // barterMult = 1 - (barterLevel * 0.03)
            // Level 1: 0.97 (3% discount)
            // Level 5: 0.85 (15% discount)
            // Level 10: 0.70 (30% discount)
            float priceMultiplier = 1.0f - (barterLevel * 0.03f);

            // Cap at minimum 0.3 (70% discount max)
            return Mathf.Max(0.3f, priceMultiplier);
        }

        /// <summary>
        /// Get the discount percentage (0-100) based on barter skill.
        /// </summary>
        public int GetDiscountPercentage()
        {
            float multiplier = GetPriceMultiplier();
            int discount = Mathf.RoundToInt((1.0f - multiplier) * 100);
            return discount;
        }

        /// <summary>
        /// Calculate buy price from vendor (player buys item).
        /// </summary>
        public int CalculateBuyPrice(int basePrice)
        {
            return Mathf.RoundToInt(basePrice * GetPriceMultiplier());
        }

        /// <summary>
        /// Calculate sell price to vendor (player sells item).
        /// Typically 50% of base price, modified by barter skill.
        /// </summary>
        public int CalculateSellPrice(int basePrice)
        {
            float sellRatio = 0.5f * GetPriceMultiplier();
            return Mathf.RoundToInt(basePrice * sellRatio);
        }

        /// <summary>
        /// Get barter level (derived from barter skill).
        /// Matches AS3 barterLvl variable.
        /// </summary>
        private int GetBarterLevel()
        {
            return playerStats.GetSkillLevel("barter");
        }

        /// <summary>
        /// Get the inventory limit multiplier based on barter skill.
        /// Based on AS3: limitBuys = 1 + 0.2 * barterLevel
        /// </summary>
        public float GetInventoryLimitMultiplier()
        {
            int barterLevel = GetBarterLevel();
            return 1.0f + (0.2f * barterLevel);
        }
    }
}
