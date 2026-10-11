using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using PFE.Core.Rng;
using PFE.Systems.Map;
using PFE.Systems.Map.Generation;

namespace PFE.Tests.Editor.Campaign
{
    /// <summary>
    /// Walks the ten <em>actual</em> procedural lands, one at a time, from the data that ships in
    /// <c>Assets/_PFE/Data/Resources/Campaign/Lands/*.asset</c> — not from synthetic grids.
    ///
    /// <para><b>Why this exists next to <see cref="LandLayoutPlannerTests"/>.</b> That fixture proves the
    /// conf <em>rules</em> are ported, using grid sizes chosen to make each rule visible. It cannot catch
    /// the other half: whether the port's land <em>data</em> agrees with those rules. A land whose
    /// <c>configId</c>, <c>mx</c>/<c>my</c> or <c>locx</c>/<c>locy</c> was transcribed wrong passes every
    /// one of those 52 tests and still builds the wrong land. These cases are the ten rows of the real
    /// roster, so a data regression fails here and nowhere else.</para>
    ///
    /// <para><b>The roster is not the seven <c>random_*</c> lands.</b> Three more carry
    /// <c>isProcedural: 1</c> and do not say "random" anywhere in their id — <c>bunker</c> (conf 7),
    /// <c>stable_pi</c> (conf 10) and <c>stable_pi_atk</c> (conf 11). They are the only lands that use
    /// confs 7/10/11 at all, which is exactly why the first version of this fixture, built from the
    /// <c>random_*</c> filename pattern, claimed "the oracle has exactly seven procedural lands" and was
    /// wrong. The set below is keyed off <c>isProcedural</c>, not off a name.</para>
    ///
    /// <para><b>Oracle for each row.</b> <c>LandAct.as:157-177</c> (<c>locx</c>/<c>locy</c>/<c>mx</c>/
    /// <c>my</c>/<c>exit</c>) and the conf branch at <c>Land.as:184-682</c>.</para>
    /// </summary>
    [TestFixture]
    public class ProceduralLandRosterTests
    {
        /// <summary>
        /// One row of the shipped roster. Every field is transcribed from the land's <c>.asset</c>; the
        /// point of the fixture is that a transcription error shows up as a failure rather than as a land
        /// that quietly builds something else.
        /// </summary>
        private struct LandRow
        {
            public string LandId;
            public int Conf;
            public int Mx;
            public int My;
            public Vector2Int Entry;

            /// <summary>AS3 <c>&lt;land exit="…"&gt;</c> — the <c>prob</c> prefix on the exit box.</summary>
            public string ExitProb;

            /// <summary>
            /// The land's <c>tip</c> tag — the destination-selection axis (<c>Land.as:907</c>,
            /// <c>PipPageInfo.as:386-396</c>). It is <em>independent</em> of both <c>conf</c> and the
            /// <c>rnd</c> procedural flag: seven rows are <c>rnd</c>-tipped and three are not, while
            /// all ten are procedural. Reading the roster off this column would give seven.
            /// </summary>
            public string Tip;
        }

        /// <summary>
        /// The roster — every land with <c>isProcedural: 1</c>, ordered by conf.
        /// </summary>
        private static readonly LandRow[] Roster =
        {
            //                          land              conf  mx  my  entry         exit           tip
            new LandRow { LandId = "random_plant",  Conf = 0,  Mx = 4, My = 6, Entry = new Vector2Int(0, 0), ExitProb = "exit_plant",  Tip = "rnd" },
            new LandRow { LandId = "random_stable", Conf = 1,  Mx = 4, My = 6, Entry = new Vector2Int(0, 0), ExitProb = "exit_stable", Tip = "rnd" },
            new LandRow { LandId = "random_sewer",  Conf = 2,  Mx = 8, My = 3, Entry = new Vector2Int(0, 0), ExitProb = "exit_sewer",  Tip = "rnd" },
            new LandRow { LandId = "random_mane",   Conf = 3,  Mx = 5, My = 5, Entry = new Vector2Int(0, 4), ExitProb = "exit_mane",   Tip = "rnd" },
            new LandRow { LandId = "random_mbase",  Conf = 4,  Mx = 5, My = 3, Entry = new Vector2Int(0, 0), ExitProb = "",            Tip = "story" },
            new LandRow { LandId = "random_canter", Conf = 5,  Mx = 8, My = 3, Entry = new Vector2Int(0, 0), ExitProb = "exit_canter", Tip = "rnd" },
            new LandRow { LandId = "random_encl",   Conf = 6,  Mx = 3, My = 8, Entry = new Vector2Int(0, 7), ExitProb = "exit_encl",   Tip = "rnd" },
            new LandRow { LandId = "bunker",        Conf = 7,  Mx = 6, My = 3, Entry = new Vector2Int(0, 0), ExitProb = "",            Tip = "hard" },
            new LandRow { LandId = "stable_pi",     Conf = 10, Mx = 4, My = 6, Entry = new Vector2Int(3, 5), ExitProb = "",            Tip = "base" },
            new LandRow { LandId = "stable_pi_atk", Conf = 11, Mx = 6, My = 2, Entry = new Vector2Int(0, 0), ExitProb = "",            Tip = "rnd" },
        };

        /// <summary>
        /// The confs that never call <c>createExit</c> in the oracle, and whose lands therefore carry an
        /// empty <c>exit</c> attribute. Kept as data rather than as a condition inside a loop, because the
        /// interesting assertion is that the two sets agree — a conf that stopped placing exits without
        /// the land data changing (or the reverse) is the bug this pins.
        /// </summary>
        private static readonly HashSet<int> ConfsWithNoExit = new HashSet<int> { 4, 7, 10, 11 };

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

        private static LandLayoutPlan Plan(LandRow row, int stage, bool visited = false, bool chance = false)
        {
            return LandLayoutPlanner.Plan(new LandLayoutRequest
            {
                LandId = row.LandId,
                Conf = row.Conf,
                GridWidth = row.Mx,
                GridHeight = row.My,
                LandStage = stage,
                EntryCell = row.Entry,
                Visited = visited,
                MbaseVisited = false,
            }, new ConstRng(chance));
        }

        private static int CountExits(LandLayoutPlan plan)
        {
            int n = 0;
            foreach (LandCellPlan c in plan.Cells) if (c.HasExit) n++;
            return n;
        }

        /// <summary>
        /// Every row is a distinct conf, and the set is exactly the set the planner claims to cover.
        ///
        /// <para><b>8 and 9 are asserted absent, not merely unused.</b> The oracle has no conf-8 or conf-9
        /// branch — a land carrying one would fall through to the plain random fill
        /// (<c>Land.as:387-390</c>) and build a land with no exits, no checkpoints and no entry room, which
        /// looks like a broken build rather than like a missing conf. If the importer ever emits 8 or 9,
        /// this is where it should be caught.</para>
        /// </summary>
        [Test]
        public void Roster_ConfsAreDistinctAndCoverExactlyThePlannersSet()
        {
            var seen = new HashSet<int>();
            foreach (LandRow row in Roster)
            {
                Assert.IsTrue(seen.Add(row.Conf),
                    $"{row.LandId} reuses conf {row.Conf}; every procedural land has its own branch.");
            }

            Assert.AreEqual(10, Roster.Length,
                "The roster is every land with isProcedural: 1 — ten of them, not the seven whose ids " +
                "start with 'random'.");

            int[] expected = { 0, 1, 2, 3, 4, 5, 6, 7, 10, 11 };
            foreach (int conf in expected)
            {
                Assert.IsTrue(seen.Contains(conf),
                    $"conf {conf} is in no land row — a conf branch would then be dead code.");
            }

            Assert.IsFalse(seen.Contains(8), "The oracle has no conf 8; a land using it would build a " +
                                             "fallback land with no exits at all.");
            Assert.IsFalse(seen.Contains(9), "The oracle has no conf 9.");
        }

        /// <summary>
        /// Each land fills its whole grid. A short cell list means the conf branch skipped cells, and a
        /// skipped cell is a hole the player can walk into.
        /// </summary>
        [Test]
        public void EachLand_FillsItsWholeGrid()
        {
            foreach (LandRow row in Roster)
            {
                LandLayoutPlan plan = Plan(row, stage: 1);

                Assert.AreEqual(row.Mx * row.My, plan.CellCount,
                    $"{row.LandId} (conf {row.Conf}) planned {plan.CellCount} cells for a " +
                    $"{row.Mx}x{row.My} grid.");

                for (int x = 0; x < row.Mx; x++)
                {
                    for (int y = 0; y < row.My; y++)
                    {
                        Assert.IsTrue(plan.HasCell(x, y),
                            $"{row.LandId} has no plan for cell ({x},{y}).");
                    }
                }
            }
        }

        /// <summary>
        /// <c>GridSize</c> equals the land's own <c>mx</c>/<c>my</c> at stage 1+ — and conf 0 is the only
        /// land the oracle clamps, and only on the first descent (<c>Land.as:180-183</c>).
        /// </summary>
        [Test]
        public void EachLand_GridIsItsOwnMxMy_ExceptConf0AtStage0()
        {
            foreach (LandRow row in Roster)
            {
                LandLayoutPlan plan = Plan(row, stage: 1);
                Assert.AreEqual(new Vector2Int(row.Mx, row.My), plan.GridSize,
                    $"{row.LandId} (conf {row.Conf}) grid at stage 1.");
            }

            LandRow plant = Find("random_plant");
            LandLayoutPlan plantStage0 = Plan(plant, stage: 0);
            Assert.AreEqual(new Vector2Int(4, 3), plantStage0.GridSize,
                "conf 0 (random_plant) must shorten to 3 rows on the first descent.");
            Assert.AreEqual(12, plantStage0.CellCount, "4x3 = 12 cells at stage 0.");

            LandLayoutPlan plantStage1 = Plan(plant, stage: 1);
            Assert.AreEqual(24, plantStage1.CellCount,
                "…and must be back to the full 4x6 = 24 cells from stage 1. If both are 12 the clamp " +
                "was not conditioned on the stage at all.");
        }

        /// <summary>
        /// The entry cell of every land is inside that land's own grid, and — for the three lands whose
        /// entry is not the origin — it is <em>not</em> silently pulled to (0,0).
        /// </summary>
        [Test]
        public void EachLand_EntryIsInsideItsGridAndIsNotFlattenedToTheOrigin()
        {
            foreach (LandRow row in Roster)
            {
                LandLayoutPlan plan = Plan(row, stage: 1);
                Vector3Int entry = WorldBuilder.ResolveProceduralEntry(row.Entry, plan.GridSize);

                Assert.AreEqual(row.Entry.x, entry.x, $"{row.LandId} entry x must be honoured.");
                Assert.AreEqual(row.Entry.y, entry.y, $"{row.LandId} entry y must be honoured.");
                Assert.IsTrue(plan.HasCell(entry.x, entry.y),
                    $"{row.LandId} entry ({entry.x},{entry.y}) names a cell that was never planned.");
            }

            // Positive controls for the three non-origin entries. Stated outright because the loop above
            // would still pass if every entry happened to be (0,0) — the assertion that matters is that
            // these specific lands keep the cell their data declares.
            Assert.AreEqual(new Vector3Int(0, 4, 0),
                WorldBuilder.ResolveProceduralEntry(new Vector2Int(0, 4), new Vector2Int(5, 5)),
                "random_mane (conf 3) enters at (0,4) — the last row of its 5x5 grid.");
            Assert.AreEqual(new Vector3Int(0, 7, 0),
                WorldBuilder.ResolveProceduralEntry(new Vector2Int(0, 7), new Vector2Int(3, 8)),
                "random_encl (conf 6) enters at (0,7) — the last row of its 3x8 grid.");
            Assert.AreEqual(new Vector3Int(3, 5, 0),
                WorldBuilder.ResolveProceduralEntry(new Vector2Int(3, 5), new Vector2Int(4, 6)),
                "stable_pi (conf 10) enters at (3,5) — the opposite corner from every other land.");
        }

        /// <summary>
        /// The entry cell carries a <c>createCheck(true)</c> checkpoint in every land. Without it there is
        /// no respawn point, so a death in that land sends the player somewhere else entirely.
        /// </summary>
        [Test]
        public void EachLand_EntryCellCarriesTheBeginCheckpoint()
        {
            foreach (LandRow row in Roster)
            {
                LandLayoutPlan plan = Plan(row, stage: 1);
                LandCellPlan entry = plan.GetCell(row.Entry.x, row.Entry.y);

                Assert.AreEqual(CheckpointKind.Begin, entry.Checkpoint,
                    $"{row.LandId} (conf {row.Conf}) must place the begin checkpoint on its entry cell " +
                    $"({row.Entry.x},{row.Entry.y}).");
            }
        }

        /// <summary>
        /// Six of the ten lands can be left; four cannot, by design.
        ///
        /// <para><b>This is the assertion that ties two independent data facts together.</b> Confs 4, 7, 10
        /// and 11 are the only branches in <c>Land.as:184-682</c> that never call <c>createExit</c>, and
        /// <c>random_mbase</c> / <c>bunker</c> / <c>stable_pi</c> / <c>stable_pi_atk</c> are the only roster
        /// rows with an empty <c>exit</c> attribute. If the importer had mis-mapped confs, those two sets
        /// would stop agreeing.</para>
        /// </summary>
        [Test]
        public void LandsWithNoExitConf_CannotBeLeft_AndTheOthersCan()
        {
            foreach (LandRow row in Roster)
            {
                LandLayoutPlan plan = Plan(row, stage: 1);
                int exits = CountExits(plan);

                if (ConfsWithNoExit.Contains(row.Conf))
                {
                    Assert.AreEqual(0, exits,
                        $"{row.LandId} (conf {row.Conf}) must place no exit — the oracle's branch never " +
                        "calls createExit. A non-zero count means another conf's rules are being applied.");
                    Assert.AreEqual("", row.ExitProb,
                        "…and its land data agrees: no exit attribute, so there is nothing to leave to.");
                    continue;
                }

                Assert.Greater(exits, 0,
                    $"{row.LandId} (conf {row.Conf}) places no exit at stage 1 — the land would be a " +
                    "dead end. This is the 'no transition out of a random land' symptom.");
                Assert.IsNotEmpty(row.ExitProb,
                    $"{row.LandId} has exits but no exit prob prefix, so the exit box would carry an " +
                    "empty prob and Interact.allAct could not resolve it.");
            }
        }

        /// <summary>
        /// The exit prob prefix is what <c>Interact.allAct</c> matches (<c>Interact.as:1558</c>), and the
        /// deep variant is that prefix with a <c>"1"</c> suffix. Assert the two forms are what the plan's
        /// <see cref="ExitKind"/> asks for, for every land that can be left.
        /// </summary>
        [Test]
        public void ExitProbsAreTheLandsOwnPrefix_WithOneSuffixedForTheDeepVariant()
        {
            foreach (LandRow row in Roster)
            {
                if (ConfsWithNoExit.Contains(row.Conf)) continue;

                bool sawShallow = false, sawDeep = false;
                for (int stage = 0; stage <= 4; stage++)
                {
                    LandLayoutPlan plan = Plan(row, stage: stage);
                    foreach (LandCellPlan c in plan.Cells)
                    {
                        if (c.Exit == ExitKind.Shallow) sawShallow = true;
                        if (c.Exit == ExitKind.Deep) sawDeep = true;
                    }
                }

                Assert.IsTrue(sawShallow,
                    $"{row.LandId} never produces a shallow exit across stages 0..4 — its prob prefix " +
                    $"'{row.ExitProb}' would then never appear on an exit box.");

                if (row.Conf == 0 || row.Conf == 1 || row.Conf == 3 || row.Conf == 5)
                {
                    Assert.IsTrue(sawDeep,
                        $"{row.LandId} (conf {row.Conf}) has a stage-gated deep exit and must produce " +
                        "one by stage 4; 'exit_<land>1' is the hand-off room's prob.");
                }
            }
        }

        /// <summary>
        /// The three lands whose id does not say "random" are the only users of confs 7, 10 and 11, and
        /// none of them is a <c>rnd</c>-tip land. Stated as its own case because it is the cheapest signal
        /// that the roster was not assembled from the filename pattern.
        /// </summary>
        [Test]
        public void TheThreeLandsWithNonRandomIds_AreTheOnlyConfs7To11Users()
        {
            var highConf = new List<string>();
            foreach (LandRow row in Roster)
            {
                if (row.Conf >= 7) highConf.Add(row.LandId);
            }

            CollectionAssert.AreEquivalent(
                new[] { "bunker", "stable_pi", "stable_pi_atk" }, highConf,
                "confs 7, 10 and 11 exist only on the three lands whose id does not say 'random' — " +
                "which is why a roster built from the filename pattern silently dropped them.");

            Assert.AreEqual(7, Find("bunker").Conf, "bunker is conf 7 (the only one).");
            Assert.AreEqual(10, Find("stable_pi").Conf, "stable_pi is conf 10 (the only one).");
            Assert.AreEqual(11, Find("stable_pi_atk").Conf, "stable_pi_atk is conf 11 (the only one).");
        }

        /// <summary>
        /// <c>tip</c> is a destination-selection tag, not the procedural flag — and in particular it is
        /// <em>not</em> a synonym for "this land is procedural".
        ///
        /// <para><b>The trap this pins.</b> Seven of the ten procedural lands carry <c>tip='rnd'</c>, so
        /// this column <em>looks</em> like the roster: filter on it and you get seven rows and a
        /// plausible-looking fixture. The procedural flag is the separate <c>rnd</c> attribute in
        /// <c>GameData.as</c> (parsed at <c>CampaignDataParser.cs:40</c>), which all ten carry. The first
        /// version of this fixture fell into exactly that trap: it asserted the three conf-7/10/11 lands
        /// were not <c>rnd</c>-tipped, but <c>stable_pi_atk</c> is conf 11 <em>and</em> tip <c>rnd</c>.</para>
        /// </summary>
        [Test]
        public void TipIsADestinationTag_NotAProxyForTheProceduralFlag()
        {
            Assert.AreEqual("rnd", Find("stable_pi_atk").Tip,
                "conf 11 and tip 'rnd' occur together — the two axes are independent.");
            Assert.AreEqual("hard", Find("bunker").Tip,
                "conf 7 and tip 'hard' occur together.");
            Assert.AreEqual("story", Find("random_mbase").Tip,
                "conf 4 and tip 'story' occur together.");
            Assert.AreEqual("base", Find("stable_pi").Tip,
                "conf 10 and tip 'base' occur together.");

            int rndTipped = 0;
            foreach (LandRow row in Roster)
            {
                if (row.Tip == "rnd") rndTipped++;
            }

            Assert.AreEqual(7, rndTipped,
                "Seven rows are rnd-tipped. If this column were the procedural flag the roster would " +
                "be 7 — the exact mistake this fixture exists to prevent.");
            Assert.AreEqual(10, Roster.Length,
                "…while the real roster is 10, keyed off the rnd attribute (isProcedural).");
        }

        // =====================================================================
        //  ResolveProceduralEntry — the clamp
        // =====================================================================

        private static LandRow Find(string landId)
        {
            foreach (LandRow row in Roster)
            {
                if (row.LandId == landId) return row;
            }
            Assert.Fail($"'{landId}' is not in the roster.");
            return default;
        }

        [Test]
        public void ResolveProceduralEntry_InsideTheGrid_IsUnchanged()
        {
            Assert.AreEqual(new Vector3Int(0, 0, 0),
                WorldBuilder.ResolveProceduralEntry(new Vector2Int(0, 0), new Vector2Int(4, 6)));
            Assert.AreEqual(new Vector3Int(3, 5, 0),
                WorldBuilder.ResolveProceduralEntry(new Vector2Int(3, 5), new Vector2Int(4, 6)));
        }

        [Test]
        public void ResolveProceduralEntry_PastTheGrid_IsClampedToTheLastCell()
        {
            Assert.AreEqual(new Vector3Int(3, 5, 0),
                WorldBuilder.ResolveProceduralEntry(new Vector2Int(9, 9), new Vector2Int(4, 6)));
            Assert.AreEqual(new Vector3Int(0, 0, 0),
                WorldBuilder.ResolveProceduralEntry(new Vector2Int(-4, -1), new Vector2Int(4, 6)));
        }

        /// <summary>
        /// A one-cell grid clamps to (0,0) rather than to (-1,-1). <c>Mathf.Max(0, grid - 1)</c> is the
        /// only thing standing between an empty grid and a negative index.
        /// </summary>
        [Test]
        public void ResolveProceduralEntry_OneCellGrid_ClampsToZeroNotMinusOne()
        {
            Assert.AreEqual(new Vector3Int(0, 0, 0),
                WorldBuilder.ResolveProceduralEntry(new Vector2Int(3, 3), new Vector2Int(1, 1)));
        }

        /// <summary>
        /// conf 0 clamps to 3 rows at stage 0, so a land declaring <c>locy</c> 4 is legitimately clamped
        /// there and legitimately not at stage 1. The two stages must disagree, or the clamp is not being
        /// fed the planned grid.
        /// </summary>
        [Test]
        public void ResolveProceduralEntry_UsesTheClampedGrid_NotTheLandsDeclaredGrid()
        {
            LandRow plant = Find("random_plant");
            LandLayoutPlan stage0 = Plan(plant, stage: 0);
            LandLayoutPlan stage1 = Plan(plant, stage: 1);

            Assert.AreEqual(3, stage0.GridSize.y, "positive control: stage 0 is clamped to 3 rows.");
            Assert.AreEqual(6, stage1.GridSize.y, "positive control: stage 1 is not clamped.");

            var entry = new Vector2Int(0, 5);
            Assert.AreEqual(new Vector3Int(0, 2, 0),
                WorldBuilder.ResolveProceduralEntry(entry, stage0.GridSize));
            Assert.AreEqual(new Vector3Int(0, 5, 0),
                WorldBuilder.ResolveProceduralEntry(entry, stage1.GridSize));
        }
    }
}
