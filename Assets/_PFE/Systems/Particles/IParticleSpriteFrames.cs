namespace PFE.Systems.Particles
{
    /// <summary>
    /// How many frames a row's visual has — the one thing <see cref="ParticleRules.Cast"/> needs from
    /// the art, and the reason this interface exists at all.
    ///
    /// <para><b>Why it is an interface rather than a <c>Sprite[]</c> lookup.</b> The spawn rules need a
    /// frame count (the <c>anim=2</c> "start on a random frame" mode draws one, and a count of 0
    /// suppresses that draw entirely). If the rules asked for sprites they would have to name
    /// <c>UnityEngine.Sprite</c>, and then <see cref="ParticleWorld"/> could not be driven from a plain
    /// host — the whole of slice 1's offline verifiability would be lost at the first caller. An
    /// <c>int</c> in, <c>int</c> out keeps the world on the Unity-free side of the seam and lets the
    /// sprite-bearing implementation (<c>ParticleSpriteCatalog</c>, in
    /// <c>Systems/Particles/Rendering</c>) stay a thin adapter.</para>
    ///
    /// <para><b>0 is "unknown", not "one".</b> A row whose art failed to import must return 0 so the
    /// random-frame draw is suppressed; returning 1 would silently pin every frame to index 0 and the
    /// animation would look like a still image rather than like missing art.</para>
    /// </summary>
    public interface IParticleSpriteFrames
    {
        /// <summary>
        /// Frames available for <paramref name="definition"/>, or 0 when its art is absent or
        /// unresolved. Must never throw: a missing visual is a rendering problem, not a spawn failure.
        /// </summary>
        int FrameCount(ParticleDefinition definition);
    }

    /// <summary>
    /// A <see cref="IParticleSpriteFrames"/> for hosts with no art at all — returns 0 for everything, so
    /// the spawn rules take their "frame count unknown" path.
    ///
    /// <para>This exists so <see cref="ParticleWorld"/> has a non-null collaborator when the sprite
    /// catalog asset is missing (a fresh clone, or before the owner has run the art importer). The
    /// alternative — a null field with a null check at every use — is the shape that turns a missing
    /// asset into a <c>NullReferenceException</c> somewhere unrelated.</para>
    /// </summary>
    public sealed class NullParticleSpriteFrames : IParticleSpriteFrames
    {
        /// <summary>The shared instance. Stateless, so one is enough.</summary>
        public static NullParticleSpriteFrames Instance { get; } = new NullParticleSpriteFrames();

        private NullParticleSpriteFrames() { }

        /// <inheritdoc />
        public int FrameCount(ParticleDefinition definition) => 0;
    }
}
