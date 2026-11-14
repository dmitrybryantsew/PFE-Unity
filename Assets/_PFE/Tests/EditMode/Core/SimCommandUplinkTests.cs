using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using PFE.Core.Input;
using PFE.Sim.Replication;
using PFE.Sim.State;

namespace PFE.Tests.EditMode.Core
{
    /// <summary>
    /// Phase 5: the executable contract for the client → host command uplink.
    ///
    /// <para><b>End to end still means bytes.</b> Every envelope in the session tests crosses the
    /// real <c>SimReplicationEnvelopeSerializer</c>, and every batch crosses the real payload codec.
    /// A loopback that passed structs by reference could hide a field the codec forgot to write.</para>
    ///
    /// <para><b>The folded-tape fact is deliberately pinned.</b> <c>PlayerController</c> increments
    /// its sequence once per render frame while <c>PlayerCommandTape</c> folds several frames into
    /// one sim-tick command that keeps the first frame's sequence. The fixtures therefore use gapped
    /// sequences (1, 4, 7, …). A guard demanding +1 would be green in a synthetic fixture and would
    /// refuse every honest batch in the game — so the test asserts the gap is <i>accepted</i> and
    /// only a rewind is refused.</para>
    /// </summary>
    [TestFixture]
    public sealed class SimCommandUplinkTests
    {
        private const ulong SessionId = 0x5E5510A11UL;
        private const ulong Nonce = 0xC11E000000000001UL;
        private const int Window = 8;
        private const int ChunkSize = 16;
        private const ulong RoomA = 0xAAAA000000000001UL;

        // ── Harness ───────────────────────────────────────────────────────────

        private sealed class FakeSource : ISimReplicationSnapshotSource
        {
            private readonly SimTileJournal _journal = new SimTileJournal();

            public SimTileJournal Journal => _journal;

            public SimSnapshot Current => new SimSnapshot
            {
                ProtocolVersion = SimSnapshotSerializer.CurrentProtocolVersion,
                TickIndex = 1L,
                SnapshotSequence = 1u,
                WorldRevision = 7L,
                BaselineHash = 0UL,
                Rooms = Array.Empty<SimRoomDelta>(),
                TileMutations = Array.Empty<SimTileMutation>(),
            };
        }

        private sealed class RecordingSink : ISimCommandSink
        {
            public readonly List<PlayerCommand> Applied = new List<PlayerCommand>();
            public int Calls;
            public uint LastPeerId;
            public byte LastSlot;
            public int LastFirstNewIndex = -1;

            public void Apply(uint peerId, byte slot, PlayerCommand[] commands, int firstNewIndex)
            {
                Calls++;
                LastPeerId = peerId;
                LastSlot = slot;
                LastFirstNewIndex = firstNewIndex;
                for (int i = firstNewIndex; i < commands.Length; i++) Applied.Add(commands[i]);
            }
        }

        private sealed class Session
        {
            public readonly SimReplicationOutbox ToClient = new SimReplicationOutbox();
            public readonly SimReplicationOutbox ToHost = new SimReplicationOutbox();
            public readonly FakeSource Source = new FakeSource();
            public readonly RecordingSink Sink;
            public readonly SimReplicationHost Host;
            public readonly SimReplicationClient Client;

            /// <summary>ONE persistent tape, like the shipping PlayerCommandTape. A fresh tape per
            /// send would re-number every overlapping command's sequence and make the overlap
            /// integrity guard (correctly) refuse an honest redundant window.</summary>
            public readonly PlayerCommandBuffer Tape = new PlayerCommandBuffer(64);
            private uint _nextSequence = 1u;

            public SimPlayerCommandBatch LastSentBatch { get; private set; }

            public Session(bool withSink = true, bool withWindow = true)
            {
                Sink = withSink ? new RecordingSink() : null;
                Host = new SimReplicationHost(SessionId, Source, ToClient, ChunkSize,
                    withWindow ? Window : 0, Sink);
                Client = new SimReplicationClient(Nonce, ToHost);
            }

            public void Join()
            {
                Client.Join(Interest());
                foreach (SimReplicationEnvelope envelope in TakeToHost()) Host.Receive(ThroughBytes(envelope));
                foreach (SimReplicationEnvelope envelope in TakeToClient())
                {
                    if (envelope.Kind == SimReplicationMessageKind.JoinAccept)
                    {
                        Client.Receive(ThroughBytes(envelope));
                    }
                }
                Assert.IsTrue(Client.IsJoined, "The arrange step must actually join the peer.");
            }

            public SimReplicationEnvelope[] TakeToHost()
            {
                SimReplicationEnvelope[] items = ToHost.ToArray();
                ToHost.Clear();
                return items;
            }

            public SimReplicationEnvelope[] TakeToClient()
            {
                SimReplicationEnvelope[] items = ToClient.ToArray();
                ToClient.Clear();
                return items;
            }

            /// <summary>Append only ticks newer than the tape's tail. Re-sending the same ticks is how
            /// a duplicate-only window is produced; re-appending them would punch a tick hole.</summary>
            public void Append(params int[] ticks)
            {
                PlayerCommand latest;
                int last = Tape.TryGetLatest(out latest) ? latest.TickIndex : int.MinValue;
                for (int i = 0; i < ticks.Length; i++)
                {
                    if (ticks[i] <= last) continue;
                    Tape.Enqueue(Command(ticks[i], _nextSequence, 0));
                    _nextSequence += 3u;   // the folded-tape sequence gaps
                    last = ticks[i];
                }
            }

            /// <summary>Publish the newest window of the persistent tape. The returned envelope is
            /// recorded but not delivered — the fixture chooses when and how many times to deliver.</summary>
            public SimReplicationEnvelope[] Send(int windowSize, params int[] ticks)
            {
                Append(ticks);
                bool sent = Client.SendCommands(Tape, windowSize);
                Assert.IsTrue(sent, "The arrange step must actually publish a command batch.");
                SimReplicationEnvelope[] items = TakeToHost();
                Assert.AreEqual(1, items.Length, "One send must produce exactly one envelope.");
                LastSentBatch = SimReplicationPayloadSerializer.ReadPlayerCommandBatch(
                    items[0].Payload, Window);
                return items;
            }

            public void Deliver(params SimReplicationEnvelope[] envelopes)
            {
                for (int i = 0; i < envelopes.Length; i++) Host.Receive(ThroughBytes(envelopes[i]));
            }
        }

        private static SimPeerInterest Interest() => new SimPeerInterest(RoomA, Array.Empty<ulong>());

        private static SimReplicationEnvelope ThroughBytes(SimReplicationEnvelope envelope) =>
            SimReplicationEnvelopeSerializer.Deserialize(
                SimReplicationEnvelopeSerializer.Serialize(envelope));

        /// <summary>A tape with the sequence gaps the real folded input path produces.</summary>
        private static PlayerCommandBuffer Tape(params int[] ticks)
        {
            var buffer = new PlayerCommandBuffer(Math.Max(Window, ticks.Length));
            uint sequence = 1u;
            for (int i = 0; i < ticks.Length; i++)
            {
                buffer.Enqueue(new PlayerCommand
                {
                    TickIndex = ticks[i],
                    SlotIndex = 0,              // the local slot; the wire must re-stamp it
                    Sequence = sequence,
                    MoveX = 10,
                    MoveY = -5,
                    AimAngle = 12.5f,
                    Buttons = PlayerCommand.Button.JumpPressed | PlayerCommand.Button.RunHeld,
                });
                sequence += 3u;                 // several render frames per sim tick
            }
            return buffer;
        }

        private static PlayerCommand Command(int tick, uint sequence, byte slot = 1)
        {
            return new PlayerCommand
            {
                TickIndex = tick,
                SlotIndex = slot,
                Sequence = sequence,
                MoveX = 10,
                MoveY = -5,
                AimAngle = 12.5f,
                Buttons = PlayerCommand.Button.JumpPressed | PlayerCommand.Button.RunHeld,
            };
        }

        private static PlayerCommand[] Commands(params int[] ticks)
        {
            var commands = new PlayerCommand[ticks.Length];
            uint sequence = 1u;
            for (int i = 0; i < ticks.Length; i++)
            {
                commands[i] = Command(ticks[i], sequence);
                sequence += 3u;
            }
            return commands;
        }

        private static SimPlayerCommandBatch Batch(uint peerId, params int[] ticks)
        {
            return new SimPlayerCommandBatch
            {
                SessionId = SessionId,
                PeerId = peerId,
                Commands = Commands(ticks),
            };
        }

        private static SimReplicationEnvelope Envelope(SimPlayerCommandBatch batch, uint peerId = 1u,
            uint sequence = 1u, ulong sessionId = SessionId)
        {
            return new SimReplicationEnvelope
            {
                ProtocolVersion = SimReplicationProtocolRules.CurrentProtocolVersion,
                SessionId = sessionId,
                PeerId = peerId,
                Kind = SimReplicationMessageKind.PlayerCommandBatch,
                Sequence = sequence,
                Payload = SimReplicationPayloadSerializer.Serialize(batch),
            };
        }

        private static byte[] SerializeCommand(PlayerCommand command)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                command.WriteTo(writer);
                writer.Flush();
                return stream.ToArray();
            }
        }

        private static PlayerCommand DeserializeCommand(byte[] bytes)
        {
            using (var stream = new MemoryStream(bytes, false))
            using (var reader = new BinaryReader(stream))
            {
                PlayerCommand command = PlayerCommand.ReadFrom(reader);
                Assert.AreEqual(stream.Length, stream.Position, "The command codec must be fixed width.");
                return command;
            }
        }

        private static SimCommandIngress Ingress(RecordingSink sink = null, Func<uint, bool> joined = null)
        {
            return new SimCommandIngress(SessionId, Window, joined ?? (peerId => peerId == 1u),
                sink ?? new RecordingSink());
        }

        private static SimReplicationEnvelope[] OfKind(SimReplicationEnvelope[] items,
            SimReplicationMessageKind kind)
        {
            var kept = new List<SimReplicationEnvelope>();
            for (int i = 0; i < items.Length; i++) if (items[i].Kind == kind) kept.Add(items[i]);
            return kept.ToArray();
        }

        // ── Controls ──────────────────────────────────────────────────────────

        [Test]
        public void Control_TheFixturesOwnGuardsCanFail()
        {
            Assert.Throws<AssertionException>(() => Assert.AreEqual(1, 2),
                "A green baseline is meaningless when the runner cannot report a failed assertion.");
            Assert.Throws<AssertionException>(() => Assert.AreEqual(2, Commands(1, 2).Length + 1),
                "The helper must return real arrays, not a stub that always satisfies an assertion.");
        }

        // ── Codec ─────────────────────────────────────────────────────────────

        [Test]
        public void PlayerCommand_WireRoundTrip_PreservesEveryField()
        {
            PlayerCommand source = Command(1234, 0xF00D1234u, 7);
            source.MoveX = -127;
            source.MoveY = 64;
            source.AimAngle = -173.25f;
            source.Buttons = PlayerCommand.Button.DashPressed | PlayerCommand.Button.DropThroughPressed
                             | PlayerCommand.Button.JumpHeld;

            PlayerCommand restored = DeserializeCommand(SerializeCommand(source));

            Assert.IsTrue(source.Equals(restored), "Every declared field must survive the wire.");
            Assert.AreEqual(source.TickIndex, restored.TickIndex);
            Assert.AreEqual(source.SlotIndex, restored.SlotIndex);
            Assert.AreEqual(source.Sequence, restored.Sequence);
            Assert.AreEqual(source.MoveX, restored.MoveX);
            Assert.AreEqual(source.MoveY, restored.MoveY);
            Assert.AreEqual(source.AimAngle, restored.AimAngle);
            Assert.AreEqual(source.Buttons, restored.Buttons);
        }

        [Test]
        public void PlayerCommand_Codec_RefusesATruncatedCommand()
        {
            byte[] bytes = SerializeCommand(Command(1, 1u));
            var shorter = new byte[bytes.Length - 1];
            Array.Copy(bytes, shorter, shorter.Length);

            Assert.Throws<EndOfStreamException>(() =>
            {
                using (var stream = new MemoryStream(shorter, false))
                using (var reader = new BinaryReader(stream)) PlayerCommand.ReadFrom(reader);
            }, "A truncated command must throw rather than fabricate a button or an aim angle.");
        }

        [Test]
        public void Batch_Codec_RoundTrip_PreservesSessionPeerAndEveryCommand()
        {
            SimPlayerCommandBatch source = Batch(3u, 10, 11, 12);

            SimPlayerCommandBatch restored = SimReplicationPayloadSerializer.ReadPlayerCommandBatch(
                SimReplicationPayloadSerializer.Serialize(source), Window);

            Assert.IsTrue(source.Equals(restored));
            Assert.AreEqual(SessionId, restored.SessionId);
            Assert.AreEqual(3u, restored.PeerId);
            Assert.AreEqual(3, restored.Commands.Length);
            Assert.AreEqual(12, restored.Commands[2].TickIndex);
        }

        [Test]
        public void Batch_Codec_RefusesTrailingBytes()
        {
            byte[] bytes = SimReplicationPayloadSerializer.Serialize(Batch(1u, 1, 2));
            var longer = new byte[bytes.Length + 1];
            Array.Copy(bytes, longer, bytes.Length);

            Assert.Throws<InvalidDataException>(() =>
                SimReplicationPayloadSerializer.ReadPlayerCommandBatch(longer, Window),
                "A corrupted kind must not be readable as a shorter message that happens to parse.");
        }

        [Test]
        public void Batch_Codec_RefusesACountBeyondTheCallersWindow()
        {
            byte[] bytes = SimReplicationPayloadSerializer.Serialize(Batch(1u, 1, 2, 3));

            Assert.Throws<InvalidDataException>(() =>
                SimReplicationPayloadSerializer.ReadPlayerCommandBatch(bytes, 2),
                "A forged length prefix must not allocate an unbounded array.");

            SimPlayerCommandBatch ok = SimReplicationPayloadSerializer.ReadPlayerCommandBatch(bytes, 3);
            Assert.AreEqual(3, ok.Commands.Length, "The same bytes inside the window are legal.");
        }

        [Test]
        public void Batch_Codec_EmptyWindowIsReadableButNotAdmittable()
        {
            var empty = new SimPlayerCommandBatch { SessionId = SessionId, PeerId = 1u, Commands = null };

            SimPlayerCommandBatch restored = SimReplicationPayloadSerializer.ReadPlayerCommandBatch(
                SimReplicationPayloadSerializer.Serialize(empty), Window);

            Assert.AreEqual(0, restored.Commands.Length);
            Assert.AreEqual(SimCommandRejectReason.EmptyBatch,
                Ingress().Receive(Envelope(restored)).Reason);
        }

        // ── Admission rules ───────────────────────────────────────────────────

        [Test]
        public void SlotForPeer_HostSlotZeroIsNeverClaimableByARemotePeer()
        {
            byte slot;
            Assert.IsFalse(SimCommandAdmissionRules.TrySlotForPeer(0u, out slot),
                "Slot 0 is the host's own body; a remote peer must never drive it.");
            Assert.AreEqual(0, slot);
            Assert.IsTrue(SimCommandAdmissionRules.TrySlotForPeer(1u, out slot));
            Assert.AreEqual(1, slot);
            Assert.IsTrue(SimCommandAdmissionRules.TrySlotForPeer(255u, out slot));
            Assert.AreEqual(255, slot);
            Assert.IsFalse(SimCommandAdmissionRules.TrySlotForPeer(256u, out slot),
                "A peer id beyond byte.MaxValue cannot own a PlayerCommand.SlotIndex.");
        }

        [Test]
        public void IsNewerTick_IsWrapSafeAcrossTheIntBoundary()
        {
            Assert.IsTrue(SimCommandAdmissionRules.IsNewerTick(11, 10));
            Assert.IsFalse(SimCommandAdmissionRules.IsNewerTick(10, 10));
            Assert.IsFalse(SimCommandAdmissionRules.IsNewerTick(9, 10));
            Assert.IsTrue(SimCommandAdmissionRules.IsNewerTick(int.MinValue, int.MaxValue),
                "int.MinValue is exactly one tick after int.MaxValue in unsigned space.");
        }

        [Test]
        public void AreTicksContiguous_RefusesAHole()
        {
            Assert.IsTrue(SimCommandAdmissionRules.AreTicksContiguous(Commands(5, 6, 7)));
            Assert.IsFalse(SimCommandAdmissionRules.AreTicksContiguous(Commands(5, 7, 8)),
                "Applying the suffix of a holed window would silently skip a tick of intent.");
            Assert.IsFalse(SimCommandAdmissionRules.AreTicksContiguous(Array.Empty<PlayerCommand>()));
        }

        [Test]
        public void AreSequencesIncreasing_AcceptsTheFoldedTapesGaps()
        {
            PlayerCommand[] commands = Commands(5, 6, 7);
            Assert.AreEqual(1u, commands[0].Sequence);
            Assert.AreEqual(4u, commands[1].Sequence);
            Assert.AreEqual(7u, commands[2].Sequence,
                "The arrange step must really contain the gaps PlayerCommandTape.Fold produces.");

            Assert.IsTrue(SimCommandAdmissionRules.AreSequencesIncreasing(commands),
                "A folded tape is not contiguous by sequence; TickIndex is the ordering authority.");
        }

        [Test]
        public void AreSequencesIncreasing_RefusesARewind()
        {
            PlayerCommand[] commands = Commands(5, 6, 7);
            commands[2].Sequence = 2u;

            Assert.IsFalse(SimCommandAdmissionRules.AreSequencesIncreasing(commands),
                "A rewound sequence means the producer or a relay rewound history.");
        }

        [Test]
        public void FirstNewIndex_SkipsTheOverlap()
        {
            PlayerCommand[] commands = Commands(5, 6, 7, 8);
            Assert.AreEqual(2, SimCommandAdmissionRules.FirstNewIndex(commands, 6));
            Assert.AreEqual(4, SimCommandAdmissionRules.FirstNewIndex(commands, 99),
                "A window made entirely of duplicates has no new commands.");
            Assert.AreEqual(0, SimCommandAdmissionRules.FirstNewIndex(commands, 0));
        }

        [Test]
        public void IsSingleSlot_RefusesAMixedBatch()
        {
            PlayerCommand[] commands = Commands(5, 6);
            Assert.IsTrue(SimCommandAdmissionRules.IsSingleSlot(commands, 1));
            commands[1].SlotIndex = 2;
            Assert.IsFalse(SimCommandAdmissionRules.IsSingleSlot(commands, 1));
        }

        // ── Client publication ───────────────────────────────────────────────

        [Test]
        public void Client_RefusesToSendBeforeJoin()
        {
            var session = new Session();
            var buffer = Tape(1, 2);

            Assert.IsFalse(session.Client.SendCommands(buffer, Window));
            Assert.AreEqual(0, session.ToHost.Count, "No packet may leave an unjoined client.");
            Assert.AreEqual(1, session.Client.RejectedCommandSendCount);
            Assert.AreEqual(0, session.Client.CommandBatchesSent);
        }

        [Test]
        public void Client_SendCommands_ReStampsTheHostAssignedSlot()
        {
            var session = new Session();
            session.Join();
            SimReplicationEnvelope[] items = session.Send(Window, 1, 2, 3);

            SimPlayerCommandBatch batch = SimReplicationPayloadSerializer.ReadPlayerCommandBatch(
                items[0].Payload, Window);

            Assert.AreEqual(session.Client.PeerId, batch.PeerId);
            Assert.AreEqual((byte)session.Client.PeerId, batch.Commands[0].SlotIndex,
                "The wire must name the host-assigned slot, not the local single-player slot 0.");
            for (int i = 0; i < batch.Commands.Length; i++)
            {
                Assert.AreEqual((byte)session.Client.PeerId, batch.Commands[i].SlotIndex);
            }
            Assert.AreEqual(3, session.Client.LastCommandBatchSize);
        }

        [Test]
        public void Client_SendCommands_LeavesTheLocalTapeUntouched()
        {
            var session = new Session();
            session.Join();
            PlayerCommandBuffer tape = Tape(1, 2);

            Assert.IsTrue(session.Client.SendCommands(tape, Window));

            PlayerCommand latest;
            Assert.IsTrue(tape.TryGetLatest(out latest));
            Assert.AreEqual(0, latest.SlotIndex,
                "Local prediction still uses the local slot; re-stamping is a wire-only operation.");
        }

        [Test]
        public void Client_SendCommands_SendsTheNewestContiguousSuffixOfALongerTape()
        {
            var session = new Session();
            session.Join();
            SimReplicationEnvelope[] items = session.Send(3, 1, 2, 3, 4, 5);

            SimPlayerCommandBatch batch = SimReplicationPayloadSerializer.ReadPlayerCommandBatch(
                items[0].Payload, Window);

            Assert.AreEqual(3, batch.Commands.Length);
            Assert.AreEqual(3, batch.Commands[0].TickIndex);
            Assert.AreEqual(5, batch.Commands[2].TickIndex,
                "Dropping the oldest keeps the window contiguous, which the ingress requires.");
        }

        [Test]
        public void Client_SendCommands_EmptyTapeSendsNoPacket()
        {
            var session = new Session();
            session.Join();

            Assert.IsFalse(session.Client.SendCommands(new PlayerCommandBuffer(Window), Window));
            Assert.AreEqual(0, session.ToHost.Count);
            Assert.AreEqual(0, session.Client.CommandBatchesSent);
        }

        [Test]
        public void Client_SendCommands_EnvelopeNamesSessionPeerKindAndVersion()
        {
            var session = new Session();
            session.Join();
            SimReplicationEnvelope[] items = session.Send(Window, 1, 2);

            Assert.AreEqual(SimReplicationMessageKind.PlayerCommandBatch, items[0].Kind);
            Assert.AreEqual(SessionId, items[0].SessionId);
            Assert.AreEqual(session.Client.PeerId, items[0].PeerId);
            Assert.AreEqual(SimReplicationProtocolRules.CurrentProtocolVersion, items[0].ProtocolVersion);
            Assert.AreEqual(1, session.Client.CommandBatchesSent);
            Assert.AreEqual(2, session.Client.CommandsSent);
        }

        // ── Host admission, through the loop ─────────────────────────────────

        [Test]
        public void EndToEnd_FirstWindow_AppliesEveryCommandToTheSink()
        {
            var session = new Session();
            session.Join();
            SimReplicationEnvelope[] items = session.Send(Window, 1, 2, 3);
            session.Deliver(items);

            Assert.AreEqual(1, session.Sink.Calls);
            Assert.AreEqual(3, session.Sink.Applied.Count);
            Assert.AreEqual(session.Client.PeerId, session.Sink.LastPeerId);
            Assert.AreEqual(0, session.Sink.LastFirstNewIndex);
            Assert.AreEqual(3, session.Host.Commands.AppliedCommandCount);
            Assert.AreEqual(1, session.Host.AcceptedCommandBatchCount);
            Assert.AreEqual(3, session.Host.Commands.LastAppliedTick(session.Client.PeerId));
        }

        [Test]
        public void EndToEnd_RedundantOverlap_IsAppliedOnlyOnce()
        {
            var session = new Session();
            session.Join();

            session.Deliver(session.Send(Window, 1, 2, 3));
            session.Deliver(session.Send(Window, 2, 3, 4));

            Assert.AreEqual(4, session.Sink.Applied.Count,
                "Ticks 1–3 are duplicates of the first window; only tick 4 is new intent.");
            Assert.AreEqual(2, session.Sink.Calls);
            Assert.AreEqual(3, session.Sink.LastFirstNewIndex);
            Assert.AreEqual(3, session.Host.Commands.DuplicateCommandCount);
            Assert.AreEqual(4, session.Host.Commands.AppliedCommandCount);
            Assert.AreEqual(4, session.Host.Commands.LastAppliedTick(session.Client.PeerId));
        }

        [Test]
        public void EndToEnd_WholeWindowDuplicate_DoesNotReRunTheSink()
        {
            var session = new Session();
            session.Join();

            session.Deliver(session.Send(Window, 1, 2));
            session.Deliver(session.Send(Window, 1, 2));

            Assert.AreEqual(1, session.Sink.Calls,
                "A duplicate-only window must not re-run one-shot jump/dash edges.");
            Assert.AreEqual(2, session.Sink.Applied.Count);
            Assert.AreEqual(2, session.Host.AcceptedCommandBatchCount);
            Assert.AreEqual(2, session.Host.Commands.DuplicateCommandCount);
            Assert.AreEqual(2, session.Host.Commands.AppliedCommandCount);
        }

        [Test]
        public void EndToEnd_RewrittenOverlap_IsRefusedWithoutMovingTheTape()
        {
            var session = new Session();
            session.Join();
            SimReplicationEnvelope[] first = session.Send(Window, 1, 2, 3);
            session.Deliver(first);

            // Start from the EXACT window that was applied, so every overlap command is identical
            // except for the one field we deliberately rewrite. A batch built from scratch would fail
            // the integrity check for unrelated sequence differences and prove nothing.
            SimPlayerCommandBatch applied = session.LastSentBatch;
            var commands = new PlayerCommand[applied.Commands.Length + 1];
            Array.Copy(applied.Commands, commands, applied.Commands.Length);
            commands[applied.Commands.Length] = Command(4, 10u, (byte)session.Client.PeerId);
            commands[1].MoveX = 99;       // history rewritten inside the overlap
            var forged = new SimPlayerCommandBatch
            {
                SessionId = applied.SessionId,
                PeerId = applied.PeerId,
                Commands = commands,
            };

            // The forged packet must be newer on the shared ordered channel, or it would be refused
            // as a duplicate before the ingress ever sees the rewritten overlap.
            SimReplicationEnvelope envelope =
                Envelope(forged, session.Client.PeerId, first[0].Sequence + 1u);
            session.Deliver(envelope);

            Assert.AreEqual(SimCommandRejectReason.OverlapWasRewritten,
                session.Host.Commands.LastRejectReason);
            Assert.AreEqual(1, session.Sink.Calls, "A refused batch must not reach the consumer.");
            Assert.AreEqual(3, session.Host.Commands.LastAppliedTick(session.Client.PeerId),
                "The peer's applied tape must be exactly where the accepted batch left it.");
            Assert.AreEqual(1, session.Host.RejectedCommandBatchCount);
        }

        [Test]
        public void EndToEnd_GapAfterALostWindow_IsAcceptedAndReported()
        {
            var session = new Session();
            session.Join();
            session.Deliver(session.Send(2, 1, 2));

            // The client's tape stays contiguous (1..8); several batches are simply lost, and the
            // next window the host sees is only the newest two commands. That is the real shape of a
            // gap: a hole in DELIVERY, not a hole in the tape.
            session.Append(3, 4, 5, 6, 7, 8);
            SimReplicationEnvelope[] items = session.Send(2);
            SimPlayerCommandBatch batch = SimReplicationPayloadSerializer.ReadPlayerCommandBatch(
                items[0].Payload, Window);
            Assert.AreEqual(7, batch.Commands[0].TickIndex);
            Assert.AreEqual(8, batch.Commands[1].TickIndex);
            session.Deliver(items);

            Assert.AreEqual(1, session.Host.Commands.GapBatchCount);
            Assert.AreEqual(4, session.Host.Commands.AppliedCommandCount);
            Assert.AreEqual(4, session.Sink.Applied.Count,
                "Refusing a gap would wedge the peer forever after one lost datagram.");
        }

        [Test]
        public void EndToEnd_DuplicateEnvelope_IsRefusedByTheSharedOrderedChannel()
        {
            var session = new Session();
            session.Join();
            SimReplicationEnvelope[] items = session.Send(Window, 1, 2);

            session.Deliver(items);
            session.Deliver(items);   // exact re-delivery, same envelope sequence

            Assert.AreEqual(1, session.Sink.Calls,
                "A re-delivered batch must not re-enter through the ingress.");
            Assert.AreEqual(1, session.Host.AcceptedCommandBatchCount);
        }

        [Test]
        public void EndToEnd_UnknownPeer_IsRefused()
        {
            var session = new Session();
            session.Join();
            session.Deliver(Envelope(Batch(9u, 1, 2), 9u));

            Assert.AreEqual(1, session.Host.RejectedEnvelopeCount);
            Assert.AreEqual(0, session.Sink.Calls);
        }

        [Test]
        public void EndToEnd_NoCommandWindow_RefusesBeforeMovingTheOrderedChannel()
        {
            var session = new Session(withSink: false, withWindow: false);
            session.Join();

            SimReplicationEnvelope[] items = session.Send(Window, 1, 2);
            session.Deliver(items);
            Assert.IsNull(session.Host.Commands);
            Assert.AreEqual(1, session.Host.RejectedEnvelopeCount);

            // Same sequence as the refused command batch. If the refusal had advanced the peer's
            // high-water mark, this legitimate control packet would now look stale too.
            var interest = new SimReplicationEnvelope
            {
                ProtocolVersion = SimReplicationProtocolRules.CurrentProtocolVersion,
                SessionId = SessionId,
                PeerId = session.Client.PeerId,
                Kind = SimReplicationMessageKind.InterestUpdate,
                Sequence = items[0].Sequence,
                Payload = SimReplicationPayloadSerializer.Serialize(new SimInterestUpdate(Interest())),
            };
            session.Deliver(interest);

            Assert.AreEqual(1, session.Host.RejectedEnvelopeCount,
                "A refused command kind must not burn the ordered channel that acks still need.");
        }

        [Test]
        public void EndToEnd_NoConsumer_IsRefusedButCounted()
        {
            var session = new Session(withSink: false);
            session.Join();
            session.Deliver(session.Send(Window, 1, 2));

            Assert.AreEqual(1, session.Host.RejectedCommandBatchCount);
            Assert.AreEqual(SimCommandRejectReason.NoConsumer, session.Host.Commands.LastRejectReason);
        }

        // ── Ingress, direct ──────────────────────────────────────────────────

        [Test]
        public void Ingress_ReAttributedPayload_IsRefused()
        {
            SimCommandReceipt receipt = Ingress().Receive(Envelope(Batch(2u, 1, 2), 1u));

            Assert.AreEqual(SimCommandRejectReason.WrongPeer, receipt.Reason,
                "A batch from peer 2 wrapped in peer 1's envelope must not drive peer 1's body.");
        }

        [Test]
        public void Ingress_WrongSession_IsRefusedTwiceOver()
        {
            SimCommandReceipt envelopeReject = Ingress().Receive(Envelope(Batch(1u, 1, 2), 1u, 1u, 42UL));
            Assert.AreEqual(SimCommandRejectReason.WrongSession, envelopeReject.Reason);

            SimPlayerCommandBatch batch = Batch(1u, 1, 2);
            batch.SessionId = 42UL;
            SimCommandReceipt payloadReject = Ingress().Receive(Envelope(batch));
            Assert.AreEqual(SimCommandRejectReason.WrongSession, payloadReject.Reason,
                "The payload repeats the session because a relay is free to re-stamp an envelope.");
        }

        [Test]
        public void Ingress_WrongKind_IsRefused()
        {
            SimReplicationEnvelope envelope = Envelope(Batch(1u, 1, 2));
            envelope.Kind = SimReplicationMessageKind.SnapshotAck;

            Assert.AreEqual(SimCommandRejectReason.WrongKind, Ingress().Receive(envelope).Reason);
        }

        [Test]
        public void Ingress_SlotSpoof_IsRefused()
        {
            SimPlayerCommandBatch batch = Batch(1u, 1, 2);
            batch.Commands[0].SlotIndex = 0;

            Assert.AreEqual(SimCommandRejectReason.SlotNotOwnedByPeer,
                Ingress().Receive(Envelope(batch)).Reason);
        }

        [Test]
        public void Ingress_HoledTicks_AreRefused()
        {
            SimPlayerCommandBatch batch = Batch(1u, 1, 2);
            batch.Commands[1].TickIndex = 3;

            Assert.AreEqual(SimCommandRejectReason.NonContiguousTicks,
                Ingress().Receive(Envelope(batch)).Reason);
        }

        [Test]
        public void Ingress_RewoundSequences_AreRefused()
        {
            SimPlayerCommandBatch batch = Batch(1u, 1, 2, 3);
            batch.Commands[2].Sequence = 2u;

            Assert.AreEqual(SimCommandRejectReason.SequencesNotIncreasing,
                Ingress().Receive(Envelope(batch)).Reason);
        }

        [Test]
        public void Ingress_MalformedPayload_IsRefusedWithoutThrowing()
        {
            SimReplicationEnvelope envelope = Envelope(Batch(1u, 1, 2));
            envelope.Payload = new byte[] { 1, 2, 3 };

            SimCommandIngress ingress = Ingress();
            Assert.AreEqual(SimCommandRejectReason.MalformedBatch, ingress.Receive(envelope).Reason,
                "A corrupt packet from one peer must not take the host's tick down.");
            Assert.AreEqual(1, ingress.RejectedBatchCount);
            Assert.AreEqual(0, ingress.AcceptedBatchCount);
        }

        [Test]
        public void Ingress_UnknownPeer_IsRefusedBeforeThePayloadIsRead()
        {
            SimReplicationEnvelope envelope = Envelope(Batch(1u, 1, 2));
            envelope.Payload = new byte[] { 1, 2, 3 };   // malformed on purpose

            Assert.AreEqual(SimCommandRejectReason.UnknownPeer,
                Ingress(joined: peerId => false).Receive(envelope).Reason);
        }

        [Test]
        public void Ingress_Atomicity_ARefusedBatchLeavesNoTrace()
        {
            SimCommandIngress ingress = Ingress();
            SimPlayerCommandBatch good = Batch(1u, 1, 2);
            Assert.IsTrue(ingress.Receive(Envelope(good)).Applied);

            SimPlayerCommandBatch forged = Batch(1u, 1, 2, 3);
            forged.Commands[2].MoveX = 99;               // fails sequence-independent integrity below
            forged.Commands[2].TickIndex = 2;            // non-contiguous ticks
            SimCommandReceipt refused = ingress.Receive(Envelope(forged, 1u, 2u));

            Assert.IsFalse(refused.Applied);
            Assert.AreEqual(2, ingress.LastAppliedTick(1u));
            Assert.AreEqual(1, ingress.AcceptedBatchCount);
            Assert.AreEqual(1, ingress.RejectedBatchCount);
            Assert.AreEqual(2, ingress.BufferedCommandCount(1u));
        }

        // ── Protocol v5 ───────────────────────────────────────────────────────

        [Test]
        public void ProtocolV5_CommandKindAndVersionArePinnedByLiterals()
        {
            Assert.AreEqual(5u, SimSnapshotSerializer.CurrentProtocolVersion,
                "The literal, not the symbol: a symbol compared against itself cannot see a rollback.");
            Assert.AreEqual(SimSnapshotSerializer.CurrentProtocolVersion,
                SimReplicationProtocolRules.CurrentProtocolVersion,
                "The handshake must share the codec version, not maintain a second number.");
            Assert.AreEqual(8, (int)SimReplicationMessageKind.PlayerCommandBatch,
                "The first intent-carrying kind must have a stable wire value.");
            Assert.AreEqual(9, Enum.GetValues(typeof(SimReplicationMessageKind)).Length,
                "None plus eight real kinds; a dead or skipped value is a protocol defect.");
        }

        [Test]
        public void ProtocolV5_HandshakeRejectsAPeerThatDeclaredV4()
        {
            var session = new Session();
            session.Client.Join(Interest(), 4u);
            foreach (SimReplicationEnvelope envelope in session.TakeToHost()) session.Host.Receive(envelope);

            SimReplicationEnvelope[] rejects = OfKind(session.TakeToClient(),
                SimReplicationMessageKind.JoinReject);
            Assert.AreEqual(1, rejects.Length,
                "A v4 peer would join, receive state and never send intent; refuse it at the handshake.");
            Assert.AreEqual(SimJoinRejectReason.IncompatibleProtocol,
                SimReplicationPayloadSerializer.ReadJoinReject(rejects[0].Payload).Reason);
        }

        [Test]
        public void ProtocolV5_JoinReject_WhenNoControllerSlotIsLeft()
        {
            var session = new Session();
            FieldInfo field = typeof(SimReplicationHost).GetField("_nextPeerId",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, "The peer allocator must exist for this guard to be reachable.");
            field.SetValue(session.Host, 256u);

            session.Client.Join(Interest());
            foreach (SimReplicationEnvelope envelope in session.TakeToHost()) session.Host.Receive(envelope);

            SimReplicationEnvelope[] rejects = OfKind(session.TakeToClient(),
                SimReplicationMessageKind.JoinReject);
            Assert.AreEqual(1, rejects.Length);
            Assert.AreEqual(SimJoinRejectReason.NoControllerSlot,
                SimReplicationPayloadSerializer.ReadJoinReject(rejects[0].Payload).Reason,
                "Admitting a peer that can never send intent is indistinguishable from an idle player.");
        }
    }
}
