using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using VContainer;
using PFE.Entities.Weapons;
using PFE.Entities.Units;
using PFE.Systems.Combat;
using PFE.Core.Pooling;
using Cysharp.Threading.Tasks;
using System.Collections;

namespace PFE.Tests.PlayMode.Combat
{
    /// <summary>
    /// Integration tests for Projectile and IDamageable system.
    /// Tests that projectiles correctly detect and damage IDamageable entities.
    ///
    /// Test coverage:
    /// - Projectile collision with IDamageable entities
    /// - Damage application through IDamageable interface
    /// - Non-damageable collision handling
    /// - Multiple projectile hits
    /// </summary>
    public class ProjectileDamageableIntegrationTests
    {
        private GameObject _projectileObj;
        private Projectile _projectile;
        private GameObject _unitObj;
        private UnitController _unitController;
        private UnitStats _unitStats;
        private GameObject _nonDamageableObj;

        [SetUp]
        public void Setup()
        {
            // Create a GameObject with Projectile for testing
            _projectileObj = new GameObject("TestProjectile");
            _projectileObj.AddComponent<Rigidbody2D>();
            _projectileObj.AddComponent<BoxCollider2D>(); // Collider for collision detection
            _projectile = _projectileObj.AddComponent<Projectile>();

            // Create a GameObject with UnitController (damageable target)
            _unitObj = new GameObject("TestUnit");
            _unitObj.AddComponent<Rigidbody2D>();
            var unitCollider = _unitObj.AddComponent<BoxCollider2D>();
            unitCollider.isTrigger = false; // Solid collider

            _unitController = _unitObj.AddComponent<UnitController>();
            _unitStats = new UnitStats(100f, 50f);

            // Use reflection to set the protected _unitStats field
            var field = typeof(UnitController).GetField("_unitStats",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            field.SetValue(_unitController, _unitStats);

            // Create a non-damageable GameObject (e.g., a wall)
            _nonDamageableObj = new GameObject("TestWall");
            _nonDamageableObj.AddComponent<Rigidbody2D>();
            var wallCollider = _nonDamageableObj.AddComponent<BoxCollider2D>();
            wallCollider.isTrigger = false;
        }

        [TearDown]
        public void TearDown()
        {
            if (_projectileObj != null) Object.DestroyImmediate(_projectileObj);
            if (_unitObj != null) Object.DestroyImmediate(_unitObj);
            if (_nonDamageableObj != null) Object.DestroyImmediate(_nonDamageableObj);
        }

        // === Projectile-Unit Collision Tests ===

        [UnityTest]
        public System.Collections.IEnumerator Projectile_CanDetectIDamageableOnCollision()
        {
            // Arrange
            float damage = 25f;
            float speed = 10f;
            Vector2 direction = Vector2.right;

            _projectile.Initialize(damage, speed, direction);
            float initialHealth = _unitController.CurrentHealth;

            // Act - Simulate collision by calling OnTriggerEnter2D directly
            var collider = _unitObj.GetComponent<Collider2D>();
            _projectile.SendMessage("OnTriggerEnter2D", collider);

            yield return null;

            // Assert
            Assert.Less(_unitController.CurrentHealth, initialHealth,
                "Projectile should reduce unit's health on collision");
        }

        [UnityTest]
        public System.Collections.IEnumerator Projectile_AppliesCorrectDamageAmount()
        {
            // Arrange
            float damage = 30f;
            float speed = 10f;
            Vector2 direction = Vector2.right;
            float expectedHealth = 70f; // 100 - 30

            _projectile.Initialize(damage, speed, direction);

            // Act
            var collider = _unitObj.GetComponent<Collider2D>();
            _projectile.SendMessage("OnTriggerEnter2D", collider);

            yield return null;

            // Assert
            Assert.AreEqual(expectedHealth, _unitController.CurrentHealth, 0.01f,
                "Projectile should apply exact damage amount to unit");
        }

        [UnityTest]
        public System.Collections.IEnumerator Projectile_CanKillUnitWithLethalDamage()
        {
            // Arrange
            float damage = 150f; // Lethal damage
            float speed = 10f;
            Vector2 direction = Vector2.right;

            _projectile.Initialize(damage, speed, direction);

            // Act
            var collider = _unitObj.GetComponent<Collider2D>();
            _projectile.SendMessage("OnTriggerEnter2D", collider);

            yield return null;

            // Assert
            Assert.IsFalse(_unitController.IsAlive,
                "Projectile should be able to kill unit with lethal damage");
            Assert.AreEqual(0f, _unitController.CurrentHealth, 0.01f,
                "Unit's health should be 0 after lethal damage");
        }

        [UnityTest]
        public System.Collections.IEnumerator Projectile_DoesNotDamageDeadUnits()
        {
            // Arrange
            float damage = 25f;
            float speed = 10f;
            Vector2 direction = Vector2.right;

            // Kill the unit first
            _unitController.TakeDamage(100f);
            float healthAfterDeath = _unitController.CurrentHealth;

            _projectile.Initialize(damage, speed, direction);

            // Act
            var collider = _unitObj.GetComponent<Collider2D>();
            _projectile.SendMessage("OnTriggerEnter2D", collider);

            yield return null;

            // Assert
            Assert.AreEqual(healthAfterDeath, _unitController.CurrentHealth, 0.01f,
                "Projectile should not damage dead units");
        }

        // === Non-Damageable Collision Tests ===

        [UnityTest]
        public System.Collections.IEnumerator Projectile_CollidesWithNonDamageable_NoHealthChange()
        {
            // Arrange
            float damage = 25f;
            float speed = 10f;
            Vector2 direction = Vector2.right;
            float initialHealth = _unitController.CurrentHealth;

            _projectile.Initialize(damage, speed, direction);

            // Act - Hit a wall instead
            var collider = _nonDamageableObj.GetComponent<Collider2D>();
            _projectile.SendMessage("OnTriggerEnter2D", collider);

            yield return null;

            // Assert - Unit's health should not change
            Assert.AreEqual(initialHealth, _unitController.CurrentHealth, 0.01f,
                "Hitting non-damageable object should not affect unit's health");
        }

        [UnityTest]
        public System.Collections.IEnumerator Projectile_CollidesWithTrigger_IgnoresCollision()
        {
            // Arrange
            GameObject triggerObj = new GameObject("Trigger");
            var triggerCollider = triggerObj.AddComponent<BoxCollider2D>();
            triggerCollider.isTrigger = true; // This is a trigger

            float damage = 25f;
            float speed = 10f;
            Vector2 direction = Vector2.right;
            float initialHealth = _unitController.CurrentHealth;

            _projectile.Initialize(damage, speed, direction);

            // Act
            _projectile.SendMessage("OnTriggerEnter2D", triggerCollider);

            yield return null;

            // Assert - Should not damage (triggers are ignored)
            Assert.AreEqual(initialHealth, _unitController.CurrentHealth, 0.01f,
                "Projectile should ignore trigger colliders");

            Object.DestroyImmediate(triggerObj);
        }

        // === Multiple Projectile Tests ===

        [UnityTest]
        public System.Collections.IEnumerator MultipleProjectiles_CanDamageSameUnit()
        {
            // Arrange
            float damagePerProjectile = 20f;
            float speed = 10f;
            Vector2 direction = Vector2.right;
            float expectedHealth = 40f; // 100 - 20 - 20 - 20

            // Act - Fire 3 projectiles
            for (int i = 0; i < 3; i++)
            {
                var projObj = new GameObject($"Projectile{i}");
                projObj.AddComponent<Rigidbody2D>();
                var proj = projObj.AddComponent<Projectile>();
                proj.Initialize(damagePerProjectile, speed, direction);

                var collider = _unitObj.GetComponent<Collider2D>();
                proj.SendMessage("OnTriggerEnter2D", collider);

                Object.DestroyImmediate(projObj);
            }

            yield return null;

            // Assert
            Assert.AreEqual(expectedHealth, _unitController.CurrentHealth, 0.01f,
                "Multiple projectiles should each apply damage to the same unit");
        }

        // === Interface Independence Tests ===

        [UnityTest]
        public System.Collections.IEnumerator IDamageable_WorksWithDifferentImplementations()
        {
            // Arrange - Create a custom damageable object
            GameObject customObj = new GameObject("CustomDamageable");
            customObj.AddComponent<BoxCollider2D>(); // Add required collider
            var customDamageable = customObj.AddComponent<CustomDamageableObject>();

            float damage = 30f;
            float speed = 10f;
            Vector2 direction = Vector2.right;

            _projectile.Initialize(damage, speed, direction);

            // Act
            var collider = customObj.GetComponent<Collider2D>();
            _projectile.SendMessage("OnTriggerEnter2D", collider);

            yield return null;

            // Assert - Custom damageable should have received damage
            Assert.AreEqual(1, customDamageable.TakeDamageCallCount,
                "Projectile should work with any IDamageable implementation");
            Assert.AreEqual(damage, customDamageable.LastDamageAmount, 0.01f,
                "Projectile should pass correct damage amount");

            Object.DestroyImmediate(customObj);
        }

        // === Helper Classes ===

        /// <summary>
        /// Test helper class that implements IDamageable for testing interface independence.
        /// </summary>
        private class CustomDamageableObject : MonoBehaviour, IDamageable
        {
            public int TakeDamageCallCount { get; private set; }
            public float LastDamageAmount { get; private set; }
            private float _health = 100f;

            public void TakeDamage(float damage)
            {
                TakeDamageCallCount++;
                LastDamageAmount = damage;
                _health = Mathf.Max(0, _health - damage);
            }

            public float CurrentHealth => _health;
            public float MaxHealth => 100f;
            public bool IsAlive => _health > 0;
        }
    }
}
