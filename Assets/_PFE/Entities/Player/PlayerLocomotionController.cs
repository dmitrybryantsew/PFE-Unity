using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Map;
using PFE.Systems.Physics;
using UnityEngine;

namespace PFE.Entities.Player
{
    /// <summary>
    /// High-level player locomotion brain.
    /// Reads intent from PlayerController, applies movement rules, and drives the low-level motor.
    /// Queries ILocomotionAbilities for perk-modified capabilities (double jump, levitation, etc.).
    /// </summary>
    [DefaultExecutionOrder(-200)]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(UnitController))]
    [RequireComponent(typeof(TilePhysicsController))]
    public sealed class PlayerLocomotionController : MonoBehaviour
    {
        private const float DefaultRunMultiplier = 2f;
        private const float DefaultSwimSpeedMultiplier = 0.75f;
        private const float DefaultCrouchSpeedMultiplier = 0.5f;
        private const float DefaultDashDurationSeconds = 0.14f;
        private const float DefaultDashCooldownSeconds = 0.4f;
        private const float DefaultDashSpeedMultiplier = 2.6f;
        private const float DefaultJumpCutGravityScale = 1.8f;
        private const float DefaultJumpHoldGravityScale = 0.75f;
        private const float DefaultFallGravityScale = 1.25f;
        private const float DefaultSwimGravityScale = 0.35f;
        private const float DefaultJumpBufferSeconds = 0.12f;
        private const float DefaultCoyoteTimeSeconds = 0.1f;
        private const float DefaultLevitationHorizontalSpeedMultiplier = 0.7f;
        // AS3: visualTele plays for 12 frames at 60fps = 0.2s; origin sparks last up to 0.5s.
        // We freeze the locomotion state for the appearance effect duration only.
        private const float DefaultTeleportFreezeDuration = 0.2f;

        [Header("Locomotion Tuning")]
        [SerializeField] private bool canDash = true;
        [SerializeField] private bool canCrouch = true;
        [SerializeField] private float dashDurationSeconds = DefaultDashDurationSeconds;
        [SerializeField] private float dashCooldownSeconds = DefaultDashCooldownSeconds;
        [SerializeField] private float dashSpeedMultiplier = DefaultDashSpeedMultiplier;
        [SerializeField] private float crouchSpeedMultiplier = DefaultCrouchSpeedMultiplier;
        [SerializeField] private float swimSpeedMultiplier = DefaultSwimSpeedMultiplier;
        [SerializeField] private float jumpBufferSeconds = DefaultJumpBufferSeconds;
        [SerializeField] private float coyoteTimeSeconds = DefaultCoyoteTimeSeconds;
        [SerializeField] private float jumpHoldGravityScale = DefaultJumpHoldGravityScale;
        [SerializeField] private float jumpCutGravityScale = DefaultJumpCutGravityScale;
        [SerializeField] private float fallGravityScale = DefaultFallGravityScale;
        [SerializeField] private float swimGravityScale = DefaultSwimGravityScale;

        private IMovementMotor _motor;
        private UnitDefinition _definition;
        private ILocomotionAbilities _abilities;
        private UnitStats _unitStats;

        private Vector2 _moveInput;
        private bool _jumpHeld;
        private bool _runHeld;
        private bool _dashPressedBuffered;
        private bool _dropThroughPressedBuffered;
        private float _aimAngle;

        private float _jumpBufferTimer;
        private float _coyoteTimer;
        private float _dashCooldownTimer;
        private float _dashTimer;

        private bool _isCrouching;
        private int _airJumpCount;
        private bool _justDoubleJumped;
        private bool _wasGroundedLastFrame;
        private bool _justLanded;

        // Levitation state
        private bool _isLevitating;
        private float _levitationStartY;
        private float _levitationVerticalSpeed;

        // Teleport state (AS3: charge-based, hold Q to charge, release to execute at cursor)
        private bool _teleportHeld;
        private float _teleportChargeTimer;
        private float _teleportCooldownTimer;
        private float _teleportFreezeTimer;
        private bool _justTeleported;
        private Vector2 _cursorWorldPixels;

        private PlayerMovementSnapshot _currentSnapshot;

        public PlayerMovementSnapshot CurrentSnapshot => _currentSnapshot;

        public bool IsDashing => _dashTimer > 0f;
        public bool IsCrouching => _isCrouching;
        public bool IsLevitating => _isLevitating;
        public bool IsTeleporting => _teleportFreezeTimer > 0f;
        private bool CanSwim => _definition == null || _definition.canSwim;

        private void Awake()
        {
            _motor = GetComponent<IMovementMotor>();
            if (_motor == null)
            {
                Debug.LogError("[PlayerLocomotionController] No IMovementMotor implementation found on this GameObject.");
            }

            UnitController unitController = GetComponent<UnitController>();
            _definition = unitController != null ? unitController.Stats : null;

            _abilities = GetComponent<ILocomotionAbilities>();
            if (_abilities == null)
            {
                Debug.LogWarning("[PlayerLocomotionController] No ILocomotionAbilities found. Ability-based movement (double jump, levitation) disabled.");
            }
        }

        /// <summary>
        /// Provide UnitStats reference for mana queries. Called by PlayerController after stats are initialized.
        /// </summary>
        public void SetUnitStats(UnitStats stats)
        {
            _unitStats = stats;
        }

        /// <summary>
        /// Set teleport key hold state. AS3: hold to charge, release to execute.
        /// </summary>
        public void SetTeleportHeld(bool held)
        {
            _teleportHeld = held;
        }

        /// <summary>
        /// Provide mouse cursor position in world pixel space for teleport targeting.
        /// AS3: World.w.celX / World.w.celY
        /// </summary>
        public void SetCursorWorldPixels(Vector2 cursorPixels)
        {
            _cursorWorldPixels = cursorPixels;
        }

        public void SetIntent(Vector2 moveInput, bool jumpHeld, bool runHeld, bool jumpPressed, bool dashPressed, bool dropThroughPressed, float aimAngle)
        {
            _moveInput = moveInput;
            _jumpHeld = jumpHeld;
            _runHeld = runHeld;
            _aimAngle = aimAngle;

            if (jumpPressed)
            {
                _jumpBufferTimer = jumpBufferSeconds;
            }

            if (dashPressed)
            {
                _dashPressedBuffered = true;
            }

            if (dropThroughPressed)
            {
                _dropThroughPressedBuffered = true;
            }
        }

        private void FixedUpdate()
        {
            if (_motor == null)
            {
                return;
            }

            float dt = Time.fixedDeltaTime;
            MovementMotorState state = _motor.State;

            // Track landing
            _justLanded = !_wasGroundedLastFrame && state.IsGrounded;

            UpdateTimers(state, dt);
            bool wantsToUseLadder = !state.IsInWater && !_isLevitating && Mathf.Abs(_moveInput.y) > 0.5f;
            _motor.SetLadderInput(_moveInput.y, wantsToUseLadder);

            _isCrouching = canCrouch &&
                state.IsGrounded &&
                !state.IsInWater &&
                !state.IsOnLadder &&
                _moveInput.y < -0.5f;

            // Levitation update (before gravity/speed resolve so state is current)
            UpdateLevitation(state, dt);

            float targetSpeed = ResolveTargetHorizontalSpeed(state);
            _motor.SetDesiredHorizontalSpeed(targetSpeed);
            _motor.SetCrouching(_isCrouching);
            _motor.SetDropThroughPlatforms(_dropThroughPressedBuffered);
            _motor.SetGravityScale(ResolveGravityScale(state));

            // Apply levitation vertical speed
            if (_isLevitating)
            {
                float verticalSpeed = ResolveLevitationVerticalSpeed(state, dt);
                _motor.SetDesiredVerticalSpeed(verticalSpeed);
            }
            // Vertical swim control (AS3: plavdy = acceleration * 0.5)
            // Only units with canSwim can actively swim; others sink normally.
            else if (state.IsFullySubmerged && CanSwim)
            {
                float swimVerticalSpeed = ResolveSwimVerticalSpeed(state);
                if (Mathf.Abs(swimVerticalSpeed) > 0.01f)
                {
                    _motor.SetDesiredVerticalSpeed(swimVerticalSpeed);
                }
            }

            _justDoubleJumped = false;
            _justTeleported = false;
            TryConsumeJump(state);
            TryConsumeDash(state);
            TryConsumeTeleport(state);

            MovementMotorState latestState = _motor.State;
            PlayerLocomotionState locomotionState = ResolveLocomotionState(latestState);

            float normalizedMoveSpeed = ResolveNormalizedMoveSpeed(latestState);

            _currentSnapshot = new PlayerMovementSnapshot(
                locomotionState,
                latestState.Velocity,
                latestState.Position,
                latestState.IsGrounded,
                latestState.IsInWater,
                latestState.IsFullySubmerged,
                latestState.IsOnLadder,
                IsDashing,
                latestState.IsCrouching,
                ResolveRunningState(latestState),
                latestState.FacingDirection,
                _aimAngle,
                _justDoubleJumped,
                _justLanded,
                normalizedMoveSpeed,
                _isLevitating,
                _justTeleported);

            _wasGroundedLastFrame = latestState.IsGrounded;
            _dashPressedBuffered = false;
            _dropThroughPressedBuffered = false;
        }

        private void UpdateTimers(MovementMotorState state, float dt)
        {
            if (state.IsGrounded)
            {
                _coyoteTimer = coyoteTimeSeconds;
                _airJumpCount = 0;
            }
            else
            {
                _coyoteTimer = Mathf.Max(0f, _coyoteTimer - dt);
            }

            _jumpBufferTimer = Mathf.Max(0f, _jumpBufferTimer - dt);
            _dashCooldownTimer = Mathf.Max(0f, _dashCooldownTimer - dt);
            _dashTimer = Mathf.Max(0f, _dashTimer - dt);
            _teleportCooldownTimer = Mathf.Max(0f, _teleportCooldownTimer - dt);
            _teleportFreezeTimer = Mathf.Max(0f, _teleportFreezeTimer - dt);
        }

        // ──────────────────────────────────────
        //  Levitation
        // ──────────────────────────────────────

        private void UpdateLevitation(MovementMotorState state, float dt)
        {
            if (_isLevitating)
            {
                // Cancel conditions: released jump, grounded, in water, on ladder, dashing
                if (!_jumpHeld || state.IsGrounded || state.IsInWater || state.IsOnLadder || IsDashing)
                {
                    EndLevitation();
                    return;
                }

                // Mana drain
                if (!TryDrainLevitationMana(state, dt))
                {
                    EndLevitation();
                    return;
                }
            }
            else
            {
                // Activation: hold jump while airborne + not grounded + not on ladder + not in water + not dashing
                // Only activate while falling (not during jump rise, to let normal jump complete first)
                if (_abilities != null && _abilities.CanLevitate
                    && _jumpHeld
                    && !state.IsGrounded
                    && !state.IsOnLadder
                    && !state.IsInWater
                    && !IsDashing
                    && state.Velocity.y <= 0.05f)
                {
                    if (HasManaForLevitation())
                    {
                        BeginLevitation(state);
                    }
                }
            }
        }

        private void BeginLevitation(MovementMotorState state)
        {
            _isLevitating = true;
            _levitationStartY = state.Position.y;
            _levitationVerticalSpeed = 0f;
        }

        private void EndLevitation()
        {
            _isLevitating = false;
            _levitationVerticalSpeed = 0f;
        }

        private bool HasManaForLevitation()
        {
            if (_abilities is PlayerLocomotionAbilities pla && pla.InfiniteMana) return true;
            if (_unitStats == null) return false;
            return _unitStats.Mana.Value > 0f;
        }

        private bool TryDrainLevitationMana(MovementMotorState state, float dt)
        {
            if (_abilities is PlayerLocomotionAbilities pla && pla.InfiniteMana) return true;
            if (_unitStats == null) return false;
            if (_abilities == null) return false;

            float cost = _abilities.LevitationManaCostPerTick;

            // Extra cost when ascending
            if (state.Velocity.y > 0.05f)
            {
                cost += _abilities.LevitationManaCostUpward;
            }

            // Scale by dt so cost is per-second regardless of fixed timestep
            cost *= dt;

            if (_unitStats.Mana.Value < cost)
            {
                return false;
            }

            _unitStats.Mana.Value -= cost;
            return true;
        }

        private float ResolveLevitationVerticalSpeed(MovementMotorState state, float dt)
        {
            if (_abilities == null) return 0f;

            float accel = _abilities.LevitationAcceleration;
            float maxHeight = _abilities.LevitationMaxHeight;
            float verticalInput = _moveInput.y; // -1 to 1

            // Height cap: prevent ascending beyond max height from levitation start
            float currentHeight = state.Position.y - _levitationStartY;
            if (currentHeight >= maxHeight && verticalInput > 0f)
            {
                verticalInput = 0f;
            }

            // Ceiling: don't push upward when head is against a solid tile
            if (state.HitCeiling && verticalInput > 0f)
            {
                verticalInput = 0f;
            }

            // Match horizontal walk speed for consistent feel
            float walkSpeed = _definition != null ? _definition.WalkSpeed : 4f;
            float targetVerticalSpeed = verticalInput * walkSpeed;

            // Smooth acceleration toward target (same feel as horizontal movement)
            _levitationVerticalSpeed = Mathf.MoveTowards(
                _levitationVerticalSpeed,
                targetVerticalSpeed,
                accel * dt * 60f);

            return _levitationVerticalSpeed;
        }

        // ──────────────────────────────────────
        //  Swimming
        // ──────────────────────────────────────

        /// <summary>
        /// Resolve vertical swim speed when fully submerged.
        /// AS3: plavdy = acceleration * 0.5, up = -plavdy, down = +plavdy/2.
        /// Jump/up key swims up, down key swims down at half speed.
        /// </summary>
        private float ResolveSwimVerticalSpeed(MovementMotorState state)
        {
            float walkSpeed = _definition != null ? _definition.WalkSpeed : 4f;
            float swimUpSpeed = walkSpeed * 0.5f;

            // Up input or jump held → swim upward
            if (_moveInput.y > 0.5f || _jumpHeld)
            {
                return swimUpSpeed;
            }

            // Down input → swim downward at half speed
            if (_moveInput.y < -0.5f)
            {
                return -swimUpSpeed * 0.5f;
            }

            return 0f;
        }

        // ──────────────────────────────────────
        //  Horizontal speed
        // ──────────────────────────────────────

        private float ResolveTargetHorizontalSpeed(MovementMotorState state)
        {
            if (IsTeleporting)
            {
                return 0f;
            }

            float walkSpeed = _definition != null ? _definition.WalkSpeed : 4f;
            float speed = walkSpeed;

            if (state.IsOnLadder)
            {
                return 0f;
            }

            if (_isLevitating)
            {
                speed *= DefaultLevitationHorizontalSpeedMultiplier;
            }
            else if (_isCrouching)
            {
                speed *= Mathf.Clamp(crouchSpeedMultiplier, 0.1f, 1f);
            }
            else if (state.IsInWater)
            {
                speed *= Mathf.Clamp(swimSpeedMultiplier, 0.1f, 1f);
            }
            else if (_runHeld)
            {
                speed = _definition != null ? _definition.RunSpeed : walkSpeed * DefaultRunMultiplier;
            }

            // Apply ability multiplier
            if (_abilities != null)
            {
                speed *= _abilities.MoveSpeedMultiplier;
            }

            return _moveInput.x * speed;
        }

        // ──────────────────────────────────────
        //  Gravity
        // ──────────────────────────────────────

        private float ResolveGravityScale(MovementMotorState state)
        {
            if (IsTeleporting)
            {
                return 0f;
            }

            if (state.IsOnLadder)
            {
                return 0f;
            }

            if (state.IsInWater)
            {
                return swimGravityScale;
            }

            if (IsDashing)
            {
                return 0.6f;
            }

            if (_isLevitating)
            {
                return 0f;
            }

            if (!state.IsGrounded)
            {
                if (state.Velocity.y > 0.05f)
                {
                    return _jumpHeld ? jumpHoldGravityScale : jumpCutGravityScale;
                }

                return fallGravityScale;
            }

            return 1f;
        }

        // ──────────────────────────────────────
        //  Jump
        // ──────────────────────────────────────

        private void TryConsumeJump(MovementMotorState state)
        {
            if (_jumpBufferTimer <= 0f)
            {
                return;
            }

            // No jumping out of levitation — releasing jump ends it, re-pressing shouldn't re-jump
            if (_isLevitating)
            {
                return;
            }

            // Fully submerged swimmers: can't normal-jump, vertical swim control handles upward movement.
            // Wading (IsInWater but not fully submerged) still allows jumping out of water.
            // Non-swimmers can still attempt to jump while submerged.
            if (state.IsFullySubmerged && CanSwim)
            {
                _jumpBufferTimer = 0f;
                return;
            }

            if (state.IsOnLadder)
            {
                float ladderJumpForce = _definition != null ? _definition.JumpForce : 15f;
                _motor.Jump(ladderJumpForce);
                _jumpBufferTimer = 0f;
                _coyoteTimer = 0f;
                return;
            }

            float baseJumpForce = _definition != null ? _definition.JumpForce : 15f;
            float jumpForceMultiplier = _abilities != null ? _abilities.JumpForceMultiplier : 1f;

            // Ground jump (includes coyote time)
            bool canUseGroundJump = state.IsGrounded || _coyoteTimer > 0f;
            if (canUseGroundJump)
            {
                _motor.Jump(baseJumpForce * jumpForceMultiplier);
                _jumpBufferTimer = 0f;
                _coyoteTimer = 0f;
                return;
            }

            // Air jump (double jump)
            if (_abilities != null && _abilities.CanDoubleJump)
            {
                int maxAirJumps = _abilities.MaxJumpCount - 1; // MaxJumpCount includes ground jump
                if (_airJumpCount < maxAirJumps)
                {
                    float airJumpRatio = (_abilities is PlayerLocomotionAbilities pla) ? pla.AirJumpForceRatio : 0.8f;
                    float airJumpForce = baseJumpForce * jumpForceMultiplier * airJumpRatio;
                    _motor.Jump(airJumpForce);
                    _airJumpCount++;
                    _jumpBufferTimer = 0f;
                    _justDoubleJumped = true;
                }
            }
        }

        // ──────────────────────────────────────
        //  Dash
        // ──────────────────────────────────────

        private void TryConsumeDash(MovementMotorState state)
        {
            if (!_dashPressedBuffered || !canDash || _dashCooldownTimer > 0f || IsDashing)
            {
                return;
            }

            if (!state.IsGrounded || state.IsOnLadder)
            {
                return;
            }

            float directionX = Mathf.Abs(_moveInput.x) > 0.1f ? Mathf.Sign(_moveInput.x) : state.FacingDirection;
            Vector2 dashDirection = new Vector2(directionX, 0f);
            if (dashDirection.sqrMagnitude <= 0f)
            {
                dashDirection = Vector2.right;
            }

            float baseSpeed = _definition != null
                ? _definition.RunSpeed
                : 4f * DefaultRunMultiplier;
            float dashSpeed = baseSpeed * dashSpeedMultiplier;
            _motor.Dash(dashDirection, dashSpeed, dashDurationSeconds);
            _dashTimer = dashDurationSeconds;
            _dashCooldownTimer = dashCooldownSeconds;
        }

        // ──────────────────────────────────────
        //  Teleport
        // ──────────────────────────────────────

        /// <summary>
        /// AS3 teleport: hold Q to charge (portTime frames), release to blink to mouse cursor.
        /// No range limit — can teleport anywhere on the revealed map if the destination is clear.
        /// Target is snapped to tile grid (AS3: Math.round(celX / tileX) * tileX).
        /// </summary>
        private void TryConsumeTeleport(MovementMotorState state)
        {
            if (_abilities == null || !_abilities.CanTeleport) return;
            if (IsTeleporting) return;

            if (_teleportHeld)
            {
                // Accumulate charge while key is held (AS3: t_port++)
                // Only charge if cooldown is done and preconditions met
                if (_teleportCooldownTimer > 0f || IsDashing || state.IsOnLadder)
                {
                    _teleportChargeTimer = 0f;
                    return;
                }

                // Mana pre-check (don't charge if we can't afford it)
                if (!HasManaForTeleport())
                {
                    _teleportChargeTimer = 0f;
                    return;
                }

                _teleportChargeTimer += Time.fixedDeltaTime;
                return;
            }

            // Key was released — check if charge is sufficient
            float chargedTime = _teleportChargeTimer;
            _teleportChargeTimer = 0f;

            if (chargedTime < _abilities.TeleportChargeTimeSeconds) return;
            if (_teleportCooldownTimer > 0f) return;
            if (IsDashing || state.IsOnLadder) return;
            if (!HasManaForTeleport()) return;

            // Snap cursor position to tile grid (AS3: Math.round(celX / tileX) * tileX)
            float tileSize = WorldConstants.TILE_SIZE;
            float targetX = Mathf.Round(_cursorWorldPixels.x / tileSize) * tileSize;
            // AS3: Math.round(celY / tileY + 1) * tileY - 1  (feet-aligned)
            float targetY = (Mathf.Round(_cursorWorldPixels.y / tileSize + 1f)) * tileSize - 1f;

            // Collider dimensions for destination validation
            var tilePhysics = _motor as TilePhysicsController;
            float hw = 15f;
            float hh = 25f;
            if (tilePhysics != null)
            {
                hw = tilePhysics.CollisionWidth * 0.5f;
                hh = tilePhysics.CollisionHeight;
            }

            // TODO: Visibility check — tile.visi >= 0.8 (fog of war not yet implemented)
            // When fog of war is added, check: tile at cursor has visibility >= 0.8
            // For outdoor maps (sky=true), skip visibility check.

            // Collision check at destination (AS3: loc.collisionUnit)
            if (!_motor.CanTeleportTo(targetX, targetY, hw, hh))
            {
                return;
            }

            // Execute teleport
            _motor.TeleportTo(targetX, targetY);

            // Drain mana
            if (!(_abilities is PlayerLocomotionAbilities pla && pla.InfiniteMana))
            {
                _unitStats.Mana.Value -= _abilities.TeleportManaCost;
            }

            // End levitation if active
            if (_isLevitating)
            {
                EndLevitation();
            }

            _teleportCooldownTimer = _abilities.TeleportCooldownSeconds;
            _teleportFreezeTimer = DefaultTeleportFreezeDuration;
            _justTeleported = true;
        }

        private bool HasManaForTeleport()
        {
            if (_abilities is PlayerLocomotionAbilities pla && pla.InfiniteMana) return true;
            if (_unitStats == null) return false;
            return _unitStats.Mana.Value >= _abilities.TeleportManaCost;
        }

        // ──────────────────────────────────────
        //  State resolution
        // ──────────────────────────────────────

        private PlayerLocomotionState ResolveLocomotionState(MovementMotorState state)
        {
            if (IsTeleporting)
            {
                return PlayerLocomotionState.Teleporting;
            }

            if (IsDashing)
            {
                return PlayerLocomotionState.Dash;
            }

            if (state.IsOnLadder)
            {
                return PlayerLocomotionState.Ladder;
            }

            if (state.IsInWater)
            {
                return PlayerLocomotionState.Swim;
            }

            if (_isLevitating)
            {
                return PlayerLocomotionState.Levitate;
            }

            if (state.IsCrouching)
            {
                return PlayerLocomotionState.Crouched;
            }

            if (!state.IsGrounded)
            {
                return state.Velocity.y > 0.05f
                    ? PlayerLocomotionState.JumpRise
                    : PlayerLocomotionState.Fall;
            }

            return PlayerLocomotionState.Grounded;
        }

        private bool ResolveRunningState(MovementMotorState state)
        {
            return _runHeld &&
                !state.IsCrouching &&
                state.IsGrounded &&
                !state.IsOnLadder &&
                !state.IsInWater &&
                Mathf.Abs(_moveInput.x) > 0.1f;
        }

        private float ResolveNormalizedMoveSpeed(MovementMotorState state)
        {
            if (state.IsOnLadder || state.IsInWater) return 0f;

            float walkSpeed = _definition != null ? _definition.WalkSpeed : 4f;
            if (walkSpeed <= 0f) return 0f;

            float horizontalSpeed = Mathf.Abs(state.Velocity.x);
            return horizontalSpeed / walkSpeed;
        }
    }
}
