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
    /// Owns the LowLevelPhysics2D <see cref="PhysicsWorld"/> and manages chain-geometry mirrors
    /// of active rooms. One world per active room set (decision L1).
    ///
    /// <para>Stage B: chain geometry exists, zero consumers. The world is stepped from
    /// <see cref="SimLoop"/> via <see cref="ISimTickable"/>, never from render time.</para>
    ///
    /// <para>Lifecycle: <see cref="MapBridge"/> calls <see cref="SubscribeToRoomEvents"/> after
    /// creating the <see cref="RoomStreamingManager"/>, wiring room activation/deactivation to
    /// geometry build/destroy.</para>
    ///
    /// <para><see cref="System.IDisposable"/> matters: <c>PhysicsConstants.MaxWorlds</c> caps how
    /// many worlds may exist at once, so a service that is rebuilt (domain reload, scope rebuild,
    /// test fixture) without releasing its world eventually fails for a reason unrelated to
    /// anything under test.</para>
    /// </summary>
    public sealed class PhysicsWorldService : IPhysicsWorldService, ISimTickable, System.IDisposable
    {
        private readonly PhysicsWorld _world;
        private readonly Dictionary<RoomInstance, RoomChainGeometry> _roomGeometry = new Dictionary<RoomInstance, RoomChainGeometry>();
        private readonly float _stepSeconds;

        public PhysicsWorld World => _world;
        public bool IsWorldValid => _world.isValid;

        /// <summary>
        /// Stepped in the <see cref="SimTickOrder.Projectiles"/> slot — the first intended consumer.
        ///
        /// <para><b>Open for Stage C.</b> A Box2D tick is write → step → read, but a single
        /// <see cref="ISimTickable"/> occupies one slot, so today the write and the read would have
        /// to share <c>Projectiles</c> and be ordered only by registration order. When the first
        /// real consumer lands this needs to become an explicit split (bodies written before the
        /// step, positions read after). It is left as-is now because Stage B has zero consumers, and
        /// guessing a second order constant would silently become load-bearing.</para>
        /// </summary>
        public int TickOrder => SimTickOrder.Projectiles;

        public PhysicsWorldService()
        {
            _stepSeconds = 1f / SimClock.CanonicalTicksPerSecond;

            PhysicsWorldDefinition def = PhysicsWorldDefinition.defaultDefinition;
            def.simulateType = PhysicsWorld.SimulationType.Script;
            def.simulationWorkers = 1;
            def.continuousAllowed = true;
            // AS3: World.as:46 ddy = 1 px/frame², expressed as units/s² (9.0) by the canonical
            // conversion — see TileQueryConstants.GravityUnitsPerSecondSquared. Derived, never a
            // literal: this is the one place the Box2D world's gravity comes from.
            def.gravity = new Vector2(0f, -TileQueryConstants.GravityUnitsPerSecondSquared);

            _world = PhysicsWorld.Create(def);
            if (!_world.isValid)
            {
                throw new System.InvalidOperationException("PhysicsWorld.Create returned an invalid world.");
            }
        }

        /// <summary>
        /// Subscribe to <see cref="RoomStreamingManager"/> events so room activation/deactivation
        /// drives geometry build/destroy. Called by <see cref="MapBridge"/> after the manager is
        /// created.
        /// </summary>
        public void SubscribeToRoomEvents(RoomStreamingManager streamingManager)
        {
            if (streamingManager == null) return;

            streamingManager.OnRoomActivated += OnRoomActivated;
            streamingManager.OnRoomDeactivated += OnRoomDeactivated;
        }

        public void BuildRoomGeometry(RoomInstance room)
        {
            if (room == null || !_world.isValid) return;

            // Idempotent: destroy existing geometry for this room first. That also unsubscribes,
            // so the subscribe below cannot accumulate duplicate handlers across rebuilds.
            DestroyRoomGeometry(room);

            ITileQueryService query = new UnifiedTileQueryService(room);
            var geometry = new RoomChainGeometry(query, _world);
            geometry.Build();
            _roomGeometry[room] = geometry;

            // Keep the mirror in sync with tile destruction. RoomInstance is the one object the
            // visual side (TileCollider) and this service already share, so the notification needs
            // no new plumbing and no query service on the collider.
            room.TilesMutated += OnRoomTilesMutated;
        }

        public void DestroyRoomGeometry(RoomInstance room)
        {
            if (room == null) return;

            if (_roomGeometry.TryGetValue(room, out RoomChainGeometry geometry))
            {
                // Unsubscribe before releasing: the handler rebuilds exactly the geometry being
                // destroyed. Safe even when raised from inside the event — a field-like event
                // snapshots its invocation list, so this affects the next raise, not the current.
                room.TilesMutated -= OnRoomTilesMutated;

                geometry.Destroy();
                _roomGeometry.Remove(room);
            }
        }

        public void RebuildRegion(RoomInstance room, RectInt tileRegion)
        {
            if (room == null) return;

            // Only rooms this service currently mirrors. Without this guard a stray notification
            // would resurrect geometry for a room that was deliberately released, or build it for
            // a room that was never activated — both of which would look like a leak rather than a
            // bug at the call site.
            if (!_roomGeometry.ContainsKey(room)) return;

            // Stage B: naive full rebuild — the region is accepted but not yet used.
            //
            // This is correct but coarse: destroying one tile destroys and re-creates every chain
            // in the room (O(room) chains, not O(affected)). Stage B has no consumers, so there is
            // no cost pressure yet, and a coarse rebuild cannot be subtly wrong the way a
            // partial one can.
            //
            // Stage C: rebuild only the chain segments intersecting tileRegion. The region
            // already carries a one-tile border (see RoomInstance.NotifyTilesMutated), which is
            // what that incremental version will need.
            BuildRoomGeometry(room);
        }

        public void SimTick(int tickIndex)
        {
            if (_world.isValid)
            {
                _world.Simulate(_stepSeconds);
            }
        }

        /// <summary>
        /// Releases every room's geometry and then the world itself. Idempotent.
        /// </summary>
        public void Dispose()
        {
            // Snapshot the keys: Destroy() removes from the dictionary.
            var rooms = new List<RoomInstance>(_roomGeometry.Keys);
            for (int i = 0; i < rooms.Count; i++)
            {
                DestroyRoomGeometry(rooms[i]);
            }
            _roomGeometry.Clear();

            if (_world.isValid)
            {
                // 0: the owner key. The world is never SetOwner'd, so the parameter is unused —
                // same convention as Llp2d.DestroyWorld.
                _world.Destroy(0);
            }
        }

        // ── Event handlers ───────────────────────────────────────────────────────────────────

        private void OnRoomActivated(RoomInstance room)
        {
            BuildRoomGeometry(room);
        }

        private void OnRoomDeactivated(RoomInstance room)
        {
            DestroyRoomGeometry(room);
        }

        /// <summary>
        /// A tile in <paramref name="room"/> changed. Rebuild the affected chain geometry.
        /// </summary>
        private void OnRoomTilesMutated(RoomInstance room, RectInt tileRegion)
        {
            RebuildRegion(room, tileRegion);
        }
    }
}
