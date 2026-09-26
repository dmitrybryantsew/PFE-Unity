using UnityEngine;

namespace PFE.Systems.Map.TileQuery
{
    /// <summary>
    /// Canonical constants for tile physics and collision, reconciled against the AS3 source.
    ///
    /// Every constant below references its AS3 source file and line.
    /// Used by <see cref="UnifiedTileQueryService"/> and throughout P2/P4.
    /// </summary>
    public static class TileQueryConstants
    {
        /// <summary>
        /// Global gravity acceleration per 30 Hz tick: 1 pixel/frame².
        /// AS3: World.as:46 (ddy = 1).
        /// Replaces the port's divergent 0.98f.
        /// </summary>
        public const float Gravity = 1.0f;

        /// <summary>
        /// Maximum distance a single collision sub-step may move before subdivision: 9 pixels.
        /// AS3: World.as:48 (maxdelta = 9).
        /// </summary>
        public const float MaxDelta = 9.0f;

        /// <summary>
        /// Maximum vertical velocity (terminal fall velocity): 20 pixels/frame.
        /// AS3: World.as:49 (maxdy = 20), overridable per location via Location.maxdy.
        /// </summary>
        public const float MaxDy = 20.0f;

        /// <summary>
        /// Default grounded step-up height in pixels: 10 pixels.
        /// AS3: Unit.as:278 (porog = 10).
        /// </summary>
        public const float PorogGrounded = 10.0f;

        /// <summary>
        /// Default airborne step-up height in pixels: 4 pixels.
        /// AS3: Unit.as:279 (porog_jump = 4).
        /// </summary>
        public const float PorogAirborne = 4.0f;

        /// <summary>
        /// Player grounded step-up height: 0 pixels.
        /// The player cannot step up walls passively; slope climb uses checkDiagon(-2, 1).
        /// AS3: UnitPlayer.as:2530 (porog = 0).
        /// </summary>
        public const float PlayerPorogGrounded = 0.0f;

        /// <summary>
        /// Player airborne step-up height while running or holding Up: 10 pixels.
        /// AS3: UnitPlayer.as:2533 (if((isRun || keyBeUp) && jumpNumb == 0) porog_jump = 10).
        /// </summary>
        public const float PlayerPorogJump = 10.0f;

        /// <summary>
        /// Ladder climb speed: 5 pixels/frame.
        /// AS3: Unit.as:72 (stairs = 5).
        /// </summary>
        public const float LadderClimbSpeed = 5.0f;

        /// <summary>
        /// Ladder attachment probe half-width in pixels.
        /// </summary>
        public const float LadderProbeHalfWidth = 6.0f;

        /// <summary>
        /// Tile dimensions: 40x40 pixels.
        /// AS3: World.as:36-37 (tileX = 40, tileY = 40).
        /// </summary>
        public const float TileSize = 40.0f;

        /// <summary>
        /// Room dimensions in tiles: 48 wide by 25 high.
        /// AS3: World.as:38-39 (cellsX = 48, cellsY = 25).
        /// </summary>
        public const int RoomCellsX = 48;
        public const int RoomCellsY = 25;
    }
}
