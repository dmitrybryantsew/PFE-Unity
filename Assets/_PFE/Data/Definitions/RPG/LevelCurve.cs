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
        /// Total XP required to reach a level — AS3 <c>Pers.xpProgress</c>
        /// (<c>Pers.as:1139-1147</c>):
        ///
        /// <code>
        /// mult = level &gt; 10 ? (level - 10) / 30 + 1 : 1
        /// return round(xpDelta * level * (level + 1) / 2 * mult * mult / 1000) * 1000
        /// </code>
        ///
        /// <para><b>One closed form, not a sum of marginal levels.</b> An earlier revision summed
        /// <c>xpDelta * lvl</c> per level, applying the multiplier only to the marginal term. The two
        /// agree exactly for level ≤ 10 (both reduce to <c>xpDelta * N(N+1) / 2</c>) and then drift:
        /// level 11 is 352 000 in the oracle and was 333 000 here, and the gap compounds because
        /// AS3 squares the multiplier over the whole sum.</para>
        /// </summary>
        public int GetXpForLevel(int level)
        {
            if (level < 1)
                return 0;

            double multiplier = 1.0;
            if (level > 10)
                multiplier = (level - 10) / 30.0 + 1.0;

            double raw = (double)xpDelta * level * (level + 1) / 2.0 * multiplier * multiplier;
            double rounded = System.Math.Round(raw / 1000.0) * 1000.0;

            if (rounded > int.MaxValue)
                return int.MaxValue;
            return (int)rounded;
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
