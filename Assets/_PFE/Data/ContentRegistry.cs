using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using PFE.ModAPI;

namespace PFE.Data
{
    /// <summary>
    /// Central merge point for all game content from all sources (base game + mods).
    /// Implements IContentRegistry from ModAPI.
    ///
    /// Content sources register their definitions here during initialization.
    /// GameDatabase delegates to this registry for all lookups.
    ///
    /// ID namespacing:
    /// - Bare IDs (no dot) are auto-prefixed with the source modId: "rifle" → "pfe.base.rifle"
    /// - Already-qualified IDs (contain a dot) are kept as-is: "somepony.mod.rifle_mk2"
    /// - Lookups try exact match first, then "pfe.base.{bareId}" fallback for backward compat
    /// </summary>
    public class ContentRegistry : IContentRegistry
    {
        /// <summary>Tracks which mod provided each content entry, for diagnostics and conflict resolution.</summary>
        struct ContentEntry
        {
            public ScriptableObject Asset;
            public string SourceModId;
            /// <summary>The original bare ID before namespacing (null if already qualified).</summary>
            public string BareId;
        }

        /// <summary>ContentType → (qualifiedId → entry).</summary>
        readonly Dictionary<ContentType, Dictionary<string, ContentEntry>> _registries = new();

        /// <summary>ContentType → (bareId → qualifiedId) for backward-compat lookups.</summary>
        readonly Dictionary<ContentType, Dictionary<string, string>> _bareIdAliases = new();

        /// <summary>All loaded content sources in load order.</summary>
        readonly List<IContentSource> _sources = new();

        /// <summary>Log messages from registration for diagnostics.</summary>
        readonly List<string> _registrationLog = new();

        public bool LogRegistrations { get; set; }
        public bool LogConflicts { get; set; } = true;

        /// <summary>All loaded content sources.</summary>
        public IReadOnlyList<IContentSource> Sources => _sources;

        /// <summary>Registration log for diagnostics.</summary>
        public IReadOnlyList<string> RegistrationLog => _registrationLog;

        /// <summary>
        /// Add a content source and have it register all its content.
        /// Sources should be added in load order (base game first, then mods by priority).
        /// </summary>
        public void AddSource(IContentSource source)
        {
            _sources.Add(source);
            source.RegisterContent(this);
        }

        /// <summary>
        /// Initialize from an ordered list of content sources.
        /// Clears any previously registered content.
        /// </summary>
        public void Initialize(IEnumerable<IContentSource> sources)
        {
            Clear();
            foreach (var source in sources)
            {
                AddSource(source);
            }
        }

        /// <summary>Clear all registered content and sources.</summary>
        public void Clear()
        {
            _registries.Clear();
            _bareIdAliases.Clear();
            _sources.Clear();
            _registrationLog.Clear();
        }

        public void Register(ModManifest source, ScriptableObject content, ConflictPolicy policy = ConflictPolicy.Additive)
        {
            if (content == null)
            {
                Debug.LogWarning($"[ContentRegistry] Null content from {source.modId}, skipping");
                return;
            }

            if (content is not IGameContent gameContent)
            {
                Debug.LogWarning($"[ContentRegistry] {content.name} from {source.modId} does not implement IGameContent, skipping");
                return;
            }

            string rawId = gameContent.ContentId;
            if (string.IsNullOrEmpty(rawId))
            {
                Debug.LogWarning($"[ContentRegistry] Empty ContentId on {content.name} from {source.modId}, skipping");
                return;
            }

            // Auto-prefix bare IDs with modId
            string bareId = null;
            string qualifiedId;
            if (rawId.Contains('.'))
            {
                // Already namespaced (e.g. "somepony.mod.rifle_mk2")
                qualifiedId = rawId;
            }
            else
            {
                // Bare ID (e.g. "rifle") → prefix with modId
                bareId = rawId;
                qualifiedId = $"{source.modId}.{rawId}";
            }

            var type = gameContent.ContentType;
            if (!_registries.TryGetValue(type, out var registry))
            {
                registry = new Dictionary<string, ContentEntry>();
                _registries[type] = registry;
            }
            if (!_bareIdAliases.TryGetValue(type, out var aliases))
            {
                aliases = new Dictionary<string, string>();
                _bareIdAliases[type] = aliases;
            }

            // Check for exact qualified ID collision
            if (registry.TryGetValue(qualifiedId, out var existing))
            {
                if (policy == ConflictPolicy.Override)
                {
                    registry[qualifiedId] = new ContentEntry { Asset = content, SourceModId = source.modId, BareId = bareId };
                    var msg = $"[ContentRegistry] {type}/{qualifiedId}: overridden by {source.modId} (was from {existing.SourceModId})";
                    _registrationLog.Add(msg);
                    if (LogConflicts) Debug.Log(msg);
                }
                else
                {
                    var msg = $"[ContentRegistry] {type}/{qualifiedId}: duplicate from {source.modId}, kept {existing.SourceModId}";
                    _registrationLog.Add(msg);
                    if (LogConflicts) Debug.LogWarning(msg);
                }
                return;
            }

            // Store under the mod's own qualified ID (every mod keeps its own copy)
            registry[qualifiedId] = new ContentEntry { Asset = content, SourceModId = source.modId, BareId = bareId };

            // Handle bare ID aliases.
            // The alias determines what GetUnit("raider1") returns — i.e., which mod "wins" the bare name.
            if (bareId != null)
            {
                if (!aliases.ContainsKey(bareId))
                {
                    // First registration wins (base game registers first)
                    aliases[bareId] = qualifiedId;
                }
                else if (policy == ConflictPolicy.Override)
                {
                    // Override: mod takes over the bare ID alias.
                    // The previous entry stays in the registry under its own qualified ID,
                    // but bare-name lookups now resolve to the mod's version.
                    string previousQualified = aliases[bareId];
                    aliases[bareId] = qualifiedId;
                    var msg = $"[ContentRegistry] {type}/{bareId}: alias redirected from {previousQualified} to {qualifiedId}";
                    _registrationLog.Add(msg);
                    if (LogConflicts) Debug.Log(msg);
                }
            }

            if (LogRegistrations)
            {
                var msg = $"[ContentRegistry] {type}/{qualifiedId} registered from {source.modId}";
                _registrationLog.Add(msg);
                Debug.Log(msg);
            }
        }

        public T Get<T>(ContentType type, string contentId) where T : ScriptableObject, IGameContent
        {
            if (string.IsNullOrEmpty(contentId)) return null;

            if (_registries.TryGetValue(type, out var registry))
            {
                // Try exact match (works for qualified IDs like "pfe.base.rifle")
                if (registry.TryGetValue(contentId, out var entry))
                    return entry.Asset as T;

                // Try bare ID alias (works for legacy lookups like "rifle" → "pfe.base.rifle")
                if (_bareIdAliases.TryGetValue(type, out var aliases) &&
                    aliases.TryGetValue(contentId, out var qualifiedId) &&
                    registry.TryGetValue(qualifiedId, out entry))
                {
                    return entry.Asset as T;
                }
            }
            return null;
        }

        public IEnumerable<T> GetAll<T>(ContentType type) where T : ScriptableObject, IGameContent
        {
            if (!_registries.TryGetValue(type, out var registry))
                return Enumerable.Empty<T>();

            return registry.Values
                .Select(e => e.Asset as T)
                .Where(a => a != null);
        }

        public IEnumerable<string> GetAllIds(ContentType type)
        {
            if (_registries.TryGetValue(type, out var registry))
                return registry.Keys;
            return Enumerable.Empty<string>();
        }

        /// <summary>
        /// Get all bare (unqualified) IDs for a given type.
        /// Useful for backward-compatible code that doesn't know about namespacing.
        /// Returns the bare ID where available, otherwise the full qualified ID.
        /// </summary>
        public IEnumerable<string> GetAllBareIds(ContentType type)
        {
            if (!_registries.TryGetValue(type, out var registry))
                return Enumerable.Empty<string>();

            return registry.Select(kvp => kvp.Value.BareId ?? kvp.Key);
        }

        public bool Contains(ContentType type, string contentId)
        {
            if (!_registries.TryGetValue(type, out var registry))
                return false;

            if (registry.ContainsKey(contentId))
                return true;

            // Try bare ID alias
            return _bareIdAliases.TryGetValue(type, out var aliases) &&
                   aliases.TryGetValue(contentId, out var qualifiedId) &&
                   registry.ContainsKey(qualifiedId);
        }

        /// <summary>Get which mod provided a specific content entry.</summary>
        public string GetSourceMod(ContentType type, string contentId)
        {
            if (_registries.TryGetValue(type, out var registry))
            {
                if (registry.TryGetValue(contentId, out var entry))
                    return entry.SourceModId;

                // Try bare ID alias
                if (_bareIdAliases.TryGetValue(type, out var aliases) &&
                    aliases.TryGetValue(contentId, out var qualifiedId) &&
                    registry.TryGetValue(qualifiedId, out entry))
                {
                    return entry.SourceModId;
                }
            }
            return null;
        }

        /// <summary>
        /// Resolve a content ID to its fully qualified form.
        /// Returns the input if already qualified or not found.
        /// </summary>
        public string ResolveId(ContentType type, string contentId)
        {
            if (string.IsNullOrEmpty(contentId)) return contentId;

            // Already qualified
            if (contentId.Contains('.')) return contentId;

            // Try bare alias
            if (_bareIdAliases.TryGetValue(type, out var aliases) &&
                aliases.TryGetValue(contentId, out var qualifiedId))
            {
                return qualifiedId;
            }

            return contentId;
        }

        /// <summary>Get total count of registered content for a type.</summary>
        public int GetCount(ContentType type)
        {
            return _registries.TryGetValue(type, out var registry) ? registry.Count : 0;
        }

        /// <summary>Get a summary of all registered content for logging.</summary>
        public string GetSummary()
        {
            var parts = new List<string>();
            foreach (var kvp in _registries.OrderBy(k => k.Key))
            {
                parts.Add($"{kvp.Key}: {kvp.Value.Count}");
            }
            return $"[ContentRegistry] {_sources.Count} sources, content: {string.Join(", ", parts)}";
        }
    }
}
