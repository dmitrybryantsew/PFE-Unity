using System;
using System.Collections.Generic;
using PFE.Sim.Replication;
using PFE.Sim.State;
using PFE.Systems.Map;
using UnityEngine;

namespace PFE.Systems.Replication
{
    /// <summary>
    /// The host adapter that feeds <see cref="SimTileJournal"/> from a live world — the last piece
    /// between "a tile changed" and "a snapshot carries the event".
    ///
    /// <para><b>Why an adapter and not code inside the projector.</b> The journal is pure C# on purpose:
    /// it is the part that must be executable offline, because that is where the emission rules live
    /// (a redundant notification is not an event; a baseline is a divergence, not a dump). Everything
    /// this class does needs <c>RoomInstance</c>, <c>RectInt</c> and the tile array — none of which
    /// exists outside the editor. Keeping the two apart is what lets the rules be tested and leaves only
    /// the plumbing to be pinned by a source lint.</para>
    ///
    /// <para><b>Why it subscribes instead of polling <c>ITileQueryService</c>.</b>
    /// <c>DrainMutations()</c> is a per-tick dirty set that is cleared on read, so a listener that reads
    /// it steals the change from every other listener and a change between two reads is lost. Geometry
    /// cannot be rebuilt from a lost event. <c>RoomInstance.TilesMutated</c> is a notification, so any
    /// number of listeners see it, and the journal keeps every event until the peer acknowledges it.</para>
    ///
    /// <para><b>Seeding on activation, not on first sight.</b> A room that leaves the active set is
    /// regenerated when it comes back, and the save may re-apply destroyed tiles after that. Both are
    /// changes the journal has to see, and both arrive without a <c>TilesMutated</c> — a regenerated room
    /// simply has different tiles. So every activation re-seeds, and <c>SimTileJournal.Seed</c> is
    /// written to refresh the <i>current</i> state while keeping the generated state, which turns a
    /// restored room into a divergence the next baseline re-emits.</para>
    /// </summary>
    public sealed class SimTileJournalHost
    {
        private readonly SimTileJournal _journal = new SimTileJournal();

        // Reference identity: two RoomInstance objects are two rooms even if every field matches, and a
        // room that is rebuilt is a new object that must be seeded again.
        private readonly HashSet<RoomInstance> _active = new HashSet<RoomInstance>();
        private readonly List<RoomInstance> _departed = new List<RoomInstance>(4);

        private long _seededCellCount;
        private long _recordedEventCount;
        private long _rejectedNotificationCount;

        /// <summary>The journal itself. The projector reads <c>ToArray()</c> from it.</summary>
        public SimTileJournal Journal => _journal;

        /// <summary>How many rooms are currently subscribed.</summary>
        public int ActiveRoomCount => _active.Count;

        /// <summary>How many cells have been seeded. The readback for "the walk actually ran".</summary>
        public long SeededCellCount => _seededCellCount;

        /// <summary>
        /// How many events the journal accepted from tile notifications. Zero in a session where walls
        /// were destroyed means the subscription is not reaching the journal.
        /// </summary>
        public long RecordedEventCount => _recordedEventCount;

        /// <summary>
        /// How many notifications were dropped because the room had no tile array. Non-zero means a
        /// change happened in a room the journal could not read — a silent geometry divergence, so it is
        /// counted rather than swallowed.
        /// </summary>
        public long RejectedNotificationCount => _rejectedNotificationCount;

        /// <summary>
        /// Reconcile the subscription set with the rooms the host considers active, seeding every room
        /// that has just arrived. Called once per projection with the projector's own active-room list,
        /// so the two cannot disagree about which rooms are live.
        /// </summary>
        public void Sync(IReadOnlyList<RoomInstance> activeRooms)
        {
            if (activeRooms == null)
            {
                return;
            }

            _departed.Clear();

            foreach (RoomInstance subscribed in _active)
            {
                bool stillActive = false;
                for (int i = 0; i < activeRooms.Count; i++)
                {
                    if (ReferenceEquals(activeRooms[i], subscribed))
                    {
                        stillActive = true;
                        break;
                    }
                }

                if (!stillActive)
                {
                    _departed.Add(subscribed);
                }
            }

            for (int i = 0; i < _departed.Count; i++)
            {
                _departed[i].TilesMutated -= OnTilesMutated;
                _active.Remove(_departed[i]);
            }

            for (int i = 0; i < activeRooms.Count; i++)
            {
                RoomInstance room = activeRooms[i];
                if (room == null || !_active.Add(room))
                {
                    continue;
                }

                room.TilesMutated += OnTilesMutated;
                SeedRoom(room);
            }
        }

        /// <summary>
        /// Start a new reliable-event lifetime and emit its geometry baseline. Called by the projector
        /// when the world revision advances, which is the only moment a peer may forget geometry.
        /// </summary>
        public void BeginLifetime(long worldRevision)
        {
            _journal.BeginLifetime(worldRevision);
        }

        private void SeedRoom(RoomInstance room)
        {
            TileData[,] tiles = room.tiles;
            if (tiles == null)
            {
                return;
            }

            ulong roomHash = SimSnapshotProjectionRules.RoomKey(room.id);
            int width = Mathf.Min(room.width, tiles.GetLength(0));
            int height = Mathf.Min(room.height, tiles.GetLength(1));

            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    TileData tile = tiles[x, y];
                    if (tile == null)
                    {
                        continue;
                    }

                    _journal.Seed(roomHash, x, y, (int)tile.physicsType, tile.hitPoints);
                    _seededCellCount++;
                }
            }
        }

        private void OnTilesMutated(RoomInstance room, RectInt region)
        {
            if (room == null)
            {
                return;
            }

            TileData[,] tiles = room.tiles;
            if (tiles == null)
            {
                // A notification from a room whose tiles are gone. Counted, not swallowed: the change is
                // real and the journal will never learn it.
                _rejectedNotificationCount++;
                return;
            }

            ulong roomHash = SimSnapshotProjectionRules.RoomKey(room.id);

            int width = Mathf.Min(room.width, tiles.GetLength(0));
            int height = Mathf.Min(room.height, tiles.GetLength(1));

            // The region carries a one-tile border on purpose (derived geometry spans runs), so most of
            // it is unchanged. `Record` returns false for those and the journal stays the same size —
            // which is why the border is harmless here and why this loop needs no special case.
            int xMin = Mathf.Max(0, region.xMin);
            int yMin = Mathf.Max(0, region.yMin);
            int xMax = Mathf.Min(width, region.xMax);
            int yMax = Mathf.Min(height, region.yMax);

            for (int x = xMin; x < xMax; x++)
            {
                for (int y = yMin; y < yMax; y++)
                {
                    TileData tile = tiles[x, y];
                    if (tile == null)
                    {
                        continue;
                    }

                    if (_journal.Record(roomHash, x, y, (int)tile.physicsType, tile.hitPoints))
                    {
                        _recordedEventCount++;
                    }
                }
            }
        }
    }
}
