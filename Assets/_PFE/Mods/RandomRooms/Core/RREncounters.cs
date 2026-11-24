using System;
using System.Collections.Generic;

namespace PFE.Mods.RandomRooms.Core
{
    public class EncounterSector
    {
        public int id;
        public double pressure;
        public double limit;
        public int nodes;
    }

    public class PlacedActor
    {
        public double x;
        public double y;
        public string kind;
        public double cost;
    }

    /// <summary>
    /// Closed-door contact sector zoning and encounter density management.
    /// Enforces the >= 4-tile (16 squared distance) major defender spacing rule.
    /// Pure C#, zero UnityEngine references.
    /// </summary>
    public class RREncounters
    {
        public List<EncounterSector> sectors = new();
        public int rejected = 0;
        private readonly Dictionary<int, int> nodes = new();
        private readonly HashSet<int> gates = new();
        private readonly RRArchitecture p;
        private readonly RRTactics tactics;
        private readonly List<PlacedActor> placed = new();
        private readonly double limit;

        public RREncounters(RRArchitecture plan, RRTactics model, int danger)
        {
            p = plan;
            tactics = model;
            limit = 2.8 + 2.2 * danger / 100.0;
            const int W = 48;

            foreach (var d in p.doors)
            {
                int doorH = d.id == "stdoor" || d.id == "door3" ? 2 : 1;
                for (int y = d.y - doorH; y <= d.y; y++)
                {
                    gates.Add(y * W + d.x);
                }
            }

            foreach (var d in p.hatches)
            {
                gates.Add(d.y * W + d.x);
                gates.Add(d.y * W + d.x + 1);
            }

            var blocked = new HashSet<int>();
            var adj = new Dictionary<int, List<int>>();

            for (int i = 0; i < tactics.clear.Length; i++)
            {
                if (tactics.clear[i])
                {
                    if (gates.Contains(i) || gates.Contains(i + 1) || gates.Contains(i - W) || gates.Contains(i - W + 1))
                    {
                        blocked.Add(i);
                    }
                    else
                    {
                        adj[i] = new List<int>();
                    }
                }
            }

            for (int i = 0; i < tactics.edges.Length; i++)
            {
                if (adj.ContainsKey(i) && tactics.edges[i] != null)
                {
                    foreach (int next in tactics.edges[i])
                    {
                        if (adj.ContainsKey(next))
                        {
                            adj[i].Add(next);
                        }
                    }
                }
            }

            foreach (var kvp in adj)
            {
                int key = kvp.Key;
                if (!nodes.ContainsKey(key))
                {
                    var sector = new EncounterSector
                    {
                        id = sectors.Count,
                        pressure = 0,
                        limit = limit,
                        nodes = 0
                    };
                    var queue = new List<int> { key };
                    nodes[key] = sector.id;
                    sectors.Add(sector);

                    for (int at = 0; at < queue.Count; at++)
                    {
                        int curr = queue[at];
                        sector.nodes++;
                        if (adj.TryGetValue(curr, out var neighbors))
                        {
                            foreach (int next in neighbors)
                            {
                                if (!nodes.ContainsKey(next))
                                {
                                    nodes[next] = sector.id;
                                    queue.Add(next);
                                }
                            }
                        }
                    }
                }
            }
        }

        public int SectorAt(double ax, double ay)
        {
            const int W = 48;
            int n = (int)ay * W + (int)ax;
            int[] deltas = { 0, -1, 1, -W, W };
            foreach (int delta in deltas)
            {
                if (nodes.TryGetValue(n + delta, out int sectorId)) return sectorId;
            }

            int nearest = -1;
            double best = 1e9;

            foreach (var kvp in nodes)
            {
                int node = kvp.Key;
                double x = node % W + 1.0;
                double y = node / W + 0.2;
                double dx = x - ax - 0.5;
                double dy = y - ay - 0.5;
                double d = dx * dx + dy * dy;

                if (d >= best) continue;

                bool clear = true;
                int steps = (int)Math.Ceiling(Math.Max(Math.Abs(dx), Math.Abs(dy)) * 5.0);
                for (int i = 0; i <= steps; i++)
                {
                    int xx = (int)(ax + 0.5 + dx * i / Math.Max(1, steps));
                    int yy = (int)(ay + 0.5 + dy * i / Math.Max(1, steps));
                    if (p.Solid(xx, yy) || gates.Contains(yy * W + xx))
                    {
                        clear = false;
                        break;
                    }
                }
                if (clear)
                {
                    best = d;
                    nearest = kvp.Value;
                }
            }

            return nearest;
        }

        public double Separation(double ax, double ay)
        {
            double nearest = 14.0;
            foreach (var b in placed)
            {
                double d = Math.Sqrt(Distance(ax, ay, b.x, b.y));
                if (d < nearest) nearest = d;
            }
            return nearest;
        }

        private static double Distance(double ax, double ay, double bx, double by)
        {
            return (ax - bx) * (ax - bx) + (ay - by) * (ay - by);
        }

        public bool Allows(double ax, double ay, string kind, double cost)
        {
            if (cost <= 0) return true;
            int sector = SectorAt(ax, ay);
            if (sector < 0)
            {
                rejected++;
                return false;
            }

            if (sector >= 0 && sectors[sector].pressure + cost > limit + 0.0001)
            {
                rejected++;
                return false;
            }

            double near = cost;
            foreach (var b in placed)
            {
                double dist = Distance(ax, ay, b.x, b.y);
                // Major defenders must have >= 4 tiles (16 squared distance) between anchors
                if ((kind == "enemy" || kind == "security") && (b.kind == "enemy" || b.kind == "security") && dist < 16.0)
                {
                    rejected++;
                    return false;
                }
                if (dist < 49.0)
                {
                    near += b.cost;
                }
            }

            if (near > limit + 1.0)
            {
                rejected++;
                return false;
            }

            foreach (var b in placed)
            {
                if (Distance(ax, ay, b.x, b.y) < 49.0)
                {
                    near = cost + b.cost;
                    foreach (var other in placed)
                    {
                        if (other != b && Distance(b.x, b.y, other.x, other.y) < 49.0)
                        {
                            near += other.cost;
                        }
                    }
                    if (near > limit + 1.0)
                    {
                        rejected++;
                        return false;
                    }
                }
            }

            return true;
        }

        public int Record(double ax, double ay, string kind, double cost)
        {
            int id = SectorAt(ax, ay);
            if (cost > 0)
            {
                if (id >= 0) sectors[id].pressure += cost;
                placed.Add(new PlacedActor
                {
                    x = ax,
                    y = ay,
                    kind = kind,
                    cost = cost
                });
            }
            return id;
        }
    }
}
