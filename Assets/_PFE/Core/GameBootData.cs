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

        /// <summary>
        /// The pending new game's difficulty as the Oracle's <c>globalDif</c> index (0..4), or
        /// <paramref name="fallback"/> when no new game is pending.
        ///
        /// <para><b>The two numbering schemes already agree, so there is no translation to get
        /// wrong.</b> <c>NewGameDifficulty</c> runs 0..4 with <c>Normal = 2</c>; the Oracle's
        /// <c>globalDif</c> runs 0..4 with a default of 2 (<c>Game.as:41</c>). This exists so the boot
        /// seed (<c>GameManager</c>) and the Pip-Buck readout can both go through one place without either
        /// of them depending on <c>NewGameSettings</c>' type.</para>
        /// </summary>
        public static int PendingDifficultyIndex(int fallback = 2)
        {
            return _pendingNewGame != null ? (int)_pendingNewGame.difficulty : fallback;
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
