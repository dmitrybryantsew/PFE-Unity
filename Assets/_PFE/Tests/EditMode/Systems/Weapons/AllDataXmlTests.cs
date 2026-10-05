using System.Collections.Generic;
using System.Xml.Linq;
using NUnit.Framework;
using PFE.Data.Definitions;

namespace PFE.Tests.EditMode.Systems.Weapons
{
    /// <summary>
    /// Pins <see cref="AllDataXml"/> — how the weapon-sprite importer reads the weapon-id set out of the
    /// AS3 <c>AllData.as</c> XML literal.
    ///
    /// <para><b>Why this fixture exists.</b> The extraction used to live in the importer and searched for
    /// a root element <c>&lt;alldata&gt;</c> that does not occur in the file at all. It returned an empty
    /// id set on every run, and once the symbol filter began classifying from that set, the import
    /// created nothing while reporting all 213 weapons as "No visual def". It stayed silent for as long
    /// as it did because nothing offline could see the importer — which is why the extraction now lives
    /// in a runtime assembly, where this fixture can reach it.</para>
    ///
    /// <para><b>Every positive is paired with an absent control.</b> The regression case is
    /// <see cref="ExtractRootBlock_AlldataRootOnly_ReturnsNull"/>: it asserts the wrong tag yields
    /// nothing, so a future "fix" that accepts both spellings cannot pass by accident.</para>
    /// </summary>
    [TestFixture]
    public class AllDataXmlTests
    {
        // ── ExtractRootBlock ─────────────────────────────────────────────────

        [Test]
        public void ExtractRootBlock_FindsTheAllElement_WithSurroundingActionScript()
        {
            const string text = "package fe\n{\n  public static var d:XML = <all><weapon id='a'/></all>;\n}\n";

            string block = AllDataXml.ExtractRootBlock(text);

            Assert.AreEqual("<all><weapon id='a'/></all>", block);
        }

        [Test]
        public void ExtractRootBlock_AlldataRootOnly_ReturnsNull()
        {
            // The exact shape the old code looked for. It never matched, and the empty result was
            // reported as a warning that named no consequence.
            const string text = "public static var d:XML = <alldata><weapon id='a'/></alldata>;";

            Assert.IsNull(AllDataXml.ExtractRootBlock(text));
        }

        [Test]
        public void ExtractRootBlock_AllOpenIsNotSatisfiedByAlldataOpen()
        {
            // The crux: "<all>" must not match the prefix of "<alldata>".
            Assert.IsNull(AllDataXml.ExtractRootBlock("<alldata></alldata>"));
            Assert.IsNotNull(AllDataXml.ExtractRootBlock("<all></all>"));
        }

        [Test]
        public void ExtractRootBlock_ClosingTagBeforeOpening_ReturnsNull()
        {
            Assert.IsNull(AllDataXml.ExtractRootBlock("</all> junk <all>"));
        }

        [Test]
        public void ExtractRootBlock_MissingOrEmptyInput_ReturnsNull()
        {
            Assert.IsNull(AllDataXml.ExtractRootBlock(null));
            Assert.IsNull(AllDataXml.ExtractRootBlock(""));
            Assert.IsNull(AllDataXml.ExtractRootBlock("package fe { }"));
        }

        // ── TryRead ──────────────────────────────────────────────────────────

        [Test]
        public void TryRead_ReadsEveryIdAndSkipsWeaponsWithoutOne()
        {
            const string text = "<all>" +
                                "<weapon id='punch' tip='0'/>" +
                                "<weapon id='udar'><vis shine='3'/></weapon>" +
                                "<weapon tip='0'/>" +
                                "</all>";

            var outcome = AllDataXml.TryRead(text, out XElement root, out HashSet<string> ids, out string error);

            Assert.AreEqual(AllDataXml.Outcome.Ok, outcome, error);
            Assert.AreEqual(2, ids.Count);
            CollectionAssert.Contains(ids, "punch");
            CollectionAssert.Contains(ids, "udar");
            Assert.IsNull(error);

            // The parsed root is returned too, because the caller also needs the <vis> children.
            Assert.IsNotNull(root);
            Assert.AreEqual(3, CountElements(root, "weapon"));
        }

        [Test]
        public void TryRead_MissingBlock_ReportsMissingBlock_NotAnEmptySuccess()
        {
            // "No block" and "a block with no weapons" must not be the same answer — collapsing them is
            // what let the original bug report itself as 213 unrelated problems.
            var outcome = AllDataXml.TryRead("<alldata></alldata>", out XElement root, out HashSet<string> ids, out string error);

            Assert.AreEqual(AllDataXml.Outcome.MissingBlock, outcome);
            Assert.IsNull(root);
            Assert.AreEqual(0, ids.Count);
            Assert.IsNotNull(error);
            StringAssert.Contains("<all>", error);
        }

        [Test]
        public void TryRead_MalformedXml_ReportsParseFailed()
        {
            const string text = "<all><weapon id='a'></all>";

            var outcome = AllDataXml.TryRead(text, out XElement root, out HashSet<string> ids, out string error);

            Assert.AreEqual(AllDataXml.Outcome.ParseFailed, outcome);
            Assert.IsNull(root);
            Assert.AreEqual(0, ids.Count);
            Assert.IsNotNull(error);
        }

        [Test]
        public void TryRead_EmptyBlock_IsOkWithNoIds()
        {
            var outcome = AllDataXml.TryRead("<all></all>", out XElement root, out HashSet<string> ids, out string error);

            Assert.AreEqual(AllDataXml.Outcome.Ok, outcome);
            Assert.IsNotNull(root);
            Assert.AreEqual(0, ids.Count);
            Assert.IsNull(error);
        }

        [Test]
        public void TryRead_RealisticActionScriptWrapper_ParsesUnitsAndWeaponsApart()
        {
            // Transcribed from the shape of the real file: a package/class wrapper, XML comments
            // including Cyrillic, and <unit> siblings that must NOT be read as weapons.
            const string text =
                "package fe\n{\n   public class AllData\n   {\n      public static var d:XML = <all>\n" +
                "\t\t\t<!--       *******   \u042e\u043d\u0438\u0442\u044b   *******         -->\n" +
                "\t\t\t<unit id='training' fraction='1'>\n" +
                "\t\t\t\t<phis sX='60' sY='75' massa='120'/>\n" +
                "\t\t\t</unit>\n" +
                "\t\t\t<weapon id='punch' tip='0' cat='1'/>\n" +
                "\t\t\t<weapon id='udar'><vis vweap='viszsword' flare='fl1'/></weapon>\n" +
                "      </all>;\n   }\n}\n";

            var outcome = AllDataXml.TryRead(text, out XElement root, out HashSet<string> ids, out string error);

            Assert.AreEqual(AllDataXml.Outcome.Ok, outcome, error);
            Assert.AreEqual(2, ids.Count);
            CollectionAssert.Contains(ids, "punch");
            CollectionAssert.Contains(ids, "udar");
            CollectionAssert.DoesNotContain(ids, "training");

            // The <vis> children survive the parse, because the importer reads the overrides off them.
            XElement udar = null;
            foreach (XElement w in root.Descendants("weapon"))
                if ((string)w.Attribute("id") == "udar") udar = w;
            Assert.IsNotNull(udar);
            Assert.AreEqual("viszsword", (string)udar.Element("vis").Attribute("vweap"));
        }

        private static int CountElements(XElement root, string name)
        {
            int n = 0;
            foreach (XElement _ in root.Descendants(name)) n++;
            return n;
        }
    }
}
