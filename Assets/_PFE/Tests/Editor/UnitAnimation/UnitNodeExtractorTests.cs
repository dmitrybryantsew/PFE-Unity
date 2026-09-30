#if UNITY_EDITOR
using System.Collections.Generic;
using NUnit.Framework;
using PFE.Data.Definitions;

namespace PFE.Tests.Editor.UnitAnimation
{
    /// <summary>
    /// Tests for <see cref="UnitNodeExtractor"/>.
    ///
    /// <para>These exist because the extraction was wrong in a way that corrupted seven real assets and
    /// produced no error. <c>AllData.as</c> has seven self-closing marker units with no children
    /// (<c>pony</c>, <c>monster</c>, <c>other</c>, <c>robot</c>, <c>bigrobot</c>, <c>smallrobot</c>,
    /// <c>turret</c>), and a scan for "the next <c>&lt;/unit&gt;</c>" makes each one inherit the
    /// <i>following</i> unit's whole body — <c>pony.asset</c> was written with raider's physics, combat,
    /// vis and animations. Each of those ids appears exactly once, so nothing overwrote the damage.</para>
    /// </summary>
    [TestFixture]
    public class UnitNodeExtractorTests
    {
        /// <summary>
        /// The bug, pinned. A self-closing unit must yield its own tag and nothing else — the fixture
        /// makes the neighbour's body unmistakable so a regression cannot pass by accident.
        /// </summary>
        [Test]
        public void ExtractAllNodes_SelfClosingUnit_DoesNotSwallowTheNextUnit()
        {
            const string content = @"
<unit id='pony' cat='1'/>
<unit id='raider' fraction='2' cat='2'>
    <phis sX='55' sY='70'/>
    <blit id='stay'/>
    <blit id='walk' y='9' len='24' rep='1'/>
</unit>";

            var nodes = UnitNodeExtractor.ExtractAllNodes(content);

            Assert.AreEqual(2, nodes.Count);
            Assert.IsTrue(nodes.ContainsKey("pony"));
            Assert.IsTrue(nodes.ContainsKey("raider"));

            Assert.IsFalse(nodes["pony"].Contains("<blit"), "pony has no body, so it must carry none of raider's rows");
            Assert.IsFalse(nodes["pony"].Contains("<phis"), "pony must not inherit raider's physics either");
            StringAssert.DoesNotContain("raider", nodes["pony"], "pony's node must not contain the next unit's tag");

            // Positive control: the neighbour still parses fully, so a fix that emptied EVERY node
            // would fail here rather than passing.
            StringAssert.Contains("<blit id='stay'/>", nodes["raider"]);
            StringAssert.Contains("<phis sX='55' sY='70'/>", nodes["raider"]);
        }

        [Test]
        public void ExtractAllNodes_PairedUnit_SpansToItsOwnClosingTag()
        {
            const string content = @"
<unit id='a'>
    <blit id='stay'/>
</unit>
<unit id='b'>
    <blit id='walk' y='1' len='4'/>
</unit>";

            var nodes = UnitNodeExtractor.ExtractAllNodes(content);

            StringAssert.Contains("<blit id='stay'/>", nodes["a"]);
            StringAssert.DoesNotContain("<blit id='walk'", nodes["a"], "a must stop at its own </unit>");
            StringAssert.Contains("<blit id='walk' y='1' len='4'/>", nodes["b"]);
        }

        /// <summary>
        /// The real shape: a marker immediately before the unit it used to swallow, exactly as
        /// <c>turret</c> sits before <c>turret0</c> in the file.
        /// </summary>
        [Test]
        public void ExtractAllNodes_MarkerBeforeItsNamesake_KeepsThemSeparate()
        {
            const string content = @"
<unit id='turret' cat='2'/>
<unit id='turret0'>
    <vis vclass='visualTurret0'/>
    <blit id='stay' y='0' len='8'/>
</unit>";

            var nodes = UnitNodeExtractor.ExtractAllNodes(content);

            Assert.IsFalse(nodes["turret"].Contains("visualTurret0"),
                "turret is a bare marker — it must not be given turret0's visual");
            Assert.IsFalse(nodes["turret"].Contains("<blit"),
                "and it must not be given turret0's animations");
            StringAssert.Contains("visualTurret0", nodes["turret0"], "control: turret0 keeps its own visual");
        }

        [Test]
        public void IsSelfClosing_AgreesWithTheTagForm()
        {
            Assert.IsTrue(UnitNodeExtractor.IsSelfClosing("<unit id='pony' cat='1'/>"));
            Assert.IsTrue(UnitNodeExtractor.IsSelfClosing("<unit id='pony' cat='1' />"),
                "a space before the slash is still self-closing");
            Assert.IsFalse(UnitNodeExtractor.IsSelfClosing("<unit id='raider' fraction='2'>"));
            Assert.IsFalse(UnitNodeExtractor.IsSelfClosing(null));
        }

        [Test]
        public void ExtractAllNodes_EmptyOrNull_IsEmpty_NotAThrow()
        {
            Assert.AreEqual(0, UnitNodeExtractor.ExtractAllNodes(null).Count);
            Assert.AreEqual(0, UnitNodeExtractor.ExtractAllNodes("").Count);
            Assert.AreEqual(0, UnitNodeExtractor.ExtractAllNodes("<all></all>").Count,
                "a file with no units yields no nodes");
        }

        /// <summary>
        /// A repeated id is reported rather than silently resolved. AS3 reads the <b>first</b> node for
        /// an id (<c>AllData.d.unit.(@id == mid)[0]</c>) while a dictionary keeps the last, so a
        /// duplicate would make the port and the game disagree — worth a warning, not a coin flip.
        /// </summary>
        [Test]
        public void ExtractAllNodes_DuplicateId_IsReported()
        {
            const string content = @"
<unit id='dup'><blit id='stay'/></unit>
<unit id='dup'><blit id='walk' y='1' len='3'/></unit>";

            var duplicates = new List<string>();
            var nodes = UnitNodeExtractor.ExtractAllNodes(content, duplicates);

            CollectionAssert.Contains(duplicates, "dup");
            Assert.AreEqual(1, nodes.Count);
            StringAssert.Contains("walk", nodes["dup"], "the later node wins in the dictionary — hence the warning");
        }

        [Test]
        public void ExtractAllNodes_NoDuplicates_ReportsNone()
        {
            // The absent control for the check above: AllData.as has no duplicate ids today, so this
            // must stay silent. A reporter that fired unconditionally would make the warning useless.
            var duplicates = new List<string>();
            UnitNodeExtractor.ExtractAllNodes(
                "<unit id='a'><blit id='stay'/></unit><unit id='b'/>", duplicates);

            Assert.AreEqual(0, duplicates.Count);
        }

        [Test]
        public void UnitOpenTag_MatchesBothForms_AndCapturesTheId()
        {
            var paired = UnitNodeExtractor.UnitOpenTag.Match("<unit id='raider' fraction='2' cat='2'>");
            Assert.IsTrue(paired.Success);
            Assert.AreEqual("raider", paired.Groups[1].Value);

            var selfClosing = UnitNodeExtractor.UnitOpenTag.Match("<unit id='pony' cat='1'/>");
            Assert.IsTrue(selfClosing.Success, "the self-closing form must still be found — it is a real unit entry");
            Assert.AreEqual("pony", selfClosing.Groups[1].Value);
        }
    }
}
#endif
