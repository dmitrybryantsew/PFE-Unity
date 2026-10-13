using UnityEngine;

namespace PFE.Systems.Map.Rendering
{
    /// <summary>
    /// The pure arithmetic of the land's <c>fon</c> layer — AS3 <c>Grafon.drawFon</c>
    /// (<c>Grafon.as:310-325</c>) and <c>Grafon.setFonSize</c> (<c>Grafon.as:327-355</c>).
    ///
    /// <para><b>Why the fit rule lives on its own.</b> It is the part of the <c>fon</c> that can be
    /// wrong in a way no screenshot shows: a backdrop that is merely the wrong size still looks like
    /// a backdrop, and the two failure modes it has (stretched, or cropped from the wrong corner)
    /// both read as "the art is like that". It is also the only part that can be pinned without an
    /// engine, which matters here because the rest of the layer needs a live <c>Camera</c> and a
    /// live <c>SpriteRenderer</c> and therefore cannot run in the offline wall.</para>
    ///
    /// <para><b>What the layer is, in one paragraph.</b> <c>fon</c> is the painted backdrop of a
    /// whole <i>land</i>, drawn behind every room. It is <b>not</b> the room's <c>backwall</c>
    /// (which is a per-room bitmap tiled over the room rect) and it is <b>not</b> the room's
    /// background tile layer. A room with <c>backwall="sky"</c> draws no backwall bitmap at all
    /// (<c>Grafon.as:730-733</c>) precisely so that this layer shows through — which is why a
    /// surface room is a hole in the world until the <c>fon</c> is drawn.</para>
    /// </summary>
    public static class LandFonMath
    {
        /// <summary>
        /// AS3 <c>Grafon.drawFon</c>'s fallback id: <c>if(param2 == "" || param2 == null) param2 =
        /// "fonDefault";</c> (<c>Grafon.as:312-315</c>). It covers <i>both</i> an unknown id and an
        /// empty one, and a land whose <c>fon</c> row is absent is the empty case.
        /// </summary>
        public const string FallbackFonId = "fonDefault";

        /// <summary>
        /// The rect <c>setFonSize</c> measures the stage against — AS3 <c>Grafon.rectX</c> /
        /// <c>Grafon.rectY</c>, declared as the literals <c>1920</c> and <c>1000</c>
        /// (<c>Grafon.as:135,137</c>).
        ///
        /// <para><b>These are ONE ROOM, not the land.</b> <c>Grafon</c> initialises its bitmaps from
        /// them (<c>Grafon.as:207-223</c>, <c>allRect = (0,0,rectX,rectY)</c> at <c>:171</c>) and
        /// every other use in the class is the room-sized scratch surface it composites a room into.
        /// They also equal <c>World.cellsX * Tile.tileX</c> and <c>World.cellsY * Tile.tileY</c>,
        /// i.e. <see cref="PFE.Systems.Map.WorldConstants.ROOM_SIZE_PIXELS"/>; the literals are
        /// transcribed rather than recomputed so a future change to the room size cannot silently
        /// move this threshold without touching the oracle's number.</para>
        /// </summary>
        public static readonly Vector2 RoomRectPixels = new Vector2(1920f, 1000f);

        /// <summary>
        /// AS3 <c>Grafon.drawFon</c>'s id resolution (<c>Grafon.as:310-315</c>).
        ///
        /// <para>Empty and null are the <i>same</i> answer, and that is the oracle's own collapse:
        /// <c>param2 == "" || param2 == null</c>. The port's <c>LandDefinition.backdropId</c> defaults
        /// to <c>""</c> and <c>CampaignDataParser.cs:74</c> writes <c>""</c> for an absent attribute,
        /// so both spellings reach here as the same string.</para>
        /// </summary>
        public static string ResolveFonId(string backdropId)
        {
            return string.IsNullOrEmpty(backdropId) ? FallbackFonId : backdropId;
        }

        /// <summary>
        /// AS3 <c>Grafon.setFonSize</c>'s first branch test (<c>Grafon.as:332</c>):
        /// <c>if(param1 &gt; this.rectX &amp;&amp; param2 &gt; this.rectY)</c> — the stage is larger than
        /// one room on <b>both</b> axes.
        ///
        /// <para>That branch stretches the <c>fon</c> to exactly the room rect and pins it at
        /// <c>visual.x/y</c>, i.e. it draws the backdrop over one room instead of over the screen.
        /// <b>The port cannot reach it</b> and <see cref="LandFonRenderer"/> reports rather than
        /// guesses when it does — see that class's remarks. This predicate exists so the condition is
        /// stated in one place and can be asserted offline.</para>
        /// </summary>
        public static bool IsStretchedToRoomRect(Vector2 stageSizePixels, Vector2 roomRectPixels)
        {
            return stageSizePixels.x > roomRectPixels.x && stageSizePixels.y > roomRectPixels.y;
        }

        /// <summary>
        /// AS3 <c>Grafon.setFonSize</c>'s <c>else</c> branch (<c>Grafon.as:339-353</c>) — the
        /// aspect-preserving <b>cover</b> of the stage, anchored at the stage origin.
        ///
        /// <para>Transcribed exactly, including which axis is tested: <c>_loc3_ = visFon.width /
        /// visFon.height</c> is the sprite's <i>intrinsic</i> ratio (the sprite is a fresh instance
        /// from <c>getObj</c>, so its width/height are still the art's), and the test is
        /// <c>if(param1 &gt;= param2 * _loc3_)</c> — stage width against stage height times that
        /// ratio. When it holds the <i>width</i> is fitted and the height overflows; otherwise the
        /// height is fitted and the width overflows. Either way the result covers the stage, so the
        /// backdrop never shows the clear colour at an edge.</para>
        ///
        /// <para><b>This is not a stretch.</b> A non-uniform scale would distort the art; AS3 keeps
        /// the ratio and lets the overflow be cropped. The crop is not centred — see
        /// <see cref="TopLeftAnchoredCentre"/>.</para>
        /// </summary>
        /// <param name="stageSizePixels">The visible area, in source pixels — AS3's <c>stageWidth</c>/<c>stageHeight</c>.</param>
        /// <param name="spriteSizePixels">The art's intrinsic size, in source pixels.</param>
        public static Vector2 ComputeCoverFit(Vector2 stageSizePixels, Vector2 spriteSizePixels)
        {
            float ratio = spriteSizePixels.x / spriteSizePixels.y;

            if (stageSizePixels.x >= stageSizePixels.y * ratio)
            {
                return new Vector2(stageSizePixels.x, stageSizePixels.x / ratio);
            }

            return new Vector2(stageSizePixels.y * ratio, stageSizePixels.y);
        }

        /// <summary>
        /// Where the fitted sprite's <b>centre</b> goes, in a space whose origin is the centre of the
        /// visible area (which is what a camera-local transform is), given that AS3 anchors the
        /// backdrop's <b>top-left</b> at the stage origin.
        ///
        /// <para>AS3 writes <c>this.visFon.x = this.visFon.y = 0</c> (<c>Grafon.as:342</c>). A Flash
        /// display object exported from the SWF carries its registration point at its own top-left,
        /// and Flash's <c>(0,0)</c> is the stage's top-left, so the assignment puts the art's top-left
        /// corner at the stage's top-left corner and lets the overflow fall off the <i>bottom</i>
        /// (and the right). Anchoring top-left rather than centring is therefore load-bearing: for a
        /// 1920x1080 piece on a 1920x1000 stage it decides whether the missing 80 px comes off the
        /// bottom edge or is split between top and bottom.</para>
        /// </summary>
        /// <param name="stageSizePixels">The visible area, in source pixels.</param>
        /// <param name="fitSizePixels">The result of <see cref="ComputeCoverFit"/>.</param>
        public static Vector2 TopLeftAnchoredCentre(Vector2 stageSizePixels, Vector2 fitSizePixels)
        {
            return new Vector2(
                (fitSizePixels.x - stageSizePixels.x) * 0.5f,
                (stageSizePixels.y - fitSizePixels.y) * 0.5f);
        }

        /// <summary>
        /// The local scale that turns a sprite of <paramref name="spriteSizePixels"/> into
        /// <paramref name="fitSizePixels"/>. Split out only so the division is written once: a
        /// component left unguarded here is a NaN transform, which Unity reports as a silently
        /// invisible renderer rather than as an error.
        /// </summary>
        public static Vector2 ComputeScale(Vector2 fitSizePixels, Vector2 spriteSizePixels)
        {
            return new Vector2(
                fitSizePixels.x / spriteSizePixels.x,
                fitSizePixels.y / spriteSizePixels.y);
        }
    }
}
