using System;
using System.Collections.Generic;
using UnityEngine;
using PFE.ModAPI;

namespace PFE.Data.Definitions.Campaign
{
    /// <summary>
    /// Represents an individual dialogue option or branch offered by an NPC.
    /// Corresponds to &lt;dial&gt; tags under &lt;npc&gt; in GameData.as.
    /// </summary>
    [Serializable]
    public class NpcDialogueEntry
    {
        [Tooltip("Dialogue entry ID (e.g. dialCalam2, vendor_weap_hi, dialStorm1)")]
        public string dialogueId = "";

        [Tooltip("Priority or critical conversation flag (imp='1' in AS3)")]
        public bool isImportant = false;

        [Tooltip("Required story trigger condition to show this dialogue (trigger attribute)")]
        public string requiredTrigger = "";

        [Tooltip("Required current land ID (land attribute)")]
        public string requiredLand = "";

        [Tooltip("Required quest ID in player journal (quest attribute)")]
        public string requiredQuest = "";

        [Tooltip("Required quest sub-stage index (sub attribute)")]
        public int requiredQuestStage = 0;

        [Tooltip("Prerequisite dialogue ID that must have been heard previously (prev attribute)")]
        public string previousDialogueId = "";

        [Tooltip("Required player armor equipped (armor attribute)")]
        public string requiredArmor = "";

        [Tooltip("Required player weapon equipped (weap attribute)")]
        public string requiredWeapon = "";

        [Tooltip("Required player level (lvl attribute)")]
        public int requiredLevel = 0;

        [Tooltip("Music track to trigger upon selecting dialogue (music attribute)")]
        public string music = "";

        [Tooltip("Follow-up script actions triggered upon dialogue activation")]
        public List<CampaignScriptAction> actions = new List<CampaignScriptAction>();
    }

    /// <summary>
    /// ScriptableObject definition for an NPC persona and dialogue tree.
    /// Corresponds to &lt;npc&gt; elements in ActionScript 3 GameData.as
    /// and fe.serv.NPC.
    /// </summary>
    [CreateAssetMenu(fileName = "NewNpcDef", menuName = "PFE/Campaign/NPC Definition")]
    public class NpcDefinition : VersionedData, IGameContent
    {
        [Header("Identity")]
        [Tooltip("Unique NPC ID (e.g. calam, calam2, velvet, steel, autodoc, vendor_weapon)")]
        public string npcId = "";

        [Tooltip("Visual asset archetype identifier (vis attribute in AS3)")]
        public string visualId = "";

        [Tooltip("Fallback / canonical name ID for localization lookup (name attribute)")]
        public string aliasName = "";

        [Tooltip("Vendor inventory identifier (vendor attribute)")]
        public string vendorId = "";

        [Tooltip("Interaction behavior archetype: travel, doc, vdoc, adoc, vr, v, nope (inter attribute)")]
        public string interactionType = "";

        [Tooltip("Minimap or dialogue portrait icon index (ico attribute)")]
        public int iconIndex = 0;

        [Tooltip("Context user action verb 1: dial, etc. (ua1 attribute)")]
        public string userAction1 = "";

        [Tooltip("Context user action verb 2: dial, etc. (ua2 attribute)")]
        public string userAction2 = "";

        [Tooltip("Default weapon ID carried (weap attribute)")]
        public string defaultWeapon = "";

        [Tooltip("Silent dialogue toggle - no voice bark (silent='1' in AS3)")]
        public bool isSilent = false;

        // IGameContent implementation
        string IGameContent.ContentId => npcId;
        ContentType IGameContent.ContentType => ContentType.Campaign;
        public override string DataId => npcId;
        public override string DisplayName => string.IsNullOrEmpty(aliasName) ? npcId : aliasName;

        [Header("Dialogue Tree")]
        [Tooltip("All conditional dialogue entries for this NPC")]
        public List<NpcDialogueEntry> dialogues = new List<NpcDialogueEntry>();

        protected override bool OnValidateData()
        {
            if (string.IsNullOrEmpty(npcId))
            {
                Debug.LogWarning("NpcDefinition: npcId is missing!");
                return false;
            }
            return true;
        }
    }
}
