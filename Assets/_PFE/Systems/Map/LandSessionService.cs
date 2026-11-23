using System;
using System.Collections.Generic;
using UnityEngine;
using PFE.Data.Definitions.Campaign;
using PFE.Sim.Campaign;
using PFE.Systems.Campaign;
using PFE.Systems.Map.Serialization;

namespace PFE.Systems.Map
{
    /// <summary>
    /// The authoritative half of a land transition — every <b>decision</b> and every
    /// <b>campaign-state write</b> AS3's <c>Game.enterLand</c> / <c>Land.enterLand</c> make, with no
    /// scene choreography, no rendering and no coroutines.
    ///
    /// <para><b>Why this exists (Wave 2, thread G).</b> <c>MapBridge</c> used to own the whole
    /// transition: the entry-cell resolution, the run-difficulty read, the checkpoint resume, the
    /// <c>visited</c>/<c>firstroom</c> campaign writes — interleaved with visual initialization,
    /// camera setup and frame yields. That coupling is what made a land transition impossible to
    /// execute in an offline fixture and what would force a headless host to drag the render layer
    /// into the simulation graph. The split puts the decision chain here, behind one constructible
    /// object, so the ordering rules (read <c>rnd</c>/<c>visited</c> BEFORE choosing the entry room,
    /// write <c>visited</c> AFTER the build) become assertable without a scene. <c>MapBridge</c>
    /// keeps only the coroutine orchestration and the scene-object plumbing.</para>
    ///
    /// <para><b>Instance, not static, and registered as a VContainer singleton.</b> The methods are
    /// stateless today, but the service is the seam where authoritative room presence (the
    /// multiplayer <c>ActiveRoomSet</c>) will live; a static rules class would have to be un-staticed
    /// again at that point. Fixtures construct it with a bare <c>new</c>.</para>
    ///
    /// <para><b>The campaign is passed in, never reached for.</b> Every method takes the
    /// <see cref="CampaignManager"/> it reads or writes as a parameter; the ambient
    /// <c>CampaignManager.Current</c> lookup stays at the choreography layer (<c>MapBridge</c>).
    /// A null campaign is a legal argument (headless boot, fixtures), and each method documents
    /// what the null case means rather than throwing.</para>
    /// </summary>
    public sealed class LandSessionService
    {
        /// <summary>
        /// Land id -> room collection, transcribed from the <c>&lt;land&gt;</c> table in <c>GameData.as</c>.
        ///
        /// A collection is keyed by a land's <c>file</c> attribute — <c>&lt;land id='rbl' file='rooms_rbl'&gt;</c>
        /// — because that is also the AS3 field name and the key <c>Rooms.as:2794-2822</c> registers the land
        /// under, and the key <c>AS3LandDefaultsDatabase</c> merges its inherited options by. The id alone
        /// cannot be turned into it: <c>random_mbase</c> is <c>rooms_mbase</c>, not <c>rooms_random_mbase</c>,
        /// and <c>stable_pi_surf</c> is <c>rooms_pis</c>. Note also that the camp is <c>rbl</c> — there is no
        /// land called "camp" in the oracle.
        /// </summary>
        private static readonly Dictionary<string, string> LandIdToCollection =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "test", "rooms" },
                { "test2", "rooms2" },
                { "begin", "rooms_begin" },
                { "surf", "rooms_surf" },
                { "rbl", "rooms_rbl" },
                { "covert", "rooms_covert" },
                { "src", "rooms_src" },
                { "nio", "rooms_nio" },
                { "raiders", "rooms_raiders" },
                { "core", "rooms_core" },
                { "mtn", "rooms_mtn" },
                { "minst", "rooms_minst" },
                { "garages", "rooms_garages" },
                { "way", "rooms_way" },
                { "workshop", "rooms_workshop" },
                { "hql", "rooms_hql" },
                { "post", "rooms_post" },
                { "comm", "rooms_comm" },
                { "art", "rooms_art" },
                { "thunder", "rooms_thunder" },
                { "grave", "rooms_grave" },
                { "random_plant", "rooms_plant" },
                { "random_sewer", "rooms_sewer" },
                { "random_stable", "rooms_stable" },
                { "random_mane", "rooms_mane" },
                { "random_canter", "rooms_canter" },
                { "random_mbase", "rooms_mbase" },
                { "random_encl", "rooms_encl" },
                { "bunker", "rooms_mbase" },
                { "stable_pi", "rooms_pi" },
                { "stable_pi_atk", "rooms_pi" },
                { "stable_pi_surf", "rooms_pis" },
                { "prob", "rooms_prob" }
            };

        /// <summary>
        /// The collection name a land id (or an already-resolved collection id) builds from. Pure;
        /// an unknown id logs one warning and returns <see cref="string.Empty"/> rather than
        /// inventing a name — the previous fallback capitalised the id, which produced "Nio",
        /// "Covert", "Src" — collections that do not exist.
        /// </summary>
        public string ResolveCollectionName(string targetLand)
        {
            if (string.IsNullOrWhiteSpace(targetLand))
            {
                return string.Empty;
            }

            string id = targetLand.Trim();
            if (LandIdToCollection.TryGetValue(id, out string collection))
            {
                return collection;
            }

            // A caller may already hold the collection id — it is what RoomTemplate.sourceCollectionId
            // stores, so a round-tripped value arrives in that form rather than as a land id.
            if (id.StartsWith("rooms", StringComparison.OrdinalIgnoreCase))
            {
                return id;
            }

            // Do not guess. The caller reports the empty collection as "no templates found" and this
            // warning is the hint that the name was not declared in GameData.as.
            Debug.LogWarning(
                $"[LandSession] Unknown land id '{targetLand}': no <land> entry in GameData.as declares it.");
            return string.Empty;
        }

        /// <summary>
        /// Whether an authored build of this collection would place every room on one land coordinate.
        ///
        /// <para>This is the fingerprint of a procedural land's template collection: AS3 generates those rooms
        /// rather than placing them, so the importer gives every one of them <c>fixedPosition (0,0,0)</c>
        /// (<c>WorldBuilder.HasFixedPosition</c> accepts them all, because it only tests <c>z &gt;= 0</c>).
        /// Building such a collection authored yields a one-cell land with no neighbour to transition to —
        /// the player is sealed in.</para>
        ///
        /// <para>Requires at least two templates that claim a position, so a genuinely single-room authored
        /// land is not caught. <c>z</c> is the "has a position" axis, matching <c>WorldBuilder</c>.</para>
        /// </summary>
        public bool TemplatesCollapseToASingleCell(List<RoomTemplate> templates)
        {
            if (templates == null || templates.Count < 2) return false;

            bool seen = false;
            Vector3Int first = default;

            for (int i = 0; i < templates.Count; i++)
            {
                RoomTemplate template = templates[i];
                if (template == null || template.fixedPosition.z < 0) continue;

                if (!seen)
                {
                    seen = true;
                    first = template.fixedPosition;
                    continue;
                }

                if (template.fixedPosition != first) return false;
            }

            return seen;
        }

        /// <summary>
        /// The cell a land is entered at when the caller named no room and no checkpoint applies —
        /// AS3 <c>Land.enterLand</c>'s else-branch: <c>locX = act.begLocX; locY = act.begLocY; locZ = 0</c>
        /// (<c>Land.as:1178-1181</c>). <c>begLocX/begLocY</c> is the land's own <c>locx</c>/<c>locy</c>
        /// (<c>LandAct.as:157</c>), stored as <see cref="LandDefinition.entryCoordinates"/>.
        ///
        /// <para><b>Why this is a method and not an expression at the call site.</b> It used to be
        /// <see cref="ResolveEntrancePosition"/> unconditionally, and that heuristic is what put every
        /// <c>nio</c> entry into the boss arena: it takes the first template with a type-0 spawn point, and
        /// <c>rooms_nio</c>'s first such template is <c>bossraider</c> at (-1,0,<b>1</b>) — the Z=1 layer of
        /// the same cell as the entrance corridor <c>room_-1_0</c> (-1,0,0). The player arrived in the boss
        /// room from both the plant exit and the land map, then had to walk out through the boss door to
        /// reach the room they should have started in. The oracle has no such ambiguity: z is forced to 0
        /// and the cell comes from the land, never from whichever room happens to own a spawn point.</para>
        ///
        /// <para><b>Pure, and public, so it is assertable offline</b> — the same reason
        /// <see cref="WorldBuilder.ResolveProceduralEntry"/> is. The surrounding transition needs a live
        /// scene; this decision does not, and it is the one that was wrong.</para>
        /// </summary>
        /// <param name="land">The resolved land definition, or null when the catalogue did not have it.</param>
        /// <param name="templates">The collection being built — read only when <paramref name="land"/> is null.</param>
        /// <param name="collectionName">The collection key, for the one hardcoded fallback inside the heuristic.</param>
        public Vector3Int ResolveDefaultEntryCell(
            LandDefinition land, List<RoomTemplate> templates, string collectionName)
        {
            if (land != null)
            {
                return new Vector3Int(land.entryCoordinates.x, land.entryCoordinates.y, 0);
            }

            return ResolveEntrancePosition(templates, collectionName);
        }

        /// <summary>
        /// The entry cell for a land the catalogue could <b>not</b> resolve — the last-resort guess.
        ///
        /// <para><b>Not the normal path.</b> AS3 enters every land at <c>act.begLocX/begLocY</c>
        /// (<c>Land.as:1178-1181</c>), i.e. the land's own <c>locx</c>/<c>locy</c>, with <c>locZ = 0</c>.
        /// That value is <see cref="LandDefinition.entryCoordinates"/> and the caller uses it directly. This
        /// method exists only for a land with no definition, where there is no <c>locx/locy</c> to read.</para>
        ///
        /// <para><b>Why the second clause is dangerous.</b> "First template with a type-0 spawn point" has no
        /// notion of the Z axis, so it can name a <i>layer</i> room. It did exactly that for <c>nio</c>: the
        /// first spawned template in the collection is <c>bossraider</c> at (-1,0,<b>1</b>), so every entry
        /// spawned the player in the boss arena. Keep it as a fallback, never as the authored entry rule.</para>
        /// </summary>
        private static Vector3Int ResolveEntrancePosition(List<RoomTemplate> templates, string collectionName)
        {
            if (string.Equals(collectionName, "rooms_surf", StringComparison.OrdinalIgnoreCase))
            {
                // The outside bunker exit room in rooms_surf is room_0_1 at (0, 1, 0)
                return new Vector3Int(0, 1, 0);
            }

            // Look for beg0 or first spawn point
            var entry = templates.Find(t => t.type == "beg0")
                     ?? templates.Find(t => t.spawnPoints != null && t.spawnPoints.Exists(sp => sp.type == 0))
                     ?? templates.Find(t => t.fixedPosition.x >= 0);

            return entry != null ? entry.fixedPosition : Vector3Int.zero;
        }

        /// <summary>
        /// AS3 <c>Game.as:347-350</c> plus <c>Land.enterLand</c>'s three-way entry choice
        /// (<c>Land.as:1143-1186</c>), computed in the oracle's order and nowhere else.
        ///
        /// <para><b>The order is the rule.</b> <c>rnd</c> and <c>visited</c> decide both the run
        /// difficulty and the resume room, so they are read BEFORE the starting room is chosen —
        /// moving these reads after <c>startPos</c> is exactly how the resume branch used to be
        /// unreachable. The precedence (named room &gt; checkpoint &gt; land entry cell) is asserted
        /// offline in <see cref="CheckpointRules.ResolveEntryRoomSource"/> rather than left to the
        /// shape of the switch below it.</para>
        ///
        /// <para><b>Side-effect free.</b> The two campaign WRITES this decision enables
        /// (<c>visited</c>, <c>firstroom</c>) are deliberately NOT made here — they must land after
        /// the build, and are exposed as <see cref="CompleteLandEntry"/>. Reading and writing are
        /// separate so the read order above can never be polluted by a write that ran too early.</para>
        /// </summary>
        /// <param name="targetLand">The land being entered, as the campaign knows it.</param>
        /// <param name="templates">The land's room collection — already fetched by the caller, who
        /// needs it for the empty-collection refusal before this plan is built.</param>
        /// <param name="campaign">The campaign; null is legal (headless, fixtures) and yields the
        /// authored reading with run difficulty 1.</param>
        /// <param name="playerLevel"><c>pers.level</c>; the oracle defaults it to 1 when no player
        /// exists, which makes <c>param3</c> 1 rather than a special case.</param>
        /// <param name="targetPosition">The caller-named room ("x:y"), or null.</param>
        public LandEntryPlan BuildEntryPlan(
            string targetLand,
            List<RoomTemplate> templates,
            CampaignManager campaign,
            int playerLevel,
            Vector3Int? targetPosition)
        {
            CampaignCatalog catalog = campaign != null ? campaign.Catalog : null;
            LandDefinition land = catalog != null ? catalog.GetLand(targetLand) : null;

            // AS3 Game.as:347-350 computes `_loc1_` — the `param1` enterLand receives — from exactly these
            // two facts. An unresolved land (`land == null`) takes the authored reading, which is the same
            // conservative choice the authored fallback below already makes for it.
            bool landIsRandom = land != null && land.isProcedural;
            bool landVisited = campaign != null && campaign.LandStates.Get(targetLand).visited;
            bool firstVisit = CheckpointRules.IsFirstVisit(landIsRandom, landVisited);

            // AS3 `Game.enterToCurLand` (Game.as:353-361) — `param3`, the RUN difficulty this land is built
            // at. It is a function of the player's LEVEL and a persistent trigger, NOT of the difficulty
            // setting: `param3 = 0` until a land has been entered, then `max(1, pers.level - 1)`.
            //
            // Read here and not inside `WorldBuilder`, because this is the one place in the port that holds
            // the campaign (the trigger table) and the player (the level) at the moment a land becomes
            // current. `WorldBuilder` is constructed outside the container and can reach neither.
            int runDifficulty = DifficultyRules.RunDifficulty(
                firstRoomSeen: campaign != null && campaign.GetTrigger("firstroom").IsGreaterThanZero(),
                playerLevel: playerLevel);

            // AS3 `Land.enterLand` (Land.as:1143-1186) is a three-way choice, and this is that order:
            //   1. param2 != null              — the caller named a room ("x:y")        -> use it
            //   2. land.currentCP && !param1   — a re-visit to a land with a checkpoint -> resume there
            //   3. otherwise                   — the land's begin cell                  -> begLocX/begLocY
            //
            // Order is load-bearing: a caller that DID name a room still gets that room on a re-visit, so
            // only a nameless entry falls through to the checkpoint.
            Vector3Int? resumeRoom = ResolveCheckpointResumeRoom(campaign, targetLand, firstVisit);

            Vector3Int startPos;
            switch (CheckpointRules.ResolveEntryRoomSource(targetPosition.HasValue, resumeRoom.HasValue))
            {
                case CheckpointRules.EntryRoomSource.Named:
                    startPos = targetPosition.Value;
                    break;

                case CheckpointRules.EntryRoomSource.Checkpoint:
                    startPos = resumeRoom.Value;
                    break;

                default:
                    startPos = ResolveDefaultEntryCell(land, templates, ResolveCollectionName(targetLand));
                    break;
            }

            return new LandEntryPlan
            {
                CollectionName = ResolveCollectionName(targetLand),
                Land = land,
                Catalog = catalog,
                LandIsRandom = landIsRandom,
                FirstVisit = firstVisit,
                RunDifficulty = runDifficulty,
                StartPos = startPos,
            };
        }

        /// <summary>
        /// AS3 <c>Land.enterLand</c>'s middle branch (<c>Land.as:1171-1176</c>) — the room a re-visit resumes
        /// in. The rule is <see cref="CheckpointRules.ResumeRoomOnEntry"/>; this is the campaign-state read.
        ///
        /// <para><b>Authored lands only, and that is a limit of the port rather than of the rule.</b> The
        /// chosen <c>startPos</c> reaches the builder only through <c>WorldBuilder.BuildSpecificWorld</c>, and
        /// the choreography layer takes that path for an authored land. A procedural land goes to
        /// <c>WorldBuilder.BuildLand</c>, which takes no start room at all — so for a procedural land
        /// this value is computed and then <b>unused</b>. AS3 has no such split: <c>ativateLoc()</c> runs
        /// against whatever land was just built, procedural or not. Recorded rather than silently returning
        /// null, because the difference is invisible from the outside — the player lands in the entry room
        /// either way, which is what a broken resume looks like too.</para>
        /// </summary>
        private static Vector3Int? ResolveCheckpointResumeRoom(
            CampaignManager campaign, string targetLand, bool firstVisit)
        {
            if (campaign == null) return null;

            CampaignManager.CheckpointRecord? checkpoint = campaign.CurrentCheckpoint;
            if (checkpoint == null) return null;

            CheckpointRules.CheckpointRoom? room = CheckpointRules.ResumeRoomOnEntry(
                hasCheckpoint: true,
                checkpointLandId: checkpoint.Value.landId,
                targetLand: targetLand,
                firstVisit: firstVisit,
                roomX: checkpoint.Value.roomX,
                roomY: checkpoint.Value.roomY,
                roomZ: checkpoint.Value.roomZ);

            if (room == null) return null;

            Debug.Log($"[LandSession] Re-entering '{targetLand}' at its checkpoint " +
                      $"({room.Value.x},{room.Value.y},{room.Value.z}).");

            return new Vector3Int(room.Value.x, room.Value.y, room.Value.z);
        }

        /// <summary>
        /// AS3 <c>Location.createCheck(true)</c>'s tail (<c>Location.as:2105-2108</c>) — the entry cell's
        /// checkpoint is <b>activated by the build</b>:
        /// <code>
        /// if(this.land.act.landStage == 0 &amp;&amp; param1) { this.cp.teleOn = true; }
        /// if(param1) { this.cp.activate(true); }
        /// </code>
        ///
        /// <para><b>What the activation buys, in order of how visible it is.</b> It writes the land's current
        /// checkpoint (<c>land.currentCP</c>, <c>:213</c>), which is the record a re-entry resumes at; it
        /// re-points the button at <c>teleport</c> when <c>mReturn &amp;&amp; teleOn &amp;&amp; !used</c>
        /// (<c>:234-240</c>), which is the "return to base" prompt; and it ends in <c>World.w.saveGame()</c>
        /// (<c>:247</c>), so arriving in a land is itself a save. The <c>param1 == true</c> argument is what
        /// skips the heal/XP restore (<c>:194</c>) — a begin checkpoint is not a rest stop.</para>
        ///
        /// <para><b>The <c>teleOn</c> is read off the placement, not re-derived here.</b> Two statements write
        /// it — the difficulty override in <c>Location.createObj</c> (<c>:2040-2046</c>) and
        /// <c>createCheck</c>'s stage-0 line — and both have already run inside the builder
        /// (<c>WorldBuilder.PlaceCellObjects</c> → <c>RoomPopulator.ApplyCheckpointTeleOn</c> /
        /// <c>ApplyBeginCheckpointTeleOn</c>). Reading the attribute is what keeps this method from owning a
        /// third copy of that rule.</para>
        ///
        /// <para><b><c>locked</c> is <c>false</c> by construction, not by assumption.</b> The begin checkpoint
        /// never rolls a locked variant — <c>CheckpointRules.SelectObjectId</c> short-circuits on
        /// <c>isBegin</c> — and the plain <c>checkpoint</c> row authors neither <c>lock</c> nor <c>mine</c>
        /// (<c>AllData.as:5007</c>). A locked entry checkpoint would be a player locked out of their own
        /// arrival save.</para>
        /// </summary>
        /// <param name="campaign">The campaign; <c>null</c> is tolerated (headless, tests).</param>
        /// <param name="landId">The land just built.</param>
        /// <param name="entryRoom">The room the player was spawned in — <c>landMap.currentRoom</c>.</param>
        public void ActivateEntryCheckpoint(CampaignManager campaign, string landId, RoomInstance entryRoom)
        {
            if (campaign == null || entryRoom == null) return;

            ObjectInstance begin = RoomPopulator.FindBeginCheckpoint(entryRoom);
            if (begin == null)
            {
                // Not an error: a conf only places a begin checkpoint where the plan says so, and the entry
                // cell of a land whose `locx/locy` is off the checkpoint parity carries none. Named anyway,
                // because "no checkpoint at the entrance" and "the activation was refused" look identical
                // from inside the game.
                Debug.Log($"[LandSession] '{landId}': the entry room ({entryRoom.landPosition.x}," +
                          $"{entryRoom.landPosition.y}) carries no begin checkpoint, so nothing was activated.");
                return;
            }

            bool teleOn = !string.IsNullOrEmpty(
                begin.GetAttribute(RoomPopulator.TeleCheckpointAttribute, string.Empty));

            if (!campaign.ActivateCheckpoint(landId, entryRoom.landPosition, begin.code,
                    isBegin: true, teleOn: teleOn, main: false, locked: false))
            {
                return;
            }

            // AS3 CheckPoint.as:247 — the last line of activate().
            SaveManager.Instance?.RequestCheckpointSave();
        }

        /// <summary>
        /// Tell the campaign which land the world it just built actually belongs to.
        ///
        /// <para>The room override is the one build path the campaign did not ask for: it is driven by a
        /// debug flag on the scene bridge, while <c>CampaignManager</c> sets its current land from
        /// <c>catalog.startingLandId</c>. Left alone the two disagree for the whole session — and the damage is
        /// not the label. The F2 Lands tab marks the current land and labels that row's button "Rebuild", so
        /// the wrong row is marked and that button declines to rebuild anything, because
        /// <c>CampaignManager.BeginMission</c> treats "you are already in this land" as a no-op. Pressing Go on
        /// the land you are <i>actually</i> standing in rebuilds it; the row that looks current does nothing.</para>
        ///
        /// <para>The collection key is the join: <c>RoomTemplate.sourceCollectionId</c> holds the same value as
        /// <c>LandDefinition.sourceFileKey</c>, and it is the only key that survives the cases where one
        /// collection backs two lands (<c>rooms_mbase</c> is both <c>random_mbase</c> and <c>bunker</c>).</para>
        /// </summary>
        public void ReportOverrideLand(CampaignManager campaign, string collectionId)
        {
            if (campaign == null)
            {
                // Not an error. The override is usable in a scene with no campaign at all.
                return;
            }

            campaign.AdoptBuiltLand(collectionId);
        }

        /// <summary>
        /// The two campaign writes that end every land entry, in AS3's order:
        ///
        /// <para>1. <c>Land.enterLand</c>'s FIRST line — <c>this.act.visited = true</c> (Land.as:1146) — for
        /// EVERY land, procedural included. Deliberately AFTER the entry and not before: <c>firstVisit</c>
        /// was computed from the pre-entry value (<see cref="CheckpointRules.IsFirstVisit"/>), and marking
        /// the land visited on the way in would make every authored land read as a re-visit and resume at
        /// its checkpoint on the very first entry. This write is also what makes a procedural land
        /// selectable on the travel map after its first entry (PipPageInfo shows a land when
        /// <c>visited || access</c>).</para>
        ///
        /// <para>2. AS3 Game.as:365-368 — <c>if(!_loc1_) this.triggers["firstroom"] = 1;</c>. The other half
        /// of the run-difficulty read, and it must come AFTER the build for the same reason: <c>firstroom</c>
        /// was READ to compute this land's <c>param3</c>, and setting it first would make the very first land
        /// build at <c>max(1, level-1)</c> instead of 0. The oracle's condition is <c>!_loc1_</c>, i.e. NOT a
        /// first visit to an authored land — so a procedural land and a re-visited authored one both arm the
        /// level scaling for every land built afterwards.</para>
        ///
        /// <para>Known divergence, recorded rather than hidden: the oracle caches <c>curLand.land</c> and
        /// only runs the constructor when it is null (or <c>Game.crea</c>), so a re-visit keeps the FIRST
        /// visit's <c>landDifLevel</c>/<c>lootLimit</c>. The port rebuilds on every entry, so a re-visit
        /// recomputes them from the player's level at that moment. That is arguably more correct and is
        /// certainly different; it is a consequence of the port having no land cache, which is its own work
        /// item.</para>
        /// </summary>
        public void CompleteLandEntry(CampaignManager campaign, string targetLand, bool firstVisit)
        {
            if (campaign == null) return;

            campaign.LandStates.MarkVisited(targetLand);

            if (!firstVisit)
            {
                campaign.SetTrigger("firstroom", 1);
            }
        }
    }

    /// <summary>
    /// The decision bundle for one land entry — everything <see cref="LandSessionService.BuildEntryPlan"/>
    /// computes in the oracle's order. A plain mutable carrier, not an immutable struct: the choreography
    /// layer reads every field once, and the codebase's lint culture favours readable fields over ceremony
    /// for throwaway plans.
    /// </summary>
    public sealed class LandEntryPlan
    {
        /// <summary>The room collection the land builds from (<c>&lt;land file=…&gt;</c>).</summary>
        public string CollectionName;

        /// <summary>The resolved land definition; null when the catalogue has no such land (the build
        /// then takes the authored reading, or refuses — see
        /// <see cref="LandSessionService.TemplatesCollapseToASingleCell"/>).</summary>
        public LandDefinition Land;

        /// <summary>The campaign catalogue <see cref="Land"/> was read from — kept because the
        /// unresolved-land warning prints its size to distinguish a stale lookup from an unauthored land.</summary>
        public CampaignCatalog Catalog;

        /// <summary>AS3 <c>curLand.rnd</c> — the land is procedural.</summary>
        public bool LandIsRandom;

        /// <summary>AS3 <c>!param1</c> — the first visit decides both the entry cell and the run
        /// difficulty, so it is computed before either is used.</summary>
        public bool FirstVisit;

        /// <summary>AS3 <c>param3</c> — <c>0</c> until a land has been entered, then
        /// <c>max(1, pers.level - 1)</c>.</summary>
        public int RunDifficulty;

        /// <summary>The land cell the build starts at: named room &gt; checkpoint &gt; land entry cell.</summary>
        public Vector3Int StartPos;
    }
}
