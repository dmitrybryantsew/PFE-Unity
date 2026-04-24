using System;
using UnityEngine;

namespace PFE.ModAPI
{
    /// <summary>
    /// Describes a mod (or the base game). Serialized from manifest.json in each mod folder.
    /// The base game itself is "pfe.base" — the first and always-loaded content source.
    /// </summary>
    [Serializable]
    public class ModManifest
    {
        /// <summary>Unique mod identifier. Format: "author.modname" (e.g. "pfe.base", "somepony.newweapons").</summary>
        public string modId;

        /// <summary>Human-readable name shown in mod manager.</summary>
        public string displayName;

        /// <summary>Semantic version string (e.g. "1.0.0").</summary>
        public string version;

        /// <summary>Mod author name.</summary>
        public string author;

        /// <summary>Short description of what the mod adds or changes.</summary>
        public string description;

        /// <summary>Minimum game version this mod is compatible with.</summary>
        public string targetGameVersion;

        /// <summary>
        /// Schema version for content data format. Incremented when breaking changes
        /// are made to definition structures. Mods with mismatched schema versions
        /// will be rejected at load time.
        /// </summary>
        public int contentSchemaVersion = 1;

        /// <summary>Mod IDs this mod depends on. These must be loaded first.</summary>
        public string[] dependencies = Array.Empty<string>();

        /// <summary>
        /// Load order priority. Lower values load first.
        /// Base game is always 0. Mods default to 100.
        /// When two mods have the same priority, alphabetical modId order is used.
        /// </summary>
        public int loadOrder = 100;

        /// <summary>
        /// If true, this mod only affects visuals/audio and does not change gameplay.
        /// Cosmetic mods don't need to match across multiplayer clients.
        /// </summary>
        public bool isCosmeticOnly;

        /// <summary>
        /// Content IDs this mod explicitly overrides from the base game or other mods.
        /// Format: "ContentType/ContentId" (e.g. "Weapon/rifle", "Unit/raider1").
        /// Listed entries use ConflictPolicy.Override instead of Additive.
        /// </summary>
        public string[] overrides = Array.Empty<string>();

        /// <summary>
        /// How this mod behaves in multiplayer.
        /// </summary>
        public MultiplayerPolicy multiplayerPolicy = MultiplayerPolicy.RequireMatch;

        /// <summary>Whether this mod is enabled. Persisted in user settings, not in manifest.</summary>
        [NonSerialized] public bool isEnabled = true;

        /// <summary>Absolute path to the mod folder on disk. Set at discovery time.</summary>
        [NonSerialized] public string folderPath;

        /// <summary>Creates the built-in base game manifest.</summary>
        public static ModManifest CreateBaseGame()
        {
            return new ModManifest
            {
                modId = "pfe.base",
                displayName = "PFE Base Game",
                version = Application.version,
                author = "PFE",
                description = "Base game content",
                targetGameVersion = Application.version,
                contentSchemaVersion = 1,
                dependencies = Array.Empty<string>(),
                loadOrder = 0,
                isCosmeticOnly = false,
                multiplayerPolicy = MultiplayerPolicy.RequireMatch,
                isEnabled = true,
            };
        }
    }

    /// <summary>
    /// How a mod behaves in multiplayer sessions.
    /// </summary>
    public enum MultiplayerPolicy
    {
        /// <summary>All players must have this mod with matching version/hash.</summary>
        RequireMatch,

        /// <summary>Only the host needs this mod (server-side content).</summary>
        HostOnly,

        /// <summary>Client-local only, no gameplay effect (cosmetic/UI).</summary>
        ClientLocal,
    }
}
