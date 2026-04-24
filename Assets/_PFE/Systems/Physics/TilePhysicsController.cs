using UnityEngine;
using PFE.Systems.Map;
using PFE.Entities.Units;

namespace PFE.Systems.Physics
{
    /// <summary>
    /// Tile-based physics controller for player/unit movement.
    /// Replaces Unity Rigidbody2D collision with direct tile grid queries.
    ///
    /// This solves the core problem: your rooms use TileData[,] arrays for collision,
    /// but your UnitController uses Rigidbody2D + Unity colliders.
    /// This component reads tiles directly from RoomInstance, matching AS3 behavior.
    ///
    /// From AS3 Unit.as:
    ///   - run() applies velocity + gravity each frame
    ///   - collisionTile() checks against tile grid
    ///   - stay flag = grounded
    ///   - dx/dy = velocity
    ///   - brake = friction
    ///   - grav = gravity multiplier
    ///
    /// Usage: Add this component to your Player GameObject.
    ///        Call SetRoom() when entering a new room.
    ///        This replaces Rigidbody2D physics entirely.
    /// </summary>
    [DefaultExecutionOrder(-150)]
    public class TilePhysicsController : MonoBehaviour, IMovementMotor
    {
        [Header("Unit Dimensions (pixels, matching AS3)")]
        [Tooltip("Width of collision box in pixels (scX in AS3)")]
        [SerializeField] private float collisionWidth = 30f;

        [Tooltip("Height of collision box in pixels (scY in AS3)")]
        [SerializeField] private float collisionHeight = 50f;

        [Tooltip("Crouched collision height in pixels. If <= 0, it is derived from UnitDefinition.sitHeight or a standing-height multiplier.")]
        [SerializeField] private float crouchedCollisionHeight = 0f;

        [Tooltip("Offset from this transform to the collider feet position, in pixels. Negative Y moves the collider down.")]
        [SerializeField] private Vector2 colliderOffsetPixels = Vector2.zero;

        [Header("Movement (matching AS3 Unit properties)")]
        [Tooltip("Horizontal acceleration (accel in AS3)")]
        [SerializeField] private float acceleration = 2.0f;

        [Tooltip("Maximum horizontal speed (pixels/frame, maxdx in AS3)")]
        [SerializeField] private float maxSpeedX = 8f;

        [Tooltip("Maximum vertical speed (pixels/frame, maxdy in AS3)")]
        [SerializeField] private float maxSpeedY = 20f;

        [Tooltip("Jump force (pixels/frame, jumpdy in AS3)")]
        [SerializeField] private float jumpForce = 10f;

        [Tooltip("Ground friction (brake in AS3, 0-1)")]
        [SerializeField] private float groundFriction = 0.7f;

        [Tooltip("Air friction")]
        [SerializeField] private float airFriction = 0.95f;

        [Tooltip("Gravity multiplier (grav in AS3)")]
        [SerializeField] private float gravityMult = 1.0f;

        [Header("Physics Constants")]
        [Tooltip("Global gravity (World.ddy in AS3)")]
        [SerializeField] private float globalGravity = 0.98f;

        [Tooltip("Platform pass-through threshold (porog in AS3)")]
        [SerializeField] private float platformThreshold = 8f;

        [Tooltip("Maximum distance a single collision sub-step is allowed to move before movement is subdivided.")]
        [SerializeField] private float maxSubStepDistance = 9f;

        [Tooltip("Maximum vertical step-up height in pixels when grounded and walking into a low obstacle.")]
        [SerializeField] private float stepUpThreshold = 10f;

        [Tooltip("Reduced step-up threshold while airborne or rising.")]
        [SerializeField] private float stepUpThresholdWhileAirborne = 4f;

        [Tooltip("How long platform collisions stay disabled after a drop-through request.")]
        [SerializeField] private float platformDropDurationSeconds = 0.18f;

        [Header("Ladder Movement")]
        [Tooltip("Vertical climb speed in pixels/frame while attached to a ladder.")]
        [SerializeField] private float ladderClimbSpeed = 5f;

        [Tooltip("Horizontal half-width in pixels used for ladder-specific collision probes.")]
        [SerializeField] private float ladderProbeHalfWidth = 6f;

        [Header("State (read-only)")]
        [SerializeField] private bool isGrounded;
        [SerializeField] private bool isOnPlatform;
        [SerializeField] private bool isInWater;
        [SerializeField] private bool isFullySubmerged;
        [SerializeField] private bool isOnLadder;
        [SerializeField] private bool isCrouching;
        [SerializeField] private bool hitCeiling;
        [SerializeField] private bool wallLeft;
        [SerializeField] private bool wallRight;
        [SerializeField] private int facingDirection = 1;

        // Velocity in pixel space (matching AS3 dx/dy)
        private float dx;
        private float dy;

        // Collider feet position in world pixel space.
        private float posX;
        private float posY;

        // Room reference
        private RoomInstance currentRoom;
        private TileCollisionSystem collisionSystem;
        
        // Room's world pixel position (for converting between world and room-local coordinates)
        private float roomWorldPixelX;
        private float roomWorldPixelY;

        // Legacy input state
        private float inputX;
        private bool inputDown; // For falling through platforms

        // Motor command state
        private float desiredHorizontalSpeed;
        private float desiredVerticalSpeed;
        private bool hasDesiredVerticalSpeed;
        private float ladderInputY;
        private float gravityScale = 1f;
        private bool wantsToCrouch;
        private bool wantsToUseLadder;
        private int activeLadderDirection;
        private float activeLadderSnapX;
        private float dashTimer;
        private float platformDropTimer;
        private Vector2 dashVelocity;
        private float standingCollisionHeight;
        private float resolvedCrouchedCollisionHeight;

        // Conversion: pixels to Unity units
        // Your WorldCoordinates uses 100 pixels = 1 Unity unit
        private const float PIX_TO_UNIT = 0.01f;
        private const float UNIT_TO_PIX = 100f;

        private readonly struct LadderContact
        {
            public readonly Vector2Int TileCoord;
            public readonly int Direction;
            public readonly float SnapX;

            public LadderContact(Vector2Int tileCoord, int direction, float snapX)
            {
                TileCoord = tileCoord;
                Direction = direction;
                SnapX = snapX;
            }
        }

        // Properties
        public float CollisionWidth => collisionWidth;
        public float CollisionHeight => collisionHeight;
        public bool IsGrounded => isGrounded;
        public bool IsOnPlatform => isOnPlatform;
        public bool IsInWater => isInWater;
        public bool IsFullySubmerged => isFullySubmerged;
        public bool IsOnLadder => isOnLadder;
        public bool IsCrouching => isCrouching;
        public bool HitCeiling => hitCeiling;
        public bool WallLeft => wallLeft;
        public bool WallRight => wallRight;
        public int FacingDirection => facingDirection;
        public float VelocityX => dx;
        public float VelocityY => dy;
        public Vector2 PixelPosition => new Vector2(posX, posY);
        public Vector2 ColliderOffsetPixels => colliderOffsetPixels;
        public MovementMotorState State => new MovementMotorState(
            isGrounded,
            isInWater,
            isFullySubmerged,
            isOnLadder,
            isCrouching,
            hitCeiling,
            wallLeft,
            wallRight,
            new Vector2(dx, dy),
            new Vector2(posX, posY),
            facingDirection);

        /// <summary>
        /// Set the current room for tile collision queries.
        /// Call this whenever the player enters a new room.
        /// </summary>
        public void SetRoom(RoomInstance room)
        {
            currentRoom = room;
            if (room != null)
            {
                collisionSystem = new TileCollisionSystem(room);
                
                // Calculate room's world pixel position
                // This accounts for land position and border offset
                int borderOffsetTiles = room.borderOffset;
                roomWorldPixelX = room.landPosition.x * WorldConstants.ROOM_WIDTH * WorldConstants.TILE_SIZE
                                  - borderOffsetTiles * WorldConstants.TILE_SIZE;
                roomWorldPixelY = room.landPosition.y * WorldConstants.ROOM_HEIGHT * WorldConstants.TILE_SIZE
                                  - borderOffsetTiles * WorldConstants.TILE_SIZE;
            }
            else
            {
                collisionSystem = null;
                roomWorldPixelX = 0;
                roomWorldPixelY = 0;
            }
        }

        /// <summary>
        /// Set movement input (call from PlayerController or InputReader).
        /// </summary>
        public void SetInput(float horizontal, bool jump, bool down = false)
        {
            inputX = horizontal;
            if (down && !inputDown)
            {
                StartPlatformDropThrough();
            }
            inputDown = down;
            desiredHorizontalSpeed = horizontal * maxSpeedX;
        }

        /// <summary>
        /// Add external force (knockback, explosions).
        /// From AS3: Unit.forces()
        /// </summary>
        public void AddForce(float forceX, float forceY)
        {
            dx += forceX;
            dy += forceY;
        }

        public void AddForce(Vector2 force)
        {
            AddForce(force.x, force.y);
        }

        public void SetDesiredHorizontalSpeed(float speed)
        {
            desiredHorizontalSpeed = speed;
        }

        public void SetDesiredVerticalSpeed(float speed)
        {
            desiredVerticalSpeed = speed;
            hasDesiredVerticalSpeed = true;
        }

        public void SetLadderInput(float verticalInput, bool wantsToClimb)
        {
            ladderInputY = Mathf.Clamp(verticalInput, -1f, 1f);
            wantsToUseLadder = wantsToClimb;
        }

        public void SetDropThroughPlatforms(bool shouldDrop)
        {
            if (shouldDrop)
            {
                StartPlatformDropThrough();
            }
        }

        public void SetCrouching(bool isCrouchingRequested)
        {
            wantsToCrouch = isCrouchingRequested;
        }

        public void Jump(float force)
        {
            isOnLadder = false;
            dy = force;
            isGrounded = false;
            isOnPlatform = false;
        }

        public void Dash(Vector2 direction, float speed, float duration)
        {
            Vector2 dashDirection = direction.sqrMagnitude > 0.001f
                ? direction.normalized
                : new Vector2(facingDirection, 0f);
            dashVelocity = dashDirection * Mathf.Max(0f, speed);
            dashTimer = Mathf.Max(0f, duration);
            dx = dashVelocity.x;
            dy = dashVelocity.y;
            isOnLadder = false;
            isGrounded = false;
        }

        public void SetGravityScale(float scale)
        {
            gravityScale = Mathf.Max(0f, scale);
        }

        /// <summary>
        /// Check whether a teleport destination is free of solid geometry.
        /// </summary>
        public bool CanTeleportTo(float targetPixelX, float targetPixelY, float halfWidth, float halfHeight)
        {
            if (collisionSystem == null) return false;

            // Build AABB at target in world pixel space
            Rect targetBounds = new Rect(
                targetPixelX - halfWidth,
                targetPixelY,
                halfWidth * 2f,
                halfHeight);

            return !collisionSystem.CheckCollision(targetBounds, isTransparent: false, canFallThroughPlatforms: true, velocityY: 0f);
        }

        /// <summary>
        /// Instantly move to a target position in pixel space. Zeroes velocity and detaches from ladder.
        /// Caller must validate with CanTeleportTo first.
        /// </summary>
        public void TeleportTo(float targetPixelX, float targetPixelY)
        {
            posX = targetPixelX;
            posY = targetPixelY;
            dx = 0f;
            dy = 0f;
            isOnLadder = false;
            dashTimer = 0f;
            SyncUnityPosition();
        }

        /// <summary>
        /// Teleport using the GameObject transform position in pixel space.
        /// </summary>
        public void SetPixelPosition(float x, float y)
        {
            Vector2 colliderPos = TransformPixelToColliderPixel(new Vector2(x, y));
            posX = colliderPos.x;
            posY = colliderPos.y;
            SyncUnityPosition();
        }

        /// <summary>
        /// Teleport using the GameObject transform position in Unity world space.
        /// </summary>
        public void SetUnityPosition(Vector3 worldPos)
        {
            Vector2 colliderPos = TransformPixelToColliderPixel(new Vector2(
                worldPos.x * UNIT_TO_PIX,
                worldPos.y * UNIT_TO_PIX));
            posX = colliderPos.x;
            posY = colliderPos.y;
            SyncUnityPosition();
        }

        private void Start()
        {
            standingCollisionHeight = collisionHeight;
            resolvedCrouchedCollisionHeight = ResolveCrouchedCollisionHeight();

            // Initialize pixel position from current Unity transform
            Vector2 colliderPos = TransformPixelToColliderPixel(new Vector2(
                transform.position.x * UNIT_TO_PIX,
                transform.position.y * UNIT_TO_PIX));
            posX = colliderPos.x;
            posY = colliderPos.y;
        }

        private void FixedUpdate()
        {
            if (currentRoom == null) return;

            hitCeiling = false;
            wallLeft = false;
            wallRight = false;
            UpdateColliderProfile();
            UpdateLadderState();

            // === AS3 Unit.run() equivalent ===

            float previousDashTimer = dashTimer;

            // 1. Apply motor commands
            ApplyHorizontalMovement();

            // 2. Apply gravity
            ApplyGravity();

            // 2b. Override vertical speed if locomotion controller is driving it (levitation)
            if (hasDesiredVerticalSpeed)
            {
                dy = desiredVerticalSpeed;
                hasDesiredVerticalSpeed = false;
            }

            // 3. Apply damping/friction
            ApplyFriction();

            // 3b. Water drag (AS3: dx *= 0.8, dy *= 0.8 when fully submerged; dx *= 0.5 when wading)
            if (isFullySubmerged)
            {
                dx *= 0.8f;
                dy *= 0.8f;
            }
            else if (isInWater)
            {
                dx *= 0.5f;
            }

            // 4. Clamp velocity
            float horizontalClamp = dashTimer > 0f
                ? Mathf.Max(maxSpeedX, Mathf.Abs(dashVelocity.x), Mathf.Abs(desiredHorizontalSpeed))
                : Mathf.Max(maxSpeedX, Mathf.Abs(desiredHorizontalSpeed));
            float verticalClamp = dashTimer > 0f
                ? Mathf.Max(maxSpeedY, Mathf.Abs(dashVelocity.y))
                : maxSpeedY;
            dx = Mathf.Clamp(dx, -horizontalClamp, horizontalClamp);
            dy = Mathf.Clamp(dy, -verticalClamp, verticalClamp);

            // 5. Move with tile collision
            MoveWithCollision();
            RefreshLadderAttachment();

            // 6. Sync Unity transform
            SyncUnityPosition();

            // 7. Update facing
            if (dx > 0.5f) facingDirection = 1;
            else if (dx < -0.5f) facingDirection = -1;

            // 8. Flip sprite
            Vector3 scale = transform.localScale;
            scale.x = Mathf.Abs(scale.x) * facingDirection;
            transform.localScale = scale;

            // 9. Check water
            CheckWater();

            dashTimer = Mathf.Max(0f, dashTimer - Time.fixedDeltaTime);
            platformDropTimer = Mathf.Max(0f, platformDropTimer - Time.fixedDeltaTime);
            if (previousDashTimer > 0f && dashTimer <= 0f)
            {
                dx *= 0.3f;
                dy *= 0.3f;
            }
        }

        /// <summary>
        /// Apply horizontal motor commands.
        /// </summary>
        private void ApplyHorizontalMovement()
        {
            if (dashTimer > 0f)
            {
                dx = dashVelocity.x;
                dy = dashVelocity.y;
                return;
            }

            if (isOnLadder)
            {
                dx = Mathf.MoveTowards(dx, 0f, acceleration);
                dy = Mathf.Abs(ladderInputY) > 0.1f
                    ? ladderInputY * ladderClimbSpeed
                    : 0f;
                isGrounded = false;
                isOnPlatform = false;
                return;
            }

            float accelerationRate = isGrounded ? acceleration : acceleration * 0.35f;
            dx = Mathf.MoveTowards(dx, desiredHorizontalSpeed, accelerationRate);
        }

        /// <summary>
        /// Apply gravity.
        /// From AS3: dy += World.ddy * grav
        /// Note: AS3 Y increases downward, Unity Y increases upward.
        /// So gravity is SUBTRACTED in Unity.
        /// </summary>
        private void ApplyGravity()
        {
            if (isOnLadder)
            {
                return;
            }

            if (!isGrounded && dashTimer <= 0f)
            {
                dy -= globalGravity * gravityMult * gravityScale;
            }
        }

        /// <summary>
        /// Apply friction.
        /// From AS3: dx *= (1 - brake) when grounded
        /// </summary>
        private void ApplyFriction()
        {
            if (dashTimer > 0f)
            {
                return;
            }

            float friction = isGrounded ? groundFriction : airFriction;

            if (Mathf.Abs(desiredHorizontalSpeed) < 0.1f)
            {
                dx *= friction;
                if (Mathf.Abs(dx) < 0.1f) dx = 0;
            }
        }

        /// <summary>
        /// Move with tile collision detection.
        /// This is the core physics loop matching AS3 collision behavior.
        ///
        /// Strategy: Move X and Y separately, check collision after each.
        /// This prevents diagonal tunneling and gives correct wall sliding.
        /// </summary>
        private void MoveWithCollision()
        {
            if (currentRoom == null || currentRoom.tiles == null) return;

            // Convert velocity from AS3-style (pixels/frame) to per-fixedUpdate
            float moveX = dx * Time.fixedDeltaTime * 60f; // Scale to ~60fps base
            float moveY = dy * Time.fixedDeltaTime * 60f;

            if (isOnLadder)
            {
                MoveOnLadder(moveY);
                return;
            }

            float maxDistance = Mathf.Max(Mathf.Abs(moveX), Mathf.Abs(moveY));
            int subSteps = Mathf.Max(1, Mathf.CeilToInt(maxDistance / Mathf.Max(1f, maxSubStepDistance)));
            float stepMoveX = moveX / subSteps;
            float stepMoveY = moveY / subSteps;

            for (int i = 0; i < subSteps; i++)
            {
                MoveSingleStep(stepMoveX, stepMoveY);
            }
        }

        private void MoveOnLadder(float moveY)
        {
            float hw = Mathf.Min(collisionWidth * 0.5f, Mathf.Max(2f, ladderProbeHalfWidth));
            float hh = collisionHeight;
            int subSteps = Mathf.Max(1, Mathf.CeilToInt(Mathf.Abs(moveY) / Mathf.Max(1f, maxSubStepDistance)));
            float stepMoveY = moveY / subSteps;

            posX = activeLadderSnapX;
            isGrounded = false;
            isOnPlatform = false;

            for (int i = 0; i < subSteps; i++)
            {
                float targetY = posY + stepMoveY;

                if (stepMoveY > 0f && CheckCeilingCollisionAt(posX, targetY, hw, hh))
                {
                    dy = 0f;
                    hitCeiling = true;
                    posY = ResolveVerticalUp(posX, posY, targetY, hw, hh);
                    break;
                }

                if (stepMoveY < 0f &&
                    CheckGroundCollisionAt(posX, targetY, hw) &&
                    !TryGetLadderContactAt(posX, targetY, hh, out _))
                {
                    dy = 0f;
                    posY = ResolveVerticalDown(posX, posY, targetY, hw);
                    isOnLadder = false;
                    isGrounded = true;
                    break;
                }

                posY = targetY;

                if (!TryGetLadderContactAt(posX, posY, hh, out LadderContact ladderContact))
                {
                    isOnLadder = false;
                    break;
                }

                SnapToLadder(ladderContact);
            }
        }

        private void MoveSingleStep(float moveX, float moveY)
        {
            float hw = collisionWidth * 0.5f;
            float hh = collisionHeight;

            // Use a small vertical inset for horizontal checks so that being
            // snapped flush against a ceiling doesn't register the ceiling
            // tile row as a wall collision (standard tile-physics margin).
            const float ceilingInset = 1f;
            float hhHorizontalCheck = hh - ceilingInset;

            float newX = posX + moveX;
            if (CheckTileCollisionAt(newX, posY, hw, hhHorizontalCheck))
            {
                if (!TryStepUp(newX, hw, hhHorizontalCheck))
                {
                    if (moveX < 0f)
                    {
                        wallLeft = true;
                    }
                    else if (moveX > 0f)
                    {
                        wallRight = true;
                    }

                    dx = 0;
                    newX = ResolveHorizontal(posX, newX, posY, hw, hhHorizontalCheck);
                    posX = newX;
                }
            }
            else
            {
                posX = newX;
            }

            float newY = posY + moveY;

            isGrounded = false;
            isOnPlatform = false;

            if (moveY <= 0f)
            {
                if (CheckGroundCollisionAt(posX, newY, hw))
                {
                    newY = ResolveVerticalDown(posX, posY, newY, hw);
                    dy = 0;
                    isGrounded = true;
                }
            }
            else if (CheckCeilingCollisionAt(posX, newY, hw, hh))
            {
                dy = 0;
                hitCeiling = true;
                newY = ResolveVerticalUp(posX, posY, newY, hw, hh);
            }

            posY = newY;

            if (!isGrounded && Mathf.Abs(dy) < 0.5f && CheckGroundCollisionAt(posX, posY - 1f, hw))
            {
                isGrounded = true;
            }
        }

        private void UpdateColliderProfile()
        {
            if (standingCollisionHeight <= 0f)
            {
                standingCollisionHeight = collisionHeight;
            }

            if (resolvedCrouchedCollisionHeight <= 0f)
            {
                resolvedCrouchedCollisionHeight = ResolveCrouchedCollisionHeight();
            }

            float hw = collisionWidth * 0.5f;
            bool standingBlocked = HasSolidOverlapAt(posX, posY, hw, standingCollisionHeight);

            if (standingBlocked)
            {
                collisionHeight = resolvedCrouchedCollisionHeight;
                isCrouching = true;
                return;
            }

            if (wantsToCrouch)
            {
                collisionHeight = resolvedCrouchedCollisionHeight;
                isCrouching = true;
                return;
            }

            if (!isCrouching)
            {
                collisionHeight = standingCollisionHeight;
                return;
            }

            if (!HasSolidOverlapAt(posX, posY, hw, standingCollisionHeight))
            {
                collisionHeight = standingCollisionHeight;
                isCrouching = false;
            }
            else
            {
                collisionHeight = resolvedCrouchedCollisionHeight;
                isCrouching = true;
            }
        }

        private float ResolveCrouchedCollisionHeight()
        {
            if (crouchedCollisionHeight > 0f)
            {
                return Mathf.Min(crouchedCollisionHeight, standingCollisionHeight > 0f ? standingCollisionHeight : collisionHeight);
            }

            UnitController unitController = GetComponent<UnitController>();
            if (unitController != null && unitController.Stats != null && unitController.Stats.sitHeight > 0f)
            {
                return Mathf.Min(unitController.Stats.sitHeight * UNIT_TO_PIX, standingCollisionHeight > 0f ? standingCollisionHeight : collisionHeight);
            }

            return Mathf.Max(standingCollisionHeight * 0.65f, 12f);
        }

        private bool TryStepUp(float attemptedX, float hw, float hh)
        {
            float maxStepHeight = (!isGrounded || dy > 0.1f)
                ? stepUpThresholdWhileAirborne
                : stepUpThreshold;
            int pixelSteps = Mathf.Max(0, Mathf.RoundToInt(maxStepHeight));
            for (int step = 1; step <= pixelSteps; step++)
            {
                float raisedY = posY + step;
                if (CheckTileCollisionAt(attemptedX, raisedY, hw, hh))
                {
                    continue;
                }

                if (!CheckGroundCollisionAt(attemptedX, raisedY, hw))
                {
                    continue;
                }

                posX = attemptedX;
                posY = raisedY;
                isGrounded = true;
                return true;
            }

            return false;
        }

        private void UpdateLadderState()
        {
            if (dashTimer > 0f)
            {
                isOnLadder = false;
                activeLadderDirection = 0;
                return;
            }

            bool climbUpPressed = ladderInputY > 0.5f;
            bool climbDownPressed = ladderInputY < -0.5f;
            bool hasLadderContact = TryGetLadderContactForCurrentIntent(out LadderContact ladderContact);

            if (isOnLadder)
            {
                if (!hasLadderContact || (Mathf.Abs(desiredHorizontalSpeed) > 0.1f && !wantsToUseLadder))
                {
                    isOnLadder = false;
                    activeLadderDirection = 0;
                    return;
                }

                SnapToLadder(ladderContact);
                isGrounded = false;
                isOnPlatform = false;
                return;
            }

            bool canEnterFromHere = climbUpPressed || (!isGrounded && climbDownPressed);
            if (!wantsToUseLadder || !canEnterFromHere || !hasLadderContact)
            {
                return;
            }

            isOnLadder = true;
            isGrounded = false;
            isOnPlatform = false;
            wantsToCrouch = false;
            dx = 0f;
            dy = 0f;
            SnapToLadder(ladderContact);
        }

        private void RefreshLadderAttachment()
        {
            if (!isOnLadder)
            {
                return;
            }

            if (TryGetLadderContactAt(posX, posY, collisionHeight, out LadderContact ladderContact))
            {
                SnapToLadder(ladderContact);
                isGrounded = false;
                isOnPlatform = false;
                return;
            }

            isOnLadder = false;
            activeLadderDirection = 0;
        }

        private bool TryGetLadderContactForCurrentIntent(out LadderContact ladderContact)
        {
            float probeY = posY;
            if (ladderInputY > 0.5f)
            {
                probeY += 2f;
            }
            else if (ladderInputY < -0.5f)
            {
                probeY -= 2f;
            }

            if (TryGetLadderContactAt(posX, probeY, collisionHeight, out ladderContact))
            {
                return true;
            }

            return TryGetLadderContactAt(posX, posY, collisionHeight, out ladderContact);
        }

        private bool TryGetLadderContactAt(float x, float y, float hh, out LadderContact ladderContact)
        {
            ladderContact = default;
            if (currentRoom == null || currentRoom.tiles == null)
            {
                return false;
            }

            float hw = collisionWidth * 0.5f;
            int tileLeft = Mathf.FloorToInt((x - hw - roomWorldPixelX) / WorldConstants.TILE_SIZE);
            int tileRight = Mathf.FloorToInt((x + hw - roomWorldPixelX) / WorldConstants.TILE_SIZE);
            int tileBottom = Mathf.FloorToInt((y - roomWorldPixelY) / WorldConstants.TILE_SIZE) - 1;
            int tileTop = Mathf.FloorToInt((y + hh - roomWorldPixelY) / WorldConstants.TILE_SIZE) + 1;

            float bestDistance = float.MaxValue;
            bool found = false;

            for (int tx = tileLeft; tx <= tileRight; tx++)
            {
                for (int ty = tileBottom; ty <= tileTop; ty++)
                {
                    TileData tile = currentRoom.GetTileAtCoord(new Vector2Int(tx, ty));
                    if (tile == null || !tile.IsClimbableLadder())
                    {
                        continue;
                    }

                    float snapX = ComputeLadderSnapX(tx, tile.stairType);
                    float distance = Mathf.Abs(x - snapX);
                    if (activeLadderDirection != 0 && tile.stairType != activeLadderDirection)
                    {
                        distance += WorldConstants.TILE_SIZE;
                    }

                    if (distance >= bestDistance)
                    {
                        continue;
                    }

                    bestDistance = distance;
                    ladderContact = new LadderContact(new Vector2Int(tx, ty), tile.stairType, snapX);
                    found = true;
                }
            }

            return found;
        }

        private float ComputeLadderSnapX(int tileX, int stairDirection)
        {
            float tileLeft = roomWorldPixelX + tileX * WorldConstants.TILE_SIZE;
            float tileRight = tileLeft + WorldConstants.TILE_SIZE;
            float halfWidth = collisionWidth * 0.5f;
            return stairDirection < 0
                ? tileLeft + halfWidth
                : tileRight - halfWidth;
        }

        private void SnapToLadder(LadderContact ladderContact)
        {
            activeLadderDirection = ladderContact.Direction;
            activeLadderSnapX = ladderContact.SnapX;
            posX = Mathf.MoveTowards(posX, activeLadderSnapX, Mathf.Max(1f, maxSubStepDistance));
        }

        private bool HasSolidOverlapAt(float x, float y, float hw, float hh)
        {
            if (currentRoom == null || currentRoom.tiles == null)
            {
                return false;
            }

            Rect bounds = Rect.MinMaxRect(
                x - hw + 0.01f,
                y + 0.01f,
                x + hw - 0.01f,
                y + hh - 0.01f);

            if (bounds.width <= 0f || bounds.height <= 0f)
            {
                return false;
            }

            int tileLeft = Mathf.FloorToInt((bounds.xMin - roomWorldPixelX) / WorldConstants.TILE_SIZE);
            int tileRight = Mathf.FloorToInt((bounds.xMax - roomWorldPixelX) / WorldConstants.TILE_SIZE);
            int tileBottom = Mathf.FloorToInt((bounds.yMin - roomWorldPixelY) / WorldConstants.TILE_SIZE);
            int tileTop = Mathf.FloorToInt((bounds.yMax - roomWorldPixelY) / WorldConstants.TILE_SIZE);

            for (int tx = tileLeft; tx <= tileRight; tx++)
            {
                for (int ty = tileBottom; ty <= tileTop; ty++)
                {
                    TileData tile = currentRoom.GetTileAtCoord(new Vector2Int(tx, ty));
                    if (tile == null || tile.physicsType != TilePhysicsType.Wall)
                    {
                        continue;
                    }

                    Rect tileBounds = tile.GetBounds();
                    tileBounds.position += new Vector2(roomWorldPixelX, roomWorldPixelY);
                    if (bounds.Overlaps(tileBounds))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Check if position collides with solid tiles.
        /// Bounds: center at (x, y + hh/2), size (hw*2, hh)
        /// Converts world pixel coordinates to room-local tile coordinates.
        /// </summary>
        private bool CheckTileCollisionAt(float x, float y, float hw, float hh)
        {
            // Entity box: x-hw to x+hw, y to y+hh (y is feet, y+hh is head)
            float left = x - hw;
            float right = x + hw;
            float bottom = y;
            float top = y + hh;

            // Convert world pixel coordinates to room-local tile coordinates
            int tileLeft = Mathf.FloorToInt((left - roomWorldPixelX) / WorldConstants.TILE_SIZE);
            int tileRight = Mathf.FloorToInt((right - roomWorldPixelX) / WorldConstants.TILE_SIZE);
            int tileBottom = Mathf.FloorToInt((bottom - roomWorldPixelY) / WorldConstants.TILE_SIZE);
            int tileTop = Mathf.FloorToInt((top - roomWorldPixelY) / WorldConstants.TILE_SIZE);

            for (int tx = tileLeft; tx <= tileRight; tx++)
            {
                for (int ty = tileBottom; ty <= tileTop; ty++)
                {
                    var tile = currentRoom.GetTileAtCoord(new Vector2Int(tx, ty));
                    if (tile != null && tile.physicsType == TilePhysicsType.Wall)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Check ground collision (walls and platforms below feet).
        /// Converts world pixel coordinates to room-local tile coordinates.
        /// </summary>
        private bool CheckGroundCollisionAt(float x, float y, float hw)
        {
            float left = x - hw;
            float right = x + hw;

            // Convert world pixel coordinates to room-local tile coordinates
            int tileLeft = Mathf.FloorToInt((left - roomWorldPixelX) / WorldConstants.TILE_SIZE);
            int tileRight = Mathf.FloorToInt((right - roomWorldPixelX) / WorldConstants.TILE_SIZE);
            int tileY = Mathf.FloorToInt((y - roomWorldPixelY) / WorldConstants.TILE_SIZE);

            // Don't fall through platforms when pressing down
            bool canFallThrough = inputDown ||
                platformDropTimer > 0f ||
                (isOnLadder && ladderInputY < -0.1f);

            for (int tx = tileLeft; tx <= tileRight; tx++)
            {
                var tile = currentRoom.GetTileAtCoord(new Vector2Int(tx, tileY));
                if (tile == null) continue;

                if (tile.physicsType == TilePhysicsType.Wall)
                {
                    return true;
                }

                if (tile.physicsType == TilePhysicsType.Platform && !canFallThrough)
                {
                    // Platform collision: only from above
                    // Convert tile top from room-local to world pixel coordinates
                    float tileTop = roomWorldPixelY + (tileY + 1) * WorldConstants.TILE_SIZE;
                    if (y <= tileTop && y >= tileTop - platformThreshold)
                    {
                        isOnPlatform = true;
                        return true;
                    }
                }

                // Only real slopes should act as walkable ground surfaces.
                if (tile.IsSlopeSurface())
                {
                    // Get ground height in room-local coordinates, then convert to world
                    float localX = x - roomWorldPixelX;
                    float groundH = tile.GetGroundHeight(localX) + roomWorldPixelY;
                    if (y <= groundH)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private void StartPlatformDropThrough()
        {
            platformDropTimer = Mathf.Max(platformDropTimer, platformDropDurationSeconds);

            if (isGrounded)
            {
                dy = Mathf.Min(dy, -2f);
                posY -= 1f;
                isGrounded = false;
                isOnPlatform = false;
            }
        }

        /// <summary>
        /// Check ceiling collision above head.
        /// Converts world pixel coordinates to room-local tile coordinates.
        /// </summary>
        private bool CheckCeilingCollisionAt(float x, float y, float hw, float hh)
        {
            float headY = y + hh;
            
            // Convert world pixel coordinates to room-local tile coordinates
            int tileY = Mathf.FloorToInt((headY - roomWorldPixelY) / WorldConstants.TILE_SIZE);

            int tileLeft = Mathf.FloorToInt((x - hw - roomWorldPixelX) / WorldConstants.TILE_SIZE);
            int tileRight = Mathf.FloorToInt((x + hw - roomWorldPixelX) / WorldConstants.TILE_SIZE);

            for (int tx = tileLeft; tx <= tileRight; tx++)
            {
                var tile = currentRoom.GetTileAtCoord(new Vector2Int(tx, tileY));
                if (tile == null)
                {
                    continue;
                }

                if (tile.physicsType == TilePhysicsType.Wall)
                {
                    if (isOnLadder && tile.stairType != 0)
                    {
                        continue;
                    }

                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Resolve horizontal collision by binary searching for valid X.
        /// </summary>
        private float ResolveHorizontal(float fromX, float toX, float y, float hw, float hh)
        {
            float dir = Mathf.Sign(toX - fromX);
            float step = 1f; // 1 pixel steps

            float testX = fromX;
            while (Mathf.Abs(testX - fromX) < Mathf.Abs(toX - fromX))
            {
                float nextX = testX + dir * step;
                if (CheckTileCollisionAt(nextX, y, hw, hh))
                {
                    return testX;
                }
                testX = nextX;
            }
            return testX;
        }

        /// <summary>
        /// Resolve downward collision - snap to ground surface.
        /// Converts world pixel coordinates to room-local tile coordinates.
        /// </summary>
        private float ResolveVerticalDown(float x, float fromY, float toY, float hw)
        {
            // Convert world pixel coordinates to room-local tile coordinates
            int tileY = Mathf.FloorToInt((toY - roomWorldPixelY) / WorldConstants.TILE_SIZE);
            
            // Tile top in world pixel coordinates
            float tileTop = roomWorldPixelY + (tileY + 1) * WorldConstants.TILE_SIZE;

            // Check for slope
            int tileCenterX = Mathf.FloorToInt((x - roomWorldPixelX) / WorldConstants.TILE_SIZE);
            var tile = currentRoom.GetTileAtCoord(new Vector2Int(tileCenterX, tileY));
            if (tile != null && tile.IsSlopeSurface())
            {
                // Get ground height in room-local coordinates, then convert to world
                float localX = x - roomWorldPixelX;
                return tile.GetGroundHeight(localX) + roomWorldPixelY;
            }

            return tileTop;
        }

        /// <summary>
        /// Resolve upward collision - snap below ceiling.
        /// Converts world pixel coordinates to room-local tile coordinates.
        /// </summary>
        private float ResolveVerticalUp(float x, float fromY, float toY, float hw, float hh)
        {
            float headY = toY + hh;
            
            // Convert world pixel coordinates to room-local tile coordinates
            int tileY = Mathf.FloorToInt((headY - roomWorldPixelY) / WorldConstants.TILE_SIZE);
            
            // Tile bottom in world pixel coordinates
            float tileBottom = roomWorldPixelY + tileY * WorldConstants.TILE_SIZE;

            return tileBottom - hh;
        }

        /// <summary>
        /// Check if currently in water using two-height sampling (AS3: Unit.checkWater()).
        /// 25% of sprite height → partial submersion (isInWater / wading).
        /// 75% of sprite height → full submersion (isFullySubmerged / isPlav).
        /// </summary>
        private void CheckWater()
        {
            if (currentRoom == null)
            {
                isInWater = false;
                isFullySubmerged = false;
                return;
            }

            float height = isCrouching ? resolvedCrouchedCollisionHeight : collisionHeight;
            int tileX = Mathf.FloorToInt((posX - roomWorldPixelX) / WorldConstants.TILE_SIZE);

            // 25% height check — wading / partial submersion
            float lowSampleY = posY + height * 0.25f;
            int lowTileY = Mathf.FloorToInt((lowSampleY - roomWorldPixelY) / WorldConstants.TILE_SIZE);
            var lowTile = currentRoom.GetTileAtCoord(new Vector2Int(tileX, lowTileY));
            isInWater = lowTile != null && lowTile.hasWater;

            // 75% height check — fully submerged (AS3 isPlav)
            float highSampleY = posY + height * 0.75f;
            int highTileY = Mathf.FloorToInt((highSampleY - roomWorldPixelY) / WorldConstants.TILE_SIZE);
            var highTile = currentRoom.GetTileAtCoord(new Vector2Int(tileX, highTileY));
            isFullySubmerged = isInWater && highTile != null && highTile.hasWater;
        }

        /// <summary>
        /// Sync Unity transform from pixel position.
        /// </summary>
        private void SyncUnityPosition()
        {
            Vector2 transformPixelPos = ColliderPixelToTransformPixel(new Vector2(posX, posY));
            transform.position = new Vector3(
                transformPixelPos.x * PIX_TO_UNIT,
                transformPixelPos.y * PIX_TO_UNIT,
                transform.position.z
            );
        }

        private Vector2 TransformPixelToColliderPixel(Vector2 transformPixelPos)
        {
            return transformPixelPos + colliderOffsetPixels;
        }

        private Vector2 ColliderPixelToTransformPixel(Vector2 colliderPixelPos)
        {
            return colliderPixelPos - colliderOffsetPixels;
        }

        private Vector3 GetColliderFeetUnityPosition()
        {
            Vector2 colliderPixelPos = TransformPixelToColliderPixel(new Vector2(
                transform.position.x * UNIT_TO_PIX,
                transform.position.y * UNIT_TO_PIX));
            return new Vector3(
                colliderPixelPos.x * PIX_TO_UNIT,
                colliderPixelPos.y * PIX_TO_UNIT,
                transform.position.z
            );
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            // Draw collision box
            float hw = collisionWidth * 0.5f * PIX_TO_UNIT;
            float hh = collisionHeight * PIX_TO_UNIT;
            Vector3 colliderFeet = GetColliderFeetUnityPosition();
            Vector3 center = colliderFeet + new Vector3(0, hh * 0.5f, 0);
            Gizmos.color = isGrounded ? Color.green : Color.yellow;
            Gizmos.DrawWireCube(center, new Vector3(hw * 2, hh, 0));

            // Draw feet position
            Gizmos.color = Color.red;
            Gizmos.DrawSphere(colliderFeet, 0.05f);
            
            // Draw tile collision visualization
            DrawTileCollisionDebug();
        }
        
        /// <summary>
        /// Draw debug visualization of tile collision bounds.
        /// Shows purple squares for solid tiles in the physics area.
        /// </summary>
        private void DrawTileCollisionDebug()
        {
            if (currentRoom == null || currentRoom.tiles == null) return;

            // Draw tiles around the player position
            int tileX = Mathf.FloorToInt((posX - roomWorldPixelX) / WorldConstants.TILE_SIZE);
            int tileY = Mathf.FloorToInt((posY - roomWorldPixelY) / WorldConstants.TILE_SIZE);

            int radius = 5; // Draw 5 tiles in each direction

            const float platformDebugHeightPixels = 6f;

            for (int dx = -radius; dx <= radius; dx++)
            {
                for (int dy = -radius; dy <= radius; dy++)
                {
                    int tx = tileX + dx;
                    int ty = tileY + dy;

                    if (tx < 0 || tx >= currentRoom.width || ty < 0 || ty >= currentRoom.height)
                        continue;

                    var tile = currentRoom.GetTileAtCoord(new Vector2Int(tx, ty));
                    if (tile == null) continue;

                    Rect tileBounds = tile.GetBounds();
                    tileBounds.position += new Vector2(roomWorldPixelX, roomWorldPixelY);

                    // Color based on physics type
                    if (tile.physicsType == TilePhysicsType.Wall)
                    {
                        DrawDebugRect(tileBounds, new Color(0.6f, 0.2f, 0.8f, 0.5f), new Color(0.8f, 0.4f, 1f, 0.8f));
                    }
                    else if (tile.physicsType == TilePhysicsType.Platform)
                    {
                        Rect platformBounds = new Rect(
                            tileBounds.xMin,
                            tileBounds.yMax - platformDebugHeightPixels,
                            tileBounds.width,
                            platformDebugHeightPixels
                        );
                        DrawDebugRect(platformBounds, new Color(1f, 1f, 0f, 0.35f), new Color(1f, 1f, 0.4f, 0.8f));
                    }
                    else if (tile.physicsType == TilePhysicsType.Stair)
                    {
                        DrawDebugRect(tileBounds, new Color(0f, 0.5f, 1f, 0.25f), new Color(0.4f, 0.8f, 1f, 0.7f));
                    }
                    else if (tile.indestructible)
                    {
                        DrawDebugRect(tileBounds, Color.clear, new Color(0.5f, 0.5f, 0.5f, 0.4f));
                    }
                }
            }

            // Draw room origin marker
            Vector3 roomOrigin = new Vector3(roomWorldPixelX * PIX_TO_UNIT, roomWorldPixelY * PIX_TO_UNIT, 0);
            Gizmos.color = Color.cyan;
            Gizmos.DrawSphere(roomOrigin, 0.1f);

            // Draw border indicator
            if (currentRoom.borderOffset > 0)
            {
                int borderTiles = currentRoom.borderOffset;
                float borderPixelX = roomWorldPixelX + borderTiles * WorldConstants.TILE_SIZE;
                float borderPixelY = roomWorldPixelY + borderTiles * WorldConstants.TILE_SIZE;
                Vector3 borderStart = new Vector3(borderPixelX * PIX_TO_UNIT, borderPixelY * PIX_TO_UNIT, 0);
                Vector3 borderSize = new Vector3(
                    (currentRoom.width - 2 * borderTiles) * WorldConstants.TILE_SIZE * PIX_TO_UNIT,
                    (currentRoom.height - 2 * borderTiles) * WorldConstants.TILE_SIZE * PIX_TO_UNIT,
                    0
                );
                Gizmos.color = new Color(0f, 1f, 1f, 0.3f);
                Gizmos.DrawWireCube(borderStart + borderSize * 0.5f, borderSize);
            }
        }

        private void DrawDebugRect(Rect pixelRect, Color fillColor, Color outlineColor)
        {
            Vector3 center = WorldCoordinates.PixelToUnity(pixelRect.center);
            Vector3 size = WorldCoordinates.PixelToUnity(pixelRect.size);

            if (fillColor.a > 0f)
            {
                Gizmos.color = fillColor;
                Gizmos.DrawCube(center, size);
            }

            if (outlineColor.a > 0f)
            {
                Gizmos.color = outlineColor;
                Gizmos.DrawWireCube(center, size);
            }
        }
#endif
    }
}
