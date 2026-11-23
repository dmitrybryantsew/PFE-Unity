namespace PFE.Systems.Map.Rendering
{
    /// <summary>
    /// The render-facing data assets a room needs, grouped into one injected object so no
    /// consumer carries a growing list of individual lookup parameters.
    ///
    /// <para><b>Why this exists (Wave 2, thread G).</b> <c>MapBridge</c> used to take
    /// <see cref="MaterialRenderDatabase"/> and <see cref="RoomBackgroundLookup"/> as separate
    /// constructor parameters purely to hand them to the visual layer — two parameters that said
    /// nothing about the bridge's real job (land-transition choreography) and everything about how
    /// the render lookups had leaked upward. The bundle is the seam: the container builds it once
    /// from the already-registered instances, consumers take one parameter, and the day a lookup
    /// retires (or joins) there is exactly one type to edit.</para>
    ///
    /// <para><b>What belongs here.</b> Only assets the DI container owns. <see cref="TileTextureLookup"/>
    /// stays scene-serialized on <c>RoomVisualController</c> for now (the backdrop reader), and
    /// <c>TileMaskLookup</c>/<c>TileAssetDatabase</c> left with the retired per-tile pipeline — the
    /// optional constructor slot keeps the door open without inventing runtime asset loading.</para>
    ///
    /// <para><b>Not a <c>ScriptableObject</c>.</b> A plain class registered as a VContainer singleton:
    /// zero scene or asset changes, and the grouped objects remain the individually-registered
    /// instances (one catalogue, not two — the same rule as <c>ContentRegistry</c> in
    /// <c>GameLifetimeScope</c>).</para>
    /// </summary>
    public sealed class RoomRenderAssetBundle
    {
        public MaterialRenderDatabase Materials { get; }
        public RoomBackgroundLookup Backgrounds { get; }

        /// <summary>Optional slots for the lookups that are not container-owned yet. Passing them
        /// keeps the bundle the single render-asset seam as the terrain/backdrop cutover finishes;
        /// they may be null and every consumer must tolerate that.</summary>
        public TileTextureLookup TileTextures { get; }
        public TileMaskLookup TileMasks { get; }

        public RoomRenderAssetBundle(
            MaterialRenderDatabase materials,
            RoomBackgroundLookup backgrounds,
            TileTextureLookup tileTextures = null,
            TileMaskLookup tileMasks = null)
        {
            Materials = materials;
            Backgrounds = backgrounds;
            TileTextures = tileTextures;
            TileMasks = tileMasks;
        }
    }
}
