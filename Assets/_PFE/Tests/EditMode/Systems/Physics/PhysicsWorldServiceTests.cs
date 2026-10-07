using NUnit.Framework;
using UnityEngine;
using PFE.Core;
using PFE.Systems.Map;
using PFE.Systems.Physics;
using PFE.Tests.EditMode.Systems.Map.TileCollision;

namespace PFE.Tests.EditMode.Systems.Physics
{
    /// <summary>
    /// Stage B0: the chain mirror must not go stale when tiles are destroyed.
    ///
    /// <para>Why this matters before Stage B1 exists: the dual-run compares the classic tile query
    /// against the chain-backed one. If the chains silently describe tiles that were destroyed
    /// several ticks ago, the dual-run reports divergences caused by stale geometry rather than by
    /// real semantic differences — contaminating the one piece of evidence it exists to produce.</para>
    ///
    /// <para>Each test disposes its <see cref="PhysicsWorldService"/>; <c>PhysicsConstants.MaxWorlds</c>
    /// caps concurrent worlds, so a leak would fail later for an unrelated reason.</para>
    /// </summary>
    [TestFixture]
    public class PhysicsWorldServiceTests
    {
        // 16x5. Row 0 is the TOP row (SyntheticRoomBuilder maps row r to y = height-1-r), so the
        // four-wide run below sits at y = 2, columns 5..8.
        private static readonly string[] RoomWithShelf =
        {
            "################",
            "#..............#",
            "#....####......#",
            "#..............#",
            "################"
        };

        /// <summary>A tile in the middle of the four-wide run.</summary>
        private static readonly Vector2Int DestroyedTile = new Vector2Int(6, 2);

        [Test]
        public void TilesMutated_RebuildsChainGeometry()
        {
            using var service = new PhysicsWorldService();
            RoomInstance room = SyntheticRoomBuilder.BuildFromAscii(RoomWithShelf);

            service.BuildRoomGeometry(room);
            int before = service.World.counters.shapeCount;
            Assert.Greater(before, 0, "Precondition: the room produced chain shapes.");

            DestroyTileAndAnnounce(room);

            int after = service.World.counters.shapeCount;

            // Splitting one run in two cannot leave the count unchanged. Modelled against the
            // production run scanner: 12 chains / 90 shapes before, 16 chains / 100 shapes after —
            // each horizontal direction turns one run into two, and each vertical direction gains a
            // face at the gap.
            Assert.AreNotEqual(before, after,
                "A destroyed tile must rebuild the room's chains, not leave the mirror stale.");
        }

        /// <summary>
        /// The decisive staleness check: a mirror rebuilt from the notification must describe
        /// exactly the same geometry as a mirror built from scratch against the same final grid.
        ///
        /// <para>The count-change test above cannot catch a mirror that is stale by a compensating
        /// amount, and asserting on the tile grid would only re-test <c>TileData.Destroy</c>. This
        /// compares the incremental path against the from-scratch path, so it fails for the one
        /// reason that matters: chains that outlived the tiles they were built from.</para>
        /// </summary>
        [Test]
        public void TilesMutated_RebuildMatchesAFreshBuildOfTheSameGrid()
        {
            RoomInstance room = SyntheticRoomBuilder.BuildFromAscii(RoomWithShelf);

            // Incremental path: build, then destroy a tile and let the notification rebuild.
            int incremental;
            using (var service = new PhysicsWorldService())
            {
                service.BuildRoomGeometry(room);
                DestroyTileAndAnnounce(room);
                incremental = service.World.counters.shapeCount;
            }

            // Reference path: a brand-new mirror of the very same, already-mutated grid. The worlds
            // are released between the blocks because PhysicsConstants.MaxWorlds caps concurrency.
            int reference;
            using (var service = new PhysicsWorldService())
            {
                service.BuildRoomGeometry(room);
                reference = service.World.counters.shapeCount;
            }

            Assert.Greater(reference, 0, "Precondition: the mutated grid still produces geometry.");
            Assert.AreEqual(reference, incremental,
                "A mirror rebuilt from a tile-mutation notification must match one built from " +
                "scratch against the same grid; a mismatch means stale chains survived the rebuild.");
        }

        [Test]
        public void DestroyRoomGeometry_UnsubscribesFromRoom()
        {
            using var service = new PhysicsWorldService();
            RoomInstance room = SyntheticRoomBuilder.BuildFromAscii(RoomWithShelf);

            service.BuildRoomGeometry(room);
            service.DestroyRoomGeometry(room);

            Assert.AreEqual(0, service.World.counters.bodyCount,
                "Precondition: the static body was released.");

            // A mutation after release must not resurrect geometry. If the handler were still
            // subscribed, RebuildRegion would rebuild the room and the counts would go back up.
            DestroyTileAndAnnounce(room);

            Assert.AreEqual(0, service.World.counters.bodyCount,
                "DestroyRoomGeometry must unsubscribe, or every released room leaks a handler.");
            Assert.AreEqual(0, service.World.counters.shapeCount,
                "No geometry may be rebuilt after the room was released.");
        }

        [Test]
        public void NotifyTilesMutated_WithNoListeners_DoesNotThrow()
        {
            RoomInstance room = SyntheticRoomBuilder.BuildFromAscii(RoomWithShelf);

            // Streaming can announce a mutation on a room nobody has subscribed to yet.
            Assert.DoesNotThrow(() => room.NotifyTilesMutated(new RectInt(0, 0, 1, 1)));
        }

        [Test]
        public void NotifyTilesMutated_DeliversRegionUnchanged()
        {
            RoomInstance room = SyntheticRoomBuilder.BuildFromAscii(RoomWithShelf);
            var expected = new RectInt(5, 1, 3, 3);

            RoomInstance seenRoom = null;
            RectInt seenRegion = default;
            int calls = 0;
            room.TilesMutated += (r, region) => { seenRoom = r; seenRegion = region; calls++; };

            room.NotifyTilesMutated(expected);

            Assert.AreEqual(1, calls, "One notification must raise exactly one event.");
            Assert.AreSame(room, seenRoom, "The event must carry the room that changed.");
            Assert.AreEqual(expected, seenRegion,
                "The region must arrive unchanged — callers depend on the one-tile border surviving.");
        }

        /// <summary>
        /// The world steps in its own slot, strictly between the systems that write bodies and the
        /// ones that read them.
        ///
        /// <para>Regression guard for the ordering defect: the world, <c>Projectile</c> and
        /// <c>ThrownObject</c> all sat in <c>SimTickOrder.Projectiles</c> (40), so the world's
        /// <c>Simulate()</c> and a projectile's sweep were ordered only by registration order. Static
        /// geometry hides it; the first dynamic body would not. Asserted as an inequality rather than
        /// against the literal 35, so renumbering the slots cannot silently break the intent.</para>
        /// </summary>
        [Test]
        public void TickOrder_StepsAfterUnitsAndBeforeConsumers()
        {
            using var service = new PhysicsWorldService();

            Assert.Greater(service.TickOrder, SimTickOrder.UnitsAndAi,
                "Bodies are written by units/AI, so the world must step after them.");
            Assert.Less(service.TickOrder, SimTickOrder.Projectiles,
                "Projectiles read the world, so it must step before them.");
        }

        // ── Fixtures ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Mirrors what <c>TileCollider.DestroyThisTile</c> does: destroy the tile in the grid, then
        /// announce the change with a one-tile border.
        /// </summary>
        private static void DestroyTileAndAnnounce(RoomInstance room)
        {
            room.tiles[DestroyedTile.x, DestroyedTile.y].Destroy();
            room.NotifyTilesMutated(new RectInt(
                DestroyedTile.x - 1,
                DestroyedTile.y - 1,
                3,
                3));
        }
    }
}
