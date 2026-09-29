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

            public Rig(WeaponDefinition def, IAmmoSource ammo = null, IRngService rng = null)
            {
                Def  = def;
                State = new WeaponRuntimeState(def);
                Ctrl  = new RangedWeaponController(State, null, ammo, rng);
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
            // reloadTime is non-zero so the jam survives: an instant reload would clear the flag.
            using var rig = new Rig(MakeDef(magazineSize: 30, maxDurability: 100, reloadTime: 90f),
                                    rng: new FixedRng(0.005f));
            rig.State.CurrentDurability = 25;

            int plans = HoldFor(rig, 12);

            Assert.AreEqual(0, plans, "A jammed weapon emits no shot plan.");
            Assert.IsTrue(rig.State.Jammed, "The jam must be recorded so the presenter can show it.");
            Assert.AreEqual(30, rig.State.CurrentAmmo, "A jam costs no round.");
            Assert.Greater(rig.State.TReload, 0, "A jam must start the reload that clears it.");
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
            // it ticks one round back in every rechargeFrames once t_attack has drained.
            using var rig = new Rig(MakeDef(magazineSize: 3, rapid: 10f, rechargeFrames: 5, reloadTime: 0f));

            Assert.AreEqual(1, PressOnce(rig));
            Assert.AreEqual(2, rig.State.CurrentAmmo, "The shot spent one of the three rounds.");

            TickFrames(rig, 30);

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
