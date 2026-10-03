using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using PFE.Core;
using PFE.Data.Definitions;
using PFE.Systems.Combat;
using PFE.Systems.Weapons;
using PFE.Systems.Weapons.Controllers;

namespace PFE.Tests.EditMode.Systems.Weapons
{
    /// <summary>
    /// Pins the magic weapon's <b>dual mana cost</b> — AS3 <c>WMagic.as</c>, which spends two
    /// different pools per shot and refuses the shot (with a lockout) when either is short.
    ///
    /// <para><b>Why this fixture exists.</b> The port imported neither cost correctly. AS3's
    /// <c>Weapon.getAmmoParam</c> (<c>Weapon.as:885-891</c>) maps two attributes separately —
    /// <c>ammo@magic</c> → the regenerating mana <i>budget</i> (<c>Unit.mana</c>) and
    /// <c>ammo@mana</c> → the mana <i>organ</i> (<c>Pers.manaHP</c>) — and
    /// <c>WMagic.shoot()</c> (<c>:110-122</c>) spends the first and wounds the second. The importer
    /// read only <c>mana</c>, into a <c>manaCost</c> field nothing consumed, so
    /// <c>magicPoolCost</c> / <c>manaHealthCost</c> stayed 0 and <b>every magic weapon cost
    /// nothing</b>. The controller also carried two local pool constants initialised to 100, so its
    /// "not enough mana" branch was unreachable after frame one.</para>
    ///
    /// <para>The gate tests below are the ones that would have caught both: they fail against a
    /// controller with a fixed 100-point pool, and against an importer that drops
    /// <c>ammo@magic</c>.</para>
    /// </summary>
    [TestFixture]
    public class MagicManaTests
    {
        /// <summary>A mana source with settable values, so a test can arrange "almost empty".</summary>
        private sealed class FakeMana : IManaSource
        {
            public float MagicMana         { get; set; }
            public float MaxMagicMana      { get; set; }
            public float ManaHp            { get; set; }
            public float ManaCostMultiplier { get; set; } = 1f;

            /// <summary>
            /// <c>Pers.spellsPoss</c>, defaulting to 1 — the AS3 declaration value
            /// (<c>Pers.as:423</c>), so every pre-existing test in this file keeps the gate open and
            /// none of them change behaviour. Set to 0 to model a wrecked mana organ.
            /// </summary>
            public int   SpellsPossible    { get; set; } = 1;

            public int   SpendCalls        { get; private set; }

            public void SpendMana(float poolCost, float organCost)
            {
                SpendCalls++;
                MagicMana = Mathf.Max(0f, MagicMana - poolCost);
                ManaHp    = Mathf.Max(0f, ManaHp    - organCost);
            }
        }

        /// <summary>
        /// The one member of <see cref="IWeaponStatSource"/> the magic path reads — the owner's tier
        /// in the weapon's skill, for <c>checkAvail</c>. Every other member is the AS3 <c>Pers</c>
        /// declaration default, since nothing else here consults them.
        /// </summary>
        private sealed class FakeStats : IWeaponStatSource
        {
            public int OwnerSkillLevel = HitAvoidance.UnknownOwnerSkillLevel;

            public float ReloadMult => 1f;
            public float RecoilMult => 1f;
            public float JammedMult => 1f;
            public float Recyc => 0f;
            public float MeleeDamMult => 1f;
            public float MeleeSpdMult => 1f;
            public float PunchDamMult => 1f;
            public float KickDestroy => 30f;
            public float MeleeRun => 10f;
            public float CritInvis => 0f;
            public float Desintegr => 0f;
            public float AllPrecMult => 1f;
            public float RunPenalty => 0.5f;
            public float JumpPenalty => 0.3f;
            public float BackPenalty => 0.4f;
            public float StayBonus => 0.3f;
            public float MazilAdd => 0f;
            public float PrecisionMultiplier => 1f;
            public float WeaponSkillMultiplier(int skillCode) => 1f;
            public int OwnerWeaponSkillLevel(int skillCode) => OwnerSkillLevel;
        }

        private static WeaponDefinition MakeMagic(float poolCost, float organCost, float rapid = 10f,
                                                  int weaponLevel = 0, int skillLevel = 6)
        {
            var def = ScriptableObject.CreateInstance<WeaponDefinition>();
            def.weaponId           = "test_magic";
            def.weaponType         = WeaponType.Magic;
            def.rapid              = rapid;
            def.prepFrames         = 0;
            def.burstCount         = 0;
            def.projectilesPerShot = 1;
            def.deviation          = 0f;
            def.magicPoolCost      = poolCost;
            def.manaHealthCost     = organCost;
            def.maxDurability      = 100;
            def.noiseRadius        = 0f;
            def.shineRadius        = 0;
            def.weaponLevel        = weaponLevel;
            def.skillLevel         = skillLevel;
            return def;
        }

        /// <summary>
        /// Advance exactly <paramref name="frames"/> 30 Hz flash frames, one per call. The margin
        /// keeps float error from flooring a frame short; it accumulates ~1 frame per 1000 calls,
        /// far beyond anything these tests do.
        /// </summary>
        private static void TickFrames(MagicWeaponController c, int frames)
        {
            for (int i = 0; i < frames; i++)
                c.Tick(1.001f / SimClock.FramesPerSecond,
                       Vector2.zero, Vector2.zero, new Vector2(1f, 0f));
        }

        [Test]
        public void MagicShot_SpendsBothPools()
        {
            var def  = MakeMagic(poolCost: 500f, organCost: 40f);
            var mana = new FakeMana { MagicMana = 1000f, MaxMagicMana = 1000f, ManaHp = 400f };
            var ctrl = new MagicWeaponController(new WeaponRuntimeState(def), mana);

            ctrl.BeginAttack();
            TickFrames(ctrl, 1);

            Assert.AreEqual(1, ctrl.FlushShotPlans().Count, "one projectile expected");
            Assert.AreEqual(1, mana.SpendCalls);
            Assert.AreEqual(500f, mana.MagicMana, 0.001f, "ammo@magic is spent from the budget");
            Assert.AreEqual(360f, mana.ManaHp,    0.001f, "ammo@mana wounds the organ");
        }

        [Test]
        public void MagicShot_DoesNotWearDurability()
        {
            // AS3's durability charge (Weapon.as:1596) is gated on `tip < 4 && tip != 0`, so tip 5
            // never wears. The port charged 1 per shot.
            var def   = MakeMagic(poolCost: 0f, organCost: 0f);
            var ctrl  = new MagicWeaponController(new WeaponRuntimeState(def), null);
            var state = ctrl.State;

            ctrl.BeginAttack();
            TickFrames(ctrl, 5);

            Assert.Greater(ctrl.FlushShotPlans().Count, 0, "the weapon must have fired for this to mean anything");
            Assert.AreEqual(def.maxDurability, state.CurrentDurability);
        }

        [Test]
        public void OrganTooLow_RefusesAndLocksOut()
        {
            var def  = MakeMagic(poolCost: 500f, organCost: 40f);
            var mana = new FakeMana { MagicMana = 1000f, MaxMagicMana = 1000f, ManaHp = 39f };
            var ctrl = new MagicWeaponController(new WeaponRuntimeState(def), mana);

            ctrl.BeginAttack();
            TickFrames(ctrl, 1);

            Assert.AreEqual(0, ctrl.FlushShotPlans().Count);
            Assert.AreEqual(0, mana.SpendCalls);
            Assert.Greater(ctrl.State.TRel, 0, "a resource failure arms the lockout timer (t_rel = t_prep * 3)");
        }

        [Test]
        public void PoolTooLow_RefusesAndLocksOut()
        {
            var def  = MakeMagic(poolCost: 500f, organCost: 40f);
            var mana = new FakeMana { MagicMana = 100f, MaxMagicMana = 1000f, ManaHp = 400f };
            var ctrl = new MagicWeaponController(new WeaponRuntimeState(def), mana);

            ctrl.BeginAttack();
            TickFrames(ctrl, 1);

            Assert.AreEqual(0, ctrl.FlushShotPlans().Count);
            Assert.AreEqual(0, mana.SpendCalls);
            Assert.Greater(ctrl.State.TRel, 0);
        }

        [Test]
        public void NearlyFullPool_BypassesTheCost()
        {
            // AS3: `dmagic <= owner.mana || owner.mana >= owner.maxmana * 0.99` (WMagic.as:67) — a
            // pool at 99% may fire even though the shot would overdraw it.
            var def  = MakeMagic(poolCost: 500f, organCost: 40f);
            var mana = new FakeMana { MagicMana = 99.5f, MaxMagicMana = 100f, ManaHp = 400f };
            var ctrl = new MagicWeaponController(new WeaponRuntimeState(def), mana);

            ctrl.BeginAttack();
            TickFrames(ctrl, 1);

            Assert.AreEqual(1, ctrl.FlushShotPlans().Count);
        }

        [Test]
        public void NullManaSource_FiresWithoutTracking()
        {
            var def  = MakeMagic(poolCost: 500f, organCost: 40f);
            var ctrl = new MagicWeaponController(new WeaponRuntimeState(def), null);

            ctrl.BeginAttack();
            TickFrames(ctrl, 1);

            Assert.AreEqual(1, ctrl.FlushShotPlans().Count,
                "a null source means 'no mana tracking', not 'no mana'");
        }

        [Test]
        public void NonAutoMagicWeapon_FiresOncePerPress()
        {
            // rapid 7 > 6 → IsAuto false. WMagic.as:23-27 refuses while t_auto > 0 and re-arms it, so
            // a held trigger cannot fire twice even though t_attack has run out.
            var def  = MakeMagic(poolCost: 0f, organCost: 0f, rapid: 7f);
            var ctrl = new MagicWeaponController(new WeaponRuntimeState(def), null);

            ctrl.BeginAttack();
            TickFrames(ctrl, 1);
            Assert.AreEqual(1, ctrl.FlushShotPlans().Count, "first press fires");

            TickFrames(ctrl, 30);   // t_attack runs out twice over; the tap gate must hold
            Assert.AreEqual(0, ctrl.FlushShotPlans().Count,
                "a non-auto magic weapon fires once per press, not once per `rapid`");
        }

        [Test]
        public void AutoMagicWeapon_KeepsFiringWhileHeld()
        {
            // rapid 6 → IsAuto true, so the tap gate is skipped and the weapon cycles on `rapid`.
            var def  = MakeMagic(poolCost: 0f, organCost: 0f, rapid: 6f);
            var ctrl = new MagicWeaponController(new WeaponRuntimeState(def), null);

            ctrl.BeginAttack();
            TickFrames(ctrl, 1);
            Assert.AreEqual(1, ctrl.FlushShotPlans().Count, "first shot");

            TickFrames(ctrl, 6);    // t_attack (6) runs out on the 7th frame; auto fires again
            Assert.AreEqual(1, ctrl.FlushShotPlans().Count,
                "an auto magic weapon keeps firing while the trigger is held");
        }

        // ── Skill gate (WMagic.as:46-52) ──────────────────────────────────────
        //
        // The arrangement below uses weaponLevel 12 against a magic skill of 6 purely to produce a
        // large gap. It is NOT fireball's data, though an earlier version of this comment claimed it
        // was: fireball's weaponLevel is 0. Its root is
        // `<weapon id='fireball' tip='5' cat='6' skill='6' perslvl='12'>` — no `lvl` attribute at all.
        // The 12 is `perslvl`, a separate player-level gate, and the importer reading it as `lvl` was
        // a boundary bug that silently raised the skill requirement on 24 weapons.
        // See WeaponXmlAttrsTests.

        [Test]
        public void OwnerSkillTooLow_RefusesToFire()
        {
            var def   = MakeMagic(poolCost: 0f, organCost: 0f, weaponLevel: 12, skillLevel: 6);
            var stats = new FakeStats { OwnerSkillLevel = 3 };
            var ctrl  = new MagicWeaponController(new WeaponRuntimeState(def), null, stats);

            ctrl.BeginAttack();
            TickFrames(ctrl, 3);

            Assert.AreEqual(0, ctrl.FlushShotPlans().Count,
                "a gap over 2 must refuse the cast outright (Weapon.checkAvail)");
        }

        [Test]
        public void OwnerSkillAtThreshold_Fires()
        {
            // gap == 2 is the last allowed value: `weaponLevel - ownerSkillLevel <= 2`.
            var def   = MakeMagic(poolCost: 0f, organCost: 0f, weaponLevel: 12, skillLevel: 6);
            var stats = new FakeStats { OwnerSkillLevel = 10 };
            var ctrl  = new MagicWeaponController(new WeaponRuntimeState(def), null, stats);

            ctrl.BeginAttack();
            TickFrames(ctrl, 1);

            Assert.AreEqual(1, ctrl.FlushShotPlans().Count);
        }

        [Test]
        public void NoStatSource_FailsOpenOnTheSkillGate()
        {
            // The unknown sentinel means "no owner stats", and AS3 never gates a unit without a Pers.
            var def  = MakeMagic(poolCost: 0f, organCost: 0f, weaponLevel: 12, skillLevel: 6);
            var ctrl = new MagicWeaponController(new WeaponRuntimeState(def), null, null);

            ctrl.BeginAttack();
            TickFrames(ctrl, 1);

            Assert.AreEqual(1, ctrl.FlushShotPlans().Count);
        }

        // ── Mana cost multiplier (WMagic.setPers, WMagic.as:92-98) ────────────

        [Test]
        public void ManaCostMultiplier_ScalesBothHalves()
        {
            var def  = MakeMagic(poolCost: 500f, organCost: 40f);
            var mana = new FakeMana
            {
                MagicMana = 1000f, MaxMagicMana = 1000f, ManaHp = 400f,
                ManaCostMultiplier = 0.5f,   // e.g. a warlock perk halving the spend
            };
            var ctrl = new MagicWeaponController(new WeaponRuntimeState(def), mana);

            ctrl.BeginAttack();
            TickFrames(ctrl, 1);

            Assert.AreEqual(1, ctrl.FlushShotPlans().Count);
            Assert.AreEqual(750f, mana.MagicMana, 0.001f, "500 * 0.5 debited from the budget");
            Assert.AreEqual(380f, mana.ManaHp,    0.001f, "40 * 0.5 wounded on the organ");
        }

        [Test]
        public void ManaCostMultiplier_IsUsedByTheGateNotJustTheSpend()
        {
            // The organ holds 30 and the raw organ cost is 40 — refused at 1.0x, allowed at 0.5x (20).
            // This is the half that would break if the gate read the raw attribute while the spend
            // read the scaled one.
            var def = MakeMagic(poolCost: 500f, organCost: 40f);

            var refuse = new FakeMana { MagicMana = 1000f, MaxMagicMana = 1000f, ManaHp = 30f, ManaCostMultiplier = 1f };
            var ctrlA  = new MagicWeaponController(new WeaponRuntimeState(def), refuse);
            ctrlA.BeginAttack();
            TickFrames(ctrlA, 1);
            Assert.AreEqual(0, ctrlA.FlushShotPlans().Count, "40 > 30 organ — must refuse");

            var allow = new FakeMana { MagicMana = 1000f, MaxMagicMana = 1000f, ManaHp = 30f, ManaCostMultiplier = 0.5f };
            var ctrlB = new MagicWeaponController(new WeaponRuntimeState(def), allow);
            ctrlB.BeginAttack();
            TickFrames(ctrlB, 1);
            Assert.AreEqual(1, ctrlB.FlushShotPlans().Count, "20 <= 30 organ — must fire");
        }

        // ── Content contract — the imported assets, not the rule ──────────────
        //
        // These read the real `Resources/Weapons` assets, so they fail if the importer ever stops
        // reading `ammo@magic` / `ammo@mana`. They were withheld until the re-bake had run: before it,
        // every magic asset held 0/0 and they would have been red for a reason unrelated to the rule.

        [Test]
        public void ImportedMagicWeapons_CarryBothManaCosts()
        {
            var fireball = Resources.Load<WeaponDefinition>("Weapons/fireball");
            Assert.IsNotNull(fireball, "Weapons/fireball must be importable from Resources");
            Assert.AreEqual(500f, fireball.magicPoolCost,  0.001f, "AS3 fireball ammo@magic='500'");
            Assert.AreEqual(40f,  fireball.manaHealthCost, 0.001f, "AS3 fireball ammo@mana='40'");

            var eclipse = Resources.Load<WeaponDefinition>("Weapons/eclipse");
            Assert.IsNotNull(eclipse);
            Assert.AreEqual(800f, eclipse.magicPoolCost,  0.001f, "AS3 eclipse ammo@magic='800'");
            Assert.AreEqual(120f, eclipse.manaHealthCost, 0.001f, "AS3 eclipse ammo@mana='120'");

            // mray is the fractional case: mana='1.5' must not be truncated to an int anywhere.
            var mray = Resources.Load<WeaponDefinition>("Weapons/mray");
            Assert.IsNotNull(mray);
            Assert.AreEqual(25f,  mray.magicPoolCost,  0.001f, "AS3 mray ammo@magic='25'");
            Assert.AreEqual(1.5f, mray.manaHealthCost, 0.001f, "AS3 mray ammo@mana='1.5'");
        }

        /// <summary>The nine supportive spells, as AS3 spells them in AllData.as:4017-4025.</summary>
        private static readonly string[] NineSpellIds =
        {
            "sp_slow", "sp_mwall", "sp_blast", "sp_cryst", "sp_kdash",
            "sp_mshit", "sp_moon", "sp_gwall", "sp_invulner",
        };

        [Test]
        public void EveryAssaultMagicWeapon_CarriesAManaCost()
        {
            // The nine `sp_*` weapons legitimately have no <ammo> node at all — they are cast, not
            // fired (see the magic audit §1). Every OTHER tip==5 weapon must carry a cost: before this
            // pass all 23 carried 0/0 and firing one was free.
            //
            // The real discriminator is `WeaponDefinition.spell` (AS3 weapon@spell). The id set is a
            // TRANSITIONAL fallback for the window before the assets carry that flag — the importer
            // writes it, so it appears only after the owner re-runs
            // `PFE/Data/Import Weapons from AllData.as`. Delete `spellStubs` once
            // SpellFlagIsAllOrNothing_AcrossTheNineSpellWeapons reports 9: the flag then carries this
            // fact alone, and a second hand-maintained copy is how the two drift apart.
            var spellStubs = new HashSet<string>(NineSpellIds);

            var magic = Resources.LoadAll<WeaponDefinition>("Weapons")
                .Where(d => d.weaponType == WeaponType.Magic)
                .ToArray();

            Assert.GreaterOrEqual(magic.Length, 20, "AllData.as has 23 tip==5 weapons");

            int assault = 0;
            foreach (var d in magic)
            {
                if (d.spell || spellStubs.Contains(d.weaponId)) continue;
                assault++;
                if (d.magicPoolCost <= 0f && d.manaHealthCost <= 0f)
                    Assert.Fail($"'{d.weaponId}' carries neither a magic nor a mana cost — the importer " +
                                "has stopped reading ammo@magic / ammo@mana.");
            }

            Assert.GreaterOrEqual(assault, 14,
                "the assault set is 14 weapons; fewer means weaponType is no longer importing tip==5");
        }

        /// <summary>
        /// AS3 <c>weapon@spell</c> must be all-or-nothing across the nine supportive spells.
        ///
        /// <para>Two states are legitimate and both are accepted: <b>0</b> flagged — the assets still
        /// predate the re-bake that writes the flag — and <b>9</b> flagged, meaning it ran. Anything
        /// in between is a partial import, which is the failure this guards: it would reach play as
        /// "some spells fire projectiles and some don't", which reads like a gameplay bug rather than
        /// an import one.</para>
        ///
        /// <para>The presence check on the nine ids is deliberately outside that branch — it holds in
        /// both states, and it is the assertion that fails if the block matcher ever regresses and the
        /// self-closing spell weapons stop being imported.</para>
        /// </summary>
        [Test]
        public void SpellFlagIsAllOrNothing_AcrossTheNineSpellWeapons()
        {
            var magic = Resources.LoadAll<WeaponDefinition>("Weapons")
                .Where(d => d.weaponType == WeaponType.Magic)
                .ToArray();

            // Independent of the flag: the nine must exist as tip==5 assets either way.
            foreach (var id in NineSpellIds)
                Assert.IsTrue(magic.Any(d => d.weaponId == id),
                    $"'{id}' must be imported as a tip==5 weapon — the nine self-closing spell " +
                    "weapons are the ones the old block matcher dropped.");

            int flagged = magic.Count(d => d.spell);

            Assert.IsTrue(flagged == 0 || flagged == 9,
                $"AS3 sets weapon@spell on exactly the nine sp_* weapons; found {flagged} tip==5 " +
                "weapons carrying it. 0 means the assets predate the re-bake; 9 means it ran. " +
                "Anything else is a partial import — re-run PFE/Data/Import Weapons from AllData.as.");

            // No weapon outside the nine may carry the flag, in either state.
            foreach (var d in magic.Where(x => x.spell))
                CollectionAssert.Contains(NineSpellIds, d.weaponId,
                    $"'{d.weaponId}' carries weapon@spell but is not one of the nine supportive spells");
        }
    }

    /// <summary>
    /// Pins AS3 <c>Weapon.animated</c> (<c>Weapon.as:84</c>) — the flag that decides whether firing
    /// arms the shoot-animation lockout <c>t_shoot</c>. Two subclasses clear it, by different rules,
    /// and the port honoured neither:
    ///
    /// <list type="bullet">
    /// <item><c>WMagic</c> clears it when <c>prep</c> is set (<c>WMagic.as:15-18</c>) — a charge-up
    /// weapon's frames come from <c>gotoAndStop(t_prep)</c> instead (<c>Weapon.as:1975-1991</c>), so a
    /// <c>gotoAndPlay("shoot")</c> would fight it. <c>mray</c> is the only weapon in AllData.as that
    /// is tip 5 <i>and</i> carries <c>prep</c>.</item>
    /// <item><c>WThrow</c> clears it outright (<c>WThrow.as:39</c>), and <c>WThrow.shoot()</c>
    /// (<c>:127-220</c>) never calls <c>super.shoot()</c> — so the only <c>t_shoot = 3</c> in the
    /// codebase (<c>Weapon.as:1600</c>) is unreachable for a thrown weapon twice over.</item>
    /// </list>
    ///
    /// <para><b>Why these live in <c>MagicManaTests.cs</c> and not their own file.</b>
    /// <c>PFE.Tests.csproj</c> is Unity-generated with one explicit <c>&lt;Compile Include&gt;</c> per
    /// test file, and the offline wall is built from a copy of that same list — so a brand-new .cs
    /// file is not compiled, and therefore not run, until the editor next regenerates the project. A
    /// guard that is never discovered protects nothing. <c>WeaponXmlBlocksTests.cs</c> already hosts
    /// two fixtures in one registered file for the same reason.</para>
    ///
    /// <para><b>These three are editor-only today, and an offline run reports them as failures.</b>
    /// They drive <c>Tick()</c>, which reaches <c>MagicWeaponController.RunAttack()</c> and
    /// <c>ThrownWeaponController</c> — and an offline host cannot JIT-compile any method whose IL
    /// mentions a Unity <c>ECall</c>. <c>RunAttack</c> contains <c>Debug.isDebugBuild</c> in a branch
    /// this fixture never takes, which is irrelevant: the failure is at JIT time, not at the call.
    /// (Measured — see the offline run notes.) The <b>whole</b> <see cref="MagicManaTests"/> fixture
    /// above has the same status, and has never run offline. So an offline failure here means "this
    /// host cannot run it", not "the behaviour regressed"; the guard for the rule itself was
    /// verified by invoking <c>Shoot()</c> directly, which has no ECall. Removing the
    /// <c>Debug</c> calls from the controllers behind a managed seam would make this fixture — and
    /// ~16 existing tests — runnable offline.</para>
    /// </summary>
    [TestFixture]
    public class ShootAnimationLockoutTests
    {
        /// <summary>
        /// A magic weapon whose visual HAS a shoot clip — AS3's <c>animated = true</c>
        /// (<c>Weapon.as:504-507</c>). <paramref name="prepFrames"/> is the only difference between
        /// the two magic tests below, so they are a true differential on the <c>prep</c> half.
        /// </summary>
        private static WeaponDefinition MakeMagicWithShootClip(int prepFrames)
        {
            var def = ScriptableObject.CreateInstance<WeaponDefinition>();
            def.weaponId           = "test_magic_anim";
            def.weaponType         = WeaponType.Magic;
            def.rapid              = 10f;
            def.prepFrames         = prepFrames;
            def.burstCount         = 0;
            def.projectilesPerShot = 1;
            def.deviation          = 0f;
            def.magicPoolCost      = 0f;
            def.manaHealthCost     = 0f;
            def.maxDurability      = 100;
            def.noiseRadius        = 0f;
            def.shineRadius        = 0;

            var vis = ScriptableObject.CreateInstance<WeaponVisualDefinition>();
            vis.shootFrameStart = 0;
            vis.shootFrameCount = 3;
            def.weaponVisual = vis;
            return def;
        }

        /// <summary>
        /// Advance until the weapon fires, returning the frame it fired on and the <c>TShoot</c> left
        /// behind. Stepping to the shot instead of to a fixed frame count keeps the test from encoding
        /// the charge arithmetic — which is not what is under test, and which the port's
        /// <c>t_prep += 2</c>-then-compare order already makes non-obvious.
        /// </summary>
        private static (int frame, int tShoot) RunToFirstShot(MagicWeaponController c, int maxFrames = 200)
        {
            for (int i = 1; i <= maxFrames; i++)
            {
                c.Tick(1.001f / SimClock.FramesPerSecond,
                       Vector2.zero, Vector2.zero, new Vector2(1f, 0f));
                if (c.FlushShotPlans().Count > 0)
                    return (i, c.State.TShoot);
            }
            return (0, c.State.TShoot);
        }

        [Test]
        public void ChargeUpMagicWeapon_DoesNotArmTheShootAnimation()
        {
            var def  = MakeMagicWithShootClip(prepFrames: 18);
            var ctrl = new MagicWeaponController(new WeaponRuntimeState(def), null);

            ctrl.BeginAttack();
            var (frame, tShoot) = RunToFirstShot(ctrl);

            Assert.Greater(frame, 0, "the weapon must actually fire, or this proves nothing");
            Assert.Greater(frame, 1, "prep 18 must delay the shot past the first frame");
            Assert.AreEqual(0, tShoot,
                "AS3 clears `animated` when prep is set (WMagic.as:15-18), so Weapon.as:1600 never " +
                "arms t_shoot — the presenter must fall through to its prep branch rather than show " +
                "shoot frames over the charge");
        }

        [Test]
        public void NonChargeMagicWeapon_StillArmsTheShootAnimation()
        {
            // The control for the test above: identical visual and rapid, prep = 0. Without it, the
            // first test could pass because the visual was never arranged with a shoot clip at all.
            var def  = MakeMagicWithShootClip(prepFrames: 0);
            var ctrl = new MagicWeaponController(new WeaponRuntimeState(def), null);

            ctrl.BeginAttack();
            var (frame, tShoot) = RunToFirstShot(ctrl);

            Assert.Greater(frame, 0, "the weapon must actually fire");
            Assert.Greater(tShoot, 0,
                "without prep the same visual must still play its shoot frames (Weapon.as:1600)");
        }

        [Test]
        public void ThrownWeapon_NeverArmsTheShootAnimation()
        {
            // WThrow.as:39 sets `animated = false` unconditionally — not conditionally like WMagic —
            // and WThrow.shoot() never calls super, so `t_shoot` stays 0 for the weapon's whole life.
            // The port armed it here.
            var def = ScriptableObject.CreateInstance<WeaponDefinition>();
            def.weaponId      = "test_thrown_anim";
            def.weaponType    = WeaponType.Thrown;
            def.rapid         = 10f;
            def.magazineSize  = 0;      // -> the controller's DefaultKolAmmo, so the throw can happen
            def.maxDurability = 100;
            def.fuseFrames    = 75;

            var ctrl = new ThrownWeaponController(new WeaponRuntimeState(def));
            ctrl.BeginAttack();
            ctrl.Tick(1.001f / SimClock.FramesPerSecond,
                      Vector2.zero, Vector2.zero, new Vector2(1f, 0f));

            Assert.Greater(ctrl.FlushShotPlans().Count, 0, "the weapon must actually throw");
            Assert.AreEqual(0, ctrl.State.TShoot,
                "AS3 never arms t_shoot for a thrown weapon (WThrow.as:39, and WThrow.shoot() skips " +
                "super.shoot(), where the only `t_shoot = 3` lives)");
        }
    }

    /// <summary>
    /// Pins the magic-only <c>spellsPoss</c> gate — AS3 <c>WMagic.attack()</c>'s <b>first</b> gate
    /// (<c>WMagic.as:33-39</c>), which the port did not have at all.
    ///
    /// <para><b>The state is real and reachable, which is why this is not a theoretical gate.</b> The
    /// mana organ at trauma stage 4 drives <c>spellsPoss</c> to 0
    /// (<c>CharacterStats.ApplyTraumaModifiers</c>, <c>CharacterStats.cs:2387-2392</c>), so a caster
    /// whose organ has been wrecked must be refused outright. The port cast anyway.</para>
    ///
    /// <para><b>Two properties are pinned, not one.</b></para>
    /// <list type="number">
    ///   <item><description><b>The cast is refused</b>, and neither pool is debited.</description></item>
    ///   <item><description><b>The refusal happens at the oracle's position</b> — before <c>t_prep += 2</c>
    ///     and before <c>checkAvail()</c>. That is why the discriminating assertion is <c>TPrep == 0</c>
    ///     and not a lockout value: a gate placed <i>after</i> the prep charge (the plausible mistake)
    ///     would leave the charge climbing while the trigger was held, and nothing else would notice.</description></item>
    /// </list>
    ///
    /// <para><b>Editor-only, by necessity.</b> <c>RunAttack()</c> contains <c>Debug.isDebugBuild</c>
    /// calls, and an offline host cannot JIT any method whose IL mentions a Unity <c>ECall</c> —
    /// reachable or not. These three therefore fail with a <c>SecurityException</c> in the offline
    /// wall, and that failure means <i>"this host cannot run it"</i>, not <i>"the gate regressed"</i>.
    /// The predicate itself is covered offline in <c>HitAvoidanceTests.CanCastSpells_*</c>; this
    /// fixture covers the wiring and the ordering.</para>
    /// </summary>
    [TestFixture]
    public class SpellPermissionGateTests
    {
        private sealed class ManaDouble : IManaSource
        {
            public float MagicMana          { get; set; }
            public float MaxMagicMana       { get; set; }
            public float ManaHp             { get; set; }
            public float ManaCostMultiplier { get; set; } = 1f;
            public int   SpellsPossible     { get; set; } = 1;
            public int   SpendCalls         { get; private set; }

            public void SpendMana(float poolCost, float organCost) => SpendCalls++;
        }

        /// <summary>
        /// A magic weapon that <b>charges</b> before firing, so <c>TPrep</c> is a usable observable.
        /// Everything else is inert (no mana cost, no spread, one pellet) so the only thing these
        /// tests can be measuring is the gate.
        /// </summary>
        private static WeaponDefinition MakeChargingMagic()
        {
            var def = ScriptableObject.CreateInstance<WeaponDefinition>();
            def.weaponId           = "test_magic_spellgate";
            def.weaponType         = WeaponType.Magic;
            def.rapid              = 10f;
            def.prepFrames         = 18;
            def.burstCount         = 0;
            def.projectilesPerShot = 1;
            def.deviation          = 0f;
            def.magicPoolCost      = 0f;
            def.manaHealthCost     = 0f;
            def.maxDurability      = 100;
            def.noiseRadius        = 0f;
            def.shineRadius        = 0;
            def.weaponLevel        = 0;
            def.skillLevel         = 6;
            return def;
        }

        private static void Hold(MagicWeaponController c, int frames)
        {
            c.BeginAttack();
            for (int i = 0; i < frames; i++)
                c.Tick(1.001f / SimClock.FramesPerSecond,
                       Vector2.zero, Vector2.zero, new Vector2(1f, 0f));
        }

        [Test]
        public void NoSpells_RefusesTheCast_AndNeverStartsCharging()
        {
            var def  = MakeChargingMagic();
            var mana = new ManaDouble
            {
                SpellsPossible = 0,             // mana organ at trauma stage 4
                MagicMana = 1000f, MaxMagicMana = 1000f, ManaHp = 400f,
            };
            var ctrl = new MagicWeaponController(new WeaponRuntimeState(def), mana);

            Hold(ctrl, 3);

            Assert.AreEqual(0, ctrl.FlushShotPlans().Count,
                "spellsPoss == 0 must refuse the cast outright (WMagic.as:33-39)");
            Assert.AreEqual(0, mana.SpendCalls,
                "a refused cast must not debit either pool — the gate is ahead of the spend");
            Assert.AreEqual(0, ctrl.State.TPrep,
                "the oracle's gate sits BEFORE `if(t_prep < prep+10) t_prep += 2`, so a refused caster " +
                "must accumulate no charge; a gate placed after it would leave this climbing");
            Assert.AreEqual(0, ctrl.State.TRel,
                "…and it arms no lockout, unlike the mana gates at :62/:80");
        }

        [Test]
        public void SpellsAvailable_ChargesAndFires()
        {
            // The control. Byte-for-byte the same arrangement with spellsPoss = 1, the AS3 declaration
            // default (Pers.as:423). Without it the test above would also pass on a controller that
            // had stopped charging, or stopped firing, for some unrelated reason.
            var def  = MakeChargingMagic();
            var mana = new ManaDouble
            {
                SpellsPossible = 1,
                MagicMana = 1000f, MaxMagicMana = 1000f, ManaHp = 400f,
            };
            var ctrl = new MagicWeaponController(new WeaponRuntimeState(def), mana);

            Hold(ctrl, 3);
            Assert.Greater(ctrl.State.TPrep, 0,
                "with spells available the same weapon must charge — the differential against the " +
                "TPrep == 0 above");

            Hold(ctrl, 200);
            Assert.Greater(ctrl.FlushShotPlans().Count, 0, "…and must eventually fire");
        }

        [Test]
        public void NoManaSource_IsNotGated()
        {
            // AS3 guards the whole gate on `owner.player`; the port's encoding of "not a player" is
            // "no owner state". An enemy carries no CharacterStats, so it must not be refused — the
            // same fail-open contract HitAvoidance.CanFire and ManaCostMult already use.
            var def  = MakeChargingMagic();
            var ctrl = new MagicWeaponController(new WeaponRuntimeState(def), null);

            Hold(ctrl, 3);

            Assert.Greater(ctrl.State.TPrep, 0,
                "a null mana source means 'not a player', not 'spellsPoss == 0'");
        }
    }
}
