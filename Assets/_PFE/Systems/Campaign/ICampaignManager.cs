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

        int GetTrigger(string triggerName);
        void SetTrigger(string triggerName, int value = 1);

        bool IsQuestActive(string questId);
        bool IsQuestCompleted(string questId);
        int GetQuestStage(string questId);
        void StartQuest(string questId);
        void AdvanceQuestStage(string questId, int nextStage);
        void CompleteQuest(string questId);

        /// <param name="forceRegenerate">AS3 <c>Game.crea</c> — rebuild even if a layout is cached.</param>
        void TransitionToLand(string targetLandId, string spawnPoint = null, bool forceRegenerate = false);
    }
}
