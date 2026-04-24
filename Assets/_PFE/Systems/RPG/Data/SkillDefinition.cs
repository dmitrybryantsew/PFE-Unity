using UnityEngine;
using PFE.ModAPI;

namespace PFE.Systems.RPG.Data
{
    /// <summary>
    /// ScriptableObject definition for RPG skills.
    /// Based on docs/task1_core_mechanics/08_rpg_system.md
    ///
    /// Supports 18 skills:
    /// - 13 Regular Skills (cap at level 20)
    /// - 3 Post-Game Skills (cap at level 100): attack, defense, knowl
    /// - 2 Special Skills (level 40+ rewards): life, spirit
    /// </summary>
    [CreateAssetMenu(fileName = "NewSkill", menuName = "RPG/Skill Definition")]
    public class SkillDefinition : ScriptableObject, IGameContent
    {
        [Header("Identity")]
        [SerializeField] public string skillId;

        // IGameContent
        string IGameContent.ContentId => skillId;
        ContentType IGameContent.ContentType => ContentType.Skill;

        [SerializeField] public string displayName;
        [SerializeField] public string description;

        [Header("Settings")]
        [SerializeField] public bool isPostGame = false;
        [SerializeField] public int maxLevel = 20;
        [SerializeField] public int sortOrder = 0;

        [Header("Level Effects")]
        [SerializeField] public SkillLevelEffect[] levelEffects;

        // Public properties (backwards compatible)
        public string SkillId => skillId;
        public string DisplayName => displayName;
        public string Description => description;
        public bool IsPostGame => isPostGame;
        public int MaxLevel => maxLevel;
        public int SortOrder => sortOrder;
        public SkillLevelEffect[] LevelEffects => levelEffects;

        /// <summary>
        /// Get skill tier (0-5) for regular skills based on level.
        /// Thresholds: 2, 5, 9, 14, 20
        /// </summary>
        public int GetSkillTier(int skillLevel)
        {
            if (skillLevel >= 20) return 5;
            if (skillLevel >= 14) return 4;
            if (skillLevel >= 9) return 3;
            if (skillLevel >= 5) return 2;
            if (skillLevel >= 2) return 1;
            return 0;
        }

        /// <summary>
        /// Get post-game skill tier (0-10) for knowl skill.
        /// Thresholds: [5, 11, 18, 26, 35, 45, 56, 68, 82, 100]
        /// </summary>
        public int GetPostSkillTier(int skillLevel)
        {
            int[] thresholds = { 5, 11, 18, 26, 35, 45, 56, 68, 82, 100 };
            int tier = 0;
            for (int i = 0; i < thresholds.Length; i++)
            {
                if (skillLevel >= thresholds[i])
                    tier = i + 1;
            }
            return tier;
        }
    }

    /// <summary>
    /// Defines stat modifiers unlocked at specific skill levels/tiers.
    /// </summary>
    [System.Serializable]
    public class SkillLevelEffect
    {
        [Tooltip("The skill tier required for this effect (0-5 for regular skills)")]
        public int levelThreshold;

        [Tooltip("Stat modifiers applied at this tier")]
        public StatModifier[] modifiers;

        [Tooltip("Text variables set at this tier")]
        public TextVariable[] textVariables;
    }

    /// <summary>
    /// Modifies a character stat based on skill/perk level.
    /// </summary>
    [System.Serializable]
    public class StatModifier
    {
        [Tooltip("Stat ID to modify (e.g., 'maxhp', 'allDamMult', 'meleeR')")]
        public string statId;

        [Tooltip("How to apply the modifier")]
        public ModifierType type;

        [Tooltip("Which entity this affects")]
        public ModifierTarget target;

        [Tooltip("Value per level: v0, v1, v2, v3, v4, v5")]
        public float[] values;

        [Tooltip("Linear increase per level")]
        public float valueDelta;

        [Tooltip("Is this a stacking multiplier (dop=1 in XML)")]
        public bool isMultiplier;

        /// <summary>
        /// Get the modifier value for a specific level.
        /// </summary>
        public float GetValueForLevel(int level)
        {
            if (values != null && values.Length > level)
                return values[level];
            if (valueDelta != 0 && values != null && values.Length > 0)
                return values[0] + valueDelta * level;
            if (values != null && values.Length > 0)
                return values[0];
            return 0f;
        }
    }

    public enum ModifierType
    {
        Add,           // Add to base value
        Multiply,      // Multiply with current value (stacking)
        Set,           // Set to absolute value
        WeaponSkill    // +5% weapon skill per level
    }

    public enum ModifierTarget
    {
        Player,
        Pers,
        Unit
    }

    /// <summary>
    /// Key-value text pair for dynamic text variables.
    /// </summary>
    [System.Serializable]
    public class TextVariable
    {
        public string key;   // e.g., "s1", "s2"
        public string value;
    }
}
