namespace PFE.ModAPI
{
    /// <summary>
    /// How to handle ID conflicts when multiple content sources provide
    /// the same ContentType + ContentId.
    /// </summary>
    public enum ConflictPolicy
    {
        /// <summary>
        /// New content with a duplicate ID is rejected. First registration wins.
        /// This is the safest default — prevents accidental overwrites.
        /// </summary>
        Additive,

        /// <summary>
        /// New content replaces existing content with the same ID.
        /// Used by mods that intentionally override base game definitions.
        /// </summary>
        Override,
    }
}
