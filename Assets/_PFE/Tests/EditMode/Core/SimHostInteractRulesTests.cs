using NUnit.Framework;
using PFE.Sim.Replication;

namespace PFE.Tests.EditMode.Core
{
    /// <summary>
    /// The host-side admission rule for a peer's "open this container" request. Each refusal reason is
    /// pinned so the view sink's counters name the first broken link instead of a generic "refused" —
    /// a peer standing in another room asking the host to loot a chest across the map is a DIFFERENT
    /// bug from an unknown room hash, and the overlay has to say which one an operator is looking at.
    /// </summary>
    [TestFixture]
    public sealed class SimHostInteractRulesTests
    {
        private const bool Yes = true;
        private const bool No = false;

        [Test]
        public void EverythingInOrder_Admits()
        {
            Assert.That(SimHostInteractRules.Admit(Yes, Yes, Yes, Yes, Yes, No, No, Yes),
                Is.EqualTo(SimHostInteractVerdict.Admit));
        }

        [Test]
        public void MissingRoom_IsRefusedBeforeAnythingElse()
        {
            Assert.That(SimHostInteractRules.Admit(No, No, No, No, No, Yes, Yes, No),
                Is.EqualTo(SimHostInteractVerdict.RefuseUnknownRoom),
                "the room is the first dependency; every later input is meaningless without it");
        }

        [Test]
        public void MissingBody_IsRefused()
        {
            Assert.That(SimHostInteractRules.Admit(Yes, No, No, Yes, Yes, No, No, Yes),
                Is.EqualTo(SimHostInteractVerdict.RefuseNoBody));
        }

        [Test]
        public void BodyInAnotherRoom_IsRefused()
        {
            // The peer must be standing at the chest it asks the host to open.
            Assert.That(SimHostInteractRules.Admit(Yes, Yes, No, Yes, Yes, No, No, Yes),
                Is.EqualTo(SimHostInteractVerdict.RefuseBodyInOtherRoom));
        }

        [Test]
        public void MissingObject_IsRefused()
        {
            Assert.That(SimHostInteractRules.Admit(Yes, Yes, Yes, No, No, No, No, No),
                Is.EqualTo(SimHostInteractVerdict.RefuseNoObject));
        }

        [Test]
        public void NonContainer_IsRefused()
        {
            Assert.That(SimHostInteractRules.Admit(Yes, Yes, Yes, Yes, No, No, No, Yes),
                Is.EqualTo(SimHostInteractVerdict.RefuseNotAContainer));
        }

        [Test]
        public void AlreadyLooted_IsRefused()
        {
            // The idempotency guard: a duplicated edge that slipped the redundancy window must not
            // re-roll a chest that was opened a moment ago.
            Assert.That(SimHostInteractRules.Admit(Yes, Yes, Yes, Yes, Yes, Yes, No, Yes),
                Is.EqualTo(SimHostInteractVerdict.RefuseAlreadyLooted));
        }

        [Test]
        public void LockedContainer_IsRefused()
        {
            // Lock state is not replicated in slice 1; the host refuses rather than running a pick
            // with invented actor state.
            Assert.That(SimHostInteractRules.Admit(Yes, Yes, Yes, Yes, Yes, No, Yes, Yes),
                Is.EqualTo(SimHostInteractVerdict.RefuseLocked));
        }

        [Test]
        public void MissingPresenter_IsRefused()
        {
            Assert.That(SimHostInteractRules.Admit(Yes, Yes, Yes, Yes, Yes, No, No, No),
                Is.EqualTo(SimHostInteractVerdict.RefuseNoPresenter));
        }

        [Test]
        public void TheFirstBrokenLinkWins()
        {
            // Locked AND no presenter: the verdict names the lock, because that is the input the
            // admission order reaches first — deterministic reasons are actionable reasons.
            Assert.That(SimHostInteractRules.Admit(Yes, Yes, Yes, Yes, Yes, No, Yes, No),
                Is.EqualTo(SimHostInteractVerdict.RefuseLocked));
            Assert.That(SimHostInteractRules.Admit(Yes, Yes, Yes, Yes, Yes, Yes, No, No),
                Is.EqualTo(SimHostInteractVerdict.RefuseAlreadyLooted));
        }

        [Test]
        public void Describe_NamesEveryVerdict()
        {
            foreach (SimHostInteractVerdict verdict in
                     System.Enum.GetValues(typeof(SimHostInteractVerdict)))
            {
                string text = SimHostInteractRules.Describe(verdict);
                Assert.That(text, Is.Not.Null.And.Not.Empty, "verdict " + verdict + " has no description");
                Assert.That(text, Does.Not.Contain("unknown ("), "verdict " + verdict + " fell through the switch");
            }
        }
    }
}
