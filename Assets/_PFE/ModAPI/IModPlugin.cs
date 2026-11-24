namespace PFE.ModAPI
{
    /// <summary>
    /// Entry point implemented by code mods and referenced via <see cref="PfeModPluginAttribute"/>.
    /// Mods declare their manifest and register capabilities through <see cref="IModHostContext"/>
    /// without touching host internals or DI containers.
    /// </summary>
    public interface IModPlugin
    {
        ModManifest Manifest { get; }

        void Register(IModHostContext host);
    }
}
