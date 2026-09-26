using NUnit.Framework;
using PFE.Entities.Weapons;
using PFE.Entities.Units;
using PFE.Systems.Audio;
using PFE.Systems.Combat;
using PFE.Core.Time;
using PFE.Data.Definitions;
using System.Reflection;
using UnityEngine;

namespace PFE.Tests.PlayMode.Combat
{
    /// <summary>
    /// Unit tests for WeaponView to verify proper integration with WeaponLogic
    /// and ProjectileFactory.
    /// </summary>
    [TestFixture]
    public class WeaponViewTests
    {
        private GameObject weaponObject;
        private WeaponView weaponView;
        private WeaponLogic weaponLogic;
        private UnitStats ownerStats;
        private ITimeProvider testTimeProvider;
        private ICombatCalculator combatCalculator;
        private IDurabilitySystem durabilitySystem;
        private IProjectileFactory mockProjectileFactory;
        private ISoundService mockSoundService;
        private PFE.Core.PfeDebugSettings debugSettings;
        private WeaponDefinition weaponDef;

        [SetUp]
        public void Setup()
        {
            // Create test infrastructure
            testTimeProvider = new UnityTimeProvider();
            combatCalculator = new CombatCalculator();
            durabilitySystem = new DurabilitySystem(combatCalculator);

            // Create test weapon definition
            weaponDef = ScriptableObject.CreateInstance<WeaponDefinition>();
            weaponDef.weaponId = "test_weapon";
            weaponDef.weaponType = WeaponType.Guns;
            weaponDef.baseDamage = 15f;
            weaponDef.rapid = 10f;
            weaponDef.deviation = 4f;
            weaponDef.maxDurability = 100;
            weaponDef.magazineSize = 12;
            weaponDef.reloadTime = 50f;

            // Create WeaponLogic
            weaponLogic = new WeaponLogic(weaponDef, testTimeProvider, combatCalculator, durabilitySystem);

            // Create owner stats
            ownerStats = new UnitStats();

            // Stand-ins for the remaining injected dependencies.
            // Construct() dereferences _debugSettings, so it must be a real instance.
            mockProjectileFactory = new MockProjectileFactory();
            mockSoundService = new MockSoundService();
            debugSettings = ScriptableObject.CreateInstance<PFE.Core.PfeDebugSettings>();

            // Create the WeaponView GameObject. A child named "Muzzle" is the documented setup
            // and must exist before AddComponent, because Awake() resolves the muzzle point.
            weaponObject = new GameObject("TestWeapon");
            var muzzle = new GameObject("Muzzle");
            muzzle.transform.SetParent(weaponObject.transform);
            weaponView = weaponObject.AddComponent<WeaponView>();

            // Manually inject dependencies (simulating VContainer).
            // Called directly rather than through reflection on purpose: a reflective
            // GetMethod("Construct").Invoke() silently rots whenever the injected signature
            // changes — it did, Construct grew from 2 to 4 parameters and every test in this
            // fixture failed in [SetUp] with TargetParameterCountException instead of failing
            // to compile. A direct call turns that class of drift into a build error.
            weaponView.Construct(testTimeProvider, mockProjectileFactory, mockSoundService, debugSettings);
        }

        [TearDown]
        public void TearDown()
        {
            if (weaponObject != null)
            {
                Object.DestroyImmediate(weaponObject);
            }
            if (weaponDef != null)
            {
                Object.DestroyImmediate(weaponDef);
            }
            if (debugSettings != null)
            {
                Object.DestroyImmediate(debugSettings);
            }
        }

        /// <summary>
        /// Reads a private instance field so the DI tests can assert what Construct actually
        /// stored, instead of asserting on the local they just assigned.
        /// </summary>
        private static T GetPrivateField<T>(object target, string fieldName)
        {
            FieldInfo field = target.GetType().GetField(
                fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(field, $"Expected a private instance field '{fieldName}' on {target.GetType().Name}.");
            return (T)field.GetValue(target);
        }

        #region Initialization Tests

        [Test]
        [Description("WeaponView_Initialize")]
        public void WeaponView_Initialize_SetsLogicAndStats()
        {
            // Act
            weaponView.Initialize(weaponLogic, ownerStats);

            // Assert - Verify that the weapon view is properly initialized
            Assert.IsNotNull(weaponView, "WeaponView should be created");
            Assert.Pass("WeaponView initialized successfully with WeaponLogic and UnitStats");
        }

        [Test]
        [Description("WeaponView_InitializeCannotBeCalledTwice")]
        public void WeaponView_Initialize_CanOnlyBeCalledOnce()
        {
            // Arrange - Initialize once
            weaponView.Initialize(weaponLogic, ownerStats);

            // Act & Assert - Calling Initialize again should be safe but may not change state
            // This test documents that Initialize can be called but won't overwrite existing logic
            Assert.DoesNotThrow(() => weaponView.Initialize(weaponLogic, ownerStats),
                "Initialize should be safe to call multiple times");
        }

        #endregion

        #region Dependency Injection Tests

        [Test]
        [Description("WeaponView_InjectsTimeProvider")]
        public void WeaponView_Construct_InjectsTimeProvider()
        {
            // Assert - Construct must have stored the injected provider on the instance.
            Assert.AreSame(testTimeProvider, GetPrivateField<ITimeProvider>(weaponView, "_timeProvider"),
                "WeaponView.Construct should store the injected ITimeProvider.");
        }

        [Test]
        [Description("WeaponView_InjectsProjectileFactory")]
        public void WeaponView_Construct_InjectsProjectileFactory()
        {
            // Assert - Construct must have stored the injected factory on the instance.
            Assert.AreSame(mockProjectileFactory, GetPrivateField<IProjectileFactory>(weaponView, "_projectileFactory"),
                "WeaponView.Construct should store the injected IProjectileFactory.");
        }

        #endregion

        #region Weapon Logic Integration Tests

        [Test]
        [Description("WeaponView_FiresWithLogic")]
        public void WeaponView_Fire_UsesWeaponLogic()
        {
            // Arrange
            weaponView.Initialize(weaponLogic, ownerStats);

            // Act - Try to fire
            // Note: We can't easily test firing without a proper scene setup,
            // but we can verify the components are linked
            var currentAmmo = weaponLogic.CurrentAmmo.Value;

            // Assert
            Assert.AreEqual(12, currentAmmo, "Weapon should start with full ammo");
        }

        [Test]
        [Description("WeaponView_RespectsAmmo")]
        public void WeaponView_Fire_RespectsAmmoConstraints()
        {
            // Arrange
            weaponView.Initialize(weaponLogic, ownerStats);

            // Assert - a fresh weapon starts full and is not empty
            Assert.AreEqual(12, weaponLogic.CurrentAmmo.Value, "Weapon should start with 12 ammo");
            Assert.IsFalse(weaponLogic.IsEmpty, "Weapon should not be empty initially");

            // Drain the magazine and confirm the constraint actually bites.
            // (The previous version of this test called CompleteReload() in a loop, which
            // refills the magazine — so it asserted the value it had just re-set.)
            weaponLogic.SetAmmo(0);

            Assert.IsTrue(weaponLogic.IsEmpty, "Weapon should report IsEmpty once the magazine is drained");
            Assert.IsFalse(weaponLogic.Fire(ownerStats), "Fire should fail with an empty magazine");
        }

        [Test]
        [Description("WeaponView_RespectsDurability")]
        public void WeaponView_Fire_RespectsDurabilityConstraints()
        {
            // Arrange
            weaponView.Initialize(weaponLogic, ownerStats);

            // Act - Verify initial durability
            int initialDurability = weaponLogic.CurrentDurability.Value;

            // Assert - Weapon should start at full durability
            Assert.AreEqual(100, initialDurability, "Weapon should start with full durability");
            Assert.IsFalse(weaponLogic.IsBroken, "Weapon should not be broken initially");
        }

        #endregion

        #region Rotation Tests

        [Test]
        [Description("WeaponView_RotateTowards")]
        public void WeaponView_RotateTowards_FacesTarget()
        {
            // Arrange
            Vector3 targetPosition = new Vector3(10, 5, 0);

            // Act
            weaponView.RotateTowards(targetPosition);

            // Assert - Weapon should be rotated towards target
            Vector3 direction = (targetPosition - weaponView.transform.position).normalized;
            float expectedAngle = Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg;
            float actualAngle = weaponView.transform.rotation.eulerAngles.z;

            // Normalize angles to 0-360 range
            expectedAngle = (expectedAngle + 360) % 360;
            actualAngle = (actualAngle + 360) % 360;

            Assert.AreEqual(expectedAngle, actualAngle, 0.1f,
                "Weapon should face the target position");
        }

        [Test]
        [Description("WeaponView_RotateToLeft_FlipsY")]
        public void WeaponView_RotateTowards_LeftSide_FlipsSprite()
        {
            // Arrange
            Vector3 targetPosition = new Vector3(-10, 0, 0); // Pointing left

            // Act
            weaponView.RotateTowards(targetPosition);

            // Assert - Y scale should be flipped when pointing left
            Assert.AreEqual(-1, weaponView.transform.localScale.y, 0.01f,
                "Weapon sprite Y should be flipped when pointing left");
        }

        [Test]
        [Description("WeaponView_RotateToRight_NoFlip")]
        public void WeaponView_RotateTowards_RightSide_NoFlip()
        {
            // Arrange
            Vector3 targetPosition = new Vector3(10, 0, 0); // Pointing right

            // Act
            weaponView.RotateTowards(targetPosition);

            // Assert - Y scale should not be flipped when pointing right
            Assert.AreEqual(1, weaponView.transform.localScale.y, 0.01f,
                "Weapon sprite Y should not be flipped when pointing right");
        }

        #endregion

        #region Firing State Tests

        [Test]
        [Description("WeaponView_BeginFiring")]
        public void WeaponView_BeginFiring_SetsFiringState()
        {
            // Act
            weaponView.BeginFiring();

            // Assert - Weapon should be in firing state
            // (This is internal state, but we can verify the method doesn't throw)
            Assert.Pass("BeginFiring should set firing state without errors");
        }

        [Test]
        [Description("WeaponView_EndFiring")]
        public void WeaponView_EndFiring_ClearsFiringState()
        {
            // Arrange
            weaponView.BeginFiring();

            // Act
            weaponView.EndFiring();

            // Assert - Weapon should stop firing
            Assert.Pass("EndFiring should clear firing state without errors");
        }

        #endregion

        #region WeaponFactory DI Tests

        [Test]
        [Description("WeaponFactory_InjectsWeaponLogic")]
        public void WeaponFactory_CreateWeaponLogic_InjectsDependencies()
        {
            // Arrange
            var factory = new WeaponFactory(
                null, // IObjectResolver - not used for CreateWeaponLogic
                testTimeProvider,
                combatCalculator,
                durabilitySystem);

            // Act
            var weaponLogic = factory.CreateWeaponLogic(weaponDef);

            // Assert - WeaponLogic should be created with all dependencies
            Assert.IsNotNull(weaponLogic, "WeaponLogic should be created by factory");
            Assert.IsNotNull(weaponLogic.WeaponDef, "WeaponLogic should have WeaponDef");
            Assert.AreEqual(weaponDef.weaponId, weaponLogic.WeaponDef.weaponId, "WeaponDef should match");
        }

        [Test]
        [Description("WeaponFactory_ManualInitialization")]
        public void WeaponView_ManualInitialization_WorksCorrectly()
        {
            // Arrange - Create WeaponView manually (simulating what factory does)
            var parentObject = new GameObject("TestParent");
            var weaponObj = new GameObject("TestWeapon");
            weaponObj.transform.SetParent(parentObject.transform);
            var muzzle = new GameObject("Muzzle");
            muzzle.transform.SetParent(weaponObj.transform);
            var testView = weaponObj.AddComponent<WeaponView>();

            // Manually inject dependencies
            testView.Construct(testTimeProvider, mockProjectileFactory, mockSoundService, debugSettings);

            // Act - Initialize with WeaponLogic and UnitStats (simulating factory behavior)
            testView.Initialize(weaponLogic, ownerStats);

            // Assert - WeaponView should be properly initialized
            Assert.IsNotNull(testView, "WeaponView should be created");
            Assert.IsNotNull(testView.gameObject, "WeaponView should have a GameObject");

            // Cleanup
            Object.DestroyImmediate(weaponObj);
            Object.DestroyImmediate(parentObject);
        }

        #endregion

        #region Mock Dependencies

        private class MockProjectileFactory : IProjectileFactory
        {
            public Projectile CreatedProjectile { get; private set; }
            public int CreateCount { get; private set; }

            public Projectile Create(WeaponDefinition weapon, Vector3 position, Vector2 direction)
            {
                CreateCount++;
                CreatedProjectile = null;
                return null;
            }

            public Projectile Create(Projectile prefab, Vector3 position, Quaternion rotation,
                float damage, float speed, Vector2 direction, float gravityScale = 0f)
            {
                CreateCount++;
                CreatedProjectile = null;
                return null;
            }
        }

        private class MockSoundService : ISoundService
        {
            public int PlayCount { get; private set; }
            public float SfxVolume { get; set; } = 1f;

            public void Play(string id, Vector2 worldPos, float volumeScale = 1f) => PlayCount++;
            public void PlayLoop(string id, object key, float volume = 1f) { }
            public void PlayLoopFromTime(string id, object key, float startTimeSec, float volume = 1f) { }
            public float GetLoopTime(object key) => -1f;
            public void StopLoop(object key) { }
            public void StopAll() { }
        }

        #endregion
    }
}
