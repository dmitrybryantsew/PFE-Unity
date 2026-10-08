using System;
using NUnit.Framework;
using PFE.Systems.Map;
using PFE.Tests.EditMode.Systems.Map.TileCollision;
using UnityEngine;

namespace PFE.Tests.Editor.Map.TileQuery
{
    /// <summary>
    /// Pins <c>TileData.IsSolidOrShelf</c> and <c>RoomInstance.IsSolidOrShelfAtRoomLocalPixels</c> — the
    /// port's <c>_loc1_.phis == 1 || _loc1_.shelf</c> (<c>UnitZombie.as:794</c>/<c>:813</c>), the "is
    /// there something 80 px ahead I could hop <b>onto</b>?" test behind the zombie's crate hop.
    ///
    /// <para><b>Why this is its own fixture.</b> The mapping from the oracle's two terms to this port's
    /// two enum members is the subtle part, and it is one line that a plausible "tidy-up" would get wrong:
    /// <c>TileData.IsSolid()</c> is <c>&gt;= Wall</c>, which also admits <c>Stair</c> — and a slope is
    /// <c>phis == 0</c> in the oracle, so it is not something to hop onto. Using <c>IsSolid()</c> here
    /// would make a zombie hop at every ramp. The enum sweep below is the assertion that forces that
    /// decision to be made deliberately rather than by default.</para>
    ///
    /// <para><b>Offline.</b> <c>TileData</c>, <c>RoomInstance</c> and <c>SyntheticRoomBuilder</c> are plain
    /// classes, so no <c>GameObject</c>, scene or editor is needed.</para>
    /// </summary>
    [TestFixture]
    public sealed class SolidOrShelfPredicateTests
    {
        private static TileData Tile(TilePhysicsType type, int slopeType = 0, int stairType = 0)
            => new TileData
            {
                gridPosition = Vector2Int.zero,
                physicsType = type,
                slopeType = slopeType,
                stairType = stairType
            };

        // ── The two members that ARE hoppable-onto ───────────────────────────────────────────

        /// <summary>
        /// A wall column (<c>phis == 1</c>) and a shelf (<c>shelf</c>) — the oracle's two terms.
        /// </summary>
        [Test]
        public void IsSolidOrShelf_WallAndPlatform_AreHoppableOnto()
        {
            Assert.IsTrue(Tile(TilePhysicsType.Wall).IsSolidOrShelf(),
                "`phis == 1` — TileDecoder.MapPhysicsType maps every non-zero phis to Wall, so this is the " +
                "`phis == 1` half.");

            Assert.IsTrue(Tile(TilePhysicsType.Platform).IsSolidOrShelf(),
                "`shelf` — Platform is written only for a shelf form on a phis == 0 base, so this is the " +
                "`_loc1_.shelf` half.");
        }

        /// <summary>Air is nothing to hop onto.</summary>
        [Test]
        public void IsSolidOrShelf_Air_IsNot()
        {
            Assert.IsFalse(Tile(TilePhysicsType.Air).IsSolidOrShelf(),
                "an air tile is what the zombie turns around at, not what it hops onto.");
        }

        /// <summary>
        /// A ladder is not something to hop onto — it is <c>phis == 0</c> in the oracle.
        /// </summary>
        [Test]
        public void IsSolidOrShelf_Ladder_IsNot()
        {
            Assert.IsFalse(Tile(TilePhysicsType.Air, stairType: 1).IsSolidOrShelf(),
                "a ladder is Air with climbable metadata and `phis == 0`, so it is not in the oracle's two " +
                "terms. (This port models it as Air + stairType rather than as a Stair, so the distinction " +
                "here is about the metadata, not the enum member.)");
        }

        /// <summary>
        /// A slope is <b>not</b> hoppable-onto — even though <c>IsSolid()</c> says it is. This is the trap
        /// the predicate exists to avoid, stated as the pair of answers it must give.
        /// </summary>
        [Test]
        public void IsSolidOrShelf_Slope_IsNot_EvenThoughIsSolidSaysYes()
        {
            TileData slope = Tile(TilePhysicsType.Stair, slopeType: 1);

            Assert.IsFalse(slope.IsSolidOrShelf(),
                "UnitZombie.as:794 tests `phis == 1 || shelf`, and a diagonal is `phis == 0` — so a slope " +
                "is not something to hop onto. A zombie walks UP a ramp; it does not hop onto it.");

            Assert.IsTrue(slope.IsSolid(),
                "...but TileData.IsSolid() is `>= Wall`, and Stair sits above Wall, so IsSolid() says yes. " +
                "That is precisely why the predicate is written out rather than reusing IsSolid(): using " +
                "IsSolid() here would make every zombie hop at every ramp.");

            Assert.IsFalse(Tile(TilePhysicsType.Stair, slopeType: -1).IsSolidOrShelf(),
                "the other slope direction reads the same way — the predicate does not care which way it " +
                "faces, only that it is a slope.");
        }

        // ── The self-policing sweep over the enum ────────────────────────────────────────────

        /// <summary>
        /// The predicate is <b>exactly</b> <c>Wall || Platform</c>, over every member of the enum.
        /// </summary>
        /// <remarks>
        /// A hand-written list of cases goes stale silently when the enum grows; an enumeration of the enum
        /// itself cannot. If a new <c>TilePhysicsType</c> is added, this test fails until someone decides
        /// whether it is hoppable-onto — which is the decision the oracle's <c>phis == 1 || shelf</c>
        /// makes explicitly and that a defaulted comparison would make by accident.
        /// </remarks>
        [Test]
        public void IsSolidOrShelf_IsExactlyWallOrPlatform_OverEveryEnumMember()
        {
            int checkedMembers = 0;

            foreach (TilePhysicsType type in Enum.GetValues(typeof(TilePhysicsType)))
            {
                bool expected = type == TilePhysicsType.Wall || type == TilePhysicsType.Platform;

                Assert.That(Tile(type).IsSolidOrShelf(), Is.EqualTo(expected),
                    $"TilePhysicsType.{type}: only Wall (`phis == 1`) and Platform (`shelf`) are the " +
                    "oracle's two terms. A new member needs a deliberate decision here, not a silent " +
                    "default.");

                checkedMembers++;
            }

            Assert.That(checkedMembers, Is.EqualTo(Enum.GetValues(typeof(TilePhysicsType)).Length),
                "every member must have been visited, or the sweep above is a claim rather than a proof.");
            Assert.That(checkedMembers, Is.GreaterThanOrEqualTo(4),
                "the enum must actually contain Air, Wall, Platform and Stair — a sweep of a one-member " +
                "enum would pass vacuously.");
        }

        // ── The room-level seam the zombie actually calls ────────────────────────────────────

        /// <summary>
        /// A mixed room: a border, a hole in the floor, a slope, and a catwalk.
        /// </summary>
        private static RoomInstance MixedRoom()
            => SyntheticRoomBuilder.BuildFromAscii(new[]
            {
                "################", // y = 4
                "#..............#", // y = 3
                "#...====.......#", // y = 2 — platform at tiles 4..7
                "#..../.........#", // y = 1 — slope at tile 5
                "#####.##########"  // y = 0 — wall 0..4, air at 5, wall 6..15
            });

        /// <summary>
        /// The room-level method answers the tile predicate for the tile the point falls in, and it is the
        /// one the zombie's crate probe calls.
        /// </summary>
        [Test]
        public void RoomIsSolidOrShelfAt_ReadsTheTileThePointFallsIn()
        {
            RoomInstance room = MixedRoom();

            // Points are room-local pixels; the room is at land (0, 0), so they are also world pixels.
            (string label, Vector2 point, bool expected)[] cases =
            {
                ("floor wall",          new Vector2(180f, 20f),  true),
                ("the hole in the floor", new Vector2(220f, 20f), false),
                ("the slope",           new Vector2(220f, 60f),  false),
                ("air beside the slope", new Vector2(180f, 60f), false),
                ("the catwalk",         new Vector2(220f, 100f), true),
                ("the catwalk's left end", new Vector2(180f, 100f), true),
                ("the catwalk's right end", new Vector2(300f, 100f), true),
                ("air past the catwalk", new Vector2(340f, 100f), false),
                ("the border wall",     new Vector2(20f, 180f),  true),
            };

            foreach ((string label, Vector2 point, bool expected) in cases)
            {
                Assert.That(room.IsSolidOrShelfAtRoomLocalPixels(point), Is.EqualTo(expected),
                    $"'{label}' at {point} must be {expected}. The point is floored to a tile, so it need " +
                    "not be a centre or a corner.");
            }
        }

        /// <summary>
        /// Out of bounds is <c>false</c>, and it agrees with <c>GetTileAtCoord</c> returning <c>null</c> —
        /// which is AS3's answer, not a fallback.
        /// </summary>
        /// <remarks>
        /// <c>Location.getAbsTile</c> returns the sentinel <c>otstoy = new Tile(-1,-1)</c>
        /// (<c>Location.as:269</c>) for a point outside the room, and <c>Tile</c>'s fields are declared
        /// <c>phis:int = 0</c> / <c>shelf:Boolean = false</c> (<c>Tile.as:18</c>, <c>:20</c>) — so the
        /// oracle reads "nothing there" too, and the zombie turns around rather than hopping off the map.
        /// </remarks>
        [Test]
        public void RoomIsSolidOrShelfAt_OutOfBoundsIsFalse_AndAgreesWithGetTileAtCoord()
        {
            RoomInstance room = MixedRoom();

            Vector2[] outside =
            {
                new Vector2(-10f, 20f),    // left of the room
                new Vector2(10000f, 20f),  // right of it
                new Vector2(220f, -10f),   // below the floor row
                new Vector2(220f, 10000f), // above the ceiling row
            };

            foreach (Vector2 point in outside)
            {
                Assert.IsFalse(room.IsSolidOrShelfAtRoomLocalPixels(point),
                    $"{point} is outside the room, so there is nothing to hop onto — the oracle's sentinel " +
                    "tile reads the same way.");

                TileData tile = room.GetTileAt(point);
                Assert.IsNull(tile,
                    $"{point}: GetTileAt must answer null for the same point — the two are documented to " +
                    "agree by construction, and a divergence would mean the sentinel is being fabricated " +
                    "somewhere.");
            }
        }
    }
}
