using UnityEngine;

namespace PFE.Systems.RPG.Data
{
    /// <summary>
    /// ScriptableObject defining XP progression and level-up rewards.
    /// Based on docs/task1_core_mechanics/08_rpg_system.md
    ///
    /// XP Formula (AS3):
    /// - Levels 1-10: xp = xpDelta * N * (N+1) / 2
    /// - Levels 11+: multiplier = (level - 10) / 30 + 1
    ///                 xp = xpDelta * N * (N+1) / 2 * multiplier^2
    /// - Result rounded to nearest 1000
    /// </summary>
    [CreateAssetMenu(fileName = "LevelCurve", menuName = "RPG/Level Curve")]
    public class LevelCurve : ScriptableObject
    {
        [Header("XP Settings")]
        [Tooltip("XP multiplier (5000 normal, 3000 fastxp)")]
        [SerializeField] public int xpDelta = 5000;

        [Tooltip("Skill points granted per level (5 normal, 3 hardskills)")]
        [SerializeField] public int skillPointsPerLevel = 5;

        [Header("HP Settings")]
        [Tooltip("Base HP at level 1 (varies by difficulty)")]
        [SerializeField] public int baseHp = 100;

        [Tooltip("HP added per level")]
        [SerializeField] public int hpPerLevel = 15;

        [Tooltip("Organ HP (head/torso/legs/blood) added per level")]
        [SerializeField] public int organHpPerLevel = 40;

        [Tooltip("Base organ HP")]
        [SerializeField] public int baseOrganHp = 200;

        [Header("Post-Game Skills")]
        [Tooltip("Knowl skill thresholds for extra perk points")]
        [SerializeField] public int[] postSkillThresholds = { 5, 11, 18, 26, 35, 45, 56, 68, 82, 100 };

        // Public properties (backwards compatible)
        public int XpDelta => xpDelta;
        public int SkillPointsPerLevel => skillPointsPerLevel;
        public int BaseHp => baseHp;
        public int HpPerLevel => hpPerLevel;
        public int OrganHpPerLevel => organHpPerLevel;
        public int BaseOrganHp => baseOrganHp;
        public int[] PostSkillThresholds => postSkillThresholds;

        /// <summary>
        /// Calculate total XP required to reach a specific level.
        /// Based on AS3 formula with level-specific multipliers.
        ///
        /// From documentation:
        /// - Levels 1-10: xp = xpDelta * N * (N+1) / 2
        /// - Levels 11+: Each level's XP gets multiplied
        /// - Where multiplier = (level - 10) / 30 + 1
        /// </summary>
        public int GetXpForLevel(int level)
        {
            if (level < 1)
                return 0;

            long totalXp = 0;  // Use long to avoid overflow

            // Sum up XP for each level individually
            for (int lvl = 1; lvl <= level; lvl++)
            {
                // Use double for larger range and precision
                double levelXp = xpDelta * lvl;  // XP per level (linear)

                // Apply multiplier for levels 11+
                if (lvl > 10)
                {
                    double multiplier = ((lvl - 10) / 30.0) + 1.0;
                    levelXp *= multiplier * multiplier;  // Square the multiplier
                }

                totalXp += (long)System.Math.Round(levelXp);

                // Clamp as we go to prevent overflow
                if (totalXp > int.MaxValue)
                    totalXp = int.MaxValue;
            }

            // Round to nearest 1000
            long roundedXp = (totalXp / 1000L) * 1000L;

            // Clamp to int range
            return (int)System.Math.Min(roundedXp, int.MaxValue);
        }

        /// <summary>
        /// Calculate character level from total XP.
        /// </summary>
        public int GetLevelForXp(int totalXp)
        {
            int level = 1;
            while (GetXpForLevel(level) <= totalXp && level < 100)
            {
                level++;
            }
            return level - 1;
        }

        /// <summary>
        /// Get the number of extra perk points from knowl skill level.
        /// </summary>
        public int GetExtraPerkPointsFromKnowl(int knowlLevel)
        {
            int extraPerks = 0;
            for (int i = 0; i < postSkillThresholds.Length; i++)
            {
                if (knowlLevel >= postSkillThresholds[i])
                    extraPerks++;
            }
            return extraPerks;
        }
    }
}
