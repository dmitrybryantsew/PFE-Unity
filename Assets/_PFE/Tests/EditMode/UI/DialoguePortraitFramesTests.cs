using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using NUnit.Framework;
using PFE.Data.Definitions;
using PFE.Tests.Editor.Common;
using PFE.UI.HUD;

namespace PFE.Tests.EditMode.UI
{
    /// <summary>
    /// Pins <see cref="DialoguePortraitFrames"/> — the frame table of AS3 <c>dial.portret</c>
    /// (SWF sprite <b>4638</b>), read out of the tag stream, and the <c>@p</c> label the Oracle's
    /// <c>GUI.dialText</c> resolves against it (<c>GUI.as:1831-1851</c>).
    ///
    /// <para><b>Why a table of 60 strings needs a fixture.</b> The labels are opaque four-letter tokens
    /// (<c>lp1</c>, <c>vel</c>, <c>zetr</c>, <c>sc4</c>) and their <i>frame numbers</i> are not derivable
    /// from them — frame 8 is unlabelled, <c>lpa</c> is frame 9, and the sequence is not alphabetical. A
    /// transcription slip therefore produces a portrait of the wrong character, which reads as an
    /// art bug rather than a data one. There is nothing on screen to notice it.</para>
    ///
    /// <para><b>The strongest assertion here is the last one</b> — that every <c>@p</c> in the shipped
    /// <c>text_en.xml</c> resolves. It is the only check that ties the table to the data the game
    /// actually plays, and it fails loudly if either side is edited alone.</para>
    ///
    /// <para><b>It is parsed as XML, and the first draft of this fixture is why.</b> The document writes
    /// attributes in both quote styles — 1047 rows as <c>p="lp1"</c>, 754 as <c>p='al1'</c> — so a regex
    /// anchored on the double quote finds 41 distinct speakers where there are 59, and 41 is an answer
    /// that looks entirely reasonable. The 18 labels that only ever appear single-quoted (the alicorns,
    /// the slavers, the raiders, <c>pip</c>, <c>mtn</c>) would each have been silently "not used by the
    /// game" and the coverage test would have measured two thirds of the document while claiming all of
    /// it.</para>
    /// </summary>
    [TestFixture]
    public class DialoguePortraitFramesTests
    {
        // ── The table ─────────────────────────────────────────────────────────

        [Test]
        public void Table_HoldsSeventyFramesOfWhichSixtyAreLabelled()
        {
            Assert.AreEqual(70, DialoguePortraitFrames.FrameCount,
                "DefineSprite_4638 has 70 ShowFrames.");
            Assert.AreEqual(60, DialoguePortraitFrames.LabelledFrameCount,
                "60 FrameLabel tags: frame 2 lp1 .. frame 62 men2.");

            // The clip's frame count and the catalog the importer builds must be the same number, or the
            // importer writes 70 files into an array the definition says holds something else.
            Assert.AreEqual(PipBuckCompositionDefinition.DialoguePortraitCount,
                            DialoguePortraitFrames.FrameCount,
                "DialoguePortraitCount and DialoguePortraitFrames.FrameCount are two statements of the " +
                "same fact and must not be able to disagree.");
        }

        [Test]
        public void Table_IndexIsFrameMinusOne()
        {
            // Frame 1 is the Oracle's blank default and carries no label — that is what makes
            // `gotoAndStop(1)` a reset rather than a leftover portrait.
            Assert.IsNull(DialoguePortraitFrames.LabelAt(0), "frame 1 is unlabelled (the blank default)");
            Assert.AreEqual("lp1", DialoguePortraitFrames.LabelAt(1), "frame 2");
            Assert.IsNull(DialoguePortraitFrames.LabelAt(7), "frame 8 is unlabelled");
            Assert.AreEqual("lpa", DialoguePortraitFrames.LabelAt(8), "frame 9");
            Assert.AreEqual("calam", DialoguePortraitFrames.LabelAt(21), "frame 22");
            Assert.AreEqual("men2", DialoguePortraitFrames.LabelAt(61), "frame 62");
            Assert.IsNull(DialoguePortraitFrames.LabelAt(69), "frame 70 is unlabelled");
        }

        [Test]
        public void Table_OutOfRangeIndicesAreNullNotAnException()
        {
            Assert.IsNull(DialoguePortraitFrames.LabelAt(-1));
            Assert.IsNull(DialoguePortraitFrames.LabelAt(DialoguePortraitFrames.FrameCount));
            Assert.IsNull(DialoguePortraitFrames.LabelAt(int.MaxValue));
        }

        [Test]
        public void Table_LabelsAreUnique()
        {
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < DialoguePortraitFrames.FrameCount; i++)
            {
                string label = DialoguePortraitFrames.LabelAt(i);
                if (label == null) continue;

                if (seen.TryGetValue(label, out int firstFrame))
                {
                    Assert.Fail($"label '{label}' appears at both frame {firstFrame + 1} and frame {i + 1} " +
                                "— the lookup would silently pick one");
                }

                seen[label] = i;
            }
        }

        // ── Label resolution ──────────────────────────────────────────────────

        [Test]
        public void TryGetIndex_ResolvesTheLabelsTheGameUses()
        {
            AssertIndex("lp1", 1);
            AssertIndex("lp6", 6);
            AssertIndex("lpa", 8);
            AssertIndex("vel", 20);
            AssertIndex("calam", 21);
            AssertIndex("pip", 24);
            AssertIndex("term", 26);
            AssertIndex("zira", 47);
            AssertIndex("wat", 48);
            AssertIndex("men", 56);
            AssertIndex("men2", 61);
        }

        [Test]
        public void TryGetIndex_ReportsUnknownLabelsWithoutThrowing()
        {
            // The Oracle's `gotoAndStop(sp)` throws on an unknown label and its `catch` lands on frame 1,
            // so "unknown" is a supported outcome that means "draw nothing" — not an error.
            AssertUnknown(null);
            AssertUnknown(string.Empty);
            AssertUnknown("nope");
            AssertUnknown("lp7");          // the sequence stops at lp6
            AssertUnknown("LP1");          // Flash frame labels are case-sensitive
            AssertUnknown("calamity");     // the UNIT is `calam`; the label is not the unit id
        }

        [Test]
        public void TryGetIndex_AcceptsAFrameNumberBecauseFlashDoes()
        {
            // `@p` goes straight into gotoAndStop (GUI.as:1845), which takes a frame number as readily as
            // a label. Nothing in text_en.xml uses one today; the branch exists because the Oracle has it.
            AssertIndex("1", 0);
            AssertIndex("22", 21);
            AssertIndex("70", 69);

            // Out of range behaves like an unknown label — Flash throws and the Oracle's catch lands on 1.
            AssertUnknown("0");
            AssertUnknown("71");
            AssertUnknown("-1");
            AssertUnknown("1.0");
            AssertUnknown(" 1");
        }

        [Test]
        public void Resolve_SubstitutesTheAlicornFrameForThePlayersOwnPortraits()
        {
            // GUI.as:1838-1842 — `sp.substr(0,2) == "lp" && World.w.alicorn` -> "lpa".
            Assert.AreEqual("lpa", DialoguePortraitFrames.Resolve("lp1", alicorn: true));
            Assert.AreEqual("lpa", DialoguePortraitFrames.Resolve("lp6", alicorn: true));

            // Only the player's own `lp*` frames — every other speaker keeps their portrait.
            Assert.AreEqual("calam", DialoguePortraitFrames.Resolve("calam", alicorn: true));
            Assert.AreEqual("vel", DialoguePortraitFrames.Resolve("vel", alicorn: true));
            Assert.AreEqual("lpa", DialoguePortraitFrames.Resolve("lpa", alicorn: true), "already lpa");

            // Off, the label is returned untouched.
            Assert.AreEqual("lp1", DialoguePortraitFrames.Resolve("lp1", alicorn: false));
            Assert.IsNull(DialoguePortraitFrames.Resolve(null, alicorn: true));
            Assert.IsNull(DialoguePortraitFrames.Resolve(string.Empty, alicorn: true));
        }

        [Test]
        public void TryGetIndexFor_AppliesTheAlicornRuleBeforeTheLookup()
        {
            Assert.That(DialoguePortraitFrames.TryGetIndexFor("lp1", alicorn: true, out int index), Is.True);
            Assert.AreEqual(8, index, "lpa is frame 9");

            Assert.That(DialoguePortraitFrames.TryGetIndexFor("lp1", alicorn: false, out index), Is.True);
            Assert.AreEqual(1, index, "lp1 is frame 2");
        }

        // ── The shipped data ──────────────────────────────────────────────────

        /// <summary>
        /// Lazily loaded, not a <c>[OneTimeSetUp]</c>: the offline wall runner
        /// (<c>.workbuddy-ai/tools/agentverify/wall/Runner.cs</c>) dispatches <c>SetUp</c>/<c>TearDown</c>
        /// and has no <c>OneTimeSetUp</c> branch, so a fixture that arranged there would be green in the
        /// editor and null-deref in the harness. Same reason <c>PipTextTableTests</c> uses a property.
        /// </summary>
        private static List<string> _shippedSpeakerLabels;
        private static int _shippedSpeakerRows;

        private static List<string> ShippedSpeakerLabels
        {
            get
            {
                if (_shippedSpeakerLabels != null) return _shippedSpeakerLabels;

                string path = Path.Combine(
                    SourceLint.ProjectRoot(), "Assets", "_PFE", "Data", "Resources", "Localisation",
                    "text_en.xml");

                Assert.That(File.Exists(path), Is.True,
                    "The localisation document is missing: " + path);

                // Parsed as XML rather than scanned for `p="..."`, and that is not fastidiousness: this
                // document writes its attributes in BOTH quote styles — 1047 rows as `p="lp1"` and 754 as
                // `p='al1'`. A regex anchored on the double quote reports 41 distinct speakers where the
                // document has 59, and 41 looks like a perfectly ordinary answer. This test was written
                // against the regex figure first and the parse is what caught it.
                //
                // The scope is the Oracle's own walk: `GUI.dialText` reaches a row through
                // `txt/id/n/r` (GUI.as:1761-1777) and reads `@p` off that `r`. A name-search would also
                // pick up any unrelated `p` attribute the document grows later.
                XDocument doc = XDocument.Load(path);
                Assert.That(doc.Root, Is.Not.Null, "text_en.xml parsed to a null root");

                var rows = doc.Root
                    .Elements("txt")
                    .Elements("n")
                    .Elements("r")
                    .Select(r => (string)r.Attribute("p"))
                    .Where(p => !string.IsNullOrEmpty(p))
                    .ToList();

                _shippedSpeakerRows = rows.Count;
                _shippedSpeakerLabels = rows
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(p => p, StringComparer.Ordinal)
                    .ToList();

                Assert.That(_shippedSpeakerLabels.Count, Is.GreaterThan(0),
                    "No `p=` attributes were found on any `txt/id/n/r` row — the parse or the document " +
                    "shape is wrong, and a zero here would make the coverage test below vacuous.");

                return _shippedSpeakerLabels;
            }
        }

        [Test]
        public void Shipped_EverySpeakerLabelInTheGameResolvesToAPortraitFrame()
        {
            // The whole point of the table: the game plays these labels, so each one must land on a frame.
            // A miss is not fatal at runtime (the Oracle's catch draws the blank frame) which is exactly
            // why it has to be fatal here.
            var unresolved = new List<string>();
            foreach (string label in ShippedSpeakerLabels)
            {
                if (!DialoguePortraitFrames.TryGetIndex(label, out _)) unresolved.Add(label);
            }

            Assert.That(unresolved, Is.Empty,
                "These @p labels name a portrait frame the clip does not have, so they would draw " +
                "nothing: " + string.Join(", ", unresolved));
        }

        [Test]
        public void Shipped_UsesFiftyNineDistinctSpeakersAndTheCensusHasNotDrifted()
        {
            // A truncated or partially-parsed document would still pass the coverage test above (fewer
            // labels, all resolvable), so the census is pinned separately.
            Assert.AreEqual(59, ShippedSpeakerLabels.Count,
                "text_en.xml names 59 distinct speakers across its dialogue rows. A different number " +
                "means the document changed or the parse stopped early — either way the coverage test " +
                "above is no longer measuring what it claims.");

            Assert.AreEqual(1801, _shippedSpeakerRows,
                "1801 `txt/id/n/r` rows carry a `p`. This is the truncation guard: distinct-count alone " +
                "survives losing every row that repeats a speaker.");

            // Positive and negative controls: a label that must be present, and one that must not.
            Assert.That(ShippedSpeakerLabels, Does.Contain("calam"),
                "control — Calamity's 47 dialogue rows are the reason this catalog exists");
            Assert.That(ShippedSpeakerLabels, Does.Contain("al1"),
                "control — `al1` is one of the 18 labels that appear ONLY single-quoted, so it is the " +
                "one a double-quote-anchored scan drops");
            Assert.That(ShippedSpeakerLabels, Does.Not.Contain("nope"),
                "control — a label that is not in the document must not appear in the census");
        }

        [Test]
        public void Shipped_TheMostUsedSpeakersAreTheOnesTheBeginLandOpensWith()
        {
            // `begDial` is the first thing the player reads, and its rows are Littlepip's. If the table
            // were off by a frame, this is the line that would show the wrong face.
            Assert.AreEqual(1, IndexOf("lp1"), "frame 2");
            Assert.AreEqual(2, IndexOf("lp2"), "frame 3");
            Assert.AreEqual(3, IndexOf("lp3"), "frame 4");
            Assert.AreEqual(4, IndexOf("lp4"), "frame 5");
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static int IndexOf(string label)
        {
            Assert.That(DialoguePortraitFrames.TryGetIndex(label, out int index), Is.True,
                $"'{label}' did not resolve");
            return index;
        }

        private static void AssertIndex(string label, int expected)
        {
            Assert.That(DialoguePortraitFrames.TryGetIndex(label, out int index), Is.True,
                $"'{label}' did not resolve");
            Assert.AreEqual(expected, index, $"'{label}' is frame {expected + 1}");
        }

        private static void AssertUnknown(string label)
        {
            Assert.That(DialoguePortraitFrames.TryGetIndex(label, out int index), Is.False,
                $"'{label ?? "<null>"}' should not resolve");
            Assert.AreEqual(-1, index, "a failed lookup must clear the index, not leave the last one");
        }
    }
}
