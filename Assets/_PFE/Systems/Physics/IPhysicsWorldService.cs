using UnityEngine;
using UnityEngine.LowLevelPhysics2D;
using PFE.Core;
using PFE.Systems.Map;
using PFE.Systems.Map.Streaming;

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
    }
}
