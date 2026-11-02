using NUnit.Framework;
using PFE.Character.Animation;
using PFE.Data.Definitions;
using PFE.Entities.Units;

namespace PFE.Tests.EditMode.Character
{
    /// <summary>
    /// Pins the playhead of the <b>boss alicorn's</b> shield dome — AS3 <c>UnitBossAlicorn</c>'s
    /// <c>visShit3</c>, the one dome in the game that is a one-shot break instead of a shield pool.
    ///
    /// <para><b>Why the fixture is not on the component.</b> <c>UnitShieldOverlay</c> assigns a
    /// <c>SpriteRenderer</c>, which is an ECall: outside the editor the JIT refuses the method that
    /// mentions it, so a fixture that touched the component could never run in this project's offline
    /// harness. The arithmetic therefore lives in <see cref="ShieldDomeOneShotRules"/> and this is the
    /// fixture that keeps it honest; the component is reduced to "call these three, then assign the
    /// sprite".</para>
    ///
    /// <para><b>The three oracle lines.</b> <c>gotoAndStop(1)</c> in the constructor
    /// (<c>:126</c>) — the dome is up from spawn, parked on frame 1. <c>gotoAndPlay(2)</c> in
    /// <c>die()</c> (<c>:712</c>) — the break starts on frame 2, skipping the intact pose. And nothing
    /// afterwards: the clip runs out and that is the end of the dome.</para>
    /// </summary>
    [TestFixture]
    public class ShieldDomeOneShotRulesTests
    {
        // ── Which dome is a one-shot ─────────────────────────────────────────────

        /// <summary>
        /// Only the boss clip. The tier-3 alicorn's <c>visShit2</c> is a <b>pool</b> dome like the
        /// ordinary one — keying this on "the big dome" would drive it with the wrong timeline and leave
        /// a tr3 alicorn's dome stuck after one break.
        /// </summary>
        [Test]
        public void IsOneShot_OnlyTheBossDome()
        {
            Assert.IsTrue(ShieldDomeOneShotRules.IsOneShot(UnitShieldOverlayKind.Boss));

            Assert.IsFalse(ShieldDomeOneShotRules.IsOneShot(UnitShieldOverlayKind.Normal),
                "visShit is pool-driven (UnitAlicorn.as:328-342 polls shithp)");
            Assert.IsFalse(ShieldDomeOneShotRules.IsOneShot(UnitShieldOverlayKind.Large),
                "visShit2 is the tr3 SIZE, not a different driver — UnitAlicorn polls shithp for it too");
        }

        // ── Intact frame ─────────────────────────────────────────────────────────

        [Test]
        public void IntactFrame_ParksOnTheFirstFrame()
        {
            Assert.AreEqual(0, ShieldDomeOneShotRules.IntactFrame(12), "gotoAndStop(1) is index 0");
            Assert.AreEqual(0, ShieldDomeOneShotRules.IntactFrame(1));

            Assert.AreEqual(OverlayClip.NoFrame, ShieldDomeOneShotRules.IntactFrame(0),
                "a clip with no frames has no pose to park on");
        }

        // ── Break start ──────────────────────────────────────────────────────────

        /// <summary>
        /// <c>gotoAndPlay(2)</c> is <b>index 1</b>, not index 0.
        /// </summary>
        /// <remarks>
        /// This is the whole reason the break has its own start frame: replaying frame 1 first would
        /// show the intact pose again for 1/30 s at the moment the player lands the killing blow — a
        /// hitch in the one animation that has to read as immediate.
        /// </remarks>
        [Test]
        public void BreakStartFrame_SkipsTheIntactPose()
        {
            Assert.AreEqual(1, ShieldDomeOneShotRules.BreakStartFrame(12));
            Assert.AreEqual(1, ShieldDomeOneShotRules.BreakStartFrame(2));

            Assert.AreEqual(0, ShieldDomeOneShotRules.BreakStartFrame(1),
                "a one-frame clip has nothing to skip — it plays its only frame rather than nothing");
            Assert.AreEqual(OverlayClip.NoFrame, ShieldDomeOneShotRules.BreakStartFrame(0));
        }

        // ── The break's timeline ─────────────────────────────────────────────────

        /// <summary>
        /// Every frame from the start to the last is drawn <b>exactly once</b>, and the step after the
        /// last ends the break.
        /// </summary>
        [Test]
        public void NextBreakFrame_DrawsEachFrameOnceThenStops()
        {
            const int frameCount = 12;

            var drawn = new System.Collections.Generic.List<int>();
            int frame = ShieldDomeOneShotRules.BreakStartFrame(frameCount);

            // The start frame itself is drawn by BreakOnce, so it counts as the first picture.
            drawn.Add(frame);

            for (int guard = 0; guard < 100; guard++)
            {
                int next = ShieldDomeOneShotRules.NextBreakFrame(frame, frameCount);
                if (next == OverlayClip.NoFrame) break;
                drawn.Add(next);
                frame = next;
            }

            CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 }, drawn,
                "frames 1..11 once each: index 0 is the intact pose and must not be replayed");

            Assert.AreEqual(OverlayClip.NoFrame,
                ShieldDomeOneShotRules.NextBreakFrame(11, frameCount),
                "the step after the last frame ends the break — a clamping rule here would hold the " +
                "dome on screen forever and the break would never finish");
        }

        /// <summary>
        /// <b>The same input, two different answers, on purpose.</b> The pool clip clamps and stays; the
        /// break runs out.
        /// </summary>
        /// <remarks>
        /// <c>visShit</c> is <c>ClampForever</c> — its last frame calls <c>stop()</c> and the dome is
        /// meant to remain materialised. Reusing <see cref="OverlayClip.NextFrame"/> for the break is
        /// therefore the single most likely implementation mistake, and it compiles: the dome would
        /// simply never disappear. This fixture is the one that names the difference.
        /// </remarks>
        [Test]
        public void NextBreakFrame_DoesNotClampLikeThePoolClip()
        {
            const int frameCount = 12;

            Assert.AreEqual(11, OverlayClip.NextFrame(11, frameCount, AnimationLoopMode.ClampForever),
                "the pool dome holds its last frame — that is correct for visShit");

            Assert.AreEqual(OverlayClip.NoFrame,
                ShieldDomeOneShotRules.NextBreakFrame(11, frameCount),
                "the break must END — visShit3 is shattered, not materialised");
        }

        [Test]
        public void NextBreakFrame_WithOneFrameClip_EndsOnTheFirstStep()
        {
            Assert.AreEqual(OverlayClip.NoFrame, ShieldDomeOneShotRules.NextBreakFrame(0, 1),
                "a placeholder import still shows one frame and then clears, rather than dividing by " +
                "zero frames or hanging");

            Assert.AreEqual(OverlayClip.NoFrame, ShieldDomeOneShotRules.NextBreakFrame(0, 0));
        }

        /// <summary>
        /// An out-of-range playhead is pulled in before the step, so a definition edited in the
        /// inspector cannot index past its own frames.
        /// </summary>
        [Test]
        public void NextBreakFrame_ClampsAnOutOfRangePlayheadFirst()
        {
            Assert.AreEqual(OverlayClip.NoFrame, ShieldDomeOneShotRules.NextBreakFrame(99, 12),
                "clamped to 11, stepped to 12, past the end — not an index error");
        }
    }
}
