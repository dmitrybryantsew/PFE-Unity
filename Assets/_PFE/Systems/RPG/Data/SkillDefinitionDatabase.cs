using UnityEngine;
using System.Collections.Generic;

namespace PFE.Systems.RPG.Data
{
    /// <summary>
    /// Central database for all RPG skill and perk definitions.
    /// Loads ScriptableObject assets and provides lookup by ID.
    /// </summary>
    [CreateAssetMenu(fileName = "SkillDefinitionDatabase", menuName = "RPG/Skill Definition Database")]
    public class SkillDefinitionDatabase : ScriptableObject
    {
        [Header("Skills")]
        [SerializeField] private SkillDefinition[] skillDefinitions;

        [Header("Perks")]
        [SerializeField] private PerkDefinition[] perkDefinitions;

        [Header("Level Curve")]
        [SerializeField] private LevelCurve levelCurve;

        // Public accessors for editor tools
        public SkillDefinition[] Skills => skillDefinitions;
        public PerkDefinition[] Perks => perkDefinitions;

        // Lookup dictionaries
        private Dictionary<string, SkillDefinition> skillDict;
        private Dictionary<string, PerkDefinition> perkDict;

        /// <summary>
        /// Initialize the database (call from GameDatabase or on startup).
        /// </summary>
        public void Initialize()
        {
            // Build skill lookup
            skillDict = new Dictionary<string, SkillDefinition>();
            if (skillDefinitions != null)
            {
                foreach (var skill in skillDefinitions)
                {
                    if (skill != null && !string.IsNullOrEmpty(skill.SkillId))
                    {
                        skillDict[skill.SkillId] = skill;
                    }
                }
            }

            // Build perk lookup
            perkDict = new Dictionary<string, PerkDefinition>();
            if (perkDefinitions != null)
            {
                foreach (var perk in perkDefinitions)
                {
                    if (perk != null && !string.IsNullOrEmpty(perk.PerkId))
                    {
                        perkDict[perk.PerkId] = perk;
                    }
                }
            }

            Debug.Log($"[RPG] SkillDefinitionDatabase initialized: {skillDict.Count} skills, {perkDict.Count} perks");
        }

        /// <summary>
        /// Get a skill definition by ID.
        /// </summary>
        public SkillDefinition GetSkill(string skillId)
        {
            if (skillDict == null)
                Initialize();

            return skillDict.TryGetValue(skillId, out var skill) ? skill : null;
        }

        /// <summary>
        /// Get a perk definition by ID.
        /// </summary>
        public PerkDefinition GetPerk(string perkId)
        {
            if (perkDict == null)
                Initialize();

            return perkDict.TryGetValue(perkId, out var perk) ? perk : null;
        }

        /// <summary>
        /// Get a perk definition by ID (alias for GetPerk).
        /// </summary>
        public PerkDefinition GetPerkDefinition(string perkId)
        {
            return GetPerk(perkId);
        }

        /// <summary>
        /// Get all skill definitions.
        /// </summary>
        public SkillDefinition[] GetAllSkills()
        {
            if (skillDict == null)
                Initialize();

            var list = new SkillDefinition[skillDict.Count];
            skillDict.Values.CopyTo(list, 0);
            return list;
        }

        /// <summary>
        /// Get all perk definitions.
        /// </summary>
        public PerkDefinition[] GetAllPerks()
        {
            if (perkDict == null)
                Initialize();

            var list = new PerkDefinition[perkDict.Count];
            perkDict.Values.CopyTo(list, 0);
            return list;
        }

        /// <summary>
        /// Get all perks that can be unlocked by a character.
        /// </summary>
        public PerkDefinition[] GetAvailablePerks(ICharacterStats stats)
        {
            if (perkDict == null)
                Initialize();

            var available = new System.Collections.Generic.List<PerkDefinition>();
            foreach (var perk in perkDict.Values)
            {
                if (perk.IsPlayerSelectable && perk.CanUnlock(stats, stats.GetPerkRank(perk.PerkId)))
                {
                    available.Add(perk);
                }
            }
            return available.ToArray();
        }

        /// <summary>
        /// Get the level curve.
        /// </summary>
        public LevelCurve GetLevelCurve()
        {
            return levelCurve;
        }
    }
}
