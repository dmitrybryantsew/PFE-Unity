using System;

namespace PFE.Sim.State
{
    /// <summary>
    /// The per-room slice of a <c>SimSnapshot</c> — every replicated entity that lives in one room,
    /// plus the spawn/despawn bookkeeping that lets a client apply the delta without a full baseline.
    ///
    /// <para><b>F-01/F-02, Phase 1.3 of the multiplayer readiness plan.</b> §9 sketches
    /// <c>SimSnapshot { … SimRoomDelta[] Rooms }</c>; this is the room element. It answers the readiness
    /// doc's open question D3 in the room-scoped direction: state is grouped <b>per room</b>, keyed by
    /// <see cref="RoomHash"/>, because that is the granularity interest management (F-04/F-05) filters
    /// on and the granularity the save baseline (<c>RoomStateSnapshot</c>) already persists. A global
    /// flat list would force every snapshot to carry every room and leave the interest filter nothing
    /// to remove.</para>
    ///
    /// <para><b>A delta, not a container of containers.</b> The room carries the full current record for
    /// each live entity it holds (<see cref="Units"/>, <see cref="Motors"/>, <see cref="Projectiles"/>,
    /// <see cref="DroppedItems"/>), plus the despawn set (<see cref="RemovedEntityHashes"/>) that names
    /// the entities present in the client's baseline but gone now. Spawn is implicit: an entity in the
    /// list that the client has never seen is a spawn. This is the smallest shape that lets a client
    /// reconcile against an ack'd baseline (§9 <c>BaselineHash</c>) without sending unchanged rooms.
    /// Reference-type arrays are used because a delta is a variable-length payload, not a fixed-size
    /// value — but every <i>element</i> is a pure value type, which is what the 1.3 lint pins.</para>
    ///
    /// <para><b>What is deliberately NOT here.</b> Static room content — the tile grid, doors, objects,
    /// spawn points, the placement attributes/items/scripts on each unit — is the save baseline's job
    /// (<c>RoomStateSnapshot</c>) and changes only through reliable ordered events with a
    /// <c>worldRevision</c> (§10 — "room tile mutations / destruction", "spawn / despawn / room
    /// transition"), never through this per-tick delta. Since v4 those events are
    /// <see cref="SimTileMutation"/> records on <see cref="SimSnapshot.TileMutations"/>, not fields of
    /// this delta: §10 says a reliable event is never a delta. The room's own identity strings
    /// (<c>RoomInstance.id</c>, <c>templateId</c>) collapse to <see cref="RoomHash"/> on the wire; the
    /// readable form stays in a debug-side table (§9).</para>
    ///
    /// <para><b>Authority.</b> Host-authoritative: the host builds one delta per active room per
    /// snapshot tick and clients apply it under the staleness guards of §10. The nested
    /// <see cref="SimDroppedItem"/> is the replicated half of <c>DroppedItemInstance</c> — position,
    /// stack, condition, identity — with the content id interned.</para>
    /// </summary>
    [Serializable]
    public struct SimRoomDelta : IEquatable<SimRoomDelta>
    {
        /// <summary>The room this delta describes, as a hash of <c>RoomInstance.id</c> — the same
        /// FNV-1a 64 scheme <c>EntityId</c> uses, so a room and its entities speak one id language.</summary>
        [SimReconcile(SimReconcileRule.Key)]
        public ulong RoomHash;

        /// <summary>Bumped on every room-level lifetime change (rebuild, re-enter after a full reset),
        /// so a delayed delta from a previous room lifetime is rejected (F-06).</summary>
        [SimReconcile(SimReconcileRule.ReliableLifecycleEvent)]
        public int LifecycleSequence;

        /// <summary>Every live unit in the room, full record. A unit the client has not seen is a
        /// spawn; one absent here but present in the baseline is named in
        /// <see cref="RemovedEntityHashes"/>.</summary>
        [SimReconcile(SimReconcileRule.Snap)]
        public SimUnitState[] Units;

        /// <summary>The motor record for each unit that has one, keyed by the same
        /// <see cref="SimMotorState.EntityHash"/>. Separate from <see cref="Units"/> because the motor
        /// is client-predicted for the owning player and host-authoritative for the rest — one field
        /// list cannot carry two reconciliation rules (§10).</summary>
        [SimReconcile(SimReconcileRule.Snap)]
        public SimMotorState[] Motors;

        /// <summary>Every in-flight projectile in the room.</summary>
        [SimReconcile(SimReconcileRule.Snap)]
        public SimProjectileState[] Projectiles;

        /// <summary>Loot on the room's floor (<c>RoomInstance.droppedItems</c>), the replicated half of
        /// each <c>DroppedItemInstance</c>.</summary>
        [SimReconcile(SimReconcileRule.Snap)]
        public SimDroppedItem[] DroppedItems;

        /// <summary>Entities present in the client's baseline for this room but gone now — died and
        /// pruned, picked up, despawned. Carried as hashes so the client drops them without a full
        /// record. This is the despawn half of the spawn/despawn reliable-event pair (§10).</summary>
        [SimReconcile(SimReconcileRule.ReliableOrderedEvent)]
        public ulong[] RemovedEntityHashes;

        // ── Wire serialization (Phase 1.4) ────────────────────────────────────

        /// <summary>Appends this delta to <paramref name="writer"/>: the two scalars, then each list
        /// as a count prefix followed by its elements in order.</summary>
        public readonly void WriteTo(System.IO.BinaryWriter writer)
        {
            writer.Write(RoomHash);
            writer.Write(LifecycleSequence);
            WriteArray(writer, Units);
            WriteArray(writer, Motors);
            WriteArray(writer, Projectiles);
            WriteArray(writer, DroppedItems);
            WriteArray(writer, RemovedEntityHashes);
        }

        /// <summary>Reads one delta from <paramref name="reader"/> in the same order
        /// <see cref="WriteTo"/> wrote it.</summary>
        public static SimRoomDelta ReadFrom(System.IO.BinaryReader reader)
        {
            return new SimRoomDelta
            {
                RoomHash = reader.ReadUInt64(),
                LifecycleSequence = reader.ReadInt32(),
                Units = ReadArray(reader, SimUnitState.ReadFrom),
                Motors = ReadArray(reader, SimMotorState.ReadFrom),
                Projectiles = ReadArray(reader, SimProjectileState.ReadFrom),
                DroppedItems = ReadArray(reader, SimDroppedItem.ReadFrom),
                RemovedEntityHashes = ReadArray(reader, r => r.ReadUInt64()),
            };
        }

        private static void WriteArray<T>(System.IO.BinaryWriter writer, T[] items)
        {
            writer.Write(items?.Length ?? 0);
            if (items == null) return;
            for (int i = 0; i < items.Length; i++)
            {
                WriteElement(writer, items[i]);
            }
        }

        private static void WriteElement<T>(System.IO.BinaryWriter writer, T item)
        {
            // Each element type writes itself; the ulong arm covers RemovedEntityHashes.
            switch (item)
            {
                case SimUnitState unit: unit.WriteTo(writer); break;
                case SimMotorState motor: motor.WriteTo(writer); break;
                case SimProjectileState projectile: projectile.WriteTo(writer); break;
                case SimDroppedItem drop: drop.WriteTo(writer); break;
                case ulong hash: writer.Write(hash); break;
                default: throw new System.InvalidOperationException(
                    "SimRoomDelta cannot serialize element of type " + typeof(T).Name);
            }
        }

        private static T[] ReadArray<T>(System.IO.BinaryReader reader, System.Func<System.IO.BinaryReader, T> readOne)
        {
            int count = reader.ReadInt32();
            var items = new T[count];
            for (int i = 0; i < count; i++)
            {
                items[i] = readOne(reader);
            }
            return items;
        }

        public readonly bool Equals(SimRoomDelta other)
        {
            return RoomHash == other.RoomHash
                && LifecycleSequence == other.LifecycleSequence
                && ArrayEquals(Units, other.Units)
                && ArrayEquals(Motors, other.Motors)
                && ArrayEquals(Projectiles, other.Projectiles)
                && ArrayEquals(DroppedItems, other.DroppedItems)
                && ArrayEquals(RemovedEntityHashes, other.RemovedEntityHashes);
        }

        public override readonly bool Equals(object obj) => obj is SimRoomDelta other && Equals(other);

        public override readonly int GetHashCode()
        {
            unchecked
            {
                int hash = RoomHash.GetHashCode();
                hash = (hash * 397) ^ LifecycleSequence;
                hash = (hash * 397) ^ ArrayHash(Units);
                hash = (hash * 397) ^ ArrayHash(Motors);
                hash = (hash * 397) ^ ArrayHash(Projectiles);
                hash = (hash * 397) ^ ArrayHash(DroppedItems);
                hash = (hash * 397) ^ ArrayHash(RemovedEntityHashes);
                return hash;
            }
        }

        public override readonly string ToString()
        {
            int units = Units?.Length ?? 0;
            int motors = Motors?.Length ?? 0;
            int projectiles = Projectiles?.Length ?? 0;
            int drops = DroppedItems?.Length ?? 0;
            int removed = RemovedEntityHashes?.Length ?? 0;
            return $"SimRoomDelta(room={RoomHash:X16} units={units} motors={motors} " +
                   $"projectiles={projectiles} drops={drops} removed={removed})";
        }

        private static bool ArrayEquals<T>(T[] a, T[] b) where T : IEquatable<T>
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (!a[i].Equals(b[i])) return false;
            }
            return true;
        }

        private static int ArrayHash<T>(T[] items) where T : IEquatable<T>
        {
            if (items == null) return 0;
            unchecked
            {
                int hash = items.Length;
                for (int i = 0; i < items.Length; i++)
                {
                    hash = (hash * 397) ^ items[i].GetHashCode();
                }
                return hash;
            }
        }
    }

    /// <summary>
    /// The replicated half of one <c>DroppedItemInstance</c> — a stack of loot on a room's floor.
    /// Pure C#; position is room-local pixels, matching <c>DroppedItemInstance.position</c>.
    ///
    /// <para><b>Identity (§10.2 gap 2, closed at v4).</b> A drop now carries a host-assigned
    /// <see cref="EntityHash"/>. Before v4 its identity was <c>(content id, position)</c>, so two
    /// identical stacks dropped on the same pixel were indistinguishable — an idempotency hole for
    /// pickups and despawns. The host mints it once when the record is registered with the room and
    /// persists it in the save, so a restored drop keeps the id a client already keyed on.
    /// <see cref="ItemIdHash"/> remains the content-versioned identity of
    /// <c>DroppedItemInstance.itemId</c> (FNV-1a 64), with <see cref="IsWeapon"/> saying which table
    /// it resolves against — the same disjoint-tables split the oracle keeps
    /// (<c>AllData.d.weapon</c> vs <c>AllData.d.item</c>).</para>
    /// </summary>
    [Serializable]
    public struct SimDroppedItem : IEquatable<SimDroppedItem>
    {
        /// <summary>Host-assigned identity (<c>EntityId.Hash</c>). A key, never reconciled; zero is
        /// invalid and a receiver must reject it rather than fall back to content+position.</summary>
        [SimReconcile(SimReconcileRule.Key)]
        public ulong EntityHash;

        /// <summary>The item or weapon id, interned (FNV-1a 64 of <c>DroppedItemInstance.itemId</c>).</summary>
        [SimReconcile(SimReconcileRule.ContentConstant)]
        public ulong ItemIdHash;
        /// <summary>True when <see cref="ItemIdHash"/> names a weapon rather than an item.</summary>
        [SimReconcile(SimReconcileRule.Snap)]
        public bool IsWeapon;
        /// <summary>Stack size (<c>DroppedItemInstance.quantity</c>).</summary>
        [SimReconcile(SimReconcileRule.Snap)]
        public int Quantity;
        /// <summary>AS3 <c>Item.variant</c> — the <c>^N</c> tier, 0 for a plain weapon.</summary>
        [SimReconcile(SimReconcileRule.Snap)]
        public int Variant;
        /// <summary>AS3 <c>sost * multHP</c> — condition, 1 = pristine.</summary>
        [SimReconcile(SimReconcileRule.Snap)]
        public float Condition;
        /// <summary>Room-local pixel X (<c>DroppedItemInstance.position.x</c>).</summary>
        [SimReconcile(SimReconcileRule.Snap)]
        public float Px;
        /// <summary>Room-local pixel Y (<c>DroppedItemInstance.position.y</c>).</summary>
        [SimReconcile(SimReconcileRule.Snap)]
        public float Py;

        // ── Wire serialization (Phase 1.4) ────────────────────────────────────

        /// <summary>Appends this record to <paramref name="writer"/> in declaration order.</summary>
        public readonly void WriteTo(System.IO.BinaryWriter writer)
        {
            writer.Write(EntityHash);
            writer.Write(ItemIdHash);
            writer.Write(IsWeapon);
            writer.Write(Quantity);
            writer.Write(Variant);
            writer.Write(Condition);
            writer.Write(Px); writer.Write(Py);
        }

        /// <summary>Reads one record from <paramref name="reader"/> in the same order
        /// <see cref="WriteTo"/> wrote it.</summary>
        public static SimDroppedItem ReadFrom(System.IO.BinaryReader reader)
        {
            return new SimDroppedItem
            {
                EntityHash = reader.ReadUInt64(),
                ItemIdHash = reader.ReadUInt64(),
                IsWeapon = reader.ReadBoolean(),
                Quantity = reader.ReadInt32(),
                Variant = reader.ReadInt32(),
                Condition = reader.ReadSingle(),
                Px = reader.ReadSingle(), Py = reader.ReadSingle(),
            };
        }

        public readonly bool Equals(SimDroppedItem other)
        {
            return EntityHash == other.EntityHash
                && ItemIdHash == other.ItemIdHash
                && IsWeapon == other.IsWeapon
                && Quantity == other.Quantity
                && Variant == other.Variant
                && Condition.Equals(other.Condition)
                && Px.Equals(other.Px) && Py.Equals(other.Py);
        }

        public override readonly bool Equals(object obj) => obj is SimDroppedItem other && Equals(other);

        public override readonly int GetHashCode()
        {
            unchecked
            {
                int hash = EntityHash.GetHashCode();
                hash = (hash * 397) ^ ItemIdHash.GetHashCode();
                hash = (hash * 397) ^ IsWeapon.GetHashCode();
                hash = (hash * 397) ^ Quantity;
                hash = (hash * 397) ^ Variant;
                hash = (hash * 397) ^ Condition.GetHashCode();
                hash = (hash * 397) ^ Px.GetHashCode();
                hash = (hash * 397) ^ Py.GetHashCode();
                return hash;
            }
        }

        public override readonly string ToString()
        {
            return $"SimDroppedItem(id={EntityHash:X16} item={ItemIdHash:X16} x{Quantity} " +
                   $"weapon={IsWeapon} pos=({Px:F1},{Py:F1}))";
        }
    }
}
