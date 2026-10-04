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
    /// <para><b>The one-frame offset is gone; the duration assertions stay.</b> This fixture used to
    /// have to work around AS3 setting <c>t_attack = rapid_act</c> and firing from inside the same
    /// pass that runs <c>--t_attack</c>, so the observable <c>t_attack</c> on the frame a hit is
    /// emitted read one lower than the window the code computed. The controller's decrement now
    /// follows its strike window, as AS3's does (<c>WClub.as:495</c>), so that offset no longer
    /// exists. These tests still assert the <i>duration</i> (how many frames until the next swing is
    /// allowed) rather than the exact frame of a hit, because duration is what the multipliers are
    /// about and it does not depend on the ordering at all.</para>
    ///
    /// <para>AS3 authority: <c>fe/weapon/WClub.as:204-205,649,654-658,667</c> (melee) and
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
            // Attacker-side hit procs — AS3 declaration defaults (0 = proc off).
            public float CritInvis    = 0f;
            public float Desintegr    = 0f;

            // Precision channel — AS3 Pers declaration defaults (composed multiplier stays 1). The
            // melee fixtures never read these, but the interface is shared with the ranged side.
            public float AllPrecMult = 1f;
            public float RunPenalty  = 0.5f;
            public float JumpPenalty = 0.3f;
            public float BackPenalty = 0.4f;
            public float StayBonus   = 0.3f;
            public float MazilAdd    = 0f;
            public float ComposedPrecisionMultiplier = 1f;

            // Weapon-skill channel — AS3 `_loc1_`. Identity defaults (see the ranged fixture).
            public float WeaponSkillMult = 1f;
            public int   OwnerSkillLevel = PFE.Systems.Combat.HitAvoidance.UnknownOwnerSkillLevel;

            float IWeaponStatSource.ReloadMult   => ReloadMult;
            float IWeaponStatSource.RecoilMult   => RecoilMult;
            float IWeaponStatSource.JammedMult   => JammedMult;
            float IWeaponStatSource.Recyc        => Recyc;
            float IWeaponStatSource.MeleeDamMult => MeleeDamMult;
            float IWeaponStatSource.MeleeSpdMult => MeleeSpdMult;
            float IWeaponStatSource.PunchDamMult => PunchDamMult;
            float IWeaponStatSource.KickDestroy  => KickDestroy;
            float IWeaponStatSource.MeleeRun     => MeleeRun;
            float IWeaponStatSource.CritInvis    => CritInvis;
            float IWeaponStatSource.Desintegr    => Desintegr;
            float IWeaponStatSource.AllPrecMult  => AllPrecMult;
            float IWeaponStatSource.RunPenalty   => RunPenalty;
            float IWeaponStatSource.JumpPenalty  => JumpPenalty;
            float IWeaponStatSource.BackPenalty  => BackPenalty;
            float IWeaponStatSource.StayBonus    => StayBonus;
            float IWeaponStatSource.MazilAdd     => MazilAdd;
            float IWeaponStatSource.PrecisionMultiplier => ComposedPrecisionMultiplier;
            float IWeaponStatSource.WeaponSkillMultiplier(int skillCode) => WeaponSkillMult;
            int   IWeaponStatSource.OwnerWeaponSkillLevel(int skillCode) => OwnerSkillLevel;
        }

        private sealed class NullHitVolume : IMeleeHitVolume
        {
            public void SetActive(bool active) { }
            public void BindMove(Vector2 prevTip, Vector2 currTip) { }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static WeaponDefinition MakeMelee(float rapid, float damage,
                                                  MeleeType type = MeleeType.Horizontal,
                                                  int weaponLevel = 0, float knockback = 0f)
        {
            var def = ScriptableObject.CreateInstance<WeaponDefinition>();
            def.weaponId      = $"test_melee_{type}";
            def.weaponType    = WeaponType.Melee;
            def.meleeType     = type;
            def.rapid         = rapid;
            def.baseDamage    = damage;
            def.meleeDlina    = 100f;
            def.meleeMinDlina = 100f;
            def.weaponLevel   = weaponLevel;
            def.knockback     = knockback;
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
                                                    Vector2 aim, int frames, int durability = -1)
        {
            var state = new WeaponRuntimeState(def);
            // -1 means "leave the constructor's default" (CurrentDurability = maxDurability, i.e.
            // unworn). A non-negative value forces the wear the test wants to observe.
            if (durability >= 0) state.CurrentDurability = durability;

            var ctrl = new MeleeWeaponController(state, stats);
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
                                               Vector2 aim, int frames,
                                               Vector2 holdPoint = default)
        {
            var ctrl = new UnarmedWeaponController(new WeaponRuntimeState(def), stats);
            var plans = new List<ShotPlan>();

            ctrl.BeginAttack();
            var hold = holdPoint;
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

        [Test]
        public void MeleeSpeed_DoesNotShortenAnOverheadSwing()
        {
            // WClub.resultRapid carries an mtip==2 early-out: `if(mtip == 2) return param1 / skillConf;`
            // (WClub.as:654-657) — no rapidMult term at all, and rapidMult is where meleeSpdMult enters
            // the family (`rapidMult = 1 / meleeSpdMult`, :204). So an autoaxe / bsaw / ripper keeps
            // its data length however fast the owner's melee speed is.
            //
            // The controller divided by meleeSpdMult for all three sub-types, so a speed perk halved
            // an overhead swing — a difference in behaviour, not in rounding.
            int control = FramesUntilRefire(MakeMelee(rapid: 20f, damage: 10f, type: MeleeType.Overhead),
                                            null, cap: 120);
            int fast    = FramesUntilRefire(MakeMelee(rapid: 20f, damage: 10f, type: MeleeType.Overhead),
                                            new FakeStats { MeleeSpdMult = 2f }, cap: 120);

            Assert.Greater(control, 0, "control: the overhead must refire within the cap.");
            Assert.AreEqual(20, control, 2, "an overhead with no stats swings in rapid frames.");
            Assert.AreEqual(20, fast, 2,
                "meleeSpdMult must NOT shorten an mtip 2 swing (WClub.as:654-657). A 10 here means " +
                "the divisor was applied to the one sub-type the oracle exempts.");
        }

        // ── The weapon-skill channel + wear (Phase 1c melee resultDamage) ──────
        //
        // These are Unity-only, like the rest of this fixture: they build a WeaponDefinition
        // ScriptableObject, so the offline wall can compile but not execute them. The formula they
        // compose is pinned offline by WeaponWearMathTests; these pin the *wiring* — that the
        // controller actually feeds the owner's skill and the weapon's wear into the shot.

        [Test]
        public void MeleeDamage_WithADoubledWeaponSkillMultiplier_DoublesTheHit()
        {
            // AS3's melee resultDamage takes `_loc1_` as its `p2` slot (WClub.as:649), and `_loc1_`
            // is `weaponSkill` for the player (Weapon.as:1454). The port passed 1 forever, so a melee
            // build's skill points bought nothing.
            var control = RunMeleeSwing(MakeMelee(rapid: 20f, damage: 10f), null,
                                        new Vector2(5f, 0f), frames: 20);
            var skilled = RunMeleeSwing(MakeMelee(rapid: 20f, damage: 10f),
                                        new FakeStats { WeaponSkillMult = 2f },
                                        new Vector2(5f, 0f), frames: 20);

            Assert.IsNotEmpty(control, "control: a swing with no owner stats must still emit a plan.");
            Assert.IsNotEmpty(skilled,  "the skilled swing must emit a plan too.");

            Assert.AreEqual(10f, control[control.Count - 1].Damage.BaseDamage, 0.001f,
                "no stat source leaves the multiplier at 1.");
            Assert.AreEqual(20f, skilled[skilled.Count - 1].Damage.BaseDamage, 0.001f,
                "the weapon-skill multiplier is the p2 factor of the melee resultDamage (WClub.as:649).");
        }

        [Test]
        public void MeleeDamage_WithAnOverqualifiedOwner_GetsTheSkillPlusDamageBonus()
        {
            // skillPlusDam = 1 + |gap|*0.1 for gap < 0 (Weapon.as:984-992), folded through the same
            // FromWeapon path the ranged weapon uses. weaponLevel 2 vs tier 5 = gap -3 -> x1.3.
            var onTier = RunMeleeSwing(MakeMelee(rapid: 20f, damage: 10f, weaponLevel: 2),
                                       new FakeStats { OwnerSkillLevel = 2 },
                                       new Vector2(5f, 0f), frames: 20);
            var over   = RunMeleeSwing(MakeMelee(rapid: 20f, damage: 10f, weaponLevel: 2),
                                       new FakeStats { OwnerSkillLevel = 5 },
                                       new Vector2(5f, 0f), frames: 20);

            Assert.IsNotEmpty(onTier, "control: a tier-matched owner must still swing.");
            Assert.AreEqual(10f, onTier[onTier.Count - 1].Damage.BaseDamage, 0.001f,
                "a gap of 0 earns no bonus.");
            Assert.AreEqual(13f, over[over.Count - 1].Damage.BaseDamage, 0.001f,
                "a 3-tier surplus is skillPlusDam 1.3 (Weapon.as:987).");
        }

        [Test]
        public void MeleeRefusesToSwing_WhenTheOwnerIsMoreThanTwoTiersUnder()
        {
            // WClub has no attack() override, so a swing runs the base checkAvail() (Weapon.as:1304):
            // a gap above 2 refuses the shot before t_attack is armed. weaponLevel 4 vs tier 0 = 4.
            var refused = RunMeleeSwing(MakeMelee(rapid: 20f, damage: 10f, weaponLevel: 4),
                                        new FakeStats { OwnerSkillLevel = 0 },
                                        new Vector2(5f, 0f), frames: 40);
            var allowed = RunMeleeSwing(MakeMelee(rapid: 20f, damage: 10f, weaponLevel: 4),
                                        new FakeStats { OwnerSkillLevel = 2 },
                                        new Vector2(5f, 0f), frames: 40);

            Assert.IsEmpty(refused,
                "a gap of 4 (>2) must refuse the swing — no plan and no t_attack (Weapon.as:1377-1381).");
            Assert.IsNotEmpty(allowed,
                "control: a gap of 2 is still allowed, so the gate is not simply always shut.");
        }

        [Test]
        public void MeleeDamage_IsReducedByWeaponWear()
        {
            // WClub.resultDamage's last factor is (1 - breaking*0.6) (WClub.as:649), and AS3 recomputes
            // breaking at the top of WClub.shoot() (:581-588). At 25% durability breaking = 0.5, so the
            // hit carries 70% of the fresh-weapon value. The port applied the wear penalty nowhere.
            var fresh = RunMeleeSwing(MakeMelee(rapid: 20f, damage: 10f), null,
                                      new Vector2(5f, 0f), frames: 20);
            var worn  = RunMeleeSwing(MakeMelee(rapid: 20f, damage: 10f), null,
                                      new Vector2(5f, 0f), frames: 20, durability: 25);

            Assert.IsNotEmpty(fresh, "control: the fresh swing must emit a plan.");
            Assert.AreEqual(10f, fresh[fresh.Count - 1].Damage.BaseDamage, 0.001f,
                "a full-durability weapon is unworn (breaking 0).");
            Assert.AreEqual(7f, worn[worn.Count - 1].Damage.BaseDamage, 0.001f,
                "breaking 0.5 -> (1 - 0.5*0.6) = 0.7 of the damage (WClub.as:649).");
        }

        [Test]
        public void MeleeWear_DoesNotScaleTheKnockback()
        {
            // The wear penalty is on damage only; AS3 leaves the knock at otbros*otbrosMult (:594).
            var fresh = RunMeleeSwing(MakeMelee(rapid: 20f, damage: 10f, knockback: 4f), null,
                                      new Vector2(5f, 0f), frames: 20);
            var worn  = RunMeleeSwing(MakeMelee(rapid: 20f, damage: 10f, knockback: 4f), null,
                                      new Vector2(5f, 0f), frames: 20, durability: 0);

            Assert.AreEqual(4f, fresh[fresh.Count - 1].Damage.Knockback, 0.001f,
                "control: the fresh knockback is the definition's.");
            Assert.AreEqual(fresh[fresh.Count - 1].Damage.Knockback,
                            worn[worn.Count - 1].Damage.Knockback, 0.001f,
                "wear must not move the knockback (WClub.as:594).");
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
            // AS3 WKick.as:64-65 puts `b.damage *= 2` INSIDE `if(this.kick)`, and a kick sets
            // `storona = -owner.storona` (:59) -- you strike BEHIND you. So the doubling is a property
            // of the ATTACK (a kick), not of the aim vector.
            //
            // The port has no kick input, so it approximates: `_storona` is derived from
            // `aimTarget.x >= holdPoint.x` (UnarmedWeaponController.cs:143) instead of the owner's
            // facing, and a "back hit" is inferred when the punch points opposite to that derived
            // facing. This fixture therefore has to produce the state that inference looks for:
            // hold to the RIGHT of the aim, so `_storona = -1`, while the punch lands to the LEFT --
            // which puts `punchAngle` outside (-PI/2, PI/2) and trips the zadok branch.
            //
            // The old fixture aimed at (-5, 0) from a hold point of (0, 0). That sets _storona = -1
            // on the SAME frame, so the two conditions cancel and no double was applied -- the test
            // measured 2x, not the 4x it asserted.
            var def = MakeUnarmed(rapid: 10f, damage: 10f, knockback: 4f);
            var plans = RunPunch(def, new FakeStats { PunchDamMult = 2f },
                                 holdPoint: new Vector2(50f, 0f), aim: new Vector2(-5f, 0f), frames: 10);

            Assert.IsNotEmpty(plans, "the back-punch must emit a plan.");
            Assert.AreEqual(40f, plans[plans.Count - 1].Damage.BaseDamage, 0.001f,
                "back-hit x2 composed with punchDamMult x2 = x4 (WKick.as:47,64).");
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
