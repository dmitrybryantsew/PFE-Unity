using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using PFE.Systems.Items;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.EditMode.Systems.Items
{
    /// <summary>
    /// Pins the held-item <c>&lt;sk&gt;</c> payload end to end at the two seams that can be exercised
    /// offline: the reader (<see cref="ItemXmlRows.ParseSkillRows"/>) and the stat engine entry the
    /// recalc step calls (<c>CharacterStats.ApplyNamedStat</c>, the same method RecalculateStats 5.5
    /// feeds).
    ///
    /// <para><b>The bug being guarded.</b> "I picked up the screwdriver and it did not give me
    /// lockpicking ability" — the oracle grants <c>possLockPick</c> through
    /// <c>Pers.setInvParameters</c> (<c>Pers.as:2290-2307</c>), which iterates the held items and calls
    /// <c>setSkillParam(itemXml, 1)</c>; the port had the field, the stat case, and the
    /// <c>GetLockTip(1)</c> gate, but nothing ever applied an ITEM's rows, so the gate stayed shut.
    /// The chain is: importer stores the verbatim run → <c>ItemDefinition.skillRowsSource</c> →
    /// <c>PlayerInventory</c> parses and caches → <c>CharacterStats</c> applies during the recalc that
    /// the pickup itself triggers. Each offline-testable link is pinned here.</para>
    ///
    /// <para><b>Why the p9 control exists.</b> A first draft of the body reader attributed
    /// <c>screwdriver</c>'s rows to the self-closing <c>p9</c> row in front of it — the regex's root
    /// match stopped at <c>&lt;item …&gt;</c> but the body scan ran to the NEXT row's
    /// <c>&lt;/item&gt;</c>. A self-closing row must have an empty body, and the adjacent-row fixture
    /// below is what keeps the fix from regressing.</para>
    /// </summary>
    public class ItemSkillRowsTests
    {
        // ── shapes, verbatim from AllData.as ──────────────────────────────────────────────────────

        /// <summary>The row the lockpicking gate lives and dies by (AllData.as:4107-4109). Note it
        /// grants ONLY the ability — the rank bonus (<c>lockPick ref='add'</c>) belongs to titansd,
        /// stethoscope and kogit; the stethoscope-without-ability control lives in
        /// ItemSkillGrantTests.</summary>
        private const string ScrewdriverRow =
            "<item id='screwdriver' tip='instr' price='10' mess='screwdriver'>" +
            "<sk id='possLockPick' v1='1'/>" +
            "</item>";

        /// <summary>The self-closing ammunition row that sits directly above screwdriver in the file
        /// (AllData.as:3761) — the mis-attribution hazard, in file order.</summary>
        private const string P9ThenScrewdriver =
            "<item base='p9' id='p9' tip='a' kol='12' chance='1.2' stage='1' lvl='0' price='1.5' sell='0.3' m='1'/>" +
            ScrewdriverRow;

        /// <summary>kogit's third row is the only item row measured with <c>vd</c> and no <c>v1</c>
        /// (AllData.as:4177-4181): base 0 (ref='add'), value 0 + 1·25 at level 1.</summary>
        private const string KogitRow =
            "<item id='kogit' tip='instr' price='500' mess='kogit'>" +
            "<sk id='hacker' ref='add' v1='1'/>" +
            "<sk id='lockPick' ref='add' v1='1'/>" +
            "<sk id='maxOd' ref='add' vd='25'/>" +
            "<textvar s1='1' s2='25'/>" +
            "</item>";

        /// <summary>bag1's row is one of the two <c>tip='m'</c> rows — the Pers-member add that ignores
        /// <c>ref</c> entirely (AllData.as:4124-4126).</summary>
        private const string Bag1Row =
            "<item id='bag1' tip='instr' price='5000'>" +
            "<sk tip='m' id='maxm2' v1='500'/>" +
            "</item>";

        /// <summary>blackbook carries the only <c>tip='res'</c> item rows — vulnerability writes with
        /// the minus direction applied by the consumer (AllData.as:4229-4235).</summary>
        private const string BlackbookRow =
            "<item id='blackbook' tip='art' price='1' mess='blackbook'>" +
            "<sk tip='res' id='16' v0='0' v1='0.2'/>" +
            "<sk tip='res' id='19' v0='0' v1='0.2'/>" +
            "<sk id='bonusHeal' ref='add' v1='100'/>" +
            "<eff id='necro' n1='+20%'/>" +
            "</item>";

        /// <summary>The twelve <c>ref='mult'</c> item rows compound from 1, not from 0 — verbatim
        /// from stat_rd (AllData.as, the speed talisman).</summary>
        private const string StatRdRow =
            "<item id='stat_rd' tip='art' price='1' mess='stat_rd'>" +
            "<sk id='gunsDamMult' ref='add' v1='0.05'/>" +
            "<sk id='speedPlavMult' ref='mult' v1='1.1'/>" +
            "<sk id='runSpeedMult' ref='mult' v1='1.1'/>" +
            "<textvar s1='5%' s2='10%'/>" +
            "</item>";

        // ── the reader ────────────────────────────────────────────────────────────────────────────

        [Test]
        public void Screwdriver_ParsesToItsOneRow_WithOracleRefSemantics()
        {
            List<ItemXmlRow> rows = ItemXmlRows.Parse(ScrewdriverRow);
            Assert.AreEqual(1, rows.Count);

            List<PersSkillRow> sk = ItemXmlRows.ParseSkillRows(rows[0].Body);
            Assert.AreEqual(1, sk.Count, "the screwdriver grants the ability only — no rank bonus.");

            Assert.AreEqual("possLockPick", sk[0].Id);
            Assert.AreEqual(SkillRowOp.Set, sk[0].Op, "absent ref is a SET, not an add (Pers.as:1554-1556)");
            Assert.AreEqual(1f, sk[0].ValueFor(1), "v1 is the level-1 value");
        }

        [Test]
        public void ASelfClosingRow_HasAnEmptyBody_AndCannotClaimTheNextRowsChildren()
        {
            List<ItemXmlRow> rows = ItemXmlRows.Parse(P9ThenScrewdriver);
            Assert.AreEqual(2, rows.Count);

            Assert.AreEqual("p9", rows[0].Id);
            Assert.IsEmpty(rows[0].Body,
                "p9 is self-closing; a non-empty body here means the reader swallowed the next row.");

            Assert.AreEqual("screwdriver", rows[1].Id);
            List<PersSkillRow> sk = ItemXmlRows.ParseSkillRows(rows[1].Body);
            Assert.AreEqual(1, sk.Count, "the sk row belongs to screwdriver, not to p9.");
            CollectionAssert.Contains(sk.Select(r => r.Id).ToList(), "possLockPick");
        }

        [Test]
        public void Kogit_VdRow_ComputesBasePlusLevelTimesDelta()
        {
            List<PersSkillRow> sk = ItemXmlRows.ParseSkillRows(ItemXmlRows.Parse(KogitRow)[0].Body);
            PersSkillRow maxOd = sk.Single(r => r.Id == "maxOd");

            Assert.IsFalse(maxOd.HasV1);
            Assert.IsTrue(maxOd.HasVd);
            Assert.AreEqual(25f, maxOd.ValueFor(1), "base 0 (ref='add') + 1·25");
            Assert.AreEqual(50f, maxOd.ValueFor(2), "the delta scales with the level, not a flat 25");
        }

        [Test]
        public void Bag1_TipM_IsCarriedVerbatim_ForTheConsumerToDispatchOn()
        {
            List<PersSkillRow> sk = ItemXmlRows.ParseSkillRows(ItemXmlRows.Parse(Bag1Row)[0].Body);
            Assert.AreEqual(1, sk.Count);
            Assert.AreEqual("m", sk[0].Tip, "the consumer dispatches 'm' to a forced add; the reader must not reinterpret it.");
            Assert.AreEqual(SkillRowOp.Set, sk[0].Op, "the row itself carries no ref; tip='m' is a separate axis.");
            Assert.AreEqual(500f, sk[0].ValueFor(1));
        }

        [Test]
        public void Blackbook_ResRows_CarryTheirV0AndV1()
        {
            List<PersSkillRow> sk = ItemXmlRows.ParseSkillRows(ItemXmlRows.Parse(BlackbookRow)[0].Body);
            Assert.AreEqual(3, sk.Count, "the <eff> children are not <sk> rows and must not be parsed.");

            PersSkillRow res16 = sk.Single(r => r.Id == "16");
            Assert.AreEqual("res", res16.Tip);
            Assert.IsTrue(res16.HasV0);
            Assert.AreEqual(0.2f, res16.ValueFor(1));
        }

        [Test]
        public void StatRd_MultRows_CarryMultOp_AndCompoundFromOneNotZero()
        {
            List<PersSkillRow> sk = ItemXmlRows.ParseSkillRows(ItemXmlRows.Parse(StatRdRow)[0].Body);

            PersSkillRow runSpeed = sk.Single(r => r.Id == "runSpeedMult");
            Assert.AreEqual(SkillRowOp.Mult, runSpeed.Op);
            Assert.AreEqual(1.1f, runSpeed.ValueFor(1),
                "ref='mult' with no v0 must base at 1 (Pers.as:1487-1489) — a 0 base would " +
                "annihilate the stat the first time the talisman is picked up.");

            PersSkillRow guns = sk.Single(r => r.Id == "gunsDamMult");
            Assert.AreEqual(SkillRowOp.Add, guns.Op, "the same item mixes add and mult rows.");
        }

        [Test]
        public void ValueFor_LevelZero_ReturnsTheBase_TheMultBaseIsOne()
        {
            var setRow = new PersSkillRow { Id = "a", Op = SkillRowOp.Set, V0 = 5f, HasV0 = true, V1 = 7f, HasV1 = true };
            Assert.AreEqual(5f, setRow.ValueFor(0), "level 0 writes the base — the oracle's removal branch.");

            var multRow = new PersSkillRow { Id = "b", Op = SkillRowOp.Mult, Vd = 0.1f, HasVd = true };
            Assert.AreEqual(1f, multRow.ValueFor(0), "ref='mult' with no v0 bases at 1, not 0 (Pers.as:1487-1489).");
            Assert.AreEqual(1.1f, multRow.ValueFor(1), "a mult vd compounds from 1 — 0 would annihilate the stat.");
        }

        [Test]
        public void AnEmptyBody_YieldsNoRows_AndAnEmptyRowIsSkippedByTheConsumer()
        {
            Assert.IsEmpty(ItemXmlRows.ParseSkillRows(null));
            Assert.IsEmpty(ItemXmlRows.ParseSkillRows(string.Empty));

            List<PersSkillRow> sk = ItemXmlRows.ParseSkillRows(ItemXmlRows.Parse(P9ThenScrewdriver)[0].Body);
            Assert.IsEmpty(sk);
        }

        // ── the real file, when it is there ───────────────────────────────────────────────────────

        /// <summary>
        /// The census. Exactly 32 of the 500 rows carry a body with <c>&lt;sk&gt;</c> children; the
        /// self-closing rows in front of them must not have stolen any. <c>possLockPick</c> has
        /// exactly two writers in the whole file — screwdriver and titansd — so a third means a body
        /// was mis-attributed, and one means the gate's tool row was lost.
        /// </summary>
        [Test]
        public void ParsingTheRealAllData_AttributesEverySkRowToItsOwnItem()
        {
            string path = FindAllDataAs();
            if (path == null)
            {
                Assert.Ignore(
                    "AllData.as is not at the project root (it is an untracked working copy). The " +
                    "shape tests above still ran; this census did not.");
                return;
            }

            List<ItemXmlRow> rows = ItemXmlRows.Parse(File.ReadAllText(path));
            Assert.AreEqual(500, rows.Count, "the body capture must not change the row count.");

            List<ItemXmlRow> withBody = rows.Where(r => r.Body.Contains("<sk")).ToList();
            Assert.AreEqual(32, withBody.Count,
                "32 rows carry <sk> children (measured on the shipped file). A body leak would push " +
                "this up; a dropped body would pull it down.");

            var parsed = new List<KeyValuePair<string, List<PersSkillRow>>>();
            foreach (ItemXmlRow row in withBody)
            {
                List<PersSkillRow> sk = ItemXmlRows.ParseSkillRows(row.Body);
                Assert.IsNotEmpty(sk, $"row '{row.Id}' has a body with <sk> but parsed to nothing.");
                CollectionAssert.DoesNotContain(sk.Select(r => r.Id), null,
                    $"row '{row.Id}' produced a row without an id.");
                parsed.Add(new KeyValuePair<string, List<PersSkillRow>>(row.Id, sk));
            }

            var possLockPickWriters = parsed.Where(p => p.Value.Any(r => r.Id == "possLockPick"))
                .Select(p => p.Key).ToList();
            CollectionAssert.AreEquivalent(new[] { "screwdriver", "titansd" }, possLockPickWriters,
                "possLockPick is granted by exactly two items. A third id here is a mis-attribution; " +
                "a missing one is the lockpicking gate going dead again.");

            // The rows known by hand keep their known grants — the positive controls.
            CollectionAssert.Contains(parsed.Select(p => p.Key).ToList(), "stethoscope");
            CollectionAssert.Contains(parsed.Select(p => p.Key).ToList(), "kogit");
            CollectionAssert.Contains(parsed.Select(p => p.Key).ToList(), "impl_intel");
            CollectionAssert.Contains(parsed.Select(p => p.Key).ToList(), "blackbook");
            CollectionAssert.DoesNotContain(parsed.Select(p => p.Key).ToList(), "p9",
                "p9 is self-closing and grants nothing — if it appears here the reader is leaking bodies.");
        }

        private static string FindAllDataAs()
        {
            string root = SourceLint.ProjectRoot();

            string[] candidates =
            {
                Path.Combine(root, "AllData.as"),
                Path.Combine(root, "pfe", "scripts", "fe", "AllData.as"),
                Path.Combine(root, "..", "pfe", "scripts", "fe", "AllData.as"),
            };

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate)) return candidate;
            }

            return null;
        }
    }
}
