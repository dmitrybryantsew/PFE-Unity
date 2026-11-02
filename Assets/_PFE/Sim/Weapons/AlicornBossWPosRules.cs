using UnityEngine;

namespace PFE.Systems.Weapons
{
    /// <summary>
    /// The Super Alicorn's per-animation-frame weapon anchor — AS3
    /// <c>BlitAnim.wPosAlicornBoss</c> (<c>fe/serv/BlitAnim.as:1834-1990</c>), transcribed verbatim,
    /// plus the row/frame lookup <c>UnitBossAlicorn.setWeaponPos</c> performs.
    ///
    /// <para><b>The override this replaces a constant with.</b> <c>UnitBossAlicorn.as:174-189</c>:
    /// </para>
    /// <code>
    /// override public function setWeaponPos(param1:int = 0) : *
    /// {
    ///    try
    ///    {
    ///       obj = this.wPos[anims[animState].id][Math.floor(anims[animState].f)];
    ///       weaponX = magicX = X + (obj.x + visBmp.x) * storona;
    ///       weaponY = magicY = Y + obj.y + visBmp.y;
    ///       weaponR = obj.r;
    ///    }
    ///    catch(err:*)
    ///    {
    ///       super.setWeaponPos(tip);
    ///    }
    /// }
    /// </code>
    /// <para>The <c>catch</c> is load-bearing: the table is shorter than the sheet in one row and the
    /// oracle indexes it blindly, so an out-of-range frame throws and the weapon falls back to the
    /// inherited body hold. <see cref="TryResolveCell"/> returns <c>false</c> for that case instead of
    /// throwing, which is the same answer without an exception.</para>
    ///
    /// <para><b>The outer index is the sprite ROW, not the animation name.</b>
    /// <c>Unit.as:1436-1439</c> builds <c>anims[xbl.@id] = new BlitAnim(xbl)</c> — keyed by the
    /// <c>id</c> string — but <c>BlitAnim</c>'s constructor assigns <c>this.id = param1.@y</c>
    /// (<c>BlitAnim.as:3686</c>), i.e. the <c>y</c> attribute, which is the sheet row. So
    /// <c>anims["die"].id</c> is <c>2</c>. For <c>bossalicorn</c> (<c>AllData.as:706-708</c>)
    /// <c>stay</c> is row 0, <c>fly</c> row 1 and <c>die</c> row 2, which is exactly the port's
    /// <c>UnitDefinition.animations</c> rows — the two agree because the importer transcribes the same
    /// attribute.</para>
    ///
    /// <para><b>Row lengths 20 / 14 / 20 against a 20-column sheet, and the two odd rows are odd for
    /// opposite reasons.</b> The <c>fly</c> row is <i>shorter</i> than the sheet — 14 entries — so a flat
    /// <c>index % columns</c> would read past its end on columns 14-19; that is the trap
    /// <see cref="PFE.Entities.Enemies.Archetypes.GriffinArmMath.TryResolveWPos"/> documents, and
    /// <see cref="TryResolveCell"/> clamps for it. The <c>die</c> row is <i>longer</i> than its clip —
    /// 20 entries while <c>&lt;blit id='die' y='2' len='14'/&gt;</c> (<c>AllData.as:708</c>) stops the
    /// frame counter at 13 — so its last six entries are unreachable <b>because of the blit, not because
    /// of the lookup</b>, and they are transcribed rather than trimmed. Two different reasons for a cell
    /// to be odd; conflating them would put a clamp where none belongs.</para>
    ///
    /// <para><b>What is deliberately NOT here: <c>r</c>.</b> Each oracle entry carries a rotation
    /// (<c>weaponR = obj.r</c>), and the whole roster has exactly one consumer of <c>weaponR</c>:
    /// <c>WClub.as:757</c>, <c>vis.rotation = 90*storona - 90 + owner.weaponR * storona</c>. That is a
    /// <b>melee club</b>. This boss's loadout is <c>alilight2</c>, <c>aliblade</c>, <c>alipsy2</c>,
    /// <c>alimray</c> (<c>UnitBossAlicorn.as:128</c>) — four ranged/spell weapons, no club — so the
    /// column is read by nothing. Carrying it here would invent a consumer, so the table stores the
    /// two numbers that are used.</para>
    ///
    /// <para><b>Cell pixels, y down.</b> Values are positions in the sheet cell's own pixel grid, origin
    /// at the cell's top-left — the space <c>wPos</c> is authored in. Turning them into a room-space
    /// offset needs <c>visBmp</c> and one sign flip; that arithmetic is
    /// <see cref="PFE.Entities.Enemies.Archetypes.GriffinArmMath.ArmOffsetPixels"/>, which is the same
    /// two AS3 lines (<c>UnitMerc.as:229-230</c>) and is reused rather than copied.</para>
    /// </summary>
    public static class AlicornBossWPosRules
    {
        /// <summary>
        /// AS3 <c>BlitAnim.wPosAlicornBoss</c>, indexed <c>[sheet row][frame]</c>.
        ///
        /// <para>Row order is the sheet's: <b>0</b> <c>stay</c> (20), <b>1</b> <c>fly</c> (14),
        /// <b>2</b> <c>die</c> (20, of which the oracle's 14-frame clip uses the first 14). Values are
        /// cell pixels with y running <b>down</b>, transcribed from
        /// <c>BlitAnim.as:1834-1990</c>.</para>
        ///
        /// <para><b>They are not offsets from the unit origin.</b> They are absolute cell coordinates;
        /// the unit's origin sits at the cell pixel <c>(sprDX, sprDY)</c> = <c>(105, 150)</c> for
        /// <c>sprAlicorn4</c>, and subtracting that is what
        /// <see cref="PFE.Entities.Enemies.Archetypes.GriffinArmMath.ArmOffsetPixels"/> does. Read
        /// raw, row 0 frame 0 (<c>146, 21</c>) would put the weapon 146 px right and 21 px <i>below the
        /// top of the cell</i>; after the offset it is 41 px right of the feet and 129 px above them,
        /// i.e. at the horn — which is where a <c>alimray</c> ray and a thrown blade come from.</para>
        /// </summary>
        public static readonly Vector2Int[][] Rows =
        {
            // row 0 — stay (20 frames). A small breathing drift: x 146→150→146, y 21→23→20.
            new[]
            {
                new Vector2Int(146,  21), new Vector2Int(146,  21), new Vector2Int(147,  21), new Vector2Int(148,  22),
                new Vector2Int(148,  22), new Vector2Int(149,  23), new Vector2Int(149,  23), new Vector2Int(149,  23),
                new Vector2Int(149,  23), new Vector2Int(149,  23), new Vector2Int(150,  23), new Vector2Int(149,  22),
                new Vector2Int(148,  22), new Vector2Int(147,  21), new Vector2Int(147,  21), new Vector2Int(146,  20),
                new Vector2Int(146,  20), new Vector2Int(146,  21), new Vector2Int(146,  21), new Vector2Int(146,  21),
            },

            // row 1 — fly (14 frames). The wing beat: a flat x with a 2px vertical swing (29→27→28).
            new[]
            {
                new Vector2Int(152,  29), new Vector2Int(152,  28), new Vector2Int(152,  28), new Vector2Int(152,  28),
                new Vector2Int(152,  27), new Vector2Int(152,  27), new Vector2Int(152,  27), new Vector2Int(152,  27),
                new Vector2Int(152,  27), new Vector2Int(152,  27), new Vector2Int(152,  27), new Vector2Int(152,  28),
                new Vector2Int(152,  28), new Vector2Int(152,  28),
            },

            // row 2 — die (20 frames, of which the oracle's `die` clip is 14). The 14 reachable frames
            // are the fall: x 152→80 as she goes down. Entries 14-19 (94,8 → 139,19) are unreachable —
            // the clip's `len='14'` never gets there — and are transcribed because the table is the
            // artefact, not a tidied-up version of it.
            new[]
            {
                new Vector2Int(152,  29), new Vector2Int(143,  21), new Vector2Int(133,  16), new Vector2Int(124,  12),
                new Vector2Int(115,  10), new Vector2Int(107,   9), new Vector2Int(101,   9), new Vector2Int( 95,   9),
                new Vector2Int( 90,  10), new Vector2Int( 86,  10), new Vector2Int( 83,  11), new Vector2Int( 82,  11),
                new Vector2Int( 81,  11), new Vector2Int( 80,  11), new Vector2Int( 80,  11), new Vector2Int( 79,  11),
                new Vector2Int( 94,   8), new Vector2Int(109,   8), new Vector2Int(125,  12), new Vector2Int(139,  19),
            },
        };

        /// <summary>The number of rows in <see cref="Rows"/> — the sheet rows the table covers.</summary>
        public static int RowCount => Rows.Length;

        /// <summary>
        /// The anchor for a sheet cell index, or <c>false</c> when the cell lies outside the table —
        /// which is the oracle's <c>catch</c> branch, not an error.
        /// </summary>
        /// <remarks>
        /// <para><b>The row's own length decides the clamp, not the column count.</b> Row 1 has 14
        /// entries in a 20-column sheet; <c>frame = cellIndex % 20</c> would index past its end. AS3
        /// would read <c>undefined</c> and throw inside <c>obj.x</c>, landing on the body hold; clamping
        /// keeps the weapon on the row's last authored frame instead, which is the position the
        /// animation itself is holding at that moment. Both are "not the authored value"; only one of
        /// them moves the weapon.</para>
        ///
        /// <para>A negative or zero <paramref name="columns"/> returns <c>false</c> rather than
        /// dividing by zero — the state of a definition whose sheet geometry has not been imported.</para>
        /// </remarks>
        /// <param name="cellIndex"><c>UnitAnimator.CurrentCellIndex</c> — <c>row * columns + column</c>, or <c>-1</c>.</param>
        /// <param name="columns"><c>UnitDefinition.spriteSheetColumns</c>.</param>
        /// <param name="wPos">The table entry, in cell pixels with y down.</param>
        public static bool TryResolveCell(int cellIndex, int columns, out Vector2Int wPos)
        {
            wPos = default;
            if (cellIndex < 0 || columns <= 0)
            {
                return false;
            }

            int row = cellIndex / columns;
            if (row < 0 || row >= Rows.Length)
            {
                return false;
            }

            Vector2Int[] frames = Rows[row];
            if (frames.Length == 0)
            {
                return false;
            }

            int frame = Mathf.Clamp(cellIndex % columns, 0, frames.Length - 1);
            wPos = frames[frame];
            return true;
        }
    }
}
