using NUnit.Framework;
using PFE.Data.Definitions;
using PFE.Entities.Units;
using UnityEngine;

namespace PFE.Tests.Editor.UnitSprite
{
    /// <summary>
    /// Guards <see cref="TurretSpriteOrigin"/> — the registration point of the six turret mounts, whose
    /// art is a DisplayObject MovieClip rather than a blit bitmap.
    ///
    /// <para><b>What this pins.</b> A turret's imported frame is 104x170 (tip 0) with the art in a band
    /// across the middle, and the definition declares no <c>sprDX</c>/<c>sprDY</c>, so the blit rule in
    /// <see cref="UnitSpriteAnchor"/> used to lift it 75 px and Unity's centre pivot centred it on the
    /// cell: the mount was drawn <b>28.5 px left and 56 px above</b> where <c>UnitTurret.setVisPos</c>
    /// puts it. Neither the build nor the asset validation can see that — the sprite is valid and the
    /// pivot is legal — so the numbers are asserted here.</para>
    /// </summary>
    public class TurretSpriteOriginTests
    {
        static readonly string[] Mounts = { "turret0", "turret1", "turret2", "turret3", "turret4", "turret5" };

        /// <summary>
        /// The oracle's <c>setWeaponPos</c> constant per mount (<c>UnitTurret.as:422-441</c>):
        /// <c>weaponY = Y - n</c>, tip 0 and 4 → 12 (the gun's own clip origin is 13.8), tip 1 → 60,
        /// tip 2 and 5 → 20, tip 3 → 55.
        ///
        /// <para>These are not a restatement of the table: they are the independent source the solve
        /// was checked against. The gun's placement matrix in <c>osn0</c> reads <c>(-0.35, -13.8)</c>
        /// and the oracle's own arithmetic reads 12 — the same pivot to within the muzzle offset — and
        /// the same agreement holds on every mount. If a future edit transposes an origin, the two stop
        /// agreeing and this fails.</para>
        /// </summary>
        static readonly float[] OracleGunOffsets = { 13.8f, 60f, 20f, 55f, 13.8f, 20f };

        [Test]
        public void EveryMount_HasAnOriginInsideItsOwnFrame()
        {
            // The registration point is the mounting surface, and FFDec crops the frame to the
            // symbol's *art* bounds, so the origin can land on — or a pixel past — an edge:
            // turret3's solved origin is (24.10, 98.12) inside a 105x97 frame. A margin of 2 px
            // still catches what this test is for, which is a transposed or sign-flipped pair of
            // numbers drawing the mount a whole frame away.
            const int EdgeMargin = 2;

            foreach (string id in Mounts)
            {
                Assert.That(TurretSpriteOrigin.TryGet(id, out Vector2Int origin), Is.True, $"{id}: no origin");
                Assert.That(TurretSpriteOrigin.TryGetFrameSize(id, out Vector2Int size), Is.True, $"{id}: no frame size");

                Assert.That(origin.x, Is.InRange(-EdgeMargin, size.x + EdgeMargin),
                    $"{id}: origin x {origin.x} outside 0..{size.x}");
                Assert.That(origin.y, Is.InRange(-EdgeMargin, size.y + EdgeMargin),
                    $"{id}: origin y {origin.y} outside 0..{size.y}");
            }
        }

        [Test]
        public void TheFamilyTemplate_IsNotAMount()
        {
            // `turret` is a self-closing category row (AllData.as:1758) with no art. A prefix match would
            // give it the floor mount's numbers and make a mis-resolved spawn look correct.
            Assert.That(TurretSpriteOrigin.TryGet("turret", out _), Is.False);
            Assert.That(TurretSpriteOrigin.TryGet(null, out _), Is.False);
            Assert.That(TurretSpriteOrigin.TryGet("", out _), Is.False);
        }

        [Test]
        public void ANonTurretUnit_KeepsItsDeclaredRegistrationPoint()
        {
            var declared = new Vector2Int(12, 30);

            Assert.That(TurretSpriteOrigin.Resolve("zombie0", declared), Is.EqualTo(declared));
            Assert.That(TurretSpriteOrigin.Resolve((UnitDefinition)null), Is.EqualTo(new Vector2Int(-1, -1)));
        }

        [Test]
        public void TheFloorMount_IsMovedDownAndRightFromWhereTheBlitRulePutIt()
        {
            // The measured frame and the pivot Unity's importer gives it (alignment: 0, centre).
            var size = new Vector2(104f, 170f);
            var centrePivot = new Vector2(52f, 85f);

            Vector2 before = UnitSpriteAnchor.PixelOffset(centrePivot, size, new Vector2Int(-1, -1));
            Vector2 after = UnitSpriteAnchor.PixelOffset(centrePivot, size, TurretSpriteOrigin.Resolve("turret0", new Vector2Int(-1, -1)));

            // Before: the blit rule's 10 px feet gap on a 170 px canvas → lifted 75 px.
            Assert.That(before, Is.EqualTo(new Vector2(0f, 75f)));
            // After: the mount hangs from its own origin → +29 px right, +19 px up, i.e. 56 px lower.
            Assert.That(after.x, Is.EqualTo(29f).Within(0.5f));
            Assert.That(after.y, Is.EqualTo(19f).Within(0.5f));
            Assert.That(before.y - after.y, Is.EqualTo(56f).Within(0.5f),
                "the correction is 56 px down; that is the 'turrets float a tile and a half up' defect");
        }

        [Test]
        public void TheSolvedOrigin_AgreesWithTheOracleGunOffset()
        {
            for (int i = 0; i < Mounts.Length; i++)
            {
                Assert.That(TurretSpriteOrigin.TryGetGunOffset(Mounts[i], out float offset), Is.True);
                Assert.That(offset, Is.EqualTo(OracleGunOffsets[i]).Within(0.001f),
                    $"{Mounts[i]}: the gun's SWF placement and UnitTurret.setWeaponPos disagree");
            }
        }

        /// <summary>
        /// The six measured origins, pinned.
        ///
        /// <para><b>Why a value assertion and not another property.</b> Everything else in this class
        /// checks a relationship — inside the frame, agrees with the oracle's gun offset — and the
        /// first solve of this table was wrong on three mounts while satisfying all of them. The
        /// in-frame test cannot even catch a transposed pair for <c>turret5</c>: its frame is 112x72,
        /// so <c>(31,58)</c> transposed to <c>(58,31)</c> is still inside it. A number that came from
        /// a measurement has to be written down as a number, or the next edit is unguarded.</para>
        ///
        /// <para>The values are reproducible: <c>docs/Diagnostics/scripts/turret_swf_probe.py</c>
        /// reprints them from <c>assets.swf</c> and fails if this table disagrees.</para>
        /// </summary>
        [Test]
        public void EveryMount_HasTheMeasuredOrigin()
        {
            var expected = new (string Id, Vector2Int Origin)[]
            {
                ("turret0", new Vector2Int(23, 104)),
                ("turret1", new Vector2Int(21, 83)),
                ("turret2", new Vector2Int(20, 43)),
                ("turret3", new Vector2Int(24, 98)),
                ("turret4", new Vector2Int(19, 41)),
                ("turret5", new Vector2Int(31, 58)),
            };

            foreach ((string id, Vector2Int origin) in expected)
            {
                Assert.That(TurretSpriteOrigin.TryGet(id, out Vector2Int actual), Is.True, $"{id}: no origin");
                Assert.That(actual, Is.EqualTo(origin),
                    $"{id}: origin is {actual}, the solve says {origin}");
            }
        }

        /// <summary>
        /// The baked body must be on the composite frame's own canvas.
        ///
        /// <para><b>This is the invariant the whole origin table rests on.</b> Every number in
        /// <see cref="TurretSpriteOrigin"/> is "frame pixels from the composite frame's top-left", so
        /// the body that replaces that composite has to be the same size — a body cropped to the art
        /// would shift every mount by the crop, and nothing in the game would look broken enough to
        /// notice. Skips rather than fails when the parts have not been baked, so a fresh checkout
        /// still compiles and tests.</para>
        /// </summary>
        [Test]
        public void TheBakedBody_IsOnTheCompositeCanvas()
        {
            int missing = 0;
            foreach (string id in Mounts)
            {
                Sprite body = Resources.Load<Sprite>(PFE.Entities.Units.TurretParts.BodyResourcePath(id));
                if (body == null)
                {
                    missing++;
                    continue;
                }

                Assert.That(TurretSpriteOrigin.TryGetFrameSize(id, out Vector2Int size), Is.True);
                Assert.That(body.rect.size, Is.EqualTo(new Vector2(size.x, size.y)),
                    $"{id}: the baked body is {body.rect.size} but the origin table assumes {size}");
            }

            if (missing == Mounts.Length)
            {
                Assert.Ignore("turret parts are not baked; run docs/Diagnostics/scripts/bake_turret_parts.py");
            }

            Assert.That(missing, Is.Zero, "some mounts have a baked body and some do not");
        }
    }
}
