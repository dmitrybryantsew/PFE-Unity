using System.Collections.Generic;
using NUnit.Framework;
using PFE.Data.Definitions;
using PFE.Systems.Inventory;

namespace PFE.Tests.EditMode.Systems.Inventory
{
    /// <summary>
    /// Pins the F2 pickers' filter. Every "this is excluded" assertion is paired with a positive control,
    /// because a filter that excludes <i>everything</i> satisfies every negative assertion in the file —
    /// and an empty picker is the failure that reads as "the registry is empty" rather than "the filter
    /// is wrong".
    /// </summary>
    [TestFixture]
    public class InventoryItemPickerTests
    {
        // A hand-written table instead of real ItemDefinitions: ItemDefinition is a ScriptableObject and
        // needs the engine to construct, which is why InventoryItemPicker takes a type lookup rather than
        // a dictionary of definitions. This is the payoff — the whole filter runs offline.
        private static readonly Dictionary<string, ItemType> Types = new Dictionary<string, ItemType>
        {
            ["kombu"]   = ItemType.Medical,     // Aid
            ["med"]     = ItemType.Chems,       // Aid
            ["book1"]   = ItemType.Book,        // Aid
            ["sphera1"] = ItemType.Sphera,      // Aid
            ["stuff1"]  = ItemType.Misc,        // Misc
            ["compa"]   = ItemType.Component,   // Misc
            ["armor1"]  = ItemType.Equipment,   // Misc (Equipment lands on Misc — see InventoryPageRules)
            ["ammo762"] = ItemType.Ammo,        // Ammo
        };

        private static readonly Dictionary<string, string> Labels = new Dictionary<string, string>
        {
            ["kombu"] = "Kombucha",
            ["med"]   = "Stimpak",
            ["book1"] = "Torn Book",
            ["stuff1"] = "Scrap",
            ["ammo762"] = "7.62 Round",
        };

        private static ItemType? TypeOf(string id)
            => Types.TryGetValue(id, out ItemType t) ? t : (ItemType?)null;

        private static string LabelOf(string id)
            => Labels.TryGetValue(id, out string l) ? l : null;

        private static List<string> AllIds()
            => new List<string> { "kombu", "med", "book1", "sphera1", "stuff1", "compa", "armor1", "ammo762" };

        private static List<string> Filter(InventoryPage page, string search = null,
                                           List<string> ids = null,
                                           System.Func<string, string> labelOf = null)
            => InventoryItemPicker.Filter(ids ?? AllIds(), TypeOf, labelOf ?? LabelOf, page, search);

        // ── The page filter ───────────────────────────────────────────────────

        [Test]
        public void PageFilter_KeepsOnlyThatPagesTypes()
        {
            CollectionAssert.AreEqual(
                new[] { "kombu", "med", "book1", "sphera1" },
                Filter(InventoryPage.Aid));
        }

        [Test]
        public void Control_AnAmmoIdIsAbsentFromTheAidPage()
        {
            // The absent half of the pair above: without this, "keeps only that page" is satisfied by a
            // filter that returns the same list for every page.
            //
            // ARGUMENT ORDER IS (collection, item) — the project's convention, and the opposite of what
            // NUnit's documentation implies. Written first as DoesNotContain("ammo762", list) and it
            // PASSED against a mutant that put ammo762 on every page: that order is silently VACUOUS, so
            // it looks like a control and controls nothing. Caught only by mutating the filter.
            CollectionAssert.DoesNotContain(Filter(InventoryPage.Aid), "ammo762");
            CollectionAssert.Contains(Filter(InventoryPage.Ammo), "ammo762");
        }

        [Test]
        public void EquipmentLandsOnTheMiscPage()
        {
            // ItemType.Equipment is the collapsed AS3 `e`/`equip` pair, and InventoryPageRules puts it on
            // Misc. Pinned here because the picker is where a user will notice it.
            CollectionAssert.Contains(Filter(InventoryPage.Misc), "armor1");
            CollectionAssert.DoesNotContain(Filter(InventoryPage.Aid), "armor1");
        }

        [Test]
        public void UnknownId_IsDroppedRatherThanGuessedOntoAPage()
        {
            // `weird` has no ItemDefinition. Defaulting it to Misc would make a missing asset look like a
            // working row whose Add then fails one layer away from the cause.
            var ids = new List<string> { "kombu", "weird" };

            List<string> misc = Filter(InventoryPage.Misc, null, ids);
            CollectionAssert.DoesNotContain(misc, "weird");   // (collection, item) — see the note above

            // Positive control: the filter is not simply returning nothing.
            CollectionAssert.Contains(Filter(InventoryPage.Aid, null, ids), "kombu");
        }

        [Test]
        public void NullTypeResolver_ExcludesEverything()
        {
            // Stated behaviour, not an accident: with no way to type an id, nothing can be placed on a
            // page. A caller that forgot to wire the lookup gets an empty picker, which is honest.
            Assert.AreEqual(0,
                InventoryItemPicker.Filter(AllIds(), null, LabelOf, InventoryPage.Aid, null).Count);
        }

        // ── The search box ────────────────────────────────────────────────────

        [Test]
        public void Search_MatchesTheId()
        {
            CollectionAssert.AreEqual(new[] { "book1" }, Filter(InventoryPage.Aid, "book"));
        }

        [Test]
        public void Search_MatchesTheLabel_WhenTheLabelDiffersFromTheId()
        {
            // "Stimpak" is the display name of `med`, and shares no substring with the id — so this can
            // only pass if the label is searched too.
            CollectionAssert.AreEqual(new[] { "med" }, Filter(InventoryPage.Aid, "Stimpak"));
        }

        [Test]
        public void Search_IsCaseInsensitive()
        {
            // The AllData ids are lowercase; a case-sensitive compare would make "Kombu" return nothing,
            // which reads exactly like "no such item".
            CollectionAssert.AreEqual(Filter(InventoryPage.Aid, "kombu"),
                                      Filter(InventoryPage.Aid, "KOMBU"));
            CollectionAssert.Contains(Filter(InventoryPage.Aid, "KOMBU"), "kombu");
        }

        [Test]
        public void EmptySearch_KeepsEveryIdOnThePage()
        {
            CollectionAssert.AreEqual(Filter(InventoryPage.Aid), Filter(InventoryPage.Aid, ""));
        }

        [Test]
        public void WhitespaceOnlySearch_IsTreatedAsEmpty_NotAsAFailedMatch()
        {
            // A stray space typed into the box must not look like "no such item".
            CollectionAssert.AreEqual(Filter(InventoryPage.Aid), Filter(InventoryPage.Aid, "   "));
        }

        [Test]
        public void NullLabelResolver_StillMatchesOnTheId()
        {
            CollectionAssert.AreEqual(new[] { "book1" },
                InventoryItemPicker.Filter(AllIds(), TypeOf, null, InventoryPage.Aid, "book"));
        }

        [Test]
        public void SearchStillRespectsThePage()
        {
            // "1" appears in ids on two different pages, so the same search text must not leak rows
            // across pages. (Written first as `["book1"]` and it FAILED — `sphera1` matches too. Kept as
            // the corrected pair because the mistake is the point: a search box is where you stop reading
            // the ids and start trusting the filter.)
            CollectionAssert.AreEqual(new[] { "book1", "sphera1" }, Filter(InventoryPage.Aid, "1"));
            CollectionAssert.AreEqual(new[] { "stuff1", "armor1" }, Filter(InventoryPage.Misc, "1"));

            // Absent control: no Ammo id or label contains "1", so a filter that ignored the search would
            // return `ammo762` here.
            Assert.AreEqual(0, Filter(InventoryPage.Ammo, "1").Count);
        }

        // ── Shape of the result ───────────────────────────────────────────────

        [Test]
        public void NullCandidateList_YieldsAnEmptyList_NotNull()
        {
            List<string> result = InventoryItemPicker.Filter(
                null, TypeOf, LabelOf, InventoryPage.Aid, null);

            Assert.IsNotNull(result);
            Assert.AreEqual(0, result.Count);
        }

        [Test]
        public void NullAndEmptyIdsAreSkipped()
        {
            var ids = new List<string> { null, string.Empty, "kombu" };
            CollectionAssert.AreEqual(new[] { "kombu" }, Filter(InventoryPage.Aid, null, ids));
        }

        [Test]
        public void InputOrderIsPreserved_NotReSorted()
        {
            // The caller passes PlayerInventory.AvailableItemIds, already ordinal-sorted. Sorting again
            // here would be a second silent source of truth about ordering — so a later "tidy-up" that
            // adds a .Sort() must fail this test.
            var ids = new List<string> { "sphera1", "kombu", "book1", "med" };
            CollectionAssert.AreEqual(ids, Filter(InventoryPage.Aid, null, ids));
        }

        [Test]
        public void FilterNeverInventsAnId()
        {
            List<string> page = Filter(InventoryPage.Aid);
            foreach (string id in page)
            {
                CollectionAssert.Contains(AllIds(), id, $"filter produced '{id}', which was not a candidate");
            }

            // Control: an id that exists in the table but was not passed in must not appear.
            var ids = new List<string> { "kombu" };
            CollectionAssert.DoesNotContain(Filter(InventoryPage.Aid, null, ids), "med");
        }
    }
}
