using System;
using PFE.Entities.Player;
using UnityEngine;
using Random = UnityEngine.Random;

namespace PFE.Character.Animation
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Stay (idle)           Priority 10 — default / always-valid fallback
    // ─────────────────────────────────────────────────────────────────────────

    public sealed class StayAnimState : IAnimationState
    {
        readonly int _aimStartFrame; // -1 = aim mapping disabled
        readonly int _aimEndFrame;

        /// <param name="aimStartFrame">First frame of the standing-aim sub-range (-1 to disable).</param>
        /// <param name="aimEndFrame">Last frame (inclusive) of the standing-aim sub-range.</param>
        public StayAnimState(int aimStartFrame = -1, int aimEndFrame = -1)
        {
            _aimStartFrame = aimStartFrame;
            _aimEndFrame   = aimEndFrame;
        }

        public int  Priority  => 10;
        public bool IsLocked  => false;

        public bool   CanEnter(in PlayerMovementSnapshot s)                              => true;
        public string GetStateName(in PlayerMovementSnapshot s)                          => "stay";

        public int OverrideFrame(in PlayerMovementSnapshot s, int currentFrame, int frameCount)
        {
            // When aim mapping is configured, drive the frame from aim angle each tick.
            // AimAngle: -90 = up, 0 = horizontal, +90 = down  →  aimStartFrame..aimEndFrame
            if (_aimStartFrame >= 0 && _aimEndFrame > _aimStartFrame)
            {
                float t = Mathf.InverseLerp(-90f, 90f, s.AimAngle);
                return Mathf.Clamp(
                    Mathf.RoundToInt(Mathf.Lerp(_aimStartFrame, _aimEndFrame, t)),
                    _aimStartFrame, _aimEndFrame);
            }
            // No aim mapping: hold frame 0 (static idle pose).
            return 0;
        }

        public void OnEnter(string prev, IAnimationEventSink e) { }
        public void OnExit(string next,  IAnimationEventSink e) { }
        public void OnFrameChanged(int frame, in PlayerMovementSnapshot s, IAnimationEventSink e) { }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Free Idle variants (free1 / free2 / free3)
    //  Priority 15 — activates after idle timeout; naturally interrupted by movement
    //  because Walk/Trot/Run have higher priority and will take over.
    // ─────────────────────────────────────────────────────────────────────────

    public sealed class FreeIdleAnimState : IAnimationState
    {
        readonly float _thresholdSeconds; // seconds of idle before triggering
        readonly float _maxPlaySeconds;   // hard cap on how long a free idle plays

        float  _idleStartTime = float.MaxValue;
        bool   _shouldPlay;
        string _chosenState;
        float  _playEndTime;

        public FreeIdleAnimState(float thresholdSeconds = 5f, float maxPlaySeconds = 3.5f)
        {
            _thresholdSeconds = thresholdSeconds;
            _maxPlaySeconds   = maxPlaySeconds;
        }

        public int  Priority  => 15;
        public bool IsLocked  => false; // Higher-priority locomotion states interrupt naturally.

        public bool CanEnter(in PlayerMovementSnapshot s)
        {
            bool isIdle = s.NormalizedMoveSpeed < 0.01f
                          && s.LocomotionState == PlayerLocomotionState.Grounded;

            // Expire by time
            if (_shouldPlay && Time.fixedTime >= _playEndTime)
                Reset();

            if (_shouldPlay) return true;

            if (!isIdle)
            {
                _idleStartTime = float.MaxValue;
                return false;
            }

            if (_idleStartTime == float.MaxValue)
                _idleStartTime = Time.fixedTime;

            if (Time.fixedTime - _idleStartTime >= _thresholdSeconds)
            {
                _shouldPlay  = true;
                _chosenState = "free" + Random.Range(1, 4); // free1, free2, or free3
                _playEndTime = Time.fixedTime + _maxPlaySeconds;
                _idleStartTime = float.MaxValue;
            }

            return _shouldPlay;
        }

        public string GetStateName(in PlayerMovementSnapshot s) => _chosenState ?? "stay";

        public int  OverrideFrame(in PlayerMovementSnapshot s, int cf, int fc) => -1;
        public void OnEnter(string prev, IAnimationEventSink e) { }
        public void OnExit(string next,  IAnimationEventSink e) => Reset();
        public void OnFrameChanged(int frame, in PlayerMovementSnapshot s, IAnimationEventSink e) { }

        void Reset() { _shouldPlay = false; _chosenState = null; }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Walk   Priority 20
    //  Original: 26-frame loop. Footfalls from AS3 UnitPon.as sndStep param2=4 (24-frame cycle).
    // ─────────────────────────────────────────────────────────────────────────

    public sealed class WalkAnimState : IAnimationState
    {
        static readonly int[] FootfallFrames = { 4, 9, 16, 21 };

        public int  Priority  => 20;
        public bool IsLocked  => false;

        public bool CanEnter(in PlayerMovementSnapshot s)
            => s.LocomotionState == PlayerLocomotionState.Grounded
               && !s.IsCrouching
               && s.NormalizedMoveSpeed > 0.01f
               && !s.IsRunning;

        public string GetStateName(in PlayerMovementSnapshot s)                          => "walk";
        public int    OverrideFrame(in PlayerMovementSnapshot s, int cf, int fc)         => -1;
        public void   OnEnter(string prev, IAnimationEventSink e)                        { }
        public void   OnExit(string next,  IAnimationEventSink e)                        { }

        public void OnFrameChanged(int frame, in PlayerMovementSnapshot s, IAnimationEventSink e)
        {
            if (Array.IndexOf(FootfallFrames, frame) >= 0)
                e?.FireEvent(AnimEvent.Footstep, s.Position);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Trot (normal movement pace)   Priority 30
    //  Original: 16-frame loop. Footfalls from AS3 UnitPon.as sndStep param2=1.
    // ─────────────────────────────────────────────────────────────────────────

    public sealed class TrotAnimState : IAnimationState
    {
        static readonly int[] FootfallFrames = { 5, 7, 13, 15 };

        public int  Priority  => 30;
        public bool IsLocked  => false;

        public bool CanEnter(in PlayerMovementSnapshot s)
            => s.LocomotionState == PlayerLocomotionState.Grounded
               && !s.IsCrouching
               && s.NormalizedMoveSpeed >= 0.6f
               && !s.IsRunning;

        public string GetStateName(in PlayerMovementSnapshot s)                          => "trot";
        public int    OverrideFrame(in PlayerMovementSnapshot s, int cf, int fc)         => -1;
        public void   OnEnter(string prev, IAnimationEventSink e)                        { }
        public void   OnExit(string next,  IAnimationEventSink e)                        { }

        public void OnFrameChanged(int frame, in PlayerMovementSnapshot s, IAnimationEventSink e)
        {
            if (Array.IndexOf(FootfallFrames, frame) >= 0)
                e?.FireEvent(AnimEvent.Footstep, s.Position);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Run   Priority 40
    //  Original: 8-frame cycle. Footfalls from AS3 UnitPon.as sndStep param2=2.
    //  Fires Footstep (same sound pool as walk) — material determines metal vs ground.
    // ─────────────────────────────────────────────────────────────────────────

    public sealed class RunAnimState : IAnimationState
    {
        static readonly int[] FootfallFrames = { 1, 2, 5, 6 };

        public int  Priority  => 40;
        public bool IsLocked  => false;

        public bool CanEnter(in PlayerMovementSnapshot s)
            => s.LocomotionState == PlayerLocomotionState.Grounded
               && !s.IsCrouching
               && s.IsRunning
               && s.NormalizedMoveSpeed > 0.01f;

        public string GetStateName(in PlayerMovementSnapshot s)                          => "run";
        public int    OverrideFrame(in PlayerMovementSnapshot s, int cf, int fc)         => -1;
        public void   OnEnter(string prev, IAnimationEventSink e)                        { }
        public void   OnExit(string next,  IAnimationEventSink e)                        { }

        public void OnFrameChanged(int frame, in PlayerMovementSnapshot s, IAnimationEventSink e)
        {
            if (Array.IndexOf(FootfallFrames, frame) >= 0)
                e?.FireEvent(AnimEvent.Footstep, s.Position);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Jump / Fall (any airborne state)   Priority 50
    //  Frame is driven by vertical velocity, NOT auto-advancing.
    //  Original AS3: frame = 16 + dy, where dy is in px/frame.
    //  Unity: map velocity.y [-maxFall .. +maxRise] → frame [last .. 0].
    // ─────────────────────────────────────────────────────────────────────────

    public sealed class JumpAnimState : IAnimationState
    {
        // Tune these to match the physics controller's actual velocity range.
        readonly float _maxRiseSpeed; // positive, e.g. 9 units/sec
        readonly float _maxFallSpeed; // positive magnitude, e.g. 12 units/sec

        public JumpAnimState(float maxRiseSpeed = 9f, float maxFallSpeed = 12f)
        {
            _maxRiseSpeed = maxRiseSpeed;
            _maxFallSpeed = maxFallSpeed;
        }

        public int  Priority  => 50;
        public bool IsLocked  => false;

        public bool CanEnter(in PlayerMovementSnapshot s)
            => s.LocomotionState is PlayerLocomotionState.JumpRise or PlayerLocomotionState.Fall;

        public string GetStateName(in PlayerMovementSnapshot s) => "jump";

        public int OverrideFrame(in PlayerMovementSnapshot s, int cf, int frameCount)
        {
            if (frameCount <= 1) return 0;
            // Rising fast → frame 0 (peak of arc). Falling fast → last frame.
            float t = Mathf.InverseLerp(_maxRiseSpeed, -_maxFallSpeed, s.Velocity.y);
            return Mathf.RoundToInt(t * (frameCount - 1));
        }

        public void OnEnter(string prev, IAnimationEventSink e)
        {
            // Sound is handled in OnFrameChanged via snapshot flags to avoid duplicate fires.
        }

        public void OnExit(string next, IAnimationEventSink e) { }

        public void OnFrameChanged(int frame, in PlayerMovementSnapshot s, IAnimationEventSink e)
        {
            if (s.JustDoubleJumped)
                e?.FireEvent(AnimEvent.DoubleJump, s.Position);
            if (s.JustLanded)
                e?.FireEvent(AnimEvent.Land, s.Position);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Crouch Idle   Priority 60
    //  Uses state name "down". Falls back to "stay" if that clip isn't imported yet.
    // ─────────────────────────────────────────────────────────────────────────

    public sealed class CrouchIdleAnimState : IAnimationState
    {
        public int  Priority  => 60;
        public bool IsLocked  => false;

        public bool CanEnter(in PlayerMovementSnapshot s)
            => s.IsCrouching && s.NormalizedMoveSpeed < 0.01f;

        public string GetStateName(in PlayerMovementSnapshot s) => "down";
        // Hold frame 0 — "down" is a static crouched pose.
        public int    OverrideFrame(in PlayerMovementSnapshot s, int cf, int fc) => 0;
        public void   OnEnter(string prev, IAnimationEventSink e)                { }
        public void   OnExit(string next,  IAnimationEventSink e)                { }
        public void   OnFrameChanged(int frame, in PlayerMovementSnapshot s, IAnimationEventSink e) { }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Polz (prone / crawling while crouched + moving)   Priority 70
    //  Original: same 24-frame walk cycle for footstep sounds (sndStep param2=4).
    // ─────────────────────────────────────────────────────────────────────────

    public sealed class PolzAnimState : IAnimationState
    {
        static readonly int[] FootfallFrames = { 4, 9, 16, 21 };

        public int  Priority  => 70;
        public bool IsLocked  => false;

        public bool CanEnter(in PlayerMovementSnapshot s)
            => s.IsCrouching && s.NormalizedMoveSpeed >= 0.01f;

        public string GetStateName(in PlayerMovementSnapshot s)                          => "polz";
        public int    OverrideFrame(in PlayerMovementSnapshot s, int cf, int fc)         => -1;
        public void   OnEnter(string prev, IAnimationEventSink e)                        { }
        public void   OnExit(string next,  IAnimationEventSink e)                        { }

        public void OnFrameChanged(int frame, in PlayerMovementSnapshot s, IAnimationEventSink e)
        {
            if (Array.IndexOf(FootfallFrames, frame) >= 0)
                e?.FireEvent(AnimEvent.FootstepCrawl, s.Position);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Swim (plav)   Priority 80
    // ─────────────────────────────────────────────────────────────────────────

    public sealed class SwimAnimState : IAnimationState
    {
        Vector2 _pos;

        public int  Priority  => 80;
        public bool IsLocked  => false;

        public bool CanEnter(in PlayerMovementSnapshot s)
        {
            _pos = s.Position;
            return s.LocomotionState == PlayerLocomotionState.Swim;
        }

        public string GetStateName(in PlayerMovementSnapshot s)                          => "plav";
        public int    OverrideFrame(in PlayerMovementSnapshot s, int cf, int fc)         => -1;

        public void OnEnter(string prev, IAnimationEventSink e)
            => e?.FireEvent(AnimEvent.WaterEnter, _pos);

        public void OnExit(string next, IAnimationEventSink e)
            => e?.FireEvent(AnimEvent.WaterExit, _pos);

        public void OnFrameChanged(int frame, in PlayerMovementSnapshot s, IAnimationEventSink e)
            => _pos = s.Position;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Levitate   Priority 90
    //  Original levit clip: frames 17–66 loop while hovering; 67+ on landing.
    //  For now the driver just plays the full clip on loop.
    // ─────────────────────────────────────────────────────────────────────────

    public sealed class LevitateAnimState : IAnimationState
    {
        public int  Priority  => 90;
        public bool IsLocked  => false;

        public bool CanEnter(in PlayerMovementSnapshot s)
            => s.LocomotionState == PlayerLocomotionState.Levitate;

        public string GetStateName(in PlayerMovementSnapshot s)                          => "levit";
        public int    OverrideFrame(in PlayerMovementSnapshot s, int cf, int fc)         => -1;
        public void   OnEnter(string prev, IAnimationEventSink e)                        { }
        public void   OnExit(string next,  IAnimationEventSink e)                        { }
        public void   OnFrameChanged(int frame, in PlayerMovementSnapshot s, IAnimationEventSink e) { }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Ladder climbing (laz)   Priority 100
    //  Frame advances forward when climbing up, reverses when climbing down.
    //  Stationary on ladder holds the current frame.
    // ─────────────────────────────────────────────────────────────────────────

    public sealed class LadderAnimState : IAnimationState
    {
        float _ladderFrame;

        public int  Priority  => 100;
        public bool IsLocked  => false;

        public bool CanEnter(in PlayerMovementSnapshot s) => s.IsOnLadder;

        public string GetStateName(in PlayerMovementSnapshot s) => "laz";

        public int OverrideFrame(in PlayerMovementSnapshot s, int cf, int frameCount)
        {
            if (frameCount <= 1) return 0;
            if (Mathf.Abs(s.Velocity.y) > 0.05f)
            {
                // Positive velocity.y = moving up in Unity (Y-up). Advance frames forward.
                float dir = s.Velocity.y > 0f ? 1f : -1f;
                _ladderFrame = Mathf.Repeat(_ladderFrame + dir, frameCount);
            }
            return Mathf.RoundToInt(_ladderFrame) % frameCount;
        }

        // Footfall frames from AS3 UnitPon.as sndStep param2=3 (14-frame cycle).
        static readonly int[] FootfallFrames = { 4, 6, 10, 12 };

        public void OnEnter(string prev, IAnimationEventSink e) => _ladderFrame = 0f;
        public void OnExit(string next,  IAnimationEventSink e) { }

        public void OnFrameChanged(int frame, in PlayerMovementSnapshot s, IAnimationEventSink e)
        {
            if (Mathf.Abs(s.Velocity.y) > 0.05f && Array.IndexOf(FootfallFrames, frame) >= 0)
                e?.FireEvent(AnimEvent.ClimbStep, s.Position);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Teleport   Priority 120
    //  Active while PlayerLocomotionState.Teleporting (freeze window, ~0.2 s).
    //  Player sprite holds "stay" — the visual is the particle effect, not the
    //  sprite.  OnEnter fires AnimEvent.Teleport so the event sink can play
    //  the "teleport" sound and spawn VFX at the destination.
    //  (AS3: sound("teleport") + Emitter.emit("teleport") + Emitter.emit("tele"))
    // ─────────────────────────────────────────────────────────────────────────

    public sealed class TeleportAnimState : IAnimationState
    {
        public int  Priority => 120;
        public bool IsLocked => false;

        public bool CanEnter(in PlayerMovementSnapshot s)
            => s.LocomotionState == PlayerLocomotionState.Teleporting;

        public string GetStateName(in PlayerMovementSnapshot s) => "stay";
        public int    OverrideFrame(in PlayerMovementSnapshot s, int cf, int fc) => -1;
        public void   OnEnter(string prev, IAnimationEventSink e) => e?.FireEvent(AnimEvent.Teleport, default);
        public void   OnExit(string next,  IAnimationEventSink e) { }
        public void   OnFrameChanged(int frame, in PlayerMovementSnapshot s, IAnimationEventSink e) { }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Pinok (airborne knockback / stunned)   Priority 110
    //  Plays the "pinok" clip and holds on last frame until grounded again.
    // ─────────────────────────────────────────────────────────────────────────

    public sealed class PinokAnimState : IAnimationState
    {
        public int  Priority  => 110;
        public bool IsLocked  => false;

        public bool CanEnter(in PlayerMovementSnapshot s)
            => s.LocomotionState == PlayerLocomotionState.Knockback;

        public string GetStateName(in PlayerMovementSnapshot s)                          => "pinok";
        public int    OverrideFrame(in PlayerMovementSnapshot s, int cf, int fc)         => -1;
        public void   OnEnter(string prev, IAnimationEventSink e)                        { }
        public void   OnExit(string next,  IAnimationEventSink e)                        { }
        public void   OnFrameChanged(int frame, in PlayerMovementSnapshot s, IAnimationEventSink e) { }
    }
}
