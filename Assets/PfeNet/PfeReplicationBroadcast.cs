using FishNet.Broadcast;
using FishNet.Serializing;

namespace PFE.Net
{
    /// <summary>
    /// The one FishNet message this adapter uses. It carries an opaque
    /// <see cref="PFE.Sim.Replication.SimReplicationEnvelopeSerializer"/> byte frame; FishNet never
    /// learns what a snapshot, an ack or a command batch is.
    ///
    /// <para><b>One message type, two channels.</b> Wave 1A split the client → host stream: acks,
    /// interest updates and room transitions stay on <c>Channel.Reliable</c> with one per-peer
    /// envelope sequence, while <c>PlayerCommandBatch</c> rides <c>Channel.Unreliable</c> with its
    /// own counter — a lost 30 Hz input batch is repaired by the redundant N-command window, and
    /// must not head-of-line block the reliable stream behind a retransmission. Host → client is
    /// the mirror image: snapshots unreliable, join/baseline reliable. Every envelope is still
    /// the same struct; only the channel argument differs per kind.</para>
    /// </summary>
    public struct PfeReplicationBroadcast : IBroadcast
    {
        public byte[] Bytes;
    }

    /// <summary>
    /// Explicit serializer registration. FishNet's IL post-processor generates a serializer for every
    /// <see cref="IBroadcast"/> struct, but registering our own makes the wire independent of whether
    /// codegen ran on <c>Assembly-CSharp</c> — a generated delegate would otherwise be absent in a
    /// player build that skipped ILPP and <c>Writer.Write&lt;T&gt;</c> would log "Write method not
    /// found" and silently drop every packet.
    ///
    /// <para><b>Order is safe either way.</b> <c>GenericWriter&lt;T&gt;.SetWrite</c> refuses to
    /// overwrite an existing <i>custom</i> serializer, and a generated delegate does not set that
    /// flag, so a later <c>Register()</c> replaces it; if codegen initialises after us, its own
    /// <c>SetWrite</c> sees the custom flag and leaves ours in place.</para>
    /// </summary>
    public static class PfeReplicationBroadcastSerializers
    {
        public static void Register()
        {
            GenericWriter<PfeReplicationBroadcast>.SetWrite(WriteBroadcast);
            GenericReader<PfeReplicationBroadcast>.SetRead(ReadBroadcast);
        }

        private static void WriteBroadcast(Writer writer, PfeReplicationBroadcast value)
        {
            writer.WriteUInt8ArrayAndSize(value.Bytes);
        }

        private static PfeReplicationBroadcast ReadBroadcast(Reader reader)
        {
            return new PfeReplicationBroadcast
            {
                Bytes = reader.ReadUInt8ArrayAndSizeAllocated(),
            };
        }
    }
}
