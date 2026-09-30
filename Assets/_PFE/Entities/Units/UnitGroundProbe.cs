using UnityEngine;
using PFE.Systems.Map.TileQuery;

namespace PFE.Entities.Units
{
    /// <summary>
    /// Where a unit's ground probe must be placed so that the tile query answers the question AS3
    /// actually asks — <c>isLaz</c>, "am I standing on something" (<c>Unit.as:1962</c>).
    ///
    /// <para><b>Why this type exists at all.</b> The port decided groundedness from Unity collision
    /// callbacks (<c>OnCollisionEnter2D</c> / <c>Stay2D</c> / <c>Exit2D</c>), which is a <i>different
    /// question</i> from AS3's, and it is a question whose answer is <b>marginal by construction</b>
    /// here:</para>
    ///
    /// <list type="number">
    /// <item>A unit is seated <b>1 px above</b> the tile surface — <c>RoomPopulator</c>'s
    /// <c>ResolveLegacyBottomAnchorPixels</c> ends <c>… * TILE_SIZE + 1f</c>, which is the port's form
    /// of AS3's step-up allowance (<c>porog</c> = 10 px, <c>TileQueryConstants.PorogGrounded</c>).</item>
    /// <item>The unit's collider bottom is its origin (the feet), so the box bottom sits exactly 1 px
    /// above the tile's top edge (<c>RoomUnitSpawner</c>, <c>offset = (0, +Height/2)</c>).</item>
    /// <item>1 px at this scale is 0.01 world units, and Box2D's polygon skin
    /// (<c>b2_polygonRadius</c> = <c>b2_linearSlop</c> = 0.005) reports a contact only within
    /// <b>2 × that = 0.01</b> — i.e. <b>exactly this gap</b>. Whether a standing unit gets a floor
    /// contact therefore depends on sub-pixel float rounding.</item>
    /// </list>
    ///
    /// <para>So a unit that is resting on solid ground can read as ungrounded, and once
    /// <c>OnCollisionExit2D</c> clears the flag — <b>any</b> collider's exit does, including the
    /// player walking through, since both bodies are Kinematic and only interpenetrate — gravity
    /// starts. A Kinematic body's <c>MovePosition</c> is not blocked by static geometry, so the unit
    /// walks straight down through the floor and never re-contacts: it falls out of the room.</para>
    ///
    /// <para><b>The fix is to ask AS3's question.</b> <c>ITileQueryService.IsOnGround</c> is exactly
    /// <c>isLaz</c>, and it has a 10 px band, so it is not boundary-sensitive — provided the rect it
    /// is given is placed correctly. That placement is this type's whole job.</para>
    /// </summary>
    public static class UnitGroundProbe
    {
        /// <summary>
        /// The seat: how far above the tile surface a unit is placed (<c>ResolveLegacyBottomAnchorPixels</c>'s
        /// trailing <c>+ 1f</c>). Named rather than inlined because <see cref="ToProbeRectPixels"/>
        /// has to undo it, and an unnamed <c>1f</c> in both places is a value that can drift.
        /// </summary>
        public const float SeatPixels = 1f;

        /// <summary>
        /// The rect to hand <c>ITileQueryService.IsOnGround</c>, in <b>world pixels</b>.
        ///
        /// <para><b>The one non-obvious part: <c>yMin</c> is the <i>surface</i>, not the feet.</b>
        /// <c>TileCollisionMath.IsOnGround</c> does not test the rect — it tests a single point,
        /// <c>boundsPx.yMin - 1f</c> (<c>TileCollisionMath.cs:541</c>), and resolves it with
        /// <c>Mathf.FloorToInt</c> (<c>:157</c>). The feet are <see cref="SeatPixels"/> px <i>above</i>
        /// the surface, so a rect placed at the feet would be sampled at the surface <i>boundary</i> —
        /// and <c>floor</c> on a boundary picks the row <b>above</b>, which is the air tile the unit is
        /// standing in. Undoing the seat puts the sample 1 px <i>inside</i> the ground tile, which is
        /// where both the Wall branch and the Platform band (<c>y &lt;= tileTop &amp;&amp; y &gt;=
        /// tileTop - 10</c>, <c>:169-177</c>) expect it.</para>
        ///
        /// <para>Width and height are carried through unchanged; only <c>yMin</c> is shifted, because
        /// <c>IsOnGround</c> reads <c>yMin</c>, <c>width</c> and <c>center.x</c> and nothing else.</para>
        /// </summary>
        /// <param name="worldBounds">A collider's world AABB, in <b>world units</b> — pass
        /// <c>Collider2D.bounds</c> rather than re-deriving the box from grid coordinates, so there is
        /// no convention to get wrong.</param>
        /// <param name="pixelToUnit">The canonical conversion (<c>TileQueryConstants.PixelToUnit</c>,
        /// 0.01). A non-positive value falls back to it rather than producing an inverted rect.</param>
        public static Rect ToProbeRectPixels(Bounds worldBounds, float pixelToUnit)
        {
            if (pixelToUnit <= 0f)
            {
                pixelToUnit = TileQueryConstants.PixelToUnit;
            }

            float unitsToPixels = 1f / pixelToUnit;

            float width = worldBounds.size.x * unitsToPixels;
            float height = worldBounds.size.y * unitsToPixels;
            float centreX = worldBounds.center.x * unitsToPixels;

            // yMin, not the centre: Rect's y IS its minimum. Undo the seat so the sample lands inside
            // the ground tile instead of on its boundary.
            float bottom = worldBounds.min.y * unitsToPixels - SeatPixels;

            return new Rect(centreX - width * 0.5f, bottom, width, height);
        }

        /// <summary>
        /// The point <c>TileCollisionMath.IsOnGround</c> will actually sample, given a rect from
        /// <see cref="ToProbeRectPixels"/>. Exposed so the boundary reasoning above is assertable
        /// rather than a claim in a comment.
        /// </summary>
        public static float ProbePointY(Rect probeRectPixels) => probeRectPixels.yMin - 1f;
    }
}
