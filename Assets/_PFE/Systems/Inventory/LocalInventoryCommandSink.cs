using System;
using PFE.Data.Definitions;

namespace PFE.Systems.Inventory
{
    /// <summary>
    /// Single-player <see cref="IInventoryCommandSink"/>: applies a command to the local
    /// <see cref="GameInventory"/> immediately, because in single-player this process <i>is</i> the
    /// authority.
    ///
    /// <para><b>This is the class the netcode replaces, and nothing else.</b> When host-authoritative
    /// co-op lands, a networked sink forwards the command to the host and applies the replicated
    /// result; the command type, the validation, the callers and <see cref="PlayerInventory"/> all stay
    /// as they are. That is the whole point of introducing the seam before it is needed.</para>
    ///
    /// <para><b>Id resolution happens here, on the applying side.</b> The command carries an id; this
    /// sink turns it into a <see cref="ItemDefinition"/> using the resolvers it was given. In
    /// single-player the resolvers read the content registry; a host would read the same table. A
    /// client never resolves — which is why the command does not carry a definition.</para>
    ///
    /// <para><b>Validation first, then resolution, then the inventory.</b> A malformed command is
    /// rejected before any table is consulted, and an unknown id is rejected before
    /// <see cref="GameInventory"/> is touched — otherwise a user typo would produce a
    /// <c>Debug.LogError</c> from deep inside the inventory and read as a broken system rather than a
    /// bad id.</para>
    /// </summary>
    public sealed class LocalInventoryCommandSink : IInventoryCommandSink
    {
        private readonly GameInventory _inventory;
        private readonly Func<string, ItemDefinition> _itemResolver;
        private readonly Func<string, ItemDefinition> _armorResolver;

        /// <param name="inventory">The inventory this sink owns. Must not be null.</param>
        /// <param name="itemResolver">Id → item row (AS3 <c>AllData.d.item</c>). May be null, in which
        /// case every item command is rejected with a reason rather than throwing.</param>
        /// <param name="armorResolver">Id → armour row (AS3 <c>AllData.d.armor</c>). May be null.</param>
        public LocalInventoryCommandSink(
            GameInventory inventory,
            Func<string, ItemDefinition> itemResolver,
            Func<string, ItemDefinition> armorResolver)
        {
            _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
            _itemResolver = itemResolver;
            _armorResolver = armorResolver;
        }

        /// <inheritdoc/>
        public InventoryCommandResult Submit(InventoryCommand command)
        {
            string malformed = InventoryCommandValidation.RejectReason(command);
            if (malformed != null)
                return InventoryCommandResult.Fail(malformed);

            switch (command.Kind)
            {
                case InventoryCommandKind.AddItem:
                    return ApplyAddItem(command);

                case InventoryCommandKind.RemoveItem:
                    return ApplyRemoveItem(command);

                case InventoryCommandKind.ConsumeAmmo:
                    return ApplyConsumeAmmo(command);

                case InventoryCommandKind.AddArmor:
                    return ApplyAddArmor(command);

                case InventoryCommandKind.DropItem:
                    return ApplyDropItem(command);

                default:
                    // Unreachable: RejectReason already rejected every unknown kind. Kept so a future
                    // kind added without a case here is a stated failure, not a silent success.
                    return InventoryCommandResult.Fail($"unsupported command kind {command.Kind}");
            }
        }

        private InventoryCommandResult ApplyAddItem(InventoryCommand command)
        {
            ItemDefinition definition = _itemResolver?.Invoke(command.Id);
            if (definition == null)
                return InventoryCommandResult.Fail($"no item row for '{command.Id}'");

            return _inventory.AddItem(definition, command.Quantity, command.PickupType)
                ? InventoryCommandResult.Ok(command.Quantity)
                : InventoryCommandResult.Fail($"the inventory refused {command.Quantity} x '{command.Id}'");
        }

        private InventoryCommandResult ApplyRemoveItem(InventoryCommand command)
        {
            // Asked before the call so the failure is a stated reason and not the inventory's own
            // warning log — which names the numbers but not the command that asked for them.
            if (!_inventory.HasItem(command.Id, command.Quantity))
            {
                int held = _inventory.GetAmmoCount(command.Id);
                return InventoryCommandResult.Fail(
                    $"only {held} x '{command.Id}' held, {command.Quantity} requested");
            }

            return _inventory.RemoveItem(command.Id, command.Quantity)
                ? InventoryCommandResult.Ok(command.Quantity)
                : InventoryCommandResult.Fail($"the inventory refused to remove {command.Quantity} x '{command.Id}'");
        }

        private InventoryCommandResult ApplyConsumeAmmo(InventoryCommand command)
        {
            // Applied, with the count actually drawn. A short draw (asked 12, held 5) is a success with
            // Amount 5, not a failure: the reload path has to be able to read the real number.
            int drawn = _inventory.ConsumeAmmo(command.Id, command.Quantity);
            return InventoryCommandResult.Ok(drawn);
        }

        private InventoryCommandResult ApplyAddArmor(InventoryCommand command)
        {
            // Checked against the armour table before calling AddArmor, which logs an error for an
            // unknown id — a user typo should be a readable rejection, not a red error in the console.
            ItemDefinition armor = _armorResolver?.Invoke(command.Id);
            if (armor == null)
                return InventoryCommandResult.Fail($"no armour row for '{command.Id}'");

            return _inventory.AddArmor(command.Id) != null
                ? InventoryCommandResult.Ok(1)
                : InventoryCommandResult.Fail($"the inventory refused armour '{command.Id}'");
        }

        /// <summary>
        /// Remove what is being dropped, <b>clamped to what is actually held</b>, and report the real
        /// amount so the caller can size the world object it spawns.
        ///
        /// <para><b>Clamped rather than rejected, because that is what AS3 does.</b>
        /// <c>Invent.drop()</c> (<c>:1535</c>) reads
        /// <c>if (param2 &gt; this.items[param1].kol) param2 = this.items[param1].kol;</c> and then
        /// returns early only if the result is <c>&lt;= 0</c>. So "drop 500 of 3" drops 3 — a request
        /// honoured as far as it can be is not an error. This is the same shape as
        /// <see cref="ApplyConsumeAmmo"/>, for the same reason.</para>
        ///
        /// <para><b>Nothing held is a rejection, not a success with 0.</b> The caller spawns a world
        /// object sized by <see cref="InventoryCommandResult.Amount"/>, so an <c>Ok(0)</c> would invite
        /// an empty pickup — a success that is not evidence.</para>
        ///
        /// <para><b>Only the inventory half happens here.</b> The world object is the caller's job; see
        /// <see cref="InventoryCommandKind.DropItem"/>.</para>
        /// </summary>
        private InventoryCommandResult ApplyDropItem(InventoryCommand command)
        {
            GameItemInstance held = _inventory.GetItem(command.Id);
            int heldCount = held?.Quantity ?? 0;

            if (heldCount <= 0)
                return InventoryCommandResult.Fail($"nothing to drop: no '{command.Id}' held");

            int amount = Math.Min(command.Quantity, heldCount);

            return _inventory.RemoveItem(command.Id, amount)
                ? InventoryCommandResult.Ok(amount)
                : InventoryCommandResult.Fail($"the inventory refused to drop {amount} x '{command.Id}'");
        }
    }
}
