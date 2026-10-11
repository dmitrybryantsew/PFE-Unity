using System;
using PFE.Systems.Map.Scripting;

namespace PFE.Systems.Map.Actions
{
    /// <summary>
    /// <c>allact == "exit"</c> — the exit box that ends a level and rebuilds the land one
    /// <c>landStage</c> deeper.
    ///
    /// <para><b>Oracle, end to end.</b> <c>Interact.allAct</c>'s <c>exit</c> branch is one line:
    /// <c>World.w.game.gotoNextLevel()</c> (<c>Interact.as:1646-1649</c>). <c>Game.gotoNextLevel</c>
    /// (<c>Game.as:464-472</c>) clears the checkpoint, sets <c>crea = true</c>, refills the vendors and
    /// calls <c>gotoLand(missionId)</c> — which re-enters the <i>same</i> land, and because the land is
    /// procedural that regenerates its grid at the new stage. The port's equivalent is
    /// <see cref="ILandScriptHost.GotoNextLevel"/>, which already existed; this action is only the
    /// branch that reaches it.</para>
    ///
    /// <para><b>This is not the bottom-row exit box.</b> That one carries
    /// <c>prob='exit_&lt;land&gt;'</c> (<c>Location.as:2119</c>) and therefore never reaches this branch
    /// at all — <c>Interact.allAct</c> tests <c>prob</c> first (<c>:1558-1565</c>) and takes the prob
    /// path instead. The object this action serves is the <c>exit</c> box inside the detached
    /// <i>exit room</i> (<c>rooms_prob/exit_plant</c>), which authors no <c>prob</c>; the room's own area
    /// script has already run <c>refill</c>, <c>upland</c> and <c>off</c> by the time the player reaches
    /// it. So the level advance is the <i>second</i> hop of a two-hop exit, and
    /// <c>landStage</c> has already been incremented before this fires.</para>
    ///
    /// <para><b>The object is <c>exit</c></b> — <c>&lt;obj id='exit' inter='9' time='60' tip='box'
    /// wall='1' allact='exit' n='Выход с уровня' size='2' wid='3'/&gt;</c>
    /// (<c>AllData.as:5016</c>). It is classified into <c>MapObjectFamily.Transition</c>
    /// (<c>MapObjectDefinition.TransitionIds</c>), so it already gets a <c>DoorPropPresenter</c> and
    /// already reaches <c>Dispatch</c> — the branch was simply unregistered, so pressing E reported
    /// <c>Unhandled</c> and fell through to opening the box like an ordinary door.</para>
    /// </summary>
    public sealed class ExitAction : IObjectAction
    {
        /// <summary>The <c>allact</c> value this handles.</summary>
        public const string Id = "exit";

        private readonly ILandScriptHost _host;

        public ExitAction(ILandScriptHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
        }

        public string ActionId => Id;

        /// <summary>
        /// Always runnable when a host is wired. AS3 puts no guard on this branch
        /// (<c>Interact.as:1646-1649</c>) — unlike <c>comein</c>, which goes through
        /// <c>UnitPlayer.outLoc</c>'s chain (<c>UnitPlayer.as:540-559</c>). The hold is the only gate, and
        /// that is the presenter's, not this action's: <c>time='60'</c> on the definition row
        /// (<c>AllData.as:5016</c>) means the player must hold the key for a second.
        /// </summary>
        public bool CanExecute(in ObjectActionContext context)
        {
            return _host != null;
        }

        public bool Execute(in ObjectActionContext context)
        {
            if (_host == null) return false;

            _host.GotoNextLevel();
            return true;
        }
    }
}
