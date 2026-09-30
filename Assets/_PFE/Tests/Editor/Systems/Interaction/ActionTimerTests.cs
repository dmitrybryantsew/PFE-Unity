using NUnit.Framework;
using PFE.Core;

namespace PFE.Tests.Editor.Systems.Interaction
{
    /// <summary>
    /// Tests for <see cref="ActionTimer"/> — the frame countdown behind every hold action.
    ///
    /// <para>The two things worth pinning hardest are the ones a "tidier" implementation would get
    /// wrong: the <b>off-by-one</b> that makes a <c>time='10'</c> hold fire on the eleventh tick, and
    /// the fact that <see cref="ActionTimer.Progress"/> reads 1 one tick <i>before</i> the effect
    /// lands. Both are AS3's behaviour, not accidents, so both are asserted explicitly rather than
    /// left to a range check.</para>
    /// </summary>
    [TestFixture]
    public class ActionTimerTests
    {
        private const float Tolerance = 1e-6f;

        [Test]
        public void Start_WithAPositiveCount_BeginsCountingDown()
        {
            var timer = new ActionTimer();

            Assert.AreEqual(ActionTimerState.Running, timer.Start(10));
            Assert.IsTrue(timer.IsRunning);
            Assert.AreEqual(10, timer.TotalFrames);
            Assert.AreEqual(10, timer.RemainingFrames);
            Assert.AreEqual(0f, timer.Progress, Tolerance, "A hold starts empty.");
        }

        [Test]
        public void Start_WithZeroFrames_IsAlreadyComplete()
        {
            // AS3's zero branch fires is_act directly and never arms a timer (UnitPlayer.as:1985-1988),
            // so a zero-frame action is not a zero-length hold — it is an action with no hold at all.
            var timer = new ActionTimer();

            Assert.AreEqual(ActionTimerState.Completed, timer.Start(0));
            Assert.IsFalse(timer.IsRunning);
            Assert.AreEqual(1f, timer.Progress, Tolerance);
        }

        [Test]
        public void Start_WithNegativeFrames_IsAlreadyComplete()
        {
            var timer = new ActionTimer();

            Assert.AreEqual(ActionTimerState.Completed, timer.Start(-5));
            Assert.IsFalse(timer.IsRunning);
        }

        [Test]
        public void Advance_TenFrameHold_CompletesOnTheEleventhTick()
        {
            var timer = new ActionTimer();
            timer.Start(10);

            // AS3's guard is `if (t_action > 0) --t_action; else fire` (UnitPlayer.as:1077-1085): the
            // completing tick is the one where the counter ALREADY reads zero, not the one that brings
            // it there. So an authored ten is ten decrements plus the firing tick.
            for (int tick = 1; tick <= 10; tick++)
            {
                Assert.AreEqual(
                    ActionTimerState.Running,
                    timer.Advance(),
                    $"Tick {tick} should still be counting down (Remaining={timer.RemainingFrames}).");
            }

            Assert.AreEqual(0, timer.RemainingFrames, "Ten decrements leave the counter at zero.");
            Assert.AreEqual(
                1f,
                timer.Progress,
                Tolerance,
                "The bar is full at Remaining == 0 — one tick before the effect lands. That is AS3's " +
                "(mt_action - t_action) / mt_action, not a rounding artefact.");

            Assert.AreEqual(
                ActionTimerState.Completed,
                timer.Advance(),
                "The eleventh tick is the one that fires.");
        }

        [Test]
        public void Advance_OneFrameHold_TakesTwoTicks()
        {
            // The same off-by-one at the smallest scale, where it is least deniable.
            var timer = new ActionTimer();
            timer.Start(1);

            Assert.AreEqual(ActionTimerState.Running, timer.Advance());
            Assert.AreEqual(0, timer.RemainingFrames);
            Assert.AreEqual(ActionTimerState.Completed, timer.Advance());
        }

        [Test]
        public void Progress_MatchesTheAs3FormulaAtEveryStep()
        {
            // GUI.as:1288 computes perc = (mt_action - t_action) / mt_action for the hold bar.
            const int total = 30;
            var timer = new ActionTimer();
            timer.Start(total);

            for (int elapsed = 0; elapsed <= total; elapsed++)
            {
                Assert.AreEqual(
                    elapsed / (float)total,
                    timer.Progress,
                    Tolerance,
                    $"After {elapsed} tick(s) of {total}.");

                if (elapsed < total)
                {
                    timer.Advance();
                }
            }
        }

        [Test]
        public void Advance_OnAnIdleTimer_DoesNothing()
        {
            // A caller that advances every tick must not have to ask first.
            var timer = new ActionTimer();

            Assert.AreEqual(ActionTimerState.Idle, timer.Advance());
            Assert.AreEqual(0f, timer.Progress, Tolerance);
            Assert.IsFalse(timer.IsRunning);
        }

        [Test]
        public void Advance_AfterCompletion_StaysCompleted()
        {
            var timer = new ActionTimer();
            timer.Start(1);
            timer.Advance();
            timer.Advance();

            Assert.AreEqual(ActionTimerState.Completed, timer.State);
            Assert.AreEqual(ActionTimerState.Completed, timer.Advance());
            Assert.AreEqual(ActionTimerState.Completed, timer.Advance());
            Assert.AreEqual(1f, timer.Progress, Tolerance);
        }

        [Test]
        public void Cancel_MidHold_LeavesNothingInFlight()
        {
            var timer = new ActionTimer();
            timer.Start(10);
            timer.Advance();
            timer.Advance();
            timer.Advance();

            Assert.AreEqual(ActionTimerState.Cancelled, timer.Cancel());
            Assert.IsFalse(timer.IsRunning);
            Assert.AreEqual(0, timer.RemainingFrames);
            Assert.AreEqual(0f, timer.Progress, Tolerance);
        }

        [Test]
        public void Cancel_AfterCompletion_DoesNotUnfireTheAction()
        {
            // The effect has already landed by then, so a late cancel must not pretend it did not.
            var timer = new ActionTimer();
            timer.Start(1);
            timer.Advance();
            timer.Advance();

            Assert.AreEqual(ActionTimerState.Completed, timer.Cancel());
            Assert.AreEqual(1f, timer.Progress, Tolerance);
        }

        [Test]
        public void Cancel_OnAnIdleTimer_DoesNothing()
        {
            var timer = new ActionTimer();

            Assert.AreEqual(ActionTimerState.Idle, timer.Cancel());
            Assert.AreEqual(ActionTimerState.Idle, timer.State);
        }

        [Test]
        public void Cancel_ThenStart_ReArms()
        {
            var timer = new ActionTimer();
            timer.Start(10);
            timer.Cancel();

            Assert.AreEqual(ActionTimerState.Running, timer.Start(10));
            Assert.AreEqual(10, timer.RemainingFrames);
            Assert.AreEqual(0f, timer.Progress, Tolerance);
        }

        [Test]
        public void Start_AfterCompletion_ReArms()
        {
            var timer = new ActionTimer();
            timer.Start(1);
            timer.Advance();
            timer.Advance();

            Assert.AreEqual(ActionTimerState.Running, timer.Start(4));
            Assert.AreEqual(4, timer.TotalFrames);
            Assert.AreEqual(4, timer.RemainingFrames);
        }

        [Test]
        public void Reset_ReturnsToIdle()
        {
            var timer = new ActionTimer();
            timer.Start(10);
            timer.Advance();

            timer.Reset();

            Assert.AreEqual(ActionTimerState.Idle, timer.State);
            Assert.AreEqual(0, timer.TotalFrames);
            Assert.AreEqual(0, timer.RemainingFrames);
        }

        [Test]
        public void TotalFrames_IsKeptAsTheProgressDenominator()
        {
            // mt_action is clamped to at least 1 in AS3 (UnitPlayer.as:1991-1993) so the bar can never
            // divide by zero; TotalFrames carries the same guarantee by construction.
            var timer = new ActionTimer();
            timer.Start(45);

            timer.Advance();
            timer.Advance();

            Assert.AreEqual(45, timer.TotalFrames, "The denominator must not shrink as the hold runs.");
            Assert.AreEqual(43, timer.RemainingFrames);
        }
    }
}
