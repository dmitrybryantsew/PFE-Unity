using UnityEngine;
using System;
using System.Linq;
using PFE.ModAPI;

namespace PFE.Systems.RPG.Data
{
    /// <summary>
    /// ScriptableObject definition for RPG perks.
    /// Based on docs/task1_core_mechanics/08_rpg_system.md
    ///
    /// Supports 60+ perks with:
    /// - Tiered requirements (level, skill, guns)
    /// - Multi-rank progression (e.g., dexter rank 1-3)
    /// - Dynamic prerequisites (dlvl = additional level per rank)
    /// - Stat modifiers and text variables per rank
    /// </summary>
    [CreateAssetMenu(fileName = "NewPerk", menuName = "RPG/Perk Definition")]
    public class PerkDefinition : ScriptableObject, IGameContent
    {
        [Header("Identity")]
        [SerializeField] public string perkId;

        // IGameContent
        string IGameContent.ContentId => perkId;
        ContentType IGameContent.ContentType => ContentType.Perk;

        [SerializeField] public string displayName;
        [SerializeField] [TextArea] public string description;

        [Header("Settings")]
        [SerializeField] [Tooltip("Can player manually select this perk? (tip=1 in XML)")]
        public bool isPlayerSelectable = true;

        [SerializeField] [Tooltip("Maximum rank (1=single, 2+=stacking)")]
        public int maxRank = 1;

        [Header("Prerequisites")]
        [SerializeField] public PerkRequirement[] requirements;

        [Header("Rank Effects")]
        [SerializeField] public PerkRankEffect[] rankEffects;

        // Public properties
        public string PerkId => perkId;
        public string DisplayName => displayName;
        public string Description => description;
        public bool IsPlayerSelectable => isPlayerSelectable;
        public int MaxRank => maxRank;
        public PerkRequirement[] Requirements => requirements;
        public PerkRankEffect[] RankEffects => rankEffects;

        /// <summary>
        /// Check if this perk can be unlocked by a character.
        /// Returns: true if requirements are met, false otherwise.
        /// </summary>
        public bool CanUnlock(ICharacterStats stats, int currentRank)
        {
            // Check if already maxed
            if (currentRank >= maxRank)
                return false;

            // Check all requirements for the next rank
            int nextRank = currentRank + 1;
            return requirements.All(req => req.IsMet(stats, nextRank));
        }

        /// <summary>
        /// Get the effects for a specific rank.
        /// </summary>
        public PerkRankEffect GetEffectsForRank(int rank)
        {
            if (rankEffects == null)
                return null;

            return rankEffects.FirstOrDefault(e => e.rank == rank);
        }
    }

    /// <summary>
    /// Prerequisite for unlocking a perk.
    /// Supports dynamic requirements that scale with perk rank.
    /// </summary>
    [System.Serializable]
    public class PerkRequirement
    {
        [Tooltip("Type of requirement")]
        public RequirementType type;

        [Tooltip("Skill ID for skill-based requirements")]
        public string skillId;

        [Tooltip("Base level required")]
        public int level = 0;

        [Tooltip("Additional levels required per perk rank (dlvl in XML)")]
        public int levelDelta = 0;

        /// <summary>
        /// Check if this requirement is met.
        /// </summary>
        public bool IsMet(ICharacterStats stats, int perkRank)
        {
            int requiredLevel = level;
            if (levelDelta > 0 && perkRank > 1)
            {
                // Dynamic requirement: level + (perkRank - 1) * levelDelta
                requiredLevel += (perkRank - 1) * levelDelta;
            }

            switch (type)
            {
                case RequirementType.Level:
                    return stats.Level >= requiredLevel;

                case RequirementType.Skill:
                    if (string.IsNullOrEmpty(skillId))
                        return false;
                    return stats.GetSkillLevel(skillId) >= requiredLevel;

                case RequirementType.Guns:
                    // Small guns OR Energy weapons
                    return stats.GetSkillLevel("smallguns") >= requiredLevel ||
                           stats.GetSkillLevel("energy") >= requiredLevel;

                default:
                    Debug.LogWarning($"Unknown requirement type: {type}");
                    return false;
            }
        }
    }

    public enum RequirementType
    {
        Level,  // Character level
        Skill,  // Specific skill level
        Guns    // Small guns OR Energy weapons
    }

    /// <summary>
    /// Effects granted at a specific perk rank.
    /// </summary>
    [System.Serializable]
    public class PerkRankEffect
    {
        [Tooltip("The rank this effect applies to (1-based)")]
        public int rank;

        [Tooltip("Stat modifiers at this rank")]
        public StatModifier[] modifiers;

        [Tooltip("Text variables set at this rank")]
        public TextVariable[] textVariables;
    }
}
