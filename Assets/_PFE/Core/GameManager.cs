using VContainer;
using VContainer.Unity;
using Cysharp.Threading.Tasks;
using UnityEngine;
using PFE.Systems.Map;
using PFE.Systems.Map.Serialization;
using PFE.Systems.Map.Streaming;
using PFE.Data;
using PFE.Core.Profiling;
using System;
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
            // Closes the span opened by PfeProfiler.HookAfterSceneLoad. Whatever that region reports
            // as self time is the 485.8 ms hole run 17 found between AfterSceneLoad and this point
            // that no region was covering. Must stay the FIRST statement so it measures the gap and
            // not our own startup work.
            PfeProfiler.CloseSpan();

            if (debugSettings.LogGameManagerLifecycle)
                Debug.Log("[GameManager] Initializing game...");

            // Initialize database first
            using (PfeProfiler.Region("game.db.init", "boot: MEASURED 3493ms (2026-09-25) — the single largest boot item, bigger than all tile rendering. Eleven Resources.LoadAll<T> calls; split per type in BuiltInContentSource"))
            {
                gameDatabase.Initialize();
            }
            PfeProfiler.Mark("game.db.init.done");

            // Load room templates
            List<RoomTemplate> roomTemplates;
            using (PfeProfiler.Region("game.rooms.load", "boot: 556 templates, believed trivial"))
            {
                roomTemplates = LoadRoomTemplates();
            }
            loadedRoomTemplates = roomTemplates;
            PfeProfiler.Mark("game.rooms.load.done", $"count={roomTemplates?.Count ?? 0}");

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
            PfeProfiler.Mark("game.generators.ready");

            // Build the world (skipped when a debug room override is active)
            if (!skipWorldBuild)
                await BuildWorldAsync();
            else if (debugSettings.LogGameManagerLifecycle)
                Debug.Log("[GameManager] Skipping full world generation (room override active).");

            // Hand the freshly built map to SaveManager. P3 built the serialiser, the migration
            // chain and its tests; nothing had ever called SetLandMap, so SaveManager.currentLandMap
            // stayed null and every SaveGame() would have failed with "No LandMap available".
            // Touching SaveManager.Instance here also creates its host GameObject, which is not in
            // any scene — this is the one place that is guaranteed to run before a save is possible.
            SaveManager.Instance.SetLandMap(landMap);
            PfeProfiler.Mark("game.save.wired");

            isInitialized = true;
            PfeProfiler.Mark("game.world.ready");
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

            if (debugSettings.LogMapIntegrityDiagnostics)
            {
                var rawResourceTemplates = Resources.LoadAll<RoomTemplate>("Rooms");
                MapIntegrityDiagnostics.LogTemplateRegistryIntegrity(templates, rawResourceTemplates, debugSettings);
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

            string specificCollection = debugSettings.DebugSpecificRoomCollection;
            bool success;

            if (!string.IsNullOrWhiteSpace(specificCollection))
            {
                var specificTemplates = loadedRoomTemplates.FindAll(template =>
                    template != null &&
                    string.Equals(template.sourceCollectionId, specificCollection, StringComparison.OrdinalIgnoreCase));

                if (debugSettings.LogMapIntegrityDiagnostics)
                {
                    Debug.Log(
                        $"[GameManager] BuildWorldAsync is using BuildSpecificWorld for collection '{specificCollection}' " +
                        $"({specificTemplates.Count} templates).");
                }

                success = worldBuilder.BuildSpecificWorld(specificTemplates);
            }
            else
            {
                if (debugSettings.LogMapIntegrityDiagnostics)
                {
                    Debug.Log("[GameManager] BuildWorldAsync is using BuildRandomWorld(currentStage).");
                }

                success = worldBuilder.BuildRandomWorld(currentStage);
            }

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
        /// Save game state to the quick-save slot.
        ///
        /// <para>Two mechanisms, and both are needed. <c>landMap.SaveState()</c> snapshots each
        /// <c>RoomInstance</c>'s own state; <c>SaveManager.QuickSave()</c> then serialises the whole
        /// <c>LandMap</c> (rooms, doors, fog, player) to disk through P3's
        /// <c>WorldSerializer</c> + migration chain. Dropping the first would save whatever the rooms
        /// last snapshotted rather than what they look like now.</para>
        /// </summary>
        public bool SaveGame()
        {
            if (debugSettings.LogGameManagerLifecycle)
                Debug.Log("[GameManager] Saving game...");

            landMap.SaveState();

            bool saved = SaveManager.Instance.QuickSave(landMap);

            if (debugSettings.LogGameManagerLifecycle)
                Debug.Log(saved ? "[GameManager] Game saved" : "[GameManager] Game save FAILED");

            return saved;
        }

        /// <summary>
        /// Load game state from the quick-save slot.
        /// </summary>
        public bool LoadGame()
        {
            if (debugSettings.LogGameManagerLifecycle)
                Debug.Log("[GameManager] Loading game...");

            // QuickLoad restores the map from disk itself, so the in-memory SaveState()/LoadState()
            // snapshot round-trip that used to be here would only be overwritten. LoadState() is
            // still what non-serialised room streaming uses, so it is called for that path.
            bool loaded = SaveManager.Instance.QuickLoad(landMap);

            if (loaded)
            {
                landMap.LoadState();

                // A load is a room swap, and the swap is three steps, not one. RestoreToMap's
                // SwitchRoom only updated the LandMap's own bookkeeping, so RoomStreamingManager
                // still had the OLD room active and the visual controller still drew the OLD room:
                // loading into a different room left the player standing at the new room's
                // coordinates over the old room's tiles, with the new room's units frozen (an
                // inactive RoomInstance does not tick). Deliberately AFTER LoadState() - the
                // re-render reads tile state, so it has to see the restored tiles.
                var transitionManager = RoomTransitionManager.Instance;
                if (transitionManager != null)
                {
                    transitionManager.ApplyRestoredRoom(landMap.currentRoom);
                }
            }

            if (debugSettings.LogGameManagerLifecycle)
                Debug.Log(loaded ? "[GameManager] Game loaded" : "[GameManager] Game load FAILED (no quick save?)");

            return loaded;
        }

        /// <summary>
        /// Get current land map (for other systems to access).
        /// </summary>
        public LandMap GetLandMap()
        {
            return landMap;
        }

        public WorldBuilder GetWorldBuilder()
        {
            return worldBuilder;
        }

        public RoomGenerator GetRoomGenerator()
        {
            return roomGenerator;
        }

        public IReadOnlyList<RoomTemplate> GetLoadedRoomTemplates()
        {
            return loadedRoomTemplates;
        }

        public List<RoomTemplate> GetTemplatesForCollection(string collectionId)
        {
            if (string.IsNullOrWhiteSpace(collectionId))
                return new List<RoomTemplate>();

            var templates = loadedRoomTemplates.FindAll(t =>
                t != null && string.Equals(t.sourceCollectionId, collectionId, StringComparison.OrdinalIgnoreCase));

            if (templates.Count == 0)
            {
                // Try reloading if newly imported or not yet cached
                ReloadRoomTemplates();
                templates = loadedRoomTemplates.FindAll(t =>
                    t != null && string.Equals(t.sourceCollectionId, collectionId, StringComparison.OrdinalIgnoreCase));
            }

            return templates;
        }

        public void ReloadRoomTemplates()
        {
            loadedRoomTemplates = LoadRoomTemplates();
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
