namespace PFE.Entities.Units
{
    /// <summary>
    /// AS3 <c>Unit.throu</c> — the "drop through a one-way surface" flag — as pure functions of the
    /// numbers the oracle actually compares.
    ///
    /// <para><b>What <c>throu</c> is.</b> It is declared <c>public var throu:Boolean = false</c>
    /// (<c>fe/unit/Unit.as:296</c>) and it means <i>"ignore one-way surfaces"</i>. Two things read it:
    /// <c>collisionTile()</c> returns "no collision" for a <c>phis == 0 &amp;&amp; shelf</c> tile
    /// (<c>:2578</c>), and <c>run()</c>'s downward branch skips the entire ground search —
    /// <c>if(_loc5_ == 0 &amp;&amp; !this.throu)</c> gates both <c>checkDiagon()</c> (<c>:2334</c>) and
    /// <c>checkShelf()</c> (<c>:2338</c>). With no ground found the unit keeps falling, which is what
    /// "the zombie dropped through the catwalk onto the player" looks like.</para>
    ///
    /// <para><b>Walls are never affected.</b> A <c>phis == 1</c> tile returns 1 unconditionally, so the
    /// flag is inert on solid ground. That is what makes it safe for a unit to hold it for as long as
    /// the trigger condition is true — which is exactly what the zombie does.</para>
    ///
    /// <para><b>Why extracted.</b> Same reason as <see cref="StaggerMath"/> and
    /// <see cref="ContactInvulnerabilityMath"/>: the state is a bare <c>bool</c> on a
    /// <c>MonoBehaviour</c> and the rule is a comparison against a threshold, so while the arithmetic
    /// lived inside the brain it was unassertable offline and every way to get it wrong is silent. A
    /// <c>&gt;</c> written as <c>&gt;=</c> shifts the trigger by a pixel and never throws; the sign is
    /// worse — Unity's Y runs <b>up</b> and AS3's runs <b>down</b>, so a flipped sign turns "the target
    /// is below me" into "the target is above me" and the zombie drops through the floor instead of the
    /// catwalk, or never drops at all.</para>
    ///
    /// <para><b>The trigger is per-family, and this is the zombie's.</b>
    /// <c>UnitZombie.control()</c> sets it only in <c>aiState == 2 || aiState == 3</c> (alert/chase):
    /// <c>if(celDY &gt; 80) { throu = true; } else { throu = false; }</c> (<c>UnitZombie.as:857-864</c>).
    /// The player has a different one — <c>UnitPlayer.as:2640</c>,
    /// <c>throu = (ctr.keyJump || !stay) &amp;&amp; ctr.keySit || isPlav || kdash_t &gt; 3 || pinok &gt; 70</c>
    /// — so <c>throu</c> is a <i>Unit-level</i> flag with per-family triggers, and nothing here should be
    /// hoisted into a shared "enemy drops through platforms" rule.</para>
    ///
    /// <para><b>Deliberately not modelled: <c>porog = 0</c>.</b> The same branch thins the step-up
    /// allowance — <c>porog = 10; if(celDY &gt; 70) { porog = 0; }</c> (<c>UnitZombie.as:691-695</c>).
    /// It is <b>subsumed</b> by the drop and is not a second trigger: once the platform is ignored
    /// outright the band width cannot matter, and <c>porog = 0</c> on its own does not drop a unit —
    /// <c>collisionTile</c>'s pass-through test is <c>Y2 - porog &gt; phY1</c>, which is <i>false</i> for
    /// a unit resting exactly on the shelf top (<c>Y2 == phY1</c>). Recorded so the next session does not
    /// re-derive it as a missing feature.</para>
    /// </summary>
    public static class UnitDropThroughMath
    {
        /// <summary>
        /// AS3 <c>UnitZombie.as:859</c> — <c>if(celDY &gt; 80)</c>. The distance, in pixels, the
        /// <i>target's centre</i> must be below this unit's <i>top</i> before the zombie commits to
        /// dropping through whatever it is standing on.
        /// </summary>
        public const float DropTriggerPixels = 80f;

        /// <summary>
        /// AS3 <c>UnitZombie.as:934-937</c> — <c>if(Y &gt; loc.spaceY * Tile.tileY - 80)
        /// { throu = false; }</c>. Near the room's floor the flag is forced off, so a zombie chasing a
        /// target it can never reach downwards does not walk itself out of the room.
        /// </summary>
        public const float RoomFloorGuardPixels = 80f;

        /// <summary>
        /// AS3 <c>UnitZombie.control()</c>'s <c>if(celDY &gt; 80)</c> (<c>:859</c>), with
        /// <c>celDY = celY - Y + scY</c> (<c>:678</c>) already reduced to the two quantities it is made
        /// of.
        ///
        /// <para><c>celDY = celY - (Y - scY)</c>, and <c>Y1 = Y - scY</c> is the unit's <b>top</b>
        /// (<c>Unit.as:1875-1876</c>: <c>Y1 = Y - scY; Y2 = Y</c>, the box sits above the origin because
        /// the origin is the feet). So <c>celDY</c> is <b>the target's centre relative to this unit's
        /// top</b> — not relative to its feet and not relative to its centre.</para>
        ///
        /// <para><b>The sign is the port's, not AS3's.</b> AS3's Y increases downward, so the oracle's
        /// <c>celDY &gt; 80</c> is "the target is more than 80 px below". Unity's Y increases upward, so
        /// the same condition is <c>unitTop - targetCentre &gt; 80</c> — the subtraction is in that order
        /// on purpose, and swapping it is the failure this signature exists to make hard.</para>
        /// </summary>
        /// <param name="unitTopPixelY">This unit's top edge, world pixels (feet + height).</param>
        /// <param name="targetCentrePixelY">The target's vertical centre, world pixels.</param>
        public static bool ShouldDropThrough(float unitTopPixelY, float targetCentrePixelY)
        {
            return unitTopPixelY - targetCentrePixelY > DropTriggerPixels;
        }

        /// <summary>
        /// AS3 <c>UnitZombie.as:934-937</c> — whether the unit is close enough to the room's floor that
        /// <c>throu</c> must be forced off.
        ///
        /// <para>AS3 is <c>Y &gt; loc.spaceY * Tile.tileY - 80</c>: in a down-positive space that is
        /// "the distance from the room's bottom is under 80 px". Unity's Y is up, so the same distance is
        /// <c>unitY - roomFloorY</c> — again the subtraction order carries the whole meaning.</para>
        /// </summary>
        /// <param name="unitPixelY">This unit's origin (its feet), world pixels.</param>
        /// <param name="roomFloorPixelY">The room's bottom edge, world pixels
        /// (<c>ITileQueryService.OriginPixel.y</c>).</param>
        public static bool IsNearRoomFloor(float unitPixelY, float roomFloorPixelY)
        {
            return unitPixelY - roomFloorPixelY < RoomFloorGuardPixels;
        }

        /// <summary>
        /// The vertical deadzone that turns an analogue stick into "down". The same <c>-0.5</c> the
        /// locomotion controller already uses for crouching, named once so the two cannot drift.
        /// </summary>
        public const float DownThreshold = -0.5f;

        /// <summary>
        /// AS3 <c>UnitPlayer.as:2640</c> — the player's <c>throu</c> trigger, reduced to its input arm:
        /// <c>(ctr.keyJump || !stay) &amp;&amp; ctr.keySit</c>.
        ///
        /// <para><b>Why this arm and not just the double-tap.</b> The player has two ways into
        /// <c>throu</c>: <c>keyDubSit</c> (a double-tap of Down) sets it outright
        /// (<c>:2917-2925</c>), and this expression re-derives it <i>every frame</i> from the keys.
        /// The second is the one that makes a drop a <i>fall</i>: the frame after the double-tap the
        /// unit is no longer <c>stay</c>, so holding Down keeps <c>throu</c> true for the whole descent.
        /// Without it the drop lasts one pulse and the unit lands on the next surface below — on a
        /// diagonal staircase that is the tread one step down, which reads as "the stairs cannot be
        /// fallen through".</para>
        ///
        /// <para><b>The <c>keyJump</c> term is transcribed, not simplified away.</b> Holding Down and
        /// Jump together is <c>throu</c> in the oracle even while standing, so the surface the unit is
        /// on stops being ground on that frame. Dropping the term would make "jump while holding Down"
        /// land back on the catwalk it left.</para>
        ///
        /// <para><b>Scope.</b> The rest of the oracle expression — <c>isPlav</c>, <c>kdash_t &gt; 3</c>,
        /// <c>pinok &gt; 70</c> — are their own states (swimming, a live dash, the punch/knockback
        /// timer) and are not input, so they are not this helper's question.</para>
        /// </summary>
        /// <param name="jumpHeld">AS3 <c>ctr.keyJump</c> — the key's level, not its edge.</param>
        /// <param name="moveY">The intent's vertical axis, Unity-positive-up.</param>
        /// <param name="grounded">AS3 <c>stay</c> — the port's <c>IsGrounded</c>.</param>
        public static bool PlayerShouldDropThrough(bool jumpHeld, float moveY, bool grounded)
        {
            return (jumpHeld || !grounded) && moveY < DownThreshold;
        }
    }
}
