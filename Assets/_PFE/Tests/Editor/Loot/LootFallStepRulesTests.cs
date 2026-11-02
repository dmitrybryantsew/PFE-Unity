using System;
using System.Collections.Generic;
using NUnit.Framework;
using PFE.Systems.Loot;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.Editor.Loot
{
    /// <summary>
    /// The collision half of <c>fe.loc.Loot</c> — <see cref="LootFallStepRules.Step"/>.
    ///
    /// <para><b>Every expected number below is the oracle's, translated — not the port's output
    /// recorded.</b> That distinction is the point of this fixture. The first port of <c>Loot.run</c> had
    /// its vertical comparisons un-mirrored (<c>bottomAfter &gt;= Top</c> where the mirror is
    /// <c>&lt;=</c>), which made the landing branch <b>unsatisfiable by construction</b>: the tile is
    /// chosen as the one containing <c>bottomAfter</c>, so <c>bottomAfter &lt; Top</c> always holds. Loot
    /// fell through every floor in the game, and no test could see it because the decision lived inside a
    /// <c>MonoBehaviour</c>. A fixture that recorded behaviour would have recorded that too.</para>
    ///
    /// <para><b>Geometry.</b> A room is 48×25 tiles of 40 px, so 1920×1000 px, with tile (0,0) at the
    /// floor-left corner and Y growing up. A tile at column <c>tx</c>, row <c>ty</c> spans
    /// <c>[tx·40, (tx+1)·40] × [ty·40, (ty+1)·40]</c>.</para>
    /// </summary>
    [TestFixture]
    public class LootFallStepRulesTests
    {
        private const float TileSize = 40f;
        private const float RoomWidthPx = 48f * TileSize;
        private const float RoomHeightPx = 25f * TileSize;

        private enum Kind
        {
            Solid,
            Shelf,
        }

        /// <summary>A tile grid built from nothing, so the rule can be run without a <c>RoomInstance</c>.</summary>
        private sealed class Grid
        {
            private readonly Dictionary<(int X, int Y), Kind> _tiles =
                new Dictionary<(int X, int Y), Kind>();

            public Grid With(int x, int y, Kind kind)
            {
                _tiles[(x, y)] = kind;
                return this;
            }

            public bool Probe(float x, float y, out LootTileBox box, out bool solid, out bool shelf)
            {
                solid = false;
                shelf = false;
                box = default;

                int tx = (int)Math.Floor(x / TileSize);
                int ty = (int)Math.Floor(y / TileSize);

                if (!_tiles.TryGetValue((tx, ty), out Kind kind))
                {
                    return false;
                }

                box = new LootTileBox(tx * TileSize, (tx + 1) * TileSize,
                                      (ty + 1) * TileSize, ty * TileSize);
                solid = kind == Kind.Solid;
                shelf = kind == Kind.Shelf;
                return true;
            }
        }

        /// <summary>A recording stand-in for <c>Loot.checkShelf</c>.</summary>
        private sealed class ShelfProbe
        {
            public bool Answer;
            public float Top;
            public int Calls;
            public float LastCentreX;
            public float LastBottom;
            public float LastDeltaY;

            public bool Probe(float centreX, float bottom, float deltaY, out float surfaceTop)
            {
                Calls++;
                LastCentreX = centreX;
                LastBottom = bottom;
                LastDeltaY = deltaY;
                surfaceTop = Answer ? Top : 0f;
                return Answer;
            }
        }

        private static LootFallState Step(
            Grid grid, ShelfProbe shelf, float centreX, float bottom, float vx, float vy,
            float height = 20f, float halfWidth = 15f, int divisor = 1)
        {
            LootTileProbe tileProbe = grid == null ? null : (LootTileProbe)grid.Probe;
            LootShelfProbe shelfProbe = shelf == null ? null : (LootShelfProbe)shelf.Probe;

            return LootFallStepRules.Step(
                centreX, bottom, halfWidth, height, vx, vy, divisor,
                RoomWidthPx, RoomHeightPx, tileProbe, shelfProbe);
        }

        // ── Landing on a tile ──────────────────────────────────────────────────

        [Test]
        public void Loot_FallingIntoTheFloorTile_LandsOnePixelAboveItsTop()
        {
            // Loot.as:463-478. The destination tile is (5,0) — solid, top edge y = 40, underside y = 0.
            // The loot's bottom starts at 45 (inside row 1, which is air) and this step takes it to 36,
            // which is INSIDE row 0. The oracle's mirrored test is `newBottom <= phY1 && oldBottom >=
            // phY2`, i.e. 36 <= 40 and 45 >= 0 — the crossing of the top edge, from above.
            var grid = new Grid().With(5, 0, Kind.Solid);

            LootFallState state = Step(grid, null, centreX: 220f, bottom: 45f, vx: 0f, vy: -9f);

            Assert.That(state.Contact, Is.EqualTo(LootContact.Floor));
            Assert.That(state.SurfaceTop, Is.EqualTo(40f), "the tile's top edge.");
            Assert.That(state.Bottom, Is.EqualTo(41f),
                "`Y = _loc4_ - 1` in AS3's y-down space is `Top + 1` here — one pixel CLEAR of the " +
                "surface, not one pixel inside it.");
            Assert.That(state.Vx, Is.EqualTo(0f));
            Assert.That(state.Vy, Is.EqualTo(0f), "`dy = dx = 0`.");
            Assert.That(state.ImpactSpeed, Is.EqualTo(9f),
                "the downward speed at contact, which gates and scales the landing sample.");
            Assert.That(state.Stopped, Is.True);
        }

        [Test]
        public void Loot_AlreadyStandingInsideASolidTile_IsLiftedOntoItsTop()
        {
            // A container can eject loot into the tile the crate itself occupies. The destination tile is
            // solid and the old bottom is already inside it, so `oldBottom >= phY2` holds and the loot is
            // placed on the top edge instead of sinking — the oracle's behaviour, and the reason a pickup
            // spawned overlapping the floor does not fall through it.
            var grid = new Grid().With(5, 1, Kind.Solid);

            LootFallState state = Step(grid, null, centreX: 220f, bottom: 60f, vx: 0f, vy: -9f);

            Assert.That(state.Contact, Is.EqualTo(LootContact.Floor));
            Assert.That(state.SurfaceTop, Is.EqualTo(80f));
            Assert.That(state.Bottom, Is.EqualTo(81f));
        }

        [Test]
        public void Loot_OnlyTheDestinationTileIsTested_NotTheOneItIsStandingIn()
        {
            // The oracle asks `getAbsTile(X, Y + dy/param1)` — the tile the bottom is moving INTO. It does
            // not sweep, so a loot whose destination cell is air keeps falling even if the cell it is
            // leaving is solid. Pinned so nobody "fixes" this into a sweep: a sweep would change where
            // loot rests relative to the oracle.
            var grid = new Grid().With(5, 1, Kind.Solid);

            LootFallState state = Step(grid, null, centreX: 220f, bottom: 45f, vx: 0f, vy: -9f);

            Assert.That(state.Contact, Is.EqualTo(LootContact.None));
            Assert.That(state.Bottom, Is.EqualTo(36f), "it moved, and nothing caught it.");
        }

        [Test]
        public void Loot_LandsOnAShelfTile_FromAboveOnly()
        {
            // `_loc2_.shelf && … && Y + dy/param1 >= phY1 && Y <= phY1` (Loot.as:464) — a one-way catwalk
            // catches a loot crossing its TOP edge, and requires the old bottom to have been above it.
            var grid = new Grid().With(5, 2, Kind.Shelf);   // spans y 80..120

            LootFallState fromAbove = Step(grid, null, centreX: 220f, bottom: 124f, vx: 0f, vy: -9f);

            Assert.That(fromAbove.Contact, Is.EqualTo(LootContact.Floor));
            Assert.That(fromAbove.SurfaceTop, Is.EqualTo(120f));
            Assert.That(fromAbove.Bottom, Is.EqualTo(121f));

            // Already inside the shelf: `Y <= phY1` fails, so it falls straight through. That is what makes
            // a catwalk jumpable-through from below and catchable from above.
            LootFallState fromInside = Step(grid, null, centreX: 220f, bottom: 100f, vx: 0f, vy: -9f);

            Assert.That(fromInside.Contact, Is.EqualTo(LootContact.None),
                "a shelf must not catch a loot whose bottom is already below its top edge.");
            Assert.That(fromInside.Bottom, Is.EqualTo(91f));
        }

        [Test]
        public void Loot_LandsOnAShelfProp_WhenNoTileIsUnderIt()
        {
            // `if(_loc4_ == 0 && !levit && !this.vsos) _loc4_ = this.checkShelf(dy / param1)`
            // (Loot.as:468-471) — a crate's top edge, only when the tile search found nothing.
            var shelf = new ShelfProbe { Answer = true, Top = 95f };

            LootFallState state = Step(new Grid(), shelf, centreX: 220f, bottom: 100f, vx: 0f, vy: -9f);

            Assert.That(state.Contact, Is.EqualTo(LootContact.Floor));
            Assert.That(state.SurfaceTop, Is.EqualTo(95f));
            Assert.That(state.Bottom, Is.EqualTo(96f));
            Assert.That(shelf.Calls, Is.EqualTo(1));

            // The probe is asked in room-local pixels and with this sub-step's motion, negative downward —
            // `TryFindLootSupport` documents both, and the first port handed it world pixels.
            Assert.That(shelf.LastCentreX, Is.EqualTo(220f));
            Assert.That(shelf.LastBottom, Is.EqualTo(100f), "the bottom BEFORE the move, as AS3 passes Y.");
            Assert.That(shelf.LastDeltaY, Is.EqualTo(-9f), "negative is downward in the port's y-up space.");
        }

        [Test]
        public void Loot_IgnoresTheShelfProp_WhenATileAlreadyCaughtIt()
        {
            var grid = new Grid().With(5, 0, Kind.Solid);
            var shelf = new ShelfProbe { Answer = true, Top = 500f };

            LootFallState state = Step(grid, shelf, centreX: 220f, bottom: 45f, vx: 0f, vy: -9f);

            Assert.That(state.SurfaceTop, Is.EqualTo(40f), "the tile's top wins.");
            Assert.That(shelf.Calls, Is.EqualTo(0),
                "`if(_loc4_ == 0 …)` — the prop search must not even run once a tile has caught it.");
        }

        // ── Ceiling and walls ──────────────────────────────────────────────────

        [Test]
        public void Loot_RisingIntoASolidTile_StopsJustBelowItsUnderside()
        {
            // Loot.as:435-448 — `if(phis == 1 && Y <= phY2 && Y >= phY1 && …) Y = phY2 + 1; dy = 0;`.
            // The oracle tests the loot's BOTTOM here, not its top, and snaps it one pixel below the
            // tile's underside. In y-up that underside is the tile's `Bottom`, so the answer is Bottom-1.
            var grid = new Grid().With(5, 3, Kind.Solid);   // spans y 120..160

            LootFallState state = Step(grid, null, centreX: 220f, bottom: 115f, vx: 0f, vy: 9f);

            Assert.That(state.Contact, Is.EqualTo(LootContact.Ceiling));
            Assert.That(state.Bottom, Is.EqualTo(119f), "one pixel below the underside at 120.");
            Assert.That(state.Vy, Is.EqualTo(0f));
            Assert.That(state.Stopped, Is.False, "a ceiling is not a landing — the loot keeps its X and falls next tick.");
        }

        [Test]
        public void Loot_RisingInAnOpenRoom_StopsAtTheRoomCeiling()
        {
            // `if(Y - scY < 0) Y = scY` (Loot.as:439-442) — the loot's TOP may not leave the room. Mirrored:
            // `bottom + height > roomHeight` is clamped to `roomHeight - height`.
            LootFallState state = Step(new Grid(), null, centreX: 220f, bottom: 990f, vx: 0f, vy: 9f,
                                       height: 20f);

            Assert.That(state.Bottom, Is.EqualTo(980f));
            Assert.That(state.Contact, Is.EqualTo(LootContact.None));
            Assert.That(state.Vy, Is.EqualTo(9f), "the clamp is a position, not a velocity.");
        }

        [Test]
        public void Loot_MovingLeftIntoAWall_SnapsJustClearOfItsRightEdge()
        {
            // Loot.as:417-425 — the tile at the NEW X, the loot's BOTTOM inside the tile vertically.
            var grid = new Grid().With(4, 1, Kind.Solid);   // spans x 160..200, y 40..80

            LootFallState state = Step(grid, null, centreX: 205f, bottom: 60f, vx: -9f, vy: 0f);

            Assert.That(state.Contact, Is.EqualTo(LootContact.WallLeft));
            Assert.That(state.CentreX, Is.EqualTo(201f), "`X = phX2 + 1` — the tile's right edge, plus one.");
            Assert.That(state.Vx, Is.EqualTo(9f), "`dx = Math.abs(dx)` — it bounces.");
        }

        [Test]
        public void Loot_MovingRightIntoAWall_SnapsJustClearOfItsLeftEdge()
        {
            var grid = new Grid().With(4, 1, Kind.Solid);

            LootFallState state = Step(grid, null, centreX: 155f, bottom: 60f, vx: 9f, vy: 0f);

            Assert.That(state.Contact, Is.EqualTo(LootContact.WallRight));
            Assert.That(state.CentreX, Is.EqualTo(159f), "`X = phX1 - 1`.");
            Assert.That(state.Vx, Is.EqualTo(-9f));
        }

        [Test]
        public void Loot_AboveAWall_DoesNotCollideWithIt()
        {
            // The horizontal test samples the loot's BOTTOM, not its centre. A loot at bottom = 100 is
            // above the 40..80 tile, so the tile at (196, 100) is a different, empty cell.
            var grid = new Grid().With(4, 1, Kind.Solid);

            LootFallState state = Step(grid, null, centreX: 205f, bottom: 100f, vx: -9f, vy: 0f);

            Assert.That(state.Contact, Is.EqualTo(LootContact.None));
            Assert.That(state.CentreX, Is.EqualTo(196f));
        }

        [Test]
        public void Loot_AtTheRoomEdges_IsClampedByItsHalfWidth()
        {
            // Loot.as:407-416. `if(X - scX/2 < 0)` and `if(X + scX/2 >= spaceX * tileX)`.
            LootFallState left = Step(new Grid(), null, centreX: 10f, bottom: 500f, vx: -9f, vy: 0f,
                                      halfWidth: 15f);
            Assert.That(left.CentreX, Is.EqualTo(15f));
            Assert.That(left.Vx, Is.EqualTo(9f));

            LootFallState right = Step(new Grid(), null, centreX: 1910f, bottom: 500f, vx: 9f, vy: 0f,
                                       halfWidth: 15f);
            Assert.That(right.CentreX, Is.EqualTo(1904f), "`spaceX * tileX - 1 - scX/2`.");
            Assert.That(right.Vx, Is.EqualTo(-9f));
        }

        // ── Leaving the room, and free fall ────────────────────────────────────

        [Test]
        public void Loot_BelowTheRoomFloor_ReportsFellOutOfRoom_AndDoesNotMove()
        {
            // Loot.as:454-462 — `if(Y + dy/param1 >= loc.spaceY * Tile.tileY) { … dx = 0; return; }`.
            // Mirrored: the room's floor is y = 0, so "past the bottom" is `bottomAfter <= 0`. The oracle
            // returns BEFORE `Y += dy/param1`, so the position does not change.
            LootFallState state = Step(new Grid(), null, centreX: 220f, bottom: 5f, vx: 3f, vy: -9f);

            Assert.That(state.Contact, Is.EqualTo(LootContact.FellOutOfRoom));
            Assert.That(state.Bottom, Is.EqualTo(5f), "the oracle returns without moving Y.");
            Assert.That(state.Vx, Is.EqualTo(0f), "`dx = 0`.");
            Assert.That(state.Vy, Is.EqualTo(0f));
            Assert.That(state.Stopped, Is.True);
        }

        [Test]
        public void Loot_WithNoTiles_FallsFreely()
        {
            LootFallState state = Step(new Grid(), null, centreX: 220f, bottom: 500f, vx: 2f, vy: -9f);

            Assert.That(state.Contact, Is.EqualTo(LootContact.None));
            Assert.That(state.CentreX, Is.EqualTo(222f));
            Assert.That(state.Bottom, Is.EqualTo(491f));
            Assert.That(state.Vy, Is.EqualTo(-9f));
        }

        [Test]
        public void Step_DividesTheMotionByTheSubstepCount()
        {
            // `X += dx / param1` and `Y + dy / param1` — the caller has already decided how many
            // sub-steps this frame needs (`LootFallRules.SubstepCount`).
            LootFallState state = Step(new Grid(), null, centreX: 220f, bottom: 500f, vx: 9f, vy: -9f,
                                       divisor: 3);

            Assert.That(state.CentreX, Is.EqualTo(223f));
            Assert.That(state.Bottom, Is.EqualTo(497f));
        }
    }

    /// <summary>
    /// What the rules fixture cannot reach: <c>WorldItemPickup.StepOnce</c> is a <c>MonoBehaviour</c>, so
    /// its conversion cannot be executed offline. These lints are the guard — the project's established
    /// instrument for exactly this (see <c>SourceLint</c>).
    ///
    /// <para><b>What they protect.</b> <see cref="LootFallStepRules.Step"/> is correct only if it is
    /// handed <b>room-local</b> pixels; the component's transform is in world units. The first port fed
    /// it world pixels, which is right in a room at land (0,0) — the room every quick test visits — and
    /// wrong by a room width or height everywhere else. That is a wiring error with no arithmetic
    /// signature, so a lint is the only thing that can fail for it.</para>
    /// </summary>
    [TestFixture]
    public class LootFallWiringLintTests
    {
        private const string Component = "Systems/Inventory/WorldItemPickup.cs";

        [Test]
        public void StepOnce_ConvertsToRoomLocal_BeforeCallingTheRules()
        {
            string source = SourceLint.ReadStripped(Component);
            string body = SourceLint.MethodBody(source, "private void StepOnce(int divisor)");

            Assert.That(body, Does.Contain("LootFallStepRules.Step("),
                "StepOnce must delegate the decision; re-deriving it inline is how the landing test " +
                "shipped un-mirrored and untestable.");

            Assert.That(body, Does.Contain("RoomOriginPixel"),
                "the room origin has to be read — the rules speak room-local pixels.");
            Assert.That(body, Does.Contain("- origin.x"),
                "X must have the origin subtracted before the call, or every room off land (0,0) is wrong.");
            Assert.That(body, Does.Contain("- origin.y"),
                "Y must have the origin subtracted before the call, for the same reason.");
        }

        [Test]
        public void StepOnce_AddsTheOriginBack_WhenItWritesTheTransform()
        {
            string source = SourceLint.ReadStripped(Component);
            string body = SourceLint.MethodBody(source, "private void StepOnce(int divisor)");

            Assert.That(body, Does.Contain("+ origin.x"), "the world position must restore the origin.");
            Assert.That(body, Does.Contain("+ origin.y"));
            Assert.That(body, Does.Contain("_bottomOffsetPx"),
                "the sprite's pivot offset must be applied on the way out as well as in.");
        }

        [Test]
        public void TheComponent_NoLongerComparesTileEdgesItself()
        {
            string source = SourceLint.ReadStripped(Component);

            foreach (string symbol in new[] { "TileTop(", "TileBottom(", "TileLeft(", "TileRight(",
                                              "FindLandingSurface" })
            {
                Assert.That(source, Does.Not.Contain(symbol),
                    "`" + symbol + "` belongs to LootFallStepRules now. A tile-edge comparison in the " +
                    "component is a second copy of the decision, and the two will disagree.");
            }
        }
    }
}
