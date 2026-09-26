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
    }
}
