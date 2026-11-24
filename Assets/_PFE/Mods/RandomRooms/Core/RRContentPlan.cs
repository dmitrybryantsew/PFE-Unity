using System;
using System.Collections.Generic;

namespace PFE.Mods.RandomRooms.Core
{
    public class DVZone
    {
        public string id;
        public List<PartitionRegion> regions = new();
        public string role;
        public int area;
        public int largest;
        public double pressure;
        public int d;
        public int v;
        public double limit;
    }

    public class ContentPoint
    {
        public string uid;
        public string id;
        public string zone;
        public int x;
        public int y;
        public string mount;
        public string kind;
        public double cost;
        public string reason;
        public int sector = -1;
        public int floorY;
        public int operatorNode = -1;
    }

    public class TerminalInfo
    {
        public string uid;
        public int x;
        public int y;
        public int node;
        public List<int> path;
        public List<(int slot, string state)> entries = new();
        public int from;
    }

    /// <summary>
    /// Danger & Value tactical director (v13.2).
    /// Places encounters, turrets, security terminals, wildlife, hazards, rewards, services.
    /// Pure C#, zero UnityEngine references.
    /// </summary>
    public class RRContentPlan
    {
        public List<DVZone> zones = new();
        public List<ContentPoint> points = new();
        public List<TurretTacticsInfo> turrets = new();
        public TerminalInfo terminal;
        public int danger;
        public int value;
        public bool peaceful;

        private readonly RRArchitecture p;
        private readonly RRPopulation pop;
        private readonly RRSeed seed;
        private readonly GenerationContext context;
        private readonly RRTactics tactics;
        private readonly RREncounters encounters;
        private int enemies = 0;
        private int specials = 0;
        private int ambient = 0;
        private bool reinforcement = false;

        private static readonly Dictionary<string, int[]> BIAS = new()
        {
            { "workshop", new[] { 10, 0 } }, { "warehouse", new[] { 4, 18 } }, { "service", new[] { -8, 2 } },
            { "control", new[] { 8, 16 } }, { "store", new[] { -12, 20 } }, { "living", new[] { -14, -4 } },
            { "medical", new[] { -8, 23 } }, { "office", new[] { -7, 7 } }, { "corridor", new[] { -2, -20 } },
            { "canal", new[] { 8, -14 } }, { "kitchen", new[] { -10, 2 } }, { "hall", new[] { 10, -8 } }
        };

        public RRContentPlan(RRArchitecture plan, RRPopulation population, RRSeed rng, GenerationContext options)
        {
            p = plan;
            pop = population;
            seed = rng;
            context = options;

            peaceful = pop.mood == "arrival" || pop.mood == "showroom";
            if (!peaceful) pop.mood = "planned";

            tactics = new RRTactics(p);
            var baseRng = seed.Fork("baseline");
            double shared = baseRng.Next();

            danger = Clamp(5 + 90 * (0.7 * baseRng.Next() + 0.3 * shared));
            value = Clamp(5 + 90 * (0.7 * baseRng.Next() + 0.3 * shared));

            if (options != null && options.city != null)
            {
                // Optional override if specified in context
            }

            if (peaceful) danger = 0;
            encounters = new RREncounters(p, tactics, danger);

            var byId = new Dictionary<string, DVZone>();
            foreach (var r in p.regions)
            {
                string key = "room-" + r.room;
                if (!byId.TryGetValue(key, out var z))
                {
                    z = new DVZone { id = key, role = r.role, area = 0, largest = 0, pressure = 0 };
                    byId[key] = z;
                    zones.Add(z);
                }
                z.regions.Add(r);
                int area = (r.x1 - r.x0 + 1) * (r.floor - r.top + 1);
                z.area += area;
                if (area > z.largest)
                {
                    z.role = r.role;
                    z.largest = area;
                }
            }

            // Assign galleries to nearest zone
            foreach (var r in p.regions)
            {
                if (r.id < 0) // gallery
                {
                    DVZone nearest = null;
                    double best = 1e9;
                    foreach (var z in zones)
                    {
                        foreach (var part in z.regions)
                        {
                            double dist = Math.Abs(r.x0 + r.x1 - part.x0 - part.x1) + Math.Abs(r.top + r.floor - part.top - part.floor);
                            if (dist < best)
                            {
                                best = dist;
                                nearest = z;
                            }
                        }
                    }
                    if (nearest != null) nearest.regions.Add(r);
                }
            }

            foreach (var z in zones)
            {
                var local = seed.Fork("values:" + z.id);
                double common = local.Next() * 2.0 - 1.0;
                int[] bias = BIAS.TryGetValue(z.role, out var b) ? b : new[] { 0, 0 };

                z.d = peaceful ? 0 : Clamp(danger + bias[0] + 32 * (0.7 * (local.Next() * 2.0 - 1.0) + 0.3 * common));
                z.v = Clamp(value + bias[1] + 32 * (0.7 * (local.Next() * 2.0 - 1.0) + 0.3 * common));
                z.limit = peaceful ? 0 : 1.8 + 5.0 * z.d / 100.0;

                foreach (var r in z.regions)
                {
                    // Update metadata if needed
                }
            }
        }

        private static int Clamp(double v)
        {
            return Math.Max(0, Math.Min(100, (int)Math.Round(v)));
        }

        private RRSeed Rng(string label, DVZone z = null)
        {
            return seed.Fork(label + (z != null ? ":" + z.id : ""));
        }

        private bool Belongs(DVZone z, int x, int y)
        {
            foreach (var r in z.regions)
            {
                if (x >= r.x0 && x <= r.x1 && y >= r.top && y <= r.floor) return true;
            }
            return false;
        }

        private List<CandidatePosition> Choices(DVZone z, string id, string mount, string kind, string label)
        {
            if (RRPopulation.WallFixture(id)) mount = "fixture";
            var outList = new List<CandidatePosition>();
            var r = Rng(label, z);

            foreach (var a in pop.Positions(id, mount, kind, null, false))
            {
                if (Belongs(z, a.x, a.y))
                {
                    const int W = 48;
                    if (mount == "floor" && !(tactics.stand[a.y * W + a.x] || (a.x > 0 && tactics.stand[a.y * W + a.x - 1]))) continue;

                    if (mount == "fixture")
                    {
                        int op = -1;
                        int[] dxs = { 0, -1, -2, 1 };
                        foreach (int dx in dxs)
                        {
                            int n = a.floorY * W + a.x + dx;
                            if (n >= 0 && n < tactics.stand.Length && tactics.stand[n] && tactics.clear[n] && OperatorFloor(n))
                            {
                                op = n;
                                break;
                            }
                        }
                        if (op < 0 || !Belongs(z, a.x, a.floorY)) continue;
                        a.operatorNode = op;
                    }

                    if (terminal != null && (kind == "enemy" || kind == "special" || kind == "ambient" || kind == "hazard" || kind == "trigger" || kind == "damager") &&
                        Math.Abs(a.x - terminal.x) + Math.Abs(a.y - terminal.y) < 4)
                    {
                        continue;
                    }

                    a.score = r.Fork(a.x + "," + a.y).Next();
                    if (kind == "enemy")
                    {
                        a.score -= Math.Min(12, encounters.Separation(a.x, a.y)) * 0.07;
                    }
                    outList.Add(a);
                }
            }

            outList.Sort((a, b) => a.score.CompareTo(b.score));
            return outList;
        }

        private PopulationObject Put(DVZone z, string id, CandidatePosition a, string kind, string mount = "floor", double cost = 0, Dictionary<string, string> attrs = null, string reason = "")
        {
            if (cost > 0 && z.pressure + cost > z.limit + 0.0001) return null;
            if (!encounters.Allows(a.x, a.y, kind, cost)) return null;
            if (RRPopulation.WallFixture(id)) mount = "fixture";

            var o = pop.Put(id, a, kind, mount, attrs);
            o.zone = z.id;
            o.danger = z.d;
            o.value = z.v;
            z.pressure += cost;

            var point = new ContentPoint
            {
                uid = o.uid,
                id = id,
                zone = z.id,
                x = a.x,
                y = a.y,
                mount = mount,
                kind = kind,
                cost = cost,
                reason = reason
            };

            point.sector = encounters.Record(a.x, a.y, kind, cost);
            o.sector = point.sector;

            if (mount == "fixture")
            {
                point.floorY = a.floorY;
                point.operatorNode = a.operatorNode;
                const int W = 48;
                p.Reserve(a.operatorNode % W, a.operatorNode / W - 1, a.operatorNode % W + 1, a.operatorNode / W);
            }

            points.Add(point);
            return o;
        }

        private PopulationObject Add(DVZone z, string id, string kind, string mount = "floor", double cost = 0, Dictionary<string, string> attrs = null, string label = "", string reason = "")
        {
            if (cost > 0 && z.pressure + cost > z.limit + 0.0001) return null;
            var a = Choices(z, id, mount, kind, !string.IsNullOrEmpty(label) ? label : (kind + ":" + id));
            foreach (var pos in a)
            {
                var o = Put(z, id, pos, kind, mount, cost, attrs, reason);
                if (o != null) return o;
            }
            return null;
        }

        public void Build()
        {
            if (!peaceful)
            {
                Security();
                SecurityTerminal();

                var order = new List<DVZone>(zones);
                order.Sort((a, b) => Rng("encounter-order", a).Next().CompareTo(Rng("encounter-order", b).Next()));

                foreach (var z in order) Major(z);
                foreach (var z in zones) Wildlife(z);
                foreach (var z in zones) Hazards(z);
                foreach (var z in zones) Fixtures(z);
                foreach (var z in zones) Special(z);
            }

            // Value rolls are deliberately last and never consume encounter RNG
            foreach (var z in zones)
            {
                Rewards(z);
                Services(z);
            }

            HiddenRewards();

            if (peaceful)
            {
                foreach (var o in pop.objects)
                {
                    o.attrs["mine"] = "0";
                    if (pop.mood == "showroom") o.attrs["lock"] = "0";
                }
            }
        }

        private void Security()
        {
            if (!pop.ecology.Armed() || p.theme == "sewer") return;

            foreach (var z in zones)
            {
                var r = Rng("turrets", z);
                if (turrets.Count >= 3 || z.limit < 2 || r.Next() >= 0.02 + 0.5 * z.d / 100.0) continue;

                string[] mounts = r.Next() < 0.6 ? new[] { "ceiling", "floor" } : new[] { "floor", "ceiling" };

                foreach (var mount in mounts)
                {
                    string id = mount == "ceiling" ? (r.Next() < 0.15 ? "hturret" : "turret") : (r.Next() < 0.25 ? "armturret" : "landturret");
                    var candidates = Choices(z, id, mount, "security", "turret:" + mount);
                    var ranked = new List<TurretTacticsInfo>();

                    foreach (var a in candidates)
                    {
                        if (mount == "ceiling" && (!p.Solid(a.x - 1, a.y - 1) || !p.Solid(a.x + 1, a.y - 1))) continue;

                        var t = new TurretTacticsInfo
                        {
                            x = a.x,
                            y = a.y,
                            id = id,
                            mount = mount,
                            turn = a.x < 24 ? 1 : -1,
                            ox = a.x + 0.5,
                            oy = a.y + (mount == "ceiling" ? 0.675 : (id == "landturret" ? -0.525 : -0.4)),
                            zone = z.id
                        };

                        if (!tactics.ProtectsEntry(t)) continue;

                        double score = -1;
                        TurretTargetInfo target = null;
                        var anchors = new List<(double x, double y, string kind)>();
                        foreach (var link in p.links) anchors.Add((link.x, link.y, link.kind));
                        foreach (var ladder in p.ladders) anchors.Add((ladder.x, ladder.bottom, "ladder"));

                        foreach (var anchor in anchors)
                        {
                            double ax = anchor.x + 0.5, ay = anchor.y - 0.2;
                            double d = Math.Sqrt((ax - t.ox) * (ax - t.ox) + (ay - t.oy) * (ay - t.oy));
                            if (d < 4 || d > 18 || !tactics.Exposed(t, ax, ay)) continue;
                            if (mount == "ceiling" && ay <= t.oy + 1) continue;

                            double s = 8 - Math.Abs(d - 8) * 0.3 + a.score;
                            if (s > score)
                            {
                                score = s;
                                target = new TurretTargetInfo { x = ax, y = ay, kind = anchor.kind };
                            }
                        }

                        if (target != null)
                        {
                            t.target = target;
                            t.score = score;
                            ranked.Add(t);
                        }
                    }

                    ranked.Sort((a, b) => b.score.CompareTo(a.score));
                    if (ranked.Count == 0) continue;

                    var chosenT = ranked[0];
                    var pos = new CandidatePosition { x = chosenT.x, y = chosenT.y };
                    var xml = Put(z, chosenT.id, pos, "security", mount, 2, null, "guard-link");
                    if (xml == null) continue;

                    chosenT.uid = xml.uid;
                    turrets.Add(chosenT);

                    int steps = (int)Math.Ceiling(Math.Max(Math.Abs(chosenT.target.x - chosenT.ox), Math.Abs(chosenT.target.y - chosenT.oy)) * 3.0);
                    for (int k = 1; k < steps; k++)
                    {
                        int xx = (int)(chosenT.ox + (chosenT.target.x - chosenT.ox) * k / steps);
                        int yy = (int)(chosenT.oy + (chosenT.target.y - chosenT.oy) * k / steps);
                        p.Reserve(xx, yy, xx, yy);
                    }
                    break;
                }
            }
        }

        private void SecurityTerminal()
        {
            if (turrets.Count == 0 || Rng("terminal-chance").Next() >= 0.62) return;
            var blocked = tactics.BlockedBy(turrets);
            var reaches = new List<TacticsReach>();
            var all = new List<TacticsReach>();

            foreach (var e in tactics.entries)
            {
                reaches.Add(tactics.Flood(e.node, blocked));
                all.Add(tactics.Flood(e.node));
            }

            CandidatePosition best = null;
            DVZone bestZ = null;
            double bestScore = -1e9;
            List<int> bestPath = null;
            List<(int slot, string state)> bestAccess = null;
            int bestFrom = -1;

            const int W = 48;
            foreach (var z in zones)
            {
                foreach (var a in Choices(z, "term1", "floor", "terminal", "terminal-position"))
                {
                    int[] dxs = { -2, 1 };
                    foreach (int dx in dxs)
                    {
                        int node = a.floorY * W + a.x + dx;
                        if (node < 0 || node >= tactics.stand.Length || !tactics.stand[node] || !tactics.clear[node] || blocked.Contains(node) || !OperatorFloor(node)) continue;

                        var path = new List<int>();
                        int from = -1;
                        double dist = 1e9;
                        var access = new List<(int slot, string state)>();

                        for (int i = 0; i < reaches.Count; i++)
                        {
                            var reach = reaches[i];
                            bool safe = reach.distances.ContainsKey(node);
                            string state = safe ? "covered-route" : (all[i].distances.ContainsKey(node) ? "exposed-route" : "unproven");
                            access.Add((tactics.entries[i].slot, state));

                            if (safe && reach.distances[node] >= 5 && reach.distances[node] < dist)
                            {
                                dist = reach.distances[node];
                                from = i;
                                path = tactics.Path(reach, node);
                            }
                        }

                        if (from < 0) continue;

                        double score = (z.role == "control" ? 6 : (z.role == "service" || z.role == "office" ? 4 : 0)) +
                            (p.Solid(a.x - 1, a.y) || p.Solid(a.x + 1, a.y) ? 3 : 0) - dist * 0.025 + a.score;

                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestZ = z;
                            best = a;
                            best.operatorNode = node;
                            bestPath = path;
                            bestAccess = access;
                            bestFrom = tactics.entries[from].slot;
                        }
                    }
                }
            }

            if (best == null) return;
            var xml = Put(bestZ, "term1", best, "terminal", "floor", 0, null, "location-turret-control");
            terminal = new TerminalInfo
            {
                uid = xml.uid,
                x = best.x,
                y = best.y,
                node = best.operatorNode,
                path = bestPath,
                entries = bestAccess,
                from = bestFrom
            };
            tactics.ReservePath(bestPath);
            p.Reserve(best.operatorNode % W - 1, best.operatorNode / W - 2, best.operatorNode % W + 2, best.operatorNode / W);
        }

        private bool OperatorFloor(int node)
        {
            const int W = 48;
            int x = node % W, y = node / W;
            if (x < 1 || x > 45 || !p.Support(x, y) || !p.Support(x + 1, y)) return false;

            for (int yy = y - 1; yy <= y; yy++)
            {
                for (int xx = x; xx <= x + 1; xx++)
                {
                    if (p.reserved.Contains((yy, xx))) return false;
                    string cell = p.grid[yy, xx];
                    if (cell.Contains("А") || cell.Contains("В") || cell.Contains("Г")) return false;
                }
            }
            return true;
        }

        private void Major(DVZone z)
        {
            var r = Rng("main", z);
            if (r.Next() >= 0.04 + 0.82 * z.d / 100.0) return;
            int count = Math.Min(3, 1 + (int)(z.d / 48.0) + (r.Next() < 0.2 ? 1 : 0));
            var ecologySeed = Rng("main-model", z);
            var eco = new RREcology(p.theme, pop.ecology.difficulty, 0, () => ecologySeed.Next())
            {
                type = pop.ecology.type
            };

            for (int i = 0; i < count && enemies < 8; i++)
            {
                string id = eco.Large();
                if (id.Contains("turret")) id = "robot";
                if (Add(z, id, "enemy", "floor", 1, null, "main:" + i, "major-group") != null) enemies++;
            }
        }

        private void Wildlife(DVZone z)
        {
            if (ambient >= 4 || Rng("ambient-chance", z).Next() >= 0.3) return;
            var r = Rng("ambient-model", z);
            var eco = new RREcology(p.theme, pop.ecology.difficulty, 0, () => r.Next())
            {
                type = pop.ecology.type
            };
            string id = eco.Small(), mount = "floor";
            if (eco.type == 2) return;
            if (r.Next() < 0.25) { id = eco.Ceiling(); mount = "ceiling"; }
            if (id.Contains("turret")) { id = "rat"; mount = "floor"; }

            if (Add(z, id, "ambient", mount, 0.35, null, "ambient", "ambient-ecology") != null) ambient++;
        }

        private double Blind(CandidatePosition a)
        {
            int seen = 0, total = 0;
            foreach (var e in tactics.entries)
            {
                total++;
                if (tactics.Line(e.x + 1, e.y, a.x + 0.5, a.y + 0.5)) seen++;
            }
            foreach (var link in p.links)
            {
                total++;
                if (tactics.Line(link.x + 0.5, link.y - 0.2, a.x + 0.5, a.y + 0.5)) seen++;
            }
            return total > 0 ? 1.0 - (double)seen / total : 0;
        }

        private void Special(DVZone z)
        {
            if (specials >= 3 || !pop.ecology.Armed()) return;
            var r = Rng("special", z);
            double chance = 0.06 + 0.60 * Math.Pow(1.0 - z.d / 100.0, 0.8) * Math.Pow(z.v / 100.0, 1.2);
            if (r.Next() >= chance || z.pressure + 1.5 > z.limit) return;

            var ids = new List<string> { "mine" };
            if (pop.ecology.type == 2 && pop.ecology.difficulty >= 4) ids.AddRange(new[] { "msp", "roller" });
            if (pop.ecology.difficulty >= 3) ids.Add(pop.ecology.type == 2 ? "spritebot" : "vortex");

            string id = ids[r.NextInt(ids.Count)];
            string mount = (id == "spritebot" || id == "vortex") ? "air" : "floor";
            var a = Choices(z, id, mount, "special", "special-position");

            foreach (var pos in a)
            {
                if (mount == "air") pos.score += Math.Abs(pos.y - z.regions[0].floor + 2) * 0.2;
                else pos.score -= Blind(pos) * 1.5;
            }

            a.Sort((pos1, pos2) => pos1.score.CompareTo(pos2.score));
            foreach (var pos in a)
            {
                if (Put(z, id, pos, "special", mount, 1.5, null, mount == "air" ? "cache-patrol" : "cache-blindspot") != null)
                {
                    specials++;
                    break;
                }
            }
        }

        private void Hazards(DVZone z)
        {
            var r = Rng("hazards", z);
            if (r.Next() > 0.12 || z.pressure + 1 > z.limit) return;
            var ecoSeed = Rng("hazard-model", z);
            var eco = new RREcology(p.theme, pop.ecology.difficulty, r.NextInt(2), () => ecoSeed.Next())
            {
                type = pop.ecology.type
            };
            string id = eco.Trap();
            if (id == "mine") return;

            if (id == "trplate" || id == "trridge" || id == "trlaser")
            {
                var triggers = Choices(z, id, "floor", "trigger", "trigger");
                string weapon = r.Next() < 0.5 ? "damshot" : "damgren";
                string mount = weapon == "damgren" ? "ceiling" : "floor";
                var guns = Choices(z, weapon, mount, "damager", "circuit");

                foreach (var a in triggers)
                {
                    foreach (var b in guns)
                    {
                        if (Math.Abs(a.x - b.x) >= 4 && Math.Abs(a.x - b.x) <= 8 && (weapon != "damshot" || Math.Abs(a.y - b.y) <= 1) &&
                            tactics.Line(a.x + 0.5, a.y + 0.5, b.x + 0.5, b.y + 0.5))
                        {
                            string group = "rrc_" + seed.seed + "_" + z.id;
                            var gunAttrs = new Dictionary<string, string> { { "allid", group }, { "turn", b.x < a.x ? "1" : "-1" } };
                            if (Put(z, weapon, b, "damager", mount, 1, gunAttrs, "paired-trap") == null) continue;
                            var trigAttrs = new Dictionary<string, string> { { "allid", group }, { "res", "" } };
                            Put(z, id, a, "trigger", "floor", 0, trigAttrs, "paired-trap");
                            return;
                        }
                    }
                }
            }
            else
            {
                var attrs = id == "slime" ? new Dictionary<string, string> { { "tr", "10" } } :
                    (id == "trcans" ? new Dictionary<string, string> { { "res", "noise" } } : null);
                Add(z, id, "hazard", "floor", 1, attrs, "hazard", "native-ecology-trap");
            }
        }

        private void Rewards(DVZone z)
        {
            var r = Rng("rewards", z);
            string[] ids;
            if (z.role == "medical") ids = new[] { "medbox", "bigmed", "case" };
            else if (z.role == "office" || z.role == "living") ids = new[] { "bookcase", "case", "medbox" };
            else if (z.role == "control") ids = new[] { "instr2", "case", "chest" };
            else ids = p.theme == "sewer" ? new[] { "chest", "instr1", "case" } : new[] { "chest", "ammobox", "instr2", "explbox" };

            if (r.Next() < 0.65) Add(z, ids[r.NextInt(ids.Length)], "loot", "floor", 0, null, "loot", "ordinary-supplies");

            if (r.Next() < 0.04 + 0.8 * z.v / 100.0)
            {
                ids = p.theme == "sewer" ? new[] { "safe", "chest", "instr2" } : new[] { "safe", "wallsafe", "weapcase", "weapbox" };
                string id = ids[r.NextInt(ids.Length)];
                var box = Add(z, id, "reward", "floor", 0, null, "prize", "high-value-opportunity");

                if (box != null && id == "wallsafe" && p.theme != "sewer" && r.Next() < 0.35)
                {
                    box.attrs["hack"] = "1";
                    Add(z, "term2", "terminal", "floor", 0, null, "lock-terminal", "optional-cache-control");
                }
                else if (box != null && r.Next() < 0.12)
                {
                    var button = Add(z, p.theme == "stable" ? "knop1" : "knop3", "switch", "floor", 0, null, "cache-switch", "optional-cache-control");
                    if (button != null)
                    {
                        button.unlockTarget = box.uid;
                    }
                }
            }

            if (p.theme != "sewer" && r.Next() < 0.04 + 0.08 * z.v / 100.0)
            {
                Add(z, "term3", "terminal", "floor", 0, null, "data-terminal", "native-data-loot");
            }
        }

        private void Fixtures(DVZone z)
        {
            var r = Rng("fixtures", z);
            if (!reinforcement && z.d >= 40 && Rng("reinforcement", z).Next() < 0.035 &&
                (z.role == "workshop" || z.role == "control" || z.role == "service" || z.role == "warehouse"))
            {
                string hiddenId = pop.ecology.Hidden();
                if (!string.IsNullOrEmpty(hiddenId) && Add(z, hiddenId, "reinforcement", "floor", 2, null, "reinforcement", "native-response-device") != null)
                {
                    reinforcement = true;
                }
            }

            if (r.Next() > 0.08) return;

            if (p.theme != "stable" && r.Next() < 0.6)
            {
                string id = r.Next() < 0.35 ? "fspikes" : "spikes";
                Add(z, id, "hazard", id == "fspikes" ? "ceiling" : "floor", 1, null, "spikes", "optional-environment-hazard");
            }
            else if (p.theme != "mane")
            {
                Add(z, r.Next() < 0.25 ? "radbigbarrel" : "radbarrel", "hazard", "floor", 0.5, null, "radiation", "industrial-residue");
            }

            if (p.theme == "sewer" && ambient < 4 && Rng("fish", z).Next() < 0.4)
            {
                foreach (var pool in p.pools)
                {
                    int x = (pool.x0 + pool.x1) / 2, y = pool.bottom;
                    if (Belongs(z, x, y) && pop.Fit("fish", x, y, "swim", "ambient"))
                    {
                        var pos = new CandidatePosition { x = x, y = y };
                        if (Put(z, "fish", pos, "ambient", "swim", 0.35, null, "water-ecology") != null)
                        {
                            ambient++;
                            break;
                        }
                    }
                }
            }
        }

        private void Services(DVZone z)
        {
            var r = Rng("services", z);
            if (r.Next() > 0.24) return;

            string id = "";
            if (z.role == "workshop" || z.role == "service" || z.role == "control") id = "work";
            if (z.role == "medical" && p.theme != "plant" && p.theme != "sewer") id = "himlab";
            if (z.role == "kitchen") id = "stove";
            if (string.IsNullOrEmpty(id) && p.theme != "sewer" && z.d < 30 && r.Next() < 0.16)
            {
                id = r.Next() < 0.7 ? "vendor" : "doctor";
            }

            if (!string.IsNullOrEmpty(id))
            {
                Add(z, id, "service", "floor", 0, null, "service", "room-purpose");
            }
        }

        private void HiddenRewards()
        {
            foreach (var c in p.caches)
            {
                DVZone z = null;
                foreach (var candidate in zones)
                {
                    if (candidate.regions.Contains(c.parent))
                    {
                        z = candidate;
                        break;
                    }
                }
                if (z == null) continue;

                var r = Rng("hidden-reward", z);
                bool valuable = r.Next() < 0.12 + 0.75 * z.v / 100.0;
                string id = valuable ? (r.Next() < 0.65 ? "safe" : "weapbox") : "chest";
                var pos = new CandidatePosition { x = c.x, y = c.y };
                var attrs = new Dictionary<string, string> { { "mine", "0" } };
                var o = Put(z, id, pos, "cache", "floor", 0, attrs, "breakable-wall-cache");
                if (o != null)
                {
                    o.cacheFace = c.face;
                }
            }
        }
    }
}
