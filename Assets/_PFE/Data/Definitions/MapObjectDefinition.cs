using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using PFE.ModAPI;
using PFE.Systems.Map;

namespace PFE.Data.Definitions
{
    /// <summary>
    /// Shared definition for one original AS3 map object id.
    /// Generated from AllData.as <obj> rows and reused by room placements.
    /// </summary>
    [CreateAssetMenu(fileName = "MapObjectDef", menuName = "PFE/Map/Map Object Definition")]
    public class MapObjectDefinition : ScriptableObject, IGameContent
    {
        [Header("Identity")]
        [Tooltip("Original AS3 object id.")]
        public string objectId;
        [Tooltip("Human-readable display name when known.")]
        public string displayName;

        string IGameContent.ContentId => objectId;
        ContentType IGameContent.ContentType => ContentType.MapObjectDefinition;

        public string ID => objectId;

        [Header("Classification")]
        [Tooltip("Original legacy tip classification from AllData.as.")]
        public string legacyTip;
        [Tooltip("Resolved family for gameplay and exporter pipelines.")]
        public MapObjectFamily family = MapObjectFamily.GenericObject;
        [Tooltip("Current room import runtime type bucket.")]
        public string defaultPlacementType = "obj";

        [Header("Footprint")]
        public int size = 1;
        public int width = 1;
        public bool wallMounted;
        public bool blocksMovement;
        public bool blocksVisibility;

        [Header("Interaction")]
        public bool isInteractive;
        public bool isSaveRelevant;
        public bool autoClose;
        public string containerId;
        public string interactionMode;
        public string allAct;
        public string allId;

        [Header("Durability")]
        public float hitPoints;
        public float threshold;
        public float shield;
        public int materialId;

        [Header("Physics")]
        [Tooltip("High-level runtime physics tier for this object.")]
        public MapObjectPhysicalCapability physicalCapability = MapObjectPhysicalCapability.Unknown;
        [Tooltip("Legacy mass value from AllData when present.")]
        public float mass;
        [Tooltip("Multiplier applied to computed mass when no explicit mass is stored.")]
        public float massMultiplier = 1f;
        [Tooltip("Legacy buoyancy/float response from Box.plav when present.")]
        public float buoyancyFactor;

        [Header("Visual")]
        [Tooltip("Legacy visual id or sprite export key when known.")]
        public string defaultVisualId;
        [Tooltip("Imported visual definition used for presentation.")]
        public MapObjectVisualDefinition visual;

        [Header("Legacy")]
        [Tooltip("Full preserved legacy attributes from AllData.as.")]
        public List<MapObjectAttributeData> legacyAttributes = new List<MapObjectAttributeData>();

        public string GetResolvedPlacementType()
        {
            if (!string.IsNullOrWhiteSpace(defaultPlacementType))
            {
                return defaultPlacementType;
            }

            return family switch
            {
                MapObjectFamily.Door => "door",
                MapObjectFamily.AreaTrigger => "area",
                MapObjectFamily.Checkpoint => "checkpoint",
                MapObjectFamily.Bonus => "bonus",
                MapObjectFamily.Trap => "trap",
                MapObjectFamily.PlayerSpawn => "player",
                MapObjectFamily.Container => "box",
                MapObjectFamily.Device => "box",
                MapObjectFamily.Furniture => "box",
                MapObjectFamily.Platform => "box",
                MapObjectFamily.Transition => "box",
                _ => "obj"
            };
        }

        public string GetAttribute(string key, string defaultValue = "")
        {
            return MapObjectDataUtility.GetAttribute(legacyAttributes, key, defaultValue);
        }

        public MapObjectPhysicalCapability GetResolvedPhysicalCapability()
        {
            if (physicalCapability != MapObjectPhysicalCapability.Unknown)
            {
                return physicalCapability;
            }

            return MapObjectDefinitionClassifier.ResolvePhysicalCapability(
                objectId,
                family,
                legacyTip,
                key => GetAttribute(key, string.Empty));
        }

        public bool IsDynamicPhysicalProp()
        {
            MapObjectPhysicalCapability capability = GetResolvedPhysicalCapability();
            return capability == MapObjectPhysicalCapability.DynamicPassive ||
                   capability == MapObjectPhysicalCapability.DynamicThrowable ||
                   capability == MapObjectPhysicalCapability.DynamicTelekinetic;
        }

        public bool SupportsTelekinesis()
        {
            return GetResolvedPhysicalCapability() == MapObjectPhysicalCapability.DynamicTelekinetic;
        }

        public bool CanCauseImpactDamage()
        {
            return IsDynamicPhysicalProp();
        }

        public float GetResolvedMass()
        {
            float resolvedMultiplier = Mathf.Max(0.01f, massMultiplier);
            if (mass > 0f)
            {
                return mass * resolvedMultiplier;
            }

            if (float.TryParse(GetAttribute("massa"), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedMass) &&
                parsedMass > 0f)
            {
                return parsedMass * resolvedMultiplier;
            }

            float derivedMass = Mathf.Max(1f, size * width * 50f);
            return derivedMass * resolvedMultiplier;
        }

        public float GetResolvedBuoyancyFactor()
        {
            if (!Mathf.Approximately(buoyancyFactor, 0f))
            {
                return buoyancyFactor;
            }

            if (float.TryParse(GetAttribute("plav"), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedBuoyancy))
            {
                return parsedBuoyancy;
            }

            return 0f;
        }

        public string GetResolvedVisualId()
        {
            if (visual != null && !string.IsNullOrWhiteSpace(visual.visualId))
            {
                return visual.visualId;
            }

            if (!string.IsNullOrWhiteSpace(defaultVisualId))
            {
                return defaultVisualId;
            }

            return objectId ?? string.Empty;
        }

        public void SetLegacyAttributes(IReadOnlyDictionary<string, string> attributes, params string[] keysToSkip)
        {
            legacyAttributes = MapObjectDataUtility.BuildAttributes(attributes, keysToSkip);
        }
    }

    public enum MapObjectFamily
    {
        Unknown,
        GenericObject,
        Door,
        Container,
        Device,
        Furniture,
        AreaTrigger,
        Checkpoint,
        Bonus,
        Trap,
        PlayerSpawn,
        SpawnMarker,
        Platform,
        Transition,
    }

    public enum MapObjectPhysicalCapability
    {
        Unknown,
        None,
        Static,
        DoorOccupancy,
        DynamicPassive,
        DynamicThrowable,
        DynamicTelekinetic,
    }

    /// <summary>
    /// Centralized classifier for legacy AS3 object ids and attributes.
    /// Keeps import and converter heuristics in one place until full typed behavior exists.
    /// </summary>
    public static class MapObjectDefinitionClassifier
    {
        static readonly HashSet<string> DoorIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "septum", "grate", "hgrate", "platform1", "window1", "window2",
            "door1", "door1a", "door1b", "door2", "door2a", "stdoor",
            "basedoor", "door3", "door4", "hatch1", "hatch2", "encldoor",
            "enclpole", "alib1", "alib2"
        };

        static readonly HashSet<string> ContainerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "safe", "wallsafe", "chest", "case", "ammobox", "explbox", "bigexpl",
            "weapbox", "weapcase", "bookcase", "medbox", "bigmed", "wallcab",
            "locker", "trash", "cup", "tumba1", "tumba2", "filecab", "fridge",
            "ccup", "wcup", "tap", "cryocap", "basechest", "enclchest", "enclcase",
            "woodbox", "mcrate1", "box", "mcrate2", "mcrate3", "bigbox", "bigbox2",
            "mcrate4", "mcrate5"
        };

        static readonly HashSet<string> FurnitureIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "table", "couch", "bed", "table1", "table2", "table3", "table4",
            "longtable", "niche1", "wmap", "stand", "vault", "p_luna",
            "barst", "smotr", "camp", "work", "himlab", "stove", "aaa", "tomb"
        };

        static readonly HashSet<string> DeviceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "instr1", "instr2", "term", "term1", "term2", "term3", "termh",
            "elpanel", "knop1", "knop2", "knop3", "knop4", "dsph", "brspr",
            "detonator", "robocell", "alarm", "reactor", "generat"
        };

        static readonly HashSet<string> TransitionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "indoor1", "indoor2", "indoor3", "indoor4", "instdoor", "inbasedoor",
            "inencldoor", "exit", "doorout", "doorprob", "doorboss",
            "door_st1", "door_st2"
        };

        static readonly HashSet<string> PlatformIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "platform1", "platform2", "platform3"
        };

        static readonly HashSet<string> TrapIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "spikes", "fspikes", "moln1", "radbarrel", "radbigbarrel", "tcloud1", "pcloud1"
        };

        public static MapObjectFamily ResolveFamily(
            string objectId,
            IReadOnlyDictionary<string, string> attributes,
            out string defaultPlacementType,
            out string normalizedTip)
        {
            string id = objectId?.Trim() ?? string.Empty;
            normalizedTip = NormalizeTip(GetAttribute(attributes, "tip"));

            if (id.Equals("player", StringComparison.OrdinalIgnoreCase))
            {
                defaultPlacementType = "player";
                return MapObjectFamily.PlayerSpawn;
            }

            if (normalizedTip == "checkpoint" || id.StartsWith("checkpoint", StringComparison.OrdinalIgnoreCase))
            {
                defaultPlacementType = "checkpoint";
                return MapObjectFamily.Checkpoint;
            }

            if (normalizedTip == "area" || id.Equals("area", StringComparison.OrdinalIgnoreCase))
            {
                defaultPlacementType = "area";
                return MapObjectFamily.AreaTrigger;
            }

            if (normalizedTip == "bonus" || id.Equals("xp", StringComparison.OrdinalIgnoreCase))
            {
                defaultPlacementType = "bonus";
                return MapObjectFamily.Bonus;
            }

            if (normalizedTip == "trap" || TrapIds.Contains(id))
            {
                defaultPlacementType = "trap";
                return MapObjectFamily.Trap;
            }

            if (normalizedTip == "door" || DoorIds.Contains(id) || id.StartsWith("door", StringComparison.OrdinalIgnoreCase))
            {
                defaultPlacementType = "door";
                return PlatformIds.Contains(id) ? MapObjectFamily.Platform : MapObjectFamily.Door;
            }

            if (normalizedTip == "spawnpoint")
            {
                defaultPlacementType = "obj";
                return MapObjectFamily.SpawnMarker;
            }

            if (TransitionIds.Contains(id))
            {
                defaultPlacementType = "box";
                return MapObjectFamily.Transition;
            }

            if (PlatformIds.Contains(id))
            {
                defaultPlacementType = id.Equals("platform1", StringComparison.OrdinalIgnoreCase) ? "door" : "box";
                return MapObjectFamily.Platform;
            }

            if (normalizedTip == "box" || ContainerIds.Contains(id) || DeviceIds.Contains(id) || FurnitureIds.Contains(id) ||
                id.StartsWith("instr", StringComparison.OrdinalIgnoreCase))
            {
                defaultPlacementType = "box";

                if (HasAttribute(attributes, "cont") || ContainerIds.Contains(id))
                {
                    return MapObjectFamily.Container;
                }

                if (HasAttribute(attributes, "allact") || HasAttribute(attributes, "allid") ||
                    HasAttribute(attributes, "knop") || DeviceIds.Contains(id))
                {
                    return MapObjectFamily.Device;
                }

                if (FurnitureIds.Contains(id))
                {
                    return MapObjectFamily.Furniture;
                }

                return MapObjectFamily.GenericObject;
            }

            defaultPlacementType = "obj";
            return MapObjectFamily.GenericObject;
        }

        public static MapObjectPhysicalCapability ResolvePhysicalCapability(
            string objectId,
            MapObjectFamily family,
            string legacyTip,
            Func<string, string> getAttribute)
        {
            string normalizedTip = NormalizeTip(legacyTip);
            if (string.IsNullOrWhiteSpace(normalizedTip))
            {
                normalizedTip = NormalizeTip(getAttribute?.Invoke("tip"));
            }

            int wall = ParseInt(getAttribute?.Invoke("wall"), ParseInt(getAttribute?.Invoke("stena"), 0));
            int size = Mathf.Max(1, ParseInt(getAttribute?.Invoke("size"), ParseInt(getAttribute?.Invoke("sz"), 1)));
            int width = Mathf.Max(1, ParseInt(getAttribute?.Invoke("wid"), ParseInt(getAttribute?.Invoke("w"), 1)));
            float mass = ParseFloat(getAttribute?.Invoke("massa"), 0f);
            int footprint = size * width;

            if (family == MapObjectFamily.PlayerSpawn ||
                family == MapObjectFamily.SpawnMarker ||
                family == MapObjectFamily.Bonus ||
                family == MapObjectFamily.AreaTrigger ||
                family == MapObjectFamily.Checkpoint)
            {
                return MapObjectPhysicalCapability.None;
            }

            if (normalizedTip == "unit" ||
                normalizedTip == "bonus" ||
                normalizedTip == "area" ||
                normalizedTip == "spawnpoint" ||
                normalizedTip == "up" ||
                normalizedTip == "enspawn")
            {
                return MapObjectPhysicalCapability.None;
            }

            if (family == MapObjectFamily.Door ||
                family == MapObjectFamily.Platform ||
                normalizedTip == "door")
            {
                return MapObjectPhysicalCapability.DoorOccupancy;
            }

            if (normalizedTip == "trap")
            {
                return MapObjectPhysicalCapability.Static;
            }

            if (wall > 0)
            {
                return MapObjectPhysicalCapability.Static;
            }

            if (normalizedTip == "box" ||
                family == MapObjectFamily.Container ||
                family == MapObjectFamily.Device ||
                family == MapObjectFamily.Furniture ||
                family == MapObjectFamily.Transition ||
                family == MapObjectFamily.GenericObject ||
                family == MapObjectFamily.Trap)
            {
                if (string.Equals(objectId, "reactor", StringComparison.OrdinalIgnoreCase) ||
                    mass >= 5000f ||
                    footprint >= 24)
                {
                    return MapObjectPhysicalCapability.DynamicPassive;
                }

                if (mass >= 400f || footprint >= 6)
                {
                    return MapObjectPhysicalCapability.DynamicThrowable;
                }

                return MapObjectPhysicalCapability.DynamicTelekinetic;
            }

            return MapObjectPhysicalCapability.None;
        }

        static string GetAttribute(IReadOnlyDictionary<string, string> attributes, string key)
        {
            if (attributes == null || string.IsNullOrWhiteSpace(key))
            {
                return string.Empty;
            }

            return attributes.TryGetValue(key, out string value) ? value ?? string.Empty : string.Empty;
        }

        static bool HasAttribute(IReadOnlyDictionary<string, string> attributes, string key)
        {
            return !string.IsNullOrWhiteSpace(GetAttribute(attributes, key));
        }

        static string NormalizeTip(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim().ToLowerInvariant();
        }

        static int ParseInt(string value, int defaultValue)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedValue)
                ? parsedValue
                : defaultValue;
        }

        static float ParseFloat(string value, float defaultValue)
        {
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedValue)
                ? parsedValue
                : defaultValue;
        }
    }
}
