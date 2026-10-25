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

        /// <summary>
        /// Add a vertical <b>impulse</b> in pixels per canonical 30 Hz frame, accumulated until the
        /// next step consumes it.
        ///
        /// <para><b>Why this is not <see cref="AddForce"/>.</b> A force is applied once, the moment
        /// it is called; a swim impulse is a <i>per-frame</i> quantity, so the motor has to scale it
        /// by the canonical frames its next step covers — exactly as it scales gravity — or the swim
        /// speed would depend on whether the build steps at 30, 50 or 120 Hz. The locomotion brain
        /// does not and cannot know that factor (it is <c>TilePhysicsStepMath.RateFramesPerStep</c>,
        /// private to the motor), so the conversion belongs on this side of the seam.</para>
        ///
        /// <para>AS3's swim is an impulse and not a target speed: <c>UnitPlayer.as:2804</c>
        /// <c>dy -= plavdy * pers.speedPlavMult</c> every frame. Assigning a speed instead destroys
        /// the accumulation, and with it the momentum a swimmer carries out of the water.</para>
        /// </summary>
        void AddVerticalImpulse(float impulsePixelsPerFrame);
        bool CanTeleportTo(float targetPixelX, float targetPixelY, float halfWidth, float halfHeight);
        void TeleportTo(float targetPixelX, float targetPixelY);
        void SetUnityPosition(Vector3 worldPos);

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
