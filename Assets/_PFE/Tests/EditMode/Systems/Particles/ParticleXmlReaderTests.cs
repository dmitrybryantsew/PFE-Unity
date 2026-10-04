using System.Collections.Generic;
using NUnit.Framework;
using PFE.Systems.Particles;

namespace PFE.Tests.EditMode.Systems.Particles
{
    /// <summary>
    /// Pins <see cref="ParticleXmlReader"/> — the port of <c>Emitter(param1)</c> and
    /// <c>Emitter.init()</c> (<c>Emitter.as:103-139</c>).
    ///
    /// <para><b>Why this fixture exists.</b> The reader used to be the kind of code that lives inside an
    /// editor importer and is therefore unreachable from the offline wall. That is exactly where this
    /// project's recurring failure lives — a value that is silently wrong while nothing goes red — and
    /// this reader has a whole family of them: five attribute names in the shipped data are
    /// <b>suffixes</b> of other attribute names, so a per-name search returns the wrong value on 26 rows
    /// without ever failing.</para>
    ///
    /// <para>The snippets below are copied from the real block rather than invented, because the point
    /// is the interaction with the data as written.</para>
    /// </summary>
    [TestFixture]
    public class ParticleXmlReaderTests
    {
        private static ParticleDefinition Find(ParticleXmlParseResult result, string id)
        {
            foreach (ParticleDefinition def in result.Definitions)
            {
                if (def.Id == id) return def;
            }
            Assert.Fail($"'{id}' was not parsed at all");
            return null;
        }

        // ── The five suffix collisions ────────────────────────────────────────
        //
        // Each of these asserts the value that a naive `Regex.Match(attrs, name + "='([^']*)'")` gets
        // WRONG. They are the reason the reader parses attributes into a map instead of searching for
        // them by name.

        [Test]
        public void DxIsNotReadFromRdx_AndDxIsSetByNoRowAtAll()
        {
            // 'flame' carries rdx='4' and no dx. A suffix-matching reader invents dx = 4 — a flat
            // sideways drift on the one particle family where it would be most visible.
            const string src = "<part id='flame' vis='visualFlame' alph='1' minliv='15' grav='-1' " +
                               "rx='20' ry='20' rdx='4' rdy='4' blend='screen'/>";

            ParticleDefinition flame = Find(ParticleXmlReader.Parse(src), "flame");

            Assert.That(flame.DX, Is.EqualTo(0f), "dx is absent from the data entirely");
            Assert.That(flame.RDX, Is.EqualTo(4f), "and rdx is the one that is really there");
        }

        [Test]
        public void DyIsNotReadFromRdy()
        {
            const string src = "<part id='flame' vis='visualFlame' rdx='4' rdy='4'/>";

            ParticleDefinition flame = Find(ParticleXmlReader.Parse(src), "flame");

            Assert.That(flame.DY, Is.EqualTo(0f), "dy is absent");
            Assert.That(flame.RDY, Is.EqualTo(4f));
        }

        [Test]
        public void GravIsNotReadFromRgrav()
        {
            // 'die_spark' carries grav='-0.1' AND rgrav='-0.2'. A suffix-matching reader would read the
            // jitter as the gravity itself — a 2x stronger, non-random pull.
            const string src = "<part id='die_spark' vis='die_spark' alph='1' minliv='20' rliv='20' " +
                               "grav='-0.1' rgrav='-0.2' rdx='1' rdy='1' rsc='0.7' blend='screen'/>";

            ParticleDefinition spark = Find(ParticleXmlReader.Parse(src), "die_spark");

            Assert.That(spark.Grav, Is.EqualTo(-0.1f));
            Assert.That(spark.RGrav, Is.EqualTo(-0.2f));
        }

        [Test]
        public void AlphIsNotReadFromPrealph()
        {
            // This is the collision that LOSES data rather than inventing it: in every row carrying both,
            // prealph is written after alph, so a suffix-matching reader's last match wins and the
            // fade-in disappears.
            ParticleDefinition pre = Find(ParticleXmlReader.Parse("<part id='t' vis='v' prealph='1'/>"), "t");

            Assert.IsFalse(pre.Alph, "the row does not set alph");
            Assert.IsTrue(pre.PreAlph);
        }

        [Test]
        public void ScaleIsNotReadFromCamscale()
        {
            // The row that makes this discriminating: a suffix-matching reader takes scale='1' out of
            // camscale='1' and never sees the row's own scale='2'.
            const string src = "<part id='t' vis='v' camscale='1' scale='2'/>";

            ParticleDefinition def = Find(ParticleXmlReader.Parse(src), "t");

            Assert.That(def.Scale, Is.EqualTo(2f), "the row's own scale, not the one inside camscale");
            Assert.IsTrue(def.CamScale);
        }

        // ── The comment block ─────────────────────────────────────────────────

        [Test]
        public void AttributesInsideACommentAreNotData()
        {
            // The block's own comment documents water='1' — and that is the ONLY water='1' anywhere in
            // AllData.as. A reader that scanned the whole file rather than per row would invent a water
            // rule no row has.
            const string src =
                "<!-- water='1' - частица существует только вне воды; rot='1' - случайный угол -->\n" +
                "<part id='real' vis='v' minliv='10'/>";

            ParticleDefinition real = Find(ParticleXmlReader.Parse(src), "real");

            Assert.That(real.Water, Is.EqualTo(0), "the documented water='1' is not a row attribute");
            Assert.That(real.Rot, Is.EqualTo(0), "nor is the documented rot='1'");
        }

        [Test]
        public void ACommentedOutRowIsNotParsed()
        {
            const string src =
                "<!-- <part id='ghost' vis='visualGhost' minliv='5'/> -->\n" +
                "<part id='real' vis='v' minliv='10'/>";

            ParticleXmlParseResult result = ParticleXmlReader.Parse(src);

            Assert.That(result.Definitions.Count, Is.EqualTo(1));
            Assert.That(result.Definitions[0].Id, Is.EqualTo("real"));
        }

        // ── The ignored-attribute report ──────────────────────────────────────

        [Test]
        public void ReportsAttributesEmitterHasNoFieldFor()
        {
            // 'gas' and 'steam' are the rows that carry rr and rd. Emitter declares neither, so
            // hasOwnProperty rejects them and the oracle drops them silently — rr is even documented in
            // the block's own comment as random rotation speed, and has never worked.
            const string src =
                "<part id='gas' vis='visualGas' ctrans='1' alph='1' prealph='1' minliv='60' " +
                "rr='6' rdy='1' rd='-2' rot='1' maxkol='1' imp='1'/>\n" +
                "<part id='steam' vis='visualSteam' ctrans='1' minliv='30' rr='12' rdy='1' " +
                "grav='-0.2' rd='-2' rot='1' anim='1' rsc='0.3' maxkol='1'/>";

            ParticleXmlParseResult result = ParticleXmlReader.Parse(src);

            Assert.That(result.IgnoredAttributes, Is.EquivalentTo(new[] { "rr", "rd" }),
                "exactly the two names the oracle discards");

            ParticleDefinition gas = Find(result, "gas");
            Assert.That(gas.Rot, Is.EqualTo(1), "rot IS a field, so it survives");
            Assert.That(gas.MaxKol, Is.EqualTo(1));
            Assert.That(gas.Imp, Is.EqualTo(1));
        }

        [Test]
        public void AWellFormedBlockReportsNothingIgnored()
        {
            const string src = "<part id='expl' blit='sprExpl' blitx='240' blity='240' anim='1' minliv='15' imp='1'/>";

            ParticleXmlParseResult result = ParticleXmlReader.Parse(src);

            Assert.That(result.IgnoredAttributes, Is.Empty);
            Assert.That(result.MalformedAttributes, Is.Empty);
        }

        [Test]
        public void FrameAndDframeAreRecognisedButDead_SoTheyAreNotReportedAsIgnored()
        {
            // Emitter declares both, so hasOwnProperty accepts them — and cast then resets them to 0
            // before reading, so they never take effect. Recognised-but-dead, not unknown.
            ParticleXmlParseResult result = ParticleXmlReader.Parse("<part id='t' vis='v' frame='5' dframe='2'/>");

            Assert.That(result.IgnoredAttributes, Is.Empty);
        }

        // ── Types and defaults ────────────────────────────────────────────────

        [Test]
        public void DefaultsMatchTheEmitterDeclarations()
        {
            ParticleDefinition def = Find(ParticleXmlReader.Parse("<part id='bare' vis='visualTest'/>"), "bare");

            Assert.That(def.MinLiv, Is.EqualTo(20), "Emitter.as:67");
            Assert.That(def.RLiv, Is.EqualTo(0));
            Assert.That(def.Brake, Is.EqualTo(1f), "Emitter.as:91");
            Assert.That(def.Scale, Is.EqualTo(1f), "Emitter.as:55");
            Assert.That(def.Sloy, Is.EqualTo(3), "Emitter.as:29");
            Assert.That(def.Anim, Is.EqualTo(0));
            Assert.That(def.Blend, Is.EqualTo("normal"), "Emitter.as:51");
            Assert.That(def.BlitLoopFrames, Is.EqualTo(-1), "Emitter.as:39 — blitf's own default, not 0");
            Assert.That(def.BlitDelta, Is.EqualTo(1f), "Emitter.as:41");
            Assert.That(def.Imp, Is.EqualTo(0));
            Assert.That(def.MaxKol, Is.EqualTo(0));
            Assert.That(def.Water, Is.EqualTo(0));
            Assert.That(def.Otklad, Is.EqualTo(0));
            Assert.That(def.Rot, Is.EqualTo(0));
            Assert.That(def.Grav, Is.EqualTo(0f));
            Assert.That(def.RGrav, Is.EqualTo(0f));
            Assert.That(def.Filter, Is.Null);
        }

        [Test]
        public void ABooleanAttributeIsTrueOnPresence_EvenWhenItSaysZero()
        {
            // The oracle's constructor test is `if (this[name] is Boolean)`, not a parse — so ANY
            // presence sets true. No shipped row writes a boolean as '0', which is the only reason this
            // quirk is harmless today.
            ParticleDefinition def = Find(ParticleXmlReader.Parse("<part id='t' vis='v' alph='0'/>"), "t");

            Assert.IsTrue(def.Alph, "presence, not value — matching Emitter.as:113-116");
        }

        [Test]
        public void BooleansAbsentStayFalse()
        {
            ParticleDefinition def = Find(ParticleXmlReader.Parse("<part id='t' vis='v'/>"), "t");

            Assert.IsFalse(def.Alph);
            Assert.IsFalse(def.PreAlph);
            Assert.IsFalse(def.Ctrans);
            Assert.IsFalse(def.CamScale);
        }

        [Test]
        public void NumbersAreParsedWithTheInvariantCulture()
        {
            // The default float overloads accept group separators, so on a de-DE machine '0.4' parses as
            // 400 — a silent x1000 error on every rsc and rgrav.
            ParticleDefinition def = Find(ParticleXmlReader.Parse("<part id='t' vis='v' rsc='0.4' rgrav='-0.2'/>"), "t");

            Assert.That(def.Rsc, Is.EqualTo(0.4f));
            Assert.That(def.RGrav, Is.EqualTo(-0.2f));
        }

        [Test]
        public void AnUnparseableNumberFallsBackToTheDefault_AndIsReported()
        {
            ParticleXmlParseResult result = ParticleXmlReader.Parse("<part id='t' vis='v' minliv='abc'/>");
            ParticleDefinition def = Find(result, "t");

            Assert.That(def.MinLiv, Is.EqualTo(20), "the declared default, not 0");
            Assert.That(result.MalformedAttributes, Is.EquivalentTo(new[] { "minliv" }));
        }

        // ── Shape ─────────────────────────────────────────────────────────────

        [Test]
        public void EveryRowIsParsedInFileOrder()
        {
            const string src =
                "<part id='a' vis='v'/>\n" +
                "<part id='b' blit='sprB' blitx='4' blity='4'/>\n" +
                "<part id='c' vis='w'/>";

            ParticleXmlParseResult result = ParticleXmlReader.Parse(src);

            Assert.That(result.Definitions.Count, Is.EqualTo(3));
            Assert.That(result.Definitions[0].Id, Is.EqualTo("a"));
            Assert.That(result.Definitions[1].Id, Is.EqualTo("b"));
            Assert.That(result.Definitions[2].Id, Is.EqualTo("c"));
        }

        [Test]
        public void VisAndBlitAreMutuallyExclusiveInTheShippedData()
        {
            // 94 vis rows and 24 blit rows, and no row sets both or neither. Asserted on the reader so a
            // later row that breaks the invariant shows up here rather than as a rendering mystery.
            const string src = "<part id='a' vis='v'/>\n<part id='b' blit='sprB'/>";

            foreach (ParticleDefinition def in ParticleXmlReader.Parse(src).Definitions)
            {
                bool hasVis = !string.IsNullOrEmpty(def.Vis);
                bool hasBlit = !string.IsNullOrEmpty(def.Blit);
                Assert.That(hasVis ^ hasBlit, Is.True, $"'{def.Id}' must have exactly one visual path");
            }
        }

        [Test]
        public void TheBlitLoopLengthIsReadAsItself_NotAsTheSheetSize()
        {
            // `fire`: blit='sprFire' blitf='17' blitx='50' blity='67' anim='2' — against a 32-frame
            // sheet, so blitf is the repeat length.
            const string src = "<part id='fire' blit='sprFire' blitf='17' blitx='50' blity='67' anim='2' " +
                               "alph='1' prealph='1' minliv='100' rliv='30' blend='hardlight' imp='1'/>";

            ParticleDefinition fire = Find(ParticleXmlReader.Parse(src), "fire");

            Assert.That(fire.BlitLoopFrames, Is.EqualTo(17));
            Assert.That(fire.BlitX, Is.EqualTo(50));
            Assert.That(fire.BlitY, Is.EqualTo(67));
            Assert.That(fire.Anim, Is.EqualTo(2));
            Assert.That(fire.Blend, Is.EqualTo("hardlight"));
            Assert.That(fire.PreAlph, Is.True);
            Assert.That(fire.Alph, Is.True);
        }

        [Test]
        public void BlitRowsWithoutBlitfKeepTheMinusOneDefault()
        {
            ParticleDefinition def = Find(
                ParticleXmlReader.Parse("<part id='expl' blit='sprExpl' blitx='240' blity='240' anim='1' minliv='15' imp='1'/>"),
                "expl");

            Assert.That(def.BlitLoopFrames, Is.EqualTo(-1), "so the `> 0` guard in cast stays false");
        }

        // ── Degenerate input ──────────────────────────────────────────────────

        [Test]
        public void EmptyInputYieldsNoRows_AndDoesNotThrow()
        {
            Assert.That(ParticleXmlReader.Parse(null).Definitions, Is.Empty);
            Assert.That(ParticleXmlReader.Parse(string.Empty).Definitions, Is.Empty);
        }

        [Test]
        public void AFileWithNoPartBlockYieldsNoRows()
        {
            // The "0 found reported as success" shape: the importer must be able to tell this apart from
            // a block that moved, which is why it counts literal occurrences too.
            ParticleXmlParseResult result = ParticleXmlReader.Parse("<all><unit id='x'/></all>");

            Assert.That(result.Definitions, Is.Empty);
            Assert.That(result.IgnoredAttributes, Is.Empty);
        }

        [Test]
        public void DuplicateIdsAreAllReturned_LeavingTheTableToResolveThem()
        {
            // The shipped data has no duplicate ids. If one appears, the reader must not silently drop a
            // row — the table keeps the first and the row count still shows the collision.
            const string src = "<part id='dup' vis='a'/>\n<part id='dup' vis='b'/>";

            ParticleXmlParseResult result = ParticleXmlReader.Parse(src);

            Assert.That(result.Definitions.Count, Is.EqualTo(2));

            var table = new ParticleDefinitionTable(result.Definitions);
            Assert.That(table.Count, Is.EqualTo(2));
            Assert.That(table.TryGet("dup", out ParticleDefinition resolved), Is.True);
            Assert.That(resolved.Vis, Is.EqualTo("a"), "the first row wins, deterministically");
        }
    }
}
