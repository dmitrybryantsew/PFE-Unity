using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.Editor.Combat
{
    /// <summary>
    /// Pins the <b>automatic-weapon</b> half of S.A.T.S.: one queued cell is a finite burst, not a
    /// held trigger that runs until the magazine is dry.
    ///
    /// <para><b>The defect this fixture exists for.</b> The minigun is authored
    /// <c>&lt;sats que='15' cons='25'/&gt;</c> with <c>&lt;ammo holder='500'/&gt;</c>. Queue one
    /// cell, accept, and the Oracle fires exactly 15 rounds: <c>Sats.act()</c> decrements
    /// <c>SatsCel.kol</c> once per round that left the barrel and pops the cell at <c>kol == 1</c>.
    /// The port instead read <c>WeaponRuntimeState.IsShoot</c> at the head of
    /// <c>PlayerWeaponLoadout.FixedUpdate</c> — but every controller clears that flag at the
    /// <i>end</i> of its own Tick, so it was always false. <c>Act()</c> never ran, the queue never
    /// emptied, and <c>BeginAttack()</c> kept a held trigger down for all 500 rounds. Independently,
    /// nothing ever called <c>EndAttack()</c> when the plan ended, so fixing only the accounting would
    /// still fire till empty.</para>
    ///
    /// <para><b>Why lints.</b> Every link here is a MonoBehaviour / component graph that cannot be
    /// constructed outside the editor. The arithmetic is pinned executably in
    /// <c>SatsRulesTests.Charges_*</c>; what those cannot see is whether the shipping call sites use
    /// it. Every assertion below is scoped to a method body and paired with an absent control or a
    /// second statement of the same list, so a blind scan cannot stay green.</para>
    /// </summary>
    [TestFixture]
    public class SatsAutomaticWeaponWiringLintTests
    {
        private const string LoadoutPath = "Systems/Weapons/PlayerWeaponLoadout.cs";
        private const string PlayerPath = "Entities/Player/PlayerController.cs";
        private const string ControllerPath = "Systems/Combat/Sats/SatsController.cs";
        private const string WeaponPath = "Systems/Combat/Sats/SatsWeapon.cs";
        private const string StatePath = "Sim/Weapons/WeaponRuntimeState.cs";
        private const string BlocksPath = "Sim/Weapons/WeaponXmlBlocks.cs";

        /// <summary>Every controller that emits a round — the writer list for <c>NoteShot()</c>.</summary>
        private static readonly string[] ShotControllers =
        {
            "Sim/Weapons/Controllers/RangedWeaponController.cs",
            "Sim/Weapons/Controllers/MagicWeaponController.cs",
            "Sim/Weapons/Controllers/UnarmedWeaponController.cs",
            "Sim/Weapons/Controllers/ThrownWeaponController.cs",
            "Sim/Weapons/Controllers/MeleeWeaponController.cs",
        };

        /// <summary>Whitespace-free view, so spacing changes cannot make a presence assertion lie.</summary>
        private static string Flat(string s) => s.Replace(" ", "").Replace("\t", "").Replace("\r", "").Replace("\n", "");

        private static string KeptSource(string relativePath)
        {
            string full = Path.Combine(SourceLint.ProjectRoot(), "Assets", "_PFE",
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.IsTrue(File.Exists(full), "Lint target missing: " + full);

            string kept = SourceLint.StripCommentsKeepStrings(File.ReadAllText(full));
            Assert.That(kept, Does.Not.Contain("///"),
                "The comment stripper did not strip — an assertion about the data attribute would " +
                "then be satisfiable by the comment that describes it.");
            return kept;
        }

        // ── 1. The charge signal is a counter, not a same-tick bool ─────────────

        /// <summary>
        /// <c>DriveSatsQueue</c> must sample <c>ShotsFired</c> and pass the counter and its baseline
        /// to <c>PreWeaponTick</c>. Reading <c>State.IsShoot</c> here is the original defect: it is
        /// cleared at the end of every controller's Tick and therefore always false here.
        /// </summary>
        [Test]
        public void Loadout_ChargesByShotCounterNotTheSameTickFlag()
        {
            string body = SourceLint.MethodBody(SourceLint.ReadStripped(LoadoutPath),
                "private void DriveSatsQueue()");

            // Absent control: proves the extractor returned the real method body.
            Assert.That(body, Does.Contain("_aimTarget"),
                "MethodBody did not return DriveSatsQueue; every assertion below would be meaningless.");

            Assert.That(body, Does.Contain("ShotsFired"),
                "DriveSatsQueue no longer samples the weapon's shot counter. Without it Sats.act() " +
                "never runs and an accepted minigun cell fires until the magazine is empty.");
            Assert.That(body, Does.Contain("PreWeaponTick(shotsNow, baseline"),
                "PreWeaponTick must receive BOTH the current counter and the previous one — the " +
                "difference is the number of rounds that left the barrel.");
            Assert.That(body, Does.Not.Contain("State.IsShoot"),
                "State.IsShoot is a same-tick flag cleared at the end of the controller's Tick. " +
                "Reading it at the head of the next FixedUpdate is always false.");
        }

        // ── 2. The held trigger is released when the plan ends ──────────────────

        /// <summary>
        /// <c>BeginAttack()</c> latches <c>_attackHeld</c> until an explicit <c>EndAttack()</c>. The
        /// Oracle simply stops calling <c>attack(true)</c> when <c>que.length</c> hits zero; the port
        /// must say so, or the last cell empties the magazine even with correct accounting.
        /// </summary>
        [Test]
        public void Loadout_ReleasesTheTriggerWhenThePlanEnds()
        {
            string source = SourceLint.ReadStripped(LoadoutPath);
            string body = SourceLint.MethodBody(source, "private void DriveSatsQueue()");
            string flat = Flat(body);

            int release = flat.IndexOf("ReleaseSatsTrigger();", StringComparison.Ordinal);
            int begin = flat.IndexOf("_current.BeginAttack();", StringComparison.Ordinal);

            Assert.That(release, Is.GreaterThanOrEqualTo(0),
                "DriveSatsQueue never releases the plan's trigger. BeginAttack() holds _attackHeld " +
                "forever, so an automatic weapon keeps firing after its cells are spent.");
            Assert.That(begin, Is.GreaterThan(release),
                "The release must be reachable on the paths BEFORE the trigger is armed — an empty " +
                "queue, a missing weapon, or an unfireable front cell.");

            // Count the release sites: four early-return paths must all let go. A single call on one
            // branch leaves the other three holding the trigger.
            int sites = 0;
            for (int i = flat.IndexOf("ReleaseSatsTrigger();", StringComparison.Ordinal);
                 i >= 0;
                 i = flat.IndexOf("ReleaseSatsTrigger();", i + 1, StringComparison.Ordinal))
            {
                sites++;
                Assert.Less(sites, 10, "Runaway count — the extractor returned more than one method.");
            }
            Assert.That(sites, Is.GreaterThanOrEqualTo(3),
                "ReleaseSatsTrigger() must run on every path where the plan is not firing this tick.");

            string releaseBody = SourceLint.MethodBody(source, "private void ReleaseSatsTrigger()");
            Assert.That(releaseBody, Does.Contain("EndAttack();"),
                "ReleaseSatsTrigger does not call EndAttack(). The latch stays down and the gun " +
                "fires until the magazine is dry.");
            Assert.That(releaseBody, Does.Contain("_satsTriggerHeld"),
                "The release is not guarded by the latch — an unrelated mouse release could cancel " +
                "a real held trigger, or the plan could call EndAttack every tick.");
        }

        // ── 3. One act() per round, not one per tick ────────────────────────────

        /// <summary>
        /// <c>PreWeaponTick</c> must charge once per new round. A single <c>if (fired) Act();</c>
        /// collapses a 15-round cell into one charge per tick and stretches the burst indefinitely.
        /// </summary>
        [Test]
        public void Controller_ChargesOneActPerRound()
        {
            string body = SourceLint.MethodBody(SourceLint.ReadStripped(ControllerPath),
                "public bool PreWeaponTick");
            string flat = Flat(body);

            Assert.That(body, Does.Contain("roundsOwed"),
                "PreWeaponTick does not bound the charge by what the queue owes — a stale counter " +
                "delta could empty a brand-new plan before it fires.");
            Assert.That(body, Does.Contain("SatsRules.ChargesForShots"),
                "PreWeaponTick no longer uses the pure charge rule.");
            Assert.That(flat, Does.Contain("for(inti=0;i<charges&&_queue.Count>0;i++)Act();"),
                "Act() must be called in a loop bounded by the number of rounds that left the " +
                "barrel. A single Act() per tick leaves kol at 15 forever on a minigun cell.");
            Assert.That(flat, Does.Contain("Mathf.Max(1,_queue[i].Kol)"),
                "The rounds-owed bound must read each cell's remaining kol.");
            Assert.That(flat, Does.Not.Contain("if(weaponFiredLastTick)Act();"),
                "The bool-latch charge is back — one act() per tick, the original defect.");
        }

        // ── 4. Every firing controller records its shot ────────────────────────

        /// <summary>
        /// A controller that emits a round but does not increment <c>ShotsFired</c> stalls the plan
        /// just as surely as the old bool did.
        ///
        /// <para><b>The discovered set is the second statement of the list.</b> A hand-written array
        /// alone goes stale silently: add a sixth controller that fires and the array still has five
        /// green entries. Scanning the directory for every controller that mentions <c>IsShoot</c> and
        /// comparing the two sets makes a new writer a red test.</para>
        /// </summary>
        [Test]
        public void EveryFiringController_IncrementsTheShotCounter()
        {
            string dir = Path.Combine(SourceLint.ProjectRoot(), "Assets", "_PFE", "Sim", "Weapons", "Controllers");
            Assert.IsTrue(Directory.Exists(dir), "Controllers directory is missing: " + dir);

            var discovered = new List<string>();
            foreach (string file in Directory.GetFiles(dir, "*Controller.cs"))
            {
                string text = SourceLint.StripCommentsKeepStrings(File.ReadAllText(file));
                if (text.Contains("IsShoot")) discovered.Add(Path.GetFileName(file));
            }
            discovered.Sort(StringComparer.Ordinal);

            var declared = new List<string>();
            foreach (string p in ShotControllers) declared.Add(Path.GetFileName(p));
            declared.Sort(StringComparer.Ordinal);

            Assert.That(discovered, Is.EqualTo(declared),
                "The set of controllers that touch IsShoot changed. A new firing controller must " +
                "call State.NoteShot() too, or its rounds never charge a S.A.T.S. plan.");

            foreach (string path in ShotControllers)
            {
                string source = SourceLint.ReadStripped(path);
                Assert.That(source, Does.Contain("State.NoteShot();"),
                    path + " no longer records its shot. The counter stops advancing and the plan stalls.");
                Assert.That(Flat(source), Does.Not.Contain("IsShoot=true"),
                    path + " assigns IsShoot directly, bypassing the counter.");
            }

            string state = SourceLint.ReadStripped(StatePath);
            Assert.That(state, Does.Contain("public void NoteShot()"),
                "WeaponRuntimeState.NoteShot() is gone — the controllers cannot record a shot.");
            Assert.That(Flat(state), Does.Contain("ShotsFired++"));
            Assert.That(Flat(state), Does.Contain("IsShoot=true"));
            Assert.That(state, Does.Contain("public int ShotsFired;"));
        }

        // ── 5. A planning click is not a manual trigger pull ───────────────────

        /// <summary>
        /// <c>Sats.step()</c> consumes <c>keyAttack</c> while the mode is open
        /// (<c>Sats.as:185-188</c>). The port reads the same InputAction twice, so
        /// <c>HandleAttackStart</c> must swallow its half or every planning click latches
        /// <c>BeginAttack()</c> and the accepted plan inherits a held trigger.
        /// </summary>
        [Test]
        public void Player_SwallowsThePlanningClick()
        {
            string body = SourceLint.MethodBody(SourceLint.ReadStripped(PlayerPath),
                "private void HandleAttackStart()");
            string flat = Flat(body);

            Assert.That(flat, Does.Contain("SatsInvalidationReason.ManualFire"),
                "MethodBody did not return HandleAttackStart; the ordering assertion would be vacuous.");
            int swallow = flat.IndexOf("_sats.IsActive)return;", StringComparison.Ordinal);
            int begin = flat.IndexOf("_loadout?.BeginAttack();", StringComparison.Ordinal);

            Assert.That(swallow, Is.GreaterThanOrEqualTo(0),
                "HandleAttackStart no longer swallows LMB while S.A.T.S. is open. Every planning " +
                "click then latches the weapon trigger behind the mode's back.");
            Assert.That(begin, Is.GreaterThan(swallow),
                "The swallow guard must precede BeginAttack(); placed below it, the guard is dead code.");
        }

        // ── 6 & 7. The authored queue size reaches the cell ─────────────────────

        /// <summary><c>&lt;sats que='15'/&gt;</c> must survive data → extras → weapon → cell.</summary>
        [Test]
        public void SetCel_SizesTheCellFromTheAuthoredQueue()
        {
            string body = SourceLint.MethodBody(SourceLint.ReadStripped(ControllerPath),
                "public void SetCel()");
            string flat = Flat(body);

            Assert.That(body, Does.Contain("CanQueue"),
                "MethodBody did not return SetCel; the assertions below would be vacuous.");
            Assert.That(flat, Does.Contain("Kol=w.SatsQue>0?w.SatsQue:1"),
                "SatsCel.kol is no longer the authored satsQue. A minigun cell would fire one " +
                "round instead of fifteen.");
            Assert.That(flat, Does.Contain("Cons=fullPrice/(w.SatsQue>0?w.SatsQue:1)"),
                "SatsCel.cons must divide the cell price by the same round count.");

            string weapon = SourceLint.ReadStripped(WeaponPath);
            Assert.That(Flat(weapon), Does.Contain("satsQue:extras.SatsQue>0?extras.SatsQue:1"),
                "SatsWeapon.Read no longer forwards the parsed queue size.");

            string blocks = KeptSource(BlocksPath);
            Assert.That(blocks, Does.Contain("AttrI(sats, \"que\""),
                "WeaponXmlExtrasReader no longer parses the `que` attribute, so every weapon falls " +
                "back to satsQue = 1.");
        }

        /// <summary>The Oracle-literal anchor for the number 15 used in the arithmetic fixtures.</summary>
        [Test]
        public void Data_MinigunAuthorsFifteenRoundsPerCell()
        {
            string full = Path.Combine(SourceLint.ProjectRoot(), "Assets", "_PFE", "Data",
                "Resources", "Loot", "AllData.txt");
            Assert.IsTrue(File.Exists(full), "AllData.txt is missing: " + full);

            string data = File.ReadAllText(full);
            int at = data.IndexOf("<weapon id='minigun'", StringComparison.Ordinal);
            Assert.That(at, Is.GreaterThanOrEqualTo(0), "The minigun row disappeared from AllData.txt.");

            string row = data.Substring(at, Math.Min(800, data.Length - at));
            Assert.That(row, Does.Contain("<sats que='15' cons='25'/>"),
                "The minigun's S.A.T.S. cell is no longer 15 rounds. Update the fixtures and the " +
                "Oracle citation together rather than leaving the number 15 unexplained.");
            Assert.That(row, Does.Contain("holder='500'"),
                "The magazine size is the other half of the symptom: one runaway cell empties 500 rounds.");
        }
    }
}
