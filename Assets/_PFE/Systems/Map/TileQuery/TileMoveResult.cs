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

        /// <summary>
        /// How far the body was lifted by a step-up this move, in pixels; <c>0</c> when it was not.
        ///
        /// <para>AS3's step-up writes <c>Y = tile.phY1</c> (<c>Unit.as:2131</c> / <c>:2208</c>) —
        /// the body's Y changes, not just its X — so a caller that only applied
        /// <see cref="Position"/>'s horizontal component would leave the unit standing inside the
        /// step it just climbed. <see cref="Position"/> already carries the lift; this is here so a
        /// caller that applies the move as a <i>delta</i> (the motor does) can apply it too.</para>
        /// </summary>
        public readonly float StepUpPixels;

        public TileMoveResult(
            Vector2 position,
            Vector2 remainder,
            bool hitCeiling,
            bool hitFloor,
            bool hitLeft,
            bool hitRight,
            bool landedOnPlatform,
            int stepsUsed,
            float stepUpPixels = 0f)
        {
            Position = position;
            Remainder = remainder;
            HitCeiling = hitCeiling;
            HitFloor = hitFloor;
            HitLeft = hitLeft;
            HitRight = hitRight;
            LandedOnPlatform = landedOnPlatform;
            StepsUsed = stepsUsed;
            StepUpPixels = stepUpPixels;
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
