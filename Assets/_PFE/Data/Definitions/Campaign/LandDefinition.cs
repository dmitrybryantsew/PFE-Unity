using System;
using System.Collections.Generic;
using UnityEngine;
using PFE.ModAPI;
using PFE.Systems.Map;

namespace PFE.Data.Definitions.Campaign
{
    /// <summary>
    /// ScriptableObject definition for a Campaign Land (level/zone).
    /// Corresponds to <c>&lt;land&gt;</c> entries in ActionScript 3 <c>GameData.as</c>
    /// and the runtime <c>fe.loc.LandAct</c> class.
    /// </summary>
    [CreateAssetMenu(fileName = "NewLandDef", menuName = "PFE/Campaign/Land Definition")]
    public class LandDefinition : VersionedData, IGameContent
    {
        [Header("Identity")]
        [Tooltip("Unique Land ID (e.g. begin, surf, rbl, random_plant, random_canter)")]
        public string landId = "";

        [Tooltip("Localized or human-readable title for UI display")]
        public string landName = "";

        [Tooltip("Land category tip: 'story', 'base', 'rnd', 'hard', 'prob'")]
        public string tip = "story";

        [Tooltip("True for the detached room collection that exit rooms and boss doors live in " +
                 "(AS3 the `prob` attribute — Game.as:75-82). Those lands go to game.probs, not " +
                 "game.lands, and are never offered on the travel map.")]
        public bool isProbLand = false;

        [Tooltip("True for a developer sandbox land (AS3 the `test` attribute, e.g. `test`/`test2`). " +
                 "Hidden from the travel map unless test mode is on.")]
        public bool isTestLand = false;

        // IGameContent implementation
        string IGameContent.ContentId => landId;
        ContentType IGameContent.ContentType => ContentType.Campaign;
        public override string DataId => landId;
        public override string DisplayName => string.IsNullOrEmpty(landName) ? landId : landName;

        [Header("Generation Mode")]
        [Tooltip("True if this is a procedurally assembled dungeon (rnd='1' in AS3). False if fixed authored coordinates.")]
        public bool isProcedural = false;

        [Tooltip("Campaign progression stage (1..12)")]
        public int stage = 1;

        [Tooltip("Base difficulty level (dif attribute in AS3)")]
        public int baseDifficulty = 0;

        [Tooltip("Scale difficulty to player level (autolevel='1' in AS3)")]
        public bool autoLevel = false;

        [Tooltip("Biome identifier for tile themes and hazards (biom in AS3)")]
        public int biomeId = 0;

        [Tooltip("Room assembly configuration archetype (conf in AS3)")]
        public int configId = 0;

        [Tooltip("Source room collection file key (e.g. rooms_begin, rooms_rbl, rooms_plant)")]
        public string sourceFileKey = "";

        [Tooltip("Exit destination land ID (exit attribute in AS3)")]
        public string exitLandId = "";

        [Tooltip("Template selection list size (list attribute in AS3)")]
        public int listCount = 0;

        [Tooltip("Loading screen background graphic index (loadscr in AS3)")]
        public int loadScreenIndex = -1;

        [Tooltip("Loot or spawn limits (limit in AS3)")]
        public int limit = 0;

        [Tooltip("Campaign finale flag: 0=normal, 1=final mission (art), 2=ending epilogue (grave)")]
        public int fin = 0;

        [Header("Grid Layout")]
        [Tooltip("Grid width in room cells (mx attribute in AS3)")]
        public int gridWidth = 1;

        [Tooltip("Grid height in room cells (my attribute in AS3)")]
        public int gridHeight = 1;

        [Tooltip("Player spawn entry room coordinates (locx, locy in AS3)")]
        public Vector2Int entryCoordinates = Vector2Int.zero;

        [Header("Atmosphere & Presentation (options)")]
        [Tooltip("Default background wall tile texture (backwall in AS3)")]
        public string backwallTexture = "";

        [Tooltip("Parallax background scenery backdrop ID (fon in AS3, e.g. fonWay, fonCanter)")]
        public string backdropId = "";

        [Tooltip("Ambient music track ID (music in AS3, e.g. music_begin, music_surf)")]
        public string musicTrackId = "";

        [Tooltip("Play battle-end / post-encounter music track (postmusic in AS3)")]
        public bool postMusic = false;

        [Tooltip("Ambient darkness modifier (darkness in AS3, e.g. -30 for dark zones)")]
        public float ambientDarkness = 0f;

        [Tooltip("Color grading palette tint (color in AS3, e.g. fire, sky, red, pink, blue)")]
        public string colorGrade = "";

        [Tooltip("Room boundary framing style (border in AS3, e.g. O, S, Q, N)")]
        public string borderStyle = "";

        [Tooltip("Visibility range restriction (vis in AS3)")]
        public int visibilityRange = 0;

        [Header("Hazards")]
        [Tooltip("Hazard type (wtip in AS3: 0=none, 1=radiation, 2=acid, 3=pink cloud)")]
        public int hazardType = 0;

        [Tooltip("Hazard water/gas radius (wrad in AS3)")]
        public float hazardRadius = 0f;

        [Tooltip("Hazard tick damage (wdam in AS3)")]
        public float hazardDamage = 0f;

        [Tooltip("Hazard damage type (wtipdam in AS3)")]
        public int hazardDamageType = 0;

        [Header("Rewards")]
        [Tooltip("Experience points awarded upon completing/clearing this land (xp in AS3)")]
        public int xpReward = 0;

        [Header("Linked Room Templates")]
        [Tooltip("All RoomTemplate assets associated with this land")]
        public List<RoomTemplate> roomTemplates = new List<RoomTemplate>();

        /// <summary>
        /// AS3 <c>&lt;prob&gt;</c> children of this <c>&lt;land&gt;</c> — the bonus and boss rooms the land
        /// can open a <c>doorprob</c> / <c>doorboss</c> into.
        ///
        /// <para>Selected by <c>Land.newRandomProb</c> (<c>Land.as:809-863</c>) and constructed by
        /// <c>Land.buildProb</c> (<c>Land.as:771-803</c>). Seven lands declare these — <c>rbl</c> (7) plus
        /// <c>random_plant</c> (14), <c>random_stable</c> (14), <c>random_mane</c> (10), <c>random_canter</c>
        /// (9), <c>random_sewer</c> (6) and <c>random_encl</c> (6). The room itself is a room of the
        /// <c>prob</c> land (<c>rooms_prob</c>, 81 rooms) whose <c>name</c> equals this <c>id</c>.</para>
        /// </summary>
        [Tooltip("AS3 <prob> children: the bonus/boss rooms a doorprob/doorboss can open into")]
        public List<ProbRoomDefinition> probRooms = new List<ProbRoomDefinition>();

        /// <summary>
        /// AS3 <c>&lt;scr eve='take' item='…'&gt;</c> children of this <c>&lt;land&gt;</c> — the
        /// <b>first-acquisition item scripts</b>.
        ///
        /// <para><b>Oracle.</b> <c>Land.as:141-149</c> builds <c>land.itemScripts</c> by walking the
        /// land's own <c>&lt;scr&gt;</c> children and keeping every one whose <c>@eve == "take"</c> and
        /// whose <c>@item</c> is non-empty, keyed by <c>@item</c>. <c>Invent.takeScript(id)</c>
        /// (<c>Invent.as:1554-1560</c>) then starts that script, and <c>Invent.take()</c> calls it from
        /// five places — each guarded by <c>this.items[l.id].kol == 0</c>, i.e. <b>only the first time
        /// the player ever holds that item</b> (<c>:1102</c>, <c>:1132</c>, <c>:1192</c>, <c>:1213</c>,
        /// <c>:1227</c>).</para>
        ///
        /// <para><b>Why this is not a curiosity.</b> <c>collectHS</c> — the campaign's first quest, the
        /// one that sends the player after the Cogwheel — is granted from <b>exactly one place in the
        /// entire game</b>: <c>&lt;scr eve='take' item='col1'&gt;&lt;s act="quest"
        /// val="collectHS"/&gt;</c> (<c>GameData.as:44-46</c>). The <c>begin</c> land declares six of
        /// these scripts in total (<c>mont</c>, <c>screwdriver</c>, <c>rech</c>, <c>r32</c>,
        /// <c>s_arson</c>, <c>col1</c>), <c>test</c> one (<c>captive</c>) and <c>test2</c> one
        /// (<c>diary</c>). With the table unparsed, taking the Cogwheel opened no quest and taking the
        /// screwdriver left <c>trDoor</c> shut.</para>
        ///
        /// <para><b>Only <c>&lt;s&gt;</c> children are actions.</b> <c>Script.as:47-53</c> iterates
        /// <c>param1.s</c>, which in E4X is the children named exactly <c>s</c> — so the one
        /// <c>&lt;s1 act="mess" val="trMont" opt1="1"/&gt;</c> in <c>GameData.as:27</c> is <b>dead data
        /// the oracle never runs</b>, and this parser must drop it too.</para>
        /// </summary>
        [Tooltip("AS3 <scr eve='take' item='…'> children: scripts that run on first acquisition of an item")]
        public List<LandItemScript> itemScripts = new List<LandItemScript>();

        protected override bool OnValidateData()
        {
            if (string.IsNullOrEmpty(landId))
            {
                Debug.LogWarning("LandDefinition: landId is missing!");
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// One AS3 <c>&lt;scr eve='take' item='…'&gt;</c> child of a <c>&lt;land&gt;</c> — a script that runs
    /// the first time the player ever acquires the named item, while that land is the loaded one.
    ///
    /// <para>Held as a plain serializable row rather than a <see cref="CampaignScriptDefinition"/>
    /// because the oracle keys these by item and never gives them an <c>id</c>: they are looked up by
    /// <c>land.itemScripts[itemId]</c> (<c>Land.as:147</c>), not by a script id. A
    /// <c>ScriptableObject</c> per row would also make the whole table unbuildable in the offline
    /// fixtures, which is where the import is actually verified.</para>
    /// </summary>
    [Serializable]
    public class LandItemScript
    {
        /// <summary>AS3 <c>@item</c> — the item id whose first acquisition fires this script.</summary>
        [Tooltip("AS3 @item: the item id whose first acquisition runs these actions")]
        public string itemId = "";

        /// <summary>
        /// AS3 the <c>&lt;s&gt;</c> children, in document order.
        ///
        /// <para><b>Not <c>&lt;s1&gt;</c>.</b> <c>Script.as:47-53</c> reads <c>param1.s</c>, which is
        /// E4X for the children named exactly <c>s</c>; the single <c>&lt;s1&gt;</c> in the data
        /// (<c>GameData.as:27</c>, in the <c>mont</c> script) is never executed by the oracle and is
        /// therefore not parsed here.</para>
        /// </summary>
        [Tooltip("AS3 <s> children, in document order (never <s1> — Script.as:47 reads .s only)")]
        public List<CampaignScriptAction> actions = new List<CampaignScriptAction>();
    }

    /// <summary>
    /// One AS3 <c>&lt;prob&gt;</c> child of a <c>&lt;land&gt;</c>: a bonus or boss room that the land can
    /// open a <c>doorprob</c> / <c>doorboss</c> into.
    ///
    /// <para><b>Oracle.</b> <c>GameData.as</c> declares 66 of these across 7 lands
    /// (<c>rbl</c> 7, <c>random_plant</c> 14, <c>random_sewer</c> 6, <c>random_stable</c> 14,
    /// <c>random_mane</c> 10, <c>random_canter</c> 9, <c>random_encl</c> 6). The attribute set is
    /// exactly <c>id</c> (66), <c>tip</c> (66), <c>level</c> (58), <c>prize</c> (24), <c>close</c> (7) and
    /// <c>imp</c> (2), with 97 <c>&lt;con&gt;</c> children and 24 <c>&lt;wave&gt;</c> children.</para>
    /// </summary>
    [Serializable]
    public class ProbRoomDefinition
    {
        [Tooltip("AS3 id. Also the name of the room in the `prob` land's collection that this opens.")]
        public string id = "";

        /// <summary>
        /// AS3 <c>level</c>. <b>Absent is not 0.</b> <c>newRandomProb</c> tests
        /// <c>xml.@level.length == 0</c>, so a room with no <c>level</c> attribute is eligible at every
        /// stage while one with <c>level='0'</c> is only eligible when <c>maxlevel &gt;= 0</c>. Eight of
        /// the 66 have no <c>level</c>; 23 carry <c>level='0'</c>.
        /// </summary>
        public bool hasLevel;
        public int level;

        [Tooltip("AS3 imp present. When a caller asks for `imp`, this room is preferred over the roll.")]
        public bool imp;

        /// <summary>
        /// AS3 <c>tip</c>. <c>"2"</c> makes the door a <c>doorboss</c>, anything else a <c>doorprob</c>
        /// (<c>Land.as:843-851</c>). In the data: 34 are <c>"2"</c>, 31 are <c>"1"</c>, 1 is <c>"0"</c>.
        /// </summary>
        public string tip = "";

        [Tooltip("AS3 prize — the room holds a reward.")]
        public bool prize;

        [Tooltip("AS3 close — the room seals behind the player until it is cleared.")]
        public bool close;

        /// <summary>
        /// AS3 <c>&lt;con&gt;</c> children: the room's contents.
        ///
        /// <para><b>The adapter now exists, so this gap is closed.</b> The rules were ported first
        /// (<see cref="PFE.Systems.Map.Generation.ProbationState"/> is the whole state machine), then
        /// the driver — <see cref="PFE.Systems.Map.Generation.ProbRoomSessions"/> owns one state per
        /// prob room, reads a live room into a <see cref="PFE.Systems.Map.Generation.ProbRoomView"/>,
        /// applies the resulting <see cref="PFE.Systems.Map.Generation.ProbEffects"/> and raises the
        /// room-cleared event. It is wired from the interaction path, the unit-death path and the
        /// enter/leave paths, and it is built and registered by
        /// <c>RoomTransitionManager.BuildProbSessions</c>.</para>
        ///
        /// <para><b>What is still not parsed: the four <c>&lt;scr&gt;</c> children</b> (alarm / in /
        /// out / close, <c>Probation.as:105-130</c>) — the event scripts. Nothing runs them. The
        /// <c>&lt;wave&gt;</c> payload question is separate and answered on
        /// <see cref="waves"/>: the metadata is parsed, the <c>&lt;obj&gt;</c>s are not, because there
        /// is no spawner to hand them to.</para>
        /// </summary>
        public List<ProbContentData> contents = new List<ProbContentData>();

        /// <summary>
        /// AS3 <c>&lt;wave&gt;</c> children: the timed spawn groups behind
        /// <see cref="ProbationState.maxwave"/>. Metadata only — the <c>&lt;obj&gt;</c> payloads are not
        /// carried, because <see cref="PFE.Systems.Map.Generation.ProbRoomSessions"/> has no spawner to
        /// hand them to. What <b>is</b> carried is what the state machine needs to be honest: the count,
        /// each delay, and each wave's size, so <c>nwave &lt; maxwave</c> is a real gate rather than
        /// always-false.
        ///
        /// <para><b>The consequence is stated, not hidden.</b> Because the payloads are not spawned,
        /// <c>nwave</c> never advances and the six wave-only probs (<c>w_raiders</c>, <c>w_zombie</c>,
        /// <c>w_robots</c>, <c>w_grif</c>, <c>w_necros</c>, <c>w_encl</c>) never close. Their gate is
        /// <c>nwave &lt; maxwave</c>, which is true whenever the waves have not run — see the class
        /// summary on <see cref="ProbRoomSessions"/>.</para>
        /// </summary>
        public List<ProbWaveData> waves = new List<ProbWaveData>();
    }

    /// <summary>
    /// One AS3 <c>&lt;wave t='…'&gt;</c> child of a <c>&lt;prob&gt;</c>, metadata only — see
    /// <see cref="ProbRoomDefinition.waves"/> for why the payload is not parsed.
    /// </summary>
    [Serializable]
    public class ProbWaveData
    {
        /// <summary>AS3 <c>@t.length()</c> — a wave with no <c>t</c> leaves the timer untouched
        /// (<c>Probation.as:341-344</c>), which is a different outcome from a delay of 0.</summary>
        public bool hasDelay;

        /// <summary>AS3 <c>@t</c>, in seconds.</summary>
        public int delaySeconds;

        /// <summary>The number of <c>&lt;obj&gt;</c> children — what <c>kolEn</c> gains
        /// (<c>Probation.as:337-340</c>).</summary>
        public int spawnCount;
    }

    /// <summary>One AS3 <c>&lt;con&gt;</c> child of a <c>&lt;prob&gt;</c>.</summary>
    [Serializable]
    public class ProbContentData
    {
        /// <summary>
        /// AS3 <c>tip</c> on the <c>&lt;con&gt;</c>. In the data: <c>box</c> 65, <c>unit</c> 26,
        /// <c>wave</c> 6. <c>checkAllCon</c> branches on exactly these three (<c>Probation.as:163-192</c>),
        /// and treats an absent <c>tip</c> as <c>box</c>.
        /// </summary>
        public string tip = "";

        /// <summary>
        /// AS3 <c>uid</c> — the object's unique id within its room. 79 of the 97 contents carry one
        /// (all 65 <c>box</c> and 14 of the 26 <c>unit</c>).
        /// </summary>
        public string uid = "";

        /// <summary>
        /// AS3 <c>qid</c> — a quest id, on the 12 unit contents that carry one instead of a
        /// <c>uid</c> (<c>Probation.as:179</c> matches either). The remaining 6 contents — the
        /// <c>tip='wave'</c> gates — carry neither.
        /// </summary>
        public string qid = "";
    }
}
