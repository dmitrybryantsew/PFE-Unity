using System.Collections.Generic;
using UnityEngine;

namespace PFE.ModAPI
{
    /// <summary>
    /// A source of game content — either the base game or a mod.
    /// Each content source has a manifest and can provide ScriptableObject definitions
    /// to the content registry.
    ///
    /// The base game is itself a content source ("pfe.base"), so all content flows
    /// through the same pipeline regardless of origin.
    /// </summary>
    public interface IContentSource
    {
        /// <summary>The manifest describing this content source.</summary>
        ModManifest Manifest { get; }

        /// <summary>
        /// Push all content from this source into the registry.
        /// Called once during initialization, in load-order sequence.
        /// </summary>
        void RegisterContent(IContentRegistry registry);
    }

    /// <summary>
    /// Registry that content sources push definitions into.
    /// Handles merging, conflict detection, and type-safe lookup.
    /// </summary>
    public interface IContentRegistry
    {
        /// <summary>
        /// Register a content definition from a specific source.
        /// The registry handles conflict detection based on ContentType + ContentId.
        /// </summary>
        /// <param name="source">The mod/source providing this content.</param>
        /// <param name="content">The ScriptableObject that implements IGameContent.</param>
        /// <param name="policy">How to handle conflicts if this ID already exists.</param>
        void Register(ModManifest source, ScriptableObject content, ConflictPolicy policy = ConflictPolicy.Additive);

        /// <summary>
        /// Look up a content definition by type and ID.
        /// Returns null if not found.
        /// </summary>
        T Get<T>(ContentType type, string contentId) where T : ScriptableObject, IGameContent;

        /// <summary>
        /// Get all content of a specific type.
        /// </summary>
        IEnumerable<T> GetAll<T>(ContentType type) where T : ScriptableObject, IGameContent;

        /// <summary>
        /// Get all registered content IDs for a given type.
        /// </summary>
        IEnumerable<string> GetAllIds(ContentType type);

        /// <summary>
        /// Check if a content ID exists for a given type.
        /// </summary>
        bool Contains(ContentType type, string contentId);
    }
}
