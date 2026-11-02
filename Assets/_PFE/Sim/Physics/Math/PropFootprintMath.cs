using UnityEngine;
using PFE.Systems.Map;

namespace PFE.Systems.Physics
{
    /// <summary>
    /// A prop's <b>physical footprint</b> in pixels — AS3's <c>Box.scX</c> / <c>Box.scY</c>, the numbers
    /// every <c>X1/X2/Y1/Y2</c> edge is built from, as one pure function.
    ///
    /// <para><b>The oracle, in full</b> (<c>fe/loc/Box.as:158-171</c>):</para>
    /// <code>
    /// scX = vis.width;                                  // :158  — the SPRITE's width
    /// scY = vis.height;                                 // :159  — the SPRITE's height
    /// if(node.@scx.length()) { scX = node.@scx; }        // :160-163 — authored override
    /// if(node.@scy.length()) { scY = node.@scy; }        // :164-167 — authored override
    /// X1 = X - scX / 2;  X2 = X + scX / 2;               // :168-169 — centred on X
    /// Y1 = Y - scY;      Y2 = Y;                         // :170-171 — standing on Y
    /// </code>
    ///
    /// <para><b>So the default is the art, and the port used the tile footprint instead.</b>
    /// <c>ObjectInstance.GetApproximatePixelSize</c> fell back to <c>size * TILE_SIZE</c> (40 px) and
    /// <c>wid * TILE_SIZE</c> whenever a definition authored no <c>scx</c>/<c>scy</c> — which is 134 of
    /// the 139 shipped definitions. The result is a collider that is a whole number of tiles wide while
    /// the sprite on top of it is not, and the two are anchored to the same point, so the difference
    /// shows up as <b>a gap between the sprite and the physical object</b>:</para>
    ///
    /// <list type="table">
    /// <item><term><c>mcrate1</c>, <c>woodbox</c>, <c>box</c>, <c>safe</c></term>
    ///   <description>art 60x60, footprint 2x2 tiles = <b>80x80</b>. The box is 10 px wider on each
    ///   side and 20 px taller than the crate you can see: two crates pushed together stop with a 20 px
    ///   strip of floor between their sprites, and a unit standing on one floats 20 px above it.</description></item>
    /// <item><term><c>mcrate5</c></term><description>art 100x88, footprint 3x3 = 120x120 — 20 px wider
    ///   and <b>32 px taller</b> than the art.</description></item>
    /// <item><term><c>bigbox2</c></term><description>art 91x110, footprint 3x3 = 120x120.</description></item>
    /// </list>
    ///
    /// <para>Measured over the shipped catalog: <b>105 of 139</b> definitions whose visual is imported
    /// disagree with their footprint, and only 34 agree. The disagreement is not a crate quirk, it is
    /// the default path — the crates are just where it is easiest to see.</para>
    ///
    /// <para><b>Why the art is a fair stand-in for <c>vis.width</c>.</b> The imported PNG frames are
    /// exported at the SWF symbol's own bounds, so frame size and <c>vis.width</c> are the same number
    /// for every static prop; <c>MapObjectVisualDefinition.pixelSize</c> is exactly that frame size
    /// (<c>MapObjectGraphicsImporter.GetPixelSize</c>). For an animated sheet the frame-1 size is the
    /// number AS3 reads too, because <c>Box</c> measures the movieclip once, at construction, on the
    /// frame it stops on.</para>
    ///
    /// <para><b>What is deliberately NOT here.</b> The caller decides whether the art applies at all —
    /// an <c>area</c> trigger is not a <c>Box</c> in AS3 (it is an <c>Area</c>, scaled by its authored
    /// <c>w</c>/<c>h</c>), so it passes <c>Vector2.zero</c> as its art size and keeps its footprint.
    /// Putting that switch in here would make this function read the object's family, which is the
    /// caller's fact, not the rule's.</para>
    /// </summary>
    public static class PropFootprintMath
    {
        /// <summary>
        /// The floor under a resolved size — AS3 never needs one, but a 0-wide box makes every
        /// overlap test in the prop layer answer "no contact", so a prop whose art and footprint are
        /// both missing must still be a solid speck rather than nothing. 8 px is a fifth of a tile.
        /// </summary>
        public const float MinimumPixelSize = 8f;

        /// <summary>
        /// Resolve both axes, in the oracle's precedence: authored <c>scx</c>/<c>scy</c>, then the
        /// sprite, then the tile footprint.
        /// </summary>
        /// <param name="authoredScx">The authored <c>scx</c>, or a non-positive value when absent —
        /// <c>node.@scx.length()</c> is a presence test in AS3, but a present-and-unparseable value is
        /// treated as absent here rather than as a zero-wide box.</param>
        /// <param name="authoredScy">The authored <c>scy</c>, same contract.</param>
        /// <param name="artPixels">The sprite's own size (<c>vis.width</c> / <c>vis.height</c>), or
        /// <c>Vector2.zero</c> when there is no imported art or the caller has decided it does not
        /// apply (an area trigger).</param>
        /// <param name="footprintWidthTiles">The authored <c>size</c> in tiles — the last resort.</param>
        /// <param name="footprintHeightTiles">The authored <c>wid</c> in tiles — the last resort.</param>
        public static Vector2 ResolvePixelSize(
            float authoredScx,
            float authoredScy,
            Vector2 artPixels,
            int footprintWidthTiles,
            int footprintHeightTiles)
        {
            return new Vector2(
                ResolveAxis(authoredScx, artPixels.x, footprintWidthTiles),
                ResolveAxis(authoredScy, artPixels.y, footprintHeightTiles));
        }

        /// <summary>
        /// One axis of <see cref="ResolvePixelSize"/>: authored, then art, then footprint tiles.
        /// </summary>
        public static float ResolveAxis(float authored, float artPixels, int footprintTiles)
        {
            float resolved;

            if (authored > 0f)
            {
                resolved = authored;
            }
            else if (artPixels > 0f)
            {
                resolved = artPixels;
            }
            else
            {
                resolved = Mathf.Max(1, footprintTiles) * WorldConstants.TILE_SIZE;
            }

            return Mathf.Max(MinimumPixelSize, resolved);
        }
    }
}
