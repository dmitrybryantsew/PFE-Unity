using UnityEngine;
using PFE.Core;
using PFE.Systems.Map;
using PFE.Systems.Map.TileQuery;
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
    /// <summary>
    /// P0-1: exactly one motor per GameObject. Two instances both write
    /// <c>transform.position</c> every tick while only the first receives input,
    /// which desyncs dx/dy, currentRoom and the rendered position.
    /// See docs/Roadmap/01_P0_CRITICAL_DEFECT_FIXES.md (Defect 1).
    /// </summary>
    [DefaultExecutionOrder(-150)]
    [DisallowMultipleComponent]
    public class TilePhysicsController : MonoBehaviour, IMovementMotor, PFE.Core.ISimTickable
    {
        /// <summary>
        /// P1: runs after input is gathered and before everything downstream, so entities move
        /// against a settled room and triggers/damage see final positions.
        /// </summary>
        public int TickOrder => PFE.Core.SimTickOrder.PlayerMotor;

        /// <summary>
        /// One authoritative simulation step. Identical work to the legacy FixedUpdate body; only the
        /// <i>driver</i> and the per-step scaling change. See <see cref="SimDriven"/>.
        /// </summary>
        public void SimTick(int tickIndex)
        {
            StepMotor();
        }

        /// <summary>
        /// Hands this motor to the fixed-step simulation. After this the motor is driven by
        /// <see cref="PFE.Core.SimLoop"/> at the configured tick rate and
        /// <see cref="FixedUpdate"/> stands down.
        ///
        /// <para>Called by MapBridge, which already resolves the player's motor in order to connect
        /// it to the room. <b>Not</b> called means the legacy FixedUpdate path, which is the default,
        /// so this is a one-flag A/B rather than a silent behaviour change.</para>
        /// </summary>
        public void AttachSimulation(PFE.Core.SimClock clock, PFE.Core.SimLoop loop)
        {
            if (clock == null || loop == null)
            {
                Debug.LogWarning(
                    "[PFE] AttachSimulation called with a null clock or loop; staying on the " +
                    "legacy FixedUpdate path.", this);
                return;
            }

            simClock = clock;
            simLoop = loop;
            simAttached = true;
            simLoop.Register(this);
        }

        private void OnEnable()
        {
            // Re-register across a disable/enable cycle (pooling, room streaming) so the motor does
            // not silently stop ticking. Register is idempotent.
            if (simAttached && simLoop != null)
            {
                simLoop.Register(this);
            }
        }

        private void OnDestroy()
        {
            if (simAttached && simLoop != null)
            {
                simLoop.Unregister(this);
            }
        }

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

        // Jump force is deliberately NOT owned by this motor. The live constant is
        // PlayerLocomotionController.baseJumpForce (sourced from UnitDefinition.jumpForce,
        // AS3 jumpdy) and is handed to the motor through IMovementMotor.Jump().
        // A [SerializeField] float jumpForce = 10f lived here; it was never read anywhere
        // (only assigned), which raised CS0414. Removed rather than wired up, because wiring
        // it up would silently change jump height. If the motor should own jumpdy instead,
        // that is a design decision for the physics rewrite, not a warning cleanup.

        [Tooltip("Ground friction (brake in AS3, 0-1)")]
        [SerializeField] private float groundFriction = 0.7f;

        [Tooltip("Air friction")]
        [SerializeField] private float airFriction = 0.95f;

        [Tooltip("Gravity multiplier (grav in AS3)")]
        [SerializeField] private float gravityMult = 1.0f;

        [Header("Physics Constants")]
        [Tooltip("Global gravity (World.ddy in AS3)")]
        [SerializeField] private float globalGravity = 1.0f;

        [Tooltip("Platform pass-through threshold (porog in AS3)")]
        [SerializeField] private float platformThreshold = TileQueryConstants.PorogGrounded;

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
        private ITileQueryService tileQueryService;
        [SerializeField] private PfeDebugSettings debugSettings;
        
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

        // ── Simulation driving (P1) ──────────────────────────────────────────
        // The motor is driven either by SimLoop at the configured tick rate ("sim-driven"), or by
        // Unity's FixedUpdate ("legacy"). Legacy stays the default so the migration can be A/B'd on
        // one flag — see PfeDebugSettings.SimTickMotor. With the flag off, every expression below
        // evaluates to exactly what it did before, so the legacy path is unchanged by construction.
        private PFE.Core.SimClock simClock;
        private PFE.Core.SimLoop simLoop;
        private bool simAttached;

        /// <summary>True when SimLoop owns the step and FixedUpdate must stand down.</summary>
        private bool SimDriven => simAttached && simClock != null;

        /// <summary>
        /// Canonical 30 Hz frames of <b>position</b> advanced by a single step.
        ///
        /// <para>Legacy: <c>Time.fixedDeltaTime * 60f</c>, i.e. 1.2 at the default 50 Hz FixedUpdate.
        /// That is the 2x-too-fast bug — 1.2 px per step at 50 steps/s is 60 px/s, against AS3's
        /// 30 px/s. It is preserved verbatim here so the legacy path is bit-identical.</para>
        ///
        /// <para>Sim: <see cref="PFE.Core.SimClock.StepScale"/> = 30/rate, so exactly 1.0 at 30 Hz
        /// (one step is one AS3 frame) and 0.5 at 60 Hz.</para>
        /// </summary>
        private float PositionFramesPerStep => TilePhysicsStepMath.PositionFramesPerStep(
            SimDriven,
            UnityEngine.Time.fixedDeltaTime,
            simClock != null ? simClock.StepScale : 1f);

        /// <summary>
        /// Canonical 30 Hz frames of <b>rate accumulation</b> (gravity, acceleration, friction decay)
        /// applied by a single step.
        ///
        /// <para>Legacy: exactly <c>1f</c>. Today these are applied once per FixedUpdate with no
        /// scaling at all, so at 50 Hz gravity accumulates 50 times a second instead of 30 — 1.67x too
        /// strong, on top of the 2x velocity error above. The two do not cancel. Again preserved
        /// verbatim so the A/B measures a real difference rather than a mixed one.</para>
        ///
        /// <para>Sim: <see cref="PFE.Core.SimClock.StepScale"/>. Semi-implicit Euler with a smaller
        /// step is not the same trajectory as AS3's h=1, but it is the correct fixed-step form: rates
        /// advance proportionally to the time the step covers.</para>
        /// </summary>
        private float RateFramesPerStep => TilePhysicsStepMath.RateFramesPerStep(
            SimDriven,
            simClock != null ? simClock.StepScale : 1f);

        /// <summary>
        /// Wall-clock seconds covered by a single step. Timers stored in seconds
        /// (<c>dashTimer</c>, <c>platformDropTimer</c>) decrement by this so their duration stops
        /// depending on the tick rate.
        /// </summary>
        private float StepSeconds => TilePhysicsStepMath.StepSeconds(
            SimDriven,
            UnityEngine.Time.fixedDeltaTime,
            simClock != null ? simClock.SimDt : 0f);

        /// <summary>
        /// Re-expresses a per-canonical-frame multiplicative decay over this step's frames.
        ///
        /// <para>AS3 applies <c>dx *= brake</c> exactly once per 30 Hz frame. Applying it once per
        /// step at a different tick rate would decay too fast (or too slow), because the number of
        /// steps per second changed. Over <c>h</c> frames the correct factor is <c>brake^h</c>.</para>
        ///
        /// <para>Returns the factor untouched when <c>h == 1</c>, which is always true on the legacy
        /// path — so legacy behaviour is unchanged by construction.</para>
        /// </summary>
        private float DecayOverStep(float perFrameFactor)
        {
            return TilePhysicsStepMath.DecayOverStep(perFrameFactor, RateFramesPerStep);
        }

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
        public RoomInstance CurrentRoom => currentRoom;
        public ITileQueryService TileQuery => tileQueryService;
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
                // Calculate room's world pixel position
                // This accounts for land position and border offset
                int borderOffsetTiles = room.borderOffset;
                roomWorldPixelX = room.landPosition.x * WorldConstants.ROOM_WIDTH * WorldConstants.TILE_SIZE
                                  - borderOffsetTiles * WorldConstants.TILE_SIZE;
                roomWorldPixelY = room.landPosition.y * WorldConstants.ROOM_HEIGHT * WorldConstants.TILE_SIZE
                                  - borderOffsetTiles * WorldConstants.TILE_SIZE;

                ITileQueryService unified = new UnifiedTileQueryService(room);
                var settings = debugSettings ?? Resources.Load<PfeDebugSettings>("PfeDebugSettings");
                if (settings != null && settings.TileQueryLogDivergence)
                {
                    ITileQueryService legacy = new GridTileQuery(room);
                    tileQueryService = new TileQueryDivergenceLogger(primary: unified, shadow: legacy, debugSettings: settings);
                }
                else
                {
                    tileQueryService = unified;
                }
            }
            else
            {
                tileQueryService = null;
                roomWorldPixelX = 0;
                roomWorldPixelY = 0;
            }
        }

        public void SetDebugSettings(PfeDebugSettings settings)
        {
            debugSettings = settings;
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
            if (currentRoom == null) return false;

            // Build AABB at target in world pixel space
            Rect targetBounds = new Rect(
                targetPixelX - halfWidth,
                targetPixelY,
                halfWidth * 2f,
                halfHeight * 2f);

            return !TileCollisionMath.CheckCollision(
                currentRoom,
                targetBounds,
                roomWorldPixelX,
                roomWorldPixelY,
                platformThreshold: TileQueryConstants.PorogGrounded,
                isTransparent: false,
                canFallThroughPlatforms: true,
                velocityY: 0f);
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

        /// <summary>
        /// P0-1 guard: <see cref="DisallowMultipleComponent"/> stops *new* duplicates but does
        /// not repair existing ones, so assert at load time. Editor/development only — this is
        /// an authoring-fault check, not a runtime path. Not gated on PfeDebugSettings because
        /// a duplicate motor is a hard data fault, not an informational log.
        /// </summary>
        private void Awake()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            TilePhysicsController[] motors = GetComponents<TilePhysicsController>();
            if (motors != null && motors.Length > 1)
            {
                Debug.LogError(
                    "[PFE] '" + name + "' has " + motors.Length + " TilePhysicsController " +
                    "components. This desyncs motor state (dx/dy, currentRoom, pixel position). " +
                    "Exactly one is required.", this);
            }
#endif
        }

        /// <summary>
        /// P0-2: reposition the motor after a room transition.
        ///
        /// Order matters: <see cref="SetRoom"/> MUST run first because it refreshes
        /// <c>currentRoom</c> and <c>roomWorldPixelX/Y</c>. SetUnityPosition and every
        /// subsequent tick resolve tiles against currentRoom, so a stale room means
        /// colliding against the old grid at the new coordinates.
        ///
        /// Velocity is cleared because AS3 rebuilds the unit on every location change
        /// — dx/dy never carry across a door. Preserving them would launch the player
        /// through the new room at the old room's exit speed.
        /// </summary>
        public void RepositionForRoom(RoomInstance room, Vector3 worldPos)
        {
            SetRoom(room);
            SetUnityPosition(worldPos);

            dx = 0f;
            dy = 0f;
            dashTimer = 0f;
            platformDropTimer = 0f;

            // If the spawn position sits directly on a ladder, automatically attach so the player
            // continues climbing without a 1-frame gravity drop or detaching across room boundaries.
            if (TryGetLadderContactAt(posX, posY, collisionHeight, out LadderContact ladderContact))
            {
                isOnLadder = true;
                SnapToLadder(ladderContact);
                isGrounded = false;
                isOnPlatform = false;
            }
            else
            {
                isOnLadder = false;
            }
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
            // Standing down when SimLoop owns the step is essential: leaving both mechanisms active
            // is the classic double-step bug. See SimDriven.
            if (SimDriven)
            {
                return;
            }

            StepMotor();
        }

        /// <summary>
        /// One motor step, shared by the legacy FixedUpdate path and the sim-driven
        /// <see cref="SimTick"/> path. Extracted so the two drivers cannot drift apart.
        /// </summary>
        private void StepMotor()
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
                dx *= DecayOverStep(0.8f);
                dy *= DecayOverStep(0.8f);
            }
            else if (isInWater)
            {
                dx *= DecayOverStep(0.5f);
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

            // 10. Check room boundary exit (AS3 Unit.as outLoc)
            CheckRoomBoundaryExit();

            // Decrement by the step's wall-clock duration, not by Unity's fixed delta, so dash and
            // platform-drop last the same number of real seconds at every tick rate. In legacy mode
            // StepSeconds IS Time.fixedDeltaTime, so this is unchanged.
            dashTimer = Mathf.Max(0f, dashTimer - StepSeconds);
            platformDropTimer = Mathf.Max(0f, platformDropTimer - StepSeconds);
            if (previousDashTimer > 0f && dashTimer <= 0f)
            {
                dx *= 0.3f;
                dy *= 0.3f;
            }
        }

        /// <summary>
        /// Check if the unit has crossed the room boundary (open corridor / border passage).
        /// Port of AS3 Unit.as:2064-2083 (outLoc).
        /// </summary>
        private void CheckRoomBoundaryExit()
        {
            if (currentRoom == null) return;

            float localPixelX = posX - roomWorldPixelX;
            float localPixelY = posY - roomWorldPixelY;
            float roomWidthPixels = currentRoom.width * WorldConstants.TILE_SIZE;
            float roomHeightPixels = currentRoom.height * WorldConstants.TILE_SIZE;

            var transitionManager = PFE.Systems.Map.Streaming.RoomTransitionManager.Instance;

            // Right boundary (direction 2)
            if (localPixelX >= roomWidthPixels)
            {
                bool transitioned = transitionManager != null && transitionManager.TransitionThroughEdge(2, gameObject);
                if (!transitioned)
                {
                    posX = roomWorldPixelX + roomWidthPixels;
                    SyncUnityPosition();
                }
            }
            // Left boundary (direction 1)
            else if (localPixelX < 0f)
            {
                bool transitioned = transitionManager != null && transitionManager.TransitionThroughEdge(1, gameObject);
                if (!transitioned)
                {
                    posX = roomWorldPixelX;
                    SyncUnityPosition();
                }
            }
            // Bottom boundary (direction 3)
            else if (localPixelY < 0f)
            {
                bool transitioned = transitionManager != null && transitionManager.TransitionThroughEdge(3, gameObject);
                if (!transitioned)
                {
                    posY = roomWorldPixelY;
                    SyncUnityPosition();
                }
            }
            // Top boundary (direction 4)
            else if (localPixelY >= roomHeightPixels)
            {
                bool transitioned = transitionManager != null && transitionManager.TransitionThroughEdge(4, gameObject);
                if (!transitioned)
                {
                    posY = roomWorldPixelY + roomHeightPixels;
                    SyncUnityPosition();
                }
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
                // accel is px per canonical frame, so the delta applied over one step is accel * h.
                // ladderClimbSpeed is a target velocity (px/frame) and is assigned, not accumulated,
                // so it needs no scaling.
                dx = Mathf.MoveTowards(dx, 0f, acceleration * RateFramesPerStep);
                dy = Mathf.Abs(ladderInputY) > 0.1f
                    ? ladderInputY * ladderClimbSpeed
                    : 0f;
                isGrounded = false;
                isOnPlatform = false;
                return;
            }

            float accelerationRate = isGrounded ? acceleration : acceleration * 0.35f;
            dx = Mathf.MoveTowards(dx, desiredHorizontalSpeed, accelerationRate * RateFramesPerStep);
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
                // AS3 applies World.ddy once per 30 Hz frame, so accumulate proportionally to the
                // frames this step covers. RateFramesPerStep is exactly 1f on the legacy path.
                dy -= globalGravity * gravityMult * gravityScale * RateFramesPerStep;
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
                dx *= DecayOverStep(friction);
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

            // dx/dy are canonical pixels per 30 Hz frame (AS3 maxdx=8, maxdy=20). Advance by the
            // number of canonical frames this step covers. Legacy is the 2x bug; sim is correct.
            float moveX = dx * PositionFramesPerStep;
            float moveY = dy * PositionFramesPerStep;

            if (isOnLadder)
            {
                MoveOnLadder(moveY);
                return;
            }

            float maxDistance = Mathf.Max(Mathf.Abs(moveX), Mathf.Abs(moveY));
            int subSteps = TileCollisionMath.CalculateSubSteps(maxDistance, maxSubStepDistance);
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
            int subSteps = TileCollisionMath.CalculateSubSteps(Mathf.Abs(moveY), maxSubStepDistance);
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
            return TileCollisionMath.HasSolidOverlapAt(currentRoom, x, y, hw, hh, roomWorldPixelX, roomWorldPixelY);
        }

        /// <summary>
        /// Check if position collides with solid tiles.
        /// Bounds: center at (x, y + hh/2), size (hw*2, hh)
        /// Converts world pixel coordinates to room-local tile coordinates.
        /// </summary>
        private bool CheckTileCollisionAt(float x, float y, float hw, float hh)
        {
            return TileCollisionMath.CheckTileCollisionAt(currentRoom, x, y, hw, hh, roomWorldPixelX, roomWorldPixelY);
        }

        /// <summary>
        /// Check ground collision (walls and platforms below feet).
        /// Converts world pixel coordinates to room-local tile coordinates.
        /// </summary>
        private bool CheckGroundCollisionAt(float x, float y, float hw)
        {
            // Don't fall through platforms when pressing down
            bool canFallThrough = inputDown ||
                platformDropTimer > 0f ||
                (isOnLadder && ladderInputY < -0.1f);

            bool hitPlatform;
            bool hit = TileCollisionMath.CheckGroundCollisionAt(
                currentRoom, x, y, hw,
                roomWorldPixelX, roomWorldPixelY,
                platformThreshold,
                canFallThrough,
                out hitPlatform);

            if (hitPlatform)
            {
                isOnPlatform = true;
            }

            return hit;
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
            return TileCollisionMath.CheckCeilingCollisionAt(currentRoom, x, y, hw, hh, roomWorldPixelX, roomWorldPixelY, isOnLadder);
        }

        /// <summary>
        /// Resolve horizontal collision by binary searching for valid X.
        /// </summary>
        private float ResolveHorizontal(float fromX, float toX, float y, float hw, float hh)
        {
            return TileCollisionMath.ResolveHorizontal(currentRoom, fromX, toX, y, hw, hh, roomWorldPixelX, roomWorldPixelY);
        }

        /// <summary>
        /// Resolve downward collision - snap to ground surface.
        /// Converts world pixel coordinates to room-local tile coordinates.
        /// </summary>
        private float ResolveVerticalDown(float x, float fromY, float toY, float hw)
        {
            return TileCollisionMath.ResolveVerticalDown(currentRoom, x, fromY, toY, hw, roomWorldPixelX, roomWorldPixelY);
        }

        /// <summary>
        /// Resolve upward collision - snap below ceiling.
        /// Converts world pixel coordinates to room-local tile coordinates.
        /// </summary>
        private float ResolveVerticalUp(float x, float fromY, float toY, float hw, float hh)
        {
            return TileCollisionMath.ResolveVerticalUp(currentRoom, x, fromY, toY, hw, hh, roomWorldPixelX, roomWorldPixelY);
        }

        /// <summary>
        /// Check if currently in water using two-height sampling (AS3: Unit.checkWater()).
        /// 25% of sprite height → partial submersion (isInWater / wading).
        /// 75% of sprite height → full submersion (isFullySubmerged / isPlav).
        /// </summary>
        private void CheckWater()
        {
            float height = isCrouching ? resolvedCrouchedCollisionHeight : collisionHeight;
            TileCollisionMath.CheckWater(
                currentRoom, posX, posY, height,
                roomWorldPixelX, roomWorldPixelY,
                out isInWater, out isFullySubmerged);
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
