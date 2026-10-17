using NUnit.Framework;
using PFE.Core;
using PFE.Systems.Campaign;
using PFE.Systems.RPG;
using PFE.UI.MainMenu;

namespace PFE.Tests.Editor.Campaign
{
    /// <summary>
    /// The offline half of the difficulty model: <see cref="GlobalDifficulty"/> (D1),
    /// <see cref="DifficultyRules.PersTable"/> and its applier (D2), the Axis-B run/land rules (D3),
    /// enemy HP scaling (D6), the checkpoint rule (D7) and <c>isNoResBoss</c> (D11).
    ///
    /// <para><b>Why these are asserted here rather than by a play-test.</b> Every one of these is a
    /// number the game reads but never prints: <c>difCapsMult</c> halves money on Super-hard,
    /// <c>param3</c> decides what difficulty a land is built at, <c>earMult</c> halves hearing. None of
    /// them shows up on screen, so a wrong value is invisible until someone diffs a run against the
    /// oracle. The rules live in <see cref="DifficultyRules"/> precisely so they can be executed here
    /// (D4's planner fixtures cover the one rule that needs an RNG).</para>
    ///
    /// <para><b>Two axes, deliberately kept apart.</b> Axis A is the <i>setting</i>
    /// (<c>globalDif</c>, 0..4, chosen in the menu); axis B is the <i>land/run</i> difficulty
    /// (<c>param3</c> → <c>landDifLevel</c>), which is a function of the player's <b>level</b> and knows
    /// nothing about the setting. The single easiest way to break this port is to derive one from the
    /// other, so <see cref="RunDifficulty_IsAFunctionOfThePlayerLevel_NotOfTheSetting"/> pins that
    /// separation directly.</para>
    /// </summary>
    [TestFixture]
    public class DifficultyRulesTests
    {
        [SetUp]
        public void SetUp()
        {
            GlobalDifficulty.ResetForTests();
            GameBootData.ClearPendingNewGame();
        }

        [TearDown]
        public void TearDown()
        {
            GlobalDifficulty.ResetForTests();
            GameBootData.ClearPendingNewGame();
        }

        private static NewGameSettings Settings(NewGameDifficulty difficulty)
        {
            return new NewGameSettings { difficulty = difficulty };
        }

        // ════════════════════════════════════════════════════════════════════════════════════════════
        //  D1 — the runtime setting (Game.as:41, :311-329)
        // ════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>The default is <b>2</b>, not 0 — AS3 <c>public var globalDif:int = 2</c> (Game.as:41).</summary>
        [Test]
        public void Default_IsNormal()
        {
            Assert.That(GlobalDifficulty.Default, Is.EqualTo(2));
            Assert.That(GlobalDifficulty.MinIndex, Is.EqualTo(0));
            Assert.That(GlobalDifficulty.MaxIndex, Is.EqualTo(4));
        }

        /// <summary>Game.as:318-325 — two clamp blocks, no <c>else</c>.</summary>
        [Test]
        public void Change_ClampsBelowAndAboveTheTable()
        {
            Assert.That(GlobalDifficulty.Change(-1), Is.True, "Change always reports a change unless the raw arg equals the value.");
            Assert.That(GlobalDifficulty.Value, Is.EqualTo(0));

            Assert.That(GlobalDifficulty.Change(9), Is.True);
            Assert.That(GlobalDifficulty.Value, Is.EqualTo(4));
        }

        /// <summary>
        /// Game.as:313-316 — <c>if(param1 == this.globalDif) return false;</c>. The bool is what the
        /// caller's redraw decision rides on, so it must be false on the second identical call.
        /// </summary>
        [Test]
        public void Change_SameValueTwice_SecondReturnsFalse()
        {
            GlobalDifficulty.Seed(2);

            Assert.That(GlobalDifficulty.Change(2), Is.False, "the value is already 2");
            Assert.That(GlobalDifficulty.Change(3), Is.True);
            Assert.That(GlobalDifficulty.Change(3), Is.False, "the second identical call must early-return");
        }

        /// <summary>
        /// <b>The comparison is against the raw argument, not the clamp result</b> — the oracle's own
        /// behaviour, reproduced rather than "fixed" (Game.as:313-316 runs before :318-325). So
        /// <c>Change(9)</c> at level 4 reports a change even though nothing can move, and the caller
        /// re-applies the parameter table every time.
        /// </summary>
        [Test]
        public void Change_ComparesTheRawArgument_SoAnOutOfRangeCallStillReportsAChange()
        {
            GlobalDifficulty.Seed(4);
            Assert.That(GlobalDifficulty.Change(9), Is.True,
                "9 != 4, so the oracle's early return does not fire even though the clamp cannot move the value");
            Assert.That(GlobalDifficulty.Value, Is.EqualTo(4));

            GlobalDifficulty.Seed(0);
            Assert.That(GlobalDifficulty.Change(-1), Is.True, "-1 != 0");
            Assert.That(GlobalDifficulty.Value, Is.EqualTo(0));
        }

        /// <summary>AS3's clamp, as a value — used by every clamp site so they cannot disagree.</summary>
        [Test]
        public void Clamp_HoldsTheTableBounds()
        {
            for (int n = 0; n <= 4; n++) Assert.That(GlobalDifficulty.Clamp(n), Is.EqualTo(n));
            Assert.That(GlobalDifficulty.Clamp(-3), Is.EqualTo(0));
            Assert.That(GlobalDifficulty.Clamp(5), Is.EqualTo(4));
            Assert.That(GlobalDifficulty.Clamp(99), Is.EqualTo(4));
        }

        /// <summary>
        /// The boot seed (D1 §3): <c>NewGameSettings.difficulty</c> is already 0..4 with
        /// <c>Normal = 2</c>, so there is <b>no translation</b> — mapping an enum member onto the wrong
        /// index would move the whole run's difficulty silently.
        /// </summary>
        [Test]
        public void Value_SeedsFromThePendingNewGameDifficulty()
        {
            GameBootData.SetPendingNewGame(Settings(NewGameDifficulty.Normal));
            GlobalDifficulty.ResetForTests();
            Assert.That(GlobalDifficulty.Value, Is.EqualTo(2));

            GameBootData.SetPendingNewGame(Settings(NewGameDifficulty.VeryEasy));
            GlobalDifficulty.ResetForTests();
            Assert.That(GlobalDifficulty.Value, Is.EqualTo(0));

            GameBootData.SetPendingNewGame(Settings(NewGameDifficulty.SuperHard));
            GlobalDifficulty.ResetForTests();
            Assert.That(GlobalDifficulty.Value, Is.EqualTo(4));
        }

        /// <summary>With no pending new game the read falls back to Normal (Game.as:41).</summary>
        [Test]
        public void Value_WithNothingPending_IsNormal()
        {
            Assert.That(GameBootData.HasPendingNewGame, Is.False);
            Assert.That(GlobalDifficulty.Value, Is.EqualTo(GlobalDifficulty.Default));
        }

        /// <summary>The tail of <c>changeDif</c> (Game.as:326-327): the pers table is re-run on a change.</summary>
        [Test]
        public void Change_RaisesChangedWithTheClampedValue()
        {
            int seen = -1;
            int times = 0;
            GlobalDifficulty.Changed += n => { seen = n; times++; };

            GlobalDifficulty.Change(9);

            Assert.That(seen, Is.EqualTo(4), "the event carries the CLAMPED value");
            Assert.That(times, Is.EqualTo(1));
        }

        /// <summary>
        /// The console verb <c>dif X</c> (Consol.as:237-247) assigns the field directly and has <b>no
        /// equality early-return</b>, so <c>dif 2</c> on a run already at 2 still re-applies the table.
        /// That is the one behavioural difference from <see cref="GlobalDifficulty.Change"/>.
        /// </summary>
        [Test]
        public void SetFromConsole_HasNoEqualityEarlyReturn()
        {
            GlobalDifficulty.Seed(2);

            int times = 0;
            GlobalDifficulty.Changed += _ => times++;

            GlobalDifficulty.SetFromConsole(2);

            Assert.That(GlobalDifficulty.Value, Is.EqualTo(2));
            Assert.That(times, Is.EqualTo(1),
                "the console verb re-applies the table unconditionally — unlike Change(2), which would early-return");
        }

        /// <summary>
        /// <see cref="GlobalDifficulty.Seed"/> applies even when it equals the current value: a new game
        /// at Normal must still get Normal's table rather than whatever the field initialisers left.
        /// </summary>
        [Test]
        public void Seed_AppliesEvenWhenTheValueIsUnchanged()
        {
            int times = 0;
            GlobalDifficulty.Changed += _ => times++;

            GlobalDifficulty.Seed(2);
            GlobalDifficulty.Seed(2);

            Assert.That(times, Is.EqualTo(2));
        }

        // ════════════════════════════════════════════════════════════════════════════════════════════
        //  D2 — Pers.setGlobalDif (Pers.as:759-839)
        // ════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>The load-bearing cell: <c>difCapsMult</c> is 1 everywhere except Super-hard, where it is
        /// 0.5</b> (Pers.as:773,787,802,817,834). It is the difficulty term on loot money and bits
        /// (LootGen.as:245-252). A port that defaulted it to 1 for every difficulty would over-pay the
        /// player on the hardest setting and no test would notice.
        /// </summary>
        [Test]
        public void PersTable_DifCapsMult_IsOneEverywhereExceptSuperHard()
        {
            Assert.That(DifficultyRules.PersTable(0).DifCapsMult, Is.EqualTo(1f).Within(1e-5));
            Assert.That(DifficultyRules.PersTable(1).DifCapsMult, Is.EqualTo(1f).Within(1e-5));
            Assert.That(DifficultyRules.PersTable(2).DifCapsMult, Is.EqualTo(1f).Within(1e-5));
            Assert.That(DifficultyRules.PersTable(3).DifCapsMult, Is.EqualTo(1f).Within(1e-5));
            Assert.That(DifficultyRules.PersTable(4).DifCapsMult, Is.EqualTo(0.5f).Within(1e-5),
                "Super-hard HALVES money (Pers.as:834) — it does not scale loot quality");
        }

        /// <summary>
        /// The three cells the port actually applies, transcribed value for value. <c>organMult</c> and
        /// <c>dieDamage</c> are the wound model: on Very-easy nothing is ever fatal
        /// (<c>dieDamage = 0</c>), which is the whole point of the easiest setting.
        /// </summary>
        [Test]
        public void PersTable_AppliedCells_MatchTheOracle()
        {
            Assert.That(DifficultyRules.PersTable(0).OrganMult, Is.EqualTo(0.2f).Within(1e-5));
            Assert.That(DifficultyRules.PersTable(0).DieDamage, Is.EqualTo(0f).Within(1e-5));
            Assert.That(DifficultyRules.PersTable(0).TeleMana, Is.EqualTo(0f).Within(1e-5));

            Assert.That(DifficultyRules.PersTable(1).OrganMult, Is.EqualTo(0.5f).Within(1e-5));
            Assert.That(DifficultyRules.PersTable(1).DieDamage, Is.EqualTo(0f).Within(1e-5));

            Assert.That(DifficultyRules.PersTable(2).OrganMult, Is.EqualTo(1f).Within(1e-5));
            Assert.That(DifficultyRules.PersTable(2).DieDamage, Is.EqualTo(0.15f).Within(1e-5));
            Assert.That(DifficultyRules.PersTable(2).TeleMana, Is.EqualTo(0.5f).Within(1e-5));

            Assert.That(DifficultyRules.PersTable(3).OrganMult, Is.EqualTo(1f).Within(1e-5));
            Assert.That(DifficultyRules.PersTable(3).DieDamage, Is.EqualTo(0.25f).Within(1e-5));
            Assert.That(DifficultyRules.PersTable(3).TeleMana, Is.EqualTo(1f).Within(1e-5));

            Assert.That(DifficultyRules.PersTable(4).OrganMult, Is.EqualTo(1f).Within(1e-5));
            Assert.That(DifficultyRules.PersTable(4).DieDamage, Is.EqualTo(0.35f).Within(1e-5));
        }

        /// <summary>
        /// The table's <c>petRes</c> column (20/20/30/60/90) is <b>recorded but never applied</b>: the
        /// oracle's own <c>defaultParams()</c> overwrites it with the literal 30 on every call
        /// (Pers.as:861). Asserting the effective constant stops someone "fixing" the dead write by
        /// wiring the column up.
        /// </summary>
        [Test]
        public void PersTable_PetRes_IsRecordedButTheEffectiveValueIsAlwaysThirty()
        {
            Assert.That(DifficultyRules.PersTable(0).PetRes, Is.EqualTo(20f).Within(1e-5));
            Assert.That(DifficultyRules.PersTable(4).PetRes, Is.EqualTo(90f).Within(1e-5));
            Assert.That(DifficultyRules.EffectivePetRes, Is.EqualTo(30f).Within(1e-5));
        }

        /// <summary>
        /// An index outside 0..4 must not read a row that does not exist. The oracle has no
        /// <c>else</c> branch and would apply nothing; the port clamps, and says so in
        /// <see cref="DifficultyRules.PersTable"/>'s remarks.
        /// </summary>
        [Test]
        public void PersTable_OutOfRangeIndex_ClampsRatherThanApplyingNothing()
        {
            Assert.That(DifficultyRules.PersTable(-5).Index, Is.EqualTo(0));
            Assert.That(DifficultyRules.PersTable(99).Index, Is.EqualTo(4));
        }

        // ── executing a MonoBehaviour method offline ─────────────────────────────────────────────────
        //
        // CharacterStats is a MonoBehaviour, so `new` is illegal in Unity and dies on the plain harness
        // with "ECall methods must be packaged into a system module" (field initializers touch Unity
        // state). The difficulty applier, though, is four managed field assignments and a call into the
        // pure table — no Unity API anywhere — so it is executed on an UNINITIALIZED object shell: the
        // constructor (and its Unity-touching initializers) is skipped, only managed memory exists.
        //
        // Two consequences, both handled below: field initializers did NOT run (so a "default" value must
        // be written by hand before the call), and IF this test ever dies with an ECall again, the
        // applier has started touching a Unity API — move it back to an owner-only EditMode fixture
        // rather than wiring Unity into the wall.

        private static CharacterStats UninitializedStats()
            => (CharacterStats)System.Runtime.CompilerServices.RuntimeHelpers
                   .GetUninitializedObject(typeof(CharacterStats));

        /// <summary>
        /// The applier writes the cells that have a destination in this port, and does <b>not</b> touch
        /// <c>petRes</c> — asserted as "unchanged by the call" rather than "equals 30", because the shell
        /// skips field initializers (the constant-30 default is pinned by
        /// <see cref="PersTable_PetRes_IsRecordedButTheEffectiveValueIsAlwaysThirty"/>).
        /// </summary>
        [Test]
        public void SetGlobalDifficulty_AppliesTheOracleTableToTheCharacter()
        {
            var stats = UninitializedStats();

            stats.SetGlobalDifficulty(0);
            Assert.That(stats.organMult, Is.EqualTo(0.2f).Within(1e-5));
            Assert.That(stats.dieDamage, Is.EqualTo(0f).Within(1e-5));
            Assert.That(stats.difCapsMult, Is.EqualTo(1f).Within(1e-5));

            float petResBefore = stats.petRes;
            stats.SetGlobalDifficulty(4);
            Assert.That(stats.organMult, Is.EqualTo(1f).Within(1e-5));
            Assert.That(stats.dieDamage, Is.EqualTo(0.35f).Within(1e-5));
            Assert.That(stats.difCapsMult, Is.EqualTo(0.5f).Within(1e-5),
                "Super-hard halves money — this is the cell that reaches the loot roller");
            Assert.That(stats.petRes, Is.EqualTo(petResBefore),
                "petRes is a dead write in the oracle (Pers.as:861 overwrites it) — the applier must not touch it");

            stats.SetGlobalDifficulty(2);
            Assert.That(stats.organMult, Is.EqualTo(1f).Within(1e-5));
            Assert.That(stats.dieDamage, Is.EqualTo(0.15f).Within(1e-5));
            Assert.That(stats.difCapsMult, Is.EqualTo(1f).Within(1e-5));
        }

        /// <summary>
        /// The loot side of D2: <c>CharacterStats</c> is the <see cref="PFE.Systems.Loot.ILootPersFlags"/>
        /// the roller asked for, so <c>SetGlobalDifficulty(4)</c> must reach <c>DifCapsMult</c> — that is
        /// the whole path from the menu to a stack of caps.
        /// </summary>
        [Test]
        public void SetGlobalDifficulty_ReachesTheLootPersFlags()
        {
            var stats = UninitializedStats();
            stats.difCapsMult = 1f;   // the field initializer the uninitialized shell skipped
            PFE.Systems.Loot.ILootPersFlags pers = stats;

            Assert.That(pers.DifCapsMult, Is.EqualTo(1f).Within(1e-5), "the written starting value");

            stats.SetGlobalDifficulty(4);
            Assert.That(pers.DifCapsMult, Is.EqualTo(0.5f).Within(1e-5),
                "the difficulty table must be visible to the roller, or money never scales");
        }

        // ════════════════════════════════════════════════════════════════════════════════════════════
        //  D3 — the run-difficulty term (Game.as:353-361) and landDifLevel (Land.as:118-138)
        // ════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <c>param3</c> is <b>0 until <c>firstroom</c> is set</b>, whatever the player's level — the
        /// first land is always built at the floor.
        /// </summary>
        [Test]
        public void RunDifficulty_FirstRoomIsZeroWhateverTheLevel()
        {
            Assert.That(DifficultyRules.RunDifficulty(false, 1), Is.EqualTo(0));
            Assert.That(DifficultyRules.RunDifficulty(false, 7), Is.EqualTo(0),
                "a level-7 player entering their FIRST land still builds it at 0 (Game.as:353-361)");
        }

        /// <summary>After the first room: <c>max(1, level - 1)</c>.</summary>
        [Test]
        public void RunDifficulty_AfterTheFirstRoom_IsMaxOfOneAndLevelMinusOne()
        {
            Assert.That(DifficultyRules.RunDifficulty(true, 1), Is.EqualTo(1), "level 1 floors at 1, not 0");
            Assert.That(DifficultyRules.RunDifficulty(true, 2), Is.EqualTo(1));
            Assert.That(DifficultyRules.RunDifficulty(true, 12), Is.EqualTo(11));
        }

        /// <summary>
        /// <b>The separation that is easiest to get wrong.</b> <c>param3</c> takes the player's level;
        /// <c>globalDif</c> is not an input to it. Sweeping the setting over a fixed level must not move
        /// the run difficulty by one.
        /// </summary>
        [Test]
        public void RunDifficulty_IsAFunctionOfThePlayerLevel_NotOfTheSetting()
        {
            for (int globalDif = 0; globalDif <= 4; globalDif++)
            {
                Assert.That(DifficultyRules.RunDifficulty(true, 9), Is.EqualTo(8),
                    "globalDif = {0} must not change param3", globalDif);
            }
        }

        /// <summary>
        /// A procedural land takes <c>max(param3, act.dif)</c> — the authored difficulty is a
        /// <b>floor</b>, never a ceiling (Land.as:120-124).
        /// </summary>
        [Test]
        public void LandDifficulty_ProceduralTakesTheMaxOfTheRunAndTheAct()
        {
            Assert.That(DifficultyRules.LandDifficulty(true, 2f, 11, false), Is.EqualTo(11f).Within(1e-4));
            Assert.That(DifficultyRules.LandDifficulty(true, 2f, 0, false), Is.EqualTo(2f).Within(1e-4),
                "act.dif 2 floors a run of 0");
            Assert.That(DifficultyRules.LandDifficulty(true, 8f, 3, false), Is.EqualTo(8f).Within(1e-4),
                "the run cannot pull a procedural land BELOW its authored difficulty");
        }

        /// <summary>
        /// An authored land takes <c>act.dif</c>, and <c>autoLevel</c> replaces it with <c>param3</c>
        /// <b>instead of taking the max</b> (Land.as:131-135) — so an <c>autoLevel</c> land can be built
        /// <i>below</i> its authored value. That asymmetry between the two branches is the whole point.
        /// </summary>
        [Test]
        public void LandDifficulty_AuthoredHonoursAutoLevelAndCanGoBelowTheAct()
        {
            Assert.That(DifficultyRules.LandDifficulty(false, 2f, 11, false), Is.EqualTo(2f).Within(1e-4),
                "without autoLevel an authored land is frozen at act.dif");
            Assert.That(DifficultyRules.LandDifficulty(false, 2f, 11, true), Is.EqualTo(11f).Within(1e-4),
                "autoLevel takes param3 INSTEAD of act.dif");
            Assert.That(DifficultyRules.LandDifficulty(false, 8f, 2, true), Is.EqualTo(2f).Within(1e-4),
                "and that can be BELOW act.dif — a max() here would be wrong");
        }

        /// <summary>
        /// <c>lootLimit = param3 + 3</c> (Land.as:139) uses the <b>raw</b> run difficulty, so on an
        /// <c>act.dif = 8</c> land entered at <c>param3 = 2</c> the cap is <b>5</b>, not 11 — the loot
        /// cap sits below the land difficulty. That is the oracle's arithmetic, not a bug to fix (Q2).
        /// </summary>
        [Test]
        public void LootLimit_UsesTheRawRunDifficultyNotTheFlooredLandLevel()
        {
            Assert.That(DifficultyRules.LootLimit(2), Is.EqualTo(5));
            Assert.That(DifficultyRules.LootLimit(0), Is.EqualTo(3));

            float landDifLevel = DifficultyRules.LandDifficulty(true, 8f, 2, false);
            Assert.That(landDifLevel, Is.EqualTo(8f).Within(1e-4));
            Assert.That(DifficultyRules.LootLimit(2), Is.Not.EqualTo((int)landDifLevel + 3),
                "lootLimit is param3 + 3, NOT landDifLevel + 3 — the two differ whenever act.dif > param3");
        }

        // ════════════════════════════════════════════════════════════════════════════════════════════
        //  D6 — Unit maxhp scaling (Unit.as:1130-1141)
        // ════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>Two arms, not a range.</b> The outer guard is <c>&lt;= 1</c> while the arms are
        /// <c>== 0</c> / <c>== 1</c>; collapsing them into "<c>&lt;= 1 → ×0.4</c>" is wrong at level 1.
        /// </summary>
        [Test]
        public void EnemyHpMultiplier_TwoArmsNotARange()
        {
            Assert.That(DifficultyRules.EnemyHpMultiplier(0), Is.EqualTo(0.4f).Within(1e-5));
            Assert.That(DifficultyRules.EnemyHpMultiplier(1), Is.EqualTo(0.7f).Within(1e-5),
                "level 1 is its own arm — a single '<= 1' branch would give 0.4 here");
            Assert.That(DifficultyRules.EnemyHpMultiplier(2), Is.EqualTo(1f).Within(1e-5));
            Assert.That(DifficultyRules.EnemyHpMultiplier(3), Is.EqualTo(1f).Within(1e-5));
            Assert.That(DifficultyRules.EnemyHpMultiplier(4), Is.EqualTo(1f).Within(1e-5),
                "Super-hard does NOT buff enemy HP — it halves the player's money instead");
        }

        /// <summary>
        /// Both halves of <c>fraction != F_PLAYER &amp;&amp; globalDif &lt;= 1</c> (Unit.as:1130): the
        /// player's own HP is never scaled, at any difficulty. Note the guard is on the <b>definition's</b>
        /// faction (<c>node0 = AllData.d.unit.(@id == mid)[0]</c>, :997), so a player-faction camp turret
        /// IS scaled.
        /// </summary>
        [Test]
        public void ScalesUnitHp_SkipsThePlayerAtEveryDifficulty()
        {
            for (int d = 0; d <= 4; d++)
            {
                Assert.That(DifficultyRules.ScalesUnitHp(d, isPlayer: true), Is.False,
                    "the player's HP is never scaled (globalDif = {0})", d);
            }

            Assert.That(DifficultyRules.ScalesUnitHp(0, isPlayer: false), Is.True);
            Assert.That(DifficultyRules.ScalesUnitHp(1, isPlayer: false), Is.True);
            Assert.That(DifficultyRules.ScalesUnitHp(2, isPlayer: false), Is.False);
            Assert.That(DifficultyRules.ScalesUnitHp(4, isPlayer: false), Is.False);
        }

        // ════════════════════════════════════════════════════════════════════════════════════════════
        //  D7 — the checkpoint rule (Location.as:2043)
        // ════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>Easy and Very-easy always teleport the checkpoint on — no draw at all.</summary>
        [Test]
        public void CheckpointTeleOn_BelowNormal_IsAlwaysTrue()
        {
            Assert.That(DifficultyRules.CheckpointTeleOn(0, landRnd: false, roll: 0.99f), Is.True);
            Assert.That(DifficultyRules.CheckpointTeleOn(1, landRnd: false, roll: 0.99f), Is.True);
            Assert.That(DifficultyRules.CheckpointTeleOn(0, landRnd: true, roll: 0.99f), Is.True);
        }

        /// <summary>Hard and Super-hard never do — the port-invented "always on at landStage 0" rule is gone.</summary>
        [Test]
        public void CheckpointTeleOn_AboveNormal_IsAlwaysFalse()
        {
            Assert.That(DifficultyRules.CheckpointTeleOn(3, landRnd: true, roll: 0f), Is.False);
            Assert.That(DifficultyRules.CheckpointTeleOn(4, landRnd: true, roll: 0f), Is.False);
        }

        /// <summary>
        /// Normal + procedural is the 33 % arm: <c>land.rnd &amp;&amp; globalDif == 2 &amp;&amp;
        /// Math.random() &lt; 0.33</c>. Strictly less-than, so 0.33 itself misses.
        /// </summary>
        [Test]
        public void CheckpointTeleOn_NormalProcedural_IsAThirdChance()
        {
            Assert.That(DifficultyRules.CheckpointTeleOn(2, landRnd: true, roll: 0.32f), Is.True);
            Assert.That(DifficultyRules.CheckpointTeleOn(2, landRnd: true, roll: 0.33f), Is.False,
                "the comparison is strict — the boundary itself misses");
            Assert.That(DifficultyRules.CheckpointTeleOn(2, landRnd: true, roll: 0.99f), Is.False);
        }

        /// <summary>Normal + <b>authored</b> is false, and must not even draw (see the next test).</summary>
        [Test]
        public void CheckpointTeleOn_NormalAuthored_IsFalse()
        {
            Assert.That(DifficultyRules.CheckpointTeleOn(2, landRnd: false, roll: 0f), Is.False);
        }

        /// <summary>
        /// A uniform sweep over [0,1) must land on 33 %. Asserted as a frequency rather than a single
        /// boundary value so a change to 0.5 or 0.25 is caught, not just an inversion.
        /// </summary>
        [Test]
        public void CheckpointTeleOn_NormalProcedural_FrequencyIsAThird()
        {
            const int samples = 1000;
            int on = 0;
            for (int i = 0; i < samples; i++)
            {
                float roll = i / (float)samples;                     // a uniform sweep of [0, 1)
                if (DifficultyRules.CheckpointTeleOn(2, landRnd: true, roll)) on++;
            }

            Assert.That(on, Is.EqualTo(330), "roll < 0.33 over a 1000-step sweep of [0,1)");
        }

        /// <summary>
        /// <b>The draw must be short-circuited.</b> The oracle's <c>&amp;&amp;</c> chain reaches
        /// <c>Math.random()</c> only at Normal on a procedural land. A caller that drew unconditionally
        /// would spend an extra value from the shared spawn stream on every checkpoint everywhere else,
        /// shifting every later placement in the land with no error.
        /// </summary>
        [Test]
        public void CheckpointRollsForTeleOn_OnlyAtNormalAndProcedural()
        {
            Assert.That(DifficultyRules.CheckpointRollsForTeleOn(2, landRnd: true), Is.True);

            Assert.That(DifficultyRules.CheckpointRollsForTeleOn(0, landRnd: true), Is.False);
            Assert.That(DifficultyRules.CheckpointRollsForTeleOn(1, landRnd: true), Is.False);
            Assert.That(DifficultyRules.CheckpointRollsForTeleOn(3, landRnd: true), Is.False);
            Assert.That(DifficultyRules.CheckpointRollsForTeleOn(4, landRnd: true), Is.False);
            Assert.That(DifficultyRules.CheckpointRollsForTeleOn(2, landRnd: false), Is.False,
                "an authored land must not draw, or the shared stream shifts");
        }

        // ════════════════════════════════════════════════════════════════════════════════════════════
        //  D11 — Unit.isNoResBoss (Unit.as:1743-1754) — DEFERRED, no consumer
        // ════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>Super-hard is false: <c>globalDif &lt;= 3</c> is the first half of the guard.</summary>
        [Test]
        public void IsNoResBoss_FalseAtSuperHard()
        {
            Assert.That(DifficultyRules.IsNoResBoss(4, hasLocation: true, landTip: "rbl"), Is.False);
        }

        /// <summary>A <c>base</c> land is false — the second half of the guard.</summary>
        [Test]
        public void IsNoResBoss_FalseOnABaseLand()
        {
            Assert.That(DifficultyRules.IsNoResBoss(2, hasLocation: true, landTip: "base"), Is.False);
        }

        /// <summary>Otherwise true, at every difficulty up to Hard.</summary>
        [Test]
        public void IsNoResBoss_TrueOtherwise()
        {
            for (int d = 0; d <= 3; d++)
            {
                Assert.That(DifficultyRules.IsNoResBoss(d, hasLocation: true, landTip: "rbl"), Is.True,
                    "globalDif = {0}", d);
            }
        }

        /// <summary>
        /// The oracle wraps the expression in a <c>try</c>/<c>catch</c> that answers false, which is how
        /// a unit with no location behaves. Modelled as <c>hasLocation: false</c> rather than as an
        /// exception.
        /// </summary>
        [Test]
        public void IsNoResBoss_NoLocation_IsFalse()
        {
            Assert.That(DifficultyRules.IsNoResBoss(2, hasLocation: false, landTip: "rbl"), Is.False);
            Assert.That(DifficultyRules.IsNoResBoss(2, hasLocation: true, landTip: null), Is.True,
                "a null tip is not 'base' — the oracle compares against the literal");
        }
    }
}
