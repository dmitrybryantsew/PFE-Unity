using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using PFE.Systems.Loot;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.Editor.Loot
{
    /// <summary>
    /// D2d of <c>docs/DeviceInteractionAndLoot/04_IMPLEMENTATION_PLAN.md</c>: the reader that turns the
    /// real <c>AllData.as</c> rows into the pool set the roller draws from, and the item catalog it
    /// resolves ids through.
    ///
    /// <para><b>Why this is the wiring half and not a nicety.</b> <c>LootPoolSet.Build</c> was called
    /// only from tests before this: nothing in shipping code turned real data into rows, so the D5 spine
    /// was unreachable at runtime. These tests run the reader against the <b>real</b> file, so the counts
    /// they assert are the oracle's, not a fixture's.</para>
    ///
    /// <para>Every census number below was measured from the shipped <c>AllData.as</c> before it was
    /// written down (500 items / 213 weapons / 35 armours; <c>eda</c> 37, <c>co</c> 21; weapon pool 105,
    /// uniq 62, magic 23). A reader tuned to a fixture cannot satisfy them by accident.</para>
    /// </summary>
    [TestFixture]
    public class LootDataXmlTests
    {
        // =====================================================================
        //  The real file
        // =====================================================================

        [Test]
        public void RealAllData_ParsesIntoTheDocumentedCensus()
        {
            string text = ReadRealAllData();
            if (text == null) Assert.Ignore("AllData.as is not at the project root.");

            Assert.AreEqual(LootDataXml.Outcome.Ok, LootDataXml.TryParse(text, out LootData data, out string error),
                "the <all> block should parse: " + error);

            Assert.AreEqual(500, data.Items.Count, "500 <item> rows.");
            Assert.AreEqual(213, data.Weapons.Count, "213 <weapon> rows.");
            Assert.AreEqual(35, data.Armors.Count, "35 <armor> rows.");

            // Weapons first, then items — the oracle's build order (LootGen.as:41 then :77).
            Assert.AreEqual(713, data.Rows.Count, "one row per weapon and item; armour is in no pool.");
        }

        [Test]
        public void RealAllData_ProducesTheTip2Pools()
        {
            LootPoolSet pools = BuildRealPoolSet(out LootData data);
            if (pools == null) Assert.Ignore("AllData.as is not at the project root.");

            // The 58 tip2 rows: 37 eda (from tip='food') and 21 co (from tip='scheme'). This is the
            // D2d acceptance check verbatim.
            Assert.AreEqual(37, pools.GetPool("eda").Count, "arr[\"eda\"] — LootGen.as:98-112.");
            Assert.AreEqual(21, pools.GetPool("co").Count, "arr[\"co\"].");

            // Control: the tip2 pools are real pools, not aliases of the primary ones.
            Assert.AreNotSame(pools.GetPool("eda"), pools.GetPool("food"));
        }

        [Test]
        public void RealAllData_CompaAndCompw_AreSeparateNonEmptyPools()
        {
            LootPoolSet pools = BuildRealPoolSet(out _);
            if (pools == null) Assert.Ignore("AllData.as is not at the project root.");

            // The five component tips collapse to one ItemType enum in the importer, which is exactly
            // why the pools are keyed on the raw string. Measured: compa 11, compw 24, compe 4,
            // compm 15, compp 29.
            Assert.AreEqual(11, pools.GetPool("compa").Count);
            Assert.AreEqual(24, pools.GetPool("compw").Count);
            Assert.AreEqual(4, pools.GetPool("compe").Count);
            Assert.AreEqual(15, pools.GetPool("compm").Count);
            Assert.AreEqual(29, pools.GetPool("compp").Count);
        }

        [Test]
        public void RealAllData_WeaponPoolIsNonEmpty_WithRealChancesAndWorth()
        {
            LootPoolSet pools = BuildRealPoolSet(out _);
            if (pools == null) Assert.Ignore("AllData.as is not at the project root.");

            IReadOnlyList<LootPoolEntry> weapons = pools.GetPool(LootPoolSet.WeaponKey);

            // D2b: the weapon pool is built from the <com> child, which no importer reads. Reading it
            // here is what makes the pool non-empty — the alternative was an empty arr["weapon"] and
            // every weapon roll returning null.
            Assert.AreEqual(105, weapons.Count, "weapons with tip 1..3 and a <com> child.");

            bool sawChance = false, sawWorth = false;
            for (int i = 0; i < weapons.Count; i++)
            {
                if (weapons[i].Chance > 0f) sawChance = true;
                if (weapons[i].Worth.HasValue && weapons[i].Worth.Value > 0f) sawWorth = true;
            }

            Assert.IsTrue(sawChance, "at least one weapon carries a non-zero com.@chance.");
            Assert.IsTrue(sawWorth, "at least one weapon carries a non-zero com.@worth.");

            Assert.AreEqual(62, pools.GetPool(LootPoolSet.UniqKey).Count, "uniq entries (com.@uniq present).");
            Assert.AreEqual(23, pools.GetPool(LootPoolSet.MagicKey).Count, "tip='5' weapons.");
        }

        // =====================================================================
        //  Shapes and controls
        // =====================================================================

        [Test]
        public void MissingBlock_IsReportedAsSuch_NotAsAnEmptyResult()
        {
            // A file with no <all>...</all> must not read as "a block with no rows" — the two are
            // different problems and the caller has to be able to tell them apart.
            Assert.AreEqual(LootDataXml.Outcome.MissingBlock,
                LootDataXml.TryParse("var x = 1; // no data here", out LootData data, out _));
            Assert.IsNull(data);
        }

        [Test]
        public void WeaponWithoutCom_IsInNoPool()
        {
            const string xml = "<all><weapon id='nocom' tip='1'><x/></weapon></all>";
            Assert.AreEqual(LootDataXml.Outcome.Ok,
                LootDataXml.TryParse(xml, out LootData data, out string error), error);

            LootPoolSet pools = LootPoolSet.Build(data.Rows);
            Assert.AreEqual(0, pools.GetPool(LootPoolSet.WeaponKey).Count,
                "a weapon with no <com> child joins no pool (LootGen.as:43).");
        }

        [Test]
        public void ItemWithoutTip_IsInNoTipPool()
        {
            const string xml = "<all><item id='notip'/></all>";
            Assert.AreEqual(LootDataXml.Outcome.Ok,
                LootDataXml.TryParse(xml, out LootData data, out string error), error);

            LootPoolSet pools = LootPoolSet.Build(data.Rows);
            // The four base pools exist regardless; nothing was added to any tip pool.
            Assert.AreEqual(4, pools.Keys.Count, "only weapon/magic/uniq/pers were created.");
            Assert.IsNull(pools.GetPool("whatever"));
        }

        // =====================================================================
        //  The catalog
        // =====================================================================

        [Test]
        public void Catalog_ResolvesByTip_AndDoesNotCrossCollections()
        {
            string text = ReadRealAllData();
            if (text == null) Assert.Ignore("AllData.as is not at the project root.");
            LootDataXml.TryParse(text, out LootData data, out _);
            var catalog = new LootItemCatalogXml(data);

            // p9 is an ammunition row (tip 'a', kol 12, stage 1) — measured from the file.
            LootItemRow? p9 = catalog.Find(LootTips.Ammo, "p9");
            Assert.IsTrue(p9.HasValue, "p9 resolves as an item.");
            Assert.AreEqual("a", p9.Value.Tip);
            Assert.AreEqual(12, p9.Value.Kol, "p9 authors kol='12'.");

            // The lookup is by tip: asking for an item id under 'weapon' must NOT fall back to the item
            // table, or a weapon id that also existed as an item id would resolve differently than AS3.
            Assert.IsFalse(catalog.Find(LootTips.Weapon, "p9").HasValue,
                "p9 is not a weapon; the weapon lookup must not cross into the item collection.");

            Assert.IsFalse(catalog.Find(LootTips.Ammo, "no_such_id").HasValue);
        }

        [Test]
        public void Catalog_FindByAnyId_OrderIsItemThenWeaponThenArmor()
        {
            string text = ReadRealAllData();
            if (text == null) Assert.Ignore("AllData.as is not at the project root.");
            LootDataXml.TryParse(text, out LootData data, out _);
            var catalog = new LootItemCatalogXml(data);

            // An item id resolves with the 'item' sentinel, which is what makes BuildItem adopt the
            // row's own tip (LootRoller / Item.as:254-257).
            catalog.FindByAnyId("p9", out string itemTip);
            Assert.AreEqual(LootTips.Item, itemTip);

            // A weapon id resolves under 'weapon' — the same sentinel BuildItem tests against.
            catalog.FindByAnyId("punch", out string weaponTip);
            Assert.AreEqual(LootTips.Weapon, weaponTip);

            Assert.IsFalse(catalog.FindByAnyId("definitely_not_an_id", out string none).HasValue);
            Assert.IsNull(none);
        }

        [Test]
        public void Catalog_ReadsKolAndLimits()
        {
            string text = ReadRealAllData();
            if (text == null) Assert.Ignore("AllData.as is not at the project root.");
            LootDataXml.TryParse(text, out LootData data, out _);
            var catalog = new LootItemCatalogXml(data);

            // 21 item rows carry @limit; potHP is one of them.
            LootItemRow? pot = catalog.Find(LootTips.Pot, "potHP");
            Assert.IsTrue(pot.HasValue);
            Assert.IsFalse(string.IsNullOrEmpty(pot.Value.LimitKey), "potHP authors a @limit.");

            // And an unlimited row does not.
            LootItemRow? p9 = catalog.Find(LootTips.Ammo, "p9");
            Assert.IsTrue(p9.HasValue);
            Assert.IsTrue(string.IsNullOrEmpty(p9.Value.LimitKey), "p9 authors no @limit.");
        }

        // =====================================================================
        //  The anti-drift guard
        // =====================================================================

        [Test]
        public void ShippedAllDataMirror_IsByteIdenticalToTheRootFile()
        {
            // LootRuntime reads Resources/Loot/AllData (a TextAsset), which is a copy of the root
            // AllData.as every importer reads. Two copies of one source of truth drift silently; this
            // is the guard that makes the drift loud. Refresh with:
            //   cp AllData.as Assets/_PFE/Data/Resources/Loot/AllData.txt
            string root = SourceLint.ProjectRoot();
            string canonical = Path.Combine(root, "AllData.as");
            string mirror = Path.Combine(root, "Assets", "_PFE", "Data", "Resources", "Loot", "AllData.txt");

            if (!File.Exists(canonical)) Assert.Ignore("AllData.as is not at the project root.");
            Assert.IsTrue(File.Exists(mirror), "the shipped loot mirror is missing: " + mirror);

            byte[] a = File.ReadAllBytes(canonical);
            byte[] b = File.ReadAllBytes(mirror);

            Assert.AreEqual(a.Length, b.Length,
                "the shipped AllData mirror has a different length from the root AllData.as — re-copy it.");
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    Assert.Fail($"the shipped AllData mirror differs from the root AllData.as at byte {i}.");
                }
            }
        }

        // =====================================================================
        //  helpers
        // =====================================================================

        private static LootPoolSet BuildRealPoolSet(out LootData data)
        {
            data = null;
            string text = ReadRealAllData();
            if (text == null) return null;

            Assert.AreEqual(LootDataXml.Outcome.Ok, LootDataXml.TryParse(text, out data, out string error), error);
            return LootPoolSet.Build(data.Rows);
        }

        private static string ReadRealAllData()
        {
            string root = SourceLint.ProjectRoot();
            string[] candidates =
            {
                Path.Combine(root, "AllData.as"),
                Path.Combine(root, "pfe", "scripts", "fe", "AllData.as"),
            };

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate)) return File.ReadAllText(candidate);
            }

            return null;
        }
    }
}
