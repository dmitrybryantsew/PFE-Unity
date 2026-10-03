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

        [Header("Stat Modifiers")]
        [SerializeField] public StatModifier[] modifiers;

        [Header("Rank Effects")]
        [SerializeField] public PerkRankEffect[] rankEffects;

        // Public properties
        public string PerkId => perkId;
        public string DisplayName => displayName;
        public string Description => description;
        public bool IsPlayerSelectable => isPlayerSelectable;
        public int MaxRank => maxRank;
        public PerkRequirement[] Requirements => requirements;
        public StatModifier[] Modifiers => modifiers;
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

            if (requirements == null || requirements.Length == 0)
                return true;

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
            // AS3 Pers.perkPoss (Pers.as:1436-1444):
            //   reqlevel = 1;  if (req.@lvl) reqlevel = int(req.@lvl);
            //   if (numb > 0 && req.@dlvl) reqlevel += numb * req.@dlvl;
            // `numb` is the rank already held, and this method is called with the NEXT rank, so the
            // held rank is perkRank - 1.
            //
            // Two divergences are fixed here. The `dlvl` term used to apply only to
            // RequirementType.Level, while AS3 adds it to every requirement type — so rank 2+ of a
            // perk gated on a skill was too easy. And the skill/guns cases OR-ed in the raw point
            // count alongside the tier; AS3 compares getSkLevel (the TIER) only, so a tier-3 gate
            // was satisfied by 3 raw points, which is tier 1.
            int heldRank = Mathf.Max(0, perkRank - 1);
            int requiredLevel = level > 0 ? level : 1;
            if (heldRank > 0 && levelDelta != 0)
            {
                requiredLevel += heldRank * levelDelta;
            }

            switch (type)
            {
                case RequirementType.Level:
                    return stats.Level >= requiredLevel;

                case RequirementType.Skill:
                    if (string.IsNullOrEmpty(skillId))
                        return false;
                    return stats.GetSkillTier(skillId) >= requiredLevel;

                case RequirementType.Guns:
                    // Small guns OR Energy weapons (AS3 Pers.as:1452-1458).
                    return stats.GetSkillTier("smallguns") >= requiredLevel
                        || stats.GetSkillTier("energy") >= requiredLevel;

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
