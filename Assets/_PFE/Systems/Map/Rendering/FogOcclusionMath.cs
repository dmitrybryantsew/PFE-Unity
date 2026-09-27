using UnityEngine;

namespace PFE.Systems.Map.Rendering
{
    /// <summary>
    /// The fog-of-war occlusion rule, kept pure so it can be pinned by tests.
    ///
    /// AS3 stores the value on the tile as <c>Tile.opac</c>. It is read in exactly one place —
    /// <c>Location.lighting():3214</c>, <c>_loc18_ = _loc17_.opac;</c> — and it is **binary**, never a
    /// partial alpha:
    ///
    /// <list type="bullet">
    /// <item><c>Tile.set()</c>: <c>if(this.phis &gt; 0) this.opac = 1;</c> (<c>Tile.as:198-201</c>)</item>
    /// <item><c>Tile.mainFrame()</c>: <c>this.opac = 1;</c> (<c>Tile.as:308</c>)</item>
    /// <item><c>Tile.dec()</c> via <c>setZForm(n &gt; 0)</c>: <c>this.opac = 0;</c> (<c>Tile.as:293-296</c>)</item>
    /// </list>
    ///
    /// Water is a separate, per-room option — <c>wopac</c> (<c>LandAct.as:225-228</c>) feeding
    /// <c>Location.opacWater</c> — and AS3 applies it as a <c>max</c>, not a sum
    /// (<c>Location.lighting():3217-3220</c>). It defaults to 0, so water only blocks when the room
    /// actually declares it.
    ///
    /// Why this is a named rule and not an inline expression: the port shipped <c>0.6f</c> plus a
    /// "has a front graphic / <c>heightLevel &gt; 0</c> / slope / stair" predicate, and every one of
    /// those was wrong in a way that only showed up in combination with the ray step. See
    /// docs/Perf_FogOfWar_RoomLag_Investigation.md.
    /// </summary>
    public static class FogOcclusionMath
    {
        /// <summary>
        /// How much light a shadow ray loses when it crosses one tile, in <c>[0,1]</c>.
        ///
        /// Note this is deliberately **not** <c>TileData.opacity</c>: that field is the tile's render
        /// alpha (<c>TileRenderer</c>) and defaults to 1 even for air, so reusing it would make every
        /// empty tile opaque to light.
        /// </summary>
        /// <param name="physicsType">The port's mapping of AS3 <c>phis</c>.</param>
        /// <param name="heightLevel">AS3 <c>zForm</c>, 0-3. A raised tile is partial-height with air above it.</param>
        /// <param name="hasWater">AS3 <c>Tile.water</c>.</param>
        /// <param name="waterOpacity">The room's <c>wopac</c> option; 0 when unset.</param>
        public static float ResolveTileOcclusionOpacity(
            TilePhysicsType physicsType,
            int heightLevel,
            bool hasWater,
            float waterOpacity)
        {
            // AS3 `if(this.phis > 0) this.opac = 1` — any non-air physics type is opaque to light.
            float opacity = physicsType != TilePhysicsType.Air ? 1f : 0f;

            // AS3 `setZForm(n > 0) -> this.opac = 0`. A raised tile is partial-height, so light passes
            // above it. Note this *clears* the value set from `phis` above, it does not add to it.
            if (heightLevel > 0)
            {
                opacity = 0f;
            }

            // AS3 `max(opac, opacWater)`, and only when the room declares `wopac`.
            if (hasWater && waterOpacity > opacity)
            {
                opacity = Mathf.Clamp01(waterOpacity);
            }

            return opacity;
        }
    }
}
