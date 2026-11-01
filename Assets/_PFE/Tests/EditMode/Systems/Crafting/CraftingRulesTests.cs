using System.Collections.Generic;
using NUnit.Framework;
using PFE.Systems.Crafting;
using PFE.Systems.Inventory;
using PFE.Systems.Items;

namespace PFE.Tests.EditMode.Systems.Crafting
{
    /// <summary>
    /// Pins the three decisions the workbench makes without a Unity runtime: <b>which recipes a station
    /// lists</b>, <b>which rows of that list survive</b>, and <b>whether a click may run</b>.
    ///
    /// <para><b>Why these live in <c>PFE.Sim</c> at all.</b> The page cannot be exercised offline (it
    /// needs a canvas) and the inventory service needs a player. The rules were therefore written as
    /// pure functions over ids and counts, so the parts that are easy to get subtly wrong — the
    /// <c>expl</c>-at-<c>work</c> station clause, the three different skip rules, the vault-inclusive
    /// material test — are testable with a few strings and no engine.</para>
    ///
    /// <para><b>Every fixture string is copied from the shipped file</b>, with its line cited, so a
    /// changed document shows up here as a red test rather than as a silent behaviour change.</para>
    /// </summary>
    public class CraftingRulesTests
    {
        // ── Fixtures, verbatim from AllData.as ────────────────────────────────

        /// <summary><c>AllData.as:4372-4375</c> — a workbench weapon recipe, two materials.</summary>
        private const string HswordRow =
            "<item id='s_hsword' tip='scheme' cat='weapon' work='work' chance='1' stage='1' price='100' " +
            "skill='repair' lvl='1'>\n" +
            "\t<craft id='strap' kol='1'/>\n" +
            "\t<craft id='fblade' kol='1'/>\n" +
            "</item>";

        /// <summary><c>AllData.as</c> — an explosive recipe, 5 scrap.</summary>
        private const string GrenadeRow =
            "<item id='s_hgren' tip='scheme' cat='weapon' work='expl' chance='1' price='500' " +
            "skill='explosives' lvl='1'>\n" +
            "\t<craft id='can' kol='1'/>\n" +
            "\t<craft id='detonat' kol='1'/>\n" +
            "\t<craft id='powder' kol='1'/>\n" +
            "\t<craft id='scrap' kol='5'/>\n" +
            "</item>";

        /// <summary>A lab recipe with the perk that doubles its output.</summary>
        private const string PotionRow =
            "<item id='s_potion_fly' tip='scheme' perk='potmaster' work='lab' chance='1' stage='7' " +
            "price='3200' skill='survival' lvl='5'>\n" +
            "\t<craft id='herb' kol='2'/>\n" +
            "</item>";

        /// <summary>A stove recipe — <c>cat</c> is absent on all 44 stove/lab rows.</summary>
        private const string SoupRow =
            "<item id='s_soup' tip='scheme' tip2='co' chance2='1' work='stove' chance='0' price='500' " +
            "skill='survival' lvl='3'>\n" +
            "\t<craft id='meat' kol='1'/>\n" +
            "</item>";

        private static CraftingRecipe Recipe(string allData)
        {
            List<CraftingRecipe> recipes = CraftingRecipeTable.Build(allData);
            Assert.That(recipes, Has.Count.EqualTo(1), "the fixture must produce exactly one recipe");
            return recipes[0];
        }

        // ── C1: the material reader ───────────────────────────────────────────

        [Test]
        public void ParseCraftRows_ReadsEveryMaterialWithItsCount()
        {
            ItemXmlRow row = ItemXmlRows.Parse(GrenadeRow)[0];
            List<CraftMaterialRow> materials = ItemXmlRows.ParseCraftRows(row.Body);

            Assert.That(materials, Has.Count.EqualTo(4));
            Assert.That(materials[0].Id, Is.EqualTo("can"));
            Assert.That(materials[3].Id, Is.EqualTo("scrap"));
            Assert.That(materials[3].Count, Is.EqualTo(5),
                "scrap is the one multi-unit material in this row; a reader that dropped @kol " +
                "would make a grenade cost one scrap instead of five.");
        }

        [Test]
        public void ParseCraftRows_OnASkillRowBody_FindsNothing()
        {
            // The 32 `<sk>` rows and the 393 self-closing rows share this reader; neither has a
            // <craft> child, and neither may produce a phantom material.
            List<CraftMaterialRow> materials = ItemXmlRows.ParseCraftRows(
                "\n\t<sk id='possLockPick' v1='1'/>\n");
            Assert.That(materials, Is.Empty);
        }

        [Test]
        public void ParseCraftRows_WithoutKol_DefaultsToOne_NotZero()
        {
            // AS3 reads `_loc2_.@kol` directly, so an absent attribute would compare as 0 and the
            // gate would always pass while removing nothing — a free recipe. 1 cannot do that.
            List<CraftMaterialRow> materials = ItemXmlRows.ParseCraftRows("<craft id='tape'/>");
            Assert.That(materials, Has.Count.EqualTo(1));
            Assert.That(materials[0].Count, Is.EqualTo(1));
        }

        // ── C2: the table and the station clause ──────────────────────────────

        [Test]
        public void Build_StripsTheSchemePrefixToNameTheProduct()
        {
            CraftingRecipe recipe = Recipe(HswordRow);
            Assert.That(recipe.SchemeId, Is.EqualTo("s_hsword"));
            Assert.That(recipe.OutputId, Is.EqualTo("hsword"),
                "PipPageWork.as:89 — `wid = s.substr(2)`; every inventory lookup is keyed on this.");
            Assert.That(recipe.Station, Is.EqualTo("work"));
            Assert.That(recipe.Skill, Is.EqualTo("repair"));
            Assert.That(recipe.SkillLevel, Is.EqualTo(1));
            Assert.That(recipe.OutputCount, Is.EqualTo(1), "@kol is absent, and its default is 1");
        }

        [Test]
        public void Build_IgnoresRowsThatAreNotSchemes()
        {
            Assert.That(CraftingRecipeTable.Build("<item id='strap' tip='compw'/>"), Is.Empty);
        }

        [Test]
        public void Station_MatchesItsOwnStation()
        {
            Assert.That(CraftingRecipeTable.StationAccepts("work", "work"), Is.True);
            Assert.That(CraftingRecipeTable.StationAccepts("lab", "lab"), Is.True);
            Assert.That(CraftingRecipeTable.StationAccepts("stove", "stove"), Is.True);
            Assert.That(CraftingRecipeTable.StationAccepts("expl", "expl"), Is.True);
        }

        [Test]
        public void Station_RejectsAnotherStationsRecipe()
        {
            Assert.That(CraftingRecipeTable.StationAccepts("lab", "work"), Is.False);
            Assert.That(CraftingRecipeTable.StationAccepts("work", "stove"), Is.False);
        }

        /// <summary>
        /// <c>PipPageWork.as:82</c> — <c>node.@work == "expl" &amp;&amp; pip.workTip == "work"</c>.
        /// There is no world object with <c>allact='expl'</c> (<c>AllData.as:5013-5015</c> has exactly
        /// <c>work</c>, <c>himlab</c> and <c>stove</c>), so without this clause 11 of the 75 recipes are
        /// unreachable in a way no menu would show.
        /// </summary>
        [Test]
        public void Station_WorkAlsoListsExplosives()
        {
            Assert.That(CraftingRecipeTable.StationAccepts("expl", "work"), Is.True);
            Assert.That(CraftingRecipeTable.StationAccepts("work", "expl"), Is.False,
                "the clause is one-way: a portable explosive bench does not list weapon recipes");
        }

        [Test]
        public void Station_PortableBenchesAreNormalisedFirst()
        {
            Assert.That(CraftingRecipeTable.NormaliseStation("mworklab"), Is.EqualTo("lab"));
            Assert.That(CraftingRecipeTable.NormaliseStation("mworkexpl"), Is.EqualTo("expl"));
            Assert.That(CraftingRecipeTable.StationAccepts("lab", "mworklab"), Is.True,
                "PipPageWork.as:40-47 rewrites the alias before anything reads the station");
            Assert.That(CraftingRecipeTable.StationAccepts("expl", "mworkexpl"), Is.True);
        }

        [Test]
        public void Station_PortableWorkbenchListsNothing()
        {
            // PipPageWork.as:49-53 — `mworkbench` is the repair-only bench; it never builds.
            Assert.That(CraftingRecipeTable.StationAccepts("work", "mworkbench"), Is.False);
            Assert.That(CraftingRecipeTable.StationAccepts("expl", "mworkbench"), Is.False);
        }

        [Test]
        public void Station_EmptyStationIsListedEverywhere()
        {
            // `node.@work.length() == 0` — unreachable on shipped data (all 75 carry @work) but the
            // oracle's first clause, and a data shape the port must not silently drop.
            Assert.That(CraftingRecipeTable.StationAccepts(string.Empty, "stove"), Is.True);
        }

        // ── C3: output count, row admission, the gate ─────────────────────────

        [Test]
        public void OutputCount_PotmasterDoublesOnlyWhenThePerkIsHeld()
        {
            CraftingRecipe potion = Recipe(PotionRow);
            Assert.That(CraftingRecipeTable.OutputCount(potion, false), Is.EqualTo(1));
            Assert.That(CraftingRecipeTable.OutputCount(potion, true), Is.EqualTo(2));
        }

        [Test]
        public void OutputCount_PotmasterDoesNotDoubleAnUnmarkedRecipe()
        {
            CraftingRecipe soup = Recipe(SoupRow);
            Assert.That(CraftingRecipeTable.OutputCount(soup, true), Is.EqualTo(1),
                "the perk test is `@perk == \"potmaster\"`, not `perk held`");
        }

        private static CraftTargetFacts Weapon(int tip, WeaponRespect respect, int have = 0)
            => new CraftTargetFacts(true, false, tip, respect, 0, string.Empty, false, have);

        private static CraftTargetFacts Armor(int level)
            => new CraftTargetFacts(false, true, 0, WeaponRespect.Active, level, string.Empty, false, 0);

        private static CraftTargetFacts Item(string tip, bool one, int have)
            => new CraftTargetFacts(false, false, 0, WeaponRespect.Active, 0, tip, one, have);

        [Test]
        public void Admit_ListsASchemeOnlyWeapon()
        {
            CraftRowView? row = CraftingRules.Admit(Recipe(HswordRow), Weapon(2, WeaponRespect.SchemeOnly), 5);
            Assert.That(row.HasValue, Is.True);
            Assert.That(row.Value.Kind, Is.EqualTo(CraftRowKind.Weapon));
            Assert.That(row.Value.Ok, Is.EqualTo(1));
        }

        [Test]
        public void Admit_SkipsAWeaponThePlayerAlreadyCarries()
        {
            // PipPageWork.as:92 — `respect == 3 || tip == 4`. An active weapon is not a craft target.
            Assert.That(CraftingRules.Admit(Recipe(HswordRow), Weapon(2, WeaponRespect.Active), 5), Is.Null);
        }

        [Test]
        public void Admit_ListsAThrownWeaponEvenWhenCarried()
        {
            // tip 4 stacks as items, so the recipe's job is to add another grenade (:92, :457-464).
            CraftRowView? row = CraftingRules.Admit(Recipe(GrenadeRow), Weapon(4, WeaponRespect.Active, 7), 5);
            Assert.That(row.HasValue, Is.True);
            Assert.That(row.Value.Have, Is.EqualTo(7));
        }

        [Test]
        public void Admit_ListsAnUnbuiltArmourAndSkipsABuiltOne()
        {
            Assert.That(CraftingRules.Admit(Recipe(HswordRow), Armor(-1), 5).HasValue, Is.True);
            Assert.That(CraftingRules.Admit(Recipe(HswordRow), Armor(0), 5), Is.Null);
        }

        [Test]
        public void Admit_SkipsAnImplantThePlayerAlreadyHas()
        {
            Assert.That(CraftingRules.Admit(Recipe(HswordRow), Item("impl", false, 0), 5).HasValue, Is.True);
            Assert.That(CraftingRules.Admit(Recipe(HswordRow), Item("impl", false, 1), 5), Is.Null);
        }

        [Test]
        public void Admit_SkipsAOneOffThePlayerAlreadyHas()
        {
            // `(tip == impl || @one > 0) && kol > 0` (:128).
            Assert.That(CraftingRules.Admit(Recipe(HswordRow), Item("item", true, 1), 5), Is.Null);
            Assert.That(CraftingRules.Admit(Recipe(HswordRow), Item("item", false, 3), 5).HasValue, Is.True,
                "a stackable item stays listed however many are held");
        }

        [Test]
        public void Admit_SkipsAProductNoTableKnows()
        {
            Assert.That(CraftingRules.Admit(Recipe(HswordRow), Item(string.Empty, false, 0), 5), Is.Null);
        }

        [Test]
        public void Admit_MarksASkillShortRowTwoRatherThanHidingIt()
        {
            // PipPageWork.as:85-88 — `ok = 2`, the row stays and draws at half alpha (:299-302).
            CraftRowView? row = CraftingRules.Admit(Recipe(HswordRow), Weapon(2, WeaponRespect.SchemeOnly), 0);
            Assert.That(row.HasValue, Is.True);
            Assert.That(row.Value.Ok, Is.EqualTo(2));
        }

        [Test]
        public void SortRows_PutsCraftableFirstThenByTheStringKey()
        {
            var rows = new List<CraftRowView>
            {
                new CraftRowView("c", CraftRowKind.Item, 2, "survival1", 0),
                new CraftRowView("a", CraftRowKind.Item, 1, "repair10", 0),
                new CraftRowView("b", CraftRowKind.Item, 1, "repair1", 0),
            };

            CraftingRules.SortRows(rows);

            // `ok` first (PipPageWork.as:149), so the two craftable rows lead.
            Assert.That(rows[2].Id, Is.EqualTo("c"), "ok 2 — not enough skill — sorts last");

            // Then the key as TEXT. `sort` is `node.@skill + node.@lvl` (:99) — "repair" and "10"
            // concatenated — so "repair1" is a prefix of "repair10" and comes first. A numeric
            // compare would put these the other way round; that is the trap this fixture pins.
            Assert.That(rows[0].Id, Is.EqualTo("b"));
            Assert.That(rows[1].Id, Is.EqualTo("a"));
        }

        [Test]
        public void SortRows_BreaksATieOnTheIdSoARepaintCannotReshuffle()
        {
            // The oracle leaves equal (ok, sort) pairs in Flash's unstable order. The port pins the id
            // so two identical-looking rows cannot swap under a hovering cursor between rebuilds.
            var rows = new List<CraftRowView>
            {
                new CraftRowView("z", CraftRowKind.Item, 1, "same", 0),
                new CraftRowView("m", CraftRowKind.Item, 1, "same", 0),
            };

            CraftingRules.SortRows(rows);

            Assert.That(rows[0].Id, Is.EqualTo("m"));
            Assert.That(rows[1].Id, Is.EqualTo("z"));
        }

        [Test]
        public void Check_SkillComesBeforeMaterials()
        {
            // The oracle tests the skill first (:374) and returns before looking at the bag (:379).
            CraftingRecipe recipe = Recipe(HswordRow);
            CraftRefusal refusal = CraftingRules.Check(recipe, 0, _ => 0);
            Assert.That(refusal, Is.EqualTo(CraftRefusal.NotEnoughSkill));
        }

        [Test]
        public void Check_CountsTheVaultInTheMaterialTest()
        {
            // PipPageWork.as:381 — `kol + vault >= kol`, with NO loc.base gate (unlike checkKol).
            CraftingRecipe recipe = Recipe(HswordRow);
            var stock = new Dictionary<string, int> { { "strap", 1 }, { "fblade", 1 } };
            Assert.That(CraftingRules.Check(recipe, 5, id => stock.TryGetValue(id, out int n) ? n : 0),
                Is.EqualTo(CraftRefusal.None));
        }

        [Test]
        public void Check_RefusesWhenOneMaterialIsShort()
        {
            CraftingRecipe recipe = Recipe(GrenadeRow);
            var stock = new Dictionary<string, int> { { "can", 1 }, { "detonat", 1 }, { "powder", 1 }, { "scrap", 4 } };
            Assert.That(CraftingRules.Check(recipe, 5, id => stock.TryGetValue(id, out int n) ? n : 0),
                Is.EqualTo(CraftRefusal.MissingMaterials),
                "5 scrap are needed; 4 must not pass");
        }

        [Test]
        public void CanCraftWeapon_MatchesTheListsOwnRule()
        {
            Assert.That(CraftingRules.CanCraftWeapon(4, WeaponRespect.Active), Is.True);
            Assert.That(CraftingRules.CanCraftWeapon(2, WeaponRespect.SchemeOnly), Is.True);
            Assert.That(CraftingRules.CanCraftWeapon(2, WeaponRespect.Active), Is.False);
        }

        // ── the real file, when it is there ───────────────────────────────────

        /// <summary>
        /// The census over the shipped document: <b>75</b> recipes, split <b>20 work / 23 lab / 21 stove /
        /// 11 expl</b>.
        ///
        /// <para><b>Why the synthetic fixtures above are not enough.</b> They prove the rules, not the
        /// <i>reader</i>. A drifted <c>&lt;craft&gt;</c> regex, a body capture that stops at the first
        /// self-closing child, or a station attribute read off the wrong element all pass every fixture
        /// and fail here. The expected numbers were written down before the run (they come from
        /// <c>AllData.as</c>, counted independently of this parser).</para>
        ///
        /// <para><b>The split is load-bearing, not trivia.</b> <c>expl</c> has <b>no world object</b> —
        /// there is no <c>allact='expl'</c> anywhere in the file — so those 11 recipes are reachable
        /// only through the <c>@work='expl' &amp;&amp; workTip=='work'</c> clause. If the split drifts,
        /// explosives have either vanished or become craftable at a stove.</para>
        /// </summary>
        [Test]
        public void ParsingTheRealAllData_BuildsAll75RecipesAtTheirOwnStations()
        {
            string path = FindAllDataAs();
            if (path == null)
            {
                Assert.Ignore(
                    "AllData.as is not at the project root (it is an untracked working copy). The " +
                    "shape tests above still ran; this census did not.");
                return;
            }

            List<CraftingRecipe> recipes = CraftingRecipeTable.Build(System.IO.File.ReadAllText(path));

            Assert.That(recipes.Count, Is.EqualTo(75),
                "75 <item tip='scheme'> rows. A body leak into a non-scheme row would push this up.");

            var byStation = new Dictionary<string, int>();
            int gated = 0;
            foreach (CraftingRecipe r in recipes)
            {
                string station = r.Station ?? string.Empty;
                byStation.TryGetValue(station, out int n);
                byStation[station] = n + 1;

                Assert.That(r.OutputId, Is.Not.Empty, r.SchemeId + " has no product");
                Assert.That(r.OutputId.StartsWith("s_", System.StringComparison.Ordinal), Is.False,
                    r.SchemeId + " kept its prefix — the product would be a schematic, not an item");
                Assert.That(r.Materials.Length, Is.GreaterThan(0),
                    r.SchemeId + " parsed to no materials — a recipe that costs nothing");
                if (r.HasSkillGate) gated++;
            }

            Assert.That(byStation["work"], Is.EqualTo(20), "workbench-only recipes");
            Assert.That(byStation["lab"], Is.EqualTo(23), "laboratory recipes");
            Assert.That(byStation["stove"], Is.EqualTo(21), "stove recipes");
            Assert.That(byStation["expl"], Is.EqualTo(11),
                "explosive recipes — the ones with no world object of their own");

            // All 75 carry `skill`, but 3 carry `lvl='0'`, which makes the oracle's `level < 0` guard
            // inert — they are craftable by anyone. An "every scheme is gated" assumption is wrong, and
            // it is wrong in a way that silently locks players out of three recipes if it is coded.
            Assert.That(gated, Is.EqualTo(72), "75 rows minus s_arson, s_rollup and s_frmeat");
            var inert = new Dictionary<string, string>
            {
                { "s_arson", "repair" },   // workbench, lvl='0'
                { "s_rollup", "survival" }, // lab, lvl='0'
                { "s_frmeat", "survival" }, // stove, lvl='0'
            };
            foreach (KeyValuePair<string, string> free in inert)
            {
                CraftingRecipe? row = CraftingRecipeTable.Find(recipes, free.Key);
                Assert.That(row.HasValue, Is.True, free.Key + " is missing from the table");
                Assert.That(row.Value.Skill, Is.EqualTo(free.Value),
                    free.Key + " — the skill is still named even though the gate is inert");
                Assert.That(row.Value.SkillLevel, Is.EqualTo(0), free.Key + " — lvl='0'");
                Assert.That(row.Value.HasSkillGate, Is.False,
                    free.Key + " must be craftable at skill 0");
            }

            // The one recipe the fixtures also name, so a parser that returns 75 empty shells fails.
            CraftingRecipe? hsword = CraftingRecipeTable.Find(recipes, "s_hsword");
            Assert.That(hsword.HasValue, Is.True);
            Assert.That(hsword.Value.OutputId, Is.EqualTo("hsword"));
            Assert.That(hsword.Value.Station, Is.EqualTo("work"));
            Assert.That(hsword.Value.SkillLevel, Is.EqualTo(1));
            Assert.That(hsword.Value.Materials.Length, Is.EqualTo(2));

            // And the reachability rule that the 11 expl rows depend on.
            Assert.That(CraftingRecipeTable.StationAccepts("expl", "work"), Is.True,
                "the workbench builds explosives (PipPageWork.as:82)");
            Assert.That(CraftingRecipeTable.StationAccepts("expl", "stove"), Is.False,
                "a stove must not");
        }

        /// <summary>
        /// The untracked working copy sits at the project root; the oracle copy may sit beside it. Walks
        /// up from the assembly so this works from both the editor and an offline harness.
        /// </summary>
        private static string FindAllDataAs()
        {
            string dir = System.IO.Path.GetDirectoryName(
                typeof(CraftingRulesTests).Assembly.Location);

            for (int i = 0; i < 12 && !string.IsNullOrEmpty(dir); i++)
            {
                string candidate = System.IO.Path.Combine(dir, "AllData.as");
                if (System.IO.File.Exists(candidate)) return candidate;
                dir = System.IO.Path.GetDirectoryName(dir);
            }

            foreach (string candidate in new[]
                     {
                         System.IO.Path.Combine("AllData.as"),
                         System.IO.Path.Combine("..", "pfe", "scripts", "fe", "AllData.as"),
                         System.IO.Path.Combine("..", "..", "pfe", "scripts", "fe", "AllData.as"),
                     })
            {
                if (System.IO.File.Exists(candidate)) return System.IO.Path.GetFullPath(candidate);
            }

            return null;
        }
    }

    /// <summary>
    /// Pins the repair and upgrade arithmetic — <c>PipPageWork.as:554-613</c>,
    /// <c>Invent.repWeapon</c> (<c>Invent.as:857-876</c>), <c>Armor.needComp</c>
    /// (<c>Armor.as:350-357</c>) and the armour row's <c>@comp</c>/<c>@kolcomp</c>/<c>upd@kol</c>.
    /// </summary>
    public class RepairAndUpgradeRulesTests
    {
        /// <summary><c>AllData.as</c> — <c>metal</c>, verbatim: no <c>@comp</c> (so the fallback applies),
        /// <c>kolcomp='4'</c>, and three <c>&lt;upd&gt;</c> rows where levels 1 and 2 carry
        /// <c>kol='15'</c>/<c>'20'</c>.</summary>
        private const string MetalArmor =
            "<all>\n" +
            "<armor id='metal' sort='3' hp='5000' price='4000' lvl='2' kolcomp='4'>\n" +
            "\t<upd armor='12' qual='0.5' bul='0.2'/>\n" +
            "\t<upd armor='14' qual='0.6' bul='0.2' kol='15'/>\n" +
            "\t<upd armor='16' qual='0.7' bul='0.2' kol='20'/>\n" +
            "</armor>\n" +
            "</all>";

        /// <summary>A row that names its own component.</summary>
        private const string SapperArmor =
            "<all><armor id='sapper' hp='5000' comp='metal_comp' lvl='2' kolcomp='6'>" +
            "<upd armor='10'/><upd armor='12' kol='9'/></armor></all>";

        // ── Repair ────────────────────────────────────────────────────────────

        [Test]
        public void WeaponRepair_ExcludesTipZeroAndThrownAndHidden()
        {
            Assert.That(RepairRules.IsWeaponRepairable(2, WeaponRespect.Active, 50f, 100f), Is.True);
            Assert.That(RepairRules.IsWeaponRepairable(0, WeaponRespect.Active, 50f, 100f), Is.False,
                "tip 0 is the base Weapon class — turrets and drone lasers");
            Assert.That(RepairRules.IsWeaponRepairable(4, WeaponRespect.Active, 50f, 100f), Is.False,
                "tip 4 is thrown");
            Assert.That(RepairRules.IsWeaponRepairable(2, WeaponRespect.Hidden, 50f, 100f), Is.False);
            Assert.That(RepairRules.IsWeaponRepairable(2, WeaponRespect.Active, 100f, 100f), Is.False,
                "an undamaged weapon is not listed");
        }

        [Test]
        public void WeaponRepair_AmountIsMaxHpTimesMultiplierTimesRepEffTimesAQuarter()
        {
            // Invent.as:864 — `maxhp * repairMult * rep_eff * 0.25`.
            Assert.That(RepairRules.WeaponRepairAmount(200f, 0.25f, 1f), Is.EqualTo(12.5f).Within(0.001f));
            Assert.That(RepairRules.WeaponRepairAmount(200f, 0.5f, 2f), Is.EqualTo(50f).Within(0.001f));
        }

        [Test]
        public void WeaponRepair_RefusesAZeroRepEff()
        {
            // Invent.as:860 — `rep_eff > 0`. Four shipped <com> rows carry rep='0'.
            Assert.That(RepairRules.CanRepairWeapon(2, 0f), Is.False);
            Assert.That(RepairRules.CanRepairWeapon(2, 1f), Is.True);
            Assert.That(RepairRules.CanRepairWeapon(4, 1f), Is.False);
        }

        [Test]
        public void ArmorRepair_ExcludesNoRepairAndIndestructible()
        {
            Assert.That(RepairRules.IsArmorRepairable(false, false, 50f, 100f), Is.True);
            Assert.That(RepairRules.IsArmorRepairable(true, false, 50f, 100f), Is.False);
            Assert.That(RepairRules.IsArmorRepairable(false, true, 50f, 100f), Is.False);
        }

        [Test]
        public void ArmorRepair_AmountIsMaxHpTimesMultiplierDividedByKolComp()
        {
            // PipPageWork.as:568 — `arm.repair(arm.maxhp * gg.pers.repairMult / arm.kolComp)`.
            Assert.That(RepairRules.ArmorRepairAmount(5000f, 0.25f, 4), Is.EqualTo(312.5f).Within(0.001f));
            Assert.That(RepairRules.ArmorRepairAmount(5000f, 0.25f, 0), Is.EqualTo(1250f).Within(0.001f),
                "kolComp 0 is clamped to 1 rather than dividing by zero");
        }

        [Test]
        public void DisplayPercent_IsOneDecimalRounded()
        {
            // PipPageWork.as:313 — `Math.round(rep * repairMult * 1000) / 10`.
            Assert.That(RepairRules.DisplayPercent(0.25f, 0.25f), Is.EqualTo(6.3f).Within(0.001f));
            Assert.That(RepairRules.DisplayPercent(0.25f, 1f), Is.EqualTo(25f).Within(0.001f));
        }

        // ── Upgrade ───────────────────────────────────────────────────────────

        [Test]
        public void ArmorUpgrade_NeedsAPerkLevelAndRoomToGrow()
        {
            Assert.That(UpgradeRules.CanUpgradeArmor(0, 2, 1), Is.True);
            Assert.That(UpgradeRules.CanUpgradeArmor(0, 2, 0), Is.False,
                "maxArmorLvl 0 is the oracle's default — the tab is empty without the perk");
            Assert.That(UpgradeRules.CanUpgradeArmor(-1, 2, 2), Is.False,
                "an unbuilt armour is not upgraded, it is built");
            Assert.That(UpgradeRules.CanUpgradeArmor(2, 2, 2), Is.False, "already at the ceiling");
        }

        [Test]
        public void WeaponUpgrade_IsScrapGunsOnlyAtTierZero()
        {
            Assert.That(UpgradeRules.CanUpgradeWeapon(3, 0, WeaponRespect.Active), Is.True);
            Assert.That(UpgradeRules.CanUpgradeWeapon(2, 0, WeaponRespect.Active), Is.False,
                "skill 2 is a small gun — not a workbench product");
            Assert.That(UpgradeRules.CanUpgradeWeapon(3, 1, WeaponRespect.Active), Is.False,
                "already tier II");
            Assert.That(UpgradeRules.CanUpgradeWeapon(3, 0, WeaponRespect.SchemeOnly), Is.False);
        }

        // ── The armour row's crafting attributes ──────────────────────────────

        [Test]
        public void ArmourCraftTable_FallsBackToIdPlusComp()
        {
            Assert.That(ArmourCraftTable.TryRead(MetalArmor, "metal", out ArmourCraftFacts facts), Is.True);
            Assert.That(facts.ComponentId, Is.EqualTo("metal_comp"),
                "Armor.as:147-151 — `@comp`, else `id + \"_comp\"`");
            Assert.That(facts.ComponentCount, Is.EqualTo(4), "kolcomp");
        }

        [Test]
        public void ArmourCraftTable_ReadsEachLevelsComponentCost()
        {
            ArmourCraftTable.TryRead(MetalArmor, "metal", out ArmourCraftFacts facts);
            Assert.That(facts.ComponentCountForNextLevel(0), Is.EqualTo(15));
            Assert.That(facts.ComponentCountForNextLevel(1), Is.EqualTo(20));
            Assert.That(facts.ComponentCountForNextLevel(2), Is.EqualTo(0),
                "no level 3 — needComp() returns 0 rather than wrapping");
        }

        [Test]
        public void ArmourCraftTable_PrefersTheDeclaredComponent()
        {
            ArmourCraftTable.TryRead(SapperArmor, "sapper", out ArmourCraftFacts facts);
            Assert.That(facts.ComponentId, Is.EqualTo("metal_comp"));
            Assert.That(facts.ComponentCount, Is.EqualTo(6));
            Assert.That(facts.ComponentCountForNextLevel(0), Is.EqualTo(9));
        }

        [Test]
        public void ArmourCraftTable_MissingRowIsReportedNotDefaulted()
        {
            Assert.That(ArmourCraftTable.TryRead(MetalArmor, "nonesuch", out _), Is.False);
        }
    }
}
