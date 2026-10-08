using System;
using UnityEngine;

namespace PFE.Systems.Map.Rendering
{
    /// <summary>
    /// The fog-of-war rules that the render pass owns, kept pure so they can be pinned by tests.
    ///
    /// <para>Two rules live here, and they are different predicates on purpose:
    /// <see cref="ResolveTileOcclusionOpacity"/> decides how much light one tile costs a shadow ray
    /// (AS3 <c>Tile.opac</c>, read at <c>Location.lighting():3214</c>), and
    /// <see cref="IsInsideRevealArea"/> decides which tiles the light pass is allowed to write at all
    /// (the <c>i = 1 … spaceX-1</c> / <c>j = 1 … spaceY-1</c> bound at <c>Location.lighting():3143-3148</c>
    /// and <c>Grafon.setLight():689-699</c>).</para>
    ///
    /// <para>AS3 stores the occlusion value on the tile as <c>Tile.opac</c>. It is read in exactly one
    /// place — <c>Location.lighting():3214</c>, <c>_loc18_ = _loc17_.opac;</c> — and <i>on the decode
    /// path</i> it is <b>binary</b>, never a partial alpha:</para>
    ///
    /// <list type="bullet">
    /// <item><c>Tile.inForm()</c>: <c>if(this.phis &gt; 0) this.opac = 1;</c> (<c>Tile.as:198-201</c>)</item>
    /// <item><c>Tile.mainFrame()</c>: <c>this.opac = 1;</c> (<c>Tile.as:308</c>)</item>
    /// <item><c>Tile.dec()</c> via <c>setZForm(n &gt; 0)</c>: <c>this.opac = 0;</c> (<c>Tile.as:293-296</c>)</item>
    /// </list>
    ///
    /// <para><b>"Binary" is a claim about the decode path, and only that.</b> Two other writers put a
    /// <i>fractional</i> value on the same field: a door object copies <c>@opac</c>, which the data
    /// authors between 0.1 and 0.8 (<c>Box.as:293-295, 667, 685</c>), and <c>Area.as:186-192</c>
    /// copies <c>@tileop</c> onto any tile with <c>phis == 0</c>. The door writer <b>is</b> wired in,
    /// through <c>TileData.doorOcclusion</c>; <c>Area</c> has no counterpart in this port and is the
    /// only one still missing.</para>
    ///
    /// <para><b>The predicate is <c>phis &gt; 0</c>, not <c>phis == 1</c>.</b> A <c>phis == 2</c>
    /// grate door and a <c>phis == 3</c> ghost wall are both opaque to light even though they are
    /// pass-through for other consumers — so the fog rule keys on <c>Wall</c> (which covers 1, 2 and
    /// 3), while <c>Unit.look()</c> keys on <c>phis == 1</c> alone. They are not the same test.</para>
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
        /// <param name="doorOcclusion">
        /// AS3 <c>Tile.opac</c> as written by a door object, or <c>TileData.NoDoorOcclusion</c> when no
        /// door covers the tile. Required, not defaulted: a default here would be a constant wearing a
        /// parameter's clothes, and the whole point is that every caller has to decide.
        /// </param>
        /// <param name="heightLevel">AS3 <c>zForm</c>, 0-3. A raised tile is partial-height with air above it.</param>
        /// <param name="hasWater">AS3 <c>Tile.water</c>.</param>
        /// <param name="waterOpacity">The room's <c>wopac</c> option; 0 when unset.</param>
        public static float ResolveTileOcclusionOpacity(
            TilePhysicsType physicsType,
            float doorOcclusion,
            int heightLevel,
            bool hasWater,
            float waterOpacity)
        {
            // AS3 `if(this.phis > 0) this.opac = 1` (Tile.as:198-201) — ONLY a tile whose `phis` is
            // non-zero is opaque to light. `physicsType == Wall` is this port's encoding of that.
            //
            // Testing `!= Air` here was the bug: a ladder decodes to `Stair` and a catwalk to
            // `Platform`, and both carry `phis == 0` in the oracle (Tile.as:182-197 — `stair`/`shelf`
            // are independent flags, `phis` is never set by them). So the port made all 6 158 ladder
            // and 18 463 catwalk tiles in the 13 shipped rooms opaque to light while the oracle made
            // them transparent.
            float opacity = physicsType == TilePhysicsType.Wall ? 1f : 0f;

            // AS3 `Box.setDoor()` *assigns* `opac` rather than combining with the `phis` value
            // (Box.as:667 for a closed door, :685 for an open one). A door therefore replaces the 1
            // above with its own fractional `@opac` — 0.1 for a grate, 0.8 for a wooden door.
            //
            // Gated on `Wall` rather than applied unconditionally: AS3's `opac` is one field, and the
            // only way a door's value can still be sitting on a tile that is no longer solid is a
            // stale one (a destroyed tile, a carved opening). Reading it then would resurrect an
            // occlusion nothing owns.
            if (physicsType == TilePhysicsType.Wall && doorOcclusion >= 0f)
            {
                opacity = Mathf.Clamp01(doorOcclusion);
            }

            // AS3 `setZForm(n > 0) -> this.opac = 0`. A raised tile is partial-height, so light passes
            // above it. Note this *clears* the value set from `phis` above, it does not add to it.
            //
            // It does not clear a door's value: `setZForm` runs during the decode pass and `initDoor`
            // after it, so the door write is the later one and wins — the same reason `setDoor`
            // re-stamps `opac` on every open/close.
            if (heightLevel > 0 && doorOcclusion < 0f)
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

        /// <summary>
        /// Whether AS3's light pass would ever assign a target to this tile — i.e. whether the tile can
        /// be lit at all.
        ///
        /// <para><c>Location.lighting():3143-3148</c> and <c>Grafon.setLight():689-699</c> both iterate
        /// <c>i = 1; while(i &lt; spaceX)</c> and <c>j = 1; while(j &lt; spaceY)</c> — <b>index 0 is
        /// never visited on either axis</b> — and both write <c>lightBmp</c> at <c>(i, j + 1)</c>.
        /// <c>lightBmp</c> is <c>lightX = 49</c> by <c>lightY = 28</c> for the 48 × 25 grid
        /// (<c>Grafon.as:141-143</c>) and is constructed, and re-<c>fillRect</c>ed, to opaque black
        /// <c>0xFF000000</c> (<c>Grafon.as:224</c>, <c>:423</c>). The cells AS3 skips therefore keep that
        /// black for the life of the room: <b>tile column 0 and tile row 0 are never lit, and never
        /// "known" either</b> — there is no second, memory-only value for them.</para>
        ///
        /// <para>Whichever way the <c>j + 1</c> row offset aligns the bitmap to the room, that
        /// conclusion is the same: AS3 writes columns 1…47 and rows 2…25 of a 49 × 28 bitmap and nothing
        /// else, so it lights a strict subset of what a full-grid pass lights. The port wrote every
        /// texel, which lit exactly the ring the oracle leaves dark — the "I can see one tile behind the
        /// outer wall" report. <c>Grafon.as:403-412</c> additionally parks four opaque <c>visBlack</c>
        /// strips on the room's four edges (<c>ramT/ramB/ramR/ramL</c>, built at <c>:227-235</c>), so in
        /// AS3 that ring is covered twice over; the port has no strip equivalent, and this predicate is
        /// the half of that behaviour the fog pass owns.</para>
        ///
        /// <para><b>Axes.</b> AS3 <c>i</c> is the column and <c>j</c> the row, and <c>j</c> increases
        /// downward (<c>nroom.a[0]</c> is the authored top row). <c>TileDecoder.ParseRoom</c> flips rows
        /// (<c>y = height - 1 - sourceRow</c>), so AS3 <c>i == 0</c> is port <c>x == 0</c> and AS3
        /// <c>j == 0</c> is port <c>y == height - 1</c>. The skipped band is therefore the <i>left
        /// column</i> and the <i>top row</i> — which for <c>room_3_0</c> is exactly its 48-tile ceiling
        /// of <c>A</c> walls, the first authored row.</para>
        /// </summary>
        public static bool IsInsideRevealArea(int x, int y, int width, int height)
        {
            return x > 0 && x < width && y >= 0 && y < height - 1;
        }

        /// <summary>
        /// Whether the <b>full</b> light pass runs this frame, as opposed to the cheap follow-up
        /// (<c>lighting2()</c>) or nothing at all.
        ///
        /// <para><c>Location.step():3398</c> is the oracle's gate:
        /// <c>if(gg.dx + gg.osndx &gt; 0.5 || … &lt; -0.5 || … || this.isRelight || this.isRebuild)</c>
        /// → <c>lighting()</c>, else <c>relight_t &gt; 0</c> → <c>lighting2()</c>. Three inputs, one
        /// OR, and the third and fourth terms are the two force flags: <c>isRelight</c> is set by a
        /// door opening (<c>Box.as:690</c>) and <c>isRebuild</c> by a solid tile changing
        /// (<c>Location.as:2527</c>, <c>:2586</c>).</para>
        ///
        /// <para>The port's three inputs are the explicit dirty flag (room teardown, fog toggle,
        /// <c>RevealAll</c>), a changed player sample (its stand-in for the camera-moved terms), and
        /// a pending <c>RoomInstance</c> relight request — which is both of AS3's force flags
        /// collapsed into one, because the port has one consumer for them.</para>
        ///
        /// <para><b>Any one input forces the pass.</b> That is the point of the predicate: an
        /// <c>&amp;&amp;</c> here would silently drop the door-open and wall-destroyed relights, which
        /// is the bug this exists to prevent — so the rule is named and pinned rather than inlined.</para>
        /// </summary>
        public static bool ShouldRunFullLightPass(bool maskDirty, bool playerSampleChanged, bool relightRequested)
        {
            return maskDirty || playerSampleChanged || relightRequested;
        }

        /// <summary>
        /// How many tiles a shadow ray charges on its way from a light to a target — AS3
        /// <c>_loc15_ = _loc9_ / _loc13_</c> with the loop <c>_loc16_ = 1; while(_loc16_ &lt;= _loc15_)</c>
        /// (<c>Location.lighting():3196</c>, <c>:3212-3213</c>).
        ///
        /// <para><b>It is <c>floor</c>, and that is the whole point.</b> The walk advances 40 px along
        /// the dominant axis per step, so the sample count is
        /// <c>floor(dominantDistance / 40)</c> and the last sample sits on the tile lattice — which
        /// means <b>the target tile is charged only when the distance is an exact multiple of 40</b>.
        /// At exactly one tile the count is <b>1</b>, so the target is charged; just under one tile it
        /// is 0, so a light never self-occludes on the tile it stands in.</para>
        ///
        /// <para><b>Regression this exists for.</b> The port used to skip the ray outright when
        /// <c>dominantDistance &lt;= 40</c> — "close enough, nothing in the way". That is a different
        /// rule, not an optimisation: the tile directly below a light is exactly 40 px away, so a
        /// solid floor was charged nothing and lit. Reported as "I can see the tile below me, the
        /// original leaves it dark". <c>OcclusionRay_AtExactlyOneTile_ChargesTheTargetTile</c> pins it.</para>
        /// </summary>
        public static int OcclusionRaySampleCount(float dominantDistance, float stepPixels)
        {
            if (dominantDistance <= 0f || stepPixels <= 0f)
            {
                return 0;
            }

            return Mathf.FloorToInt(dominantDistance / stepPixels);
        }

        /// <summary>
        /// AS3's occlusion ray: <b>subtractive</b> accumulation of tile opacity along the walk
        /// (<c>Location.lighting():3221-3228</c>), returning what survives as transmission in [0, 1].
        ///
        /// <para>Samples sit at <c>source + k * stepVector</c> for <c>k = 1 … </c>
        /// <see cref="OcclusionRaySampleCount"/>, where <c>stepVector</c> is <c>stepPixels</c> along the
        /// dominant axis (<c>_loc13_ = ±Tile.tileX</c> / <c>_loc14_ = ±Tile.tileY</c>,
        /// <c>:3186-3211</c>) — the lattice, not the segment. The source tile is never charged
        /// (<c>k</c> starts at 1).</para>
        ///
        /// <para><c>opacityAt</c> supplies the room lookup so this stays pure: the renderer passes a
        /// wrapper over <see cref="ResolveTileOcclusionOpacity"/>, tests pass a synthetic field.</para>
        /// </summary>
        public static float ResolveRayTransmission(
            Vector2 sourcePixels,
            Vector2 targetPixels,
            float stepPixels,
            Func<Vector2, float> opacityAt)
        {
            if (opacityAt == null)
            {
                return 1f;
            }

            Vector2 delta = targetPixels - sourcePixels;
            float dominantDistance = Mathf.Max(Mathf.Abs(delta.x), Mathf.Abs(delta.y));
            int steps = OcclusionRaySampleCount(dominantDistance, stepPixels);
            if (steps <= 0)
            {
                return 1f;
            }

            Vector2 stepVector = delta / dominantDistance * stepPixels;
            float transmission = 1f;
            for (int step = 1; step <= steps; step++)
            {
                float opacity = opacityAt(sourcePixels + stepVector * step);
                if (opacity <= 0f)
                {
                    continue;
                }

                transmission -= opacity;
                if (transmission <= 0f)
                {
                    return 0f;
                }
            }

            return Mathf.Clamp01(transmission);
        }

        /// <summary>
        /// How far in front of the unit the player's light sits, in pixels — AS3
        /// <c>gg.storona * 12</c> (<c>Location.lighting():3139</c>). <c>storona</c> is the unit's
        /// facing (<c>Unit.as:602-612</c>), so the light is on the side the unit looks towards.
        /// </summary>
        public const float PlayerLightForwardOffsetPixels = 12f;

        /// <summary>
        /// How far up the unit's sprite the player's light sits, as a fraction of the sprite height.
        ///
        /// <para>AS3 is <c>gg.Y1 + gg.stayY * 0.247</c> (<c>Location.lighting():3140</c>) with
        /// <c>Y1 = Y - scY</c> (<c>Unit.as:1875</c>) and <c>stayY = scY</c> — the sprite height
        /// (<c>Unit.as:1027</c>). Substituting: <c>Y - scY + scY * 0.247 = Y - scY * 0.753</c>, i.e.
        /// <b>75.3 % of the way up the sprite, measured from the feet</b>. For a 70 px player that is
        /// 52.7 px above the feet — chest height, where eyes are.</para>
        ///
        /// <para><b>This is not cosmetic, and it is not separable from the ray.</b> The port used to
        /// put the light at <c>transform.position</c>, which is the unit's <b>feet</b>
        /// (<c>PlayerRigBuilder</c>: the box is offset <c>+height/2</c> above the origin, "because the
        /// origin is the unit's feet"). A grounded unit's feet rest exactly on a tile boundary
        /// (<c>TileCollisionMath.ResolveVerticalDown</c> returns <c>tileTop</c>), so the light sat on
        /// the lattice. Under the port's old proportional ray that was harmless — it sampled the
        /// midpoint, 30 px below the light and safely inside the floor tile. Under AS3's lattice ray
        /// it is a knife edge: the first sample lands at <c>lightY - 40</c>, which is exactly the
        /// floor tile's bottom edge, and <c>floor</c> puts it in the floor tile only while the value
        /// is not a hair low. It is a hair low about a quarter of the time, because
        /// <c>RoomVisualController</c> reads the light off <c>transform.position</c> and that has been
        /// through the <c>×0.01</c>/<c>×100</c> pixel↔unit round trip. So the tile below the player's
        /// floor goes uncharged and lights up — intermittently, and depending on the room's world
        /// position, because the round-trip error grows with magnitude.</para>
        ///
        /// <para>AS3 is immune for the same reason the fix works: at 52.7 px up, the first sample sits
        /// ~13 px inside the floor tile, so no epsilon can push it out. <b>The ray rule and the light
        /// position are a matched pair</b> — port one without the other and you get a boundary bug
        /// that neither has alone.</para>
        /// </summary>
        public const float PlayerLightHeightFraction = 0.753f;

        /// <summary>
        /// The player's light source relative to its feet, in room-local pixels — AS3
        /// <c>Location.lighting():3137-3140</c>.
        ///
        /// <para>Y is positive <b>upwards</b>, matching this port's pixel space (AS3 runs Y downward,
        /// so its <c>Y - scY * 0.753</c> is the same point). <paramref name="facingDirection"/> is
        /// AS3's <c>storona</c>: <c>-1</c> faces left, anything else right — AS3 only ever writes
        /// ±1, so 0 is folded to +1 rather than producing a light with no side.</para>
        /// </summary>
        public static Vector2 ResolvePlayerLightPixelOffset(int facingDirection, float spriteHeightPixels)
        {
            float forward = facingDirection < 0 ? -1f : 1f;
            return new Vector2(
                forward * PlayerLightForwardOffsetPixels,
                Mathf.Max(0f, spriteHeightPixels) * PlayerLightHeightFraction);
        }

        /// <summary>
        /// The room-local pixel the light pass samples for tile <c>(tileX, tileY)</c> — AS3
        /// <c>Location.lighting():3152-3153</c>:
        ///
        /// <code>
        /// _loc9_  = _loc7_ * Tile.tileX - param1;   // tile column * 40 - lightX
        /// _loc10_ = _loc8_ * Tile.tileY - param2;   // tile row    * 40 - lightY
        /// </code>
        ///
        /// <para><b>It is the tile's TOP-LEFT CORNER, not its centre.</b> AS3 measures the light's
        /// distance to <c>(40i, 40j)</c>, and the occlusion ray is aimed at that same point. The port
        /// sampled the centre, <c>((x + 0.5) * 40, (y + 0.5) * 40)</c> — half a tile away on both
        /// axes — which moved the ray's target and therefore changed <i>which tiles are lit</i>, not
        /// merely where the fog is drawn. Measured over five player positions in a 48 x 25 room (a
        /// transliteration of both passes, <c>.workbuddy-ai/tmp/fogsim</c>), the centre sample left
        /// <b>37-46 tiles too dark</b> and revealed <b>12-20 tiles</b> the oracle leaves black. Moving
        /// the target to the corner took both to <b>0</b> in every case.</para>
        ///
        /// <para><b>Port Y is up, AS3 Y is down.</b> AS3's <c>(40i, 40j)</c> is the top-left because
        /// AS3's <c>j</c> grows downward (<c>Tile.as:109-112</c>: <c>phY1 = Y * 40</c> is the top).
        /// Port tile <c>(x, y)</c> is AS3 tile <c>(x, H - 1 - y)</c> (<c>TileDecoder.ParseRoom</c>
        /// flips rows), so the same physical corner is <c>(40x, 40(y + 1))</c> — the port tile's
        /// <b>top</b> edge. Using <c>y * 40</c> instead would drop the light a whole tile.</para>
        /// </summary>
        public static Vector2 ResolveLightSamplePixel(int tileX, int tileY, float tileSizePixels)
        {
            return new Vector2(tileX * tileSizePixels, (tileY + 1) * tileSizePixels);
        }

        /// <summary>
        /// The port tile a room-local pixel belongs to, for the light ray's occlusion lookup — the
        /// AS3-faithful form of <c>Location.getAbsTile():2333-2340</c>.
        ///
        /// <para>AS3 floors in a Y-<b>down</b> space: <c>space[floor(px / 40)][floor(py / 40)]</c>.
        /// The port's Y is up, so the row is the mirror of that floor — <c>ceil(py / 40) - 1</c> — and
        /// a plain <c>floor</c> names the row <b>above</b> on an exact tile boundary.
        /// <c>WorldCoordinates</c>' axis section (<c>:102-104</c>) already records that as "a real
        /// defect and not an epsilon", and <c>ThrownObject.CellAt</c> uses the same form.</para>
        ///
        /// <para>The fog ray reached its lookup through <c>RoomInstance.GetTileAt</c> ->
        /// <c>WorldCoordinates.PixelToTile</c>, which is a plain floor — so any sample landing exactly
        /// on a boundary charged the wrong tile. It does not bite for the player's light, whose
        /// coordinates are fractional, but a lamp's <c>X +/- 10, Y - scY/2</c> can land on one. The
        /// conversion goes through <see cref="WorldCoordinates.As3YToUnityRow"/> rather than repeating
        /// the <c>- 1</c>, per that section's own instruction.</para>
        /// </summary>
        public static Vector2Int ResolveRayLookupCoord(
            float localX, float localY, int roomHeightTiles, float tileSizePixels)
        {
            int column = Mathf.FloorToInt(localX / tileSizePixels);
            int as3Y = Mathf.FloorToInt(roomHeightTiles * tileSizePixels - localY);
            return new Vector2Int(column, WorldCoordinates.As3YToUnityRow(as3Y, roomHeightTiles));
        }
    }
}
