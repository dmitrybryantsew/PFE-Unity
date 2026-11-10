using System;
using System.Collections.Generic;
using PFE.Sim.State;

namespace PFE.Sim.Replication
{
    /// <summary>
    /// Phase 4 (F-11): client endpoint of the transport-neutral replication loop.
    ///
    /// <para><b>Admission is a pipeline, not a flag.</b> A packet must name this host's session, come
    /// from the admitted peer id, and (except for baseline chunks) be strictly newer in the host's
    /// per-peer envelope sequence. Baseline chunks are the deliberate exception: they are placed by
    /// <c>ChunkIndex</c> and are idempotent, so a re-delivered or reordered part does not have to be
    /// refused — but a re-delivered <i>snapshot</i> does, because applying one twice would re-run its
    /// loot full-replace and its reliable-journal duplicate check on stale state.</para>
    ///
    /// <para><b>Why the ack carries the receiver's readback.</b> <c>LastEventSequence</c> is
    /// <see cref="SimSnapshotReceiver.LastEventSequence"/>, which only moves when a snapshot was
    /// actually applied. An ack minted from the incoming sequence instead would let a corrupt or stale
    /// snapshot trim the host's journal — the exact "ack must not outrun the consumer" failure.</para>
    /// </summary>
    public sealed class SimReplicationClient
    {
        private readonly ISimReplicationWire _wire;
        private readonly ulong _clientNonce;
        private readonly List<SimBaselineChunk> _chunks = new List<SimBaselineChunk>(8);

        private SimSnapshotReceiver _receiver = new SimSnapshotReceiver();

        private uint _envelopeSequence;
        private uint _hostEnvelopeSequence;
        private ulong _sessionId;
        private uint _peerId;
        private long _worldRevision;
        private bool _isJoined;
        private bool _baselinePending;

        public SimReplicationClient(ulong clientNonce, ISimReplicationWire wire)
        {
            if (clientNonce == 0UL) throw new ArgumentException("A client nonce must be non-zero.", nameof(clientNonce));
            if (wire == null) throw new ArgumentNullException(nameof(wire));

            _clientNonce = clientNonce;
            _wire = wire;
        }

        public bool IsJoined => _isJoined;
        public ulong SessionId => _sessionId;
        public uint PeerId => _peerId;
        public long WorldRevision => _worldRevision;
        public SimSnapshotReceiver Receiver => _receiver;
        public int RejectedEnvelopeCount { get; private set; }
        public int RejectedSnapshotCount { get; private set; }
        public int AcksSent { get; private set; }
        public int BaselineChunksReceived { get; private set; }
        public int DuplicateChunkCount { get; private set; }
        public int AssembledBaselineCount { get; private set; }
        public SimBaselineAssemblyResult LastAssemblyResult { get; private set; }
        public SimJoinRejectReason LastJoinRejectReason { get; private set; }

        public void Join(SimPeerInterest interest)
        {
            Join(interest, SimReplicationProtocolRules.CurrentProtocolVersion);
        }

        /// <summary>
        /// Join declaring a protocol version. A real old build still forms a valid envelope; it is the
        /// <i>claim inside the join</i> that the host must check before admitting the peer. Exposed so
        /// the incompatible-peer guard can be proven able to fail.
        /// </summary>
        public void Join(SimPeerInterest interest, uint declaredProtocolVersion)
        {
            SendClient(SimReplicationMessageKind.JoinRequest, 0UL, 0u,
                SimReplicationPayloadSerializer.Serialize(
                    new SimJoinRequest(declaredProtocolVersion, _clientNonce, interest)));
        }

        public void UpdateInterest(SimPeerInterest interest)
        {
            SendClient(SimReplicationMessageKind.InterestUpdate, _sessionId, _peerId,
                SimReplicationPayloadSerializer.Serialize(new SimInterestUpdate(interest)));
        }

        public void Receive(SimReplicationEnvelope envelope)
        {
            if (envelope.Kind == SimReplicationMessageKind.JoinReject)
            {
                if (_isJoined) { RejectedEnvelopeCount++; return; }
                LastJoinRejectReason =
                    SimReplicationPayloadSerializer.ReadJoinReject(envelope.Payload).Reason;
                return;
            }

            if (envelope.Kind == SimReplicationMessageKind.JoinAccept)
            {
                // A second accept in the same attempt is a duplicate connection, not a re-join.
                if (_isJoined) { RejectedEnvelopeCount++; return; }

                SimJoinAccept accept = SimReplicationPayloadSerializer.ReadJoinAccept(envelope.Payload);
                if (!SimReplicationProtocolRules.IsCompatible(accept.ProtocolVersion)
                    || accept.SessionId == 0UL || accept.PeerId == 0u)
                {
                    RejectedEnvelopeCount++;
                    return;
                }

                _sessionId = accept.SessionId;
                _peerId = accept.PeerId;
                _worldRevision = accept.WorldRevision;
                _hostEnvelopeSequence = envelope.Sequence;
                _isJoined = true;
                _baselinePending = true;
                _chunks.Clear();
                return;
            }

            if (!_isJoined || envelope.SessionId != _sessionId || envelope.PeerId != _peerId
                || envelope.ProtocolVersion != SimReplicationProtocolRules.CurrentProtocolVersion
                || envelope.Payload == null)
            {
                RejectedEnvelopeCount++;
                return;
            }

            // Only two host message kinds exist after admission, and the kind is checked *before*
            // the ordered-channel guard. A garbage packet with a high sequence must not advance the
            // high-water mark on its way to being refused, or every legitimate packet after it would
            // look stale.
            if (envelope.Kind != SimReplicationMessageKind.BaselineChunk
                && envelope.Kind != SimReplicationMessageKind.Snapshot)
            {
                RejectedEnvelopeCount++;
                return;
            }

            // Indexed chunks are idempotent and order-independent; a snapshot is on the ordered
            // reliable channel and must be strictly newer than the last one admitted.
            if (envelope.Kind != SimReplicationMessageKind.BaselineChunk)
            {
                if (!SimReplicationProtocolRules.AcceptEnvelope(envelope.Sequence, _hostEnvelopeSequence))
                {
                    RejectedEnvelopeCount++;
                    return;
                }
                _hostEnvelopeSequence = envelope.Sequence;
            }

            if (envelope.Kind == SimReplicationMessageKind.BaselineChunk)
            {
                ReceiveBaselineChunk(envelope.Payload);
            }
            else
            {
                ReceiveSnapshot(envelope.Payload);
            }
        }

        private void ReceiveBaselineChunk(byte[] payload)
        {
            SimBaselineChunk chunk = SimReplicationPayloadSerializer.ReadBaselineChunk(payload);

            if (chunk.WorldRevision > _worldRevision)
            {
                // A lifetime change is announced by its baseline. The host bumps a peer's revision on
                // a world change or an interest gain, and the chunks are the first bytes of that
                // lifetime; parts of the previous baseline are then meaningless.
                _chunks.Clear();
                _worldRevision = chunk.WorldRevision;
                _baselinePending = true;
            }
            else if (chunk.WorldRevision != _worldRevision)
            {
                RejectedEnvelopeCount++;
                return;
            }

            for (int i = 0; i < _chunks.Count; i++)
            {
                if (_chunks[i].ChunkIndex != chunk.ChunkIndex) continue;

                if (!_chunks[i].Equals(chunk))
                {
                    // Same index, different bytes: a forged or re-ordered part must not silently
                    // overwrite the one already held.
                    RejectedEnvelopeCount++;
                    return;
                }

                DuplicateChunkCount++;
                return;
            }

            // A lifetime's baseline is already whole. Keeping an unknown part would leave an orphan
            // that re-triggers reassembly and overwrites the good result, so refuse it.
            if (!_baselinePending)
            {
                RejectedEnvelopeCount++;
                return;
            }

            _chunks.Add(chunk);
            BaselineChunksReceived++;

            if (_chunks.Count < chunk.ChunkCount) return;

            byte[] assembled;
            SimBaselineAssemblyResult result;
            if (!SimReplicationBaselineRules.TryAssemble(_chunks, out assembled, out result))
            {
                LastAssemblyResult = result;
                return;
            }

            LastAssemblyResult = result;
            SimSnapshot baseline = SimSnapshotSerializer.Deserialize(assembled);

            if (baseline.WorldRevision != _worldRevision)
            {
                RejectedSnapshotCount++;
                return;
            }

            SimSnapshotReceipt receipt = _receiver.Accept(baseline);
            if (!receipt.Applied)
            {
                RejectedSnapshotCount++;
                return;
            }

            _baselinePending = false;
            // Keep the assembled parts as this lifetime's idempotency record. A reliable channel may
            // re-deliver the final part after assembly; it must be recognised as a duplicate rather
            // than stored as the first part of a baseline that does not exist.
            AssembledBaselineCount++;
            SendAck(baseline.SnapshotSequence);
        }

        private void ReceiveSnapshot(byte[] payload)
        {
            // F-11's ordering rule: no unreliable snapshot may overtake the final baseline chunk.
            // Applying one first would let an older snapshot overwrite the world the baseline built.
            if (_baselinePending)
            {
                RejectedSnapshotCount++;
                return;
            }

            SimSnapshot snapshot = SimSnapshotSerializer.Deserialize(payload);
            if (snapshot.WorldRevision != _worldRevision)
            {
                RejectedSnapshotCount++;
                return;
            }

            SimSnapshotReceipt receipt = _receiver.Accept(snapshot);
            if (!receipt.Applied)
            {
                RejectedSnapshotCount++;
                return;
            }

            SendAck(snapshot.SnapshotSequence);
        }

        private void SendAck(uint snapshotSequence)
        {
            SendClient(SimReplicationMessageKind.SnapshotAck, _sessionId, _peerId,
                SimReplicationPayloadSerializer.Serialize(new SimSnapshotAck
                {
                    WorldRevision = _receiver.WorldRevision,
                    LastEventSequence = _receiver.LastEventSequence,
                    SnapshotSequence = snapshotSequence,
                }));
            AcksSent++;
        }

        private void SendClient(SimReplicationMessageKind kind, ulong sessionId, uint peerId, byte[] payload)
        {
            _envelopeSequence = SimReplicationProtocolRules.NextEnvelopeSequence(_envelopeSequence);
            _wire.Send(new SimReplicationEnvelope
            {
                ProtocolVersion = SimReplicationProtocolRules.CurrentProtocolVersion,
                SessionId = sessionId,
                PeerId = peerId,
                Kind = kind,
                Sequence = _envelopeSequence,
                Payload = payload,
            });
        }
    }
}
