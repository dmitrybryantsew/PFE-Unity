using UnityEngine;

namespace PFE.Systems.Physics
{
    /// <summary>
    /// Contract between a locomotion brain and a low-level movement motor.
    /// </summary>
    public interface IMovementMotor
    {
        void SetDesiredHorizontalSpeed(float speed);
        void SetDesiredVerticalSpeed(float speed);
        void SetLadderInput(float verticalInput, bool wantsToClimb);
        void SetDropThroughPlatforms(bool shouldDrop);
        void SetCrouching(bool isCrouching);
        void Jump(float force);
        void Dash(Vector2 direction, float speed, float duration);
        void SetGravityScale(float scale);
        void AddForce(Vector2 force);
        bool CanTeleportTo(float targetPixelX, float targetPixelY, float halfWidth, float halfHeight);
        void TeleportTo(float targetPixelX, float targetPixelY);

        MovementMotorState State { get; }
    }
}
