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
}
