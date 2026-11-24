namespace PFE.ModAPI
{
    /// <summary>
    /// Narrow, host-owned registration facade passed to <see cref="IModPlugin.Register"/>.
    /// Keeps mods decoupled from host DI containers and internal simulation/map services.
    /// </summary>
    public interface IModHostContext
    {
        /// <summary>
        /// Live read access to this mod's own persisted settings (declared via
        /// <see cref="IModSettingsProvider"/> and edited in the host's Mods tab).
        /// Capture it during Register and read values lazily when they are needed.
        /// </summary>
        IModSettings Settings { get; }

        void RegisterContentSource(IContentSource source);

        void RegisterProceduralLandProvider(IProceduralLandProvider provider);

        void RegisterConsoleCommands(string prefix, object commandHandler);
    }
}
