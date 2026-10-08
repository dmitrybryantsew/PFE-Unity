namespace PFE.Systems.Map
{
    /// <summary>
    /// The port's collision geometry for a tile cell — deliberately <b>not</b> a copy of AS3
    /// <c>phis</c>.
    ///
    /// <para>AS3 <c>phis</c> is 0 = air, 1 = solid, 2 = a level-2 solid (the grate doors,
    /// <c>AllData.as:4859-4860</c>) and 3 = a ghost wall set only at runtime (<c>Spell.as:446</c>).
    /// It has <b>no</b> "platform" and <b>no</b> "stair" value. The port splits the <c>phis == 0</c>
    /// case into geometry markers so the motor can give a catwalk one-way collision and a ladder a
    /// climb surface — that is what <see cref="Platform"/> and <see cref="Stair"/> are, and why this
    /// enum is not a 1:1 mirror of <c>phis</c>.</para>
    ///
    /// <para><b>The consequence, which has already bitten twice:</b> "is not <see cref="Air"/>"
    /// is <b>not</b> the same question as "AS3 <c>phis</c> is non-zero". A ladder and a catwalk are
    /// <see cref="Stair"/> / <see cref="Platform"/> here but <c>phis == 0</c> in the oracle, so the
    /// oracle leaves them transparent to light and to line of sight. Every predicate asking "does
    /// this block light / sight / damage" must test <see cref="Wall"/> — see
    /// <c>FogOcclusionMath</c>, <c>TileCollisionSystem.Raycast</c> and <c>TileData.IsDamageable</c>.
    /// <c>TileData.IsSolid()</c> is a different question ("blocks movement") and is wide on
    /// purpose.</para>
    /// </summary>
    public enum TilePhysicsType
    {
        /// <summary>
        /// Air - No collision, player can walk through
        /// </summary>
        Air = 0,

        /// <summary>
        /// Wall - Solid, blocks movement completely
        /// </summary>
        Wall = 1,

        /// <summary>
        /// Platform - One-way collision, can jump up through from below
        /// </summary>
        Platform = 2,

        /// <summary>
        /// Stair - Walkable slope or staircase
        /// </summary>
        Stair = 3
    }
}
