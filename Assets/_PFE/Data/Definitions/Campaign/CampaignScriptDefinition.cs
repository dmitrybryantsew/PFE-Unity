using System;
using System.Collections.Generic;
using UnityEngine;
using PFE.ModAPI;

namespace PFE.Data.Definitions.Campaign
{
    /// <summary>
    /// Represents an atomic ActionScript script command from &lt;s act="..."&gt; in GameData.as.
    /// Used by fe.serv.Script to trigger dialogues, quests, transitions, spawns, and effects.
    /// </summary>
    [Serializable]
    public class CampaignScriptAction
    {
        [Tooltip("Action command name: dialog, stage, showstage, trigger, openland, gotoland, take, give, xp, sp, pay, rep, etc.")]
        public string action = "";

        [Tooltip("Target entity identifier: this, player, or NPC ID (targ attribute)")]
        public string target = "";

        [Tooltip("String value or identifier payload: land ID, quest ID, dialogue ID, sound ID (val attribute)")]
        public string value = "";

        [Tooltip("Numeric parameter: stage index, trigger flag value, count, or sub-index (n attribute)")]
        public int numericParam = 0;

        [Tooltip("Delay or timer in seconds before this action executes (t attribute in AS3, multiplied by 30 fps)")]
        public float delaySeconds = 0f;

        [Tooltip("First optional integer flag/parameter (opt1 attribute)")]
        public int opt1 = 0;

        [Tooltip("Second optional integer flag/parameter (opt2 attribute)")]
        public int opt2 = 0;
    }

    /// <summary>
    /// ScriptableObject definition for an authored script sequence.
    /// Corresponds to &lt;scr id="..."&gt; elements in ActionScript 3 GameData.as
    /// and fe.serv.Script.
    /// </summary>
    [CreateAssetMenu(fileName = "NewCampaignScriptDef", menuName = "PFE/Campaign/Script Definition")]
    public class CampaignScriptDefinition : VersionedData, IGameContent
    {
        [Header("Identity")]
        [Tooltip("Unique script identifier (e.g. tamePhoenix, smokeRollup, rblVisit)")]
        public string scriptId = "";

        // IGameContent implementation
        string IGameContent.ContentId => scriptId;
        ContentType IGameContent.ContentType => ContentType.Campaign;
        public override string DataId => scriptId;
        public override string DisplayName => scriptId;

        [Header("Script Actions")]
        [Tooltip("Sequential list of script commands")]
        public List<CampaignScriptAction> actions = new List<CampaignScriptAction>();

        protected override bool OnValidateData()
        {
            if (string.IsNullOrEmpty(scriptId))
            {
                Debug.LogWarning("CampaignScriptDefinition: scriptId is missing!");
                return false;
            }
            return true;
        }
    }
}
