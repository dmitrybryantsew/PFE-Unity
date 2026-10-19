using System.Collections.Generic;
using NUnit.Framework;
using PFE.Data.Definitions.Campaign;
using PFE.Systems.Trade;

namespace PFE.Tests.Editor.Trade
{
    /// <summary>
    /// The trade page's rules, run offline — <c>VendorTradeRules</c> is plain C# for exactly this
    /// reason.
    ///
    /// <para><b>Every case names the Oracle line it pins.</b> A rule that is only asserted "works" is a
    /// rule that can be changed to anything; the citations are what make a later edit a decision.</para>
    /// </summary>
    [TestFixture]
    public sealed class VendorTradeRulesTests
    {
        // ── Fixtures ──────────────────────────────────────────────────────────

        private sealed class Facts : IVendorBuyFacts
        {
            public bool Scheme;
            public bool Variant;
            public bool Armor;
            public bool Art;
            public bool Trigger;

            public bool OwnsScheme(string itemId) => Scheme;
            public bool OwnsWeaponVariantAtLeast(string itemId, int variant) => Variant;
            public bool OwnsArmor(string itemId) => Armor;
            public bool OwnsArt(string itemId) => Art;
            public bool TriggerIsOne(string triggerName) => Trigger;
        }

        private static VendorStock Stock(string id, int kol = 5, string tip = "med", int variant = 0)
        {
            return new VendorStock { Id = id, Kol = kol, Tip = tip, Variant = variant, Sost = 1f };
        }

        private static VendorItemFacts Item(int price = 100, int sell = 10, float maxHp = 100f)
        {
            return new VendorItemFacts
            {
                Exists = true,
                Name = "thing",
                Tip = "med",
                Price = price,
                SellPrice = sell,
                MaxHp = maxHp
            };
        }

        private static VendorPageContext Ctx(int caps = 1000, int level = 10, int barter = 0,
            bool hard = false, bool inBase = false, string filter = "")
        {
            return new VendorPageContext
            {
                Caps = caps,
                Level = level,
                BarterLevel = barter,
                HardInventory = hard,
                InBase = inBase,
                CategoryFilter = filter
            };
        }

        // ── Buy admission (PipPageVend.as:129-210) ────────────────────────────

        [Test]
        public void Admit_EmptyStock_IsRejected()
        {
            // :131 — `if(b.kol <= 0) continue;`
            Assert.IsFalse(VendorTradeRules.AdmitBuyRow(Stock("x", kol: 0), Item(), Ctx(), new Facts(), out _));
        }

        [Test]
        public void Admit_KnownScheme_IsRejected()
        {
            // :135 — a schematic the player already knows.
            var facts = new Facts { Scheme = true };
            Assert.IsFalse(VendorTradeRules.AdmitBuyRow(Stock("s", tip: "scheme"), Item(), Ctx(), facts, out _));
        }

        [Test]
        public void Admit_OwnedWeaponVariant_IsRejected()
        {
            // :137 — the same weapon at a variant the player already has.
            var facts = new Facts { Variant = true };
            Assert.IsFalse(VendorTradeRules.AdmitBuyRow(Stock("shotgun", tip: "weapon"), Item(), Ctx(), facts, out _));
        }

        [Test]
        public void Admit_OwnedArmor_IsRejected()
        {
            // :139.
            var facts = new Facts { Armor = true };
            Assert.IsFalse(VendorTradeRules.AdmitBuyRow(Stock("leather", tip: "armor"), Item(), Ctx(), facts, out _));
        }

        [Test]
        public void Admit_MissingItemRow_IsRejected_ExceptForWeapons()
        {
            // :141 — `b.xml.@price` must exist unless the row is a weapon. A weapon is priced from
            // the weapon table, so an item row is not required.
            var missing = new VendorItemFacts { Exists = false };

            Assert.IsFalse(VendorTradeRules.AdmitBuyRow(Stock("potion", tip: "med"), missing, Ctx(), new Facts(), out _),
                "a non-weapon row with no item row must not be listed");

            Assert.IsTrue(VendorTradeRules.AdmitBuyRow(Stock("shotgun", tip: "weapon"), missing, Ctx(), new Facts(), out _),
                "a weapon row is priced from the weapon table and has no item row");
        }

        [Test]
        public void Admit_LevelAndBarterGates_AreInclusive()
        {
            // :145 — `b.lvl <= pers.level && b.barter <= pers.barterLvl`. Equal passes.
            VendorStock row = Stock("x");
            row.Lvl = 10;
            row.Barter = 3;

            Assert.IsTrue(VendorTradeRules.AdmitBuyRow(row, Item(), Ctx(level: 10, barter: 3), new Facts(), out _),
                "a row at exactly the player's level and barter level is admitted");

            Assert.IsFalse(VendorTradeRules.AdmitBuyRow(row, Item(), Ctx(level: 9, barter: 3), new Facts(), out _));
            Assert.IsFalse(VendorTradeRules.AdmitBuyRow(row, Item(), Ctx(level: 10, barter: 2), new Facts(), out _));
        }

        [Test]
        public void Admit_TriggerGate_NeedsTheTriggerToBeOne()
        {
            // :147.
            VendorStock row = Stock("x");
            row.Trig = "power_armor";

            Assert.IsFalse(VendorTradeRules.AdmitBuyRow(row, Item(), Ctx(), new Facts(), out _));

            var facts = new Facts { Trigger = true };
            Assert.IsTrue(VendorTradeRules.AdmitBuyRow(row, Item(), Ctx(), facts, out _));
        }

        [Test]
        public void Admit_HardInventoryRow_NeedsHardInventory()
        {
            // :149 — `!b.hardinv || World.w.hardInv`.
            VendorStock row = Stock("bag1");
            row.Hardinv = true;

            Assert.IsFalse(VendorTradeRules.AdmitBuyRow(row, Item(), Ctx(hard: false), new Facts(), out _));
            Assert.IsTrue(VendorTradeRules.AdmitBuyRow(row, Item(), Ctx(hard: true), new Facts(), out _));
        }

        // ── The price (PipPageVend.as:154-158, 180-183) ───────────────────────

        [Test]
        public void Multiplier_IsTheMaxOfTheItemsAndTheVendors()
        {
            // Item.getMultPrice() = sell/price = 10/100 = 0.1; the vendor's 0.8 is higher and wins.
            Assert.AreEqual(0.8f, VendorTradeRules.BuyMultiplier(Stock("x"), Item(price: 100, sell: 10), 0.8f),
                0.0001f);

            // A player multiplier of 0.5 does NOT undercut the item's own 0.9.
            Assert.AreEqual(0.9f, VendorTradeRules.BuyMultiplier(Stock("x"), Item(price: 100, sell: 90), 0.5f),
                0.0001f);
        }

        [Test]
        public void Multiplier_NoCheapForcesOne()
        {
            // :180-183 — `nocheap` sets mp = 1 regardless of either side.
            VendorStock row = Stock("x");
            row.Nocheap = true;

            Assert.AreEqual(1f, VendorTradeRules.BuyMultiplier(row, Item(price: 100, sell: 90), 0.8f), 0.0001f);
        }

        [Test]
        public void UnitPrice_AppliesConditionAndRounds()
        {
            // price * sost, then * mp, rounded.
            VendorStock row = Stock("x");
            row.Sost = 0.5f;

            int price = VendorTradeRules.BuyUnitPrice(row, Item(price: 290, sell: 29), 1f, out _);
            Assert.AreEqual(145, price);
        }

        [Test]
        public void UnitPrice_AppliesPmultWhenTheRowSetIt()
        {
            VendorStock row = Stock("power");
            row.HasPmult = true;
            row.Pmult = 0.4f;

            int price = VendorTradeRules.BuyUnitPrice(row, Item(price: 1000, sell: 100), 1f, out _);
            Assert.AreEqual(400, price);
        }

        // ── The ledger (PipPageVend.as:705-714, 719, 948, 990) ────────────────

        [Test]
        public void ClampToAffordable_FloorDividesTheRemainingCaps()
        {
            // 250 caps left, 58 a unit -> 4, not 5.
            Assert.AreEqual(4, VendorTradeRules.ClampToAffordable(10, 58, 250));
            Assert.AreEqual(0, VendorTradeRules.ClampToAffordable(10, 58, 10));
            Assert.AreEqual(3, VendorTradeRules.ClampToAffordable(3, 58, 250));
        }

        [Test]
        public void Totals_BuyRoundsUp_SellRoundsDown()
        {
            // :948 `Math.ceil(kolBou)`; :990 `Math.floor(kolSell)`. The vendor keeps the fraction.
            Assert.AreEqual(4, VendorTradeRules.BuyTotal(3.1f));
            Assert.AreEqual(3, VendorTradeRules.SellTotal(3.9f));
        }

        [Test]
        public void Sale_IsRefusedOutsideABaseWhenTheVendorCannotPay()
        {
            // :962-966 — in a base the purse does not matter.
            Assert.IsFalse(VendorTradeRules.CanPayForSale(Ctx(inBase: false), vendorMoney: 100, kolSell: 150f));
            Assert.IsTrue(VendorTradeRules.CanPayForSale(Ctx(inBase: true), vendorMoney: 100, kolSell: 150f));
            Assert.IsTrue(VendorTradeRules.CanPayForSale(Ctx(inBase: false), vendorMoney: 100, kolSell: 100f));
        }

        // ── Repair (PipPageVend.as:594) ───────────────────────────────────────

        [Test]
        public void RepairPrice_IsTheDamageFractionTimesTheMultipliers()
        {
            // ceil(price * (maxhp-hp)/maxhp * multPrice * mp)
            int price = VendorTradeRules.RepairPrice(1000f, hp: 50f, maxHp: 100f, vendorMult: 1f, mp: 1f);
            Assert.AreEqual(500, price);

            int armour = VendorTradeRules.RepairPrice(1000f, hp: 50f, maxHp: 100f, vendorMult: 1f, mp: 0.5f);
            Assert.AreEqual(250, armour, "armour uses priceRepArmor as its extra factor");
        }

        [Test]
        public void RepairPrice_IsZeroAtFullHealth()
        {
            Assert.AreEqual(0, VendorTradeRules.RepairPrice(1000f, 100f, 100f, 1f, 1f));
        }

        [Test]
        public void IsRepairable_ExcludesFullHealthAndUnsupportedTips()
        {
            Assert.IsFalse(VendorTradeRules.IsRepairable("weapon", 100f, 100f));
            Assert.IsTrue(VendorTradeRules.IsRepairable("weapon", 99f, 100f));
            Assert.IsTrue(VendorTradeRules.IsRepairable("armor", 1f, 100f));
            Assert.IsFalse(VendorTradeRules.IsRepairable("med", 1f, 100f), "only weapons and armour are repairable");
            Assert.IsFalse(VendorTradeRules.IsRepairable("weapon", 1f, 100f, respects: true),
                ":323-343 excludes respect == 1");
        }

        // ── Sorting (PipPageVend.as:213) ──────────────────────────────────────

        [Test]
        public void BuySort_IsCategoryThenBarterThenPrice()
        {
            var rows = new List<VendorBuyRow>
            {
                new VendorBuyRow { Rid = "b", SortName = "weapons", Barter = 0, Price = 100 },
                new VendorBuyRow { Rid = "a", SortName = "aid", Barter = 0, Price = 900 },
                new VendorBuyRow { Rid = "c", SortName = "weapons", Barter = 2, Price = 10 },
                new VendorBuyRow { Rid = "d", SortName = "weapons", Barter = 0, Price = 50 },
            };

            VendorTradeRules.SortBuyRows(rows);

            Assert.AreEqual(new[] { "a", "d", "b", "c" }, new[] { rows[0].Rid, rows[1].Rid, rows[2].Rid, rows[3].Rid });
        }
    }
}
