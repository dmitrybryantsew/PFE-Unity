using NUnit.Framework;
using PFE.Core;

namespace PFE.Tests.EditMode.Core
{
    /// <summary>
    /// The one-frame carrier between a suppressed container interact and the next command build.
    /// Exactly-once consumption is the whole contract: a raise the build never consumes is dropped
    /// (the lost-press answer), and a consumed edge cannot fire twice (the double-open answer).
    /// </summary>
    [TestFixture]
    public sealed class SimInteractCommandLatchTests
    {
        [TearDown]
        public void TearDown()
        {
            SimInteractCommandLatch.Reset();
        }

        [Test]
        public void NothingRaised_NothingToConsume()
        {
            ulong roomHash;
            int objectIndex;
            Assert.That(SimInteractCommandLatch.TryConsume(out roomHash, out objectIndex), Is.False);
            Assert.That(roomHash, Is.EqualTo(0UL));
            Assert.That(objectIndex, Is.EqualTo(0));
        }

        [Test]
        public void RaisedEdge_IsConsumedExactlyOnce()
        {
            SimInteractCommandLatch.Raise(0xA0A0000000000001UL, 13);

            ulong roomHash;
            int objectIndex;
            Assert.That(SimInteractCommandLatch.TryConsume(out roomHash, out objectIndex), Is.True);
            Assert.That(roomHash, Is.EqualTo(0xA0A0000000000001UL));
            Assert.That(objectIndex, Is.EqualTo(13));

            // The second consume must find nothing — the edge already rode its command.
            Assert.That(SimInteractCommandLatch.TryConsume(out roomHash, out objectIndex), Is.False);
        }

        [Test]
        public void ARaiseBeforeConsumption_OverwritesThePreviousEdge()
        {
            // One frame carries one press; the latch holds the NEWEST target, matching how the
            // per-tick tape fold keeps the newest frame's intent.
            SimInteractCommandLatch.Raise(1UL, 1);
            SimInteractCommandLatch.Raise(2UL, 2);

            ulong roomHash;
            int objectIndex;
            Assert.That(SimInteractCommandLatch.TryConsume(out roomHash, out objectIndex), Is.True);
            Assert.That(roomHash, Is.EqualTo(2UL));
            Assert.That(objectIndex, Is.EqualTo(2));
        }

        [Test]
        public void Reset_DropsAPendingEdge()
        {
            SimInteractCommandLatch.Raise(9UL, 9);
            SimInteractCommandLatch.Reset();

            ulong roomHash;
            int objectIndex;
            Assert.That(SimInteractCommandLatch.TryConsume(out roomHash, out objectIndex), Is.False);
        }
    }
}
