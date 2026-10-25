using System;
using NUnit.Framework;
using UnityEngine;
using PFE.Systems.Map;
using PFE.Systems.Map.Rendering;
using PFE.Systems.Map.Rendering.RoomTerrain;

namespace PFE.Tests.EditMode.Systems.Map.RoomTerrain
{
    /// <summary>
    /// Pins the quarter renderer's two pieces of arithmetic: the edge-state rule and the placement
    /// transform.
    ///
    /// <para><b>Why these and not a rendering test.</b> Both are pure functions, so they can be pinned
    /// without an editor, a scene, an imported sprite or a Tilemap — and they are where a wrong answer
    /// still produces a picture. A mask placed with the flip on the wrong quarter, or a state rule with
    /// its two neighbour arguments swapped, renders something plausible. The pixels would have to be
    /// eyeballed; these numbers do not.</para>
    ///
    /// <para><b><see cref="States_AgreeWithKonturCalculator"/> is the important one.</b> It runs the
    /// port's own <see cref="KonturCalculator"/> and this renderer's rule over the same synthetic room
    /// and requires them to agree quarter for quarter. Two independent implementations of AS3
    /// <c>insKontur</c> agreeing is evidence; one implementation agreeing with itself is not.</para>
    /// </summary>
    public sealed class RoomTerrainGeometryTests
    {
        // ─── The state rule ──────────────────────────────────────

        [Test]
        public void State_FollowsInsKontur()
        {
            // (yNeighbourFilled, xNeighbourFilled, diagonalFilled) -> expected
            Assert.AreEqual(RoomQuarterRules.Inside, RoomQuarterRules.State(true, true, true), "both + diagonal");
            Assert.AreEqual(RoomQuarterRules.InnerCorner, RoomQuarterRules.State(true, true, false), "both, no diagonal");
            Assert.AreEqual(RoomQuarterRules.HorizontalEdge, RoomQuarterRules.State(false, true, true), "x only");
            Assert.AreEqual(RoomQuarterRules.HorizontalEdge, RoomQuarterRules.State(false, true, false), "x only, no diagonal");
            Assert.AreEqual(RoomQuarterRules.VerticalEdge, RoomQuarterRules.State(true, false, true), "y only");
            Assert.AreEqual(RoomQuarterRules.VerticalEdge, RoomQuarterRules.State(true, false, false), "y only, no diagonal");
            Assert.AreEqual(RoomQuarterRules.OuterCorner, RoomQuarterRules.State(false, false, true), "neither");
            Assert.AreEqual(RoomQuarterRules.OuterCorner, RoomQuarterRules.State(false, false, false), "neither, no diagonal");
        }

        [Test]
        public void State_IsNotSymmetricInItsTwoNeighbours()
        {
            // The trap this pins: "vertical edge" (3) and "horizontal edge" (2) are different states
            // and swapping the arguments swaps them. A renderer built on the swapped order turns every
            // wall edge 90 degrees and still looks like a wall.
            Assert.AreNotEqual(
                RoomQuarterRules.State(true, false, false),
                RoomQuarterRules.State(false, true, false),
                "a y-only neighbour and an x-only neighbour must not produce the same state");
        }

        // ─── Grid computation ────────────────────────────────────

        [Test]
        public void Compute_IsolatedCell_IsAnOuterCornerOnEveryQuarter()
        {
            RoomQuarterStates states = Compute(3, 3, solid: (x, y) => x == 1 && y == 1);

            foreach (RoomQuarter quarter in RoomQuarterRules.All())
            {
                Assert.AreEqual(RoomQuarterRules.OuterCorner, states.Front(1, 1, quarter),
                    $"{quarter} of an isolated cell should be an outer corner");
            }

            Assert.IsFalse(states.HasFront(0, 0), "a cell that is not solid draws no front quarters");
        }

        [Test]
        public void Compute_SolidBlockInterior_IsInsideOnEveryQuarter()
        {
            RoomQuarterStates states = Compute(5, 5, solid: (x, y) => x >= 1 && x <= 3 && y >= 1 && y <= 3);

            foreach (RoomQuarter quarter in RoomQuarterRules.All())
            {
                Assert.AreEqual(RoomQuarterRules.Inside, states.Front(2, 2, quarter),
                    $"{quarter} of a fully surrounded cell should be inside");
            }
        }

        [Test]
        public void Compute_BlockEdges_ProduceTheMatchingEdgeState()
        {
            // A 3x3 block at x,y in 1..3 inside a 5x5 room.
            RoomQuarterStates states = Compute(5, 5, solid: (x, y) => x >= 1 && x <= 3 && y >= 1 && y <= 3);

            // Left edge of the block: open to the left, so both left quarters are vertical edges.
            Assert.AreEqual(RoomQuarterRules.VerticalEdge, states.Front(1, 2, RoomQuarter.TopLeft), "left edge, upper");
            Assert.AreEqual(RoomQuarterRules.VerticalEdge, states.Front(1, 2, RoomQuarter.BottomLeft), "left edge, lower");
            Assert.AreEqual(RoomQuarterRules.Inside, states.Front(1, 2, RoomQuarter.TopRight), "inside face");

            // Bottom edge of the block: open below, so both bottom quarters are horizontal edges.
            Assert.AreEqual(RoomQuarterRules.HorizontalEdge, states.Front(2, 1, RoomQuarter.BottomLeft), "bottom edge, left");
            Assert.AreEqual(RoomQuarterRules.HorizontalEdge, states.Front(2, 1, RoomQuarter.BottomRight), "bottom edge, right");
            Assert.AreEqual(RoomQuarterRules.Inside, states.Front(2, 1, RoomQuarter.TopLeft), "inside face");
        }

        [Test]
        public void Compute_OutOfBounds_CountsAsFilled()
        {
            // One cell in the room's bottom-left corner. Its lower-left quarter looks entirely outside
            // the room, and outside counts as filled — so that quarter is "inside", while the upper-left
            // one sees the empty cell above and becomes a horizontal edge.
            RoomQuarterStates states = Compute(3, 3, solid: (x, y) => x == 0 && y == 0);

            Assert.AreEqual(RoomQuarterRules.Inside, states.Front(0, 0, RoomQuarter.BottomLeft),
                "outside the room is filled, so the bottom-left quarter has both neighbours");
            Assert.AreEqual(RoomQuarterRules.HorizontalEdge, states.Front(0, 0, RoomQuarter.TopLeft),
                "the cell above is inside the room and empty");
        }

        [Test]
        public void Compute_BackLayer_SplitsWhereTheBackgroundChanges()
        {
            // Two background surfaces side by side: ids 1 for x < 2, id 2 for x >= 2.
            RoomQuarterStates states = RoomQuarterStates.Compute(
                4, 3,
                isSolid: (x, y) => false,
                backId: (x, y) => x < 2 ? 1 : 2,
                isShelf: (x, y) => false,
                skyBackdrop: false);

            Assert.AreEqual(RoomQuarterRules.VerticalEdge, states.Back(1, 1, RoomQuarter.TopRight),
                "the right quarters of the left surface face a different background");
            Assert.AreEqual(RoomQuarterRules.Inside, states.Back(1, 1, RoomQuarter.TopLeft),
                "the left quarters continue into the same background");
            Assert.AreEqual(RoomQuarterRules.VerticalEdge, states.Back(2, 1, RoomQuarter.TopLeft),
                "and the split is symmetric");

            // A background cell is drawn on every quarter it has — NotDrawn would mean the whole cell
            // silently vanished, which is what an unset id looks like.
            Assert.AreNotEqual(RoomQuarterRules.NotDrawn, states.Back(1, 1, RoomQuarter.BottomLeft),
                "back quarters are drawn wherever a background exists");
        }

        [Test]
        public void Compute_EmptyRoom_DrawsNothing()
        {
            RoomQuarterStates states = Compute(4, 4, solid: (x, y) => false);
            Assert.AreEqual(0, states.CountFrontCells());
        }

        [Test]
        public void Compute_ZeroSizedRoom_IsLegal()
        {
            RoomQuarterStates states = RoomQuarterStates.Compute(
                0, 0, (x, y) => false, (x, y) => 0, (x, y) => false, false);

            Assert.AreEqual(0, states.Width);
            Assert.AreEqual(RoomQuarterRules.NotDrawn, states.Front(0, 0, RoomQuarter.TopLeft));
        }

        [Test]
        public void Compute_RejectsNullPredicates()
        {
            Assert.Throws<ArgumentNullException>(() =>
                RoomQuarterStates.Compute(2, 2, null, (x, y) => 0, (x, y) => false, false));

            Assert.Throws<ArgumentNullException>(() =>
                RoomQuarterStates.Compute(2, 2, (x, y) => false, null, (x, y) => false, false));
        }

        // ─── The room colour scheme ──────────────────────────────

        /// <summary>
        /// The front layer is multiplied by the room's colour scheme, and by nothing else.
        ///
        /// <para><b>This is the one place the renderer deliberately disagrees with the shipped path.</b>
        /// <c>Grafon.as:617</c> colour-transforms <c>frontBmp</c> by
        /// <c>colorFilter(loc.color)</c>; <c>RoomVisualController</c> passes <c>Color.white</c> as the
        /// front manager's primary tint, so the shipped picture has no scheme on its walls. The
        /// assertion below therefore pins <i>AS3</i>, and the "differs from the back" assertion is the
        /// point: if someone later "fixes" this by reusing
        /// <see cref="RoomTerrainRenderer.ResolveBackTint"/>'s answer, the front would go dark with the
        /// backdrop.</para>
        /// </summary>
        [Test]
        public void FrontTint_IsTheRoomColourSchemeWithoutDarkness()
        {
            RoomInstance room = BuildRoom(2, 2);
            room.environment.colorScheme = "black";
            room.environment.darkness = 0;

            Color front = RoomTerrainRenderer.ResolveFrontTint(room, apply: true);

            // Location.colorFilter("black") — the scheme table the backdrop renderer already carries.
            Assert.That(front.r, Is.EqualTo(0.5f).Within(1e-5f), "red");
            Assert.That(front.g, Is.EqualTo(0.6f).Within(1e-5f), "green");
            Assert.That(front.b, Is.EqualTo(0.7f).Within(1e-5f), "blue");
            Assert.That(front.a, Is.EqualTo(1f).Within(1e-5f), "alpha is not the scheme's business");

            Color back = RoomBackdropRenderer.ResolveBackgroundTileTint(room.environment);
            Assert.That(back.r, Is.Not.EqualTo(front.r).Within(1e-5f),
                "the back adds the darkness term; the front must not. Equal values mean one of the " +
                "two transforms is being reused for the other pass.");
        }

        [Test]
        public void FrontTint_IsWhiteWhenTheOptionIsOff()
        {
            // The comparison switch: with the tint off the A/B isolates shape and projection, which is
            // the only way to compare against a shipped picture that draws its walls untinted.
            RoomInstance room = BuildRoom(2, 2);
            room.environment.colorScheme = "black";

            Assert.AreEqual(Color.white, RoomTerrainRenderer.ResolveFrontTint(room, apply: false));
        }

        [Test]
        public void FrontTint_IsWhiteForARoomWithNoScheme()
        {
            // 241 of the 640 shipped room assets declare no colour, and `colorFilter("")` returns an
            // identity transform in AS3 — so white here is the oracle's answer, not a fallback.
            RoomInstance room = BuildRoom(2, 2);
            room.environment.colorScheme = "";

            Color tint = RoomTerrainRenderer.ResolveFrontTint(room, apply: true);

            Assert.That(tint.r, Is.EqualTo(1f).Within(1e-5f));
            Assert.That(tint.g, Is.EqualTo(1f).Within(1e-5f));
            Assert.That(tint.b, Is.EqualTo(1f).Within(1e-5f));
        }

        [Test]
        public void FrontTint_ToleratesAMissingEnvironment()
        {
            RoomInstance room = BuildRoom(2, 2);
            room.environment = null;

            Assert.AreEqual(Color.white, RoomTerrainRenderer.ResolveFrontTint(room, apply: true));
            Assert.AreEqual(Color.white, RoomTerrainRenderer.ResolveFrontTint(null, apply: true));
        }

        // ─── Agreement with the shipped port ─────────────────────

        [Test]
        public void States_AgreeWithKonturCalculator()
        {
            // A room with a blob of walls, a hole in it, and two background surfaces. Deliberately
            // avoids door ids, shelves and platforms: KonturCalculator treats a door as a wall and a
            // shelf via isLedge only, so including either would test a documented difference rather
            // than the rule this renderer is meant to reproduce.
            RoomInstance room = BuildRoom(7, 6);
            for (int x = 1; x <= 5; x++)
            {
                for (int y = 1; y <= 4; y++)
                {
                    bool hole = x == 3 && y == 2;
                    if (hole) continue;
                    room.tiles[x, y].physicsType = TilePhysicsType.Wall;
                }
            }

            for (int x = 0; x < 7; x++)
            {
                for (int y = 0; y < 6; y++)
                {
                    if (room.tiles[x, y].physicsType == TilePhysicsType.Wall) continue;
                    room.tiles[x, y].SetBackGraphic(x < 4 ? "tConcrete" : "tMetal");
                }
            }

            KonturCalculator.CalculateAll(room);

            RoomQuarterStates states = RoomQuarterStates.Compute(
                7, 6,
                isSolid: (x, y) => room.tiles[x, y].physicsType == TilePhysicsType.Wall,
                backId: (x, y) =>
                {
                    string graphic = room.tiles[x, y].GetBackGraphic();
                    if (string.IsNullOrEmpty(graphic)) return 0;
                    return graphic == "tConcrete" ? 1 : 2;
                },
                isShelf: (x, y) => false,
                skyBackdrop: false);

            int compared = 0;

            for (int x = 0; x < 7; x++)
            {
                for (int y = 0; y < 6; y++)
                {
                    TileData tile = room.tiles[x, y];

                    if (tile.physicsType == TilePhysicsType.Wall)
                    {
                        Assert.AreEqual(tile.kontur1, states.Front(x, y, RoomQuarter.TopLeft), $"kontur1 at ({x},{y})");
                        Assert.AreEqual(tile.kontur2, states.Front(x, y, RoomQuarter.TopRight), $"kontur2 at ({x},{y})");
                        Assert.AreEqual(tile.kontur3, states.Front(x, y, RoomQuarter.BottomLeft), $"kontur3 at ({x},{y})");
                        Assert.AreEqual(tile.kontur4, states.Front(x, y, RoomQuarter.BottomRight), $"kontur4 at ({x},{y})");
                        compared += 4;
                    }

                    if (!string.IsNullOrEmpty(tile.GetBackGraphic()))
                    {
                        Assert.AreEqual(tile.pontur1, states.Back(x, y, RoomQuarter.TopLeft), $"pontur1 at ({x},{y})");
                        Assert.AreEqual(tile.pontur2, states.Back(x, y, RoomQuarter.TopRight), $"pontur2 at ({x},{y})");
                        Assert.AreEqual(tile.pontur3, states.Back(x, y, RoomQuarter.BottomLeft), $"pontur3 at ({x},{y})");
                        Assert.AreEqual(tile.pontur4, states.Back(x, y, RoomQuarter.BottomRight), $"pontur4 at ({x},{y})");
                        compared += 4;
                    }
                }
            }

            // A guard against the test passing because it compared nothing: this room must produce
            // plenty of comparisons, and it must include at least one non-zero state, or it is only
            // proving that two functions both return 0.
            Assert.Greater(compared, 40, "the fixture should compare many quarters");

            bool sawEdge = false;
            for (int x = 0; x < 7 && !sawEdge; x++)
            {
                for (int y = 0; y < 6 && !sawEdge; y++)
                {
                    if (room.tiles[x, y].physicsType != TilePhysicsType.Wall) continue;
                    sawEdge = room.tiles[x, y].kontur1 != RoomQuarterRules.Inside
                              || room.tiles[x, y].kontur2 != RoomQuarterRules.Inside
                              || room.tiles[x, y].kontur3 != RoomQuarterRules.Inside
                              || room.tiles[x, y].kontur4 != RoomQuarterRules.Inside;
                }
            }

            Assert.IsTrue(sawEdge, "the fixture must contain an edge, or the comparison is vacuous");
        }

        // ─── Placement ───────────────────────────────────────────

        [Test]
        public void SpriteScale_ReproducesTheRemainsExport()
        {
            // Remains exported at 50 px/m x zoom 8 = 400 ppu with a 0.8 m cell; this project's cell is
            // 0.4 units, so the same art needs half the scale.
            Assert.AreEqual(0.5f, RoomTerrainGeometry.SpriteScale(400f, 0.4f, 40, 8d), 0.0001f);
        }

        [Test]
        public void SpriteScale_MakesAQuarterSpriteExactlyOneQuarter()
        {
            const float pixelsPerUnit = 400f;
            const float cellSize = 0.4f;
            const int cellPixels = 40;
            const double zoom = 8d;

            float scale = RoomTerrainGeometry.SpriteScale(pixelsPerUnit, cellSize, cellPixels, zoom);

            // A 160 px-wide quarter image at 400 ppu is 0.4 units before scaling.
            const float spriteWidthPixels = 160f;
            float scaledWidth = spriteWidthPixels / pixelsPerUnit * scale;

            Assert.AreEqual(cellSize * 0.5f, scaledWidth, 0.0001f,
                "one quarter of the source cell must land on one quarter of a world cell");
        }

        [Test]
        public void SpriteScale_IsInverselyProportionalToTheSourceCellSize()
        {
            // The scale is a ratio between "raster pixels per cell" and "world units per cell", so
            // halving the declared cell pixels must double it. This is what would break if the
            // derivation used the sprite's own size instead of the manifest's cellPixels.
            float coarse = RoomTerrainGeometry.SpriteScale(400f, 0.4f, 20, 8d);
            float fine = RoomTerrainGeometry.SpriteScale(400f, 0.4f, 40, 8d);

            Assert.AreEqual(fine * 2f, coarse, 0.0001f);
        }

        [Test]
        public void SpriteScale_IsSafeOnDegenerateInput()
        {
            Assert.AreEqual(1f, RoomTerrainGeometry.SpriteScale(0f, 0.4f, 40, 8d));
            Assert.AreEqual(1f, RoomTerrainGeometry.SpriteScale(400f, 0f, 40, 8d));
            Assert.AreEqual(1f, RoomTerrainGeometry.SpriteScale(400f, 0.4f, 0, 8d));
            Assert.AreEqual(1f, RoomTerrainGeometry.SpriteScale(400f, 0.4f, 40, 0d));
        }

        [Test]
        public void MaskPlacement_FitsTheSlotAfterSpriteScale()
        {
            // The four fixtures above all ask SpriteScale about itself, so all four stayed green while
            // the renderer never called it and every mask drew at twice its slot. This one asks the
            // *composition*: a placed mask's world span has to be the slot's span.
            const float pixelsPerUnit = 400f;
            const float cellSize = 0.4f;
            const int cellPixels = 40;
            const double zoom = 8d;
            const float quarterPixels = 160f;   // exported quarter: 20 source px x zoom 8
            const float wholePixels = 320f;     // TileMask: 40 source px x zoom 8

            float scale = RoomTerrainGeometry.SpriteScale(pixelsPerUnit, cellSize, cellPixels, zoom);

            RoomTerrainPlacement quarter = RoomTerrainGeometry.Scaled(
                RoomTerrainGeometry.Quarter(RoomQuarter.BottomLeft, 3, 5, cellSize, 1f, 0f, false, false),
                scale);

            RoomTerrainPlacement whole = RoomTerrainGeometry.Scaled(
                RoomTerrainGeometry.Whole(3, 5, cellSize, 1f, 0f),
                scale);

            Assert.AreEqual(cellSize * 0.5f, quarterPixels / pixelsPerUnit * Mathf.Abs(quarter.Scale.x), 0.0001f,
                "a corner mask must span half a cell, not a whole one");
            Assert.AreEqual(cellSize, wholePixels / pixelsPerUnit * Mathf.Abs(whole.Scale.x), 0.0001f,
                "TileMask must span one cell; at two it repeats the material texture twice and the beam reads as tripled");
        }

        [Test]
        public void BeamMask_ShowsExactlyOneMaterialPeriod()
        {
            // The owner's report was "duplication of ledges above and below the actual one". A beam is
            // not placed three times: it is placed once, and the material inside its mask is sampled by
            // *world position* (PFE_RoomTerrain.shader), so the number of girders on screen is
            //
            //     placed mask span ÷ material period
            //
            // and nothing else. This fixture pins that quotient, because a span of "one cell" is not
            // the same claim as "one girder" — it is only one girder while the period is one cell.
            const float cellSize = 0.4f;
            const int cellPixels = 40;
            const double zoom = 8d;
            const float maskPixels = 320f;     // TileMask: a 40-source-px cell exported at zoom 8
            const float maskPpu = 400f;        // the export's 50 px/m × zoom
            const float girderPixels = 40f;    // tShelf is 240×40 — one cell tall
            float texelsPerMeter = cellPixels / cellSize;   // RoomTerrainRenderer.MaterialFor

            float period = RoomTerrainGeometry.MaterialPeriod(girderPixels, texelsPerMeter);
            Assert.AreEqual(cellSize, period, 0.0001f,
                "tShelf must repeat once per cell, or the whole beam is drawn at the wrong scale");

            // Before the fit factor: scale 1, so a 320 px @ 400 ppu mask covers two cells and the
            // 0.4-unit period repeats twice — the girder appears above and below the beam's own row.
            float rawSpan = RoomTerrainGeometry.PlacedSpan(maskPixels, maskPpu, 1f);
            Assert.AreEqual(2f, RoomTerrainGeometry.MaterialPeriods(rawSpan, period), 0.0001f,
                "this is the reported duplication: an unscaled TileMask covers 2 cells, i.e. 2 girders");

            // After it: the same mask covers exactly its own cell.
            float scale = RoomTerrainGeometry.SpriteScale(maskPpu, cellSize, cellPixels, zoom);
            RoomTerrainPlacement placed = RoomTerrainGeometry.Scaled(
                RoomTerrainGeometry.Whole(15, 12, cellSize, 1f, 0f), scale);
            float span = RoomTerrainGeometry.PlacedSpan(maskPixels, maskPpu, placed.Scale.y);

            Assert.AreEqual(cellSize, span, 0.0001f, "a beam mask must cover its own cell and nothing else");
            Assert.AreEqual(1f, RoomTerrainGeometry.MaterialPeriods(span, period), 0.0001f,
                "so the material shows exactly one girder — no copy above it, none below");
        }

        [Test]
        public void MaterialPeriods_IsSafeOnDegenerateInput()
        {
            Assert.AreEqual(0f, RoomTerrainGeometry.PlacedSpan(0f, 400f, 1f));
            Assert.AreEqual(0f, RoomTerrainGeometry.PlacedSpan(320f, 0f, 1f));
            Assert.AreEqual(0f, RoomTerrainGeometry.MaterialPeriod(0f, 100f));
            Assert.AreEqual(0f, RoomTerrainGeometry.MaterialPeriod(40f, 0f));
            Assert.AreEqual(0f, RoomTerrainGeometry.MaterialPeriods(0.4f, 0f));
        }

        [Test]
        public void OverlaySort_AboveTheWallsMeansTheWallLayer()
        {
            // The owner reported that both overlay toggles "change nothing". They did: every ladder and
            // tread form is a rear form (`rear='1'`), so the ON branch used to compute
            // RearOrder + OverlayAboveOrder = -488 and leave the sprite on BackgroundDecor. -488 is a
            // big number inside a layer that sorts *below* MainTiles, where the walls live — so the
            // toggle moved the order and not the pixel. The invariant is a pair, never an order alone.
            RoomTerrainGeometry.OverlaySort(true, true, out string layer, out int order);
            Assert.AreEqual(MapSortingLayers.MainTiles, layer,
                "a rear overlay must be lifted onto the wall layer, or 'above' is a bigger number under the wall");
            Assert.Greater(order, RoomTerrainRenderer.MainOrder,
                "and its order must beat the front walls it shares that layer with");
            Assert.Greater(order, RoomTerrainRenderer.FloorOrder, "and the floor strip");

            // A front overlay takes the same lift, so both kinds agree when the toggle is on.
            RoomTerrainGeometry.OverlaySort(false, true, out string frontLayer, out int frontOrder);
            Assert.AreEqual(layer, frontLayer);
            Assert.AreEqual(order, frontOrder);

            // Off is AS3's bitmap order: rear under the walls, front over the floor but under them too.
            RoomTerrainGeometry.OverlaySort(true, false, out string offLayer, out int offOrder);
            Assert.AreEqual(MapSortingLayers.BackgroundDecor, offLayer);
            Assert.Less(offOrder, RoomTerrainRenderer.MainOrder);

            RoomTerrainGeometry.OverlaySort(false, false, out string offFrontLayer, out int offFrontOrder);
            Assert.AreEqual(MapSortingLayers.MainTiles, offFrontLayer);
            Assert.Less(offFrontOrder, RoomTerrainRenderer.MainOrder);
        }

        [Test]
        public void Scaled_KeepsTheMirrorAndTheSquash()
        {
            // The multiplier is positive, so it must ride on top of the flip sign and the height squash
            // rather than replace them. A mirrored, squashed quarter scaled by 0.5 is (-0.5, -0.375).
            RoomTerrainPlacement placed = RoomTerrainGeometry.Quarter(
                RoomQuarter.TopRight, 0, 0, 0.4f, RoomTerrainGeometry.HeightScale(1), 0f, true, true);

            RoomTerrainPlacement scaled = RoomTerrainGeometry.Scaled(placed, 0.5f);

            Assert.AreEqual(-0.5f, scaled.Scale.x, 0.0001f, "flipX must survive the fit factor");
            Assert.AreEqual(-0.375f, scaled.Scale.y, 0.0001f, "height squash must survive the fit factor");
            Assert.AreEqual(placed.Cell, scaled.Cell);
            Assert.AreEqual(placed.Translation, scaled.Translation);
        }

        [Test]
        public void Scaled_IsAnIdentityAtOne()
        {
            RoomTerrainPlacement placed = RoomTerrainGeometry.Whole(1, 2, 0.4f, 1f, 0f);
            RoomTerrainPlacement scaled = RoomTerrainGeometry.Scaled(placed, 1f);

            Assert.AreEqual(placed.Scale.x, scaled.Scale.x, 0.0001f);
            Assert.AreEqual(placed.Scale.y, scaled.Scale.y, 0.0001f);
        }

        [Test]
        public void HeightScale_And_Drop_MatchTheShippedRenderer()
        {
            Assert.AreEqual(1f, RoomTerrainGeometry.HeightScale(0), 0.0001f);
            Assert.AreEqual(0.75f, RoomTerrainGeometry.HeightScale(1), 0.0001f);
            Assert.AreEqual(0.5f, RoomTerrainGeometry.HeightScale(2), 0.0001f);
            Assert.AreEqual(0.25f, RoomTerrainGeometry.HeightScale(3), 0.0001f);
            Assert.AreEqual(0.25f, RoomTerrainGeometry.HeightScale(9), 0.0001f, "out of range clamps");

            Assert.AreEqual(0f, RoomTerrainGeometry.HeightDrop(0, 0.4f), 0.0001f);
            Assert.AreEqual(0.1f, RoomTerrainGeometry.HeightDrop(1, 0.4f), 0.0001f);
            Assert.AreEqual(0.2f, RoomTerrainGeometry.HeightDrop(2, 0.4f), 0.0001f);
            Assert.AreEqual(0.3f, RoomTerrainGeometry.HeightDrop(3, 0.4f), 0.0001f);
        }

        [Test]
        public void Quarter_Placement_LandsEachQuarterOnItsCellCorner()
        {
            const float cell = 0.4f;

            // Cell (3,5): its top-right quarter occupies half-cell slot (7,11) and its registration
            // point is the cell's top-right corner in room space.
            RoomTerrainPlacement topRight = RoomTerrainGeometry.Quarter(
                RoomQuarter.TopRight, 3, 5, cell, 1f, 0f, false, false);

            Assert.AreEqual(7, topRight.Cell.x);
            Assert.AreEqual(11, topRight.Cell.y);
            Assert.AreEqual(0.2f, topRight.Translation.x, 0.0001f);
            Assert.AreEqual(0.2f, topRight.Translation.y, 0.0001f);
            Assert.AreEqual(1f, topRight.Scale.x, 0.0001f);
            Assert.AreEqual(1f, topRight.Scale.y, 0.0001f);

            // The bottom-left quarter is the cell's own corner: slot (6,10), no offset at all.
            RoomTerrainPlacement bottomLeft = RoomTerrainGeometry.Quarter(
                RoomQuarter.BottomLeft, 3, 5, cell, 1f, 0f, false, false);

            Assert.AreEqual(6, bottomLeft.Cell.x);
            Assert.AreEqual(10, bottomLeft.Cell.y);
            Assert.AreEqual(0f, bottomLeft.Translation.x, 0.0001f);
            Assert.AreEqual(0f, bottomLeft.Translation.y, 0.0001f);
        }

        [Test]
        public void Quarter_Placement_SquashesAndDropsAFloorStrip()
        {
            // A top quarter's slot starts half a cell above the cell's own bottom edge, so its
            // translation is (cell * scale) - drop - cell/2. Stated as numbers rather than as the
            // formula, so a change to the formula has to disagree with this test.
            const float cell = 0.4f;

            // Floor strip, ceiling lowered by one quarter: scale 1, drop 0.1.
            RoomTerrainPlacement floor = RoomTerrainGeometry.Quarter(
                RoomQuarter.TopLeft, 2, 4, cell, 1f, 0.1f, false, false);

            Assert.AreEqual(1f, floor.Scale.y, 0.0001f, "a floor strip is not squashed");
            Assert.AreEqual(0.1f, floor.Translation.y, 0.0001f,
                "0.4 * 1 - 0.1 - 0.2: the strip moves down with the ceiling but is not squashed");

            // Wall face, ceiling lowered by two quarters: scale 0.5, no drop.
            RoomTerrainPlacement ceiling = RoomTerrainGeometry.Quarter(
                RoomQuarter.TopLeft, 2, 4, cell, 0.5f, 0f, false, false);

            Assert.AreEqual(0.5f, ceiling.Scale.y, 0.0001f, "the shape is squashed onto the lowered top");
            Assert.AreEqual(0f, ceiling.Translation.y, 0.0001f,
                "0.4 * 0.5 - 0.2: at scale 0.5 the lowered top edge is exactly the quarter's own origin");
        }

        [Test]
        public void Quarter_Placement_CarriesTheMirrorInTheSign()
        {
            RoomTerrainPlacement flipped = RoomTerrainGeometry.Quarter(
                RoomQuarter.BottomRight, 1, 1, 0.4f, 0.5f, 0f, true, true);

            Assert.AreEqual(-1f, flipped.Scale.x, 0.0001f);
            Assert.AreEqual(-0.5f, flipped.Scale.y, 0.0001f, "the flip multiplies the squashed scale");
        }

        [Test]
        public void Whole_Placement_RegistersAtTheCellCentre()
        {
            RoomTerrainPlacement whole = RoomTerrainGeometry.Whole(2, 3, 0.4f, 1f, 0f);

            Assert.AreEqual(4, whole.Cell.x);
            Assert.AreEqual(6, whole.Cell.y);
            Assert.AreEqual(0.2f, whole.Translation.x, 0.0001f);
            Assert.AreEqual(0.2f, whole.Translation.y, 0.0001f);
            Assert.AreEqual(1f, whole.Scale.x, 0.0001f);
        }

        /// <summary>
        /// AS3 pins the overlay symbol's <b>registration point</b> — not the PNG's top-left — to the
        /// cell's top-left corner (<c>Grafon.as:479-480</c>: <c>tile.x = i * tileX; tile.y = j *
        /// tileY</c>). The registration sits (5, 30) px inside the 50×110 canvas, so the sprite's
        /// centre is not on the cell's centre.
        /// </summary>
        [Test]
        public void Overlay_PinsTheRegistrationPointToTheCellCorner()
        {
            // A tread frame: 50×110 px at 100 px per unit = 0.5 × 1.1 world units, on a 0.4 cell.
            // Origin (5, 30) px at 0.01 units per px = (0.05, 0.30).
            Vector3 centre = RoomTerrainGeometry.Overlay(2, 3, 0.4f, 0.5f, 1.1f, 0.05f, 0.30f);

            // Recover the registration point from the centre: it is the canvas top-left plus the
            // origin, and the canvas top-left is the centre minus half the sprite.
            float registrationX = centre.x - 0.25f + 0.05f;
            float registrationY = centre.y + 0.55f - 0.30f;

            Assert.AreEqual(0.8f, registrationX, 0.0001f, "x * cellSize — the cell's left edge");
            Assert.AreEqual(1.6f, registrationY, 0.0001f, "(y + 1) * cellSize — the cell's TOP edge, because AS3's y runs down");
        }

        /// <summary>
        /// The control for the fixture above: dropping the origin (pinning the canvas corner instead)
        /// and centring the canvas on the cell are both wrong, by different amounts and in opposite
        /// directions. A regression to either one cannot pass this.
        /// </summary>
        [Test]
        public void Overlay_OriginIsNotZeroAndTheCanvasIsNotCentred()
        {
            const float cell = 0.4f;
            Vector3 placed = RoomTerrainGeometry.Overlay(2, 3, cell, 0.5f, 1.1f, 0.05f, 0.30f);
            Vector3 noOrigin = RoomTerrainGeometry.Overlay(2, 3, cell, 0.5f, 1.1f, 0f, 0f);
            Vector3 centred = new Vector3((2 + 0.5f) * cell, (3 + 0.5f) * cell, 0f);

            Assert.AreEqual(-0.05f, placed.x - noOrigin.x, 0.0001f, "5 px left: the origin's x");
            Assert.AreEqual(0.30f, placed.y - noOrigin.y, 0.0001f, "30 px up: the origin's y, mirrored");

            // The correct centre is 5 px *below* the cell centre, not 5 px above and not 30 px off.
            Assert.AreEqual(0f, placed.x - centred.x, 0.0001f);
            Assert.AreEqual(-0.05f, placed.y - centred.y, 0.0001f);
        }

        [Test]
        public void Overlay_ScalesWithTheCellSize()
        {
            Vector3 atHalf = RoomTerrainGeometry.Overlay(2, 3, 0.2f, 0.25f, 0.55f, 0.025f, 0.15f);
            Vector3 atFull = RoomTerrainGeometry.Overlay(2, 3, 0.4f, 0.5f, 1.1f, 0.05f, 0.30f);

            Assert.AreEqual(atHalf.x * 2f, atFull.x, 0.0001f);
            Assert.AreEqual(atHalf.y * 2f, atFull.y, 0.0001f);
        }

        /// <summary>
        /// An overlay's size, measured <b>in cells</b>, must not depend on the cell size.
        ///
        /// <para><b>This is the fixture for the <c>rect</c>-vs-<c>bounds</c> mistake.</b> The renderer
        /// used to take the sprite's world size from <c>Sprite.bounds.size</c>, which the importer has
        /// already divided by <c>spritePixelsToUnits</c> — 100 for the <c>tileFront</c> frames — and so
        /// is a <i>fixed</i> 0.5 × 1.1 no matter what the cell is. That equals the right answer only
        /// while <c>cellSize / cellPixels</c> happens to be 1/100, which at the shipped 0.4 over 40 px
        /// it is. Expressing the claim in cells is what makes the coincidence visible: the frames are
        /// 50 × 110 px on a 40 px grid, so they are <b>1.25 × 2.75 cells</b> at any cell size, and
        /// <c>bounds</c> would report 0.625 × 1.375 at a 0.8 cell.</para>
        ///
        /// <para><b>Why there is no companion "registration lands on the corner" fixture.</b> There was
        /// one, and it was vacuous: it added back exactly the <c>−origin + w/2</c> that
        /// <see cref="RoomTerrainGeometry.Overlay"/> subtracts, so it was true for any size whatsoever
        /// and survived the mutant that reintroduced this very bug. A fixture that inverts the function
        /// it is testing is an identity, not a test. The absolute corner is already pinned by
        /// <see cref="Overlay_PinsTheRegistrationPointToTheCellCorner"/> with hardcoded deltas.</para>
        /// </summary>
        [Test]
        public void OverlaySize_IsConstantInCells()
        {
            foreach (float cellSize in new[] { 0.2f, 0.4f, 0.8f, 1.5f })
            {
                Vector2 size = RoomTerrainGeometry.OverlaySize(50f, 110f, cellSize, 40);

                Assert.AreEqual(1.25f, size.x / cellSize, 0.0001f, $"width in cells at cell {cellSize}");
                Assert.AreEqual(2.75f, size.y / cellSize, 0.0001f, $"height in cells at cell {cellSize}");
            }
        }

        /// <summary>
        /// The shipped frames are 50×110 with the registration at (5, 30) — that is Remains-Unity's
        /// manifest for <c>front:1..16</c> and it is corroborated by our own export, where every frame's
        /// content starts at <c>y = 30</c> and frames 5..16 start at <c>x = 5</c>. If a future export
        /// changes the canvas size, this fails and the constants must be re-derived rather than
        /// silently kept.
        /// </summary>
        [Test]
        public void OverlayOrigin_MatchesTheExportedCanvas()
        {
            Assert.AreEqual(5, RoomTerrainArt.OverlayOriginXPixels);
            Assert.AreEqual(30, RoomTerrainArt.OverlayOriginYPixels);

            // The origin must land inside the canvas it was measured against, and the art must be
            // able to reach the cell corner it is pinned to.
            Assert.Less(RoomTerrainArt.OverlayOriginXPixels, 50);
            Assert.Less(RoomTerrainArt.OverlayOriginYPixels, 110);
        }

        [Test]
        public void QuarterCells_AreDistinctWithinACell()
        {
            var seen = new System.Collections.Generic.HashSet<Vector2Int>();

            foreach (RoomQuarter quarter in RoomQuarterRules.All())
            {
                var cell = new Vector2Int(
                    RoomTerrainGeometry.QuarterCellX(3, quarter),
                    RoomTerrainGeometry.QuarterCellY(5, quarter));

                Assert.IsTrue(seen.Add(cell), $"{quarter} collided with another quarter at {cell}");
            }

            Assert.AreEqual(4, seen.Count);
        }

        [Test]
        public void QuarterNames_RoundTrip()
        {
            foreach (RoomQuarter quarter in RoomQuarterRules.All())
            {
                string name = RoomQuarterRules.Name(quarter);
                Assert.IsTrue(RoomQuarterRules.TryParse(name, out RoomQuarter parsed), name);
                Assert.AreEqual(quarter, parsed, name);
                Assert.IsTrue(RoomQuarterRules.TryParse(name.ToUpperInvariant(), out RoomQuarter upper), name);
                Assert.AreEqual(quarter, upper, name + " (upper)");
            }

            Assert.IsFalse(RoomQuarterRules.TryParse("top-middle", out _));
            Assert.IsFalse(RoomQuarterRules.TryParse(null, out _));
            Assert.IsFalse(RoomQuarterRules.TryParse(string.Empty, out _));
        }

        // ─── Non-solid cells that carry art (platforms, beams) ───

        [Test]
        public void Compute_NonSolidFrontCell_IsAPlainSquareNotAnEdge()
        {
            // The oracle: `Location.tileKontur` fills kont1..4 inside `if (phis == 1)` only, and
            // `Tile.kont1` is declared `= 0`. A platform's front is therefore a full square, and every
            // quarter reads state 0. Computing real edges here would cut the corners off every beam —
            // a rule that sounds more careful and is wrong.
            RoomQuarterStates states = RoomQuarterStates.Compute(
                5, 5,
                isSolid: (x, y) => false,
                backId: (x, y) => 0,
                isShelf: (x, y) => x == 2 && y == 2,
                skyBackdrop: false,
                hasFront: (x, y) => x == 2 && y == 2);

            foreach (RoomQuarter quarter in RoomQuarterRules.All())
            {
                Assert.AreEqual(RoomQuarterRules.Inside, states.Front(2, 2, quarter),
                    "a non-solid cell with a front graphic draws a plain square on " + quarter);
            }

            Assert.AreEqual(1, states.CountFrontCells(), "only the platform cell has a front");
        }

        [Test]
        public void Compute_NonSolidWithoutFront_IsStillNotDrawn()
        {
            // The control for the test above: without the predicate the same platform cell is dropped,
            // which is exactly the defect that made platforms disappear. Both directions have to be
            // pinned, or "it draws something" would pass either way.
            RoomQuarterStates states = RoomQuarterStates.Compute(
                5, 5,
                isSolid: (x, y) => false,
                backId: (x, y) => 0,
                isShelf: (x, y) => x == 2 && y == 2,
                skyBackdrop: false);

            foreach (RoomQuarter quarter in RoomQuarterRules.All())
            {
                Assert.AreEqual(RoomQuarterRules.NotDrawn, states.Front(2, 2, quarter),
                    "no hasFront predicate means no front state — the pre-fix behaviour");
            }
        }

        // ─── Overlay frames ──────────────────────────────────────

        [Test]
        public void OverlayIndex_IsVidMinusOne()
        {
            // AS3 selects a tileFront frame by a bare integer (`tile.gotoAndStop(t.vid)`), and the
            // export names the frames 1..16, so the array index is the id minus one. An off-by-one
            // here draws the wrong ladder for every cell in the game and still looks like a ladder.
            Assert.AreEqual(0, RoomTerrainArt.OverlayIndex(1, 16), "vid 1 is the first frame");
            Assert.AreEqual(15, RoomTerrainArt.OverlayIndex(16, 16), "vid 16 is the last frame");
            Assert.AreEqual(3, RoomTerrainArt.OverlayIndex(4, 16));
        }

        [Test]
        public void OverlayIndex_UnknownVid_IsNone_NotTheLastFrame()
        {
            // The dangerous mutant is a clamp: an id past the end drawing the last frame looks like
            // "some ladder" instead of a missing one, and the presenter's missing-frame counter would
            // stay at zero while the picture is wrong.
            Assert.AreEqual(-1, RoomTerrainArt.OverlayIndex(17, 16), "past the end");
            Assert.AreEqual(-1, RoomTerrainArt.OverlayIndex(0, 16), "0 is 'no overlay', not frame 1");
            Assert.AreEqual(-1, RoomTerrainArt.OverlayIndex(-3, 16));
            Assert.AreEqual(-1, RoomTerrainArt.OverlayIndex(1, 0), "an art asset with no frames at all");
        }

        [Test]
        public void Validate_SaysSoWhenThereAreNoOverlayFrames()
        {
            // An asset built before overlays existed is a real state: every ladder disappears and
            // nothing throws. The renderer counts the misses, and this is the sentence that tells the
            // reader to rebuild rather than to look for a geometry bug.
            //
            // Written against the pure helper, not against an instance: ScriptableObject.CreateInstance
            // is an ECall and throws "must be packaged into a system module" offline, the same wall the
            // ToMatrix note below describes.
            string missing = RoomTerrainArt.ValidateOverlayCount(0);
            Assert.IsNotNull(missing, "an asset with no overlay frames must say so");
            StringAssert.Contains("overlay", missing.ToLowerInvariant());
            StringAssert.Contains("rebuild", missing.ToLowerInvariant());

            Assert.IsNull(RoomTerrainArt.ValidateOverlayCount(16), "16 frames is the shipped export");
        }

        // NOTE: there is deliberately no test of RoomTerrainPlacement.ToMatrix(). Matrix4x4's TRS and
        // MultiplyVector are engine internal calls, so the fixture throws
        // "SecurityException: ECall methods must be packaged into a system module" outside the editor.
        // The claim that matters — the translation and the signed scale — is pinned above, and the
        // composition is one Matrix4x4.TRS call; the A/B window verifies it on screen (set Gap to 0 and
        // the two sides must coincide).

        // ─── Fixtures ────────────────────────────────────────────

        private static RoomQuarterStates Compute(int width, int height, Func<int, int, bool> solid)
        {
            return RoomQuarterStates.Compute(width, height, solid, (x, y) => 0, (x, y) => false, false);
        }

        private static RoomInstance BuildRoom(int width, int height)
        {
            var room = new RoomInstance { id = "roomterrain_test", width = width, height = height };
            room.InitializeTiles();
            return room;
        }
    }
}
