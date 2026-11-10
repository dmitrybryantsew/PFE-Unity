using System;
using System.IO;

namespace PFE.Sim.State
{
    /// <summary>
    /// The versioned wire codec for <see cref="SimSnapshot"/> — Phase 1.4 of the multiplayer readiness
    /// plan (F-01/F-02/F-06). Turns a snapshot into a self-describing byte payload and back, with the
    /// staleness guards the readiness doc's round-trip acceptance (§8 row 1.4, §11 item 1) requires.
    ///
    /// <para><b>Wire layout.</b> A fixed-size magic, then the protocol version, then the body:
    /// <code>
    ///   [0..3]  magic "PFES"                  — not a snapshot at all if absent
    ///   [4..7]  uint   ProtocolVersion        — reject if != CurrentProtocolVersion
    ///   [8..]   SimSnapshot body              — header scalars, per-room deltas, then the
    ///                                          reliable tile-mutation journal (v4)
    /// </code>
    /// The version is read <i>before</i> any body field, so an incompatible peer is rejected before a
    /// single record is interpreted (Remains <c>ProtocolAdmission</c>). The body is written by the
    /// records' own <c>WriteTo</c>/<c>ReadFrom</c> in declaration order — both ends must agree, which
    /// is exactly what the version number exists to flag when the layout changes.</para>
    ///
    /// <para><b>Determinism and endianness.</b> <see cref="BinaryWriter"/>/<see cref="BinaryReader"/>
    /// are little-endian on every platform PFE targets, and the field order is fixed by the records, so
    /// the same snapshot always serializes to the same bytes on host and client. This is what makes the
    /// §8 acceptance — <c>serialize → deserialize → state hash</c> — meaningful.</para>
    ///
    /// <para><b>Strict reads.</b> Every read is bounds-checked: a truncated payload throws
    /// <see cref="EndOfStreamException"/> rather than silently fabricating state, and a payload with
    /// trailing bytes after the last record is rejected as corrupt. A negative or absurd length prefix
    /// is rejected as corrupt rather than trusted to allocate. These are the corruption cases §11 item
    /// 1 names (truncated delta, plus the wrong-version case the header handles); each gets its own
    /// failing assertion in the round-trip tests.</para>
    ///
    /// <para><b>Pure C#.</b> No <c>UnityEngine</c> — the codec lives in <c>PFE.Sim</c> beside the
    /// records so it can be exercised headless and so the snapshot never depends on the view layer.</para>
    /// </summary>
    public static class SimSnapshotSerializer
    {
        /// <summary>The only wire version this build can read. Bump when the record layout changes in
        /// an incompatible way; a peer at any other version is rejected.</summary>
        /// <remarks>v2 (2026-11-05, Phase 1.5 / F-02): <c>SimUnitState</c> gained the motor-less
        /// unit's live motion/gait half (Vx/Vy, IsGrounded, DropThroughPlatforms, OverhangLeft/Right,
        /// SupportCarryX/Y), written between <c>FacingDirection</c> and <c>Health</c>. A v1 reader
        /// would misalign every subsequent field, so the version guard must reject it before any
        /// body field is interpreted.
        ///
        /// <para>v3 (2026-11-08, Phase 3.3): <c>SimProjectileState</c> gained <c>Kind</c>, written
        /// immediately after <c>DefinitionId</c>. The array is now the <i>transient entity</i> list —
        /// fired rounds, thrown objects and placed mines — so a v2 reader would take a grenade's
        /// position bytes as its velocity, and the record would be garbage rather than merely
        /// incomplete.
        ///
        /// <para>v4 (2026-11-09, §10.2): both gaps landed as incompatible layout changes.
        /// <c>SimDroppedItem</c> gained a host-assigned <c>EntityHash</c> written <b>before</b>
        /// <c>ItemIdHash</c>, and <c>SimSnapshot</c> gained a trailing reliable
        /// <c>SimTileMutation[] TileMutations</c> journal written after <c>Rooms</c>. A v3 reader
        /// would read the first loot id as the content id and then interpret the journal's count as
        /// trailing bytes — so the version guard must reject before a single record is read.</para>
        /// </remarks>
        public const uint CurrentProtocolVersion = 4;

        /// <summary>"PFES" — PFE Snapshot. Rejects a payload that is not a snapshot at all before the
        /// version is even read.</summary>
        private static readonly byte[] Magic = { (byte)'P', (byte)'F', (byte)'E', (byte)'S' };

        /// <summary>Serialize <paramref name="snapshot"/> to a self-describing byte payload.</summary>
        public static byte[] Serialize(in SimSnapshot snapshot)
        {
            using var stream = new MemoryStream(capacity: 256);
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Magic);
                writer.Write(snapshot.ProtocolVersion);
                WriteBody(writer, in snapshot);
            }
            return stream.ToArray();
        }

        /// <summary>
        /// Deserialize a payload back into a <see cref="SimSnapshot"/>.
        /// </summary>
        /// <exception cref="InvalidDataException">
        /// The payload is not a snapshot (bad magic), is a different protocol version, is truncated, or
        /// has trailing bytes after the last record.
        /// </exception>
        public static SimSnapshot Deserialize(byte[] payload)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));

            using var stream = new MemoryStream(payload, writable: false);
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);

            byte[] magic = ReadExact(reader, Magic.Length, "magic");
            for (int i = 0; i < Magic.Length; i++)
            {
                if (magic[i] != Magic[i])
                {
                    throw new InvalidDataException(
                        "Not a PFE snapshot: bad magic. The payload is corrupt or not a snapshot at all.");
                }
            }

            uint version = ReadUInt32(reader, "protocol version");
            if (version != CurrentProtocolVersion)
            {
                throw new InvalidDataException(
                    $"Unsupported snapshot protocol version {version}; this build reads " +
                    $"{CurrentProtocolVersion}. Rejecting an incompatible peer before interpreting a byte.");
            }

            SimSnapshot snapshot = ReadBody(reader);

            if (stream.Position != stream.Length)
            {
                throw new InvalidDataException(
                    $"Corrupt snapshot: {stream.Length - stream.Position} trailing byte(s) after the " +
                    "last record. The payload does not match the version it claims.");
            }

            return snapshot;
        }

        /// <summary>Convenience for the round-trip acceptance: serialize, deserialize, and return the
        /// state hash of the result. Equal to <c>snapshot.ComputeStateHash()</c> when the codec is
        /// lossless.</summary>
        public static ulong RoundTripStateHash(in SimSnapshot snapshot)
        {
            return Deserialize(Serialize(in snapshot)).ComputeStateHash();
        }

        // ── Body ──────────────────────────────────────────────────────────────

        private static void WriteBody(BinaryWriter writer, in SimSnapshot snapshot)
        {
            writer.Write(snapshot.TickIndex);
            writer.Write(snapshot.SnapshotSequence);
            writer.Write(snapshot.WorldRevision);
            writer.Write(snapshot.BaselineHash);

            SimRoomDelta[] rooms = snapshot.Rooms;
            writer.Write(rooms?.Length ?? 0);
            if (rooms != null)
            {
                for (int i = 0; i < rooms.Length; i++)
                {
                    rooms[i].WriteTo(writer);
                }
            }

            // Reliable events are written after the deltas, never inside one. The host repeats the
            // whole journal for the current WorldRevision until an ack exists; a v3 reader would see
            // these bytes as trailing garbage, which is exactly what the version guard prevents.
            SimTileMutation[] tileMutations = snapshot.TileMutations;
            writer.Write(tileMutations?.Length ?? 0);
            if (tileMutations != null)
            {
                for (int i = 0; i < tileMutations.Length; i++)
                {
                    tileMutations[i].WriteTo(writer);
                }
            }
        }

        private static SimSnapshot ReadBody(BinaryReader reader)
        {
            var snapshot = new SimSnapshot
            {
                ProtocolVersion = CurrentProtocolVersion,
                TickIndex = ReadInt64(reader, "tick index"),
                SnapshotSequence = ReadUInt32(reader, "snapshot sequence"),
                WorldRevision = ReadInt64(reader, "world revision"),
                BaselineHash = ReadUInt64(reader, "baseline hash"),
            };

            int roomCount = ReadCount(reader, "room count");
            var rooms = new SimRoomDelta[roomCount];
            for (int i = 0; i < roomCount; i++)
            {
                rooms[i] = SimRoomDelta.ReadFrom(reader);
            }
            snapshot.Rooms = rooms;

            int eventCount = ReadCount(reader, "tile mutation count");
            var tileMutations = new SimTileMutation[eventCount];
            for (int i = 0; i < eventCount; i++)
            {
                tileMutations[i] = SimTileMutation.ReadFrom(reader);
            }
            snapshot.TileMutations = tileMutations;
            return snapshot;
        }

        // ── Strict primitives ─────────────────────────────────────────────────

        /// <summary>Reads a length/count prefix and rejects a negative or absurd value, so a corrupt
        /// count cannot drive a huge allocation or a nonsense loop.</summary>
        private static int ReadCount(BinaryReader reader, string what)
        {
            int count = ReadInt32(reader, what);
            // A snapshot never legitimately holds more than this many of any one list; the cap turns a
            // corrupt count into a clear error instead of an out-of-memory. 1M is far past any room.
            const int MaxReasonableCount = 1 << 20;
            if (count < 0 || count > MaxReasonableCount)
            {
                throw new InvalidDataException(
                    $"Corrupt snapshot: {what} of {count} is out of range [0, {MaxReasonableCount}].");
            }
            return count;
        }

        private static byte[] ReadExact(BinaryReader reader, int count, string what)
        {
            byte[] bytes = reader.ReadBytes(count);
            if (bytes.Length != count)
            {
                throw new InvalidDataException(
                    $"Truncated snapshot: expected {count} byte(s) for {what}, got {bytes.Length}.");
            }
            return bytes;
        }

        private static uint ReadUInt32(BinaryReader reader, string what)
        {
            return BitConverter.ToUInt32(ReadExact(reader, 4, what), 0);
        }

        private static int ReadInt32(BinaryReader reader, string what)
        {
            return BitConverter.ToInt32(ReadExact(reader, 4, what), 0);
        }

        private static long ReadInt64(BinaryReader reader, string what)
        {
            return BitConverter.ToInt64(ReadExact(reader, 8, what), 0);
        }

        private static ulong ReadUInt64(BinaryReader reader, string what)
        {
            return BitConverter.ToUInt64(ReadExact(reader, 8, what), 0);
        }
    }
}
