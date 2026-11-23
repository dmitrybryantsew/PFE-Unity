using System;
using System.Collections.Generic;
using PFE.Core;
using PFE.Entities.Units;
using PFE.Entities.Weapons;
using PFE.Sim.Content;
using PFE.Sim.Replication;
using PFE.Sim.State;
using PFE.Systems.Map;
using PFE.Systems.Map.Streaming;
using UnityEngine;
using EntityId = PFE.Core.Ids.EntityId;

namespace PFE.Systems.Replication
{
    /// <summary>
    /// Phase 3.3 (F-05 closure): the host-side producer of <see cref="SimSnapshot"/> — the first piece
    /// of shipping code that <b>reads</b> the live entity registry.
    ///
    /// <para><b>Why this class exists at all.</b> Phase 3.2 gave every live entity an id and registered
    /// it; rows 3.3 added the sequence/revision fields and the guards that reject a stale packet. None
    /// of that had a consumer: <c>RuntimeEntityIdContext.Registry</c> had no production reader,
    /// <c>IEntityRegistry.GetAll()</c> had no production caller, and no code built a
    /// <see cref="SimSnapshot"/>. A guard with no producer is a guard that can be wrong for a year
    /// without a symptom — this project's most expensive failure mode. The projector closes the loop:
    /// it enumerates the registry every tick and emits the payload the guards are written against.</para>
    ///
    /// <para><b>It runs last on purpose.</b> <see cref="SimTickOrder.ViewAligned"/> is the final slot,
    /// so the snapshot describes the tick's settled state rather than a half-applied one. A projector in
    /// the <c>Projectiles</c> slot would sample before damage resolution and report a round that has
    /// already hit something as still in flight.</para>
    ///
    /// <para><b>Two sources, because entities come in two shapes.</b> <i>Transient</i> entities — a
    /// fired round, a thrown object, a placed mine — are found through
    /// <c>RuntimeEntityIdContext.Registry.GetAll()</c>: none of them has a room record, so the registry
    /// is the only place they exist, and all three reach this class through
    /// <see cref="ISimTransientStateSource"/> rather than three parallel lists. <i>Units</i> are found
    /// through <see cref="UnitViewRegistry"/>, because the registry is keyed on the <c>UnitInstance</c>
    /// record (row 3.2's decision) and nothing walks from a record back to its controller. Both are
    /// filtered to entities that actually carry an id, and both are sorted by <c>EntityHash</c>:
    /// <c>Dictionary</c> enumeration order is unspecified, and two hosts projecting the same state must
    /// produce byte-identical payloads or the state hash is a coin flip.</para>
    ///
    /// <para><b>Phase 4 connects it to the loop through one interface, not through a transport.</b>
    /// This projector implements <see cref="ISimReplicationSnapshotSource"/>, so
    /// <c>SimReplicationHost</c> can read the snapshot and the reliable journal without knowing that a
    /// <c>MonoBehaviour</c>-adjacent scene walker produced them. The wire itself is a separate
    /// <see cref="ISimReplicationWire"/>; a FishNet adapter lives in the view assembly and is owed to
    /// the owner. Nothing here names FishNet, a socket or a channel — the offline wall must still be
    /// able to execute the whole loop with an in-process loopback wire.</para>
    /// </summary>
    public sealed class SimProjectileSnapshotProjector : IAutoRegisteredSimTickable, ISimReplicationSnapshotSource
    {
        /// <summary>Deterministic order — <c>Dictionary</c> enumeration order is unspecified.</summary>
        private static readonly Comparison<ISimTransientStateSource> ByTransientHash =
            (a, b) => a.SimEntityHash.CompareTo(b.SimEntityHash);

        /// <summary>Deterministic order for units, by the same key.</summary>
        private static readonly Comparison<UnitController> ByUnitHash =
            (a, b) => a.EntityId.Hash.CompareTo(b.EntityId.Hash);

        private readonly List<ISimTransientStateSource> _projectiles =
            new List<ISimTransientStateSource>(64);
        private readonly List<UnitController> _units = new List<UnitController>(64);
        private readonly List<RoomInstance> _rooms = new List<RoomInstance>(4);

        /// <summary>
        /// Deterministic order for loot: <c>List</c> order follows drop time, which differs between two
        /// hosts that dropped the same items in a different order. Keyed on the host-assigned id, which
        /// is the one thing that is the same on both sides.
        /// </summary>
        private static readonly Comparison<DroppedItemInstance> ByDroppedItemHash =
            (a, b) => a.entityHash.CompareTo(b.entityHash);

        /// <summary>
        /// Deterministic order for opened containers: <c>spawnIndex</c> ascending. The index is the
        /// receiver's key and the only ordering two hosts are guaranteed to share — <c>room.objects</c>
        /// order is not stable because <c>RemoveObject</c> closes gaps in it.
        /// </summary>
        private static readonly Comparison<ObjectInstance> ByObjectSpawnIndex =
            (a, b) => a.spawnIndex.CompareTo(b.spawnIndex);

        /// <summary>
        /// The tile journal and its room subscriptions — §10.2 gap 1's producer.
        ///
        /// <para><b>Constructed here rather than injected.</b> It has exactly one consumer, which is this
        /// class, and it owns scene state (subscriptions on live <c>RoomInstance</c> objects) that a
        /// container singleton would outlive. A DI registration would add a lifetime to reason about and
        /// buy nothing.</para>
        /// </summary>
        private readonly SimTileJournalHost _journalHost = new SimTileJournalHost();

        private readonly List<DroppedItemInstance> _droppedScratch = new List<DroppedItemInstance>(16);
        private readonly List<ObjectInstance> _objectScratch = new List<ObjectInstance>(16);

        /// <summary>
        /// The live-view list units are projected from — thread E's replacement for the retired static
        /// <c>UnitViewRegistry</c>. The container injects the session singleton; a null (a fixture that
        /// never supplies one) projects zero units rather than throwing.
        /// </summary>
        private readonly UnitViewRegistry _unitViews;

        /// <summary>
        /// The entity-id service the registry walk reads — thread E's instance-level replacement for
        /// the static <c>RuntimeEntityIdContext.Registry</c>. Optional for the same fixture reason as
        /// <paramref name="unitViews"/>; when null the walk falls back to
        /// <see cref="RuntimeEntityIdContext.Active"/>, which the lifetime scope's Bind points at the
        /// same singleton, so both paths enumerate one registry.
        /// </summary>
        private readonly RuntimeEntityIdService _entityIds;

        /// <summary>The service the registry walk enumerates — injected when present, ambient otherwise.</summary>
        private RuntimeEntityIdService EntityIds => _entityIds ?? RuntimeEntityIdContext.Active;

        /// <summary>How many live views the registry currently tracks — the net session's F-12 readback,
        /// replacing the static <c>UnitViewRegistry.Count</c> the overlay used to read.</summary>
        public int LiveUnitViewCount => _unitViews != null ? _unitViews.Count : 0;

        /// <summary>
        /// <paramref name="unitViews"/> and <paramref name="entityIds"/> are optional so the offline
        /// fixtures that build the projector around a fake loop keep compiling; production registers
        /// both as container singletons and VContainer fills them in.
        /// </summary>
        public SimProjectileSnapshotProjector(
            UnitViewRegistry unitViews = null,
            RuntimeEntityIdService entityIds = null)
        {
            _unitViews = unitViews;
            _entityIds = entityIds;
        }

        private SimTileMutation[] _tileJournal = Array.Empty<SimTileMutation>();
        private int _tileJournalVersion = -1;

        // The projected frame storage: two buffers alternate (see _frameFlip). While one frame is
        // published through <see cref="Current"/>, the other is being overwritten, so a reader that
        // held the previous SimSnapshot keeps valid data for one full extra tick — and the current
        // frame is what <c>SimReplicationHost</c> serializes, within the same tick it is published.
        // A frame's inner arrays are exactly the length of the count they carry (the arrays ARE the
        // wire truth the serializer counts) and are reused only when the next projection needs the
        // same length — steady state, where room/unit/projectile counts are stable tick to tick,
        // allocates nothing.
        private ProjectionFrame _frameEven = new ProjectionFrame();
        private ProjectionFrame _frameOdd = new ProjectionFrame();
        private bool _frameFlip;

        /// <summary>
        /// One tick's projected storage: the room-delta array plus the per-room inner arrays the
        /// deltas reference. Owned by the projector (never handed to a pool) so the frame is valid
        /// until the same buffer comes around again two ticks later; the jagged rows are grown, never
        /// shrunk, because a room-set change is the rare case and stale rows beyond the current room
        /// count are unreachable through <see cref="Current"/>.
        /// </summary>
        private sealed class ProjectionFrame
        {
            public SimRoomDelta[] Deltas = Array.Empty<SimRoomDelta>();
            public SimUnitState[][] Units = Array.Empty<SimUnitState[]>();
            public SimProjectileState[][] Projectiles = Array.Empty<SimProjectileState[]>();
            public SimDroppedItem[][] DroppedItems = Array.Empty<SimDroppedItem[]>();
            public SimObjectState[][] LootedObjects = Array.Empty<SimObjectState[]>();
            public SimDoorState[][] DoorStates = Array.Empty<SimDoorState[]>();
        }

        /// <summary>Reuses <paramref name="current"/> when its length already matches the count;
        /// otherwise allocates an exact-size array. Zero counts use the shared empty array, which is
        /// also what keeps an empty slot allocation-free.</summary>
        private static T[] ReuseOrCreate<T>(T[] current, int count)
        {
            if (count == 0) return Array.Empty<T>();
            return current != null && current.Length == count ? current : new T[count];
        }

        /// <summary>Grows a jagged slot array when the room count exceeded it. Rows beyond the old
        /// length start null and are allocated by <see cref="ReuseOrCreate{T}"/> on demand.</summary>
        private static T[][] GrowRows<T>(T[][] rows, int count)
        {
            if (rows.Length >= count) return rows;
            var grown = new T[count][];
            Array.Copy(rows, grown, rows.Length);
            return grown;
        }

        // NOT paired with a "resolved" flag: see ResolveStreaming(). MapBridge builds this scene
        // object at the END of a multi-second boot coroutine, long after this tickable's first tick.
        private RoomStreamingManager _streaming;

        private SimSnapshot _current;
        private uint _snapshotSequence;
        private long _worldRevision;
        private ulong _roomSetSignature = SimSnapshotProjectionRules.EmptyRoomSetSignature;
        private bool _hasProjected;

        private int _lastProjectileCount;
        private int _lastUnattributedProjectileCount;
        private int _lastSkippedNoIdProjectileCount;
        private int _lastUnitCount;
        private int _lastPlayerUnitCount;
        private int _lastUnattributedUnitCount;
        private int _lastUninternedDefinitionCount;
        private int _lastDroppedItemCount;
        private int _lastUnidentifiedDroppedItemCount;
        private int _lastLootedObjectCount;
        private int _lastUnaddressableLootedObjectCount;
        private int _lastDoorStateCount;
        private long _projectionCount;
        private uint _transientSequence;

        /// <summary>After every other system, so the snapshot is the tick's settled state.</summary>
        public int TickOrder => SimTickOrder.ViewAligned;

        /// <summary>The snapshot built by the last tick. Default until <see cref="SimTick"/> has run once.</summary>
        public SimSnapshot Current => _current;

        /// <summary>
        /// Whether the projector has found the scene's streaming manager. FALSE for the whole session
        /// is the failure the fourth play-test showed: <c>projUnits=0</c> on a healthy wire. The
        /// projector is an <see cref="IAutoRegisteredSimTickable"/> that <c>SimTickRegistrar</c>
        /// attaches at container build, so it ticks while the world is still being built.
        /// </summary>
        public bool HasStreaming => _streaming != null;

        /// <summary>How many rooms the last projection emitted deltas for. 0 means no unit can be sent.</summary>
        public int ActiveRoomCount => _rooms.Count;

        /// <summary>
        /// How many live units passed the <c>HasEntityId</c> filter on the last projection. A non-zero
        /// value here with <see cref="ProjectedUnitCount"/> == 0 means every unit failed the
        /// <c>ReferenceEquals(controller.Room, room)</c> attribution, not that the wire is broken.
        /// </summary>
        public int IdentifiedUnitCount => _units.Count;

        /// <summary>
        /// The producer's reliable tile stream, exposed so Phase 4's ack can reach
        /// <c>SimTileJournal.Acknowledge</c>. A host that only held the snapshot could acknowledge its
        /// own bytes but could never trim this journal, which would then grow for the whole session.
        /// </summary>
        public SimTileJournal Journal => _journalHost.Journal;

        /// <summary>Wrap-safe ordering of <see cref="Current"/> on the unreliable channel.</summary>
        public uint SnapshotSequence => _snapshotSequence;

        /// <summary>Reliable-event lifetime of <see cref="Current"/>; advances only on a room-set change.</summary>
        public long WorldRevision => _worldRevision;

        /// <summary>How many transient entities (rounds, throws, mines) the last projection emitted.</summary>
        public int ProjectedProjectileCount => _lastProjectileCount;

        /// <summary>
        /// How many registered projectiles had nowhere to go because no room was active. Non-zero in a
        /// test scene with no streaming manager; a non-zero value in a live session means a projectile
        /// outlived its room.
        /// </summary>
        public int UnattributedProjectileCount => _lastUnattributedProjectileCount;

        /// <summary>
        /// How many registry entries the projectile walk dropped because the source carried no valid
        /// id or a zero entity hash. The receiver refuses hash-0 records as malformed
        /// (<c>PfeRemoteProjectileRules.IsDisplayableRecord</c>), so a round counted here is a round
        /// NO client can ever see — play-test #7's "the host confirms the shot, nothing displays".
        /// Counted every tick rather than dropped silently.
        /// </summary>
        public int SkippedNoIdProjectileCount => _lastSkippedNoIdProjectileCount;

        /// <summary>How many units the last projection emitted.</summary>
        public int ProjectedUnitCount => _lastUnitCount;

        /// <summary>
        /// How many of the projected units are player-driven (<c>ControllerSlot &gt;= 0</c>). The
        /// readback for the one projection path that is <b>not</b> verified offline: the player's
        /// transform is not parented to a room root, so its projected position space is the first thing
        /// a play-test must check. 0 in a session where the player exists means nothing was projected.
        /// </summary>
        public int ProjectedPlayerUnitCount => _lastPlayerUnitCount;

        /// <summary>
        /// How many live units carried an id but could not be attributed to an active room, because
        /// their <c>Room</c> was null or was not in the active set. Reported rather than dropped
        /// silently: a unit that leaves the snapshot disappears on the client with no error.
        /// </summary>
        public int UnattributedUnitCount => _lastUnattributedUnitCount;

        /// <summary>
        /// How many projected entities carried <see cref="ContentInternTable.Uninterned"/> as their
        /// definition id. Non-zero means the intern table was built from a different content set than
        /// the one that spawned the entity — the failure that would otherwise render a plasma round as
        /// a pistol.
        /// </summary>
        public int UninternedDefinitionCount => _lastUninternedDefinitionCount;

        /// <summary>How many loot stacks the last projection emitted.</summary>
        public int ProjectedDroppedItemCount => _lastDroppedItemCount;

        /// <summary>
        /// How many live loot records were skipped because they carried no host-assigned id or no
        /// content id. Non-zero means a record reached a room without passing
        /// <c>AddDroppedItem</c> — a stack the client cannot see, which is otherwise completely silent.
        /// </summary>
        public int UnidentifiedDroppedItemCount => _lastUnidentifiedDroppedItemCount;

        /// <summary>How many opened-container records the last projection emitted, across every room.
        /// Zero on a fresh world is correct; zero after somebody opened a chest is the readback that
        /// says the host never wrote the flag.</summary>
        public int ProjectedLootedObjectCount => _lastLootedObjectCount;

        /// <summary>
        /// How many opened containers were skipped because their <c>spawnIndex</c> was negative — i.e.
        /// the object never went through <c>RoomInstance.AddObject</c>. Such a container cannot be
        /// addressed on the wire at all: the client would have no index to key it on, and the same
        /// chest would be opened twice. Counted rather than dropped silently.
        /// </summary>
        public int UnaddressableLootedObjectCount => _lastUnaddressableLootedObjectCount;

        /// <summary>How many door-state records the last projection emitted, across every room. The
        /// readback for the door half of the object-state replication: a toggled door that never
        /// moves this counter never left the host.</summary>
        public int ProjectedDoorStateCount => _lastDoorStateCount;

        /// <summary>How many reliable tile events the last snapshot carried.</summary>
        public int TileJournalCount => _tileJournal.Length;

        /// <summary>
        /// The journal's lifetime. Equal to <see cref="WorldRevision"/> after every tick; exposed
        /// separately because a mismatch is the failure that matters — a journal from an older lifetime
        /// is geometry the receiver will refuse.
        /// </summary>
        public long JournalWorldRevision => _journalHost.Journal.WorldRevision;

        /// <summary>
        /// How many tile events the producer has recorded in this lifetime, whether or not the last
        /// snapshot carried them. The readback for "the subscription is reaching the journal": a session
        /// where walls were destroyed and this stays 0 means the wiring is dead.
        /// </summary>
        public long RecordedTileEventCount => _journalHost.RecordedEventCount;

        /// <summary>How many projections have run. The readback for "this system is actually ticking".</summary>
        public long ProjectionCount => _projectionCount;

        /// <summary>
        /// Build one snapshot for this tick. Never throws: a missing streaming manager or an empty
        /// registry produces a valid, empty snapshot rather than skipping the tick, so
        /// <see cref="SnapshotSequence"/> stays a contiguous count of projections.
        /// </summary>
        public void SimTick(int tickIndex)
        {
            _snapshotSequence = SimSnapshotProjectionRules.NextSnapshotSequence(_snapshotSequence);

            RoomStreamingManager streaming = ResolveStreaming();

            _rooms.Clear();
            ulong signature = SimSnapshotProjectionRules.EmptyRoomSetSignature;

            if (streaming != null)
            {
                foreach (RoomInstance room in streaming.ActiveRooms)
                {
                    if (room == null) continue;
                    _rooms.Add(room);
                    signature = SimSnapshotProjectionRules.FoldRoomSetSignature(
                        signature, SimSnapshotProjectionRules.RoomKey(room.id));
                }
            }

            // First projection counts as a change: the revision names a lifetime, and "lifetime 0 with
            // state" is not a state a receiver can reason about.
            bool roomSetChanged = !_hasProjected || signature != _roomSetSignature;

            _worldRevision = SimSnapshotProjectionRules.NextWorldRevision(_worldRevision, roomSetChanged);
            _roomSetSignature = signature;
            _hasProjected = true;

            // The journal follows the projector's own active-room list, so the two cannot disagree
            // about which rooms are live. Seeding happens on activation; a new lifetime then re-emits
            // the divergence as its baseline — which is why the order here matters and the sync comes
            // first: the baseline must include the cells of the room that just arrived.
            _journalHost.Sync(_rooms);
            if (roomSetChanged)
            {
                _journalHost.BeginLifetime(_worldRevision);
            }

            RefreshTileJournalCache();

            CollectProjectiles();
            CollectUnits();

            int projectedProjectiles = 0;
            int projectedUnits = 0;
            int projectedPlayers = 0;
            int uninterned = 0;
            int projectedDroppedItems = 0;
            int projectedLootedObjects = 0;
            int projectedDoorStates = 0;
            _lastUnidentifiedDroppedItemCount = 0;
            _lastUnaddressableLootedObjectCount = 0;
            _lastSkippedNoIdProjectileCount = 0;

            // Write the projection into the idle frame buffer; the previously published frame stays
            // intact for any reader one tick behind (double buffering), and the current one is
            // serialized by the host within this same tick.
            ProjectionFrame frame = _frameFlip ? _frameOdd : _frameEven;
            _frameFlip = !_frameFlip;
            frame.Deltas = ReuseOrCreate(frame.Deltas, _rooms.Count);
            frame.Units = GrowRows(frame.Units, _rooms.Count);
            frame.Projectiles = GrowRows(frame.Projectiles, _rooms.Count);
            frame.DroppedItems = GrowRows(frame.DroppedItems, _rooms.Count);
            frame.LootedObjects = GrowRows(frame.LootedObjects, _rooms.Count);
            frame.DoorStates = GrowRows(frame.DoorStates, _rooms.Count);

            for (int i = 0; i < _rooms.Count; i++)
            {
                RoomInstance room = _rooms[i];

                // Projectiles are scene-scoped in this codebase — there is no per-room projectile list
                // — so they are attributed to the presentation room when it is active and to the first
                // active room otherwise. Any other choice would be a guess dressed as data.
                bool ownsProjectiles = streaming != null
                    && (ReferenceEquals(room, streaming.PresentationRoom)
                        || (i == 0 && streaming.PresentationRoom == null));

                SimProjectileState[] projectiles =
                    ReuseOrCreate(frame.Projectiles[i], ownsProjectiles ? _projectiles.Count : 0);
                frame.Projectiles[i] = projectiles;

                if (ownsProjectiles && _projectiles.Count > 0)
                {
                    for (int p = 0; p < _projectiles.Count; p++)
                    {
                        ISimTransientStateSource transient = _projectiles[p];

                        // One sample number per emitted record, owned by the projector: the sequence is
                        // the wrap-safe ordering on the unreliable channel, and it has to advance once
                        // per payload or a client cannot tell two samples apart.
                        _transientSequence = SimSnapshotProjectionRules.NextSnapshotSequence(_transientSequence);
                        SimProjectileState state = transient.CaptureSimState(_transientSequence);
                        projectiles[p] = state;

                        if (state.DefinitionId == ContentInternTable.Uninterned) uninterned++;
                    }

                    projectedProjectiles = _projectiles.Count;
                }

                // Units carry their own room: `UnitController.Room` resolves through the tile query, so
                // attribution is a reference match rather than a guess from position — a room-local
                // pixel coordinate cannot say which room it is local to.
                int unitCount = 0;
                for (int u = 0; u < _units.Count; u++)
                {
                    if (ReferenceEquals(_units[u].Room, room)) unitCount++;
                }

                SimUnitState[] units = ReuseOrCreate(frame.Units[i], unitCount);
                frame.Units[i] = units;

                if (unitCount > 0)
                {
                    int written = 0;

                    for (int u = 0; u < _units.Count; u++)
                    {
                        UnitController controller = _units[u];
                        if (!ReferenceEquals(controller.Room, room)) continue;

                        SimUnitState state = controller.CaptureSimState();
                        units[written++] = state;

                        if (state.ControllerSlot >= 0) projectedPlayers++;
                        if (state.DefinitionId == ContentInternTable.Uninterned) uninterned++;
                    }

                    projectedUnits += written;
                }

                SimDroppedItem[] droppedItems = ProjectDroppedItems(room, frame.DroppedItems[i]);
                frame.DroppedItems[i] = droppedItems;
                SimObjectState[] lootedObjects = ProjectLootedObjects(room, frame.LootedObjects[i]);
                frame.LootedObjects[i] = lootedObjects;
                SimDoorState[] doorStates = ProjectDoorStates(room, frame.DoorStates[i]);
                frame.DoorStates[i] = doorStates;

                frame.Deltas[i] = new SimRoomDelta
                {
                    RoomHash = SimSnapshotProjectionRules.RoomKey(room.id),
                    LifecycleSequence = streaming != null ? streaming.GetRoomLifecycleSequence(room) : 0,
                    Units = units,
                    Motors = Array.Empty<SimMotorState>(),
                    Projectiles = projectiles,
                    DroppedItems = droppedItems,
                    RemovedEntityHashes = Array.Empty<ulong>(),
                    LootedObjects = lootedObjects,
                    DoorStates = doorStates,
                };

                projectedDroppedItems += droppedItems.Length;
                projectedLootedObjects += lootedObjects.Length;
                projectedDoorStates += doorStates.Length;
            }

            _lastProjectileCount = projectedProjectiles;
            _lastUnattributedProjectileCount = _projectiles.Count - projectedProjectiles;
            _lastUnitCount = projectedUnits;
            _lastPlayerUnitCount = projectedPlayers;
            _lastUnattributedUnitCount = _units.Count - projectedUnits;
            _lastUninternedDefinitionCount = uninterned;
            _lastDroppedItemCount = projectedDroppedItems;
            _lastLootedObjectCount = projectedLootedObjects;
            _lastDoorStateCount = projectedDoorStates;

            _current = new SimSnapshot
            {
                ProtocolVersion = SimSnapshotSerializer.CurrentProtocolVersion,
                TickIndex = tickIndex,
                SnapshotSequence = _snapshotSequence,
                WorldRevision = _worldRevision,
                // A full baseline, not a delta against an acked one: delta compression is Phase 3.6 and
                // the field's contract is 0 for "no baseline".
                BaselineHash = 0UL,
                Rooms = frame.Deltas,
                // The whole reliable journal for this lifetime. Repeated in every snapshot until an ack
                // exists (Phase 4 owns the ack), which is why it is cached by journal version rather than
                // copied per tick: an unchanged journal must not cost an allocation per frame.
                TileMutations = _tileJournal,
            };

            _projectionCount++;
        }

        /// <summary>
        /// Rebuild the journal array only when the journal actually changed. The journal repeats in every
        /// snapshot until an ack exists, so an unchanged journal would otherwise be copied once per tick —
        /// and a quiet tick is the common case by a wide margin.
        /// </summary>
        private void RefreshTileJournalCache()
        {
            SimTileJournal journal = _journalHost.Journal;
            if (_tileJournalVersion == journal.Version)
            {
                return;
            }

            _tileJournal = journal.ToArray();
            _tileJournalVersion = journal.Version;
        }

        /// <summary>
        /// One room's floor loot, keyed by the host-assigned id — §10.2 gap 2's producing side.
        ///
        /// <para><b>Sorted by id, not by list order.</b> <c>droppedItems</c> is in drop order, which two
        /// hosts can legitimately differ on; the id is the only thing both sides agree on, and the state
        /// hash is a coin flip if the order is not pinned.</para>
        ///
        /// <para><b>A record with no id is skipped and counted.</b> It cannot be expressed on the wire —
        /// the receiver rejects a zero <c>EntityHash</c> — and dropping it silently would make a stack
        /// vanish from the client with no error anywhere. The counter is the only signal that a record
        /// bypassed <c>AddDroppedItem</c>.</para>
        ///
        /// <para><b>The destination array is caller-owned frame storage.</b> It is reused when its
        /// length matches this room's emitted count and replaced with an exact-size array when the
        /// count changed — the projector's double-buffered frames keep the steady-state tick free of
        /// loot-array allocations.</para>
        /// </summary>
        private SimDroppedItem[] ProjectDroppedItems(RoomInstance room, SimDroppedItem[] reuse)
        {
            List<DroppedItemInstance> records = room.droppedItems;
            if (records == null || records.Count == 0)
            {
                return Array.Empty<SimDroppedItem>();
            }

            _droppedScratch.Clear();

            for (int i = 0; i < records.Count; i++)
            {
                DroppedItemInstance record = records[i];
                if (record == null || string.IsNullOrEmpty(record.itemId))
                {
                    continue;
                }

                if (record.entityHash == 0UL)
                {
                    _lastUnidentifiedDroppedItemCount++;
                    continue;
                }

                _droppedScratch.Add(record);
            }

            if (_droppedScratch.Count == 0)
            {
                return Array.Empty<SimDroppedItem>();
            }

            if (_droppedScratch.Count > 1)
            {
                _droppedScratch.Sort(ByDroppedItemHash);
            }

            SimDroppedItem[] projected = ReuseOrCreate(reuse, _droppedScratch.Count);

            for (int i = 0; i < _droppedScratch.Count; i++)
            {
                DroppedItemInstance record = _droppedScratch[i];
                projected[i] = new SimDroppedItem
                {
                    EntityHash = record.entityHash,
                    ItemIdHash = SimSnapshotProjectionRules.LootItemKey(record.itemId, record.isWeapon),
                    IsWeapon = record.isWeapon,
                    Quantity = record.quantity,
                    Variant = record.variant,
                    Condition = record.condition,
                    Px = record.position.x,
                    Py = record.position.y,
                };
            }

            return projected;
        }

        /// <summary>
        /// One room's opened containers — the authoritative half of
        /// <c>ObjectInstance.runtimeState.lootState</c>, and the field that stops two peers from each
        /// emptying the same chest.
        ///
        /// <para><b>Why the host has to be the one that reports it.</b> Container <i>contents</i> are
        /// derived from <c>(roomKey, spawnIndex)</c> on both machines, so nothing needs sending for
        /// them. "Somebody already took it" is the one fact no derivation can produce, and without it
        /// each peer rolls and spawns its own copy of the same chest. Reading the live
        /// <see cref="ObjectInstance"/> means the flag is authoritative exactly when the loot path
        /// wrote it — no separate bookkeeping to fall out of sync.</para>
        ///
        /// <para><b>Sorted by <c>spawnIndex</c>, not by list order.</b> <c>room.objects</c> is in
        /// population order today, but <c>RemoveObject</c> closes gaps in it, so the list order is not
        /// stable within a session. The index is what the receiver keys on, and it is the only order
        /// two hosts are guaranteed to agree on — an unpinned order makes the state hash a coin
        /// flip.</para>
        ///
        /// <para><b>Only opened containers are emitted.</b> The set is a full replace per room, so
        /// listing every container with its flag would send the closed ones for no information: closed
        /// is the default on both sides. Only a positive <c>lootState</c> is a member.</para>
        ///
        /// <para><b>The destination array is caller-owned frame storage</b> — reused when its length
        /// matches the emitted count, replaced when the count changed (see
        /// <see cref="ProjectionFrame"/>).</para>
        /// </summary>
        private SimObjectState[] ProjectLootedObjects(RoomInstance room, SimObjectState[] reuse)
        {
            List<ObjectInstance> objects = room.objects;
            if (objects == null || objects.Count == 0)
            {
                return Array.Empty<SimObjectState>();
            }

            _objectScratch.Clear();

            for (int i = 0; i < objects.Count; i++)
            {
                ObjectInstance obj = objects[i];
                if (obj?.runtimeState == null || obj.runtimeState.lootState <= 0)
                {
                    continue;
                }

                if (obj.spawnIndex < 0)
                {
                    // No addressable identity: the receiver keys the set on spawnIndex, so emitting it
                    // would address whichever object happens to be at a defaulted index. Counted, so a
                    // container built outside AddObject is visible in the overlay instead of being a
                    // chest that mysteriously never stays open.
                    _lastUnaddressableLootedObjectCount++;
                    continue;
                }

                _objectScratch.Add(obj);
            }

            if (_objectScratch.Count == 0)
            {
                return Array.Empty<SimObjectState>();
            }

            if (_objectScratch.Count > 1)
            {
                _objectScratch.Sort(ByObjectSpawnIndex);
            }

            SimObjectState[] projected = ReuseOrCreate(reuse, _objectScratch.Count);

            for (int i = 0; i < _objectScratch.Count; i++)
            {
                ObjectInstance obj = _objectScratch[i];
                projected[i] = new SimObjectState
                {
                    ObjectIndex = obj.spawnIndex,
                    LootState = obj.runtimeState.lootState,
                };
            }

            return projected;
        }

        /// <summary>
        /// One room's door boxes and their current open state — the producing half of the door-state
        /// replication, the same full-replace shape as <see cref="ProjectLootedObjects"/> but
        /// bidirectional (a closed door is state, not the absence of state).
        ///
        /// <para><b>Every door box is a member, sorted by <c>spawnIndex</c>.</b> The receiver full-replaces,
        /// so the set must say "closed" as explicitly as "open" — a player-closed door must read closed
        /// on a peer that generated it open. The sort pins the only order two hosts agree on.</para>
        ///
        /// <para><b>Why the projector reads <c>ObjectInstance</c> directly and not the presenters.</b>
        /// The state being replicated IS the record (<c>runtimeState.isOpen</c>); a presenter is a
        /// view that may not be built for a streamed-out room, while the record lives as long as the
        /// room. <see cref="ObjectInstance.IsDoorBox"/> is the same definition test the presenter
        /// gates on, so the two cannot disagree about what a door is.</para>
        ///
        /// <para><b>The tiles are not here.</b> A toggle rewrites the covered cells'
        /// <c>TilePhysicsType</c> and <c>SetOpen</c> notifies, so the reliable journal already carries
        /// the geometry to the peer. This set exists for the half the journal cannot reach: the
        /// presenter's sprite and its solid projectile collider.</para>
        ///
        /// <para><b>The destination array is caller-owned frame storage</b> — reused when its length
        /// matches the emitted count, replaced when the count changed (see
        /// <see cref="ProjectionFrame"/>).</para>
        /// </summary>
        private SimDoorState[] ProjectDoorStates(RoomInstance room, SimDoorState[] reuse)
        {
            List<ObjectInstance> objects = room.objects;
            if (objects == null || objects.Count == 0)
            {
                return Array.Empty<SimDoorState>();
            }

            _objectScratch.Clear();

            for (int i = 0; i < objects.Count; i++)
            {
                ObjectInstance obj = objects[i];
                if (obj?.runtimeState == null || obj.spawnIndex < 0 || !obj.IsDoorBox())
                {
                    continue;
                }

                _objectScratch.Add(obj);
            }

            if (_objectScratch.Count == 0)
            {
                return Array.Empty<SimDoorState>();
            }

            if (_objectScratch.Count > 1)
            {
                _objectScratch.Sort(ByObjectSpawnIndex);
            }

            SimDoorState[] projected = ReuseOrCreate(reuse, _objectScratch.Count);

            for (int i = 0; i < _objectScratch.Count; i++)
            {
                ObjectInstance obj = _objectScratch[i];
                projected[i] = new SimDoorState
                {
                    ObjectIndex = obj.spawnIndex,
                    IsOpen = obj.runtimeState.isOpen,
                };
            }

            return projected;
        }

        /// <summary>
        /// The registry walk — the production read that F-05 was missing. Sorted by id so the emitted
        /// order does not depend on <c>Dictionary</c> internals.
        /// </summary>
        private void CollectProjectiles()
        {
            _projectiles.Clear();

            foreach (KeyValuePair<EntityId, object> entry in EntityIds.Registry.GetAll())
            {
                // Every kind is collected, not just fired rounds. A thrown object and a mine have no
                // room record either, so the registry is the only place they exist — before this they
                // held an id that nothing ever read, which is a spawn a client cannot see.
                if (!(entry.Value is ISimTransientStateSource transient)) continue;

                // Counted, never dropped silently: the receiver refuses a hash-0 record as malformed,
                // so a transient without a valid id can never draw on any client. Play-test #7's
                // missing client rounds were invisible because this walk used to skip them with no
                // readback (the host overlay said shots=3 while projCaptured stayed 0).
                if (!entry.Key.IsValid || transient.SimEntityHash == 0UL)
                {
                    _lastSkippedNoIdProjectileCount++;
                    continue;
                }

                _projectiles.Add(transient);
            }

            if (_projectiles.Count > 1) _projectiles.Sort(ByTransientHash);
        }

        /// <summary>
        /// The live-view walk. A unit is projected only once it has an id — <c>OnEnable</c> registers it
        /// inside <c>AddComponent</c>, before the spawner stamps the id, so the first tick of a spawn
        /// legitimately sees an anonymous controller and must skip it rather than emit hash 0.
        /// </summary>
        private void CollectUnits()
        {
            _units.Clear();

            // Thread E: the registry is the container-owned instance handed down the spawn chain; null
            // (a fixture with no registry) means "no live views", the same answer an empty room gives.
            IReadOnlyList<UnitController> live = _unitViews != null ? _unitViews.Live : Array.Empty<UnitController>();

            for (int i = 0; i < live.Count; i++)
            {
                UnitController unit = live[i];
                if (unit == null || !unit.HasEntityId) continue;
                _units.Add(unit);
            }

            if (_units.Count > 1) _units.Sort(ByUnitHash);
        }

        /// <summary>
        /// Resolve the scene's streaming manager, retrying until it exists, then caching it.
        ///
        /// <para><b>Why a "resolved" flag is wrong here.</b> This class is an
        /// <c>IAutoRegisteredSimTickable</c>: <c>SimTickRegistrar.Start()</c> attaches it to
        /// <c>SimLoop</c> at container build, so its first tick runs while
        /// <c>MapBridge.WaitForInitialization</c> is still building the visual world. MapBridge
        /// creates the <c>RoomStreamingManager</c> only at the END of that multi-second coroutine
        /// (<c>MapBridge.cs:466-472</c>). Caching the first <c>FindFirstObjectByType</c> result
        /// therefore caches <b>null for the entire session</b>: <c>_rooms</c> stays empty, every
        /// snapshot carries zero room deltas, and <c>ProjectedUnitCount</c> is permanently 0 while
        /// transport, snapshots, commands and the world contract all look perfectly healthy.</para>
        ///
        /// <para>That is the fourth play-test readback: <c>bootAct=1</c> (the session's own retrying
        /// resolver DID find the manager and activated the boot room) next to <c>projUnits=0
        /// projPlayers=0 localProj=0</c> (the projector was still looking at its cached null).
        /// Unity's overloaded <c>== null</c> also re-arms the lookup after a scene unload destroys
        /// the cached object, which a plain "resolved" flag could never do.</para>
        ///
        /// <para>The scene walk happens only on the boot ticks before the manager exists; once found
        /// it is cached, so the steady-state cost is unchanged.</para>
        /// </summary>
        private RoomStreamingManager ResolveStreaming()
        {
            if (_streaming == null)
                _streaming = UnityEngine.Object.FindFirstObjectByType<RoomStreamingManager>();
            return _streaming;
        }
    }
}
