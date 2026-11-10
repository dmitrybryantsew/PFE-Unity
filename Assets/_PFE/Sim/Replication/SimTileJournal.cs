using System;
using System.Collections.Generic;
using PFE.Sim.State;

namespace PFE.Sim.Replication
{
    /// <summary>
    /// The host-side producer of the reliable tile-mutation stream — the other half of §10.2 gap 1.
    ///
    /// <para><b>Why this is a journal and not a per-tick delta.</b> <c>ITileQueryService.DrainMutations</c>
    /// is a per-tick dirty set: it is cleared on read, so a dropped packet loses the change forever, and
    /// a listener that reads it first steals the events from every other listener. Geometry cannot be
    /// rebuilt from a lost event, so the journal keeps every event for the current
    /// <c>WorldRevision</c> until the peer acknowledges it. That is F3 §2.6's interim rule, made
    /// executable.</para>
    ///
    /// <para><b>What the baseline is, exactly.</b> Not "every tile". A client generates the same rooms
    /// from the same <c>RoomTemplate</c>, so the generated state is already known on both sides; what it
    /// cannot know is which tiles the host has <i>changed since generation</i>. So each cell records the
    /// state it was seeded with (<c>initial</c>) and its state now (<c>current</c>), and
    /// <see cref="BeginLifetime"/> re-emits only the cells where the two differ. An untouched room
    /// therefore has an empty baseline — which is why <c>SimSnapshotReceiver</c> must accept an empty
    /// journal for a new lifetime. A full dump would be tens of thousands of events per room for no
    /// information the client did not already have.</para>
    ///
    /// <para><b>A cell that was never seeded is always in the baseline.</b> If the journal learns about
    /// a tile only from a mutation, it does not know what the client generated there, so it cannot claim
    /// the peer is in sync. Treating "first observation" as "initial" would make that mutation
    /// disappear from every subsequent baseline — a wall the player destroyed would silently come back
    /// for a reconnecting peer.</para>
    ///
    /// <para><b>Why <c>Seed</c> never resets <c>initial</c>.</b> A room that leaves the active set and
    /// comes back is regenerated. If re-seeding reset <c>initial</c> to the regenerated state, every
    /// mutation the host had made to that room would be silently forgotten — a returning player would
    /// find the wall they blew up intact. Keeping <c>initial</c> from the first activation makes the
    /// restored room differ from it again, so the baseline re-emits exactly the changes the save
    /// restored.</para>
    ///
    /// <para><b>Ack.</b> <see cref="Acknowledge"/> drops the acknowledged prefix. The receiver accepts a
    /// contiguous run starting at its own last applied sequence + 1, so a trimmed journal is a legal
    /// suffix rather than a corrupt prefix. Until transport exists nothing calls this and the journal
    /// grows; the rule is landed and tested so Phase 4 only has to deliver the number.</para>
    ///
    /// <para><b>Pure C#.</b> No <c>UnityEngine</c>, no <c>RectInt</c>: the host adapter that walks a
    /// room's tiles lives in the view assembly. <c>SimStateRecordPurityLintTests</c> pins the record, and
    /// this class is exercised offline by <c>SimTileJournalTests</c>.</para>
    /// </summary>
    public sealed class SimTileJournal
    {
        private struct Cell
        {
            /// <summary>Whether <see cref="InitialPhysicsType"/>/<see cref="InitialHitPoints"/> are a
            /// real generated state, as opposed to the placeholder a first <see cref="Record"/> left.</summary>
            public bool Seeded;
            public int InitialPhysicsType;
            public int InitialHitPoints;
            public int PhysicsType;
            public int HitPoints;
        }

        private readonly Dictionary<SimTileKey, Cell> _cells = new Dictionary<SimTileKey, Cell>();
        private readonly List<SimTileMutation> _events = new List<SimTileMutation>(64);

        private long _revision;
        private uint _lastSequence;
        private uint _ackedSequence;
        private bool _hasLifetime;
        private int _version;

        /// <summary>The reliable-event lifetime these events belong to.</summary>
        public long WorldRevision => _revision;

        /// <summary>The last sequence appended in this lifetime; 0 before the first event.</summary>
        public uint LastSequence => _lastSequence;

        /// <summary>The highest sequence the peer has acknowledged. 0 until an ack exists.</summary>
        public uint AckedSequence => _ackedSequence;

        /// <summary>How many unacknowledged events the journal currently holds.</summary>
        public int JournalCount => _events.Count;

        /// <summary>The first sequence still in the journal; 0 when it is empty.</summary>
        public uint FirstSequence => _events.Count == 0 ? 0u : _events[0].EventSequence;

        /// <summary>How many tile cells this journal knows the state of.</summary>
        public int KnownTileCount => _cells.Count;

        /// <summary>How many cells currently differ from the state they were generated with.</summary>
        public int DivergedTileCount
        {
            get
            {
                int count = 0;
                foreach (KeyValuePair<SimTileKey, Cell> entry in _cells)
                {
                    if (IsDiverged(entry.Value))
                    {
                        count++;
                    }
                }
                return count;
            }
        }

        /// <summary>
        /// Monotonic counter bumped by every mutation of the journal. The projector caches the emitted
        /// array and rebuilds only when this changes — a per-tick copy of a 10 000-tile journal would be
        /// the most expensive allocation in the projection.
        /// </summary>
        public int Version => _version;

        /// <summary>
        /// Record the state a cell was generated with. No event is produced: seeding is the host telling
        /// the journal what the client already knows.
        ///
        /// <para>A cell that is already known keeps its <c>initial</c> state and only has <c>current</c>
        /// refreshed — see the class remarks for why resetting it would forget the host's changes.</para>
        /// </summary>
        public void Seed(ulong roomHash, int tileX, int tileY, int physicsType, int hitPoints)
        {
            if (roomHash == 0UL) return;

            var key = new SimTileKey(roomHash, tileX, tileY);

            if (_cells.TryGetValue(key, out Cell existing))
            {
                existing.PhysicsType = physicsType;
                existing.HitPoints = hitPoints;
                _cells[key] = existing;
            }
            else
            {
                _cells[key] = new Cell
                {
                    Seeded = true,
                    InitialPhysicsType = physicsType,
                    InitialHitPoints = hitPoints,
                    PhysicsType = physicsType,
                    HitPoints = hitPoints,
                };
            }

            _version++;
        }

        /// <summary>
        /// Record one observed tile state. Returns true when it produced an event, false when the state is
        /// unchanged — which is the common case: <c>RoomInstance.TilesMutated</c> reports a region with a
        /// one-tile border, and the border tiles did not change.
        ///
        /// <para>An unseeded cell is recorded and <b>does</b> produce an event: the caller is reporting a
        /// change, and refusing to emit it because the journal had not been told the initial state would
        /// drop a real mutation. Such a cell is also never treated as in sync afterwards — see
        /// <see cref="IsDiverged"/> — because the journal cannot prove the client's generated state
        /// matches what it now holds, so every baseline must carry it until a <see cref="Seed"/> says
        /// otherwise.</para>
        /// </summary>
        public bool Record(ulong roomHash, int tileX, int tileY, int physicsType, int hitPoints)
        {
            if (roomHash == 0UL) return false;

            EnsureLifetime();

            var key = new SimTileKey(roomHash, tileX, tileY);

            if (_cells.TryGetValue(key, out Cell cell))
            {
                if (cell.PhysicsType == physicsType && cell.HitPoints == hitPoints)
                {
                    return false;
                }
            }
            else
            {
                // No `Seeded = true`: the initial state is a placeholder standing in for "unknown".
                cell = default;
            }

            cell.PhysicsType = physicsType;
            cell.HitPoints = hitPoints;
            _cells[key] = cell;

            Append(roomHash, tileX, tileY, physicsType, hitPoints);
            return true;
        }

        /// <summary>
        /// Start a new reliable-event lifetime: drop the old journal, restart the sequence at
        /// <see cref="SimSnapshotReceiver.FirstReliableEventSequence"/>, and emit the geometry baseline —
        /// every cell that differs from the state it was generated with, ordered by
        /// <c>(room, x, y)</c> so two hosts emit identical bytes.
        ///
        /// <para>Repeating the same revision is a no-op: a caller that begins a lifetime twice would
        /// otherwise throw away the events recorded since the first call.</para>
        /// </summary>
        public void BeginLifetime(long worldRevision)
        {
            if (_hasLifetime && _revision == worldRevision)
            {
                return;
            }

            _hasLifetime = true;
            _revision = worldRevision;
            _events.Clear();
            _lastSequence = SimSnapshotReceiver.FirstReliableEventSequence - 1u;
            _ackedSequence = 0u;
            _version++;

            List<SimTileKey> baseline = CollectDivergedKeys();
            if (baseline == null)
            {
                return;
            }

            for (int i = 0; i < baseline.Count; i++)
            {
                SimTileKey key = baseline[i];
                Cell cell = _cells[key];
                Append(key.RoomHash, key.X, key.Y, cell.PhysicsType, cell.HitPoints);
            }
        }

        /// <summary>
        /// Emit the divergence from generated geometry as a <b>fresh, read-only</b> reliable baseline
        /// for <paramref name="worldRevision"/>, numbered 1..N in <c>(room, x, y)</c> order.
        ///
        /// <para><b>Why Phase 4 needs this.</b> A late joiner cannot be given the journal's unacked
        /// suffix: that suffix was trimmed as soon as the existing peers acknowledged it, so it holds
        /// only the recent events, not the wall somebody destroyed an hour ago. The authoritative
        /// divergence lives in <c>_cells</c>, and it survives every ack. <see cref="BeginLifetime"/>
        /// cannot be used here — it resets the <i>global</i> sequence and ack state, which would
        /// invalidate every other peer's reliable stream just because one peer connected.</para>
        ///
        /// <para><b>Read-only by construction.</b> This method appends nothing to <c>_events</c>, moves
        /// no counter and bumps no version. The caller stamps the events into its own per-peer
        /// sequence space; the journal keeps speaking its global one.</para>
        /// </summary>
        public void CopyBaselineTo(long worldRevision, List<SimTileMutation> destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            destination.Clear();

            List<SimTileKey> baseline = CollectDivergedKeys();
            if (baseline == null)
            {
                return;
            }

            uint sequence = SimSnapshotReceiver.FirstReliableEventSequence - 1u;
            for (int i = 0; i < baseline.Count; i++)
            {
                SimTileKey key = baseline[i];
                Cell cell = _cells[key];
                sequence = SimSnapshotProjectionRules.NextReliableEventSequence(sequence);
                destination.Add(new SimTileMutation
                {
                    RoomHash = key.RoomHash,
                    WorldRevision = worldRevision,
                    EventSequence = sequence,
                    TileX = key.X,
                    TileY = key.Y,
                    PhysicsType = cell.PhysicsType,
                    HitPoints = cell.HitPoints,
                });
            }
        }

        /// <summary>
        /// One definition of "a cell whose current state differs from generated geometry", shared by
        /// <see cref="BeginLifetime"/> and <see cref="CopyBaselineTo"/> so the global lifetime and a
        /// peer's late-join baseline cannot disagree about which cells need sending.
        /// </summary>
        private List<SimTileKey> CollectDivergedKeys()
        {
            List<SimTileKey> baseline = null;

            foreach (KeyValuePair<SimTileKey, Cell> entry in _cells)
            {
                if (!IsDiverged(entry.Value))
                {
                    continue;
                }

                baseline ??= new List<SimTileKey>(16);
                baseline.Add(entry.Key);
            }

            if (baseline != null) baseline.Sort(CompareKeys);
            return baseline;
        }

        /// <summary>
        /// Drop every event at or below <paramref name="sequence"/>. Idempotent, and clamped to
        /// <see cref="LastSequence"/> so a bogus ack cannot discard an event that has not been sent.
        /// </summary>
        public void Acknowledge(uint sequence)
        {
            if (!SimStalenessRules.IsNewer(sequence, _ackedSequence))
            {
                return;
            }

            if (SimStalenessRules.IsNewer(sequence, _lastSequence))
            {
                sequence = _lastSequence;
            }

            _ackedSequence = sequence;

            int removed = 0;
            while (removed < _events.Count
                   && !SimStalenessRules.IsNewer(_events[removed].EventSequence, sequence))
            {
                removed++;
            }

            if (removed > 0)
            {
                _events.RemoveRange(0, removed);
                _version++;
            }
        }

        /// <summary>The current unacknowledged journal, in sequence order.</summary>
        public SimTileMutation[] ToArray()
        {
            return _events.Count == 0 ? Array.Empty<SimTileMutation>() : _events.ToArray();
        }

        /// <summary>Appends the current journal to <paramref name="destination"/> after clearing it.</summary>
        public void CopyJournalTo(List<SimTileMutation> destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            destination.Clear();
            destination.AddRange(_events);
        }

        /// <summary>The journal's current view of one cell, or false when it has never seen it.</summary>
        public bool TryGetCurrent(ulong roomHash, int tileX, int tileY,
            out int physicsType, out int hitPoints)
        {
            if (_cells.TryGetValue(new SimTileKey(roomHash, tileX, tileY), out Cell cell))
            {
                physicsType = cell.PhysicsType;
                hitPoints = cell.HitPoints;
                return true;
            }

            physicsType = 0;
            hitPoints = 0;
            return false;
        }

        /// <summary>
        /// Whether a cell's current state differs from the state the client is expected to have
        /// generated. An unseeded cell always counts as diverged: the journal never learned what the
        /// client generates there, so silence would leave the peer holding the wrong tile forever.
        /// </summary>
        private static bool IsDiverged(in Cell cell)
        {
            return !cell.Seeded
                || cell.PhysicsType != cell.InitialPhysicsType
                || cell.HitPoints != cell.InitialHitPoints;
        }

        private void EnsureLifetime()
        {
            if (_hasLifetime) return;

            // Events can arrive before the first projection (a tile destroyed during the first tick).
            // Lifetime 0 is a placeholder: the first BeginLifetime re-emits the divergence as the
            // baseline, so nothing is lost.
            _hasLifetime = true;
            _revision = 0L;
            _lastSequence = SimSnapshotReceiver.FirstReliableEventSequence - 1u;
            _ackedSequence = 0u;
        }

        private void Append(ulong roomHash, int tileX, int tileY, int physicsType, int hitPoints)
        {
            _lastSequence = SimSnapshotProjectionRules.NextReliableEventSequence(_lastSequence);
            _events.Add(new SimTileMutation
            {
                RoomHash = roomHash,
                WorldRevision = _revision,
                EventSequence = _lastSequence,
                TileX = tileX,
                TileY = tileY,
                PhysicsType = physicsType,
                HitPoints = hitPoints,
            });
            _version++;
        }

        private static int CompareKeys(SimTileKey a, SimTileKey b)
        {
            int byRoom = a.RoomHash.CompareTo(b.RoomHash);
            if (byRoom != 0) return byRoom;
            int byX = a.X.CompareTo(b.X);
            return byX != 0 ? byX : a.Y.CompareTo(b.Y);
        }
    }

    /// <summary>
    /// A room-local tile coordinate keyed by room. Shared by the journal and the receiver so the two
    /// cannot disagree about what "the same tile" means.
    /// </summary>
    internal readonly struct SimTileKey : IEquatable<SimTileKey>
    {
        public readonly ulong RoomHash;
        public readonly int X;
        public readonly int Y;

        public SimTileKey(ulong roomHash, int x, int y)
        {
            RoomHash = roomHash;
            X = x;
            Y = y;
        }

        public bool Equals(SimTileKey other)
        {
            return RoomHash == other.RoomHash && X == other.X && Y == other.Y;
        }

        public override bool Equals(object obj) => obj is SimTileKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = RoomHash.GetHashCode();
                hash = (hash * 397) ^ X;
                hash = (hash * 397) ^ Y;
                return hash;
            }
        }
    }
}
