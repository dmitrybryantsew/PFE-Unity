using System.Collections.Generic;
using PFE.Sim.State;
using PFE.Systems.Map;
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

        private bool _hasRevision;
        private long _revision;

        public int AppliedEvents { get; private set; }
        public int AppliedCells { get; private set; }
        public int SkippedUnknownRoom { get; private set; }
        public int SkippedNullTile { get; private set; }
        public int RebuildCount { get; private set; }
        public int KnownRoomCount => _rooms.Count;

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
        public void RefreshRooms(LandMap map)
        {
            _rooms.Clear();
            _queries.Clear();
            if (map == null) return;

            foreach (RoomInstance room in map.GetAllRooms())
            {
                if (room == null || string.IsNullOrEmpty(room.id)) continue;
                ulong hash = SimSnapshotProjectionRules.RoomKey(room.id);
                _rooms[hash] = room;
            }
        }

        /// <summary>Apply the not-yet-applied part of one accepted snapshot's journal.</summary>
        public void Apply(in SimSnapshot snapshot)
        {
            if (!_hasRevision || snapshot.WorldRevision != _revision)
            {
                _hasRevision = true;
                _revision = snapshot.WorldRevision;
                _appliedSequences.Clear();
            }

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
    }
}
