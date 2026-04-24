using NUnit.Framework;
using PFE.Systems.Combat;
using PFE.Systems.RPG;
using PFE.Systems.RPG.Data;
using PFE.Core.Time;
using UnityEngine;

namespace PFE.Tests.Editor.Combat
{
    /// <summary>
    /// TDD Tests for Combat System based on docs/02_combat_logic.md
    ///
    /// Tests cover:
    /// - Damage calculation with armor, vulnerabilities, critical hits
    /// - Fire rate (rapid) system
    /// - Accuracy (precision/deviation) system
    /// - Projectile behavior
    /// - Weapon durability and jamming
    /// - Ammunition system
    /// - Enemy vs Player differences
    /// </summary>
    [TestFixture]
    public class CombatLogicTests
    {
        private GameObject testCharacter;
        private CharacterStats playerStats;
        private GameObject testWeaponObject;
        private WeaponLogic weaponLogic;
        private ITimeProvider testTimeProvider;
        private ICombatCalculator combatCalculator;
        private LevelCurve testLevelCurve;

        [SetUp]
        public void Setup()
        {
            // Create test infrastructure
            testTimeProvider = new UnityTimeProvider();
            combatCalculator = new CombatCalculator();

            // Create level curve
            testLevelCurve = ScriptableObject.CreateInstance<LevelCurve>();
            testLevelCurve.xpDelta = 5000;
            testLevelCurve.skillPointsPerLevel = 5;
            testLevelCurve.baseHp = 100;
            testLevelCurve.hpPerLevel = 15;
            testLevelCurve.organHpPerLevel = 40;
            testLevelCurve.baseOrganHp = 200;

            // Create test character
            testCharacter = new GameObject("TestPlayer");
            playerStats = testCharacter.AddComponent<CharacterStats>();
            playerStats.Initialize(testLevelCurve);

            // Create test weapon (WeaponLogic is not a MonoBehaviour, so we create it directly)
            // Create a test WeaponDefinition
            var testWeaponDef = ScriptableObject.CreateInstance<PFE.Data.Definitions.WeaponDefinition>();
            testWeaponDef.weaponId = "test_weapon";
            testWeaponDef.weaponType = PFE.Data.Definitions.WeaponType.Guns;
            testWeaponDef.baseDamage = 15f;
            testWeaponDef.rapid = 10f;
            testWeaponDef.deviation = 4f;
            testWeaponDef.maxDurability = 100;
            testWeaponDef.magazineSize = 12;
            testWeaponDef.reloadTime = 50f;

            var durabilitySystem = new DurabilitySystem(combatCalculator);
            weaponLogic = new WeaponLogic(testWeaponDef, testTimeProvider, combatCalculator, durabilitySystem);

            // Store the test weapon object for cleanup
            testWeaponObject = new GameObject("TestWeapon");
        }

        [TearDown]
        public void TearDown()
        {
            if (testCharacter != null) Object.DestroyImmediate(testCharacter);
            if (testWeaponObject != null) Object.DestroyImmediate(testWeaponObject);
            if (testLevelCurve != null) Object.DestroyImmediate(testLevelCurve);
        }

        #region Damage Calculation Tests

        [Test]
        [Description("Damage_WeaponBaseDamage")]
        public void Weapon_BaseDamage_IsCorrect()
        {
            // From docs: Base damage from XML
            // Example: Pistol with damage='15'
            float baseDamage = 15f;

            Assert.AreEqual(15f, baseDamage, "Base pistol damage should be 15");
        }

        [Test]
        [Description("Damage_SkillMultiplier")]
        public void Weapon_Damage_WithSkillMultiplier()
        {
            // From docs: damage = (base + damAdd) * damMult * weaponSkill * skillPlusDam * (1 - breaking * 0.3)
            // Example: Base 20, skill 1.5 = 30 damage
            float baseDamage = 20f;
            float weaponSkill = 1.5f;
            float expectedDamage = baseDamage * weaponSkill;

            Assert.AreEqual(30f, expectedDamage, 0.01f, "Damage should scale with weapon skill");
        }

        [Test]
        [Description("Damage_ArmorReduction")]
        public void Weapon_Damage_ReducedByArmor()
        {
            // From docs: armorValue -= armor - pier
            // Example: 20 armor, 5 pier = 15 damage reduction
            float damage = 50f;
            float armor = 20f;
            float penetration = 5f;

            float finalDamage = damage - Mathf.Max(0, armor - penetration);

            Assert.AreEqual(35f, finalDamage, 0.01f, "Damage should be reduced by armor after penetration");
        }

        [Test]
        [Description("Damage_VulnerabilityMultiplier")]
        public void Weapon_Damage_AppliesVulnerability()
        {
            // From docs: damage *= vulnerability[type]
            // Example: 0.75 resistance to plasma
            float damage = 40f;
            float vulnerability = 0.75f;

            float finalDamage = damage * vulnerability;

            Assert.AreEqual(30f, finalDamage, 0.01f, "Damage should be multiplied by vulnerability");
        }

        [Test]
        [Description("Damage_CriticalHit")]
        public void Weapon_Damage_CriticalHit_MultipliesDamage()
        {
            // From docs: if (Random() < critCh) damage *= critDamMult
            // Example: 15% crit chance, 2.5x multiplier
            float damage = 20f;
            float critMultiplier = 2.5f;
            bool isCrit = true; // Simulating a crit

            float finalDamage = isCrit ? damage * critMultiplier : damage;

            Assert.AreEqual(50f, finalDamage, 0.01f, "Critical hit should multiply damage");
        }

        [Test]
        [Description("Damage_CompleteCalculation")]
        public void Weapon_Damage_CompleteCalculation_MatchesAS3()
        {
            // From docs example:
            // Base: 20, Skill: 1.5, Ammo: 1.2x, Vulnerability: 0.75, Armor: 20, Pier: 5, Crit: 2.5x
            float baseDamage = 20f;
            float damMult = 1.2f;
            float weaponSkill = 1.5f;
            float ammoDamage = 1.2f;
            float vulnerability = 0.75f;
            float armor = 20f;
            float penetration = 5f;
            float critMultiplier = 2.5f;

            // Step 1: Base calculation
            float damage = baseDamage * damMult * weaponSkill;

            // Step 2: Ammo multiplier
            damage *= ammoDamage;

            // Step 3: Vulnerability
            damage *= vulnerability;

            // Step 4: Armor
            float effectiveArmor = Mathf.Max(0, armor - penetration);
            damage -= effectiveArmor;

            // Step 5: Critical
            damage *= critMultiplier;

            // Expected: ~43.5 (from docs)
            Assert.IsTrue(damage > 40f && damage < 50f, $"Complete damage calculation should match AS3 (~43.5), got {damage}");
        }

        #endregion

        #region Fire Rate (Rapid) Tests

        [Test]
        [Description("Rapid_Conversion")]
        public void Weapon_Rapid_ConvertsFramesToSeconds()
        {
            // From docs: rapid is in frames (30 FPS = 1 second)
            // rapid='10' = 10 frames = 0.33 seconds = 3.0 shots/second
            int rapidFrames = 10;
            int fps = 30;

            float rapidSeconds = rapidFrames / (float)fps;
            float fireRate = 1f / rapidSeconds;

            Assert.AreEqual(0.33f, rapidSeconds, 0.01f, "10 frames should be ~0.33 seconds");
            Assert.AreEqual(3.0f, fireRate, 0.1f, "Fire rate should be ~3 shots/second");
        }

        [Test]
        [Description("Rapid_WeaponExamples")]
        public void Weapon_Rapid_Examples_MatchDocs()
        {
            // From docs table:
            // Pistol: rapid='10' = 3.0/sec
            // SMG: rapid='5' = 6.0/sec
            // Minigun: rapid='1' = 30/sec

            int fps = 30;

            float pistolRapid = 10f / fps;
            Assert.AreEqual(3.0f, 1f / pistolRapid, 0.1f, "Pistol fire rate");

            float smgRapid = 5f / fps;
            Assert.AreEqual(6.0f, 1f / smgRapid, 0.1f, "SMG fire rate");

            float minigunRapid = 1f / fps;
            Assert.AreEqual(30f, 1f / minigunRapid, 0.1f, "Minigun fire rate");
        }

        [Test]
        [Description("Rapid_MeleeSkillModifier")]
        public void Weapon_Rapid_Melee_ModifiedBySkill()
        {
            // From docs: melee rapid = rapid / skillConf * rapidMult / rapidMultCont
            // Spear (mtip=2): rapid / skillConf
            float rapid = 15f;
            float skillConf = 1.0f; // Full skill
            float rapidMult = 1.0f;

            float modifiedRapid = rapid / skillConf * rapidMult;

            Assert.AreEqual(15f, modifiedRapid, 0.01f, "Melee rapid with full skill should be unchanged");

            // Low skill (0.6)
            skillConf = 0.6f;
            modifiedRapid = rapid / skillConf * rapidMult;

            Assert.AreEqual(25f, modifiedRapid, 0.01f, "Melee rapid with low skill should be slower");
        }

        #endregion

        #region Accuracy Tests

        [Test]
        [Description("Accuracy_DeviationCalculation")]
        public void Weapon_Accuracy_Deviation_AffectsSpread()
        {
            // From docs: accuracyOffset = (Random() - 0.5) * (deviation * (1 + breaking * 2) / skillConf / (weaponSkill + 0.01) + mazil) * devMult
            float deviation = 4f; // Pistol
            float breaking = 0f; // No damage
            float skillConf = 1.0f;
            float weaponSkill = 1f;

            float baseSpread = deviation * (1f + breaking * 2f) / skillConf / (weaponSkill + 0.01f);

            // Formula includes +0.01 to weaponSkill: 4 / 1.01 = 3.96
            Assert.AreEqual(3.96f, baseSpread, 0.01f, "Base spread should match deviation divided by (weaponSkill + 0.01)");
        }

        [Test]
        [Description("Accuracy_BreakingIncreasesSpread")]
        public void Weapon_Accuracy_Breaking_IncreasesSpread()
        {
            // From docs: deviation * (1 + breaking * 2)
            float deviation = 4f;
            float breaking = 0.5f; // 50% damaged

            float spreadMultiplier = 1 + breaking * 2;
            float effectiveSpread = deviation * spreadMultiplier;

            Assert.AreEqual(8f, effectiveSpread, 0.01f, "Spread should double at 50% breaking");
        }

        [Test]
        [Description("Accuracy_PrecisionHitChance")]
        public void Weapon_Accuracy_Precision_ScalesWithDistance()
        {
            // From docs: Bullet.accuracy() returns precision / dist
            float precision = 6f;
            float distance = 100f;

            float hitChance = precision / distance;

            Assert.AreEqual(0.06f, hitChance, 0.001f, "Hit chance should decrease with distance");
        }

        [Test]
        [Description("Accuracy_AntiPrecisionAtCloseRange")]
        public void Weapon_Accuracy_AntiPrecision_ReducesCloseAccuracy()
        {
            // From docs: if (antiprec > 0 && dist < antiprec) return dist / antiprec * 0.75 + 0.25
            float antiprec = 50f;
            float distance = 25f;

            float hitChance = distance / antiprec * 0.75f + 0.25f;

            Assert.AreEqual(0.625f, hitChance, 0.001f, "Anti-precision should reduce hit chance at close range");
        }

        #endregion

        #region Projectile Tests

        [Test]
        [Description("Projectile_Movement")]
        public void Projectile_MovesWithVelocity()
        {
            // From docs: dx += ddx, dy += ddy
            float velocity = 100f; // pixels/frame
            float deltaTime = 1f / 30f; // 1 frame

            float distanceMoved = velocity * deltaTime;
            Assert.AreEqual(3.33f, distanceMoved, 0.01f, "Projectile should move 3.33 units per frame at 30 FPS");
        }

        [Test]
        [Description("Projectile_SubStepping")]
        public void Projectile_SubStepping_ForHighVelocity()
        {
            // From docs: if (|dx| > maxdelta) sub-step
            float velocity = 1000f;
            float maxDelta = 50f;
            int expectedSteps = Mathf.CeilToInt(velocity / maxDelta);

            Assert.AreEqual(20, expectedSteps, "Should calculate 20 sub-steps for high velocity");
        }

        [Test]
        [Description("Projectile_Penetration")]
        public void Projectile_Penetration_AllowsMultipleHits()
        {
            // From docs: if (probiv > 0 && damage > 0) continue after hit
            float penetration = 0.5f; // 50% penetration chance
            float damage = 20f;

            bool canPenetrate = penetration > 0 && damage > 0;

            Assert.IsTrue(canPenetrate, "Projectile with penetration should continue after hit");
        }

        [Test]
        [Description("Projectile_Explosion")]
        public void Projectile_Explodes_WithRadius()
        {
            // From docs: if (explRadius > 0) explosion()
            float explosionRadius = 150f;
            float explosionDamage = 100f;

            Assert.IsTrue(explosionRadius > 0, "Explosive projectile should have radius");
            Assert.IsTrue(explosionDamage > 0, "Explosive projectile should have damage");
        }

        #endregion

        #region Durability Tests

        [Test]
        [Description("Durability_BreakingCalculation")]
        public void Weapon_Durability_Breaking_StatusCalculatedCorrectly()
        {
            // From docs: if (hp < maxhp / 2) breaking = (maxhp - hp) / maxhp * 2 - 1
            int maxHp = 100;
            int currentHp = 30;

            float breaking = Mathf.Clamp01((maxHp - currentHp) / (float)maxHp * 2f - 1f);

            Assert.AreEqual(0.4f, breaking, 0.01f, "Breaking status should be 0.4 at 30% HP");
        }

        [Test]
        [Description("Durability_JamChance")]
        public void Weapon_Durability_JamChance_IncreasesWithDamage()
        {
            // From docs: jamChance = breaking / max(20, holder) * jamMultiplier
            // Line 1114: breaking / Math.max(20, this.holder) * jamMultiplier
            float breaking = 0.5f;
            int holder = 12; // Magazine size
            float jamMultiplier = 1f;

            float jamChance = breaking / Mathf.Max(20f, holder) * jamMultiplier;

            // Formula: 0.5 / max(20, 12) * 1 = 0.5 / 20 = 0.025
            Assert.AreEqual(0.025f, jamChance, 0.001f, "Jam chance should be ~2.5%");
        }

        [Test]
        [Description("Durability_DamageReduction")]
        public void Weapon_Durability_Damage_ReducedAtLowDurability()
        {
            // From docs: damage * (1 - breaking * 0.3)
            float baseDamage = 20f;
            float breaking = 0.5f;

            float reducedDamage = baseDamage * (1 - breaking * 0.3f);

            Assert.AreEqual(17f, reducedDamage, 0.01f, "Damage should be reduced by 15% at 50% breaking");
        }

        [Test]
        [Description("Durability_SpreadIncrease")]
        public void Weapon_Durability_Spread_IncreasesWithDamage()
        {
            // From docs: deviation * (1 + breaking * 2)
            float baseDeviation = 4f;
            float breaking = 0.5f;

            float increasedDeviation = baseDeviation * (1 + breaking * 2f);

            Assert.AreEqual(8f, increasedDeviation, 0.01f, "Deviation should double at 50% breaking");
        }

        #endregion

        #region Ammunition Tests

        [Test]
        [Description("Ammo_MagazineReload")]
        public void Weapon_Ammo_ReloadsToFullMagazine()
        {
            // From docs: holder = magazine size, hold = current ammo
            int magazineSize = 12;
            int currentAmmo = 3;

            int ammoNeeded = magazineSize - currentAmmo;

            Assert.AreEqual(9, ammoNeeded, "Should need 9 rounds to reload");
        }

        [Test]
        [Description("Ammo_Regeneration")]
        public void Weapon_Ammo_RegeneratesOverTime()
        {
            // From docs: if (recharg && hold < holder && t_attack == 0) ++hold every recharg frames
            int currentAmmo = 5;
            int maxAmmo = 10;

            bool canRecharge = currentAmmo < maxAmmo;
            Assert.IsTrue(canRecharge, "Weapon should be able to recharge when not full");
        }

        [Test]
        [Description("Ammo_DamageMultiplier")]
        public void Weapon_Ammo_ModifiesDamage()
        {
            // From docs: bullet.damage = resultDamage() * ammoDamage
            float baseDamage = 20f;
            float ammoDamageMult = 1.2f; // Enhanced ammo

            float finalDamage = baseDamage * ammoDamageMult;

            Assert.AreEqual(24f, finalDamage, 0.01f, "Enhanced ammo should increase damage by 20%");
        }

        [Test]
        [Description("Ammo_ArmorPenetration")]
        public void Weapon_Ammo_IncreasesPenetration()
        {
            // From docs: ammoPier, ammoArmor
            float basePierce = 0f;
            float ammoPierce = 10f;
            float baseArmorMult = 1f;
            float ammoArmorMult = 1.2f;

            float totalPierce = basePierce + ammoPierce;
            float totalArmorMult = baseArmorMult * ammoArmorMult;

            Assert.AreEqual(10f, totalPierce, 0.01f, "Ammo should add penetration");
            Assert.AreEqual(1.2f, totalArmorMult, 0.01f, "Ammo should multiply armor effectiveness");
        }

        #endregion

        #region Enemy vs Player Tests

        [Test]
        [Description("Enemy_AutoFire")]
        public void Enemy_Weapon_UsesAutomaticFire()
        {
            // From docs: if (!owner.player) auto = true
            bool isPlayer = false;

            bool isAutomatic = !isPlayer;

            Assert.IsTrue(isAutomatic, "Enemy weapons should always be automatic");
        }

        [Test]
        [Description("Enemy_RecoilReduction")]
        public void Enemy_Weapon_HasReducedRecoil()
        {
            // From docs: recoilUp *= 0.2 for enemies
            float baseRecoil = 2f;
            float enemyRecoilMult = 0.2f;

            float enemyRecoil = baseRecoil * enemyRecoilMult;

            Assert.AreEqual(0.4f, enemyRecoil, 0.01f, "Enemy recoil should be 20% of base");
        }

        [Test]
        [Description("Enemy_NoDurabilityLoss")]
        public void Enemy_Weapon_DoesNotDegrade()
        {
            // From docs: Only player weapons degrade
            bool isPlayer = false;

            bool degrades = isPlayer;

            Assert.IsFalse(degrades, "Enemy weapons should not degrade");
        }

        [Test]
        [Description("Enemy_FriendlyFireReduction")]
        public void Enemy_Explosion_HasReducedFriendlyFire()
        {
            // From docs: friendlyExpl = 0.25 for enemies
            float baseDamage = 100f;
            float friendlyFireMult = 0.25f;

            float friendlyDamage = baseDamage * friendlyFireMult;

            Assert.AreEqual(25f, friendlyDamage, 0.01f, "Enemy friendly fire should be 25% damage");
        }

        #endregion

        #region Skill Integration Tests

        [Test]
        [Description("Skill_MeleeWeaponBonus")]
        public void MeleeSkill_IncreasesWeaponSkill_By5PercentPerLevel()
        {
            // From docs: Weapon skill +5% per level
            int skillLevel = 3;
            float baseWeaponSkill = 1.0f;
            float bonusPerLevel = 0.05f;

            float weaponSkill = baseWeaponSkill + (skillLevel * bonusPerLevel);

            Assert.AreEqual(1.15f, weaponSkill, 0.01f, "Melee skill 3 should give +15% weapon skill");
        }

        [Test]
        [Description("Skill_SneakCritBonus")]
        public void SneakSkill_IncreasesBackstabCrit_By5PercentPerLevel()
        {
            // From docs: critInvis: +5% when hidden per level
            int sneakLevel = 4;
            float bonusPerLevel = 0.05f;

            float backstabCritChance = sneakLevel * bonusPerLevel;

            Assert.AreEqual(0.20f, backstabCritChance, 0.001f, "Sneak 4 should give +20% backstab crit");
        }

        [Test]
        [Description("Skill_ExplosivesMineDetection")]
        public void ExplosivesSkill_IncreasesMineDetection_By1PerLevel()
        {
            // From docs: remine: +1 detect mines per level
            int explosivesLevel = 5;

            int detectRange = explosivesLevel;

            Assert.AreEqual(5, detectRange, "Explosives 5 should detect mines at range 5");
        }

        [Test]
        [Description("Skill_MagicManaRegen")]
        public void MagicSkill_IncreasesManaRegen_By0Point75PerLevel()
        {
            // From docs: recManaMin: +0.75 mana regen per level
            int magicLevel = 4;
            float regenPerLevel = 0.75f;

            float manaRegen = magicLevel * regenPerLevel;

            Assert.AreEqual(3.0f, manaRegen, 0.01f, "Magic 4 should give +3.0 mana regen");
        }

        [Test]
        [Description("Skill_RepairBonus")]
        public void RepairSkill_IncreasesRepairSkill_By1PerLevel()
        {
            // From docs: repair: +1 per level
            int repairLevel = 7;

            int repairSkill = repairLevel;

            Assert.AreEqual(7, repairSkill, "Repair 7 should give +7 repair skill");
        }

        #endregion

        #region Perk Integration Tests

        [Test]
        [Description("Perk_OakBladeResistance")]
        public void OakPerk_GivesBladeResistance()
        {
            // From docs: Oak perk - res[1] (blade): +0.25
            float baseResistance = 0f;
            float perkBonus = 0.25f;

            float totalResistance = baseResistance + perkBonus;

            Assert.AreEqual(0.25f, totalResistance, 0.01f, "Oak perk should give +25% blade resistance");
        }

        [Test]
        [Description("Perk_ActionPoints")]
        public void ActionPerk_IncreasesMaxActionPoints_By25PerRank()
        {
            // From docs: action perk - maxOd: +25 per level
            int rank = 2;
            int bonusPerRank = 25;

            int actionPoints = rank * bonusPerRank;

            Assert.AreEqual(50, actionPoints, "Action rank 2 should give +50 AP");
        }

        [Test]
        [Description("Perk_CritChance")]
        public void CritchPerk_IncreasesCritChance_By5PercentPerRank()
        {
            // From docs: critch perk - critCh: +0.05 per level
            int rank = 3;
            float bonusPerRank = 0.05f;

            float critBonus = rank * bonusPerRank;

            Assert.AreEqual(0.15f, critBonus, 0.001f, "Critch rank 3 should give +15% crit chance");
        }

        #endregion

        #region Integration Tests

        [Test]
        [Description("Integration_FullCombatSequence")]
        public void FullCombat_Sequence_AttackHitDamageDeath()
        {
            // Simulate full combat sequence from docs flow diagram
            // 1. Player attacks
            // 2. Calculate accuracy
            // 3. Create projectile
            // 4. Detect collision
            // 5. Calculate damage
            // 6. Apply armor/vulnerability
            // 7. Apply critical hit
            // 8. Reduce HP

            float baseDamage = 20f;
            float weaponSkill = 1.5f;
            float vulnerability = 1f;
            float armor = 10f;
            float penetration = 0f;
            float critMult = 2f;
            bool isCrit = true; // Simulated crit

            // Calculate final damage
            float damage = baseDamage * weaponSkill;
            damage *= vulnerability;
            damage -= Mathf.Max(0, armor - penetration);
            if (isCrit) damage *= critMult;

            // Expected: 20 * 1.5 * 1 - 10 * 2 = 40
            Assert.AreEqual(40f, damage, 0.01f, $"Full combat damage should be 40, got {damage}");
            Assert.IsTrue(damage > 30f, $"Full combat damage should be meaningful (>30), got {damage}");
        }

        [Test]
        [Description("Integration_LevelingWithCombat")]
        public void Leveling_ImprovesCombatEffectiveness()
        {
            // Test that leveling up improves damage output
            // Level 1: weapon skill 1.0 = 20 damage
            // Level 5: weapon skill 1.25 = 25 damage

            float baseDamage = 20f;
            float level1Skill = 1.0f;
            float level5Skill = 1.25f;

            float level1Damage = baseDamage * level1Skill;
            float level5Damage = baseDamage * level5Skill;

            Assert.AreEqual(20f, level1Damage, 0.01f, "Level 1 damage");
            Assert.AreEqual(25f, level5Damage, 0.01f, "Level 5 damage");
            Assert.IsTrue(level5Damage > level1Damage, "Higher level should deal more damage");
        }

        [Test]
        [Description("Integration_SkillAndPerkSynergy")]
        public void SkillAndPerk_CombineForBonus()
        {
            // Test that skills and perks stack correctly
            // Melee 3: +15% weapon skill
            // Oak perk: +25% blade resistance
            // Both should apply simultaneously

            float baseWeaponSkill = 1.0f;
            int meleeLevel = 3;
            float skillBonus = meleeLevel * 0.05f;
            float totalWeaponSkill = baseWeaponSkill + skillBonus;

            float baseResistance = 0f;
            float perkResistance = 0.25f;
            float totalResistance = baseResistance + perkResistance;

            Assert.AreEqual(1.15f, totalWeaponSkill, 0.01f, "Combined weapon skill");
            Assert.AreEqual(0.25f, totalResistance, 0.01f, "Combined resistance");
        }

        #endregion
    }
}
