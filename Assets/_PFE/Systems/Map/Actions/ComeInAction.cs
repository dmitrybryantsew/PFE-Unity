using System;

namespace PFE.Systems.Map.Actions
{
    /// <summary>
    /// <c>allact == "comein"</c> — a "Z door": step through to the opposite layer of the same land cell.
    ///
    /// <para><b>Oracle, end to end.</b> <c>Interact.as:1619-1621</c> calls
    /// <c>World.w.gg.outLoc(5, this.X, this.Y)</c>; <c>UnitPlayer.outLoc</c>
    /// (<c>UnitPlayer.as:540-559</c>) applies the player-state guards and forwards to
    /// <c>Land.gotoLoc(5, X, Y)</c>; there <c>param1 == 5</c> means <c>locZ = 1 - locZ</c>
    /// (<c>Land.as:1330-1332</c>) and the arrival point is taken verbatim from the passed coordinates
    /// (<c>:1367-1371</c>) rather than computed from the player's position.</para>
    ///
    /// <para><b>This is not an edge transition.</b> x and y do not change, and the player arrives at the
    /// door. That is why nothing here needs z-aware adjacency — AS3 has none either: <c>gotoLoc</c>'s
    /// directions 1–4 move in the plane and only case 5 touches z. Modelling the toggle as a fifth
    /// adjacency would have been an invention.</para>
    ///
    /// <para>The doors that carry this are exactly nine objects: <c>indoor1..4</c>, <c>instdoor</c>,
    /// <c>inbasedoor</c> and <c>inencldoor</c> (<c>AllData.as:4916-4922</c>, the seven literally named
    /// "…Z"), plus <c>door_st1</c> and <c>door_st2</c> (<c>AllData.as:5040-5041</c>). Grep
    /// <c>allact='comein'</c> and those nine are the whole set.</para>
    ///
    /// <para><b><c>doorboss</c> is not one of them.</b> It is worth saying outright because the name
    /// invites the assumption: <c>doorboss</c> carries no <c>allact</c> at all
    /// (<c>AllData.as:5019</c>, <c>inter='8' tip='box' wall='1'</c>), and it reaches a prob room through
    /// the <c>prob</c> attribute that <c>Interact.allAct</c> tests <i>before</i> the <c>allact</c> switch
    /// (<c>Interact.as:1558-1565</c>). An earlier version of this comment listed it here, which would
    /// send a reader looking for a <c>comein</c> handler on an object that never dispatches one.</para>
    /// </summary>
    public sealed class ComeInAction : IObjectAction
    {
        /// <summary>The <c>allact</c> value this handles.</summary>
        public const string Id = "comein";

        private readonly IRoomLayerTransition _transition;

        public ComeInAction(IRoomLayerTransition transition)
        {
            _transition = transition ?? throw new ArgumentNullException(nameof(transition));
        }

        public string ActionId => Id;

        /// <summary>
        /// Whether the toggle is possible. The player-state half of AS3's guard chain — teleporting,
        /// mid-action, working, fettered, in the sky (<c>UnitPlayer.as:542</c>) and
        /// <c>World.w.possiblyOut()</c> (<c>:547</c>, which blocks it while hostiles are near) — is
        /// <b>not</b> reproduced yet; only the map-side condition is. That is the honest current state:
        /// the guards live on the player and have no port.
        /// </summary>
        public bool CanExecute(in ObjectActionContext context)
        {
            return _transition.CanToggleLayer;
        }

        public bool Execute(in ObjectActionContext context)
        {
            if (!CanExecute(in context))
            {
                return false;
            }

            return _transition.ToggleLayer(context.User, context.WorldPosition);
        }
    }
}
