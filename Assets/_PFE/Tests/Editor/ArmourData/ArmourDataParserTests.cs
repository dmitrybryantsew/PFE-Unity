using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using PFE.Data.Definitions;

namespace PFE.Tests.Editor.ArmourData
{
    /// <summary>
    /// Pins <see cref="ArmourDataParser"/> against the shapes actually present in <c>AllData.as</c>.
    ///
    /// <para><b>Why the namespace is not <c>…Editor.Data</c>.</b> It was, and that broke the build:
    /// <c>GameInventoryTests</c> lives in <c>PFE.Tests.Editor</c> and writes
    /// <c>Data.Definitions.InventoryCategory.Ammo</c>. Adding a <c>PFE.Tests.Editor.Data</c> namespace
    /// made <c>Data</c> bind to it, so <c>Data.Definitions</c> resolved to a namespace that does not
    /// exist and four lines in a file this change never touched failed with CS0234. C# resolves a
    /// namespace segment against the <i>enclosing</i> namespaces first, so any sibling folder named
    /// <c>Data</c> is a landmine for every test in the tree — renaming this one removes the hazard
    /// rather than patching the one victim it happened to hit.</para>
    ///
    /// <para><b>Why the fixtures are inline.</b> <c>AllData.as</c> sits at the project root as an
    /// <b>untracked</b> working copy, and the tracked oracle lives outside the repository
    /// (<c>…/pfeToUnity/pfe/scripts/fe/AllData.as</c>). A fixture that read either would pass on this
    /// machine and fail on a fresh clone, so each test states the snippet it needs. One census test
    /// does read the real file, and <b>ignores itself with a message</b> when it is absent rather than
    /// pretending to have run.</para>
    ///
    /// <para><b>What the parser must not do is silently narrow.</b> The armour XML carries 20
    /// attributes on <c>&lt;armor&gt;</c> and 24 on <c>&lt;upd&gt;</c>; the port models eleven. So the
    /// tests here assert on the <i>warnings</i> as much as on the values — a dropped attribute is a
    /// decision that has to be visible, and an unknown one has to be louder than a known drop.</para>
    /// </summary>
    [TestFixture]
    public class ArmourDataParserTests
    {
        // === Real snippets from AllData.as, verbatim ===

        private const string PipBlock =
            "<armor id='pip' sort='0' clo='1' hp='5000' price='100' und='1' norep='1'>\n" +
            "\t\t\t\t<upd dexter='0.2'/>\n" +
            "\t\t\t</armor>";

        private const string KombuBlock =
            "<armor id='kombu' sort='1' hp='2000' price='800' lvl='2' kolcomp='2'>\n" +
            "\t\t\t\t<upd armor='4' marmor='3' qual='0.6' fire='0.1' cryo='0.1' dexter='0.15'/>\n" +
            "\t\t\t\t<upd armor='5' marmor='4' qual='0.7' fire='0.1' cryo='0.1' dexter='0.15' kol='10'/>\n" +
            "\t\t\t\t<upd armor='6' marmor='5' qual='0.8' fire='0.1' cryo='0.1' dexter='0.15' kol='20'/>\n" +
            "\t\t\t</armor>";

        private const string MetalBlock =
            "<armor id='metal' sort='3' hp='5000' price='4000' lvl='2' kolcomp='4'>\n" +
            "\t\t\t\t<upd armor='12' qual='0.5' bul='0.2' phis='0.1' blade='0.1' fang='0.1' expl='0.1' laser='0.25' spark='-0.3' dexter='-0.3'/>\n" +
            "\t\t\t\t<upd armor='14' qual='0.6' bul='0.2' phis='0.1' blade='0.1' fang='0.1' expl='0.1' laser='0.25' spark='-0.2' dexter='-0.25' kol='15'/>\n" +
            "\t\t\t\t<upd armor='16' qual='0.7' bul='0.2' phis='0.1' blade='0.1' fang='0.1' expl='0.1' laser='0.25' spark='-0.1' dexter='-0.2' kol='20'/>\n" +
            "\t\t\t</armor>";

        private const string AdeptAmuletBlock =
            "<armor id='amul_adept' tip='3' price='5000' und='1' norep='1' magic='1.16'>\n" +
            "\t\t\t\t<upd dark='0.2'/>\n" +
            "\t\t\t</armor>";

        // === Fixture state ===

        private readonly List<Object> _created = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            // ItemDefinition is a ScriptableObject; EditMode tests that create them without destroying
            // them leave "instance not destroyed" noise behind on the next domain reload.
            foreach (Object created in _created)
                if (created != null) Object.DestroyImmediate(created);

            _created.Clear();
        }

        private ItemDefinition NewItemDefinition()
        {
            var definition = ScriptableObject.CreateInstance<ItemDefinition>();
            _created.Add(definition);
            return definition;
        }

        private static ArmourDefinitionData ParseFirst(string snippet)
        {
            List<ArmourDefinitionData> parsed = ArmourDataParser.ParseAll(snippet);
            Assert.AreEqual(1, parsed.Count, "Fixture should contain exactly one <armor> element.");
            return parsed[0];
        }

        private static string WarningsFor(ArmourDefinitionData data, string fragment)
        {
            foreach (string warning in data.warnings)
                if (warning.Contains(fragment)) return warning;
            return null;
        }

        // === The per-item half (Armor.as:97-172) ===

        [Test]
        public void ParsesThePerItemHalf()
        {
            ArmourDefinitionData pip = ParseFirst(PipBlock);

            Assert.AreEqual("pip", pip.id);
            Assert.AreEqual(0, pip.sortOrder);
            Assert.AreEqual(5000, pip.armorHP);
            Assert.AreEqual(100, pip.price);
            Assert.IsTrue(pip.indestructible, "@und is a presence flag.");
            Assert.IsTrue(pip.noRepair, "@norep is a presence flag.");
            Assert.IsFalse(pip.hideMane, "pip declares no @hide.");
            Assert.AreEqual(1, pip.tip, "Armor.tip defaults to 1 (Armor.as:16).");
        }

        [Test]
        public void ParsesHideAsAValueNotAPresenceFlag()
        {
            // Armor.as:170-172 assigns the *value*, so hide='0' would mean "does not hide" — unlike
            // @und / @norep which are tested with .length(). AllData only ever writes hide='1'.
            ArmourDefinitionData hidden = ParseFirst("<armor id='x' hide='1'><upd armor='1'/></armor>");
            ArmourDefinitionData shown = ParseFirst("<armor id='x' hide='0'><upd armor='1'/></armor>");

            Assert.IsTrue(hidden.hideMane);
            Assert.IsFalse(shown.hideMane);
        }

        [Test]
        public void AnArmourWithNoPriceReadsAsZero()
        {
            // AS3 does `this.price = this.xml.@price;` with no guard, which is NaN when absent — so
            // there is no oracle value to match. 0 is the port's choice and is recorded here.
            ArmourDefinitionData data = ParseFirst("<armor id='x'><upd armor='1'/></armor>");

            Assert.AreEqual(0, data.price);
        }

        // === The per-level half (Armor.as:186-193) ===

        [Test]
        public void KeepsEveryUpgradeLevel()
        {
            ArmourDefinitionData kombu = ParseFirst(KombuBlock);

            Assert.AreEqual(3, kombu.levels.Length, "kombu has three <upd> children.");
            Assert.AreEqual(2, kombu.MaxLevel, "And @lvl='2' agrees with them.");
        }

        [Test]
        public void ParsesEachLevelsOwnRatings()
        {
            ArmourDefinitionData kombu = ParseFirst(KombuBlock);

            Assert.AreEqual(4, kombu.levels[0].armor);
            Assert.AreEqual(5, kombu.levels[1].armor);
            Assert.AreEqual(6, kombu.levels[2].armor, "The whole reason armourLevels exists.");

            Assert.AreEqual(3, kombu.levels[0].magicArmor);
            Assert.AreEqual(5, kombu.levels[2].magicArmor);

            Assert.AreEqual(0.6f, kombu.levels[0].reliability, 1e-4f);
            Assert.AreEqual(0.8f, kombu.levels[2].reliability, 1e-4f);
        }

        [Test]
        public void ParsesResistsAndTheSignedDexterity()
        {
            ArmourDefinitionData kombu = ParseFirst(KombuBlock);
            ArmourDefinitionData metal = ParseFirst(MetalBlock);

            Assert.AreEqual(0.1f, kombu.levels[0].resists.GetResist(DamageType.Fire), 1e-4f);
            Assert.AreEqual(0.1f, kombu.levels[0].resists.GetResist(DamageType.Cryo), 1e-4f);
            Assert.AreEqual(0f, kombu.levels[0].resists.GetResist(DamageType.Blade), 1e-4f,
                "kombu declares no blade resistance, so it is neutral 0 — not 1.");

            Assert.AreEqual(0.15f, kombu.levels[0].dexterity, 1e-4f);
            Assert.AreEqual(-0.3f, metal.levels[0].dexterity, 1e-4f,
                "metal is dexter='-0.3' — signed, and negative is a *bonus*. The field used to be " +
                "called dexPenalty, which read this backwards.");
        }

        [Test]
        public void ParsesNegativeResistsAsVulnerabilities()
        {
            ArmourDefinitionData metal = ParseFirst(MetalBlock);

            Assert.AreEqual(-0.3f, metal.levels[0].resists.GetResist(DamageType.Spark), 1e-4f);
            Assert.AreEqual(-0.2f, metal.levels[1].resists.GetResist(DamageType.Spark), 1e-4f);
            Assert.AreEqual(-0.1f, metal.levels[2].resists.GetResist(DamageType.Spark), 1e-4f,
                "Metal gets *less* vulnerable to spark as it is upgraded.");
        }

        [Test]
        public void AnAmuletIsTipThreeAndItsRatingsAreElsewhere()
        {
            ArmourDefinitionData adept = ParseFirst(AdeptAmuletBlock);

            Assert.AreEqual(3, adept.tip, "tip='3' is the amulet slot.");
            Assert.AreEqual(0, adept.levels[0].armor, "The amulet's bonus is magic, not armour.");
            Assert.AreEqual(0, adept.levels[0].magicArmor,
                "…and @magic is a *damage multiplier* (Armor.magicMult), not @marmor — so magicArmor " +
                "stays 0 and the multiplier is recorded as a deliberate omission.");
        }

        // === The warnings: every drop is named ===

        [Test]
        public void DarkIsDroppedAndSaysWhy()
        {
            // amul_adept is the only carrier. Armor.getXmlParam has no @dark branch, so AS3 ignores it;
            // the warning exists so a later reader does not 'fix' the omission into a divergence.
            ArmourDefinitionData adept = ParseFirst(AdeptAmuletBlock);

            string warning = WarningsFor(adept, "dark");
            Assert.IsNotNull(warning, "The dropped @dark must be reported, not swallowed.");
            StringAssert.Contains("getXmlParam", warning);
            StringAssert.Contains("0.2", warning, "The warning quotes the value it dropped.");
        }

        [Test]
        public void EveryDeliberateOmissionIsReportedAsADrop()
        {
            // One synthetic block carrying every attribute the port knowingly does not model. Each must
            // produce a "dropped —" warning: a decision, visibly made.
            const string block =
                "<armor id='x' clo='1' tre='3' h2o='0.25' melee='1.1' guns='1.1' magic='1.1' crit='0.05' " +
                "comp='metal_comp' kolcomp='4' abil='stealth_armor' fly='1' lvl='1'>" +
                "<upd armor='1' kol='10' radx='0.5' sneak='0.2' mana='1000' act='100' used='1' res='1'/>" +
                "</armor>";

            ArmourDefinitionData data = ParseFirst(block);

            foreach (string attribute in new[]
                     {
                         "clo", "tre", "h2o", "melee", "guns", "magic", "crit", "comp", "kolcomp",
                         "abil", "fly", "kol", "radx", "sneak", "mana", "act", "used", "res",
                     })
                Assert.IsNotNull(WarningsFor(data, $"@{attribute}='"),
                    $"@{attribute} is a known omission and must say so — otherwise it looks like a bug.");

            CollectionAssert.IsEmpty(
                data.warnings.FindAll(w => w.Contains("UNRECOGNISED")),
                "None of these is *unknown*; they are all on the deliberate-omission list.");
        }

        [Test]
        public void AnUnknownAttributeIsLouderThanAKnownDrop()
        {
            ArmourDefinitionData data = ParseFirst(
                "<armor id='x' clo='1' wat='7'><upd armor='1' zzz='2'/></armor>");

            Assert.IsNotNull(WarningsFor(data, "UNRECOGNISED"),
                "An attribute nobody has classified must be distinguishable from a deliberate drop.");
            Assert.IsNotNull(WarningsFor(data, "@clo='"), "The known drop is still reported.");
            Assert.IsNotNull(WarningsFor(data, "@wat='"));
            Assert.IsNotNull(WarningsFor(data, "@zzz='"));
        }

        [Test]
        public void WarnsWhenTheDeclaredLevelCeilingDisagreesWithTheTable()
        {
            // @lvl is Armor.maxlvl — a declaration, not the index AS3 uses (it indexes upd[] with the
            // constructor argument). If the two disagree one of them is wrong, and the failure mode is
            // a level that silently has no stats.
            ArmourDefinitionData data = ParseFirst(
                "<armor id='x' lvl='2'><upd armor='1'/></armor>");

            string warning = WarningsFor(data, "@lvl=");
            Assert.IsNotNull(warning);
            StringAssert.Contains("max index 0", warning);
        }

        [Test]
        public void AnArmourWithNoUpdStillYieldsOneAllZeroLevel()
        {
            ArmourDefinitionData data = ParseFirst("<armor id='x' hp='100'></armor>");

            Assert.AreEqual(1, data.levels.Length, "levels[0] must always be safe to read.");
            Assert.AreEqual(0, data.levels[0].armor);
            Assert.AreEqual(0f, data.levels[0].reliability, 1e-4f,
                "qual 0 means isrnd(0), which is never true — so this armour absorbs nothing.");
            Assert.IsNotNull(WarningsFor(data, "no <upd> child"));
        }

        [Test]
        public void ThrowsWhenAnArmourHasNoId()
        {
            Assert.Throws<System.FormatException>(
                () => ArmourDataParser.ParseAll("<armor hp='100'><upd armor='1'/></armor>"));
        }

        // === Structural: what ParseAll picks up, and what it does not ===

        [Test]
        public void ParsesEveryArmourElementInOrder()
        {
            List<ArmourDefinitionData> parsed = ArmourDataParser.ParseAll(
                "<!-- *******   Броня   *******  -->\n" + PipBlock + "\n" + KombuBlock);

            Assert.AreEqual(2, parsed.Count);
            Assert.AreEqual("pip", parsed[0].id);
            Assert.AreEqual("kombu", parsed[1].id);
        }

        [Test]
        public void DoesNotPickUpArmourInsideAnXmlComment()
        {
            // Positive control for the comment stripping: AllData.as has 127 comments, and a commented
            // -out armour must not become a definition.
            List<ArmourDefinitionData> parsed = ArmourDataParser.ParseAll(
                "<!-- <armor id='ghost' hp='1'><upd armor='1'/></armor> -->\n" + PipBlock);

            Assert.AreEqual(1, parsed.Count);
            Assert.AreEqual("pip", parsed[0].id, "The real element, not the commented one.");
        }

        [Test]
        public void DoesNotPickUpItemsOrOtherElements()
        {
            // The <item> element also carries id/price/sort, so the <armor anchor is load-bearing.
            List<ArmourDefinitionData> parsed = ArmourDataParser.ParseAll(
                "<item id='not_armour' tip='b' price='10' sort='1'/>\n" + PipBlock);

            Assert.AreEqual(1, parsed.Count);
            Assert.AreEqual("pip", parsed[0].id);
        }

        [Test]
        public void ParseAllOnEmptyInputReturnsEmpty()
        {
            Assert.IsEmpty(ArmourDataParser.ParseAll(null));
            Assert.IsEmpty(ArmourDataParser.ParseAll(string.Empty));
            Assert.IsEmpty(ArmourDataParser.ParseAll("no elements here"));
        }

        [Test]
        public void ParseBlockIsUsableWithoutTheRegex()
        {
            // The importer uses ParseAll, but ParseBlock is the seam a hand-written block would use.
            ArmourDefinitionData data = ArmourDataParser.ParseBlock(
                " id='x' hp='250' tip='1' und='1' ", "<upd armor='7' qual='0.5'/>");

            Assert.AreEqual("x", data.id);
            Assert.AreEqual(250, data.armorHP);
            Assert.IsTrue(data.indestructible);
            Assert.AreEqual(7, data.levels[0].armor);
        }

        [Test]
        public void ReadAttributesAcceptsBothQuoteStyles()
        {
            Dictionary<string, string> attributes =
                ArmourDataParser.ReadAttributes(" a='1' b=\"2\" c = '3' ");

            Assert.AreEqual("1", attributes["a"]);
            Assert.AreEqual("2", attributes["b"]);
            Assert.AreEqual("3", attributes["c"]);
        }

        // === Culture ===

        [Test]
        public void ParsesDecimalsCorrectlyUnderACommaDecimalCulture()
        {
            // The highest-value test here. float.TryParse's default overload allows thousands
            // separators, so on de-DE — where '.' *is* the group separator — qual='0.25' parses as 25.
            // A silent x100 that presents as "armour absorbs everything".
            CultureInfo originalCulture = CultureInfo.CurrentCulture;
            CultureInfo originalDefault = CultureInfo.DefaultThreadCurrentCulture;

            try
            {
                var german = new CultureInfo("de-DE");
                Thread.CurrentThread.CurrentCulture = german;
                CultureInfo.DefaultThreadCurrentCulture = german;

                AssertTheCommaDecimalCultureTookEffect();

                ArmourDefinitionData data = ParseFirst("<armor id='x'><upd qual='0.25' dexter='-0.3'/></armor>");

                Assert.AreEqual(0.25f, data.levels[0].reliability, 1e-4f,
                    "0.25, not 25 — the parse must be invariant-culture.");
                Assert.AreEqual(-0.3f, data.levels[0].dexterity, 1e-4f);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = originalCulture;
                CultureInfo.DefaultThreadCurrentCulture = originalDefault;
            }
        }

        /// <summary>
        /// Proves the culture override actually took effect, so the test above cannot pass vacuously.
        ///
        /// <para>The naive parse is the bug being guarded against: <c>float.TryParse</c>'s default
        /// overload allows thousands separators, so under a comma-decimal culture <c>"0.25"</c> either
        /// becomes <c>25</c> or fails outright — <b>never</b> <c>0.25</c>. So the assertion is "the
        /// ambient culture does not read this as a quarter", which holds for both failure modes and
        /// still catches an override that did not take.</para>
        /// </summary>
        private static void AssertTheCommaDecimalCultureTookEffect()
        {
            bool naiveParsed = float.TryParse("0.25", out float naive);

            Assert.IsFalse(naiveParsed && Mathf.Approximately(naive, 0.25f),
                $"The ambient culture is not comma-decimal (naive parse gave " +
                $"{(naiveParsed ? naive.ToString(CultureInfo.InvariantCulture) : "a failure")}), so this " +
                "test would not exercise the bug it exists for.");
        }

        // === ApplyTo: the asset mapping ===

        [Test]
        public void ApplyToSetsIdentityTypeAndAcquisitionFields()
        {
            ItemDefinition target = NewItemDefinition();
            ParseFirst(KombuBlock).ApplyTo(target);

            Assert.AreEqual("kombu", target.itemId);
            Assert.AreEqual(ItemType.Equipment, target.type);
            Assert.AreEqual(InventoryCategory.Apparel, target.inventoryCategory);
            Assert.AreEqual(UsageType.Equipment, target.usageType);
            Assert.AreEqual(800, target.basePrice);
            Assert.AreEqual(1, target.sortOrder);
        }

        [Test]
        public void ApplyToProducesSomethingIsArmourRecognises_WhereTypeAloneWouldNot()
        {
            // IsArmour exists because `type == ItemType.Equipment` is NOT a discriminator — the test
            // above sets exactly that on armour, but any other equipment carries it too. A caller that
            // tested `type` would happily build an armour item from a definition with no level table:
            // one that absorbs nothing, never wears, and reports an empty id. Only armourLevels is
            // written by this importer, so it is the honest test.
            ItemDefinition target = NewItemDefinition();
            ParseFirst(KombuBlock).ApplyTo(target);

            Assert.IsTrue(target.IsArmour);

            // The control for the trap: set the field a `type`-based check would rely on, and show
            // IsArmour still refuses.
            ItemDefinition plainEquipment = NewItemDefinition();
            plainEquipment.type = ItemType.Equipment;

            Assert.IsFalse(plainEquipment.IsArmour,
                "Equipment-typed with no level table is not armour — the case IsArmour must reject.");
        }

        [Test]
        public void ApplyToCopiesBothHalves()
        {
            ItemDefinition target = NewItemDefinition();
            ParseFirst(KombuBlock).ApplyTo(target);

            Assert.AreEqual(2000, target.armorHP, "The per-item half.");
            Assert.AreEqual(1, target.armorTip);
            Assert.IsFalse(target.armorIndestructible);

            Assert.AreEqual(3, target.armourLevels.Length, "The per-level half.");
            Assert.AreEqual(6, target.armourLevels[2].armor);
        }

        [Test]
        public void ApplyToKeepsLevelZeroAndEquipmentInAgreement()
        {
            // The documented invariant. `equipment` stays the level-0 snapshot so the many callers that
            // ignore upgrades keep working, and the two are written from the same source in one place.
            ItemDefinition target = NewItemDefinition();
            ParseFirst(MetalBlock).ApplyTo(target);

            Assert.AreEqual(target.armourLevels[0].armor, target.equipment.armor);
            Assert.AreEqual(target.armourLevels[0].magicArmor, target.equipment.magicArmor);
            Assert.AreEqual(target.armourLevels[0].reliability, target.equipment.reliability, 1e-4f);
            Assert.AreEqual(target.armourLevels[0].dexterity, target.equipment.dexterity, 1e-4f);
            Assert.AreEqual(
                target.armourLevels[0].resists.GetResist(DamageType.Spark),
                target.equipment.resists.GetResist(DamageType.Spark), 1e-4f);
        }

        [Test]
        public void ApplyToDoesNotTreatArmourLvlAsARequiredLevel()
        {
            // The sibling AmmoDataImporter maps its own @lvl to requiredLevel. For armour @lvl is the
            // *upgrade ceiling*, so reusing that mapping would demand character level 2 for kombu.
            ItemDefinition target = NewItemDefinition();
            ParseFirst(KombuBlock).ApplyTo(target);

            Assert.AreEqual(0, target.requiredLevel, "kombu's lvl='2' is maxlvl, not a level gate.");
        }

        [Test]
        public void ApplyToCarriesNoRepairAndIndestructibility()
        {
            ItemDefinition pip = NewItemDefinition();
            ParseFirst(PipBlock).ApplyTo(pip);

            Assert.IsTrue(pip.armorIndestructible);
            Assert.IsTrue(pip.armorNoRepair);
        }

        [Test]
        public void ApplyToThrowsWithoutALevelTable()
        {
            var data = new ArmourDefinitionData { id = "x", levels = null };
            Assert.Throws<System.InvalidOperationException>(() => data.ApplyTo(NewItemDefinition()));
        }

        [Test]
        public void ApplyToRejectsANullTarget()
        {
            Assert.Throws<System.ArgumentNullException>(() => ParseFirst(PipBlock).ApplyTo(null));
        }

        // === The real file, when it is there ===

        [Test]
        public void ParsingTheRealAllDataFindsEveryArmourElement()
        {
            string path = FindAllDataAs();
            if (path == null)
            {
                Assert.Ignore(
                    "AllData.as is not next to this project (it is an untracked working copy, and the " +
                    "tracked oracle lives outside the repository). The shape tests above still ran; " +
                    "this census did not.");
                return;
            }

            List<ArmourDefinitionData> parsed = ArmourDataParser.ParseAll(File.ReadAllText(path));

            Assert.AreEqual(35, parsed.Count, "35 <armor> elements — 18 body armours with 3 levels, " +
                                              "17 one-level items (pip, tre, socks, 14 amulets).");
            Assert.AreEqual(71, CountLevels(parsed), "18*3 + 17 = 71 <upd> children in total.");

            ArmourDefinitionData pip = parsed.Find(d => d.id == "pip");
            Assert.IsNotNull(pip);
            Assert.IsTrue(pip.indestructible && pip.noRepair, "pip is both @und and @norep.");
            Assert.AreEqual(5000, pip.armorHP);

            ArmourDefinitionData metal = parsed.Find(d => d.id == "metal");
            Assert.AreEqual(-0.3f, metal.levels[0].resists.GetResist(DamageType.Spark), 1e-4f);

            ArmourDefinitionData adept = parsed.Find(d => d.id == "amul_adept");
            Assert.AreEqual(3, adept.tip);
            Assert.IsNotNull(WarningsFor(adept, "dark"), "amul_adept's dark='0.2' is the only carrier.");

            // Every item must be applyable and keep the invariant — this is the sweep that would catch a
            // malformed block the shape tests never saw.
            foreach (ArmourDefinitionData data in parsed)
            {
                ItemDefinition asset = NewItemDefinition();
                Assert.DoesNotThrow(() => data.ApplyTo(asset), $"{data.id} failed to apply.");
                Assert.GreaterOrEqual(asset.armourLevels.Length, 1, data.id);
                Assert.AreEqual(asset.armourLevels[0].armor, asset.equipment.armor, data.id);
            }
        }

        private static int CountLevels(List<ArmourDefinitionData> parsed)
        {
            int total = 0;
            foreach (ArmourDefinitionData data in parsed) total += data.levels.Length;
            return total;
        }

        /// <summary>
        /// Looks for <c>AllData.as</c> beside the project. Returns <c>null</c> when it is not there —
        /// the caller then ignores rather than fails, because its absence is an environment fact.
        /// </summary>
        private static string FindAllDataAs()
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;

            string[] candidates =
            {
                Path.Combine(projectRoot, "AllData.as"),
                Path.Combine(projectRoot, "pfe", "scripts", "fe", "AllData.as"),
                Path.Combine(projectRoot, "..", "pfe", "scripts", "fe", "AllData.as"),
            };

            foreach (string candidate in candidates)
                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);

            return null;
        }
    }
}
