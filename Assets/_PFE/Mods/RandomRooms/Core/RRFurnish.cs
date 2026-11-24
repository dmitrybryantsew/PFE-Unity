using System;
using System.Collections.Generic;

namespace PFE.Mods.RandomRooms.Core
{
    public class FurnishObject
    {
        public string id;
        public int x;
        public int y;
        public string fixture; // "door", "hatch", "window", or null
    }

    public class BackDecoration
    {
        public string id;
        public int x;
        public int y;
    }

    public class CoverageInfo
    {
        public int region;
        public string role;
        public int groups;
        public int target;
    }

    internal class FurnishKitGroup
    {
        public int w;
        public int h;
        public List<(string id, int x, int y)> obj = new();
        public List<(string id, int x, int y)> back = new();
    }

    /// <summary>
    /// Semantic furniture kits and background scenery placement.
    /// Pure C#, zero UnityEngine references.
    /// </summary>
    public class RRFurnish
    {
        public List<FurnishObject> objects = new();
        public List<BackDecoration> backs = new();
        public List<CoverageInfo> coverage = new();
        private readonly RRArchitecture plan;
        private readonly Func<double> rnd;
        private readonly HashSet<(int y, int x)> occupied = new();
        private readonly HashSet<(int f, int x)> backUsed = new();

        public static readonly Dictionary<string, int[]> OBJECTS = new()
        {
            { "table", new[] { 2, 1, 0 } }, { "table1", new[] { 2, 1, 0 } }, { "table2", new[] { 2, 1, 0 } }, { "filecab", new[] { 1, 2, 0 } },
            { "bookcase", new[] { 2, 3, 0 } }, { "locker", new[] { 2, 3, 0 } }, { "couch", new[] { 2, 1, 0 } }, { "bed", new[] { 4, 1, 0 } },
            { "mcrate1", new[] { 2, 2, 0 } }, { "box", new[] { 2, 2, 0 } }, { "bigbox", new[] { 3, 2, 0 } }, { "instr1", new[] { 2, 2, 0 } },
            { "bigmed", new[] { 2, 3, 0 } }, { "medbox", new[] { 1, 1, 1 } }, { "wcup", new[] { 1, 1, 1 } }, { "wallcab", new[] { 1, 1, 1 } },
            { "cup", new[] { 2, 2, 0 } }, { "ccup", new[] { 1, 1, 0 } }, { "tap", new[] { 1, 2, 0 } }, { "fridge", new[] { 1, 2, 0 } }, { "trash", new[] { 1, 1, 0 } },
            { "woodbox", new[] { 2, 2, 0 } }, { "mcrate2", new[] { 2, 2, 0 } }, { "tumba1", new[] { 1, 1, 0 } }, { "tumba2", new[] { 1, 1, 0 } }
        };

        public static readonly Dictionary<string, int[]> BACKS = new()
        {
            { "stwindow", new[] { 4, 2 } }, { "bwindow", new[] { 4, 2 } }, { "stlight3", new[] { 2, 1 } }, { "stlight4", new[] { 2, 1 } },
            { "light2", new[] { 2, 1 } }, { "light3", new[] { 2, 1 } }, { "light4", new[] { 2, 1 } }, { "wires1", new[] { 1, 2 } }, { "wires2", new[] { 1, 2 } },
            { "pult", new[] { 2, 1 } }, { "monitor", new[] { 2, 1 } }, { "zavod2", new[] { 3, 2 } }, { "pipe3", new[] { 2, 1 } }, { "pipe4", new[] { 1, 2 } },
            { "potek", new[] { 10, 2 } }, { "vent", new[] { 1, 1 } }, { "bvent", new[] { 4, 2 } }, { "depot", new[] { 2, 3 } }, { "storage", new[] { 4, 3 } },
            { "electro", new[] { 3, 4 } }, { "clock", new[] { 1, 1 } }, { "poster", new[] { 2, 2 } }, { "stillage", new[] { 3, 3 } },
            { "fwindow", new[] { 6, 6 } }, { "stabledoor", new[] { 3, 3 } }, { "stlight1", new[] { 1, 1 } }, { "light1", new[] { 1, 1 } },
            { "konstr", new[] { 1, 4 } }, { "vkonstr", new[] { 2, 3 } }, { "hkonstr", new[] { 6, 2 } }, { "railing", new[] { 4, 1 } },
            { "pipe1", new[] { 10, 2 } }, { "pipe2", new[] { 12, 3 } }, { "pipes", new[] { 3, 3 } }, { "stok", new[] { 3, 3 } }, { "stok2", new[] { 2, 2 } },
            { "plesen", new[] { 10, 2 } }, { "moss", new[] { 10, 2 } }, { "zavod1", new[] { 4, 3 } }, { "fuse", new[] { 4, 3 } },
            { "heap1", new[] { 6, 3 } }, { "heap2", new[] { 6, 2 } }, { "heap3", new[] { 3, 1 } }, { "swindow", new[] { 2, 1 } }, { "hole", new[] { 10, 10 } }
        };

        public RRFurnish(RRArchitecture p, Func<double> random)
        {
            plan = p;
            rnd = random;
        }

        private FurnishKitGroup Group(int w, int h, (string id, int x, int y)[] obj, (string id, int x, int y)[] back)
        {
            var g = new FurnishKitGroup { w = w, h = h };
            if (obj != null) g.obj.AddRange(obj);
            if (back != null) g.back.AddRange(back);
            return g;
        }

        private FurnishKitGroup Kit(string role, bool compact = false)
        {
            string theme = plan.theme;
            string light = plan.scene.light;

            if (role == "street" || role == "roof")
            {
                return compact ? Group(3, 2, Array.Empty<(string, int, int)>(), new[] { ("heap3", 0, 0) }) :
                    Group(7, 3, Array.Empty<(string, int, int)>(), new[] { ("heap3", 0, 0), ("railing", 3, 0) });
            }

            if (theme == "sewer")
            {
                if (role == "canal" || role == "canal_walk")
                {
                    return Group(3, 3, Array.Empty<(string, int, int)>(), new[] { ("light1", 0, -3), ("pipe4", 2, -2) });
                }
                if (compact)
                {
                    return role == "store" ? Group(2, 3, new[] { ("locker", 0, 0) }, Array.Empty<(string, int, int)>()) :
                        Group(2, 2, new[] { ("instr1", 0, 0) }, new[] { ("vent", 0, -2) });
                }
                if (role == "control")
                {
                    return Group(7, 4, new[] { ("instr1", 0, 0), ("table1", 3, 0) },
                        new[] { ("pult", 3, -2), ("pipe4", 6, -3), ("pipe4", 6, -1), ("light1", 2, -4) });
                }
                if (role == "store")
                {
                    return Group(6, 4, new[] { ("locker", 0, 0), ("mcrate2", 4, 0) },
                        new[] { ("storage", 2, -2), ("light1", 3, -4) });
                }
                return Group(6, 4, new[] { ("table1", 0, 0), ("instr1", 4, 0) },
                    new[] { ("pipe4", 3, -3), ("pipe4", 3, -1), ("vent", 0, -3), ("light1", 5, -4) });
            }

            if (compact)
            {
                if (role == "living")
                {
                    return theme == "stable" && rnd() < 0.7 ?
                        Group(4, 1, new[] { ("bed", 0, 0) }, Array.Empty<(string, int, int)>()) :
                        Group(3, 1, new[] { ("couch", 0, 0), (theme == "stable" ? "tumba2" : "tumba1", 2, 0) }, Array.Empty<(string, int, int)>());
                }
                if (role == "office")
                {
                    return Group(3, 2, new[] { (theme == "stable" ? "table2" : "table", 0, 0), ("filecab", 2, 0) }, new[] { ("clock", 0, -2) });
                }
                if (role == "kitchen")
                {
                    return Group(3, 2, new[] { ("ccup", 0, 0), ("tap", 1, 0), ("fridge", 2, 0) }, new[] { ("vent", 1, -2) });
                }
                if (role == "medical")
                {
                    return Group(2, 3, new[] { ("bigmed", 0, 0) }, Array.Empty<(string, int, int)>());
                }
                if (role == "control")
                {
                    return Group(2, 2, new[] { ("table1", 0, 0) }, new[] { ("monitor", 0, -2), ("pult", 0, -1) });
                }
                if (role == "hall" || role == "corridor")
                {
                    return Group(2, 1, new[] { ("couch", 0, 0) }, Array.Empty<(string, int, int)>());
                }
                string small = role == "service" || role == "workshop" ? "instr1" :
                    (theme == "stable" ? "mcrate1" : (theme == "plant" ? "woodbox" : "mcrate2"));
                return Group(2, 2, new[] { (small, 0, 0) }, Array.Empty<(string, int, int)>());
            }

            if (role == "office")
            {
                if (theme == "stable")
                {
                    return Group(7, 4, new[] { ("table2", 1, 0), ("filecab", 4, 0), ("filecab", 5, 0) },
                        new[] { ("stwindow", 0, -3), ("wires1", 6, -3), ("wires1", 6, -1), ("stlight4", 1, -4) });
                }
                if (rnd() < 0.5)
                {
                    return Group(7, 4, new[] { ("table", 1, 0), ("filecab", 4, 0), ("filecab", 5, 0) },
                        new[] { ("bwindow", 0, -3), ("clock", 5, -3), (light, 2, -4) });
                }
                return Group(7, 4, new[] { ("bookcase", 0, 0), ("table", 3, 0), ("trash", 6, 0) },
                    new[] { ("poster", 3, -3), (light, 4, -4) });
            }

            if (role == "living")
            {
                if (theme == "stable")
                {
                    return rnd() < 0.65 ?
                        Group(7, 4, new[] { ("bed", 0, 0), ("tumba2", 4, 0), ("cup", 5, 0) }, new[] { ("stwindow", 1, -3), ("stlight3", 4, -4) }) :
                        Group(7, 4, new[] { ("couch", 0, 0), ("table2", 3, 0), ("tumba2", 6, 0) }, new[] { ("stwindow", 0, -3), ("stlight3", 4, -4) });
                }
                if (rnd() < 0.55)
                {
                    return Group(8, 3, new[] { ("couch", 0, 0), ("table1", 3, 0), ("cup", 6, 0) },
                        new[] { ("poster", 0, -3), (light, 4, -3) });
                }
                return Group(6, 4, new[] { ("table1", 0, 0), ("ccup", 2, 0), ("tap", 3, 0), ("fridge", 5, 0), ("wcup", 2, -3), ("wcup", 3, -3) },
                    new[] { (light, 0, -4) });
            }

            if (role == "kitchen")
            {
                return Group(7, 4, new[] { (theme == "stable" ? "table2" : "table1", 0, 0), ("ccup", 3, 0), ("tap", 4, 0), ("fridge", 6, 0), ("wcup", 3, -3), ("wcup", 4, -3) },
                    new[] { (light, 0, -4) });
            }

            if (role == "workshop" || role == "control" || role == "service")
            {
                if (theme == "plant" && role == "workshop")
                {
                    return Group(9, 5, new[] { ("instr1", 5, 0), ("box", 7, 0) },
                        new[] { ("zavod1", 0, -2), ("pult", 5, -2), ("light4", 3, -5), ("konstr", 8, -4) });
                }
                if (role == "control")
                {
                    return Group(8, 4, new[] { ("bigbox", 0, 0) },
                        new[] { ("electro", 0, -3), ("pult", 3, -1), ("monitor", 3, -2), ("monitor", 3, -3), ("electro", 5, -3), (light, 3, -4) });
                }
                return Group(7, 4, new[] { ("table1", 0, 0), ("instr1", 4, 0) },
                    new[] { ("vent", 1, -3), ("pipe4", 6, -3), ("pipe4", 6, -1), (light, 2, -4) });
            }

            if (role == "medical")
            {
                return Group(7, 4, new[] { ("table", 0, 0), ("bigmed", 4, 0), ("medbox", 2, -2) },
                    new[] { ("vent", 0, -3), ("pipe4", 6, -3), ("pipe4", 6, -1), (light, 2, -4) });
            }

            if (role == "hall" || role == "corridor")
            {
                return theme == "stable" ?
                    Group(6, 4, new[] { ("couch", 0, 0), ("tumba2", 4, 0) }, new[] { ("stwindow", 0, -3), (light, 3, -4) }) :
                    Group(5, 3, new[] { ("trash", 3, 0) }, new[] { (light, 1, -3) });
            }

            int count = 2 + (int)(rnd() * 2);
            var oList = new List<(string, int, int)>();
            var bList = new List<(string, int, int)>();
            for (int i = 0; i < count; i++)
            {
                oList.Add((theme == "stable" ? "mcrate1" : (theme == "plant" ? "woodbox" : "mcrate2"), i * 3, 0));
                bList.Add((theme == "mane" ? "stillage" : "depot", i * 3, -2));
            }
            bList.Add((light, 1, -4));
            return Group(count * 3, 4, oList.ToArray(), bList.ToArray());
        }

        private bool FitsObject(string id, int x, int y, HashSet<(int, int)> extra = null)
        {
            if (!OBJECTS.TryGetValue(id, out var d)) return false;
            for (int yy = y - d[1] + 1; yy <= y; yy++)
            {
                for (int xx = x; xx < x + d[0]; xx++)
                {
                    if (plan.Solid(xx, yy) || plan.reserved.Contains((yy, xx)) || occupied.Contains((yy, xx)) ||
                        (extra != null && extra.Contains((yy, xx))))
                    {
                        return false;
                    }
                    string code = plan.grid[yy, xx];
                    if (code.Contains("-") || code.Contains("А") || code.Contains("Г")) return false;
                }
            }

            if (d[2] == 0)
            {
                for (int xx = x; xx < x + d[0]; xx++)
                {
                    if (!plan.Support(xx, y)) return false;
                }
            }
            return true;
        }

        private void Mark(string id, int x, int y, HashSet<(int, int)> dst)
        {
            if (!OBJECTS.TryGetValue(id, out var d)) return;
            for (int yy = y - d[1] + 1; yy <= y; yy++)
            {
                for (int xx = x; xx < x + d[0]; xx++)
                {
                    dst.Add((yy, xx));
                }
            }
        }

        private bool FitsBack(string id, int x, int y)
        {
            if (!BACKS.TryGetValue(id, out var d)) return false;
            for (int yy = y; yy < y + d[1]; yy++)
            {
                for (int xx = x; xx < x + d[0]; xx++)
                {
                    if (plan.Solid(xx, yy)) return false;
                }
            }
            return true;
        }

        private bool Facility(string id, int x, int y, PartitionRegion r)
        {
            if (!BACKS.TryGetValue(id, out var d) || x < r.x0 || y < r.top || x + d[0] - 1 > r.x1 || y + d[1] - 1 > r.floor)
            {
                return false;
            }

            for (int yy = y; yy < y + d[1]; yy++)
            {
                for (int xx = x; xx < x + d[0]; xx++)
                {
                    if (plan.Solid(xx, yy)) return false;
                }
            }

            backs.Add(new BackDecoration { id = id, x = x, y = y });
            return true;
        }

        private void Facilities()
        {
            foreach (var r in plan.regions)
            {
                int h = r.floor - r.top + 1;
                int w = r.x1 - r.x0 + 1;

                if (plan.theme == "sewer")
                {
                    if (r.role == "canal" || r.role == "service")
                    {
                        for (int x = r.x0; x < r.x1; x += 2)
                        {
                            Facility("pipe3", x, r.top + 1, r);
                            if (h >= 8) Facility("pipe3", x, r.top + 2, r);
                        }
                        for (int y = r.top + 3; y < r.floor; y += 2) Facility("pipe4", r.x0 + 1, y, r);
                        for (int x = r.x0; x + 9 <= r.x1; x += 10) Facility(rnd() < 0.5 ? "potek" : "plesen", x, r.floor - 3, r);
                        if (w >= 12 && h >= 9)
                        {
                            for (int x = r.x0 + 1; x + 9 <= r.x1; x += 10) Facility("pipe1", x, r.top + 4, r);
                        }
                        if (r.role == "canal")
                        {
                            foreach (var basin in plan.pools)
                            {
                                if (basin.x0 < r.x0 || basin.x1 > r.x1 || basin.bottom != r.floor) continue;
                                Facility("stok", basin.x0, basin.top - 2, r);
                                if (basin.x1 - basin.x0 > 13) Facility("stok2", basin.x1 - 2, basin.top - 1, r);
                                for (int x = basin.x0; x + 3 <= basin.x1; x += 4) Facility("railing", x, basin.deck - 1, r);
                                for (int x = r.x0; x + 9 <= r.x1; x += 10) Facility("moss", x, basin.top - 1, r);
                            }
                        }
                    }
                    Facility("light1", r.x0 + w / 2, r.top, r);
                }
                else if (plan.theme == "plant")
                {
                    if (r.role == "workshop" || r.role == "warehouse")
                    {
                        if (h >= 9) for (int x = r.x0 + 2; x + 5 <= r.x1; x += 9) Facility("fwindow", x, r.top + 1, r);
                        for (int x = r.x0; x <= r.x1; x += Math.Max(12, w - 1))
                        {
                            for (int y = r.top; y + 3 <= r.floor; y += 4) Facility("konstr", x, y, r);
                        }
                        for (int x = r.x0; x + 5 <= r.x1; x += 6) Facility("hkonstr", x, r.top, r);
                        for (int x = r.x0 + 2; x + 3 <= r.x1; x += plan.modern ? 6 + (int)(rnd() * 5) : 7)
                        {
                            Facility(r.role == "warehouse" ? "storage" : "zavod1", x, r.floor - 2, r);
                        }
                    }
                    Facility("light4", r.x0 + w / 2 - 1, r.top, r);
                }
                else if (plan.theme == "stable")
                {
                    for (int y = r.top; y + 3 <= r.floor; y += 4) Facility("konstr", r.x0, y, r);
                    if (h >= 5 && w >= 7 && r.role != "control") Facility("stabledoor", r.x1 - 3, r.floor - 2, r);
                    if (r.role == "hall")
                    {
                        for (int x = r.x0 + 2; x + 3 <= r.x1; x += 7) Facility("stwindow", x, r.top + 2, r);
                        for (int x = r.x0 + 2; x + 2 <= r.x1; x += 8) Facility("stabledoor", x, r.floor - 2, r);
                    }
                    Facility("stlight4", r.x0 + w / 2 - 1, r.top, r);
                }
                else
                {
                    if (r.role == "street" || r.role == "roof")
                    {
                        for (int x = r.x0 + 1; x + 3 <= r.x1; x += 7) Facility("railing", x, r.floor, r);
                        if (w >= 8) Facility("heap2", r.x0 + 1, r.floor - 1, r);
                    }
                    else
                    {
                        for (int x = r.x0 + 2; x + 1 <= r.x1; x += 5) Facility("swindow", x, r.top + 2, r);
                        if (w >= 10 && h >= 10) Facility("hole", r.x0, r.top, r);
                        Facility("heap3", r.x1 - 2, r.floor, r);
                        Facility("light3", r.x0 + w / 2 - 1, r.top, r);
                    }
                }
            }
        }

        private bool Place(FurnishKitGroup g, int x, int f)
        {
            var extra = new HashSet<(int, int)>();
            var objList = new List<(string id, int x, int y)>(g.obj);
            var backList = new List<(string id, int x, int y)>(g.back);

            if (plan.modern && rnd() < 0.5)
            {
                for (int i = 0; i < objList.Count; i++)
                {
                    var a = objList[i];
                    objList[i] = (a.id, g.w - OBJECTS[a.id][0] - a.x, a.y);
                }
                for (int i = 0; i < backList.Count; i++)
                {
                    var a = backList[i];
                    backList[i] = (a.id, g.w - BACKS[a.id][0] - a.x, a.y);
                }
            }

            foreach (var a in objList)
            {
                if (!FitsObject(a.id, x + a.x, f + a.y, extra)) return false;
                Mark(a.id, x + a.x, f + a.y, extra);
            }

            for (int ix = x; ix < x + g.w; ix++)
            {
                if (backUsed.Contains((f, ix))) return false;
            }
            for (int ix = x; ix < x + g.w; ix++)
            {
                backUsed.Add((f, ix));
            }

            foreach (var a in objList)
            {
                objects.Add(new FurnishObject { id = a.id, x = x + a.x, y = f + a.y });
                Mark(a.id, x + a.x, f + a.y, occupied);
            }

            foreach (var a in backList)
            {
                if (FitsBack(a.id, x + a.x, f + a.y))
                {
                    backs.Add(new BackDecoration { id = a.id, x = x + a.x, y = f + a.y });
                }
            }

            return true;
        }

        public void Build()
        {
            objects.Add(new FurnishObject { id = "player", x = plan.spawn.x, y = plan.spawn.y });

            foreach (var d in plan.doors)
            {
                objects.Add(new FurnishObject { id = d.id, x = d.x, y = d.y, fixture = "door" });
            }
            foreach (var d in plan.hatches)
            {
                objects.Add(new FurnishObject { id = d.id, x = d.x, y = d.y, fixture = "hatch" });
            }
            foreach (var d in plan.windows)
            {
                objects.Add(new FurnishObject { id = d.id, x = d.x, y = d.y, fixture = "window" });
            }

            foreach (var r in plan.regions)
            {
                if (r.role == "shaft") continue;
                int width = r.x1 - r.x0 + 1;
                int groups = width > 26 ? 3 : (width > 11 ? 2 : 1);
                if (plan.modern && width > 11) groups = Math.Max(1, groups + (rnd() < 0.4 ? -1 : 0));

                int placedGroups = 0;
                for (int n = 0; n < groups; n++)
                {
                    bool placed = false;
                    for (int trial = 0; trial < 24 && !placed; trial++)
                    {
                        var g = Kit(r.role);
                        if (g.w > width || g.h > r.floor - r.top) continue;
                        int x = r.x0 + (int)(rnd() * (width - g.w + 1));
                        placed = Place(g, x, r.floor);
                    }

                    if (!placed && width >= 3)
                    {
                        for (int variant = 0; variant < 2 && !placed; variant++)
                        {
                            var g = Kit(r.role, true);
                            if (g.w > width || g.h > r.floor - r.top) continue;
                            int slots = width - g.w + 1;
                            int start = (int)(rnd() * slots);
                            for (int trial = 0; trial < slots && !placed; trial++)
                            {
                                placed = Place(g, r.x0 + (start + trial) % slots, r.floor);
                            }
                        }
                    }

                    if (placed) placedGroups++;
                }

                coverage.Add(new CoverageInfo { region = r.id, role = r.role, groups = placedGroups, target = groups });
            }

            Facilities();
        }
    }
}
