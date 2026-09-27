using NUnit.Framework;
using UnityEngine;
using UnityEngine.LowLevelPhysics2D;
using PFE.Systems.Map;
using PFE.Systems.Map.TileQuery;
using PFE.Systems.Physics;
using PFE.Tests.EditMode.Systems.Map.TileCollision;

namespace PFE.Tests.EditMode.Systems.Physics
{
    /// <summary>
    /// Stage B0: verify the production <see cref="RoomChainGeometry"/> builds valid chains from
    /// <see cref="ITileQueryService"/> and that the resulting world has the expected body/shape
    /// counts.
    ///
    /// <para>These are geometry-build tests, not physics-behaviour tests — the point is that the
    /// chain builder stays a pure function of the tile query service and does not drift.</para>
    ///
    /// <para><b>World hygiene.</b> These tests create raw <see cref="PhysicsWorld"/> instances
    /// rather than going through the spike's <c>Llp2d</c> helper, because the point is to exercise
    /// the production type directly. That means each test destroys its own world in a
    /// <c>finally</c> block: <c>PhysicsConstants.MaxWorlds</c> caps concurrency, so a leak would
    /// eventually fail for a reason unrelated to anything under test.</para>
    /// </summary>
    [TestFixture]
    public class RoomChainGeometryTests
    {
        [Test]
        public void Build_EmptyRoom_CreatesNoChains()
        {
            PhysicsWorld world = CreateWorld();
            try
            {
                RoomInstance room = BuildEmptyRoom(4, 4);
                ITileQueryService query = new UnifiedTileQueryService(room);

                var geometry = new RoomChainGeometry(query, world);
                geometry.Build();

                Assert.AreEqual(0, world.counters.shapeCount,
                    "An empty room must produce zero chain shapes.");
            }
            finally
            {
                world.Destroy(0);
            }
        }

        [Test]
        public void Build_SingleWallTile_CreatesTopAndSideChains()
        {
            PhysicsWorld world = CreateWorld();
            try
            {
                RoomInstance room = BuildEmptyRoom(4, 4);
                room.tiles[1, 1] = MakeTile(1, 1, TilePhysicsType.Wall);
                ITileQueryService query = new UnifiedTileQueryService(room);

                var geometry = new RoomChainGeometry(query, world);
                geometry.Build();

                // One wall tile exposed on all four sides in an empty room:
                // top surface (horizontal), bottom surface (horizontal), left face, right face.
                // That's 4 chains, but the bottom surface is also exposed.
                Assert.Greater(world.counters.shapeCount, 0,
                    "A single wall tile must produce at least one chain shape.");
            }
            finally
            {
                world.Destroy(0);
            }
        }

        [Test]
        public void Build_SlopeTile_ProducesNoChain_BecauseASlopeDoesNotOcclude()
        {
            PhysicsWorld world = CreateWorld();
            try
            {
                RoomInstance room = BuildEmptyRoom(4, 4);
                room.tiles[1, 1] = MakeSlopeTile(1, 1, slopeType: 1); // /
                ITileQueryService query = new UnifiedTileQueryService(room);

                var geometry = new RoomChainGeometry(query, world);
                geometry.Build();

                // AS3's bullet test is `_loc3_.phis == 1 || _loc3_.phis == 2` and nothing else
                // (weapon/Bullet.as:476): `diagon`, `shelf` and `stair` are never consulted, and a
                // diagon form carries phis = 0. A slope is a walkable ramp, not an occluder, so it
                // contributes NO surface.
                //
                // This test used to assert the opposite — it pinned Stage B's deferral, "emit the slope
                // tile's tile-top surface, not a diagonal, until orientation lands in the seam". That
                // deferral is gone, and not because orientation arrived: a flat ledge at the cell top is
                // not a partial answer but a wrong one, and it stopped bullets in mid-air above every
                // slope. Slope diagonals are still not emitted, and now never will be by this builder,
                // because nothing should collide with a slope.
                Assert.AreEqual(0, world.counters.shapeCount,
                    "A slope tile must produce no chain shape. A non-zero count means the builder " +
                    "treats TileQueryFlags.Slope as an occluder, and bullets will stop in mid-air " +
                    "above every slope.");
            }
            finally
            {
                world.Destroy(0);
            }
        }

        [Test]
        public void Build_SolidTileBesideASlope_StillEmitsTheFacingSurface()
        {
            PhysicsWorld world = CreateWorld();
            try
            {
                RoomInstance room = BuildEmptyRoom(4, 4);
                room.tiles[0, 1] = MakeTile(0, 1, TilePhysicsType.Wall);
                room.tiles[1, 1] = MakeSlopeTile(1, 1, slopeType: 1);
                ITileQueryService query = new UnifiedTileQueryService(room);

                var geometry = new RoomChainGeometry(query, world);
                geometry.Build();

                // The complement of the test above, and the reason that one cannot be satisfied by a
                // builder that simply emits nothing: dropping non-occluders from the geometry must not
                // drop the SOLID tiles beside them. The wall at (0,1) has a slope on its right, so the
                // slope must not count as occluding it — its right face belongs at local x = 1 tile =
                // 0.4 units, spanning y 0.4..0.8 units. If exposure used a wider predicate than the
                // geometry, that face would be missing and a bullet could enter the wall from the side.
                Assert.IsTrue(OverlapsChain(world, new Vector2(0.4f, 0.6f)),
                    "The wall's right face must be emitted: a slope is not an occluder, so it cannot " +
                    "hide the face of the solid tile next to it.");
            }
            finally
            {
                world.Destroy(0);
            }
        }

        [Test]
        public void Build_MixedGeometryFromAscii_MatchesExpectedChainCount()
        {
            PhysicsWorld world = CreateWorld();
            try
            {
                RoomInstance room = SyntheticRoomBuilder.BuildFromAscii(MixedRoom);
                ITileQueryService query = new UnifiedTileQueryService(room);

                var geometry = new RoomChainGeometry(query, world);
                geometry.Build();

                int shapes = world.counters.shapeCount;

                // One static body + however many chain segments.
                Assert.AreEqual(1, world.counters.bodyCount,
                    "Exactly one static body should own all chains.");
                Assert.GreaterOrEqual(shapes, 4,
                    "Border walls alone should produce at least 4 chain shapes.");
            }
            finally
            {
                world.Destroy(0);
            }

            // The room's `=` row, `H` column and two `/` tiles are catwalk, ladder and slope forms —
            // all phis = 0, so none is an occluder and none contributes geometry. Blanking them to air
            // must therefore leave the shape count identical, which pins that without hardcoding a
            // number and is what the test's name promises.
            int mixedShapes      = ShapesFor(SyntheticRoomBuilder.BuildFromAscii(MixedRoom));
            int borderOnlyShapes = ShapesFor(SyntheticRoomBuilder.BuildFromAscii(BorderOnlyRoom));

            Assert.AreEqual(borderOnlyShapes, mixedShapes,
                "The catwalk, ladder and slope tiles must contribute no chains: the room's geometry " +
                "is its border walls alone. A difference means one of those flags is being treated " +
                "as an occluder.");
        }

        [Test]
        public void Build_Twice_DoesNotAccumulateChains()
        {
            PhysicsWorld world = CreateWorld();
            try
            {
                RoomInstance room = BuildEmptyRoom(8, 6);
                for (int x = 1; x < 7; x++)
                    room.tiles[x, 1] = MakeTile(x, 1, TilePhysicsType.Wall);
                ITileQueryService query = new UnifiedTileQueryService(room);

                var geometry = new RoomChainGeometry(query, world);
                geometry.Build();
                int afterFirst = world.counters.shapeCount;
                int chainsFirst = geometry.ChainCount;

                geometry.Build();
                int afterSecond = world.counters.shapeCount;

                Assert.Greater(afterFirst, 0, "The first build must produce shapes.");
                Assert.AreEqual(afterFirst, afterSecond,
                    "A rebuild must replace chains, not accumulate them. Chain segments cannot be " +
                    "destroyed through PhysicsShape.Destroy, so this is the test that proves " +
                    "PhysicsChain.Destroy is actually being used.");
                Assert.AreEqual(chainsFirst, geometry.ChainCount,
                    "The reported chain count must describe the current build, not the total ever built.");
            }
            finally
            {
                world.Destroy(0);
            }
        }

        [Test]
        public void Destroy_ReleasesChainsAndBody()
        {
            PhysicsWorld world = CreateWorld();
            try
            {
                RoomInstance room = BuildEmptyRoom(4, 4);
                room.tiles[1, 1] = MakeTile(1, 1, TilePhysicsType.Wall);
                ITileQueryService query = new UnifiedTileQueryService(room);

                var geometry = new RoomChainGeometry(query, world);
                geometry.Build();

                Assert.Greater(world.counters.shapeCount, 0, "Precondition: something was built.");
                Assert.AreEqual(1, world.counters.bodyCount, "Precondition: one static body.");

                geometry.Destroy();

                Assert.AreEqual(0, world.counters.shapeCount,
                    "Destroy must release every chain segment.");
                Assert.AreEqual(0, world.counters.bodyCount,
                    "Destroy must release the static body too.");

                // Idempotence: a second Destroy on an already-destroyed geometry must not throw.
                Assert.DoesNotThrow(() => geometry.Destroy());
            }
            finally
            {
                world.Destroy(0);
            }
        }

        /// <summary>
        /// A short run's <b>ends</b> must be collidable, not just its middle.
        ///
        /// <para>Box2D gives an open chain no collision on its first and final edge (<i>"An open
        /// chain has no collision on the first and final edge"</i>, shipped XML docs for
        /// <c>PhysicsChain</c>). The builder's subdivision minimum is four vertices, so a naive
        /// single-tile run keeps exactly one live edge — the middle third of the tile — and a
        /// projectile would pass through the outer two thirds of every short surface. This test pins
        /// the lead-in/lead-out that makes the whole surface live.</para>
        /// </summary>
        [Test]
        public void Build_ShortRun_WholeSurfaceIsCollidableIncludingItsEnds()
        {
            PhysicsWorld world = CreateWorld();
            try
            {
                RoomInstance room = BuildEmptyRoom(4, 4);
                room.tiles[1, 1] = MakeTile(1, 1, TilePhysicsType.Wall);
                ITileQueryService query = new UnifiedTileQueryService(room);

                var geometry = new RoomChainGeometry(query, world);
                geometry.Build();

                // The tile's top surface runs along y = 2 tiles = 0.8 units, x from 1 tile (0.4) to
                // 2 tiles (0.8). Probe the last third, which the un-extended build left inert: the
                // subdivision puts the live edge at x in [0.5333, 0.6667], so x = 0.75 is inside the
                // surface but outside the only live edge.
                Assert.IsTrue(OverlapsChain(world, new Vector2(0.75f, 0.8f)),
                    "A probe on the last third of a one-tile top surface must overlap the chain. If " +
                    "this fails, the run's end edges are the ones Box2D discards and the lead-out is " +
                    "missing — a projectile would pass through the end of every surface.");

                // The middle must stay live too, so the assertion above cannot be satisfied by a
                // chain that simply covers everything.
                Assert.IsTrue(OverlapsChain(world, new Vector2(0.6f, 0.8f)),
                    "A probe on the middle of the surface must overlap the chain.");

                // And the chain must still be a one-dimensional surface, not a filled box: a probe
                // well clear of it must miss.
                Assert.IsFalse(OverlapsChain(world, new Vector2(0.6f, 1.6f)),
                    "A probe clear of the surface must not overlap it, so the chain is still a " +
                    "surface rather than a volume.");
            }
            finally
            {
                world.Destroy(0);
            }
        }

        /// <summary>
        /// Chains must be emitted in <b>world</b> space, not room-local space.
        ///
        /// <para><see cref="ITileQueryService"/> is a world-pixel seam, so a builder that converts
        /// room-local tile indices straight to units silently puts the mirror of every room except the
        /// one at land position (0,0) in the wrong place — displaced by one room width per land step.
        /// The debug overlay makes that obvious in play, but no test here could see it, because every
        /// other fixture in this file builds a room with the default <c>landPosition</c> of (0,0) and
        /// a zero origin makes the two coordinate spaces identical. This one does not.</para>
        /// </summary>
        [Test]
        public void Build_RoomAwayFromOrigin_ChainsAreInWorldSpace()
        {
            PhysicsWorld world = CreateWorld();
            try
            {
                RoomInstance room = BuildEmptyRoom(4, 4);
                // One land step right. The origin formula uses the canonical room size
                // (WorldConstants.ROOM_WIDTH), not this 4x4 fixture's width, so the origin is
                // 1 * 48 tiles * 40 px = 1920 px = 19.2 units.
                room.landPosition = new Vector3Int(1, 0, 0);
                room.tiles[1, 1] = MakeTile(1, 1, TilePhysicsType.Wall);

                ITileQueryService query = new UnifiedTileQueryService(room);
                var geometry = new RoomChainGeometry(query, world);
                geometry.Build();

                // Tile (1,1)'s top surface is local x in [1,2] tiles = [40,80] px at local y = 2
                // tiles = 80 px. Shifted by the origin that is world x in [19.6, 20.0] units at
                // y = 0.8 units, so a probe at 19.8 units sits mid-surface.
                Assert.IsTrue(OverlapsChain(world, new Vector2(19.8f, 0.8f)),
                    "The chain must be built at the room's WORLD position: tile (1,1)'s top surface " +
                    "belongs at world x 19.6..20.0 units for a room at land position (1,0). If this " +
                    "fails, the builder ignored ITileQueryService.OriginPixel and the whole mirror " +
                    "is sitting at the world origin instead of on the room.");

                // The un-offset position must stay empty, so the assertion above cannot be satisfied
                // by geometry emitted twice.
                Assert.IsFalse(OverlapsChain(world, new Vector2(0.6f, 0.8f)),
                    "Nothing may be emitted at the room-local position. That is exactly where a " +
                    "builder which ignored the room origin would put it, and it lies inside a " +
                    "different room.");
            }
            finally
            {
                world.Destroy(0);
            }
        }

        /// <summary>
        /// The same query the Stage B1 dual-run harness uses, so this geometry test and the
        /// projectile comparison cannot drift apart: an 8x8 px box — the AS3 bullet size class —
        /// tested with the exact-geometry polygon query rather than the AABB one, because a chain
        /// segment is zero-thickness and its AABB is not its shape.
        /// </summary>
        private static bool OverlapsChain(PhysicsWorld world, Vector2 centreUnits)
        {
            PolygonGeometry box = PolygonGeometry.CreateBox(
                new Vector2(0.08f, 0.08f),
                0f,
                new PhysicsTransform(centreUnits, PhysicsRotate.identity),
                false);

            return world.TestOverlapGeometry(box, new PhysicsQuery.QueryFilter());
        }

        // ── Fixtures ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A room whose only solids are its border walls: the <c>=</c> row is a catwalk, the <c>H</c>
        /// column a ladder and the two <c>/</c> tiles slopes. Kept as a field so
        /// <c>Build_MixedGeometryFromAscii_MatchesExpectedChainCount</c> can build the same room twice.
        /// </summary>
        private static readonly string[] MixedRoom =
        {
            "################",
            "#..............#",
            "#....====......#",
            "#....H.........#",
            "#....H..../....#",
            "#....H.../.....#",
            "################"
        };

        /// <summary>
        /// <see cref="MixedRoom"/> with the catwalk, ladder and slope tiles blanked to air — the
        /// control the test above compares against.
        /// </summary>
        private static readonly string[] BorderOnlyRoom =
        {
            "################",
            "#..............#",
            "#..............#",
            "#..............#",
            "#..............#",
            "#..............#",
            "################"
        };

        /// <summary>
        /// Shape count for a room, in its own world. Lets two rooms' geometry be compared without
        /// hardcoding a count, and without two <see cref="RoomChainGeometry"/> instances sharing one
        /// world.
        /// </summary>
        private static int ShapesFor(RoomInstance room)
        {
            PhysicsWorld world = CreateWorld();
            try
            {
                var geometry = new RoomChainGeometry(new UnifiedTileQueryService(room), world);
                geometry.Build();
                return world.counters.shapeCount;
            }
            finally
            {
                world.Destroy(0);
            }
        }

        private static PhysicsWorld CreateWorld()
        {
            PhysicsWorldDefinition def = PhysicsWorldDefinition.defaultDefinition;
            def.simulateType = PhysicsWorld.SimulationType.Script;
            def.simulationWorkers = 1;
            def.gravity = Vector2.zero;
            return PhysicsWorld.Create(def);
        }

        private static RoomInstance BuildEmptyRoom(int width, int height)
        {
            var room = new RoomInstance
            {
                width = width,
                height = height,
                tiles = new TileData[width, height]
            };
            for (int x = 0; x < width; x++)
                for (int y = 0; y < height; y++)
                    room.tiles[x, y] = MakeTile(x, y, TilePhysicsType.Air);
            return room;
        }

        private static TileData MakeTile(int x, int y, TilePhysicsType type)
        {
            return new TileData
            {
                gridPosition = new Vector2Int(x, y),
                physicsType = type
            };
        }

        private static TileData MakeSlopeTile(int x, int y, int slopeType)
        {
            return new TileData
            {
                gridPosition = new Vector2Int(x, y),
                physicsType = TilePhysicsType.Stair,
                slopeType = slopeType
            };
        }
    }
}
