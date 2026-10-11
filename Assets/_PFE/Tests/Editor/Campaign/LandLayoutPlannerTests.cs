using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using PFE.Core.Rng;
using PFE.Systems.Map.Generation;

namespace PFE.Tests.Editor.Campaign
{
    /// <summary>
    /// Asserts the per-conf layout rules of <see cref="LandLayoutPlanner"/> against the checks written in
    /// <c>docs/LandGameplayLoop/conf/CONF_*.md</c>, which were transcribed from AS3
    /// <c>fe.loc.Land.buildRandomLand()</c> (<c>Land.as:163-682</c>).
    ///
    /// <para>Every fixture below carries a positive control (the thing that must be there) and, where the
    /// assertion is "nothing happened", a negative control that would have caught a silent no-op.</para>
    /// </summary>
    [TestFixture]
    public class LandLayoutPlannerTests
    {
        /// <summary>
        /// A deterministic <see cref="IRngService"/> whose <c>Chance</c> always answers the same way, so a
        /// layout assertion does not depend on the RNG stream position.
        /// </summary>
        private sealed class ConstRng : IRngService
        {
            private readonly bool _chance;
            public ConstRng(bool chanceResult) { _chance = chanceResult; }

            public bool Chance(float probability) => _chance;
            public float NextFloat() => _chance ? 0.0f : 0.9f;
            public uint NextUInt() => _chance ? 0u : 1u;
            public int NextInt(int maxExclusive) => 0;
            public int Range(int minInclusive, int maxExclusive) => minInclusive;
            public float Range(float min, float max) => min;
            public void Shuffle<T>(IList<T> list) { }
            public IRngService GetStream(RngStream stream, int? salt = null) => this;
        }

        private static LandLayoutPlan Plan(int conf, int mx, int my, int stage, Vector2Int entry,
            bool chance = false, bool visited = false, bool mbaseVisited = false)
        {
            return LandLayoutPlanner.Plan(new LandLayoutRequest
            {
                LandId = "test_land",
                Conf = conf,
                GridWidth = mx,
                GridHeight = my,
                LandStage = stage,
                EntryCell = entry,
                Visited = visited,
                MbaseVisited = mbaseVisited,
            }, new ConstRng(chance));
        }

        private static int CountExits(LandLayoutPlan plan)
        {
            int n = 0;
            foreach (LandCellPlan c in plan.Cells) if (c.HasExit) n++;
            return n;
        }

        private static int CountCheckpoints(LandLayoutPlan plan)
        {
            int n = 0;
            foreach (LandCellPlan c in plan.Cells) if (c.Checkpoint != CheckpointKind.None) n++;
            return n;
        }

        private static int CountProbs(LandLayoutPlan plan)
        {
            int n = 0;
            foreach (LandCellPlan c in plan.Cells) if (c.Prob != ProbKind.None) n++;
            return n;
        }

        // =====================================================================
        //  conf 0 — random_plant
        // =====================================================================

        [Test]
        public void Conf0_Stage0_ClampsGridToThreeRows()
        {
            LandLayoutPlan plan = Plan(0, 4, 6, stage: 0, entry: new Vector2Int(0, 0));

            Assert.That(plan.GridSize, Is.EqualTo(new Vector2Int(4, 3)),
                "Land.as:180-183 clamps conf 0 to my = 3 while landStage <= 0.");
            Assert.That(plan.CellCount, Is.EqualTo(12), "4 x 3 cells.");
            Assert.That(plan.BoundsMax, Is.EqualTo(new Vector2Int(4, 3)));

            // Positive control on the clamp's *exit*: the bottom row is 2, not 5.
            Assert.That(plan.GetCell(0, 2).Exit, Is.Not.EqualTo(ExitKind.None),
                "With my clamped to 3 the exit row is y == 2.");
            Assert.That(plan.HasCell(0, 3), Is.False, "Row 3 must not exist at stage 0.");
        }

        [Test]
        public void Conf0_Stage1_KeepsTheFullGrid()
        {
            LandLayoutPlan plan = Plan(0, 4, 6, stage: 1, entry: new Vector2Int(0, 0));
            Assert.That(plan.GridSize, Is.EqualTo(new Vector2Int(4, 6)),
                "The clamp is conf 0 AND landStage <= 0 only.");
            Assert.That(plan.CellCount, Is.EqualTo(24));
        }

        [Test]
        public void Conf0_EntryRowIsBegByColumnAndNeverMirrored()
        {
            LandLayoutPlan plan = Plan(0, 4, 6, stage: 1, entry: new Vector2Int(0, 0), chance: true);

            for (int x = 0; x < 4; x++)
            {
                LandCellPlan c = plan.GetCell(x, 0);
                Assert.That(c.Fill, Is.EqualTo(CellFill.Tip));
                Assert.That(c.Tip, Is.EqualTo("beg" + x), "Land.as:220-227 fills beg0..beg3 by column.");
                Assert.That(c.Mirror, Is.False, "A beg* cell is never mirrored (Land.as:226).");
            }

            // Negative control: the row below is NOT a beg row, and with Chance always true it IS mirrored.
            Assert.That(plan.GetCell(0, 1).Tip, Is.Not.EqualTo("beg0"));
            Assert.That(plan.GetCell(0, 1).Mirror, Is.True,
                "Every non-beg cell draws mirror = random < 0.5 (Land.as:192).");
        }

        [Test]
        public void Conf0_NoBegRoomWhenVisited()
        {
            LandLayoutPlan plan = Plan(0, 4, 6, stage: 1, entry: new Vector2Int(0, 0), visited: true);

            foreach (LandCellPlan c in plan.Cells)
            {
                Assert.That(c.Tip, Is.Null.Or.Not.StartsWith("beg"),
                    "The beg* row is only used on a first visit (!act.visited).");
            }
            Assert.That(plan.GetCell(0, 0).Fill, Is.EqualTo(CellFill.Random));
        }

        [Test]
        public void Conf0_ExitsOnBottomRowOnly_CheckpointsBetween()
        {
            LandLayoutPlan plan = Plan(0, 4, 6, stage: 1, entry: new Vector2Int(0, 0));

            int my = plan.GridSize.y;
            for (int x = 0; x < plan.GridSize.x; x++)
            {
                LandCellPlan bottom = plan.GetCell(x, my - 1);
                if ((x + (my - 1)) % 2 == 0)
                {
                    Assert.That(bottom.Exit, Is.EqualTo(ExitKind.Shallow),
                        "At landStage 1 conf 0 still uses the shallow exit (deep starts at 2).");
                }
                else
                {
                    Assert.That(bottom.Prob, Is.EqualTo(ProbKind.Forced),
                        "An odd-parity bottom cell takes a forced prob (Land.as:531-538).");
                }
            }

            // Negative control: no exit appears on row 0.
            for (int x = 0; x < plan.GridSize.x; x++)
            {
                Assert.That(plan.GetCell(x, 0).Exit, Is.EqualTo(ExitKind.None),
                    "Row 0 must never carry an exit.");
            }
        }

        [Test]
        public void Conf0_DeepExitStartsAtStage2()
        {
            LandLayoutPlan shallow = Plan(0, 4, 6, stage: 1, entry: new Vector2Int(0, 0));
            LandLayoutPlan deep = Plan(0, 4, 6, stage: 2, entry: new Vector2Int(0, 0));

            // The exit row is parity-gated: with my == 6 the bottom row is y == 5, so the exit lands on the
            // odd columns (x + 5 even ⇒ x odd). x == 0 on that row is a *forced prob*, not an exit
            // (Land.as:511-539). Asserting on (0, my-1) is what made this fixture look like an
            // implementation bug — the implementation was right and the probe was wrong.
            const int exitX = 1;

            Assert.That(shallow.GetCell(exitX, shallow.GridSize.y - 1).Exit, Is.EqualTo(ExitKind.Shallow),
                "At landStage 1 conf 0 still uses the shallow exit.");
            Assert.That(deep.GetCell(exitX, deep.GridSize.y - 1).Exit, Is.EqualTo(ExitKind.Deep),
                "Land.as:517 — conf 0 flips to createExit(\"1\") at landStage >= 2.");

            // Positive control on the probe itself: its parity partner on the same row is a forced prob,
            // so exitX is a place an exit can be and x == 0 is a place it cannot.
            Assert.That(shallow.GetCell(0, shallow.GridSize.y - 1).Exit, Is.EqualTo(ExitKind.None));
            Assert.That(shallow.GetCell(0, shallow.GridSize.y - 1).Prob, Is.EqualTo(ProbKind.Forced));
        }

        [Test]
        public void Conf0_EntryCellCarriesTheBeginCheckpoint()
        {
            LandLayoutPlan plan = Plan(0, 4, 6, stage: 1, entry: new Vector2Int(0, 0));
            Assert.That(plan.GetCell(0, 0).Checkpoint, Is.EqualTo(CheckpointKind.Begin),
                "createCheck(x == begLocX && y == begLocY) at the entry cell.");
            Assert.That(CountCheckpoints(plan), Is.GreaterThan(1),
                "Other even-parity non-bottom cells carry a plain checkpoint.");
        }

        [Test]
        public void Conf0_ChanceProbsOnlyOnOddParityNonBottomCells()
        {
            LandLayoutPlan plan = Plan(0, 4, 6, stage: 1, entry: new Vector2Int(0, 0), chance: true);

            int my = plan.GridSize.y;
            for (int x = 0; x < plan.GridSize.x; x++)
            {
                for (int y = 0; y < my; y++)
                {
                    LandCellPlan c = plan.GetCell(x, y);
                    bool oddParity = ((x + y) % 2) == 1;
                    if (oddParity && y < my - 1)
                    {
                        Assert.That(c.Prob, Is.EqualTo(ProbKind.Chance), $"({x},{y})");
                    }
                    else if (!oddParity)
                    {
                        Assert.That(c.Prob, Is.EqualTo(ProbKind.None), $"({x},{y}) is even parity.");
                    }
                }
            }
        }

        [Test]
        public void Conf0_DifficultyOffsetIsHalfTheRowIndex()
        {
            LandLayoutPlan plan = Plan(0, 4, 6, stage: 1, entry: new Vector2Int(0, 0));
            Assert.That(plan.GetCell(0, 0).DifficultyOffset, Is.EqualTo(0f));
            Assert.That(plan.GetCell(0, 4).DifficultyOffset, Is.EqualTo(2f), "Land.as:964 — y / 2.");
        }

        // =====================================================================
        //  conf 1 — random_stable
        // =====================================================================

        [Test]
        public void Conf1_NoGridClampAndSingleBegCell()
        {
            LandLayoutPlan plan = Plan(1, 4, 6, stage: 0, entry: new Vector2Int(0, 0));

            Assert.That(plan.GridSize, Is.EqualTo(new Vector2Int(4, 6)),
                "The my = 3 clamp is conf 0 only (Land.as:180).");
            Assert.That(plan.GetCell(0, 0).Tip, Is.EqualTo("beg0"));
            Assert.That(plan.GetCell(0, 0).Mirror, Is.False);

            for (int x = 0; x < 4; x++)
            {
                for (int y = 0; y < 6; y++)
                {
                    if (x == 0 && y == 0) continue;
                    Assert.That(plan.GetCell(x, y).Tip, Is.Null.Or.Not.StartsWith("beg"),
                        $"conf 1 has exactly one beg* cell; ({x},{y}) is not it.");
                }
            }
        }

        [Test]
        public void Conf1_DeepExitStartsAtStage1()
        {
            LandLayoutPlan stage0 = Plan(1, 4, 6, stage: 0, entry: new Vector2Int(0, 0));
            LandLayoutPlan stage1 = Plan(1, 4, 6, stage: 1, entry: new Vector2Int(0, 0));

            // Same parity gate as conf 0 (they share one arm, Land.as:511-539): y == 5 wants an odd x.
            const int exitX = 1;

            Assert.That(stage0.GetCell(exitX, 5).Exit, Is.EqualTo(ExitKind.Shallow),
                "conf 1 at landStage 0 still uses the shallow exit.");
            Assert.That(stage1.GetCell(exitX, 5).Exit, Is.EqualTo(ExitKind.Deep),
                "Land.as:517 — conf 1 is two runs, not three.");

            // Positive control: the off-parity cell on the same row is not an exit at either stage.
            Assert.That(stage1.GetCell(0, 5).Exit, Is.EqualTo(ExitKind.None));
            Assert.That(stage1.GetCell(0, 5).Prob, Is.EqualTo(ProbKind.Forced));
        }

        [Test]
        public void Conf1_DifficultyOffsetIsTheRowIndex()
        {
            LandLayoutPlan plan = Plan(1, 4, 6, stage: 0, entry: new Vector2Int(0, 0));
            Assert.That(plan.GetCell(0, 5).DifficultyOffset, Is.EqualTo(5f), "Land.as:966 — y.");
        }

        // =====================================================================
        //  conf 2 — random_sewer
        // =====================================================================

        [Test]
        public void Conf2_ExitIsTheFarRightColumnNotTheBottomRow()
        {
            LandLayoutPlan plan = Plan(2, 8, 3, stage: 0, entry: new Vector2Int(0, 0));

            Assert.That(plan.GridSize, Is.EqualTo(new Vector2Int(8, 3)));
            for (int y = 0; y < 3; y++)
            {
                Assert.That(plan.GetCell(7, y).Exit, Is.EqualTo(ExitKind.Shallow),
                    $"The exit is on x == maxLocX - 1 for every row (Land.as:546). y={y}");
            }

            // Negative control: no exit on the bottom row's other cells.
            for (int x = 0; x < 7; x++)
            {
                Assert.That(plan.GetCell(x, 2).Exit, Is.EqualTo(ExitKind.None), $"({x},2)");
            }
        }

        [Test]
        public void Conf2_ExitIsNeverDeep()
        {
            foreach (int stage in new[] { 0, 1, 5 })
            {
                LandLayoutPlan plan = Plan(2, 8, 3, stage, new Vector2Int(0, 0));
                foreach (LandCellPlan c in plan.Cells)
                {
                    Assert.That(c.Exit, Is.Not.EqualTo(ExitKind.Deep),
                        $"conf 2 never calls createExit(\"1\") — stage {stage}.");
                }
            }
        }

        [Test]
        public void Conf2_ForcedProbIsAtCellThreeZero()
        {
            LandLayoutPlan plan = Plan(2, 8, 3, stage: 0, entry: new Vector2Int(0, 0));
            Assert.That(plan.ForcedProbs.Count, Is.EqualTo(1));
            Assert.That(plan.ForcedProbs[0].Position, Is.EqualTo(new Vector2Int(3, 0)),
                "Land.as:495-498 forces a prob at locs[3][0][0].");
            Assert.That(plan.ForcedProbs[0].MinStage, Is.EqualTo(0));
        }

        [Test]
        public void Conf2_WaterAndPetOnFollowTheRowRules()
        {
            LandLayoutPlan plan = Plan(2, 8, 3, stage: 0, entry: new Vector2Int(0, 0));

            Assert.That(plan.GetCell(0, 0).Water, Is.EqualTo(-1), "y == 0 leaves water unset.");
            Assert.That(plan.GetCell(0, 1).Water, Is.EqualTo(17), "Land.as:199-202 — y == 1.");
            Assert.That(plan.GetCell(0, 2).Water, Is.EqualTo(0), "Land.as:203-206 — y > 1.");

            Assert.That(plan.GetCell(0, 1).PetOn, Is.True);
            Assert.That(plan.GetCell(0, 2).PetOn, Is.False, "Land.as:568-571 — petOn = false below row 1.");
        }

        [Test]
        public void Conf2_DifficultyOffsetIsRowTimesTwoPointFive()
        {
            LandLayoutPlan plan = Plan(2, 8, 3, stage: 0, entry: new Vector2Int(0, 0));
            Assert.That(plan.GetCell(0, 2).DifficultyOffset, Is.EqualTo(5f), "Land.as:970-973 — y * 2.5.");
        }

        // =====================================================================
        //  conf 3 — random_mane
        // =====================================================================

        [Test]
        public void Conf3_MiddleColumnIsAShaftAndTakesNoPlacement()
        {
            LandLayoutPlan plan = Plan(3, 5, 5, stage: 0, entry: new Vector2Int(0, 4));

            Assert.That(plan.GetCell(2, 0).Tip, Is.EqualTo("passroof"), "Land.as:246-248.");
            Assert.That(plan.GetCell(2, 0).Bezdna, Is.True);
            for (int y = 1; y < 5; y++)
            {
                Assert.That(plan.GetCell(2, y).Tip, Is.EqualTo("pass"), $"({2},{y}) is a pass room.");
                Assert.That(plan.GetCell(2, y).Bezdna, Is.True);
            }

            // Land.as:580 guards the whole placement pass with x != 2.
            for (int y = 0; y < 5; y++)
            {
                LandCellPlan c = plan.GetCell(2, y);
                Assert.That(c.SuppressPlacement, Is.True, $"({2},{y})");
                Assert.That(c.Exit, Is.EqualTo(ExitKind.None));
                Assert.That(c.Checkpoint, Is.EqualTo(CheckpointKind.None));
                Assert.That(c.Prob, Is.EqualTo(ProbKind.None));
            }
        }

        [Test]
        public void Conf3_ExitIsOnTheTopRow_AnAscent()
        {
            LandLayoutPlan plan = Plan(3, 5, 5, stage: 0, entry: new Vector2Int(0, 4));

            int exits = CountExits(plan);
            Assert.That(exits, Is.GreaterThan(0), "The player climbs from y = 4 to the exit row y = 0.");
            foreach (LandCellPlan c in plan.Cells)
            {
                if (c.HasExit)
                {
                    Assert.That(c.Position.y, Is.EqualTo(0), "The exit row is the TOP row (Land.as:584).");
                    Assert.That(c.Position.x, Is.Not.EqualTo(2), "Column 2 is excluded (Land.as:580).");
                }
            }

            // Negative control: the bottom row (the entry row) has no exit.
            for (int x = 0; x < 5; x++)
            {
                Assert.That(plan.GetCell(x, 4).Exit, Is.EqualTo(ExitKind.None), $"({x},4)");
            }
        }

        [Test]
        public void Conf3_EntryCellCarriesTheBeginCheckpoint()
        {
            LandLayoutPlan plan = Plan(3, 5, 5, stage: 0, entry: new Vector2Int(0, 4));
            Assert.That(plan.GetCell(0, 4).Checkpoint, Is.EqualTo(CheckpointKind.Begin));
        }

        [Test]
        public void Conf3_ForcedProbIsStageGated()
        {
            LandLayoutPlan plan = Plan(3, 5, 5, stage: 0, entry: new Vector2Int(0, 4));
            Assert.That(plan.ForcedProbs.Count, Is.EqualTo(1));
            Assert.That(plan.ForcedProbs[0].Position, Is.EqualTo(new Vector2Int(1, 4)),
                "Land.as:499-502 forces a prob at locs[1][4][0].");
            Assert.That(plan.ForcedProbs[0].MinStage, Is.EqualTo(1),
                "…but only from landStage >= 1.");
        }

        [Test]
        public void Conf3_DifficultyOffsetIsZeroEverywhere()
        {
            LandLayoutPlan plan = Plan(3, 5, 5, stage: 0, entry: new Vector2Int(0, 4));
            foreach (LandCellPlan c in plan.Cells)
            {
                Assert.That(c.DifficultyOffset, Is.EqualTo(0f), "conf 3 is absent from the offset list.");
            }
        }

        [Test]
        public void Conf3_RoofRowIsTheTopRowAndTranspFonIsOn()
        {
            LandLayoutPlan plan = Plan(3, 5, 5, stage: 0, entry: new Vector2Int(0, 4));
            for (int x = 0; x < 5; x++)
            {
                if (x == 2) continue;
                Assert.That(plan.GetCell(x, 0).Tip, Is.EqualTo("roof"), $"({x},0)");
            }
            Assert.That(plan.GetCell(0, 2).TranspFon, Is.True, "Land.as:240 sets transpFon on every conf-3 cell.");
        }

        // =====================================================================
        //  conf 4 — random_mbase
        // =====================================================================

        [Test]
        public void Conf4_NoExitsNoProbsAndIsNotAHomeLand()
        {
            LandLayoutPlan plan = Plan(4, 5, 3, stage: 3, entry: new Vector2Int(0, 0));

            Assert.That(CountExits(plan), Is.EqualTo(0), "conf 4 never calls createExit (Land.as:609-619).");
            Assert.That(CountProbs(plan), Is.EqualTo(0), "conf 4 never calls newRandomProb.");
            Assert.That(plan.ForcedProbs.Count, Is.EqualTo(0));
            Assert.That(plan.UniqueRoomPerCell, Is.True, "conf 4 zeroes kol, so no template repeats.");

            // `home` is set in exactly one place in the whole oracle — the conf-10 branch
            // (Land.as:358). It was wrongly applied here, which suppressed the xp bonuses on every
            // conf-4 cell; the `homeStable`/`homeAtk` guard in `Location.createXpBonuses`
            // (Location.as:2145-2148) is what made that a behavioural bug rather than a cosmetic one.
            foreach (LandCellPlan c in plan.Cells)
            {
                Assert.That(c.Home, Is.False, $"({c.Position}) — conf 4 is not a home land.");
                Assert.That(c.Atk, Is.False, $"({c.Position}) — conf 4 is not an atk land.");
            }
        }

        [Test]
        public void Conf4_BegEndAndVertColumns()
        {
            LandLayoutPlan plan = Plan(4, 5, 3, stage: 3, entry: new Vector2Int(0, 0));

            Assert.That(plan.GetCell(0, 0).Tip, Is.EqualTo("beg0"), "Land.as:276-282.");
            Assert.That(plan.GetCell(0, 0).Ramka, Is.EqualTo(8));
            Assert.That(plan.GetCell(0, 0).Mirror, Is.False);
            Assert.That(plan.GetCell(4, 2).Tip, Is.EqualTo("end"), "Land.as:288-292.");
            Assert.That(plan.GetCell(0, 1).Tip, Is.EqualTo("vert"));
            Assert.That(plan.GetCell(4, 0).Tip, Is.EqualTo("vert"));
        }

        [Test]
        public void Conf4_BegRoomIsSkippedOnASecondVisit()
        {
            LandLayoutPlan first = Plan(4, 5, 3, stage: 3, entry: new Vector2Int(0, 0), mbaseVisited: false);
            LandLayoutPlan second = Plan(4, 5, 3, stage: 3, entry: new Vector2Int(0, 0), mbaseVisited: true);

            Assert.That(first.GetCell(0, 0).Tip, Is.EqualTo("beg0"));
            Assert.That(second.GetCell(0, 0).Tip, Is.Null.Or.Not.EqualTo("beg0"),
                "Land.as:276 — the beg0 room is only placed while triggers[\"mbase_visited\"] <= 0.");
            Assert.That(second.GetCell(0, 0).Fill, Is.EqualTo(CellFill.Random));
        }

        [Test]
        public void Conf4_RoomSelectionStageIsTheLiteralZero()
        {
            LandLayoutPlan plan = Plan(4, 5, 3, stage: 9, entry: new Vector2Int(0, 0));
            Assert.That(plan.GetCell(1, 1).SelectionStage, Is.EqualTo(0),
                "Land.as:296-308 passes the literal 0, not act.landStage.");
        }

        [Test]
        public void Conf4_CheckpointsAtEntryAndColumnThree()
        {
            LandLayoutPlan plan = Plan(4, 5, 3, stage: 3, entry: new Vector2Int(0, 0));

            Assert.That(plan.GetCell(0, 0).Checkpoint, Is.EqualTo(CheckpointKind.Begin));
            for (int y = 0; y < 3; y++)
            {
                Assert.That(plan.GetCell(3, y).Checkpoint, Is.EqualTo(CheckpointKind.Normal), $"(3,{y})");
            }
        }

        [Test]
        public void Conf4_XpBonusesSuppressedAtTheEntryCellOnly()
        {
            LandLayoutPlan plan = Plan(4, 5, 3, stage: 3, entry: new Vector2Int(0, 0));

            Assert.That(plan.GetCell(0, 0).XpBonuses, Is.False, "AS3 _loc10_ = false at the conf-4 entry.");

            // Regression guard: conf 4 must not set `home`. `home` is the only setter of
            // `Location.homeStable`, the sole early-return guard in `Location.createXpBonuses`
            // (`Location.as:2145-2148`), and only conf 10 sets it (`Land.as:358`). Sweeping the whole grid
            // is what distinguishes "the entry is suppressed" from "every cell is suppressed" — the
            // single-cell assertion below passed while the whole grid was wrong.
            foreach (LandCellPlan c in plan.Cells)
            {
                if (c.Position == new Vector2Int(0, 0)) continue;
                Assert.That(c.XpBonuses, Is.True, $"({c.Position}) must keep its xp bonuses.");
                Assert.That(c.Home, Is.False, $"({c.Position}) — conf 4 is not a home land.");
            }
        }

        // =====================================================================
        //  conf 5 — random_canter
        // =====================================================================

        [Test]
        public void Conf5_TopRowIsSurfTipWithVisibilityTwoAndGasEverywhereElse()
        {
            LandLayoutPlan plan = Plan(5, 8, 3, stage: 0, entry: new Vector2Int(0, 0));

            for (int x = 0; x < 8; x++)
            {
                LandCellPlan c = plan.GetCell(x, 0);
                Assert.That(c.Ramka, Is.EqualTo(3));
                Assert.That(c.Backform, Is.EqualTo(3));
                Assert.That(c.TranspFon, Is.True);
                if (x == 0)
                {
                    // The entry cell leaves the fill chain at the `(conf 2|1|5) && y == 0 && x == 0 &&
                    // !visited` arm (Land.as:229-238), which sets ramka/backform/transpFon but never
                    // reaches the conf-5 arm's `_loc1_.gas = 1` (Land.as:354). It is the one gas-less cell.
                    Assert.That(c.Tip, Is.EqualTo("beg0"), "The entry cell is beg0, not surf.");
                    Assert.That(c.Gas, Is.False, "Land.as:354 — the beg0 arm returns before `gas = 1`.");
                    Assert.That(c.VisMult, Is.Not.EqualTo(2), "visMult = 2 belongs to the surf arm.");
                }
                else
                {
                    Assert.That(c.Tip, Is.EqualTo("surf"), $"({x},0)");
                    Assert.That(c.VisMult, Is.EqualTo(2), $"({x},0) visMult = 2 (Land.as:348).");
                    Assert.That(c.Gas, Is.True, $"({x},0) gas = 1 (Land.as:354).");
                }
            }

            // Every non-top-row cell takes the plain conf-5 arm, which always ends in `gas = 1`.
            foreach (LandCellPlan c in plan.Cells)
            {
                if (c.Position.y != 0) Assert.That(c.Gas, Is.True, $"({c.Position}) gas = 1 (Land.as:354).");
            }
        }

        [Test]
        public void Conf5_VisitedEntryCellFallsThroughToTheSurfArm()
        {
            // Positive control for the fixture above: with `visited` the beg0 arm does not match, so (0,0)
            // takes the ordinary conf-5 y == 0 arm and does get the surf tip, visMult 2 and the gas flag.
            // That proves (0,0) is gas-less only because of the *entry* arm, not because of its column.
            LandLayoutPlan plan = Plan(5, 8, 3, stage: 0, entry: new Vector2Int(0, 0), visited: true);

            LandCellPlan c = plan.GetCell(0, 0);
            Assert.That(c.Tip, Is.EqualTo("surf"));
            Assert.That(c.VisMult, Is.EqualTo(2));
            Assert.That(c.Gas, Is.True);
        }

        [Test]
        public void Conf5_ExitIsTopRightNotBottomRight()
        {
            LandLayoutPlan plan = Plan(5, 8, 3, stage: 0, entry: new Vector2Int(0, 0));

            Assert.That(plan.GetCell(7, 0).Exit, Is.EqualTo(ExitKind.Shallow),
                "Land.as:550-560 — the exit is (maxLocX-1, 0).");
            Assert.That(plan.GetCell(7, 2).Exit, Is.EqualTo(ExitKind.None),
                "…and NOT the bottom-right cell.");
        }

        [Test]
        public void Conf5_DeepExitStartsAtStage1()
        {
            LandLayoutPlan plan = Plan(5, 8, 3, stage: 1, entry: new Vector2Int(0, 0));
            Assert.That(plan.GetCell(7, 0).Exit, Is.EqualTo(ExitKind.Deep), "Land.as:552.");
        }

        [Test]
        public void Conf5_TheExitCellIsNotAlsoGivenACheckpoint()
        {
            LandLayoutPlan plan = Plan(5, 8, 3, stage: 0, entry: new Vector2Int(0, 0));
            Assert.That(plan.GetCell(7, 0).Checkpoint, Is.EqualTo(CheckpointKind.None),
                "Land.as:550-567 — the exit `if` is followed by an `else if` for the checkpoint.");
        }

        [Test]
        public void Conf5_WaterFollowsTheRowRule()
        {
            LandLayoutPlan plan = Plan(5, 8, 3, stage: 0, entry: new Vector2Int(0, 0));
            Assert.That(plan.GetCell(0, 2).Water, Is.EqualTo(21), "Land.as:208-211 — y == 2.");
            Assert.That(plan.GetCell(0, 0).Water, Is.EqualTo(-1), "y < 2 leaves water unset.");
        }

        // =====================================================================
        //  conf 6 — random_encl
        // =====================================================================

        [Test]
        public void Conf6_HasNoBegCell()
        {
            LandLayoutPlan plan = Plan(6, 3, 8, stage: 0, entry: new Vector2Int(0, 7));
            foreach (LandCellPlan c in plan.Cells)
            {
                Assert.That(c.Tip, Is.Null.Or.Not.StartsWith("beg"),
                    $"conf 6 is absent from the entry branch (Land.as:228); ({c.Position})");
            }
        }

        [Test]
        public void Conf6_TwoExitsOnTheTopRowWithTheBossDoorBetween()
        {
            LandLayoutPlan plan = Plan(6, 3, 8, stage: 0, entry: new Vector2Int(0, 7));

            Assert.That(plan.GetCell(0, 0).Exit, Is.Not.EqualTo(ExitKind.None), "(0,0)");
            Assert.That(plan.GetCell(2, 0).Exit, Is.Not.EqualTo(ExitKind.None), "(2,0)");
            Assert.That(plan.GetCell(1, 0).Prob, Is.EqualTo(ProbKind.Forced),
                "Land.as:635-638 — the middle top cell is the boss door.");
            Assert.That(CountExits(plan), Is.EqualTo(2));
        }

        [Test]
        public void Conf6_CheckpointDiagonal()
        {
            LandLayoutPlan plan = Plan(6, 3, 8, stage: 0, entry: new Vector2Int(0, 7));

            for (int y = 1; y < 8; y++)
            {
                int diagX = (7 - y) % 3;
                Assert.That(plan.GetCell(diagX, y).Checkpoint, Is.Not.EqualTo(CheckpointKind.None),
                    $"Land.as:640 — the diagonal x == (7 - y) % 3 at y = {y}.");
            }
        }

        [Test]
        public void Conf6_LandIsTransparentBacked()
        {
            LandLayoutPlan plan = Plan(6, 3, 8, stage: 0, entry: new Vector2Int(0, 7));
            Assert.That(plan.TranspFon, Is.True, "Land.as:630 sets transpFon on the land.");
        }

        // =====================================================================
        //  conf 7 — bunker
        // =====================================================================

        [Test]
        public void Conf7_UsesBeg1AndEnd1()
        {
            LandLayoutPlan plan = Plan(7, 6, 3, stage: 0, entry: new Vector2Int(0, 0));

            Assert.That(plan.GetCell(0, 0).Tip, Is.EqualTo("beg1"),
                "bunker's entry is beg1, not beg0 (Land.as:311-315).");
            Assert.That(plan.GetCell(5, 2).Tip, Is.EqualTo("end1"),
                "…and its goal is end1 (Land.as:321-325).");
            Assert.That(plan.GetCell(0, 1).Tip, Is.EqualTo("vert"));
            Assert.That(plan.GetCell(5, 0).Tip, Is.EqualTo("vert"));
        }

        [Test]
        public void Conf7_OnlyCheckpointIsTheEntry()
        {
            LandLayoutPlan plan = Plan(7, 6, 3, stage: 0, entry: new Vector2Int(0, 0));

            Assert.That(CountCheckpoints(plan), Is.EqualTo(1), "Land.as:620-627.");
            Assert.That(plan.GetCell(0, 0).Checkpoint, Is.EqualTo(CheckpointKind.Begin));
            Assert.That(CountExits(plan), Is.EqualTo(0));
            Assert.That(CountProbs(plan), Is.EqualTo(0));
        }

        [Test]
        public void Conf7_RoomSelectionStageIsTheLiteralOne()
        {
            LandLayoutPlan plan = Plan(7, 6, 3, stage: 8, entry: new Vector2Int(0, 0));
            Assert.That(plan.GetCell(1, 1).SelectionStage, Is.EqualTo(1),
                "Land.as:320-337 passes the literal 1, not act.landStage.");
        }

        // =====================================================================
        //  conf 10 — stable_pi
        // =====================================================================

        [Test]
        public void Conf10_EntryIsBottomRightAndRoofLandmarkIsAtOneOne()
        {
            LandLayoutPlan plan = Plan(10, 4, 6, stage: 0, entry: new Vector2Int(3, 5));

            Assert.That(plan.GetCell(3, 5).Tip, Is.EqualTo("beg0"), "Land.as:359-363.");
            Assert.That(plan.GetCell(3, 5).Mirror, Is.False);
            Assert.That(plan.GetCell(1, 1).Tip, Is.EqualTo("roof"),
                "Land.as:364-368 — the fixed roof landmark.");
            Assert.That(plan.GetCell(1, 1).Mirror, Is.False);
        }

        [Test]
        public void Conf10_OnlyCheckpointIsTheEntryAndNoExitsOrProbs()
        {
            LandLayoutPlan plan = Plan(10, 4, 6, stage: 0, entry: new Vector2Int(3, 5));

            Assert.That(CountCheckpoints(plan), Is.EqualTo(1), "Land.as:658-664.");
            Assert.That(plan.GetCell(3, 5).Checkpoint, Is.EqualTo(CheckpointKind.Begin));
            Assert.That(CountExits(plan), Is.EqualTo(0));
            Assert.That(CountProbs(plan), Is.EqualTo(0));
        }

        [Test]
        public void Conf10_HomeCellsSuppressXpBonuses()
        {
            LandLayoutPlan plan = Plan(10, 4, 6, stage: 0, entry: new Vector2Int(3, 5));
            foreach (LandCellPlan c in plan.Cells)
            {
                Assert.That(c.Home, Is.True, $"({c.Position})");
                Assert.That(c.XpBonuses, Is.False,
                    "createXpBonuses returns immediately when homeStable is set (Location.as:2145-2148).");
            }
        }

        [Test]
        public void Conf10_RoomSelectionStageIsTheLiteralTen()
        {
            LandLayoutPlan plan = Plan(10, 4, 6, stage: 0, entry: new Vector2Int(3, 5));
            Assert.That(plan.GetCell(0, 0).SelectionStage, Is.EqualTo(10), "Land.as:371.");
        }

        // =====================================================================
        //  conf 11 — stable_pi_atk
        // =====================================================================

        [Test]
        public void Conf11_PassLandmarkAtFiveZeroAndAtkEverywhere()
        {
            LandLayoutPlan plan = Plan(11, 6, 2, stage: 0, entry: new Vector2Int(0, 0));

            Assert.That(plan.GetCell(5, 0).Tip, Is.EqualTo("pass"), "Land.as:376-380.");
            Assert.That(plan.GetCell(5, 0).Mirror, Is.False);
            foreach (LandCellPlan c in plan.Cells)
            {
                Assert.That(c.Atk, Is.True, $"({c.Position})");

                // `atk` sets `homeAtk`, which is the *other* early-return guard in
                // `Location.createXpBonuses` (Location.as:2145-2148). An atk land therefore carries no
                // xp bonuses either — checking only `home` missed this half of the guard.
                Assert.That(c.XpBonuses, Is.False, $"({c.Position}) — homeAtk returns before createXpBonuses.");
            }
        }

        [Test]
        public void Conf11_OnlyCheckpointIsTheEntryAndNoExitsOrProbs()
        {
            LandLayoutPlan plan = Plan(11, 6, 2, stage: 0, entry: new Vector2Int(0, 0));

            Assert.That(CountCheckpoints(plan), Is.EqualTo(1), "Land.as:658-664.");
            Assert.That(plan.GetCell(0, 0).Checkpoint, Is.EqualTo(CheckpointKind.Begin));
            Assert.That(CountExits(plan), Is.EqualTo(0));
            Assert.That(CountProbs(plan), Is.EqualTo(0));
        }

        [Test]
        public void Conf11_RoomSelectionUsesLandStage()
        {
            LandLayoutPlan plan = Plan(11, 6, 2, stage: 7, entry: new Vector2Int(0, 0));
            Assert.That(plan.GetCell(1, 0).SelectionStage, Is.EqualTo(7),
                "Land.as:384 passes act.landStage — unlike conf 4/7/10.");
        }

        // =====================================================================
        //  cross-conf invariants
        // =====================================================================

        [Test]
        public void EveryConf_FillsEveryCellInBounds()
        {
            (int conf, int mx, int my, int stage, int ex, int ey)[] cases =
            {
                (0, 4, 6, 1, 0, 0),
                (1, 4, 6, 0, 0, 0),
                (2, 8, 3, 0, 0, 0),
                (3, 5, 5, 0, 0, 4),
                (4, 5, 3, 0, 0, 0),
                (5, 8, 3, 0, 0, 0),
                (6, 3, 8, 0, 0, 7),
                (7, 6, 3, 0, 0, 0),
                (10, 4, 6, 0, 3, 5),
                (11, 6, 2, 0, 0, 0),
            };

            foreach (var c in cases)
            {
                LandLayoutPlan plan = Plan(c.conf, c.mx, c.my, c.stage, new Vector2Int(c.ex, c.ey));

                Assert.That(plan.CellCount, Is.EqualTo(c.mx * c.my), $"conf {c.conf} cell count.");
                for (int x = 0; x < c.mx; x++)
                {
                    for (int y = 0; y < c.my; y++)
                    {
                        LandCellPlan cell = plan.GetCell(x, y);
                        Assert.That(cell.Fill, Is.Not.EqualTo(CellFill.None),
                            $"conf {c.conf} cell ({x},{y}) was never filled — a missing branch.");
                    }
                }
            }
        }

        [Test]
        public void EveryConf_HasExactlyOneBeginCheckpoint()
        {
            (int conf, int mx, int my, int ex, int ey)[] cases =
            {
                (0, 4, 6, 0, 0),
                (1, 4, 6, 0, 0),
                (2, 8, 3, 0, 0),
                (3, 5, 5, 0, 4),
                (4, 5, 3, 0, 0),
                (5, 8, 3, 0, 0),
                (6, 3, 8, 0, 7),
                (7, 6, 3, 0, 0),
                (10, 4, 6, 3, 5),
                (11, 6, 2, 0, 0),
            };

            foreach (var c in cases)
            {
                LandLayoutPlan plan = Plan(c.conf, c.mx, c.my, 0, new Vector2Int(c.ex, c.ey));

                int begins = 0;
                foreach (LandCellPlan cell in plan.Cells)
                {
                    if (cell.Checkpoint == CheckpointKind.Begin) begins++;
                }

                Assert.That(begins, Is.EqualTo(1),
                    $"conf {c.conf} must place exactly one createCheck(true) — the entry checkpoint.");
            }
        }

        [Test]
        public void Planner_NullRequestRng_Throws()
        {
            Assert.Throws<System.ArgumentNullException>(() =>
                LandLayoutPlanner.Plan(new LandLayoutRequest { Conf = 0, GridWidth = 1, GridHeight = 1 }, null));
        }
    }
}
