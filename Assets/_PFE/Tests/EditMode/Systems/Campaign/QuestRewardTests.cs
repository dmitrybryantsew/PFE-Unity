using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using PFE.Data.Definitions.Campaign;
using PFE.Systems.Campaign;

namespace PFE.Tests.EditMode.Systems.Campaign
{
    /// <summary>
    /// Pins the <b>payout</b> half of <c>Quest.close</c> — the part that was registered as
    /// <c>null</c> and therefore paid nothing.
    ///
    /// <para><b>Why these cases and not "a reward arrives".</b> Three separate things can each make a
    /// payout vanish, and each is invisible from the others:</para>
    /// <list type="number">
    /// <item>the <i>parser</i> never read the row, so the definition is empty;</item>
    /// <item>the <i>rules</i> never projected it onto the outcome, so the definition is read and then
    /// dropped — which is what <c>CloseParent</c> did before <c>ParentPayout</c> existed;</item>
    /// <item>the <i>caller</i> built the outcome literal by hand and omitted the field — which is what
    /// <c>IncQuests</c> and <c>CheckQuests</c> did, and those are the two paths a collect quest
    /// (<c>collectHS</c>, the campaign's first quest) actually closes through.</item>
    /// </list>
    /// <para>So there is one fixture per stage, plus an end-to-end one that drives
    /// <c>CheckQuests</c> — the path that matters for the cogwheel — and asserts the host was handed the
    /// rows. Negative controls are present at every level, because "the reward arrived" is also
    /// satisfied by a fixture that hands the host everything unconditionally.</para>
    /// </summary>
    [TestFixture]
    public sealed class QuestRewardTests
    {
        /// <summary>
        /// The authored quest used by the end-to-end case, copied in shape from the real
        /// <c>collectHS</c> (<c>GameData.as:1836</c>) plus the reward shapes that exist in the shipped
        /// data: an item row with a count (<c>money</c> — 21 of the 77 rows), a weapon row
        /// (<c>x37</c> — a weapon id with no item row), and a trigger-only row.
        /// </summary>
        private const string CollectQuestXml = @"
<game>
    <quest id='collectHS' main='1' sp='1' xp='1000' empl='guide'
           trigger='trCogs' triggerset='wait' enddial='dialCogs' begdial='dialBegin'>
        <q id='1' collect='col1' kol='20' del='1'/>
        <reward id='money' kol='1000'/>
        <reward id='x37' kol='4'/>
        <reward trigger='look_cryomine' set='0'/>
    </quest>
</game>";

        private const string BareQuestXml = @"
<game>
    <quest id='noPay'>
        <q id='1' collect='thing' kol='1'/>
    </quest>
</game>";

        // ─────────────────────────────── helpers ───────────────────────────────

        private static QuestFacts Facts(string id, params QuestSubFacts[] subs)
        {
            var facts = new QuestFacts { QuestId = id };
            if (subs != null) facts.Subs.AddRange(subs);
            return facts;
        }

        private static QuestRuntimeState RuntimeOf(QuestFacts facts, QuestState state = QuestState.Active)
            => QuestRuntimeState.CreateFrom(facts, state);

        /// <summary>Records every host call, so "the host was called" and "with what" are separate
        /// assertions.</summary>
        private sealed class RecordingRewardHost : IQuestRewardHost
        {
            public readonly List<(string QuestId, int Xp, int Sp, int Rep)> ScalarCalls = new();
            public readonly List<IReadOnlyList<QuestReward>> ItemCalls = new();
            public readonly List<IReadOnlyList<QuestWithdrawal>> WithdrawCalls = new();

            public void GrantQuestRewards(string questId, int xp, int sp, int reputation)
                => ScalarCalls.Add((questId, xp, sp, reputation));

            public void GrantQuestItems(string questId, IReadOnlyList<QuestReward> rewards)
                => ItemCalls.Add(rewards);

            public void WithdrawQuestItems(string questId, IReadOnlyList<QuestWithdrawal> withdrawals)
                => WithdrawCalls.Add(withdrawals);
        }

        private static CampaignCatalog CatalogWith(params string[] questXml)
        {
            var quests = new List<QuestDefinition>();
            foreach (string xml in questXml)
            {
                quests.AddRange(CampaignDataParser.ParseQuests(xml));
            }

            var catalog = ScriptableObject.CreateInstance<CampaignCatalog>();
            catalog.SetEntries(new List<LandDefinition>(), quests, new List<NpcDefinition>(),
                               new List<CampaignScriptDefinition>());
            return catalog;
        }

        // ═══════════════════════════ A. the parser ═══════════════════════════

        [Test]
        public void ParseQuests_ReadsBothHalvesOfEveryRewardRow()
        {
            QuestDefinition quest = CampaignDataParser.ParseQuests(CollectQuestXml)[0];

            Assert.That(quest.rewards.Count, Is.EqualTo(3),
                "All three <reward> children must survive parsing — the trigger-only row included, or " +
                "the loop that applies them would see a gap where a row is.");

            Assert.That(quest.rewards[0].itemId, Is.EqualTo("money"));
            Assert.That(quest.rewards[0].count, Is.EqualTo(1000));
            Assert.That(quest.rewards[0].trigger, Is.Empty);

            Assert.That(quest.rewards[1].itemId, Is.EqualTo("x37"));
            Assert.That(quest.rewards[1].count, Is.EqualTo(4));

            // The trigger-only row: no id, both trigger halves present.
            Assert.That(quest.rewards[2].itemId, Is.Empty);
            Assert.That(quest.rewards[2].trigger, Is.EqualTo("look_cryomine"));
            Assert.That(quest.rewards[2].set, Is.EqualTo("0"));
        }

        [Test]
        public void ParseQuests_ReadsTheCloseSideScalars()
        {
            QuestDefinition quest = CampaignDataParser.ParseQuests(CollectQuestXml)[0];

            // These four fields existed on QuestFacts since it was written and had no writer, so the
            // trigger write and the closing dialogue were dead on the parent path.
            Assert.That(quest.trigger, Is.EqualTo("trCogs"));
            Assert.That(quest.triggerSet, Is.EqualTo("wait"));
            Assert.That(quest.endDialogueId, Is.EqualTo("dialCogs"));
            Assert.That(quest.beginDialogueId, Is.EqualTo("dialBegin"));
        }

        [Test]
        public void ParseQuests_ReadsStageDelColtipAndResult()
        {
            const string xml = @"
<game>
    <quest id='q'>
        <q id='1' collect='col1' kol='20' del='1' coltip='1'/>
        <q id='2' collect='thing' kol='1'/>
        <q id='3' result='1'/>
    </quest>
</game>";

            QuestDefinition quest = CampaignDataParser.ParseQuests(xml)[0];

            Assert.That(quest.stages[0].deleteOnClose, Is.True);
            Assert.That(quest.stages[0].collectTip, Is.EqualTo(1));

            // Negative control: stage 2 carries no `del`, so a parser that always set the flag — or one
            // that read `del` off the wrong node — fails here.
            Assert.That(quest.stages[1].deleteOnClose, Is.False,
                "A stage without `del` must not be marked for withdrawal.");
            Assert.That(quest.stages[1].collectTip, Is.EqualTo(0));

            Assert.That(quest.stages[2].isResult, Is.True);
            Assert.That(quest.stages[0].isResult, Is.False);
        }

        [Test]
        public void ParseQuests_WithoutRewardChildren_LeavesTheListEmpty()
        {
            QuestDefinition quest = CampaignDataParser.ParseQuests(BareQuestXml)[0];

            Assert.That(quest.rewards, Is.Not.Null, "The list must be usable, not null.");
            Assert.That(quest.rewards, Is.Empty);
            Assert.That(quest.trigger, Is.Empty);
            Assert.That(quest.triggerSet, Is.Empty);
        }

        // ═══════════════════════════ B. the projection ═══════════════════════════

        [Test]
        public void ProjectQuest_CarriesRewardsAndSubFlags()
        {
            QuestDefinition definition = CampaignDataParser.ParseQuests(CollectQuestXml)[0];
            QuestFacts facts = CampaignManager.ProjectQuest(definition);

            Assert.That(facts.Rewards.Count, Is.EqualTo(3));

            // `@set` absent means the literal "1" (Quest.as:679-686); `@kol` absent means 1
            // (:667-674). Both defaults are applied at projection, not at apply, so the rules layer
            // and the host agree on the value.
            Assert.That(facts.Rewards[2].Set, Is.EqualTo("0"));
            Assert.That(facts.Rewards[0].Count, Is.EqualTo(1000));

            Assert.That(facts.Trigger, Is.EqualTo("trCogs"));
            Assert.That(facts.TriggerSet, Is.EqualTo("wait"));
            Assert.That(facts.EndDial, Is.EqualTo("dialCogs"));
            Assert.That(facts.BegDial, Is.EqualTo("dialBegin"));

            Assert.That(facts.Subs[0].DeleteOnClose, Is.True);
            Assert.That(facts.Subs[0].Collect, Is.EqualTo("col1"));
            Assert.That(facts.Subs[0].Kol, Is.EqualTo(20));
        }

        [Test]
        public void ProjectQuest_WithNoAuthoredTriggerSet_DefaultsToOne()
        {
            // Quest.as:208-215 — an absent `@triggerset` becomes the literal "1". An empty string here
            // would make SetTrigger write a trigger with no value, which reads as unset.
            const string xml = "<game><quest id='q' trigger='t'><q id='1'/></quest></game>";
            QuestFacts facts = CampaignManager.ProjectQuest(CampaignDataParser.ParseQuests(xml)[0]);

            Assert.That(facts.TriggerSet, Is.EqualTo("1"));
        }

        // ═══════════════════════════ C. the rules ═══════════════════════════

        [Test]
        public void CloseParent_CarriesTheAuthoredRewardsInOrder()
        {
            QuestFacts facts = Facts("q");
            facts.Rewards.Add(new QuestReward { ItemId = "a", Count = 1, Set = "1" });
            facts.Rewards.Add(new QuestReward { ItemId = "b", Count = 2, Set = "1" });
            facts.Rewards.Add(new QuestReward { Trigger = "t", Set = "9" });

            QuestCloseOutcome outcome = QuestRules.CloseParent(facts, RuntimeOf(facts));

            Assert.That(outcome.Closed, Is.True);
            Assert.That(outcome.Rewards, Is.Not.Null);
            Assert.That(outcome.Rewards.Count, Is.EqualTo(3));
            Assert.That(outcome.Rewards[0].ItemId, Is.EqualTo("a"));
            Assert.That(outcome.Rewards[1].ItemId, Is.EqualTo("b"));
            Assert.That(outcome.Rewards[2].Trigger, Is.EqualTo("t"),
                "Order is the only thing that makes the two halves of a row observable together.");
        }

        [Test]
        public void CloseParent_CollectsDelWithdrawals_FromCollectStagesOnly()
        {
            QuestFacts facts = Facts("q",
                new QuestSubFacts { SubId = "1", Collect = "col1", Kol = 20, DeleteOnClose = true },
                // `del` without `collect` is unreachable authored data — isDel is only set inside
                // `if(this.xml.@collect.length())` (Quest.as:140-156). A stage that carries both is the
                // only shape that withdraws.
                new QuestSubFacts { SubId = "2", Collect = "thing", Kol = 3, DeleteOnClose = false },
                new QuestSubFacts { SubId = "3", Collect = "gun", Kol = 1, DeleteOnClose = true, ColTip = 1 });

            QuestCloseOutcome outcome = QuestRules.CloseParent(facts, RuntimeOf(facts));

            Assert.That(outcome.Withdrawals, Is.Not.Null);
            Assert.That(outcome.Withdrawals.Count, Is.EqualTo(2),
                "Only the two stages with `del` withdraw; the third is a collect-only stage.");
            Assert.That(outcome.Withdrawals[0].Id, Is.EqualTo("col1"));
            Assert.That(outcome.Withdrawals[0].Count, Is.EqualTo(20));
            Assert.That(outcome.Withdrawals[0].ColTip, Is.EqualTo(0));
            Assert.That(outcome.Withdrawals[1].Id, Is.EqualTo("gun"));
            Assert.That(outcome.Withdrawals[1].ColTip, Is.EqualTo(1),
                "coltip=1 routes to remWeapon rather than minusItem (Quest.as:631-641).");
        }

        [Test]
        public void CloseParent_WithNoDelStage_ProducesNoWithdrawals()
        {
            QuestFacts facts = Facts("q", new QuestSubFacts { SubId = "1", Collect = "col1", Kol = 20 });

            QuestCloseOutcome outcome = QuestRules.CloseParent(facts, RuntimeOf(facts));

            Assert.That(outcome.Withdrawals, Is.Empty,
                "Negative control: a quest that collects without `del` must not empty the player's bag.");
        }

        [Test]
        public void CloseParent_WhenAlreadyDone_ProducesNothing()
        {
            QuestFacts facts = Facts("q");
            facts.Rewards.Add(new QuestReward { ItemId = "a", Count = 1 });
            QuestRuntimeState runtime = RuntimeOf(facts, QuestState.Done);

            QuestCloseOutcome outcome = QuestRules.CloseParent(facts, runtime);

            // `if(this.state == 2) return;` (Quest.as:608-611). This is what makes close idempotent, and
            // it is why the two collect-path callers cannot re-close to obtain the payout — see
            // ParentPayout.
            Assert.That(outcome.Closed, Is.False);
            Assert.That(outcome.Rewards, Is.Null);
            Assert.That(outcome.Withdrawals, Is.Null);
        }

        [Test]
        public void ParentPayout_MatchesCloseParent_FieldForField()
        {
            QuestFacts facts = Facts("q",
                new QuestSubFacts { SubId = "1", Collect = "col1", Kol = 20, DeleteOnClose = true });
            facts.Rewards.Add(new QuestReward { ItemId = "money", Count = 1000 });
            facts.Xp = 1000;
            facts.Sp = 1;
            facts.Rep = 5;
            facts.Trigger = "trCogs";
            facts.TriggerSet = "wait";
            facts.EndDial = "dialCogs";
            facts.NextQuestId = "nextQ";

            QuestCloseOutcome viaClose = QuestRules.CloseParent(facts, RuntimeOf(facts));
            QuestCloseOutcome viaProjection = QuestRules.ParentPayout(facts);

            // The invariant that keeps the three callers honest. If a field is ever added to the
            // outcome and wired into only one of these, this fails.
            Assert.That(viaProjection.Closed, Is.EqualTo(viaClose.Closed));
            Assert.That(viaProjection.IsParent, Is.EqualTo(viaClose.IsParent));
            Assert.That(viaProjection.QuestId, Is.EqualTo(viaClose.QuestId));
            Assert.That(viaProjection.Xp, Is.EqualTo(viaClose.Xp));
            Assert.That(viaProjection.Sp, Is.EqualTo(viaClose.Sp));
            Assert.That(viaProjection.Rep, Is.EqualTo(viaClose.Rep));
            Assert.That(viaProjection.Trigger, Is.EqualTo(viaClose.Trigger));
            Assert.That(viaProjection.TriggerSet, Is.EqualTo(viaClose.TriggerSet));
            Assert.That(viaProjection.EndDial, Is.EqualTo(viaClose.EndDial));
            Assert.That(viaProjection.EndScript, Is.EqualTo(viaClose.EndScript));
            Assert.That(viaProjection.NextQuestId, Is.EqualTo(viaClose.NextQuestId));
            Assert.That(viaProjection.Rewards.Count, Is.EqualTo(viaClose.Rewards.Count));
            Assert.That(viaProjection.Withdrawals.Count, Is.EqualTo(viaClose.Withdrawals.Count));
            Assert.That(viaProjection.Withdrawals[0].Id, Is.EqualTo(viaClose.Withdrawals[0].Id));
        }

        [Test]
        public void CloseQuest_ViaASub_CarriesTheParentPayout()
        {
            QuestFacts facts = Facts("q", new QuestSubFacts { SubId = "1", Collect = "col1", Kol = 20 });
            facts.Rewards.Add(new QuestReward { ItemId = "money", Count = 500 });
            facts.Xp = 250;
            QuestRuntimeState runtime = RuntimeOf(facts);

            QuestCloseOutcome outcome = QuestRules.CloseQuest(facts, runtime, "1");

            Assert.That(outcome.Closed, Is.True);
            Assert.That(outcome.IsParent, Is.True, "Closing the last sub auto-closes the parent.");
            Assert.That(outcome.Xp, Is.EqualTo(250));
            Assert.That(outcome.Rewards, Is.Not.Null);
            Assert.That(outcome.Rewards.Count, Is.EqualTo(1),
                "The sub-close merge must carry the reward list through, not just the scalars.");
        }

        // ═══════════════════════════ D. the host ═══════════════════════════

        [Test]
        public void QuestRewardHost_WithNoStats_ReportsThePayout_AndGrantsNothing()
        {
            var reports = new List<string>();
            var host = new QuestRewardHost(stats: () => null, inventory: () => null,
                                           report: reports.Add);

            host.GrantQuestRewards("collectHS", 1000, 1, 0);

            Assert.That(host.XpGranted, Is.Zero);
            Assert.That(host.SkillPointsGranted, Is.Zero);
            Assert.That(reports.Count, Is.EqualTo(1),
                "An unreachable player must be reported by name, or the payout reads as a quest that " +
                "pays nothing.");
            Assert.That(reports[0], Does.Contain("collectHS"));
        }

        [Test]
        public void QuestRewardHost_ReputationOnly_IsReportedNotInvented()
        {
            var reports = new List<string>();
            var host = new QuestRewardHost(stats: () => null, inventory: () => null,
                                           report: reports.Add);

            host.GrantQuestRewards("q", 0, 0, 5);

            // The port has no reputation system (PipBuckBootstrapper.cs:564 records the same absence),
            // so `@rep` — carried by 40 quests — cannot be granted. It must not be silently dropped.
            Assert.That(reports.Count, Is.EqualTo(1));
            Assert.That(reports[0], Does.Contain("reputation"));
        }

        [Test]
        public void QuestRewardHost_WithNoInventory_ReportsTheItemGrant()
        {
            var reports = new List<string>();
            var host = new QuestRewardHost(stats: () => null, inventory: () => null,
                                           report: reports.Add);

            host.GrantQuestItems("q", new List<QuestReward> { new QuestReward { ItemId = "money", Count = 1000 } });

            Assert.That(host.ItemsGranted, Is.Zero);
            Assert.That(reports.Count, Is.EqualTo(1));

            // Case-insensitive on purpose: the report names the TYPE ("no PlayerInventory is
            // reachable"), and the assertion's job is to pin WHICH subsystem is blamed, not how the
            // word is capitalised. It still goes red if the message blames something else.
            Assert.That(reports[0], Does.Contain("inventory").IgnoreCase);
        }

        [Test]
        public void QuestRewardHost_WithNoWithdrawalsToMake_DoesNotReport()
        {
            var reports = new List<string>();
            var host = new QuestRewardHost(stats: () => null, inventory: () => null,
                                           report: reports.Add);

            host.WithdrawQuestItems("q", new List<QuestWithdrawal>());

            Assert.That(reports, Is.Empty,
                "Negative control: an empty withdrawal list is the common case and must be silent.");
        }

        // ═══════════════════════════ E. end-to-end ═══════════════════════════

        [Test]
        public void CheckQuests_PaysTheRewardThroughTheHost_OnTheCollectPath()
        {
            // This is the path `collectHS` actually closes through: a collect quest is finished by
            // `checkQuests`, which closes the parent from inside QuestRules.Check and then projects the
            // payout. The two callers there used to build the outcome literal by hand and dropped the
            // `<reward>` and `del` halves — so this fixture is the regression guard for that.
            var host = new RecordingRewardHost();
            CampaignCatalog catalog = CatalogWith(CollectQuestXml);
            var manager = new CampaignManager(catalog: catalog, questRewards: host);

            manager.AddQuest("collectHS");
            manager.CheckQuests(itemId => itemId == "col1" ? 20 : 0, "col1");

            Assert.That(host.ScalarCalls.Count, Is.EqualTo(1),
                "The quest closed, so the scalars must be paid exactly once.");
            Assert.That(host.ScalarCalls[0].QuestId, Is.EqualTo("collectHS"));
            Assert.That(host.ScalarCalls[0].Xp, Is.EqualTo(1000));
            Assert.That(host.ScalarCalls[0].Sp, Is.EqualTo(1));

            Assert.That(host.ItemCalls.Count, Is.EqualTo(1));
            Assert.That(host.ItemCalls[0].Count, Is.EqualTo(2),
                "Two of the three <reward> rows carry an id; the trigger-only row is applied by " +
                "CampaignManager, not handed to the inventory host.");
            Assert.That(host.ItemCalls[0][0].ItemId, Is.EqualTo("money"));
            Assert.That(host.ItemCalls[0][0].Count, Is.EqualTo(1000));
            Assert.That(host.ItemCalls[0][1].ItemId, Is.EqualTo("x37"));

            Assert.That(host.WithdrawCalls.Count, Is.EqualTo(1),
                "`del='1'` on the cogwheel stage withdraws the 20 collected items (Quest.as:617-630).");
            Assert.That(host.WithdrawCalls[0][0].Id, Is.EqualTo("col1"));
            Assert.That(host.WithdrawCalls[0][0].Count, Is.EqualTo(20));

            // The trigger halves: the parent's own `@trigger` and the reward row's `@trigger`.
            Assert.That(manager.GetTrigger("trCogs").RawString, Is.EqualTo("wait"),
                "The parent `trigger`/`triggerset` pair (Quest.as:657-660) had no writer before.");
            Assert.That(manager.GetTrigger("look_cryomine").RawString, Is.EqualTo("0"),
                "A reward row's `trigger`/`set` pair is applied on the same pass (Quest.as:677-687).");
        }

        [Test]
        public void CheckQuests_WithNoHost_StillClosesTheQuest()
        {
            // Negative control for the wiring: a null host must not prevent the state change, and must
            // not throw. It warns instead — the shape ApplyQuestClose has always had.
            CampaignCatalog catalog = CatalogWith(BareQuestXml);
            var manager = new CampaignManager(catalog: catalog, questRewards: null);

            manager.AddQuest("noPay");
            manager.CheckQuests(itemId => itemId == "thing" ? 1 : 0, "thing");

            Assert.That(manager.TryGetQuestRuntime("noPay", out QuestRuntimeState runtime), Is.True);
            Assert.That(runtime.State, Is.EqualTo((int)QuestState.Done));
        }
    }
}
