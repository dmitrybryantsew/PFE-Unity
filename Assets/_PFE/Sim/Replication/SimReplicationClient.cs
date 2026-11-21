using System;
using System.Collections.Generic;
using PFE.Core.Input;
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
        private uint _commandEnvelopeSequence;
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
        public int CommandBatchesSent { get; private set; }
        public int CommandsSent { get; private set; }
        public int RejectedCommandSendCount { get; private set; }
        public int LastCommandBatchSize { get; private set; }
        public int RoomTransitionsSent { get; private set; }
        public int RejectedRoomTransitionSendCount { get; private set; }

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

        /// <summary>
        /// Phase 5: publish the redundant last-N window of the local command tape to the host.
        ///
        /// <para><b>Why the whole window, every call.</b> D4's answer to a lost command datagram is a
        /// redundant window rather than a resend protocol: the next batch repeats the commands the
        /// host may have missed, so no ack, no hole list and no retransmission state is needed. The
        /// host's overlap-integrity check makes a repeat harmless. <paramref name="windowSize"/> is
        /// caller-supplied — the pure layer owns no invented <c>N</c>.</para>
        ///
        /// <para><b>Why the slot is re-stamped.</b> <c>PlayerController</c> builds commands with the
        /// local slot (0 in single-player). The host assigns controller slots from peer ids, so the
        /// wire must name the <b>host's</b> slot for this peer; otherwise the ingress refuses the
        /// batch as an attempt to drive another player's body. Slot is session metadata, not intent,
        /// so re-stamping does not change what the player asked to do. The local tape is left
        /// untouched — local prediction still uses the local slot.</para>
        ///
        /// <para><b>A channel of its own.</b> Command batches ride a separate unreliable channel with a
        /// dedicated <c>_commandEnvelopeSequence</c>, exactly as the FishNet wire sends them: the
        /// redundant window repairs a lost batch at the app layer, so a fast batch must never
        /// advance the reliable counter and mark a slower SnapshotAck or PeerRoomTransition
        /// stale.</para>
        /// </summary>
        public bool SendCommands(PlayerCommandBuffer buffer, int windowSize)
        {
            if (!_isJoined || buffer == null || windowSize <= 0)
            {
                RejectedCommandSendCount++;
                return false;
            }

            byte slot;
            if (!SimCommandAdmissionRules.TrySlotForPeer(_peerId, out slot))
            {
                RejectedCommandSendCount++;
                return false;
            }

            int total = buffer.Count;
            if (total == 0) return false;   // Nothing to publish is not an error and sends no packet.

            int take = total < windowSize ? total : windowSize;
            var all = new PlayerCommand[total];
            int copied = buffer.CopyOldestFirst(all);
            if (copied != total)
            {
                RejectedCommandSendCount++;
                return false;
            }

            // A tape longer than the window contributes its newest suffix; dropping the oldest keeps
            // the batch contiguous in ticks, which the ingress requires.
            var window = new PlayerCommand[take];
            int start = total - take;
            for (int i = 0; i < take; i++)
            {
                PlayerCommand command = all[start + i];
                command.SlotIndex = slot;
                window[i] = command;
            }

            SendClient(SimReplicationMessageKind.PlayerCommandBatch, _sessionId, _peerId,
                SimReplicationPayloadSerializer.Serialize(new SimPlayerCommandBatch
                {
                    SessionId = _sessionId,
                    PeerId = _peerId,
                    Commands = window,
                }));

            CommandBatchesSent++;
            CommandsSent += take;
            LastCommandBatchSize = take;
            return true;
        }

        /// <summary>
        /// Tell the host the local player crossed into another room. A room change is a
        /// discontinuous teleport the command stream cannot express: the host simulates this
        /// player's body, and without the message the body keeps walking against the room we
        /// already left, which reads on both screens as a position desync.
        ///
        /// <para><b>Why a declaration, not just movement news.</b> The host also uses the first
        /// transition after join to place the peer's body: before it arrives the only room the
        /// host has seen is its own, and a body built there is a body in the wrong room. The view
        /// layer therefore sends one transition right after joining even when the player stands
        /// still.</para>
        ///
        /// <para><b>Reliable channel only.</b> Transitions share the client envelope counter with acks
        /// and interest updates; command batches have their own unreliable counter (see
        /// <see cref="SendCommands"/>), so a fast batch can neither overtake nor age out this
        /// declaration.</para>
        /// </summary>
        /// <param name="roomId">The entered room's own id — the same string the projector hashes
        /// into <c>RoomHash</c>.</param>
        /// <param name="worldPixelX">Arrival position in world pixels, the one coordinate space
        /// both processes agree on verbatim.</param>
        public bool SendRoomTransition(string roomId, float worldPixelX, float worldPixelY)
        {
            if (!_isJoined || string.IsNullOrEmpty(roomId)
                || roomId.Length > SimPeerRoomTransition.MaxRoomIdLength
                || float.IsNaN(worldPixelX) || float.IsInfinity(worldPixelX)
                || float.IsNaN(worldPixelY) || float.IsInfinity(worldPixelY))
            {
                RejectedRoomTransitionSendCount++;
                return false;
            }

            SendClient(SimReplicationMessageKind.PeerRoomTransition, _sessionId, _peerId,
                SimReplicationPayloadSerializer.Serialize(new SimPeerRoomTransition
                {
                    SessionId = _sessionId,
                    PeerId = _peerId,
                    RoomId = roomId,
                    WorldPixelX = worldPixelX,
                    WorldPixelY = worldPixelY,
                }));

            RoomTransitionsSent++;
            return true;
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
            // PlayerCommandBatch is the only client envelope on the unreliable channel; it gets its
            // own counter so it can never advance the reliable high-water mark the host keeps for
            // acks, interest and transitions.
            uint seq;
            if (kind == SimReplicationMessageKind.PlayerCommandBatch)
            {
                _commandEnvelopeSequence =
                    SimReplicationProtocolRules.NextEnvelopeSequence(_commandEnvelopeSequence);
                seq = _commandEnvelopeSequence;
            }
            else
            {
                _envelopeSequence = SimReplicationProtocolRules.NextEnvelopeSequence(_envelopeSequence);
                seq = _envelopeSequence;
            }

            _wire.Send(new SimReplicationEnvelope
            {
                ProtocolVersion = SimReplicationProtocolRules.CurrentProtocolVersion,
                SessionId = sessionId,
                PeerId = peerId,
                Kind = kind,
                Sequence = seq,
                Payload = payload,
            });
        }
    }
}
