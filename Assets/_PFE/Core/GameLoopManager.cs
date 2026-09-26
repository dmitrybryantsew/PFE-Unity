using VContainer.Unity;
using Cysharp.Threading.Tasks;
using UnityEngine;
using PFE.Core.Input;
using PFE.Data;
using PFE.Systems.Map; 

namespace PFE.Core
{
    /// <summary>
    /// Main game loop manager.
    /// Replaces World.as -> step() function from the original ActionScript.
    /// Uses VContainer's IStartable and ITickable interfaces instead of MonoBehaviour.
    ///
    /// <para><b>Two drivers, one heartbeat.</b> AS3's <c>World.step()</c> ran at a fixed 30 fps. This
    /// class can be driven either by <see cref="SimLoop"/> at the configured tick rate
    /// (<c>PfeDebugSettings.SimTickRoom</c> on — one call per tick, stepped by exactly
    /// <c>SimClock.SimDt</c>), or by Unity's per-frame <see cref="ITickable"/> (flag off — the
    /// historical path, one call per rendered frame with a hardcoded 1/60 step). The two are mutually
    /// exclusive on purpose: running both would step the room twice per frame at two different
    /// rates.</para>
    /// </summary>
    public class GameLoopManager : IStartable, ITickable, ISimTickable
    {
        private readonly InputReader _input;
        private readonly GameDatabase _gameDatabase;
        private readonly LandMap _landMap;
        private readonly GameManager _gameManager;
        private readonly PfeDebugSettings _debugSettings;
        private readonly SimClock _simClock;
        private readonly SimLoop _simLoop;

        // Game state
        private bool _isPaused = false;

        // Constructor injection via VContainer
        public GameLoopManager(
            InputReader input,
            GameDatabase gameDatabase,
            LandMap landMap,
            GameManager gameManager,
            PfeDebugSettings debugSettings,
            SimClock simClock,
            SimLoop simLoop)
        {
            _input = input;
            _gameDatabase = gameDatabase;
            _landMap = landMap;
            _gameManager = gameManager;
            _debugSettings = debugSettings;
            _simClock = simClock;
            _simLoop = simLoop;
        }

        /// <summary>
        /// The room must exist and be stepped before anything moves inside it, so the heartbeat runs
        /// at <see cref="SimTickOrder.RoomState"/> — ahead of the player motor and every entity.
        /// </summary>
        public int TickOrder => SimTickOrder.RoomState;

        public void Start()
        {
            if (_debugSettings.LogGameManagerLifecycle)
                Debug.Log("[GameLoopManager] Game Engine Started.");

            // P1: hand the room heartbeat to the fixed-step simulation. Opt-in — with the flag off the
            // heartbeat stays on the per-frame ITickable path in Tick(). SimLoop.Register de-duplicates,
            // so a repeated Start cannot double-register.
            if (_debugSettings.SimTickRoom)
            {
                if (_simLoop != null && _simClock != null)
                {
                    _simLoop.Register(this);

                    // Deliberately not gated on LogGameManagerLifecycle: this confirms an opt-in
                    // behaviour change, and silence would be ambiguous with "the flag did nothing".
                    Debug.Log(
                        "[GameLoopManager] Room heartbeat attached to SimLoop at " +
                        _simClock.TicksPerSecond + " Hz (one step per tick).");
                }
                else
                {
                    Debug.LogWarning(
                        "[GameLoopManager] SimTickRoom is on but SimClock/SimLoop is unavailable; " +
                        "staying on the per-frame path.");
                }
            }

            InitializeAsync().Forget();
        }

        private async UniTaskVoid InitializeAsync()
        {
            // GameManager handles database and map initialization
            // Wait for it to complete
            await UniTask.WaitUntil(() => _gameManager.IsInitialized());

            if (_debugSettings.LogGameManagerLifecycle)
                Debug.Log("[GameLoopManager] All systems ready.");

            // TODO: Spawn player, setup camera, etc.
        }

        /// <summary>
        /// Fixed-step heartbeat. Called once per simulation tick when <c>PfeDebugSettings.SimTickRoom</c>
        /// is on, always with an explicit <c>SimClock.SimDt</c> step so one tick is exactly one AS3
        /// frame and the rate no longer depends on the display refresh.
        /// </summary>
        public void SimTick(int tickIndex)
        {
            if (_isPaused)
            {
                return;
            }

            _landMap?.Update(_simClock.SimDt);
        }

        public void Tick()
        {
            // This is the global heartbeat, similar to World.step() in AS3

            if (_isPaused)
            {
                return;
            }

            // When SimLoop owns the heartbeat, standing down here is essential: leaving both drivers
            // active would step the room twice per frame at two different rates.
            if (_debugSettings.SimTickRoom)
            {
                return;
            }

            // Update land map (current room)
            _landMap?.Update();

            // Process input if manual polling is needed
            // _input.ProcessInput();

            // TODO: Update other global systems (physics, AI, etc.)
        }

        /// <summary>
        /// Pause the game.
        /// </summary>
        public void Pause()
        {
            _isPaused = true;
            if (_debugSettings.LogGameLoopEvents)
                Debug.Log("[GameLoopManager] Game paused");
        }

        /// <summary>
        /// Resume the game.
        /// </summary>
        public void Resume()
        {
            _isPaused = false;
            if (_debugSettings.LogGameLoopEvents)
                Debug.Log("[GameLoopManager] Game resumed");
        }

        /// <summary>
        /// Check if game is paused.
        /// </summary>
        public bool IsPaused()
        {
            return _isPaused;
        }
    }
}
