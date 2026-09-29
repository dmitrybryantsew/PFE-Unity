using System.IO;
using System.Text;
using PFE.Entities.Player;
using PFE.Systems.Map;
using PFE.Systems.Map.Serialization;

namespace PFE.Core.Scripting
{
    /// <summary>
    /// Debug commands exposed to the Lua console as the global <c>save</c> table:
    /// <c>save:Save()</c>, <c>save:Load()</c>, <c>save:Status()</c>. The console verbs
    /// <c>save</c> / <c>load</c> / <c>saves</c> are sugar over the same three methods.
    ///
    /// <para><b>Why save/load lives on the console and not on a hotkey.</b> The F-key row is
    /// already spoken for in the Editor: <c>DoorPropPresenter</c> owns F9, <c>AreaTriggerPresenter</c>
    /// F8, <c>ObjectColliderDebugPresenter</c> F10, this console F1 — and MonKey Commander (an
    /// Editor plugin) treats F1-F15 as freely bindable. A save hotkey that fires when a plugin's
    /// hotkey fires is an accidental write to the quick-save slot, so the console — which needs no
    /// binding and reports what it did — is the safer surface.</para>
    ///
    /// <para><b>Why the world-ready gate is enforced here and not only in the caller.</b> Saving a
    /// half-built map writes a perfectly valid file containing almost nothing, which then loads
    /// "successfully" and reads as a load bug. Refusing is the only outcome that is not misleading,
    /// so the gate sits next to the message that explains it.</para>
    ///
    /// <para><b>MoonSharp visibility.</b> The wiring members are <c>internal</c>/<c>private</c>;
    /// MoonSharp's default reflection interop exposes public members only, so only the command
    /// methods below become callable from Lua. Do not make the wiring fields public.</para>
    /// </summary>
    [LocalOnly]
    public sealed class DevConsoleSaveCommands
    {
        private GameManager _gameManager;
        private LandMap _landMap;

        internal void Wire(GameManager gameManager, LandMap landMap)
        {
            _gameManager = gameManager;
            _landMap = landMap;
        }

        // ── Commands ──────────────────────────────────────────────────────────

        /// <summary>Quick-save the world to the <c>quicksave</c> slot.</summary>
        public string Save()
        {
            if (!Ready(out string blocked)) return blocked;

            int rooms = _landMap.GetRoomCount();

            if (!_gameManager.SaveGame())
                return "[save] Save FAILED - see the Unity console for the exception.";

            return $"[save] Saved {rooms} room(s) -> {QuickSavePath()}";
        }

        /// <summary>Quick-load the world from the <c>quicksave</c> slot.</summary>
        public string Load()
        {
            if (!Ready(out string blocked)) return blocked;

            // Checked before LoadGame so "no save yet" reports as itself rather than as a generic
            // load failure - the two have completely different fixes.
            if (!SaveManager.Instance.HasQuickSave())
                return "[save] No quick save on disk - run 'save' first.";

            if (!_gameManager.LoadGame())
                return "[save] Load FAILED - see the Unity console for the exception.";

            // Reports the player's ACTUAL state after the restore, not the snapshot it was asked to
            // apply - a readback is the only way the console can tell you the restore stuck.
            return $"[save] Loaded quicksave; room=({_landMap.GetCurrentPosition().x}, " +
                   $"{_landMap.GetCurrentPosition().y}) player={PlayerStateText()}";
        }

        /// <summary>
        /// Report the quick-save slot's on-disk state (presence, size, write time) plus the current
        /// room and player position. This is the readout that makes a save/load round-trip
        /// checkable: save, move, load, and compare the player line against the pre-move one.
        /// </summary>
        public string Status()
        {
            if (_gameManager == null)
                return "[save] GameManager not available.";

            var sb = new StringBuilder("[save] ");
            sb.Append(_gameManager.IsInitialized() ? "world=ready" : "world=BUILDING");

            string path = QuickSavePath();
            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                sb.Append($" quicksave={FormatBytes(info.Length)}");
                sb.Append($" written={info.LastWriteTimeUtc:yyyy-MM-dd HH:mm:ss}Z");
            }
            else
            {
                sb.Append(" quicksave=absent");
            }

            sb.Append($" dir={WorldSerializer.GetSaveDirectory()}");

            if (_landMap != null)
            {
                var coord = _landMap.GetCurrentPosition();
                sb.Append($" room=({coord.x}, {coord.y})");
            }

            var player = UnityEngine.Object.FindFirstObjectByType<PlayerController>();
            if (player != null)
            {
                // Explicit != null rather than ?. : null-conditional bypasses UnityEngine.Object's
                // overloaded equality, so it would not catch a destroyed-but-not-null object.
                sb.Append($" player={player.transform.position}");

                var stats = player.Stats;
                if (stats != null)
                    sb.Append($" hp={stats.CurrentHp.Value:0.#}/{stats.MaxHp.Value:0.#}");
            }

            return sb.ToString();
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// The player's live position and health, as one string. Shared by <see cref="Load"/> so a
        /// load's reply carries the values a caller would otherwise have to go and check.
        /// </summary>
        private static string PlayerStateText()
        {
            var player = UnityEngine.Object.FindFirstObjectByType<PlayerController>();
            if (player == null) return "not found";

            var stats = player.Stats;
            return stats != null
                ? $"{player.transform.position} hp={stats.CurrentHp.Value:0.#}/{stats.MaxHp.Value:0.#}"
                : $"{player.transform.position}";
        }

        /// <summary>
        /// The one gate shared by <see cref="Save"/> and <see cref="Load"/>. Returns false with a
        /// message that says which dependency is missing, because "nothing happened" is the least
        /// useful thing a debug command can say.
        /// </summary>
        private bool Ready(out string message)
        {
            if (_gameManager == null)
            {
                message = "[save] GameManager not available.";
                return false;
            }

            if (!_gameManager.IsInitialized())
            {
                message = "[save] World is not initialised yet - refusing to touch the save slot " +
                          "(it would write a partial map). Wait for the world build, then retry.";
                return false;
            }

            if (_landMap == null)
            {
                message = "[save] LandMap not available.";
                return false;
            }

            message = null;
            return true;
        }

        private static string QuickSavePath()
            => WorldSerializer.GetSaveFilePath(SaveManager.QuickSaveSlotId);

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024f:0.#} KiB";
            return $"{bytes / (1024f * 1024f):0.#} MiB";
        }
    }
}
