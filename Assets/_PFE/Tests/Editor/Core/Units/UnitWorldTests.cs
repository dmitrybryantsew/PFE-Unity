using System;
using NUnit.Framework;
using PFE.Sim.Units;

namespace PFE.Tests.Editor.Core.Units
{
    /// <summary>
    /// Thread J, Phase 2 — the <see cref="UnitWorld"/> slot store the stealth/noise/suspicion
    /// record migrated into.
    ///
    /// <para><b>What these tests pin.</b> The handle contract that makes a batch store safe to
    /// hand to systems later: acquire/release lifecycle, generation-bumped slot reuse, and the
    /// fail-loud stale/foreign access checks — a stale read here would be another unit's stealth
    /// numbers surfacing as "the AI forgot the player", so it must throw, not guess. Also the
    /// defaults: a fresh slot must be exactly what the controller's field initialisers used to
    /// produce, or the migration silently changes spawn-state behaviour.</para>
    /// </summary>
    [TestFixture]
    public sealed class UnitWorldTests
    {
        [Test]
        public void Acquire_GrowsLiveCount_AndFreshSlotMatchesTheOldFieldDefaults()
        {
            var world = new UnitWorld();

            UnitHandle first = world.Acquire();
            UnitHandle second = world.Acquire();

            Assert.That(world.LiveCount, Is.EqualTo(2));
            Assert.That(first, Is.Not.EqualTo(second));
            Assert.IsTrue(first.IsValid && second.IsValid);

            ref UnitStealthData data = ref world.Access(first);
            Assert.That(data.Noise, Is.EqualTo(0));
            Assert.That(data.NoiseRippleTicks, Is.EqualTo(0));
            Assert.That(data.NoiseWasGrounded, Is.True,
                "the controller's old `_noiseWasGrounded = true` initializer must survive the move — " +
                "a unit is born on the ground, and the first landing edge depends on it");
            Assert.That(data.NoiseFallSpeedPixelsPerFrame, Is.EqualTo(0f));
            Assert.That(data.Suspicion.Value, Is.EqualTo(0f));
            Assert.That(data.Suspicion.HoldTicks, Is.EqualTo(0));
            Assert.That(data.Stealth, Is.EqualTo(StealthMath.StealthState.Default));
        }

        [Test]
        public void Access_WritesAreIsolatedPerSlot()
        {
            var world = new UnitWorld();
            UnitHandle a = world.Acquire();
            UnitHandle b = world.Acquire();

            world.Access(a).Noise = 300;
            world.Access(a).Stealth = new StealthMath.StealthState { Demask = 150f };
            world.Access(b).Suspicion = new NoiseMath.Observation { Value = 7f, HoldTicks = 30 };

            Assert.That(world.Access(a).Noise, Is.EqualTo(300));
            Assert.That(world.Access(a).Suspicion.Value, Is.EqualTo(0f), "slot b's write must not leak into slot a");
            Assert.That(world.Access(b).Stealth.Demask, Is.EqualTo(0f));
            Assert.That(world.Access(b).Suspicion.Value, Is.EqualTo(7f));
        }

        [Test]
        public void Release_ReusesTheSlotWithABumpedGeneration_AndResetsItsData()
        {
            var world = new UnitWorld();
            UnitHandle first = world.Acquire();
            world.Access(first).Noise = 300;
            world.Access(first).Stealth = new StealthMath.StealthState { Demask = 200f };

            UnitHandle copy = first;
            world.Release(ref first);

            Assert.That(world.LiveCount, Is.EqualTo(0));
            Assert.IsFalse(first.IsValid, "release clears the caller's handle — a second release is a no-op, not a throw");

            UnitHandle second = world.Acquire();
            Assert.That(second.Slot, Is.EqualTo(copy.Slot), "the free list is used before fresh capacity");
            Assert.That(second.Generation, Is.EqualTo(copy.Generation + 1), "reissue must bump the generation");
            Assert.That(world.Access(second).Noise, Is.EqualTo(0), "and the reissued slot starts from Default");
            Assert.That(world.Access(second).Stealth.Demask, Is.EqualTo(0f));
        }

        [Test]
        public void Access_WithAStaleHandle_ThrowsInsteadOfReadingAnotherUnitsSlot()
        {
            var world = new UnitWorld();
            UnitHandle old = world.Acquire();
            world.Release(ref old);
            world.Acquire(); // reissues the same slot at the next generation

            Assert.Throws<InvalidOperationException>(() =>
            {
                // ReSharper disable once UnusedVariable — the ref is the assertion's subject.
                ref UnitStealthData data = ref world.Access(old);
                int read = data.Noise;
            });
        }

        [Test]
        public void Access_WithANeverIssuedSlot_ThrowsAsForeign()
        {
            var issuer = new UnitWorld();
            UnitHandle handle = issuer.Acquire();

            var other = new UnitWorld();
            Assert.Throws<InvalidOperationException>(() =>
            {
                ref UnitStealthData data = ref other.Access(handle);
                int read = data.Noise;
            });
        }

        [Test]
        public void Access_WithTheInvalidHandle_Throws()
        {
            var world = new UnitWorld();
            UnitHandle handle = UnitHandle.Invalid;

            Assert.Throws<InvalidOperationException>(() =>
            {
                ref UnitStealthData data = ref world.Access(handle);
                int read = data.Noise;
            });
        }

        [Test]
        public void Release_IsIdempotentOnceTheHandleIsCleared()
        {
            var world = new UnitWorld();
            UnitHandle handle = world.Acquire();

            world.Release(ref handle);
            Assert.DoesNotThrow(() => world.Release(ref handle), "the cleared handle makes the second call a no-op");
            Assert.That(world.LiveCount, Is.EqualTo(0));
        }

        [Test]
        public void TryGet_NonThrowingRead_MirrorsAccessForLiveHandlesOnly()
        {
            var world = new UnitWorld();
            UnitHandle handle = world.Acquire();
            world.Access(handle).Noise = 42;

            Assert.IsTrue(world.TryGet(handle, out UnitStealthData data));
            Assert.That(data.Noise, Is.EqualTo(42));

            world.Release(ref handle);
            Assert.IsFalse(world.TryGet(handle, out UnitStealthData stale), "a released handle is not live");
            Assert.That(stale.Noise, Is.EqualTo(0));
        }

        [Test]
        public void Acquire_GrowsTheArraysGeometrically()
        {
            var world = new UnitWorld(2);
            world.Acquire();
            world.Acquire();

            Assert.That(world.Capacity, Is.EqualTo(2));
            UnitHandle third = world.Acquire();
            Assert.That(world.Capacity, Is.EqualTo(4), "growth doubles, matching every other array-backed store");
            Assert.IsTrue(world.TryGet(third, out _));
            Assert.That(world.LiveCount, Is.EqualTo(3));
        }

        [Test]
        public void Clear_ResetsEverything_AndMakesEveryPriorHandleStale()
        {
            var world = new UnitWorld();
            UnitHandle handle = world.Acquire();
            world.Access(handle).Noise = 300;

            world.Clear();

            Assert.That(world.LiveCount, Is.EqualTo(0));
            Assert.Throws<InvalidOperationException>(() =>
            {
                ref UnitStealthData data = ref world.Access(handle);
                int read = data.Noise;
            });

            UnitHandle fresh = world.Acquire();
            Assert.That(fresh.Slot, Is.EqualTo(0), "after a clear the high-water mark restarts");
            Assert.That(world.Access(fresh).Noise, Is.EqualTo(0));
        }
    }
}
