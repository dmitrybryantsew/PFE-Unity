using System;

namespace PFE.Sim.State
{
    /// <summary>
    /// One reliable, ordered tile-mutation event — the payload §10.2 gap 1 was missing.
    ///
    /// <para><b>Why this is a snapshot-level event and not a field of <see cref="SimRoomDelta"/>.</b>
    /// §10's tile row says "reliable ordered event with worldRevision, <b>never a delta</b>". A delta
    /// is what an unreliable channel may lose; geometry cannot be rebuilt from a lost packet, so a
    /// tile event belongs to the reliable stream that the snapshot envelope repeats until it is
    /// consumed. Each event carries its own <see cref="RoomHash"/>, so the journal needs no per-room
    /// container and interest management can filter it without changing the wire shape.</para>
    ///
    /// <para><b>What "new tile state" means here.</b> The port's local mutation seam
    /// (<c>ITileQueryService.TileMutation</c>) is exactly <c>(coord, physicsType, hitPoints)</c>: those
    /// are the only tile fields a runtime mutation writes, and they are the only ones
    /// <c>ApplyMutations</c> reads back. Graphics ids, strings and door/trap ids are content, never
    /// change at runtime, and stay in the one-time full baseline. Carrying them per event would be a
    /// decorative field the receiver could not apply.</para>
    ///
    /// <para><b>Reliability contract.</b> <see cref="WorldRevision"/> names the reliable-event
    /// lifetime; <see cref="EventSequence"/> is a wrap-safe, contiguous, monotonically increasing
    /// counter within that lifetime. The host repeats the whole journal for the current revision in
    /// every snapshot until an ack exists (Phase 4 owns the ack); a receiver dedupes by
    /// <c>(WorldRevision, EventSequence)</c> and rejects an out-of-order gap rather than applying a
    /// partial geometry change. A room-set change starts a new lifetime and the host re-emits every
    /// tile of every active room as a full baseline — F3 §2.6's interim rule, in event form.</para>
    ///
    /// <para><b>Pure C#.</b> No <c>UnityEngine</c>, no <c>Vector2Int</c>, no strings: coordinates are
    /// ints, the physics enum is an int, identity is a ulong. <c>SimStateRecordPurityLintTests</c>
    /// pins that.</para>
    /// </summary>
    [Serializable]
    public struct SimTileMutation : IEquatable<SimTileMutation>
    {
        /// <summary>The room whose tile changed — the same FNV-1a key as <c>SimRoomDelta.RoomHash</c>.</summary>
        [SimReconcile(SimReconcileRule.Key)]
        public ulong RoomHash;

        /// <summary>The reliable-event lifetime this event belongs to. Must equal the carrying
        /// snapshot's <c>WorldRevision</c>; a mismatch is a corrupt payload, not a late one.</summary>
        [SimReconcile(SimReconcileRule.RejectIfStale)]
        public long WorldRevision;

        /// <summary>Contiguous, wrap-safe ordering inside <see cref="WorldRevision"/>. A gap means the
        /// reliable stream was broken and the geometry must not be applied partially.</summary>
        [SimReconcile(SimReconcileRule.RejectIfStale)]
        public uint EventSequence;

        /// <summary>Room-local tile X.</summary>
        [SimReconcile(SimReconcileRule.Key)]
        public int TileX;

        /// <summary>Room-local tile Y.</summary>
        [SimReconcile(SimReconcileRule.Key)]
        public int TileY;

        /// <summary>The tile's new <c>TilePhysicsType</c>, carried as an int so the record stays pure.
        /// <c>Air</c> means the tile was destroyed.</summary>
        [SimReconcile(SimReconcileRule.Snap)]
        public int PhysicsType;

        /// <summary>Remaining hit points after the mutation.</summary>
        [SimReconcile(SimReconcileRule.Snap)]
        public int HitPoints;

        // ── Wire serialization (v4) ──────────────────────────────────────────

        /// <summary>Appends this event to <paramref name="writer"/> in declaration order.</summary>
        public readonly void WriteTo(System.IO.BinaryWriter writer)
        {
            writer.Write(RoomHash);
            writer.Write(WorldRevision);
            writer.Write(EventSequence);
            writer.Write(TileX);
            writer.Write(TileY);
            writer.Write(PhysicsType);
            writer.Write(HitPoints);
        }

        /// <summary>Reads one event in the same order <see cref="WriteTo"/> wrote it.</summary>
        public static SimTileMutation ReadFrom(System.IO.BinaryReader reader)
        {
            return new SimTileMutation
            {
                RoomHash = reader.ReadUInt64(),
                WorldRevision = reader.ReadInt64(),
                EventSequence = reader.ReadUInt32(),
                TileX = reader.ReadInt32(),
                TileY = reader.ReadInt32(),
                PhysicsType = reader.ReadInt32(),
                HitPoints = reader.ReadInt32(),
            };
        }

        public readonly bool Equals(SimTileMutation other)
        {
            return RoomHash == other.RoomHash
                && WorldRevision == other.WorldRevision
                && EventSequence == other.EventSequence
                && TileX == other.TileX
                && TileY == other.TileY
                && PhysicsType == other.PhysicsType
                && HitPoints == other.HitPoints;
        }

        public override readonly bool Equals(object obj) => obj is SimTileMutation other && Equals(other);

        public override readonly int GetHashCode()
        {
            unchecked
            {
                int hash = RoomHash.GetHashCode();
                hash = (hash * 397) ^ WorldRevision.GetHashCode();
                hash = (hash * 397) ^ (int)EventSequence;
                hash = (hash * 397) ^ TileX;
                hash = (hash * 397) ^ TileY;
                hash = (hash * 397) ^ PhysicsType;
                hash = (hash * 397) ^ HitPoints;
                return hash;
            }
        }

        public override readonly string ToString()
        {
            return $"SimTileMutation(room={RoomHash:X16} rev={WorldRevision} seq={EventSequence} " +
                   $"tile=({TileX},{TileY}) phis={PhysicsType} hp={HitPoints})";
        }
    }
}
