using UnityEngine;
using VContainer;
using R3;
using MessagePipe;
using PFE.Core.Input;
using PFE.Core.Messages;
using PFE.Entities.Units;
using PFE.Systems.Interaction;
using PFE.Systems.Map;
using PFE.Systems.Physics;
using PFE.Systems.Weapons;
namespace PFE.Entities.Player
{
    /// <summary>
    /// Player controller for LittlePip (the protagonist).
    /// Replaces UnitPlayer.as from ActionScript (5,017 lines condensed to ~200 lines).
    ///
    /// Key features:
    /// - WASD movement with acceleration
    /// - Jump with adjustable force
    /// - Dash/double-tap detection (TODO)
    /// - Weapon aiming and firing
    /// - Uses VContainer for dependency injection
    /// - Uses InputReader for decoupled input handling
    /// </summary>
    public class PlayerController : UnitController
    {
        [Header("Player Specifics")]
        [SerializeField]
        [Tooltip("Should the player always face the mouse cursor?")]
        private bool _mouseAiming = true;

        [SerializeField]
        [Tooltip("Maximum time between directional taps for dash/drop-through detection.")]
        private float _doubleTapWindowSeconds = 0.25f;
        private TilePhysicsController _tilePhysics;
        private PlayerLocomotionController _locomotion;

        /// <summary>
        /// Runs the hold-to-act gesture for world objects. Optional — a player prefab without one keeps
        /// the immediate-press behaviour for every target, since every use below is null-guarded.
        /// </summary>
        private PlayerActionInteractor _actionInteractor;

        /// <summary>
        /// Frame of the last accepted interact press. The press arrives twice — once on the
        /// MessagePipe subscription and once from the direct poll in <see cref="Update"/> — and this is
        /// what stops one key press acting twice, which for a door would open and close it in a single
        /// frame.
        /// </summary>
        private int _lastInteractPressFrame = -1;
        // Dependencies (injected via VContainer)
        private InputReader _input;
        private PFE.Core.PfeDebugSettings _debugSettings;

        /// <summary>
        /// The player's weapon, and the only weapon path there is.
        ///
        /// <para>The component exists from <c>Awake</c>, but the loadout does not create its
        /// <c>IWeaponController</c> until its own <c>Start()</c> — and Unity does not order
        /// <c>Start</c> between components. So every call below goes through the loadout's
        /// null-guarded forwarding methods rather than reaching for the controller directly.</para>
        ///
        /// <para>The legacy <c>WeaponLogic</c> / <c>WeaponView</c> / <c>IWeaponFactory</c> branch was
        /// retired in Phase 2.2. It had been unreachable since the Player prefab gained a
        /// <c>PlayerWeaponLoadout</c>, but it kept a second, half-wired weapon system alive.</para>
        /// </summary>
        private PlayerWeaponLoadout _loadout;

        // MessagePipe subscriptions (disposable)
        private CompositeDisposable _disposables;

        private Camera _mainCamera;
        private bool _isRunning;
        private float _aimAngle;
        private float _lastLeftTapTime = float.NegativeInfinity;
        private float _lastRightTapTime = float.NegativeInfinity;
        private float _lastDownTapTime = float.NegativeInfinity;
        private bool _wasLeftHeld;
        private bool _wasRightHeld;
        private bool _wasDownHeld;

        // VContainer Injection
        [Inject]
        public void Construct(
            InputReader input,
            ISubscriber<AttackMessage> attackSubscriber,
            ISubscriber<TeleportMessage> teleportSubscriber,
            ISubscriber<InteractMessage> interactSubscriber,
            PFE.Core.PfeDebugSettings debugSettings)
        {
            _input = input;
            _debugSettings = debugSettings;
            if (_debugSettings.LogDependencyInjectionConstruct)
                Debug.Log("[PlayerController] Construct() called — dependencies injected.");

            // Subscribe to MessagePipe events
            _disposables = new CompositeDisposable();

            attackSubscriber.Subscribe(message =>
            {
                if (_debugSettings.LogWeaponLifecycle)
                    Debug.Log($"[PlayerController] AttackMessage received — IsStarted={message.IsStarted}.");
                if (message.IsStarted)
                    HandleAttackStart();
                else
                    HandleAttackEnd();
            }).AddTo(_disposables);

            teleportSubscriber.Subscribe(message =>
            {
                if (_locomotion != null)
                {
                    _locomotion.SetTeleportHeld(message.IsStarted);
                }
            }).AddTo(_disposables);

            interactSubscriber.Subscribe(message =>
            {
                if (message.IsPressed)
                {
                    HandleInteract();
                }
                else
                {
                    EndInteract();
                }
            }).AddTo(_disposables);
        }

        protected override void Awake()
        {
            base.Awake();
            _mainCamera = Camera.main;
            _tilePhysics = GetComponent<TilePhysicsController>();
            _locomotion = GetComponent<PlayerLocomotionController>();
            _loadout = GetComponent<PlayerWeaponLoadout>();
            _actionInteractor = GetComponent<PlayerActionInteractor>();
            base._unitStats = new UnitStats();

            if (_locomotion != null)
                _locomotion.SetUnitStats(base._unitStats);
        }

        private void OnDestroy()
        {
            // Clean up MessagePipe subscriptions
            _disposables?.Dispose();
        }

        private void Update()
        {
            HandleAiming();
            HandleMovementInput();

            // Direct interact check (E key or Right-Click). Both edges are read, because acting is a
            // hold: AS3 keeps keyAction as a held boolean (set at Ctr.as:699, cleared at :797) and
            // UnitPlayer.as:2115-2135 acts on the false edge by abandoning the action. The MessagePipe
            // subscription above carries the same two edges; both paths are guarded per frame below,
            // so the redundancy cannot double-fire.
            bool ePressed = Input.GetKeyDown(KeyCode.E) ||
                (UnityEngine.InputSystem.Keyboard.current != null && UnityEngine.InputSystem.Keyboard.current.eKey.wasPressedThisFrame);
            bool rightClick = Input.GetMouseButtonDown(1) ||
                (UnityEngine.InputSystem.Mouse.current != null && UnityEngine.InputSystem.Mouse.current.rightButton.wasPressedThisFrame);

            if (ePressed || rightClick)
            {
                HandleInteract();
            }

            bool eReleased = Input.GetKeyUp(KeyCode.E) ||
                (UnityEngine.InputSystem.Keyboard.current != null && UnityEngine.InputSystem.Keyboard.current.eKey.wasReleasedThisFrame);
            bool rightReleased = Input.GetMouseButtonUp(1) ||
                (UnityEngine.InputSystem.Mouse.current != null && UnityEngine.InputSystem.Mouse.current.rightButton.wasReleasedThisFrame);

            if (eReleased || rightReleased)
            {
                EndInteract();
            }
        }

        /// <summary>
        /// Handle WASD movement input.
        /// Replaces: control() function from UnitPlayer.as
        /// </summary>
        private void HandleMovementInput()
        {
            if (_input == null)
            {
                return;
            }

            Vector2 input = _input.CurrentMoveInput;

            if (_locomotion != null)
            {
                bool jumpHeld = _input.IsJumpHeld;
                bool runHeld = _input.IsDashing.Value;
                bool jumpPressed = _input.Jump != null && _input.Jump.WasPressedThisFrame();
                bool dashPressed = runHeld && DetectHorizontalDoubleTap(input.x);
                bool dropThroughPressed = DetectDownDoubleTap(input.y);
                // Provide mouse cursor world position for teleport targeting (AS3: World.w.celX/celY)
                Vector2 cursorWorldPixels = Vector2.zero;
                if (_mainCamera != null)
                {
                    Vector3 mouseWorld = _mainCamera.ScreenToWorldPoint(Input.mousePosition);
                    // Convert Unity world units to pixel space
                    cursorWorldPixels = new Vector2(mouseWorld.x * 100f, mouseWorld.y * 100f);
                }
                _locomotion.SetCursorWorldPixels(cursorWorldPixels);
                _locomotion.SetIntent(input, jumpHeld, runHeld, jumpPressed, dashPressed, dropThroughPressed, _aimAngle);
                _isGrounded = _locomotion.CurrentSnapshot.IsGrounded;
                _isRunning = _locomotion.CurrentSnapshot.IsRunning;
                return;
            }

            if (_tilePhysics != null)
            {
                // Route input through tile physics controller
                bool jump = Input.GetKey(KeyCode.Space) || Input.GetKey(KeyCode.W);
                bool down = Input.GetKey(KeyCode.S);
                _tilePhysics.SetInput(input.x, jump, down);

                // Sync grounded state from tile physics
                _isGrounded = _tilePhysics.IsGrounded;
                return;
            }

            // Fallback: original Rigidbody2D movement (if no TilePhysicsController)
            if (input == Vector2.zero)
            {
                _isRunning = false;
                return;
            }

            float targetSpeed = _isRunning ? base._stats.RunSpeed : base._stats.WalkSpeed;
            float acceleration = base._stats.Acceleration * Time.deltaTime * 50f;
            float targetVelocityX = input.x * targetSpeed;
            _velocity.x = Mathf.MoveTowards(_velocity.x, targetVelocityX, acceleration);
        }

        private bool DetectHorizontalDoubleTap(float horizontalInput)
        {
            const float threshold = 0.5f;
            float currentTime = Time.unscaledTime;
            bool dashTriggered = false;

            bool leftHeld = horizontalInput <= -threshold;
            bool rightHeld = horizontalInput >= threshold;

            if (leftHeld && !_wasLeftHeld)
            {
                dashTriggered = currentTime - _lastLeftTapTime <= _doubleTapWindowSeconds;
                _lastLeftTapTime = currentTime;
            }

            if (rightHeld && !_wasRightHeld)
            {
                dashTriggered = currentTime - _lastRightTapTime <= _doubleTapWindowSeconds;
                _lastRightTapTime = currentTime;
            }

            _wasLeftHeld = leftHeld;
            _wasRightHeld = rightHeld;
            return dashTriggered;
        }

        private bool DetectDownDoubleTap(float verticalInput)
        {
            const float threshold = 0.5f;
            float currentTime = Time.unscaledTime;
            bool downHeld = verticalInput <= -threshold;
            bool dropTriggered = false;

            if (downHeld && !_wasDownHeld)
            {
                dropTriggered = currentTime - _lastDownTapTime <= _doubleTapWindowSeconds;
                _lastDownTapTime = currentTime;
            }

            _wasDownHeld = downHeld;
            return dropTriggered;
        }

        /// <summary>
        /// Handle weapon aiming towards mouse cursor.
        /// Original PFE had weapons rotate around the player character.
        /// </summary>
        private void HandleAiming()
        {
            if (!_mouseAiming || _mainCamera == null) return;

            Vector3 mouseScreen = Input.mousePosition;
            Vector3 mouseWorld  = _mainCamera.ScreenToWorldPoint(mouseScreen);
            mouseWorld.z = 0f;

            Vector2 aimDirection = mouseWorld - transform.position;
            _aimAngle = Mathf.Atan2(aimDirection.y, aimDirection.x) * Mathf.Rad2Deg;

            // Push the aim target into the loadout; the controller and the presenter read it from there.
            _loadout?.SetAimTarget(mouseWorld);
        }

        /// <summary>Start attacking when attack button is pressed.</summary>
        private void HandleAttackStart()
        {
            // If the mouse is aimed directly at an interactable within reach, interact instead of
            // attacking. Cursor only — this must not fall back to the nearest target, or a click meant
            // as a shot would be swallowed by whatever happens to be standing next to the player.
            //
            // Routed through the same hold/immediate decision as the interact key, so a Z door needs
            // its hold here too rather than opening on the click. There is deliberately no matching
            // release: a hold begun by a click simply runs to completion, or is abandoned by walking
            // away (the range guard). Letting go of the attack button cancelling an E-initiated hold
            // would be a surprising coupling.
            if (BeginOrPerformInteract(FindCursorTarget()))
            {
                return;
            }

            _loadout?.BeginAttack();
        }

        /// <summary>Stop attacking when attack button is released.</summary>
        private void HandleAttackEnd()
        {
            _loadout?.EndAttack();
        }

        // ── Identity ──────────────────────────────────────────────────────────

        /// <summary>
        /// The player is player-controlled. AS3's <c>Unit.player</c>.
        ///
        /// <para><b>This was a latent lie until now:</b> <c>UnitController.IsPlayer</c> is
        /// <c>virtual … => false</c> and nothing overrode it, so every unit — the player included —
        /// reported <c>false</c>. Nothing in production read it, which is why it never showed, but
        /// AS3 branches on <c>_loc1_.player</c> in the explosion path
        /// (<c>weapon/Bullet.as:782</c>, <c>:817</c>) and that branch is exactly where the player's
        /// own-explosion multiplier lives.</para>
        /// </summary>
        public override bool IsPlayer => true;

        /// <summary>
        /// The player's faction. AS3 sets this in code, not in data — <c>UnitPlayer.as:385</c>
        /// <c>fraction = F_PLAYER</c> — because <c>littlepip</c> carries no <c>fraction</c> attribute
        /// in <c>AllData.as</c> at all. Overriding here rather than writing <c>Stats.fraction</c>
        /// keeps the shared <c>UnitDefinition</c> asset untouched.
        /// </summary>
        public override PFE.Data.Definitions.FactionType Faction => PFE.Data.Definitions.FactionType.Player;

        /// <summary>
        /// Apply damage to the player.
        /// Overrides base implementation to add player-specific death handling.
        /// </summary>
        public override void TakeDamage(float damage)
        {
            // Call base implementation (handles UnitStats damage and death check)
            base.TakeDamage(damage);

            // Player-specific damage feedback
            // TODO: Trigger damage feedback (screen shake, flash, etc.)
        }

        /// <summary>
        /// Override OnDeath to handle player-specific death logic.
        /// </summary>
        protected override void OnDeath()
        {
            base.OnDeath();

            // Player-specific death handling
            Debug.Log("[PlayerController] LittlePip has died!");

            // TODO: Show game over screen
            // TODO: Reload last save
        }

        /// <summary>
        /// Handle player death.
        /// </summary>
        private void Die()
        {
            Debug.Log("[Player] LittlePip has died!");

            // TODO: Show game over screen
            // TODO: Reload last save
        }

        // There is deliberately no serialized interaction radius any more. The reach is AS3's
        // World.w.actionDist, read from WorldConstants.ACTION_REACH by both this class and every
        // IInteractable, so a scene cannot widen it by accident and the two cannot drift apart.

        /// <summary>
        /// Resolves what the player is trying to act on, <b>without acting</b>: the interactable
        /// under the cursor, if one is within reach.
        ///
        /// <para><b>There is no nearest-target fallback, and adding one back is a bug.</b> AS3
        /// requires the cursor to be on the object — <c>UnitPlayer.actAction</c> tests
        /// <c>loc.celObj &amp;&amp; loc.celObj.onCursor &amp;&amp; loc.celDist &lt;= World.w.actionDist</c>
        /// (<c>UnitPlayer.as:1931</c>) — and when nothing is under the cursor it does nothing at all.
        /// The old <c>?? FindNearestTarget()</c> here made <c>E</c> work from anywhere near a door
        /// regardless of where the player was pointing, which is not a behaviour AS3 has anywhere in
        /// the interaction path.</para>
        ///
        /// <para>Split out of the old <c>HandleInteract</c> because the press has to <i>decide</i>
        /// before it acts: a target that authors a hold must not be interacted with on the press —
        /// AS3 arms <c>t_action</c> and returns (<c>UnitPlayer.as:1944-1988</c>), and the effect lands
        /// only from the timer's completion branch (<c>:1081-1086</c>).</para>
        /// </summary>
        private IInteractable ResolveInteractTarget()
        {
            return FindCursorTarget();
        }

        /// <summary>
        /// The interactable under the mouse cursor, if one is within reach. AS3:
        /// <c>loc.celObj &amp;&amp; loc.celObj.onCursor</c> (<c>UnitPlayer.as:1931</c>).
        /// </summary>
        private IInteractable FindCursorTarget()
        {
            if (_mainCamera == null)
            {
                _mainCamera = Camera.main;
                if (_mainCamera == null) _mainCamera = FindFirstObjectByType<Camera>();
            }
            if (_mainCamera == null) return null;

            Vector3 mouseScreen = Input.mousePosition;
            if (!_mainCamera.orthographic)
            {
                mouseScreen.z = -_mainCamera.transform.position.z;
            }
            Vector3 mouseWorld = _mainCamera.ScreenToWorldPoint(mouseScreen);
            mouseWorld.z = 0f;
            Vector2 playerPos = transform.position;

            Collider2D[] cursorHits = Physics2D.OverlapCircleAll(mouseWorld, CursorHitRadius);
            for (int i = 0; i < cursorHits.Length; i++)
            {
                Collider2D hit = cursorHits[i];
                if (hit == null || hit.gameObject == gameObject) continue;

                IInteractable cursorInteractable = hit.GetComponent<IInteractable>() ?? hit.GetComponentInParent<IInteractable>();
                if (cursorInteractable != null && cursorInteractable.CanInteract(gameObject))
                {
                    Vector2 closestPt = hit.ClosestPoint(playerPos);
                    float distSq = (closestPt - playerPos).sqrMagnitude;
                    if (distSq <= WorldConstants.ACTION_REACH * WorldConstants.ACTION_REACH)
                    {
                        return cursorInteractable;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Radius of the cursor probe, in world units. AS3 asks whether the <i>cell</i> under the
        /// cursor is the one holding the object (<c>loc.celObj.onCursor</c>), and a cell is
        /// <c>World.tileX</c> = 40 source pixels = 0.4 world units. A 0.45 probe covers that cell
        /// with a hair of slack for small props whose collider sits inside the tile.
        /// </summary>
        private const float CursorHitRadius = 0.45f;

        /// <summary>
        /// The action key went down.
        /// </summary>
        private void HandleInteract()
        {
            // Guarded per frame. Both the MessagePipe subscription and the direct poll in Update
            // deliver the same press, and an instant action must not run twice for one key press —
            // which for a door means open-then-close in a single frame.
            if (Time.frameCount == _lastInteractPressFrame) return;
            _lastInteractPressFrame = Time.frameCount;

            // A hold in flight owns the action: AS3 will not start a second while actionObj is set —
            // actAction() only re-checks the incumbent (:1924-1930).
            if (_actionInteractor != null && _actionInteractor.IsHolding) return;

            BeginOrPerformInteract(ResolveInteractTarget());
        }

        /// <summary>
        /// Acts on <paramref name="target"/>: starts a hold if it authors a duration, otherwise
        /// interacts at once — AS3's zero-time branch (<c>UnitPlayer.as:1985-1988</c>).
        ///
        /// <para>Shared by the interact key and by <see cref="HandleAttackStart"/>'s
        /// "clicking an interactable interacts instead of attacking" path, so both obey the same rule.
        /// Splitting them is how a held Z door would open instantly on a left-click while needing a
        /// third of a second on E.</para>
        /// </summary>
        /// <returns>True when the target was consumed, so the caller must not also act.</returns>
        private bool BeginOrPerformInteract(IInteractable target)
        {
            if (target == null)
            {
                return false;
            }

            if (target is IHoldInteractable hold && hold.HoldFrames > 0)
            {
                if (_actionInteractor != null &&
                    _actionInteractor.TryBeginHold(hold, gameObject, hold.WorldPosition))
                {
                    return true;
                }
            }

            target.Interact(gameObject);
            return true;
        }

        /// <summary>
        /// The action key came up: abandon any hold in flight. Nothing fires.
        ///
        /// <para>This is AS3's release path — a false <c>keyAction</c> nulls <c>actionObj</c> at
        /// <c>UnitPlayer.as:2131-2135</c> — and it is why letting go of E half-way through a Z door
        /// does nothing at all rather than finishing.</para>
        /// </summary>
        private void EndInteract()
        {
            _actionInteractor?.ReleaseHold();
        }

        // Public getters

        public new UnitStats Stats => base._unitStats;
        public bool IsRunning => _locomotion != null ? _locomotion.CurrentSnapshot.IsRunning : _isRunning;
    }
}
