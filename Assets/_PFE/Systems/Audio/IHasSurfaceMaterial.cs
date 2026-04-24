namespace PFE.Systems.Audio
{
    /// <summary>
    /// Implemented by any GameObject that wants to declare its own impact surface material
    /// (e.g. a metal robot returns Metal, a slime enemy returns Slime).
    ///
    /// ImpactSoundResolver checks this interface first before falling back to IDamageable → Flesh.
    /// </summary>
    public interface IHasSurfaceMaterial
    {
        SurfaceMaterial SurfaceMaterial { get; }
    }
}
