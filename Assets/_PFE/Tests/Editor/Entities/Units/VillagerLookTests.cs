using NUnit.Framework;
using PFE.Data.Definitions.Campaign;
using PFE.Entities.Units;
using UnityEngine;

namespace PFE.Tests.Editor.Entities.Units
{
    /// <summary>
    /// Pins the villager pony's look selection (<c>UnitPonPon.as:112</c>) and the anchor that goes with
    /// it, because both are numbers copied out of the SWF and neither is visible when it drifts.
    ///
    /// <para><b>What was reported.</b> "NPCs are a bit sunk into the ground" and "there should be more
    /// diversity in npc — for now it's just one mare". Two separate causes, both measured:</para>
    ///
    /// <list type="number">
    /// <item><description><c>ponpon</c>'s <c>&lt;vis&gt;</c> is <c>visualPonPon</c>, whose timeline is
    /// <b>one</b> frame placing <c>osn</c> → <c>pon</c>. <c>pon</c> is the real art — 27 frames, one per
    /// villager — and the placement's <c>tr</c> picks one. Exporting only the outer symbol drew frame 1
    /// everywhere: one mare.</description></item>
    /// <item><description>The unit's origin sits <b>4.67 px</b> above a pony frame's bottom edge, not the
    /// bitmap rule's flat <b>10</b> (<c>Unit.as:2844-2859</c> describes a cell blitted out of a sheet; a
    /// DisplayObject is placed by <c>vis.y = Y</c>, <c>Unit.as:2969-2977</c>). Drawing at 10 sank every
    /// villager 5.3 px into the floor.</description></item>
    /// </list>
    ///
    /// <para><b>Why nothing here touches a <see cref="Sprite"/> or a <c>ScriptableObject</c>.</b> Both are
    /// native calls that throw <c>ECall</c> outside the editor, which would make this whole file
    /// editor-only — and a rule that is only tested when someone opens Unity is a rule that is not
    /// tested. The selection rule is therefore split into <see cref="PonPonVariants.ResolveIndex"/>
    /// (integers only) and the anchor into <c>Vector2</c> arithmetic, and the fixture drives those. What
    /// is left in <see cref="PonPonVariants.TryResolve"/> is the array index and the scale constants,
    /// which the two assertions at the end cover through the pure half.</para>
    ///
    /// <para><b>Why the negative controls matter here.</b> A test that only asserts "the look is not
    /// always frame 1" passes on a broken selector that returns frame 2 for everything; and a test that
    /// only asserts "the gap is 4.67" passes on a hardcoded constant that ignores the sprite it is
    /// given. So each rule below is paired with the case that must fail it.</para>
    /// </summary>
    [TestFixture]
    public class VillagerLookTests
    {
        // ── 1. `tr` → frame ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The shipped room's own values. <c>RoomsCamp.as:34,118-125</c> authors
        /// <c>tr="20" tr="22" tr="16" tr="14" tr="17" tr="15" tr="13" tr="21" tr="27"</c> across the
        /// nine camp ponies, and every one of them must land on its own frame — that is the whole
        /// diversity fix.
        /// </summary>
        [TestCase("13", 13)]
        [TestCase("14", 14)]
        [TestCase("15", 15)]
        [TestCase("16", 16)]
        [TestCase("17", 17)]
        [TestCase("20", 20)]
        [TestCase("21", 21)]
        [TestCase("22", 22)]
        [TestCase("27", 27)]
        [TestCase("1", 1)]
        public void ResolveFrame_ReturnsThePlacementsOwnTr(string tr, int expected)
        {
            Assert.AreEqual(expected, PonPonVariants.ResolveFrame(tr));
        }

        /// <summary>
        /// <c>gotoAndStop(n)</c> on a 27-frame clip is defined for 1..27 and undefined outside it, so the
        /// port clamps rather than letting the array index escape. <c>0</c> and a negative are the cases
        /// a real room can author by mistake; a number above the clip is the case a re-export creates.
        /// </summary>
        [TestCase("0", 1)]
        [TestCase("-5", 1)]
        [TestCase("28", 27)]
        [TestCase("999", 27)]
        public void ResolveFrame_ClampsIntoTheClip(string tr, int expected)
        {
            Assert.AreEqual(expected, PonPonVariants.ResolveFrame(tr));
        }

        /// <summary>
        /// A missing or unparseable <c>tr</c> is frame 1, <b>not</b> a roll. The oracle rolls
        /// <c>floor(random*15+13)</c> in that case (<c>UnitPonPon.as:59</c>), but the roll shares the
        /// spawn stream with the base unit's facing coin flip, so adding it here would shift every later
        /// placement in every room — a whole-map change for a cosmetic gain. Every shipped placement
        /// authors a <c>tr</c>, so this is the "the room named nothing" answer only.
        /// </summary>
        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("abc")]
        [TestCase("13px")]
        public void ResolveFrame_FallsBackToTheFirstVariant_WithoutRolling(string tr)
        {
            Assert.AreEqual(PonPonVariants.FirstVariant, PonPonVariants.ResolveFrame(tr));
        }

        // ── 2. frame number → array slot ─────────────────────────────────────────────────────────

        /// <summary>
        /// <c>gotoAndStop(tr)</c> is an array index, so <c>tr</c> and the slot are one apart. An
        /// off-by-one here is invisible in a screenshot of nine ponies — it just swaps two villagers —
        /// which is exactly why it is pinned per value rather than by a single spot check.
        /// </summary>
        [TestCase("1", 0)]
        [TestCase("13", 12)]
        [TestCase("20", 19)]
        [TestCase("27", 26)]
        public void ResolveIndex_IsTheFrameNumberMinusOne(string tr, int expected)
        {
            Assert.AreEqual(expected, PonPonVariants.ResolveIndex(tr, PonPonVariants.VariantCount));
        }

        /// <summary>
        /// A short export — fewer than 27 frames — must answer "no such look" rather than index past the
        /// end. The importer warns about the count; the room still has to draw.
        /// </summary>
        [Test]
        public void ResolveIndex_RefusesASlotTheClipDoesNotHave()
        {
            Assert.AreEqual(2, PonPonVariants.ResolveIndex("3", 3), "tr=3 is inside a 3-frame export");
            Assert.AreEqual(-1, PonPonVariants.ResolveIndex("20", 3),
                            "tr=20 is past the end of a 3-frame export and must not throw");
            Assert.AreEqual(-1, PonPonVariants.ResolveIndex("1", 0), "an empty clip has no slot at all");
        }

        /// <summary>
        /// The same rule from the other side: whatever <c>tr</c> a room authors, the slot must be inside
        /// the array the importer is going to fill. This is the assertion that would have caught the
        /// original defect — with one imported frame, only <c>tr="1"</c> is satisfiable, which is the
        /// "it is just one mare" symptom stated as arithmetic.
        /// </summary>
        [Test]
        public void WithOnlyOneImportedFrame_EveryRealTrIsRefused()
        {
            foreach (string tr in new[] { "13", "14", "15", "16", "17", "20", "21", "22", "27" })
            {
                Assert.AreEqual(-1, PonPonVariants.ResolveIndex(tr, 1),
                                $"tr={tr} cannot be served by a 1-frame export — this is the old bug");
            }

            Assert.AreEqual(PonPonVariants.ResolveFrame("13") - PonPonVariants.FirstVariant,
                            PonPonVariants.ResolveIndex("13", PonPonVariants.VariantCount),
                            "the positive control: with all 27 frames, tr=13 resolves to slot 12");
        }

        /// <summary>
        /// The constants the caller draws with. They are the <c>osn</c> → <c>pon</c> placement matrix,
        /// read out of the SWF; a change in either is a change in how big and how low every villager is
        /// drawn, so they are asserted rather than trusted.
        /// </summary>
        [Test]
        public void TheVariantConstantsAreTheOracleMatrix()
        {
            Assert.AreEqual(27, PonPonVariants.VariantCount);
            Assert.AreEqual(1, PonPonVariants.FirstVariant);
            Assert.AreEqual(0.54629517f, PonPonVariants.Scale.x, 1e-6f, "osn's matrix scaleX");
            Assert.AreEqual(0.54663086f, PonPonVariants.Scale.y, 1e-6f, "osn's matrix scaleY");
            Assert.AreEqual(8.54f, PonPonVariants.FeetGapPixels, 0.01f,
                            "pon's origin above its frame's bottom, in source pixels");
        }

        // ── 3. the anchor ───────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The "sunk into the ground" fix, as arithmetic. A <c>pon</c> frame is 183×180 and its origin
        /// sits 8.54 source px above the bottom edge; drawn at 0.5463 that is <b>4.67</b> rendered px.
        ///
        /// <para>The assertion is written as a <i>round trip</i> — take the offset the helper returns and
        /// ask what gap it implies — so it cannot be satisfied by a constant that ignores the sprite it
        /// is handed. The companion assertion that it is <b>not</b> the bitmap rule's 10 is the negative
        /// control: 10 is what the port drew before, and it is 5.3 px too low.</para>
        /// </summary>
        [Test]
        public void FeetGapAnchor_PutsTheOrigin467PixelsAboveTheFramesBottomEdge()
        {
            const float width = 183f;
            const float height = 180f;
            Vector2 scale = PonPonVariants.Scale;

            var pivot = new Vector2(width * 0.5f * Mathf.Abs(scale.x),
                                    height * 0.5f * Mathf.Abs(scale.y));
            var size = new Vector2(width * Mathf.Abs(scale.x), height * Mathf.Abs(scale.y));

            Vector2 offset = UnitSpriteAnchor.PixelOffsetForFeetGap(
                pivot, size, PonPonVariants.FeetGapPixels * Mathf.Abs(scale.y));

            // The renderer's origin is at `offset`; the sprite hangs `pivot - offset` below it.
            float renderedGap = pivot.y - offset.y;

            Assert.AreEqual(4.67f, renderedGap, 0.05f,
                            "a pony's origin is 8.54 source px above its frame's bottom, 4.67 rendered");
            Assert.Greater(Mathf.Abs(renderedGap - UnitSpriteAnchor.DefaultFeetGapPixels), 5f,
                           "10 is the sheet-blit rule (Unit.as:2844) and sank every villager 5.3 px");

            // Horizontal: a centre pivot is the only anchor a DisplayObject gets without its own sprDX.
            Assert.AreEqual(0f, offset.x, 0.001f, "a centred frame must not be nudged sideways");
        }

        /// <summary>
        /// The same rule through the measured table, for the two families that were wrong in opposite
        /// directions: the composite pony frame (drawn <b>too low</b> at 10) and the shopkeepers' single
        /// frames (drawn <b>too high</b>, 18.28 against 10).
        ///
        /// <para><b>The two boss rows are the ones that were not merely a few pixels out.</b>
        /// <c>visualNecrBoss</c> is a 435 px canvas holding 96x101 of art with 212 px of transparent
        /// padding underneath, so the bitmap rule's <c>pivot - 10</c> put the art <b>202 px</b> — five
        /// tiles — above a collider that was standing on the floor. Its entry is solved against a
        /// 255.3 px child and the PNG is the outer symbol at zoom 1.704, which is why the lookup
        /// rescales rather than comparing sizes.</para>
        /// </summary>
        [TestCase("visualPonPon", 100f, 98f, 4.67f)]
        [TestCase("visualVendorWeapon", 244f, 336f, 18.28f)]
        [TestCase("visualDoctor", 227f, 317f, 18.28f)]
        [TestCase("visualVendorMech", 81f, 89f, 3.41f)]
        [TestCase("visualAutodoc", 101f, 113f, 0f)]
        [TestCase("visualNecrBoss", 435f, 435f, 214.97f)]
        [TestCase("visualRaiderBoss2", 177f, 166f, 18.28f)]
        public void VisualSpriteOrigins_CarriesTheMeasuredFeetGap(string visual, float width,
                                                                 float height, float expected)
        {
            Assert.IsTrue(VisualSpriteOrigins.TryGetFeetGap(visual, width, height, out float gap),
                          $"{visual} at {width}x{height} must have a solved anchor");
            Assert.AreEqual(expected, gap, 0.05f);
        }

        /// <summary>
        /// A frame exported at another zoom is the <b>same crop</b>, so the anchor scales with it rather
        /// than being refused.
        ///
        /// <para>This is the defect that made the Centurion zebra's necromancer float: her PNG is
        /// <c>visualNecrBoss</c> at 435 px while the table was solved against the 255.3 px child, the
        /// old size guard rejected the entry outright, and the fallback drew her by the bitmap rule.
        /// A guard that refuses a rescaled frame does not protect the unit — it hands it to a rule that
        /// does not describe it.</para>
        /// </summary>
        [Test]
        public void VisualSpriteOrigins_RescalesAnEntryForAFrameExportedAtAnotherZoom()
        {
            Assert.IsTrue(VisualSpriteOrigins.TryGetFeetGap("visualPonPon", 200f, 196f, out float doubled),
                          "double the canvas is the same crop at twice the zoom");
            Assert.AreEqual(4.67f * (196f / 98.8f), doubled, 0.05f);

            // The same name at the size the entry was solved for must be unchanged by the rescale —
            // the guard that makes this a scale and not a fudge factor.
            Assert.IsTrue(VisualSpriteOrigins.TryGetFeetGap("visualPonPon", 100f, 98f, out float exact));
            Assert.AreEqual(4.67f * (98f / 98.8f), exact, 0.05f);
        }

        /// <summary>
        /// The guard that replaced the size test, and the negative control for the two above: a frame
        /// whose <b>aspect</b> disagrees is a different crop, not the same one at another zoom, and the
        /// anchor solved for one crop does not describe it.
        /// </summary>
        [Test]
        public void VisualSpriteOrigins_RefusesAFrameWhoseCropChanged()
        {
            Assert.IsFalse(VisualSpriteOrigins.TryGetFeetGap("visualPonPon", 100f, 50f, out float gap),
                           "half the height at the same width is a different crop");
            Assert.AreEqual(0f, gap);

            Assert.IsFalse(VisualSpriteOrigins.TryGetFeetGap("noSuchVisual", 100f, 98f, out _),
                           "an unknown visual must answer false, not a default");
        }
    }
}
