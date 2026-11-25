using System;
using System.Collections.Generic;

namespace PFE.Mods.RandomRooms.Core
{
    /// <summary>
    /// Conservative two-tile clearance simulator with directed walking, climbing, and falling edges.
    /// BFS reachability gate verifying 100% room traversal and dry routes around sewer pools.
    /// Pure C#, zero UnityEngine references.
    /// </summary>
    public static class RRTraversal
    {
        public static void Check(RRArchitecture plan, bool dry = false)
        {
            int W = plan.W;
            int H = plan.H;
            int TotalCells = W * H;

            bool[] clear = new bool[TotalCells];
            bool[] climb = new bool[TotalCells];
            bool[] stand = new bool[TotalCells];
            bool[] hard = new bool[TotalCells];
            bool[] slope = new bool[TotalCells];
            int[] slopeDirection = new int[TotalCells];

            List<int>[] forward = new List<int>[TotalCells];
            List<int>[] reverse = new List<int>[TotalCells];

            var glass = new HashSet<int>();
            foreach (var window in plan.windows)
            {
                glass.Add(window.y * W + window.x);
                glass.Add((window.y - 1) * W + window.x);
            }

            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    int i = y * W + x;
                    hard[i] = plan.Solid(x, y) || glass.Contains(i);
                }
            }

            for (int y = 1; y < H; y++)
            {
                for (int x = 0; x < W - 1; x++)
                {
                    int i = y * W + x;
                    clear[i] = !hard[i] && !hard[i + 1] && !hard[i - W] && !hard[i - W + 1];

                    if (dry && (plan.grid[y, x].Contains("*") || plan.grid[y, x + 1].Contains("*") ||
                        plan.grid[y - 1, x].Contains("*") || plan.grid[y - 1, x + 1].Contains("*")))
                    {
                        clear[i] = false;
                    }

                    if (!clear[i]) continue;

                    forward[i] = new List<int>();
                    reverse[i] = new List<int>();
                    climb[i] = false;

                    // Cyrillic 'А' is ladder modifier
                    if (plan.grid[y, x + 1].Contains("А")) climb[i] = true;
                    stand[i] = false;
                    slope[i] = false;

                    for (int sy = y; sy <= Math.Min(H - 1, y + 1); sy++)
                    {
                        for (int sx = x; sx <= x + 1; sx++)
                        {
                            string cell = plan.grid[sy, sx];
                            // Cyrillic 'В' or 'Г'
                            if (cell.Contains("В") || cell.Contains("Г"))
                            {
                                slope[i] = true;
                                slopeDirection[i] = cell.Contains("Г") ? 1 : -1;
                            }
                        }
                    }

                    if (y < H - 1)
                    {
                        for (int xx = x; xx <= x + 1; xx++)
                        {
                            string cellBelow = plan.grid[y + 1, xx];
                            string cellCurrent = plan.grid[y, xx];
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
                        if (x + dx >= 0 && x + dx < W - 1 && clear[n] && (stand[i] || stand[n] || climb[i] || climb[n]))
                        {
                            Edge(forward, reverse, i, n);
                        }

                        int[] dys = { -1, 1 };
                        foreach (int dy in dys)
                        {
                            int diagonal = n + dy * W;
                            if (x + dx >= 0 && x + dx < W - 1 && diagonal >= 0 && diagonal < TotalCells && clear[diagonal] &&
                                (slope[i] || slope[diagonal]) &&
                                dy == dx * (slope[i] ? slopeDirection[i] : slopeDirection[diagonal]) &&
                                (clear[n] || (i + dy * W >= 0 && i + dy * W < TotalCells && clear[i + dy * W])))
                            {
                                Edge(forward, reverse, i, diagonal);
                            }
                        }
                    }

                    int nUp = i - W;
                    if (nUp >= 0 && clear[nUp] && (climb[i] || climb[nUp]))
                    {
                        Edge(forward, reverse, i, nUp);
                    }

                    int nDown = i + W;
                    if (nDown < TotalCells && clear[nDown] && (!hard[nDown] && !hard[nDown + 1]))
                    {
                        Edge(forward, reverse, i, nDown);
                    }
                }
            }

            int start = plan.spawn.y * W + plan.spawn.x;
            var reached = Flood(forward, start);
            var returned = Flood(reverse, start);

            for (int i = 0; i < TotalCells; i++)
            {
                if (clear[i] && stand[i] && !plan.hiddenCells.Contains(i) && (!reached.Contains(i) || !returned.Contains(i)))
                {
                    throw new InvalidOperationException((dry ? "Dry " : "") + $"floor lacks a walking/climbing return {i % W},{i / W}");
                }
            }

            for (int p = 0; p < 22; p++)
            {
                if (plan.ports[p] >= 2)
                {
                    PortRect b = RRPorts.Rect(p, plan.ports[p], W, H);
                    int px = RRPorts.Vertical(p) ? b.x0 : (p >= 11 ? 0 : W - 2);
                    int py = RRPorts.Vertical(p) ? (p >= 17 ? 1 : H - 1) : b.y1;
                    int i = py * W + px;
                    if (!reached.Contains(i) || !returned.Contains(i))
                    {
                        throw new InvalidOperationException($"Port lacks a walking/climbing return {p}");
                    }
                }
            }
        }

        private static void Edge(List<int>[] forward, List<int>[] reverse, int from, int to)
        {
            forward[from].Add(to);
            reverse[to].Add(from);
        }

        private static HashSet<int> Flood(List<int>[] edges, int start)
        {
            var seen = new HashSet<int> { start };
            var queue = new List<int> { start };

            for (int at = 0; at < queue.Count; at++)
            {
                int curr = queue[at];
                if (edges[curr] == null) continue;
                foreach (int n in edges[curr])
                {
                    if (seen.Add(n))
                    {
                        queue.Add(n);
                    }
                }
            }

            return seen;
        }
    }
}
