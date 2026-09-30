using System.Collections.Generic;
using UnityEngine;
using UnityEngine.LowLevelPhysics2D;
using PFE.Core;
using PFE.Systems.Map;
using PFE.Systems.Map.Streaming;
using PFE.Systems.Map.TileQuery;

namespace PFE.Systems.Physics
{
    /// <summary>
    /// Owns the <see cref="PhysicsWorld"/> for LowLevelPhysics2D (Box2D v3) and manages the
    /// chain-geometry mirror of active rooms.
    ///
    /// <para>One world per active room set (decision L1). The world is stepped from
    /// <see cref="SimLoop"/>, never from <c>FixedUpdate</c> or render time — which is why this
    /// interface extends <see cref="ISimTickable"/> rather than leaving stepping to the
    /// implementation. A world that is not registered on the loop does not advance, and nothing
    /// about that failure is visible from the outside.</para>
    ///
    /// <para>Stage B: chain geometry exists, zero consumers. Stage C: projectiles query this world.
    /// See <c>docs/Roadmap/LLP2D_IMPLEMENTATION_GUIDE.md</c>.</para>
    /// </summary>
    public interface IPhysicsWorldService : ISimTickable
    {
        /// <summary>The owned Box2D v3 world. Valid after first room activation.</summary>
        PhysicsWorld World { get; }

        /// <summary>
        /// The rooms this service currently mirrors, each with the chain geometry it built.
        ///
        /// <para><b>For the debug overlay, and only for it.</b> The chain mirror is invisible from the
        /// scene — it is Box2D geometry inside a <see cref="PhysicsWorld"/>, not a
        /// <c>Collider2D</c> on a GameObject — so <c>ColliderDebugOverlay</c>'s existing tile boxes
        /// show Unity's collider grid and say nothing at all about what a projectile actually sweeps
        /// against. When those two disagree, that difference <i>is</i> the bug, and it cannot be seen
        /// without this. <c>col on physics</c> draws it.</para>
        ///
        /// <para>Keys are live <see cref="RoomInstance"/> references and the values die with
        /// <see cref="DestroyRoomGeometry"/>, so a caller must not hold either past a room
        /// transition.</para>
        /// </summary>
        IReadOnlyDictionary<RoomInstance, RoomChainGeometry> MirroredRooms { get; }

        /// <summary>True once the world has been created and is ready for bodies.</summary>
        bool IsWorldValid { get; }

        /// <summary>
        /// Subscribe to <see cref="RoomStreamingManager"/> room events so activation builds geometry
        /// and deactivation destroys it. <see cref="MapBridge"/> calls this once, after it creates
        /// the streaming manager. Idempotency is not guaranteed: calling twice subscribes twice.
        /// </summary>
        void SubscribeToRoomEvents(RoomStreamingManager streamingManager);

        /// <summary>
        /// Build chain geometry for <paramref name="room"/> into the world.
        /// Called by <see cref="RoomStreamingManager.OnRoomActivated"/>.
        /// Idempotent: calling twice for the same room rebuilds (destroys old, creates new).
        /// </summary>
        void BuildRoomGeometry(RoomInstance room);

        /// <summary>
        /// Destroy chain geometry for <paramref name="room"/>.
        /// Called by <see cref="RoomStreamingManager.OnRoomDeactivated"/>.
        /// </summary>
        void DestroyRoomGeometry(RoomInstance room);

        /// <summary>
        /// Rebuild geometry for a region of <paramref name="room"/> after tiles mutate.
        /// Called via <see cref="PFE.Systems.Map.TileQuery.ITileQueryService.NotifyTilesMutated"/>.
        /// </summary>
        void RebuildRegion(RoomInstance room, RectInt tileRegion);

        /// <summary>
        /// Sweeps a projectile-shaped capsule through the active rooms' chain geometry and reports the
        /// nearest tile contact. All coordinates are <b>world pixels</b> — the same space
        /// <c>ITileQueryService</c> uses, so a caller never converts.
        ///
        /// <para><b>This is Stage C's whole purpose.</b> Until now the only thing that answered "is
        /// there a tile in my way?" for a projectile was Unity's per-tile <c>BoxCollider2D</c> grid,
        /// which owns its own interpretation of the slope and produces ghost collisions at the seams
        /// between adjacent cells. Answering it here instead means the geometry comes from
        /// <c>ITileQueryService</c> via the chain mirror, so it cannot drift.</para>
        ///
        /// <para><b>Swept, not sampled.</b> A chain is a zero-thickness surface, so the window in
        /// which a shape overlaps it is only as wide as the shape. AS3 bullets travel up to 500 px per
        /// frame, far wider than that window, so this must be a swept test — an end-of-step overlap
        /// test would let them tunnel through walls. See
        /// <see cref="RoomChainGeometry.TrySweep"/>.</para>
        ///
        /// <para>Returns false when the world is invalid or no active room has geometry, which is the
        /// correct answer for a projectile in an unbuilt room: nothing is there to hit.</para>
        /// </summary>
        /// <param name="fromPx">Shape centre at the start of the move, world pixels.</param>
        /// <param name="deltaPx">Translation over this step, world pixels.</param>
        /// <param name="sizePx">Shape (length, thickness) in pixels; mirrors the projectile
        /// prefab's collider. Length runs along <paramref name="facing"/>. A thickness equal to the
        /// length has no straight section and is swept as a circle — see
        /// <see cref="RoomChainGeometry.TrySweep"/> for why that case needs its own shape.</param>
        /// <param name="facing">Unit direction of the shape's long axis — the travel direction.
        /// Without it a needle-shaped hitbox would be modelled as a dot at its centre and every
        /// bullet would register its hit ~46 px late, i.e. buried halfway into the wall.</param>
        /// <param name="contactPointPx">Nearest contact point, world pixels.</param>
        /// <param name="contactNormal">Surface normal at the contact, or zero for an initial overlap.</param>
        bool TrySweepTiles(Vector2 fromPx, Vector2 deltaPx, Vector2 sizePx, Vector2 facing,
                           out Vector2 contactPointPx, out Vector2 contactNormal);

        /// <summary>
        /// The tile-query seam of the mirrored room containing a world-pixel point, or <c>null</c>
        /// when the point is outside every room this service currently mirrors.
        ///
        /// <para><b>Why this exists, and why it is not <see cref="TrySweepTiles"/>.</b> A thrown
        /// object's tile collision in AS3 is <i>not</i> a swept shape. <c>PhisBullet.run()</c>
        /// (<c>PhisBullet.as:209-347</c>) advances a <b>point</b> by at most <c>World.maxdelta</c>
        /// (9 px, <c>World.as:48</c>) and asks <c>loc.getAbsTile(X, Y)</c> whether the cell it landed
        /// in is solid — so tunnelling is prevented by sub-stepping, not by a sweep, and the
        /// collision shape is a point rather than the prefab's capsule. Reproducing that with a
        /// swept capsule would give a grenade a hitbox it does not have in the original, which is a
        /// behaviour change disguised as an implementation change. What the flip needs from this
        /// seam is therefore the <i>room's own</i> query service, not the chain mirror's sweep.</para>
        ///
        /// <para><b>Room identity, not just a query.</b> AS3 binds a thrown object to one
        /// <c>Location</c> for its whole life — <c>loc.getAbsTile</c>, <c>loc.spaceX</c> — and removes
        /// it when it leaves that room (<c>PhisBullet.as:234-238</c>). Resolving the room once, at
        /// spawn, is what makes that removal rule expressible; a world-wide query cannot say where
        /// "outside" begins.</para>
        ///
        /// <para>The returned service must not be cached across room streaming: it is created per
        /// room activation and dies with it. Resolve once per object at spawn, which is exactly the
        /// AS3 lifetime, and do not hold it past that.</para>
        /// </summary>
        /// <param name="worldPx">Query point, world pixels.</param>
        /// <param name="query">The room's tile seam and its world-pixel bounds.</param>
        bool TryGetRoomTileQueryAt(Vector2 worldPx, out RoomTileQuery query);
    }

    /// <summary>
    /// One mirrored room's tile seam together with the world-pixel rectangle it answers for.
    ///
    /// <para>The two travel together on purpose. A caller that needs <i>both</i> — a thrown object
    /// asking "is this cell solid" and "have I left the room" — must not derive the rectangle from
    /// <c>OriginPixel</c> a second time; that is precisely the kind of duplicated formula that let
    /// the chain mirror sit one room width out of place for the whole of Stage B (see
    /// <see cref="RoomChainGeometry.EmitChain"/>).</para>
    /// </summary>
    public readonly struct RoomTileQuery
    {
        /// <summary>The room's own query service. Valid only while the room stays mirrored.</summary>
        public readonly ITileQueryService Service;

        /// <summary>The room's world-pixel bounds — AS3 <c>loc.limX</c>/<c>limY</c>.</summary>
        public readonly Rect WorldBoundsPx;

        public RoomTileQuery(ITileQueryService service, Rect worldBoundsPx)
        {
            Service = service;
            WorldBoundsPx = worldBoundsPx;
        }
    }
}
