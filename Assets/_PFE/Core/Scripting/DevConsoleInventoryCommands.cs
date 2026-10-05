using System;
using System.Linq;
using System.Text;
using PFE.Entities.Player;
using PFE.Systems.Inventory;

namespace PFE.Core.Scripting
{
    /// <summary>
    /// Debug commands exposed to the Lua console as the global <c>inv</c> table, plus the console verb
    /// <c>inv</c> (sugar over the same methods).
    ///
    /// <para><b>Why this exists.</b> The inventory is wired into the player now, which means it is
    /// possible for the first time to see what is in it — and to put something in it without a pickup
    /// system. Every mutation goes through the same <see cref="IInventoryCommandSink"/> gameplay will
    /// use, so <c>inv add</c> is an end-to-end test of the command seam, not a bypass: it exercises
    /// validation, id resolution and the inventory in the same order a real pickup will.</para>
    ///
    /// <para><b>Status names the broken link, like <c>spell</c>, <c>eff</c> and <c>rpg</c>.</b> "The
    /// inventory is empty" has four different causes — no player, no <see cref="PlayerInventory"/>
    /// component, a component whose <c>Construct</c> never ran, or a component with no command sink —
    /// and they are indistinguishable from the far end. A bare <c>inv</c> prints the one figure that
    /// separates them.</para>
    ///
    /// <para><b>MoonSharp visibility.</b> <c>Wire</c> is <c>internal</c>; MoonSharp's default reflection
    /// interop exposes public members only, so only the command methods below become callable from Lua.
    /// Do not make <c>_playerProvider</c> or <c>Wire</c> public.</para>
    /// </summary>
    [LocalOnly]
    public sealed class DevConsoleInventoryCommands
    {
        /// <summary>
        /// Late-resolved so the commands survive a respawn — the same reason and shape as
        /// <see cref="DevConsoleSpellCommands"/>. A cached reference to a destroyed player is the
        /// classic "MissingReferenceException in a debug tool" failure.
        /// </summary>
        private Func<PlayerController> _playerProvider;

        internal void Wire(Func<PlayerController> playerProvider)
        {
            _playerProvider = playerProvider;
        }

        private static PlayerController FindPlayer()
            => UnityEngine.Object.FindFirstObjectByType<PlayerController>();

        private PlayerController ResolvePlayer()
        {
            var player = _playerProvider != null ? _playerProvider() : null;
            return player != null ? player : FindPlayer();
        }

        /// <summary>
        /// The inventory on the player, or null. <see cref="PlayerInventory"/> is added by
        /// <c>PlayerController.Awake</c>, so it is present whenever the player is — but it is resolved
        /// rather than assumed, because "the component is missing" is one of the causes <see cref="Status"/>
        /// exists to tell apart.
        /// </summary>
        private PlayerInventory ResolveInventory()
        {
            var player = ResolvePlayer();
            return player != null ? player.GetComponent<PlayerInventory>() : null;
        }

        /// <summary>
        /// Engine health: is there an inventory, is it wired, and what is in it. A bare <c>inv</c> runs
        /// this, never a toggle.
        /// </summary>
        public string Status()
        {
            var player = ResolvePlayer();
            if (player == null) return "[inv] No PlayerController in the scene.";

            var inventory = player.GetComponent<PlayerInventory>();
            if (inventory == null)
            {
                return "[inv] PlayerController has no PlayerInventory. It is created in PlayerController.Awake, " +
                       "so this means Awake never ran.";
            }

            if (!inventory.IsReady)
            {
                return "[inv] The PlayerInventory exists but its Construct has not run, so there is no " +
                       "GameInventory yet. This means neither Construct nor Awake completed its wiring.";
            }

            if (inventory.Commands == null)
            {
                return "[inv] The inventory exists but has no command sink — no content registry was " +
                       "injected, so every id would fail to resolve. Commands will be rejected with a reason.";
            }

            var game = inventory.Inventory;
            var report = new StringBuilder();
            report.Append($"[inv] Ready. {game.Items.Count} item stack(s), {game.Weapons.Count} weapon(s), " +
                          $"{game.Armors.Count} armour(s), total mass {game.GetTotalMass():0.##}.");

            // Sorted so two runs of the same inventory print the same thing — a status line that
            // reshuffles is unreadable in a log and hides real changes.
            foreach (var pair in game.Items.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                report.Append($"\n  {pair.Key} x{pair.Value.Quantity}");
            }

            return report.ToString();
        }

        /// <summary>
        /// Grant <paramref name="amount"/> of item <paramref name="itemId"/>, through the command seam.
        /// Returns the sink's own verdict, so a rejected id names itself.
        /// </summary>
        public string Add(string itemId, int amount = 1)
        {
            var (inventory, error) = RequireReady();
            if (error != null) return error;

            if (string.IsNullOrEmpty(itemId))
                return "Usage: inv add <itemId> [amount]";

            var result = inventory.Submit(InventoryCommand.AddItem(itemId, amount));
            return result.Applied
                ? $"[inv] Added {result.Amount} x '{itemId}'. Now holding {inventory.GetAmmoCount(itemId)}."
                : $"[inv] Could not add '{itemId}': {result.Reason}";
        }

        /// <summary>Remove <paramref name="amount"/> of item <paramref name="itemId"/>.</summary>
        public string Remove(string itemId, int amount = 1)
        {
            var (inventory, error) = RequireReady();
            if (error != null) return error;

            if (string.IsNullOrEmpty(itemId))
                return "Usage: inv remove <itemId> [amount]";

            var result = inventory.Submit(InventoryCommand.RemoveItem(itemId, amount));
            return result.Applied
                ? $"[inv] Removed {result.Amount} x '{itemId}'. Now holding {inventory.GetAmmoCount(itemId)}."
                : $"[inv] Could not remove '{itemId}': {result.Reason}";
        }

        /// <summary>Grant armour by id, through the same command the pickup path will use.</summary>
        public string Armor(string armorId)
        {
            var (inventory, error) = RequireReady();
            if (error != null) return error;

            if (string.IsNullOrEmpty(armorId))
                return "Usage: inv armor <armorId>";

            var result = inventory.Submit(InventoryCommand.AddArmor(armorId));
            return result.Applied
                ? $"[inv] Added armour '{armorId}'."
                : $"[inv] Could not add armour '{armorId}': {result.Reason}";
        }

        /// <summary>Report how many rounds of <paramref name="ammoId"/> are held.</summary>
        public string Ammo(string ammoId)
        {
            var (inventory, error) = RequireReady();
            if (error != null) return error;

            if (string.IsNullOrEmpty(ammoId))
                return "Usage: inv ammo <ammoId>";

            return $"[inv] '{ammoId}': {inventory.GetAmmoCount(ammoId)} held.";
        }

        /// <summary>
        /// The guard every mutating command shares: resolve the inventory and prove it can actually
        /// accept a command, or return the reason it cannot.
        /// </summary>
        private (PlayerInventory inventory, string error) RequireReady()
        {
            var inventory = ResolveInventory();
            if (inventory == null) return (null, Status());
            if (!inventory.IsReady || inventory.Commands == null) return (null, Status());
            return (inventory, null);
        }
    }
}
