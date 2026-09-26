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
using PFE.Tests.EditMode.Systems.Map.TileCollision;

namespace PFE.Tests.Editor.Map.TileQuery
{
    /// <summary>
    /// Tests for the ITileQueryService seam.
    ///
    /// Two kinds of test live here and they serve different purposes:
    ///
    /// 1. SEAM GUARDS - prove the bound implementation is a pure pass-through to the reconciled
    ///    collision math and returns byte-identical results to it. If one of these fails, the seam
    ///    changed behaviour and must not ship.
    ///
    /// 2. PARAMETERISED DIFF TESTS - run the same probe set through every implemented backend and
    ///    assert they agree. Today that is one backend (Unified), so these are cheap no-ops; the
    ///    point is that adding a backend automatically produces a diff with no further edits.
    ///
    /// A second backend used to exist (GridTileQuery, shadowed behind TileQueryDivergenceLogger).
    /// It was deleted: both sides forwarded to the same TileCollisionMath, so it could not stand in
    /// for pre-P2 behaviour, and its one genuine difference - a hardcoded platformThreshold of 8
    /// where AS3 says 10 - was a port invention, not a legacy behaviour worth preserving. The AS3
    /// half of that fact is pinned by PlatformThreshold_* below. See
    /// docs/Roadmap/LLP2D_IMPLEMENTATION_GUIDE.md section 4.3 and decision L4.
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

        /// <summary>
        /// Mixed-geometry fixture: border walls, a one-way platform, a ladder and a slope.
        /// ASCII row 0 is the TOP row (SyntheticRoomBuilder maps it to the highest Y), so with
        /// 7 rows: row 0 -> y=6 (ceiling), row 2 -> y=4 (platform), row 6 -> y=0 (floor).
        /// </summary>
        private static RoomInstance BuildAsciiRoom()
        {
            return SyntheticRoomBuilder.BuildFromAscii(new[]
            {
                "################",
                "#..............#",
                "#....====......#",
                "#....H.........#",
                "#....H..../....#",
                "#....H.../.....#",
                "################"
            });
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
                case TileQueryBackend.Unified:
                    return new UnifiedTileQueryService(room);
                default:
                    throw new NotImplementedException(
                        "Backend " + backend + " has no implementation yet. Add one, then add it to ImplementedBackends.");
            }
        }

        // ── Seam guards: the bound implementation must stay a pass-through ────────────────

        [Test]
        public void Seam_Unified_CheckCollision_MatchesTileCollisionMath()
        {
            ITileQueryService subject = new UnifiedTileQueryService(_room);

            foreach (Rect probe in ProbeRects())
            {
                foreach (TileQueryOptions options in ProbeOptions())
                {
                    bool expected = TileCollisionMath.CheckCollision(
                        _room, probe, 0f, 0f, TileQueryConstants.PorogGrounded,
                        options.IsTransparent, options.CanFallThroughPlatforms, options.VelocityY);
                    bool actual = subject.CheckCollision(probe, options);
                    Assert.AreEqual(expected, actual,
                        "CheckCollision diverged at " + probe + " with options velocityY=" + options.VelocityY);
                }
            }
        }

        [Test]
        public void Seam_Unified_Raycast_MatchesTileCollisionSystem()
        {
            TileCollisionSystem reference = new TileCollisionSystem(_room);
            ITileQueryService subject = new UnifiedTileQueryService(_room);

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
        public void Seam_Unified_GetGroundHeight_MatchesTileCollisionMath()
        {
            ITileQueryService subject = new UnifiedTileQueryService(_room);

            foreach (Vector2 point in ProbePoints())
            {
                float expected = TileCollisionMath.GetGroundHeight(_room, point.x, point.y, 0f, 0f);
                float actual = subject.GetGroundHeight(point);
                Assert.AreEqual(expected, actual, "GetGroundHeight diverged at " + point);
            }
        }

        [Test]
        public void Seam_Unified_IsSolidAt_MatchesTileCollisionMath()
        {
            ITileQueryService subject = new UnifiedTileQueryService(_room);

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

        // ── AS3 platform threshold (porog) ────────────────────────────────────────────────
        //
        // These pin the one fact the deleted GridTileQuery shadow used to carry: the platform
        // pass-through band is AS3's porog = 10 px (Unit.as:278). GridTileQuery hardcoded 8 px,
        // which was a port invention, not an AS3 behaviour, so the shadow measured distance from
        // a value that was itself wrong.
        //
        // The probe must stay INSIDE the platform's own tile row (y = 160..200). A taller probe
        // (this used to be 50 px tall, spanning 191..241) reaches the ceiling wall row above
        // (y = 240..280), so every backend collides on that wall and the threshold is masked.

        [Test]
        public void PlatformThreshold_InsideAs3Porog_Collides()
        {
            ITileQueryService query = new UnifiedTileQueryService(BuildAsciiRoom());

            // Platform at y=4 -> top is at (4 + 1) * 40 = 200 px. Feet 9 px below that are
            // inside porog 10 (200 - 10 = 190) and outside the port's old porog 8 (192).
            Rect feet9pxBelowTop = new Rect(200f, 191f, 30f, 5f);

            Assert.IsTrue(query.CheckCollision(feet9pxBelowTop, TileQueryOptions.Default),
                "AS3 porog is 10 (Unit.as:278), so feet 9 px below a platform top must collide.");
        }

        [Test]
        public void PlatformThreshold_BeyondAs3Porog_DoesNotCollide()
        {
            ITileQueryService query = new UnifiedTileQueryService(BuildAsciiRoom());

            // Feet 11 px below the top are outside porog 10 entirely.
            Rect feet11pxBelowTop = new Rect(200f, 189f, 30f, 5f);

            Assert.IsFalse(query.CheckCollision(feet11pxBelowTop, TileQueryOptions.Default),
                "porog is a band, not 'always collide': feet 11 px below the top must pass through.");
        }

        // ── Classification and swept move (mixed-geometry fixture) ─────────────────────────

        [Test]
        public void Classify_ReportsFlagsForEachTileType()
        {
            ITileQueryService query = new UnifiedTileQueryService(BuildAsciiRoom());

            Assert.AreEqual(TileQueryFlags.Solid, query.Classify(new Vector2Int(0, 0)) & TileQueryFlags.Solid);
            Assert.AreEqual(TileQueryFlags.Platform, query.Classify(new Vector2Int(5, 4)) & TileQueryFlags.Platform);
            Assert.AreEqual(TileQueryFlags.Ladder, query.Classify(new Vector2Int(5, 3)) & TileQueryFlags.Ladder);
            Assert.AreEqual(TileQueryFlags.Slope, query.Classify(new Vector2Int(10, 2)) & TileQueryFlags.Slope);
        }

        [Test]
        public void ResolveMove_IntoLeftWall_BlocksAndReportsSide()
        {
            ITileQueryService query = new UnifiedTileQueryService(BuildAsciiRoom());

            // Moving into the left border wall (x = 0..40) from x = 80 toward x = -20.
            TileBox box = TileBox.FromFeet(new Vector2(80f, 40f), halfWidth: 15f, height: 50f);
            TileMoveResult result = query.ResolveMove(box, new Vector2(-100f, 0f), TileQueryFlags.Solid);

            Assert.IsTrue(result.HitLeft, "A leftward move into the border wall must report HitLeft.");
            Assert.Greater(result.Position.x, 0f, "The box must not tunnel through the border wall.");
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
