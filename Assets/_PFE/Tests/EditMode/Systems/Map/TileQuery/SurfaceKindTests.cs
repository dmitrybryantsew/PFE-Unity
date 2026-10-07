using NUnit.Framework;
using UnityEngine;
using PFE.Systems.Map;
using PFE.Systems.Map.TileQuery;
using PFE.Tests.EditMode.Systems.Map.TileCollision;

namespace PFE.Tests.EditMode.Systems.Map.TileQuery
{
    /// <summary>
    /// Pins <see cref="SurfaceKindRule.Of"/> — the one answer to "what <i>surface</i> is this cell?",
    /// the classification every per-consumer predicate reads.
    ///
    /// <para><b>Why this fixture exists.</b> Before this, each consumer re-derived the tile's identity
    /// from <see cref="TileData"/>'s fields inline, so "is this a wall / a catwalk / a ramp / a
    /// ladder?" was answered three different ways in three files. Extracting it to one pure function
    /// of the tile makes it directly assertable, exactly as <see cref="ProjectileOcclusionRule"/>
    /// and <c>ProjectilePhysicsMath</c> were extracted — the same move that turns an untestable
    /// inline condition into a truth table.</para>
    ///
    /// <para><b>The trap this guards.</b> The obvious-looking classification is
    /// <see cref="TileData.IsSolid"/>, which is <c>physicsType &gt;= Wall</c> and therefore lumps
    /// <c>Platform</c> and <c>Stair</c> in with <c>Wall</c> — it answers "does this block
    /// <i>movement</i>", a different question with a different answer per consumer. The whole point
    /// of <see cref="SurfaceKind"/> is that a shelf, a ramp and a ladder are <i>distinct</i> from a
    /// wall, because a projectile passes all three and a prop passes two of them.</para>
    ///
    /// <para><b>It is the AS3 identity, not the port's decode.</b> AS3 separates these by
    /// <c>phis</c> plus the <c>shelf</c>/<c>diagon</c>/<c>stair</c> flags
    /// (<c>loc/Box.as:1260-1275</c>). The port's <see cref="TilePhysicsType"/> does not map one-to-one
    /// onto that — a slope is <c>Air</c>/<c>Stair</c> plus a <c>slopeType</c>, a ladder is
    /// <c>Air</c> plus a <c>stairType</c> — so the classification reads the metadata fields, and the
    /// truth table below is written in AS3's terms.</para>
    /// </summary>
    [TestFixture]
    public sealed class SurfaceKindTests
    {
        // ── The truth table: physicsType alone ──────────────────────────────────────────────

        [TestCase(TilePhysicsType.Wall,     SurfaceKind.Solid,  "phis 1 (and 2) — a wall, the only occluder")]
        [TestCase(TilePhysicsType.Platform, SurfaceKind.Shelf,  "phis 0 + shelf — a one-way catwalk")]
        [TestCase(TilePhysicsType.Air,      SurfaceKind.None,   "phis 0 — empty space, no surface")]
        [TestCase(TilePhysicsType.Stair,    SurfaceKind.Stair,  "phis 3 with no climb metadata — still a stair kind")]
        public void Of_ReadsPhysicsTypeAlone(
            TilePhysicsType physicsType, SurfaceKind expected, string why)
        {
            var tile = new TileData { physicsType = physicsType };

            Assert.That(SurfaceKindRule.Of(tile), Is.EqualTo(expected),
                $"{physicsType} should classify as {expected}: {why}.");
        }

        // ── The truth table: metadata fields the port decodes to Air ─────────────────────────

        /// <summary>
        /// A slope carries <c>slopeType != 0</c>. In the port it decodes to <c>Air</c> (or
        /// <c>Stair</c>) plus that field, so <see cref="TilePhysicsType"/> alone cannot see it — the
        /// classification must read <c>slopeType</c>. Both slope directions classify the same: the
        /// <i>kind</i> is "ramp", the direction is the renderer's business.
        /// </summary>
        [TestCase(1,  "slope up   (AS3 diagon)")]
        [TestCase(-1, "slope down (AS3 diagon)")]
        public void Of_ReadsSlopeType_AsDiagon(int slopeType, string why)
        {
            var tile = new TileData { physicsType = TilePhysicsType.Stair, slopeType = slopeType };

            Assert.That(SurfaceKindRule.Of(tile), Is.EqualTo(SurfaceKind.Diagon),
                $"slopeType = {slopeType} is a walkable ramp: {why}.");
        }

        /// <summary>
        /// A ladder carries <c>stairType != 0</c> with <c>slopeType == 0</c> and decodes to
        /// <c>Air</c>, so again only the metadata field distinguishes it.
        /// </summary>
        [Test]
        public void Of_ReadsClimbableLadder_AsStair()
        {
            var tile = new TileData { physicsType = TilePhysicsType.Air, stairType = 1 };

            Assert.That(SurfaceKindRule.Of(tile), Is.EqualTo(SurfaceKind.Stair),
                "A ladder (stairType != 0, slopeType == 0) is a stair kind: it passes every consumer.");
        }

        /// <summary>
        /// Water is a property, not a surface — a cell can be water <i>and</i> a wall, and on its own
        /// it is nothing to collide with.
        /// </summary>
        [Test]
        public void Of_WaterAlone_IsNotASurface()
        {
            var tile = new TileData { physicsType = TilePhysicsType.Air, hasWater = true };

            Assert.That(SurfaceKindRule.Of(tile), Is.EqualTo(SurfaceKind.None),
                "Water alone is not a surface; a cell is only a surface by its physics form.");
        }

        /// <summary>
        /// A collider carrying no <c>TileData</c> must be <see cref="SurfaceKind.None"/>, never a
        /// surface. Assuming a surface where the semantics are unknown is the louder failure — it
        /// would produce phantom collisions — and it mirrors
        /// <see cref="ProjectileOcclusionRule.BlocksProjectile"/>'s null contract.
        /// </summary>
        [Test]
        public void Of_NullTile_IsNone()
        {
            Assert.That(SurfaceKindRule.Of(null), Is.EqualTo(SurfaceKind.None),
                "A null tile is an unbuilt cell or a collider with no TileData. It is not a surface.");
        }

        // ── The wrong predicate, named ───────────────────────────────────────────────────────

        /// <summary>
        /// Names the wrong classification explicitly, so a future "simplification" to
        /// <c>tile.IsSolid() ? Solid : None</c> fails here with its reason attached rather than as a
        /// gameplay regression someone has to bisect. Each of these is a movement surface
        /// (<see cref="TileData.IsSolid"/> is true) but is <i>not</i> a wall
        /// (<see cref="SurfaceKind.Solid"/> is not what it classifies as).
        /// </summary>
        [Test]
        public void TileDataIsSolidIsTheWrongPredicate_ForNonWallMovementSurfaces()
        {
            var shelf = new TileData { physicsType = TilePhysicsType.Platform };
            var ramp = new TileData { physicsType = TilePhysicsType.Stair, slopeType = 1 };
            var ladder = new TileData { physicsType = TilePhysicsType.Air, stairType = 1 };

            Assert.That(shelf.IsSolid(), Is.True, "A shelf blocks movement, so IsSolid() is true.");
            Assert.That(SurfaceKindRule.Of(shelf), Is.EqualTo(SurfaceKind.Shelf),
                "A shelf is a Shelf, not a Solid. If this fails the classification was widened to " +
                "TileData.IsSolid() and a projectile will stop in mid-air on every catwalk.");

            Assert.That(ramp.IsSolid(), Is.True, "A ramp is a movement surface, so IsSolid() is true.");
            Assert.That(SurfaceKindRule.Of(ramp), Is.EqualTo(SurfaceKind.Diagon),
                "A ramp is a Diagon, not a Solid — a prop and a projectile both pass it.");

            Assert.That(SurfaceKindRule.Of(ladder), Is.EqualTo(SurfaceKind.Stair),
                "A ladder is a Stair, not a Solid — a prop and a projectile both pass it.");
        }

        // ── The two representations must agree ──────────────────────────────────────────────

        /// <summary>
        /// <c>RoomChainGeometry.IsSolidAt</c> decides from <c>ClassifySurface(...) == Solid</c>; the
        /// chain mirror's other consumers decide from <c>Classify(...) &amp; TileQueryFlags.Solid</c>.
        /// Those are two representations of "is this a wall", and if they ever disagree the Stage C
        /// rollback flag would change <i>gameplay</i> rather than only the implementation — which is
        /// exactly why the rewrite from flags to <see cref="SurfaceKind"/> must be a no-op.
        ///
        /// <para>Every tile kind in the ASCII vocabulary is covered in one room, so a kind added to
        /// the vocabulary without a matching rule shows up as a mismatch rather than as silence.</para>
        /// </summary>
        [Test]
        public void ClassifySurface_AgreesWithTheSolidFlag_ForEveryTileKindInTheVocabulary()
        {
            //       0         1         2
            //       0123456789012345678901234
            string[] rows =
            {
                "#########################",   // Wall
                "#.......=...............#",   // Platform (shelf)
                "#.......H...............#",   // Ladder
                "#......./...............#",   // Slope up
                "#.......\\...............#",   // Slope down
                "#.......~...............#",   // Water
                "#########################"
            };

            RoomInstance room = SyntheticRoomBuilder.BuildFromAscii(rows);
            var query = new UnifiedTileQueryService(room);

            int walls = 0;
            int nonWalls = 0;

            for (int x = 0; x < room.width; x++)
            {
                for (int y = 0; y < room.height; y++)
                {
                    TileData tile = room.tiles[x, y];

                    bool viaKind = query.ClassifySurface(new Vector2Int(x, y)) == SurfaceKind.Solid;
                    bool viaFlag = (query.Classify(new Vector2Int(x, y)) & TileQueryFlags.Solid) != 0;

                    if (viaKind) walls++; else nonWalls++;

                    Assert.That(viaKind, Is.EqualTo(viaFlag),
                        $"Tile ({x},{y}) is {tile.physicsType}: ClassifySurface says {viaKind} and the " +
                        $"Solid flag says {viaFlag}. The two must agree, or the chain mirror's rewrite " +
                        "from flags to SurfaceKind changed gameplay rather than the implementation.");
                }
            }

            // Both directions must be exercised, or the agreement above is vacuous — a room of
            // nothing but air would pass it while proving nothing about walls.
            Assert.That(walls, Is.GreaterThan(0),
                "The room must contain at least one wall, or the comparison never tests the positive case.");
            Assert.That(nonWalls, Is.GreaterThan(0),
                "The room must contain at least one non-wall, or the comparison never tests the negative case.");
        }

        /// <summary>
        /// <c>ClassifySurface</c> must survive a room with no tile array — the same null contract
        /// <c>Classify</c> has, because a <c>RoomInstance</c> exists before it is filled in.
        /// </summary>
        [Test]
        public void ClassifySurface_NullRoomOrTiles_IsNone()
        {
            Assert.That(new UnifiedTileQueryService(null).ClassifySurface(new Vector2Int(0, 0)),
                Is.EqualTo(SurfaceKind.None), "A null room has no surfaces.");

            var unfilled = new RoomInstance { width = 4, height = 4, tiles = null };
            Assert.That(new UnifiedTileQueryService(unfilled).ClassifySurface(new Vector2Int(1, 1)),
                Is.EqualTo(SurfaceKind.None), "A room with no tile array has no surfaces.");
        }
    }
}
