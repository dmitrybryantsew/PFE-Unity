using System;
using System.Collections.Generic;
using UnityEngine;
using PFE.ModAPI;

namespace PFE.Data.Definitions.Campaign
{
    /// <summary>
    /// Represents a sub-objective or stage within a Quest.
    /// Corresponds to <c>&lt;q&gt;</c> elements under <c>&lt;quest&gt;</c> in <c>GameData.as</c>.
    /// </summary>
    [Serializable]
    public class QuestStageDefinition
    {
        [Tooltip("1-based stage index (id attribute in AS3)")]
        public int stageIndex = 1;

        [Tooltip("Localized description string or key")]
        public string descriptionKey = "";

        [Tooltip("Hidden objective not shown in journal until discovered (invis='1' in AS3)")]
        public bool isHidden = false;

        [Tooltip("Optional sub-objective (nn='1' in AS3)")]
        public bool isOptional = false;

        [Tooltip("Item required to collect for this stage (collect attribute in AS3)")]
        public string requiredItemId = "";

        [Tooltip("Quantity of item required (kol attribute in AS3)")]
        public int requiredItemCount = 1;

        [Tooltip("Item required to hand in or deliver (give attribute in AS3)")]
        public string turnInItemId = "";
    }

    /// <summary>
    /// ScriptableObject definition for a Quest.
    /// Corresponds to <c>&lt;quest&gt;</c> entries in ActionScript 3 <c>GameData.as</c>
    /// and the runtime <c>fe.loc.Quest</c> class.
    /// </summary>
    [CreateAssetMenu(fileName = "NewQuestDef", menuName = "PFE/Campaign/Quest Definition")]
    public class QuestDefinition : VersionedData, IGameContent
    {
        [Header("Identity")]
        [Tooltip("Unique Quest ID (e.g. toExit, storyContact, storyMain, storyFbattle)")]
        public string questId = "";

        [Tooltip("Quest title displayed in journal")]
        public string questTitle = "";

        [Tooltip("Overview or initial synopsis text")]
        [TextArea(2, 5)]
        public string questDescription = "";

        [Tooltip("NPC or faction offering this quest (empl attribute in AS3)")]
        public string employer = "";

        // IGameContent implementation
        string IGameContent.ContentId => questId;
        ContentType IGameContent.ContentType => ContentType.Mission;
        public override string DataId => questId;
        public override string DisplayName => string.IsNullOrEmpty(questTitle) ? questId : questTitle;

        [Header("Classification")]
        [Tooltip("True for mainline story quests, false for optional side quests")]
        public bool isMainQuest = true;

        [Header("Rewards")]
        [Tooltip("Experience points awarded upon completion (xp in AS3)")]
        public int rewardXp = 0;

        [Tooltip("Skill points awarded upon completion (sp in AS3)")]
        public int rewardSp = 0;

        [Tooltip("Caps or currency payment awarded upon completion (pay in AS3)")]
        public int rewardPay = 0;

        [Tooltip("Faction reputation gain awarded upon completion (rep in AS3)")]
        public int rewardReputation = 0;

        [Header("Stages / Sub-Objectives")]
        [Tooltip("Ordered sequence of quest objectives")]
        public List<QuestStageDefinition> stages = new List<QuestStageDefinition>();

        [Header("Chained Follow-Up Quest")]
        [Tooltip("Quest ID automatically granted upon completing this quest (next attribute in AS3)")]
        public string nextQuestId = "";

        protected override bool OnValidateData()
        {
            if (string.IsNullOrEmpty(questId))
            {
                Debug.LogWarning("QuestDefinition: questId is missing!");
                return false;
            }
            return true;
        }
    }
}
