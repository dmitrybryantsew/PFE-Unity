using System;
using PFE.Character;

namespace PFE.UI.MainMenu
{
    public enum NewGameDifficulty
    {
        VeryEasy = 0,
        Easy = 1,
        Normal = 2,
        Hard = 3,
        SuperHard = 4
    }

    [Flags]
    public enum NewGameRuleFlags
    {
        None = 0,

        // TODO: Hook these into actual gameplay/new-game initialization rules.
        SkipTraining = 1 << 0,
        FasterLevelUps = 1 << 1,
        WeLiveOnce = 1 << 2,
        RandomSkills = 1 << 3,
        SlowLearner = 1 << 4,
        LimitedInventory = 1 << 5
    }

    /// <summary>
    /// Captures the settings chosen in the main-menu new-game flow.
    /// </summary>
    [Serializable]
    public class NewGameSettings
    {
        public string playerName = "Littlepip";
        public NewGameDifficulty difficulty = NewGameDifficulty.Normal;
        public NewGameRuleFlags ruleFlags = NewGameRuleFlags.None;
        public CharacterAppearance appearance = CharacterAppearance.CreateDefault();

        public bool HasFlag(NewGameRuleFlags flag)
        {
            return (ruleFlags & flag) == flag;
        }

        public void SetFlag(NewGameRuleFlags flag, bool enabled)
        {
            if (enabled)
            {
                ruleFlags |= flag;
            }
            else
            {
                ruleFlags &= ~flag;
            }
        }

        public NewGameSettings Clone()
        {
            return new NewGameSettings
            {
                playerName = playerName,
                difficulty = difficulty,
                ruleFlags = ruleFlags,
                appearance = appearance?.Clone() ?? CharacterAppearance.CreateDefault()
            };
        }

        public static string GetDifficultyLabel(NewGameDifficulty difficulty)
        {
            return difficulty switch
            {
                NewGameDifficulty.VeryEasy => "Very easy",
                NewGameDifficulty.Easy => "Easy",
                NewGameDifficulty.Normal => "Normal",
                NewGameDifficulty.Hard => "Hard",
                NewGameDifficulty.SuperHard => "Super-hard",
                _ => difficulty.ToString()
            };
        }

        public static string GetRuleLabel(NewGameRuleFlags flag)
        {
            string label = flag switch
            {
                NewGameRuleFlags.SkipTraining => "Skip training",
                NewGameRuleFlags.FasterLevelUps => "Faster level ups",
                NewGameRuleFlags.WeLiveOnce => "We live once",
                NewGameRuleFlags.RandomSkills => "Random skills",
                NewGameRuleFlags.SlowLearner => "Slow learner",
                NewGameRuleFlags.LimitedInventory => "Limited inventory",
                _ => flag.ToString()
            };

            return $"{label} (TODO)";
        }

        public static NewGameSettings CreateDefault()
        {
            return new NewGameSettings
            {
                playerName = "Littlepip",
                difficulty = NewGameDifficulty.Normal,
                ruleFlags = NewGameRuleFlags.None,
                appearance = CharacterAppearance.CreateDefault()
            };
        }
    }
}
