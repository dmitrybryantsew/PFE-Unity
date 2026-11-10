using NUnit.Framework;
using PFE.Core.Input;

namespace PFE.Tests.Editor.Core
{
    /// <summary>
    /// Regression fixture for the 2026-11-06 "Space no longer always jumps" defect, and for the
    /// per-tick command tape that fixes its replay half.
    ///
    /// <para><b>The shape of the bug.</b> <c>PlayerController.Update()</c> samples input every render
    /// frame while <c>SimLoop</c> ticks at 30 Hz, so several frames share one <c>commandTick</c>. The
    /// Phase 2.3 path enqueued a command per frame and consumed it through
    /// <see cref="PlayerCommandBuffer.TryGetForTick"/>, which returns the <b>oldest</b> match. A jump
    /// edge produced on the second frame of a tick therefore read back as the first frame's
    /// <c>jumpPressed=false</c>: <c>SetIntent</c> never armed <c>_jumpBufferTimer</c> and
    /// <c>TryConsumeJump</c> bailed out at its <c>&lt;= 0f</c> guard. Offline reproduction against
    /// the real command sources: press on frame 2 of a tick jumped, press on frame 3 did not.</para>
    ///
    /// <para><b>What is pinned here.</b> The tape must hold exactly one command per tick, and the
    /// tick-aligned consumer must see every edge produced during that tick — including one raised on
    /// the LAST frame. The pre-fix shape is reconstructed explicitly in
    /// <see cref="PreFixShape_EnqueuePerFrame_LosesThePress_AndDuplicatesTheTick"/> so the green
    /// fixtures are known not to be vacuous.</para>
    /// </summary>
    [TestFixture]
    public sealed class PlayerCommandTapeTests
    {
        private const int Tick = 7;
        private const int Capacity = 16;

        private static PlayerCommand Frame(
            int tick,
            uint sequence,
            float moveX = 0f,
            bool jumpHeld = false,
            bool runHeld = false,
            bool jumpPressed = false,
            bool dashPressed = false,
            bool dropThroughPressed = false,
            float aimAngle = 0f)
        {
            return PlayerCommand.FromIntent(
                tickIndex: tick,
                slotIndex: 0,
                sequence: sequence,
                moveInputX: moveX,
                moveInputY: 0f,
                jumpHeld: jumpHeld,
                runHeld: runHeld,
                jumpPressed: jumpPressed,
                dashPressed: dashPressed,
                dropThroughPressed: dropThroughPressed,
                aimAngle: aimAngle);
        }

        // ── Pure fold ─────────────────────────────────────────────────────────

        [Test]
        public void Fold_OrsAllEdges_AndTakesLevelsFromTheNewestFrame()
        {
            PlayerCommand first = Frame(Tick, 1u, -1f,
                jumpHeld: true, runHeld: true, jumpPressed: true, dashPressed: true, aimAngle: 10f);
            PlayerCommand second = Frame(Tick, 2u, 1f,
                jumpHeld: false, runHeld: false, dropThroughPressed: true, aimAngle: 90f);

            PlayerCommand merged = PlayerCommandTape.Fold(first, second);

            Assert.That(merged.JumpPressed, Is.True, "edge from the FIRST frame must survive");
            Assert.That(merged.DashPressed, Is.True, "edge from the FIRST frame must survive");
            Assert.That(merged.DropThroughPressed, Is.True, "edge from the SECOND frame must survive");
            Assert.That(merged.JumpHeld, Is.False, "levels come from the newest frame");
            Assert.That(merged.RunHeld, Is.False, "levels come from the newest frame");
            Assert.That(merged.MoveX, Is.EqualTo(PlayerCommand.Quantize(1f)),
                "analogue intent comes from the newest frame");
            Assert.That(merged.AimAngle, Is.EqualTo(90f).Within(0.001f));
            Assert.That(merged.TickIndex, Is.EqualTo(Tick), "a folded tick stays one tick");
            Assert.That(merged.Sequence, Is.EqualTo(1u),
                "a folded tick is ONE wire message, named by its first frame");
        }

        [Test]
        public void Fold_Regression_JumpPressedOnTheSecondFrameSurvives()
        {
            PlayerCommand quiet = Frame(Tick, 1u);
            PlayerCommand pressed = Frame(Tick, 2u, jumpPressed: true, jumpHeld: true);

            PlayerCommand merged = PlayerCommandTape.Fold(quiet, pressed);

            Assert.That(merged.JumpPressed, Is.True,
                "the exact defect: a jump edge produced on a later frame of the same tick was lost");
            Assert.That(merged.JumpHeld, Is.True);
        }

        [Test]
        public void MutationControl_FoldThatTakesOnlyTheNewestFrame_LosesThePress()
        {
            // A guard that cannot fail is not a guard. Simulate the plausible wrong fix — "just take
            // the newest frame" — and prove the assertion above would go red on it.
            PlayerCommand pressed = Frame(Tick, 1u, jumpPressed: true);
            PlayerCommand quiet = Frame(Tick, 2u);

            PlayerCommand real = PlayerCommandTape.Fold(pressed, quiet);
            Assert.That(real.JumpPressed, Is.True, "Precondition: the real fold keeps the press.");

            PlayerCommand mutated = pressed;
            mutated.Buttons = quiet.Buttons;          // "newest frame wins", edges included
            mutated.MoveX = quiet.MoveX;
            mutated.AimAngle = quiet.AimAngle;

            Assert.That(mutated.JumpPressed, Is.False,
                "Mutation control failed: dropping the edge OR was not detected. The fold guard " +
                "would be vacuously green.");
        }

        // ── Tape: one entry per tick ──────────────────────────────────────────

        [Test]
        public void Tape_TwoFramesOfOneTick_ProduceExactlyOneBufferedEntry()
        {
            var tape = new PlayerCommandTape(Capacity);
            tape.Push(Frame(Tick, 1u, -1f));
            tape.Push(Frame(Tick, 2u, 1f, jumpPressed: true));

            Assert.That(tape.Buffer.Count, Is.EqualTo(1),
                "several render frames of one sim tick must fold into ONE tape entry");
        }

        [Test]
        public void Tape_Regression_PressOnTheSecondFrameOfATick_ReachesTickAlignedConsumer()
        {
            var tape = new PlayerCommandTape(Capacity);

            // Two Update() frames sharing tick 7. The press happens on the SECOND one — the frame
            // whose command TryGetForTick used to lose.
            tape.Push(Frame(Tick, 1u, -1f));
            tape.Push(Frame(Tick, 2u, 1f, jumpPressed: true, jumpHeld: true));

            Assert.That(tape.Buffer.TryGetForTick(Tick, out PlayerCommand consumed), Is.True,
                "the host-style tick-aligned consumer must find the command for this tick");
            Assert.That(consumed.JumpPressed, Is.True,
                "the jump edge from the second frame must survive onto the tick's command");
            Assert.That(consumed.JumpHeld, Is.True);
            Assert.That(consumed.MoveX, Is.EqualTo(PlayerCommand.Quantize(1f)));
        }

        [Test]
        public void Tape_PressOnTheFirstFrameOfATick_AlsoReachesTickAlignedConsumer()
        {
            // Equivalence control: folding must not merely favour the last frame. A press on the
            // FIRST frame, followed by a quiet frame, must still be on the tick's command.
            var tape = new PlayerCommandTape(Capacity);
            tape.Push(Frame(Tick, 1u, jumpPressed: true));
            tape.Push(Frame(Tick, 2u, 1f));

            Assert.That(tape.Buffer.TryGetForTick(Tick, out PlayerCommand consumed), Is.True);
            Assert.That(consumed.JumpPressed, Is.True,
                "folding is an OR, not a replacement — an earlier edge must not be dropped");
        }

        [Test]
        public void Tape_AcrossManyTicks_OneEntryPerTickAndChronological()
        {
            const int ticks = 10;
            const int framesPerTick = 3;
            var tape = new PlayerCommandTape(Capacity + 1);
            uint sequence = 0;

            for (int tick = 0; tick < ticks; tick++)
            {
                for (int frame = 0; frame < framesPerTick; frame++)
                {
                    tape.Push(Frame(tick, sequence++, (float)frame / framesPerTick,
                        jumpPressed: tick == 6 && frame == framesPerTick - 1));
                }
            }

            Assert.That(tape.Buffer.Count, Is.EqualTo(ticks),
                "one entry per tick, not one per render frame");

            var window = new PlayerCommand[ticks];
            Assert.That(tape.Buffer.CopyOldestFirst(window), Is.EqualTo(ticks));
            for (int tick = 0; tick < ticks; tick++)
            {
                Assert.That(window[tick].TickIndex, Is.EqualTo(tick), "tape must stay chronological");
                Assert.That(window[tick].Sequence, Is.EqualTo((uint)(tick * framesPerTick)),
                    "a folded tick keeps its FIRST frame's sequence, and those are strictly increasing");

                Assert.That(window[tick].MoveX, Is.EqualTo(PlayerCommand.Quantize(2f / framesPerTick)),
                    "the tick's entry must describe its LAST sampled frame");
                if (tick == 6)
                {
                    Assert.That(window[tick].JumpPressed, Is.True,
                        "the late-frame press on tick 6 must be on tick 6's only entry");
                }
                else
                {
                    Assert.That(window[tick].JumpPressed, Is.False,
                        "no other tick produced a press — an always-set edge would be vacuous");
                }
            }
        }

        [Test]
        public void Tape_LevelsAndAnalogueComeFromTheLastFrameOfTheTick()
        {
            var tape = new PlayerCommandTape(Capacity);
            tape.Push(Frame(Tick, 1u, -1f, jumpHeld: true, runHeld: true, aimAngle: 15f));
            tape.Push(Frame(Tick, 2u, 1f, jumpHeld: false, runHeld: false, aimAngle: 275f));

            Assert.That(tape.Buffer.TryGetForTick(Tick, out PlayerCommand consumed), Is.True);
            Assert.That(consumed.MoveX, Is.EqualTo(PlayerCommand.Quantize(1f)));
            Assert.That(consumed.AimAngle, Is.EqualTo(275f).Within(0.001f));
            Assert.That(consumed.JumpHeld, Is.False);
            Assert.That(consumed.RunHeld, Is.False);
        }

        [Test]
        public void Tape_AfterExternalClear_RecreatesTheCurrentTicksEntry()
        {
            // Defensive branch in Push: an ack/Clear can remove the pending entry mid-tick. The tape
            // must not then lose the tick entirely or append a duplicate.
            var tape = new PlayerCommandTape(Capacity);
            tape.Push(Frame(Tick, 1u, jumpPressed: true));
            tape.Buffer.Clear();
            tape.Push(Frame(Tick, 2u, 1f));

            Assert.That(tape.Buffer.Count, Is.EqualTo(1));
            Assert.That(tape.Buffer.TryGetForTick(Tick, out PlayerCommand consumed), Is.True);
            Assert.That(consumed.MoveX, Is.EqualTo(PlayerCommand.Quantize(1f)));
        }

        // ── Pre-fix shape: proof that the regression was real and the guards bite ──

        [Test]
        public void PreFixShape_EnqueuePerFrame_LosesThePress_AndDuplicatesTheTick()
        {
            // Reconstruct the broken 2026-11-06 wiring with the real buffer: enqueue one command per
            // render frame, then consume with TryGetForTick (oldest-first). This is the absent control
            // for the tape fixtures above — if this shape PASSED their assertions, those fixtures
            // would be proving nothing.
            var buffer = new PlayerCommandBuffer(Capacity);
            buffer.Enqueue(Frame(Tick, 1u, -1f));
            buffer.Enqueue(Frame(Tick, 2u, 1f, jumpPressed: true, jumpHeld: true));

            Assert.That(buffer.Count, Is.EqualTo(2),
                "Precondition: the pre-fix path really did enqueue two commands for one tick.");

            Assert.That(buffer.TryGetForTick(Tick, out PlayerCommand consumed), Is.True);
            Assert.That(consumed.JumpPressed, Is.False,
                "Precondition: oldest-first lookup really did return the stale no-press command. " +
                "If this is now true, the reproduction no longer models the defect.");
            Assert.That(consumed.JumpHeld, Is.False);
            Assert.That(consumed.MoveX, Is.EqualTo(PlayerCommand.Quantize(-1f)),
                "the stale command also carried the OLD movement intent");
        }

        // ── ReplaceLast (the buffer primitive the tape needs) ──────────────────

        [Test]
        public void Buffer_ReplaceLast_OverwritesNewestWithoutGrowing()
        {
            var buffer = new PlayerCommandBuffer(4);
            buffer.Enqueue(Frame(1, 1u));
            buffer.Enqueue(Frame(2, 2u));
            PlayerCommand replacement = Frame(2, 2u, jumpPressed: true);

            buffer.ReplaceLast(replacement);

            Assert.That(buffer.Count, Is.EqualTo(2), "ReplaceLast must not append");
            Assert.That(buffer.TryGetLatest(out PlayerCommand latest), Is.True);
            Assert.That(latest, Is.EqualTo(replacement));
            Assert.That(buffer.TryGetForTick(1, out PlayerCommand older), Is.True);
            Assert.That(older.TickIndex, Is.EqualTo(1), "older entries are untouched");
        }

        [Test]
        public void Buffer_ReplaceLast_OnEmpty_Enqueues()
        {
            var buffer = new PlayerCommandBuffer(4);
            PlayerCommand only = Frame(3, 7u, jumpPressed: true);

            buffer.ReplaceLast(only);

            Assert.That(buffer.Count, Is.EqualTo(1));
            Assert.That(buffer.TryGetLatest(out PlayerCommand latest), Is.True);
            Assert.That(latest, Is.EqualTo(only));
        }
    }
}
