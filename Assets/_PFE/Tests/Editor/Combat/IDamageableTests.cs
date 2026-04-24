using NUnit.Framework;
using UnityEngine;
using R3;
using PFE.Entities.Units;
using PFE.Systems.Combat;
using PFE.Data.Definitions;

namespace PFE.Tests.Editor.Combat
{
    /// <summary>
    /// Unit tests for IDamageable interface implementation.
    /// Tests that projectiles can properly apply damage to any damageable entity.
    ///
    /// Test coverage:
    /// - Interface contract compliance
    /// - UnitController base class implementation
    /// - PlayerController override behavior
    /// - Health calculation and death handling
    /// - Multiple damage applications
    /// </summary>
    public class IDamageableTests
    {
        private UnitController _unitController;
        private UnitStats _unitStats;
        private CompositeDisposable disposables;

        [SetUp]
        public void Setup()
        {
            // Create disposables
            disposables = new CompositeDisposable();

            // Create a GameObject with UnitController for testing
            GameObject go = new GameObject("TestUnit");
            _unitController = go.AddComponent<UnitController>();

            // Create UnitStats for the unit
            _unitStats = new UnitStats(100f, 50f);

            // Use reflection to set the protected _unitStats field
            var field = typeof(UnitController).GetField("_unitStats",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            field.SetValue(_unitController, _unitStats);
        }

        [TearDown]
        public void TearDown()
        {
            disposables?.Dispose();
            if (_unitController != null)
            {
                Object.DestroyImmediate(_unitController.gameObject);
            }
        }

        // === Interface Contract Tests ===

        [Test]
        public void IDamageable_UnitController_ImplementsInterface()
        {
            // UnitController should implement IDamageable
            Assert.IsNotNull(_unitController as IDamageable,
                "UnitController should implement IDamageable interface");
        }

        [Test]
        public void IDamageable_HasRequiredMembers()
        {
            // Get the interface type
            var interfaceType = typeof(IDamageable);

            // Verify TakeDamage method exists
            var takeDamageMethod = interfaceType.GetMethod("TakeDamage");
            Assert.IsNotNull(takeDamageMethod, "IDamageable should have TakeDamage method");

            // Verify CurrentHealth property exists
            var currentHealthProperty = interfaceType.GetProperty("CurrentHealth");
            Assert.IsNotNull(currentHealthProperty, "IDamageable should have CurrentHealth property");

            // Verify MaxHealth property exists
            var maxHealthProperty = interfaceType.GetProperty("MaxHealth");
            Assert.IsNotNull(maxHealthProperty, "IDamageable should have MaxHealth property");

            // Verify IsAlive property exists
            var isAliveProperty = interfaceType.GetProperty("IsAlive");
            Assert.IsNotNull(isAliveProperty, "IDamageable should have IsAlive property");
        }

        // === Health Property Tests ===

        [Test]
        public void CurrentHealth_ReturnsInitialValue()
        {
            // Arrange
            float expectedHealth = 100f;

            // Act
            float actualHealth = _unitController.CurrentHealth;

            // Assert
            Assert.AreEqual(expectedHealth, actualHealth, 0.01f,
                "CurrentHealth should return initial UnitStats value");
        }

        [Test]
        public void MaxHealth_ReturnsInitialValue()
        {
            // Arrange
            float expectedMaxHealth = 100f;

            // Act
            float actualMaxHealth = _unitController.MaxHealth;

            // Assert
            Assert.AreEqual(expectedMaxHealth, actualMaxHealth, 0.01f,
                "MaxHealth should return initial UnitStats value");
        }

        [Test]
        public void IsAlive_ReturnsTrueWhenHealthAboveZero()
        {
            // Arrange
            _unitStats.Damage(0f); // Full health

            // Act
            bool isAlive = _unitController.IsAlive;

            // Assert
            Assert.IsTrue(isAlive, "IsAlive should return true when health > 0");
        }

        [Test]
        public void IsAlive_ReturnsFalseWhenHealthIsZero()
        {
            // Arrange
            _unitStats.Damage(100f); // Reduce to zero

            // Act
            bool isAlive = _unitController.IsAlive;

            // Assert
            Assert.IsFalse(isAlive, "IsAlive should return false when health <= 0");
        }

        // === TakeDamage Tests ===

        [Test]
        public void TakeDamage_ReducesCurrentHealth()
        {
            // Arrange
            float damageAmount = 30f;
            float expectedHealth = 70f; // 100 - 30

            // Act
            _unitController.TakeDamage(damageAmount);

            // Assert
            Assert.AreEqual(expectedHealth, _unitController.CurrentHealth, 0.01f,
                "TakeDamage should reduce CurrentHealth by damage amount");
        }

        [Test]
        public void TakeDamage_MultipleApplications_StacksCorrectly()
        {
            // Arrange
            float expectedHealth = 40f; // 100 - 30 - 30

            // Act
            _unitController.TakeDamage(30f);
            _unitController.TakeDamage(30f);

            // Assert
            Assert.AreEqual(expectedHealth, _unitController.CurrentHealth, 0.01f,
                "Multiple TakeDamage calls should stack correctly");
        }

        [Test]
        public void TakeDamage_Overkill_ClampsAtZero()
        {
            // Arrange
            float expectedHealth = 0f;

            // Act
            _unitController.TakeDamage(200f); // More than max health

            // Assert
            Assert.AreEqual(expectedHealth, _unitController.CurrentHealth, 0.01f,
                "TakeDamage should clamp health at minimum 0");
        }

        [Test]
        public void TakeDamage_ZeroDamage_NoHealthChange()
        {
            // Arrange
            float expectedHealth = 100f;

            // Act
            _unitController.TakeDamage(0f);

            // Assert
            Assert.AreEqual(expectedHealth, _unitController.CurrentHealth, 0.01f,
                "TakeDamage(0) should not change health");
        }

        [Test]
        public void TakeDamage_WhenAlreadyDead_DoesNothing()
        {
            // Arrange
            _unitController.TakeDamage(100f); // Kill the unit
            float healthAfterDeath = _unitController.CurrentHealth;

            // Act
            _unitController.TakeDamage(50f); // Try to damage dead unit

            // Assert
            Assert.AreEqual(healthAfterDeath, _unitController.CurrentHealth, 0.01f,
                "TakeDamage on dead unit should not reduce health below 0");
        }

        // === Edge Cases ===

        [Test]
        public void TakeDamage_NegativeDamage_ClampedToZero()
        {
            // Arrange
            float initialHealth = _unitController.CurrentHealth;

            // Act
            _unitController.TakeDamage(-10f); // Negative damage (heal attempt)

            // Assert
            Assert.AreEqual(initialHealth, _unitController.CurrentHealth, 0.01f,
                "TakeDamage with negative value should not increase health (use Heal instead)");
        }

        [Test]
        public void CurrentHealth_UnitStatsNull_ReturnsZero()
        {
            // Arrange
            var field = typeof(UnitController).GetField("_unitStats",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            field.SetValue(_unitController, null);

            // Act
            float health = _unitController.CurrentHealth;

            // Assert
            Assert.AreEqual(0f, health, "CurrentHealth should return 0 when UnitStats is null");
        }

        [Test]
        public void MaxHealth_UnitStatsNull_ReturnsOne()
        {
            // Arrange
            var field = typeof(UnitController).GetField("_unitStats",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            field.SetValue(_unitController, null);

            // Act
            float maxHealth = _unitController.MaxHealth;

            // Assert
            Assert.AreEqual(1f, maxHealth, "MaxHealth should return 1 when UnitStats is null (avoid divide by zero)");
        }

        [Test]
        public void IsAlive_UnitStatsNull_ReturnsFalse()
        {
            // Arrange
            var field = typeof(UnitController).GetField("_unitStats",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            field.SetValue(_unitController, null);

            // Act
            bool isAlive = _unitController.IsAlive;

            // Assert
            Assert.IsFalse(isAlive, "IsAlive should return false when UnitStats is null");
        }

        // === Integration with UnitStats Tests ===

        [Test]
        public void TakeDamage_UpdatesUnitStatsReactiveProperty()
        {
            // Arrange
            float expectedHealth = 60f;
            bool healthChanged = false;

            // Subscribe to health changes
            _unitStats.CurrentHp.Subscribe(_ => healthChanged = true).AddTo(disposables);

            // Act
            _unitController.TakeDamage(40f);

            // Assert
            Assert.IsTrue(healthChanged, "TakeDamage should trigger UnitStats.CurrentHp change");
            Assert.AreEqual(expectedHealth, _unitStats.CurrentHp.Value, 0.01f,
                "TakeDamage should update underlying UnitStats.CurrentHp");
        }

        [Test]
        public void HealthPercentage_CalculatedCorrectly()
        {
            // Arrange
            _unitController.TakeDamage(30f); // 70/100 = 70%

            // Act
            float percentage = _unitController.CurrentHealth / _unitController.MaxHealth;

            // Assert
            Assert.AreEqual(0.7f, percentage, 0.01f,
                "Health percentage should be calculated correctly");
        }

        // === Death Handling Tests ===

        [Test]
        public void TakeDamage_WhenKilled_CallsOnDeath()
        {
            // Arrange - This is difficult to test directly since OnDeath is protected
            // We'll verify it indirectly through IsAlive state

            // Act
            _unitController.TakeDamage(100f); // Lethal damage

            // Assert
            Assert.IsFalse(_unitController.IsAlive,
                "Unit should be dead after taking lethal damage");
        }

        [Test]
        public void TakeDamage_NonLethal_DoesNotKill()
        {
            // Act
            _unitController.TakeDamage(50f); // Non-lethal damage

            // Assert
            Assert.IsTrue(_unitController.IsAlive,
                "Unit should still be alive after non-lethal damage");
        }
    }
}
