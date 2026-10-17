namespace PFE.UI.PipBuck
{
    /// <summary>
    /// The local map's pan / zoom / centre state — the port of <c>PipPageInfo</c>'s four map methods
    /// (<c>setMapSize</c> <c>:816-834</c>, <c>funZoomP</c>/<c>funZoomM</c> <c>:798-808</c>,
    /// <c>funCenter</c> <c>:810-814</c>, <c>scroll</c> <c>:836-850</c>) and of the two drag handlers
    /// (<c>:787-796</c>).
    ///
    /// <para><b>Why this is a separate type from the panel.</b> The arithmetic is the whole of the
    /// feature and it is fiddly in a way that is invisible on screen: <c>setMapSize</c> does not
    /// <i>set</i> the scale, it <i>re-anchors</i> an existing pan by the ratio of the new scale to the
    /// old one, about a point the caller supplies. Three of the four callers supply a different anchor —
    /// the zoom buttons the window's centre, the wheel the mouse, the drag-release its own default — so
    /// getting the anchor wrong looks like "zoom drifts a little", which is exactly the kind of
    /// wrongness a play-test will not report. Being Unity-free means every one of those branches is
    /// assertable offline.</para>
    ///
    /// <para><b>Coordinates are Flash's</b>: <c>+x</c> right, <c>+y</c> <b>down</b>, origin at the
    /// <c>vmap</c> clip's parent. The panel does the single y negation when it places GameObjects —
    /// the same one-flip rule the rest of this device follows.</para>
    /// </summary>
    public sealed class PipMinimapState
    {
        /// <summary><c>mapScale</c> — the current zoom, clamped to 1..6 by <c>setMapSize</c>.</summary>
        public float MapScale { get; private set; } = PipInfoFrame.MapInitialScale;

        /// <summary>
        /// <c>ms</c> — the zoom the last <c>setMapSize</c> left behind, i.e. the ratio's denominator.
        ///
        /// <para><b>It is not a copy of <see cref="MapScale"/> and must not be collapsed into one.</b>
        /// <c>funCenter</c> moves the pan without calling <c>setMapSize</c> (<c>:810-814</c>), so after a
        /// centre the two still agree — but the Oracle updates <c>ms</c> only inside <c>setMapSize</c>,
        /// and a port that derived it would re-anchor by 1 instead of by the previous zoom on the very
        /// first zoom after a resize. Keeping the field is what keeps the two orders identical.</para>
        /// </summary>
        public float LastScale { get; private set; } = PipInfoFrame.MapInitialScale;

        /// <summary><c>vmap.x</c> — the pan layer's translation, Flash space.</summary>
        public float PanX { get; private set; } = PipInfoFrame.MapPanX0;

        /// <summary><c>vmap.y</c>.</summary>
        public float PanY { get; private set; } = PipInfoFrame.MapPanY0;

        /// <summary><c>plTag.x</c> — the player marker, in the <i>scaled</i> map bitmap's own space.</summary>
        public float TagX { get; private set; }

        /// <summary><c>plTag.y</c>.</summary>
        public float TagY { get; private set; }

        /// <summary>Whether the pointer is dragging the map. The Oracle's <c>startDrag</c>/<c>stopDrag</c>.</summary>
        public bool Dragging { get; private set; }

        /// <summary>
        /// The port of <c>setMapSize</c> (<c>PipPageInfo.as:816-834</c>).
        ///
        /// <para><b>What it actually does.</b> It clamps the zoom, then rescales the pan about
        /// <paramref name="anchorX"/>/<paramref name="anchorY"/> so that whatever map point was under
        /// the anchor stays under it. The anchor is in the same space as <c>vmap</c>'s translation,
        /// which is why the zoom buttons can pass the window's centre and the wheel can pass the mouse
        /// without either needing a conversion.</para>
        /// </summary>
        /// <param name="anchorX">Flash x to hold still.</param>
        /// <param name="anchorY">Flash y to hold still.</param>
        /// <param name="tagTileX">
        /// The player's tile column — AS3 <c>World.w.land.ggX / World.tileX</c> (<c>:831</c>). Already
        /// divided, because <c>World.tileX</c> is a constant the caller owns.
        /// </param>
        /// <param name="tagTileY">The player's tile row — <c>ggY / World.tileY</c> (<c>:832</c>).</param>
        public void SetMapSize(float anchorX, float anchorY, float tagTileX, float tagTileY)
        {
            // :818-825 — the clamp. Note it happens BEFORE the ratio, so a wheel that walks the scale
            // past the limit still re-anchors by the ratio of the clamped value to the previous one.
            if (MapScale > PipInfoFrame.MapMaxScale) MapScale = PipInfoFrame.MapMaxScale;
            if (MapScale < PipInfoFrame.MapMinScale) MapScale = PipInfoFrame.MapMinScale;

            // :827-830. `LastScale` is never zero — it starts at the initial scale and is only ever
            // assigned a clamped `MapScale` — so this division is safe by construction rather than by a
            // guard that would hide a bad start value.
            float scaledX = (PanX - anchorX) * MapScale / LastScale;
            float scaledY = (PanY - anchorY) * MapScale / LastScale;

            PanX = scaledX + anchorX;
            PanY = scaledY + anchorY;

            // :831-832 — the marker rides the same scale, and is positioned in the SCALED bitmap's
            // space because that is the space `vmap`'s children live in.
            TagX = tagTileX * MapScale;
            TagY = tagTileY * MapScale;

            // :833 — the ratio for next time.
            LastScale = MapScale;
        }

        /// <summary>
        /// The port of <c>funZoomP</c> / <c>funZoomM</c> (<c>:798-808</c>): step the zoom by
        /// <paramref name="delta"/> and re-anchor on the window's centre.
        /// </summary>
        public void ZoomBy(int delta, float tagTileX, float tagTileY)
        {
            MapScale += delta;
            SetMapSize(PipInfoFrame.MapZoomAnchorX, PipInfoFrame.MapZoomAnchorY, tagTileX, tagTileY);
        }

        /// <summary>
        /// The port of <c>scroll</c> (<c>:836-850</c>): the wheel steps the zoom and re-anchors on the
        /// <b>mouse</b>, not the centre — which is the whole difference between the wheel and the two
        /// buttons, and the reason the anchor is a parameter of <see cref="SetMapSize"/> at all.
        /// </summary>
        public void Scroll(int delta, float mouseX, float mouseY, float tagTileX, float tagTileY)
        {
            if (delta > 0) MapScale += 1f;
            if (delta < 0) MapScale -= 1f;

            SetMapSize(mouseX, mouseY, tagTileX, tagTileY);
        }

        /// <summary>
        /// The port of <c>funCenter</c> (<c>:810-814</c>): put the player marker at the middle of the
        /// window.
        ///
        /// <para><b>It deliberately does not call <c>setMapSize</c></b> — so the scale, the marker and
        /// <c>LastScale</c> are all untouched, and the next zoom re-anchors by the ratio it would have
        /// used anyway. Re-using <c>setMapSize</c> here would look equivalent and would be wrong in
        /// exactly one case: centring at a scale that <c>setMapSize</c> would clamp.</para>
        /// </summary>
        public void Centre()
        {
            PanX = PipInfoFrame.MapWindowCentreX - TagX;
            PanY = PipInfoFrame.MapWindowCentreY - TagY;
        }

        /// <summary>
        /// Moves the pan by a pointer delta — the port of <c>startDrag</c>'s effect
        /// (<c>:787-796</c>). The Oracle lets Flash move the clip and only re-anchors on release; the
        /// port accumulates the same translation itself, so the release-time <c>setMapSize</c> sees the
        /// same <c>vmap.x/y</c> Flash would have.
        /// </summary>
        /// <param name="flashDeltaX">Movement in Flash x (right positive).</param>
        /// <param name="flashDeltaY">Movement in Flash y (<b>down</b> positive).</param>
        public void Drag(float flashDeltaX, float flashDeltaY)
        {
            if (!Dragging) return;
            PanX += flashDeltaX;
            PanY += flashDeltaY;
        }

        /// <summary>The port of <c>onMouseDown</c> (<c>:787-790</c>).</summary>
        public void BeginDrag() => Dragging = true;

        /// <summary>
        /// The port of <c>onMouseUp</c> (<c>:792-796</c>): stop dragging and re-anchor — with
        /// <b>no anchor argument</b>, i.e. the method's own defaults of 350 / 285, not the window's
        /// centre. That is the Oracle's own inconsistency and it is reproduced rather than tidied,
        /// because a release re-anchoring about a different point than a zoom is a visible difference
        /// in where the map sits after a drag-then-zoom.
        /// </summary>
        public void EndDrag(float tagTileX, float tagTileY)
        {
            Dragging = false;
            SetMapSize(PipInfoFrame.MapDefaultAnchorX, PipInfoFrame.MapDefaultAnchorY, tagTileX, tagTileY);
        }

        /// <summary>
        /// The map's own position inside the pan layer — the port of the bitmap's <c>scaleX</c>/
        /// <c>scaleY</c> (<c>:826</c>) and its origin, which Flash leaves at <c>(0, 0)</c> because
        /// <c>visMap.vmap.addChild(map)</c> places it there (<c>:53</c>).
        /// </summary>
        public float BitmapScale => MapScale;

        /// <summary>Resets to the state the page opens in — <c>mapScale = ms = 2</c> (<c>:28-30</c>).</summary>
        public void Reset()
        {
            MapScale = PipInfoFrame.MapInitialScale;
            LastScale = PipInfoFrame.MapInitialScale;
            PanX = PipInfoFrame.MapPanX0;
            PanY = PipInfoFrame.MapPanY0;
            TagX = 0f;
            TagY = 0f;
            Dragging = false;
        }
    }
}
