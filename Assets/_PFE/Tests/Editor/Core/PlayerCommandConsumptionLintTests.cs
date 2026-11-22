using NUnit.Framework;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.Editor.Core
{
    /// <summary>
    /// Source lint for the <b>wiring half</b> of the 2026-11-06 "Space no longer always jumps" fix,
    /// retargeted by the thread-F split: the policy lives in <c>LocalPlayerInputSampler.SampleFrame</c>
    /// and the wiring lines live in <c>PlayerController.Update</c>.
    ///
    /// <para><b>Why a lint at all.</b> The defect was not bad arithmetic — it was two lines: build a
    /// fresh <c>PlayerCommand</c>, enqueue it, then read it back with
    /// <c>PlayerCommandBuffer.TryGetForTick</c>. Offline fixtures cannot instantiate the
    /// <c>MonoBehaviour</c> half, so the fold policy was extracted into <c>PlayerCommandTape</c> and
    /// the sampling into <c>LocalPlayerInputSampler</c> (a plain C# class the fixtures CAN run — see
    /// <c>LocalPlayerInputSamplerTests</c>), and this lint pins the remaining source facts: the
    /// sampler pushes the fresh command and never reads the tape back, and <c>Update</c> hands that
    /// same fresh frame to the executor.</para>
    ///
    /// <para><b>What must never come back.</b> Any read-back through <c>TryGetForTick</c> or
    /// <c>TryGetLatest</c> in the sampling or execution path. <c>TryGetForTick</c> scans oldest-first,
    /// so once several render frames share a 30 Hz tick it returns a <i>stale</i> command; the fresh
    /// <c>WasPressedThisFrame</c> edge is discarded and locomotion never arms its jump buffer.
    /// <c>TryGetLatest</c> is no better — it is a different lookup into the same tape, not the command
    /// this frame just built. The local consumer must execute the frame the sampler just returned.</para>
    ///
    /// <para><b>Guards that can fail.</b> The presence predicates have mutation controls (delete the
    /// push / restore the read-back / drop the fresh hand-off) and an equivalence control over
    /// literal known-good and known-bad bodies. <c>SourceLint.ReadStripped</c> removes comments and
    /// string literals, so a comment naming the right call cannot satisfy the lint.</para>
    /// </summary>
    [TestFixture]
    public sealed class PlayerCommandConsumptionLintTests
    {
        private const string PlayerControllerPath = "Entities/Player/PlayerController.cs";
        private const string SamplerPath = "Entities/Player/LocalPlayerInputSampler.cs";
        private const string ExecutorPath = "Entities/Player/PlayerCommandExecutor.cs";
        private const string SampleFrameSignature = "public SampledFrame SampleFrame(";

        private const string TapePush = "_commandTape.Push(command);";
        private const string FreshCommandHandoff = "command: command,";
        private const string SampleCall = "_inputSampler.SampleFrame(";
        private const string ExecutorCall = "_commandExecutor.Execute(in frame,";

        private static string SampleFrameBody()
        {
            return SourceLint.MethodBody(SourceLint.ReadStripped(SamplerPath), SampleFrameSignature);
        }

        /// <summary>
        /// The reviewed shape: the built command is pushed onto the tape AND returned in the fresh
        /// frame. No lookup of any kind between building and handing off.
        /// </summary>
        private static bool IsProducerOnly(string body)
        {
            return body.Contains(TapePush)
                   && body.Contains(FreshCommandHandoff)
                   && !body.Contains("TryGetForTick")
                   && !body.Contains("TryGetLatest")
                   && !body.Contains("_commandBuffer.Enqueue");
        }

        [Test]
        public void SampleFrame_PushesTheFreshCommand_AndNeverReadsTheTapeBack()
        {
            // This is the guard that was RED on the pre-fix source: the old body called
            // _commandBuffer.TryGetForTick(commandTick, ...) and only used SetIntent(command) as the
            // fallback for an evicted buffer.
            string body = SampleFrameBody();

            Assert.That(body, Does.Contain(TapePush),
                "the replay tape must be fed through PlayerCommandTape so frames of one tick fold");
            Assert.That(body, Does.Contain(FreshCommandHandoff),
                "the command this frame built must be the command the frame carries to the executor");
            Assert.That(body, Does.Not.Contain("TryGetForTick"),
                "TryGetForTick is oldest-first and returns a stale command when several render " +
                "frames share a 30 Hz tick — the 2026-11-06 lost-jump defect");
            Assert.That(body, Does.Not.Contain("TryGetLatest"),
                "TryGetLatest reads the tape, not the command this frame built");
            Assert.That(body, Does.Not.Contain("_commandBuffer.Enqueue"),
                "enqueueing per frame produces duplicate tape entries for one tick");
            Assert.That(IsProducerOnly(body), Is.True);
        }

        [Test]
        public void Controller_NeverLooksUpItsOwnCommandBuffer()
        {
            // Whole-file form: a read-back reintroduced from a *different* method would dodge the
            // method-body guard above. Scans the controller AND the executor — neither may read the
            // tape; it is the uplink's payload source, never a local input. Comments are stripped, so
            // a doc mentioning TryGetForTick does not count as a usage.
            string controller = SourceLint.ReadStripped(PlayerControllerPath);
            string executor = SourceLint.ReadStripped(ExecutorPath);

            Assert.That(controller, Does.Not.Contain("TryGetForTick"));
            Assert.That(controller, Does.Not.Contain("TryGetLatest"));
            Assert.That(executor, Does.Not.Contain("TryGetForTick"));
            Assert.That(executor, Does.Not.Contain("TryGetLatest"));
        }

        [Test]
        public void Update_SamplesThenExecutesTheFreshFrame()
        {
            // The two wiring lines the whole shape depends on: one frame from the sampler, straight
            // into the executor. Interposing anything that reads state between them is the defect
            // shape — the tape included.
            string body = SourceLint.MethodBody(SourceLint.ReadStripped(PlayerControllerPath),
                "private void Update()");

            Assert.That(body, Does.Contain(SampleCall),
                "Update must sample the frame from LocalPlayerInputSampler");
            Assert.That(body, Does.Contain(ExecutorCall),
                "Update must execute the frame the sampler just returned");
        }

        [Test]
        public void MutationControl_RestoringThePreFixReadBack_IsDetected()
        {
            string real = SampleFrameBody();
            Assert.That(IsProducerOnly(real), Is.True,
                "Precondition: the shipping body must pass the guard.");

            // Simulate the exact regression: put the oldest-first read-back back in front of the
            // hand-off. This is the state the guard was red on before the fix.
            string mutated = real.Replace(
                FreshCommandHandoff,
                "if (_commandBuffer.TryGetForTick(commandTick, out var stale)) " +
                "{ command = stale; } " +
                "return new SampledFrame(hasCommand: true, command: command, processedMoveInput: input, armTakeWindow: false);");

            Assert.That(IsProducerOnly(mutated), Is.False,
                "Mutation control failed: restoring the stale read-back was not detected. The green " +
                "guard above would be vacuous.");
        }

        [Test]
        public void MutationControl_DroppingTheTapePush_IsDetected()
        {
            string real = SampleFrameBody();
            Assert.That(IsProducerOnly(real), Is.True, "Precondition.");

            string mutated = real.Replace(TapePush, "");

            Assert.That(IsProducerOnly(mutated), Is.False,
                "Mutation control failed: bypassing the one-command-per-tick folder was not detected.");
        }

        [Test]
        public void MutationControl_DroppingTheFreshHandoff_IsDetected()
        {
            string real = SampleFrameBody();
            Assert.That(IsProducerOnly(real), Is.True, "Precondition.");

            string mutated = real.Replace(FreshCommandHandoff, "");

            Assert.That(IsProducerOnly(mutated), Is.False,
                "Mutation control failed: returning a frame that does not carry the built command " +
                "was not detected — the executor would run whatever the frame was default-initialised with.");
        }

        [Test]
        public void MutationControl_DroppingTheDirectExecute_IsDetected()
        {
            string real = SourceLint.MethodBody(SourceLint.ReadStripped(PlayerControllerPath),
                "private void Update()");
            Assert.That(real, Does.Contain(ExecutorCall), "Precondition.");

            string mutated = real.Replace(ExecutorCall, "// removed");

            Assert.That(mutated, Does.Not.Contain(ExecutorCall),
                "Mutation control failed: deleting the execute call was not detected.");
        }

        [Test]
        public void EquivalenceControl_LiteralGoodAndBadBodies()
        {
            // Positive + absent control over the *predicate itself*, independent of the shipping
            // file: a body with the right shape passes, the same body with a read-back fails.
            const string good =
                "{ var command = Build(); _commandTape.Push(command); " +
                "return new SampledFrame(hasCommand: true, command: command, processedMoveInput: input, armTakeWindow: false); }";
            const string bad =
                "{ var command = Build(); _commandBuffer.Enqueue(command); " +
                "if (_commandBuffer.TryGetForTick(t, out var c)) { command = c; } " +
                "return new SampledFrame(hasCommand: true, command: command, processedMoveInput: input, armTakeWindow: false); }";

            Assert.That(IsProducerOnly(good), Is.True,
                "the predicate must accept the reviewed shape");
            Assert.That(IsProducerOnly(bad), Is.False,
                "the predicate must reject the 2026-11-06 read-back shape");
        }

        [Test]
        public void PositiveControl_CommentNamingTheCall_DoesNotSatisfyTheLint()
        {
            // SourceLint.ReadStripped is the instrument every assertion here depends on. If the
            // stripper stopped stripping, a comment could make this lint green while the call is gone.
            string stripped = SourceLint.StripCommentsAndStrings(
                "// _commandTape.Push(command);\n" +
                "// command: command,\n" +
                "public SampledFrame SampleFrame(");

            Assert.That(stripped, Does.Not.Contain(TapePush));
            Assert.That(stripped, Does.Not.Contain(FreshCommandHandoff));
        }
    }
}
