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

        /// <summary>
        /// The last draw-option value written to the world, so <see cref="ApplyDrawOptions"/> can skip
        /// redundant writes. Initialised to the constructor's value so the first tick does not write.
        /// </summary>
        private PhysicsWorld.DrawOptions _appliedDrawOptions = PhysicsWorld.DrawOptions.Off;

        public PhysicsWorld World => _world;
        public bool IsWorldValid => _world.isValid;

        /// <inheritdoc />
        public IReadOnlyDictionary<RoomInstance, RoomChainGeometry> MirroredRooms => _roomGeometry;

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

            // The ENGINE debug-draws this world by default, and it is not one of our channels, so
            // `col off` could never silence it — the "connected grey circles" along every mirrored
            // surface. Start Off unconditionally rather than reading settings here: this constructor
            // runs before the settings asset is necessarily loaded (and in test fixtures), and a world
            // that starts noisy and is quietened a tick later is worse than one that starts quiet.
            // SimTick applies the resolved value, so `col on physics` still turns it back on live.
            def.drawOptions = PhysicsWorld.DrawOptions.Off;

            _world = PhysicsWorld.Create(def);
            if (!_world.isValid)
            {
                throw new System.InvalidOperationException("PhysicsWorld.Create returned an invalid world.");
            }

            ApplyDrawOptions();
        }

        /// <summary>
        /// Push the current diagnostic state onto the world's built-in draw.
        ///
        /// <para><c>drawOptions</c> is a settable field on <see cref="PhysicsWorld"/> itself (not only
        /// on the definition), so this needs no world rebuild and takes effect on the next frame. The
        /// write is guarded by a comparison because <see cref="SimTick"/> runs every tick and the
        /// value changes only when someone types a command.</para>
        ///
        /// <para><b>Why the settings lookup is shared, and why that matters.</b> This reads the same
        /// <see cref="DebugOverlays.Settings"/> instance the console writes through
        /// (<c>DevConsoleColliderCommands</c> takes its settings from <c>DebugOverlays.Settings</c>
        /// too), so <c>col on physics</c> is visible here on the next tick. Reading a different
        /// <see cref="PfeDebugSettings"/> reference — an injected one, say — would make the console
        /// and this method disagree, which is the "toggle is on and nothing happens" failure this
        /// project keeps hitting.</para>
        ///
        /// <para><b>Known, deliberate coupling of the ON direction.</b> <see cref="SimTick"/> is the
        /// only per-frame hook this service has, and <c>SimLoop</c> gates dispatch on
        /// <c>PfeDebugSettings.SimTickEnabled</c>. So with that flag off, this method is never called
        /// and <c>col on physics</c> would not bring the engine draw back. The OFF direction is not
        /// affected — the constructor sets <c>Off</c> unconditionally, so the shipping default holds
        /// whatever the flag says. That asymmetry is accepted rather than papered over: with the sim
        /// tick disabled the world is not being stepped either, so a live re-enable of its debug draw
        /// has little to show. Both flags ship ON in <c>PfeDebugSettings.asset</c>.</para>
        /// </summary>
        private void ApplyDrawOptions()
        {
            if (!_world.isValid)
            {
                return;
            }

            PhysicsWorld.DrawOptions desired = PhysicsWorldDraw.For(
                DebugOverlays.IsOn(DebugOverlayChannel.LowLevelPhysics));

            if (desired == _appliedDrawOptions)
            {
                return;
            }

            _world.drawOptions = desired;
            _appliedDrawOptions = desired;
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
                // Before the step, so `col on physics` / `col off` is visible on the very next frame.
                // See PhysicsWorldDraw for why the engine's own draw needs switching at all.
                ApplyDrawOptions();
                _world.Simulate(_stepSeconds);
            }
        }

        /// <summary>
        /// Nearest tile contact along a swept box, across every room currently mirrored.
        ///
        /// <para>One world serves the whole active room set (decision L1) and each room keeps its own
        /// chains, so the search is per-room and then reduced to the closest hit. Rooms are few (one
        /// or two during a transition) and each sweep is AABB-filtered, so this stays cheap enough to
        /// call once per projectile per tick.</para>
        ///
        /// <para>Rooms are compared by contact distance rather than by iteration order: dictionary
        /// order is not stable, and a projectile straddling a doorway would otherwise get whichever
        /// room happened to come first.</para>
        /// </summary>
        public bool TrySweepTiles(Vector2 fromPx, Vector2 deltaPx, Vector2 sizePx, Vector2 facing,
                                  out Vector2 contactPointPx, out Vector2 contactNormal)
        {
            contactPointPx = default;
            contactNormal  = default;

            if (!_world.isValid || _roomGeometry.Count == 0) return false;

            bool found = false;
            float bestDistanceSq = float.MaxValue;

            foreach (KeyValuePair<RoomInstance, RoomChainGeometry> entry in _roomGeometry)
            {
                if (!entry.Value.TrySweep(
                        fromPx, deltaPx, sizePx, facing, out Vector2 point, out Vector2 normal))
                {
                    continue;
                }

                float distanceSq = (point - fromPx).sqrMagnitude;
                if (distanceSq >= bestDistanceSq) continue;

                bestDistanceSq = distanceSq;
                contactPointPx = point;
                contactNormal  = normal;
                found          = true;
            }

            return found;
        }

        /// <summary>
        /// The tile seam and world-pixel bounds of the mirrored room containing a point.
        ///
        /// <para>Rooms are compared by <see cref="Rect.Contains"/>, whose maximum edges are
        /// <i>exclusive</i> — which is the convention AS3 uses too (<c>X &gt;= loc.spaceX * Tile.tileX</c>
        /// is outside, <c>PhisBullet.as:234</c>), so a point on the shared boundary between two rooms
        /// resolves to the lower one rather than to neither. At most one or two rooms are mirrored at
        /// a time (decision L1), so the scan is a couple of <c>Rect</c> tests.</para>
        ///
        /// <para>Returns false outside every mirrored room, which is the honest answer: the caller
        /// then has no tile semantics to consult and must not pretend otherwise. For a thrown object
        /// that means staying on the legacy path rather than registering a sim object that would fly
        /// through every wall.</para>
        /// </summary>
        public bool TryGetRoomTileQueryAt(Vector2 worldPx, out RoomTileQuery query)
        {
            foreach (KeyValuePair<RoomInstance, RoomChainGeometry> entry in _roomGeometry)
            {
                if (entry.Value.WorldBoundsPx.Contains(worldPx))
                {
                    query = new RoomTileQuery(entry.Value.Query, entry.Value.WorldBoundsPx);
                    return true;
                }
            }

            query = default;
            return false;
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
