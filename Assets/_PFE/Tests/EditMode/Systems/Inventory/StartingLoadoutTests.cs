using System.IO;
using NUnit.Framework;
using PFE.Systems.Inventory;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.EditMode.Systems.Inventory
{
    /// <summary>
    /// Pins <see cref="StartingLoadout"/> — the port of <c>Invent.addMin()</c>
    /// (<c>pfe/scripts/fe/unit/Invent.as:1600-1615</c>).
    ///
    /// <para><b>Why a fixture for a data table.</b> The grant itself runs once, from a
    /// <c>MonoBehaviour</c>, at a moment nothing can schedule a test for, so a wrong transcription
    /// would see the console exactly once and then be invisible forever. The table is the whole rule,
    /// so the table is what gets pinned — counts, ids, stack sizes and favourite slots, against the
    /// Oracle.</para>
    ///
    /// <para><b>The last two tests are lints, and they are the ones that earn the fixture.</b> A
    /// transcription can be right and still grant nothing, if the row it names has no asset behind
    /// it. One checks that every grantable row has a file on disk; the other that the four weapons
    /// resolve through <c>ContentType.Weapon</c> and <i>not</i> through a row in the item table, so
    /// the weapon grant keeps working the way the Oracle's disjoint <c>d.weapon</c>/<c>d.item</c>
    /// tables work rather than being "fixed" by making weapons into items.</para>
    /// </summary>
    [TestFixture]
    public sealed class StartingLoadoutTests
    {
        private const string InventMin = "Invent.addMin(), Invent.as:1600-1615";

        // ── The transcription ────────────────────────────────────────────────

        [Test]
        public void Weapons_AreTheFourAddMinGrantsInTheOraclesOrder()
        {
            Assert.That(StartingLoadout.WeaponIds, Is.EqualTo(new[] { "r32", "rech", "mont", "bat" }),
                "addMin grants r32, rech, mont and bat, and nothing else.");
        }

        [Test]
        public void Armour_IsTheStableBoySuitAddMinAndAddBeginBothGrant()
        {
            Assert.That(StartingLoadout.ArmorIds, Is.EqualTo(new[] { "pip" }),
                "addMin wears 'pip'; addBegin (Invent.as:1618-1622) grants the same one.");
        }

        [Test]
        public void Items_CarryTheOraclesStackSizesInItsOrder()
        {
            int count = StartingLoadout.Items.Length;
            Assert.That(count, Is.EqualTo(4), "addMin sets kol on exactly four item rows.");

            Assert.That(StartingLoadout.Items[0].ItemId, Is.EqualTo("p32"));
            Assert.That(StartingLoadout.Items[0].Quantity, Is.EqualTo(16), "items[\"p32\"].kol = 16");

            Assert.That(StartingLoadout.Items[1].ItemId, Is.EqualTo("pot0"));
            Assert.That(StartingLoadout.Items[1].Quantity, Is.EqualTo(1), "items[\"pot0\"].kol = 1");

            Assert.That(StartingLoadout.Items[2].ItemId, Is.EqualTo("pot1"));
            Assert.That(StartingLoadout.Items[2].Quantity, Is.EqualTo(1), "items[\"pot1\"].kol = 1");

            Assert.That(StartingLoadout.Items[3].ItemId, Is.EqualTo("screwdriver"));
            Assert.That(StartingLoadout.Items[3].Quantity, Is.EqualTo(1), "items[\"screwdriver\"].kol = 1");
        }

        [Test]
        public void EveryGrantedStackIsPositive()
        {
            // The command seam rejects a non-positive quantity, so a transposed 0 would be refused
            // and logged rather than silently granted — but the loadout should state what it means.
            for (int i = 0; i < StartingLoadout.Items.Length; i++)
            {
                Assert.That(StartingLoadout.Items[i].Quantity, Is.GreaterThan(0),
                    $"row {i} ('{StartingLoadout.Items[i].ItemId}') would be refused by " +
                    $"InventoryCommandValidation for {InventMin}");
            }
        }

        [Test]
        public void Money_IsFiftyCaps_AndIsGrantedOntoTheCapsItemRow()
        {
            Assert.That(StartingLoadout.Money, Is.EqualTo(50),
                "items[\"money\"].kol = 50 (Invent.as:1610). One store: GameInventory.Money writes that " +
                "row through ItemDefinitionResolver, so the bag row and the readout cannot disagree. " +
                "The old assertion said the port did not model money as a held item at all, which is " +
                "what let the two numbers drift apart.");
        }

        [Test]
        public void Favourites_BindTheCleaverAndPistolToSlotsOneAndTwo()
        {
            Assert.That(StartingLoadout.Favourites.Length, Is.EqualTo(2),
                "addMin favours exactly two things.");

            Assert.That(StartingLoadout.Favourites[0].ItemId, Is.EqualTo("mont"));
            Assert.That(StartingLoadout.Favourites[0].Slot, Is.EqualTo(1), "favItem(\"mont\", 1)");

            Assert.That(StartingLoadout.Favourites[1].ItemId, Is.EqualTo("r32"));
            Assert.That(StartingLoadout.Favourites[1].Slot, Is.EqualTo(2), "favItem(\"r32\", 2)");
        }

        [Test]
        public void NoIdIsNamedTwice()
        {
            // A repeat would double-grant: AddItem merges into one stack, so a duplicated row would
            // quietly inflate the total, and AddArmor refuses the second outright.
            var seen = new System.Collections.Generic.List<string>();

            foreach (string id in StartingLoadout.WeaponIds) seen.Add(id);
            foreach (string id in StartingLoadout.ArmorIds) seen.Add(id);
            foreach (StartingLoadout.Stack stack in StartingLoadout.Items) seen.Add(stack.ItemId);
            seen.Add("money");

            for (int i = 0; i < seen.Count; i++)
            {
                for (int j = i + 1; j < seen.Count; j++)
                {
                    Assert.That(seen[j], Is.Not.EqualTo(seen[i]),
                        $"'{seen[i]}' is named twice; a doubled row would double the grant.");
                }
            }
        }

        [Test]
        public void TheLoadoutNamesTenIds()
        {
            Assert.That(StartingLoadout.IdCount, Is.EqualTo(10),
                "4 weapons + 1 armour + 4 items + money. A change to any one is a change to addMin.");
        }

        // ── The lints: can the grant actually land? ───────────────────────────

        [Test]
        public void EveryGrantableRowHasAContentAssetOnDisk()
        {
            string root = SourceLint.ProjectRoot();

            foreach (StartingLoadout.Stack stack in StartingLoadout.Items)
            {
                Assert.That(File.Exists(ItemAsset(root, stack.ItemId)), Is.True,
                    $"no item row for '{stack.ItemId}': the grant would be refused and the " +
                    $"inventory would start short by {stack.Quantity} ({InventMin}).");
            }

            foreach (string id in StartingLoadout.ArmorIds)
            {
                Assert.That(File.Exists(ArmorAsset(root, id)), Is.True,
                    $"no armour row for '{id}': the grant would be refused ({InventMin}).");
            }
        }

        [Test]
        public void TheFourStartingWeaponsResolveThroughTheWeaponTable_AndNotThroughAnItemRow()
        {
            // The four `addWeapon` grants in SeedStartingLoadout resolve through ContentType.Weapon, so
            // each id needs a Resources/Weapons asset or the grant is refused at boot. Without the asset
            // the failure would be DATA rather than model, and the error log would be naming the wrong
            // thing to fix.
            //
            // The second assertion guards the tempting wrong fix. Adding a Resources/Items/r32.asset would
            // make ResolveItem answer for the id — but it would produce an item-shaped weapon: a stackable
            // row with a weight and no condition, magazine or respect state. The Oracle keeps the two
            // tables disjoint (AllData.d.weapon vs AllData.d.item) and so must the port.
            string root = SourceLint.ProjectRoot();

            foreach (string id in StartingLoadout.WeaponIds)
            {
                Assert.That(File.Exists(WeaponAsset(root, id)), Is.True,
                    $"no weapon definition for '{id}': GameInventory.AddWeapon resolves through " +
                    $"ContentType.Weapon, so the starting grant would be refused for a missing asset.");

                Assert.That(File.Exists(ItemAsset(root, id)), Is.False,
                    $"'{id}' has an item row. A weapon must resolve through ContentType.Weapon — an item " +
                    $"row would make it a stackable item with a weight and no condition or magazine. " +
                    $"Delete Resources/Items/{id}.asset.");
            }
        }

        // ── Paths ────────────────────────────────────────────────────────────

        /// <summary>Where the port's <c>ContentType.Item</c> rows for consumables live.</summary>
        private static string ItemAsset(string root, string itemId)
            => Path.Combine(root, "Assets", "_PFE", "Data", "Resources", "Items", itemId + ".asset");

        /// <summary>Where armour rows live. They register as <c>ContentType.Item</c> too.</summary>
        private static string ArmorAsset(string root, string armorId)
            => Path.Combine(root, "Assets", "_PFE", "Data", "Resources", "Armor", armorId + ".asset");

        /// <summary>Where weapon definitions live — the table <c>GameInventory.AddWeapon</c> resolves
        /// through, and the table a weapon id must NOT also appear in as an item row.</summary>
        private static string WeaponAsset(string root, string weaponId)
            => Path.Combine(root, "Assets", "_PFE", "Data", "Resources", "Weapons", weaponId + ".asset");
    }
}
