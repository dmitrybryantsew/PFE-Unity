using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using PFE.Core;
using PFE.Systems.Map;
using PFE.Systems.Map.Serialization;
using PFE.Systems.Map.TileQuery;
using PFE.Systems.Physics;

namespace PFE.Tests.Editor.Map.TileQuery
{
    /// <summary>
    /// Tests for the ITileQueryService seam.
    ///
    /// Two kinds of test live here and they serve different purposes:
    ///
    /// 1. STAGE A EQUIVALENCE GUARDS - prove GridTileQuery is a pure pass-through and returns
    ///    byte-identical results to the TileCollisionSystem it wraps. If one of these fails, the
    ///    seam changed behaviour and must not ship.
    ///
    /// 2. PARAMETERISED DIFF TESTS - run the same probe set through every implemented backend and
    ///    assert they agree. The point is not to test the grid (already covered by
    ///    TileCollisionSystemTests); it is to make adding a backend automatically produce a diff.
    ///
    /// TODO(ChainTileQuery): add TileQueryBackend.Chain to ImplementedBackends when it lands.
    /// Every diff test below then covers it with no further edits.
    /// </summary>
    [TestFixture]
    public class TileQueryServiceTests
    {
        private const int RoomW = 12;
        private const int RoomH = 12;

        /// <summary>
        /// Backends the diff harness compares. Add implementations here as they become runnable.
        /// </summary>
        private static readonly TileQueryBackend[] ImplementedBackends =
        {
            TileQueryBackend.Grid,
            TileQueryBackend.Unified,
        };

        private RoomInstance _room;

        // Wall placed here by BuildRoom so probe tests have something to hit.
        private static readonly Vector2Int WallCoord = new Vector2Int(5, 5);

        [SetUp]
        public void SetUp()
        {
            _room = BuildEmptyRoom();
            _room.tiles[WallCoord.x, WallCoord.y] = MakeTile(WallCoord.x, WallCoord.y, TilePhysicsType.Wall);
        }

        [TearDown]
        public void TearDown()
        {
            _room = null;
        }

        private static RoomInstance BuildEmptyRoom()
        {
            // landPosition and borderOffset default to zero, so roomWorldPixelX/Y is 0 and
            // WORLD PIXELS == ROOM-LOCAL PIXELS for this fixture. A room at a non-zero land
            // position is NOT covered here and is a known divergence (see ITileQueryService docs).
            RoomInstance room = new RoomInstance
            {
                width = RoomW,
                height = RoomH,
                tiles = new TileData[RoomW, RoomH]
            };

            for (int x = 0; x < RoomW; x++)
            {
                for (int y = 0; y < RoomH; y++)
                {
                    room.tiles[x, y] = MakeTile(x, y, TilePhysicsType.Air);
                }
            }

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

        private static ITileQueryService Create(TileQueryBackend backend, RoomInstance room)
        {
            switch (backend)
            {
                case TileQueryBackend.Grid:
                    return new GridTileQuery(room);
                case TileQueryBackend.Unified:
                    return new UnifiedTileQueryService(room);
                default:
                    throw new NotImplementedException(
                        "Backend " + backend + " has no implementation yet. Add one, then add it to ImplementedBackends.");
            }
        }

        // ── Stage A equivalence guards ────────────────────────────────────────────────────

        [Test]
        public void StageA_GridBackend_CheckCollision_MatchesTileCollisionMath()
        {
            ITileQueryService subject = new GridTileQuery(_room);

            foreach (Rect probe in ProbeRects())
            {
                foreach (TileQueryOptions options in ProbeOptions())
                {
                    bool expected = TileCollisionMath.CheckCollision(
                        _room, probe, 0f, 0f, 8f,
                        options.IsTransparent, options.CanFallThroughPlatforms, options.VelocityY);
                    bool actual = subject.CheckCollision(probe, options);
                    Assert.AreEqual(expected, actual,
                        "CheckCollision diverged at " + probe + " with options velocityY=" + options.VelocityY);
                }
            }
        }

        [Test]
        public void StageA_GridBackend_Raycast_MatchesTileCollisionSystem()
        {
            TileCollisionSystem reference = new TileCollisionSystem(_room);
            ITileQueryService subject = new GridTileQuery(_room);

            foreach (Vector2 origin in ProbePoints())
            {
                (TileData tile, Vector2 point)? expected = reference.Raycast(origin, Vector2.right, 400f);
                TileRaycastHit? actual = subject.Raycast(origin, Vector2.right, 400f);

                Assert.AreEqual(expected.HasValue, actual.HasValue, "Raycast hit presence diverged at " + origin);
                if (expected.HasValue && actual.HasValue)
                {
                    Assert.AreEqual(expected.Value.point, actual.Value.Point, "Raycast point diverged at " + origin);
                    Assert.AreSame(expected.Value.tile, actual.Value.Tile, "Raycast tile diverged at " + origin);
                }
            }
        }

        [Test]
        public void StageA_GridBackend_GetGroundHeight_MatchesTileCollisionMath()
        {
            ITileQueryService subject = new GridTileQuery(_room);

            foreach (Vector2 point in ProbePoints())
            {
                float expected = TileCollisionMath.GetGroundHeight(_room, point.x, point.y, 0f, 0f);
                float actual = subject.GetGroundHeight(point);
                Assert.AreEqual(expected, actual, "GetGroundHeight diverged at " + point);
            }
        }

        [Test]
        public void StageA_GridBackend_IsSolidAt_MatchesTileCollisionMath()
        {
            ITileQueryService subject = new GridTileQuery(_room);

            for (int x = 0; x < RoomW; x++)
            {
                for (int y = 0; y < RoomH; y++)
                {
                    Vector2Int coord = new Vector2Int(x, y);
                    bool expected = TileCollisionMath.IsSolidAt(_room, coord);
                    bool actual = subject.IsSolidAt(coord);
                    Assert.AreEqual(expected, actual, "IsSolidAt diverged at " + coord);
                }
            }
        }

        // ── Contract tests (parameterised over every implemented backend) ──────────────────

        [Test]
        public void IsSolidAt_AirTile_IsFalse([ValueSource(nameof(ImplementedBackends))] TileQueryBackend backend)
        {
            ITileQueryService query = Create(backend, _room);
            Assert.IsFalse(query.IsSolidAt(new Vector2Int(1, 1)));
        }

        [Test]
        public void IsSolidAt_WallTile_IsTrue([ValueSource(nameof(ImplementedBackends))] TileQueryBackend backend)
        {
            ITileQueryService query = Create(backend, _room);
            Assert.IsTrue(query.IsSolidAt(WallCoord));
        }

        [Test]
        public void IsSolidAt_OutsideRoom_IsFalse([ValueSource(nameof(ImplementedBackends))] TileQueryBackend backend)
        {
            ITileQueryService query = Create(backend, _room);
            Assert.IsFalse(query.IsSolidAt(new Vector2Int(-1, -1)));
            Assert.IsFalse(query.IsSolidAt(new Vector2Int(RoomW + 5, RoomH + 5)));
        }

        [Test]
        public void Raycast_ThroughOpenAir_ReportsNoHit([ValueSource(nameof(ImplementedBackends))] TileQueryBackend backend)
        {
            ITileQueryService query = Create(backend, _room);
            // Row 1 is all air.
            TileRaycastHit? hit = query.Raycast(new Vector2(8f, 20f), Vector2.left, 200f);
            Assert.IsFalse(hit.HasValue);
        }

        [Test]
        public void Raycast_IntoWall_ReportsHit([ValueSource(nameof(ImplementedBackends))] TileQueryBackend backend)
        {
            ITileQueryService query = Create(backend, _room);
            // Wall at tile (5,5) spans pixels 200..240 on both axes.
            TileRaycastHit? hit = query.Raycast(new Vector2(8f, 220f), Vector2.right, 400f);
            Assert.IsTrue(hit.HasValue, "Expected a hit travelling right into the wall.");
            Assert.AreSame(_room.tiles[WallCoord.x, WallCoord.y], hit.Value.Tile);
        }

        [Test]
        public void NotifyTilesMutated_DoesNotChangeGridResults(
            [ValueSource(nameof(ImplementedBackends))] TileQueryBackend backend)
        {
            ITileQueryService query = Create(backend, _room);
            Assert.IsTrue(query.IsSolidAt(WallCoord));

            // Mutate the tile directly, the path that does NOT go through ApplyDamage.
            _room.tiles[WallCoord.x, WallCoord.y].physicsType = TilePhysicsType.Air;
            query.NotifyTilesMutated(new RectInt(WallCoord.x, WallCoord.y, 1, 1));

            Assert.IsFalse(query.IsSolidAt(WallCoord),
                "Backend returned stale geometry after NotifyTilesMutated. Caching backends must rebuild here.");
        }

        // ── The diff harness ──────────────────────────────────────────────────────────────

        [Test]
        public void DiffHarness_AllBackendsAgree_OnSolidity()
        {
            List<ITileQueryService> queries = CreateAll();

            for (int x = 0; x < RoomW; x++)
            {
                for (int y = 0; y < RoomH; y++)
                {
                    Vector2Int coord = new Vector2Int(x, y);
                    bool first = queries[0].IsSolidAt(coord);
                    for (int i = 1; i < queries.Count; i++)
                    {
                        Assert.AreEqual(first, queries[i].IsSolidAt(coord),
                            "Backends disagree on IsSolidAt(" + coord + "): " +
                            queries[0].Backend + "=" + first + " vs " + queries[i].Backend);
                    }
                }
            }
        }

        [Test]
        public void DiffHarness_AllBackendsAgree_OnRaycasts()
        {
            List<ITileQueryService> queries = CreateAll();

            foreach (Vector2 origin in ProbePoints())
            {
                TileRaycastHit? first = queries[0].Raycast(origin, Vector2.right, 400f);
                for (int i = 1; i < queries.Count; i++)
                {
                    TileRaycastHit? other = queries[i].Raycast(origin, Vector2.right, 400f);
                    Assert.AreEqual(first.HasValue, other.HasValue,
                        "Backends disagree on raycast presence from " + origin);
                    if (first.HasValue && other.HasValue)
                    {
                        Assert.AreEqual(first.Value.Point.x, other.Value.Point.x, 0.5f,
                            "Backends disagree on raycast distance from " + origin);
                    }
                }
            }
        }

        // ── Replication: snapshot ─────────────────────────────────────────────────────────

        [Test]
        public void Snapshot_CapturesEveryTile(
            [ValueSource(nameof(ImplementedBackends))] TileQueryBackend backend)
        {
            ITileQueryService query = Create(backend, _room);
            TileStateSnapshot[] snapshot = query.CaptureFullState();
            Assert.AreEqual(RoomW * RoomH, snapshot.Length);
        }

        [Test]
        public void Snapshot_RoundTrip_PreservesMutableState(
            [ValueSource(nameof(ImplementedBackends))] TileQueryBackend backend)
        {
            ITileQueryService query = Create(backend, _room);

            // Give the wall a distinctive HP so a no-op apply would be visible.
            _room.tiles[WallCoord.x, WallCoord.y].hitPoints = 321;
            _room.tiles[WallCoord.x, WallCoord.y].damageThreshold = 7;

            TileStateSnapshot[] snapshot = query.CaptureFullState();

            // Wipe local state, then restore from the snapshot.
            foreach (TileData tile in _room.tiles)
            {
                tile.physicsType = TilePhysicsType.Air;
                tile.hitPoints = 0;
                tile.damageThreshold = 0;
            }
            query.DrainMutations();

            query.ApplyFullState(snapshot);

            TileData restored = _room.tiles[WallCoord.x, WallCoord.y];
            Assert.AreEqual(TilePhysicsType.Wall, restored.physicsType);
            Assert.AreEqual(321, restored.hitPoints);
            Assert.AreEqual(7, restored.damageThreshold);
            Assert.IsTrue(query.IsSolidAt(WallCoord));
        }

        // ── Replication: mutation delta stream ─────────────────────────────────────────────

        [Test]
        public void DrainMutations_IsEmptyBeforeAnythingChanges(
            [ValueSource(nameof(ImplementedBackends))] TileQueryBackend backend)
        {
            ITileQueryService query = Create(backend, _room);
            Assert.AreEqual(0, query.DrainMutations().Length);
        }

        [Test]
        public void DrainMutations_AfterNotifyTilesMutated_ReportsTheRegion(
            [ValueSource(nameof(ImplementedBackends))] TileQueryBackend backend)
        {
            ITileQueryService query = Create(backend, _room);
            query.NotifyTilesMutated(new RectInt(3, 4, 2, 2));

            TileMutation[] mutations = query.DrainMutations();
            Assert.AreEqual(4, mutations.Length, "A 2x2 region should report four tiles.");

            HashSet<Vector2Int> reported = new HashSet<Vector2Int>();
            foreach (TileMutation mutation in mutations) reported.Add(mutation.Coord);

            for (int x = 3; x < 5; x++)
            {
                for (int y = 4; y < 6; y++)
                {
                    Assert.IsTrue(reported.Contains(new Vector2Int(x, y)), "Missing coord (" + x + "," + y + ")");
                }
            }
        }

        [Test]
        public void DrainMutations_ClearsThePendingSet(
            [ValueSource(nameof(ImplementedBackends))] TileQueryBackend backend)
        {
            ITileQueryService query = Create(backend, _room);
            query.NotifyTilesMutated(new RectInt(1, 1, 1, 1));

            Assert.AreEqual(1, query.DrainMutations().Length);
            Assert.AreEqual(0, query.DrainMutations().Length, "Second drain must be empty.");
        }

        [Test]
        public void ApplyMutations_ChangesLocalSolidity(
            [ValueSource(nameof(ImplementedBackends))] TileQueryBackend backend)
        {
            ITileQueryService query = Create(backend, _room);
            Assert.IsTrue(query.IsSolidAt(WallCoord));

            query.ApplyMutations(new[]
            {
                new TileMutation(WallCoord, TilePhysicsType.Air, 0)
            });

            Assert.IsFalse(query.IsSolidAt(WallCoord), "Remote mutation did not apply.");
        }

        [Test]
        public void ReceivingState_DoesNotOriginateMutations(
            [ValueSource(nameof(ImplementedBackends))] TileQueryBackend backend)
        {
            ITileQueryService query = Create(backend, _room);

            TileStateSnapshot[] snapshot = query.CaptureFullState();
            query.ApplyFullState(snapshot);
            Assert.AreEqual(0, query.DrainMutations().Length,
                "Applying a full snapshot must not feed the outgoing delta stream, or every "
                + "client would echo state back to the host.");

            query.ApplyMutations(new[] { new TileMutation(WallCoord, TilePhysicsType.Air, 0) });
            Assert.AreEqual(0, query.DrainMutations().Length,
                "Applying remote mutations must not feed the outgoing delta stream.");
        }

        // ── Authority map (P4.1) ───────────────────────────────────────────────────────────

        [Test]
        public void AuthorityMap_MutationsAreHostOnly()
        {
            Assert.AreEqual(SimAuthority.Authoritative, AuthorityOf("ApplyDamage"));
            Assert.AreEqual(SimAuthority.Authoritative, AuthorityOf("NotifyTilesMutated"));
        }

        [Test]
        public void AuthorityMap_QueriesAreLocalOnly()
        {
            Assert.AreEqual(SimAuthority.LocalOnly, AuthorityOf("IsSolidAt"));
            Assert.AreEqual(SimAuthority.LocalOnly, AuthorityOf("CheckCollision"));
            Assert.AreEqual(SimAuthority.LocalOnly, AuthorityOf("GetGroundHeight"));
            Assert.AreEqual(SimAuthority.LocalOnly, AuthorityOf("IsOnGround"));
            Assert.AreEqual(SimAuthority.LocalOnly, AuthorityOf("Raycast"));
        }

        /// <summary>
        /// The lint that makes the authority map self-maintaining: adding a member to
        /// ITileQueryService without classifying it fails here.
        /// </summary>
        [Test]
        public void AuthorityMap_EveryMemberIsClassified()
        {
            List<string> unclassified = new List<string>();

            foreach (MethodInfo method in typeof(ITileQueryService).GetMethods())
            {
                // Property getters/setters are IsSpecialName; they are classified via the
                // PROPERTY declaration below (attributes sit on the property, not the accessor).
                if (method.IsSpecialName)
                {
                    continue;
                }

                if (AuthorityResolver.Of(method) == SimAuthority.Unspecified)
                {
                    unclassified.Add("method " + method.Name);
                }
            }

            foreach (PropertyInfo property in typeof(ITileQueryService).GetProperties())
            {
                if (AuthorityResolver.Of(property) == SimAuthority.Unspecified)
                {
                    unclassified.Add("property " + property.Name);
                }
            }

            Assert.IsEmpty(unclassified,
                "Unclassified members on ITileQueryService: " + string.Join(", ", unclassified));
        }

        // ── Fixtures ──────────────────────────────────────────────────────────────────────

        private static SimAuthority AuthorityOf(string methodName)
        {
            MethodInfo method = typeof(ITileQueryService).GetMethod(methodName);
            Assert.IsNotNull(method, "ITileQueryService has no method named " + methodName);
            return AuthorityResolver.Of(method);
        }

        private List<ITileQueryService> CreateAll()
        {
            List<ITileQueryService> queries = new List<ITileQueryService>();
            foreach (TileQueryBackend backend in ImplementedBackends)
            {
                queries.Add(Create(backend, _room));
            }
            return queries;
        }

        private static IEnumerable<Vector2> ProbePoints()
        {
            for (int tx = 0; tx < RoomW; tx++)
            {
                for (int ty = 0; ty < RoomH; ty++)
                {
                    // Tile centre in pixels (TILE_SIZE = 40).
                    yield return new Vector2(tx * 40f + 20f, ty * 40f + 20f);
                }
            }
        }

        private static IEnumerable<Rect> ProbeRects()
        {
            foreach (Vector2 centre in ProbePoints())
            {
                yield return new Rect(centre.x - 8f, centre.y - 8f, 16f, 16f);
            }
        }

        private static IEnumerable<TileQueryOptions> ProbeOptions()
        {
            yield return TileQueryOptions.Default;
            yield return new TileQueryOptions(isTransparent: true);
            yield return new TileQueryOptions(canFallThroughPlatforms: true);
            yield return new TileQueryOptions(velocityY: -5f);
            yield return new TileQueryOptions(velocityY: 5f);
        }
    }
}
