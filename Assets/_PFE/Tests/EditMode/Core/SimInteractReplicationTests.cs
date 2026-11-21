using System;
using System.Collections.Generic;
using NUnit.Framework;
using PFE.Core.Input;
using PFE.Sim.Replication;
using PFE.Sim.State;

namespace PFE.Tests.EditMode.Core
{
    /// <summary>
    /// The executable contract for the container half of the replication path: an interact edge on the
    /// command channel, and the authoritative opened-container set on the snapshot.
    ///
    /// <para><b>Why this needs its own fixture rather than a line in an existing one.</b> Two rules here
    /// can only be wrong in ways a compile cannot see. First, a command batch is a <i>redundancy
    /// window</i>, so an interact edge appears in several consecutive batches; a host that fired the
    /// whole window would open the chest once per resend — the exact double-loot the missing flag
    /// caused, reintroduced by the transport. Second, <c>LootedObjects</c> is a <b>full replace</b> per
    /// room, so a receiver that merged instead of replacing would keep a stale "opened" flag on a
    /// regenerated container forever. Both are pinned below by fixtures that fail if the rule is
    /// removed.</para>
    ///
    /// <para><b>Every path crosses the real codec.</b> The batch crosses
    /// <c>SimReplicationPayloadSerializer</c> and the snapshot crosses
    /// <c>SimSnapshotSerializer</c>, so a field the codec forgot to write is a test failure rather than
    /// a session that silently opens the wrong chest.</para>
    /// </summary>
    [TestFixture]
    public sealed class SimInteractReplicationTests
    {
        private const ulong SessionId = 0x1E7E0AC700000001UL;
        private const uint PeerId = 1u;
        private const byte Slot = 1;
        private const ulong RoomA = 0xA0A0000000000001UL;
        private const ulong RoomB = 0xB0B0000000000002UL;

        // ── Harness ───────────────────────────────────────────────────────────

        private sealed class RecordingInteractSink : ISimInteractSink
        {
            public readonly List<SimInteractRequest> Requests = new List<SimInteractRequest>();
            public uint LastPeerId;
            public byte LastSlot;

            public void Apply(uint peerId, byte slot, in SimInteractRequest request)
            {
                LastPeerId = peerId;
                LastSlot = slot;
                Requests.Add(request);
            }
        }

        private sealed class NullCommandSink : ISimCommandSink
        {
            public int Calls;

            public void Apply(uint peerId, byte slot, PlayerCommand[] commands, int firstNewIndex)
            {
                Calls++;
            }
        }

        private static PlayerCommand Command(int tick, uint sequence,
            bool interact = false, ulong room = 0UL, int objectIndex = 0)
        {
            PlayerCommand command = PlayerCommand.FromIntent(
                tickIndex: tick,
                slotIndex: Slot,
                sequence: sequence,
                moveInputX: 0f,
                moveInputY: 0f,
                jumpHeld: false,
                runHeld: false,
                jumpPressed: false,
                dashPressed: false,
                dropThroughPressed: false,
                aimAngle: 0f,
                interactPressed: interact,
                targetRoomHash: room,
                targetObjectIndex: objectIndex);
            return command;
        }

        private static SimReplicationEnvelope BatchEnvelope(uint envelopeSequence, params PlayerCommand[] commands)
        {
            return new SimReplicationEnvelope
            {
                ProtocolVersion = SimReplicationProtocolRules.CurrentProtocolVersion,
                SessionId = SessionId,
                PeerId = PeerId,
                Kind = SimReplicationMessageKind.PlayerCommandBatch,
                Sequence = envelopeSequence,
                Payload = SimReplicationPayloadSerializer.Serialize(new SimPlayerCommandBatch
                {
                    SessionId = SessionId,
                    PeerId = PeerId,
                    Commands = commands,
                }),
            };
        }

        private static SimCommandIngress NewIngress(ISimInteractSink interactSink)
        {
            return new SimCommandIngress(SessionId, 16, _ => true, new NullCommandSink(), interactSink);
        }

        private static SimRoomDelta RoomWith(ulong roomHash, params SimObjectState[] opened)
        {
            return new SimRoomDelta
            {
                RoomHash = roomHash,
                LifecycleSequence = 0,
                Units = Array.Empty<SimUnitState>(),
                Motors = Array.Empty<SimMotorState>(),
                Projectiles = Array.Empty<SimProjectileState>(),
                DroppedItems = Array.Empty<SimDroppedItem>(),
                RemovedEntityHashes = Array.Empty<ulong>(),
                LootedObjects = opened,
            };
        }

        private static SimSnapshot Snapshot(uint sequence, long revision, params SimRoomDelta[] rooms)
        {
            return new SimSnapshot
            {
                ProtocolVersion = SimSnapshotSerializer.CurrentProtocolVersion,
                TickIndex = 1L,
                SnapshotSequence = sequence,
                WorldRevision = revision,
                BaselineHash = 0UL,
                Rooms = rooms,
                TileMutations = Array.Empty<SimTileMutation>(),
            };
        }

        // ── The command carries a target ──────────────────────────────────────

        [Test]
        public void PlayerCommand_RoundTripsTheInteractTarget()
        {
            PlayerCommand source = Command(40, 7u, interact: true, room: RoomA, objectIndex: 13);

            using var stream = new System.IO.MemoryStream();
            using (var writer = new System.IO.BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                source.WriteTo(writer);
            }
            stream.Position = 0;
            PlayerCommand roundTripped;
            using (var reader = new System.IO.BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                roundTripped = PlayerCommand.ReadFrom(reader);
            }

            Assert.That(roundTripped.InteractPressed, Is.True,
                "The interact bit did not survive the codec, so the host never sees the press.");
            Assert.That(roundTripped.TargetRoomHash, Is.EqualTo(RoomA));
            Assert.That(roundTripped.TargetObjectIndex, Is.EqualTo(13));
            Assert.That(roundTripped, Is.EqualTo(source),
                "A command that differs from the one sent would be refused as OverlapWasRewritten on the " +
                "next redundant batch.");
        }

        [Test]
        public void PlayerCommand_ATargetWithoutTheEdgeIsNotAnInteract()
        {
            // The presence signal is the bit, not the ids: a client that fills the target every tick and
            // only sets the bit on the press must not open a chest every tick.
            PlayerCommand command = Command(40, 7u, interact: false, room: RoomA, objectIndex: 13);

            Assert.That(command.InteractPressed, Is.False);
            Assert.That(SimInteractRules.TryRead(command, out _), Is.False);
        }

        // ── Which edges are well formed ───────────────────────────────────────

        [Test]
        public void InteractRules_RefusesAZeroRoomHash()
        {
            // RoomKey prefixes the id so a real room is never zero. A zero here means "the client
            // pressed E with nothing targeted", and defaulting it to room 0 would address nothing.
            PlayerCommand command = Command(40, 7u, interact: true, room: 0UL, objectIndex: 3);

            Assert.That(SimInteractRules.TryRead(command, out SimInteractRequest request), Is.False);
            Assert.That(request.RoomHash, Is.Zero);
        }

        [Test]
        public void InteractRules_RefusesANegativeObjectIndex()
        {
            PlayerCommand command = Command(40, 7u, interact: true, room: RoomA, objectIndex: -1);

            Assert.That(SimInteractRules.TryRead(command, out _), Is.False);
        }

        [Test]
        public void InteractRules_AcceptsObjectIndexZero()
        {
            // The trap this guards: if "absent" were encoded as index 0 (the obvious sentinel), the
            // first container in every room would be unopenable — and the fixture that only ever used
            // index 1 would stay green.
            PlayerCommand command = Command(40, 7u, interact: true, room: RoomA, objectIndex: 0);

            Assert.That(SimInteractRules.TryRead(command, out SimInteractRequest request), Is.True);
            Assert.That(request.ObjectIndex, Is.Zero);
            Assert.That(request.RoomHash, Is.EqualTo(RoomA));
            Assert.That(request.TickIndex, Is.EqualTo(40));
        }

        // ── The redundancy window ─────────────────────────────────────────────

        [Test]
        public void InteractRules_DispatchesOnlyTheFreshSuffix()
        {
            // A window of four ticks whose first three the host already applied. The interact edge sits
            // at the OLDEST tick — exactly the shape of a real press: the edge is at the start of the
            // window and the window slides forward one tick per batch.
            PlayerCommand[] window =
            {
                Command(10, 1u, interact: true, room: RoomA, objectIndex: 2),
                Command(11, 2u),
                Command(12, 3u),
                Command(13, 4u),
            };

            var sink = new RecordingInteractSink();
            int delivered = SimInteractRules.Dispatch(window, firstNewIndex: 3, PeerId, Slot, sink);

            Assert.That(delivered, Is.Zero,
                "The overlap was re-fired. A chest would open once per redundant batch.");
            Assert.That(sink.Requests, Is.Empty);
        }

        [Test]
        public void InteractRules_DispatchesAFreshEdgeAtTheStartOfTheSuffix()
        {
            PlayerCommand[] window =
            {
                Command(10, 1u),
                Command(11, 2u),
                Command(12, 3u, interact: true, room: RoomA, objectIndex: 2),
                Command(13, 4u),
            };

            var sink = new RecordingInteractSink();
            int delivered = SimInteractRules.Dispatch(window, firstNewIndex: 2, PeerId, Slot, sink);

            Assert.That(delivered, Is.EqualTo(1));
            Assert.That(sink.Requests.Count, Is.EqualTo(1));
            Assert.That(sink.Requests[0].TickIndex, Is.EqualTo(12));
            Assert.That(sink.Requests[0].ObjectIndex, Is.EqualTo(2));
            Assert.That(sink.LastPeerId, Is.EqualTo(PeerId));
            Assert.That(sink.LastSlot, Is.EqualTo(Slot));
        }

        [Test]
        public void InteractRules_TwoEdgesInOneWindowAreTwoRequests()
        {
            // A player who presses E, gets nothing (a locked chest), and presses again two ticks later.
            // Reporting only the first would make the second press silently vanish.
            PlayerCommand[] window =
            {
                Command(10, 1u, interact: true, room: RoomA, objectIndex: 2),
                Command(11, 2u),
                Command(12, 3u, interact: true, room: RoomA, objectIndex: 5),
            };

            var sink = new RecordingInteractSink();
            int delivered = SimInteractRules.Dispatch(window, firstNewIndex: 0, PeerId, Slot, sink);

            Assert.That(delivered, Is.EqualTo(2));
            Assert.That(sink.Requests[0].ObjectIndex, Is.EqualTo(2));
            Assert.That(sink.Requests[1].ObjectIndex, Is.EqualTo(5));
        }

        [Test]
        public void InteractRules_NoSinkDeliversNothing()
        {
            PlayerCommand[] window = { Command(10, 1u, interact: true, room: RoomA, objectIndex: 2) };

            Assert.That(SimInteractRules.Dispatch(window, 0, PeerId, Slot, null), Is.Zero);
        }

        // ── The ingress drives the sink from the fresh suffix only ─────────────

        [Test]
        public void Ingress_RoutesAFreshInteractEdgeAcrossTheRealCodec()
        {
            var sink = new RecordingInteractSink();
            SimCommandIngress ingress = NewIngress(sink);

            SimCommandReceipt receipt = ingress.Receive(BatchEnvelope(1u,
                Command(10, 1u),
                Command(11, 2u, interact: true, room: RoomA, objectIndex: 4)));

            Assert.That(receipt.Applied, Is.True, "The batch was refused: " + receipt.Reason);
            Assert.That(sink.Requests.Count, Is.EqualTo(1));
            Assert.That(sink.Requests[0].RoomHash, Is.EqualTo(RoomA),
                "The room hash did not survive the batch codec.");
            Assert.That(sink.Requests[0].ObjectIndex, Is.EqualTo(4));
            Assert.That(ingress.DispatchedInteractCount, Is.EqualTo(1));
        }

        [Test]
        public void Ingress_DoesNotRefireAnEdgeFromTheOverlap()
        {
            var sink = new RecordingInteractSink();
            SimCommandIngress ingress = NewIngress(sink);

            // Batch one: ticks 10..11, the press on tick 10.
            ingress.Receive(BatchEnvelope(1u,
                Command(10, 1u, interact: true, room: RoomA, objectIndex: 4),
                Command(11, 2u)));

            // Batch two: the redundancy window repeats ticks 10..11 and adds 12. The press is a
            // duplicate — byte-identical, so the ingress accepts the batch — and must not fire again.
            SimCommandReceipt second = ingress.Receive(BatchEnvelope(2u,
                Command(10, 1u, interact: true, room: RoomA, objectIndex: 4),
                Command(11, 2u),
                Command(12, 3u)));

            Assert.That(second.Applied, Is.True, "A pure-repeat window is legal: " + second.Reason);
            Assert.That(second.DuplicateCommands, Is.EqualTo(2));
            Assert.That(sink.Requests.Count, Is.EqualTo(1),
                "The redundant window re-opened the chest. This is the double-loot bug in transport form.");
            Assert.That(ingress.DispatchedInteractCount, Is.EqualTo(1));
        }

        [Test]
        public void Ingress_WithoutAnInteractSinkStillAcceptsBatches()
        {
            // The sink is optional; a session that does not drive containers must not start refusing
            // command batches, or the whole Phase 5 uplink would look broken.
            SimCommandIngress ingress = NewIngress(null);

            SimCommandReceipt receipt = ingress.Receive(BatchEnvelope(1u,
                Command(10, 1u, interact: true, room: RoomA, objectIndex: 4)));

            Assert.That(receipt.Applied, Is.True, "An absent interact sink must not reject intent: " + receipt.Reason);
            Assert.That(ingress.DispatchedInteractCount, Is.Zero);
        }

        // ── The room delta carries the opened set ─────────────────────────────

        [Test]
        public void RoomDelta_RoundTripsTheOpenedSet()
        {
            SimRoomDelta source = RoomWith(RoomA,
                new SimObjectState { ObjectIndex = 0, LootState = 1 },
                new SimObjectState { ObjectIndex = 7, LootState = 2 });

            using var stream = new System.IO.MemoryStream();
            using (var writer = new System.IO.BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                source.WriteTo(writer);
            }
            stream.Position = 0;
            SimRoomDelta roundTripped;
            using (var reader = new System.IO.BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                roundTripped = SimRoomDelta.ReadFrom(reader);
            }

            Assert.That(roundTripped.LootedObjects, Has.Length.EqualTo(2));
            Assert.That(roundTripped.LootedObjects[0].ObjectIndex, Is.Zero);
            Assert.That(roundTripped.LootedObjects[0].LootState, Is.EqualTo(1));
            Assert.That(roundTripped.LootedObjects[1].ObjectIndex, Is.EqualTo(7));
            Assert.That(roundTripped.LootedObjects[1].LootState, Is.EqualTo(2));
            Assert.That(roundTripped, Is.EqualTo(source));
        }

        [Test]
        public void Snapshot_RoundTripsTheOpenedSetThroughTheCodec()
        {
            SimSnapshot source = Snapshot(1u, 3L, RoomWith(RoomA,
                new SimObjectState { ObjectIndex = 3, LootState = 1 }));

            SimSnapshot roundTripped = SimSnapshotSerializer.Deserialize(SimSnapshotSerializer.Serialize(source));

            Assert.That(roundTripped.ComputeStateHash(), Is.EqualTo(source.ComputeStateHash()),
                "The opened set is not part of the state hash, so a divergence in it would go unreported.");
            Assert.That(SimSnapshotSerializer.RoundTripStateHash(source), Is.EqualTo(source.ComputeStateHash()));
        }

        [Test]
        public void Snapshot_StateHashChangesWhenTheOpenedSetChanges()
        {
            SimSnapshot closed = Snapshot(1u, 3L, RoomWith(RoomA));
            SimSnapshot opened = Snapshot(1u, 3L, RoomWith(RoomA,
                new SimObjectState { ObjectIndex = 3, LootState = 1 }));

            Assert.That(opened.ComputeStateHash(), Is.Not.EqualTo(closed.ComputeStateHash()));

            // The same index with a different oracle value is a different state: 2 means "an important
            // item came out", and a receiver that collapsed both to a bool could not restore it.
            SimSnapshot important = Snapshot(1u, 3L, RoomWith(RoomA,
                new SimObjectState { ObjectIndex = 3, LootState = 2 }));
            Assert.That(important.ComputeStateHash(), Is.Not.EqualTo(opened.ComputeStateHash()));
        }

        [Test]
        public void RoomDelta_NullArraysAndEmptyArraysAreTheSameState()
        {
            // A producer with nothing to report may leave an array null; the codec reads a zero count
            // back as an empty array. Equality has to call those the same state or a plain round-trip
            // reports a divergence that is not there. This only surfaced when the opened set became
            // the first array a fixture could leave null.
            var sparse = new SimRoomDelta { RoomHash = RoomA, LifecycleSequence = 2 };

            using var stream = new System.IO.MemoryStream();
            using (var writer = new System.IO.BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                sparse.WriteTo(writer);
            }
            stream.Position = 0;
            SimRoomDelta roundTripped;
            using (var reader = new System.IO.BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                roundTripped = SimRoomDelta.ReadFrom(reader);
            }

            Assert.That(roundTripped, Is.EqualTo(sparse),
                "null and empty are the same state; the codec turns one into the other.");
            Assert.That(roundTripped.LootedObjects, Is.Empty);
        }

        [Test]
        public void RoomDelta_AnEmptySetIsNotTheSameAsAPopulatedOne()
        {
            // The positive control for the rule above: treating null as empty must not collapse a real
            // difference. Without this, "null == empty" could be satisfied by "everything is equal".
            SimRoomDelta empty = RoomWith(RoomA);
            SimRoomDelta populated = RoomWith(RoomA, new SimObjectState { ObjectIndex = 1, LootState = 1 });

            Assert.That(populated, Is.Not.EqualTo(empty));
            Assert.That(populated.GetHashCode(), Is.Not.EqualTo(empty.GetHashCode()));
        }

        // ── The receiver applies it as a full replace ─────────────────────────

        [Test]
        public void Receiver_AppliesTheOpenedSet()
        {
            var receiver = new SimSnapshotReceiver();

            SimSnapshotReceipt receipt = receiver.Accept(Snapshot(1u, 3L, RoomWith(RoomA,
                new SimObjectState { ObjectIndex = 4, LootState = 2 })));

            Assert.That(receipt.Applied, Is.True, "Refused: " + receipt.RejectReason);
            Assert.That(receipt.LootedObjectsApplied, Is.EqualTo(1));
            Assert.That(receiver.OpenedObjectCount(RoomA), Is.EqualTo(1));
            Assert.That(receiver.TryGetOpenedObject(RoomA, 4, out SimObjectState state), Is.True);
            Assert.That(state.LootState, Is.EqualTo(2));
        }

        [Test]
        public void Receiver_FullReplaceClosesAContainerTheHostNoLongerReports()
        {
            // A room that streams out and back is regenerated; the host then reports an empty set. If
            // the receiver merged instead of replacing, the chest would stay "already looted" forever
            // on that client while the host would happily let it be opened again.
            var receiver = new SimSnapshotReceiver();
            receiver.Accept(Snapshot(1u, 3L, RoomWith(RoomA,
                new SimObjectState { ObjectIndex = 4, LootState = 1 })));

            SimSnapshotReceipt second = receiver.Accept(Snapshot(2u, 3L, RoomWith(RoomA)));

            Assert.That(second.Applied, Is.True, "Refused: " + second.RejectReason);
            Assert.That(receiver.OpenedObjectCount(RoomA), Is.Zero,
                "The opened set was merged rather than replaced.");
            Assert.That(receiver.TryGetOpenedObject(RoomA, 4, out _), Is.False);
        }

        [Test]
        public void Receiver_AnAbsentRoomLeavesItsSetUntouched()
        {
            var receiver = new SimSnapshotReceiver();
            receiver.Accept(Snapshot(1u, 3L, RoomWith(RoomA,
                new SimObjectState { ObjectIndex = 4, LootState = 1 })));

            // Room B present, room A absent: absent means unchanged, not emptied.
            receiver.Accept(Snapshot(2u, 3L, RoomWith(RoomB)));

            Assert.That(receiver.OpenedObjectCount(RoomA), Is.EqualTo(1));
            Assert.That(receiver.OpenedObjectCount(RoomB), Is.Zero);
        }

        [Test]
        public void Receiver_RefusesADuplicateObjectIndex()
        {
            var receiver = new SimSnapshotReceiver();

            SimSnapshotReceipt receipt = receiver.Accept(Snapshot(1u, 3L, RoomWith(RoomA,
                new SimObjectState { ObjectIndex = 4, LootState = 1 },
                new SimObjectState { ObjectIndex = 4, LootState = 2 })));

            Assert.That(receipt.Applied, Is.False);
            Assert.That(receipt.RejectReason, Is.EqualTo(SimSnapshotRejectReason.CorruptObjectState));
            Assert.That(receiver.AcceptedSnapshotCount, Is.Zero);
        }

        [Test]
        public void Receiver_RefusesANegativeObjectIndex()
        {
            var receiver = new SimSnapshotReceiver();

            SimSnapshotReceipt receipt = receiver.Accept(Snapshot(1u, 3L, RoomWith(RoomA,
                new SimObjectState { ObjectIndex = -1, LootState = 1 })));

            Assert.That(receipt.Applied, Is.False);
            Assert.That(receipt.RejectReason, Is.EqualTo(SimSnapshotRejectReason.CorruptObjectState));
        }

        [Test]
        public void Receiver_RefusesANonPositiveLootState()
        {
            // Membership in the set *is* "opened", so a zero contradicts the set's own meaning. Folding
            // it would make the container openable again on this client while the host refuses it.
            var receiver = new SimSnapshotReceiver();

            SimSnapshotReceipt receipt = receiver.Accept(Snapshot(1u, 3L, RoomWith(RoomA,
                new SimObjectState { ObjectIndex = 4, LootState = 0 })));

            Assert.That(receipt.Applied, Is.False);
            Assert.That(receipt.RejectReason, Is.EqualTo(SimSnapshotRejectReason.CorruptObjectState));
        }

        [Test]
        public void Receiver_ARejectedSnapshotLeavesTheOpenedSetUntouched()
        {
            var receiver = new SimSnapshotReceiver();
            receiver.Accept(Snapshot(1u, 3L, RoomWith(RoomA,
                new SimObjectState { ObjectIndex = 4, LootState = 1 })));

            // A later snapshot whose opened set is corrupt must not be half-applied.
            SimSnapshotReceipt rejected = receiver.Accept(Snapshot(2u, 3L, RoomWith(RoomA,
                new SimObjectState { ObjectIndex = 9, LootState = 1 },
                new SimObjectState { ObjectIndex = 9, LootState = 1 })));

            Assert.That(rejected.Applied, Is.False);
            Assert.That(receiver.OpenedObjectCount(RoomA), Is.EqualTo(1));
            Assert.That(receiver.TryGetOpenedObject(RoomA, 4, out _), Is.True);
            Assert.That(receiver.TryGetOpenedObject(RoomA, 9, out _), Is.False);
        }
    }
}
