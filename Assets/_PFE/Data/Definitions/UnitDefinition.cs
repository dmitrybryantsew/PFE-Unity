using UnityEngine;
using PFE.ModAPI;
#if ODIN_INSPECTOR
using Sirenix.OdinInspector;
#endif

// The enums and structs are in the same namespace
// Just need to ensure the file is included in the build

namespace PFE.Data.Definitions
{
    /// <summary>
    /// Complete ScriptableObject definition for unit data.
    /// Replaces XML-based unit definitions from AllData.as in ActionScript.
    ///
    /// XML Structure Reference:
    /// <unit id='raider1' fraction='2' cat='3' parent='raider' xp='100'>
    ///     <phis sX='55' sY='70' massa='55'/>
    ///     <move speed='4' jump='18' accel='3'/>
    ///     <comb hp='50' damage='11' krep='1'/>
    ///     <vis blit='sprRaider1' sprX='120' sex='w'/>
    ///     <n>Display Name</n>
    ///     <w id='cknife' ch='0.2' dif='6'/>
    /// </unit>
    ///
    /// Create instances via Assets > Create > PFE > Unit Definition
    /// </summary>
    [CreateAssetMenu(fileName = "NewUnit", menuName = "PFE Data/Units/Unit Definition")]
    public class UnitDefinition : VersionedData, IGameContent
    {
        [Header("Identity")]
        [Tooltip("Unique ID for this unit (e.g., 'raider1', 'littlepip')")]
        public string id;

        public override string DataId => id;

        // Legacy property for compatibility
        public string ID => id;

        // IGameContent
        string IGameContent.ContentId => id;
        ContentType IGameContent.ContentType => ContentType.Unit;

        [Tooltip("Parent template ID (e.g., 'raider' for 'raider1')")]
        public string parentId;

        [Tooltip("AI controller ID")]
        public string controllerId;

        [Tooltip("Faction type: 0=Neutral, 1=Player, 2=Enemy, 3=Unknown, 4=Special")]
        public FactionType fraction = FactionType.Enemy;

        [Tooltip("Category: 1=Template, 2=Faction, 3=Spawnable")]
        public UnitCategory category = UnitCategory.Template;

        [Tooltip("XP reward for killing")]
        public int xpReward;

        [Header("Display")]
        [Tooltip("Display name (from <n> tag in XML)")]
        public string displayName;

        public override string DisplayName => displayName ?? id;

        #region Physics (phis tag)

        [Header("Physics")]
        [Tooltip("Width from 'sX' in AS3 (55px = 0.55 units)")]
        [Range(0.1f, 2f)]
        public float width = 0.55f;

        [Tooltip("Height from 'sY' in AS3 (70px = 0.70 units)")]
        [Range(0.1f, 3f)]
        public float height = 0.70f;

        [Tooltip("Sitting height")]
        [Range(0f, 2f)]
        public float sitHeight = 0.5f;

        [Tooltip("Mass from 'massa' in AS3")]
        [Range(1f, 500f)]
        public float mass = 50f;

        // Legacy properties for compatibility
        public float Width => width;
        public float Height => height;

        #endregion

        #region Movement (move tag)

        [Header("Movement")]
        [Tooltip("Walking speed")]
        [Range(0f, 20f)]
        public float moveSpeed = 4f;

        [Tooltip("Running speed multiplier")]
        [Range(1f, 5f)]
        public float runMultiplier = 2f;

        [Tooltip("Acceleration rate")]
        [Range(0.1f, 10f)]
        public float acceleration = 2f;

        [Tooltip("Braking/friction")]
        [Range(0.1f, 2f)]
        public float braking = 0.5f;

        [Tooltip("Jump force")]
        [Range(0f, 30f)]
        public float jumpForce = 15f;

        // Legacy properties for compatibility
        public float WalkSpeed => moveSpeed;
        public float RunSpeed => moveSpeed * runMultiplier;
        public float Acceleration => acceleration;
        public float JumpForce => jumpForce;

        [Tooltip("Max levitation height")]
        [Range(0f, 200f)]
        public float levitationMaxHeight = 60f;

        [Tooltip("Levitation acceleration")]
        [Range(0f, 10f)]
        public float levitationAcceleration = 1.6f;

        [Tooltip("Can swim")]
        public bool canSwim = true;

        [Tooltip("Can levitate")]
        public bool canLevitate = false;

        [Tooltip("Can be knocked down")]
        public bool canBeKnockedDown = true;

        [Tooltip("Is fixed in place (cannot move)")]
        public bool isFixed = false;

        [Tooltip("Wall damage per frame")]
        [Range(0, 200)]
        public int wallDamage = 0;

        [Tooltip("Step threshold (for detecting drops)")]
        [Range(0, 50)]
        public int stepThreshold = 0;

        #endregion

        #region Combat (comb tag)

        [Header("Combat")]
        [Tooltip("Maximum health points")]
        [Range(1, 5000)]
        public int health = 50;

        [Tooltip("Armor (physical damage reduction)")]
        [Range(0, 100)]
        public int armor = 0;

        [Tooltip("Magic armor (energy damage reduction)")]
        [Range(0, 100)]
        public int magicArmor = 0;

        [Tooltip("Armor health (armor durability)")]
        [Range(0, 1000)]
        public int armorHealth = 0;

        [Tooltip("Base damage")]
        [Range(0, 200)]
        public int damage = 10;

        [Tooltip("Observation range (detection)")]
        [Range(0, 20)]
        public int observationRange = 0;

        [Tooltip("Radiation damage on hit")]
        [Range(0, 50)]
        public int radiationDamage = 0;

        [Tooltip("Skin thickness (damage reduction)")]
        [Range(0f, 50f)]
        public float skinThickness = 0f;

        [Tooltip("Dexterity")]
        [Range(0.1f, 5f)]
        public float dexterity = 1f;

        [Tooltip("Skill multiplier")]
        [Range(0.1f, 3f)]
        public float skill = 1f;

        [Tooltip("Water ability")]
        [Range(0f, 2f)]
        public float waterAbility = 1f;

        [Tooltip("Hearing range")]
        [Range(0f, 20f)]
        public float hearingRange = 5f;

        [Tooltip("Damage type")]
        public DamageType damageType = DamageType.PhysicalMelee;

        [Tooltip("Is stable (cannot be knocked down)")]
        public bool isStable = false;

        #endregion

        #region Parameters (param tag)

        [Header("Parameters")]
        [Tooltip("Blood type: 0=None, 1=Red, 2=Green, 3=Pink")]
        public BloodType bloodType = BloodType.Red;

        [Tooltip("Leaves corpse on death")]
        public bool leavesCorpse = true;

        [Tooltip("Is invulnerable")]
        public bool isInvulnerable = false;

        [Tooltip("Can activate traps")]
        public bool canActivateTraps = true;

        [Tooltip("Uses special stats")]
        public bool usesSpecialStats = false;

        [Tooltip("Is NPC (not hostile)")]
        public bool isNpc = false;

        [Tooltip("Can overlook (see through obstacles)")]
        public bool canOverlook = false;

        [Tooltip("Is pony")]
        public bool isPony = false;

        [Tooltip("Is zombie")]
        public bool isZombie = false;

        [Tooltip("Is insect")]
        public bool isInsect = false;

        [Tooltip("Is mechanical (robot)")]
        public bool isMechanical = false;

        [Tooltip("Is alicorn")]
        public bool isAlicorn = false;

        [Tooltip("Hero type (for damage bonuses)")]
        public string heroType;

        [Tooltip("Has hero bonus")]
        public bool hasHeroBonus = false;

        #endregion

        #region Vulnerabilities

        [Header("Vulnerabilities")]
        public VulnerabilityData vulnerabilities = new VulnerabilityData(1f);

        #endregion

        #region Vision/AI

        [Header("Vision/AI")]
        [Tooltip("Noise level generated")]
        [Range(0, 2000)]
        public int noiseLevel = 300;

        [Tooltip("Visual damage level")]
        [Range(0, 10)]
        public int visualDamageLevel = 1;

        [Tooltip("Splash damage")]
        [Range(0, 50)]
        public int splashDamage = 0;

        [Tooltip("Trip damage")]
        [Range(0, 50)]
        public int tripDamage = 0;

        [Tooltip("Dialogue set ID")]
        public string dialogueSet;

        [Tooltip("Teleport color")]
        public Color teleportColor = Color.white;

        [Tooltip("Sprite")]
        public Sprite sprite;

        [Tooltip("Sprite sheet array")]
        public Sprite[] spriteSheet;

        [Tooltip("Sprite dimensions (width, height)")]
        public Vector2Int spriteDimensions = new Vector2Int(120, 120);

        [Tooltip("Draw dimensions")]
        public Vector2Int drawDimensions = new Vector2Int(60, 60);

        [Tooltip("Gender")]
        public Gender gender = Gender.Other;

        #endregion

        #region Sounds

        [Header("Sounds")]
        [Tooltip("Music track ID played when this unit is in combat")]
        public string musicTrack;

        [Tooltip("Sound ID played on death (supports group IDs like 'rm', 'rw')")]
        public string deathSoundId;

        [Tooltip("Sound ID played when the unit falls / lands")]
        public string fallingSoundId;

        [Tooltip("Sound ID looped while the unit is running (e.g. 'drone' for robots)")]
        public string soundRun;

        #endregion

        #region Special

        [Header("Special")]
        [Tooltip("Detection distance")]
        [Range(0, 2000)]
        public int detectionDistance = 400;

        [Tooltip("Grenade count")]
        [Range(0, 20)]
        public int grenadeCount = 0;

        [Tooltip("Stalk distance")]
        [Range(0, 2000)]
        public int stalkDistance = 0;

        [Tooltip("Action points (SATS)")]
        [Range(0, 100)]
        public int actionPoints = 0;

        [Tooltip("Special state")]
        [Range(0, 10)]
        public int specialState = 0;

        [Tooltip("Attract range")]
        [Range(0, 1000)]
        public int attractRange = 0;

        [Tooltip("Is walker (patrols)")]
        public bool isWalker = false;

        [Tooltip("Is sniper")]
        public bool isSniper = false;

        [Tooltip("Has grenades")]
        public bool hasGrenades = false;

        [Tooltip("Uses enclave weapons")]
        public bool usesEnclaveWeapons = false;

        [Tooltip("Will stay in place")]
        public bool willStayInPlace = false;

        [Tooltip("Can resurrect")]
        public bool canResurrect = false;

        [Tooltip("Glows")]
        public bool glows = false;

        [Tooltip("Has light bulb")]
        public bool hasLightBulb = false;

        [Tooltip("Can carry items")]
        public bool canCarryItems = false;

        [Tooltip("Attach distance")]
        [Range(0f, 5f)]
        public float attachDistance = 1f;

        [Tooltip("Drop item ID")]
        public string dropItem;

        #endregion

        #region Weapons

        [Header("Weapons")]
        [Tooltip("Weapons this unit can carry")]
        public WeaponChance[] weapons;

        #endregion

        #region Animations

        [Header("Animations")]
        public AnimationSet animations;

        #endregion

        protected override bool OnValidateData()
        {
            // Validate parent unit exists
            if (!string.IsNullOrEmpty(parentId))
            {
                // Would check database in real implementation
            }

            // Validate weapon references
            if (weapons != null)
            {
                foreach (var w in weapons)
                {
                    if (string.IsNullOrEmpty(w.weaponId))
                    {
                        Debug.LogWarning($"{id}: Weapon has null ID");
                        return false;
                    }
                }
            }

            return true;
        }

        public override string[] GetReferencedDataIds()
        {
            var ids = new System.Collections.Generic.List<string>();

            if (!string.IsNullOrEmpty(parentId))
                ids.Add(parentId);

            if (!string.IsNullOrEmpty(controllerId))
                ids.Add(controllerId);

            if (weapons != null)
            {
                foreach (var w in weapons)
                {
                    if (!string.IsNullOrEmpty(w.weaponId))
                        ids.Add(w.weaponId);
                }
            }

            return ids.ToArray();
        }
    }
}
