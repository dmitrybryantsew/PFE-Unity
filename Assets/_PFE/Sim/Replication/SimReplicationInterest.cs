using System;
using System.Collections.Generic;
using PFE.Sim.State;

namespace PFE.Sim.Replication
{
    /// <summary>
    /// Roadmap 05 §1.5's interest rule: a peer receives its <b>local room plus its adjacent rooms</b>,
    /// not every active room. Keyed on the same <c>RoomHash</c> the snapshot and the tile journal use,
    /// so the filter and the producer cannot disagree about what "the same room" means.
    ///
    /// <para><b>Why a set and not a distance.</b> Adjacency in this port is a room-graph relation
    /// (<c>RoomInstance</c> neighbours), not a geometric radius: two rooms may be adjacent in the graph
    /// while far apart in world pixels. The pure layer must not invent a coordinate rule for it; the
    /// view-layer caller supplies the neighbour hashes.</para>
    /// </summary>
    public sealed class SimPeerInterest : IEquatable<SimPeerInterest>
    {
        private readonly HashSet<ulong> _rooms = new HashSet<ulong>();

        public SimPeerInterest(ulong localRoomHash, IEnumerable<ulong> adjacentRoomHashes)
        {
            LocalRoomHash = localRoomHash;
            if (localRoomHash != 0UL) _rooms.Add(localRoomHash);

            if (adjacentRoomHashes != null)
            {
                foreach (ulong hash in adjacentRoomHashes)
                {
                    if (hash != 0UL) _rooms.Add(hash);
                }
            }
        }

        /// <summary>The peer's own room. Zero means the peer is between rooms.</summary>
        public ulong LocalRoomHash { get; }

        public int RoomCount => _rooms.Count;

        public bool Contains(ulong roomHash) => roomHash != 0UL && _rooms.Contains(roomHash);

        public ulong[] ToArray()
        {
            var copy = new ulong[_rooms.Count];
            _rooms.CopyTo(copy);
            Array.Sort(copy);
            return copy;
        }

        public bool Equals(SimPeerInterest other)
        {
            if (other == null || LocalRoomHash != other.LocalRoomHash || _rooms.Count != other._rooms.Count)
            {
                return false;
            }

            foreach (ulong hash in _rooms)
            {
                if (!other._rooms.Contains(hash)) return false;
            }
            return true;
        }

        public override bool Equals(object obj) => obj is SimPeerInterest other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                // Order-independent: two peers with the same set must hash the same regardless of the
                // order their adjacency list happened to enumerate in.
                ulong fold = 14695981039346656037UL;
                foreach (ulong hash in _rooms) fold ^= hash;
                return fold.GetHashCode() ^ LocalRoomHash.GetHashCode();
            }
        }
    }

    /// <summary>
    /// The per-peer filter. Both halves of a snapshot are room-keyed, so both are filtered; filtering
    /// only <c>Rooms</c> would leave the peer paying for every other room's tile divergence.
    ///
    /// <para><b>What this filter does NOT do.</b> It does not renumber <c>EventSequence</c>. A reliable
    /// stream must be contiguous in whatever sequence space the receiver validates, so dropping an
    /// event here would create a gap and the receiver would refuse the whole snapshot.
    /// <see cref="SimReplicationHost"/> owns the per-peer sequence-space remap: this function only
    /// decides membership, and the host re-stamps the surviving events into the peer's own stream.</para>
    /// </summary>
    public static class SimReplicationInterestRules
    {
        public static SimRoomDelta[] FilterRooms(SimRoomDelta[] rooms, SimPeerInterest interest)
        {
            if (interest == null) throw new ArgumentNullException(nameof(interest));
            if (rooms == null || rooms.Length == 0) return Array.Empty<SimRoomDelta>();

            var kept = new List<SimRoomDelta>(rooms.Length);
            for (int i = 0; i < rooms.Length; i++)
            {
                if (interest.Contains(rooms[i].RoomHash)) kept.Add(rooms[i]);
            }
            return kept.ToArray();
        }

        public static SimTileMutation[] FilterJournal(SimTileMutation[] journal, SimPeerInterest interest)
        {
            if (interest == null) throw new ArgumentNullException(nameof(interest));
            if (journal == null || journal.Length == 0) return Array.Empty<SimTileMutation>();

            var kept = new List<SimTileMutation>(journal.Length);
            for (int i = 0; i < journal.Length; i++)
            {
                if (interest.Contains(journal[i].RoomHash)) kept.Add(journal[i]);
            }
            return kept.ToArray();
        }

        /// <summary>
        /// Whether an interest change forces a new peer lifetime. Adding a room does: the peer has no
        /// reliable history for that room, so it needs a fresh baseline. Shrinking a room does not:
        /// the peer already holds valid geometry for the rooms it keeps, and a new lifetime would
        /// needlessly discard a stream it has acknowledged.
        /// </summary>
        public static bool RequiresNewLifetime(SimPeerInterest previous, SimPeerInterest next)
        {
            if (previous == null) return true;
            if (next == null) throw new ArgumentNullException(nameof(next));

            ulong[] added = next.ToArray();
            for (int i = 0; i < added.Length; i++)
            {
                if (!previous.Contains(added[i])) return true;
            }
            return false;
        }
    }
}
