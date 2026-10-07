namespace PFE.Data
{
    /// <summary>
    /// Debug logging configuration contract for the GameDatabase content pipeline.
    /// Decouples GameDatabase from the View-layer PfeDebugSettings ScriptableObject.
    /// </summary>
    public interface IGameDatabaseDebugSettings
    {
        bool LogGameDatabaseInitializationSummary { get; }
        bool LogGameDatabaseAssetRegistration { get; }
        bool LogGameDatabaseDuplicateRegistrationWarnings { get; }
    }

    /// <summary>
    /// Default fallback settings when no PfeDebugSettings asset is provided.
    /// </summary>
    public sealed class DefaultGameDatabaseDebugSettings : IGameDatabaseDebugSettings
    {
        public static readonly DefaultGameDatabaseDebugSettings Instance = new();

        public bool LogGameDatabaseInitializationSummary => true;
        public bool LogGameDatabaseAssetRegistration => false;
        public bool LogGameDatabaseDuplicateRegistrationWarnings => false;
    }
}
