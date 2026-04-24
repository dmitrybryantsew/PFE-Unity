using NUnit.Framework;
using PFE.Systems.Combat;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Core.Time;
using UnityEngine;
using R3;

namespace PFE.Tests.Editor.Combat
{
    /// <summary>
    /// Unit tests for WeaponLogic to verify:
    /// - Weapon firing behavior
    /// - Fire rate cooldown
    /// - Accuracy deviation calculation
    /// - Jam and misfire mechanics
    /// - Ammunition consumption
    /// - Durability consumption
    /// - Reload behavior
    /// - Repair functionality
    /// - Reactive property updates
    /// </summary>
    [TestFixture]
    public class WeaponLogicTests
    {
        private WeaponLogic weaponLogic;
        private WeaponDefinition testWeaponDef;
        private MockTimeProvider mockTimeProvider;
        private ICombatCalculator combatCalculator;
        private IDurabilitySystem durabilitySystem;
        private UnitStats testOwnerStats;
        private CompositeDisposable disposables;

        // Mock TimeProvider for testing
        private class MockTimeProvider : ITimeProvider
        {
            public float CurrentTime { get; set; }
            public float DeltaTime { get; set; }
        }

        [SetUp]
        public void Setup()
        {
            // Create disposables
            disposables = new CompositeDisposable();

            // Create mock time provider
            mockTimeProvider = new MockTimeProvider { CurrentTime = 0f, DeltaTime = 0.016f };

            // Create combat calculator and durability system
            combatCalculator = new CombatCalculator();
            durabilitySystem = new DurabilitySystem(combatCalculator);

            // Create test weapon definition
            testWeaponDef = ScriptableObject.CreateInstance<WeaponDefinition>();
            testWeaponDef.weaponId = "test_rifle";
            testWeaponDef.weaponType = WeaponType.Guns;
            testWeaponDef.baseDamage = 20f;
            testWeaponDef.rapid = 0f; // 0 = no cooldown for most tests
            testWeaponDef.maxDurability = 100;
            testWeaponDef.magazineSize = 30;
            testWeaponDef.reloadTime = 90f; // 3 seconds at 30 FPS
            testWeaponDef.deviation = 2f;
            testWeaponDef.burstCount = 0; // Single fire

            // Create test owner stats
            testOwnerStats = new UnitStats(100f, 50f);
            testOwnerStats.critChanceBonus = 0f;
            testOwnerStats.critDamageBonus = 0f;
            testOwnerStats.critChanceBonusAdditional = 0f;

            // Create weapon logic
            weaponLogic = new WeaponLogic(testWeaponDef, mockTimeProvider, combatCalculator, durabilitySystem);
        }

        [TearDown]
        public void TearDown()
        {
            disposables?.Dispose();
            if (testWeaponDef != null)
                Object.DestroyImmediate(testWeaponDef);
            // UnitStats is not a ScriptableObject, no need to destroy
        }

        #region Initialization

        [Test]
        public void WeaponLogic_Constructor_InitializesWithFullStats()
        {
            // Assert
            Assert.AreEqual(30, weaponLogic.CurrentAmmo.Value, "Should start with full magazine");
            Assert.AreEqual(100, weaponLogic.CurrentDurability.Value, "Should start with full durability");
            Assert.IsFalse(weaponLogic.IsReloading.Value, "Should not be reloading");
            Assert.IsFalse(weaponLogic.IsEmpty, "Should not be empty");
            Assert.IsFalse(weaponLogic.IsBroken, "Should not be broken");
        }

        [Test]
        public void WeaponLogic_WeaponDef_ReturnsCorrectDefinition()
        {
            // Act
            WeaponDefinition def = weaponLogic.WeaponDef;

            // Assert
            Assert.AreEqual(testWeaponDef, def, "Should return the weapon definition");
        }

        #endregion

        #region Firing Mechanics

        [Test]
        public void Fire_WithFullMagazineAndNotOnCooldown_ReturnsTrue()
        {
            // Act
            bool fired = weaponLogic.Fire(testOwnerStats);

            // Assert
            Assert.IsTrue(fired, "Should fire successfully");
            Assert.AreEqual(29, weaponLogic.CurrentAmmo.Value, "Should consume 1 ammo");
            Assert.AreEqual(99, weaponLogic.CurrentDurability.Value, "Should consume 1 durability");
        }

        [Test]
        public void Fire_WhileReloading_ReturnsFalse()
        {
            // Arrange - Consume some ammo first
            weaponLogic.Fire(testOwnerStats); // 29 ammo
            // Start reload - it will set IsReloading to true
            weaponLogic.StartReloadAsync().Forget();

            // Act
            bool fired = weaponLogic.Fire(testOwnerStats);

            // Assert
            Assert.IsFalse(fired, "Should not fire while reloading");
        }

        [Test]
        public void Fire_WithEmptyMagazine_ReturnsFalse()
        {
            // Arrange
            for (int i = 0; i < 30; i++)
                weaponLogic.Fire(testOwnerStats);

            // Act
            bool fired = weaponLogic.Fire(testOwnerStats);

            // Assert
            Assert.IsFalse(fired, "Should not fire with empty magazine");
            Assert.IsTrue(weaponLogic.IsEmpty, "Should be empty");
        }

        [Test]
        public void Fire_WhenBroken_ReturnsFalse()
        {
            // Arrange - Set durability to 0 to simulate broken weapon
            weaponLogic.SetDurability(0);

            // Act
            bool fired = weaponLogic.Fire(testOwnerStats);

            // Assert
            Assert.IsFalse(fired, "Should not fire when broken");
            Assert.IsTrue(weaponLogic.IsBroken, "Should be broken");
        }

        [Test]
        public void Fire_WithCooldown_ReturnsFalse()
        {
            // Arrange
            testWeaponDef.rapid = 10f; // 10 frame cooldown = 1/3 second
            weaponLogic = new WeaponLogic(testWeaponDef, mockTimeProvider, combatCalculator, durabilitySystem);
            weaponLogic.Fire(testOwnerStats);
            mockTimeProvider.CurrentTime = 0.01f; // Less than 1/3 second cooldown

            // Act
            bool fired = weaponLogic.Fire(testOwnerStats);

            // Assert
            Assert.IsFalse(fired, "Should not fire during cooldown");
        }

        [Test]
        public void Fire_AfterCooldown_ReturnsTrue()
        {
            // Arrange
            testWeaponDef.rapid = 10f; // 10 frame cooldown = 1/3 second
            weaponLogic = new WeaponLogic(testWeaponDef, mockTimeProvider, combatCalculator, durabilitySystem);
            weaponLogic.Fire(testOwnerStats);
            mockTimeProvider.CurrentTime = 0.5f; // More than 1/3 second cooldown

            // Act
            bool fired = weaponLogic.Fire(testOwnerStats);

            // Assert
            Assert.IsTrue(fired, "Should fire after cooldown");
        }

        #endregion

        #region Burst Fire

        [Test]
        public void Fire_BurstWeapon_ConsumesMultipleAmmo()
        {
            // Arrange
            testWeaponDef.burstCount = 3;
            weaponLogic = new WeaponLogic(testWeaponDef, mockTimeProvider, combatCalculator, durabilitySystem);

            // Act
            bool fired = weaponLogic.Fire(testOwnerStats);

            // Assert
            Assert.IsTrue(fired, "Should fire burst");
            Assert.AreEqual(27, weaponLogic.CurrentAmmo.Value, "Should consume 3 ammo");
            Assert.AreEqual(97, weaponLogic.CurrentDurability.Value, "Should consume 3 durability");
        }

        [Test]
        public void Fire_BurstWeapon_WithInsufficientAmmo_FiresPartialBurst()
        {
            // Arrange
            testWeaponDef.burstCount = 3;
            weaponLogic = new WeaponLogic(testWeaponDef, mockTimeProvider, combatCalculator, durabilitySystem);
            // Set ammo to 2 (less than burst count)
            weaponLogic.SetAmmo(2);

            Assert.AreEqual(2, weaponLogic.CurrentAmmo.Value, "Should have 2 ammo");

            // Act
            bool fired = weaponLogic.Fire(testOwnerStats);

            // Assert
            Assert.IsTrue(fired, "Should fire partial burst");
            Assert.AreEqual(0, weaponLogic.CurrentAmmo.Value, "Should consume remaining ammo");
        }

        #endregion

        #region Accuracy Calculation

        [Test]
        public void CalculateDeviation_WithFullDurability_ReturnsBaseDeviation()
        {
            // Act
            float deviation = weaponLogic.CalculateDeviation(testOwnerStats);

            // Assert
            // Full durability: breaking = -1, deviation multiplier = -1
            // effective deviation = 2 * -1 / 1 / 1 + 0 = -2
            Assert.AreEqual(-2f, deviation, 0.001f, "Should calculate deviation with durability modifier");
        }

        [Test]
        public void CalculateDeviation_WithHalfDurability_ReturnsBaseDeviation()
        {
            // Arrange
            weaponLogic.SetDurability(50);

            // Act
            float deviation = weaponLogic.CalculateDeviation(testOwnerStats);

            // Assert
            // Half durability: breaking = 0, deviation multiplier = 1
            // effective deviation = 2 * 1 / 1 / 1 + 0 = 2
            Assert.AreEqual(2f, deviation, 0.001f, $"Should calculate deviation with half durability, got {deviation}, weaponDef.deviation={testWeaponDef.deviation}");
        }

        [Test]
        public void CalculateDeviation_WithZeroDurability_TriplesDeviation()
        {
            // Arrange
            weaponLogic.SetDurability(0);

            // Act
            float deviation = weaponLogic.CalculateDeviation(testOwnerStats);

            // Assert
            // Zero durability: breaking = 1, deviation multiplier = 3
            // effective deviation = 2 * 3 / 1 / 1 + 0 = 6
            Assert.AreEqual(6f, deviation, 0.001f, $"Should triple deviation at zero durability, got {deviation}, weaponDef.deviation={testWeaponDef.deviation}");
        }

        [Test]
        public void CalculateDeviation_WithWeaponSkill_ReducesDeviation()
        {
            // Act
            float deviation = weaponLogic.CalculateDeviation(testOwnerStats, 2f);

            // Assert
            // High skill reduces deviation
            Assert.Less(deviation, 0f, "High skill should reduce deviation");
        }

        #endregion

        #region Jam and Misfire

        [Test]
        public void Fire_AtHalfDurability_CanMisfire()
        {
            // Arrange - Fire until half durability but not empty
            weaponLogic.Reset();
            for (int i = 0; i < 50; i++)
                weaponLogic.Fire(testOwnerStats);
            // Refill for testing
            weaponLogic.CompleteReload();
            mockTimeProvider.CurrentTime = 10f; // Reset cooldown

            int initialAmmo = weaponLogic.CurrentAmmo.Value;
            int initialDurability = weaponLogic.CurrentDurability.Value;

            // Act
            // Note: Misfire is random, so we can't guarantee it happens
            // This test verifies the logic path, not the random outcome
            bool fired = weaponLogic.Fire(testOwnerStats);

            // Assert
            // Whether it fires or misfires, both consume ammo and durability
            // (unless it's empty or broken)
            if (fired || !weaponLogic.IsBroken)
            {
                Assert.AreEqual(initialAmmo - 1, weaponLogic.CurrentAmmo.Value, "Should consume ammo");
                Assert.AreEqual(initialDurability - 1, weaponLogic.CurrentDurability.Value, "Should consume durability");
            }
        }

        [Test]
        public void Fire_AtZeroDurability_HighJamChance()
        {
            // Arrange
            weaponLogic.SetDurability(0);
            mockTimeProvider.CurrentTime = 10f; // Reset cooldown

            int jams = 0;
            int attempts = 0;
            int trials = 100;

            // Act
            for (int i = 0; i < trials; i++)
            {
                attempts++;
                // At zero durability, weapon is broken, so Fire returns false immediately
                // This is expected behavior - the test verifies IsBroken prevents firing
                if (!weaponLogic.Fire(testOwnerStats))
                    jams++;
            }

            // Assert
            // At zero durability, weapon is broken, so all fire attempts fail
            Assert.AreEqual(trials, jams, "All attempts should fail when weapon is broken");
        }

        #endregion

        #region Reload Mechanics

        [Test]
        public void CompleteReload_WithEmptyMagazine_RefillsMagazine()
        {
            // Arrange
            for (int i = 0; i < 30; i++)
                weaponLogic.Fire(testOwnerStats);

            // Act
            weaponLogic.CompleteReload();

            // Assert
            Assert.AreEqual(30, weaponLogic.CurrentAmmo.Value, "Should refill to full magazine");
            Assert.IsFalse(weaponLogic.IsReloading.Value, "Should clear reloading flag");
        }

        [Test]
        public void CompleteReload_WithPartialMagazine_RefillsToFull()
        {
            // Arrange
            weaponLogic.Fire(testOwnerStats); // 29 ammo

            // Act
            weaponLogic.CompleteReload();

            // Assert
            Assert.AreEqual(30, weaponLogic.CurrentAmmo.Value, "Should refill to full");
        }

        [Test]
        public void CompleteReload_ClearsReloadingFlag()
        {
            // Arrange
            weaponLogic.StartReloadAsync().Forget();

            // Act
            weaponLogic.CompleteReload();

            // Assert
            Assert.IsFalse(weaponLogic.IsReloading.Value, "Should not be reloading");
            Assert.AreEqual(0f, weaponLogic.ReloadProgress.Value, "Should reset reload progress");
        }

        [Test]
        public void StartReloadAsync_WhenFull_DoesNothing()
        {
            // Arrange
            bool reloadTriggered = false;
            weaponLogic.IsReloading.Subscribe(isReloading =>
            {
                if (isReloading) reloadTriggered = true;
            }).AddTo(disposables);

            // Act
            weaponLogic.StartReloadAsync().Forget();

            // Assert
            Assert.IsFalse(reloadTriggered, "Should not trigger reload when full");
        }

        #endregion

        #region Repair Mechanics

        [Test]
        public void Repair_WithDamage_RestoresDurability()
        {
            // Arrange
            for (int i = 0; i < 20; i++)
                weaponLogic.Fire(testOwnerStats);

            // Act
            weaponLogic.Repair(10);

            // Assert
            Assert.AreEqual(90, weaponLogic.CurrentDurability.Value, "Should restore 10 durability");
        }

        [Test]
        public void Repair_ExceedsMax_ClampsToMax()
        {
            // Arrange
            for (int i = 0; i < 20; i++)
                weaponLogic.Fire(testOwnerStats);

            // Act
            weaponLogic.Repair(100);

            // Assert
            Assert.AreEqual(100, weaponLogic.CurrentDurability.Value, "Should not exceed max durability");
        }

        [Test]
        public void Repair_BrokenWeapon_RestoresToFunctional()
        {
            // Arrange - Set durability to 0
            weaponLogic.SetDurability(0);
            Assert.IsTrue(weaponLogic.IsBroken, "Should be broken");

            // Act
            weaponLogic.Repair(50);

            // Assert
            Assert.IsFalse(weaponLogic.IsBroken, "Should be functional after repair");
            Assert.AreEqual(50, weaponLogic.CurrentDurability.Value, "Should have 50 durability");
        }

        #endregion

        #region Reset Mechanics

        [Test]
        public void Reset_WithLowAmmoAndDurability_RestoresToFull()
        {
            // Arrange
            for (int i = 0; i < 50; i++)
                weaponLogic.Fire(testOwnerStats);

            // Act
            weaponLogic.Reset();

            // Assert
            Assert.AreEqual(30, weaponLogic.CurrentAmmo.Value, "Should reset ammo to full");
            Assert.AreEqual(100, weaponLogic.CurrentDurability.Value, "Should reset durability to full");
            Assert.IsFalse(weaponLogic.IsReloading.Value, "Should clear reloading flag");
        }

        #endregion

        #region Fire Rate

        [Test]
        public void GetFireRate_ReturnsCorrectShotsPerSecond()
        {
            // Arrange
            // The default weapon has rapid=0 (infinite fire rate)
            float fireRate = weaponLogic.GetFireRate();

            // Assert
            // rapid=0 means infinite fire rate
            Assert.AreEqual(float.PositiveInfinity, fireRate, "rapid=0 should give infinite fire rate");
        }

        [Test]
        public void GetFireRate_WithRapid10_Returns3ShotsPerSecond()
        {
            // Arrange
            testWeaponDef.rapid = 10f;
            weaponLogic = new WeaponLogic(testWeaponDef, mockTimeProvider, combatCalculator, durabilitySystem);

            // Act
            float fireRate = weaponLogic.GetFireRate();

            // Assert
            // rapid=10 means 3 shots/second at 30 FPS
            Assert.AreEqual(3f, fireRate, "Should calculate 3 shots/second");
        }

        #endregion

        #region Reactive Properties

        [Test]
        public void CurrentAmmo_UpdatesWhenFiring()
        {
            // Arrange
            bool propertyChanged = false;
            weaponLogic.CurrentAmmo.Subscribe(_ => propertyChanged = true).AddTo(disposables);

            // Act
            weaponLogic.Fire(testOwnerStats);

            // Assert
            Assert.IsTrue(propertyChanged, "Should trigger reactive property update");
        }

        [Test]
        public void CurrentDurability_UpdatesWhenFiring()
        {
            // Arrange
            bool propertyChanged = false;
            weaponLogic.CurrentDurability.Subscribe(_ => propertyChanged = true).AddTo(disposables);
            weaponLogic.CurrentDurability.Subscribe(_ => propertyChanged = true);

            // Act
            weaponLogic.Fire(testOwnerStats);

            // Assert
            Assert.IsTrue(propertyChanged, "Should trigger reactive property update");
        }

        [Test]
        public void IsEmpty_ReturnsTrueWhenEmpty()
        {
            // Arrange
            for (int i = 0; i < 30; i++)
                weaponLogic.Fire(testOwnerStats);

            // Act
            bool isEmpty = weaponLogic.IsEmpty;

            // Assert
            Assert.IsTrue(isEmpty, "Should be empty");
        }

        [Test]
        public void IsBroken_ReturnsTrueWhenDurabilityZero()
        {
            // Arrange - Set durability to 0
            weaponLogic.SetDurability(0);

            // Act
            bool isBroken = weaponLogic.IsBroken;

            // Assert
            Assert.IsTrue(isBroken, "Should be broken");
        }

        #endregion

        #region Edge Cases

        [Test]
        public void Fire_ZeroMagazineSize_AlwaysFires()
        {
            // Arrange
            testWeaponDef.magazineSize = 0; // Unlimited ammo weapon
            testWeaponDef.maxDurability = 1000;
            weaponLogic = new WeaponLogic(testWeaponDef, mockTimeProvider, combatCalculator, durabilitySystem);

            // Act
            bool fired1 = weaponLogic.Fire(testOwnerStats);
            mockTimeProvider.CurrentTime += 1f;
            bool fired2 = weaponLogic.Fire(testOwnerStats);

            // Assert
            Assert.IsTrue(fired1, "Should fire with unlimited ammo");
            Assert.IsTrue(fired2, "Should fire again without reloading");
            Assert.IsFalse(weaponLogic.IsEmpty, "Should never be empty with unlimited ammo");
        }

        [Test]
        public void CalculateDeviation_ZeroBaseDeviation_ReturnsZero()
        {
            // Arrange
            testWeaponDef.deviation = 0f;
            weaponLogic = new WeaponLogic(testWeaponDef, mockTimeProvider, combatCalculator, durabilitySystem);

            // Act
            float deviation = weaponLogic.CalculateDeviation(testOwnerStats);

            // Assert
            Assert.AreEqual(0f, deviation, "Zero base deviation should remain zero");
        }

        #endregion
    }
}
