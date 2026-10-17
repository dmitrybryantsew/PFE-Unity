using System;
using System.Collections.Generic;
using System.Xml.Linq;
using UnityEngine;
using PFE.Data.Definitions;
using PFE.Data.Definitions.Campaign;
using PFE.Entities.Player;
using PFE.Systems.Campaign;
using PFE.Systems.Map;
using PFE.Systems.Map.Minimap;
using PFE.Systems.Physics;

namespace PFE.UI.PipBuck
{
    /// <summary>
    /// Reads page 3 out of the live world — the port of the five data-gathering branches of
    /// <c>PipPageInfo.setSubPages</c> (<c>:78-292</c>).
    ///
    /// <para><b>Every owner is a <see cref="Func{TResult}"/>, and that is deliberate.</b> The page's data
    /// lives on four different objects — the <c>CampaignManager</c>, the <c>PlayerController</c>, the
    /// live <c>LandMap</c> and the <c>AllData</c> document — and none of them exists when the PipBuck is
    /// built: the player is assembled in <c>PlayerController.Awake</c> and the campaign manager is
    /// resolved from a scene search, neither of which Unity orders against the bootstrapper's
    /// <c>Start</c>. A constructor that captured instances would bind null once and then show five empty
    /// sub-tabs forever, which is the failure <c>HudBootstrapper</c> documents. Resolving per read means
    /// a campaign that appears a second later simply appears on the page.</para>
    ///
    /// <para><b>What the port cannot read produces nothing, never a zero.</b> The three readers whose
    /// absence is meaningful — the trigger table, the skill table and the <c>frag_</c> counters — return
    /// the "unknown" answers <see cref="PipInfoSnapshot"/> documents (0, 0 and -1), so a unit with no
    /// counter shows no count rather than a count of zero, and a quest the campaign has never heard of
    /// reads as unknown rather than as "not started".</para>
    /// </summary>
    public sealed class PlayerPipInfoSource : IPipInfoSource
    {
        private readonly Func<CampaignManager> _campaign;
        private readonly Func<PlayerController> _player;

        /// <summary>Cached across reads so a rebuild does not allocate a fresh snapshot. Cleared at the
        /// start of every <see cref="Read"/> rather than appended to — a stale entry would show a quest
        /// the player no longer has.</summary>
        private readonly PipInfoSnapshot _snapshot = new PipInfoSnapshot();

        /// <summary>Parsed once and kept: <c>AllData</c> is a build-time document and does not change.</summary>
        private static XElement _allDataRoot;
        private static bool _allDataAttempted;

        public PlayerPipInfoSource(Func<CampaignManager> campaign, Func<PlayerController> player)
        {
            _campaign = campaign;
            _player = player;
        }

        // ── Optional providers ────────────────────────────────────────────────
        //
        // Left null until the owning system exists, and each null has a stated default rather than a
        // guess. Same arrangement, and the same reason, as PlayerPipStatSource's providers.

        /// <summary>
        /// The live <c>LandMap</c> — <c>MapBridge.CurrentLandMap</c>. Null disables sub-tab 1, which is
        /// the honest answer before a land has been built: <c>World.w.land</c> does not exist yet.
        /// </summary>
        public Func<LandMap> LandMapProvider { get; set; }

        /// <summary><c>gg.pers.persName</c>. Null leaves <c>&amp;lp</c> substituted with nothing, which
        /// is what the Oracle shows before a name has been given.</summary>
        public Func<string> PlayerNameProvider { get; set; }

        /// <summary>
        /// <c>World.w.drawAllMap</c> — the console's reveal-everything flag. Null reads false, which is
        /// the Oracle's own default.
        /// </summary>
        public Func<bool> RevealAllMapProvider { get; set; }

        /// <summary>
        /// <c>World.w.testMode</c> — opens the travel map and relaxes every gate. Null reads false, which
        /// is the shipped game's value.
        /// </summary>
        public Func<bool> TestModeProvider { get; set; }

        /// <summary>
        /// <c>World.w.helpMess</c> — the hint-marker master switch. Null reads false.
        ///
        /// <para><b>False is a choice with a visible consequence.</b> With it off no travel node carries
        /// the "new land" marker. The Oracle sets it from the options screen, which the port has no page
        /// for yet (page 5 is a stub), so there is nothing that can turn it on — and defaulting it to
        /// true would put a marker on every reachable land in a way the player could not disable.</para>
        /// </summary>
        public Func<bool> HelpMessProvider { get; set; }

        /// <summary>
        /// <c>World.w.pers.speedShtr</c> — how badly the player's shooting is impaired, 0..3. Null reads
        /// 0, which suppresses all four of the travel pane's warning lines.
        /// </summary>
        public Func<int> SpeedShooterProvider { get; set; }

        /// <summary>
        /// <c>World.w.game.triggers[name]</c>, coerced to a number, for the travel ladder and the vendor
        /// gate — the absent-answer here is 0. Null makes every trigger read 0, which refuses every
        /// trigger-gated vendor task.
        /// </summary>
        public Func<string, int> TriggerProvider { get; set; }

        /// <summary>
        /// The bestiary's kill count — the same trigger table as <see cref="TriggerProvider"/> but with
        /// the OPPOSITE absent-answer, and that is why it is its own provider rather than a reuse of the
        /// one above.
        ///
        /// <para>The Oracle's guard is <c>triggers["frag_"+id] &gt;= 0</c> (<c>PipPageInfo.as:269</c>),
        /// and <c>undefined &gt;= 0</c> is false, so "absent" and "stored 0" are different answers there.
        /// The travel ladder wants absent to read 0; the bestiary wants it to read negative, which hides
        /// the count — wiring one <see cref="Func{T,U}"/> into both slots would print a count of zero on
        /// every creature the player has never met. Return a negative number for absent;
        /// <see cref="PipInfoSnapshot.FragCount"/> documents the contract.</para>
        /// </summary>
        public Func<string, int> FragCountProvider { get; set; }

        /// <summary>
        /// <c>World.w.pers.skill[id]</c>. Null reads 0, which refuses every skill-gated vendor task.
        /// </summary>
        public Func<string, int> SkillProvider { get; set; }

        /// <summary>
        /// The campaign's note journal. Null falls back to <see cref="CampaignNotes.Current"/>, and a
        /// campaign that has never seeded one shows an empty journal rather than throwing.
        /// </summary>
        public CampaignNotes Notes { get; set; }

        // ── Reading ───────────────────────────────────────────────────────────

        public PipInfoSnapshot Read()
        {
            CampaignManager campaign = _campaign?.Invoke();
            PlayerController player = _player?.Invoke();

            Reset();

            ReadQuests(campaign);
            ReadVendorTasks(campaign);
            ReadNotes();
            ReadLands(campaign);
            ReadPlayer(player);
            ReadBestiary();
            ReadMinimap();

            _snapshot.TriggerOf = TriggerProvider;
            _snapshot.SkillOf = SkillProvider;
            _snapshot.FragCountOf = FragCountProvider;
            _snapshot.QuestStateOf = QuestStateOf(campaign);

            return _snapshot;
        }

        /// <summary>
        /// Clears every list. The three delegate fields are deliberately left alone — they are re-assigned
        /// at the end of every read, and nulling them here would make a mid-read exception leave the
        /// snapshot in a state that is neither the old one nor the new one.
        /// </summary>
        private void Reset()
        {
            _snapshot.Quests.Clear();
            _snapshot.VendorTasks.Clear();
            _snapshot.Notes.Clear();
            _snapshot.Lands.Clear();
            _snapshot.AllDataRoot = _allDataRoot;
            _snapshot.Map = new PipMinimapFacts();
            _snapshot.PlayerName = "";
            _snapshot.PlayerLevel = 0;
            _snapshot.InBaseLand = false;
            _snapshot.TravelUnlocked = false;
            _snapshot.TestMode = false;
            _snapshot.HelpMess = false;
            _snapshot.SpeedShooter = 0;
        }

        private void ReadQuests(CampaignManager campaign)
        {
            if (campaign == null) return;

            // The Oracle iterates `game.quests`, which holds every quest the campaign has *ever* created,
            // in creation order. The port's registry is keyed by id, so the order comes from the runtime
            // state objects the manager exposes; the journal's own sort then decides what the player
            // sees, which is why the order here only matters as a tie-break.
            IReadOnlyList<string> known = KnownQuestIds(campaign);
            for (int i = 0; i < known.Count; i++)
            {
                string questId = known[i];
                if (string.IsNullOrEmpty(questId)) continue;

                QuestFacts facts = campaign.GetQuestFacts(questId);
                if (facts == null) continue;

                QuestRuntimeState state = campaign.TryGetQuestRuntime(questId, out QuestRuntimeState runtime)
                    ? runtime
                    : null;

                if (state == null) continue;

                _snapshot.Quests.Add(new PipQuestEntry(facts, state));
            }
        }

        /// <summary>
        /// Every quest id the campaign knows, in a stable order. <c>KnownQuestIds</c> is the manager's own
        /// union of active and completed ids; it is sorted so that two reads of the same campaign produce
        /// the same input order and the journal's stable sort is reproducible.
        /// </summary>
        private static IReadOnlyList<string> KnownQuestIds(CampaignManager campaign)
        {
            var ids = new List<string>(campaign.KnownQuestIds);
            ids.Sort(StringComparer.Ordinal);
            return ids;
        }

        private void ReadVendorTasks(CampaignManager campaign)
        {
            if (campaign?.Catalog == null) return;

            IReadOnlyList<VendorDefinition> vendors = campaign.Catalog.AllVendors;
            if (vendors == null) return;

            // `GameData.d.vendor.task` is a flat table in the Oracle — every vendor's rows in one list,
            // and `checkQuest` sees all of them. The port keeps them per vendor, so the page's copy is
            // the concatenation. Deduplicated by task id: the same quest can be offered by two vendors,
            // and the Oracle's table would hold it twice, but `TasksToGrant` would then grant it twice
            // and `addQuest` would refuse the second — a difference worth removing rather than
            // reproducing.
            var seen = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < vendors.Count; i++)
            {
                VendorDefinition vendor = vendors[i];
                if (vendor?.tasks == null) continue;

                for (int j = 0; j < vendor.tasks.Count; j++)
                {
                    VendorTaskEntry task = vendor.tasks[j];
                    if (task == null || string.IsNullOrEmpty(task.taskId)) continue;
                    if (!seen.Add(task.taskId)) continue;

                    _snapshot.VendorTasks.Add(new PipVendorTaskFacts
                    {
                        TaskId = task.taskId,
                        LandId = task.land ?? "",
                        Trigger = task.trigger ?? "",
                        Skill = task.skill ?? "",
                        SkillLevel = task.skillLevel,

                        // See PipVendorTaskFacts.HasSkillLevel: the Oracle gates on both attributes
                        // being present, and an absent `skill` already skips the arm.
                        HasSkillLevel = !string.IsNullOrEmpty(task.skill),

                        Manual = task.manual == "1",
                    });
                }
            }
        }

        private void ReadNotes()
        {
            CampaignNotes notes = Notes ?? CampaignNotes.Current;
            if (notes == null) return;

            IReadOnlyList<string> ids = notes.Ids;
            for (int i = 0; i < ids.Count; i++)
            {
                if (!string.IsNullOrEmpty(ids[i])) _snapshot.Notes.Add(ids[i]);
            }
        }

        private void ReadLands(CampaignManager campaign)
        {
            CampaignCatalog catalog = campaign?.Catalog;
            if (catalog == null) return;

            TravelMapModel travel = TravelMapModel.FromCatalog(
                catalog,
                campaign.LandStates,
                TriggerProvider,
                () => campaign.CurrentLandId?.CurrentValue,
                TestModeProvider != null && TestModeProvider());

            _snapshot.TestMode = TestModeProvider != null && TestModeProvider();
            _snapshot.HelpMess = HelpMessProvider != null && HelpMessProvider();
            _snapshot.TravelUnlocked = campaign.TravelUnlocked;

            IReadOnlyList<LandDefinition> definitions = catalog.AllLands;
            if (definitions == null) return;

            for (int i = 0; i < definitions.Count; i++)
            {
                LandDefinition definition = definitions[i];
                if (definition == null || string.IsNullOrEmpty(definition.landId)) continue;

                LandRuntimeState state = campaign.LandStates.Get(definition.landId);

                _snapshot.Lands.Add(new PipLandFacts
                {
                    LandId = definition.landId,
                    IsProb = definition.isProbLand,
                    IsTest = definition.isTestLand,
                    Visited = state != null && state.visited,
                    Access = state != null && state.access,
                    Passed = state != null && state.passed,
                    Difficulty = definition.baseDifficulty,
                    Tip = definition.tip ?? "",
                    Loaded = definition.roomTemplates != null && definition.roomTemplates.Count > 0,
                    LandStage = state != null ? state.landStage : 0,
                    AllProbCount = definition.probRooms != null ? definition.probRooms.Count : 0,
                    ClosedProbCount = 0,   // see the remarks below
                    CanTravel = CanTravel(travel, definition.landId),
                });
            }
        }

        /// <summary>
        /// <c>game.checkTravel(id)</c> for one land, through the port's own ladder.
        ///
        /// <para><b>A land the model does not hold is not travelable.</b> That is the Oracle's own
        /// answer — <c>checkTravel</c> reads <c>game.lands[id]</c> and returns false for an id it does
        /// not know — and it is why <c>grave</c>, which has no map node, is refused outright
        /// (<c>Game.as:485</c>).</para>
        /// </summary>
        private static bool CanTravel(TravelMapModel travel, string landId)
        {
            if (travel == null || string.IsNullOrEmpty(landId)) return false;

            IReadOnlyList<TravelLand> lands = travel.Lands;
            for (int i = 0; i < lands.Count; i++)
            {
                if (string.Equals(lands[i].LandId, landId, StringComparison.Ordinal))
                {
                    return travel.CheckTravel(lands[i]);
                }
            }

            return false;
        }

        private void ReadPlayer(PlayerController player)
        {
            _snapshot.PlayerName = PlayerNameProvider?.Invoke() ?? "";

            if (player != null)
            {
                var stats = player.GetComponent<PFE.Systems.RPG.CharacterStats>();
                if (stats != null) _snapshot.PlayerLevel = stats.Level;
            }

            _snapshot.SpeedShooter = SpeedShooterProvider?.Invoke() ?? 0;

            CampaignManager campaign = _campaign?.Invoke();
            _snapshot.InBaseLand = IsBaseLand(campaign);
        }

        /// <summary>
        /// <c>World.w.loc.base</c> — the player is standing in a base land. Read from the land's
        /// <c>tip</c>, through the manager's own predicate, so the page and the campaign cannot disagree
        /// about which lands count.
        /// </summary>
        private static bool IsBaseLand(CampaignManager campaign)
        {
            if (campaign == null) return false;

            LandDefinition land = campaign.CurrentLand?.CurrentValue;
            return land != null && CampaignManager.IsBaseLandTip(land.tip);
        }

        private void ReadBestiary()
        {
            _snapshot.AllDataRoot = AllDataRoot();
        }

        /// <summary>
        /// The parsed <c>&lt;all&gt;</c> document, loaded once per process.
        ///
        /// <para><b>Why the raw document and not the typed assets.</b> <c>infoUnit</c> asks whether an
        /// attribute <i>exists</i>; see <see cref="PipInfoSnapshot.AllDataRoot"/>. The load is guarded
        /// because it is an engine ECall: offline, or in a host without the asset, it must mean "no
        /// bestiary" rather than "the page cannot be built".</para>
        /// </summary>
        private static XElement AllDataRoot()
        {
            if (_allDataAttempted) return _allDataRoot;
            _allDataAttempted = true;

            try
            {
                TextAsset asset = Resources.Load<TextAsset>(PFE.Systems.Loot.LootRuntime.AllDataResourcePath);
                if (asset == null)
                {
                    Debug.LogWarning("[PlayerPipInfoSource] no AllData TextAsset; sub-tab 5 (Enemies) will " +
                                     "list nothing. Ship AllData.as as a .txt under Resources/Loot/.");
                    return null;
                }

                AllDataXml.Outcome outcome = AllDataXml.TryRead(asset.text, out XElement root, out _, out string error);
                if (outcome != AllDataXml.Outcome.Ok)
                {
                    Debug.LogWarning($"[PlayerPipInfoSource] AllData could not be parsed ({outcome}: {error}); " +
                                     "sub-tab 5 (Enemies) will list nothing.");
                    return null;
                }

                _allDataRoot = root;
            }
            catch (Exception)
            {
                // No engine here. Deliberately silent, for the reason CampaignManager.Catalog's own
                // catch gives: the caller's "no bestiary" report is the useful one.
                return null;
            }

            return _allDataRoot;
        }

        private void ReadMinimap()
        {
            LandMap map = LandMapProvider?.Invoke();
            var facts = new PipMinimapFacts
            {
                RevealAll = RevealAllMapProvider != null && RevealAllMapProvider(),
            };

            // `if(World.w.loc.noMap)` — there is no map at all for this location. That is the Oracle's
            // own `Location.noMap`, and the port's equivalent is a current room the composer refuses.
            facts.NoMap = map == null || map.currentRoom == null;

            if (!facts.NoMap)
            {
                MinimapPlayerMarker marker = ResolvePlayerMarker(_player?.Invoke());
                facts.Marker = marker;

                if (LandMinimapComposer.TryBuild(
                        map, facts.RevealAll, marker, out LandMinimapBuffer buffer, out LandMinimapStats stats))
                {
                    facts.Buffer = buffer;
                    facts.Stats = stats;
                }
                else
                {
                    // A map with no positioned rooms is "nothing to draw" — the Oracle's own `drawMap`
                    // returns an empty bitmap and the page then shows no map.
                    facts.NoMap = true;
                }
            }

            _snapshot.Map = facts;
        }

        /// <summary>
        /// The player's cell and room-local pixels — the same reading
        /// <c>PlayerDebugEditorOverlay.ResolvePlayerMarker</c> uses, so the page's marker and the F2
        /// overlay cannot disagree about where the player's feet are.
        /// </summary>
        private static MinimapPlayerMarker ResolvePlayerMarker(PlayerController player)
        {
            if (player == null) return MinimapPlayerMarker.None;

            var physics = player.GetComponent<TilePhysicsController>();
            if (physics == null) return MinimapPlayerMarker.None;

            Vector2 worldPixels = physics.PixelPosition;
            return new MinimapPlayerMarker(
                true,
                WorldCoordinates.WorldToLand(worldPixels),
                WorldCoordinates.WorldToLocal(worldPixels));
        }

        /// <summary>
        /// <c>game.quests[id].state</c>, or a <b>negative</b> number when the campaign has never heard of
        /// the quest — see <see cref="PipInfoSnapshot.QuestStateOf"/>.
        /// </summary>
        private static Func<string, int> QuestStateOf(CampaignManager campaign)
        {
            if (campaign == null) return null;

            return questId =>
            {
                if (string.IsNullOrEmpty(questId)) return -1;
                if (!campaign.TryGetQuestRuntime(questId, out QuestRuntimeState runtime)) return -1;
                return runtime != null ? runtime.State : -1;
            };
        }
    }
}
