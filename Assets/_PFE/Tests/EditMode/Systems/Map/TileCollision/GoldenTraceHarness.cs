using System;
using UnityEngine;
using PFE.Core;
using PFE.Systems.Map;
using PFE.Systems.Physics;

namespace PFE.Tests.EditMode.Systems.Map.TileCollision
{
    /// <summary>
    /// Test harness for running deterministic motor simulation in EditMode.
    /// Drives TilePhysicsController with scripted inputs via SimLoop.StepOnce().
    ///
    /// Follows the harness recipe from P2_RESEARCH.md §8:
    ///   1. Pure EditMode GameObject with motor
    ///   2. Canonical 30 Hz SimClock + SimLoop
    ///   3. AttachSimulation to avoid 50 Hz FixedUpdate 2x bug
    ///   4. Explicit SetRoom and SetPixelPosition
    ///   5. Scripted input loop with TraceRecorder
    /// </summary>
    public sealed class GoldenTraceHarness : IDisposable
    {
        private GameObject _go;
        private readonly TilePhysicsController _motor;
        private readonly SimClock _clock;
        private readonly SimLoop _loop;
        private readonly TraceRecorder _recorder;
        private int _currentTick;

        public TilePhysicsController Motor => _motor;
        public SimClock Clock => _clock;
        public SimLoop Loop => _loop;
        public TraceRecorder Recorder => _recorder;
        public int CurrentTick => _currentTick;

        public GoldenTraceHarness(RoomInstance room, float startX, float startY)
        {
            _go = new GameObject("GoldenTraceMotor");
            _motor = _go.AddComponent<TilePhysicsController>();
            _clock = new SimClock(SimClock.CanonicalTicksPerSecond);
            _loop = new SimLoop(_clock, null);
            _recorder = new TraceRecorder();

            _motor.SetRoom(room);
            _motor.SetPixelPosition(startX, startY);
            _motor.AttachSimulation(_clock, _loop);

            _currentTick = 0;
            // Record initial state at tick 0 before any steps
            _recorder.Record(_currentTick, _motor);
        }

        /// <summary>
        /// Step simulation by one tick with specified inputs, then record state.
        /// </summary>
        public void Step(
            float horizontal = 0f,
            bool jump = false,
            bool down = false,
            float ladderY = 0f,
            bool ladderClimb = false,
            float jumpForce = 15f)
        {
            _currentTick++;

            _motor.SetInput(horizontal, jump, down);

            // Always apply ladder input, including the "none" case. The motor keeps the last
            // ladder input it was given, so skipping the call would leave the PREVIOUS tick's
            // `wantsToUseLadder` / `ladderInputY` live and silently re-attach a ladder the
            // caller just jumped off. Inputs must not persist across Step() calls.
            _motor.SetLadderInput(ladderY, ladderClimb);

            if (jump)
            {
                _motor.Jump(jumpForce);
            }

            _loop.StepOnce();

            _recorder.Record(_currentTick, _motor);
        }

        /// <summary>
        /// Step multiple ticks with identical input.
        /// </summary>
        public void StepN(
            int ticks,
            float horizontal = 0f,
            bool jump = false,
            bool down = false,
            float ladderY = 0f,
            bool ladderClimb = false)
        {
            for (int i = 0; i < ticks; i++)
            {
                // Only fire jump on the first tick of the batch
                bool doJump = (i == 0) && jump;
                Step(horizontal, doJump, down, ladderY, ladderClimb);
            }
        }

        public void Dispose()
        {
            if (_go != null)
            {
                UnityEngine.Object.DestroyImmediate(_go);
                _go = null;
            }
        }
    }
}
