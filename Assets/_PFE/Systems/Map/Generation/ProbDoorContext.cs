using System;
using System.Collections.Generic;
using PFE.Data.Definitions.Campaign;
using PFE.Systems.Campaign;

namespace PFE.Systems.Map.Generation
{
    /// <summary>
    /// The concrete <see cref="IProbDoorContext"/>: the <c>prob</c> land's room templates plus a snapshot
    /// of which probs the run has already cleared, and a registry of the detached rooms that get built.
    ///
    /// <para><b>Why the completion set is materialized rather than answered live.</b>
    /// <c>ICampaignManager</c> exposes <c>GetTrigger(name)</c> but nothing that enumerates the trigger
    /// store, so a lazy <c>Contains</c>-only view would have to throw on <c>Count</c> — a land mine for
    /// the next reader. Materializing from the land's own <c>&lt;prob&gt;</c> list costs 66 dictionary
    /// lookups once per land build and gives a real <c>HashSet</c>.</para>
    ///
    /// <para><b>And a snapshot is what the oracle reads anyway.</b> <c>newRandomProb</c> consults
    /// <c>triggers["prob_" + id]</c> at <i>build</i> time (<c>Land.as:821</c>), not continuously, so a
    /// snapshot taken when the land is built is not an approximation of the oracle — it is the same
    /// read.</para>
    /// </summary>
    public sealed class ProbDoorContext : IProbDoorContext
    {
        static readonly RoomTemplate[] NoTemplates = new RoomTemplate[0];

        readonly IReadOnlyList<RoomTemplate> _templates;
        readonly HashSet<string> _completed = new HashSet<string>(StringComparer.Ordinal);
        readonly Dictionary<string, RoomInstance> _rooms = new Dictionary<string, RoomInstance>(StringComparer.Ordinal);

        /// <summary>A context with no prob rooms — the builder will place nothing.</summary>
        public ProbDoorContext()
            : this(NoTemplates)
        {
        }

        /// <summary>A context over a land's prob-room collection and no completion record.</summary>
        public ProbDoorContext(IReadOnlyList<RoomTemplate> probRoomTemplates)
        {
            _templates = probRoomTemplates ?? NoTemplates;
        }

        /// <inheritdoc />
        public IReadOnlyList<RoomTemplate> ProbRoomTemplates => _templates;

        /// <inheritdoc />
        public ICollection<string> CompletedProbKeys => _completed;

        /// <inheritdoc />
        public void RegisterProbRoom(string probId, RoomInstance room)
        {
            if (string.IsNullOrEmpty(probId) || room == null) return;
            _rooms[probId] = room;
        }

        /// <summary>
        /// Builds the context for one land: its prob templates, and the completion record read out of the
        /// campaign for exactly the probs that land declares.
        ///
        /// <para><b>Keyed off <paramref name="landProbs"/>, not the template list.</b> The completion key
        /// is <c>prob_&lt;id&gt;</c> (<c>Probation.as:201-208</c>) and only the land's <c>&lt;prob&gt;</c>
        /// children carry those ids; asking the campaign about a prob the land does not declare would
        /// record a completion that could never be selected anyway.</para>
        /// </summary>
        public static ProbDoorContext ForLand(
            IReadOnlyList<RoomTemplate> probRoomTemplates,
            IReadOnlyList<ProbRoomDefinition> landProbs,
            ICampaignManager campaign)
        {
            var context = new ProbDoorContext(probRoomTemplates);

            if (landProbs == null || campaign == null) return context;

            for (int i = 0; i < landProbs.Count; i++)
            {
                ProbRoomDefinition prob = landProbs[i];
                if (prob == null || string.IsNullOrEmpty(prob.id)) continue;

                string key = ProbSelection.TriggerKey(prob.id);
                if (campaign.GetTrigger(key) > 0) context._completed.Add(key);
            }

            return context;
        }

        /// <summary>
        /// Records a prob as cleared, in the snapshot as well as the run. The runtime calls this when
        /// <c>ProbationState.CloseProb</c> reports a close, so a second door placed later in the same
        /// land build cannot open into a room that has just been finished.
        /// </summary>
        public void MarkCompleted(string probId, ICampaignManager campaign)
        {
            if (string.IsNullOrEmpty(probId)) return;

            string key = ProbSelection.TriggerKey(probId);
            int next = campaign == null ? 1 : campaign.GetTrigger(key) + 1;
            campaign?.SetTrigger(key, next);

            _completed.Add(key);
        }

        /// <summary>Whether a prob has been cleared, per this context's snapshot.</summary>
        public bool IsCompleted(string probId)
        {
            return !string.IsNullOrEmpty(probId) && _completed.Contains(ProbSelection.TriggerKey(probId));
        }

        /// <inheritdoc />
        public bool TryGetRoom(string probId, out RoomInstance room)
        {
            room = null;
            return !string.IsNullOrEmpty(probId) && _rooms.TryGetValue(probId, out room) && room != null;
        }

        /// <summary>How many prob rooms have been built and registered.</summary>
        public int RegisteredRoomCount => _rooms.Count;

        /// <summary>The prob ids registered so far, for a diagnostic that has to name them.</summary>
        public IEnumerable<string> RegisteredProbIds => _rooms.Keys;
    }
}
