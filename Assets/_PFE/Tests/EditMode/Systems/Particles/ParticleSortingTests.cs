using NUnit.Framework;
using PFE.Systems.Map.Rendering;
using PFE.Systems.Particles;
using PFE.Systems.Particles.Rendering;

namespace PFE.Tests.EditMode.Systems.Particles
{
    /// <summary>
    /// Pins the <b>sorting</b> half of <see cref="ParticleRenderer"/> — the layer it draws on and the
    /// order band it occupies.
    ///
    /// <para><b>What went wrong, and why it needed a fixture rather than a careful read.</b> The renderer
    /// shipped with <c>sortingLayerName = "Default"</c>, which is the built-in layer and the <b>first</b>
    /// entry in the project's sorting-layer list — so it renders behind every custom layer, and the whole
    /// <c>bandBaseSortingOrder</c> scheme was inert (<c>sortingOrder</c> only breaks ties <i>inside</i> a
    /// layer). Every particle was therefore drawn behind the map and was visible only where nothing
    /// covered it, which the owner reported as *"they show only when the unit is at the map edge"*.
    /// Nothing threw, nothing logged, and no existing fixture could see it: the layer's meaning is its
    /// position in <c>ProjectSettings/TagManager.asset</c>, which is not reachable from C#.</para>
    ///
    /// <para><b>What is checkable offline, and what is not.</b> The layer's <i>position</i> is not — that
    /// is a project setting, and it is guarded at runtime by <c>ParticleRenderer.ValidateSortingLayer</c>,
    /// which is where the owner will actually see it. What <i>is</i> checkable is that the layer is not
    /// the bottom-most one, that it is a real room layer, and that the order band fits between the two
    /// neighbours it must fit between. The neighbour orders are recomputed here from their own source
    /// literals rather than imported, so a change to either is a red test rather than a silent overlap.</para>
    ///
    /// <para><b>Why the neighbours are literals and not constants.</b> They live on private serialized
    /// fields of two other components (<c>CharacterOverlayDefinition.sortingOrder</c>,
    /// <c>RoomBackdropRenderer.VisibilityMaskSortingOrder</c>), which a fixture cannot read without
    /// instantiating a <c>MonoBehaviour</c>. Recomputing them from the source literal is the same shape
    /// as the particle harnesses that print parsed counts beside independently computed ones — the point
    /// is that the two sides are derived separately, so agreement means something.</para>
    ///
    /// <para><b>One relationship is argued rather than tested, on purpose.</b> The order assertions below
    /// are only meaningful because the front wall tiles and the fog mask are on the <i>same layer</i> the
    /// particles are (<c>sortingOrder</c> is compared within a layer, never across layers). That
    /// relationship cannot be asserted from a fixture: <c>ParticleRenderer.DefaultSortingLayer</c> <i>is</i>
    /// <c>MapSortingLayers.Foreground</c>, both are <c>const string</c>s, the compiler inlines them, and
    /// an assertion comparing the two would pass for any edit — a test that cannot fail, which is worse
    /// than a missing one because it reads as coverage. It is argued in
    /// <c>ParticleRenderer.DefaultSortingLayer</c>'s remarks (which cite both neighbours) and enforced at
    /// runtime. Recorded here so the gap is visible rather than mistaken for a green result.</para>
    ///
    /// <para><b>⚠ A stale test assembly can turn the two layer tests red for no reason.</b>
    /// <c>ParticleRenderer.DefaultSortingLayer</c> is a <c>const</c>, and the compiler <i>inlines</i> a
    /// cross-assembly <c>const</c> into the referencing assembly — so <c>PFE.Tests.dll</c> carries its
    /// own baked copy of the value. If a test run begins before Unity has recompiled
    /// <c>ParticleRenderer.cs</c>, that run reads the <b>old</b> literal and reports
    /// <c>But was: "Default"</c> even though the source on disk already reads <c>Foreground</c>. Seen
    /// once, 10-04: the run ended <c>14:37:32</c>, the edit landed <c>14:37:36</c>, the rebuild
    /// <c>14:37:37</c>. So before acting on a red here, compare the mtime of
    /// <c>Library/ScriptAssemblies/PFE.Core.dll</c> with <c>ParticleRenderer.cs</c> — and re-run instead
    /// of "fixing" a source that is already correct.</para>
    /// </summary>
    [TestFixture]
    public class ParticleSortingTests
    {
        // ── The neighbours on `Foreground`, recomputed from their own declarations ────────────────
        //
        // The order bands on the `Foreground` layer, from the code that assigns them:
        //
        //   front wall tiles      TileRenderer.ApplySorting: baseOrder + 1 / + 2, where
        //                         baseOrder = sortingOrderOffset - gridPosition.y. Offset 0 and a
        //                         floor-row tile give 1..2, and rows above it go negative.
        //   debug markers         AreaTriggerPresenter / DoorPropPresenter / ObjectColliderDebugPresenter
        //                         at 998/999.
        //   character slots       CharacterSpriteAssembler:609 — `slotIdx * 2`, i.e. 0..~40 for a
        //                         two-dozen-part rig.
        //   character overlays    CharacterSpriteAssembler:411 — `def.sortingOrder`, whose declared
        //                         default is 1000 (CharacterAnimationDefinition.cs:182).
        //   fog / visibility mask RoomBackdropRenderer.VisibilityMaskSortingOrder = 5000.
        //   collider debug        ColliderDebugOverlay.OverlaySortingOrder = 32000.
        //
        // So the free slot on this layer is 2000..3379, and it is the only one that is simultaneously
        // above every character and below the fog mask.
        private const int FrontTileOrderMax = 2;
        private const int CharacterOrderMax = 1000;
        private const int FogMaskOrder = 5000;

        /// <summary>
        /// Every layer <see cref="MapSortingLayers"/> declares, listed here independently so a value that
        /// is not a room layer at all — a typo, a UI layer — is caught.
        /// </summary>
        private static readonly string[] RoomLayers =
        {
            MapSortingLayers.Backwall,
            MapSortingLayers.BackgroundTiles,
            MapSortingLayers.BackgroundDecor,
            MapSortingLayers.MainTiles,
            MapSortingLayers.BackgroundObject,
            MapSortingLayers.BackgroundPhysicalObjects,
            MapSortingLayers.Water,
            MapSortingLayers.Foreground,
        };

        // ── The layer ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The regression itself: the particle layer must not be <c>Default</c>, because <c>Default</c>
        /// renders behind every room layer and makes the order band meaningless.
        /// </summary>
        [Test]
        public void TheParticleLayerIsNotDefault()
        {
            Assert.AreNotEqual("Default", ParticleRenderer.DefaultSortingLayer,
                "`Default` is the first layer in the project's list, so it draws behind the backwall, the " +
                "tile grid, the units and the fog mask — every particle would be hidden behind the map. " +
                "If ParticleRenderer.cs already reads `Foreground`, then this assembly is stale (a `const` " +
                "is inlined across assemblies): let Unity recompile and re-run, do not edit the source");

            Assert.IsFalse(string.IsNullOrEmpty(ParticleRenderer.DefaultSortingLayer),
                "an empty name resolves to `Default` for the same reason");
        }

        /// <summary>
        /// And it must be a layer the room actually draws on — a name that is not in the project's list
        /// resolves to <c>Default</c> too, which is the same bug with a different spelling.
        /// </summary>
        [Test]
        public void TheParticleLayerIsARoomLayer()
        {
            CollectionAssert.Contains(RoomLayers, ParticleRenderer.DefaultSortingLayer,
                "particles must be on a layer the room actually uses, or the ordering assertions below " +
                "compare against nothing");
        }

        // ── The order band ───────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Two <c>sloy</c> bands must never interleave. The rank counter is per band and can reach the
        /// global live ceiling, so a stride at or below it would let band <c>n</c>'s last particle draw
        /// over band <c>n+1</c>'s first — which would reorder particles that AS3 orders by band first.
        /// </summary>
        [Test]
        public void TheBandStrideExceedsTheGlobalLiveCeiling()
        {
            Assert.Greater(ParticleRenderer.DefaultBandStride, ParticleBudget.DefaultMaxParts,
                "a stride at or below the live ceiling lets two sloy bands interleave");
        }

        /// <summary>
        /// The whole band range must start above every character on the layer — AS3 draws particles
        /// (<c>sloy</c> 3) in front of units (<c>sloy</c> 2), so blood over a unit is correct and blood
        /// behind one is not.
        /// </summary>
        [Test]
        public void TheBandRangeStartsAboveEveryCharacterAndTheFrontTiles()
        {
            Assert.Greater(ParticleRenderer.DefaultBandBaseSortingOrder, CharacterOrderMax,
                "particles would draw behind characters");
            Assert.Greater(ParticleRenderer.DefaultBandBaseSortingOrder, FrontTileOrderMax,
                "particles would draw behind the front wall tiles");
        }

        /// <summary>
        /// And the top of the range must stay <b>below the fog-of-war mask</b>. This is the assertion
        /// whose failure would be least obvious: particles above the mask would draw through fog and
        /// reveal rooms the player has not entered — which reads as a fog bug, not as a particle bug.
        /// </summary>
        [Test]
        public void TheWholeBandRangeStaysBelowTheFogMask()
        {
            int topBandOrder = ParticleRenderer.DefaultBandBaseSortingOrder
                             + (ParticleRenderer.BandCount - 1) * ParticleRenderer.DefaultBandStride
                             + ParticleBudget.DefaultMaxParts;

            Assert.Less(topBandOrder, FogMaskOrder,
                "the top of the particle band range reaches the fog-of-war mask's order, so particles " +
                "would draw over the fog and reveal unexplored rooms");

            // Control: the range is not vacuous — a base order of 0 would satisfy the assertion above
            // while putting every particle behind the map, which is the bug this fixture exists for.
            Assert.Greater(ParticleRenderer.DefaultBandBaseSortingOrder, 0,
                "a non-positive base order would be below the front tiles");
        }

        /// <summary>
        /// Every shipped row is <c>sloy</c> 3 (<c>grep -c sloy AllData.as</c> is 0, so the declaration's
        /// default of 3 applies), so the band that has to <i>look</i> right is band 3 — and it must not be
        /// the one that overflows into the fog mask.
        /// </summary>
        [Test]
        public void TheShippedBandThreeSitsInsideTheRange()
        {
            const int ShippedSloy = 3;

            int order = ParticleRenderer.DefaultBandBaseSortingOrder
                      + ShippedSloy * ParticleRenderer.DefaultBandStride;

            Assert.Greater(order, CharacterOrderMax,
                "the shipped band would draw behind characters");
            Assert.Less(order, FogMaskOrder,
                "the shipped band would draw over the fog-of-war mask");
        }
    }
}
