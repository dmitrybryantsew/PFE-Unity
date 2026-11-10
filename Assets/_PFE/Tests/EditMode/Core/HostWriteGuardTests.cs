using System;
using System.Collections.Generic;
using NUnit.Framework;
using PFE.Core;

namespace PFE.Tests.EditMode.Core
{
    /// <summary>
    /// Phase 2.4 acceptance — the runtime half of mechanism (b)+(c), all pure C# (no
    /// <c>MonoBehaviour</c>, no <c>Application.dataPath</c>, no ECall), so this fixture really runs in
    /// the offline harness.
    ///
    /// <para><b>What the guard asserts.</b> A client never <b>originates</b> <c>[Authoritative]</c>
    /// state; it only <b>mirrors</b> host truth. The three contexts are host simulation, snapshot
    /// application and client prediction. The first two are legal on any peer; the third is not.
    /// The draft brief's two-state "outside a host tick is forbidden" rule is not implemented — it
    /// would fire on every correct snapshot receive and on console/save/<c>Awake</c> writes in
    /// single-player.</para>
    ///
    /// <para><b>Why a failure sink.</b> The real guard throws, but a thrown exception is a poor
    /// assertion and depends on <c>UNITY_ASSERTIONS</c> being defined at the call site. Swapping in a
    /// recorder makes both the mutation control (it must fire) and the absent controls (it must not)
    /// observable and independent of build symbols. A guard that cannot fail is not a guard; a guard
    /// that fails on a legitimate write is worse than no guard.</para>
    /// </summary>
    [TestFixture]
    public sealed class HostWriteGuardTests
    {
        private List<string> _failures;

        [SetUp]
        public void SetUp()
        {
            SimAuthorityContext.Reset();
            _failures = new List<string>();
            HostWriteGuard.SetFailureSink(_failures.Add);
        }

        [TearDown]
        public void TearDown()
        {
            Assert.That(SimAuthorityContext.ScopeDepth, Is.EqualTo(0),
                "A test leaked an authority scope — every Enter* must be disposed.");
            HostWriteGuard.ResetFailureSink();
            SimAuthorityContext.Reset();
        }

        // ── Originating writes ────────────────────────────────────────────────

        [Test]
        public void MutationControl_AClientPredictionTick_TripsTheGuard()
        {
            using (SimAuthorityContext.EnterSimulation(SimLoopRole.ClientPrediction))
            {
                HostWriteGuard.Check(SimAuthority.Authoritative);
            }

            Assert.That(_failures.Count, Is.EqualTo(1),
                "The forced client write must trip the guard exactly once.");
            Assert.That(_failures[0], Does.Contain("originated"),
                "The diagnostic must say this was an originating write, not a mirroring one.");
        }

        [Test]
        public void AbsentControl_AHostTick_DoesNotTripTheGuard()
        {
            using (SimAuthorityContext.EnterSimulation(SimLoopRole.Host))
            {
                HostWriteGuard.Check(SimAuthority.Authoritative);
            }

            Assert.That(_failures, Is.Empty,
                "A legitimate in-tick host write must stay silent — single-player behaviour is unchanged.");
        }

        [Test]
        public void AbsentControl_ASnapshotApplicationOnAClient_DoesNotTripTheGuard()
        {
            SimAuthorityContext.SetPeerRole(SimPeerRole.Client);

            using (SimAuthorityContext.EnterSnapshotApplication())
            {
                HostWriteGuard.Check(SimAuthority.Authoritative);
            }

            Assert.That(_failures, Is.Empty,
                "Mirroring host truth is the correct client write path, not a violation.");
        }

        [Test]
        public void PeerRole_Client_AmbientWriteOutsideAnyTick_TripsTheGuard()
        {
            // The hole a globally-ambient Host default would have left open: a message handler, HUD
            // action, save restore or Awake on a client, between ticks.
            SimAuthorityContext.SetPeerRole(SimPeerRole.Client);

            HostWriteGuard.Check(SimAuthority.Authoritative);

            Assert.That(_failures.Count, Is.EqualTo(1),
                "The ambient context must be the peer role, or client writes between ticks are silent.");
        }

        [Test]
        public void PeerRole_Host_AmbientWriteOutsideAnyTick_IsAllowed()
        {
            // Console `damage 100`, save/load and Awake all write authoritative state outside a tick.
            HostWriteGuard.Check(SimAuthority.Authoritative);

            Assert.That(_failures, Is.Empty,
                "Single-player has no client; the guard must be entirely silent here.");
        }

        [Test]
        public void ClientPredictedState_IsWritableByAClient()
        {
            using (SimAuthorityContext.EnterSimulation(SimLoopRole.ClientPrediction))
            {
                HostWriteGuard.Check(SimAuthority.ClientPredicted);
                HostWriteGuard.Check(SimAuthority.LocalOnly);
                HostWriteGuard.Check(SimAuthority.Unspecified);
            }

            Assert.That(_failures, Is.Empty,
                "Only [Authoritative] state is host-owned; motor state is client-predicted by design.");
        }

        [Test]
        public void DefaultFailureSink_Throws()
        {
            HostWriteGuard.ResetFailureSink();

            Assert.Throws<InvalidOperationException>(() =>
            {
                using (SimAuthorityContext.EnterSimulation(SimLoopRole.ClientPrediction))
                {
                    HostWriteGuard.Check(SimAuthority.Authoritative);
                }
            }, "In a debug build a forbidden write is a hard assert, not a log line.");
        }

        // ── Restore boundary (mechanism a, folded into c) ──────────────────────

        [Test]
        public void CheckRestore_OnHost_IsAllowed()
        {
            HostWriteGuard.CheckRestore();
            Assert.That(_failures, Is.Empty);
        }

        [Test]
        public void CheckRestore_OutsideTheSnapshotPipelineOnAClient_Trips()
        {
            SimAuthorityContext.SetPeerRole(SimPeerRole.Client);

            HostWriteGuard.CheckRestore();

            Assert.That(_failures.Count, Is.EqualTo(1),
                "Client code must not call a restore surface outside the snapshot applier.");
        }

        [Test]
        public void CheckRestore_InsideASnapshotScopeBetweenTicks_IsAllowed()
        {
            SimAuthorityContext.SetPeerRole(SimPeerRole.Client);

            using (SimAuthorityContext.EnterSnapshotApplication())
            {
                HostWriteGuard.CheckRestore();
            }

            Assert.That(_failures, Is.Empty,
                "This is the legitimate mirroring path a future client must exercise.");
        }

        [Test]
        public void CheckRestore_InsideAPredictionTick_TripsEvenWithASnapshotScope()
        {
            // The ordering bug: host truth interleaved into an in-flight prediction tick. A nested
            // snapshot scope must not be able to launder it.
            using (SimAuthorityContext.EnterSimulation(SimLoopRole.ClientPrediction))
            using (SimAuthorityContext.EnterSnapshotApplication())
            {
                HostWriteGuard.CheckRestore();
            }

            Assert.That(_failures.Count, Is.EqualTo(1),
                "A snapshot applied inside a prediction tick must fail loud.");
        }

        // ── Scope mechanics ───────────────────────────────────────────────────

        [Test]
        public void NestedScopes_RestoreThePreviousContextOnExit()
        {
            SimAuthorityContext.SetPeerRole(SimPeerRole.Client);
            Assert.That(SimAuthorityContext.Current, Is.EqualTo(SimWriteContext.ClientPrediction));

            using (SimAuthorityContext.EnterSimulation(SimLoopRole.Host))
            {
                Assert.That(SimAuthorityContext.Current, Is.EqualTo(SimWriteContext.HostSimulation));

                using (SimAuthorityContext.EnterSnapshotApplication())
                {
                    Assert.That(SimAuthorityContext.Current, Is.EqualTo(SimWriteContext.SnapshotApplication));
                }

                Assert.That(SimAuthorityContext.Current, Is.EqualTo(SimWriteContext.HostSimulation),
                    "A bare set/clear bool would have corrupted the outer scope here.");
            }

            Assert.That(SimAuthorityContext.Current, Is.EqualTo(SimWriteContext.ClientPrediction));
            Assert.That(SimAuthorityContext.IsPredictionTickActive, Is.False,
                "The prediction depth is a depth, not a flag, and must unwind to zero.");
        }

        [Test]
        public void SetPeerRole_DoesNotClobberAnActiveScope()
        {
            using (SimAuthorityContext.EnterSnapshotApplication())
            {
                SimAuthorityContext.SetPeerRole(SimPeerRole.Client);
                Assert.That(SimAuthorityContext.Current, Is.EqualTo(SimWriteContext.SnapshotApplication),
                    "A late bootstrap push must not change an in-flight scope.");
            }

            Assert.That(SimAuthorityContext.Current, Is.EqualTo(SimWriteContext.HostSimulation),
                "The scope restores its own saved context, not the role pushed while it was active. " +
                "Bootstrap pushes the role before any tick, so this is a corner case, not the normal path.");
        }
    }
}
