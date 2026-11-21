using System;
using System.IO;

namespace PFE.Sim.Replication
{
    /// <summary>
    /// The wire form of a peer's interest. Kept as hashes, not room ids: the pure layer has no content
    /// table and §9 already collapsed room identity to <c>RoomHash</c>.
    /// </summary>
    internal static class SimInterestPayload
    {
        public static void WriteTo(BinaryWriter writer, ulong localRoomHash, ulong[] adjacent)
        {
            writer.Write(localRoomHash);
            ulong[] rooms = adjacent;
            writer.Write(rooms?.Length ?? 0);
            if (rooms == null) return;
            for (int i = 0; i < rooms.Length; i++) writer.Write(rooms[i]);
        }

        public static void ReadFrom(BinaryReader reader, out ulong localRoomHash, out ulong[] adjacent)
        {
            localRoomHash = reader.ReadUInt64();
            int count = reader.ReadInt32();
            if (count < 0 || count > 4096) throw new InvalidDataException("Absurd interest room count.");
            adjacent = new ulong[count];
            for (int i = 0; i < count; i++) adjacent[i] = reader.ReadUInt64();
        }

        public static SimPeerInterest ToInterest(ulong localRoomHash, ulong[] adjacent)
        {
            return new SimPeerInterest(localRoomHash, adjacent);
        }
    }

    /// <summary>
    /// Client → host. The nonce is per attempt, so a retried join is not a new session. The peer
    /// declares its local + adjacent rooms here, because the host cannot filter a peer it knows
    /// nothing about and a default "send everything" would silently defeat interest management.
    /// </summary>
    [Serializable]
    public struct SimJoinRequest : IEquatable<SimJoinRequest>
    {
        public uint ProtocolVersion;
        public ulong ClientNonce;
        public ulong LocalRoomHash;
        public ulong[] AdjacentRoomHashes;

        public SimJoinRequest(uint protocolVersion, ulong clientNonce, SimPeerInterest interest)
        {
            ProtocolVersion = protocolVersion;
            ClientNonce = clientNonce;
            LocalRoomHash = interest?.LocalRoomHash ?? 0UL;
            AdjacentRoomHashes = interest?.ToArray() ?? Array.Empty<ulong>();
        }

        public readonly SimPeerInterest ToInterest() =>
            SimInterestPayload.ToInterest(LocalRoomHash, AdjacentRoomHashes);

        public readonly void WriteTo(BinaryWriter writer)
        {
            writer.Write(ProtocolVersion);
            writer.Write(ClientNonce);
            SimInterestPayload.WriteTo(writer, LocalRoomHash, AdjacentRoomHashes);
        }

        public static SimJoinRequest ReadFrom(BinaryReader reader)
        {
            uint version = reader.ReadUInt32();
            ulong nonce = reader.ReadUInt64();
            ulong local;
            ulong[] adjacent;
            SimInterestPayload.ReadFrom(reader, out local, out adjacent);
            return new SimJoinRequest
            {
                ProtocolVersion = version,
                ClientNonce = nonce,
                LocalRoomHash = local,
                AdjacentRoomHashes = adjacent,
            };
        }

        public readonly bool Equals(SimJoinRequest other) =>
            ProtocolVersion == other.ProtocolVersion && ClientNonce == other.ClientNonce
            && LocalRoomHash == other.LocalRoomHash;
        public override readonly bool Equals(object obj) => obj is SimJoinRequest other && Equals(other);
        public override readonly int GetHashCode() =>
            unchecked((int)ProtocolVersion * 397 ^ ClientNonce.GetHashCode());
    }

    /// <summary>
    /// Client → host when the peer's room graph changes. An added room is a reliable lifetime change
    /// (<see cref="SimReplicationInterestRules.RequiresNewLifetime"/>): the peer has no history there,
    /// so the host re-sends a full baseline rather than a delta against rooms it never saw.
    /// </summary>
    [Serializable]
    public struct SimInterestUpdate : IEquatable<SimInterestUpdate>
    {
        public ulong LocalRoomHash;
        public ulong[] AdjacentRoomHashes;

        public SimInterestUpdate(SimPeerInterest interest)
        {
            LocalRoomHash = interest?.LocalRoomHash ?? 0UL;
            AdjacentRoomHashes = interest?.ToArray() ?? Array.Empty<ulong>();
        }

        public readonly SimPeerInterest ToInterest() =>
            SimInterestPayload.ToInterest(LocalRoomHash, AdjacentRoomHashes);

        public readonly void WriteTo(BinaryWriter writer)
        {
            SimInterestPayload.WriteTo(writer, LocalRoomHash, AdjacentRoomHashes);
        }

        public static SimInterestUpdate ReadFrom(BinaryReader reader)
        {
            ulong local;
            ulong[] adjacent;
            SimInterestPayload.ReadFrom(reader, out local, out adjacent);
            return new SimInterestUpdate
            {
                LocalRoomHash = local,
                AdjacentRoomHashes = adjacent,
            };
        }

        public readonly bool Equals(SimInterestUpdate other) => LocalRoomHash == other.LocalRoomHash;
        public override readonly bool Equals(object obj) => obj is SimInterestUpdate other && Equals(other);
        public override readonly int GetHashCode() => LocalRoomHash.GetHashCode();
    }

    /// <summary>
    /// Host → client. <c>SessionId</c> is host-owned: the client cannot invent a session it then
    /// accepts packets from. <c>WorldRevision</c> names the peer's first reliable lifetime.
    /// </summary>
    [Serializable]
    public struct SimJoinAccept : IEquatable<SimJoinAccept>
    {
        public ulong SessionId;
        public uint PeerId;
        public uint ProtocolVersion;
        public long WorldRevision;

        public readonly void WriteTo(BinaryWriter writer)
        {
            writer.Write(SessionId);
            writer.Write(PeerId);
            writer.Write(ProtocolVersion);
            writer.Write(WorldRevision);
        }

        public static SimJoinAccept ReadFrom(BinaryReader reader)
        {
            return new SimJoinAccept
            {
                SessionId = reader.ReadUInt64(),
                PeerId = reader.ReadUInt32(),
                ProtocolVersion = reader.ReadUInt32(),
                WorldRevision = reader.ReadInt64(),
            };
        }

        public readonly bool Equals(SimJoinAccept other) =>
            SessionId == other.SessionId && PeerId == other.PeerId
            && ProtocolVersion == other.ProtocolVersion && WorldRevision == other.WorldRevision;
        public override readonly bool Equals(object obj) => obj is SimJoinAccept other && Equals(other);
        public override readonly int GetHashCode() =>
            unchecked(SessionId.GetHashCode() * 397 ^ (int)PeerId * 31 ^ (int)WorldRevision);
    }

    [Serializable]
    public struct SimJoinReject : IEquatable<SimJoinReject>
    {
        public SimJoinRejectReason Reason;
        public uint ExpectedProtocolVersion;

        public readonly void WriteTo(BinaryWriter writer)
        {
            writer.Write((byte)Reason);
            writer.Write(ExpectedProtocolVersion);
        }

        public static SimJoinReject ReadFrom(BinaryReader reader)
        {
            return new SimJoinReject
            {
                Reason = (SimJoinRejectReason)reader.ReadByte(),
                ExpectedProtocolVersion = reader.ReadUInt32(),
            };
        }

        public readonly bool Equals(SimJoinReject other) =>
            Reason == other.Reason && ExpectedProtocolVersion == other.ExpectedProtocolVersion;
        public override readonly bool Equals(object obj) => obj is SimJoinReject other && Equals(other);
        public override readonly int GetHashCode() => unchecked((int)Reason * 397 ^ (int)ExpectedProtocolVersion);
    }

    /// <summary>
    /// F-11's <c>WorldPartMessage { total, offset, data }</c> in pure form. Indexed rather than
    /// streamed, so a duplicate chunk is idempotent and a chunk delivered after a later one still
    /// lands in its own slot.
    /// </summary>
    [Serializable]
    public struct SimBaselineChunk : IEquatable<SimBaselineChunk>
    {
        public long WorldRevision;
        public int ChunkIndex;
        public int ChunkCount;
        public ulong PayloadHash;
        public byte[] Bytes;

        public readonly void WriteTo(BinaryWriter writer)
        {
            writer.Write(WorldRevision);
            writer.Write(ChunkIndex);
            writer.Write(ChunkCount);
            writer.Write(PayloadHash);
            byte[] bytes = Bytes;
            writer.Write(bytes?.Length ?? 0);
            if (bytes != null) writer.Write(bytes);
        }

        public static SimBaselineChunk ReadFrom(BinaryReader reader)
        {
            long revision = reader.ReadInt64();
            int index = reader.ReadInt32();
            int count = reader.ReadInt32();
            ulong hash = reader.ReadUInt64();
            int length = reader.ReadInt32();
            if (length < 0) throw new InvalidDataException("Negative baseline chunk length.");
            byte[] bytes = reader.ReadBytes(length);
            if (bytes.Length != length) throw new EndOfStreamException("Truncated baseline chunk.");
            return new SimBaselineChunk
            {
                WorldRevision = revision,
                ChunkIndex = index,
                ChunkCount = count,
                PayloadHash = hash,
                Bytes = bytes,
            };
        }

        public readonly bool Equals(SimBaselineChunk other)
        {
            if (WorldRevision != other.WorldRevision || ChunkIndex != other.ChunkIndex
                || ChunkCount != other.ChunkCount || PayloadHash != other.PayloadHash) return false;
            byte[] a = Bytes;
            byte[] b = other.Bytes;
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
        public override readonly bool Equals(object obj) => obj is SimBaselineChunk other && Equals(other);
        public override readonly int GetHashCode() =>
            unchecked(ChunkIndex * 397 ^ ChunkCount * 31 ^ PayloadHash.GetHashCode());
    }

    /// <summary>
    /// Client → host. The receiver's own last applied sequence, not a number the client guessed:
    /// <c>LastEventSequence</c> is the readback of <see cref="SimSnapshotReceiver"/>, so an ack cannot
    /// claim geometry the receiver refused.
    /// </summary>
    [Serializable]
    public struct SimSnapshotAck : IEquatable<SimSnapshotAck>
    {
        public long WorldRevision;
        public uint LastEventSequence;
        public uint SnapshotSequence;

        public readonly void WriteTo(BinaryWriter writer)
        {
            writer.Write(WorldRevision);
            writer.Write(LastEventSequence);
            writer.Write(SnapshotSequence);
        }

        public static SimSnapshotAck ReadFrom(BinaryReader reader)
        {
            return new SimSnapshotAck
            {
                WorldRevision = reader.ReadInt64(),
                LastEventSequence = reader.ReadUInt32(),
                SnapshotSequence = reader.ReadUInt32(),
            };
        }

        public readonly bool Equals(SimSnapshotAck other) =>
            WorldRevision == other.WorldRevision
            && LastEventSequence == other.LastEventSequence
            && SnapshotSequence == other.SnapshotSequence;
        public override readonly bool Equals(object obj) => obj is SimSnapshotAck other && Equals(other);
        public override readonly int GetHashCode() =>
            unchecked(WorldRevision.GetHashCode() * 397 ^ (int)LastEventSequence * 31 ^ (int)SnapshotSequence);
    }

    /// <summary>
    /// Client → host: one peer's room change. A room transition is a discontinuous teleport —
    /// intents flowing in command batches integrate continuous motion and cannot say "I stepped
    /// through a door and am now in room X at this spot". The host simulates the peer's body, so
    /// without this message the body keeps colliding against the room the peer already left,
    /// which reads on both screens as a position desync.
    ///
    /// <para><c>RoomId</c> is the room's own id (the same string <c>SimSnapshotProjectionRules
    /// .RoomKey</c> hashes), not the hash: the host must find the live <c>RoomInstance</c>, and a
    /// hash collision would silently bind the body to the wrong room. The position is WORLD
    /// pixels — the one space both processes agree on verbatim (the world contract guarantees
    /// identical geometry), whereas room-local pixels would need the very room lookup the host
    /// is about to do.</para>
    ///
    /// <para>Declared once after join even when the peer never moves: the host builds the peer's
    /// body from the declaration instead of guessing the host player's own room.</para>
    /// </summary>
    [Serializable]
    public struct SimPeerRoomTransition : IEquatable<SimPeerRoomTransition>
    {
        /// <summary>The longest room id the wire admits. Room ids are short coordinate strings;
        /// a length prefix above this is corruption, not a big room.</summary>
        public const int MaxRoomIdLength = 128;

        public ulong SessionId;
        public uint PeerId;
        public string RoomId;
        public float WorldPixelX;
        public float WorldPixelY;

        public readonly void WriteTo(BinaryWriter writer)
        {
            writer.Write(SessionId);
            writer.Write(PeerId);
            writer.Write(RoomId ?? string.Empty);
            writer.Write(WorldPixelX);
            writer.Write(WorldPixelY);
        }

        /// <summary>Strict read. Refuses a null/oversized room id and non-finite coordinates:
        /// a NaN arrival position would silently poison the host body's authoritative pixel state.</summary>
        public static SimPeerRoomTransition ReadFrom(BinaryReader reader)
        {
            ulong session = reader.ReadUInt64();
            uint peer = reader.ReadUInt32();
            string roomId = reader.ReadString();
            float px = reader.ReadSingle();
            float py = reader.ReadSingle();

            if (string.IsNullOrEmpty(roomId) || roomId.Length > MaxRoomIdLength)
            {
                throw new InvalidDataException("Peer room transition names no room or an absurd one.");
            }
            if (float.IsNaN(px) || float.IsInfinity(px) || float.IsNaN(py) || float.IsInfinity(py))
            {
                throw new InvalidDataException("Peer room transition carries a non-finite position.");
            }

            return new SimPeerRoomTransition
            {
                SessionId = session,
                PeerId = peer,
                RoomId = roomId,
                WorldPixelX = px,
                WorldPixelY = py,
            };
        }

        public readonly bool Equals(SimPeerRoomTransition other) =>
            SessionId == other.SessionId && PeerId == other.PeerId
            && string.Equals(RoomId, other.RoomId, StringComparison.Ordinal)
            && WorldPixelX.Equals(other.WorldPixelX) && WorldPixelY.Equals(other.WorldPixelY);
        public override readonly bool Equals(object obj) => obj is SimPeerRoomTransition other && Equals(other);
        public override readonly int GetHashCode() =>
            unchecked(SessionId.GetHashCode() * 397 ^ (int)PeerId * 31 ^ (RoomId?.Length ?? 0));
    }

    /// <summary>The host-side consumer of admitted peer room transitions. The view layer implements
    /// this to move the peer's simulated body into the named room; keeping it as one method means
    /// the pure layer never learns what a <c>MonoBehaviour</c>, a slot or a motor is — the same
    /// seam shape as <see cref="ISimCommandSink"/>.</summary>
    public interface ISimRoomTransitionSink
    {
        /// <summary>One admitted transition. <paramref name="slot"/> is the host-assigned controller
        /// slot for the peer; the position is world pixels, matching
        /// <see cref="SimPeerRoomTransition"/>.</summary>
        void Apply(uint peerId, byte slot, string roomId, float worldPixelX, float worldPixelY);
    }

    /// <summary>Strict single-payload codecs. Each refuses trailing bytes, so a corrupted kind cannot
    /// be read as a shorter message that happens to parse.</summary>
    public static class SimReplicationPayloadSerializer
    {
        public static byte[] Serialize(SimJoinRequest value) => Write(w => value.WriteTo(w));
        public static byte[] Serialize(SimJoinAccept value) => Write(w => value.WriteTo(w));
        public static byte[] Serialize(SimJoinReject value) => Write(w => value.WriteTo(w));
        public static byte[] Serialize(SimBaselineChunk value) => Write(w => value.WriteTo(w));
        public static byte[] Serialize(SimSnapshotAck value) => Write(w => value.WriteTo(w));
        public static byte[] Serialize(SimInterestUpdate value) => Write(w => value.WriteTo(w));
        public static byte[] Serialize(SimPlayerCommandBatch value) => Write(w => value.WriteTo(w));
        public static byte[] Serialize(SimPeerRoomTransition value) => Write(w => value.WriteTo(w));

        private static byte[] Write(Action<BinaryWriter> body)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                body(writer);
                writer.Flush();
                return stream.ToArray();
            }
        }

        /// <summary>
        /// Reads one command window. <paramref name="maxCommands"/> is supplied by the session layer,
        /// not invented here: the pure endpoint owns no transport constant, and a forged length prefix
        /// must not be able to allocate an unbounded array.
        /// </summary>
        public static SimPlayerCommandBatch ReadPlayerCommandBatch(byte[] bytes, int maxCommands) =>
            Read(reader => SimPlayerCommandBatch.ReadFrom(reader, maxCommands), bytes);

        public static SimJoinRequest ReadJoinRequest(byte[] bytes) => Read(SimJoinRequest.ReadFrom, bytes);
        public static SimJoinAccept ReadJoinAccept(byte[] bytes) => Read(SimJoinAccept.ReadFrom, bytes);
        public static SimJoinReject ReadJoinReject(byte[] bytes) => Read(SimJoinReject.ReadFrom, bytes);
        public static SimBaselineChunk ReadBaselineChunk(byte[] bytes) => Read(SimBaselineChunk.ReadFrom, bytes);
        public static SimSnapshotAck ReadSnapshotAck(byte[] bytes) => Read(SimSnapshotAck.ReadFrom, bytes);
        public static SimInterestUpdate ReadInterestUpdate(byte[] bytes) => Read(SimInterestUpdate.ReadFrom, bytes);
        public static SimPeerRoomTransition ReadPeerRoomTransition(byte[] bytes) =>
            Read(SimPeerRoomTransition.ReadFrom, bytes);

        private static T Read<T>(Func<BinaryReader, T> body, byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            using (var stream = new MemoryStream(bytes, false))
            using (var reader = new BinaryReader(stream))
            {
                T value = body(reader);
                if (stream.Position != stream.Length)
                {
                    throw new InvalidDataException("Trailing bytes after replication payload.");
                }
                return value;
            }
        }
    }
}
