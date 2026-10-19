using NUnit.Framework;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.Editor.Combat
{
    /// <summary>
    /// Pins the <b>wiring</b> of the corpse fix — the one-word predicate each damage source gates on.
    ///
    /// <para><b>Why a source lint and not a behavioural test.</b> The bug lived in five call sites, and
    /// every one of them needs a <c>GameObject</c>, a collider and a physics step to exercise:
    /// <c>Projectile.HandleImpact</c> needs a round in flight, <c>Detonate</c> needs an overlap query,
    /// <c>StepMotor</c> needs a motor-driven unit. Outside the editor those throw before reaching an
    /// assertion, so a behavioural guard would be red for reasons unrelated to the fix — and the
    /// regression being guarded is textual: <c>IsAlive</c> where the oracle says <c>sost == 4</c>.
    /// <see cref="SourceLint"/> strips comments and strings first, so a comment that <i>names</i> the
    /// call cannot satisfy a presence check, and cannot break an absence check.</para>
    ///
    /// <para><b>Every assertion is paired.</b> A presence check alone passes on a body extracted from
    /// the wrong place; an absence check alone passes on an empty string. So each lint asserts the
    /// needle it wants <i>and</i> a known-present token proving the extracted region is the right one.
    /// <see cref="MethodBody_ScopesToTheNamedMethod"/> is the control for the extractor itself.</para>
    /// </summary>
    [TestFixture]
    public class CorpseDamageWiringLintTests
    {
        private const string ProjectilePath = "Entities/Weapons/Projectile.cs";
        private const string DamageSystemPath = "Systems/Combat/DamageSystem.cs";

        private static string Projectile() => SourceLint.ReadStripped(ProjectilePath);

        // ── The control: the extractor must scope, or every lint below is vacuous ──

        [Test]
        public void MethodBody_ScopesToTheNamedMethod()
        {
            string projectile = Projectile();

            string homing = SourceLint.MethodBody(projectile, "private void ApplyHoming(float dt, Vector2 selfPosition)");

            Assert.That(homing, Does.Contain("OverlapCircleAll"), "the extractor must find ApplyHoming");
            Assert.That(homing, Does.Not.Contain("ApplyDirectDamage"),
                "if MethodBody returned the whole file, every scoped assertion in this fixture would " +
                "pass or fail for the wrong reason. ApplyDirectDamage is called from HandleImpact and " +
                "nowhere near ApplyHoming, so finding it here means the scoping is broken.");
        }

        // ── Projectile: the site where both symptoms were born ──────────────────

        [Test]
        public void HandleImpact_GatesOnCanBeHit_NotIsAlive()
        {
            string body = SourceLint.MethodBody(Projectile(), "private bool HandleImpact(Collider2D other, Vector3 impactPos)");

            Assert.That(body, Does.Contain("ApplyDirectDamage"),
                "positive control: this has to be HandleImpact's real body");
            Assert.That(body, Does.Contain("damageable.CanBeHit"),
                "a corpse must reach the damage block — sost == 3 passes the oracle's sost == 4 filter");

            // The exact regression. Gating the block on IsAlive skipped the damage AND the whole
            // `_penetration` block below it, which is why one bug produced both "infinite bullets"
            // and "piercing does not work on corpses".
            Assert.That(body, Does.Not.Contain("damageable.IsAlive"),
                "HandleImpact must not gate on IsAlive: that is the bug, and it silently takes the " +
                "penetration block with it.");
        }

        [Test]
        public void Detonate_GatesOnCanBeHit_NotIsAlive()
        {
            string body = SourceLint.MethodBody(Projectile(), "private void Detonate(Vector3 centre)");

            Assert.That(body, Does.Contain("OverlapCircleAll"), "positive control for the blast loop");
            Assert.That(body, Does.Contain("damageable.CanBeHit"),
                "AS3's blast loop tests sost == 4 too (Bullet.as:756, :809)");
            Assert.That(body, Does.Not.Contain("damageable.IsAlive"));
        }

        [Test]
        public void Homing_StillIgnoresCorpses()
        {
            // The deliberate divergence from a blanket find-and-replace: a weapon must not lock onto
            // a body. If someone "fixes" every IsAlive in the file, homing rounds start steering into
            // corpses, which is worse than the bug being fixed.
            string homing = SourceLint.MethodBody(Projectile(), "private void ApplyHoming(float dt, Vector2 selfPosition)");

            Assert.That(homing, Does.Contain("dmg.IsAlive"),
                "target acquisition is a different question from targetability: a corpse is shootable " +
                "but must not be tracked.");
        }

        // ── DamageSystem: the authority every projectile and blast reports through ──

        [Test]
        public void DamageSystem_GatesBothReportAndApplyOnCanBeHit()
        {
            string source = SourceLint.ReadStripped(DamageSystemPath);

            string report = SourceLint.MethodBody(source, "public DamageVerdict Report(in PendingDamage hit)");
            Assert.That(report, Does.Contain("!target.CanBeHit"));
            Assert.That(report, Does.Not.Contain("!target.IsAlive"),
                "the report-time gate is the one that used to drop every corpse hit before it was " +
                "even rolled against");

            string apply = SourceLint.MethodBody(source, "private DamageVerdict Apply(in PendingDamage hit, IRngService rng)");
            Assert.That(apply, Does.Contain("!target.CanBeHit"));
            Assert.That(apply, Does.Not.Contain("!target.IsAlive"),
                "the drain-time gate: a target killed by an earlier hit in the same drain is a body, " +
                "not a removal");
        }

        [Test]
        public void DamageSystem_StillWithholdsNumbersFromCorpses()
        {
            // The counterpart, and the reason a blanket replacement would be wrong: AS3 only shows a
            // floating number while `sost == 1 || sost == 2` (Unit.as:3683), so the number feed must
            // keep reading IsAlive even though the damage gate no longer does.
            string source = SourceLint.ReadStripped(DamageSystemPath);

            Assert.That(source, Does.Contain("showsDamageNumber = target.IsAlive"),
                "a body takes damage but must not print a damage number");
        }

        // ── The other three damage sources ──────────────────────────────────────

        [Test]
        public void MeleeContact_GatesOnCanBeHit()
        {
            string body = SourceLint.MethodBody(
                SourceLint.ReadStripped("Entities/Weapons/MeleeHitVolume.cs"),
                "private void OnTriggerEnter2D(Collider2D other)");

            Assert.That(body, Does.Contain("damageable.CanBeHit"),
                "AS3's contact attack reaches udarUnit (Unit.as:4125), which has no sost test at all");
        }

        [Test]
        public void ThrownObjectBlast_GatesOnCanBeHit()
        {
            // The loop is in RunPulse, not Detonate: Detonate is the entry point and delegates the
            // damage to RunPulse(0) (and to further pulses for a sustained blast). Targeting Detonate
            // would extract a body that never contained the loop and assert against nothing.
            string body = SourceLint.MethodBody(
                SourceLint.ReadStripped("Entities/Weapons/ThrownObject.cs"),
                "private void RunPulse(int pulseIndex)");

            Assert.That(body, Does.Contain("OverlapCircleAll"), "positive control for the blast loop");
            Assert.That(body, Does.Contain("damageable.CanBeHit"),
                "a grenade blast damages a body, like every other blast");
        }

        [Test]
        public void MineBlast_GatesOnCanBeHit_ButArmingDoesNot()
        {
            string source = SourceLint.ReadStripped("Entities/Weapons/MineObject.cs");

            Assert.That(source, Does.Contain("damageable.CanBeHit"),
                "the blast loop must damage a corpse");

            // The control that the replacement was targeted, not blanket: a mine must not ARM on a
            // corpse, and that check is a different question from the blast's.
            Assert.That(source, Does.Contain("!unit.IsAlive"),
                "a dead unit must not trigger a mine — arming reads IsAlive deliberately");
        }

        // ── The predicate itself ────────────────────────────────────────────────

        [Test]
        public void CanBeHit_IsADisjunction_NotIsAlive()
        {
            string source = SourceLint.ReadStripped("Entities/Units/UnitController.cs");

            Assert.That(source, Does.Contain("CanBeHit => IsAlive || IsCorpse"),
                "CanBeHit must answer for both sost == 1 and sost == 3. If it collapses back to " +
                "IsAlive, every damage source silently skips corpses again and nothing else goes red.");
            Assert.That(source, Does.Contain("IsInWorld => IsAlive || IsCorpse"),
                "IsInWorld is the sost < 4 question and must agree");
        }

        [Test]
        public void IDamageable_DeclaresCanBeHit()
        {
            string source = SourceLint.ReadStripped("Sim/Combat/Interfaces/IDamageable.cs");

            // Stripped, so the interface's long remarks naming the member cannot satisfy this.
            Assert.That(source, Does.Contain("bool CanBeHit"),
                "the member has to be on the interface, or a caller resolving IDamageable cannot ask " +
                "the question and falls back to IsAlive by omission");
        }

        // ── The dual-driver trap ────────────────────────────────────────────────

        [Test]
        public void DissolveIsTickedByBothUnitDrivers()
        {
            // A motor-driven unit never reaches UnitController.StepUnit — TilePhysicsController.
            // StepMotor owns its step. A dissolve ticked from only one of them freezes for every
            // spawned enemy in the shipped configuration (unitMotor: 1), and nothing goes red.
            string motor = SourceLint.MethodBody(
                SourceLint.ReadStripped("Systems/Physics/TilePhysicsController.cs"),
                "private void StepMotor()");

            Assert.That(motor, Does.Contain("TickEffects"), "positive control: this is StepMotor's body");
            Assert.That(motor, Does.Contain("TickCorpse"),
                "motor-driven units are the shipped default; without this hook their bodies never " +
                "finish dissolving");

            string unit = SourceLint.MethodBody(
                SourceLint.ReadStripped("Entities/Units/UnitController.cs"),
                "protected virtual void StepUnit()");

            Assert.That(unit, Does.Contain("TickCorpse"),
                "and the motor-less driver needs it too, or the other half of the units never finish");
        }
    }
}
