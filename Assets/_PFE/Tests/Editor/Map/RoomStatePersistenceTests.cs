using System.Collections.Generic;
using NUnit.Framework;
using PFE.Systems.Inventory;
using PFE.Systems.Map;
using PFE.Systems.Map.Serialization;
using UnityEngine;

namespace PFE.Tests.Map
{
    /// <summary>
    /// Pins the contract that lets a room survive its own view being destroyed: the record is the only
    /// owner of state, and a view may be thrown away and rebuilt from it at any time.
    /// </summary>
    /// <remarks>
    /// <para><b>What this fixture can and cannot reach.</b> Everything here is a record-level fact —
    /// pruning, the health a rebuild restores, a loot record, a snapshot round-trip and the room-local
    /// conversion. The <i>view</i> half (<c>WorldItemPickup.RestoreForRoom</c>,
    /// <c>RoomUnitSpawner.Spawn</c>, <c>RoomVisualController.ClearVisuals</c>) builds GameObjects and
    /// textures and cannot run outside the editor, so it is not covered here. That gap is named rather
    /// than papered over: these tests prove the arithmetic and the data, and a play-test still has to
    /// prove the wiring.</para>
    ///
    /// <para><b>Why these particular cases.</b> Each one is a decision whose wrong answer is silent. A
    /// corpse that is not pruned respawns with no log line; a restored health value taken from a field
    /// default turns a 300-HP boss into a 100-HP one; a loot record with a zero count either duplicates
    /// or vanishes depending on which guard falls; and a room-local conversion that forgets the room
    /// origin is exactly correct in every room at land (0,0), which is the room a quick test visits.</para>
    /// </remarks>
    [TestFixture]
    public class RoomStatePersistenceTests
    {
        // ── resetUnits ───────────────────────────────────────────────────────

        /// <summary>
        /// AS3 <c>units = units.filter(u =&gt; u.sost &lt; 4)</c> — corpses leave, survivors keep their
        /// order.
        /// </summary>
        [Test]
        public void PruneDeadUnits_DropsCorpsesAndKeepsTheLiving()
        {
            var living1 = new UnitInstance { unitId = "raider", entityId = "e1" };
            var corpse = new UnitInstance { unitId = "zombie", entityId = "e2", isDead = true };
            var living2 = new UnitInstance { unitId = "npc", entityId = "e3" };
            var units = new List<UnitInstance> { living1, corpse, living2, null };

            int removed = RoomStateRules.PruneDeadUnits(units);

            Assert.AreEqual(2, removed, "the corpse and the null slot are both dropped");
            Assert.AreEqual(2, units.Count);
            Assert.AreSame(living1, units[0], "survivor order is preserved — the list is filtered, not re-sorted");
            Assert.AreSame(living2, units[1]);
        }

        [Test]
        public void PruneDeadUnits_NullListIsZeroAndDoesNotThrow()
        {
            Assert.AreEqual(0, RoomStateRules.PruneDeadUnits(null));
        }

        /// <summary>
        /// The room-level entry point must be the same rule, not a second copy of it.
        /// </summary>
        [Test]
        public void RoomInstance_PruneDeadUnits_DelegatesToTheRule()
        {
            var room = new RoomInstance { id = "r" };
            room.units.Add(new UnitInstance { unitId = "raider" });
            room.units.Add(new UnitInstance { unitId = "zombie", isDead = true });

            int removed = room.PruneDeadUnits();

            Assert.AreEqual(1, removed);
            Assert.AreEqual(1, room.units.Count);
            Assert.AreEqual("raider", room.units[0].unitId);
        }

        // ── health a rebuild restores ────────────────────────────────────────

        /// <summary>
        /// The case the latch exists for: a unit that has never been hurt. Its record still carries the
        /// field default (100), and applying that to a 300-HP boss would be a silent two-thirds health
        /// cut on every room re-entry.
        /// </summary>
        [Test]
        public void TryResolveRestoredHealth_RefusesARecordThatNeverHadRuntimeHealth()
        {
            bool applied = RoomStateRules.TryResolveRestoredHealth(
                hasRuntimeHealth: false, recordedHealth: 100f, builtMaxHealth: 300f, out float health);

            Assert.IsFalse(applied, "no runtime health on the record means 'leave the constructed value alone'");
            Assert.AreEqual(0f, health);
        }

        [Test]
        public void TryResolveRestoredHealth_AppliesTheRecordedValue()
        {
            bool applied = RoomStateRules.TryResolveRestoredHealth(
                hasRuntimeHealth: true, recordedHealth: 42f, builtMaxHealth: 120f, out float health);

            Assert.IsTrue(applied);
            Assert.AreEqual(42f, health, 0.0001f);
        }

        /// <summary>
        /// A record can exceed the rebuilt ceiling — the difficulty setting scales non-player HP at
        /// construction. Clamping keeps the unit full rather than above a maximum it cannot reach.
        /// </summary>
        [Test]
        public void TryResolveRestoredHealth_ClampsToTheRebuiltCeiling()
        {
            bool applied = RoomStateRules.TryResolveRestoredHealth(
                hasRuntimeHealth: true, recordedHealth: 180f, builtMaxHealth: 120f, out float health);

            Assert.IsTrue(applied);
            Assert.AreEqual(120f, health, 0.0001f);
        }

        /// <summary>
        /// Zero means "died". A dead record is pruned before a rebuild, so reaching here with a zero means
        /// the two halves disagree — refuse rather than write a 1-HP zombie with no death event behind it.
        /// </summary>
        [Test]
        public void TryResolveRestoredHealth_RefusesADeadRecord()
        {
            Assert.IsFalse(RoomStateRules.TryResolveRestoredHealth(true, 0f, 120f, out _));
            Assert.IsFalse(RoomStateRules.TryResolveRestoredHealth(true, -5f, 120f, out _));
        }

        [Test]
        public void TryResolveRestoredHealth_RefusesAViewWithNoCeiling()
        {
            Assert.IsFalse(RoomStateRules.TryResolveRestoredHealth(true, 40f, 0f, out _));
        }

        // ── floor loot records ───────────────────────────────────────────────

        [Test]
        public void IsRestorableLoot_RejectsIncompleteRecords()
        {
            Assert.IsFalse(RoomStateRules.IsRestorableLoot(null), "no record");
            Assert.IsFalse(RoomStateRules.IsRestorableLoot(new DroppedItemInstance()), "no id");
            Assert.IsFalse(RoomStateRules.IsRestorableLoot(new DroppedItemInstance { itemId = "medkit", quantity = 0 }),
                           "a zero stack is a view that can never be taken");
            Assert.IsTrue(RoomStateRules.IsRestorableLoot(new DroppedItemInstance { itemId = "medkit", quantity = 1 }));
        }

        [Test]
        public void RoomInstance_AddAndRemoveDroppedItem_TracksTheList()
        {
            var room = new RoomInstance { id = "r" };
            var item = new DroppedItemInstance { itemId = "medkit", quantity = 2, position = new Vector2(40f, 60f) };

            room.AddDroppedItem(item);

            Assert.AreEqual(1, room.droppedItems.Count);
            Assert.AreSame(item, room.droppedItems[0]);

            Assert.IsTrue(room.RemoveDroppedItem(item), "taking an item forgets its record");
            Assert.AreEqual(0, room.droppedItems.Count);
            Assert.IsFalse(room.RemoveDroppedItem(item), "removing twice is not an error, it is a no-op");
        }

        [Test]
        public void RoomInstance_AddDroppedItem_IgnoresNull()
        {
            var room = new RoomInstance { id = "r" };
            room.AddDroppedItem(null);
            Assert.AreEqual(0, room.droppedItems.Count);
        }

        // ── snapshot round-trip ──────────────────────────────────────────────

        /// <summary>
        /// A save must reproduce the unit, not just its position: the placement's attributes carry
        /// <c>npc</c>/<c>turn</c>/<c>qid</c>, its <c>&lt;item&gt;</c> children are its death drop, and its
        /// <c>&lt;scr&gt;</c> children are its death and alarm scripts. A loaded save used to lose all
        /// three, and the unit came back as a generic enemy at the right coordinates.
        /// </summary>
        [Test]
        public void Snapshot_RoundTrip_KeepsDeadUnitsAndTheirPlacement()
        {
            RoomInstance source = CreateRoom("room_a", new Vector3Int(0, 0, 0));

            var unit = new UnitInstance
            {
                unitId = "zombie0",
                entityId = "e7",
                unitType = "enemy",
                uid = "zomb_uid",
                code = "zomb_code",
                controllerId = "UnitZombie",
                position = new Vector2(120f, 340f),
                isDead = true,
                currentHealth = 0f,
                maxHealth = 55f,
                hasRuntimeHealth = true,
                facingDirection = -1,
                digger = 1,
                ambushArmed = true,
                fraction = PFE.Data.Definitions.FactionType.Zombie,
            };
            unit.attributes.Add(new MapObjectAttributeData { key = "qid", value = "killZomb" });
            // `UnitInstance.items` is null, not empty, when nothing was authored (RoomPopulator relies on
            // that to keep the field out of the Inspector), so the drop has to be assigned rather than added.
            unit.items = new List<MapObjectItemData> { new MapObjectItemData { id = "alc1" } };
            unit.scripts.Add(new MapObjectScriptData { eventName = "die" });
            source.units.Add(unit);

            RoomStateSnapshot snapshot = RoomStateSnapshot.CreateFromRoom(source);

            RoomInstance restored = CreateRoom("room_b", new Vector3Int(2, 1, 0));
            snapshot.RestoreToRoom(restored);

            Assert.AreEqual(1, restored.units.Count, "the dead record survives the file; the prune is what drops it");
            UnitInstance round = restored.units[0];

            Assert.AreEqual("zombie0", round.unitId);
            Assert.AreEqual("e7", round.entityId);
            Assert.AreEqual("zomb_uid", round.uid);
            Assert.AreEqual("zomb_code", round.code);
            Assert.AreEqual("UnitZombie", round.controllerId);
            Assert.IsTrue(round.IsDead);
            Assert.IsTrue(round.hasRuntimeHealth);
            Assert.AreEqual(0f, round.currentHealth, 0.0001f);
            Assert.AreEqual(55f, round.maxHealth, 0.0001f);
            Assert.AreEqual(-1, round.facingDirection);
            Assert.AreEqual(1, round.digger);
            Assert.IsTrue(round.ambushArmed, "a risen ambusher must not be able to re-bury itself after a load");
            Assert.AreEqual(PFE.Data.Definitions.FactionType.Zombie, round.fraction);

            Assert.AreEqual(1, round.items.Count, "the unit's death drop");
            Assert.AreEqual("alc1", round.items[0].id);
            Assert.AreEqual(1, round.scripts.Count, "the unit's death script");
            Assert.AreEqual("die", round.scripts[0].eventName);
            Assert.AreEqual("killZomb", round.GetAttribute("qid"), "the placement's authored keys");
        }

        /// <summary>
        /// A dropped stack is in no room but this one, so a save that omits it deletes the player's loot.
        /// </summary>
        [Test]
        public void Snapshot_RoundTrip_KeepsFloorLoot()
        {
            RoomInstance source = CreateRoom("room_a", new Vector3Int(0, 0, 0));
            source.AddDroppedItem(new DroppedItemInstance
            {
                itemId = "medkit",
                isWeapon = false,
                quantity = 3,
                variant = 0,
                condition = 1f,
                autoCollect = true,
                position = new Vector2(83.5f, 121.25f),
            });
            source.AddDroppedItem(new DroppedItemInstance
            {
                itemId = "rifle",
                isWeapon = true,
                quantity = 1,
                variant = 1,
                condition = 0.25f,
                autoCollect = false,
                position = new Vector2(20f, 40f),
            });

            RoomStateSnapshot snapshot = RoomStateSnapshot.CreateFromRoom(source);

            Assert.AreEqual(2, snapshot.droppedItems.Length);

            RoomInstance restored = CreateRoom("room_b", new Vector3Int(0, 0, 0));
            snapshot.RestoreToRoom(restored);

            Assert.AreEqual(2, restored.droppedItems.Count);

            DroppedItemInstance item = restored.droppedItems[0];
            Assert.AreEqual("medkit", item.itemId);
            Assert.IsFalse(item.isWeapon);
            Assert.AreEqual(3, item.quantity);
            Assert.IsTrue(item.autoCollect);
            Assert.AreEqual(83.5f, item.position.x, 0.0001f, "room-local pixels, exactly");
            Assert.AreEqual(121.25f, item.position.y, 0.0001f);

            DroppedItemInstance weapon = restored.droppedItems[1];
            Assert.AreEqual("rifle", weapon.itemId);
            Assert.IsTrue(weapon.isWeapon, "which table the id belongs to is what makes it resolvable");
            Assert.AreEqual(1, weapon.variant);
            Assert.AreEqual(0.25f, weapon.condition, 0.0001f);
            Assert.IsFalse(weapon.autoCollect, "a player's own drop is not walk-over collected");
        }

        /// <summary>
        /// A save written before floor loot existed deserialises with no array at all; restoring must
        /// produce an empty list rather than throw or leave a stale one.
        /// </summary>
        [Test]
        public void Snapshot_Restore_WithNoLootArray_ClearsTheRoomsLoot()
        {
            RoomInstance target = CreateRoom("room_b", new Vector3Int(0, 0, 0));
            target.AddDroppedItem(new DroppedItemInstance { itemId = "stale", quantity = 1 });

            var snapshot = new RoomStateSnapshot
            {
                roomId = "room_b",
                width = 4,
                height = 3,
                droppedItems = null,
                units = System.Array.Empty<UnitStateSnapshot>(),
                objects = System.Array.Empty<ObjectStateSnapshot>(),
            };

            snapshot.RestoreToRoom(target);

            Assert.AreEqual(0, target.droppedItems.Count);
        }

        // ── room-local conversion ────────────────────────────────────────────

        /// <summary>
        /// <c>transform.position</c> is world; the record's <c>position</c> is room-local. The round-trip
        /// has to be the identity, or loot drifts a little every time a room is rebuilt.
        /// </summary>
        [Test]
        public void RoomLocalConversion_RoundTrips()
        {
            RoomInstance room = CreateRoom("r", new Vector3Int(3, 2, 0));
            var world = new Vector3(47.5f, -23.25f, 0f);

            Vector2 local = WorldItemPickup.ToRoomLocal(room, world);
            Vector3 back = WorldItemPickup.FromRoomLocal(room, local);

            Assert.AreEqual(world.x, back.x, 0.0001f);
            Assert.AreEqual(world.y, back.y, 0.0001f);
        }

        /// <summary>
        /// The origin subtraction, stated as a difference rather than as a re-derivation of the formula:
        /// the same world point is one whole room-width further into a room that sits one column right.
        /// Forgetting the origin is invisible in a room at land (0,0) and wrong in every other room.
        /// </summary>
        [Test]
        public void RoomLocalConversion_SubtractsTheRoomOrigin()
        {
            RoomInstance atOrigin = CreateRoom("a", new Vector3Int(0, 0, 0));
            RoomInstance oneRight = CreateRoom("b", new Vector3Int(1, 0, 0));
            var world = new Vector3(25f, 5f, 0f);

            Vector2 localAtOrigin = WorldItemPickup.ToRoomLocal(atOrigin, world);
            Vector2 localOneRight = WorldItemPickup.ToRoomLocal(oneRight, world);

            float roomWidthPixels = WorldConstants.ROOM_SIZE_PIXELS.x;
            Assert.AreEqual(roomWidthPixels, localAtOrigin.x - localOneRight.x, 0.01f,
                "the second room's local X is one room-width smaller for the same world point");
            Assert.AreEqual(0f, localAtOrigin.y - localOneRight.y, 0.01f,
                "the two rooms share a land row, so local Y is unchanged");
        }

        // ── helpers ──────────────────────────────────────────────────────────

        static RoomInstance CreateRoom(string id, Vector3Int landPosition)
        {
            var room = new RoomInstance
            {
                id = id,
                templateId = id + "_template",
                landPosition = landPosition,
                width = 4,
                height = 3,
            };
            room.InitializeTiles();
            return room;
        }
    }
}
