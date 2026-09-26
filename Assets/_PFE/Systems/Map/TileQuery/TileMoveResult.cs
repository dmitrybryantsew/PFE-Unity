using UnityEngine;

namespace PFE.Systems.Map.TileQuery
{
    /// <summary>
    /// Result of a swept move (<see cref="ITileQueryService.ResolveMove"/>).
    /// Immutable value type returned per-step.
    /// </summary>
    public readonly struct TileMoveResult
    {
        /// <summary>Resolved final center position in pixel space.</summary>
        public readonly Vector2 Position;

        /// <summary>Unapplied delta remainder due to collision blockage.</summary>
        public readonly Vector2 Remainder;

        /// <summary>Hit ceiling during move.</summary>
        public readonly bool HitCeiling;

        /// <summary>Hit ground/floor during move.</summary>
        public readonly bool HitFloor;

        /// <summary>Hit left wall during move.</summary>
        public readonly bool HitLeft;

        /// <summary>Hit right wall during move.</summary>
        public readonly bool HitRight;

        /// <summary>Landed on a one-way platform.</summary>
        public readonly bool LandedOnPlatform;

        /// <summary>Number of sub-steps used (for maxdelta verification).</summary>
        public readonly int StepsUsed;

        public TileMoveResult(
            Vector2 position,
            Vector2 remainder,
            bool hitCeiling,
            bool hitFloor,
            bool hitLeft,
            bool hitRight,
            bool landedOnPlatform,
            int stepsUsed)
        {
            Position = position;
            Remainder = remainder;
            HitCeiling = hitCeiling;
            HitFloor = hitFloor;
            HitLeft = hitLeft;
            HitRight = hitRight;
            LandedOnPlatform = landedOnPlatform;
            StepsUsed = stepsUsed;
        }

        public static TileMoveResult Unblocked(Vector2 position, int stepsUsed = 1)
        {
            return new TileMoveResult(
                position,
                Vector2.zero,
                hitCeiling: false,
                hitFloor: false,
                hitLeft: false,
                hitRight: false,
                landedOnPlatform: false,
                stepsUsed: stepsUsed);
        }
    }
}
