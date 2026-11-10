using System;
using System.Collections.Generic;

namespace PFE.Sim.Replication
{
    public enum SimBaselineAssemblyResult
    {
        None = 0,
        Ok = 1,
        Empty = 2,
        InconsistentCount = 3,
        BadIndex = 4,
        MissingChunk = 5,
        HashMismatch = 6,
    }

    /// <summary>
    /// F-11's chunked late-join world baseline, kept pure. The baseline is a serialized
    /// <c>SimSnapshot</c> — the same bytes a normal snapshot uses — split into caller-sized chunks.
    ///
    /// <para><b>The chunk size is an argument, never a constant.</b> §11's "N is a rule, not a number"
    /// applies here too: the right size belongs to a measured Phase-4 stress profile and to the chosen
    /// transport's MTU. Baking a plausible 1024 into Sim would be the invented-value defect this
    /// project keeps finding, and it would silently bake a FishNet assumption into the pure assembly.
    /// A test therefore passes an explicit size that forces several chunks.</para>
    ///
    /// <para><b>Why indexed chunks instead of a byte stream.</b> The oracle's
    /// <c>WorldPartMessage { total, offset, data }</c> carries an offset for a reason: a reliable
    /// channel may re-deliver a part. Placing by index makes a duplicate idempotent and lets parts
    /// land out of order, while the envelope sequence guard below still refuses reordered
    /// <i>snapshots</i>. Reassembly verifies the hash of the whole payload, so a missing or altered
    /// part cannot produce a snapshot that happens to parse.</para>
    /// </summary>
    public static class SimReplicationBaselineRules
    {
        public static int ChunkCount(int payloadLength, int chunkSize)
        {
            if (chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSize));
            if (payloadLength <= 0) return 1; // an empty payload is still one (empty) chunk
            return (payloadLength + chunkSize - 1) / chunkSize;
        }

        public static byte[][] Split(byte[] payload, int chunkSize)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            if (chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSize));

            int count = ChunkCount(payload.Length, chunkSize);
            var chunks = new byte[count][];

            for (int i = 0; i < count; i++)
            {
                int offset = i * chunkSize;
                int length = Math.Min(chunkSize, payload.Length - offset);
                if (length < 0) length = 0;

                var slice = new byte[length];
                if (length > 0) Array.Copy(payload, offset, slice, 0, length);
                chunks[i] = slice;
            }

            return chunks;
        }

        /// <summary>
        /// Reassemble chunks by their index. A duplicate index is idempotent when the bytes agree and
        /// a corruption when they do not — silence would let a forged part into the baseline.
        /// </summary>
        public static bool TryAssemble(IReadOnlyList<SimBaselineChunk> chunks,
            out byte[] payload, out SimBaselineAssemblyResult result)
        {
            payload = Array.Empty<byte>();

            if (chunks == null || chunks.Count == 0)
            {
                result = SimBaselineAssemblyResult.Empty;
                return false;
            }

            int expectedCount = chunks[0].ChunkCount;
            ulong expectedHash = chunks[0].PayloadHash;
            long revision = chunks[0].WorldRevision;

            if (expectedCount <= 0)
            {
                result = SimBaselineAssemblyResult.InconsistentCount;
                return false;
            }

            var slots = new byte[expectedCount][];

            for (int i = 0; i < chunks.Count; i++)
            {
                SimBaselineChunk chunk = chunks[i];

                if (chunk.ChunkCount != expectedCount
                    || chunk.PayloadHash != expectedHash
                    || chunk.WorldRevision != revision)
                {
                    result = SimBaselineAssemblyResult.InconsistentCount;
                    return false;
                }

                if (chunk.ChunkIndex < 0 || chunk.ChunkIndex >= expectedCount)
                {
                    result = SimBaselineAssemblyResult.BadIndex;
                    return false;
                }

                byte[] previous = slots[chunk.ChunkIndex];
                if (previous != null && !BytesEqual(previous, chunk.Bytes ?? Array.Empty<byte>()))
                {
                    result = SimBaselineAssemblyResult.HashMismatch;
                    return false;
                }

                slots[chunk.ChunkIndex] = chunk.Bytes ?? Array.Empty<byte>();
            }

            int total = 0;
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i] == null)
                {
                    result = SimBaselineAssemblyResult.MissingChunk;
                    return false;
                }
                total += slots[i].Length;
            }

            var assembled = new byte[total];
            int cursor = 0;
            for (int i = 0; i < slots.Length; i++)
            {
                Array.Copy(slots[i], 0, assembled, cursor, slots[i].Length);
                cursor += slots[i].Length;
            }

            if (SimReplicationProtocolRules.ComputePayloadHash(assembled) != expectedHash)
            {
                result = SimBaselineAssemblyResult.HashMismatch;
                return false;
            }

            payload = assembled;
            result = SimBaselineAssemblyResult.Ok;
            return true;
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}
