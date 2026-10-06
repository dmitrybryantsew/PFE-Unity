using System;
using System.Collections.Generic;
using UnityEngine;

namespace PFE.Data.Definitions.Campaign
{
    /// <summary>
    /// Master catalog referencing all imported Lands, Quests, NPCs, and Scripts.
    /// Provides rapid ID lookup for CampaignManager, GameManager, and Editor validation tools.
    /// </summary>
    [CreateAssetMenu(fileName = "CampaignCatalog", menuName = "PFE/Campaign/Campaign Catalog")]
    public class CampaignCatalog : ScriptableObject
    {
        [Header("Starting Land")]
        [Tooltip("The initial prologue land ID where a new game begins (default: begin)")]
        public string startingLandId = "begin";

        [Tooltip("The player hub safe haven land ID (default: rbl)")]
        public string playerHubLandId = "rbl";

        [Header("Lands")]
        [SerializeField]
        private List<LandDefinition> lands = new List<LandDefinition>();

        [Header("Quests")]
        [SerializeField]
        private List<QuestDefinition> quests = new List<QuestDefinition>();

        [Header("NPCs")]
        [SerializeField]
        private List<NpcDefinition> npcs = new List<NpcDefinition>();

        [Header("Scripts")]
        [SerializeField]
        private List<CampaignScriptDefinition> scripts = new List<CampaignScriptDefinition>();

        // Runtime lookups
        private readonly Dictionary<string, LandDefinition> _landsById = new Dictionary<string, LandDefinition>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, QuestDefinition> _questsById = new Dictionary<string, QuestDefinition>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, NpcDefinition> _npcsById = new Dictionary<string, NpcDefinition>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CampaignScriptDefinition> _scriptsById = new Dictionary<string, CampaignScriptDefinition>(StringComparer.OrdinalIgnoreCase);
        private bool _isInitialized = false;

        public IReadOnlyList<LandDefinition> AllLands => lands;
        public IReadOnlyList<QuestDefinition> AllQuests => quests;
        public IReadOnlyList<NpcDefinition> AllNpcs => npcs;
        public IReadOnlyList<CampaignScriptDefinition> AllScripts => scripts;

        public void Initialize()
        {
            if (_isInitialized) return;

            _landsById.Clear();
            foreach (var land in lands)
            {
                if (land != null && !string.IsNullOrEmpty(land.landId))
                {
                    _landsById[land.landId] = land;
                }
            }

            _questsById.Clear();
            foreach (var quest in quests)
            {
                if (quest != null && !string.IsNullOrEmpty(quest.questId))
                {
                    _questsById[quest.questId] = quest;
                }
            }

            _npcsById.Clear();
            foreach (var npc in npcs)
            {
                if (npc != null && !string.IsNullOrEmpty(npc.npcId))
                {
                    _npcsById[npc.npcId] = npc;
                }
            }

            _scriptsById.Clear();
            foreach (var script in scripts)
            {
                if (script != null && !string.IsNullOrEmpty(script.scriptId))
                {
                    _scriptsById[script.scriptId] = script;
                }
            }

            _isInitialized = true;
        }

        public LandDefinition GetLand(string id)
        {
            Initialize();
            return _landsById.TryGetValue(id ?? string.Empty, out var land) ? land : null;
        }

        public QuestDefinition GetQuest(string id)
        {
            Initialize();
            return _questsById.TryGetValue(id ?? string.Empty, out var quest) ? quest : null;
        }

        public NpcDefinition GetNpc(string id)
        {
            Initialize();
            return _npcsById.TryGetValue(id ?? string.Empty, out var npc) ? npc : null;
        }

        public CampaignScriptDefinition GetScript(string id)
        {
            Initialize();
            return _scriptsById.TryGetValue(id ?? string.Empty, out var script) ? script : null;
        }

        public bool HasLand(string id)
        {
            Initialize();
            return !string.IsNullOrEmpty(id) && _landsById.ContainsKey(id);
        }

        public bool HasQuest(string id)
        {
            Initialize();
            return !string.IsNullOrEmpty(id) && _questsById.ContainsKey(id);
        }

        public bool HasNpc(string id)
        {
            Initialize();
            return !string.IsNullOrEmpty(id) && _npcsById.ContainsKey(id);
        }

        public bool HasScript(string id)
        {
            Initialize();
            return !string.IsNullOrEmpty(id) && _scriptsById.ContainsKey(id);
        }

        public void SetEntries(
            List<LandDefinition> newLands,
            List<QuestDefinition> newQuests,
            List<NpcDefinition> newNpcs = null,
            List<CampaignScriptDefinition> newScripts = null)
        {
            if (newLands != null) lands = new List<LandDefinition>(newLands);
            if (newQuests != null) quests = new List<QuestDefinition>(newQuests);
            if (newNpcs != null) npcs = new List<NpcDefinition>(newNpcs);
            if (newScripts != null) scripts = new List<CampaignScriptDefinition>(newScripts);

            _isInitialized = false;
            Initialize();
        }
    }
}
