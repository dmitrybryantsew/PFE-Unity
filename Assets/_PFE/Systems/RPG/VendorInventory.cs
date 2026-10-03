using UnityEngine;
using PFE.Systems.RPG.Data;

namespace PFE.Systems.RPG
{
    /// <summary>
    /// Vendor inventory system that scales with the player's barter stat.
    /// Port of AS3 <c>Vendor.as</c>:
    /// - Doctor vendor:  <c>5 + 3 * barterLvl</c>, then <c>* (0.5 + random()*0.7)</c>
    /// - Regular vendor: <c>10 + 6 * barterLvl</c>, then <c>* (0.5 + random()*0.7)</c>
    /// - Random vendor:  <c>30</c>, then <c>* (0.5 + random()*0.7)</c>
    /// - Prices are multiplied by <c>Pers.barterMult</c> (<c>PipBuck.as:414</c>)
    /// - Restock caps are multiplied by <c>Pers.limitBuys</c> (<c>Vendor.as:304</c>)
    /// </summary>
    public class VendorInventory : MonoBehaviour
    {
        [SerializeField] private CharacterStats playerStats;
        private PFE.Core.Rng.IRngService _rng;
        private static PFE.Core.Rng.IRngService s_lootRng;
        private PFE.Core.Rng.IRngService Rng => _rng ?? (s_lootRng ??= new PFE.Core.Rng.PcgRngService().GetStream(PFE.Core.Rng.RngStream.Loot));

        [VContainer.Inject]
        public void Construct(PFE.Core.Rng.IRngService rng = null)
        {
            _rng = rng?.GetStream(PFE.Core.Rng.RngStream.Loot);
        }

        public void SetRng(PFE.Core.Rng.IRngService rng)
        {
            _rng = rng?.GetStream(PFE.Core.Rng.RngStream.Loot);
        }

        [Header("Vendor Settings")]
        [SerializeField] private bool isDoctor = false;
        [SerializeField] private bool isRandomVendor = false;

        /// <summary>
        /// Set player stats (for testing and DI).
        /// </summary>
        public void SetPlayerStats(CharacterStats stats)
        {
            playerStats = stats;
        }

        /// <summary>
        /// Set vendor type. The AS3 item counts are constants per type
        /// (<c>Vendor.as:136-147</c>), so no tuning fields are exposed here.
        /// </summary>
        public void SetVendorType(bool doctor, bool randomVendor = false)
        {
            isDoctor = doctor;
            isRandomVendor = randomVendor;
        }

        /// <summary>
        /// Get the inventory size based on player's barter skill.
        /// Based on AS3 Vendor.as setRndBuys() (Vendor.as:125-148).
        ///
        /// <para>
        /// AS3 computes <c>count</c> from <b>pers.barterLvl</b> — the dedicated barter-level field,
        /// not the raw <c>barter</c> skill — then applies
        /// <c>Math.round(count * (0.5 + random() * 0.7))</c>, i.e. a <b>0.5–1.2</b> multiplier.
        /// The earlier port added an extra integer bonus (<c>Rng.Range(0, 3|5)</c>) that has no
        /// AS3 counterpart; it is removed here.
        /// </para>
        /// </summary>
        public int GetInventorySize()
        {
            if (playerStats == null)
            {
                Debug.LogWarning("[VendorInventory] No player stats assigned, using an empty inventory");
                return 0;
            }

            int barterLevel = GetBarterLevel();

            int calculatedItems;

            if (isRandomVendor)
            {
                calculatedItems = 30;
            }
            else if (isDoctor)
            {
                // AS3 Vendor.as:142 — doctor: 5 + 3 * barterLvl
                calculatedItems = 5 + 3 * barterLevel;
            }
            else
            {
                // AS3 Vendor.as:146 — regular: 10 + 6 * barterLvl
                calculatedItems = 10 + 6 * barterLevel;
            }

            // AS3 Vendor.as:148 — Math.round(count * (0.5 + random() * 0.7))
            float randomMultiplier = 0.5f + Rng.NextFloat() * 0.7f;
            calculatedItems = Mathf.RoundToInt(calculatedItems * randomMultiplier);

            return Mathf.Max(0, calculatedItems);
        }

        /// <summary>
        /// Get the price multiplier based on the player's barter state.
        ///
        /// <para>
        /// AS3 does <b>not</b> derive a price curve from the barter skill. It sets
        /// <c>Vendor.multPrice = pers.barterMult</c> (<c>PipBuck.as:414</c>) and multiplies prices
        /// by it directly (<c>PipPageVend.as:594, 829-850</c>). In the port <c>barterMult</c> and
        /// <c>capsMult</c> share one destination field (<c>CharacterStats.capsMult</c>), so that is
        /// what is read here.
        /// </para>
        ///
        /// <para>
        /// The previous implementation invented <c>1 - barterLevel * 0.03</c> and clamped it at
        /// 0.3. That formula exists nowhere in AS3 — it was a plausible-looking derivation that
        /// silently ignored the real field. Removed.
        /// </para>
        /// </summary>
        public float GetPriceMultiplier()
        {
            if (playerStats == null)
            {
                Debug.LogWarning("[VendorInventory] No player stats assigned, using default price multiplier");
                return 1.0f;
            }

            return playerStats.capsMult;
        }

        /// <summary>
        /// Get the discount percentage (0-100) based on barter.
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
        /// Typically 50% of base price, modified by barter.
        /// </summary>
        public int CalculateSellPrice(int basePrice)
        {
            float sellRatio = 0.5f * GetPriceMultiplier();
            return Mathf.RoundToInt(basePrice * sellRatio);
        }

        /// <summary>
        /// Get barter level (the dedicated stat).
        /// AS3 reads <c>pers.barterLvl</c> (Vendor.as:142/146), not <c>GetSkillLevel("barter")</c>.
        /// </summary>
        private int GetBarterLevel()
        {
            return playerStats.barterLvl;
        }

        /// <summary>
        /// Get the per-restock buy limit multiplier.
        ///
        /// <para>
        /// AS3 reads <c>pers.limitBuys</c> directly (<c>Vendor.as:304</c>:
        /// <c>lim = Math.ceil(buy.@n * pers.limitBuys)</c>). The field's own default is 1
        /// (<c>Pers.as:317</c>) and it is raised by barter perks. The previous implementation
        /// invented <c>1 + 0.2 * barterLevel</c>; removed.
        /// </para>
        /// </summary>
        public float GetInventoryLimitMultiplier()
        {
            return playerStats.limitBuys;
        }
    }
}
