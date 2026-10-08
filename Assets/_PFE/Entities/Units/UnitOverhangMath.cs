using UnityEngine;

namespace PFE.Entities.Units
{
    /// <summary>
    /// AS3 <c>Unit.shX1</c>/<c>shX2</c> — the unit's <b>overhang</b>: how much of its body width hangs
    /// past the edge of whatever it is standing on — and the two AI sites that read it.
    ///
    /// <para><b>What these two numbers are, because the obvious guess is wrong.</b> They are not
    /// wall-contact flags. They are <i>per-side overhang fractions</i>, and the proof is the base-class
    /// consumer at <c>Unit.as:2086-2097</c>: <i>while moving left</i>, <c>shX1 &gt; 0.5</c> runs
    /// <c>checkDiagon(-5)</c> — "is there a ramp I should step onto?" — which is a question you ask
    /// because your leading edge is off the support, not because a wall is in front of you.</para>
    ///
    /// <para><b>The oracle's four writers, all one shape.</b> Declared with no initialiser
    /// (<c>Unit.as:272</c>, <c>:274</c>), so AS3 leaves them <c>NaN</c> until the first ground pass and
    /// <c>NaN &gt; 0.5</c> is false:</para>
    ///
    /// <list type="bullet">
    /// <item><c>Unit.as:2251</c> — <c>shX1 = shX2 = 1</c> at the start of each descending ground pass.</item>
    /// <item><c>Unit.as:2301</c>/<c>:2305</c> — the tile-collision loop:
    /// <c>shX1 = -(X1 - tile.phX1)/scX</c>, <c>shX2 = (X2 - tile.phX2)/scX</c>, each reduced by
    /// <b>min</b>.</item>
    /// <item><c>Unit.as:2722-2729</c> — <c>checkShelf</c>, the identical formula against a prop's
    /// <c>X1</c>/<c>X2</c> instead of a tile's.</item>
    /// <item><c>Unit.as:2797</c> — <c>checkDiagon</c> <b>clears</b> both to <c>0</c> on engaging a ramp.</item>
    /// </list>
    ///
    /// <para><c>Tile.as:109-110</c> gives the tile's edges: <c>phX1 = X * tileX</c>,
    /// <c>phX2 = (X + 1) * tileX</c> — the grid cell edges. So the sign reads as follows:</para>
    ///
    /// <list type="bullet">
    /// <item><b>negative</b> — that side is fully supported (its edge is well inside the support);</item>
    /// <item><b>0 &lt; v &le; 0.5</b> — that side overhangs by up to half the body width;</item>
    /// <item><b>&gt; 0.5</b> — <b>more than half the body width hangs past the edge</b>;</item>
    /// <item><b>1</b> — no supporting tile or prop was found, i.e. <i>maximal</i> overhang.</item>
    /// </list>
    ///
    /// <para><b>The last one is the trap.</b> <c>1</c> is the <i>initial</i> value each pass and the
    /// reduction is a <c>min</c>, so "nothing supports this side" comes out as <c>1</c> — which is
    /// exactly right ("the edge is behind me"), and is <b>not</b> an "unknown" sentinel to be mapped to
    /// <c>false</c>. A port that treated it as "no information" would make a unit standing on a crate
    /// over air read as <i>never</i> at an edge, which is the opposite of the truth. See
    /// <see cref="NoSupportOverhang"/>.</para>
    ///
    /// <para><b>The reduction is a span.</b> <c>-(X1 - phX1)/scX</c> is minimised when <c>phX1</c> is
    /// <i>smallest</i>, so <c>shX1</c> is decided by the <b>leftmost</b> supporting tile's left edge and
    /// <c>shX2</c> by the <b>rightmost</b> supporting tile's right edge. The pair is therefore fully
    /// described by one span, which is why the port computes
    /// <c>TileCollisionMath.TryGetSupportSpan</c> once rather than reducing per tile. Non-contiguous
    /// support behaves identically in both, because the min and the max are taken independently.</para>
    ///
    /// <para><b>The gate is <c>shX</c> AND a desired direction.</b> Every consumer in the family writes
    /// <c>shX1 &gt; t &amp;&amp; aiNapr &lt; 0</c> (mirrored for <c>shX2</c>): "the edge is ahead <i>in
    /// the direction I am trying to go</i>". <c>aiNapr</c> is <c>Unit.as:348</c>, aliased to
    /// <c>storona</c> (facing) wherever either is written. The idiom appears in <c>UnitAIRobot</c>,
    /// <c>UnitAlicorn</c>, <c>UnitAnt</c>, <c>UnitBossNecr</c>, <c>UnitBossRaider</c>,
    /// <c>UnitHellhound</c> and <c>UnitZombie</c> — a <c>Unit</c>-level contract, not a zombie
    /// quirk.</para>
    /// </summary>
    public static class UnitOverhangMath
    {
        /// <summary>
        /// AS3 <c>Unit.as:2251</c> — <c>shX1 = shX2 = 1</c>. The value when nothing supports that side,
        /// and <b>maximal</b> overhang rather than a sentinel: it exceeds every threshold here, so a unit
        /// standing over a gap reads as fully at the edge.
        /// </summary>
        public const float NoSupportOverhang = 1f;

        /// <summary>
        /// AS3 <c>UnitZombie.as:755</c>/<c>:759</c> — the <c>aiState == 0</c> (idle) gate. Only asks for
        /// a turn; sets no jump.
        /// </summary>
        public const float IdleEdgeThreshold = 0.5f;

        /// <summary>
        /// AS3 <c>UnitZombie.as:789</c>/<c>:808</c> — the <c>aiState == 1</c> (patrol) gate.
        ///
        /// <para><b>Why this is a quarter and the chase's is a half.</b> The patrol notices the edge when
        /// only a quarter of its body is past the lip — early enough to act <i>before</i> it falls. The
        /// chase is already committed by the time half the body is over the edge.</para>
        /// </summary>
        public const float PatrolEdgeThreshold = 0.25f;

        /// <summary>
        /// AS3 <c>UnitZombie.as:890</c> — the <c>aiState == 2 || 3</c> (alert / chase) gate, shared with
        /// the idle gate's value at <c>:755</c>/<c>:759</c>.
        /// </summary>
        public const float ChaseEdgeThreshold = 0.5f;

        /// <summary>
        /// AS3 <c>UnitZombie.as:793</c>/<c>:812</c> — <c>loc.getAbsTile(X + storona * 80, Y + 10)</c>: the
        /// probe sits <b>80 px ahead</b> of the unit's centre, about two tiles.
        /// </summary>
        public const float AheadProbePixels = 80f;

        /// <summary>
        /// AS3 <c>UnitZombie.as:793</c>/<c>:812</c> — the probe's vertical offset, <c>Y + 10</c>.
        ///
        /// <para><b>AS3's Y runs down; this port's room-local Y runs up</b> (tile row 0 is the
        /// <i>lowest</i> row — see <c>ZombieBrain.UpdateDropThroughPlatforms</c>' remarks), so "10 px
        /// below the feet" is <c>feetY - 10</c> here, not <c>+ 10</c>. Getting this backwards puts the
        /// probe in the air <i>above</i> the zombie's head, where there is never a crate, so the patrol
        /// would turn around at every lip and never hop — a silent failure with no exception and no
        /// visible cause.</para>
        ///
        /// <para><b>Why the offset exists at all.</b> <c>getAbsTile</c> floors the point
        /// (<c>Location.as:2339</c>), and the feet sit exactly on the boundary between the air tile and
        /// the ground tile. Dropping 10 px puts the sample unambiguously <i>inside</i> the tile at floor
        /// level — the tile the zombie would be standing on if it walked forward.</para>
        /// </summary>
        public const float AheadProbeDropPixels = 10f;

        /// <summary>
        /// AS3 <c>UnitZombie.as:796</c>/<c>:815</c>/<c>:894</c> — <c>_loc2_ = 0.5</c>: the hop onto a
        /// crate is a <b>half</b> hop. <c>jump(param1)</c> applies <c>dy = -jumpdy * param1</c>
        /// (<c>:388-399</c>), so this reaches half the height of the full hop that the target-overhead
        /// site uses (<c>:841</c>, <c>_loc2_ = 1</c>). A crate is one tile; a full hop would overshoot it
        /// and put the zombie's head into the ceiling, which <c>checkJump</c> would then refuse.
        /// </summary>
        public const float CrateHopMagnitude = 0.5f;

        /// <summary>
        /// AS3 <c>Unit.as:2301</c> — <c>shX1 = -(X1 - tile.phX1) / scX</c>.
        /// </summary>
        /// <param name="feetLeftPixelX">The unit's left edge, <c>X1 = X - scX/2</c>.</param>
        /// <param name="supportLeftPixelX">The supporting tile's (or prop's) left edge, <c>phX1</c>.</param>
        /// <param name="widthPixels">The unit's body width, <c>scX</c>.</param>
        public static float OverhangLeft(float feetLeftPixelX, float supportLeftPixelX, float widthPixels)
        {
            // A zero-width body cannot be expressed as a fraction of itself. AS3 divides by `scX`
            // unconditionally and would hand the AI a NaN, which compares false against every threshold
            // — i.e. a zombie with no collider would simply never notice an edge. Returning the
            // no-support value says the same thing without the NaN, and the NaN would also propagate
            // into any future consumer that does arithmetic on the result.
            if (widthPixels <= 0f)
            {
                return NoSupportOverhang;
            }

            return -(feetLeftPixelX - supportLeftPixelX) / widthPixels;
        }

        /// <summary>
        /// AS3 <c>Unit.as:2305</c> — <c>shX2 = (X2 - tile.phX2) / scX</c>. The mirror of
        /// <see cref="OverhangLeft"/>; the sign flip is the oracle's, not a typo.
        /// </summary>
        /// <param name="feetRightPixelX">The unit's right edge, <c>X2 = X + scX/2</c>.</param>
        /// <param name="supportRightPixelX">The supporting tile's (or prop's) right edge, <c>phX2</c>.</param>
        /// <param name="widthPixels">The unit's body width, <c>scX</c>.</param>
        public static float OverhangRight(float feetRightPixelX, float supportRightPixelX, float widthPixels)
        {
            if (widthPixels <= 0f)
            {
                return NoSupportOverhang;
            }

            return (feetRightPixelX - supportRightPixelX) / widthPixels;
        }

        /// <summary>
        /// The overhang on the side the unit is trying to move toward — AS3's
        /// <c>aiNapr &lt; 0 ? shX1 : shX2</c> selection, which every consumer writes out inline.
        /// </summary>
        /// <param name="direction">The desired direction: negative is left, anything else right.</param>
        public static float OverhangToward(int direction, float overhangLeft, float overhangRight)
        {
            return direction < 0 ? overhangLeft : overhangRight;
        }

        /// <summary>
        /// AS3 <c>UnitZombie.as:789</c>/<c>:808</c> (patrol, <see cref="PatrolEdgeThreshold"/>) and
        /// <c>:890</c> (chase, <see cref="ChaseEdgeThreshold"/>) — <c>stay &amp;&amp; shX &gt; t &amp;&amp;
        /// aiNapr</c> toward that edge.
        ///
        /// <para><b>Strictly greater</b>, matching the oracle's <c>&gt;</c>. A unit overhanging by
        /// exactly a quarter of its body does not trigger the patrol.</para>
        /// </summary>
        public static bool IsAtEdgeToward(int direction, float overhangLeft, float overhangRight, float threshold)
        {
            return OverhangToward(direction, overhangLeft, overhangRight) > threshold;
        }

        /// <summary>
        /// AS3 <c>UnitZombie.as:793</c>/<c>:812</c> — the probe point,
        /// <c>(X + storona * 80, Y + 10)</c>, in <b>room-local pixels</b>.
        ///
        /// <para><b>The vertical offset is subtracted, and that is the whole subtlety.</b> AS3's
        /// <c>Y + 10</c> is 10 px <i>below</i> the feet because AS3's Y grows downward; this port's
        /// room-local Y grows upward. See <see cref="AheadProbeDropPixels"/>.</para>
        ///
        /// <para><b>The point is only half the test.</b> What the probe <i>asks</i> — AS3's
        /// <c>_loc1_.phis == 1 || _loc1_.shelf</c>, "is there something to hop onto?" — is
        /// <see cref="PFE.Data.Definitions.Map.TileData.IsSolidOrShelf"/>, reached through
        /// <c>RoomInstance.IsSolidOrShelfAtRoomLocalPixels</c>. It lives there rather than here so the
        /// <c>phis</c> mapping has one home; this class owns the geometry, that one owns the
        /// classification.</para>
        /// </summary>
        /// <param name="feetPixelX">The unit's centre X, room-local pixels.</param>
        /// <param name="feetPixelY">The unit's feet Y, room-local pixels.</param>
        /// <param name="direction">The facing / desired direction.</param>
        public static Vector2 AheadProbePoint(float feetPixelX, float feetPixelY, int direction)
        {
            return new Vector2(
                feetPixelX + (direction < 0 ? -AheadProbePixels : AheadProbePixels),
                feetPixelY - AheadProbeDropPixels);
        }
    }
}
