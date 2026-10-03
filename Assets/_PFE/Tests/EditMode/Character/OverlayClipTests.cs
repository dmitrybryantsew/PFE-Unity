using NUnit.Framework;
using PFE.Character.Animation;
using PFE.Data.Definitions;

namespace PFE.Tests.EditMode.Character
{
    /// <summary>
    /// The overlay playhead — the rules behind <c>vis.shit</c> and the other sibling clips on the
    /// character's visual container.
    ///
    /// <para><b>Why these are offline.</b> The overlay's <i>renderer</i> cannot run in an offline host:
    /// assigning a <c>SpriteRenderer</c> reaches a Unity <c>ECall</c>, and the JIT refuses any method
    /// whose IL mentions one. That is exactly why <see cref="OverlayClip"/> holds the decision and the
    /// assembler holds only the assignment — so the interesting half is pinned here and the half that
    /// cannot be tested is as thin as it can be made.</para>
    /// </summary>
    [TestFixture]
    public class OverlayClipTests
    {
        // The shield, as imported: 20 frames, holding on the last one (visShit.as:12 declares
        // stop() on frames 1 and 20, and the export is 20 PNGs).
        const int ShieldFrames = 20;

        // ── RestartFrame ────────────────────────────────────────────────────────────────────────

        [Test]
        public void RestartFrame_IsZero_BecauseTheOracleNamesFrameOne()
        {
            Assert.AreEqual(0, OverlayClip.RestartFrame(ShieldFrames),
                "gotoAndPlay(1) / gotoAndStop(1) both name frame 1, i.e. index 0");

            // The absent case: an overlay whose frames were never imported must not read as "draw
            // frame 0", or every character renders sprite 0 of an empty array.
            Assert.AreEqual(OverlayClip.NoFrame, OverlayClip.RestartFrame(0));
            Assert.AreEqual(OverlayClip.NoFrame, OverlayClip.RestartFrame(-3));
        }

        // ── ClampFrame ──────────────────────────────────────────────────────────────────────────

        [Test]
        public void ClampFrame_PullsAPlayheadIntoRange_AndReportsNoFrameWhenThereIsNothingToDraw()
        {
            Assert.AreEqual(0, OverlayClip.ClampFrame(-5, ShieldFrames));
            Assert.AreEqual(19, OverlayClip.ClampFrame(25, ShieldFrames));
            Assert.AreEqual(19, OverlayClip.ClampFrame(19, ShieldFrames), "the last frame is in range");
            Assert.AreEqual(OverlayClip.NoFrame, OverlayClip.ClampFrame(0, 0));
        }

        // ── NextFrame ───────────────────────────────────────────────────────────────────────────

        [Test]
        public void NextFrame_ClampForever_AdvancesThenHoldsOnTheLastFrame()
        {
            Assert.AreEqual(1, OverlayClip.NextFrame(0, ShieldFrames, AnimationLoopMode.ClampForever));
            Assert.AreEqual(19, OverlayClip.NextFrame(18, ShieldFrames, AnimationLoopMode.ClampForever));
            Assert.AreEqual(19, OverlayClip.NextFrame(19, ShieldFrames, AnimationLoopMode.ClampForever),
                "frame 20 holds — visShit's frame script stops there");
        }

        [Test]
        public void NextFrame_Loop_WrapsToTheFirstFrame()
        {
            Assert.AreEqual(1, OverlayClip.NextFrame(0, ShieldFrames, AnimationLoopMode.Loop));
            Assert.AreEqual(0, OverlayClip.NextFrame(19, ShieldFrames, AnimationLoopMode.Loop));
        }

        [Test]
        public void NextFrame_LoopRange_WrapsInsideTheSubRange_NotToFrameZero()
        {
            // start 5, end 10 (exclusive) — the playhead must return to 5, not to 0.
            Assert.AreEqual(6, OverlayClip.NextFrame(5, ShieldFrames, AnimationLoopMode.LoopRange, 5, 10));
            Assert.AreEqual(5, OverlayClip.NextFrame(9, ShieldFrames, AnimationLoopMode.LoopRange, 5, 10));
        }

        [Test]
        public void NextFrame_LoopRange_WithADegenerateRange_HoldsInsteadOfSpinning()
        {
            // end == start + 1 would make `next >= end` true immediately and re-enter the same frame
            // forever; end < start would do the same. Both must collapse to the start frame.
            Assert.AreEqual(5, OverlayClip.NextFrame(5, ShieldFrames, AnimationLoopMode.LoopRange, 5, 6));
            Assert.AreEqual(5, OverlayClip.NextFrame(5, ShieldFrames, AnimationLoopMode.LoopRange, 5, 5));
            Assert.AreEqual(5, OverlayClip.NextFrame(5, ShieldFrames, AnimationLoopMode.LoopRange, 5, 2));
        }

        [Test]
        public void NextFrame_OnAnEmptyClip_IsNoFrame_NotAMinusOneIndex()
        {
            Assert.AreEqual(OverlayClip.NoFrame,
                OverlayClip.NextFrame(0, 0, AnimationLoopMode.Loop));
            Assert.AreEqual(OverlayClip.NoFrame,
                OverlayClip.NextFrame(0, 0, AnimationLoopMode.ClampForever));
        }

        // ── ShouldDraw ──────────────────────────────────────────────────────────────────────────

        [Test]
        public void ShouldDraw_NeedsBothTheGateAndFrames()
        {
            Assert.IsTrue(OverlayClip.ShouldDraw(gate: true, frameCount: ShieldFrames));
            Assert.IsFalse(OverlayClip.ShouldDraw(gate: false, frameCount: ShieldFrames),
                "a downed shield does not draw");
            Assert.IsFalse(OverlayClip.ShouldDraw(gate: true, frameCount: 0),
                "an un-imported overlay does not draw even while its gate is on");
            Assert.IsFalse(OverlayClip.ShouldDraw(gate: false, frameCount: 0));
        }

        // ── The whole cycle ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The bug this whole class exists to prevent: a shield that pops into existence at full
        /// brightness on its second rise, because the fall edge left the playhead on the last frame.
        /// </summary>
        [Test]
        public void ARaisedDroppedAndRaisedShield_MaterialisesAgain_FromTheFirstFrame()
        {
            int playhead = OverlayClip.NoFrame;   // hidden, nothing drawn

            // ── Rise: shithp goes positive, AS3 runs gotoAndPlay(1).
            playhead = OverlayClip.RestartFrame(ShieldFrames);
            Assert.AreEqual(0, playhead, "the rise starts on the faint first frame");

            // ── Play out the clip. 20 frames means 19 advances to reach the last.
            for (int i = 0; i < 19; i++)
            {
                playhead = OverlayClip.NextFrame(playhead, ShieldFrames, AnimationLoopMode.ClampForever);
            }

            Assert.AreEqual(19, playhead, "the materialise animation has finished");
            Assert.AreEqual(19, OverlayClip.NextFrame(playhead, ShieldFrames, AnimationLoopMode.ClampForever),
                "and it stays finished while the shield holds");

            // ── Fall: shithp hits zero, AS3 runs gotoAndStop(1) — the playhead is written, not parked.
            playhead = OverlayClip.RestartFrame(ShieldFrames);
            Assert.AreEqual(0, playhead,
                "gotoAndStop(1) parks the clip on frame 1 — if the fall edge did not write the " +
                "playhead, the second rise below would show frame 20 straight away");

            // ── Rise again: the materialise animation replays from the faint frame.
            playhead = OverlayClip.RestartFrame(ShieldFrames);
            Assert.AreEqual(0, playhead, "the second shield materialises from the start, not at full brightness");
        }
    }
}
