using NUnit.Framework;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.Editor.Core
{
    /// <summary>
    /// Source lint for the <b>wiring half</b> of the 2026-11-06 "Space no longer always jumps" fix.
    ///
    /// <para><b>Why a lint at all.</b> The defect was not bad arithmetic — it was two lines in a
    /// <c>MonoBehaviour</c>: build a fresh <c>PlayerCommand</c>, enqueue it, then read it back with
    /// <c>PlayerCommandBuffer.TryGetForTick</c>. Offline fixtures cannot instantiate a
    /// <c>MonoBehaviour</c> (Unity ECall wall), so the tape's folding policy was extracted into
    /// <c>PlayerCommandTape</c> where it is behaviourally testable, and this lint pins the two lines
    /// that remain in the component.</para>
    ///
    /// <para><b>What must never come back.</b> Any read-back through <c>TryGetForTick</c> or
    /// <c>TryGetLatest</c> in <c>HandleMovementInput()</c>. <c>TryGetForTick</c> scans oldest-first,
    /// so once several render frames share a 30 Hz tick, it returns a <i>stale</i> command; the fresh
    /// <c>WasPressedThisFrame</c> edge is discarded and locomotion never arms its jump buffer.
    /// <c>TryGetLatest</c> is no better — it is a different lookup into the same tape, not the command
    /// this frame just built. The local consumer must call <c>SetIntent(command)</c> directly.</para>
    ///
    /// <para><b>Guards that can fail.</b> The presence predicate has mutation controls (delete the
    /// direct call / restore the read-back / drop the tape push) and an equivalence control over a
    /// literal known-good and known-bad body. <c>SourceLint.ReadStripped</c> removes comments and
    /// string literals, so a comment naming the right call cannot satisfy the lint.</para>
    /// </summary>
    [TestFixture]
    public sealed class PlayerCommandConsumptionLintTests
    {
        private const string PlayerControllerPath = "Entities/Player/PlayerController.cs";
        private const string MethodSignature = "private void HandleMovementInput()";

        private const string DirectConsumption = "_locomotion.SetIntent(command);";
        private const string TapePush = "_commandTape.Push(command);";

        private static string Body()
        {
            return SourceLint.MethodBody(SourceLint.ReadStripped(PlayerControllerPath), MethodSignature);
        }

        /// <summary>
        /// The reviewed shape: this frame's command goes straight to locomotion, and the tape is fed
        /// through the folder. No lookup of any kind between building and consuming.
        /// </summary>
        private static bool IsDirectConsumption(string body)
        {
            return body.Contains(DirectConsumption)
                   && body.Contains(TapePush)
                   && !body.Contains("TryGetForTick")
                   && !body.Contains("TryGetLatest")
                   && !body.Contains("_commandBuffer.Enqueue");
        }

        [Test]
        public void HandleMovementInput_ConsumesTheFreshCommand_NotABufferReadBack()
        {
            // This is the guard that was RED on the pre-fix source: the old body called
            // _commandBuffer.TryGetForTick(commandTick, ...) and only used SetIntent(command) as the
            // fallback for an evicted buffer.
            string body = Body();

            Assert.That(body, Does.Contain(DirectConsumption),
                "local intent must be handed the command this frame just built");
            Assert.That(body, Does.Contain(TapePush),
                "the replay tape must be fed through PlayerCommandTape so frames of one tick fold");
            Assert.That(body, Does.Not.Contain("TryGetForTick"),
                "TryGetForTick is oldest-first and returns a stale command when several render " +
                "frames share a 30 Hz tick — the 2026-11-06 lost-jump defect");
            Assert.That(body, Does.Not.Contain("TryGetLatest"),
                "TryGetLatest reads the tape, not the command this frame built");
            Assert.That(body, Does.Not.Contain("_commandBuffer.Enqueue"),
                "enqueueing per frame produces duplicate tape entries for one tick");
            Assert.That(IsDirectConsumption(body), Is.True);
        }

        [Test]
        public void PlayerController_NeverLooksUpItsOwnCommandBuffer()
        {
            // Whole-file form: a read-back reintroduced from a *different* method would dodge the
            // method-body guard above. Comments are stripped, so a doc mentioning TryGetForTick
            // does not count as a usage.
            string source = SourceLint.ReadStripped(PlayerControllerPath);

            Assert.That(source, Does.Not.Contain("TryGetForTick"));
            Assert.That(source, Does.Not.Contain("TryGetLatest"));
        }

        [Test]
        public void MutationControl_RestoringThePreFixReadBack_IsDetected()
        {
            string real = Body();
            Assert.That(IsDirectConsumption(real), Is.True,
                "Precondition: the shipping body must pass the guard.");

            // Simulate the exact regression: put the oldest-first read-back back in front of the
            // direct call. This is the state the guard was red on before the fix.
            string mutated = real.Replace(
                DirectConsumption,
                "if (_commandBuffer.TryGetForTick(commandTick, out var stale)) " +
                "{ _locomotion.SetIntent(stale); } else { _locomotion.SetIntent(command); }");

            Assert.That(IsDirectConsumption(mutated), Is.False,
                "Mutation control failed: restoring the stale read-back was not detected. The green " +
                "guard above would be vacuous.");
        }

        [Test]
        public void MutationControl_DroppingTheDirectCall_IsDetected()
        {
            string real = Body();
            Assert.That(IsDirectConsumption(real), Is.True, "Precondition.");

            string mutated = real.Replace(DirectConsumption, "");

            Assert.That(IsDirectConsumption(mutated), Is.False,
                "Mutation control failed: deleting the local SetIntent was not detected.");
        }

        [Test]
        public void MutationControl_DroppingTheTapePush_IsDetected()
        {
            string real = Body();
            Assert.That(IsDirectConsumption(real), Is.True, "Precondition.");

            string mutated = real.Replace(TapePush, "");

            Assert.That(IsDirectConsumption(mutated), Is.False,
                "Mutation control failed: bypassing the one-command-per-tick folder was not detected.");
        }

        [Test]
        public void EquivalenceControl_LiteralGoodAndBadBodies()
        {
            // Positive + absent control over the *predicate itself*, independent of the shipping
            // file: a body with the right shape passes, the same body with a read-back fails.
            const string good =
                "{ var command = Build(); _commandTape.Push(command); " +
                "_locomotion.SetIntent(command); }";
            const string bad =
                "{ var command = Build(); _commandBuffer.Enqueue(command); " +
                "if (_commandBuffer.TryGetForTick(t, out var c)) { _locomotion.SetIntent(c); } " +
                "else { _locomotion.SetIntent(command); } }";

            Assert.That(IsDirectConsumption(good), Is.True,
                "the predicate must accept the reviewed shape");
            Assert.That(IsDirectConsumption(bad), Is.False,
                "the predicate must reject the 2026-11-06 read-back shape");
        }

        [Test]
        public void PositiveControl_CommentNamingTheCall_DoesNotSatisfyTheLint()
        {
            // SourceLint.ReadStripped is the instrument every assertion here depends on. If the
            // stripper stopped stripping, a comment could make this lint green while the call is gone.
            string stripped = SourceLint.StripCommentsAndStrings(
                "// _locomotion.SetIntent(command);\n" +
                "// _commandTape.Push(command);\n" +
                "private void HandleMovementInput() { }");

            Assert.That(stripped, Does.Not.Contain(DirectConsumption));
            Assert.That(stripped, Does.Not.Contain(TapePush));
        }
    }
}
