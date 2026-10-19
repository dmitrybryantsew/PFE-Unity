using UnityEngine;

namespace PFE.Entities.Enemies.Archetypes
{
    /// <summary>
    /// The griffin's separate aiming arm — AS3 <c>UnitMerc.arm</c>, <c>fe/unit/UnitMerc.as</c>.
    ///
    /// <para><b>Why a griffin needs an arm at all.</b> Every other unit in the family draws its weapon
    /// at a fixed point on its body (<c>Unit.setWeaponPos</c>, the port's
    /// <see cref="PFE.Systems.Weapons.WeaponHoldRule"/>). <c>UnitMerc</c> is one of the classes that
    /// overrides that: it owns a <i>second display object</i> — a front-limb clip chosen per variant
    /// (<c>visualGrifArm1..5</c>) — rotates it with the weapon's aim, and hangs the weapon on the arm's
    /// <c>emit</c> marker instead of on the body
    /// (<c>UnitMerc.as:29</c>, <c>:85-91</c>, <c>:95-109</c>, <c>:229-230</c>).</para>
    ///
    /// <para><b>Everything in this file is transcribed or measured, nothing is invented.</b> Three
    /// independent sources feed it, and each number says which:</para>
    /// <list type="number">
    /// <item><description><see cref="WPosGriffon1"/> — <c>BlitAnim.wPosGriffon1</c>
    /// (<c>fe/serv/BlitAnim.as:2052-2295</c>), the per-frame arm-origin table, transcribed.</description></item>
    /// <item><description><see cref="ArmOffsetPixels"/> — <c>UnitMerc.as:229-230</c> plus
    /// <c>Unit.initBlit</c>'s <c>visBmp</c> rule (<c>Unit.as:2844-2859</c>), transcribed.</description></item>
    /// <item><description><see cref="Variants"/> — <b>measured from the SWF</b>, because the arm's
    /// registration point and its <c>emit</c> bone exist nowhere in the AS3 source. See
    /// <see cref="GriffinArmVariant"/>.</description></item>
    /// </list>
    ///
    /// <para><b>Coordinate systems, stated once because this file mixes three.</b></para>
    /// <list type="bullet">
    /// <item><description><b>Cell pixels</b> — the sheet cell, x right, y <b>down</b>, origin at the
    /// cell's top-left. This is the space <c>wPos</c> and <c>emit</c> are authored in.</description></item>
    /// <item><description><b>Room pixels</b> — the port's, x right, y <b>up</b>, origin at the unit's
    /// feet. <c>WorldCoordinates.PixelToUnity</c> is a straight /100 scale with no flip.</description></item>
    /// <item><description><b>Sprite pixels</b> — Unity's, from the sprite's <b>bottom-left</b>, which is
    /// what <see cref="Sprite.pivot"/> and <see cref="Sprite.rect"/> use.</description></item>
    /// </list>
    /// <para>AS3 y runs down and the port's runs up — the flip is applied at exactly two places,
    /// <see cref="ArmOffsetPixels"/> and <see cref="EmitLocalPixels"/>, and nowhere else.</para>
    /// </summary>
    public static class GriffinArmMath
    {
        /// <summary>
        /// The griffin's arm-origin table — AS3 <c>BlitAnim.wPosGriffon1</c>, transcribed verbatim.
        ///
        /// <para>Indexed <c>[sheet row][frame within that row]</c>, exactly like
        /// <c>UnitMerc.animate</c>'s <c>wPos[anims[animState].id][Math.floor(anims[animState].f)]</c>
        /// (<c>UnitMerc.as:228</c>). The row is the AS3 <c>&lt;blit y=…/&gt;</c> of the state
        /// (<c>AllData.as:416-422</c>): <c>walk</c>=0, <c>fly</c>=1, <c>jump</c>=1, <c>die</c>=2,
        /// <c>death</c>=3, <c>fall</c>=3, <c>stay</c>=0 (no <c>y</c>, so row 0, frame 0).</para>
        ///
        /// <para><b>The row lengths are 25 / 14 / 25 / 17, not four times 25.</b> The sheet itself is
        /// 25 columns by 4 rows (<c>sprGriffon1.png</c> is 4250x680 at a 170px cell), so rows 1 and 3
        /// are short and the table is short with them. Indexing it with a flat cell index therefore
        /// needs the row's <i>own</i> length — see <see cref="TryResolveWPos"/>.</para>
        ///
        /// <para>Values are <b>cell pixels</b> (y down). They are not offsets from anything: as the
        /// table confirms arithmetically, <c>wPos</c> is the arm origin's position in the cell's own
        /// pixel grid. For <c>sprGriffon1</c> the unit origin sits at cell pixel (96, 132) (its
        /// <c>sprDX</c>/<c>sprDY</c>), and <c>arm.x = X + (104 - 96) = X + 8</c> puts the arm 8px right
        /// of the unit origin — which is cell pixel 96 + 8 = 104, the table's own value.</para>
        /// </summary>
        public static readonly Vector2Int[][] WPosGriffon1 =
        {
            // row 0 — walk (25 frames). A flat hold: the arm origin does not move while walking.
            new[]
            {
                new Vector2Int(104, 105), new Vector2Int(104, 105), new Vector2Int(104, 105),
                new Vector2Int(104, 105), new Vector2Int(104, 105), new Vector2Int(104, 105),
                new Vector2Int(104, 105), new Vector2Int(104, 105), new Vector2Int(104, 105),
                new Vector2Int(104, 105), new Vector2Int(104, 105), new Vector2Int(104, 105),
                new Vector2Int(104, 105), new Vector2Int(104, 105), new Vector2Int(104, 105),
                new Vector2Int(104, 105), new Vector2Int(104, 105), new Vector2Int(104, 105),
                new Vector2Int(104, 105), new Vector2Int(104, 105), new Vector2Int(104, 105),
                new Vector2Int(104, 105), new Vector2Int(104, 105), new Vector2Int(104, 105),
                new Vector2Int(104, 105),
            },

            // row 1 — fly / jump (14 frames). The 7px vertical swing is the wing beat.
            new[]
            {
                new Vector2Int(105, 105), new Vector2Int(104, 104), new Vector2Int(104, 103),
                new Vector2Int(104, 102), new Vector2Int(104, 101), new Vector2Int(104, 100),
                new Vector2Int(104, 99),  new Vector2Int(104, 98),  new Vector2Int(104, 99),
                new Vector2Int(104, 100), new Vector2Int(104, 101), new Vector2Int(104, 102),
                new Vector2Int(104, 103), new Vector2Int(104, 104),
            },

            // row 2 — die (25 frames).
            new[]
            {
                new Vector2Int(104, 105), new Vector2Int(104, 105), new Vector2Int(104, 106),
                new Vector2Int(104, 106), new Vector2Int(104, 107), new Vector2Int(104, 108),
                new Vector2Int(103, 108), new Vector2Int(103, 109), new Vector2Int(103, 110),
                new Vector2Int(103, 111), new Vector2Int(102, 111), new Vector2Int(102, 112),
                new Vector2Int(102, 113), new Vector2Int(102, 114), new Vector2Int(102, 115),
                new Vector2Int(102, 116), new Vector2Int(102, 117), new Vector2Int(102, 118),
                new Vector2Int(101, 119), new Vector2Int(101, 119), new Vector2Int(102, 119),
                new Vector2Int(102, 119), new Vector2Int(102, 119), new Vector2Int(101, 119),
                new Vector2Int(101, 119),
            },

            // row 3 — death / fall (17 frames).
            new[]
            {
                new Vector2Int(107, 92),  new Vector2Int(105, 100), new Vector2Int(104, 105),
                new Vector2Int(104, 106), new Vector2Int(104, 107), new Vector2Int(104, 108),
                new Vector2Int(103, 108), new Vector2Int(103, 109), new Vector2Int(103, 110),
                new Vector2Int(103, 111), new Vector2Int(102, 111), new Vector2Int(102, 112),
                new Vector2Int(102, 113), new Vector2Int(102, 114), new Vector2Int(102, 115),
                new Vector2Int(102, 116), new Vector2Int(101, 119),
            },
        };

        /// <summary>
        /// One griffin arm variant: which symbol to draw, where its origin sits inside the exported
        /// PNG, and where the weapon hangs on it.
        ///
        /// <para><b>These numbers are measured from <c>_assets/assets.swf</c>, not transcribed.</b> The
        /// AS3 source never says where the arm's origin is — it is the <c>MovieClip</c>'s registration
        /// point, which lives in the symbol's display list — and it never says where <c>emit</c> is
        /// either (<c>public var emit:MovieClip</c> is just a declaration; the placement is in the
        /// SWF). Both were read out of the tag stream:</para>
        /// <list type="bullet">
        /// <item><description><b>emit</b> — the <c>PlaceObject2</c> with instance name <c>emit</c> inside
        /// each arm's <c>DefineSprite</c>. Its matrix translation is in <b>twips</b> (1/20 px) while the
        /// scale and rotate terms are 16.16 fixed; mixing the two units makes the marker land 20x too
        /// far away, which is how the unit convention was caught.</description></item>
        /// <item><description><b>origin</b> — the union of the arm's children's bounds, transformed by
        /// their placement matrices, with the PNG's top-left at <c>(-minX, -minY)</c> in arm-local
        /// space. Nothing is excluded: an earlier reading of this table believed one placement per arm
        /// carried bounds but drew nothing, which was really a bug in the probe — it applied the SWF
        /// matrix as <c>x' = a·x + b·y</c> when the spec is <c>x' = a·x + c·y</c>. Three of a limb's
        /// four parts are unrotated, so <c>b = c = 0</c> hid the swap and only the rotated fourth part
        /// was thrown 17px out. With the coefficients the right way round all five placements sit
        /// inside the PNG. See <c>griffin_arm_swf_probe.py</c>'s <c>placed_corners</c>.</description></item>
        /// </list>
        ///
        /// <para><b>The measurement is falsified against the art, not trusted.</b> Every variant's
        /// computed box matches its exported PNG (33.48x29.48 vs 33x29; 35.87x32.76 vs 35x32;
        /// 35.44x33.00 vs 35x33; 35.44x30.24 vs 35x30; 35.44x30.72 vs 35x30) — ten independent
        /// numbers agreeing on a chain of four conversions (twips, per-field bit widths, the matrix
        /// convention, the y flip). And <c>emit</c> lands in the gold gauntlet on all five, which is
        /// where a weapon grip has to be. The probe re-checks both and exits non-zero on drift.</para>
        /// </summary>
        public readonly struct GriffinArmVariant
        {
            /// <summary>AS3 <c>Res.getVis("visualGrifArm" + tr, …)</c> — <c>UnitMerc.as:29</c>.</summary>
            public readonly string SymbolName;

            /// <summary>
            /// Where the arm's <b>registration point</b> sits inside its exported PNG, in sprite
            /// pixels from the PNG's <b>top-left</b> (y down, to match the cell-pixel space the rest
            /// of this file reasons in).
            ///
            /// <para>The arm rotates about this point, so it is the shoulder. Getting it wrong does
            /// not move the arm relative to the weapon — both hang off the same origin — it moves the
            /// pair relative to the body, by exactly the error.</para>
            /// </summary>
            public readonly Vector2 OriginPixels;

            /// <summary>
            /// The <c>emit</c> bone in <b>arm-local</b> pixels (y down) — where
            /// <c>UnitMerc.setWeaponPos</c> hangs the weapon (<c>UnitMerc.as:104-108</c>).
            ///
            /// <para>Y is down here, so a positive value is <i>below</i> the shoulder: at aim angle 0
            /// the hand hangs down-right of the shoulder and the arm art points along +x, which is
            /// what makes <c>arm.rotation = weapon.rot</c> line the limb up with the barrel.</para>
            /// </summary>
            public readonly Vector2 EmitPixels;

            public GriffinArmVariant(string symbolName, Vector2 originPixels, Vector2 emitPixels)
            {
                SymbolName   = symbolName;
                OriginPixels = originPixels;
                EmitPixels   = emitPixels;
            }
        }

        /// <summary>
        /// The five arms, in AS3 <c>tr</c> order — index 0 is <c>visualGrifArm1</c>, which is the
        /// <c>merc1</c> griffin drawn from <c>sprGriffon1</c>. The numbering is shared: the arm is
        /// chosen with <c>Res.getVis("visualGrifArm" + tr)</c> and the sheet with
        /// <c>&lt;vis blit='sprGriffon' + tr&gt;</c>, so <c>sprGriffonN</c> ⇒ <c>visualGrifArmN</c>
        /// (<c>AllData.as:428,441,458,475,489</c>).
        /// </summary>
        public static readonly GriffinArmVariant[] Variants =
        {
            new GriffinArmVariant("visualGrifArm1", new Vector2(10.37f, 6.61f), new Vector2(16.30f, 14.95f)),
            new GriffinArmVariant("visualGrifArm2", new Vector2(10.80f, 9.88f), new Vector2(17.30f, 10.95f)),
            new GriffinArmVariant("visualGrifArm3", new Vector2(10.37f, 10.04f), new Vector2(17.95f, 10.20f)),
            new GriffinArmVariant("visualGrifArm4", new Vector2(10.37f, 7.36f), new Vector2(17.95f, 10.20f)),
            new GriffinArmVariant("visualGrifArm5", new Vector2(10.37f, 7.36f), new Vector2(17.95f, 10.20f)),
        };

        /// <summary>
        /// The 0-based arm index for a body sheet id, or <c>-1</c> when the unit is not a griffin —
        /// AS3's <c>tr</c>, minus one.
        ///
        /// <para><b>The pairing is the oracle's, and it is why this reads the sheet id rather than a
        /// table of unit ids.</b> <c>UnitMerc</c> picks the limb with
        /// <c>Res.getVis("visualGrifArm" + tr)</c> (<c>UnitMerc.as:29</c>) and the body with
        /// <c>&lt;vis blit='sprGriffon' + tr&gt;</c> (<c>AllData.as:428,441,458,475,489</c>) — one
        /// number, two lookups. A hand-written <c>merc1..5</c> map would be a second copy of that
        /// pairing and could disagree with the data the moment a variant is added or renamed. Both the
        /// importer and the runtime call this, so they cannot disagree either.</para>
        /// </summary>
        public static int VariantIndexForSpriteSheet(string spriteSheetId)
        {
            const string prefix = "sprGriffon";

            if (string.IsNullOrEmpty(spriteSheetId) ||
                !spriteSheetId.StartsWith(prefix, System.StringComparison.Ordinal))
            {
                return -1;
            }

            string suffix = spriteSheetId.Substring(prefix.Length);
            if (!int.TryParse(suffix, out int variant))
            {
                return -1;
            }

            return variant >= 1 && variant <= Variants.Length ? variant - 1 : -1;
        }

        /// <summary>
        /// AS3's <c>visBmp</c> — the offset from the unit's origin to the sprite cell's top-left, in
        /// cell pixels (y down). <c>Unit.as:2844-2859</c>:
        /// <code>
        /// visBmp.x = blitDX &gt;= 0 ? -blitDX : -blitX / 2;
        /// visBmp.y = blitDY &gt;= 0 ? -blitDY : -blitY + 10;
        /// </code>
        /// A negative registration component means "not declared" and selects the centring fallback —
        /// the same presence test <c>PFE.Entities.Units.UnitSpriteAnchor</c> performs, which is the
        /// port's other user of this rule.
        /// </summary>
        public static Vector2 SpriteBitmapOffsetPixels(Vector2Int registrationPoint, Vector2Int cellSize)
        {
            float x = registrationPoint.x >= 0 ? -registrationPoint.x : -cellSize.x * 0.5f;
            float y = registrationPoint.y >= 0 ? -registrationPoint.y : -cellSize.y + 10f;
            return new Vector2(x, y);
        }

        /// <summary>
        /// The arm node's offset from the unit's origin, in <b>room pixels</b> (y up) — AS3
        /// <c>UnitMerc.as:229-230</c>:
        /// <code>
        /// arm.x = X + (wPos.x + visBmp.x) * storona;
        /// arm.y = Y +  wPos.y + visBmp.y;
        /// </code>
        ///
        /// <para><b>The <c>* storona</c> is deliberately absent.</b> In AS3 the arm is a sibling of
        /// <c>vis</c> on an unscaled layer, so the mirror has to be written into the x itself. In the
        /// port the arm node hangs under the unit root, which already carries
        /// <c>localScale.x = facing</c>, so a local x is mirrored by the parent and writing it again
        /// would cancel it. The y term needs no mirror either way — a horizontal mirror does not touch
        /// it — and its sign flips once, because AS3 y runs down and room y runs up.</para>
        /// </summary>
        /// <param name="wPos">The table entry for the current row and frame (cell pixels, y down).</param>
        public static Vector2 ArmOffsetPixels(Vector2Int wPos, Vector2Int registrationPoint, Vector2Int cellSize)
        {
            Vector2 visBmp = SpriteBitmapOffsetPixels(registrationPoint, cellSize);
            return new Vector2(wPos.x + visBmp.x, -(wPos.y + visBmp.y));
        }

        /// <summary>
        /// The <c>emit</c> bone in <b>room pixels</b> relative to the arm node's own origin (y up) —
        /// the point <c>arm.localToGlobal(emit)</c> resolves to once the node's rotation and mirror
        /// are applied.
        /// </summary>
        public static Vector2 EmitLocalPixels(Vector2 emitPixels) => new Vector2(emitPixels.x, -emitPixels.y);

        /// <summary>
        /// Where to draw the arm's art inside the arm node so its <b>registration point</b> — not its
        /// centre — sits on the node's origin, in room pixels (y up).
        ///
        /// <para>Derived from the sprite actually loaded rather than from a constant, so it is
        /// self-correcting: an import that already carries the measured origin yields exactly zero,
        /// and one that carries Unity's default centre yields the difference. That is the same
        /// argument, and the same arithmetic, as <c>UnitSpriteAnchor.PixelOffset</c>; the only
        /// difference is that the arm's registration point is measured off the SWF and is therefore
        /// fractional, so this takes floats where that one takes a <c>Vector2Int</c>.</para>
        ///
        /// <para><paramref name="spritePivotPixels"/> is Unity's pivot measured from the sprite's
        /// bottom-left, while <paramref name="originPixels"/> is measured from its top-left (the
        /// cell-pixel convention). The one flip between them is the <c>spriteSize.y - origin.y</c>
        /// below.</para>
        /// </summary>
        public static Vector2 ArmArtOffsetPixels(Vector2 spritePivotPixels, Vector2 spriteSizePixels, Vector2 originPixels)
        {
            Vector2 desiredFromBottomLeft = new Vector2(originPixels.x, spriteSizePixels.y - originPixels.y);
            Vector2 offset = spritePivotPixels - desiredFromBottomLeft;
            return new Vector2(offset.x, offset.y);
        }

        /// <summary>
        /// The arm node's z rotation in degrees — AS3 <c>UnitMerc.as:89</c>:
        /// <c>arm.rotation = currentWeapon.rot * 180 / Math.PI + 90 * (1 - storona)</c>.
        ///
        /// <para><b><c>90 * (1 - storona)</c> is <c>facingLeft ? 180 : 0</c></b>, which makes this
        /// exactly <c>WeaponVisMath.VisRotationDeg(rot, 0, facingLeft)</c> — the rule the port already
        /// applies to a held weapon's <c>vis</c>. That identity is not a coincidence to preserve
        /// loosely: it is why the arm and the gun cannot drift apart, and the rig calls the shared
        /// helper rather than a copy of it.</para>
        /// </summary>
        public static float ArmRotationDeg(float rotRad, bool facingLeft)
            => rotRad * Mathf.Rad2Deg + (facingLeft ? 180f : 0f);

        /// <summary>
        /// The <c>wPos</c> entry for a sheet cell index, or <c>false</c> when the cell lies outside
        /// the table (a unit whose sheet has more rows than the arm table, or an
        /// <see cref="PFE.Entities.Units.UnitAnimator.CurrentCellIndex"/> of <c>-1</c>).
        ///
        /// <para><b>The row length has to come from the table, not from the column count.</b> Rows 1
        /// and 3 hold 14 and 17 entries in a 25-column sheet, so <c>frame = index % 25</c> reads past
        /// the end of the short rows. Resolving through the row's own length keeps a short row's last
        /// frame at its last entry instead of falling off it.</para>
        /// </summary>
        public static bool TryResolveWPos(int cellIndex, int columns, out Vector2Int wPos)
        {
            wPos = default;
            if (cellIndex < 0 || columns <= 0)
            {
                return false;
            }

            int row = cellIndex / columns;
            int column = cellIndex % columns;
            if (row < 0 || row >= WPosGriffon1.Length)
            {
                return false;
            }

            Vector2Int[] frames = WPosGriffon1[row];
            if (frames.Length == 0)
            {
                return false;
            }

            // A row shorter than the sheet is padded by clamping: AS3 would index past the end and
            // read `undefined`, which throws inside `_loc3_.x`. Clamping is the only non-throwing
            // answer that keeps the arm where the row's last authored frame puts it.
            int frame = Mathf.Clamp(column, 0, frames.Length - 1);
            wPos = frames[frame];
            return true;
        }
    }
}
