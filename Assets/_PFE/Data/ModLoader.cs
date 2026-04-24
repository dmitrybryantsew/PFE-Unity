using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using PFE.ModAPI;

namespace PFE.Data
{
    /// <summary>
    /// Discovers, validates, and loads mods from the Mods/ folder.
    /// Returns an ordered list of IContentSource instances ready for registration.
    ///
    /// Load order:
    /// 1. pfe.base (always first, loadOrder=0)
    /// 2. Mods sorted by loadOrder, then alphabetically by modId
    ///
    /// Mod folder structure:
    ///   {ModsRoot}/author.modname/manifest.json
    /// </summary>
    public class ModLoader
    {
        /// <summary>
        /// Default mods folder path. Relative to Application.persistentDataPath.
        /// Can be overridden for testing.
        /// </summary>
        public string ModsRoot { get; set; }

        /// <summary>Results from the last discovery pass.</summary>
        public List<ModManifest> DiscoveredMods { get; private set; } = new();

        /// <summary>Validation warnings/errors from the last discovery pass.</summary>
        public List<string> ValidationLog { get; private set; } = new();

        public ModLoader()
        {
            ModsRoot = Path.Combine(Application.persistentDataPath, "Mods");
        }

        /// <summary>
        /// Build the complete ordered list of content sources.
        /// Base game is always first, followed by valid enabled mods.
        /// </summary>
        public List<IContentSource> BuildSourceList()
        {
            var sources = new List<IContentSource>();

            // Base game is always first
            sources.Add(new BuiltInContentSource());

            // Discover and add mod sources
            var mods = DiscoverMods();
            foreach (var manifest in mods)
            {
                if (!manifest.isEnabled)
                {
                    Debug.Log($"[ModLoader] Skipping disabled mod: {manifest.modId}");
                    continue;
                }

                if (!ValidateManifest(manifest))
                    continue;

                // For now, mod content sources are stubs.
                // Phase 1 will add actual mod content loading.
                sources.Add(new ModContentSource(manifest));
            }

            Debug.Log($"[ModLoader] Built source list: {sources.Count} sources " +
                $"(1 base + {sources.Count - 1} mods)");

            return sources;
        }

        /// <summary>
        /// Scan the Mods/ folder for mod manifests.
        /// Each subfolder should contain a manifest.json.
        /// </summary>
        public List<ModManifest> DiscoverMods()
        {
            DiscoveredMods.Clear();
            ValidationLog.Clear();

            if (!Directory.Exists(ModsRoot))
            {
                Debug.Log($"[ModLoader] Mods folder not found at {ModsRoot}, no mods to load");
                return DiscoveredMods;
            }

            var modFolders = Directory.GetDirectories(ModsRoot);
            foreach (var folder in modFolders)
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
                    var manifest = JsonUtility.FromJson<ModManifest>(json);
                    manifest.folderPath = folder;
                    manifest.isEnabled = true; // TODO: load from user preferences

                    if (string.IsNullOrEmpty(manifest.modId))
                    {
                        ValidationLog.Add($"Skipping {Path.GetFileName(folder)}: modId is empty");
                        continue;
                    }

                    DiscoveredMods.Add(manifest);
                    Debug.Log($"[ModLoader] Discovered mod: {manifest.modId} v{manifest.version} by {manifest.author}");
                }
                catch (Exception e)
                {
                    ValidationLog.Add($"Error reading {Path.GetFileName(folder)}/manifest.json: {e.Message}");
                    Debug.LogWarning($"[ModLoader] Failed to parse manifest in {folder}: {e.Message}");
                }
            }

            // Sort by load order, then alphabetically
            DiscoveredMods.Sort((a, b) =>
            {
                int orderCmp = a.loadOrder.CompareTo(b.loadOrder);
                return orderCmp != 0 ? orderCmp : string.Compare(a.modId, b.modId, StringComparison.Ordinal);
            });

            return DiscoveredMods;
        }

        /// <summary>
        /// Validate a mod manifest for compatibility.
        /// </summary>
        bool ValidateManifest(ModManifest manifest)
        {
            bool valid = true;

            if (string.IsNullOrEmpty(manifest.modId))
            {
                ValidationLog.Add($"Mod has empty modId");
                valid = false;
            }

            // Check for duplicate mod IDs
            int count = DiscoveredMods.Count(m => m.modId == manifest.modId);
            if (count > 1)
            {
                ValidationLog.Add($"Duplicate modId: {manifest.modId} (found {count} times)");
                valid = false;
            }

            // Check dependencies
            if (manifest.dependencies != null)
            {
                foreach (var dep in manifest.dependencies)
                {
                    if (dep == "pfe.base") continue; // always available
                    if (!DiscoveredMods.Any(m => m.modId == dep && m.isEnabled))
                    {
                        ValidationLog.Add($"{manifest.modId}: missing dependency '{dep}'");
                        Debug.LogWarning($"[ModLoader] {manifest.modId}: missing dependency '{dep}'");
                        valid = false;
                    }
                }
            }

            return valid;
        }
    }

    /// <summary>
    /// Content source for a user-installed mod.
    /// Loads ScriptableObjects from AssetBundles and/or JSON data files.
    ///
    /// Expected mod folder structure:
    ///   manifest.json              — mod identity and metadata
    ///   bundles/content.bundle     — AssetBundle containing ScriptableObjects
    ///   data/*.json                — JSON overrides (Phase 2, not yet implemented)
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

            // Pre-build override lookup from manifest (e.g. "Weapon/rifle" → override policy)
            if (manifest.overrides != null)
            {
                foreach (var key in manifest.overrides)
                    _overrideKeys.Add(key);
            }
        }

        public void RegisterContent(IContentRegistry registry)
        {
            int count = 0;

            // Try loading from AssetBundle
            count += LoadFromBundles(registry);

            // TODO Phase 2: Load JSON definitions from data/ folder
            // count += LoadFromJson(registry);

            Debug.Log($"[ModContentSource] {_manifest.modId}: registered {count} content entries");
        }

        /// <summary>
        /// Load ScriptableObjects from AssetBundles in the mod's bundles/ folder.
        /// All ScriptableObjects that implement IGameContent are registered.
        /// </summary>
        int LoadFromBundles(IContentRegistry registry)
        {
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
                            // Check if this content is declared as an override in manifest
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

        /// <summary>
        /// Unload the AssetBundle when this source is no longer needed.
        /// </summary>
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
