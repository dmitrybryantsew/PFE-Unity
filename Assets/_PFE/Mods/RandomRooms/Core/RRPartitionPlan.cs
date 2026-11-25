using System;
using System.Collections.Generic;

namespace PFE.Mods.RandomRooms.Core
{
    public class PartitionRegion
    {
        public int x0;
        public int top;
        public int x1;
        public int floor;
        public string role;
        public SpaceDemand spec;
        public int id;
        public string bg;
        public int room;
        public bool keep;
        public bool recess;
        public FinishInfo finish;

        public int Width => x1 - x0 + 1;
        public int Height => floor - top + 1;
        public int Area => Width * Height;

        public PartitionRegion Clone()
        {
            return new PartitionRegion
            {
                x0 = x0,
                top = top,
                x1 = x1,
                floor = floor,
                role = role,
                spec = spec?.Clone(),
                id = id,
                bg = bg,
                room = room,
                keep = keep,
                recess = recess,
                finish = finish
            };
        }
    }

    public class FinishInfo
    {
        public int top;
        public int floor;
        public int area;
        public string bg;
        public string role;
    }

    public class PartitionInfo
    {
        public string main;
        public string variant;
        public string layout;
        public string density;
        public int requested;
        public int count;
        public int masses;
        public int visits;
        public int retiled;
        public double extra;
        public int rooms;
        public int merges;
        public int recesses;
        public int excavated;
        public int caches;
    }

    /// <summary>
    /// Bounded, demand-first rectangular floorplans.
    /// Supports wings, storeys, staggered, and sliced layouts.
    /// Pure C#, zero UnityEngine references.
    /// </summary>
    public class RRPartitionPlan
    {
        public PartitionInfo info;
        public List<PartitionRegion> masses = new();
        private readonly Func<double> rnd;
        private int[] ports;
        private SpaceProgram program;
        private int visits;
        private int maxVisits = 2400;
        private int W = 48;
        private int H = 25;

        public RRPartitionPlan(Func<double> random)
        {
            rnd = random;
        }

        private int Sx(int v)
        {
            return (int)Math.Round(v * (W - 2) / 46.0);
        }

        private int Sy(int v)
        {
            return (int)Math.Round(v * (H - 2) / 23.0);
        }

        private int N(int a, int b)
        {
            return a + (int)(rnd() * (b - a + 1));
        }

        private void Shuffle<T>(List<T> a)
        {
            for (int i = a.Count - 1; i > 0; i--)
            {
                int j = N(0, i);
                (a[i], a[j]) = (a[j], a[i]);
            }
        }

        private PartitionRegion Rect(int x, int y, int right, int bottom)
        {
            return new PartitionRegion { x0 = x, top = y, x1 = right, floor = bottom };
        }

        public List<PartitionRegion> Build(string theme, string form, int[] boundary, bool modern = false,
            int width = 0, int height = 0)
        {
            W = width > 0 ? Math.Max(RRSynth.MIN_GRID_W, width) : 48;
            H = height > 0 ? Math.Max(RRSynth.MIN_GRID_H, height) : 25;

            ports = boundary;
            visits = 0;
            masses = new List<PartitionRegion>();
            double areaScale = (double)((W - 2) * (H - 2)) / (46.0 * 23.0);
            program = new RRSpaceRules(rnd).Create(theme, form, modern, areaScale);

            PartitionRegion whole = Rect(1, 1, W - 2, H - 2);
            string layout = rnd() < program.levels ? "storeys" :
                (rnd() < 0.28 && form != "cistern" && form != "street_links" && form != "rooftops" ? "staggered" : "sliced");
            if (modern && rnd() < 0.48 && form != "street_links" && form != "rooftops") layout = "wings";

            List<PartitionRegion> outList = layout == "wings" ? Wings(whole, program.rooms) :
                (layout == "storeys" ? Storeys(whole, program.rooms) :
                (layout == "staggered" ? Staggered(whole, program.rooms) : Split(whole, program.rooms)));

            if (outList == null) throw new InvalidOperationException("Partition demand search exhausted: " + layout);

            int shifted = layout == "storeys" || layout == "wings" ? 0 : Retile(outList);
            var usable = new List<PartitionRegion>();
            foreach (var cell in outList)
            {
                if (cell.role == "mass") masses.Add(cell);
                else usable.Add(cell);
            }
            outList = usable;
            ValidatePorts(outList);

            info = new PartitionInfo
            {
                main = program.main,
                variant = program.variant,
                layout = layout,
                density = program.density,
                requested = program.requested,
                count = outList.Count,
                masses = masses.Count,
                visits = visits,
                retiled = shifted,
                extra = program.extra
            };

            return outList;
        }

        private bool Feasible(PartitionRegion r, List<SpaceDemand> items)
        {
            if (items.Count == 0 || r.Width < 5 || r.Height < 3) return false;
            int need = 0, capacity = 0;
            foreach (var p in items)
            {
                if (p.minW > r.Width || p.minH > r.Height || (p.edge == "top" && r.top != 1)) return false;
                need += p.minW * p.minH;
                capacity += p.maxArea.HasValue ? Math.Min(p.maxArea.Value, p.maxW * p.maxH) : p.maxW * p.maxH;
            }
            if (need + (items.Count - 1) * 4 > r.Area || capacity < r.Area) return false;

            if (items.Count == 1)
            {
                var p = items[0];
                if (r.Width > p.maxW || r.Height > p.maxH) return false;
                if (p.maxArea.HasValue && r.Area > p.maxArea.Value) return false;
                if (p.role == "mass")
                {
                    for (int port = 0; port < 22; port++)
                    {
                        if (ports[port] >= 2)
                        {
                            PortRect socket = RRPorts.Rect(port, ports[port], W, H);
                            if (RRPorts.Vertical(port))
                            {
                                if ((port >= 17 ? r.top == 1 : r.floor == H - 2) && r.x0 <= socket.x1 + 1 && r.x1 >= socket.x0 - 1) return false;
                            }
                            else if ((port >= 11 ? r.x0 == 1 : r.x1 == W - 2) && r.top <= socket.y1 && r.floor >= socket.y0) return false;
                        }
                    }
                }
                if (p.role == "canal" && r.top == 1)
                {
                    for (int k = 17; k < 22; k++)
                    {
                        if (ports[k] >= 2)
                        {
                            PortRect b = RRPorts.Rect(k, ports[k], W, H);
                            if (b.x0 >= r.x0 && b.x0 <= r.x1) return false;
                        }
                    }
                }
            }
            return true;
        }

        private bool CutOK(PartitionRegion r, bool vertical, int c)
        {
            for (int p = 0; p < 22; p++)
            {
                if (ports[p] >= 2)
                {
                    PortRect b = RRPorts.Rect(p, ports[p], W, H);
                    if (vertical && RRPorts.Vertical(p) && ((p >= 17 && r.top == 1) || (p < 11 && r.floor == H - 2)) && c >= b.x0 - 1 && c <= b.x1 + 1) return false;
                    if (!vertical && !RRPorts.Vertical(p) && ((p >= 11 && r.x0 == 1) || (p < 6 && r.x1 == W - 2)) && c >= b.y0 && c <= b.y1) return false;
                }
            }
            return true;
        }

        private double Weight(List<SpaceDemand> a)
        {
            double w = 0;
            foreach (var p in a) w += p.weight;
            return w;
        }

        private List<PartitionRegion> Split(PartitionRegion r, List<SpaceDemand> items)
        {
            if (++visits > maxVisits || !Feasible(r, items)) return null;
            if (items.Count == 1)
            {
                var p = items[0];
                var leaf = Rect(r.x0, r.top, r.x1, r.floor);
                leaf.role = p.role;
                leaf.spec = p;
                return new List<PartitionRegion> { leaf };
            }

            for (int t = 0; t < 30 && visits < maxVisits; t++)
            {
                var order = new List<SpaceDemand>(items);
                Shuffle(order);
                if (rnd() < 0.65)
                {
                    string anchor = order[0].role;
                    var ranked = new List<(SpaceDemand item, double score)>();
                    foreach (var related in order)
                    {
                        ranked.Add((related, RRSpaceRules.Affinity(program.theme, anchor, related.role) * (0.35 + rnd())));
                    }
                    ranked.Sort((a, b) => b.score.CompareTo(a.score));
                    order.Clear();
                    foreach (var rankedItem in ranked) order.Add(rankedItem.item);
                }

                int k = N(1, order.Count - 1);
                var left = order.GetRange(0, k);
                var right = order.GetRange(k, order.Count - k);
                bool vertical = rnd() < program.vertical;
                int lo = vertical ? r.x0 + 5 : r.top + 3;
                int hi = vertical ? r.x1 - 5 : r.floor - 3;
                if (lo > hi) continue;

                double fraction = Weight(left) / (Weight(left) + Weight(right));
                int c = rnd() < 0.25 ? N(lo, hi) : (int)((vertical ? r.x0 : r.top) + (vertical ? r.Width : r.Height) * (fraction + (rnd() - 0.5) * 0.34));
                c = Math.Max(lo, Math.Min(hi, c));
                if (!CutOK(r, vertical, c)) continue;

                PartitionRegion a = Rect(r.x0, r.top, vertical ? c - 1 : r.x1, vertical ? r.floor : c - 1);
                PartitionRegion b = Rect(vertical ? c + 1 : r.x0, vertical ? r.top : c + 1, r.x1, r.floor);
                if (!Feasible(a, left) || !Feasible(b, right)) continue;

                var aa = Split(a, left);
                if (aa == null) continue;
                var bb = Split(b, right);
                if (bb != null)
                {
                    aa.AddRange(bb);
                    return aa;
                }
            }
            return null;
        }

        private List<PartitionRegion> Storeys(PartitionRegion r, List<SpaceDemand> items)
        {
            int tall = 0;
            foreach (var p in items) tall = Math.Max(tall, p.minH);

            for (int trial = 0; trial < 80 && visits < maxVisits; trial++)
            {
                visits++;
                int[] counts = program.theme == "mane" ? new[] { 3, 3, 3, 4, 5 } : new[] { 4, 5, 5 };
                int count = tall > 7 ? N(2, 3) : counts[N(0, counts.Length - 1)];
                count = Math.Min(count, Math.Min(items.Count, (r.Height + 1) / 4));
                var hs = new List<int>();
                int remaining = r.Height - (count - 1) - 3 * count;
                if (remaining < 0) continue;
                for (int b = 0; b < count; b++) hs.Add(3);

                int tallBand = tall > 7 ? N(0, count - 1) : -1;
                if (tallBand >= 0)
                {
                    hs[tallBand] = tall;
                    remaining -= tall - 3;
                }
                if (remaining < 0) continue;

                int tries = 0;
                while (remaining > 0 && tries++ < 100)
                {
                    int preferred = program.theme == "mane" ? 7 : 4;
                    var shorter = new List<int>();
                    for (int b = 0; b < count; b++)
                    {
                        if (b != tallBand && hs[b] < preferred) shorter.Add(b);
                    }
                    int bIdx = shorter.Count > 0 ? shorter[N(0, shorter.Count - 1)] : N(0, count - 1);
                    if (hs[bIdx] >= (bIdx == tallBand ? H - 4 : (program.theme == "mane" ? 7 : 6))) continue;
                    hs[bIdx]++;
                    remaining--;
                }
                if (remaining > 0) continue;

                var bands = new List<PartitionRegion>();
                int y = r.top;
                bool good = true;
                for (int b = 0; b < count; b++)
                {
                    bands.Add(Rect(r.x0, y, r.x1, y + hs[b] - 1));
                    if (b < count - 1 && !CutOK(r, false, y + hs[b])) good = false;
                    y += hs[b] + 1;
                }
                if (!good) continue;

                for (int assignment = 0; assignment < 20 && visits < maxVisits; assignment++)
                {
                    visits++;
                    var groups = new List<List<SpaceDemand>>();
                    var used = new List<int>();
                    for (int b = 0; b < count; b++)
                    {
                        groups.Add(new List<SpaceDemand>());
                        used.Add(0);
                    }
                    var pending = new List<SpaceDemand>(items);
                    Shuffle(pending);
                    pending.Sort((a, b) => b.minH.CompareTo(a.minH));
                    good = true;

                    for (int i = 0; i < pending.Count; i++)
                    {
                        var p = pending[i];
                        var choices = new List<(int index, double score)>();
                        int empty = 0;
                        for (int b = 0; b < count; b++) if (groups[b].Count == 0) empty++;

                        for (int b = 0; b < count; b++)
                        {
                            if (pending.Count - i == empty && groups[b].Count > 0) continue;
                            if (hs[b] < p.minH || hs[b] > p.maxH || (p.edge == "top" && bands[b].top != 1)) continue;
                            if (used[b] + p.minW + (groups[b].Count > 0 ? 1 : 0) > r.Width) continue;
                            if (p.role == "canal" && !Feasible(bands[b], new List<SpaceDemand> { p })) continue;
                            choices.Add((b, (r.Width - used[b]) * (0.5 + rnd())));
                        }
                        if (choices.Count == 0) { good = false; break; }
                        choices.Sort((a, b) => b.score.CompareTo(a.score));
                        int chosenBand = choices[0].index;
                        used[chosenBand] += p.minW + (groups[chosenBand].Count > 0 ? 1 : 0);
                        groups[chosenBand].Add(p);
                    }
                    if (!good) continue;

                    for (int b = 0; b < count; b++)
                    {
                        if (!Feasible(bands[b], groups[b])) good = false;
                    }
                    if (!good) continue;

                    var result = new List<PartitionRegion>();
                    for (int b = 0; b < count; b++)
                    {
                        var part = Split(bands[b], groups[b]);
                        if (part == null) { good = false; break; }
                        result.AddRange(part);
                    }
                    if (good) return result;
                }
            }
            return null;
        }

        private List<PartitionRegion> Wings(PartitionRegion r, List<SpaceDemand> items)
        {
            for (int trial = 0; trial < 36 && visits < maxVisits; trial++)
            {
                visits++;
                var order = new List<SpaceDemand>(items);
                Shuffle(order);
                int k = N(2, Math.Max(2, items.Count - 2));
                var aa = order.GetRange(0, k);
                var bb = order.GetRange(k, order.Count - k);
                if (bb.Count == 0) continue;

                bool vertical = rnd() < 0.8;
                int c = vertical ? N(r.x0 + 12, r.x1 - 12) : N(r.top + 7, r.floor - 7);
                if (!CutOK(r, vertical, c)) continue;

                PartitionRegion a = Rect(r.x0, r.top, vertical ? c - 1 : r.x1, vertical ? r.floor : c - 1);
                PartitionRegion b = Rect(vertical ? c + 1 : r.x0, vertical ? r.top : c + 1, r.x1, r.floor);
                if (!Feasible(a, aa) || !Feasible(b, bb)) continue;

                var first = rnd() < 0.7 ? Storeys(a, aa) : Split(a, aa);
                if (first == null) continue;
                var second = rnd() < 0.5 ? Storeys(b, bb) : Split(b, bb);
                if (second != null)
                {
                    first.AddRange(second);
                    return first;
                }
            }
            return null;
        }

        private List<PartitionRegion> Staggered(PartitionRegion r, List<SpaceDemand> items)
        {
            for (int attempt = 0; attempt < 18 && visits < maxVisits; attempt++)
            {
                int lx = N(Sx(8), Sx(19)), rx = N(lx + Sx(6), Sx(38)), ty = N(Sy(6), Sy(11)), by = N(ty + Sy(4), Sy(18));
                var boxes = new List<PartitionRegion>
                {
                    Rect(lx, ty, rx, by),
                    Rect(1, 1, rx, ty - 2),
                    Rect(rx + 2, 1, W - 2, by),
                    Rect(lx, by + 2, W - 2, H - 2),
                    Rect(1, ty, lx - 2, H - 2)
                };

                if (rnd() < 0.5)
                {
                    foreach (var mirror in boxes)
                    {
                        int old = mirror.x0;
                        mirror.x0 = (W - 1) - mirror.x1;
                        mirror.x1 = (W - 1) - old;
                    }
                }

                for (int assignment = 0; assignment < 12 && visits < maxVisits; assignment++)
                {
                    var groups = new List<List<SpaceDemand>>();
                    for (int q = 0; q < 5; q++) groups.Add(new List<SpaceDemand>());
                    var pending = new List<SpaceDemand>(items);
                    pending.Sort((a, b) => (b.minW * b.minH).CompareTo(a.minW * a.minH));
                    bool good = true;

                    for (int i = 0; i < pending.Count; i++)
                    {
                        int empty = 0;
                        for (int q = 0; q < 5; q++) if (groups[q].Count == 0) empty++;
                        var choices = new List<(int index, double score)>();

                        for (int q = 0; q < 5; q++)
                        {
                            if (pending.Count - i == empty && groups[q].Count > 0) continue;
                            var candidate = new List<SpaceDemand>(groups[q]) { pending[i] };
                            int minArea = 0;
                            bool ok = true;
                            foreach (var item in candidate)
                            {
                                minArea += item.minW * item.minH;
                                if (item.minW > boxes[q].Width || item.minH > boxes[q].Height) ok = false;
                            }
                            if (ok && minArea + 4 * (candidate.Count - 1) <= boxes[q].Area)
                            {
                                choices.Add((q, boxes[q].Area / (Weight(candidate) + 0.5) * (0.35 + rnd())));
                            }
                        }
                        if (choices.Count == 0) { good = false; break; }
                        choices.Sort((a, b) => b.score.CompareTo(a.score));
                        groups[choices[0].index].Add(pending[i]);
                    }
                    if (!good) continue;

                    for (int q = 0; q < 5; q++)
                    {
                        if (!Feasible(boxes[q], groups[q])) good = false;
                    }
                    if (!good) continue;

                    var outList = new List<PartitionRegion>();
                    for (int q = 0; q < 5; q++)
                    {
                        var part = Split(boxes[q], groups[q]);
                        if (part == null) { good = false; break; }
                        outList.AddRange(part);
                    }
                    if (good) return outList;
                }
            }
            return null;
        }

        private int Retile(List<PartitionRegion> a)
        {
            int changed = 0;
            for (int t = 0; t < 12; t++)
            {
                int i = N(0, a.Count - 1), j = N(0, a.Count - 1);
                if (i == j) continue;
                var p = a[i];
                var q = a[j];
                bool side = p.top == q.top && p.floor == q.floor && (p.x1 + 2 == q.x0 || q.x1 + 2 == p.x0);
                bool stack = p.x0 == q.x0 && p.x1 == q.x1 && (p.floor + 2 == q.top || q.floor + 2 == p.top);
                if (!side && !stack) continue;

                var box = Rect(Math.Min(p.x0, q.x0), Math.Min(p.top, q.top), Math.Max(p.x1, q.x1), Math.Max(p.floor, q.floor));
                maxVisits = visits + 100;
                var fresh = Split(box, new List<SpaceDemand> { p.spec, q.spec });
                maxVisits = 2400;

                if (fresh != null && (p.x0 != fresh[0].x0 || p.top != fresh[0].top || p.x1 != fresh[0].x1 || p.floor != fresh[0].floor))
                {
                    a[i] = fresh[0];
                    a[j] = fresh[1];
                    changed++;
                }
            }
            return changed;
        }

        private void ValidatePorts(List<PartitionRegion> rooms)
        {
            for (int p = 0; p < 22; p++)
            {
                if (ports[p] >= 2)
                {
                    PortRect b = RRPorts.Rect(p, ports[p], W, H);
                    bool found = false;
                    foreach (var r in rooms)
                    {
                        if (RRPorts.Vertical(p))
                        {
                            if ((p >= 17 ? r.top == 1 : r.floor == H - 2) && r.x0 <= b.x0 - 1 && r.x1 >= b.x1 + 1) found = true;
                        }
                        else if ((p >= 11 ? r.x0 == 1 : r.x1 == W - 2) && r.top <= b.y0 && r.floor >= b.y1)
                        {
                            found = true;
                        }
                    }
                    if (!found) throw new InvalidOperationException("Partition splits a shared socket " + p);
                }
            }
        }
    }
}
