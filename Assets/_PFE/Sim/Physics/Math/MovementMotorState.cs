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

        /// <summary>
        /// AS3 <c>stayPhis == 2</c> — standing on a <b>one-way</b> surface (the oracle's
        /// <c>shelf</c>), as opposed to solid ground (<c>stayPhis == 1</c>).
        ///
        /// <para><b>Why the locomotion brain needs it.</b> The player's Down key has two meanings
        /// depending on what is underfoot: over solid ground it crouches on the second held frame, over
        /// a shelf only on the sixth (<c>UnitPlayer.as:2859</c>). That window is what lets a double-tap
        /// of Down mean "drop through" without also crouching on the way down, so the distinction is
        /// part of the mechanic and not an implementation detail of the motor's ground query.</para>
        ///
        /// <para>Trailing and defaulted rather than inserted in the middle: this struct is constructed
        /// in exactly one place (<c>TilePhysicsController.State</c>), but a positional field order that
        /// silently re-binds is a far worse failure than an optional parameter at the end.</para>
        /// </summary>
        public readonly bool IsOnPlatform;

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
            int facingDirection,
            bool isOnPlatform = false)
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
            IsOnPlatform = isOnPlatform;
        }
    }
}
