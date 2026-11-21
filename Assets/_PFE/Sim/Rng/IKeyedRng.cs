namespace PFE.Core.Rng
{
    /// <summary>
    /// The optional capability behind <see cref="RngDerivation.Derive"/>: a stream that is a pure
    /// function of a key, with no dependence on how many draws the parent has already made.
    ///
    /// <para><b>Why this is not a member of <see cref="IRngService"/>.</b> A dozen test doubles
    /// implement <c>IRngService</c> (<c>FixedRng</c>, <c>QueueRng</c>, <c>ScriptedRng</c>,
    /// <c>ConstantRng</c>, …) and none of them needs keyed derivation. Adding the member to the
    /// interface would break every one of them for no gain, so the capability is opt-in and the
    /// extension falls back to <see cref="IRngService.GetStream"/> for implementations that do not
    /// offer it.</para>
    ///
    /// <para><b>Only <see cref="PcgRngService"/> implements this in production.</b> That is
    /// deliberate: keyed derivation is a determinism guarantee, and a double that quietly changed its
    /// answer would make the determinism wall prove nothing.</para>
    /// </summary>
    public interface IKeyedRng
    {
        /// <summary>
        /// A child stream determined only by this stream's seed, the domain and the two keys.
        /// </summary>
        /// <param name="stream">The domain (spawn, loot, combat, …).</param>
        /// <param name="key0">The identity being generated — a land key, a room key, a spawn index.</param>
        /// <param name="key1">A sub-index within that identity.</param>
        IRngService Derive(RngStream stream, ulong key0, ulong key1 = 0UL);
    }
}
