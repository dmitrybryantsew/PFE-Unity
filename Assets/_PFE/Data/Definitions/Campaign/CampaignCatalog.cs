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

        /// <summary>
        /// The list instances — and their sizes — that the four indexes above were built from.
        ///
        /// <para><b>Why a one-shot flag is not enough to decide whether an index is valid.</b> Unity
        /// re-applies an asset's serialized data to the <i>live</i> <see cref="ScriptableObject"/> — on an
        /// editor re-import, and on the play-mode domain-reload backup/restore
        /// (<c>BackupInstance</c> / <c>AwakeInstancesAfterBackupRestoration</c>) — and that path never goes
        /// through <see cref="SetEntries"/>. An <c>_isInitialized</c> flag therefore outlives its input,
        /// and the catalogue ends up in a state that looks self-contradictory from the outside:
        /// <see cref="AllLands"/> (which reads <c>lands</c> directly) still lists every land, while
        /// <see cref="GetLand"/> (which reads <c>_landsById</c>) resolves none of them.</para>
        ///
        /// <para><b>Observed live, 2026-10-10.</b> <c>MapBridge</c>'s catalogue lookup returned null for
        /// <c>random_plant</c> (and for <c>begin</c>, <c>nio</c>, <c>rbl</c>), so the land took the authored
        /// path; an authored build places one room per <c>fixedPosition</c> and every procedural template
        /// carries <c>(0,0,0)</c>, so the grid collapsed to a single cell. The player spawned in
        /// <c>rooms_plant/арсенал_0_0_0</c> with no neighbour and could not leave — while the debug
        /// overlay's land list, drawn from <see cref="AllLands"/>, was still fully populated.</para>
        ///
        /// <para>Two things guard against that now: <see cref="Initialize"/> rebuilds when a source list is
        /// replaced or resized, and every lookup rebuilds once on a miss before reporting "not found". The
        /// second is what makes the failure mode self-healing rather than silent, whatever the invalidation
        /// turns out to be.</para>
        /// </summary>
        private List<LandDefinition> _landsIndexSource;
        private int _landsIndexSize = -1;
        private List<QuestDefinition> _questsIndexSource;
        private int _questsIndexSize = -1;
        private List<NpcDefinition> _npcsIndexSource;
        private int _npcsIndexSize = -1;
        private List<CampaignScriptDefinition> _scriptsIndexSource;
        private int _scriptsIndexSize = -1;

        private bool _isInitialized = false;

        public IReadOnlyList<LandDefinition> AllLands => lands;
        public IReadOnlyList<QuestDefinition> AllQuests => quests;
        public IReadOnlyList<NpcDefinition> AllNpcs => npcs;
        public IReadOnlyList<CampaignScriptDefinition> AllScripts => scripts;

        public void Initialize()
        {
            if (_isInitialized && IndexesAreCurrent()) return;
            RebuildIndexes();
        }

        /// <summary>
        /// Rebuild all four indexes from the serialized lists, unconditionally.
        ///
        /// <para>Called by <see cref="Initialize"/> and, on a lookup miss, by the four <c>Get*</c> methods —
        /// a miss is the one moment where a stale index costs correctness rather than speed.</para>
        /// </summary>
        private void RebuildIndexes()
        {
            _landsById.Clear();
            if (lands != null)
            {
                foreach (var land in lands)
                {
                    if (land != null && !string.IsNullOrEmpty(land.landId))
                    {
                        _landsById[land.landId] = land;
                    }
                }
            }
            _landsIndexSource = lands;
            _landsIndexSize = SizeOf(lands);

            _questsById.Clear();
            if (quests != null)
            {
                foreach (var quest in quests)
                {
                    if (quest != null && !string.IsNullOrEmpty(quest.questId))
                    {
                        _questsById[quest.questId] = quest;
                    }
                }
            }
            _questsIndexSource = quests;
            _questsIndexSize = SizeOf(quests);

            _npcsById.Clear();
            if (npcs != null)
            {
                foreach (var npc in npcs)
                {
                    if (npc != null && !string.IsNullOrEmpty(npc.npcId))
                    {
                        _npcsById[npc.npcId] = npc;
                    }
                }
            }
            _npcsIndexSource = npcs;
            _npcsIndexSize = SizeOf(npcs);

            _scriptsById.Clear();
            if (scripts != null)
            {
                foreach (var script in scripts)
                {
                    if (script != null && !string.IsNullOrEmpty(script.scriptId))
                    {
                        _scriptsById[script.scriptId] = script;
                    }
                }
            }
            _scriptsIndexSource = scripts;
            _scriptsIndexSize = SizeOf(scripts);

            _isInitialized = true;
        }

        /// <summary>
        /// Whether every index was built from the list it is currently supposed to describe. A serialized
        /// <c>List&lt;&gt;</c> can be invalidated two ways — Unity hands back a new instance, or it
        /// clear-and-refills the existing one — so both the reference and the size are compared.
        /// </summary>
        private bool IndexesAreCurrent()
        {
            return ReferenceEquals(_landsIndexSource, lands) && _landsIndexSize == SizeOf(lands)
                && ReferenceEquals(_questsIndexSource, quests) && _questsIndexSize == SizeOf(quests)
                && ReferenceEquals(_npcsIndexSource, npcs) && _npcsIndexSize == SizeOf(npcs)
                && ReferenceEquals(_scriptsIndexSource, scripts) && _scriptsIndexSize == SizeOf(scripts);
        }

        private static int SizeOf<T>(List<T> list) => list == null ? -1 : list.Count;

        public LandDefinition GetLand(string id)
        {
            Initialize();
            if (_landsById.TryGetValue(id ?? string.Empty, out var land)) return land;

            RebuildIndexes();
            return _landsById.TryGetValue(id ?? string.Empty, out land) ? land : null;
        }

        public QuestDefinition GetQuest(string id)
        {
            Initialize();
            if (_questsById.TryGetValue(id ?? string.Empty, out var quest)) return quest;

            RebuildIndexes();
            return _questsById.TryGetValue(id ?? string.Empty, out quest) ? quest : null;
        }

        public NpcDefinition GetNpc(string id)
        {
            Initialize();
            if (_npcsById.TryGetValue(id ?? string.Empty, out var npc)) return npc;

            RebuildIndexes();
            return _npcsById.TryGetValue(id ?? string.Empty, out npc) ? npc : null;
        }

        public CampaignScriptDefinition GetScript(string id)
        {
            Initialize();
            if (_scriptsById.TryGetValue(id ?? string.Empty, out var script)) return script;

            RebuildIndexes();
            return _scriptsById.TryGetValue(id ?? string.Empty, out script) ? script : null;
        }

        public bool HasLand(string id)
        {
            return !string.IsNullOrEmpty(id) && GetLand(id) != null;
        }

        public bool HasQuest(string id)
        {
            return !string.IsNullOrEmpty(id) && GetQuest(id) != null;
        }

        public bool HasNpc(string id)
        {
            return !string.IsNullOrEmpty(id) && GetNpc(id) != null;
        }

        public bool HasScript(string id)
        {
            return !string.IsNullOrEmpty(id) && GetScript(id) != null;
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
