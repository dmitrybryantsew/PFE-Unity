using UnityEngine;

namespace PFE.Data.Definitions
{
    /// <summary>
    /// All enumerations for PFE data system.
    /// Matching the ActionScript constants from the original game.
    /// </summary>

    /// <summary>
    /// Fraction (Faction) type from AS3.
    /// 0 = Neutral, 1 = Player, 2 = Enemy, 3 = Unknown, 4 = Special (robots)
    /// </summary>
    public enum FactionType
    {
        Neutral = 0,
        Player = 1,
        Enemy = 2,
        Unknown = 3,
        Special = 4  // Robots
    }

    /// <summary>
    /// Unit category from AS3.
    /// 1 = Template, 2 = Faction, 3 = Spawnable
    /// </summary>
    public enum UnitCategory
    {
        Template = 1,
        Faction = 2,
        Spawnable = 3
    }

    /// <summary>
    /// Blood type from AS3.
    /// 0 = None, 1 = Red, 2 = Green, 3 = Pink
    /// </summary>
    public enum BloodType
    {
        None = 0,
        Red = 1,
        Green = 2,
        Pink = 3
    }

    /// <summary>
    /// Gender from AS3.
    /// </summary>
    public enum Gender
    {
        Male,
        Female,
        Other
    }

    /// <summary>
    /// Damage type enumeration matching the original game.
    /// Based on Unit.as D_BUL through D_PINK constants.
    /// </summary>
    public enum DamageType
    {
        PhysicalBullet = 0,   // D_BUL
        Blade = 1,            // D_BLADE
        PhysicalMelee = 2,    // D_PHIS
        Fire = 3,             // D_FIRE
        Explosive = 4,        // D_EXPL
        Laser = 5,            // D_LASER
        Plasma = 6,           // D_PLASMA
        Venom = 7,            // D_VENOM
        EMP = 8,              // D_EMP
        Spark = 9,            // D_SPARK
        Acid = 10,            // D_ACID
        Cryo = 11,            // D_CRIO
        Poison = 12,          // D_POISON
        Bleed = 13,           // D_BLEED
        Fang = 14,            // D_FANG
        Balefire = 15,        // D_BALE
        Necrotic = 16,        // D_NECRO
        Psionic = 17,         // D_PSY
        Astral = 18,          // D_ASTRO
        Pink = 19,            // D_PINK
        Internal = 100,       // D_INSIDE
        FriendlyFire = 101    // D_FRIEND
    }

    /// <summary>
    /// Weapon type enumeration from AS3.
    /// tip='0' = Internal/Unarmed
    /// tip='1' = Melee
    /// tip='2' = Guns (Small guns, pistols, rifles)
    /// tip='3' = Big guns (Heavy weapons)
    /// tip='4' = Thrown
    /// tip='5' = Magic
    /// </summary>
    public enum WeaponType
    {
        Internal = 0,
        Melee = 1,
        Guns = 2,
        BigGun = 3,
        Thrown = 4,
        Magic = 5
    }

    /// <summary>
    /// Weapon category for weapon type '2' (Guns).
    /// 0 = Unarmed, 1 = Melee, 2 = Pistol, 3 = SMG, 4 = Shotgun, 5 = Rifle, 6 = Heavy, 7 = Sniper, 8 = Explosive, 9 = Magic
    /// </summary>
    public enum WeaponCategory
    {
        Unarmed = 0,
        Melee = 1,
        Pistol = 2,
        SMG = 3,
        Shotgun = 4,
        Rifle = 5,
        Heavy = 6,
        Sniper = 7,
        Explosive = 8,
        Magic = 9
    }

    /// <summary>
    /// Melee weapon subtype from AS3.
    /// </summary>
    public enum MeleeSubType
    {
        Unarmed = 0,
        Sword = 1,
        Axe = 2,
        Sledge = 3,
        Spear = 4,
        Knife = 5,
        Club = 6,
        Fist = 7
    }

    /// <summary>
    /// Thrown weapon subtype from AS3.
    /// </summary>
    public enum ThrownSubType
    {
        Grenade = 0,
        Mine = 1,
        ThrownWeapon = 2
    }

    /// <summary>
    /// Melee weapon type from AS3.
    /// mtip='0' - Horizontal swing (sword, bat)
    /// mtip='1' - Thrust (spear)
    /// mtip='2' - Overhead smash (hammer)
    /// </summary>
    public enum MeleeType
    {
        Horizontal = 0,
        Thrust = 1,
        Overhead = 2
    }

    /// <summary>
    /// Item type enumeration from AS3.
    /// tip='a' = Ammo
    /// tip='m' = Medical
    /// tip='b' = Book
    /// tip='c' = Component
    /// tip='e' = Equipment
    /// tip='s' = Sphera (Artifact)
    /// tip='i' = Implant
    /// </summary>
    public enum ItemType
    {
        Ammo,
        Medical,
        Book,
        Component,
        Equipment,
        Chems,  // Potions
        Sphera,  // Artifacts
        Implant,
        Key,
        Quest,
        Valuable,
        Misc
    }

    /// <summary>
    /// Item subcategory for component type.
    /// </summary>
    public enum ComponentCategory
    {
        General,
        Mechanical,
        Electronic,
        Plant,
        Energy,
        Alchemy
    }

    /// <summary>
    /// Inventory category for UI organization
    /// </summary>
    public enum InventoryCategory
    {
        General = 0,
        Weapons = 1,
        Apparel = 2,
        Aid = 3,
        Misc = 4,
        Ammo = 5,
        Books = 6,
        Keys = 7
    }

    /// <summary>
    /// How the item is used
    /// </summary>
    public enum UsageType
    {
        None = 0,
        SingleUse = 1,
        Consumable = 2,
        Equipment = 3
    }

    /// <summary>
    /// Ammo modifier variants
    /// </summary>
    public enum AmmoModifier
    {
        None = 0,
        ArmorPiercing = 1,
        Expansive = 2,
        Pulse = 3,
        Incendiary = 4,
        Plasma = 5,
        Overcharge = 6,
        Napalm = 7,
        Cryo = 8,
        Magic = 9
    }

    /// <summary>
    /// Effect type from AS3.
    /// tip='1' = Good
    /// tip='2' = Bad
    /// tip='3' = Special
    /// tip='4' = Neutral
    /// </summary>
    public enum EffectType
    {
        Good = 1,
        Bad = 2,
        Special = 4,
        Neutral = 0
    }

    /// <summary>
    /// Perk type from AS3.
    /// tip='1' = Player selectable
    /// tip='0' = Automatic/effect only
    /// </summary>
    public enum PerkType
    {
        Automatic = 0,
        Selectable = 1
    }

    /// <summary>
    /// Decal type from AS3 — tipdec values in weapon XML vis node.
    /// Values match the original tipdec integers directly.
    /// </summary>
    public enum DecalType
    {
        None        = 0,
        Metal       = 1,   // metal bullet hole
        Stone       = 2,   // stone/rock impact
        Rail        = 3,   // railgun / piercing
        Blade       = 4,   // melee / blade slash
        Wood        = 5,   // wood splinter
        // 6-8 unused in source
        Explosive   = 9,   // explosion scorch
        // 10 unused
        FireEnergy  = 11,  // fire / energy burn
        Laser       = 12,  // laser scorch
        EnergyBeam  = 13,  // energy beam mark
        // 14 unused
        Plasma      = 15,  // plasma burn
        // 16-18 unused
        Sparkle     = 19,  // special sparkle / pink
    }

    /// <summary>
    /// Projectile visual and physics archetype.
    /// Determines which prefab to spawn and how the bullet behaves.
    /// Derived at import time from vbul, spring, flame, phisbul and navod XML fields.
    /// </summary>
    public enum ProjectileArchetype
    {
        /// <summary>Standard ballistic round — spring=1, no gravity override, default physics.</summary>
        Ballistic  = 0,
        /// <summary>Laser beam — spring=2, speed≥2000, stretched from origin.</summary>
        Laser      = 1,
        /// <summary>Plasma orb — vbul contains "plasma", additive glow.</summary>
        Plasma     = 2,
        /// <summary>Flame / fire — flame>0, short lifetime, gravity arc.</summary>
        Flame      = 3,
        /// <summary>Physics projectile (grenade, rocket) — phisbul=1, Dynamic Rigidbody, AoE.</summary>
        Explosive  = 4,
        /// <summary>Spark / electrical — vbul="spark"|"sparkl"|"lightning", animated.</summary>
        Spark      = 5,
        /// <summary>Spit / acid / venom — vbul contains "plevok"|"venom"|"kapl".</summary>
        Spit       = 6,
        /// <summary>Homing missile — navod>0, SmartBullet tracking.</summary>
        Homing     = 7,
        /// <summary>Innate unicorn magic spell — tip==5 (WMagic), spawns from horn, costs mana.</summary>
        Magic      = 8,
    }

    /// <summary>
    /// Location type from AS3.
    /// </summary>
    public enum LocationType
    {
        Interior,
        Exterior,
        Dungeon,
        Town,
        Special,
        Boss,
        Tutorial
    }

    /// <summary>
    /// Value tier from AS3.
    /// Determines item drop probability.
    /// </summary>
    public enum ValueTier
    {
        Trash = 0,
        Common = 1,
        Uncommon = 2,
        Rare = 3,
        Epic = 4,
        Legendary = 5
    }

    /// <summary>
    /// Skill requirement types from AS3.
    /// </summary>
    public enum RequirementType
    {
        Level,
        Skill,
        Guns,  // Small guns OR Energy weapons
        Perk
    }

    /// <summary>
    /// Stat modifier types from AS3.
    /// ref='add' = Add to base
    /// ref='mult' = Multiply with base
    /// (no ref) = Set value
    /// </summary>
    public enum ModifierType
    {
        Add,
        Multiply,
        Set,
        WeaponSkill  // Special case for weapon skills
    }

    /// <summary>
    /// Stat modifier target from AS3.
    /// </summary>
    public enum ModifierTarget
    {
        Player,
        Pers,  // Character
        Unit   // Enemy/NPC
    }
}
