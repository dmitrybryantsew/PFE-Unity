using System;

namespace PFE.Systems.Inventory
{
    /// <summary>
    /// What a caller is asking the inventory to do.
    ///
    /// <para><b>Why an enum and not a method call.</b> Every mutation of the player's inventory has to
    /// become a request that one authority resolves, because the project's target is host-authoritative
    /// co-op (<c>docs/multiplayer/OnMultiplayerAddition.md</c>: host owns shared truth, clients send
    /// requests, host replicates outcomes). Calling <c>GameInventory.AddItem</c> straight from gameplay
    /// would work today and turn every call site into a migration target the day the netcode lands.
    /// A command is that request in its transport-ready shape: a small value with no Unity object
    /// references, so it can be serialised and validated by the host.</para>
    /// </summary>
    public enum InventoryCommandKind
    {
        /// <summary>Add <see cref="InventoryCommand.Quantity"/> of item <see cref="InventoryCommand.Id"/>.</summary>
        AddItem,

        /// <summary>Remove <see cref="InventoryCommand.Quantity"/> of item <see cref="InventoryCommand.Id"/>.</summary>
        RemoveItem,

        /// <summary>Add the armour whose id is <see cref="InventoryCommand.Id"/> (quantity is always 1).</summary>
        AddArmor,

        /// <summary>Draw up to <see cref="InventoryCommand.Quantity"/> rounds of ammo id <see cref="InventoryCommand.Id"/>.</summary>
        ConsumeAmmo,

        /// <summary>
        /// Remove <see cref="InventoryCommand.Quantity"/> of item <see cref="InventoryCommand.Id"/> <b>in order to
        /// drop it into the world</b> — AS3 <c>Invent.drop()</c>.
        ///
        /// <para><b>Why this is a distinct kind and not just <see cref="RemoveItem"/>.</b> The inventory
        /// mutation is identical, but the <i>intent</i> is not, and under host-authoritative co-op the two
        /// are replicated differently: removing an item destroys it, whereas a drop must also create a
        /// world entity on every peer. A host receiving this kind knows to spawn the loot; receiving
        /// <see cref="RemoveItem"/> it knows not to. Collapsing them would make the two indistinguishable
        /// at the one place the distinction matters.</para>
        ///
        /// <para><b>The sink applies only the inventory half.</b> It clamps to what is held and removes,
        /// returning the amount actually removed as <see cref="InventoryCommandResult.Amount"/>. Creating
        /// the world object is the caller's job (<c>PlayerInventory.DropItem</c>), because a sink that
        /// instantiated prefabs could not be exercised offline and would not be the piece the netcode
        /// replaces.</para>
        /// </summary>
        DropItem
    }

    /// <summary>
    /// One request against a player's inventory.
    ///
    /// <para><b>It carries an id, never a definition.</b> A definition is a Unity
    /// <c>ScriptableObject</c> — not serialisable across a network boundary and not resolvable on the
    /// host from a client's pointer. The sink resolves the id on the side that owns the table, which is
    /// exactly what AS3 does (<c>Invent.as</c> indexes <c>AllData.d.item</c> / <c>d.armor</c> by id) and
    /// exactly what a host has to do with a request that arrives from a client.</para>
    /// </summary>
    public readonly struct InventoryCommand
    {
        public readonly InventoryCommandKind Kind;

        /// <summary>Item id, armour id, or ammo id, depending on <see cref="Kind"/>.</summary>
        public readonly string Id;

        /// <summary>Units to add/remove/consume. Always 1 for <see cref="InventoryCommandKind.AddArmor"/>.</summary>
        public readonly int Quantity;

        /// <summary>AS3 <c>Invent.take()</c>'s <c>tr</c> argument; only read by
        /// <see cref="InventoryCommandKind.AddItem"/>.</summary>
        public readonly PickupType PickupType;

        /// <summary>
        /// Public, unlike the rest of this codebase's value types, because a network deserializer has to
        /// rebuild a command from the wire — <b>including a kind this build does not recognise</b>, which
        /// is precisely the case <see cref="InventoryCommandValidation.IsKnownKind"/> exists to catch.
        /// A private constructor would make that path untestable and leave the guard unprovable. The
        /// factories below remain the way gameplay constructs one.
        /// </summary>
        public InventoryCommand(InventoryCommandKind kind, string id, int quantity, PickupType pickupType)
        {
            Kind = kind;
            Id = id;
            Quantity = quantity;
            PickupType = pickupType;
        }

        public static InventoryCommand AddItem(string itemId, int quantity = 1,
                                               PickupType pickupType = PickupType.Loot)
            => new InventoryCommand(InventoryCommandKind.AddItem, itemId, quantity, pickupType);

        public static InventoryCommand RemoveItem(string itemId, int quantity = 1)
            => new InventoryCommand(InventoryCommandKind.RemoveItem, itemId, quantity, PickupType.Loot);

        public static InventoryCommand AddArmor(string armorId)
            => new InventoryCommand(InventoryCommandKind.AddArmor, armorId, 1, PickupType.Loot);

        public static InventoryCommand ConsumeAmmo(string ammoId, int amount)
            => new InventoryCommand(InventoryCommandKind.ConsumeAmmo, ammoId, amount, PickupType.Loot);

        /// <summary>
        /// Drop <paramref name="quantity"/> of <paramref name="itemId"/> into the world. The sink removes
        /// what is held (clamped) and reports the real amount; the caller spawns the loot with it.
        /// </summary>
        public static InventoryCommand DropItem(string itemId, int quantity = 1)
            => new InventoryCommand(InventoryCommandKind.DropItem, itemId, quantity, PickupType.Loot);

        public override string ToString() => $"{Kind}('{Id}' x{Quantity})";
    }

    /// <summary>
    /// The authority's answer to an <see cref="InventoryCommand"/>.
    ///
    /// <para><see cref="Applied"/> is the only field a caller must read; <see cref="Amount"/> carries the
    /// units actually moved, which is what <see cref="IAmmoSource.ConsumeAmmo"/> has to return (a reload
    /// that asked for 12 and got 5 must know it got 5). <see cref="Reason"/> is always set on rejection
    /// and is meant to be printed, not swallowed — a silent no-op is the failure shape this project
    /// keeps paying for.</para>
    /// </summary>
    public readonly struct InventoryCommandResult
    {
        public readonly bool Applied;
        public readonly int Amount;
        public readonly string Reason;

        private InventoryCommandResult(bool applied, int amount, string reason)
        {
            Applied = applied;
            Amount = amount;
            Reason = reason;
        }

        // Named Ok/Fail rather than Applied/Rejected: the struct already has an `Applied` *field*, and
        // a static method of the same name is CS0102, not an overload.
        public static InventoryCommandResult Ok(int amount = 0)
            => new InventoryCommandResult(true, amount, null);

        public static InventoryCommandResult Fail(string reason)
            => new InventoryCommandResult(false, 0, reason ?? "rejected");

        public override string ToString()
            => Applied ? $"applied ({Amount})" : $"rejected: {Reason}";
    }

    /// <summary>
    /// The single mutation entry point for a player's inventory.
    ///
    /// <para><b>Gameplay submits; the authority applies.</b> Today the only implementation is
    /// <see cref="LocalInventoryCommandSink"/>, which applies the command to the local
    /// <see cref="GameInventory"/> immediately — single-player has one authority, this process. Under
    /// host-authoritative co-op the implementation becomes "send to the host and wait for the
    /// replicated outcome", and no call site changes. That swap is the entire reason this interface
    /// exists before it is needed.</para>
    ///
    /// <para><b>Reads do not go through here.</b> Queries (<c>GetAmmoCount</c>, <c>HasItem</c>) are
    /// local and cheap; only state changes are requests. See <see cref="PlayerInventory"/>, which
    /// implements <see cref="IAmmoSource"/> by reading locally and consuming through this seam.</para>
    /// </summary>
    public interface IInventoryCommandSink
    {
        InventoryCommandResult Submit(InventoryCommand command);
    }
}
