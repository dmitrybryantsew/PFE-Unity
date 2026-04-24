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
            AmmoData[] ammo = Resources.LoadAll<AmmoData>("Ammo");
            Assert.AreEqual(47, ammo.Length, $"Expected 47 ammo items, got {ammo.Length}");
        }

        [Test]
        public void ItemsImport_VerifyCount()
        {
            ItemDefinition[] items = Resources.LoadAll<ItemDefinition>("Items");
            Assert.AreEqual(451, items.Length, $"Expected 451 items, got {items.Length}");
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
            int total = Resources.LoadAll<AmmoData>("Ammo").Length +
                       Resources.LoadAll<ItemDefinition>("Items").Length +
                       Resources.LoadAll<PerkDefinition>("Perks").Length;
            Assert.AreEqual(582, total, $"Expected total 582 assets, got {total}");
        }

        [Test]
        public void Ammo_ContainsEssentialItems()
        {
            var p10 = Resources.Load<AmmoData>("Ammo/p10");
            Assert.IsNotNull(p10, "p10 ammo should exist");
            Assert.AreEqual("p10", p10.Id, "p10 should have correct ID");

            var batt = Resources.Load<AmmoData>("Ammo/batt");
            Assert.IsNotNull(batt, "batt ammo should exist");

            var fuel = Resources.Load<AmmoData>("Ammo/fuel");
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
            var ammo = Resources.LoadAll<AmmoData>("Ammo");
            foreach (var a in ammo)
            {
                Assert.IsFalse(string.IsNullOrEmpty(a.Id), $"Ammo has null/empty ID: {a.name}");
                Assert.IsTrue(a.Id.Length > 0, $"Ammo has empty ID: {a.name}");
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
            var ammo = Resources.LoadAll<AmmoData>("Ammo");
            var items = Resources.LoadAll<ItemDefinition>("Items");
            var perks = Resources.LoadAll<PerkDefinition>("Perks");

            var ammoIds = new System.Collections.Generic.HashSet<string>();
            var itemIds = new System.Collections.Generic.HashSet<string>();
            var perkIds = new System.Collections.Generic.HashSet<string>();

            foreach (var a in ammo)
            {
                Assert.IsFalse(ammoIds.Contains(a.Id), $"Duplicate ammo ID: {a.Id}");
                ammoIds.Add(a.Id);
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
