using System;
using System.Collections.Generic;

namespace PFE.Data
{
    /// <summary>
    /// Records which mods were active when a save was created.
    /// Embedded in save files to detect missing/incompatible mods on load.
    /// </summary>
    [Serializable]
    public class ModSaveMetadata
    {
        /// <summary>Game version when save was created.</summary>
        public string gameVersion;

        /// <summary>Content schema version when save was created.</summary>
        public int contentSchemaVersion;

        /// <summary>Active mods at save time, in load order.</summary>
        public List<ModSaveEntry> activeMods = new();

        [Serializable]
        public class ModSaveEntry
        {
            public string modId;
            public string version;
            public bool isCosmeticOnly;
        }

        /// <summary>
        /// Build metadata from the current content registry state.
        /// </summary>
        public static ModSaveMetadata FromCurrentState(ContentRegistry registry)
        {
            var meta = new ModSaveMetadata
            {
                gameVersion = UnityEngine.Application.version,
                contentSchemaVersion = 1,
            };

            foreach (var source in registry.Sources)
            {
                meta.activeMods.Add(new ModSaveEntry
                {
                    modId = source.Manifest.modId,
                    version = source.Manifest.version,
                    isCosmeticOnly = source.Manifest.isCosmeticOnly,
                });
            }

            return meta;
        }

        /// <summary>
        /// Validate that the current mod state is compatible with this save.
        /// Returns list of warnings (empty = compatible).
        /// </summary>
        public List<string> ValidateCompatibility(ContentRegistry registry)
        {
            var warnings = new List<string>();

            var currentMods = new HashSet<string>();
            foreach (var source in registry.Sources)
                currentMods.Add(source.Manifest.modId);

            foreach (var savedMod in activeMods)
            {
                if (savedMod.isCosmeticOnly) continue; // cosmetic mods don't affect saves

                if (!currentMods.Contains(savedMod.modId))
                {
                    warnings.Add($"Missing mod: {savedMod.modId} v{savedMod.version} (was active when save was created)");
                }
            }

            return warnings;
        }
    }
}
