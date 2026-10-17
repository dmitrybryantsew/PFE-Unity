using System;
using System.Collections.Generic;
using UnityEngine;
using PFE.ModAPI;

namespace PFE.Data.Definitions.Campaign
{
    /// <summary>
    /// Represents a sub-objective or stage within a Quest.
    /// Corresponds to <c>&lt;q&gt;</c> elements under <c>&lt;quest&gt;</c> in <c>GameData.as</c>.
    ///
    /// <para><b><see cref="isHidden"/> and <see cref="isInvisible"/> are two different attributes and
    /// were conflated here until 2026-10-14.</b> <c>GameData.as:1713-1722</c> names them side by side:
    /// <c>invis='1'</c> means the objective is <i>not listed at all</i> until something opens it
    /// (<c>Quest.showSub</c>, <c>Quest.as:594-601</c>), while <c>hidden='1'</c> means it <i>is</i>
    /// listed but its text is replaced by <c>?????</c> (<c>PipPage.as:1334-1341</c>). The parser used to
    /// read <c>invis</c> into <c>isHidden</c> and never read <c>hidden</c> at all, so the journal would
    /// have shown <c>?????</c> for the 45 invisible objectives and the real text for the 13 hidden
    /// ones — exactly inverted, in both directions.</para>
    /// </summary>
    [Serializable]
    public class QuestStageDefinition
    {
        [Tooltip("1-based stage index (id attribute in AS3)")]
        public int stageIndex = 1;

        [Tooltip("Localized description string or key")]
        public string descriptionKey = "";

        [Tooltip("Objective text is replaced by '?????' in the journal until discovered (hidden='1' in AS3)")]
        public bool isHidden = false;

        [Tooltip("Objective is not listed in the journal at all until it is opened (invis='1' in AS3)")]
        public bool isInvisible = false;

        [Tooltip("Optional sub-objective (nn='1' in AS3)")]
        public bool isOptional = false;

        [Tooltip("Item required to collect for this stage (collect attribute in AS3)")]
        public string requiredItemId = "";

        [Tooltip("Quantity of item required (kol attribute in AS3)")]
        public int requiredItemCount = 1;

        [Tooltip("Item required to hand in or deliver (give attribute in AS3)")]
        public string turnInItemId = "";

        [Tooltip("The collected items are withdrawn from the inventory when the parent closes " +
                 "(del attribute in AS3, Quest.as:148-151)")]
        public bool deleteOnClose = false;

        [Tooltip("0 = the collected thing is an item, 1 = it is a weapon (coltip attribute in AS3, " +
                 "Quest.as:152-155; read by the del withdrawal at Quest.as:631-641)")]
        public int collectTip = 0;

        [Tooltip("The objective is revealed only once every earlier stage is done " +
                 "(result attribute in AS3, Quest.as:185-192)")]
        public bool isResult = false;
    }

    /// <summary>
    /// One <c>&lt;reward&gt;</c> child of a <c>&lt;quest&gt;</c> — paid by <c>Quest.close</c>
    /// (<c>Quest.as:661-689</c>).
    ///
    /// <para><b>The same element carries two independent payloads, and both are live data.</b> 75 of the
    /// 77 shipped rows carry <c>@id</c> (an item or weapon id, plus an optional <c>@kol</c> defaulting to
    /// 1); the remaining two carry <c>@trigger</c> + <c>@set</c> instead. <c>money</c> is the most
    /// common id by far — 21 of the 77 rows — and it is an ordinary row in the item table, so it needs
    /// no special case.</para>
    /// </summary>
    [Serializable]
    public class QuestRewardDefinition
    {
        [Tooltip("Item or weapon id granted (id attribute in AS3)")]
        public string itemId = "";

        [Tooltip("How many are granted (kol attribute in AS3; absent means 1, Quest.as:667-674)")]
        public int count = 1;

        [Tooltip("Trigger written instead of granting an item (trigger attribute in AS3)")]
        public string trigger = "";

        [Tooltip("Value written to that trigger (set attribute in AS3; absent means \"1\", Quest.as:679-686)")]
        public string set = "";
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

        /// <summary>
        /// AS3 <c>Quest.info</c> — the synopsis the journal's detail pane opens with.
        ///
        /// <para><b>Deliberately never populated by the importer.</b> The Oracle does not read this from
        /// the game data: <c>Quest.as:225-252</c> looks the quest up in the <b>text table</b> by id and
        /// takes the node's <c>&lt;info&gt;</c> child, falling back to the literal <c>"---"</c>. The
        /// synopsis therefore lives in <c>text_en.xml</c> alongside every other translated string, and
        /// <c>PipQuestJournal</c> resolves it there — copying it onto the asset would give the project a
        /// second, staler copy that the translator cannot reach.</para>
        /// </summary>
        [Tooltip("Unused: AS3 reads the synopsis from the text table, not the game data")]
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

        [Tooltip("<reward> children: item/weapon grants and trigger writes paid by Quest.close")]
        public List<QuestRewardDefinition> rewards = new List<QuestRewardDefinition>();

        [Header("Close-side effects")]
        [Tooltip("Trigger written when the quest closes (trigger attribute in AS3, Quest.as:205-220)")]
        public string trigger = "";

        [Tooltip("Value written to that trigger; the Oracle defaults to \"1\" when absent " +
                 "(triggerset attribute in AS3, Quest.as:208-215)")]
        public string triggerSet = "";

        [Tooltip("Dialogue id played when the quest closes (enddial attribute in AS3, Quest.as:128-131)")]
        public string endDialogueId = "";

        [Tooltip("Dialogue id played when the quest is granted (begdial attribute in AS3, Quest.as:124-127)")]
        public string beginDialogueId = "";

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
