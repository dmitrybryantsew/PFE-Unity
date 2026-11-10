using NUnit.Framework;
using PFE.Core.Ids;
using PFE.Sim.Replication;
using PFE.Sim.State;

namespace PFE.Tests.Editor.Core
{
    /// <summary>
    /// Phase 3.3 (F-06): the arithmetic behind the host-side snapshot projection.
    ///
    /// <para><b>Why this is a separate fixture from <see cref="SimStalenessRulesTests"/>.</b> Those two
    /// classes answer different questions about the same header. <c>SimStalenessRules</c> decides whether
    /// an incoming snapshot may replace the held one; this one decides what the produced snapshot's
    /// header <i>says</i>. A guard tested only against hand-written header values would be green while
    /// the producer emitted a constant, so the producer's arithmetic gets its own cases — including the
    /// one that matters most, which is that <c>WorldRevision</c> must <b>not</b> advance per tick.</para>
    /// </summary>
    [TestFixture]
    public class SimSnapshotProjectionRulesTests
    {
        // ── Room key ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The key is the one place a room's identity is spelled. It has to be the same string the
        /// streaming manager's presence rules use, because a delta addresses a room by this number:
        /// two spellings would produce a snapshot that deserializes cleanly and reconciles against
        /// nothing.
        /// </summary>
        [Test]
        public void RoomKey_IsTheStreamingManagersKeyAndIsNeverZero()
        {
            Assert.AreEqual(
                EntityId.ComputeFnv1a64("room:cellar"),
                SimSnapshotProjectionRules.RoomKey("cellar"),
                "The key must be the documented 'room:' + id FNV-1a, not some other spelling.");

            Assert.AreEqual(
                SimSnapshotProjectionRules.RoomKey(string.Empty),
                SimSnapshotProjectionRules.RoomKey(null),
                "A null id must key exactly as an empty one; the streaming manager passes null for a "
                + "missing room and an unguarded concat would produce the literal 'room:' + nothing "
                + "either way, so the two must not diverge.");

            Assert.AreNotEqual(0UL, SimSnapshotProjectionRules.RoomKey(string.Empty),
                "0 is SimRoomDelta's 'no room' value; the prefix exists so no real room can hash to it.");

            Assert.AreNotEqual(
                SimSnapshotProjectionRules.RoomKey("cellar"),
                SimSnapshotProjectionRules.RoomKey("cellar2"),
                "Distinct rooms must not collide, or two deltas would address one room.");
        }

        // ── Snapshot sequence ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// The producer increments with the same wrap-safe semantics the guard compares with. Pinning
        /// the pair together is the point: a producer that saturated at <c>uint.MaxValue</c> and a guard
        /// that accepts the wrap would deadlock the channel at rollover.
        /// </summary>
        [Test]
        public void NextSnapshotSequence_WrapsAndTheGuardReadsTheWrappedValueAsNewer()
        {
            Assert.AreEqual(1u, SimSnapshotProjectionRules.NextSnapshotSequence(0u));
            Assert.AreEqual(0u, SimSnapshotProjectionRules.NextSnapshotSequence(uint.MaxValue),
                "The producer must roll over, not saturate.");

            Assert.IsTrue(
                SimStalenessRules.IsNewer(
                    SimSnapshotProjectionRules.NextSnapshotSequence(uint.MaxValue),
                    uint.MaxValue),
                "The producer's own successor must pass the guard's wrap-safe compare.");
        }

        /// <summary>
        /// The reliable-event counter is a <b>separate</b> sequence from the snapshot one, and its first
        /// value is the value the receiver admits. Both halves are pinned here because a producer that
        /// starts its journal at 0 — or shares the snapshot counter — is rejected as
        /// <c>CorruptTileJournal</c> with no other symptom: the payload looks well-formed and every
        /// field is plausible.
        /// </summary>
        [Test]
        public void NextReliableEventSequence_StartsWhereTheReceiverAdmitsAndWraps()
        {
            Assert.AreEqual(SimSnapshotReceiver.FirstReliableEventSequence,
                SimSnapshotProjectionRules.NextReliableEventSequence(
                    SimSnapshotReceiver.FirstReliableEventSequence - 1u),
                "The producer's first event sequence must be the one the receiver's journal starts at. "
                + "A 0-based journal is refused, and the refusal looks like corruption, not an offset.");

            Assert.AreEqual(0u, SimSnapshotProjectionRules.NextReliableEventSequence(uint.MaxValue),
                "The reliable counter must roll over, not saturate — the same rule as the snapshot one.");

            Assert.IsTrue(
                SimStalenessRules.IsNewer(
                    SimSnapshotProjectionRules.NextReliableEventSequence(uint.MaxValue),
                    uint.MaxValue),
                "A wrapped reliable sequence must read as newer, or the channel deadlocks at rollover.");
        }

        // ── World revision ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <b>The one that would silently break stale-delta rejection.</b> A revision that advances every
        /// tick makes every snapshot claim a new world lifetime, so <c>AcceptSnapshot</c> would accept a
        /// packet from a previous room's state — the exact failure <c>WorldRevision</c> exists to stop.
        /// </summary>
        [Test]
        public void NextWorldRevision_DoesNotAdvanceWithoutARoomSetChange()
        {
            const long revision = 7L;

            Assert.AreEqual(revision, SimSnapshotProjectionRules.NextWorldRevision(revision, false),
                "A tick with an unchanged room set must keep the revision; bumping it per tick turns "
                + "the stale-delta guard into a no-op.");

            Assert.AreEqual(revision + 1, SimSnapshotProjectionRules.NextWorldRevision(revision, true));
        }

        /// <summary>
        /// A revision bump must outrank any sequence value, so the guard has to accept the new revision
        /// even when the sequence looks older — the case that makes the field load-bearing at all.
        /// </summary>
        [Test]
        public void NextWorldRevision_BumpedRevisionOutranksAWrappedBackSequence()
        {
            long revision = SimSnapshotProjectionRules.NextWorldRevision(3L, true);
            uint sequence = SimSnapshotProjectionRules.NextSnapshotSequence(uint.MaxValue);   // 0

            Assert.IsTrue(
                SimStalenessRules.AcceptSnapshot(sequence, revision, uint.MaxValue, 3L),
                "Revision " + revision + " with sequence " + sequence + " must beat revision 3 with "
                + "sequence uint.MaxValue.");
        }

        // ── Room-set signature ───────────────────────────────────────────────────────────────

        /// <summary>
        /// The fold must be order-independent. <c>ActiveRooms</c> is a <c>HashSet</c>, whose enumeration
        /// order is unspecified and may differ between ticks without the set changing; an
        /// order-dependent fold would then report a change, bump <c>WorldRevision</c> and invalidate
        /// every in-flight delta for no reason.
        /// </summary>
        [Test]
        public void FoldRoomSetSignature_IsOrderIndependent()
        {
            ulong a = SimSnapshotProjectionRules.RoomKey("a");
            ulong b = SimSnapshotProjectionRules.RoomKey("b");
            ulong c = SimSnapshotProjectionRules.RoomKey("c");

            ulong forward = SimSnapshotProjectionRules.FoldRoomSetSignature(
                SimSnapshotProjectionRules.FoldRoomSetSignature(
                    SimSnapshotProjectionRules.FoldRoomSetSignature(
                        SimSnapshotProjectionRules.EmptyRoomSetSignature, a), b), c);

            ulong reversed = SimSnapshotProjectionRules.FoldRoomSetSignature(
                SimSnapshotProjectionRules.FoldRoomSetSignature(
                    SimSnapshotProjectionRules.FoldRoomSetSignature(
                        SimSnapshotProjectionRules.EmptyRoomSetSignature, c), b), a);

            Assert.AreEqual(forward, reversed,
                "The signature must not depend on enumeration order.");
        }

        /// <summary>
        /// A membership change must move the signature, and removing the member must restore the previous
        /// value — that is what makes "did the room set change" answerable with one comparison.
        /// </summary>
        [Test]
        public void FoldRoomSetSignature_DetectsAMembershipChangeAndRestoresOnRemoval()
        {
            ulong a = SimSnapshotProjectionRules.RoomKey("a");
            ulong b = SimSnapshotProjectionRules.RoomKey("b");

            ulong withA = SimSnapshotProjectionRules.FoldRoomSetSignature(
                SimSnapshotProjectionRules.EmptyRoomSetSignature, a);
            ulong withAB = SimSnapshotProjectionRules.FoldRoomSetSignature(withA, b);

            Assert.AreNotEqual(withA, withAB, "Adding a room must change the signature.");

            ulong backToA = SimSnapshotProjectionRules.FoldRoomSetSignature(withAB, b);
            Assert.AreEqual(withA, backToA,
                "Folding the same key again must undo it — that is how a deactivation is expressed.");
        }

        /// <summary>
        /// The honest limit of XOR, pinned so it cannot be discovered in a live match: two rooms with the
        /// <b>same id</b> fold to nothing, so a duplicate-keyed pair is indistinguishable from one room.
        /// That is acceptable only because room ids are unique per map; if they ever are not, the
        /// signature is the wrong structure and this test is where that shows up.
        /// </summary>
        [Test]
        public void FoldRoomSetSignature_DuplicateKeysCancel_DocumentedLimit()
        {
            ulong a = SimSnapshotProjectionRules.RoomKey("a");

            ulong twice = SimSnapshotProjectionRules.FoldRoomSetSignature(
                SimSnapshotProjectionRules.FoldRoomSetSignature(
                    SimSnapshotProjectionRules.EmptyRoomSetSignature, a), a);

            Assert.AreEqual(SimSnapshotProjectionRules.EmptyRoomSetSignature, twice,
                "Two identical room keys cancel. Room ids must therefore stay unique per map; this "
                + "assertion is the tripwire, not a desirable property.");
        }

        // ── §10.2 gap 2: the content key of a loot stack ─────────────────────

        /// <summary>
        /// <c>ItemDefinition</c> and <c>WeaponDefinition</c> are disjoint tables whose ids are not
        /// namespaced, so the key has to carry which table the id belongs to. Without this, an item and a
        /// weapon sharing an id become one stack on the receiver — and unlike <c>IsWeapon</c>, the hash
        /// cannot be recovered from the wire once it is wrong.
        /// </summary>
        [Test]
        public void LootItemKey_SeparatesTheTwoContentTables()
        {
            ulong asItem = SimSnapshotProjectionRules.LootItemKey("medkit", false);
            ulong asWeapon = SimSnapshotProjectionRules.LootItemKey("medkit", true);

            Assert.AreNotEqual(asItem, asWeapon,
                "The same id in the two content tables is two different things; one key for both is the "
                + "conflation IsWeapon exists to prevent.");
            Assert.AreNotEqual(0UL, asItem);
            Assert.AreNotEqual(0UL, asWeapon,
                "A zero content key is as unreadable as a zero entity id.");
        }

        [Test]
        public void LootItemKey_IsStableAndNotEmptyForAMissingId()
        {
            Assert.AreEqual(SimSnapshotProjectionRules.LootItemKey("pistol", true),
                SimSnapshotProjectionRules.LootItemKey("pistol", true),
                "Two hosts projecting the same stack must produce the same key.");

            Assert.AreNotEqual(SimSnapshotProjectionRules.LootItemKey("pistol", true),
                SimSnapshotProjectionRules.LootItemKey("pistol ", true),
                "The key is over the id verbatim: trimming would merge two ids that the content set "
                + "treats as distinct.");

            Assert.AreNotEqual(0UL, SimSnapshotProjectionRules.LootItemKey(null, false),
                "The prefix keeps the key non-zero even for a missing id, so a malformed record is "
                + "still addressable rather than colliding with the 'no identity' sentinel.");
        }
    }
}
