using UnityEngine;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// Where <c>UnitAlicorn.actPort</c> puts the caster — the point-selection half of
    /// <c>fe/unit/UnitAlicorn.as:1248-1317</c>.
    ///
    /// <para><b>Two ladders, selected by <c>param1</c>.</b> The oracle's first two branches are both
    /// gated on <c>!param1</c>, so the flag does not merely tweak the distance — it changes which
    /// <i>algorithm</i> runs:</para>
    /// <list type="bullet">
    ///   <item><description><b><c>actPort()</c></b> (the <c>superSila</c> caller,
    ///     <c>:1113/:1117</c>) uses a three-rung ladder keyed on the attempt index: attempts 1-4 jump
    ///     200-500 px sideways at the player's altitude, 5-9 jump 200-1000 px sideways with ±80 px of
    ///     vertical jitter, and 10-20 fall back to a uniformly random point in the room.</description></item>
    ///   <item><description><b><c>actPort(true)</c></b> (the <c>tr2</c> reactive escape,
    ///     <c>:424</c>) skips both near rungs entirely and goes <i>straight</i> to the uniformly random
    ///     point. A <c>tr2</c> alicorn that is hit while calm therefore does not step back a few metres
    ///     — it vanishes somewhere else in the level, which is why the damage is refused outright.</description></item>
    /// </list>
    ///
    /// <para><b>Y is mirrored, and the snap is the tell.</b> AS3's <c>_loc4_</c> is the unit's
    /// <i>bottom</i> in a Y-down space, so the oracle snaps it with <c>Math.ceil(y / tileY) * tileY -
    /// 1</c> — "one pixel above the tile line, measured downward". The port's feet coordinate grows
    /// upward, so the mirror is <c>Math.floor(y / tileY) * tileY + 1</c>: one pixel above the tile line,
    /// measured upward. Both mean "standing on the seam", and copying the oracle's <c>ceil … - 1</c>
    /// verbatim would put the unit one pixel <i>into</i> the floor.</para>
    ///
    /// <para><b>Unity-free</b> (only <c>Vector2</c>) so the ladder and the four clamps can be pinned by
    /// an offline fixture. The collision retry is <i>not</i> here: it needs the room's tile grid, so the
    /// caller drives the loop and simply asks for a fresh point.</para>
    /// </summary>
    public static class AlicornTeleportMath
    {
        /// <summary><c>while(_loc5_ &lt;= 20)</c> (<c>:1255</c>) — collision retries before giving up.</summary>
        public const int MaxAttempts = 20;

        /// <summary><c>_loc5_ &lt; 5</c> (<c>:1257</c>) — the near ladder owns attempts 1-4.</summary>
        public const int NearPhaseAttempts = 5;

        /// <summary><c>_loc5_ &lt; 10</c> (<c>:1270</c>) — the far ladder owns attempts 5-9.</summary>
        public const int FarPhaseAttempts = 10;

        /// <summary><c>Math.random() * 300 + 200</c> (<c>:1261</c>/<c>:1265</c>) — the near jump's span.</summary>
        public const float NearSpanPixels = 300f;

        /// <summary>The near jump's floor — the <c>+ 200</c> of the same expression.</summary>
        public const float NearMinPixels = 200f;

        /// <summary><c>Math.random() * 800 + 200</c> (<c>:1274</c>/<c>:1278</c>) — the far jump's span.</summary>
        public const float FarSpanPixels = 800f;

        /// <summary>The far jump's floor — the <c>+ 200</c> of the same expression.</summary>
        public const float FarMinPixels = 200f;

        /// <summary><c>Math.random() * 160 - 80</c> (<c>:1280</c>) — the far jump's vertical jitter.</summary>
        public const float FarVerticalJitterPixels = 80f;

        /// <summary><c>isrnd(0.7)</c> (<c>:1259</c>) — the near jump goes <i>behind</i> the player this often.</summary>
        public const float NearBranchChance = 0.7f;

        /// <summary><c>isrnd()</c> (<c>:1272</c>) — the far jump's coin, at <c>isrnd</c>'s default 0.5.</summary>
        public const float FarBranchChance = 0.5f;

        /// <summary><c>_loc4_ &lt; scY + 40</c> / <c>_loc4_ &gt; limY - 40</c> (<c>:1303-1310</c>) — the floor margin.</summary>
        public const float EdgeMarginPixels = 40f;

        /// <summary>
        /// One candidate point, snapped and clamped, in <b>room-local pixels with Y up</b>.
        /// </summary>
        /// <param name="reactive">AS3 <c>param1</c> — <c>true</c> skips both near rungs.</param>
        /// <param name="attempt">AS3 <c>_loc5_</c>, <b>1-based</b>. The ladder is keyed on it, so an
        /// off-by-one here silently changes which branch runs.</param>
        /// <param name="playerLocal">AS3 <c>World.w.gg</c>'s position — the point both near rungs are
        /// measured from. In room-local pixels.</param>
        /// <param name="playerFacing">AS3 <c>gg.storona</c> — the direction the near jumps are cast
        /// <i>behind</i>.</param>
        /// <param name="spriteSize">AS3 <c>scX</c>/<c>scY</c> — the caster's own body size, which is
        /// also the clamp inset.</param>
        /// <param name="roomLimitX">AS3 <c>loc.limX</c> — the room's width in pixels.</param>
        /// <param name="roomLimitY">AS3 <c>loc.limY</c> — the room's height in pixels.</param>
        /// <param name="tileSize">AS3 <c>World.tileX</c>/<c>tileY</c> — the snap grid. The port's rooms
        /// are square-tiled, so one value serves both axes.</param>
        /// <param name="rollBranch">The <c>isrnd</c> draw that picks the side.</param>
        /// <param name="rollMagnitude">The <c>Math.random()</c> that picks the distance — or, on the
        /// random rung, the X fraction of the room.</param>
        /// <param name="rollJitter">The vertical draw — the far rung's ±80 jitter, or the random rung's
        /// Y fraction of the room.</param>
        public static Vector2 PickPoint(
            bool reactive,
            int attempt,
            Vector2 playerLocal,
            int playerFacing,
            Vector2 spriteSize,
            float roomLimitX,
            float roomLimitY,
            float tileSize,
            float rollBranch,
            float rollMagnitude,
            float rollJitter)
        {
            float facing = playerFacing < 0 ? -1f : 1f;
            float x;
            float y;

            if (!reactive && attempt < NearPhaseAttempts)
            {
                float distance = NearMinPixels + rollMagnitude * NearSpanPixels;
                x = rollBranch < NearBranchChance
                    ? playerLocal.x - facing * distance
                    : playerLocal.x + facing * distance;
                y = playerLocal.y;
            }
            else if (!reactive && attempt < FarPhaseAttempts)
            {
                float distance = FarMinPixels + rollMagnitude * FarSpanPixels;
                x = rollBranch < FarBranchChance
                    ? playerLocal.x - facing * distance
                    : playerLocal.x + facing * distance;
                y = playerLocal.y + (rollJitter * 2f - 1f) * FarVerticalJitterPixels;
            }
            else
            {
                // `_loc3_ = Math.random() * loc.limX; _loc4_ = Math.random() * loc.limY;` (:1286-1287).
                x = rollMagnitude * roomLimitX;
                y = rollJitter * roomLimitY;
            }

            return SnapAndClamp(x, y, spriteSize, roomLimitX, roomLimitY, tileSize);
        }

        /// <summary>
        /// <c>:1290-1310</c> — the tile snap and the four clamps, in the oracle's order.
        /// </summary>
        /// <remarks>
        /// <b>The order matters.</b> AS3 snaps <i>then</i> clamps, so a snapped coordinate can be pushed
        /// back off the grid by a clamp; clamping first would leave a point outside the room after the
        /// snap. And the vertical clamps are asymmetric on purpose — the floor side only reserves
        /// <see cref="EdgeMarginPixels"/>, while the ceiling side reserves the caster's full body height.
        /// </remarks>
        public static Vector2 SnapAndClamp(
            float x,
            float y,
            Vector2 spriteSize,
            float roomLimitX,
            float roomLimitY,
            float tileSize)
        {
            if (tileSize > 0f)
            {
                // `Math.round(_loc3_ / tileX) * tileX` and the mirrored vertical snap — see the class
                // remarks for why this is `floor … + 1` and not the oracle's `ceil … - 1`.
                x = RoundHalfUp(x / tileSize) * tileSize;
                y = (float)System.Math.Floor(y / tileSize) * tileSize + 1f;
            }

            if (x < spriteSize.x) x = spriteSize.x;
            if (y < EdgeMarginPixels) y = EdgeMarginPixels;
            if (x > roomLimitX - spriteSize.x) x = roomLimitX - spriteSize.x;
            if (y > roomLimitY - spriteSize.y) y = roomLimitY - spriteSize.y;

            return new Vector2(x, y);
        }

        /// <summary>
        /// AS3's <c>Math.round</c> is round-half-<b>up</b>; <c>System.Math.Round</c> is banker's
        /// rounding, which sends 2.5 to 2 and 3.5 to 4. A two-tile difference on the snap is invisible
        /// until it puts a unit through a wall, so the distinction is made explicit here.
        /// </summary>
        private static float RoundHalfUp(float value) => (float)System.Math.Floor(value + 0.5f);
    }
}
