using System;
using System.Collections.Generic;
using NUnit.Framework;
using PFE.Core.Rng;
using PFE.Systems.Loot;

namespace PFE.Tests.Editor.Loot
{
    /// <summary>
    /// D5 of <c>docs/DeviceInteractionAndLoot/04_IMPLEMENTATION_PLAN.md</c>: the roll engine — the port of
    /// <c>LootGen.getRandom</c> / <c>newLoot</c> / <c>lootCont</c> / <c>lootDrop</c>.
    ///
    /// <para><b>The first test is the one the plan demands before any other is meaningful:</b> a roller
    /// that only ever returns items passes every positive test and is wrong, so
    /// <see cref="Roll_CanFail_UnknownKey_EmptyPool_AndZeroWeight_AllProduceNothing"/> establishes that
    /// this one can produce nothing — three separate ways — and then a positive control shows it can
    /// produce something.</para>
    ///
    /// <para>Pure: no <c>ScriptableObject</c>, no scene, no Unity API. Runs in the offline wall as well as
    /// in the editor.</para>
    /// </summary>
    [TestFixture]
    public class LootRollerTests
    {
        // =====================================================================
        //  The mandated test: the roller can fail
        // =====================================================================

        /// <summary>
        /// Three independent ways to produce nothing, plus a control.
        ///
        /// <para>(a) <b>An unknown key.</b> AS3's <c>if/else if</c> chain simply falls through and returns
        /// <c>false</c> — silently. That silence is how <c>cont="*"</c> produced an empty container for
        /// years (Q1), so the port must also <b>report</b> the key.</para>
        ///
        /// <para>(b) <b>A known key whose pool is empty.</b> <c>robocell</c> rolls a single
        /// <c>compm</c>; with no <c>compm</c> pool <c>getRandom</c> returns <c>null</c> at <c>:124</c>,
        /// then at <c>:151</c>, then <c>newLoot</c> gives up at <c>:222-226</c>.</para>
        ///
        /// <para>(c) <b>Pool entries whose weights are all zero.</b> This is not hypothetical — it is the
        /// real behaviour of <c>arr["magic"]</c>, which <c>init()</c> fills with literal
        /// <c>chance: 0</c> (<c>LootGen.as:66-76</c>). Two zero-weight entries give a total of 0, so
        /// <c>roll = random * 0 = 0</c> and no entry's running total ever exceeds it.</para>
        /// </summary>
        [Test]
        public void Roll_CanFail_UnknownKey_EmptyPool_AndZeroWeight_AllProduceNothing()
        {
            // (a) unknown key --------------------------------------------------
            var ctxA = LootFixture.Context(new StubRng { Value = 0.5f }, LootFixture.ChestPools());
            bool anyA = LootRoller.RollContainer("no-such-table", ctxA);

            Assert.IsFalse(anyA, "an unmatched key must not claim a loot event");
            Assert.AreEqual(0, ctxA.Results.Count);
            CollectionAssert.Contains(ctxA.UnmatchedKeys, "no-such-table",
                "AS3 falls through silently; the port must surface the key instead (Q1)");

            // (b) known key, pool missing --------------------------------------
            var ctxB = LootFixture.Context(new StubRng { Value = 0.5f }, LootFixture.ChestPools());
            bool anyB = LootRoller.RollContainer("robocell", ctxB);

            Assert.IsFalse(anyB, "robocell rolls one compm; with no compm pool it must produce nothing");
            Assert.AreEqual(0, ctxB.Results.Count);
            Assert.IsTrue(ctxB.UnmatchedKeys.Contains(LootRoller.UnresolvedPrefix + LootTips.Compm),
                "the failed pool lookup must be reported, not swallowed (LootGen.as:222-226)");

            // (c) all-zero weights ---------------------------------------------
            var zeroWeight = new FakeLootPools()
                .Add("a", new LootPoolEntry("ammo_a", 0f), new LootPoolEntry("ammo_b", 0f));
            var ctxC = LootFixture.Context(new StubRng { Value = 0.5f }, zeroWeight);
            bool anyC = LootRoller.RollContainer("ammo", ctxC);

            Assert.IsFalse(anyC, "a zero-weight pool must return null, not an item (LootGen.as:159-167)");
            Assert.AreEqual(0, ctxC.Results.Count);

            // control: the same machinery CAN produce something -----------------
            var ctxD = LootFixture.Context(new StubRng { Value = 0.5f }, LootFixture.ChestPools());
            bool anyD = LootRoller.RollContainer("bomb", ctxD);

            Assert.IsTrue(anyD, "control — bomb rolls three dynamite at chance 1, so it must succeed");
            Assert.AreEqual(3, ctxD.Results.Count);
        }

        // =====================================================================
        //  chest — the M1 table, measured against its authored chances
        // =====================================================================

        /// <summary>
        /// The plan's acceptance check for D5: roll <c>chest</c> 10 000 times with a seeded RNG and assert
        /// every one of its eight entries lands within ±15 % of its authored chance.
        ///
        /// <para>The pool is deliberately one entry per tip, so <c>getRandom</c> takes its
        /// <c>length == 1</c> short-circuit and draws nothing. That isolates the <b>table's</b> chances
        /// from the <b>pool's</b> composition — which is what this test is about. Pool filtering has its
        /// own fixture below.</para>
        /// </summary>
        [Test]
        public void Chest_ObservedFrequencies_MatchAuthoredChances()
        {
            const int N = 10000;
            var rng = new PcgRngService(0xC0FFEEUL, 1UL);
            var pools = LootFixture.ChestPools();

            var authored = new Dictionary<string, float>(StringComparer.Ordinal)
            {
                { "pin", 0.10f },       // LootGen.as:679
                { "weapon", 0.20f },    // :680
                { "bit", 0.20f },       // :681
                { "gem", 0.30f },       // :682
                { "compa", 0.25f },     // :683
                { "book", 0.03f },      // :684
                { "ammo", 0.50f },      // :685
                { "scheme", 0.03f },    // :686
            };
            var observed = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string key in authored.Keys) observed[key] = 0;

            for (int i = 0; i < N; i++)
            {
                var ctx = LootFixture.Context(rng, pools);
                LootRoller.RollContainer("chest", ctx);
                foreach (var roll in ctx.Results)
                {
                    string bucket = Classify(roll.ItemId);
                    Assert.IsTrue(observed.ContainsKey(bucket), "unexpected roll: " + roll);
                    observed[bucket]++;
                }
            }

            // positive control: the roller actually produced things
            int total = 0;
            foreach (var kv in observed) total += kv.Value;
            Assert.Greater(total, 0, "control — a roller that produced nothing would pass every band trivially");

            foreach (var kv in authored)
            {
                float expected = kv.Value * N;
                float got = observed[kv.Key];
                float delta = Math.Abs(got - expected) / expected;
                Assert.LessOrEqual(delta, 0.15f,
                    string.Format("entry '{0}': authored {1:P1}, observed {2:F3} over {3} rolls ({4:P1} off)",
                                  kv.Key, kv.Value, got / N, N, delta));
            }
        }

        private static string Classify(string itemId)
        {
            if (itemId == "pin") return "pin";
            if (itemId == "wpn3") return "weapon";
            if (itemId == "bit") return "bit";
            if (itemId != null && itemId.StartsWith("gem", StringComparison.Ordinal)) return "gem";
            if (itemId == "compa_x") return "compa";
            if (itemId == "book_x") return "book";
            if (itemId == "ammo_x") return "ammo";
            if (itemId == "scheme_x") return "scheme";
            return "?" + itemId;
        }

        // =====================================================================
        //  getRandom — the filters, and the draw it must not take
        // =====================================================================

        /// <summary><c>arr[tip] == null</c> means no item, not an empty one (<c>LootGen.as:124</c>).</summary>
        [Test]
        public void GetRandom_UnknownTip_IsNull()
        {
            var ctx = LootFixture.Context(new StubRng(), new FakeLootPools());
            Assert.IsNull(LootRoller.GetRandom(ctx, "nothing-here"));
        }

        /// <summary>
        /// <b>The <c>length == 1</c> short-circuit must not draw.</b> A single candidate is returned
        /// as-is (<c>LootGen.as:155-158</c>) — so an RNG that records its calls sees none. This matters
        /// because a port that drew anyway would shift every subsequent roll in the stream.
        /// </summary>
        [Test]
        public void GetRandom_SingleCandidate_ReturnsItWithoutDrawing()
        {
            var rng = new StubRng { Value = 0.5f };
            var pools = new FakeLootPools().Add("a", "only_one", 1f);
            var ctx = LootFixture.Context(rng, pools);

            string id = LootRoller.GetRandom(ctx, "a", levelGate: 1f);

            Assert.AreEqual("only_one", id);
            Assert.AreEqual(0, rng.Calls, "the short-circuit at LootGen.as:155-158 returns before Math.random()");
        }

        /// <summary>
        /// <b>An entry with no <c>@stage</c> passes the stage gate; one with a stage above the current
        /// game stage does not.</b> Two candidates — one unstaged, one staged beyond the gate — leave
        /// exactly one, so the unstaged entry is the only reachable one. If the port treated an absent
        /// stage as <c>0</c> instead of "passes", <i>both</i> would be filtered and the pool would yield
        /// nothing at all.
        /// </summary>
        [Test]
        public void GetRandom_AbsentStagePasses_StagedBeyondGameStageDoesNot()
        {
            var pools = new FakeLootPools()
                .Add("a", new LootPoolEntry("unstaged", 1f, stage: null))
                .Add("a", new LootPoolEntry("stage5", 1f, stage: 5));

            var ctx = LootFixture.Context(new StubRng(), pools);
            ctx.GameStage = 3;

            Assert.AreEqual("unstaged", LootRoller.GetRandom(ctx, "a", levelGate: 1f),
                "an absent @stage passes (LootGen.as:138) while stage 5 > gameStage 3 does not");
        }

        /// <summary>
        /// <b>The level gate is a threshold, and an absent <c>@lvl</c> passes.</b> Same shape as the stage
        /// test: a null-level entry survives a tight gate, a level-99 entry does not.
        /// </summary>
        [Test]
        public void GetRandom_AbsentLevelPasses_PresentLevelIsThresholded()
        {
            var pools = new FakeLootPools()
                .Add("a", new LootPoolEntry("nolevel", 1f, level: null))
                .Add("a", new LootPoolEntry("level99", 1f, level: 99));

            var ctx = LootFixture.Context(new StubRng(), pools);

            Assert.AreEqual("nolevel", LootRoller.GetRandom(ctx, "a", levelGate: 1f));
        }

        /// <summary>
        /// <b>The worth gate is an <i>exact match</i>, not a ceiling.</b> <c>param3 == _loc8_.worth</c>
        /// (<c>LootGen.as:138</c>) — so <c>worth=3.5</c> is <b>not</b> reachable when the caller asked for
        /// 3. A port that read it as <c>&lt;=</c> would hand out better weapons than the oracle at every
        /// <c>wbattle</c>/<c>chest</c> roll.
        /// </summary>
        [Test]
        public void GetRandom_WorthGate_IsAnExactMatch()
        {
            var pools = new FakeLootPools()
                .Add("weapon", new LootPoolEntry("w3", 1f, worth: 3f))
                .Add("weapon", new LootPoolEntry("w3_5", 1f, worth: 3.5f))
                .Add("weapon", new LootPoolEntry("w4", 1f, worth: 4f));

            var ctx = LootFixture.Context(new StubRng(), pools);

            Assert.AreEqual("w3", LootRoller.GetRandom(ctx, "weapon", levelGate: 1f, worthGate: 3),
                "3.5 must NOT satisfy a request for 3");
            Assert.AreEqual("w4", LootRoller.GetRandom(ctx, "weapon", levelGate: 1f, worthGate: 4));
        }

        /// <summary>
        /// <b><c>book</c> bypasses the filter entirely.</b> <c>param1 != Item.L_BOOK</c> guards the whole
        /// filtering block (<c>LootGen.as:133</c>), so a book entry staged beyond the game stage is still
        /// reachable. Weighted 1-vs-0 so the draw is deterministic without scripting.
        /// </summary>
        [Test]
        public void GetRandom_BookBypassesTheFilter()
        {
            var pools = new FakeLootPools()
                .Add("book", new LootPoolEntry("common", 0f))
                .Add("book", new LootPoolEntry("stage99", 1f, stage: 99));

            var ctx = LootFixture.Context(new StubRng { Value = 0.5f }, pools);
            ctx.GameStage = 1;

            Assert.AreEqual("stage99", LootRoller.GetRandom(ctx, "book", levelGate: 1f),
                "the book tip skips filtering, so the stage-99 entry is still selectable");
        }

        /// <summary>Zero total weight yields null — the <c>magic</c> pool's real behaviour.</summary>
        [Test]
        public void GetRandom_ZeroTotalWeight_IsNull()
        {
            var pools = new FakeLootPools()
                .Add("magic", new LootPoolEntry("m1", 0f), new LootPoolEntry("m2", 0f));
            var ctx = LootFixture.Context(new StubRng { Value = 0.5f }, pools);

            Assert.IsNull(LootRoller.GetRandom(ctx, "magic"));
        }

        // =====================================================================
        //  newLoot — the chance gate, the broken-container penalties, the perks
        // =====================================================================

        /// <summary>
        /// <b><c>if(!newLoot(a)) newLoot(b)</c> is either/or, not both.</b> <c>metal</c> is the clearest
        /// case: money, or failing that one ammo. The same RNG value decides both halves, so the two runs
        /// below differ in exactly one thing.
        /// </summary>
        [Test]
        public void EitherOr_Metal_GivesAmmoOnlyWhenTheMoneyRollFails()
        {
            var pools = new FakeLootPools().Add("a", "ammo_x", 1f);

            // 0.1 <= 0.5, so the chance gate at LootGen.as:175 passes and money is produced.
            var ctxMoney = LootFixture.Context(new StubRng { Value = 0.1f }, pools);
            LootRoller.RollContainer("metal", ctxMoney);

            Assert.AreEqual(1, ctxMoney.Results.Count, "money succeeded, so the ammo arm must not run");
            Assert.AreEqual("money", ctxMoney.Results[0].ItemId);

            // 0.9 > 0.5, so the money roll fails and the either/or falls through to ammo.
            var ctxAmmo = LootFixture.Context(new StubRng { Value = 0.9f }, pools);
            LootRoller.RollContainer("metal", ctxAmmo);

            Assert.AreEqual(1, ctxAmmo.Results.Count, "money failed, so exactly the ammo arm runs");
            Assert.AreEqual("ammo_x", ctxAmmo.Results[0].ItemId);
        }

        /// <summary>
        /// <b>A broken container halves its money.</b> <c>lootBroken</c> multiplies <c>kol</c> by 0.5
        /// (<c>LootGen.as:253-256</c>) and sets <c>multHP = 0.4</c> (<c>:179-183</c>). Both are asserted
        /// because they travel on the same roll.
        /// </summary>
        [Test]
        public void LootBroken_HalvesMoneyAndSetsHpMultiplier()
        {
            var pools = new FakeLootPools();

            var intact = LootFixture.Context(new StubRng { Value = 0.1f }, pools);
            LootRoller.RollContainer("case", intact);

            var brokenCtx = LootFixture.Context(new StubRng { Value = 0.1f }, pools);
            brokenCtx.LootBroken = true;
            LootRoller.RollContainer("case", brokenCtx);

            Assert.AreEqual(1, intact.Results.Count);
            Assert.AreEqual(1, brokenCtx.Results.Count);

            // case: kol = floor(random * 20 * (0 * 0.11 + 1) + 5) = floor(0.1 * 20 + 5) = 7
            Assert.AreEqual(7, intact.Results[0].Quantity, "unbroken case yields the full stack");
            Assert.AreEqual(3, brokenCtx.Results[0].Quantity, "7 * 0.5 truncates to 3 (LootGen.as:255)");
            Assert.AreEqual(1f, intact.Results[0].HpMultiplier);
            Assert.AreEqual(0.4f, brokenCtx.Results[0].HpMultiplier);
        }

        /// <summary>
        /// <b>A broken container loses roughly half its ammo and explosives outright</b> — the roll is
        /// discarded <i>after</i> the item is built (<c>LootGen.as:257-260</c>), which is why it must not
        /// be modelled as a lowered chance.
        /// </summary>
        [Test]
        public void LootBroken_DiscardsAmmoWhenTheFollowUpRollIsLow()
        {
            var pools = new FakeLootPools().Add("a", "ammo_x", 1f);

            var intact = LootFixture.Context(new StubRng { Value = 0.1f }, pools);
            LootRoller.RollContainer("ammo", intact);

            var brokenCtx = LootFixture.Context(new StubRng { Value = 0.1f }, pools);
            brokenCtx.LootBroken = true;
            LootRoller.RollContainer("ammo", brokenCtx);

            Assert.AreEqual(3, intact.Results.Count,
                "0.1 passes all three ammo chance gates (0.7 / 0.25 / 0.15)");
            Assert.AreEqual(0, brokenCtx.Results.Count,
                "0.1 < 0.5 discards every ammo roll at LootGen.as:257-260");
        }

        /// <summary>
        /// <b><c>freel</c> adds one more roll to the same table.</b> The perk is not a multiplier — it is
        /// an extra <c>newLoot</c> call (<c>LootGen.as:332-335</c>), so the count changes, not the odds.
        /// </summary>
        [Test]
        public void FreelPerk_AddsAnExtraAmmoRoll()
        {
            var pools = new FakeLootPools().Add("a", "ammo_x", 1f);

            var plain = LootFixture.Context(new StubRng { Value = 0.1f }, pools);
            LootRoller.RollContainer("ammo", plain);

            var perky = LootFixture.Context(new StubRng { Value = 0.1f }, pools,
                                            pers: new FakePers { Freel = true });
            LootRoller.RollContainer("ammo", perky);

            Assert.AreEqual(3, plain.Results.Count);
            Assert.AreEqual(4, perky.Results.Count, "the freel arm is a fourth newLoot call");
        }

        /// <summary>
        /// <b><c>barahlo</c> adds five component rolls to <c>instr</c></b> (<c>LootGen.as:452-459</c>).
        /// Counted rather than sampled, because the extra arm is a fixed sequence.
        /// </summary>
        [Test]
        public void BarahloPerk_AddsFiveComponentRolls()
        {
            var pools = new FakeLootPools()
                .Add("compa", "compa_x", 1f).Add("compw", "compw_x", 1f)
                .Add("compe", "compe_x", 1f).Add("compm", "compm_x", 1f)
                .Add("paint", "paint_x", 1f);

            // 0.1 passes every chance gate in instr (0.1 / 0.35 / 0.5 / 0.85 / 0.7).
            var plain = LootFixture.Context(new StubRng { Value = 0.1f }, pools);
            LootRoller.RollContainer("instr", plain);

            var perky = LootFixture.Context(new StubRng { Value = 0.1f }, pools,
                                            pers: new FakePers { Barahlo = true });
            LootRoller.RollContainer("instr", perky);

            Assert.AreEqual(7, plain.Results.Count, "pin + weapon + compa + compw + compe + compm + paint");
            Assert.AreEqual(12, perky.Results.Count, "the barahlo arm adds exactly five more (LootGen.as:454-458)");
        }

        /// <summary>
        /// <b><c>bloat</c> produces no items but does spawn a critter</b> — so <c>lootCont</c> returns
        /// <c>false</c> and the HUD announces "itsEmpty" (<c>Interact.as:2020-2023</c>). This is the
        /// fixture that pins down what <c>is_loot</c> counts: items, not events.
        /// </summary>
        [Test]
        public void BloatContainer_ProducesNoItemsButSpawnsOneUnit()
        {
            var effects = new FakeSideEffects();
            var ctx = LootFixture.Context(new StubRng { Value = 0.1f }, new FakeLootPools(), effects: effects);

            bool any = LootRoller.RollContainer("bloat", ctx, nx: 3f, ny: 4f);

            Assert.IsFalse(any, "no item was produced, so is_loot stays 0");
            Assert.AreEqual(0, ctx.Results.Count);
            CollectionAssert.AreEqual(new[] { "bloat" }, effects.Units);
            Assert.AreEqual(3f, effects.UnitXs[0]);
            Assert.AreEqual(4f, effects.UnitYs[0]);
        }

        /// <summary>
        /// <b><c>replic("full")</c> fires when a chest yields more than five items</b>, and the 50 % gate
        /// at <c>LootGen.as:1073</c> is part of the oracle. With every draw at 0.01 all eight entries
        /// succeed, so the count is 8 &gt; 5 and the gate (0.01 &lt; 0.5) passes.
        /// </summary>
        [Test]
        public void Chest_WithMoreThanFiveItems_RequestsFullReplic()
        {
            var effects = new FakeSideEffects();
            var ctx = LootFixture.Context(new StubRng { Value = 0.01f }, LootFixture.ChestPools(), effects: effects);

            LootRoller.RollContainer("chest", ctx);

            Assert.AreEqual(8, ctx.Results.Count, "0.01 passes all eight chest gates");
            CollectionAssert.AreEqual(new[] { "full" }, effects.ReplicStates);
        }

        /// <summary>
        /// <b><c>replic("empty")</c> fires when a chest yields fewer than two items</b> — and only when the
        /// 50 % gate happens to pass (<c>LootGen.as:1073</c>).
        ///
        /// <para>The script is eleven high values because that is exactly how many draws <c>chest</c> takes
        /// when every gate fails — and the count is asserted, because it is a real property of the
        /// transcription: <c>pin</c>, <c>bit</c> and <c>gem</c> each evaluate a <c>kol</c> expression
        /// <b>before</b> entering <c>newLoot</c>, so their argument draws precede their chance-gate draws
        /// (<c>2 + 1 + 2 + 2 + 1 + 1 + 1 + 1 = 11</c>). The twelfth draw is <c>replic</c>'s own gate,
        /// which the fallback makes low enough to pass.</para>
        /// </summary>
        [Test]
        public void Chest_WithFewerThanTwoItems_RequestsEmptyReplicWhenTheGatePasses()
        {
            var effects = new FakeSideEffects();
            var rng = new ScriptedRng { Fallback = 0.1f }.PushMany(11, 0.99f);
            var ctx = LootFixture.Context(rng, LootFixture.ChestPools(), effects: effects);

            LootRoller.RollContainer("chest", ctx);

            Assert.AreEqual(0, ctx.Results.Count, "0.99 fails every chest chance gate");
            CollectionAssert.AreEqual(new[] { "empty" }, effects.ReplicStates);
            Assert.AreEqual(12, rng.Calls,
                "eleven branch draws then replic's gate — a change here means the RNG order moved");
        }

        /// <summary>
        /// <b>A <c>uniq</c> roll produces the upgraded variant, and the id comes back stripped.</b> The
        /// pool entry id is <c>"lsword^1"</c> (<c>LootGen.as:56</c>); <c>Item</c> turns that into
        /// <c>id = "lsword"</c>, <c>variant = 1</c> (<c>Item.as:132-136</c>), and the quantity is forced
        /// to 1 because <c>uniq</c> maps to <c>weapon</c> (<c>:147-150</c>, <c>:227-230</c>).
        /// </summary>
        [Test]
        public void UniqRoll_StripsTheVariantSuffix_AndIsAQuantityOfOne()
        {
            var pools = new FakeLootPools().Add("uniq", new LootPoolEntry("lsword^1", 1f, worth: 4f));

            // wbattle: the uniq gate is 0.04, so 0.01 passes it and the uniq pool resolves.
            var ctx = LootFixture.Context(new StubRng { Value = 0.01f }, pools);
            LootRoller.RollContainer("wbattle", ctx);

            Assert.GreaterOrEqual(ctx.Results.Count, 1);
            var roll = ctx.Results[0];
            Assert.AreEqual("lsword", roll.ItemId, "the ^1 suffix is a variant, not part of the id");
            Assert.AreEqual(1, roll.Variant);
            Assert.AreEqual(1, roll.Quantity, "a uniq is a weapon, so kol is forced to 1");
        }

        /// <summary>
        /// <b>A weapon's condition is rolled from the <c>kol</c> argument, and it consumes the RNG.</b>
        /// <c>wbattle</c> asks for <c>kol = 1</c>, which means <c>sost = 0.6 + random * 0.25</c>
        /// (<c>Item.as:189-192</c>). A port that skipped this would silently shift every later draw.
        /// </summary>
        [Test]
        public void WeaponRoll_ForcesQuantityOne_AndRollsCondition()
        {
            var pools = new FakeLootPools()
                .Add("weapon", new LootPoolEntry("wpn3", 1f, worth: 3f))
                .Add("a", "ammo_x", 1f);

            // 0.5: the uniq gate (0.04) fails, `Math.min(0, 0.7)` sends us to the worth-3 arm,
            //      then sost = 0.6 + 0.5 * 0.25 = 0.725.
            var ctx = LootFixture.Context(new StubRng { Value = 0.5f }, pools);
            LootRoller.RollContainer("wbattle", ctx);

            Assert.AreEqual(2, ctx.Results.Count, "one weapon plus one ammo");
            var weapon = ctx.Results[0];
            Assert.AreEqual("wpn3", weapon.ItemId);
            Assert.AreEqual(1, weapon.Quantity);
            Assert.AreEqual(0.725f, weapon.Condition, 1e-5f);
        }

        // =====================================================================
        //  The per-run limit (LootGen.as:261-286) — the imp exemption
        // =====================================================================

        /// <summary>
        /// <b>The limit blocks the roll once the run's counter reaches the cap.</b> A table roll always
        /// passes <c>imp = 0</c> — all 267 call sites do — so <c>imp: 0</c> here models exactly that,
        /// and the third roll must be refused and reported.
        /// </summary>
        [Test]
        public void Limit_BlocksTheRollAtTheCap()
        {
            var catalog = new StubCatalog().With(new LootItemRow("limited_x", LootTips.Item, kol: null,
                                                                limitKey: "book"));
            var limits = new FakeLimits();

            for (int i = 0; i < 2; i++)
            {
                var ctx = LootFixture.Context(new StubRng { Value = 0.5f }, new FakeLootPools(), catalog,
                                              limits: limits);
                ctx.LootLimit = 2;
                Assert.IsTrue(LootRoller.RollPlacedItem("limited_x", ctx, kol: 1, imp: 0),
                              "roll " + (i + 1) + " is under the cap of 2 and must succeed");
            }
            Assert.AreEqual(2, limits.Counts["book"], "each successful roll increments the counter");

            var blocked = LootFixture.Context(new StubRng { Value = 0.5f }, new FakeLootPools(), catalog,
                                              limits: limits);
            blocked.LootLimit = 2;
            Assert.IsFalse(LootRoller.RollPlacedItem("limited_x", blocked, kol: 1, imp: 0),
                           "have (2) >= cap (2) refuses the roll at LootGen.as:277-284");
            Assert.AreEqual(0, blocked.Results.Count);
            Assert.AreEqual(2, limits.Counts["book"], "a refused roll must not increment");
            CollectionAssert.Contains(blocked.UnmatchedKeys, "limit:book");
        }

        /// <summary>
        /// <b><c>@maxlim</c> is an absolute cap that overrides the run's limit.</b> It is checked first
        /// (<c>LootGen.as:269-276</c>) and is present on only three rows in <c>AllData</c>.
        /// </summary>
        [Test]
        public void Limit_AbsoluteCapIsCheckedFirst()
        {
            var catalog = new StubCatalog().With(new LootItemRow("capped_x", LootTips.Item, kol: null,
                                                                limitKey: "gem", limitAbsolute: 1));
            var limits = new FakeLimits();

            var first = LootFixture.Context(new StubRng { Value = 0.5f }, new FakeLootPools(), catalog,
                                            limits: limits);
            first.LootLimit = 999;      // the run limit is generous; only @maxlim can bite
            Assert.IsTrue(LootRoller.RollPlacedItem("capped_x", first, kol: 1, imp: 0));

            var second = LootFixture.Context(new StubRng { Value = 0.5f }, new FakeLootPools(), catalog,
                                             limits: limits);
            second.LootLimit = 999;
            Assert.IsFalse(LootRoller.RollPlacedItem("capped_x", second, kol: 1, imp: 0),
                           "@maxlim=1 has been reached, so the run limit is irrelevant");
            CollectionAssert.Contains(second.UnmatchedKeys, "maxlim:gem");
        }

        /// <summary>
        /// <b><c>imp != 0</c> skips the limit check entirely.</b> Placed items carry <c>imp = 1</c> or
        /// <c>2</c> (<c>Interact.as:1987-1995</c>), which is why a container's own authored contents are
        /// never limited.
        /// </summary>
        [Test]
        public void ImportantRoll_SkipsTheLimitEntirely()
        {
            var pools = new FakeLootPools();
            var catalog = new StubCatalog().With(new LootItemRow("book_x", "book", kol: null, limitKey: "book"));
            var limits = new FakeLimits();

            var ctx = LootFixture.Context(new StubRng { Value = 0.1f }, pools, catalog, limits: limits);
            ctx.LootLimit = 0;
            bool ok = LootRoller.RollPlacedItem("book_x", ctx, kol: 1, imp: 2);

            Assert.IsTrue(ok, "imp != 0 must bypass the limit (LootGen.as:261)");
            Assert.AreEqual(0, limits.GetCalls, "the limit store must not even be consulted");
            Assert.AreEqual(1, ctx.Results.Count);
            Assert.IsTrue(ctx.Results[0].Important);
        }

        // =====================================================================
        //  Placed items — Interact.loot()'s first half
        // =====================================================================

        /// <summary>
        /// <b>A container's own <c>&lt;item&gt;</c> children roll before the table, with <c>imp</c> forced
        /// non-zero.</b> The flag that reaches <c>setAct("loot", n)</c> is 2 when any of them was
        /// important, which is how AS3 remembers that the container held something that mattered.
        /// </summary>
        [Test]
        public void PlacedItems_RollFirst_AndReportImportance()
        {
            var effects = new FakeSideEffects();
            var ctx = LootFixture.Context(new StubRng { Value = 0.1f }, new FakeLootPools(), effects: effects);

            var items = new List<LootRoller.PlacedLootItem>
            {
                new LootRoller.PlacedLootItem("gem1", kol: 2),
                new LootRoller.PlacedLootItem("key1", important: true),
            };
            var result = LootRoller.RollPlacedItems(items, ctx);

            Assert.IsTrue(result.AnyPlaced);
            Assert.IsTrue(result.AnyImportant, "the second item carried @imp, so saveLoot must be 2");
            Assert.AreEqual(2, ctx.Results.Count);
            Assert.AreEqual(2, ctx.Results[0].Quantity, "@kol=2 is passed straight through");
            Assert.AreEqual(1, ctx.Results[1].Quantity, "@kol absent means 1 (Interact.as:1981-1986)");
            Assert.IsTrue(ctx.Results[1].Important);
        }

        // =====================================================================
        //  Coverage — the transcription is complete, and provably so
        // =====================================================================

        /// <summary>
        /// <b>The key sets match the oracle's counts.</b> 29 container keys = the 28 <c>else if</c> arms of
        /// <c>lootCont</c> with <c>term</c>/<c>info</c> counted separately; 47 unit keys = the 39 arms of
        /// <c>lootDrop</c> with <c>alicorn1-3</c>, <c>ranger1-3</c>, <c>encl2-4</c> and
        /// <c>vortex/spritebot/roller</c> expanded.
        /// </summary>
        [Test]
        public void KeySets_MatchTheOracleCounts()
        {
            Assert.AreEqual(29, LootRoller.KnownContainerKeys.Count);
            Assert.AreEqual(47, LootRoller.KnownUnitKeys.Count);
        }

        /// <summary>
        /// <b>Every known key is actually dispatched.</b> Rolling each one must not report the key itself
        /// as unmatched — the <c>default:</c> arm is the only thing that can add a bare key, so a
        /// <b>deleted</b> <c>case</c> label shows up here. The pools are deliberately empty, so
        /// <c>unresolved:</c> entries are expected and ignored; only the bare key matters.
        ///
        /// <para>Note this proves a <i>label</i> exists, not that the arm <b>does</b> anything — a gutted
        /// arm still matches. <see cref="EveryKnownKey_ProducesAnObservableEffect"/> is the test for
        /// that, and the two are not interchangeable.</para>
        /// </summary>
        [Test]
        public void EveryKnownKey_IsDispatched()
        {
            var pools = new FakeLootPools();

            foreach (string key in LootRoller.KnownContainerKeys)
            {
                var ctx = LootFixture.Context(new StubRng { Value = 0.5f }, pools);
                LootRoller.RollContainer(key, ctx);
                CollectionAssert.DoesNotContain(ctx.UnmatchedKeys, key,
                    "container key '" + key + "' has no case arm");
            }

            foreach (string key in LootRoller.KnownUnitKeys)
            {
                var ctx = LootFixture.Context(new StubRng { Value = 0.5f }, pools);
                LootRoller.RollUnitDrop(key, ctx);
                CollectionAssert.DoesNotContain(ctx.UnmatchedKeys, key,
                    "unit key '" + key + "' has no case arm");
            }
        }

        /// <summary>
        /// <b>Every known key produces at least one observable effect</b> — a roll, a critter spawn, or a
        /// replic request — when its chance gates can pass and its pools resolve.
        ///
        /// <para>This is the test that catches a <b>gutted</b> arm. Replacing
        /// <c>case "cryo": ContCryo(s); break;</c> with <c>case "cryo": break;</c> leaves
        /// <see cref="EveryKnownKey_IsDispatched"/> green — the label is still there — while this one goes
        /// red. That is the "a rule that can never go red" failure mode, and it is why both tests exist.</para>
        ///
        /// <para>The three effect kinds are counted together because the oracle uses all three: a container
        /// can hand out items, spawn a critter instead (<c>bloat</c>, <c>trash</c>, <c>fridge</c>), or
        /// simply ask for the pip-boy animation (<c>chest</c>, <c>safe</c>). A test that only counted
        /// items would call <c>bloat</c> broken.</para>
        ///
        /// <para><b>Known gap:</b> this pins <i>that</i> each key acts, not <i>what</i> it produces. A
        /// per-key golden item list would be stronger and is recorded as follow-up work in
        /// <c>04_IMPLEMENTATION_PLAN.md</c>.</para>
        /// </summary>
        [Test]
        public void EveryKnownKey_ProducesAnObservableEffect()
        {
            foreach (string key in LootRoller.KnownContainerKeys)
            {
                AssertKeyHasAnEffect("container '" + key + "'", key,
                                     (k, ctx) => LootRoller.RollContainer(k, ctx));
            }

            foreach (string key in LootRoller.KnownUnitKeys)
            {
                AssertKeyHasAnEffect("unit '" + key + "'", key,
                                     (k, ctx) => LootRoller.RollUnitDrop(k, ctx));
            }
        }

        private static void AssertKeyHasAnEffect(string label, string key, Action<string, LootContext> roll)
        {
            var effects = new FakeSideEffects();
            // 0.01 passes every chance gate in both tables, takes the low arm of every random branch and
            // leaves every loop at its minimum bound.
            var ctx = LootFixture.Context(new StubRng { Value = 0.01f }, new UniversalPools(), effects: effects);
            ctx.Hero = 1;   // so the hero-gated arms (hellhound1, zombie7-9, robobrain, ...) are reachable

            roll(key, ctx);

            int observable = ctx.Results.Count + effects.Units.Count + effects.ReplicStates.Count;
            Assert.Greater(observable, 0,
                label + " produced no rolls, no critter and no replic — its arm does nothing");
        }

        /// <summary>
        /// <b>An unknown key on either path is reported and rolls nothing.</b> The unit path is checked
        /// separately because it has its own dispatcher.
        /// </summary>
        [Test]
        public void UnknownKeys_AreReportedOnBothPaths()
        {
            var ctxCont = LootFixture.Context(new StubRng(), new FakeLootPools());
            Assert.IsFalse(LootRoller.RollContainer("bogus", ctxCont));
            CollectionAssert.Contains(ctxCont.UnmatchedKeys, "bogus");

            var ctxDrop = LootFixture.Context(new StubRng(), new FakeLootPools());
            Assert.IsFalse(LootRoller.RollUnitDrop("bogus", ctxDrop));
            CollectionAssert.Contains(ctxDrop.UnmatchedKeys, "bogus");
        }

        /// <summary>
        /// <b>A roll cannot run without an RNG or a pool set</b> — both are required inputs, and a
        /// silently-missing one would make the whole engine non-deterministic or empty. Failing loudly is
        /// the point.
        /// </summary>
        [Test]
        public void MissingInputs_ThrowRatherThanDegrade()
        {
            Assert.Throws<ArgumentNullException>(() => LootRoller.RollContainer("chest", null));

            var noRng = new LootContext { Pools = new FakeLootPools() };
            Assert.Throws<InvalidOperationException>(() => LootRoller.RollContainer("chest", noRng));

            var noPools = new LootContext { Rng = new StubRng() };
            Assert.Throws<InvalidOperationException>(() => LootRoller.RollContainer("chest", noPools));
        }

        /// <summary>
        /// <b>A missing catalog is reported, not hidden.</b> The roll still works — the oracle tolerates an
        /// unknown id — but every stack size falls back to the caller's value, so the configuration bug
        /// must be visible.
        /// </summary>
        [Test]
        public void MissingCatalog_IsReported()
        {
            var ctx = LootFixture.Context(new StubRng { Value = 0.5f }, LootFixture.ChestPools());
            ctx.Catalog = null;

            LootRoller.RollContainer("bomb", ctx);

            CollectionAssert.Contains(ctx.UnmatchedKeys, "no-catalog:bomb");
        }
    }
}
