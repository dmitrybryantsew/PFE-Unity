using System;
using System.Collections.Generic;
using UnityEngine;
using R3;
using MessagePipe;
using VContainer.Unity;
using PFE.Core.Messages;
using PFE.Data.Definitions.Campaign;
using PFE.Systems.Map.Scripting;
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
    public class CampaignManager : ICampaignManager, ILandScriptHost, ITravelMapHost, IInitializable, IDisposable
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
        private readonly Dictionary<string, int> _triggers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Quest progress
        private readonly Dictionary<string, int> _questStages = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _completedQuests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _activeQuests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public CampaignManager(
            ISubscriber<LandTransitionMessage> transitionSubscriber = null,
            IPublisher<LandTransitionMessage> transitionPublisher = null,
            CampaignCatalog catalog = null,
            IPublisher<LandBuildRequestMessage> buildPublisher = null,
            IPublisher<TravelMapOpenedMessage> travelMapPublisher = null)
        {
            _transitionSubscriber = transitionSubscriber;
            _transitionPublisher = transitionPublisher;
            _buildPublisher = buildPublisher;
            _travelMapPublisher = travelMapPublisher;

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

            // Arriving somewhere re-derives the travel grant: the camp unlocks it, anywhere else locks it
            // (PipBuck.as:271-273 / :342-345).
            RefreshTravelUnlockedForCurrentLand();

            _buildPublisher?.Publish(new LandBuildRequestMessage(resolvedId, spawnPoint, forceRegenerate));
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

        /// <summary>AS3 <c>refill</c> → <c>Land.refill()</c> (<c>Land.as:1481-1496</c>).</summary>
        public void RefillVendors()
        {
            // Blocked on a vendor runtime: VendorInventory exists but nothing instantiates one per land
            // (03_GAP_LEDGER.md §2, `refillVendors`). Deliberately a no-op rather than a silent half-fix.
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
            RefillVendors();

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
        /// <see cref="OpenTravelMap"/>.
        ///
        /// <para><b>Telemetry, not gameplay — and it exists because the wall map's effect is otherwise
        /// invisible.</b> Opening the map publishes <see cref="TravelMapOpenedMessage"/>, and that
        /// message has <b>no subscriber</b>: the travel-map page is the one piece of the loop that has
        /// not been built. So pressing E on the camp's wall map produces no pixel anywhere, which is
        /// indistinguishable from an object whose interaction surface was never created — and that
        /// ambiguity is exactly what made the wall map read as "not interactable" after its
        /// <c>allact='map'</c> handler had already been wired. The debug overlay prints this, so a play
        /// test can tell "the interaction never fired" (count stays 0) from "it fired and drew
        /// nothing" (count rises).</para>
        /// </summary>
        public int TravelMapOpenCount { get; private set; }

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
            TravelMapOpenCount++;

            string landId = _currentLandId.Value;
            Debug.Log($"[CampaignManager] Opening the travel map (in '{landId}', travel unlocked: " +
                      $"{_travelUnlocked}).");
            _travelMapPublisher?.Publish(new TravelMapOpenedMessage(landId));
        }

        public void GrantTravelAndOpenMap()
        {
            // Interact.as:1638-1640 / NPC.as:213-215, all three lines, in order. The trailing grant is
            // load-bearing: OpenTravelMap recomputes from the current land and clears it outside a base.
            UnlockTravel();
            OpenTravelMap();
            UnlockTravel();
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
            if (CurrentCheckpoint == null) return false;

            CheckpointRecord current = CurrentCheckpoint.Value;
            if (!string.Equals(current.landId, landId, StringComparison.OrdinalIgnoreCase)) return false;

            if (!string.IsNullOrEmpty(code) || !string.IsNullOrEmpty(current.code))
            {
                return string.Equals(current.code, code, StringComparison.Ordinal);
            }

            return current.roomX == room.x && current.roomY == room.y && current.roomZ == room.z;
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
                // used / mReturn are not modelled yet: the port has no hardcore one-shot and no
                // `mReturn` toggle, so both stay false rather than being invented.
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

            // AS3 `if(code)` (CheckPoint.as:210-212) — an empty code leaves lastCpCode untouched.
            if (result.writesCode) state.lastCpCode = code;

            Debug.Log($"[CampaignManager] checkpoint activated in '{resolved}' at ({room.x},{room.y})" +
                      (result.writesCode ? $", code '{code}'" : ", no code") +
                      (result.offersTeleport ? ", teleport offered" : string.Empty) + ".");
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
            GotoLand(target, 0, null);
            return true;
        }

        /// <summary>The story trigger table, for the save payload. AS3 <c>World.w.game.triggers</c>
        /// (<c>Game.as:25</c>).</summary>
        public IReadOnlyDictionary<string, int> Triggers => _triggers;

        /// <summary>
        /// Restore the trigger table from a save. Deliberately not a loop over
        /// <see cref="SetTrigger"/>: that logs one line per trigger, which is a wall of noise on load and
        /// would read as gameplay activity in the editor log.
        /// </summary>
        public void LoadTriggers(IEnumerable<KeyValuePair<string, int>> pairs)
        {
            _triggers.Clear();
            if (pairs == null) return;

            foreach (KeyValuePair<string, int> pair in pairs)
            {
                if (string.IsNullOrEmpty(pair.Key)) continue;
                _triggers[pair.Key] = pair.Value;
            }
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

        public int GetTrigger(string triggerName)
        {
            if (string.IsNullOrEmpty(triggerName)) return 0;
            return _triggers.TryGetValue(triggerName, out int val) ? val : 0;
        }

        public void SetTrigger(string triggerName, int value = 1)
        {
            if (string.IsNullOrEmpty(triggerName)) return;
            _triggers[triggerName] = value;
            Debug.Log($"[CampaignManager] Trigger '{triggerName}' set to {value}.");
        }

        public bool IsQuestActive(string questId)
        {
            return !string.IsNullOrEmpty(questId) && _activeQuests.Contains(questId);
        }

        public bool IsQuestCompleted(string questId)
        {
            return !string.IsNullOrEmpty(questId) && _completedQuests.Contains(questId);
        }

        public int GetQuestStage(string questId)
        {
            if (string.IsNullOrEmpty(questId)) return 0;
            return _questStages.TryGetValue(questId, out int stage) ? stage : 0;
        }

        public void StartQuest(string questId)
        {
            if (string.IsNullOrEmpty(questId)) return;
            _activeQuests.Add(questId);
            if (!_questStages.ContainsKey(questId))
            {
                _questStages[questId] = 1;
            }
            Debug.Log($"[CampaignManager] Started quest '{questId}'.");
        }

        public void AdvanceQuestStage(string questId, int nextStage)
        {
            if (string.IsNullOrEmpty(questId)) return;
            _activeQuests.Add(questId);
            _questStages[questId] = nextStage;
            Debug.Log($"[CampaignManager] Quest '{questId}' advanced to stage {nextStage}.");
        }

        public void CompleteQuest(string questId)
        {
            if (string.IsNullOrEmpty(questId)) return;
            _activeQuests.Remove(questId);
            _completedQuests.Add(questId);

            Debug.Log($"[CampaignManager] Quest '{questId}' completed.");

            // Check if there is a chained next quest
            CampaignCatalog catalog = Catalog;
            if (catalog != null)
            {
                var questDef = catalog.GetQuest(questId);
                if (questDef != null && !string.IsNullOrEmpty(questDef.nextQuestId))
                {
                    StartQuest(questDef.nextQuestId);
                }
            }
        }
    }
}
