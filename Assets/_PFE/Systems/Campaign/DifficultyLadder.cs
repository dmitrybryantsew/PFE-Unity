using System;
using System.Collections.Generic;
using System.Globalization;
using PFE.Systems.Combat;
using PFE.UI.MainMenu;

namespace PFE.Systems.Campaign
{
    /// <summary>
    /// What a checkpoint's <c>teleOn</c> does at a given difficulty, as a three-way answer rather than a
    /// bool — because "always on" and "33 % of the time" are different facts and one bool cannot carry
    /// both.
    /// </summary>
    public enum CheckpointTeleMode
    {
        /// <summary><c>globalDif &lt;= 1</c> — <c>teleOn</c> is written with no roll at all.</summary>
        Always,

        /// <summary><c>globalDif == 2</c> — one draw is spent, and <c>teleOn</c> is written when it is
        /// below 0.33 (<c>Location.as:2043</c>). Only procedural lands reach the draw.</summary>
        Chance,

        /// <summary><c>globalDif &gt;= 3</c> — <c>teleOn</c> is never written.</summary>
        Never
    }

    /// <summary>
    /// Every <b>live</b> consequence of one difficulty index, as values — the row the F2 difficulty panel
    /// prints and the fixture asserts.
    ///
    /// <para><b>Every field is read from the rule that gameplay reads.</b> Nothing here is a second copy
    /// of a number: <see cref="Pers"/> comes from <see cref="DifficultyRules.PersTable"/>, the enemy-HP and
    /// ear/enemy-level terms from <see cref="DifficultyRules"/>, the boss terms from
    /// <c>BossAlicornRules</c> / <c>BossNecrRules</c>. A panel that re-derives a value can describe a game
    /// that does not exist; this one cannot.</para>
    ///
    /// <para><b>What is deliberately absent.</b> <c>isNoResBoss</c> is not carried: its three oracle
    /// readers are all on the un-ported <c>Unit.setNull</c> death path, so a panel line for it would claim
    /// an effect the port does not have. The <c>Pers</c> cells with no destination field
    /// (<c>begHp</c>, <c>lvlHp</c>, <c>critHeal</c>, the five <c>Price*</c>, <c>himBadDif</c>) are carried
    /// only through <see cref="SpecOnlyLines"/>, which says so in words.</para>
    /// </summary>
    public readonly struct DifficultyLevelFacts
    {
        /// <summary>The clamped index, 0..4.</summary>
        public readonly int Index;

        /// <summary>The main menu's own name for this index (<c>VeryEasy</c> … <c>SuperHard</c>).</summary>
        public readonly string Name;

        /// <summary>The <c>Pers.setGlobalDif</c> row — the table the player's stats are set from.</summary>
        public readonly PersDifficultyTable Pers;

        /// <summary>Non-player max-HP multiplier (<c>Unit.as:1130-1141</c>). 1 above level 1.</summary>
        public readonly float EnemyHpMultiplier;

        /// <summary>Location hearing multiplier (<c>Land.as:988-991</c>). 0.5 below Normal, else 1.</summary>
        public readonly float EarMultiplier;

        /// <summary>Levels added to a room's <c>enemyLevel</c> (<c>Land.as:992-995</c>). 0, 2 or 4.</summary>
        public readonly int EnemyLevelBonus;

        /// <summary>The checkpoint's <c>teleOn</c> behaviour (<c>Location.as:2043</c>).</summary>
        public readonly CheckpointTeleMode Checkpoint;

        /// <summary>Alicorn boss attack multiplier (<c>BossAlicornRules.DifficultyScale</c>).</summary>
        public readonly float BossAttackScale;

        /// <summary>Necromancer summon attack multiplier (<c>BossNecrRules.SummonAttackScaleFor</c>).</summary>
        public readonly float SummonAttackScale;

        public DifficultyLevelFacts(
            int index, string name, PersDifficultyTable pers, float enemyHpMultiplier, float earMultiplier,
            int enemyLevelBonus, CheckpointTeleMode checkpoint, float bossAttackScale, float summonAttackScale)
        {
            Index = index;
            Name = name;
            Pers = pers;
            EnemyHpMultiplier = enemyHpMultiplier;
            EarMultiplier = earMultiplier;
            EnemyLevelBonus = enemyLevelBonus;
            Checkpoint = checkpoint;
            BossAttackScale = bossAttackScale;
            SummonAttackScale = summonAttackScale;
        }
    }

    /// <summary>
    /// The difficulty ladder as data plus pre-wrapped text: one row per index, and the lines the F2 panel
    /// draws for it.
    ///
    /// <para><b>Why the text lives here and not in the overlay.</b> The overlay cannot be tested offline
    /// and a <c>GUILayout.Label</c> does not wrap — a line that is too long is <i>clipped</i>, silently,
    /// and the tail is usually the part that explains the rest. Composing the lines in a Unity-free class
    /// makes both the numbers and the line widths assertable (<see cref="MaxLineLength"/>).</para>
    ///
    /// <para><b>What this is not.</b> It is not a second difficulty model. It is a projection of
    /// <see cref="DifficultyRules"/> and the two boss rule classes over the five indices, and
    /// <see cref="For"/> is the only place that projects them.</para>
    /// </summary>
    public static class DifficultyLadder
    {
        /// <summary>
        /// The widest line <see cref="ChangeLines"/> / <see cref="DeltaLines"/> /
        /// <see cref="SpecOnlyLines"/> will produce.
        /// </summary>
        /// <remarks>
        /// The F2 window is 920 px and its labels do not wrap; at font size 12 that is roughly 135
        /// rendered characters, less once a line is inside a card's padding. 60 is a deliberately
        /// conservative budget — half the window — so the lines survive a narrower window and a wider
        /// font. Asserted by <c>DifficultyLadderTests</c>; do not raise it without re-measuring.
        /// </remarks>
        public const int MaxLineLength = 60;

        /// <summary>How many rows the ladder has — 0..4 inclusive, the clamp's own range.</summary>
        public static int LevelCount => GlobalDifficulty.MaxIndex - GlobalDifficulty.MinIndex + 1;

        /// <summary>
        /// The main menu's name for an index. Reads <see cref="NewGameDifficulty"/>, the enum the new-game
        /// screen and the console's <c>dif</c> reply both use, so the three cannot drift.
        /// </summary>
        public static string NameOf(int index)
        {
            int clamped = GlobalDifficulty.Clamp(index);
            return ((NewGameDifficulty)clamped).ToString();
        }

        /// <summary>
        /// Everything one index changes, read from the rules that implement it. Out-of-range indices are
        /// clamped, exactly as <see cref="DifficultyRules.PersTable"/> clamps — a caller cannot ask for a
        /// row that does not exist.
        /// </summary>
        public static DifficultyLevelFacts For(int index)
        {
            int clamped = GlobalDifficulty.Clamp(index);

            // `teleOn` is derived, not restated: the "always" arm is the rule answering with no land and
            // no roll, and the "rolls" arm is the rule that says whether the draw is reached at all.
            bool always = DifficultyRules.CheckpointTeleOn(clamped, landRnd: false, roll: 0f);
            bool rolls = DifficultyRules.CheckpointRollsForTeleOn(clamped, landRnd: true);

            return new DifficultyLevelFacts(
                index: clamped,
                name: NameOf(clamped),
                pers: DifficultyRules.PersTable(clamped),
                enemyHpMultiplier: DifficultyRules.EnemyHpMultiplier(clamped),
                earMultiplier: DifficultyRules.EarMultiplier(clamped),
                enemyLevelBonus: DifficultyRules.EnemyLevelBonus(clamped),
                checkpoint: always
                    ? CheckpointTeleMode.Always
                    : (rolls ? CheckpointTeleMode.Chance : CheckpointTeleMode.Never),
                bossAttackScale: BossAlicornRules.DifficultyScale(clamped),
                summonAttackScale: BossNecrRules.SummonAttackScaleFor(clamped));
        }

        /// <summary>
        /// The five "what this level does" lines, in a fixed order so two levels can be compared line by
        /// line. Every number is a live effect; nothing here is spec-only (see
        /// <see cref="SpecOnlyLines"/> for those, which is a separate card on purpose).
        /// </summary>
        public static IReadOnlyList<string> ChangeLines(int index)
        {
            DifficultyLevelFacts f = For(index);

            return new[]
            {
                $"organ dmg ×{N(f.Pers.OrganMult)} · forced fatal {P(f.Pers.DieDamage)}",
                $"telekinesis {N(f.Pers.TeleMana)}/tick · money+bits ×{N(f.Pers.DifCapsMult)}",
                $"enemy max HP ×{N(f.EnemyHpMultiplier)} · hearing ×{N(f.EarMultiplier)}",
                $"enemy level +{f.EnemyLevelBonus} · checkpoint {CheckpointText(f.Checkpoint)}",
                $"boss attack ×{N(f.BossAttackScale)} · necro summon ×{N(f.SummonAttackScale)}"
            };
        }

        /// <summary>
        /// The cells the <c>Pers</c> table assigns that have <b>no destination field</b> in the port, said
        /// out loud. Two lines: a header and the values.
        /// </summary>
        /// <remarks>
        /// This exists so the panel cannot be read as "the table above is the whole story". The oracle
        /// reads these (organ init, level-up, healing, med vendors, addiction) and the port has no field
        /// for any of them, so a tester who changes difficulty and sees no change in them is looking at a
        /// recorded gap, not a regression.
        /// </remarks>
        public static IReadOnlyList<string> SpecOnlyLines(int index)
        {
            DifficultyLevelFacts f = For(index);

            return new[]
            {
                "spec only — no destination field in the port:",
                $"begHP {f.Pers.BegHp} · lvlHP {f.Pers.LvlHp} · critHeal {P(f.Pers.CritHeal)} · " +
                $"himBadDif {N(f.Pers.HimBadDif)}"
            };
        }

        /// <summary>
        /// What this index changes <b>relative to Normal</b>, wrapped to <see cref="MaxLineLength"/>.
        /// Empty for Normal itself — there is no delta, and an empty list is the honest answer.
        /// </summary>
        /// <remarks>
        /// Only <i>differences</i> are listed, so a reader does not have to diff two five-line cards by
        /// eye. The items are built from the same facts <see cref="ChangeLines"/> prints, so the two
        /// cannot disagree.
        /// </remarks>
        public static IReadOnlyList<string> DeltaLines(int index)
        {
            DifficultyLevelFacts f = For(index);
            DifficultyLevelFacts normal = For(GlobalDifficulty.Default);

            var items = new List<string>();
            if (!Near(f.Pers.OrganMult, normal.Pers.OrganMult)) items.Add($"organ ×{N(f.Pers.OrganMult)}");
            if (!Near(f.Pers.DieDamage, normal.Pers.DieDamage)) items.Add($"fatal {P(f.Pers.DieDamage)}");
            if (!Near(f.Pers.TeleMana, normal.Pers.TeleMana)) items.Add($"tele {N(f.Pers.TeleMana)}");
            if (!Near(f.Pers.DifCapsMult, normal.Pers.DifCapsMult)) items.Add($"money ×{N(f.Pers.DifCapsMult)}");
            if (!Near(f.EnemyHpMultiplier, normal.EnemyHpMultiplier)) items.Add($"enemyHP ×{N(f.EnemyHpMultiplier)}");
            if (!Near(f.EarMultiplier, normal.EarMultiplier)) items.Add($"hearing ×{N(f.EarMultiplier)}");
            if (f.EnemyLevelBonus != normal.EnemyLevelBonus) items.Add($"enemyLvl +{f.EnemyLevelBonus}");
            if (f.Checkpoint != normal.Checkpoint) items.Add($"checkpoint {CheckpointText(f.Checkpoint)}");
            if (!Near(f.BossAttackScale, normal.BossAttackScale)) items.Add($"boss ×{N(f.BossAttackScale)}");
            if (!Near(f.SummonAttackScale, normal.SummonAttackScale)) items.Add($"necro ×{N(f.SummonAttackScale)}");

            return items.Count == 0
                ? Array.Empty<string>()
                : WrapItems("vs Normal: ", "  ", items);
        }

        /// <summary>The checkpoint mode in words — the three arms of <see cref="CheckpointTeleMode"/>.</summary>
        public static string CheckpointText(CheckpointTeleMode mode)
        {
            switch (mode)
            {
                case CheckpointTeleMode.Always: return "ALWAYS on";
                case CheckpointTeleMode.Chance: return "33% on rnd land";
                case CheckpointTeleMode.Never: return "never";
                default: return "?";
            }
        }

        // ── formatting ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A number as the panel prints it: invariant culture, trailing zeros dropped, so 1f is "1" and
        /// 0.35f is "0.35". Invariant because a comma decimal separator would make "x0,5" in a screenshot
        /// look like a different number.
        /// </summary>
        private static string N(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);

        /// <summary>A 0..1 fraction as a whole percent — <c>dieDamage</c> is only ever read as a fraction.</summary>
        private static string P(float fraction) => N(fraction * 100f) + "%";

        /// <summary>Float equality for "did this cell change?" — the table's own precision, not exact bits.</summary>
        private static bool Near(float a, float b) => Math.Abs(a - b) < 0.0001f;

        /// <summary>
        /// Greedy wrap of <c>a · b · c</c> items onto lines of at most <see cref="MaxLineLength"/>, with
        /// <paramref name="prefix"/> on the first line and <paramref name="continuation"/> on the rest.
        /// </summary>
        /// <remarks>
        /// The first item always lands on the prefix line even if it overflows — an empty line before it
        /// would be worse than one over-long line, and the alternative (dropping the prefix) hides which
        /// baseline the deltas are against.
        /// </remarks>
        private static List<string> WrapItems(string prefix, string continuation, IReadOnlyList<string> items)
        {
            var lines = new List<string>();
            string line = prefix;
            bool empty = true;

            for (int i = 0; i < items.Count; i++)
            {
                string separator = empty ? string.Empty : " · ";

                if (!empty && line.Length + separator.Length + items[i].Length > MaxLineLength)
                {
                    lines.Add(line);
                    line = continuation;
                    separator = string.Empty;
                    empty = true;
                }

                line += separator + items[i];
                empty = false;
            }

            if (!empty)
            {
                lines.Add(line);
            }

            return lines;
        }
    }
}
