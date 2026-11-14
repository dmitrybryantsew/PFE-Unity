using System;
using System.IO;
using PFE.Core.Input;

namespace PFE.Sim.Replication
{
    /// <summary>Why a command batch was refused. There is deliberately no "accepted anyway" member,
    /// and no member for a rule that was removed. Every value names a check that can actually fail.</summary>
    public enum SimCommandRejectReason
    {
        None = 0,
        WrongKind = 1,
        WrongSession = 2,
        WrongPeer = 3,
        UnknownPeer = 4,
        EmptyBatch = 5,
        MalformedBatch = 6,
        NonContiguousTicks = 7,
        SlotNotOwnedByPeer = 8,
        SequencesNotIncreasing = 9,
        OverlapWasRewritten = 10,
        NoConsumer = 11,
    }

    /// <summary>
    /// Phase 5: the client → host command payload. A redundant last-N window of tick-stamped intents,
    /// not a single command, so one lost datagram is repaired by the next batch without a resend
    /// protocol (readiness §1.3 / D4).
    ///
    /// <para><b>Why the session and peer are repeated inside the payload.</b> The envelope already
    /// carries both, but a transport is free to relay or re-stamp an envelope. The payload is what the
    /// ingress validates against the peer table, so it must name the same values; otherwise a batch
    /// from peer 2 wrapped in peer 1's envelope would drive peer 1's body. The check is cheap and it
    /// is the only place the pure layer can detect a re-attributed packet.</para>
    ///
    /// <para><b>Intent only.</b> No position, no motor state, no authoritative outcome. The host alone
    /// decides what a command does; a client that sends a pose is a client that can teleport.</para>
    /// </summary>
    [Serializable]
    public struct SimPlayerCommandBatch : IEquatable<SimPlayerCommandBatch>
    {
        public ulong SessionId;
        public uint PeerId;
        public PlayerCommand[] Commands;

        public readonly void WriteTo(BinaryWriter writer)
        {
            writer.Write(SessionId);
            writer.Write(PeerId);
            PlayerCommand[] commands = Commands;
            writer.Write(commands?.Length ?? 0);
            if (commands == null) return;
            for (int i = 0; i < commands.Length; i++) commands[i].WriteTo(writer);
        }

        /// <summary>
        /// Strict read. <paramref name="maxCommands"/> is caller-supplied because the pure layer owns
        /// no transport constant: the session layer passes the size of the client's own command tape.
        /// An unbounded length prefix would let a corrupt packet allocate an arbitrary array.
        /// </summary>
        public static SimPlayerCommandBatch ReadFrom(BinaryReader reader, int maxCommands)
        {
            if (maxCommands <= 0) throw new ArgumentOutOfRangeException(nameof(maxCommands));

            ulong session = reader.ReadUInt64();
            uint peer = reader.ReadUInt32();
            int count = reader.ReadInt32();
            if (count < 0 || count > maxCommands)
            {
                throw new InvalidDataException("Command batch length is negative or exceeds the caller's window.");
            }

            var commands = new PlayerCommand[count];
            for (int i = 0; i < count; i++) commands[i] = PlayerCommand.ReadFrom(reader);

            return new SimPlayerCommandBatch
            {
                SessionId = session,
                PeerId = peer,
                Commands = commands,
            };
        }

        public readonly bool Equals(SimPlayerCommandBatch other)
        {
            if (SessionId != other.SessionId || PeerId != other.PeerId) return false;
            PlayerCommand[] a = Commands, b = other.Commands;
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (!a[i].Equals(b[i])) return false;
            return true;
        }

        public override readonly bool Equals(object obj) => obj is SimPlayerCommandBatch other && Equals(other);

        public override readonly int GetHashCode()
        {
            unchecked
            {
                int hash = SessionId.GetHashCode();
                hash = (hash * 397) ^ (int)PeerId;
                hash = (hash * 397) ^ (Commands?.Length ?? 0);
                return hash;
            }
        }
    }

    /// <summary>
    /// The admission rules for one command window. Kept separate from <see cref="SimCommandIngress"/>
    /// so each rule can be exercised without a session, a wire or a peer table — the same reason the
    /// staleness rules were split out in row 3.3.
    /// </summary>
    public static class SimCommandAdmissionRules
    {
        /// <summary>
        /// A peer's controller slot is its host-assigned peer id. The host owns slot 0 and
        /// <c>SimReplicationHost</c> allocates peer ids from 1, so this is a statement about the
        /// allocator, not a new numbering scheme. Slot 0 must never be claimable by a remote peer:
        /// that is the host's own body.
        /// </summary>
        public static bool TrySlotForPeer(uint peerId, out byte slot)
        {
            if (peerId == 0u || peerId > byte.MaxValue)
            {
                slot = 0;
                return false;
            }
            slot = (byte)peerId;
            return true;
        }

        /// <summary>Wrap-safe tick ordering, the same signed-difference rule as the snapshot guards.</summary>
        public static bool IsNewerTick(int incoming, int current)
        {
            return unchecked((int)((uint)incoming - (uint)current)) > 0;
        }

        /// <summary>Every command in a batch must name the slot this peer was assigned. A mixed batch
        /// fails here rather than in the tick scan, so the rejection names the actual defect.</summary>
        public static bool IsSingleSlot(PlayerCommand[] commands, byte slot)
        {
            if (commands == null) return false;
            for (int i = 0; i < commands.Length; i++)
            {
                if (commands[i].SlotIndex != slot) return false;
            }
            return true;
        }

        /// <summary>A hole in the redundancy window means a tick of intent was lost; applying the
        /// suffix would silently skip that tick.</summary>
        public static bool AreTicksContiguous(PlayerCommand[] commands)
        {
            if (commands == null || commands.Length == 0) return false;
            int first = commands[0].TickIndex;
            for (int i = 0; i < commands.Length; i++)
            {
                if (commands[i].TickIndex != unchecked(first + i)) return false;
            }
            return true;
        }

        /// <summary>
        /// Sequences must be strictly increasing, <b>not contiguous</b>. This distinction is a
        /// production fact, not a convenience: <c>PlayerController</c> increments
        /// <c>_commandSequence</c> once per <i>render frame</i>, while
        /// <c>PlayerCommandTape.Fold</c> collapses several frames of one sim tick into a single
        /// command that keeps the FIRST frame's sequence. A real tape therefore reads 1, 3, 6, …
        /// with gaps of 1–4. Requiring +1 would refuse every honest batch from the shipping input
        /// path. <see cref="PlayerCommand.TickIndex"/> is the ordering authority for the window;
        /// the per-command sequence is only a wrap-safe provenance counter, and a rewind here means
        /// the producer or a relay rewound history.
        /// </summary>
        public static bool AreSequencesIncreasing(PlayerCommand[] commands)
        {
            if (commands == null || commands.Length == 0) return false;
            for (int i = 1; i < commands.Length; i++)
            {
                uint delta = unchecked(commands[i].Sequence - commands[i - 1].Sequence);
                if ((int)delta <= 0) return false;
            }
            return true;
        }

        /// <summary>Index of the first command newer than the last applied tick, or the batch length
        /// when the whole window is a duplicate.</summary>
        public static int FirstNewIndex(PlayerCommand[] commands, int lastAppliedTick)
        {
            if (commands == null) return 0;
            for (int i = 0; i < commands.Length; i++)
            {
                if (IsNewerTick(commands[i].TickIndex, lastAppliedTick)) return i;
            }
            return commands.Length;
        }
    }
}
