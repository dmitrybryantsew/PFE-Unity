using PFE.Data.Definitions;

namespace PFE.Systems.Inventory
{
    /// <summary>
    /// AS3's Pip-Boy inventory pages — the top-level tabs of <c>PipPageInv</c> — and which item type
    /// belongs to each.
    ///
    /// <para><b>The oracle's tab table</b>, <c>fe/inter/PipPageInv.as:33</c> (index 0 is unused; the
    /// array is indexed by <c>page2</c>, and each inner array is the page's <i>sub-tabs</i>):</para>
    /// <code>
    /// tips = [
    ///   [],
    ///   ["", "w1","w2","w4","w5","w6","w3"],                                      // 1 WEAPONS
    ///   ["", "armor1","armor3"],                                                  // 2 ARMOR
    ///   ["", "med",["him","pot"],"food",["equip","spell"],["book","sphera","note"],"paint"],  // 3 AID
    ///   ["", ["valuables","money"],["spec","key"],["impl","art","instr","equip"],
    ///        ["stuff","compa","compw","compe","compm"],["compp","food"],"scheme"],            // 4 MISC
    ///   ["", "a","e"]                                                             // 5 AMMO
    /// ];
    /// </code>
    ///
    /// <para><b>AS3's pages overlap, and this port deliberately does not reproduce that.</b>
    /// <c>equip</c> appears in both AID (sub-tab 4) and MISC (sub-tab 3), and <c>food</c> in both AID
    /// (sub-tab 3) and MISC (sub-tab 5) — an item is listed under two tabs depending on which sub-tab you
    /// pick. That is meaningful in the Pip-Boy, where the sub-tab <i>is</i> the filter; in a flat debug
    /// list it would just print the same row twice. So <see cref="PageOf"/> is a <b>partition</b>: every
    /// <see cref="ItemType"/> belongs to exactly one page.</para>
    ///
    /// <para><b>Weapons and armour are not in the item table.</b> AS3's weapons and armours are separate
    /// associative arrays (<c>inv.weapons</c> / <c>inv.armors</c>) exactly as the port's
    /// <c>GameInventory</c> keeps them, so those two pages read those dictionaries rather than the item
    /// dict. <see cref="PageOf"/> therefore only covers the three item-backed pages.</para>
    /// </summary>
    public enum InventoryPage
    {
        /// <summary>AS3 page 1 — the weapon list (<c>inv.weapons</c>).</summary>
        Weapons = 0,

        /// <summary>AS3 page 2 — the armour list (<c>inv.armors</c>).</summary>
        Armor = 1,

        /// <summary>AS3 page 3 — medical, chems, food, books, spells, artefacts.</summary>
        Aid = 2,

        /// <summary>AS3 page 4 — valuables, keys, implants, components, general stuff.</summary>
        Misc = 3,

        /// <summary>AS3 page 5 — ammunition and explosives.</summary>
        Ammo = 4,
    }

    /// <summary>
    /// Which <see cref="InventoryPage"/> an item belongs to, and the display name of each page.
    /// Unity-free so the offline wall can pin the partition.
    /// </summary>
    public static class InventoryPageRules
    {
        /// <summary>How many pages there are. Mirrors AS3's five item pages.</summary>
        public const int PageCount = 5;

        /// <summary>The AS3 page's own name, used for the tab caption.</summary>
        public static string Name(InventoryPage page)
        {
            switch (page)
            {
                case InventoryPage.Weapons: return "Weapons";
                case InventoryPage.Armor:   return "Armor";
                case InventoryPage.Aid:     return "Aid";
                case InventoryPage.Misc:    return "Misc";
                case InventoryPage.Ammo:    return "Ammo";
                default:                    return "?";
            }
        }

        /// <summary>
        /// Whether a page reads the item dictionary. <c>false</c> for <see cref="InventoryPage.Weapons"/>
        /// and <see cref="InventoryPage.Armor"/>, which read their own dictionaries — the caller must not
        /// filter the item dict by those pages, or it will list nothing and look empty.
        /// </summary>
        public static bool IsItemBacked(InventoryPage page)
            => page == InventoryPage.Aid || page == InventoryPage.Misc || page == InventoryPage.Ammo;

        /// <summary>
        /// The page an item type is listed under.
        ///
        /// <para><b>A partition, not AS3's overlapping sets</b> — see the enum's remarks. The deviations
        /// from AS3, both deliberate and both visible in the UI because the row prints its
        /// <see cref="ItemType"/>:</para>
        /// <list type="bullet">
        /// <item><description><c>Equipment</c> (AS3 <c>e</c> and <c>equip</c>, collapsed by the import)
        /// goes to <b>Misc</b>. AS3 would put <c>equip</c> in AID too and <c>e</c> in AMMO.</description></item>
        /// <item><description><c>Misc</c> (AS3 <c>food</c>, <c>note</c>, <c>paint</c>, <c>stuff</c>,
        /// <c>instr</c>, <c>scheme</c>, <c>money</c>, …) goes to <b>Misc</b>, where AS3 would split
        /// <c>food</c>/<c>paint</c> into AID.</description></item>
        /// </list>
        /// </summary>
        public static InventoryPage PageOf(ItemType type)
        {
            switch (type)
            {
                case ItemType.Ammo:
                    return InventoryPage.Ammo;

                case ItemType.Medical:
                case ItemType.Chems:
                case ItemType.Book:
                case ItemType.Sphera:
                case ItemType.Spell:
                    return InventoryPage.Aid;

                default:
                    // Misc, Component, Implant, Key, Quest, Valuable, Equipment.
                    return InventoryPage.Misc;
            }
        }

        /// <summary>Whether an item of this type is listed on this page.</summary>
        public static bool Contains(InventoryPage page, ItemType type)
            => IsItemBacked(page) && PageOf(type) == page;
    }
}
