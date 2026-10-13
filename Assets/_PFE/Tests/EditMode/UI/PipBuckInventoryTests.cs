using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using PFE.Systems.Inventory;
using PFE.UI.Common;
using PFE.UI.PipBuck;
using UnityEngine;

namespace PFE.Tests.EditMode.UI
{
    /// <summary>
    /// Pins the PipBuck's inventory page (item I9): the filtering, sorting and column rules of
    /// <c>PipPageInv.setSubPages</c> / <c>setStatItem</c> that <see cref="PipInventoryList"/> ports,
    /// plus the geometry <see cref="PipBuckLayout"/> added for the page header and the corrections
    /// the SWF read forced on the device's tab bar.
    ///
    /// <para><b>Why these particular rules and not the whole file.</b> The page is mostly plumbing:
    /// it builds 18 row clips and writes six strings into each. The plumbing can only be checked by
    /// looking at the screen, and the editor here is owner-only. What <i>can</i> be checked from a
    /// shell is the part that branches — which entries are listed, in what order, and which column
    /// each value lands in. Every assertion below is on a branch, and each one names the Oracle line
    /// it is standing on.</para>
    ///
    /// <para><b>The reader is not under test.</b> <c>PlayerPipInventorySource</c> needs a live
    /// <c>GameInventory</c>, which needs a <c>ScriptableObject</c> definition, which needs the editor.
    /// It is deliberately thin for that reason: it fills a flat <see cref="PipInventoryEntry"/> and
    /// everything with a branch in it lives here. The two decisions it does own — skipping
    /// <c>respect == 3</c> weapons and zeroing mass outside hard mode — are recorded on the reader
    /// itself, and are the two things this fixture cannot see.</para>
    ///
    /// <para><b>Every expectation is watched failing.</b> These tests were run against a mutated
    /// <c>PipInventoryList.cs</c> — the hidden gate ignored, the ammo-page guard dropped, the value
    /// suffix forced on, the mass gate half-removed, the name tiebreak removed — and each mutation
    /// killed exactly the test named in the driver's prediction
    /// (<c>.workbuddy-ai/tools/agentverify/wall/mutate_pipinv.py</c>). A green run of a probe nobody
    /// has seen fail proves nothing, which is the recurring lesson in this project.</para>
    /// </summary>
    [TestFixture]
    public class PipBuckInventoryTests
    {
        // ── Builders ─────────────────────────────────────────────────────────
        //
        // PipInventoryEntry is a struct of public fields (not init properties) because this project
        // targets .NET Standard 2.1, where `init` does not compile — see the file's own remarks.

        private static PipInventoryEntry Entry(string id, PipInventoryKind kind, int subPage)
        {
            return new PipInventoryEntry { Id = id, Name = id, Kind = kind, SubPage = subPage };
        }

        private static PipInventoryEntry Item(string id, string categoryKey)
        {
            return new PipInventoryEntry
            {
                Id = id,
                Name = id,
                Kind = PipInventoryKind.Item,
                SubPage = PipInventoryList.AidSubPage,
                CategoryKey = categoryKey,
            };
        }

        private static string[] Ids(IEnumerable<PipInventoryRow> rows)
            => rows.Select(r => r.Id).ToArray();

        // ── Sub-page ↔ page ──────────────────────────────────────────────────
        //
        // The Oracle's page2 is 1-based; the port's InventoryPage is 0-based. Getting this wrong
        // lists armour under Aid and reads like a data problem.

        [Test]
        public void SubPages_AreTheOraclesOneBasedPage2Values()
        {
            // PipPageInv.as:33 `tips`, :64, :178, :215, :253.
            Assert.AreEqual(1, PipInventoryList.WeaponsSubPage);
            Assert.AreEqual(2, PipInventoryList.ArmorSubPage);
            Assert.AreEqual(3, PipInventoryList.AidSubPage);
            Assert.AreEqual(4, PipInventoryList.MiscSubPage);
            Assert.AreEqual(5, PipInventoryList.AmmoSubPage);
            Assert.AreEqual(5, PipInventoryList.SubPageCount);
            Assert.AreEqual(InventoryPageRules.PageCount, PipInventoryList.SubPageCount,
                "the sub-page table and the port's page enum must not drift apart");
        }

        [Test]
        public void PageForSubPage_MapsOneBasedToTheZeroBasedEnum()
        {
            Assert.AreEqual(InventoryPage.Weapons, PipInventoryList.PageForSubPage(1));
            Assert.AreEqual(InventoryPage.Armor, PipInventoryList.PageForSubPage(2));
            Assert.AreEqual(InventoryPage.Aid, PipInventoryList.PageForSubPage(3));
            Assert.AreEqual(InventoryPage.Misc, PipInventoryList.PageForSubPage(4));
            Assert.AreEqual(InventoryPage.Ammo, PipInventoryList.PageForSubPage(5));
        }

        [Test]
        public void SubPageFor_RoundTripsPageForSubPage()
        {
            for (int sub = 1; sub <= PipInventoryList.SubPageCount; sub++)
            {
                Assert.AreEqual(sub, PipInventoryList.SubPageFor(PipInventoryList.PageForSubPage(sub)));
            }
        }

        [Test]
        public void PageForSubPage_RefusesZeroAndSix()
        {
            // Silently clamping would put a sub-page on the wrong tab; the Oracle's table has five rows.
            Assert.Throws<ArgumentOutOfRangeException>(() => PipInventoryList.PageForSubPage(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => PipInventoryList.PageForSubPage(6));
        }

        [Test]
        public void SubPageName_UsesThePagesOwnName()
        {
            Assert.AreEqual("Weapons", PipInventoryList.SubPageName(1));
            Assert.AreEqual("Armor", PipInventoryList.SubPageName(2));
            Assert.AreEqual("Aid", PipInventoryList.SubPageName(3));
            Assert.AreEqual("Misc", PipInventoryList.SubPageName(4));
            Assert.AreEqual("Ammo", PipInventoryList.SubPageName(5));
            Assert.AreEqual("?", PipInventoryList.SubPageName(0), "an out-of-range sub-page has no caption");
        }

        // ── Headers ──────────────────────────────────────────────────────────

        [Test]
        public void HeaderKeys_AreTheOraclesUnExtractedPipTextKeys()
        {
            // PipPageInv.as:68-73, :180-185, :218-226. Recorded rather than invented: these keys exist
            // in the Oracle's Res.pipText table, which was never extracted, and the gap is greppable.
            CollectionAssert.AreEqual(new[] { "ii1", "ii2", "ii3", "", "", "ii4" },
                PipInventoryList.HeaderKeysFor(PipInventoryList.WeaponsSubPage));
            CollectionAssert.AreEqual(new[] { "ii1", "ii2", "ii3", "", "", "" },
                PipInventoryList.HeaderKeysFor(PipInventoryList.ArmorSubPage));
            CollectionAssert.AreEqual(new[] { "ii1", "ii2", "ii5", "", "ii8", "ii6" },
                PipInventoryList.HeaderKeysFor(PipInventoryList.AmmoSubPage));
        }

        [Test]
        public void HeaderKeys_TheThreeItemPagesShareOneShape()
        {
            // Aid, Misc and Ammo differ only in which items they list, never in their columns.
            CollectionAssert.AreEqual(PipInventoryList.HeaderKeysFor(5), PipInventoryList.HeaderKeysFor(3));
            CollectionAssert.AreEqual(PipInventoryList.HeaderKeysFor(5), PipInventoryList.HeaderKeysFor(4));
        }

        [Test]
        public void HeaderKeys_ArmourHasNoAmmoTipColumn()
        {
            string[] armour = PipInventoryList.HeaderKeysFor(PipInventoryList.ArmorSubPage);
            Assert.AreEqual(string.Empty, armour[5], "PipPageInv.as:180-185 writes no ammotip key");
        }

        [Test]
        public void Header_EverySubPageHasSixColumns()
        {
            // The row clip has six text fields; a header of a different length would misalign by one.
            for (int sub = 1; sub <= PipInventoryList.SubPageCount; sub++)
            {
                Assert.AreEqual(6, PipInventoryList.HeaderFor(sub).ToArray().Length, $"sub-page {sub}");
            }
        }

        [Test]
        public void Header_CaptionsFollowTheColumnShapesTheOracleUses()
        {
            Assert.AreEqual("COND", PipInventoryList.HeaderFor(1).Value);
            Assert.AreEqual("ROUNDS", PipInventoryList.HeaderFor(1).Extra);
            Assert.AreEqual("AMMO", PipInventoryList.HeaderFor(1).ExtraTip);

            Assert.AreEqual("COND", PipInventoryList.HeaderFor(2).Value);
            Assert.AreEqual(string.Empty, PipInventoryList.HeaderFor(2).ExtraTip);

            Assert.AreEqual("COUNT", PipInventoryList.HeaderFor(3).Value);
            Assert.AreEqual("COUNT", PipInventoryList.HeaderFor(5).Value);
            Assert.AreEqual("MASS", PipInventoryList.HeaderFor(5).Mass);
            Assert.AreEqual("TYPE", PipInventoryList.HeaderFor(5).ExtraTip);
        }

        // ── The filter ───────────────────────────────────────────────────────

        [Test]
        public void RowsFor_ListsOnlyTheRequestedSubPage()
        {
            var entries = new List<PipInventoryEntry>
            {
                Entry("w1", PipInventoryKind.Weapon, 1),
                Entry("a1", PipInventoryKind.Armor, 2),
                Entry("i1", PipInventoryKind.Item, 3),
            };

            List<PipInventoryRow> rows = PipInventoryList.RowsFor(entries, PipInventoryList.ArmorSubPage, false);

            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual("a1", rows[0].Id);
        }

        [Test]
        public void RowsFor_HiddenWeaponIsDroppedUnlessShowHidden()
        {
            // PipPageInv.as:92-95 — `respect == 1` is stored, not carried, and is listed only while
            // pip.showHidden is on. The parameter is the whole point of the test: dropping the entry
            // unconditionally is the bug a reader would write if it forgot the flag existed.
            var entries = new List<PipInventoryEntry>
            {
                new PipInventoryEntry
                {
                    Id = "carried", Name = "Carried", Kind = PipInventoryKind.Weapon, SubPage = 1,
                },
                new PipInventoryEntry
                {
                    Id = "stored", Name = "Stored", Kind = PipInventoryKind.Weapon, SubPage = 1,
                    Hidden = true,
                },
            };

            List<PipInventoryRow> hiddenOff = PipInventoryList.RowsFor(entries, 1, false);
            CollectionAssert.AreEqual(new[] { "carried" }, Ids(hiddenOff));

            List<PipInventoryRow> hiddenOn = PipInventoryList.RowsFor(entries, 1, true);
            CollectionAssert.AreEqual(new[] { "carried", "stored" }, Ids(hiddenOn));
        }

        [Test]
        public void RowsFor_ShowHiddenDoesNotChangeWhichOrdinaryEntriesAppear()
        {
            // The flag must only ever add rows. If it also reordered or filtered the visible ones,
            // toggling it would shuffle the whole list.
            var entries = new List<PipInventoryEntry>
            {
                Item("alpha", "food"),
                Item("bravo", "food"),
            };

            List<PipInventoryRow> off = PipInventoryList.RowsFor(entries, PipInventoryList.AidSubPage, false);
            List<PipInventoryRow> on = PipInventoryList.RowsFor(entries, PipInventoryList.AidSubPage, true);

            CollectionAssert.AreEqual(Ids(off), Ids(on));
        }

        [Test]
        public void RowsFor_NullEntries_IsEmptyAndDoesNotThrow()
        {
            Assert.AreEqual(0, PipInventoryList.RowsFor(null, 1, false).Count);
        }

        [Test]
        public void RowsFor_RefusesASubPageOutsideOneToFive()
        {
            var entries = new List<PipInventoryEntry>();
            Assert.Throws<ArgumentOutOfRangeException>(() => PipInventoryList.RowsFor(entries, 0, false));
            Assert.Throws<ArgumentOutOfRangeException>(() => PipInventoryList.RowsFor(entries, 6, false));
        }

        // ── The sort ─────────────────────────────────────────────────────────

        [Test]
        public void RowsFor_WeaponsSortBySort1ThenSort2ThenSort3()
        {
            // PipPageInv.as:174 — sortOn = ["sort1","sort2","sort3","nazv"].
            var entries = new List<PipInventoryEntry>
            {
                new PipInventoryEntry { Id = "d", Name = "D", Kind = PipInventoryKind.Weapon, SubPage = 1, Sort1 = 2 },
                new PipInventoryEntry { Id = "b", Name = "B", Kind = PipInventoryKind.Weapon, SubPage = 1, Sort1 = 1, Sort2 = 2 },
                new PipInventoryEntry { Id = "a", Name = "A", Kind = PipInventoryKind.Weapon, SubPage = 1, Sort1 = 1, Sort2 = 1, Sort3 = 2 },
                new PipInventoryEntry { Id = "c", Name = "C", Kind = PipInventoryKind.Weapon, SubPage = 1, Sort1 = 1, Sort2 = 1, Sort3 = 1 },
            };

            List<PipInventoryRow> rows = PipInventoryList.RowsFor(entries, 1, false);

            CollectionAssert.AreEqual(new[] { "c", "a", "b", "d" }, Ids(rows));
        }

        [Test]
        public void RowsFor_ArmourSortsByTrolThenSort()
        {
            // PipPageInv.as:211 — sortOn = ["trol","sort"], where trol is CategoryKey ("armor" + tip).
            var entries = new List<PipInventoryEntry>
            {
                new PipInventoryEntry { Id = "z", Name = "Z", Kind = PipInventoryKind.Armor, SubPage = 2, CategoryKey = "armorZ", Sort1 = 0 },
                new PipInventoryEntry { Id = "a2", Name = "A2", Kind = PipInventoryKind.Armor, SubPage = 2, CategoryKey = "armorA", Sort1 = 2 },
                new PipInventoryEntry { Id = "a1", Name = "A1", Kind = PipInventoryKind.Armor, SubPage = 2, CategoryKey = "armorA", Sort1 = 1 },
            };

            List<PipInventoryRow> rows = PipInventoryList.RowsFor(entries, 2, false);

            CollectionAssert.AreEqual(new[] { "a1", "a2", "z" }, Ids(rows));
        }

        [Test]
        public void RowsFor_ItemPagesSortByCategoryThenSort2()
        {
            // PipPageInv.as:283 `sort = cat`, :303 sortOn = ["sort","sort2","nazv"].
            var entries = new List<PipInventoryEntry>
            {
                new PipInventoryEntry { Id = "b", Name = "B", Kind = PipInventoryKind.Item, SubPage = 3, CategoryKey = "food", Sort1 = 0 },
                new PipInventoryEntry { Id = "a2", Name = "A2", Kind = PipInventoryKind.Item, SubPage = 3, CategoryKey = "chems", Sort1 = 2 },
                new PipInventoryEntry { Id = "a1", Name = "A1", Kind = PipInventoryKind.Item, SubPage = 3, CategoryKey = "chems", Sort1 = 1 },
            };

            List<PipInventoryRow> rows = PipInventoryList.RowsFor(entries, 3, false);

            CollectionAssert.AreEqual(new[] { "a1", "a2", "b" }, Ids(rows));
        }

        [Test]
        public void RowsFor_LoadedAmmoFloatsToTheTopOfTheAmmoPage()
        {
            // PipPageInv.as:285-288 — the loaded round's sort key gets a "0" prefix. "b" sorts AFTER
            // "a"; "0b" sorts before it, which is the only reason the equipped round leads the list.
            var entries = new List<PipInventoryEntry>
            {
                new PipInventoryEntry { Id = "other", Name = "Other", Kind = PipInventoryKind.Item, SubPage = 5, CategoryKey = "a" },
                new PipInventoryEntry { Id = "loaded", Name = "Loaded", Kind = PipInventoryKind.Item, SubPage = 5, CategoryKey = "b", IsLoadedAmmo = true },
            };

            List<PipInventoryRow> rows = PipInventoryList.RowsFor(entries, 5, false);

            CollectionAssert.AreEqual(new[] { "loaded", "other" }, Ids(rows));
        }

        [Test]
        public void RowsFor_LoadedAmmoNudgeIsNotAppliedOffTheAmmoPage()
        {
            // The nudge is gated on page2 == 5. Without the gate a loaded round floats to the top of
            // whichever page happens to list it — a two-condition guard ported half
            // (PipPageInv.as:285), which is invisible until a player opens the wrong tab.
            var entries = new List<PipInventoryEntry>
            {
                new PipInventoryEntry { Id = "other", Name = "Other", Kind = PipInventoryKind.Item, SubPage = 4, CategoryKey = "a" },
                new PipInventoryEntry { Id = "loaded", Name = "Loaded", Kind = PipInventoryKind.Item, SubPage = 4, CategoryKey = "z", IsLoadedAmmo = true },
            };

            List<PipInventoryRow> rows = PipInventoryList.RowsFor(entries, 4, false);

            CollectionAssert.AreEqual(new[] { "other", "loaded" }, Ids(rows),
                "on the misc page the category order stands; only the ammo page floats the loaded round");
        }

        [Test]
        public void RowsFor_EqualKeysFallBackToTheNameSoTheOrderIsDeterministic()
        {
            // The Oracle's armour key list has no name term, so armours with the same trol and sort
            // come out in dictionary order. The port breaks that tie on the name deliberately: a
            // fixture that pinned the Oracle's arbitrary order would be pinning an accident.
            //
            // Three entries, written out of alphabetical order, so removing the tiebreak leaves a
            // different order rather than a coin-flip between two equal elements.
            var entries = new List<PipInventoryEntry>
            {
                new PipInventoryEntry { Id = "c", Name = "Charlie", Kind = PipInventoryKind.Armor, SubPage = 2, CategoryKey = "armorX", Sort1 = 1 },
                new PipInventoryEntry { Id = "a", Name = "Alpha", Kind = PipInventoryKind.Armor, SubPage = 2, CategoryKey = "armorX", Sort1 = 1 },
                new PipInventoryEntry { Id = "b", Name = "Bravo", Kind = PipInventoryKind.Armor, SubPage = 2, CategoryKey = "armorX", Sort1 = 1 },
            };

            List<PipInventoryRow> rows = PipInventoryList.RowsFor(entries, 2, false);

            CollectionAssert.AreEqual(new[] { "a", "b", "c" }, Ids(rows));
        }

        // ── The columns (setStatItem) ────────────────────────────────────────

        [Test]
        public void BuildRow_PercentValueIsRoundedHalfUpAndSuffixed()
        {
            // PipPageInv.as:142, :199 — Math.round(hp / maxhp * 100) + "%".
            PipInventoryRow row = PipInventoryList.BuildRow(new PipInventoryEntry
            {
                Id = "w", Name = "W", Kind = PipInventoryKind.Weapon, SubPage = 1,
                Value = 37.4f, ValueIsPercent = true,
            });
            Assert.AreEqual("37%", row.Value);

            // AS3's Math.round is half-UP; Unity's Mathf.Round is half-to-even. The two controls are
            // here so the test proves it is discriminating rather than merely agreeing by luck.
            Assert.AreEqual("1%", PipInventoryList.BuildRow(new PipInventoryEntry { Value = 0.5f, ValueIsPercent = true }).Value);
            Assert.AreEqual("3%", PipInventoryList.BuildRow(new PipInventoryEntry { Value = 2.5f, ValueIsPercent = true }).Value);
            Assert.AreEqual(0, Mathf.Round(0.5f), "control: Mathf.Round really is half-to-even");
            Assert.AreEqual(2, Mathf.Round(2.5f), "control: Mathf.Round really is half-to-even");
        }

        [Test]
        public void BuildRow_CountValueHasNoPercentSuffix()
        {
            // PipPageInv.as:461 — an item's value column is a bare count. Rendering it as "3%" would
            // read as a condition, which is the column next door on the weapon page.
            PipInventoryRow row = PipInventoryList.BuildRow(new PipInventoryEntry
            {
                Id = "i", Name = "I", Kind = PipInventoryKind.Item, SubPage = 3,
                Value = 3f, ValueIsPercent = false,
            });

            Assert.AreEqual("3", row.Value);
            Assert.IsFalse(row.Value.Contains("%"));
        }

        [Test]
        public void BuildRow_MassColumnIsCountTimesMass()
        {
            // PipPageInv.as:462-465 — `kol * mass`.
            PipInventoryRow row = PipInventoryList.BuildRow(new PipInventoryEntry
            {
                Id = "i", Name = "I", Kind = PipInventoryKind.Item, SubPage = 3,
                Mass = 0.5f, Count = 5,
            });

            Assert.AreEqual("3", row.Mass, "5 * 0.5 = 2.5, and AS3 rounds that up");
        }

        [Test]
        public void BuildRow_MassColumnIsSuppressedWhenTheItemHasNoMass()
        {
            // The Oracle gates the column on `mass`; a zero-mass item leaves the cell blank rather
            // than printing a "0" that reads as a real weight.
            PipInventoryRow row = PipInventoryList.BuildRow(new PipInventoryEntry
            {
                Id = "i", Name = "I", Kind = PipInventoryKind.Item, SubPage = 3,
                Mass = 0f, Count = 5,
            });

            Assert.AreEqual(string.Empty, row.Mass);
        }

        [Test]
        public void BuildRow_MassColumnIsSuppressedWhenTheCountIsZero()
        {
            PipInventoryRow row = PipInventoryList.BuildRow(new PipInventoryEntry
            {
                Id = "i", Name = "I", Kind = PipInventoryKind.Item, SubPage = 3,
                Mass = 2f, Count = 0,
            });

            Assert.AreEqual(string.Empty, row.Mass);
        }

        [Test]
        public void BuildRow_WeaponsAndArmourNeverUseTheMassColumn()
        {
            // The mass cell is written on the item path only (PipPageInv.as:462). A weapon instance
            // does carry a mass, so testing the value rather than the kind would print a weight in a
            // column the weapon page does not have.
            PipInventoryRow weapon = PipInventoryList.BuildRow(new PipInventoryEntry
            {
                Id = "w", Name = "W", Kind = PipInventoryKind.Weapon, SubPage = 1, Mass = 3f, Count = 1,
            });
            PipInventoryRow armor = PipInventoryList.BuildRow(new PipInventoryEntry
            {
                Id = "a", Name = "A", Kind = PipInventoryKind.Armor, SubPage = 2, Mass = 3f, Count = 1,
            });

            Assert.AreEqual(string.Empty, weapon.Mass);
            Assert.AreEqual(string.Empty, armor.Mass);
        }

        [Test]
        public void BuildRow_ItemExtraIsThePriceOnlyForValuables()
        {
            // PipPageInv.as:466-473 — the ammo column is the price, and only when @tip == "valuables".
            PipInventoryRow priced = PipInventoryList.BuildRow(new PipInventoryEntry
            {
                Id = "v", Name = "V", Kind = PipInventoryKind.Item, SubPage = 4,
                ShowPrice = true, Extra = "120",
            });
            PipInventoryRow plain = PipInventoryList.BuildRow(new PipInventoryEntry
            {
                Id = "m", Name = "M", Kind = PipInventoryKind.Item, SubPage = 4,
                ShowPrice = false, Extra = "120",
            });

            Assert.AreEqual("120", priced.Extra);
            Assert.AreEqual(string.Empty, plain.Extra, "an ordinary item shows no price");
        }

        [Test]
        public void BuildRow_WeaponExtraIsShownWithoutTheValuablesGate()
        {
            // PipPageInv.as:402-410 writes a weapon's rounds unconditionally. The valuables gate
            // belongs to the item path, so a ShowPrice-only test would blank every weapon's rounds.
            PipInventoryRow row = PipInventoryList.BuildRow(new PipInventoryEntry
            {
                Id = "w", Name = "W", Kind = PipInventoryKind.Weapon, SubPage = 1,
                ShowPrice = false, Extra = "24",
            });

            Assert.AreEqual("24", row.Extra);
        }

        [Test]
        public void BuildRow_PendingDropTakesOverTheAmmoTipColumn()
        {
            // PipPageInv.as:485-492 — while `drop` is non-zero it replaces the category readout.
            PipInventoryRow row = PipInventoryList.BuildRow(new PipInventoryEntry
            {
                Id = "i", Name = "I", Kind = PipInventoryKind.Item, SubPage = 3,
                ExtraTip = "chems", PendingDrop = 4,
            });

            Assert.AreEqual("DROP: 4", row.ExtraTip);
        }

        [Test]
        public void BuildRow_ZeroPendingDropLeavesTheAmmoTipColumnAlone()
        {
            PipInventoryRow row = PipInventoryList.BuildRow(new PipInventoryEntry
            {
                Id = "i", Name = "I", Kind = PipInventoryKind.Item, SubPage = 3,
                ExtraTip = "chems", PendingDrop = 0,
            });

            Assert.AreEqual("chems", row.ExtraTip);
        }

        [Test]
        public void BuildRow_UnboundNewItemShowsTheNewMarker()
        {
            // PipPageInv.as:474-484 — the marker fills the fav column only when nothing else has it.
            PipInventoryRow row = PipInventoryList.BuildRow(new PipInventoryEntry
            {
                Id = "i", Name = "I", Kind = PipInventoryKind.Item, SubPage = 3, IsNew = true,
            });

            Assert.AreEqual("☩", row.Fav);
        }

        [Test]
        public void BuildRow_UnboundRecentItemShowsTheRecentMarker()
        {
            PipInventoryRow row = PipInventoryList.BuildRow(new PipInventoryEntry
            {
                Id = "i", Name = "I", Kind = PipInventoryKind.Item, SubPage = 3, IsRecent = true,
            });

            Assert.AreEqual("+", row.Fav);
        }

        [Test]
        public void BuildRow_NewWinsOverRecentWhenBothAreSet()
        {
            // The Oracle's chain is an if/else-if, so `nov == 1` takes precedence over `nov == 2`.
            PipInventoryRow row = PipInventoryList.BuildRow(new PipInventoryEntry
            {
                Id = "i", Name = "I", Kind = PipInventoryKind.Item, SubPage = 3,
                IsNew = true, IsRecent = true,
            });

            Assert.AreEqual("☩", row.Fav);
        }

        [Test]
        public void BuildRow_ABoundSlotWinsOverTheNewMarker()
        {
            PipInventoryRow row = PipInventoryList.BuildRow(new PipInventoryEntry
            {
                Id = "i", Name = "I", Kind = PipInventoryKind.Item, SubPage = 3,
                Favorite = "[3]", IsNew = true,
            });

            Assert.AreEqual("[3]", row.Fav, "the marker only fills an empty fav column");
        }

        [Test]
        public void BuildRow_CarriesTheIdKindEquippedAndAvailableThrough()
        {
            // A click sends the id back, the frame reads Equipped and the name's alpha reads
            // Available — so all four must survive row construction rather than being re-derived.
            PipInventoryRow row = PipInventoryList.BuildRow(new PipInventoryEntry
            {
                Id = "w9", Name = "W", Kind = PipInventoryKind.Weapon, SubPage = 1,
                Equipped = true, Available = true,
            });

            Assert.AreEqual("w9", row.Id);
            Assert.AreEqual(PipInventoryKind.Weapon, row.Kind);
            Assert.IsTrue(row.Equipped);
            Assert.IsTrue(row.Available);
        }

        [Test]
        public void BuildRow_FormatIsTheSixColumnsInColumnOrder()
        {
            // Fav | Name | Value | Extra | Mass | ExtraTip — the order the row clip's fields are in.
            PipInventoryRow row = PipInventoryList.BuildRow(new PipInventoryEntry
            {
                Id = "i", Name = "N", Kind = PipInventoryKind.Item, SubPage = 3,
                Value = 7f, Mass = 1f, Count = 2, ExtraTip = "cat",
            });

            Assert.AreEqual(" | N | 7 |  | 2 | cat", row.Format());
        }

        // ── Windowing (PipPage.setStatItems) ─────────────────────────────────

        [Test]
        public void EntryIndexForRow_ShiftsTheRowByTheScrollOffset()
        {
            // PipPage.as:983-1003 — row i shows entry i + scroll.
            Assert.AreEqual(0, PipInventoryList.EntryIndexForRow(0, 40, 0, 18));
            Assert.AreEqual(3, PipInventoryList.EntryIndexForRow(0, 40, 3, 18));
            Assert.AreEqual(20, PipInventoryList.EntryIndexForRow(17, 40, 3, 18));
        }

        [Test]
        public void EntryIndexForRow_HidesARowThatRunsPastTheEnd()
        {
            // A row past the end must be hidden, not left showing the previous page's text.
            Assert.AreEqual(4, PipInventoryList.EntryIndexForRow(4, 5, 0, 18));
            Assert.AreEqual(-1, PipInventoryList.EntryIndexForRow(5, 5, 0, 18));
            Assert.AreEqual(-1, PipInventoryList.EntryIndexForRow(17, 5, 0, 18));
        }

        [Test]
        public void EntryIndexForRow_HidesARowOutsideTheClipRange()
        {
            Assert.AreEqual(-1, PipInventoryList.EntryIndexForRow(-1, 40, 0, 18));
            Assert.AreEqual(-1, PipInventoryList.EntryIndexForRow(18, 40, 0, 18));
        }

        [Test]
        public void MaxScroll_ClampsAtZeroForAShortList()
        {
            // PipPage.as:947 — arr.length - maxrows; a negative offset is not a scroll.
            Assert.AreEqual(22, PipInventoryList.MaxScroll(40, 18));
            Assert.AreEqual(0, PipInventoryList.MaxScroll(18, 18));
            Assert.AreEqual(0, PipInventoryList.MaxScroll(5, 18));
            Assert.AreEqual(0, PipInventoryList.MaxScroll(0, 18));
        }

        // ── The bottom strip (showBottext) ───────────────────────────────────

        [Test]
        public void BottomText_AlwaysShowsTheCapsTotal()
        {
            // PipPageInv.as:323 — written unconditionally, in the `yel` class.
            string expected = PipBuckReadoutFormat.Caps(1234);

            Assert.AreEqual(expected, PipInventoryList.BottomText(1234, false, 3, null));
            Assert.AreEqual(expected, PipInventoryList.BottomText(1234, true, 3, null),
                "a null category-mass provider leaves the caps total and nothing else");
            Assert.IsTrue(expected.Contains("1234"));
        }

        [Test]
        public void BottomText_OmitsMassWhenHardInventoryIsOff()
        {
            // World.w.hardInv off means the weight system is off, so no mass line at all.
            string soft = PipInventoryList.BottomText(10, false, 3, cat => 99f);
            Assert.AreEqual(PipBuckReadoutFormat.Caps(10), soft);
        }

        [Test]
        public void BottomText_WeaponsReportBothTheWeaponAndMagicCategories()
        {
            // PipPageInv.as:324-342 — page 1 asks for categories 4 AND 5, not just 4.
            string text = PipInventoryList.BottomText(0, true, 1, cat => cat == 4 ? 12f : 3f);
            Assert.AreEqual(PipBuckReadoutFormat.Caps(0) + "    12    3", text);
        }

        [Test]
        public void BottomText_TheItemPagesAskForAidMiscAmmoAsOneThreeTwo()
        {
            // The category is NOT the sub-page number: aid (3) -> 1, misc (4) -> 3, ammo (5) -> 2.
            // Reading it off the sub-page gets two of the three wrong. Each provider answers -1 for
            // any other category, so a wrong mapping prints "-1" rather than passing by coincidence.
            Assert.AreEqual(PipBuckReadoutFormat.Caps(0) + "    11",
                PipInventoryList.BottomText(0, true, 3, cat => cat == 1 ? 11f : -1f));
            Assert.AreEqual(PipBuckReadoutFormat.Caps(0) + "    33",
                PipInventoryList.BottomText(0, true, 4, cat => cat == 3 ? 33f : -1f));
            Assert.AreEqual(PipBuckReadoutFormat.Caps(0) + "    55",
                PipInventoryList.BottomText(0, true, 5, cat => cat == 2 ? 55f : -1f));
        }

        [Test]
        public void BottomText_ArmourHasNoMassLine()
        {
            // The Oracle's switch has no case for page2 == 2, so armour falls through to the caps
            // total alone even in hard mode.
            Assert.AreEqual(PipBuckReadoutFormat.Caps(7),
                PipInventoryList.BottomText(7, true, PipInventoryList.ArmorSubPage, cat => 99f));
        }

        [Test]
        public void BottomText_FromAFooter_MatchesTheLooseArguments()
        {
            var footer = new PipInventoryFooter
            {
                Money = 42,
                HardInventory = true,
                CategoryMass = cat => cat * 2f,
            };

            Assert.AreEqual(PipBuckReadoutFormat.Caps(42) + "    6", PipInventoryList.BottomText(footer, 4));
        }

        // ── Layout: the page header and the corrected tab bar ────────────────

        [Test]
        public void PageHeaderPosition_IsOneRowPitchAboveRowZero()
        {
            // PipPage.as:108-142 builds maxrows + 1 clips; the -1 iteration is statHead at
            // y = 100 + (-1) * 30. It is a real row clip, which is why the captions line up by
            // construction rather than by two numbers agreeing.
            Vector2 header = PipBuckLayout.PageHeaderPosition();
            Vector2 first = PipBuckLayout.PageRowPosition(0);

            Assert.AreEqual(PipBuckLayout.PageRowX, header.x, 0.0001f);
            Assert.AreEqual(70f, header.y, 0.0001f);
            Assert.AreEqual(PipBuckLayout.PageRowPitch, first.y - header.y, 0.0001f);
        }

        [Test]
        public void PageRowPosition_PitchesByThirtyAndRefusesAnIndexOutsideTheClipRange()
        {
            Assert.AreEqual(new Vector2(30f, 100f), PipBuckLayout.PageRowPosition(0));
            Assert.AreEqual(new Vector2(30f, 100f + 17f * 30f), PipBuckLayout.PageRowPosition(17));
            Assert.Throws<ArgumentOutOfRangeException>(() => PipBuckLayout.PageRowPosition(18));
            Assert.Throws<ArgumentOutOfRangeException>(() => PipBuckLayout.PageRowPosition(-1));
        }

        [Test]
        public void MonitorRowPosition_PitchesByThirtyOverFifteenRows()
        {
            // PipBuck.as:131-144 — kolRItems = 15, x = 5, y = 40 + i * 30.
            Assert.AreEqual(15, PipBuckLayout.MonitorRowCount);
            Assert.AreEqual(new Vector2(5f, 40f), PipBuckLayout.MonitorRowPosition(0));
            Assert.AreEqual(new Vector2(5f, 40f + 14f * 30f), PipBuckLayout.MonitorRowPosition(14));
            Assert.Throws<ArgumentOutOfRangeException>(() => PipBuckLayout.MonitorRowPosition(15));
        }

        [Test]
        public void EveryPipElement_HasAnAnchorRow()
        {
            // The failure this guards is a NEW enum member nobody added to the table: AnchorFor would
            // throw at build time on the device, which is the right failure, but only if it is
            // actually reached. Here it is reached for every member.
            foreach (PipElement element in Enum.GetValues(typeof(PipElement)))
            {
                Assert.DoesNotThrow(() => PipBuckLayout.AnchorFor(element), $"no anchor row for {element}");
            }
        }

        [Test]
        public void AnchorTableOrder_MatchesTheEnumOrder()
        {
            // AnchorFor indexes the table by `(int)element - 1`, so a reorder of either list silently
            // hands an element another element's position — no exception, just a tab where the
            // monitor belongs. Naming each element's expected position is the only way to see that.
            Assert.AreEqual(new Vector2(600f, 403f), PipBuckLayout.AnchorFor(PipElement.Skin).Position);
            Assert.AreEqual(new Vector2(599.5f, 399.5f), PipBuckLayout.AnchorFor(PipElement.Fon).Position);
            Assert.AreEqual(new Vector2(620f, 390.8f), PipBuckLayout.AnchorFor(PipElement.PipError).Position);
            Assert.AreEqual(new Vector2(30f, 693.5f), PipBuckLayout.AnchorFor(PipElement.CloseTab).Position);
            Assert.AreEqual(new Vector2(17f, 135.5f), PipBuckLayout.AnchorFor(PipElement.Tab1).Position);
            Assert.AreEqual(new Vector2(17f, 453.5f), PipBuckLayout.AnchorFor(PipElement.Tab5).Position);
            Assert.AreEqual(new Vector2(17f, 531.9f), PipBuckLayout.AnchorFor(PipElement.HelpButton).Position);
            Assert.AreEqual(new Vector2(76f, 531.9f), PipBuckLayout.AnchorFor(PipElement.MassButton).Position);
            Assert.AreEqual(new Vector2(505.4f, 5f), PipBuckLayout.AnchorFor(PipElement.TopText).Position);
            Assert.AreEqual(new Vector2(1105.7f, 135.5f), PipBuckLayout.AnchorFor(PipElement.StatusPanel).Position);
            Assert.AreEqual(new Vector2(165f, 72f), PipBuckLayout.AnchorFor(PipElement.PageRoot).Position);
            Assert.AreEqual(new Vector2(168f, 138f), PipBuckLayout.AnchorFor(PipElement.HelpPopup).Position);
            Assert.AreEqual(new Vector2(600f, 400f), PipBuckLayout.AnchorFor(PipElement.SetKeyPopup).Position);
        }

        [Test]
        public void TheTabsAreALeftHandColumnNotATopBar()
        {
            // The interface specification's "6 top buttons" / "Top Nav Tabs" is wrong, and the SWF
            // read says so: but1..but5 share x = 17 and step DOWN by ~80 px, and but0 (Close) sits
            // alone near the bottom. This test exists so the spec's claim cannot quietly come back.
            Vector2[] tabs =
            {
                PipBuckLayout.AnchorFor(PipElement.Tab1).Position,
                PipBuckLayout.AnchorFor(PipElement.Tab2).Position,
                PipBuckLayout.AnchorFor(PipElement.Tab3).Position,
                PipBuckLayout.AnchorFor(PipElement.Tab4).Position,
                PipBuckLayout.AnchorFor(PipElement.Tab5).Position,
            };

            foreach (Vector2 tab in tabs)
            {
                Assert.AreEqual(17f, tab.x, 0.0001f, "every page tab shares one left-hand column");
            }

            for (int i = 1; i < tabs.Length; i++)
            {
                Assert.Greater(tabs[i].y, tabs[i - 1].y,
                    "the column steps down; a top bar would have equal y and different x");
            }

            Vector2 close = PipBuckLayout.AnchorFor(PipElement.CloseTab).Position;
            Assert.AreEqual(30f, close.x, 0.0001f);
            Assert.Greater(close.y, tabs[4].y, "Close sits below the last page tab");
        }

        [Test]
        public void ToUnityAnchoredPosition_FlipsYExactlyOnce()
        {
            // Flash's y grows down, uGUI's anchoredPosition grows up. Every placement goes through
            // this one method, so the flip exists in exactly one place.
            PipAnchor anchor = PipBuckLayout.AnchorFor(PipElement.TopText);

            Assert.AreEqual(new Vector2(505.4f, -5f), anchor.ToUnityAnchoredPosition(),
                "toptext is 5 px DOWN from the top edge, so it is -5 in a Y-up space");
        }

        [Test]
        public void BackdropAnchor_IsAlreadyNegativeInDeviceSpaceSoTheFlipYieldsPositive()
        {
            // The one placement that looks like a double negation and is not one, so it is pinned
            // separately rather than folded into the test above.
            //
            // The artwork is 1920x1080 around a 1200x800 screen, so its top-left corner hangs 360 px
            // left and 137 px ABOVE the screen's top-left. "Above" is NEGATIVE in a Y-down space,
            // which is why BackdropOffset.y is already -137; negating it for uGUI therefore gives
            // +137 — the same 137 px above, now in a Y-up space. A reader who "fixes" the sign in
            // either place moves the whole device 274 px down the screen.
            Assert.Less(PipBuckLayout.BackdropOffset.y, 0f,
                "control: the device-space value is negative, so the flip is a real sign change");
            Assert.AreEqual(-360f, PipBuckLayout.BackdropOffset.x, 0.0001f);

            Vector2 backdrop = PipBuckLayout.BackdropAnchor.ToUnityAnchoredPosition();

            Assert.AreEqual(PipBuckLayout.BackdropOffset.x, backdrop.x, 0.0001f);
            Assert.AreEqual(-PipBuckLayout.BackdropOffset.y, backdrop.y, 0.0001f);
            Assert.AreEqual(137f, backdrop.y, 0.0001f,
                "positive: the corner is 137 px above the device's top-left, which is up in uGUI");

            // The value itself: sprite 1155's frame-1 bounds minimum, i.e. the PNG's own origin.
            // `skin` (1138) is the outer term on both axes — a 1920x1080 shape centred on its
            // placement (600, 403) — so the bounds are (-360, -137) .. (1560, 943).
            //
            // The y term used to read -140, "measured" as `toptext` (symbol y=5) landing on the title
            // text at PNG y=145. That is 3 px out: `toptext` is a DefineEditText whose glyphs sit
            // below its registration point. Cross-correlating ButPage's standalone export against the
            // composite agrees with the bounds computation (symbol origin at 137.0), so the artwork
            // was being drawn 3 px above the port's own overlays.
            Assert.AreEqual(-360f, PipBuckLayout.BackdropOffset.x, 0.0001f,
                "sprite 1155 bounds min = skin's extent: 1920x1080 centred on (600, 403)");
            Assert.AreEqual(-137f, PipBuckLayout.BackdropOffset.y, 0.0001f,
                "403 - 540; the old -140 came from toptext's glyphs, 3 px below its own origin");
            Assert.AreEqual(1560f - PipBuckLayout.BackdropOffset.x, PipBuckLayout.BackdropSize.x, 0.0001f);
            Assert.AreEqual(943f - PipBuckLayout.BackdropOffset.y, PipBuckLayout.BackdropSize.y, 0.0001f);
        }

        [Test]
        public void BackdropFootprints_LandOnTheSixBakedButtonPositions()
        {
            // FFDec's sprite export flattens visPipBuck's display list, so each tab button's art sits
            // inside PipBuck_Bezel.png at (symbol - boundsMin - canvasInset): the placement gives the
            // registration point and ButPage's canvas is the shape padded by half its stroke width.
            // These six rects are where the six ButPage instances were rasterised — including their
            // design-time captions.
            //
            // Cross-checked against the real pixels, not derived twice: re-exporting sprite 1155 with
            // the six ButPage placements removed changes exactly these six rectangles and nothing
            // else (53064 px total, 8844 per tab, 0 outside) —
            // .workbuddy-ai/tools/verify_bezel_tab_strip.py.
            var expected = new Dictionary<PipElement, Vector2>
            {
                { PipElement.Tab1, new Vector2(375.5f, 271f) },
                { PipElement.Tab2, new Vector2(375.5f, 349f) },
                { PipElement.Tab3, new Vector2(375.5f, 429f) },
                { PipElement.Tab4, new Vector2(375.5f, 509f) },
                { PipElement.Tab5, new Vector2(375.5f, 589f) },
                { PipElement.CloseTab, new Vector2(388.5f, 829f) },
            };

            foreach (var pair in expected)
            {
                Rect r = PipBuckLayout.BackdropFootprint(pair.Key);
                Assert.AreEqual(pair.Value.x, r.x, 0.0001f, pair.Key + " footprint x");
                Assert.AreEqual(pair.Value.y, r.y, 0.0001f, pair.Key + " footprint y");
                Assert.AreEqual(132f, r.width, 0.0001f, "the ButPage canvas is 132x69");
                Assert.AreEqual(69f, r.height, 0.0001f, "the ButPage canvas is 132x69");
            }
        }

        [Test]
        public void TabSlots_AreCloseThenOneToFiveInDrawOrder()
        {
            // PipBuckView.BuildTabs maps slot 0 to but0 (Close) and slots 1..5 to but1..but5, and
            // PipBuck.as:108-176 sets the captions in that order. The footprint table walks the same
            // list, so the two cannot disagree about which tab is which.
            Assert.AreEqual(6, PipBuckLayout.TabSlots.Length);
            Assert.AreEqual(PipElement.CloseTab, PipBuckLayout.TabSlots[0]);
            for (int i = 1; i < PipBuckLayout.TabSlots.Length; i++)
            {
                Assert.AreEqual((int)PipElement.Tab1 + i - 1, (int)PipBuckLayout.TabSlots[i],
                    "slot " + i + " is but" + i);
            }
        }

        [Test]
        public void TabFootprintInterior_IsInsetSoTheButtonsOwnStrokeIsExcluded()
        {
            // The content check asks "is this region free of #00ff99?". The button's own border is
            // that colour — 1 px on frame 1, 3 px on frame 2 — so the interior must stay clear of it,
            // and must still cover the caption (ButPage-local y 17.3..43.7 of a 69 px canvas).
            foreach (PipElement tab in PipBuckLayout.TabSlots)
            {
                Rect outer = PipBuckLayout.BackdropFootprint(tab);
                Rect inner = PipBuckLayout.BackdropFootprintInterior(tab);

                Assert.Greater(inner.xMin, outer.xMin + 3f, tab + ": clear of the 3 px active stroke");
                Assert.Greater(inner.yMin, outer.yMin + 3f, tab + ": clear of the 3 px active stroke");
                Assert.Less(inner.xMax, outer.xMax - 3f, tab + ": clear of the 3 px active stroke");
                Assert.Less(inner.yMax, outer.yMax - 3f, tab + ": clear of the 3 px active stroke");

                Assert.Greater(inner.yMax, outer.yMin + 44f,
                    tab + ": still covers the caption band (ButPage-local y 17.3..43.7)");
            }
        }

        [Test]
        public void ComputeDevicePlacement_CoversAllThreeRegimes()
        {
            // PipBuck.as:357-384.
            PipBuckLayout.DevicePlacement atReference = PipBuckLayout.ComputeDevicePlacement(1200f, 800f);
            Assert.AreEqual(0f, atReference.X, 0.0001f);
            Assert.AreEqual(0f, atReference.Y, 0.0001f);
            Assert.AreEqual(1f, atReference.Scale, 0.0001f);

            // The 1320 boundary is exclusive: at exactly 1320 the device is still pinned top-left.
            PipBuckLayout.DevicePlacement atBoundary = PipBuckLayout.ComputeDevicePlacement(1320f, 800f);
            Assert.AreEqual(0f, atBoundary.X, 0.0001f, "1320 is not > 1320");

            PipBuckLayout.DevicePlacement wide = PipBuckLayout.ComputeDevicePlacement(1920f, 1080f);
            Assert.AreEqual(300f, wide.X, 0.0001f, "(1920 - 1200) / 2 - 60");
            Assert.AreEqual(140f, wide.Y, 0.0001f);
            Assert.AreEqual(1f, wide.Scale, 0.0001f, "the device is never magnified, only moved");

            PipBuckLayout.DevicePlacement narrow = PipBuckLayout.ComputeDevicePlacement(1000f, 800f);
            Assert.AreEqual(1000f / 1200f, narrow.Scale, 0.0001f);
            Assert.AreEqual(0f, narrow.X, 0.0001f, "a shrunk device is pinned top-left, not centred");

            PipBuckLayout.DevicePlacement squat = PipBuckLayout.ComputeDevicePlacement(1200f, 700f);
            Assert.AreEqual(700f / 800f, squat.Scale, 0.0001f, "the smaller of the two ratios wins");
        }

        // ── Readout format, on the numbers the columns above go through ───────

        [Test]
        public void As3Round_RoundsHalfUpNotHalfToEven()
        {
            // PipBuck.as:586-597's Math.round. Unity's Mathf.Round would give 0 and 2 here.
            Assert.AreEqual(1, PipBuckReadoutFormat.As3Round(0.5f));
            Assert.AreEqual(3, PipBuckReadoutFormat.As3Round(2.5f));
            Assert.AreEqual(37, PipBuckReadoutFormat.As3Round(36.5f));
            Assert.AreEqual(37, PipBuckReadoutFormat.As3Round(36.6f));
            Assert.AreEqual(36, PipBuckReadoutFormat.As3Round(36.4f));
        }

        [Test]
        public void MedClass_UsesTheOraclesTwoThresholds()
        {
            // red below 25 %, "or" below 50 %, else the sheet default.
            Assert.AreEqual("red", PipBuckReadoutFormat.MedClass(24f, 100f));
            Assert.AreEqual("or", PipBuckReadoutFormat.MedClass(25f, 100f), "25 is not < 25");
            Assert.AreEqual("or", PipBuckReadoutFormat.MedClass(49f, 100f));
            Assert.AreEqual("", PipBuckReadoutFormat.MedClass(50f, 100f), "50 is not < 50");
        }

        [Test]
        public void ClassToHex_ResolvesTheOraclesClassesToInterfaceColours()
        {
            Assert.AreEqual(PipBuckReadoutFormat.ToHex(PfeUiColors.PageRed), PipBuckReadoutFormat.ClassToHex("red"));
            Assert.AreEqual(PipBuckReadoutFormat.ToHex(PfeUiColors.PageOrange), PipBuckReadoutFormat.ClassToHex("or"));
            Assert.AreEqual(PipBuckReadoutFormat.ToHex(PfeUiColors.PageYellow), PipBuckReadoutFormat.ClassToHex("yel"));
            Assert.AreEqual(PipBuckReadoutFormat.ToHex(PfeUiColors.PageR0), PipBuckReadoutFormat.ClassToHex("r0"));
            Assert.AreEqual(PipBuckReadoutFormat.ToHex(PfeUiColors.PageR0), PipBuckReadoutFormat.ClassToHex(""),
                "the sheet default and .r0 are the same colour");
        }

        [Test]
        public void Caps_IsTheYellowClassWithNoDenominator()
        {
            // PipBuck.as:539 — written inline, so it has no threshold colouring.
            Assert.AreEqual(
                "<color=#" + PipBuckReadoutFormat.ToHex(PfeUiColors.PageYellow) + ">1234</color>",
                PipBuckReadoutFormat.Caps(1234));
        }

        [Test]
        public void Percent_AtZeroMaxShowsZeroRatherThanNaN()
        {
            // The Oracle divides unguarded and renders the literal "NaN%". The port shows "0%". That
            // is a recorded divergence, and this test is where it is recorded.
            string text = PipBuckReadoutFormat.Percent(0f, 0f);

            Assert.IsTrue(text.Contains("0%"));
            Assert.IsFalse(text.Contains("NaN"));
        }

        [Test]
        public void ItemKind_TestsAmmoBeforeUsable()
        {
            // PipPageInv.as:244-253 — `tip == "a" || tip == "e"` is tested BEFORE `@us > 0`, so an
            // ammo type that is also usable still lands on the ammo page. Swapping the branches moves
            // items between tabs and looks like a data problem.
            Assert.AreEqual(2, PipBuckReadoutFormat.ItemKind(true, true));
            Assert.AreEqual(2, PipBuckReadoutFormat.ItemKind(true, false));
            Assert.AreEqual(1, PipBuckReadoutFormat.ItemKind(false, true));
            Assert.AreEqual(0, PipBuckReadoutFormat.ItemKind(false, false));
        }

        [Test]
        public void ItemKindForSubPage_MapsAidMiscAmmoToOneZeroTwo()
        {
            Assert.AreEqual(1, PipBuckReadoutFormat.ItemKindForSubPage(3));
            Assert.AreEqual(0, PipBuckReadoutFormat.ItemKindForSubPage(4));
            Assert.AreEqual(2, PipBuckReadoutFormat.ItemKindForSubPage(5));
            Assert.Throws<ArgumentOutOfRangeException>(() => PipBuckReadoutFormat.ItemKindForSubPage(1));
            Assert.Throws<ArgumentOutOfRangeException>(() => PipBuckReadoutFormat.ItemKindForSubPage(2));
        }

        // ── The tab button's art (I7, the "Инвентарь1" defect) ────────────────
        //
        // The exported tab PNG could not be used: SWF symbol 1143 (`ButPage`) also carries two
        // DefineEditText fields whose design-time values are the author's Russian placeholders, and
        // FFDec composites them into the flat export. `PipBuckTabArt` renders the Oracle's own
        // background shape instead (pfe/shapes/1139.svg and 1142.svg).
        //
        // These are the assertions that the replacement is the real art and not a stand-in. The
        // colour expectations are FFDec's own pixels, sampled out of
        // Assets/_PFE/Art/UI/Interface/PipBuck_TabButton_*.png before they were regenerated.

        private static readonly Color32 TabStroke = new Color32(0x00, 0xFF, 0x99, 0xFF);

        [Test]
        public void TabArt_IsExactlyTheSizeTheLayoutPlaces()
        {
            // The art and the layout have to agree: PipBuckLayout.TabButtonSize is what the tab's
            // RectTransform is set to, so a 1 px mismatch would stretch the button's border.
            Assert.AreEqual(PipBuckLayout.TabButtonSize.x, PipBuckTabArt.Width, 0.0001f);
            Assert.AreEqual(PipBuckLayout.TabButtonSize.y, PipBuckTabArt.Height, 0.0001f);
        }

        [Test]
        public void TabArt_NormalFrame_HasATransparentOuterPixelAndAOnePixelStroke()
        {
            // FFDec, frame 1: row 33 is (0,0,0,0) at x=0, the stroke at x=1, and gradient at x=2.
            // The 1 px stroke is what distinguishes frame 1 from frame 2.
            Assert.AreEqual(new Color32(0, 0, 0, 0), PipBuckTabArt.Sample(0, 33, false),
                "the 1 px stroke must not reach the canvas edge");
            Assert.AreEqual(TabStroke, PipBuckTabArt.Sample(1, 33, false));
            Assert.AreNotEqual(TabStroke, PipBuckTabArt.Sample(2, 33, false),
                "the stroke is 1 px, so x=2 is already the gradient");
        }

        [Test]
        public void TabArt_ActiveFrame_HasAThreePixelStrokeThatReachesTheEdge()
        {
            // FFDec, frame 2: x=0..2 are all stroke, x=3 is the first gradient pixel. This is the
            // whole visual difference between a selected tab and an unselected one.
            Assert.AreEqual(TabStroke, PipBuckTabArt.Sample(0, 33, true));
            Assert.AreEqual(TabStroke, PipBuckTabArt.Sample(1, 33, true));
            Assert.AreEqual(TabStroke, PipBuckTabArt.Sample(2, 33, true));
            Assert.AreNotEqual(TabStroke, PipBuckTabArt.Sample(3, 33, true),
                "the stroke is 3 px, so x=3 is already the gradient");
        }

        [Test]
        public void TabArt_GradientIsBlackAtTheCentreAndGreenerAtTheEdge()
        {
            // The radial gradient is #000000 at its centre and #004629 at the rim, so the button
            // reads as a dark well. Getting the radii wrong flattens it or inverts it.
            Color32 centre = PipBuckTabArt.Sample(66, 34, false);
            Color32 corner = PipBuckTabArt.Sample(6, 6, false);

            Assert.LessOrEqual(centre.g, 3, "the centre of the gradient is black");
            Assert.GreaterOrEqual(corner.g, 55, "a corner is close to the #004629 rim");
            Assert.Greater(corner.g, centre.g);
            Assert.AreEqual(255, centre.a, "the interior is fully opaque");
            Assert.AreEqual(255, corner.a);
        }

        [Test]
        public void TabArt_ActiveFrameIsBrighterThanTheNormalFrame()
        {
            // Frame 2's outer stop is #006d40 against frame 1's #004629 — the only fill difference
            // between the two states, and the reason a lit tab reads as lit.
            Color32 normal = PipBuckTabArt.Sample(6, 6, false);
            Color32 active = PipBuckTabArt.Sample(6, 6, true);

            Assert.Greater(active.g, normal.g);
            Assert.Greater(active.b, normal.b);
        }

        [Test]
        public void TabArt_HasNoStrokeColouredPixelInItsInterior()
        {
            // THE DEFECT THIS ART EXISTS TO FIX. FFDec's export carries the author's design-time
            // placeholder — "Инвентарь" plus "1", SWF characters 1140 and 1141 — rasterised in
            // #00ff99 straight across the middle of the button, which is the same colour as the
            // border. A rendered button must have none of that.
            //
            // The control matters as much as the assertion: a button rendered entirely transparent,
            // or with the border forgotten, would also have no green in its interior. So the border
            // is asserted to BE stroke-green first.
            Assert.AreEqual(TabStroke, PipBuckTabArt.Sample(1, 33, false),
                "control: the border is stroke-green, so 'no green inside' is a real claim");
            Assert.AreEqual(TabStroke, PipBuckTabArt.Sample(66, 1, false),
                "control: the top border is stroke-green too");

            for (int y = 8; y < PipBuckTabArt.Height - 8; y++)
            {
                for (int x = 8; x < PipBuckTabArt.Width - 8; x++)
                {
                    Color32 pixel = PipBuckTabArt.Sample(x, y, false);
                    Assert.Less(pixel.g, 120,
                        $"interior pixel ({x},{y}) is stroke-green — that is the baked placeholder");
                }
            }
        }

        [Test]
        public void TabArt_IsFullyOpaqueEverywhereInsideTheStroke()
        {
            // The button is a solid rectangle, so a hole in the middle would show the bezel through
            // it. Sampled away from the border, where coverage is partial by design.
            for (int y = 6; y < PipBuckTabArt.Height - 6; y++)
            {
                for (int x = 6; x < PipBuckTabArt.Width - 6; x++)
                {
                    Assert.AreEqual(255, PipBuckTabArt.Sample(x, y, false).a,
                        $"({x},{y}) is inside the button and must be opaque");
                }
            }
        }

        [Test]
        public void TabArt_IsDrawnInsideItsCanvasWithATransparentOuterCorner()
        {
            // A guard on the loops above: the corner samples must be real, not silently out of
            // range and returning transparent black for the wrong reason.
            //
            // The outermost corner is genuinely empty, and that is not a bug to be "fixed": the
            // 1 px stroke's outer edge lands at x=131.7, y=68.0, which leaves pixel (131,68) with
            // no area at all. FFDec's own export agrees — row 68 of PipBuck_TabButton_1.png is
            // fully transparent. An earlier version of this test asserted the corner was opaque;
            // the art was right and the expectation was wrong.
            Assert.AreEqual(0, PipBuckTabArt.Sample(PipBuckTabArt.Width - 1,
                PipBuckTabArt.Height - 1, false).a);

            Assert.AreNotEqual(0, PipBuckTabArt.Sample(PipBuckTabArt.Width - 2,
                PipBuckTabArt.Height - 2, false).a,
                "the pixel just inside that corner is on the stroke");
        }

        // ── The tab captions ──────────────────────────────────────────────────

        [Test]
        public void TabLabels_AreTheGamesOwnCaptions_NotThePageIdentifiers()
        {
            // The Oracle resolves these from Res.pipText("main" + n) (PipBuck.as:172) against a
            // localisation document that was never extracted; the values here are read off the
            // shipped game's screen. The previous behaviour — showing the page identifier — is what
            // put "STAT"/"INV" on the tabs.
            Assert.AreEqual("Close [TAB]", PipBuckPages.TabLabelFor(0));
            Assert.AreEqual("Status", PipBuckPages.TabLabelFor(1));
            Assert.AreEqual("Inventory", PipBuckPages.TabLabelFor(2));
            Assert.AreEqual("Information", PipBuckPages.TabLabelFor(3));
            Assert.AreEqual("Main menu", PipBuckPages.TabLabelFor(5));
        }

        [Test]
        public void TabLabels_FallBackToTheOracleIdentifierForAPageWithNoCaption()
        {
            // Pages 6..9 are the contextual ones: PipBuck.as:275 hides tab 4 unless a contextual page
            // owns it and :279 relabels it to that page's own caption, so `main6`..`main9` never reach
            // a permanent tab and this table carries none of them. The fallback is the Oracle's own
            // page identifier — attested data — rather than an invented word.
            //
            // Page 4 used to be the example here, because its caption was unobservable too. It is not
            // any more: the shipped `text_en.xml` has `<pip id="main4">Trading</pip>` and
            // PipBuckPages.Captions now carries it, so this assertion read "VEND" while the device
            // read "Trading". Corrected rather than deleted — the fallback below is still live, it
            // just moved to the four pages that are actually captionless.
            Assert.AreEqual("MED", PipBuckPages.TabLabelFor(6));
            Assert.AreEqual("WORK", PipBuckPages.TabLabelFor(7));
            Assert.AreEqual("APP", PipBuckPages.TabLabelFor(8));
            Assert.AreEqual("VAULT", PipBuckPages.TabLabelFor(9));

            // The page whose caption was found, and which is therefore no longer a fallback case.
            Assert.AreEqual("Trading", PipBuckPages.TabLabelFor(4));

            Assert.AreEqual("?", PipBuckPages.TabLabelFor(99));
            Assert.AreEqual("?", PipBuckPages.TabLabelFor(-1));
        }

        [Test]
        public void LabelKeys_AreTheOracleStringTableKeys()
        {
            // These are the keys a future localisation import has to satisfy. Page 0 is `main0`
            // and not `mainclose`: the Oracle writes mainclose in the constructor (PipBuck.as:110)
            // and then main0 in toNormalMode (:177), which World.as:862 runs unconditionally — so
            // main0 is the one the player actually sees.
            Assert.AreEqual("main0", PipBuckPages.LabelKeyFor(0));
            Assert.AreEqual("main1", PipBuckPages.LabelKeyFor(1));
            Assert.AreEqual("main5", PipBuckPages.LabelKeyFor(5));
            Assert.AreEqual("main9", PipBuckPages.LabelKeyFor(9));
        }

        [Test]
        public void EveryPageHasANonEmptyLabel()
        {
            // A blank tab is indistinguishable from a tab whose art failed to load, which is the
            // failure this whole area just came out of.
            foreach (int pageId in PipBuckPages.AllPageIds())
            {
                Assert.IsNotEmpty(PipBuckPages.TabLabelFor(pageId), $"page {pageId} has no label");
            }

            Assert.IsNotEmpty(PipBuckPages.TabLabelFor(0), "the Close tab has no label");
        }
    }
}
