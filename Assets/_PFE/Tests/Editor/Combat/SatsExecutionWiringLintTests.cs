using System;
using NUnit.Framework;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.Editor.Combat
{
    /// <summary>
    /// Pins the <b>execution wiring</b> of S.A.T.S.: that a queued plan actually fires through the
    /// player's normal fire cycle once the mode closes, and that nothing fires while it is open.
    ///
    /// <para><b>Why lints and not behavioural tests.</b> Every link in this chain is a
    /// <c>MonoBehaviour</c> — the loadout's <c>FixedUpdate</c>, the player's <c>HandleAiming</c>, the
    /// controller's tick registration. Outside the editor they need a <c>GameObject</c> and throw
    /// <c>ECall methods must be packaged into a system module</c> before reaching an assertion. The
    /// arithmetic the chain feeds is pinned elsewhere: <c>SatsRulesTests</c> covers
    /// <c>ShotPopsFrontCell</c> and the cost expressions, and the offline <c>satsverify</c> harness
    /// replays the whole 3-shot sequence against the real rules. What none of those can see is
    /// <i>whether the call is ever made</i>, and this project's repeated failure mode is exactly that:
    /// arithmetic perfect, wiring wrong, no exception to trace.</para>
    ///
    /// <para><b>Every assertion is paired with an absent control</b> taken from the same extracted
    /// region, so a lint cannot pass because the extractor returned nothing.</para>
    /// </summary>
    [TestFixture]
    public class SatsExecutionWiringLintTests
    {
        private const string LoadoutPath = "Systems/Weapons/PlayerWeaponLoadout.cs";
        private const string PlayerPath = "Entities/Player/PlayerController.cs";
        private const string ControllerPath = "Systems/Combat/Sats/SatsController.cs";

        /// <summary>
        /// The text of one method, from its signature to its closing brace.
        ///
        /// <para><b>Why bounding matters.</b> A search that runs "from the signature onward" silently
        /// widens to the rest of the file, so an assertion about a call <i>inside</i> the method is
        /// satisfied by the same identifier appearing in any later member. That is a guard that cannot
        /// fail, and this fixture had one until a mutation exposed it. The closing brace is matched at
        /// the member indent (8 spaces) so nested blocks do not end the region early.</para>
        /// </summary>
        private static string MethodBody(string source, string signature)
        {
            int start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0,
                "Could not find `" + signature + "`. If the method was renamed, this fixture must be " +
                "repointed rather than left to fail as a missing-signature error.");

            int end = source.IndexOf("\n        }", start, StringComparison.Ordinal);
            Assert.IsTrue(end > start, "Could not find the end of `" + signature + "`.");

            return source.Substring(start, end - start);
        }

        // ── 1. The pause gate comes before the plan's turn ──────────────────────

        /// <summary>
        /// <c>FixedUpdate</c> must return on <c>IsWorldPaused</c> <b>before</b> it reaches
        /// <c>DriveSatsQueue</c>.
        ///
        /// <para><b>The defect this prevents.</b> S.A.T.S. is a hard pause, and the plan is meant to
        /// fire <i>after</i> the player leaves. If the queue were driven above the gate, every queued
        /// shot would be released into the frozen world the mode exists to plan in: the player would
        /// watch their own plan execute while they were still editing it, and the mode would be
        /// unusable rather than merely wrong.</para>
        /// </summary>
        [Test]
        public void Loadout_PausesBeforeDrivingTheQueue()
        {
            string source = SourceLint.ReadStripped(LoadoutPath);

            int gate = source.IndexOf("if (IsWorldPaused) return;", StringComparison.Ordinal);

            // The CALL, not the definition: `private void DriveSatsQueue()` also contains
            // `DriveSatsQueue()`, and it sits above FixedUpdate — so matching the bare name would
            // compare the gate against the method's own declaration and fail for the wrong reason.
            int drive = source.IndexOf("DriveSatsQueue();", StringComparison.Ordinal);

            Assert.IsTrue(gate >= 0,
                "The pause gate is gone from PlayerWeaponLoadout.FixedUpdate. Without it a queued " +
                "S.A.T.S. shot fires inside the frozen frame the mode was opened to plan in.");

            Assert.IsTrue(drive >= 0,
                "DriveSatsQueue() is never called. A plan that is never driven looks exactly like a " +
                "plan with no cells: the mode queues shots, accepts, and nothing happens.");

            Assert.Less(gate, drive,
                "The pause gate must precede DriveSatsQueue(). Reversing them releases queued shots " +
                "into the paused world — the one thing a hard pause cannot allow.");
        }

        // ── 2. The queue is driven before the weapon's own tick ─────────────────

        /// <summary>
        /// <c>DriveSatsQueue</c> must call <c>PreWeaponTick</c> <b>and then</b> <c>BeginAttack</c>, in
        /// that order, and must push the returned aim point.
        ///
        /// <para><b>Why the order is load-bearing.</b> AS3's <c>control()</c> runs before the weapon's
        /// <c>actions()</c> in the same unit step: it reads the <c>is_shoot</c> the <i>previous</i>
        /// tick's shot set, charges that shot, and arms the next one. Swapping the two would charge a
        /// shot on the tick it is armed rather than the tick after, so a three-cell plan would spend
        /// its last cell without ever firing it.</para>
        /// </summary>
        [Test]
        public void Loadout_ArmsTheShotAfterChargingTheLastOne()
        {
            string source = SourceLint.ReadStripped(LoadoutPath);
            string body = MethodBody(source, "private void DriveSatsQueue()");

            int pre = body.IndexOf("PreWeaponTick", StringComparison.Ordinal);
            int begin = body.IndexOf("BeginAttack()", StringComparison.Ordinal);

            Assert.IsTrue(pre > 0, "DriveSatsQueue never calls PreWeaponTick.");
            Assert.IsTrue(begin > 0, "DriveSatsQueue never calls BeginAttack — the plan would be " +
                                     "charged but never fired.");
            Assert.Less(pre, begin,
                "PreWeaponTick must run before BeginAttack: it decides whether the front cell may " +
                "fire at all, and calls UnsetCel when it may not.");

            // The aim override. Without it the shot leaves along the cursor, not along the cell, which
            // is the whole difference between S.A.T.S. and holding the mouse over an enemy.
            //
            // Scoped to THIS method's body. An earlier version searched "from the signature onward",
            // and a mutation that deleted the assignment still passed because `_aimTarget` appears in
            // several later members — a guard that could not fail.
            //
            // What this does NOT catch: `_aimTarget = _aimTarget;` leaves the identifier present while
            // making the assignment dead. A lint reads presence, not meaning; the behavioural half of
            // this claim is the offline `satsverify` harness, which checks the cell's aim point
            // reaches the shot. Recorded so a reader does not over-trust this assertion.
            Assert.IsTrue(body.Contains("_aimTarget"),
                "DriveSatsQueue does not set _aimTarget from the front cell's aim point. The queued " +
                "shot would then fire at the cursor, so a plan built across a room would miss.");
        }

        // ── 3. The aim override outranks the cursor while the mode is open ──────

        /// <summary>
        /// <c>HandleAiming</c> must apply the queue's aim point <b>before</b> its own pause early
        /// return.
        ///
        /// <para><b>The defect this prevents.</b> While the mode is open the cursor drives a
        /// <i>preview</i>, and the front cell's aim point is what the reticle is drawn from. If the
        /// override sat below the pause return it would never run while paused, so the preview and the
        /// eventual shot would disagree — the plan would fire where the player last hovered rather than
        /// where the cell was placed.</para>
        /// </summary>
        [Test]
        public void Player_AppliesTheQueueAimBeforeThePauseReturn()
        {
            string body = MethodBody(SourceLint.ReadStripped(PlayerPath), "private void HandleAiming()");

            int overrideAt = body.IndexOf("QueueAimWorldUnits", StringComparison.Ordinal);
            int pauseAt = body.IndexOf("if (IsWorldPaused) return;", StringComparison.Ordinal);

            Assert.IsTrue(overrideAt > 0,
                "HandleAiming no longer consults QueueAimWorldUnits. A queued shot then fires at the " +
                "cursor and the mode degenerates into an aim-bot rather than a shot queue.");
            Assert.IsTrue(pauseAt > 0, "HandleAiming lost its pause guard.");
            Assert.Less(overrideAt, pauseAt,
                "The queue's aim override must be applied before the pause return, or it never runs " +
                "while the mode is open — which is the only time it matters.");
        }

        // ── 4. Accepting does not cancel ────────────────────────────────────────

        /// <summary>
        /// <c>Exit()</c> must not clear the queue: the Oracle's own hint text calls that key
        /// "accept" (<c>text_en.xml:4750-4751</c>).
        ///
        /// <para><b>Why this needs pinning at all.</b> It is the one place where the obvious
        /// implementation is the wrong one. Every other mode's "close" discards pending state, so
        /// <c>ClearAll()</c> in <c>Exit</c> is what a reader expects to find — and it would silently
        /// turn the mode into a queue the player can never use.</para>
        /// </summary>
        [Test]
        public void Exit_KeepsTheQueue()
        {
            string body = MethodBody(SourceLint.ReadStripped(ControllerPath), "public void Exit()");

            Assert.IsFalse(body.Contains("ClearAll"),
                "Exit() calls ClearAll. The exit key is the ACCEPT key: cancelling is Q (remove the " +
                "last cell) or a manual shot. Clearing here makes the mode unable to fire anything.");
        }

        // ── 5. The mode ticks while the world is frozen ─────────────────────────

        /// <summary>
        /// <c>SatsController</c> must be a <c>ViewAligned</c> tickable, because its input runs under
        /// <c>allStat &gt;= 1</c> — i.e. <b>while frozen</b>.
        ///
        /// <para><b>The defect this prevents.</b> <c>SimLoop</c> dispatches only
        /// <see cref="PFE.Core.SimTickOrder.ViewAligned"/> tickables while the gate is held. A
        /// controller registered at the default order is simply never ticked while the mode is open, so
        /// queueing, removing and accepting all do nothing — and the mode looks like a rendering bug
        /// rather than a scheduling one.</para>
        /// </summary>
        [Test]
        public void Controller_TicksInTheViewAlignedPhase()
        {
            string source = SourceLint.ReadStripped(ControllerPath);

            // The exact declaration, because SimTickOrder is a static class of `const int` and a
            // looser match ("mentions ViewAligned somewhere after TickOrder") would also be satisfied
            // by a neighbouring member. There is no `SimTickOrder.Default` to exclude — the type has
            // no such member — so the literal is the only control that can actually fail.
            Assert.IsTrue(
                source.Contains("public int TickOrder => PFE.Core.SimTickOrder.ViewAligned;"),
                "SatsController.TickOrder is not SimTickOrder.ViewAligned. SimLoop dispatches only the " +
                "ViewAligned bucket while InterfacePauseGate holds the world, so the mode would receive " +
                "no input at all while it is open — queueing, removing and accepting would all be dead, " +
                "and it would present as a rendering fault rather than a scheduling one.");
        }
    }
}
