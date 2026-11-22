using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using PFE.Core.Input;
using PFE.Sim.Content;
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

        private sealed class TransitionRecordingSink : ISimRoomTransitionSink
        {
            public int Calls;
            public uint LastPeerId;
            public byte LastSlot;
            public string LastRoomId;
            public float LastX;
            public float LastY;

            public void Apply(uint peerId, byte slot, string roomId, float worldPixelX, float worldPixelY)
            {
                Calls++;
                LastPeerId = peerId;
                LastSlot = slot;
                LastRoomId = roomId;
                LastX = worldPixelX;
                LastY = worldPixelY;
            }
        }

        private sealed class Session
        {
            public readonly SimReplicationOutbox ToClient = new SimReplicationOutbox();
            public readonly SimReplicationOutbox ToHost = new SimReplicationOutbox();
            public readonly FakeSource Source = new FakeSource();
            public readonly RecordingSink Sink;
            public readonly TransitionRecordingSink TransitionSink;
            public readonly SimReplicationHost Host;
            public readonly SimReplicationClient Client;

            /// <summary>ONE persistent tape, like the shipping PlayerCommandTape. A fresh tape per
            /// send would re-number every overlapping command's sequence and make the overlap
            /// integrity guard (correctly) refuse an honest redundant window.</summary>
            public readonly PlayerCommandBuffer Tape = new PlayerCommandBuffer(64);
            private uint _nextSequence = 1u;

            public SimPlayerCommandBatch LastSentBatch { get; private set; }

            public Session(bool withSink = true, bool withWindow = true,
                bool withTransitionSink = true)
            {
                Sink = withSink ? new RecordingSink() : null;
                TransitionSink = withTransitionSink ? new TransitionRecordingSink() : null;
                Host = new SimReplicationHost(SessionId, Source, ToClient, ChunkSize,
                    withWindow ? Window : 0, Sink, TransitionSink);
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
                Assert.AreEqual(stream.Length, stream.Position,
                    "The v10 codec is variable width but exact: a reader must stop precisely where " +
                    "the writer stopped, never consume a following record's bytes.");
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

        // ── v10 conditional encoding + interned weapon key ──────────────────────

        [Test]
        public void PlayerCommand_Codec_ConditionalEncoding_RoundTripsEveryBranch()
        {
            AssertConditionalRoundTrip(Command(5, 1u));

            PlayerCommand interact = Command(6, 2u);
            interact.Buttons |= PlayerCommand.Button.InteractPressed;
            interact.TargetRoomHash = 0xAAAA000000000001UL;
            interact.TargetObjectIndex = 7;
            AssertConditionalRoundTrip(interact);

            PlayerCommand teleport = Command(7, 3u);
            teleport.Buttons |= PlayerCommand.Button.TeleportHeld;
            teleport.CursorWorldX = 123.5f;
            teleport.CursorWorldY = -456.25f;
            AssertConditionalRoundTrip(teleport);

            PlayerCommand both = interact;
            both.Buttons |= PlayerCommand.Button.TeleportHeld;
            both.CursorWorldX = 1f;
            both.CursorWorldY = 2f;
            AssertConditionalRoundTrip(both);
        }

        private static void AssertConditionalRoundTrip(PlayerCommand source)
        {
            PlayerCommand restored = DeserializeCommand(SerializeCommand(source));
            Assert.IsTrue(source.Equals(restored),
                "Every set field must survive the wire; Buttons gate which fields are on it.");
            if (!source.InteractPressed)
            {
                Assert.AreEqual(0UL, restored.TargetRoomHash,
                    "A non-interact command must read a zero target, not a stale struct value.");
                Assert.AreEqual(0, restored.TargetObjectIndex);
            }
            if (!source.TeleportHeld)
            {
                Assert.AreEqual(0f, restored.CursorWorldX,
                    "A non-teleport command must read a zero cursor, not a stale struct value.");
                Assert.AreEqual(0f, restored.CursorWorldY);
            }
        }

        [Test]
        public void PlayerCommand_Codec_AnyTruncationInsideAConditionalBlock_Throws()
        {
            PlayerCommand both = Command(8, 4u);
            both.Buttons |= PlayerCommand.Button.InteractPressed | PlayerCommand.Button.TeleportHeld;
            both.TargetRoomHash = 1UL;
            both.TargetObjectIndex = 2;
            both.CursorWorldX = 3f;
            both.CursorWorldY = 4f;
            byte[] full = SerializeCommand(both);
            Assert.AreEqual(39, full.Length, "Both flags set is the 39-byte v10 maximum.");

            // Every cut — inside the head, inside the target block, between the blocks, inside the
            // cursor block — must throw, not fabricate: the reader branches on Buttons and reads
            // exactly what the writer wrote.
            foreach (int cut in new[] { 0, 1, 18, 20, 25, 30, 32, 35, 38 })
            {
                var shortened = new byte[cut];
                Array.Copy(full, shortened, cut);
                int length = cut;
                Assert.Throws<EndOfStreamException>(() =>
                {
                    using (var stream = new MemoryStream(shortened, false))
                    using (var reader = new BinaryReader(stream))
                    {
                        PlayerCommand.ReadFrom(reader);
                    }
                }, "A payload cut to " + length + " of 39 bytes must throw, not fabricate a command.");
            }
        }

        [Test]
        public void PlayerCommand_WeaponKey_RoundTripsAtTheKeySpaceExtremes()
        {
            PlayerCommand source = Command(9, 5u);
            source.WeaponKey = ushort.MaxValue;

            PlayerCommand restored = DeserializeCommand(SerializeCommand(source));

            Assert.AreEqual(ushort.MaxValue, restored.WeaponKey,
                "The interned key must survive the wire bit-for-bit.");
            Assert.IsTrue(source.Equals(restored));
        }

        [Test]
        public void WeaponKey_HostBoundary_KeyResolvesBackToTheContentHash()
        {
            // The identity the whole conversion must preserve: for every weapon the table knows,
            // key → hash at the host boundary is the same hash the producer sent pre-v10
            // (SimUnitState.WeaponIdHash keeps speaking LootItemKey(id, true)).
            ContentInternTable table = ContentInternTable.Build(
                new[] { "pistol", "plasma_rifle", "rusty_knife" });

            foreach (string id in new[] { "pistol", "plasma_rifle", "rusty_knife" })
            {
                ushort key = SimWeaponKeyRules.ResolveWeaponKey(table, id);
                Assert.AreNotEqual(ContentInternTable.Uninterned, key, id + " must intern.");
                Assert.AreEqual(SimSnapshotProjectionRules.LootItemKey(id, true),
                    SimWeaponKeyRules.ResolveWeaponHash(table, key),
                    "key → content id → LootItemKey(id, true) must reproduce the pre-v10 hash for " + id);
            }
        }

        [Test]
        public void WeaponKey_UnarmedAndUnknownKeysResolveToZeroHash()
        {
            ContentInternTable table = ContentInternTable.Build(new[] { "pistol" });

            Assert.AreEqual(0UL, SimWeaponKeyRules.ResolveWeaponHash(table, ContentInternTable.Uninterned),
                "Unarmed (key 0) is 'nothing equipped' on the unit record.");
            Assert.AreEqual(0UL, SimWeaponKeyRules.ResolveWeaponHash(table, 42),
                "A key the table does not hold is a content-version disagreement, not a hash to guess.");
            Assert.AreEqual(ContentInternTable.Uninterned, SimWeaponKeyRules.ResolveWeaponKey(table, null));
            Assert.AreEqual(ContentInternTable.Uninterned,
                SimWeaponKeyRules.ResolveWeaponKey(table, "no_such_weapon"));
        }

        [Test]
        public void WeaponKey_SurvivesTheWholeUplink_ToTheHostSink()
        {
            var session = new Session();
            session.Join();

            PlayerCommand command = Command(1, 1u);
            command.WeaponKey = 7;
            var batch = new SimPlayerCommandBatch
            {
                SessionId = SessionId,
                PeerId = session.Client.PeerId,
                Commands = new[] { command },
            };
            session.Deliver(Envelope(batch, session.Client.PeerId));

            Assert.AreEqual(1, session.Sink.Applied.Count);
            Assert.AreEqual((ushort)7, session.Sink.Applied[0].WeaponKey,
                "The pure layer carries the interned key to the sink unchanged; PfeRemoteBody resolves " +
                "it back to the content hash at the body boundary.");
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
        public void EndToEnd_DuplicateEnvelope_IsRefusedByTheCommandChannelWatermark()
        {
            var session = new Session();
            session.Join();
            SimReplicationEnvelope[] items = session.Send(Window, 1, 2);

            session.Deliver(items);
            session.Deliver(items);   // exact re-delivery, same command envelope sequence

            Assert.AreEqual(1, session.Sink.Calls,
                "A re-delivered batch must not re-enter through the ingress.");
            Assert.AreEqual(1, session.Host.AcceptedCommandBatchCount);
        }

        [Test]
        public void EndToEnd_FastCommandBatch_DoesNotAgeOutASlowerAckOrTransition()
        {
            var session = new Session();
            // A manual join: the Session helper consumes only the accept, and the baseline chunks
            // it drops are exactly what produces the ack this test guards.
            session.Client.Join(Interest());
            session.Deliver(session.TakeToHost());
            SimReplicationEnvelope[] toClient = session.TakeToClient();
            foreach (SimReplicationEnvelope accept in OfKind(toClient,
                         SimReplicationMessageKind.JoinAccept))
                session.Client.Receive(ThroughBytes(accept));
            foreach (SimReplicationEnvelope chunk in OfKind(toClient,
                         SimReplicationMessageKind.BaselineChunk))
                session.Client.Receive(ThroughBytes(chunk));

            SimReplicationEnvelope[] acks = OfKind(session.TakeToHost(),
                SimReplicationMessageKind.SnapshotAck);
            Assert.AreEqual(1, acks.Length, "The arrange step must produce the ack the test guards.");

            // The client then declares its room and publishes two overlapping windows. On the
            // wire the unreliable batches can overtake ALL THREE reliable envelopes; the second
            // batch carries the higher command sequence and lands first.
            Assert.IsTrue(session.Client.SendRoomTransition("room_1_0", 5f, 6f));
            SimReplicationEnvelope[] transition = session.TakeToHost();
            SimReplicationEnvelope[] first = session.Send(Window, 1, 2);
            SimReplicationEnvelope[] second = session.Send(Window, 3);

            session.Deliver(second[0]);
            session.Deliver(acks[0]);
            session.Deliver(transition[0]);
            session.Deliver(first[0]);   // overtaken on the command stream: stale there only

            Assert.AreEqual(1, session.Host.AcceptedCommandBatchCount);
            Assert.AreEqual(1, session.Host.AppliedAckCount,
                "A fast command batch must not commit the reliable watermark: the ack is older on " +
                "that stream and would be refused as stale.");
            Assert.AreEqual(1, session.Host.AcceptedRoomTransitionCount,
                "The transition is equally older on the reliable stream and must survive too.");
            Assert.AreEqual(1, session.Host.RejectedEnvelopeCount,
                "Only the overtaken first batch is refused — and refused on the command watermark, " +
                "not by anything the reliable stream committed.");
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
        public void EndToEnd_NoCommandWindow_RefusesBeforeMovingAnyWatermark()
        {
            var session = new Session(withSink: false, withWindow: false);
            session.Join();

            SimReplicationEnvelope[] items = session.Send(Window, 1, 2);
            session.Deliver(items);
            Assert.IsNull(session.Host.Commands);
            Assert.AreEqual(1, session.Host.RejectedEnvelopeCount);

            // The refused batch is stamped on the command counter; the reliable stream is probed
            // with the real client's next reliable envelope. If the refusal had advanced either
            // watermark, this legitimate control packet would now look stale too.
            session.Client.UpdateInterest(Interest());
            session.Deliver(session.TakeToHost());

            Assert.AreEqual(1, session.Host.RejectedEnvelopeCount,
                "A refused command kind must not burn the reliable channel that acks still need.");
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

        // ── Protocol v5 / v6 / v7 / v8 / v9 ─────────────────────────────────────

        [Test]
        public void ProtocolV10_CommandKindAndVersionArePinnedByLiterals()
        {
            Assert.AreEqual(10u, SimSnapshotSerializer.CurrentProtocolVersion,
                "The literal, not the symbol: a symbol compared against itself cannot see a rollback. " +
                "v10 re-encoded the wire in place (unit flags byte + quantized scalars, motor flags " +
                "ushort, command interned weapon key + conditional target/cursor), so a v9 peer would " +
                "misread the first record of every payload and must be refused at the handshake. " +
                "(v9 grew SimRoomDelta by the door-state set; v8 grew SimUnitState by WeaponIdHash + " +
                "AimAngle; v7 grew the command by the interact target.)");
            Assert.AreEqual(SimSnapshotSerializer.CurrentProtocolVersion,
                SimReplicationProtocolRules.CurrentProtocolVersion,
                "The handshake must share the codec version, not maintain a second number.");
            Assert.AreEqual(8, (int)SimReplicationMessageKind.PlayerCommandBatch,
                "The first intent-carrying kind must have a stable wire value.");
            Assert.AreEqual(9, (int)SimReplicationMessageKind.PeerRoomTransition,
                "The room-transition kind must have a stable wire value.");
            Assert.AreEqual(10, Enum.GetValues(typeof(SimReplicationMessageKind)).Length,
                "None plus nine real kinds; a dead or skipped value is a protocol defect.");
        }

        [Test]
        public void ProtocolV10_TheCommandWireSizesArePinned()
        {
            // A command's byte width is a protocol fact, not an implementation detail: the batch is a
            // run of variable-width records read back to back, so one reordered field silently
            // re-frames every command after it. Pinned as literals so a packing regression is caught
            // by name and forces the version bump in the same commit.
            //
            // v10 fixed head: int tick + byte slot + uint seq + 2 sbyte axes + float aim + ushort
            // weapon key + ushort buttons = 19 bytes. Interact adds targetRoomHash u64 +
            // targetObjectIndex i32 (+12); TeleportHeld adds cursorWorldX/Y f32 (+8). Maximum 39.
            PlayerCommand plain = PlayerCommand.FromIntent(1, 0, 1u, 0f, 0f, false, false, false,
                false, false, 0f, weaponKey: 9);

            Assert.AreEqual(19, WireSize(in plain),
                "The v10 fixed head must stay 19 bytes; a different width means a re-frame, " +
                "not a quiet resize.");

            PlayerCommand interact = PlayerCommand.FromIntent(1, 0, 1u, 0f, 0f, false, false,
                false, false, false, 0f, interactPressed: true, targetRoomHash: 1UL,
                targetObjectIndex: 2, weaponKey: 9);
            PlayerCommand teleport = PlayerCommand.FromIntent(1, 0, 1u, 0f, 0f, false, false,
                false, false, false, 0f, teleportHeld: true, cursorWorldX: 3f, cursorWorldY: 4f);
            var both = interact;
            both.Buttons |= PlayerCommand.Button.TeleportHeld;
            both.CursorWorldX = 3f;
            both.CursorWorldY = 4f;

            Assert.AreEqual(31, WireSize(in interact),
                "InteractPressed must add exactly the 12-byte target to the head.");
            Assert.AreEqual(27, WireSize(in teleport),
                "TeleportHeld must add exactly the 8-byte cursor to the head.");
            Assert.AreEqual(39, WireSize(in both),
                "Both flags set is the 39-byte maximum.");

            // Absent controls: a non-interact command must NOT carry the 12-byte target even when
            // the struct's target fields are populated — the encoder branches on the button bit.
            var carriedButNotPressed = PlayerCommand.FromIntent(1, 0, 1u, 0f, 0f, false, false,
                false, false, false, 0f, targetRoomHash: 1UL, targetObjectIndex: 2);
            Assert.AreEqual(19, WireSize(in carriedButNotPressed),
                "The wire is what Buttons say, not what the struct fields happen to hold.");
        }

        private static int WireSize(in PlayerCommand command)
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                command.WriteTo(writer);
            }
            return (int)stream.Length;
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

        // ── Peer room transitions (v6) ────────────────────────────────────────

        [Test]
        public void RoomTransitionPayload_RoundTripsThroughTheCodec()
        {
            var value = new SimPeerRoomTransition
            {
                SessionId = SessionId,
                PeerId = 1u,
                RoomId = "room_1_0",
                WorldPixelX = 123.5f,
                WorldPixelY = -456.25f,
            };

            SimPeerRoomTransition restored = SimReplicationPayloadSerializer.ReadPeerRoomTransition(
                SimReplicationPayloadSerializer.Serialize(value));

            Assert.AreEqual(value, restored,
                "Room id and both world-pixel coordinates must survive the bytes, not just the struct.");
        }

        [Test]
        public void RoomTransitionPayload_RefusesOversizedRoomIdAndNonFinitePosition()
        {
            var oversized = new SimPeerRoomTransition
            {
                SessionId = SessionId,
                PeerId = 1u,
                RoomId = new string('x', SimPeerRoomTransition.MaxRoomIdLength + 1),
                WorldPixelX = 1f,
                WorldPixelY = 2f,
            };
            Assert.Throws<InvalidDataException>(() =>
                SimReplicationPayloadSerializer.ReadPeerRoomTransition(
                    SimReplicationPayloadSerializer.Serialize(oversized)));

            var nan = new SimPeerRoomTransition
            {
                SessionId = SessionId,
                PeerId = 1u,
                RoomId = "room_1_0",
                WorldPixelX = float.NaN,
                WorldPixelY = 2f,
            };
            Assert.Throws<InvalidDataException>(() =>
                SimReplicationPayloadSerializer.ReadPeerRoomTransition(
                    SimReplicationPayloadSerializer.Serialize(nan)));
        }

        [Test]
        public void RoomTransition_ClientRefusesToSendBeforeJoining()
        {
            var session = new Session();

            Assert.IsFalse(session.Client.SendRoomTransition("room_1_0", 1f, 2f));
            Assert.AreEqual(1, session.Client.RejectedRoomTransitionSendCount);
            Assert.AreEqual(0, session.Client.RoomTransitionsSent);
            Assert.AreEqual(0, session.TakeToHost().Length, "A refused send must not emit an envelope.");
        }

        [Test]
        public void RoomTransition_ClientSendsDeclarationAfterJoin()
        {
            var session = new Session();
            session.Join();

            Assert.IsTrue(session.Client.SendRoomTransition("room_0_0", 100f, 200f));

            SimReplicationEnvelope[] items = session.TakeToHost();
            Assert.AreEqual(1, items.Length);
            Assert.AreEqual(SimReplicationMessageKind.PeerRoomTransition, items[0].Kind);
            Assert.AreEqual(1, session.Client.RoomTransitionsSent);

            SimPeerRoomTransition payload =
                SimReplicationPayloadSerializer.ReadPeerRoomTransition(items[0].Payload);
            Assert.AreEqual(SessionId, payload.SessionId);
            Assert.AreEqual(items[0].PeerId, payload.PeerId, "The declaration must name the host-assigned peer.");
            Assert.AreEqual("room_0_0", payload.RoomId);
            Assert.AreEqual(100f, payload.WorldPixelX);
            Assert.AreEqual(200f, payload.WorldPixelY);
        }

        [Test]
        public void RoomTransition_HostAppliesTheAdmittedDeclaration()
        {
            var session = new Session();
            session.Join();
            Assert.IsTrue(session.Client.SendRoomTransition("room_1_0", 10f, 20f));

            session.Deliver(session.TakeToHost());

            Assert.AreEqual(1, session.Host.AcceptedRoomTransitionCount);
            Assert.AreEqual(0, session.Host.RejectedRoomTransitionCount);
            Assert.AreEqual(1, session.TransitionSink.Calls);
            Assert.AreEqual(1u, session.TransitionSink.LastPeerId);
            Assert.AreEqual((byte)1, session.TransitionSink.LastSlot,
                "The sink must receive the host-assigned controller slot, not a locally invented one.");
            Assert.AreEqual("room_1_0", session.TransitionSink.LastRoomId);
            Assert.AreEqual(10f, session.TransitionSink.LastX);
            Assert.AreEqual(20f, session.TransitionSink.LastY);
        }

        [Test]
        public void RoomTransition_HostRefusesAnUnknownPeer()
        {
            var session = new Session();
            var envelope = new SimReplicationEnvelope
            {
                ProtocolVersion = SimReplicationProtocolRules.CurrentProtocolVersion,
                SessionId = SessionId,
                PeerId = 77u,
                Kind = SimReplicationMessageKind.PeerRoomTransition,
                Sequence = 1u,
                Payload = SimReplicationPayloadSerializer.Serialize(new SimPeerRoomTransition
                {
                    SessionId = SessionId,
                    PeerId = 77u,
                    RoomId = "room_1_0",
                    WorldPixelX = 1f,
                    WorldPixelY = 1f,
                }),
            };

            session.Deliver(envelope);

            Assert.AreEqual(0, session.Host.AcceptedRoomTransitionCount);
            Assert.AreEqual(1, session.Host.RejectedEnvelopeCount);
            Assert.AreEqual(0, session.TransitionSink.Calls);
        }

        [Test]
        public void RoomTransition_ADuplicateDeliveryMovesTheBodyOnce()
        {
            var session = new Session();
            session.Join();
            Assert.IsTrue(session.Client.SendRoomTransition("room_1_0", 5f, 6f));
            SimReplicationEnvelope[] items = session.TakeToHost();
            Assert.AreEqual(1, items.Length);

            session.Deliver(items[0]);
            session.Deliver(items[0]);

            Assert.AreEqual(1, session.Host.AcceptedRoomTransitionCount);
            Assert.AreEqual(1, session.TransitionSink.Calls,
                "A re-delivered transition on the ordered channel must not relocate the body twice.");
        }

        [Test]
        public void RoomTransition_CommandBatchesUseTheirOwnChannelWatermark()
        {
            var session = new Session();
            session.Join();

            // The command batch is published first, then the transition. On the wire the reliable
            // transition can arrive before the unreliable batch...
            SimReplicationEnvelope[] commands = session.Send(Window, 1, 2);
            Assert.IsTrue(session.Client.SendRoomTransition("room_1_0", 5f, 6f));
            SimReplicationEnvelope[] transition = session.TakeToHost();
            Assert.AreEqual(1, transition.Length);

            // ...and committing the reliable watermark first must not age the batch out: the batch
            // is stamped from the command counter, not the reliable one. (The pre-1A shared
            // watermark refused it as stale here — the exact mixing failure SendCommands used to
            // document.)
            session.Deliver(transition[0]);
            session.Deliver(commands[0]);

            Assert.AreEqual(1, session.Host.AcceptedRoomTransitionCount);
            Assert.AreEqual(1, session.Host.AcceptedCommandBatchCount,
                "A batch on its own channel must be admitted even when a reliable envelope " +
                "committed the other watermark first.");
            Assert.AreEqual(0, session.Host.RejectedEnvelopeCount);
        }

        [Test]
        public void RoomTransition_HostRefusesMismatchedInnerSessionAndPeer()
        {
            var session = new Session();
            session.Join();
            Assert.IsTrue(session.Client.SendRoomTransition("room_1_0", 5f, 6f));
            SimReplicationEnvelope sent = session.TakeToHost()[0];

            var forgedSession = new SimReplicationEnvelope
            {
                ProtocolVersion = sent.ProtocolVersion,
                SessionId = sent.SessionId,
                PeerId = sent.PeerId,
                Kind = sent.Kind,
                Sequence = sent.Sequence + 1u,
                Payload = SimReplicationPayloadSerializer.Serialize(new SimPeerRoomTransition
                {
                    SessionId = SessionId + 1UL,
                    PeerId = sent.PeerId,
                    RoomId = "room_1_0",
                    WorldPixelX = 5f,
                    WorldPixelY = 6f,
                }),
            };
            var forgedPeer = new SimReplicationEnvelope
            {
                ProtocolVersion = sent.ProtocolVersion,
                SessionId = sent.SessionId,
                PeerId = sent.PeerId,
                Kind = sent.Kind,
                Sequence = sent.Sequence + 2u,
                Payload = SimReplicationPayloadSerializer.Serialize(new SimPeerRoomTransition
                {
                    SessionId = SessionId,
                    PeerId = sent.PeerId + 1u,
                    RoomId = "room_1_0",
                    WorldPixelX = 5f,
                    WorldPixelY = 6f,
                }),
            };

            session.Deliver(sent, forgedSession, forgedPeer);

            Assert.AreEqual(1, session.Host.AcceptedRoomTransitionCount, "Only the honest envelope is admitted.");
            Assert.AreEqual(2, session.Host.RejectedRoomTransitionCount);
            Assert.AreEqual(1, session.TransitionSink.Calls);
        }

        [Test]
        public void RoomTransition_HostCountsAMalformedPayloadAsARefusedTransition()
        {
            var session = new Session();
            session.Join();
            var envelope = new SimReplicationEnvelope
            {
                ProtocolVersion = SimReplicationProtocolRules.CurrentProtocolVersion,
                SessionId = SessionId,
                PeerId = 1u,
                Kind = SimReplicationMessageKind.PeerRoomTransition,
                Sequence = 2u,
                Payload = new byte[] { 1, 2, 3 },
            };

            session.Deliver(envelope);

            Assert.AreEqual(0, session.Host.AcceptedRoomTransitionCount);
            Assert.AreEqual(1, session.Host.RejectedRoomTransitionCount);
            Assert.AreEqual(0, session.TransitionSink.Calls,
                "A payload that failed to parse must never reach the sink.");
        }

        [Test]
        public void RoomTransition_AHostWithoutASinkRefusesTheKind()
        {
            var session = new Session(withTransitionSink: false);
            session.Join();
            Assert.IsTrue(session.Client.SendRoomTransition("room_1_0", 1f, 2f));

            session.Deliver(session.TakeToHost());

            Assert.AreEqual(0, session.Host.AcceptedRoomTransitionCount);
            Assert.AreEqual(1, session.Host.RejectedEnvelopeCount);
        }
    }
}
