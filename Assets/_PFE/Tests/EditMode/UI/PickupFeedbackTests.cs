using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using PFE.Systems.Campaign;
using PFE.Systems.Inventory;
using PFE.Tests.Editor.Common;
using PFE.UI.Common;
using PFE.UI.HUD;
namespace PFE.Tests.EditMode.UI
{
    /// <summary>
    /// Pins the pickup-feedback rules — <see cref="InfoLogRules"/> (the port of <c>GUI.infoText</c> and
    /// its drain timer) and <see cref="PickupFeedbackRules"/> (the port of <c>Invent.take</c>'s tail).
    ///
    /// <para><b>Why this fixture exists in this shape.</b> The first draft of the colour rule was a
    /// single <c>tip</c>-keyed table, which is what the oracle's <i>catch-all arm</i> looks like if you
    /// read only that arm. The real chain is ten ordered arms, each with its own colour constant, and it
    /// is wrong in four places under the flat reading: weapon 5 not 1, scheme 7 not 6, ammunition 3,
    /// medicine 1, book 4. Nothing failed when it was wrong — the label was simply the wrong colour —
    /// which is exactly the class of defect a fixture catches and a play-test does not.</para>
    ///
    /// <para>Like <c>PipTextTableTests</c>, this has two halves: the <b>rules</b> against inline values,
    /// and the <b>data</b> against the shipped <c>text_en.xml</c>, because a rule fixture cannot notice
    /// that a key the port asks for was renamed.</para>
    /// </summary>
    [TestFixture]
    public class PickupFeedbackTests
    {
        // ── InfoLogRules: the message list ────────────────────────────────────

        [Test]
        public void InfoLog_KeepsAtMostSixLines()
        {
            var rules = new InfoLogRules();

            for (int i = 0; i < 9; i++)
            {
                rules.Add("line " + i);
            }

            Assert.That(rules.LineCount, Is.EqualTo(InfoLogRules.MaxLines),
                "GUI.as:1571 — `if (kolStr > 6) remStr()`.");

            // The oldest went first, so the survivor set is the last six.
            Assert.That(rules.Lines[0], Is.EqualTo("line 3"));
            Assert.That(rules.Lines[5], Is.EqualTo("line 8"));
        }

        [Test]
        public void InfoLog_RefusesAnImmediateRepeat()
        {
            var rules = new InfoLogRules();

            Assert.That(rules.Add("Found: Screwdriver"), Is.True);
            Assert.That(rules.Add("Found: Screwdriver"), Is.False,
                "GUI.as:1563 — `_loc5_ != this.prevInfoText`. Without this a held take key " +
                "scrolls six identical lines.");
            Assert.That(rules.LineCount, Is.EqualTo(1));
        }

        [Test]
        public void InfoLog_ForgetsTheRepeatAfterFiftyTicks()
        {
            var rules = new InfoLogRules();
            rules.Add("Found: Screwdriver");

            // t_info starts at 150 and the guard clears prevInfoText at 100 — fifty ticks later.
            for (int i = 0; i < InfoLogRules.HoldTicks - InfoLogRules.RepeatWindowTicks; i++)
            {
                rules.Step();
            }

            Assert.That(rules.PreviousLine, Is.Empty,
                "GUI.as:1957-1959 — `if (t_info == 100) prevInfoText = \"\"`.");

            Assert.That(rules.Add("Found: Screwdriver"), Is.True,
                "After the window the same message is accepted again.");
        }

        [Test]
        public void InfoLog_DrainsOneLinePerSixtyTicks()
        {
            var rules = new InfoLogRules();
            rules.Add("first");
            rules.Add("second");

            // Two lines, so the 150-tick hold expires once, drops one, and re-arms at 60.
            for (int i = 0; i < InfoLogRules.HoldTicks; i++) rules.Step();
            Assert.That(rules.LineCount, Is.EqualTo(1), "The 150-tick hold expired and one line went.");
            Assert.That(rules.Lines[0], Is.EqualTo("second"));

            for (int i = 0; i < InfoLogRules.DrainTicks; i++) rules.Step();
            Assert.That(rules.LineCount, Is.EqualTo(0), "The 60-tick re-arm drained the last line.");
        }

        [Test]
        public void InfoLog_RefusesAnEmptyLine()
        {
            var rules = new InfoLogRules();

            Assert.That(rules.Add(string.Empty), Is.False,
                "A missing localisation key must not consume one of the six slots.");
            Assert.That(rules.LineCount, Is.EqualTo(0));
        }

        // ── PickupFeedbackRules: which arm ────────────────────────────────────

        [Test]
        public void Classify_FollowsTheOracleBranchOrder()
        {
            // Invent.as:1060-1268, in order. `scheme` is tested BEFORE the sk catch-all, so a scheme
            // row that also carries skill children keeps the scheme arm.
            Assert.That(PickupFeedbackRules.Classify("weapon", false), Is.EqualTo(PickupKind.Weapon));
            Assert.That(PickupFeedbackRules.Classify("armor", false), Is.EqualTo(PickupKind.Armour));
            Assert.That(PickupFeedbackRules.Classify("spell", false), Is.EqualTo(PickupKind.Spell));
            Assert.That(PickupFeedbackRules.Classify("scheme", true), Is.EqualTo(PickupKind.Scheme));
            Assert.That(PickupFeedbackRules.Classify("e", false), Is.EqualTo(PickupKind.Explosive));
            Assert.That(PickupFeedbackRules.Classify("a", false), Is.EqualTo(PickupKind.Ammo));
            Assert.That(PickupFeedbackRules.Classify("med", false), Is.EqualTo(PickupKind.Medicine));
            Assert.That(PickupFeedbackRules.Classify("book", false), Is.EqualTo(PickupKind.Book));
            Assert.That(PickupFeedbackRules.Classify("instr", false), Is.EqualTo(PickupKind.Skill));
            Assert.That(PickupFeedbackRules.Classify("art", false), Is.EqualTo(PickupKind.Skill));
            Assert.That(PickupFeedbackRules.Classify("impl", false), Is.EqualTo(PickupKind.Skill));

            // The last term of the chain is the PRESENCE OF <sk> CHILDREN, not a tip value.
            Assert.That(PickupFeedbackRules.Classify("stuff", true), Is.EqualTo(PickupKind.Skill),
                "`l.xml && l.xml.sk.length()` is the fourth disjunct of the sk arm.");
            Assert.That(PickupFeedbackRules.Classify("stuff", false), Is.EqualTo(PickupKind.Other));
            Assert.That(PickupFeedbackRules.Classify(null, false), Is.EqualTo(PickupKind.Other));
        }

        [Test]
        public void InfoKey_IsNullForTheTwoSilentArms()
        {
            // Invent.as:1119-1131 — the armour and spell arms set a colour and produce NO message.
            Assert.That(PickupFeedbackRules.InfoKey(PickupKind.Armour, false), Is.Null);
            Assert.That(PickupFeedbackRules.InfoKey(PickupKind.Spell, false), Is.Null);

            Assert.That(PickupFeedbackRules.InfoKey(PickupKind.Weapon, false), Is.EqualTo("takeWeapon"));
            Assert.That(PickupFeedbackRules.InfoKey(PickupKind.Ammo, false), Is.EqualTo("takeAmmo"));
            Assert.That(PickupFeedbackRules.InfoKey(PickupKind.Medicine, false), Is.EqualTo("takeMed"));
            Assert.That(PickupFeedbackRules.InfoKey(PickupKind.Book, false), Is.EqualTo("takeBook"));
            Assert.That(PickupFeedbackRules.InfoKey(PickupKind.Scheme, false), Is.EqualTo("take"));
            Assert.That(PickupFeedbackRules.InfoKey(PickupKind.Skill, false), Is.EqualTo("take"));
            Assert.That(PickupFeedbackRules.InfoKey(PickupKind.Other, false), Is.EqualTo("take"));
            Assert.That(PickupFeedbackRules.InfoKey(PickupKind.Other, true), Is.EqualTo("takeMoney"));
        }

        [Test]
        public void InfoFirst_TakesTheCountForMoneyAndTheBareNameElsewhere()
        {
            Assert.That(PickupFeedbackRules.InfoFirst(PickupKind.Other, true, "Caps", 47), Is.EqualTo("47"),
                "Invent.as:1234 — `infoText(\"takeMoney\", l.kol)`.");

            Assert.That(PickupFeedbackRules.InfoFirst(PickupKind.Medicine, false, "Stimpak", 3),
                Is.EqualTo("Stimpak"),
                "Invent.as:1176 — `infoText(\"takeMed\", l.nazv)`, no quantity suffix.");

            Assert.That(PickupFeedbackRules.InfoFirst(PickupKind.Other, false, "Scrap metal", 3),
                Is.EqualTo("Scrap metal (3)"),
                "Invent.as:1238 — the catch-all appends the count.");
        }

        [Test]
        public void InfoSecond_IsOnlySuppliedByTheTwoTwoArgumentArms()
        {
            Assert.That(PickupFeedbackRules.InfoSecond(PickupKind.Weapon, 1, 73), Is.EqualTo("73"));
            Assert.That(PickupFeedbackRules.InfoSecond(PickupKind.Ammo, 24, 100), Is.EqualTo("24"));
            Assert.That(PickupFeedbackRules.InfoSecond(PickupKind.Other, 5, 100), Is.Null);
            Assert.That(PickupFeedbackRules.InfoSecond(PickupKind.Medicine, 5, 100), Is.Null);
        }

        [Test]
        public void Substitute_BoldsOnlyTheFirstSlot()
        {
            Assert.That(
                PickupFeedbackRules.Substitute("Found weapon @1 (condition @2%)", ".32 pistol", "73"),
                Is.EqualTo("Found weapon <b>.32 pistol</b> (condition 73%)"),
                "GUI.as:1555-1560 — `replace(\"@1\", \"<b>\" + param2 + \"</b>\")`, then a bare @2.");
        }

        [Test]
        public void FloatText_AppendsTheCountOnlyAboveOne()
        {
            Assert.That(PickupFeedbackRules.FloatText("Screwdriver", 1), Is.EqualTo("Screwdriver"));
            Assert.That(PickupFeedbackRules.FloatText("Screwdriver", 4), Is.EqualTo("Screwdriver (4)"));
            Assert.That(PickupFeedbackRules.FloatText(null, 1), Is.EqualTo(string.Empty));
        }

        [Test]
        public void FloatColour_UsesTheArmConstantsNotTheCatchAllTable()
        {
            // The five a flat tip-keyed table gets wrong. Invent.as:1060-1268.
            Assert.That(PickupFeedbackRules.FloatColour(PickupKind.Weapon, "weapon", -1), Is.EqualTo(5),
                "`color = 5` in the weapon arm (:1115).");
            Assert.That(PickupFeedbackRules.FloatColour(PickupKind.Scheme, "scheme", -1), Is.EqualTo(7),
                "`color = 7` in the scheme arm (:1145) — not the catch-all's 6.");
            Assert.That(PickupFeedbackRules.FloatColour(PickupKind.Ammo, "a", -1), Is.EqualTo(3));
            Assert.That(PickupFeedbackRules.FloatColour(PickupKind.Medicine, "med", -1), Is.EqualTo(1));
            Assert.That(PickupFeedbackRules.FloatColour(PickupKind.Book, "book", -1), Is.EqualTo(4));
            Assert.That(PickupFeedbackRules.FloatColour(PickupKind.Skill, "instr", -1), Is.EqualTo(6));

            // The catch-all's nested table, which is the only place `tip` is consulted again.
            Assert.That(PickupFeedbackRules.FloatColour(PickupKind.Other, "valuables", -1), Is.EqualTo(2));
            Assert.That(PickupFeedbackRules.FloatColour(PickupKind.Other, "him", -1), Is.EqualTo(1));
            Assert.That(PickupFeedbackRules.FloatColour(PickupKind.Other, "pot", -1), Is.EqualTo(1));
            Assert.That(PickupFeedbackRules.FloatColour(PickupKind.Other, "key", -1), Is.EqualTo(6));
            Assert.That(PickupFeedbackRules.FloatColour(PickupKind.Other, "spec", -1), Is.EqualTo(6));
            Assert.That(PickupFeedbackRules.FloatColour(PickupKind.Other, "equip", -1), Is.EqualTo(8));
            Assert.That(PickupFeedbackRules.FloatColour(PickupKind.Other, "stuff", -1), Is.EqualTo(0));
        }

        [Test]
        public void FloatColour_ForcedColourWinsOnEveryArm()
        {
            // Invent.as:1289-1291 — `if (l.fc >= 0) color = l.fc` applies after the chain, so it
            // overrides the arm constants too.
            Assert.That(PickupFeedbackRules.FloatColour(PickupKind.Weapon, "weapon", 3), Is.EqualTo(3));
            Assert.That(PickupFeedbackRules.FloatColour(PickupKind.Other, "stuff", 0), Is.EqualTo(0),
                "Zero is a real forced colour (r0 grey), not 'unset' — unset is -1.");
        }

        [Test]
        public void FlashColour_MatchesTheHudStyleSheet()
        {
            // GUI.as:192-208. Spelled out through PfeUiColors so the two tables cannot drift.
            Assert.That(PickupFeedbackRules.FlashColour(1), Is.EqualTo(PfeUiColors.Cyan));
            Assert.That(PickupFeedbackRules.FlashColour(6), Is.EqualTo(PfeUiColors.Violet));
            Assert.That(PickupFeedbackRules.FlashColour(8), Is.EqualTo(PfeUiColors.Olive));
            Assert.That(PickupFeedbackRules.FlashColour(0), Is.EqualTo(PfeUiColors.Grey));
            Assert.That(PickupFeedbackRules.FlashColour(99), Is.EqualTo(PfeUiColors.Grey),
                "An out-of-range index falls back to r0, not to `.r` — `.r` is the body colour, " +
                "not an index.");
        }

        [Test]
        public void ImportantMessage_NeedsTheOptionOrAnArtefact()
        {
            Assert.That(PickupFeedbackRules.ShowsImportantMessage(true, "stuff", "screwdriver", false),
                Is.True);
            Assert.That(PickupFeedbackRules.ShowsImportantMessage(false, "stuff", "screwdriver", false),
                Is.False, "Tutorial messages off, and it is not an artefact.");
            Assert.That(PickupFeedbackRules.ShowsImportantMessage(false, "art", "sphere", false),
                Is.True, "`l.tip == \"art\"` bypasses the option (Invent.as:1293).");
            Assert.That(PickupFeedbackRules.ShowsImportantMessage(true, "stuff", "screwdriver", true),
                Is.False, "The `mess_` latch is once per save.");
            Assert.That(PickupFeedbackRules.ShowsImportantMessage(true, "stuff", null, false),
                Is.False, "No mess key, no modal.");
        }

        [Test]
        public void ImportantMessage_LatchKeyIsPrefixed()
        {
            Assert.That(PickupFeedbackRules.MessTriggerKey("screwdriver"), Is.EqualTo("mess_screwdriver"),
                "Invent.as:1295 — `World.w.game.triggers[\"mess_\" + l.mess]`. The prefix is what keeps " +
                "a tutorial latch from colliding with a script trigger of the same name.");
        }

        [Test]
        public void HelpMessages_DefaultsToOn()
        {
            // World.as:218 `public var helpMess:Boolean = true`. A port that defaulted this to false
            // would silently drop every tutorial popup in a default run.
            HelpMessages.Reset();
            Assert.That(HelpMessages.Enabled, Is.True);
            Assert.That(HelpMessages.Default, Is.True);

            HelpMessages.Enabled = false;
            HelpMessages.Reset();
            Assert.That(HelpMessages.Enabled, Is.True, "Reset restores the Oracle's start, not the last value.");
        }

        [Test]
        public void ImpMess_SwallowsInputForThirtyFrames()
        {
            // GUI.as:1930 `this.t_bulb = 30`. Without the lockout the click that picked the item up
            // would dismiss the popup on the frame it appeared.
            Assert.That(ImpMessView.LockoutFrames, Is.EqualTo(30));
        }

        [Test]
        public void ResolveMessKey_PrefersTheAssetAndNeverInventsOne()
        {
            // The asset field wins when the importer has filled it.
            Assert.That(ItemPickupFeedback.ResolveItemMessKey("screwdriver", "screwdriver"),
                Is.EqualTo("screwdriver"));
            Assert.That(ItemPickupFeedback.ResolveWeaponMessKey("r32", "r32"), Is.EqualTo("r32"));

            // No id and no field is the "no popup" case, and it must not fall through to the AllData
            // mirror — that path does a Resources.Load, which throws outside the editor.
            Assert.That(ItemPickupFeedback.ResolveItemMessKey(string.Empty, null), Is.Null);
            Assert.That(ItemPickupFeedback.ResolveItemMessKey(string.Empty, string.Empty), Is.Null);
            Assert.That(ItemPickupFeedback.ResolveWeaponMessKey(string.Empty, null), Is.Null);
            Assert.That(ItemPickupFeedback.ResolveWeaponMessKey(string.Empty, string.Empty), Is.Null);

            // A non-empty id with no field DOES reach the mirror, so it must be given a real id only.
            // The two resolvers read different tables and must not be interchangeable: `screwdriver`
            // is an `<item>` row and not a `<weapon>` row, which is the control
            // `WeaponRowMess_ReadsTheWeaponTableAndNotTheItemTable` pins against the shipped file.
            Assert.That(ItemPickupFeedback.ResolveItemMessKey("screwdriver", null), Is.Null,
                "Offline there is no Resources system, so the mirror read yields nothing. The point of " +
                "this line is that the method does not THROW and does not invent a key.");
        }

        // ── The shipped document ──────────────────────────────────────────────

        private static PFE.UI.PipBuck.PipTextTable _shipped;

        private static PFE.UI.PipBuck.PipTextTable Shipped
        {
            get
            {
                if (_shipped != null) return _shipped;

                string path = Path.Combine(
                    SourceLint.ProjectRoot(), "Assets", "_PFE", "Data", "Resources", "Localisation",
                    "text_en.xml");

                Assert.That(File.Exists(path), Is.True, "The localisation document is missing: " + path);

                _shipped = PFE.UI.PipBuck.PipTextTable.Parse(File.ReadAllText(path));
                Assert.That(_shipped.IsEmpty, Is.False, "The document parsed to zero entries: " + path);
                return _shipped;
            }
        }

        [Test]
        public void Shipped_HasEveryTakeTemplate()
        {
            // Every key PickupFeedbackRules can return. A rename here would make the message list
            // silently show nothing, because a missing key resolves to null and is then skipped.
            string[] keys = { "take", "takeWeapon", "takeAmmo", "takeMed", "takeBook", "takeMoney" };

            foreach (string key in keys)
            {
                Assert.That(Shipped.TryGetInTable("info", key, 0, out string text), Is.True,
                    "`<info id=\"" + key + "\">` is missing from text_en.xml.");
                Assert.That(text, Is.Not.Null, "`<info id=\"" + key + "\">` has no <n> child.");
                Assert.That(text, Is.Not.Empty, "`<info id=\"" + key + "\">` has an empty <n> child.");
            }
        }

        [Test]
        public void Shipped_TakeTemplateHasTheBoldSlot()
        {
            Assert.That(Shipped.TryGetInTable("info", "take", 0, out string take), Is.True);

            // The substitution only does anything if the template carries @1 — a template without it
            // would render "Found: " with the item name dropped on the floor.
            Assert.That(take, Does.Contain("@1"), "`take` must carry the @1 slot the rules substitute.");

            string rendered = PickupFeedbackRules.Substitute(take, "Screwdriver", null);
            Assert.That(rendered, Does.Contain("Screwdriver"));
            Assert.That(rendered, Does.Not.Contain("@1"));
        }

        [Test]
        public void Shipped_ScrewdriverPopupRowIsComplete()
        {
            // The exact two lookups the popup performs: Res.txt("i", key) for the title and
            // Res.txt("i", key, 2) for the body — razd 2 is the `mess` child of `n / info / mess / help`.
            Assert.That(Shipped.TryGetInTable("item", "screwdriver", 0, out string title), Is.True,
                "`<item id=\"screwdriver\"><n>` is what the popup's title reads.");
            Assert.That(title, Is.EqualTo("Screwdriver"));

            Assert.That(Shipped.TryGetInTable("item", "screwdriver", 2, out string body), Is.True,
                "`<item id=\"screwdriver\"><mess>` is what the popup's body reads. Without it the " +
                "publisher latches the key and the bootstrapper logs a miss — the popup never shows.");
            Assert.That(body, Does.StartWith("A screwdriver!"),
                "The body is the tutorial text, not the `<info>` pane's description.");
        }

        [Test]
        public void Shipped_MessKeysAreMissingTextMoreOftenThanNot()
        {
            // The measurement the popup's latch placement rests on. 45 of the 500 `<item>` rows carry a
            // `@mess` key (and 4 of the 213 `<weapon>` rows do), but only 21 of those keys have a
            // matching `<item id=key><mess>` row in the shipped text_en.xml — the missing ones are the
            // skill-book and implant tutorials (`melee`, `smallguns`, `sneak`, `repair`, `science`,
            // `sp_*`, `mod_*`, `impl_*`, `lockpick`, `tele`, …).
            //
            // That is why `HudBootstrapper.ShowImportantMessage` writes the `mess_` latch and
            // `WorldItemPickup` only reads it: a latch written at offer time would burn every key whose
            // text the shipped document does not have, permanently, even against a more complete
            // language file. So this fixture asserts the DIRECTION rather than an exact count — a future
            // text file that fills them in must not fail the build.
            string allData = Path.Combine(SourceLint.ProjectRoot(), "Assets", "_PFE", "Data", "Resources",
                "Loot", "AllData.txt");
            Assert.That(File.Exists(allData), Is.True, "The AllData mirror is missing: " + allData);

            int keys = 0, withText = 0;
            foreach (PFE.Systems.Items.ItemXmlRow row0 in
                     PFE.Systems.Items.ItemXmlRows.Parse(File.ReadAllText(allData)))
            {
                string key = PFE.Systems.Weapons.WeaponXmlAttrs.Attr(row0.Attrs, "mess");
                if (string.IsNullOrEmpty(key)) continue;

                keys++;
                if (Shipped.TryGetInTable("item", key, 2, out string body) && !string.IsNullOrEmpty(body))
                {
                    withText++;
                }
            }

            Assert.That(keys, Is.GreaterThan(40),
                "The `<item>` rows carrying `@mess` — measured 45. A smaller count means the mirror " +
                "or the reader changed, and the popup's coverage changed with it.");
            Assert.That(withText, Is.LessThan(keys / 2),
                "More than half the mess keys have text in text_en.xml — the latch placement in " +
                "ShowImportantMessage (latch on success, not on offer) was chosen because the majority " +
                "did NOT. Re-read §10 of the gap ledger before changing that.");
        }

        [Test]
        public void Shipped_SpellPopupBodyCarriesAKeyPlaceholderTheChildNames()
        {
            // The `@1` substitution, measured on the shipped rows rather than assumed. All nine `sp_*`
            // spell books point at `mess='spell'`, and `spell`'s body is
            //   `<mess s1="keyDef">…you can apply it by pressing the @1 key…`
            // (text_en.xml:2807-2809). The placeholder is on the **child**, not on the entry, which is
            // the second of `Res.txt`'s two passes (Res.as:116-119) and was unreachable until
            // `PipTextTable.KeysOfChild` existed. Without it the popup showed "pressing the @1 key".
            const string table = "item", key = "spell";

            Assert.That(Shipped.TryGetInTable(table, key, 2, out string body), Is.True,
                "`<item id=\"spell\"><mess>` is the body the popup renders.");
            Assert.That(body, Does.Contain("@1"),
                "The shipped body really does carry the placeholder. If a future English rewrite drops " +
                "it, this fixture stops covering anything and the substitution test below is vacuous.");

            // The entry's own pass is a no-op here and the child's is the live one — that asymmetry is
            // the whole reason the parser needed a third key carrier.
            Assert.That(Shipped.KeysOfEntry(table, key), Is.Null,
                "`<item id=\"spell\">` has no @s1 of its own, so the first pass substitutes nothing.");
            Assert.That(Shipped.KeysOfChild(table, key, 2), Is.Not.Null,
                "The `<mess>` child carries @s1 — Res.as:116-119.");
            Assert.That(Shipped.KeysOfChild(table, key, 2)[0], Is.EqualTo("keyDef"),
                "`<mess s1=\"keyDef\">`.");

            // The title is razd 0 and the Oracle substitutes nothing there (`if(razd >= 1 || dop)`,
            // Res.as:108). Its child is `<n>`, which carries no key names, so the asymmetry shows up
            // structurally rather than only in the gate.
            Assert.That(Shipped.TryGetInTable(table, key, 0, out string title), Is.True);
            Assert.That(Shipped.KeysOfChild(table, key, 0), Is.Null,
                "The `<n>` child carries no @s1, and even if it did the razd-0 title is not substituted.");
            Assert.That(title, Is.EqualTo("Protective spell"));

            // The substitution itself: the placeholder goes, the resolved name arrives in its place. The
            // caption is the port's stand-in (`Res.txt("k", id)` for the real `Ctr.retKey`), so this
            // asserts the placeholder is gone rather than a particular caption.
            string resolved = PFE.UI.PipBuck.PipTextTable.ApplyKeys(body, Shipped.KeysOfChild(table, key, 2),
                Shipped.Key);

            Assert.That(resolved, Does.Not.Contain("@1"),
                "The player must never see '@1'. This is the assertion that was missing.");
            Assert.That(resolved, Does.Contain(Shipped.Key("keyDef")),
                "The resolved caption replaces the placeholder — Res.addKeys, Res.as:362-378.");

            // The reachability half: which items land on this row in the first place.
            string allData = Path.Combine(SourceLint.ProjectRoot(), "Assets", "_PFE", "Data", "Resources",
                "Loot", "AllData.txt");
            Assert.That(File.Exists(allData), Is.True, "The AllData mirror is missing: " + allData);

            int spellBooks = 0;
            foreach (PFE.Systems.Items.ItemXmlRow row in
                     PFE.Systems.Items.ItemXmlRows.Parse(File.ReadAllText(allData)))
            {
                if (PFE.Systems.Weapons.WeaponXmlAttrs.Attr(row.Attrs, "mess") == key) spellBooks++;
            }

            Assert.That(spellBooks, Is.EqualTo(9),
                "The nine `sp_*` spell books — slow, mwall, blast, cryst, kdash, mshit, moon, gwall, " +
                "invulner. Each one of them showed the placeholder before the fix.");
        }

        // ── The weapon half of the popup ─────────────────────────────────────

        private static string AllDataText
        {
            get
            {
                string path = Path.Combine(SourceLint.ProjectRoot(), "Assets", "_PFE", "Data", "Resources",
                    "Loot", "AllData.txt");
                Assert.That(File.Exists(path), Is.True, "The AllData mirror is missing: " + path);
                return File.ReadAllText(path);
            }
        }

        [Test]
        public void Shipped_ExactlyFourWeaponRowsCarryAMessKey()
        {
            // The whole weapon half of the popup, in one number. `Invent.take`'s popup tail runs for
            // weapons (`Invent.as:1293`) because the `if(l.tip == Item.L_WEAPON)` branch is INSIDE
            // `take` and it calls `addWeapon` at :1101 — it is not a separate entry point.
            var keys = new List<string>();
            foreach (PFE.Systems.Weapons.AllDataElement row in
                     PFE.Systems.Weapons.AllDataElements.TopLevel(AllDataText, "weapon"))
            {
                string mess = PFE.Systems.Weapons.WeaponXmlAttrs.Attr(row.Attrs, "mess");
                if (!string.IsNullOrEmpty(mess)) keys.Add(mess);
            }

            Assert.That(keys, Is.EquivalentTo(new[] { "mont", "r32", "rech", "telebul" }),
                "The `<weapon>` rows carrying `@mess`. AllData.as:2192 / :2455 / :2559 / :3533. A new " +
                "one means a new tutorial to verify; a missing one means a row was renamed.");
        }

        [Test]
        public void Shipped_WeaponAndItemIdsOverlapByThirtyTwo()
        {
            // The measurement that makes the popup's key lookup table-EXPLICIT rather than a
            // fallback chain. The Oracle reads `AllData.d.weapon` in the weapon branch
            // (`Invent.as:1060`, :1101) and `AllData.d.item` everywhere else, so a port that tries the
            // item row first would resolve these 32 from the wrong table and show the wrong tutorial.
            var items = new HashSet<string>();
            foreach (PFE.Systems.Items.ItemXmlRow row in
                     PFE.Systems.Items.ItemXmlRows.Parse(AllDataText))
            {
                if (!string.IsNullOrEmpty(row.Id)) items.Add(row.Id);
            }

            var weapons = new HashSet<string>();
            foreach (PFE.Systems.Weapons.AllDataElement row in
                     PFE.Systems.Weapons.AllDataElements.TopLevel(AllDataText, "weapon"))
            {
                if (!string.IsNullOrEmpty(row.Id)) weapons.Add(row.Id);
            }

            items.IntersectWith(weapons);

            Assert.That(items.Count, Is.EqualTo(32),
                "Ids present in BOTH the `<item>` and `<weapon>` tables (bomb, cryogr, dinamit, fgren…). " +
                "A table-explicit read is what the Oracle does; a shared lookup would guess wrong here.");
        }

        [Test]
        public void Shipped_WeaponMessKeysResolveTheirTextInTheItemTable()
        {
            // All four keys DO have popup text — just not where a reader would first look. The
            // `<weapon>` table in text_en.xml ships `<n>` and `<info>` and no `<mess>` child at all,
            // so the bodies live in the `item` table keyed by the weapon id. The popup therefore needs
            // no new lookup path for weapons, only a way to find the key.
            foreach (string key in new[] { "mont", "r32", "rech", "telebul" })
            {
                Assert.That(Shipped.TryGetInTable("item", key, 0, out string title), Is.True,
                    "`<item id=\"" + key + "\"><n>` — the popup title for a weapon.");
                Assert.That(title, Is.Not.Empty);

                Assert.That(Shipped.TryGetInTable("item", key, 2, out string body), Is.True,
                    "`<item id=\"" + key + "\"><mess>` — the popup body. The `<weapon>` table has no " +
                    "`<mess>` child for any id, so this row is the only text a weapon popup can read.");
                Assert.That(body, Is.Not.Empty);
            }

            // The absent control: the `<weapon>` table itself must carry none, or the claim above
            // would be vacuous.
            foreach (string key in new[] { "mont", "r32", "rech", "telebul" })
            {
                Assert.That(Shipped.TryGetInTable("weapon", key, 2, out _), Is.False,
                    "`<weapon id=\"" + key + "\"><mess>` must NOT exist — that is why the text is read " +
                    "out of the `item` table.");
            }
        }

        [Test]
        public void WeaponRowMess_ReadsTheWeaponTableAndNotTheItemTable()
        {
            // The reader itself, through the same path `PlayerInventory.WeaponRowMess` uses. The
            // positive and the absent control in one test, because a lookup that returned the item
            // row's value would otherwise pass on the four real keys alone.
            string allData = AllDataText;

            foreach (string key in new[] { "mont", "r32", "rech", "telebul" })
            {
                Assert.That(PFE.Systems.Weapons.AllDataElements.TryGet(allData, "weapon", key, out var row),
                    Is.True, "`<weapon id='" + key + "'>` is missing from AllData.");
                Assert.That(PFE.Systems.Weapons.WeaponXmlAttrs.Attr(row.Attrs, "mess"), Is.EqualTo(key),
                    "AllData carries the id back as its own mess key — " + key + ".");
            }

            // Absent control. `screwdriver` IS an `<item>` row with `mess='screwdriver'`, and is NOT a
            // `<weapon>` row. A lookup that fell through to the item table would return
            // "screwdriver" here and the weapon popup would fire for a screwdriver pickup.
            Assert.That(PFE.Systems.Weapons.AllDataElements.TryGet(allData, "weapon", "screwdriver", out _),
                Is.False, "`screwdriver` must not resolve as a WEAPON row — it is an item row.");
            Assert.That(
                PFE.Systems.Weapons.AllDataElements.TryGet(allData, "item", "screwdriver", out var itemRow),
                Is.True, "…and it must still resolve as an ITEM row, or the control proves nothing.");
            Assert.That(PFE.Systems.Weapons.WeaponXmlAttrs.Attr(itemRow.Attrs, "mess"),
                Is.EqualTo("screwdriver"));
        }

        [Test]
        public void Shipped_ScrewdriverRowCarriesItsMessKey()
        {
            // The other half of the chain: the popup key itself. Read from the runtime mirror, because
            // that is the file `PlayerInventory.ItemRowMess` falls back to while the item import has
            // not filled `ItemDefinition.mess`.
            string path = Path.Combine(SourceLint.ProjectRoot(), "Assets", "_PFE", "Data", "Resources",
                "Loot", "AllData.txt");
            Assert.That(File.Exists(path), Is.True, "The AllData mirror is missing: " + path);

            PFE.Systems.Items.ItemXmlRow? row = null;
            foreach (PFE.Systems.Items.ItemXmlRow candidate in
                     PFE.Systems.Items.ItemXmlRows.Parse(File.ReadAllText(path)))
            {
                if (candidate.Id == "screwdriver")
                {
                    row = candidate;
                    break;
                }
            }

            Assert.That(row.HasValue, Is.True, "No `<item id='screwdriver'>` row in the mirror.");
            Assert.That(PFE.Systems.Weapons.WeaponXmlAttrs.Attr(row.Value.Attrs, "mess"),
                Is.EqualTo("screwdriver"),
                "AllData.as:4107 — `<item id='screwdriver' tip='instr' price='10' mess='screwdriver'>`. " +
                "If this attribute is renamed, the screwdriver's tutorial silently stops appearing.");
        }
    }
}
