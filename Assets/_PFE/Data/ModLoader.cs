using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using PFE.Data.Definitions.Campaign;
using PFE.ModAPI;

namespace PFE.Data
{
    /// <summary>
    /// Discovers, validates, and loads mods from both the Mods/ folder ([Asset] mods)
    /// and loaded assemblies marked with <see cref="PfeModPluginAttribute"/> ([Code] mods).
    /// Persists enabled/disabled state per modId via <see cref="IModConfigStore"/> (mods_config.json).
    /// </summary>
    public class ModLoader
    {
        private readonly IModConfigStore _configStore;
        private readonly IEnumerable<Assembly> _assembliesOverride;
        private readonly Dictionary<string, IModPlugin> _pluginsByModId = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool> _enabledStates = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<string, string>> _settingValues = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Default mods folder path. Relative to Application.persistentDataPath.
        /// Can be overridden for testing.
        /// </summary>
        public string ModsRoot { get; set; }

        /// <summary>Persistence store for per-mod enabled flags.</summary>
        public IModConfigStore ConfigStore => _configStore;

        /// <summary>Registry of enabled procedural land providers and mod console handlers.</summary>
        public ProceduralLandRegistry ProceduralLands { get; }

        /// <summary>Results from the last discovery pass.</summary>
        public List<ModManifest> DiscoveredMods { get; private set; } = new();

        /// <summary>Read-only view of all discovered mods from the last discovery pass.</summary>
        public IReadOnlyList<ModManifest> AllDiscoveredMods => DiscoveredMods;

        /// <summary>Validation warnings/errors from the last discovery pass.</summary>
        public List<string> ValidationLog { get; private set; } = new();

        public ModLoader()
            : this(null, null, null)
        {
        }

        public ModLoader(
            IModConfigStore configStore,
            IEnumerable<Assembly> assembliesToScan = null,
            ProceduralLandRegistry proceduralLands = null)
        {
            ModsRoot = Path.Combine(Application.persistentDataPath, "Mods");
            _configStore = configStore ?? new JsonFileModConfigStore();
            _assembliesOverride = assembliesToScan;
            ProceduralLands = proceduralLands ?? new ProceduralLandRegistry();
        }

        /// <summary>
        /// Sets whether a discovered mod is enabled and persists the configuration.
        /// </summary>
        public void SetModEnabled(string modId, bool enabled)
        {
            if (string.IsNullOrWhiteSpace(modId)) return;

            _enabledStates[modId] = enabled;

            for (int i = 0; i < DiscoveredMods.Count; i++)
            {
                if (string.Equals(DiscoveredMods[i].modId, modId, StringComparison.OrdinalIgnoreCase))
                {
                    DiscoveredMods[i].isEnabled = enabled;
                }
            }

            SaveConfig();
        }

        /// <summary>
        /// Returns whether the given mod is currently enabled (defaults to true if unknown).
        /// </summary>
        public bool IsModEnabled(string modId)
        {
            if (string.IsNullOrWhiteSpace(modId)) return false;
            return !_enabledStates.TryGetValue(modId, out bool enabled) || enabled;
        }

        /// <summary>
        /// Persists the current per-mod enabled flags and setting values to <see cref="ConfigStore"/>.
        /// </summary>
        public void SaveConfig()
        {
            for (int i = 0; i < DiscoveredMods.Count; i++)
            {
                ModManifest m = DiscoveredMods[i];
                if (m != null && !string.IsNullOrEmpty(m.modId) && !_enabledStates.ContainsKey(m.modId))
                {
                    _enabledStates[m.modId] = m.isEnabled;
                }
            }

            var snapshot = new ModConfigSnapshot();
            foreach (var kvp in _enabledStates)
            {
                snapshot.Enabled[kvp.Key] = kvp.Value;
            }
            foreach (var kvp in _settingValues)
            {
                foreach (var setting in kvp.Value)
                {
                    snapshot.SetSetting(kvp.Key, setting.Key, setting.Value);
                }
            }

            _configStore.Save(snapshot);
        }

        /// <summary>
        /// Returns the persisted raw value of one mod setting, or null when never edited.
        /// </summary>
        public string GetModSetting(string modId, string key)
        {
            if (string.IsNullOrEmpty(modId) || string.IsNullOrEmpty(key)) return null;
            return _settingValues.TryGetValue(modId, out var byKey) && byKey.TryGetValue(key, out string value)
                ? value
                : null;
        }

        /// <summary>
        /// Stores one mod setting value (invariant-culture string) and persists the configuration.
        /// </summary>
        public void SetModSetting(string modId, string key, string value)
        {
            if (string.IsNullOrEmpty(modId) || string.IsNullOrEmpty(key)) return;

            if (!_settingValues.TryGetValue(modId, out var byKey))
            {
                byKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _settingValues[modId] = byKey;
            }
            byKey[key] = value ?? string.Empty;

            SaveConfig();
        }

        /// <summary>
        /// The settings schema a code mod exposes via <see cref="IModSettingsProvider"/>,
        /// or null when the mod declares no settings (asset mods, schema-less code mods).
        /// </summary>
        public IReadOnlyList<ModSettingDescriptor> GetSettingsSchema(string modId)
        {
            if (string.IsNullOrEmpty(modId)) return null;
            if (!_pluginsByModId.TryGetValue(modId, out IModPlugin plugin)) return null;
            if (plugin is not IModSettingsProvider settingsProvider) return null;

            IReadOnlyList<ModSettingDescriptor> schema = settingsProvider.GetSettingsSchema();
            return schema != null && schema.Count > 0 ? schema : null;
        }

        /// <summary>True when the mod exposes at least one setting the host can render.</summary>
        public bool HasModSettings(string modId) => GetSettingsSchema(modId) != null;

        /// <summary>
        /// Live settings view scoped to one mod, handed to the mod through the host context
        /// and used by host UI to display current values. Reads fall back to the supplied
        /// default when the stored value is missing or unparsable.
        /// </summary>
        public IModSettings GetSettingsAccessor(string modId)
        {
            return new ModSettingsAccessor(this, modId);
        }

        /// <summary>
        /// Build the complete ordered list of content sources and register enabled
        /// procedural land providers. Disabled mods are skipped completely.
        /// </summary>
        public List<IContentSource> BuildSourceList()
        {
            var sources = new List<IContentSource>();
            ProceduralLands.Clear();

            // Base game is always first
            sources.Add(new BuiltInContentSource());

            // Discover and sort all mods
            List<ModManifest> mods = DiscoverMods();
            foreach (ModManifest manifest in mods)
            {
                if (!manifest.isEnabled)
                {
                    Debug.Log($"[ModLoader] Skipping disabled mod: {manifest.modId}");
                    continue;
                }

                if (!ValidateManifest(manifest))
                    continue;

                if (manifest.isCodeMod)
                {
                    if (_pluginsByModId.TryGetValue(manifest.modId, out IModPlugin plugin) && plugin != null)
                    {
                        var hostContext = new ModHostContext(manifest, ProceduralLands, sources, GetSettingsAccessor(manifest.modId));
                        plugin.Register(hostContext);
                        hostContext.FinalizeDescriptorLands();
                    }
                }
                else
                {
                    sources.Add(new ModContentSource(manifest));
                }
            }

            Debug.Log($"[ModLoader] Built source list: {sources.Count} sources " +
                $"(1 base + {sources.Count - 1} mod sources, {ProceduralLands.Providers.Count} procedural providers)");

            return sources;
        }

        /// <summary>
        /// Scan both the Mods/ folder ([Asset] mods) and loaded assemblies ([Code] plugins).
        /// </summary>
        public List<ModManifest> DiscoverMods()
        {
            DiscoveredMods.Clear();
            ValidationLog.Clear();
            _pluginsByModId.Clear();

            ModConfigSnapshot persisted = _configStore.Load();
            foreach (var kvp in persisted.Enabled)
            {
                _enabledStates[kvp.Key] = kvp.Value;
            }
            foreach (var kvp in persisted.Settings)
            {
                if (kvp.Value == null) continue;
                foreach (var setting in kvp.Value)
                {
                    if (string.IsNullOrEmpty(setting.Key)) continue;
                    if (!_settingValues.TryGetValue(kvp.Key, out var byKey))
                    {
                        byKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        _settingValues[kvp.Key] = byKey;
                    }
                    byKey[setting.Key] = setting.Value;
                }
            }

            DiscoverFolderMods();
            DiscoverCodePluginMods();

            SortModsWithDependencies(DiscoveredMods);
            return DiscoveredMods;
        }

        private void DiscoverFolderMods()
        {
            if (string.IsNullOrEmpty(ModsRoot) || !Directory.Exists(ModsRoot))
            {
                return;
            }

            string[] modFolders = Directory.GetDirectories(ModsRoot);
            foreach (string folder in modFolders)
            {
                string manifestPath = Path.Combine(folder, "manifest.json");
                if (!File.Exists(manifestPath))
                {
                    ValidationLog.Add($"Skipping {Path.GetFileName(folder)}: no manifest.json");
                    continue;
                }

                try
                {
                    string json = File.ReadAllText(manifestPath);
                    ModManifest manifest = JsonUtility.FromJson<ModManifest>(json);
                    if (manifest == null || string.IsNullOrEmpty(manifest.modId))
                    {
                        ValidationLog.Add($"Skipping {Path.GetFileName(folder)}: modId is empty");
                        continue;
                    }

                    manifest.folderPath = folder;
                    manifest.isCodeMod = false;
                    manifest.isEnabled = IsModEnabled(manifest.modId);

                    DiscoveredMods.Add(manifest);
                    Debug.Log($"[ModLoader] Discovered asset mod: {manifest.modId} v{manifest.version} by {manifest.author}");
                }
                catch (Exception e)
                {
                    ValidationLog.Add($"Error reading {Path.GetFileName(folder)}/manifest.json: {e.Message}");
                    Debug.LogWarning($"[ModLoader] Failed to parse manifest in {folder}: {e.Message}");
                }
            }
        }

        private void DiscoverCodePluginMods()
        {
            IEnumerable<Assembly> assemblies = _assembliesOverride ?? AppDomain.CurrentDomain.GetAssemblies();
            foreach (Assembly assembly in assemblies)
            {
                if (assembly == null || assembly.IsDynamic) continue;

                object[] attributes;
                try
                {
                    attributes = assembly.GetCustomAttributes(typeof(PfeModPluginAttribute), inherit: false);
                }
                catch (Exception ex)
                {
                    ValidationLog.Add($"Skipping assembly {assembly.GetName().Name}: {ex.Message}");
                    continue;
                }

                if (attributes == null || attributes.Length == 0) continue;

                for (int i = 0; i < attributes.Length; i++)
                {
                    if (attributes[i] is not PfeModPluginAttribute pluginAttr || pluginAttr.PluginType == null)
                        continue;

                    if (!typeof(IModPlugin).IsAssignableFrom(pluginAttr.PluginType))
                    {
                        ValidationLog.Add($"Plugin type {pluginAttr.PluginType.FullName} does not implement IModPlugin");
                        continue;
                    }

                    try
                    {
                        if (Activator.CreateInstance(pluginAttr.PluginType) is not IModPlugin plugin)
                        {
                            ValidationLog.Add($"Failed to instantiate plugin {pluginAttr.PluginType.FullName}");
                            continue;
                        }

                        ModManifest manifest = plugin.Manifest;
                        if (manifest == null || string.IsNullOrEmpty(manifest.modId))
                        {
                            ValidationLog.Add($"Plugin {pluginAttr.PluginType.FullName} returned a null or empty modId");
                            continue;
                        }

                        manifest.isCodeMod = true;
                        manifest.isEnabled = IsModEnabled(manifest.modId);

                        _pluginsByModId[manifest.modId] = plugin;
                        DiscoveredMods.Add(manifest);
                        Debug.Log($"[ModLoader] Discovered code mod: {manifest.modId} v{manifest.version} by {manifest.author}");
                    }
                    catch (Exception ex)
                    {
                        ValidationLog.Add($"Error instantiating plugin {pluginAttr.PluginType.FullName}: {ex.Message}");
                        Debug.LogWarning($"[ModLoader] Failed to instantiate plugin {pluginAttr.PluginType.FullName}: {ex.Message}");
                    }
                }
            }
        }

        private static void SortModsWithDependencies(List<ModManifest> mods)
        {
            mods.Sort((a, b) =>
            {
                int orderCmp = a.loadOrder.CompareTo(b.loadOrder);
                return orderCmp != 0 ? orderCmp : string.Compare(a.modId, b.modId, StringComparison.Ordinal);
            });

            // Stable topological refinement so dependencies appear before dependents when declared.
            var byId = new Dictionary<string, ModManifest>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < mods.Count; i++)
            {
                if (!string.IsNullOrEmpty(mods[i].modId) && !byId.ContainsKey(mods[i].modId))
                {
                    byId[mods[i].modId] = mods[i];
                }
            }

            var ordered = new List<ModManifest>(mods.Count);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Visit(ModManifest mod)
            {
                if (mod == null || string.IsNullOrEmpty(mod.modId)) return;
                if (visited.Contains(mod.modId)) return;
                if (!visiting.Add(mod.modId)) return; // Cycle guard

                if (mod.dependencies != null)
                {
                    for (int i = 0; i < mod.dependencies.Length; i++)
                    {
                        string depId = mod.dependencies[i];
                        if (!string.IsNullOrEmpty(depId) && byId.TryGetValue(depId, out ModManifest depMod))
                        {
                            Visit(depMod);
                        }
                    }
                }

                visiting.Remove(mod.modId);
                if (visited.Add(mod.modId))
                {
                    ordered.Add(mod);
                }
            }

            for (int i = 0; i < mods.Count; i++)
            {
                Visit(mods[i]);
            }

            mods.Clear();
            mods.AddRange(ordered);
        }

        /// <summary>
        /// Validate a mod manifest for compatibility.
        /// </summary>
        private bool ValidateManifest(ModManifest manifest)
        {
            bool valid = true;

            if (string.IsNullOrEmpty(manifest.modId))
            {
                ValidationLog.Add("Mod has empty modId");
                valid = false;
            }

            int count = DiscoveredMods.Count(m => string.Equals(m.modId, manifest.modId, StringComparison.OrdinalIgnoreCase));
            if (count > 1)
            {
                ValidationLog.Add($"Duplicate modId: {manifest.modId} (found {count} times)");
                valid = false;
            }

            if (manifest.dependencies != null)
            {
                foreach (string dep in manifest.dependencies)
                {
                    if (string.Equals(dep, "pfe.base", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!DiscoveredMods.Any(m => string.Equals(m.modId, dep, StringComparison.OrdinalIgnoreCase) && m.isEnabled))
                    {
                        ValidationLog.Add($"{manifest.modId}: missing dependency '{dep}'");
                        Debug.LogWarning($"[ModLoader] {manifest.modId}: missing dependency '{dep}'");
                        valid = false;
                    }
                }
            }

            return valid;
        }

        private sealed class ModHostContext : IModHostContext
        {
            private readonly ModManifest _manifest;
            private readonly ProceduralLandRegistry _proceduralRegistry;
            private readonly List<IContentSource> _sources;
            private readonly List<ProceduralLandDescriptor> _descriptors = new();

            public ModHostContext(
                ModManifest manifest,
                ProceduralLandRegistry proceduralRegistry,
                List<IContentSource> sources,
                IModSettings settings)
            {
                _manifest = manifest;
                _proceduralRegistry = proceduralRegistry;
                _sources = sources;
                Settings = settings;
            }

            public IModSettings Settings { get; }

            public void RegisterContentSource(IContentSource source)
            {
                if (source != null)
                {
                    _sources.Add(source);
                }
            }

            public void RegisterProceduralLandProvider(IProceduralLandProvider provider)
            {
                if (provider == null) return;

                _proceduralRegistry.RegisterProvider(provider);

                IReadOnlyList<ProceduralLandDescriptor> descriptors = provider.GetDescriptors();
                if (descriptors != null)
                {
                    for (int i = 0; i < descriptors.Count; i++)
                    {
                        if (descriptors[i] != null && !string.IsNullOrEmpty(descriptors[i].LandId))
                        {
                            _descriptors.Add(descriptors[i]);
                        }
                    }
                }
            }

            public void RegisterConsoleCommands(string prefix, object commandHandler)
            {
                _proceduralRegistry.RegisterConsoleCommands(prefix, commandHandler);
            }

            public void FinalizeDescriptorLands()
            {
                if (_descriptors.Count > 0)
                {
                    _sources.Add(new ProceduralDescriptorContentSource(_manifest, _descriptors));
                }
            }
        }

        private sealed class ProceduralDescriptorContentSource : IContentSource
        {
            private readonly ModManifest _manifest;
            private readonly List<ProceduralLandDescriptor> _descriptors;

            public ModManifest Manifest => _manifest;

            public ProceduralDescriptorContentSource(ModManifest manifest, List<ProceduralLandDescriptor> descriptors)
            {
                _manifest = manifest;
                _descriptors = descriptors;
            }

            public void RegisterContent(IContentRegistry registry)
            {
                if (registry == null) return;

                for (int i = 0; i < _descriptors.Count; i++)
                {
                    ProceduralLandDescriptor desc = _descriptors[i];
                    if (desc == null || string.IsNullOrEmpty(desc.LandId)) continue;

                    // Skip if an explicit LandDefinition was already registered by a content source.
                    if (registry.Contains(ContentType.Campaign, desc.LandId)) continue;

                    LandDefinition land = ScriptableObject.CreateInstance<LandDefinition>();
                    land.name = desc.LandId;
                    land.landId = desc.LandId;
                    land.landName = string.IsNullOrEmpty(desc.DisplayName) ? desc.LandId : desc.DisplayName;
                    land.tip = string.IsNullOrEmpty(desc.Tip) ? "rnd" : desc.Tip;
                    land.isProcedural = true;
                    land.isModProcedural = true;
                    land.stage = desc.Stage > 0 ? desc.Stage : 1;
                    land.gridWidth = Mathf.Max(1, desc.InitialBoundsMax.x - desc.InitialBoundsMin.x);
                    land.gridHeight = Mathf.Max(1, desc.InitialBoundsMax.y - desc.InitialBoundsMin.y);

                    registry.Register(_manifest, land, ConflictPolicy.Additive);
                }
            }
        }
    }

    /// <summary>
    /// Live <see cref="IModSettings"/> implementation scoped to one mod: reads through to the
    /// loader's current values, so edits made in the Mods tab are visible on the next read
    /// without re-registering the mod. Missing or unparsable values yield the caller's fallback.
    /// </summary>
    public sealed class ModSettingsAccessor : IModSettings
    {
        private readonly ModLoader _loader;
        private readonly string _modId;

        public ModSettingsAccessor(ModLoader loader, string modId)
        {
            _loader = loader;
            _modId = modId;
        }

        public int GetInt(string key, int fallback = 0)
        {
            string raw = _loader?.GetModSetting(_modId, key);
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                ? value
                : fallback;
        }

        public float GetFloat(string key, float fallback = 0f)
        {
            string raw = _loader?.GetModSetting(_modId, key);
            return float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
                ? value
                : fallback;
        }

        public bool GetBool(string key, bool fallback = false)
        {
            string raw = _loader?.GetModSetting(_modId, key);
            if (string.IsNullOrEmpty(raw)) return fallback;
            if (bool.TryParse(raw, out bool value)) return value;
            if (raw == "1") return true;
            if (raw == "0") return false;
            return fallback;
        }

        public string GetString(string key, string fallback = "")
        {
            string raw = _loader?.GetModSetting(_modId, key);
            return string.IsNullOrEmpty(raw) ? fallback : raw;
        }
    }

    /// <summary>
    /// Content source for a user-installed asset mod.
    /// Loads ScriptableObjects from AssetBundles in the mod's bundles/ folder.
    /// </summary>
    public class ModContentSource : IContentSource
    {
        readonly ModManifest _manifest;
        readonly HashSet<string> _overrideKeys = new();
        AssetBundle _loadedBundle;

        public ModManifest Manifest => _manifest;

        public ModContentSource(ModManifest manifest)
        {
            _manifest = manifest;

            if (manifest.overrides != null)
            {
                foreach (var key in manifest.overrides)
                    _overrideKeys.Add(key);
            }
        }

        public void RegisterContent(IContentRegistry registry)
        {
            int count = LoadFromBundles(registry);
            Debug.Log($"[ModContentSource] {_manifest.modId}: registered {count} content entries");
        }

        int LoadFromBundles(IContentRegistry registry)
        {
            if (string.IsNullOrEmpty(_manifest.folderPath))
                return 0;

            string bundlesPath = Path.Combine(_manifest.folderPath, "bundles");
            if (!Directory.Exists(bundlesPath))
                return 0;

            int count = 0;
            var bundleFiles = Directory.GetFiles(bundlesPath, "*.bundle");

            foreach (var bundlePath in bundleFiles)
            {
                try
                {
                    _loadedBundle = AssetBundle.LoadFromFile(bundlePath);
                    if (_loadedBundle == null)
                    {
                        Debug.LogWarning($"[ModContentSource] {_manifest.modId}: failed to load bundle {bundlePath}");
                        continue;
                    }

                    var allAssets = _loadedBundle.LoadAllAssets<ScriptableObject>();
                    foreach (var asset in allAssets)
                    {
                        if (asset is IGameContent gameContent)
                        {
                            string overrideKey = $"{gameContent.ContentType}/{gameContent.ContentId}";
                            var policy = _overrideKeys.Contains(overrideKey)
                                ? ConflictPolicy.Override
                                : ConflictPolicy.Additive;
                            registry.Register(_manifest, asset, policy);
                            count++;
                        }
                    }

                    Debug.Log($"[ModContentSource] {_manifest.modId}: loaded {allAssets.Length} assets from {Path.GetFileName(bundlePath)}");
                }
                catch (Exception e)
                {
                    Debug.LogError($"[ModContentSource] {_manifest.modId}: error loading bundle {bundlePath}: {e.Message}");
                }
            }

            return count;
        }

        public void Unload()
        {
            if (_loadedBundle != null)
            {
                _loadedBundle.Unload(false);
                _loadedBundle = null;
            }
        }
    }
}
