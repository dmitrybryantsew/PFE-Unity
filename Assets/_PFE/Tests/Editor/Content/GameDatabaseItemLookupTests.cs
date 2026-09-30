using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using PFE.Data;
using PFE.Data.Definitions;
using PFE.ModAPI;

namespace PFE.Tests.Editor.Content
{
    /// <summary>
    /// Pins <see cref="GameDatabase.GetItem"/> — the lookup <c>GameInventory.AddArmor</c> needs — and
    /// the decision behind it: <b>armour registers under <see cref="ContentType.Item"/>, the same type
    /// as every other item.</b>
    ///
    /// <para><b>Why armour is a separate Resources folder but not a separate ContentType.</b> The 35
    /// <c>&lt;armor&gt;</c> elements are a different element type from AllData's 500 <c>&lt;item&gt;</c>
    /// ones, and <c>DataImportVerificationTests</c> pins <c>Resources/Items</c> at exactly 451 assets —
    /// so the importer writes to <c>Resources/Armor</c> and <c>BuiltInContentSource</c> loads it
    /// separately. But <see cref="ItemDefinition"/> declares <c>ContentType.Item</c> for itself, so both
    /// folders land in one registry namespace. That is safe here (no id appears in both sets) and
    /// matches AS3, where armour and items share the item list.</para>
    ///
    /// <para><b>Registration goes through <see cref="ContentRegistry.Register"/>, not the legacy
    /// <c>Register*</c> methods.</b> Those write only to <c>GameDatabase</c>'s private dictionaries —
    /// <c>RegisterRoomTemplate</c> never touches the registry — which is why <c>GetUnit</c>/<c>GetWeapon</c>
    /// carry a legacy fallback and <c>GetItem</c> does not. The registry path is what
    /// <c>BuiltInContentSource</c> uses in production, so it is the path worth testing.</para>
    /// </summary>
    [TestFixture]
    public class GameDatabaseItemLookupTests
    {
        private readonly List<Object> _created = new List<Object>();
        private GameDatabase _database;

        [SetUp]
        public void SetUp()
        {
            _database = new GameDatabase();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (Object created in _created)
                if (created != null) Object.DestroyImmediate(created);

            _created.Clear();
        }

        private ItemDefinition Definition(string id)
        {
            var definition = ScriptableObject.CreateInstance<ItemDefinition>();
            definition.itemId = id;
            _created.Add(definition);
            return definition;
        }

        /// <summary>Registers exactly the way <c>BuiltInContentSource</c> does.</summary>
        private void Register(ItemDefinition definition)
            => _database.Registry.Register(ModManifest.CreateBaseGame(), definition);

        [Test]
        public void GetItem_FindsABareIdThroughTheBaseGameAlias()
        {
            ItemDefinition item = Definition("pot1");
            Register(item);

            Assert.AreSame(item, _database.GetItem("pot1"),
                "A bare id must resolve: the registry stores it as pfe.base.pot1 and keeps the bare name " +
                "as an alias, which is what every legacy caller passes.");
        }

        [Test]
        public void GetItem_FindsTheNamespacedId()
        {
            ItemDefinition item = Definition("pot1");
            Register(item);

            Assert.AreSame(item, _database.GetItem("pfe.base.pot1"));
        }

        [Test]
        public void GetItem_FindsAnItemAndAnArmourUnderTheSameContentType()
        {
            ItemDefinition item = Definition("pot1");

            ItemDefinition armour = Definition("kombu");
            armour.armourLevels = new[] { new EquipmentData { armor = 4, reliability = 0.6f } };
            armour.equipment = armour.armourLevels[0];

            Register(item);
            Register(armour);

            Assert.AreSame(item, _database.GetItem("pot1"), "A plain item.");
            Assert.AreSame(armour, _database.GetItem("kombu"), "And an armour, through the same lookup.");
            Assert.AreEqual(4, _database.GetItem("kombu").armourLevels[0].armor,
                "The per-level table survives the round trip through the registry.");
        }

        [Test]
        public void GetItem_ReturnsNullForAnUnknownId()
        {
            Register(Definition("pot1"));

            Assert.IsNull(_database.GetItem("kombu"));
        }

        [Test]
        public void GetItem_ReturnsNullForNullOrEmpty()
        {
            Assert.IsNull(_database.GetItem(null));
            Assert.IsNull(_database.GetItem(string.Empty));
        }

        [Test]
        public void GetAllItemIDs_ListsTheBareIds()
        {
            Register(Definition("pot1"));
            Register(Definition("kombu"));

            CollectionAssert.AreEquivalent(new[] { "pot1", "kombu" }, _database.GetAllItemIDs());
        }

        [Test]
        public void AnIdUsedTwice_KeepsTheFirstAndWarns()
        {
            // The hazard of one shared ContentType. Today there is no collision — 35 armour ids against
            // 500 item ids, disjoint — but that is a property of the *data*, not of the code, so the
            // failure mode is pinned here: first registration wins and the second only warns.
            ItemDefinition first = Definition("clash");
            ItemDefinition second = Definition("clash");

            Register(first);

            LogAssert.Expect(LogType.Warning,
                "[ContentRegistry] Item/pfe.base.clash: duplicate from pfe.base, kept pfe.base");

            Register(second);

            Assert.AreSame(first, _database.GetItem("clash"),
                "Additive policy keeps the first; an armour that shadowed an item id would lose silently.");
        }
    }
}
