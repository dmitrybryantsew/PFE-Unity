using NUnit.Framework;
using PFE.Systems.Map.Rendering;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.Editor.Map.Rendering
{
    /// <summary>
    /// Pins the backdrop rebuild's sweep — the half that removes a previous instance's GameObjects — and
    /// the rule that decides what may be swept.
    ///
    /// <para><b>The bug this exists to make unrepeatable.</b> A rebuild used to destroy only the objects it
    /// was <i>tracking</i>, and an instance does not survive a domain reload. The editor preview
    /// (<c>RoomVisualController.RefreshBackdropVisuals</c>, reached from <c>OnValidate</c>) rebuilds the
    /// backdrop with a fresh instance on every inspector change, so its destroy step was a no-op against
    /// whatever the previous session had left behind — including objects Unity had already saved into
    /// <c>SampleScene</c>. One more <c>Backdrop__0</c> per session, and three of them were sitting in the
    /// scene when this was written. The sweep now walks the parent instead, because the parent is the only
    /// record that outlives the instance.</para>
    ///
    /// <para><b>The negative half is the load-bearing one.</b> <c>BackgroundTiles</c> is a sibling under the
    /// same parent and belongs to <c>RoomVisualController</c>; a sweep that matched it would destroy the
    /// background tile layer. That is why the rule is matched by name rather than by clearing the parent,
    /// and why <see cref="RoomBackdropRenderer.IsOwnedBackdropChild"/> is <c>public</c> — a pure string rule
    /// can be pinned here, without an engine, where a behavioural test of the sweep cannot run at all.</para>
    /// </summary>
    [TestFixture]
    public sealed class RoomBackdropStaleChildTests
    {
        /// <summary>The renderer, for the one assertion that the sweep is actually called.</summary>
        private const string RendererPath = "Systems/Map/Rendering/RoomBackdropRenderer.cs";

        /// <summary>
        /// Everything the renderer creates under the background parent must be sweepable, or a rebuild
        /// leaves that object behind — which is the whole defect, one object class at a time.
        /// </summary>
        [Test]
        public void TheSweepMatchesEveryObjectTheRendererCreates()
        {
            // The two per-room families, named at their creation sites:
            // `Backdrop_{wall}_{i}` (the room backdrop segments) and `Background_{id}_{x}_{y}` (decorations).
            // The empty wall is the common case — 43 rooms author `backwall=""` — so the double underscore
            // is a real name, not a typo to normalise away.
            Assert.IsTrue(RoomBackdropRenderer.IsOwnedBackdropChild("Backdrop__0"),
                "`Backdrop__0` is the name CreateRoomBackdrop gives its first segment when the room authors " +
                "no backwall. If the sweep stops matching it, exactly the object the owner reported " +
                "accumulating is the one that is left behind.");

            Assert.IsTrue(RoomBackdropRenderer.IsOwnedBackdropChild("Backdrop_wood_3"),
                "A named wall's segment must be swept too — matching only the empty-wall name would leave " +
                "every other room's backdrop behind.");

            Assert.IsTrue(RoomBackdropRenderer.IsOwnedBackdropChild("Background_lgreen_3_4"),
                "Background decorations (`Background_{id}_{x}_{y}`) accumulate the same way.");

            // The singletons, matched by exact name.
            Assert.IsTrue(RoomBackdropRenderer.IsOwnedBackdropChild("VisibilityMask"),
                "The visibility-mask overlay is created under the same parent.");
            Assert.IsTrue(RoomBackdropRenderer.IsOwnedBackdropChild("BackgroundTileShadow"),
                "The baked tile-shadow overlay is created under the same parent.");
            Assert.IsTrue(RoomBackdropRenderer.IsOwnedBackdropChild("WaterOverlay"),
                "The water overlay is created under the same parent.");
        }

        /// <summary>
        /// The negative control. <c>BackgroundTiles</c> shares the parent and is <b>not</b> the renderer's,
        /// so a sweep that claims it takes the background tile layer with it — a worse bug than the one
        /// being fixed, and a silent one, because the room would still draw.
        /// </summary>
        [Test]
        public void TheSweepNeverClaimsASiblingItDoesNotOwn()
        {
            Assert.IsFalse(RoomBackdropRenderer.IsOwnedBackdropChild("BackgroundTiles"),
                "`BackgroundTiles` is a sibling under the same parent, created and owned by " +
                "RoomVisualController.EnsureBackgroundTileParent. Sweeping it destroys the background tile " +
                "layer — the `Background_` prefix must stay underscored so it cannot match this name.");

            Assert.IsFalse(RoomBackdropRenderer.IsOwnedBackdropChild("BackgroundObjects"),
                "The object parents hang off the controller's transform, not the backdrop parent; claiming " +
                "them would let a backdrop rebuild destroy placed props.");

            Assert.IsFalse(RoomBackdropRenderer.IsOwnedBackdropChild("BackgroundPhysicalObjects"),
                "Same as BackgroundObjects — units and physical props live here.");

            Assert.IsFalse(RoomBackdropRenderer.IsOwnedBackdropChild("Tiles"),
                "The main tile layer is a different parent entirely and must never be claimed.");

            Assert.IsFalse(RoomBackdropRenderer.IsOwnedBackdropChild(""),
                "An empty name must not match: Unity's default name is empty on a not-yet-named object, and " +
                "a prefix test against \"\" would have to be special-cased rather than accidentally true.");

            Assert.IsFalse(RoomBackdropRenderer.IsOwnedBackdropChild(null),
                "Null must be answered, not thrown on — the sweep passes Transform.name through.");
        }

        /// <summary>
        /// The predicate above is only worth pinning if something calls it. This is the other half: a
        /// rebuild must sweep before it creates, or the orphans are joined rather than replaced.
        /// </summary>
        [Test]
        public void EveryRebuildSweepsBeforeItCreates()
        {
            string source = SourceLint.ReadStripped(RendererPath);
            string createVisuals = SourceLint.MethodBody(source, "public void CreateVisuals()");

            Assert.That(createVisuals, Does.Contain("DestroyStaleBackdropChildren"),
                "RoomBackdropRenderer.CreateVisuals no longer sweeps the parent. `DestroyVisuals` only " +
                "removes the objects this instance is tracking, and a fresh instance (a domain reload, or " +
                "the editor preview's rebuild) tracks none — so without the sweep the previous session's " +
                "`Backdrop__0` stays where it is and a second one is added beside it, which is the " +
                "accumulation this fixture was written for.");
        }
    }
}
