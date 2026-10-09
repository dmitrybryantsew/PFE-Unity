#if UNITY_EDITOR
using NUnit.Framework;
using PFE.Data.Definitions;

namespace PFE.Tests.Editor.UnitAnimation
{
    /// <summary>
    /// Tests for <see cref="UnitAnimationParser"/> — the port of the oracle's
    /// <c>anims[xbl.@id] = new BlitAnim(xbl)</c> loop (<c>Unit.as:1430-1436</c>).
    ///
    /// <para><b>The fixtures are real rows from <c>AllData.as</c>, not invented ones.</b> That matters
    /// here more than usual: the interesting cases are the ones a reasonable implementation gets wrong,
    /// and two of them are only visible in the actual data — <c>rep='0'</c> (which means <i>true</i>,
    /// because AS3 presence-tests the attribute) and rows that carry no <c>y</c> at all.</para>
    /// </summary>
    [TestFixture]
    public class UnitAnimationParserTests
    {
        // ── the oracle's own text ────────────────────────────────────────────────────────────
        //
        // `raider`, verbatim from AllData.as. 11 rows, and the only unit node in the file that shows
        // every shape at once: a bare row, a row with a sheet index and a length, a `stab` row, and
        // two rows with no `y`.

        const string RaiderNode = @"
<unit id='raider' fraction='2' cat='2'>
    <move brake='0.3' levit_max='60' levitaccel='1.6' damwall='25'/>
    <comb skill='0.8' aqual='0.5' damage='8' levitatk='0'/>
    <vis noise='600' replic='raider' visdam='1'/>
    <n>Общие параметры рейдеров</n>
    <blit id='stay'/>
    <blit id='trot' y='1' len='17' rf='1' rep='1'/>
    <blit id='run' y='2' len='8' rep='1'/>
    <blit id='jump' y='3' len='16' stab='1'/>
    <blit id='die' y='4' len='20'/>
    <blit id='death' y='5'/>
    <blit id='fall' y='5' len='13'/>
    <blit id='derg' y='6' len='8' rep='1'/>
    <blit id='plav' y='7' len='24' rep='1'/>
    <blit id='laz' y='8' len='12' rep='1'/>
    <blit id='walk' y='9' len='24' rep='1'/>
</unit>";

        // `zombie` (the family) and `zombie3` (the variant), verbatim. The variant declares exactly one
        // row — `pre` — which is the delta the overlay has to apply.
        const string ZombieFamilyNode = @"
<unit id='zombie' fraction='0' cat='2'>
    <vis noise='600' visdam='1'/>
    <blit id='stay' y='0' len='20' rep='1'/>
    <blit id='walk' y='1' len='18' rep='1'/>
    <blit id='run' y='2' len='12' rep='1'/>
    <blit id='jump' y='3' len='16' stab='1'/>
    <blit id='die' y='4' len='20'/>
    <blit id='death' y='5'/>
    <blit id='fall' y='5' len='13'/>
    <blit id='plav' y='7' len='24' rep='1'/>
    <blit id='laz' y='8' len='12' rep='1'/>
</unit>";

        const string Zombie3Node = @"
<unit id='zombie3' cont='zombie' xp='150' cat='3' parent='zombie'>
    <un ss='3'/>
    <phis sX='55' sY='70' massa='65'/>
    <comb hp='85' damage='15'/>
    <vis blit='sprZombie3' sprX='120' sex='w' visdam='3' sdamage='15' stipdam='7'/>
    <blit id='pre' y='8' len='18'/>
    <n>Ядовитый</n>
</unit>";

        // ── the presence test, which is the whole reason this parser exists ───────────────────

        /// <summary>
        /// <c>rep='0'</c> means <b>true</b>. AS3 tests <c>param1.@rep.length()</c>
        /// (<c>BlitAnim.as:3703</c>), so the attribute's VALUE is never read — only its presence.
        ///
        /// <para>This is not a hypothetical edge case: <c>AllData.as</c> really does carry three
        /// <c>rep='0'</c> rows, all on <c>stay</c>. An implementation that parsed the value would read
        /// them as "do not loop" and the affected units would freeze on one idle frame.</para>
        /// </summary>
        [Test]
        public void Parse_RepZero_IsStillReplay_()
        {
            var result = UnitAnimationParser.Parse("<blit id='stay' len='1' rep='0'/>");

            Assert.IsTrue(result.Animations.stay.replay,
                "rep='0' must parse as replay=true — AS3 presence-tests the attribute, it does not read its value");
        }

        [Test]
        public void Parse_RepAbsent_IsNotReplay()
        {
            var result = UnitAnimationParser.Parse("<blit id='die' y='4' len='20'/>");

            Assert.IsFalse(result.Animations.die.replay, "a row with no rep attribute must not loop");
        }

        [Test]
        public void Parse_StabPresent_IsStatic_AndIsPresenceTestedToo()
        {
            var result = UnitAnimationParser.Parse("<blit id='jump' y='3' len='16' stab='1'/>");

            Assert.IsTrue(result.Animations.jump.isStatic, "stab drives setStab(), not a self-advancing step()");
            Assert.IsFalse(result.Animations.jump.replay, "a stab row need not loop");
        }

        // ── defaults, straight from the BlitAnim field initialisers ───────────────────────────

        /// <summary>
        /// A row with no <c>y</c> keeps <c>id = 0</c> — the field default — because
        /// <c>BlitAnim.as:3681</c> guards the assignment with <c>if(param1.@y.length())</c>.
        /// The <c>raider</c> node has four such rows, and a parser that defaulted the row to anything
        /// else would draw them from the wrong strip of the sheet.
        /// </summary>
        [Test]
        public void Parse_MissingY_DefaultsRowToZero()
        {
            var result = UnitAnimationParser.Parse("<blit id='walk' len='13' ff='1' rf='2' df='0.5' rep='1'/>");

            Assert.AreEqual(0, result.Animations.walk.row, "no y attribute => row 0, the BlitAnim field default");
            Assert.AreEqual(13, result.Animations.walk.length);
            Assert.AreEqual(1, result.Animations.walk.firstFrame, "ff is the first frame INDEX, not a skip amount");
            Assert.AreEqual(2, result.Animations.walk.returnFrame, "rf is the replay restart index, not a reverse flag");
            Assert.That(result.Animations.walk.frameStep, Is.EqualTo(0.5f),
                "df is the per-step advance and may be fractional — 0.5 means every other frame");
        }

        [Test]
        public void Parse_MissingLen_DefaultsToOne()
        {
            var result = UnitAnimationParser.Parse("<blit id='stay'/>");

            Assert.AreEqual(1, result.Animations.stay.length, "maxf defaults to 1 in BlitAnim");
            Assert.That(result.Animations.stay.frameStep, Is.EqualTo(1f), "df defaults to 1");
            Assert.AreEqual(0, result.Animations.stay.firstFrame);
            Assert.AreEqual(0, result.Animations.stay.returnFrame);
        }

        // ── the real nodes ───────────────────────────────────────────────────────────────────

        [Test]
        public void Parse_RaiderNode_ReadsElevenRows()
        {
            var result = UnitAnimationParser.Parse(RaiderNode);

            Assert.AreEqual(11, result.RowsRead, "the raider node declares 11 blit rows");
            Assert.AreEqual(0, result.RowsWithoutId);

            Assert.AreEqual(0, result.Animations.stay.row);
            Assert.AreEqual(1, result.Animations.trot.row);
            Assert.AreEqual(17, result.Animations.trot.length);
            Assert.AreEqual(1, result.Animations.trot.returnFrame);
            Assert.IsTrue(result.Animations.trot.replay);

            Assert.AreEqual(3, result.Animations.jump.row);
            Assert.IsTrue(result.Animations.jump.isStatic, "raider's jump is the stab row");

            Assert.AreEqual(24, result.Animations.walk.length);
            Assert.AreEqual(9, result.Animations.walk.row, "walk is the bottom row of the raider sheet");

            // `plav`/`laz` are the Russian ids; they must land on the swim/climb fields, not vanish.
            Assert.AreEqual(7, result.Animations.swim.row, "plav (плавать) maps to swim");
            Assert.AreEqual(8, result.Animations.climb.row, "laz (лазать) maps to climb");
        }

        /// <summary>
        /// An id with no field in <see cref="AnimationSet"/> is <b>reported</b>, not silently dropped — a
        /// silent drop is how a state that the game actually plays goes missing from the port without
        /// anything failing.
        ///
        /// <para><b>The fixture is synthetic now, and that is the only honest option.</b> This used to
        /// parse <c>RaiderNode</c> and assert <c>derg</c> was unmapped. It is not any more: <c>derg</c>,
        /// <c>super</c> and <c>attack</c> are all authored rows (by nine units between them) and
        /// <see cref="AnimationSet"/> now has a field for each, so <c>AllData.as</c> holds no unmapped id
        /// at all. Asserting the report against a real id would be asserting that the port had lost a row
        /// — the opposite of the intent. The behaviour is still real, so it is driven with a row that
        /// genuinely has no field.</para>
        /// </summary>
        [Test]
        public void Parse_UnmappedId_IsReportedNotDropped()
        {
            const string synthetic = @"<blit id='stay' y='0' len='4'/>
<blit id='nosuchstate' y='9' len='7'/>";

            var result = UnitAnimationParser.Parse(synthetic);

            CollectionAssert.Contains(result.UnmappedIds, "nosuchstate");
            Assert.IsFalse(AnimationSet.IsMapped("nosuchstate"), "control: the id genuinely has no field");
            Assert.IsTrue(AnimationSet.IsMapped("stay"), "control: stay genuinely has one");

            // Control that the row was refused rather than stored under a fallback id.
            Assert.AreEqual(1, result.SetIds.Count, "only `stay` should have been stored");
        }

        [Test]
        public void KnownUnmappedIds_IsEmptyBecauseEveryAuthoredIdHasAField()
        {
            // Empty on purpose — see the remark on UnitAnimationParser.KnownUnmappedIds. Pinned so a
            // future row the set cannot hold is added here deliberately rather than discovered later as a
            // silently missing animation.
            CollectionAssert.IsEmpty(UnitAnimationParser.KnownUnmappedIds);

            // Regression guard for the three ids that used to be listed here. Each is authored by real
            // units, so each must have a field; if one is dropped from AnimationSet this fails rather than
            // the row quietly vanishing from nine units' sheets.
            foreach (string id in new[] { "attack", "derg", "super" })
            {
                Assert.IsTrue(AnimationSet.IsMapped(id),
                    $"'{id}' is an authored row in AllData.as (scorp1..3 / raider,slaver,zebra / " +
                    "zombie2,5,7) and must have a field in AnimationSet.");
            }
        }

        /// <summary>
        /// The three recovered ids land in their own fields, with the oracle's own numbers — the positive
        /// control for the guard above. <c>raider</c>'s <c>derg</c> is
        /// <c>&lt;blit id='derg' y='6' len='8' rep='1'/&gt;</c> (<c>AllData.as:84</c>).
        /// </summary>
        [Test]
        public void Parse_RecoveredIds_LandInTheirOwnFields()
        {
            var raider = UnitAnimationParser.Parse(RaiderNode);
            Assert.AreEqual(8, raider.Animations.derg.length, "raider's derg row is len='8'");
            Assert.AreEqual(6, raider.Animations.derg.row, "raider's derg row is y='6'");
            Assert.IsTrue(raider.Animations.derg.replay, "raider's derg row is rep='1'");
            CollectionAssert.DoesNotContain(raider.UnmappedIds, "derg",
                "derg is mapped now, so it must not be reported as unmapped");

            // The other two, with their real rows (AllData.as:1150 and :872).
            var scorp = UnitAnimationParser.Parse("<blit id='attack' len='15' ff='1'/>");
            Assert.AreEqual(15, scorp.Animations.attack.length);

            var zombie = UnitAnimationParser.Parse("<blit id='super' y='8' len='4' rep='1'/>");
            Assert.AreEqual(4, zombie.Animations.super.length);
            Assert.AreEqual(8, zombie.Animations.super.row);
        }

        // ── the family/variant overlay — the controller's double getXmlParam call ────────────

        /// <summary>
        /// The oracle reads the family node first and the unit's own node second
        /// (<c>UnitAlicorn.as:233-234</c>), and because <c>Unit.as:1430</c> assigns one id at a time the
        /// second pass <b>overlays</b>. So <c>zombie3</c> ends up with the family's nine states, with
        /// <c>pre</c> replaced by its own row.
        /// </summary>
        [Test]
        public void Parse_WithFamilyNode_OverlaysOwnRowOnTopOfFamily()
        {
            var result = UnitAnimationParser.Parse(Zombie3Node, ZombieFamilyNode);

            // Family states survive.
            Assert.AreEqual(20, result.Animations.stay.length, "stay comes from the family node");
            Assert.AreEqual(18, result.Animations.walk.length, "walk comes from the family node");
            Assert.AreEqual(12, result.Animations.run.length);

            // The variant's own row wins.
            Assert.AreEqual(18, result.Animations.preAttack.length, "zombie3's own pre row overrides");
            Assert.AreEqual(8, result.Animations.preAttack.row);

            // Zero, not one: `pre` is ADDED, not overridden. The family node declares stay/trot/run/
            // jump/die/death/fall/dig/walk and no `pre` at all — checked against AllData.as, where
            // <unit id='zombie'> carries the same nine rows and zombie3 carries only <blit id='pre'>.
            // The counter is a delta count, so it stays 0 here; the overlay itself is proven by the
            // assertions above and below (pre appears, every family-only state survives).
            Assert.AreEqual(0, result.RowsOverridingTemplate,
                "zombie3's `pre` is a state the family never set, so nothing is overridden");

            // And it is a genuine overlay, not a replace: the family's untouched ids are still there.
            Assert.IsTrue(result.Animations.stay.replay);
            Assert.AreEqual(24, result.Animations.swim.length,
                "plav (family-only, not mentioned by zombie3) must survive the overlay");
            Assert.AreEqual(12, result.Animations.climb.length,
                "laz (family-only) must survive too — an overlay that replaced the set would lose both");
        }

        [Test]
        public void Parse_WithFamilyNode_RowsReadCountsOnlyTheUnitsOwnRows()
        {
            var result = UnitAnimationParser.Parse(Zombie3Node, ZombieFamilyNode);

            Assert.AreEqual(1, result.RowsRead, "RowsRead counts the unit's own rows, not the family's");
            Assert.IsTrue(result.HasAnyState);
        }

        [Test]
        public void Parse_WithoutFamilyNode_KeepsOnlyOwnRows()
        {
            var result = UnitAnimationParser.Parse(Zombie3Node);

            Assert.AreEqual(1, result.RowsRead);
            Assert.AreEqual(18, result.Animations.preAttack.length);
            Assert.AreEqual(0, result.Animations.stay.length,
                "with no family pass there is no stay state — length stays at the default 0");
            Assert.AreEqual(0, result.RowsOverridingTemplate);
        }

        // ── the stepping rule ────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>setStab</c> writes <c>f = maxf * progress</c> and <b>ignores <c>firstf</c></b>
        /// (<c>BlitAnim.as:3741-3751</c>). That is an inconsistency in the source — <c>step()</c> keeps
        /// the cursor inside <c>[firstf, firstf + maxf - 1]</c> — but a port that quietly corrected it
        /// would draw a different cell from the game. The <c>ff='10'</c> here is the discriminator: an
        /// implementation that added <c>firstFrame</c> would return 12, not 2.
        /// </summary>
        [Test]
        public void FrameAtProgress_MirrorsSetStab_AndDoesNotAddFirstFrame()
        {
            var frame = new AnimationFrame { firstFrame = 10, length = 5 };

            Assert.AreEqual(2, frame.FrameAtProgress(0.5f),
                "setStab multiplies maxf and ignores firstf — 10 + (int)(5*0.5) = 12 would be a deviation");
            Assert.AreEqual(0, frame.FrameAtProgress(0f));
            Assert.AreEqual(4, frame.FrameAtProgress(0.999f), "the clamp is below 1 so the last cell is reachable");
            Assert.AreEqual(0, frame.FrameAtProgress(-1f), "negative progress clamps to 0");
            Assert.AreEqual(4, frame.FrameAtProgress(5f), "progress above 1 clamps to 0.999");
        }

        [Test]
        public void AnimationFrame_HasFrames_IsDrivenByLength()
        {
            Assert.IsTrue(new AnimationFrame { length = 1 }.HasFrames);
            Assert.IsFalse(new AnimationFrame().HasFrames, "the default frame names no cells");
        }

        // ── read-only Get (a regression, not a hypothetical) ──────────────────────────────────

        /// <summary>
        /// <see cref="AnimationSet.Get"/> must not mutate. An earlier revision wrote
        /// <c>TrySet(as3Id, default) ? GetMapped(as3Id) : default</c>, using the setter as a predicate —
        /// so every read blanked the state it was reading, and the overlay above would have destroyed
        /// each family row the moment it inspected it.
        /// </summary>
        [Test]
        public void AnimationSet_Get_IsReadOnly()
        {
            var set = new AnimationSet();
            set.TrySet("stay", new AnimationFrame { row = 3, length = 20, replay = true });

            var first = set.Get("stay");
            var second = set.Get("stay");

            Assert.AreEqual(20, first.length);
            Assert.AreEqual(20, second.length, "a second read must return the same state, not an empty one");
            Assert.AreEqual(20, set.stay.length, "reading must not have cleared the field");
            Assert.IsTrue(set.stay.replay, "reading must not have cleared the flags either");
        }

        [Test]
        public void AnimationSet_TrySet_RejectsUnknownIds_AndIsMappedAgrees()
        {
            var set = new AnimationSet();

            Assert.IsFalse(set.TrySet("nosuchstate", new AnimationFrame { row = 6, length = 8 }),
                "nosuchstate has no field, so TrySet must report the failure rather than swallow it");
            Assert.IsTrue(set.TrySet("walk", new AnimationFrame { length = 5 }));
            Assert.AreEqual(5, set.walk.length);

            // The other half, and the one that changed: `derg` HAS a field now (it is an authored row on
            // raider/slaver/zebra), so TrySet must accept it. This is the guard against a future edit
            // re-adding it to the unmapped list.
            Assert.IsTrue(set.TrySet("derg", new AnimationFrame { row = 6, length = 8 }));
            Assert.AreEqual(8, set.derg.length);
            Assert.AreEqual(6, set.derg.row);
        }

        // ── robustness ───────────────────────────────────────────────────────────────────────

        [Test]
        public void Parse_DoubleQuotedAttributes_AreAccepted()
        {
            // AllData.as is single-quoted throughout, but the room files are double-quoted and a
            // hand-written fixture or a future data file should not be read as empty.
            var result = UnitAnimationParser.Parse("<blit id=\"trot\" y=\"1\" len=\"17\" rep=\"1\"/>");

            Assert.AreEqual(1, result.Animations.trot.row);
            Assert.AreEqual(17, result.Animations.trot.length);
            Assert.IsTrue(result.Animations.trot.replay);
        }

        [Test]
        public void Parse_EmptyOrNull_IsEmpty_NotAThrow()
        {
            foreach (string input in new[] { null, "", "   " })
            {
                var result = UnitAnimationParser.Parse(input);
                Assert.AreEqual(0, result.RowsRead, $"input {input ?? "<null>"} should read no rows");
                Assert.IsFalse(result.HasAnyState);
            }
        }

        [Test]
        public void Parse_TextWithoutBlits_ReadsNothing()
        {
            // The 24 room-placed units with no blit rows — slime, turret, training — must produce an
            // empty set without error. This is the ABSENT control for the whole fixture: a parser that
            // always reported states would pass every test above and fail this one.
            var result = UnitAnimationParser.Parse(
                "<unit id='slime' fraction='0'><vis vclass='visualSlime'/><phis sX='40' sY='40'/></unit>");

            Assert.AreEqual(0, result.RowsRead);
            Assert.AreEqual(0, result.RowsWithoutId);
            Assert.IsFalse(result.HasAnyState, "a vclass unit has no sheet and no states — that is correct, not a failure");
        }

        /// <summary>
        /// The attribute matcher must anchor on a boundary. Without it <c>id</c> also matches inside
        /// <c>uid</c> — a substring hit that still parses, and therefore animates the wrong cells with
        /// nothing to show for it.
        /// </summary>
        [Test]
        public void Parse_DoesNotMatchAttributeNamesAsSubstrings()
        {
            // `uid` is not in any blit row today, but it IS used on <obj> rows, so it is one careless
            // copy-paste away from appearing in one. A pattern without a boundary reads `uid` as `id`
            // and the row silently animates the wrong cells.
            var result = UnitAnimationParser.Parse("<blit id='walk' uid='999' y='2' len='7'/>");

            Assert.AreEqual(7, result.Animations.walk.length, "the row must be keyed by id='walk'");
            CollectionAssert.AreEquivalent(new[] { "walk" }, result.SetIds,
                "only 'walk' was declared — if uid had been read as id, '999' would appear here");
            Assert.AreEqual(0, result.UnmappedIds.Count, "no bogus id should have been reported either");
            Assert.AreEqual(2, result.Animations.walk.row);
        }
    }
}
#endif
