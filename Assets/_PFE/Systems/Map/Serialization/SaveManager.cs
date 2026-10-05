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

        /// <summary>
        /// Slot id that <see cref="QuickSave"/> and <see cref="QuickLoad"/> address. Public so the
        /// developer console (and tests) can name the slot without duplicating the literal.
        /// </summary>
        public const string QuickSaveSlotId = QuickSaveSlot;

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

        // Active inventory reference
        public PFE.Systems.Inventory.GameInventory CurrentInventory { get; set; }

        // Private helper methods

        private PlayerStateSnapshot CreatePlayerStateSnapshot()
        {
            var snapshot = PlayerStateSnapshot.CreateFromPlayer();

            var player = FindFirstObjectByType<PFE.Entities.Player.PlayerController>();
            if (player != null)
            {
                var pos = player.transform.position;
                snapshot.posX = pos.x;
                snapshot.posY = pos.y;
                snapshot.health = player.CurrentHealth;
                snapshot.maxHealth = player.MaxHealth;

                // Which room the save belongs to. The fields existed but were never written, so
                // every save carried (0,0,0) - and a load had no way to tell the motor which tile
                // grid to resolve against. Taken from the map because that is exactly what
                // WorldSaveData.RestoreToMap puts back via SwitchRoom, keeping the two symmetric.
                if (currentLandMap != null)
                {
                    Vector3Int coord = currentLandMap.GetCurrentPosition();
                    snapshot.roomX = coord.x;
                    snapshot.roomY = coord.y;
                    snapshot.roomZ = coord.z;
                }

                // RPG Stats
                var stats = player.GetComponent<PFE.Systems.RPG.CharacterStats>();
                if (stats != null)
                {
                    snapshot.rpgStats = PFE.Systems.RPG.RPGSaveData.FromCharacterStats(stats);
                    snapshot.level = stats.Level;
                    snapshot.experience = stats.Xp;
                }

                // Weapon loadout & Ammo source
                var loadout = player.GetComponent<PFE.Systems.Weapons.PlayerWeaponLoadout>();

                // The player's own inventory first, and it has to be explicit rather than left to the
                // `as GameInventory` arm: `PlayerWeaponLoadout.AmmoSource` is now the PlayerInventory
                // (which routes mutations through the command seam), not the GameInventory itself, so
                // that cast yields null. Left alone, the inventory would be written empty and read back
                // empty with no error anywhere — the silent-data-loss shape this project keeps hitting.
                // The other two arms stay for a rig that has no PlayerInventory component.
                var inv = player.GetComponent<PFE.Systems.Inventory.PlayerInventory>()?.Inventory
                          ?? (loadout?.AmmoSource as PFE.Systems.Inventory.GameInventory)
                          ?? CurrentInventory;
                if (inv != null)
                {
                    snapshot.inventory = inv.CreateSaveData();
                    snapshot.equippedWeaponId = inv.CurrentWeaponId;
                    snapshot.equippedArmorId = inv.CurrentArmorId;
                }

                if (loadout != null && loadout.Current?.State != null)
                {
                    snapshot.weaponRuntime = PFE.Systems.Weapons.WeaponRuntimeSaveData.FromRuntimeState(loadout.Current.State);
                    if (string.IsNullOrEmpty(snapshot.equippedWeaponId) && loadout.Current.State.Def != null)
                    {
                        snapshot.equippedWeaponId = loadout.Current.State.Def.weaponId;
                    }
                }
            }

            return snapshot;
        }

        private void RestorePlayerState(PlayerStateSnapshot playerState)
        {
            if (playerState == null)
            {
                return;
            }

            var player = FindFirstObjectByType<PFE.Entities.Player.PlayerController>();
            if (player != null)
            {
                var worldPos = new Vector3(
                    playerState.posX,
                    playerState.posY,
                    player.transform.position.z);

                // Position must go through the motor, not the transform.
                // TilePhysicsController owns its own pixel position (posX/posY) and WRITES
                // transform.position from it every tick via SyncUnityPosition, so a raw transform
                // assignment is overwritten on the next tick - the player appeared not to move at
                // all, which reads as "load did not restore position".
                var motor = player.GetComponent<PFE.Systems.Physics.TilePhysicsController>();

                RoomInstance room = null;
                if (currentLandMap != null)
                {
                    room = currentLandMap.GetRoom(
                        new Vector3Int(playerState.roomX, playerState.roomY, playerState.roomZ));
                }

                if (motor != null && room != null)
                {
                    // RepositionForRoom sets the room BEFORE the position, which the motor requires:
                    // it resolves tiles against its current room, so a stale room means colliding
                    // against the old grid at the new coordinates. It also clears velocity, matching
                    // AS3, where a unit is rebuilt on every location change.
                    motor.RepositionForRoom(room, worldPos);
                }
                else if (motor != null)
                {
                    motor.SetUnityPosition(worldPos);
                }
                else
                {
                    player.transform.position = worldPos;
                }

                // Health. UnitController.CurrentHealth is a read-only expression over
                // Stats.CurrentHp (see UnitController.cs), so there is nothing to assign on the
                // controller - which is why this was captured on save and then silently dropped.
                // MaxHp first: CurrentHp is clamped against it.
                var unitStats = player.Stats;
                if (unitStats != null)
                {
                    if (playerState.maxHealth > 0f)
                    {
                        unitStats.MaxHp.Value = playerState.maxHealth;
                    }

                    unitStats.CurrentHp.Value = Mathf.Clamp(playerState.health, 0f, unitStats.MaxHp.Value);
                }

                var stats = player.GetComponent<PFE.Systems.RPG.CharacterStats>();
                if (stats != null && playerState.rpgStats != null)
                {
                    PFE.Systems.RPG.CharacterStatsExtensions.LoadSaveData(stats, playerState.rpgStats);
                }

                var loadout = player.GetComponent<PFE.Systems.Weapons.PlayerWeaponLoadout>();

                // The player's own inventory first, and it has to be explicit rather than left to the
                // `as GameInventory` arm: `PlayerWeaponLoadout.AmmoSource` is now the PlayerInventory
                // (which routes mutations through the command seam), not the GameInventory itself, so
                // that cast yields null. Left alone, the inventory would be written empty and read back
                // empty with no error anywhere — the silent-data-loss shape this project keeps hitting.
                // The other two arms stay for a rig that has no PlayerInventory component.
                var inv = player.GetComponent<PFE.Systems.Inventory.PlayerInventory>()?.Inventory
                          ?? (loadout?.AmmoSource as PFE.Systems.Inventory.GameInventory)
                          ?? CurrentInventory;
                if (inv != null && playerState.inventory != null)
                {
                    inv.RestoreFromSaveData(playerState.inventory);
                }

                if (loadout != null && playerState.weaponRuntime != null && loadout.Current?.State != null)
                {
                    playerState.weaponRuntime.RestoreTo(loadout.Current.State);
                }
            }

            // Report what the player actually has, not just what the snapshot asked for. The
            // previous version echoed the snapshot back, so it read identically whether the restore
            // worked or the motor immediately undid it - which is precisely the bug fixed above.
            var restored = FindFirstObjectByType<PFE.Entities.Player.PlayerController>();
            string actualPos = restored != null ? restored.transform.position.ToString() : "n/a";
            var restoredStats = restored != null ? restored.Stats : null;
            string actualHp = restoredStats != null
                ? $"{restoredStats.CurrentHp.Value}/{restoredStats.MaxHp.Value}"
                : "n/a";

            Debug.Log(
                $"Player state restore: saved pos=({playerState.posX}, {playerState.posY}) " +
                $"room=({playerState.roomX}, {playerState.roomY}, {playerState.roomZ}) " +
                $"hp={playerState.health}/{playerState.maxHealth} -> " +
                $"actual pos={actualPos} hp={actualHp}");
        }
    }
}
