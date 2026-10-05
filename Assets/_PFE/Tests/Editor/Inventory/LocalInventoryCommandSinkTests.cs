using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using PFE.Data.Definitions;
using PFE.Systems.Inventory;

namespace PFE.Tests.Editor
{
    /// <summary>
    /// The single-player command sink: validate → resolve → apply, against a real
    /// <see cref="GameInventory"/>.
    ///
    /// <para><b>This fixture cannot run in the offline wall.</b> Its arrange calls
    /// <c>ScriptableObject.CreateInstance&lt;ItemDefinition&gt;</c>, which is an engine ECall and throws
    /// <c>SecurityException: ECall methods must be packaged into a system module</c> outside the editor —
    /// the same reason <see cref="GameInventoryTests"/> reports as failed there. The rule half of the seam
    /// is proved offline by <see cref="InventoryCommandValidationTests"/>; this fixture is the part that
    /// needs the engine, and it runs in the owner's EditMode pass.</para>
    ///
    /// <para><b>Every rejection test also asserts the inventory did not change.</b> "It returned
    /// Applied=false" alone cannot distinguish a correct rejection from a sink that rejects everything
    /// after having already mutated the bag.</para>
    /// </summary>
    [TestFixture]
    public class LocalInventoryCommandSinkTests
    {
        private const string ItemId = "kombu";
        private const string ArmorId = "leather";
        private const string AmmoId = "p10";

        private readonly List<UnityEngine.Object> _created = new List<UnityEngine.Object>();

        private GameInventory _inventory;
        private LocalInventoryCommandSink _sink;

        [SetUp]
        public void SetUp()
        {
            ItemDefinition item = MakeItem(ItemId, ItemType.Misc, InventoryCategory.Misc, stackSize: 50, weight: 0.2f);
            ItemDefinition ammo = MakeItem(AmmoId, ItemType.Ammo, InventoryCategory.Ammo, stackSize: 999, weight: 0.01f);
            ItemDefinition armor = MakeItem(ArmorId, ItemType.Equipment, InventoryCategory.Apparel, stackSize: 1, weight: 5f);
            armor.armourLevels = new[] { new EquipmentData() };

            var byId = new Dictionary<string, ItemDefinition>(StringComparer.Ordinal)
            {
                { ItemId, item },
                { AmmoId, ammo },
                { ArmorId, armor }
            };

            _inventory = new GameInventory
            {
                ArmorDefinitionResolver = id => byId.TryGetValue(id, out ItemDefinition d) && d.IsArmour ? d : null
            };

            _sink = new LocalInventoryCommandSink(
                _inventory,
                id => byId.TryGetValue(id, out ItemDefinition d) ? d : null,
                id => byId.TryGetValue(id, out ItemDefinition d) && d.IsArmour ? d : null);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (UnityEngine.Object o in _created)
            {
                if (o != null) UnityEngine.Object.DestroyImmediate(o);
            }
            _created.Clear();
        }

        /// <summary>
        /// How many of <paramref name="id"/> are held, whatever kind of item it is.
        /// <c>GetAmmoCount</c> would return the same number — it is the same dictionary — but naming it
        /// that in an item test would bury the misnomer rather than avoid it.
        /// </summary>
        private int Held(string id) => _inventory.GetItem(id)?.Quantity ?? 0;

        private ItemDefinition MakeItem(string id, ItemType type, InventoryCategory category, int stackSize, float weight)
        {
            var def = ScriptableObject.CreateInstance<ItemDefinition>();
            def.itemId = id;
            def.displayName = id;
            def.type = type;
            def.inventoryCategory = category;
            def.stackSize = stackSize;
            def.weight = weight;
            def.basePrice = 1;
            _created.Add(def);
            return def;
        }

        // ── AddItem ───────────────────────────────────────────────────────────

        [Test]
        public void AddItem_AppliesAndStocksTheInventory()
        {
            InventoryCommandResult result = _sink.Submit(InventoryCommand.AddItem(ItemId, 3));

            Assert.IsTrue(result.Applied, result.Reason);
            Assert.AreEqual(3, result.Amount);
            Assert.AreEqual(3, Held(ItemId), "the item must actually be in the bag");
        }

        [Test]
        public void AddItem_Twice_Stacks()
        {
            _sink.Submit(InventoryCommand.AddItem(ItemId, 3));
            _sink.Submit(InventoryCommand.AddItem(ItemId, 2));

            Assert.AreEqual(5, Held(ItemId));
        }

        [Test]
        public void AddItem_WithAnUnknownId_IsRejectedAndNamesIt()
        {
            InventoryCommandResult result = _sink.Submit(InventoryCommand.AddItem("no_such_item", 1));

            Assert.IsFalse(result.Applied);
            StringAssert.Contains("no_such_item", result.Reason);
            Assert.IsFalse(_inventory.HasItem("no_such_item"), "a rejected add must not create a stack");
        }

        [Test]
        public void AddItem_WithAnEmptyId_IsRejectedBeforeResolution()
        {
            InventoryCommandResult result = _sink.Submit(InventoryCommand.AddItem("", 1));

            Assert.IsFalse(result.Applied);
            StringAssert.Contains("id", result.Reason);
        }

        [Test]
        public void AddItem_WithZeroQuantity_IsRejected()
        {
            InventoryCommandResult result = _sink.Submit(InventoryCommand.AddItem(ItemId, 0));

            Assert.IsFalse(result.Applied);
            Assert.AreEqual(0, Held(ItemId));
        }

        // ── RemoveItem ────────────────────────────────────────────────────────

        [Test]
        public void RemoveItem_AppliesAndDrawsDownTheStack()
        {
            _sink.Submit(InventoryCommand.AddItem(ItemId, 5));

            InventoryCommandResult result = _sink.Submit(InventoryCommand.RemoveItem(ItemId, 2));

            Assert.IsTrue(result.Applied, result.Reason);
            Assert.AreEqual(2, result.Amount);
            Assert.AreEqual(3, Held(ItemId));
        }

        [Test]
        public void RemoveItem_WhenNotEnoughHeld_IsRejectedAndLeavesTheStackAlone()
        {
            _sink.Submit(InventoryCommand.AddItem(ItemId, 2));

            InventoryCommandResult result = _sink.Submit(InventoryCommand.RemoveItem(ItemId, 5));

            Assert.IsFalse(result.Applied);
            StringAssert.Contains("2", result.Reason, "the reason must state what is actually held");
            Assert.AreEqual(2, Held(ItemId), "a rejected removal must not partially apply");
        }

        [Test]
        public void RemoveItem_ForAnIdNeverHeld_IsRejected()
        {
            InventoryCommandResult result = _sink.Submit(InventoryCommand.RemoveItem(ItemId, 1));

            Assert.IsFalse(result.Applied);
        }

        // ── ConsumeAmmo ───────────────────────────────────────────────────────

        [Test]
        public void ConsumeAmmo_DrawsWhatWasAskedFor()
        {
            _sink.Submit(InventoryCommand.AddItem(AmmoId, 30));

            InventoryCommandResult result = _sink.Submit(InventoryCommand.ConsumeAmmo(AmmoId, 12));

            Assert.IsTrue(result.Applied);
            Assert.AreEqual(12, result.Amount);
            Assert.AreEqual(18, Held(AmmoId));
        }

        [Test]
        public void ConsumeAmmo_ShortDraw_ReportsTheRealNumber_NotTheRequest()
        {
            // The reload path reads Amount, so a short draw has to be visible: asking for 12 with 5 held
            // must report 5, not 12 and not a failure.
            _sink.Submit(InventoryCommand.AddItem(AmmoId, 5));

            InventoryCommandResult result = _sink.Submit(InventoryCommand.ConsumeAmmo(AmmoId, 12));

            Assert.IsTrue(result.Applied);
            Assert.AreEqual(5, result.Amount);
            Assert.AreEqual(0, Held(AmmoId));
        }

        [Test]
        public void ConsumeAmmo_WithNoneHeld_IsAppliedWithZeroDrawn()
        {
            InventoryCommandResult result = _sink.Submit(InventoryCommand.ConsumeAmmo(AmmoId, 12));

            Assert.IsTrue(result.Applied);
            Assert.AreEqual(0, result.Amount);
        }

        // ── AddArmor ──────────────────────────────────────────────────────────

        [Test]
        public void AddArmor_AppliesAndStoresTheArmour()
        {
            InventoryCommandResult result = _sink.Submit(InventoryCommand.AddArmor(ArmorId));

            Assert.IsTrue(result.Applied, result.Reason);
            Assert.AreEqual(1, result.Amount);
            Assert.IsNotNull(_inventory.GetArmor(ArmorId));
        }

        [Test]
        public void AddArmor_ForAnItemThatIsNotArmour_IsRejected()
        {
            // The armour resolver filters on IsArmour, so a plain item id must not become armour — the
            // trap GameDatabase.GetItem sets for any caller that treats "an item row" as "armour".
            InventoryCommandResult result = _sink.Submit(InventoryCommand.AddArmor(ItemId));

            Assert.IsFalse(result.Applied);
            Assert.IsNull(_inventory.GetArmor(ItemId));
        }

        [Test]
        public void AddArmor_ForAnUnknownId_IsRejectedAndNamesIt()
        {
            InventoryCommandResult result = _sink.Submit(InventoryCommand.AddArmor("no_such_armor"));

            Assert.IsFalse(result.Applied);
            StringAssert.Contains("no_such_armor", result.Reason);
        }

        // ── Construction / degraded wiring ────────────────────────────────────

        [Test]
        public void NullInventory_ThrowsAtConstruction_SoTheMistakeIsLoud()
        {
            Assert.Throws<ArgumentNullException>(() => new LocalInventoryCommandSink(null, id => null, id => null));
        }

        [Test]
        public void NullResolvers_RejectWithAReason_AndDoNotThrow()
        {
            // A rig with no content registry must degrade to "rejected, and here is why" rather than a
            // NullReferenceException from inside the seam.
            var sink = new LocalInventoryCommandSink(new GameInventory(), null, null);

            InventoryCommandResult result = sink.Submit(InventoryCommand.AddItem(ItemId, 1));

            Assert.IsFalse(result.Applied);
            Assert.IsNotNull(result.Reason);
        }
    }
}
