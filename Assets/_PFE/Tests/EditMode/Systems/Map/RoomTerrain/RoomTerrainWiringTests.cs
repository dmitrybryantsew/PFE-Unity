using NUnit.Framework;
using UnityEngine;
using PFE.Systems.Map;
using PFE.Systems.Map.Rendering;
using PFE.Systems.Map.Rendering.RoomTerrain;

namespace PFE.Tests.EditMode.Systems.Map.RoomTerrain
{
    /// <summary>
    /// Pins the decision that puts the quarter renderer into a room: whether it can be built, and the
    /// numbers it is built with — for the main room and for the Z-door background layer.
    ///
    /// <para><b>Why the decision and not the picture.</b> <c>RoomTerrainGeometryTests</c> already pins the
    /// arithmetic that places a shape. What it cannot see is the failure mode this class exists for: the
    /// art asset missing and a room that draws <i>nothing at all</i> because the one terrain renderer
    /// never ran. That is a two-input decision with a reason, so it is a function, and these fixtures
    /// ask it each way.</para>
    ///
    /// <para><b>Two fixtures are deliberately not written.</b> There is no "the flag defaults to off"
    /// test — it would assert a field initialiser against itself, which is an identity and survives its
    /// own mutant (lesson #234). And there is no test of <see cref="RoomTerrainWiring.ShippedCellSize"/>
    /// against <c>WorldConstants.TILE_SIZE / 100f</c>, because that is its definition. What is worth
    /// pinning is the <i>cross-check</i> between two constants that were written down independently —
    /// <see cref="RoomTerrainGeometry.DefaultCellSize"/> and <see cref="WorldConstants.TILE_SIZE"/> — and
    /// that is <see cref="CellSize_MatchesTheShippedTileSize"/>.</para>
    /// </summary>
    public sealed class RoomTerrainWiringTests
    {
        // ─── The decision ────────────────────────────────────────

        [Test]
        public void TryResolve_BuildsOnlyWhenBothInputsArePresent()
        {
            RoomTerrainArt art = ScriptableObject.CreateInstance<RoomTerrainArt>();
            var materials = ScriptableObject.CreateInstance<MaterialRenderDatabase>();

            try
            {
                Assert.IsTrue(RoomTerrainWiring.TryResolve(art, materials, out string ok),
                    "art present, materials present");
                Assert.IsNull(ok, "a successful resolve must not carry a reason");

                Assert.IsFalse(RoomTerrainWiring.TryResolve(null, materials, out string noArt),
                    "art missing");
                StringAssert.Contains("art", noArt, "the reason must name the art asset");

                Assert.IsFalse(RoomTerrainWiring.TryResolve(art, null, out string noMaterials),
                    "materials missing");
                StringAssert.Contains("MaterialRenderDatabase", noMaterials,
                    "the reason must name what the scene is missing");
            }
            finally
            {
                Object.DestroyImmediate(art);
                Object.DestroyImmediate(materials);
            }
        }

        [Test]
        public void TryResolve_NamesTheArtAssetWhenItIsMissing()
        {
            // The case that matters in practice: the importer has never run. The reason must point at
            // the art, because that is the thing the reader has to go and do — a generic "cannot
            // build" sends them to the wrong file.
            Assert.IsFalse(RoomTerrainWiring.TryResolve(null, null, out string reason));
            StringAssert.Contains("art", reason);
        }

        // ─── The numbers it is built with ────────────────────────

        [Test]
        public void CellSize_MatchesTheShippedTileSize()
        {
            // Two independently written constants: RoomTerrainGeometry states 0.4 with a comment,
            // WorldConstants states TILE_SIZE = 40 px. The shipped renderer derives its own scale from
            // the latter (TileRenderer.ApplyScale), so if TILE_SIZE ever changes, the quarter renderer's
            // literal would draw the room at the wrong size next to units that use the other one. This
            // fixture is the thing that fails when that happens.
            Assert.AreEqual(WorldConstants.TILE_SIZE / 100f, RoomTerrainGeometry.DefaultCellSize, 1e-6f,
                "the quarter renderer's default cell must be the shipped tile size");

            Assert.AreEqual(RoomTerrainGeometry.DefaultCellSize, RoomTerrainWiring.ShippedCellSize, 1e-6f,
                "the wiring must hand the renderer the shipped cell size");

            // Guard against the fixture passing on a degenerate value (0 would satisfy any equality).
            Assert.Greater(RoomTerrainWiring.ShippedCellSize, 0.1f, "a cell is a fraction of a unit, not zero");
            Assert.Less(RoomTerrainWiring.ShippedCellSize, 1f, "a cell is smaller than a world unit here");
        }

        [Test]
        public void Options_DoNotApplyTheDarknessTwice()
        {
            // RoomTerrainRenderer.BackTint already carries the room's darkness
            // (RoomBackdropRenderer.ResolveBackgroundTileTint multiplies by the darkness term), and the
            // shader multiplies rgb by _Ambient on top. Feeding the darkness into both would make a dark
            // room quadratically dark — a difference that reads as "the new renderer is too dark" and
            // sends the reader looking for a missing light.
            RoomTerrainRenderer.Options options = RoomTerrainWiring.ResolveOptions(RoomTerrainWiring.ShippedCellSize);

            Assert.AreEqual(1f, options.ambient, 1e-6f, "ambient stays neutral; the darkness is in the tint");
        }

        [Test]
        public void Options_KeepTheFrontTintThatAS3Applies()
        {
            // Grafon.as:617 multiplies the whole front bitmap by the room's colour scheme. The shipped
            // path does not, so turning this off in game would make the two paths agree in colour by
            // making this one wrong — the opposite of what the flag is for.
            RoomTerrainRenderer.Options options = RoomTerrainWiring.ResolveOptions(RoomTerrainWiring.ShippedCellSize);

            Assert.IsTrue(options.applyFrontTint, "the front pass must keep AS3's colour scheme");
            Assert.IsTrue(options.applyBackTint, "the back pass must keep the scheme x darkness");
        }

        [Test]
        public void Options_TakeTheCellSizeTheyAreGiven()
        {
            // Guards the other direction: a resolver that ignored its argument and returned the default
            // would pass every fixture above, because they all pass the default in.
            Assert.AreEqual(0.8f, RoomTerrainWiring.ResolveOptions(0.8f).cellSize, 1e-6f);

            // A non-positive cell size is a caller bug, not a request for a zero-sized room: fall back to
            // the default rather than building a grid with no extent.
            Assert.AreEqual(RoomTerrainRenderer.Options.Default().cellSize,
                RoomTerrainWiring.ResolveOptions(0f).cellSize, 1e-6f);
        }

        [Test]
        public void BackgroundOptions_PackEverythingOntoTheBackgroundTilesLayer()
        {
            // The Z-door back layer must draw behind the main room. That is a single forced sorting
            // layer plus the -3000 offset the shipped background path used — anything else puts a
            // background wall in front of the main room's floor.
            RoomTerrainRenderer.Options main = RoomTerrainWiring.ResolveOptions(RoomTerrainWiring.ShippedCellSize);
            RoomTerrainRenderer.Options background =
                RoomTerrainWiring.ResolveBackgroundOptions(RoomTerrainWiring.ShippedCellSize);

            Assert.IsNull(main.sortingLayerName, "the main room keeps its natural per-pass layers");
            Assert.AreEqual(0, main.sortingOrderOffset, "the main room has no offset");

            Assert.AreEqual(MapSortingLayers.BackgroundTiles, background.sortingLayerName,
                "the back layer renders entirely on BackgroundTiles");
            Assert.AreEqual(RoomTerrainWiring.BackgroundTileSortingOffset, background.sortingOrderOffset,
                "the back layer keeps the shipped path's -3000 offset");
            Assert.Less(background.sortingOrderOffset + 9, 0,
                "even the back layer's flash must land behind the main room's back pass at order 0");

            // The picture-affecting flags must match the main room's, or the back layer would read
            // as a different room.
            Assert.AreEqual(main.applyBackTint, background.applyBackTint);
            Assert.AreEqual(main.applyFrontTint, background.applyFrontTint);
            Assert.AreEqual(main.drawOverlays, background.drawOverlays);
            Assert.AreEqual(main.cellSize, background.cellSize, 1e-6f);
        }
    }
}
