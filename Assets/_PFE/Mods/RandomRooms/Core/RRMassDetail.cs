using System;
using System.Collections.Generic;

namespace PFE.Mods.RandomRooms.Core
{
    /// <summary>
    /// Excavates recesses in solid masses. Some become sealed caches behind weak 'F' tiles.
    /// Pure C#, zero UnityEngine references.
    /// </summary>
    public class RRMassDetail
    {
        private readonly RRArchitecture p;
        private readonly Func<double> rnd;

        public RRMassDetail(RRArchitecture plan, Func<double> random)
        {
            p = plan;
            rnd = random;
        }

        private int N(int a, int b)
        {
            return a + (int)(rnd() * (b - a + 1));
        }

        public void Build()
        {
            var candidates = new List<CacheInfo>();

            foreach (var r in p.regions)
            {
                if (r.role != "canal" && r.role != "street")
                {
                    int[] dirs = { -1, 1 };
                    foreach (int dir in dirs)
                    {
                        int face = dir > 0 ? r.x1 + 1 : r.x0 - 1;
                        int f = r.floor;
                        int depth = N(4, 7);
                        int top = f - 2;
                        int end = face + dir * (depth - 1);
                        int lo = Math.Min(face, end);
                        int hi = Math.Max(face, end);

                        if (lo < 3 || hi > 44 || top < 2 || f > 22) continue;

                        bool ok = true;
                        for (int x = face - dir * 2; x != face; x += dir)
                        {
                            if (!p.Support(x, f)) ok = false;
                            for (int y = f - 1; y <= f; y++)
                            {
                                if (p.Solid(x, y) || p.reserved.Contains((y, x))) ok = false;
                            }
                        }

                        for (int y = top - 1; y <= f + 1; y++)
                        {
                            for (int x = lo; x <= hi; x++)
                            {
                                if (!p.Solid(x, y) || p.reserved.Contains((y, x))) ok = false;
                            }
                        }

                        for (int y = top; y <= f; y++)
                        {
                            if (!p.Solid(end + dir, y)) ok = false;
                        }

                        if (ok)
                        {
                            candidates.Add(new CacheInfo
                            {
                                face = face,
                                dir = dir,
                                x0 = lo,
                                x1 = hi,
                                top = top,
                                floor = f,
                                parent = r,
                                score = rnd()
                            });
                        }
                    }
                }
            }

            candidates.Sort((a, b) => a.score.CompareTo(b.score));

            int changed = 0;
            int removed = 0;
            int target = N(1, p.theme == "sewer" ? 3 : 2);

            foreach (var c in candidates)
            {
                if (changed >= target) break;

                bool ok = true;
                for (int y = c.top; y <= c.floor; y++)
                {
                    for (int x = c.x0; x <= c.x1; x++)
                    {
                        if (!p.Solid(x, y) || p.reserved.Contains((y, x))) ok = false;
                    }
                }
                if (!ok) continue;

                bool sealedCache = p.caches.Count == 0 && rnd() < 0.48;
                var before = new List<(int x, int y, string code)>();

                for (int y = c.top; y <= c.floor; y++)
                {
                    for (int x = c.x0; x <= c.x1; x++)
                    {
                        before.Add((x, y, p.grid[y, x]));
                        p.grid[y, x] = (sealedCache && x == c.face) ? ("F" + c.parent.bg) : ("_" + c.parent.bg);
                        if (sealedCache) p.hiddenCells.Add(y * 48 + x);
                    }
                }

                c.sealedCache = sealedCache;
                p.caches.Add(c);

                if (!Valid())
                {
                    p.caches.RemoveAt(p.caches.Count - 1);
                    foreach (var cell in before)
                    {
                        p.grid[cell.y, cell.x] = cell.code;
                        p.hiddenCells.Remove(cell.y * 48 + cell.x);
                    }
                    continue;
                }

                changed++;
                removed += (c.x1 - c.x0 + 1 - (sealedCache ? 1 : 0)) * 3;

                if (sealedCache)
                {
                    p.Reserve(c.x0, c.top, c.x1, c.floor);
                    c.x = c.dir > 0 ? c.x1 - 1 : c.x0;
                    c.y = c.floor;
                }
                else
                {
                    p.caches.RemoveAt(p.caches.Count - 1);
                    p.regions.Add(new PartitionRegion
                    {
                        id = p.regions.Count,
                        room = c.parent.room,
                        x0 = c.x0,
                        x1 = c.x1,
                        top = c.top,
                        floor = c.floor,
                        role = c.parent.role,
                        bg = c.parent.bg,
                        recess = true
                    });
                }

                p.Reserve(Math.Min(c.face - c.dir * 2, c.face), c.floor - 1, Math.Max(c.face - c.dir * 2, c.face), c.floor);
            }

            if (p.partitionInfo != null)
            {
                p.partitionInfo.recesses = changed;
                p.partitionInfo.excavated = removed;
                p.partitionInfo.caches = p.caches.Count;
            }
        }

        private bool Valid()
        {
            var saved = new List<(int x, int y, string code)>();
            var hidden = new HashSet<int>(p.hiddenCells);
            bool good = false;

            try
            {
                RRTraversal.Check(p);
                if (p.pools.Count > 0) RRTraversal.Check(p, true);

                foreach (var c in p.caches)
                {
                    if (c.sealedCache)
                    {
                        for (int y = c.top; y <= c.floor; y++)
                        {
                            saved.Add((c.face, y, p.grid[y, c.face]));
                            p.grid[y, c.face] = "_" + c.parent.bg;
                        }
                    }
                }

                p.hiddenCells.Clear();
                RRTraversal.Check(p);
                if (p.pools.Count > 0) RRTraversal.Check(p, true);
                good = true;
            }
            catch
            {
                good = false;
            }

            foreach (var cell in saved)
            {
                p.grid[cell.y, cell.x] = cell.code;
            }

            p.hiddenCells = hidden;
            return good;
        }
    }
}
