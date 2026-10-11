using System;
using PFE.Systems.Map.Generation;

namespace PFE.Systems.Map.Actions
{
    /// <summary>
    /// <c>allact == "probreturn"</c> — the <c>doorout</c> box a prob room carries, which puts the player
    /// back where they came from.
    ///
    /// <para><b>Oracle.</b> <c>Interact.allAct</c>'s second branch (<c>Interact.as:1567-1578</c>):</para>
    /// <code>
    /// else if(this.allact == "probreturn")
    /// {
    ///    if(this.loc.landProb != "")
    ///    {
    ///       … this.loc.land.gotoProb("", this.owner.X, this.owner.Y);
    ///    }
    /// }
    /// </code>
    /// <para>and the <c>param1 == ""</c> half of <c>Land.gotoProb</c>
    /// (<c>Land.as:1393-1410</c>) restores <c>locX/locY/locZ</c> from <c>retLoc*</c> and the player from
    /// <c>retX/retY</c> — or from the room's spawn point when that pair is the <c>(0,0)</c> sentinel.</para>
    ///
    /// <para><b>Why the branch is guarded on the <i>room</i>, not the object.</b> AS3's condition is
    /// <c>this.loc.landProb != ""</c> — "the room I am standing in <i>is</i> a prob room". The port keeps
    /// that as "the host has a saved return point", which is the same fact recorded the other way round:
    /// the host only records one when it entered a prob room, and only clears it on the way out. Reading
    /// it off the <i>object</i> would be wrong — the <c>doorout</c> box exists in exactly the rooms where
    /// the guard is already true, so a per-object test could never be red.</para>
    ///
    /// <para><b>The object is <c>doorout</c></b> — <c>&lt;obj id='doorout' inter='11' time='10' tip='box'
    /// allact='probreturn' wall='1' n='Дверь возврата' …/&gt;</c> (<c>AllData.as:5017</c>). It is placed by
    /// <c>RoomPopulator.PlaceReturnDoor</c> with a deliberately empty <c>prob</c> attribute so this branch
    /// is the one that runs; see <see cref="ObjectInstance.GetProb"/>.</para>
    /// </summary>
    public sealed class ProbReturnAction : IObjectAction
    {
        /// <summary>The <c>allact</c> value this handles.</summary>
        public const string Id = ProbTransition.ReturnActionId;

        private readonly IProbRoomHost _host;

        public ProbReturnAction(IProbRoomHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
        }

        public string ActionId => Id;

        /// <summary>True only when there is somewhere to return to — AS3's <c>landProb != ""</c>.</summary>
        public bool CanExecute(in ObjectActionContext context)
        {
            return _host != null && _host.IsInProbRoom;
        }

        public bool Execute(in ObjectActionContext context)
        {
            if (_host == null) return false;

            return _host.TryReturnFromProb();
        }
    }
}
