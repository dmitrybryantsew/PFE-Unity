namespace PFE.Systems.Particles
{
    /// <summary>
    /// The tile water value under a particle — the one piece of world state the step rules need, and the
    /// reason this interface exists.
    ///
    /// <para><b>Why an interface.</b> <c>Part.step</c> reads the water flag of the tile the particle is
    /// standing on to apply the <c>water=</c> lifetime rule (<c>Part.as:192-199</c>): a <c>water=2</c>
    /// particle dies on a dry tile and a <c>water=1</c> particle dies in water. Six of the 118 rows carry
    /// a <c>water=</c> value. Asking for it directly would drag a tile query — and therefore the map, and
    /// therefore Unity — into <see cref="ParticleWorld"/>, which would end its offline testability for the
    /// sake of a value that is unread for 112 of the 118 rows. An <c>int</c> in, <c>int</c> out keeps the
    /// world Unity-free and lets the adapter stay thin, the same split as
    /// <see cref="IParticleSpriteFrames"/>.</para>
    /// </summary>
    public interface IParticleTileWater
    {
        /// <summary>
        /// AS3 <c>loc.getTile(x, y).water</c> — <b>0</b> for dry, <b>&gt; 0</b> for water. Callers may
        /// assume it is only asked when the definition's <c>Water</c> is non-zero, so an implementation
        /// that is expensive for every particle is not required.
        /// </summary>
        /// <param name="x">AS3 room-local pixel X.</param>
        /// <param name="y">
        /// AS3 room-local pixel Y — <b>downward</b>, row 0 at the ceiling. It is the same space
        /// <c>ParticleState.X/Y</c> are in and therefore the same axis an implementation must mirror
        /// before it names a port tile row. See <see cref="ParticleState.X"/>.
        /// </param>
        int WaterAt(float x, float y);
    }

    /// <summary>
    /// A <see cref="IParticleTileWater"/> for hosts with no map — always dry.
    ///
    /// <para>Not a silent stub: it makes every <c>water=2</c> row (only in-water) die immediately and every
    /// <c>water=1</c> row (only out of water) live its full life. That is the correct answer for a dry
    /// world, and it is what the offline fixtures exercise, so the value is honest rather than
    /// arbitrary.</para>
    /// </summary>
    public sealed class DryParticleTileWater : IParticleTileWater
    {
        /// <summary>The shared instance. Stateless, so one is enough.</summary>
        public static DryParticleTileWater Instance { get; } = new DryParticleTileWater();

        private DryParticleTileWater() { }

        /// <inheritdoc />
        public int WaterAt(float x, float y) => 0;
    }
}
