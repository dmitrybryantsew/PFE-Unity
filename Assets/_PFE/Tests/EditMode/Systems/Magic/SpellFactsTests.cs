using NUnit.Framework;
using PFE.Data.Definitions;
using PFE.Systems.Magic;

namespace PFE.Tests.EditMode.Systems.Magic
{
    /// <summary>
    /// The spell description formatter.
    ///
    /// <para><b>The values below are the oracle's own, not invented fixtures.</b> Each one is a verbatim
    /// copy of the matching row in <c>AllData.as:4008-4016</c>, so a failure here means the formatter
    /// stopped describing the data the game actually ships. The row is quoted on each test so the
    /// expected string can be re-derived by hand.</para>
    ///
    /// <para>Pure static and Unity-free, so these run offline — <see cref="SpellData"/> is a plain
    /// struct and <see cref="SpellFacts"/> names no engine type.</para>
    /// </summary>
    [TestFixture]
    public class SpellFactsTests
    {
        // ── Summarize: what the spell does and costs ────────────────────────────────────────────────

        /// <summary>
        /// <c>&lt;item id='sp_mshit' tip='spell' us='1' price='2000' atk='1' hp='150' magic='500'
        /// mana='100' culd='30' snd='mshit' mess='spell'/&gt;</c> (AllData.as:4013).
        /// </summary>
        [Test]
        public void Summarize_MshidShield_ReadsItsHpAndBothCosts()
        {
            var d = new SpellData { atk = true, hp = 150f, magic = 500f, mana = 100f, culd = 30f, snd = "mshit" };

            Assert.AreEqual("creates 150 hp, 500 magic, 100 mana, cooldown 30s", SpellFacts.Summarize(in d));
        }

        /// <summary>
        /// <c>&lt;item id='sp_blast' … atk='1' dam='20' magic='300' culd='10' mana='30' rad='500'
        /// tele='1' snd='blast' …/&gt;</c> (AllData.as:4010). Note <c>dam</c> precedes <c>rad</c>.
        /// </summary>
        [Test]
        public void Summarize_Blast_ListsDamageThenRadius()
        {
            var d = new SpellData { atk = true, dam = 20f, magic = 300f, culd = 10f, mana = 30f, rad = 500f, tele = true, snd = "blast" };

            Assert.AreEqual("damage 20, radius 500, 300 magic, 30 mana, cooldown 10s", SpellFacts.Summarize(in d));
        }

        /// <summary>
        /// <c>&lt;item id='sp_gwall' … hp='100' dam='10' dist='300' line='1' …/&gt;</c>
        /// (AllData.as:4015) — the only shipped spell with a non-zero <c>dist</c>.
        /// </summary>
        [Test]
        public void Summarize_Gwall_IncludesRangeAndLineOfSight()
        {
            var d = new SpellData { atk = true, dam = 10f, hp = 100f, dist = 300f, magic = 500f, mana = 50f, line = true, culd = 6f, snd = "mwall" };

            Assert.AreEqual("damage 10, creates 100 hp, range 300, 500 magic, 50 mana, line of sight, cooldown 6s",
                SpellFacts.Summarize(in d));
        }

        /// <summary>
        /// <c>sp_cryst</c> has <c>culd='0'</c> genuinely — a free spell and an unimported row are
        /// indistinguishable by a single field, which is the whole reason
        /// <see cref="SpellData.IsPopulated"/> exists. A zero cooldown must be omitted, not printed as
        /// "cooldown 0s".
        /// </summary>
        [Test]
        public void Summarize_Cryst_OmitsItsGenuineZeroCooldown()
        {
            var d = new SpellData { atk = true, prod = true, magic = 50f, culd = 0f, mana = 5f, snd = "crystal" };

            Assert.AreEqual("50 magic, 5 mana", SpellFacts.Summarize(in d));
        }

        /// <summary>An unimported row must say so rather than render as a blank line.</summary>
        [Test]
        public void Summarize_EmptyRow_SaysNoAttributes()
        {
            var d = new SpellData();

            Assert.AreEqual("(no attributes)", SpellFacts.Summarize(in d));
        }

        // ── Flags: the behavioural attributes, kept out of Summarize ────────────────────────────────

        /// <summary>
        /// <c>sp_cryst</c> is the only <c>prod='1'</c> row, and <c>atk='1'</c>. Both belong to Flags,
        /// not Summarize — a mutation that moved <c>prod</c> into the summary would fail both this test
        /// and <see cref="Summarize_Cryst_OmitsItsGenuineZeroCooldown"/>.
        /// </summary>
        [Test]
        public void Flags_Cryst_IsOffensiveAndRepeats()
        {
            var d = new SpellData { atk = true, prod = true, magic = 50f, snd = "crystal" };

            Assert.AreEqual("offensive, repeats while held", SpellFacts.Flags(in d));
        }

        /// <summary>
        /// <c>sp_slow</c> (AllData.as:4008) sets neither <c>atk</c> nor <c>prod</c> nor <c>tele</c> —
        /// one of only two spells that are not offensive (<c>sp_kdash</c> is the other).
        /// </summary>
        [Test]
        public void Flags_Slow_IsEmptyAndSaysSo()
        {
            var d = new SpellData { hp = 60f, magic = 500f, mana = 30f, culd = 10f, rad = 200f, snd = "slow" };

            Assert.AreEqual("-", SpellFacts.Flags(in d),
                "an empty flag set must render as a dash, not as a blank cell");
        }

        [Test]
        public void Flags_Blast_IsOffensiveAndTelekinetic()
        {
            var d = new SpellData { atk = true, tele = true, dam = 20f, snd = "blast" };

            Assert.AreEqual("offensive, telekinetic", SpellFacts.Flags(in d));
        }

        // ── Placeholder detection ───────────────────────────────────────────────────────────────────

        [Test]
        public void IsPlaceholderName_RejectsTheImportersUnfilledDefault()
        {
            // The exact string the importer leaves: ItemDefinition.cs:279's field initializer. Verified
            // present in the shipped Items/sp_mshit.asset.
            Assert.IsTrue(SpellFacts.IsPlaceholderName("Item Name"));
            Assert.IsTrue(SpellFacts.IsPlaceholderName(null));
            Assert.IsTrue(SpellFacts.IsPlaceholderName(""));
            Assert.IsTrue(SpellFacts.IsPlaceholderName("   "));

            // Control: a real name must not be mistaken for the placeholder.
            Assert.IsFalse(SpellFacts.IsPlaceholderName("Magic Shield"));
            Assert.IsFalse(SpellFacts.IsPlaceholderName("sp_mshit"));
        }

        // ── Describe: the composed multi-line form ──────────────────────────────────────────────────

        [Test]
        public void Describe_FallsBackToTheIdWhenTheNameIsAPlaceholder()
        {
            var d = new SpellData { atk = true, hp = 150f, magic = 500f, mana = 100f, culd = 30f, snd = "mshit" };

            string text = SpellFacts.Describe("sp_mshit", "Item Name", in d);

            StringAssert.StartsWith("sp_mshit — creates 150 hp, 500 magic, 100 mana, cooldown 30s", text);
            StringAssert.Contains("flags: offensive", text);
            StringAssert.Contains("cast sound: mshit", text);
        }

        [Test]
        public void Describe_UsesTheRealNameWhenTheImporterFilledOneIn()
        {
            var d = new SpellData { atk = true, magic = 50f, snd = "crystal" };

            string text = SpellFacts.Describe("sp_cryst", "Crystal Growth", in d);

            StringAssert.StartsWith("Crystal Growth —", text);
            StringAssert.DoesNotContain("sp_cryst —", text);
        }

        // ── SoundId ─────────────────────────────────────────────────────────────────────────────────

        [Test]
        public void SoundId_ReturnsNullRatherThanAnEmptyString()
        {
            Assert.AreEqual("mshit", SpellFacts.SoundId(new SpellData { snd = "mshit" }));
            Assert.IsNull(SpellFacts.SoundId(new SpellData { snd = "" }));
            Assert.IsNull(SpellFacts.SoundId(new SpellData()));
        }

        // ── IsPopulated, pinned here because the catalogue filter depends on it ─────────────────────

        [Test]
        public void IsPopulated_IsTrueForAShippedRowAndFalseForAnEmptyOne()
        {
            Assert.IsTrue(new SpellData { snd = "mshit", magic = 500f }.IsPopulated);
            Assert.IsTrue(new SpellData { magic = 500f }.IsPopulated,
                "magic alone is enough — snd is the other discriminator, not the only one");
            Assert.IsFalse(new SpellData().IsPopulated);
        }
    }
}
