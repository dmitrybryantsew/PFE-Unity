using UnityEngine;
using PFE.ModAPI;
#if ODIN_INSPECTOR
using Sirenix.OdinInspector;
#endif

namespace PFE.Data.Definitions
{
    /// <summary>
    /// ScriptableObject definition for weapon data.
    /// Replaces XML-based weapon definitions from AllData.as in ActionScript.
    /// Create instances via Assets > Create > PFE > Weapon Definition
    /// </summary>
    [CreateAssetMenu(fileName = "NewWeaponDef", menuName = "PFE/Weapon Definition")]
    public class WeaponDefinition : ScriptableObject, IGameContent
    {
        [Header("Identification")]
        public string weaponId;

        // Legacy property for compatibility
        public string ID => weaponId;

        // IGameContent
        string IGameContent.ContentId => weaponId;
        ContentType IGameContent.ContentType => ContentType.Weapon;

        [Tooltip("Weapon type determines firing behavior")]
        public WeaponType weaponType;

        // Legacy property for compatibility
        public WeaponType Type => weaponType;

        public int skillLevel;              // Required skill level
        public int weaponLevel;              // Weapon level

        [Header("Combat Stats")]
        public float baseDamage = 10f;       // Base damage from XML

        // Legacy property for compatibility
        public float Damage => baseDamage;

        [Tooltip("Fire cooldown in frames (rapid in AS3, 30 FPS = 1 second)")]
        public float rapid = 10f;

        // Legacy property for compatibility
        public int FireRateFrames => (int)rapid;

        [Tooltip("Accuracy stat (higher = better, scales with distance)")]
        public float precision = 0f;

        // Legacy property for compatibility
        public float Accuracy => precision;

        [Tooltip("Spread angle in degrees (lower = better)")]
        public float deviation = 0f;

        public float armorPenetration = 0f;  // pier in AS3
        public float knockback = 0f;         // otbros in AS3

        [Header("Critical Hit")]
        [Range(0f, 1f)]
        public float critChance = 0.1f;      // critCh in AS3
        public float critMultiplier = 2f;    // critM in AS3

        [Header("Projectile")]
        public int projectilesPerShot = 1;   // kol in AS3
        public float projectileSpeed = 100f; // speed in AS3

        [Header("Burst Fire")]
        public int burstCount = 0;           // dkol in AS3

        [Header("Explosion")]
        public float explRadius = 0f;        // Explosion radius
        public float explosionDamage = 0f;   // damageExpl in AS3

        [Header("Ammunition")]
        public int magazineSize = 0;         // holder in AS3
        public float reloadTime = 0f;        // reload in AS3 (frames)
        public string ammoType;

        // Legacy property for compatibility
        public string AmmoTypeID => ammoType;

        [Header("Durability")]
        public int maxDurability = 100;      // maxhp in AS3

        [Header("Fire Timing — AS3 parity")]
        [Tooltip("Charge-up frames before first shot fires (prep in AS3). 0 = no charge. Minigun, railgun, etc.")]
        public int prepFrames;
        [Tooltip("Ammo consumed per shot (rashod in AS3). Most weapons = 1. Railgun = 2, etc.")]
        public int ammoPerShot = 1;
        [Tooltip("Self-regenerating ammo cadence in frames (recharg in AS3). 0 = no recharge. Used by 'recharg' ammo weapons.")]
        public int rechargeFrames;

        [Header("Recoil")]
        [Tooltip("Duration of push-back in frames after shot (recoil in AS3). Applied as positional offset along aim axis.")]
        public int recoilFrames;
        [Tooltip("Angular lift added to rotation per shot (recoilUp in AS3). Decays each frame. In degrees.")]
        public float recoilLift;

        [Header("Magic — dual cost")]
        [Tooltip("Deducted from owner mana pool per shot (dmagic / magic in AS3). WMagic weapons only.")]
        public float magicPoolCost;
        [Tooltip("Deducted from pers.manaHP (health-mana pool) per shot (dmana / mana in AS3). WMagic weapons only.")]
        public float manaHealthCost;

        [Header("Thrown Settings")]
        [Tooltip("Throw sub-type (throwtip in AS3). 0=arc grenade, 1=mine placement, 2=sticky throw.")]
        public int throwTip;
        [Tooltip("Fuse countdown in frames before detonation (detTime in AS3). Default 75 (~2.5s at 30fps).")]
        public int fuseFrames = 75;
        [Tooltip("Radio detonation flag (char.@radio in AS3). Second press detonates placed mines.")]
        public bool radio;

        [Header("Melee Settings")]
        public MeleeType meleeType;          // mtip in AS3
        [Tooltip("Reach of the weapon in Flash units (dlina in AS3). Club/sword swing radius or spear reach.")]
        public float meleeDlina = 100f;
        [Tooltip("Minimum reach for thrust/slash weapons (mindlina in AS3). Only used when mtip=1 or 2.")]
        public float meleeMinDlina = 100f;
        [Tooltip("Enables combo counter — every 4th hit deals 2× damage (combinat in AS3).")]
        public bool meleeCombo;
        [Tooltip("Enables charged power attack when attack held long enough (powerfull in AS3).")]
        public bool meleePowerAttack;

        [Header("Projectile — Type")]
        [Tooltip("Which prefab archetype to spawn. Set by importer from vbul/spring/flame/phisbul/navod.")]
        public ProjectileArchetype projectileArchetype;
        [Tooltip("Damage type carried by this projectile (tipdam in AS3 char node).")]
        public DamageType damageType;

        [Header("Projectile — Physics")]
        [Tooltip("Gravity multiplier on the bullet (phis.@grav). 0 = no gravity. Explosive/flame weapons use 1.")]
        public float bulletGravity;
        [Tooltip("Bullet acceleration magnitude per frame (phis.@accel). Rockets use this.")]
        public float bulletAccel;
        [Tooltip("Flame type (phis.@flame). 0=none, 1=strong flame arc, 2=weak. Affects lifetime and gravity.")]
        public int bulletFlame;
        [Tooltip("Homing strength (phis.@navod). >0 spawns a SmartBullet that tracks nearest enemy.")]
        public float bulletNavod;
        [Tooltip("Uses physics bullet (vis.@phisbul). If true: Dynamic Rigidbody2D, real gravity, bounces/sticks.")]
        public bool isPhysBullet;

        [Header("Projectile — Visual")]
        [Tooltip("Bullet visual class name from AllData.as (vis.@vbul). Empty = default ballistic round.")]
        public string vbul;
        [Tooltip("Imported projectile art definition matched from vbul or default ballistic rules.")]
        public ProjectileVisualDefinition projectileVisual;
        [Tooltip("Spring/visual stretch mode (vis.@spring). 1=velocity scale, 2=laser stretch, 3=multi-frame spread.")]
        public int springMode = 1;
        [Tooltip("Play animation on bullet vis each shot (vis.@bulanim). Used for sparks and flames.")]
        public bool bulletAnimated;
        [Tooltip("Eject shell casing particle on fire (vis.@shell).")]
        public bool hasShell;
        [Tooltip("Demask/light radius emitted when firing (vis.@shine). 500 = default.")]
        public int shineRadius = 500;

        [Header("Projectile — Impact")]
        [Tooltip("Decal type left on surfaces (vis.@tipdec + weapon.@tipdec).")]
        public DecalType decalType;
        [Tooltip("Tile/structure destruction amount per hit (char.@destroy).")]
        public float destroyTiles;
        [Tooltip("Armor penetration probability 0–1 (char.@pier / dop.@probiv).")]
        public float piercing;

        [Header("Magic Weapon")]
        [Tooltip("Mana cost per shot (ammo.@mana). Only used by WMagic (tip==5) weapons.")]
        public float manaCost;
        [Tooltip("Requires Alicorn Amulet equipped to use (weapon.@alicorn).")]
        public bool alicornOnly;

        [Header("Visuals (held sprite)")]
        [Tooltip("Imported weapon visual definition. Wired by WeaponGraphicsImportWindow.")]
        public WeaponVisualDefinition weaponVisual;

        [Tooltip("Override sprite symbol name (vis.@vweap). Empty = use 'vis' + weaponId. " +
                 "Set by importer when AllData.as has an explicit vweap attribute.")]
        public string visualOverrideId;

        [Tooltip("Muzzle flare effect id (vis.@flare). e.g. 'spark', 'plasma', 'laser'. Empty = none.")]
        public string muzzleFlareId;

        // Legacy string field kept for tools that wrote to it previously.
        [HideInInspector]
        public string weaponSprite;

        // Legacy properties for compatibility
        public string WeaponSprite => weaponSprite;

        [Header("Audio")]
        [Tooltip("Sound ID played when the weapon fires (e.g. 'rifle_s')")]
        public string soundShoot;
        [Tooltip("Sound ID played when reloading starts (e.g. 'rifle_r')")]
        public string soundReload;
        [Tooltip("Sound ID played on bullet impact (e.g. 'hit_metal'). Leave empty to use material-based hit sounds.")]
        public string soundHit;
        [Tooltip("Sound ID played during weapon prep / ready animation (e.g. charge-up)")]
        public string soundPrep;
        [Tooltip("Loop restart point within the prep sound file in milliseconds (snd.@t1). " +
                 "While firing, playback jumps back here when it approaches t2. " +
                 "Example: minigun_s t1=1030 = start of the firing loop section.")]
        public int soundPrepT1;
        [Tooltip("Spin-down start point within the prep sound file in milliseconds (snd.@t2). " +
                 "On trigger release, playback jumps here to play the wind-down tail. " +
                 "Example: minigun_s t2=3060 = end of firing loop / start of spin-down.")]
        public int soundPrepT2;
        [Tooltip("How far the shot sound travels in the world — used for AI noise awareness (snd.@noise).")]
        public float noiseRadius = 600f;

        // Legacy properties for compatibility
        public string SoundShoot => soundShoot;
        public string SoundReload => soundReload;
    }
}
