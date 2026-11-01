using UnityEngine;

namespace PFE.Systems.Map.Rendering
{
    /// <summary>
    /// Reading the fog back out of the mask — AS3's <c>Tile.visi</c> for an arbitrary point, and the
    /// alpha a world-anchored marker must carry so the fog covers it the way it covers a unit's body.
    ///
    /// <para><b>Why this is its own file, and not part of <see cref="FogOcclusionMath"/>.</b>
    /// <see cref="FogOcclusionMath"/> is the <i>write</i> side of the fog: it needs
    /// <see cref="TilePhysicsType"/> and <c>WorldCoordinates</c> to answer "how much light does this
    /// tile cost a ray". This is the <i>read</i> side — a lookup into the buffer the write side
    /// produced — and it needs nothing but floats. Keeping it separate is what lets the offline harness
    /// link the real file (see <c>.workbuddy-ai/tools/agentverify/fogplate/</c>) instead of copying the
    /// rule into a shim, which is the difference between a test and a re-implementation.</para>
    ///
    /// <para><b>The defect this exists for.</b> A starred enemy's gold star and name are drawn on the
    /// HUD canvas, above every world layer, so they stayed at full brightness in an unlit tile — a
    /// marker floating in the dark, advertising an enemy the player cannot see. AS3 has no such leak
    /// because its marker is <i>not</i> screen furniture: <c>Unit.addVisual()</c> adds the <c>hpBar</c>
    /// to <c>Grafon.visObjs[3]</c> (<c>Unit.as:2896-2899</c>), and <c>Grafon</c> adds that layer
    /// <b>before</b> <c>visLight</c>, the fog bitmap (<c>Grafon.as:193-200</c>). The bar is therefore
    /// composited <i>under</i> the fog and dims with it, exactly like the unit's own sprite.</para>
    ///
    /// <para><b>Why multiplying the marker's alpha by the visibility is an exact reproduction, not an
    /// approximation.</b> The mask texel for visibility <c>v</c> is opaque black at
    /// <c>alpha = 1 - v</c> (<c>RoomBackdropRenderer.ResolveMaskPixel</c>), so the colour the player
    /// sees is <c>marker * v + black * (1 - v) = marker * v</c>. Drawing the marker with
    /// <c>alpha = v</c> over the same background produces the same number.</para>
    /// </summary>
    public static class FogVisibilitySampling
    {
        /// <summary>
        /// The fog visibility at <paramref name="localPixels"/>, or <c>false</c> when the point is not
        /// covered by the mask at all — in which case the caller must treat it as <b>fully visible</b>,
        /// because nothing is drawn over it.
        ///
        /// <para><b>Row order.</b> <paramref name="visibility"/> is the renderer's own
        /// <c>_visibilityMaskCurrentVisibility</c>, indexed <c>y * width + x</c> with <c>y</c> counting
        /// <b>up</b> from the room's bottom row — the port's tile order, matching how the mask texture is
        /// uploaded (<c>Texture2D.SetPixels</c> starts at the bottom-left) and how the light pass writes
        /// it (<see cref="FogOcclusionMath.ResolveLightSamplePixel"/> is <c>(40x, 40(y + 1))</c>, the
        /// port tile's top edge). This is <b>not</b> the AS3 <c>getAbsTile</c> mirror: that one exists to
        /// convert a Y-down oracle lookup into a Y-up row, and there is no oracle index here — only a
        /// buffer this port wrote itself. A plain <c>floor</c> is correct and the <c>- 1</c> would be
        /// wrong.</para>
        /// </summary>
        /// <param name="visibility">The mask's per-tile visibility buffer; values in <c>[0, 1]</c>.</param>
        /// <param name="width">Buffer width in tiles.</param>
        /// <param name="height">Buffer height in tiles.</param>
        /// <param name="localPixels">Point in room-local pixels, origin at the room's bottom-left.</param>
        /// <param name="tileSizePixels">AS3 <c>Tile.tileX</c> — 40 in every shipped room.</param>
        /// <param name="value">The sampled visibility when the function returns <c>true</c>.</param>
        public static bool TrySample(
            float[] visibility,
            int width,
            int height,
            Vector2 localPixels,
            float tileSizePixels,
            out float value)
        {
            value = 1f;

            if (visibility == null || width <= 0 || height <= 0 || tileSizePixels <= 0f)
            {
                return false;
            }

            // A buffer that is shorter than the declared grid is a half-built mask, not a dark one:
            // reading it would index past the end. Refusing is the safe half of that failure.
            if (visibility.Length < width * height)
            {
                return false;
            }

            // The mask covers exactly `width * 40` by `height * 40` pixels. A point on the far edge is
            // outside the last tile, not inside it — the same half-open convention the tile grid uses.
            float right = width * tileSizePixels;
            float top = height * tileSizePixels;
            if (localPixels.x < 0f || localPixels.y < 0f || localPixels.x >= right || localPixels.y >= top)
            {
                return false;
            }

            int column = Mathf.Clamp(Mathf.FloorToInt(localPixels.x / tileSizePixels), 0, width - 1);
            int row = Mathf.Clamp(Mathf.FloorToInt(localPixels.y / tileSizePixels), 0, height - 1);

            value = Mathf.Clamp01(visibility[row * width + column]);
            return true;
        }

        /// <summary>
        /// The alpha a HUD element anchored to a world point must carry so the fog covers it — AS3's
        /// <c>visObjs[3]</c> sitting under <c>visLight</c>.
        ///
        /// <para><b>Two ways to be invisible, and they are not the same input.</b>
        /// <paramref name="fogVisibility"/> is "how much light reaches this spot";
        /// <paramref name="anchorVisible"/> is "is the thing being marked drawn at all". AS3 gates both:
        /// the fog dims the bar, and <c>Unit.addVisual()</c> sets <c>hpBar.visible = false</c> outright
        /// for an <c>invis</c> unit (<c>Unit.as:2893-2896</c>), which <c>visDetails()</c> then re-tests
        /// as <c>&amp;&amp; !this.invis</c> (<c>Unit.as:2983</c>). A star over a stealthed enemy would
        /// hand the player the one piece of information the spell exists to deny.</para>
        /// </summary>
        public static float ResolveWorldAnchoredAlpha(float fogVisibility, bool anchorVisible)
        {
            if (!anchorVisible)
            {
                return 0f;
            }

            return Mathf.Clamp01(fogVisibility);
        }
    }
}
