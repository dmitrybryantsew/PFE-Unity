using System;

namespace PFE.Mods.RandomRooms.Core
{
    /// <summary>
    /// Deterministic PRNG using xorshift32 algorithm.
    /// Bit-for-bit identical to the AS3 / Flash RandomRooms implementation.
    /// Zero UnityEngine or System.Random dependencies.
    /// </summary>
    public class RRSeed
    {
        public uint seed { get; }
        private uint _x;

        public RRSeed(uint s)
        {
            seed = s;
            _x = (s == 0) ? 1 : s;
        }

        /// <summary>
        /// Derive a new deterministic child PRNG stream isolated by a string label.
        /// Does not advance the state of the parent stream.
        /// </summary>
        public RRSeed Fork(string label)
        {
            uint h = seed ^ 0x9E3779B9u;
            for (int i = 0; i < label.Length; i++)
            {
                h ^= (uint)label[i];
                h ^= h << 13;
                h ^= h >> 17;
                h ^= h << 5;
            }
            return new RRSeed(h);
        }

        /// <summary>
        /// Next pseudo-random double in range [0, 1).
        /// </summary>
        public double Next()
        {
            _x ^= _x << 13;
            _x ^= _x >> 17;
            _x ^= _x << 5;
            return (double)_x / 4294967296.0;
        }

        /// <summary>
        /// Next pseudo-random integer in range [0, n).
        /// </summary>
        public int NextInt(int n)
        {
            if (n <= 0) return 0;
            return (int)(Next() * n);
        }

        /// <summary>
        /// Previews the next n numbers for validation. Advances internal state.
        /// </summary>
        public double[] Preview(int n)
        {
            double[] result = new double[n];
            for (int i = 0; i < n; i++)
            {
                result[i] = Next();
            }
            return result;
        }
    }
}
