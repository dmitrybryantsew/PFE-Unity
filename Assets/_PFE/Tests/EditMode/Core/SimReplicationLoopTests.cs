using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using PFE.Sim.Replication;
using PFE.Sim.State;

namespace PFE.Tests.EditMode.Core
{
    /// <summary>
    /// Phase 4 (F-11): the executable contract for the transport-neutral replication loop.
    ///
    /// <para><b>What "end to end" means here.</b> There is no socket and no package. Every envelope
    /// still crosses the wire as <b>bytes</b>: <see cref="SimReplicationLoopTests.ThroughBytes"/> runs
    /// <c>SimReplicationEnvelopeSerializer.Serialize</c> / <c>Deserialize</c> on each delivery, so the
    /// fixture drives <c>host → wire bytes → client → ack bytes → host → journal trim</c> using the real
    /// codec, the real snapshot serializer, the real receiver and the real producer journal. The only
    /// stand-ins are the snapshot source (an interface the production projector implements) and the
    /// outbox that plays the transport — which is exactly the seam a FishNet adapter will replace.</para>
    ///
    /// <para><b>Why the loop is driven by hand as well as pumped.</b> <see cref="Loop.Pump"/> proves the
    /// happy path converges. The interesting failures are all about <i>which packet arrives when</i>, so
    /// the rejection tests take the recorded envelopes and deliver them out of order, twice, with a
    /// forged field, or not at all. A loopback that auto-delivers everything would silently skip every
    /// one of those guards.</para>
    ///
    /// <para><b>Every guard has a positive and a negative control.</b> A guard that cannot fail is not
    /// a guard: the codec tests corrupt a known-good payload, the handshake tests both an admitted and
    /// a refused peer, and the ack tests both a peer that trims the journal and a peer that must not.</para>
    /// </summary>
    [TestFixture]
    public sealed class SimReplicationLoopTests
    {
        private const ulong RoomA = 0xAAAA000000000001UL;
        private const ulong RoomB = 0xBBBB000000000001UL;
        private const ulong RoomC = 0xCCCC000000000001UL;

        private const ulong LootA = 0xA111000000000001UL;
        private const ulong LootB = 0xB111000000000001UL;
        private const ulong LootC = 0xC111000000000001UL;
        private const ulong LootId = 0xD00D000000000001UL;

        private const ulong NonceA = 0xC11E000000000001UL;
        private const ulong NonceB = 0xC11E000000000002UL;

        private const long SourceRevision = 7L;
        private const int TileX = 3;
        private const int TileY = 4;

        // ── Source builders ───────────────────────────────────────────────────

        private static SimDroppedItem Loot(ulong entityHash, int quantity)
        {
            return new SimDroppedItem
            {
                EntityHash = entityHash,
                ItemIdHash = 0x1700000000000001UL,
                IsWeapon = false,
                Quantity = quantity,
                Variant = 0,
                Condition = 1f,
                Px = 12f,
                Py = 34f,
            };
        }

        private static SimRoomDelta Room(ulong roomHash, params SimDroppedItem[] drops)
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

        private static SimSnapshot Source(params SimRoomDelta[] rooms)
        {
            return new SimSnapshot
            {
                ProtocolVersion = SimSnapshotSerializer.CurrentProtocolVersion,
                TickIndex = 1L,
                SnapshotSequence = 1u,
                WorldRevision = SourceRevision,
                BaselineHash = 0UL,
                Rooms = rooms ?? Array.Empty<SimRoomDelta>(),
                TileMutations = Array.Empty<SimTileMutation>(),
            };
        }

        /// <summary>One destroyed wall per room: seeded as generated, then mutated, so the journal's
        /// divergence baseline has one authoritative event per room.</summary>
        private static void SeedDiverged(SimTileJournal journal, params ulong[] rooms)
        {
            for (int i = 0; i < rooms.Length; i++)
            {
                journal.Seed(rooms[i], TileX, TileY, 1, 10);
                Assert.IsTrue(journal.Record(rooms[i], TileX, TileY, 2, 5),
                    "The arrange step must actually produce a diverged cell.");
            }
        }

        private static SimPeerInterest InterestAll()
        {
            return new SimPeerInterest(RoomA, new[] { RoomB, RoomC });
        }

        private static SimPeerInterest InterestAB()
        {
            return new SimPeerInterest(RoomA, new[] { RoomB });
        }

        // ── The loop ──────────────────────────────────────────────────────────

        /// <summary>The production inputs behind the very interface the projector implements.</summary>
        private sealed class FakeSource : ISimReplicationSnapshotSource
        {
            private readonly SimTileJournal _journal = new SimTileJournal();

            public SimSnapshot Snapshot;
            public SimTileJournal Journal => _journal;
            public SimSnapshot Current => Snapshot;
        }

        private sealed class Loop
        {
            public const ulong SessionId = 0x5E5510A11UL;
            public const int ChunkSize = 16;

            public readonly SimReplicationOutbox ToClient = new SimReplicationOutbox();
            public readonly SimReplicationOutbox ToHost = new SimReplicationOutbox();
            public readonly FakeSource Source = new FakeSource();
            public readonly SimReplicationHost Host;
            public readonly List<SimReplicationClient> Clients = new List<SimReplicationClient>();

            public Loop(SimSnapshot snapshot, int chunkSize = ChunkSize)
            {
                Source.Snapshot = snapshot;
                Host = new SimReplicationHost(SessionId, Source, ToClient, chunkSize);
            }

            public SimReplicationClient AddClient(ulong nonce)
            {
                var client = new SimReplicationClient(nonce, ToHost);
                Clients.Add(client);
                return client;
            }

            public SimReplicationEnvelope[] TakeToClient()
            {
                SimReplicationEnvelope[] items = ToClient.ToArray();
                ToClient.Clear();
                return items;
            }

            public SimReplicationEnvelope[] TakeToHost()
            {
                SimReplicationEnvelope[] items = ToHost.ToArray();
                ToHost.Clear();
                return items;
            }

            public void DeliverToHost()
            {
                SimReplicationEnvelope[] items = TakeToHost();
                for (int i = 0; i < items.Length; i++)
                {
                    Host.Receive(ThroughBytes(items[i]));
                }
            }

            public void DeliverToClient(IEnumerable<SimReplicationEnvelope> items)
            {
                foreach (SimReplicationEnvelope item in items)
                {
                    SimReplicationEnvelope envelope = ThroughBytes(item);

                    // JoinReject is the one host message addressed before a peer id exists.
                    int index = envelope.Kind == SimReplicationMessageKind.JoinReject
                                && envelope.PeerId == 0u
                        ? Clients.Count - 1
                        : (int)envelope.PeerId - 1;

                    Assert.That(index, Is.InRange(0, Clients.Count - 1),
                        "Every host envelope must have a client to land on; a silently dropped packet " +
                        "would make an untested guard look green.");

                    Clients[index].Receive(envelope);
                }
            }

            /// <summary>Deliver until both outboxes are empty. The loop must converge in a handful of
            /// rounds; a chain that never settles is itself a defect.</summary>
            public void Pump()
            {
                for (int round = 0; round < 16; round++)
                {
                    bool any = ToHost.Count > 0 || ToClient.Count > 0;
                    DeliverToHost();
                    DeliverToClient(TakeToClient());
                    if (!any) return;
                }

                Assert.Fail("The replication loop did not converge after 16 delivery rounds.");
            }
        }

        /// <summary>The wire. Every crossing is a real serialize/deserialize, so a codec that drops a
        /// field cannot hide behind a loopback that passes structs by reference.</summary>
        private static SimReplicationEnvelope ThroughBytes(SimReplicationEnvelope envelope)
        {
            return SimReplicationEnvelopeSerializer.Deserialize(
                SimReplicationEnvelopeSerializer.Serialize(envelope));
        }

        private static SimReplicationEnvelope[] OfKind(SimReplicationEnvelope[] items,
            SimReplicationMessageKind kind)
        {
            var kept = new List<SimReplicationEnvelope>();
            for (int i = 0; i < items.Length; i++)
            {
                if (items[i].Kind == kind) kept.Add(items[i]);
            }
            return kept.ToArray();
        }

        private static SimReplicationEnvelope[] OfPeer(SimReplicationEnvelope[] items, uint peerId,
            SimReplicationMessageKind kind)
        {
            var kept = new List<SimReplicationEnvelope>();
            for (int i = 0; i < items.Length; i++)
            {
                if (items[i].PeerId == peerId && items[i].Kind == kind) kept.Add(items[i]);
            }
            return kept.ToArray();
        }

        private static void AssertKindCount(SimReplicationEnvelope[] items,
            SimReplicationMessageKind kind, int expected, string why)
        {
            Assert.AreEqual(expected, OfKind(items, kind).Length, why);
        }

        private static SimReplicationEnvelope AckEnvelope(SimReplicationClient client, uint sequence,
            SimSnapshotAck ack)
        {
            return new SimReplicationEnvelope
            {
                ProtocolVersion = SimReplicationProtocolRules.CurrentProtocolVersion,
                SessionId = Loop.SessionId,
                PeerId = client.PeerId,
                Kind = SimReplicationMessageKind.SnapshotAck,
                Sequence = sequence,
                Payload = SimReplicationPayloadSerializer.Serialize(ack),
            };
        }

        /// <summary>Reassemble the recorded baseline chunks and decode the very snapshot the client
        /// will admit — so a test can assert on the peer-local sequence space itself.</summary>
        private static SimSnapshot BaselineSnapshot(SimReplicationEnvelope[] recorded)
        {
            SimReplicationEnvelope[] chunkEnvelopes = OfKind(recorded,
                SimReplicationMessageKind.BaselineChunk);
            Assert.That(chunkEnvelopes.Length, Is.GreaterThan(0), "No baseline chunks were recorded.");

            var chunks = new List<SimBaselineChunk>(chunkEnvelopes.Length);
            for (int i = 0; i < chunkEnvelopes.Length; i++)
            {
                chunks.Add(SimReplicationPayloadSerializer.ReadBaselineChunk(chunkEnvelopes[i].Payload));
            }

            byte[] payload;
            SimBaselineAssemblyResult result;
            Assert.IsTrue(SimReplicationBaselineRules.TryAssemble(chunks, out payload, out result),
                "The recorded parts must reassemble on their own.");
            Assert.AreEqual(SimBaselineAssemblyResult.Ok, result);

            return SimSnapshotSerializer.Deserialize(payload);
        }

        // ── Controls and codec ────────────────────────────────────────────────

        [Test]
        public void Control_TheFixturesOwnGuardsCanFail()
        {
            Assert.Throws<AssertionException>(() => Assert.AreEqual(1, 2),
                "A green baseline is meaningless if the runner cannot report a failed assertion.");

            var onlySnapshot = new[]
            {
                new SimReplicationEnvelope { Kind = SimReplicationMessageKind.Snapshot },
            };

            Assert.Throws<AssertionException>(
                () => AssertKindCount(onlySnapshot, SimReplicationMessageKind.SnapshotAck, 1, "control"),
                "A kind counter that cannot come up short is not a guard.");

            Assert.DoesNotThrow(
                () => AssertKindCount(onlySnapshot, SimReplicationMessageKind.Snapshot, 1, "control"));
        }

        [Test]
        public void ProtocolVersion_HandshakeAndSnapshotCodecShareOneNumber()
        {
            Assert.AreEqual(SimSnapshotSerializer.CurrentProtocolVersion,
                SimReplicationProtocolRules.CurrentProtocolVersion,
                "The handshake and the payload must not be able to drift to two different versions.");
            Assert.IsTrue(SimReplicationProtocolRules.IsCompatible(
                SimSnapshotSerializer.CurrentProtocolVersion));
            Assert.IsFalse(SimReplicationProtocolRules.IsCompatible(
                SimSnapshotSerializer.CurrentProtocolVersion + 1u));
        }

        [Test]
        public void Envelope_RoundTripsEveryFieldThroughBytes()
        {
            var envelope = new SimReplicationEnvelope
            {
                ProtocolVersion = SimReplicationProtocolRules.CurrentProtocolVersion,
                SessionId = Loop.SessionId,
                PeerId = 9u,
                Kind = SimReplicationMessageKind.BaselineChunk,
                Sequence = 123u,
                Payload = new byte[] { 7, 0, 255, 1 },
            };

            byte[] bytes = SimReplicationEnvelopeSerializer.Serialize(envelope);
            SimReplicationEnvelope back = SimReplicationEnvelopeSerializer.Deserialize(bytes);

            Assert.AreEqual(envelope, back, "Every field of the wire shape must survive the codec.");
            Assert.AreEqual(new byte[] { 7, 0, 255, 1 }, back.Payload);
        }

        [Test]
        public void Envelope_RejectsBadMagicIncompatibleVersionAndTrailingBytes()
        {
            var envelope = new SimReplicationEnvelope
            {
                ProtocolVersion = SimReplicationProtocolRules.CurrentProtocolVersion,
                SessionId = Loop.SessionId,
                PeerId = 1u,
                Kind = SimReplicationMessageKind.SnapshotAck,
                Sequence = 3u,
                Payload = new byte[] { 1, 2, 3 },
            };

            byte[] good = SimReplicationEnvelopeSerializer.Serialize(envelope);
            Assert.DoesNotThrow(() => SimReplicationEnvelopeSerializer.Deserialize(good),
                "Positive control: the uncorrupted payload must be accepted.");

            byte[] badMagic = (byte[])good.Clone();
            badMagic[0] = (byte)'X';
            Assert.Throws<InvalidDataException>(
                () => SimReplicationEnvelopeSerializer.Deserialize(badMagic),
                "A packet that is not a PFE envelope must be refused before any field is read.");

            // The four magic bytes are [0..3]; the protocol version is [4..7].
            byte[] badVersion = (byte[])good.Clone();
            badVersion[4] = (byte)(badVersion[4] + 1);
            Assert.Throws<InvalidDataException>(
                () => SimReplicationEnvelopeSerializer.Deserialize(badVersion),
                "An incompatible peer must be refused by the codec, not just by the handshake.");

            var trailing = new byte[good.Length + 1];
            Array.Copy(good, trailing, good.Length);
            Assert.Throws<InvalidDataException>(
                () => SimReplicationEnvelopeSerializer.Deserialize(trailing),
                "Trailing bytes must not let a shorter message hide inside a longer packet.");
        }

        // ── Admission ─────────────────────────────────────────────────────────

        [Test]
        public void Host_RejectsForeignSessionPeerProtocolAndNullEnvelopes()
        {
            var loop = new Loop(Source(Room(RoomA)));
            SimReplicationClient client = loop.AddClient(NonceA);
            client.Join(InterestAB());
            loop.Pump();

            int rejectedBefore = loop.Host.RejectedEnvelopeCount;
            int appliedBefore = loop.Host.AppliedAckCount;

            SimReplicationEnvelope template = AckEnvelope(client, 200u, new SimSnapshotAck
            {
                WorldRevision = client.WorldRevision,
                LastEventSequence = 0u,
                SnapshotSequence = 1u,
            });

            var foreignSession = template; foreignSession.SessionId = 0UL;
            var oldProtocol = template; oldProtocol.ProtocolVersion =
                SimReplicationProtocolRules.CurrentProtocolVersion + 1u;
            var unknownPeer = template; unknownPeer.PeerId = 999u;
            var noKind = template; noKind.Kind = SimReplicationMessageKind.None;
            var noPayload = template; noPayload.Payload = null;
            var forgedJoinSession = template;
            forgedJoinSession.Kind = SimReplicationMessageKind.JoinRequest;
            forgedJoinSession.SessionId = 123UL;

            loop.Host.Receive(foreignSession);
            loop.Host.Receive(oldProtocol);
            loop.Host.Receive(unknownPeer);
            loop.Host.Receive(noKind);
            loop.Host.Receive(noPayload);
            loop.Host.Receive(forgedJoinSession);

            Assert.AreEqual(rejectedBefore + 6, loop.Host.RejectedEnvelopeCount,
                "Six forged envelopes must all be refused by value, not by a connection handle.");
            Assert.AreEqual(appliedBefore, loop.Host.AppliedAckCount,
                "A refused envelope must not move the ack water mark.");
        }

        [Test]
        public void Handshake_JoinAssignsSessionPeerAndBaseline()
        {
            var loop = new Loop(Source(Room(RoomA, Loot(LootA, 1))));
            SimReplicationClient client = loop.AddClient(NonceA);

            client.Join(InterestAB());
            loop.DeliverToHost();
            SimReplicationEnvelope[] recorded = loop.TakeToClient();

            Assert.AreEqual(1, OfKind(recorded, SimReplicationMessageKind.JoinAccept).Length,
                "Exactly one accept must be sent for one join.");
            Assert.That(OfKind(recorded, SimReplicationMessageKind.BaselineChunk).Length,
                Is.GreaterThan(0), "A join must be followed by a chunked world baseline.");

            // Positive control: the accept really is addressed to the peer that will receive it.
            SimJoinAccept accept = SimReplicationPayloadSerializer.ReadJoinAccept(
                OfKind(recorded, SimReplicationMessageKind.JoinAccept)[0].Payload);
            Assert.AreEqual(Loop.SessionId, accept.SessionId);
            Assert.AreEqual(1u, accept.PeerId);
            Assert.AreEqual(SimReplicationProtocolRules.CurrentProtocolVersion, accept.ProtocolVersion);
            Assert.That(accept.WorldRevision, Is.GreaterThan(0L),
                "The accept must name the lifetime the baseline chunks carry, not a placeholder.");

            loop.DeliverToClient(OfKind(recorded, SimReplicationMessageKind.JoinAccept));
            Assert.IsTrue(client.IsJoined);
            Assert.AreEqual(Loop.SessionId, client.SessionId);
            Assert.AreEqual(1u, client.PeerId);
            Assert.AreEqual(accept.WorldRevision, client.WorldRevision);
            Assert.AreEqual(accept.WorldRevision, loop.Host.PeerWorldRevision(1u));

            loop.DeliverToClient(OfKind(recorded, SimReplicationMessageKind.BaselineChunk));
            Assert.AreEqual(1, client.AssembledBaselineCount);
            Assert.AreEqual(SimBaselineAssemblyResult.Ok, client.LastAssemblyResult);

            loop.DeliverToHost();
            Assert.AreEqual(1, loop.Host.AppliedAckCount,
                "The baseline ack must reach the host for the loop to be closed.");
        }

        [Test]
        public void Handshake_IncompatibleDeclaredProtocolIsRefused()
        {
            var loop = new Loop(Source(Room(RoomA)));
            SimReplicationClient client = loop.AddClient(NonceA);

            client.Join(InterestAB(), SimReplicationProtocolRules.CurrentProtocolVersion + 1u);
            loop.Pump();

            Assert.IsFalse(client.IsJoined, "An old build must not be admitted.");
            Assert.AreEqual(SimJoinRejectReason.IncompatibleProtocol, client.LastJoinRejectReason);
            Assert.AreEqual(0, loop.Host.JoinedPeerCount);
            Assert.AreEqual(0, loop.Host.SentBaselineChunkCount,
                "No baseline bytes may be produced for a peer that was never admitted.");
        }

        [Test]
        public void Handshake_DuplicateNonceIsNotASecondPeer()
        {
            var loop = new Loop(Source(Room(RoomA)));
            SimReplicationClient first = loop.AddClient(NonceA);
            first.Join(InterestAB());
            loop.Pump();
            Assert.AreEqual(1, loop.Host.JoinedPeerCount);

            SimReplicationClient retry = loop.AddClient(NonceA);
            retry.Join(InterestAB());
            loop.Pump();

            Assert.AreEqual(1, loop.Host.JoinedPeerCount,
                "A retried join with the same nonce is one connection, not two reliable streams.");
            Assert.IsFalse(retry.IsJoined);
            Assert.AreEqual(SimJoinRejectReason.AlreadyJoined, retry.LastJoinRejectReason);
            Assert.AreEqual(0, retry.AssembledBaselineCount);
        }

        [Test]
        public void Handshake_DuplicateJoinAcceptIsRefused()
        {
            var loop = new Loop(Source(Room(RoomA)));
            SimReplicationClient client = loop.AddClient(NonceA);
            client.Join(InterestAB());
            loop.DeliverToHost();

            SimReplicationEnvelope[] accepts = OfKind(loop.TakeToClient(),
                SimReplicationMessageKind.JoinAccept);
            loop.DeliverToClient(accepts);
            loop.DeliverToClient(accepts);

            Assert.IsTrue(client.IsJoined);
            Assert.That(client.RejectedEnvelopeCount, Is.GreaterThanOrEqualTo(1),
                "A second accept in one attempt is a duplicate connection, not a re-join.");
            Assert.AreEqual(0, client.AssembledBaselineCount);
        }

        // ── Chunked late-join baseline ────────────────────────────────────────

        [Test]
        public void Baseline_IsSplitIntoCallerSizedChunks()
        {
            var loop = new Loop(Source(Room(RoomA, Loot(LootA, 1)), Room(RoomB, Loot(LootB, 2))), 16);
            SimReplicationClient client = loop.AddClient(NonceA);
            client.Join(InterestAB());
            loop.DeliverToHost();

            SimReplicationEnvelope[] recorded = loop.TakeToClient();
            SimReplicationEnvelope[] chunkEnvelopes = OfKind(recorded,
                SimReplicationMessageKind.BaselineChunk);

            Assert.That(chunkEnvelopes.Length, Is.GreaterThan(1),
                "A 16-byte caller-supplied chunk size must split this baseline into several parts.");
            Assert.AreEqual(chunkEnvelopes.Length, loop.Host.SentBaselineChunkCount);

            for (int i = 0; i < chunkEnvelopes.Length; i++)
            {
                SimBaselineChunk chunk = SimReplicationPayloadSerializer.ReadBaselineChunk(
                    chunkEnvelopes[i].Payload);
                Assert.AreEqual(chunkEnvelopes.Length, chunk.ChunkCount);
                Assert.AreEqual(i, chunk.ChunkIndex);
                Assert.That(chunk.Bytes.Length, Is.LessThanOrEqualTo(16));
                Assert.That(chunk.Bytes.Length, Is.GreaterThan(0));
            }

            // Arrange control: a chunk is addressed to a joined peer. Delivering parts without the
            // accept would have every one of them refused, and a zero readback would look like a
            // chunking failure rather than an unjoined client.
            loop.DeliverToClient(OfKind(recorded, SimReplicationMessageKind.JoinAccept));
            Assert.IsTrue(client.IsJoined);

            loop.DeliverToClient(chunkEnvelopes);
            Assert.AreEqual(chunkEnvelopes.Length, client.BaselineChunksReceived);
            Assert.AreEqual(1, client.AssembledBaselineCount,
                "The caller-sized parts must reassemble into exactly one world baseline.");
        }

        [Test]
        public void Baseline_ChunksAreOrderIndependentAndIdempotent()
        {
            var loop = new Loop(Source(Room(RoomA, Loot(LootA, 1)), Room(RoomB, Loot(LootB, 2))), 16);
            SimReplicationClient client = loop.AddClient(NonceA);
            client.Join(InterestAB());
            loop.DeliverToHost();

            SimReplicationEnvelope[] recorded = loop.TakeToClient();
            loop.DeliverToClient(OfKind(recorded, SimReplicationMessageKind.JoinAccept));

            SimReplicationEnvelope[] chunks = OfKind(recorded,
                SimReplicationMessageKind.BaselineChunk);

            // Reverse order, with the first part delivered a second time.
            var delivery = new List<SimReplicationEnvelope>();
            for (int i = chunks.Length - 1; i >= 0; i--) delivery.Add(chunks[i]);
            delivery.Add(chunks[0]);

            loop.DeliverToClient(delivery);

            Assert.AreEqual(1, client.AssembledBaselineCount,
                "Indexed chunks must assemble regardless of arrival order.");
            Assert.AreEqual(1, client.DuplicateChunkCount,
                "An identical re-delivered part is idempotent, not a corrupt duplicate.");
            Assert.AreEqual(SimBaselineAssemblyResult.Ok, client.LastAssemblyResult);
            Assert.AreEqual(1, client.AcksSent);
            Assert.AreEqual(0, client.RejectedSnapshotCount);
            Assert.AreEqual(1, client.Receiver.LootCount(RoomA));
        }

        [Test]
        public void Baseline_ALatePartTheHostNeverSentIsRefused()
        {
            var loop = new Loop(Source(Room(RoomA, Loot(LootA, 1)), Room(RoomB, Loot(LootB, 2))), 16);
            SimReplicationClient client = loop.AddClient(NonceA);
            client.Join(InterestAB());
            loop.DeliverToHost();

            SimReplicationEnvelope[] recorded = loop.TakeToClient();
            loop.DeliverToClient(OfKind(recorded, SimReplicationMessageKind.JoinAccept));
            SimReplicationEnvelope[] chunks = OfKind(recorded,
                SimReplicationMessageKind.BaselineChunk);
            loop.DeliverToClient(chunks);

            Assert.AreEqual(1, client.AssembledBaselineCount);
            Assert.AreEqual(SimBaselineAssemblyResult.Ok, client.LastAssemblyResult);
            int rejectedBefore = client.RejectedEnvelopeCount;
            int acksBefore = client.AcksSent;

            // Same lifetime, same revision and hash, but an index the host never emitted and bytes
            // that differ: this is neither a duplicate nor a missing part.
            SimBaselineChunk stranger = SimReplicationPayloadSerializer.ReadBaselineChunk(
                chunks[0].Payload);
            stranger.ChunkIndex = stranger.ChunkCount;
            byte[] altered = (byte[])stranger.Bytes.Clone();
            altered[0] = (byte)(altered[0] ^ 0xFF);
            stranger.Bytes = altered;

            var forged = new SimReplicationEnvelope
            {
                ProtocolVersion = chunks[0].ProtocolVersion,
                SessionId = chunks[0].SessionId,
                PeerId = chunks[0].PeerId,
                Kind = SimReplicationMessageKind.BaselineChunk,
                Sequence = chunks[0].Sequence,
                Payload = SimReplicationPayloadSerializer.Serialize(stranger),
            };

            loop.DeliverToClient(new[] { forged });

            Assert.AreEqual(rejectedBefore + 1, client.RejectedEnvelopeCount,
                "Once the lifetime is whole, an unknown part must be refused, not retained.");
            Assert.AreEqual(SimBaselineAssemblyResult.Ok, client.LastAssemblyResult,
                "A stray part must not re-run reassembly and overwrite a good result.");
            Assert.AreEqual(1, client.AssembledBaselineCount);
            Assert.AreEqual(acksBefore, client.AcksSent);
        }

        [Test]
        public void Baseline_CorruptOrMissingChunkNeverAssembles()
        {
            // A missing part must never produce a snapshot.
            var missing = new Loop(Source(Room(RoomA, Loot(LootA, 1)), Room(RoomB, Loot(LootB, 2))), 16);
            SimReplicationClient shortClient = missing.AddClient(NonceA);
            shortClient.Join(InterestAB());
            missing.DeliverToHost();
            SimReplicationEnvelope[] shortRecorded = missing.TakeToClient();
            missing.DeliverToClient(OfKind(shortRecorded, SimReplicationMessageKind.JoinAccept));
            SimReplicationEnvelope[] shortChunks = OfKind(shortRecorded,
                SimReplicationMessageKind.BaselineChunk);
            Assert.That(shortChunks.Length, Is.GreaterThan(1));

            var incomplete = new SimReplicationEnvelope[shortChunks.Length - 1];
            Array.Copy(shortChunks, incomplete, incomplete.Length);
            missing.DeliverToClient(incomplete);
            Assert.AreEqual(0, shortClient.AssembledBaselineCount);
            Assert.AreEqual(0, shortClient.AcksSent);

            // A part with altered bytes must fail the whole-payload hash.
            var corrupt = new Loop(Source(Room(RoomA, Loot(LootA, 1)), Room(RoomB, Loot(LootB, 2))), 16);
            SimReplicationClient corruptClient = corrupt.AddClient(NonceA);
            corruptClient.Join(InterestAB());
            corrupt.DeliverToHost();
            SimReplicationEnvelope[] corruptRecorded = corrupt.TakeToClient();
            corrupt.DeliverToClient(OfKind(corruptRecorded, SimReplicationMessageKind.JoinAccept));

            SimReplicationEnvelope[] chunks = OfKind(corruptRecorded,
                SimReplicationMessageKind.BaselineChunk);
            SimBaselineChunk broken = SimReplicationPayloadSerializer.ReadBaselineChunk(chunks[0].Payload);
            byte[] altered = (byte[])broken.Bytes.Clone();
            altered[0] = (byte)(altered[0] ^ 0xFF);
            broken.Bytes = altered;

            var forged = new SimReplicationEnvelope
            {
                ProtocolVersion = chunks[0].ProtocolVersion,
                SessionId = chunks[0].SessionId,
                PeerId = chunks[0].PeerId,
                Kind = SimReplicationMessageKind.BaselineChunk,
                Sequence = chunks[0].Sequence,
                Payload = SimReplicationPayloadSerializer.Serialize(broken),
            };

            var delivery = new List<SimReplicationEnvelope> { forged };
            for (int i = 1; i < chunks.Length; i++) delivery.Add(chunks[i]);
            corrupt.DeliverToClient(delivery);

            Assert.AreEqual(SimBaselineAssemblyResult.HashMismatch, corruptClient.LastAssemblyResult,
                "Reassembly must verify the hash of the whole payload, not just the part count.");
            Assert.AreEqual(0, corruptClient.AssembledBaselineCount);
            Assert.AreEqual(0, corruptClient.AcksSent);
            Assert.AreEqual(0, corruptClient.Receiver.AcceptedSnapshotCount);
        }

        // ── Snapshot ordering ─────────────────────────────────────────────────

        [Test]
        public void Snapshot_BeforeTheFinalBaselineChunkIsRefused()
        {
            var loop = new Loop(Source(Room(RoomA, Loot(LootA, 1))));
            SimReplicationClient client = loop.AddClient(NonceA);
            client.Join(InterestAB());
            loop.DeliverToHost();

            SimReplicationEnvelope[] recorded = loop.TakeToClient();
            loop.DeliverToClient(OfKind(recorded, SimReplicationMessageKind.JoinAccept));
            Assert.IsTrue(client.IsJoined);

            // The chunks are deliberately held back; a snapshot must not overtake them.
            loop.Host.Tick();
            SimReplicationEnvelope[] snapshots = OfKind(loop.TakeToClient(),
                SimReplicationMessageKind.Snapshot);
            Assert.AreEqual(1, snapshots.Length);
            loop.DeliverToClient(snapshots);

            Assert.AreEqual(1, client.RejectedSnapshotCount,
                "F-11: no snapshot may be applied before the baseline that owns the lifetime.");
            Assert.AreEqual(0, client.Receiver.AcceptedSnapshotCount);
            Assert.AreEqual(0, client.AcksSent);
        }

        [Test]
        public void Snapshot_DuplicateAndReorderedDeliveryIsRefused()
        {
            var loop = new Loop(Source(Room(RoomA, Loot(LootA, 1))));
            SimReplicationClient client = loop.AddClient(NonceA);
            client.Join(InterestAB());
            loop.Pump();

            int acceptedBefore = client.Receiver.AcceptedSnapshotCount;
            int acksBefore = client.AcksSent;
            int rejectedBefore = client.RejectedEnvelopeCount;

            loop.Host.Tick();
            SimReplicationEnvelope[] snapshots = OfKind(loop.TakeToClient(),
                SimReplicationMessageKind.Snapshot);
            Assert.AreEqual(1, snapshots.Length);

            SimReplicationEnvelope snapshot = snapshots[0];
            loop.DeliverToClient(new[] { snapshot, snapshot });

            Assert.AreEqual(acceptedBefore + 1, client.Receiver.AcceptedSnapshotCount,
                "The first delivery is applied exactly once.");
            Assert.AreEqual(acksBefore + 1, client.AcksSent,
                "A duplicate snapshot must not produce a second ack.");
            Assert.That(client.RejectedEnvelopeCount, Is.GreaterThan(rejectedBefore),
                "The re-delivery must be refused by the ordered-channel guard.");

            var stale = snapshot;
            stale.Sequence = snapshot.Sequence - 1u;
            loop.DeliverToClient(new[] { stale });
            Assert.AreEqual(acksBefore + 1, client.AcksSent,
                "An out-of-order snapshot must not be applied either.");

            loop.DeliverToHost();
            Assert.AreEqual(2, loop.Host.AppliedAckCount,
                "Baseline ack plus one snapshot ack; the duplicates never reached the host.");
        }

        [Test]
        public void Snapshot_LootAndTilesFlowThroughTheWholeLoop()
        {
            var loop = new Loop(Source(Room(RoomA, Loot(LootId, 3))));
            SeedDiverged(loop.Source.Journal, RoomA);
            SimReplicationClient client = loop.AddClient(NonceA);

            client.Join(InterestAB());
            loop.Pump();

            SimDroppedItem loot;
            Assert.IsTrue(client.Receiver.TryGetLoot(RoomA, LootId, out loot),
                "The v4 entity-keyed loot record must survive the full wire round trip.");
            Assert.AreEqual(3, loot.Quantity);

            SimTileMutation tile;
            Assert.IsTrue(client.Receiver.TryGetTile(RoomA, TileX, TileY, out tile),
                "A diverged tile from the producer journal must reach the consumer's tile map.");
            Assert.AreEqual(2, tile.PhysicsType);
            Assert.AreEqual(5, tile.HitPoints);
            Assert.AreEqual(1, loop.Host.AppliedAckCount);
        }

        // ── Interest management ──────────────────────────────────────────────

        [Test]
        public void Interest_OnlyLocalAndAdjacentRoomsAreDelivered()
        {
            var loop = new Loop(Source(
                Room(RoomA, Loot(LootA, 1)),
                Room(RoomB, Loot(LootB, 2)),
                Room(RoomC, Loot(LootC, 3))));
            SeedDiverged(loop.Source.Journal, RoomA, RoomB, RoomC);
            SimReplicationClient client = loop.AddClient(NonceA);

            client.Join(InterestAB());
            loop.DeliverToHost();
            SimReplicationEnvelope[] recorded = loop.TakeToClient();

            // Filtering is pinned against the pure rule, not against a count someone remembered.
            Assert.AreEqual(2, SimReplicationInterestRules.FilterRooms(loop.Source.Snapshot.Rooms,
                InterestAB()).Length);
            Assert.AreEqual(2, loop.Host.PeerViewCount(1u),
                "The peer's reliable stream must hold only its interesting rooms.");

            SimSnapshot baseline = BaselineSnapshot(recorded);
            Assert.AreEqual(2, baseline.Rooms.Length);
            Assert.AreEqual(2, baseline.TileMutations.Length);
            for (int i = 0; i < baseline.TileMutations.Length; i++)
            {
                Assert.IsTrue(baseline.TileMutations[i].RoomHash == RoomA
                              || baseline.TileMutations[i].RoomHash == RoomB,
                    "Only rooms in the peer's interest may appear in its reliable stream.");
            }

            loop.DeliverToClient(OfKind(recorded, SimReplicationMessageKind.JoinAccept));
            loop.DeliverToClient(OfKind(recorded, SimReplicationMessageKind.BaselineChunk));

            Assert.AreEqual(1, client.Receiver.LootCount(RoomA));
            Assert.AreEqual(1, client.Receiver.LootCount(RoomB));
            Assert.AreEqual(0, client.Receiver.LootCount(RoomC),
                "A room outside the peer's interest must never appear in its world.");
            Assert.IsFalse(client.Receiver.TryGetTile(RoomC, TileX, TileY, out _));
            Assert.IsTrue(client.Receiver.TryGetTile(RoomA, TileX, TileY, out _));

            loop.DeliverToHost();
            Assert.AreEqual(0, loop.Host.PeerViewCount(1u),
                "Once the peer acknowledges the baseline, its unacknowledged view is empty.");
        }

        [Test]
        public void Interest_FilteredJournalIsRestampedContiguouslyForThePeer()
        {
            var loop = new Loop(Source(Room(RoomA), Room(RoomB), Room(RoomC)));
            SeedDiverged(loop.Source.Journal, RoomA, RoomB, RoomC);
            SimReplicationClient client = loop.AddClient(NonceA);

            client.Join(InterestAB());
            loop.DeliverToHost();
            SimReplicationEnvelope[] recorded = loop.TakeToClient();

            SimSnapshot baseline = BaselineSnapshot(recorded);
            Assert.AreEqual(2, baseline.TileMutations.Length,
                "Interest filtering drops room C's event, so the peer must see two, not three.");

            // The pure filter does not renumber; the host does. Without the re-stamp the receiver's
            // contiguous reliable-journal check would reject this very baseline.
            SimTileMutation[] filtered = SimReplicationInterestRules.FilterJournal(
                loop.Source.Journal.ToArray(), InterestAB());
            Assert.AreEqual(2, filtered.Length);
            Assert.AreEqual(1u, filtered[0].EventSequence);
            Assert.AreEqual(2u, filtered[1].EventSequence);

            for (int i = 0; i < baseline.TileMutations.Length; i++)
            {
                Assert.AreEqual(unchecked((uint)(i + 1)), baseline.TileMutations[i].EventSequence,
                    "The peer-local stream must be 1..N after filtering, not the global numbering.");
                Assert.AreEqual(baseline.WorldRevision, baseline.TileMutations[i].WorldRevision);
            }

            loop.DeliverToClient(OfKind(recorded, SimReplicationMessageKind.JoinAccept));
            loop.DeliverToClient(OfKind(recorded, SimReplicationMessageKind.BaselineChunk));
            Assert.AreEqual(1, client.AssembledBaselineCount,
                "A gapped journal would have been refused by SimSnapshotReceiver.");
            Assert.AreEqual(2u, client.Receiver.LastEventSequence);
        }

        [Test]
        public void Interest_AddingARoomStartsANewPeerLifetime()
        {
            var loop = new Loop(Source(
                Room(RoomA, Loot(LootA, 1)),
                Room(RoomB, Loot(LootB, 2)),
                Room(RoomC, Loot(LootC, 3))));
            SimReplicationClient client = loop.AddClient(NonceA);
            client.Join(new SimPeerInterest(RoomA, Array.Empty<ulong>()));
            loop.Pump();

            long firstRevision = client.WorldRevision;
            Assert.AreEqual(0, client.Receiver.LootCount(RoomB));

            client.UpdateInterest(InterestAB());
            loop.Pump();

            // The interest update alone must not fabricate a baseline; the next tick does.
            Assert.AreEqual(1, client.AssembledBaselineCount);
            loop.Host.Tick();
            loop.Pump();

            Assert.That(client.WorldRevision, Is.GreaterThan(firstRevision),
                "Gaining a room with no history must start a new reliable lifetime.");
            Assert.AreEqual(2, client.AssembledBaselineCount);
            Assert.AreEqual(1, client.Receiver.LootCount(RoomB),
                "The fresh baseline must carry the newly interesting room.");
            Assert.AreEqual(0, client.Receiver.LootCount(RoomC));
        }

        // ── Ack ───────────────────────────────────────────────────────────────

        [Test]
        public void Ack_ReportsTheReceiversOwnReadback()
        {
            var loop = new Loop(Source(Room(RoomA), Room(RoomB)));
            SeedDiverged(loop.Source.Journal, RoomA, RoomB);
            SimReplicationClient client = loop.AddClient(NonceA);

            client.Join(InterestAB());
            loop.DeliverToHost();
            SimReplicationEnvelope[] recorded = loop.TakeToClient();
            loop.DeliverToClient(OfKind(recorded, SimReplicationMessageKind.JoinAccept));
            loop.DeliverToClient(OfKind(recorded, SimReplicationMessageKind.BaselineChunk));

            SimReplicationEnvelope[] ackEnvelopes = OfKind(loop.TakeToHost(),
                SimReplicationMessageKind.SnapshotAck);
            Assert.AreEqual(1, ackEnvelopes.Length);

            SimSnapshotAck ack = SimReplicationPayloadSerializer.ReadSnapshotAck(ackEnvelopes[0].Payload);
            Assert.AreEqual(client.Receiver.WorldRevision, ack.WorldRevision);
            Assert.AreEqual(client.Receiver.LastEventSequence, ack.LastEventSequence,
                "The ack must carry the sequence the consumer actually applied, not a guessed number.");
            Assert.AreEqual(2u, ack.LastEventSequence,
                "Two diverged cells were applied; a snapshot sequence of 1 must not be mistaken for it.");
            Assert.AreEqual(1u, ack.SnapshotSequence);
        }

        [Test]
        public void Ack_TrimsTheProducerJournal()
        {
            var loop = new Loop(Source(Room(RoomA), Room(RoomB), Room(RoomC)));
            SeedDiverged(loop.Source.Journal, RoomA, RoomB, RoomC);
            SimReplicationClient client = loop.AddClient(NonceA);

            Assert.AreEqual(3, loop.Host.JournalCount,
                "Arrange control: the producer holds three unacknowledged events.");

            client.Join(InterestAll());
            loop.Pump();

            Assert.AreEqual(1, loop.Host.AppliedAckCount);
            Assert.AreEqual(3u, loop.Source.Journal.AckedSequence);
            Assert.AreEqual(0, loop.Host.JournalCount,
                "§8's owed item: the ack must reach SimTileJournal.Acknowledge, not just the endpoint.");
            Assert.AreEqual(0, loop.Source.Journal.ToArray().Length);
        }

        [Test]
        public void Ack_OneSlowPeerKeepsTheGlobalJournalAlive()
        {
            var loop = new Loop(Source(Room(RoomA), Room(RoomB)));
            SeedDiverged(loop.Source.Journal, RoomA, RoomB);
            SimReplicationClient fast = loop.AddClient(NonceA);
            SimReplicationClient slow = loop.AddClient(NonceB);

            // Both peers join before either ack is processed: one fast peer must not be allowed to
            // discard events the second peer has not even received yet.
            fast.Join(InterestAB());
            slow.Join(InterestAB());
            loop.DeliverToHost();
            SimReplicationEnvelope[] recorded = loop.TakeToClient();
            Assert.AreEqual(2, loop.Host.JoinedPeerCount);

            loop.DeliverToClient(OfPeer(recorded, 1u, SimReplicationMessageKind.JoinAccept));
            loop.DeliverToClient(OfPeer(recorded, 1u, SimReplicationMessageKind.BaselineChunk));
            loop.DeliverToHost();

            Assert.AreEqual(1, loop.Host.AppliedAckCount);
            Assert.AreEqual(2, loop.Host.JournalCount,
                "A fast peer's ack is not evidence that every peer is done; the journal must stay.");

            loop.DeliverToClient(OfPeer(recorded, 2u, SimReplicationMessageKind.JoinAccept));
            loop.DeliverToClient(OfPeer(recorded, 2u, SimReplicationMessageKind.BaselineChunk));
            loop.DeliverToHost();

            Assert.AreEqual(2, loop.Host.AppliedAckCount);
            Assert.AreEqual(0, loop.Host.JournalCount,
                "Once the slow peer catches up, the minimum across peers may trim the prefix.");
        }

        [Test]
        public void Ack_FromAnOldLifetimeOrUnsentSnapshotIsRefused()
        {
            var loop = new Loop(Source(Room(RoomA)));
            SeedDiverged(loop.Source.Journal, RoomA);
            SimReplicationClient client = loop.AddClient(NonceA);
            client.Join(InterestAB());
            loop.Pump();

            int appliedBefore = loop.Host.AppliedAckCount;
            int rejectedBefore = loop.Host.RejectedEnvelopeCount;

            SimReplicationEnvelope oldLifetime = AckEnvelope(client, 90u, new SimSnapshotAck
            {
                WorldRevision = client.WorldRevision - 1L,
                LastEventSequence = 1u,
                SnapshotSequence = 1u,
            });
            SimReplicationEnvelope unsent = AckEnvelope(client, 91u, new SimSnapshotAck
            {
                WorldRevision = client.WorldRevision,
                LastEventSequence = 1u,
                SnapshotSequence = 9999u,
            });

            loop.Host.Receive(oldLifetime);
            loop.Host.Receive(unsent);

            Assert.AreEqual(appliedBefore, loop.Host.AppliedAckCount);
            Assert.AreEqual(rejectedBefore + 2, loop.Host.RejectedEnvelopeCount,
                "An ack from another lifetime, or for a snapshot never sent, is not evidence of anything.");
        }

        [Test]
        public void Ack_DuplicateClientAckIsRefused()
        {
            var loop = new Loop(Source(Room(RoomA)));
            SeedDiverged(loop.Source.Journal, RoomA);
            SimReplicationClient client = loop.AddClient(NonceA);
            client.Join(InterestAB());
            loop.DeliverToHost();
            SimReplicationEnvelope[] recorded = loop.TakeToClient();
            loop.DeliverToClient(OfKind(recorded, SimReplicationMessageKind.JoinAccept));
            loop.DeliverToClient(OfKind(recorded, SimReplicationMessageKind.BaselineChunk));

            SimReplicationEnvelope[] acks = OfKind(loop.TakeToHost(),
                SimReplicationMessageKind.SnapshotAck);
            Assert.AreEqual(1, acks.Length);

            loop.Host.Receive(ThroughBytes(acks[0]));
            Assert.AreEqual(1, loop.Host.AppliedAckCount);
            loop.Host.Receive(ThroughBytes(acks[0]));

            Assert.AreEqual(1, loop.Host.AppliedAckCount,
                "A re-delivered ack must not be applied twice.");
            Assert.That(loop.Host.RejectedEnvelopeCount, Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        public void Client_RejectsEnvelopesFromAnotherSessionPeerOrProtocol()
        {
            var loop = new Loop(Source(Room(RoomA, Loot(LootA, 1))));
            SimReplicationClient client = loop.AddClient(NonceA);
            client.Join(InterestAB());
            loop.Pump();

            loop.Host.Tick();
            SimReplicationEnvelope[] snapshots = OfKind(loop.TakeToClient(),
                SimReplicationMessageKind.Snapshot);
            Assert.AreEqual(1, snapshots.Length);
            SimReplicationEnvelope snapshot = snapshots[0];

            // Positive control, and the premise of the "stale" case: admit the real packet first so
            // its sequence becomes the client's high-water mark. Without this delivery the mark is
            // still the JoinAccept, and a packet one behind the snapshot would in fact be *newer*
            // than everything admitted so far — it would be applied, not refused.
            loop.DeliverToClient(new[] { snapshot });

            int rejectedBefore = client.RejectedEnvelopeCount;
            int acceptedBefore = client.Receiver.AcceptedSnapshotCount;
            int acksBefore = client.AcksSent;

            // Fresh sequences for the forged-field cases, so each reaches the guard it is meant to
            // exercise instead of being stopped early by the ordered-channel check.
            uint fresh = unchecked(snapshot.Sequence + 1u);
            SimReplicationEnvelope Copy()
            {
                SimReplicationEnvelope copy = snapshot;
                copy.Sequence = fresh;
                fresh = unchecked(fresh + 1u);
                return copy;
            }

            var otherSession = Copy(); otherSession.SessionId = 0xDEAD000000000001UL;
            var otherPeer = Copy(); otherPeer.PeerId = 999u;
            var oldProtocol = Copy();
            oldProtocol.ProtocolVersion = SimReplicationProtocolRules.CurrentProtocolVersion + 1u;
            var noPayload = Copy(); noPayload.Payload = null;
            var stale = snapshot; stale.Sequence = snapshot.Sequence - 1u;
            var noKind = Copy(); noKind.Kind = SimReplicationMessageKind.None;

            client.Receive(otherSession);
            client.Receive(otherPeer);
            client.Receive(oldProtocol);
            client.Receive(noPayload);
            client.Receive(stale);
            client.Receive(noKind);

            Assert.AreEqual(rejectedBefore + 6, client.RejectedEnvelopeCount,
                "A late packet from another session, peer, protocol or sequence must be refused by value.");
            Assert.AreEqual(acceptedBefore, client.Receiver.AcceptedSnapshotCount);
            Assert.AreEqual(acksBefore, client.AcksSent);

            // A refused garbage kind must not advance the ordered channel's high-water mark. The
            // next real snapshot's sequence is lower than the forged ones; if the refusal had moved
            // the mark, this legitimate packet would be lost too.
            loop.Host.Tick();
            SimReplicationEnvelope[] next = OfKind(loop.TakeToClient(),
                SimReplicationMessageKind.Snapshot);
            Assert.AreEqual(1, next.Length);
            Assert.That((long)next[0].Sequence, Is.LessThan((long)fresh),
                "Control: the real next packet must really be older than the forged sequences.");
            loop.DeliverToClient(next);
            Assert.AreEqual(acceptedBefore + 1, client.Receiver.AcceptedSnapshotCount,
                "A refused unknown kind must not poison the ordered channel.");
        }

        [Test]
        public void Reconnect_ANewNonceGetsItsOwnPeerAndFreshBaseline()
        {
            var loop = new Loop(Source(Room(RoomA, Loot(LootA, 1)), Room(RoomB, Loot(LootB, 2))));
            SimReplicationClient first = loop.AddClient(NonceA);
            first.Join(InterestAB());
            loop.Pump();

            SimReplicationClient second = loop.AddClient(NonceB);
            second.Join(InterestAB());
            loop.Pump();

            Assert.AreEqual(2, loop.Host.JoinedPeerCount);
            Assert.AreEqual(1u, first.PeerId);
            Assert.AreEqual(2u, second.PeerId,
                "A new nonce is a new peer, not a resurrection of the first one's reliable stream.");
            Assert.IsTrue(second.IsJoined);
            Assert.AreEqual(Loop.SessionId, second.SessionId);
            Assert.AreEqual(1, second.AssembledBaselineCount,
                "A late joiner must receive its own chunked world baseline.");
            Assert.AreEqual(1, second.Receiver.LootCount(RoomA));
            Assert.AreEqual(loop.Host.PeerWorldRevision(2u), second.WorldRevision);
        }
    }
}
