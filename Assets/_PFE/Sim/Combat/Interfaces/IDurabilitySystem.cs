namespace PFE.Systems.Combat
{
    /// <summary>
    /// Interface for weapon durability, degradation, jamming, and misfire calculations.
    /// </summary>
    public interface IDurabilitySystem
    {
        float CalculateBreaking(int maxHp, int currentHp);
        float CalculateDurabilityPercentage(int maxHp, int currentHp);
        float CalculateDeviationMultiplier(float breaking);
        float CalculateDamageMultiplier(float breaking);
        float CalculateJamChance(float breaking, int magazineSize, float jamMultiplier = 1f);
        float CalculateMisfireChance(float breaking, float jamMultiplier = 1f);
        bool RollJam(float jamChance);
        bool RollMisfire(float misfireChance);
        int CalculateDurabilityCost(int baseCost = 1, int ammoHp = 0);
        int ApplyDurabilityDamage(int currentDurability, int damage);
        bool IsBroken(int currentDurability);
        int CalculateRepair(int currentDurability, int maxDurability, int repairAmount);
    }
}
