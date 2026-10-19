using NUnit.Framework;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.Editor.Combat
{
    /// <summary>
    /// Pins the <b>wiring</b> of the contact-attack shove and of the motor seam the bullet knockback
    /// needed — the two halves of "a hit moves the thing it lands on" that were each written and then
    /// read by nothing.
    ///
    /// <para><b>Why a source lint and not a behavioural test.</b> Both defects are of the same shape as
    /// the ones <c>CorpseDamageWiringLintTests</c> guards: the code that computes the right number
    /// existed, was correct, and was never reached. <c>TryContactAttack</c> needs two colliders, a
    /// <c>DamageSystem</c> and a component graph to run, and the knockback defect needs a unit that is
    /// motor-driven <i>and</i> being shot — so outside the editor a behavioural guard throws before it
    /// asserts anything, and inside it the arrange step is a whole room. The regressions being guarded
    /// are textual: a missing call, and <c>AddForce</c> where the motor seam belongs.
    /// <see cref="SourceLint"/> strips comments and strings first, so a doc comment that <i>names</i>
    /// the call it checks for cannot satisfy the check.</para>
    ///
    /// <para><b>Every assertion is paired.</b> A presence check alone passes on a body extracted from
    /// the wrong method; an absence check alone passes on an empty string. Each lint below therefore
    /// asserts a token it wants <i>and</i> a known-present token proving the extracted region is the
    /// right one, and <see cref="MethodBody_ScopesToTheNamedMethod"/> is the control for the extractor
    /// itself.</para>
    /// </summary>
    [TestFixture]
    public class ContactCollisionWiringLintTests
    {
        private const string UnitControllerPath = "Entities/Units/UnitController.cs";
        private const string BeastPath = "Entities/Enemies/Archetypes/Beast/BeastController.cs";
        private const string ZombiePath = "Entities/Enemies/Archetypes/ZombieController.cs";
        private const string ArmedShooterPath = "Entities/Enemies/Archetypes/ArmedShooter/ArmedShooterController.cs";
        private const string BossPath = "Entities/Enemies/Bosses/BossController.cs";

        private static string UnitController() => SourceLint.ReadStripped(UnitControllerPath);

        // ── The control: the extractor must scope, or every lint below is vacuous ──

        [Test]
        public void MethodBody_ScopesToTheNamedMethod()
        {
            string source = UnitController();

            string body = SourceLint.MethodBody(
                source, "private void ApplyContactCollisionExchange(UnitController target)");

            Assert.That(body, Does.Contain("ResolveExchange"),
                "the extractor must find ApplyContactCollisionExchange");

            Assert.That(body, Does.Not.Contain("RollContactAttackVariance"),
                "if MethodBody returned the whole file, every scoped assertion in this fixture would " +
                "pass or fail for the wrong reason. The spread draw belongs to TryContactAttack, so " +
                "finding it here means the scoping is broken.");
        }

        // ── The shove: called, in the oracle's position in the order ─────────────

        /// <summary>
        /// AS3 draws the damage spread at <c>:4150</c>, exchanges at <c>:4151-4159</c>, and only then
        /// calls <c>damage()</c> at <c>:4162</c>. The port must keep that order: the exchange reads and
        /// writes velocities the damage must not see, and the spread draw must stay the first thing the
        /// hit does.
        /// </summary>
        [Test]
        public void TryContactAttack_ExchangesMomentum_BetweenTheSpreadAndTheDamage()
        {
            string body = SourceLint.MethodBody(
                UnitController(), "public virtual bool TryContactAttack(UnitController target, float damageScale = 1f)");

            Assert.That(body, Does.Contain("GrantContactInvulnerability"),
                "positive control: this has to be TryContactAttack's real body");

            Assert.That(body, Does.Contain("ApplyContactCollisionExchange(target)"),
                "a contact hit must shove — AS3 udarUnit:4151-4159. Without this call the whole " +
                "mechanic is dead even though UnitCollisionImpulseMath is correct.");

            int spread = body.IndexOf("RollContactAttackVariance");
            int exchange = body.IndexOf("ApplyContactCollisionExchange");
            int report = body.IndexOf("_damageSystem.Report");

            Assert.Greater(spread, -1, "the spread draw is AS3 :4150 and must still be here");
            Assert.Greater(exchange, spread,
                "the exchange comes AFTER the spread draw (Unit.as:4150 then :4151)");
            Assert.Greater(report, exchange,
                "and BEFORE damage(), which is the Report call (Unit.as:4162) — damage() must not be " +
                "able to read the post-exchange velocity");
        }

        /// <summary>
        /// The gate is <c>if(param1.collisionTip == 1)</c> — read on the attacker, in the exchange, and
        /// nowhere else. A gate on the target would make every slime in the game immovable and every
        /// attacker of a slime recoil.
        /// </summary>
        [Test]
        public void TheExchange_GatesOnTheAttackersCollisionTip()
        {
            string body = SourceLint.MethodBody(
                UnitController(), "private void ApplyContactCollisionExchange(UnitController target)");

            Assert.That(body, Does.Contain("if (!CollisionTip)"),
                "the flag is the ATTACKER's — AS3's `param1.collisionTip`, and `param1` is the striker");
            Assert.That(body, Does.Not.Contain("target.CollisionTip"),
                "reading it on the target is the plausible inversion: it type-checks, it runs, and it " +
                "silently makes five enemy classes immovable instead of making them not shove");

            Assert.That(body, Does.Contain("SetVelocityPixelsPerFrame"),
                "the exchange REPLACES both velocities (AS3 `dx = …`); it is not an impulse");
            Assert.That(body, Does.Not.Contain("ApplyKnockback"),
                "ApplyKnockback is additive and belongs to otbros. Routing the exchange through it " +
                "would let two bodies gain energy on every contact.");
        }

        // ── The bullet knockback: the motor seam it was missing ──────────────────

        /// <summary>
        /// <b>The regression this exists for.</b> <c>ApplyKnockback</c> used to be
        /// <c>AddForce(impulse)</c>, i.e. <c>_velocity += impulse</c>. <c>_velocity</c> is the
        /// motor-less storage; the player is always motor-driven, so every shot, blast and throw at the
        /// player was computed, converted to the right unit, written — and then dropped. The mechanic
        /// visibly worked on everything the player shot, which is why it survived.
        ///
        /// <para>The fix is the seam, not a second conversion: <c>AddVelocityPixelsPerFrame</c> routes
        /// to the motor when there is one and to <c>_velocity</c> when there is not, and it already
        /// owns the px/frame ↔ units/s conversion. Writing <c>_velocity</c> directly here is exactly
        /// the bug.</para>
        /// </summary>
        [Test]
        public void ApplyKnockback_RoutesThroughTheMotorSeam_NotThroughAddForce()
        {
            string body = SourceLint.MethodBody(
                UnitController(), "public virtual void ApplyKnockback(Vector2 impulse)");

            Assert.That(body, Does.Contain("AddVelocityPixelsPerFrame"),
                "the impulse must go through the seam that knows whether a motor owns the velocity");

            Assert.That(body, Does.Not.Contain("AddForce("),
                "AddForce writes _velocity, which a motor-driven unit never reads — that is the bug " +
                "this lint was written for, and it is invisible in play because every NPC is " +
                "motor-less and therefore still gets thrown.");

            Assert.That(body, Does.Not.Contain("_velocity"),
                "and it must not reach past the seam to the storage either");
        }

        // ── collisionTip: five classes answer false, and the rest must not ───────

        /// <summary>
        /// The five ported classes that set <c>collisionTip = 0</c> in their AS3 constructors. A
        /// missing override is not a crash and not a red fixture anywhere else — it is a slime that
        /// shoves, which reads as "the slime is heavier than it looks" and never gets reported.
        /// </summary>
        [TestCase(BeastPath, "public class SlimeController : BeastController", "UnitSlime.as:65")]
        [TestCase(ZombiePath, "public sealed class SpectreController : EnemyController", "UnitSpectre.as:33")]
        [TestCase(ArmedShooterPath, "public class NecrosController : ArmedShooterController", "UnitNecros.as:29")]
        [TestCase(BossPath, "public class BossDronController : BossController", "UnitBossDron.as:60")]
        [TestCase(BossPath, "public class ThunderHeadController : BossController", "UnitThunderHead.as:147")]
        public void TheFiveClassesThatDisableTheExchange_SaySo(string path, string classDeclaration, string oracle)
        {
            string body = SourceLint.MethodBody(SourceLint.ReadStripped(path), classDeclaration);

            Assert.That(body, Does.Contain("public override bool CollisionTip => false;"),
                $"{classDeclaration} sets collisionTip = 0 ({oracle}), so it must not exchange momentum");
        }

        /// <summary>
        /// The negative control for the five above. <c>UnitZombie</c> does <b>not</b> set
        /// <c>collisionTip</c>, so a zombie claw must shove — and a blanket "override it everywhere"
        /// cleanup, which would make the five tests above pass and this one fail, is the mistake this
        /// case exists to catch.
        /// </summary>
        [Test]
        public void AnOrdinaryClassKeepsTheExchange()
        {
            string body = SourceLint.MethodBody(
                SourceLint.ReadStripped(ZombiePath),
                "public sealed class ZombieController : EnemyController");

            Assert.That(body, Does.Contain("ControllerId"),
                "positive control: this has to be ZombieController's real body. The id literal itself " +
                "is stripped along with every other string, which is why this checks the field name.");
            Assert.That(body, Does.Not.Contain("CollisionTip"),
                "UnitZombie never touches collisionTip, so it keeps AS3's default of 1 — the zombie " +
                "that charges the player must be stopped by the exchange, not pass through");
        }
    }
}
