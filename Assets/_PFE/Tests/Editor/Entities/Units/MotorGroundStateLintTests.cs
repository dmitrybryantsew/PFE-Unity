using NUnit.Framework;
using PFE.Tests.Editor.Common;

namespace PFE.Tests.Editor.Entities.Units
{
    /// <summary>
    /// Pins the two halves of one invariant about who owns a unit's step — the seam whose missing half
    /// made a zombie hop instead of run in the live build.
    ///
    /// <para><b>The bug this exists to make unrepeatable.</b> <c>UnitController</c> hands its whole step to
    /// a <c>TilePhysicsController</c> when one is present (<c>_hasTilePhysics</c>), and <i>only</i>
    /// <c>StepUnit</c> resolved the unit's ground state — the tile query that answers "am I standing on
    /// something" and the support span that answers "how much of me is past the edge". So for every
    /// motor-driven unit, which is every unit when <c>PfeDebugSettings.UnitMotor</c> is on, two fields
    /// froze at their initialisers: <c>_overhangLeft</c>/<c>_overhangRight</c> stayed at
    /// <c>UnitOverhangMath.NoSupportOverhang</c> = <c>1</c> — maximal overhang, "nothing is under me" —
    /// and <c>_isGrounded</c> was left to the Unity collision callbacks, whose answer is marginal at the
    /// 1 px seat.</para>
    ///
    /// <para>Every ledge threshold in <c>ZombieBrain</c> reads <c>1</c> as "I am at a lip", so a chasing
    /// zombie took its half hop on 50 % of ticks and a patrolling one hopped on 10 % and turned around on
    /// the other 90 %. Nothing threw, no test failed, and the build was green: the arithmetic fixtures
    /// (<c>SupportSpanTests</c>, <c>UnitOverhangMathTests</c>) were all correct, because the arithmetic was
    /// never the problem. <b>The wiring was, and wiring is what this fixture asserts.</b></para>
    ///
    /// <para><b>Why a source lint rather than a behavioural test.</b> The behaviour needs a
    /// <c>GameObject</c>, a room and a motor, and this project's harness cannot construct one outside the
    /// editor — <c>UnitAnimationStepTests</c>, <c>EnemyBrainTests</c> and <c>UnitMotorReHomeTests</c> have
    /// never run offline for exactly that reason. A lint is the only form of this guard that executes
    /// here; the reading and stripping it needs live in <see cref="SourceLint"/>, with the reasoning for
    /// stripping at all.</para>
    ///
    /// <para><b>The assertion is a conjunction, and both halves matter.</b> Half one is the <i>reason</i>
    /// the handover is needed: <c>UnitController</c> still stands its own step down for a motored unit.
    /// Half two is the handover itself. Deleting the handover fails half two; deleting the stand-down
    /// makes half one fail, which is the signal that the handover is now dead weight. Asserting only the
    /// call would keep passing if the stand-down were removed and the call became a second opinion on a
    /// state the unit already computes — which is how the two would drift apart again.</para>
    /// </summary>
    [TestFixture]
    public sealed class MotorGroundStateLintTests
    {
        /// <summary>The unit's own driver, and the predicate that stands it down.</summary>
        private const string UnitControllerPath = "Entities/Units/UnitController.cs";

        /// <summary>The motor — the driver that takes over when <c>_hasTilePhysics</c> is true.</summary>
        private const string MotorPath = "Systems/Physics/TilePhysicsController.cs";

        [Test]
        public void MotorDriver_HandsTheUnitItsGroundState_AndTheUnitStandsDownForIt()
        {
            string unit = SourceLint.ReadStripped(UnitControllerPath);
            string motor = SourceLint.ReadStripped(MotorPath);

            // ── Half one: the stand-down, which is why half two is required. ──────────────────────
            string simTick = SourceLint.MethodBody(unit, "public void SimTick(int tickIndex)");

            Assert.That(simTick, Does.Contain("_hasTilePhysics"),
                "UnitController.SimTick no longer returns on _hasTilePhysics. If a motored unit is meant " +
                "to run its own step now, the handover below is a second opinion on state the unit " +
                "already computes, and the two will drift — which is the failure this fixture was " +
                "written for.");

            Assert.That(simTick, Does.Contain("return"),
                "The _hasTilePhysics test must actually stand the step down, not merely mention the flag.");

            // ── Half two: the handover the stand-down makes necessary. ───────────────────────────
            string stepMotor = SourceLint.MethodBody(motor, "private void StepMotor()");

            Assert.That(stepMotor, Does.Contain("ResolveGroundStateFromMotor("),
                "TilePhysicsController.StepMotor no longer hands the unit its ground state. A motored " +
                "unit's overhang then keeps its initialiser, NoSupportOverhang = 1, which every ledge " +
                "threshold reads as 'standing on a lip' — the zombie hops instead of running, and hops " +
                "its way along patrol routes.");

            // The handover must be fed the motor's own grounded flag, not a constant or a re-derivation:
            // the flag is the whole point of the handover (the motor is the only thing that knows the
            // DIRECTION of the step, which is what makes a jump's first tick ungrounded).
            Assert.That(stepMotor, Does.Contain("IsGrounded"),
                "The handover must pass the motor's own IsGrounded. A literal — or a re-derivation from " +
                "the tile query — reintroduces the second source of truth the handover exists to remove.");

            // ── And the receiver must be reachable from the other assembly. ───────────────────────
            Assert.That(unit, Does.Contain("public void ResolveGroundStateFromMotor("),
                "ResolveGroundStateFromMotor must stay public: TilePhysicsController is in a different " +
                "assembly, so a `protected` or `private` receiver compiles in neither direction and the " +
                "motor silently has nothing to call.");
        }
    }
}
