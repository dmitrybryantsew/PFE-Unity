using System;
using NUnit.Framework;
using PFE.Core.Ids;

namespace PFE.Tests.EditMode.Core
{
    /// <summary>
    /// §10.2 gap 2, producing side: <see cref="DroppedItemIdentityRules"/> is the rule that gives every
    /// floor-loot record an id exactly once.
    ///
    /// <para><b>Why this is a pure fixture and not a room test.</b> The mint is called from
    /// <c>RoomInstance.AddDroppedItem</c>, which needs a room, which needs the editor — a test written
    /// there would never run in this project's offline harness, and an unexecuted guard is the thing
    /// these fixtures exist to avoid. The decision itself is pure, so it is tested here; a source lint
    /// (<c>DroppedItemIdentityLintTests</c>) pins the one thing that cannot be checked offline, namely
    /// that the room actually calls this.</para>
    ///
    /// <para><b>Both halves are asserted.</b> "It mints when absent" passes on an implementation that
    /// mints <i>always</i>, which would give one stack a new identity on every room rebuild — the same
    /// as having no identity. So the keep path is its own test with a mint that fails the test if it is
    /// ever invoked.</para>
    /// </summary>
    [TestFixture]
    public sealed class DroppedItemIdentityRulesTests
    {
        private const ulong Existing = 0xABCD000000000001;

        [Test]
        public void AnExistingId_IsKeptAndNothingIsMinted()
        {
            int minted = 0;

            ulong resolved = DroppedItemIdentityRules.Resolve(Existing, () =>
            {
                minted++;
                return new EntityId(0xFFFF000000000001, "runtime:9:9");
            });

            Assert.AreEqual(Existing, resolved,
                "A record that already has an id must keep it: re-adding a record is not a re-identity.");
            Assert.AreEqual(0, minted,
                "Minting on every add would give one stack a new identity every time a room was "
                + "rebuilt, which is indistinguishable from having no identity.");
        }

        [Test]
        public void AnAbsentId_IsMintedOnce()
        {
            int minted = 0;

            ulong resolved = DroppedItemIdentityRules.Resolve(DroppedItemIdentityRules.Unassigned, () =>
            {
                minted++;
                return new EntityId(0x1234000000000001, "runtime:42:0");
            });

            Assert.AreEqual(0x1234000000000001UL, resolved);
            Assert.AreEqual(1, minted);
            Assert.AreNotEqual(DroppedItemIdentityRules.Unassigned, resolved,
                "Zero is the receiver's 'this stack has no identity' sentinel.");
        }

        [Test]
        public void AnInvalidMint_LeavesTheRecordUnassigned()
        {
            ulong resolved = DroppedItemIdentityRules.Resolve(
                DroppedItemIdentityRules.Unassigned, () => EntityId.Empty);

            Assert.AreEqual(DroppedItemIdentityRules.Unassigned, resolved,
                "Storing a broken allocator's answer would turn one failure into a duplicate id later; "
                + "leaving the record unassigned keeps it visible at the one place that can refuse it.");
        }

        [Test]
        public void ANullMint_IsSafe()
        {
            Assert.AreEqual(DroppedItemIdentityRules.Unassigned,
                DroppedItemIdentityRules.Resolve(DroppedItemIdentityRules.Unassigned, null),
                "A context with no allocator must not throw from a room build.");
            Assert.AreEqual(Existing,
                DroppedItemIdentityRules.Resolve(Existing, null),
                "The keep path does not need an allocator at all.");
        }

        [Test]
        public void ConsecutiveRecords_GetDistinctIds()
        {
            // The real allocator, not a stub: the property that matters is that two stacks dropped in
            // the same tick are two stacks, and only the allocator's own counter can demonstrate that.
            var allocator = new RuntimeEntityIdAllocator();

            ulong first = DroppedItemIdentityRules.Resolve(
                DroppedItemIdentityRules.Unassigned, () => allocator.Allocate(7L));
            ulong second = DroppedItemIdentityRules.Resolve(
                DroppedItemIdentityRules.Unassigned, () => allocator.Allocate(7L));

            Assert.AreNotEqual(DroppedItemIdentityRules.Unassigned, first);
            Assert.AreNotEqual(DroppedItemIdentityRules.Unassigned, second);
            Assert.AreNotEqual(first, second,
                "Two stacks dropped in the same tick must not share an id: the receiver rejects a "
                + "duplicate id as the (content id, position) conflation §10.2 removed.");
        }

        [Test]
        public void ReResolvingTheResult_IsStable()
        {
            ulong first = DroppedItemIdentityRules.Resolve(
                DroppedItemIdentityRules.Unassigned, () => new EntityId(Existing, "runtime:1:0"));

            ulong again = DroppedItemIdentityRules.Resolve(first, () =>
            {
                Assert.Fail("A record that already carries the id must not be minted a second time.");
                return EntityId.Empty;
            });

            Assert.AreEqual(first, again);
        }
    }
}
