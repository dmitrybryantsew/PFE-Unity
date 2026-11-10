using NUnit.Framework;
using PFE.Core.Input;
using UnityEngine;

namespace PFE.Tests.Editor.Core
{
    /// <summary>
    /// Covers <see cref="PlayerCommand"/> and <see cref="PlayerCommandBuffer"/> — the F-03 / Phase 2.1
    /// command seam. The load-bearing properties are: intent survives the FromIntent → consume
    /// round-trip unchanged (a state-location change, not a behaviour change); quantization is
    /// lossless at the endpoints and monotonic; the ring buffer evicts oldest-first and replays a
    /// tape deterministically; and the struct is value-comparable for serialization tests.
    /// </summary>
    [TestFixture]
    public class PlayerCommandTests
    {
        // ── FromIntent → field round-trip ─────────────────────────────────────

        [Test]
        public void FromIntent_PreservesAllIntent()
        {
            var command = PlayerCommand.FromIntent(
                tickIndex: 42,
                slotIndex: 3,
                sequence: 1000u,
                moveInputX: 1f,
                moveInputY: -1f,
                jumpHeld: true,
                runHeld: true,
                jumpPressed: true,
                dashPressed: false,
                dropThroughPressed: true,
                aimAngle: 135.5f);

            Assert.That(command.TickIndex, Is.EqualTo(42));
            Assert.That(command.SlotIndex, Is.EqualTo((byte)3));
            Assert.That(command.Sequence, Is.EqualTo(1000u));
            Assert.That(command.MoveXFloat, Is.EqualTo(1f).Within(0.001f));
            Assert.That(command.MoveYFloat, Is.EqualTo(-1f).Within(0.001f));
            Assert.That(command.AimAngle, Is.EqualTo(135.5f).Within(0.001f));
            Assert.That(command.JumpHeld, Is.True);
            Assert.That(command.RunHeld, Is.True);
            Assert.That(command.JumpPressed, Is.True);
            Assert.That(command.DashPressed, Is.False);
            Assert.That(command.DropThroughPressed, Is.True);
        }

        [Test]
        public void FromIntent_ZeroIntent_ProducesNoButtons()
        {
            var command = PlayerCommand.FromIntent(0, 0, 0u, 0f, 0f,
                false, false, false, false, false, 0f);

            Assert.That(command.Buttons, Is.EqualTo(PlayerCommand.Button.None));
            Assert.That(command.MoveXFloat, Is.EqualTo(0f).Within(0.001f));
            Assert.That(command.MoveYFloat, Is.EqualTo(0f).Within(0.001f));
        }

        // ── Quantization ──────────────────────────────────────────────────────

        [Test]
        public void Quantize_EndpointsAreExact()
        {
            Assert.That(PlayerCommand.Quantize(1f), Is.EqualTo((sbyte)127));
            Assert.That(PlayerCommand.Quantize(-1f), Is.EqualTo((sbyte)-127));
            Assert.That(PlayerCommand.Quantize(0f), Is.EqualTo((sbyte)0));
        }

        [Test]
        public void Quantize_ClampsOutOfRange()
        {
            Assert.That(PlayerCommand.Quantize(5f), Is.EqualTo((sbyte)127));
            Assert.That(PlayerCommand.Quantize(-5f), Is.EqualTo((sbyte)-127));
        }

        [Test]
        public void Quantize_IsMonotonic()
        {
            sbyte previous = PlayerCommand.Quantize(-1f);
            for (float v = -0.99f; v <= 1f; v += 0.01f)
            {
                sbyte current = PlayerCommand.Quantize(v);
                Assert.That(current, Is.GreaterThanOrEqualTo(previous),
                    $"quantize({v}) = {current} went below quantize of a smaller value ({previous})");
                previous = current;
            }
        }

        // ── Value equality (for serialization round-trip assertions) ──────────

        [Test]
        public void Equality_SameFields_AreEqual()
        {
            var a = PlayerCommand.FromIntent(7, 1, 5u, 0.5f, -0.25f, true, false, true, false, false, 90f);
            var b = PlayerCommand.FromIntent(7, 1, 5u, 0.5f, -0.25f, true, false, true, false, false, 90f);
            Assert.That(a, Is.EqualTo(b));
            Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
        }

        [Test]
        public void Equality_DifferentTick_NotEqual()
        {
            var a = PlayerCommand.FromIntent(7, 1, 5u, 0f, 0f, false, false, false, false, false, 0f);
            var b = PlayerCommand.FromIntent(8, 1, 5u, 0f, 0f, false, false, false, false, false, 0f);
            Assert.That(a, Is.Not.EqualTo(b));
        }

        // ── Ring buffer ───────────────────────────────────────────────────────

        [Test]
        public void Buffer_Enqueue_ThenTryGetLatest_ReturnsNewest()
        {
            var buffer = new PlayerCommandBuffer(4);
            buffer.Enqueue(PlayerCommand.FromIntent(0, 0, 0u, 0f, 0f, false, false, false, false, false, 0f));
            var newest = PlayerCommand.FromIntent(1, 0, 1u, 1f, 0f, true, false, false, false, false, 10f);
            buffer.Enqueue(newest);

            Assert.That(buffer.TryGetLatest(out PlayerCommand latest), Is.True);
            Assert.That(latest, Is.EqualTo(newest));
        }

        [Test]
        public void Buffer_WhenFull_EvictsOldestFirst()
        {
            var buffer = new PlayerCommandBuffer(3);
            for (int i = 0; i < 5; i++)
            {
                buffer.Enqueue(PlayerCommand.FromIntent(i, 0, (uint)i, 0f, 0f, false, false, false, false, false, 0f));
            }

            Assert.That(buffer.Count, Is.EqualTo(3), "capacity is bounded");
            // Ticks 0 and 1 were evicted; 2,3,4 remain.
            Assert.That(buffer.TryGetForTick(0, out _), Is.False);
            Assert.That(buffer.TryGetForTick(1, out _), Is.False);
            Assert.That(buffer.TryGetForTick(2, out _), Is.True);
            Assert.That(buffer.TryGetForTick(4, out _), Is.True);
        }

        [Test]
        public void Buffer_CopyOldestFirst_IsChronological()
        {
            var buffer = new PlayerCommandBuffer(4);
            for (int i = 0; i < 6; i++)
            {
                buffer.Enqueue(PlayerCommand.FromIntent(i, 0, (uint)i, 0f, 0f, false, false, false, false, false, 0f));
            }

            var destination = new PlayerCommand[4];
            int written = buffer.CopyOldestFirst(destination);

            Assert.That(written, Is.EqualTo(4));
            for (int i = 0; i < 4; i++)
            {
                Assert.That(destination[i].TickIndex, Is.EqualTo(2 + i),
                    "oldest-first copy must be chronological after eviction");
            }
        }

        [Test]
        public void Buffer_DiscardThrough_RemovesAckedTicks()
        {
            var buffer = new PlayerCommandBuffer(8);
            for (int i = 0; i < 6; i++)
            {
                buffer.Enqueue(PlayerCommand.FromIntent(i, 0, (uint)i, 0f, 0f, false, false, false, false, false, 0f));
            }

            buffer.DiscardThrough(3);

            Assert.That(buffer.Count, Is.EqualTo(2), "ticks 0..3 acked and dropped; 4,5 remain");
            Assert.That(buffer.TryGetForTick(3, out _), Is.False);
            Assert.That(buffer.TryGetForTick(4, out _), Is.True);
        }

        // ── Determinism: the same tape produces the same window ───────────────

        /// <summary>
        /// The core F4 / reconciliation property: two independent buffers fed the same command tape
        /// end in identical states, so a host and a predicting client replay identically.
        /// </summary>
        [Test]
        public void Buffer_SameTape_TwoConsumers_ProduceIdenticalWindows()
        {
            const int tapeLength = 200;
            var tape = new PlayerCommand[tapeLength];
            for (int i = 0; i < tapeLength; i++)
            {
                tape[i] = PlayerCommand.FromIntent(
                    i, 0, (uint)i,
                    Mathf.Sin(i * 0.1f), Mathf.Cos(i * 0.1f),
                    (i % 3) == 0, (i % 5) == 0, (i % 7) == 0, (i % 11) == 0, (i % 13) == 0,
                    i * 1.5f);
            }

            var a = new PlayerCommandBuffer(64);
            var b = new PlayerCommandBuffer(64);
            foreach (var command in tape)
            {
                a.Enqueue(command);
                b.Enqueue(command);
            }

            var windowA = new PlayerCommand[64];
            var windowB = new PlayerCommand[64];
            int countA = a.CopyOldestFirst(windowA);
            int countB = b.CopyOldestFirst(windowB);

            Assert.That(countA, Is.EqualTo(countB));
            for (int i = 0; i < countA; i++)
            {
                Assert.That(windowA[i], Is.EqualTo(windowB[i]),
                    $"window diverged at index {i}: same tape must replay identically");
            }
        }
    }
}
