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

        /// <summary>
        /// AS3's `punch` attribute — a class selector *separate* from <see cref="weaponType"/>.
        ///
        /// This is a trap worth stating plainly: `tip` is NOT the class selector on its own.
        /// Weapon.as:345-394 dispatches tip==1 → WClub, tip==12 → WPaint, tip==4 → WThrow,
        /// tip==5 → WMagic, **then punch &gt; 0 → WPunch**, and everything else falls through to
        /// the base `Weapon` class — which is a *ranged* weapon, not "unarmed".
        ///
        /// Only 3 weapons in AllData.as carry punch='1' (scorppunch / scorp2punch / scorp3punch,
        /// all created by UnitMonstrik.as). Treating tip==0 as unarmed mis-routes the other 57
        /// tip==0 weapons (every turret, drone laser, zombie spitter, alimray, robominigun, …)
        /// to the punch controller, which spawns no projectile and draws no sprite.
        ///
        /// See docs/OnWeaponsSystemImplementation/13_WeaponTypeBehaviourAudit_2026-09-27.md §1.
        /// </summary>
        [Tooltip("AS3 `punch` attribute — 1 selects WPunch. Separate from tip; tip==0 alone means RANGED.")]
        public int punch;

        /// <summary>
        /// True only for the punch/kick family (AS3 WPunch / WKick).
        ///
        /// Single source of truth for "unarmed": both the controller factory and the sprite
        /// presenter read this, so the two cannot drift apart the way
        /// `weaponType == WeaponType.Internal` did (which hid the held sprite for 57 ranged weapons).
        /// </summary>
        public bool IsUnarmed => punch > 0;

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

        /// <summary>
        /// AS3's <c>char@auto</c> override. <b>0 = attribute absent</b>, 1 = <c>auto='0'</c>
        /// (force single-shot), 2 = <c>auto='1'</c> (force continuous).
        ///
        /// <para>AS3 tests <i>presence</i> first and only then the value (<c>Weapon.as:856-859</c>),
        /// so "absent" and "'0'" are different answers and a plain <c>bool</c> cannot carry both.
        /// Imported from the <b>tier-1</b> <c>&lt;char&gt;</c> node, alongside <c>rapid</c> — both live
        /// on <c>&lt;char&gt;</c>, not on the <c>&lt;weapon&gt;</c> root.</para>
        ///
        /// <para><b>Why "absent" is the zero value rather than -1.</b> Every one of the 204 existing
        /// assets predates this field, so its value on load comes from the fallback for a field the
        /// YAML does not mention — not from the importer. Encoding "absent" as 0 makes that fallback
        /// correct whether the engine applies the field initializer or zero-fills. The cost of getting
        /// it wrong is severe and silent in one direction only: zero-fill with -1-as-absent would make
        /// every weapon read as <i>explicitly single-shot</i>, so the SMG, minigun and every other
        /// fast weapon would stop firing while the attack key is held.</para>
        /// </summary>
        [Tooltip("AS3 char@auto: 0 = absent (use rapid<=6), 1 = forced single-shot, 2 = forced auto")]
        public int autoMode;

        /// <summary>
        /// True when the weapon fires continuously while the attack key is held.
        ///
        /// Mirrors <c>Weapon.as:856-859</c> exactly:
        /// <code>
        /// this.auto = this.rapid &lt;= 6;
        /// if(param1.@auto.length()) this.auto = param1.@auto != "0";
        /// </code>
        ///
        /// <para><b>Single source of truth</b>, like <see cref="IsUnarmed"/>: all three weapon
        /// controllers read this instead of repeating <c>rapid &lt;= 6</c> inline. Three copies of
        /// the heuristic is how the <c>@auto</c> half went missing from all of them at once —
        /// 14 weapons (shotgun, bfg, mont, knife, lasp, …) fired single-shot in Unity where AS3
        /// fires them continuously.</para>
        ///
        /// <para>Not modelled here: AS3 also forces <c>auto = true</c> for every non-player unit
        /// (<c>Weapon.as:339-342</c>). Nothing equips an enemy yet, so that rule has no call site —
        /// add it when an enemy weapon loadout lands.</para>
        /// </summary>
        public bool IsAuto => autoMode == 1 ? false : autoMode == 2 ? true : rapid <= 6f;

        // Legacy property for compatibility
        public int FireRateFrames => (int)rapid;

        [Tooltip("Accuracy stat (higher = better, scales with distance)")]
        public float precision = 0f;

        // Legacy property for compatibility
        public float Accuracy => precision;

        /// <summary>
        /// Minimum-range distance in pixels — AS3 <c>@antiprec * 40</c> (<c>Weapon.as:826</c>), the
        /// partner of <see cref="precision"/> and scaled identically so the two are comparable against
        /// a round's travel distance. Inside it, accuracy ramps from 0.25 to 1.0, i.e. the weapon is
        /// <b>worse</b> point-blank. <c>0</c> = no minimum range.
        ///
        /// <para>Four weapons in <c>AllData.as</c> carry <c>antiprec='8'</c> (320 px, 8 tiles) —
        /// long-range pieces with a real dead zone. Ported as data rather than dropped because
        /// "accuracy clamped to a minimum" would invert the mechanic. Consumed by
        /// <see cref="PFE.Systems.Combat.HitAvoidance.Accuracy"/>.</para>
        /// </summary>
        public float antiPrecision = 0f;

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
        [Tooltip("Bullet thrust along the aim direction, in AS3 pixels/frame² (phis.@accel). " +
                 "Rockets use this. Stored RAW — Projectile applies the px/frame² -> units/s² " +
                 "conversion (x9), not the x0.3 velocity one.")]
        public float bulletAccel;
        [Tooltip("Flame type (phis.@flame). 0=none, 1=strong upward arc, 2=weak. Sets the upward " +
                 "lift acceleration (Weapon.as:1545/1551) and the bullet's lifetime. Lift ADDS to " +
                 "gravity, as in AS3; it does not replace it.")]
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
        [Tooltip("Flat armour-piercing POINTS (char.@pier), subtracted from the target's armour " +
                 "reduction. Not a probability — see penetration.")]
        public float piercing;

        [Tooltip("Penetration budget (dop.@probiv, plus the ammo's probiv when ammo is wired). " +
                 "A round with this > 0 does NOT stop on the unit it hits: it spends damage and " +
                 "carries on. 0 = an ordinary round, which stops.")]
        public float penetration;

        [Header("On-hit effect (dop node)")]
        [Tooltip("AS3 <dop effect> — the on-hit status this weapon applies (Weapon.as:691-694). " +
                 "Empty means no effect. Mapped to an effect id by the damage path, not directly.")]
        public string dopEffect;

        [Tooltip("AS3 <dop damage> — the payload amount for the on-hit effect (Weapon.as:695-698). " +
                 "Field default 0, so an absent attribute applies an effect with value 0.")]
        public float dopDamage;

        [Tooltip("AS3 <dop ch> — application chance (Weapon.as:699-702). DEFAULT 1 = always; an " +
                 "absent attribute is not 0. Values >= 1 are treated as CERTAIN, not rolled " +
                 "(Unit.as:3771: `dopCh >= 1 || Math.random() < dopCh`).")]
        public float dopChance = 1f;

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
