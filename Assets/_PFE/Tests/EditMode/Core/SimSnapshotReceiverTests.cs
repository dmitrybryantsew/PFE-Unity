using System;
using NUnit.Framework;
using PFE.Sim.Replication;
using PFE.Sim.State;

namespace PFE.Tests.EditMode.Core
{
    /// <summary>
    /// Phase-4 prerequisite, §10.2 gaps 1 and 2: the <see cref="SimSnapshotReceiver"/> is the first
    /// consumer of the two new payloads, and these tests are what make the payloads more than
    /// decoration.
    ///
    /// <para><b>Why the receiver is tested here and not through a transport.</b> A payload that is
    /// only ever serialized is unverifiable: it can be mis-ordered, mis-keyed or applied twice for a
    /// year without a symptom. The receiver is pure C#, so the offline wall can drive the real
    /// admission path — protocol, staleness, journal contiguity, duplicate identity, loot keying —
    /// without a socket or a scene.</para>
    ///
    /// <para><b>Every accept assertion is paired with a reject assertion.</b> A receiver that accepts
    /// everything passes any "it applied the snapshot" test; the contract is the refusal. Each
    /// corruption gets its own test so a regression names the guard that broke, and each reject also
    /// asserts that <i>nothing</i> moved — the atomicity claim is the one that is easy to lose while
    /// refactoring validation into the apply loop.</para>
    /// </summary>
    [TestFixture]
    public sealed class SimSnapshotReceiverTests
    {
        private const ulong RoomA = 0xAAAA000000000001;
        private const ulong RoomB = 0xBBBB000000000001;
        private const long Revision = 12;

        // ── Builders ──────────────────────────────────────────────────────────

        private static SimTileMutation Tile(uint sequence, long revision = Revision, ulong room = RoomA,
            int x = 3, int y = 4, int physicsType = 2, int hitPoints = 10)
        {
            return new SimTileMutation
            {
                RoomHash = room,
                WorldRevision = revision,
                EventSequence = sequence,
                TileX = x,
                TileY = y,
                PhysicsType = physicsType,
                HitPoints = hitPoints,
            };
        }

        private static SimDroppedItem Drop(ulong entityHash, ulong itemIdHash = 0xCCCC000000000001,
            float px = 50f, float py = 20f)
        {
            return new SimDroppedItem
            {
                EntityHash = entityHash,
                ItemIdHash = itemIdHash,
                IsWeapon = true,
                Quantity = 1,
                Variant = 0,
                Condition = 1f,
                Px = px,
                Py = py,
            };
        }

        private static SimRoomDelta Room(ulong roomHash, SimDroppedItem[] drops = null)
        {
            return new SimRoomDelta
            {
                RoomHash = roomHash,
                LifecycleSequence = 1,
                Units = Array.Empty<SimUnitState>(),
                Motors = Array.Empty<SimMotorState>(),
                Projectiles = Array.Empty<SimProjectileState>(),
                DroppedItems = drops ?? Array.Empty<SimDroppedItem>(),
                RemovedEntityHashes = Array.Empty<ulong>(),
            };
        }

        private static SimSnapshot Snapshot(uint sequence, long revision, SimTileMutation[] journal,
            SimRoomDelta[] rooms)
        {
            return new SimSnapshot
            {
                ProtocolVersion = SimSnapshotSerializer.CurrentProtocolVersion,
                TickIndex = sequence * 10L,
                SnapshotSequence = sequence,
                WorldRevision = revision,
                BaselineHash = 0UL,
                Rooms = rooms,
                TileMutations = journal,
            };
        }

        // ── The happy path, with readbacks ────────────────────────────────────

        [Test]
        public void FirstSnapshot_AppliesTheJournalAndTheLoot()
        {
            var receiver = new SimSnapshotReceiver();
            SimSnapshot snapshot = Snapshot(1, Revision,
                new[] { Tile(1, x: 3, y: 4), Tile(2, x: 4, y: 4, physicsType: 0, hitPoints: 0) },
                new[] { Room(RoomA, new[] { Drop(0x1001), Drop(0x1002) }) });

            SimSnapshotReceipt receipt = receiver.Accept(in snapshot);

            Assert.IsTrue(receipt.Applied, "The first snapshot is the baseline and must be admitted.");
            Assert.AreEqual(SimSnapshotRejectReason.None, receipt.RejectReason);
            Assert.AreEqual(2, receipt.TileEventsApplied);
            Assert.AreEqual(0, receipt.TileEventsDuplicated);
            Assert.AreEqual(2, receipt.DroppedItemsApplied);
            Assert.AreEqual(1, receipt.LootRoomsReplaced);
            Assert.AreEqual(1, receiver.AcceptedSnapshotCount);

            Assert.AreEqual(2u, receiver.LastEventSequence,
                "The receiver must record the last reliable sequence it applied, not the journal length.");
            Assert.AreEqual(Revision, receiver.WorldRevision);
            Assert.AreEqual(2, receiver.TileCount);

            Assert.IsTrue(receiver.TryGetTile(RoomA, 4, 4, out SimTileMutation applied),
                "The second event must be readable at its own cell.");
            Assert.AreEqual(0, applied.PhysicsType, "Air must be applied: a destroyed tile is not a skip.");
            Assert.AreEqual(0, applied.HitPoints);
        }

        [Test]
        public void RepeatedJournal_IsAppliedExactlyOnce()
        {
            var receiver = new SimSnapshotReceiver();
            SimTileMutation[] journal = { Tile(1, x: 3, y: 4), Tile(2, x: 4, y: 4) };
            SimRoomDelta[] rooms = { Room(RoomA, new[] { Drop(0x1001) }) };

            SimSnapshot first = Snapshot(1, Revision, journal, rooms);
            receiver.Accept(in first);

            // The host repeats the whole journal until an ack exists; the next snapshot differs only in
            // its sequence and tick.
            SimSnapshot repeat = Snapshot(2, Revision, journal, rooms);
            SimSnapshotReceipt receipt = receiver.Accept(in repeat);

            Assert.IsTrue(receipt.Applied);
            Assert.AreEqual(0, receipt.TileEventsApplied,
                "A repeated reliable event must not be applied twice — a tile would take the same "
                + "damage on every snapshot.");
            Assert.AreEqual(2, receipt.TileEventsDuplicated);
            Assert.AreEqual(2u, receiver.LastEventSequence);
            Assert.AreEqual(2, receiver.TileCount);
            Assert.AreEqual(2, receiver.AcceptedSnapshotCount);
        }

        // ── Rejections: each must be named, and must move nothing ─────────────

        [Test]
        public void JournalGap_IsRejectedAndAppliesNothing()
        {
            var receiver = new SimSnapshotReceiver();
            SimSnapshot snapshot = Snapshot(1, Revision,
                new[] { Tile(1), Tile(3) },
                new[] { Room(RoomA) });

            SimSnapshotReceipt receipt = receiver.Accept(in snapshot);

            Assert.IsFalse(receipt.Applied);
            Assert.AreEqual(SimSnapshotRejectReason.CorruptTileJournal, receipt.RejectReason,
                "A gap in a reliable stream means the geometry cannot be rebuilt; the whole snapshot "
                + "must be refused rather than partially applied.");
            Assert.AreEqual(0, receipt.TileEventsApplied);
            Assert.AreEqual(0, receiver.AcceptedSnapshotCount);
            Assert.AreEqual(0, receiver.TileCount);
            Assert.AreEqual(0u, receiver.LastEventSequence);
        }

        [Test]
        public void RevisionMismatchInsideTheJournal_IsRejected()
        {
            var receiver = new SimSnapshotReceiver();
            SimSnapshot snapshot = Snapshot(1, Revision,
                new[] { Tile(1), Tile(2, revision: Revision + 1) },
                new[] { Room(RoomA) });

            SimSnapshotReceipt receipt = receiver.Accept(in snapshot);

            Assert.AreEqual(SimSnapshotRejectReason.CorruptTileJournal, receipt.RejectReason,
                "An event stamped with another lifetime is corrupt, not late: the revision is the "
                + "lifetime key, so it cannot be applied under this one.");
            Assert.AreEqual(0, receiver.TileCount);
        }

        [Test]
        public void ZeroRoomHashInsideTheJournal_IsRejected()
        {
            var receiver = new SimSnapshotReceiver();
            SimSnapshot snapshot = Snapshot(1, Revision,
                new[] { Tile(1, room: 0UL) },
                new[] { Room(RoomA) });

            Assert.AreEqual(SimSnapshotRejectReason.CorruptTileJournal,
                receiver.Accept(in snapshot).RejectReason,
                "RoomHash 0 is the 'no room' sentinel; an event addressed to no room cannot be applied.");
        }

        [Test]
        public void ChangedDuplicate_IsRejectedWithoutPartialApplication()
        {
            var receiver = new SimSnapshotReceiver();
            SimTileMutation[] journal = { Tile(1, x: 1), Tile(2, x: 2) };
            SimRoomDelta[] rooms = { Room(RoomA) };
            SimSnapshot first = Snapshot(1, Revision, journal, rooms);
            receiver.Accept(in first);

            // Event 2 is repeated with a different payload, and a genuine event 3 follows it. A receiver
            // that validated while applying would accept event 1's duplicate, silently rewrite event 2
            // and then apply event 3 — half a geometry change from a corrupt stream.
            SimTileMutation[] tampered = { Tile(1, x: 1), Tile(2, x: 9), Tile(3, x: 3) };
            SimSnapshot second = Snapshot(2, Revision, tampered, rooms);
            SimSnapshotReceipt receipt = receiver.Accept(in second);

            Assert.AreEqual(SimSnapshotRejectReason.CorruptTileJournal, receipt.RejectReason,
                "A repeated sequence must carry the event already applied; rewriting history inside a "
                + "repeated journal is corruption.");
            Assert.AreEqual(2, receiver.TileCount, "A rejected snapshot must not add a tile.");
            Assert.AreEqual(2u, receiver.LastEventSequence, "A rejected snapshot must not advance the sequence.");

            Assert.IsTrue(receiver.TryGetTile(RoomA, 2, 4, out SimTileMutation kept));
            Assert.AreEqual(2, kept.TileX, "The applied event 2 must keep its original payload.");
            Assert.IsFalse(receiver.TryGetTile(RoomA, 3, 4, out _),
                "Event 3 must not have been applied by a snapshot that was rejected.");
        }

        [Test]
        public void TrimmedJournal_IsRejected()
        {
            var receiver = new SimSnapshotReceiver();
            SimRoomDelta[] rooms = { Room(RoomA) };
            SimSnapshot first = Snapshot(1, Revision,
                new[] { Tile(1, x: 1), Tile(2, x: 2), Tile(3, x: 3) }, rooms);
            receiver.Accept(in first);

            // Same lifetime, but the host stopped repeating the prefix. That is a broken reliable
            // stream: the receiver cannot tell a trimmed prefix from a lost event.
            SimSnapshot trimmed = Snapshot(2, Revision, new[] { Tile(1, x: 1), Tile(2, x: 2) }, rooms);
            SimSnapshotReceipt receipt = receiver.Accept(in trimmed);

            Assert.AreEqual(SimSnapshotRejectReason.CorruptTileJournal, receipt.RejectReason,
                "A journal shorter than the sequence already applied has lost its prefix.");
            Assert.AreEqual(3u, receiver.LastEventSequence);
            Assert.AreEqual(3, receiver.TileCount);
        }

        [Test]
        public void ZeroOrDuplicatedEntityHash_IsRejected()
        {
            var zero = new SimSnapshotReceiver();
            SimSnapshot zeroSnapshot = Snapshot(1, Revision,
                new[] { Tile(1) },
                new[] { Room(RoomA, new[] { Drop(0UL), Drop(0x1002) }) });

            Assert.AreEqual(SimSnapshotRejectReason.CorruptLoot,
                zero.Accept(in zeroSnapshot).RejectReason,
                "A drop with no host-assigned id would fall back to (content id, position) — the exact "
                + "conflation §10.2 removed.");
            Assert.AreEqual(0, zero.LootCount(RoomA), "A rejected loot set must not be half-installed.");

            var duplicate = new SimSnapshotReceiver();
            SimSnapshot duplicateSnapshot = Snapshot(1, Revision,
                new[] { Tile(1) },
                new[] { Room(RoomA, new[] { Drop(0x1001), Drop(0x1001, px: 51f) }) });

            Assert.AreEqual(SimSnapshotRejectReason.CorruptLoot,
                duplicate.Accept(in duplicateSnapshot).RejectReason,
                "Two drops with one id would silently collapse into one on the receiver.");
            Assert.AreEqual(0, duplicate.LootCount(RoomA));
        }

        [Test]
        public void StaleSnapshot_IsRejected()
        {
            var receiver = new SimSnapshotReceiver();
            SimRoomDelta[] rooms = { Room(RoomA) };
            SimSnapshot first = Snapshot(5, Revision, new[] { Tile(1) }, rooms);
            receiver.Accept(in first);

            SimSnapshot olderSequence = Snapshot(4, Revision, new[] { Tile(1) }, rooms);
            Assert.AreEqual(SimSnapshotRejectReason.StaleSnapshot,
                receiver.Accept(in olderSequence).RejectReason,
                "An older snapshot sequence must never overwrite newer state.");

            SimSnapshot olderRevision = Snapshot(6, Revision - 1, new[] { Tile(1) }, rooms);
            Assert.AreEqual(SimSnapshotRejectReason.StaleSnapshot,
                receiver.Accept(in olderRevision).RejectReason,
                "An older world revision is stale no matter how new its sequence looks.");

            Assert.AreEqual(1, receiver.AcceptedSnapshotCount);
            Assert.AreEqual(1u, receiver.LastEventSequence,
                "A stale snapshot must not advance the reliable sequence.");
        }

        [Test]
        public void IncompatibleProtocol_IsRejectedBeforeAnyFieldIsRead()
        {
            var receiver = new SimSnapshotReceiver();
            SimSnapshot snapshot = Snapshot(1, Revision, new[] { Tile(1) }, new[] { Room(RoomA) });
            snapshot.ProtocolVersion = SimSnapshotSerializer.CurrentProtocolVersion - 1;

            Assert.AreEqual(SimSnapshotRejectReason.IncompatibleProtocol,
                receiver.Accept(in snapshot).RejectReason,
                "A different wire version must be refused before a single field is interpreted.");
            Assert.AreEqual(0, receiver.AcceptedSnapshotCount);
            Assert.AreEqual(0, receiver.TileCount);
        }

        // ── §10.2 gap 2: identity, not content+position ───────────────────────

        [Test]
        public void IdenticalStacksOnOnePixel_AreKeyedById()
        {
            var receiver = new SimSnapshotReceiver();
            SimSnapshot snapshot = Snapshot(1, Revision,
                new[] { Tile(1) },
                new[] { Room(RoomA, new[] { Drop(0x1001), Drop(0x1002) }) });

            receiver.Accept(in snapshot);

            Assert.AreEqual(2, receiver.LootCount(RoomA),
                "Two identical stacks on the same pixel are two stacks, not one.");
            Assert.IsTrue(receiver.TryGetLoot(RoomA, 0x1001, out SimDroppedItem first));
            Assert.IsTrue(receiver.TryGetLoot(RoomA, 0x1002, out SimDroppedItem second));
            Assert.AreEqual(first.Px, second.Px);
            Assert.AreEqual(first.Py, second.Py);
            Assert.AreEqual(first.ItemIdHash, second.ItemIdHash);

            // Absent control: the readback is a lookup, not a constant success.
            Assert.IsFalse(receiver.TryGetLoot(RoomA, 0x1003, out _),
                "TryGetLoot must return false for an id that was never received.");
        }

        [Test]
        public void LootIsFullReplacePerRoom_AndAnAbsentRoomIsUnchanged()
        {
            var receiver = new SimSnapshotReceiver();
            SimTileMutation[] journal = { Tile(1) };

            SimSnapshot first = Snapshot(1, Revision, journal,
                new[]
                {
                    Room(RoomA, new[] { Drop(0x1001), Drop(0x1002) }),
                    Room(RoomB, new[] { Drop(0x2001) }),
                });
            receiver.Accept(in first);

            // Room A is replaced with one new stack; room B is absent, which means "unchanged".
            SimSnapshot second = Snapshot(2, Revision, journal,
                new[] { Room(RoomA, new[] { Drop(0x1003) }) });
            receiver.Accept(in second);

            Assert.AreEqual(1, receiver.LootCount(RoomA), "A present room's loot set is a full replace.");
            Assert.IsFalse(receiver.TryGetLoot(RoomA, 0x1001, out _),
                "A stack absent from the replacement set is gone — that is how a pickup is expressed.");
            Assert.IsTrue(receiver.TryGetLoot(RoomA, 0x1003, out _));

            Assert.AreEqual(1, receiver.LootCount(RoomB),
                "An absent room means unchanged, not emptied: interest management omits rooms.");
            Assert.IsTrue(receiver.TryGetLoot(RoomB, 0x2001, out _));
        }

        // ── Lifetime changes ─────────────────────────────────────────────────

        [Test]
        public void NewWorldRevision_ClearsGeometryEvenWithAnEmptyBaseline()
        {
            var receiver = new SimSnapshotReceiver();
            SimRoomDelta[] rooms = { Room(RoomA) };
            SimSnapshot first = Snapshot(1, Revision, new[] { Tile(1, x: 1), Tile(2, x: 2) }, rooms);
            receiver.Accept(in first);
            Assert.AreEqual(2, receiver.TileCount);

            // The host's baseline is a diff from generated geometry, so a lifetime in which no tile
            // was changed legitimately arrives with no events at all. Rejecting that would reject the
            // most common new revision — a world where nobody has broken anything yet.
            SimSnapshot emptyBaseline = Snapshot(2, Revision + 1, Array.Empty<SimTileMutation>(), rooms);
            SimSnapshotReceipt accepted = receiver.Accept(in emptyBaseline);

            Assert.IsTrue(accepted.Applied,
                "An empty journal is the correct baseline for a lifetime whose geometry is unchanged.");
            Assert.AreEqual(0, accepted.TileEventsApplied);
            Assert.AreEqual(Revision + 1, receiver.WorldRevision);
            Assert.AreEqual(0u, receiver.LastEventSequence,
                "A new lifetime restarts at zero applied events.");
            Assert.AreEqual(0, receiver.TileCount,
                "The previous lifetime's geometry must be cleared even when the new baseline is empty.");
            Assert.IsFalse(receiver.TryGetTile(RoomA, 1, 4, out _),
                "Geometry from the previous lifetime must not leak into the new one.");
        }

        [Test]
        public void NewWorldRevision_BaselineRestartsTheSequenceAtOne()
        {
            var receiver = new SimSnapshotReceiver();
            SimRoomDelta[] rooms = { Room(RoomA) };
            SimSnapshot first = Snapshot(1, Revision, new[] { Tile(1, x: 1) }, rooms);
            receiver.Accept(in first);

            SimSnapshot withBaseline = Snapshot(2, Revision + 1,
                new[] { Tile(1, revision: Revision + 1, x: 9) }, rooms);
            SimSnapshotReceipt accepted = receiver.Accept(in withBaseline);

            Assert.IsTrue(accepted.Applied,
                "The new lifetime's baseline is a fresh 1..N sequence, so it must be admitted even "
                + "though its first event repeats the previous lifetime's number.");
            Assert.AreEqual(Revision + 1, receiver.WorldRevision);
            Assert.AreEqual(1u, receiver.LastEventSequence, "A new lifetime restarts the sequence at 1.");
            Assert.AreEqual(1, receiver.TileCount, "The old lifetime's tiles must be cleared.");
            Assert.IsFalse(receiver.TryGetTile(RoomA, 1, 4, out _),
                "Geometry from the previous lifetime must not leak into the new one.");
            Assert.IsTrue(receiver.TryGetTile(RoomA, 9, 4, out _));
        }

        // ── The post-ack suffix: the same stream seen from a later position ──

        [Test]
        public void PostAckSuffix_ContinuesTheStream()
        {
            var receiver = new SimSnapshotReceiver();
            SimRoomDelta[] rooms = { Room(RoomA) };
            SimSnapshot first = Snapshot(1, Revision,
                new[] { Tile(1, x: 1), Tile(2, x: 2), Tile(3, x: 3) }, rooms);
            receiver.Accept(in first);

            // The host trimmed 1..3 after an ack. A run beginning at LastEventSequence + 1 is the same
            // stream seen from the receiver's position — not a corrupt prefix, and not a new baseline.
            SimSnapshot suffix = Snapshot(2, Revision, new[] { Tile(4, x: 4), Tile(5, x: 5) }, rooms);
            SimSnapshotReceipt receipt = receiver.Accept(in suffix);

            Assert.IsTrue(receipt.Applied,
                "A journal that starts exactly at the next owed sequence must be admitted.");
            Assert.AreEqual(2, receipt.TileEventsApplied);
            Assert.AreEqual(0, receipt.TileEventsDuplicated);
            Assert.AreEqual(5u, receiver.LastEventSequence);
            Assert.AreEqual(5, receiver.TileCount,
                "A suffix must add to the geometry, not replace it.");
            Assert.IsTrue(receiver.TryGetTile(RoomA, 1, 4, out _),
                "A suffix must not be mistaken for a new baseline: earlier cells survive.");
        }

        [Test]
        public void SuffixBeginningPastTheNextSequence_IsARejectedGap()
        {
            var receiver = new SimSnapshotReceiver();
            SimRoomDelta[] rooms = { Room(RoomA) };
            SimSnapshot first = Snapshot(1, Revision, new[] { Tile(1, x: 1), Tile(2, x: 2) }, rooms);
            receiver.Accept(in first);

            // Events 3 is missing: the suffix starts one past where it may start. Those events are
            // gone for good, so the run cannot be applied as if it were contiguous.
            SimSnapshot gap = Snapshot(2, Revision, new[] { Tile(4, x: 4) }, rooms);
            SimSnapshotReceipt receipt = receiver.Accept(in gap);

            Assert.AreEqual(SimSnapshotRejectReason.CorruptTileJournal, receipt.RejectReason,
                "A suffix must begin at LastEventSequence + 1; starting later is a gap, not a suffix.");
            Assert.AreEqual(2u, receiver.LastEventSequence);
            Assert.AreEqual(2, receiver.TileCount);
        }

        [Test]
        public void SequenceZero_IsRejected()
        {
            var receiver = new SimSnapshotReceiver();
            SimSnapshot snapshot = Snapshot(1, Revision, new[] { Tile(0) }, new[] { Room(RoomA) });

            Assert.AreEqual(SimSnapshotRejectReason.CorruptTileJournal,
                receiver.Accept(in snapshot).RejectReason,
                "0 is the 'nothing applied yet' sentinel, so it can only be read as a duplicate of an "
                + "event that was never applied. Admitting it would make LastEventSequence ambiguous.");
            Assert.AreEqual(0, receiver.TileCount);
        }

        [Test]
        public void EmptyJournalOnTheSameLifetime_LeavesGeometryUntouched()
        {
            var receiver = new SimSnapshotReceiver();
            SimRoomDelta[] rooms = { Room(RoomA) };
            SimSnapshot first = Snapshot(1, Revision, new[] { Tile(1, x: 1), Tile(2, x: 2) }, rooms);
            receiver.Accept(in first);

            // Nothing changed since the last projection: the host sends no events and the geometry
            // must survive. An empty journal is not a "clear the world" instruction.
            SimSnapshot quiet = Snapshot(2, Revision, Array.Empty<SimTileMutation>(), rooms);
            SimSnapshotReceipt receipt = receiver.Accept(in quiet);

            Assert.IsTrue(receipt.Applied);
            Assert.AreEqual(0, receipt.TileEventsApplied);
            Assert.AreEqual(2, receiver.TileCount);
            Assert.AreEqual(2u, receiver.LastEventSequence);
        }

        // ── The codec and the receiver agree ─────────────────────────────────

        [Test]
        public void WireRoundTrip_IsAcceptedAndReplayStaysIdempotent()
        {
            var receiver = new SimSnapshotReceiver();
            // Distinct cells on purpose: two events at one cell are one tile, and TileCount would then
            // be 1 for reasons that have nothing to do with the wire.
            SimSnapshot source = Snapshot(1, Revision,
                new[] { Tile(1, x: 3), Tile(2, x: 4) },
                new[] { Room(RoomA, new[] { Drop(0x1001) }) });

            SimSnapshot received = SimSnapshotSerializer.Deserialize(
                SimSnapshotSerializer.Serialize(in source));

            Assert.IsTrue(receiver.Accept(in received).Applied,
                "A snapshot that survived the wire must still pass admission; otherwise the codec and "
                + "the receiver disagree about the layout.");

            SimSnapshot replay = SimSnapshotSerializer.Deserialize(
                SimSnapshotSerializer.Serialize(in source));
            replay.SnapshotSequence = 2;

            SimSnapshotReceipt second = receiver.Accept(in replay);

            Assert.IsTrue(second.Applied);
            Assert.AreEqual(0, second.TileEventsApplied,
                "The wire must not turn a repeated reliable event into a fresh one.");
            Assert.AreEqual(2, second.TileEventsDuplicated);
            Assert.AreEqual(2, receiver.TileCount);
            Assert.AreEqual(1, receiver.LootCount(RoomA));
        }
    }
}
