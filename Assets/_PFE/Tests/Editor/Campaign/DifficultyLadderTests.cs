using System.Collections.Generic;
using NUnit.Framework;
using PFE.Systems.Campaign;

namespace PFE.Tests.Editor.Campaign
{
    /// <summary>
    /// <see cref="DifficultyLadder"/> — the projection the F2 difficulty panel prints.
    ///
    /// <para><b>Why these are worth asserting when the values come from <see cref="DifficultyRules"/>.</b>
    /// The rules are already pinned by <c>DifficultyRulesTests</c>; what is <i>not</i> pinned is the
    /// projection: that each index gets the right row, that the checkpoint's three-way answer is derived
    /// correctly, and that the printed text says the same number the facts carry. A panel whose text
    /// disagrees with its own facts is a debug tool that lies, and it lies in the one place a tester
    /// trusts without checking.</para>
    ///
    /// <para>Unity-free, so this runs through the offline wall as well as in the editor.</para>
    /// </summary>
    [TestFixture]
    public sealed class DifficultyLadderTests
    {
        private static IEnumerable<int> Levels()
        {
            for (int i = GlobalDifficulty.MinIndex; i <= GlobalDifficulty.MaxIndex; i++)
            {
                yield return i;
            }
        }

        // ── the enemy-HP term: two arms, not a range ────────────────────────────

        [Test]
        public void EnemyHp_IsTwoArmsAndNotARange()
        {
            // Unit.as:1130-1141. The oracle's outer guard is `<= 1` while its two arms are `== 0` / `== 1`,
            // so a `<= 1 → 0.4` collapse reads the same at level 0 and is wrong at level 1.
            Assert.That(DifficultyLadder.For(0).EnemyHpMultiplier, Is.EqualTo(0.4f));
            Assert.That(DifficultyLadder.For(1).EnemyHpMultiplier, Is.EqualTo(0.7f),
                "level 1 is its own arm (0.7), not the level-0 value");
            Assert.That(DifficultyLadder.For(2).EnemyHpMultiplier, Is.EqualTo(1f));
            Assert.That(DifficultyLadder.For(3).EnemyHpMultiplier, Is.EqualTo(1f));
            Assert.That(DifficultyLadder.For(4).EnemyHpMultiplier, Is.EqualTo(1f));
        }

        // ── the two build-time land adjustments ────────────────────────────────

        [Test]
        public void Hearing_HalvesBelowNormalOnly()
        {
            Assert.That(DifficultyLadder.For(0).EarMultiplier, Is.EqualTo(0.5f));
            Assert.That(DifficultyLadder.For(1).EarMultiplier, Is.EqualTo(0.5f));
            Assert.That(DifficultyLadder.For(2).EarMultiplier, Is.EqualTo(1f));
            Assert.That(DifficultyLadder.For(3).EarMultiplier, Is.EqualTo(1f));
            Assert.That(DifficultyLadder.For(4).EarMultiplier, Is.EqualTo(1f));
        }

        [Test]
        public void EnemyLevel_NormalIsTheIdentityAndHardStepsByTwo()
        {
            // Land.as:992-995 — the guard is `> 2`, and the step is 2 per level above Normal.
            Assert.That(DifficultyLadder.For(0).EnemyLevelBonus, Is.EqualTo(0));
            Assert.That(DifficultyLadder.For(1).EnemyLevelBonus, Is.EqualTo(0));
            Assert.That(DifficultyLadder.For(2).EnemyLevelBonus, Is.EqualTo(0), "Normal must not move the level");
            Assert.That(DifficultyLadder.For(3).EnemyLevelBonus, Is.EqualTo(2));
            Assert.That(DifficultyLadder.For(4).EnemyLevelBonus, Is.EqualTo(4));
        }

        // ── the checkpoint's three-way answer ──────────────────────────────────

        [Test]
        public void Checkpoint_DerivesAlwaysChanceNeverFromTheRule()
        {
            // `always` is the rule answering with no land and no roll; `chance` is the rule that says a
            // draw is reached at all. Deriving both is what stops level 2 from being reported as "never".
            Assert.That(DifficultyLadder.For(0).Checkpoint, Is.EqualTo(CheckpointTeleMode.Always));
            Assert.That(DifficultyLadder.For(1).Checkpoint, Is.EqualTo(CheckpointTeleMode.Always));
            Assert.That(DifficultyLadder.For(2).Checkpoint, Is.EqualTo(CheckpointTeleMode.Chance));
            Assert.That(DifficultyLadder.For(3).Checkpoint, Is.EqualTo(CheckpointTeleMode.Never));
            Assert.That(DifficultyLadder.For(4).Checkpoint, Is.EqualTo(CheckpointTeleMode.Never));
        }

        // ── the two boss terms ─────────────────────────────────────────────────

        [Test]
        public void BossTerms_MoveOnlyAboveNormal()
        {
            Assert.That(DifficultyLadder.For(0).BossAttackScale, Is.EqualTo(1f));
            Assert.That(DifficultyLadder.For(3).BossAttackScale, Is.EqualTo(1.2f));
            Assert.That(DifficultyLadder.For(4).BossAttackScale, Is.EqualTo(1.5f));

            // The necromancer's default is 0.5, not 1 — the summon arm is a weakening even at Normal.
            Assert.That(DifficultyLadder.For(0).SummonAttackScale, Is.EqualTo(0.5f));
            Assert.That(DifficultyLadder.For(2).SummonAttackScale, Is.EqualTo(0.5f));
            Assert.That(DifficultyLadder.For(3).SummonAttackScale, Is.EqualTo(0.65f));
            Assert.That(DifficultyLadder.For(4).SummonAttackScale, Is.EqualTo(0.8f));
        }

        // ── the loot sink ──────────────────────────────────────────────────────

        [Test]
        public void MoneyCap_HalvesOnlyAtSuperHard()
        {
            Assert.That(DifficultyLadder.For(0).Pers.DifCapsMult, Is.EqualTo(1f));
            Assert.That(DifficultyLadder.For(1).Pers.DifCapsMult, Is.EqualTo(1f));
            Assert.That(DifficultyLadder.For(2).Pers.DifCapsMult, Is.EqualTo(1f));
            Assert.That(DifficultyLadder.For(3).Pers.DifCapsMult, Is.EqualTo(1f));
            Assert.That(DifficultyLadder.For(4).Pers.DifCapsMult, Is.EqualTo(0.5f),
                "Super-hard is the only level that halves money and bits");
        }

        // ── index handling ─────────────────────────────────────────────────────

        [Test]
        public void For_ClampsInsteadOfThrowing()
        {
            Assert.That(DifficultyLadder.For(-3).Index, Is.EqualTo(GlobalDifficulty.MinIndex));
            Assert.That(DifficultyLadder.For(9).Index, Is.EqualTo(GlobalDifficulty.MaxIndex));
        }

        [Test]
        public void NameOf_ReadsTheMainMenuEnum()
        {
            // The console's `dif` reply and the new-game screen use the same enum; a private name table
            // here would be a third copy that can drift.
            Assert.That(DifficultyLadder.NameOf(0), Is.EqualTo("VeryEasy"));
            Assert.That(DifficultyLadder.NameOf(2), Is.EqualTo("Normal"));
            Assert.That(DifficultyLadder.NameOf(4), Is.EqualTo("SuperHard"));
        }

        // ── the printed text says what the facts say ───────────────────────────

        [Test]
        public void ChangeLines_PrintTheLevelsOwnNumbers()
        {
            // A copy-paste in the text is invisible in a screenshot and reads as a real finding, so the
            // text is checked against the facts rather than trusted.
            IReadOnlyList<string> easy = DifficultyLadder.ChangeLines(1);
            string enemyHpLine = Find(easy, "enemy max HP");

            StringAssert.Contains("0.7", enemyHpLine);
            StringAssert.DoesNotContain("0.4", enemyHpLine, "level 1 must not print level 0's multiplier");

            string hard = Find(DifficultyLadder.ChangeLines(3), "enemy level");
            StringAssert.Contains("+2", hard);
            StringAssert.Contains("never", Find(DifficultyLadder.ChangeLines(3), "checkpoint"));
            StringAssert.Contains("33%", Find(DifficultyLadder.ChangeLines(2), "checkpoint"));
        }

        [Test]
        public void DeltaLines_AreEmptyForNormalAndNameTheHeadlineChangeOtherwise()
        {
            Assert.That(DifficultyLadder.DeltaLines(GlobalDifficulty.Default), Is.Empty,
                "there is no delta against itself, and an empty list is the honest answer");

            StringAssert.Contains("0.5", Join(DifficultyLadder.DeltaLines(4)),
                "Super-hard's headline is the money halving");
            StringAssert.Contains("0.4", Join(DifficultyLadder.DeltaLines(0)),
                "Very easy's headline is the enemy-HP cut");
        }

        [Test]
        public void DeltaLines_NeverClaimAChangeThatIsNotThere()
        {
            // Level 1 differs from Normal in organ damage, fatal fraction, telekinesis upkeep, enemy HP and
            // hearing — and in nothing else. A delta list that mentions money or boss scaling here would be
            // reporting a change the rules do not make.
            string delta = Join(DifficultyLadder.DeltaLines(1));

            StringAssert.Contains("enemyHP", delta);
            StringAssert.DoesNotContain("money", delta);
            StringAssert.DoesNotContain("boss", delta);
            StringAssert.DoesNotContain("enemyLvl", delta);
        }

        [Test]
        public void SpecOnlyLines_SayThatNothingReadsThem()
        {
            // The `Pers` cells with no destination field must be labelled as such: a tester who changes
            // difficulty and sees `begHP` not move is looking at a recorded gap, not a regression.
            IReadOnlyList<string> lines = DifficultyLadder.SpecOnlyLines(0);

            Assert.That(lines.Count, Is.EqualTo(2));
            StringAssert.Contains("no destination field", lines[0]);
            StringAssert.Contains("200", lines[1], "VeryEasy's begHP");
        }

        // ── the silent-clipping guard ──────────────────────────────────────────

        [Test]
        public void EveryLine_FitsThePanelsWidthBudget()
        {
            var all = new List<string>();
            foreach (int level in Levels())
            {
                all.AddRange(DifficultyLadder.ChangeLines(level));
                all.AddRange(DifficultyLadder.DeltaLines(level));
                all.AddRange(DifficultyLadder.SpecOnlyLines(level));
            }

            // Positive control: an empty scan would satisfy every assertion below while proving nothing.
            Assert.That(all.Count, Is.GreaterThanOrEqualTo(39),
                "5 levels x (5 change + 2 spec) lines, plus at least one delta line per non-Normal level");

            foreach (string line in all)
            {
                Assert.That(line.Length, Is.LessThanOrEqualTo(DifficultyLadder.MaxLineLength),
                    $"a label does not wrap in this overlay — this line is clipped: \"{line}\"");
            }
        }

        private static string Find(IReadOnlyList<string> lines, string contains)
        {
            foreach (string line in lines)
            {
                if (line.IndexOf(contains, System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return line;
                }
            }

            Assert.Fail($"no line contained \"{contains}\" (the scan found {lines.Count} lines)");
            return string.Empty;
        }

        private static string Join(IReadOnlyList<string> lines) => string.Join(" ", lines);
    }
}
