using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using PFE.Systems.Loot;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.Editor.Loot
{
    /// <summary>
    /// D4/D11 of <c>docs/DeviceInteractionAndLoot/04_IMPLEMENTATION_PLAN.md</c>: the key a dying unit
    /// drops from, and the two bonus gates of AS3 <c>Unit.dropLoot()</c> (<c>Unit.as:4450-4467</c>).
    ///
    /// <para><b>The key resolution is the part worth pinning.</b> AS3 never stores the drop key on the
    /// unit row: <c>Unit.as:1005-1009</c> sets <c>inter.cont = mid</c> (the unit's <i>id</i>) and then
    /// lets the row's own <c>@cont</c> override it. A port that read <c>@cont</c> alone would silently
    /// starve all 45 units that drop from a table named after their own id (<c>rat</c>, <c>slime</c>,
    /// <c>ant1</c>, …); one that added a <c>parent</c> fallback would invent a rule the oracle does not
    /// have. The census numbers below are measured from the real file, so neither mistake can pass.</para>
    /// </summary>
    [TestFixture]
    public class UnitDeathDropTests
    {
        // =====================================================================
        //  Key resolution
        // =====================================================================

        [Test]
        public void ResolveLootTableKey_AuthoredContWins()
        {
            // raider5 authors cont='raider' (AllData.as), so it rolls the raider table, not raider5.
            Assert.AreEqual("raider", UnitDeathDrop.ResolveLootTableKey("raider", null, "raider5"));
            // ...and it wins even over a legacy controllerId, which holds the same value pre-reimport.
            Assert.AreEqual("raider", UnitDeathDrop.ResolveLootTableKey("raider", "zombie", "raider5"));
        }

        [Test]
        public void ResolveLootTableKey_FallsBackToTheUnitId_WhenNoContIsAuthored()
        {
            // rat authors no cont, so AS3 rolls the table keyed "rat" — the unit's own id.
            Assert.AreEqual("rat", UnitDeathDrop.ResolveLootTableKey(null, null, "rat"));
            Assert.AreEqual("slime", UnitDeathDrop.ResolveLootTableKey(string.Empty, string.Empty, "slime"));

            // A unit that is neither: still its own id, which simply names no table and drops nothing.
            // This is the honest answer, not a failure — 63 of the 148 unit rows resolve this way.
            Assert.AreEqual("littlepip", UnitDeathDrop.ResolveLootTableKey(null, null, "littlepip"));
        }

        [Test]
        public void ResolveLootTableKey_DoesNotWalkToAParent()
        {
            // The trap. ant1 has parent='ant' and authors no cont. A "then the parent" step would return
            // "ant" — but AS3 reads only the unit's OWN row's @cont (Unit.as:996-1009) and would key the
            // drop "ant1". There is no parent chain for `cont`; Unit.as never reads `parent` at all.
            Assert.AreEqual("ant1", UnitDeathDrop.ResolveLootTableKey(null, null, "ant1"));
            Assert.AreNotEqual("ant", UnitDeathDrop.ResolveLootTableKey(null, null, "ant1"));
        }

        [Test]
        public void ResolveLootTableKey_UsesTheLegacyControllerIdOnlyWhenItNamesARealTable()
        {
            // TRANSITIONAL branch: before the unit re-import, `cont` lives only in controllerId. It is
            // accepted only when it names a table the roller actually has — so it can never invent one.
            Assert.AreEqual("raider", UnitDeathDrop.ResolveLootTableKey(null, "raider", "raider5"));
            Assert.AreEqual("scorp", UnitDeathDrop.ResolveLootTableKey(null, "scorp", "scorp1"));

            // A controllerId that is NOT a drop key (an <obj> cl like "UnitRaider", or a stray value) is
            // ignored, and the id stands. This is what keeps the branch from becoming a silent wildcard.
            Assert.AreEqual("raider5", UnitDeathDrop.ResolveLootTableKey(null, "UnitRaider", "raider5"));
            Assert.AreEqual("rat", UnitDeathDrop.ResolveLootTableKey(null, "not_a_key", "rat"));
        }

        // =====================================================================
        //  The real file — the two halves of the rule, measured
        // =====================================================================

        [Test]
        public void RealAllData_EveryAuthoredContNamesATableTheRollerHas()
        {
            List<(string Id, string Cont)> rows = ReadUnitRows();
            if (rows == null) Assert.Ignore("AllData.as is not at the project root.");

            int withCont = 0;
            var distinct = new HashSet<string>();
            foreach ((string id, string cont) in rows)
            {
                if (string.IsNullOrEmpty(cont)) continue;
                withCont++;
                distinct.Add(cont);

                Assert.IsTrue(LootRoller.IsKnownUnitKey(cont),
                    $"unit '{id}' authors cont='{cont}', which names no lootDrop table — the key and the " +
                    "table list have drifted apart.");
            }

            Assert.AreEqual(40, withCont, "40 of the 148 unit rows author cont.");
            Assert.AreEqual(7, distinct.Count, "…and they name 7 distinct tables.");

            // The seven, with their multiplicities — the census, spelled out.
            var expected = new Dictionary<string, int>
            {
                { "raider", 25 }, { "zombie", 4 }, { "turret", 4 }, { "scorp", 3 },
                { "turret1", 2 }, { "protect", 1 }, { "gutsy", 1 },
            };
            foreach (var pair in expected)
            {
                Assert.IsTrue(distinct.Contains(pair.Key), $"'{pair.Key}' should be one of the authored keys.");
            }
        }

        [Test]
        public void RealAllData_TheIdFallbackIsWhatCoversTheUnitsWithNoCont()
        {
            List<(string Id, string Cont)> rows = ReadUnitRows();
            if (rows == null) Assert.Ignore("AllData.as is not at the project root.");

            int coveredByIdFallback = 0;
            foreach ((string id, string cont) in rows)
            {
                if (!string.IsNullOrEmpty(cont)) continue;
                if (LootRoller.IsKnownUnitKey(UnitDeathDrop.ResolveLootTableKey(null, null, id)))
                {
                    coveredByIdFallback++;
                }
            }

            // 45 units drop from a table named after their own id — rat, slime, ant1..3, bloodwing,
            // bloat0..4, zombie4..9, alicorn1..3, ranger1..3, encl2..4, fish1/2, necros, ebloat, eqd,
            // sentinel, robobrain, protect, gutsy, hellhound1, molerat, pinkslime, spritebot, vortex,
            // roller, turret, raider, zombie. Without the id fallback all of them would drop nothing.
            Assert.AreEqual(45, coveredByIdFallback,
                "the id fallback is the only thing that gives these units a table.");
        }

        // =====================================================================
        //  The bonus gates
        // =====================================================================

        [Test]
        public void ShouldDropEssence_IsGatedOnHeroAndNotARobot()
        {
            // AS3 :4456 — hero > 0 && opt.robot != true && isrnd(0.75).
            Assert.IsTrue(UnitDeathDrop.ShouldDropEssence(heroTier: 3, isRobot: false, rnd: 0.0f));
            Assert.IsTrue(UnitDeathDrop.ShouldDropEssence(1, false, 0.7499f));

            Assert.IsFalse(UnitDeathDrop.ShouldDropEssence(1, false, 0.75f),
                "isrnd(0.75) is `rnd < 0.75` — the boundary fails.");
            Assert.IsFalse(UnitDeathDrop.ShouldDropEssence(0, false, 0.0f),
                "an ordinary unit (hero == 0) drops no essence, however lucky the roll.");
            Assert.IsFalse(UnitDeathDrop.ShouldDropEssence(3, true, 0.0f),
                "a robot hero drops no essence (opt.robot).");
        }

        [Test]
        public void ShouldDropGem_ScalesWithPerkLevelAndXp()
        {
            // AS3 :4462 — rnd < dropTre * xp / 4000.
            Assert.IsFalse(UnitDeathDrop.ShouldDropGem(0f, 500, 0.0f), "dropTre == 0 is the whole gate.");

            // rat: xp 20, dropTre 5 -> 5*20/4000 = 0.025.
            Assert.IsTrue(UnitDeathDrop.ShouldDropGem(5f, 20, 0.0249f));
            Assert.IsFalse(UnitDeathDrop.ShouldDropGem(5f, 20, 0.025f), "the boundary fails (`<`).");

            // sentinel: xp 2000, dropTre 5 -> 2.5, i.e. always.
            Assert.IsTrue(UnitDeathDrop.ShouldDropGem(5f, 2000, 0.9999f));

            Assert.IsFalse(UnitDeathDrop.ShouldDropGem(5f, 0, 0.0f), "a unit worth no xp drops no gem.");
        }

        [Test]
        public void RollGemId_IsAlwaysOneOfThree()
        {
            Assert.AreEqual("gem1", UnitDeathDrop.RollGemId(0f));
            Assert.AreEqual("gem2", UnitDeathDrop.RollGemId(0.5f));
            Assert.AreEqual("gem3", UnitDeathDrop.RollGemId(0.9999f));

            // Math.random() is [0,1), so the floor is 1..3 — never gem0 and never gem4.
            for (int i = 0; i < 100; i++)
            {
                string id = UnitDeathDrop.RollGemId(i / 100f);
                Assert.IsTrue(id == "gem1" || id == "gem2" || id == "gem3", id);
            }
        }

        // =====================================================================
        //  The drop seam
        // =====================================================================

        [Test]
        public void ThePlayerOverridesTheDropSeam_SoItsOwnDeathRollsNothing()
        {
            // UnitController.DropsLootOnDeath is true for every world unit; PlayerController overrides it
            // to false, because the player is not a loot source and its id names no table. Reflection on
            // the declaring type is the instance-free way to assert the override still exists: delete it
            // and GetProperty walks up to the base declaration.
            const System.Reflection.BindingFlags Flags =
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

            var baseProp = typeof(PFE.Entities.Units.UnitController)
                .GetProperty("DropsLootOnDeath", Flags);
            Assert.IsNotNull(baseProp, "UnitController.DropsLootOnDeath is the seam the drop path reads.");
            Assert.AreEqual(typeof(PFE.Entities.Units.UnitController), baseProp.DeclaringType);

            var playerProp = typeof(PFE.Entities.Player.PlayerController)
                .GetProperty("DropsLootOnDeath", Flags);
            Assert.IsNotNull(playerProp);
            Assert.AreEqual(typeof(PFE.Entities.Player.PlayerController), playerProp.DeclaringType,
                "PlayerController must override DropsLootOnDeath, or the player's death runs the drop " +
                "path and logs an unmatched-key warning for 'littlepip' on every death.");
        }

        // =====================================================================
        //  helpers
        // =====================================================================

        /// <summary>
        /// Every <c>&lt;unit&gt;</c> row's id and <c>cont</c>, in file order. Deliberately a second,
        /// independent reader of the same file the importer reads — a reader tuned to a fixture could not
        /// satisfy the census.
        /// </summary>
        private static List<(string Id, string Cont)> ReadUnitRows()
        {
            string root = SourceLint.ProjectRoot();
            string[] candidates =
            {
                Path.Combine(root, "AllData.as"),
                Path.Combine(root, "pfe", "scripts", "fe", "AllData.as"),
            };

            string text = null;
            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate)) { text = File.ReadAllText(candidate); break; }
            }
            if (text == null) return null;

            var rows = new List<(string, string)>();
            foreach (Match m in Regex.Matches(text, @"<unit\s([^>]*?)/?>"))
            {
                string attrs = m.Groups[1].Value;
                Match id = Regex.Match(attrs, @"id='([^']*)'");
                if (!id.Success) continue;

                Match cont = Regex.Match(attrs, @"cont='([^']*)'");
                rows.Add((id.Groups[1].Value, cont.Success ? cont.Groups[1].Value : null));
            }

            return rows;
        }
    }
}
