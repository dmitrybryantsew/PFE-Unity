using FishNet.Broadcast;
using FishNet.Serializing;

namespace PFE.Net
{
    /// <summary>
    /// A second FishNet message, deliberately separate from <see cref="PfeReplicationBroadcast"/>.
    ///
    /// <para><b>Why not another envelope kind.</b> The Sim envelope stream is a single per-peer ordered
    /// sequence whose kinds are protocol-versioned in <c>PFE.Sim</c>. A world contract is transport
    /// metadata, not Sim state: it is sent once per peer, has no sequence, and must survive a protocol
    /// bump unchanged. Putting it inside that stream would force a v6 bump and make a late-joining peer
    /// wait behind baseline chunks to learn which world it is in.</para>
    ///
    /// <para><b>Same Channel.Reliable stream.</b> Both broadcasts use the default reliable channel, so a
    /// world contract sent immediately after a peer's join accept cannot be overtaken by the first
    /// snapshot that would already be applying tile mutations to an unverified map.</para>
    /// </summary>
    public struct PfeWorldContractBroadcast : IBroadcast
    {
        public byte[] Bytes;
    }

    /// <summary>
    /// Explicit serializer, for the same reason as <see cref="PfeReplicationBroadcastSerializers"/>:
    /// an ILPP-generated delegate may be absent in a player build, and without a registered writer
    /// FishNet logs "Write method not found" and silently drops the packet. The client would then
    /// report "host contract pending" forever with a perfectly healthy transport.
    /// </summary>
    public static class PfeWorldContractBroadcastSerializers
    {
        public static void Register()
        {
            GenericWriter<PfeWorldContractBroadcast>.SetWrite(WriteBroadcast);
            GenericReader<PfeWorldContractBroadcast>.SetRead(ReadBroadcast);
        }

        private static void WriteBroadcast(Writer writer, PfeWorldContractBroadcast value)
        {
            writer.WriteUInt8ArrayAndSize(value.Bytes);
        }

        private static PfeWorldContractBroadcast ReadBroadcast(Reader reader)
        {
            return new PfeWorldContractBroadcast
            {
                Bytes = reader.ReadUInt8ArrayAndSizeAllocated(),
            };
        }
    }
}
