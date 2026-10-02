using System.Collections.Generic;
using UnityEngine;
using VContainer.Unity;

namespace PFE.Core
{
    /// <summary>
    /// Snapshot of simulation timing, for the debug overlay and for tests. Read-only.
    /// </summary>
    public readonly struct SimMetrics
    {
        public SimMetrics(
            int tickIndex,
            float accumulator,
            float alpha,
            int ticksThisFrame,
            float averageTicksPerSecond,
            int droppedTicks,
            int tickableCount,
            int ticksPerSecond)
        {
            TickIndex = tickIndex;
            Accumulator = accumulator;
            Alpha = alpha;
            TicksThisFrame = ticksThisFrame;
            AverageTicksPerSecond = averageTicksPerSecond;
            DroppedTicks = droppedTicks;
            TickableCount = tickableCount;
            TicksPerSecond = ticksPerSecond;
        }

        /// <summary>Monotonic index of the most recently completed tick.</summary>
        public int TickIndex { get; }

        /// <summary>Unconsumed wall time carried into the next frame, in seconds.</summary>
        public float Accumulator { get; }

        /// <summary>Interpolation factor in [0,1) between the last two tick states.</summary>
        public float Alpha { get; }

        /// <summary>How many ticks ran during the frame that just ended.</summary>
        public int TicksThisFrame { get; }

        /// <summary>Measured tick rate over a rolling window. Should sit at the configured rate.</summary>
        public float AverageTicksPerSecond { get; }

        /// <summary>Cumulative ticks discarded by the spiral-of-death guard. Should stay 0.</summary>
        public int DroppedTicks { get; }

        /// <summary>Registered <see cref="ISimTickable"/> count.</summary>
        public int TickableCount { get; }

        /// <summary>Configured tick rate.</summary>
        public int TicksPerSecond { get; }
    }

    /// <summary>
    /// The fixed-step simulation driver. Owns the accumulator and the tick index; dispatches
    /// <see cref="ISimTickable.SimTick"/> in a stable, total order.
    ///
    /// <para>Runs off VContainer's <see cref="ITickable"/>, i.e. the render/player loop, which is a
    /// variable rate (60/120/144/240 Hz depending on the display). The accumulator converts that
    /// variable frame cadence into a fixed tick cadence, so simulation speed stops being a function
    /// of the player's monitor. See <c>docs/Roadmap/02_P1_FIXED_SIMULATION_TICK.md</c>.</para>
    ///
    /// <para>Stage A of the migration: this driver exists and is registered, but <b>no consumers are
    /// attached</b>. With an empty registry the loop is inert — it only advances its own counters, so
    /// gameplay is unchanged. Consumers move across in Stage C.</para>
    /// </summary>
    public sealed class SimLoop : IStartable, ITickable
    {
        private const int RateWindowFrames = 60;

        private readonly SimClock _clock;
        private readonly PfeDebugSettings _debugSettings;
        private readonly List<ISimTickable> _tickables = new();

        private bool _orderDirty;

        // Accumulator state
        private float _accumulator;
        private int _tickIndex = -1;
        private int _ticksThisFrame;
        private int _droppedTicks;

        // Rolling tick-rate measurement: ticks observed per RateWindowFrames frames.
        private int _rateWindowTicks;
        private int _rateWindowFrames;

        /// <summary>Wall time actually covered by the current window, accumulated per frame.</summary>
        private float _rateWindowSeconds;
        private float _averageTicksPerSecond;

        private GameObject _overlayObject;
        private SimDebugOverlay _overlay;

        public SimLoop(SimClock clock, PfeDebugSettings debugSettings)
        {
            _clock = clock;
            _debugSettings = debugSettings;
        }

        /// <summary>Configured simulation clock. Canonical constants live on <see cref="SimClock"/>.</summary>
        public SimClock Clock => _clock;

        /// <summary>Index of the most recently completed tick, or -1 before the first tick.</summary>
        public int TickIndex => _tickIndex;

        /// <summary>
        /// Interpolation factor in [0,1) between the last two tick states. A view pass lerps with this
        /// so rendering is smooth at any display rate while the sim stays on its fixed cadence.
        /// </summary>
        public float Alpha => _clock.SimDt > 0f ? Mathf.Clamp01(_accumulator / _clock.SimDt) : 0f;

        /// <summary>Number of registered simulation consumers.</summary>
        public int TickableCount => _tickables.Count;

        public SimMetrics Metrics => new SimMetrics(
            _tickIndex,
            _accumulator,
            Alpha,
            _ticksThisFrame,
            _averageTicksPerSecond,
            _droppedTicks,
            _tickables.Count,
            _clock.TicksPerSecond);

        public void Start()
        {
            if (_debugSettings != null && _debugSettings.SimTickOverlayEnabled)
            {
                CreateOverlay();
            }
        }

        /// <summary>
        /// Adds a simulation consumer. Safe to call during startup; order is resolved lazily on the
        /// next tick, so registration order does not matter.
        /// </summary>
        public void Register(ISimTickable tickable)
        {
            if (tickable == null || _tickables.Contains(tickable))
            {
                return;
            }

            _tickables.Add(tickable);
            _orderDirty = true;
        }

        /// <summary>Removes a simulation consumer.</summary>
        public bool Unregister(ISimTickable tickable)
        {
            bool removed = _tickables.Remove(tickable);
            if (removed)
            {
                _orderDirty = true;
            }

            return removed;
        }

        /// <summary>
        /// Advances the simulation by exactly one tick and returns the index of the tick that ran.
        ///
        /// <para>Public so a future lockstep network driver can step ticks on receipt of a remote
        /// input batch instead of on wall time. Wall-clock accumulation is just one caller.</para>
        /// </summary>
        public int StepOnce()
        {
            // The single most important number for "the room is at 2 FPS": how many ticks a FRAME
            // runs. `Advance` caps that at SimClock.MaxCatchupTicks (5), so a frame whose cost
            // exceeds 5 * SimDt saturates the cap and then the frame time IS 5 x (cost of one tick).
            // Read `calls(sim.tick) / calls(sim.frame)` to get ticks-per-frame, and
            // `total(sim.tick) / calls(sim.frame)` to get the milliseconds each frame spends in the
            // sim. If that second number is most of the frame, the fix is inside a tick, not in
            // rendering — and `sim.tick.*` below says which part.
            using (PFE.Core.Profiling.PfeProfiler.Region("sim.tick",
                "sim: one fixed tick across every ISimTickable. calls / calls(sim.frame) == ticks per frame."))
            {
                EnsureOrder();

                _tickIndex++;

                for (int i = 0; i < _tickables.Count; i++)
                {
                    _tickables[i].SimTick(_tickIndex);
                }

                return _tickIndex;
            }
        }

        public void Tick()
        {
            EnsureOverlay();

            // Must be fully qualified. This file is in namespace PFE.Core, and PFE.Core.Time is a
            // NAMESPACE (Assets/_PFE/Core/Time/), so a bare `Time` binds to that namespace rather than
            // to UnityEngine.Time — namespace members win over `using`-imported types. Same reason
            // CameraFollow.cs, SceneLoader.cs and UnityTimeProvider.cs all write UnityEngine.Time.
            Advance(UnityEngine.Time.unscaledDeltaTime);
        }

        /// <summary>
        /// Create the SIM CLOCK readout the first time its overlay channel is switched on.
        ///
        /// <para><b>Why this is not only in <see cref="Start"/>.</b> It used to be: the overlay was
        /// created at startup if the flag happened to be on then, and never otherwise. Now that the
        /// channel is reachable from the console mid-session (<c>col on clock</c>), a
        /// create-only-at-Start lifecycle turns every later toggle into the worst kind of bug — the
        /// reply says the overlay is on and the screen shows nothing, which is indistinguishable from
        /// the overlay being broken. The check is one flag read per tick.</para>
        /// </summary>
        private void EnsureOverlay()
        {
            if (_overlay != null) return;
            if (_debugSettings == null || !_debugSettings.SimTickOverlayEnabled) return;

            CreateOverlay();
        }

        /// <summary>
        /// Advances the accumulator by an explicit amount of wall time.
        ///
        /// <para><see cref="Tick"/> is a thin wrapper that passes <c>UnityEngine.Time.unscaledDeltaTime</c>; this
        /// overload exists so tests can drive the accumulator, the catch-up budget and the spiral
        /// guard directly, without the engine or a running player loop.</para>
        /// </summary>
        /// <param name="deltaSeconds">Wall time elapsed since the previous call, in seconds.</param>
        public void Advance(float deltaSeconds)
        {
            // Frame-level anchor for the whole sim. Every other `sim.*` region is a child of this
            // one, so its SELF time is "the frame cost that is NOT in a tick" — which is the number
            // that separates "the sim is eating the frame" from "something else is".
            using (PFE.Core.Profiling.PfeProfiler.Region("sim.frame",
                "sim: one frame's advance — parent of sim.tick. Its self time is frame cost outside the sim."))
            {
            _ticksThisFrame = 0;

            // Dispatch is gated; accumulation is not, so the clock stays measurable before any
            // consumer is attached.
            bool dispatch = _debugSettings == null || _debugSettings.SimTickEnabled;

            if (!dispatch)
            {
                // Do NOT accumulate while dispatch is off. The accumulator would grow to the clamp
                // every frame and the drop guard below would then report phantom drops forever.
                _accumulator = 0f;
                UpdateRateMeasurement(deltaSeconds);
                return;
            }

            // Unscaled on purpose: game-time scaling (slow-mo, hit-stop) is a simulation concern and
            // belongs inside a tick, not in the clock that decides how many ticks to run.
            //
            // Clamp before accumulating so a single long hitch (editor pause, breakpoint, asset load)
            // cannot queue up an unbounded burst of ticks.
            float maxAccumulation = _clock.SimDt * SimClock.MaxCatchupTicks;
            _accumulator += Mathf.Min(Mathf.Max(0f, deltaSeconds), maxAccumulation);

            int guard = 0;
            while (_accumulator >= _clock.SimDt && guard < SimClock.MaxCatchupTicks)
            {
                StepOnce();
                _accumulator -= _clock.SimDt;
                guard++;
                _ticksThisFrame++;
            }

            _rateWindowTicks += _ticksThisFrame;

            // Spiral-of-death guard, second half.
            //
            // Reachability note, verified by induction rather than assumed: the input clamp above
            // already makes this branch unreachable through Advance(). Because each call adds at most
            // maxAccumulation and then drains up to MaxCatchupTicks ticks (== maxAccumulation), the
            // accumulator satisfies acc <= maxAccumulation after every call, and a rate change cannot
            // break that either (acc < SimDt_old, and SimDt_old <= MaxCatchupTicks * SimDt_new for
            // every pair of supported rates). It is kept as a cheap invariant net so that a future
            // change to the clamp, or an externally-driven tick source, cannot silently reintroduce
            // an unbounded stall. Do not add a test asserting it fires — it does not.
            if (_accumulator > maxAccumulation)
            {
                _droppedTicks += Mathf.Max(0, Mathf.FloorToInt(_accumulator / _clock.SimDt));
                _accumulator = 0f;
            }

            UpdateRateMeasurement(deltaSeconds);
            }
        }

        private void UpdateRateMeasurement(float deltaSeconds)
        {
            _rateWindowSeconds += Mathf.Max(0f, deltaSeconds);
            _rateWindowFrames++;
            if (_rateWindowFrames < RateWindowFrames)
            {
                return;
            }

            // Ticks over the window, divided by the wall time the window actually covered.
            //
            // Accumulated per frame. This used to be `RateWindowFrames * deltaSeconds` — the last
            // frame's duration applied to all 60 frames — which reports a rate inflated by
            // (window average frame time / last frame time). On an uneven frame time that is not
            // small: the live overlay read "measured 46.6 ticks/s" against a canonical 30 Hz clock
            // whose alpha and accumulator both showed the correct 33.3 ms period. The clock was
            // right; the instrument was wrong, and it was the only timing number on screen.
            float windowSeconds = Mathf.Max(_rateWindowSeconds, 1e-5f);
            _averageTicksPerSecond = _rateWindowTicks / windowSeconds;

            _rateWindowTicks = 0;
            _rateWindowFrames = 0;
            _rateWindowSeconds = 0f;
        }

        /// <summary>
        /// Stable total order: ascending <see cref="ISimTickable.TickOrder"/>, registration order as
        /// the tie-break. List.Sort is not stable, so the original index is compared explicitly.
        /// </summary>
        private void EnsureOrder()
        {
            if (!_orderDirty)
            {
                return;
            }

            var indexed = new List<KeyValuePair<int, ISimTickable>>(_tickables.Count);
            for (int i = 0; i < _tickables.Count; i++)
            {
                indexed.Add(new KeyValuePair<int, ISimTickable>(i, _tickables[i]));
            }

            indexed.Sort((a, b) =>
            {
                int byOrder = a.Value.TickOrder.CompareTo(b.Value.TickOrder);
                return byOrder != 0 ? byOrder : a.Key.CompareTo(b.Key);
            });

            for (int i = 0; i < indexed.Count; i++)
            {
                _tickables[i] = indexed[i].Value;
            }

            _orderDirty = false;
        }

        private void CreateOverlay()
        {
            _overlayObject = new GameObject("__SimDebugOverlay");
            Object.DontDestroyOnLoad(_overlayObject);
            _overlay = _overlayObject.AddComponent<SimDebugOverlay>();
            _overlay.Bind(this);
        }
    }
}
