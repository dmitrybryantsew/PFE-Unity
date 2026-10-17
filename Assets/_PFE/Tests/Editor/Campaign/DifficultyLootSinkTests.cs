using System;
using NUnit.Framework;
using PFE.Systems.Campaign;
using PFE.Systems.Loot;
using PFE.Tests.Editor.Loot;

namespace PFE.Tests.Editor.Campaign
{
    /// <summary>
    /// D8 (<c>Land.lootLimit</c> / <c>Land.gameStage</c> / <c>Game.limits</c>) and D9
    /// (<c>ctx.Pers</c> / <c>ctx.Env</c>) — the difficulty model's two loot sinks.
    ///
    /// <para><b>What is provable offline, and what is not.</b> The two <i>rules</i> are pure and are
    /// asserted here: the story-stage gate (<c>LootGen.as:133,138</c>), the per-run <c>@limit</c> cap
    /// (<c>:261-286</c>), and the difficulty term on money and bits (<c>:245-252</c>). The <i>wiring</i>
    /// that publishes <see cref="LootRuntime.LandGameStage"/> and <see cref="LootRuntime.LandLootLimit"/>
    /// from a land entry is not: <see cref="LootRuntime.CreateContext"/> builds its pools from a
    /// <c>Resources</c> asset, which is a Unity call, so a context cannot be constructed here. Those
    /// fields are pinned for what they are (land-scoped statics that must not leak between lands) and the
    /// gate they feed is pinned directly against a hand-built context.</para>
    ///
    /// <para><b>Why the sinks matter at all.</b> Before D8/D9 neither sink set <c>GameStage</c>,
    /// <c>LootLimit</c>, <c>Limits</c>, <c>Pers</c> or <c>Env</c>, so every roll ran with stage 0 (no
    /// staged row reachable), a cap of 0 (every limited row refused), no perks and <c>biom 0</c>. All of
    /// those are silent — the container simply has less in it.</para>
    /// </summary>
    [TestFixture]
    public class DifficultyLootSinkTests
    {
        private int _savedGameStage;
        private int _savedLootLimit;
        private string _savedActId;
        private bool _savedRnd;

        [SetUp]
        public void SetUp()
        {
            _savedGameStage = LootRuntime.LandGameStage;
            _savedLootLimit = LootRuntime.LandLootLimit;
            _savedActId = LootRuntime.LandActId;
            _savedRnd = LootRuntime.LandRnd;
        }

        [TearDown]
        public void TearDown()
        {
            LootRuntime.LandGameStage = _savedGameStage;
            LootRuntime.LandLootLimit = _savedLootLimit;
            LootRuntime.LandActId = _savedActId;
            LootRuntime.LandRnd = _savedRnd;
        }

        private static bool Resulted(LootContext ctx, string itemId)
        {
            for (int i = 0; i < ctx.Results.Count; i++)
            {
                if (string.Equals(ctx.Results[i].ItemId, itemId, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        // ════════════════════════════════════════════════════════════════════════════════════════════
        //  D8 — the story-stage gate (LootGen.as:133,138)
        // ════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <c>st == null || st &lt;= gameStage</c> — the land's authored <c>@stage</c> is a gate on a pool
        /// entry, and an <b>absent</b> stage passes rather than counting as 0 (a third state, which is why
        /// <see cref="LootPoolEntry.Stage"/> is nullable).
        ///
        /// <para>The exclusion proof uses the <b>same physical draw</b> in both runs: the filter runs
        /// <i>before</i> the draw, so removing stage_4 also removes its weight, and the identical random
        /// value lands one entry earlier. That is the oracle's arithmetic (<c>LootGen.as:133-167</c>),
        /// not a re-normalisation invented by the port.</para>
        /// </summary>
        [Test]
        public void StageGate_ExcludesEntriesStagedBeyondTheLand()
        {
            var pools = new FakeLootPools()
                .Add("a", new LootPoolEntry("absent_stage", 1f, stage: null))
                .Add("a", new LootPoolEntry("stage_3", 1f, stage: 3))
                .Add("a", new LootPoolEntry("stage_4", 1f, stage: 4));

            // Control at gameStage 4: all three entries survive, draw 0.9 → roll 2.7 of 3 → stage_4.
            var full = LootFixture.Context(new StubRng { Value = 0.9f }, pools);
            full.GameStage = 4;
            Assert.That(LootRoller.GetRandom(full, "a", levelGate: 1f), Is.EqualTo("stage_4"),
                "control: at gameStage 4 the draw reaches the stage-4 entry");

            // gameStage 3: stage_4 is filtered before the draw, the total drops to 2, and the identical
            // draw (0.9 * 2 = 1.8) lands on stage_3 — the stage-4 entry is unreachable, not re-rolled.
            var gated = LootFixture.Context(new StubRng { Value = 0.9f }, pools);
            gated.GameStage = 3;
            Assert.That(LootRoller.GetRandom(gated, "a", levelGate: 1f), Is.EqualTo("stage_3"),
                "stage 4 > gameStage 3 (LootGen.as:138) — filtered before the draw");
            Assert.That(LootRoller.GetRandom(gated, "a", levelGate: 1f), Is.EqualTo("stage_3"),
                "the stub is constant, so the draw is deterministic");

            // The unstaged entry survives the gate (draw 0.1 → roll 0.2 of 2), so the pool is not simply
            // empty and the exclusion above is an exclusion, not a wipe.
            var low = LootFixture.Context(new StubRng { Value = 0.1f }, pools);
            low.GameStage = 3;
            Assert.That(LootRoller.GetRandom(low, "a", levelGate: 1f), Is.EqualTo("absent_stage"),
                "control: the unstaged entry survives the gate");
        }

        /// <summary>
        /// <b>The gate is inert at stage 0</b> — the oracle's <c>_loc7_ &lt;= 0 ||</c> arm
        /// (<c>LootGen.as:138</c>), plus the outer <c>param2 &gt; 0 || param3 &gt; 0 || _loc7_ &gt; 0</c>
        /// (<c>:133</c>). A land with no <c>@stage</c> therefore reaches <i>every</i> staged row. That is
        /// the oracle's own behaviour, not a port shortcut, and it is the reason the wiring matters: stage
        /// 0 is not "the most restrictive stage", it is "no gate".
        /// </summary>
        [Test]
        public void StageGate_IsInertAtStageZero()
        {
            var pools = new FakeLootPools().Add("a", new LootPoolEntry("stage_9", 1f, stage: 9));

            var ctx = LootFixture.Context(new StubRng { Value = 0.5f }, pools);
            ctx.GameStage = 0;

            Assert.That(LootRoller.GetRandom(ctx, "a", levelGate: 1f), Is.EqualTo("stage_9"),
                "gameStage 0 disables the stage gate entirely (LootGen.as:133,138)");
        }

        // ════════════════════════════════════════════════════════════════════════════════════════════
        //  D8 — the per-run @limit cap (LootGen.as:261-286)
        // ════════════════════════════════════════════════════════════════════════════════════════════

        private static LootContext LimitedContext(FakeLimits limits, int lootLimit, LootItemRow row)
        {
            var catalog = new StubCatalog().With(row);
            var ctx = LootFixture.Context(new StubRng { Value = 0.1f }, new FakeLootPools(), catalog,
                                          limits: limits);
            ctx.LootLimit = lootLimit;
            return ctx;
        }

        /// <summary>
        /// The cap is the land's <c>lootLimit</c>, and <b>the counter increments only on a successful
        /// roll</b> (<c>LootGen.as:285</c> — <c>addLimit</c> sits after every <c>return false</c>). A port
        /// that incremented on attempt would exhaust a limit with items it never handed out.
        /// </summary>
        [Test]
        public void LimitCap_StopsTheRollAndOnlyASuccessIncrements()
        {
            var limits = new FakeLimits();
            var row = new LootItemRow("limitem", "item", kol: 1, limitKey: "lim");
            var ctx = LimitedContext(limits, lootLimit: 3, row);

            for (int i = 1; i <= 3; i++)
            {
                Assert.That(LootRoller.RollPlacedItem("limitem", ctx, kol: 1), Is.True, "roll {0} is under the cap", i);
                Assert.That(limits.GetLimit("lim"), Is.EqualTo(i), "roll {0} incremented the counter", i);
            }

            Assert.That(LootRoller.RollPlacedItem("limitem", ctx, kol: 1), Is.False,
                "have (3) >= cap (3) — the roll is refused (LootGen.as:283-286)");
            Assert.That(limits.GetLimit("lim"), Is.EqualTo(3),
                "a REFUSED roll must not increment, or a limit empties without paying out");
            Assert.That(ctx.UnmatchedKeys, Contains.Item("limit:lim"),
                "the refusal is recorded rather than silently dropped");
        }

        /// <summary><c>@mlim</c> scales the run's loot limit (<c>LootGen.as:264-267</c>).</summary>
        [Test]
        public void LimitCap_MultiplierScalesTheRunLimit()
        {
            var limits = new FakeLimits();
            var row = new LootItemRow("limitem", "item", kol: 1, limitKey: "lim", limitMultiplier: 2f);
            var ctx = LimitedContext(limits, lootLimit: 3, row);

            for (int i = 0; i < 6; i++)
                Assert.That(LootRoller.RollPlacedItem("limitem", ctx, kol: 1), Is.True, "roll {0} of 6", i + 1);

            Assert.That(LootRoller.RollPlacedItem("limitem", ctx, kol: 1), Is.False, "cap is 3 * 2 = 6");
            Assert.That(limits.GetLimit("lim"), Is.EqualTo(6));
        }

        /// <summary>
        /// <c>@maxlim</c> is an <b>absolute</b> cap, checked separately from (and before) the scaled one
        /// (<c>LootGen.as:279-282</c>) — so it binds even when the run's loot limit is far higher.
        /// </summary>
        [Test]
        public void LimitCap_AbsoluteCapBindsIndependentlyOfTheRunLimit()
        {
            var limits = new FakeLimits();
            var row = new LootItemRow("limitem", "item", kol: 1, limitKey: "lim", limitAbsolute: 2);
            var ctx = LimitedContext(limits, lootLimit: 20, row);

            Assert.That(LootRoller.RollPlacedItem("limitem", ctx, kol: 1), Is.True);
            Assert.That(LootRoller.RollPlacedItem("limitem", ctx, kol: 1), Is.True);
            Assert.That(LootRoller.RollPlacedItem("limitem", ctx, kol: 1), Is.False,
                "maxlim = 2 binds even though the run limit is 20");
            Assert.That(ctx.UnmatchedKeys, Contains.Item("maxlim:lim"));
        }

        /// <summary>
        /// <b>No limit store means no cap.</b> <c>Limits == null</c> reads <c>have = 0</c>
        /// (<c>LootGen.as:263</c> — <c>getLimit</c> on a counter that was never written returns 0), so a
        /// positive run limit is never reached and the item always rolls — the pre-D8 behaviour, pinned so
        /// that wiring a store is a visible change rather than an invisible one.
        ///
        /// <para>The limit here must be <b>positive</b>: the oracle's refuse test
        /// (<c>_loc10_ &gt;= _loc11_</c>, <c>:283-286</c>) is unguarded, so <c>lootLimit 0</c> refuses
        /// <i>with</i> a store and refuses identically without one. A shipped land never has
        /// <c>lootLimit 0</c> — it is <c>param3 + 3</c> — so the zero case is unreachable in play.</para>
        /// </summary>
        [Test]
        public void LimitCap_NoStore_MeansNoCap()
        {
            var catalog = new StubCatalog().With(new LootItemRow("limitem", "item", kol: 1, limitKey: "lim"));
            var ctx = LootFixture.Context(new StubRng { Value = 0.1f }, new FakeLootPools(), catalog);
            ctx.LootLimit = 5;
            ctx.Limits = null;

            for (int i = 0; i < 10; i++)
                Assert.That(LootRoller.RollPlacedItem("limitem", ctx, kol: 1), Is.True, "roll {0}", i + 1);
        }

        // ════════════════════════════════════════════════════════════════════════════════════════════
        //  D8 — the land-scoped publishers
        // ════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <c>gameStage</c> and <c>lootLimit</c> are written once per land (<c>Land.as:139-140</c>) and
        /// read back through <c>World.w.land</c>. They are <b>land-scoped</b>, so a land entry that forgot
        /// one would inherit the previous land's — a high stage silently unlocking late-game loot in an
        /// early land. <see cref="LootRuntime.ResetForTests"/> is the seam that clears them.
        /// </summary>
        [Test]
        public void LandScopedFields_AreClearedBetweenRuns()
        {
            LootRuntime.LandGameStage = 7;
            LootRuntime.LandLootLimit = 9;
            LootRuntime.LandActId = "minst";
            LootRuntime.LandRnd = true;

            LootRuntime.ResetForTests();

            Assert.That(LootRuntime.LandGameStage, Is.EqualTo(0));
            Assert.That(LootRuntime.LandLootLimit, Is.EqualTo(0));
            Assert.That(LootRuntime.LandActId, Is.EqualTo(string.Empty));
            Assert.That(LootRuntime.LandRnd, Is.False,
                "a stale landRnd would keep the `safe` bloat-ambush guard armed in the next, authored land");
        }

        /// <summary>
        /// The value D8 publishes is <see cref="DifficultyRules.LootLimit"/> — <b>param3 + 3</b>, not
        /// <c>landDifLevel + 3</c>. On an <c>act.dif = 8</c> land entered at <c>param3 = 2</c> the cap is
        /// 5 while the land's own difficulty is 8.
        /// </summary>
        [Test]
        public void LootLimit_Published_IsParam3PlusThree()
        {
            float landDifLevel = DifficultyRules.LandDifficulty(procedural: true, actDif: 8f,
                                                               runDifficulty: 2, autoLevel: false);
            LootRuntime.LandLootLimit = DifficultyRules.LootLimit(2);

            Assert.That(landDifLevel, Is.EqualTo(8f).Within(1e-4));
            Assert.That(LootRuntime.LandLootLimit, Is.EqualTo(5),
                "the loot cap uses the RAW run difficulty, so it can sit below the land's difficulty");
        }

        /// <summary>
        /// <c>Game.getLimit</c>'s seed arm reads a story trigger of the same name
        /// (<c>Game.as:687-691</c>). It is implemented but <b>deliberately not wired</b>: none of the ten
        /// shipped <c>@limit</c> keys is ever used as a trigger name, so enabling it changes nothing and
        /// would couple the loot runtime to the campaign. The seam is pinned here so the day a mod gates a
        /// unique item with a trigger the call site already exists and is tested.
        ///
        /// <para><c>addLimit</c>'s two deltas go to <b>different tables</b> (<c>Game.as:696-720</c>):
        /// delta 1 increments the counter only — the trigger keeps its seeded value — and delta 2
        /// increments the trigger only. That asymmetry is the oracle's own, and it is why the roller's
        /// <c>AddLimit(key, 1)</c> never touches the campaign's trigger table.</para>
        /// </summary>
        [Test]
        public void WireLimitsToCampaign_SeedsFromTheTriggerTable()
        {
            var triggers = new System.Collections.Generic.Dictionary<string, int> { ["lim"] = 2 };
            LootRuntime.WireLimitsToCampaign(
                readTrigger: key => triggers.TryGetValue(key, out int v) ? v : 0,
                writeTrigger: (key, value) => triggers[key] = value);

            try
            {
                Assert.That(LootRuntime.Limits.GetLimit("lim"), Is.EqualTo(2),
                    "a trigger of the same name seeds the counter (Game.as:687-691)");

                LootRuntime.Limits.AddLimit("lim", 1);
                Assert.That(LootRuntime.Limits.GetLimit("lim"), Is.EqualTo(3),
                    "delta 1 increments the counter");
                Assert.That(triggers["lim"], Is.EqualTo(2),
                    "delta 1 does NOT write the trigger back — that is addLimit's delta-2 arm (:710-719)");

                LootRuntime.Limits.AddLimit("lim", 2);
                Assert.That(triggers["lim"], Is.EqualTo(3), "delta 2 increments the trigger");
            }
            finally
            {
                LootRuntime.Limits = null;
            }
        }

        // ════════════════════════════════════════════════════════════════════════════════════════════
        //  D9 — ctx.Pers: the difficulty term on money (LootGen.as:245-252)
        // ════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>With <c>Pers</c> set, money scales by <c>capsMult × difCapsMult</c>; with <c>Pers == null</c>
        /// it does not.</b> The null case is the pre-D9 behaviour and is pinned so the change is visible:
        /// before this, <c>ctx.Pers</c> was always null and Super-hard's <c>difCapsMult = 0.5</c> never
        /// reached a single stack of caps.
        /// </summary>
        [Test]
        public void Money_ScalesByCapsMultTimesDifCapsMult()
        {
            int Base(FakePers pers)
            {
                var ctx = LootFixture.Context(new StubRng { Value = 0.1f }, new FakeLootPools(), pers: pers);
                Assert.That(LootRoller.RollPlacedItem("money", ctx, kol: 20), Is.True,
                    "control: the money roll itself must succeed");
                return ctx.Results[0].Quantity;
            }

            Assert.That(Base(null), Is.EqualTo(20),
                "Pers == null: no scaling at all — the behaviour D9 replaced");
            Assert.That(Base(new FakePers { CapsMult = 2f }), Is.EqualTo(40), "capsMult only");
            Assert.That(Base(new FakePers { DifCapsMult = 0.5f }), Is.EqualTo(10),
                "Super-hard halves money (Pers.as:834, LootGen.as:247)");
            Assert.That(Base(new FakePers { CapsMult = 2f, DifCapsMult = 0.5f }), Is.EqualTo(20),
                "the two multiply: 20 * 2 * 0.5");
        }

        /// <summary><c>bit</c> carries the same difficulty term, through its own multiplier.</summary>
        [Test]
        public void Bits_ScaleByBitsMultTimesDifCapsMult()
        {
            var ctx = LootFixture.Context(new StubRng { Value = 0.1f }, new FakeLootPools(),
                                          pers: new FakePers { BitsMult = 2f, DifCapsMult = 0.5f });
            Assert.That(LootRoller.RollPlacedItem("bit", ctx, kol: 20), Is.True);
            Assert.That(ctx.Results[0].Quantity, Is.EqualTo(20), "20 * 2 * 0.5 (LootGen.as:251)");
        }

        // ════════════════════════════════════════════════════════════════════════════════════════════
        //  D9 — ctx.Env: the biome gate
        // ════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// A <c>biom</c>-gated arm is taken when the environment matches and skipped when it does not —
        /// <c>trash</c>'s <c>if(biom == 0) newLoot(0.25, food, "radcookie")</c>
        /// (<c>LootGen.as:480-483</c>). Before D9 <c>Env</c> was never set, so every room in every land
        /// rolled as biome 0 and the "home biome" branch was universal.
        /// </summary>
        [Test]
        public void Env_BiomGate_IsTakenWhenTheBiomeMatches()
        {
            var home = LootFixture.Context(new StubRng { Value = 0.1f }, new FakeLootPools(),
                                           env: new FakeEnv { Biom = 0 });
            LootRoller.RollContainer("trash", home);
            Assert.That(Resulted(home, "radcookie"), Is.True, "biom 0 is the home biome");

            var elsewhere = LootFixture.Context(new StubRng { Value = 0.1f }, new FakeLootPools(),
                                                env: new FakeEnv { Biom = 3 });
            LootRoller.RollContainer("trash", elsewhere);
            Assert.That(Resulted(elsewhere, "radcookie"), Is.False,
                "the same roll in another biome must not produce it");
        }

        /// <summary>
        /// <b><c>Env == null</c> still reads as biome 0</b> (<c>LootContext.Biom =&gt; Env?.Biom ?? 0</c>),
        /// which is why a missing adapter is invisible rather than empty. Pinned so the adapter's absence
        /// is a known state, not an unknown one.
        /// </summary>
        [Test]
        public void Env_Null_ReadsAsHomeBiome()
        {
            var ctx = LootFixture.Context(new StubRng { Value = 0.1f }, new FakeLootPools());
            Assert.That(ctx.Env, Is.Null);
            Assert.That(ctx.Biom, Is.EqualTo(0));

            LootRoller.RollContainer("trash", ctx);
            Assert.That(Resulted(ctx, "radcookie"), Is.True,
                "a null Env behaves as biome 0 — the pre-D9 behaviour, recorded rather than assumed");
        }

        /// <summary>
        /// <b><c>LacksWeapon</c> defaults to the conservative answer.</b> With no callback,
        /// <see cref="RoomLootEnvironment"/> answers <b>false</b> ("the player already has it"), which is
        /// what stops <c>specweap</c> handing out a duplicate of a unique weapon
        /// (<c>LootGen.as:741-755</c>). Assuming <c>true</c> would hand out <c>lsword^1</c>,
        /// <c>antidrak^1</c>, <c>quick^1</c> and <c>mlau^1</c> on every roll. This is an assumption, not a
        /// fact, and it is asserted so a future inventory hook changes it deliberately.
        /// </summary>
        [Test]
        public void Env_LacksWeapon_DefaultsToAlreadyOwned()
        {
            var env = new RoomLootEnvironment(biom: 0, itemsTip: string.Empty, actId: "rbl",
                                              landRnd: true, hasProb: false);

            Assert.That(env.LacksWeapon("lsword"), Is.False,
                "no inventory wired: answer 'owned' so specweap cannot duplicate a unique");
            Assert.That(new RoomLootEnvironment(0, null, null, false, false, id => id == "lsword")
                            .LacksWeapon("lsword"), Is.True,
                "control: a supplied callback IS consulted");
        }

        /// <summary>
        /// The adapter takes primitives so the loot namespace does not depend on the map — and a negative
        /// biome (the port's "unknown" sentinel) is read as <b>0</b>, because AS3 has no negative biome
        /// (<c>LandAct.as:30,115-117</c>). Treating the sentinel as a real biome would make every
        /// biome-gated arm fail in a room that simply never had the field.
        /// </summary>
        [Test]
        public void Env_NegativeBiomeSentinel_ReadsAsHome()
        {
            var env = new RoomLootEnvironment(biom: -1, itemsTip: null, actId: null,
                                              landRnd: false, hasProb: false);

            Assert.That(env.Biom, Is.EqualTo(0), "the sentinel is 'no biome', which AS3 reads as 0");
            Assert.That(env.ItemsTip, Is.EqualTo(string.Empty), "null is normalised, so a null-check downstream cannot NRE");
            Assert.That(env.ActId, Is.EqualTo(string.Empty));
            Assert.That(env.LandRnd, Is.False);
            Assert.That(env.HasProb, Is.False);
        }
    }
}
