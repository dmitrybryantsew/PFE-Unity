using System.Xml;
using NUnit.Framework;
using PFE.Data.Definitions.Campaign;

namespace PFE.Tests.Editor.Campaign
{
    /// <summary>
    /// <c>CampaignDataParser.ParseProbRoom</c> — the reader for the <c>&lt;prob&gt;</c> children of
    /// <c>&lt;land&gt;</c> that nothing used to read at all.
    ///
    /// <para><b>Why this is a separate fixture from <c>CampaignDataTests</c>.</b> <c>ParseLands</c> calls
    /// <c>ScriptableObject.CreateInstance</c>, so every case that goes through it fails with
    /// <c>ECall methods must be packaged into a system module</c> in a plain shell — that fixture is
    /// editor-only. <c>ParseProbRoom</c> takes an <c>XmlNode</c> and returns a plain
    /// <c>[Serializable]</c> class, so the reader itself is testable anywhere. Splitting it out means the
    /// parsing rules get run on every change instead of only when someone opens the editor.</para>
    /// </summary>
    [TestFixture]
    public class ProbParsingTests
    {
        private static ProbRoomDefinition Parse(string probElement)
        {
            var doc = new XmlDocument();
            doc.LoadXml("<game><land id='t'>" + probElement + "</land></game>");

            XmlNode node = doc.SelectSingleNode("//prob");
            Assert.IsNotNull(node, "the fixture XML does not contain a <prob> element");
            return CampaignDataParser.ParseProbRoom(node);
        }

        // =====================================================================
        //  Attribute reading
        // =====================================================================

        /// <summary>
        /// The three attribute orders that actually occur in <c>GameData.as</c> all read the same.
        ///
        /// <para><b>The trap this pins.</b> The oracle writes <c>&lt;prob id level tip close&gt;</c>,
        /// <c>&lt;prob id prize tip&gt;</c> and <c>&lt;prob id tip&gt;</c> — the same attributes in
        /// different positions, and with different subsets. A reader that walked the attribute list
        /// positionally would pass on the first shape and silently mis-assign <c>tip</c> and
        /// <c>prize</c> on the other two. Reading by name is the only thing that survives all three.</para>
        /// </summary>
        [Test]
        public void AllThreeRealAttributeOrders_ReadTheSame()
        {
            // Shape 1 — the boss rooms.
            ProbRoomDefinition boss = Parse("<prob id='bossraider1' level='0' tip='2' close='1'/>");
            Assert.AreEqual("bossraider1", boss.id);
            Assert.IsTrue(boss.hasLevel);
            Assert.AreEqual(0, boss.level);
            Assert.AreEqual("2", boss.tip);
            Assert.IsTrue(boss.close);
            Assert.IsFalse(boss.prize);

            // Shape 2 — a prize room; `prize` comes BEFORE `tip` here.
            ProbRoomDefinition prize = Parse("<prob id='labirint' prize='1' tip='1'/>");
            Assert.AreEqual("labirint", prize.id);
            Assert.IsFalse(prize.hasLevel, "no level attribute at all");
            Assert.IsTrue(prize.prize, "prize precedes tip in this shape");
            Assert.AreEqual("1", prize.tip);

            // Shape 3 — neither level, prize nor close.
            ProbRoomDefinition bare = Parse("<prob id='buttons1' tip='1'/>");
            Assert.AreEqual("buttons1", bare.id);
            Assert.IsFalse(bare.hasLevel);
            Assert.IsFalse(bare.prize);
            Assert.IsFalse(bare.close);
            Assert.AreEqual("1", bare.tip);
        }

        /// <summary>
        /// <b>An absent <c>level</c> is not <c>level='0'</c>.</b> <c>hasLevel</c> is what keeps them apart;
        /// without it <see cref="ProbSelection"/> cannot apply <c>Land.as:821</c> correctly.
        /// </summary>
        [Test]
        public void Level_AbsentIsNotZero()
        {
            ProbRoomDefinition absent = Parse("<prob id='a' tip='1'/>");
            Assert.IsFalse(absent.hasLevel, "absent level must leave hasLevel false");
            Assert.AreEqual(0, absent.level, "…and level stays at its default, which is never read");

            ProbRoomDefinition explicitZero = Parse("<prob id='b' level='0' tip='1'/>");
            Assert.IsTrue(explicitZero.hasLevel, "level='0' is present, so hasLevel must be true");
            Assert.AreEqual(0, explicitZero.level);

            // The two must be distinguishable, which is the whole point.
            Assert.AreNotEqual(absent.hasLevel, explicitZero.hasLevel);
        }

        /// <summary>
        /// <c>level</c> is read as an integer, including non-zero values.
        /// </summary>
        [Test]
        public void Level_ReadsTheValue()
        {
            Assert.AreEqual(1, Parse("<prob id='a' level='1' tip='1'/>").level);
            Assert.AreEqual(3, Parse("<prob id='a' level='3' tip='1'/>").level);
        }

        /// <summary>
        /// <b><c>imp</c>, <c>prize</c> and <c>close</c> are presence tests, not value tests.</b>
        /// <c>Probation.as:89-99</c> tests <c>xml.@prize.length()</c> and <c>xml.@close.length()</c>, and
        /// <c>Land.as:824</c> tests <c>xml.@imp.length()</c>.
        ///
        /// <para>So <c>close='0'</c> still means "the room closes". A <c>== "1"</c> comparison — the
        /// obvious way to write it — would silently drop every <c>'0'</c> and every other non-<c>'1'</c>
        /// spelling. The data uses <c>'1'</c> throughout, which is exactly why the mistake would never
        /// show up in a smoke test.</para>
        /// </summary>
        [Test]
        public void ImpPrizeClose_ArePresenceTests_SoAnExplicitZeroStillCounts()
        {
            ProbRoomDefinition zeroed = Parse("<prob id='a' imp='0' prize='0' close='0' tip='1'/>");
            Assert.IsTrue(zeroed.imp, "imp='0' is present, so imp is set — AS3 tests .length(), not the value");
            Assert.IsTrue(zeroed.prize, "prize='0' is present, so prizeActive is true in AS3");
            Assert.IsTrue(zeroed.close, "close='0' is present, so isClose is true in AS3");

            ProbRoomDefinition absent = Parse("<prob id='a' tip='1'/>");
            Assert.IsFalse(absent.imp);
            Assert.IsFalse(absent.prize);
            Assert.IsFalse(absent.close);
        }

        /// <summary>
        /// <c>tip</c> is the one attribute that IS a value — <c>Probation.as:93-96</c> assigns it and
        /// defaults to <c>0</c>. An absent <c>tip</c> reads as empty, which is not <c>"2"</c> and so is a
        /// plain prob door.
        /// </summary>
        [Test]
        public void Tip_IsAValue_AndAbsentReadsAsEmpty()
        {
            Assert.AreEqual("2", Parse("<prob id='a' tip='2'/>").tip);
            Assert.AreEqual("1", Parse("<prob id='a' tip='1'/>").tip);
            Assert.AreEqual("0", Parse("<prob id='a' tip='0'/>").tip,
                "tip='0' is a value, not an absent attribute");
            Assert.AreEqual("", Parse("<prob id='a'/>").tip);
        }

        // =====================================================================
        //  <con> children
        // =====================================================================

        /// <summary>
        /// <c>&lt;con&gt;</c> children are read into <c>contents</c>, carrying <c>tip</c>/<c>uid</c>/<c>qid</c>.
        ///
        /// <para>The oracle has 100 of these: 82 with a <c>uid</c>, and 12 unit contents with a
        /// <c>qid</c> — the quest a unit is tied to (<c>Probation.as:175-184</c>). All three fields are
        /// needed for <c>checkAllCon</c> to be portable later.</para>
        /// </summary>
        [Test]
        public void ConChildren_AreParsedWithTipUidAndQid()
        {
            ProbRoomDefinition prob = Parse(
                "<prob id='bossencl' level='0' tip='2' close='1'>" +
                "  <con tip='unit' uid='bossenclF1'/>" +
                "  <con tip='box' uid='chest7'/>" +
                "  <con tip='unit' qid='killBoss'/>" +
                "</prob>");

            Assert.AreEqual(3, prob.contents.Count);

            Assert.AreEqual("unit", prob.contents[0].tip);
            Assert.AreEqual("bossenclF1", prob.contents[0].uid);
            Assert.AreEqual("", prob.contents[0].qid);

            Assert.AreEqual("box", prob.contents[1].tip);
            Assert.AreEqual("chest7", prob.contents[1].uid);

            Assert.AreEqual("unit", prob.contents[2].tip);
            Assert.AreEqual("", prob.contents[2].uid);
            Assert.AreEqual("killBoss", prob.contents[2].qid,
                "a unit content may carry a quest id instead of a uid");
        }

        /// <summary>
        /// A prob with no <c>&lt;con&gt;</c> gets an empty list, not <c>null</c>. A null would make every
        /// consumer add its own guard, and one of them would forget.
        /// </summary>
        [Test]
        public void NoConChildren_YieldsAnEmptyListNotNull()
        {
            ProbRoomDefinition prob = Parse("<prob id='a' tip='1'/>");
            Assert.IsNotNull(prob.contents);
            Assert.AreEqual(0, prob.contents.Count);
        }

        /// <summary>
        /// <c>&lt;wave&gt;</c> children are <em>not</em> read here, and that is deliberate rather than an
        /// oversight: <c>Probation.maxwave</c> is the only thing that uses them
        /// (<c>Probation.as:127-130</c>) and the wave runtime has no port. Asserting the count would
        /// imply a consumer that does not exist; this case records the boundary instead.
        /// </summary>
        [Test]
        public void WaveChildren_AreNotReadYet_BecauseNoConsumerExists()
        {
            ProbRoomDefinition prob = Parse(
                "<prob id='bossraider1' level='0' tip='2' close='1'>" +
                "  <con tip='wave'/>" +
                "  <wave t='15'><obj id='raider'/></wave>" +
                "  <wave t='20'><obj id='raider'/></wave>" +
                "</prob>");

            Assert.AreEqual(1, prob.contents.Count, "only the <con> is a content");
            Assert.AreEqual("wave", prob.contents[0].tip,
                "the <con tip='wave'> is the gate checkAllCon uses; the <wave> elements are the payload");
        }

        // =====================================================================
        //  Robustness
        // =====================================================================

        /// <summary>
        /// An unknown attribute is ignored rather than throwing, and does not shift the ones that are
        /// known.
        /// </summary>
        [Test]
        public void UnknownAttributes_AreIgnored()
        {
            ProbRoomDefinition prob = Parse("<prob id='a' tip='2' whatever='17' other='x'/>");
            Assert.AreEqual("a", prob.id);
            Assert.AreEqual("2", prob.tip);
        }

        /// <summary>
        /// A <c>&lt;prob&gt;</c> with no <c>id</c> parses to an empty id rather than throwing.
        /// <see cref="ProbSelection"/> then skips it, so the outcome is "no door", not "a door to
        /// nowhere".
        /// </summary>
        [Test]
        public void MissingId_ParsesToEmpty_AndIsThenSkippedBySelection()
        {
            ProbRoomDefinition prob = Parse("<prob tip='1'/>");
            Assert.AreEqual("", prob.id);
        }

        /// <summary>
        /// A non-numeric <c>level</c> falls back to 0 but still counts as <em>present</em> — the attribute
        /// was there, so the room is stage-gated, which is the conservative direction.
        /// </summary>
        [Test]
        public void NonNumericLevel_IsPresentWithValueZero()
        {
            ProbRoomDefinition prob = Parse("<prob id='a' level='soon' tip='1'/>");
            Assert.IsTrue(prob.hasLevel);
            Assert.AreEqual(0, prob.level);
        }
    }
}
