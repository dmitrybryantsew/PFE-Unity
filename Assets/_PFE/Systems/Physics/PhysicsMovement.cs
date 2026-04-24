using UnityEngine;

namespace PFE.Systems.Physics
{
    /// <summary>
    /// Tile-based movement controller with input handling.
    /// Based on ActionScript Ctr.as input system and Part.as movement physics.
    /// Provides player/character movement with keyboard input.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(PhysicsBody))]
    public class PhysicsMovement : MonoBehaviour
    {
        [Header("Input Settings")]
        [Tooltip("Enable keyboard input control")]
        [SerializeField] protected bool enableInput = true;

        [SerializeField] protected bool useRawInput = false;

        [Header("Movement Keys")]
        [SerializeField] protected KeyCode keyLeft = KeyCode.A;
        [SerializeField] protected KeyCode keyRight = KeyCode.D;
        [SerializeField] protected KeyCode keyJump = KeyCode.Space;
        [SerializeField] protected KeyCode keyUp = KeyCode.W;
        [SerializeField] protected KeyCode keyDown = KeyCode.S;
        [SerializeField] protected KeyCode keyRun = KeyCode.LeftShift;

        [Header("Input State")]
        [SerializeField] protected bool isLeftPressed = false;
        [SerializeField] protected bool isRightPressed = false;
        [SerializeField] protected bool isJumpPressed = false;
        [SerializeField] protected bool isUpPressed = false;
        [SerializeField] protected bool isDownPressed = false;
        [SerializeField] protected bool isRunPressed = false;

        [Header("Ground Check")]
        [Tooltip("Transform from which to check ground")]
        [SerializeField] protected Transform groundCheck;

        [Tooltip("Radius for ground detection")]
        [SerializeField] protected float groundCheckRadius = 0.2f;

        [Tooltip("Layers considered ground")]
        [SerializeField] protected LayerMask groundLayer = 1;

        [Header("State")]
        [SerializeField] protected bool isGrounded = false;
        [SerializeField] protected bool isRunning = false;
        [SerializeField] protected int facingDirection = 1; // 1 = right, -1 = left

        protected PhysicsBody physicsBody;
        protected PhysicsConfig config;

        // Double tap detection (like Ctr.as keyDubLeft, keyDubRight)
        protected float lastLeftTapTime = -999f;
        protected float lastRightTapTime = -999f;
        protected const float DOUBLE_TAP_WINDOW = 0.3f;
        [SerializeField] protected bool isDoubleTapLeft = false;
        [SerializeField] protected bool isDoubleTapRight = false;

        /// <summary>
        /// Is the character on the ground?
        /// </summary>
        public bool IsGrounded => isGrounded;

        /// <summary>
        /// Is the character running?
        /// </summary>
        public bool IsRunning => isRunning;

        /// <summary>
        /// Current facing direction (1 = right, -1 = left)
        /// </summary>
        public int FacingDirection => facingDirection;

        /// <summary>
        /// Double tap left detected
        /// </summary>
        public bool IsDoubleTapLeft => isDoubleTapLeft;

        /// <summary>
        /// Double tap right detected
        /// </summary>
        public bool IsDoubleTapRight => isDoubleTapRight;

        protected virtual void Awake()
        {
            physicsBody = GetComponent<PhysicsBody>();
            config = physicsBody?.Config;

            if (groundCheck == null)
            {
                // Create ground check transform if not assigned
                GameObject groundCheckObj = new GameObject("GroundCheck");
                groundCheckObj.transform.SetParent(transform);
                groundCheckObj.transform.localPosition = new Vector3(0f, -0.5f, 0f);
                groundCheck = groundCheckObj.transform;
            }
        }

        protected virtual void Update()
        {
            if (!enableInput) return;

            ReadInput();
            CheckGrounded();
        }

        protected virtual void FixedUpdate()
        {
            ApplyMovement();
            ConsumeDoubleTap();
        }

        /// <summary>
        /// Read keyboard input (based on Ctr.as input handling)
        /// </summary>
        protected virtual void ReadInput()
        {
            // Left/Right movement
            bool wasLeftPressed = isLeftPressed;
            bool wasRightPressed = isRightPressed;

            isLeftPressed = Input.GetKey(keyLeft);
            isRightPressed = Input.GetKey(keyRight);

            // Double tap detection
            if (isLeftPressed && !wasLeftPressed && Time.time - lastLeftTapTime < DOUBLE_TAP_WINDOW)
            {
                isDoubleTapLeft = true;
            }
            if (isRightPressed && !wasRightPressed && Time.time - lastRightTapTime < DOUBLE_TAP_WINDOW)
            {
                isDoubleTapRight = true;
            }
            if (isLeftPressed && !wasLeftPressed)
            {
                lastLeftTapTime = Time.time;
            }
            if (isRightPressed && !wasRightPressed)
            {
                lastRightTapTime = Time.time;
            }

            // Other inputs
            isJumpPressed = Input.GetKey(keyJump);
            isUpPressed = Input.GetKey(keyUp);
            isDownPressed = Input.GetKey(keyDown);
            isRunPressed = Input.GetKey(keyRun);

            // Update facing direction
            if (isLeftPressed && !isRightPressed)
            {
                facingDirection = -1;
            }
            else if (isRightPressed && !isLeftPressed)
            {
                facingDirection = 1;
            }

            // Check running state
            isRunning = isRunPressed;
        }

        /// <summary>
        /// Apply movement forces based on input
        /// </summary>
        protected virtual void ApplyMovement()
        {
            if (physicsBody == null || config == null) return;

            // Apply gravity when not grounded
            if (!isGrounded)
            {
                physicsBody.SetAcceleration(config.gravity);
                physicsBody.SetBrake(config.airBrake);
            }
            else
            {
                physicsBody.SetAcceleration(0f);
                physicsBody.SetBrake(config.groundBrake);

                // Stop falling when on ground
                if (physicsBody.VelocityY < 0f)
                {
                    physicsBody.VelocityY = 0f;
                }
            }

            // Horizontal movement
            float moveInput = 0f;
            if (isLeftPressed) moveInput -= 1f;
            if (isRightPressed) moveInput += 1f;

            float acceleration = config.moveAcceleration;
            float maxSpeed = config.maxHorizontalSpeed;

            if (isRunning)
            {
                acceleration *= 1.5f;
                maxSpeed *= 1.5f;
            }

            // Apply horizontal force
            if (Mathf.Abs(moveInput) > 0.01f)
            {
                float accelerationForce = acceleration * Time.fixedDeltaTime;
                physicsBody.VelocityX += moveInput * accelerationForce;

                // Clamp to max speed
                physicsBody.VelocityX = Mathf.Clamp(physicsBody.VelocityX, -maxSpeed, maxSpeed);
            }

            // Jump
            if (isJumpPressed && isGrounded)
            {
                physicsBody.VelocityY = config.jumpImpulse;
                isGrounded = false;
            }

            // Step physics
            physicsBody.PhysicsStep(Time.fixedDeltaTime);
        }

        /// <summary>
        /// Check if character is on ground
        /// </summary>
        protected virtual void CheckGrounded()
        {
            isGrounded = false;
            if (groundCheck != null)
            {
                isGrounded = Physics2D.OverlapCircle(groundCheck.position, groundCheckRadius, groundLayer);
            }
        }

        /// <summary>
        /// Consume double tap flags after one frame
        /// </summary>
        protected virtual void ConsumeDoubleTap()
        {
            if (isDoubleTapLeft || isDoubleTapRight)
            {
                // Reset after one frame of being true
                isDoubleTapLeft = false;
                isDoubleTapRight = false;
            }
        }

        /// <summary>
        /// Set the movement keys programmatically
        /// </summary>
        public virtual void SetKeys(KeyCode left, KeyCode right, KeyCode jump, KeyCode up, KeyCode down, KeyCode run)
        {
            keyLeft = left;
            keyRight = right;
            keyJump = jump;
            keyUp = up;
            keyDown = down;
            keyRun = run;
        }

        /// <summary>
        /// Enable or disable input control
        /// </summary>
        public virtual void SetInputEnabled(bool enabled)
        {
            enableInput = enabled;
        }

#if UNITY_EDITOR
        protected virtual void OnDrawGizmos()
        {
            if (groundCheck != null)
            {
                Gizmos.color = isGrounded ? Color.green : Color.red;
                Gizmos.DrawWireSphere(groundCheck.position, groundCheckRadius);
            }
        }
#endif
    }
}
