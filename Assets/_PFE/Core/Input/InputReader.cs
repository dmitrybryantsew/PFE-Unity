using UnityEngine;
using UnityEngine.InputSystem;
using System;
using R3;
using MessagePipe;
using PFE.Core;
using PFE.Core.Messages;

namespace PFE.Core.Input
{
    /// <summary>
    /// Input system wrapper using Unity's new Input System.
    /// Replaces Ctr.as from ActionScript which used Keyboard.KEY polling.
    /// Uses Reactive Properties (R3) for UI binding and MessagePipe for gameplay logic.
    ///
    /// Advantages over original AS3:
    /// - No more manual key polling every frame
    /// - Automatic rebinding support
    /// - Works with gamepad, keyboard, mouse simultaneously
    /// - Reactive properties for UI (no Update() loops needed)
    /// - MessagePipe for decoupled event communication
    /// </summary>
    public class InputReader : IDisposable
    {
        private InputActionMap _gameplayMap;

        // Actions
        public InputAction Move { get; private set; }
        public InputAction Jump { get; private set; }
        public InputAction Attack { get; private set; }
        public InputAction Interact { get; private set; }
        public InputAction Dash { get; private set; }
        public InputAction Teleport { get; private set; }

        // Reactive Properties (for UI binding with R3)
        public readonly ReactiveProperty<Vector2> MoveInput = new(Vector2.zero);
        public readonly ReactiveProperty<bool> IsJumping = new(false);
        public readonly ReactiveProperty<bool> IsAttacking = new(false);
        public readonly ReactiveProperty<bool> IsDashing = new(false);

        // MessagePipe Publishers (for gameplay logic)
        private readonly IPublisher<JumpMessage> _jumpPublisher;
        private readonly IPublisher<AttackMessage> _attackPublisher;
        private readonly IPublisher<InteractMessage> _interactPublisher;
        private readonly IPublisher<DashMessage> _dashPublisher;
        private readonly IPublisher<TeleportMessage> _teleportPublisher;
        private readonly PfeDebugSettings _debugSettings;

        // Constructor with dependency injection for MessagePipe publishers
        public InputReader(
            IPublisher<JumpMessage> jumpPublisher,
            IPublisher<AttackMessage> attackPublisher,
            IPublisher<InteractMessage> interactPublisher,
            IPublisher<DashMessage> dashPublisher,
            IPublisher<TeleportMessage> teleportPublisher,
            PfeDebugSettings debugSettings,
            PfeInputSettings settings = null)
        {
            _jumpPublisher = jumpPublisher;
            _attackPublisher = attackPublisher;
            _interactPublisher = interactPublisher;
            _dashPublisher = dashPublisher;
            _teleportPublisher = teleportPublisher;
            _debugSettings = debugSettings;

            _gameplayMap = new InputActionMap("Gameplay");

            BuildBindings(settings);
            SetupEventSubscriptions();
            _gameplayMap.Enable();
        }

        private void BuildBindings(PfeInputSettings s)
        {
            var m = s?.move;

            // Movement: WASD + Arrow keys + Gamepad left stick
            Move = _gameplayMap.AddAction("Move", InputActionType.Value);
            Move.AddCompositeBinding("2DVector")
                .With("Up",    m?.kbUp    ?? "<Keyboard>/w")
                .With("Down",  m?.kbDown  ?? "<Keyboard>/s")
                .With("Left",  m?.kbLeft  ?? "<Keyboard>/a")
                .With("Right", m?.kbRight ?? "<Keyboard>/d");
            Move.AddCompositeBinding("2DVector")
                .With("Up",    m?.altUp    ?? "<Keyboard>/upArrow")
                .With("Down",  m?.altDown  ?? "<Keyboard>/downArrow")
                .With("Left",  m?.altLeft  ?? "<Keyboard>/leftArrow")
                .With("Right", m?.altRight ?? "<Keyboard>/rightArrow");
            Move.AddCompositeBinding("2DVector")
                .With("Up",    "<Gamepad>/leftStick/up")
                .With("Down",  "<Gamepad>/leftStick/down")
                .With("Left",  "<Gamepad>/leftStick/left")
                .With("Right", "<Gamepad>/leftStick/right");

            // Single-button actions
            Jump = BuildButtonAction("Jump", s?.jump,
                "<Keyboard>/space", "<Keyboard>/ctrl", "<Gamepad>/buttonSouth");

            Attack = BuildButtonAction("Attack", s?.attack,
                "<Mouse>/leftButton", "", "<Gamepad>/buttonRightShoulder");

            Interact = BuildButtonAction("Interact", s?.interact,
                "<Keyboard>/e", "", "<Gamepad>/buttonWest");

            Dash = BuildButtonAction("Dash", s?.dash,
                "<Keyboard>/leftShift", "", "<Gamepad>/buttonEast");

            Teleport = BuildButtonAction("Teleport", s?.teleport,
                "<Keyboard>/q", "", "<Gamepad>/leftShoulder");
        }

        private InputAction BuildButtonAction(string name, ButtonBinding b,
            string defaultKb, string defaultAlt, string defaultGp)
        {
            var action = _gameplayMap.AddAction(name, InputActionType.Button);

            string kb  = !string.IsNullOrEmpty(b?.keyboard)    ? b.keyboard    : defaultKb;
            string alt = !string.IsNullOrEmpty(b?.altKeyboard) ? b.altKeyboard : defaultAlt;
            string gp  = !string.IsNullOrEmpty(b?.gamepad)     ? b.gamepad     : defaultGp;

            if (!string.IsNullOrEmpty(kb))  action.AddBinding(kb);
            if (!string.IsNullOrEmpty(alt)) action.AddBinding(alt);
            if (!string.IsNullOrEmpty(gp))  action.AddBinding(gp);

            return action;
        }

        private void SetupEventSubscriptions()
        {
            // Movement - continuous value
            Move.performed += ctx =>
            {
                Vector2 input = ctx.ReadValue<Vector2>();
                MoveInput.Value = input;
            };
            Move.canceled += ctx =>
            {
                MoveInput.Value = Vector2.zero;
            };

            // Jump - started/canceled
            Jump.started += _ =>
            {
                IsJumping.Value = true;
                _jumpPublisher.Publish(new JumpMessage { IsStarted = true });
            };
            Jump.canceled += _ =>
            {
                IsJumping.Value = false;
                _jumpPublisher.Publish(new JumpMessage { IsStarted = false });
            };

            // Attack - started/canceled
            Attack.started += _ =>
            {
                IsAttacking.Value = true;
                if (_debugSettings?.LogInputActionEvents == true)
                    Debug.Log("[InputReader] Attack started — publishing AttackMessage.");
                _attackPublisher.Publish(new AttackMessage { IsStarted = true });
            };
            Attack.canceled += _ =>
            {
                IsAttacking.Value = false;
                if (_debugSettings?.LogInputActionEvents == true)
                    Debug.Log("[InputReader] Attack canceled — publishing AttackMessage.");
                _attackPublisher.Publish(new AttackMessage { IsStarted = false });
            };

            // Interact - pressed
            Interact.performed += _ =>
            {
                _interactPublisher.Publish(new InteractMessage { IsPressed = true });
            };

            // Dash - pressed
            Dash.started += _ =>
            {
                IsDashing.Value = true;
                _dashPublisher.Publish(new DashMessage { IsStarted = true });
            };
            Dash.canceled += _ =>
            {
                IsDashing.Value = false;
            };

            // Teleport - hold to charge, release to execute (AS3: keyTele)
            Teleport.started += _ =>
            {
                _teleportPublisher.Publish(new TeleportMessage { IsStarted = true });
            };
            Teleport.canceled += _ =>
            {
                _teleportPublisher.Publish(new TeleportMessage { IsStarted = false });
            };
        }

        /// <summary>
        /// Cleanup - disable and dispose input actions.
        /// </summary>
        public void Dispose()
        {
            _gameplayMap.Disable();
            _gameplayMap.Dispose();

            // Clean up reactive properties
            MoveInput.Dispose();
            IsJumping.Dispose();
            IsAttacking.Dispose();
            IsDashing.Dispose();
        }

        // Helper properties for polling if needed (though events are preferred)

        /// <summary>
        /// Current movement input vector.
        /// </summary>
        public Vector2 CurrentMoveInput => MoveInput.Value;

        /// <summary>
        /// Is the attack button being held down?
        /// </summary>
        public bool IsAttackHeld => IsAttacking.Value;

        /// <summary>
        /// Is the jump button being pressed?
        /// </summary>
        public bool IsJumpHeld => IsJumping.Value;
    }
}
