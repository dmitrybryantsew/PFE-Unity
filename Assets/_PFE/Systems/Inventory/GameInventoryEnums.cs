using UnityEngine;
using PFE.Data.Definitions;

namespace PFE.Systems.Inventory
{
    /// <summary>
    /// Item type identifiers matching ActionScript Item.as
    /// Maps to the tip field in AS3 item definitions
    ///
    /// These constants are used for type comparison rather than enum values
    /// because AS3 uses string values for item types
    /// </summary>
    public static class ItemTypeId
    {
        // Core types
        public const string Item = "item";
        public const string Armor = "armor";
        public const string Weapon = "weapon";
        public const string Uniq = "uniq";     // Unique items (maps to weapon)
        public const string Spell = "spell";

        // Consumables
        public const string Ammo = "a";        // Ammunition
        public const string Expl = "e";        // Explosives/throwables
        public const string Med = "med";       // Medical supplies
        public const string Pot = "pot";       // Potions
        public const string Food = "food";     // Food items
        public const string Him = "him";       // Drugs/chems

        // Knowledge
        public const string Book = "book";     // Skill books
        public const string Scheme = "scheme"; // Crafting schematics

        // Crafting
        public const string Paint = "paint";   // Weapon paints
        public const string Compa = "compa";   // General components
        public const string Compw = "compw";   // Weapon components
        public const string Compe = "compe";   // Energy components
        public const string Compm = "compm";   // Medical components
        public const string Compp = "compp";   // Plant/ingredient components

        // Special
        public const string Spec = "spec";     // Special items
        public const string Instr = "instr";   // Instruments
        public const string Stuff = "stuff";   // Misc stuff
        public const string Art = "art";       // Artifacts
        public const string Impl = "impl";     // Implants
        public const string Key = "key";       // Keys
    }

    // Note: InventoryCategory is defined in PFE.Data.Definitions
    // using PFE.Data.Definitions;

    /// <summary>
    /// Weapon "respect" state from AS3 Invent.as
    /// Controls availability and weight calculation
    /// respect: 0=active, 1=hidden, 2=favorited, 3=scheme only
    /// </summary>
    public enum WeaponRespect
    {
        Active = 0,      // Counts toward weight, can use
        Hidden = 1,      // Doesn't count, can't use (except at base)
        Favorited = 2,   // Counts toward weight, quick access
        SchemeOnly = 3   // Unlocked by scheme, can't drop
    }

    /// <summary>
    /// New item status for UI highlighting
    /// From AS3 Invent.as nov field
    /// </summary>
    public enum NewItemStatus
    {
        None = 0,
        New = 1,         // Just acquired
        Recent = 2       // Acquired recently
    }

    /// <summary>
    /// Pickup type matching AS3 Invent.as take() method
    /// tr parameter: 0=loot, 1=trade, 2=reward
    /// </summary>
    public enum PickupType
    {
        Loot = 0,        // Picked up from container/ground
        Trade = 1,       // Bought/traded
        Reward = 2       // Quest reward
    }
}
