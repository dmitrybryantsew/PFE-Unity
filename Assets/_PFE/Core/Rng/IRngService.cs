using System.Collections.Generic;

namespace PFE.Core.Rng
{
    /// <summary>
    /// Stream categories for PRNG separation (Decision D3).
    /// Prevents call-count changes in one system from perturbing other systems.
    /// </summary>
    public enum RngStream
    {
        Combat,      // Damage rolls, critical hits, weapon jam/misfire
        Ai,          // Enemy AI decisions, path choices
        Loot,        // Drops, chest contents, vendor inventory
        Spawn,       // Procedural placement, room layout variations
        Presentation // FX jitter (particles, audio pitch) — may remain unseeded
    }

    /// <summary>
    /// Deterministic PRNG service for simulation systems.
    /// Given the same seed, produces identical output on every platform, architecture, and build.
    /// </summary>
    public interface IRngService
    {
        /// <summary>Generates a 32-bit pseudo-random unsigned integer.</summary>
        uint NextUInt();

        /// <summary>Generates a pseudo-random float in [0.0f, 1.0f).</summary>
        float NextFloat();

        /// <summary>Generates a pseudo-random integer in [0, maxExclusive).</summary>
        int NextInt(int maxExclusive);

        /// <summary>Generates a pseudo-random integer in [minInclusive, maxExclusive).</summary>
        int Range(int minInclusive, int maxExclusive);

        /// <summary>Generates a pseudo-random float in [min, max).</summary>
        float Range(float min, float max);

        /// <summary>Returns true with probability in [0, 1].</summary>
        bool Chance(float probability);

        /// <summary>Performs an in-place Fisher-Yates shuffle on the given list.</summary>
        void Shuffle<T>(IList<T> list);

        /// <summary>
        /// Gets an isolated child stream for a specific domain.
        /// </summary>
        IRngService GetStream(RngStream stream, int? salt = null);
    }
}
