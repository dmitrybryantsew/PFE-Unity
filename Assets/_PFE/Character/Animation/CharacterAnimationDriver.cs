using System;
using System.Collections.Generic;
using PFE.Data.Definitions;
using PFE.Entities.Player;
using UnityEngine;

namespace PFE.Character.Animation
{
    /// <summary>
    /// FSM that maps PlayerMovementSnapshot → CharacterSpriteAssembler.SetState / AdvanceFrame.
    ///
    /// Architecture — Option C (state classes):
    ///   • Locomotion states (priority 10–110) evaluate continuously from PlayerMovementSnapshot.
    ///   • Action states (priority 200–250) are activated externally via TriggerAction().
    ///   • A locked state (IsLocked=true) blocks re-evaluation until its clip finishes.
    ///   • TriggerAction always breaks any active lock so higher-priority actions (die) can
    ///     always interrupt lower ones (punch).
    ///
    /// Setup:
    ///   1. Add CharacterSpriteAssembler to the player GameObject.
    ///   2. Add PlayerCharacterVisual to the player GameObject and assign definition/styleData.
    ///   3. Add this component to the player GameObject.
    ///   4. Optionally assign an IAnimationEventSink implementor for sounds.
    ///
    /// Execution order: runs at -50, after PlayerLocomotionController (-200), so
    /// CurrentSnapshot is already populated each FixedUpdate.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    [DisallowMultipleComponent]
    public sealed class CharacterAnimationDriver : MonoBehaviour
    {
        // ── Inspector ────────────────────────────────────────────────────────

        [Header("References")]
        [Tooltip("Assign if the assembler is on a child object; otherwise auto-found on this GO.")]
        [SerializeField] CharacterSpriteAssembler _assembler;

        [Tooltip("MonoBehaviour on this or another GO that implements IAnimationEventSink (sounds/VFX).")]
        [SerializeField] MonoBehaviour _eventSinkObject;

        [Header("Stay aim sub-frames")]
        [Tooltip("First frame of the standing aim sub-range in the 'stay' clip. Set -1 to disable.")]
        [SerializeField] int _aimIdleStartFrame = -1;
        [SerializeField] int _aimIdleEndFrame   = -1;

        [Header("Jump frame mapping")]
        [Tooltip("Vertical velocity (Unity units/sec) that maps to the first jump frame (peak of arc).")]
        [SerializeField] float _jumpMaxRiseSpeed = 9f;
        [Tooltip("Vertical velocity magnitude that maps to the last jump frame (max fall speed).")]
        [SerializeField] float _jumpMaxFallSpeed = 12f;

        [Header("Free idle")]
        [Tooltip("Seconds of standing-still required before a random free idle triggers.")]
        [SerializeField] float _freeIdleThresholdSeconds = 5f;
        [Tooltip("Maximum seconds a free idle plays before returning to stay.")]
        [SerializeField] float _freeIdleMaxPlaySeconds   = 3.5f;

        // ── Private state ────────────────────────────────────────────────────

        PlayerLocomotionController _locomotion;
        IAnimationEventSink        _eventSink;

        List<IAnimationState> _states;
        IAnimationState       _activeState;
        bool                  _isLocked;
        bool                  _forceReEvaluate;

        // Action states kept as typed fields so TriggerAction can call Activate() on them.
        DieAnimState   _dieState;
        ResAnimState   _resState;
        PunchAnimState _punchState;
        KickAnimState  _kickState;
        DergAnimState  _dergState;
        LurkAnimState  _lurkState;

        // 24-fps animation tick cadence (matches original Flash framerate).
        // Overridden at runtime by definition.frameRate if available.
        float _frameDuration = 1f / 24f;
        float _frameTimer;

        // ── Unity lifecycle ──────────────────────────────────────────────────

        void Awake()
        {
            _locomotion = GetComponent<PlayerLocomotionController>();
            if (_locomotion == null)
                Debug.LogError("[CharacterAnimationDriver] No PlayerLocomotionController found.", this);

            if (_assembler == null)
                _assembler = GetComponent<CharacterSpriteAssembler>();
            if (_assembler == null)
                Debug.LogError("[CharacterAnimationDriver] No CharacterSpriteAssembler found.", this);

            if (_eventSinkObject != null)
                _eventSink = _eventSinkObject as IAnimationEventSink;
        }

        void Start()
        {
            if (_assembler?.Definition != null)
                _frameDuration = 1f / Mathf.Max(1f, _assembler.Definition.frameRate);

            BuildStates();
        }

        void FixedUpdate()
        {
            if (_assembler == null || _locomotion == null) return;

            PlayerMovementSnapshot snapshot = _locomotion.CurrentSnapshot;

            // ── Lock management ──────────────────────────────────────────────
            if (_isLocked && !_forceReEvaluate)
            {
                if (IsCurrentClipFinished())
                {
                    // Clip ended naturally — unlock and fall through to re-evaluate.
                    string nextName = "(unlocked)";
                    _activeState?.OnExit(nextName, _eventSink);
                    _activeState = null;
                    _isLocked    = false;
                }
                else
                {
                    // Stay locked; only tick the frame.
                    TickFrame(snapshot);
                    return;
                }
            }

            _forceReEvaluate = false;

            // ── State evaluation ─────────────────────────────────────────────
            IAnimationState best = EvaluateBestState(snapshot);
            if (best == null) { TickFrame(snapshot); return; }

            string bestName = ValidatedStateName(best.GetStateName(snapshot));
            if (bestName == null) bestName = "stay"; // definition fallback

            if (bestName != _assembler.CurrentState)
            {
                // Transition
                _activeState?.OnExit(bestName, _eventSink);
                best.OnEnter(_assembler.CurrentState, _eventSink);
                _assembler.SetState(bestName, 0);
                _activeState = best;
                _isLocked    = best.IsLocked;
                _frameTimer  = 0f;
            }
            else if (_activeState != best)
            {
                // Same clip name but different state object (shouldn't normally happen, but guard it).
                _activeState = best;
            }

            TickFrame(snapshot);
        }

        // ── Public API ───────────────────────────────────────────────────────

        /// <summary>
        /// Trigger a game-event-driven action (death, melee, lurk, etc.).
        /// Always interrupts any currently locked lower-priority state.
        /// </summary>
        public void TriggerAction(AnimationAction action, int lurkVariant = 0)
        {
            switch (action)
            {
                case AnimationAction.Die:      _dieState.Activate(false);         break;
                case AnimationAction.DieAlt:   _dieState.Activate(true);          break;
                case AnimationAction.Resurrect: _resState.Activate();             break;
                case AnimationAction.Punch:    _punchState.Activate();             break;
                case AnimationAction.Kick:     _kickState.Activate();              break;
                case AnimationAction.Stagger:  _dergState.Activate();              break;
                case AnimationAction.Lurk:     _lurkState.Activate(lurkVariant);  break;
                case AnimationAction.Unlurk:   _lurkState.Unlurk();               break;
            }
            _forceReEvaluate = true;
        }

        /// <summary>Replace the event sink at runtime (e.g. after a scene AudioManager awakens).</summary>
        public void SetEventSink(IAnimationEventSink sink) => _eventSink = sink;

        // ── Internal ─────────────────────────────────────────────────────────

        void BuildStates()
        {
            // Locomotion states — continuously driven by PlayerMovementSnapshot.
            var stay       = new StayAnimState(_aimIdleStartFrame, _aimIdleEndFrame);
            var freeIdle   = new FreeIdleAnimState(_freeIdleThresholdSeconds, _freeIdleMaxPlaySeconds);
            var walk       = new WalkAnimState();
            var trot       = new TrotAnimState();
            var run        = new RunAnimState();
            var jump       = new JumpAnimState(_jumpMaxRiseSpeed, _jumpMaxFallSpeed);
            var crouchIdle = new CrouchIdleAnimState();
            var polz       = new PolzAnimState();
            var swim       = new SwimAnimState();
            var levitate   = new LevitateAnimState();
            var ladder     = new LadderAnimState();
            var teleport   = new TeleportAnimState();
            var pinok      = new PinokAnimState();

            // Action states — activated externally via TriggerAction().
            _dieState   = new DieAnimState();
            _resState   = new ResAnimState();
            _punchState = new PunchAnimState();
            _kickState  = new KickAnimState();
            _dergState  = new DergAnimState();
            _lurkState  = new LurkAnimState();

            _states = new List<IAnimationState>
            {
                _dieState,   // 250
                _resState,   // 240
                _punchState, // 230
                _kickState,  // 220
                _dergState,  // 210
                _lurkState,  // 200
                teleport,    // 120
                pinok,       // 110
                ladder,      // 100
                levitate,    //  90
                swim,        //  80
                polz,        //  70
                crouchIdle,  //  60
                jump,        //  50
                run,         //  40
                trot,        //  30
                walk,        //  20
                freeIdle,    //  15
                stay,        //  10
            };

            // Ensure sorted descending even if the list above is reordered.
            _states.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        }

        IAnimationState EvaluateBestState(in PlayerMovementSnapshot snapshot)
        {
            foreach (IAnimationState state in _states)
            {
                if (state.CanEnter(snapshot))
                    return state;
            }
            return null;
        }

        void TickFrame(in PlayerMovementSnapshot snapshot)
        {
            _frameTimer += Time.fixedDeltaTime;
            while (_frameTimer >= _frameDuration)
            {
                _frameTimer -= _frameDuration;
                ApplyFrame(snapshot);
            }
        }

        void ApplyFrame(in PlayerMovementSnapshot snapshot)
        {
            if (_assembler == null) return;

            string stateName  = _assembler.CurrentState;
            int    frameCount = GetFrameCount(stateName);
            int    current    = _assembler.CurrentFrame;

            int overrideFrame = _activeState?.OverrideFrame(snapshot, current, frameCount) ?? -1;

            if (overrideFrame >= 0)
            {
                if (overrideFrame != current)
                    _assembler.SetState(stateName, overrideFrame);
            }
            else
            {
                _assembler.AdvanceFrame();
            }

            _activeState?.OnFrameChanged(_assembler.CurrentFrame, snapshot, _eventSink);

            // Dash start — fired here (one-shot edge) rather than in the state itself.
            if (snapshot.IsDashing && !_wasDashing)
                _eventSink?.FireEvent(AnimEvent.Dash, snapshot.Position);
            _wasDashing = snapshot.IsDashing;
        }

        bool _wasDashing;

        bool IsCurrentClipFinished()
        {
            if (_assembler == null) return true;
            string stateName = _assembler.CurrentState;
            if (string.IsNullOrEmpty(stateName)) return true;

            CharacterStateClip clip = _assembler.Definition?.GetStateClip(stateName);
            if (clip == null) return true;

            // Only ClampForever clips have a natural end.
            return clip.loopMode == AnimationLoopMode.ClampForever
                   && _assembler.CurrentFrame >= clip.frameCount - 1;
        }

        /// <summary>
        /// Returns name if it exists in the definition, else null.
        /// The driver falls back to "stay" when null is returned.
        /// </summary>
        string ValidatedStateName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            return _assembler?.Definition?.GetStateClip(name) != null ? name : null;
        }

        int GetFrameCount(string stateName)
        {
            if (string.IsNullOrEmpty(stateName)) return 1;
            return _assembler?.Definition?.GetStateClip(stateName)?.frameCount ?? 1;
        }
    }
}
