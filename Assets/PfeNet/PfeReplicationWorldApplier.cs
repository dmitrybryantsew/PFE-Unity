using System;
using System.Collections.Generic;
using PFE.Sim.State;
using PFE.Systems.Inventory;
using PFE.Systems.Map;
using PFE.Systems.Map.Rendering;
using PFE.Systems.Map.TileQuery;
using UnityEngine;

namespace PFE.Net
{
    /// <summary>
    /// Client-side projection of the host's reliable tile journal onto the visible world. This is the
    /// piece that turns "the client accepted a snapshot" into "both players see the same broken wall".
    ///
    /// <para><b>Why the snapshot's <c>TileMutations</c> and not a local damage path.</b> The host
    /// projects <c>SimTileJournal</c> — divergence from generated geometry — and repeats the
    /// unacknowledged contiguous run on every snapshot. Writing those records into the room's tile
    /// array is idempotent by value, so a repeated event is safe; the only thing that must not repeat
    /// is the visual rebuild, which is why fresh events are tracked individually and a room is
    /// notified at most once per applied batch.</para>
    ///
    /// <para><b>Why a set and not a high-water mark.</b> Rooms stream in and out. If an event for a
    /// room that has not arrived yet advanced the cursor, that geometry could never be applied later:
    /// the host keeps repeating the whole journal but every repeat would look stale. Leaving an
    /// unapplied sequence in the set lets a late room pick up its own events on a later snapshot.</para>
    ///
    /// <para><b>Known feedback.</b> <c>RoomInstance.NotifyTilesMutated</c> is the only public seam that
    /// makes the renderer and the collider chain rebuild, and a client's own
    /// <c>SimTileJournalHost</c> also subscribes to it. A client therefore records replicated events
    /// in its local journal too. That journal is never published — state flows host→client only in
    /// this build — so it cannot echo back, but it does mean a client's <c>RecordedTileEventCount</c>
    /// is not a measure of local destruction. F-12's reconciliation pass must add a suppression flag
    /// when a client is allowed to predict its own tile damage.</para>
    ///
    /// <para><b>Two halves, applied in this order.</b> The per-room authoritative sets
    /// (<c>LootedObjects</c>) come first, because they are full replaces that must land on a tick with
    /// an empty journal — the tick a chest is opened is exactly that tick. The reliable tile journal
    /// comes second, unchanged. Both are idempotent by value, so a repeated snapshot is harmless.</para>
    /// </summary>
    /// </summary>
    public sealed class PfeReplicationWorldApplier
    {
        private struct CellBounds
        {
            public bool Has;
            public int MinX;
            public int MinY;
            public int MaxX;
            public int MaxY;

            public void Add(int x, int y)
            {
                if (!Has)
                {
                    Has = true;
                    MinX = MaxX = x;
                    MinY = MaxY = y;
                    return;
                }
                if (x < MinX) MinX = x;
                if (y < MinY) MinY = y;
                if (x > MaxX) MaxX = x;
                if (y > MaxY) MaxY = y;
            }

            public RectInt ToRect()
            {
                return new RectInt(MinX, MinY, MaxX - MinX + 1, MaxY - MinY + 1);
            }
        }

        private readonly Dictionary<ulong, RoomInstance> _rooms = new Dictionary<ulong, RoomInstance>();
        private readonly Dictionary<ulong, UnifiedTileQueryService> _queries =
            new Dictionary<ulong, UnifiedTileQueryService>();
        private readonly Dictionary<ulong, CellBounds> _bounds = new Dictionary<ulong, CellBounds>();
        private readonly HashSet<uint> _appliedSequences = new HashSet<uint>();
        private readonly List<TileMutation> _scratch = new List<TileMutation>(32);

        /// <summary>
        /// Room hash → <c>ObjectInstance.spawnIndex</c> → the object. Rebuilt by
        /// <see cref="RefreshRooms"/> rather than scanned per snapshot: the opened-container set is
        /// re-sent for every room on every snapshot, so a linear scan would be O(objects) per room per
        /// tick for a lookup the room's own indices answer directly.
        ///
        /// <para><b>Staleness is the same staleness the room table already has.</b> An object added
        /// after the last <see cref="RefreshRooms"/> is not in this map until the next refresh, which
        /// is exactly when a room's <c>objects</c> list is rebuilt. A miss is counted, never guessed.</para>
        /// </summary>
        private readonly Dictionary<ulong, Dictionary<int, ObjectInstance>> _objectsBySpawn =
            new Dictionary<ulong, Dictionary<int, ObjectInstance>>();

        private bool _hasRevision;
        private long _revision;

        // RefreshRooms dirty-check state: same LandMap instance, same room count and same total
        // object count means the projection tables cannot have changed, so the steady-state cost of
        // a per-snapshot refresh is three comparisons, not a table rebuild.
        private LandMap _cachedMap;
        private int _cachedRoomCount;
        private int _cachedTotalObjects;

        public int AppliedEvents { get; private set; }
        public int AppliedCells { get; private set; }
        public int SkippedUnknownRoom { get; private set; }
        public int SkippedNullTile { get; private set; }
        public int RebuildCount { get; private set; }
        public int KnownRoomCount => _rooms.Count;

        /// <summary>How many opened-container records were written into the local world.</summary>
        public int AppliedLootedObjects { get; private set; }

        /// <summary>How many drop records the reconciler created on this machine (host drops the local
        /// world did not have). The readback that separates "the host spilled loot" from "the client
        /// never applied it".</summary>
        public int SpawnedReplicatedDrops { get; private set; }

        /// <summary>How many drop records the reconciler removed because the wire set no longer
        /// reports them (taken or despawned on the host).</summary>
        public int RemovedReplicatedDrops { get; private set; }

        /// <summary>How many kept records had a field or position update applied.</summary>
        public int UpdatedReplicatedDrops { get; private set; }

        /// <summary>How many wire drops could not be mapped back to a content id. Non-zero means the
        /// two peers run different content builds — the exact case the world-contract digest exists
        /// to catch before this point.</summary>
        public int SkippedUnknownDropContent { get; private set; }

        /// <summary>How many per-room drop sets were refused as malformed (zero or duplicate entity
        /// hash). The whole set is skipped, never half-applied.</summary>
        public int RefusedMalformedDropSets { get; private set; }

        /// <summary>How many were dropped because the room or the <c>spawnIndex</c> was not in the live
        /// world. Non-zero with a healthy wire means the two machines generated different object sets
        /// for the same room — the container analogue of <see cref="LastUnknownRoomHash"/>.</summary>
        public int SkippedUnknownObject { get; private set; }

        /// <summary>How many door-state records were applied to a live door presenter on this machine.
        /// The client-side readback for the door half: a host-toggled door must move this counter on
        /// every peer in the room.</summary>
        public int AppliedDoorStates { get; private set; }

        /// <summary>How many door-state records named an object with no live door presenter (visuals
        /// not built here). Counted, never guessed — the next room rebuild picks the state up from
        /// the record, exactly like the looted flag.</summary>
        public int SkippedDoorNoPresenter { get; private set; }

        /// <summary>
        /// The room hash of the last tile event nobody could place. A client that retries the same
        /// events forever (<c>unknownRoom=36923</c> in the fourth play-test) needs to say WHICH room
        /// the host is addressing: a zero hash means an unseeded id, a real but foreign hash means the
        /// two worlds generated different room identities.
        /// </summary>
        public ulong LastUnknownRoomHash { get; private set; }

        /// <summary>Rebuild the room-hash table from the live map. Room ids are stable within a
        /// session, but rooms stream in and out, so the table is refreshed before each apply rather
        /// than cached once at join.</summary>
        /// <remarks>Dirty-checked: only the LandMap identity and the (room, object) counts decide.
        /// Rebuilding allocates nothing in steady state — the inner per-room dictionaries are
        /// cleared and repopulated in place.</remarks>
        public void RefreshRooms(LandMap map)
        {
            if (map == null)
            {
                _rooms.Clear();
                _queries.Clear();
                _objectsBySpawn.Clear();
                _cachedMap = null;
                _cachedRoomCount = 0;
                _cachedTotalObjects = 0;
                return;
            }

            int roomCount = 0;
            int totalObjects = 0;
            foreach (RoomInstance r in map.GetAllRooms())
            {
                if (r == null || string.IsNullOrEmpty(r.id)) continue;
                roomCount++;
                totalObjects += r.objects?.Count ?? 0;
            }

            // Fast path: 0 allocations in steady-state play.
            if (ReferenceEquals(map, _cachedMap) && roomCount == _cachedRoomCount
                && totalObjects == _cachedTotalObjects)
            {
                return;
            }

            _cachedMap = map;
            _cachedRoomCount = roomCount;
            _cachedTotalObjects = totalObjects;

            _rooms.Clear();
            foreach (RoomInstance room in map.GetAllRooms())
            {
                if (room == null || string.IsNullOrEmpty(room.id)) continue;
                ulong hash = SimSnapshotProjectionRules.RoomKey(room.id);
                _rooms[hash] = room;

                Dictionary<int, ObjectInstance> byIndex;
                if (!_objectsBySpawn.TryGetValue(hash, out byIndex))
                {
                    byIndex = new Dictionary<int, ObjectInstance>(room.objects?.Count ?? 0);
                    _objectsBySpawn[hash] = byIndex;
                }
                PopulateBySpawnIndex(room, byIndex);
            }
        }

        /// <summary>
        /// The room's objects keyed by <c>spawnIndex</c>. Deliberately <b>not</b> keyed by list
        /// position: <c>RoomInstance.RemoveObject</c> closes the gap in <c>objects</c>, so a position
        /// shifts when any earlier object is removed and the replicated set would land on the wrong
        /// prop. An object with a negative index was never added through <c>AddObject</c> and is
        /// unreachable by design — the host cannot address it either.
        /// </summary>
        private static void PopulateBySpawnIndex(RoomInstance room, Dictionary<int, ObjectInstance> byIndex)
        {
            byIndex.Clear();
            List<ObjectInstance> objects = room.objects;
            if (objects == null) return;

            for (int i = 0; i < objects.Count; i++)
            {
                ObjectInstance obj = objects[i];
                if (obj == null || obj.spawnIndex < 0) continue;
                byIndex[obj.spawnIndex] = obj;
            }
        }

        /// <summary>Apply the not-yet-applied part of one accepted snapshot: first the per-room
        /// authoritative sets, then the reliable tile journal.</summary>
        public void Apply(in SimSnapshot snapshot)
        {
            if (!_hasRevision || snapshot.WorldRevision != _revision)
            {
                _hasRevision = true;
                _revision = snapshot.WorldRevision;
                _appliedSequences.Clear();
            }

            // Room-scoped sets are full replaces and must be applied even on a tick with an empty
            // journal — the tick somebody opened a chest is exactly such a tick. Returning early on
            // the journal here would drop that flag forever, because the host only repeats it while
            // the *room* is on the wire, not while the journal is non-empty.
            //
            // Drops come before the looted flags: the world gains the spilled items first, then the
            // chest marks itself opened. Both are idempotent full replaces, so the order only decides
            // which half an operator reads about first.
            ApplyDroppedItems(snapshot.Rooms);
            ApplyLootedObjects(snapshot.Rooms);
            ApplyDoorStates(snapshot.Rooms);

            SimTileMutation[] journal = snapshot.TileMutations;
            if (journal == null || journal.Length == 0) return;

            for (int i = 0; i < journal.Length; i++)
            {
                SimTileMutation mutation = journal[i];
                if (_appliedSequences.Contains(mutation.EventSequence)) continue;

                RoomInstance room;
                if (!_rooms.TryGetValue(mutation.RoomHash, out room) || room == null)
                {
                    SkippedUnknownRoom++;
                    LastUnknownRoomHash = mutation.RoomHash;
                    continue;
                }

                TileData tile = room.GetTileAtCoord(new Vector2Int(mutation.TileX, mutation.TileY));
                if (tile == null)
                {
                    SkippedNullTile++;
                    continue;
                }

                ulong hash = mutation.RoomHash;
                UnifiedTileQueryService query;
                if (!_queries.TryGetValue(hash, out query))
                {
                    query = new UnifiedTileQueryService(room);
                    _queries[hash] = query;
                }

                _scratch.Clear();
                _scratch.Add(new TileMutation(
                    new Vector2Int(mutation.TileX, mutation.TileY),
                    (TilePhysicsType)mutation.PhysicsType,
                    mutation.HitPoints));
                query.ApplyMutations(_scratch.ToArray());

                CellBounds bounds;
                _bounds.TryGetValue(hash, out bounds);
                bounds.Add(mutation.TileX, mutation.TileY);
                _bounds[hash] = bounds;

                _appliedSequences.Add(mutation.EventSequence);
                AppliedEvents++;
                AppliedCells++;
            }

            if (_bounds.Count == 0) return;

            foreach (KeyValuePair<ulong, CellBounds> entry in _bounds)
            {
                RoomInstance room;
                if (!_rooms.TryGetValue(entry.Key, out room) || room == null || !entry.Value.Has) continue;
                room.NotifyTilesMutated(entry.Value.ToRect());
                RebuildCount++;
            }
            _bounds.Clear();
        }

        /// <summary>
        /// Write the host's floor-loot sets into the local world — the half that makes a chest somebody
        /// else opened spill its items on this screen too.
        ///
        /// <para><b>Full replace, per room present on the wire.</b> The wire set is the room's whole drop
        /// population, so a local record the host no longer reports is GONE (taken or despawned on the
        /// host) and a wire entry the room does not hold is NEW. The plan itself is pure
        /// (<see cref="SimDroppedItemSetRules"/>); this method owns every Unity touch.</para>
        ///
        /// <para><b>Identity is the wire hash, verbatim.</b> A replicated record is created with
        /// <c>entityHash = SimDroppedItem.EntityHash</c> and <see cref="RoomInstance.AddDroppedItem"/>
        /// keeps an existing hash, so the next snapshot's reconcile keys on the same id the host minted.
        /// Views are built through <see cref="WorldItemPickup.RestoreForRoom"/> with
        /// <c>replicatedViews: true</c> — the same data path a room rebuild uses, so a mirror view
        /// behaves exactly like a restored one except that it refuses collection.</para>
        ///
        /// <para><b>Host truth wins.</b> A drop this machine spawned on its own (its own kills, an
        /// unguarded local path) that the host does not report is removed. That is the authority model:
        /// the floor is the host's state, and a client that originated floor records is exactly the
        /// double-loot class this slice removes.</para>
        /// </summary>
        private void ApplyDroppedItems(SimRoomDelta[] rooms)
        {
            if (rooms == null || rooms.Length == 0) return;

            for (int r = 0; r < rooms.Length; r++)
            {
                SimDroppedItem[] wire = rooms[r].DroppedItems ?? Array.Empty<SimDroppedItem>();

                RoomInstance room;
                if (!_rooms.TryGetValue(rooms[r].RoomHash, out room) || room == null)
                {
                    // Same rule as the tile journal: a miss is counted by the caller's room table and
                    // never guessed. Drops for a room we do not have keep no state on this machine.
                    continue;
                }

                string refuseReason;
                if (!SimDroppedItemSetRules.TryValidateWireSet(wire, out refuseReason))
                {
                    // A set with no or duplicate identities cannot be applied without inventing one;
                    // skip the whole room set rather than half-apply it.
                    RefusedMalformedDropSets++;
                    continue;
                }

                ReconcileRoomDrops(room, wire);
            }
        }

        private void ReconcileRoomDrops(RoomInstance room, SimDroppedItem[] wire)
        {
            List<DroppedItemInstance> records = room.droppedItems;
            int recordCount = records?.Count ?? 0;

            var localHashes = new ulong[recordCount];
            var recordByHash = new Dictionary<ulong, DroppedItemInstance>(recordCount);
            for (int i = 0; i < recordCount; i++)
            {
                DroppedItemInstance record = records[i];
                ulong hash = record != null ? record.entityHash : 0UL;
                localHashes[i] = hash;
                if (record != null && hash != 0UL) recordByHash[hash] = record;
            }

            SimDroppedItemSetRules.Plan plan = SimDroppedItemSetRules.ComputePlan(wire, localHashes);

            // ── Removals ──────────────────────────────────────────────────────
            // Collect before acting: Despawn mutates the very list the indices address.
            for (int i = 0; i < plan.RemovedLocalIndices.Length; i++)
            {
                int localIndex = plan.RemovedLocalIndices[i];
                if (localIndex < 0 || localIndex >= recordCount) continue;

                DroppedItemInstance record = records[localIndex];
                if (record == null) continue;

                WorldItemPickup view;
                if (WorldItemPickup.TryGetLiveView(record, out view) && view != null)
                {
                    view.Despawn();
                }
                else
                {
                    // A record with no view (content that never resolved) still has to leave the room.
                    room.RemoveDroppedItem(record);
                }
                RemovedReplicatedDrops++;
            }

            // ── Additions ─────────────────────────────────────────────────────
            bool anyAdded = false;
            for (int i = 0; i < plan.AddedWireIndices.Length; i++)
            {
                SimDroppedItem drop = wire[plan.AddedWireIndices[i]];

                string id;
                if (!SimLootHashContext.TryGetId(drop.ItemIdHash, drop.IsWeapon, out id))
                {
                    // The hash names content this build does not carry — a content mismatch the world
                    // contract exists to catch earlier. Counted, never guessed into a definition.
                    SkippedUnknownDropContent++;
                    continue;
                }

                var record = new DroppedItemInstance
                {
                    itemId = id,
                    isWeapon = drop.IsWeapon,
                    quantity = drop.Quantity,
                    variant = drop.Variant,
                    condition = drop.Condition,
                    // The wire set does not carry the flag; container spills and death drops are
                    // auto-true in the oracle (only player drops and room placements pass false), so a
                    // replicated drop takes the conservative LootGen default. Slice 1 makes
                    // replicated views non-collectable anyway.
                    autoCollect = true,
                    position = new Vector2(drop.Px, drop.Py),
                    entityHash = drop.EntityHash,
                };
                // AddDroppedItem keeps an existing hash (DroppedItemIdentityRules), so the record
                // keeps the identity the host minted and the next reconcile keys on it.
                room.AddDroppedItem(record);
                anyAdded = true;
                SpawnedReplicatedDrops++;
            }

            if (anyAdded)
            {
                // One build pass over the room spawns a view for every record that lacks one — the
                // new records, plus any earlier record whose content has since resolved.
                WorldItemPickup.RestoreForRoom(room, replicatedViews: true);
            }

            // ── Kept records: mirror field and position updates ───────────────
            for (int i = 0; i < plan.KeptWireIndices.Length; i++)
            {
                SimDroppedItem drop = wire[plan.KeptWireIndices[i]];

                DroppedItemInstance record;
                if (!recordByHash.TryGetValue(drop.EntityHash, out record) || record == null) continue;

                bool changed = false;
                if (record.quantity != drop.Quantity) { record.quantity = drop.Quantity; changed = true; }
                if (record.variant != drop.Variant) { record.variant = drop.Variant; changed = true; }
                if (!Mathf.Approximately(record.condition, drop.Condition))
                {
                    record.condition = drop.Condition;
                    changed = true;
                }

                Vector2 wirePosition = new Vector2(drop.Px, drop.Py);
                if (((Vector2)record.position - wirePosition).sqrMagnitude > 0.01f)
                {
                    record.position = wirePosition;

                    // The host's item is falling or was magnetised; the mirror does not integrate
                    // either, so it snaps to the wire position instead.
                    WorldItemPickup view;
                    if (WorldItemPickup.TryGetLiveView(record, out view) && view != null)
                    {
                        view.SyncViewToRecord(room);
                    }
                    changed = true;
                }

                if (changed) UpdatedReplicatedDrops++;
            }
        }

        /// <summary>
        /// Write the host's opened-container sets into the local world — the half that makes a chest
        /// somebody else already emptied stay emptied.
        ///
        /// <para><b>Full replace, per room present on the wire.</b> The host sends the whole opened set
        /// for a room, so writing each member and clearing nothing is not enough: a container the host
        /// no longer reports must be closed again, or a regenerated room would keep a stale flag from
        /// its previous life. The set is therefore treated as authoritative for the room rather than
        /// as a list of changes — which is also why this is idempotent and needs no sequence.</para>
        ///
        /// <para><b>What this does NOT do.</b> It does not roll or spawn contents: those are derived on
        /// this machine from <c>(roomKey, spawnIndex)</c> at the moment the player opens the chest, and
        /// the host's <c>DroppedItems</c> set is what actually replicates the items. It also does not
        /// force a presenter rebuild, because no object-state notification seam exists yet — the flag
        /// is live immediately (so <c>ContainerPropPresenter.IsLooted</c> refuses the interact) but the
        /// looted sprite appears when the room next streams in. That is a presentation lag, not a
        /// divergence, and it is recorded here rather than papered over.</para>
        /// </summary>
        private void ApplyLootedObjects(SimRoomDelta[] rooms)
        {
            if (rooms == null || rooms.Length == 0) return;

            for (int r = 0; r < rooms.Length; r++)
            {
                SimObjectState[] opened = rooms[r].LootedObjects;
                if (opened == null || opened.Length == 0) continue;

                Dictionary<int, ObjectInstance> byIndex;
                if (!_objectsBySpawn.TryGetValue(rooms[r].RoomHash, out byIndex) || byIndex == null)
                {
                    SkippedUnknownObject += opened.Length;
                    continue;
                }

                for (int o = 0; o < opened.Length; o++)
                {
                    ObjectInstance obj;
                    if (!byIndex.TryGetValue(opened[o].ObjectIndex, out obj) || obj?.runtimeState == null)
                    {
                        SkippedUnknownObject++;
                        continue;
                    }

                    if (obj.runtimeState.lootState == opened[o].LootState) continue;

                    obj.runtimeState.lootState = opened[o].LootState;
                    AppliedLootedObjects++;
                }
            }
        }

        /// <summary>
        /// Write the host's door states into the local presenters — the receiving half of
        /// <c>SimRoomDelta.DoorStates</c>.
        ///
        /// <para><b>Full replace, per room present on the wire.</b> The set carries EVERY door box with
        /// its state, so "the host closed it" is as explicit as "the host opened it". Application is
        /// presentation-only (<see cref="DoorPropPresenter.ApplyNetworkState"/>): record, solid
        /// collider, sprite. The covered tiles arrive in the same snapshot through the reliable
        /// journal — a door state deliberately does NOT restamp them, or the two writers would race
        /// inside one apply.</para>
        ///
        /// <para><b>A missing presenter is a presentation lag, not a divergence.</b> The state is the
        /// <c>ObjectInstance</c> record, which lives as long as the room; a door whose visuals were
        /// never built here (streamed-out) still has its flag updated below, so the presenter that
        /// builds later reads the right state from its own Initialize.</para>
        /// </summary>
        private void ApplyDoorStates(SimRoomDelta[] rooms)
        {
            if (rooms == null || rooms.Length == 0) return;

            for (int r = 0; r < rooms.Length; r++)
            {
                SimDoorState[] doors = rooms[r].DoorStates;
                if (doors == null || doors.Length == 0) continue;

                Dictionary<int, ObjectInstance> byIndex;
                if (!_objectsBySpawn.TryGetValue(rooms[r].RoomHash, out byIndex) || byIndex == null)
                {
                    SkippedUnknownObject += doors.Length;
                    continue;
                }

                for (int d = 0; d < doors.Length; d++)
                {
                    ObjectInstance obj;
                    if (!byIndex.TryGetValue(doors[d].ObjectIndex, out obj) || obj?.runtimeState == null)
                    {
                        SkippedUnknownObject++;
                        continue;
                    }

                    // The record is the authority even when the presenter is absent: a later build
                    // reads it from Initialize, which is what makes this a full replace rather than
                    // a frame-by-frame visual patch.
                    if (obj.runtimeState.isOpen != doors[d].IsOpen)
                    {
                        obj.runtimeState.isOpen = doors[d].IsOpen;
                    }

                    DoorPropPresenter presenter;
                    if (!DoorPropPresenter.TryGetPresenter(obj, out presenter) || presenter == null)
                    {
                        SkippedDoorNoPresenter++;
                        continue;
                    }

                    if (presenter.ApplyNetworkState(doors[d].IsOpen))
                    {
                        AppliedDoorStates++;
                    }
                }
            }
        }
    }
}
