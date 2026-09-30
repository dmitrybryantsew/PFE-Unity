using PFE.Systems.Map;
using UnityEngine;

namespace PFE.Entities.Units
{
    /// <summary>
    /// Places a unit's sprite so it lands where AS3 draws it, <b>independently of whatever pivot the
    /// imported sprite asset happens to carry</b>.
    ///
    /// <para><b>The mismatch this exists to fix.</b> A unit's origin is its <i>feet</i>, horizontally
    /// centred — <c>Unit.as:1875-1878</c> is explicit:</para>
    ///
    /// <code>
    /// Y1 = Y - scY;        // the collision box's TOP
    /// Y2 = Y;              // the collision box's BOTTOM == the unit's origin
    /// X1 = X - scX / 2;    // horizontally centred
    /// X2 = X + scX / 2;
    /// </code>
    ///
    /// <para>…and the sprite is <i>not</i> centred on that origin. <c>Unit.initBlit</c>
    /// (<c>Unit.as:2844-2859</c>) offsets the <c>blitX × blitY</c> bitmap to:</para>
    ///
    /// <code>
    /// visBmp.x = blitDX &gt;= 0 ? -blitDX : -blitX / 2;
    /// visBmp.y = blitDY &gt;= 0 ? -blitDY : -blitY + 10;   // 10px ABOVE the bitmap's bottom
    /// </code>
    ///
    /// <para>So by default the sprite hangs <b>10 px below</b> the unit's origin, and the origin sits
    /// <c>blitY - 10</c> px above the sprite's bottom edge. Unity's sprite pivot is the opposite
    /// question — "how far above the sprite's bottom edge does the transform origin sit" — so the
    /// oracle's number <i>is</i> a pivot: <c>pivotY = 10 / blitY</c>. <see cref="PFE.Data.Definitions.UnitSheetLayout.PivotFor"/>
    /// computes exactly that.</para>
    ///
    /// <para><b>But nothing writes it.</b> Measured 2026-09-30: all <b>293</b> unit sprite
    /// <c>.png.meta</c> files under <c>Art/Units</c> carry <c>alignment: 0</c> (Center) and
    /// <c>spritePivot: {x: 0.5, y: 0.5}</c>. <c>UnitSpriteImporter</c> sets
    /// <c>SpriteAlignment.Custom</c> with the computed pivot only for the <i>Multiple</i>-mode sheet
    /// texture (<c>UnitSpriteImporter.cs:355</c>); the <i>Single</i>-mode per-frame PNGs get
    /// <c>SpriteAlignment.Center</c> (<c>:700-704</c>). Since <c>UnitDefinition.sprite</c> — the
    /// resting frame that <c>RoomUnitSpawner</c> draws — points at a per-frame PNG, the sprite is
    /// centred on the feet instead of hanging 10 px below them.</para>
    ///
    /// <para><b>How big the error is.</b> For <c>training</c> the resting frame is
    /// <c>visualTrain/f001.png</c>, 62×88. Centred, its bottom is 44 px below the feet; AS3 puts it
    /// 10 px below. So the whole dummy is drawn <b>34 px too low</b> — the "units clip under the
    /// floor" report. It looks intermittent because it is: a unit whose <c>stay</c> state has real
    /// frames is redrawn by <see cref="UnitAnimator"/> from the <i>sheet</i> sprites, which do carry
    /// the custom pivot, and snaps up to the correct height. <c>training</c> declares
    /// <c>stay.length: 0</c>, so it never does.</para>
    ///
    /// <para><b>Why compensate at draw time instead of fixing the metas.</b> The compensation is
    /// derived from the sprite actually being drawn, so it is <i>self-correcting</i>: a sprite that
    /// already carries the oracle's pivot yields an offset of exactly zero, and one that carries the
    /// default centre yields the difference. That means this survives a re-import, needs no
    /// regeneration of 293 generated assets, and cannot go stale the way a cached pivot does. It also
    /// matches what the port already does for props — <c>RoomObjectVisualManager.ResolveLocalPosition</c>
    /// adds <c>sprite.pivot - desiredPivotPixels</c> to the anchor.</para>
    /// </summary>
    public static class UnitSpriteAnchor
    {
        /// <summary>
        /// AS3's <c>-blitY + 10</c> — the default gap between the sprite's bottom edge and the unit's
        /// origin, in pixels. An <b>absolute</b> constant in the oracle, not a fraction of the cell,
        /// which is why this is a pixel value rather than a 0-1 pivot.
        /// </summary>
        public const float DefaultFeetGapPixels = 10f;

        /// <summary>
        /// The local offset, <b>in room pixels</b>, to add to a unit's origin so the sprite lands where
        /// AS3 would draw it.
        ///
        /// <para>A positive y is <i>up</i>, because room pixel space is bottom-up in the port
        /// (<see cref="WorldCoordinates.PixelToUnity"/> is a straight scale with no flip).</para>
        ///
        /// <para>Pure numbers rather than a <see cref="Sprite"/> so the rule can be tested without
        /// building a texture: the three inputs are the asset's own pivot, the asset's pixel size, and
        /// the unit's declared registration point.</para>
        /// </summary>
        /// <param name="spritePivot">
        /// <see cref="Sprite.pivot"/> — pixels from the sprite's bottom-left corner.
        /// </param>
        /// <param name="spriteSize">The sprite's pixel size (<see cref="Sprite.rect"/> size).</param>
        /// <param name="registrationPoint">
        /// The unit's <c>sprDX</c>/<c>sprDY</c>. A negative component means "not declared", which is the
        /// oracle's own default (<c>Unit.as:514-516</c>) and is what makes its <c>&gt;= 0</c> test a
        /// presence test.
        /// </param>
        public static Vector2 PixelOffset(Vector2 spritePivot, Vector2 spriteSize, Vector2Int registrationPoint)
        {
            // AS3: visBmp.x = blitDX >= 0 ? -blitDX : -blitX / 2.
            // The origin sits `blitDX` px right of the bitmap's left edge, or centred.
            float desiredX = registrationPoint.x >= 0 ? registrationPoint.x : spriteSize.x * 0.5f;

            // AS3: visBmp.y = blitDY >= 0 ? -blitDY : -blitY + 10.
            // Declared: the origin sits `blitY - blitDY` px above the bitmap's bottom edge.
            // Default: it sits a flat 10 px above it.
            float desiredY = registrationPoint.y >= 0
                ? spriteSize.y - registrationPoint.y
                : DefaultFeetGapPixels;

            return new Vector2(spritePivot.x - desiredX, spritePivot.y - desiredY);
        }

        /// <inheritdoc cref="PixelOffset(Vector2, Vector2, Vector2Int)"/>
        public static Vector2 PixelOffset(Sprite sprite, Vector2Int registrationPoint)
        {
            if (sprite == null)
            {
                return Vector2.zero;
            }

            return PixelOffset(sprite.pivot, sprite.rect.size, registrationPoint);
        }

        /// <summary>
        /// Set the sprite <b>and</b> move its renderer so the sprite is anchored the way AS3 anchors it.
        ///
        /// <para><b>The renderer must sit on the unit's visual child, not on the unit itself.</b> That is
        /// deliberate: the unit's own transform has to stay on the feet, because that is what the
        /// collider, <c>UnitController</c>'s <c>MovePosition</c> and every ground/tile query treat as the
        /// unit's position. Only the drawing moves. Moving the unit instead would fix the sprite and
        /// silently move the physics.</para>
        ///
        /// <para>Call this from every place that assigns a unit's sprite — the resting frame in
        /// <c>RoomUnitSpawner</c> and each animation cell in <see cref="UnitAnimator"/> — so the offset
        /// follows the sprite that is actually on screen. Cells from one sheet share a height, so in
        /// practice this is a constant per unit; deriving it per sprite is what keeps that an
        /// observation rather than an assumption.</para>
        /// </summary>
        public static void ApplyTo(SpriteRenderer visualRenderer, Sprite sprite, Vector2Int registrationPoint)
        {
            if (visualRenderer == null)
            {
                return;
            }

            visualRenderer.sprite = sprite;

            Vector2 pixels = PixelOffset(sprite, registrationPoint);
            visualRenderer.transform.localPosition = WorldCoordinates.PixelToUnity(pixels);
        }
    }
}
