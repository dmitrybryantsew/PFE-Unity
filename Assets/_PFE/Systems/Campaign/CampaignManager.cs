using System;
using System.Collections.Generic;
using UnityEngine;
using R3;
using MessagePipe;
using VContainer.Unity;
using PFE.Core.Messages;
using PFE.Data.Definitions.Campaign;
using PFE.Systems.Map.Scripting;
using PFE.Systems.Trade;
using PFE.Sim.Campaign;

namespace PFE.Systems.Campaign
{
    /// <summary>
    /// Runtime Campaign Manager.
    /// Manages active land state, quest tracking, persistent world triggers,
    /// and listens to LandTransitionMessage from AreaTriggerSystem and script actions.
    ///
    /// <para>Also the <see cref="ILandScriptHost"/>: it owns the per-land runtime state
    /// (<c>landStage</c>, <c>access</c>, <c>visited</c>, <c>passed</c>, <c>upStage</c>) and is the one
    /// place that publishes a <see cref="LandBuildRequestMessage"/> — the build itself belongs to
    /// <c>MapBridge</c>. See <c>docs/LandGameplayLoop/03_GAP_LEDGER.md</c> §1.</para>
    /// </summary>
    public class CampaignManager : ICampaignManager, ILandScriptHost, IScriptQuestHost, ITravelMapHost, IInitializable, IDisposable
    {
        /// <summary>
        /// The live instance, for the scene components that cannot be injected.
        ///
        /// <para><c>DoorPropPresenter</c> is added with <c>AddComponent</c> by
        /// <c>RoomObjectVisualManager</c>, so VContainer never observes it and <c>[Inject]</c> never runs
        /// — the same limitation <c>MapBridge</c> documents for <c>RoomVisualController</c>. The project's
        /// answer elsewhere is a static accessor over a resolved instance
        /// (<c>RoomTransitionManager.Instance</c>); this is that, for a type that is not a
        /// <c>MonoBehaviour</c> and so cannot be found with <c>FindFirstObjectByType</c>.</para>
        ///
        /// <para>Set in <see cref="Initialize"/> and cleared in <see cref="Dispose"/>, so it tracks the
        /// container's lifetime rather than a stray constructor call.</para>
        /// </summary>
        public static CampaignManager Current { get; private set; }

        private readonly ISubscriber<LandTransitionMessage> _transitionSubscriber;
        private readonly IPublisher<LandTransitionMessage> _transitionPublisher;
        private readonly IPublisher<LandBuildRequestMessage> _buildPublisher;
        private readonly IPublisher<TravelMapOpenedMessage> _travelMapPublisher;

        /// <summary>
        /// AS3 <c>PipBuck.travel</c> — whether the world map accepts a destination right now.
        ///
        /// <para>Recomputed from the current land on every entry and on every map open
        /// (<c>PipBuck.as:271-273</c>, <c>:342-345</c>), and granted outright by the camp's wall map or a
        /// <c>travel</c> NPC. See <see cref="ITravelMapHost"/>.</para>
        /// </summary>
        private bool _travelUnlocked;

        /// <summary>
        /// AS3 <c>Game.missionId</c> — the land a mission began in, which is where <c>gotoNextLevel</c>
        /// returns to (<c>Game.as:471</c>). It survives a trip back to camp, unlike <c>curLandId</c>.
        /// </summary>
        private string _missionId;

        /// <summary>
        /// The authored catalogue. Resolved lazily via <see cref="Catalog"/>: the constructor used
        /// to call <c>Resources.Load</c> unconditionally, which made the whole class impossible to
        /// construct outside the engine — including in the offline wall, where <c>Resources.Load</c> is
        /// an ECall and throws. Nothing about constructing the campaign state needs the asset.
        /// </summary>
        private CampaignCatalog _catalog;

        /// <summary>Whether the one-shot <c>Resources.Load</c> fallback has already been attempted.</summary>
        private bool _catalogLookupAttempted;

        private IDisposable _transitionSubscription;

        /// <summary>Per-land runtime state. AS3 keeps this on <c>LandAct</c> (<c>LandAct.as:90-101</c>);
        /// it must never live on the shared <c>LandDefinition</c> ScriptableObject.</summary>
        private readonly LandRuntimeStateRegistry _landStates = new LandRuntimeStateRegistry();

        public LandRuntimeStateRegistry LandStates => _landStates;

        /// <summary>
        /// AS3 <c>Game.vendors</c> (<c>Game.as:61</c>) — one live <c>VendorRuntime</c> per imported
        /// <c>&lt;vendor&gt;</c> block, keyed by id. Populated by <see cref="InitializeVendors"/>;
        /// empty (and harmless) until then, so a headless host that never calls it still works.
        /// </summary>
        private readonly VendorRegistry _vendors = new VendorRegistry();

        public VendorRegistry Vendors => _vendors;

        /// <summary>
        /// AS3 <c>World.w.pers.limitBuys</c>, read by the refill sweep (<c>Vendor.as:304</c>). A
        /// <see cref="Func{TResult}"/> for the same reason every status provider is one: the player does
        /// not exist when this manager is constructed. Null reads as the field's own default, 1
        /// (<c>Pers.as:317</c>).
        /// </summary>
        public Func<float> VendorLimitBuysProvider { get; set; }

        /// <summary>
        /// AS3 <c>World.w.pers.barterLvl</c>, read by <c>setRndBuys</c>'s count
        /// (<c>Vendor.as:142/146</c>). Null reads as 0, the field's default.
        /// </summary>
        public Func<int> VendorBarterLevelProvider { get; set; }

        // Reactive State
        private readonly ReactiveProperty<string> _currentLandId = new ReactiveProperty<string>("");
        private readonly ReactiveProperty<LandDefinition> _currentLand = new ReactiveProperty<LandDefinition>(null);

        public ReadOnlyReactiveProperty<string> CurrentLandId => _currentLandId;
        public ReadOnlyReactiveProperty<LandDefinition> CurrentLand => _currentLand;

        /// <summary>
        /// The catalogue, or <c>null</c> when there is none. Every call site already guards for null, so
        /// an absent catalogue degrades to "transition by id only" rather than throwing.
        ///
        /// <para>The <c>Resources.Load</c> fallback runs at most once and is guarded: offline (or in any
        /// host without the engine) it is an ECall that throws, and a thrown lookup must mean "no
        /// catalogue", not "the campaign manager cannot be built".</para>
        /// </summary>
        public CampaignCatalog Catalog
        {
            get
            {
                if (_catalogLookupAttempted) return _catalog;
                _catalogLookupAttempted = true;

                if (_catalog == null)
                {
                    try
                    {
                        _catalog = Resources.Load<CampaignCatalog>("CampaignCatalog");
                    }
                    catch (Exception)
                    {
                        // No engine here. Deliberately silent: the caller's own "not in the catalog"
                        // warning is the useful report, and logging from this catch would re-enter the
                        // very native layer that just failed.
                        _catalog = null;
                    }
                }

                if (_catalog != null) _catalog.Initialize();
                return _catalog;
            }
        }

        // Story triggers (World.w.game.triggers)
        //
        // AS3's table is untyped and holds strings as well as ints (`"wait"` from NPC.as:591, a quest
        // `@set` value from Quest.as:332), so the value is a TriggerValue and not an int. See
        // TriggerValue's own docs for why the two oracle readers do NOT disagree about a string "1".
        private readonly Dictionary<string, TriggerValue> _triggers = new Dictionary<string, TriggerValue>(StringComparer.OrdinalIgnoreCase);

        // Quest progress.
        //
        // AS3 keeps a Quest object per quest — a *tree*: the parent carries `state` and an ordered list
        // of sub-objectives, each with its own state, collect counter and visibility (Quest.as:9-83).
        // The port used to keep three flat collections (`Dictionary<string,int>` for a stage index plus
        // two HashSets), which cannot express "sub 3 done while 1 and 2 stay open" and cannot complete a
        // parent at all. See QuestRules for what each operation really is.
        //
        // The immutable half (the authored definition, projected) is cached per quest id; the mutable
        // half is the runtime state. The projection is cached because QuestFacts is derived purely from
        // the asset, and rebuilding it per call would allocate on a hot path.
        private readonly Dictionary<string, QuestFacts> _questFacts = new Dictionary<string, QuestFacts>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, QuestRuntimeState> _questRuntime = new Dictionary<string, QuestRuntimeState>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The campaign event surface — see <see cref="CampaignEventBus"/>. Optional so a headless
        /// construction keeps working; a null bus drops events rather than throwing.
        /// </summary>
        private readonly CampaignEventBus _events;

        /// <summary>
        /// Where a quest's XP / skill points / reputation go on close. AS3 pays these from
        /// <c>Quest.close</c> straight into <c>World.w.pers</c> (<c>Quest.as:645-656</c>); the port has no
        /// campaign→character-stat seam, so the payout is delegated and <b>reported when unwired</b>.
        /// </summary>
        private readonly IQuestRewardHost _questRewards;

        /// <summary>
        /// Runs an authored script by id — AS3 <c>Game.runScript(id)</c> (<c>Game.as:722-737</c>), which a
        /// quest's <c>endscr</c> needs (<c>Quest.as:704-707</c>). Optional and reported when absent.
        /// </summary>
        private readonly IQuestScriptHost _questHooks;

        public CampaignManager(
            ISubscriber<LandTransitionMessage> transitionSubscriber = null,
            IPublisher<LandTransitionMessage> transitionPublisher = null,
            CampaignCatalog catalog = null,
            IPublisher<LandBuildRequestMessage> buildPublisher = null,
            IPublisher<TravelMapOpenedMessage> travelMapPublisher = null,
            CampaignEventBus events = null,
            IQuestRewardHost questRewards = null,
            IQuestScriptHost questHooks = null)
        {
            _transitionSubscriber = transitionSubscriber;
            _transitionPublisher = transitionPublisher;
            _buildPublisher = buildPublisher;
            _travelMapPublisher = travelMapPublisher;
            _events = events;
            _questRewards = questRewards;
            _questHooks = questHooks;

            // No engine call here. A null catalogue is a legitimate state (headless, tests, a host with
            // no authored content) and every reader guards for it; the Resources fallback happens on the
            // first `Catalog` access instead.
            _catalog = catalog;
        }

        public void Initialize()
        {
            Current = this;

            if (_transitionSubscriber != null)
            {
                _transitionSubscription = _transitionSubscriber.Subscribe(OnLandTransitionRequested);
            }

            // Start in starting land if uninitialized
            CampaignCatalog catalog = Catalog;
            if (string.IsNullOrEmpty(_currentLandId.Value) && catalog != null)
            {
                string startLand = !string.IsNullOrEmpty(catalog.startingLandId) ? catalog.startingLandId : "begin";
                TransitionToLand(startLand);
            }
        }

        public void Dispose()
        {
            if (ReferenceEquals(Current, this)) Current = null;

            _transitionSubscription?.Dispose();
            _currentLandId?.Dispose();
            _currentLand?.Dispose();
        }

        private void OnLandTransitionRequested(LandTransitionMessage msg)
        {
            Debug.Log($"[CampaignManager] Received LandTransitionMessage: TargetLand='{msg.TargetLand}', Coords='{msg.TargetCoordinates}'");
            TransitionToLand(msg.TargetLand, msg.TargetCoordinates);
        }

        public void TransitionToLand(string targetLandId, string spawnPoint = null, bool forceRegenerate = false)
        {
            if (string.IsNullOrWhiteSpace(targetLandId))
            {
                Debug.LogWarning("[CampaignManager] Attempted transition to empty land ID.");
                return;
            }

            string previousLandId = _currentLandId.Value;
            string resolvedId = targetLandId;

            CampaignCatalog catalog = Catalog;
            if (catalog != null)
            {
                var landDef = catalog.GetLand(targetLandId);
                if (landDef != null)
                {
                    resolvedId = landDef.landId;
                    _currentLandId.Value = landDef.landId;
                    _currentLand.Value = landDef;
                }
                else
                {
                    // Do NOT claim success when the catalog lookup failed — the old log did, which is
                    // what made a dead transition read as a working one (03_GAP_LEDGER.md §1, D1).
                    Debug.LogWarning($"[CampaignManager] Land '{targetLandId}' is not in the catalog; " +
                                     "transitioning by id only.");
                    _currentLandId.Value = targetLandId;
                }
            }
            else
            {
                _currentLandId.Value = targetLandId;
            }

            // AS3 enterToCurLand clears upStage on entry (Game.as:396-399) — that is what lets the next
            // run's `upland` increment again.
            _landStates.ResetUpStage(resolvedId);

            // AS3 builds every room's Location up front at land load (Land.as:947) and swaps them on a
            // room change (:1263), so a land entry discards the whole previous set — and with it every
            // re-pointed button. Only `act`/`used` come back on the next build (CheckPoint.as:110-122),
            // which is why a checkpoint that offered the return before the trip does not offer it on
            // arrival: its restored `active == 2` makes `activate()` return at :190 before it can
            // re-point anything.
            ReturnCheckpoint = null;

            // Arriving somewhere re-derives the travel grant: the camp unlocks it, anywhere else locks it
            // (PipBuck.as:271-273 / :342-345).
            RefreshTravelUnlockedForCurrentLand();

            // AS3 `Game.enterToCurLand` runs once per entry (Game.as:396-400); this is that moment,
            // named. Published before the build request so a hook that reacts to the entry sees the
            // campaign's state already consistent with the new land.
            _events?.PublishLandEntered(resolvedId, previousLandId, _landStates.Get(resolvedId).landStage);

            _buildPublisher?.Publish(new LandBuildRequestMessage(resolvedId, spawnPoint, forceRegenerate));
        }

        /// <summary>
        /// Adopt the land whose room collection the world was just built from, <b>without</b> publishing a
        /// build request. For the one caller that builds a world the campaign did not ask for: the scene's
        /// debug room override (<c>MapBridge._useRoomOverride</c>).
        /// </summary>
        /// <remarks>
        /// <para><b>The defect this closes.</b> <see cref="Initialize"/> sets <c>_currentLandId</c> to
        /// <c>catalog.startingLandId</c> and <i>then</i> publishes a build request. Two things can happen
        /// to that request and, in the shipped scene, the second one does: <c>MapBridge</c> subscribes in
        /// <c>Start</c> while the campaign publishes during its own initialization, so the request is
        /// dropped — and the scene's room override then builds a different collection entirely
        /// (<c>rooms_rbl</c> where the campaign named <c>begin</c>). Measured in <c>Editor.log</c>: the only
        /// room in the world is <c>rooms_rbl/room_0_0_0_0_0</c> while <c>[CampaignManager]</c> prints
        /// <c>in 'begin'</c>, and <c>rbl</c> is never a build target anywhere in the session.</para>
        ///
        /// <para><b>Why that is worse than a cosmetic label.</b> The F2 Lands tab marks the current land
        /// with a marker and labels its button "Rebuild", so the wrong row was marked and that button
        /// declined to do anything at all (<c>BeginMission</c> no-ops on an id match). Pressing Go on the
        /// land you were <i>actually</i> standing in rebuilt it; the row that looked current did nothing.</para>
        ///
        /// <para><b>Why not <see cref="TransitionToLand"/>.</b> That publishes a build request, and this
        /// caller's world is already built — re-publishing would rebuild it and race the override. Only the
        /// identity and the state derived from it are set here.</para>
        ///
        /// <para><b><c>upStage</c> is deliberately not reset.</b> AS3 clears it in <c>enterToCurLand</c>
        /// (<c>Game.as:396-399</c>), but this is a debug boot and not an entry: silently clearing per-entry
        /// stage state from here would make a later <c>upland</c> bug unattributable.</para>
        /// </remarks>
        /// <param name="collectionId">A room collection key, i.e. <c>RoomTemplate.sourceCollectionId</c>.</param>
        /// <returns>True when a land was adopted, or when the current land already matched it.</returns>
        public bool AdoptBuiltLand(string collectionId)
        {
            string previous = string.IsNullOrEmpty(_currentLandId.Value) ? "(none)" : _currentLandId.Value;

            if (string.IsNullOrWhiteSpace(collectionId))
            {
                Debug.LogWarning(
                    "[CampaignManager] AdoptBuiltLand was given no room collection, so the world that was " +
                    $"just built cannot be attributed to a land. The current land stays '{previous}'.");
                return false;
            }

            CampaignCatalog catalog = Catalog;
            if (catalog == null || catalog.AllLands == null)
            {
                Debug.LogWarning(
                    $"[CampaignManager] No catalogue, so collection '{collectionId}' cannot be resolved to " +
                    $"a land. The current land stays '{previous}'.");
                return false;
            }

            LandDefinition match = null;
            for (int i = 0; i < catalog.AllLands.Count; i++)
            {
                LandDefinition land = catalog.AllLands[i];
                if (land == null || string.IsNullOrEmpty(land.landId)) continue;
                if (!string.Equals(land.sourceFileKey, collectionId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // A collection can back more than one land — `rooms_mbase` is both `random_mbase` and
                // `bunker`. The override builds authored templates, so the authored land is the better
                // answer; a tie keeps the first, which is deterministic rather than catalogue-order luck.
                if (match == null || (match.isProcedural && !land.isProcedural))
                {
                    match = land;
                }
            }

            if (match == null)
            {
                Debug.LogWarning(
                    $"[CampaignManager] No land declares sourceFileKey '{collectionId}', so the world that " +
                    $"was just built cannot be attributed to a land. The current land stays '{previous}' — " +
                    "which is a different land from the one you are standing in, so the Lands tab will mark " +
                    "the wrong row as current. Add the land to CampaignCatalog, or turn the room override " +
                    "off in the scene.");
                return false;
            }

            if (string.Equals(match.landId, _currentLandId.Value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            Debug.Log(
                $"[CampaignManager] Adopting land '{match.landId}' for the world built from collection " +
                $"'{collectionId}' (was '{previous}').");

            _currentLandId.Value = match.landId;
            _currentLand.Value = match;

            // Arriving somewhere re-derives the travel grant (PipBuck.as:271-273 / :342-345) and the camp is
            // the land that unlocks it. Read through the same helper as TransitionToLand so the two cannot
            // drift apart.
            RefreshTravelUnlockedForCurrentLand();
            return true;
        }

        // =====================================================================
        //  ILandScriptHost — the campaign-level script vocabulary
        //  (03_GAP_LEDGER.md §8: `upland`, `openland`, `refill` and `trigger` are the four that matter)
        // =====================================================================

        /// <summary>AS3 <c>upland</c> → <c>Game.upLandLevel()</c> (<c>Game.as:474-481</c>).</summary>
        public bool UpLandLevel()
        {
            string landId = _currentLandId.Value;
            if (string.IsNullOrEmpty(landId)) return false;

            bool moved = _landStates.UpLandLevel(landId);
            Debug.Log($"[CampaignManager] upland: land '{landId}' landStage is now " +
                      $"{_landStates.Get(landId).landStage} ({(moved ? "incremented" : "already raised this entry")})");
            return moved;
        }

        /// <summary>AS3 <c>openland</c> (<c>Script.as:458-468</c>).</summary>
        public bool OpenLand(string landId)
        {
            if (string.IsNullOrEmpty(landId)) return false;

            CampaignCatalog catalog = Catalog;
            if (catalog != null && !catalog.HasLand(landId))
            {
                Debug.LogWarning($"[CampaignManager] openland: error land '{landId}' — not in the catalog.");
                return false;
            }

            _landStates.SetAccess(landId, true);
            Debug.Log($"[CampaignManager] openland: '{landId}' is now selectable on the travel map.");
            return true;
        }

        /// <summary>
        /// AS3 <c>refill</c> → <c>Land.refill()</c> (<c>Land.as:1481-1496</c>) → <c>Game.refillVendors</c>
        /// (<c>Game.as:508-525</c>).
        ///
        /// <para><b>What the oracle does, in order</b> (<c>Game.as:508-525</c>): refill every vendor;
        /// flip every trigger whose value is the string <c>"wait"</c> to <c>1</c>; set
        /// <c>invent.good.kol = pers.goodHp</c>; show the <c>"refill"</c> info text.</para>
        ///
        /// <para><b>What is ported and what is not.</b> The vendor sweep and the trigger flip are here —
        /// the flip is how <c>patient_tr2</c> and similar "come back later" beats advance
        /// (<c>02_USER_MODEL_RECHECK.md</c> §7). The <c>good.kol</c> line belongs to the inventory, which
        /// the campaign manager has no reference to, and the info text is presentation; both are
        /// <b>reported</b> below rather than silently dropped.</para>
        /// </summary>
        public void RefillVendors()
        {
            float limitBuys = VendorLimitBuysProvider?.Invoke() ?? 1f;   // Pers.as:317
            int barterLevel = VendorBarterLevelProvider?.Invoke() ?? 0;  // Pers.as:315

            _vendors.RefillAll(limitBuys, barterLevel);

            // Game.as:514-520 — `if(this.triggers[k] == "wait") this.triggers[k] = 1;`. TriggerValue's
            // own EqualsString doc names this sweep as its reason for existing, and it answers false for
            // an int entry, which is what keeps a numeric 1 from being rewritten.
            List<string> waiting = null;
            foreach (KeyValuePair<string, TriggerValue> pair in _triggers)
            {
                if (pair.Value.EqualsString("wait"))
                {
                    (waiting ??= new List<string>()).Add(pair.Key);
                }
            }

            if (waiting != null)
            {
                for (int i = 0; i < waiting.Count; i++)
                {
                    _triggers[waiting[i]] = TriggerValue.FromInt(1);
                }
            }

            Debug.Log($"[CampaignManager] refill: {_vendors.Count} vendors restocked, " +
                      $"{waiting?.Count ?? 0} 'wait' trigger(s) advanced. " +
                      "invent.good.kol and the 'refill' info text are not wired " +
                      "(Game.as:522-524) — the campaign manager has no inventory or GUI seam.");
        }

        /// <summary>
        /// Builds the live vendors from the catalogue — AS3 <c>Game.init</c>'s
        /// <c>for each(GameData.d.vendor) new Vendor(0, xml, saved)</c> (<c>Game.as:220-229</c>).
        ///
        /// <para>Separate from <see cref="Initialize"/> because it needs two things the constructor must
        /// not require: the item-table seam (an ECall-bearing adapter in production) and the loot RNG.
        /// The composition root calls it once the game database is up.</para>
        /// </summary>
        public void InitializeVendors(IVendorItemSource items, PFE.Core.Rng.IRngService rng,
            IReadOnlyDictionary<string, List<VendorStockSave>> saved = null)
        {
            if (items == null || rng == null)
            {
                Debug.LogWarning("[CampaignManager] InitializeVendors called without an item source or RNG; " +
                                 "the vendors stay unbuilt and every trade page would show an empty shelf.");
                return;
            }

            CampaignCatalog catalog = Catalog;
            IReadOnlyList<VendorDefinition> definitions = catalog?.AllVendors;

            _vendors.Initialize(definitions, rng, items, saved);

            if (definitions == null || definitions.Count == 0)
            {
                Debug.LogWarning("[CampaignManager] No <vendor> definitions are in the catalogue, so no " +
                                 "vendors were built. Run PFE ▸ Campaign ▸ Import Vendor Stock from " +
                                 "GameData.as — a page over an empty shelf looks exactly like a working " +
                                 "page with no stock.");
            }
        }

        /// <summary>AS3 <c>passed</c> (<c>Script.as:469-472</c>).</summary>
        public void MarkPassed()
        {
            string landId = _currentLandId.Value;
            if (string.IsNullOrEmpty(landId)) return;
            _landStates.MarkPassed(landId);
        }

        /// <summary>
        /// AS3 <c>exit</c> → <c>Game.gotoNextLevel()</c> (<c>Game.as:464-472</c>): clear the checkpoint,
        /// set <c>crea</c>, refill, then re-enter the same land — which regenerates the grid.
        /// </summary>
        public void GotoNextLevel()
        {
            // AS3 goes to `missionId`, not `curLandId` (Game.as:471). They are equal while inside a land,
            // but a trip back to camp changes curLandId and leaves missionId alone — so an `exit` taken
            // after a return still lands in the mission instead of regenerating the camp.
            string landId = !string.IsNullOrEmpty(_missionId) ? _missionId : _currentLandId.Value;
            if (string.IsNullOrEmpty(landId))
            {
                Debug.LogWarning("[CampaignManager] gotoNextLevel with no current land.");
                return;
            }

            // AS3 clears pers.currentCPCode / pers.prevCPCode / land.currentCP here (Game.as:467-468), so
            // the rebuilt land starts with no current checkpoint. It does refill the vendors (Game.as:470)
            // — currently a documented no-op.
            CurrentCheckpoint = null;

            // ...and the rebuilt rooms take the re-pointed buttons with them, exactly as
            // TransitionToLand does for a fresh land: `gotoNextLevel` ends in `gotoLand(missionId)`
            // (Game.as:471), which builds a new Location set.
            ReturnCheckpoint = null;
            RefillVendors();

            // AS3 reaches the next level through `gotoLand(missionId)` (Game.as:471), and `gotoLand`
            // calls `enterToCurLand`, which ends with `if(curLand.upStage) curLand.upStage = false;`
            // (Game.as:398-400). That clear is what lets the NEXT run's `upland` increment again.
            //
            // This is the half the port was missing: `TransitionToLand` clears `upStage` (:217) but
            // `GotoNextLevel` publishes a build request directly and never went through it, so after the
            // first descent `upStage` stayed set and every later `upland` was the "already raised this
            // entry" no-op — the land rebuilt for ever at landStage 1. Nothing went red: a rebuild
            // looks exactly like progress.
            _landStates.ResetUpStage(landId);

            Debug.Log($"[CampaignManager] gotoNextLevel: regenerating '{landId}' at landStage " +
                      $"{_landStates.Get(landId).landStage}.");
            _buildPublisher?.Publish(new LandBuildRequestMessage(landId, null, forceRegenerate: true));
        }

        /// <summary>AS3 <c>gotoland</c> (<c>Script.as:443-456</c>).</summary>
        public void GotoLand(string landId, int n, string coordinates)
        {
            // n == 2 forces a regenerate; n == 1 enters at the "x:y" the action carried; else plain.
            TransitionToLand(landId, n == 1 ? coordinates : null, n == 2);
        }

        // =====================================================================
        //  ITravelMapHost — "the camp can send you somewhere"
        //  (Interact.as:1636-1641, NPC.as:210-216, Game.as:447-462)
        // =====================================================================

        /// <summary>
        /// AS3 <c>land.tip == "base"</c> — the main camp and any other hub. The single predicate the
        /// travel grant is derived from, so a second hub land unlocks travel without touching this file.
        /// </summary>
        public static bool IsBaseLandTip(string tip)
        {
            return string.Equals(tip, "base", StringComparison.OrdinalIgnoreCase);
        }

        public bool TravelUnlocked => _travelUnlocked;

        /// <summary>
        /// How many times the travel map has been asked to open. Incremented by
        /// <see cref="PublishTravelMapOpened"/>.
        ///
        /// <para><b>Telemetry, not gameplay — and it exists because the wall map's effect was otherwise
        /// invisible.</b> Opening the map publishes <see cref="TravelMapOpenedMessage"/>. While that
        /// message had <b>no subscriber</b>, pressing E on the camp's wall map produced no pixel
        /// anywhere, which is indistinguishable from an object whose interaction surface was never
        /// created — and that ambiguity is exactly what made the wall map read as "not interactable"
        /// after its <c>allact='map'</c> handler had already been wired. The subscriber landed
        /// 2026-10-16 (<c>PipBuckBootstrapper.BindTravelMap</c>), so the count is no longer the only
        /// evidence, but the debug overlay still prints it: a play test can tell "the interaction never
        /// fired" (count stays 0) from "it fired and the page did not open" (count rises).</para>
        /// </summary>
        public int TravelMapOpenCount { get; private set; }

        /// <summary>
        /// <c>World.w.loc.base</c> (<c>PipBuck.as:271</c>, <c>:342</c>) — the player is standing in a
        /// hub. The room half of the Oracle's condition is the room's own <c>options.base</c>
        /// (<c>Location.as:439-442</c>); the port carries it as the <i>land's</i>
        /// <c>tip == "base"</c>, which is the granularity the imported data has.
        ///
        /// <para>Public because the PipBuck device has to answer the same question when it opens
        /// (<see cref="UI.PipBuck.PipBuckManager"/>), and a second derivation of it there is exactly
        /// how the two answers drift apart.</para>
        /// </summary>
        public bool IsCurrentLandBase => IsBaseLandTip(CurrentLandTip());

        public void UnlockTravel()
        {
            _travelUnlocked = true;
        }

        /// <summary>
        /// AS3 <c>pip.onoff(3,3)</c>. Re-derives the grant first, because <c>onoff</c> ends in
        /// <c>setButtons()</c> and that is where the <c>base</c>-tip recompute lives
        /// (<c>PipBuck.as:342-345</c>) — see <see cref="ITravelMapHost.OpenTravelMap"/>.
        /// </summary>
        public void OpenTravelMap()
        {
            RefreshTravelUnlockedForCurrentLand();
            PublishTravelMapOpened();
        }

        /// <summary>
        /// AS3 <c>Interact.as:1638-1640</c> / <c>NPC.as:213-215</c> — the three writes
        /// <c>pip.travel = true; pip.onoff(3,3); pip.travel = true;</c>.
        ///
        /// <para><b>Why the display is deferred to the end, which is the one place the port does not
        /// mirror the Oracle line for line.</b> The middle line's effect is
        /// <see cref="RefreshTravelUnlockedForCurrentLand"/> — <c>onoff</c> ends in <c>setButtons()</c>
        /// and that is where the <c>base</c>-tip recompute lives, so it <i>clears</i> the grant outside a
        /// hub — and the third line puts it back. But the middle line is also what <i>shows</i> the page,
        /// and in the port showing the page means publishing
        /// <see cref="TravelMapOpenedMessage"/>, whose subscriber runs <b>synchronously</b>:
        /// <c>PipBuckBootstrapper.BindTravelMap</c> calls <c>PipBuckManager.OnOff(3,3)</c>, which calls
        /// <c>SetPage</c>, which calls the page's <c>SetStatus</c>, which re-reads the source and
        /// <b>captures <c>pip.travel</c> into the page's snapshot</b>. Publishing where the Oracle's
        /// <c>onoff</c> sits therefore froze the page on the <i>disarmed</i> value, and the page's click
        /// gate (<c>PipPageInfo.as:724</c>) then refused every node — the map opened, hovered, drew its
        /// detail pane, and did nothing at all on a click.</para>
        ///
        /// <para>AS3 has the same three writes but no such window: <c>PipPageInfo.itemClick</c> reads
        /// <c>pip.travel</c> <i>live</i> at click time, so it sees the final <c>true</c>. The port's page
        /// reads a snapshot taken while the publish is in flight, so the only way to give it the same
        /// answer is to publish after the last grant. The flag is left unlocked either way, which is what
        /// <see cref="ITravelMapHost.GrantTravelAndOpenMap"/> promises.</para>
        /// </summary>
        public void GrantTravelAndOpenMap()
        {
            UnlockTravel();

            // The middle line's recompute, without its display half.
            RefreshTravelUnlockedForCurrentLand();

            // The third line, which is the one that survives.
            UnlockTravel();

            // ...and only now show the page, so the snapshot it takes is the final value.
            PublishTravelMapOpened();
        }

        /// <summary>
        /// The display half of <c>onoff(3,3)</c>, split out so <see cref="GrantTravelAndOpenMap"/> can
        /// run it <i>after</i> its final grant. See that method for why the order matters.
        /// </summary>
        private void PublishTravelMapOpened()
        {
            TravelMapOpenCount++;

            string landId = _currentLandId.Value;
            Debug.Log($"[CampaignManager] Opening the travel map (in '{landId}', travel unlocked: " +
                      $"{_travelUnlocked}).");
            _travelMapPublisher?.Publish(new TravelMapOpenedMessage(landId));
        }

        /// <summary>
        /// The <c>base</c>-tip half of <c>PipBuck.setButtons</c>: standing in a hub unlocks travel,
        /// standing anywhere else locks it (<c>PipBuck.as:271-273</c> and <c>:342-345</c>).
        ///
        /// <para>Only the land tip is consulted. AS3 also requires <c>!light</c> — the PipBuck torch being
        /// off — and the port has no torch state, so the condition is deliberately not invented here.</para>
        /// </summary>
        public void RefreshTravelUnlockedForCurrentLand()
        {
            _travelUnlocked = IsBaseLandTip(CurrentLandTip());
        }

        private string CurrentLandTip()
        {
            CampaignCatalog catalog = Catalog;
            if (catalog != null)
            {
                LandDefinition def = catalog.GetLand(_currentLandId.Value);
                if (def != null) return def.tip;
            }

            // No catalogue (headless, tests): the tip came in with the transition and was not kept.
            // Falling back to "not a base" is the safe direction — it locks travel rather than granting it.
            return string.Empty;
        }

        /// <summary>
        /// AS3 <c>Game.beginMission</c> (<c>Game.as:447-462</c>) — the world map's confirm button.
        ///
        /// <para>Three rules, all of them load-bearing:</para>
        /// <list type="number">
        /// <item>travelling to the land you are already in does nothing at all (<c>:449-452</c>) — not even
        /// a rebuild;</item>
        /// <item>a mission land sets <c>crea = true</c>, i.e. a fresh layout every time; a <b>base</b> land
        /// does <b>not</b> (<c>:453-460</c>), which is what makes returning to camp cheap and, more
        /// importantly, what stops the camp being regenerated out from under its own contents;</item>
        /// <item>the land must exist in the catalogue for <c>missionId</c> to be recorded — an unknown id
        /// still transitions, it just does not become the mission.</item>
        /// </list>
        /// </summary>
        /// <returns><c>true</c> when a transition was started.</returns>
        public bool BeginMission(string landId)
        {
            if (string.IsNullOrWhiteSpace(landId))
            {
                Debug.LogWarning("[CampaignManager] beginMission with an empty land id.");
                return false;
            }

            // Game.as:449-452 — re-selecting where you already are is a no-op, not a rebuild.
            if (string.Equals(landId, _currentLandId.Value, StringComparison.OrdinalIgnoreCase))
            {
                Debug.Log($"[CampaignManager] beginMission: already in '{landId}'; nothing to do.");
                return false;
            }

            bool forceRegenerate = false;

            CampaignCatalog catalog = Catalog;
            LandDefinition land = catalog?.GetLand(landId);
            if (land != null && !IsBaseLandTip(land.tip))
            {
                _missionId = landId;
                forceRegenerate = true;
            }

            TransitionToLand(landId, null, forceRegenerate);
            return true;
        }

        /// <summary>AS3 <c>Game.missionId</c> — the land <c>gotoNextLevel</c> returns to
        /// (<c>Game.as:471</c>). Null until a mission is begun.</summary>
        public string MissionId => _missionId;

        // =====================================================================
        //  Checkpoints — fe.loc.CheckPoint (CheckPoint.as:184-291)
        //  The port of `pers.currentCP` / `currentCPCode` / `land.act.lastCpCode`. The *rules* are
        //  PFE.Sim.Campaign.CheckpointRules (offline-tested); this is the campaign-side state they write.
        // =====================================================================

        /// <summary>
        /// AS3 <c>Game.baseId</c> (<c>Game.as:11</c>, <c>baseId = "rbl"</c>) — the hub a non-<c>main</c>
        /// checkpoint returns to (<c>CheckPoint.as:252</c>).
        ///
        /// <para>Read from the catalogue so a second hub land does not need this file changed. The fallback
        /// is the oracle's own literal, documented as such — it is the same value
        /// <c>MapBridge.LandIdToCollection</c> hard-codes.</para>
        /// </summary>
        public string BaseLandId
        {
            get
            {
                string hub = Catalog?.playerHubLandId;
                return string.IsNullOrEmpty(hub) ? "rbl" : hub;
            }
        }

        /// <summary>Where a checkpoint is — AS3 <c>pers.currentCP</c> together with <c>currentCPCode</c>.
        /// Room coordinates are the port's land grid, not source pixels.</summary>
        [Serializable]
        public struct CheckpointRecord
        {
            public string landId;
            public int roomX;
            public int roomY;
            public int roomZ;
            public string code;
        }

        /// <summary>
        /// AS3 <c>World.w.pers.currentCP</c> — the checkpoint the player last activated, or <c>null</c>.
        /// Cleared by <see cref="GotoNextLevel"/> exactly as AS3 clears it (<c>Game.as:467-468</c>).
        /// </summary>
        public CheckpointRecord? CurrentCheckpoint { get; private set; }

        /// <summary>
        /// Whether the checkpoint at <paramref name="room"/> <b>is</b> the current one — the port of
        /// <c>Location.as:2065-2067</c>, where a freshly built land re-finds its checkpoint by code and
        /// <c>Land.as:1172-1176</c> then calls <c>activate()</c> on it.
        ///
        /// <para><b>This is what stops a rebuilt land re-healing and re-saving at the same checkpoint.</b>
        /// A code is the identity when there is one; without a code (the exit room's checkpoint,
        /// <c>RoomsProb.as:4724</c>) the room cell is. That is why the empty-code case is not treated as
        /// "matches everything".</para>
        /// </summary>
        public bool IsCurrentCheckpoint(string landId, Vector3Int room, string code)
        {
            return CurrentCheckpoint != null && MatchesCheckpoint(CurrentCheckpoint.Value, landId, room, code);
        }

        /// <summary>
        /// AS3's re-pointed button (<c>CheckPoint.as:236-238</c>): the checkpoint whose interaction
        /// currently offers the return, or <c>null</c>. Set by <see cref="ActivateCheckpoint"/> when the
        /// activation armed it, and cleared whenever a land's rooms are (re)built.
        ///
        /// <para><b>Why this is campaign state and not a field on the presenter.</b> The oracle's button
        /// lives on the <c>CheckPoint</c> object, and AS3 builds <b>every</b> room's <c>Location</c> up
        /// front at land load (<c>Land.as:947</c>, indexed <c>locs[x][y][z]</c>, swapped at
        /// <c>:1263</c>), so the object — and its re-pointed <c>inter.actFun</c> — survives a room change
        /// and dies only with the land. The port builds one <c>RoomObjectVisualManager</c> per room and
        /// destroys its presenters on every room change, so a field there would lose the offer by walking
        /// out of the room and back. <c>CampaignManager</c> is the port's stand-in for "the land is
        /// loaded".</para>
        ///
        /// <para><b>Deliberately not persisted, and not derived from <c>active</c>.</b> Only
        /// <c>act</c>/<c>used</c> are restored on re-entry (<c>CheckPoint.as:110-122</c>), so the oracle
        /// loses the re-pointing too: a checkpoint restored as <c>active == 2</c> has a dead
        /// <c>activate</c> button, not a return. Re-deriving the offer from "is this the current
        /// checkpoint" would hand the player a return the oracle refuses.</para>
        /// </summary>
        public CheckpointRecord? ReturnCheckpoint { get; private set; }

        /// <summary>
        /// Whether the checkpoint at (<paramref name="room"/>) in <paramref name="landId"/> is the one
        /// whose button currently offers the return — the read behind <c>DoorPropPresenter</c>'s press
        /// routing. See <see cref="ReturnCheckpoint"/>.
        /// </summary>
        public bool OffersReturn(string landId, Vector3Int room, string code)
        {
            return ReturnCheckpoint != null && MatchesCheckpoint(ReturnCheckpoint.Value, landId, room, code);
        }

        /// <summary>
        /// Whether a stored checkpoint record names the checkpoint at (<paramref name="room"/>) in
        /// <paramref name="landId"/>.
        ///
        /// <para><b>A code is the identity when either side has one; otherwise the room cell is.</b>
        /// This is the port of <c>Location.as:2065-2067</c>, where a rebuilt land re-finds its checkpoint
        /// by <c>code</c>. The empty-code case must not read as "matches everything": the exit room's
        /// checkpoint carries no code (<c>RoomsProb.as:4724</c>), so a wildcard reading would let one
        /// code-less checkpoint answer for every other one in the land.</para>
        /// </summary>
        private static bool MatchesCheckpoint(CheckpointRecord record, string landId, Vector3Int room, string code)
        {
            if (!string.Equals(record.landId, landId, StringComparison.OrdinalIgnoreCase)) return false;

            if (!string.IsNullOrEmpty(code) || !string.IsNullOrEmpty(record.code))
            {
                return string.Equals(record.code, code, StringComparison.Ordinal);
            }

            return record.roomX == room.x && record.roomY == room.y && record.roomZ == room.z;
        }

        /// <summary>
        /// AS3 <c>CheckPoint.step()</c>'s <c>main</c> branch (<c>CheckPoint.as:300-310</c>) — whether a
        /// <c>main</c> checkpoint's button is live right now, i.e. whether the player is away on a
        /// mission. The rule is <see cref="CheckpointRules.MainIsLive"/>; this is the two catalogue reads
        /// it takes.
        ///
        /// <para>The two reads are kept separate on purpose: <c>missionId</c> naming a land the catalogue
        /// does not have is <b>not</b> the same answer as naming the hub, and only the first one means
        /// "the world is inconsistent". Both take the oracle's <c>else</c>, but collapsing them here
        /// would hide the difference from the rule's own tests.</para>
        /// </summary>
        public bool MainReturnIsLive()
        {
            LandDefinition mission = Catalog?.GetLand(_missionId);
            return CheckpointRules.MainIsLive(
                mission != null,
                mission != null && IsBaseLandTip(mission.tip));
        }

        /// <summary>
        /// AS3 <c>CheckPoint.activate()</c> (<c>CheckPoint.as:184-248</c>) minus the visuals and the
        /// interact bookkeeping, which belong to the presenter.
        ///
        /// <para><b>The save is deliberately not here.</b> AS3's last line is <c>World.w.saveGame()</c>
        /// (<c>CheckPoint.as:247</c>); the port keeps it on the caller so the write happens once, after the
        /// caller has confirmed the activation actually ran, and so this class stays free of a
        /// <c>SaveManager</c> reference.</para>
        ///
        /// <para><b>Not yet wired (reported, not silent):</b> the <c>grantsRestoreBonus</c> half heals
        /// <c>pers.manaCPres</c> and grants <c>pers.xpCPadd * 3</c> (<c>CheckPoint.as:191-201</c>); the port
        /// has no checkpoint-restore seam into the player's stats yet, so the decision is computed and
        /// returned by <see cref="CheckpointRules"/> but not applied here.</para>
        /// </summary>
        /// <para><b>Two entry points, and they are not the same guard.</b> AS3 reaches this by
        /// <c>activate()</c> (the interaction button) or by <c>areaActivate()</c> (walking into the
        /// checkpoint's own area, <c>CheckPoint.as:269-275</c>), and the second one first requires
        /// <c>active == 0</c>. <paramref name="entry"/> selects which, so a checkpoint the player walks
        /// past behaves as the oracle's does rather than as a second copy of the button.</para>
        /// </summary>
        /// <param name="locked">AS3 <c>inter.lock &gt; 0 || inter.mine &gt; 0</c>
        /// (<c>CheckPoint.as:186-189</c>) — a locked or mined checkpoint refuses outright. The rule is
        /// <see cref="CheckpointRules.IsLocked"/>; the caller owns the definition read.</param>
        /// <param name="entry">Which of AS3's two entry points is running.</param>
        /// <returns><c>true</c> when the checkpoint activated — i.e. when the caller should save.</returns>
        public bool ActivateCheckpoint(string landId, Vector3Int room, string code,
            bool isBegin = false, bool teleOn = false, bool main = false, bool locked = false,
            CheckpointEntry entry = CheckpointEntry.Activate)
        {
            string resolved = string.IsNullOrEmpty(landId) ? _currentLandId.Value : landId;
            if (string.IsNullOrEmpty(resolved))
            {
                Debug.LogWarning("[CampaignManager] ActivateCheckpoint with no land id.");
                return false;
            }

            LandRuntimeState state = _landStates.Get(resolved);

            // AS3 reads `active` off the CheckPoint object, which is rebuilt with the land. The port derives
            // it from "is this the checkpoint the player already activated" (IsCurrentCheckpoint, the port of
            // Location.as:2065-2067), then hands the facts to CheckpointRules.Construct, which applies the
            // constructor's `main` normalisation: a main checkpoint is forced to active == 2 (CheckPoint.as:133)
            // and so is refused by activate() — it can only be teleported from, never activated.
            CheckpointFacts facts = CheckpointRules.Construct(new CheckpointFacts
            {
                active = IsCurrentCheckpoint(resolved, room, code)
                    ? CheckpointRules.Activated
                    : CheckpointRules.Fresh,
                isBegin = isBegin,
                teleOn = teleOn,
                main = main,
                locked = locked,
                code = code,
                // AS3 `Game.as:390-397` — `mReturn` is recomputed from the `noreturn` trigger on every
                // land entry and is never stored, so it is derived here the same way rather than read
                // from a field. Note the oracle's field initialiser is `true` (`Game.as:49`), and the
                // trigger only ever turns it OFF: leaving this false by default (as it was) inverts the
                // oracle's default and makes `offersTeleport` permanently false, so no checkpoint ever
                // offers the return-to-camp interaction that sends the player to `baseId`.
                mReturn = !GetTrigger("noreturn").IsGreaterThanZero(),
                // `used` is still not modelled: it is AS3's hardcore one-shot
                // (`World.w.hardInv && World.w.land.rnd`, CheckPoint.as:251-256), which the port has no
                // equivalent of. It stays false rather than being invented — meaning a checkpoint is
                // never consumed by teleporting, which is the permissive direction.
            });

            // AS3 areaActivate() is not activate() — it first requires `active == 0`
            // (CheckPoint.as:271). A checkpoint that is already active, or that has reopened
            // (active == 1), therefore ignores a walk-in but still answers the button. Note the
            // `main` normalisation above has already forced a main checkpoint to active == 2, so it
            // is refused here too, which is how the oracle gets that without testing `main`.
            if (entry == CheckpointEntry.Area && !CheckpointRules.AreaActivates(in facts))
            {
                return false;
            }

            CheckpointActivation result = CheckpointRules.Activate(in facts);
            if (result.refused)
            {
                Debug.Log($"[CampaignManager] checkpoint at ({room.x},{room.y}) in '{resolved}' is " +
                          (main ? "a main checkpoint (already active)" : "already active, or locked") +
                          "; nothing to do — no save.");
                return false;
            }

            CurrentCheckpoint = new CheckpointRecord
            {
                landId = resolved,
                roomX = room.x,
                roomY = room.y,
                roomZ = room.z,
                code = code ?? string.Empty,
            };

            // AS3's `inter.actFun = this.teleport` (CheckPoint.as:236-238) — recorded here, from the
            // same `offersTeleport` the log line below reads, so the button and the report cannot
            // disagree. The `else` branch is `inter.active = false` (:243): a checkpoint that did not arm
            // a return has a dead button, and writing null is what clears an offer left standing by an
            // earlier checkpoint — the port of `deactivate()` when `pers.currentCP` moves on (:321-324).
            ReturnCheckpoint = result.offersTeleport ? CurrentCheckpoint : null;

            // AS3 `if(code)` (CheckPoint.as:210-212) — an empty code leaves lastCpCode untouched.
            if (result.writesCode) state.lastCpCode = code;

            Debug.Log($"[CampaignManager] checkpoint activated in '{resolved}' at ({room.x},{room.y})" +
                      (result.writesCode ? $", code '{code}'" : ", no code") +
                      (result.offersTeleport ? ", teleport offered" : string.Empty) + ".");

            _events?.PublishCheckpointActivated(resolved, room.x, room.y, room.z, code);
            return true;
        }

        /// <summary>
        /// Put back the checkpoint a save recorded — AS3 <c>Pers.load</c>'s <c>currentCPCode</c> read
        /// (<c>Pers.as:591</c>). Called by <c>SaveManager</c> after the campaign block is deserialised.
        /// </summary>
        /// <param name="has">Whether the save carried a checkpoint at all. An older save reads as
        /// <c>false</c>, which must mean "no checkpoint" rather than a checkpoint at (0,0,0).</param>
        public void RestoreCheckpoint(bool has, string landId, int roomX, int roomY, int roomZ, string code)
        {
            if (!has || string.IsNullOrEmpty(landId))
            {
                CurrentCheckpoint = null;
                return;
            }

            CurrentCheckpoint = new CheckpointRecord
            {
                landId = landId,
                roomX = roomX,
                roomY = roomY,
                roomZ = roomZ,
                code = code ?? string.Empty,
            };
        }

        /// <summary>
        /// AS3 <c>CheckPoint.teleport()</c> (<c>CheckPoint.as:250-267</c>). The target rule is
        /// <see cref="CheckpointRules.TeleportTarget"/>; this is the travel.
        ///
        /// <para><b>The caller is the checkpoint's own press</b> — <c>DoorPropPresenter</c> routes a press
        /// here when <see cref="ReturnCheckpoint"/> (or a live <c>main</c> button) says the button is the
        /// return. That routing is what made this method reachable from gameplay; before it, the only
        /// caller was the debug overlay, so a correct target rule produced no travel anywhere.</para>
        ///
        /// <para><b>Not modelled:</b> the hardcore one-shot (<c>:257-261</c>), which spends the checkpoint
        /// and deadens its button when <c>hardInv &amp;&amp; land.rnd</c>. The port has no <c>hardInv</c>, so
        /// <c>used</c> stays false and a return never consumes the checkpoint — the permissive
        /// direction.</para>
        /// </summary>
        /// <returns><c>true</c> when a transition was started.</returns>
        public bool TeleportToCheckpoint(bool main)
        {
            string target = CheckpointRules.TeleportTarget(main, _missionId, BaseLandId);
            if (string.IsNullOrEmpty(target))
            {
                Debug.Log("[CampaignManager] checkpoint teleport is a no-op here " +
                          "(a main checkpoint whose mission is the hub).");
                return false;
            }

            Debug.Log($"[CampaignManager] checkpoint teleport -> '{target}' ({(main ? "main" : "hub")}).");
            // GotoLand -> TransitionToLand, which drops the return offer: the destination builds its own
            // rooms, and the oracle's re-pointed button does not survive that.
            GotoLand(target, 0, null);
            return true;
        }

        /// <summary>The story trigger table, for the save payload. AS3 <c>World.w.game.triggers</c>
        /// (<c>Game.as:25</c>). Values are <see cref="TriggerValue"/> because the oracle stores strings
        /// as well as ints — see that type's docs.</summary>
        public IReadOnlyDictionary<string, TriggerValue> Triggers => _triggers;

        /// <summary>
        /// Restore the trigger table from a save. Deliberately not a loop over
        /// <see cref="SetTrigger(string,int)"/>: that logs one line per trigger, which is a wall of noise
        /// on load and would read as gameplay activity in the editor log. Nor does it raise the
        /// <c>TriggerChanged</c> event — a load is not gameplay progress.
        /// </summary>
        public void LoadTriggers(IEnumerable<KeyValuePair<string, TriggerValue>> pairs)
        {
            _triggers.Clear();
            if (pairs == null) return;

            foreach (KeyValuePair<string, TriggerValue> pair in pairs)
            {
                if (string.IsNullOrEmpty(pair.Key)) continue;
                _triggers[pair.Key] = pair.Value;
            }
        }

        /// <summary>
        /// The quests currently in progress (AS3 <c>state == 1</c>). Read-only: the overlay's Campaign
        /// Vars tab needs the list to draw states, and writing to it from outside would bypass the
        /// chain-on-complete rule <see cref="CompleteQuest"/> owns.
        /// </summary>
        public IReadOnlyCollection<string> ActiveQuestIds => QuestIdsInState(QuestState.Active);

        /// <summary>Quests that are done (AS3 <c>state == 2</c>). Read-only, as
        /// <see cref="ActiveQuestIds"/>.</summary>
        public IReadOnlyCollection<string> CompletedQuestIds => QuestIdsInState(QuestState.Done);

        /// <summary>
        /// Every quest the campaign knows about, in any state. The overlay needs the whole set to show a
        /// quest parked at state 0 — <c>addQuest(noVis:true)</c> puts one there deliberately
        /// (<c>Game.as:562-565</c>), and a list of only the active ones would hide it.
        /// </summary>
        public IReadOnlyCollection<string> KnownQuestIds
        {
            get
            {
                var ids = new List<string>();
                foreach (KeyValuePair<string, QuestRuntimeState> pair in _questRuntime) ids.Add(pair.Key);
                return ids;
            }
        }

        /// <summary>
        /// Project a quest's runtime state for a reader. Returns <c>false</c> for a quest the campaign has
        /// never been told about — a different answer from "known but not started".
        /// </summary>
        public bool TryGetQuestState(string questId, out int state, out int doneSubs, out int totalSubs)
        {
            state = 0;
            doneSubs = 0;
            totalSubs = 0;

            if (string.IsNullOrEmpty(questId) || !_questRuntime.TryGetValue(questId, out QuestRuntimeState runtime))
            {
                return false;
            }

            state = runtime.State;
            for (int i = 0; i < runtime.Subs.Count; i++)
            {
                if (runtime.Subs[i] == null) continue;
                totalSubs++;
                if (runtime.Subs[i].State >= (int)QuestState.Done) doneSubs++;
            }

            return true;
        }

        private List<string> QuestIdsInState(QuestState state)
        {
            var ids = new List<string>();
            foreach (KeyValuePair<string, QuestRuntimeState> pair in _questRuntime)
            {
                if (pair.Value != null && pair.Value.State == (int)state) ids.Add(pair.Key);
            }

            return ids;
        }

        /// <summary>
        /// Read a trigger. An absent key reads as <see cref="TriggerValue.Unset"/> — which the oracle
        /// distinguishes from a stored zero (<c>Land.as:821</c> tests <c>== null</c>;
        /// <c>Area.as:360</c> tests <c>!= 1</c>). Numeric callers use
        /// <c>.AsInt(0)</c> / <c>.IsGreaterThanZero()</c>, which implement AS3's coercing comparison.
        /// </summary>
        public TriggerValue GetTrigger(string triggerName)
        {
            if (string.IsNullOrEmpty(triggerName)) return TriggerValue.Unset;
            return _triggers.TryGetValue(triggerName, out TriggerValue value) ? value : TriggerValue.Unset;
        }

        /// <summary>AS3 <c>setTrigger(name, value = 1)</c> (<c>Game.as:676-679</c>) — the numeric arm.</summary>
        public void SetTrigger(string triggerName, int value = 1)
        {
            SetTrigger(triggerName, TriggerValue.FromInt(value));
        }

        /// <summary>
        /// Drop a trigger entirely, so a later read returns <see cref="TriggerValue.Unset">unset</see>
        /// rather than a stored zero.
        ///
        /// <para><b>Debug-only, and deliberately so.</b> The oracle has no unset — <c>setTrigger</c>
        /// writes verbatim (<c>Game.as:676-679</c>) and no AS3 code path deletes a key — so gameplay must
        /// never need this. It exists for the F2 Campaign Vars tab, where removing a trigger written by a
        /// test run is the only way to get back to the "never happened" state an absent key means.</para>
        /// </summary>
        /// <returns><c>true</c> when a key was actually there, so a caller can report from what the
        /// seam really did rather than from the intent.</returns>
        public bool RemoveTrigger(string triggerName)
        {
            if (string.IsNullOrEmpty(triggerName)) return false;
            if (_triggers.Remove(triggerName))
            {
                Debug.Log($"[CampaignManager] Trigger '{triggerName}' removed (unset).");
                return true;
            }

            return false;
        }

        /// <summary>
        /// Set a trigger to any value, including a string. AS3 writes the value <b>verbatim</b>
        /// (<c>Game.as:678</c>), so this is the overload that makes <c>"wait"</c>
        /// (<c>NPC.as:591</c>) and a quest <c>@set</c> value (<c>Quest.as:332</c>) storable.
        /// </summary>
        public void SetTrigger(string triggerName, TriggerValue value)
        {
            if (string.IsNullOrEmpty(triggerName)) return;

            // The old value is read before the write so the event can carry both. The oracle has no
            // "was" notion — it reads the table — but a hook that fires on "the trigger became set"
            // cannot be written without it.
            TriggerValue previous = GetTrigger(triggerName);
            _triggers[triggerName] = value;
            Debug.Log($"[CampaignManager] Trigger '{triggerName}' set to {value}.");

            // `LoadTriggers` deliberately does not raise this: a load is not gameplay progress, and a
            // save with 200 triggers would fire 200 events through a hook that expects a story beat.
            _events?.PublishTriggerChanged(triggerName, previous, value);
        }

        /// <summary>
        /// The NPC half of the travel grant: <c>NPC.activate()</c> with <c>npcInter == "travel"</c>
        /// (<c>NPC.as:210-216</c>) runs the same three steps as the camp's wall map.
        ///
        /// <para>The NPC system itself is unported — nothing walks up to an NPC and calls this yet. It is
        /// here so the <i>rule</i> is stated and tested in one place, rather than being re-derived by
        /// whoever writes that system.</para>
        /// </summary>
        /// <returns><c>true</c> when the interaction was a travel grant.</returns>
        public bool ActivateNpc(string interactionType)
        {
            if (!IsTravelNpcInteraction(interactionType)) return false;

            GrantTravelAndOpenMap();
            return true;
        }

        /// <summary>AS3 <c>npcInter == "travel"</c> (<c>NPC.as:212</c>).</summary>
        public static bool IsTravelNpcInteraction(string interactionType)
        {
            return string.Equals(interactionType, "travel", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The mutable runtime state of a quest — AS3's <c>game.quests[id]</c>.
        ///
        /// <para><b>Why a live reference rather than a projection.</b> The quest journal needs per-sub
        /// <c>est</c>, <c>gived</c> and <c>invis</c> (<c>PipPage.as:1324-1367</c>), and
        /// <see cref="TryGetQuestState"/> only counts. Reading the object is safe because every writer
        /// goes through <see cref="AddQuest"/> / <see cref="CloseQuest"/> / <see cref="CompleteQuest"/>,
        /// which are the rules that keep <c>isClosed</c> and <c>check</c> apart. A caller that mutated
        /// this directly would bypass all of them.</para>
        /// </summary>
        public bool TryGetQuestRuntime(string questId, out QuestRuntimeState runtime)
        {
            runtime = null;
            if (string.IsNullOrEmpty(questId)) return false;
            return _questRuntime.TryGetValue(questId, out runtime) && runtime != null;
        }

        public bool IsQuestActive(string questId)
        {
            return !string.IsNullOrEmpty(questId)
                && _questRuntime.TryGetValue(questId, out QuestRuntimeState runtime)
                && runtime.State == (int)QuestState.Active;
        }

        public bool IsQuestCompleted(string questId)
        {
            return !string.IsNullOrEmpty(questId)
                && _questRuntime.TryGetValue(questId, out QuestRuntimeState runtime)
                && runtime.State == (int)QuestState.Done;
        }

        /// <summary>
        /// How many of a quest's sub-objectives are done. This is the honest reading of the field the old
        /// <c>GetQuestStage</c> exposed: the oracle has no stage cursor, only a set of finished subs.
        /// </summary>
        public int GetQuestStage(string questId)
        {
            if (string.IsNullOrEmpty(questId) || !_questRuntime.TryGetValue(questId, out QuestRuntimeState runtime))
            {
                return 0;
            }

            int done = 0;
            for (int i = 0; i < runtime.Subs.Count; i++)
            {
                if (runtime.Subs[i] != null && runtime.Subs[i].State >= (int)QuestState.Done) done++;
            }

            return done;
        }

        /// <summary>
        /// AS3 <c>Game.addQuest(id)</c> — the quest's own name for what the old API called "start".
        /// Kept as a named alias because the interface and the F2 overlay both speak it.
        /// </summary>
        public void StartQuest(string questId)
        {
            AddQuest(questId);
        }

        /// <summary>
        /// <b>A shim, and it does not mean what it says.</b> The old model stored a monotonic index and
        /// this wrote it. The oracle has no index: <c>stage val n</c> means "mark sub <c>n</c> complete"
        /// (<c>Game.closeQuest</c> → <c>Quest.closeSub</c>). So this forwards to
        /// <see cref="CloseQuest"/> with <paramref name="nextStage"/> rendered as the sub id — which is
        /// what every caller actually wanted, including the F2 tab's "advance" button.
        ///
        /// <para>Passing 0 or a negative value completes the <b>whole</b> quest, matching
        /// <c>Game.as:624-627</c>. That is the honest reading, and the reason this is documented rather
        /// than quietly reimplemented.</para>
        /// </summary>
        public void AdvanceQuestStage(string questId, int nextStage)
        {
            CloseQuest(questId, nextStage.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        /// <summary>AS3 <c>Quest.close()</c> by way of <c>Game.closeQuest(id)</c> with no <c>n</c>.</summary>
        public void CompleteQuest(string questId)
        {
            CloseQuest(questId, null);
        }

        // =====================================================================
        //  Quests — fe.loc.Quest (Quest.as) and Game.addQuest / showQuest / closeQuest
        //  (Game.as:527-632). The rules live in QuestRules (Unity-free, offline-tested);
        //  this is the state they operate on and the seams the side effects go through.
        // =====================================================================

        /// <summary>
        /// The scripting seam's <c>quest</c> command (AS3 <c>Script.as:408-423</c>).
        ///
        /// <para><b>Explicit, because the two signatures genuinely differ.</b> The public
        /// <see cref="AddQuest"/> returns the runtime state and takes the <c>noVis</c> park flag, both of
        /// which the script vocabulary has no use for — a script says <c>quest val</c> and nothing else.
        /// Narrowing the seam here rather than widening the public method keeps "what a script can do" a
        /// separate, smaller question from "what the campaign can do", which is the whole point of
        /// <see cref="IScriptQuestHost"/>.</para>
        /// </summary>
        void IScriptQuestHost.AddQuest(string questId) => AddQuest(questId);

        /// <summary>
        /// AS3 <c>Game.addQuest(id, loadObj, noVis, snd, showDial)</c> (<c>Game.as:527-581</c>).
        ///
        /// <para>Three cases, and the port previously had only one of them: a quest the campaign already
        /// knows is returned (promoting it from state 0 to 1 if it was parked); a quest it does not know
        /// is looked up in the catalogue and instantiated; an unknown id is reported and returns
        /// <c>null</c> — AS3 traces <c>"не найден квест"</c> (<c>:556</c>).</para>
        /// </summary>
        /// <param name="noVis">AS3 <c>noVis</c> — park the quest at state 0 when it is not <c>auto</c>.
        /// Used by <c>showQuest</c>/<c>closeQuest</c>, which grant a quest in order to act on it without
        /// announcing it.</param>
        /// <returns>The quest's runtime state, or <c>null</c> when the id is not in the catalogue.</returns>
        public QuestRuntimeState AddQuest(string questId, bool noVis = false)
        {
            if (string.IsNullOrEmpty(questId)) return null;
            QuestFacts facts = GetQuestFacts(questId);

            if (_questRuntime.TryGetValue(questId, out QuestRuntimeState existing))
            {
                // `if(this.quests[id].state == 0) { state = 1; … isClosed(); deposit(); }` (:539-550)
                if (QuestRules.Activate(existing))
                {
                    Debug.Log($"[CampaignManager] Quest '{questId}' is now in progress.");
                    PublishQuestStateChanged(existing, (int)QuestState.NotStarted, (int)QuestState.Active);

                    // A quest whose subs are already satisfied closes the moment it is granted — that is
                    // how a "collect 3" objective completed before the quest was given resolves at once.
                    if (QuestRules.IsClosed(existing)) CloseQuest(questId, null);
                }

                return existing;
            }

            if (facts == null)
            {
                Debug.LogWarning($"[CampaignManager] addQuest: '{questId}' is not in the catalogue, so no " +
                                 "quest was granted. (AS3 traces 'не найден квест' here.)");
                return null;
            }

            // AS3 constructs the Quest at state 1 (:109) and only parks it when `noVis && !auto`
            // (:562-565).
            QuestState initial = noVis && !facts.Auto ? QuestState.NotStarted : QuestState.Active;
            QuestRuntimeState runtime = QuestRuntimeState.CreateFrom(facts, initial);
            _questRuntime[questId] = runtime;

            Debug.Log($"[CampaignManager] Quest '{questId}' granted (state {runtime.State}, " +
                      $"{runtime.Subs.Count} sub-objective(s)).");

            if (runtime.State == (int)QuestState.Active)
            {
                PublishQuestStateChanged(runtime, (int)QuestState.NotStarted, (int)QuestState.Active);
                if (QuestRules.IsClosed(runtime)) CloseQuest(questId, null);
            }

            return runtime;
        }

        /// <summary>
        /// AS3 <c>Game.showQuest(id, n)</c> (<c>Game.as:583-611</c>) — the <c>showstage</c> script command.
        /// Grants the quest if it is unknown, then un-hides sub <paramref name="n"/>.
        /// </summary>
        public void ShowQuest(string questId, string n)
        {
            if (string.IsNullOrEmpty(questId)) return;

            QuestRuntimeState runtime = _questRuntime.TryGetValue(questId, out QuestRuntimeState found)
                ? found
                : AddQuest(questId, noVis: true);

            if (runtime == null)
            {
                Debug.LogWarning($"[CampaignManager] showQuest: '{questId}' could not be granted, so sub " +
                                 $"'{n}' was not revealed.");
                return;
            }

            QuestFacts facts = GetQuestFacts(questId);
            if (QuestRules.ShowQuest(facts, runtime, n))
            {
                Debug.Log($"[CampaignManager] Quest '{questId}' revealed sub-objective '{n}'.");
            }
        }

        /// <summary>
        /// AS3 <c>Game.closeQuest(id, n)</c> (<c>Game.as:613-632</c>) — the <c>stage</c> script command.
        ///
        /// <para><b><paramref name="n"/> absent, empty or negative completes the WHOLE quest</b>
        /// (<c>:624-627</c>). That is not an edge case: an authored <c>&lt;s act="stage" val="x"/&gt;</c>
        /// carries the string <c>"-1"</c> because <c>analiz</c> declares it so (<c>Script.as:71</c>), so a
        /// bare <c>stage</c> means "finish it".</para>
        /// </summary>
        public void CloseQuest(string questId, string n)
        {
            if (string.IsNullOrEmpty(questId)) return;

            QuestRuntimeState runtime = _questRuntime.TryGetValue(questId, out QuestRuntimeState found)
                ? found
                : AddQuest(questId, noVis: true);

            if (runtime == null)
            {
                Debug.LogWarning($"[CampaignManager] closeQuest: '{questId}' could not be granted, so " +
                                 $"nothing closed (n='{n ?? "-"}').");
                return;
            }

            QuestFacts facts = GetQuestFacts(questId);
            int previousState = runtime.State;

            QuestCloseOutcome outcome = QuestRules.CloseQuest(facts, runtime, n);
            if (!outcome.Closed)
            {
                return;
            }

            ApplyQuestClose(outcome, previousState);
        }

        /// <summary>
        /// AS3 <c>Game.incQuests(itemId, delta)</c> (<c>Game.as:653-664</c>) — every in-progress quest that
        /// counts items gets the delta, then re-checks.
        /// </summary>
        /// <param name="countOf">How many of an item the inventory holds, for the re-check.</param>
        public void IncQuests(string itemId, int delta, Func<string, int> countOf = null)
        {
            if (string.IsNullOrEmpty(itemId)) return;

            foreach (KeyValuePair<string, QuestRuntimeState> pair in SnapshotQuestsInState(QuestState.Active))
            {
                QuestFacts facts = GetQuestFacts(pair.Key);
                if (facts == null || !facts.IsCheck) continue;

                QuestRules.Inc(facts, pair.Value, itemId, delta);
                QuestRules.Check(facts, pair.Value, countOf, itemId);

                // `Check` closes the parent from inside itself when every required sub is done
                // (Quest.as:426), so this must NOT call CloseParent again — the quest is already at
                // state 2 and that would early-return an empty outcome, dropping the payout. The
                // literal that used to stand here did drop the `<reward>` and `del` halves for exactly
                // that reason; ParentPayout is the same projection CloseParent uses.
                if (pair.Value.State == (int)QuestState.Done)
                {
                    ApplyQuestClose(QuestRules.ParentPayout(facts), (int)QuestState.Active);
                }
            }
        }

        /// <summary>
        /// AS3 <c>Game.checkQuests(itemId)</c> (<c>Game.as:634-651</c>) — recompute every item-counting
        /// quest from the inventory.
        /// </summary>
        public void CheckQuests(Func<string, int> countOf, string changedItem = null)
        {
            foreach (KeyValuePair<string, QuestRuntimeState> pair in SnapshotQuestsInState(QuestState.Active))
            {
                QuestFacts facts = GetQuestFacts(pair.Key);
                if (facts == null || !facts.IsCheck) continue;

                int before = pair.Value.State;
                QuestRules.Check(facts, pair.Value, countOf, changedItem);
                if (before != (int)QuestState.Done && pair.Value.State == (int)QuestState.Done)
                {
                    // Same reasoning as IncQuests above: `Check` already closed the parent, so the
                    // payout has to be projected rather than produced by a second CloseParent.
                    ApplyQuestClose(QuestRules.ParentPayout(facts), before);
                }
            }
        }

        /// <summary>
        /// The side effects of a close, through the campaign's own seams.
        ///
        /// <para><b>What is applied and what is not.</b> XP/SP/reputation go to <c>IQuestRewardHost</c>
        /// when one is wired; the trigger goes through <see cref="SetTrigger(string, TriggerValue)"/>; the
        /// chained quest through <see cref="AddQuest"/>. The inventory halves — the <c>del</c> withdrawal
        /// (<c>Quest.as:613-643</c>) and the <c>&lt;reward&gt;</c> grants (<c>:661-689</c>) — are
        /// <b>reported, not skipped silently</b>, because the port has no "an item changed" notification
        /// for them to hang off. That is the one external dependency of this item, and a silent no-op here
        /// would read as "the quest paid nothing".</para>
        /// </summary>
        private void ApplyQuestClose(QuestCloseOutcome outcome, int previousState)
        {
            QuestRuntimeState runtime = outcome.QuestId != null
                && _questRuntime.TryGetValue(outcome.QuestId, out QuestRuntimeState found)
                ? found
                : null;

            if (outcome.IsParent)
            {
                Debug.Log($"[CampaignManager] Quest '{outcome.QuestId}' completed " +
                          $"(xp {outcome.Xp}, sp {outcome.Sp}, rep {outcome.Rep}).");
            }
            else
            {
                Debug.Log($"[CampaignManager] Quest '{outcome.QuestId}' closed sub-objective " +
                          $"'{outcome.SubId}'.");
            }

            // ── AS3 close() order, reproduced ────────────────────────────────────────────────────
            // del withdrawals (:613-643) → xp/sp/rep (:645-656) → trigger (:657-660) →
            // <reward> items and triggers (:661-689). The order is observable when a `del` stage
            // collects the very item a `<reward>` row grants, and when a reward's trigger is read by
            // a dialogue the same close starts — so it is copied rather than tidied.

            // The `del` withdrawal. Only a parent close produces these (Quest.as:613 `if(!this.sub)`),
            // which is why the list is empty on a sub-only outcome.
            if (outcome.Withdrawals != null && outcome.Withdrawals.Count > 0)
            {
                if (_questRewards != null)
                {
                    _questRewards.WithdrawQuestItems(outcome.QuestId, outcome.Withdrawals);
                }
                else
                {
                    Debug.LogWarning(
                        $"[CampaignManager] Quest '{outcome.QuestId}' withdraws " +
                        $"{outcome.Withdrawals.Count} collected item stack(s) on close, but no " +
                        "IQuestRewardHost is wired, so they were left in the inventory. AS3 removes " +
                        "them from Quest.close (Quest.as:613-643).");
                }
            }

            if (outcome.Xp != 0 || outcome.Sp != 0 || outcome.Rep != 0)
            {
                if (_questRewards != null)
                {
                    _questRewards.GrantQuestRewards(outcome.QuestId, outcome.Xp, outcome.Sp, outcome.Rep);
                }
                else
                {
                    Debug.LogWarning(
                        $"[CampaignManager] Quest '{outcome.QuestId}' owes {outcome.Xp} xp, " +
                        $"{outcome.Sp} sp and {outcome.Rep} reputation, but no IQuestRewardHost is wired, " +
                        "so the payout was dropped. AS3 pays it from Quest.close (Quest.as:645-656).");
                }
            }

            if (!string.IsNullOrEmpty(outcome.Trigger))
            {
                SetTrigger(outcome.Trigger, TriggerValue.FromString(outcome.TriggerSet ?? "1"));
            }

            // The `<reward>` children (Quest.as:661-689). A row can carry an item id, a trigger, or
            // both — the Oracle's loop is two consecutive `if(@x.length())` tests, so each half is
            // applied independently and a trigger-only row must not be mistaken for an empty one.
            ApplyQuestRewards(outcome.QuestId, outcome.Rewards);

            PublishQuestStateChanged(runtime, previousState,
                runtime?.State ?? previousState, outcome.IsParent ? null : outcome.SubId);

            // AS3 `if(this.endScript != null) World.w.game.runScript(this.endScript);` (:704-707).
            if (!string.IsNullOrEmpty(outcome.EndScript))
            {
                _questHooks?.RunAuthoredScript(outcome.EndScript);
            }

            if (!string.IsNullOrEmpty(outcome.NextQuestId))
            {
                AddQuest(outcome.NextQuestId);
            }
        }

        /// <summary>
        /// Applies the <c>&lt;reward&gt;</c> rows of a close (<c>Quest.as:661-689</c>).
        ///
        /// <para><b>The two halves are split by owner, not by row.</b> The trigger half is written here
        /// because the trigger table is this class's; the item half is handed to
        /// <see cref="IQuestRewardHost"/> because the inventory is not. Splitting <i>within</i> a row is
        /// what the Oracle does — two consecutive independent <c>if</c>s — so a row carrying both does
        /// both and a row carrying neither is inert.</para>
        /// </summary>
        private void ApplyQuestRewards(string questId, List<QuestReward> rewards)
        {
            if (rewards == null || rewards.Count == 0) return;

            List<QuestReward> itemRows = null;

            for (int i = 0; i < rewards.Count; i++)
            {
                QuestReward reward = rewards[i];

                // `if(_loc2_.@trigger.length())` (:677-687) — `@set` absent means the literal 1.
                if (!string.IsNullOrEmpty(reward.Trigger))
                {
                    SetTrigger(reward.Trigger, TriggerValue.FromString(
                        string.IsNullOrEmpty(reward.Set) ? "1" : reward.Set));
                }

                if (string.IsNullOrEmpty(reward.ItemId)) continue;
                (itemRows ??= new List<QuestReward>()).Add(reward);
            }

            if (itemRows == null) return;

            if (_questRewards != null)
            {
                _questRewards.GrantQuestItems(questId, itemRows);
            }
            else
            {
                Debug.LogWarning(
                    $"[CampaignManager] Quest '{questId}' grants {itemRows.Count} reward item row(s), " +
                    "but no IQuestRewardHost is wired, so they were dropped. AS3 hands them to " +
                    "World.w.invent.take (Quest.as:661-676).");
            }
        }

        /// <summary>A copy of the quests in one state, so a close during the walk cannot mutate the
        /// collection being enumerated (the failure shape lesson #78 records for the overlay).</summary>
        private List<KeyValuePair<string, QuestRuntimeState>> SnapshotQuestsInState(QuestState state)
        {
            var snapshot = new List<KeyValuePair<string, QuestRuntimeState>>();
            foreach (KeyValuePair<string, QuestRuntimeState> pair in _questRuntime)
            {
                if (pair.Value != null && pair.Value.State == (int)state) snapshot.Add(pair);
            }

            return snapshot;
        }

        private void PublishQuestStateChanged(QuestRuntimeState runtime, int oldState, int newState, string stage = null)
        {
            if (runtime == null) return;
            _events?.PublishQuestStateChanged(runtime.QuestId, oldState, newState, stage);
        }

        /// <summary>
        /// The authored definition, projected into the Unity-free facts the rules run on, cached per id.
        /// A quest that is not in the catalogue reads as <c>null</c> — reported by the caller, never
        /// invented.
        /// </summary>
        public QuestFacts GetQuestFacts(string questId)
        {
            if (string.IsNullOrEmpty(questId)) return null;
            if (_questFacts.TryGetValue(questId, out QuestFacts cached)) return cached;

            QuestDefinition definition = Catalog?.GetQuest(questId);
            if (definition == null) return null;

            QuestFacts facts = ProjectQuest(definition);
            _questFacts[questId] = facts;
            return facts;
        }

        /// <summary>
        /// Project a <see cref="QuestDefinition"/> onto <see cref="QuestFacts"/>.
        ///
        /// <para><b>The sub id is the stage index rendered as a string, and that is not a liberty.</b> The
        /// oracle's <c>&lt;q id="…"&gt;</c> is a string and <c>closeSub</c> keys on it verbatim
        /// (<c>Quest.as:579</c>); the importer mapped that id onto <c>stageIndex</c> (an <c>int</c>). Every
        /// shipped quest uses 1-based sequential ids — verified against the 58 imported assets, where
        /// <c>toExit</c> has exactly one stage at index 1 and an authored <c>&lt;s act="stage" val="toExit"
        /// n="1"/&gt;</c> closes it. A quest whose authored ids were not sequential would need the importer
        /// to carry the raw id; that is recorded rather than papered over here.</para>
        /// </summary>
        public static QuestFacts ProjectQuest(QuestDefinition definition)
        {
            if (definition == null) return null;

            var facts = new QuestFacts
            {
                QuestId = definition.questId ?? "",
                Title = string.IsNullOrEmpty(definition.questTitle) ? definition.questId : definition.questTitle,
                Empl = definition.employer ?? "",
                Xp = definition.rewardXp,
                Sp = definition.rewardSp,
                Rep = definition.rewardReputation,
                IsMain = definition.isMainQuest,
                NextQuestId = definition.nextQuestId ?? "",
                // The four close-side scalars. `TriggerSet` keeps its "1" default when the authored
                // `@triggerset` is absent — the Oracle writes that literal at Quest.as:214, and an
                // empty string here would make SetTrigger write a trigger with no value.
                Trigger = definition.trigger ?? "",
                TriggerSet = string.IsNullOrEmpty(definition.triggerSet) ? "1" : definition.triggerSet,
                EndDial = definition.endDialogueId ?? "",
                BegDial = definition.beginDialogueId ?? "",
            };

            if (definition.rewards != null)
            {
                for (int i = 0; i < definition.rewards.Count; i++)
                {
                    QuestRewardDefinition reward = definition.rewards[i];
                    if (reward == null) continue;

                    facts.Rewards.Add(new QuestReward
                    {
                        ItemId = reward.itemId ?? "",
                        Count = reward.count <= 0 ? 1 : reward.count,
                        Trigger = reward.trigger ?? "",
                        // `if(_loc2_.@set.length()) … else triggers[…] = 1` (Quest.as:679-686).
                        Set = string.IsNullOrEmpty(reward.set) ? "1" : reward.set,
                    });
                }
            }

            if (definition.stages != null)
            {
                for (int i = 0; i < definition.stages.Count; i++)
                {
                    QuestStageDefinition stage = definition.stages[i];
                    if (stage == null) continue;

                    facts.Subs.Add(new QuestSubFacts
                    {
                        SubId = stage.stageIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        Optional = stage.isOptional,
                        Hidden = stage.isHidden,
                        Invis = stage.isInvisible,
                        Collect = stage.requiredItemId ?? "",
                        Kol = stage.requiredItemCount <= 0 ? 1 : stage.requiredItemCount,
                        Give = stage.turnInItemId ?? "",
                        // `del` / `coltip` / `result` — the three sub attributes that had fields on
                        // QuestSubFacts and no writer, so the close-side withdrawal and the whole
                        // result-reveal pass were unreachable. `isResult` on the PARENT is re-derived
                        // below, matching `this.par.result = true` (Quest.as:188-191).
                        DeleteOnClose = stage.deleteOnClose,
                        ColTip = stage.collectTip,
                        IsResult = stage.isResult,
                    });

                    if (!string.IsNullOrEmpty(stage.requiredItemId)) facts.IsCheck = true;
                }
            }

            return facts;
        }
    }
}
