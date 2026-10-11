using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using PFE.Data.Definitions.Campaign;

namespace PFE.Tests.Editor.Campaign
{
    /// <summary>
    /// Unit and parity tests for Campaign Lands, Quests, NPCs, and Scripts data models and XML parsers.
    /// Validates parsing against the AS3 GameData.as schema and verifies CampaignCatalog lookup mechanisms.
    /// </summary>
    [TestFixture]
    public class CampaignDataTests
    {
        private const string SampleLandXml = @"
<game>
    <land id='test_begin' tip='story' rnd='0' stage='1' dif='2' biom='3' conf='4' file='rooms_begin' exit='surf' mx='5' my='3' locx='1' locy='2'>
        <options backwall='brick' fon='fonWay' music='music_begin' postmusic='1' darkness='-30' color='fire' border='S' vis='12' wtip='1' wrad='50' wdam='5' wtipdam='2' xp='250'/>
    </land>
    <land id='test_rnd' tip='rnd' rnd='1' stage='3' dif='5' biom='2' conf='1' file='rooms_plant' mx='10' my='8' locx='0' locy='0'>
        <options backwall='metal' fon='fonCanter' music='music_plant' darkness='0' xp='500'/>
    </land>
</game>";

        private const string SampleQuestXml = @"
<game>
    <quest id='toExit' main='1' empl='guide' xp='500' sp='1' pay='100' rep='5'>
        <q id='1' invis='0' nn='0' collect='key_card' kol='2' give='terminal'/>
        <q id='2' invis='1' nn='1'/>
        <next id='storyContact'/>
    </quest>
    <quest id='sideBounty' main='0' empl='sheriff' xp='200' sp='0' pay='50' rep='1'>
        <q id='1' invis='0' nn='0' collect='badge' kol='1'/>
    </quest>
</game>";

        private const string SampleNpcAndScriptXml = @"
<game>
    <npc id='calam' vis='Calam' inter='travel' ico='10' weap='sniper' silent='0'>
        <dial id='dial1' imp='1' trigger='rbl_visited' land='rbl' quest='storyContact' sub='1' prev='dial0'>
            <scr>
                <s act='openland' val='rbl'/>
                <s act='stage' val='storyContact' n='2' t='1.5'/>
            </scr>
        </dial>
    </npc>
    <scr id='tamePhoenix'>
        <s act='dialog' val='dialTamePhoenix'/>
        <s targ='this' act='tame'/>
        <s act='stage' val='tamePhoenix' n='2'/>
        <s t='3' act='take' val='whistle' n='1'/>
    </scr>
</game>";

        [Test]
        public void LandParsing_ExtractsAttributesAndGridBounds()
        {
            var lands = CampaignDataParser.ParseLands(SampleLandXml);
            Assert.AreEqual(2, lands.Count, "Should parse 2 lands");

            var begin = lands[0];
            Assert.AreEqual("test_begin", begin.landId);
            Assert.AreEqual("Test Begin", begin.landName);
            Assert.AreEqual("story", begin.tip);
            Assert.IsFalse(begin.isProcedural);
            Assert.AreEqual(1, begin.stage);
            Assert.AreEqual(2, begin.baseDifficulty);
            Assert.AreEqual(3, begin.biomeId);
            Assert.AreEqual(4, begin.configId);
            Assert.AreEqual("rooms_begin", begin.sourceFileKey);
            Assert.AreEqual("surf", begin.exitLandId);
            Assert.AreEqual(5, begin.gridWidth);
            Assert.AreEqual(3, begin.gridHeight);
            Assert.AreEqual(new Vector2Int(1, 2), begin.entryCoordinates);

            // Options
            Assert.AreEqual("brick", begin.backwallTexture);
            Assert.AreEqual("fonWay", begin.backdropId);
            Assert.AreEqual("music_begin", begin.musicTrackId);
            Assert.IsTrue(begin.postMusic);
            Assert.AreEqual(-30f, begin.ambientDarkness);
            Assert.AreEqual("fire", begin.colorGrade);
            Assert.AreEqual("S", begin.borderStyle);
            Assert.AreEqual(12, begin.visibilityRange);
            Assert.AreEqual(1, begin.hazardType);
            Assert.AreEqual(50f, begin.hazardRadius);
            Assert.AreEqual(5f, begin.hazardDamage);
            Assert.AreEqual(2, begin.hazardDamageType);
            Assert.AreEqual(250, begin.xpReward);

            var rndLand = lands[1];
            Assert.AreEqual("test_rnd", rndLand.landId);
            Assert.IsTrue(rndLand.isProcedural);
            Assert.AreEqual(10, rndLand.gridWidth);
            Assert.AreEqual(8, rndLand.gridHeight);
            Assert.AreEqual(500, rndLand.xpReward);
        }

        [Test]
        public void QuestParsing_ExtractsStagesAndNextQuest()
        {
            var quests = CampaignDataParser.ParseQuests(SampleQuestXml);
            Assert.AreEqual(2, quests.Count, "Should parse 2 quests");

            var mainQuest = quests[0];
            Assert.AreEqual("toExit", mainQuest.questId);
            Assert.IsTrue(mainQuest.isMainQuest);
            Assert.AreEqual("guide", mainQuest.employer);
            Assert.AreEqual(500, mainQuest.rewardXp);
            Assert.AreEqual(1, mainQuest.rewardSp);
            Assert.AreEqual(100, mainQuest.rewardPay);
            Assert.AreEqual(5, mainQuest.rewardReputation);
            Assert.AreEqual("storyContact", mainQuest.nextQuestId);

            Assert.AreEqual(2, mainQuest.stages.Count);
            var stage1 = mainQuest.stages[0];
            Assert.AreEqual(1, stage1.stageIndex);
            Assert.IsFalse(stage1.isHidden);
            Assert.IsFalse(stage1.isOptional);
            Assert.AreEqual("key_card", stage1.requiredItemId);
            Assert.AreEqual(2, stage1.requiredItemCount);
            Assert.AreEqual("terminal", stage1.turnInItemId);

            var stage2 = mainQuest.stages[1];
            Assert.AreEqual(2, stage2.stageIndex);
            Assert.IsTrue(stage2.isHidden);
            Assert.IsTrue(stage2.isOptional);

            var sideQuest = quests[1];
            Assert.AreEqual("sideBounty", sideQuest.questId);
            Assert.IsFalse(sideQuest.isMainQuest);
            Assert.AreEqual(string.Empty, sideQuest.nextQuestId);
            Assert.AreEqual(1, sideQuest.stages.Count);
        }

        [Test]
        public void NpcAndScriptParsing_ExtractsDialoguesAndActions()
        {
            var (npcs, scripts) = CampaignDataParser.ParseNpcsAndScripts(SampleNpcAndScriptXml);

            Assert.AreEqual(1, npcs.Count, "Should parse 1 NPC");
            var npc = npcs[0];
            Assert.AreEqual("calam", npc.npcId);
            Assert.AreEqual("Calam", npc.visualId);
            Assert.AreEqual("travel", npc.interactionType);
            Assert.AreEqual(10, npc.iconIndex);
            Assert.AreEqual("sniper", npc.defaultWeapon);
            Assert.IsFalse(npc.isSilent);

            Assert.AreEqual(1, npc.dialogues.Count);
            var dial = npc.dialogues[0];
            Assert.AreEqual("dial1", dial.dialogueId);
            Assert.IsTrue(dial.isImportant);
            Assert.AreEqual("rbl_visited", dial.requiredTrigger);
            Assert.AreEqual("rbl", dial.requiredLand);
            Assert.AreEqual("storyContact", dial.requiredQuest);
            Assert.AreEqual(1, dial.requiredQuestStage);
            Assert.AreEqual("dial0", dial.previousDialogueId);

            Assert.AreEqual(2, dial.actions.Count);
            Assert.AreEqual("openland", dial.actions[0].action);
            Assert.AreEqual("rbl", dial.actions[0].value);
            Assert.AreEqual("stage", dial.actions[1].action);
            Assert.AreEqual("storyContact", dial.actions[1].value);
            Assert.AreEqual(2, dial.actions[1].numericParam);
            Assert.AreEqual(1.5f, dial.actions[1].delaySeconds);

            Assert.AreEqual(1, scripts.Count, "Should parse 1 Script");
            var scr = scripts[0];
            Assert.AreEqual("tamePhoenix", scr.scriptId);
            Assert.AreEqual(4, scr.actions.Count);
            Assert.AreEqual("dialog", scr.actions[0].action);
            Assert.AreEqual("dialTamePhoenix", scr.actions[0].value);
            Assert.AreEqual("tame", scr.actions[1].action);
            Assert.AreEqual("this", scr.actions[1].target);
            Assert.AreEqual("take", scr.actions[3].action);
            Assert.AreEqual("whistle", scr.actions[3].value);
            Assert.AreEqual(1, scr.actions[3].numericParam);
            Assert.AreEqual(3f, scr.actions[3].delaySeconds);
        }

        [Test]
        public void CampaignCatalog_InitializesAndResolvesEntities()
        {
            var lands = CampaignDataParser.ParseLands(SampleLandXml);
            var quests = CampaignDataParser.ParseQuests(SampleQuestXml);
            var (npcs, scripts) = CampaignDataParser.ParseNpcsAndScripts(SampleNpcAndScriptXml);

            var catalog = ScriptableObject.CreateInstance<CampaignCatalog>();
            catalog.startingLandId = "test_begin";
            catalog.playerHubLandId = "test_begin";
            catalog.SetEntries(lands, quests, npcs, scripts);

            // Land lookups
            Assert.IsTrue(catalog.HasLand("test_begin"));
            Assert.IsTrue(catalog.HasLand("TEST_BEGIN"), "Catalog lookup must be case-insensitive");
            Assert.IsFalse(catalog.HasLand("unknown_land"));
            Assert.IsNotNull(catalog.GetLand("test_begin"));
            Assert.AreEqual("Test Begin", catalog.GetLand("test_begin").DisplayName);

            // Quest lookups
            Assert.IsTrue(catalog.HasQuest("toExit"));
            Assert.IsTrue(catalog.HasQuest("TOEXIT"), "Quest lookup must be case-insensitive");
            Assert.IsFalse(catalog.HasQuest("unknown_quest"));
            Assert.IsNotNull(catalog.GetQuest("toExit"));

            // NPC lookups
            Assert.IsTrue(catalog.HasNpc("calam"));
            Assert.IsNotNull(catalog.GetNpc("calam"));

            // Script lookups
            Assert.IsTrue(catalog.HasScript("tamePhoenix"));
            Assert.IsNotNull(catalog.GetScript("tamePhoenix"));
        }

        /// <summary>
        /// Reads the oracle's <c>GameData.as</c> if any known copy is on this machine, else null.
        ///
        /// <para>Split out of the two tests that need it so both probe the same list: a second, drifting
        /// probe list is how one of them ends up skipping silently while the other runs.</para>
        /// </summary>
        private static string TryReadRealGameData()
        {
            string[] probePaths = new[]
            {
                // The canonical oracle, first. The two below are byte-identical copies (md5
                // 300844e12816dbcb1bad8b232ad9faa8 at the time of writing), kept so this probe still
                // resolves on a machine that holds only one of them — but the canonical path is the one
                // that must survive, so it is probed first.
                @"E:\Games\UnityGames\pfeToUnity\pfe\scripts\fe\GameData.as",
                @"C:\Users\User\Documents\rustProjects\pfeToUnity\pfe\scripts\fe\GameData.as",
                @"E:\Games\UnityGames\backup from ralph\New folder\pfeToUnity\pfe\scripts\fe\GameData.as"
            };

            foreach (var p in probePaths)
            {
                if (File.Exists(p))
                {
                    return File.ReadAllText(p);
                }
            }

            return null;
        }

        [Test]
        public void RealGameData_ParsesIfAvailable()
        {
            string realContent = TryReadRealGameData();

            if (string.IsNullOrEmpty(realContent))
            {
                Assert.Pass("Skipping RealGameData test as GameData.as was not found on local dev disk.");
                return;
            }

            // Real Lands
            var lands = CampaignDataParser.ParseLands(realContent);
            Assert.GreaterOrEqual(lands.Count, 29, $"Expected at least 29 lands from GameData.as, got {lands.Count}");

            var beginLand = lands.Find(l => l.landId == "begin");
            Assert.IsNotNull(beginLand, "Prologue land 'begin' must exist in GameData.as");
            Assert.AreEqual("rooms_begin", beginLand.sourceFileKey);

            var rblLand = lands.Find(l => l.landId == "rbl");
            Assert.IsNotNull(rblLand, "Hub land 'rbl' must exist in GameData.as");
            Assert.AreEqual("rooms_rbl", rblLand.sourceFileKey);

            // Real Quests
            var quests = CampaignDataParser.ParseQuests(realContent);
            Assert.GreaterOrEqual(quests.Count, 50, $"Expected at least 50 quests from GameData.as, got {quests.Count}");

            var toExitQuest = quests.Find(q => q.questId == "toExit");
            Assert.IsNotNull(toExitQuest, "First quest 'toExit' must exist in GameData.as");
            Assert.Greater(toExitQuest.stages.Count, 0, "toExit quest must have stages");

            // Real NPCs & Scripts
            var (npcs, scripts) = CampaignDataParser.ParseNpcsAndScripts(realContent);
            Assert.GreaterOrEqual(npcs.Count, 50, $"Expected at least 50 NPCs from GameData.as, got {npcs.Count}");
            Assert.GreaterOrEqual(scripts.Count, 70, $"Expected at least 70 Scripts from GameData.as, got {scripts.Count}");

            var calamNpc = npcs.Find(n => n.npcId == "calam");
            Assert.IsNotNull(calamNpc, "NPC 'calam' must exist");
            Assert.Greater(calamNpc.dialogues.Count, 0, "Calam must have dialogues");
        }

        /// <summary>
        /// The <c>&lt;prob&gt;</c> children that <c>ParseLands</c> used to skip entirely.
        ///
        /// <para><b>Every number below is a count of the oracle, not of the parser's output.</b> They were
        /// taken from <c>GameData.as</c> independently, so a parser that drops an attribute or mis-files a
        /// child fails here instead of agreeing with itself. <c>CampaignDataParser</c> selected only
        /// <c>//land</c>, <c>//quest</c>, <c>//npc</c> and <c>//scr</c>, so before this the count was 0 for
        /// every one of them and no door could be placed.</para>
        ///
        /// <para>Editor-only: <c>ParseLands</c> calls <c>ScriptableObject.CreateInstance</c>, which raises
        /// <c>ECall</c> in a plain shell. The parsing <em>rules</em> are covered offline by
        /// <c>ProbParsingTests</c>; this case covers the real data.</para>
        /// </summary>
        [Test]
        public void RealGameData_DeclaresTheProbRoomsThatWereNeverRead()
        {
            string realContent = TryReadRealGameData();
            if (string.IsNullOrEmpty(realContent))
            {
                Assert.Pass("Skipping: GameData.as was not found on local dev disk.");
                return;
            }

            var lands = CampaignDataParser.ParseLands(realContent);

            var probLands = lands.FindAll(l => l.probRooms != null && l.probRooms.Count > 0);

            CollectionAssert.AreEquivalent(
                new[]
                {
                    "rbl", "random_plant", "random_sewer", "random_stable",
                    "random_mane", "random_canter", "random_encl",
                },
                probLands.ConvertAll(l => l.landId),
                "Seven lands declare <prob> children. A land missing here means its <prob> block was not " +
                "walked; an extra one means a land picked up another land's children.");

            // Per-land counts, so a land that read the wrong sibling's block is caught.
            Assert.AreEqual(7, CountProbs(lands, "rbl"));
            Assert.AreEqual(14, CountProbs(lands, "random_plant"));
            Assert.AreEqual(6, CountProbs(lands, "random_sewer"));
            Assert.AreEqual(14, CountProbs(lands, "random_stable"));
            Assert.AreEqual(10, CountProbs(lands, "random_mane"));
            Assert.AreEqual(9, CountProbs(lands, "random_canter"));
            Assert.AreEqual(6, CountProbs(lands, "random_encl"));

            int total = 0, imps = 0, bossTips = 0, probTips = 0, absentLevel = 0, zeroLevel = 0;
            var impIds = new List<string>();
            foreach (var land in probLands)
            {
                foreach (var prob in land.probRooms)
                {
                    total++;
                    if (prob.imp) { imps++; impIds.Add(prob.id); }
                    if (prob.tip == "2") bossTips++;
                    else if (prob.tip == "1") probTips++;
                    if (!prob.hasLevel) absentLevel++;
                    else if (prob.level == 0) zeroLevel++;
                }
            }

            Assert.AreEqual(66, total, "66 <prob> elements in GameData.as.");
            Assert.AreEqual(2, imps, "Exactly two carry `imp`.");
            CollectionAssert.AreEquivalent(new[] { "camp", "griff" }, impIds,
                "…and they are `camp` (random_sewer) and `griff` (random_mane).");
            Assert.AreEqual(34, bossTips, "34 are tip='2' — a doorboss.");
            Assert.AreEqual(31, probTips, "31 are tip='1' — a doorprob.");
            Assert.AreEqual(8, absentLevel,
                "8 have no `level` attribute at all. If this reads 0 the parser is collapsing 'absent' " +
                "into 'level=0' and newRandomProb's `.length == 0` test cannot be reproduced.");
            Assert.AreEqual(23, zeroLevel, "23 carry level='0' explicitly — a different answer from absent.");

            // The detached collection itself.
            var probLand = lands.Find(l => l.landId == "prob");
            Assert.IsNotNull(probLand, "The `prob` land holds every prob room (Land.as:783).");
            Assert.IsTrue(probLand.isProbLand,
                "AS3 `prob='1'` sends this land to game.probs rather than game.lands (Game.as:75-82). " +
                "Before the parser read the attribute this was false.");
            Assert.AreEqual("rooms_prob", probLand.sourceFileKey);
            Assert.AreEqual(0, probLand.probRooms.Count, "the `prob` land declares no <prob> of its own");
        }

        private static int CountProbs(List<LandDefinition> lands, string landId)
        {
            var land = lands.Find(l => l.landId == landId);
            return land?.probRooms?.Count ?? 0;
        }
    }
}
