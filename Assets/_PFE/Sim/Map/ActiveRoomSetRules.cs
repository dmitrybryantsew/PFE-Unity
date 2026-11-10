using System;
using System.Collections.Generic;

namespace PFE.Systems.Map
{
    /// <summary>
    /// One room's connected-player presence, keyed by a 64-bit room key.
    ///
    /// <para><b>Why a key and not a <c>RoomInstance</c>.</b> This file lives in <c>PFE.Sim</c>, which
    /// is kept Unity-free by <c>SimStateRecordPurityLintTests</c>. <c>RoomInstance</c> is a View-side
    /// type carrying <c>Vector3Int</c>, tile arrays and object lists. The key is the same FNV-1a 64
    /// room identity <c>SimRoomDelta.RoomHash</c> uses on the wire, so the rule and the snapshot speak
    /// one language without the Sim assembly ever learning what a room object is.</para>
    /// </summary>
    [Serializable]
    public struct ActiveRoomPresence : IEquatable<ActiveRoomPresence>
    {
        /// <summary>Non-zero room key. Zero is reserved by <see cref="ActiveRoomSetRules.NoPresentationRoom"/>.</summary>
        public ulong RoomHash;

        /// <summary>Connected players physically present in this room. Zero or negative means absent.</summary>
        public int ConnectedPlayers;

        public ActiveRoomPresence(ulong roomHash, int connectedPlayers)
        {
            RoomHash = roomHash;
            ConnectedPlayers = connectedPlayers;
        }

        public bool Equals(ActiveRoomPresence other)
        {
            return RoomHash == other.RoomHash && ConnectedPlayers == other.ConnectedPlayers;
        }

        public override bool Equals(object obj)
        {
            return obj is ActiveRoomPresence other && Equals(other);
        }

        public override int GetHashCode()
        {
            return RoomHash.GetHashCode() ^ ConnectedPlayers;
        }
    }

    /// <summary>
    /// Phase 3.1 of the multiplayer readiness plan: the pure decisions behind a <b>set</b> of active
    /// rooms. No Unity types, no clock, no frame counter — the <c>MonoBehaviour</c> owns those.
    ///
    /// <para><b>The three claims, each with a wrong answer that looks right.</b></para>
    /// <list type="bullet">
    /// <item>A room is <c>Active</c> when a connected player is present <b>or</b> it is the
    /// presentation room. Dropping the presentation half leaves the camera's own room with no tile
    /// geometry; dropping the presence half collapses co-op back to one room.</item>
    /// <item>A room <c>maySimulate</c> only when a connected player is present. The presentation room
    /// is view-only when nobody is in it — "active" is a rendering/geometry word, "simulate" is a
    /// truth word. Conflating them makes a camera follow produce authoritative state on a client.</item>
    /// <item>A transition is a <b>diff</b>, not a rebuild. Deactivate first, activate second, and never
    /// name the same key in both directions — otherwise <c>PhysicsWorldService</c> tears down and
    /// rebuilds chain geometry for a room that never left the set.</item>
    /// </list>
    ///
    /// <para><b>D3 (per-room vs global state) is settled in the room-scoped direction</b> by
    /// <c>SimRoomDelta.cs</c>. That is why this rule resolves a set of room keys rather than one global
    /// flag: interest management has something to filter.</para>
    /// </summary>
    public static class ActiveRoomSetRules
    {
        /// <summary>Sentinel for "no presentation room". A valid room key is never zero.</summary>
        public const ulong NoPresentationRoom = 0UL;

        /// <summary>At least one connected player. Negative counts are absent, not "some".</summary>
        public static bool HasPresence(int connectedPlayers)
        {
            return connectedPlayers > 0;
        }

        /// <summary>Whether this room may originate authoritative simulation state.</summary>
        public static bool MaySimulate(in ActiveRoomPresence presence)
        {
            return HasPresence(presence.ConnectedPlayers);
        }

        /// <summary>
        /// Whether this room belongs in the active set (tile geometry + view), which is broader than
        /// <see cref="MaySimulate"/>: the presentation room is active even when nobody is in it.
        /// </summary>
        public static bool IsActive(in ActiveRoomPresence presence, ulong presentationRoomHash)
        {
            return (presentationRoomHash != NoPresentationRoom
                    && presence.RoomHash == presentationRoomHash)
                   || MaySimulate(presence);
        }

        /// <summary>
        /// Resolve the desired active-room set. Deterministic: the presentation room comes first, then
        /// rooms in presence order. Duplicate keys collapse to one entry, and the zero sentinel is
        /// never emitted.
        /// </summary>
        public static ulong[] ResolveActiveRooms(
            IReadOnlyList<ActiveRoomPresence> presence,
            ulong presentationRoomHash)
        {
            var result = new List<ulong>();
            var seen = new HashSet<ulong>();

            if (presentationRoomHash != NoPresentationRoom)
            {
                result.Add(presentationRoomHash);
                seen.Add(presentationRoomHash);
            }

            if (presence == null) return result.ToArray();

            for (int i = 0; i < presence.Count; i++)
            {
                ActiveRoomPresence entry = presence[i];
                if (entry.RoomHash == NoPresentationRoom) continue;
                if (!IsActive(entry, presentationRoomHash)) continue;
                if (!seen.Add(entry.RoomHash)) continue;
                result.Add(entry.RoomHash);
            }

            return result.ToArray();
        }

        /// <summary>
        /// Difference between the currently active set and the desired one. The caller MUST apply
        /// <paramref name="toDeactivate"/> before <paramref name="toActivate"/>: releasing geometry
        /// before building it keeps one world with one chain per room.
        /// </summary>
        public static void Diff(
            IReadOnlyList<ulong> current,
            IReadOnlyList<ulong> desired,
            out ulong[] toActivate,
            out ulong[] toDeactivate)
        {
            var want = new HashSet<ulong>();
            if (desired != null)
            {
                for (int i = 0; i < desired.Count; i++) want.Add(desired[i]);
            }

            var have = new HashSet<ulong>();
            var drop = new List<ulong>();
            if (current != null)
            {
                for (int i = 0; i < current.Count; i++)
                {
                    ulong key = current[i];
                    if (!have.Add(key)) continue;
                    if (!want.Contains(key)) drop.Add(key);
                }
            }

            var add = new List<ulong>();
            if (desired != null)
            {
                for (int i = 0; i < desired.Count; i++)
                {
                    ulong key = desired[i];
                    if (!have.Contains(key)) add.Add(key);
                }
            }

            toActivate = add.ToArray();
            toDeactivate = drop.ToArray();
        }
    }
}
