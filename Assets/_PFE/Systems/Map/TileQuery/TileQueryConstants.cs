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
        /// The project's pixels-per-Unity-unit scale: 100 px = 1 unit.
        /// </summary>
        public const float PixelToUnit = 0.01f;

        /// <summary>
        /// The inverse of <see cref="PixelToUnit"/>: 1 unit = 100 px.
        ///
        /// <para><b>Use this rather than a hand-written <c>100f</c>.</b> The project keeps two unit
        /// systems side by side — Unity units for anything the engine or a Rigidbody touches, and
        /// pixels for anything that speaks to <c>ITileQueryService</c> — so conversions happen at
        /// several boundaries and a bare literal at each one is how a factor goes missing. It did:
        /// <c>Projectile.SimTick</c> passed its <i>units</i> position straight to
        /// <c>TrySweepTiles</c>, which takes <i>pixels</i>, so every tile query ran at 1/100 scale
        /// near the world origin, found nothing, and let projectiles fly through every wall.</para>
        ///
        /// <para>This is deliberately a compile-time constant and not a method, so it folds away and
        /// costs nothing at a call site that runs per projectile per tick.</para>
        /// </summary>
        public const float UnitToPixel = 100f;

        /// <summary>
        /// Converts any AS3 <b>per-frame²</b> acceleration (px/frame²) into Unity units/s²:
        /// <c>px/frame² × 0.01 units/px × 30² frame²/s² = × 9</c>.
        ///
        /// <para><b>Do not use <see cref="Gravity"/> or a bare frame rate for this.</b> A
        /// per-frame acceleration needs the frame rate <i>squared</i> and the pixel scale; using
        /// <c>× fps</c> alone is wrong by <c>fps / (fps² × PixelToUnit)</c> = 100/30 ≈ 3.33×, and
        /// using <c>× fps / 100</c> (the correct idiom for a per-frame <i>velocity</i>) is wrong by
        /// 30×. Both mistakes exist in the port — see the change log in
        /// <c>REPLICA_BEHAVIOR_CONTRACT.md</c> §6.</para>
        /// </summary>
        public const float PerFrameAccelerationToUnitsPerSecondSquared =
            PixelToUnit * PFE.Core.SimClock.CanonicalTicksPerSecond * PFE.Core.SimClock.CanonicalTicksPerSecond;

        /// <summary>
        /// AS3 <c>World.ddy</c> expressed as an acceleration in Unity units/s²: 9.0.
        ///
        /// <para>This is the form a physics engine consumes. Anything applying gravity against a
        /// <i>seconds</i>-based delta must use this rather than <see cref="Gravity"/>: the raw
        /// per-frame value is 30× too small for that, and hand-converted literals are how the port
        /// ended up with three different gravities (0.98, 0.6, 1800 px/s²).</para>
        /// </summary>
        public const float GravityUnitsPerSecondSquared =
            Gravity * PerFrameAccelerationToUnitsPerSecondSquared;

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
