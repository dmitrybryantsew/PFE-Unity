using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using PFE.Data.Definitions;
using PFE.Systems.Weapons;
using PFE.Systems.Weapons.Controllers;

namespace PFE.Tests.EditMode.Systems.Weapons
{
    /// <summary>
    /// Pins the melee / unarmed owner multipliers that AS3 reads out of <c>Pers</c> but the port
    /// previously wrote and never read.
    ///
    /// <para><b>Why this fixture exists.</b> The statId audit (2026-10-02) found
    /// <c>meleeDamMult</c>, <c>meleeSpdMult</c> and <c>punchDamMult</c> in the "AS3 reads it, the
    /// port does not" cell: the fields existed on <c>CharacterStats</c>, perks and the melee skill
    /// wrote them, and <b>no gameplay code read any of them</b>. A melee build scaled for damage and
    /// swing speed played exactly like a bare one. Nothing asserted the multipliers, so the suite was
    /// silent — the same shape as the hit-volume bug next door.</para>
    ///
    /// <para><b>The one-frame offset, again.</b> AS3 sets <c>t_attack = rapid_act</c> and fires from
    /// inside the same pass that runs <c>--t_attack</c>, so the observable <c>t_attack</c> on the
    /// frame a hit is emitted is one lower than the window the code computed. The tests below assert
    /// the <i>duration</i> (how many frames until the next swing is allowed) rather than the exact
    /// frame of the hit, which is well-defined and free of that offset.</para>
    ///
    /// <para>AS3 authority: <c>fe/weapon/WClub.as:204-205,649,658,667</c> (melee) and
    /// <c>WKick.as:47-48</c> (unarmed).</para>
    /// </summary>
    [TestFixture]
    public class MeleeOwnerStatTests
    {
        private const float Dt = 1f / 30f;   // one AS3 flash frame

        // ── Stubs ─────────────────────────────────────────────────────────────

        /// <summary>A stat source we can dial per test. Mirrors the ranged fixture's stub.</summary>
        private sealed class FakeStats : IWeaponStatSource
        {
            public float ReloadMult   = 1f;
            public float RecoilMult   = 1f;
            public float JammedMult   = 1f;
            public float Recyc        = 0f;
            public float MeleeDamMult = 1f;
            public float MeleeSpdMult = 1f;
            public float PunchDamMult = 1f;
            public float KickDestroy  = 30f;
            public float MeleeRun     = 10f;

            float IWeaponStatSource.ReloadMult   => ReloadMult;
            float IWeaponStatSource.RecoilMult   => RecoilMult;
            float IWeaponStatSource.JammedMult   => JammedMult;
            float IWeaponStatSource.Recyc        => Recyc;
            float IWeaponStatSource.MeleeDamMult => MeleeDamMult;
            float IWeaponStatSource.MeleeSpdMult => MeleeSpdMult;
            float IWeaponStatSource.PunchDamMult => PunchDamMult;
            float IWeaponStatSource.KickDestroy  => KickDestroy;
            float IWeaponStatSource.MeleeRun     => MeleeRun;
        }

        private sealed class NullHitVolume : IMeleeHitVolume
        {
            public void SetActive(bool active) { }
            public void BindMove(Vector2 prevTip, Vector2 currTip) { }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static WeaponDefinition MakeMelee(float rapid, float damage,
                                                  MeleeType type = MeleeType.Horizontal)
        {
            var def = ScriptableObject.CreateInstance<WeaponDefinition>();
            def.weaponId      = $"test_melee_{type}";
            def.weaponType    = WeaponType.Melee;
            def.meleeType     = type;
            def.rapid         = rapid;
            def.baseDamage    = damage;
            def.meleeDlina    = 100f;
            def.meleeMinDlina = 100f;
            return def;
        }

        private static WeaponDefinition MakeUnarmed(float rapid, float damage, float knockback)
        {
            var def = ScriptableObject.CreateInstance<WeaponDefinition>();
            def.weaponId   = "test_punch";
            def.weaponType = WeaponType.Internal;
            def.punch      = 1;                 // selects UnarmedWeaponController
            def.rapid      = rapid;
            def.baseDamage = damage;
            def.knockback  = knockback;
            return def;
        }

        /// <summary>
        /// Run one melee swing and return every ShotPlan the controller emitted. Damage is read from
        /// the last plan, which is the one that carries the final multipliers.
        /// </summary>
        private static List<ShotPlan> RunMeleeSwing(WeaponDefinition def, IWeaponStatSource stats,
                                                    Vector2 aim, int frames)
        {
            var ctrl = new MeleeWeaponController(new WeaponRuntimeState(def), stats);
            ctrl.HitVolume = new NullHitVolume();
            var plans = new List<ShotPlan>();

            ctrl.BeginAttack();
            var hold = Vector2.zero;
            for (int i = 0; i < frames; i++)
            {
                ctrl.Tick(Dt, hold, hold, aim);
                plans.AddRange(ctrl.FlushShotPlans());
            }

            ctrl.Dispose();
            UnityEngine.Object.DestroyImmediate(def);
            return plans;
        }

        private static List<ShotPlan> RunPunch(WeaponDefinition def, IWeaponStatSource stats,
                                               Vector2 aim, int frames)
        {
            var ctrl = new UnarmedWeaponController(new WeaponRuntimeState(def), stats);
            var plans = new List<ShotPlan>();

            ctrl.BeginAttack();
            var hold = Vector2.zero;
            for (int i = 0; i < frames; i++)
            {
                ctrl.Tick(Dt, hold, hold, aim);
                plans.AddRange(ctrl.FlushShotPlans());
            }

            ctrl.Dispose();
            UnityEngine.Object.DestroyImmediate(def);
            return plans;
        }

        /// <summary>Frames from attack start until a second attack is accepted.</summary>
        private static int FramesUntilRefire(WeaponDefinition def, IWeaponStatSource stats, int cap)
        {
            var ctrl = new MeleeWeaponController(new WeaponRuntimeState(def), stats);
            ctrl.HitVolume = new NullHitVolume();

            ctrl.BeginAttack();
            var hold = Vector2.zero;
            var aim  = Vector2.zero;

            // First frame accepts the attack and sets t_attack.
            ctrl.Tick(Dt, hold, hold, aim);
            int firstKolShoot = ctrl.State.KolShoot;

            for (int i = 1; i <= cap; i++)
            {
                ctrl.Tick(Dt, hold, hold, aim);
                if (ctrl.State.KolShoot > firstKolShoot)
                {
                    ctrl.Dispose();
                    UnityEngine.Object.DestroyImmediate(def);
                    return i;   // frames after the first before the next swing landed
                }
            }

            ctrl.Dispose();
            UnityEngine.Object.DestroyImmediate(def);
            return -1;
        }

        // ── meleeDamMult ──────────────────────────────────────────────────────

        [Test]
        public void MeleeDamage_WithADoubledMeleeDamMult_DoublesTheHit()
        {
            var baseline = RunMeleeSwing(MakeMelee(rapid: 20f, damage: 10f), null,
                                         new Vector2(5f, 0f), frames: 20);
            var boosted  = RunMeleeSwing(MakeMelee(rapid: 20f, damage: 10f),
                                         new FakeStats { MeleeDamMult = 2f },
                                         new Vector2(5f, 0f), frames: 20);

            Assert.IsNotEmpty(baseline, "control: a swing with no owner stats must still emit a plan.");
            Assert.IsNotEmpty(boosted,  "the boosted swing must emit a plan too.");

            float control = baseline[baseline.Count - 1].Damage.BaseDamage;
            float sujet   = boosted[boosted.Count - 1].Damage.BaseDamage;

            Assert.AreEqual(10f, control, 0.001f,
                "with no stat source the owner multiplier is 1, so the weapon's own damage stands.");
            Assert.AreEqual(20f, sujet, 0.001f,
                "meleeDamMult = 2 must double the melee hit (WClub.as:205,649).");
        }

        [Test]
        public void MeleeDamage_DoesNotScaleTheKnockback()
        {
            // AS3's melee resultDamage scales damage only; the knock is otbros * otbrosMult with no
            // melee term (WClub.as:594). A knock that scaled with meleeDamMult would be a bonus AS3
            // does not grant, and it would be invisible in a damage-only test.
            var def = MakeMelee(rapid: 20f, damage: 10f);
            var plans = RunMeleeSwing(def, new FakeStats { MeleeDamMult = 3f },
                                      new Vector2(5f, 0f), frames: 20);

            // MakeMelee leaves knockback at the WeaponDefinition default, so assert the multiplier
            // did not move it *relative to the same weapon with no stats*.
            var control = RunMeleeSwing(MakeMelee(rapid: 20f, damage: 10f), null,
                                        new Vector2(5f, 0f), frames: 20);

            Assert.AreEqual(control[control.Count - 1].Damage.Knockback,
                            plans[plans.Count - 1].Damage.Knockback, 0.001f,
                "melee knockback must not scale with meleeDamMult (WClub.as:594).");
        }

        // ── meleeSpdMult ──────────────────────────────────────────────────────

        [Test]
        public void MeleeSpeed_WithADoubledMeleeSpdMult_SwingsTwiceAsFast()
        {
            int control = FramesUntilRefire(MakeMelee(rapid: 20f, damage: 10f), null, cap: 120);
            int fast    = FramesUntilRefire(MakeMelee(rapid: 20f, damage: 10f),
                                            new FakeStats { MeleeSpdMult = 2f }, cap: 120);

            Assert.Greater(control, 0, "control: the weapon must refire within the cap.");

            // rapid = 20 → rapid_act = 20 with no stats, 10 at meleeSpdMult 2. Allow one frame of
            // slack for the same single-frame offset the hit-window tests document.
            Assert.AreEqual(20, control, 2, "rapid_act is rapid when meleeSpdMult is 1.");
            Assert.AreEqual(10, fast, 2,
                "meleeSpdMult = 2 must halve the swing (WClub.as:204,658,667).");
        }

        [Test]
        public void MeleeSpeed_DoesNotChangeTheDamage()
        {
            // rapidMult and damMult are separate fields in AS3; a speed perk that also changed the
            // damage would be a silent composite the oracle does not have.
            var control = RunMeleeSwing(MakeMelee(rapid: 20f, damage: 10f), null,
                                        new Vector2(5f, 0f), frames: 40);
            var fast    = RunMeleeSwing(MakeMelee(rapid: 20f, damage: 10f),
                                        new FakeStats { MeleeSpdMult = 3f },
                                        new Vector2(5f, 0f), frames: 40);

            Assert.AreEqual(control[control.Count - 1].Damage.BaseDamage,
                            fast[fast.Count - 1].Damage.BaseDamage, 0.001f,
                "meleeSpdMult must not touch melee damage.");
        }

        // ── punchDamMult ──────────────────────────────────────────────────────

        [Test]
        public void Punch_WithARaisedPunchDamMult_ScalesDamageAndKnockbackTogether()
        {
            // AS3 WKick.as:47-48 scales BOTH:
            //   b.damage = damage * punchDamMult;  b.otbros = otbros * punchDamMult;
            var control = RunPunch(MakeUnarmed(rapid: 10f, damage: 10f, knockback: 4f), null,
                                   new Vector2(5f, 0f), frames: 10);
            var boosted = RunPunch(MakeUnarmed(rapid: 10f, damage: 10f, knockback: 4f),
                                   new FakeStats { PunchDamMult = 1.5f },
                                   new Vector2(5f, 0f), frames: 10);

            Assert.IsNotEmpty(control, "control: a punch with no owner stats must still emit a plan.");

            Assert.AreEqual(10f, control[control.Count - 1].Damage.BaseDamage, 0.001f,
                "punchDamMult defaults to 1.");
            Assert.AreEqual(15f, boosted[boosted.Count - 1].Damage.BaseDamage, 0.001f,
                "punchDamMult = 1.5 must scale the punch's damage (WKick.as:47).");

            Assert.AreEqual(4f, control[control.Count - 1].Damage.Knockback, 0.001f);
            Assert.AreEqual(6f, boosted[boosted.Count - 1].Damage.Knockback, 0.001f,
                "punchDamMult also scales the knockback (WKick.as:48) — unlike melee.");
        }

        [Test]
        public void Punch_WithNoOwnerStats_IsUnchangedFromTheDefinition()
        {
            // The negative control for the two above: a null stat source must not scale anything, and
            // in particular must not apply the 1.5 the interface happens to declare elsewhere.
            var plans = RunPunch(MakeUnarmed(rapid: 10f, damage: 8f, knockback: 3f), null,
                                 new Vector2(5f, 0f), frames: 10);

            Assert.IsNotEmpty(plans);
            Assert.AreEqual(8f, plans[plans.Count - 1].Damage.BaseDamage, 0.001f);
            Assert.AreEqual(3f, plans[plans.Count - 1].Damage.Knockback, 0.001f);
        }

        // ── Back-hit (zadok) composes with punchDamMult ────────────────────────

        [Test]
        public void Punch_BackHit_ComposesItsDoublingWithTheOwnerMultiplier()
        {
            // Facing right (aim to +x sets storona), then punch to the left: WKick.as:64-65 doubles
            // damage and ×1.5 the knockback on a back-hit. punchDamMult must multiply on top of that,
            // not replace it — AS3 applies the two in separate statements on the same bullet.
            var def = MakeUnarmed(rapid: 10f, damage: 10f, knockback: 4f);
            var plans = RunPunch(def, new FakeStats { PunchDamMult = 2f },
                                 new Vector2(-5f, 0f), frames: 10);

            Assert.IsNotEmpty(plans, "the back-punch must emit a plan.");
            Assert.AreEqual(40f, plans[plans.Count - 1].Damage.BaseDamage, 0.001f,
                "back-hit ×2 composed with punchDamMult ×2 = ×4 (WKick.as:47,64).");
        }

        // ── The seam itself ───────────────────────────────────────────────────

        [Test]
        public void MeleeControllers_AcceptAStatSource_AndTolerateNull()
        {
            // A reflection-free guard that the two controllers expose the stat-source parameter and
            // do not throw when handed null — the state every enemy weapon and every pre-existing
            // test hit before this change.
            var def = MakeMelee(rapid: 20f, damage: 10f);

            Assert.DoesNotThrow(() =>
            {
                using var ctrl = new MeleeWeaponController(new WeaponRuntimeState(def), null);
                ctrl.Tick(Dt, Vector2.zero, Vector2.zero, Vector2.zero);
            }, "MeleeWeaponController must tolerate a null stat source.");

            var udef = MakeUnarmed(rapid: 10f, damage: 10f, knockback: 4f);
            Assert.DoesNotThrow(() =>
            {
                using var ctrl = new UnarmedWeaponController(new WeaponRuntimeState(udef), null);
                ctrl.Tick(Dt, Vector2.zero, Vector2.zero, Vector2.zero);
            }, "UnarmedWeaponController must tolerate a null stat source.");

            UnityEngine.Object.DestroyImmediate(def);
            UnityEngine.Object.DestroyImmediate(udef);
        }
    }
}
