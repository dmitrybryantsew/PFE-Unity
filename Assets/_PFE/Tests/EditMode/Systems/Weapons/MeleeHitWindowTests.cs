using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using PFE.Data.Definitions;
using PFE.Systems.Weapons;
using PFE.Systems.Weapons.Controllers;

namespace PFE.Tests.EditMode.Systems.Weapons
{
    /// <summary>
    /// Pins that every melee sub-type actually enables its hit volume, and sweeps the path AS3 sweeps.
    ///
    /// <para><b>Why this fixture exists.</b> <c>MeleeWeaponController.UpdateHitWindow</c> used to
    /// early-return for any <c>meleeType != Horizontal</c>, and <c>HitVolume.SetActive(true)</c> exists
    /// only past that guard — so <b>Thrust and Overhead had a permanently disabled hit volume and could
    /// never hit anything</b>: spear / mspear / tlance (Thrust) and autoaxe / bsaw / ripper (Overhead).
    /// Six weapons that swung, made noise and burned durability while being unable to damage a single
    /// enemy. Nothing asserted the hit volume's on/off transitions, so the suite was silent.</para>
    ///
    /// <para>The recording stub is the only way to see this from EditMode: the real
    /// <c>MeleeHitVolume</c> caches its <c>CapsuleCollider2D</c> in <c>Awake</c>, and EditMode never
    /// runs <c>Awake</c> for <c>AddComponent</c>.</para>
    ///
    /// <para><b>Why the aim target equals the hold point</b> in the geometry tests: with a distant aim
    /// the weapon's spring position drifts toward it each frame (pre-existing behaviour, not under
    /// test), which offsets the absolute tip coordinates. Aiming at the weapon's own position freezes
    /// the spring, so the tip distance is exactly the AS3 expression — and it also exercises
    /// <c>DirectionTo</c>'s degenerate-input fallback, which AS3's <c>atan2(0,0)</c> resolves the same
    /// way.</para>
    ///
    /// <para>AS3 authority: <c>fe/weapon/WClub.as</c> — mtip 0 at :378-408, mtip 1 at :435-454,
    /// mtip 2 at :469-483. Audit §3.</para>
    /// </summary>
    [TestFixture]
    public class MeleeHitWindowTests
    {
        private const float Dt = 1f / 30f;   // one AS3 flash frame

        private sealed class RecordingHitVolume : IMeleeHitVolume
        {
            public readonly List<bool> Toggles = new List<bool>();
            public readonly List<Vector2> Prev = new List<Vector2>();
            public readonly List<Vector2> Curr = new List<Vector2>();

            public void SetActive(bool active) => Toggles.Add(active);

            public void BindMove(Vector2 prevTip, Vector2 currTip)
            {
                Prev.Add(prevTip);
                Curr.Add(currTip);
            }

            public bool WasEnabled => Toggles.Contains(true);
            public int  MoveCount  => Curr.Count;
        }

        private static WeaponDefinition MakeDef(MeleeType type, float rapid, float minReach, float reach)
        {
            var def = ScriptableObject.CreateInstance<WeaponDefinition>();
            def.weaponId      = $"test_melee_{type}";
            def.weaponType    = WeaponType.Melee;
            def.meleeType     = type;
            def.rapid         = rapid;
            def.meleeDlina    = reach;      // AS3 phis@long
            def.meleeMinDlina = minReach;   // AS3 phis@minlong
            return def;
        }

        /// <summary>Swing once and return everything the controller told the hit volume to do.</summary>
        private static RecordingHitVolume RunOneSwing(MeleeType type, float rapid,
                                                     float minReach, float reach, int frames)
        {
            var def  = MakeDef(type, rapid, minReach, reach);
            var ctrl = new MeleeWeaponController(new WeaponRuntimeState(def));
            var vol  = new RecordingHitVolume();
            ctrl.HitVolume = vol;

            ctrl.BeginAttack();
            var hold = Vector2.zero;
            var aim  = Vector2.zero;    // aim at the weapon itself: the spring cannot drift
            for (int i = 0; i < frames; i++)
                ctrl.Tick(Dt, hold, hold, aim);

            ctrl.Dispose();
            UnityEngine.Object.DestroyImmediate(def);
            return vol;
        }

        // ── Horizontal — the control, which already worked ───────────────────────

        [Test]
        public void Horizontal_EnablesTheHitVolume_AndSweepsEachActiveFrame()
        {
            var vol = RunOneSwing(MeleeType.Horizontal, rapid: 20f, minReach: 100f, reach: 100f, frames: 20);

            Assert.IsTrue(vol.WasEnabled, "mtip 0 must enable the hit volume during its strike window.");
            Assert.Greater(vol.MoveCount, 1, "mtip 0 sweeps the arc, so it binds more than once per swing.");
        }

        // ── Thrust — could never hit before ─────────────────────────────────────

        [Test]
        public void Thrust_EnablesTheHitVolume()
        {
            var vol = RunOneSwing(MeleeType.Thrust, rapid: 20f, minReach: 50f, reach: 50f, frames: 20);

            Assert.IsTrue(vol.WasEnabled,
                "mtip 1 must enable the hit volume — spear/mspear/tlance never did before (audit §3).");
        }

        [Test]
        public void Thrust_LungesOutward_ReachingRoughlyDlinaPlusAtDlina()
        {
            // WClub.as:433-438 — tip = X + cos2*dlina + cos2*anim*atDlina, atDlina = 100 px = 1 unit.
            // anim rises to 1.0 across the window, so the tip peaks near dlina + atDlina = 0.5 + 1.0.
            //
            // The peak is asserted as a range because of a known one-frame lag: UpdateAnim() runs
            // before RunActions() decrements TAttack, so the strike window evaluates `anim` from the
            // *previous* TAttack (peak 1.3125) where AS3 evaluates anim and the window against the same
            // t_attack (peak 1.5). Both are accepted here — the point of this test is that the atDlina
            // lunge is applied at all. Aligning the two is audit §4's melee phase-math item.
            var vol = RunOneSwing(MeleeType.Thrust, rapid: 20f, minReach: 50f, reach: 50f, frames: 20);

            Assert.Greater(vol.MoveCount, 1, "The thrust window spans several frames, so it must sweep.");

            float first = vol.Curr[0].magnitude;
            float peak  = 0f;
            foreach (var tip in vol.Curr) peak = Mathf.Max(peak, tip.magnitude);

            Assert.Greater(peak, first,
                "The spear must reach FURTHER as the attack progresses (anim rises), not retract.");

            Assert.That(peak, Is.InRange(1.2f, 1.6f),
                $"Peak reach should approach dlina + atDlina = 1.5 units (got {peak}). Near 0.5 means " +
                "the atDlina lunge was dropped; near 2.0 means dlina was applied twice.");
        }

        // ── Overhead — could never hit before ───────────────────────────────────

        [Test]
        public void Overhead_EnablesTheHitVolume_ExactlyOnce_AtTheEndOfTheAttack()
        {
            // rapid 6 → the attack lasts 6 frames, so one call sequence is exactly one swing.
            var vol = RunOneSwing(MeleeType.Overhead, rapid: 6f, minReach: 30f, reach: 70f, frames: 6);

            Assert.IsTrue(vol.WasEnabled,
                "mtip 2 must enable the hit volume — autoaxe/bsaw/ripper never did before (audit §3).");

            Assert.AreEqual(1, vol.MoveCount,
                "WClub.as:469-483 binds ONE segment, on the t_attack == 1 frame. More than one means the " +
                "swing fires every frame; zero means the window never opened.");
        }

        [Test]
        public void Overhead_BindsTheSegmentFromMinReachToFullReach()
        {
            var vol = RunOneSwing(MeleeType.Overhead, rapid: 6f, minReach: 30f, reach: 70f, frames: 6);

            Assert.AreEqual(1, vol.MoveCount);

            Assert.That(Vector2.Distance(vol.Prev[0], vol.Curr[0]), Is.EqualTo(0.4f).Within(0.01f),
                "The segment spans mindlina → dlina = (70 - 30) px = 0.4 units. A length of ~0 means " +
                "meleeMinDlina was ignored; the weapon's reach values come from phis@long/@minlong.");

            Assert.That(vol.Curr[0].magnitude, Is.EqualTo(0.7f).Within(0.01f),
                "The segment ends at dlina (phis@long = 70 px = 0.7 units), along the aim axis.");
        }

        [Test]
        public void Overhead_DoesNotBindOnTheFramesBeforeTheLastOne()
        {
            // Run only the first half of the swing: nothing may have fired yet.
            var vol = RunOneSwing(MeleeType.Overhead, rapid: 6f, minReach: 30f, reach: 70f, frames: 3);

            Assert.AreEqual(0, vol.MoveCount,
                "AS3 fires the overhead at the END of the swing. An early bind means the port is still " +
                "firing at attack start — the timing inversion the audit flagged (WeaponAttack isInstant).");
        }
    }
}
