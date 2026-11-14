using System;
using PFE.Core.Ids;
using PFE.Core.Input;
using PFE.Sim.State;

namespace PFE.Net
{
    /// <summary>
    /// Pure decisions for a remote player body. No <c>UnityEngine</c>, no <c>MonoBehaviour</c>, no
    /// scene: this is the half of F-12 that the offline wall can actually execute.
    ///
    /// <para><b>Why a body needs an agreed identity before it needs a prefab.</b> The host simulates
    /// the peer's body and projects it in <see cref="SimRoomDelta.Units"/>; the client must find that
    /// same body to move it. If the two sides minted ids independently — the obvious
    /// <c>EntityId.CreateRuntime(tick, counter)</c> — the hashes would differ, the client would apply
    /// every unit to "not mine", and the remote avatar would simply never move. It would look like a
    /// transport bug. <c>EntityId.CreateForPlayer(slot)</c> is already deterministic across processes
    /// and already names the thing it identifies (<c>player:1</c>), so it is the identity, not a new
    /// scheme.</para>
    ///
    /// <para><b>Why the command gate is the load-bearing rule.</b> <c>PlayerLocomotionController
    /// .SetIntent</c> takes the whole <see cref="PlayerCommand"/>, including the one-shot edges
    /// (<c>JumpPressed</c>, <c>DashPressed</c>). Re-applying the same command every tick — the
    /// obvious "keep the intent alive" implementation — re-fires jump and dash on every tick the peer
    /// stands still, because the edge is a property of the command, not of the locomotion. Applying
    /// each command exactly once and letting the held levels persist is the only version that matches
    /// what the local player does, where one command is pushed per frame.</para>
    /// </summary>
    public static class PfeRemoteBodyRules
    {
        /// <summary>Slot 0 is the host's own player. A body is built for every slot that is *not* the
        /// local one, which on a client includes slot 0 — the host's player is the remote avatar there.</summary>
        public const byte HostSlot = 0;

        /// <summary>The session-long lifetime every remote body is stamped with. Generation 1 means
        /// "the first and only lifetime", so a rejoin is a new body rather than a resurrection.</summary>
        public const int BodyGeneration = 1;

        /// <summary>
        /// The identity both sides must agree on. Slot 0 is legal: on a client the host's player is a
        /// remote avatar. What is never legal is a body for the *local* slot — see
        /// <see cref="ShouldCreateBody"/> — because that would put a second unit on the hash the local
        /// player already owns, and the projector would emit whichever it walked last.
        /// </summary>
        public static bool TryBodyId(byte slot, out EntityId id)
        {
            id = EntityId.CreateForPlayer(slot);
            return id.IsValid;
        }

        /// <summary>Whether this process should build a body for <paramref name="bodySlot"/>. The only
        /// exclusion is the local slot: building one for yourself duplicates your own hash.</summary>
        public static bool ShouldCreateBody(byte bodySlot, byte localSlot)
        {
            return bodySlot != localSlot;
        }

        /// <summary>
        /// Which live body a stopped connection removes. The router's <c>Unbind</c> erases the
        /// peer-to-connection mapping, so the caller must resolve this <b>before</b> unbinding.
        ///
        /// <para><b>Why this is not just <c>(byte)peerId</c>.</b> Phase 5 binds slot = peerId for
        /// 1..255. A peer id of 0 is the host itself, which never owns a remote body; a peer id
        /// above 255 has no slot and a truncating cast would name the wrong player (256 becomes
        /// slot 0, the host's avatar on a client); and the local slot never has a body at all.
        /// Those are three distinct ways to delete a body that must stay, or to leave a body that
        /// must go — the exact failure a compile and a screenshot both miss.</para>
        /// </summary>
        public static bool TryRemovalSlot(uint peerId, byte localSlot, out byte slot)
        {
            slot = 0;
            if (peerId == 0u || peerId > byte.MaxValue) return false;
            byte candidate = (byte)peerId;
            if (!ShouldCreateBody(candidate, localSlot)) return false;
            slot = candidate;
            return true;
        }

        /// <summary>The lifetime to stamp alongside <see cref="TryBodyId"/>.</summary>
        public static EntityLifetime BodyLifetime(byte slot)
        {
            return new EntityLifetime(BodyGeneration, slot);
        }

        /// <summary>
        /// Whether <paramref name="incoming"/> is a command this body has not applied yet. Wrap-safe:
        /// it is the same comparison <c>SimStalenessRules.IsNewer</c> uses, because a command
        /// <c>Sequence</c> is a <c>uint</c> that a long session can wrap.
        ///
        /// <para><b>A zero <paramref name="applied"/> means "nothing applied yet".</b> Command
        /// sequences start at 1 in this project, so 0 cannot be a real applied value and does not need
        /// a separate <c>hasApplied</c> flag — which is the flag that would otherwise be forgotten on
        /// one of the two reset paths.</para>
        /// </summary>
        public static bool IsNewerCommand(uint incoming, uint applied)
        {
            if (applied == 0u) return incoming != 0u;
            return unchecked((int)(incoming - applied)) > 0;
        }

        /// <summary>
        /// Whether a snapshot unit record describes <paramref name="bodyHash"/>. Equality only: a
        /// record with hash 0 is an unidentified unit, and treating it as "mine" would move a remote
        /// body onto whatever the last anonymous unit happened to be.
        /// </summary>
        public static bool IsBodyRecord(ulong bodyHash, in SimUnitState state)
        {
            return bodyHash != 0UL && state.EntityHash == bodyHash;
        }

        /// <summary>
        /// Whether the client may move its body to a replicated position. Both coordinates must be
        /// finite: a NaN would silently poison <c>transform.localPosition</c> and the body would
        /// vanish with no error, which is the one failure mode a screenshot cannot explain.
        /// </summary>
        public static bool IsUsablePosition(float px, float py)
        {
            return !float.IsNaN(px) && !float.IsNaN(py)
                && !float.IsInfinity(px) && !float.IsInfinity(py);
        }

        /// <summary>
        /// Which slot owns the <b>local</b> player on this machine. The host is always slot 0; a
        /// client uses the host-assigned peer id, which is also the slot its commands are stamped
        /// with (<c>SimCommandAdmissionRules.TrySlotForPeer</c>).
        ///
        /// <para><b>Why a client may have NO slot yet.</b> A join accept has not arrived during the
        /// first ticks. Stamping <c>player:0</c> in that window would give the client the HOST's
        /// identity: the projector would emit a second <c>player:0</c>, the client's own display
        /// body would be refused by <see cref="ShouldCreateBody"/>, and two avatars would share one
        /// hash. Returning false defers the stamp instead of guessing.</para>
        ///
        /// <para><b>Why 256 is refused rather than truncated.</b> A truncating cast turns peer 256
        /// into slot 0 — the host's identity — which is the same collision with a different cause.
        /// The bounds mirror <c>TrySlotForPeer</c> exactly so the identity and the command slot can
        /// never disagree.</para>
        /// </summary>
        public static bool TryLocalPlayerSlot(bool isHost, uint peerId, out byte slot)
        {
            if (isHost)
            {
                slot = HostSlot;
                return true;
            }

            if (peerId == 0u || peerId > byte.MaxValue)
            {
                slot = 0;
                return false;
            }

            slot = (byte)peerId;
            return true;
        }

        /// <summary>
        /// The hash the local player must carry. Derived from the SAME scheme as
        /// <see cref="TryBodyId"/>: the host projects <c>player:0</c>, and the client's display body
        /// for slot 0 only matches a record carrying exactly that hash. A locally minted runtime id
        /// would differ on the two machines and the avatar would silently never move.
        /// </summary>
        public static ulong LocalPlayerHash(byte slot)
        {
            return EntityId.CreateForPlayer(slot).Hash;
        }

        /// <summary>
        /// Whether the room baked into the local player's tile query has gone stale. The query is a
        /// snapshot of a <c>RoomInstance</c>, not a live lookup: a door transition swaps
        /// <c>LandMap.currentRoom</c> and the projector's active set, but the query still names the
        /// room the player LEFT, so the local player stops being attributed to any emitted delta.
        ///
        /// <para>A null/empty <paramref name="currentRoomId"/> means "no authoritative room yet" —
        /// there is nothing to bind to, and replacing a good binding with nothing would be a
        /// regression rather than a retry.</para>
        /// </summary>
        public static bool NeedsRoomRebind(string boundRoomId, string currentRoomId)
        {
            if (string.IsNullOrEmpty(currentRoomId)) return false;
            return !string.Equals(boundRoomId, currentRoomId, StringComparison.Ordinal);
        }

        /// <summary>
        /// Whether a unit record from one <c>SimRoomDelta</c> may move a display body here. Positions
        /// are room-local pixels, so applying a record from a different room draws the host's avatar
        /// at a plausible-looking but wrong spot in the room the camera currently follows — the
        /// "players in different rooms don't see each other" symptom from the third play-test.
        ///
        /// <para>A zero hash is "no room named", which is a claim about absence rather than a room:
        /// it must not be treated as a wildcard. Both hashes must be present and equal.</para>
        /// </summary>
        public static bool CanPlaceBodyInRoom(ulong deltaRoomHash, ulong localRoomHash)
        {
            return deltaRoomHash != 0UL && deltaRoomHash == localRoomHash;
        }

        /// <summary>Why a projection emitted no usable unit records. One word on the overlay instead of
        /// six counters a human has to correlate by hand.</summary>
        public enum ProjectionFault
        {
            None,
            NoStreamingManager,
            NoActiveRooms,
            NoIdentifiedUnits,
            UnitsUnattributed,
        }

        /// <summary>
        /// Classify a projection from its readbacks, in the order the producer actually depends on
        /// them: no streaming manager means no rooms; no rooms means no deltas; no identified units
        /// means <c>CollectUnits</c> filtered everything; identified-but-unprojected units means the
        /// <c>ReferenceEquals(controller.Room, room)</c> attribution rejected them all.
        ///
        /// <para>The fourth play-test produced exactly this ambiguity: <c>projUnits=0 projPlayers=0
        /// localProj=0</c> on a healthy wire, with no way to tell "producer never looked at a room"
        /// from "producer looked at a room and found no units". A zero in every counter is not a
        /// diagnosis — this ordering is.</para>
        /// </summary>
        public static ProjectionFault ClassifyProjection(
            bool hasStreaming, int activeRooms, int identifiedUnits, int projectedUnits)
        {
            if (!hasStreaming) return ProjectionFault.NoStreamingManager;
            if (activeRooms <= 0) return ProjectionFault.NoActiveRooms;
            if (identifiedUnits <= 0) return ProjectionFault.NoIdentifiedUnits;
            if (projectedUnits <= 0) return ProjectionFault.UnitsUnattributed;
            return ProjectionFault.None;
        }

        /// <summary>
        /// The intent a host body should be driven with. Only the held levels and the aim are taken
        /// from the peer; the identity fields are the host's own, because the body is simulated here
        /// and its <c>SlotIndex</c> is the host's slot binding, not the peer's.
        /// </summary>
        public static PlayerCommand ToBodyIntent(in PlayerCommand command, byte slot)
        {
            PlayerCommand intent = command;
            intent.SlotIndex = slot;
            return intent;
        }
    }
}
