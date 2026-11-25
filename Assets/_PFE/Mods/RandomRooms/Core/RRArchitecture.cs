using System;
using System.Collections.Generic;

namespace PFE.Mods.RandomRooms.Core
{
    public class GenerationContext
    {
        public string contentVersion = "13";
        public string form;
        public CityInfo city;
        public string partition = "rules";
        public uint? seed;
    }

    public class LadderFixture
    {
        public int x;
        public int top;
        public int bottom;
    }

    public class StairFixture
    {
        public int x;
        public int top;
        public int bottom;
        public int dir; // 1 or -1
    }

    public class DoorFixture
    {
        public string id;
        public int x;
        public int y;
    }

    public class HatchFixture
    {
        public string id;
        public int x;
        public int y;
    }

    public class WindowFixture
    {
        public string id;
        public int x;
        public int y;
    }

    public class PoolInfo
    {
        public int x0;
        public int x1;
        public int top;
        public int bottom;
        public int deck;
    }

    public class MergeInfo
    {
        public int a;
        public int b;
        public int x0;
        public int top;
        public int x1;
        public int floor;
        public string axis;
    }

    public class CirculationLink
    {
        public int a;
        public int b;
        public string kind;
        public int x;
        public int y;
    }

    public class CacheInfo
    {
        public int face;
        public int dir;
        public int x0;
        public int x1;
        public int top;
        public int floor;
        public PartitionRegion parent;
        public double score;
        public bool sealedCache;
        public int x;
        public int y;
    }

    internal class EdgeInfo
    {
        public PartitionRegion a;
        public PartitionRegion b;
        public bool vertical;
        public int x;
        public int x0;
        public int x1;
        public int y;
        public int y1;
        public int lo;
        public int hi;
        public double priority;
        public string kind;
    }

    /// <summary>
    /// Core architectural synthesis pipeline.
    /// Carves grid, MST circulation, ladders, stairs, doors, hatches, windows, pools, caches.
    /// Pure C#, zero UnityEngine references.
    /// </summary>
    public class RRArchitecture
    {
        public string[,] grid = new string[25, 48];

        /// <summary>
        /// Canvas width in tiles. Every canvas-boundary literal in this file is expressed against
        /// <see cref="W"/>/<see cref="H"/>; the default 48x25 reproduces the ported AS3 grid exactly
        /// (Sx/Sy are identity at the default size).
        /// </summary>
        public int W = 48;

        /// <summary>Canvas height in tiles. See <see cref="W"/>.</summary>
        public int H = 25;
        public List<PartitionRegion> regions = new();
        public List<LadderFixture> ladders = new();
        public List<StairFixture> stairs = new();
        public List<DoorFixture> doors = new();
        public List<HatchFixture> hatches = new();
        public List<WindowFixture> windows = new();
        public HashSet<(int y, int x)> reserved = new();
        public string wall;
        public string trim;
        public string[] backgrounds;
        public string theme;
        public string archetype;
        public int[] ports;
        public List<CirculationLink> links = new();
        public (int x, int y) spawn;
        public SceneProfile scene;
        public string sceneForm;
        public List<PoolInfo> pools = new();
        public PartitionInfo partitionInfo;
        public List<PartitionRegion> masses = new();
        public List<MergeInfo> merges = new();
        public bool modern;
        public List<CacheInfo> caches = new();
        public HashSet<int> hiddenCells = new();

        private readonly Func<double> rnd;
        private List<PartitionRegion> volumes = new();
        private double tallBias;

        public const string WALL_CHARS = "ABCDEFGHIJKLMNOPQRST";

        public RRArchitecture(Func<double> random)
        {
            rnd = random;
        }

        private int Pick(int lo, int hi)
        {
            return lo + (int)(rnd() * (hi - lo + 1));
        }

        /// <summary>
        /// A horizontal design coordinate (expressed on the 48-wide reference canvas) scaled to
        /// <see cref="W"/>. Identity at W = 48.
        /// </summary>
        private int Sx(int v)
        {
            return (int)Math.Round(v * (W - 2) / 46.0);
        }

        /// <summary>A vertical design coordinate on the 25-tall reference canvas, scaled to <see cref="H"/>.</summary>
        private int Sy(int v)
        {
            return (int)Math.Round(v * (H - 2) / 23.0);
        }

        private void Shuffle<T>(List<T> a)
        {
            for (int i = a.Count - 1; i > 0; i--)
            {
                int j = Pick(0, i);
                (a[i], a[j]) = (a[j], a[i]);
            }
        }

        public void Build(string biome, string requested = "", int[] boundary = null, GenerationContext context = null,
            int width = 0, int height = 0)
        {
            W = width > 0 ? Math.Max(RRSynth.MIN_GRID_W, width) : 48;
            H = height > 0 ? Math.Max(RRSynth.MIN_GRID_H, height) : 25;
            grid = new string[H, W];

            theme = biome;
            modern = context != null && context.contentVersion == "13";
            scene = RRScene.Profile(theme);
            sceneForm = scene.forms[Pick(0, scene.forms.Length - 1)];

            if (context != null && !string.IsNullOrEmpty(context.form) && Array.IndexOf(scene.forms, context.form) >= 0)
            {
                sceneForm = context.form;
            }
            if (theme == "mane" && context != null && context.city != null)
            {
                sceneForm = context.city.form;
            }

            wall = scene.wall;
            trim = scene.trim;
            backgrounds = scene.backgrounds;

            string[] kinds;
            if (theme == "stable") kinds = new[] { "atrium", "offices", "offices", "service", "warehouse" };
            else if (theme == "sewer") kinds = new[] { "service", "service", "workshop", "damaged", "warehouse" };
            else if (theme == "plant") kinds = new[] { "workshop", "workshop", "warehouse", "service", "atrium" };
            else kinds = new[] { "damaged", "damaged", "offices", "warehouse", "atrium" };

            archetype = kinds[Pick(0, kinds.Length - 1)];
            if (requested == "hall") requested = "atrium";
            if (requested == "corridor") requested = "service";
            if (Array.IndexOf(new[] { "atrium", "offices", "workshop", "damaged", "service", "warehouse", "connector" }, requested) >= 0)
            {
                archetype = requested;
            }

            ports = boundary != null ? (int[])boundary.Clone() : RRPorts.Sample(rnd);
            regions.Clear();
            ladders.Clear();
            stairs.Clear();
            doors.Clear();
            hatches.Clear();
            windows.Clear();
            pools.Clear();
            reserved.Clear();
            links.Clear();
            partitionInfo = null;
            masses.Clear();
            merges.Clear();
            caches.Clear();
            hiddenCells.Clear();

            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    grid[y, x] = wall;
                }
            }

            tallBias = scene.bias;
            volumes = new List<PartitionRegion>
            {
                new PartitionRegion { x0 = 1, top = 1, x1 = W - 2, floor = H - 2 }
            };

            if (context != null && context.partition == "rules")
            {
                var partition = new RRPartitionPlan(rnd);
                volumes = partition.Build(theme, sceneForm, ports, modern, W, H);
                partitionInfo = partition.info;
                masses = partition.masses;
            }
            else
            {
                SeedScene();
                RefineSpaces();
            }

            for (int i = 0; i < volumes.Count; i++)
            {
                var r = volumes[i];
                r.id = i;
                r.role = !string.IsNullOrEmpty(r.role) ? r.role : Purpose(r);
                r.bg = RRScene.Background(theme, r.role, rnd);
                Open(r.x0, r.top, r.x1, r.floor, r.bg);
                regions.Add(r);
            }

            ConnectBoundary();
            ConnectVolumes();

            foreach (var r in volumes)
            {
                if (r.floor - r.top >= 10 && r.x1 - r.x0 >= 14 &&
                    (r.role == "workshop" || r.role == "warehouse" || r.role == "hall" || r.role == "street" ||
                     (theme == "sewer" && r.role == "service")))
                {
                    Gallery(r);
                }
                ShapeCeiling(r);
            }

            AddSceneFeatures();
            AddHatches();
            AddWindows();
            ChooseSpawn();
            AuditLadders();
            AuditStairs();

            foreach (var fixture in doors)
            {
                int doorH = fixture.id == "stdoor" || fixture.id == "door3" ? 3 : 2;
                if (!Support(fixture.x, fixture.y) || !Solid(fixture.x, fixture.y - doorH))
                {
                    throw new InvalidOperationException("Unsupported door frame after circulation");
                }
            }

            AuditBoundary();
            RRTraversal.Check(this);
            if (pools.Count > 0) RRTraversal.Check(this, true);

            for (int y = 1; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    if (Solid(x, y) && !Solid(x, y - 1))
                    {
                        grid[y, x] = trim;
                    }
                }
            }

            DressMaterials();
            if (modern) new RRMassDetail(this, rnd).Build();
        }

        private string Purpose(PartitionRegion r)
        {
            string[] roles;
            if (theme == "sewer") roles = new[] { "service", "service", "service", "control", "store" };
            else if (theme == "plant") roles = new[] { "workshop", "workshop", "store", "control", "office" };
            else if (theme == "stable") roles = sceneForm == "quarters" ? new[] { "living", "living", "kitchen", "office" } :
                new[] { "office", "medical", "service", "control", "store" };
            else roles = new[] { "living", "living", "kitchen", "office", "office", "store" };

            return roles[Pick(0, roles.Length - 1)];
        }

        private PartitionRegion[] Divide(PartitionRegion r, bool vertical, int lo, int hi)
        {
            var cuts = new List<int>();
            for (int c = lo; c <= hi; c++) if (CanSplit(r, vertical, c)) cuts.Add(c);

            if (cuts.Count == 0)
            {
                lo = vertical ? r.x0 + 8 : r.top + 5;
                hi = vertical ? r.x1 - 8 : r.floor - 5;
                for (int c = lo; c <= hi; c++) if (CanSplit(r, vertical, c)) cuts.Add(c);
            }

            if (cuts.Count == 0) throw new InvalidOperationException("Scene cannot meet shared ports");

            int cut = cuts[Pick(0, cuts.Count - 1)];
            var a = new PartitionRegion { x0 = r.x0, top = r.top, x1 = vertical ? cut - 1 : r.x1, floor = vertical ? r.floor : cut - 1 };
            var b = new PartitionRegion { x0 = vertical ? cut + 1 : r.x0, top = vertical ? r.top : cut + 1, x1 = r.x1, floor = r.floor };

            volumes.Remove(r);
            volumes.Add(a);
            volumes.Add(b);
            return new[] { a, b };
        }

        private void SplitWing(PartitionRegion r, string[] roles)
        {
            var parts = Divide(r, false, r.top + 6, r.floor - 6);
            parts[0].role = roles[0];
            parts[1].role = roles[1];
        }

        private void Cells(PartitionRegion r, int count, string[] roles)
        {
            var todo = new List<PartitionRegion> { r };
            while (todo.Count < count)
            {
                todo.Sort((a, b) => (b.x1 - b.x0).CompareTo(a.x1 - a.x0));
                var major = todo[0];
                todo.RemoveAt(0);

                if (major.x1 - major.x0 < 17)
                {
                    todo.Add(major);
                    break;
                }

                int mid = (major.x0 + major.x1) / 2;
                var pair = Divide(major, true, Math.Max(major.x0 + 8, mid - 3), Math.Min(major.x1 - 8, mid + 3));
                todo.Add(pair[0]);
                todo.Add(pair[1]);
            }

            for (int i = 0; i < todo.Count; i++)
            {
                todo[i].role = roles[i % roles.Length];
            }
        }

        private PartitionRegion CentralBay(string role)
        {
            var left = Divide(volumes[0], true, Sx(12), Sx(16));
            var right = Divide(left[1], true, Sx(30), Sx(34));
            var center = right[0];
            center.role = role;
            SplitWing(left[0], theme == "stable" ? new[] { "office", "living" } : new[] { "control", "store" });
            SplitWing(right[1], theme == "stable" ? new[] { "service", "office" } : new[] { "office", "service" });
            return center;
        }

        private void SeedScene()
        {
            PartitionRegion[] parts;
            PartitionRegion major;
            PartitionRegion[] other;

            if (theme == "plant")
            {
                if (sceneForm == "production") CentralBay("workshop");
                else if (sceneForm == "storage_hall")
                {
                    parts = Divide(volumes[0], false, Sy(7), Sy(10));
                    parts[1].role = "warehouse";
                    Cells(parts[0], Pick(2, 3), new[] { "store", "control", "office" });
                }
                else
                {
                    parts = Divide(volumes[0], true, Sx(17), Sx(23));
                    parts[1].role = "workshop";
                    other = Divide(parts[0], false, Sy(10), Sy(16));
                    other[0].role = "control";
                    Cells(other[1], 2, new[] { "service", "store" });
                }
                return;
            }

            if (theme == "stable")
            {
                if (sceneForm == "atrium_ring") CentralBay("hall");
                else if (sceneForm == "quarters")
                {
                    parts = Divide(volumes[0], false, Sy(6), Sy(9));
                    other = Divide(parts[1], false, Sy(12), Sy(15));
                    other[0].role = "corridor";
                    Cells(parts[0], Pick(3, 4), new[] { "living", "living", "office" });
                    Cells(other[1], Pick(2, 3), new[] { "living", "kitchen", "medical" });
                }
                else
                {
                    parts = Divide(volumes[0], true, Sx(12), Sx(18));
                    SplitWing(parts[0], new[] { "store", "service" });
                    other = Divide(parts[1], false, Sy(9), Sy(14));
                    Cells(other[0], 2, new[] { "control", "service" });
                    Cells(other[1], 2, new[] { "medical", "office" });
                }
                return;
            }

            if (theme == "sewer")
            {
                if (sceneForm == "cistern")
                {
                    parts = Divide(volumes[0], false, Sy(6), Sy(8));
                    Cells(parts[0], 2, new[] { "service", "service" });
                    foreach (var galleryRoom in volumes) if (galleryRoom.floor <= Sy(8)) galleryRoom.keep = true;
                    other = Divide(parts[1], true, Sx(11), Sx(14));
                    other[0].role = "control";
                    other[1].role = "canal";
                }
                else if (sceneForm == "dry_tunnels")
                {
                    parts = Divide(volumes[0], true, Sx(19), Sx(28));
                    SplitWing(parts[0], new[] { "service", "store" });
                    other = Divide(parts[1], false, Sy(6), Sy(10));
                    other[0].role = "service";
                    SplitWing(other[1], new[] { "control", "service" });
                }
                else
                {
                    parts = Divide(volumes[0], false, Sy(sceneForm == "canal_gallery" ? 12 : 7), Sy(sceneForm == "canal_gallery" ? 14 : 11));
                    Cells(parts[0], 2, new[] { "service", "control" });
                    if (sceneForm == "pump_chain")
                    {
                        other = Divide(parts[1], true, Sx(21), Sx(28));
                        other[0].role = "canal";
                        other[1].role = "control";
                    }
                    else
                    {
                        parts[1].role = "canal";
                    }
                }
                return;
            }

            if (sceneForm == "rooftops")
            {
                parts = Divide(volumes[0], false, Sy(6), Sy(9));
                major = parts[0];
                major.keep = true;
                major.role = "roof";
                parts[1].role = "service";
            }
            else if (sceneForm == "street_links")
            {
                parts = Divide(volumes[0], true, Sx(13), Sx(17));
                other = Divide(parts[1], true, Sx(30), Sx(34));
                other[0].role = "street";
                other[0].keep = true;
                parts[0].role = "store";
                other[1].role = "service";
            }
            else if (sceneForm == "offices")
            {
                parts = Divide(volumes[0], true, Sx(9), Sx(12));
                parts[0].role = "service";
                parts[1].role = "office";
            }
            else if (sceneForm == "commercial")
            {
                parts = Divide(volumes[0], false, Sy(10), Sy(14));
                parts[0].role = "office";
                parts[1].role = "store";
            }
            else if (sceneForm == "ruined")
            {
                parts = Divide(volumes[0], true, Sx(18), Sx(25));
                parts[0].role = "living";
                parts[1].role = "hall";
            }
            else
            {
                volumes[0].role = "living";
            }
        }

        private void RefineSpaces()
        {
            int limit = theme == "stable" ? Pick(11, 15) : (theme == "plant" ? Pick(8, 11) :
                (theme == "sewer" ? Pick(8, 11) : (sceneForm == "rooftops" || sceneForm == "street_links" ? Pick(7, 10) : Pick(10, 14))));

            for (int attempt = 0; attempt < 60 && volumes.Count < limit; attempt++)
            {
                var candidates = new List<(PartitionRegion r, List<(bool vertical, int c, int center)> cuts, double score)>();

                foreach (var r in volumes)
                {
                    if (r.keep || r.role == "canal" || r.role == "corridor") continue;
                    int w = r.x1 - r.x0 + 1, h = r.floor - r.top + 1;
                    bool hall = r.role == "workshop" || r.role == "warehouse" || r.role == "hall";
                    int maxArea = hall ? 210 : (theme == "sewer" ? 110 : 95);
                    int maxHeight = hall ? 12 : 8, maxWidth = hall ? 25 : 16;
                    if (w * h <= maxArea && h <= maxHeight && w <= maxWidth) continue;

                    bool[] directions = (h > maxHeight && h >= 11) ? new[] { false, true } : new[] { true, false };
                    var cuts = new List<(bool vertical, int c, int center)>();

                    foreach (bool vertical in directions)
                    {
                        int lo = vertical ? r.x0 + 7 : r.top + 5, hi = vertical ? r.x1 - 7 : r.floor - 5;
                        for (int c = lo; c <= hi; c++)
                        {
                            if (CanSplit(r, vertical, c))
                            {
                                cuts.Add((vertical, c, Math.Abs(c - (lo + hi) / 2)));
                            }
                        }
                        if (cuts.Count > 0) break;
                    }

                    if (cuts.Count > 0)
                    {
                        candidates.Add((r, cuts, (double)(w * h) / maxArea));
                    }
                }

                if (candidates.Count == 0) break;
                candidates.Sort((a, b) => b.score.CompareTo(a.score));

                var chosen = candidates[0];
                var chosenR = chosen.r;
                bool isHall = chosenR.role == "workshop" || chosenR.role == "warehouse" || chosenR.role == "hall";
                chosen.cuts.Sort((a, b) => a.center.CompareTo(b.center));

                var cut = chosen.cuts[Pick(0, Math.Min(3, chosen.cuts.Count - 1))];
                var pair = Divide(chosenR, cut.vertical, cut.c, cut.c);
                pair[0].role = chosenR.role;
                pair[1].role = chosenR.role;

                if (isHall)
                {
                    var small = (pair[0].x1 - pair[0].x0 + 1) * (pair[0].floor - pair[0].top + 1) <
                        (pair[1].x1 - pair[1].x0 + 1) * (pair[1].floor - pair[1].top + 1) ? pair[0] : pair[1];
                    small.role = theme == "plant" ? "control" : (theme == "stable" ? "office" : "store");
                }
                else if (chosenR.role == "living" && rnd() < 0.4)
                {
                    pair[1].role = rnd() < 0.5 ? "kitchen" : "office";
                }
                else if (chosenR.role == "service" && rnd() < 0.4)
                {
                    pair[1].role = "store";
                }
            }
        }

        private bool CanSplit(PartitionRegion r, bool vertical, int c)
        {
            for (int p = 0; p < 22; p++)
            {
                if (ports[p] >= 2)
                {
                    PortRect b = RRPorts.Rect(p, ports[p], W, H);
                    if (vertical && RRPorts.Vertical(p) && ((p >= 17 && r.top == 1) || (p < 11 && r.floor == H - 2)) &&
                        c >= b.x0 - 1 && c <= b.x1 + 1) return false;
                    if (!vertical && !RRPorts.Vertical(p) && ((p >= 11 && r.x0 == 1) || (p < 6 && r.x1 == W - 2)) &&
                        c >= b.y0 - 1 && c <= b.y1) return false;
                }
            }
            return true;
        }

        private EdgeInfo Adjacent(PartitionRegion a, PartitionRegion b)
        {
            int reach = partitionInfo != null ? 12 : 1;
            var left = a.x0 < b.x0 ? a : b;
            var right = left == a ? b : a;
            var above = a.top < b.top ? a : b;
            var below = above == a ? b : a;

            if (right.x0 - left.x1 >= 2 && right.x0 - left.x1 <= reach + 1)
            {
                int lo = Math.Max(a.top, b.top);
                int hi = Math.Min(a.floor, b.floor);
                if (hi - lo >= (partitionInfo != null ? 2 : 3) && EmptyBridge(left.x1 + 1, hi - 2, right.x0 - 1, hi, a, b))
                {
                    return new EdgeInfo
                    {
                        a = a,
                        b = b,
                        vertical = false,
                        x = left.x1 + 1,
                        x0 = left.x1 + 1,
                        x1 = right.x0 - 1,
                        lo = lo,
                        hi = hi
                    };
                }
            }

            if (below.top - above.floor >= 2 && below.top - above.floor <= reach + 1)
            {
                int lo = Math.Max(a.x0, b.x0);
                int hi = Math.Min(a.x1, b.x1);
                if (hi - lo >= 4 && EmptyBridge(lo, above.floor + 1, hi, below.top - 1, a, b))
                {
                    return new EdgeInfo
                    {
                        a = a,
                        b = b,
                        vertical = true,
                        y = above.floor + 1,
                        y1 = below.top - 1,
                        lo = lo,
                        hi = hi
                    };
                }
            }

            return null;
        }

        private bool EmptyBridge(int x0, int y0, int x1, int y1, PartitionRegion a, PartitionRegion b)
        {
            foreach (var r in volumes)
            {
                if (r != a && r != b && r.x0 <= x1 && r.x1 >= x0 && r.top <= y1 && r.floor >= y0)
                {
                    return false;
                }
            }
            return true;
        }

        private bool MergeSide(EdgeInfo e, int[] roomGroups)
        {
            if (partitionInfo == null || e.vertical || e.x0 != e.x1 || roomGroups[e.a.id] == roomGroups[e.b.id]) return false;
            string[] excluded = { "canal", "roof", "street", "medical" };
            if (Array.IndexOf(excluded, e.a.role) >= 0 || Array.IndexOf(excluded, e.b.role) >= 0) return false;

            int size = 0;
            foreach (int g in roomGroups) if (g == roomGroups[e.a.id] || g == roomGroups[e.b.id]) size++;
            if (size > 3) return false;

            bool irregular = e.a.top != e.b.top || e.a.floor != e.b.floor;
            if (rnd() > (irregular ? 0.6 : 0.22)) return false;

            for (int y = e.lo; y <= e.hi; y++)
            {
                if (reserved.Contains((y, e.x)) || ContainsLadderOrStairs(grid[y, e.x])) return false;
            }

            Open(e.x, e.lo, e.x, e.hi, e.a.bg);
            Reserve(e.x - 1, e.lo, e.x + 1, e.hi);
            merges.Add(new MergeInfo
            {
                a = e.a.id,
                b = e.b.id,
                x0 = e.x,
                top = e.lo,
                x1 = e.x,
                floor = e.hi,
                axis = "side"
            });
            return true;
        }

        private bool ContainsLadderOrStairs(string cell)
        {
            return cell.Contains("А") || cell.Contains("В") || cell.Contains("Г") || cell.Contains("-");
        }

        private bool MergeStoreys(EdgeInfo e, int[] roomGroups)
        {
            if (partitionInfo == null || e.y1 != e.y || e.hi - e.lo < 9 || roomGroups[e.a.id] == roomGroups[e.b.id]) return false;
            string[] excluded = { "canal", "roof", "street", "medical" };
            if (Array.IndexOf(excluded, e.a.role) >= 0 || Array.IndexOf(excluded, e.b.role) >= 0) return false;

            int size = 0;
            foreach (int g in roomGroups) if (g == roomGroups[e.a.id] || g == roomGroups[e.b.id]) size++;
            if (size > 3 || rnd() > (theme == "stable" ? 0.10 : 0.20)) return false;

            var upper = e.a.top < e.b.top ? e.a : e.b;
            var runs = new List<(int x, int w)>();

            int[] directions = { -1, 1 };
            foreach (int direction in directions)
            {
                int edge = direction > 0 ? e.lo : e.hi;
                if (edge != (direction > 0 ? upper.x0 : upper.x1)) continue;
                int length = 0;
                for (int x = edge; x >= e.lo && x <= e.hi && length < 9; x += direction)
                {
                    bool ok = Solid(x, e.y);
                    for (int y = e.y - 2; y <= e.y + 1; y++) if (reserved.Contains((y, x))) ok = false;
                    if (!ok) break;
                    length++;
                }
                if (length >= 5) runs.Add((direction > 0 ? edge : edge - length + 1, length));
            }

            if (runs.Count == 0) return false;
            var cut = runs[Pick(0, runs.Count - 1)];
            Open(cut.x, e.y, cut.x + cut.w - 1, e.y, e.a.bg);
            Reserve(cut.x, e.y - 2, cut.x + cut.w - 1, e.y + 1);
            merges.Add(new MergeInfo
            {
                a = e.a.id,
                b = e.b.id,
                x0 = cut.x,
                top = e.y,
                x1 = cut.x + cut.w - 1,
                floor = e.y,
                axis = "floor"
            });
            return true;
        }

        private void ConnectVolumes()
        {
            var edges = new List<EdgeInfo>();
            int[] groups = new int[volumes.Count];
            for (int i = 0; i < volumes.Count; i++)
            {
                groups[i] = i;
                for (int j = 0; j < i; j++)
                {
                    var e = Adjacent(volumes[i], volumes[j]);
                    if (e != null) edges.Add(e);
                }
            }

            Shuffle(edges);
            if (partitionInfo != null)
            {
                foreach (var relation in edges)
                {
                    relation.priority = -Math.Log(Math.Max(0.00001, rnd())) / RRSpaceRules.Affinity(theme, relation.a.role, relation.b.role);
                }
                edges.Sort((a, b) => a.priority.CompareTo(b.priority));
            }

            var chosen = new List<EdgeInfo>();
            var extras = new List<EdgeInfo>();

            foreach (var e in edges)
            {
                int ga = groups[e.a.id], gb = groups[e.b.id];
                if (ga == gb)
                {
                    extras.Add(e);
                    continue;
                }
                chosen.Add(e);
                for (int i = 0; i < groups.Length; i++) if (groups[i] == gb) groups[i] = ga;
            }

            if (chosen.Count != volumes.Count - 1) throw new InvalidOperationException("Disconnected architectural partition");

            double density = partitionInfo != null ? partitionInfo.extra : new[] { 0.0, 0.45, 0.75, 1.0 }[Pick(0, 3)];
            foreach (var e in extras) if (rnd() < density) chosen.Add(e);

            int[] roomGroups = new int[volumes.Count];
            for (int i = 0; i < volumes.Count; i++) roomGroups[i] = i;

            foreach (var e in chosen)
            {
                bool merged = false;
                if (e.vertical)
                {
                    var lower = e.a.top > e.b.top ? e.a : e.b;
                    int lx = LadderPosition(e.lo + 1, e.hi - 2, e.y, lower.floor, lower.role == "canal" ? lower : null);
                    LadderRoute(lx, e.y, lower.floor, lower);
                    e.x = lx;
                    e.kind = "ladder";
                    merged = MergeStoreys(e, roomGroups);
                }
                else
                {
                    int f = e.hi;
                    merged = MergeSide(e, roomGroups);
                    if (!merged) Open(e.x0, f - 2, e.x1, f, e.a.bg);
                    Reserve(e.x0 - 2, f - 3, e.x1 + 2, f);
                    LandingToFloor(e.a, e.x, f);
                    LandingToFloor(e.b, e.x1, f);
                    bool outdoors = e.a.role == "street" || e.b.role == "street" || e.a.role == "roof" || e.b.role == "roof";
                    double chance = theme == "sewer" ? 0.28 : (theme == "stable" ? 0.88 : 0.7);
                    if (modern) chance = theme == "sewer" ? 0.55 : 0.92;

                    if (merged)
                    {
                        e.kind = "merge";
                    }
                    else if (rnd() < chance && !outdoors && Support(e.x, f))
                    {
                        Door(e.x, f, e.a.bg, e.a.role, e.b.role);
                        e.kind = "door";
                    }
                    else
                    {
                        e.kind = "opening";
                    }
                    e.y = f;
                }

                if (merged)
                {
                    e.kind = "merge";
                    int ga = roomGroups[e.a.id], gb = roomGroups[e.b.id];
                    for (int i = 0; i < roomGroups.Length; i++) if (roomGroups[i] == gb) roomGroups[i] = ga;
                }

                links.Add(new CirculationLink
                {
                    a = e.a.id,
                    b = e.b.id,
                    kind = e.kind,
                    x = e.x,
                    y = e.y
                });
            }

            if (partitionInfo != null)
            {
                var distinct = new HashSet<int>();
                for (int i = 0; i < volumes.Count; i++)
                {
                    volumes[i].room = roomGroups[i];
                    distinct.Add(roomGroups[i]);
                }
                partitionInfo.rooms = distinct.Count;
                partitionInfo.merges = merges.Count;

                if (modern)
                {
                    var finishMap = new Dictionary<string, FinishInfo>();
                    foreach (var volume in volumes)
                    {
                        int area = (volume.x1 - volume.x0 + 1) * (volume.floor - volume.top + 1);
                        string groupKey = volume.room.ToString();
                        if (!finishMap.TryGetValue(groupKey, out var material))
                        {
                            material = new FinishInfo { top = volume.top, floor = volume.floor, area = 0 };
                            finishMap[groupKey] = material;
                        }
                        material.top = Math.Min(material.top, volume.top);
                        material.floor = Math.Max(material.floor, volume.floor);
                        if (area > material.area)
                        {
                            material.area = area;
                            material.bg = volume.bg;
                            material.role = volume.role;
                        }
                    }
                    foreach (var volume in volumes)
                    {
                        volume.finish = finishMap[volume.room.ToString()];
                        volume.bg = volume.finish.bg;
                    }
                }
            }
        }

        private PartitionRegion VolumeAt(int x, int y)
        {
            foreach (var r in volumes)
            {
                if (x >= r.x0 && x <= r.x1 && y >= r.top && y <= r.floor) return r;
            }
            throw new InvalidOperationException($"No volume at port {x},{y}");
        }

        private void ConnectBoundary()
        {
            for (int p = 0; p < 22; p++)
            {
                if (ports[p] >= 2)
                {
                    PortRect b = RRPorts.Rect(p, ports[p], W, H);
                    PartitionRegion r;
                    if (RRPorts.Vertical(p))
                    {
                        r = VolumeAt(b.x0, p >= 17 ? 1 : H - 2);
                        if (p >= 17) LadderRoute(b.x0, 0, r.floor, r);
                        else Ladder(b.x0, H - 1, H - 1, r.bg);
                        Reserve(b.x0 - 1, p >= 17 ? 0 : H - 4, b.x1 + 1, p >= 17 ? 3 : H - 1);
                    }
                    else
                    {
                        r = VolumeAt(p >= 11 ? 1 : W - 2, b.y1);
                        Open(b.x0, b.y0, b.x1, b.y1, r.bg);
                        for (int x = b.x0; x <= b.x1; x++) grid[b.y1 + 1, x] = trim;
                        LandingToFloor(r, p >= 11 ? 0 : W - 1, b.y1);
                        Reserve(p >= 11 ? 0 : W - 5, b.y0 - 1, p >= 11 ? 4 : W - 1, b.y1);
                    }
                }
            }
        }

        private void LandingToFloor(PartitionRegion r, int edgeX, int f)
        {
            if (r.floor == f) return;
            bool left = edgeX <= r.x0;
            PartitionRegion bank = partitionInfo != null && r.role == "canal" ? r : null;
            int lx = left ? LadderPosition(r.x0, Math.Min(r.x0 + 6, r.x1 - 1), f + 1, r.floor, bank) :
                LadderPosition(Math.Max(r.x1 - 7, r.x0), r.x1 - 1, f + 1, r.floor, bank);
            int x0 = left ? r.x0 : Math.Max(r.x0, lx - 1), x1 = left ? Math.Min(r.x1, lx + 2) : r.x1;
            Platform(x0, x1, f + 1, r.bg);
            LadderRoute(lx, f + 1, r.floor, r);
            Reserve(x0, f - 2, x1, f);
        }

        private void Gallery(PartitionRegion r)
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                int f = Pick(r.top + 4, r.floor - 5);
                bool wide = r.role == "hall" || (r.role == "workshop" && rnd() < 0.5);
                int width = wide ? Pick((r.x1 - r.x0) / 2, r.x1 - r.x0 - 3) : Pick(6, Math.Min(12, r.x1 - r.x0 - 3));
                bool left = rnd() < 0.5;
                int x0 = left ? r.x0 : r.x1 - width + 1, x1 = left ? r.x0 + width - 1 : r.x1;
                bool clear = true;

                for (int y = f - 2; y <= f + 1; y++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        if (reserved.Contains((y, x)) || Solid(x, y)) clear = false;
                    }
                }
                if (!clear) continue;

                Platform(x0, x1, f + 1, r.bg);
                int lx = LadderPosition(x0 + 1, x1 - 2, f + 1, r.floor);
                LadderRoute(lx, f + 1, r.floor, r);
                regions.Add(new PartitionRegion
                {
                    x0 = x0,
                    top = r.top,
                    x1 = x1,
                    floor = f,
                    bg = r.bg,
                    role = r.role == "hall" ? "corridor" : (r.role == "workshop" ? "control" : (r.role == "warehouse" ? "store" : (r.role == "service" ? "service" : "roof")))
                });
                return;
            }
        }

        private void AddSceneFeatures()
        {
            if (theme == "sewer")
            {
                foreach (var r in volumes) if (r.role == "canal") AddPool(r);
                if (sceneForm != "dry_tunnels" && pools.Count == 0) throw new InvalidOperationException("No safe space for a canal and dry bank");
            }
            if (theme == "mane" && (sceneForm == "ruined" || sceneForm == "rooftops" || sceneForm == "street_links"))
            {
                int changes = Pick(1, 3);
                for (int t = 0; t < 60 && changes > 0; t++)
                {
                    int x = Pick(Sx(5), Sx(39)), y = Pick(Sy(6), Sy(19)), width = Pick(2, 4);
                    bool ok = true;
                    for (int xx = x - 1; xx <= x + width; xx++)
                    {
                        if (!Solid(xx, y) || Solid(xx, y - 1) || Solid(xx, y + 1)) ok = false;
                        for (int yy = y - 2; yy <= y + 2; yy++) if (reserved.Contains((yy, xx))) ok = false;
                    }
                    if (!ok) continue;

                    for (int xx = x; xx < x + width; xx++) grid[y, xx] = grid[y + 1, xx] + "-";
                    changes--;
                }
            }
        }

        private void AddPool(PartitionRegion r)
        {
            int f = r.floor;
            int depth = sceneForm == "cistern" ? Pick(5, 8) : Pick(2, 3);
            if (partitionInfo != null && sceneForm == "cistern") depth = Math.Min(depth, r.floor - r.top - 4);
            if (f - r.top < depth + 4 || r.x1 - r.x0 < 14) return;

            var candidates = new List<(int lo, int hi, double score)>();
            for (int lo = r.x0 + 4; lo <= r.x1 - 10; lo++)
            {
                int available = Math.Min(sceneForm == "pump_chain" ? 12 : 26, r.x1 - lo - 4);
                int minimum = sceneForm == "cistern" ? (partitionInfo != null ? 10 : 12) : (sceneForm == "pump_chain" ? 6 : 10);
                if (available < minimum) continue;

                for (int width = available; width >= minimum; width--)
                {
                    int hi = lo + width - 1;
                    bool ok = true;
                    for (int y = f - depth - 3; y <= f; y++)
                    {
                        for (int x = lo - 3; x <= hi + 3; x++)
                        {
                            if (Solid(x, y) || reserved.Contains((y, x)) || grid[y, x].Contains("-")) ok = false;
                        }
                    }
                    if (ok)
                    {
                        candidates.Add((lo, hi, width + rnd() * 3.0));
                        break;
                    }
                }
            }

            if (candidates.Count == 0) return;
            candidates.Sort((a, b) => b.score.CompareTo(a.score));
            var p = candidates[0];
            int poolLo = p.lo;
            int poolHi = p.hi;

            for (int y = f - depth + 1; y <= f; y++)
            {
                grid[y, poolLo - 1] = wall;
                grid[y, poolHi + 1] = wall;
            }
            Platform(poolLo - 2, poolHi + 2, f - depth, r.bg);
            Ladder(poolLo - 3, f - depth, f, r.bg);
            Ladder(poolHi + 2, f - depth, f, r.bg);
            Ladder(poolLo + 1, f - depth, f, r.bg);

            for (int y = f - depth + 1; y <= f; y++)
            {
                for (int x = poolLo; x <= poolHi; x++)
                {
                    grid[y, x] += "*";
                }
            }

            pools.Add(new PoolInfo
            {
                x0 = poolLo,
                x1 = poolHi,
                top = f - depth + 1,
                bottom = f,
                deck = f - depth
            });

            Reserve(poolLo - 3, f - depth - 3, poolHi + 3, f);
            regions.Add(new PartitionRegion
            {
                x0 = poolLo - 2,
                x1 = poolHi + 2,
                top = r.top,
                floor = f - depth - 1,
                bg = r.bg,
                role = "canal_walk"
            });
        }

        private void ShapeCeiling(PartitionRegion r)
        {
            if (theme == "mane" || r.role == "canal" || r.role == "hall" || r.role == "warehouse") return;
            int deep = theme == "sewer" ? Pick(2, 5) : (theme == "stable" ? Pick(1, 3) : Pick(0, 2));
            deep = Math.Min(deep, r.floor - r.top - 5);
            if (deep < 1) return;

            bool left = rnd() < 0.5;
            int width = Pick(3, Math.Max(3, (int)((r.x1 - r.x0 + 1) * 0.65)));
            int lo = left ? r.x0 : r.x1 - width + 1, hi = lo + width - 1;

            for (int y = r.top; y < r.top + deep; y++)
            {
                for (int x = lo; x <= hi; x++)
                {
                    if (reserved.Contains((y, x)) || grid[y, x].Contains("А") || grid[y, x].Contains("-")) return;
                }
            }

            for (int y = r.top; y < r.top + deep; y++)
            {
                for (int x = lo; x <= hi; x++)
                {
                    grid[y, x] = wall;
                }
            }
        }

        private void DressMaterials()
        {
            foreach (var r in volumes)
            {
                var refObj = modern && r.finish != null ? r.finish : new FinishInfo { top = r.top, floor = r.floor, bg = r.bg, role = r.role };
                for (int y = r.top; y <= r.floor; y++)
                {
                    for (int x = r.x0; x <= r.x1; x++)
                    {
                        string code = grid[y, x];
                        if (Solid(x, y)) continue;

                        string bg = r.bg;
                        if (theme == "stable")
                        {
                            if (y <= refObj.top + 1) bg = "R";
                            else if (y >= refObj.floor - 1) bg = "P";
                            else if (r.role == "control" && x >= r.x0 + 2 && x <= r.x1 - 2) bg = "O";
                        }
                        else if (theme == "sewer" && (r.role == "canal" || r.role == "service"))
                        {
                            bg = y >= refObj.floor - 3 ? "E" : (x < r.x0 + 3 || x > r.x1 - 3 ? "S" : r.bg);
                        }
                        else if (theme == "plant" && (r.role == "workshop" || r.role == "warehouse"))
                        {
                            bg = y >= refObj.floor - 3 ? "D" : r.bg;
                        }

                        string extra = code.Length > 1 ? code.Substring(1) : "";
                        if (extra.Length > 0 && WALL_CHARS.IndexOf(extra[0]) >= 0) extra = extra.Substring(1);
                        grid[y, x] = "_" + bg + extra;
                    }
                }
            }

            for (int y = 1; y < H - 1; y++)
            {
                for (int x = 1; x < W - 1; x++)
                {
                    if (Solid(x, y))
                    {
                        if (theme == "stable" && Solid(x, y - 1) && Solid(x, y + 1)) grid[y, x] = (x % 9 < 2) ? "K" : "J";
                        if (theme == "plant" && (x < 4 || x > W - 5 || y > H - 5) && (x / 7 + y / 5) % 3 == 0) grid[y, x] = "D";
                        if (theme == "sewer" && Solid(x, y - 1) && Solid(x, y + 1) && (x < 5 || x > W - 6)) grid[y, x] = "I";
                        if (theme == "mane" && (x / 8 + y / 6) % 3 == 0) grid[y, x] = "D";
                    }
                }
            }
        }

        public void Open(int x0, int top, int x1, int bottom, string bg)
        {
            for (int y = Math.Max(0, top); y <= Math.Min(H - 1, bottom); y++)
            {
                for (int x = Math.Max(0, x0); x <= Math.Min(W - 1, x1); x++)
                {
                    grid[y, x] = "_" + bg;
                }
            }
        }

        public void Platform(int x0, int x1, int y, string bg)
        {
            for (int x = Math.Max(1, x0); x <= Math.Min(W - 2, x1); x++)
            {
                if (!reserved.Contains((y, x))) grid[y, x] = "_" + bg + "-";
            }
        }

        public void Ladder(int x, int top, int bottom, string bg)
        {
            for (int yy = top; yy <= bottom; yy++)
            {
                for (int xx = x; xx <= x + 1; xx++)
                {
                    if (grid[yy, xx].Contains("В") || grid[yy, xx].Contains("Г"))
                    {
                        throw new InvalidOperationException("Later ladder cuts an existing stair flight");
                    }
                }
            }

            for (int y = top; y <= bottom; y++)
            {
                grid[y, x] = "_" + bg;
                grid[y, x + 1] = "_" + bg + "А";
            }

            ladders.Add(new LadderFixture { x = x, top = top, bottom = bottom });
            Reserve(x, Math.Max(0, top - 2), x + 1, bottom);
            Reserve(x - 1, bottom - 1, x + 2, bottom);
        }

        private int LadderPosition(int lo, int hi, int top, int bottom, PartitionRegion canal = null)
        {
            var choices = new List<int>();
            for (int x = lo; x <= hi; x++)
            {
                bool ok = true;
                foreach (var l in ladders)
                {
                    if (top <= l.bottom + 1 && bottom >= l.top - 1 && x != l.x && Math.Abs(x - l.x) < 3) ok = false;
                    if (archetype != "connector" && x == l.x && top <= l.bottom && bottom > l.bottom) ok = false;
                }
                for (int p = 17; p < 22; p++)
                {
                    if (ports[p] >= 2)
                    {
                        int px = 5 + 9 * (p - 17);
                        if (x != px && Math.Abs(x - px) < 3) ok = false;
                    }
                }
                if (archetype != "connector" && bottom >= H - 4)
                {
                    for (int p = 6; p < 11; p++)
                    {
                        if (ports[p] >= 2 && Math.Abs(x - (5 + 9 * (p - 6))) < 3) ok = false;
                    }
                }
                if (ok) choices.Add(x);
            }

            if (choices.Count == 0) throw new InvalidOperationException($"No separate ladder landing {lo}..{hi}");

            if (canal != null)
            {
                choices.Sort((a, b) =>
                {
                    int distA = Math.Min(Math.Abs(a - canal.x0), Math.Abs(a - canal.x1 + 1));
                    int distB = Math.Min(Math.Abs(b - canal.x0), Math.Abs(b - canal.x1 + 1));
                    return distA.CompareTo(distB);
                });
                return choices[0];
            }

            return choices[Pick(0, choices.Count - 1)];
        }

        private void LadderRoute(int x, int top, int bottom, PartitionRegion r)
        {
            if (partitionInfo != null && r.role == "canal")
            {
                Ladder(x, top, bottom, r.bg);
                return;
            }

            if (top >= 4 && bottom - top >= 3 && bottom - top <= 8 && rnd() < 0.65 && StairRoute(x, top, bottom, r))
            {
                return;
            }

            if (bottom - top > 10 && archetype != "connector")
            {
                int mid = top + Pick(5, 8);
                var choices = new List<(int x, int a, int b)>();

                for (int nx = r.x0; nx <= r.x1 - 1; nx++)
                {
                    if (Math.Abs(nx - x) < 3 || Math.Abs(nx - x) > 9) continue;
                    int a = Math.Max(r.x0, Math.Min(x, nx) - 1);
                    int b = Math.Min(r.x1, Math.Max(x, nx) + 2);
                    bool ok = true;

                    for (int yy = mid - 2; yy <= mid; yy++)
                    {
                        for (int xx = a; xx <= b; xx++)
                        {
                            if (Solid(xx, yy) || reserved.Contains((yy, xx))) ok = false;
                        }
                    }

                    foreach (var l in ladders)
                    {
                        if (mid <= l.bottom + 1 && bottom >= l.top - 1 && nx != l.x && Math.Abs(nx - l.x) < 3) ok = false;
                    }

                    if (bottom >= H - 4)
                    {
                        for (int p = 6; p < 11; p++)
                        {
                            if (ports[p] >= 2 && Math.Abs(nx - (5 + 9 * (p - 6))) < 3) ok = false;
                        }
                    }

                    if (ok) choices.Add((nx, a, b));
                }

                if (choices.Count > 0)
                {
                    var chosen = choices[Pick(0, choices.Count - 1)];
                    Platform(chosen.a, chosen.b, mid, r.bg);
                    Ladder(x, top, mid - 1, r.bg);
                    Ladder(chosen.x, mid, bottom, r.bg);
                    Reserve(chosen.a, mid - 2, chosen.b, mid - 1);
                    return;
                }
            }

            Ladder(x, top, bottom, r.bg);
        }

        private bool StairRoute(int x, int top, int bottom, PartitionRegion r)
        {
            int[] directions = rnd() < 0.5 ? new[] { 1, -1 } : new[] { -1, 1 };
            foreach (int dir in directions)
            {
                int start = dir > 0 ? x : x + 1;
                int end = start + dir * (bottom - top);
                int apertureLo = Math.Min(x, start + dir * 4);
                int apertureHi = Math.Max(x + 1, start + dir * 4);

                if (Math.Min(start, end) < r.x0 + 2 || Math.Max(start, end) > r.x1 - 2) continue;
                if (!Landing(dir > 0 ? x - 1 : x + 2, top)) continue;
                if (partitionInfo != null && Landing(apertureLo - 1, top) && Landing(apertureHi + 1, top)) continue;

                bool ok = true;
                for (int y = top; y <= bottom; y++)
                {
                    int sx = start + dir * (y - top);
                    for (int yy = y - 3; yy <= y; yy++)
                    {
                        for (int xx = sx - 1; xx <= sx + 1; xx++)
                        {
                            if (yy == top && xx == (dir > 0 ? x - 1 : x + 2)) continue;
                            if (yy == top && xx >= apertureLo && xx <= apertureHi)
                            {
                                if (reserved.Contains((yy, xx))) ok = false;
                                continue;
                            }
                            if (Solid(xx, yy) || reserved.Contains((yy, xx)) || grid[yy, xx].Contains("-")) ok = false;
                        }
                    }
                }
                if (!ok) continue;

                Open(apertureLo, top, apertureHi, top, r.bg);
                Reserve(apertureLo, top - 3, apertureHi, top);

                for (int y = top; y <= bottom; y++)
                {
                    int sx = start + dir * (y - top);
                    grid[y, sx] = "_" + r.bg + (dir > 0 ? "Г" : "В");
                    Reserve(sx - 1, y - 3, sx + 1, y);
                }

                stairs.Add(new StairFixture
                {
                    x = start,
                    top = top,
                    bottom = bottom,
                    dir = dir
                });
                return true;
            }
            return false;
        }

        private void Door(int x, int floor, string bg, string a, string b)
        {
            string id = RRScene.Door(theme, a, b, rnd);
            int h = id == "stdoor" || id == "door3" ? 3 : 2;
            grid[floor - h, x] = wall;
            Open(x, floor - h + 1, x, floor, bg);
            doors.Add(new DoorFixture { id = id, x = x, y = floor });
            Reserve(x - 1, floor - h, x + 1, floor);
        }

        private bool Landing(int x, int y)
        {
            return Solid(x, y) || grid[y, x].Contains("-");
        }

        private void AuditLadders()
        {
            for (int y = 0; y < H; y++)
            {
                for (int x = 1; x < W; x++)
                {
                    if (!grid[y, x].Contains("А")) continue;
                    if (Solid(x - 1, y)) throw new InvalidOperationException($"Narrow ladder {x},{y}");
                    if (y == 0 || grid[y - 1, x].Contains("А")) continue;
                    if (y < 2 || Solid(x, y - 1) || Solid(x - 1, y - 1) || Solid(x, y - 2) || Solid(x - 1, y - 2))
                    {
                        throw new InvalidOperationException($"Ladder head {x},{y}");
                    }
                    if (!Landing(x - 2, y) && !Landing(x + 1, y)) throw new InvalidOperationException($"Ladder landing {x},{y}");
                }
            }
        }

        private void AuditStairs()
        {
            foreach (var flight in stairs)
            {
                for (int y = flight.top; y <= flight.bottom; y++)
                {
                    int x = flight.x + flight.dir * (y - flight.top);
                    if (!grid[y, x].Contains(flight.dir > 0 ? "Г" : "В"))
                    {
                        throw new InvalidOperationException($"Interrupted stair flight {x},{y}");
                    }
                }
            }
        }

        private void AuditBoundary()
        {
            var expected = new HashSet<(int y, int x)>();
            for (int p = 0; p < 22; p++)
            {
                if (ports[p] >= 2)
                {
                    PortRect b = RRPorts.Rect(p, ports[p], W, H);
                    for (int y = b.y0; y <= b.y1; y++)
                    {
                        for (int x = b.x0; x <= b.x1; x++)
                        {
                            if (Solid(x, y)) throw new InvalidOperationException($"Blocked declared port {p}");
                            expected.Add((y, x));
                        }
                    }
                }
            }

            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    if ((x == 0 || x == W - 1 || y == 0 || y == H - 1) && !Solid(x, y) && !expected.Contains((y, x)))
                    {
                        throw new InvalidOperationException($"Undeclared edge hole {x},{y}");
                    }
                }
            }
        }

        private void AddHatches()
        {
            var candidates = new List<HatchFixture>();
            foreach (var l in ladders)
            {
                for (int y = Math.Max(4, l.top); y <= Math.Min(H - 5, l.bottom - 2); y++)
                {
                    int x = l.x;
                    if (!Landing(x - 1, y) || !Landing(x + 2, y)) continue;
                    bool clear = true;
                    for (int yy = y - 2; yy <= y + 2; yy++)
                    {
                        if (Solid(x, yy) || Solid(x + 1, yy)) clear = false;
                    }
                    if (clear) candidates.Add(new HatchFixture { x = x, y = y });
                }
            }

            Shuffle(candidates);
            int target = Pick(0, 2);
            foreach (var p in candidates)
            {
                if (hatches.Count >= target) break;
                bool overlaps = false;
                foreach (var h in hatches)
                {
                    if (Math.Abs(p.x - h.x) < 3 && Math.Abs(p.y - h.y) < 4) overlaps = true;
                }
                if (overlaps) continue;

                p.id = scene.hatches[Pick(0, scene.hatches.Length - 1)];
                hatches.Add(p);
                Reserve(p.x - 1, p.y - 2, p.x + 2, p.y + 2);
            }
        }

        private void AddWindows()
        {
            var candidates = new List<WindowFixture>();
            for (int x = Sx(4); x <= Sx(43); x++)
            {
                for (int y = Sy(3); y <= Sy(21); y++)
                {
                    if (!Solid(x, y - 2) || !Solid(x, y - 1) || !Solid(x, y) || !Solid(x, y + 1)) continue;
                    if (reserved.Contains((y - 1, x)) || reserved.Contains((y, x))) continue;
                    if (Solid(x - 1, y - 1) || Solid(x - 1, y) || Solid(x + 1, y - 1) || Solid(x + 1, y)) continue;
                    candidates.Add(new WindowFixture { x = x, y = y });
                }
            }

            Shuffle(candidates);
            int target = Pick(0, scene.windowMax);
            foreach (var p in candidates)
            {
                if (windows.Count >= target) break;
                if (reserved.Contains((p.y - 1, p.x)) || reserved.Contains((p.y, p.x))) continue;

                p.id = scene.windows[Pick(0, scene.windows.Length - 1)];
                Open(p.x, p.y - 1, p.x, p.y, backgrounds[0]);
                windows.Add(p);
                Reserve(p.x - 1, p.y - 2, p.x + 1, p.y + 1);
            }
        }

        private void ChooseSpawn()
        {
            var candidates = new List<(int x, int y)>();
            foreach (var r in volumes)
            {
                for (int x = r.x0 + 1; x < r.x1 - 1; x++)
                {
                    int f = r.floor;
                    bool ok = true;
                    for (int xx = x; xx <= x + 1; xx++)
                    {
                        for (int y = f - 2; y <= f; y++)
                        {
                            if (Solid(xx, y) || reserved.Contains((y, xx))) ok = false;
                        }
                    }
                    if (ok && Support(x, f) && Support(x + 1, f)) candidates.Add((x, f));
                }
            }

            if (candidates.Count == 0) throw new InvalidOperationException("No supported spawn in architecture");
            spawn = candidates[Pick(0, candidates.Count - 1)];
            Reserve(spawn.x - 1, spawn.y - 3, spawn.x + 2, spawn.y);
        }

        public void Reserve(int x0, int top, int x1, int bottom)
        {
            for (int y = Math.Max(0, top); y <= Math.Min(H - 1, bottom); y++)
            {
                for (int x = Math.Max(0, x0); x <= Math.Min(W - 1, x1); x++)
                {
                    reserved.Add((y, x));
                }
            }
        }

        public bool Solid(int x, int y)
        {
            return x < 0 || x >= W || y < 0 || y >= H || (grid[y, x].Length > 0 && WALL_CHARS.IndexOf(grid[y, x][0]) >= 0);
        }

        public bool Support(int x, int y)
        {
            return x >= 0 && x < W && y >= 0 && y + 1 < H && (Solid(x, y + 1) || grid[y + 1, x].Contains("-"));
        }
    }
}
