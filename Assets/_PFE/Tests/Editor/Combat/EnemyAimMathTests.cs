using NUnit.Framework;
using PFE.Systems.Combat;
using UnityEngine;

namespace PFE.Tests.Editor.Combat
{
    /// <summary>
    /// Pins <see cref="EnemyAimMath"/> — the three <c>cel</c> points AS3 writes, which are the points
    /// every enemy weapon's barrel angle is computed from (<c>Weapon.as:1074-1086</c>).
    ///
    /// <para><b>Why this needs a guard.</b> The reported defect was "some enemies miss consistently,
    /// the alicorn boss especially". Nothing crashed and no fixture went red: five brains aimed their
    /// weapons at <c>target.transform.position</c>, which for a ported unit is the <b>feet</b>
    /// (<c>Unit.as:1875</c>: <c>Y1 = Y - scY; Y2 = Y</c>), while the oracle aims at the <b>middle of
    /// the body</b> (<c>Unit.as:4708</c>: <c>celY = Y - scY/2</c>). A bolt fired on the line to the feet
    /// reaches the ground at the target's hooves, and the floor tile is hit before the collider is.
    /// The number was never checked against anything, so it could not be wrong loudly.</para>
    ///
    /// <para><b>Every assertion is a difference, not an absolute.</b> A test that only said "the aim
    /// point is above the feet" would pass for a point one pixel up; the cases below pin the two
    /// <i>exact</i> oracle terms — half the body height, and a quarter of the body width along the
    /// target's facing — and the sign of the second one, which is the part a plausible rewrite gets
    /// wrong.</para>
    /// </summary>
    [TestFixture]
    public class EnemyAimMathTests
    {
        private const float Tolerance = 1e-4f;

        // A player-sized body: scX 40, scY 60, feet at (300, 400). All three cases below use it, so the
        // arithmetic in each expected value can be read off the oracle line it comes from.
        private static readonly Vector2 Feet = new Vector2(300f, 400f);
        private static readonly Vector2 Size = new Vector2(40f, 60f);

        // ── Unit.as:4707-4708, the live-target aim point ──────────────────────

        /// <summary>
        /// <c>celX = param1.X + param1.scX / 4 * param1.storona; celY = param1.Y - param1.scY / 2;</c>
        /// — <c>40 / 4 = 10</c> to the right and <c>60 / 2 = 30</c> up from the feet.
        /// </summary>
        [Test]
        public void TargetCel_IsAQuarterBodyForward_AndHalfABodyUp()
        {
            Vector2 cel = EnemyAimMath.TargetCel(Feet, Size, targetFacing: 1);

            Assert.AreEqual(310f, cel.x, Tolerance,
                "AS3 adds scX/4 * storona: 40/4 = 10 px along the target's facing");
            Assert.AreEqual(430f, cel.y, Tolerance,
                "AS3 subtracts scY/2 from a downward Y, which is +scY/2 from a feet-anchored upward Y: " +
                "60/2 = 30 px above the feet");
        }

        /// <summary>
        /// <b>The half of the bug that is easiest to get backwards.</b> The lead follows
        /// <c>storona</c> — the side the target is <i>facing</i> — not its velocity and not the
        /// shooter. A mirrored sign puts the aim a full half-body off centre instead of a quarter,
        /// which is a worse miss than not having the lead at all.
        /// </summary>
        [Test]
        public void TargetCel_LeadsTowardTheTargetsOwnFacing()
        {
            Vector2 right = EnemyAimMath.TargetCel(Feet, Size, targetFacing: 1);
            Vector2 left = EnemyAimMath.TargetCel(Feet, Size, targetFacing: -1);

            Assert.AreEqual(290f, left.x, Tolerance, "storona = -1 mirrors the quarter-width lead");
            Assert.AreEqual(right.y, left.y, Tolerance,
                "the facing has no vertical term — celY is the same mid-body point either way");
            Assert.AreEqual(20f, right.x - left.x, Tolerance,
                "the two aims are half a body apart, i.e. 2 * scX/4");
        }

        /// <summary>
        /// <c>Obj.as:24</c> gives <c>storona</c> the default <c>1</c>, so a unit that has never been
        /// turned — a freshly spawned one, or a test double — aims as if it faced right. Treating
        /// <c>0</c> as "no lead" would silently drop the term on exactly those units, which is the
        /// kind of bug that only shows up on the one enemy nobody tested.
        /// </summary>
        [Test]
        public void TargetCel_TreatsAnUnsetFacingAsRight()
        {
            Assert.AreEqual(
                EnemyAimMath.TargetCel(Feet, Size, targetFacing: 1),
                EnemyAimMath.TargetCel(Feet, Size, targetFacing: 0));
        }

        /// <summary>
        /// The regression itself, stated as a property rather than a number: whatever the body, the aim
        /// is <b>strictly above the feet</b> and <b>inside the body box horizontally</b>. An
        /// implementation that returned <c>transform.position</c> — the shipped bug — fails the first
        /// line for every size; one that led by a full body fails the second.
        /// </summary>
        [TestCase(40f, 60f, 1)]
        [TestCase(120f, 200f, 1)]
        [TestCase(30f, 44f, -1)]
        public void TargetCel_IsAlwaysInsideTheBodyBox(float widthPx, float heightPx, int facing)
        {
            var size = new Vector2(widthPx, heightPx);
            Vector2 cel = EnemyAimMath.TargetCel(Feet, size, facing);

            Assert.Greater(cel.y, Feet.y,
                "aiming AT the feet is the shipped bug: the round lands on the floor tile the target " +
                "stands on, not on the target");

            Assert.AreEqual(Feet.y + heightPx * 0.5f, cel.y, Tolerance,
                "and the point is the middle of the body, not merely 'somewhere above the feet'");

            float halfWidth = widthPx * 0.5f;
            Assert.LessOrEqual(Mathf.Abs(cel.x - Feet.x), halfWidth + Tolerance,
                "a quarter-width lead must stay inside the body box; a full width would aim past her");
        }

        // ── UnitPet.as:815-816, the one archetype that drops the lead ─────────

        /// <summary>
        /// <c>celX = celUnit.X; celY = celUnit.Y - celUnit.scY / 2;</c> — the pet override. Same
        /// vertical point, no horizontal term at all.
        /// </summary>
        [Test]
        public void TargetCentre_HasNoLead_ButKeepsTheMidBodyHeight()
        {
            Vector2 centre = EnemyAimMath.TargetCentre(Feet, Size);

            Assert.AreEqual(Feet.x, centre.x, Tolerance,
                "UnitPet.setCel writes celX = celUnit.X with no scX/4 term");
            Assert.AreEqual(430f, centre.y, Tolerance, "the vertical term is unchanged");
            Assert.AreEqual(
                EnemyAimMath.TargetCel(Feet, Size, 1).y, centre.y, Tolerance,
                "the pet's point and the ordinary point must agree vertically — only the lead differs");
        }

        // ── UnitRaider.as:682-683, the no-target ahead point ──────────────────

        /// <summary>
        /// <c>celY = Y - scY; celX = X + scX * storona * 2;</c> — two body widths forward at the
        /// <b>top</b> of the unit's own body. This is what an idle armed unit points its barrel at, and
        /// it is why the mount's old "facing only, from the feet" fallback tilted every idle gun at the
        /// floor.
        /// </summary>
        [Test]
        public void AheadCel_IsTwoBodiesForward_AtHeadHeight()
        {
            Vector2 ahead = EnemyAimMath.AheadCel(Feet, Size, selfFacing: 1);

            Assert.AreEqual(380f, ahead.x, Tolerance, "X + scX * storona * 2 = 300 + 40*2");
            Assert.AreEqual(460f, ahead.y, Tolerance,
                "Y - scY in a downward Y is +scY from the feet in the port: the TOP of the body, not " +
                "its middle and certainly not its feet");
        }

        [Test]
        public void AheadCel_MirrorsWithFacing_AndDefaultsToRight()
        {
            Assert.AreEqual(220f, EnemyAimMath.AheadCel(Feet, Size, selfFacing: -1).x, Tolerance,
                "X - scX * 2 = 300 - 80");

            Assert.AreEqual(
                EnemyAimMath.AheadCel(Feet, Size, selfFacing: 1),
                EnemyAimMath.AheadCel(Feet, Size, selfFacing: 0),
                "storona defaults to 1 (Obj.as:24)");
        }
    }
}
