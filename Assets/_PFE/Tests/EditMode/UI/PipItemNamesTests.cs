using System.IO;
using NUnit.Framework;
using PFE.Tests.Editor.Common;
using PFE.UI.PipBuck;

namespace PFE.Tests.EditMode.UI
{
    /// <summary>
    /// Pins <see cref="PipItemNames"/> — the port of the name half of <c>fe.serv.Item</c>'s constructor
    /// (<c>Item.as:206-267</c>).
    ///
    /// <para><b>Why this needs a fixture at all.</b> The Oracle has no name on an inventory row: it
    /// <i>assigns</i> <c>Item.nazv</c> from the localisation document by a rule that branches five ways.
    /// The port's <c>ItemDefinition.displayName</c> is a different field whose C# initialiser is the
    /// literal <c>"Item Name"</c> and which the importer never writes — so all 500 shipped item assets
    /// carry the placeholder. A play-test reported the result as "items have no name", which is what a
    /// list of 500 identical strings looks like. Five branches and a fallback chain is exactly the kind
    /// of code a compile cannot check, so it lives in a Unity-free static class and is checked here.</para>
    ///
    /// <para><b>Two halves, and the split matters.</b> The first runs against a small inline document
    /// and pins the <i>rules</i>: which table each kind is named from, the <c>@base</c> and <c>@mod</c>
    /// variant rule, the <c>^variant</c> rule, the schematic override, and the fallback order. The
    /// second runs against the <b>shipped <c>text_en.xml</c></b> and pins the <i>data</i>: that the real
    /// rows in the real document come out as the strings a player reads. A rule fixture that never sees
    /// the shipped document cannot notice that a table alias was wrong; a data fixture cannot notice
    /// that a branch is wrong.</para>
    ///
    /// <para><b>Why the file is read from disk.</b> <c>Resources.Load</c> is a Unity ECall and throws
    /// outside the editor, which is how this project's offline harness runs its fixtures.
    /// <c>SourceLint.ProjectRoot</c> already solves that for the lints; this reuses it rather than
    /// inventing a second way to find the repository — the same arrangement as <c>PipTextTableTests</c>.</para>
    /// </summary>
    [TestFixture]
    public class PipItemNamesTests
    {
        // ── The inline document ───────────────────────────────────────────────
        //
        // Deliberately NOT the shipped file. Every assertion below is about one branch of the rule, and
        // a branch test that runs against 2000 entries cannot say which entry it depended on. The ids
        // here are chosen so that each branch is reachable and every other branch is not — e.g. `w1^1`
        // exists while `w1^2` does not, so the "variant with its own row" and the " - II" fallback are
        // separated by one character.

        private const string InlineXml = @"<all>
  <weapon id=""w1""><n>Base gun</n></weapon>
  <weapon id=""w1^1""><n>Gun mark two</n></weapon>
  <weapon id=""boom""><n>Boom stick</n></weapon>
  <armor id=""plate""><n>Plate mail</n></armor>
  <item id=""round""><n>Round</n></item>
  <item id=""named""><n>A named thing</n></item>
  <item id=""s_named""><n>Schematics for a thing</n></item>
  <item id=""target""><n>The target thing</n></item>
  <item id=""labtarget""><n>The lab thing</n></item>
  <pip id=""am_1""><n>ap</n></pip>
  <pip id=""scheme1""><n>Scheme</n></pip>
  <pip id=""recipe""><n>Recipe</n></pip>
</all>";

        private static PipTextTable Inline() => PipTextTable.Parse(InlineXml);

        // ── The weapon table (Item.as:206-220) ────────────────────────────────

        [Test]
        public void Weapon_Plain_ReadsTheWeaponTable()
        {
            Assert.That(PipItemNames.Weapon(Inline(), "w1", 0, null), Is.EqualTo("Base gun"),
                "Item.as:208-210 — Res.txt(\"w\", id). The weapon table, never the item table.");
        }

        [Test]
        public void Weapon_VariantWithItsOwnRow_UsesThatRow()
        {
            Assert.That(PipItemNames.Weapon(Inline(), "w1", 1, null), Is.EqualTo("Gun mark two"),
                "Item.as:212-214 — `id + \"^\" + variant` is looked up first.");
        }

        [Test]
        public void Weapon_VariantWithNoRow_AppendsTheLiteralTwo()
        {
            Assert.That(PipItemNames.Weapon(Inline(), "w1", 2, null), Is.EqualTo("Base gun - II"),
                "Item.as:216-218 — the Oracle appends the literal \" - II\" rather than degrading to the " +
                "base name, so a player can tell the two apart.");
        }

        [Test]
        public void Weapon_ExplosiveIdInTheWeaponTable_Resolves()
        {
            // An explosive is a weapon row (`tip='e'`) and is named from the weapon table. See
            // Item_ExplosiveIsNamedFromTheWeaponTable for the item-side entry point.
            Assert.That(PipItemNames.Weapon(Inline(), "boom", 0, null), Is.EqualTo("Boom stick"));
        }

        // ── The armour table (Item.as:229-231) ────────────────────────────────

        [Test]
        public void Armor_ReadsTheArmorTable()
        {
            Assert.That(PipItemNames.Armor(Inline(), "plate", null), Is.EqualTo("Plate mail"),
                "Item.as:229-231 — Res.txt(\"a\", id). Armour is an <armor> element, never an <item> row.");
        }

        // ── The item table and the @base variant branch (Item.as:240-252) ─────

        [Test]
        public void Item_Plain_ReadsTheItemTable()
        {
            Assert.That(PipItemNames.Item(Inline(), "named", "stuff", "", 0, "", null),
                Is.EqualTo("A named thing"), "Item.as:252 — Res.txt(\"i\", id).");
        }

        [Test]
        public void Item_ExplosiveIsNamedFromTheWeaponTable()
        {
            // The branch that is easiest to get wrong: the row's own id has no <item> row (it is a
            // weapon row), so the item-table lookup misses and the answer has to come from the weapon
            // table. 22 of the 500 shipped rows are this shape.
            Assert.That(PipItemNames.Item(Inline(), "boom", PipItemNames.ExplosiveTip, "", 0, "", null),
                Is.EqualTo("Boom stick"),
                "Item.as:206-220 — `tip == L_EXPL` takes the weapon branch, and it is checked before the " +
                "item table so an explosive cannot miss on a table it was never in.");
        }

        [Test]
        public void Item_BaseBranch_NamesAfterTheBaseNotItself()
        {
            // `round_1` is the variant; `round` is the base. The name is the BASE's, because the
            // variant's own id has no row — that is the whole point of the branch.
            Assert.That(PipItemNames.Item(Inline(), "round_1", "a", "round", 0, "", null),
                Is.EqualTo("Round"),
                "Item.as:240-245 — a row carrying @base is named after the base item.");
        }

        [Test]
        public void Item_BaseBranch_AddsTheModifierSuffix()
        {
            Assert.That(PipItemNames.Item(Inline(), "round_1", "a", "round", 1, "", null),
                Is.EqualTo("Round (ap)"),
                "Item.as:247 — `\" (\" + Res.pipText(\"am_\" + @mod) + \")\"`.");
        }

        [Test]
        public void Item_BaseBranch_DropsTheSuffixWhenTheModifierHasNoRow()
        {
            // @mod = 7 has no `am_7` row. The suffix is dropped rather than filled with the Oracle's
            // missing-key marker (`*pip_am_7`), because this string is a name the player reads.
            Assert.That(PipItemNames.Item(Inline(), "round_1", "a", "round", 7, "", null),
                Is.EqualTo("Round"),
                "A missing `am_N` row must not put a marker inside a name.");
        }

        [Test]
        public void Item_BaseBranch_WithoutAModifierKeepsTheBareBaseName()
        {
            Assert.That(PipItemNames.Item(Inline(), "round_2", "a", "round", 0, "", null),
                Is.EqualTo("Round"),
                "Item.as:246 — `@mod == 0` is the bare base name, not \"Round ()\".");
        }

        // ── The schematic override (Item.as:257-267) ──────────────────────────

        [Test]
        public void Item_SchemeOverride_WorkbenchReadsScheme()
        {
            // The ids are `s_…` because the rule strips exactly TWO characters (`id.substr(2)`), so a
            // longer prefix would leave the remainder unresolved and silently take the fallback. The
            // shipped ids are `s_mod_metal`, `s_potion_stim`, … — the same shape.
            Assert.That(
                PipItemNames.Item(Inline(), "s_target", PipItemNames.SchemeTip, "", 0,
                    PipItemNames.WorkbenchStation, null),
                Is.EqualTo("Scheme «The target thing»"),
                "Item.as:262-266 — @work == \"work\" is a workbench schematic, and `id.substr(2)` strips " +
                "the two-character `s_` prefix.");
        }

        [Test]
        public void Item_SchemeOverride_OtherStationsReadRecipe()
        {
            Assert.That(
                PipItemNames.Item(Inline(), "s_labtarget", PipItemNames.SchemeTip, "", 0, "lab", null),
                Is.EqualTo("Recipe «The lab thing»"),
                "Every station other than the workbench (lab, stove, expl) makes a recipe.");
        }

        [Test]
        public void Item_SchemeOverride_DoesNotApplyWhenTheRowNamesItself()
        {
            // `s_named` has its own <item> row, so it is not renamed. This is what makes the branch an
            // *override* rather than a rename, and it is the gate at Item.as:257.
            Assert.That(
                PipItemNames.Item(Inline(), "s_named", PipItemNames.SchemeTip, "", 0,
                    PipItemNames.WorkbenchStation, null),
                Is.EqualTo("Schematics for a thing"),
                "Item.as:257 — gated on the item table NOT having this row's own id.");
        }

        [Test]
        public void Item_SchemeOverride_NeedsTheTargetToResolve()
        {
            // The `s_` prefix is stripped and the remainder is looked up. With no such row there is
            // nothing to name the scheme after, so the id is shown rather than a half-built string.
            Assert.That(
                PipItemNames.Item(Inline(), "s_absent", PipItemNames.SchemeTip, "", 0,
                    PipItemNames.WorkbenchStation, null),
                Is.EqualTo("s_absent"),
                "An unresolvable target must fall back, not print \"Scheme «»\".");
        }

        [Test]
        public void Item_SchemeOverride_GuardsTheTwoCharacterPrefix()
        {
            // AS3's `substr(2)` returns "" past the end; C#'s `Substring` throws. A two-character id
            // would take the whole rule down with an exception, so the guard is asserted rather than
            // assumed.
            Assert.DoesNotThrow(() =>
                PipItemNames.Item(Inline(), "s_", PipItemNames.SchemeTip, "", 0,
                    PipItemNames.WorkbenchStation, null));
            Assert.DoesNotThrow(() =>
                PipItemNames.Item(Inline(), "s", PipItemNames.SchemeTip, "", 0, "lab", null));
        }

        // ── The fallback chain (the class's own last resort) ──────────────────

        [Test]
        public void IsPlaceholder_IsTheShippedInitialiser()
        {
            Assert.That(PipItemNames.IsPlaceholder("Item Name"), Is.True);
            Assert.That(PipItemNames.IsPlaceholder(""), Is.True);
            Assert.That(PipItemNames.IsPlaceholder("   "), Is.True);
            Assert.That(PipItemNames.IsPlaceholder("Minigun"), Is.False);
            Assert.That(PipItemNames.PlaceholderName, Is.EqualTo("Item Name"),
                "The field's own C# initialiser — the string 500 shipped item assets carry.");
        }

        [Test]
        public void Miss_ReturnsTheDefinitionNameWhenItIsAName()
        {
            Assert.That(PipItemNames.Weapon(Inline(), "absent", 0, "A real name"),
                Is.EqualTo("A real name"));
        }

        [Test]
        public void Miss_NeverReturnsThePlaceholder()
        {
            // The whole reason this class exists. A placeholder that reaches the screen is
            // indistinguishable from a broken list, so it is refused even as a fallback.
            Assert.That(PipItemNames.Item(Inline(), "absent", "stuff", "", 0, "", PipItemNames.PlaceholderName),
                Is.EqualTo("absent"),
                "An id on screen is ugly; 500 identical \"Item Name\"s read as a layout fault.");
        }

        [Test]
        public void Miss_ReturnsEmptyForANullIdRatherThanThrowing()
        {
            Assert.That(PipItemNames.Item(Inline(), null, "stuff", "", 0, "", null), Is.EqualTo(string.Empty));
            Assert.That(PipItemNames.Weapon(Inline(), null, 0, null), Is.EqualTo(string.Empty));
            Assert.That(PipItemNames.Armor(Inline(), null, null), Is.EqualTo(string.Empty));
        }

        [Test]
        public void NullDocument_NeverThrowsAndNeverInventsAName()
        {
            // PipTextTable.Empty is the documented "no document" value, and a page built before the
            // document loads must still draw. Every lookup misses, so every name is the id.
            Assert.That(PipItemNames.Weapon(PipTextTable.Empty, "w1", 0, null), Is.EqualTo("w1"));
            Assert.That(PipItemNames.Armor(PipTextTable.Empty, "plate", null), Is.EqualTo("plate"));
            Assert.That(PipItemNames.Item(PipTextTable.Empty, "named", "stuff", "", 0, "", null),
                Is.EqualTo("named"));
            Assert.That(PipItemNames.Weapon(null, "w1", 0, null), Is.EqualTo("w1"));
        }

        [Test]
        public void EmptyTable_DoesNotPrintTheMissingKeyMarker()
        {
            // PipTextTable.Text reproduces the Oracle's `*table_id` marker on purpose — it is a debug
            // surface. A name is not: the marker reads as a name and hides the miss. TryGet is the
            // marker-free form, and this is the assertion that the port uses it.
            Assert.That(PipItemNames.Weapon(Inline(), "absent", 0, null), Is.EqualTo("absent"));
            Assert.That(PipItemNames.Weapon(Inline(), "absent", 0, null), Does.Not.Contain("*"));
        }

        // ── The shipped document ──────────────────────────────────────────────
        //
        // The rules above are asserted against a document small enough to reason about. These assert
        // that the REAL document, read by the REAL lookup, produces the strings a player reads. Every
        // expected value below was read out of `text_en.xml` / `AllData.as`, not guessed: an assertion
        // invented from memory would pin the port to a typo and pass.

        private static PipTextTable _shipped;

        private static PipTextTable Shipped
        {
            get
            {
                if (_shipped != null) return _shipped;

                string path = Path.Combine(
                    SourceLint.ProjectRoot(), "Assets", "_PFE", "Data", "Resources", "Localisation",
                    "text_en.xml");

                Assert.That(File.Exists(path), Is.True,
                    "The localisation document is missing: " + path + ". Every name in this fixture " +
                    "comes from it; without it the port falls back to raw ids.");

                _shipped = PipTextTable.Parse(File.ReadAllText(path));
                Assert.That(_shipped.IsEmpty, Is.False, "The document parsed to zero entries: " + path);
                return _shipped;
            }
        }

        [Test]
        public void Shipped_NamesTheMinigun()
        {
            // The weapon the play-test was holding while the inventory showed nothing.
            Assert.That(PipItemNames.Weapon(Shipped, "minigun", 0, null), Is.EqualTo("Minigun"));
        }

        [Test]
        public void Shipped_NamesAMinigunVariantFromItsOwnRow()
        {
            // `minigun^1` is a real row in the shipped weapon table — the branch that is easy to drop
            // because most variants have no row of their own.
            Assert.That(PipItemNames.Weapon(Shipped, "minigun", 1, null), Is.EqualTo("\"Gale\""),
                "Item.as:212-214 — the `id^variant` row wins over the \" - II\" fallback.");
        }

        [Test]
        public void Shipped_NamesAVariantWithNoRowThroughTheTwoSuffix()
        {
            Assert.That(PipItemNames.Weapon(Shipped, "r32", 2, null), Is.EqualTo(".32 pistol - II"),
                "No `r32^2` row exists in the shipped document, so the Oracle's \" - II\" applies.");
        }

        [Test]
        public void Shipped_NamesAnArmourPlateFromTheArmorTable()
        {
            // `assault` has NO <item> row at all — it is an <armor> element — so an item-table lookup
            // would miss and print the id. This is the assertion that the armour reader uses the right
            // table.
            Assert.That(PipItemNames.Armor(Shipped, "assault", null), Is.EqualTo("Stormtrooper armor"));
        }

        [Test]
        public void Shipped_NamesAnAmmoVariantThroughItsBaseAndModifier()
        {
            // `p10_1` has no row of its own; its `@base` is `p10` ("10 mm round") and its `@mod` is 1
            // ("ap"). All three facts are in the shipped data, and all three are load-bearing.
            Assert.That(PipItemNames.Item(Shipped, "p10_1", "a", "p10", 1, "", null),
                Is.EqualTo("10 mm round (ap)"));
            Assert.That(PipItemNames.Item(Shipped, "p10_2", "a", "p10", 2, "", null),
                Is.EqualTo("10 mm round (exp)"));
        }

        [Test]
        public void Shipped_NamesAnExplosiveFromTheWeaponTable()
        {
            // `hgren` is `tip='e'` and is in the weapon table only. Without the explosive branch it
            // would take the item-table path, miss, and print "hgren".
            Assert.That(PipItemNames.Item(Shipped, "hgren", PipItemNames.ExplosiveTip, "", 0, "", null),
                Is.EqualTo("Hoofmade grenade"));
        }

        [Test]
        public void Shipped_NamesAWorkbenchSchematicThroughTheOverride()
        {
            // `s_mod_metal` is `tip='scheme'`, has NO item row of its own, `@work='work'`, and its
            // `substr(2)` target `mod_metal` is "Metal detector". 52 of the 75 schematics take this
            // path, and this is one of them.
            Assert.That(
                PipItemNames.Item(Shipped, "s_mod_metal", PipItemNames.SchemeTip, "", 0,
                    PipItemNames.WorkbenchStation, null),
                Is.EqualTo("Scheme «Metal detector»"));
        }

        [Test]
        public void Shipped_NamesALabSchematicAsARecipe()
        {
            // Same branch, a non-workbench station: `@work='lab'` reads "Recipe", not "Scheme".
            Assert.That(
                PipItemNames.Item(Shipped, "s_potion_stim", PipItemNames.SchemeTip, "", 0, "lab", null),
                Is.EqualTo("Recipe «Super-stimulation potion»"));
        }

        [Test]
        public void Shipped_KeepsTheNameOfASchematicThatHasItsOwnRow()
        {
            // 23 of the 75 shipped schematics have their own <item> row, and the override must not touch
            // them. `s_owl` is chosen from those 23 because it is one of only TWO whose `substr(2)`
            // target (`owl`) also has a row — so the own-row gate is the ONLY thing stopping this from
            // resolving to "Scheme «Robo-owl»". This is the assertion that the gate is load-bearing on
            // shipped data, and the mutation control proves it (mutate_pipnames.py, M8).
            Assert.That(
                PipItemNames.Item(Shipped, "s_owl", PipItemNames.SchemeTip, "", 0,
                    PipItemNames.WorkbenchStation, null),
                Is.EqualTo("Robo-owl schematics"),
                "Item.as:257 — the override is gated on the item table not having this row's own id.");
        }

        [Test]
        public void Shipped_SchematicWhoseTargetHasNoRowKeepsItsOwnName()
        {
            // The other 21 of those 23, of which `s_hsword` is one: their `substr(2)` target has no row
            // either, so the override is blocked TWICE and the row's own name survives regardless of the
            // gate. Kept as a data assertion — it pins that the port does not fabricate a string out of
            // a missing target — and explicitly NOT as a gate test, because the own-row gate is redundant
            // here. Claiming it tested the gate is what made the M8 prediction over-wide.
            Assert.That(
                PipItemNames.Item(Shipped, "s_hsword", PipItemNames.SchemeTip, "", 0,
                    PipItemNames.WorkbenchStation, null),
                Is.EqualTo("Schematics for a hoofmade sword"));
            Assert.That(Shipped.Text("i", "owl"), Is.EqualTo("Robo-owl"),
                "The control for the test above: s_owl's target resolves, s_hsword's does not.");
        }

        [Test]
        public void Shipped_EveryExpectedNameIsNeverThePlaceholder()
        {
            // The property that matters, stated once and separately from the strings above: none of the
            // names this fixture pins may be the placeholder the assets ship with, and none may carry the
            // Oracle's missing-key marker.
            //
            // **The last row is the one that makes this a test of the FALLBACK CHAIN and not just of the
            // five branches.** Every other row resolves from the document, so `Finish`'s placeholder
            // guard is never reached — the mutation control (mutate_pipnames.py, M10) proved that by
            // dropping the guard and killing only the inline test. `no_such_weapon_id` misses on
            // purpose, with the placeholder as the fallback, so the guard is on the path.
            var names = new (string Label, string Name)[]
            {
                ("plain weapon", PipItemNames.Weapon(Shipped, "minigun", 0, PipItemNames.PlaceholderName)),
                ("weapon variant",
                    PipItemNames.Weapon(Shipped, "minigun", 1, PipItemNames.PlaceholderName)),
                ("variant with no row",
                    PipItemNames.Weapon(Shipped, "r32", 2, PipItemNames.PlaceholderName)),
                ("armour plate",
                    PipItemNames.Armor(Shipped, "assault", PipItemNames.PlaceholderName)),
                ("ammo variant",
                    PipItemNames.Item(Shipped, "p10_1", "a", "p10", 1, "",
                        PipItemNames.PlaceholderName)),
                ("explosive",
                    PipItemNames.Item(Shipped, "hgren", PipItemNames.ExplosiveTip, "", 0, "",
                        PipItemNames.PlaceholderName)),
                ("workbench schematic",
                    PipItemNames.Item(Shipped, "s_mod_metal", PipItemNames.SchemeTip, "", 0, "work",
                        PipItemNames.PlaceholderName)),
                ("a miss, with the placeholder as the fallback",
                    PipItemNames.Weapon(Shipped, "no_such_weapon_id", 0, PipItemNames.PlaceholderName)),
            };

            foreach ((string label, string name) in names)
            {
                Assert.That(PipItemNames.IsPlaceholder(name), Is.False,
                    "Placeholder reached the list (" + label + "): " + name);
                Assert.That(name, Does.Not.Contain("*"),
                    "A missing-key marker reads as a name (" + label + "): " + name);
            }

            // And the miss row must degrade to its id, which is the documented last resort.
            Assert.That(PipItemNames.Weapon(Shipped, "no_such_weapon_id", 0, PipItemNames.PlaceholderName),
                Is.EqualTo("no_such_weapon_id"));
        }
    }
}
