using UnityEngine;
using PFE.Systems.Map;

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

        /// <summary>
        /// Move the motor into a new room and re-sync its authoritative pixel state from
        /// a world-space position, clearing velocity. Added for P0 Defect 2: assigning
        /// transform.position alone left posX/posY and currentRoom pointing at the old
        /// room, so the next tick collided against the old grid at the new coordinates.
        /// Implementations must refresh room context BEFORE resolving the position.
        /// </summary>
        void RepositionForRoom(RoomInstance room, Vector3 worldPos);

        /// <summary>
        /// The room the motor currently resolves tiles against. Exposed so tests and
        /// diagnostics can assert room context without reaching into the concrete class.
        /// </summary>
        RoomInstance CurrentRoom { get; }

        MovementMotorState State { get; }
    }
}
