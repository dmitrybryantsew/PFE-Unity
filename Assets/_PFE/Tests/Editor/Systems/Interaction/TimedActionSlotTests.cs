using System.Collections.Generic;
using NUnit.Framework;
using PFE.Systems.Interaction;

namespace PFE.Tests.Editor.Systems.Interaction
{
    /// <summary>
    /// Tests for <see cref="TimedActionSlot{TPayload}"/> — the single-slot owner of the hold, and the
    /// port of AS3's <c>actionObj</c> / <c>t_action</c> / <c>mt_action</c> trio.
    ///
    /// <para>The rule with the highest consequence is the one the owner stated explicitly:
    /// <b>releasing the key cancels progress</b>. So the centrepiece here is that a hold cancelled
    /// part-way reports <c>Cancelled</c> and can never afterwards report <c>Completed</c> — a slot that
    /// leaked a completion after a release would open a door the player let go of.</para>
    /// </summary>
    [TestFixture]
    public class TimedActionSlotTests
    {
        /// <summary>
        /// Records every call so the tests can assert on the view contract, not just on the countdown.
        /// </summary>
        private sealed class RecordingProgressView : IActionProgressView
        {
            public readonly List<float> Shows = new List<float>();
            public int Hides;

            public void Show(float progress) => Shows.Add(progress);

            public void Hide() => Hides++;
        }

        private static TimedActionSlot<string> NewSlot(out RecordingProgressView view)
        {
            view = new RecordingProgressView();
            return new TimedActionSlot<string>(view);
        }

        [Test]
        public void TryBegin_WithZeroFrames_IsRefusedAsNotATimedAction()
        {
            // Not an error: it means the object has no hold, and AS3 fires those on the spot
            // (UnitPlayer.as:1985-1988). The caller is expected to have taken its instant path.
            var slot = NewSlot(out RecordingProgressView view);

            Assert.AreEqual(TimedActionBeginResult.NotATimedAction, slot.TryBegin(0, "door"));
            Assert.IsFalse(slot.IsActive);
            Assert.AreEqual(0, view.Shows.Count, "Nothing was started, so nothing should be shown.");
        }

        [Test]
        public void TryBegin_WithNegativeFrames_IsRefusedAsNotATimedAction()
        {
            var slot = NewSlot(out _);

            Assert.AreEqual(TimedActionBeginResult.NotATimedAction, slot.TryBegin(-3, "door"));
            Assert.IsFalse(slot.IsActive);
        }

        [Test]
        public void TryBegin_WhileAHoldIsInFlight_IsRefusedAsAlreadyActive()
        {
            // AS3 keeps exactly one actionObj; a second begin is a no-op there too.
            var slot = NewSlot(out _);
            Assert.AreEqual(TimedActionBeginResult.Begun, slot.TryBegin(10, "first"));

            Assert.AreEqual(TimedActionBeginResult.AlreadyActive, slot.TryBegin(10, "second"));
            Assert.AreEqual("first", slot.ActivePayload, "The incumbent must not be replaced.");
        }

        [Test]
        public void TryBegin_ShowsTheBarAtZeroImmediately()
        {
            // On the press, not one tick later: a bar that appears 33 ms after the press reads as a
            // dropped input.
            var slot = NewSlot(out RecordingProgressView view);

            slot.TryBegin(10, "door");

            Assert.AreEqual(1, view.Shows.Count);
            Assert.AreEqual(0f, view.Shows[0], 1e-6f);
        }

        [Test]
        public void Advance_WhenNothingIsInFlight_ReportsIdle()
        {
            var slot = NewSlot(out RecordingProgressView view);

            TimedActionTick<string> tick = slot.Advance();

            Assert.AreEqual(TimedActionTickResult.Idle, tick.Result);
            Assert.IsFalse(tick.IsTerminal);
            Assert.AreEqual(0, view.Shows.Count);
        }

        [Test]
        public void Advance_WhileCountingDown_ReportsRunningAndPushesProgress()
        {
            var slot = NewSlot(out RecordingProgressView view);
            slot.TryBegin(10, "door");
            view.Shows.Clear();

            TimedActionTick<string> tick = slot.Advance();

            Assert.AreEqual(TimedActionTickResult.Running, tick.Result);
            Assert.IsNull(tick.Payload, "A running tick hands out no payload.");
            Assert.AreEqual(1, view.Shows.Count);
            Assert.AreEqual(0.1f, view.Shows[0], 1e-6f, "One tick of ten is 10%.");
        }

        [Test]
        public void Advance_OnCompletion_HandsThePayloadOutExactlyOnce()
        {
            var slot = NewSlot(out _);
            slot.TryBegin(1, "the-door");

            Assert.AreEqual(TimedActionTickResult.Running, slot.Advance().Result);

            TimedActionTick<string> completion = slot.Advance();
            Assert.AreEqual(TimedActionTickResult.Completed, completion.Result);
            Assert.IsTrue(completion.IsTerminal);
            Assert.AreEqual("the-door", completion.Payload);

            // The second advance must not repeat the effect.
            TimedActionTick<string> after = slot.Advance();
            Assert.AreEqual(TimedActionTickResult.Idle, after.Result);
            Assert.IsNull(after.Payload);
        }

        [Test]
        public void Advance_OnCompletion_ShowsTheBarFullThenHidesIt()
        {
            // Shown full before hiding, so a scale-driven bar animates to 100% instead of vanishing
            // from wherever it happened to be.
            var slot = NewSlot(out RecordingProgressView view);
            slot.TryBegin(1, "door");
            slot.Advance();
            view.Shows.Clear();

            slot.Advance();

            Assert.AreEqual(1, view.Shows.Count);
            Assert.AreEqual(1f, view.Shows[0], 1e-6f);
            Assert.AreEqual(1, view.Hides);
        }

        [Test]
        public void Cancel_MidHold_ReportsCancelledAndNeverCompletes()
        {
            // THE rule the owner stated: releasing the key cancels progress. A slot that could still
            // report Completed after this would run the effect for an input the player abandoned.
            var slot = NewSlot(out RecordingProgressView view);
            slot.TryBegin(10, "the-door");

            for (int i = 0; i < 5; i++)
            {
                Assert.AreEqual(TimedActionTickResult.Running, slot.Advance().Result);
            }

            TimedActionTick<string> cancelled = slot.Cancel();
            Assert.AreEqual(TimedActionTickResult.Cancelled, cancelled.Result);
            Assert.IsTrue(cancelled.IsTerminal);
            Assert.AreEqual("the-door", cancelled.Payload, "The caller needs the target to unwind it.");
            Assert.IsFalse(slot.IsActive);

            // And it stays cancelled: no amount of further advancing can produce the effect.
            for (int i = 0; i < 20; i++)
            {
                Assert.AreEqual(
                    TimedActionTickResult.Idle,
                    slot.Advance().Result,
                    "A cancelled hold must never fire, however long the caller keeps ticking.");
            }
        }

        [Test]
        public void Cancel_HidesTheBar()
        {
            var slot = NewSlot(out RecordingProgressView view);
            slot.TryBegin(10, "door");
            slot.Advance();

            slot.Cancel();

            Assert.AreEqual(1, view.Hides);
        }

        [Test]
        public void Cancel_WhenNothingIsInFlight_ReportsIdleAndTouchesNoView()
        {
            // The natural call sites are "on key up" and "on disable", neither of which can know
            // whether a hold was live.
            var slot = NewSlot(out RecordingProgressView view);

            TimedActionTick<string> tick = slot.Cancel();

            Assert.AreEqual(TimedActionTickResult.Idle, tick.Result);
            Assert.IsNull(tick.Payload);
            Assert.AreEqual(0, view.Hides, "Hiding a bar that was never shown must not cost a UI write.");
        }

        [Test]
        public void ActivePayload_IsReadableWhileActiveAndClearedAfterTheTerminalTick()
        {
            // Readable while in flight is what lets a caller re-validate the target each tick; cleared
            // after is what stops a stale target being acted on.
            var slot = NewSlot(out _);
            slot.TryBegin(2, "the-door");
            Assert.AreEqual("the-door", slot.ActivePayload);

            slot.Advance();
            Assert.AreEqual("the-door", slot.ActivePayload, "Still in flight.");

            slot.Advance();
            Assert.AreEqual("the-door", slot.ActivePayload,
                "Still in flight on the second tick: a 2-frame hold completes on the THIRD advance.");

            // AS3 counts down and *then* fires — the completing tick is the one where the counter
            // already reads 0 — so a hold of N frames needs N+1 advances. TryBegin_AfterCompletion_ReArms
            // pins the same rule from the other side (a 1-frame hold completes on the second advance).
            slot.Advance(); // completes
            Assert.IsNull(slot.ActivePayload, "Handed out, so no longer held.");
        }

        [Test]
        public void ActivePayload_IsClearedAfterCancel()
        {
            var slot = NewSlot(out _);
            slot.TryBegin(10, "the-door");

            slot.Cancel();

            Assert.IsNull(slot.ActivePayload);
        }

        [Test]
        public void TryBegin_AfterCompletion_ReArms()
        {
            // Holding E on a door, letting it open, then holding again must work — AS3 re-arms
            // t_action from scratch on every fresh start.
            var slot = NewSlot(out _);
            slot.TryBegin(1, "first");
            slot.Advance();
            slot.Advance();

            Assert.AreEqual(TimedActionBeginResult.Begun, slot.TryBegin(3, "second"));
            Assert.IsTrue(slot.IsActive);
            Assert.AreEqual("second", slot.ActivePayload);
            Assert.AreEqual(3, slot.RemainingFrames);
        }

        [Test]
        public void Reset_ClearsTheHoldAndHidesTheBar()
        {
            var slot = NewSlot(out RecordingProgressView view);
            slot.TryBegin(10, "door");
            slot.Advance();

            slot.Reset();

            Assert.IsFalse(slot.IsActive);
            Assert.IsNull(slot.ActivePayload);
            Assert.AreEqual(1, view.Hides);
            Assert.AreEqual(TimedActionTickResult.Idle, slot.Advance().Result);
        }

        [Test]
        public void TenFrameHold_NeedsElevenAdvances_ThroughTheSlot()
        {
            // The off-by-one from ActionTimerTests, re-asserted at the layer the game actually uses:
            // a Z door's time='10' is eleven sim ticks, i.e. 0.367 s at AS3's 30 Hz.
            var slot = NewSlot(out _);
            slot.TryBegin(10, "indoor2");

            int ticks = 0;
            while (slot.IsActive && ticks < 50)
            {
                slot.Advance();
                ticks++;
            }

            Assert.AreEqual(11, ticks);
        }

        [Test]
        public void DefaultView_IsTheNullView_SoNoCallSiteNeedsANullCheck()
        {
            var slot = new TimedActionSlot<string>();

            Assert.DoesNotThrow(() => slot.TryBegin(5, "door"));
            Assert.DoesNotThrow(() => slot.Advance());
            Assert.DoesNotThrow(() => slot.Cancel());
        }
    }
}
