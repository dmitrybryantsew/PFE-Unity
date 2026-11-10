using System;
using System.Collections.Generic;
using PFE.Sim.State;

namespace PFE.Sim.Replication
{
    /// <summary>Why a snapshot was refused. There is deliberately no "applied anyway" member.</summary>
    public enum SimSnapshotRejectReason
    {
        None = 0,
        IncompatibleProtocol = 1,
        StaleSnapshot = 2,
        CorruptTileJournal = 3,
        CorruptLoot = 4,
    }

    /// <summary>
    /// What one <see cref="SimSnapshotReceiver.Accept"/> call did. Readbacks are part of the contract:
    /// a receiver that silently drops a reliable event looks identical to one that never received it.
    /// </summary>
    public readonly struct SimSnapshotReceipt
    {
        public SimSnapshotReceipt(bool applied, SimSnapshotRejectReason reason, int tileEventsApplied,
            int tileEventsDuplicated, int droppedItemsApplied, int lootRoomsReplaced)
        {
            Applied = applied;
            RejectReason = reason;
            TileEventsApplied = tileEventsApplied;
            TileEventsDuplicated = tileEventsDuplicated;
            DroppedItemsApplied = droppedItemsApplied;
            LootRoomsReplaced = lootRoomsReplaced;
        }

        public bool Applied { get; }
        public SimSnapshotRejectReason RejectReason { get; }
        public int TileEventsApplied { get; }
        public int TileEventsDuplicated { get; }
        public int DroppedItemsApplied { get; }
        public int LootRoomsReplaced { get; }

        public static SimSnapshotReceipt Reject(SimSnapshotRejectReason reason)
        {
            return new SimSnapshotReceipt(false, reason, 0, 0, 0, 0);
        }
    }

    /// <summary>
    /// The first consumer of a <see cref="SimSnapshot"/> — Phase-4 prerequisite, §10.2 gaps 1 and 2.
    /// Pure C# so the offline wall can execute it: no transport, no <c>MonoBehaviour</c>, no scene.
    ///
    /// <para><b>Why a receiver is part of the gap, not an optional extra.</b> A payload with no
    /// consumer is decoration: it can be mis-serialized, mis-ordered or mis-keyed for a year with no
    /// symptom. This class is the executable statement of what the two new fields mean — a tile event
    /// is a reliable ordered event that must be applied exactly once, and a loot record is an
    /// entity-keyed member of a full-replace set.</para>
    ///
    /// <para><b>Snapshot admission.</b> Protocol version is checked before any field is read; then
    /// <see cref="SimStalenessRules.AcceptSnapshot"/> rejects an older <c>WorldRevision</c> or a stale
    /// wrap-safe <c>SnapshotSequence</c>. The first accepted snapshot is the baseline and is admitted
    /// unconditionally — there is no prior state to be newer than.</para>
    ///
    /// <para><b>Reliable tile journal.</b> The journal is a contiguous run of the current lifetime's
    /// events, every one stamped with the carrying snapshot's <c>WorldRevision</c>. Two forms are
    /// legal: the full <c>1..N</c> prefix the host repeats while no ack exists, and a suffix beginning
    /// at <c>LastEventSequence + 1</c> once the host has trimmed what the peer acknowledged. What is
    /// never legal is a run that begins <i>after</i> <c>LastEventSequence + 1</c> (a gap — the missing
    /// events are gone for good) or ends <i>before</i> <c>LastEventSequence</c> (a rollback — the host
    /// dropped events it already sent). Events at or below <c>LastEventSequence</c> are duplicates:
    /// skipped, never applied twice, and they must equal the event already applied under that
    /// sequence, because a host that rewrites history inside a repeated journal is corrupt, not late.
    /// A gap, a revision mismatch, a rollback or a changed duplicate rejects the <b>whole</b> snapshot:
    /// applying half a geometry change is worse than dropping one.</para>
    ///
    /// <para><b>An empty journal is legal.</b> F3 §2.6 asks for a full geometry baseline on a new
    /// <c>WorldRevision</c>, and the host produces that baseline as a <i>diff from generated
    /// geometry</i>: a client generates the same rooms from the same <c>RoomTemplate</c>, so what it
    /// cannot know is which tiles the host changed. An untouched room therefore diverges from nothing
    /// and its baseline is empty by construction. Demanding a non-empty journal would reject the most
    /// common new lifetime — a world where nobody has broken anything yet — and would force a full
    /// tile dump that carries no information the peer did not already have. A new revision still
    /// <b>clears</b> the tile state, so stale geometry can never survive into a new lifetime; it simply
    /// does not demand a dump.</para>
    ///
    /// <para><b>Floor loot.</b> F3 §2.6's interim rule is full-replace of <c>DroppedItems</c> per room
    /// present in the snapshot; an absent room means unchanged, not emptied. Every drop must carry a
    /// non-zero host-assigned <c>EntityHash</c> and ids must be unique within a room — a zero or
    /// duplicated id is exactly the §10.2 conflation this payload was added to remove, so it is
    /// rejected rather than silently folded into <c>(content id, position)</c>.</para>
    ///
    /// <para><b>Atomicity.</b> The journal, the loot sets and the admission guards are all validated
    /// before a single field of receiver state is written. A rejected snapshot leaves
    /// <see cref="AcceptedSnapshotCount"/>, <see cref="LastEventSequence"/>, <see cref="TileCount"/>
    /// and every loot readback exactly as they were.</para>
    /// </summary>
    public sealed class SimSnapshotReceiver
    {
        /// <summary>The first sequence of a reliable-event lifetime. A protocol start marker, not a
        /// tunable: it is the value the host's journal begins at after every WorldRevision change.</summary>
        public const uint FirstReliableEventSequence = 1u;

        private readonly Dictionary<TileKey, SimTileMutation> _tiles =
            new Dictionary<TileKey, SimTileMutation>();

        private readonly Dictionary<uint, SimTileMutation> _appliedEvents =
            new Dictionary<uint, SimTileMutation>();

        private readonly Dictionary<ulong, Dictionary<ulong, SimDroppedItem>> _lootByRoom =
            new Dictionary<ulong, Dictionary<ulong, SimDroppedItem>>();

        private long _worldRevision;
        private uint _snapshotSequence;
        private bool _hasAccepted;

        private long _journalRevision;
        private bool _hasJournal;
        private uint _lastEventSequence;

        /// <summary>How many snapshots were applied. The readback for "the receiver is actually used".</summary>
        public int AcceptedSnapshotCount { get; private set; }

        /// <summary>The last reliable tile-event sequence applied in the current lifetime.</summary>
        public uint LastEventSequence => _lastEventSequence;

        /// <summary>The reliable-event lifetime whose geometry this receiver currently holds.</summary>
        public long WorldRevision => _worldRevision;

        /// <summary>How many tile cells have a known post-mutation state in the current lifetime.</summary>
        public int TileCount => _tiles.Count;

        /// <summary>
        /// Admit and apply one snapshot. All validation happens before any state is mutated: a corrupt
        /// journal or a corrupt loot set cannot leave the receiver half-updated.
        /// </summary>
        public SimSnapshotReceipt Accept(in SimSnapshot snapshot)
        {
            if (snapshot.ProtocolVersion != SimSnapshotSerializer.CurrentProtocolVersion)
            {
                return SimSnapshotReceipt.Reject(SimSnapshotRejectReason.IncompatibleProtocol);
            }

            if (_hasAccepted && !SimStalenessRules.AcceptSnapshot(
                    snapshot.SnapshotSequence, snapshot.WorldRevision,
                    _snapshotSequence, _worldRevision))
            {
                return SimSnapshotReceipt.Reject(SimSnapshotRejectReason.StaleSnapshot);
            }

            SimTileMutation[] journal = snapshot.TileMutations ?? Array.Empty<SimTileMutation>();
            SimRoomDelta[] rooms = snapshot.Rooms ?? Array.Empty<SimRoomDelta>();

            bool newLifetime = !_hasJournal || snapshot.WorldRevision > _journalRevision;
            uint baselineLast = newLifetime ? FirstReliableEventSequence - 1u : _lastEventSequence;

            // Dry run #1: the whole reliable journal, including the duplicate-integrity check against
            // what this lifetime already applied. Nothing is written here.
            if (!ValidateJournal(journal, snapshot.WorldRevision, baselineLast, _appliedEvents,
                    out int tileEventsApplied, out int tileEventsDuplicated))
            {
                return SimSnapshotReceipt.Reject(SimSnapshotRejectReason.CorruptTileJournal);
            }

            // Dry run #2: every loot set, before any of them is replaced.
            for (int r = 0; r < rooms.Length; r++)
            {
                SimDroppedItem[] drops = rooms[r].DroppedItems ?? Array.Empty<SimDroppedItem>();
                var seen = new HashSet<ulong>();
                for (int d = 0; d < drops.Length; d++)
                {
                    if (drops[d].EntityHash == 0UL || !seen.Add(drops[d].EntityHash))
                    {
                        return SimSnapshotReceipt.Reject(SimSnapshotRejectReason.CorruptLoot);
                    }
                }
            }

            // ── Commit. Only reachable when both dry runs passed, so no rejection below. ──────

            if (newLifetime)
            {
                _tiles.Clear();
                _appliedEvents.Clear();
                _journalRevision = snapshot.WorldRevision;
                _hasJournal = true;
            }

            uint cursor = baselineLast;
            for (int i = 0; i < journal.Length; i++)
            {
                SimTileMutation mutation = journal[i];
                if (!SimStalenessRules.IsNewer(mutation.EventSequence, cursor))
                {
                    continue; // already applied in this lifetime; integrity was checked above
                }

                cursor = mutation.EventSequence;
                _tiles[new TileKey(mutation.RoomHash, mutation.TileX, mutation.TileY)] = mutation;
                _appliedEvents[mutation.EventSequence] = mutation;
            }
            _lastEventSequence = cursor;

            int droppedItemsApplied = 0;
            for (int r = 0; r < rooms.Length; r++)
            {
                SimDroppedItem[] drops = rooms[r].DroppedItems ?? Array.Empty<SimDroppedItem>();
                var replacement = new Dictionary<ulong, SimDroppedItem>(drops.Length);
                for (int d = 0; d < drops.Length; d++)
                {
                    replacement[drops[d].EntityHash] = drops[d];
                }
                _lootByRoom[rooms[r].RoomHash] = replacement;
                droppedItemsApplied += drops.Length;
            }

            _snapshotSequence = snapshot.SnapshotSequence;
            _worldRevision = snapshot.WorldRevision;
            _hasAccepted = true;
            AcceptedSnapshotCount++;

            return new SimSnapshotReceipt(true, SimSnapshotRejectReason.None,
                tileEventsApplied, tileEventsDuplicated, droppedItemsApplied, rooms.Length);
        }

        /// <summary>The last reliably applied state of one tile cell in this lifetime.</summary>
        public bool TryGetTile(ulong roomHash, int tileX, int tileY, out SimTileMutation mutation)
        {
            return _tiles.TryGetValue(new TileKey(roomHash, tileX, tileY), out mutation);
        }

        /// <summary>One loot stack, keyed by its host-assigned id rather than by content and position.</summary>
        public bool TryGetLoot(ulong roomHash, ulong entityHash, out SimDroppedItem item)
        {
            item = default;
            return _lootByRoom.TryGetValue(roomHash, out Dictionary<ulong, SimDroppedItem> room)
                   && room.TryGetValue(entityHash, out item);
        }

        /// <summary>How many loot stacks a room currently holds. An absent room has zero.</summary>
        public int LootCount(ulong roomHash)
        {
            return _lootByRoom.TryGetValue(roomHash, out Dictionary<ulong, SimDroppedItem> room)
                ? room.Count
                : 0;
        }

        /// <summary>
        /// Validate the repeated reliable journal without touching receiver state.
        ///
        /// <para>The journal must be a contiguous run of the current lifetime. Either form is
        /// admitted — the full <c>1..N</c> prefix the host repeats before an ack exists, or the
        /// post-ack suffix that begins at <paramref name="lastEventSequence"/> + 1 — because both
        /// describe the same stream from the receiver's position. Events at or below
        /// <paramref name="lastEventSequence"/> are duplicates and must equal the event already
        /// applied under that sequence; otherwise a replay that rewrites geometry would be accepted
        /// as a no-op. A run that begins past the next owed sequence is a gap, and a run whose tail
        /// is older than what is already applied is a rollback of history. An empty journal is
        /// legal: the new-lifetime baseline is a diff from generated geometry, and an untouched
        /// world's diff is empty.</para>
        ///
        /// <para>Both refusal rules for the run's <i>start</i> live in the loop rather than in header
        /// checks, because the loop's "the next fresh event must be exactly <c>cursor + 1</c>" rule
        /// implies them: a run beginning later than the next owed sequence fails on its first event,
        /// and a run beginning at sequence 0 fails as a duplicate of an event that was never applied.
        /// Two guards for one condition means one of them is never exercised — and an unexercised
        /// guard is indistinguishable from a missing one.</para>
        /// </summary>
        private static bool ValidateJournal(
            SimTileMutation[] journal,
            long worldRevision,
            uint lastEventSequence,
            Dictionary<uint, SimTileMutation> applied,
            out int appliedCount,
            out int duplicatedCount)
        {
            appliedCount = 0;
            duplicatedCount = 0;

            // The normal new-lifetime baseline: the host sends the divergence from generated
            // geometry, and an untouched world diverges from nothing.
            if (journal.Length == 0)
            {
                return true;
            }

            // No header guards for "starts past the next owed sequence" or "sequence 0": both are
            // already refused by the loop below, and a guard that cannot fail is worse than a
            // comment. The first event that is newer than `cursor` must be exactly `cursor + 1`, so a
            // run that begins later is a gap wherever the check lives; and sequence 0 is always a
            // "duplicate" of an event that was never applied, so the integrity check refuses it.
            uint first = journal[0].EventSequence;
            uint cursor = lastEventSequence;
            for (int i = 0; i < journal.Length; i++)
            {
                SimTileMutation mutation = journal[i];
                if (mutation.WorldRevision != worldRevision) return false;

                // Contiguous inside the journal itself: a suffix is legal, a sparse run is not.
                if (mutation.EventSequence != unchecked(first + (uint)i)) return false;
                if (mutation.RoomHash == 0UL) return false;

                if (SimStalenessRules.IsNewer(mutation.EventSequence, cursor))
                {
                    // A fresh event must be exactly the next sequence; a gap is a broken reliable
                    // stream, so applying the suffix would fabricate geometry from missing events.
                    if (mutation.EventSequence != unchecked(cursor + 1u)) return false;
                    cursor = mutation.EventSequence;
                    appliedCount++;
                }
                else
                {
                    if (!applied.TryGetValue(mutation.EventSequence, out SimTileMutation previous)
                        || !previous.Equals(mutation))
                    {
                        return false;
                    }
                    duplicatedCount++;
                }
            }

            // A tail older than what is already applied is a rollback of history, not a trimmed
            // prefix: the host has dropped events it previously sent. Compare against the journal's
            // TAIL, not `cursor` — a journal of pure duplicates never moves `cursor`, so a truncated
            // replay would otherwise look complete.
            uint tail = journal[journal.Length - 1].EventSequence;
            if (SimStalenessRules.IsNewer(lastEventSequence, tail))
            {
                return false;
            }

            return true;
        }

        private readonly struct TileKey : IEquatable<TileKey>
        {
            private readonly ulong _roomHash;
            private readonly int _x;
            private readonly int _y;

            public TileKey(ulong roomHash, int x, int y)
            {
                _roomHash = roomHash;
                _x = x;
                _y = y;
            }

            public bool Equals(TileKey other)
            {
                return _roomHash == other._roomHash && _x == other._x && _y == other._y;
            }

            public override bool Equals(object obj) => obj is TileKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = _roomHash.GetHashCode();
                    hash = (hash * 397) ^ _x;
                    hash = (hash * 397) ^ _y;
                    return hash;
                }
            }
        }
    }
}
