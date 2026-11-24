using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using PFE.Core;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using PFE.Sim.Units;
using PFE.Systems.Combat;
using UnityEngine;

namespace PFE.Tests.Editor.Core.Units
{
    /// <summary>
    /// Thread J, Phase 2 — the second <see cref="UnitWorld"/> domain: the corpse lifecycle record
    /// (<see cref="UnitCorpseData"/>), the store half first and then the forwarding facade
    /// <see cref="UnitController"/> became when its four <c>[Authoritative]</c> corpse fields moved
    /// into the slot beside the stealth record.
    ///
    /// <para><b>The contract under test.</b> Exactly the stealth migration's: through the public
    /// surface the world slot and the local fallback are indistinguishable — same calls, same
    /// numbers, only where the bytes sit differs. The seam both halves read is the shared
    /// <c>_stealthHandle</c>: one handle names both of a unit's domain slots, because both live
    /// and die with the same unit.</para>
    ///
    /// <para><b>Why the dissolve is driven through reflection.</b> The only public entries into
    /// the corpse state machine are a death through the damage pipeline (which needs a resolver, a
    /// stats object and an authority fixture) and <see cref="UnitController.ReviveFromCorpse"/>.
    /// The private <c>BeginDissolve</c> is the one shared setup both real arms converge on, so
    /// invoking it directly exercises every forwarder — state, countdown, preset — without the
    /// damage pipeline's baggage, exactly like the drive-lane fixtures do for ticking.</para>
    /// </summary>
    [TestFixture]
    public sealed class UnitWorldCorpseTests
    {
        private readonly List<GameObject> _spawned = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in _spawned)
            {
                if (go != null)
                {
                    UnityEngine.Object.DestroyImmediate(go);
                }
            }

            _spawned.Clear();
        }

        // ── The store: the corpse array ──────────────────────────────────────────

        [Test]
        public void Acquire_StartsTheCorpseRecordAtDefault()
        {
            var world = new UnitWorld();
            UnitHandle handle = world.Acquire();

            ref UnitCorpseData data = ref world.AccessCorpse(handle);
            Assert.That(data.State, Is.EqualTo(CorpseState.None));
            Assert.That(data.DissolveFrames, Is.EqualTo(0f));
            Assert.That(data.Tip, Is.EqualTo(DesintegrTip.None));
            Assert.That(data.LastDamageType, Is.EqualTo(DamageType.PhysicalBullet),
                "the resting value: a kill that never went through the damage pipeline dies as a " +
                "rifle shot would");
        }

        [Test]
        public void AccessCorpse_ThrowsOnAReleasedHandle()
        {
            var world = new UnitWorld();
            UnitHandle handle = world.Acquire();
            world.Release(ref handle);

            Assert.Throws<InvalidOperationException>(() => ReadState(world, handle));
        }

        [Test]
        public void TryGetCorpse_FalseOnAReleasedHandle()
        {
            var world = new UnitWorld();
            UnitHandle handle = world.Acquire();
            world.Release(ref handle);

            Assert.IsFalse(world.TryGetCorpse(handle, out _));
        }

        [Test]
        public void ReleasedSlot_ReacquiresWithADefaultCorpseRecord()
        {
            var world = new UnitWorld();
            UnitHandle first = world.Acquire();
            UnitHandle stale = first;
            world.AccessCorpse(first).State = CorpseState.Dissolving;
            world.Release(ref first);

            UnitHandle second = world.Acquire();

            Assert.IsFalse(world.TryGetCorpse(stale, out _),
                "the reissued slot carries a bumped generation, so the old handle stays stale");
            Assert.That(world.AccessCorpse(second).State, Is.EqualTo(CorpseState.None),
                "a reissued slot starts clean — the previous tenant's corpse record must not leak");
        }

        [Test]
        public void CorpseRecords_AreIndependentAcrossSlots()
        {
            var world = new UnitWorld();
            UnitHandle a = world.Acquire();
            UnitHandle b = world.Acquire();

            world.AccessCorpse(a).Tip = DesintegrTip.Burn;

            Assert.That(world.AccessCorpse(a).Tip, Is.EqualTo(DesintegrTip.Burn));
            Assert.That(world.AccessCorpse(b).Tip, Is.EqualTo(DesintegrTip.None),
                "each slot owns its record; the two tenants cannot see each other's dissolve");
        }

        [Test]
        public void Clear_InvalidatesEveryCorpseRecord()
        {
            var world = new UnitWorld();
            UnitHandle handle = world.Acquire();
            world.AccessCorpse(handle).State = CorpseState.Corpse;

            world.Clear();

            Assert.IsFalse(world.TryGetCorpse(handle, out _));
        }

        private static CorpseState ReadState(UnitWorld world, in UnitHandle handle) =>
            world.AccessCorpse(handle).State;

        // ── The facade: UnitController forwards ──────────────────────────────────

        private sealed class SpyUnit : UnitController
        {
            protected override void StepUnit()
            {
            }
        }

        private static SimClock MakeClock(out SimLoop loop)
        {
            var clock = new SimClock(30);
            loop = new SimLoop(clock, null);
            return clock;
        }

        private SpyUnit MakeUnit(SimClock clock, SimLoop loop)
        {
            var go = new GameObject("SpyUnit");
            _spawned.Add(go);
            SpyUnit unit = go.AddComponent<SpyUnit>();
            unit.AttachSimulation(clock, loop);
            return unit;
        }

        private static UnitHandle StealthHandleOf(UnitController unit)
        {
            FieldInfo field = typeof(UnitController).GetField(
                "_stealthHandle", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, "the facade's handle field is the seam this test reads");
            return (UnitHandle)field.GetValue(unit);
        }

        private static UnitCorpseData LocalCorpseOf(UnitController unit)
        {
            FieldInfo field = typeof(UnitController).GetField(
                "_localCorpse", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, "the local fallback copy is the seam the no-world path is read through");
            return (UnitCorpseData)field.GetValue(unit);
        }

        private static readonly MethodInfo BeginDissolveMethod = typeof(UnitController).GetMethod(
            "BeginDissolve", BindingFlags.NonPublic | BindingFlags.Instance);

        private static void BeginDissolve(UnitController unit, DesintegrTip tip)
        {
            Assert.IsNotNull(BeginDissolveMethod,
                "the private dissolve entry point is the seam both real arms converge on");
            BeginDissolveMethod.Invoke(unit, new object[] { tip });
        }

        [Test]
        public void WorldPath_BeginDissolve_LandsEveryPieceInTheSlot()
        {
            SimClock clock = MakeClock(out SimLoop loop);
            var world = new UnitWorld();
            loop.UnitWorld = world;
            SpyUnit unit = MakeUnit(clock, loop);

            BeginDissolve(unit, DesintegrTip.Burn);

            Assert.IsTrue(unit.IsCorpse);
            Assert.IsFalse(unit.IsCorpseRemoved);

            UnitHandle handle = StealthHandleOf(unit);
            Assert.IsTrue(world.TryGetCorpse(handle, out UnitCorpseData data),
                "the shared handle must name the corpse slot too");
            Assert.That(data.State, Is.EqualTo(CorpseState.Dissolving));
            Assert.That(data.Tip, Is.EqualTo(DesintegrTip.Burn));
            Assert.That(data.DissolveFrames, Is.EqualTo(0f));

            unit.TickCorpse();

            Assert.IsTrue(world.TryGetCorpse(handle, out data));
            Assert.That(data.DissolveFrames, Is.EqualTo(clock.StepScale),
                "one tick of the 30 Hz clock advances the dissolve by exactly one canonical frame");
        }

        [Test]
        public void WorldPath_FullDissolve_RemovesTheBody_AndTheSlotMirrorsIt()
        {
            SimClock clock = MakeClock(out SimLoop loop);
            var world = new UnitWorld();
            loop.UnitWorld = world;
            SpyUnit unit = MakeUnit(clock, loop);

            BeginDissolve(unit, DesintegrTip.Burn);
            for (int i = 0; i < CorpseDamageMath.DissolveFrames; i++)
            {
                unit.TickCorpse();
            }

            Assert.IsTrue(unit.IsCorpseRemoved);
            Assert.IsFalse(unit.IsCorpse);

            UnitHandle handle = StealthHandleOf(unit);
            Assert.IsTrue(world.TryGetCorpse(handle, out UnitCorpseData data));
            Assert.That(data.State, Is.EqualTo(CorpseState.Removed));
            Assert.That(data.DissolveFrames, Is.EqualTo(0f), "exterminate resets the countdown");
            Assert.That(data.Tip, Is.EqualTo(DesintegrTip.Burn),
                "the preset survives extermination in a bare fixture: ReleaseDesintegrMaterial " +
                "early-returns when no material was ever created, which is exactly the old " +
                "field-coupled behaviour the facade preserves");
        }

        [Test]
        public void WorldPath_ReviveFromCorpse_ResetsTheSlot()
        {
            SimClock clock = MakeClock(out SimLoop loop);
            var world = new UnitWorld();
            loop.UnitWorld = world;
            SpyUnit unit = MakeUnit(clock, loop);

            BeginDissolve(unit, DesintegrTip.Burn);
            unit.TickCorpse();
            unit.ReviveFromCorpse();

            Assert.IsFalse(unit.IsCorpse);
            Assert.IsFalse(unit.IsCorpseRemoved);

            UnitHandle handle = StealthHandleOf(unit);
            Assert.IsTrue(world.TryGetCorpse(handle, out UnitCorpseData data));
            Assert.That(data.State, Is.EqualTo(CorpseState.None));
            Assert.That(data.DissolveFrames, Is.EqualTo(0f));
        }

        [Test]
        public void LocalPath_LoopWithoutWorld_KeepsTheWholeLifecycleInTheFallback()
        {
            SimClock clock = MakeClock(out SimLoop loop);
            SpyUnit unit = MakeUnit(clock, loop);

            BeginDissolve(unit, DesintegrTip.Plasma);
            unit.TickCorpse();

            Assert.IsTrue(unit.IsCorpse);
            Assert.IsFalse(StealthHandleOf(unit).IsValid,
                "no world pushed — the fallback copy is in use and no slot must exist");

            UnitCorpseData local = LocalCorpseOf(unit);
            Assert.That(local.State, Is.EqualTo(CorpseState.Dissolving));
            Assert.That(local.Tip, Is.EqualTo(DesintegrTip.Plasma));
            Assert.That(local.DissolveFrames, Is.EqualTo(clock.StepScale));

            for (int i = 1; i < CorpseDamageMath.DissolveFrames; i++)
            {
                unit.TickCorpse();
            }

            Assert.IsTrue(unit.IsCorpseRemoved);
            Assert.That(LocalCorpseOf(unit).State, Is.EqualTo(CorpseState.Removed));
        }

        [Test]
        public void BothPaths_AreNumericallyIdentical_ForTheSameLifecycle()
        {
            SimClock worldClock = MakeClock(out SimLoop worldLoop);
            worldLoop.UnitWorld = new UnitWorld();
            SpyUnit inWorld = MakeUnit(worldClock, worldLoop);

            SimClock localClock = MakeClock(out SimLoop localLoop);
            SpyUnit local = MakeUnit(localClock, localLoop);

            BeginDissolve(inWorld, DesintegrTip.BloodRed);
            BeginDissolve(local, DesintegrTip.BloodRed);

            for (int i = 0; i < CorpseDamageMath.DissolveFrames; i++)
            {
                inWorld.TickCorpse();
                local.TickCorpse();

                Assert.That(inWorld.IsCorpse, Is.EqualTo(local.IsCorpse),
                    $"IsCorpse diverged at tick {i}");
                Assert.That(inWorld.IsCorpseRemoved, Is.EqualTo(local.IsCorpseRemoved),
                    $"IsCorpseRemoved diverged at tick {i}");
            }

            Assert.IsTrue(inWorld.IsCorpseRemoved);
            Assert.IsTrue(local.IsCorpseRemoved);

            Assert.IsTrue(
                worldLoop.UnitWorld.TryGetCorpse(StealthHandleOf(inWorld), out UnitCorpseData slot));
            UnitCorpseData fallback = LocalCorpseOf(local);
            Assert.That(slot.State, Is.EqualTo(fallback.State));
            Assert.That(slot.DissolveFrames, Is.EqualTo(fallback.DissolveFrames));
            Assert.That(slot.Tip, Is.EqualTo(fallback.Tip));
            Assert.That(slot.LastDamageType, Is.EqualTo(fallback.LastDamageType),
                "the record is byte-identical between the world slot and the fallback copy");
        }
    }
}
