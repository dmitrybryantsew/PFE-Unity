using System;
using System.Collections.Generic;
using UnityEngine;
using R3;
using MessagePipe;
using VContainer.Unity;
using PFE.Core.Messages;
using PFE.Data.Definitions.Campaign;

namespace PFE.Systems.Campaign
{
    /// <summary>
    /// Runtime Campaign Manager.
    /// Manages active land state, quest tracking, persistent world triggers,
    /// and listens to LandTransitionMessage from AreaTriggerSystem and script actions.
    /// </summary>
    public class CampaignManager : ICampaignManager, IInitializable, IDisposable
    {
        private readonly ISubscriber<LandTransitionMessage> _transitionSubscriber;
        private readonly IPublisher<LandTransitionMessage> _transitionPublisher;
        private readonly CampaignCatalog _catalog;

        private IDisposable _transitionSubscription;

        // Reactive State
        private readonly ReactiveProperty<string> _currentLandId = new ReactiveProperty<string>("");
        private readonly ReactiveProperty<LandDefinition> _currentLand = new ReactiveProperty<LandDefinition>(null);

        public ReadOnlyReactiveProperty<string> CurrentLandId => _currentLandId;
        public ReadOnlyReactiveProperty<LandDefinition> CurrentLand => _currentLand;
        public CampaignCatalog Catalog => _catalog;

        // Story triggers (World.w.game.triggers)
        private readonly Dictionary<string, int> _triggers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Quest progress
        private readonly Dictionary<string, int> _questStages = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _completedQuests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _activeQuests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public CampaignManager(
            ISubscriber<LandTransitionMessage> transitionSubscriber = null,
            IPublisher<LandTransitionMessage> transitionPublisher = null,
            CampaignCatalog catalog = null)
        {
            _transitionSubscriber = transitionSubscriber;
            _transitionPublisher = transitionPublisher;

            if (catalog != null)
            {
                _catalog = catalog;
            }
            else
            {
                _catalog = Resources.Load<CampaignCatalog>("CampaignCatalog");
            }

            if (_catalog != null)
            {
                _catalog.Initialize();
            }
        }

        public void Initialize()
        {
            if (_transitionSubscriber != null)
            {
                _transitionSubscription = _transitionSubscriber.Subscribe(OnLandTransitionRequested);
            }

            // Start in starting land if uninitialized
            if (string.IsNullOrEmpty(_currentLandId.Value) && _catalog != null)
            {
                string startLand = !string.IsNullOrEmpty(_catalog.startingLandId) ? _catalog.startingLandId : "begin";
                TransitionToLand(startLand);
            }
        }

        public void Dispose()
        {
            _transitionSubscription?.Dispose();
            _currentLandId?.Dispose();
            _currentLand?.Dispose();
        }

        private void OnLandTransitionRequested(LandTransitionMessage msg)
        {
            Debug.Log($"[CampaignManager] Received LandTransitionMessage: TargetLand='{msg.TargetLand}', Coords='{msg.TargetCoordinates}'");
            TransitionToLand(msg.TargetLand, msg.TargetCoordinates);
        }

        public void TransitionToLand(string targetLandId, string spawnPoint = null)
        {
            if (string.IsNullOrWhiteSpace(targetLandId))
            {
                Debug.LogWarning("[CampaignManager] Attempted transition to empty land ID.");
                return;
            }

            if (_catalog != null)
            {
                var landDef = _catalog.GetLand(targetLandId);
                if (landDef != null)
                {
                    _currentLandId.Value = landDef.landId;
                    _currentLand.Value = landDef;
                    Debug.Log($"[CampaignManager] Successfully transitioned to land '{landDef.landId}' ({landDef.DisplayName}). Rooms: {landDef.roomTemplates.Count}");
                    return;
                }
            }

            // Fallback if catalog not loaded
            _currentLandId.Value = targetLandId;
            Debug.Log($"[CampaignManager] Transitioned to land '{targetLandId}' (without catalog definition).");
        }

        public int GetTrigger(string triggerName)
        {
            if (string.IsNullOrEmpty(triggerName)) return 0;
            return _triggers.TryGetValue(triggerName, out int val) ? val : 0;
        }

        public void SetTrigger(string triggerName, int value = 1)
        {
            if (string.IsNullOrEmpty(triggerName)) return;
            _triggers[triggerName] = value;
            Debug.Log($"[CampaignManager] Trigger '{triggerName}' set to {value}.");
        }

        public bool IsQuestActive(string questId)
        {
            return !string.IsNullOrEmpty(questId) && _activeQuests.Contains(questId);
        }

        public bool IsQuestCompleted(string questId)
        {
            return !string.IsNullOrEmpty(questId) && _completedQuests.Contains(questId);
        }

        public int GetQuestStage(string questId)
        {
            if (string.IsNullOrEmpty(questId)) return 0;
            return _questStages.TryGetValue(questId, out int stage) ? stage : 0;
        }

        public void StartQuest(string questId)
        {
            if (string.IsNullOrEmpty(questId)) return;
            _activeQuests.Add(questId);
            if (!_questStages.ContainsKey(questId))
            {
                _questStages[questId] = 1;
            }
            Debug.Log($"[CampaignManager] Started quest '{questId}'.");
        }

        public void AdvanceQuestStage(string questId, int nextStage)
        {
            if (string.IsNullOrEmpty(questId)) return;
            _activeQuests.Add(questId);
            _questStages[questId] = nextStage;
            Debug.Log($"[CampaignManager] Quest '{questId}' advanced to stage {nextStage}.");
        }

        public void CompleteQuest(string questId)
        {
            if (string.IsNullOrEmpty(questId)) return;
            _activeQuests.Remove(questId);
            _completedQuests.Add(questId);

            Debug.Log($"[CampaignManager] Quest '{questId}' completed.");

            // Check if there is a chained next quest
            if (_catalog != null)
            {
                var questDef = _catalog.GetQuest(questId);
                if (questDef != null && !string.IsNullOrEmpty(questDef.nextQuestId))
                {
                    StartQuest(questDef.nextQuestId);
                }
            }
        }
    }
}
