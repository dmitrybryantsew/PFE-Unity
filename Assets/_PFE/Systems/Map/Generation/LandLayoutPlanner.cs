using System;
using System.Collections.Generic;
using UnityEngine;
using PFE.Core.Rng;
using PFE.Data.Definitions.Campaign;

namespace PFE.Systems.Map.Generation
{
    /// <summary>
    /// The engine-free inputs a procedural layout needs. Kept separate from
    /// <see cref="LandDefinition"/> so the planner can be tested without a ScriptableObject.
    /// </summary>
    public struct LandLayoutRequest
    {
        public string LandId;
        public int Conf;
        public int GridWidth;
        public int GridHeight;
        public int LandStage;
        public Vector2Int EntryCell;
        /// <summary>AS3 <c>LandAct.visited</c> — gates the <c>beg*</c> entry room.</summary>
        public bool Visited;
        /// <summary>AS3 <c>triggers["mbase_visited"] &gt; 0</c> — gates conf 4's <c>beg0</c>.</summary>
        public bool MbaseVisited;
    }

    /// <summary>
    /// Ports the layout half of AS3 <c>fe.loc.Land.buildRandomLand()</c> (<c>Land.as:163-682</c>) —
    /// every per-<c>conf</c> branch, the cell fill, the placement pass, and the two
    /// pre-pass forced probs — into a pure <see cref="LandLayoutPlan"/>.
    ///
    /// <para><b>What this does NOT do.</b> It does not pick room templates (that is
    /// <c>RoomGenerator</c>), it does not carve doors (that is <c>DoorMatchMath</c> +
    /// <c>WorldBuilder</c>), and it does not place Unity objects. Those consume the plan.</para>
    ///
    /// <para><b>Conf coverage:</b> 0,1,2,3,4,5,6,7,10,11. Confs 8 and 9 do not exist in the oracle —
    /// a land carrying one falls through to the plain random fill (<c>Land.as:387-390</c>).</para>
    ///
    /// <para><b>RNG.</b> AS3 consumes one <c>Math.random()</c> per cell for the mirror flag and one per
    /// placement roll. The plan draws in that same order from the injected stream, but the port's RNG is
    /// seeded and the oracle's is not, so the <em>values</em> are not expected to match AS3 — only the
    /// <em>structure</em> is.</para>
    /// </summary>
    public static class LandLayoutPlanner
    {
        /// <summary>Convenience overload that reads a <see cref="LandDefinition"/>.</summary>
        public static LandLayoutPlan Plan(LandDefinition land, int landStage, bool visited,
            IRngService rng, bool mbaseVisited = false)
        {
            if (land == null) throw new ArgumentNullException(nameof(land));

            return Plan(new LandLayoutRequest
            {
                LandId = land.landId,
                Conf = land.configId,
                GridWidth = land.gridWidth,
                GridHeight = land.gridHeight,
                LandStage = landStage,
                EntryCell = land.entryCoordinates,
                Visited = visited,
                MbaseVisited = mbaseVisited,
            }, rng);
        }

        public static LandLayoutPlan Plan(LandLayoutRequest req, IRngService rng)
        {
            if (rng == null) throw new ArgumentNullException(nameof(rng));

            int conf = req.Conf;
            int mx = Mathf.Max(1, req.GridWidth);
            int my = Mathf.Max(1, req.GridHeight);

            // Land.as:180-183 — conf 0 shortens the grid to 3 rows on the first run only.
            if (conf == 0 && req.LandStage <= 0) my = 3;

            var plan = new LandLayoutPlan
            {
                LandId = req.LandId ?? string.Empty,
                Conf = conf,
                LandStage = req.LandStage,
                GridSize = new Vector2Int(mx, my),
                BoundsMin = Vector2Int.zero,
                BoundsMax = new Vector2Int(mx, my),
                UniqueRoomPerCell = conf == 4, // Land.as:938-941 — newRandomLoc zeroes kol for conf 4
            };

            Vector2Int beg = req.EntryCell;

            // ---- Pass 1: cell fill (Land.as:184-410) ----
            for (int x = 0; x < mx; x++)
            {
                for (int y = 0; y < my; y++)
                {
                    var c = new LandCellPlan
                    {
                        Position = new Vector2Int(x, y),
                        Mirror = rng.Chance(0.5f), // Land.as:192
                        Water = -1,
                        PetOn = true,
                        XpBonuses = true,
                        SelectionStage = req.LandStage,
                        SuppressPlacement = false,
                    };

                    if (conf == 2)
                    {
                        if (y == 1) c.Water = 17;
                        if (y > 1) c.Water = 0;
                    }
                    else if (conf == 5)
                    {
                        if (y == 2) c.Water = 21;
                        if (y > 2) c.Water = 0;
                    }

                    FillCell(ref c, req, beg, x, y, mx, my);

                    // Land.newLoc (Land.as:958-975): the per-cell difficulty offset is a function of the
                    // cell's Y index, and only confs 0, 1 and 2 add anything.
                    c.DifficultyOffset = DifficultyOffsetFor(conf, y);

                    plan.Cells.Add(c);
                }
            }

            // ---- Pre-pass forced probs (Land.as:495-502) ----
            if (conf == 2) plan.ForcedProbs.Add(new ForcedProbPlan { Position = new Vector2Int(3, 0), MinStage = 0 });
            if (conf == 3) plan.ForcedProbs.Add(new ForcedProbPlan { Position = new Vector2Int(1, 4), MinStage = 1 });

            // ---- Pass 4: exits / checkpoints / probs (Land.as:503-680) ----
            // AS3 walks the rows from the bottom (maxLocY-1) upward; the order matters only for the RNG
            // draws, and it is preserved here.
            for (int y = my - 1; y >= 0; y--)
            {
                for (int x = 0; x < mx; x++)
                {
                    int idx = IndexOf(plan.Cells, x, y);
                    if (idx < 0) continue;
                    LandCellPlan c = plan.Cells[idx];

                    bool xpBonuses = true; // AS3 _loc10_

                    ApplyPlacementPass(ref c, plan, req, beg, x, y, mx, my, conf, rng, ref xpBonuses);

                    // `Location.createXpBonuses` returns immediately when EITHER `homeStable` OR `homeAtk`
                    // is set (Location.as:2145-2148). `home` is the only setter of `homeStable`
                    // (Location.as:323-326) and `atk` the only setter of `homeAtk` (Location.as:327-330);
                    // in `Land.buildRandomLand` those come from conf 10 (`Land.as:358`) and conf 11
                    // (`Land.as:376`) respectively. Both must suppress, not just `home`.
                    c.XpBonuses = xpBonuses && !c.Home && !c.Atk;
                    plan.Cells[idx] = c;
                }
            }

            return plan;
        }

        /// <summary>
        /// AS3 <c>Land.newLoc</c> (<c>Land.as:958-975</c>): the <c>yOffset</c> handed to <c>setLocDif</c>.
        /// Only confs 0 (<c>y/2</c>), 1 (<c>y</c>) and 2 (<c>y*2.5</c>) add anything; every other conf
        /// relies on <c>landDifLevel</c> alone.
        /// </summary>
        public static float DifficultyOffsetFor(int conf, int y)
        {
            switch (conf)
            {
                case 0: return y / 2f;
                case 1: return y;
                case 2: return y * 2.5f;
                default: return 0f;
            }
        }

        private static void FillCell(ref LandCellPlan c, LandLayoutRequest req, Vector2Int beg,
            int x, int y, int mx, int my)
        {
            int conf = req.Conf;
            int stage = req.LandStage;

            if (conf == 0 && y == 0 && !req.Visited)
            {
                // Land.as:220-227 — beg0..beg3 by column, never mirrored.
                c.Mirror = false;
                c.Fill = CellFill.Tip;
                c.Tip = "beg" + x;
                return;
            }

            if ((conf == 2 || conf == 1 || conf == 5) && y == 0 && x == 0 && !req.Visited)
            {
                // Land.as:228-238
                c.Mirror = false;
                if (conf == 5)
                {
                    c.Ramka = 3;
                    c.Backform = 3;
                    c.TranspFon = true;
                }
                c.Fill = CellFill.Tip;
                c.Tip = "beg0";
                return;
            }

            if (conf == 3)
            {
                // Land.as:239-269 — structural shaft at x == 2.
                c.TranspFon = true;
                if (x == 2)
                {
                    if (y == 0)
                    {
                        c.Ramka = 7;
                        c.Fill = CellFill.Tip;
                        c.Tip = "passroof";
                    }
                    else
                    {
                        c.Ramka = 5;
                        c.Fill = CellFill.Random;
                        c.SelectionStage = stage;
                        c.Tip = "pass";
                    }
                    c.Bezdna = true;
                }
                else if (y == 0)
                {
                    c.Ramka = 6;
                    c.Fill = CellFill.Random;
                    c.SelectionStage = stage;
                    c.Tip = "roof";
                }
                else
                {
                    c.Fill = CellFill.Random;
                }
                return;
            }

            if (conf == 4)
            {
                // Land.as:270-308 — literal stage 0 for every cell.
                //
                // NOTE: conf 4 does NOT set `home`. Only conf 10 does (`Land.as:358`), and `home` is the
                // only thing that sets `Location.homeStable`, which is the *sole* early-return guard in
                // `Location.createXpBonuses` (`Location.as:2145-2148`). Setting it here suppressed the
                // conf-4 xp bonuses on every cell, not just the entry — the fixture caught it.
                if (x == 0 && y == 0)
                {
                    if (!req.MbaseVisited)
                    {
                        c.Mirror = false;
                        c.Ramka = 8;
                        c.Fill = CellFill.Tip;
                        c.Tip = "beg0";
                    }
                    else
                    {
                        c.Fill = CellFill.Random;
                        c.SelectionStage = 0;
                    }
                }
                else if (x == 0 && y > 0)
                {
                    c.Fill = CellFill.Random;
                    c.SelectionStage = 0;
                    c.Tip = "vert";
                }
                else if (x == mx - 1 && y == my - 1)
                {
                    c.Mirror = false;
                    c.Fill = CellFill.Tip;
                    c.Tip = "end";
                }
                else if (x == mx - 1 && y < my - 1)
                {
                    c.Fill = CellFill.Random;
                    c.SelectionStage = 0;
                    c.Tip = "vert";
                }
                else
                {
                    c.Fill = CellFill.Random;
                    c.SelectionStage = 0;
                }
                return;
            }

            if (conf == 5)
            {
                // Land.as:340-355
                if (y == 0)
                {
                    c.Ramka = 3;
                    c.Backform = 3;
                    c.TranspFon = true;
                    c.Fill = CellFill.Random;
                    c.SelectionStage = stage;
                    c.Tip = "surf";
                    c.VisMult = 2;
                }
                else
                {
                    c.Fill = CellFill.Random;
                }
                c.Gas = true;
                return;
            }

            if (conf == 7)
            {
                // Land.as:309-339 — literal stage 1; beg1/end1, not beg0/end.
                if (x == 0 && y == 0)
                {
                    c.Mirror = false;
                    c.Fill = CellFill.Tip;
                    c.Tip = "beg1";
                }
                else if (x == 0 && y > 0)
                {
                    c.Fill = CellFill.Random;
                    c.SelectionStage = 1;
                    c.Tip = "vert";
                }
                else if (x == mx - 1 && y == my - 1)
                {
                    c.Mirror = false;
                    c.Fill = CellFill.Tip;
                    c.Tip = "end1";
                }
                else if (x == mx - 1 && y < my - 1)
                {
                    c.Fill = CellFill.Random;
                    c.SelectionStage = 1;
                    c.Tip = "vert";
                }
                else
                {
                    c.Fill = CellFill.Random;
                    c.SelectionStage = 1;
                }
                return;
            }

            if (conf == 10)
            {
                // Land.as:356-373 — literal stage 10; every cell is a `home` cell.
                c.Home = true;
                if (x == beg.x && y == beg.y)
                {
                    c.Mirror = false;
                    c.Fill = CellFill.Tip;
                    c.Tip = "beg0";
                }
                else if (x == 1 && y == 1)
                {
                    c.Mirror = false;
                    c.Fill = CellFill.Tip;
                    c.Tip = "roof";
                }
                else
                {
                    c.Fill = CellFill.Random;
                    c.SelectionStage = 10;
                }
                return;
            }

            if (conf == 11)
            {
                // Land.as:374-386 — every cell is an `atk` cell.
                c.Atk = true;
                if (x == 5 && y == 0)
                {
                    c.Mirror = false;
                    c.Fill = CellFill.Tip;
                    c.Tip = "pass";
                }
                else
                {
                    c.Fill = CellFill.Random;
                    c.SelectionStage = stage;
                }
                return;
            }

            // Land.as:387-390 — the plain fallback (also confs 6, 8, 9).
            c.Fill = CellFill.Random;
            c.SelectionStage = stage;
        }

        private static void ApplyPlacementPass(ref LandCellPlan c, LandLayoutPlan plan,
            LandLayoutRequest req, Vector2Int beg, int x, int y, int mx, int my, int conf,
            IRngService rng, ref bool xpBonuses)
        {
            int stage = req.LandStage;
            bool isBeg = x == beg.x && y == beg.y;
            bool parityEven = ((x + y) % 2) == 0;

            if (conf == 0 || conf == 1)
            {
                // Land.as:511-539
                if (parityEven)
                {
                    if (y == my - 1)
                    {
                        bool deep = (conf == 0 && stage >= 2) || (conf == 1 && stage >= 1);
                        c.Exit = deep ? ExitKind.Deep : ExitKind.Shallow;
                    }
                    else
                    {
                        c.Checkpoint = isBeg ? CheckpointKind.Begin : CheckpointKind.Normal;
                    }
                }
                else if (y == my - 1)
                {
                    c.Prob = ProbKind.Forced;
                }
                else if (rng.Chance(0.3f))
                {
                    c.Prob = ProbKind.Chance;
                }
                return;
            }

            if (conf == 2 || conf == 5)
            {
                // Land.as:540-578 (shared branch)
                // createClouds(y) is a purely visual scatter and is not modelled in the plan.
                if (conf == 2 && x == mx - 1)
                {
                    c.Exit = ExitKind.Shallow;
                }

                if (conf == 5 && x == mx - 1 && y == 0)
                {
                    c.Exit = stage >= 1 ? ExitKind.Deep : ExitKind.Shallow;
                }
                else if (parityEven)
                {
                    bool cond = (conf == 2 && y < 2 && x < mx - 1) || (conf == 5 && (x == 0 || y > 0));
                    if (cond)
                    {
                        c.Checkpoint = isBeg ? CheckpointKind.Begin : CheckpointKind.Normal;
                    }
                }

                if (conf == 2 && y > 1) c.PetOn = false;

                bool probWindow = (y > 1 && x < mx - 1) || (conf == 2 && x < mx - 1 && rng.Chance(0.25f));
                if (probWindow && !parityEven)
                {
                    c.Prob = ProbKind.Chance;
                }
                return;
            }

            if (conf == 3 && x != 2)
            {
                // Land.as:580-608
                if (parityEven)
                {
                    if (y == 0) c.Exit = stage >= 1 ? ExitKind.Deep : ExitKind.Shallow;
                    else c.Checkpoint = isBeg ? CheckpointKind.Begin : CheckpointKind.Normal;
                }
                else if (y == 0)
                {
                    c.Prob = ProbKind.Forced;
                }
                else if (y != 4 && rng.Chance(0.25f))
                {
                    c.Prob = ProbKind.Chance;
                }
            }
            else if (conf == 3)
            {
                c.SuppressPlacement = true;
            }

            if (conf == 4)
            {
                // Land.as:609-619 — no exits, no probs.
                if (isBeg || x == 3)
                {
                    c.Checkpoint = isBeg ? CheckpointKind.Begin : CheckpointKind.Normal;
                    if (isBeg) xpBonuses = false;
                }
            }

            if (conf == 7)
            {
                // Land.as:620-627 — the only checkpoint is the entry.
                if (isBeg)
                {
                    c.Checkpoint = CheckpointKind.Begin;
                    xpBonuses = false;
                }
            }

            if (conf == 6)
            {
                // Land.as:628-657 — two exits on the top row, a boss door between them.
                plan.TranspFon = true;
                if (y == 0)
                {
                    if (x == 0 || x == 2) c.Exit = stage >= 1 ? ExitKind.Deep : ExitKind.Shallow;
                    if (x == 1) c.Prob = ProbKind.Forced;
                }
                else if (isBeg || x == (7 - y) % 3)
                {
                    c.Checkpoint = isBeg ? CheckpointKind.Begin : CheckpointKind.Normal;
                }
                else if (rng.Chance(0.25f))
                {
                    c.Prob = ProbKind.Chance;
                }
            }

            if (conf == 10 || conf == 11)
            {
                // Land.as:658-664 (shared branch)
                if (isBeg) c.Checkpoint = CheckpointKind.Begin;
            }
        }

        private static int IndexOf(List<LandCellPlan> cells, int x, int y)
        {
            for (int i = 0; i < cells.Count; i++)
            {
                if (cells[i].Position.x == x && cells[i].Position.y == y) return i;
            }
            return -1;
        }
    }
}
