using FishNet.Broadcast;
using FishNet.Serializing;

namespace PFE.Net
{
    /// <summary>
    /// The one FishNet message this adapter uses. It carries an opaque
    /// <see cref="PFE.Sim.Replication.SimReplicationEnvelopeSerializer"/> byte frame; FishNet never
    /// learns what a snapshot, an ack or a command batch is.
    ///
    /// <para><b>One message type, one channel.</b> Phase 5 put acks, interest updates and command
    /// batches on a single per-peer ordered envelope sequence. Splitting them across FishNet channels
    /// would let a fast command broadcast overtake a slower ack and mark it stale. Every envelope is
    /// therefore sent as the same struct on <c>Channel.Reliable</c>.</para>
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
