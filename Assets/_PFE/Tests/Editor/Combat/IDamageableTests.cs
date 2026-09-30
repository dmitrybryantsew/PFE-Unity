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

        // === The vulnerability table (IDamageable.Vulnerabilities) ===

        [Test]
        public void Vulnerabilities_WithNoDefinition_IsAs3sNeutral_NotTheIdentity()
        {
            // AS3 fills vulner with 1 and then unconditionally forces vulner[D_EMP] = 0
            // (Unit.as:583-590), so a unit that declares no <vulner> element is EMP-immune — not
            // "unmodified". Neutral and the identity differ in exactly one slot, which is why the
            // fallback has to be a named constant rather than whatever a default happened to be.
            VulnerabilityData table = _unitController.Vulnerabilities;

            Assert.AreEqual(0f, table.GetVulnerability(DamageType.EMP), 1e-6f,
                "Neutral carries emp = 0; substituting the identity would hand out EMP damage the " +
                "oracle denies.");
            Assert.AreEqual(1f, table.GetVulnerability(DamageType.Laser), 1e-6f,
                "…and every other slot is still 1.");
        }

        [Test]
        public void Vulnerabilities_WithBothStatsAndDefinition_PrefersTheLiveTable()
        {
            // A7b changed this precedence, and this test is the one that pinned the old one. Before A7b
            // the property read the definition; now UnitStats is the live table and the definition is
            // only the *baseline* it is derived from.
            //
            // The oracle's split is why: `Unit.begvulner` is the unit's static <vulner> element and
            // `vulner` is derived from it (Unit.as:977-984, :3466-3495) — and for the player the
            // derivation is not the identity, because Pers.armorParameters():2067 folds the equipped
            // armour's resist in. So the derived value is the one combat must see, and reading the
            // baseline directly would silently skip the fold. Note this fixture's SetUp always assigns
            // a _unitStats, so the definition is genuinely *not* what a unit with both reads.
            var definition = ScriptableObject.CreateInstance<UnitDefinition>();
            definition.vulnerabilities = new VulnerabilityData(1f);
            definition.vulnerabilities.laser = 0.5f;

            var field = typeof(UnitController).GetField("_stats",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            field.SetValue(_unitController, definition);

            Assert.AreEqual(1f, _unitController.Vulnerabilities.GetVulnerability(DamageType.Laser),
                1e-6f,
                "UnitStats wins when it exists, so the definition's laser = 0.5 must NOT appear here. " +
                "The live table is the baseline folded with any armour resist.");

            Object.DestroyImmediate(definition);
        }

        [Test]
        public void Vulnerabilities_WithNoStats_FallsBackToTheDefinition()
        {
            // The complement to the test above, and the case that test used to cover. A unit with no
            // UnitStats has no derivation to run, so its baseline IS its live table — which is why the
            // fallback is the definition rather than an error, and why an NPC needs no UnitStats to
            // carry a real <vulner> table into combat.
            //
            // LATENT GAP, named rather than hidden: a unit that has BOTH reads UnitStats, and nothing
            // seeds UnitStats from the definition today, so such an NPC would read Neutral and silently
            // lose its <vulner> element. SetVulnerabilityBaseline(definition.vulnerabilities) is the
            // seam that closes it and the spawner is what must call it. Until then this test pins the
            // no-stats path only.
            var definition = ScriptableObject.CreateInstance<UnitDefinition>();
            definition.vulnerabilities = new VulnerabilityData(1f);
            definition.vulnerabilities.laser = 0.5f;

            var statsField = typeof(UnitController).GetField("_unitStats",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            statsField.SetValue(_unitController, null);

            var field = typeof(UnitController).GetField("_stats",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            field.SetValue(_unitController, definition);

            Assert.AreEqual(0.5f, _unitController.Vulnerabilities.GetVulnerability(DamageType.Laser),
                1e-6f,
                "AllData.as:118 — raider3's <vulner laser='0.5'/> has to reach the resolver intact when " +
                "there is no derived table to prefer.");

            Object.DestroyImmediate(definition);
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
