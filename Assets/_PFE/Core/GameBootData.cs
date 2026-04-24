using UnityEngine;
using PFE.UI.MainMenu;

namespace PFE.Core
{
    /// <summary>
    /// Small static bridge for boot-time scene transitions.
    /// </summary>
    public static class GameBootData
    {
        private static NewGameSettings _pendingNewGame;

        public static bool HasPendingNewGame => _pendingNewGame != null;

        public static void SetPendingNewGame(NewGameSettings settings)
        {
            _pendingNewGame = settings?.Clone();

            if (_pendingNewGame != null)
            {
                Debug.Log(
                    $"[GameBootData] Stored new game settings: name={_pendingNewGame.playerName}, " +
                    $"difficulty={_pendingNewGame.difficulty}, flags={_pendingNewGame.ruleFlags}");
            }
        }

        public static NewGameSettings PeekPendingNewGame()
        {
            return _pendingNewGame?.Clone();
        }

        public static NewGameSettings ConsumePendingNewGame()
        {
            NewGameSettings settings = _pendingNewGame?.Clone();
            _pendingNewGame = null;
            return settings;
        }

        public static void ClearPendingNewGame()
        {
            _pendingNewGame = null;
        }
    }
}
