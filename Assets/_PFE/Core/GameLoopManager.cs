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
    /// </summary>
    public class GameLoopManager : IStartable, ITickable
    {
        private readonly InputReader _input;
        private readonly GameDatabase _gameDatabase;
        private readonly LandMap _landMap;
        private readonly GameManager _gameManager;
        private readonly PfeDebugSettings _debugSettings;

        // Game state
        private bool _isPaused = false;

        // Constructor injection via VContainer
        public GameLoopManager(
            InputReader input,
            GameDatabase gameDatabase,
            LandMap landMap,
            GameManager gameManager,
            PfeDebugSettings debugSettings)
        {
            _input = input;
            _gameDatabase = gameDatabase;
            _landMap = landMap;
            _gameManager = gameManager;
            _debugSettings = debugSettings;
        }

        public void Start()
        {
            if (_debugSettings.LogGameManagerLifecycle)
                Debug.Log("[GameLoopManager] Game Engine Started.");
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

        public void Tick()
        {
            // This is the global heartbeat, similar to World.step() in AS3

            if (_isPaused)
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
