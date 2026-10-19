using NUnit.Framework;
using PFE.Entities.Units;
using UnityEngine;

namespace PFE.Tests.Editor.UnitSprite
{
    /// <summary>
    /// Guards <see cref="TurretParts"/> — the numbers that put a turret's barrel and lamp back where
    /// the oracle draws them, and the arithmetic that turns them into transforms.
    ///
    /// <para><b>What is at stake.</b> The port drew each mount as one baked composite, so a turret
    /// could track and shoot the player while its barrel stayed pointed at the wall. Splitting it
    /// needs three things to agree: the barrel's placement relative to the mount's origin, the point
    /// the barrel rotates about, and the frame each of the two animated parts shows. Each of them is
    /// a number that can be wrong in a way that still draws <i>something</i> — a barrel rotating
    /// about its centre still rotates, a barrel offset by a few pixels still looks attached — so
    /// they are asserted rather than eyeballed.</para>
    /// </summary>
    public class TurretPartsTests
    {
        static readonly string[] Mounts = { "turret0", "turret1", "turret2", "turret3", "turret4", "turret5" };

        const float Tolerance = 1e-3f;

        [Test]
        public void EveryMount_HasPartPlacements()
        {
            foreach (string id in Mounts)
            {
                Assert.That(TurretParts.TryGetPlacements(id, out Vector2 gun, out Vector2 lamp), Is.True,
                    $"{id}: no part placements");
                Assert.That(gun.y, Is.LessThan(0f), $"{id}: the barrel sits above the origin in Flash space");
                Assert.That(lamp.y, Is.LessThan(0f), $"{id}: the lamp sits above the origin in Flash space");
            }

            // The family template is a category row with no art -- a prefix match would give it the
            // floor mount's numbers and make a mis-resolved spawn look correct.
            Assert.That(TurretParts.TryGetPlacements("turret", out _, out _), Is.False);
            Assert.That(TurretParts.TryGetPlacements(null, out _, out _), Is.False);
        }

        /// <summary>
        /// The barrel's placement must be the same number the registration-point table records as
        /// <c>setWeaponPos</c>'s offset. They are two copies of one measurement — one kept for
        /// anchoring the mount, one for placing the barrel — and a copy is exactly the kind of thing
        /// that gets edited in one place.
        /// </summary>
        [Test]
        public void TheGunPlacement_AgreesWithTheRegistrationPointTable()
        {
            foreach (string id in Mounts)
            {
                Assert.That(TurretParts.TryGetPlacements(id, out Vector2 gun, out _), Is.True);
                Assert.That(TurretSpriteOrigin.TryGetGunOffset(id, out float offsetAboveOrigin), Is.True);

                Assert.That(-gun.y, Is.EqualTo(offsetAboveOrigin).Within(Tolerance),
                    $"{id}: the barrel is placed {-gun.y} px above the origin but TurretSpriteOrigin "
                    + $"records {offsetAboveOrigin}");
            }
        }

        [Test]
        public void LocalPixels_FlipsYAndNothingElse()
        {
            // Flash y grows down, the port's room pixels grow up, and the x axis is the same axis.
            Assert.That(TurretParts.LocalPixels(new Vector2(-0.35f, -13.8f)),
                Is.EqualTo(new Vector2(-0.35f, 13.8f)));
            Assert.That(TurretParts.LocalPixels(Vector2.zero), Is.EqualTo(Vector2.zero));
        }

        /// <summary>
        /// The correction has to put the sprite's registration pixel on the node's origin, for any
        /// pivot the importer happens to give the sprite.
        ///
        /// <para>This is the arithmetic that makes the barrel sweep about its trunnion instead of its
        /// centre. It is checked by reconstructing where the sprite's top-left corner lands and
        /// asking where the registration pixel therefore is — not by restating the formula.</para>
        /// </summary>
        [Test]
        public void PivotCorrection_PutsTheRegistrationPixelOnTheNode()
        {
            var size = new Vector2(96f, 45f);          // the barrel frame
            var centrePivot = new Vector2(48f, 22.5f);

            Vector2 correction = TurretParts.PivotCorrection(centrePivot, size, TurretParts.GunPivotPx);

            // The renderer sits `correction` from the node; the sprite's pivot is `centrePivot` from
            // its own bottom-left, so the sprite's bottom-left is `correction - centrePivot` away.
            Vector2 bottomLeft = correction - centrePivot;

            // ...and the registration pixel is `desired.x` right of that edge and `size.y - desired.y`
            // up from the bottom edge.
            Vector2 registrationFromNode = bottomLeft + new Vector2(
                TurretParts.GunPivotPx.x,
                size.y - TurretParts.GunPivotPx.y);

            Assert.That(registrationFromNode.x, Is.EqualTo(0f).Within(Tolerance));
            Assert.That(registrationFromNode.y, Is.EqualTo(0f).Within(Tolerance));
        }

        /// <summary>
        /// The gun's pivot is not the centre of its frame. That is the whole reason this class exists:
        /// a centre pivot rotates the barrel about its middle, which swings the breech out of the
        /// mount on every aim.
        /// </summary>
        [Test]
        public void TheGunPivot_IsNotTheCentreOfItsFrame()
        {
            var centre = new Vector2(96f / 2f, 45f / 2f);

            Assert.That(Mathf.Abs(TurretParts.GunPivotPx.x - centre.x), Is.GreaterThan(1f),
                "the barrel's registration point is 15.1 px in from the left edge, not centred");
            Assert.That(TurretParts.GunPivotPx.x, Is.EqualTo(15.1f).Within(Tolerance));
            Assert.That(TurretParts.GunPivotPx.y, Is.EqualTo(23.12f).Within(Tolerance));
        }

        [Test]
        public void GunFrame_IsTheTrAsAFrameNumber_Clamped()
        {
            // AS3: `vis.osn.puha.gotoAndStop(this.tr)`. `tr` is the weapon row's number and is used
            // as a frame index directly -- the difficulty bands reach 1..7 and `turret10` reaches 10.
            Assert.That(TurretParts.GunFrame(1), Is.EqualTo(1));
            Assert.That(TurretParts.GunFrame(7), Is.EqualTo(7));
            Assert.That(TurretParts.GunFrame(10), Is.EqualTo(10));

            // Clamped, not wrapped: a wrap would draw frame 1 for tr = 11, which looks like a working
            // mount rather than like a missing frame.
            Assert.That(TurretParts.GunFrame(11), Is.EqualTo(10));
            Assert.That(TurretParts.GunFrame(0), Is.EqualTo(1));
            Assert.That(TurretParts.GunFrame(-3), Is.EqualTo(1));
        }

        [Test]
        public void LampFrame_IsAiStatePlusOne_Clamped()
        {
            // AS3: `vis.osn.light.gotoAndStop(aiState + 1)`. Frame 1 is the unlit pose and is an
            // empty frame in the symbol, so an idle mount shows no lamp at all.
            Assert.That(TurretParts.LampFrame(0), Is.EqualTo(1));
            Assert.That(TurretParts.LampFrame(3), Is.EqualTo(4));
            Assert.That(TurretParts.LampFrame(9), Is.EqualTo(TurretParts.LampFrameCount));
            Assert.That(TurretParts.LampFrame(-1), Is.EqualTo(1));
        }

        /// <summary>
        /// AS3's whole rule for a mount is <c>vis.osn.puha.rotation = currentWeapon.rot * 180 / PI</c>:
        /// no <c>rotUp</c> lift and no mirror, because <c>UnitTurret</c> never writes <c>vis.scaleX</c>.
        ///
        /// <para>A held weapon's rule is <c>rot·180/π − rotUp</c> facing right and
        /// <c>rot·180/π + 180 + rotUp</c> facing left. Copying it here would add a lift the mount does
        /// not have and, on a left-facing mount, mirror the barrel to point backwards — which is the
        /// shape the "it fires from its back" defect took for characters.</para>
        /// </summary>
        [Test]
        public void GunRotationDeg_IsAPlainRadiansToDegrees()
        {
            Assert.That(TurretParts.GunRotationDeg(0f), Is.EqualTo(0f).Within(Tolerance));
            Assert.That(TurretParts.GunRotationDeg(Mathf.PI / 2f), Is.EqualTo(90f).Within(Tolerance));
            Assert.That(TurretParts.GunRotationDeg(Mathf.PI), Is.EqualTo(180f).Within(Tolerance));
            Assert.That(TurretParts.GunRotationDeg(-Mathf.PI / 2f), Is.EqualTo(-90f).Within(Tolerance));
        }

        [Test]
        public void ResourcePaths_AreUnderTheTurretPartsFolder()
        {
            Assert.That(TurretParts.BodyResourcePath("turret3"), Is.EqualTo("TurretParts/turret3/body"));
            Assert.That(TurretParts.GunResourcePath(1), Is.EqualTo("TurretParts/_shared/gun/f01"));
            Assert.That(TurretParts.GunResourcePath(10), Is.EqualTo("TurretParts/_shared/gun/f10"));
            Assert.That(TurretParts.LampResourcePath(6), Is.EqualTo("TurretParts/_shared/lamp/f06"));
        }
    }
}
