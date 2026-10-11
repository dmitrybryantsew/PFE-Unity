using System;
using PFE.Systems.Campaign;

namespace PFE.Systems.Map.Actions
{
    /// <summary>
    /// <c>allact == "map"</c> — the camp's wall map. Interacting with it grants travel and opens the
    /// world map.
    ///
    /// <para><b>Oracle</b> (<c>Interact.as:1636-1641</c>):</para>
    /// <code>
    /// else if(this.allact == "map")
    /// {
    ///    World.w.pip.travel = true;
    ///    World.w.pip.onoff(3,3);
    ///    World.w.pip.travel = true;   // &lt;-- not a copy/paste slip
    /// }
    /// </code>
    ///
    /// <para><b>The flag is set twice on purpose, and the port keeps both writes.</b>
    /// <c>onoff</c> ends by calling <c>setButtons()</c>, which recomputes <c>travel</c> from the land you
    /// are standing in — <c>if(!light &amp;&amp; loc &amp;&amp; !loc.base) travel = false</c>
    /// (<c>PipBuck.as:342-345</c>). So the middle call <i>clears</i> the flag the first line just set
    /// whenever the map object is somewhere other than a base land, and the trailing line re-asserts it.
    /// Collapsing the three lines into one write would make a wall map outside the camp silently do
    /// nothing — the kind of change that looks like a tidy-up and is a behaviour change.</para>
    ///
    /// <para>The object is <c>wmap</c> — <c>&lt;obj id='wmap' allact='map' n='Карта' size='4' wid='3'/&gt;</c>
    /// (<c>AllData.as:5020</c>), already imported with <c>allAct: map</c>. The camp places one in
    /// <c>rooms_rbl/room_0_0</c>.</para>
    /// </summary>
    public sealed class MapAction : IObjectAction
    {
        /// <summary>The <c>allact</c> value this handles.</summary>
        public const string Id = "map";

        private readonly ITravelMapHost _host;

        public MapAction(ITravelMapHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
        }

        public string ActionId => Id;

        /// <summary>
        /// Always runnable when a host is wired. AS3 puts no guard on this branch — the map opens from
        /// anywhere, including a land you are not supposed to leave, because the <c>base</c> recompute in
        /// <c>setButtons</c> is what takes the grant back.
        /// </summary>
        public bool CanExecute(in ObjectActionContext context)
        {
            return _host != null;
        }

        public bool Execute(in ObjectActionContext context)
        {
            if (_host == null) return false;

            // Interact.as:1638-1640. The three steps and their order live on the host, because the NPC
            // `travel` path needs exactly the same sequence (NPC.as:213-215) and duplicating it is how the
            // trailing re-grant gets "tidied away" in one copy and not the other.
            _host.GrantTravelAndOpenMap();
            return true;
        }
    }
}
