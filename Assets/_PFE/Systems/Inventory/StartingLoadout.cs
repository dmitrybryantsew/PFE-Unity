using System;

namespace PFE.Systems.Inventory
{
    /// <summary>
    /// The player's starting inventory — the port of <c>Invent.addMin()</c>
    /// (<c>pfe/scripts/fe/unit/Invent.as:1600-1615</c>).
    ///
    /// <para><b>Where the Oracle grants it.</b> The <c>Invent</c> constructor
    /// (<c>Invent.as:102-110</c>) first registers <i>every</i> item row from
    /// <c>GameData.d.item</c> at quantity 0, then branches: a save to replay calls
    /// <c>addLoad</c>, and a fresh game calls <c>addMin()</c> when it carries a
    /// <c>propusk</c> permit and <c>addBegin()</c> otherwise. <c>addBegin()</c>
    /// (<c>:1618-1622</c>) grants only the Stable-boy armour; <c>addMin()</c> is the full kit.
    /// This table is <c>addMin</c>, transcribed id for id.</para>
    ///
    /// <para><b>Money is transcribed but deliberately ungranted.</b> <c>addMin</c> writes
    /// <c>items["money"].kol = 50</c>. The port does not model money as a held item at all:
    /// <c>GameInventory</c> records category 0 as "Not tracked (money, special)"
    /// (<c>GameInventory.cs:71-73</c>) and <c>PlayerPipStatusSource</c/> its <c>CapsProvider</c>
    /// is left unset for the same reason, so the monitor's caps row reads 0 —
    /// <c>PipBuck.as:51</c>'s own starting value. Granting a money <i>item</i> would put a row on
    /// the inventory page for a quantity the rest of the port deliberately does not track.
    /// The constant is therefore here as attested data, with the decision recorded next to it.</para>
    ///
    /// <para><b>Weapons are transcribed and cannot be granted yet.</b> The four ids are the
    /// Oracle's own, but <c>GameInventory.AddWeapon</c> builds a
    /// <see cref="PFE.Data.Definitions.GameWeaponInstance"/> from an <c>ItemDefinition</c> while
    /// the port's weapons are <c>WeaponDefinition</c> assets in <c>Resources/Weapons</c> and are
    /// <b>not</b> rows of the item table (there is no <c>Items/r32.asset</c>). The method returns
    /// null and logs for every id, so the gap is loud. <c>WeaponIds</c> is kept in this table
    /// rather than dropped so the grant — and the reason it is partial — is one greppable fact.</para>
    ///
    /// <para><b>Why a table and not an inline list in the loader.</b> The grant runs once, from a
    /// MonoBehaviour, at a moment nothing can schedule a test for. The table is plain data: the
    /// counts, the quantities and the favourite slots can be pinned against the Oracle offline,
    /// which is the only way they stay right if someone later wants the <c>addBegin</c> variant.</para>
    /// </summary>
    public static class StartingLoadout
    {
        /// <summary>An item id and the stack size it starts with — AS3 <c>items[id].kol</c>.</summary>
        public readonly struct Stack
        {
            public Stack(string itemId, int quantity)
            {
                ItemId = itemId;
                Quantity = quantity;
            }

            public string ItemId { get; }
            public int Quantity { get; }
        }

        /// <summary>
        /// A bound favourite — AS3 <c>favItem(id, slot)</c>. Slots are 1-based
        /// (<c>addMin</c> favours the cleaver on 1 and the pistol on 2).
        /// </summary>
        public readonly struct Favourite
        {
            public Favourite(string itemId, int slot)
            {
                ItemId = itemId;
                Slot = slot;
            }

            public string ItemId { get; }
            public int Slot { get; }
        }

        /// <summary><c>addWeapon("r32" / "rech" / "mont" / "bat")</c>. Not grantable yet — see the class summary.</summary>
        public static readonly string[] WeaponIds = { "r32", "rech", "mont", "bat" };

        /// <summary><c>addArmor("pip")</c>, the Stable-boy armour, also worn by <c>addBegin</c>.</summary>
        public static readonly string[] ArmorIds = { "pip" };

        /// <summary>
        /// <c>items["p32"].kol = 16</c> and the three single-count rows, in AS3's order.
        /// <c>p32</c> is the pistol rounds; <c>pot0</c>/<c>pot1</c> the two healing potions.
        /// </summary>
        public static readonly Stack[] Items =
        {
            new Stack("p32", 16),
            new Stack("pot0", 1),
            new Stack("pot1", 1),
            new Stack("screwdriver", 1),
        };

        /// <summary><c>favItem("mont", 1)</c> and <c>favItem("r32", 2)</c>.</summary>
        public static readonly Favourite[] Favourites =
        {
            new Favourite("mont", 1),
            new Favourite("r32", 2),
        };

        /// <summary><c>items["money"].kol = 50</c>. Recorded, not granted — see the class summary.</summary>
        public const int Money = 50;

        /// <summary>How many distinct ids the loadout names, for a log line and for the fixture.</summary>
        public static int IdCount =>
            WeaponIds.Length + ArmorIds.Length + Items.Length + 1 /* money */;
    }
}
