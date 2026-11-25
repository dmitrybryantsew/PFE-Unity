using System;
using System.Collections.Generic;

namespace PFE.Mods.RandomRooms.Core
{
    public class SpaceDemand
    {
        public string role;
        public int minW;
        public int minH;
        public int maxW;
        public int maxH;
        public double weight;
        public string edge;
        public int? maxArea;
        public int uid;

        public SpaceDemand Clone()
        {
            return new SpaceDemand
            {
                role = role,
                minW = minW,
                minH = minH,
                maxW = maxW,
                maxH = maxH,
                weight = weight,
                edge = edge,
                maxArea = maxArea,
                uid = uid
            };
        }
    }

    public class SpaceProgram
    {
        public string theme;
        public string form;
        public string main;
        public List<SpaceDemand> rooms = new();
        public double vertical;
        public double extra;
        public double levels;
        public string density;
        public int requested;
        public string variant;
    }

    /// <summary>
    /// Space demands, functional room sizing, and architectural affinity rules.
    /// Pure C#, zero UnityEngine references.
    /// </summary>
    public class RRSpaceRules
    {
        private readonly Func<double> rnd;
        private string sceneId;

        public RRSpaceRules(Func<double> random)
        {
            rnd = random;
        }

        private string Pick(string[] a)
        {
            return a[(int)(rnd() * a.Length)];
        }

        private int Between(int a, int b)
        {
            return a + (int)(rnd() * (b - a + 1));
        }

        private SpaceDemand Demand(string role, bool wide = false)
        {
            var p = new SpaceDemand
            {
                role = role,
                minW = 5,
                minH = 3,
                maxW = 25,
                maxH = sceneId == "mane" ? 7 : (sceneId == "plant" ? 6 : 5),
                weight = 0.5 + rnd() * 0.8
            };

            if (role == "living")
            {
                p.minW = 7;
                p.minH = 4;
                p.maxW = 26;
            }
            if (role == "kitchen" || role == "medical")
            {
                p.minH = 4;
            }
            if (role == "workshop" || role == "warehouse" || role == "hall")
            {
                p.minW = wide ? 22 : 8;
                p.minH = wide ? 10 : 4;
                p.maxW = wide ? 46 : 32;
                p.maxH = wide ? 21 : 8;
                p.weight = wide ? 6.0 + rnd() * 3.0 : 1.1 + rnd();
            }
            if (role == "corridor")
            {
                p.minW = 10;
                p.minH = 3;
                p.maxW = 46;
                p.maxH = 5;
                p.weight = 1.1;
            }
            if (role == "service")
            {
                p.maxW = 30;
            }
            if (role == "roof")
            {
                p.minW = 16;
                p.minH = 4;
                p.maxW = 46;
                p.maxH = 9;
                p.weight = 2.4;
                p.edge = "top";
            }
            if (role == "street")
            {
                p.minW = 10;
                p.minH = 14;
                p.maxW = 23;
                p.maxH = 23;
                p.weight = 3.5;
            }

            return p;
        }

        public SpaceProgram Create(string theme, string form, bool modern = false, double areaScale = 1.0)
        {
            if (areaScale < 1.0) areaScale = 1.0;
            sceneId = theme;
            var rooms = new List<SpaceDemand>();
            string[] bag;
            int count;
            string main;

            string density = Pick(new[] { "sparse", "standard", "standard", "standard", "dense" });
            bool wide = density == "sparse" || (density == "standard" && rnd() < 0.45);
            double levels = theme == "stable" ? 0.58 : (theme == "mane" ? 0.46 : (theme == "plant" ? 0.35 : 0.24));
            double vertical = 0.5;
            double extra = 0.2 + rnd() * 0.5;

            if (theme == "plant")
            {
                vertical = 0.40 + rnd() * 0.20;
                main = form == "storage_hall" ? "warehouse" : (form == "service_wing" ? "service" : "workshop");
                count = density == "dense" ? Between(10, 12) : (density == "sparse" && main != "service" ? Between(4, 6) : Between(6, 9));
                rooms.Add(Demand(main, wide && main != "service"));
                bag = main == "warehouse" ? new[] { "store", "store", "warehouse", "control", "service" } :
                    (main == "service" ? new[] { "service", "service", "control", "control", "office", "store" } :
                    new[] { "workshop", "workshop", "control", "store", "service" });
                if (rnd() < 0.65) rooms.Add(Demand("control"));
            }
            else if (theme == "stable")
            {
                vertical = 0.30 + rnd() * 0.20;
                main = form == "atrium_ring" ? "hall" : (form == "quarters" ? "living" : "service");
                count = density == "dense" ? Between(10, 13) : (density == "sparse" && main == "hall" ? Between(4, 6) : Between(7, 10));
                rooms.Add(Demand(main, wide && main == "hall"));
                bag = main == "living" ? new[] { "living", "living", "living", "office", "kitchen", "medical" } :
                    (main == "hall" ? new[] { "living", "office", "office", "service", "store", "medical" } :
                    new[] { "service", "service", "control", "control", "medical", "store" });
                if (rnd() < 0.85) rooms.Add(Demand("corridor"));
            }
            else if (theme == "sewer")
            {
                count = density == "dense" ? Between(9, 11) : Between(5, 8);
                vertical = 0.3 + rnd() * 0.2;
                main = form == "dry_tunnels" ? "service" : "canal";
                if (main == "canal")
                {
                    rooms.Add(new SpaceDemand
                    {
                        role = "canal",
                        minW = form == "cistern" ? 26 : 23,
                        minH = form == "cistern" ? 12 : 10,
                        maxW = 46,
                        maxH = 21,
                        weight = form == "cistern" ? 7 : 5
                    });
                    count = density == "dense" ? Between(8, 10) : (density == "sparse" ? Between(3, 5) : Between(5, 7));
                }
                else
                {
                    rooms.Add(Demand("service"));
                }
                bag = form == "pump_chain" ? new[] { "control", "control", "service", "service", "store" } :
                    new[] { "service", "service", "service", "control", "store" };
                extra = 0.2 + rnd() * 0.35;
            }
            else
            {
                vertical = 0.35 + rnd() * 0.2;
                main = form == "offices" ? "office" : (form == "commercial" ? "store" : (form == "ruined" ? "hall" :
                    (form == "rooftops" ? "roof" : (form == "street_links" ? "street" : "living"))));
                rooms.Add(Demand(main, main == "hall"));
                count = density == "dense" ? Between(9, 12) : (density == "sparse" && main == "hall" ? Between(3, 5) : Between(5, 8));
                bag = main == "office" ? new[] { "office", "office", "office", "store", "service" } :
                    (main == "store" ? new[] { "store", "store", "office", "kitchen", "service" } :
                    (main == "roof" || main == "street" ? new[] { "service", "store", "service", "office" } :
                    new[] { "living", "living", "kitchen", "office", "store" }));
                if (main == "roof" || main == "street") count = density == "dense" ? Between(8, 10) : Between(4, 7);
            }

            // A canvas larger than the 46x23 reference interior has more area to fill: the demand
            // capacities and the room count grow with it, otherwise Feasible() rejects the whole
            // plan (capacity < area) and every attempt fails. areaScale == 1 leaves this a no-op.
            //
            // The count grows with the LINEAR dimension (sqrt of the area), not with the area: area
            // grows quadratically per canvas dimension, so a linear count turned a 500x250 canvas
            // into ~1000 demands of up to 5000 tiles wide — a demand set the partition search can
            // never place. The per-room capacities below absorb the remaining growth.
            if (areaScale > 1.0)
            {
                count = (int)Math.Round(count * Math.Sqrt(areaScale));
            }

            while (rooms.Count < count)
            {
                rooms.Add(Demand(Pick(bag)));
            }

            double massChance = theme == "mane" ? 0.35 : (theme == "plant" ? 0.65 : (theme == "stable" ? 0.90 : 1.0));
            int masses = rnd() < massChance ? Between(1, theme == "sewer" ? 3 : (theme == "stable" ? 2 : 1)) : 0;
            for (int i = 0; i < masses; i++)
            {
                rooms.Add(new SpaceDemand
                {
                    role = "mass",
                    minW = 5,
                    minH = 3,
                    maxW = theme == "mane" ? 18 : (theme == "plant" ? 28 : 38),
                    maxH = theme == "mane" ? 6 : (theme == "sewer" ? 17 : 11),
                    weight = theme == "mane" ? 0.8 + rnd() : 1.6 + rnd() * 1.8
                });
            }

            if (modern)
            {
                int massBudget = theme == "mane" ? 40 : (theme == "plant" ? 65 : (theme == "stable" ? 95 : 125));
                foreach (var item in rooms)
                {
                    if (item.role == "mass")
                    {
                        item.maxArea = Math.Max(18, massBudget / Math.Max(1, masses));
                        item.maxW = theme == "mane" ? 13 : (theme == "plant" ? 20 : 24);
                        item.maxH = theme == "sewer" ? 9 : 7;
                        item.weight *= 0.55;
                    }
                }
            }

            if (areaScale > 1.0)
            {
                foreach (var item in rooms)
                {
                    item.maxW = Math.Max(item.minW, (int)Math.Round(item.maxW * areaScale));
                    item.maxH = Math.Max(item.minH, (int)Math.Round(item.maxH * areaScale));
                    if (item.maxArea.HasValue)
                    {
                        item.maxArea = Math.Max(item.minW * item.minH, (int)Math.Round(item.maxArea.Value * areaScale));
                    }
                }
            }

            for (int i = 0; i < rooms.Count; i++)
            {
                rooms[i].uid = i;
            }

            return new SpaceProgram
            {
                theme = theme,
                form = form,
                main = main,
                rooms = rooms,
                vertical = vertical,
                extra = extra,
                levels = levels,
                density = density,
                requested = count,
                variant = wide ? "large-and-small" : "compact-cluster"
            };
        }

        public static double Affinity(string theme, string a, string b)
        {
            if (a == "corridor" || b == "corridor") return 3.0;
            if (a == "street" || b == "street" || a == "roof" || b == "roof") return 1.1;
            if (theme == "plant")
            {
                if ((a == "control" && b == "workshop") || (b == "control" && a == "workshop")) return 3.2;
                if ((a == "store" && b == "warehouse") || (b == "store" && a == "warehouse")) return 3.1;
                if (a == "service" || b == "service") return 2.0;
            }
            if (theme == "stable")
            {
                if (a == "hall" || b == "hall") return 2.8;
                if ((a == "control" && b == "service") || (b == "control" && a == "service")) return 3.0;
                if (a == "living" && b == "living") return 0.7;
            }
            if (theme == "sewer" && (a == "service" || b == "service")) return 2.6;
            if (a == b) return 1.5;
            return 1.0;
        }
    }
}
