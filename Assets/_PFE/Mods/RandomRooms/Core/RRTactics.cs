using System;
using System.Collections.Generic;

namespace PFE.Mods.RandomRooms.Core
{
    public class TacticsEntry
    {
        public int slot;
        public int x;
        public int y;
        public int node;
    }

    public class TacticsReach
    {
        public Dictionary<int, int> distances = new();
        public Dictionary<int, int> parents = new();
        public int start;
    }

    public class TurretTargetInfo
    {
        public double x;
        public double y;
        public string kind;
    }

    public class TurretTacticsInfo
    {
        public string uid;
        public int x;
        public int y;
        public string id;
        public string mount;
        public int turn;
        public double ox;
        public double oy;
        public string zone;
        public double score;
        public TurretTargetInfo target;
    }

    /// <summary>
    /// Conservative tactical movement, line-of-sight raycasting, and covered approach model.
    /// Pure C#, zero UnityEngine references.
    /// </summary>
    public class RRTactics
    {
        public List<TacticsEntry> entries = new();
        public List<int>[] edges;
        public bool[] stand;
        public bool[] clear;
        private readonly RRArchitecture p;

        public RRTactics(RRArchitecture plan)
        {
            p = plan;
            const int W = 48;
            const int H = 25;
            const int TotalCells = W * H;

            edges = new List<int>[TotalCells];
            stand = new bool[TotalCells];
            clear = new bool[TotalCells];

            bool[] climb = new bool[TotalCells];
            bool[] slope = new bool[TotalCells];
            int[] direction = new int[TotalCells];
            bool[] hard = new bool[TotalCells];
            var glass = new HashSet<int>();

            foreach (var window in p.windows)
            {
                glass.Add(window.y * W + window.x);
                glass.Add((window.y - 1) * W + window.x);
            }

            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    hard[y * W + x] = p.Solid(x, y) || glass.Contains(y * W + x);
                }
            }

            for (int y = 1; y < H; y++)
            {
                for (int x = 0; x < W - 1; x++)
                {
                    int i = y * W + x;
                    clear[i] = !hard[i] && !hard[i + 1] && !hard[i - W] && !hard[i - W + 1];

                    for (int yy = y - 1; yy <= y; yy++)
                    {
                        for (int xx = x; xx <= x + 1; xx++)
                        {
                            string cell = p.grid[yy, xx];
                            if (cell.Contains("*") || cell.Contains(",") || cell.Contains(";"))
                            {
                                clear[i] = false;
                            }
                        }
                    }

                    if (!clear[i]) continue;

                    edges[i] = new List<int>();
                    climb[i] = p.grid[y, x + 1].Contains("А");
                    stand[i] = false;

                    for (int sy = y; sy <= Math.Min(24, y + 1); sy++)
                    {
                        for (int sx = x; sx <= x + 1; sx++)
                        {
                            string cell = p.grid[sy, sx];
                            if (cell.Contains("В") || cell.Contains("Г"))
                            {
                                slope[i] = true;
                                direction[i] = cell.Contains("Г") ? 1 : -1;
                            }
                        }
                    }

                    if (y < 24)
                    {
                        for (int xx = x; xx <= x + 1; xx++)
                        {
                            string cellBelow = p.grid[y + 1, xx];
                            string cellCurrent = p.grid[y, xx];
                            if (hard[(y + 1) * W + xx] || cellBelow.Contains("-") ||
                                (cellBelow.Contains("А") && !cellCurrent.Contains("А")))
                            {
                                stand[i] = true;
                            }
                        }
                    }

                    if (slope[i]) stand[i] = true;
                }
            }

            for (int y = 1; y < H; y++)
            {
                for (int x = 0; x < W - 1; x++)
                {
                    int i = y * W + x;
                    if (!clear[i]) continue;

                    int[] dxs = { -1, 1 };
                    foreach (int dx in dxs)
                    {
                        int n = i + dx;
                        if (x + dx < 0 || x + dx >= W - 1) continue;
                        if (clear[n] && (stand[i] || stand[n] || climb[i] || climb[n]))
                        {
                            edges[i].Add(n);
                        }

                        int[] dys = { -1, 1 };
                        foreach (int dy in dys)
                        {
                            int d = n + dy * W;
                            if (d >= 0 && d < TotalCells && clear[d] && (slope[i] || slope[d]) &&
                                dy == dx * (slope[i] ? direction[i] : direction[d]) &&
                                (clear[n] || (i + dy * W >= 0 && i + dy * W < TotalCells && clear[i + dy * W])))
                            {
                                edges[i].Add(d);
                            }
                        }
                    }

                    if (i - W >= 0 && clear[i - W] && (climb[i] || climb[i - W]))
                    {
                        edges[i].Add(i - W);
                    }
                    if (i + W < TotalCells && clear[i + W])
                    {
                        edges[i].Add(i + W);
                    }
                }
            }

            for (int slot = 0; slot < 22; slot++)
            {
                if (p.ports[slot] >= 2)
                {
                    PortRect b = RRPorts.Rect(slot, p.ports[slot]);
                    int px = RRPorts.Vertical(slot) ? b.x0 : (slot >= 11 ? 0 : 46);
                    int py = RRPorts.Vertical(slot) ? (slot >= 17 ? 1 : 24) : b.y1;
                    entries.Add(new TacticsEntry
                    {
                        slot = slot,
                        x = px,
                        y = py,
                        node = py * W + px
                    });
                }
            }
        }

        public bool Line(double ax, double ay, double bx, double by)
        {
            int steps = (int)Math.Ceiling(Math.Max(Math.Abs(ax - bx), Math.Abs(ay - by)) * 5.0);
            for (int i = 1; i < steps; i++)
            {
                int xx = (int)(ax + (bx - ax) * i / steps);
                int yy = (int)(ay + (by - ay) * i / steps);
                if (p.Solid(xx, yy)) return false;
            }
            return true;
        }

        public bool Exposed(TurretTacticsInfo t, double x, double y)
        {
            double dx = x - t.ox;
            double dy = y - t.oy;
            double angle = Math.Atan2(dy, dx) * 180.0 / Math.PI;

            if (t.mount == "ceiling" && angle > -150 && angle < -30) return false;
            if (t.id == "armturret" && (t.turn > 0 ? Math.Abs(angle) > 30 : Math.Abs(angle) < 150)) return false;

            return Line(t.ox, t.oy, x, y);
        }

        public bool BodyExposed(TurretTacticsInfo t, int x, int y)
        {
            double[] pxs = { x + 0.2, x + 1.0, x + 1.8 };
            double[] pys = { y - 0.8, y + 0.1, y + 0.8 };

            foreach (double px in pxs)
            {
                foreach (double py in pys)
                {
                    if (Exposed(t, px, py)) return true;
                }
            }
            return false;
        }

        public bool ProtectsEntry(TurretTacticsInfo t)
        {
            const int W = 48;
            foreach (var e in entries)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int node = (e.y + dy) * W + e.x + dx;
                        if (node >= 0 && node < clear.Length && clear[node] && BodyExposed(t, e.x + dx, e.y + dy))
                        {
                            return false;
                        }
                    }
                }
            }
            return !BodyExposed(t, p.spawn.x, p.spawn.y);
        }

        public HashSet<int> BlockedBy(List<TurretTacticsInfo> turrets)
        {
            const int W = 48;
            var blocked = new HashSet<int>();
            for (int i = 0; i < clear.Length; i++)
            {
                if (clear[i])
                {
                    foreach (var t in turrets)
                    {
                        if (BodyExposed(t, i % W, i / W))
                        {
                            blocked.Add(i);
                            break;
                        }
                    }
                }
            }
            return blocked;
        }

        public TacticsReach Flood(int start, HashSet<int> blocked = null)
        {
            var reach = new TacticsReach { start = start };
            var queue = new List<int>();

            if (clear[start] && (blocked == null || !blocked.Contains(start)))
            {
                reach.distances[start] = 0;
                queue.Add(start);
            }

            for (int at = 0; at < queue.Count; at++)
            {
                int curr = queue[at];
                if (edges[curr] == null) continue;

                foreach (int next in edges[curr])
                {
                    if (!reach.distances.ContainsKey(next) && (blocked == null || !blocked.Contains(next)))
                    {
                        reach.distances[next] = reach.distances[curr] + 1;
                        reach.parents[next] = curr;
                        queue.Add(next);
                    }
                }
            }

            return reach;
        }

        public List<int> Path(TacticsReach reach, int to)
        {
            var outPath = new List<int>();
            if (!reach.distances.ContainsKey(to)) return outPath;

            int curr = to;
            while (curr != reach.start)
            {
                outPath.Insert(0, curr);
                curr = reach.parents[curr];
            }
            outPath.Insert(0, reach.start);
            return outPath;
        }

        public void ReservePath(List<int> nodes)
        {
            const int W = 48;
            foreach (int node in nodes)
            {
                p.Reserve(node % W, node / W - 1, node % W + 1, node / W);
            }
        }
    }
}
