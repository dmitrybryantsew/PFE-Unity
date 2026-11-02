using UnityEngine;

namespace PFE.Entities.Player
{
    /// <summary>
    /// Read-only movement data produced by the locomotion layer.
    /// Future animation systems should consume this instead of reading physics state directly.
    /// </summary>
    public readonly struct PlayerMovementSnapshot
    {
        public readonly PlayerLocomotionState LocomotionState;
        public readonly Vector2 Velocity;
        public readonly Vector2 Position;
        public readonly bool IsGrounded;
        public readonly bool IsInWater;
        public readonly bool IsFullySubmerged;
        public readonly bool IsOnLadder;
        public readonly bool IsDashing;
        public readonly bool IsCrouching;
        public readonly bool IsRunning;
        public readonly int FacingDirection;
        public readonly float AimAngle;
        public readonly bool JustDoubleJumped;
        public readonly bool JustLanded;
        /// <summary>0 = idle, 0-1 = walk, 1+ = run</summary>
        public readonly float NormalizedMoveSpeed;
        public readonly bool IsLevitating;
        public readonly bool JustTeleported;

        /// <summary>
        /// This crouch began <b>out of a sprint</b>, so its first 15 animation frames are the
        /// <c>roll</c> clip rather than the crawl (AS3 <c>UnitPlayer.as:4424</c>, handover at
        /// <c>:4417</c>). It stays true for the whole crouch — the roll is a transition, and the
        /// animation state's own frame count is what ends it, exactly as in the oracle.
        /// </summary>
        public readonly bool IsCrouchRoll;

        public PlayerMovementSnapshot(
            PlayerLocomotionState locomotionState,
            Vector2 velocity,
            Vector2 position,
            bool isGrounded,
            bool isInWater,
            bool isFullySubmerged,
            bool isOnLadder,
            bool isDashing,
            bool isCrouching,
            bool isRunning,
            int facingDirection,
            float aimAngle,
            bool justDoubleJumped,
            bool justLanded,
            float normalizedMoveSpeed,
            bool isLevitating,
            bool justTeleported,
            bool isCrouchRoll = false)
        {
            LocomotionState = locomotionState;
            Velocity = velocity;
            Position = position;
            IsGrounded = isGrounded;
            IsInWater = isInWater;
            IsFullySubmerged = isFullySubmerged;
            IsOnLadder = isOnLadder;
            IsDashing = isDashing;
            IsCrouching = isCrouching;
            IsRunning = isRunning;
            FacingDirection = facingDirection;
            AimAngle = aimAngle;
            JustDoubleJumped = justDoubleJumped;
            JustLanded = justLanded;
            NormalizedMoveSpeed = normalizedMoveSpeed;
            IsLevitating = isLevitating;
            JustTeleported = justTeleported;
            IsCrouchRoll = isCrouchRoll;
        }
    }
}
