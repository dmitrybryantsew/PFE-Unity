using UnityEngine;

namespace PFE.Entities.Units
{
    /// <summary>
    /// The unit half of AS3 <c>Unit.checkShelf</c> (<c>Unit.as:2713-2741</c>), as pure functions of
    /// their arguments.
    ///
    /// <para><b>Why extracted rather than inlined.</b> Two callers need this: the ordinary unit
    /// (<c>UnitController.ResolveGroundState</c>, which resolves groundedness <i>before</i> its step)
    /// and the player's motor (<c>TilePhysicsController.MoveSingleStep</c>, which resolves it
    /// <i>during</i> the step and therefore knows the step). They reach the same arithmetic from
    /// different directions and they would otherwise each carry their own copy of it — which is
    /// exactly how the port ended up with four divergent gravity literals. A plain function of
    /// numbers is assertable without a GameObject, a collider or a running engine.</para>
    ///
    /// <para><b>The oracle.</b> <c>fe/unit/Unit.as</c>, called from <c>run()</c> on the downward
    /// branch (<c>:2340</c> and <c>:2422</c>) with the substep already known:</para>
    /// <code>
    /// if(!_loc4_.invis &amp;&amp; _loc4_.shelf &amp;&amp; !_loc4_.levit
    ///    &amp;&amp; !(X2 &lt; _loc4_.X1 || X1 &gt; _loc4_.X2)          // full AABB overlap
    ///    &amp;&amp; Y2 + param2 &lt;= _loc4_.Y1              // my feet were at or above its top
    ///    &amp;&amp; Y2 + param1 + param2 &gt; _loc4_.Y1)      // and this step takes me below it
    /// {
    ///    this.stayMat = _loc4_.mat;
    ///    this.stayPhis = 2;
    ///    this.stayOsn = _loc4_;
    ///    if(!_loc4_.stay)
    ///    {
    ///       _loc4_.dy += dy * massa / (massa + _loc4_.massa);   // :2736
    ///       _loc4_.fixPlav = false;
    ///    }
    ///    return _loc4_.Y1;
    /// }
    /// </code>
    ///
    /// <para><b>AS3 is y-DOWN; the port is y-UP.</b> <c>Y1 = Y - scY</c> is a prop's top (smaller y)
    /// and <c>Y2 = Y</c> its bottom, so "above" and the sign of the step both flip. Copying the two
    /// comparisons across literally inverts the whole test — which is precisely how
    /// <c>RoomObjectPhysicsLayer.TryFindSupportProp</c> was written the first time, and what
    /// <c>FallingProp_FindsAShelfPropBelowIt</c> caught.</para>
    /// </summary>
    public static class UnitCheckShelfMath
    {
        /// <summary>
        /// AS3 <c>Unit.as:2016</c> — how far a support may move in one tick and still be carried.
        /// <c>if(stayOsn.cdx &gt; 10 || stayOsn.cdx &lt; -10 || stayOsn.cdy &gt; 10 || stayOsn.cdy &lt; -10)
        /// { stay = false; }</c>. <b>Strict</b> comparisons, so a support that moved exactly 10 px still
        /// carries — the same boundary shape as <c>Obj.shelf</c>'s exactly-40 px crate.
        /// </summary>
        public const float SupportFollowMaxDeltaPixels = 10f;

        /// <summary>
        /// The landing test: did this step put the feet onto <paramref name="surfaceRoomLocalY"/>.
        ///
        /// <para><b>In y-up terms</b> AS3's <c>Y2 + param2 &lt;= Y1</c> is "the surface is at or below
        /// where my feet started", and <c>Y2 + param1 + param2 &gt; Y1</c> is "and it is at or above
        /// where they ended". So the whole condition is</para>
        /// <code>surface &lt;= feetBefore  &amp;&amp;  surface &gt;= feetAfter</code>
        ///
        /// <para><b>The one deliberate deviation, and why.</b> The oracle's second comparison is
        /// <i>strict</i> (<c>&gt;</c>), and this uses <c>&gt;=</c>. The difference shows up in exactly
        /// one case: <c>feetAfter == feetBefore == surface</c>, i.e. a unit already seated on the prop
        /// with no vertical motion this step. AS3 never reaches that case — <c>dy == 0</c> means neither
        /// of <c>run()</c>'s vertical branches executes, so <c>checkShelf</c> is not called at all and
        /// the unit simply keeps its <c>Y</c>. The port resolves groundedness as a <i>state</i> rather
        /// than only as an event, so it does ask. With the strict form the answer would be "not
        /// standing on it" for a unit that is visibly standing on it, and it would re-enter gravity,
        /// sink a pixel and be caught again — a one-pixel flicker every tick. <c>&gt;=</c> makes the
        /// seated state self-confirming and changes no moving case, because for any real step
        /// <c>feetAfter &lt; feetBefore</c> and the two forms agree.</para>
        ///
        /// <para><b>Order matters.</b> Both comparisons are needed: the first rejects a surface the unit
        /// is still above (a fast fall is a sequence of substeps, and only the substep that actually
        /// crosses lands), the second rejects one it has already passed through.</para>
        /// </summary>
        /// <param name="feetBeforeRoomLocalY">The feet before this step, room-local pixels.</param>
        /// <param name="feetAfterRoomLocalY">The feet after this step, room-local pixels. Equal to
        /// <paramref name="feetBeforeRoomLocalY"/> for a substep with no vertical motion.</param>
        /// <param name="surfaceRoomLocalY">The prop's top edge, room-local pixels.</param>
        public static bool IsLandingOnSurface(
            float feetBeforeRoomLocalY,
            float feetAfterRoomLocalY,
            float surfaceRoomLocalY)
        {
            return surfaceRoomLocalY <= feetBeforeRoomLocalY
                && surfaceRoomLocalY >= feetAfterRoomLocalY;
        }

        /// <summary>
        /// AS3 <c>Unit.as:2016-2019</c> — is this support still carrying the unit, or has it moved far
        /// enough this tick that the unit should stop following it.
        /// </summary>
        public static bool IsSupportStillCarrying(float cdx, float cdy)
        {
            return cdx <= SupportFollowMaxDeltaPixels && cdx >= -SupportFollowMaxDeltaPixels
                && cdy <= SupportFollowMaxDeltaPixels && cdy >= -SupportFollowMaxDeltaPixels;
        }

        /// <summary>
        /// AS3 <c>Unit.as:2736</c> — <c>_loc4_.dy += dy * massa / (massa + _loc4_.massa)</c>: the
        /// share of the unit's downward speed handed to the prop it landed on.
        ///
        /// <para><b>Both masses must be in the same scale</b>, and the port has two. AS3's <c>massa</c>
        /// is the <i>small</i> one — <c>UnitDefinition.Massa</c> and <c>ObjectInstance.GetAs3Massa()</c>
        /// both divide by 50 and are therefore the pair that belongs together. Passing
        /// <c>GetResolvedMass()</c> here would put a 50× heavier number on one side of the ratio and
        /// silently change the split.</para>
        ///
        /// <para><b>Why this does not need a "just landed" latch.</b> The oracle guards it with
        /// <c>if(!_loc4_.stay)</c> (<c>:2734</c>) — only a prop that is <i>not yet at rest</i> is pushed.
        /// So a unit standing on a settled crate pushes nothing, and the transfer is self-limiting
        /// rather than firing once and needing to be suppressed afterwards. A crate still falling into
        /// place under a unit keeps being pushed until it settles, which is the oracle's behaviour and
        /// is what makes a crate land under a unit rather than through it.</para>
        /// </summary>
        /// <param name="unitDownwardPixelsPerSecond">The unit's downward speed, <b>positive when
        /// falling</b> — the port's <c>_velocity.y</c> negated.</param>
        /// <param name="unitMassa">AS3-scale mass of the unit (<c>UnitDefinition.Massa</c>).</param>
        /// <param name="supportMassa">AS3-scale mass of the prop (<c>ObjectInstance.GetAs3Massa()</c>).</param>
        public static float MomentumShareDownwardPixelsPerSecond(
            float unitDownwardPixelsPerSecond,
            float unitMassa,
            float supportMassa)
        {
            if (unitDownwardPixelsPerSecond <= 0f)
            {
                return 0f;
            }

            float total = unitMassa + supportMassa;
            if (total <= 0f)
            {
                // Both masses are zero — a prop and a unit that author neither. AS3 would divide by
                // zero and hand the prop NaN, which then propagates into its position and destroys it.
                // Nothing to share is the safe reading.
                return 0f;
            }

            return unitDownwardPixelsPerSecond * (unitMassa / total);
        }

        /// <summary>
        /// The support's velocity after receiving <paramref name="shareDownwardPixelsPerSecond"/>.
        ///
        /// <para>AS3's <c>dy</c> is <b>positive downward</b> and the port's prop velocity is
        /// <b>negative downward</b>, so the oracle's <c>+=</c> is a <c>-=</c> here. Getting this
        /// backwards makes a unit push the crate it is standing on <i>up</i> through the ceiling.</para>
        /// </summary>
        public static Vector2 SupportVelocityAfterLanding(
            Vector2 supportVelocity,
            float shareDownwardPixelsPerSecond)
        {
            return new Vector2(supportVelocity.x, supportVelocity.y - shareDownwardPixelsPerSecond);
        }

        /// <summary>
        /// A room-local pixel height as a world pixel height. The room origin is
        /// <c>ITileQueryService.OriginPixel</c> / <c>TilePhysicsController</c>'s
        /// <c>roomWorldPixelY</c>; <b>add</b> it to go room-local → world.
        ///
        /// <para>Named rather than inlined because "two coordinate spaces subtracted" is this project's
        /// most-repeated bug — <c>ObjectInstance.position</c> is room-local while the cursor and the
        /// player are world. A prop's top edge comes out of <c>GetApproximateBounds()</c> in room-local
        /// pixels, and the unit's feet come out of <c>Collider2D.bounds</c> in world pixels; mixing them
        /// silently offsets every crate in a room that is not at land position (0,0), which is why it
        /// looks correct in every test built on an origin room.</para>
        /// </summary>
        public static float FeetWorldPixelY(float surfaceRoomLocalY, float roomOriginWorldPixelY)
        {
            return surfaceRoomLocalY + roomOriginWorldPixelY;
        }
    }
}
