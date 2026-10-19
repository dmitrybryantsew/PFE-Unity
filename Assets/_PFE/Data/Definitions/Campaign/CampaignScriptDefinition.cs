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

        /// <summary>
        /// The <c>@n</c> attribute — a stage index, a trigger value, a count, or a sub-objective id.
        ///
        /// <para><b>The default is <c>-1</c>, and it is not a sentinel this port invented.</b>
        /// <c>analiz</c> declares <c>var _loc6_:String = "-1";</c> (<c>Script.as:70</c>) and only
        /// overwrites it when the attribute is present (<c>:97-100</c>), so <b>every</b> action pushed
        /// by the oracle carries <c>n = "-1"</c> when the author wrote no <c>@n</c>. Three consumers
        /// branch on the sign, and a <c>0</c> default silently picks the wrong arm of all three:
        /// <c>stage</c> (<c>Game.as:624</c> — negative means "finish the whole quest"),
        /// <c>take</c> (<c>Script.as:302</c> — negative means "withdraw" rather than "grant"), and
        /// <c>setTrigger</c> (<c>Game.as:676</c> — writes <c>-1</c>, not <c>0</c>).</para>
        ///
        /// <para><b>An authored <c>n="0"</c> is a different answer and stays <c>0</c>.</b> Fifteen
        /// rows in <c>GameData.as</c> write it explicitly; <c>closeSub("0")</c> finds no sub and is a
        /// deliberate no-op, which is not what a bare <c>stage</c> means.</para>
        ///
        /// <para><b>The type is <c>int</c> and that is lossy for three rows.</b> <c>@n</c> is a string
        /// in AS3 and three authored values are non-numeric (<c>n='wait'</c>, <c>n='a1'</c>,
        /// <c>n='d3'</c> — all in prob-room <c>out</c>/<c>close</c> scripts, which the port does not run
        /// yet). They arrive here as <c>-1</c> via <c>ParseInt</c>'s fallback rather than as their
        /// authored text; that is recorded rather than papered over, and widening the field is the
        /// change to make when the prob-room event pipeline lands.</para>
        /// </summary>
        [Tooltip("Numeric parameter: stage index, trigger flag value, count, or sub-index (n attribute). " +
                 "Absent means -1, matching AS3 analiz's default — see the doc comment.")]
        public int numericParam = -1;

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
