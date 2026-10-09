using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using PFE.Core.Scripting;

namespace PFE.Tests.Scripting
{
    /// <summary>
    /// The F2 unit-spawn picker's decision table. <b>Unity-free, so these run offline</b> through the
    /// wall harness as well as in the editor — see the
    /// <c>execute-unity-editmode-fixtures-offline</c> skill.
    /// </summary>
    [TestFixture]
    public sealed class UnitSpawnCatalogTests
    {
        // ── The id arithmetic ────────────────────────────────────────────────

        [TestCase("bloat10", "bloat")]
        [TestCase("zombie0", "zombie")]
        [TestCase("raider9", "raider")]
        [TestCase("damexpl1", "damexpl")]
        [TestCase("molerat", "molerat")]
        [TestCase("dront", "dront")]
        [TestCase("", "")]
        [TestCase(null, "")]
        public void RootOf_StripsOnlyTrailingDigits(string id, string expected)
        {
            Assert.That(UnitSpawnCatalog.RootOf(id), Is.EqualTo(expected));
        }

        [TestCase("raider3", 3)]
        [TestCase("bloat10", 10)]
        [TestCase("zombie0", 0)]
        [TestCase("molerat", -1)]
        [TestCase("dront", -1)]
        [TestCase("", -1)]
        public void TierOf_ReadsTheTrailingNumberOrMinusOne(string id, int expected)
        {
            Assert.That(UnitSpawnCatalog.TierOf(id), Is.EqualTo(expected));
        }

        [Test]
        public void TierOf_DoesNotThrowOnAnAbsurdSuffix()
        {
            // A 12-digit suffix is not a tier, and int.Parse would throw on it. Reporting -1 keeps a
            // malformed id a cosmetic problem instead of an exception inside OnGUI.
            Assert.That(UnitSpawnCatalog.TierOf("unit123456789012"), Is.EqualTo(-1));
        }

        // ── The rule table polices itself ────────────────────────────────────

        [Test]
        public void Rules_AreAlreadyDigitStripped()
        {
            // The table's one silent failure mode. Roots are matched against RootOf(id), so a root
            // written as "damexpl1" can never match "damexpl1" (which roots to "damexpl") — the unit
            // would quietly land in Unclassified and the Damagers row would look merely short.
            string[] offenders = UnitSpawnCatalog.Rules
                .Where(r => UnitSpawnCatalog.RootOf(r.Root) != r.Root)
                .Select(r => r.Family + ":" + r.Root)
                .ToArray();

            Assert.That(offenders, Is.Empty,
                "these roots carry their digits and can never match: " + string.Join(", ", offenders));
        }

        [Test]
        public void Rules_ListNoRootTwice()
        {
            string[] duplicates = UnitSpawnCatalog.Rules
                .GroupBy(r => r.Root, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToArray();

            Assert.That(duplicates, Is.Empty,
                "a root listed under two families is shadowed by the first, silently: " + string.Join(", ", duplicates));
        }

        [Test]
        public void Rules_ArePopulated()
        {
            // Control for the two tests above: on an empty table both would pass vacuously.
            Assert.That(UnitSpawnCatalog.Rules, Is.Not.Empty);
            foreach (UnitSpawnRule rule in UnitSpawnCatalog.Rules)
            {
                Assert.That(rule.Family, Is.Not.Null.And.Not.Empty);
                Assert.That(rule.Root, Is.Not.Null.And.Not.Empty);
            }
        }

        // ── Build ────────────────────────────────────────────────────────────

        [Test]
        public void Build_PlacesEveryIdInExactlyOneGroup()
        {
            string[] ids = { "raider1", "raider2", "zombie0", "dron2", "molerat", "bossdron", "brandnewthing" };

            List<UnitSpawnGroup> groups = UnitSpawnCatalog.Build(ids);
            string[] placed = groups.SelectMany(g => g.VariantIds).ToArray();

            Assert.That(placed.Length, Is.EqualTo(ids.Length),
                "an id that is dropped is invisible in the picker");
            Assert.That(placed, Is.Unique);
            Assert.That(placed, Does.Contain("brandnewthing"),
                "positive control: an id no rule claims must still be placed, in Unclassified");
        }

        [Test]
        public void Build_PutsAnUnknownIdInATrailingUnclassifiedGroup()
        {
            List<UnitSpawnGroup> groups = UnitSpawnCatalog.Build(new[] { "raider1", "zzz_mystery" });

            Assert.That(groups[groups.Count - 1].Name, Is.EqualTo(UnitSpawnCatalog.UnclassifiedGroupName));
            Assert.That(groups[groups.Count - 1].VariantIds, Is.EqualTo(new[] { "zzz_mystery" }));
        }

        [Test]
        public void Build_OrdersVariantsNumericallyNotOrdinally()
        {
            // An ordinal sort gives bloat0, bloat1, bloat10, bloat2 — which reads as "bloat1 has no
            // variants" and "bloat10 is a low tier". This is the test that pins the numeric key.
            List<UnitSpawnGroup> groups = UnitSpawnCatalog.Build(new[] { "bloat10", "bloat2", "bloat0" });

            Assert.That(groups[0].VariantIds, Is.EqualTo(new[] { "bloat0", "bloat2", "bloat10" }));
        }

        [Test]
        public void Build_OrdersAFoldedFamilyByRootThenTier()
        {
            List<UnitSpawnGroup> groups = UnitSpawnCatalog.Build(
                new[] { "roller2", "dron3", "dron1", "msp", "gutsy1", "roller" });

            Assert.That(groups[0].Name, Is.EqualTo("Robots"));
            Assert.That(groups[0].VariantIds,
                Is.EqualTo(new[] { "dron1", "dron3", "gutsy1", "msp", "roller", "roller2" }));
        }

        [Test]
        public void Build_ReportsRootedOnlyForASingleRootFamily()
        {
            List<UnitSpawnGroup> groups = UnitSpawnCatalog.Build(new[] { "raider1", "raider2", "dron1", "gutsy1" });

            UnitSpawnGroup raiders = groups.Find(g => g.Name == "Raiders");
            UnitSpawnGroup robots = groups.Find(g => g.Name == "Robots");

            Assert.That(raiders, Is.Not.Null);
            Assert.That(robots, Is.Not.Null);
            Assert.That(raiders.IsRooted, Is.True, "raider1..raider9 really are tiers of one unit");
            Assert.That(robots.IsRooted, Is.False, "dron1 and gutsy1 are different units, not tiers of one");
        }

        [Test]
        public void Build_OmitsAGroupWithNoMembers()
        {
            List<UnitSpawnGroup> groups = UnitSpawnCatalog.Build(new[] { "raider1" });

            Assert.That(groups.Count, Is.EqualTo(1));
            Assert.That(groups[0].Name, Is.EqualTo("Raiders"));
        }

        [Test]
        public void Build_ToleratesNullsEmptiesAndDuplicateIds()
        {
            List<UnitSpawnGroup> groups = UnitSpawnCatalog.Build(new[] { null, "", "   ", "raider1", "RAIDER1" });

            Assert.That(groups.Count, Is.EqualTo(1));
            Assert.That(groups[0].VariantIds.Count, Is.EqualTo(1));
        }

        [Test]
        public void Build_OnNoInputReturnsNoGroups()
        {
            Assert.That(UnitSpawnCatalog.Build(null), Is.Empty);
            Assert.That(UnitSpawnCatalog.Build(new string[0]), Is.Empty);
        }

        // ── The table covers the assets that exist today ─────────────────────

        /// <summary>
        /// Every id under <c>Assets/_PFE/Data/Resources/Units</c> as of 2026-10-08 (148 assets).
        /// <b>This list is meant to go stale.</b> It is not a mirror of the data — it is the input that
        /// proves the curated table claims every asset, and it fails loudly when a new one lands so the
        /// author adds a root instead of shipping a unit nobody can spawn from the picker.
        /// </summary>
        static readonly string[] RealUnitIds =
        {
            "alicorn", "alicorn1", "alicorn2", "alicorn3", "ant", "ant1", "ant2", "ant3",
            "bigrobot", "bloat", "bloat0", "bloat1", "bloat2", "bloat3", "bloat4", "bloat5",
            "bloat6", "bloat7", "bloat8", "bloat9", "bloat10", "bloodwing", "bloodwing2",
            "bossalicorn", "bossdron", "bossencl", "bossnecr", "bossraider", "bossultra",
            "captive", "cryoslime", "damexpl1", "damgren", "damshot", "destr1", "doctor",
            "dron1", "dron2", "dron3", "dront", "eant", "ebloat",
            "encl", "encl1", "encl2", "encl3", "encl4", "eqd",
            "fish1", "fish2", "fish3", "gutsy", "gutsy1", "hellhound", "hellhound1",
            "littlepip", "merc", "merc1", "merc2", "merc3", "merc4", "merc5", "molerat",
            "monster", "moon", "msp", "mtrap", "mwall", "necros", "npc", "other", "owl",
            "phoenix", "pinkslime", "ponpon", "pony", "protect", "protect1",
            "raider", "raider1", "raider2", "raider3", "raider4", "raider5", "raider6",
            "raider7", "raider8", "raider9",
            "ranger", "ranger1", "ranger2", "ranger3", "rat", "robobrain", "robot",
            "roller", "roller2", "scorp1", "scorp2", "scorp3", "scythe", "sentinel",
            "slaver", "slaver1", "slaver2", "slaver3", "slaver4", "slaver5", "slaver6",
            "slime", "smallrobot", "spectre", "spritebot", "tarakan", "thunderhead",
            "training", "transmitter", "trigcans", "triglaser", "trigplate", "trigridge",
            "ttur", "turret", "turret0", "turret1", "turret2", "turret3", "turret4", "turret5",
            "vendor", "vortex",
            "zebra", "zebra1", "zebra2", "zebra3", "zebra4", "zebra5",
            "zombie", "zombie0", "zombie1", "zombie2", "zombie3", "zombie4", "zombie5",
            "zombie6", "zombie7", "zombie8", "zombie9",
        };

        [Test]
        public void Build_ClaimsEveryAssetThatExistsToday()
        {
            Assert.That(RealUnitIds.Length, Is.EqualTo(148), "the literal drifted; re-count Resources/Units");

            List<UnitSpawnGroup> groups = UnitSpawnCatalog.Build(RealUnitIds);

            UnitSpawnGroup unclassified = groups.Find(g => g.Name == UnitSpawnCatalog.UnclassifiedGroupName);
            string[] orphans = unclassified != null ? unclassified.VariantIds.ToArray() : new string[0];

            Assert.That(orphans, Is.Empty,
                "these assets are claimed by no family rule, so the picker can only show them under " +
                "Unclassified — add a root to UnitSpawnCatalog.GroupRules: " + string.Join(", ", orphans));

            // The count is the half that stops `orphans` above being vacuous: it is empty for two very
            // different reasons — "every asset is classified" (what we want) and "Build emitted no
            // Unclassified bucket at all" (a bug) — and only the total distinguishes them.
            //
            // WHAT THIS TEST CANNOT SEE, measured by mutation 2026-10-08: replacing Build's
            // `if (unclassified.Count > 0)` with `if (false)` does NOT fail here, because all 148 real
            // assets are already claimed by a family rule, so the bucket is empty either way and the sum
            // is 148 either way. That mutant is caught instead by
            // Build_PutsAnUnknownIdInATrailingUnclassifiedGroup and Build_PlacesEveryIdInExactlyOneGroup,
            // which feed a synthetic unknown id. The count assertion below earns its place the moment the
            // real data gains one unclassified asset — not before.
            Assert.That(groups.SelectMany(g => g.VariantIds).Count(), Is.EqualTo(RealUnitIds.Length));
        }

        // ── Non-spawnable judgement ──────────────────────────────────────────

        [TestCase("raider", true)]
        [TestCase("zombie", true)]
        [TestCase("bloat", true)]
        [TestCase("turret", true)]
        [TestCase("littlepip", true)]
        [TestCase("npc", true)]
        [TestCase("raider1", false)]
        [TestCase("zombie0", false)]
        [TestCase("bloat5", false)]
        [TestCase("turret2", false)]
        [TestCase("training", false)]
        [TestCase("moon", false)]
        [TestCase("bossraider", false)]
        public void IsNonSpawnable_JudgesTheIdNotTheFamily(string id, bool expected)
        {
            // Paired deliberately: every `true` case has its own variant as a `false` case, so a rule
            // that matched on the root instead of the whole id fails here rather than passing.
            Assert.That(UnitSpawnCatalog.IsNonSpawnable(id), Is.EqualTo(expected));
        }

        [Test]
        public void IsNonSpawnable_IgnoresCase()
        {
            Assert.That(UnitSpawnCatalog.IsNonSpawnable("Raider"), Is.True);
            Assert.That(UnitSpawnCatalog.IsNonSpawnable("RAIDER"), Is.True);
        }

        [Test]
        public void IsNonSpawnable_IsFalseForNullAndEmpty()
        {
            Assert.That(UnitSpawnCatalog.IsNonSpawnable(null), Is.False);
            Assert.That(UnitSpawnCatalog.IsNonSpawnable(""), Is.False);
        }

        // ── Filter (what the "show non-spawnable" toggle goes through) ───────

        [Test]
        public void Filter_DropsAGroupThatFiltersToNothing()
        {
            List<UnitSpawnGroup> groups = UnitSpawnCatalog.Build(new[] { "raider1", "raider", "npc", "owl" });
            Assert.That(groups.Count, Is.EqualTo(2), "arrange: Raiders and NPCs & other are both present");

            List<UnitSpawnGroup> shown = UnitSpawnCatalog.Filter(groups, id => !UnitSpawnCatalog.IsNonSpawnable(id));

            Assert.That(shown.Count, Is.EqualTo(1), "a family whose every member is hidden must vanish, not render empty");
            Assert.That(shown[0].Name, Is.EqualTo("Raiders"));
            Assert.That(shown[0].VariantIds, Is.EqualTo(new[] { "raider1" }));
        }

        [Test]
        public void Filter_RecomputesRootedAfterNarrowing()
        {
            List<UnitSpawnGroup> groups = UnitSpawnCatalog.Build(new[] { "dron1", "dron2", "gutsy1" });
            Assert.That(groups[0].IsRooted, Is.False, "arrange: Robots folds two roots");

            List<UnitSpawnGroup> dronesOnly = UnitSpawnCatalog.Filter(groups, id => id.StartsWith("dron"));

            Assert.That(dronesOnly[0].IsRooted, Is.True, "narrowed to one root, the number IS the tier again");
            Assert.That(dronesOnly[0].VariantIds, Is.EqualTo(new[] { "dron1", "dron2" }));
        }

        [Test]
        public void Filter_KeepsGroupOrder()
        {
            List<UnitSpawnGroup> groups = UnitSpawnCatalog.Build(new[] { "raider1", "zombie0", "dron1" });

            List<UnitSpawnGroup> kept = UnitSpawnCatalog.Filter(groups, _ => true);

            Assert.That(kept.Select(g => g.Name), Is.EqualTo(groups.Select(g => g.Name)));
        }

        [Test]
        public void Filter_WithNoGroupsReturnsNothing()
        {
            Assert.That(UnitSpawnCatalog.Filter(null, _ => true), Is.Empty);
        }

        // ── Labels ───────────────────────────────────────────────────────────

        [Test]
        public void VariantLabel_NamesTheTierOnlyForASingleRootFamily()
        {
            // Robots needs TWO roots here, or the arrange defeats the point: a family holding one
            // variant shares one root trivially, so it would come back rooted and the label would name
            // a tier. That is what the first version of this test got wrong.
            List<UnitSpawnGroup> groups = UnitSpawnCatalog.Build(new[] { "raider3", "dron2", "gutsy1" });
            UnitSpawnGroup raiders = groups.Find(g => g.Name == "Raiders");
            UnitSpawnGroup robots = groups.Find(g => g.Name == "Robots");

            Assert.That(raiders.IsRooted, Is.True);
            Assert.That(robots.IsRooted, Is.False, "arrange: Robots must hold more than one root");
            Assert.That(UnitSpawnCatalog.VariantLabel(raiders, "raider3"), Is.EqualTo("Tier 3  ·  raider3"));
            Assert.That(UnitSpawnCatalog.VariantLabel(robots, "dron2"), Is.EqualTo("dron2"));
        }

        [Test]
        public void VariantLabel_NamesTheTierOnceFilteringLeavesOneRoot()
        {
            // Recorded rather than accidental: IsRooted describes the list in front of the tester, so
            // narrowing Robots to drones makes the number meaningful again and the label says so.
            List<UnitSpawnGroup> groups = UnitSpawnCatalog.Build(new[] { "dron1", "dron2", "gutsy1" });
            List<UnitSpawnGroup> dronesOnly = UnitSpawnCatalog.Filter(groups, id => id.StartsWith("dron"));

            Assert.That(dronesOnly[0].IsRooted, Is.True);
            Assert.That(UnitSpawnCatalog.VariantLabel(dronesOnly[0], "dron2"), Is.EqualTo("Tier 2  ·  dron2"));
        }

        [Test]
        public void VariantLabel_LeavesAnIdWithNoDigitsAloneEvenInARootedFamily()
        {
            // Necros is its own one-variant family and carries no trailing number, so there is no tier
            // to name — the label must fall back to the id rather than print "Tier -1".
            List<UnitSpawnGroup> groups = UnitSpawnCatalog.Build(new[] { "necros" });

            Assert.That(groups[0].Name, Is.EqualTo("Necros"));
            Assert.That(groups[0].IsRooted, Is.True);
            Assert.That(UnitSpawnCatalog.VariantLabel(groups[0], "necros"), Is.EqualTo("necros"));
        }

        [Test]
        public void FamilyLabel_CarriesTheCount()
        {
            List<UnitSpawnGroup> groups = UnitSpawnCatalog.Build(new[] { "raider1", "raider2" });

            Assert.That(UnitSpawnCatalog.FamilyLabel(groups[0]), Is.EqualTo("Raiders  (2)"));
        }

        // ── Dropdown viewport (the "rows do not fit vertically" defect) ───────
        //
        // The overlay used to size the list's viewport with `options.Count * 22f` while drawing 20 px
        // rows, and it forced a height onto a style whose own padding left only 8 px for a ~15 px glyph.
        // Two independent errors, both invisible to the compiler and both visible only on screen. The
        // viewport rule is now this method, so it is assertable here; the clipping half is structural
        // (the row carries no explicit height) and is not assertable offline.

        [TestCase(25f, 21, 300f, 11)]   // the real shape: 21 families, ~25 px rows, 300 px cap
        [TestCase(25f, 5, 300f, 5)]     // everything fits, so nothing scrolls
        [TestCase(25f, 1, 300f, 1)]
        [TestCase(19f, 21, 300f, 15)]   // a tighter skin fits more rows in the same cap
        [TestCase(40f, 21, 300f, 7)]    // a fatter skin fits fewer
        [TestCase(25f, 21, 27f, 1)]     // a cap of one row plus slack still shows exactly one row
        [TestCase(25f, 21, 1f, 1)]      // a degenerate cap shows one row, never zero
        public void VisibleRows_FitsWholeRowsWithinTheCap(float rowAdvance, int count, float maxHeight, int expected)
        {
            Assert.That(UnitPickerLayout.VisibleRows(rowAdvance, count, maxHeight), Is.EqualTo(expected));
        }

        [Test]
        public void Viewport_IsAWholeNumberOfRowsPlusSlack()
        {
            // The property that was broken: the viewport must be an exact multiple of the row advance, or
            // the last row is drawn cut in half and reads as a rendering bug rather than as "scroll me".
            var advances = new[] { 19f, 22f, 25f, 31.5f, 40f };

            foreach (float advance in advances)
            {
                for (int count = 1; count <= 25; count++)
                {
                    float viewport = UnitPickerLayout.ViewportHeight(advance, count, 300f);
                    int rows = UnitPickerLayout.VisibleRows(advance, count, 300f);
                    float expected = rows * advance + UnitPickerLayout.ViewportSlack;

                    Assert.That(viewport, Is.EqualTo(expected).Within(0.001f),
                        $"advance={advance} count={count}: a partial row would be drawn");
                }
            }
        }

        [Test]
        public void Viewport_ShowsEveryRowWhenTheyAllFit()
        {
            // With 5 rows of 25 px and a 300 px cap the list must not scroll at all -- the scrollbar is
            // the signal that something is hidden, so showing one over a complete list is a lie.
            float viewport = UnitPickerLayout.ViewportHeight(25f, 5, 300f);

            Assert.That(viewport, Is.EqualTo(5 * 25f + UnitPickerLayout.ViewportSlack).Within(0.001f));
            Assert.That(UnitPickerLayout.Scrolls(25f, 5, 300f), Is.False);
            Assert.That(UnitPickerLayout.Scrolls(25f, 21, 300f), Is.True, "21 rows cannot fit in 300 px");
        }

        [Test]
        public void Viewport_IsEmptyForAnEmptyList()
        {
            // The overlay returns before drawing when the list is empty, so this is the guard behind that
            // guard: a 2 px sliver of empty scroll view is not a dropdown.
            Assert.That(UnitPickerLayout.ViewportHeight(25f, 0, 300f), Is.EqualTo(0f));
            Assert.That(UnitPickerLayout.VisibleRows(25f, 0, 300f), Is.EqualTo(0));
        }

        [Test]
        public void Viewport_DoesNotDivideByZeroOnADegenerateRowAdvance()
        {
            // A style with neither font nor padding measures 0. Dividing by it would be an
            // ArithmeticException at draw time, i.e. an exception inside OnGUI on every single frame.
            //
            // The assertion is that the result is FINITE and IN RANGE, not a particular row count: the
            // degrade is to a 1 px row, so all ten rows fit in 300 px and `10` is the right answer. The
            // first version of this test asserted 1 and failed -- the expectation was wrong, not the
            // code, and pinning a count here would have pinned the wrong thing.
            foreach (float advance in new[] { 0f, -1f })
            {
                int rows = UnitPickerLayout.VisibleRows(advance, 10, 300f);

                Assert.That(rows, Is.InRange(1, 10), $"advance={advance} must degrade, not divide by zero");
                Assert.That(UnitPickerLayout.ViewportHeight(advance, 10, 300f), Is.GreaterThan(0f),
                    $"advance={advance} must still yield a drawable viewport");
            }
        }
    }
}
