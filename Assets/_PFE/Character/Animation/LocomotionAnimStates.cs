using System;
using PFE.Entities.Player;
using PFE.Systems.Physics;
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
    //
    //  The clip is "stay", NOT "down". There is no `down` clip in the imported definition — `down` is a
    //  FRAME LABEL inside the oracle's 76-frame body pose bank (`getStayFrame()`, UnitPlayer.as:4722),
    //  and the bank is what this project imports as the `stay` clip (frameCount 76, loopMode Manual).
    //  AS3 indexes it 1-based and returns 2 for a crouched, non-leaning player, so the port's 0-based
    //  index is 1 — index 0 is the standing pose, which is what this state used to fall back to, and
    //  that is why crouching rendered the standing sprite.
    //
    //  Held, not played: the oracle's steady crouch is `body.gotoAndStop(2)`. The `gotoAndPlay("down")`
    //  just before it is the 1→2 transition, which the port does not have a clip for (audit row 4).
    // ─────────────────────────────────────────────────────────────────────────

    public sealed class CrouchIdleAnimState : IAnimationState
    {
        /// <summary>
        /// AS3 <c>getStayFrame()</c> → 2 for <c>isSit</c> with no wall lean (<c>UnitPlayer.as:4726</c>),
        /// as a 0-based index into the port's <c>stay</c> clip.
        /// </summary>
        public const int CrouchPoseFrame = 1;

        public int  Priority  => 60;
        public bool IsLocked  => false;

        public bool CanEnter(in PlayerMovementSnapshot s)
            => s.IsCrouching && s.NormalizedMoveSpeed < 0.01f;

        public string GetStateName(in PlayerMovementSnapshot s) => "stay";
        // Hold the crouched pose — `stay` is a Manual pose bank, so nothing advances on its own.
        public int    OverrideFrame(in PlayerMovementSnapshot s, int cf, int fc) => CrouchPoseFrame;
        public void   OnEnter(string prev, IAnimationEventSink e)                { }
        public void   OnExit(string next,  IAnimationEventSink e)                { }
        public void   OnFrameChanged(int frame, in PlayerMovementSnapshot s, IAnimationEventSink e) { }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Roll   Priority 75 — above Polz (70), because it is the same crouch and only the first frames
    //  of it differ.
    //
    //  AS3 (UnitPlayer.as:4417-4435): while sitting and moving, `roll` is selected when
    //  `maxSpeed > walkSpeed * 1.6 && dx*storona > 0 && (runForever || keyRun && (left||right))`.
    //  `maxSpeed` is `sitSpeed` on every frame a unit is already sitting, so that test can only pass on
    //  the frame the crouch BEGINS — which is why the movement layer decides it once, at the latch
    //  (CrouchStateMath.ShouldRoll), and the snapshot carries the answer.
    //
    //  The clip plays to frame 15 and then hands over to `polz`, frame-for-frame as the oracle does.
    //  This state therefore owns the ONLY end condition: `AdvanceFrame` must not be overridden here.
    // ─────────────────────────────────────────────────────────────────────────

    public sealed class RollAnimState : IAnimationState
    {
        int  _frame;
        bool _interrupted;

        public int  Priority  => 75;
        public bool IsLocked  => false;

        public bool CanEnter(in PlayerMovementSnapshot s)
        {
            if (!s.IsCrouchRoll)
            {
                // No roll for this crouch — either it never was one, or the previous crouch ended and
                // the movement layer cleared the flag. Both mean the next crouch starts at frame 0.
                _frame = 0;
                _interrupted = false;
                return false;
            }

            if (!s.IsCrouching || _interrupted)
            {
                return false;
            }

            // The roll lives inside AS3's "sitting AND moving" branch (`:4415`); the idle branch above
            // it takes over the moment the player stops, and the roll is abandoned rather than resumed.
            // Without this term a player who crouches out of a sprint and stops dead would keep
            // spinning on the spot for fifteen frames.
            if (s.NormalizedMoveSpeed < 0.01f)
            {
                return false;
            }

            // Past the handover the crawl owns the body (AS3 `:4417`); this also stops the roll from
            // re-starting on every later evaluation of the same crouch.
            return _frame < CrouchStateMath.RollHandoffFrame;
        }

        public string GetStateName(in PlayerMovementSnapshot s)                          => "roll";
        public int    OverrideFrame(in PlayerMovementSnapshot s, int cf, int fc)         => -1;
        public void   OnEnter(string prev, IAnimationEventSink e)                        => _frame = 0;

        // Leaving for anything but `polz` means the roll was cut short (the player stopped, or a
        // higher-priority state took over). AS3 sets `animState` away from "roll" in that case, and
        // the branch that re-enters it can never fire again for the same crouch — so neither can this.
        public void   OnExit(string next, IAnimationEventSink e)
        {
            if (next != "polz")
            {
                _interrupted = true;
            }
        }

        public void OnFrameChanged(int frame, in PlayerMovementSnapshot s, IAnimationEventSink e)
        {
            _frame = frame;
        }
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
        float   _velocityY;

        public int  Priority  => 80;
        public bool IsLocked  => false;

        public bool CanEnter(in PlayerMovementSnapshot s)
        {
            _pos = s.Position;
            _velocityY = s.Velocity.y;
            return s.LocomotionState == PlayerLocomotionState.Swim;
        }

        public string GetStateName(in PlayerMovementSnapshot s)                          => "plav";
        public int    OverrideFrame(in PlayerMovementSnapshot s, int cf, int fc)         => -1;

        // ── The splash, gated and scaled exactly as AS3 does ──────────────────────────────────
        //
        // `dy` is read from the snapshot cached by CanEnter. On the ordinary transition that is this
        // tick's value, because the driver evaluates states in priority order and plav is the state it
        // selects. The one exception is a transition where a HIGHER-priority state wins (ladder 100,
        // levitate 90) — `EvaluateBestState` returns on the first match, so plav's CanEnter is not
        // called that tick and the cache is one tick old. That is the right value anyway: AS3 compares
        // this frame's `inWater` against the previous frame's flag, so the speed it gates on is the
        // speed the unit had as it crossed. Recorded because "stale" and "previous frame" look the
        // same here and only one of them is a bug.
        //
        // Why the gate exists is written up on SwimMath: a swimmer holding Up at the surface bobs
        // across the water line at 3.72 px/frame, below AS3's ±5, and is silent. Firing on the
        // transition alone (what this did) turns every stroke of that bob into two full-volume
        // splashes — exit, then re-entry — because the swim impulse now actually carries the unit out
        // of the water. See SwimMath.SplashMinVerticalSpeedPixelsPerFrame.

        public void OnEnter(string prev, IAnimationEventSink e)
        {
            if (!SwimMath.ShouldSplashEnteringWater(_velocityY))
            {
                return;
            }

            e?.FireEvent(AnimEvent.WaterEnter, _pos, SwimMath.SplashVolume(_velocityY));
        }

        public void OnExit(string next, IAnimationEventSink e)
        {
            if (!SwimMath.ShouldSplashLeavingWater(_velocityY))
            {
                return;
            }

            e?.FireEvent(AnimEvent.WaterExit, _pos, SwimMath.SplashVolume(_velocityY));
        }

        public void OnFrameChanged(int frame, in PlayerMovementSnapshot s, IAnimationEventSink e)
        {
            _pos = s.Position;
            _velocityY = s.Velocity.y;
        }
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
