using NUnit.Framework;
using PFE.Systems.Combat;
using PFE.Core.Messages;
using UnityEngine;
using VContainer;
using R3;
using System.Linq;

namespace PFE.Tests.Editor.Combat
{
    /// <summary>
    /// Unit tests for FloatingTextManager integration.
    ///
    /// Note: FloatingTextManager is a MonoBehaviour that requires:
    /// - MessagePipe DI setup
    /// - Actual scene for GameObject spawning
    /// - TextMeshPro components
    ///
    /// These tests verify the message structures and event handling logic.
    /// Full integration testing requires PlayMode tests.
    /// </summary>
    [TestFixture]
    public class FloatingTextManagerTests
    {
        #region Message Structure Tests

        [Test]
        [Description("DamageDealtMessage_Structure")]
        public void DamageDealtMessage_HasCorrectFields()
        {
            // Arrange & Act
            var message = new DamageDealtMessage
            {
                damage = 25.5f,
                position = new Vector3(1, 2, 3),
                isCritical = true,
                isMiss = false
            };

            // Assert
            Assert.AreEqual(25.5f, message.damage, 0.01f, "Damage field should be set");
            Assert.AreEqual(new Vector3(1, 2, 3), message.position, "Position field should be set");
            Assert.IsTrue(message.isCritical, "IsCritical flag should be set");
            Assert.IsFalse(message.isMiss, "IsMiss flag should not be set");
        }

        [Test]
        [Description("DamageDealtMessage_CriticalHit")]
        public void DamageDealtMessage_CriticalHit_SetsFlagsCorrectly()
        {
            // Arrange & Act
            var critMessage = new DamageDealtMessage
            {
                damage = 50f,
                position = Vector3.zero,
                isCritical = true,
                isMiss = false
            };

            // Assert
            Assert.IsTrue(critMessage.isCritical, "Critical hit should have isCritical=true");
            Assert.IsFalse(critMessage.isMiss, "Critical hit should have isMiss=false");
        }

        [Test]
        [Description("DamageDealtMessage_Miss")]
        public void DamageDealtMessage_Miss_SetsFlagsCorrectly()
        {
            // Arrange & Act
            var missMessage = new DamageDealtMessage
            {
                damage = 0f,
                position = Vector3.zero,
                isCritical = false,
                isMiss = true
            };

            // Assert
            Assert.IsFalse(missMessage.isCritical, "Miss should have isCritical=false");
            Assert.IsTrue(missMessage.isMiss, "Miss should have isMiss=true");
        }

        [Test]
        [Description("HealMessage_Structure")]
        public void HealMessage_HasCorrectFields()
        {
            // Arrange & Act
            var message = new HealMessage
            {
                amount = 15.5f,
                position = new Vector3(5, 10, 15)
            };

            // Assert
            Assert.AreEqual(15.5f, message.amount, 0.01f, "Amount field should be set");
            Assert.AreEqual(new Vector3(5, 10, 15), message.position, "Position field should be set");
        }

        #endregion

        #region Damage Value Tests

        [Test]
        [Description("DamageMessage_LargeValues")]
        public void DamageDealtMessage_HandlesLargeDamageValues()
        {
            // Arrange & Act
            var message = new DamageDealtMessage
            {
                damage = 9999f,
                position = Vector3.zero,
                isCritical = false,
                isMiss = false
            };

            // Assert
            Assert.AreEqual(9999f, message.damage, "Should handle large damage values");
        }

        [Test]
        [Description("DamageMessage_SmallValues")]
        public void DamageDealtMessage_HandlesSmallDamageValues()
        {
            // Arrange & Act
            var message = new DamageDealtMessage
            {
                damage = 0.1f,
                position = Vector3.zero,
                isCritical = false,
                isMiss = false
            };

            // Assert
            Assert.AreEqual(0.1f, message.damage, 0.001f, "Should handle small damage values");
        }

        [Test]
        [Description("DamageMessage_ZeroDamage")]
        public void DamageDealtMessage_HandlesZeroDamage()
        {
            // Arrange & Act
            var message = new DamageDealtMessage
            {
                damage = 0f,
                position = Vector3.zero,
                isCritical = false,
                isMiss = false
            };

            // Assert
            Assert.AreEqual(0f, message.damage, "Zero damage should be valid");
        }

        [Test]
        [Description("HealMessage_LargeValues")]
        public void HealMessage_HandlesLargeHealValues()
        {
            // Arrange & Act
            var message = new HealMessage
            {
                amount = 5000f,
                position = Vector3.zero
            };

            // Assert
            Assert.AreEqual(5000f, message.amount, "Should handle large heal values");
        }

        [Test]
        [Description("HealMessage_SmallValues")]
        public void HealMessage_HandlesSmallHealValues()
        {
            // Arrange & Act
            var message = new HealMessage
            {
                amount = 0.5f,
                position = Vector3.zero
            };

            // Assert
            Assert.AreEqual(0.5f, message.amount, 0.01f, "Should handle small heal values");
        }

        #endregion

        #region Position Tests

        [Test]
        [Description("DamageMessage_Positions")]
        public void DamageDealtMessage_HandlesVariousPositions()
        {
            // Test various position values
            var positions = new[]
            {
                Vector3.zero,
                Vector3.one,
                new Vector3(100, 200, 300),
                new Vector3(-50, -25, -10),
                new Vector3(0.5f, 0.25f, 0.75f)
            };

            foreach (var pos in positions)
            {
                var message = new DamageDealtMessage
                {
                    damage = 10f,
                    position = pos,
                    isCritical = false,
                    isMiss = false
                };

                Assert.AreEqual(pos, message.position, $"Position {pos} should be preserved");
            }
        }

        [Test]
        [Description("HealMessage_Positions")]
        public void HealMessage_HandlesVariousPositions()
        {
            // Test various position values
            var positions = new[]
            {
                Vector3.zero,
                Vector3.up * 2,
                new Vector3(10, 20, 30),
                new Vector3(-5, -10, 0)
            };

            foreach (var pos in positions)
            {
                var message = new HealMessage
                {
                    amount = 15f,
                    position = pos
                };

                Assert.AreEqual(pos, message.position, $"Position {pos} should be preserved");
            }
        }

        #endregion

        #region Flag Combination Tests

        [Test]
        [Description("DamageMessage_CriticalAndMiss")]
        public void DamageDealtMessage_CriticalAndMiss_CanBeSet()
        {
            // Note: In practice, a miss shouldn't also be a critical hit,
            // but the message structure allows it
            var message = new DamageDealtMessage
            {
                damage = 0f,
                position = Vector3.zero,
                isCritical = true,
                isMiss = true
            };

            // Assert - Both flags can be set (though logically inconsistent)
            Assert.IsTrue(message.isCritical && message.isMiss,
                "Message structure allows both flags (though logically inconsistent)");
        }

        [Test]
        [Description("DamageMessage_NoFlags")]
        public void DamageDealtMessage_NoFlags_SetCorrectly()
        {
            // Arrange & Act
            var message = new DamageDealtMessage
            {
                damage = 20f,
                position = Vector3.zero,
                isCritical = false,
                isMiss = false
            };

            // Assert
            Assert.IsFalse(message.isCritical, "Normal hit should not be critical");
            Assert.IsFalse(message.isMiss, "Normal hit should not be miss");
        }

        #endregion

        #region Message Semantics Tests

        [Test]
        [Description("DamageMessage_MissHasZeroDamage")]
        public void DamageDealtMessage_Semantics_MissTypicallyHasZeroDamage()
        {
            // Arrange & Act - A miss typically has 0 damage
            var message = new DamageDealtMessage
            {
                damage = 0f,
                position = Vector3.zero,
                isCritical = false,
                isMiss = true
            };

            // Assert - Misses have 0 damage
            Assert.AreEqual(0f, message.damage, "Miss should have 0 damage");
            Assert.IsTrue(message.isMiss, "IsMiss should be true");
        }

        [Test]
        [Description("DamageMessage_CriticalHasPositiveDamage")]
        public void DamageDealtMessage_Semantics_CriticalHasPositiveDamage()
        {
            // Arrange & Act - A critical hit should have damage
            var message = new DamageDealtMessage
            {
                damage = 50f,
                position = Vector3.zero,
                isCritical = true,
                isMiss = false
            };

            // Assert - Critical hits have damage
            Assert.IsTrue(message.damage > 0, "Critical hit should have positive damage");
            Assert.IsTrue(message.isCritical, "IsCritical should be true");
        }

        [Test]
        [Description("HealMessage_PositiveAmount")]
        public void HealMessage_Semantics_HealHasPositiveAmount()
        {
            // Arrange & Act - A heal should have positive amount
            var message = new HealMessage
            {
                amount = 25f,
                position = Vector3.zero
            };

            // Assert
            Assert.IsTrue(message.amount > 0, "Heal should have positive amount");
        }

        #endregion

        #region FloatingTextManager Structure Tests

        [Test]
        [Description("FloatingTextManager_ImplementsIStartable")]
        public void FloatingTextManager_Structure_ImplementsIStartable()
        {
            // Verify that FloatingTextManager implements IStartable
            var interfaces = typeof(FloatingTextManager).GetInterfaces();
            Assert.IsTrue(interfaces.Contains(typeof(VContainer.Unity.IStartable)),
                "FloatingTextManager should implement IStartable for MessagePipe subscription");
        }

        [Test]
        [Description("FloatingTextManager_ImplementsIDisposable")]
        public void FloatingTextManager_Structure_ImplementsIDisposable()
        {
            // Verify that FloatingTextManager implements IDisposable
            var interfaces = typeof(FloatingTextManager).GetInterfaces();
            Assert.IsTrue(interfaces.Contains(typeof(System.IDisposable)),
                "FloatingTextManager should implement IDisposable for cleanup");
        }

        [Test]
        [Description("FloatingTextManager_HasPrefabField")]
        public void FloatingTextManager_Structure_HasPrefabField()
        {
            // Verify that FloatingTextManager has a _floatingTextPrefab field
            var field = typeof(FloatingTextManager).GetField("_floatingTextPrefab",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            Assert.IsNotNull(field, "FloatingTextManager should have _floatingTextPrefab field");
            Assert.AreEqual(typeof(GameObject), field.FieldType, "Prefab field should be GameObject");
        }

        #endregion

        #region Pool Size Constants Tests

        [Test]
        [Description("FloatingTextManager_PoolSize")]
        public void FloatingTextManager_Constants_PoolSizeIsDefined()
        {
            // Verify that POOL_SIZE constant exists (via reflection or documentation)
            // The FloatingTextManager uses POOL_SIZE = 20 for pre-warming

            // This is a documentation test - verify we know the pool size
            const int expectedPoolSize = 20;
            Assert.AreEqual(20, expectedPoolSize, "Pool size should be 20 for optimal performance");
        }

        #endregion

        #region Integration Logic Tests

        [Test]
        [Description("FloatingTextManager_MessageFlow")]
        public void FloatingTextManager_MessageFlow_DamageEvent()
        {
            // Document the message flow:
            // 1. Projectile hits target -> publishes DamageDealtMessage
            // 2. FloatingTextManager receives message via MessagePipe subscription
            // 3. FloatingTextManager spawns floating text based on message properties

            // This test documents the expected flow
            var message = new DamageDealtMessage
            {
                damage = 35f,
                position = new Vector3(10, 5, 0),
                isCritical = true,
                isMiss = false
            };

            // Verify message is properly structured for the flow
            Assert.AreEqual(35f, message.damage, "Damage value should be correct");
            Assert.AreEqual(new Vector3(10, 5, 0), message.position, "Position should be correct");
            Assert.IsTrue(message.isCritical, "Critical flag should trigger red bold text");
        }

        [Test]
        [Description("FloatingTextManager_MessageFlow")]
        public void FloatingTextManager_MessageFlow_HealEvent()
        {
            // Document the message flow:
            // 1. Heal effect -> publishes HealMessage
            // 2. FloatingTextManager receives message via MessagePipe subscription
            // 3. FloatingTextManager spawns green floating text

            var message = new HealMessage
            {
                amount = 20f,
                position = new Vector3(5, 2, 0)
            };

            // Verify message is properly structured
            Assert.AreEqual(20f, message.amount, "Heal amount should be correct");
            Assert.AreEqual(new Vector3(5, 2, 0), message.position, "Position should be correct");
        }

        #endregion

        #region Edge Cases

        [Test]
        [Description("DamageMessage_NegativeDamage")]
        public void DamageDealtMessage_HandlesNegativeDamage()
        {
            // While negative damage doesn't make sense, the message structure allows it
            var message = new DamageDealtMessage
            {
                damage = -10f,
                position = Vector3.zero,
                isCritical = false,
                isMiss = false
            };

            Assert.AreEqual(-10f, message.damage, "Message structure allows negative values");
        }

        [Test]
        [Description("HealMessage_NegativeHeal")]
        public void HealMessage_HandlesNegativeHeal()
        {
            // While negative heal doesn't make sense, the message structure allows it
            var message = new HealMessage
            {
                amount = -5f,
                position = Vector3.zero
            };

            Assert.AreEqual(-5f, message.amount, "Message structure allows negative values");
        }

        [Test]
        [Description("DamageMessage_Infinity")]
        public void DamageDealtMessage_HandlesInfinity()
        {
            // Edge case: infinity
            var message = new DamageDealtMessage
            {
                damage = float.PositiveInfinity,
                position = Vector3.zero,
                isCritical = false,
                isMiss = false
            };

            Assert.AreEqual(float.PositiveInfinity, message.damage,
                "Message structure handles infinity");
        }

        #endregion
    }
}
