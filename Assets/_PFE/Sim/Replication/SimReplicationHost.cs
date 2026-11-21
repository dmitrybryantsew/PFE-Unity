using System;
using System.Collections.Generic;
using System.IO;
using PFE.Sim.State;

namespace PFE.Sim.Replication
{
    /// <summary>
    /// Phase 4 (F-11): host endpoint of the transport-neutral replication loop.
    ///
    /// <para><b>The one problem this class exists to solve.</b> <see cref="SimSnapshotReceiver"/>
    /// requires the tile journal to be a <i>contiguous</i> reliable run, because a gap means lost
    /// geometry. §8's owed item asks for per-peer interest filtering — but simply deleting the events
    /// of rooms a peer is not in punches holes in the global sequence numbering, and the receiver
    /// would reject every filtered snapshot. So each peer gets its <b>own</b> reliable sequence space:
    /// the host ingests the global journal, keeps only the events in that peer's interest, and
    /// re-stamps them into a contiguous 1..N stream with a peer-local <c>WorldRevision</c>. The
    /// receiver's rules stay untouched and still mean exactly what they meant.</para>
    ///
    /// <para><b>Why a peer-local WorldRevision.</b> A peer's reliable lifetime ends whenever the global
    /// world revision changes <i>or</i> the peer gains a room it has no history for. Reusing the global
    /// revision would make those two events indistinguishable, and a peer whose interest changed would
    /// keep comparing new events against an old sequence space. The revision on the wire is therefore
    /// a lifetime marker for that peer, not a copy of the projector's field.</para>
    ///
    /// <para><b>Ack.</b> A client only acks a snapshot it actually applied. The host records, per sent
    /// snapshot, the global journal high-water mark at send time; an ack for that snapshot therefore
    /// proves the peer no longer needs any global event up to that mark. The host takes the
    /// <b>minimum</b> across joined peers and calls <see cref="SimTileJournal.Acknowledge"/> with it —
    /// a fast peer must not let the host discard an event a slow peer still needs. This is the call
    /// §8 records as missing.</para>
    /// </summary>
    public sealed class SimReplicationHost
    {
        private sealed class PeerState
        {
            public uint PeerId;
            public ulong ClientNonce;
            public SimPeerInterest Interest;

            /// <summary>Peer-local reliable lifetime. Bumped on a world change or an interest gain.</summary>
            public long WorldRevision;

            public uint EnvelopeSequence;
            public uint ClientEnvelopeSequence;

            /// <summary>High-water mark of the peer's unreliable command stream. Deliberately separate
            /// from <see cref="ClientEnvelopeSequence"/>: batches ride their own channel and counter,
            /// so a fast batch can never mark a reliable ack or transition stale.</summary>
            public uint ClientCommandEnvelopeSequence;

            public uint SnapshotSequence;

            public uint NextLocalEventSequence;
            public uint LastAckedLocalSequence;
            public uint LastGlobalSequenceConsumed;
            public uint GlobalAckedSequence;

            public bool NeedsBaseline;
            public int BaselinesSent;

            /// <summary>Unacknowledged peer-local reliable events, repeated every snapshot.</summary>
            public readonly List<SimTileMutation> View = new List<SimTileMutation>(16);

            /// <summary>Snapshot sequence -> global journal high-water mark at the moment it was sent.</summary>
            public readonly Dictionary<uint, uint> HighWaterBySnapshot = new Dictionary<uint, uint>();
        }

        private readonly ulong _sessionId;
        private readonly ISimReplicationSnapshotSource _source;
        private readonly ISimReplicationWire _wire;
        private readonly int _chunkSize;
        private readonly SimCommandIngress _commands;
        private readonly ISimRoomTransitionSink _roomTransitions;

        private readonly Dictionary<uint, PeerState> _peers = new Dictionary<uint, PeerState>();
        private readonly Dictionary<ulong, uint> _peerByNonce = new Dictionary<ulong, uint>();
        private readonly List<SimTileMutation> _baselineScratch = new List<SimTileMutation>(16);

        private uint _nextPeerId = 1u;
        private long _globalRevision;
        private bool _hasGlobalRevision;

        /// <summary>
        /// <paramref name="commandWindow"/> / <paramref name="commandSink"/> configure the Phase 5
        /// uplink. Both are optional so the Phase 4 loop (state only) keeps its exact constructor;
        /// a window without a sink still parses and orders batches but refuses them with
        /// <c>NoConsumer</c> rather than silently discarding intent. A session with
        /// <c>commandWindow == 0</c> has no uplink at all and refuses the kind before touching the
        /// peer's command high-water mark.
        /// </summary>
        /// <param name="roomTransitionSink">The consumer of admitted
        /// <see cref="SimReplicationMessageKind.PeerRoomTransition"/> envelopes, or null to refuse
        /// the kind. Optional so the Phase 4/5 constructor shapes keep working; a net session that
        /// admits peers but cannot move their bodies strands every peer in the first room, which
        /// reads on both screens as a position desync.</param>
        /// <param name="interactSink">The consumer of admitted interact edges carried in command
        /// batches, or null when the session does not drive containers. Optional for the same reason
        /// as the two sinks above: a host that admits intent but cannot act on the container half
        /// leaves every chest closed on both screens while the client believes it pressed the key.</param>
        public SimReplicationHost(ulong sessionId, ISimReplicationSnapshotSource source,
            ISimReplicationWire wire, int chunkSize, int commandWindow = 0,
            ISimCommandSink commandSink = null, ISimRoomTransitionSink roomTransitionSink = null,
            ISimInteractSink interactSink = null)
        {
            if (sessionId == 0UL) throw new ArgumentException("A host session id must be non-zero.", nameof(sessionId));
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (wire == null) throw new ArgumentNullException(nameof(wire));
            if (chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSize));
            if (commandWindow < 0) throw new ArgumentOutOfRangeException(nameof(commandWindow));

            _sessionId = sessionId;
            _source = source;
            _wire = wire;
            _chunkSize = chunkSize;
            _roomTransitions = roomTransitionSink;

            if (commandWindow > 0)
            {
                _commands = new SimCommandIngress(sessionId, commandWindow,
                    peerId => _peers.ContainsKey(peerId), commandSink, interactSink);
            }
        }

        public ulong SessionId => _sessionId;
        public int JoinedPeerCount => _peers.Count;
        public int SentSnapshotCount { get; private set; }
        public int SentBaselineChunkCount { get; private set; }
        public int AppliedAckCount { get; private set; }
        public int RejectedEnvelopeCount { get; private set; }
        public int AcceptedCommandBatchCount { get; private set; }
        public int RejectedCommandBatchCount { get; private set; }
        public int AcceptedRoomTransitionCount { get; private set; }
        public int RejectedRoomTransitionCount { get; private set; }

        /// <summary>The live ingress, or null when this host was built without a command window.
        /// Exposed for readbacks, not for callers to drive: only <see cref="Receive"/> may admit a
        /// batch, because the command channel's high-water mark lives here.</summary>
        public SimCommandIngress Commands => _commands;

        /// <summary>The producer's unacknowledged reliable events. The readback that proves an ack
        /// reached the journal, not just this endpoint.</summary>
        public int JournalCount => _source.Journal.JournalCount;

        public uint[] JoinedPeerIds()
        {
            var ids = new uint[_peers.Count];
            _peers.Keys.CopyTo(ids, 0);
            Array.Sort(ids);
            return ids;
        }

        public long PeerWorldRevision(uint peerId)
        {
            PeerState peer;
            return _peers.TryGetValue(peerId, out peer) ? peer.WorldRevision : 0L;
        }

        public int PeerViewCount(uint peerId)
        {
            PeerState peer;
            return _peers.TryGetValue(peerId, out peer) ? peer.View.Count : 0;
        }

        /// <summary>
        /// Build and send one snapshot (or a fresh baseline where a peer's lifetime changed) to every
        /// joined peer. Called once per simulation tick by the session layer.
        /// </summary>
        public void Tick()
        {
            SimSnapshot source = _source.Current;

            // The first observed revision is the starting point, not a change: a peer that joined
            // before the first tick already received a baseline and must not be given a second one.
            bool globalChanged = _hasGlobalRevision && source.WorldRevision != _globalRevision;
            _globalRevision = source.WorldRevision;
            _hasGlobalRevision = true;

            if (globalChanged)
            {
                // A new global lifetime restarts the global sequence at 1. An ack number from the old
                // lifetime would otherwise clamp Acknowledge() to the new LastSequence and trim the
                // baseline before a single peer had applied it.
                foreach (KeyValuePair<uint, PeerState> entry in _peers)
                {
                    entry.Value.GlobalAckedSequence = 0u;
                }
            }

            // Deterministic peer order: two hosts with the same inputs must emit identical bytes.
            uint[] ids = JoinedPeerIds();
            for (int i = 0; i < ids.Length; i++)
            {
                PeerState peer = _peers[ids[i]];

                if (globalChanged || peer.NeedsBaseline)
                {
                    ResetPeerLifetime(peer);
                    SendBaseline(peer, source);
                    continue;
                }

                IngestGlobalEvents(peer);
                SendSnapshot(peer, source);
            }
        }

        /// <summary>Handle one client → host envelope.</summary>
        public void Receive(SimReplicationEnvelope envelope)
        {
            // A join request is the one message a peer can send before it knows the host's session id;
            // it must carry 0, or a peer could name a session it was never admitted to.
            bool admitted = envelope.Kind == SimReplicationMessageKind.JoinRequest
                ? envelope.SessionId == 0UL
                  && envelope.ProtocolVersion == SimReplicationProtocolRules.CurrentProtocolVersion
                : envelope.SessionId == _sessionId
                  && envelope.ProtocolVersion == SimReplicationProtocolRules.CurrentProtocolVersion;

            if (!admitted || envelope.Kind == SimReplicationMessageKind.None || envelope.Payload == null)
            {
                RejectedEnvelopeCount++;
                return;
            }

            switch (envelope.Kind)
            {
                case SimReplicationMessageKind.JoinRequest:
                    HandleJoin(envelope);
                    break;

                case SimReplicationMessageKind.SnapshotAck:
                    HandleAck(envelope);
                    break;

                case SimReplicationMessageKind.InterestUpdate:
                    HandleInterest(envelope);
                    break;

                case SimReplicationMessageKind.PlayerCommandBatch:
                    HandleCommandBatch(envelope);
                    break;

                case SimReplicationMessageKind.PeerRoomTransition:
                    HandlePeerRoomTransition(envelope);
                    break;

                default:
                    RejectedEnvelopeCount++;
                    break;
            }
        }

        private void HandleJoin(SimReplicationEnvelope envelope)
        {
            SimJoinRequest request = SimReplicationPayloadSerializer.ReadJoinRequest(envelope.Payload);

            if (!SimReplicationProtocolRules.IsCompatible(request.ProtocolVersion))
            {
                SendJoinReject(0u, 1u, SimJoinRejectReason.IncompatibleProtocol);
                return;
            }

            if (_peerByNonce.ContainsKey(request.ClientNonce))
            {
                // A retried join with the same nonce is a duplicate connection attempt, not a new
                // peer. Admitting it would give one client two reliable streams over the same state.
                SendJoinReject(0u, 1u, SimJoinRejectReason.AlreadyJoined);
                return;
            }

            byte ignoredSlot;
            if (!SimCommandAdmissionRules.TrySlotForPeer(_nextPeerId, out ignoredSlot))
            {
                // A controller slot is not optional: admitting a peer that can never send intent
                // would look exactly like an idle player to every diagnostic.
                SendJoinReject(0u, 1u, SimJoinRejectReason.NoControllerSlot);
                return;
            }

            uint peerId = _nextPeerId;
            _nextPeerId = SimReplicationProtocolRules.NextEnvelopeSequence(_nextPeerId);

            var peer = new PeerState
            {
                PeerId = peerId,
                ClientNonce = request.ClientNonce,
                Interest = request.ToInterest(),
                ClientEnvelopeSequence = envelope.Sequence,
                NeedsBaseline = true,
            };

            _peers[peerId] = peer;
            _peerByNonce[request.ClientNonce] = peerId;

            // Lifetime first: the accept must name the revision the baseline chunks actually carry,
            // or a client would admit chunks from a lifetime it was never told about.
            ResetPeerLifetime(peer);

            Send(peer, SimReplicationMessageKind.JoinAccept,
                SimReplicationPayloadSerializer.Serialize(new SimJoinAccept
                {
                    SessionId = _sessionId,
                    PeerId = peerId,
                    ProtocolVersion = SimReplicationProtocolRules.CurrentProtocolVersion,
                    WorldRevision = peer.WorldRevision,
                }));

            SendBaseline(peer, _source.Current);
        }

        private void HandleAck(SimReplicationEnvelope envelope)
        {
            PeerState peer;
            if (!_peers.TryGetValue(envelope.PeerId, out peer)) { RejectedEnvelopeCount++; return; }

            if (!SimReplicationProtocolRules.AcceptEnvelope(envelope.Sequence, peer.ClientEnvelopeSequence))
            {
                RejectedEnvelopeCount++;
                return;
            }
            peer.ClientEnvelopeSequence = envelope.Sequence;

            SimSnapshotAck ack = SimReplicationPayloadSerializer.ReadSnapshotAck(envelope.Payload);
            if (ack.WorldRevision != peer.WorldRevision)
            {
                // An ack from a previous peer lifetime must not trim the current one's stream.
                RejectedEnvelopeCount++;
                return;
            }

            uint highWater;
            if (!peer.HighWaterBySnapshot.TryGetValue(ack.SnapshotSequence, out highWater))
            {
                // An ack for a snapshot this host never sent is not evidence of anything.
                RejectedEnvelopeCount++;
                return;
            }

            if (SimStalenessRules.IsNewer(highWater, peer.GlobalAckedSequence))
            {
                peer.GlobalAckedSequence = highWater;
            }

            if (SimStalenessRules.IsNewer(ack.LastEventSequence, peer.LastAckedLocalSequence))
            {
                peer.LastAckedLocalSequence = ack.LastEventSequence;

                int remove = 0;
                while (remove < peer.View.Count
                       && !SimStalenessRules.IsNewer(peer.View[remove].EventSequence, ack.LastEventSequence))
                {
                    remove++;
                }
                if (remove > 0) peer.View.RemoveRange(0, remove);
            }

            // Drop high-water entries at or below the acked snapshot; the dictionary must not grow
            // for the whole session.
            var stale = new List<uint>();
            foreach (KeyValuePair<uint, uint> entry in peer.HighWaterBySnapshot)
            {
                if (!SimStalenessRules.IsNewer(entry.Key, ack.SnapshotSequence)) stale.Add(entry.Key);
            }
            for (int i = 0; i < stale.Count; i++) peer.HighWaterBySnapshot.Remove(stale[i]);

            AppliedAckCount++;
            TrimGlobalJournal();
        }

        private void HandleInterest(SimReplicationEnvelope envelope)
        {
            PeerState peer;
            if (!_peers.TryGetValue(envelope.PeerId, out peer)) { RejectedEnvelopeCount++; return; }

            if (!SimReplicationProtocolRules.AcceptEnvelope(envelope.Sequence, peer.ClientEnvelopeSequence))
            {
                RejectedEnvelopeCount++;
                return;
            }
            peer.ClientEnvelopeSequence = envelope.Sequence;

            SimInterestUpdate update = SimReplicationPayloadSerializer.ReadInterestUpdate(envelope.Payload);
            SimPeerInterest next = update.ToInterest();

            if (SimReplicationInterestRules.RequiresNewLifetime(peer.Interest, next))
            {
                peer.NeedsBaseline = true;
            }
            peer.Interest = next;
        }

        /// <summary>
        /// Phase 5: admit a client → host command window. Batches arrive on the peer's unreliable
        /// channel and are gated by the dedicated <see cref="PeerState.ClientCommandEnvelopeSequence"/>
        /// high-water mark, NOT the reliable mark acks and transitions use — a fast batch commits
        /// only its own watermark, so it cannot age out a slower SnapshotAck or room transition
        /// (the head-of-line stutter Wave 1A splits this channel to fix). Contiguity and overlap
        /// integrity inside the window are the ingress's job, on TickIndex, not on this counter.
        /// A session built without an ingress refuses before that mark moves — otherwise packets
        /// nobody can read would still burn the command channel.
        /// </summary>
        private void HandleCommandBatch(SimReplicationEnvelope envelope)
        {
            PeerState peer;
            if (!_peers.TryGetValue(envelope.PeerId, out peer)) { RejectedEnvelopeCount++; return; }

            if (!SimReplicationProtocolRules.AcceptEnvelope(envelope.Sequence,
                    peer.ClientCommandEnvelopeSequence))
            {
                RejectedEnvelopeCount++;
                return;
            }

            if (_commands == null) { RejectedEnvelopeCount++; return; }
            peer.ClientCommandEnvelopeSequence = envelope.Sequence;

            SimCommandReceipt receipt = _commands.Receive(envelope);
            if (receipt.Applied) AcceptedCommandBatchCount++;
            else RejectedCommandBatchCount++;
        }

        /// <summary>
        /// A peer's room change. Shares the per-peer reliable high-water mark with acks and interest
        /// updates; command batches keep their own mark, so an overtaking batch can neither
        /// re-admit nor refuse this declaration.
        /// </summary>
        private void HandlePeerRoomTransition(SimReplicationEnvelope envelope)
        {
            PeerState peer;
            if (!_peers.TryGetValue(envelope.PeerId, out peer)) { RejectedEnvelopeCount++; return; }

            if (!SimReplicationProtocolRules.AcceptEnvelope(envelope.Sequence, peer.ClientEnvelopeSequence))
            {
                RejectedEnvelopeCount++;
                return;
            }

            if (_roomTransitions == null) { RejectedEnvelopeCount++; return; }
            peer.ClientEnvelopeSequence = envelope.Sequence;

            SimPeerRoomTransition transition;
            try
            {
                transition = SimReplicationPayloadSerializer.ReadPeerRoomTransition(envelope.Payload);
            }
            catch (Exception e) when (e is InvalidDataException || e is EndOfStreamException
                                      || e is IOException || e is ArgumentException
                                      || e is FormatException)
            {
                RejectedRoomTransitionCount++;
                return;
            }

            if (transition.SessionId != envelope.SessionId)
            {
                RejectedRoomTransitionCount++;
                return;
            }
            if (transition.PeerId != envelope.PeerId)
            {
                RejectedRoomTransitionCount++;
                return;
            }

            byte slot;
            if (!SimCommandAdmissionRules.TrySlotForPeer(envelope.PeerId, out slot))
            {
                RejectedRoomTransitionCount++;
                return;
            }

            _roomTransitions.Apply(envelope.PeerId, slot, transition.RoomId,
                transition.WorldPixelX, transition.WorldPixelY);
            AcceptedRoomTransitionCount++;
        }

        /// <summary>
        /// The global journal can only discard an event when <b>every</b> joined peer has moved past
        /// it. One slow peer is enough to keep the prefix alive.
        /// </summary>
        private void TrimGlobalJournal()
        {
            if (_peers.Count == 0) return;

            uint minimum = 0u;
            bool first = true;

            foreach (KeyValuePair<uint, PeerState> entry in _peers)
            {
                uint acked = entry.Value.GlobalAckedSequence;
                if (first)
                {
                    minimum = acked;
                    first = false;
                }
                else if (SimStalenessRules.IsNewer(minimum, acked))
                {
                    minimum = acked;
                }
            }

            _source.Journal.Acknowledge(minimum);
        }

        private void ResetPeerLifetime(PeerState peer)
        {
            peer.WorldRevision = unchecked(peer.WorldRevision + 1);
            peer.View.Clear();
            peer.HighWaterBySnapshot.Clear();
            peer.NextLocalEventSequence = SimSnapshotReceiver.FirstReliableEventSequence;
            peer.LastAckedLocalSequence = 0u;
            peer.NeedsBaseline = false;
            peer.BaselinesSent++;

            // A fresh lifetime is a full divergence baseline for the rooms this peer cares about, not
            // a suffix of the global journal (which may already have been trimmed by other peers).
            _baselineScratch.Clear();
            _source.Journal.CopyBaselineTo(peer.WorldRevision, _baselineScratch);
            SimTileMutation[] interesting = SimReplicationInterestRules.FilterJournal(
                _baselineScratch.ToArray(), peer.Interest);

            for (int i = 0; i < interesting.Length; i++)
            {
                SimTileMutation mutation = interesting[i];
                mutation.EventSequence = peer.NextLocalEventSequence;
                peer.NextLocalEventSequence =
                    SimSnapshotProjectionRules.NextReliableEventSequence(peer.NextLocalEventSequence);
                peer.View.Add(mutation);
            }

            peer.LastGlobalSequenceConsumed = _source.Journal.LastSequence;
        }

        private void IngestGlobalEvents(PeerState peer)
        {
            SimTileMutation[] global = _source.Journal.ToArray();

            var fresh = new List<SimTileMutation>(global.Length);
            for (int i = 0; i < global.Length; i++)
            {
                if (SimStalenessRules.IsNewer(global[i].EventSequence, peer.LastGlobalSequenceConsumed))
                {
                    fresh.Add(global[i]);
                }
            }

            SimTileMutation[] interesting =
                SimReplicationInterestRules.FilterJournal(fresh.ToArray(), peer.Interest);

            for (int i = 0; i < interesting.Length; i++)
            {
                SimTileMutation mutation = interesting[i];
                mutation.WorldRevision = peer.WorldRevision;
                mutation.EventSequence = peer.NextLocalEventSequence;
                peer.NextLocalEventSequence =
                    SimSnapshotProjectionRules.NextReliableEventSequence(peer.NextLocalEventSequence);
                peer.View.Add(mutation);
            }

            peer.LastGlobalSequenceConsumed = _source.Journal.LastSequence;
        }

        private void SendBaseline(PeerState peer, SimSnapshot source)
        {
            peer.SnapshotSequence = SimSnapshotProjectionRules.NextSnapshotSequence(peer.SnapshotSequence);

            var baseline = new SimSnapshot
            {
                ProtocolVersion = SimReplicationProtocolRules.CurrentProtocolVersion,
                TickIndex = source.TickIndex,
                SnapshotSequence = peer.SnapshotSequence,
                WorldRevision = peer.WorldRevision,
                BaselineHash = 0UL,
                Rooms = SimReplicationInterestRules.FilterRooms(source.Rooms, peer.Interest),
                TileMutations = peer.View.ToArray(),
            };

            RecordHighWater(peer, baseline.SnapshotSequence);

            byte[] payload = SimSnapshotSerializer.Serialize(baseline);
            byte[][] chunks = SimReplicationBaselineRules.Split(payload, _chunkSize);
            ulong hash = SimReplicationProtocolRules.ComputePayloadHash(payload);

            for (int i = 0; i < chunks.Length; i++)
            {
                Send(peer, SimReplicationMessageKind.BaselineChunk,
                    SimReplicationPayloadSerializer.Serialize(new SimBaselineChunk
                    {
                        WorldRevision = peer.WorldRevision,
                        ChunkIndex = i,
                        ChunkCount = chunks.Length,
                        PayloadHash = hash,
                        Bytes = chunks[i],
                    }));
            }

            SentBaselineChunkCount += chunks.Length;
        }

        private void SendSnapshot(PeerState peer, SimSnapshot source)
        {
            peer.SnapshotSequence = SimSnapshotProjectionRules.NextSnapshotSequence(peer.SnapshotSequence);

            var snapshot = new SimSnapshot
            {
                ProtocolVersion = SimReplicationProtocolRules.CurrentProtocolVersion,
                TickIndex = source.TickIndex,
                SnapshotSequence = peer.SnapshotSequence,
                WorldRevision = peer.WorldRevision,
                BaselineHash = 0UL,
                Rooms = SimReplicationInterestRules.FilterRooms(source.Rooms, peer.Interest),
                TileMutations = peer.View.ToArray(),
            };

            RecordHighWater(peer, snapshot.SnapshotSequence);
            Send(peer, SimReplicationMessageKind.Snapshot, SimSnapshotSerializer.Serialize(snapshot));
            SentSnapshotCount++;
        }

        private void RecordHighWater(PeerState peer, uint snapshotSequence)
        {
            peer.HighWaterBySnapshot[snapshotSequence] = _source.Journal.LastSequence;
        }

        private void Send(PeerState peer, SimReplicationMessageKind kind, byte[] payload)
        {
            peer.EnvelopeSequence = SimReplicationProtocolRules.NextEnvelopeSequence(peer.EnvelopeSequence);
            _wire.Send(new SimReplicationEnvelope
            {
                ProtocolVersion = SimReplicationProtocolRules.CurrentProtocolVersion,
                SessionId = _sessionId,
                PeerId = peer.PeerId,
                Kind = kind,
                Sequence = peer.EnvelopeSequence,
                Payload = payload,
            });
        }

        private void SendJoinReject(uint peerId, uint sequence, SimJoinRejectReason reason)
        {
            _wire.Send(new SimReplicationEnvelope
            {
                ProtocolVersion = SimReplicationProtocolRules.CurrentProtocolVersion,
                SessionId = _sessionId,
                PeerId = peerId,
                Kind = SimReplicationMessageKind.JoinReject,
                Sequence = sequence,
                Payload = SimReplicationPayloadSerializer.Serialize(new SimJoinReject
                {
                    Reason = reason,
                    ExpectedProtocolVersion = SimReplicationProtocolRules.CurrentProtocolVersion,
                }),
            });
        }
    }
}
