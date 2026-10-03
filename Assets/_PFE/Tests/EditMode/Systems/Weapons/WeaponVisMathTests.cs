using NUnit.Framework;
using PFE.Systems.Weapons;
using UnityEngine;

namespace PFE.Tests.EditMode.Systems.Weapons
{
    /// <summary>
    /// Pins <see cref="WeaponVisMath"/> — which way the held weapon's vis points.
    ///
    /// <para><b>The bug these guard.</b> "Character turned left, the weapon flipped and it fires from
    /// its back." The unit root mirrors itself on facing
    /// (<c>UnitController.ApplyFacingToTransform</c> writes <c>localScale.x = _facingDirection</c>) and
    /// the weapon is its child, so writing AS3's <c>vis.scaleX = -1</c> on top of that mirror produced
    /// <c>+1</c> — the two cancelled — and the barrel was drawn on the far side of the grip.</para>
    ///
    /// <para><b>What is actually asserted.</b> Not "the local scale is +1 when facing left", which is
    /// the implementation, but the invariant the implementation exists to satisfy: <i>the transform the
    /// player sees equals the transform AS3's <c>vis</c> would have</i>, for either parent. The two
    /// property tests below check that identity across both signs, so they keep working if the
    /// conversion is ever rewritten — and they fail immediately if someone "simplifies" it back to
    /// writing the oracle's numbers straight into local space.</para>
    /// </summary>
    [TestFixture]
    public class WeaponVisMathTests
    {
        private const float Tolerance = 1e-4f;

        // ── The oracle, transcribed ───────────────────────────────────────────

        [Test]
        public void VisRotationDeg_MatchesWeaponAnimate()
        {
            // AS3 Weapon.as:1996-2008 — facing right: `rot*180/PI - rotUp`; facing left:
            // `rot*180/PI + 180 + rotUp`.
            Assert.AreEqual(0f, WeaponVisMath.VisRotationDeg(0f, 0f, facingLeft: false), Tolerance);
            Assert.AreEqual(90f, WeaponVisMath.VisRotationDeg(Mathf.PI / 2f, 0f, facingLeft: false), Tolerance);
            Assert.AreEqual(180f, WeaponVisMath.VisRotationDeg(Mathf.PI, 0f, facingLeft: false), Tolerance);

            // Facing left adds 180 to compensate the mirror; at rot == PI the two cancel and the gun is
            // a pure mirror, upright, pointing left. That cancellation is the whole point of the +180.
            Assert.AreEqual(360f, WeaponVisMath.VisRotationDeg(Mathf.PI, 0f, facingLeft: true), Tolerance);
            Assert.AreEqual(180f, WeaponVisMath.VisRotationDeg(0f, 0f, facingLeft: true), Tolerance);
        }

        [Test]
        public void VisRotationDeg_RotUpIsAddedFacingLeft_SubtractedFacingRight()
        {
            Assert.AreEqual(-7f, WeaponVisMath.VisRotationDeg(0f, 7f, facingLeft: false), Tolerance);
            Assert.AreEqual(187f, WeaponVisMath.VisRotationDeg(0f, 7f, facingLeft: true), Tolerance);
        }

        [Test]
        public void VisScaleX_IsMinusOneFacingLeft()
        {
            Assert.AreEqual(1f, WeaponVisMath.VisScaleX(facingLeft: false), Tolerance);
            Assert.AreEqual(-1f, WeaponVisMath.VisScaleX(facingLeft: true), Tolerance);
        }

        [Test]
        public void IsFacingLeft_FollowsTheCursor()
        {
            // AS3 flips when the weapon's X is right of the cursor X (`X > owner.celX`).
            Assert.IsTrue(WeaponVisMath.IsFacingLeft(aimWorldX: -1f, visWorldX: 0f));
            Assert.IsFalse(WeaponVisMath.IsFacingLeft(aimWorldX: 1f, visWorldX: 0f));
            // Exactly on the weapon is "not left" — the oracle's test is strict.
            Assert.IsFalse(WeaponVisMath.IsFacingLeft(aimWorldX: 0f, visWorldX: 0f));
        }

        // ── The conversion ────────────────────────────────────────────────────

        [Test]
        public void VisLocalScaleX_InvertsUnderAMirroredParent()
        {
            // Unmirrored parent: the local value is the oracle's.
            Assert.AreEqual(1f, WeaponVisMath.VisLocalScaleX(facingLeft: false, parentScaleSign: 1f), Tolerance);
            Assert.AreEqual(-1f, WeaponVisMath.VisLocalScaleX(facingLeft: true, parentScaleSign: 1f), Tolerance);

            // Mirrored parent: the mirror already supplies the -1, so the child must not repeat it.
            // Facing left locally becomes +1; writing -1 here is the reported bug.
            Assert.AreEqual(-1f, WeaponVisMath.VisLocalScaleX(facingLeft: false, parentScaleSign: -1f), Tolerance);
            Assert.AreEqual(1f, WeaponVisMath.VisLocalScaleX(facingLeft: true, parentScaleSign: -1f), Tolerance);
        }

        [Test]
        public void WorldScale_EqualsTheOraclesScaleX_ForEitherParent()
        {
            // The invariant: the world scale is the product of the parent's mirror and the local scale,
            // and it must come out at the oracle's value whichever parent we are under. This is what
            // "the sprite faces the way AS3 draws it" means.
            foreach (float parentSign in new[] { 1f, -1f })
            foreach (bool facingLeft in new[] { false, true })
            {
                float local = WeaponVisMath.VisLocalScaleX(facingLeft, parentSign);
                float world = parentSign * local;
                Assert.AreEqual(WeaponVisMath.VisScaleX(facingLeft), world, Tolerance,
                    $"parentSign={parentSign} facingLeft={facingLeft}");
            }
        }

        [Test]
        public void WorldRotation_EqualsTheOraclesRotation_ForEitherParent()
        {
            // A mirrored parent renders a child's local rotation phi as -phi, so the world angle is
            // `parentSign * local`. It has to land on the oracle's angle for both parents.
            foreach (float parentSign in new[] { 1f, -1f })
            foreach (bool facingLeft in new[] { false, true })
            foreach (float rotDeg in new[] { 0f, 45f, 180f, 315f })
            foreach (float rotUpDeg in new[] { 0f, 7f })
            {
                float local = WeaponVisMath.VisLocalRotationDeg(rotDeg * Mathf.Deg2Rad, rotUpDeg, facingLeft, parentSign);
                float world = parentSign * local;
                float expected = WeaponVisMath.VisRotationDeg(rotDeg * Mathf.Deg2Rad, rotUpDeg, facingLeft);

                Assert.AreEqual(Mathf.DeltaAngle(0f, expected), Mathf.DeltaAngle(0f, world), Tolerance,
                    $"parentSign={parentSign} facingLeft={facingLeft} rot={rotDeg} rotUp={rotUpDeg}");
            }
        }

        // ── The parent chain's mirror (engine-free) ───────────────────────────

        [Test]
        public void ParentScaleSign_TreatsNoParentAsUnmirrored()
        {
            // A vis with no parent has no mirror to divide out. This is the only branch of
            // ParentScaleSign that does not need a live transform, and it is the one that runs here.
            Assert.AreEqual(1f, WeaponVisMath.ParentScaleSign(null), Tolerance);
        }

        [Test]
        public void ChainStep_OnlyANegativeLinkMirrors()
        {
            Assert.AreEqual(-1f, WeaponVisMath.ChainStep(1f, -1f), Tolerance);
            Assert.AreEqual(1f, WeaponVisMath.ChainStep(-1f, -1f), Tolerance);
            Assert.AreEqual(1f, WeaponVisMath.ChainStep(1f, 1f), Tolerance);

            // A degenerate zero scale is not a mirror — it must leave the sign alone rather than
            // reading as `not positive`.
            Assert.AreEqual(1f, WeaponVisMath.ChainStep(1f, 0f), Tolerance);
            Assert.AreEqual(-1f, WeaponVisMath.ChainStep(-1f, 0f), Tolerance);
        }

        [Test]
        public void ChainScaleSign_FlipsOncePerMirroringLink()
        {
            // The product's sign, which is all the conversion needs: an even number of mirrors is no
            // mirror at all.
            Assert.AreEqual(1f, WeaponVisMath.ChainScaleSign(new float[0]), Tolerance);
            Assert.AreEqual(-1f, WeaponVisMath.ChainScaleSign(new[] { -1f }), Tolerance);
            Assert.AreEqual(1f, WeaponVisMath.ChainScaleSign(new[] { -1f, -1f }), Tolerance);
            Assert.AreEqual(-1f, WeaponVisMath.ChainScaleSign(new[] { -1f, -1f, -1f }), Tolerance);
            Assert.AreEqual(1f, WeaponVisMath.ChainScaleSign(new[] { 1f, 1f }), Tolerance);
        }

        [Test]
        public void ChainScaleSign_SeesThroughAnInterveningNode()
        {
            // The shipped rig: the vis hangs off the Weapon node (unscaled) which hangs off the unit
            // root, which mirrors on facing. Two links, one mirror → mirrored. That is the case
            // ParentScaleSign exists to find, and it must not be answered by the immediate parent
            // alone — the Weapon node itself is +1.
            Assert.AreEqual(-1f, WeaponVisMath.ChainScaleSign(new[] { 1f, -1f }), Tolerance,
                "this is the unit root mirroring when the player faces left");

            // A compensating scale on the intermediate node cancels it, and the sign must follow.
            Assert.AreEqual(1f, WeaponVisMath.ChainScaleSign(new[] { -1f, -1f }), Tolerance,
                "an intermediate mirror cancels the root's");
        }
    }
}
