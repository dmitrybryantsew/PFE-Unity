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
    /// <para><b>Two more guards added 2026-10-03, after the audit's §4 was re-checked.</b> The
    /// original fixture only asked whether *some* frame was active, so it could not see that mtip 0's
    /// window opened at <c>rapid*1/6</c> instead of <c>rapid/2</c> — twice as long, starting a third
    /// of the swing early — nor that the plan carrying the damage context was published *after* the
    /// Thrust window had already closed. <c>StrikeWindow_MatchesTheOracleBounds</c> pins the bounds
    /// per sub-type; <c>DamagePlan_IsPublishedBeforeTheStrikeWindowOpens</c> pins the ordering. The
    /// window is the whole of the behaviour here, so the window is what gets asserted.</para>
    ///
    /// <para>AS3 authority: <c>fe/weapon/WClub.as</c> — mtip 0 at :378-408, mtip 1 at :435-454,
    /// mtip 2 at :469-483, <c>shoot()</c> at :674. Audit §3 and §4.</para>
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
            public bool EndedActive => Toggles.Count > 0 && Toggles[Toggles.Count - 1];
        }

        /// <summary>One flash frame's outcome: the t_attack the strike window compared, whether the
        /// hit volume was left enabled, and how many ShotPlans the frame published.</summary>
        private readonly struct Step
        {
            public readonly int  SawTAttack;
            public readonly bool VolumeActive;
            public readonly int  PlansThisFrame;

            public Step(int sawTAttack, bool volumeActive, int plansThisFrame)
            {
                SawTAttack     = sawTAttack;
                VolumeActive   = volumeActive;
                PlansThisFrame = plansThisFrame;
            }
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

        /// <summary>
        /// Drive a swing one flash frame at a time and record what the strike window saw on each.
        ///
        /// <para><b>Why <c>+ 1</c>.</b> <c>State.TAttack</c> is read after <c>Tick</c>, i.e. after
        /// that frame's decrement, so the value the window compared is one higher. AS3 runs the whole
        /// anim + strike-window block <i>before</i> its own <c>--t_attack</c>
        /// (<c>WClub.as:337-495</c>) and the controller mirrors that order — this is what makes the
        /// recorded value the oracle's <c>t_attack</c> rather than an off-by-one.</para>
        /// </summary>
        private static List<Step> RunFrames(MeleeType type, float rapid, float minReach, float reach,
                                           int frames)
        {
            var def  = MakeDef(type, rapid, minReach, reach);
            var ctrl = new MeleeWeaponController(new WeaponRuntimeState(def));
            var vol  = new RecordingHitVolume();
            ctrl.HitVolume = vol;

            var log  = new List<Step>();
            var hold = Vector2.zero;
            var aim  = Vector2.zero;    // aim at the weapon itself: the spring cannot drift

            ctrl.BeginAttack();
            for (int i = 0; i < frames; i++)
            {
                ctrl.Tick(Dt, hold, hold, aim);
                log.Add(new Step(ctrl.State.TAttack + 1, vol.EndedActive,
                                 ctrl.FlushShotPlans().Count));
            }

            ctrl.Dispose();
            UnityEngine.Object.DestroyImmediate(def);
            return log;
        }

        // ── The window bounds, per sub-type ──────────────────────────────────────

        /// <summary>
        /// Pins each sub-type's strike window against the oracle's bounds.
        ///
        /// <para><b>Why this exists.</b> mtip 0 read <c>TAttack &gt; rapid*1/6</c> instead of
        /// <c>&gt;= rapid/2</c> until 2026-10-03, so a club damaged during the first third of its own
        /// wind-up and stayed active for twice the oracle's span. The fixture next door could not see
        /// it: it only asserts that <i>some</i> frame is active, and a window that is too wide is
        /// still a window.</para>
        ///
        /// <para>rapid 20 → rapid_act 20 → the window is <c>[10, 16.67)</c>, i.e. the integer
        /// <c>t_attack</c> values 10..16, for <b>both</b> mtip 0 and mtip 1 — <c>WClub.as:378</c> and
        /// <c>:435</c> carry identical bounds and differ only in the geometry they sweep. mtip 2
        /// takes exactly <c>t_attack == 1</c> (<c>:469</c>).</para>
        ///
        /// <para><b><c>lowest</c> / <c>highest</c>, not "first" / "last".</b> <c>t_attack</c> counts
        /// <i>down</i>, so the recorded frames arrive highest-first: the window <i>opens</i> at
        /// <c>highest</c> and <i>closes</i> after <c>lowest</c>. Reading the recorded list as if it
        /// were ascending is the trap this naming avoids — the earlier <c>first</c>/<c>last</c>
        /// naming asserted <c>active[0] == 10</c>, which the descending list can never satisfy.</para>
        /// </summary>
        [TestCase(MeleeType.Horizontal, 20f, 10, 16)]
        [TestCase(MeleeType.Thrust,     20f, 10, 16)]
        [TestCase(MeleeType.Overhead,    6f,  1,  1)]
        public void StrikeWindow_MatchesTheOracleBounds(MeleeType type, float rapid, int lowest, int highest)
        {
            var log = RunFrames(type, rapid, minReach: 50f, reach: 50f, frames: (int)rapid);

            var active = new List<int>();
            foreach (var step in log)
                if (step.VolumeActive) active.Add(step.SawTAttack);

            Assert.IsNotEmpty(active, $"{type}: the strike window never opened.");

            // Frame order is descending t_attack (see the doc comment), so the OPEN end of the window
            // is the first recorded frame and the CLOSE end is the last.
            Assert.AreEqual(highest, active[0],
                $"{type}: the window must open at t_attack {highest}. Opening higher means it starts " +
                "before rapid_act*5/6 (the wind-up bug); opening at {active[0]} it does.");
            Assert.AreEqual(lowest, active[active.Count - 1],
                $"{type}: the window must close after t_attack {lowest}. Closing lower means it runs " +
                "past rapid_act/2 (the mtip 0 bug this test was added for).");
            Assert.AreEqual(highest - lowest + 1, active.Count,
                $"{type}: the window must be contiguous over [{lowest},{highest}] — got " +
                $"[{string.Join(",", active)}].");
        }

        /// <summary>
        /// The damage context must already be published when the strike window opens — for every
        /// sub-type, on the first swing.
        ///
        /// <para><b>Why this is a separate assertion.</b> The MeleeSweep plan is the only carrier of
        /// the damage context to <c>MeleeHitVolume</c>, and its <c>OnTriggerEnter2D</c> falls back to
        /// a flat <c>TakeDamage(1f)</c> while no context has ever been set. Thrust used to publish its
        /// plan at <c>t_attack == rapid_act/2</c> — <i>after</i> its own window had opened and closed —
        /// so the first thrust swing of a session swept unarmed, and every later one used the previous
        /// swing's damage. Nothing asserted the plan at all, so both were invisible.</para>
        ///
        /// <para>AS3 arms the bullet once per attack from <c>weaponAttack()</c> → <c>shoot()</c>
        /// (<c>WClub.as:674</c>), for every mtip; the per-mtip branches in <c>actions()</c> only move
        /// it afterwards. So the plan belongs on frame 0 for all three.</para>
        /// </summary>
        [TestCase(MeleeType.Horizontal)]
        [TestCase(MeleeType.Thrust)]
        [TestCase(MeleeType.Overhead)]
        public void DamagePlan_IsPublishedBeforeTheStrikeWindowOpens(MeleeType type)
        {
            var log = RunFrames(type, rapid: 20f, minReach: 50f, reach: 50f, frames: 20);

            int firstPlan = -1, firstActive = -1;
            for (int i = 0; i < log.Count; i++)
            {
                if (firstPlan   < 0 && log[i].PlansThisFrame > 0) firstPlan   = i;
                if (firstActive < 0 && log[i].VolumeActive)       firstActive = i;
            }

            Assert.AreEqual(0, firstPlan,
                $"{type}: the damage plan must be published on the attack's first frame " +
                $"(WClub.as:674) — got frame {firstPlan}.");
            Assert.GreaterOrEqual(firstActive, 0, $"{type}: the strike window never opened.");
            Assert.Greater(firstActive, firstPlan,
                $"{type}: the window must open after the plan exists. A window that opens first " +
                "sweeps with no damage context, and MeleeHitVolume hits for 1.");
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
            // anim rises to 1.0 across the window, so the tip peaks at dlina + atDlina = 0.5 + 1.0.
            //
            // The peak is now asserted EXACTLY. It used to be a 1.2–1.6 range because of a one-frame
            // lag: RunActions() decremented TAttack before the window ran, so the window evaluated
            // `anim` from the previous TAttack (peak 1.3125) where AS3 evaluates anim and the window
            // against the same t_attack (peak 1.5). That ordering was fixed on 2026-10-03 — the
            // decrement now follows the window, as it does in AS3 (:495) — so the oracle's 1.5 is
            // what the controller must produce, and the range would only hide a regression back to
            // the lagged value.
            var vol = RunOneSwing(MeleeType.Thrust, rapid: 20f, minReach: 50f, reach: 50f, frames: 20);

            Assert.Greater(vol.MoveCount, 1, "The thrust window spans several frames, so it must sweep.");

            float first = vol.Curr[0].magnitude;
            float peak  = 0f;
            foreach (var tip in vol.Curr) peak = Mathf.Max(peak, tip.magnitude);

            Assert.Greater(peak, first,
                "The spear must reach FURTHER as the attack progresses (anim rises), not retract.");

            Assert.That(peak, Is.EqualTo(1.5f).Within(0.01f),
                $"Peak reach must be dlina + atDlina = 1.5 units (got {peak}). Near 0.5 means the " +
                "atDlina lunge was dropped; near 2.0 means dlina was applied twice; 1.3125 means the " +
                "strike window is again sampling anim one frame stale.");
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
