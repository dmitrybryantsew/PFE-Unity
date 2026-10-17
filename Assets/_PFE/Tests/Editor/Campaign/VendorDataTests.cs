using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using PFE.Data.Definitions.Campaign;

namespace PFE.Tests.Editor.Campaign
{
    /// <summary>
    /// Acceptance tests for the <c>&lt;vendor&gt;</c> import — the data half of
    /// <c>fe.serv.Vendor</c> (<c>Vendor.as:24-123</c>).
    ///
    /// <para><b>Why this fixture runs offline while <see cref="CampaignDataTests"/> cannot.</b> Every
    /// other parser in <c>CampaignDataParser</c> ends by calling
    /// <c>ScriptableObject.CreateInstance&lt;T&gt;()</c>, which is an <c>ECall</c> and throws
    /// <c>SecurityException</c> outside the engine. <c>ParseVendors</c> deliberately returns a plain
    /// <see cref="VendorParseResult"/> instead, so the rules below are checkable in the offline wall —
    /// which is the only place the real <c>GameData.as</c> counts can be asserted without driving the
    /// GUI.</para>
    ///
    /// <para><b>Every real-data number is a count of the oracle, not of the parser.</b> They were
    /// counted independently over <c>GameData.as</c> (13 blocks, 477 <c>&lt;buy&gt;</c>, 38
    /// <c>&lt;task&gt;</c>, 32 variant rows / 29 distinct, 72 <c>noref</c>, 23 <c>nocheap</c>, 4
    /// <c>hardinv</c>, exactly one <c>pmult</c>), so a parser that drops an attribute or mis-files a
    /// child fails here rather than agreeing with itself.</para>
    /// </summary>
    [TestFixture]
    public class VendorDataTests
    {
        // ── synthetic fixtures: the rules, with no dependency on the shipped data ──────────────

        private const string VariantXml = @"
<game>
    <vendor id='v'>
        <buy id='shotgun' n='1'/>
        <buy id='shotgun^1' n='2' lvl='10'/>
    </vendor>
</game>";

        private const string PresenceXml = @"
<game>
    <vendor id='v'>
        <buy id='a' n='1' noref='0' nocheap='0' hardinv='0' pmult='0'/>
        <buy id='b' n='1'/>
    </vendor>
</game>";

        private const string SentinelsXml = @"
<game>
    <vendor id='nope'/>
    <vendor id='random'/>
    <vendor id='v'><buy id='x' n='1'/></vendor>
</game>";

        private const string UnknownAttributeXml = @"
<game>
    <vendor id='v'>
        <buy id='x' n='1' barter='2' teleport='1'/>
        <task id='t' skill='mech' skilln='3' land='rbl' trigger='t1' man='1' prio='9'/>
    </vendor>
</game>";

        [Test]
        public void VariantSplit_KeepsTwoVariantsOfOneWeaponApart()
        {
            List<VendorParseResult> vendors = CampaignDataParser.ParseVendors(VariantXml);
            Assert.AreEqual(1, vendors.Count);
            Assert.AreEqual(2, vendors[0].stock.Count);

            VendorStockEntry plain = vendors[0].stock[0];
            VendorStockEntry variant = vendors[0].stock[1];

            // AS3 Item.as:132-140 strips the suffix; the base id is what a row lookup uses.
            Assert.AreEqual("shotgun", plain.itemId);
            Assert.AreEqual(0, plain.variant);
            Assert.AreEqual("shotgun", variant.itemId, "The base id must not keep its '^1'.");
            Assert.AreEqual(1, variant.variant);

            // Vendor.as:109-117 — and this is the part that matters: the two rows must stay two rows.
            Assert.AreEqual("shotgun", plain.StockKey);
            Assert.AreEqual("shotgun^1", variant.StockKey);
            Assert.AreNotEqual(plain.StockKey, variant.StockKey,
                "Keying buys2 by the bare id silently merges two different weapons into one shelf row.");

            Assert.AreEqual(1, plain.stockCount);
            Assert.AreEqual(2, variant.stockCount);
            Assert.AreEqual(10, variant.level);
        }

        [Test]
        public void PresenceAttributes_ArePresenceTestsNotValueTests()
        {
            List<VendorParseResult> vendors = CampaignDataParser.ParseVendors(PresenceXml);
            VendorStockEntry withZeroes = vendors[0].stock[0];
            VendorStockEntry plain = vendors[0].stock[1];

            // Vendor.as:69-84 tests `@x.length()`, so an attribute spelled with the value "0" is ON.
            Assert.IsTrue(withZeroes.noRefill, "noref='0' still means 'never restocked' in AS3.");
            Assert.IsTrue(withZeroes.noCheap, "nocheap='0' still forces the row multiplier to 1.");
            Assert.IsTrue(withZeroes.hardInventory, "hardinv='0' still gates the row on World.w.hardInv.");
            Assert.IsTrue(withZeroes.hasPriceMultiplier, "pmult='0' is a present attribute — a real zero price.");
            Assert.AreEqual(0f, withZeroes.priceMultiplier, 1e-6f);

            Assert.IsFalse(plain.noRefill);
            Assert.IsFalse(plain.noCheap);
            Assert.IsFalse(plain.hardInventory);
            Assert.IsFalse(plain.hasPriceMultiplier,
                "An absent pmult must not read as a present one — the runtime then uses 1, not 0.");
        }

        [Test]
        public void Sentinels_AreImportedAsEmptyBlocksNotSkipped()
        {
            List<VendorParseResult> vendors = CampaignDataParser.ParseVendors(SentinelsXml);

            Assert.AreEqual(3, vendors.Count,
                "`nope` and `random` are oracle sentinels, not failed blocks. Dropping them makes an NPC " +
                "that names them look like it names a vendor that does not exist.");

            VendorParseResult nope = vendors.Find(v => v.vendorId == "nope");
            VendorParseResult random = vendors.Find(v => v.vendorId == "random");
            Assert.IsNotNull(nope);
            Assert.IsNotNull(random);
            Assert.AreEqual(0, nope.stock.Count);
            Assert.AreEqual(0, random.stock.Count);
            Assert.AreEqual(1, vendors.Find(v => v.vendorId == "v").stock.Count);
        }

        [Test]
        public void UnrecognisedAttributes_AreReportedInsteadOfDropped()
        {
            List<VendorParseResult> vendors = CampaignDataParser.ParseVendors(UnknownAttributeXml);

            // Rule #12: an attribute with no matching field is silent data loss. It must surface.
            CollectionAssert.AreEquivalent(new[] { "teleport", "prio" }, vendors[0].unrecognisedAttributes,
                "A new oracle attribute has to be visible as a log line, not swallowed.");
        }

        [Test]
        public void TaskRows_ReadAllSixAttributes()
        {
            List<VendorParseResult> vendors = CampaignDataParser.ParseVendors(UnknownAttributeXml);
            Assert.AreEqual(1, vendors[0].tasks.Count);

            VendorTaskEntry task = vendors[0].tasks[0];
            Assert.AreEqual("t", task.taskId);
            Assert.AreEqual("mech", task.skill);
            Assert.AreEqual(3, task.skillLevel);
            Assert.AreEqual("rbl", task.land);
            Assert.AreEqual("t1", task.trigger);
            Assert.AreEqual("1", task.manual,
                "`man` is read by the quest journal (PipPageInfo.as:771), so it is carried rather than dropped.");
        }

        // ── the shipped data ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Reads the oracle's <c>GameData.as</c> if any known copy is on this machine, else null. The
        /// same probe list as <c>CampaignDataTests.TryReadRealGameData</c>, deliberately duplicated
        /// rather than made public: a shared helper that silently resolved to a *different* file for one
        /// fixture than the other would make the two disagree without either going red.
        /// </summary>
        private static string TryReadRealGameData()
        {
            string[] probePaths =
            {
                @"E:\Games\UnityGames\pfeToUnity\pfe\scripts\fe\GameData.as",
                @"C:\Users\User\Documents\rustProjects\pfeToUnity\pfe\scripts\fe\GameData.as",
                @"E:\Games\UnityGames\backup from ralph\New folder\pfeToUnity\pfe\scripts\fe\GameData.as"
            };

            foreach (string p in probePaths)
            {
                if (File.Exists(p)) return File.ReadAllText(p);
            }

            return null;
        }

        [Test]
        public void RealGameData_HasThirteenVendorsWithTheShippedShelfSizes()
        {
            string content = TryReadRealGameData();
            if (string.IsNullOrEmpty(content))
            {
                Assert.Pass("Skipping: GameData.as was not found on local dev disk.");
                return;
            }

            List<VendorParseResult> vendors = CampaignDataParser.ParseVendors(content);

            Assert.AreEqual(13, vendors.Count, "GameData.d.vendor declares 13 blocks (GameData.as:2143-2700).");
            Assert.AreEqual(477, vendors.Sum(v => v.stock.Count), "477 <buy> rows across all vendors.");
            Assert.AreEqual(38, vendors.Sum(v => v.tasks.Count), "38 <task> rows across all vendors.");

            // Per-vendor counts, so a vendor that read a sibling's rows is caught rather than absorbed
            // by a plausible total.
            var expected = new Dictionary<string, (int buys, int tasks)>
            {
                { "nope", (0, 0) },
                { "random", (0, 0) },
                { "vendor_bar", (10, 1) },
                { "vendor_weapon", (83, 5) },
                { "vendor_expl", (43, 5) },
                { "vendor_medic", (27, 4) },
                { "vendor_armor", (27, 6) },
                { "vendor_mech", (67, 6) },
                { "vendor_mage", (64, 3) },
                { "vendor_cook", (51, 0) },
                { "vendor_zebr", (45, 2) },
                { "vendor_sr", (54, 6) },
                { "vendor_maps", (6, 0) },
            };

            foreach (KeyValuePair<string, (int buys, int tasks)> pair in expected)
            {
                VendorParseResult vendor = vendors.Find(v => v.vendorId == pair.Key);
                Assert.IsNotNull(vendor, $"Vendor '{pair.Key}' must be imported.");
                Assert.AreEqual(pair.Value.buys, vendor.stock.Count, $"'{pair.Key}' buy rows.");
                Assert.AreEqual(pair.Value.tasks, vendor.tasks.Count, $"'{pair.Key}' task rows.");
            }
        }

        [Test]
        public void RealGameData_NoAttributeOfTheShippedDataIsUnrecognised()
        {
            string content = TryReadRealGameData();
            if (string.IsNullOrEmpty(content))
            {
                Assert.Pass("Skipping: GameData.as was not found on local dev disk.");
                return;
            }

            List<VendorParseResult> vendors = CampaignDataParser.ParseVendors(content);

            var unknown = new List<string>();
            foreach (VendorParseResult vendor in vendors)
            {
                foreach (string name in vendor.unrecognisedAttributes)
                {
                    unknown.Add(vendor.vendorId + "." + name);
                }
            }

            // The strongest single check here: it says the field set covers *every* attribute the
            // shipped <buy>/<task> rows actually carry, which is the rule the four-slot task model in
            // the research got wrong (there are six).
            CollectionAssert.IsEmpty(unknown,
                "Unrecognised vendor attributes: " + string.Join(", ", unknown));
        }

        [Test]
        public void RealGameData_EveryBuyRowCarriesItsStockCount()
        {
            string content = TryReadRealGameData();
            if (string.IsNullOrEmpty(content))
            {
                Assert.Pass("Skipping: GameData.as was not found on local dev disk.");
                return;
            }

            List<VendorParseResult> vendors = CampaignDataParser.ParseVendors(content);

            var withoutN = new List<string>();
            foreach (VendorParseResult vendor in vendors)
            {
                foreach (VendorStockEntry row in vendor.stock)
                {
                    if (row.stockCount == 0) withoutN.Add(vendor.vendorId + "/" + row.itemId);
                }
            }

            CollectionAssert.IsEmpty(withoutN,
                "Every shipped <buy> carries `n` (0 rows lack it). A row reading 0 here means the " +
                "attribute was not read, and the page's `b.kol > 0` guard would hide it silently. " +
                "Offenders: " + string.Join(", ", withoutN));
        }

        [Test]
        public void RealGameData_KeepsEveryVariantRowAndItsBaseId()
        {
            string content = TryReadRealGameData();
            if (string.IsNullOrEmpty(content))
            {
                Assert.Pass("Skipping: GameData.as was not found on local dev disk.");
                return;
            }

            List<VendorParseResult> vendors = CampaignDataParser.ParseVendors(content);

            var variantRows = new List<VendorStockEntry>();
            foreach (VendorParseResult vendor in vendors)
            {
                variantRows.AddRange(vendor.stock.Where(r => r.variant > 0));
            }

            // 32 rows, but only 29 distinct ids: `plam^1`, `plamg^1` and `sword^1` are each stocked by
            // two different vendors. Counting distinct ids here reads 29 and looks right — which is why
            // the assertion is on rows. The per-vendor key is what keeps those three pairs apart; a
            // single global `id^variant` table would merge two shops' shelves.
            Assert.AreEqual(32, variantRows.Count,
                "32 <buy> rows carry a '^N' suffix. (29 distinct ids — three appear in two vendors each.)");
            CollectionAssert.IsEmpty(variantRows.Where(r => r.itemId.Contains("^")).ToList(),
                "No base id may still contain '^' — the split is at parse time.");

            var sharedVariantIds = new List<string>();
            foreach (string id in new[] { "plam^1", "plamg^1", "sword^1" })
            {
                var owners = new List<string>();
                foreach (VendorParseResult vendor in vendors)
                {
                    if (vendor.stock.Any(r => r.StockKey == id)) owners.Add(vendor.vendorId);
                }

                Assert.AreEqual(2, owners.Count, $"'{id}' is stocked by exactly two vendors.");
                sharedVariantIds.Add(id);
            }

            Assert.AreEqual(3, sharedVariantIds.Count);

            VendorParseResult weapons = vendors.Find(v => v.vendorId == "vendor_weapon");
            var weaponVariants = weapons.stock.Where(r => r.variant > 0).ToList();
            Assert.AreEqual(8, weaponVariants.Count,
                "vendor_weapon sells 8 weapon variants — the same weapons at a higher tier.");

            VendorStockEntry shotgun1 = weapons.stock.Find(r => r.StockKey == "shotgun^1");
            Assert.IsNotNull(shotgun1);
            Assert.AreEqual("shotgun", shotgun1.itemId);
            Assert.AreEqual(2, shotgun1.stockCount);
            Assert.AreEqual(10, shotgun1.level);

            // The plain shotgun is on the same shelf — this pair is the whole reason the key is
            // id^variant rather than id.
            Assert.IsNotNull(weapons.stock.Find(r => r.StockKey == "shotgun"));
        }

        [Test]
        public void RealGameData_TheOnlyPriceMultiplierRowIsThePowerArmor()
        {
            string content = TryReadRealGameData();
            if (string.IsNullOrEmpty(content))
            {
                Assert.Pass("Skipping: GameData.as was not found on local dev disk.");
                return;
            }

            List<VendorParseResult> vendors = CampaignDataParser.ParseVendors(content);

            var withPmult = new List<(string vendor, VendorStockEntry row)>();
            foreach (VendorParseResult vendor in vendors)
            {
                foreach (VendorStockEntry row in vendor.stock)
                {
                    if (row.hasPriceMultiplier) withPmult.Add((vendor.vendorId, row));
                }
            }

            Assert.AreEqual(1, withPmult.Count, "Exactly one <buy> row in GameData.as sets `pmult`.");
            Assert.AreEqual("vendor_armor", withPmult[0].vendor);
            Assert.AreEqual("power", withPmult[0].row.itemId);
            Assert.AreEqual(0.4f, withPmult[0].row.priceMultiplier, 1e-6f);

            // The same row is the only one carrying a `trigger` that is not a story gate but the
            // armour's own acquisition flag — worth pinning because it is a one-off in the data.
            Assert.AreEqual("power_armor", withPmult[0].row.trigger);
            Assert.AreEqual(20, withPmult[0].row.level);
        }

        [Test]
        public void RealGameData_NoCheapAndNoRefillRowsAreWhereTheDataSaysTheyAre()
        {
            string content = TryReadRealGameData();
            if (string.IsNullOrEmpty(content))
            {
                Assert.Pass("Skipping: GameData.as was not found on local dev disk.");
                return;
            }

            List<VendorParseResult> vendors = CampaignDataParser.ParseVendors(content);

            int nocheap = 0, noref = 0, hardinv = 0;
            var hardinvIds = new List<string>();
            foreach (VendorParseResult vendor in vendors)
            {
                foreach (VendorStockEntry row in vendor.stock)
                {
                    if (row.noCheap) nocheap++;
                    if (row.noRefill) noref++;
                    if (row.hardInventory) { hardinv++; hardinvIds.Add(vendor.vendorId + "/" + row.itemId); }
                }
            }

            Assert.AreEqual(23, nocheap, "23 rows are marked nocheap (a fixed price).");
            Assert.AreEqual(72, noref,
                "72 rows are never restocked. Note this is the FILE total; vendor_weapon alone has 2 " +
                "(amul_sniper, bag1) — reading a per-vendor number as the file total is how this " +
                "assertion first went wrong.");
            Assert.AreEqual(4, hardinv, "4 rows are hardinv-gated.");

            // The same consumable is hardinv-gated in two different shops — so the gate is a row
            // property, not a per-item one.
            CollectionAssert.AreEquivalent(
                new[]
                {
                    "vendor_weapon/bag1", "vendor_mage/potion_consc",
                    "vendor_zebr/potion_consc", "vendor_sr/bag2"
                },
                hardinvIds);
        }

        [Test]
        public void RealGameData_ReadsTheManFlagOnTheZebrAndSrTasks()
        {
            string content = TryReadRealGameData();
            if (string.IsNullOrEmpty(content))
            {
                Assert.Pass("Skipping: GameData.as was not found on local dev disk.");
                return;
            }

            List<VendorParseResult> vendors = CampaignDataParser.ParseVendors(content);

            int manRows = 0, triggerRows = 0, landRows = 0, skillRows = 0;
            foreach (VendorParseResult vendor in vendors)
            {
                foreach (VendorTaskEntry task in vendor.tasks)
                {
                    if (!string.IsNullOrEmpty(task.manual)) manRows++;
                    if (!string.IsNullOrEmpty(task.trigger)) triggerRows++;
                    if (!string.IsNullOrEmpty(task.land)) landRows++;
                    if (!string.IsNullOrEmpty(task.skill)) skillRows++;
                }
            }

            // A four-attribute task model (id/skill/skilln/land) reads 0 here and still "parses".
            Assert.AreEqual(8, manRows, "8 <task> rows carry `man` — read by PipPageInfo.as:771.");
            Assert.AreEqual(2, triggerRows, "2 <task> rows carry `trigger` — read by PipPage.checkQuest.");
            Assert.AreEqual(17, landRows);
            Assert.AreEqual(3, skillRows);
        }

        // ── the NPC field set ───────────────────────────────────────────────────────────────────
        //
        // These live here rather than in CampaignDataTests because ParseNpcsAndScripts builds a
        // ScriptableObject (an ECall) and therefore cannot run outside the engine. The coverage walk
        // below reads the same attributes without constructing anything, which is what makes the NPC
        // field set checkable offline.

        [Test]
        public void RealGameData_ModelsEveryNpcAttributeTheShippedDataUses()
        {
            string content = TryReadRealGameData();
            if (string.IsNullOrEmpty(content))
            {
                Assert.Pass("Skipping: GameData.as was not found on local dev disk.");
                return;
            }

            List<string> unknown = CampaignDataParser.ParseNpcUnrecognisedAttributes(content);

            // 54 <npc> blocks use exactly 17 distinct attributes. Seven of them — ndial, noturn,
            // replic, sloy, weap2, needskill, needitem — had no NpcDefinition field at all until this
            // pass, so their rows were read as if ungated (rule #12). This assertion is the thing that
            // keeps the next one from being dropped in silence.
            CollectionAssert.IsEmpty(unknown,
                "Unrecognised <npc> attributes: " + string.Join(", ", unknown) +
                " — add a field to NpcDefinition rather than letting the parser drop it.");
        }

        [Test]
        public void NpcAttributeWalk_ReportsAnUnknownAttributeAndIgnoresTheKnownSeventeen()
        {
            const string xml = @"
<game>
    <npc id='a' vis='VendorBar' name='x' inter='v' ico='9' ua1='p' ua2='q' weap='w' silent='1'
         ndial='dialOld' noturn='1' replic='r' sloy='2' weap2='w2' needskill='barter' needitem='key'/>
    <npc id='b' vis='Doctor' vendor='nope' gossip='1'/>
</game>";

            List<string> unknown = CampaignDataParser.ParseNpcUnrecognisedAttributes(xml);

            CollectionAssert.AreEquivalent(new[] { "b.gossip" }, unknown,
                "Only the attribute with no field may be reported — and it must name the NPC it came from.");
        }
    }
}
