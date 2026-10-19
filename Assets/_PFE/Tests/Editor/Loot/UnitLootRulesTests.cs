using System.Collections.Generic;
using NUnit.Framework;
using PFE.Systems.Loot;
using PFE.Systems.Map;

namespace PFE.Tests.Editor.Loot
{
    /// <summary>
    /// The placement-<c>&lt;item&gt;</c> projection — the unit half of <c>Unit.dropLoot</c>'s first
    /// statement, <c>if(inter) { inter.loot(); }</c> (<c>Unit.as:4452-455</c> → <c>Interact.as:1973-2000</c>).
    ///
    /// <para><b>Why the arithmetic is worth a fixture.</b> Every number here is <b>1 or 0</b>, decided by
    /// two coercion rules that disagree about which attribute is which: <c>@kol</c> is a <b>presence</b>
    /// test whose absent case means 1, and <c>@imp</c> is a presence test with no value case at all. So a
    /// placement carrying <c>kol=""</c> drops <b>0</b> of an item, while one carrying no <c>kol</c>
    /// drops <b>1</b> — the same "nothing was written" authoring, opposite outcomes. That is invisible in
    /// a screenshot and one <c>HasAttribute</c> away from being flipped.</para>
    ///
    /// <para><b>Why the null-in-null-out contract matters.</b> This is what lets
    /// <c>UnitController.DropLootOnDeath</c> skip the whole block rather than hand the roller a list it
    /// would have to reject — the same shape as AS3's own guard, where a unit with no <c>&lt;item&gt;</c>
    /// children never builds an <c>Interact</c> and the branch never runs.</para>
    /// </summary>
    [TestFixture]
    public class UnitLootRulesTests
    {
        private static MapObjectItemData Row(string id, params (string key, string value)[] attrs)
        {
            var item = new MapObjectItemData { id = id };
            foreach ((string key, string value) pair in attrs)
            {
                item.attributes.Add(new MapObjectAttributeData { key = pair.key, value = pair.value });
            }

            return item;
        }

        // ── The empty and absent cases ────────────────────────────────────────

        [Test]
        public void NoPlacementItems_ReturnsNull()
        {
            Assert.IsNull(UnitLootRules.PlacedItems(null));
            Assert.IsNull(UnitLootRules.PlacedItems(new List<MapObjectItemData>()));
        }

        [Test]
        public void ARowWithNoId_IsDroppedRatherThanRolled()
        {
            // AS3 would call `LootGen.lootId(…, "")` and resolve nothing. 0 of the 564 shipped room assets
            // author an id-less `<item>`, so this is a guard against a bad room, not a rule of play.
            List<MapObjectItemData> source = new List<MapObjectItemData>
            {
                Row("key_sewer", ("imp", "1")),
                Row(null),
                Row(string.Empty),
            };

            List<LootRoller.PlacedLootItem> result = UnitLootRules.PlacedItems(source);

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("key_sewer", result[0].Id);
        }

        [Test]
        public void EveryRowWasEmpty_IsNullNotAnEmptyList()
        {
            List<MapObjectItemData> source = new List<MapObjectItemData> { Row(null), Row("") };
            Assert.IsNull(UnitLootRules.PlacedItems(source));
        }

        // ── `imp` is a presence test ──────────────────────────────────────────

        [Test]
        public void ImpPresent_IsImportantRegardlessOfValue()
        {
            // `if(_loc6_.@imp.length()) { _loc3_ = 2; }` (Interact.as:1987-1991) — the VALUE is never read.
            // The boss's own row is `imp="1"`, but `imp="0"` and `imp=""` are the same branch.
            foreach (string value in new[] { "1", "0", "", "anything" })
            {
                List<LootRoller.PlacedLootItem> result =
                    UnitLootRules.PlacedItems(new List<MapObjectItemData> { Row("key_sewer", ("imp", value)) });

                Assert.IsTrue(result[0].Important,
                    "imp='" + value + "' must read as present.");
            }
        }

        [Test]
        public void ImpAbsent_IsNotImportant()
        {
            List<LootRoller.PlacedLootItem> result =
                UnitLootRules.PlacedItems(new List<MapObjectItemData> { Row("alc1") });

            Assert.IsFalse(result[0].Important);
        }

        // ── `kol` is a presence test, and its fallback is 1 not 0 ─────────────

        [Test]
        public void KolAbsent_LeavesItNullSoTheRollerUsesOne()
        {
            // `if(@kol.length()) { kol = int(@kol) } else { kol = 1 }` (Interact.as:1979-1986). The roller
            // owns the 1; a null here is the honest "AS3 had the literal".
            List<LootRoller.PlacedLootItem> result =
                UnitLootRules.PlacedItems(new List<MapObjectItemData> { Row("alc1") });

            Assert.IsNull(result[0].Kol);
        }

        [Test]
        public void KolPresent_IsParsed()
        {
            List<LootRoller.PlacedLootItem> result =
                UnitLootRules.PlacedItems(new List<MapObjectItemData> { Row("caps", ("kol", "37")) });

            Assert.AreEqual(37, result[0].Kol);
        }

        [Test]
        public void KolPresentButNotANumber_IsZeroNotOne()
        {
            // The trap this whole fixture exists for. AS3 assigns `int("x")` into an int field, which is
            // NaN, and `Interact.as:1982` hands that NaN to `lootId` — a drop count that is not a number.
            // The port has no NaN in an int?, so the honest reading of "present but unparsable" is the
            // coercion's result, not the absent case's 1.
            List<LootRoller.PlacedLootItem> result =
                UnitLootRules.PlacedItems(new List<MapObjectItemData> { Row("caps", ("kol", "many")) });

            Assert.AreEqual(0, result[0].Kol);
        }

        [Test]
        public void KolPresentButEmpty_IsZeroNotOne()
        {
            List<LootRoller.PlacedLootItem> result =
                UnitLootRules.PlacedItems(new List<MapObjectItemData> { Row("caps", ("kol", "")) });

            Assert.AreEqual(0, result[0].Kol);
        }

        // ── Order is preserved, because AS3 drops in document order ───────────

        [Test]
        public void RowsKeepTheirDocumentOrder()
        {
            // `for each(_loc6_ in this.xml.item)` (Interact.as:1975) — no sorting, no dedup.
            List<MapObjectItemData> source = new List<MapObjectItemData>
            {
                Row("alc1"),
                Row("key_sewer", ("imp", "1")),
                Row("pot1", ("kol", "3")),
                Row("alc1"),
            };

            List<LootRoller.PlacedLootItem> result = UnitLootRules.PlacedItems(source);

            Assert.AreEqual(4, result.Count);
            Assert.AreEqual("alc1", result[0].Id);
            Assert.IsFalse(result[0].Important);
            Assert.IsTrue(result[1].Important);
            Assert.AreEqual("pot1", result[2].Id);
            Assert.AreEqual(3, result[2].Kol);
            Assert.AreEqual("alc1", result[3].Id);
        }

        [Test]
        public void ABossRow_ProjectsToWhatTheOracleDescribes()
        {
            // The shipped `rooms_nio/bossraider.asset:170-174` row, verbatim:
            //   items:
            //   - id: key_sewer
            //     attributes: [{id, key_sewer}, {imp, 1}]
            var boss = new MapObjectItemData { id = "key_sewer" };
            boss.attributes.Add(new MapObjectAttributeData { key = "id", value = "key_sewer" });
            boss.attributes.Add(new MapObjectAttributeData { key = "imp", value = "1" });

            List<LootRoller.PlacedLootItem> result =
                UnitLootRules.PlacedItems(new List<MapObjectItemData> { boss });

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("key_sewer", result[0].Id);
            Assert.IsNull(result[0].Kol, "no @kol, so the count is the roller's 1");
            Assert.IsTrue(result[0].Important, "@imp present, so drop-mode 2 — exempt from the run limit");
        }

        [Test]
        public void AMixedList_OnlyCarriesItsOwnAttributes()
        {
            // Attributes are read per row, never inherited from the row before it. The second row
            // authors neither `kol` nor `imp`, so its `Kol` is null (absent) — and crucially NOT the
            // 50 of the row above it.
            List<MapObjectItemData> source = new List<MapObjectItemData>
            {
                Row("caps", ("kol", "50"), ("imp", "1")),
                Row("alc1"),
            };

            List<LootRoller.PlacedLootItem> result = UnitLootRules.PlacedItems(source);

            Assert.AreEqual(50, result[0].Kol);
            Assert.IsTrue(result[0].Important);
            Assert.IsNull(result[1].Kol, "no @kol of its own, so null — not the previous row's 50");
            Assert.IsFalse(result[1].Important, "not imp of its own, so not important");
        }
    }
}
