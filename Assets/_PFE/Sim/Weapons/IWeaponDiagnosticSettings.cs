namespace PFE.Systems.Weapons
{
    /// <summary>
    /// Decouples weapon controllers from concrete PfeDebugSettings ScriptableObject.
    /// </summary>
    public interface IWeaponDiagnosticSettings
    {
        bool LogWeaponControllerDiagnostics { get; }
    }
}
