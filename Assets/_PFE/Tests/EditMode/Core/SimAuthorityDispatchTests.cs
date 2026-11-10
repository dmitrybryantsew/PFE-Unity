using System.Collections.Generic;
using NUnit.Framework;
using PFE.Core;

namespace PFE.Tests.EditMode.Core
{
    /// <summary>
    /// Phase 2.4, mechanism (b) exercised through the real <see cref="SimLoop"/>: the authority
    /// context is set for the whole <c>StepOnce()</c> dispatch and unwound afterwards, and a
    /// prediction-only loop is the one that makes the guard fire. Same scripted-tickable pattern as
    /// <c>TwoSimLoopDeterminismTests.CommandConsumer</c> — no MonoBehaviour, so it runs offline.
    ///
    /// <para><b>Why both halves matter.</b> A context that is set but never cleared would leave a host
    /// loop looking like a prediction loop for the rest of the frame; one that is cleared but never
    /// set would make the guard vacuously silent. The fixture pins set AND restore by giving the
    /// process a distinguishable ambient peer role and reading <see cref="SimAuthorityContext.Current"/>
    /// from inside the tick.</para>
    /// </summary>
    [TestFixture]
    public sealed class SimAuthorityDispatchTests
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
                "StepOnce leaked its authority scope.");
            HostWriteGuard.ResetFailureSink();
            SimAuthorityContext.Reset();
        }

        /// <summary>Reads the context from inside a tick and optionally performs a guarded write.</summary>
        private sealed class ContextProbe : ISimTickable
        {
            private readonly bool _performGuardedWrite;
            public SimWriteContext Seen;
            public int Calls;

            public ContextProbe(bool performGuardedWrite)
            {
                _performGuardedWrite = performGuardedWrite;
            }

            public int TickOrder => SimTickOrder.Input;

            public void SimTick(int tickIndex)
            {
                Seen = SimAuthorityContext.Current;
                Calls++;
                if (_performGuardedWrite)
                {
                    HostWriteGuard.Check(SimAuthority.Authoritative);
                }
            }
        }

        private static SimLoop NewLoop(SimLoopRole role)
        {
            var loop = new SimLoop(new SimClock(SimClock.CanonicalTicksPerSecond), null);
            loop.Role = role;
            return loop;
        }

        [Test]
        public void DefaultRole_IsHost_SoEveryExistingFixtureStaysSilent()
        {
            var loop = new SimLoop(new SimClock(SimClock.CanonicalTicksPerSecond), null);
            Assert.That(loop.Role, Is.EqualTo(SimLoopRole.Host),
                "Six call sites construct SimLoop(clock, settings) directly; the default must be Host.");
        }

        [Test]
        public void HostLoop_SetsHostSimulationInsideDispatch_AndRestoresAmbientAfter()
        {
            // A client ambient makes the restore observable: without it, HostSimulation -> HostSimulation
            // would prove nothing about the clear half.
            SimAuthorityContext.SetPeerRole(SimPeerRole.Client);
            var loop = NewLoop(SimLoopRole.Host);
            var probe = new ContextProbe(false);
            loop.Register(probe);

            loop.StepOnce();

            Assert.That(probe.Calls, Is.EqualTo(1));
            Assert.That(probe.Seen, Is.EqualTo(SimWriteContext.HostSimulation),
                "The tickable must see the host context during dispatch.");
            Assert.That(SimAuthorityContext.Current, Is.EqualTo(SimWriteContext.ClientPrediction),
                "The scope must unwind back to the process ambient context after the tick.");
        }

        [Test]
        public void ClientPredictionLoop_SetsClientPredictionInsideDispatch_AndRestoresAfter()
        {
            var loop = NewLoop(SimLoopRole.ClientPrediction);
            var probe = new ContextProbe(false);
            loop.Register(probe);

            loop.StepOnce();

            Assert.That(probe.Seen, Is.EqualTo(SimWriteContext.ClientPrediction));
            Assert.That(SimAuthorityContext.Current, Is.EqualTo(SimWriteContext.HostSimulation));
            Assert.That(SimAuthorityContext.IsPredictionTickActive, Is.False,
                "The prediction depth must unwind with the scope.");
        }

        [Test]
        public void MutationControl_AWriteInsideAClientPredictionTick_TripsTheGuard()
        {
            var loop = NewLoop(SimLoopRole.ClientPrediction);
            loop.Register(new ContextProbe(true));

            loop.StepOnce();

            Assert.That(_failures.Count, Is.EqualTo(1),
                "A host system accidentally registered on a client's prediction loop must fail loud.");
        }

        [Test]
        public void AbsentControl_AWriteInsideAHostTick_DoesNotTripTheGuard()
        {
            var loop = NewLoop(SimLoopRole.Host);
            loop.Register(new ContextProbe(true));

            loop.StepOnce();

            Assert.That(_failures, Is.Empty,
                "A legitimate in-tick host write must not trip the guard.");
        }

        [Test]
        public void ScopesUnwindAcrossManyTicks()
        {
            var loop = NewLoop(SimLoopRole.ClientPrediction);
            var probe = new ContextProbe(false);
            loop.Register(probe);

            for (int i = 0; i < 300; i++)
            {
                loop.StepOnce();
                Assert.That(SimAuthorityContext.ScopeDepth, Is.EqualTo(0),
                    "Scope leaked at tick " + i);
            }

            Assert.That(probe.Calls, Is.EqualTo(300));
            Assert.That(loop.TickIndex, Is.EqualTo(299),
                "The added scope must not change the loop's tick arithmetic (golden-trace invariant).");
        }
    }
}
