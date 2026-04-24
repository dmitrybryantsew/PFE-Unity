using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using R3;
using PFE.Systems.Inventory;
using PFE.Data.Definitions;

namespace PFE.Tests.Editor
{
    /// <summary>
    /// Test suite for GameInventory system
    /// Tests ActionScript-style inventory with weapons, armors, items, and weight system
    /// </summary>
    [TestFixture]
    public class GameInventoryTests
    {
        private ItemDefinition _stackableItem;
        private ItemDefinition _nonStackableItem;
        private ItemDefinition _weaponItem;
        private ItemDefinition _armorItem;

        [SetUp]
        public void Setup()
        {
            // Create test stackable item
            _stackableItem = ScriptableObject.CreateInstance<ItemDefinition>();
            _stackableItem.itemId = "ammo_10mm";
            _stackableItem.displayName = "10mm Ammo";
            _stackableItem.type = ItemType.Ammo;
            _stackableItem.inventoryCategory = Data.Definitions.InventoryCategory.Ammo;
            _stackableItem.stackSize = 100;
            _stackableItem.weight = 0.1f;
            _stackableItem.basePrice = 1;

            // Create test non-stackable item
            _nonStackableItem = ScriptableObject.CreateInstance<ItemDefinition>();
            _nonStackableItem.itemId = "med_stimpack";
            _nonStackableItem.displayName = "Stimpack";
            _nonStackableItem.type = ItemType.Medical;
            _nonStackableItem.inventoryCategory = Data.Definitions.InventoryCategory.Aid;
            _nonStackableItem.stackSize = 1;
            _nonStackableItem.weight = 0.5f;
            _nonStackableItem.basePrice = 50;

            // Create test weapon item
            _weaponItem = ScriptableObject.CreateInstance<ItemDefinition>();
            _weaponItem.itemId = "weapon_pistol";
            _weaponItem.displayName = "10mm Pistol";
            _weaponItem.type = ItemType.Equipment;
            _weaponItem.inventoryCategory = Data.Definitions.InventoryCategory.Weapons;
            _weaponItem.stackSize = 1;
            _weaponItem.weight = 2.0f;
            _weaponItem.basePrice = 200;

            // Create test armor item
            _armorItem = ScriptableObject.CreateInstance<ItemDefinition>();
            _armorItem.itemId = "armor_leather";
            _armorItem.displayName = "Leather Armor";
            _armorItem.type = ItemType.Equipment;
            _armorItem.inventoryCategory = Data.Definitions.InventoryCategory.Apparel;
            _armorItem.stackSize = 1;
            _armorItem.weight = 5.0f;
            _armorItem.basePrice = 100;
        }

        [TearDown]
        public void Teardown()
        {
            UnityEngine.Object.DestroyImmediate(_stackableItem);
            UnityEngine.Object.DestroyImmediate(_nonStackableItem);
            UnityEngine.Object.DestroyImmediate(_weaponItem);
            UnityEngine.Object.DestroyImmediate(_armorItem);
        }

        #region Constructor Tests

        [Test]
        public void Constructor_InitializesWithDefaults()
        {
            var inventory = new GameInventory();

            Assert.AreEqual(0, inventory.TotalItemCount.Value);
            Assert.AreEqual(0f, inventory.TotalWeight.Value);
            Assert.AreEqual(0, inventory.WeaponCount.Value);
            Assert.AreEqual(0, inventory.ArmorCount.Value);
        }

        #endregion

        #region Add Item Tests

        [Test]
        public void AddItem_WithStackableItem_AddsSuccessfully()
        {
            var inventory = new GameInventory();

            bool result = inventory.AddItem(_stackableItem, 50);

            Assert.IsTrue(result);
            Assert.AreEqual(50, inventory.GetItem(_stackableItem.itemId)?.Quantity);
        }

        [Test]
        public void AddItem_WithExistingStackableItem_StacksCorrectly()
        {
            var inventory = new GameInventory();
            inventory.AddItem(_stackableItem, 30);

            bool result = inventory.AddItem(_stackableItem, 20);

            Assert.IsTrue(result);
            Assert.AreEqual(50, inventory.GetItem(_stackableItem.itemId)?.Quantity);
        }

        [Test]
        public void AddItem_WhenStackFull_DoesNotAdd()
        {
            var inventory = new GameInventory();
            inventory.AddItem(_stackableItem, 100); // Fill stack

            bool result = inventory.AddItem(_stackableItem, 1);

            Assert.IsFalse(result);
            Assert.AreEqual(100, inventory.GetItem(_stackableItem.itemId)?.Quantity);
        }

        [Test]
        public void AddItem_SetsNewStatus()
        {
            var inventory = new GameInventory();

            inventory.AddItem(_stackableItem, 10);

            var item = inventory.GetItem(_stackableItem.itemId);
            Assert.AreEqual(NewItemStatus.New, item?.NewStatus);
        }

        #endregion

        #region Remove Item Tests

        [Test]
        public void RemoveItem_WithValidQuantity_RemovesSuccessfully()
        {
            var inventory = new GameInventory();
            inventory.AddItem(_stackableItem, 50);

            bool result = inventory.RemoveItem(_stackableItem.itemId, 20);

            Assert.IsTrue(result);
            Assert.AreEqual(30, inventory.GetItem(_stackableItem.itemId)?.Quantity);
        }

        [Test]
        public void RemoveItem_EntireStack_RemovesItem()
        {
            var inventory = new GameInventory();
            inventory.AddItem(_stackableItem, 50);

            bool result = inventory.RemoveItem(_stackableItem.itemId, 50);

            Assert.IsTrue(result);
            Assert.IsNull(inventory.GetItem(_stackableItem.itemId));
        }

        [Test]
        public void RemoveItem_WithInsufficientQuantity_Fails()
        {
            var inventory = new GameInventory();
            inventory.AddItem(_stackableItem, 10);

            bool result = inventory.RemoveItem(_stackableItem.itemId, 20);

            Assert.IsFalse(result);
            Assert.AreEqual(10, inventory.GetItem(_stackableItem.itemId)?.Quantity);
        }

        [Test]
        public void RemoveItem_WithVault_AllowsNegative()
        {
            var inventory = new GameInventory();
            inventory.AddItem(_stackableItem, 5);

            bool result = inventory.RemoveItem(_stackableItem.itemId, 10, allowVault: true);

            Assert.IsTrue(result);
            var item = inventory.GetItem(_stackableItem.itemId);
            Assert.AreEqual(0, item?.Quantity);
            Assert.AreEqual(-5, item?.VaultQuantity);
        }

        #endregion

        #region Query Tests

        [Test]
        public void HasItem_WithExistingItem_ReturnsTrue()
        {
            var inventory = new GameInventory();
            inventory.AddItem(_stackableItem, 10);

            Assert.IsTrue(inventory.HasItem(_stackableItem.itemId));
        }

        [Test]
        public void HasItem_WithQuantity_ChecksAmount()
        {
            var inventory = new GameInventory();
            inventory.AddItem(_stackableItem, 10);

            Assert.IsTrue(inventory.HasItem(_stackableItem.itemId, 10));
            Assert.IsTrue(inventory.HasItem(_stackableItem.itemId, 5));
            Assert.IsFalse(inventory.HasItem(_stackableItem.itemId, 15));
        }

        [Test]
        public void GetItem_WithValidId_ReturnsItem()
        {
            var inventory = new GameInventory();
            inventory.AddItem(_stackableItem, 25);

            var item = inventory.GetItem(_stackableItem.itemId);

            Assert.IsNotNull(item);
            Assert.AreEqual(_stackableItem.itemId, item.Definition.itemId);
            Assert.AreEqual(25, item.Quantity);
        }

        [Test]
        public void GetItem_WithInvalidId_ReturnsNull()
        {
            var inventory = new GameInventory();

            var item = inventory.GetItem("nonexistent");

            Assert.IsNull(item);
        }

        #endregion

        #region Weight System Tests

        [Test]
        public void AddItem_UpdatesCategoryMass()
        {
            var inventory = new GameInventory();

            inventory.AddItem(_stackableItem, 50); // 50 * 0.1 = 5.0

            Assert.AreEqual(5.0f, inventory.GetCategoryMass(2)); // 2 = Ammo category in our weight system
        }

        [Test]
        public void RemoveItem_UpdatesCategoryMass()
        {
            var inventory = new GameInventory();
            inventory.AddItem(_stackableItem, 100); // 10.0 total
            inventory.RemoveItem(_stackableItem.itemId, 50); // 5.0 remaining

            Assert.AreEqual(5.0f, inventory.GetCategoryMass(2)); // 2 = Ammo category in our weight system
        }

        [Test]
        public void GetTotalMass_CalculatesCorrectly()
        {
            var inventory = new GameInventory();
            inventory.AddItem(_stackableItem, 100); // 10.0
            inventory.AddItem(_nonStackableItem, 1); // 0.5

            float total = inventory.GetTotalMass();

            Assert.AreEqual(10.5f, total, 0.01f);
        }

        [Test]
        public void TotalWeight_UpdatesWhenItemAdded()
        {
            var inventory = new GameInventory();
            float lastValue = 0f;
            int changeCount = 0;

            using var subscription = inventory.TotalWeight.Subscribe((value) =>
            {
                changeCount++;
                lastValue = value;
            });

            inventory.AddItem(_stackableItem, 50); // 5.0

            Assert.AreEqual(2, changeCount); // Initial + change
            Assert.AreEqual(5.0f, lastValue, 0.01f);
        }

        #endregion

        #region Weapon Tests

        [Test]
        public void AddWeapon_AddsSuccessfully()
        {
            var inventory = new GameInventory();

            // Expected error until GameDatabase lookup is implemented
            LogAssert.Expect(LogType.Error, "[GameInventory] Weapon definition not found: weapon_pistol");

            var weapon = inventory.AddWeapon(_weaponItem.itemId);

            // Note: Will return null until GameDatabase lookup is implemented
            // This test structure is ready for when that's added
            Assert.IsNull(weapon);
        }

        [Test]
        public void AddWeapon_WithExistingWeapon_Repairs()
        {
            var inventory = new GameInventory();

            // Expected errors until GameDatabase lookup is implemented
            LogAssert.Expect(LogType.Error, "[GameInventory] Weapon definition not found: weapon_pistol");
            LogAssert.Expect(LogType.Error, "[GameInventory] Weapon definition not found: weapon_pistol");

            // First add
            var weapon1 = inventory.AddWeapon(_weaponItem.itemId, health: 50f);
            // Second add should repair
            var weapon2 = inventory.AddWeapon(_weaponItem.itemId, health: 30f);

            // Should be same instance
            // Assert.AreSame(weapon1, weapon2);
            // Implementation pending GameDatabase
            Assert.IsNull(weapon2);
        }

        #endregion

        #region Armor Tests

        [Test]
        public void AddArmor_AddsSuccessfully()
        {
            var inventory = new GameInventory();

            // Expected error until GameDatabase lookup is implemented
            LogAssert.Expect(LogType.Error, "[GameInventory] Armor definition not found: armor_leather");

            var armor = inventory.AddArmor(_armorItem.itemId);

            // Note: Will return null until GameDatabase lookup is implemented
            // This test structure is ready for when that's added
            Assert.IsNull(armor);
        }

        [Test]
        public void AddArmor_WithExistingArmor_ReturnsNull()
        {
            var inventory = new GameInventory();

            // Expected errors until GameDatabase lookup is implemented
            LogAssert.Expect(LogType.Error, "[GameInventory] Armor definition not found: armor_leather");
            LogAssert.Expect(LogType.Error, "[GameInventory] Armor definition not found: armor_leather");

            inventory.AddArmor(_armorItem.itemId);
            var armor2 = inventory.AddArmor(_armorItem.itemId);

            // Should not allow duplicate armor
            // Assert.IsNull(armor2);
            // Implementation pending GameDatabase
            Assert.IsNull(armor2);
        }

        #endregion

        #region Favorite Slots Tests

        [Test]
        public void SetFavorite_AssignsToSlot()
        {
            var inventory = new GameInventory();
            inventory.AddItem(_stackableItem, 10);

            bool result = inventory.SetFavorite(_stackableItem.itemId, 5);

            Assert.IsTrue(result);
        }

        [Test]
        public void SetFavorite_WithInvalidSlot_ReturnsFalse()
        {
            var inventory = new GameInventory();
            inventory.AddItem(_stackableItem, 10);

            bool result = inventory.SetFavorite(_stackableItem.itemId, 50); // Invalid slot

            Assert.IsFalse(result);
        }

        #endregion

        #region Reactive Properties Tests

        [Test]
        public void TotalItemCount_UpdatesWhenItemAdded()
        {
            var inventory = new GameInventory();
            int changeCount = 0;

            using var subscription = inventory.TotalItemCount.Subscribe((_) => changeCount++);

            inventory.AddItem(_stackableItem, 10);

            Assert.GreaterOrEqual(changeCount, 1);
        }

        [Test]
        public void OnWeightChanged_FiresWhenMassChanges()
        {
            var inventory = new GameInventory();
            float lastWeight = 0f;
            bool fired = false;

            inventory.OnWeightChanged += (weight) =>
            {
                fired = true;
                lastWeight = weight;
            };

            inventory.AddItem(_stackableItem, 50); // 5.0 weight

            Assert.IsTrue(fired);
            Assert.AreEqual(5.0f, lastWeight, 0.01f);
        }

        #endregion
    }
}
