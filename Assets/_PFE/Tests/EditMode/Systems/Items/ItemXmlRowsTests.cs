using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using PFE.Systems.Items;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.EditMode.Systems.Items
{
    /// <summary>
    /// Pins <see cref="ItemXmlRows"/> — the reader that lifts <c>&lt;item&gt;</c> rows out of
    /// <c>AllData.as</c>, and with them the two raw classification strings the loot pools are keyed on.
    ///
    /// <para><b>Why this is the guard for D2a.</b> <c>ItemDefinition</c> gained <c>legacyTip</c> and
    /// <c>tip2</c>, and <c>SimpleDataImporter</c> fills them from this reader. The importer itself
    /// cannot be exercised offline — it is <c>#if UNITY_EDITOR</c> and calls <c>AssetDatabase</c> — so
    /// the reader is the seam that <i>can</i> be, and the importer calls the same one. A test against a
    /// second, private regex would prove nothing about the shipped path.</para>
    ///
    /// <para><b>The acceptance criterion, verbatim:</b> "a test asserts <c>legacyTip == "compa"</c> for
    /// a known <c>compa</c> item and <c>"compw"</c> for a known <c>compw</c> item — i.e. the two are
    /// <b>distinguishable</b>, which they are not today."
    /// <see cref="CompaAndCompwAreDistinguishable"/> and the census assert exactly that, and the
    /// census asserts it against the real file rather than a fixture I chose.</para>
    /// </summary>
    public class ItemXmlRowsTests
    {
        // ── shapes, verbatim from AllData.as ──────────────────────────────────────────────────────

        /// <summary>A plain component row, <c>id</c> first — the shape of 456 of the 500 rows.</summary>
        private const string CompwRow =
            "<item id='gear' tip='compw' chance='0.5' stage='1' lvl='0' price='40' sell='8'/>";

        /// <summary>A component row of the other <c>comp*</c> family, same shape.</summary>
        private const string CompaRow =
            "<item id='spring' tip='compa' chance='0.5' stage='1' lvl='0' price='40' sell='8'/>";

        /// <summary>
        /// <b>The 44-row shape.</b> Every ammunition variant in the file leads with <c>base=</c>, not
        /// <c>id=</c> — <c>AllData.as</c> line 1900 area. A positional reader
        /// (<c>&lt;item\s+id='([^']+)'</c>) drops all 44 without a word.
        /// </summary>
        private const string BaseFirstRow =
            "<item base='p9' id='p9' tip='a' kol='12' chance='1.2' stage='1' lvl='0' price='1.5' sell='0.3' m='1'/>";

        /// <summary>A row carrying both classifications — 37 of these (<c>food</c>/<c>eda</c>).</summary>
        private const string FoodEdaRow =
            "<item id='milk' tip='food' sort='3' chance='0' tip2='eda' chance2='1' ftip='1' us='1' stage='1' price='40' sell='8'/>";

        /// <summary>A row carrying a tip2 of the other family — 21 of these (<c>scheme</c>/<c>co</c>).</summary>
        private const string SchemeCoRow =
            "<item id='s_soup' tip='scheme' tip2='co' chance2='1' work='stove' chance='0' price='500' skill='survival' lvl='3'>";

        // ── the reader ────────────────────────────────────────────────────────────────────────────

        [Test]
        public void ParsesIdTipAndTip2()
        {
            var rows = ItemXmlRows.Parse(CompwRow);

            Assert.AreEqual(1, rows.Count, "One <item> row in, one out.");
            Assert.AreEqual("gear", rows[0].Id);
            Assert.AreEqual("compw", rows[0].Tip);
            Assert.AreEqual("", rows[0].Tip2, "No tip2 attribute means empty, not null-by-luck.");
        }

        [Test]
        public void DoesNotAssumeIdIsTheFirstAttribute()
        {
            // Positive control on the fixture itself: if this row ever stopped leading with `base=`
            // the test would pass while exercising nothing. Assert the hazard is present.
            Assert.IsTrue(BaseFirstRow.StartsWith("<item base="),
                "This fixture must lead with base= or it does not exercise the hazard at all.");

            var rows = ItemXmlRows.Parse(BaseFirstRow);

            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual("p9", rows[0].Id,
                "id is the SECOND attribute here. A positional reader returns nothing for the 44 " +
                "ammunition-variant rows, all of which are shaped like this.");
            Assert.AreEqual("a", rows[0].Tip, "tip is the third attribute.");
            Assert.AreEqual("12", PFE.Systems.Weapons.WeaponXmlAttrs.Attr(rows[0].Attrs, "kol"),
                "The full attribute run must survive, so downstream readers still see every attribute.");
        }

        [Test]
        public void TipIsNotConfusedWithTip2()
        {
            var rows = ItemXmlRows.Parse(FoodEdaRow);

            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual("food", rows[0].Tip, "`tip` must not be read out of `tip2`.");
            Assert.AreEqual("eda", rows[0].Tip2);
        }

        [Test]
        public void Tip2OfTheOtherFamilyIsAlsoRead()
        {
            var rows = ItemXmlRows.Parse(SchemeCoRow);

            Assert.AreEqual("scheme", rows[0].Tip);
            Assert.AreEqual("co", rows[0].Tip2);
        }

        [Test]
        public void CompaAndCompwAreDistinguishable()
        {
            // The D2a acceptance criterion, at its smallest. Both rows become ItemType.Component under
            // FixDataImport.GetItemTypeFromSource (compa and compw are both in its Component arm), so
            // the enum cannot tell them apart — and these two must not be equal.
            var compa = ItemXmlRows.Parse(CompaRow).Single();
            var compw = ItemXmlRows.Parse(CompwRow).Single();

            Assert.AreEqual("compa", compa.Tip);
            Assert.AreEqual("compw", compw.Tip);
            Assert.AreNotEqual(compa.Tip, compw.Tip,
                "These two collapse to the same ItemType.Component. If they read equal here, the " +
                "distinction the loot tables need has been lost.");
        }

        [Test]
        public void ReadsARowWithABodyWithoutSwallowingItsChildren()
        {
            // s_frmeat and the other 20 `scheme` rows are written as open tags with children.
            var rows = ItemXmlRows.Parse(SchemeCoRow + "<work id='stove'/><out id='frmeat'/></item>");

            Assert.AreEqual(1, rows.Count,
                "Only the root <item> tag is a row; a nested <out>/<work> is not a second item.");
            Assert.AreEqual("s_soup", rows[0].Id);
        }

        [Test]
        public void EmptyOrNullSourceYieldsNoRows()
        {
            Assert.AreEqual(0, ItemXmlRows.Parse(null).Count);
            Assert.AreEqual(0, ItemXmlRows.Parse("").Count);
            Assert.AreEqual(0, ItemXmlRows.Parse("<weapon id='x' tip='1'/>").Count,
                "A weapon is not an item — the matcher must not be a loose `<i` prefix.");
        }

        [Test]
        public void ParsesEveryRowInFileOrder()
        {
            var rows = ItemXmlRows.Parse(CompwRow + CompaRow + BaseFirstRow);

            Assert.AreEqual(3, rows.Count);
            CollectionAssert.AreEqual(new[] { "gear", "spring", "p9" }, rows.Select(r => r.Id).ToArray());
        }

        // ── the real file, when it is there ───────────────────────────────────────────────────────

        /// <summary>
        /// The census. Runs against the real <c>AllData.as</c> — the point being that a reader tuned to
        /// fixtures I chose proves only that it agrees with me.
        ///
        /// <para>Skipped, not failed, when the file is absent: it is an untracked working copy, and its
        /// absence is an environment fact rather than a defect. The shape tests above still ran.</para>
        /// </summary>
        [Test]
        public void ParsingTheRealAllDataFindsEveryItemRow()
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

            Assert.AreEqual(500, rows.Count,
                "500 <item> rows in AllData.as. Fewer means the matcher is dropping rows — check the " +
                "44 that lead with base= first.");

            // Every row is identifiable and classified. These two are the positive control on the
            // whole census: if the attribute readers broke, these would be empty and every count
            // below would collapse to 0 rather than reading as a plausible number.
            Assert.AreEqual(0, rows.Count(r => string.IsNullOrEmpty(r.Id)), "every row has an @id");
            Assert.AreEqual(0, rows.Count(r => string.IsNullOrEmpty(r.Tip)), "every row has an @tip");

            // tip2 — the field D2a exists for, and the reason arr["eda"]/arr["co"] can be built.
            var withTip2 = rows.Where(r => !string.IsNullOrEmpty(r.Tip2)).ToList();
            Assert.AreEqual(58, withTip2.Count, "58 rows carry a @tip2.");
            Assert.AreEqual(37, withTip2.Count(r => r.Tip2 == "eda"), "37 tip2='eda'.");
            Assert.AreEqual(21, withTip2.Count(r => r.Tip2 == "co"), "21 tip2='co'.");
            Assert.AreEqual(0, withTip2.Count(r => r.Tip2 != "eda" && r.Tip2 != "co"),
                "eda and co are the only two tip2 values in the file; a third means the reader is " +
                "picking up something that is not a tip2.");

            // The 44 ammunition variants really are the base=-first ones, and they really are read.
            var baseFirst = rows.Where(r => r.Attrs.TrimStart().StartsWith("base=")).ToList();
            Assert.AreEqual(44, baseFirst.Count,
                "44 rows lead with base= — the ammunition variants. If this is 0 the file changed " +
                "shape and the positional hazard is gone; if the ids below are empty it is back.");
            Assert.IsTrue(baseFirst.All(r => !string.IsNullOrEmpty(r.Id)),
                "Every base=-first row must still yield its id.");

            // The acceptance criterion, against real data: both families present, and distinct.
            var compaIds = rows.Where(r => r.Tip == "compa").Select(r => r.Id).ToList();
            var compwIds = rows.Where(r => r.Tip == "compw").Select(r => r.Id).ToList();
            Assert.IsNotEmpty(compaIds, "AllData.as has compa rows.");
            Assert.IsNotEmpty(compwIds, "AllData.as has compw rows.");
            CollectionAssert.IsEmpty(compaIds.Intersect(compwIds),
                "No id may be both — which is the whole point: the enum maps both tips to Component.");
        }

        /// <summary>
        /// The assumption <see cref="ItemXmlRows"/> is built on, measured rather than assumed.
        ///
        /// <para>The reader uses <c>WeaponXmlAttrs</c>, which matches <c>name='value'</c> and not
        /// <c>name="value"</c>. The regex it replaced accepted both. That is safe here only because no
        /// item row uses double quotes — so this test is what keeps the strictness honest. If a future
        /// row is authored with double quotes this goes red instead of the row silently vanishing.</para>
        /// </summary>
        [Test]
        public void RealAllDataUsesNoDoubleQuotedItemAttributes()
        {
            string path = FindAllDataAs();
            if (path == null)
            {
                Assert.Ignore("AllData.as is not at the project root.");
                return;
            }

            string source = File.ReadAllText(path);
            var rows = ItemXmlRows.Parse(source);

            // The needle is built from a char code so the literal never contains an escaped quote.
            const string EqQuote = "=\u0022";   // ="

            int doubleQuoted = rows.Count(r => r.Attrs.Contains(EqQuote));

            // Positive control: the probe must be able to see a double-quoted attribute at all.
            Assert.IsTrue(("<item id=" + EqQuote + "x" + EqQuote + "/>").Contains(EqQuote),
                "Control: the needle this test looks for is reachable.");

            Assert.AreEqual(0, doubleQuoted,
                "No <item> root tag uses double quotes. If this fires, ItemXmlRows needs a " +
                "double-quote-capable attribute reader before it can be trusted on the new row.");
        }

        /// <summary>
        /// The repository root, via <see cref="SourceLint"/> — deliberately <b>not</b>
        /// <c>Application.dataPath</c>, which is an ECall and throws outside the editor, i.e. in
        /// exactly the harness these tests are meant to run in.
        /// </summary>
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

    /// <summary>
    /// The guard for the half of D2a that <b>cannot</b> be exercised offline: the importer that writes
    /// <c>legacyTip</c>/<c>tip2</c> onto the assets.
    ///
    /// <para><b>Why a source lint is the honest instrument here.</b> <c>SimpleDataImporter</c> is
    /// <c>#if UNITY_EDITOR</c> and calls <c>AssetDatabase</c>, so it is not in this assembly and no
    /// assertion against its behaviour can run in the harness. Claiming the fields get written without
    /// a guard would be claiming an unverified thing — and the specific failure this guards is the one
    /// the file itself already documents twice: an import that reports success and writes nothing.
    ///
    /// <para><b>The trap, precisely.</b> The ~500 item assets already exist, so <c>existing</c> is
    /// non-null for every one of them and the create branch never runs for them. The two new fields
    /// are therefore only reachable through the repair branch — and that branch is gated on
    /// <c>NeedsTipRepair</c>, which is a <c>switch</c> returning <c>false</c> for every tip it does
    /// not know. Gating on it alone would import 0 items, repair ~9 spells, and leave all 500
    /// <c>legacyTip</c> values empty. So the assertion that matters is that the condition also
    /// consults <c>NeedsLegacyTipRepair</c>.</para>
    /// </summary>
    public class ItemLegacyTipImportLintTests
    {
        private const string Importer = "Editor/Importers/SimpleDataImporter.cs";

        [Test]
        public void TheRepairBranchConsultsNeedsLegacyTipRepair()
        {
            string source = SourceLint.ReadStripped(Importer);
            string body = SourceLint.MethodBody(source, "private static void ImportItems()");

            Assert.That(body, Does.Contain("NeedsLegacyTipRepair"),
                "ImportItems must gate its repair branch on NeedsLegacyTipRepair as well as " +
                "NeedsTipRepair. Without it every pre-existing asset keeps an empty legacyTip and the " +
                "import still logs success.");

            // Control: the create branch is the other place the fields are written, and it must not be
            // the only one. If `ApplyLegacyTips` were absent from the repair branch the assertion above
            // could still pass on a call that does nothing, so pin that both fields are assigned.
            Assert.That(body, Does.Contain("ApplyLegacyTips"),
                "ImportItems must call ApplyLegacyTips, which is what assigns the two fields.");
        }

        [Test]
        public void ApplyLegacyTipsAssignsBothRawAttributes()
        {
            string source = SourceLint.ReadStripped(Importer);
            string body = SourceLint.MethodBody(source, "private static void ApplyLegacyTips(");

            Assert.That(body, Does.Contain("legacyTip"),
                "ApplyLegacyTips must write legacyTip.");
            Assert.That(body, Does.Contain("tip2"),
                "ApplyLegacyTips must write tip2 — it is a separate field and a separate loot pool, " +
                "so writing only the first would silently empty arr[\"eda\"] and arr[\"co\"].");

            // Both come off the row, not from a re-parse: the row is the thing the census tested.
            Assert.That(body, Does.Contain("row.Tip"),
                "legacyTip must come from the parsed row.");
            Assert.That(body, Does.Contain("row.Tip2"),
                "tip2 must come from the parsed row.");
        }

        [Test]
        public void TheImporterReadsRowsThroughTheSharedReader()
        {
            string source = SourceLint.ReadStripped(Importer);
            string body = SourceLint.MethodBody(source, "private static void ImportItems()");

            Assert.That(body, Does.Contain("ItemXmlRows.Parse"),
                "ImportItems must read rows through ItemXmlRows, so the reader the census exercises " +
                "is the reader that runs. A private regex here would make the census prove nothing " +
                "about the shipped path.");

            // `ExtractAttribute` was how this method used to pull the id out of the tag. It is code, so
            // it survives the stripper and this assertion can actually fail — unlike a check for the old
            // regex *string*, which `ReadStripped` deletes before any assertion sees it and which would
            // therefore be a guard that can never go red.
            Assert.That(body, Does.Not.Contain("ExtractAttribute"),
                "ImportItems must not go back to hand-extracting the id from the tag; that is the " +
                "shape that needed the id to be present for every row.");
        }
    }
}
