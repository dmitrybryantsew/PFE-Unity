using NUnit.Framework;
using UnityEngine;
using VContainer;
using MessagePipe;
using R3;
using PFE.Core.Messages;
using System;

namespace PFE.Tests.Combat
{
    /// <summary>
    /// Tests for the MessagePipe-based damage event system.
    /// Verifies that damage events are properly published and received.
    /// </summary>
    [TestFixture]
    public class DamageEventSystemTests
    {
        private IObjectResolver _objectResolver;
        private IPublisher<DamageDealtMessage> _publisher;
        private ISubscriber<DamageDealtMessage> _subscriber;

        [SetUp]
        public void Setup()
        {
            // Create MessagePipe service provider
            var builder = new ContainerBuilder();
            // Register MessagePipe services - cast to IContainerBuilder for extension method
            IContainerBuilder containerBuilder = builder;
            containerBuilder.RegisterMessagePipe();

            _objectResolver = builder.Build();
            _publisher = _objectResolver.Resolve<IPublisher<DamageDealtMessage>>();
            _subscriber = _objectResolver.Resolve<ISubscriber<DamageDealtMessage>>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_objectResolver is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        [Test]
        public void DamageDealtMessage_ShouldHaveCorrectStructure()
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
            Assert.AreEqual(25.5f, message.damage, "Damage should be 25.5");
            Assert.AreEqual(new Vector3(1, 2, 3), message.position, "Position should match");
            Assert.IsTrue(message.isCritical, "Should be critical hit");
            Assert.IsFalse(message.isMiss, "Should not be miss");
        }

        [Test]
        public void Publisher_ShouldPublishDamageMessage()
        {
            // Arrange
            DamageDealtMessage? receivedMessage = null;
            var disposable = _subscriber.Subscribe(msg => receivedMessage = msg);

            var testMessage = new DamageDealtMessage
            {
                damage = 100f,
                position = Vector3.zero,
                isCritical = false,
                isMiss = false
            };

            // Act
            _publisher.Publish(testMessage);

            // Assert
            Assert.IsTrue(receivedMessage.HasValue, "Message should be received");
            Assert.AreEqual(100f, receivedMessage.Value.damage, "Damage should match");
        }

        [Test]
        public void Subscriber_ShouldReceiveMultipleMessages()
        {
            // Arrange
            int messageCount = 0;
            float totalDamage = 0f;

            var disposable = _subscriber.Subscribe(msg =>
            {
                messageCount++;
                totalDamage += msg.damage;
            });

            // Act
            _publisher.Publish(new DamageDealtMessage { damage = 10f, position = Vector3.zero });
            _publisher.Publish(new DamageDealtMessage { damage = 20f, position = Vector3.zero });
            _publisher.Publish(new DamageDealtMessage { damage = 30f, position = Vector3.zero });

            // Assert
            Assert.AreEqual(3, messageCount, "Should receive 3 messages");
            Assert.AreEqual(60f, totalDamage, "Total damage should be 60");
        }

        [Test]
        public void Subscriber_ShouldFilterCriticalHits()
        {
            // Arrange
            int critCount = 0;
            int normalCount = 0;

            var disposable = _subscriber.Subscribe(msg =>
            {
                if (msg.isCritical)
                    critCount++;
                else
                    normalCount++;
            });

            // Act
            _publisher.Publish(new DamageDealtMessage { damage = 10f, position = Vector3.zero, isCritical = true });
            _publisher.Publish(new DamageDealtMessage { damage = 20f, position = Vector3.zero, isCritical = false });
            _publisher.Publish(new DamageDealtMessage { damage = 30f, position = Vector3.zero, isCritical = true });

            // Assert
            Assert.AreEqual(2, critCount, "Should have 2 critical hits");
            Assert.AreEqual(1, normalCount, "Should have 1 normal hit");
        }

        [Test]
        public void Subscriber_ShouldHandleMissMessages()
        {
            // Arrange
            bool missReceived = false;

            var disposable = _subscriber.Subscribe(msg =>
            {
                if (msg.isMiss)
                    missReceived = true;
            });

            // Act
            _publisher.Publish(new DamageDealtMessage
            {
                damage = 0f,
                position = Vector3.zero,
                isCritical = false,
                isMiss = true
            });

            // Assert
            Assert.IsTrue(missReceived, "Miss message should be received");
        }

        [Test]
        public void MultipleSubscribers_ShouldAllReceiveMessages()
        {
            // Arrange
            int subscriber1Count = 0;
            int subscriber2Count = 0;

            var disposable1 = _subscriber.Subscribe(msg => subscriber1Count++);
            var disposable2 = _subscriber.Subscribe(msg => subscriber2Count++);

            // Act
            _publisher.Publish(new DamageDealtMessage { damage = 10f, position = Vector3.zero });
            _publisher.Publish(new DamageDealtMessage { damage = 20f, position = Vector3.zero });

            // Assert
            Assert.AreEqual(2, subscriber1Count, "Subscriber 1 should receive 2 messages");
            Assert.AreEqual(2, subscriber2Count, "Subscriber 2 should receive 2 messages");
        }

        [Test]
        public void Unsubscribe_ShouldStopReceivingMessages()
        {
            // Arrange
            int messageCount = 0;

            var disposable = _subscriber.Subscribe(msg => messageCount++);

            // Act - publish first message
            _publisher.Publish(new DamageDealtMessage { damage = 10f, position = Vector3.zero });

            // Unsubscribe
            disposable.Dispose();

            // Publish second message
            _publisher.Publish(new DamageDealtMessage { damage = 20f, position = Vector3.zero });

            // Assert
            Assert.AreEqual(1, messageCount, "Should only receive 1 message after unsubscribe");
        }

        [Test]
        public void DamageEvent_Position_ShouldBeAccurate()
        {
            // Arrange
            Vector3? receivedPosition = null;

            var disposable = _subscriber.Subscribe(msg =>
            {
                receivedPosition = msg.position;
            });

            var testPosition = new Vector3(5.5f, -2.3f, 10.1f);

            // Act
            _publisher.Publish(new DamageDealtMessage
            {
                damage = 15f,
                position = testPosition,
                isCritical = false,
                isMiss = false
            });

            // Assert
            Assert.IsTrue(receivedPosition.HasValue, "Position should be received");
            Assert.AreEqual(testPosition, receivedPosition.Value, "Position should match exactly");
        }
    }
}
