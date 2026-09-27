using UnityEngine;
using VContainer;
using R3;
using MessagePipe;
using PFE.Core.Input;
using PFE.Core.Messages;
using PFE.Entities.Units;
using PFE.Entities.Weapons;
using PFE.Systems.Combat;
using PFE.Systems.Interaction;
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
        [Tooltip("Weapon definition asset for the player's starting weapon")]
        private PFE.Data.Definitions.WeaponDefinition _startingWeaponDef;

        [SerializeField]
        [Tooltip("Should the player always face the mouse cursor?")]
        private bool _mouseAiming = true;

        [SerializeField]
        [Tooltip("Maximum time between directional taps for dash/drop-through detection.")]
        private float _doubleTapWindowSeconds = 0.25f;
        private TilePhysicsController _tilePhysics;
        private PlayerLocomotionController _locomotion;
        // Dependencies (injected via VContainer)
        private InputReader _input;
        private IWeaponFactory _weaponFactory;
        private PFE.Core.PfeDebugSettings _debugSettings;

        // New weapon system — primary path.
        private PlayerWeaponLoadout _loadout;

        // Legacy weapon components — kept until WeaponLogic.cs / WeaponView.cs are deleted.
        // All code paths null-guard these; _loadout takes priority when present.
        private WeaponLogic _weaponLogic;
        private WeaponView _weaponView;

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
            IWeaponFactory weaponFactory,
            ISubscriber<AttackMessage> attackSubscriber,
            ISubscriber<TeleportMessage> teleportSubscriber,
            ISubscriber<InteractMessage> interactSubscriber,
            PFE.Core.PfeDebugSettings debugSettings)
        {
            _input = input;
            _weaponFactory = weaponFactory;
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
            }).AddTo(_disposables);
        }

        protected override void Awake()
        {
            base.Awake();
            _mainCamera = Camera.main;
            _tilePhysics = GetComponent<TilePhysicsController>();
            _locomotion = GetComponent<PlayerLocomotionController>();
            _loadout = GetComponent<PlayerWeaponLoadout>();
            base._unitStats = new UnitStats();

            if (_locomotion != null)
                _locomotion.SetUnitStats(base._unitStats);
        }

        private void Start()
        {
            // VContainer injects [Inject] methods between Awake and Start,
            // so _weaponFactory is guaranteed to be set here.
            //
            // If PlayerWeaponLoadout is present it owns weapon initialization —
            // skip the legacy InitializeWeapon() path.
            if (_loadout == null)
                InitializeWeapon();
        }

        /// <summary>
        /// Initialize weapon using factory pattern.
        /// Creates both WeaponLogic and WeaponView, then links them.
        /// </summary>
        private void InitializeWeapon()
        {
            if (_startingWeaponDef == null)
            {
                Debug.LogWarning("[PlayerController] No starting weapon definition assigned");
                return;
            }
            if (_debugSettings?.LogWeaponLifecycle == true)
                Debug.Log($"[PlayerController] InitializeWeapon() — factory={_weaponFactory != null}, def={_startingWeaponDef.weaponId}.");

            // Create WeaponLogic (pure C# class)
            _weaponLogic = _weaponFactory.CreateWeaponLogic(_startingWeaponDef);

            // Create or find WeaponView (MonoBehaviour component)
            // Look for existing WeaponView component in children
            _weaponView = GetComponentInChildren<WeaponView>();

            if (_weaponView == null)
            {
                // Create new weapon GameObject as child of player
                GameObject weaponObj = new GameObject("Weapon");
                weaponObj.transform.SetParent(transform);
                weaponObj.transform.localPosition = Vector3.zero;

                // Add WeaponView component
                _weaponView = weaponObj.AddComponent<WeaponView>();
            }

            // Link WeaponView with WeaponLogic
            _weaponView.Initialize(_weaponLogic, base._unitStats);
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

            // Direct interact check (E key or Right-Click)
            bool ePressed = Input.GetKeyDown(KeyCode.E) ||
                (UnityEngine.InputSystem.Keyboard.current != null && UnityEngine.InputSystem.Keyboard.current.eKey.wasPressedThisFrame);
            bool rightClick = Input.GetMouseButtonDown(1) ||
                (UnityEngine.InputSystem.Mouse.current != null && UnityEngine.InputSystem.Mouse.current.rightButton.wasPressedThisFrame);

            if (ePressed || rightClick)
            {
                HandleInteract();
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

            // New path: push aim target into the loadout (controller + presenter read it).
            if (_loadout != null)
            {
                _loadout.SetAimTarget(mouseWorld);
                return;
            }

            // Legacy fallback.
            _weaponView?.RotateTowards(mouseWorld);
        }

        /// <summary>Start attacking when attack button is pressed.</summary>
        private void HandleAttackStart()
        {
            // If mouse is aimed directly at an interactable within reach, interact instead of attacking
            if (TryCursorInteract())
            {
                return;
            }

            if (_loadout != null) { _loadout.BeginAttack(); return; }
            _weaponView?.BeginFiring();
        }

        /// <summary>Stop attacking when attack button is released.</summary>
        private void HandleAttackEnd()
        {
            if (_loadout != null) { _loadout.EndAttack(); return; }
            _weaponView?.EndFiring();
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

        [Header("Interaction")]
        [SerializeField]
        [Tooltip("Maximum radius to scan for interactable objects (AS3 World.w.actionDist: 200px = 2.0m).")]
        private float _interactionRadius = 2.0f;

        /// <summary>
        /// Attempt to interact with whatever is under the mouse cursor within reach.
        /// </summary>
        private bool TryCursorInteract()
        {
            if (_mainCamera == null)
            {
                _mainCamera = Camera.main;
                if (_mainCamera == null) _mainCamera = FindFirstObjectByType<Camera>();
            }
            if (_mainCamera == null) return false;

            Vector3 mouseScreen = Input.mousePosition;
            if (!_mainCamera.orthographic)
            {
                mouseScreen.z = -_mainCamera.transform.position.z;
            }
            Vector3 mouseWorld = _mainCamera.ScreenToWorldPoint(mouseScreen);
            mouseWorld.z = 0f;
            Vector2 playerPos = transform.position;

            Collider2D[] cursorHits = Physics2D.OverlapCircleAll(mouseWorld, 0.45f);
            for (int i = 0; i < cursorHits.Length; i++)
            {
                Collider2D hit = cursorHits[i];
                if (hit == null || hit.gameObject == gameObject) continue;

                IInteractable cursorInteractable = hit.GetComponent<IInteractable>() ?? hit.GetComponentInParent<IInteractable>();
                if (cursorInteractable != null && cursorInteractable.CanInteract(gameObject))
                {
                    Vector2 closestPt = hit.ClosestPoint(playerPos);
                    float distSq = (closestPt - playerPos).sqrMagnitude;
                    if (distSq <= 6.25f) // 2.5m (AS3 actionDist = 200px = 2.0m)
                    {
                        cursorInteractable.Interact(gameObject);
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Scan for interactable objects (doors, containers, switches) and trigger interaction.
        /// Prioritizes interactable under cursor (AS3: loc.celObj && loc.celObj.onCursor),
        /// falling back to the closest interactable in radius.
        /// </summary>
        private void HandleInteract()
        {
            Vector2 playerPos = transform.position;

            // 1. Prioritize interactable under mouse cursor if within reach
            if (TryCursorInteract())
            {
                return;
            }

            // 2. Fall back to closest interactable in proximity
            Collider2D[] hits = Physics2D.OverlapCircleAll(playerPos, _interactionRadius);
            IInteractable bestTarget = null;
            float bestDistSq = float.MaxValue;

            for (int i = 0; i < hits.Length; i++)
            {
                Collider2D hit = hits[i];
                if (hit == null || hit.gameObject == gameObject) continue;

                IInteractable interactable = hit.GetComponent<IInteractable>() ?? hit.GetComponentInParent<IInteractable>();
                if (interactable != null && interactable.CanInteract(gameObject))
                {
                    Vector2 closestPt = hit.ClosestPoint(playerPos);
                    float distSq = (closestPt - playerPos).sqrMagnitude;
                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        bestTarget = interactable;
                    }
                }
            }

            if (bestTarget != null)
            {
                bestTarget.Interact(gameObject);
            }
        }

        // Public getters

        public new UnitStats Stats => base._unitStats;
        public bool IsRunning => _locomotion != null ? _locomotion.CurrentSnapshot.IsRunning : _isRunning;
    }
}
