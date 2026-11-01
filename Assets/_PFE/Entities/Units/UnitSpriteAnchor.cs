using PFE.Data.Definitions;
using PFE.Data.Definitions.Campaign;
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

        /// <summary>
        /// The same offset expressed the other way round: <paramref name="feetGapPixels"/> is how far
        /// the unit's origin sits <b>above the sprite's bottom edge</b>, already in post-scale pixels.
        ///
        /// <para><b>Why a DisplayObject visual needs this instead of
        /// <see cref="PixelOffset(Vector2, Vector2, Vector2Int)"/>.</b> The 10 px constant in that
        /// overload is AS3's <i>bitmap</i> rule (<c>visBmp.y = -blitY + 10</c>, <c>Unit.as:2844-2859</c>),
        /// and it only applies to a cell blitted out of a sheet. A unit whose art is a DisplayObject is
        /// positioned by <c>vis.x = X; vis.y = Y</c> (<c>Unit.as:2969-2977</c>), so its anchor is wherever
        /// the artist put the symbol's origin — for the ponies and the shopkeepers, <b>~4.7 px</b> above
        /// the bottom edge. Applying the bitmap rule to them draws every one of them 5.3 px into the
        /// floor, which is the "NPCs are sunk into the ground" report. The measured value per visual
        /// lives in <c>VisualSpriteOrigins</c>.</para>
        ///
        /// <para>Horizontal placement stays centred, which is what the oracle does when a visual
        /// declares no <c>sprDX</c>: the 10 px rule has no horizontal half, so <c>-blitX / 2</c> is the
        /// only horizontal anchor a DisplayObject can be given without its own <c>sprDX</c>.</para>
        /// </summary>
        public static Vector2 PixelOffsetForFeetGap(Vector2 spritePivot, Vector2 spriteSize,
                                                     float feetGapPixels)
        {
            return new Vector2(spritePivot.x - spriteSize.x * 0.5f, spritePivot.y - feetGapPixels);
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

        /// <summary>
        /// Set the sprite and anchor it by <b>whichever rule describes this unit's art</b> — the one
        /// entry point every reader of a unit sprite calls, so the resting frame and an animation cell
        /// cannot disagree about where the feet are.
        ///
        /// <para><b>Three rules, and the unit's own data picks between them.</b></para>
        /// <list type="number">
        /// <item><b>A turret mount</b> — a DisplayObject whose origin is neither the frame's centre nor
        /// a 10 px gap. <see cref="TurretSpriteOrigin"/> carries the solved origin.</item>
        /// <item><b>Any other DisplayObject unit</b> (<c>visualClassName</c> set and no sheet — 40 of
        /// the definitions, including <c>bossnecr</c>, <c>bossraider</c>, <c>bloat*</c>, <c>dron*</c>) —
        /// AS3 places it with <c>vis.x = X; vis.y = Y</c>, so the anchor is the symbol's origin as
        /// measured in <see cref="VisualSpriteOrigins"/>.</item>
        /// <item><b>Everything else</b> — a sheet-blitted unit, where the bitmap rule
        /// (<c>-blitY + 10</c>) and the definition's declared <c>sprDX</c>/<c>sprDY</c> apply.</item>
        /// </list>
        ///
        /// <para><b>Why one function rather than a branch at each call site.</b> There are two readers
        /// of a unit's sprite — <c>RoomUnitSpawner</c>'s resting frame and <see cref="UnitAnimator"/>'s
        /// cells — and an override applied at only one of them is a unit that stands in one place and
        /// jumps when it animates. That is exactly how the boss in <c>bossnecr1</c> would have been
        /// re-floated on its first tick if only the spawner had been fixed.</para>
        /// </summary>
        /// <param name="unitId">
        /// The spawned unit's id, for the turret table. Passed rather than read off
        /// <paramref name="definition"/> because a spawn can resolve a placement to a variant
        /// definition, and the mount is a property of what was placed.
        /// </param>
        public static void ApplyResolved(SpriteRenderer visualRenderer, Sprite sprite,
                                         UnitDefinition definition, string unitId)
        {
            if (visualRenderer == null)
            {
                return;
            }

            // A DisplayObject unit's origin is the symbol's, not the bitmap rule's. `TryGetFeetGap`
            // answers false for an unknown visual or a frame whose crop it does not recognise, which
            // falls through to the sheet rule rather than guessing.
            if (sprite != null && definition != null
                && !string.IsNullOrEmpty(definition.visualClassName)
                && !TurretSpriteOrigin.TryGet(unitId, out _)
                && VisualSpriteOrigins.TryGetFeetGap(definition.visualClassName,
                                                     sprite.rect.width, sprite.rect.height,
                                                     out float visualFeetGap))
            {
                ApplyWithFeetGap(visualRenderer, sprite, visualFeetGap, Vector2.one);
                return;
            }

            ApplyTo(visualRenderer, sprite,
                    TurretSpriteOrigin.Resolve(unitId,
                        definition != null ? definition.registrationPoint : new Vector2Int(-1, -1)));
        }

        /// <summary>
        /// Set the sprite, put <paramref name="scale"/> on the visual child, and anchor it by a
        /// measured <paramref name="feetGapPixels"/> (source pixels, above the sprite's bottom edge).
        ///
        /// <para>The gap is multiplied by the scale's Y before the offset is computed, because
        /// <c>localScale</c> scales the sprite about the transform's origin: a 180 px frame drawn at
        /// 0.546 has its bottom edge 8.54 × 0.546 px below the feet, not 8.54.</para>
        /// </summary>
        public static void ApplyWithFeetGap(SpriteRenderer visualRenderer, Sprite sprite,
                                            float feetGapPixels, Vector2 scale)
        {
            if (visualRenderer == null)
            {
                return;
            }

            visualRenderer.sprite = sprite;
            visualRenderer.transform.localScale = new Vector3(scale.x, scale.y, 1f);

            if (sprite == null)
            {
                return;
            }

            float sx = Mathf.Abs(scale.x);
            float sy = Mathf.Abs(scale.y);
            var pivot = new Vector2(sprite.pivot.x * sx, sprite.pivot.y * sy);
            var size = new Vector2(sprite.rect.width * sx, sprite.rect.height * sy);

            visualRenderer.transform.localPosition = WorldCoordinates.PixelToUnity(
                PixelOffsetForFeetGap(pivot, size, feetGapPixels * sy));
        }
    }
}
