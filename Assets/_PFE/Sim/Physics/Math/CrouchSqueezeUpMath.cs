using System;

namespace PFE.Systems.Physics
{
    /// <summary>
    /// AS3's <b>Up-key mantle</b> — hold Up while walking into a one-tile-high ledge and the unit is
    /// lifted onto it and crouched. This is the other half of the crouch, and the half the port was
    /// missing: the collision box could already shrink (<c>UpdateColliderProfile</c>) and already
    /// re-shrank under a low ceiling (the oracle's <c>unsit()</c> → <c>collisionAll()</c> →
    /// <c>sit(true)</c> round-trip), but nothing ever <i>put the unit up there</i>.
    ///
    /// <para><b>The oracle arm.</b> <c>Unit.as:2108-2118</c> (leftward) and <c>:2185-2195</c>
    /// (rightward), inside the horizontal-collision branch:</para>
    /// <code>
    /// if (this.player &amp;&amp; this.isUp &amp;&amp; stay &amp;&amp; !this.isSit) {
    ///    _loc2_ = loc.space[floor(X1 / tileX)][floor(Y1 / tileY)];       // head row, leading column
    ///    _loc3_ = loc.space[floor(X1 / tileX)][floor(Y1 / tileY) + 1];   // one row BELOW the head
    ///    if ((_loc2_.phis == 0 || _loc2_.phis == 3)                       // head cell is open
    ///        &amp;&amp; !(_loc3_.phis == 0 || _loc3_.phis == 3) &amp;&amp; _loc3_.zForm == 0) {
    ///       Y = Y2 = _loc3_.phY1;                                         // feet onto that cell's top
    ///       this.sit(true);                                               // and crouch
    ///    }
    /// }
    /// </code>
    ///
    /// <para><b>Why "one row below the head" is the row the torso is in.</b> <c>Y1</c> is
    /// <c>Y - scY</c>, the head, and AS3's grid runs <b>downward</b>, so <c>headRow + 1</c> is the
    /// row <i>lower on screen</i> — the cell the chest occupies. The arm therefore fires only when the
    /// cell at the unit's own torso height is solid while the cell at its head height is open, i.e.
    /// when the obstacle's top surface sits between the feet and the head. That is precisely the
    /// geometry a one-tile ledge has, and precisely the geometry <c>porog</c> cannot climb: the
    /// player's walking <c>porog</c> is 20 px (<c>UnitPlayer.as:2550</c>) against a 40 px tile.</para>
    ///
    /// <para><b>The crouch is not this class's job, and must not become it.</b> The oracle calls
    /// <c>sit(true)</c> and then, later in the same frame, <c>:2884</c>
    /// (<c>isSit &amp;&amp; keyBeUp &amp;&amp; !keySit</c>) calls <c>unsit()</c>, which grows the box back
    /// only if <c>collisionAll()</c> says it fits (<c>Unit.as:2532-2538</c>). So the crouch that
    /// <i>sticks</i> is decided by the headroom, not by the mantle. The port's
    /// <c>TilePhysicsController.UpdateColliderProfile</c> is already that decision — after the lift,
    /// <c>HasSolidOverlapAt(standing height)</c> is true and it holds the box at <c>sitHeight</c> on
    /// its own. Adding a second crouch decision here is how the two would disagree.</para>
    ///
    /// <para><b>Y is mirrored, and that is the whole risk.</b> The oracle's grid index grows downward;
    /// the port's <c>WorldPixelToTileY</c> grows upward, with the feet at <c>y</c> and the head at
    /// <c>y + height</c>. So the head row is <c>floor((feetY + height - origin) / tileSize)</c> and the
    /// torso row is <b>headRow − 1</b>, not <c>+ 1</c>. Writing the oracle's <c>+ 1</c> here would
    /// probe the cell <i>above the head</i> — always open, never a ledge — and the mantle would simply
    /// never fire, which is the failure mode that looks like "the feature is unimplemented" rather
    /// than "the sign is wrong".</para>
    ///
    /// <para><b>What this class deliberately does not model.</b> <c>!isFly</c>, <c>!isPlav</c>,
    /// <c>!levit</c>, <c>this.player</c> and <c>shX1 &gt; 0.5</c> are gates the caller owns: the
    /// player check is the fact that only <c>PlayerLocomotionController</c> publishes an Up level, and
    /// flight / water / levitation already refuse the crouch through
    /// <see cref="CrouchStateMath.MayBeginCrouch"/>. <c>_loc6_</c> (the oracle's "the mantle ran"
    /// flag) has no reader this port needs — see <c>TilePhysicsController.TrySqueezeUp</c>.</para>
    /// </summary>
    public static class CrouchSqueezeUpMath
    {
        /// <summary>
        /// AS3 <c>ctr.keyBeUp</c> as an axis level — the same magnitude as
        /// <see cref="CrouchStateMath.UpThreshold"/>, because it is the same key: the one that ends a
        /// crouch (<c>UnitPlayer.as:2884</c>) and the one that mantles a ledge (<c>Unit.as:2108</c>).
        /// Reusing the constant is the point — a player who is crouched is by definition not holding
        /// Up, so the two arms can never fire together.
        /// </summary>
        public const float UpThreshold = CrouchStateMath.UpThreshold;

        /// <summary>
        /// The grid row containing the unit's <b>head</b> — AS3 <c>floor(Y1 / Tile.tileY)</c> with
        /// <c>Y1 = Y - scY</c>, mirrored into the port's Y-up grid.
        ///
        /// <para><b>No epsilon.</b> AS3's <c>floor</c> has none, and a head exactly on a cell boundary
        /// must land in the same cell on both sides of the mirror: Y-down <c>floor(40/40) = 1</c>
        /// selects the row spanning <c>[40, 80)</c>, and Y-up <c>floor(40/40) = 1</c> selects the row
        /// spanning <c>[40, 80)</c> as well. Adding a nudging <c>- 0.01f</c> (as
        /// <c>TileCollisionMath.HasSolidOverlapAt</c> does for its AABB, where it is closing an
        /// <i>overlap</i> test) would move the head down a row at exactly the boundary the oracle
        /// keeps it on.</para>
        /// </summary>
        /// <param name="feetY">Feet in room-local pixels — the motor's <c>posY</c>.</param>
        /// <param name="bodyHeight">The <b>standing</b> height in pixels — AS3 <c>scY</c>.</param>
        /// <param name="roomOriginY">The room's world origin — <c>roomWorldPixelY</c>.</param>
        /// <param name="tileSize">40 (<c>WorldConstants.TILE_SIZE</c>).</param>
        public static int HeadRow(float feetY, float bodyHeight, float roomOriginY, float tileSize)
        {
            if (tileSize <= 0f)
            {
                return int.MinValue;
            }

            return (int)Math.Floor((feetY + bodyHeight - roomOriginY) / tileSize);
        }

        /// <summary>
        /// The row the unit's <b>torso</b> is in — AS3 <c>floor(Y1 / tileY) + 1</c>, which in the
        /// port's upward-growing grid is <b>one row down</b>. See the class note on the sign.
        /// </summary>
        public static int TorsoRow(int headRow)
        {
            return headRow - 1;
        }

        /// <summary>
        /// May the mantle fire? The oracle's four gates plus its three cell tests, with nothing else
        /// folded in.
        ///
        /// <para><b>Why <paramref name="crouching"/> is a refusal rather than a "stand up first".</b>
        /// AS3 requires <c>!isSit</c> (<c>Unit.as:2108</c>). Holding Up already clears the crouch
        /// (<c>:2884</c>), so a crouched unit is never Up-held for longer than the frame that clears
        /// it — the two conditions are near-mutually-exclusive in practice, and making the mantle
        /// stand the unit up first would let a crawl under a ceiling teleport them through it.</para>
        /// </summary>
        /// <param name="upHeld">AS3 <c>isUp</c>, which is <c>ctr.keyBeUp</c> (<c>UnitPlayer.as:2944</c>).</param>
        /// <param name="crouching">AS3 <c>isSit</c>.</param>
        /// <param name="grounded">AS3 <c>stay</c>.</param>
        /// <param name="headRowOpen">
        /// <c>_loc2_.phis == 0 || _loc2_.phis == 3</c> — the head cell is not a wall.
        /// </param>
        /// <param name="torsoRowSolid">
        /// <c>_loc3_.phis != 0 &amp;&amp; _loc3_.phis != 3</c> — the torso cell <i>is</i> a wall, so its
        /// top surface is a floor to stand on.
        /// </param>
        /// <param name="torsoRowUnraised">
        /// <c>_loc3_.zForm == 0</c> — the port's <c>heightLevel == 0</c>. A quarter-height form's
        /// <c>phY1</c> is 10 px down a cell, and mantling onto it would put the feet inside the cell
        /// above rather than on top of it.
        /// </param>
        public static bool ShouldSqueezeUp(
            bool upHeld,
            bool crouching,
            bool grounded,
            bool headRowOpen,
            bool torsoRowSolid,
            bool torsoRowUnraised)
        {
            if (!upHeld || crouching || !grounded)
            {
                return false;
            }

            return headRowOpen && torsoRowSolid && torsoRowUnraised;
        }

        /// <summary>
        /// The oracle assigns <c>Y = _loc3_.phY1</c> with no test that this is <i>up</i> — it is
        /// implied by the geometry, because the torso cell's top is above the feet by construction.
        /// The port probes the column <b>ahead</b> rather than the unit's own edge (see
        /// <c>TilePhysicsController.TrySqueezeUp</c>), which makes that implication worth asserting
        /// instead of assuming: a downward "lift" is the shape a sign error takes, and it would drop
        /// the unit through the floor it was standing on.
        /// </summary>
        /// <param name="feetY">Where the feet are now.</param>
        /// <param name="targetFeetY">The torso cell's top surface, in world pixels.</param>
        public static bool LiftsUpward(float feetY, float targetFeetY)
        {
            return targetFeetY > feetY + 0.01f;
        }
    }
}
