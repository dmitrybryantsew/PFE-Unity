using System;

namespace PFE.Systems.Map.TileQuery
{
    /// <summary>
    /// Classification flags for a tile cell, determining how entities interact with it.
    /// Bitmask that maps from <see cref="TilePhysicsType"/> and special tile properties.
    /// </summary>
    [Flags]
    public enum TileQueryFlags
    {
        None         = 0,
        Solid        = 1 << 0,  // Fully blocking wall
        Platform     = 1 << 1,  // One-way shelf: blocks only downward approach
        Ladder       = 1 << 2,  // Climbable stair/ladder
        Water        = 1 << 3,  // Liquid/water
        Slope        = 1 << 4,  // Diagonal ground surface
        Destructible = 1 << 5,  // Can take damage / be destroyed
        IgnoreOneWay = 1 << 6,  // Drop-through or moving upward
    }
}
