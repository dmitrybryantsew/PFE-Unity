using System.Collections.Generic;
using NUnit.Framework;
using PFE.Data.Definitions;

namespace PFE.Tests.EditMode.Systems.Weapons
{
    /// <summary>
    /// Pins <see cref="WeaponVisSymbolRule"/> — which SWF symbols the weapon-sprite importer treats as
    /// a weapon's held art.
    ///
    /// <para><b>Why this fixture exists.</b> The rule it replaces was a hand-maintained blocklist of
    /// excluded name prefixes, and it was wrong for 44 real weapons — every throwable and every mine
    /// among them. Nothing went red: the import simply created no
    /// <c>WeaponVisualDefinition</c> for them, <c>WeaponDefinition.weaponVisual</c> stayed
    /// <c>null</c>, and the symptom surfaced much later and much further away as "there is no graphics
    /// on throwables and mines". A rule that decides what gets imported has to be pinned where the
    /// offline wall can see it, which is why it lives in a runtime assembly and not in the importer.</para>
    ///
    /// <para><b>Every positive case is paired with an absent control.</b> The rule's failure mode is
    /// accepting everything (<c>^vis</c>), which would import the map objects, traps and robot bodies
    /// as weapons; and the blocklist's failure mode was accepting nothing for a whole family. A test
    /// that only asserts the acceptances cannot tell those apart, so each one names a symbol that must
    /// be rejected on the same input.</para>
    ///
    /// <para><b>Where the fixture data comes from.</b> The weapon ids and symbol names are transcribed
    /// from the real <c>AllData.as</c> rows and the real export's <c>symbols.csv</c>, and the ids used
    /// in the "must reject" cases are asserted absent from the same set — so a fixture that
    /// accidentally contained <c>damgren</c> as a weapon id would fail rather than silently pass.</para>
    /// </summary>
    [TestFixture]
    public class WeaponVisSymbolRuleTests
    {
        /// <summary>
        /// The real weapon ids this fixture needs: the throwables and mines whose visuals the blocklist
        /// dropped, plus the two whose symbols collided with a shorter excluded prefix, plus the
        /// controls.
        /// </summary>
        private static readonly string[] WeaponIds =
        {
            // throwables (throwTip 0) — grenades, bottles, dynamite
            "acidgr", "molotov", "plagr", "fgren", "gasgr", "grenade", "hgren", "impgr", "spgren",
            "dinamit", "pipe",
            // sticky throwables (throwTip 2)
            "dbomb", "bomb", "exc4", "mercgr", "drongr",
            // mines (throwTip 1)
            "mine", "cryomine", "plamine", "impmine", "x37", "zebmine", "balemine", "hmine",
            // the two whose symbols a SHORTER prefix swallowed
            "cryo", "cryogr",
            // ordinary weapons, present so the set is not only throwables
            "p10mm", "minigun", "laser",
            // explicitly NOT present, though a symbol of this name exists in the export:
            //   damgren, damshot, damexpl, box, trap, cur, curTarget, bulgren40
        };

        private static readonly HashSet<string> Ids =
            new HashSet<string>(WeaponIds, System.StringComparer.OrdinalIgnoreCase);

        // ── The bug: the whole throwable and mine family was excluded ──────────

        /// <summary>
        /// Every throwable and mine symbol the old prefix blocklist listed as an "environment/unit
        /// object" is in fact a weapon's held art. These are the exact names that were in the blocklist.
        /// </summary>
        [Test]
        public void EveryThrowableAndMineSymbol_IsWeaponArt_ThoughTheOldBlocklistRejectedIt()
        {
            string[] theOnesTheBlocklistDropped =
            {
                "visacidgr", "vismolotov", "visplagr", "visfgren", "visgasgr", "visgrenade",
                "vishgren", "visimpgr", "visspgren", "visdinamit", "vispipe",
                "visdbomb", "visbomb", "visexc4", "vismercgr", "visdrongr",
                "vismine", "viscryomine", "visplamine", "visimpmine", "visx37", "viszebmine",
                "visbalemine", "vishmine",
            };

            foreach (string symbol in theOnesTheBlocklistDropped)
            {
                Assert.IsTrue(
                    WeaponVisSymbolRule.IsWeaponVisSymbol(symbol, Ids),
                    $"'{symbol}' is a weapon's held art — its id is a <weapon> row in AllData.as. " +
                    "The old ExcludePrefixes blocklist rejected it, so no WeaponVisualDefinition was " +
                    "ever created and the weapon drew nothing.");
            }

            // Absent control, same input: the environment symbols that are NOT weapon art must still
            // be rejected, or this test would pass on a rule that simply accepts every `vis*` name.
            foreach (string symbol in new[] { "visdamgren", "visdamshot", "visdamexpl", "visbox", "vistrap" })
            {
                Assert.IsFalse(
                    WeaponVisSymbolRule.IsWeaponVisSymbol(symbol, Ids),
                    $"'{symbol}' has no <weapon> row, so it is not weapon art — the rule must not " +
                    "accept every vis* symbol.");
            }
        }

        /// <summary>
        /// The prefix-collision half of the same bug: the blocklist matched by <i>prefix</i>, so a
        /// three-to-five letter entry swallowed longer, unrelated weapon ids. These four pairs are the
        /// real collisions.
        /// </summary>
        [Test]
        public void AShortExcludedPrefix_NoLongerSwallowsALongerWeaponId()
        {
            var collisions = new (string BlocklistEntry, string SymbolItSwallowed, string WeaponId)[]
            {
                ("visbal", "visbalemine", "balemine"),
                ("viscry", "viscryomine", "cryomine"),
                ("viscry", "viscryogr",    "cryogr"),
                ("visdin", "visdinamit",   "dinamit"),
                ("vispip", "vispipe",      "pipe"),
                ("visexc", "visexc4",      "exc4"),
                ("visgren","visgrenade",   "grenade"),
            };

            foreach (var (blocklistEntry, symbol, weaponId) in collisions)
            {
                // The blocklist entry itself is a prefix of the symbol but is NOT a weapon id, which is
                // exactly why a prefix test misfires and an exact id test cannot.
                Assert.IsFalse(Ids.Contains(blocklistEntry),
                    $"fixture check: '{blocklistEntry}' is not a weapon id, so it is a pure prefix");

                Assert.IsTrue(Ids.Contains(weaponId),
                    $"fixture check: '{weaponId}' is a weapon id");

                Assert.IsTrue(WeaponVisSymbolRule.IsWeaponVisSymbol(symbol, Ids),
                    $"'{symbol}' belongs to weapon '{weaponId}' and must be accepted; the blocklist " +
                    $"rejected it because '{blocklistEntry}' was a prefix of it");
            }
        }

        // ── The rule itself ───────────────────────────────────────────────────

        [Test]
        public void VisPlusAWeaponId_IsAccepted()
        {
            Assert.IsTrue(WeaponVisSymbolRule.IsWeaponVisSymbol("vismine", Ids),
                "vis + a weapon id is the convention Weapon.as:496 and Mine.as:98 both use");
        }

        [Test]
        public void VisPlusANonWeaponId_IsRejected()
        {
            Assert.IsFalse(WeaponVisSymbolRule.IsWeaponVisSymbol("visdamgren", Ids),
                "'damgren' is a map object, not a <weapon> row");
        }

        /// <summary>
        /// The bullet-art family. These are real symbols in the export, and the rule must keep them
        /// out: they belong to <c>projectileVisual</c>, which is a different field wired by a different
        /// importer, and importing them here would create 40-odd spurious weapon visuals.
        /// </summary>
        [Test]
        public void TheVisBulletFamily_IsRejected()
        {
            foreach (string symbol in new[] { "visbulgren40", "visbulgren50", "visbulplasma", "visualBullet" })
            {
                Assert.IsFalse(WeaponVisSymbolRule.IsWeaponVisSymbol(symbol, Ids),
                    $"'{symbol}' is a bullet/projectile visual, not a held weapon");
            }
        }

        [Test]
        public void TheBarePrefixAndEmptyNames_AreRejected()
        {
            Assert.IsFalse(WeaponVisSymbolRule.IsWeaponVisSymbol("vis", Ids),
                "'vis' with no id is not a symbol");
            Assert.IsFalse(WeaponVisSymbolRule.IsWeaponVisSymbol("", Ids));
            Assert.IsFalse(WeaponVisSymbolRule.IsWeaponVisSymbol(null, Ids));
            Assert.IsFalse(WeaponVisSymbolRule.IsWeaponVisSymbol("mine", Ids),
                "the prefix is required — 'mine' is the id, not the symbol");
        }

        [Test]
        public void WithNoWeaponIds_NothingIsAccepted()
        {
            Assert.IsFalse(WeaponVisSymbolRule.IsWeaponVisSymbol("vismine", null),
                "a null id set must not be treated as 'accept everything'");
            Assert.IsFalse(WeaponVisSymbolRule.IsWeaponVisSymbol("vismine", new string[0]));
        }

        /// <summary>
        /// A weapon may name a symbol that is not <c>vis</c> + its own id (<c>vis.@vweap</c>), and the
        /// importer's wiring step honours that. The import set has to contain it too, or the override
        /// resolves to a definition that was never created — the same silent-nothing failure.
        /// </summary>
        [Test]
        public void AnExplicitVweapOverride_IsAccepted_EvenWhenItIsNotVisPlusId()
        {
            var overrides = new[] { "visspecialGun" };

            Assert.IsFalse(WeaponVisSymbolRule.IsWeaponVisSymbol("visspecialGun", Ids),
                "control: without the override list it is rejected, so the next line proves the list");

            Assert.IsTrue(WeaponVisSymbolRule.IsWeaponVisSymbol("visspecialGun", Ids, overrides),
                "a symbol named by vis.@vweap must be importable");
        }

        [Test]
        public void OverrideSymbols_DoNotLetTheRestOfTheBlocklistBackIn()
        {
            // The override list is an addition, not a replacement: everything it does not name is
            // still decided by the weapon-id test.
            Assert.IsFalse(WeaponVisSymbolRule.IsWeaponVisSymbol("visdamgren", Ids, new[] { "visspecialGun" }),
                "an override list must not turn the rule into 'accept everything'");
        }
    }
}
