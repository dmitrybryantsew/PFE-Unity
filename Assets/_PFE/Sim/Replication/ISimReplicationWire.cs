using System.Collections.Generic;
using PFE.Sim.State;

namespace PFE.Sim.Replication
{
    /// <summary>
    /// The <b>entire</b> transport seam. One method, no connection handle, no channel enum, no MTU,
    /// no latency: those belong to the stack, and the owner's choice is FishNet.
    ///
    /// <para><b>Why so narrow.</b> §12 D5 says the command/snapshot contract must not bake in a
    /// transport assumption. A FishNet adapter in the view assembly can implement this by mapping an
    /// envelope to a broadcast/RPC on a reliable channel for joins, baseline chunks and acks, and on
    /// an unreliable channel for snapshots. Nothing in <c>PFE.Sim</c> needs to know that. A local
    /// single-process session can instead use <see cref="SimReplicationOutbox"/> — which is what the
    /// offline wall does.</para>
    ///
    /// <para><b>By value, on purpose.</b> A transport is free to queue, split or retry; it must never
    /// be able to mutate the endpoint's in-flight envelope. The payload array is shared, but endpoints
    /// never mutate a payload after sending it.</para>
    /// </summary>
    public interface ISimReplicationWire
    {
        void Send(SimReplicationEnvelope envelope);
    }

    /// <summary>
    /// The host's production inputs, behind an interface so the replication loop can be executed
    /// offline with a stand-in. <see cref="SimProjectileSnapshotProjector"/> implements this.
    ///
    /// <para><b>Why the journal is here and not just the snapshot.</b> The ack the client sends has to
    /// reach <c>SimTileJournal.Acknowledge</c>; a host that only held a snapshot could acknowledge the
    /// bytes it sent but could not trim the producer's reliable stream, and the journal would grow for
    /// the whole session — the exact item §8 records as owed.</para>
    /// </summary>
    public interface ISimReplicationSnapshotSource
    {
        SimSnapshot Current { get; }
        SimTileJournal Journal { get; }
    }

    /// <summary>
    /// In-process loopback wire. It records envelopes instead of delivering them, so a fixture acts as
    /// the transport: it can deliver in order, drop, duplicate or reorder a packet by choosing which
    /// recorded envelope to hand to <c>Receive</c>. That is the whole point — out-of-order and
    /// duplicate behaviour is exercised without a socket or a package dependency.
    /// </summary>
    public sealed class SimReplicationOutbox : ISimReplicationWire
    {
        private readonly List<SimReplicationEnvelope> _sent = new List<SimReplicationEnvelope>(16);

        public int Count => _sent.Count;

        public SimReplicationEnvelope this[int index] => _sent[index];

        public void Send(SimReplicationEnvelope envelope)
        {
            _sent.Add(envelope);
        }

        public void Clear()
        {
            _sent.Clear();
        }

        public SimReplicationEnvelope[] ToArray()
        {
            return _sent.ToArray();
        }
    }
}
