using UnityEngine;

namespace PFE.Data.Definitions
{
    /// <summary>
    /// Supporting data structures for PFE data system.
    /// </summary>

    /// <summary>
    /// Vulnerability data for units.
    /// 16 damage type multipliers from the original game.
    /// </summary>
    [System.Serializable]
    public struct VulnerabilityData
    {
        [Header("Physical Damage")]
        public float bullet;      // PhysicalBullet
        public float blade;       // Blade
        public float phis;        // PhysicalMelee

        [Header("Elemental Damage")]
        public float fire;        // Fire
        public float expl;        // Explosive
        public float laser;       // Laser
        public float plasma;      // Plasma
        public float spark;       // Spark
        public float acid;        // Acid
        public float cryo;        // Cryo

        [Header("Biological Damage")]
        public float venom;       // Venom
        public float poison;      // Poison
        public float bleed;       // Bleed
        public float fang;        // Fang

        [Header("Special Damage")]
        public float emp;         // EMP
        public float pink;        // Pink
        public float necro;       // Necrotic

        public VulnerabilityData(float defaultValue = 1f)
        {
            bullet = blade = phis = fire = expl = laser = plasma = spark = defaultValue;
            acid = cryo = venom = poison = bleed = fang = emp = pink = necro = defaultValue;
        }

        /// <summary>
        /// Get vulnerability multiplier for damage type.
        /// </summary>
        public float GetVulnerability(DamageType damageType)
        {
            switch (damageType)
            {
                case DamageType.PhysicalBullet: return bullet;
                case DamageType.Blade: return blade;
                case DamageType.PhysicalMelee: return phis;
                case DamageType.Fire: return fire;
                case DamageType.Explosive: return expl;
                case DamageType.Laser: return laser;
                case DamageType.Plasma: return plasma;
                case DamageType.Spark: return spark;
                case DamageType.Acid: return acid;
                case DamageType.Cryo: return cryo;
                case DamageType.Venom: return venom;
                case DamageType.Poison: return poison;
                case DamageType.Bleed: return bleed;
                case DamageType.Fang: return fang;
                case DamageType.EMP: return emp;
                case DamageType.Pink: return pink;
                case DamageType.Necrotic: return necro;
                default: return 1f;
            }
        }

        /// <summary>
        /// Set vulnerability multiplier for damage type.
        /// </summary>
        public void SetVulnerability(DamageType damageType, float value)
        {
            switch (damageType)
            {
                case DamageType.PhysicalBullet: bullet = value; break;
                case DamageType.Blade: blade = value; break;
                case DamageType.PhysicalMelee: phis = value; break;
                case DamageType.Fire: fire = value; break;
                case DamageType.Explosive: expl = value; break;
                case DamageType.Laser: laser = value; break;
                case DamageType.Plasma: plasma = value; break;
                case DamageType.Spark: spark = value; break;
                case DamageType.Acid: acid = value; break;
                case DamageType.Cryo: cryo = value; break;
                case DamageType.Venom: venom = value; break;
                case DamageType.Poison: poison = value; break;
                case DamageType.Bleed: bleed = value; break;
                case DamageType.Fang: fang = value; break;
                case DamageType.EMP: emp = value; break;
                case DamageType.Pink: pink = value; break;
                case DamageType.Necrotic: necro = value; break;
            }
        }
    }

    /// <summary>
    /// Weapon chance entry for unit inventory.
    /// Defines which weapons a unit can carry and with what probability.
    /// </summary>
    [System.Serializable]
    public struct WeaponChance
    {
        public string weaponId;     // Weapon ID from WeaponData
        [Range(0f, 1f)]
        public float chance;        // Probability (0-1)
        public int difficulty;      // Required difficulty level

        public WeaponChance(string id, float ch, int dif = 0)
        {
            weaponId = id;
            chance = ch;
            difficulty = dif;
        }
    }

    /// <summary>
    /// Animation frame definition for sprite animations.
    /// </summary>
    [System.Serializable]
    public struct AnimationFrame
    {
        public int y;               // Y position in sprite sheet
        public int length;          // Number of frames
        public bool rep;            // Repeat (looping)
        public float ff;            // Frame skip (speed)
        public float rf;            // Reverse frame
        public float df;            // Double frame (speed)
        public bool stab;           // Stable animation

        public AnimationFrame(int yPos, int len, bool repeat = false)
        {
            y = yPos;
            length = len;
            rep = repeat;
            ff = rf = df = 0f;
            stab = false;
        }
    }

    /// <summary>
    /// Complete animation set for a unit.
    /// </summary>
    [System.Serializable]
    public class AnimationSet
    {
        public AnimationFrame stay;
        public AnimationFrame walk;
        public AnimationFrame trot;
        public AnimationFrame run;
        public AnimationFrame jump;
        public AnimationFrame die;
        public AnimationFrame death;
        public AnimationFrame fall;
        public AnimationFrame drag;
        public AnimationFrame swim;
        public AnimationFrame climb;
        public AnimationFrame sit;
        public AnimationFrame crawl;
        public AnimationFrame fly;
        public AnimationFrame dig;
        public AnimationFrame transform;
        public AnimationFrame preAttack;
    }

    /// <summary>
    /// Weapon tier definition for upgradeable weapons.
    /// 21 fields defining weapon stats at each tier.
    /// </summary>
    [System.Serializable]
    public struct WeaponTier
    {
        public int tier;                    // Tier number (1-4)

        [Header("Combat Stats")]
        public float rapid;                  // Fire rate
        public float damage;                 // Base damage
        public float tipDamage;              // Special damage
        public float pierce;                 // Armor piercing
        public float knockback;              // Knockback
        public float destroy;                // Destroy chance
        public float precision;              // Accuracy
        public float crit;                   // Crit chance
        public float critDamage;             // Crit damage

        [Header("Probability")]
        public float probiv;                 // Armor piercing chance

        [Header("Special")]
        public float fireDamage;             // Additional fire damage

        public WeaponTier(int t)
        {
            tier = t;
            rapid = damage = tipDamage = pierce = knockback = destroy = 0f;
            precision = crit = critDamage = probiv = fireDamage = 0f;
        }
    }

    /// <summary>
    /// Weapon effect (additional effects beyond damage).
    /// </summary>
    [System.Serializable]
    public struct WeaponEffect
    {
        public string effectId;              // Effect ID from EffectData
        [Range(0f, 1f)]
        public float chance;                 // Trigger chance
        public float damage;                 // Effect damage
        public float duration;               // Effect duration

        public WeaponEffect(string id, float ch, float dmg = 0f)
        {
            effectId = id;
            chance = ch;
            damage = dmg;
            duration = 0f;
        }
    }

    /// <summary>
    /// Stat modifier for skills and perks.
    /// </summary>
    [System.Serializable]
    public struct StatModifier
    {
        public string statId;                // Stat ID (e.g., "maxhp", "allDamMult")
        public ModifierType type;            // Add, Multiply, Set
        public ModifierTarget target;        // Player, Pers, Unit

        [Header("Values per rank/level")]
        public float v0;                     // Base value
        public float v1;                     // Level 1 / Rank 1
        public float v2;                     // Level 2 / Rank 2
        public float v3;                     // Level 3 / Rank 3
        public float v4;                     // Level 4 / Rank 4
        public float v5;                     // Level 5 / Rank 5

        public float vd;                     // Delta (linear increase per level)

        /// <summary>
        /// Get value for specific level/rank.
        /// </summary>
        public float GetValueForLevel(int level)
        {
            if (level == 0) return v0;
            if (level == 1) return v1;
            if (level == 2) return v2;
            if (level == 3) return v3;
            if (level == 4) return v4;
            if (level == 5) return v5;

            // For higher levels, use delta if available
            if (vd != 0)
                return v0 + vd * level;

            return v0;
        }
    }

    /// <summary>
    /// Text variable for dynamic UI text.
    /// </summary>
    [System.Serializable]
    public struct TextVariable
    {
        public string key;                   // Variable name (s1, s2, etc.)
        public string value;                 // Text value
    }

    /// <summary>
    /// Perk requirement definition.
    /// </summary>
    [System.Serializable]
    public struct PerkRequirement
    {
        public RequirementType type;
        public string skillId;               // For skill requirements
        public int level;                    // Base level required
        public int levelDelta;               // Additional levels per perk rank (dlvl)

        /// <summary>
        /// Check if requirement is met for given stats and rank.
        /// </summary>
        public bool IsMet(object stats, int currentRank)
        {
            // This would interface with CharacterStats
            // For now, return stub
            return true;
        }
    }

    /// <summary>
    /// Skill modifier for items and perks.
    /// 8 fields from AS3.
    /// </summary>
    [System.Serializable]
    public struct SkillModifier
    {
        public string skillId;               // Skill ID
        public float value;                  // Bonus value
        public bool isMultiplier;            // True = multiply, False = add
        public bool isWeaponSkill;           // Affects weapon skill

        public SkillModifier(string id, float val, bool mult = false)
        {
            skillId = id;
            value = val;
            isMultiplier = mult;
            isWeaponSkill = false;
        }
    }

    /// <summary>
    /// Effect reference for items and perks.
    /// </summary>
    [System.Serializable]
    public struct EffectReference
    {
        public string effectId;              // Effect ID from EffectData
        [Range(0f, 1f)]
        public float chance;                 // Trigger chance
        public float duration;               // Override duration (0 = use default)
    }

    /// <summary>
    /// Component requirement for crafting.
    /// </summary>
    [System.Serializable]
    public struct ComponentRequirement
    {
        public string itemId;                // Item ID from ItemData
        public int count;                    // Quantity needed
    }

    /// <summary>
    /// Loot entry for loot tables.
    /// </summary>
    [System.Serializable]
    public struct LootEntry
    {
        public string itemId;                // Item ID
        [Range(0f, 1f)]
        public float chance;                 // Drop chance
        public int minCount;                 // Minimum drop
        public int maxCount;                 // Maximum drop
        public int requiredDifficulty;       // Required difficulty
    }

    /// <summary>
    /// Encounter probability for locations.
    /// </summary>
    [System.Serializable]
    public struct EncounterProbability
    {
        public string unitId;                // Unit ID
        [Range(0f, 1f)]
        public float chance;                 // Spawn chance
        public int minCount;                 // Minimum spawn
        public int maxCount;                 // Maximum spawn
    }

    /// <summary>
    /// Medical item data.
    /// 12 fields from AS3.
    /// </summary>
    [System.Serializable]
    public struct MedicalData
    {
        [Header("Healing")]
        public float hpRestore;              // HP restored
        public float organRestore;           // Organ HP restored
        public float overTime;               // HP per second
        public float duration;               // Effect duration

        [Header("Special")]
        public float removeRads;             // Radiation removed
        public bool cureAddiction;           // Cures chem addiction
        public bool restoreLimbs;            // Restores crippled limbs

        [Header("Side Effects")]
        public string addictionId;           // Addiction effect ID
        [Range(0f, 1f)]
        public float addictionChance;        // Chance of addiction

        [Header("Requirements")]
        public int medicineSkillRequired;     // Required medic skill
    }

    /// <summary>
    /// Ammo variant data.
    /// 10 fields from AS3.
    /// </summary>
    [System.Serializable]
    public struct AmmoVariantData
    {
        [Header("Modifiers")]
        public int armorPiercingBonus;       // pier in AS3
        public float damageMultiplier;       // damage mult
        public float armorMultiplier;        // armor mult
        public float knockbackMultiplier;    // knock mult
        public float precisionMultiplier;    // precision mult

        [Header("Special")]
        public bool extraDurabilityCost;     // Uses more weapon durability
        public int fireDamage;               // Additional fire damage
        public DamageType damageTypeOverride; // Override damage type
    }

    /// <summary>
    /// Crafting data.
    /// </summary>
    [System.Serializable]
    public struct CraftingData
    {
        public string resultItemId;          // Item created
        public int resultCount;              // Quantity
        public string requiredWorkbench;     // Workbench type
        public string requiredSkill;         // Skill ID
        public int requiredSkillLevel;       // Skill level needed
    }

    /// <summary>
    /// Book data (skill books).
    /// </summary>
    [System.Serializable]
    public struct BookData
    {
        public string skillId;               // Skill boosted
        public int bonusLevels;              // Levels granted (usually 1)
        public bool oneTime;                 // Can only read once
    }

    /// <summary>
    /// Equipment data (wearable items).
    /// </summary>
    [System.Serializable]
    public struct EquipmentData
    {
        public int armor;                    // Defense bonus
        public int magicArmor;               // Magic defense
        public int armorHP;                  // Armor durability
        public float dexPenalty;             // Dexterity penalty
    }

    /// <summary>
    /// Component data (crafting materials).
    /// </summary>
    [System.Serializable]
    public struct ComponentData
    {
        public ComponentCategory category;   // Component type
        public bool isJunk;                  // Can be scrapped
    }

    /// <summary>
    /// Potion data (alchemy).
    /// </summary>
    [System.Serializable]
    public struct PotionData
    {
        public bool isCraftable;             // Can be crafted
        public string requiredSkill;         // Skill needed (survival)
        public int requiredSkillLevel;       // Skill level
    }
}
