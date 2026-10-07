using NUnit.Framework;
using UnityEngine;
using PFE.Systems.Map;
using PFE.Systems.Map.TileQuery;
using PFE.Tests.EditMode.Systems.Map.TileCollision;

namespace PFE.Tests.EditMode.Systems.Map.TileQuery
{
    /// <summary>
    /// Pins <see cref="PropCollisionRule"/> — the third tile predicate, the one that answers "does
    /// this tile stop a <b>dynamic prop</b>?".
    ///
    /// <para><b>Why this fixture exists.</b> The prop path used to call the <i>unit's</i> rule
    /// (<c>RoomInstance.CheckCollision</c> → <c>TileCollisionMath.CheckCollision</c>). AS3 gives props
    /// their own predicate — <c>loc/Box.collisionTile</c> (<c>loc/Box.as:1260-1275</c>) — and it
    /// disagrees with the unit's in three places. Each is a real divergence, and each is asserted
    /// below so that "reusing the shared function" cannot quietly come back.</para>
    ///
    /// <para><b>The three divergences, named.</b>
    /// <list type="number">
    /// <item><description><b>Ramps blocked.</b> <c>CheckCollision</c> returns true for a slope
    /// (<c>TileCollisionMath.cs:450</c> for an <c>Air</c> slope, <c>:505</c> for a
    /// <c>Stair</c> one). AS3 passes a prop over a ramp (<c>Box.as:1262</c>).</description></item>
    /// <item><description><b>Stairs blocked.</b> <c>CheckCollision</c> returns true for
    /// <c>physicsType == Stair</c> (<c>:505</c>). AS3 passes (<c>Box.as:1262</c>).</description></item>
    /// <item><description><b>The one-way window.</b> <c>CheckCollision</c> gates a platform on a
    /// <c>porog</c> window (<c>:497</c>) with a <c>velocityY &gt; 0</c> rising guard (<c>:484</c>)
    /// that the prop caller defeated by hardcoding <c>velocityY = 0</c>. AS3 uses a half-line — the
    /// prop's <i>current</i> bottom against the shelf top (<c>:1270</c>) — with no window, so a
    /// fast-falling prop cannot tunnel through.</description></item>
    /// </list></para>
    /// </summary>
    [TestFixture]
    public sealed class PropCollisionRuleTests
    {
        // A tile at grid (1,1): x 40..80, y 40..80, so its top edge is 80.
        private static readonly Rect Tile = new Rect(40f, 40f, 40f, 40f);

        // A prop AABB that overlaps that tile (x 45..75, y 45..75).
        private static readonly Rect Overlapping = new Rect(45f, 45f, 30f, 30f);

        // A prop AABB nowhere near that tile.
        private static readonly Rect Faraway = new Rect(200f, 200f, 10f, 10f);

        // ── The truth table ──────────────────────────────────────────────────────────────────

        [TestCase(SurfaceKind.None,   false, "air — nothing to collide with")]
        [TestCase(SurfaceKind.Solid,  true,  "a wall blocks a prop")]
        [TestCase(SurfaceKind.Shelf,  true,  "a shelf blocks from above (the current bottom is above its top)")]
        [TestCase(SurfaceKind.Diagon, false, "a ramp passes a prop (divergence 1)")]
        [TestCase(SurfaceKind.Stair,  false, "a stair passes a prop (divergence 2)")]
        public void BlocksProp_TruthTable(SurfaceKind kind, bool expected, string why)
        {
            // The prop's current bottom sits at 85, above the tile top of 80, so the shelf clause is
            // satisfied for the Shelf case; the other kinds ignore it.
            var current = new Rect(45f, 85f, 30f, 30f);

            Assert.That(
                PropCollisionRule.BlocksProp(kind, Overlapping, current, Tile, false, false),
                Is.EqualTo(expected), $"{kind}: {why}.");
        }

        /// <summary>
        /// A wall only blocks when the prop actually overlaps it — the candidate AABB is the test,
        /// not the tile alone.
        /// </summary>
        [Test]
        public void BlocksProp_Solid_DoesNotBlockWithoutOverlap()
        {
            Assert.That(
                PropCollisionRule.BlocksProp(SurfaceKind.Solid, Faraway, Faraway, Tile, false, false),
                Is.False, "A wall with no AABB overlap is not a collision.");
        }

        // ── The shelf: a half-line, not a window (divergence 3) ───────────────────────────────

        /// <summary>
        /// A shelf blocks from above: a prop whose <i>current</i> bottom is at the shelf top is
        /// stopped. This is the "resting on / falling onto" half of a one-way platform.
        /// </summary>
        [Test]
        public void BlocksProp_Shelf_BlocksFromAbove()
        {
            var current = new Rect(45f, 80f, 30f, 30f); // bottom exactly at the shelf top

            Assert.That(
                PropCollisionRule.BlocksProp(SurfaceKind.Shelf, Overlapping, current, Tile, false, false),
                Is.True, "A prop resting on a shelf is supported by it.");
        }

        /// <summary>
        /// The prop's bottom is <i>far</i> above the shelf top and it still blocks — there is no
        /// upper window. The old <c>porog</c> window (<c>TileCollisionMath.cs:497</c>) required the
        /// bottom to be within a few pixels of the tile top; a fast-falling prop would pass straight
        /// through. AS3 has no such bound, so neither does this rule.
        /// </summary>
        [Test]
        public void BlocksProp_Shelf_HasNoUpperWindow_SoAFastFallCannotTunnel()
        {
            var current = new Rect(45f, 400f, 30f, 30f); // bottom 320 px above the shelf top

            Assert.That(
                PropCollisionRule.BlocksProp(SurfaceKind.Shelf, Overlapping, current, Tile, false, false),
                Is.True, "A shelf has no upper window: a fast-falling prop must still be stopped.");
        }

        /// <summary>
        /// A prop whose bottom is already below the shelf top has passed through it, so the shelf no
        /// longer blocks — the "from below" half of a one-way platform. This is the test that fails
        /// if the rule is simplified to "a shelf always blocks", which would make every catwalk a
        /// ceiling.
        /// </summary>
        [Test]
        public void BlocksProp_Shelf_PassesWhenAlreadyBelow()
        {
            var current = new Rect(45f, 60f, 30f, 30f); // bottom below the shelf top of 80

            Assert.That(
                PropCollisionRule.BlocksProp(SurfaceKind.Shelf, Overlapping, current, Tile, false, false),
                Is.False, "A prop already through a shelf must not be blocked by it.");
        }

        /// <summary>
        /// Telekinesis and a thrown prop bypass a shelf outright (<c>levit</c>/<c>isThrow</c>,
        /// <c>Box.as:1270</c>). The old path hardcoded both to <c>false</c>, so the bypass could never
        /// fire.
        /// </summary>
        [TestCase(true,  false, "held by telekinesis")]
        [TestCase(false, true,  "thrown")]
        [TestCase(true,  true,  "both")]
        public void BlocksProp_Shelf_IsBypassedByLevitatingOrThrown(bool isLevitating, bool isThrown, string why)
        {
            var current = new Rect(45f, 80f, 30f, 30f); // would block if not bypassed

            Assert.That(
                PropCollisionRule.BlocksProp(SurfaceKind.Shelf, Overlapping, current, Tile, isLevitating, isThrown),
                Is.False, $"A shelf does not block a prop that is {why}.");
        }

        // ── The wrong predicate, named ───────────────────────────────────────────────────────

        /// <summary>
        /// Names the wrong predicate explicitly. <see cref="TileData.IsSolid"/> is
        /// <c>physicsType &gt;= Wall</c>, so it is true for a ramp and a stair — but AS3 passes a prop
        /// over both. Reusing it (or the shared <c>CheckCollision</c> that implements it) is exactly
        /// how props came to catch on every ramp and staircase.
        /// </summary>
        [Test]
        public void TileDataIsSolidIsTheWrongPredicate_ForRampsAndStairs()
        {
            var ramp = new TileData { physicsType = TilePhysicsType.Stair, slopeType = 1 };
            var stair = new TileData { physicsType = TilePhysicsType.Stair };

            Assert.That(ramp.IsSolid(), Is.True, "A ramp is a movement surface, so IsSolid() is true.");
            Assert.That(SurfaceKindRule.Of(ramp), Is.EqualTo(SurfaceKind.Diagon));
            Assert.That(
                PropCollisionRule.BlocksProp(SurfaceKindRule.Of(ramp), Overlapping, Overlapping, Tile, false, false),
                Is.False,
                "A prop must pass over a ramp. If this fails the rule has been widened to the unit's " +
                "predicate and props will catch on every slope.");

            Assert.That(stair.IsSolid(), Is.True, "A stair is a movement surface, so IsSolid() is true.");
            Assert.That(SurfaceKindRule.Of(stair), Is.EqualTo(SurfaceKind.Stair));
            Assert.That(
                PropCollisionRule.BlocksProp(SurfaceKindRule.Of(stair), Overlapping, Overlapping, Tile, false, false),
                Is.False,
                "A prop must pass over a stair. If this fails props will catch on every staircase.");
        }

        // ── The AABB form, over a room ────────────────────────────────────────────────────────

        /// <summary>
        /// <see cref="PropCollisionRule.BlocksMove"/> iterates the same tile range the tile query
        /// does. One room carries every surface kind, so each branch is reached through the real
        /// entry point rather than only through <see cref="PropCollisionRule.BlocksProp"/>.
        /// </summary>
        [Test]
        public void BlocksMove_ReachesEverySurfaceKindThroughTheRoom()
        {
            //       0         1         2         3         4         5         6         7         8
            //       012345678
            string[] rows =
            {
                "#########",   // y = 3
                "#.......#",   // y = 2
                "#.=./.H.#",   // y = 1  → x2 Platform, x4 Slope(/), x6 Ladder(H)
                "#########"    // y = 0
            };

            RoomInstance room = SyntheticRoomBuilder.BuildFromAscii(rows);

            // Wall at (0,2): bounds x 0..40, y 80..120.
            Assert.That(
                PropCollisionRule.BlocksMove(room, new Rect(5f, 85f, 20f, 20f), new Rect(5f, 85f, 20f, 20f), false, false),
                Is.True, "A prop AABB inside the wall cell (0,2) must be blocked.");

            // Platform at (2,1): bounds x 80..120, y 40..80, top = 80. Prop falling onto it:
            // current bottom at 82 (above the top), candidate bottom at 77 (penetrating the cell).
            Assert.That(
                PropCollisionRule.BlocksMove(room, new Rect(85f, 77f, 20f, 20f), new Rect(85f, 82f, 20f, 20f), false, false),
                Is.True, "A prop falling onto the shelf (2,1) must be blocked.");

            // Same shelf, same penetration, but the prop's current bottom is already below the top.
            Assert.That(
                PropCollisionRule.BlocksMove(room, new Rect(85f, 65f, 20f, 20f), new Rect(85f, 60f, 20f, 20f), false, false),
                Is.False, "A prop already through the shelf (2,1) must pass.");

            // Slope at (4,1): bounds x 160..200, y 40..80.
            Assert.That(
                PropCollisionRule.BlocksMove(room, new Rect(165f, 45f, 20f, 20f), new Rect(165f, 45f, 20f, 20f), false, false),
                Is.False, "A prop over the ramp (4,1) must pass.");

            // Ladder at (6,1): bounds x 240..280, y 40..80.
            Assert.That(
                PropCollisionRule.BlocksMove(room, new Rect(245f, 45f, 20f, 20f), new Rect(245f, 45f, 20f, 20f), false, false),
                Is.False, "A prop over the ladder (6,1) must pass.");
        }

        /// <summary>
        /// A null room, or one with no tile array, blocks nothing — the same contract
        /// <c>TileCollisionMath.CheckCollision</c> has, and the same shape as
        /// <c>RoomInstance.GetTileAtCoord</c>'s null guard.
        /// </summary>
        [Test]
        public void BlocksMove_NullRoomOrTiles_BlocksNothing()
        {
            Assert.That(
                PropCollisionRule.BlocksMove(null, Overlapping, Overlapping, false, false),
                Is.False, "A null room blocks nothing.");

            var unfilled = new RoomInstance { width = 4, height = 4, tiles = null };
            Assert.That(
                PropCollisionRule.BlocksMove(unfilled, Overlapping, Overlapping, false, false),
                Is.False, "A room with no tile array blocks nothing.");
        }
    }
}
