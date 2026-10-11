namespace PFE.Systems.Campaign
{
    /// <summary>
    /// "Can the player leave here, and show me where to." The port of AS3's <c>PipBuck.travel</c> flag
    /// plus the <c>onoff(3,3)</c> that opens the world-map page.
    ///
    /// <para><b>Oracle, end to end.</b> Two things grant travel, and both do the same three steps:</para>
    /// <list type="bullet">
    /// <item>a placed object with <c>allact='map'</c> — the camp's wall map, <c>wmap</c>
    /// (<c>AllData.as:5020</c>), which is the object <c>rooms_rbl/room_0_0</c> actually places
    /// (<c>Interact.as:1636-1641</c>);</item>
    /// <item>an NPC whose <c>inter</c> is <c>travel</c> (<c>NPC.as:210-216</c>), which the importer
    /// already carries as <c>NpcDefinition.interactionType</c>.</item>
    /// </list>
    ///
    /// <para>Separately, <c>PipBuck.setButtons</c> recomputes the flag from <i>where you are standing</i>:
    /// <c>if(!light &amp;&amp; loc.base) travel = true</c> (<c>PipBuck.as:271-273</c>) and
    /// <c>if(!light &amp;&amp; !loc.base) travel = false</c> (<c>:342-345</c>). So standing in a
    /// <c>tip='base'</c> land — the main camp — unlocks travel on its own, and stepping out of one locks it
    /// again. That is why the grant is not a one-way door.</para>
    /// </summary>
    public interface ITravelMapHost
    {
        /// <summary>AS3 <c>PipBuck.travel</c>. While false the world-map page refuses every click
        /// (<c>PipPageInfo.as:724</c>).</summary>
        bool TravelUnlocked { get; }

        /// <summary>Grant travel for this visit. Half of <see cref="GrantTravelAndOpenMap"/>; exposed so a
        /// caller that only needs the grant (a script action, say) does not have to open a page.</summary>
        void UnlockTravel();

        /// <summary>
        /// Show the world map (AS3 <c>pip.onoff(3,3)</c> — PipBuck page 3, tab 3).
        ///
        /// <para><b>This call re-derives <see cref="TravelUnlocked"/> from the land the player is in</b>,
        /// because AS3's <c>onoff</c> ends in <c>setButtons()</c> and that is where the
        /// <c>base</c>-tip recompute lives (<c>PipBuck.as:342-345</c>). A caller that has just granted
        /// travel and wants it to survive this call must use <see cref="GrantTravelAndOpenMap"/>, not
        /// <see cref="UnlockTravel"/> followed by this.</para>
        /// </summary>
        void OpenTravelMap();

        /// <summary>
        /// AS3's <c>Interact.as:1638-1640</c>, verbatim and in order: grant, open, grant again.
        ///
        /// <para>The trailing grant is <b>not</b> redundant. <see cref="OpenTravelMap"/> recomputes the
        /// flag from the current land, so the middle call <i>clears</i> the grant whenever the player is
        /// not standing in a <c>base</c> land — and the third line puts it back. Collapsing the three
        /// steps into one would make a wall map outside the camp silently do nothing.</para>
        /// </summary>
        void GrantTravelAndOpenMap();
    }
}
