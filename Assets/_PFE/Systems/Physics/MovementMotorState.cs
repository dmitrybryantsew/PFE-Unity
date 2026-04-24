using UnityEngine;

namespace PFE.Systems.Physics
{
    /// <summary>
    /// Read-only runtime state exposed by a movement motor.
    /// This is the seam between high-level locomotion logic and low-level physics execution.
    /// </summary>
    public readonly struct MovementMotorState
    {
        public readonly bool IsGrounded;
        public readonly bool IsInWater;
        /// <summary>True when the 75%-height sample point is inside a water tile (fully submerged, AS3 isPlav).</summary>
        public readonly bool IsFullySubmerged;
        public readonly bool IsOnLadder;
        public readonly bool IsCrouching;
        public readonly bool HitCeiling;
        public readonly bool WallLeft;
        public readonly bool WallRight;
        public readonly Vector2 Velocity;
        public readonly Vector2 Position;
        public readonly int FacingDirection;

        public MovementMotorState(
            bool isGrounded,
            bool isInWater,
            bool isFullySubmerged,
            bool isOnLadder,
            bool isCrouching,
            bool hitCeiling,
            bool wallLeft,
            bool wallRight,
            Vector2 velocity,
            Vector2 position,
            int facingDirection)
        {
            IsGrounded = isGrounded;
            IsInWater = isInWater;
            IsFullySubmerged = isFullySubmerged;
            IsOnLadder = isOnLadder;
            IsCrouching = isCrouching;
            HitCeiling = hitCeiling;
            WallLeft = wallLeft;
            WallRight = wallRight;
            Velocity = velocity;
            Position = position;
            FacingDirection = facingDirection;
        }
    }
}
