using System.Collections.Generic;

namespace PFE.Systems.Map.Generation
{
    /// <summary>
    /// What AS3's <c>Land.newRandomProb</c> needs that is not on the land itself: the detached room
    /// collection a prob room is built from, and the run's record of which probs are already done.
    ///
    /// <para><b>Why a seam rather than a direct read of the campaign database.</b> The builder is handed
    /// one land's room collection (<c>WorldBuilder.BuildLand</c>), so it cannot reach the <c>prob</c>
    /// land's collection on its own — and the two things it needs live in different places anyway: the
    /// rooms are content (<c>GameManager.GetTemplatesForCollection</c>) while the completion record is run
    /// state (<c>ICampaignManager.GetTrigger</c>). Naming the pair as one capability keeps the builder
    /// from acquiring a dependency on either, and lets a test drive it with neither.</para>
    ///
    /// <para><b>Null is a state, not an error.</b> With no context the builder places no prob doors and
    /// reports once — it does not throw, and it does not invent a room. That matches how the rest of the
    /// map layer treats a missing optional collaborator (<c>ObjectActionDispatcher.CreateDefault</c>
    /// reports a missing <c>IRoomLayerTransition</c> and carries on).</para>
    /// </summary>
    public interface IProbDoorContext
    {
        /// <summary>
        /// The <c>prob</c> land's rooms — AS3 <c>World.w.game.probs["prob"].allroom</c>
        /// (<c>Land.as:783</c>). Looked up by <see cref="ProbRoomBuilder.FindRoom"/>. Empty or null means
        /// no prob room can be built, so no door should be placed either.
        /// </summary>
        IReadOnlyList<RoomTemplate> ProbRoomTemplates { get; }

        /// <summary>
        /// Completion-counter keys (AS3 <c>World.w.game.triggers</c>), for the filter at
        /// <c>Land.as:821</c>. Use <see cref="ProbSelection.TriggerKey"/> to build a key. Null means
        /// "nothing completed", which is the correct reading for a fresh run.
        /// </summary>
        ICollection<string> CompletedProbKeys { get; }

        /// <summary>
        /// Hand the built prob room back so the runtime can find it when the door is used — AS3
        /// <c>this.probs[nprob] = …</c> plus <c>listLocs.push(loc)</c> (<c>Land.as:801-802</c>).
        /// </summary>
        void RegisterProbRoom(string probId, RoomInstance room);

        /// <summary>
        /// The detached room built for a prob, for the entry transition to activate — AS3
        /// <c>this.probs[this.prob][locX][locY][locZ]</c> (<c>Land.as:1246</c>).
        ///
        /// <para><b>False is the oracle's refusal, not an error.</b> <c>ativateLoc</c> opens with
        /// <c>if(this.prob != "" &amp;&amp; this.probs[this.prob] == null) return false;</c>
        /// (<c>Land.as:1237-1240</c>) and its caller then rolls the coordinates back and moves nobody
        /// (<c>:1427-1436</c>). So a door whose prob was never built must answer "no" here and let the
        /// entry fail loudly, rather than be handed an invented room.</para>
        ///
        /// <para><b>Why this is on the seam and not read off the concrete type.</b> The runtime that
        /// activates the room is handed an <see cref="IProbDoorContext"/> and nothing else, and
        /// <c>RegisterProbRoom</c>'s own doc already promises "so the runtime can find it when the door is
        /// used" — a lookup that existed only on the implementation would have made that promise
        /// unkeepable without a downcast.</para>
        /// </summary>
        bool TryGetRoom(string probId, out RoomInstance room);
    }
}
