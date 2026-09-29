using UnityEngine;
using VContainer;
using PFE.Core.Input;

namespace PFE.Core
{
    /// <summary>
    /// F5 = quick save, F11 = quick load (bindings live in <see cref="PfeInputSettings"/>).
    /// F11 rather than the conventional F9 because F9 is the door-collider debug overlay's key.
    ///
    /// <para><b>Why this is a separate component and not a line in <c>PlayerController.Update()</c>.</b>
    /// Saving is world state, not player state — a respawned or replaced player must not take the
    /// save key with it. It is also not published as a MessagePipe message: the sim's bus carries
    /// gameplay events that must be deterministic and replayable, and a file write is neither.</para>
    ///
    /// <para><b>Why it polls rather than subscribes.</b> <c>InputAction.performed</c> fires during
    /// input update, which can land before <c>GameManager</c> has finished generating the world.
    /// Polling in <c>Update</c> with an explicit <c>GameManager.IsInitialized()</c> gate makes
    /// "F5 during boot" a no-op instead of a half-written save.</para>
    /// </summary>
    [LocalOnly]
    public sealed class SaveHotkeys : MonoBehaviour
    {
        private InputReader _input;
        private GameManager _gameManager;

        [Inject]
        public void Construct(InputReader input, GameManager gameManager)
        {
            _input = input;
            _gameManager = gameManager;
        }

        private void Update()
        {
            if (_input == null || _gameManager == null)
                return;

            // Gate on the world existing. Without this, an F5 pressed during the ~3.5 s
            // game.db.init / world build would serialise whatever rooms happen to be in the map.
            // GameManager already exposed this as a method; it predates this file.
            if (!_gameManager.IsInitialized())
                return;

            if (_input.QuickSave.WasPressedThisFrame())
                _gameManager.SaveGame();

            if (_input.QuickLoad.WasPressedThisFrame())
                _gameManager.LoadGame();
        }
    }
}
