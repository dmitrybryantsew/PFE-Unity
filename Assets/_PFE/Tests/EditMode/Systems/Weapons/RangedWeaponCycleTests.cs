using System.Collections.Generic;
using NUnit.Framework;
using R3;
using UnityEngine;
using PFE.Core.Rng;
using PFE.Data.Definitions;
using PFE.Systems.Inventory;
using PFE.Systems.Weapons;
using PFE.Systems.Weapons.Controllers;

namespace PFE.Tests.EditMode.Systems.Weapons
{
    /// <summary>
    /// The ranged fire / jam / reload / recharge / durability cycle, ported off the retired
    /// <c>PFE.Systems.Combat.WeaponLogic</c>.
    ///
    /// <para><b>Why this fixture replaced <c>WeaponLogicTests</c>.</b> Phase 2.2 retired the legacy
    /// weapon path (<c>WeaponLogic</c> + <c>WeaponView</c> + <c>WeaponFactory</c>). Its 32-test
    /// fixture was written against a class whose <c>Fire(UnitStats)</c> returned a bool and whose
    /// public members (<c>CompleteReload</c>, <c>Repair</c>, <c>SetAmmo</c>, <c>SetDurability</c>,
    /// <c>Reset</c>, <c>GetFireRate</c>, <c>StartReloadAsync</c>) have no counterpart on the
    /// replacement: the new path is driven by <c>Tick()</c> and pulls ammunition from
    /// <see cref="IAmmoSource"/> rather than exposing a mutator for every field. So the port keeps
    /// the <i>behaviour</i> and drops the <i>API shape</i>.</para>
    ///
    /// <para><b>What was deliberately not ported, and why.</b></para>
    /// <list type="bullet">
    /// <item><description><c>CalculateDeviation_*</c> (4 tests) and <c>GetFireRate_*</c> (2) —
    /// <c>WeaponLogic</c> only delegated these to <see cref="PFE.Systems.Combat.ICombatCalculator"/>,
    /// and <c>CombatCalculatorTests</c> already asserts the formulas directly (55 tests, including
    /// <c>CalculateFireRate_RapidTen_ReturnsThreeShotsPerSecond</c>). Re-asserting them through a
    /// controller would test the delegation, not the maths. The one property that <i>is</i> new here
    /// — that a definition with <c>deviation == 0</c> produces pellets exactly on the aim axis — is
    /// kept as <see cref="Shot_WithZeroDeviation_EmitsPelletsOnTheAimAxis"/>.</description></item>
    /// <item><description><c>Repair_*</c> (3) — weapon repair is an inventory-side operation; the
    /// legacy <c>Repair(int)</c> had no production caller.</description></item>
    /// <item><description><c>Fire_AtHalfDurability_CanMisfire</c> — it asserted
    /// "ammo was consumed whether or not the shot fired", which is the opposite of what the new
    /// controller does (a misfire costs no round). Replaced by the two deterministic
    /// <c>FixedRng</c> tests below, which pin the actual jam and misfire outcomes instead of
    /// observing that a random branch ran.</description></item>
    /// <item><description><c>CompleteReload_WithPartialMagazine_RefillsToFull</c> — a duplicate of
    /// the empty-magazine case once the reload is driven through <c>StartReload()</c>.</description></item>
    /// </list>
    ///
    /// <para><b>How the controller is driven.</b> Everything happens inside <c>Tick(dt, …)</c>, at one
    /// AS3 flash frame per call (<c>dt = 1/30</c>). <c>BeginAttack</c>/<c>EndAttack</c> latch the
    /// trigger, so a "press" is: hold for N frames, release, then drain the timers. The drain matters:
    /// <c>t_attack</c> must reach 0 and the single-shot debounce <c>t_auto</c> must reach 0 before the
    /// next press can fire, so a press that is not drained silently becomes a no-op.</para>
    ///
    /// <para><b>Determinism.</b> A fresh <see cref="WeaponDefinition"/> has <c>maxDurability = 100</c>,
    /// and <c>Breaking()</c> returns 0 until durability drops below half — so a full weapon never
    /// consults the RNG for jamming at all, and ammo/pellet counts are exact. The jam and misfire
    /// paths are reached by setting durability to 25 (breaking = 0.5) and injecting a fixed RNG.</para>
    /// </summary>
    [TestFixture]
    public class RangedWeaponCycleTests
    {
        private const float Dt = 1f / 30f;   // one AS3 flash frame

        // ── Harness ───────────────────────────────────────────────────────────

        private sealed class Rig : System.IDisposable
        {
            public readonly WeaponDefinition Def;
            public readonly WeaponRuntimeState State;
            public readonly RangedWeaponController Ctrl;

            public Rig(WeaponDefinition def, IAmmoSource ammo = null, IRngService rng = null,
                       IWeaponStatSource stats = null)
            {
                Def  = def;
                State = new WeaponRuntimeState(def);
                Ctrl  = new RangedWeaponController(State, null, ammo, rng, stats);
            }

            public void Dispose()
            {
                Ctrl.Dispose();
                UnityEngine.Object.DestroyImmediate(Def);
            }
        }

        /// <summary>Every roll returns the same value, so a jam / misfire / clean shot is chosen by the test.</summary>
        private sealed class FixedRng : IRngService
        {
            private readonly float _value;
            public FixedRng(float value) => _value = value;

            public uint  NextUInt() => 0u;
            public float NextFloat() => _value;
            public int   NextInt(int maxExclusive) => 0;
            public int   Range(int minInclusive, int maxExclusive) => minInclusive;
            public float Range(float min, float max) => min;
            public bool  Chance(float probability) => _value < probability;
            public void  Shuffle<T>(IList<T> list) { }
            public IRngService GetStream(RngStream stream, int? salt = null) => this;
        }

        /// <summary>
        /// Stands in for <c>CharacterStats</c>, which cannot be built here (it is a MonoBehaviour and
        /// its <c>Awake</c> reaches for <c>Resources</c>). Every field is settable so a test can move
        /// one multiplier at a time and leave the rest at the AS3 <c>Pers</c> declaration defaults.
        /// </summary>
        private sealed class FakeWeaponStats : IWeaponStatSource
        {
            public float ReloadMult = 1f;
            public float RecoilMult = 1f;
            public float JammedMult = 1f;
            public float Recyc      = 0f;

            // Melee / unarmed family — unused by the ranged fixtures, but the interface is shared.
            public float MeleeDamMult = 1f;
            public float MeleeSpdMult = 1f;
            public float PunchDamMult = 1f;
            public float KickDestroy  = 30f;
            public float MeleeRun     = 10f;

            // Attacker-side hit procs. AS3's declaration defaults (no perk, no sneak skill) — 0
            // disables both procs, which is what these ranged fixtures assert by default.
            public float CritInvis    = 0f;
            public float Desintegr    = 0f;

            // Precision channel — AS3 Pers declaration defaults, so the composed multiplier is 1 and
            // these fixtures' precision assertions are unaffected. Kept as fields so a test can dial
            // one locomotion term without disturbing the rest.
            public float AllPrecMult = 1f;
            public float RunPenalty  = 0.5f;
            public float JumpPenalty = 0.3f;
            public float BackPenalty = 0.4f;
            public float StayBonus   = 0.3f;
            public float MazilAdd    = 0f;
            public float ComposedPrecisionMultiplier = 1f;

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
        }

        private sealed class RecordingAmmoSource : IAmmoSource
        {
            private readonly string _type;

            public int Remaining;
            public int ConsumeCalls;
            public int TotalConsumed;

            public RecordingAmmoSource(string type, int remaining)
            {
                _type     = type;
                Remaining = remaining;
            }

            public int GetAmmoCount(string ammoType) => ammoType == _type ? Remaining : 0;

            public int ConsumeAmmo(string ammoType, int amount)
            {
                ConsumeCalls++;
                if (ammoType != _type) return 0;
                int taken  = Mathf.Min(amount, Remaining);
                Remaining -= taken;
                TotalConsumed += taken;
                return taken;
            }
        }

        private static WeaponDefinition MakeDef(
            string weaponId = "test_rifle", int magazineSize = 30, int maxDurability = 100,
            float rapid = 10f, int projectilesPerShot = 1, int burstCount = 0, float reloadTime = 0f,
            float deviation = 0f, string ammoType = null, int rechargeFrames = 0)
        {
            var def = ScriptableObject.CreateInstance<WeaponDefinition>();
            def.weaponId           = weaponId;
            def.weaponType         = WeaponType.Guns;
            def.baseDamage         = 20f;
            def.rapid              = rapid;
            def.magazineSize       = magazineSize;
            def.maxDurability      = maxDurability;
            def.projectilesPerShot = projectilesPerShot;
            def.burstCount         = burstCount;
            def.reloadTime         = reloadTime;
            def.deviation          = deviation;
            def.ammoType           = ammoType;
            def.rechargeFrames     = rechargeFrames;
            return def;
        }

        /// <summary>Advance <paramref name="frames"/> flash frames and count the shots emitted.</summary>
        private static int TickFrames(Rig rig, int frames)
        {
            Vector2 hold = Vector2.zero;   // hold == aim: the weapon's spring position cannot drift
            int plans = 0;
            for (int i = 0; i < frames; i++)
            {
                rig.Ctrl.Tick(Dt, hold, hold, hold);
                plans += rig.Ctrl.FlushShotPlans().Count;
            }
            return plans;
        }

        /// <summary>Hold the trigger for <paramref name="frames"/> frames, release, then let the timers settle.</summary>
        private static int HoldFor(Rig rig, int frames)
        {
            rig.Ctrl.BeginAttack();
            int plans = TickFrames(rig, frames);
            rig.Ctrl.EndAttack();
            plans += TickFrames(rig, 4);
            return plans;
        }

        /// <summary>
        /// One trigger press for a single-shot weapon: the shot lands on the first frame, then every
        /// timer is drained so the next press is clean.
        /// </summary>
        private static int PressOnce(Rig rig)
        {
            rig.Ctrl.BeginAttack();
            int plans = TickFrames(rig, 1);
            rig.Ctrl.EndAttack();
            plans += TickFrames(rig, Mathf.CeilToInt(rig.Def.rapid) + 4);
            return plans;
        }

        // ── Initialization ────────────────────────────────────────────────────

        [Test]
        public void Constructor_StartsWithAFullMagazineAndDurability()
        {
            using var rig = new Rig(MakeDef(magazineSize: 30, maxDurability: 100));

            Assert.AreEqual(30, rig.State.CurrentAmmo, "Should start with a full magazine.");
            Assert.AreEqual(100, rig.State.CurrentDurability, "Should start at full durability.");
            Assert.IsFalse(rig.State.IsReloadingRP.Value, "Should not start mid-reload.");
            Assert.IsFalse(rig.State.NeedsReload, "A full magazine does not need a reload.");
            Assert.IsFalse(rig.State.IsBroken, "A new weapon is not broken.");
        }

        [Test]
        public void Definition_IsTheSameInstanceTheStateWasBuiltFrom()
        {
            var def = MakeDef(weaponId: "test_rifle");
            using var rig = new Rig(def);

            Assert.AreSame(def, rig.State.Def);
            Assert.AreEqual("test_rifle", rig.State.Def.weaponId);
        }

        // ── Firing ────────────────────────────────────────────────────────────

        [Test]
        public void OneTriggerPress_FiresOnce_ConsumingOneRoundAndOneDurability()
        {
            using var rig = new Rig(MakeDef(magazineSize: 30, maxDurability: 100));

            int plans = PressOnce(rig);

            Assert.AreEqual(1, plans, "One press of a single-shot weapon is one shot.");
            Assert.AreEqual(29, rig.State.CurrentAmmo, "A shot costs one round.");
            Assert.AreEqual(99, rig.State.CurrentDurability, "A shot costs one durability (AS3: hp -= 1).");
        }

        [Test]
        public void HoldingTheTrigger_FiresOnlyOnce_ForANonAutoWeapon()
        {
            // rapid 10 > 6 and autoMode 0, so IsAuto is false: the trigger is a tap, not a stream.
            using var rig = new Rig(MakeDef(rapid: 10f));

            int plans = HoldFor(rig, 90);   // three seconds of held fire

            Assert.AreEqual(1, plans,
                "A non-auto weapon fires once per press. More than one means the single-shot debounce " +
                "(t_auto) is not being refreshed while the trigger is held.");
            Assert.AreEqual(29, rig.State.CurrentAmmo);
        }

        [Test]
        public void ReleasingAndPressingAgain_FiresASecondTime()
        {
            using var rig = new Rig(MakeDef(rapid: 10f));

            int first  = PressOnce(rig);
            int second = PressOnce(rig);

            Assert.AreEqual(1, first, "First press fires.");
            Assert.AreEqual(1, second, "Second press fires — t_attack and t_auto must both have drained.");
            Assert.AreEqual(28, rig.State.CurrentAmmo);
            Assert.AreEqual(98, rig.State.CurrentDurability);
        }

        [Test]
        public void Shot_EmitsOnePlanPerPellet_WithSequentialPelletIndices()
        {
            using var rig = new Rig(MakeDef(projectilesPerShot: 5));

            rig.Ctrl.BeginAttack();
            rig.Ctrl.Tick(Dt, Vector2.zero, Vector2.zero, Vector2.zero);
            var plans = rig.Ctrl.FlushShotPlans();
            rig.Ctrl.EndAttack();

            Assert.AreEqual(5, plans.Count, "projectilesPerShot (AS3: kol) is the pellet count.");
            for (int i = 0; i < plans.Count; i++)
            {
                Assert.AreEqual(i, plans[i].PelletIndex, $"Pellet {i} must carry its own index.");
                Assert.AreEqual(5, plans[i].TotalPellets, "Every pellet knows the burst size.");
            }

            Assert.AreEqual(29, rig.State.CurrentAmmo,
                "One shot costs ammoPerShot rounds, NOT one round per pellet — the pellets are one trigger pull.");
        }

        [Test]
        public void Shot_WithZeroDeviation_EmitsPelletsOnTheAimAxis()
        {
            // deviation 0 and recoilLift 0 with hold == aim == origin: every random term must vanish,
            // so the pellet angle is exactly the weapon's own rotation (0 rad). This is what
            // CalculateDeviation_ZeroBaseDeviation_ReturnsZero used to assert on the legacy path.
            using var rig = new Rig(MakeDef(deviation: 0f, projectilesPerShot: 3));

            rig.Ctrl.BeginAttack();
            rig.Ctrl.Tick(Dt, Vector2.zero, Vector2.zero, Vector2.zero);
            var plans = rig.Ctrl.FlushShotPlans();
            rig.Ctrl.EndAttack();

            Assert.AreEqual(3, plans.Count);
            foreach (var plan in plans)
                Assert.AreEqual(0f, plan.AngleRad, 1e-5f,
                    "With deviation == 0 and no recoil lift, no pellet may be off-axis.");
        }

        [Test]
        public void Shot_WithMultiplePellets_SpreadsThemSymmetricallyAboutTheAimAxis()
        {
            // The RNG is pinned at 0.5 so the per-pellet random deviation is exactly zero and the only
            // thing left varying between pellets is the spread term. Without that, every pellet draws
            // its own random offset and the fan-out cannot be told apart from the noise.
            using var rig = new Rig(MakeDef(deviation: 4f, projectilesPerShot: 5), rng: new FixedRng(0.5f));

            rig.Ctrl.BeginAttack();
            rig.Ctrl.Tick(Dt, Vector2.zero, Vector2.zero, Vector2.zero);
            var plans = rig.Ctrl.FlushShotPlans();
            rig.Ctrl.EndAttack();

            Assert.AreEqual(5, plans.Count);

            // Odd pellet count: the middle pellet is the centre of the spread, and the pairs either
            // side must mirror it. Asserted as a shape rather than against the exact constant, because
            // the spread coefficient (deviation * PI / 360) is not yet pinned to an AS3 line.
            float centre = plans[2].AngleRad;
            float step   = plans[3].AngleRad - centre;
            Assert.Greater(step, 0f, "The pellets must fan out, not stack on the aim axis.");

            for (int i = 0; i < 5; i++)
                Assert.That(plans[i].AngleRad, Is.EqualTo(centre + (i - 2) * step).Within(1e-4f),
                    $"Pellet {i} must sit at its own offset from the centre of the spread.");
        }

        // ── Burst fire ────────────────────────────────────────────────────────

        [Test]
        public void Burst_FiresOneShotPerBurstRound_ConsumingOneRoundEach()
        {
            // AS3 dkol = 3: the burst arms t_attack = rapid * (burstCount + 1) and fires on each
            // t_attack that is a multiple of rapid inside the window — i.e. exactly burstCount shots.
            using var rig = new Rig(MakeDef(magazineSize: 30, maxDurability: 100, rapid: 10f, burstCount: 3));

            int plans = HoldFor(rig, 25);

            Assert.AreEqual(3, plans, "burstCount 3 must produce 3 shots in one trigger pull.");
            Assert.AreEqual(27, rig.State.CurrentAmmo, "The burst costs one round per shot, so 3 rounds.");
            Assert.AreEqual(97, rig.State.CurrentDurability, "The burst costs one durability per shot.");
        }

        [Test]
        public void Burst_WithInsufficientAmmo_FiresWhatItCan_AndStopsAtEmpty()
        {
            // reloadTime is non-zero so the empty magazine stays empty for the duration of the test;
            // with reloadTime 0 the controller would refill instantly and the assertion would be vacuous.
            using var rig = new Rig(MakeDef(magazineSize: 30, rapid: 10f, burstCount: 3, reloadTime: 90f));
            rig.State.CurrentAmmo = 2;

            int plans = HoldFor(rig, 25);

            Assert.AreEqual(2, plans, "A 3-round burst with 2 rounds loaded fires 2 shots, not 0 and not 3.");
            Assert.AreEqual(0, rig.State.CurrentAmmo, "The burst drains the magazine to empty.");
            Assert.IsTrue(rig.State.NeedsReload, "An empty magazine needs a reload.");
        }

        // ── Empty, broken, unlimited ──────────────────────────────────────────

        [Test]
        public void Fire_WithAnEmptyMagazine_EmitsNoShot_AndNeedsReload()
        {
            using var rig = new Rig(MakeDef(magazineSize: 3, rapid: 10f, reloadTime: 90f));

            Assert.AreEqual(1, PressOnce(rig), "First press fires.");
            Assert.AreEqual(1, PressOnce(rig), "Second press fires.");
            Assert.AreEqual(1, PressOnce(rig), "Third press fires — the magazine is now empty.");
            Assert.AreEqual(0, rig.State.CurrentAmmo);

            int plans = PressOnce(rig);

            Assert.AreEqual(0, plans, "An empty magazine must not fire.");
            Assert.IsTrue(rig.State.NeedsReload, "The weapon must report that it needs a reload.");
        }

        [Test]
        public void Fire_WhenBroken_EmitsNoShot()
        {
            using var rig = new Rig(MakeDef());
            rig.State.CurrentDurability = 0;

            int plans = HoldFor(rig, 60);

            Assert.AreEqual(0, plans, "A broken weapon never fires — not even once.");
            Assert.IsTrue(rig.State.IsBroken);
            Assert.AreEqual(30, rig.State.CurrentAmmo, "A weapon that cannot fire must not burn ammunition.");
        }

        [Test]
        public void Fire_WithUnlimitedAmmo_NeverNeedsReload()
        {
            // magazineSize 0 is AS3's "no magazine" weapon: it fires forever and never reloads.
            using var rig = new Rig(MakeDef(magazineSize: 0, maxDurability: 1000));

            Assert.AreEqual(1, PressOnce(rig));
            Assert.AreEqual(1, PressOnce(rig));

            Assert.AreEqual(0, rig.State.CurrentAmmo, "An unlimited weapon carries no magazine count.");
            Assert.IsFalse(rig.State.NeedsReload, "An unlimited weapon never needs a reload.");
            Assert.AreEqual(998, rig.State.CurrentDurability, "It still wears out one point per shot.");
        }

        // ── Jam and misfire ───────────────────────────────────────────────────

        [Test]
        public void Shoot_WithAHealthyWeapon_NeverJams_EvenWithTheWorstRng()
        {
            // breaking() is 0 at or above half durability, so the jam roll is never even consulted.
            using var rig = new Rig(MakeDef(maxDurability: 100), rng: new FixedRng(0f));

            int plans = PressOnce(rig);

            Assert.AreEqual(1, plans, "A full-durability weapon must fire even when the RNG returns 0.");
            Assert.IsFalse(rig.State.Jammed);
            Assert.AreEqual(29, rig.State.CurrentAmmo);
        }

        [Test]
        public void Shoot_WithAWornWeapon_CanJam_AndAJamEmitsNoShot()
        {
            // durability 25 of 100 → breaking = 0.5. holderSafe = max(20, magazineSize) = 30, so the
            // jam threshold is 0.5/30 ≈ 0.0167 and a roll of 0.005 lands inside it.
            // reloadTime is non-zero so the jam survives the assertion: an instant reload would
            // complete — and therefore clear the jam — within the same frame.
            using var rig = new Rig(MakeDef(magazineSize: 30, maxDurability: 100, reloadTime: 90f),
                                    rng: new FixedRng(0.005f));
            rig.State.CurrentDurability = 25;

            int plans = HoldFor(rig, 12);

            Assert.AreEqual(0, plans, "A jammed weapon emits no shot plan.");
            Assert.IsTrue(rig.State.Jammed, "The jam must be recorded so the presenter can show it.");
            Assert.AreEqual(30, rig.State.CurrentAmmo, "A jam costs no round.");
            Assert.Greater(rig.State.TReload, 0, "A jam must start the reload that clears it.");

            // AS3: jammed is set in shoot() (Weapon.as:1433) and cleared in reloadWeapon()
            // (Weapon.as:1710) — the function that FILLS the magazine. So the flag has to survive the
            // whole reload and clear only on completion. Clearing it at reload start makes it dead:
            // set and cleared in one frame, observable by nobody.
            TickFrames(rig, 90);

            Assert.IsFalse(rig.State.Jammed, "Completing the reload must clear the jam.");
            Assert.AreEqual(0, rig.State.TReload);
        }

        [Test]
        public void Shoot_WithAWornWeapon_CanMisfire_AndAMisfireCostsNoRound()
        {
            // Same worn weapon, but the roll (0.05) is past the jam threshold (0.0167) and inside the
            // misfire threshold (breaking/5 = 0.1): the weapon clicks and nothing comes out.
            //
            // This is where the new path deliberately DIVERGES from the retired WeaponLogicTests,
            // which asserted that a misfire still consumed a round and a point of durability.
            using var rig = new Rig(MakeDef(magazineSize: 30, maxDurability: 100, reloadTime: 90f),
                                    rng: new FixedRng(0.05f));
            rig.State.CurrentDurability = 25;

            int plans = HoldFor(rig, 12);

            Assert.AreEqual(0, plans, "A misfire emits no shot plan.");
            Assert.IsFalse(rig.State.Jammed, "A misfire is not a jam — the weapon is not stuck.");
            Assert.AreEqual(30, rig.State.CurrentAmmo, "A misfire costs no round.");
            Assert.AreEqual(25, rig.State.CurrentDurability, "A misfire costs no durability.");
        }

        // ── Owner stat multipliers (IWeaponStatSource) ────────────────────────
        // AS3 copies these Pers fields onto the weapon in Weapon.setPers (Weapon.as:973-977) and the
        // weapon then reads its own copy. Each test below is paired with a control at the default,
        // because a one-sided assertion cannot tell "the multiplier applied" from "the branch never
        // ran at all" — the failure mode that produced several of the port's dead statIds.

        [Test]
        public void CharacterStats_ExposesTheWeaponStatSource_SoTheFactoryCanBeHandedOne()
        {
            // Structural guard, and the one that matters most: PlayerWeaponLoadout hands the player's
            // CharacterStats straight to WeaponControllerFactory. Without this interface the four
            // multipliers still compile — the backing fields are public — but nothing can ever read
            // them, which is exactly how they were dead before.
            Assert.IsTrue(typeof(IWeaponStatSource).IsAssignableFrom(typeof(PFE.Systems.RPG.CharacterStats)),
                "CharacterStats must implement IWeaponStatSource or the weapon multipliers are inert.");
        }

        [Test]
        public void Reload_WithADoubledReloadMult_TakesTwiceAsLong_AndFinishesAtTheScaledThreshold()
        {
            // AS3 sets t_reload = round(reload * reloadMult) at initReload (Weapon.as:1939) and
            // completes the reload at t_reload == round(10 * reloadMult) (Weapon.as:1272). Both ends
            // scale, so the whole window moves together rather than merely starting later — a
            // port that only scaled the duration would finish at 10 and cut the reload short.
            var stats = new FakeWeaponStats { ReloadMult = 2f };
            using var rig = new Rig(MakeDef(magazineSize: 10, reloadTime: 90f), stats: stats);

            Assert.AreEqual(1, PressOnce(rig));
            rig.Ctrl.StartReload();

            Assert.AreEqual(180, rig.State.TReload, "round(90 * 2) = 180 frames of reload.");

            TickFrames(rig, 159);
            Assert.AreEqual(21, rig.State.TReload, "One frame short of the scaled completion threshold.");
            Assert.IsTrue(rig.State.IsReloadingRP.Value,
                "Still reloading at t_reload 21 — the unscaled threshold of 10 is not where it ends.");

            TickFrames(rig, 1);
            Assert.AreEqual(0, rig.State.TReload, "round(10 * 2) = 20 is where the reload lands.");
            Assert.IsFalse(rig.State.IsReloadingRP.Value);
        }

        [Test]
        public void Shoot_WithARaisedJammedMult_TurnsAMisfireIntoAJam_AndTheDefaultDoesNot()
        {
            // breaking = 0.5 and holderSafe = max(20, 30) = 30, so the base jam threshold is
            // 0.5/30 ≈ 0.0167 and a roll of 0.05 falls through to the misfire threshold of 0.1. At
            // jammedMult 10 that same roll is 0.05 < 0.5/30*10 ≈ 0.167 and the weapon jams instead.
            // The control run first proves the second run's jam is caused by jammedMult.
            var defaultMult = new FakeWeaponStats { JammedMult = 1f };
            using (var control = new Rig(MakeDef(magazineSize: 30, maxDurability: 100, reloadTime: 90f),
                                         rng: new FixedRng(0.05f), stats: defaultMult))
            {
                control.State.CurrentDurability = 25;
                HoldFor(control, 12);

                Assert.IsFalse(control.State.Jammed, "At jammedMult 1 a 0.05 roll is only a misfire.");
            }

            var drawback = new FakeWeaponStats { JammedMult = 10f };
            using (var rig = new Rig(MakeDef(magazineSize: 30, maxDurability: 100, reloadTime: 90f),
                                     rng: new FixedRng(0.05f), stats: drawback))
            {
                rig.State.CurrentDurability = 25;
                int plans = HoldFor(rig, 12);

                Assert.AreEqual(0, plans, "A jammed weapon emits no shot plan.");
                Assert.IsTrue(rig.State.Jammed,
                    "jammedMult scales the jam threshold, so the same roll jams. It is a drawback " +
                    "multiplier: higher means MORE jams (Pers.as:251).");
            }
        }

        [Test]
        public void Shoot_WithRecycAndEnergyAmmo_KeepsTheRound_ButBallisticAmmoDoesNot()
        {
            // AS3 Weapon.as:1586 skips the ammo deduction only when the owner has recyc > 0 AND the
            // weapon feeds batt/energ/crystal. All three runs share the same recyc and the same RNG
            // roll; the ammo type and the presence of a stat source are what change.
            var stats = new FakeWeaponStats { Recyc = 0.5f };

            using (var energy = new Rig(MakeDef(magazineSize: 30, ammoType: "energ"),
                                        rng: new FixedRng(0f), stats: stats))
            {
                PressOnce(energy);
                Assert.AreEqual(30, energy.State.CurrentAmmo,
                    "recyc 0.5 with a 0.0 roll keeps the round on an energy weapon.");
            }

            using (var ballistic = new Rig(MakeDef(magazineSize: 30, ammoType: "556"),
                                           rng: new FixedRng(0f), stats: stats))
            {
                PressOnce(ballistic);
                Assert.AreEqual(29, ballistic.State.CurrentAmmo,
                    "A ballistic weapon recycles nothing: the ammo-type guard must short-circuit " +
                    "before the roll is even drawn.");
            }

            using (var noSource = new Rig(MakeDef(magazineSize: 30, ammoType: "energ"),
                                          rng: new FixedRng(0f)))
            {
                PressOnce(noSource);
                Assert.AreEqual(29, noSource.State.CurrentAmmo,
                    "With no stat source, recyc is the AS3 declaration default of 0 and the round is spent.");
            }
        }

        [Test]
        public void Shoot_WithARecoilMult_ScalesTheRecoilWindow_ButOnlyFloorsAboveThree()
        {
            // Paired against the default so the difference is attributable to recoilMult alone.
            Assert.AreEqual(9,  RecoilAfterOneFrame(10, 1f),
                "round(10 * 1) = 10 frames of recoil, less the same-frame decrement.");
            Assert.AreEqual(19, RecoilAfterOneFrame(10, 2f),
                "round(10 * 2) = 20 frames of recoil, less the same-frame decrement.");

            // AS3 floors the window at 3 frames, but only when the weapon's own recoil exceeds 3
            // (Weapon.as:1613). Both runs round to below 3; only the first is promoted.
            Assert.AreEqual(2, RecoilAfterOneFrame(10, 0.1f),
                "recoil 10 > 3 rounds to 1, so the floor lifts it to 3 (2 after the decrement).");
            Assert.AreEqual(0, RecoilAfterOneFrame(2, 0.1f),
                "recoil 2 is not > 3, so the floor does not apply and round(0.2) = 0 stands.");
        }

        /// <summary>
        /// The recoil window as observed one frame after the shot.
        ///
        /// <para>The off-by-one is the oracle's, not the test's: AS3 calls <c>shoot()</c> from inside
        /// <c>actions()</c> and then runs <c>if(this.t_ret > 0) --this.t_ret</c> on the same pass
        /// (<c>Weapon.as:1612</c> then the timer block at <c>:1250-1277</c>), so the value anyone can
        /// observe is already one frame below the window that was computed.</para>
        /// </summary>
        private static int RecoilAfterOneFrame(int recoilFrames, float recoilMult)
        {
            var stats = new FakeWeaponStats { RecoilMult = recoilMult };
            using var rig = new Rig(MakeDef(magazineSize: 30), stats: stats);
            rig.Def.recoilFrames = recoilFrames;

            rig.Ctrl.BeginAttack();
            rig.Ctrl.Tick(Dt, Vector2.zero, Vector2.zero, Vector2.zero);
            return rig.State.TRet;
        }

        // ── Reload ────────────────────────────────────────────────────────────

        [Test]
        public void Reload_WhenTheMagazineIsFull_DoesNothing()
        {
            using var rig = new Rig(MakeDef(magazineSize: 30, reloadTime: 90f));

            rig.Ctrl.StartReload();

            Assert.AreEqual(0, rig.State.TReload, "A full magazine must not start a reload.");
            Assert.IsFalse(rig.State.IsReloadingRP.Value);
        }

        [Test]
        public void Reload_WithNoReloadTime_IsInstant()
        {
            using var rig = new Rig(MakeDef(magazineSize: 10, reloadTime: 0f));

            Assert.AreEqual(1, PressOnce(rig));
            Assert.AreEqual(9, rig.State.CurrentAmmo);

            rig.Ctrl.StartReload();

            Assert.AreEqual(10, rig.State.CurrentAmmo, "reloadTime 0 refills within the same call.");
            Assert.IsFalse(rig.State.IsReloadingRP.Value, "An instant reload must not leave the flag set.");
            Assert.AreEqual(1f, rig.State.ReloadProgressRP.Value,
                "A finished reload reports progress 1, not 0 — the legacy path reset it to 0.");
        }

        [Test]
        public void Reload_OverTime_SetsTheFlagThenClearsIt_AndReportsProgress()
        {
            // AS3: the reload completes when t_reload reaches round(10 * reloadMult) = 10, so a
            // reloadTime of 90 frames is done after 80 ticks.
            using var rig = new Rig(MakeDef(magazineSize: 10, reloadTime: 90f));

            Assert.AreEqual(1, PressOnce(rig));
            Assert.AreEqual(9, rig.State.CurrentAmmo);

            rig.Ctrl.StartReload();

            Assert.AreEqual(90, rig.State.TReload, "The reload countdown is reloadTime frames.");
            Assert.IsTrue(rig.State.IsReloadingRP.Value, "Starting a reload must raise the flag.");
            Assert.AreEqual(0f, rig.State.ReloadProgressRP.Value, "Progress starts at 0.");

            TickFrames(rig, 40);

            Assert.IsTrue(rig.State.IsReloadingRP.Value, "Halfway through, the weapon is still reloading.");
            Assert.That(rig.State.ReloadProgressRP.Value, Is.InRange(0.3f, 0.7f),
                "Progress must advance with the countdown, so the presenter can pick the right frame.");

            TickFrames(rig, 40);

            Assert.AreEqual(10, rig.State.CurrentAmmo, "The magazine is full when the reload completes.");
            Assert.IsFalse(rig.State.IsReloadingRP.Value, "Completing a reload must clear the flag.");
            Assert.AreEqual(1f, rig.State.ReloadProgressRP.Value, "Progress ends at 1.");
        }

        [Test]
        public void Reload_PullsOnlyWhatTheAmmoSourceHas()
        {
            var source = new RecordingAmmoSource("556", remaining: 7);
            using var rig = new Rig(MakeDef(magazineSize: 10, reloadTime: 0f, ammoType: "556"), ammo: source);
            rig.State.CurrentAmmo = 2;   // needs 8, only 7 exist

            rig.Ctrl.StartReload();

            Assert.AreEqual(9, rig.State.CurrentAmmo, "The reload loads whatever the source can supply.");
            Assert.AreEqual(7, source.TotalConsumed, "Exactly the available rounds are consumed.");
            Assert.AreEqual(0, source.Remaining, "The source is drained.");
        }

        [Test]
        public void Reload_FromAnEmptySource_LoadsNothing()
        {
            var source = new RecordingAmmoSource("556", remaining: 0);
            using var rig = new Rig(MakeDef(magazineSize: 10, reloadTime: 0f, ammoType: "556"), ammo: source);
            rig.State.CurrentAmmo = 2;

            rig.Ctrl.StartReload();

            Assert.AreEqual(2, rig.State.CurrentAmmo, "An empty source cannot refill anything.");
            Assert.AreEqual(0, source.ConsumeCalls, "With nothing to take, the source must not be charged.");
        }

        [Test]
        public void Reload_WithNoAmmoSource_FillsUnconditionally()
        {
            // A null IAmmoSource is the training / no-inventory case: the magazine refills for free.
            using var rig = new Rig(MakeDef(magazineSize: 10, reloadTime: 0f), ammo: null);
            rig.State.CurrentAmmo = 2;

            rig.Ctrl.StartReload();

            Assert.AreEqual(10, rig.State.CurrentAmmo, "With no inventory, the reload fills the magazine.");
        }

        // ── Self-recharge ─────────────────────────────────────────────────────

        [Test]
        public void Recharge_RefillsTheMagazineOverTime()
        {
            // rechargeFrames > 0 is the "recharg" ammo type: the weapon never reloads from inventory,
            // it ticks one round back in every rechargeFrames ticks once t_attack has drained.
            //
            // Driven with HoldFor, not PressOnce: PressOnce drains rapid + 4 = 14 frames, which is
            // exactly long enough for this 5-frame recharge to complete — the "the shot spent a
            // round" assertion would then fail on a weapon that had already refilled itself.
            using var rig = new Rig(MakeDef(magazineSize: 3, rapid: 10f, rechargeFrames: 5, reloadTime: 0f));

            int plans = HoldFor(rig, 1);
            Assert.AreEqual(1, plans, "One trigger press fires one shot.");
            Assert.AreEqual(2, rig.State.CurrentAmmo, "The shot spent one of the three rounds.");

            // t_attack is still draining, so the recharge timer has not started.
            TickFrames(rig, 5);
            Assert.AreEqual(2, rig.State.CurrentAmmo, "No round may return while t_attack is still running.");

            TickFrames(rig, 25);
            Assert.AreEqual(3, rig.State.CurrentAmmo, "The weapon must recharge back to a full magazine.");
            Assert.IsFalse(rig.State.NeedsReload);
        }

        // ── Reactive surface (what the HUD binds to) ──────────────────────────

        [Test]
        public void CurrentAmmoReactiveProperty_TracksFiring()
        {
            using var rig = new Rig(MakeDef(magazineSize: 30));
            var seen = new List<int>();
            var sub  = rig.State.CurrentAmmoRP.Subscribe(seen.Add);

            PressOnce(rig);

            Assert.IsTrue(seen.Contains(29),
                "The ammo ReactiveProperty must publish the new count — the HUD reads nothing else.");
            sub.Dispose();
        }

        [Test]
        public void CurrentDurabilityReactiveProperty_TracksFiring()
        {
            using var rig = new Rig(MakeDef(maxDurability: 100));
            var seen = new List<int>();
            var sub  = rig.State.CurrentDurabilityRP.Subscribe(seen.Add);

            PressOnce(rig);

            Assert.IsTrue(seen.Contains(99), "The durability ReactiveProperty must publish the new value.");
            sub.Dispose();
        }
    }
}
