using UnityEngine;

namespace PFE.Systems.Physics
{
    /// <summary>
    /// Core physics body component.
    /// Implements velocity, acceleration, friction, and rotation physics
    /// based on ActionScript Part.as (dx, dy, ddy, brake, dr, r).
    /// </summary>
    [DisallowMultipleComponent]
    public class PhysicsBody : MonoBehaviour
    {
        [Header("Physics Configuration")]
        [SerializeField] protected PhysicsConfig config;

        /// <summary>
        /// Physics configuration asset
        /// </summary>
        public PhysicsConfig Config => config;

        [Header("Velocity (dx, dy)")]
        [Tooltip("Current horizontal velocity")]
        [SerializeField] protected float velocityX = 0f;

        [Tooltip("Current vertical velocity")]
        [SerializeField] protected float velocityY = 0f;

        [Header("Acceleration (ddy)")]
        [Tooltip("Vertical acceleration (gravity, etc.)")]
        [SerializeField] protected float accelY = 0f;

        [Header("Rotation")]
        [Tooltip("Current rotation in degrees (r)")]
        [SerializeField] protected float rotation = 0f;

        [Tooltip("Rotational velocity (dr - degrees per frame)")]
        [SerializeField] protected float rotationVelocity = 0f;

        [Header("Friction")]
        [Tooltip("Brake coefficient (0-1, where 1 = no friction)")]
        [Range(0f, 1f)]
        [SerializeField] protected float brake = 1f;

        [Header("State")]
        [Tooltip("Is this body actively processing physics?")]
        [SerializeField] protected bool isMoving = false;

        /// <summary>
        /// Current horizontal velocity (dx in ActionScript)
        /// </summary>
        public float VelocityX
        {
            get => velocityX;
            set => velocityX = value;
        }

        /// <summary>
        /// Current vertical velocity (dy in ActionScript)
        /// </summary>
        public float VelocityY
        {
            get => velocityY;
            set => velocityY = value;
        }

        /// <summary>
        /// Vertical acceleration (ddy in ActionScript)
        /// </summary>
        public float AccelY
        {
            get => accelY;
            set => accelY = value;
        }

        /// <summary>
        /// Current rotation in degrees (r in ActionScript)
        /// </summary>
        public float Rotation
        {
            get => rotation;
            set => rotation = value;
        }

        /// <summary>
        /// Rotational velocity (dr in ActionScript)
        /// </summary>
        public float RotationVelocity
        {
            get => rotationVelocity;
            set => rotationVelocity = value;
        }

        /// <summary>
        /// Brake coefficient (brake in ActionScript)
        /// </summary>
        public float Brake
        {
            get => brake;
            set => brake = Mathf.Clamp01(value);
        }

        /// <summary>
        /// Is this body actively processing physics?
        /// </summary>
        public bool IsMoving
        {
            get => isMoving;
            set => isMoving = value;
        }

        /// <summary>
        /// Current velocity vector
        /// </summary>
        public Vector2 Velocity => new Vector2(velocityX, velocityY);

        protected virtual void Awake()
        {
            // Use default config if none assigned
            if (config == null)
            {
                config = PhysicsConfig.CreateInstance<PhysicsConfig>();
            }
        }

        /// <summary>
        /// Apply physics step (called from FixedUpdate or manual step)
        /// Based on Part.as step() function:
        /// - X += dx
        /// - Y += dy
        /// - dy += ddy
        /// - r += dr
        /// - dx *= brake
        /// - dy *= brake
        /// </summary>
        public virtual void PhysicsStep(float deltaTime)
        {
            if (!isMoving) return;

            // Apply acceleration to velocity (dy += ddy)
            velocityY += accelY * deltaTime;

            // Apply velocity to position (X += dx, Y += dy)
            transform.position += new Vector3(velocityX, velocityY, 0f) * deltaTime;

            // Apply rotation (r += dr)
            rotation += rotationVelocity * deltaTime;
            transform.rotation = Quaternion.Euler(0f, 0f, rotation);

            // Apply friction (dx *= brake, dy *= brake)
            velocityX *= brake;
            velocityY *= brake;
            rotationVelocity *= brake;
        }

        /// <summary>
        /// Apply an instantaneous force to velocity
        /// </summary>
        public virtual void ApplyForce(Vector2 force)
        {
            velocityX += force.x;
            velocityY += force.y;
        }

        /// <summary>
        /// Apply an instantaneous impulse to velocity
        /// </summary>
        public virtual void ApplyImpulse(Vector2 impulse)
        {
            velocityX += impulse.x;
            velocityY += impulse.y;
        }

        /// <summary>
        /// Set vertical acceleration (typically gravity)
        /// </summary>
        public virtual void SetAcceleration(float acceleration)
        {
            accelY = acceleration;
        }

        /// <summary>
        /// Reset velocity and acceleration
        /// </summary>
        public virtual void ResetMotion()
        {
            velocityX = 0f;
            velocityY = 0f;
            accelY = 0f;
            rotationVelocity = 0f;
        }

        /// <summary>
        /// Check if velocity is effectively zero
        /// </summary>
        public bool IsNearlyStopped(float threshold = 0.01f)
        {
            return Mathf.Abs(velocityX) < threshold &&
                   Mathf.Abs(velocityY) < threshold &&
                   Mathf.Abs(rotationVelocity) < threshold;
        }

        /// <summary>
        /// Get speed magnitude
        /// </summary>
        public float GetSpeed()
        {
            return new Vector2(velocityX, velocityY).magnitude;
        }

        /// <summary>
        /// Set brake coefficient
        /// </summary>
        public void SetBrake(float newBrake)
        {
            brake = Mathf.Clamp01(newBrake);
        }

#if UNITY_EDITOR
        protected virtual void OnDrawGizmosSelected()
        {
            if (isMoving)
            {
                // Draw velocity vector
                Gizmos.color = Color.green;
                Gizmos.DrawLine(transform.position, transform.position + new Vector3(velocityX, velocityY, 0f) * 0.1f);

                // Draw acceleration vector
                Gizmos.color = Color.yellow;
                Gizmos.DrawLine(transform.position, transform.position + new Vector3(0f, accelY, 0f) * 0.1f);
            }
        }
#endif
    }
}
