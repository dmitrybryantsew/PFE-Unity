namespace PFE.Systems.Map.Serialization
{
    /// <summary>
    /// Interface for migrating world save data from one version to the next.
    /// </summary>
    public interface ISaveMigrator
    {
        int SourceVersion { get; }
        int TargetVersion { get; }
        WorldSaveData Migrate(WorldSaveData data);
    }
}
