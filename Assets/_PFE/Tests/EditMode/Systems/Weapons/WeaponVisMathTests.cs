using NUnit.Framework;
using PFE.Data.Definitions;
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

        // ── Whether the held sprite is hidden (the throw override) ────────────

        /// <summary>
        /// The throw override hides the held sprite only for a <b>thrown</b> weapon that is mid-throw.
        ///
        /// <para><b>The bug these guard.</b> "I select acidgr then minigun (or any other weapon) — its
        /// graphics disappear." The presenter hid the sprite by writing the renderer's alpha to 0, but
        /// only did so inside the thrown branch — so after a throw the alpha stayed 0 and the branch
        /// never ran again for the next weapon. The new weapon was drawn fully transparent, and nothing
        /// but another thrown weapon would ever clear it.</para>
        /// </summary>
        [Test]
        public void HidesHeldSprite_OnlyForAThrownWeaponMidThrow()
        {
            Assert.IsTrue(WeaponVisMath.HidesHeldSprite(WeaponType.Thrown, tAttack: 15),
                "a thrown weapon hides its held sprite while the throw is in progress");

            Assert.IsFalse(WeaponVisMath.HidesHeldSprite(WeaponType.Thrown, tAttack: 0),
                "…and shows it again the moment the throw finishes");
        }

        /// <summary>
        /// <b>The control that matters.</b> A ranged weapon mid-attack has <c>tAttack &gt; 0</c> too, and
        /// must stay visible: hiding on <c>tAttack</c> alone would blink every gun out for a few frames
        /// after each shot. This is the assertion that fails if the <see cref="WeaponType.Thrown"/> gate
        /// is dropped.
        /// </summary>
        [Test]
        public void HidesHeldSprite_ANonThrownWeaponMidAttack_StaysVisible()
        {
            // Every weapon type that is not Thrown, at the same TAttack that hides a grenade.
            foreach (WeaponType type in new[]
                     {
                         WeaponType.Internal, WeaponType.Melee, WeaponType.Guns,
                         WeaponType.BigGun, WeaponType.Magic,
                     })
            {
                Assert.IsFalse(WeaponVisMath.HidesHeldSprite(type, tAttack: 15),
                    $"'{type}' must stay visible while it is mid-attack");
            }

            // …and the thrown case with identical inputs still hides, so the loop above is not
            // passing merely because the helper always returns false.
            Assert.IsTrue(WeaponVisMath.HidesHeldSprite(WeaponType.Thrown, tAttack: 15),
                "the very same tAttack DOES hide a thrown weapon");
        }

        /// <summary>
        /// A negative or zero countdown is "not attacking" for every type — the oracle's test is
        /// <c>&gt; 0</c>, so a weapon that somehow reads back negative must not be hidden either.
        /// </summary>
        [Test]
        public void HidesHeldSprite_NegativeCountdown_NeverHides()
        {
            foreach (WeaponType type in new[]
                     {
                         WeaponType.Internal, WeaponType.Melee, WeaponType.Guns,
                         WeaponType.BigGun, WeaponType.Thrown, WeaponType.Magic,
                     })
            {
                Assert.IsFalse(WeaponVisMath.HidesHeldSprite(type, tAttack: -1),
                    $"'{type}' must not be hidden on a negative countdown");
            }
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

        // ── Melee (WClub.animate) ─────────────────────────────────────────────

        /// <summary>
        /// <b>The identity that lets the ranged rule draw a melee weapon unchanged.</b>
        ///
        /// <para>AS3 melee mirrors on <b>Y</b> (<c>vis.scaleY = storona</c>, <c>WClub.as:746</c>) while
        /// the ranged rule mirrors on <b>X</b> and adds 180° to the rotation. They are the same world
        /// transform:
        /// <c>R(θ+180)·S(−1,1) = R(θ)·S(−1,−1)·S(−1,1) = R(θ)·S(1,−1)</c>.</para>
        ///
        /// <para>This is why <see cref="WeaponPresenter"/> needs no melee branch for a
        /// <c>krep == 0</c> weapon — the reported case (raider2/raider4). It is asserted here so that a
        /// future change to <i>either</i> rule goes red rather than silently breaking the other, which
        /// is exactly the drift the two presenters were consolidated to prevent.</para>
        /// </summary>
        [Test]
        public void MeleeFacingLeft_RangedFlipRule_IsTheOraclesScaleYMirror()
        {
            // Drives the PRODUCTION ranged rule (VisLocalScaleX / VisLocalRotationDeg), composes it
            // through the parent, and compares against the oracle's melee matrix. A pure-math restatement
            // could not go red; this goes red the moment either production function changes shape.
            foreach (float parentSign in new[] { 1f, -1f })
            foreach (float rotDeg in new[] { -120f, -90f, -30f, 0f, 45f, 179f, 180f, 270f })
            {
                var world = Compose(
                    Scale(parentSign, 1f),
                    Compose(
                        Rotation(WeaponVisMath.VisLocalRotationDeg(
                            rotDeg * Mathf.Deg2Rad, 0f, facingLeft: true, parentSign)),
                        Scale(WeaponVisMath.VisLocalScaleX(facingLeft: true, parentSign), 1f)));

                // The oracle's melee rule facing left: scaleY = storona = -1, rotation unmodified.
                var oracle = Compose(Rotation(rotDeg), Scale(1f, -1f));

                AssertMatrix(world, oracle, $"parentSign={parentSign} rot={rotDeg}");
            }
        }

        /// <summary>
        /// The <c>krep &gt; 0</c> melee branch (a rigidly-mounted unit, e.g. <c>slaver1</c>) — the one
        /// case the ranged rule does <b>not</b> cover.
        /// </summary>
        [Test]
        public void MeleeFixedVisRotationDeg_IsZeroFacingRight_AndMinus180FacingLeft()
        {
            // AS3 WClub.as:757 — `90*storona - 90 + owner.weaponR*storona`, with weaponR unported (0).
            Assert.AreEqual(0f, WeaponVisMath.MeleeFixedVisRotationDeg(facingLeft: false, weaponRDeg: 0f), Tolerance);
            Assert.AreEqual(-180f, WeaponVisMath.MeleeFixedVisRotationDeg(facingLeft: true, weaponRDeg: 0f), Tolerance);

            // weaponR rides the facing sign, so the same value tilts the opposite way per side — the
            // assertion that fails if the `* storona` is dropped from the weaponR term.
            Assert.AreEqual(10f, WeaponVisMath.MeleeFixedVisRotationDeg(facingLeft: false, weaponRDeg: 10f), Tolerance);
            Assert.AreEqual(-190f, WeaponVisMath.MeleeFixedVisRotationDeg(facingLeft: true, weaponRDeg: 10f), Tolerance);
        }

        [Test]
        public void MeleeVisScaleY_IsMinusOneFacingLeft()
        {
            Assert.AreEqual(1f, WeaponVisMath.MeleeVisScaleY(facingLeft: false), Tolerance);
            Assert.AreEqual(-1f, WeaponVisMath.MeleeVisScaleY(facingLeft: true), Tolerance);
        }

        /// <summary>
        /// The melee fold reproduces the oracle's world transform for either parent — the invariant, not
        /// the implementation.
        ///
        /// <para><b>This is the test that catches the tempting mistake.</b> The ranged fold multiplies
        /// the local X by <c>parentSign</c>; copying that onto the melee <b>Y</b> scale would double the
        /// mirror on every turn-around. The world matrix would then be <c>S(1, ±1)</c> off the oracle's,
        /// which this assertion reports.</para>
        /// </summary>
        [Test]
        public void MeleeLocalFold_ReproducesTheOracleWorldTransform_ForEitherParent()
        {
            foreach (float parentSign in new[] { 1f, -1f })
            foreach (bool facingLeft in new[] { false, true })
            foreach (float rotDeg in new[] { -120f, -90f, 0f, 45f, 179f })
            {
                float oracleDeg = WeaponVisMath.MeleeFixedVisRotationDeg(facingLeft, 0f);

                // What the presenter writes locally (WeaponPresenter.ApplyRotation's melee branch).
                float localScaleX = WeaponVisMath.MeleeVisLocalScaleX(parentSign);
                float localScaleY = WeaponVisMath.MeleeVisLocalScaleY(facingLeft);
                float localRot    = WeaponVisMath.MeleeVisLocalRotationDeg(oracleDeg, parentSign);

                // World = parent ∘ local.
                var world = Compose(
                    Scale(parentSign, 1f),
                    Compose(Rotation(localRot), Scale(localScaleX, localScaleY)));

                // The oracle: vis.scaleY = storona, vis.scaleX = 1, vis.rotation = oracleDeg.
                var expected = Compose(
                    Rotation(oracleDeg),
                    Scale(1f, WeaponVisMath.MeleeVisScaleY(facingLeft)));

                AssertMatrix(world, expected,
                    $"parentSign={parentSign} facingLeft={facingLeft} rot={rotDeg}");
            }
        }

        // ── 2×2 linear maps, so the assertions are on the transform the player sees ─────────────
        //
        // A linear map is carried as its two COLUMNS, and composed by applying the left map to each
        // column of the right one. Kept as plain Vector2s rather than a Matrix type: the point is to
        // compare the composed result against the oracle's composed result, not to reimplement Unity.

        private static Vector2 Apply(Vector2 col0, Vector2 col1, Vector2 v) => col0 * v.x + col1 * v.y;

        private static (Vector2, Vector2) Compose((Vector2, Vector2) left, (Vector2, Vector2) right)
            => (Apply(left.Item1, left.Item2, right.Item1),
                Apply(left.Item1, left.Item2, right.Item2));

        private static (Vector2, Vector2) Rotation(float deg)
        {
            float r = deg * Mathf.Deg2Rad;
            float c = Mathf.Cos(r), s = Mathf.Sin(r);
            return (new Vector2(c, s), new Vector2(-s, c));
        }

        private static (Vector2, Vector2) Scale(float sx, float sy)
            => (new Vector2(sx, 0f), new Vector2(0f, sy));

        private static void AssertMatrix((Vector2, Vector2) actual, (Vector2, Vector2) expected, string ctx)
        {
            Assert.AreEqual(expected.Item1.x, actual.Item1.x, Tolerance, $"{ctx} col0.x");
            Assert.AreEqual(expected.Item1.y, actual.Item1.y, Tolerance, $"{ctx} col0.y");
            Assert.AreEqual(expected.Item2.x, actual.Item2.x, Tolerance, $"{ctx} col1.x");
            Assert.AreEqual(expected.Item2.y, actual.Item2.y, Tolerance, $"{ctx} col1.y");
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
