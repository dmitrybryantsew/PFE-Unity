#if UNITY_EDITOR
using NUnit.Framework;
using PFE.Data.Definitions;
using UnityEngine;

namespace PFE.Tests.Editor
{
    /// <summary>
    /// Verification tests for imported data.
    /// Ensures all imported assets are loadable and valid.
    /// </summary>
    [TestFixture]
    public class DataImportVerificationTests
    {
        [Test]
        public void AmmoImport_VerifyCount()
        {
            // The live type is AmmoDefinition, not the superseded AmmoData (which nothing in
            // production references). `AmmoDefinitionImporter` (Assets/_PFE/Editor/Importers/
            // AmmoDataImporter.cs) writes to Assets/_PFE/Data/Resources/Ammo, selecting
            // tip='a' | 'compw' | 'stuff' and skipping chance<=0.
            //
            // Verified against AllData.as: 500 <item> rows -> 49 tip='a' + 24 'compw' + 9 'stuff'
            // = 82 candidates, minus 7 with chance<=0 (recharg, not, kogt, hcrystal, fan, lamp,
            // kofe) = 75. `tip='a'` alone is 49, which is the row count the ITEM import creates in
            // Items/ — a different number because it is a different question.
            AmmoDefinition[] ammo = Resources.LoadAll<AmmoDefinition>("Ammo");
            Assert.AreEqual(75, ammo.Length, $"Expected 75 ammo definitions, got {ammo.Length}");
        }

        [Test]
        public void ItemsImport_VerifyCount()
        {
            // AS3 AllData.as carries exactly 500 <item> rows. `SimpleDataImporter.ImportItems` used
            // to skip `tip == "a"` (49 rounds), which is where the old 451 came from — but that skip
            // was the ammo bug (nothing could draw a round), and it is gone. All 500 now land.
            ItemDefinition[] items = Resources.LoadAll<ItemDefinition>("Items");
            Assert.AreEqual(500, items.Length, $"Expected 500 items, got {items.Length}");
        }

        [Test]
        public void PerksImport_VerifyCount()
        {
            PerkDefinition[] perks = Resources.LoadAll<PerkDefinition>("Perks");
            Assert.AreEqual(84, perks.Length, $"Expected 84 perks, got {perks.Length}");
        }

        [Test]
        public void TotalAssets_VerifyCount()
        {
            // Note the type mix: Ammo/ holds AmmoDefinition, Perks/ and Items/ hold the
            // PFE.Data.Definitions types. The sum is 500 + 84 + 75 = 659. The old 582 was
            // 451 + 84 + 47 — i.e. it inherited both stale counts.
            int total = Resources.LoadAll<AmmoDefinition>("Ammo").Length +
                       Resources.LoadAll<ItemDefinition>("Items").Length +
                       Resources.LoadAll<PerkDefinition>("Perks").Length;
            Assert.AreEqual(659, total, $"Expected total 659 assets, got {total}");
        }

        [Test]
        public void Ammo_ContainsEssentialItems()
        {
            var p10 = Resources.Load<AmmoDefinition>("Ammo/p10");
            Assert.IsNotNull(p10, "p10 ammo should exist");
            Assert.AreEqual("p10", p10.ID, "p10 should have correct ID");

            var batt = Resources.Load<AmmoDefinition>("Ammo/batt");
            Assert.IsNotNull(batt, "batt ammo should exist");

            var fuel = Resources.Load<AmmoDefinition>("Ammo/fuel");
            Assert.IsNotNull(fuel, "fuel ammo should exist");
        }

        [Test]
        public void Items_ContainsEssentialItems()
        {
            var stealth = Resources.Load<ItemDefinition>("Items/stealth");
            Assert.IsNotNull(stealth, "stealth item should exist");

            var pot1 = Resources.Load<ItemDefinition>("Items/pot1");
            Assert.IsNotNull(pot1, "pot1 medical item should exist");

            var mint = Resources.Load<ItemDefinition>("Items/mint");
            Assert.IsNotNull(mint, "mint chem should exist");

            var book_cm = Resources.Load<ItemDefinition>("Items/book_cm");
            Assert.IsNotNull(book_cm, "book_cm should exist");
        }

        [Test]
        public void ArmorImport_VerifyCount()
        {
            ItemDefinition[] armour = Resources.LoadAll<ItemDefinition>("Armor");

            if (armour.Length == 0)
            {
                Assert.Ignore(
                    "Resources/Armor is empty — run 'PFE/Data/Import Armour from AllData.as' and re-run. " +
                    "Ignored rather than failed because importing is a manual menu step, and a red suite " +
                    "that only means 'you have not run the menu item' trains people to ignore red.");
                return;
            }

            Assert.AreEqual(35, armour.Length, $"Expected 35 armour definitions, got {armour.Length}");
        }

        [Test]
        public void ArmorImport_AllHaveValidIds()
        {
            foreach (var armour in Resources.LoadAll<ItemDefinition>("Armor"))
                Assert.IsFalse(string.IsNullOrEmpty(armour.itemId), $"Armour has null/empty ID: {armour.name}");
        }

        [Test]
        public void ArmorIds_DoNotCollideWithItemIds()
        {
            // Armour lives in its own Resources folder but registers under the SAME ContentType.Item as
            // items, so the two sets share one registry namespace. A collision is not raised loudly —
            // additive policy keeps the first and only warns — so it is checked here instead.
            var itemIds = new System.Collections.Generic.HashSet<string>();
            foreach (var item in Resources.LoadAll<ItemDefinition>("Items"))
                if (!string.IsNullOrEmpty(item.itemId)) itemIds.Add(item.itemId);

            foreach (var armour in Resources.LoadAll<ItemDefinition>("Armor"))
            {
                if (string.IsNullOrEmpty(armour.itemId)) continue;

                Assert.IsFalse(itemIds.Contains(armour.itemId),
                    $"'{armour.itemId}' is both an item and an armour; the registry would keep whichever " +
                    "registered first and the other would be unreachable by id.");
            }
        }

        [Test]
        public void Perks_ContainsEssentialPerks()
        {
            var levitation = Resources.Load<PerkDefinition>("Perks/levitation");
            Assert.IsNotNull(levitation, "levitation perk should exist");

            var oak = Resources.Load<PerkDefinition>("Perks/oak");
            Assert.IsNotNull(oak, "oak perk should exist");

            var pistol = Resources.Load<PerkDefinition>("Perks/pistol");
            Assert.IsNotNull(pistol, "pistol perk should exist");

            var acute = Resources.Load<PerkDefinition>("Perks/acute");
            Assert.IsNotNull(acute, "acute perk should exist");

            var shot = Resources.Load<PerkDefinition>("Perks/shot");
            Assert.IsNotNull(shot, "shot perk should exist");
        }

        [Test]
        public void Ammo_AllHaveValidIds()
        {
            var ammo = Resources.LoadAll<AmmoDefinition>("Ammo");
            foreach (var a in ammo)
            {
                Assert.IsFalse(string.IsNullOrEmpty(a.ID), $"Ammo has null/empty ID: {a.name}");
                Assert.IsTrue(a.ID.Length > 0, $"Ammo has empty ID: {a.name}");
            }
        }

        [Test]
        public void Items_AllHaveValidIds()
        {
            var items = Resources.LoadAll<ItemDefinition>("Items");
            foreach (var item in items)
            {
                Assert.IsFalse(string.IsNullOrEmpty(item.itemId), $"Item has null/empty ID: {item.name}");
            }
        }

        [Test]
        public void Perks_AllHaveValidIds()
        {
            var perks = Resources.LoadAll<PerkDefinition>("Perks");
            foreach (var perk in perks)
            {
                Assert.IsFalse(string.IsNullOrEmpty(perk.perkId), $"Perk has null/empty ID: {perk.name}");
            }
        }

        [Test]
        public void DataImport_NoDuplicateIds()
        {
            var ammo = Resources.LoadAll<AmmoDefinition>("Ammo");
            var items = Resources.LoadAll<ItemDefinition>("Items");
            var perks = Resources.LoadAll<PerkDefinition>("Perks");

            var ammoIds = new System.Collections.Generic.HashSet<string>();
            var itemIds = new System.Collections.Generic.HashSet<string>();
            var perkIds = new System.Collections.Generic.HashSet<string>();

            foreach (var a in ammo)
            {
                Assert.IsFalse(ammoIds.Contains(a.ID), $"Duplicate ammo ID: {a.ID}");
                ammoIds.Add(a.ID);
            }

            foreach (var item in items)
            {
                string id = item.itemId;
                // Skip empty IDs for now - they'll be fixed by the importer
                if (!string.IsNullOrEmpty(id))
                {
                    Assert.IsFalse(itemIds.Contains(id), $"Duplicate item ID: {id}");
                    itemIds.Add(id);
                }
            }

            foreach (var perk in perks)
            {
                string id = perk.perkId;
                // Skip empty IDs for now - they'll be fixed by the importer
                if (!string.IsNullOrEmpty(id))
                {
                    Assert.IsFalse(perkIds.Contains(id), $"Duplicate perk ID: {id}");
                    perkIds.Add(id);
                }
            }
        }
    }
}
#endif
