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
        /// The tile journal and its room subscriptions — §10.2 gap 1's producer.
        ///
        /// <para><b>Constructed here rather than injected.</b> It has exactly one consumer, which is this
        /// class, and it owns scene state (subscriptions on live <c>RoomInstance</c> objects) that a
        /// container singleton would outlive. A DI registration would add a lifetime to reason about and
        /// buy nothing.</para>
        /// </summary>
        private readonly SimTileJournalHost _journalHost = new SimTileJournalHost();

        private readonly List<DroppedItemInstance> _droppedScratch = new List<DroppedItemInstance>(16);

        private SimTileMutation[] _tileJournal = Array.Empty<SimTileMutation>();
        private int _tileJournalVersion = -1;

        private RoomStreamingManager _streaming;
        private bool _streamingResolved;

        private SimSnapshot _current;
        private uint _snapshotSequence;
        private long _worldRevision;
        private ulong _roomSetSignature = SimSnapshotProjectionRules.EmptyRoomSetSignature;
        private bool _hasProjected;

        private int _lastProjectileCount;
        private int _lastUnattributedProjectileCount;
        private int _lastUnitCount;
        private int _lastPlayerUnitCount;
        private int _lastUnattributedUnitCount;
        private int _lastUninternedDefinitionCount;
        private int _lastDroppedItemCount;
        private int _lastUnidentifiedDroppedItemCount;
        private long _projectionCount;
        private uint _transientSequence;

        /// <summary>After every other system, so the snapshot is the tick's settled state.</summary>
        public int TickOrder => SimTickOrder.ViewAligned;

        /// <summary>The snapshot built by the last tick. Default until <see cref="SimTick"/> has run once.</summary>
        public SimSnapshot Current => _current;

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
            _lastUnidentifiedDroppedItemCount = 0;

            var deltas = new SimRoomDelta[_rooms.Count];

            for (int i = 0; i < _rooms.Count; i++)
            {
                RoomInstance room = _rooms[i];

                // Projectiles are scene-scoped in this codebase — there is no per-room projectile list
                // — so they are attributed to the presentation room when it is active and to the first
                // active room otherwise. Any other choice would be a guess dressed as data.
                bool ownsProjectiles = streaming != null
                    && (ReferenceEquals(room, streaming.PresentationRoom)
                        || (i == 0 && streaming.PresentationRoom == null));

                SimProjectileState[] projectiles = Array.Empty<SimProjectileState>();

                if (ownsProjectiles && _projectiles.Count > 0)
                {
                    projectiles = new SimProjectileState[_projectiles.Count];

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

                SimUnitState[] units = unitCount == 0 ? Array.Empty<SimUnitState>() : new SimUnitState[unitCount];

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

                deltas[i] = new SimRoomDelta
                {
                    RoomHash = SimSnapshotProjectionRules.RoomKey(room.id),
                    LifecycleSequence = streaming != null ? streaming.GetRoomLifecycleSequence(room) : 0,
                    Units = units,
                    Motors = Array.Empty<SimMotorState>(),
                    Projectiles = projectiles,
                    DroppedItems = ProjectDroppedItems(room),
                    RemovedEntityHashes = Array.Empty<ulong>(),
                };

                projectedDroppedItems += deltas[i].DroppedItems.Length;
            }

            _lastProjectileCount = projectedProjectiles;
            _lastUnattributedProjectileCount = _projectiles.Count - projectedProjectiles;
            _lastUnitCount = projectedUnits;
            _lastPlayerUnitCount = projectedPlayers;
            _lastUnattributedUnitCount = _units.Count - projectedUnits;
            _lastUninternedDefinitionCount = uninterned;
            _lastDroppedItemCount = projectedDroppedItems;

            _current = new SimSnapshot
            {
                ProtocolVersion = SimSnapshotSerializer.CurrentProtocolVersion,
                TickIndex = tickIndex,
                SnapshotSequence = _snapshotSequence,
                WorldRevision = _worldRevision,
                // A full baseline, not a delta against an acked one: delta compression is Phase 3.6 and
                // the field's contract is 0 for "no baseline".
                BaselineHash = 0UL,
                Rooms = deltas,
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
        /// </summary>
        private SimDroppedItem[] ProjectDroppedItems(RoomInstance room)
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

            var projected = new SimDroppedItem[_droppedScratch.Count];

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
        /// The registry walk — the production read that F-05 was missing. Sorted by id so the emitted
        /// order does not depend on <c>Dictionary</c> internals.
        /// </summary>
        private void CollectProjectiles()
        {
            _projectiles.Clear();

            foreach (KeyValuePair<EntityId, object> entry in RuntimeEntityIdContext.Registry.GetAll())
            {
                // Every kind is collected, not just fired rounds. A thrown object and a mine have no
                // room record either, so the registry is the only place they exist — before this they
                // held an id that nothing ever read, which is a spawn a client cannot see.
                if (entry.Value is ISimTransientStateSource transient && entry.Key.IsValid)
                {
                    _projectiles.Add(transient);
                }
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

            IReadOnlyList<UnitController> live = UnitViewRegistry.Live;

            for (int i = 0; i < live.Count; i++)
            {
                UnitController unit = live[i];
                if (unit == null || !unit.HasEntityId) continue;
                _units.Add(unit);
            }

            if (_units.Count > 1) _units.Sort(ByUnitHash);
        }

        /// <summary>
        /// Resolve the scene's streaming manager once, and only once. It is a scene object, so the
        /// container cannot inject it; the lookup is cached because <c>FindFirstObjectByType</c> walks
        /// the scene, and a per-tick walk would be the most expensive thing in the projection.
        /// </summary>
        private RoomStreamingManager ResolveStreaming()
        {
            if (_streamingResolved) return _streaming;

            _streaming = UnityEngine.Object.FindFirstObjectByType<RoomStreamingManager>();
            _streamingResolved = true;
            return _streaming;
        }
    }
}
