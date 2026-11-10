using System;
using System.IO;

namespace PFE.Sim.Replication
{
    /// <summary>The six messages the transport-neutral replication loop exchanges.</summary>
    public enum SimReplicationMessageKind : byte
    {
        None = 0,
        JoinRequest = 1,
        JoinAccept = 2,
        JoinReject = 3,
        BaselineChunk = 4,
        Snapshot = 5,
        SnapshotAck = 6,
        InterestUpdate = 7,
    }

    /// <summary>Why a join was refused. There is deliberately no "accepted anyway" member.</summary>
    public enum SimJoinRejectReason : byte
    {
        None = 0,
        IncompatibleProtocol = 1,
        AlreadyJoined = 2,
    }

    /// <summary>
    /// Phase 4 (F-11): the one version a peer must agree on before a single byte of state is read.
    ///
    /// <para><b>Why the snapshot codec's version, not a new constant.</b> Two numbers that must move
    /// together would drift: a handshake that says "v4" while the payload is encoded as v5 is exactly
    /// the incompatible peer the handshake exists to refuse. The replication protocol <i>is</i> the
    /// snapshot protocol, so it names the same constant.</para>
    ///
    /// <para><b>Why nothing here names a transport.</b> FishNet (or any other stack) enters later as a
    /// <see cref="ISimReplicationWire"/> implementation in the view assembly. These rules are pure C#
    /// so the offline wall can execute a whole host → wire → client → ack → host loop with no socket,
    /// no package and no <c>MonoBehaviour</c>.</para>
    /// </summary>
    public static class SimReplicationProtocolRules
    {
        public const uint CurrentProtocolVersion = State.SimSnapshotSerializer.CurrentProtocolVersion;

        private const byte Magic0 = (byte)'P';
        private const byte Magic1 = (byte)'F';
        private const byte Magic2 = (byte)'E';
        private const byte Magic3 = (byte)'R';

        /// <summary>
        /// The envelope sequence's successor. Envelope ordering is wrap-safe and per-sender, and is
        /// deliberately separate from <c>SimSnapshot.SnapshotSequence</c>: one reliable envelope can
        /// carry a baseline chunk, which is not a snapshot at all.
        /// </summary>
        public static uint NextEnvelopeSequence(uint current)
        {
            return unchecked(current + 1);
        }

        /// <summary>
        /// Whether an ordered envelope may be delivered. Duplicate and out-of-order deliveries are
        /// refused rather than applied twice, because a re-delivered ack must not trim the journal
        /// twice and a re-delivered snapshot must not overwrite a newer one.
        /// </summary>
        public static bool AcceptEnvelope(uint incoming, uint current)
        {
            return State.SimStalenessRules.IsNewer(incoming, current);
        }

        /// <summary>True when a peer's declared protocol can be admitted before any payload is read.</summary>
        public static bool IsCompatible(uint protocolVersion)
        {
            return protocolVersion == CurrentProtocolVersion;
        }

        /// <summary>
        /// FNV-1a 64 over the payload bytes. Used to prove that reassembled baseline chunks are the
        /// bytes the host split, rather than merely the right number of bytes.
        /// </summary>
        public static ulong ComputePayloadHash(byte[] payload)
        {
            unchecked
            {
                ulong hash = 14695981039346656037UL;
                if (payload == null) return hash;
                for (int i = 0; i < payload.Length; i++)
                {
                    hash ^= payload[i];
                    hash *= 1099511628211UL;
                }
                return hash;
            }
        }

        public static void WriteMagic(BinaryWriter writer)
        {
            writer.Write(Magic0);
            writer.Write(Magic1);
            writer.Write(Magic2);
            writer.Write(Magic3);
        }

        public static void ReadMagic(BinaryReader reader)
        {
            byte a = reader.ReadByte();
            byte b = reader.ReadByte();
            byte c = reader.ReadByte();
            byte d = reader.ReadByte();
            if (a != Magic0 || b != Magic1 || c != Magic2 || d != Magic3)
            {
                throw new InvalidDataException("Not a PFE replication envelope (bad magic).");
            }
        }
    }

    /// <summary>
    /// The whole wire shape. One struct, one <c>Send</c> seam, no transport type.
    ///
    /// <para><b>Why <c>SessionId</c> is in the envelope and not just the join reply.</b> A peer that
    /// disconnected and reconnected owns a new session. A late packet from the old session must be
    /// refused by value, not by a connection handle the Sim layer does not have — this is the only
    /// place a pure endpoint can tell "this host session" from "the previous one".</para>
    ///
    /// <para><b>Why <c>Kind</c> precedes the payload.</b> A receiver can refuse a snapshot that arrives
    /// before a join accept without deserializing a snapshot. F-11's ordering rule (later reliable
    /// events follow the baseline's final chunk) is a rule about kinds, and kinds live here.</para>
    /// </summary>
    [Serializable]
    public struct SimReplicationEnvelope : IEquatable<SimReplicationEnvelope>
    {
        public uint ProtocolVersion;
        public ulong SessionId;
        public uint PeerId;
        public SimReplicationMessageKind Kind;
        public uint Sequence;
        public byte[] Payload;

        public readonly void WriteTo(BinaryWriter writer)
        {
            SimReplicationProtocolRules.WriteMagic(writer);
            writer.Write(ProtocolVersion);
            writer.Write(SessionId);
            writer.Write(PeerId);
            writer.Write((byte)Kind);
            writer.Write(Sequence);

            byte[] payload = Payload;
            writer.Write(payload?.Length ?? 0);
            if (payload != null) writer.Write(payload);
        }

        public static SimReplicationEnvelope ReadFrom(BinaryReader reader)
        {
            SimReplicationProtocolRules.ReadMagic(reader);

            uint version = reader.ReadUInt32();
            if (!SimReplicationProtocolRules.IsCompatible(version))
            {
                throw new InvalidDataException(
                    "Unsupported replication protocol version " + version + "; this endpoint reads " +
                    SimReplicationProtocolRules.CurrentProtocolVersion + ".");
            }

            ulong session = reader.ReadUInt64();
            uint peer = reader.ReadUInt32();
            byte kind = reader.ReadByte();
            uint sequence = reader.ReadUInt32();

            int length = reader.ReadInt32();
            if (length < 0) throw new InvalidDataException("Negative envelope payload length.");
            byte[] payload = reader.ReadBytes(length);
            if (payload.Length != length)
            {
                throw new EndOfStreamException("Truncated envelope payload.");
            }

            return new SimReplicationEnvelope
            {
                ProtocolVersion = version,
                SessionId = session,
                PeerId = peer,
                Kind = (SimReplicationMessageKind)kind,
                Sequence = sequence,
                Payload = payload,
            };
        }

        public readonly bool Equals(SimReplicationEnvelope other)
        {
            if (ProtocolVersion != other.ProtocolVersion || SessionId != other.SessionId
                || PeerId != other.PeerId || Kind != other.Kind || Sequence != other.Sequence)
            {
                return false;
            }

            byte[] a = Payload;
            byte[] b = other.Payload;
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        public override readonly bool Equals(object obj) => obj is SimReplicationEnvelope other && Equals(other);

        public override readonly int GetHashCode()
        {
            unchecked
            {
                int hash = (int)ProtocolVersion;
                hash = (hash * 397) ^ SessionId.GetHashCode();
                hash = (hash * 397) ^ (int)PeerId;
                hash = (hash * 397) ^ (int)Kind;
                hash = (hash * 397) ^ (int)Sequence;
                hash = (hash * 397) ^ (Payload?.Length ?? 0);
                return hash;
            }
        }
    }

    /// <summary>
    /// Strict byte codec for <see cref="SimReplicationEnvelope"/>. A transport hands over bytes; this
    /// is the last point at which a malformed or incompatible packet can be refused before an endpoint
    /// interprets it.
    /// </summary>
    public static class SimReplicationEnvelopeSerializer
    {
        public static byte[] Serialize(in SimReplicationEnvelope envelope)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                envelope.WriteTo(writer);
                writer.Flush();
                return stream.ToArray();
            }
        }

        public static SimReplicationEnvelope Deserialize(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));

            using (var stream = new MemoryStream(bytes, false))
            using (var reader = new BinaryReader(stream))
            {
                SimReplicationEnvelope envelope = SimReplicationEnvelope.ReadFrom(reader);
                if (stream.Position != stream.Length)
                {
                    throw new InvalidDataException("Trailing bytes after replication envelope.");
                }
                return envelope;
            }
        }
    }
}
