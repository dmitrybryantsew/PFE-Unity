using NUnit.Framework;
using UnityEngine;
using PFE.Systems.Combat;

namespace PFE.Tests.Editor.Combat
{
    /// <summary>
    /// Pins <see cref="UnitCollisionImpulseMath"/> — the port of the momentum exchange inside
    /// <c>Unit.udarUnit()</c>, <c>fe/unit/Unit.as:4151-4159</c>:
    ///
    /// <code>
    /// if(param1.collisionTip == 1)
    /// {
    ///    _loc5_ = (param1.dx * param1.massa + dx * massa) / (param1.massa + massa);
    ///    _loc6_ = (param1.dy * param1.massa + dy * massa) / (param1.massa + massa);
    ///    dx      = (-dx      + _loc5_) * this.knocked    + _loc5_;
    ///    dy      = (-dy      + _loc6_) * this.knocked    + _loc6_;
    ///    param1.dx = (-param1.dx + _loc5_) * param1.knocked + _loc5_;
    ///    param1.dy = (-param1.dy + _loc6_) * param1.knocked + _loc6_;
    /// }
    /// </code>
    ///
    /// <para><b>What is pinned and why each case exists.</b> The formula is short enough to look
    /// obviously right and to be wrong in four separate ways, so each of the four has a case:</para>
    /// <list type="bullet">
    /// <item><description><b>The verb.</b> It <i>replaces</i> velocity, it does not add. Adding is what
    /// <c>otbros</c> does, and an additive implementation conserves nothing — two bodies would gain
    /// energy on every contact and a charging zombie would accelerate itself to the clamp. The
    /// equal-mass case is the sharpest statement: the two bodies <b>swap</b>.</description></item>
    /// <item><description><b>Both sides.</b> Writing only the target makes every melee attacker a
    /// perpetual-motion ram, because it keeps its velocity <i>and</i> pushes.</description></item>
    /// <item><description><b>The two scales are different.</b> Each body reflects by its <i>own</i>
    /// <c>knocked</c> — not by a shared factor and not by a literal. <c>udarBox</c> is the one that uses
    /// the literal <c>0.25</c> on the other body; this is the sibling that does not.</description></item>
    /// <item><description><b>The axes are independent.</b> A diagonal body-check resolves x about the
    /// x-component of the centre of mass and y about the y-component. Resolving along the contact
    /// normal is what a physics engine would do and is <i>not</i> what the game does; a "cleanup" that
    /// unified the axes would change every diagonal hit while every horizontal fixture stayed
    /// green.</description></item>
    /// </list>
    ///
    /// <para><b>No RNG.</b> The spread draw at <c>:4150</c> belongs to the damage and is taken by
    /// <c>DamageSystem.RollContactAttackVariance</c> before this runs. There is deliberately no
    /// <c>IRngService</c> parameter here, so a future edit cannot quietly move a draw into a pure
    /// function and desynchronise the combat stream.</para>
    /// </summary>
    [TestFixture]
    public class UnitCollisionImpulseMathTests
    {
        private const float Tolerance = 1e-4f;

        /// <summary>
        /// Equal masses, both fully elastic — AS3's expression reduces to a pure swap, which is the
        /// single assertion that fails for every additive implementation.
        /// </summary>
        [Test]
        public void EqualMassesBothElastic_SwapVelocities()
        {
            UnitCollisionExchange result = UnitCollisionImpulseMath.ResolveExchange(
                attackerVelocityPixelsPerFrame: new Vector2(10f, 0f),
                attackerMassa: 1f,
                attackerKnocked: 1f,
                targetVelocityPixelsPerFrame: Vector2.zero,
                targetMassa: 1f,
                targetKnocked: 1f);

            Assert.AreEqual(0f, result.AttackerVelocityPixelsPerFrame.x, Tolerance,
                "an elastic collision between equal masses stops the striker");
            Assert.AreEqual(10f, result.TargetVelocityPixelsPerFrame.x, Tolerance,
                "…and hands its whole velocity to the target. A sum here means the exchange was " +
                "written as an impulse (dx += …) instead of a replacement (dx = …).");
        }

        /// <summary>
        /// <c>knocked = 0</c> is AS3's "cannot be moved" and it means <i>collapse onto the centre of
        /// mass</i>, not "no exchange". The other body still reflects off it, which is why a turret is
        /// a wall rather than a hole.
        /// </summary>
        [Test]
        public void ImmovableTarget_CollapsesOntoCentreOfMass_AndStillThrowsTheAttacker()
        {
            // com = (10*3 + 0*1) / 4 = 7.5
            UnitCollisionExchange result = UnitCollisionImpulseMath.ResolveExchange(
                attackerVelocityPixelsPerFrame: new Vector2(10f, 0f),
                attackerMassa: 3f,
                attackerKnocked: 1f,
                targetVelocityPixelsPerFrame: Vector2.zero,
                targetMassa: 1f,
                targetKnocked: 0f);

            Assert.AreEqual(7.5f, result.TargetVelocityPixelsPerFrame.x, Tolerance,
                "knocked = 0 puts the body exactly at the centre-of-mass velocity — the oracle's " +
                "`(-v + com) * 0 + com`");
            Assert.AreEqual(5f, result.AttackerVelocityPixelsPerFrame.x, Tolerance,
                "and the attacker reflects off it: 2*7.5 - 10. Zero here would mean the exchange was " +
                "skipped entirely for an immovable target.");
        }

        /// <summary>
        /// With both bodies elastic the exchange conserves momentum. This is the property a wrong
        /// per-body scale breaks, and it holds for every mass ratio, so it is checked at an awkward one
        /// rather than at 1:1 where it would hold by symmetry alone.
        /// </summary>
        [Test]
        public void BothElastic_ConservesMomentum_AtUnequalMasses()
        {
            Vector2 attacker = new Vector2(7f, -3f);
            Vector2 target = new Vector2(-2f, 4f);
            const float attackerMass = 4f;
            const float targetMass = 1.5f;

            UnitCollisionExchange result = UnitCollisionImpulseMath.ResolveExchange(
                attacker, attackerMass, 1f, target, targetMass, 1f);

            Vector2 before = attacker * attackerMass + target * targetMass;
            Vector2 after = result.AttackerVelocityPixelsPerFrame * attackerMass
                          + result.TargetVelocityPixelsPerFrame * targetMass;

            Assert.AreEqual(before.x, after.x, Tolerance, "x momentum");
            Assert.AreEqual(before.y, after.y, Tolerance, "y momentum");
        }

        /// <summary>
        /// Each body reflects by its <b>own</b> <c>knocked</c>. Two different values on the same hit is
        /// the only arrangement that distinguishes "each body's own" from "a shared factor".
        /// </summary>
        [Test]
        public void EachBodyUsesItsOwnKnocked()
        {
            // com = 5. Attacker knocked 0.5 -> 5 + 0.5*(5-10) = 2.5. Target knocked 1 -> 10.
            UnitCollisionExchange result = UnitCollisionImpulseMath.ResolveExchange(
                attackerVelocityPixelsPerFrame: new Vector2(10f, 0f),
                attackerMassa: 1f,
                attackerKnocked: 0.5f,
                targetVelocityPixelsPerFrame: Vector2.zero,
                targetMassa: 1f,
                targetKnocked: 1f);

            Assert.AreEqual(2.5f, result.AttackerVelocityPixelsPerFrame.x, Tolerance,
                "a heavy-footed attacker keeps half of its bounce");
            Assert.AreEqual(10f, result.TargetVelocityPixelsPerFrame.x, Tolerance,
                "an ordinary target still takes the full swap");
        }

        /// <summary>
        /// The axes are resolved independently, each about its own component of the centre of mass —
        /// <b>not</b> along the line between the bodies.
        ///
        /// <para>The numbers are chosen so the two readings disagree: with a diagonal contact, a
        /// normal-direction exchange would put the target's velocity on the 45° line through the
        /// bodies, while the oracle keeps x and y in their own lanes. Both bodies are elastic and equal
        /// mass, so the expected answer is a component-wise swap.</para>
        /// </summary>
        [Test]
        public void AxesAreResolvedIndependently_NotAlongTheContactNormal()
        {
            UnitCollisionExchange result = UnitCollisionImpulseMath.ResolveExchange(
                attackerVelocityPixelsPerFrame: new Vector2(10f, 2f),
                attackerMassa: 1f,
                attackerKnocked: 1f,
                targetVelocityPixelsPerFrame: Vector2.zero,
                targetMassa: 1f,
                targetKnocked: 1f);

            Assert.AreEqual(10f, result.TargetVelocityPixelsPerFrame.x, Tolerance, "x swaps on its own");
            Assert.AreEqual(2f, result.TargetVelocityPixelsPerFrame.y, Tolerance,
                "y swaps on its own too — a normal-direction exchange would have put both bodies on " +
                "the 45° line, so this y is the assertion that tells the two apart");
            Assert.AreEqual(0f, result.AttackerVelocityPixelsPerFrame.x, Tolerance);
            Assert.AreEqual(0f, result.AttackerVelocityPixelsPerFrame.y, Tolerance);
        }

        /// <summary>
        /// A heavy striker barely slows and throws a light target hard. This is the qualitative
        /// behaviour the whole mechanic exists for, and it is the case a transposed multiply in the
        /// centre-of-mass numerator gets wrong.
        /// </summary>
        [Test]
        public void HeavyStriker_BarelySlows_AndThrowsTheLightTargetHard()
        {
            UnitCollisionExchange result = UnitCollisionImpulseMath.ResolveExchange(
                attackerVelocityPixelsPerFrame: new Vector2(10f, 0f),
                attackerMassa: 10f,
                attackerKnocked: 1f,
                targetVelocityPixelsPerFrame: Vector2.zero,
                targetMassa: 1f,
                targetKnocked: 1f);

            // com = 100/11 = 9.0909; attacker -> 8.1818, target -> 18.1818
            Assert.AreEqual(8.1818f, result.AttackerVelocityPixelsPerFrame.x, 1e-3f);
            Assert.AreEqual(18.1818f, result.TargetVelocityPixelsPerFrame.x, 1e-3f,
                "the light body leaves faster than the striker arrived — that is the exchange, not a " +
                "bug: it took the striker's momentum plus its own reflection");
        }

        /// <summary>
        /// AS3 divides by <c>massa + massa</c> with no guard, so a zero total would hand both bodies
        /// <c>NaN</c> — and a <c>NaN</c> velocity is not a wrong number, it is a unit that is gone for
        /// the rest of the run, with nothing on screen to explain it. The port leaves both bodies
        /// untouched instead.
        /// </summary>
        [Test]
        public void ZeroTotalMass_LeavesBothBodiesUntouched_AndProducesNoNaN()
        {
            Vector2 attacker = new Vector2(3f, -4f);
            Vector2 target = new Vector2(-1f, 2f);

            UnitCollisionExchange result = UnitCollisionImpulseMath.ResolveExchange(
                attacker, 0f, 1f, target, 0f, 1f);

            Assert.AreEqual(attacker, result.AttackerVelocityPixelsPerFrame);
            Assert.AreEqual(target, result.TargetVelocityPixelsPerFrame);

            Assert.IsFalse(float.IsNaN(result.AttackerVelocityPixelsPerFrame.x)
                        || float.IsNaN(result.TargetVelocityPixelsPerFrame.x),
                "a NaN here would corrupt the unit's position permanently");
        }

        /// <summary>
        /// AS3's field default, and the answer the port must give for every unit that does not override
        /// it — which is all but five. A <c>false</c> default would silently disable the shove for the
        /// entire roster while every arithmetic case above still passed.
        /// </summary>
        [Test]
        public void CollisionTipDefaultIsTrue()
        {
            Assert.IsTrue(UnitCollisionImpulseMath.CollisionTipDefault,
                "AS3 declares `public var collisionTip:int = 1` (Unit.as:196)");
        }
    }
}
