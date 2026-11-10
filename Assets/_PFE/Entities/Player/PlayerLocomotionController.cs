using PFE.Core;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Systems.Map;
using PFE.Systems.Map.TileQuery;
using PFE.Systems.Physics;
using UnityEngine;

namespace PFE.Entities.Player
{
    /// <summary>
    /// High-level player locomotion brain.
    /// Reads intent from PlayerController, applies movement rules, and drives the low-level motor.
    /// Queries ILocomotionAbilities for perk-modified capabilities (double jump, levitation, etc.).
    ///
    /// <para><b>It is also an <see cref="ISimTickable"/>, for one line of its work only: the swim
    /// impulse.</b> Everything else here is a <i>level</i> — a target speed, a gravity scale, a
    /// crouch flag — and a level is idempotent, so applying it once per Unity fixed step is
    /// harmless even when the motor steps on the sim clock instead. The swim is the exception:
    /// AS3 <i>adds</i> <c>plavdy</c> to <c>dy</c> every frame (<c>UnitPlayer.as:2804</c>), so the
    /// number of applications between two motor steps is the swim speed. At this project's
    /// <c>Fixed Timestep</c> of 0.02 (50 Hz) against a 30 Hz sim, <c>FixedUpdate</c> runs 1.67 times
    /// per step, which made the swim 1.67x too fast — terminal ~10 px/frame instead of 5.6 — and
    /// that is what made the water splash fire on every stroke of a surface bob, because the exit
    /// velocity then cleared AS3's ±5 px/frame splash gate
    /// (<see cref="PFE.Systems.Physics.SwimMath.SplashMinVerticalSpeedPixelsPerFrame"/>). See
    /// <see cref="SimTick"/>.</para>
    /// </summary>
    [DefaultExecutionOrder(-200)]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(UnitController))]
    [RequireComponent(typeof(TilePhysicsController))]
    public sealed class PlayerLocomotionController : MonoBehaviour, ISimTickable
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
        [SerializeField] internal bool canDash = true;
        [SerializeField] internal bool canCrouch = true;
        [SerializeField] internal float dashDurationSeconds = DefaultDashDurationSeconds;
        [SerializeField] internal float dashCooldownSeconds = DefaultDashCooldownSeconds;
        [SerializeField] internal float dashSpeedMultiplier = DefaultDashSpeedMultiplier;
        [SerializeField] internal float crouchSpeedMultiplier = DefaultCrouchSpeedMultiplier;
        [SerializeField] internal float swimSpeedMultiplier = DefaultSwimSpeedMultiplier;
        [SerializeField] internal float jumpBufferSeconds = DefaultJumpBufferSeconds;
        [SerializeField] internal float coyoteTimeSeconds = DefaultCoyoteTimeSeconds;
        [SerializeField] internal float jumpHoldGravityScale = DefaultJumpHoldGravityScale;
        [SerializeField] internal float jumpCutGravityScale = DefaultJumpCutGravityScale;
        [SerializeField] internal float fallGravityScale = DefaultFallGravityScale;
        [SerializeField] internal float swimGravityScale = DefaultSwimGravityScale;

        private IMovementMotor _motor;
        private UnitDefinition _definition;
        private ILocomotionAbilities _abilities;
        private UnitStats _unitStats;

        // The RPG model, so the locomotion tick can publish the precision state AS3 recomputes in
        // UnitPlayer.control(). Null when the player has no CharacterStats — the publish is skipped
        // rather than defaulted, so a model that is not there cannot silently report a multiplier.
        private PFE.Systems.RPG.CharacterStats _characterStats;

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

        // AS3 `downp` (`UnitPlayer.as:2878-2881`) in seconds: how long Down has been held without a
        // gap. A hold DEBOUNCE — the crouch latches only after it passes
        // `CrouchStateMath.RequiredHoldSeconds`, which is 2 frames on solid ground and 6 on a one-way
        // shelf. That window is what lets a double-tap of Down mean "drop through" without also
        // crouching on the way down.
        private float _sitHeldSeconds;

        // One crouch, one chance to roll. AS3 gates the roll on `maxSpeed > walkSpeed * 1.6`, and
        // `maxSpeed` is only that large on the frame the crouch BEGINS (see CrouchStateMath), so this
        // is decided once, at the latch, and then carried by the snapshot until the crouch ends.
        private bool _crouchRoll;

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

        /// <summary>
        /// The destination square (AS3 <c>vis.portCel</c>). Built lazily on the first frame of a charge
        /// that is long enough to show it, so a player that never teleports never pays for the art.
        /// Null when the art is missing — see <see cref="TeleportDestinationView.Attach"/>.
        /// </summary>
        private TeleportDestinationView _teleportMarker;
        private bool _teleportMarkerResolved;

        // Telekinesis/teleport trace bookkeeping. Both exist only to keep the trace from flooding the
        // console: `TryConsumeTeleport` runs EVERY frame, so a per-frame line for the whole hold would
        // bury the one event worth seeing (did the charge ever reach portTime?).
        private bool _teleportChargeLoggedReady;
        private bool _teleportUnavailableLogged;

        private PlayerMovementSnapshot _currentSnapshot;

        public PlayerMovementSnapshot CurrentSnapshot => _currentSnapshot;

        public bool IsDashing => _dashTimer > 0f;
        public bool IsCrouching => _isCrouching;
        public bool IsLevitating => _isLevitating;

        /// <summary>
        /// AS3's <c>levitup</c> — levitating and climbing. The mana block charges the higher
        /// <c>levitDManaUp + levitDMana</c> only in this state (<c>UnitPlayer.as:1311-1314</c>),
        /// and the full organ wound rather than a third of it (<c>:1323-1329</c>).
        /// </summary>
        public bool IsLevitatingUpward => _isLevitating && _levitationVerticalSpeed > 0f;

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
        /// Provide the RPG model so the player's <c>precMult</c> can be rebuilt each tick.
        /// Called by <c>PlayerController</c> alongside <see cref="SetUnitStats"/>.
        ///
        /// <para><b>Why the movement layer owns this.</b> AS3 computes the four situational precision
        /// terms inside <c>UnitPlayer.control()</c> (<c>UnitPlayer.as:1181-1200</c>) — the movement
        /// tick — and the weapon only ever reads the result. Keeping the publish here means the weapon
        /// controllers need no reference to locomotion, and the multiplier a shot carries is the one
        /// the player's actual state produced, not one re-derived (and possibly re-derived
        /// differently) at fire time.</para>
        /// </summary>
        public void SetCharacterStats(PFE.Systems.RPG.CharacterStats stats)
        {
            _characterStats = stats;
        }

        /// <summary>
        /// Publish the oracle's precision inputs for this tick.
        ///
        /// <para><b>Unit conversion is the trap.</b> AS3's <c>dx</c>/<c>dy</c> are in <b>pixels per
        /// AS3 frame</b>, and the thresholds (<c>&gt; 10</c>, <c>&lt; 1</c>) mean what they say only
        /// in that unit. <c>MovementMotorState.Velocity</c> is in Unity world units per second, where
        /// one world unit is one tile and one tile is <c>TileQueryConstants.UnitToPixel</c> = 100 px.
        /// The project's own documented idiom for a per-frame velocity is
        /// <c>px/frame × fps / 100 = units/s</c> (see <c>TileQueryConstants</c>), so the inverse is
        /// <c>units/s × 100 / fps = px/frame</c>. Passing raw world velocity would make every walk
        /// exceed the running threshold by ~3000× and permanently disable the stand-still bonus.</para>
        ///
        /// <para><b>The frame rate is the canonical 30, not the live tick rate.</b> AS3's <c>dx</c>
        /// is defined per AS3 frame, so the conversion must use the same unit definition the physics
        /// constants do — <c>SimClock.FramesPerSecond</c>. Using the live
        /// <c>TicksPerSecond</c> would change the meaning of the oracle's literal thresholds on a
        /// 60/90/120 Hz build, which is exactly what "nothing may derive from a different value"
        /// forbids.</para>
        ///
        /// <para><b>Weapon-facing is left false here.</b> AS3's term is
        /// <c>currentWeapon.storona != storona</c> — a comparison of the equipped weapon's facing
        /// against the body's. The weapon's facing is derived from the cursor
        /// (<c>Weapon.as:1062-1066</c>), which the locomotion layer does not own, so publishing
        /// <c>false</c> is the honest "not known here" value and keeps the term out of the multiplier
        /// rather than guessing it. The back-penalty therefore reads as not-applied until a caller
        /// that knows both facings supplies it — recorded as an open divergence, not silently
        /// approximated.</para>
        /// </summary>
        private void PublishPrecisionState(MovementMotorState state)
        {
            if (_characterStats == null)
                return;

            const float fps = PFE.Core.SimClock.FramesPerSecond;   // canonical 30, not the live rate
            float unitsToPixelsPerFrame = TileQueryConstants.UnitToPixel / fps;

            _characterStats.CurrentPrecisionState = new PFE.Systems.RPG.CharacterStats.PrecisionState(
                isGrounded:          state.IsGrounded,
                dx:                  state.Velocity.x * unitsToPixelsPerFrame,
                dy:                  state.Velocity.y * unitsToPixelsPerFrame,
                weaponFacingDiffers: false);
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

        /// <summary>
        /// Consume one tick of intent as a <see cref="PFE.Core.Input.PlayerCommand"/> — the
        /// transport-ready form of <see cref="SetIntent(Vector2, bool, bool, bool, bool, bool, float)"/>.
        /// This is the seam a host (applying a received command) or a predicting client (replaying a
        /// buffered command) drives; the intent it carries is identical to the parameter-list form,
        /// so behaviour is unchanged — only the shape of the hand-off differs.
        /// </summary>
        public void SetIntent(in PFE.Core.Input.PlayerCommand command)
        {
            SetIntent(
                new Vector2(command.MoveXFloat, command.MoveYFloat),
                command.JumpHeld,
                command.RunHeld,
                command.JumpPressed,
                command.DashPressed,
                command.DropThroughPressed,
                command.AimAngle);
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

            // AS3 `isUp = this.ctr.keyBeUp` (`UnitPlayer.as:2944`). The same axis the ladder call above
            // receives, published separately because the oracle keeps them apart: Up is the ladder's
            // climb key AND the crouch's release key AND the mantle's key (`Unit.as:2108`), and which
            // of the three acts is decided by the geometry, not by the input.
            _motor.SetUpHeld(CrouchStateMath.UpHeld(_moveInput.y));

            UpdateCrouch(state, dt);

            // Levitation update (before gravity/speed resolve so state is current)
            UpdateLevitation(state, dt);

            float targetSpeed = ResolveTargetHorizontalSpeed(state);
            _motor.SetDesiredHorizontalSpeed(targetSpeed);
            _motor.SetCrouching(_isCrouching);

            // AS3 `UnitPlayer.as:2640` — `throu = (ctr.keyJump || !stay) && ctr.keySit || …`, which
            // is re-derived EVERY frame. The buffered double-tap below is the oracle's other arm
            // (`keyDubSit`, `:2917-2925`) and it is a one-off: it sets the flag and hands out a 10
            // px/frame push. On its own the port then falls for the pulse's 0.18 s and is caught by the
            // next surface down — on a diagonal staircase that is the tread one step below, so the
            // flight could not be dropped through. The level arm is what keeps `throu` true while the
            // unit is off the ground with Down held, which is how the original falls the whole way.
            _motor.SetDropThroughPlatforms(_dropThroughPressedBuffered
                || UnitDropThroughMath.PlayerShouldDropThrough(_jumpHeld, _moveInput.y, state.IsGrounded));
            _motor.SetGravityScale(ResolveGravityScale(state));

            // Apply levitation vertical speed
            if (_isLevitating)
            {
                float verticalSpeed = ResolveLevitationVerticalSpeed(state, dt);
                _motor.SetDesiredVerticalSpeed(verticalSpeed);
            }
            // Vertical swim control (AS3: `dy -= plavdy * speedPlavMult` — an IMPULSE, not a speed).
            // Only units with canSwim can actively swim; others sink normally.
            //
            // `!_simRegistered`: with the impulse on the sim clock (SimTick) this arm must stand down,
            // or the impulse is applied twice — once per fixed step AND once per step, which is the
            // 1.67x over-application this class comment describes. Unregistered (the legacy path,
            // where the motor steps per FixedUpdate) one call here is one call per step, so the
            // ratio is 1:1 and this arm is correct.
            else if (!_simRegistered && state.IsFullySubmerged && CanSwim)
            {
                ApplySwimImpulse();
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
                Crouching(latestState),
                ResolveRunningState(latestState),
                latestState.FacingDirection,
                _aimAngle,
                _justDoubleJumped,
                _justLanded,
                normalizedMoveSpeed,
                _isLevitating,
                _justTeleported,
                _crouchRoll);

            _wasGroundedLastFrame = latestState.IsGrounded;
            _dashPressedBuffered = false;
            _dropThroughPressedBuffered = false;

            // Last, so it sees the final state for this tick — the same ordering as AS3, where the
            // precision block runs at the end of control() after movement has resolved.
            PublishPrecisionState(latestState);
        }

        // ──────────────────────────────────────
        //  Crouch (AS3 `isSit`)
        // ──────────────────────────────────────

        /// <summary>
        /// AS3's <c>isSit</c> latch, once per fixed step.
        ///
        /// <para><b>Three separate things live in the oracle's one flag and this method keeps them
        /// separate.</b> (1) The <i>hold debounce</i> — <c>downp</c> — which decides when a held Down
        /// key becomes a crouch. (2) The <i>latch</i>, cleared only by Up or by leaving the ground.
        /// (3) The <i>entry speed</i>, which decides whether that crouch is a roll. Folding (1) or (3)
        /// back into the latch is what produced the shipped behaviour, where crouch was simply
        /// <c>move.y &lt; -0.5</c> and neither the crawl nor the roll was reachable.</para>
        ///
        /// <para><b>What this method does not own: the collision box.</b> <c>SetCrouching</c> asks; the
        /// motor decides, because a player under a low ceiling must stay low even after the latch
        /// clears (AS3's <c>unsit()</c> re-sits on <c>collisionAll()</c>,
        /// <c>Unit.as:2532-2538</c>). <see cref="Crouching"/> therefore ORs the two, and everything
        /// downstream — speed, locomotion state, animation — reads that instead of the latch.</para>
        /// </summary>
        private void UpdateCrouch(MovementMotorState state, float dt)
        {
            bool sitHeld = CrouchStateMath.SitHeld(_moveInput.y);
            bool upHeld = CrouchStateMath.UpHeld(_moveInput.y);

            _sitHeldSeconds = sitHeld ? _sitHeldSeconds + dt : 0f;

            if (!canCrouch)
            {
                ClearCrouch();
                return;
            }

            if (_isCrouching)
            {
                // `stay` for the release test is grounded-and-not-on-a-ladder: a ladder's Down
                // descends (AS3 `isLaz` arm), it does not crouch, so the two states are exclusive.
                if (CrouchStateMath.ShouldEndCrouch(upHeld, sitHeld, state.IsGrounded && !state.IsOnLadder))
                {
                    ClearCrouch();
                }

                return;
            }

            if (!CrouchStateMath.MayBeginCrouch(state.IsGrounded, state.IsInWater, state.IsOnLadder, RunForced))
            {
                return;
            }

            if (_sitHeldSeconds < CrouchStateMath.RequiredHoldSeconds(state.IsOnPlatform, PFE.Core.SimClock.FramesPerSecond))
            {
                return;
            }

            _isCrouching = true;
            _crouchRoll = CrouchStateMath.ShouldRoll(
                speedBeforeCrouch: ResolveSpeedIgnoringCrouch(state),
                walkSpeed: WalkSpeed,
                horizontalVelocity: state.Velocity.x,
                facingDirection: state.FacingDirection,
                horizontalInput: _moveInput.x,
                runHeld: _runHeld,
                runForced: RunForced);
        }

        private void ClearCrouch()
        {
            _isCrouching = false;
            _crouchRoll = false;
        }

        /// <summary>
        /// The crouch the <i>world</i> sees: the latch, or a collision box the motor is still holding
        /// low because standing up is blocked. See <see cref="UpdateCrouch"/>.
        /// </summary>
        private bool Crouching(MovementMotorState state)
        {
            return _isCrouching || state.IsCrouching;
        }

        /// <summary>
        /// AS3's <c>maxSpeed</c> on the frame a crouch begins — computed <i>before</i> <c>sit(true)</c>
        /// ran (<c>UnitPlayer.as:2457-2490</c> vs <c>:2859</c>), so with the crouch term absent. Only
        /// the run/walk distinction matters: the crouch already requires <c>stay</c>, no water, no
        /// ladder and no diagonal, which are the oracle's other overrides.
        /// </summary>
        private float ResolveSpeedIgnoringCrouch(MovementMotorState state)
        {
            float walkSpeed = WalkSpeed;

            if (state.IsOnLadder || state.IsInWater || _isLevitating)
            {
                return walkSpeed;
            }

            if (_runHeld || RunForced)
            {
                return _definition != null ? _definition.RunSpeed : walkSpeed * DefaultRunMultiplier;
            }

            return walkSpeed;
        }

        private float WalkSpeed => _definition != null ? _definition.WalkSpeed : 4f;

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

                // Affordability only — the drain is applied once per tick by PlayerManaTicker.
                if (!CanAffordLevitationTick(state))
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

        /// <summary>
        /// Whether levitation may continue this frame — a CHECK, not a charge.
        ///
        /// <para>The drain itself is applied by <see cref="PlayerManaTicker"/> →
        /// <see cref="PFE.Systems.RPG.CharacterStats.TickMana"/>, which is AS3's single per-frame
        /// block (<c>UnitPlayer.as:1302-1332</c>) and charges BOTH pools: the budget via
        /// <c>mana += dmana</c> and the organ via <c>manaDamage(...)</c>. This method used to
        /// subtract the cost itself, which would now double-charge the budget and — worse — would
        /// leave the organ wound out of the loop entirely.</para>
        ///
        /// <para>The cost is deliberately per-tick, with no <c>deltaTime</c>: AS3 charges once per
        /// frame and the sim's canonical rate is 30 Hz, so a per-second conversion here was the
        /// bug rather than the fix. The previous <c>cost *= dt</c> made levitation's cost depend on
        /// Unity's fixed-step setting.</para>
        /// </summary>
        private bool CanAffordLevitationTick(MovementMotorState state)
        {
            if (_abilities is PlayerLocomotionAbilities pla && pla.InfiniteMana) return true;
            if (_unitStats == null) return false;
            if (_abilities == null) return false;

            float cost = _abilities.LevitationManaCostPerTick;

            // Extra cost when ascending (AS3's `levitup` branch, UnitPlayer.as:1311-1314).
            if (state.Velocity.y > 0.05f)
            {
                cost += _abilities.LevitationManaCostUpward;
            }

            return _unitStats.Mana.Value >= cost;
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

        private SimLoop _simLoop;
        private bool _simRegistered;

        /// <summary>
        /// One impulse per AS3 frame, so it is applied once per <b>sim step</b> and never per
        /// <c>FixedUpdate</c> — see the class comment for why the difference is the swim speed.
        ///
        /// <para><see cref="SimTickOrder.Input"/> rather than <c>PlayerMotor</c>: the motor consumes
        /// the pending impulse inside its own step (20), so this has to run before it. That is also
        /// the oracle's order — <c>control()</c> reads the key and writes <c>dy</c>, then
        /// <c>run()</c> integrates.</para>
        /// </summary>
        public int TickOrder => SimTickOrder.Input;

        /// <summary>Puts the swim impulse on the sim clock. Safe to call twice.</summary>
        public void AttachSimulation(SimClock clock, SimLoop loop)
        {
            if (loop == null)
            {
                return;
            }

            _simLoop = loop;
            if (!_simRegistered)
            {
                _simLoop.Register(this);
                _simRegistered = true;
            }
        }

        public void SimTick(int tickIndex)
        {
            // `_isLevitating` is a level set in FixedUpdate; a levitating player does not swim
            // (`UnitPlayer.as:2804` is gated on isPlav, and levitation stands the swim down).
            if (_motor == null || _isLevitating)
            {
                return;
            }

            // `isPlav`, not `inWater`: AS3 only accepts the swim keys while the 75% sample is wet.
            // Read from the motor, i.e. the state the last step left behind — the same value
            // FixedUpdate's `state.IsFullySubmerged` sees.
            //
            // `_moveInput`/`_jumpHeld` are the frame-boundary snapshot FixedUpdate fills in, which is
            // exactly what `ISimTickable` asks a tick to read: a level gathered once per frame, not a
            // value sampled inside the tick. At most one fixed step of latency, and the oracle has
            // the same one — `control()` reads the key state the frame handler last wrote.
            MovementMotorState state = _motor.State;
            if (!state.IsFullySubmerged || !CanSwim)
            {
                return;
            }

            ApplySwimImpulse();
        }

        private void OnDestroy()
        {
            if (_simLoop != null && _simRegistered)
            {
                _simLoop.Unregister(this);
                _simRegistered = false;
            }
        }

        /// <summary>
        /// AS3's swim control: an <b>impulse added to <c>dy</c> every frame</b>, not a target speed.
        ///
        /// <para><c>UnitPlayer.as:2804-2807</c> is <c>if(isPlav &amp;&amp; keyBeUp &amp;&amp; !keyJump)
        /// dy -= plavdy * pers.speedPlavMult</c>, and <c>:2700-2703</c> is the same impulse for
        /// <c>keyJump</c> while <c>isPlav</c> — which is why a swimmer's jump key does not jump, it
        /// swims. <c>:2873</c> adds half of it downward for <c>keySit</c>. The water's
        /// <c>dy *= 0.8</c> (<c>Unit.as:1931</c>, the motor's drag) is what turns the impulse into a
        /// terminal speed; see <see cref="PFE.Systems.Physics.SwimMath"/> for the arithmetic and for
        /// why the old assigned-speed form could not carry the unit out of the water.</para>
        ///
        /// <para><b>One impulse per frame, not two.</b> The up-key and jump arms are mutually
        /// exclusive in the oracle (<c>keyBeUp &amp;&amp; !keyJump</c>), so holding both must not swim
        /// at twice the speed — hence the early return rather than two calls.</para>
        /// </summary>
        private void ApplySwimImpulse()
        {
            float walkSpeed = _definition != null ? _definition.WalkSpeed : 4f;

            if (CrouchStateMath.UpHeld(_moveInput.y) || _jumpHeld)
            {
                _motor.AddVerticalImpulse(SwimMath.UpImpulsePixelsPerFrame(walkSpeed));
                return;
            }

            if (CrouchStateMath.SitHeld(_moveInput.y))
            {
                _motor.AddVerticalImpulse(-SwimMath.DownImpulsePixelsPerFrame(walkSpeed));
            }
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
            else if (Crouching(state))
            {
                speed *= Mathf.Clamp(crouchSpeedMultiplier, 0.1f, 1f);
            }
            else if (state.IsInWater)
            {
                speed *= Mathf.Clamp(swimSpeedMultiplier, 0.1f, 1f);
            }
            else if (_runHeld || RunForced)
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

        /// <summary>
        /// AS3 <c>runForever &gt; 0</c> — the <c>horror</c> effect's forced run
        /// (<c>AllData.as:5963</c> → <c>UnitPlayer.as:2467</c>).
        ///
        /// <para><b>It substitutes for the run key, and nothing else.</b> The oracle is
        /// <c>isRun = (possRun || stam &gt; 200) &amp;&amp; ctr.keyRun || runForever &gt; 0</c>, so the
        /// forced term sits exactly where the key sits — which is why this is folded in beside
        /// <c>_runHeld</c> rather than short-circuiting the speed. The port maps the run key to
        /// <c>IsDashing</c> (see <c>PlayerController</c>), so the substitution lands in the same
        /// place.</para>
        /// </summary>
        private bool RunForced => _characterStats != null && _characterStats.runForever > 0;

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

            // Fully submerged swimmers: can't normal-jump, the swim impulse handles upward movement.
            // AS3 has the same shape — while `isPlav`, the jump key takes the `dy -= plavdy` arm
            // (`UnitPlayer.as:2700`) and never reaches the `jumpp > 0` jump arm, so a swimmer cannot
            // jump and `jumpp` is never even charged (it is only set while `stay`). Holding the key
            // still swims up, through `_jumpHeld` in ApplySwimImpulse.
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

            // AS3 `UnitPlayer.as:2592`/`:2615` — the dash condition carries `!isSit`. It was
            // unreachable while the crouch was a key level (you could not be crouched and double-tap a
            // direction at the same time); with the latch it is a real input, so the gate has to exist.
            if (Crouching(state))
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
            if (_abilities == null || !_abilities.CanTeleport)
            {
                if (TelekinesisTrace.Enabled && !_teleportUnavailableLogged)
                {
                    _teleportUnavailableLogged = true;
                    TelekinesisTrace.Log(
                        $"[6] teleport DISABLED: _abilities={(_abilities == null ? "NULL" : "present")}, " +
                        $"CanTeleport={(_abilities != null && _abilities.CanTeleport)}. Q can never teleport in this state.");
                }
                HideTeleportMarker();
                return;
            }
            if (IsTeleporting)
            {
                HideTeleportMarker();
                return;
            }

            // There is deliberately NO held-object gate here. AS3's teleport path is checkPort()
            // (UnitPlayer.as:1690), which tests mana, loc.sky, tile visibility >= 0.8 and
            // collisionUnit — nothing about teleObj — and actPort() does not drop the held prop
            // either. An earlier version of this method refused to charge while IsHoldingObject,
            // which meant that as soon as grabbing worked, holding Q silently stopped teleporting.
            // That gate was invented, not ported.

            if (_teleportHeld)
            {
                // Accumulate charge while key is held (AS3: t_port++)
                // Only charge if cooldown is done and preconditions met
                if (_teleportCooldownTimer > 0f || IsDashing || state.IsOnLadder)
                {
                    if (_teleportChargeTimer > 0f)
                    {
                        TelekinesisTrace.Log(
                            $"teleport charge RESET: cooldown={_teleportCooldownTimer:F2}s dashing={IsDashing} onLadder={state.IsOnLadder}");
                    }
                    _teleportChargeTimer = 0f;
                    HideTeleportMarker();
                    return;
                }

                // Mana pre-check (don't charge if we can't afford it)
                if (!HasManaForTeleport())
                {
                    if (_teleportChargeTimer > 0f)
                    {
                        TelekinesisTrace.Log("teleport charge RESET: not enough mana");
                    }
                    _teleportChargeTimer = 0f;
                    HideTeleportMarker();
                    return;
                }

                _teleportChargeTimer += Time.fixedDeltaTime;

                // GUI.as:1977 — the destination square is drawn from the 15th frame of the charge on,
                // in one of three frames chosen by checkPort() (GUI.as:1536-1551).
                UpdateTeleportMarker();

                // Log the crossing only. Whether the charge REACHES portTime is the whole question;
                // a line per frame for the duration of the hold would bury it.
                if (!_teleportChargeLoggedReady &&
                    _teleportChargeTimer >= _abilities.TeleportChargeTimeSeconds)
                {
                    _teleportChargeLoggedReady = true;
                    TelekinesisTrace.Log(
                        $"teleport charge REACHED portTime ({_teleportChargeTimer:F2}s >= " +
                        $"{_abilities.TeleportChargeTimeSeconds:F2}s) -- releasing Q now will teleport");
                }
                return;
            }

            // Key was released — check if charge is sufficient.
            // NOTE this branch runs every frame while Q is up, so nothing below may log unless a charge
            // was actually in progress.
            float chargedTime = _teleportChargeTimer;
            _teleportChargeTimer = 0f;
            _teleportChargeLoggedReady = false;
            HideTeleportMarker();
            bool hadCharge = chargedTime > 0f;

            if (chargedTime < _abilities.TeleportChargeTimeSeconds)
            {
                if (hadCharge)
                {
                    TelekinesisTrace.Log(
                        $"teleport REFUSED: charge too short ({chargedTime:F2}s < " +
                        $"{_abilities.TeleportChargeTimeSeconds:F2}s) -- hold Q longer");
                }
                return;
            }
            if (_teleportCooldownTimer > 0f)
            {
                TelekinesisTrace.Log($"teleport REFUSED: cooldown {_teleportCooldownTimer:F2}s remaining");
                return;
            }
            if (IsDashing || state.IsOnLadder)
            {
                TelekinesisTrace.Log($"teleport REFUSED: dashing={IsDashing} onLadder={state.IsOnLadder}");
                return;
            }
            if (!HasManaForTeleport())
            {
                TelekinesisTrace.Log("teleport REFUSED: not enough mana");
                return;
            }

            TelekinesisTrace.Log(
                $"teleport RELEASED with a valid charge ({chargedTime:F2}s) -- cursor=({_cursorWorldPixels.x:F1},{_cursorWorldPixels.y:F1})px");

            // Snap the cursor to the destination cell. AS3: UnitPlayer.as:1641-1642 —
            //   _loc1_ = Math.round(celX / tileX) * tileX;
            //   _loc2_ = Math.round(celY / tileY + 1) * tileY - 1;
            // The y expression is NOT portable as written: AS3's y grows downward, so "+ tileY - 1"
            // puts the feet one pixel above the destination cell's BOTTOM seam. Copied into this y-up
            // motor it evaluates to `round(y/tile)*tile + tile - 1`, one whole cell ABOVE the cursor —
            // every indoor destination landed inside the ceiling, CanTeleportTo refused it, and
            // teleporting looked broken in every room with a roof while still working under open sky.
            // See PlayerTeleportTargetMath for the derivation.
            PlayerTeleportTargetMath.Target target =
                PlayerTeleportTargetMath.Resolve(_cursorWorldPixels, WorldConstants.TILE_SIZE);
            float targetX = target.FeetPixelX;
            float targetY = target.FeetPixelY;

            ResolveTeleportProbe(out float hw, out float hh);

            // TODO: Visibility check — tile.visi >= 0.8 (UnitPlayer.as:1701). The port has no per-tile
            // visibility: fog is a render-pass shader over the backdrop, and nothing writes an AS3-style
            // `Tile.visi` back to the tile data. Outdoors the oracle skips the test anyway (`loc.sky`),
            // and indoors the port currently allows the teleport on collision alone. Not invented here.
            if (!_motor.CanTeleportTo(targetX, targetY, hw, hh))
            {
                TelekinesisTrace.Log(
                    $"teleport REFUSED: destination ({targetX:F0},{targetY:F0}) is not clear " +
                    $"(CanTeleportTo false; probe half-extents {hw:F1}x{hh:F1})");
                return;
            }

            // Execute teleport
            _motor.TeleportTo(targetX, targetY);

            TelekinesisTrace.Log($"teleport EXECUTED to ({targetX:F0},{targetY:F0})");

            // Drain mana — the BUDGET pool (AS3 Unit.mana, 1000), not the mana organ.
            // TeleportManaCost already folds in allDManaMult (AS3's `portMana * allDManaMult`).
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

        /// <summary>
        /// AS3's teleport affordability test — <c>UnitPlayer.as:1633</c>:
        /// <c>mana &lt; pers.portMana * pers.allDManaMult &amp;&amp; mana &lt; maxmana * 0.99</c>.
        ///
        /// <para>Note the <c>&amp;&amp;</c>: the refusal needs BOTH conditions, so a pool at or above
        /// 99% of its ceiling teleports regardless of cost. That looks like a bug and probably is
        /// one, but it is the oracle. It is routed through
        /// <see cref="PFE.Systems.RPG.CharacterStats.CanAffordMagicMana"/> so the rule lives in one
        /// place — duplicating it here is how the two copies would drift.</para>
        /// </summary>
        private bool HasManaForTeleport()
        {
            if (_abilities is PlayerLocomotionAbilities pla && pla.InfiniteMana) return true;
            if (_unitStats == null) return false;

            float cost = _abilities.TeleportManaCost;

            if (_abilities is PlayerLocomotionAbilities stats && stats.CharacterStats != null)
            {
                return stats.CharacterStats.CanAffordMagicMana(cost);
            }

            // No organs reachable. UnitStats carries the BUDGET, so the same rule still applies --
            // including the 99% bypass, which a bare `>= cost` here would have dropped. Routed
            // through the shared static rather than re-written, so the two cannot drift.
            return PFE.Systems.RPG.CharacterStats.CanAffordMagic(
                _unitStats.Mana.Value, _unitStats.MaxMana.Value, cost);
        }

        /// <summary>
        /// The AABB the oracle's <c>loc.collisionUnit(x, y, stayX, stayY)</c> tests at the destination
        /// (<c>Location.as:2342-2366</c>): half-width <c>stayX / 2</c> and full height <c>stayY</c>,
        /// standing ON the given point.
        ///
        /// <para><b>The height is not doubled.</b> <c>CanTeleportTo</c> used to build its rect as
        /// <c>(x - hw, y, 2hw, 2hh)</c>, i.e. twice the body height, so any solid within two body
        /// heights above the feet refused the teleport — which indoors is the ceiling, the whole point.
        /// Combined with the mis-mirrored y snap (see <see cref="PlayerTeleportTargetMath"/>) that made
        /// every indoor teleport fail while outdoor ones still worked.</para>
        /// </summary>
        private void ResolveTeleportProbe(out float halfWidth, out float halfHeight)
        {
            halfWidth = 15f;
            halfHeight = 25f;

            if (_motor is TilePhysicsController tilePhysics)
            {
                halfWidth = tilePhysics.CollisionWidth * 0.5f;
                halfHeight = tilePhysics.CollisionHeight;
            }
        }

        /// <summary>
        /// <c>gg.checkPort()</c> (<c>UnitPlayer.as:1690-1706</c>) as the marker needs it: mana, and the
        /// destination clear of solid geometry. The oracle's third term, <c>tile.visi &gt;= 0.8</c>, has
        /// no counterpart in this port — there is no per-tile visibility value to read — and the sky
        /// branch (<c>loc.sky</c>) exists only to skip it, so both are absent rather than faked.
        /// </summary>
        private bool IsTeleportDestinationAllowed(PlayerTeleportTargetMath.Target target)
        {
            if (!HasManaForTeleport())
            {
                return false;
            }

            ResolveTeleportProbe(out float hw, out float hh);
            return _motor.CanTeleportTo(target.FeetPixelX, target.FeetPixelY, hw, hh);
        }

        /// <summary>
        /// Draw or hide the destination square for the current charge — the port of
        /// <c>GUI.step</c>'s <c>if(gg.t_port &gt; 15) showPortCel();</c> (<c>GUI.as:1977</c>) and
        /// <c>showPortCel</c> itself (<c>:1531-1552</c>).
        /// </summary>
        private void UpdateTeleportMarker()
        {
            float chargeTime = _abilities != null ? _abilities.TeleportChargeTimeSeconds : 0f;
            if (!PlayerTeleportTargetMath.ShouldShowMarker(_teleportChargeTimer, chargeTime))
            {
                HideTeleportMarker();
                return;
            }

            if (!_teleportMarkerResolved)
            {
                _teleportMarkerResolved = true;
                _teleportMarker = TeleportDestinationView.Attach(gameObject);
            }

            if (_teleportMarker == null)
            {
                return;
            }

            PlayerTeleportTargetMath.Target target =
                PlayerTeleportTargetMath.Resolve(_cursorWorldPixels, WorldConstants.TILE_SIZE);

            bool allowed = IsTeleportDestinationAllowed(target);
            bool complete = _teleportChargeTimer >= chargeTime;
            int frame = PlayerTeleportTargetMath.ResolveMarkerFrame(allowed, complete);

            _teleportMarker.Show(frame, target.CellRectPixels(WorldConstants.TILE_SIZE));
        }

        private void HideTeleportMarker()
        {
            if (_teleportMarker != null)
            {
                _teleportMarker.Hide();
            }
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

            if (Crouching(state))
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
            bool forced = RunForced;
            if (!(_runHeld || forced))
            {
                return false;
            }

            if (Crouching(state) || !state.IsGrounded || state.IsOnLadder || state.IsInWater)
            {
                return false;
            }

            // The movement requirement is the port's, not the oracle's: AS3's `isRun` has no such term.
            // It exists because `IsRunning` drives the locomotion animation here. A forced run skips it
            // on purpose — AS3 refuses the sit/stay transition while `runForever > 0` (`:2839`), so a
            // panicking player is in the run state even standing still. Dropping the requirement for
            // the key path instead would make an idle player animate as running.
            return forced || Mathf.Abs(_moveInput.x) > 0.1f;
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
