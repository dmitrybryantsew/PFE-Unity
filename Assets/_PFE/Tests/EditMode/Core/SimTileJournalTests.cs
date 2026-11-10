using System;
using NUnit.Framework;
using PFE.Sim.Replication;
using PFE.Sim.State;

namespace PFE.Tests.EditMode.Core
{
    /// <summary>
    /// The host-side producer of §10.2 gap 1: <see cref="SimTileJournal"/>.
    ///
    /// <para><b>What these tests are for.</b> The journal is the half of the reliable tile stream that
    /// decides <i>what gets sent</i>. A journal that emits a redundant event is wasteful; one that
    /// omits a real mutation is a world that disagrees with itself forever, and no receiver test can
    /// catch that — the receiver never sees the missing event. So the assertions here are about
    /// emission and omission, not about admission.</para>
    ///
    /// <para><b>Three rules carry the design, and each has a test that fails if it is dropped:</b>
    /// a redundant notification is not an event; the baseline is the divergence from generated
    /// geometry, not a dump; and an acknowledged prefix is trimmed without renumbering the stream.
    /// The last test drives the real <see cref="SimSnapshotReceiver"/> with the real journal, because
    /// producer and consumer agreeing by construction is the only property that cannot be checked
    /// from either side alone.</para>
    /// </summary>
    [TestFixture]
    public sealed class SimTileJournalTests
    {
        private const ulong RoomA = 0xAAAA000000000001;
        private const ulong RoomB = 0xBBBB000000000001;
        private const long Revision = 31;

        // ── Builders ──────────────────────────────────────────────────────────

        private static SimRoomDelta Room(ulong roomHash)
        {
            return new SimRoomDelta
            {
                RoomHash = roomHash,
                LifecycleSequence = 1,
                Units = Array.Empty<SimUnitState>(),
                Motors = Array.Empty<SimMotorState>(),
                Projectiles = Array.Empty<SimProjectileState>(),
                DroppedItems = Array.Empty<SimDroppedItem>(),
                RemovedEntityHashes = Array.Empty<ulong>(),
            };
        }

        private static SimSnapshot Snapshot(uint sequence, SimTileJournal journal)
        {
            return new SimSnapshot
            {
                ProtocolVersion = SimSnapshotSerializer.CurrentProtocolVersion,
                TickIndex = sequence * 10L,
                SnapshotSequence = sequence,
                WorldRevision = journal.WorldRevision,
                BaselineHash = 0UL,
                Rooms = new[] { Room(RoomA) },
                TileMutations = journal.ToArray(),
            };
        }

        // ── Emission and omission ─────────────────────────────────────────────

        [Test]
        public void Seed_IsSilent()
        {
            var journal = new SimTileJournal();
            journal.BeginLifetime(Revision);

            journal.Seed(RoomA, 3, 4, 2, 10);
            journal.Seed(RoomA, 5, 5, 2, 10);

            Assert.AreEqual(0, journal.JournalCount,
                "Seeding tells the journal what the client already generated; it is not a change.");
            Assert.AreEqual(0u, journal.LastSequence);
            Assert.AreEqual(2, journal.KnownTileCount);
            Assert.AreEqual(0, journal.DivergedTileCount);
        }

        [Test]
        public void Record_EmitsOnlyWhenTheStateActuallyChanged()
        {
            var journal = new SimTileJournal();
            journal.BeginLifetime(Revision);
            journal.Seed(RoomA, 3, 4, 2, 10);

            // RoomInstance.TilesMutated reports a region with a one-tile border, and the border tiles
            // did not change. Emitting them would put every neighbour of a damaged tile on the wire.
            Assert.IsFalse(journal.Record(RoomA, 3, 4, 2, 10),
                "An unchanged observation must not produce an event.");
            Assert.AreEqual(0, journal.JournalCount);

            Assert.IsTrue(journal.Record(RoomA, 3, 4, 0, 0));
            Assert.AreEqual(1, journal.JournalCount);

            SimTileMutation emitted = journal.ToArray()[0];
            Assert.AreEqual(1u, emitted.EventSequence, "The first event of a lifetime is sequence 1.");
            Assert.AreEqual(Revision, emitted.WorldRevision, "Every event is stamped with its lifetime.");
            Assert.AreEqual(RoomA, emitted.RoomHash);
            Assert.AreEqual(3, emitted.TileX);
            Assert.AreEqual(4, emitted.TileY);
            Assert.AreEqual(0, emitted.PhysicsType, "Air is a real state: a destroyed tile must be sent.");
            Assert.AreEqual(0, emitted.HitPoints);
        }

        [Test]
        public void Record_OnAnUnseededCell_EmitsTheChangeAndKeepsItInEveryBaseline()
        {
            var journal = new SimTileJournal();

            Assert.IsTrue(journal.Record(RoomA, 7, 7, 0, 0),
                "A reported change must be emitted even if the journal was never told the initial state; "
                + "refusing would drop a real mutation.");
            Assert.AreEqual(1, journal.JournalCount);

            // The journal cannot prove the client generated (2,10) at this cell, so it may not claim the
            // peer is in sync: treating the first observation as "initial" would erase the mutation from
            // every later baseline and the destroyed tile would reappear for a reconnecting peer.
            Assert.AreEqual(1, journal.DivergedTileCount,
                "An unseeded cell is never considered in sync.");

            journal.BeginLifetime(Revision);
            Assert.AreEqual(1, journal.JournalCount,
                "The new lifetime's baseline must still carry the cell it never learned the initial "
                + "state of.");
            Assert.AreEqual(0, journal.ToArray()[0].PhysicsType);
        }

        [Test]
        public void ZeroRoomHash_IsIgnored()
        {
            var journal = new SimTileJournal();

            journal.Seed(0UL, 1, 1, 2, 10);
            Assert.IsFalse(journal.Record(0UL, 1, 1, 0, 0));

            Assert.AreEqual(0, journal.KnownTileCount, "RoomHash 0 is the 'no room' sentinel.");
            Assert.AreEqual(0, journal.JournalCount);
        }

        // ── The baseline is a divergence, not a dump ──────────────────────────

        [Test]
        public void BeginLifetime_OnAnUntouchedWorld_EmitsNothing()
        {
            var journal = new SimTileJournal();
            journal.Seed(RoomA, 1, 1, 2, 10);
            journal.Seed(RoomA, 2, 2, 2, 10);
            journal.Seed(RoomB, 1, 1, 2, 10);

            journal.BeginLifetime(Revision);

            Assert.AreEqual(0, journal.JournalCount,
                "A client generates the same rooms from the same template, so an untouched room's "
                + "baseline is empty. A full dump would be tens of thousands of events carrying no "
                + "information the peer did not already have.");
            Assert.AreEqual(0u, journal.LastSequence);
            Assert.AreEqual(3, journal.KnownTileCount, "Seeding still records the state for later diffs.");
        }

        [Test]
        public void BeginLifetime_EmitsOnlyTheDivergenceInDeterministicOrder()
        {
            var journal = new SimTileJournal();
            journal.Seed(RoomB, 1, 1, 2, 10);
            journal.Seed(RoomA, 3, 3, 2, 10);
            journal.Seed(RoomA, 1, 1, 2, 10);
            journal.Seed(RoomA, 2, 2, 2, 10); // untouched: must not appear

            // Recorded out of order on purpose: two hosts must emit identical bytes for identical
            // worlds, so the baseline is sorted by (room, x, y) rather than by mutation time.
            journal.Record(RoomB, 1, 1, 0, 0);
            journal.Record(RoomA, 3, 3, 0, 0);
            journal.Record(RoomA, 1, 1, 0, 0);

            journal.BeginLifetime(Revision);
            SimTileMutation[] baseline = journal.ToArray();

            Assert.AreEqual(3, baseline.Length, "Only the three changed cells belong in the baseline.");
            Assert.AreEqual(1u, baseline[0].EventSequence);
            Assert.AreEqual(2u, baseline[1].EventSequence);
            Assert.AreEqual(3u, baseline[2].EventSequence);
            foreach (SimTileMutation mutation in baseline)
            {
                Assert.AreEqual(Revision, mutation.WorldRevision,
                    "A baseline event carries the lifetime it belongs to, not the one it was recorded in.");
            }

            Assert.AreEqual(RoomA, baseline[0].RoomHash);
            Assert.AreEqual(1, baseline[0].TileX);
            Assert.AreEqual(RoomA, baseline[1].RoomHash);
            Assert.AreEqual(3, baseline[1].TileX);
            Assert.AreEqual(RoomB, baseline[2].RoomHash,
                "Rooms are ordered by hash, then by x, then by y.");
            Assert.AreEqual(0, baseline[1].PhysicsType, "A baseline must carry the current state.");
        }

        [Test]
        public void BeginLifetime_OnANewRevision_RestartsTheSequenceAndRebuildsTheBaseline()
        {
            var journal = new SimTileJournal();
            journal.Seed(RoomA, 1, 1, 2, 10);
            journal.Seed(RoomA, 2, 2, 2, 10);
            journal.BeginLifetime(Revision);
            journal.Record(RoomA, 1, 1, 0, 0);
            journal.Record(RoomA, 2, 2, 0, 0);
            Assert.AreEqual(2u, journal.LastSequence);

            journal.BeginLifetime(Revision + 1);

            Assert.AreEqual(Revision + 1, journal.WorldRevision);
            Assert.AreEqual(2, journal.JournalCount, "The new lifetime re-emits every diverged cell.");
            Assert.AreEqual(1u, journal.FirstSequence, "A new lifetime restarts the sequence at 1.");
            Assert.AreEqual(2u, journal.LastSequence);
            Assert.AreEqual(0u, journal.AckedSequence, "An ack belongs to the lifetime that produced it.");
        }

        [Test]
        public void BeginLifetime_WithTheSameRevision_KeepsTheJournal()
        {
            var journal = new SimTileJournal();
            journal.BeginLifetime(Revision);
            journal.Seed(RoomA, 1, 1, 2, 10);
            journal.Record(RoomA, 1, 1, 0, 0); // sequence 1 — the tile is destroyed
            journal.Record(RoomA, 1, 1, 2, 3); // sequence 2 — the same cell, hit again

            Assert.AreEqual(2, journal.JournalCount);

            journal.BeginLifetime(Revision);

            // Two events, one cell: a rebuild would collapse them into a single baseline event. The
            // peer has not acked either event, so a caller that begins the same lifetime twice must
            // not throw them away — it would have to resend them from a restarted sequence.
            Assert.AreEqual(2, journal.JournalCount,
                "Repeating a lifetime must keep the events recorded since the first call.");
            Assert.AreEqual(2u, journal.LastSequence);
        }

        // ── The seed rule that makes a reloaded room correct ──────────────────

        [Test]
        public void Seed_DoesNotForgetAMutationWhenTheRoomIsReactivated()
        {
            var journal = new SimTileJournal();
            journal.Seed(RoomA, 4, 4, 2, 10);
            journal.BeginLifetime(Revision);
            journal.Record(RoomA, 4, 4, 0, 0); // the host blows the wall up

            // The room leaves the active set and comes back restored: the tile is regenerated and the
            // save re-applies the mutation, so the journal is seeded with the state it already holds.
            journal.Seed(RoomA, 4, 4, 0, 0);

            Assert.AreEqual(1, journal.DivergedTileCount,
                "Re-seeding refreshes the current state; it must not redefine what 'generated' means, "
                + "or the returning player would find the wall intact.");

            journal.BeginLifetime(Revision + 1);
            Assert.AreEqual(1, journal.JournalCount, "The restored mutation is still a divergence.");
            Assert.AreEqual(0, journal.ToArray()[0].PhysicsType);
        }

        // ── Ack ───────────────────────────────────────────────────────────────

        [Test]
        public void Acknowledge_TrimsThePrefixAndClampsToTheJournal()
        {
            var journal = new SimTileJournal();
            journal.BeginLifetime(Revision);
            journal.Seed(RoomA, 1, 1, 2, 10);
            journal.Seed(RoomA, 2, 2, 2, 10);
            journal.Seed(RoomA, 3, 3, 2, 10);
            journal.Record(RoomA, 1, 1, 0, 0);
            journal.Record(RoomA, 2, 2, 0, 0);
            journal.Record(RoomA, 3, 3, 0, 0);

            journal.Acknowledge(2);

            Assert.AreEqual(1, journal.JournalCount, "The acknowledged prefix is dropped.");
            Assert.AreEqual(3u, journal.FirstSequence, "What remains is a suffix, not a renumbered stream.");
            Assert.AreEqual(2u, journal.AckedSequence);

            journal.Acknowledge(1);
            Assert.AreEqual(1, journal.JournalCount, "An older ack must not resurrect a trimmed event.");
            Assert.AreEqual(2u, journal.AckedSequence);

            journal.Acknowledge(99u);
            Assert.AreEqual(0, journal.JournalCount);
            Assert.AreEqual(3u, journal.AckedSequence,
                "An ack beyond the journal is clamped to LastSequence: it cannot discard an event that "
                + "has not been sent yet.");
            Assert.AreEqual(3u, journal.LastSequence);
        }

        [Test]
        public void Acknowledge_DoesNotRestartTheSequence()
        {
            var journal = new SimTileJournal();
            journal.BeginLifetime(Revision);
            journal.Seed(RoomA, 1, 1, 2, 10);
            journal.Seed(RoomA, 2, 2, 2, 10);
            journal.Record(RoomA, 1, 1, 0, 0);
            journal.Acknowledge(1u);

            journal.Record(RoomA, 2, 2, 0, 0);

            Assert.AreEqual(2u, journal.LastSequence,
                "Trimming must not renumber: the receiver continues from its own LastEventSequence + 1, "
                + "so a restarted sequence would look like a duplicate.");
            Assert.AreEqual(2u, journal.FirstSequence);
        }

        // ── Readbacks ─────────────────────────────────────────────────────────

        [Test]
        public void TryGetCurrent_ReportsStateAndAbsence()
        {
            var journal = new SimTileJournal();
            journal.Seed(RoomA, 3, 4, 2, 10);

            Assert.IsTrue(journal.TryGetCurrent(RoomA, 3, 4, out int physicsType, out int hitPoints));
            Assert.AreEqual(2, physicsType);
            Assert.AreEqual(10, hitPoints);

            // Absent control: the readback is a lookup, not a constant success.
            Assert.IsFalse(journal.TryGetCurrent(RoomA, 9, 9, out _, out _),
                "TryGetCurrent must report absence for a cell the journal never saw.");
            Assert.IsFalse(journal.TryGetCurrent(RoomB, 3, 4, out _, out _),
                "The same coordinates in another room are another cell.");
        }

        [Test]
        public void CopyJournalTo_ReplacesTheDestination()
        {
            var journal = new SimTileJournal();
            journal.BeginLifetime(Revision);
            journal.Seed(RoomA, 1, 1, 2, 10);
            journal.Record(RoomA, 1, 1, 0, 0);

            var destination = new System.Collections.Generic.List<SimTileMutation>
            {
                new SimTileMutation { EventSequence = 99u },
                new SimTileMutation { EventSequence = 100u },
            };
            journal.CopyJournalTo(destination);

            Assert.AreEqual(1, destination.Count, "The destination is replaced, not appended to.");
            Assert.AreEqual(1u, destination[0].EventSequence);
        }

        [Test]
        public void ToArray_ReturnsACopy()
        {
            var journal = new SimTileJournal();
            journal.BeginLifetime(Revision);
            journal.Seed(RoomA, 1, 1, 2, 10);
            journal.Record(RoomA, 1, 1, 0, 0);

            SimTileMutation[] first = journal.ToArray();
            first[0].PhysicsType = 7;

            Assert.AreEqual(0, journal.ToArray()[0].PhysicsType,
                "A caller that edits the returned array must not be able to rewrite the journal.");
        }

        [Test]
        public void Version_AdvancesOnEveryMutation()
        {
            var journal = new SimTileJournal();
            journal.BeginLifetime(Revision);
            int afterLifetime = journal.Version;

            journal.Seed(RoomA, 1, 1, 2, 10);
            int afterSeed = journal.Version;
            Assert.Greater(afterSeed, afterLifetime, "Seeding changes what a baseline would contain.");

            journal.Record(RoomA, 1, 1, 0, 0);
            int afterRecord = journal.Version;
            Assert.Greater(afterRecord, afterSeed);

            journal.Record(RoomA, 1, 1, 0, 0); // no change
            Assert.AreEqual(afterRecord, journal.Version,
                "A suppressed event must not invalidate the projector's cached array.");

            journal.Acknowledge(1u);
            Assert.Greater(journal.Version, afterRecord, "Trimming changes the journal the host sends.");
        }

        // ── Producer and consumer, driven together ───────────────────────────

        [Test]
        public void Journal_AndReceiver_AgreeAcrossAckAndBaseline()
        {
            var journal = new SimTileJournal();
            journal.BeginLifetime(Revision);
            journal.Seed(RoomA, 3, 4, 2, 10);
            journal.Seed(RoomA, 5, 5, 2, 10);
            journal.Record(RoomA, 3, 4, 0, 0); // sequence 1
            journal.Record(RoomA, 5, 5, 0, 0); // sequence 2

            var receiver = new SimSnapshotReceiver();

            SimSnapshot first = Snapshot(1u, journal);
            Assert.IsTrue(receiver.Accept(in first).Applied);
            Assert.AreEqual(2, receiver.TileCount);
            Assert.AreEqual(2u, receiver.LastEventSequence);

            // The host repeats the journal until an ack exists; the receiver must not apply twice.
            SimSnapshot repeat = Snapshot(2u, journal);
            SimSnapshotReceipt duplicated = receiver.Accept(in repeat);
            Assert.IsTrue(duplicated.Applied);
            Assert.AreEqual(0, duplicated.TileEventsApplied);
            Assert.AreEqual(2, duplicated.TileEventsDuplicated);

            // The peer acknowledges what it applied, the host trims, and a later mutation is a suffix.
            journal.Acknowledge(receiver.LastEventSequence);
            Assert.AreEqual(0, journal.JournalCount, "Everything the peer acked is trimmed.");
            journal.Seed(RoomA, 7, 7, 2, 10);
            journal.Record(RoomA, 7, 7, 0, 0); // sequence 3

            SimSnapshot suffix = Snapshot(3u, journal);
            SimSnapshotReceipt applied = receiver.Accept(in suffix);
            Assert.IsTrue(applied.Applied,
                "A trimmed journal is a legal suffix starting at the receiver's next sequence.");
            Assert.AreEqual(1, applied.TileEventsApplied);
            Assert.AreEqual(3u, receiver.LastEventSequence);
            Assert.AreEqual(3, receiver.TileCount);

            // A new lifetime: the journal rebuilds the divergence baseline and the receiver clears first.
            journal.BeginLifetime(Revision + 1);
            SimSnapshot nextLifetime = Snapshot(4u, journal);
            SimSnapshotReceipt fresh = receiver.Accept(in nextLifetime);

            Assert.IsTrue(fresh.Applied);
            Assert.AreEqual(3, fresh.TileEventsApplied,
                "Every diverged cell is re-emitted for the new lifetime.");
            Assert.AreEqual(3, receiver.TileCount);
            Assert.AreEqual(3u, receiver.LastEventSequence);
            Assert.AreEqual(Revision + 1, receiver.WorldRevision);
        }
    }
}
