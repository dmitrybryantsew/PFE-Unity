using System;
using System.Collections.Generic;
using UnityEngine;

namespace PFE.Systems.RPG
{
    /// <summary>
    /// Serializable save data for RPG character stats.
    /// This class contains all data that needs to be persisted for the RPG system.
    ///
    /// Based on docs/task1_core_mechanics/08_rpg_system.md
    /// </summary>
    [Serializable]
    public class RPGSaveData
    {
        [Header("Base Stats")]
        public int level;
        public int xp;
        public int skillPoints;
        public int perkPoints;
        public int perkPointsExtra;

        [Header("Health")]
        public float maxHp;
        public float maxMana;
        public float organMaxHp;
        public float headHp;
        public float torsHp;
        public float legsHp;
        public float bloodHp;
        public float manaHp;

        [Header("Skills")]
        public List<SkillSaveData> skills = new List<SkillSaveData>();

        [Header("Perks")]
        public List<PerkSaveData> perks = new List<PerkSaveData>();

        /// <summary>
        /// Serializable data for a single skill.
        /// </summary>
        [Serializable]
        public class SkillSaveData
        {
            public string skillId;
            public int level;

            public SkillSaveData() { }

            public SkillSaveData(string id, int lvl)
            {
                skillId = id;
                level = lvl;
            }
        }

        /// <summary>
        /// Serializable data for a single perk.
        /// </summary>
        [Serializable]
        public class PerkSaveData
        {
            public string perkId;
            public int rank;

            public PerkSaveData() { }

            public PerkSaveData(string id, int r)
            {
                perkId = id;
                rank = r;
            }
        }

        /// <summary>
        /// Create save data from a CharacterStats instance.
        /// </summary>
        public static RPGSaveData FromCharacterStats(CharacterStats stats)
        {
            if (stats == null)
            {
                Debug.LogError("[RPGSaveData] Cannot create save data from null CharacterStats");
                return null;
            }

            RPGSaveData data = new RPGSaveData
            {
                level = stats.Level,
                xp = stats.Xp,
                skillPoints = stats.SkillPoints,
                perkPoints = stats.PerkPoints,
                perkPointsExtra = stats.PerkPointsExtra,
                maxHp = stats.MaxHp,
                maxMana = stats.MaxMana,
                organMaxHp = stats.OrganMaxHp,
                headHp = stats.headHp,
                torsHp = stats.torsHp,
                legsHp = stats.legsHp,
                bloodHp = stats.bloodHp,
                manaHp = stats.manaHp
            };

            // Save all skills
            foreach (string skillId in stats.GetAllSkillIds())
            {
                int level = stats.GetSkillLevel(skillId);
                if (level > 0) // Only save non-zero skills to save space
                {
                    data.skills.Add(new SkillSaveData(skillId, level));
                }
            }

            // Save all perks
            foreach (string perkId in stats.GetAllPerkIds())
            {
                int rank = stats.GetPerkRank(perkId);
                if (rank > 0) // Only save unlocked perks
                {
                    data.perks.Add(new PerkSaveData(perkId, rank));
                }
            }

            return data;
        }

        /// <summary>
        /// Convert save data to JSON string.
        /// </summary>
        public string ToJson()
        {
            return JsonUtility.ToJson(this, prettyPrint: true);
        }

        /// <summary>
        /// Create save data from JSON string.
        /// </summary>
        public static RPGSaveData FromJson(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                Debug.LogError("[RPGSaveData] Cannot parse null or empty JSON");
                return null;
            }

            try
            {
                return JsonUtility.FromJson<RPGSaveData>(json);
            }
            catch (Exception e)
            {
                Debug.LogError($"[RPGSaveData] Failed to parse JSON: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Validate the save data integrity.
        /// Returns true if data is valid, false otherwise.
        /// </summary>
        public bool Validate()
        {
            // Check basic stats
            if (level < 1 || level > 100)
            {
                Debug.LogError($"[RPGSaveData] Invalid level: {level}");
                return false;
            }

            if (xp < 0)
            {
                Debug.LogError($"[RPGSaveData] Invalid XP: {xp}");
                return false;
            }

            if (skillPoints < 0 || perkPoints < 0 || perkPointsExtra < 0)
            {
                Debug.LogError($"[RPGSaveData] Invalid point values");
                return false;
            }

            // Check health values
            if (maxHp <= 0 || maxMana <= 0 || organMaxHp <= 0)
            {
                Debug.LogError("[RPGSaveData] Invalid health values");
                return false;
            }

            // Check skills
            foreach (var skill in skills)
            {
                if (string.IsNullOrEmpty(skill.skillId))
                {
                    Debug.LogError("[RPGSaveData] Skill has null or empty ID");
                    return false;
                }

                if (skill.level < 0 || skill.level > 100)
                {
                    Debug.LogError($"[RPGSaveData] Invalid skill level for {skill.skillId}: {skill.level}");
                    return false;
                }
            }

            // Check perks
            foreach (var perk in perks)
            {
                if (string.IsNullOrEmpty(perk.perkId))
                {
                    Debug.LogError("[RPGSaveData] Perk has null or empty ID");
                    return false;
                }

                if (perk.rank < 1 || perk.rank > 10)
                {
                    Debug.LogError($"[RPGSaveData] Invalid perk rank for {perk.perkId}: {perk.rank}");
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Get a summary of the save data for debugging.
        /// </summary>
        public string GetSummary()
        {
            return $"Level {level} ({xp} XP), " +
                   $"{skillPoints} SP, {perkPoints + perkPointsExtra} PP, " +
                   $"{skills.Count} skills, {perks.Count} perks";
        }
    }
}
