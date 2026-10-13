using System;
using System.Collections.Generic;
using R3;
using PFE.Data.Definitions.Campaign;

namespace PFE.Systems.Campaign
{
    /// <summary>
    /// Contract for managing campaign lands, quest states, persistent story triggers, and world progression.
    /// </summary>
    public interface ICampaignManager
    {
        ReadOnlyReactiveProperty<string> CurrentLandId { get; }
        ReadOnlyReactiveProperty<LandDefinition> CurrentLand { get; }

        CampaignCatalog Catalog { get; }

        /// <summary>
        /// Read a story trigger. AS3 <c>World.w.game.triggers[name]</c> (<c>Game.as:25</c>).
        ///
        /// <para>Returns the raw <see cref="TriggerValue"/> rather than an <c>int</c>, because the oracle's
        /// table holds strings as well as ints (<c>"wait"</c> at <c>NPC.as:591</c>; a quest <c>@set</c>
        /// value at <c>Quest.as:332</c>) and an <c>int</c> return could not represent them. An absent key
        /// reads as <see cref="TriggerValue.Unset"/>, which is a different answer from a stored zero.</para>
        ///
        /// <para>Callers that want the numeric behaviour use <c>.AsInt(0)</c>,
        /// <c>.IsGreaterThanZero()</c> or <c>.EqualsInt(1)</c> — which implement AS3's coercing
        /// comparison, so a string <c>"1"</c> satisfies an int <c>== 1</c> exactly as the oracle's does.</para>
        /// </summary>
        TriggerValue GetTrigger(string triggerName);

        /// <summary>Set a trigger to a number. AS3 <c>setTrigger(name, value = 1)</c>
        /// (<c>Game.as:676-679</c>) — the default is <c>1</c>.</summary>
        void SetTrigger(string triggerName, int value = 1);

        /// <summary>
        /// Set a trigger to any value, including a string. This is the overload that makes
        /// <c>"wait"</c> and a quest <c>@set</c> value storable at all.
        /// </summary>
        void SetTrigger(string triggerName, TriggerValue value);

        bool IsQuestActive(string questId);
        bool IsQuestCompleted(string questId);

        /// <summary>How many of the quest's sub-objectives are done. The oracle has no stage cursor — see
        /// <c>QuestRules</c> — so this is a count of finished subs, not a position.</summary>
        int GetQuestStage(string questId);

        /// <summary>AS3 <c>Game.addQuest(id)</c> (<c>Game.as:527-581</c>) — the <c>quest</c> script
        /// command. Returns the runtime state, or <c>null</c> when the id is not in the catalogue.</summary>
        QuestRuntimeState AddQuest(string questId, bool noVis = false);

        /// <summary>AS3 <c>Game.showQuest(id, n)</c> (<c>Game.as:583-611</c>) — the <c>showstage</c>
        /// script command.</summary>
        void ShowQuest(string questId, string n);

        /// <summary>
        /// AS3 <c>Game.closeQuest(id, n)</c> (<c>Game.as:613-632</c>) — the <c>stage</c> script command.
        /// <paramref name="n"/> absent, empty or negative completes the <b>whole</b> quest; otherwise only
        /// that sub-objective closes.
        /// </summary>
        void CloseQuest(string questId, string n);

        /// <summary>AS3 <c>Game.addQuest(id)</c> under its old name. Kept because the debug overlay and
        /// the tests speak it.</summary>
        void StartQuest(string questId);

        /// <summary>
        /// <b>A shim.</b> The old model stored a monotonic index; the oracle's <c>stage val n</c> means
        /// "mark sub <c>n</c> complete". Forwards to <see cref="CloseQuest"/>.
        /// </summary>
        void AdvanceQuestStage(string questId, int nextStage);

        /// <summary>AS3 <c>Quest.close()</c> — completes the whole quest.</summary>
        void CompleteQuest(string questId);

        /// <param name="forceRegenerate">AS3 <c>Game.crea</c> — rebuild even if a layout is cached.</param>
        void TransitionToLand(string targetLandId, string spawnPoint = null, bool forceRegenerate = false);
    }
}
