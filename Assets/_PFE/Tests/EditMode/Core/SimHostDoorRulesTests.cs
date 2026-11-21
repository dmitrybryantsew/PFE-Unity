using NUnit.Framework;
using PFE.Sim.Replication;

namespace PFE.Tests.EditMode.Core
{
    /// <summary>
    /// The host-side admission rule for a peer's "toggle this door" request — the door counterpart of
    /// <see cref="SimHostInteractRulesTests"/>. Each refusal reason is pinned so the view sink's
    /// counters name the first broken link: a peer standing in another room asking the host to work a
    /// door across the map is a DIFFERENT bug from an unknown room hash, and the overlay has to say
    /// which one an operator is looking at.
    /// </summary>
    [TestFixture]
    public sealed class SimHostDoorRulesTests
    {
        private const bool Yes = true;
        private const bool No = false;

        [Test]
        public void EverythingInOrder_Admits()
        {
            Assert.That(SimHostDoorRules.Admit(Yes, Yes, Yes, Yes, Yes, No, Yes, No),
                Is.EqualTo(SimHostDoorVerdict.Admit));
        }

        [Test]
        public void MissingRoom_IsRefusedBeforeAnythingElse()
        {
            Assert.That(SimHostDoorRules.Admit(No, No, No, No, No, Yes, No, No),
                Is.EqualTo(SimHostDoorVerdict.RefuseUnknownRoom),
                "the room is the first dependency; every later input is meaningless without it");
        }

        [Test]
        public void MissingBody_IsRefused()
        {
            Assert.That(SimHostDoorRules.Admit(Yes, No, No, Yes, Yes, No, Yes, No),
                Is.EqualTo(SimHostDoorVerdict.RefuseNoBody));
        }

        [Test]
        public void BodyInAnotherRoom_IsRefused()
        {
            // The peer must be standing at the door it asks the host to toggle.
            Assert.That(SimHostDoorRules.Admit(Yes, Yes, No, Yes, Yes, No, Yes, No),
                Is.EqualTo(SimHostDoorVerdict.RefuseBodyInOtherRoom));
        }

        [Test]
        public void MissingObject_IsRefused()
        {
            Assert.That(SimHostDoorRules.Admit(Yes, Yes, Yes, No, No, No, No, No),
                Is.EqualTo(SimHostDoorVerdict.RefuseNoObject));
        }

        [Test]
        public void NonDoor_IsRefused()
        {
            Assert.That(SimHostDoorRules.Admit(Yes, Yes, Yes, Yes, No, No, Yes, No),
                Is.EqualTo(SimHostDoorVerdict.RefuseNotADoor));
        }

        [Test]
        public void CheckpointDoor_IsRefused()
        {
            // A checkpoint press is a story trigger, not a toggle: a peer's edge must not turn it
            // into a plain door. The checkpoint check runs before the lock so a locked checkpoint
            // still names the real reason.
            Assert.That(SimHostDoorRules.Admit(Yes, Yes, Yes, Yes, Yes, No, Yes, Yes),
                Is.EqualTo(SimHostDoorVerdict.RefuseCheckpoint));
        }

        [Test]
        public void LockedDoor_IsRefused()
        {
            // Lock state is not replicated; the host refuses rather than running a pick with
            // invented actor state — the same documented gap as remote container opens.
            Assert.That(SimHostDoorRules.Admit(Yes, Yes, Yes, Yes, Yes, Yes, Yes, No),
                Is.EqualTo(SimHostDoorVerdict.RefuseLocked));
        }

        [Test]
        public void MissingPresenter_IsRefused()
        {
            Assert.That(SimHostDoorRules.Admit(Yes, Yes, Yes, Yes, Yes, No, No, No),
                Is.EqualTo(SimHostDoorVerdict.RefuseNoPresenter));
        }

        [Test]
        public void TheFirstBrokenLinkWins()
        {
            // Checkpoint AND locked: the verdict names the checkpoint, because that is the input the
            // admission order reaches first — deterministic reasons are actionable reasons.
            Assert.That(SimHostDoorRules.Admit(Yes, Yes, Yes, Yes, Yes, Yes, Yes, Yes),
                Is.EqualTo(SimHostDoorVerdict.RefuseCheckpoint));
            Assert.That(SimHostDoorRules.Admit(Yes, Yes, Yes, Yes, Yes, Yes, No, No),
                Is.EqualTo(SimHostDoorVerdict.RefuseLocked));
        }

        [Test]
        public void Describe_NamesEveryVerdict()
        {
            foreach (SimHostDoorVerdict verdict in
                     System.Enum.GetValues(typeof(SimHostDoorVerdict)))
            {
                string text = SimHostDoorRules.Describe(verdict);
                Assert.That(text, Is.Not.Null.And.Not.Empty, "verdict " + verdict + " has no description");
                Assert.That(text, Does.Not.Contain("unknown ("), "verdict " + verdict + " fell through the switch");
            }
        }
    }
}
