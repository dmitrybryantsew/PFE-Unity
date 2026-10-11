using System;
using System.Collections.Generic;
using PFE.Data.Definitions.Campaign;

namespace PFE.Systems.Campaign
{
    /// <summary>
    /// The travel-relevant facts about one land, lifted out of <see cref="LandDefinition"/>.
    ///
    /// <para><see cref="LandDefinition"/> is a <c>ScriptableObject</c>: it cannot be constructed outside
    /// the editor (<c>ScriptableObject.CreateInstance</c> is an ECall into Unity's native layer), so any
    /// rule written directly against it is untestable offline — exactly the class of rule this file
    /// exists to pin down. The rules therefore read this value type instead, and
    /// <see cref="From"/> is the single place that knows how to project an asset onto it.</para>
    /// </summary>
    public readonly struct TravelLand
    {
        /// <summary>AS3 <c>LandAct.id</c>.</summary>
        public readonly string LandId;

        /// <summary>
        /// AS3 keeps the detached room collection in <c>game.probs</c>, not <c>game.lands</c>
        /// (<c>Game.as:75-82</c>): the split is on the <c>prob</c> attribute, not on <c>tip</c>.
        /// The importer wrote such a land with <c>tip == "prob"</c>, so both signals are honoured.
        /// </summary>
        public readonly bool IsProb;

        /// <summary>AS3 the <c>test</c> attribute — a developer sandbox land.</summary>
        public readonly bool IsTest;

        /// <summary>AS3 <c>fin</c>: 0 = normal, 1 = final mission, 2 = ending epilogue.</summary>
        public readonly int Fin;

        /// <summary>AS3 <c>LandAct.loaded</c> — the land's room XML has been read. In the port that is
        /// "the definition carries templates".</summary>
        public readonly bool Loaded;

        public TravelLand(string landId, bool isProb, bool isTest, int fin, bool loaded)
        {
            LandId = landId ?? string.Empty;
            IsProb = isProb;
            IsTest = isTest;
            Fin = fin;
            Loaded = loaded;
        }

        /// <summary>Project a land asset onto its travel facts. A null asset reads as a prob land, i.e.
        /// invisible and untravellable, which is the safe direction.</summary>
        public static TravelLand From(LandDefinition land)
        {
            if (land == null) return new TravelLand(string.Empty, true, true, 0, false);

            bool isProb = land.isProbLand ||
                          string.Equals(land.tip, "prob", StringComparison.OrdinalIgnoreCase);
            bool loaded = land.roomTemplates != null && land.roomTemplates.Count > 0;

            return new TravelLand(land.landId, isProb, land.isTestLand, land.fin, loaded);
        }
    }

    /// <summary>
    /// The travel map ("select area") as a <b>model first, view second</b>.
    /// Ports the two gates AS3 applies on the PipBuck Info page —
    /// visibility (<c>PipPageInfo.as:143-205</c>) and travellability
    /// (<c>Game.checkTravel</c>, <c>Game.as:483-506</c>) — plus the load gate
    /// (<c>PipPageInfo.as:761</c>).
    ///
    /// <para>No UI here on purpose: the visibility/travel rules are the part that can silently be wrong,
    /// and they are assertable offline. That is why this class consumes <see cref="TravelLand"/> rather
    /// than <see cref="LandDefinition"/> — see the note on that type.</para>
    /// </summary>
    public sealed class TravelMapModel
    {
        private readonly IReadOnlyList<TravelLand> _lands;
        private readonly LandRuntimeStateRegistry _states;
        private readonly Func<string, int> _triggerLookup;
        private readonly Func<string> _currentLandId;
        private readonly bool _testMode;

        /// <param name="lands">The lands to consider, in presentation order (may be null).</param>
        /// <param name="states">Per-land runtime state; supplies <c>visited</c> / <c>access</c>.</param>
        /// <param name="triggerLookup">Reads a world trigger (used for the <c>fin</c> gate).</param>
        /// <param name="currentLandId">Reads the current land id — <c>checkTravel</c> keys off it.</param>
        /// <param name="testMode">AS3 <c>World.w.testMode</c>: makes every non-test land visible.</param>
        public TravelMapModel(
            IReadOnlyList<TravelLand> lands,
            LandRuntimeStateRegistry states,
            Func<string, int> triggerLookup = null,
            Func<string> currentLandId = null,
            bool testMode = false)
        {
            _lands = lands ?? (IReadOnlyList<TravelLand>)Array.Empty<TravelLand>();
            _states = states ?? new LandRuntimeStateRegistry();
            _triggerLookup = triggerLookup ?? (_ => 0);
            _currentLandId = currentLandId ?? (() => string.Empty);
            _testMode = testMode;
        }

        /// <summary>
        /// Build the model from the authored catalogue. This is the production entry point; it is the
        /// only overload that touches a <c>ScriptableObject</c>.
        /// </summary>
        public static TravelMapModel FromCatalog(
            CampaignCatalog catalog,
            LandRuntimeStateRegistry states,
            Func<string, int> triggerLookup = null,
            Func<string> currentLandId = null,
            bool testMode = false)
        {
            var lands = new List<TravelLand>();
            if (catalog != null)
            {
                IReadOnlyList<LandDefinition> source = catalog.AllLands;
                if (source != null)
                {
                    for (int i = 0; i < source.Count; i++)
                    {
                        if (source[i] != null) lands.Add(TravelLand.From(source[i]));
                    }
                }
            }
            return new TravelMapModel(lands, states, triggerLookup, currentLandId, testMode);
        }

        /// <summary>Every land under consideration, in the order it was supplied.</summary>
        public IReadOnlyList<TravelLand> Lands => _lands;

        /// <summary>AS3 <c>Game.as:75-82</c> — prob lands are detached and never offered.</summary>
        public static bool IsProbLand(TravelLand land) => land.IsProb;

        /// <summary>AS3 <c>LandAct.loaded</c> (<c>LandAct.as:14</c>).</summary>
        public static bool IsLoaded(TravelLand land) => land.Loaded;

        /// <summary>
        /// AS3 <c>PipPageInfo.as:193</c> — <c>testMode || visited || access</c>, and a dev-sandbox land
        /// (<c>test='1'</c>) is hidden outside test mode. A land that fails this is not greyed out;
        /// it is <b>invisible</b>.
        /// </summary>
        public bool IsVisible(TravelLand land)
        {
            if (land.IsProb) return false;
            if (land.IsTest && !_testMode) return false;
            if (_testMode) return true;

            LandRuntimeState state = _states.Get(land.LandId);
            return state.visited || state.access;
        }

        /// <summary>
        /// AS3 <c>Game.checkTravel</c> (<c>Game.as:483-506</c>) — the <c>fin</c> gate.
        /// <c>grave</c> is never travelable, and the finale stages restrict which lands are reachable.
        /// </summary>
        public bool CheckTravel(TravelLand land)
        {
            // The oracle tests the CURRENT land id, not the target (`Game.as:485`).
            if (string.Equals(_currentLandId(), "grave", StringComparison.OrdinalIgnoreCase)) return false;

            int fin = _triggerLookup("fin");
            if (fin <= 0) return true;
            if (fin == 1) return land.Fin == 0 || land.Fin == 1;
            if (fin == 2) return land.Fin == 0 || land.Fin == 2;
            if (fin == 3) return land.Fin == 2;
            return true;
        }

        /// <summary>
        /// The confirm button's full gate: <c>checkTravel</c> <b>and</b> the land's room XML loaded
        /// (<c>PipPageInfo.as:750-766</c>).
        /// </summary>
        public bool CanTravel(TravelLand land)
        {
            if (land.IsProb) return false;
            if (!land.Loaded) return false;
            return CheckTravel(land);
        }

        /// <summary>The lands the map should draw a clickable icon for.</summary>
        public List<TravelLand> OfferedLands()
        {
            var result = new List<TravelLand>();
            for (int i = 0; i < _lands.Count; i++)
            {
                if (IsVisible(_lands[i])) result.Add(_lands[i]);
            }
            return result;
        }

        /// <summary>The lands the map should draw as "can travel here now".</summary>
        public List<TravelLand> TravellableLands()
        {
            var result = new List<TravelLand>();
            for (int i = 0; i < _lands.Count; i++)
            {
                if (IsVisible(_lands[i]) && CanTravel(_lands[i])) result.Add(_lands[i]);
            }
            return result;
        }
    }
}
