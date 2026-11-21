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
    /// <see cref="DroppedItems"/>, <see cref="LootedObjects"/>), plus the despawn set
    /// (<see cref="RemovedEntityHashes"/>) that names the entities present in the client's baseline but
    /// gone now. Spawn is implicit: an entity in the list that the client has never seen is a spawn.
    /// This is the smallest shape that lets a client reconcile against an ack'd baseline (§9
    /// <c>BaselineHash</c>) without sending unchanged rooms. Reference-type arrays are used because a
    /// delta is a variable-length payload, not a fixed-size value — but every <i>element</i> is a pure
    /// value type, which is what the 1.3 lint pins.</para>
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

        /// <summary>
        /// The containers in this room that have been <b>opened</b> — a full-replace set, and the
        /// authoritative half of <c>ObjectInstance.runtimeState.lootState</c>.
        ///
        /// <para><b>Why this is a delta field and not a reliable event.</b> §10 says a reliable event
        /// is for state that cannot be rebuilt from a lost packet — a destroyed wall. A container's
        /// loot flag is 8 bytes per opened chest, and it is <i>already</i> a value the receiver can
        /// full-replace, exactly like <see cref="DroppedItems"/>. Repeating the set in every snapshot
        /// for a room present on the wire therefore costs a few dozen bytes and needs no second
        /// sequence space, no second ack counter and no journal retention: a lost snapshot is repaired
        /// by the next one, and a late joiner's baseline carries the set for free. A journal would buy
        /// nothing here and cost the machinery that exists for geometry.</para>
        ///
        /// <para><b>Why it must be replicated at all.</b> Container <i>contents</i> are derived from
        /// <c>(roomKey, spawnIndex)</c>, so two peers already roll the same inventory. What they could
        /// not agree on is whether it had been taken: with no replicated flag each peer rolled its own
        /// copy of the same chest and the world gained two. The flag is the only non-derived part.</para>
        ///
        /// <para><b>Absent room means unchanged; present room with an empty set means "nothing opened
        /// here".</b> Same contract as <see cref="DroppedItems"/>, and it is why the host must project
        /// from live room state rather than from a set it only ever adds to.</para>
        /// </summary>
        [SimReconcile(SimReconcileRule.Snap)]
        public SimObjectState[] LootedObjects;

        /// <summary>
        /// Every door box in this room and its current open state — the authoritative half of
        /// <c>ObjectInstance.runtimeState.isOpen</c>, the same full-replace contract as
        /// <see cref="LootedObjects"/> but carrying <b>both</b> states: a door a player closed must
        /// read closed on a peer that generated it open, so "only the positives" (the opened set)
        /// cannot express it. Small by construction: a room carries a handful of doors.
        ///
        /// <para><b>What this does and does not carry.</b> The door's TILES travel in the reliable
        /// tile journal (<see cref="SimSnapshot.TileMutations"/>): a toggle rewrites the covered
        /// cells' physics and <c>DoorPropPresenter.SetOpen</c> notifies, so geometry replicates
        /// already. What the journal cannot convey is the presenter's own view state — the sprite
        /// frame and the solid collider (<c>RefreshSolidCollider</c>) — which derive from
        /// <c>runtimeState.isOpen</c> and are never rebuilt from tiles. Without this set a peer
        /// whose door state diverged keeps the wrong picture and the wrong projectile surface even
        /// though the floor plan agrees.</para>
        /// </summary>
        [SimReconcile(SimReconcileRule.Snap)]
        public SimDoorState[] DoorStates;

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
            WriteArray(writer, LootedObjects);
            WriteArray(writer, DoorStates);
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
                LootedObjects = ReadArray(reader, SimObjectState.ReadFrom),
                DoorStates = ReadArray(reader, SimDoorState.ReadFrom),
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
                case SimObjectState objectState: objectState.WriteTo(writer); break;
                case SimDoorState doorState: doorState.WriteTo(writer); break;
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
                && ArrayEquals(RemovedEntityHashes, other.RemovedEntityHashes)
                && ArrayEquals(LootedObjects, other.LootedObjects)
                && ArrayEquals(DoorStates, other.DoorStates);
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
                hash = (hash * 397) ^ ArrayHash(LootedObjects);
                hash = (hash * 397) ^ ArrayHash(DoorStates);
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
            int looted = LootedObjects?.Length ?? 0;
            int doors = DoorStates?.Length ?? 0;
            return $"SimRoomDelta(room={RoomHash:X16} units={units} motors={motors} " +
                   $"projectiles={projectiles} drops={drops} removed={removed} looted={looted} doors={doors})";
        }

        /// <summary>
        /// Element-wise array equality where <c>null</c> and empty are the <b>same state</b>.
        ///
        /// <para><b>Why null must not be a third value.</b> A producer that has nothing to report may
        /// leave an array null, while the codec reads a zero count back as an empty array — so a
        /// strict null check makes a plain <c>serialize → deserialize</c> report a divergence that is
        /// not there. The other two readers of these arrays already agree that the two are equal:
        /// <see cref="ArrayHash"/> answers 0 for both, and <c>SimSnapshot.HashArray</c> mixes the same
        /// zero length for both. Equality disagreeing with the hash was a latent inconsistency that
        /// stayed invisible only because every existing fixture populated all five arrays.</para>
        /// </summary>
        private static bool ArrayEquals<T>(T[] a, T[] b) where T : IEquatable<T>
        {
            int aLength = a?.Length ?? 0;
            int bLength = b?.Length ?? 0;
            if (aLength != bLength) return false;
            for (int i = 0; i < aLength; i++)
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

    /// <summary>
    /// One container in a room that has been opened — the replicated half of
    /// <c>ObjectInstance.runtimeState.lootState</c>.
    ///
    /// <para><b>Why only the index and the flag, and nothing else.</b> Everything else about a
    /// container is already identical on both machines: the prop exists because
    /// <c>RoomPopulator</c> walked the same template, and its contents because
    /// <c>WorldRng.ForObject</c> derives them from <c>(roomKey, spawnIndex)</c>. The one thing no
    /// derivation can predict is whether somebody already took the loot, and that is
    /// <see cref="LootState"/>. Sending the inventory instead would be sending the answer to a question
    /// both sides can already compute.</para>
    ///
    /// <para><b><see cref="ObjectIndex"/> is <c>spawnIndex</c>, not the list position.</b>
    /// <c>RoomInstance.RemoveObject</c> closes the gap in <c>objects</c>, so a list position shifts when
    /// any earlier object is removed — a chest would inherit its neighbour's loot flag. <c>spawnIndex</c>
    /// is assigned once at population time and is serialized with the save, so it survives both
    /// removal and a reload. An index below zero means the object never went through
    /// <c>RoomInstance.AddObject</c> and therefore cannot be addressed at all.</para>
    ///
    /// <para><b>Pure C#.</b> Two ints, no <c>UnityEngine</c>, no strings — the same shape constraint
    /// <see cref="SimDroppedItem"/> and <see cref="SimTileMutation"/> are held to.</para>
    /// </summary>
    [Serializable]
    public struct SimObjectState : IEquatable<SimObjectState>
    {
        /// <summary>The container's stable <c>ObjectInstance.spawnIndex</c> within its room.</summary>
        [SimReconcile(SimReconcileRule.Key)]
        public int ObjectIndex;

        /// <summary>The container's <c>runtimeState.lootState</c> — AS3 <c>setAct("loot", n)</c>, where
        /// 1 is "opened" and 2 is "opened and an important item came out" (<c>Interact.as:1972</c>,
        /// <c>:1988-1990</c>, <c>:2024</c>). Only ever positive: a member of this set <i>is</i> an
        /// opened container, so 0 would be a contradiction rather than "not opened".</summary>
        [SimReconcile(SimReconcileRule.Snap)]
        public int LootState;

        // ── Wire serialization (v7) ──────────────────────────────────────────

        /// <summary>Appends this record to <paramref name="writer"/> in declaration order.</summary>
        public readonly void WriteTo(System.IO.BinaryWriter writer)
        {
            writer.Write(ObjectIndex);
            writer.Write(LootState);
        }

        /// <summary>Reads one record from <paramref name="reader"/> in the same order
        /// <see cref="WriteTo"/> wrote it.</summary>
        public static SimObjectState ReadFrom(System.IO.BinaryReader reader)
        {
            return new SimObjectState
            {
                ObjectIndex = reader.ReadInt32(),
                LootState = reader.ReadInt32(),
            };
        }

        public readonly bool Equals(SimObjectState other)
        {
            return ObjectIndex == other.ObjectIndex && LootState == other.LootState;
        }

        public override readonly bool Equals(object obj) => obj is SimObjectState other && Equals(other);

        public override readonly int GetHashCode()
        {
            unchecked { return (ObjectIndex * 397) ^ LootState; }
        }

        public override readonly string ToString()
        {
            return $"SimObjectState(index={ObjectIndex} lootState={LootState})";
        }
    }

    /// <summary>
    /// One door box in a room and its current open state — the replicated half of
    /// <c>ObjectInstance.runtimeState.isOpen</c>.
    ///
    /// <para><b>Every door is a member, not only the toggled ones.</b> The opened-container set
    /// (<see cref="SimObjectState"/>) is one-directional — "opened" never becomes un-true. A door a
    /// player CLOSED must read closed on a peer that generated it open, so the set carries the
    /// state of every door box in the room and the receiver full-replaces. Rooms carry a handful
    /// of doors; the cost of the whole list is a few bytes per snapshot.</para>
    ///
    /// <para><b>Geometry is deliberately not here.</b> The covered tiles travel in the reliable
    /// journal (<see cref="SimSnapshot.TileMutations"/>) — <c>DoorPropPresenter.SetOpen</c> already
    /// notifies after rewriting them. This record exists for the half the journal cannot reach:
    /// the presenter's sprite and its solid projectile collider, both derived from
    /// <c>runtimeState.isOpen</c>.</para>
    ///
    /// <para><b>Pure C#.</b> An int and a bool — the same shape constraint
    /// <see cref="SimObjectState"/> is held to.</para>
    /// </summary>
    [Serializable]
    public struct SimDoorState : IEquatable<SimDoorState>
    {
        /// <summary>The door's stable <c>ObjectInstance.spawnIndex</c> within its room — the same
        /// addressing <see cref="SimObjectState"/> uses, for the same gap-closing reason.</summary>
        [SimReconcile(SimReconcileRule.Key)]
        public int ObjectIndex;

        /// <summary><c>ObjectInstance.runtimeState.isOpen</c> — true when the door currently
        /// stands open on the host.</summary>
        [SimReconcile(SimReconcileRule.Snap)]
        public bool IsOpen;

        // ── Wire serialization (v9) ──────────────────────────────────────────

        /// <summary>Appends this record to <paramref name="writer"/> in declaration order.</summary>
        public readonly void WriteTo(System.IO.BinaryWriter writer)
        {
            writer.Write(ObjectIndex);
            writer.Write(IsOpen);
        }

        /// <summary>Reads one record from <paramref name="reader"/> in the same order
        /// <see cref="WriteTo"/> wrote it.</summary>
        public static SimDoorState ReadFrom(System.IO.BinaryReader reader)
        {
            return new SimDoorState
            {
                ObjectIndex = reader.ReadInt32(),
                IsOpen = reader.ReadBoolean(),
            };
        }

        public readonly bool Equals(SimDoorState other)
        {
            return ObjectIndex == other.ObjectIndex && IsOpen == other.IsOpen;
        }

        public override readonly bool Equals(object obj) => obj is SimDoorState other && Equals(other);

        public override readonly int GetHashCode()
        {
            unchecked { return (ObjectIndex * 397) ^ IsOpen.GetHashCode(); }
        }

        public override readonly string ToString()
        {
            return $"SimDoorState(index={ObjectIndex} open={IsOpen})";
        }
    }
}
