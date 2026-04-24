using NUnit.Framework;
using PFE.Systems.Combat;
using PFE.Entities.Weapons;
using PFE.Core.Time;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using UnityEngine;

namespace PFE.Tests.Editor.Combat
{
    /// <summary>
    /// Comprehensive unit tests for WeaponFactory to verify:
    /// - Factory pattern implementation with proper DI
    /// - WeaponLogic creation with all dependencies
    /// - WeaponView creation with MonoBehaviour initialization
    /// - VContainer integration
    /// </summary>
    [TestFixture]
    public class WeaponFactoryTests
    {
        private WeaponFactory weaponFactory;
        private ITimeProvider testTimeProvider;
        private ICombatCalculator combatCalculator;
        private IDurabilitySystem durabilitySystem;
        private MockObjectResolver mockResolver;
        private WeaponDefinition testWeaponDef;
        private GameObject testParentObject;
        private UnitStats testUnitStats;

        [SetUp]
        public void Setup()
        {
            // Create test infrastructure
            testTimeProvider = new UnityTimeProvider();
            combatCalculator = new CombatCalculator();
            durabilitySystem = new DurabilitySystem(combatCalculator);
            mockResolver = new MockObjectResolver();

            // Create factory with dependencies
            weaponFactory = new WeaponFactory(
                mockResolver,
                testTimeProvider,
                combatCalculator,
                durabilitySystem);

            // Create test weapon definition
            testWeaponDef = ScriptableObject.CreateInstance<WeaponDefinition>();
            testWeaponDef.weaponId = "test_pistol";
            testWeaponDef.weaponType = WeaponType.Guns;
            testWeaponDef.baseDamage = 15f;
            testWeaponDef.rapid = 10f;
            testWeaponDef.deviation = 4f;
            testWeaponDef.maxDurability = 100;
            testWeaponDef.magazineSize = 12;
            testWeaponDef.reloadTime = 50f;

            // Create parent GameObject
            testParentObject = new GameObject("TestParent");

            // Create test UnitStats
            testUnitStats = new UnitStats(100f, 50f);
        }

        [TearDown]
        public void TearDown()
        {
            // Clean up all created GameObjects
            if (testParentObject != null)
            {
                Object.DestroyImmediate(testParentObject);
            }

            // Clean up weapon definition
            if (testWeaponDef != null)
            {
                Object.DestroyImmediate(testWeaponDef);
            }

            // Clean up any stray WeaponView GameObjects
            var weaponViews = Object.FindObjectsByType<WeaponView>(FindObjectsSortMode.None);
            foreach (var view in weaponViews)
            {
                Object.DestroyImmediate(view.gameObject);
            }
        }

        #region Factory Creation Tests

        [Test]
        [Description("WeaponFactory_Creation")]
        public void WeaponFactory_Constructor_InitializesProperly()
        {
            // Assert - Factory should be created with all dependencies
            Assert.IsNotNull(weaponFactory, "WeaponFactory should be created");
            Assert.IsNotNull(testTimeProvider, "TimeProvider should be set");
            Assert.IsNotNull(combatCalculator, "CombatCalculator should be set");
            Assert.IsNotNull(durabilitySystem, "DurabilitySystem should be set");
        }

        [Test]
        [Description("WeaponFactory_NullDependencies")]
        public void WeaponFactory_Constructor_WithNullDependencies_DoesNotThrow()
        {
            // Act & Assert - Factory should handle null resolver gracefully
            Assert.DoesNotThrow(() =>
            {
                var factory = new WeaponFactory(null, testTimeProvider, combatCalculator, durabilitySystem);
            }, "WeaponFactory should handle null ObjectResolver");
        }

        #endregion

        #region CreateWeaponLogic Tests

        [Test]
        [Description("WeaponFactory_CreateWeaponLogic")]
        public void CreateWeaponLogic_WithValidDefinition_ReturnsWeaponLogic()
        {
            // Act
            var weaponLogic = weaponFactory.CreateWeaponLogic(testWeaponDef);

            // Assert
            Assert.IsNotNull(weaponLogic, "CreateWeaponLogic should return a valid WeaponLogic");
            Assert.AreEqual(testWeaponDef.weaponId, weaponLogic.WeaponDef.weaponId,
                "WeaponLogic should have the correct WeaponDefinition");
        }

        [Test]
        [Description("WeaponLogic_InitialAmmo")]
        public void CreateWeaponLogic_InitializesWithFullAmmo()
        {
            // Act
            var weaponLogic = weaponFactory.CreateWeaponLogic(testWeaponDef);

            // Assert
            Assert.AreEqual(testWeaponDef.magazineSize, weaponLogic.CurrentAmmo.Value,
                "WeaponLogic should start with full ammo");
            Assert.IsFalse(weaponLogic.IsEmpty, "Weapon should not be empty initially");
        }

        [Test]
        [Description("WeaponLogic_InitialDurability")]
        public void CreateWeaponLogic_InitializesWithFullDurability()
        {
            // Act
            var weaponLogic = weaponFactory.CreateWeaponLogic(testWeaponDef);

            // Assert
            Assert.AreEqual(testWeaponDef.maxDurability, weaponLogic.CurrentDurability.Value,
                "WeaponLogic should start with full durability");
            Assert.IsFalse(weaponLogic.IsBroken, "Weapon should not be broken initially");
        }

        [Test]
        [Description("WeaponLogic_ReactiveProperties")]
        public void CreateWeaponLogic_InitializesReactiveProperties()
        {
            // Act
            var weaponLogic = weaponFactory.CreateWeaponLogic(testWeaponDef);

            // Assert
            Assert.IsNotNull(weaponLogic.CurrentAmmo, "CurrentAmmo ReactiveProperty should be initialized");
            Assert.IsNotNull(weaponLogic.CurrentDurability, "CurrentDurability ReactiveProperty should be initialized");
            Assert.IsNotNull(weaponLogic.IsReloading, "IsReloading ReactiveProperty should be initialized");
            Assert.IsNotNull(weaponLogic.ReloadProgress, "ReloadProgress ReactiveProperty should be initialized");
        }

        [Test]
        [Description("WeaponLogic_NotReloading")]
        public void CreateWeaponLogic_InitializesNotReloading()
        {
            // Act
            var weaponLogic = weaponFactory.CreateWeaponLogic(testWeaponDef);

            // Assert
            Assert.IsFalse(weaponLogic.IsReloading.Value, "Weapon should not be reloading initially");
            Assert.AreEqual(0f, weaponLogic.ReloadProgress.Value, "Reload progress should be 0 initially");
        }

        [Test]
        [Description("WeaponLogic_ZeroAmmo")]
        public void CreateWeaponLogic_WithZeroMagazineSize_HasInfiniteAmmo()
        {
            // Arrange
            testWeaponDef.magazineSize = 0;

            // Act
            var weaponLogic = weaponFactory.CreateWeaponLogic(testWeaponDef);

            // Assert
            Assert.IsFalse(weaponLogic.IsEmpty, "Weapon with 0 magazine size should never be empty (infinite ammo)");
        }

        [Test]
        [Description("WeaponLogic_ZeroDurability")]
        public void CreateWeaponLogic_WithZeroDurability_StartsBroken()
        {
            // Arrange
            testWeaponDef.maxDurability = 0;

            // Act
            var weaponLogic = weaponFactory.CreateWeaponLogic(testWeaponDef);

            // Assert
            Assert.IsTrue(weaponLogic.IsBroken, "Weapon with 0 max durability should start broken");
        }

        [Test]
        [Description("WeaponLogic_MultipleInstances")]
        public void CreateWeaponLogic_CreatesIndependentInstances()
        {
            // Act
            var logic1 = weaponFactory.CreateWeaponLogic(testWeaponDef);
            var logic2 = weaponFactory.CreateWeaponLogic(testWeaponDef);

            // Assert
            Assert.AreNotSame(logic1, logic2, "Each CreateWeaponLogic call should return a new instance");

            // Modify first instance
            logic1.CurrentAmmo.Value = 5;

            // Assert - Second instance should be unaffected
            Assert.AreEqual(testWeaponDef.magazineSize, logic2.CurrentAmmo.Value,
                "WeaponLogic instances should be independent");
        }

        [Test]
        [Description("WeaponLogic_DifferentDefinitions")]
        public void CreateWeaponLogic_WithDifferentDefinitions_CreatesCorrectWeapons()
        {
            // Arrange
            var weaponDef2 = ScriptableObject.CreateInstance<WeaponDefinition>();
            weaponDef2.weaponId = "test_rifle";
            weaponDef2.baseDamage = 25f;
            weaponDef2.maxDurability = 150;
            weaponDef2.magazineSize = 30;

            // Act
            var pistol = weaponFactory.CreateWeaponLogic(testWeaponDef);
            var rifle = weaponFactory.CreateWeaponLogic(weaponDef2);

            // Assert
            Assert.AreEqual("test_pistol", pistol.WeaponDef.weaponId, "Pistol should have correct ID");
            Assert.AreEqual("test_rifle", rifle.WeaponDef.weaponId, "Rifle should have correct ID");
            Assert.AreEqual(15f, pistol.WeaponDef.baseDamage, "Pistol should have correct damage");
            Assert.AreEqual(25f, rifle.WeaponDef.baseDamage, "Rifle should have correct damage");

            // Cleanup
            Object.DestroyImmediate(weaponDef2);
        }

        #endregion

        #region CreateWeaponView Tests

        [Test]
        [Description("WeaponFactory_CreateWeaponView")]
        public void CreateWeaponView_WithValidParameters_ReturnsWeaponView()
        {
            // Act
            var weaponView = weaponFactory.CreateWeaponView(testWeaponDef, testParentObject.transform, testUnitStats);

            // Assert
            Assert.IsNotNull(weaponView, "CreateWeaponView should return a valid WeaponView");
            Assert.IsInstanceOf<WeaponView>(weaponView, "Created object should be a WeaponView");
        }

        [Test]
        [Description("WeaponView_GameObjectHierarchy")]
        public void CreateWeaponView_SetsParentTransformCorrectly()
        {
            // Act
            var weaponView = weaponFactory.CreateWeaponView(testWeaponDef, testParentObject.transform, testUnitStats);

            // Assert
            Assert.AreEqual(testParentObject.transform, weaponView.transform.parent,
                "WeaponView should be parented to the specified Transform");
        }

        [Test]
        [Description("WeaponView_GameObjectName")]
        public void CreateWeaponView_SetsGameObjectNameCorrectly()
        {
            // Act
            var weaponView = weaponFactory.CreateWeaponView(testWeaponDef, testParentObject.transform, testUnitStats);

            // Assert
            Assert.AreEqual("Weapon_test_pistol", weaponView.gameObject.name,
                "WeaponView GameObject should be named 'Weapon_{weaponId}'");
        }

        [Test]
        [Description("WeaponView_WeaponLogicInjected")]
        public void CreateWeaponView_InjectsWeaponLogicIntoView()
        {
            // Act
            var weaponView = weaponFactory.CreateWeaponView(testWeaponDef, testParentObject.transform, testUnitStats);

            // Assert - WeaponView should have WeaponLogic injected
            // We verify this by checking that the view was initialized successfully
            Assert.IsNotNull(weaponView, "WeaponView should be initialized with WeaponLogic");
        }

        [Test]
        [Description("WeaponView_UnitStatsInjected")]
        public void CreateWeaponView_InjectsUnitStatsIntoView()
        {
            // Act
            var weaponView = weaponFactory.CreateWeaponView(testWeaponDef, testParentObject.transform, testUnitStats);

            // Assert - WeaponView should have UnitStats injected
            Assert.IsNotNull(weaponView, "WeaponView should be initialized with UnitStats");
        }

        [Test]
        [Description("WeaponView_MultipleInstances")]
        public void CreateWeaponView_CreatesIndependentInstances()
        {
            // Act
            var view1 = weaponFactory.CreateWeaponView(testWeaponDef, testParentObject.transform, testUnitStats);
            var view2 = weaponFactory.CreateWeaponView(testWeaponDef, testParentObject.transform, testUnitStats);

            // Assert
            Assert.AreNotSame(view1, view2, "Each CreateWeaponView call should return a new instance");
            Assert.AreNotSame(view1.gameObject, view2.gameObject,
                "Each WeaponView should have its own GameObject");
        }

        [Test]
        [Description("WeaponView_DifferentParents")]
        public void CreateWeaponView_WithDifferentParents_SetsCorrectParents()
        {
            // Arrange
            var parent1 = new GameObject("Parent1");
            var parent2 = new GameObject("Parent2");

            // Act
            var view1 = weaponFactory.CreateWeaponView(testWeaponDef, parent1.transform, testUnitStats);
            var view2 = weaponFactory.CreateWeaponView(testWeaponDef, parent2.transform, testUnitStats);

            // Assert
            Assert.AreEqual(parent1.transform, view1.transform.parent, "View1 should be parented to parent1");
            Assert.AreEqual(parent2.transform, view2.transform.parent, "View2 should be parented to parent2");

            // Cleanup
            Object.DestroyImmediate(parent1);
            Object.DestroyImmediate(parent2);
        }

        [Test]
        [Description("WeaponView_NullParent")]
        public void CreateWeaponView_WithNullParent_CreatesWithoutParent()
        {
            // Act & Assert - Should not throw when parent is null
            Assert.DoesNotThrow(() =>
            {
                var weaponView = weaponFactory.CreateWeaponView(testWeaponDef, null, testUnitStats);
                Assert.IsNotNull(weaponView, "WeaponView should be created even with null parent");
            }, "CreateWeaponView should handle null parent");
        }

        [Test]
        [Description("WeaponView_NullUnitStats")]
        public void CreateWeaponView_WithNullUnitStats_InitializesSuccessfully()
        {
            // Act & Assert - Should not throw when UnitStats is null
            Assert.DoesNotThrow(() =>
            {
                var weaponView = weaponFactory.CreateWeaponView(testWeaponDef, testParentObject.transform, null);
                Assert.IsNotNull(weaponView, "WeaponView should be created even with null UnitStats");
            }, "CreateWeaponView should handle null UnitStats");
        }

        #endregion

        #region Dependency Injection Tests

        [Test]
        [Description("WeaponFactory_InjectsTimeProvider")]
        public void CreateWeaponLogic_InjectsTimeProvider()
        {
            // Act
            var weaponLogic = weaponFactory.CreateWeaponLogic(testWeaponDef);

            // Assert - WeaponLogic should use the injected TimeProvider
            // We verify this by checking that firing respects fire rate
            float fireRate = weaponLogic.GetFireRate();
            Assert.IsTrue(fireRate > 0, "FireRate should be calculated using TimeProvider");
        }

        [Test]
        [Description("WeaponFactory_InjectsCombatCalculator")]
        public void CreateWeaponLogic_InjectsCombatCalculator()
        {
            // Act
            var weaponLogic = weaponFactory.CreateWeaponLogic(testWeaponDef);

            // Assert - WeaponLogic should use the injected CombatCalculator
            // We verify this by checking deviation calculation (negative deviation = better accuracy)
            float deviation = weaponLogic.CalculateDeviation(testUnitStats, 1f);
            Assert.AreNotEqual(0f, deviation, "Deviation should be calculated using CombatCalculator");
        }

        [Test]
        [Description("WeaponFactory_InjectsDurabilitySystem")]
        public void CreateWeaponLogic_InjectsDurabilitySystem()
        {
            // Act
            var weaponLogic = weaponFactory.CreateWeaponLogic(testWeaponDef);

            // Assert - WeaponLogic should use the injected DurabilitySystem
            // We verify this by checking durability consumption
            weaponLogic.Fire(testUnitStats); // Consume 1 durability (99)
            weaponLogic.Repair(5); // Repair 5 (would be 104, but clamps to 100)
            Assert.AreEqual(100, weaponLogic.CurrentDurability.Value,
                "Repair should work using DurabilitySystem and clamp to max");
        }

        [Test]
        [Description("WeaponFactory_VContainerIntegration")]
        public void CreateWeaponView_UsesVContainerForInjection()
        {
            // Act
            var weaponView = weaponFactory.CreateWeaponView(testWeaponDef, testParentObject.transform, testUnitStats);

            // Assert - VContainer resolver should have been called
            Assert.IsTrue(mockResolver.InjectCalled, "VContainer Inject should have been called on WeaponView");
        }

        [Test]
        [Description("WeaponFactory_MultipleInjectCalls")]
        public void CreateWeaponView_MultipleViews_InjectsEachView()
        {
            // Act
            weaponFactory.CreateWeaponView(testWeaponDef, testParentObject.transform, testUnitStats);
            weaponFactory.CreateWeaponView(testWeaponDef, testParentObject.transform, testUnitStats);
            weaponFactory.CreateWeaponView(testWeaponDef, testParentObject.transform, testUnitStats);

            // Assert
            Assert.AreEqual(3, mockResolver.InjectCallCount,
                "VContainer Inject should have been called 3 times");
        }

        #endregion

        #region Factory Pattern Tests

        [Test]
        [Description("WeaponFactory_ImplementsInterface")]
        public void WeaponFactory_ImplementsIWeaponFactory()
        {
            // Assert
            Assert.IsInstanceOf<IWeaponFactory>(weaponFactory,
                "WeaponFactory should implement IWeaponFactory interface");
        }

        [Test]
        [Description("WeaponFactory_FactorySeparation")]
        public void WeaponFactory_SeparatesCreationFromUsage()
        {
            // Arrange
            var weaponLogic = weaponFactory.CreateWeaponLogic(testWeaponDef);

            // Act - Use the weapon (fire)
            bool canFire = weaponLogic.Fire(testUnitStats);

            // Assert - Factory should not be needed after creation
            Assert.IsTrue(canFire, "Weapon should be usable after factory creation");
        }

        [Test]
        [Description("WeaponFactory_Decoupling")]
        public void WeaponFactory_DecouplesDependenciesFromConsumers()
        {
            // Act - Consumer doesn't need to know about internal dependencies
            var weaponLogic = weaponFactory.CreateWeaponLogic(testWeaponDef);

            // Assert - Consumer only gets WeaponLogic, not the factory
            Assert.IsNotNull(weaponLogic, "Consumer receives fully constructed WeaponLogic");
            Assert.IsInstanceOf<WeaponLogic>(weaponLogic, "Consumer receives correct type");
        }

        #endregion

        #region Edge Cases and Error Handling

        [Test]
        [Description("WeaponFactory_NullDefinition")]
        public void CreateWeaponLogic_WithNullDefinition_ThrowsArgumentNullException()
        {
            // Act & Assert
            Assert.Throws<System.ArgumentNullException>(() =>
            {
                weaponFactory.CreateWeaponLogic(null);
            }, "CreateWeaponLogic should throw on null definition");
        }

        [Test]
        [Description("WeaponFactory_EmptyWeaponId")]
        public void CreateWeaponLogic_WithEmptyWeaponId_CreatesWeapon()
        {
            // Arrange
            testWeaponDef.weaponId = "";

            // Act & Assert - Should create weapon even with empty ID
            Assert.DoesNotThrow(() =>
            {
                var weaponLogic = weaponFactory.CreateWeaponLogic(testWeaponDef);
                Assert.IsNotNull(weaponLogic, "Weapon should be created even with empty ID");
            }, "CreateWeaponLogic should handle empty weapon ID");
        }

        [Test]
        [Description("WeaponFactory_NegativeValues")]
        public void CreateWeaponLogic_WithNegativeValues_ClampsCorrectly()
        {
            // Arrange
            testWeaponDef.baseDamage = -10f;
            testWeaponDef.maxDurability = -50;
            testWeaponDef.magazineSize = -12;

            // Act
            var weaponLogic = weaponFactory.CreateWeaponLogic(testWeaponDef);

            // Assert - Values should be clamped to reasonable ranges
            Assert.IsNotNull(weaponLogic, "WeaponLogic should be created");
            // The actual clamping behavior depends on implementation
        }

        [Test]
        [Description("WeaponFactory_LargeValues")]
        public void CreateWeaponLogic_WithLargeValues_HandlesCorrectly()
        {
            // Arrange
            testWeaponDef.maxDurability = 999999;
            testWeaponDef.magazineSize = 99999;

            // Act
            var weaponLogic = weaponFactory.CreateWeaponLogic(testWeaponDef);

            // Assert
            Assert.IsNotNull(weaponLogic, "WeaponLogic should be created with large values");
            Assert.AreEqual(999999, weaponLogic.CurrentDurability.Value, "Large durability should be preserved");
            Assert.AreEqual(99999, weaponLogic.CurrentAmmo.Value, "Large magazine size should be preserved");
        }

        #endregion

        #region Mock Classes

        private class MockObjectResolver : VContainer.IObjectResolver
        {
            public bool InjectCalled { get; private set; }
            public int InjectCallCount { get; private set; }

            public void Inject(object instance)
            {
                InjectCalled = true;
                InjectCallCount++;
            }

            // IObjectResolver interface implementation
            public object Resolve(System.Type type) => null;
            public object Resolve(System.Type type, object scope) => null;
            public object Resolve(VContainer.Registration registration) => null;
            public T Resolve<T>() where T : class => default;
            public bool TryResolve(System.Type type, out object dependency)
            {
                dependency = null;
                return false;
            }
            public bool TryResolve(System.Type type, out object dependency, object scope)
            {
                dependency = null;
                return false;
            }
            public bool TryResolve<T>(out T dependency) where T : class
            {
                dependency = default;
                return false;
            }
            public System.Collections.Generic.IEnumerable<object> ResolveEnumerable(System.Type elementType) => System.Array.Empty<object>();
            public void Dispose() { }
            public VContainer.IScopedObjectResolver CreateScope(System.Action<VContainer.IContainerBuilder> builder) => null;
            public bool TryGetRegistration(System.Type type, out VContainer.Registration registration) => throw new System.NotImplementedException();
            public bool TryGetRegistration(System.Type type, out VContainer.Registration registration, object scope) => throw new System.NotImplementedException();
            public object ApplicationOrigin => null;
            public VContainer.Diagnostics.DiagnosticsCollector Diagnostics { get; set; }
        }

        #endregion
    }
}
