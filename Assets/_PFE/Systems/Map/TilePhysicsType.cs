namespace PFE.Systems.Map
{
    /// <summary>
    /// Tile physics types matching AS3 phis property.
    /// From Tile.as: phis (0=air, 1=wall, 2=platform, 3=stair)
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
