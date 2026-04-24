using UnityEngine;
using PFE.Data.Definitions;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// Combat calculations service.
    /// Handles frame-to-second conversions and common combat formulas.
    /// Based on docs/task1_core_mechanics/02_combat_logic.md
    /// Uses dependency injection for testability and loose coupling.
    /// </summary>
    public class CombatCalculator : ICombatCalculator
    {
        /// <summary>
        /// Original game FPS for frame-based calculations.
        /// All rapid/duration values in AS3 are based on 30 FPS.
        /// </summary>
        public const float ORIGINAL_FPS = 30f;

        #region Frame Conversions

        /// <summary>
        /// Convert frames to seconds (at 30 FPS).
        /// Example: 10 frames = 0.333 seconds
        /// </summary>
        public float FramesToSeconds(float frames)
        {
            return frames / ORIGINAL_FPS;
        }

        /// <summary>
        /// Convert seconds to frames (at 30 FPS).
        /// Example: 1 second = 30 frames
        /// </summary>
        public float SecondsToFrames(float seconds)
        {
            return seconds * ORIGINAL_FPS;
        }

        /// <summary>
        /// Calculate fire rate (shots per second) from rapid value.
        /// rapid=10 means 10 frame cooldown = 3 shots/sec at 30 FPS.
        /// rapid=0 means no cooldown (infinite fire rate).
        /// </summary>
        public float CalculateFireRate(float rapid)
        {
            if (rapid <= 0f)
                return float.PositiveInfinity;
            return ORIGINAL_FPS / rapid;
        }

        #endregion

        #region Durability Calculations

        /// <summary>
        /// Calculate weapon breaking status.
        /// breaking = (maxHp - hp) / maxHp * 2 - 1
        /// At 50% hp: breaking = 0
        /// At 0% hp: breaking = 1
        /// </summary>
        public float CalculateBreaking(int maxHp, int currentHp)
        {
            return (maxHp - currentHp) / (float)maxHp * 2f - 1f;
        }

        /// <summary>
        /// Calculate deviation multiplier based on durability.
        /// deviation * (1 + breaking * 2)
        /// At 0% durability (breaking=1): deviation triples
        /// At 50% durability (breaking=0): no change
        /// At 100% durability (breaking=-1): deviation multiplier = -1 (better accuracy)
        /// </summary>
        public float CalculateDeviationMultiplier(float breaking)
        {
            // Clamp breaking to avoid extreme values
            float clampedBreaking = Mathf.Clamp(breaking, -1f, 1f);
            return 1f + clampedBreaking * 2f;
        }

        /// <summary>
        /// Calculate damage penalty based on durability.
        /// (1 - breaking * 0.3)
        /// At 0% durability (breaking=1): 70% damage (30% penalty)
        /// </summary>
        public float CalculateDurabilityDamageMultiplier(float breaking)
        {
            return 1f - breaking * 0.3f;
        }

        /// <summary>
        /// Calculate jam chance based on durability.
        /// jamChance = breaking / magazineSize * jamMultiplier
        /// </summary>
        public float CalculateJamChance(float breaking, int magazineSize, float jamMultiplier = 1f)
        {
            return breaking / magazineSize * jamMultiplier;
        }

        /// <summary>
        /// Calculate misfire chance based on durability.
        /// misfireChance = breaking / 5 * jamMultiplier
        /// </summary>
        public float CalculateMisfireChance(float breaking, float jamMultiplier = 1f)
        {
            return breaking / 5f * jamMultiplier;
        }

        #endregion

        #region Accuracy Calculations

        /// <summary>
        /// Calculate accuracy spread including all modifiers.
        /// Formula: deviation * (1 + breaking * 2) / skillConf / weaponSkill + mazil
        /// </summary>
        /// <param name="baseDeviation">Weapon's base deviation</param>
        /// <param name="breaking">Weapon durability status (-1 to 1)</param>
        /// <param name="skillConf">Skill confidence multiplier</param>
        /// <param name="weaponSkill">Weapon skill level</param>
        /// <param name="mazil">Additional accuracy modifier</param>
        public float CalculateEffectiveDeviation(
            float baseDeviation,
            float breaking,
            float skillConf = 1f,
            float weaponSkill = 1f,
            float mazil = 0f)
        {
            float durabilityMultiplier = CalculateDeviationMultiplier(breaking);
            return baseDeviation * durabilityMultiplier / skillConf / weaponSkill + mazil;
        }

        #endregion

        #region Damage Calculations

        /// <summary>
        /// Calculate base damage with all modifiers.
        /// Formula: (baseDamage + damAdd) * damMult * weaponSkill * skillPlusDam * durabilityMultiplier
        /// </summary>
        public float CalculateBaseDamage(
            float baseDamage,
            float damAdd = 0f,
            float damMult = 1f,
            float weaponSkill = 1f,
            float skillPlusDam = 1f,
            float durabilityMultiplier = 1f)
        {
            return (baseDamage + damAdd) * damMult * weaponSkill * skillPlusDam * durabilityMultiplier;
        }

        /// <summary>
        /// Calculate effective armor after penetration.
        /// Formula: armor * armorEffectiveness - penetration
        /// </summary>
        public float CalculateEffectiveArmor(float armor, float armorEffectiveness, float penetration)
        {
            return Mathf.Max(0, armor * armorEffectiveness - penetration);
        }

        /// <summary>
        /// Apply armor to damage.
        /// Returns damage remaining after armor reduction.
        /// </summary>
        public float ApplyArmor(float damage, float effectiveArmor)
        {
            return Mathf.Max(0, damage - effectiveArmor);
        }

        /// <summary>
        /// Calculate weapon level penalty.
        /// 10% penalty per level below weapon level.
        /// </summary>
        /// <param name="weaponLevel">Weapon's required level</param>
        /// <param name="playerWeaponLevel">Player's weapon skill level</param>
        public float CalculateLevelPenalty(int weaponLevel, int playerWeaponLevel)
        {
            int levelDiff = weaponLevel - playerWeaponLevel;
            if (levelDiff <= 0)
                return 1f; // No penalty if player level >= weapon level

            return 1f - levelDiff * 0.1f;
        }

        #endregion

        #region Critical Hit Calculations

        /// <summary>
        /// Calculate total critical hit chance.
        /// Formula: baseCrit + ownerCrit + critchAdd
        /// </summary>
        public float CalculateCriticalChance(
            float baseCrit,
            float ownerCrit = 0f,
            float critchAdd = 0f)
        {
            return baseCrit + ownerCrit + critchAdd;
        }

        /// <summary>
        /// Calculate total critical hit damage multiplier.
        /// Formula: ownerCritDamMult + critDamPlus
        /// </summary>
        public float CalculateCriticalMultiplier(
            float ownerCritDamMult,
            float critDamPlus = 0f)
        {
            return ownerCritDamMult + critDamPlus;
        }

        /// <summary>
        /// Roll for critical hit.
        /// Returns true if the roll is less than crit chance.
        /// </summary>
        public bool RollCriticalHit(float critChance)
        {
            return UnityEngine.Random.value < critChance;
        }

        #endregion

        #region Melee Calculations

        /// <summary>
        /// Calculate power attack damage multiplier.
        /// Charge up to 2.15 seconds for max damage.
        /// Formula: 1 + chargeTime / 2.15
        /// Max: ~2x damage
        /// </summary>
        public float CalculatePowerAttackMultiplier(float chargeTime)
        {
            return 1f + chargeTime / 2.15f;
        }

        /// <summary>
        /// Calculate combo damage multiplier.
        /// 4th consecutive hit deals 2x damage.
        /// </summary>
        public float CalculateComboMultiplier(int comboCount)
        {
            return comboCount >= 4 ? 2f : 1f;
        }

        #endregion

        #region Projectile Conversions

        /// <summary>
        /// Convert pixel speed to Unity units per second.
        /// Original: 100 pixels/frame
        /// Unity: 100 pixels = 1 unit, 30 FPS
        /// Result: 30 units/second
        /// </summary>
        public float PixelSpeedToUnitySpeed(float pixelSpeed)
        {
            const float PIXELS_TO_UNITS = 100f;
            return pixelSpeed / PIXELS_TO_UNITS * ORIGINAL_FPS;
        }

        /// <summary>
        /// Calculate max projectile distance.
        /// Formula: (lifetime / FPS) * (speed / 100)
        /// </summary>
        public float CalculateProjectileMaxDistance(int lifetimeFrames, float pixelSpeed)
        {
            float lifetimeSeconds = FramesToSeconds(lifetimeFrames);
            float unitySpeed = PixelSpeedToUnitySpeed(pixelSpeed);
            return lifetimeSeconds * unitySpeed;
        }

        #endregion
    }
}
