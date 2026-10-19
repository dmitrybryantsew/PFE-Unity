using PFE.Data.Definitions;
using UnityEngine;

namespace PFE.Entities.Units
{
    /// <summary>
    /// Where the unit's origin sits inside an imported <b>DisplayObject</b> frame — the six turret
    /// mounts, whose art is a Flash MovieClip rather than a blit bitmap.
    ///
    /// <para><b>Why the turrets need a table and every other unit does not.</b> A blit unit's resting
    /// frame is a cell of a sprite sheet, and AS3 anchors it with <c>Unit.initBlit</c>
    /// (<c>Unit.as:2844-2859</c>): the bitmap's bottom edge sits 10 px below the unit's origin unless
    /// the row declares <c>sprDX</c>/<c>sprDY</c>. <see cref="UnitSpriteAnchor"/> implements exactly
    /// that, and it is correct for the ~290 sheet units. A turret is not a blit: <c>UnitTurret</c>
    /// assigns <c>vis = new visualTurret0()</c> and <c>setVisPos</c> does <c>vis.x = X; vis.y = Y</c>
    /// (<c>UnitTurret.as:288-292</c>), so the symbol's own registration point — not a 10 px feet gap —
    /// is what lands on the unit's origin. The exported frame is a 104x170 canvas whose art occupies a
    /// small band in the middle of it, so the blit rule plus Unity's centre pivot put the mount
    /// <b>28.5 px left and 56 px above</b> where the oracle draws it: the turret floats a tile and a
    /// half over the room and hangs off the side of its placement cell.</para>
    ///
    /// <para><b>Where the numbers come from — measured, not guessed.</b> The registration point of a
    /// MovieClip frame is not in the PNG; FFDec exports the frame cropped to the symbol's bounds and
    /// drops the origin. It is recoverable from the source SWF, because the origin of a child is
    /// written in its <c>PlaceObject2</c> matrix:</para>
    /// <code>
    /// pfe/scripts/_assets/assets.swf
    ///   DefineSprite_1835_visualTurret0  1 frame, places `osn` (1834) at identity
    ///     DefineSprite_1834_pfe_fla.osn0_1260  12 frames
    ///       depth 1  cid 1831 (body art, scale 0.2082)
    ///       depth 3  cid 1796 `puha` (gun, 10 frames) at (-0.35, -13.8)
    ///       depth 5  cid 1801 `light` (lamp, 6 frames) at (0, -16.5)
    /// </code>
    /// <para>The gun's placement is a constant the oracle also states as code —
    /// <c>setWeaponPos</c>'s <c>weaponY = Y - 12</c> for tip 0 (<c>UnitTurret.as:425-428</c>) — and the
    /// two agree to a tenth of a pixel on every mount (see the table). So each origin below was solved
    /// as <c>gunPlacement + gunOriginInItsOwnFrame - gunPlacementInTheParent</c>, where the middle term
    /// is the gun frame's own bounds (also read from the SWF) and the last is the matrix above.</para>
    ///
    /// <para><b>The first solve of this table was wrong on three of the six, and passed every check it
    /// had.</b> The middle term — "where in the composite frame is the gun" — was found by maximising
    /// the raw overlap of the gun's mask with the composite's, and an unnormalised count rewards a gun
    /// laid over the body's largest blob: <c>turret1</c> and <c>turret2</c> came out 5 px left of the
    /// truth and <c>turret5</c> 12 px left and 18 px high, while the frame size, the in-frame test and
    /// the oracle cross-check all still passed. The fix is to score <i>agreement of the fully opaque
    /// silhouettes</i> — every gun pixel on composite ink, no gun pixel over empty canvas — which has a
    /// unique optimum on all six. The re-solve also makes the whole-assembly check behave: rendering
    /// body + gun + lamp at these origins and XOR-ing against the composite frame puts the minimum at
    /// (0,0) for every mount, and <c>turret5</c>'s residual falls from 35 % of its pixels to 1.5 %.</para>
    ///
    /// <para><b>Why a code table rather than <see cref="UnitDefinition.registrationPoint"/>.</b> That
    /// field is written unconditionally by <c>UnitDataImporter</c> from the row's <c>sprDX</c>/<c>sprDY</c>
    /// (<c>UnitDataImporter.cs:1029-1031</c>), and no turret row authors either attribute, so a value
    /// placed in the asset would be reset to <c>(-1,-1)</c> by the next
    /// <c>PFE/Data/Reimport Units</c> — the classic "correct until someone re-imports" data fix. The
    /// numbers belong to the ART, and the art is imported by <c>UnitSpriteImporter</c>, so they live
    /// here, in one place both readers can reach.</para>
    /// </summary>
    public static class TurretSpriteOrigin
    {
        /// <summary>
        /// The unit id → registration point, in <b>frame pixels from the frame's top-left corner</b>.
        ///
        /// <para>That is the convention <see cref="UnitSpriteAnchor.PixelOffset"/> already consumes:
        /// <c>desiredX = reg.x</c> and <c>desiredY = spriteHeight - reg.y</c>. The values are the
        /// solved origins rounded to whole pixels; the sub-pixel remainder (at most 0.45 px) is below
        /// what the camera can show.</para>
        ///
        /// <para>The second column is the same origin expressed as the oracle's own
        /// <c>weaponY = Y - n</c> for that mount, which is the independent check that the solve is
        /// right: the gun's matrix translation and the hard-coded offset must be the same number.</para>
        /// </summary>
        static readonly (string UnitId, Vector2Int Origin, float GunOffsetAboveOrigin)[] Origins =
        {
            // unit id   origin (frame px)   oracle's weaponY = Y - n
            ("turret0", new Vector2Int(23, 104), 13.8f),   // floor / ceiling mount, 104x170 frame
            ("turret1", new Vector2Int(21, 83),  60.0f),   // land mount, 102x83
            ("turret2", new Vector2Int(20, 43),  20.0f),   // wall mount, 101x45
            ("turret3", new Vector2Int(24, 98),  55.0f),   // arm mount, 105x97
            ("turret4", new Vector2Int(19, 41),  13.8f),   // combat mount, 100x49
            ("turret5", new Vector2Int(31, 58),  20.0f),   // boss mount, 112x72
        };

        /// <summary>
        /// The registration point for a turret mount, or false for any other unit.
        ///
        /// <para>Exact id match only. The family template <c>turret</c> is deliberately absent: it is a
        /// category row with no art (<c>AllData.as:1758</c>), and a prefix match would silently give it
        /// the floor mount's numbers and make a mis-resolved spawn look correct.</para>
        /// </summary>
        public static bool TryGet(string unitId, out Vector2Int origin)
        {
            if (!string.IsNullOrEmpty(unitId))
            {
                for (int i = 0; i < Origins.Length; i++)
                {
                    if (string.Equals(Origins[i].UnitId, unitId, System.StringComparison.OrdinalIgnoreCase))
                    {
                        origin = Origins[i].Origin;
                        return true;
                    }
                }
            }

            origin = new Vector2Int(-1, -1);
            return false;
        }

        /// <summary>
        /// The registration point to hand <see cref="UnitSpriteAnchor.ApplyTo"/> for
        /// <paramref name="unitId"/>: the measured DisplayObject origin when this unit is a turret,
        /// otherwise whatever the definition declares.
        ///
        /// <para>One method rather than two call sites each doing the lookup, because there are two
        /// readers of a unit's sprite — <c>RoomUnitSpawner</c>'s resting frame and
        /// <see cref="UnitAnimator"/>'s cells — and an override applied at only one of them is how a
        /// unit ends up standing in one place and jumping when it animates.</para>
        /// </summary>
        public static Vector2Int Resolve(string unitId, Vector2Int declared)
        {
            return TryGet(unitId, out Vector2Int origin) ? origin : declared;
        }

        /// <inheritdoc cref="Resolve(string, Vector2Int)"/>
        public static Vector2Int Resolve(UnitDefinition definition)
        {
            if (definition == null)
            {
                return new Vector2Int(-1, -1);
            }

            return Resolve(definition.id, definition.registrationPoint);
        }

        /// <summary>
        /// The frame each mount's art was exported at, in pixels. Used by the tests to prove the origin
        /// above lies inside the frame it belongs to — an origin outside its own bitmap is the shape a
        /// transposed pair of numbers takes, and it would draw the mount a whole frame away.
        /// </summary>
        public static bool TryGetFrameSize(string unitId, out Vector2Int size)
        {
            switch (unitId)
            {
                case "turret0": size = new Vector2Int(104, 170); return true;
                case "turret1": size = new Vector2Int(102, 83); return true;
                case "turret2": size = new Vector2Int(101, 45); return true;
                case "turret3": size = new Vector2Int(105, 97); return true;
                case "turret4": size = new Vector2Int(100, 49); return true;
                case "turret5": size = new Vector2Int(112, 72); return true;
                default: size = Vector2Int.zero; return false;
            }
        }

        /// <summary>
        /// The gun offset above the origin that the solve produced, for the cross-check the class
        /// summary describes. Kept public so the test can assert it against
        /// <c>UnitTurret.setWeaponPos</c> rather than restating the number twice.
        /// </summary>
        public static bool TryGetGunOffset(string unitId, out float gunOffsetAboveOrigin)
        {
            if (!string.IsNullOrEmpty(unitId))
            {
                for (int i = 0; i < Origins.Length; i++)
                {
                    if (string.Equals(Origins[i].UnitId, unitId, System.StringComparison.OrdinalIgnoreCase))
                    {
                        gunOffsetAboveOrigin = Origins[i].GunOffsetAboveOrigin;
                        return true;
                    }
                }
            }

            gunOffsetAboveOrigin = 0f;
            return false;
        }
    }
}
