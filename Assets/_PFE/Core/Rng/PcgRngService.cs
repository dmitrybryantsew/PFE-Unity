using System;
using System.Collections.Generic;

namespace PFE.Core.Rng
{
    /// <summary>
    /// High-performance, bit-identical deterministic PRNG based on PCG32 (PCG-XSH-RR).
    /// Uses 64-bit internal state and integer-only bit manipulations to ensure
    /// cross-platform determinism without floating-point transcendental drift.
    /// </summary>
    public sealed class PcgRngService : IRngService
    {
        private ulong _state;
        private readonly ulong _inc;
        private readonly IRngService[] _childStreams;

        private const ulong Multiplier = 6364136223846793005UL;

        /// <summary>
        /// Initializes a new PCG32 PRNG instance.
        /// </summary>
        /// <param name="seed">Initial seed value.</param>
        /// <param name="streamSequence">Stream selector (determines sequence trajectory).</param>
        public PcgRngService(ulong seed = 0x853c49e6748fea9bUL, ulong streamSequence = 0xda3e39cb94b95bdbUL)
        {
            _inc = (streamSequence << 1) | 1UL;
            _state = 0UL;
            NextUInt();
            _state = unchecked(_state + seed);
            NextUInt();

            int streamCount = Enum.GetValues(typeof(RngStream)).Length;
            _childStreams = new IRngService[streamCount];
        }

        /// <inheritdoc/>
        public uint NextUInt()
        {
            ulong oldState = _state;
            _state = unchecked(oldState * Multiplier + _inc);
            uint xorshifted = (uint)(((oldState >> 18) ^ oldState) >> 27);
            int rot = (int)(oldState >> 59);
            return (xorshifted >> rot) | (xorshifted << ((-rot) & 31));
        }

        /// <inheritdoc/>
        public float NextFloat()
        {
            // 24 bits of mantissa precision: [0.0f, 1.0f)
            return (NextUInt() >> 8) * (1.0f / 16777216.0f);
        }

        /// <inheritdoc/>
        public int NextInt(int maxExclusive)
        {
            if (maxExclusive <= 0) return 0;

            // Unbiased bounded random generation via rejection
            uint bound = (uint)maxExclusive;
            uint threshold = unchecked((uint)-(int)bound) % bound;

            while (true)
            {
                uint r = NextUInt();
                if (r >= threshold)
                {
                    return (int)(r % bound);
                }
            }
        }

        /// <inheritdoc/>
        public int Range(int minInclusive, int maxExclusive)
        {
            if (minInclusive >= maxExclusive) return minInclusive;
            long diff = (long)maxExclusive - minInclusive;
            return minInclusive + NextInt((int)diff);
        }

        /// <inheritdoc/>
        public float Range(float min, float max)
        {
            if (min >= max) return min;
            return min + (max - min) * NextFloat();
        }

        /// <inheritdoc/>
        public bool Chance(float probability)
        {
            if (probability <= 0f) return false;
            if (probability >= 1f) return true;
            return NextFloat() < probability;
        }

        /// <inheritdoc/>
        public void Shuffle<T>(IList<T> list)
        {
            if (list == null || list.Count <= 1) return;

            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = Range(0, i + 1);
                T temp = list[i];
                list[i] = list[j];
                list[j] = temp;
            }
        }

        /// <inheritdoc/>
        public IRngService GetStream(RngStream stream, int? salt = null)
        {
            int streamIndex = (int)stream;
            if (salt.HasValue)
            {
                // Ephemeral or parameterized sub-stream
                ulong subSeed = Hash64(_state ^ (ulong)streamIndex, (ulong)salt.Value);
                return new PcgRngService(subSeed, (ulong)streamIndex + 1UL);
            }

            if (_childStreams[streamIndex] == null)
            {
                ulong subSeed = Hash64(_state, (ulong)streamIndex);
                _childStreams[streamIndex] = new PcgRngService(subSeed, (ulong)streamIndex + 1UL);
            }

            return _childStreams[streamIndex];
        }

        /// <summary>
        /// 64-bit integer hash mixer for seeding sub-streams.
        /// </summary>
        public static ulong Hash64(ulong a, ulong b)
        {
            ulong hash = a ^ 0x517cc1b727220a95UL;
            hash = unchecked(hash * 0xbf58476d1ce4e5b9UL + b);
            hash ^= hash >> 30;
            hash = unchecked(hash * 0x94d049bb133111ebUL);
            hash ^= hash >> 27;
            hash = unchecked(hash * 0xbf58476d1ce4e5b9UL);
            hash ^= hash >> 31;
            return hash;
        }
    }
}
