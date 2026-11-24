using System;
using System.Collections.Generic;

namespace PFE.Mods.RandomRooms.Core
{
    public class SceneProfile
    {
        public string wall;
        public string trim;
        public string[] backgrounds;
        public string[] forms;
        public int min;
        public int max;
        public double bias;
        public string light;
        public string[] hatches;
        public string[] windows;
        public int windowMax;
    }

    public class SceneOptions
    {
        public string backwall;
        public string music;
        public string color;
        public int wtip;
        public int wrad;
        public int vis;
        public int darkness;
    }

    /// <summary>
    /// Scene profile, material palettes, door definitions, and environmental properties for biomes.
    /// Pure C#, zero UnityEngine references.
    /// </summary>
    public static class RRScene
    {
        public static readonly string[] IDS = { "plant", "stable", "sewer", "mane" };
        public static readonly string[] NAMES = { "工厂", "废弃避难厩", "下水道", "城市废墟" };

        public static bool Valid(string id)
        {
            return Array.IndexOf(IDS, id) >= 0;
        }

        public static string Name(string id)
        {
            int idx = Array.IndexOf(IDS, id);
            return idx >= 0 ? NAMES[idx] : "随机选择";
        }

        public static SceneProfile Profile(string id)
        {
            switch (id)
            {
                case "plant":
                    return new SceneProfile
                    {
                        wall = "C",
                        trim = "C",
                        backgrounds = new[] { "C", "D", "J" },
                        forms = new[] { "production", "storage_hall", "service_wing" },
                        min = 4,
                        max = 7,
                        bias = 0.65,
                        light = "light4",
                        hatches = new[] { "hatch1", "hatch1", "hatch2" },
                        windows = new[] { "window1" },
                        windowMax = 1
                    };
                case "stable":
                    return new SceneProfile
                    {
                        wall = "J",
                        trim = "K",
                        backgrounds = new[] { "N", "R", "Q" },
                        forms = new[] { "quarters", "atrium_ring", "service_cluster" },
                        min = 6,
                        max = 9,
                        bias = 0.46,
                        light = "stlight3",
                        hatches = new[] { "hatch2" },
                        windows = new[] { "window2" },
                        windowMax = 3
                    };
                case "sewer":
                    return new SceneProfile
                    {
                        wall = "L",
                        trim = "M",
                        backgrounds = new[] { "E", "C", "T" },
                        forms = new[] { "canal_gallery", "cistern", "pump_chain", "dry_tunnels" },
                        min = 3,
                        max = 6,
                        bias = 0.22,
                        light = "light1",
                        hatches = new[] { "hatch1", "hatch1", "hatch2" },
                        windows = new[] { "window1" },
                        windowMax = 1
                    };
                case "mane":
                    return new SceneProfile
                    {
                        wall = "N",
                        trim = "N",
                        backgrounds = new[] { "C", "D", "J" },
                        forms = new[] { "apartments", "offices", "commercial", "ruined", "rooftops", "street_links" },
                        min = 8,
                        max = 14,
                        bias = 0.55,
                        light = "light3",
                        hatches = new[] { "hatch1", "hatch2" },
                        windows = new[] { "window1", "window1", "window2" },
                        windowMax = 5
                    };
                default:
                    throw new ArgumentException($"Unknown scene: {id}");
            }
        }

        public static SceneOptions Options(string id, string form = "")
        {
            switch (id)
            {
                case "plant":
                    return new SceneOptions { backwall = "tBackWall", music = "music_plant_1" };
                case "stable":
                    return new SceneOptions { backwall = "tStConcrete", music = "music_stable_1" };
                case "sewer":
                    return new SceneOptions { backwall = "tMossy", music = "music_sewer_1", color = "green", wtip = 1, wrad = 3 };
                case "mane":
                    return new SceneOptions
                    {
                        backwall = form == "rooftops" ? "sky" : (form == "street_links" ? "tWindows2" : "tWindows"),
                        music = "music_mane_1",
                        vis = 2,
                        darkness = -20
                    };
                default:
                    throw new ArgumentException($"Unknown scene options: {id}");
            }
        }

        public static string Background(string id, string role, Func<double> random)
        {
            if (role == "street" || role == "roof") return "";
            string[] a;
            if (id == "stable")
            {
                a = (role == "living" || role == "kitchen" || role == "medical") ? new[] { "N" } :
                    ((role == "control" || role == "service") ? new[] { "Q" } : new[] { "P" });
            }
            else if (id == "plant")
            {
                a = role == "office" ? new[] { "B" } :
                    (role == "control" ? new[] { "H" } : new[] { "C", "D" });
            }
            else if (id == "sewer")
            {
                a = role == "control" ? new[] { "H" } :
                    (role == "service" ? new[] { "S" } : new[] { "E" });
            }
            else if (id == "mane")
            {
                a = (role == "service" || role == "store") ? new[] { "H" } :
                    ((role == "kitchen" || role == "medical") ? new[] { "J" } : new[] { "C", "D" });
            }
            else
            {
                throw new ArgumentException($"Unknown background scene: {id}");
            }

            return a[(int)(random() * a.Length)];
        }

        public static string Door(string id, string a, string b, Func<double> random)
        {
            if (a == "street" || b == "street" || a == "roof" || b == "roof") return "door1";
            if (id == "stable")
            {
                return (a == "service" || b == "service") ? "door1b" :
                    ((a == "control" || b == "control") ? "door2" : "stdoor");
            }
            if (id == "sewer")
            {
                return (a == "control" || b == "control") ? "door3" : "door1";
            }
            if (id == "plant")
            {
                return (a == "control" || b == "control") ? "door2" :
                    (((a == "workshop" || b == "workshop") && random() < 0.2) ? "door3" : "door1");
            }
            if (id == "mane")
            {
                return random() < 0.8 ? "door1" : "door1a";
            }
            throw new ArgumentException($"Unknown door scene: {id}");
        }
    }
}
