using VContainer;
using VContainer.Unity;
using Cysharp.Threading.Tasks;
using UnityEngine;
using PFE.Systems.Map;
using PFE.Data;
using System.Collections.Generic;

namespace PFE.Core
{
    /// <summary>
    /// GameManager - Orchestrates game state and map system.
    /// Integrates WorldBuilder, LandMap, and game initialization.
    /// This is separate from GameLoopManager which handles the low-level tick.
    /// </summary>
    public class GameManager :  IStartable
    {
        private readonly LandMap landMap;
        private readonly RoomGenerator roomGenerator;
        private readonly WorldBuilder worldBuilder;
        private readonly GameDatabase gameDatabase;
        private readonly PfeDebugSettings debugSettings;
        private List<RoomTemplate> loadedRoomTemplates = new List<RoomTemplate>();

        // Game configuration
        private bool isInitialized = false;
        private int currentStage = 1;
        private readonly bool prototypeMapGeneration = true;
        private bool skipWorldBuild = false;

        [Inject]
        public GameManager(
            LandMap landMap,
            RoomGenerator roomGenerator,
            WorldBuilder worldBuilder,
            GameDatabase gameDatabase,
            PfeDebugSettings debugSettings)
        {
            this.landMap = landMap;
            this.roomGenerator = roomGenerator;
            this.worldBuilder = worldBuilder;
            this.gameDatabase = gameDatabase;
            this.debugSettings = debugSettings;
        }

        public async void Start()
        {
            if (debugSettings.LogGameManagerLifecycle)
                Debug.Log("[GameManager] Initializing game...");

            // Initialize database first
            gameDatabase.Initialize();

            // Load room templates
            var roomTemplates = LoadRoomTemplates();
            loadedRoomTemplates = roomTemplates;

            if (roomTemplates == null || roomTemplates.Count == 0)
            {
                Debug.LogError("[GameManager] No room templates loaded! Cannot build world.");
                return;
            }

            // Initialize systems
            roomGenerator.ConfigureGenerationMode(
                usePrototypeMode: prototypeMapGeneration,
                excludeSpecialTypesInRandom: true);
            roomGenerator.Initialize(roomTemplates);
            worldBuilder.Initialize(landMap, roomGenerator, roomTemplates, debugSettings);

            // Build the world (skipped when a debug room override is active)
            if (!skipWorldBuild)
                await BuildWorldAsync();
            else if (debugSettings.LogGameManagerLifecycle)
                Debug.Log("[GameManager] Skipping full world generation (room override active).");

            isInitialized = true;
            if (debugSettings.LogGameManagerLifecycle)
                Debug.Log($"[GameManager] Game initialized successfully!");
        }

        /// <summary>
        /// Load room templates from the content pipeline (GameDatabase → ContentRegistry).
        /// All room templates come through the mod-aware content source system.
        /// </summary>
        private List<RoomTemplate> LoadRoomTemplates()
        {
            var templates = new List<RoomTemplate>(gameDatabase.GetAllRoomTemplates());

            if (templates.Count == 0)
            {
                Debug.LogError("[GameManager] CRITICAL: No room templates found! " +
                    "Content pipeline returned zero templates. " +
                    "Ensure RoomTemplate ScriptableObjects exist in a Resources folder.");
                return templates;
            }

            if (debugSettings.LogRoomTemplateLoadSummary)
            {
                Debug.Log($"[GameManager] Loaded {templates.Count} room templates from content pipeline");
            }

            if (debugSettings.LogLoadedRoomTemplateList)
            {
                foreach (var template in templates)
                {
                    if (template != null)
                    {
                        Debug.Log($"[GameManager] Room template: {template.name} (ID: {template.id})");
                    }
                }
            }

            return templates;
        }

        /// <summary>
        /// Build world asynchronously.
        /// </summary>
        private async UniTask BuildWorldAsync()
        {
            if (debugSettings.LogGameManagerLifecycle)
                Debug.Log("[GameManager] Building world...");

            // Build random world
            bool success = worldBuilder.BuildRandomWorld(currentStage);

            if (!success)
            {
                Debug.LogError("[GameManager] Failed to build world!");
                return;
            }

            // Simulate async loading (for future use with loading screens)
            await UniTask.Delay(100);

            if (debugSettings.LogGameManagerLifecycle)
                Debug.Log($"[GameManager] World built with {landMap.GetRoomCount()} rooms");
        }

        /// <summary>
        /// Start new game at specific stage.
        /// </summary>
        public void NewGame(int stage = 1)
        {
            if (isInitialized)
            {
                Debug.LogWarning("[GameManager] Game already initialized. Use RestartGame() to start over.");
                return;
            }

            currentStage = stage;
            if (debugSettings.LogGameManagerLifecycle)
                Debug.Log($"[GameManager] Starting new game at stage {stage}");
        }

        /// <summary>
        /// Restart the game.
        /// </summary>
        public void RestartGame()
        {
            if (debugSettings.LogGameManagerLifecycle)
                Debug.Log("[GameManager] Restarting game...");

            // Clear existing world
            landMap.Clear();
            roomGenerator.ResetUsageCounts();

            // Rebuild
            BuildWorldAsync().Forget();

            if (debugSettings.LogGameManagerLifecycle)
                Debug.Log("[GameManager] Game restarted");
        }

        /// <summary>
        /// Go to next stage.
        /// </summary>
        public void NextStage()
        {
            currentStage++;
            if (debugSettings.LogGameManagerLifecycle)
                Debug.Log($"[GameManager] Advancing to stage {currentStage}");

            // Clear and rebuild
            landMap.Clear();
            roomGenerator.ResetUsageCounts();

            BuildWorldAsync().Forget();
        }

        /// <summary>
        /// Load specific level.
        /// </summary>
        public void LoadLevel(string levelId)
        {
            if (debugSettings.LogGameManagerLifecycle)
                Debug.Log($"[GameManager] Loading level: {levelId}");

            // TODO: Implement specific level loading
            // This would load pre-defined room layouts instead of procedural generation
        }

        /// <summary>
        /// Save game state.
        /// </summary>
        public void SaveGame()
        {
            if (debugSettings.LogGameManagerLifecycle)
                Debug.Log("[GameManager] Saving game...");

            landMap.SaveState();

            // TODO: Save to file
            if (debugSettings.LogGameManagerLifecycle)
                Debug.Log("[GameManager] Game saved");
        }

        /// <summary>
        /// Load game state.
        /// </summary>
        public void LoadGame()
        {
            if (debugSettings.LogGameManagerLifecycle)
                Debug.Log("[GameManager] Loading game...");

            landMap.LoadState();

            // TODO: Load from file
            if (debugSettings.LogGameManagerLifecycle)
                Debug.Log("[GameManager] Game loaded");
        }

        /// <summary>
        /// Get current land map (for other systems to access).
        /// </summary>
        public LandMap GetLandMap()
        {
            return landMap;
        }

        public IReadOnlyList<RoomTemplate> GetLoadedRoomTemplates()
        {
            return loadedRoomTemplates;
        }

        /// <summary>
        /// Get current room.
        /// </summary>
        public RoomInstance GetCurrentRoom()
        {
            return landMap.currentRoom;
        }

        /// <summary>
        /// Check if game is initialized.
        /// </summary>
        /// <summary>
        /// Called by MapBridge during injection (before Start) when a debug room override
        /// is active, so full world generation is skipped.
        /// </summary>
        public void SetSkipWorldBuild(bool skip)
        {
            skipWorldBuild = skip;
        }

        public bool IsInitialized()
        {
            return isInitialized;
        }
    }
}
