using NUnit.Framework;
using UnityEngine;
using R3;
using PFE.Systems.Inventory;
using PFE.Data.Definitions;
using System;

namespace PFE.Tests.Editor
{
    /// <summary>
    /// Test suite for Inventory system
    /// Tests type-safe item lookup, capacity management, and reactive properties
    /// </summary>
    [TestFixture]
    public class InventoryTests
    {
        private ItemDefinition _stackableItem;
        private ItemDefinition _nonStackableItem;

        [SetUp]
        public void Setup()
        {
            // Create test items
            _stackableItem = ScriptableObject.CreateInstance<ItemDefinition>();
            _stackableItem.itemId = "ammo_stimpack";
            _stackableItem.displayName = "Stimpack";
            _stackableItem.type = ItemType.Medical;
            _stackableItem.inventoryCategory = InventoryCategory.Aid;
            _stackableItem.stackSize = 10;
            _stackableItem.weight = 0.5f;
            _stackableItem.basePrice = 50;

            _nonStackableItem = ScriptableObject.CreateInstance<ItemDefinition>();
            _nonStackableItem.itemId = "weapon_pistol";
            _nonStackableItem.displayName = "10mm Pistol";
            _nonStackableItem.type = ItemType.Equipment;
            _nonStackableItem.inventoryCategory = InventoryCategory.Weapons;
            _nonStackableItem.stackSize = 1;
            _nonStackableItem.weight = 2.0f;
            _nonStackableItem.basePrice = 200;
        }

        [TearDown]
        public void Teardown()
        {
            UnityEngine.Object.DestroyImmediate(_stackableItem);
            UnityEngine.Object.DestroyImmediate(_nonStackableItem);
        }

        #region Constructor Tests

        [Test]
        public void Constructor_WithValidParameters_CreatesInventory()
        {
            var inventory = new Inventory(maxSlots: 20, maxWeight: 100f);

            Assert.AreEqual(20, inventory.MaxSlots);
            Assert.AreEqual(100f, inventory.MaxWeight);
            Assert.AreEqual(0, inventory.ItemCount.Value);
            Assert.AreEqual(0f, inventory.TotalWeight.Value);
            Assert.AreEqual(0, inventory.UsedSlots.Value);
        }

        [Test]
        public void Constructor_WithInvalidSlots_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => new Inventory(maxSlots: 0, maxWeight: 100f));
            Assert.Throws<ArgumentException>(() => new Inventory(maxSlots: -1, maxWeight: 100f));
        }

        #endregion

        #region Add Item Tests

        [Test]
        public void AddItem_WithStackableItem_AddsSuccessfully()
        {
            var inventory = new Inventory(maxSlots: 10, maxWeight: 100f);

            int added = inventory.AddItem(_stackableItem, 5);

            Assert.AreEqual(5, added);
            Assert.AreEqual(5, inventory.ItemCount.Value);
            Assert.AreEqual(1, inventory.UsedSlots.Value);
            Assert.AreEqual(2.5f, inventory.TotalWeight.Value);
        }

        [Test]
        public void AddItem_WithNonStackableItem_AddsSuccessfully()
        {
            var inventory = new Inventory(maxSlots: 10, maxWeight: 100f);

            int added = inventory.AddItem(_nonStackableItem, 1);

            Assert.AreEqual(1, added);
            Assert.AreEqual(1, inventory.ItemCount.Value);
            Assert.AreEqual(1, inventory.UsedSlots.Value);
            Assert.AreEqual(2.0f, inventory.TotalWeight.Value);
        }

        [Test]
        public void AddItem_WithExistingStackableItem_StacksCorrectly()
        {
            var inventory = new Inventory(maxSlots: 10, maxWeight: 100f);
            inventory.AddItem(_stackableItem, 5);

            int added = inventory.AddItem(_stackableItem, 3);

            Assert.AreEqual(3, added);
            Assert.AreEqual(8, inventory.ItemCount.Value);
            Assert.AreEqual(1, inventory.UsedSlots.Value);
        }

        [Test]
        public void AddItem_WhenStackFull_DoesNotAdd()
        {
            var inventory = new Inventory(maxSlots: 10, maxWeight: 100f);
            inventory.AddItem(_stackableItem, 10); // Fill stack

            int added = inventory.AddItem(_stackableItem, 1);

            Assert.AreEqual(0, added);
            Assert.AreEqual(10, inventory.ItemCount.Value);
        }

        #endregion

        #region Remove Item Tests

        [Test]
        public void RemoveItem_WithValidQuantity_RemovesSuccessfully()
        {
            var inventory = new Inventory(maxSlots: 10, maxWeight: 100f);
            inventory.AddItem(_stackableItem, 10);

            int removed = inventory.RemoveItem(_stackableItem.itemId, 3);

            Assert.AreEqual(3, removed);
            Assert.AreEqual(7, inventory.GetQuantity(_stackableItem.itemId));
        }

        [Test]
        public void RemoveItem_EntireStack_RemovesItemCompletely()
        {
            var inventory = new Inventory(maxSlots: 10, maxWeight: 100f);
            inventory.AddItem(_stackableItem, 5);

            int removed = inventory.RemoveItem(_stackableItem.itemId, 5);

            Assert.AreEqual(5, removed);
            Assert.IsFalse(inventory.HasItem(_stackableItem.itemId));
            Assert.AreEqual(0, inventory.UsedSlots.Value);
        }

        #endregion

        #region Query Tests

        [Test]
        public void GetItem_WithValidId_ReturnsItem()
        {
            var inventory = new Inventory(maxSlots: 10, maxWeight: 100f);
            inventory.AddItem(_stackableItem, 5);

            var item = inventory.GetItem(_stackableItem.itemId);

            Assert.IsNotNull(item);
            Assert.AreEqual(_stackableItem.itemId, item.ItemDefinition.itemId);
            Assert.AreEqual(5, item.Quantity);
        }

        [Test]
        public void HasItem_WithExistingItem_ReturnsTrue()
        {
            var inventory = new Inventory(maxSlots: 10, maxWeight: 100f);
            inventory.AddItem(_stackableItem, 5);

            Assert.IsTrue(inventory.HasItem(_stackableItem.itemId));
        }

        [Test]
        public void HasItem_WithQuantity_ChecksQuantity()
        {
            var inventory = new Inventory(maxSlots: 10, maxWeight: 100f);
            inventory.AddItem(_stackableItem, 5);

            Assert.IsTrue(inventory.HasItem(_stackableItem.itemId, 5));
            Assert.IsTrue(inventory.HasItem(_stackableItem.itemId, 3));
            Assert.IsFalse(inventory.HasItem(_stackableItem.itemId, 6));
        }

        #endregion

        #region Reactive Properties Tests

        [Test]
        public void ItemCount_UpdatesWhenItemAdded()
        {
            var inventory = new Inventory(maxSlots: 10, maxWeight: 100f);
            int changeCount = 0;
            int lastValue = 0;

            using var subscription = inventory.ItemCount.Subscribe((value) =>
            {
                changeCount++;
                lastValue = value;
            });

            inventory.AddItem(_stackableItem, 5);

            Assert.AreEqual(2, changeCount); // Initial value + change
            Assert.AreEqual(5, lastValue);
        }

        [Test]
        public void ItemAdded_PublishesEvent()
        {
            var inventory = new Inventory(maxSlots: 10, maxWeight: 100f);
            ItemChangedEventArgs lastEvent = null;

            using var subscription = inventory.ItemAdded.Subscribe((e) =>
            {
                lastEvent = e;
            });

            inventory.AddItem(_stackableItem, 5);

            Assert.IsNotNull(lastEvent);
            Assert.AreEqual(_stackableItem.itemId, lastEvent.ItemDefinition.itemId);
            Assert.AreEqual(5, lastEvent.Quantity);
            Assert.AreEqual(5, lastEvent.CurrentQuantity);
        }

        #endregion

        #region Capacity Tests

        [Test]
        public void CanAddItem_WithCapacity_ReturnsTrue()
        {
            var inventory = new Inventory(maxSlots: 10, maxWeight: 100f);

            Assert.IsTrue(inventory.CanAddItem(_stackableItem, 5));
        }

        [Test]
        public void AvailableSlots_ReturnsCorrectValue()
        {
            var inventory = new Inventory(maxSlots: 5, maxWeight: 100f);
            inventory.AddItem(_stackableItem, 1);
            inventory.AddItem(_nonStackableItem, 1);

            Assert.AreEqual(3, inventory.AvailableSlots);
        }

        #endregion
    }
}
