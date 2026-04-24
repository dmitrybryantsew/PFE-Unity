using System;
using System.Linq;
using UnityEngine;

namespace PFE.Systems.RPG
{
    /// <summary>
    /// Extension methods for CharacterStats to support improved save/load functionality.
    /// This provides JSON serialization support that works with Unity's JsonUtility.
    /// </summary>
    public static class CharacterStatsExtensions
    {
        /// <summary>
        /// Create RPGSaveData from CharacterStats for JSON serialization.
        /// This is the preferred method for saving as it produces proper JSON.
        /// </summary>
        public static RPGSaveData CreateSaveData(this CharacterStats stats)
        {
            return RPGSaveData.FromCharacterStats(stats);
        }

        /// <summary>
        /// Load data from RPGSaveData into CharacterStats.
        /// Returns true if successful, false otherwise.
        /// </summary>
        public static bool LoadSaveData(this CharacterStats stats, RPGSaveData data)
        {
            if (stats == null || data == null)
            {
                Debug.LogError("[CharacterStatsExtensions] Cannot load null data");
                return false;
            }

            // Validate data before loading
            if (!data.Validate())
            {
                Debug.LogError("[CharacterStatsExtensions] Save data validation failed");
                return false;
            }

            try
            {
                // Load base stats using reflection or internal method access
                // For now, we'll use the existing LoadSaveData method with conversion
                var legacyData = ConvertToLegacyData(data);
                stats.LoadSaveData(legacyData);

                Debug.Log($"[CharacterStatsExtensions] Loaded save data: {data.GetSummary()}");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[CharacterStatsExtensions] Failed to load save data: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Save CharacterStats to JSON string.
        /// </summary>
        public static string SaveToJson(this CharacterStats stats)
        {
            var data = stats.CreateSaveData();
            return data?.ToJson() ?? null;
        }

        /// <summary>
        /// Load CharacterStats from JSON string.
        /// Returns true if successful, false otherwise.
        /// </summary>
        public static bool LoadFromJson(this CharacterStats stats, string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                Debug.LogError("[CharacterStatsExtensions] Cannot load from null/empty JSON");
                return false;
            }

            var data = RPGSaveData.FromJson(json);
            if (data == null)
            {
                Debug.LogError("[CharacterStatsExtensions] Failed to parse JSON");
                return false;
            }

            return stats.LoadSaveData(data);
        }

        /// <summary>
        /// Get all skill IDs from CharacterStats.
        /// This is a helper method for save functionality.
        /// </summary>
        public static string[] GetAllSkillIds(this CharacterStats stats)
        {
            // Access the internal skillIds array via reflection
            var field = stats.GetType().GetField("skillIds",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);

            if (field != null)
            {
                var value = field.GetValue(stats);
                if (value is string[] arr)
                    return arr;
            }

            // Fallback to all known skills
            return new string[]
            {
                "tele", "melee", "smallguns", "energy", "explosives", "magic",
                "repair", "medic", "lockpick", "science", "sneak", "barter", "survival",
                "attack", "defense", "knowl", "life", "spirit"
            };
        }

        /// <summary>
        /// Get all perk IDs from the perk database.
        /// This is a helper method for save functionality.
        /// </summary>
        public static string[] GetAllPerkIds(this CharacterStats stats)
        {
            // Get database from CharacterStats
            var field = stats.GetType().GetField("skillDatabase",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);

            if (field != null)
            {
                var db = field.GetValue(stats) as Data.SkillDefinitionDatabase;
                if (db != null)
                {
                    var perks = db.GetAllPerks();
                    return perks.Select(p => p.PerkId).ToArray();
                }
            }

            return new string[0];
        }

        /// <summary>
        /// Convert RPGSaveData to legacy CharacterSaveData format.
        /// </summary>
        private static CharacterSaveData ConvertToLegacyData(RPGSaveData data)
        {
            var legacyData = new CharacterSaveData
            {
                level = data.level,
                xp = data.xp,
                skillPoints = data.skillPoints,
                perkPoints = data.perkPoints,
                perkPointsExtra = data.perkPointsExtra,
                // Health values are stored as absolute values in RPGSaveData
                // but CharacterSaveData expects normalized 0-1 values
                // The LoadSaveData method will multiply by organMaxHp/maxMana
                headHp = data.organMaxHp > 0 ? data.headHp / data.organMaxHp : 0f,
                torsHp = data.organMaxHp > 0 ? data.torsHp / data.organMaxHp : 0f,
                legsHp = data.organMaxHp > 0 ? data.legsHp / data.organMaxHp : 0f,
                bloodHp = data.organMaxHp > 0 ? data.bloodHp / data.organMaxHp : 0f,
                manaHp = data.maxMana > 0 ? data.manaHp / data.maxMana : 0f
            };

            // Convert skill list to dictionary
            foreach (var skill in data.skills)
            {
                legacyData.skillLevels[skill.skillId] = skill.level;
            }

            // Convert perk list to dictionary
            foreach (var perk in data.perks)
            {
                legacyData.perkRanks[perk.perkId] = perk.rank;
            }

            return legacyData;
        }
    }
}
