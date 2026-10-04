using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using PFE.Systems.Particles;

namespace PFE.Tests.EditMode.Systems.Particles
{
    /// <summary>
    /// Pins <see cref="ParticleSpriteSource"/> — where a <c>&lt;part&gt;</c> row's frames live in the
    /// exported SWF trees, and how many there are.
    ///
    /// <para><b>Why this fixture exists.</b> Both derivations fail <i>silently and identically</i> when
    /// wrong: a folder name that does not match yields zero frames, and zero frames renders as nothing —
    /// which is indistinguishable from a definition that emits nothing, from a budget refusal, and from
    /// an id the emitter could not resolve. There is no crash and no log line to notice. That is the
    /// recurring shape in this project (a belief that went stale while nothing went red), so the
    /// derivation is deliberately kept out of the editor-only importer and executed here instead.</para>
    ///
    /// <para><b>The literals below are real.</b> The symbol rows, the folder name and the sheet geometry
    /// are copied from the trees as measured on 2026-10-04 rather than invented, because the point is the
    /// interaction with the data as written. Where a fact is asserted that the data itself could not
    /// supply (that <c>sprite.swf</c> holds no effect sheets), the test says so rather than implying it
    /// was verified here.</para>
    /// </summary>
    [TestFixture]
    public class ParticleSpriteSourceTests
    {
        /// <summary>The real header rows of <c>pfe/symbolClass/symbols.csv</c>, verbatim.</summary>
        private const string VisSymbolsCsv =
            "3966;\"visualFlame\"\n" +
            "2827;\"visualBlast\"\n" +
            "2767;\"visualGwall\"\n";

        /// <summary>The real rows of <c>sprite1.swf/symbolClass/symbols.csv</c> that matter here, verbatim.</summary>
        private const string BlitSymbolsCsv =
            "17;\"sprExpl\"\n" +
            "15;\"sprExplW\"\n" +
            "29;\"sprIskr\"\n" +
            "0;\"sprite1_fla.MainTimeline\"\n";

        private static ParticleDefinition Def(string id, string vis, string blit, int blitX = 0, int blitY = 0) =>
            new ParticleDefinition { Id = id, Vis = vis, Blit = blit, BlitX = blitX, BlitY = blitY };

        // ── Which pipeline ───────────────────────────────────────────────────

        [Test]
        public void KindOf_PicksVisWhenTheRowNamesAClass()
        {
            Assert.AreEqual(ParticleSpriteKind.Vis, ParticleSpriteSource.KindOf(Def("flare", "visualFlare", null)));
        }

        [Test]
        public void KindOf_PicksBlitWhenTheRowNamesASheet()
        {
            Assert.AreEqual(ParticleSpriteKind.Blit,
                ParticleSpriteSource.KindOf(Def("expl", null, "sprExpl", 240, 240)));
        }

        [Test]
        public void KindOf_IsNoneForATextRowOrANullRow()
        {
            // `numb` / `replic` / `gui` / `take` carry params.txt and have neither attribute. The 118
            // shipped rows include none of them, so this is the guard for hand-edited data.
            Assert.AreEqual(ParticleSpriteKind.None, ParticleSpriteSource.KindOf(Def("numb", null, null)));
            Assert.AreEqual(ParticleSpriteKind.None, ParticleSpriteSource.KindOf(null));
        }

        [Test]
        public void KindOf_PrefersVisWhenBothAreSomehowPresent()
        {
            // 0 of 118 shipped rows carry both, so this is a tie-break for edited data only. It must match
            // ParticleRules.SpawnOne, which sets up the vis cursor before it ever tests Blit.
            Assert.AreEqual(ParticleSpriteKind.Vis,
                ParticleSpriteSource.KindOf(Def("odd", "visualFlare", "sprExpl", 240, 240)));
        }

        [Test]
        public void AssetIdOf_ReturnsTheClassForVisAndTheSheetForBlit()
        {
            Assert.AreEqual("visualFlare", ParticleSpriteSource.AssetIdOf(Def("flare", "visualFlare", null)));
            Assert.AreEqual("sprExpl", ParticleSpriteSource.AssetIdOf(Def("expl", null, "sprExpl", 240, 240)));
            Assert.AreEqual(string.Empty, ParticleSpriteSource.AssetIdOf(Def("numb", null, null)));
        }

        // ── vis= layout ──────────────────────────────────────────────────────

        [Test]
        public void VisFolderName_RepeatsTheSymbolIdInBothHalves()
        {
            // Measured: visualBlast is symbol 2827 and its frames are at
            // pfe/scripts/_assets/sprites/DefineSprite_2827_symbol2827/ (15 PNGs, 1.png..15.png).
            Assert.AreEqual("DefineSprite_2827_symbol2827", ParticleSpriteSource.VisFolderName(2827));

            // Negative control: the folder is NOT the bare-id form. CharacterSpriteImporter also builds
            // the doubled form, so a "DefineSprite_2827" lookup finds nothing — assert the difference
            // rather than trusting the string.
            Assert.AreNotEqual("DefineSprite_2827", ParticleSpriteSource.VisFolderName(2827));
        }

        [Test]
        public void VisFrameNumber_ReadsTheExportedUnpaddedNames()
        {
            Assert.AreEqual(1, ParticleSpriteSource.VisFrameNumber("1.png"));
            Assert.AreEqual(2, ParticleSpriteSource.VisFrameNumber("2.png"));
            Assert.AreEqual(15, ParticleSpriteSource.VisFrameNumber("15.png"));
        }

        [Test]
        public void VisFrameNumber_AlsoReadsThisImportersOwnPaddedOutput()
        {
            // The readback path reads f000.png back, so the parser has to accept both namings. If it only
            // accepted one, the importer would write frames and then load none of them back.
            Assert.AreEqual(1, ParticleSpriteSource.VisFrameNumber("f000.png"));
            Assert.AreEqual(13, ParticleSpriteSource.VisFrameNumber("f012.png"));
            Assert.AreEqual(13, ParticleSpriteSource.VisFrameNumber("f012"));
        }

        [Test]
        public void VisFrameNumber_RejectsNamesThatAreNotFrames()
        {
            Assert.AreEqual(-1, ParticleSpriteSource.VisFrameNumber("sprite.png"));
            Assert.AreEqual(-1, ParticleSpriteSource.VisFrameNumber(""));
            Assert.AreEqual(-1, ParticleSpriteSource.VisFrameNumber(null));
            Assert.AreEqual(-1, ParticleSpriteSource.VisFrameNumber("1a.png"));
            Assert.AreEqual(-1, ParticleSpriteSource.VisFrameNumber(".png"));
        }

        [Test]
        public void VisFrameNumber_RefusesAnOutOfRangeNumberInsteadOfThrowing()
        {
            // "all digits" does not bound the value, so int.Parse would throw OverflowException — out of a
            // scan of an arbitrary folder, which is where an unhandled exception costs the whole import.
            Assert.AreEqual(-1, ParticleSpriteSource.VisFrameNumber("99999999999.png"));
            Assert.AreEqual(-1, ParticleSpriteSource.VisFrameNumber("f99999999999.png"));
        }

        [Test]
        public void VisFrameNumber_SortsNumericallyWherePlainTextSortingWouldNot()
        {
            string[] exported = { "1.png", "10.png", "11.png", "2.png", "3.png" };

            int[] numeric = exported.OrderBy(ParticleSpriteSource.VisFrameNumber).Select(f => ParticleSpriteSource.VisFrameNumber(f)).ToArray();
            CollectionAssert.AreEqual(new[] { 1, 2, 3, 10, 11 }, numeric);

            // Negative control, and the whole reason the importer must not sort by name: a plain string
            // sort puts 10.png before 2.png. Asserting that the naive order differs is what makes the
            // assertion above mean something.
            string[] byName = exported.OrderBy(f => f, System.StringComparer.Ordinal).ToArray();
            Assert.AreEqual("10.png", byName[1], "the naive text sort should disagree — if it agrees, this test proves nothing");
            Assert.AreNotEqual(byName[1], exported.OrderBy(ParticleSpriteSource.VisFrameNumber).First(f => ParticleSpriteSource.VisFrameNumber(f) == 2));
        }

        // ── blit= layout ─────────────────────────────────────────────────────

        [Test]
        public void BlitSheetFileName_PrefixesTheSymbolId()
        {
            // Measured: sprite1.swf/images/15_sprExplW.png, and 17_sprExpl.png for sprExpl.
            Assert.AreEqual("15_sprExplW.png", ParticleSpriteSource.BlitSheetFileName(15, "sprExplW"));
            Assert.AreEqual("17_sprExpl.png", ParticleSpriteSource.BlitSheetFileName(17, "sprExpl"));
        }

        [Test]
        public void BlitSwfFolderIsSprite1NotSprite()
        {
            // The trap this pins: sprite.swf ALSO exists and ALSO has images/<id>_<Class>.png, but it
            // holds unit sprites (10_sprSlaver3.png, 16_sprRat.png) and none of the effect sheets. Pointing
            // at it reports every one of the 24 blit rows as missing art.
            Assert.AreEqual("sprite1", ParticleSpriteSource.BlitSwfFolderName);
            Assert.AreNotEqual("sprite", ParticleSpriteSource.BlitSwfFolderName);
        }

        [Test]
        public void FrameCountFromSheet_DividesWidthByTheCellWidth()
        {
            // All four measured from the real sheets.
            Assert.AreEqual(14, ParticleSpriteSource.FrameCountFromSheet(3360, 240));  // sprExpl, sprExplW
            Assert.AreEqual(30, ParticleSpriteSource.FrameCountFromSheet(7200, 240));  // sprBale
            Assert.AreEqual(6,  ParticleSpriteSource.FrameCountFromSheet(18, 3));      // sprIskr
            Assert.AreEqual(17, ParticleSpriteSource.FrameCountFromSheet(850, 50));    // sprFire
            Assert.AreEqual(20, ParticleSpriteSource.FrameCountFromSheet(4800, 240));  // sprBl1/2/3
        }

        [Test]
        public void FrameCountFromSheet_ReturnsZeroRatherThanOneForDegenerateInput()
        {
            // 0 is the value ParticleRules treats as "frame count unknown" and which suppresses the
            // random-frame draw. Returning 1 would silently pin every animation to frame 0 — a still
            // image, which reads as broken art rather than as missing art.
            Assert.AreEqual(0, ParticleSpriteSource.FrameCountFromSheet(100, 0));
            Assert.AreEqual(0, ParticleSpriteSource.FrameCountFromSheet(100, -240));
            Assert.AreEqual(0, ParticleSpriteSource.FrameCountFromSheet(10, 240));  // narrower than one cell
            Assert.AreEqual(0, ParticleSpriteSource.FrameCountFromSheet(0, 240));
            Assert.AreEqual(1, ParticleSpriteSource.FrameCountFromSheet(240, 240));
        }

        // ── The symbol maps ──────────────────────────────────────────────────

        [Test]
        public void ParseSymbolCsv_ReadsTheRealFormat()
        {
            Dictionary<string, int> map = ParticleSpriteSource.ParseSymbolCsv(VisSymbolsCsv, out int skipped);

            Assert.AreEqual(0, skipped);
            Assert.AreEqual(2827, map["visualBlast"]);
            Assert.AreEqual(3966, map["visualFlame"]);
            Assert.AreEqual(2767, map["visualGwall"]);
            Assert.AreEqual(3, map.Count);
        }

        [Test]
        public void ParseSymbolCsv_KeepsTheZeroSymbolRowAndIgnoresBlankLines()
        {
            // sprite1.swf's real table ends with a `0;"sprite1_fla.MainTimeline"` row. It is harmless and
            // must not be treated as a parse failure, and the trailing newline must not count as a skip.
            Dictionary<string, int> map = ParticleSpriteSource.ParseSymbolCsv(BlitSymbolsCsv, out int skipped);

            Assert.AreEqual(0, skipped, "a blank trailing line is not a malformed row");
            Assert.AreEqual(15, map["sprExplW"]);
            Assert.AreEqual(17, map["sprExpl"]);
            Assert.AreEqual(0, map["sprite1_fla.MainTimeline"]);
        }

        [Test]
        public void ParseSymbolCsv_CountsMalformedRowsInsteadOfSwallowingThem()
        {
            const string messy =
                "2827;\"visualBlast\"\n" +
                "not-a-number;\"visualX\"\n" +
                "no separator here\n" +
                "999;\"\"\n" +
                "\n" +
                "3966;\"visualFlame\"\n";

            Dictionary<string, int> map = ParticleSpriteSource.ParseSymbolCsv(messy, out int skipped);

            Assert.AreEqual(2, map.Count, "only the two well-formed rows should survive");
            Assert.AreEqual(3, skipped, "the bad id, the missing separator and the empty class name");
            CollectionAssert.DoesNotContain(map.Keys, "visualX");
        }

        [Test]
        public void ParseSymbolCsv_IsFirstWinsOnADuplicateClassName()
        {
            // A hand-edit could duplicate a class name. Last-wins would make resolution depend on file
            // order, which nothing guarantees; first-wins is stable and matches the asset's own index.
            const string duplicated = "2827;\"visualBlast\"\n9999;\"visualBlast\"\n";
            Dictionary<string, int> map = ParticleSpriteSource.ParseSymbolCsv(duplicated, out _);
            Assert.AreEqual(2827, map["visualBlast"]);
        }

        [Test]
        public void ParseSymbolCsv_ReturnsAnEmptyMapForNullAndEmptyInput()
        {
            Assert.AreEqual(0, ParticleSpriteSource.ParseSymbolCsv(null, out int a).Count);
            Assert.AreEqual(0, a);
            Assert.AreEqual(0, ParticleSpriteSource.ParseSymbolCsv("", out int b).Count);
            Assert.AreEqual(0, b);
        }

        [Test]
        public void TheTwoSymbolTablesAreDifferentFiles()
        {
            // The failure this pins: looking a blit id up in the vis table (or the reverse). Both files
            // exist and both parse, so nothing fails — the id is simply absent and its rows lose their art.
            Assert.AreNotEqual(ParticleSpriteSource.VisSymbolCsvRelativePath,
                               ParticleSpriteSource.BlitSymbolCsvRelativePath);
            Assert.IsTrue(ParticleSpriteSource.VisSymbolCsvRelativePath.StartsWith("pfe/"),
                          "the vis table lives under pfe/");
            Assert.IsTrue(ParticleSpriteSource.BlitSymbolCsvRelativePath.StartsWith("sprite1.swf/"),
                          "the blit table lives under sprite1.swf/");
        }

        // ── The output naming, and the round trip that ties it together ──────

        [Test]
        public void OutputFrameFileName_IsZeroPaddedSoItSortsAsText()
        {
            Assert.AreEqual("f000.png", ParticleSpriteSource.OutputFrameFileName(0));
            Assert.AreEqual("f012.png", ParticleSpriteSource.OutputFrameFileName(12));
            Assert.AreEqual("f999.png", ParticleSpriteSource.OutputFrameFileName(999));
        }

        [Test]
        public void OutputNamingRoundTripsThroughTheFrameParser()
        {
            // The importer writes fNNN.png and then reads the folder back to build the sprite array. If
            // the two namings ever disagreed, every import would write frames and load none of them, and
            // the only symptom would be effects that do not appear.
            for (int frame = 0; frame < 40; frame++)
            {
                int parsed = ParticleSpriteSource.VisFrameNumber(ParticleSpriteSource.OutputFrameFileName(frame));
                Assert.AreEqual(frame + 1, parsed, $"frame {frame} did not round-trip");
            }
        }

        [Test]
        public void OutputNamesSortAsTextInFrameOrder()
        {
            string[] written = Enumerable.Range(0, 30)
                .Select(ParticleSpriteSource.OutputFrameFileName)
                .OrderBy(n => n, System.StringComparer.Ordinal)
                .ToArray();

            for (int i = 0; i < written.Length; i++)
                Assert.AreEqual(ParticleSpriteSource.OutputFrameFileName(i), written[i]);
        }
    }
}
