using PFE.Data.Definitions;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// Interface for combat calculations.
    /// Provides frame-to-second conversions and common combat formulas.
    /// </summary>
    public interface ICombatCalculator
    {
        #region Frame Conversions

        float FramesToSeconds(float frames);
        float SecondsToFrames(float seconds);
        float CalculateFireRate(float rapid);

        #endregion

        #region Durability Calculations

        float CalculateBreaking(int maxHp, int currentHp);
        float CalculateDeviationMultiplier(float breaking);
        float CalculateDurabilityDamageMultiplier(float breaking);
        float CalculateJamChance(float breaking, int magazineSize, float jamMultiplier = 1f);
        float CalculateMisfireChance(float breaking, float jamMultiplier = 1f);

        #endregion

        #region Accuracy Calculations

        float CalculateEffectiveDeviation(
            float baseDeviation,
            float breaking,
            float skillConf = 1f,
            float weaponSkill = 1f,
            float mazil = 0f);

        #endregion

        #region Damage Calculations

        float CalculateBaseDamage(
            float baseDamage,
            float damAdd = 0f,
            float damMult = 1f,
            float weaponSkill = 1f,
            float skillPlusDam = 1f,
            float durabilityMultiplier = 1f);

        float CalculateEffectiveArmor(float armor, float armorEffectiveness, float penetration);
        float ApplyArmor(float damage, float effectiveArmor);
        float CalculateLevelPenalty(int weaponLevel, int playerWeaponLevel);

        #endregion

        #region Critical Hit Calculations

        float CalculateCriticalChance(float baseCrit, float ownerCrit = 0f, float critchAdd = 0f);
        float CalculateCriticalMultiplier(float ownerCritDamMult, float critDamPlus = 0f);
        bool RollCriticalHit(float critChance);

        #endregion

        #region Melee Calculations

        float CalculatePowerAttackMultiplier(float chargeTime);
        float CalculateComboMultiplier(int comboCount);

        #endregion

        #region Projectile Conversions

        float PixelSpeedToUnitySpeed(float pixelSpeed);
        float CalculateProjectileMaxDistance(int lifetimeFrames, float pixelSpeed);

        #endregion
    }
}
