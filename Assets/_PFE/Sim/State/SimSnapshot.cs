using System;

namespace PFE.Sim.State
{
    /// <summary>
    /// One versioned, self-describing snapshot of the whole replicated Sim for one tick — the object
    /// the host serializes onto the wire and a client deserializes, hashes, and reconciles against.
    ///
    /// <para><b>F-01/F-02/F-06, Phase 1.4 of the multiplayer readiness plan.</b> §8 row 1.4 and §9 of
    /// the readiness doc sketch the shape; this is the concrete record. It is pure C#: no
    /// <c>UnityEngine.Object</c>, no <c>Transform</c>. The readiness doc's design intent (§5, quoting
    /// <c>docs/Roadmap/05</c> §1.1) is <i>"the Sim state snapshot is the network state — there is no
    /// separate replication layer to design."</i> This is that payload. §7.4 warns against a big
    /// mutable <c>WorldState</c> with <c>Guid</c> defaults and class lists; this snapshot is a value
    /// type with an explicit <see cref="TickIndex"/>, <see cref="WorldRevision"/>, and room-scoped
    /// deltas for exactly that reason.</para>
    ///
    /// <para><b>The header is the staleness machinery (F-06).</b> <see cref="ProtocolVersion"/> rejects
    /// an incompatible peer before any field is read (Remains <c>ProtocolAdmission.Version</c>).
    /// <see cref="SnapshotSequence"/> is the wrap-safe ordering on the unreliable channel — an older
    /// snapshot must never overwrite newer events (§10 — "reject if stale"). <see cref="WorldRevision"/>
    /// ties the delta to the reliable-event stream (tile mutations, spawns) so a delayed delta from a
    /// previous lifetime is dropped. <see cref="BaselineHash"/> names the ack'd baseline this delta is
    /// against, for delta compression (§9, Phase 3.6). <see cref="TickIndex"/> is the Sim tick the
    /// state describes — the same tick a <c>PlayerCommand</c> names, so prediction and reconciliation
    /// speak one time.</para>
    ///
    /// <para><b>The body is room deltas, interest-filtered.</b> <see cref="Rooms"/> carries one
    /// <see cref="SimRoomDelta"/> per active room — the set the host's interest management (F-04/F-05,
    /// Phase 3.1) decided this client needs, not the whole world. A room the client is not interested
    /// in is simply absent.</para>
    ///
    /// <para><b>Round-trip acceptance (§8 row 1.4).</b> <c>serialize → deserialize → state hash</c>
    /// must match, and a wrong-version payload must be rejected. <see cref="ComputeStateHash"/> is a
    /// deterministic 64-bit FNV-1a over every field in declaration order (the same construction
    /// <c>EntityId.ComputeFnv1a64</c> and the golden-trace <c>TraceRecorder.ComputeHash</c> use), so
    /// two snapshots that differ in any bit hash differently, and a round-tripped snapshot hashes
    /// identically to its source. The version check lives in <c>SimSnapshotSerializer</c>, not here,
    /// because it is a property of the wire, not of the state.</para>
    /// </summary>
    [Serializable]
    public struct SimSnapshot : IEquatable<SimSnapshot>
    {
        /// <summary>Wire format version. Bumped on any incompatible change to the record layout; a
        /// peer reading a different version rejects the payload before interpreting a byte.</summary>
        [SimReconcile(SimReconcileRule.RejectIfIncompatible)]
        public uint ProtocolVersion;
        /// <summary>The Sim tick this snapshot describes. Names the same time a <c>PlayerCommand</c>
        /// does, so a client can reconcile a host state against its own predicted tick.</summary>
        [SimReconcile(SimReconcileRule.RejectIfStale)]
        public long TickIndex;
        /// <summary>Wrap-safe ordering on the unreliable snapshot channel; stale snapshots are
        /// rejected by <c>IsNewer</c>-style compare (F-06).</summary>
        [SimReconcile(SimReconcileRule.RejectIfStale)]
        public uint SnapshotSequence;
        /// <summary>Monotonic world revision tying this delta to the reliable-event stream; a delta
        /// from a previous world lifetime is rejected (F-06).</summary>
        [SimReconcile(SimReconcileRule.RejectIfStale)]
        public long WorldRevision;
        /// <summary>Hash of the ack'd baseline this delta is computed against (Phase 3.6 delta
        /// compression). Zero for a full baseline.</summary>
        [SimReconcile(SimReconcileRule.RejectIfStale)]
        public ulong BaselineHash;
        /// <summary>One delta per interest-filtered active room. Absent rooms are ones this client
        /// does not need.</summary>
        [SimReconcile(SimReconcileRule.Snap)]
        public SimRoomDelta[] Rooms;

        /// <summary>The reliable ordered tile-mutation journal for <see cref="WorldRevision"/> — the
        /// §10.2 gap-1 payload. This is <b>not</b> a delta: the host repeats the whole journal in every
        /// snapshot until an ack exists, and a receiver dedupes by
        /// <c>(WorldRevision, EventSequence)</c>. Empty when no tile has changed in this lifetime.</summary>
        [SimReconcile(SimReconcileRule.ReliableOrderedEvent)]
        public SimTileMutation[] TileMutations;

        public readonly bool Equals(SimSnapshot other)
        {
            if (ProtocolVersion != other.ProtocolVersion
                || TickIndex != other.TickIndex
                || SnapshotSequence != other.SnapshotSequence
                || WorldRevision != other.WorldRevision
                || BaselineHash != other.BaselineHash)
            {
                return false;
            }

            SimRoomDelta[] a = Rooms, b = other.Rooms;
            // A shared Rooms reference is NOT a licence to return true: equal rooms say nothing about
            // the tile journal that was appended at v4. Fall through to the journal comparison below.
            if (!ReferenceEquals(a, b))
            {
                if (a == null || b == null || a.Length != b.Length) return false;
                for (int i = 0; i < a.Length; i++)
                {
                    if (!a[i].Equals(b[i])) return false;
                }
            }

            // The reliable journal is optional: a producer that has no tile changes may leave it null,
            // while the codec reads a zero count back as an empty array. Those are the same state, so
            // they must compare equal or a plain round-trip would report a false divergence.
            SimTileMutation[] ta = TileMutations, tb = other.TileMutations;
            int taLength = ta?.Length ?? 0, tbLength = tb?.Length ?? 0;
            if (taLength != tbLength) return false;
            for (int i = 0; i < taLength; i++)
            {
                if (!ta[i].Equals(tb[i])) return false;
            }
            return true;
        }

        public override readonly bool Equals(object obj) => obj is SimSnapshot other && Equals(other);

        public override readonly int GetHashCode()
        {
            unchecked
            {
                int hash = (int)ProtocolVersion;
                hash = (hash * 397) ^ TickIndex.GetHashCode();
                hash = (hash * 397) ^ (int)SnapshotSequence;
                hash = (hash * 397) ^ WorldRevision.GetHashCode();
                hash = (hash * 397) ^ BaselineHash.GetHashCode();
                if (Rooms != null)
                {
                    for (int i = 0; i < Rooms.Length; i++)
                    {
                        hash = (hash * 397) ^ Rooms[i].GetHashCode();
                    }
                }
                if (TileMutations != null)
                {
                    for (int i = 0; i < TileMutations.Length; i++)
                    {
                        hash = (hash * 397) ^ TileMutations[i].GetHashCode();
                    }
                }
                return hash;
            }
        }

        /// <summary>
        /// A deterministic 64-bit FNV-1a hash over every field of the snapshot, in declaration order,
        /// recursing into each room delta and its element records. This is the round-trip acceptance
        /// hash of §8 row 1.4: <c>serialize → deserialize → ComputeStateHash</c> must be invariant.
        ///
        /// <para><b>Why FNV-1a over the field values, not over the serialized bytes.</b> The hash is a
        /// property of the <i>state</i>, not of one encoding of it — the same snapshot must hash
        /// identically whether it was built by hand, read off the wire, or re-simulated. Hashing the
        /// canonical field values (rather than a byte buffer) keeps the hash independent of the wire
        /// format's versioning. The byte-mixing matches <c>EntityId.ComputeFnv1a64</c> and the
        /// golden-trace <c>TraceRecorder.ComputeHash</c>, so the whole project hashes state one way.</para>
        /// </summary>
        public readonly ulong ComputeStateHash()
        {
            const ulong basis = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;

            ulong hash = basis;
            hash = Mix(hash, prime, ProtocolVersion);
            hash = Mix(hash, prime, unchecked((ulong)TickIndex));
            hash = Mix(hash, prime, SnapshotSequence);
            hash = Mix(hash, prime, unchecked((ulong)WorldRevision));
            hash = Mix(hash, prime, BaselineHash);

            if (Rooms != null)
            {
                hash = Mix(hash, prime, (uint)Rooms.Length);
                for (int i = 0; i < Rooms.Length; i++)
                {
                    hash = HashRoom(hash, prime, Rooms[i]);
                }
            }
            else
            {
                hash = Mix(hash, prime, 0u);
            }

            // The reliable journal is hashed after the rooms it belongs to. A null journal and an
            // empty one both mix a single zero length, which matches SimSnapshot.Equals' treatment of
            // them as the same state.
            hash = HashArray(hash, prime, TileMutations, HashTileMutation);

            return hash;
        }

        private static ulong HashRoom(ulong hash, ulong prime, in SimRoomDelta room)
        {
            hash = Mix(hash, prime, room.RoomHash);
            hash = Mix(hash, prime, unchecked((ulong)room.LifecycleSequence));
            hash = HashArray(hash, prime, room.Units, HashUnit);
            hash = HashArray(hash, prime, room.Motors, HashMotor);
            hash = HashArray(hash, prime, room.Projectiles, HashProjectile);
            hash = HashArray(hash, prime, room.DroppedItems, HashDroppedItem);
            hash = HashArray(hash, prime, room.RemovedEntityHashes, (ulong h, ulong p, in ulong v) => Mix(h, p, v));
            return hash;
        }

        private static ulong HashUnit(ulong hash, ulong prime, in SimUnitState u)
        {
            hash = Mix(hash, prime, u.EntityHash);
            hash = Mix(hash, prime, unchecked((ulong)u.Generation));
            hash = Mix(hash, prime, unchecked((ulong)u.LifecycleSequence));
            hash = Mix(hash, prime, unchecked((ulong)u.ControllerSlot));
            hash = Mix(hash, prime, u.DefinitionId);
            hash = Mix(hash, prime, u.Px); hash = Mix(hash, prime, u.Py);
            hash = Mix(hash, prime, unchecked((ulong)u.FacingDirection));
            hash = Mix(hash, prime, u.Vx); hash = Mix(hash, prime, u.Vy);
            hash = Mix(hash, prime, u.IsGrounded); hash = Mix(hash, prime, u.DropThroughPlatforms);
            hash = Mix(hash, prime, u.OverhangLeft); hash = Mix(hash, prime, u.OverhangRight);
            hash = Mix(hash, prime, u.SupportCarryX); hash = Mix(hash, prime, u.SupportCarryY);
            hash = Mix(hash, prime, u.Health); hash = Mix(hash, prime, u.MaxHealth);
            hash = Mix(hash, prime, u.HasRuntimeHealth);
            hash = Mix(hash, prime, u.IsDead); hash = Mix(hash, prime, u.IsDown);
            hash = Mix(hash, prime, u.IsDissolving);
            hash = Mix(hash, prime, u.DissolveProgress);
            hash = Mix(hash, prime, unchecked((ulong)u.Faction));
            hash = Mix(hash, prime, unchecked((ulong)u.Digger));
            hash = Mix(hash, prime, u.AmbushArmed);
            hash = Mix(hash, prime, u.PoseId);
            hash = Mix(hash, prime, u.PoseFrame);
            hash = Mix(hash, prime, u.PoseSequence);
            return hash;
        }

        private static ulong HashMotor(ulong hash, ulong prime, in SimMotorState m)
        {
            hash = Mix(hash, prime, m.EntityHash);
            hash = Mix(hash, prime, m.Px); hash = Mix(hash, prime, m.Py);
            hash = Mix(hash, prime, m.Vx); hash = Mix(hash, prime, m.Vy);
            hash = Mix(hash, prime, m.IsGrounded); hash = Mix(hash, prime, m.IsOnPlatform);
            hash = Mix(hash, prime, m.IsInWater); hash = Mix(hash, prime, m.IsFullySubmerged);
            hash = Mix(hash, prime, m.IsOnLadder); hash = Mix(hash, prime, m.IsCrouching);
            hash = Mix(hash, prime, m.HitCeiling); hash = Mix(hash, prime, m.WallLeft);
            hash = Mix(hash, prime, m.WallRight);
            hash = Mix(hash, prime, unchecked((ulong)m.FacingDirection));
            hash = Mix(hash, prime, m.PendingVerticalImpulse);
            hash = Mix(hash, prime, m.DashTimer);
            hash = Mix(hash, prime, m.DashVelocityX); hash = Mix(hash, prime, m.DashVelocityY);
            hash = Mix(hash, prime, m.PlatformDropTimer);
            hash = Mix(hash, prime, unchecked((ulong)m.ActiveLadderDirection));
            hash = Mix(hash, prime, m.ActiveLadderSnapX);
            hash = Mix(hash, prime, m.GravityScale);
            hash = Mix(hash, prime, m.SupportCarryX); hash = Mix(hash, prime, m.SupportCarryY);
            return hash;
        }

        private static ulong HashProjectile(ulong hash, ulong prime, in SimProjectileState p)
        {
            hash = Mix(hash, prime, p.EntityHash);
            hash = Mix(hash, prime, p.LauncherHash);
            hash = Mix(hash, prime, unchecked((ulong)p.Generation));
            hash = Mix(hash, prime, p.Sequence);
            hash = Mix(hash, prime, p.DefinitionId);
            hash = Mix(hash, prime, p.Kind);
            hash = Mix(hash, prime, p.Px); hash = Mix(hash, prime, p.Py);
            hash = Mix(hash, prime, p.Vx); hash = Mix(hash, prime, p.Vy);
            hash = Mix(hash, prime, p.AccelerationX); hash = Mix(hash, prime, p.AccelerationY);
            hash = Mix(hash, prime, p.HomingStrength);
            hash = Mix(hash, prime, p.TraveledPixels);
            hash = Mix(hash, prime, p.LifetimeRemaining);
            hash = Mix(hash, prime, p.RemainingDamage);
            hash = Mix(hash, prime, p.HasDetonated);
            hash = Mix(hash, prime, p.IsImpacting);
            return hash;
        }

        private static ulong HashTileMutation(ulong hash, ulong prime, in SimTileMutation t)
        {
            hash = Mix(hash, prime, t.RoomHash);
            hash = Mix(hash, prime, unchecked((ulong)t.WorldRevision));
            hash = Mix(hash, prime, t.EventSequence);
            hash = Mix(hash, prime, unchecked((ulong)t.TileX));
            hash = Mix(hash, prime, unchecked((ulong)t.TileY));
            hash = Mix(hash, prime, unchecked((ulong)t.PhysicsType));
            hash = Mix(hash, prime, unchecked((ulong)t.HitPoints));
            return hash;
        }

        private static ulong HashDroppedItem(ulong hash, ulong prime, in SimDroppedItem d)
        {
            hash = Mix(hash, prime, d.EntityHash);
            hash = Mix(hash, prime, d.ItemIdHash);
            hash = Mix(hash, prime, d.IsWeapon);
            hash = Mix(hash, prime, unchecked((ulong)d.Quantity));
            hash = Mix(hash, prime, unchecked((ulong)d.Variant));
            hash = Mix(hash, prime, d.Condition);
            hash = Mix(hash, prime, d.Px); hash = Mix(hash, prime, d.Py);
            return hash;
        }

        private delegate ulong HashElement<T>(ulong hash, ulong prime, in T item);

        private static ulong HashArray<T>(ulong hash, ulong prime, T[] items, HashElement<T> hashOne)
        {
            if (items == null)
            {
                return Mix(hash, prime, 0u);
            }

            hash = Mix(hash, prime, (uint)items.Length);
            for (int i = 0; i < items.Length; i++)
            {
                hash = hashOne(hash, prime, items[i]);
            }
            return hash;
        }

        // ── FNV-1a mixing primitives ──────────────────────────────────────────

        private static ulong Mix(ulong hash, ulong prime, bool value)
        {
            unchecked { hash ^= value ? (byte)1 : (byte)0; hash *= prime; }
            return hash;
        }

        private static ulong Mix(ulong hash, ulong prime, byte value)
        {
            unchecked { hash ^= value; hash *= prime; }
            return hash;
        }

        private static ulong Mix(ulong hash, ulong prime, ushort value)
        {
            unchecked
            {
                hash ^= (byte)(value & 0xFF); hash *= prime;
                hash ^= (byte)(value >> 8); hash *= prime;
            }
            return hash;
        }

        private static ulong Mix(ulong hash, ulong prime, uint value)
        {
            unchecked
            {
                hash ^= (byte)(value & 0xFF); hash *= prime;
                hash ^= (byte)((value >> 8) & 0xFF); hash *= prime;
                hash ^= (byte)((value >> 16) & 0xFF); hash *= prime;
                hash ^= (byte)((value >> 24) & 0xFF); hash *= prime;
            }
            return hash;
        }

        private static ulong Mix(ulong hash, ulong prime, ulong value)
        {
            unchecked
            {
                for (int shift = 0; shift < 64; shift += 8)
                {
                    hash ^= (byte)((value >> shift) & 0xFF);
                    hash *= prime;
                }
            }
            return hash;
        }

        private static ulong Mix(ulong hash, ulong prime, float value)
        {
            // Hash the IEEE-754 bit pattern, not the value, so +0/-0 and NaN are stable and two
            // snapshots with bit-identical floats hash identically. The union reinterprets the float's
            // bytes as a uint without a conversion (which would collapse NaN payloads).
            var union = new FloatBits { Float = value };
            return Mix(hash, prime, union.Bits);
        }

        [System.Runtime.InteropServices.StructLayout(
            System.Runtime.InteropServices.LayoutKind.Explicit)]
        private struct FloatBits
        {
            [System.Runtime.InteropServices.FieldOffset(0)] public float Float;
            [System.Runtime.InteropServices.FieldOffset(0)] public uint Bits;
        }

        public override readonly string ToString()
        {
            int rooms = Rooms?.Length ?? 0;
            int tileEvents = TileMutations?.Length ?? 0;
            return $"SimSnapshot(v{ProtocolVersion} tick={TickIndex} seq={SnapshotSequence} " +
                   $"rev={WorldRevision} rooms={rooms} tileEvents={tileEvents} " +
                   $"hash=0x{ComputeStateHash():X16})";
        }
    }
}
