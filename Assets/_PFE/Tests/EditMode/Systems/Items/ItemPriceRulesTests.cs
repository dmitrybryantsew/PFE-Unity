using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using PFE.Systems.Items;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.EditMode.Systems.Items
{
    /// <summary>
    /// Pins <see cref="ItemPriceRules"/> against the shipped <c>AllData</c> rows — the item table's
    /// <c>@price</c> / <c>@sell</c>, which is what <c>Item.getPrice()</c> and <c>Item.getMultPrice()</c>
    /// read (<c>Item.as:352-383</c>).
    ///
    /// <para><b>The bug being guarded.</b> Every one of the 500 <c>ItemDefinition</c> assets carries the
    /// field default (<c>basePrice: 10</c>, <c>sellPrice: 5</c>) — measured on the shipped set, 491 of
    /// 500 are exactly 10, and the nine that are not are the spells, which the importer fills by hand.
    /// The vendor page priced from the asset, so <c>vendor_medic</c> sold the four implants — 10 000,
    /// 10 000, 15 000 and 15 000 caps — for <b>10 caps each</b>, and because the placeholder
    /// <c>sellPrice</c> was positive, every implant was also listed in the Sell page at 5 caps. AS3
    /// gives an implant no <c>@sell</c> at all, which is precisely what keeps it out of that list
    /// (<c>PipPageVend.as:237</c>).</para>
    ///
    /// <para><b>Why the numbers are read from the file rather than typed in.</b> A fixture that
    /// hard-codes 10 000 proves the rule copies a constant; one that reads the mirror proves the rule
    /// agrees with the data that ships. The literals are still asserted, so a silent edit to
    /// <c>impl_skin</c>'s price fails here — which is the point.</para>
    /// </summary>
    public class ItemPriceRulesTests
    {
        /// <summary>Id → the row's attribute run, from the shipped mirror. Null entries are "no row".</summary>
        private static Dictionary<string, string> _rows;

        [OneTimeSetUp]
        public void LoadShippedRows()
        {
            string mirror = Path.Combine(SourceLint.ProjectRoot(),
                "Assets", "_PFE", "Data", "Resources", "Loot", "AllData.txt");

            if (!File.Exists(mirror))
            {
                Assert.Ignore("the shipped AllData mirror is missing: " + mirror);
            }

            _rows = new Dictionary<string, string>();
            foreach (ItemXmlRow row in ItemXmlRows.Parse(File.ReadAllText(mirror)))
            {
                if (!string.IsNullOrEmpty(row.Id) && !_rows.ContainsKey(row.Id))
                    _rows[row.Id] = row.Attrs;
            }

            Assert.Greater(_rows.Count, 400, "the item table did not parse — every case below would be vacuous.");
        }

        private static string Row(string id)
        {
            Assert.IsTrue(_rows.TryGetValue(id, out string attrs), "no <item> row for '" + id + "' in the mirror.");
            return attrs;
        }

        // ── the four implants the doctor sells ────────────────────────────────────────────────────

        /// <summary>
        /// <c>vendor_medic</c>'s four implants, at the prices their rows carry
        /// (<c>AllData.as:4132-4148</c>). These are the numbers the shop must charge.
        /// </summary>
        [TestCase("impl_skin", 10000)]
        [TestCase("impl_mrech", 10000)]
        [TestCase("impl_regen", 15000)]
        [TestCase("impl_intel", 15000)]
        public void AnImplantPricesAtItsRowPrice(string itemId, int expected)
        {
            // The 10 is the asset's placeholder — the value the port actually charged before the row
            // was read. Passing it in proves the row wins.
            Assert.AreEqual(expected, ItemPriceRules.BuyBase(Row(itemId), 10),
                itemId + " must price at its row's @price, not at the ItemDefinition placeholder.");
        }

        /// <summary>
        /// None of the four carries <c>@sell</c>, so none of them may appear in the Sell page —
        /// <c>PipPageVend.as:237</c> lists only rows with a positive <c>@sell</c>.
        /// </summary>
        [TestCase("impl_skin")]
        [TestCase("impl_mrech")]
        [TestCase("impl_regen")]
        [TestCase("impl_intel")]
        public void AnImplantCannotBeSoldBack(string itemId)
        {
            // 5 is the asset placeholder that made every implant sellable.
            Assert.AreEqual(0, ItemPriceRules.SellPrice(Row(itemId), 5),
                itemId + " has no @sell, so the Sell list must not offer it.");

            // …and therefore its sell-side multiplier is the oracle's 0.1 fallback (Item.as:376-383).
            Assert.AreEqual(0.1f, ItemPriceRules.MultPrice(
                ItemPriceRules.BuyBase(Row(itemId), 10),
                ItemPriceRules.SellPrice(Row(itemId), 5)), 1e-6f);
        }

        /// <summary>
        /// The other side of the same coin: a row that <i>does</i> carry <c>@sell</c> is sellable, and
        /// its ratio is the oracle's <c>sell / price</c>.
        /// </summary>
        [Test]
        public void AConsumableCarriesItsSellPriceAndRatio()
        {
            string pot1 = Row("pot1");   // price='50' sell='10' (AllData.as:3862)
            Assert.AreEqual(50, ItemPriceRules.BuyBase(pot1, 10));
            Assert.AreEqual(10, ItemPriceRules.SellPrice(pot1, 5));
            Assert.AreEqual(0.2f, ItemPriceRules.MultPrice(50, 10), 1e-6f);
        }

        /// <summary>
        /// An instrument is <c>instr</c>, not <c>impl</c>, and the rule must not special-case the tip:
        /// <c>stethoscope</c> costs 2000 and has no <c>@sell</c> either (<c>AllData.as:4115</c>).
        /// </summary>
        [Test]
        public void AnInstrumentPricesTheSameWay()
        {
            Assert.AreEqual(2000, ItemPriceRules.BuyBase(Row("stethoscope"), 10));
            Assert.AreEqual(0, ItemPriceRules.SellPrice(Row("stethoscope"), 5));
        }

        // ── the edges ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 75 of the 500 rows carry no <c>@price</c> (<c>money</c>, <c>bit</c>, <c>gem1..3</c>,
        /// <c>data</c>, …). AS3's <c>@price * sost * multHP * pmult</c> on an absent attribute is
        /// <b>0</b>, and the buy page then skips the row (<c>PipPageVend.as:141</c>). Falling back to
        /// the asset's 10 would invent a price the oracle does not have.
        /// </summary>
        [Test]
        public void ARowWithoutPriceIsZero_NotTheAssetFallback()
        {
            string money = Row("money");
            Assert.IsFalse(ItemPriceRules.HasPrice(money), "money authors no @price.");
            Assert.AreEqual(0, ItemPriceRules.BuyBase(money, 10));
            Assert.AreEqual(0, ItemPriceRules.SellPrice(money, 5));
        }

        /// <summary>
        /// The fallback exists for one case only: the mirror is not there at all (offline, or the
        /// TextAsset was not shipped). <c>PlayerInventory.ItemRowAttrsOrNull</c> returns null for that,
        /// and the asset's value is then the only answer available.
        /// </summary>
        [Test]
        public void NoRowAtAll_FallsBackToTheAsset()
        {
            Assert.AreEqual(10, ItemPriceRules.BuyBase(null, 10));
            Assert.AreEqual(5, ItemPriceRules.SellPrice(null, 5));
            Assert.IsFalse(ItemPriceRules.HasPrice(null));
        }

        /// <summary>
        /// <c>Item.price</c> is a Number and 41 of the shipped values are fractional — every one an
        /// ammunition row. The port's facts carry ints, so they round half-up; this pins that choice
        /// on the one row that shows it (<c>p9</c>: <c>price='1.5' sell='0.3'</c>).
        /// </summary>
        [Test]
        public void AFractionalAmmoPriceRoundsHalfUp()
        {
            string p9 = Row("p9");
            Assert.AreEqual(2, ItemPriceRules.BuyBase(p9, 1));
            Assert.AreEqual(0, ItemPriceRules.SellPrice(p9, 0));
        }

        /// <summary>
        /// <c>MultPrice</c> is <c>sell / price</c> only when <b>both</b> are positive
        /// (<c>Item.as:376-383</c>); everything else is 0.1. The two zero arms are separate bugs in the
        /// oracle's eyes — a priceless row and an unsellable one both fall back, but for different
        /// reasons.
        /// </summary>
        [Test]
        public void MultPriceFallsBackToPointOne()
        {
            Assert.AreEqual(0.1f, ItemPriceRules.MultPrice(0, 10), 1e-6f);
            Assert.AreEqual(0.1f, ItemPriceRules.MultPrice(100, 0), 1e-6f);
            Assert.AreEqual(0.1f, ItemPriceRules.MultPrice(0, 0), 1e-6f);
            Assert.AreEqual(0.5f, ItemPriceRules.MultPrice(100, 50), 1e-6f);
        }

        /// <summary>
        /// A negative control for the whole fixture: if the mirror were being read as the
        /// <i>assets</i> are (everything 10), the implant cases above would fail. This asserts the two
        /// populations are actually different, so a future "fix" that makes the rule return the
        /// placeholder everywhere cannot pass by coincidence.
        /// </summary>
        [Test]
        public void TheRowPricesAreNotAllTheSamePlaceholder()
        {
            List<int> prices = _rows.Keys
                .Select(id => ItemPriceRules.BuyBase(_rows[id], 10))
                .Distinct()
                .ToList();

            Assert.Greater(prices.Count, 50,
                "the item table's prices collapsed to a handful of values — the row read is not happening.");
            Assert.Contains(10000, prices);
            Assert.Contains(15000, prices);
        }
    }
}
