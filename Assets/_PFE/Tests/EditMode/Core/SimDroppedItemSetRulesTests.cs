using System;
using NUnit.Framework;
using PFE.Sim.State;

namespace PFE.Tests.EditMode.Core
{
    /// <summary>
    /// The executable contract of the drop-set reconcile — the pure half of the client's
    /// "containers drop loot for both". A merge-instead-of-replace receiver re-adds drops the local
    /// player watched vanish; a receiver that applies malformed sets invents identities. Both shapes
    /// are pinned here so the view applier cannot drift them.
    /// </summary>
    [TestFixture]
    public sealed class SimDroppedItemSetRulesTests
    {
        private static SimDroppedItem Drop(ulong hash, int quantity = 1)
        {
            return new SimDroppedItem
            {
                EntityHash = hash,
                ItemIdHash = hash ^ 0x5A5A000000000000UL,
                IsWeapon = false,
                Quantity = quantity,
                Variant = 0,
                Condition = 1f,
                Px = 100f,
                Py = 50f,
            };
        }

        [Test]
        public void Validate_AcceptsAGoodSet()
        {
            SimDroppedItem[] wire = { Drop(1), Drop(2), Drop(3) };
            string reason;
            Assert.That(SimDroppedItemSetRules.TryValidateWireSet(wire, out reason), Is.True, reason);
            Assert.That(reason, Is.EqualTo("none"));
        }

        [Test]
        public void Validate_RefusesAZeroEntityHash()
        {
            SimDroppedItem[] wire = { Drop(1), Drop(0) };
            string reason;
            Assert.That(SimDroppedItemSetRules.TryValidateWireSet(wire, out reason), Is.False);
            Assert.That(reason, Does.Contain("no entity hash"));
        }

        [Test]
        public void Validate_RefusesDuplicateEntityHashes()
        {
            SimDroppedItem[] wire = { Drop(7), Drop(7) };
            string reason;
            Assert.That(SimDroppedItemSetRules.TryValidateWireSet(wire, out reason), Is.False);
            Assert.That(reason, Does.Contain("duplicate entity hash"));
        }

        [Test]
        public void Validate_RefusesNull()
        {
            string reason;
            Assert.That(SimDroppedItemSetRules.TryValidateWireSet(null, out reason), Is.False);
        }

        [Test]
        public void Plan_EmptyWireAndEmptyLocal_NothingHappens()
        {
            SimDroppedItemSetRules.Plan plan =
                SimDroppedItemSetRules.ComputePlan(Array.Empty<SimDroppedItem>(), Array.Empty<ulong>());

            Assert.That(plan.AddedWireIndices, Is.Empty);
            Assert.That(plan.RemovedLocalIndices, Is.Empty);
            Assert.That(plan.KeptWireIndices, Is.Empty);
        }

        [Test]
        public void Plan_WireEntryTheLocalRoomLacks_IsAdded()
        {
            SimDroppedItem[] wire = { Drop(10), Drop(11) };
            var local = new ulong[] { 10 };

            SimDroppedItemSetRules.Plan plan = SimDroppedItemSetRules.ComputePlan(wire, local);

            Assert.That(plan.AddedWireIndices, Is.EqualTo(new[] { 1 }));
            Assert.That(plan.KeptWireIndices, Is.EqualTo(new[] { 0 }));
            Assert.That(plan.RemovedLocalIndices, Is.Empty);
        }

        [Test]
        public void Plan_LocalRecordTheWireOmits_IsRemoved()
        {
            // The full-replace half: a record the host no longer reports is GONE. A merge receiver
            // would keep it forever, which is exactly how a taken item reappears on the client.
            SimDroppedItem[] wire = { Drop(10) };
            var local = new ulong[] { 10, 42 };

            SimDroppedItemSetRules.Plan plan = SimDroppedItemSetRules.ComputePlan(wire, local);

            Assert.That(plan.RemovedLocalIndices, Is.EqualTo(new[] { 1 }));
            Assert.That(plan.KeptWireIndices, Is.EqualTo(new[] { 0 }));
            Assert.That(plan.AddedWireIndices, Is.Empty);
        }

        [Test]
        public void Plan_EmptyWire_RemovesEveryLocalRecord()
        {
            SimDroppedItem[] wire = Array.Empty<SimDroppedItem>();
            var local = new ulong[] { 1, 2, 3 };

            SimDroppedItemSetRules.Plan plan = SimDroppedItemSetRules.ComputePlan(wire, local);

            Assert.That(plan.RemovedLocalIndices, Is.EqualTo(new[] { 0, 1, 2 }));
            Assert.That(plan.AddedWireIndices, Is.Empty);
            Assert.That(plan.KeptWireIndices, Is.Empty);
        }

        [Test]
        public void Plan_IsIdempotentUnderARepeatedWireSet()
        {
            // A lost snapshot is repaired by the next one only if applying the same set twice is a
            // no-op the second time.
            SimDroppedItem[] wire = { Drop(5), Drop(6) };
            var localAfterFirstApply = new ulong[] { 5, 6 };

            SimDroppedItemSetRules.Plan second =
                SimDroppedItemSetRules.ComputePlan(wire, localAfterFirstApply);

            Assert.That(second.AddedWireIndices, Is.Empty);
            Assert.That(second.RemovedLocalIndices, Is.Empty);
            Assert.That(second.KeptWireIndices, Is.EqualTo(new[] { 0, 1 }));
        }

        [Test]
        public void Plan_IgnoresNullLocalSlots()
        {
            // A defensive null in the room's record list must not crash the reconcile; its slot is
            // simply "not on the wire" and lands in the removal set.
            SimDroppedItem[] wire = Array.Empty<SimDroppedItem>();
            var local = new ulong[] { 0 };

            SimDroppedItemSetRules.Plan plan = SimDroppedItemSetRules.ComputePlan(wire, local);

            Assert.That(plan.RemovedLocalIndices, Is.EqualTo(new[] { 0 }));
        }
    }
}
