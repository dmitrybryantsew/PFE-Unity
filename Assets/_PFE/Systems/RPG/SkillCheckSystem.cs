using UnityEngine;
using PFE.Systems.RPG.Data;

namespace PFE.Systems.RPG
{
    /// <summary>
    /// System for performing skill checks in dialogue, vendor interactions,
    /// and world interactions (lockpicking, hacking, etc.).
    /// Based on docs/task1_core_mechanics/08_rpg_system.md
    /// </summary>
    public class SkillCheckSystem : MonoBehaviour
    {
        [SerializeField] private CharacterStats playerStats;

        /// <summary>
        /// Set player stats (for testing).
        /// </summary>
        public void SetPlayerStats(CharacterStats stats)
        {
            playerStats = stats;
        }

        /// <summary>
        /// Types of skill checks in the game.
        /// Maps to AS3 getLockTip() lock types:
        /// 1 = Physical locks (lockpick)
        /// 2 = Terminals (hacker/science)
        /// 3 = Mines (remine/explosives)
        /// 4-5 = Repair (repair)
        /// 6 = Signal (sneak)
        /// </summary>
        public enum CheckType
        {
            Lockpick,      // skill = lockpick (AS3 lock type 1)
            Hacker,        // skill = hacker (science) (AS3 lock type 2)
            Mine,          // skill = remine (explosives) (AS3 lock type 3)
            Repair,        // skill = repair (AS3 lock types 4-5)
            Signal,        // skill = signal (sneak) (AS3 lock type 6)
            Barter,        // skill = barterLvl (barter)
            Dialogue       // Generic skill check
        }

        /// <summary>
        /// Lock types matching AS3 getLockTip() function.
        /// </summary>
        public enum LockType
        {
            Physical = 1,    // lockpick skill
            Terminal = 2,    // hacker skill
            Mine = 3,        // remine skill
            WeaponRepair = 4,// repair skill
            OtherRepair = 5, // repair skill
            Signal = 6       // signal skill
        }

        /// <summary>
        /// Data for a skill check.
        /// </summary>
        [System.Serializable]
        public class SkillCheck
        {
            public CheckType type;
            public string skillId;
            public int difficulty;
            public bool oneTime;       // one='1' in XML
            public string resultId;    // res='resultId'
        }

        /// <summary>
        /// Result of a skill check.
        /// </summary>
        [System.Serializable]
        public class CheckResult
        {
            public bool success;
            public int playerLevel;
            public int difficulty;
            public bool autoSuccess;
            public string resultId;

            /// <summary>
            /// Get the success chance (0-1).
            /// In this system, checks are binary (pass/fail) based on skill level.
            /// </summary>
            public float GetSuccessChance()
            {
                if (autoSuccess) return 1.0f;
                if (playerLevel >= difficulty) return 1.0f;
                return 0.0f;
            }
        }

        /// <summary>
        /// Perform a skill check.
        /// </summary>
        public CheckResult PerformCheck(SkillCheck check)
        {
            if (playerStats == null)
            {
                Debug.LogError("[SkillCheckSystem] No player stats assigned!");
                return new CheckResult { success = false };
            }

            int playerSkillLevel = 0;

            switch (check.type)
            {
                case CheckType.Lockpick:
                    playerSkillLevel = GetEffectiveSkill("lockpick");
                    break;

                case CheckType.Hacker:
                    playerSkillLevel = GetEffectiveSkill("science");
                    break;

                case CheckType.Mine:
                    playerSkillLevel = GetEffectiveSkill("explosives");
                    break;

                case CheckType.Repair:
                    playerSkillLevel = GetEffectiveSkill("repair");
                    break;

                case CheckType.Signal:
                    playerSkillLevel = playerStats.GetSkillLevel("sneak");
                    break;

                case CheckType.Barter:
                    playerSkillLevel = GetBarterLevel();
                    break;

                case CheckType.Dialogue:
                    playerSkillLevel = GetEffectiveSkill(check.skillId);
                    break;
            }

            bool success = playerSkillLevel >= check.difficulty;
            bool autoSuccess = HasAutoSuccess(check.type, playerSkillLevel, check.difficulty);

            return new CheckResult
            {
                success = autoSuccess || success,
                playerLevel = playerSkillLevel,
                difficulty = check.difficulty,
                autoSuccess = autoSuccess,
                resultId = check.resultId
            };
        }

        /// <summary>
        /// Get effective skill level including perk bonuses.
        /// </summary>
        private int GetEffectiveSkill(string skillId)
        {
            int baseLevel = playerStats.GetSkillLevel(skillId);

            // Apply perk bonuses
            // Example: infiltrator perk reduces lock difficulty by 25%
            // This is handled in difficulty calculation rather than skill level
            return baseLevel;
        }

        /// <summary>
        /// Get barter level (used for vendor interactions).
        /// </summary>
        private int GetBarterLevel()
        {
            return playerStats.GetSkillLevel("barter");
        }

        /// <summary>
        /// Check if a character has auto-success for a check type.
        /// </summary>
        private bool HasAutoSuccess(CheckType type, int playerLevel, int difficulty)
        {
            // Freel perk: auto-pick locks at or below skill level
            if (type == CheckType.Lockpick && playerStats.GetPerkRank("freel") > 0)
            {
                if (playerStats.GetSkillLevel("lockpick") >= difficulty)
                    return true;
            }

            // Security perk: auto-hack terminals at or below skill level
            if (type == CheckType.Hacker && playerStats.GetPerkRank("security") > 0)
            {
                if (playerStats.GetSkillLevel("science") >= difficulty)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Perform a simple skill check (convenience method).
        /// </summary>
        public bool CheckSkill(string skillId, int difficulty)
        {
            SkillCheck check = new SkillCheck
            {
                type = CheckType.Dialogue,
                skillId = skillId,
                difficulty = difficulty
            };

            return PerformCheck(check).success;
        }

        /// <summary>
        /// Perform a lockpick check.
        /// </summary>
        public CheckResult CheckLockpick(int lockLevel)
        {
            // Apply infiltrator perk bonus (25% reduction in difficulty)
            int effectiveDifficulty = lockLevel;
            if (playerStats.GetPerkRank("infiltrator") > 0)
            {
                effectiveDifficulty = Mathf.RoundToInt(lockLevel * 0.75f);
            }

            SkillCheck check = new SkillCheck
            {
                type = CheckType.Lockpick,
                difficulty = effectiveDifficulty
            };

            return PerformCheck(check);
        }

        /// <summary>
        /// Perform a hacking check.
        /// </summary>
        public CheckResult CheckHacker(int terminalLevel)
        {
            SkillCheck check = new SkillCheck
            {
                type = CheckType.Hacker,
                difficulty = terminalLevel
            };

            return PerformCheck(check);
        }

        /// <summary>
        /// Get skill level for a lock type (AS3 getLockTip equivalent).
        /// Returns -100 if lock type 1 (physical) and character has no lockpick ability.
        /// </summary>
        public int GetLockTip(LockType lockType)
        {
            switch (lockType)
            {
                case LockType.Physical:
                    // Check if character has lockpick ability (possLockPick > 0 in AS3)
                    if (playerStats.GetSkillLevel("lockpick") > 0)
                        return playerStats.GetSkillLevel("lockpick");
                    return -100;

                case LockType.Terminal:
                    return playerStats.GetSkillLevel("science");

                case LockType.Mine:
                    return playerStats.GetSkillLevel("explosives");

                case LockType.WeaponRepair:
                case LockType.OtherRepair:
                    return playerStats.GetSkillLevel("repair");

                case LockType.Signal:
                    return playerStats.GetSkillLevel("sneak");

                default:
                    return 0;
            }
        }

        /// <summary>
        /// Get master skill level for advanced unlock attempts (AS3 getLockMaster equivalent).
        /// Returns unlockMaster for physical locks, hackerMaster for terminals.
        /// </summary>
        public int GetLockMaster(LockType lockType)
        {
            switch (lockType)
            {
                case LockType.Physical:
                    return playerStats.GetSkillLevel("lockpick");

                case LockType.Terminal:
                    return playerStats.GetSkillLevel("science");

                default:
                    return 100; // Default high value for other types
            }
        }

        /// <summary>
        /// Perform a mine detection/disarming check.
        /// </summary>
        public CheckResult CheckMine(int mineLevel)
        {
            SkillCheck check = new SkillCheck
            {
                type = CheckType.Mine,
                difficulty = mineLevel
            };

            return PerformCheck(check);
        }

        /// <summary>
        /// Perform a repair check.
        /// </summary>
        public CheckResult CheckRepair(int repairDifficulty)
        {
            SkillCheck check = new SkillCheck
            {
                type = CheckType.Repair,
                difficulty = repairDifficulty
            };

            return PerformCheck(check);
        }

        /// <summary>
        /// Perform a signal detection check.
        /// </summary>
        public CheckResult CheckSignal(int signalStrength)
        {
            SkillCheck check = new SkillCheck
            {
                type = CheckType.Signal,
                difficulty = signalStrength
            };

            return PerformCheck(check);
        }
    }
}
