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
        /// <para><b>Two sibling elements are still not parsed, and the reason has changed.</b> The 24
        /// <c>&lt;wave&gt;</c> children (128 <c>&lt;obj&gt;</c> between them) feed
        /// <c>Probation.maxwave</c> and the wave timer, and the 4 <c>&lt;scr&gt;</c> children are the
        /// alarm/in/out/close event scripts (<c>Probation.as:105-130</c>). Those <i>rules</i> now exist —
        /// <see cref="PFE.Systems.Map.Generation.ProbationState"/> ports the whole state machine, including
        /// <c>SetMaxWave</c>, <c>CreateWave</c> and <c>Step</c> — but what is missing is the adapter that
        /// would <i>drive</i> them: it is the piece that would read a <c>&lt;wave&gt;</c> payload and hand
        /// the state machine a <c>ProbWaveView</c>, and it is owner-only play-test work. Parsing the
        /// payloads before that adapter exists would produce data no code reads, which is
        /// indistinguishable from a bug.</para>
        /// </summary>
        public List<ProbContentData> contents = new List<ProbContentData>();
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
