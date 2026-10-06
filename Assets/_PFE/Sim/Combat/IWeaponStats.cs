using PFE.Data.Definitions;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// Pure simulation contract for weapon visual parameters required by controllers and state machines.
    /// </summary>
    public interface IWeaponVisualStats
    {
        int shootFrameStart { get; }
    }

    /// <summary>
    /// Pure C# data container for weapon visual parameters.
    /// </summary>
    public class WeaponVisualSpec : IWeaponVisualStats
    {
        public int shootFrameStart { get; set; } = -1;
    }

    /// <summary>
    /// Pure simulation contract for weapon statistics.
    /// Decouples simulation calculators and weapon controllers from Unity ScriptableObjects.
    /// </summary>
    public interface IWeaponStats
    {
        string weaponId { get; }
        WeaponType weaponType { get; }
        int punch { get; }
        bool IsUnarmed { get; }
        int skillLevel { get; }
        int weaponLevel { get; }
        float baseDamage { get; }
        float rapid { get; }
        int autoMode { get; }
        bool IsAuto { get; }
        float precision { get; }
        float antiPrecision { get; }
        float deviation { get; }
        float armorPenetration { get; }
        float knockback { get; }
        float critChance { get; }
        float critMultiplier { get; }
        int projectilesPerShot { get; }
        float projectileSpeed { get; }
        int burstCount { get; }
        float explRadius { get; }
        float explosionDamage { get; }
        int explTip { get; }
        int explKol { get; }
        int magazineSize { get; }
        float reloadTime { get; }
        string ammoType { get; }
        int maxDurability { get; }
        int prepFrames { get; }
        int ammoPerShot { get; }
        int rechargeFrames { get; }
        int recoilFrames { get; }
        float recoilLift { get; }
        float magicPoolCost { get; }
        float manaHealthCost { get; }
        int throwTip { get; }
        int fuseFrames { get; }
        bool radio { get; }
        float sens { get; }
        MeleeType meleeType { get; }
        float meleeDlina { get; }
        float meleeMinDlina { get; }
        bool meleeCombo { get; }
        bool meleePowerAttack { get; }
        ProjectileArchetype projectileArchetype { get; }
        DamageType damageType { get; }
        float bulletGravity { get; }
        float bulletAccel { get; }
        int bulletFlame { get; }
        float bulletNavod { get; }
        bool isPhysBullet { get; }
        bool bumc { get; }
        string soundShoot { get; }
        string soundReload { get; }
        string soundHit { get; }
        string soundPrep { get; }
        int soundPrepT1 { get; }
        int soundPrepT2 { get; }
        float noiseRadius { get; }
        string soundFall { get; }
        string soundSens { get; }
        DecalType decalType { get; }
        float destroyTiles { get; }
        float piercing { get; }
        float penetration { get; }
        string dopEffect { get; }
        float dopDamage { get; }
        float dopChance { get; }
        bool alicornOnly { get; }
        bool spell { get; }
        IWeaponVisualStats weaponVisual { get; }
        string muzzleFlareId { get; }
        int shineRadius { get; }
        bool hasShell { get; }

        // PascalCase aliases
        string WeaponId => weaponId;
        float BaseDamage => baseDamage;
        WeaponType Type => weaponType;
        float ArmorPenetration => armorPenetration;
        float CritChance => critChance;
        float CritMultiplier => critMultiplier;
        float Rapid => rapid;
        int MagazineSize => magazineSize;
        float Precision => precision;
        float AntiPrecision => antiPrecision;
    }

    /// <summary>
    /// Pure C# data container for weapon stats used in simulation, tests, and headless environments.
    /// </summary>
    public class WeaponSpec : IWeaponStats
    {
        public string weaponId { get; set; } = string.Empty;
        public WeaponType weaponType { get; set; } = WeaponType.Guns;
        public int punch { get; set; } = 0;
        public bool IsUnarmed => punch > 0;
        public int skillLevel { get; set; } = 1;
        public int weaponLevel { get; set; } = 1;
        public float baseDamage { get; set; } = 10f;
        public float rapid { get; set; } = 10f;
        public int autoMode { get; set; } = 0;
        public bool IsAuto => autoMode == 1 ? false : autoMode == 2 ? true : rapid <= 6f;
        public float precision { get; set; } = 0f;
        public float antiPrecision { get; set; } = 0f;
        public float deviation { get; set; } = 0f;
        public float armorPenetration { get; set; } = 0f;
        public float knockback { get; set; } = 0f;
        public float critChance { get; set; } = 0.1f;
        public float critMultiplier { get; set; } = 2f;
        public int projectilesPerShot { get; set; } = 1;
        public float projectileSpeed { get; set; } = 100f;
        public int burstCount { get; set; } = 0;
        public float explRadius { get; set; } = 0f;
        public float explosionDamage { get; set; } = 0f;
        public int explTip { get; set; } = 1;
        public int explKol { get; set; } = 0;
        public int magazineSize { get; set; } = 0;
        public float reloadTime { get; set; } = 0f;
        public string ammoType { get; set; } = string.Empty;
        public int maxDurability { get; set; } = 100;
        public int prepFrames { get; set; } = 0;
        public int ammoPerShot { get; set; } = 1;
        public int rechargeFrames { get; set; } = 0;
        public int recoilFrames { get; set; } = 0;
        public float recoilLift { get; set; } = 0f;
        public float magicPoolCost { get; set; } = 0f;
        public float manaHealthCost { get; set; } = 0f;
        public int throwTip { get; set; } = 0;
        public int fuseFrames { get; set; } = 75;
        public bool radio { get; set; } = false;
        public float sens { get; set; } = 100f;
        public MeleeType meleeType { get; set; } = MeleeType.Horizontal;
        public float meleeDlina { get; set; } = 100f;
        public float meleeMinDlina { get; set; } = 100f;
        public bool meleeCombo { get; set; } = false;
        public bool meleePowerAttack { get; set; } = false;
        public ProjectileArchetype projectileArchetype { get; set; } = ProjectileArchetype.Ballistic;
        public DamageType damageType { get; set; } = DamageType.PhysicalBullet;
        public float bulletGravity { get; set; } = 0f;
        public float bulletAccel { get; set; } = 0f;
        public int bulletFlame { get; set; } = 0;
        public float bulletNavod { get; set; } = 0f;
        public bool isPhysBullet { get; set; } = false;
        public bool bumc { get; set; } = false;
        public string soundShoot { get; set; } = string.Empty;
        public string soundReload { get; set; } = string.Empty;
        public string soundHit { get; set; } = string.Empty;
        public string soundPrep { get; set; } = string.Empty;
        public int soundPrepT1 { get; set; } = 0;
        public int soundPrepT2 { get; set; } = 0;
        public float noiseRadius { get; set; } = 600f;
        public string soundFall { get; set; } = string.Empty;
        public string soundSens { get; set; } = string.Empty;
        public DecalType decalType { get; set; } = DecalType.None;
        public float destroyTiles { get; set; } = 0f;
        public float piercing { get; set; } = 0f;
        public float penetration { get; set; } = 0f;
        public string dopEffect { get; set; } = string.Empty;
        public float dopDamage { get; set; } = 0f;
        public float dopChance { get; set; } = 1f;
        public bool alicornOnly { get; set; } = false;
        public bool spell { get; set; } = false;
        public IWeaponVisualStats weaponVisual { get; set; }
        public string muzzleFlareId { get; set; } = string.Empty;
        public int shineRadius { get; set; } = 500;
        public bool hasShell { get; set; } = false;
    }
}
