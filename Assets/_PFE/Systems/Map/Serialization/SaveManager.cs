using System;
using System.Collections.Generic;
using UnityEngine;

namespace PFE.Systems.Map.Serialization
{
    /// <summary>
    /// High-level save/load API for the world.
    /// Manages save slots, auto-save, and quick save/load.
    /// </summary>
    public class SaveManager : MonoBehaviour
    {
        // Singleton instance
        private static SaveManager _instance;
        public static SaveManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    GameObject go = new GameObject("SaveManager");
                    _instance = go.AddComponent<SaveManager>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        // Current save slot
        private string currentSaveId;

        // Reference to the world map (set by game manager)
        private LandMap currentLandMap;

        // Auto-save settings
        [SerializeField] private bool autoSaveEnabled = true;
        [SerializeField] private float autoSaveInterval = 300f;  // 5 minutes
        private float autoSaveTimer;

        // Quick save slot
        private const string QuickSaveSlot = "quicksave";
        private const string AutoSaveSlot = "autosave";

        // Events
        public event Action<string> OnGameSaved;
        public event Action<string> OnGameLoaded;
        public event Action<string> OnSaveFailed;

        /// <summary>
        /// Set the current land map (call this from game manager).
        /// </summary>
        public void SetLandMap(LandMap landMap)
        {
            currentLandMap = landMap;
        }

        /// <summary>
        /// Get the current land map.
        /// </summary>
        public LandMap GetLandMap()
        {
            return currentLandMap;
        }

        /// <summary>
        /// Save the current game state.
        /// </summary>
        public bool SaveGame(string saveId = null, LandMap landMap = null)
        {
            try
            {
                // Use provided map or current map
                LandMap map = landMap ?? currentLandMap;
                if (map == null)
                {
                    Debug.LogError("No LandMap available for save");
                    OnSaveFailed?.Invoke(saveId);
                    return false;
                }

                // Use current save ID if not specified
                if (string.IsNullOrEmpty(saveId))
                {
                    saveId = currentSaveId;
                }

                if (string.IsNullOrEmpty(saveId))
                {
                    Debug.LogError("No save ID specified and no current save slot");
                    OnSaveFailed?.Invoke(saveId);
                    return false;
                }

                // Create player state snapshot
                PlayerStateSnapshot playerState = CreatePlayerStateSnapshot();

                // Create save data
                WorldSaveData saveData = WorldSaveData.CreateFromMap(map, playerState);

                if (saveData == null)
                {
                    Debug.LogError("Failed to create save data");
                    OnSaveFailed?.Invoke(saveId);
                    return false;
                }

                // Set save ID
                saveData.saveId = saveId;
                currentSaveId = saveId;

                // Serialize to file
                bool success = WorldSerializer.SerializeWorld(saveData, saveId);

                if (success)
                {
                    Debug.Log($"Game saved successfully: {saveId}");
                    OnGameSaved?.Invoke(saveId);
                    return true;
                }
                else
                {
                    Debug.LogError($"Failed to save game: {saveId}");
                    OnSaveFailed?.Invoke(saveId);
                    return false;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Exception during save: {e.Message}");
                OnSaveFailed?.Invoke(saveId);
                return false;
            }
        }

        /// <summary>
        /// Load a saved game.
        /// </summary>
        public bool LoadGame(string saveId, LandMap landMap = null)
        {
            try
            {
                if (string.IsNullOrEmpty(saveId))
                {
                    Debug.LogError("Save ID cannot be null or empty");
                    return false;
                }

                // Check if save exists
                if (!WorldSerializer.SaveExists(saveId))
                {
                    Debug.LogError($"Save not found: {saveId}");
                    return false;
                }

                // Deserialize save data
                WorldSaveData saveData = WorldDeserializer.DeserializeWorld(saveId);

                if (saveData == null)
                {
                    Debug.LogError($"Failed to load save: {saveId}");
                    return false;
                }

                // Use provided map or current map
                LandMap map = landMap ?? currentLandMap;
                if (map == null)
                {
                    Debug.LogError("No LandMap available for load");
                    return false;
                }

                // Restore save data to map
                saveData.RestoreToMap(map);

                // Restore player state
                RestorePlayerState(saveData.player);

                // Update current save ID
                currentSaveId = saveId;

                Debug.Log($"Game loaded successfully: {saveId}");
                OnGameLoaded?.Invoke(saveId);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"Exception during load: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Quick save to dedicated slot.
        /// </summary>
        public bool QuickSave(LandMap landMap = null)
        {
            Debug.Log("Performing quick save...");
            return SaveGame(QuickSaveSlot, landMap);
        }

        /// <summary>
        /// Quick load from dedicated slot.
        /// </summary>
        public bool QuickLoad(LandMap landMap = null)
        {
            if (!WorldSerializer.SaveExists(QuickSaveSlot))
            {
                Debug.Log("No quick save found");
                return false;
            }

            Debug.Log("Performing quick load...");
            return LoadGame(QuickSaveSlot, landMap);
        }

        /// <summary>
        /// Delete a save file.
        /// </summary>
        public bool DeleteSave(string saveId)
        {
            bool success = WorldSerializer.DeleteSave(saveId);

            if (success && saveId == currentSaveId)
            {
                currentSaveId = null;
            }

            return success;
        }

        /// <summary>
        /// Get all save metadata.
        /// </summary>
        public List<SaveMetadata> GetAllSaves()
        {
            List<SaveMetadata> saves = new List<SaveMetadata>();

            string[] saveIds = WorldSerializer.GetAllSaveIds();
            foreach (string saveId in saveIds)
            {
                SaveMetadata metadata = WorldDeserializer.GetSaveMetadata(saveId);
                if (metadata != null)
                {
                    saves.Add(metadata);
                }
            }

            // Sort by timestamp (newest first)
            saves.Sort((a, b) => b.timestamp.CompareTo(a.timestamp));

            return saves;
        }

        /// <summary>
        /// Get current save ID.
        /// </summary>
        public string GetCurrentSaveId()
        {
            return currentSaveId;
        }

        /// <summary>
        /// Check if quick save exists.
        /// </summary>
        public bool HasQuickSave()
        {
            return WorldSerializer.SaveExists(QuickSaveSlot);
        }

        /// <summary>
        /// Set auto-save enabled state.
        /// </summary>
        public void SetAutoSaveEnabled(bool enabled)
        {
            autoSaveEnabled = enabled;
        }

        /// <summary>
        /// Set auto-save interval in seconds.
        /// </summary>
        public void SetAutoSaveInterval(float seconds)
        {
            autoSaveInterval = seconds;
        }

        /// <summary>
        /// Trigger auto-save immediately.
        /// </summary>
        public bool TriggerAutoSave()
        {
            if (!autoSaveEnabled)
            {
                return false;
            }

            return SaveGame(AutoSaveSlot);
        }

        // Unity lifecycle
        private void Update()
        {
            // Auto-save timer
            if (autoSaveEnabled)
            {
                autoSaveTimer += Time.deltaTime;
                if (autoSaveTimer >= autoSaveInterval)
                {
                    TriggerAutoSave();
                    autoSaveTimer = 0f;
                }
            }
        }

        private void Awake()
        {
            // Singleton pattern
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
            DontDestroyOnLoad(gameObject);
        }

        // Private helper methods

        private PlayerStateSnapshot CreatePlayerStateSnapshot()
        {
            // TODO: Get actual player state from player controller
            return PlayerStateSnapshot.CreateFromPlayer();
        }

        private void RestorePlayerState(PlayerStateSnapshot playerState)
        {
            if (playerState == null)
            {
                return;
            }

            // TODO: Restore player state to player controller
            // This would position the player, restore health, etc.

            Debug.Log($"Player state restore: pos=({playerState.posX}, {playerState.posY}), " +
                     $"room=({playerState.roomX}, {playerState.roomY}, {playerState.roomZ}), " +
                     $"health={playerState.health}/{playerState.maxHealth}");
        }
    }
}
