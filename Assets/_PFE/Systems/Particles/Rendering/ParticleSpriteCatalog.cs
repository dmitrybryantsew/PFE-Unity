using System.Collections.Generic;
using PFE.Data.Definitions;
using UnityEngine;

namespace PFE.Systems.Particles.Rendering
{
    /// <summary>
    /// Resolves a <c>&lt;part&gt;</c> row to its frames — the adapter that turns the art asset into the
    /// <see cref="IParticleSpriteFrames"/> the spawn rules ask for, plus the sprite lookup the renderer
    /// uses per draw.
    ///
    /// <para><b>This is the only class that knows a row and a sprite are related.</b> The rules never see
    /// a <c>Sprite</c>; the asset never sees a <c>ParticleDefinition</c>. Everything in between is one
    /// dictionary hop: row → asset id (via <see cref="ParticleSpriteSource.AssetIdOf"/>) → entry → frames.</para>
    ///
    /// <para><b>Unresolved ids are collected, not thrown.</b> A row whose art is missing must render as
    /// nothing and be <i>findable</i> — the alternative, an exception, would take down the projectile that
    /// fired it, and a silent return would leave "the explosion did not appear" indistinguishable from
    /// "the explosion was never emitted". <see cref="MissingIds"/> is the readback, in first-seen order,
    /// mirroring <c>IParticleWorld.UnknownIds</c>.</para>
    /// </summary>
    public sealed class ParticleSpriteCatalog : IParticleSpriteFrames
    {
        private readonly ParticleSpriteCatalogAsset _asset;
        private readonly HashSet<string> _missing = new HashSet<string>();
        private readonly List<string> _missingOrder = new List<string>();

        /// <summary>
        /// Wraps the art asset. A null asset is legal and means "no art" — every row then reports 0
        /// frames and every draw resolves to nothing, which is what a fresh clone should look like
        /// before the importer has been run, rather than a crash.
        /// </summary>
        public ParticleSpriteCatalog(ParticleSpriteCatalogAsset asset) => _asset = asset;

        /// <summary>True when there is art to resolve against at all.</summary>
        public bool HasAsset => _asset != null;

        /// <summary>
        /// Asset ids that were asked for and had no frames, in first-seen order. Includes both ids
        /// absent from the asset and ids present with an empty frame array.
        /// </summary>
        public IReadOnlyList<string> MissingIds => _missingOrder;

        /// <inheritdoc />
        public int FrameCount(ParticleDefinition definition)
        {
            Sprite[] frames = FramesFor(definition, out _);
            return frames?.Length ?? 0;
        }

        /// <summary>
        /// The frames for a row, or null. <paramref name="assetId"/> comes back so a caller reporting a
        /// miss can name the asset rather than the row — with several rows sharing one sheet, the asset
        /// is the thing that is actually missing.
        /// </summary>
        public Sprite[] FramesFor(ParticleDefinition definition, out string assetId)
        {
            assetId = ParticleSpriteSource.AssetIdOf(definition);
            if (assetId.Length == 0) return null;

            Sprite[] frames = _asset?.FramesFor(assetId);
            if (frames == null || frames.Length == 0)
            {
                NoteMissing(assetId);
                return null;
            }

            return frames;
        }

        /// <summary>
        /// The sprite to draw for a row at a 0-based frame index, or null when the row has no art.
        ///
        /// <para><b>Out-of-range is clamped, not wrapped.</b> The frame index arrives from
        /// <c>ParticleState.DrawFrame</c>, which the rules derived from the frame count they were given at
        /// spawn; if the art is re-imported mid-session with fewer frames, the stale index would wrap and
        /// show a frame from the wrong end of the animation. Clamping shows the last frame, which reads as
        /// a held pose rather than as a glitch.</para>
        /// </summary>
        public Sprite FrameAt(ParticleDefinition definition, int frameIndex)
        {
            Sprite[] frames = FramesFor(definition, out _);
            if (frames == null || frames.Length == 0) return null;
            if (frameIndex < 0) return frames[0];
            return frames[frameIndex < frames.Length ? frameIndex : frames.Length - 1];
        }

        private void NoteMissing(string assetId)
        {
            if (_missing.Add(assetId)) _missingOrder.Add(assetId);
        }
    }
}
