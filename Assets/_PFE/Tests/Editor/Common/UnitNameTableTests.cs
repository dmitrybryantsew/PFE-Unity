using NUnit.Framework;
using PFE.Data.Definitions;

namespace PFE.Tests.Editor.Common
{
    /// <summary>
    /// Pins <see cref="UnitNameTable"/> — the port of AS3 <c>Res.namesArr(id)</c> (<c>Res.as:313-335</c>)
    /// and the draw-without-replacement in <c>Unit.getName()</c> (<c>Unit.as:1477-1497</c>).
    ///
    /// <para><b>Why the parse is tested against a literal and not the shipped document.</b> The document
    /// is 13 000 lines and lives in <c>Resources</c>, so a fixture that loaded it would be an integration
    /// test that fails for the wrong reasons (a missing asset reads exactly like a broken parser). The
    /// literal below reproduces the shipped block's exact shape — <c>&lt;names&gt;</c>, then
    /// <c>&lt;name id='…'&gt;</c> with <c>&lt;r&gt;</c> children, single-quoted ids — and the harness
    /// additionally parses the real file once, outside this fixture.</para>
    ///
    /// <para><b>What is being guarded.</b> Three failure modes, all silent in production: a parser that
    /// finds no list (every starred enemy keeps its species name), a draw that samples with replacement
    /// (two raiders named "Fixer" in one room), and a <see cref="UnitNameTable.HasList"/> that reports
    /// "no list" once the pool is exhausted (which would stop promoting that species altogether).</para>
    /// </summary>
    [TestFixture]
    public class UnitNameTableTests
    {
        /// <summary>The shipped block's shape, cut down to three lists.</summary>
        private const string Document =
            "<text>\n" +
            "  <names>\n" +
            "    <name id='raider'>\n" +
            "      <r>Pooptooth</r>\n" +
            "      <r>Fixer</r>\n" +
            "      <r>Garl Death-Hoof</r>\n" +
            "    </name>\n" +
            "    <name id='raider_f'>\n" +
            "      <r>She-Wolf</r>\n" +
            "    </name>\n" +
            "    <name id=\"zombie\">\n" +
            "      <r>Rott &amp; Bone</r>\n" +
            "    </name>\n" +
            "  </names>\n" +
            "</text>\n";

        [Test]
        public void Parse_ReadsOneListPerAuthoredId()
        {
            UnitNameTable table = UnitNameTable.Parse(Document);

            Assert.IsTrue(table.HasList("raider"));
            Assert.IsTrue(table.HasList("raider_f"));
            Assert.IsTrue(table.HasList("zombie"), "double-quoted ids are as likely as single");
            Assert.IsFalse(table.HasList("slaver"));
        }

        [Test]
        public void TryTake_DrawsTheIndexedName()
        {
            UnitNameTable table = UnitNameTable.Parse(Document);

            Assert.IsTrue(table.TryTake("raider", 0.0, out string first));
            Assert.AreEqual("Pooptooth", first);

            Assert.IsTrue(table.TryTake("raider", 0.0, out string next));
            Assert.AreEqual("Fixer", next, "the drawn name was removed, so 0.0 now indexes the second");
        }

        [Test]
        public void TryTake_NeverRepeatsAName()
        {
            // `_loc1_.splice(_loc2_,1)` — the pool shrinks. Three draws exhaust a three-name list.
            UnitNameTable table = UnitNameTable.Parse(Document);

            Assert.IsTrue(table.TryTake("raider", 0.5, out string a));
            Assert.IsTrue(table.TryTake("raider", 0.5, out string b));
            Assert.IsTrue(table.TryTake("raider", 0.5, out string c));

            Assert.AreNotEqual(a, b);
            Assert.AreNotEqual(b, c);
            Assert.AreNotEqual(a, c);
        }

        [Test]
        public void TryTake_OnAnExhaustedList_Fails()
        {
            // `getName()` returns "" and `setHero` keeps the species name — the nineteenth raider in a
            // land is simply "Raider".
            UnitNameTable table = UnitNameTable.Parse(Document);

            Assert.IsTrue(table.TryTake("raider_f", 0.0, out _));
            Assert.IsFalse(table.TryTake("raider_f", 0.0, out string name));
            Assert.IsNull(name);
        }

        [Test]
        public void HasList_StaysTrueAfterThePoolIsExhausted()
        {
            // The distinction this type exists to make: "can this species be a hero at all" is a DATA
            // question. Deriving it from the live list would make a species stop being promotable the
            // moment its last name was drawn.
            UnitNameTable table = UnitNameTable.Parse(Document);

            Assert.IsTrue(table.TryTake("raider_f", 0.0, out _));
            Assert.IsTrue(table.HasList("raider_f"));
            Assert.IsFalse(table.TryTake("raider_f", 0.0, out _));
        }

        [Test]
        public void TryTake_OnAnUnknownKey_Fails()
        {
            UnitNameTable table = UnitNameTable.Parse(Document);

            Assert.IsFalse(table.TryTake("alicorn", 0.0, out string name));
            Assert.IsNull(name);
            Assert.IsFalse(table.TryTake(null, 0.0, out _));
            Assert.IsFalse(table.TryTake(string.Empty, 0.0, out _));
        }

        [Test]
        public void TryTake_ACallerSuppliedOne_ClampsToTheLastName()
        {
            // `Math.random()` cannot return 1, but a caller's generator can, and an out-of-range index
            // would throw where the oracle silently produces undefined.
            UnitNameTable table = UnitNameTable.Parse(Document);

            Assert.IsTrue(table.TryTake("zombie", 1.0, out string name));
            Assert.AreEqual("Rott & Bone", name);
        }

        [Test]
        public void Parse_DecodesXmlEntities()
        {
            // `&amp;` must reach the label as `&`; an escaped one would show its escape.
            UnitNameTable table = UnitNameTable.Parse(Document);

            Assert.IsTrue(table.TryTake("zombie", 0.0, out string name));
            Assert.AreEqual("Rott & Bone", name);
        }

        [Test]
        public void Parse_OfADocumentWithNoNamesBlock_YieldsAnEmptyTable()
        {
            // The degraded state must be a usable object, not null and not a throw: every caller has a
            // documented fallback.
            UnitNameTable table = UnitNameTable.Parse("<text><i><t>nothing here</t></i></text>");

            Assert.IsFalse(table.HasList("raider"));
            Assert.IsFalse(table.TryTake("raider", 0.0, out _));
        }

        [Test]
        public void Parse_OfNull_IsEmptyRatherThanAThrow()
        {
            Assert.IsFalse(UnitNameTable.Parse(null).HasList("raider"));
            Assert.IsFalse(UnitNameTable.Parse(string.Empty).HasList("raider"));
            Assert.IsFalse(UnitNameTable.Empty.HasList("raider"));
        }
    }
}
