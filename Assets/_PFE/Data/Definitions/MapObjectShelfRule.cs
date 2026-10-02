using UnityEngine;
using PFE.Systems.Map;

namespace PFE.Data.Definitions
{
    /// <summary>
    /// AS3's <c>Obj.shelf</c> — "can something else rest on top of this prop". The flag that decides
    /// whether a crate can be stood on and whether crates can be stacked.
    ///
    /// <para><b>The oracle, in full</b> (<c>fe/loc/Box.as</c>):</para>
    /// <code>
    /// public var shelf:Boolean = true;                    // :22   — default TRUE
    /// ...
    /// if(scX &lt; 40 || this.wall &gt; 0) { this.shelf = false; }   // :269-272
    /// if(node.@shelf.length())          { this.shelf = true;  }  // :272-274
    /// </code>
    ///
    /// <para>Three things follow, and all three are easy to get backwards:</para>
    ///
    /// <list type="number">
    /// <item><b>The default is <c>true</c>.</b> So this is not a per-prop opt-in list. Almost every
    /// prop wide enough to be a floor is already standable in the original game — including the
    /// ordinary crates. A port that treats "standable" as an authored capability will report every
    /// crate as non-standable and be wrong about all of them.</item>
    /// <item><b>The clear rule is about <i>width</i>, not kind.</b> <c>scX</c> is the sprite width in
    /// pixels (<c>Box.as:158</c> <c>scX = vis.width</c>, overridden by <c>node.@scx</c> at
    /// <c>:162</c>). A prop narrower than 40 px — one tile — is too narrow to stand on, whatever it
    /// is. A 1-tile prop is exactly 40 px and therefore <b>passes</b>; the comparison is
    /// <c>&lt;</c>, not <c>&lt;=</c>.</item>
    /// <item><b>An authored <c>shelf</c> attribute wins over the clear rule.</b> It is applied
    /// <i>after</i> it, so it can restore a narrow prop. Order matters: reading it as "authored
    /// <c>shelf</c> is the only source" gives the same answer for the few props that author it and
    /// the wrong answer for every prop that does not.</item>
    /// </list>
    ///
    /// <para><b>Why this is a pure function of three arguments.</b> The rule reads one authored
    /// attribute, one authored number and one measured width; nothing else. Pinning it here means the
    /// boundary (<c>scX = 40</c>) is assertable without a room, a prop instance or a running engine —
    /// the same reason <c>UnitGroundProbe</c> and <c>TelekinesisMath</c> are shaped this way.</para>
    /// </summary>
    public static class MapObjectShelfRule
    {
        /// <summary>
        /// The width below which a prop is too narrow to be a floor: <c>scX &lt; 40</c>
        /// (<c>Box.as:269</c>), i.e. one tile (<c>WorldConstants.TILE_SIZE</c>).
        ///
        /// <para>Named rather than inlined because it is a <b>strict</b> comparison against a tile
        /// width, and a prop whose footprint is exactly one tile therefore passes. Writing it as
        /// <c>&lt;=</c>, or as "at least two tiles", silently makes every single-tile crate
        /// non-standable.</para>
        /// </summary>
        public const float MinimumShelfWidthPixels = 40f;

        /// <summary>
        /// The rule, in AS3's own order: the authored attribute first, then the clear rule.
        /// </summary>
        /// <param name="hasAuthoredShelfAttribute">Does the prop author a <c>shelf</c> attribute at
        /// all (<c>node.@shelf.length()</c>, <c>Box.as:272</c>)? Presence is the test — AS3 ignores the
        /// value entirely, so <c>shelf="0"</c> also means true.</param>
        /// <param name="isWall">Is this prop part of the wall geometry (<c>this.wall &gt; 0</c>,
        /// <c>Box.as:226</c>)? In practice such a prop is also classified
        /// <see cref="MapObjectPhysicalCapability.Static"/> and never reaches the dynamic path, so this
        /// term is the oracle's own belt-and-braces rather than a live branch.</param>
        /// <param name="widthPixels">The prop's sprite width in pixels — <c>scX</c>. A non-positive
        /// value is treated as "unknown", which fails the width test rather than passing it: an
        /// unmeasured prop must not silently become a floor.</param>
        public static bool IsShelf(bool hasAuthoredShelfAttribute, bool isWall, float widthPixels)
        {
            if (hasAuthoredShelfAttribute)
            {
                return true;
            }

            if (isWall)
            {
                return false;
            }

            return widthPixels >= MinimumShelfWidthPixels;
        }

        /// <summary>
        /// The prop's width in pixels for the rule above: the authored <c>scx</c> when there is one,
        /// else the footprint in tiles. <c>Box.as:158</c> takes the visual's width and <c>:162</c>
        /// overrides it from <c>node.@scx</c>; the port has no sprite width on the definition, so the
        /// tile footprint is the stand-in and the authored attribute is honoured when present — the
        /// same two-step fallback <c>ObjectInstance.GetApproximatePixelSize</c> already uses.
        /// </summary>
        public static float ResolveWidthPixels(string authoredScx, int footprintTiles)
        {
            if (!string.IsNullOrWhiteSpace(authoredScx) &&
                float.TryParse(authoredScx, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float parsed) &&
                parsed > 0f)
            {
                return parsed;
            }

            return Mathf.Max(1, footprintTiles) * WorldConstants.TILE_SIZE;
        }
    }
}
