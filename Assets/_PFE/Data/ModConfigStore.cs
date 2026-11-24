using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace PFE.Data
{
    /// <summary>
    /// Everything persisted about the user's mod configuration: which mods are enabled and
    /// the per-mod setting values edited in the Main Menu Mods tab. Setting values are stored
    /// as invariant-culture strings keyed by modId then setting key.
    /// </summary>
    public sealed class ModConfigSnapshot
    {
        public Dictionary<string, bool> Enabled = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Dictionary<string, string>> Settings = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        public string GetSetting(string modId, string key)
        {
            if (string.IsNullOrEmpty(modId) || string.IsNullOrEmpty(key)) return null;
            return Settings.TryGetValue(modId, out var byKey) && byKey.TryGetValue(key, out string value)
                ? value
                : null;
        }

        public void SetSetting(string modId, string key, string value)
        {
            if (string.IsNullOrEmpty(modId) || string.IsNullOrEmpty(key)) return;
            if (!Settings.TryGetValue(modId, out var byKey))
            {
                byKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                Settings[modId] = byKey;
            }
            byKey[key] = value ?? string.Empty;
        }
    }

    /// <summary>
    /// Persistence abstraction for mod enabled/disabled states and mod setting values.
    /// Injectable so EditMode tests can avoid touching disk.
    /// </summary>
    public interface IModConfigStore
    {
        ModConfigSnapshot Load();
        void Save(ModConfigSnapshot snapshot);
    }

    /// <summary>
    /// Stores the mod configuration in <c>mods_config.json</c>. Files written before mod
    /// settings existed (no "settings" array on an entry) load cleanly with empty settings.
    /// </summary>
    public sealed class JsonFileModConfigStore : IModConfigStore
    {
        [Serializable]
        private sealed class ModSettingEntry
        {
            public string key;
            public string value;
        }

        [Serializable]
        private sealed class ModConfigEntry
        {
            public string modId;
            public bool enabled;
            public List<ModSettingEntry> settings;
        }

        [Serializable]
        private sealed class ModConfigFile
        {
            public List<ModConfigEntry> mods = new List<ModConfigEntry>();
        }

        private readonly string _explicitFilePath;

        public JsonFileModConfigStore(string filePath = null)
        {
            _explicitFilePath = filePath;
        }

        public string FilePath => string.IsNullOrEmpty(_explicitFilePath)
            ? Path.Combine(Application.persistentDataPath, "mods_config.json")
            : _explicitFilePath;

        public ModConfigSnapshot Load()
        {
            var result = new ModConfigSnapshot();
            string path = FilePath;
            if (!File.Exists(path))
            {
                return result;
            }

            try
            {
                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return result;
                }

                ModConfigFile config = JsonUtility.FromJson<ModConfigFile>(json);
                if (config?.mods != null)
                {
                    for (int i = 0; i < config.mods.Count; i++)
                    {
                        ModConfigEntry entry = config.mods[i];
                        if (entry == null || string.IsNullOrEmpty(entry.modId)) continue;

                        result.Enabled[entry.modId] = entry.enabled;

                        if (entry.settings != null)
                        {
                            for (int s = 0; s < entry.settings.Count; s++)
                            {
                                ModSettingEntry setting = entry.settings[s];
                                if (setting != null && !string.IsNullOrEmpty(setting.key))
                                {
                                    result.SetSetting(entry.modId, setting.key, setting.value);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ModConfigStore] Failed to load '{path}': {ex.Message}");
            }

            return result;
        }

        public void Save(ModConfigSnapshot snapshot)
        {
            string path = FilePath;
            try
            {
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var config = new ModConfigFile();
                if (snapshot != null)
                {
                    var modIds = new SortedSet<string>(StringComparer.Ordinal);
                    foreach (string modId in snapshot.Enabled.Keys)
                    {
                        if (!string.IsNullOrEmpty(modId)) modIds.Add(modId);
                    }
                    foreach (string modId in snapshot.Settings.Keys)
                    {
                        if (!string.IsNullOrEmpty(modId)) modIds.Add(modId);
                    }

                    foreach (string modId in modIds)
                    {
                        var entry = new ModConfigEntry
                        {
                            modId = modId,
                            enabled = !snapshot.Enabled.TryGetValue(modId, out bool enabled) || enabled,
                            settings = new List<ModSettingEntry>()
                        };

                        if (snapshot.Settings.TryGetValue(modId, out var byKey) && byKey != null)
                        {
                            var keys = new List<string>(byKey.Keys);
                            keys.Sort(StringComparer.Ordinal);
                            foreach (string key in keys)
                            {
                                if (string.IsNullOrEmpty(key)) continue;
                                entry.settings.Add(new ModSettingEntry { key = key, value = byKey[key] });
                            }
                        }

                        config.mods.Add(entry);
                    }
                }

                string json = JsonUtility.ToJson(config, prettyPrint: true);
                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ModConfigStore] Failed to save '{path}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// In-memory config store for EditMode tests that should not touch disk.
    /// </summary>
    public sealed class InMemoryModConfigStore : IModConfigStore
    {
        private readonly ModConfigSnapshot _snapshot = new ModConfigSnapshot();

        public int SaveCount { get; private set; }

        public InMemoryModConfigStore(
            IDictionary<string, bool> initial = null,
            IDictionary<string, Dictionary<string, string>> initialSettings = null)
        {
            if (initial != null)
            {
                foreach (var kvp in initial)
                {
                    if (!string.IsNullOrEmpty(kvp.Key))
                    {
                        _snapshot.Enabled[kvp.Key] = kvp.Value;
                    }
                }
            }

            if (initialSettings != null)
            {
                foreach (var kvp in initialSettings)
                {
                    if (string.IsNullOrEmpty(kvp.Key) || kvp.Value == null) continue;
                    foreach (var setting in kvp.Value)
                    {
                        if (!string.IsNullOrEmpty(setting.Key))
                        {
                            _snapshot.SetSetting(kvp.Key, setting.Key, setting.Value);
                        }
                    }
                }
            }
        }

        public ModConfigSnapshot Load()
        {
            var copy = new ModConfigSnapshot();
            foreach (var kvp in _snapshot.Enabled)
            {
                copy.Enabled[kvp.Key] = kvp.Value;
            }
            foreach (var kvp in _snapshot.Settings)
            {
                foreach (var setting in kvp.Value)
                {
                    copy.SetSetting(kvp.Key, setting.Key, setting.Value);
                }
            }
            return copy;
        }

        public void Save(ModConfigSnapshot snapshot)
        {
            SaveCount++;
            _snapshot.Enabled.Clear();
            _snapshot.Settings.Clear();
            if (snapshot == null) return;

            foreach (var kvp in snapshot.Enabled)
            {
                if (!string.IsNullOrEmpty(kvp.Key))
                {
                    _snapshot.Enabled[kvp.Key] = kvp.Value;
                }
            }
            foreach (var kvp in snapshot.Settings)
            {
                if (kvp.Value == null) continue;
                foreach (var setting in kvp.Value)
                {
                    if (!string.IsNullOrEmpty(setting.Key))
                    {
                        _snapshot.SetSetting(kvp.Key, setting.Key, setting.Value);
                    }
                }
            }
        }
    }
}