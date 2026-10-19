using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using PFE.Data.Definitions.Campaign;
using PFE.Systems.Loot;
using PFE.Systems.Map;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.Editor.Loot
{
    /// <summary>
    /// The raider family's <b>wielded-weapon drop</b> — AS3 <c>UnitRaider.dropLoot</c>
    /// (<c>UnitRaider.as:503-539</c>) and the hero upgrade that decides what the gun <i>is</i>
    /// (<c>:362-372</c>).
    ///
    /// <para><b>Why this is a fixture and not a play-test.</b> Three of the rules here are invisible in
    /// play: a <c>nodrop</c> attribute that is present with value <c>"0"</c> suppresses the drop exactly
    /// like <c>"1"</c>, a weapon with <c>uniq='0'</c> never upgrades while the <c>updVariant</c> guard it
    /// superficially resembles would allow it, and the hero roll only fires on <c>hero == 1</c> — not on
    /// the three armoured hero tiers above it. A hand test would report "guns drop" and "some are II" and
    /// pass with every one of those wrong.</para>
    ///
    /// <para><b>The census assertions read the real <c>AllData.as</c></b> — the same independent-reader
    /// approach as <see cref="UnitDeathDropTests"/> — so the counts quoted in the code comments cannot
    /// drift away from the data without a failure.</para>
    /// </summary>
    [TestFixture]
    public class EnemyWeaponDropRulesTests
    {
        // =====================================================================
        //  Who drops — `isDropArm`
        // =====================================================================

        [Test]
        public void IsDropArm_TheRaiderFamilyDrops()
        {
            // UnitRaider.as:47 — `public var isDropArm:Boolean = true;`. The field is declared true, so
            // the whole family drops unless something clears it. These are the shipped family ids.
            Assert.IsTrue(EnemyWeaponDropRules.IsDropArm(true, "raider", noDropAttribute: false));
            Assert.IsTrue(EnemyWeaponDropRules.IsDropArm(true, "slaver", noDropAttribute: false));
            Assert.IsTrue(EnemyWeaponDropRules.IsDropArm(true, "zebra", noDropAttribute: false));
            Assert.IsTrue(EnemyWeaponDropRules.IsDropArm(true, "merc", noDropAttribute: false));
        }

        [Test]
        public void IsDropArm_EnclAndRangerClearIt()
        {
            // UnitEncl.as:35 and UnitRanger.as:24 both set `isDropArm = false` in their constructors.
            // Both extend UnitRaider, so they share this controller and this rule — the family id is the
            // only thing that separates them.
            Assert.IsFalse(EnemyWeaponDropRules.IsDropArm(true, "encl", noDropAttribute: false));
            Assert.IsFalse(EnemyWeaponDropRules.IsDropArm(true, "ranger", noDropAttribute: false));
        }

        [Test]
        public void IsDropArm_NodropIsPresenceNotValue()
        {
            // UnitRaider.as:206-209 — `if(Boolean(param3) && Boolean(param3.@nodrop.length()))
            // isDropArm = false;`. `.length()` counts matching attributes, so the VALUE is never read.
            // The two shipped placements author nodrop="1", but nodrop="0" would suppress the drop just
            // as surely, and this is the assertion that keeps a `!= ""` / `== "1"` rewrite from passing.
            Assert.IsFalse(EnemyWeaponDropRules.IsDropArm(true, "slaver", noDropAttribute: true));
        }

        [Test]
        public void IsDropArm_ANonRaiderNeverDrops_HoweverItIsConfigured()
        {
            // UnitNecros extends Unit, not UnitRaider, so it inherits the base dropLoot — which never
            // looks at currentWeapon. The port shares this controller with it for convenience, so the
            // family flag is the only thing standing between a necrosprite and a dropped gun.
            Assert.IsFalse(EnemyWeaponDropRules.IsDropArm(false, "raider", noDropAttribute: false));
            Assert.IsFalse(EnemyWeaponDropRules.IsDropArm(false, null, noDropAttribute: false));
            Assert.IsFalse(EnemyWeaponDropRules.IsDropArm(false, "necros", noDropAttribute: false));
        }

        [Test]
        public void IsDropArm_AnUnknownFamilyIdDrops()
        {
            // The answer is "true unless excluded", never "true only for a known id": a mod that adds a
            // new raider subclass must inherit the drop, exactly as AS3's subclass would.
            Assert.IsTrue(EnemyWeaponDropRules.IsDropArm(true, null, noDropAttribute: false));
            Assert.IsTrue(EnemyWeaponDropRules.IsDropArm(true, string.Empty, noDropAttribute: false));
            Assert.IsTrue(EnemyWeaponDropRules.IsDropArm(true, "some_mod_family", noDropAttribute: false));
        }

        [Test]
        public void IsDropArm_EnclIsNotARangerAndNeitherIsAPrefix()
        {
            // Ordinal equality, not a substring test: a family called "enclave" or "ranger2" must not be
            // mistaken for the two classes that clear the flag. A `StartsWith` here would silently
            // disarm every future subclass of either.
            Assert.IsTrue(EnemyWeaponDropRules.IsDropArm(true, "enclave", noDropAttribute: false));
            Assert.IsTrue(EnemyWeaponDropRules.IsDropArm(true, "ranger2", noDropAttribute: false));
            Assert.IsTrue(EnemyWeaponDropRules.IsDropArm(true, "ENCL", noDropAttribute: false));
        }

        // =====================================================================
        //  What is dropped — the `id^N` composition
        // =====================================================================

        [Test]
        public void DropId_AppendsTheTierOnlyAboveZero()
        {
            // UnitRaider.as:516-520 — `_loc1_ = currentWeapon.id; if(currentWeapon.variant > 0)
            // _loc1_ += "^" + currentWeapon.variant;`. Note `> 0`: a weapon with no variant is dropped as
            // its plain id, never as "carbine^0".
            Assert.AreEqual("carbine", EnemyWeaponDropRules.DropId("carbine", 0));
            Assert.AreEqual("carbine", EnemyWeaponDropRules.DropId("carbine", -1));
            Assert.AreEqual("carbine^1", EnemyWeaponDropRules.DropId("carbine", 1));
        }

        [Test]
        public void DropId_RoundTripsThroughTheSharedSplitter()
        {
            // The drop composes the id and the loot generator splits it again (Item.as:132-140). Those
            // are two separate functions, so the pair is what has to hold: composing here and failing to
            // parse there would drop the base weapon silently, and a "carbine^1" that parsed as id
            // "carbine^1" would resolve to no weapon at all.
            CampaignDataParser.SplitVariant(EnemyWeaponDropRules.DropId("lsword", 1),
                                            out string baseId, out int variant);
            Assert.AreEqual("lsword", baseId);
            Assert.AreEqual(1, variant);

            // A plain weapon must survive the same round trip unchanged.
            CampaignDataParser.SplitVariant(EnemyWeaponDropRules.DropId("lsword", 0),
                                            out baseId, out variant);
            Assert.AreEqual("lsword", baseId);
            Assert.AreEqual(0, variant);
        }

        [Test]
        public void DropId_PassesEmptyAndNullThrough()
        {
            // A unit that rolled nothing never reaches this, but the helper must not turn "no id" into
            // "^1" — that would resolve to a weapon named "" and be reported as a missing table row.
            Assert.IsNull(EnemyWeaponDropRules.DropId(null, 1));
            Assert.AreEqual(string.Empty, EnemyWeaponDropRules.DropId(string.Empty, 1));
        }

        [Test]
        public void DropId_UsesTheAsciiCaretAndNoWhitespace()
        {
            // A stray space ("carbine ^1") would defeat the splitter's `Length - 2` test and the whole
            // drop would resolve to nothing. Cheap, but this is exactly the character a copy-paste from
            // the AS3 concatenation gets wrong.
            string id = EnemyWeaponDropRules.DropId("carbine", 1);
            Assert.AreEqual("carbine^1", id);
            Assert.AreEqual('^', EnemyWeaponDropRules.VariantSeparator);
            Assert.AreEqual(id.Length - 2, id.LastIndexOf('^'));
        }

        [Test]
        public void TheTierTheHeroRuleInstallsIsTheOneShippedDataDefines()
        {
            // `updVariant(1)` is the oracle's argument and `WeaponVariantXml.FirstVariant` is the tier
            // the importer reads. If either moved, the hero raider would hold a variant no asset defines
            // and the drop would name an id nothing resolves.
            Assert.AreEqual(1, EnemyWeaponDropRules.HeroVariant);
            Assert.AreEqual(1, PFE.Systems.Weapons.WeaponVariantXml.FirstVariant);
        }

        // =====================================================================
        //  The hero roll — `uniq > 0 && isrnd(uniq / 2)`
        // =====================================================================

        [Test]
        public void ShouldUpgradeToHeroVariant_NoComMeansNoVariant()
        {
            // Weapon.as:100 — `public var uniq:Number = -1;`, assigned only when `<com uniq>` is present.
            // 142 of the 213 shipped weapons are -1, and they have no tier 1 to upgrade TO, so the rule
            // must refuse before it draws a random number at all.
            Assert.IsFalse(EnemyWeaponDropRules.ShouldUpgradeToHeroVariant(-1f, 0f));
            Assert.IsFalse(EnemyWeaponDropRules.ShouldUpgradeToHeroVariant(-1f, 0.9999f));
        }

        [Test]
        public void ShouldUpgradeToHeroVariant_ZeroNeverUpgrades_EvenThoughUpdVariantWouldAllowIt()
        {
            // THE trap of this rule. UnitRaider.as:367 tests `uniq > 0`, so the 45 weapons authoring
            // uniq='0' never upgrade on a hero raider — while Weapon.updVariant's own guard is only
            // `uniq < 0` (Weapon.as:895-900) and WOULD accept them. The two tests are not
            // interchangeable, and unifying them would hand 45 weapons a tier-1 variant the oracle never
            // gives them.
            Assert.IsFalse(EnemyWeaponDropRules.ShouldUpgradeToHeroVariant(0f, 0f));
            Assert.IsFalse(EnemyWeaponDropRules.ShouldUpgradeToHeroVariant(0f, 0.0001f));
        }

        [Test]
        public void ShouldUpgradeToHeroVariant_UniqOneIsExactlyAHalf()
        {
            // isrnd(x) is `Math.random() < x` (Unit.as:4855-4858) and Math.random() is [0,1), so the
            // chance is uniq/2. 11 shipped weapons author uniq='1' — a live 50 % on every hero raider
            // holding one of them.
            Assert.IsTrue(EnemyWeaponDropRules.ShouldUpgradeToHeroVariant(1f, 0f));
            Assert.IsTrue(EnemyWeaponDropRules.ShouldUpgradeToHeroVariant(1f, 0.4999f));
            Assert.IsFalse(EnemyWeaponDropRules.ShouldUpgradeToHeroVariant(1f, 0.5f),
                "isrnd is `<`, so the boundary itself fails.");
            Assert.IsFalse(EnemyWeaponDropRules.ShouldUpgradeToHeroVariant(1f, 0.9999f));
        }

        [Test]
        public void ShouldUpgradeToHeroVariant_ScalesWithUniq()
        {
            // The fractional rows are real: 0.5 (3 weapons), 0.3 (2), 0.2 (2), 0.05 (2), and five
            // singletons. Each boundary is checked just below the line and exactly on it.
            Assert.IsTrue(EnemyWeaponDropRules.ShouldUpgradeToHeroVariant(0.5f, 0.2499f));
            Assert.IsFalse(EnemyWeaponDropRules.ShouldUpgradeToHeroVariant(0.5f, 0.25f));

            Assert.IsTrue(EnemyWeaponDropRules.ShouldUpgradeToHeroVariant(0.3f, 0.1499f));
            Assert.IsFalse(EnemyWeaponDropRules.ShouldUpgradeToHeroVariant(0.3f, 0.15f));

            Assert.IsTrue(EnemyWeaponDropRules.ShouldUpgradeToHeroVariant(0.05f, 0.0249f));
            Assert.IsFalse(EnemyWeaponDropRules.ShouldUpgradeToHeroVariant(0.05f, 0.025f));
        }

        [Test]
        public void TheHeroTierThatUpgradesIsOne_NotEveryHeroTier()
        {
            // UnitRaider.as:367 — `if(hero == 1)`. Tiers 2, 3 and 4 are Unit.setHero's
            // armoured/damage tiers (Unit.as:1651-1665) and do not touch the weapon. A rule written as
            // `hero > 0` would upgrade the weapon of every armoured hero in the game.
            Assert.AreEqual(1, EnemyWeaponDropRules.HeroTierThatUpgradesTheWeapon);
            Assert.AreNotEqual(0, EnemyWeaponDropRules.HeroTierThatUpgradesTheWeapon,
                "tier 0 is 'not a hero at all', which never reaches this rule.");
        }

        // =====================================================================
        //  Taking it — Invent.take's upgrade branch
        // =====================================================================

        [Test]
        public void ShouldUpgradeOwnedWeapon_OnlyForAStrictlyHigherTier()
        {
            // Invent.as:1076-1085 — `if(weapons[id].variant < l.variant) updWeapon(id, l.variant);`.
            Assert.IsTrue(EnemyWeaponDropRules.ShouldUpgradeOwnedWeapon(
                ownedVariant: 0, incomingVariant: 1, uniq: 1f));
            Assert.IsFalse(EnemyWeaponDropRules.ShouldUpgradeOwnedWeapon(1, 1, 1f),
                "the same tier is not an upgrade — a second plain gun must not reset the first one's hp.");
            Assert.IsFalse(EnemyWeaponDropRules.ShouldUpgradeOwnedWeapon(1, 0, 1f),
                "a LOWER tier never downgrades the copy already held.");
        }

        [Test]
        public void ShouldUpgradeOwnedWeapon_ZeroStillUpgrades_UnlikeTheHeroRoll()
        {
            // The deliberate asymmetry. This is Weapon.updVariant's guard (`uniq < 0`, Weapon.as:895),
            // not UnitRaider.setHero's (`uniq > 0`). A uniq='0' weapon has a tier 1 that simply never
            // drops as random loot — so picking one up by any other route must still upgrade the bag.
            Assert.IsTrue(EnemyWeaponDropRules.ShouldUpgradeOwnedWeapon(0, 1, 0f));
            Assert.IsFalse(EnemyWeaponDropRules.ShouldUpgradeOwnedWeapon(0, 1, -1f),
                "a weapon with no <com> has no tier 1 to upgrade to.");
        }

        [Test]
        public void ShouldUpgradeOwnedWeapon_AndTheHeroRollDisagreeOnZero_OnPurpose()
        {
            // Pinned as a pair, because "they look like the same test" is exactly how one of them gets
            // refactored into the other. Both read the same `uniq` and both are about variant 1.
            Assert.IsFalse(EnemyWeaponDropRules.ShouldUpgradeToHeroVariant(0f, 0f),
                "a hero raider's uniq='0' weapon never comes out as variant 1.");
            Assert.IsTrue(EnemyWeaponDropRules.ShouldUpgradeOwnedWeapon(0, 1, 0f),
                "…but taking a variant-1 copy of it still upgrades the one in the bag.");
        }

        // =====================================================================
        //  The plumbing the rules sit on
        // =====================================================================

        [Test]
        public void HasAttribute_SeesNodropWhateverItsValue()
        {
            // The rule above takes a bool; this is where the bool comes from. BuildAttributes is what the
            // room converter uses for a placement's attributes (AS3ToUnityConverter.cs:270) and it copies
            // every key except the five structural ones — so `nodrop` survives into
            // UnitInstance.attributes and HasAttribute can answer.
            var attributes = new Dictionary<string, string>
            {
                { "x", "43" }, { "y", "3" }, { "id", "slaver" }, { "code", "PMDmEIR2fuGLdkFM" },
                { "uid", "u1" }, { "weap", "anti" }, { "nodrop", "0" },
            };

            List<MapObjectAttributeData> built = MapObjectDataUtility.BuildAttributes(
                attributes, "x", "y", "id", "code", "uid");

            Assert.IsTrue(MapObjectDataUtility.HasAttribute(built, "nodrop"),
                "`nodrop` must survive the conversion, or IsDropArm's third argument is always false and " +
                "the two shipped slaver placements would drop their guns.");
            Assert.IsTrue(MapObjectDataUtility.HasAttribute(built, "weap"));

            // The value is irrelevant to presence — `nodrop="0"` is present.
            Assert.AreEqual("0", MapObjectDataUtility.GetAttribute(built, "nodrop"));
        }

        [Test]
        public void HasAttribute_ReportsAbsenceForAPlacementWithoutIt()
        {
            // The control. Without it, a HasAttribute that always returned true would satisfy the test
            // above — and would silently disarm every raider in the game.
            var attributes = new Dictionary<string, string>
            {
                { "x", "1" }, { "y", "2" }, { "id", "raider5" }, { "weap", "mach" },
            };

            List<MapObjectAttributeData> built = MapObjectDataUtility.BuildAttributes(
                attributes, "x", "y", "id", "code", "uid");

            Assert.IsFalse(MapObjectDataUtility.HasAttribute(built, "nodrop"));
        }

        [Test]
        public void TheDroppedWeaponConditionBandIsKolZero()
        {
            // LootGen.lootId(loc, X, Y, id, 0) — UnitRaider.as:521. That fifth argument is `kol`, and
            // kol == 0 is what rolls 0.05..0.20 (Item.as:185-188). Passing 1 instead would make every
            // corpse's gun 60–85 %, and passing nothing would make it pristine — both look plausible in
            // play, which is why the constant is asserted rather than inlined.
            Assert.AreEqual(0, EnemyWeaponDropRules.DroppedWeaponKol);
        }

        // =====================================================================
        //  The real data — censuses that keep the comments honest
        // =====================================================================

        [Test]
        public void RealAllData_TheUniqCensus()
        {
            UniqCensus census = ReadUniqCensus();
            if (census == null) Assert.Ignore("AllData.as is not at the project root.");

            Assert.AreEqual(213, census.Weapons, "the shipped weapon rows.");

            // Weapon.as:100 — uniq defaults to -1 and is assigned only on presence. Two shapes reach
            // that default: no <com> at all, and a <com> that omits uniq.
            Assert.AreEqual(142, census.Absent, "weapons with no uniq — the default -1.");
            Assert.AreEqual(93, census.NoComNode, "…of which this many have no <com> node at all.");
            Assert.AreEqual(49, census.ComWithoutUniq, "…and this many have a <com> that omits uniq.");

            Assert.AreEqual(45, census.Zero, "uniq='0' — has a tier 1 that never drops as random loot.");
            Assert.AreEqual(11, census.One, "uniq='1' — the 50 % hero upgrade.");
            Assert.AreEqual(26, census.Positive,
                "uniq > 0 — the only weapons a hero raider's setHero can upgrade.");

            // The partition must be exhaustive: a parser change that mis-read one row would break it.
            Assert.AreEqual(census.Weapons, census.Absent + census.Zero + census.Positive);
        }

        [Test]
        public void RealAllData_HeroRowsAreSixRaiderRowsOutOfThirtyFour()
        {
            HeroCensus census = ReadHeroCensus();
            if (census == null) Assert.Ignore("AllData.as is not at the project root.");

            // `mHero` is set by the unit ROW's `<param hero='…'>` (Unit.as:1371-1375), and the value is a
            // hero *name* — `hero='bloat'`, `hero='zebra'` — not a number. `hero` itself is the numeric
            // tier, assigned by setHero's argument.
            Assert.AreEqual(34, census.RowsWithHeroParam, "shipped <param hero> rows.");
            Assert.AreEqual(19, census.HeroNames.Count, "…over 19 distinct hero names.");

            // Exactly six of them are raider-family rows, and they are the six family base rows — the
            // only raider rows a shipped placement ever gives a `hero` attribute to.
            Assert.AreEqual(6, census.RaiderRowsWithHeroParam,
                "raider/slaver/zebra/merc/ranger/encl — one row each.");
            foreach (string family in new[] { "raider", "slaver", "zebra", "merc", "ranger", "encl" })
            {
                Assert.IsTrue(census.HeroNames.Contains(family), $"'{family}' is a hero name.");
            }

            // …and 32 of the 38 raider-family rows are NOT mHero rows. That gap is why the port's gate is
            // "is this family a raider" rather than "is this row an mHero row": the row-level flag is not
            // imported, and the substitution is exact only because hero placements target base rows.
            Assert.AreEqual(38, census.RaiderFamilyRows);
            Assert.AreEqual(32, census.RaiderFamilyRows - census.RaiderRowsWithHeroParam,
                "raider1..9, slaver1..6, zebra1..5, merc1..5, ranger1..3, encl1..4 carry no hero param.");

            // The one family the port shares this controller with, but which AS3 does not treat as a
            // raider, carries none — which is what makes excluding it exact rather than lucky.
            Assert.IsFalse(census.HeroNames.Contains("necros"),
                "UnitNecros extends Unit and authors no <param hero>, so a necros hero must never take " +
                "the weapon upgrade.");
        }

        // =====================================================================
        //  helpers
        // =====================================================================

        /// <summary>
        /// The <c>&lt;weapon&gt;</c> rows' <c>&lt;com uniq&gt;</c>, counted by shape.
        /// <para><b>A second, independent reader</b> of the same file the importer reads, and deliberately
        /// not a re-implementation of <c>WeaponDataImporter</c>: a reader tuned to a fixture could not
        /// reproduce these numbers.</para>
        /// </summary>
        private sealed class UniqCensus
        {
            public int Weapons;
            public int Absent;
            public int NoComNode;
            public int ComWithoutUniq;
            public int Zero;
            public int Positive;
            public int One;
        }

        /// <summary>The <c>&lt;param hero&gt;</c> rows, by name, and the raider-family total.</summary>
        private sealed class HeroCensus
        {
            public int RowsWithHeroParam;
            public int RaiderRowsWithHeroParam;
            public int RaiderFamilyRows;
            public readonly HashSet<string> HeroNames = new HashSet<string>();
        }

        private static UniqCensus ReadUniqCensus()
        {
            string text = ReadAllData();
            if (text == null) return null;

            var census = new UniqCensus();

            // Each <weapon …> body runs to the next <weapon, which is how the rows are laid out.
            MatchCollection rows = Regex.Matches(text, "<weapon\\b([^>]*?)/?>");
            census.Weapons = rows.Count;

            for (int i = 0; i < rows.Count; i++)
            {
                int start = rows[i].Index + rows[i].Length;
                int end = i + 1 < rows.Count ? rows[i + 1].Index : text.Length;
                string body = text.Substring(start, end - start);

                Match com = Regex.Match(body, "<com\\b([^>]*?)/?>");
                if (!com.Success)
                {
                    census.Absent++;
                    census.NoComNode++;
                    continue;
                }

                Match uniq = Regex.Match(com.Groups[1].Value, "uniq='([^']*)'");
                if (!uniq.Success)
                {
                    census.Absent++;
                    census.ComWithoutUniq++;
                    continue;
                }

                float value = float.Parse(uniq.Groups[1].Value,
                                          System.Globalization.CultureInfo.InvariantCulture);
                if (value > 0f)
                {
                    census.Positive++;
                    if (value == 1f) census.One++;
                }
                else if (value == 0f)
                {
                    census.Zero++;
                }
                else
                {
                    // uniq < 0 is authored nowhere; it is the ABSENT value in AS3 and is never written.
                    Assert.Fail($"a weapon authors uniq='{uniq.Groups[1].Value}' — negative is the " +
                                "absent value and is never written to the file.");
                }
            }

            return census;
        }

        private static HeroCensus ReadHeroCensus()
        {
            string text = ReadAllData();
            if (text == null) return null;

            var census = new HeroCensus();

            // Every raider family this controller treats as a dropper (see EnemyWeaponDropRules).
            var raiderFamilies = new HashSet<string>
            {
                "raider", "slaver", "zebra", "merc", "ranger", "encl",
            };

            MatchCollection rows = Regex.Matches(text, "<unit\\b([^>]*?)/?>");
            for (int i = 0; i < rows.Count; i++)
            {
                Match id = Regex.Match(rows[i].Groups[1].Value, "id='([^']*)'");
                if (!id.Success) continue;

                Match parent = Regex.Match(rows[i].Groups[1].Value, "parent='([^']*)'");
                string family = parent.Success ? parent.Groups[1].Value : id.Groups[1].Value;
                bool isRaider = raiderFamilies.Contains(family);
                if (isRaider) census.RaiderFamilyRows++;

                int start = rows[i].Index + rows[i].Length;
                int end = i + 1 < rows.Count ? rows[i + 1].Index : text.Length;
                string body = text.Substring(start, end - start);
                int close = body.IndexOf("</unit>", System.StringComparison.Ordinal);
                if (close >= 0) body = body.Substring(0, close);

                Match hero = Regex.Match(body, "<param\\b[^>]*hero='([^']*)'");
                if (!hero.Success) continue;

                census.RowsWithHeroParam++;
                census.HeroNames.Add(hero.Groups[1].Value);
                if (isRaider) census.RaiderRowsWithHeroParam++;
            }

            return census;
        }

        private static string ReadAllData()
        {
            string root = SourceLint.ProjectRoot();
            string[] candidates =
            {
                Path.Combine(root, "AllData.as"),
                Path.Combine(root, "pfe", "scripts", "fe", "AllData.as"),
            };

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate)) return File.ReadAllText(candidate);
            }

            return null;
        }
    }
}
