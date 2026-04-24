namespace PFE.ModAPI
{
    /// <summary>
    /// Minimal interface for any content definition that can be registered in the content system.
    /// All game definitions (units, weapons, rooms, items, etc.) should implement this.
    /// </summary>
    public interface IGameContent
    {
        /// <summary>
        /// Unique content identifier. For base game content, this is a simple ID like "rifle".
        /// For mod content, this should be namespaced: "author.mod.rifle_mk2".
        /// The content registry will prefix with the source mod ID if not already namespaced.
        /// </summary>
        string ContentId { get; }

        /// <summary>
        /// What type of content this is (unit, weapon, room, etc.).
        /// Used for type-safe registry lookups and conflict detection.
        /// </summary>
        ContentType ContentType { get; }
    }
}
