using System;

namespace PFE.Core.Rng
{
    /// <summary>
    /// Keyed derivation for any <see cref="IRngService"/>.
    ///
    /// <para><b>The rule this enforces.</b> A generated value must be a function of
    /// <c>(seed, domain, key)</c> and of nothing else — in particular not of how many values were
    /// drawn before it. <see cref="IRngService.GetStream"/> violates that by hashing the parent's
    /// live cursor, which is why two machines that populate rooms in a different order currently get
    /// different enemies in the same room.</para>
    ///
    /// <para><b>The fallback is a degradation, not a guarantee.</b> An implementation that does not
    /// offer <see cref="IKeyedRng"/> gets <c>GetStream(domain, salt)</c>, which is order-dependent.
    /// That is acceptable for test doubles and unacceptable for production, and the two are
    /// distinguishable by <c>is IKeyedRng</c>.</para>
    /// </summary>
    public static class RngDerivation
    {
        /// <summary>
        /// A child stream determined by <paramref name="parent"/>'s seed, the domain and the keys.
        /// </summary>
        public static IRngService Derive(this IRngService parent, RngStream stream, ulong key0,
            ulong key1 = 0UL)
        {
            if (parent == null) throw new ArgumentNullException(nameof(parent));

            if (parent is IKeyedRng keyed)
            {
                return keyed.Derive(stream, key0, key1);
            }

            // Order-dependent by nature. Kept so a hand-written double still runs; never relied on in
            // production, where the resolved IRngService is always a PcgRngService.
            return parent.GetStream(stream, salt: unchecked((int)key0));
        }

        /// <summary>
        /// Whether <paramref name="rng"/> can honour keyed derivation. Callers that must guarantee
        /// order-independence assert this rather than assuming it.
        /// </summary>
        public static bool SupportsDerivation(this IRngService rng)
        {
            return rng is IKeyedRng;
        }
    }
}
