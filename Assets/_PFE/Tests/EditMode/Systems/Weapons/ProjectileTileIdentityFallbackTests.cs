using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using PFE.Data.Definitions;
using PFE.Entities.Weapons;
using PFE.Systems.Combat;
using PFE.Systems.Map;
using PFE.Systems.Map.Rendering;
using PFE.Systems.Map.TileQuery;
using PFE.Systems.Physics;

namespace PFE.Tests.EditMode.Systems.Weapons
{
    /// <summary>
    /// Pins the door half of <see cref="Projectile"/>'s tile-contact resolution since Stage D.
    ///
    /// <para><b>What changed.</b> A chain contact used to resolve to a per-tile <c>TileCollider</c>
    /// GameObject, with a second probe for any <see cref="IDestructibleTile"/> as the door fallback.
    /// Tiles have no colliders now: the grid answers for plain tiles (a wall's identity is its
    /// <c>TileData</c>), and the probe names only what still owns a collider — a door or barricade
    /// prop. This fixture pins that probe.</para>
    ///
    /// <para><b>Why the fallback exists at all.</b> A closed door stamps
    /// <c>TilePhysicsType.Wall</c> over the cells it covers, so the chain reports them as solid —
    /// but those cells are air in the shipped maps and a door-stamped cell is the prop's business,
    /// not the grid's. Without the probe a round aimed at a closed door stopped and resolved to
    /// nothing: no blast, no door damage, no impact sound. That was the owner's "the grenade
    /// launcher doesn't hit some doors, the grenade just disappears".</para>
    ///
    /// <para><b>Runs in the editor only.</b> The probe calls <c>Physics2D.OverlapBoxAll</c>, an ECall,
    /// so the offline wall cannot JIT it — same venue and same reason as
    /// <see cref="ProjectileImpactStopTests"/>.</para>
    /// </summary>
    [TestFixture]
    public class ProjectileTileIdentityFallbackTests
    {
        private GameObject _projectileGo;
        private Projectile _projectile;
        private List<GameObject> _spawned;
        private List<PhysicsWorldService> _worlds;

        /// <summary>
        /// A stand-in for a door: a non-trigger collider carrying <see cref="IDestructibleTile"/>.
        /// The interface is the only thing the probe accepts, so the double implements it.
        /// </summary>
        private class DestructiblePropStub : MonoBehaviour, IDestructibleTile
        {
            public int DirectCalls;
            public int RadiusCalls;

            public void ApplyDestruction(Vector3 worldPosition, float destroyAmount, DamageType damageType,
                                         DecalType decal)
                => DirectCalls++;

            public void ApplyDestructionRadius(Vector3 worldPosition, float radius,
                                               float destroyAmount, DamageType damageType, DecalType decal)
                => RadiusCalls++;
        }

        [SetUp]
        public void SetUp()
        {
            _spawned   = new List<GameObject>();
            _worlds    = new List<PhysicsWorldService>();
            _projectileGo = new GameObject("ProjectileTileIdentityFallback");
            _projectile   = _projectileGo.AddComponent<Projectile>();

            // No normal, so the probe sits on the contact point itself rather than 2 px behind it.
            // The fixture places each candidate collider exactly where it probes to.
            SetField("_lastContactNormal", Vector2.zero);
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _worlds.Count; i++)
            {
                _worlds[i]?.Dispose();
            }

            for (int i = 0; i < _spawned.Count; i++)
            {
                if (_spawned[i] != null) Object.DestroyImmediate(_spawned[i]);
            }

            if (_projectileGo != null) Object.DestroyImmediate(_projectileGo);
        }

        /// <summary>
        /// The regression. A surface the chain reports but no grid tile owns — a closed door's
        /// stamped cell — must still resolve to something, or the round dies silently.
        /// </summary>
        [Test]
        public void AChainSurfaceNoGridTileOwns_FallsBackToTheDestructibleProp()
        {
            GameObject door = NewDoorStub("doorLikeSurface");
            Collider2D found = ProbeAt(door);

            Assert.IsNotNull(found,
                "The chain reports the door's stamped cell as solid, so a round aimed at it stops " +
                "there — and stopping without an identified surface means HandleImpact never runs, " +
                "so there is no blast, no door damage and no impact sound. That is the reported " +
                "'the grenade disappears'. Do not remove the IDestructibleTile probe: it is the only " +
                "reason this surface answers at all.");

            Assert.AreSame(door.GetComponent<IDestructibleTile>(),
                           found.GetComponent<IDestructibleTile>(),
                "The probe resolves the hit to the prop that owns the surface, so HandleImpact " +
                "routes it through the same IDestructibleTile branch it has always taken.");
        }

        /// <summary>
        /// The Stage D companion to the old "a real tile wins" case. A plain wall has no collider
        /// anymore, so the door probe must NOT answer for it — walls are the tile grid's business
        /// (<c>TryGridTileAt</c>), and a probe that still matched solid-but-not-destructible
        /// surfaces would drag wall hits back onto whatever prop happened to overlap.
        /// </summary>
        [Test]
        public void APlainSolidColliderWithoutTheInterface_IsNotASurface()
        {
            // A solid, non-trigger collider with no IDestructibleTile — the shape a wall used to
            // have. Since Stage D nothing tile-like may resolve through the probe.
            GameObject plain = NewPlainSolid("solidButNotDestructible");
            Collider2D found = ProbeAt(plain);

            Assert.IsNull(found,
                "The probe names door/barricade props only. A plain solid resolves through the " +
                "tile grid, or not at all — answering here would move a wall hit onto an " +
                "overlapping prop.");
        }

        /// <summary>
        /// The honest-empty case, so the probe is not a blanket truth. It is what keeps the
        /// "stopping without surface sound" warning in <c>ResolveTileImpact</c> reachable — a
        /// probe that always answered would turn that failure into a silent one.
        /// </summary>
        [Test]
        public void NothingThereAtAll_StillAnswersNothing()
        {
            Assert.IsNull(ProbeAt(null));
        }

        // ── Grid identity: the sweep → ResolveTileImpact composition ─────────────────────────
        //
        // The two regressions these pin were both live-only: every link was individually green,
        // but composed in a running room a round could stop on a wall and destroy nothing.
        //   1. The chain sweep reports the TOUCH point, which lies exactly on a tile-face
        //      boundary; a raw floor() there names the air neighbour for half the approach
        //      directions, the hit resolves to an air tile, and TileHitRules refuses it — the
        //      "plasma gatling can't break walls" report.
        //   2. A door stamp outlives the door: Box.setDoor / Box.die write physicsType = Air but
        //      leave doorOcclusion at 0, so `doorOcclusion == NoDoorOcclusion` never matched an
        //      open doorway and the round stopped on the wreck — the "broken door frame blocks
        //      shots" report. AS3's bullet test wants phis >= 1; a phis == 0 doorway is flown
        //      through.

        /// <summary>The regression case: a left-moving round meets a wall's RIGHT face, so the
        /// contact sits on the upper (exclusive) side of a cell boundary. The nudge into the
        /// surface must name the wall cell — without it the air neighbour answers and nothing
        /// is destroyed.</summary>
        [Test]
        public void WallRightFaceHit_ContactOnBoundary_ResolvesToTheWallCellAndDestroysIt()
        {
            const int wallX = 24, rowY = 12;
            RoomInstance room = BuildRoomWithWallColumn(wallX, out PhysicsWorldService world);

            Vector2 origin = new UnifiedTileQueryService(room).OriginPixel;
            Vector2 fromPx = origin + new Vector2(27f * 40f, rowY * 40f + 20f);
            Assert.IsTrue(world.TrySweepTiles(
                fromPx, new Vector2(-120f, 0f), new Vector2(8f, 8f), new Vector2(-1f, 0f),
                out Vector2 contactPx, out Vector2 normal),
                "the mirrored wall column must report the swept contact");

            bool returned = false;
            _projectile.OnReturnToPool = _ => returned = true;
            SetField("_physicsWorld", world);
            SetField("_destroyTiles", 600f);
            SetField("_decalType", (DecalType)9);
            SetField("_lastContactNormal", normal);

            Invoke("ResolveTileImpact", contactPx);

            Assert.IsTrue(returned, "the round stopped on the wall");
            Assert.AreEqual(TilePhysicsType.Air, room.GetTileAtCoord(new Vector2Int(wallX, rowY)).physicsType,
                "the cell that owns the face was destroyed — without the surface nudge the contact " +
                "resolved to the air neighbour and the wall survived untouched");
        }

        /// <summary>The companion pin: a right-moving round meets the wall's LEFT face, the side
        /// a raw floor() already names correctly. The nudge must not break this direction.</summary>
        [Test]
        public void WallLeftFaceHit_ContactOnBoundary_StillResolvesToTheWallCell()
        {
            const int wallX = 24, rowY = 12;
            RoomInstance room = BuildRoomWithWallColumn(wallX, out PhysicsWorldService world);

            Vector2 origin = new UnifiedTileQueryService(room).OriginPixel;
            Vector2 fromPx = origin + new Vector2(21f * 40f, rowY * 40f + 20f);
            Assert.IsTrue(world.TrySweepTiles(
                fromPx, new Vector2(120f, 0f), new Vector2(8f, 8f), new Vector2(1f, 0f),
                out Vector2 contactPx, out Vector2 normal));

            bool returned = false;
            _projectile.OnReturnToPool = _ => returned = true;
            SetField("_physicsWorld", world);
            SetField("_destroyTiles", 600f);
            SetField("_decalType", (DecalType)9);
            SetField("_lastContactNormal", normal);

            Invoke("ResolveTileImpact", contactPx);

            Assert.IsTrue(returned, "the round stopped on the wall");
            Assert.AreEqual(TilePhysicsType.Air, room.GetTileAtCoord(new Vector2Int(wallX, rowY)).physicsType,
                "the wall cell was destroyed");
        }

        /// <summary>The doorway case: the cell a closed door stamped is left stamped (opac 0)
        /// but Air — a wrecked or open door. The round passes through: AS3's bullet test wants
        /// phis >= 1 and this cell has phis == 0, so there is no surface, no sound and no stop.
        /// Before the fix the stamp alone routed the hit to the (now empty) door probe and the
        /// round stopped on every shot-open doorway.</summary>
        [Test]
        public void DoorwayCellStampedButAir_TheRoundPassesThrough()
        {
            const int wallX = 24, rowY = 12;
            RoomInstance room = BuildRoomWithWallColumn(wallX, out PhysicsWorldService world);

            // Box.die's tile write: physicsType = Air, doorOcclusion = 0 (NOT the -1 sentinel).
            TileData doorway = room.GetTileAtCoord(new Vector2Int(wallX, rowY));
            doorway.physicsType  = TilePhysicsType.Air;
            doorway.doorOcclusion = 0f;

            // A synthetic chain contact on the cell's left face with the normal a right-moving
            // round would produce — exactly what a stale reported surface hands ResolveTileImpact.
            Vector2 origin = new UnifiedTileQueryService(room).OriginPixel;
            Vector2 contactPx = origin + new Vector2(wallX * 40f, rowY * 40f + 20f);

            bool returned = false;
            _projectile.OnReturnToPool = _ => returned = true;
            SetField("_physicsWorld", world);
            SetField("_destroyTiles", 600f);
            SetField("_decalType", (DecalType)9);
            SetField("_lastContactNormal", new Vector2(-1f, 0f));

            Invoke("ResolveTileImpact", contactPx);

            Assert.IsFalse(returned,
                "an open/wrecked doorway has phis == 0 (Box.setDoor/Box.die), so AS3 flies the " +
                "round through; stopping here is what sealed every doorway a shot had opened");
            Assert.AreEqual(TilePhysicsType.Air, doorway.physicsType, "nothing to damage");
        }

        // ── helpers ──────────────────────────────────────────────────────────────────────────

        /// <summary>A real room mirrored in a real chain geometry: one solid column at
        /// <paramref name="wallX"/> (rows 8..16), everything else air. The service is tracked
        /// for disposal by <see cref="TearDown"/>.</summary>
        private RoomInstance BuildRoomWithWallColumn(int wallX, out PhysicsWorldService world)
        {
            RoomInstance room = new RoomInstance
            {
                id = "tile_identity_room",
                width  = WorldConstants.ROOM_WIDTH,
                height = WorldConstants.ROOM_HEIGHT,
                landPosition = new Vector3Int(0, 0, 0)
            };
            room.InitializeTiles();

            for (int y = 8; y <= 16; y++)
            {
                TileData tile = room.GetTileAtCoord(new Vector2Int(wallX, y));
                tile.physicsType      = TilePhysicsType.Wall;
                tile.hitPoints        = 100;
                tile.damageThreshold  = 10;
                tile.indestructible   = false;
            }

            world = new PhysicsWorldService();
            _worlds.Add(world);
            world.BuildRoomGeometry(room);
            return room;
        }

        /// <summary>
        /// Builds a candidate surface at world (1, 1) — the position a 100 px contact point maps to
        /// — and asks <c>FindDestructibleAt</c> for it.
        /// </summary>
        private Collider2D ProbeAt(GameObject at)
        {
            Vector2 contactPx = new Vector2(100f, 100f);
            if (at != null)
            {
                at.transform.position = new Vector3(1f, 1f, 0f);
                // The project ships with Physics2D auto-sync OFF (m_AutoSyncTransforms: 0), so a
                // query right after a transform write sees the collider's stale pose and misses.
                Physics2D.SyncTransforms();
            }

            return (Collider2D)Invoke("FindDestructibleAt", contactPx);
        }

        private GameObject NewDoorStub(string name)
        {
            GameObject go = new GameObject(name);
            var box = go.AddComponent<BoxCollider2D>();
            box.isTrigger = false;
            box.size      = new Vector2(0.4f, 0.4f);   // one tile, well inside the 0.06 probe box
            go.AddComponent<DestructiblePropStub>();
            _spawned.Add(go);
            return go;
        }

        private GameObject NewPlainSolid(string name)
        {
            GameObject go = new GameObject(name);
            var box = go.AddComponent<BoxCollider2D>();
            box.isTrigger = false;
            box.size      = new Vector2(0.4f, 0.4f);
            _spawned.Add(go);
            return go;
        }

        private void SetField(string name, object value)
        {
            var field = typeof(Projectile).GetField(
                name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

            Assert.IsNotNull(field, $"Projectile has no private instance field '{name}'.");

            field.SetValue(_projectile, value);
        }

        private object Invoke(string name, params object[] args)
        {
            var method = typeof(Projectile).GetMethod(
                name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

            Assert.IsNotNull(method, $"Projectile has no private instance method '{name}'.");

            return method.Invoke(_projectile, args);
        }
    }
}
