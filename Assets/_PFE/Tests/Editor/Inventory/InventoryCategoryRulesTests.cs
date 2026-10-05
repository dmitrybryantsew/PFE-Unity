using NUnit.Framework;
using PFE.Data.Definitions;
using PFE.Systems.Inventory;

namespace PFE.Tests.EditMode.Systems.Inventory
{
    /// <summary>
    /// Pins AS3's <c>invCat</c> derivation (<c>fe/serv/Item.as:83,271-293</c>).
    ///
    /// <para><b>Every rule has a positive control</b>, because a suite that only asserts "this is
    /// rejected" cannot tell "correctly rejects" from "rejects everything". The two rules that interact —
    /// the usable rule and the explicit override — are pinned in both orders, since the whole point of
    /// the oracle is that the override is applied <i>after</i> the derivation.</para>
    ///
    /// <para><b>The numbers in the comments are measured, not assumed.</b> They come from a census of
    /// <c>AllData.as</c> over all 500 item rows: <c>invCat 1 = 87</c>, <c>2 = 71</c>, <c>3 = 342</c>.</para>
    /// </summary>
    [TestFixture]
    public class InventoryCategoryRulesTests
    {
        // ── The default ───────────────────────────────────────────────────────

        [Test]
        public void NoTipAndNoUses_FallsToStuff()
        {
            Assert.AreEqual(InventoryCategoryRules.Stuff,
                InventoryCategoryRules.Resolve(null, 0, false, InventoryCategoryRules.Stuff));
        }

        [Test]
        public void AnUnrecognisedTip_StillFallsToStuff()
        {
            // 342 of the 500 rows land here, so it is the common case, not an edge.
            Assert.AreEqual(InventoryCategoryRules.Stuff,
                InventoryCategoryRules.Resolve("scheme", 0, false, InventoryCategoryRules.Stuff));
        }

        // ── The ammo/explosive rule ───────────────────────────────────────────

        [Test]
        public void AmmoTip_IsAmmoCategory()
        {
            Assert.AreEqual(InventoryCategoryRules.Ammo,
                InventoryCategoryRules.Resolve("a", 0, false, InventoryCategoryRules.Stuff));
        }

        [Test]
        public void ExplosiveTip_IsAlsoAmmoCategory()
        {
            // `e` is ammo-category in AS3 even though it is not ammunition. 22 rows; `a`+`e` = 71 = the
            // measured invCat 2 total.
            Assert.AreEqual(InventoryCategoryRules.Ammo,
                InventoryCategoryRules.Resolve("e", 0, false, InventoryCategoryRules.Stuff));
        }

        [Test]
        public void Control_EquipTip_IsNotAmmoCategory()
        {
            // The absent control for the pair above: `equip` must NOT take the ammo branch, or the two
            // tests would pass for a rule that puts everything in Ammo. This is also the pair the port
            // cannot distinguish — both collapse to ItemType.Equipment on import.
            Assert.AreNotEqual(InventoryCategoryRules.Ammo,
                InventoryCategoryRules.Resolve("equip", 0, false, InventoryCategoryRules.Stuff));
        }

        // ── The usable rule ───────────────────────────────────────────────────

        [Test]
        public void UsableTipWithUses_IsUsableCategory()
        {
            Assert.AreEqual(InventoryCategoryRules.Usable,
                InventoryCategoryRules.Resolve("pot", 1, false, InventoryCategoryRules.Stuff));
        }

        [Test]
        public void UsableTipWithZeroUses_IsNotUsable()
        {
            // `@us` present but 0 must not qualify — the oracle tests `> 0`, not "the attribute exists".
            // Every @us row in the current data is > 0, so this case is synthetic but load-bearing.
            Assert.AreEqual(InventoryCategoryRules.Stuff,
                InventoryCategoryRules.Resolve("pot", 0, false, InventoryCategoryRules.Stuff));
        }

        [TestCase("food")]
        [TestCase("book")]
        [TestCase("eda")]
        public void ExemptTips_AreNotUsableEvenWithUses(string exemptTip)
        {
            // 57 food + 14 book rows carry @us > 0 and are excluded by the oracle's own tip test — so
            // without this the distribution would be 87 + 71 = 158 usable, not 87.
            Assert.AreEqual(InventoryCategoryRules.Stuff,
                InventoryCategoryRules.Resolve(exemptTip, 5, false, InventoryCategoryRules.Stuff));
        }

        [Test]
        public void Control_ANonExemptUsableTip_IsUsable()
        {
            // The positive control for the exempt set: proves the loop actually matches tips, rather than
            // IsUsableExemptTip returning true for everything.
            Assert.IsTrue(InventoryCategoryRules.IsUsableExemptTip("food"));
            Assert.IsFalse(InventoryCategoryRules.IsUsableExemptTip("med"));
            Assert.AreEqual(InventoryCategoryRules.Usable,
                InventoryCategoryRules.Resolve("med", 1, false, InventoryCategoryRules.Stuff));
        }

        // ── The explicit override, which is applied LAST ──────────────────────

        [Test]
        public void ExplicitOverride_BeatsTheUsableRule()
        {
            // The 9 `equip` rows that carry @invcat='3' while having @us > 0. This is the case that makes
            // the override non-redundant: every recorded @invcat value equals the DEFAULT 3, which is
            // exactly why treating it as a no-op is the tempting mistake.
            Assert.AreEqual(InventoryCategoryRules.Stuff,
                InventoryCategoryRules.Resolve("equip", 15, true, 3));
        }

        [Test]
        public void ExplicitOverride_BeatsTheAmmoRule()
        {
            Assert.AreEqual(InventoryCategoryRules.Usable,
                InventoryCategoryRules.Resolve("a", 0, true, InventoryCategoryRules.Usable));
        }

        [Test]
        public void ExplicitOverride_CanReachTheUntrackedCategory()
        {
            // Slot 0 is never produced by the derivation, but @invcat='0' can reach it, so the constant
            // is not dead.
            Assert.AreEqual(InventoryCategoryRules.NotTracked,
                InventoryCategoryRules.Resolve("pot", 9, true, 0));
        }

        [Test]
        public void OverrideFlagOff_IgnoresTheValue()
        {
            // Guards the `hasExplicitCategory` flag: a caller that passes a stale value without the flag
            // must get the derived answer, not the stale one.
            Assert.AreEqual(InventoryCategoryRules.Usable,
                InventoryCategoryRules.Resolve("pot", 3, false, InventoryCategoryRules.Stuff));
        }

        // ── The tip classifiers, directly ─────────────────────────────────────

        [Test]
        public void TipClassifiers_MatchOnlyTheirOwnSet()
        {
            Assert.IsTrue(InventoryCategoryRules.IsAmmoOrExplosiveTip("a"));
            Assert.IsTrue(InventoryCategoryRules.IsAmmoOrExplosiveTip("e"));
            Assert.IsFalse(InventoryCategoryRules.IsAmmoOrExplosiveTip("equip"));
            Assert.IsFalse(InventoryCategoryRules.IsAmmoOrExplosiveTip(""));

            Assert.IsTrue(InventoryCategoryRules.IsUsableExemptTip("book"));
            Assert.IsFalse(InventoryCategoryRules.IsUsableExemptTip("scheme"));
            Assert.IsFalse(InventoryCategoryRules.IsUsableExemptTip(null));
        }

        // ── The collapsed-enum approximation, stated rather than hidden ───────

        [Test]
        public void AmmoIsTheOneTypeTheCollapsedEnumPreserves()
        {
            Assert.AreEqual(InventoryCategoryRules.Ammo,
                InventoryCategoryRules.ResolveFromItemType(ItemType.Ammo));
        }

        [Test]
        public void CollapsedEquipment_DeliberatelyFallsToStuff()
        {
            // ItemType.Equipment merges AS3's `e` (category 2, 22 rows) with `equip` (category 1 for 7 and
            // 3 for 9). No function of ItemType can separate them, so the approximation refuses to guess.
            // If someone later "improves" this to return Ammo, this test should be what stops them until
            // `tip` is actually imported.
            Assert.AreEqual(InventoryCategoryRules.Stuff,
                InventoryCategoryRules.ResolveFromItemType(ItemType.Equipment));
        }

        [Test]
        public void EveryCategoryIsWithinTheMassArray()
        {
            // categoryMass is a float[4]; a category outside 0..3 would be silently dropped by
            // CalculateMass, so the bound is asserted rather than assumed.
            foreach (string tip in new[] { "a", "e", "pot", "food", "equip", "scheme", null })
            {
                int cat = InventoryCategoryRules.Resolve(tip, 1, false, InventoryCategoryRules.Stuff);
                Assert.GreaterOrEqual(cat, 0, $"tip '{tip}' produced {cat}");
                Assert.LessOrEqual(cat, InventoryCategoryRules.MaxCategory, $"tip '{tip}' produced {cat}");
            }
        }
    }

    /// <summary>
    /// Pins the page partition used by the F2 inventory tab's sub-tabs, and the AS3 table it derives from.
    /// </summary>
    [TestFixture]
    public class InventoryPageRulesTests
    {
        [Test]
        public void WeaponsAndArmorAreNotItemBacked()
        {
            // The caller must not filter the item dictionary by these pages, or the list would be empty
            // and look like a broken tab rather than a wrong filter.
            Assert.IsFalse(InventoryPageRules.IsItemBacked(InventoryPage.Weapons));
            Assert.IsFalse(InventoryPageRules.IsItemBacked(InventoryPage.Armor));

            Assert.IsTrue(InventoryPageRules.IsItemBacked(InventoryPage.Aid));
            Assert.IsTrue(InventoryPageRules.IsItemBacked(InventoryPage.Misc));
            Assert.IsTrue(InventoryPageRules.IsItemBacked(InventoryPage.Ammo));
        }

        [Test]
        public void AmmoType_LandsOnTheAmmoPage()
        {
            Assert.AreEqual(InventoryPage.Ammo, InventoryPageRules.PageOf(ItemType.Ammo));
        }

        [TestCase(ItemType.Medical)]
        [TestCase(ItemType.Chems)]
        [TestCase(ItemType.Book)]
        [TestCase(ItemType.Sphera)]
        [TestCase(ItemType.Spell)]
        public void AidTypes_LandOnTheAidPage(ItemType type)
        {
            Assert.AreEqual(InventoryPage.Aid, InventoryPageRules.PageOf(type));
        }

        [TestCase(ItemType.Misc)]
        [TestCase(ItemType.Component)]
        [TestCase(ItemType.Implant)]
        [TestCase(ItemType.Key)]
        [TestCase(ItemType.Quest)]
        [TestCase(ItemType.Valuable)]
        [TestCase(ItemType.Equipment)]
        public void RemainingTypes_LandOnTheMiscPage(ItemType type)
        {
            Assert.AreEqual(InventoryPage.Misc, InventoryPageRules.PageOf(type));
        }

        [Test]
        public void Control_AMiscTypeIsNotOnTheAidPage()
        {
            // Absent control: proves the Contains/PageOf pair discriminates, rather than every type
            // matching every page.
            Assert.IsFalse(InventoryPageRules.Contains(InventoryPage.Aid, ItemType.Component));
            Assert.IsTrue(InventoryPageRules.Contains(InventoryPage.Misc, ItemType.Component));
        }

        [Test]
        public void EveryItemTypeBelongsToExactlyOneItemBackedPage()
        {
            // The partition property, asserted over the whole enum: an ItemType added later and left out
            // of PageOf would default to Misc and become invisible on the other pages — this is what
            // catches that, because it does not enumerate the types by hand.
            foreach (ItemType type in System.Enum.GetValues(typeof(ItemType)))
            {
                int matches = 0;
                for (int p = 0; p < InventoryPageRules.PageCount; p++)
                {
                    if (InventoryPageRules.Contains((InventoryPage)p, type)) matches++;
                }

                Assert.AreEqual(1, matches, $"ItemType.{type} matched {matches} pages, expected exactly 1");
            }
        }

        [Test]
        public void PageCountMatchesTheOracleAndEveryPageHasAName()
        {
            Assert.AreEqual(5, InventoryPageRules.PageCount);

            for (int p = 0; p < InventoryPageRules.PageCount; p++)
            {
                string name = InventoryPageRules.Name((InventoryPage)p);
                Assert.IsFalse(string.IsNullOrEmpty(name), $"page {p} has no name");
                Assert.AreNotEqual("?", name, $"page {p} is unnamed");
            }

            // Control: an out-of-range page must NOT accidentally get a real name.
            Assert.AreEqual("?", InventoryPageRules.Name((InventoryPage)99));
        }
    }
}
