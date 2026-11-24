using System;
using System.Collections.Generic;

namespace PFE.Mods.RandomRooms.Core
{
    public class PopulationObject
    {
        public string id;
        public int x;
        public int y;
        public string rrContent;
        public string rrMount;
        public string uid;
        public string code;
        public Dictionary<string, string> attrs = new();
        public int floorY;
        public int lift;
        public int operatorNode = -1;
        public int turn = 0;
        public string vis;
        public int sector = -1;
        public string zone;
        public int danger;
        public int value;
        public int? cacheFace;
        public string unlockTarget;
    }

    public class CandidatePosition
    {
        public int x;
        public int y;
        public int floorY;
        public double score;
        public int operatorNode = -1;
    }

    /// <summary>
    /// Placement helper and footprint validator for enemies, loot, terminals, and devices.
    /// Pure C#, zero UnityEngine references.
    /// </summary>
    public class RRPopulation
    {
        public List<PopulationObject> objects = new();
        public string mood;
        public int stage;
        public RREcology ecology;
        public RRContentPlan contentPlan;

        private readonly RRArchitecture p;
        private readonly Func<double> random;
        private readonly GenerationContext context;
        private readonly HashSet<(int y, int x)> used = new();
        private int serial = 0;
        private readonly string prefix;

        public static readonly Dictionary<string, int[]> SIZES = new()
        {
            { "chest", new[] { 2, 1 } }, { "safe", new[] { 2, 2 } }, { "wallsafe", new[] { 1, 1 } }, { "ammobox", new[] { 1, 1 } }, { "explbox", new[] { 1, 1 } },
            { "bigexpl", new[] { 3, 1 } }, { "weapbox", new[] { 2, 2 } }, { "weapcase", new[] { 3, 1 } }, { "case", new[] { 1, 1 } }, { "basechest", new[] { 2, 1 } },
            { "medbox", new[] { 1, 1 } }, { "bigmed", new[] { 2, 3 } }, { "instr1", new[] { 2, 2 } }, { "instr2", new[] { 2, 1 } }, { "bookcase", new[] { 2, 3 } },
            { "cryocap", new[] { 2, 3 } }, { "term1", new[] { 1, 1 } }, { "term2", new[] { 1, 1 } }, { "term3", new[] { 1, 1 } },
            { "work", new[] { 2, 2 } }, { "himlab", new[] { 2, 2 } }, { "stove", new[] { 1, 1 } }, { "vendor", new[] { 2, 2 } }, { "doctor", new[] { 2, 2 } },
            { "mine", new[] { 1, 1 } }, { "trap", new[] { 1, 1 } }, { "spikes", new[] { 1, 1 } }, { "fspikes", new[] { 1, 1 } }, { "trcans", new[] { 1, 2 } },
            { "trridge", new[] { 1, 1 } }, { "trplate", new[] { 1, 1 } }, { "trlaser", new[] { 1, 1 } }, { "damshot", new[] { 1, 2 } }, { "damgren", new[] { 1, 1 } }, { "expl1", new[] { 1, 1 } },
            { "robocell", new[] { 2, 2 } }, { "alarm", new[] { 1, 2 } }, { "radbarrel", new[] { 1, 2 } }, { "radbigbarrel", new[] { 2, 3 } },
            { "transm", new[] { 1, 1 } }, { "moln1", new[] { 1, 3 } }, { "elpanel", new[] { 1, 1 } }, { "knop1", new[] { 1, 1 } }, { "knop2", new[] { 1, 1 } }, { "knop3", new[] { 1, 1 } }, { "knop4", new[] { 1, 1 } },
            { "turret", new[] { 1, 1 } }, { "landturret", new[] { 1, 2 } }, { "armturret", new[] { 1, 2 } }, { "hturret", new[] { 1, 1 } }, { "wturret", new[] { 1, 1 } }, { "cturret", new[] { 1, 1 } },
            { "raider", new[] { 2, 2 } }, { "slaver", new[] { 2, 2 } }, { "merc", new[] { 2, 2 } }, { "zebra", new[] { 2, 2 } }, { "zombie", new[] { 2, 2 } },
            { "alicorn", new[] { 2, 3 } }, { "hellhound", new[] { 2, 3 } }, { "necros", new[] { 2, 2 } }, { "robot", new[] { 2, 2 } }, { "protect", new[] { 2, 2 } }, { "gutsy", new[] { 2, 2 } }, { "eqd", new[] { 2, 2 } },
            { "bloat", new[] { 1, 1 } }, { "tarakan", new[] { 1, 1 } }, { "rat", new[] { 1, 1 } }, { "ant", new[] { 1, 1 } }, { "molerat", new[] { 2, 1 } }, { "scorp", new[] { 2, 1 } },
            { "slime", new[] { 1, 1 } }, { "bloodwing", new[] { 1, 1 } }, { "fish", new[] { 1, 1 } }, { "roller", new[] { 1, 1 } }, { "spritebot", new[] { 1, 1 } }, { "msp", new[] { 1, 1 } }, { "vortex", new[] { 1, 1 } }
        };

        public static bool WallFixture(string id)
        {
            return id == "term1" || id == "term2" || id == "term3" || id == "knop1" || id == "knop3" || id == "wallsafe" || id == "elpanel";
        }

        public RRPopulation(RRArchitecture plan, Func<double> rnd, int n, int depth, bool peaceful, bool safe, GenerationContext ctx = null)
        {
            p = plan;
            random = rnd;
            context = ctx;
            prefix = "rrp_" + n + "_";
            stage = Math.Max(0, depth);

            double dif = 8 + 2 * stage;
            int parity = n;
            ecology = new RREcology(p.theme, dif, parity, rnd);

            for (int slot = 0; slot < 22; slot++)
            {
                if (p.ports[slot] >= 2)
                {
                    PortRect b = RRPorts.Rect(slot, p.ports[slot]);
                    if (slot < 6) b.x0 = 42;
                    else if (slot >= 11 && slot < 17) b.x1 = 5;
                    else if (slot >= 17) b.y1 = 2;
                    else b.y0 = 22;

                    for (int y = b.y0; y <= b.y1; y++)
                    {
                        for (int x = b.x0; x <= b.x1; x++)
                        {
                            p.reserved.Add((y, x));
                        }
                    }
                }
            }

            double roll = random();
            mood = peaceful ? "showroom" : (safe ? "arrival" : (roll < 0.18 ? "quiet" : (roll < 0.42 ? "cache" : (roll < 0.77 ? "patrol" : "guarded"))));
        }

        private void Shuffle<T>(List<T> a)
        {
            for (int i = a.Count - 1; i > 0; i--)
            {
                int j = (int)(random() * (i + 1));
                (a[i], a[j]) = (a[j], a[i]);
            }
        }

        private bool Dangerous(string kind)
        {
            return kind == "enemy" || kind == "security" || kind == "hazard" || kind == "trigger" || kind == "damager" ||
                   kind == "special" || kind == "ambient" || kind == "reinforcement";
        }

        public bool Fit(string id, int x, int y, string mount, string kind)
        {
            if (!SIZES.TryGetValue(id, out var d)) return false;
            int w = d[0], h = d[1];
            if (x < 3 || x + w > 45 || y - h < 1 || y > 23) return false;
            if (Dangerous(kind) && Math.Abs(x - p.spawn.x) + Math.Abs(y - p.spawn.y) < 8) return false;

            for (int yy = y - h + 1; yy <= y; yy++)
            {
                for (int xx = x; xx < x + w; xx++)
                {
                    string code = p.grid[yy, xx];
                    if (p.Solid(xx, yy) || used.Contains((yy, xx)) || (p.reserved.Contains((yy, xx)) && !(mount == "swim" && code.Contains("*"))))
                    {
                        return false;
                    }
                    if (code.Contains("-") || code.Contains("А") || code.Contains("Г")) return false;
                    if (mount != "swim" && (code.Contains("*") || code.Contains(",") || code.Contains(";"))) return false;
                }
            }

            if (mount == "floor")
            {
                for (int xx = x; xx < x + w; xx++)
                {
                    if (!p.Support(xx, y)) return false;
                }
            }
            if (mount == "ceiling" && !p.Solid(x, y - h)) return false;
            if (mount == "wall" && !p.Solid(x - 1, y) && !p.Solid(x + w, y)) return false;
            if (mount == "fixture")
            {
                string cell = p.grid[y, x];
                if (cell.Length < 2 || cell[1] < 'A' || cell[1] > 'T') return false;
                if (!p.Support(x, y + 1)) return false;
            }
            return true;
        }

        public List<CandidatePosition> Positions(string id, string mount, string kind, PartitionRegion region = null, bool randomize = true)
        {
            var a = new List<CandidatePosition>();
            int w = SIZES[id][0];

            for (int y = 2; y <= 23; y++)
            {
                for (int x = 3; x <= 44; x++)
                {
                    if (region != null && (x < region.x0 || x + w - 1 > region.x1 || y < region.top || y > region.floor)) continue;
                    if (Fit(id, x, y, mount, kind))
                    {
                        a.Add(new CandidatePosition
                        {
                            x = x,
                            y = y,
                            floorY = mount == "fixture" ? y + 1 : y
                        });
                    }
                }
            }

            if (randomize) Shuffle(a);
            return a;
        }

        public PopulationObject Put(string id, CandidatePosition pos, string kind, string mount = "floor", Dictionary<string, string> attrs = null)
        {
            var o = new PopulationObject
            {
                id = id,
                x = pos.x,
                y = pos.y,
                rrContent = kind,
                rrMount = mount,
                uid = prefix + (serial++)
            };

            if (attrs != null)
            {
                foreach (var kvp in attrs) o.attrs[kvp.Key] = kvp.Value;
            }

            if (mount == "fixture")
            {
                o.floorY = pos.floorY;
                o.lift = pos.floorY - pos.y;
                if (pos.operatorNode >= 0) o.operatorNode = pos.operatorNode;
            }
            if (id == "armturret") o.turn = pos.x < 24 ? 1 : -1;
            if (id == "wturret") o.vis = p.Solid(pos.x - 1, pos.y) ? "right" : "left";

            objects.Add(o);
            var d = SIZES[id];
            for (int y = pos.y - d[1] + 1; y <= pos.y; y++)
            {
                for (int x = pos.x; x < pos.x + d[0]; x++)
                {
                    used.Add((y, x));
                    p.reserved.Add((y, x));
                }
            }
            return o;
        }

        public void Build()
        {
            uint seedVal = context != null && context.seed.HasValue ? context.seed.Value : (uint)(random() * 4294967295.0);
            var contentRng = new RRSeed(seedVal).Fork("content-v13");
            contentPlan = new RRContentPlan(p, this, contentRng, context);
            contentPlan.Build();
        }
    }
}
