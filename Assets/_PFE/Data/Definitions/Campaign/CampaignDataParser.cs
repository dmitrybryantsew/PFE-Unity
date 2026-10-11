using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using UnityEngine;

namespace PFE.Data.Definitions.Campaign
{
    /// <summary>
    /// Pure C# XML parser for Campaign Lands, Quests, NPCs, and Scripts from GameData.as.
    /// Decoupled from UnityEditor AssetDatabase so that it can be executed both by
    /// editor importers and headless/EditMode unit tests in PFE.Tests.
    /// </summary>
    public static class CampaignDataParser
    {
        public static List<LandDefinition> ParseLands(string content)
        {
            var results = new List<LandDefinition>();
            if (string.IsNullOrEmpty(content)) return results;

            XmlDocument doc = ExtractGameXmlDocument(content);
            if (doc == null) return results;

            XmlNodeList landNodes = doc.SelectNodes("//land | //land1");
            if (landNodes == null) return results;

            foreach (XmlNode node in landNodes)
            {
                string id = node.Attributes?["id"]?.Value?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(id)) continue;

                bool isTest = node.Attributes?["test"]?.Value == "1" || node.Name == "land1";

                var landDef = ScriptableObject.CreateInstance<LandDefinition>();
                landDef.landId = id;
                landDef.landName = FormatTitle(id);
                landDef.tip = node.Attributes?["tip"]?.Value?.Trim() ?? (isTest ? "test" : "story");

                // AS3 `prob='1'` (Game.as:75-82, LandAct.as:48/147-149): the land is a *detached room
                // collection*, not a playable level. `LandAct.prob` is an int defaulting to 0 and is
                // assigned only when the attribute is present, so the test is a value test here — unlike
                // the `<prob>` element's own imp/prize/close, which are presence tests.
                //
                // Exactly one land carries it (`id='prob'`, `file='rooms_prob'`). It is the collection
                // `Land.buildProb` looks up by name (Land.as:783-804), so reading it is what makes the
                // prob rooms reachable at all.
                landDef.isProbLand = node.Attributes?["prob"]?.Value == "1";

                landDef.isProcedural = node.Attributes?["rnd"]?.Value == "1";
                landDef.stage = ParseInt(node.Attributes?["stage"]?.Value, 1);
                landDef.baseDifficulty = ParseInt(node.Attributes?["dif"]?.Value, 0);
                landDef.autoLevel = node.Attributes?["autolevel"]?.Value == "1";
                landDef.biomeId = ParseInt(node.Attributes?["biom"]?.Value, 0);
                landDef.configId = ParseInt(node.Attributes?["conf"]?.Value, 0);
                landDef.sourceFileKey = node.Attributes?["file"]?.Value?.Trim() ?? string.Empty;
                landDef.exitLandId = node.Attributes?["exit"]?.Value?.Trim() ?? string.Empty;
                landDef.listCount = ParseInt(node.Attributes?["list"]?.Value, 0);
                landDef.loadScreenIndex = ParseInt(node.Attributes?["loadscr"]?.Value, -1);
                landDef.limit = ParseInt(node.Attributes?["limit"]?.Value, 0);
                landDef.fin = ParseInt(node.Attributes?["fin"]?.Value, 0);

                landDef.gridWidth = ParseInt(node.Attributes?["mx"]?.Value, 1);
                landDef.gridHeight = ParseInt(node.Attributes?["my"]?.Value, 1);
                landDef.entryCoordinates = new Vector2Int(
                    ParseInt(node.Attributes?["locx"]?.Value, 0),
                    ParseInt(node.Attributes?["locy"]?.Value, 0)
                );

                XmlNode optNode = node.SelectSingleNode("./options");
                if (optNode != null && optNode.Attributes != null)
                {
                    landDef.backwallTexture = optNode.Attributes["backwall"]?.Value ?? string.Empty;
                    landDef.backdropId = optNode.Attributes["fon"]?.Value ?? string.Empty;
                    landDef.musicTrackId = optNode.Attributes["music"]?.Value ?? string.Empty;
                    landDef.postMusic = optNode.Attributes["postmusic"]?.Value == "1";
                    landDef.ambientDarkness = ParseFloat(optNode.Attributes["darkness"]?.Value, 0f);
                    landDef.colorGrade = optNode.Attributes["color"]?.Value ?? string.Empty;
                    landDef.borderStyle = optNode.Attributes["border"]?.Value ?? string.Empty;
                    landDef.visibilityRange = ParseInt(optNode.Attributes["vis"]?.Value, 0);
                    landDef.hazardType = ParseInt(optNode.Attributes["wtip"]?.Value, 0);
                    landDef.hazardRadius = ParseFloat(optNode.Attributes["wrad"]?.Value, 0f);
                    landDef.hazardDamage = ParseFloat(optNode.Attributes["wdam"]?.Value, 0f);
                    landDef.hazardDamageType = ParseInt(optNode.Attributes["wtipdam"]?.Value, 0);
                    landDef.xpReward = ParseInt(optNode.Attributes["xp"]?.Value, 0);
                }

                // AS3 <prob id level imp tip prize close> children of <land> (Land.as:809-863). These are
                // the bonus/boss rooms a doorprob/doorboss opens into. Nothing read them before — this
                // parser selected only //land, //quest, //npc and //scr — so the doors were unbuildable.
                XmlNodeList probNodes = node.SelectNodes("./prob");
                if (probNodes != null)
                {
                    foreach (XmlNode probNode in probNodes)
                    {
                        landDef.probRooms.Add(ParseProbRoom(probNode));
                    }
                }

                results.Add(landDef);
            }

            return results;
        }

        public static List<QuestDefinition> ParseQuests(string content)
        {
            var results = new List<QuestDefinition>();
            if (string.IsNullOrEmpty(content)) return results;

            XmlDocument doc = ExtractGameXmlDocument(content);
            if (doc == null) return results;

            XmlNodeList questNodes = doc.SelectNodes("//quest");
            if (questNodes == null) return results;

            foreach (XmlNode node in questNodes)
            {
                string id = node.Attributes?["id"]?.Value?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(id)) continue;

                var questDef = ScriptableObject.CreateInstance<QuestDefinition>();
                questDef.questId = id;
                questDef.questTitle = FormatTitle(id);
                questDef.isMainQuest = node.Attributes?["main"]?.Value == "1";
                questDef.employer = node.Attributes?["empl"]?.Value?.Trim() ?? string.Empty;

                questDef.rewardXp = ParseInt(node.Attributes?["xp"]?.Value, 0);
                questDef.rewardSp = ParseInt(node.Attributes?["sp"]?.Value, 0);
                questDef.rewardPay = ParseInt(node.Attributes?["pay"]?.Value, 0);
                questDef.rewardReputation = ParseInt(node.Attributes?["rep"]?.Value, 0);

                questDef.stages.Clear();
                XmlNodeList stageNodes = node.SelectNodes("./q");
                if (stageNodes != null)
                {
                    foreach (XmlNode stageNode in stageNodes)
                    {
                        var stage = new QuestStageDefinition
                        {
                            stageIndex = ParseInt(stageNode.Attributes?["id"]?.Value, questDef.stages.Count + 1),
                            descriptionKey = $"{id}_stage_{stageNode.Attributes?["id"]?.Value}",
                            isHidden = stageNode.Attributes?["invis"]?.Value == "1",
                            isOptional = stageNode.Attributes?["nn"]?.Value == "1",
                            requiredItemId = stageNode.Attributes?["collect"]?.Value?.Trim() ?? string.Empty,
                            requiredItemCount = ParseInt(stageNode.Attributes?["kol"]?.Value, 1),
                            turnInItemId = stageNode.Attributes?["give"]?.Value?.Trim() ?? string.Empty
                        };
                        questDef.stages.Add(stage);
                    }
                }

                XmlNode nextNode = node.SelectSingleNode("./next");
                questDef.nextQuestId = nextNode?.Attributes?["id"]?.Value?.Trim() ?? string.Empty;

                results.Add(questDef);
            }

            return results;
        }

        public static (List<NpcDefinition> npcs, List<CampaignScriptDefinition> scripts) ParseNpcsAndScripts(string content)
        {
            var npcResults = new List<NpcDefinition>();
            var scriptResults = new List<CampaignScriptDefinition>();
            if (string.IsNullOrEmpty(content)) return (npcResults, scriptResults);

            XmlDocument doc = ExtractGameXmlDocument(content);
            if (doc == null) return (npcResults, scriptResults);

            // NPCs
            XmlNodeList npcNodes = doc.SelectNodes("//npc");
            if (npcNodes != null)
            {
                foreach (XmlNode node in npcNodes)
                {
                    string id = node.Attributes?["id"]?.Value?.Trim() ?? string.Empty;
                    if (string.IsNullOrEmpty(id)) continue;

                    var npcDef = ScriptableObject.CreateInstance<NpcDefinition>();
                    npcDef.npcId = id;
                    npcDef.visualId = node.Attributes?["vis"]?.Value?.Trim() ?? string.Empty;
                    npcDef.aliasName = node.Attributes?["name"]?.Value?.Trim() ?? string.Empty;
                    npcDef.vendorId = node.Attributes?["vendor"]?.Value?.Trim() ?? string.Empty;
                    npcDef.interactionType = node.Attributes?["inter"]?.Value?.Trim() ?? string.Empty;
                    npcDef.iconIndex = ParseInt(node.Attributes?["ico"]?.Value, 0);
                    npcDef.userAction1 = node.Attributes?["ua1"]?.Value?.Trim() ?? string.Empty;
                    npcDef.userAction2 = node.Attributes?["ua2"]?.Value?.Trim() ?? string.Empty;
                    npcDef.defaultWeapon = node.Attributes?["weap"]?.Value?.Trim() ?? string.Empty;
                    npcDef.isSilent = node.Attributes?["silent"]?.Value == "1";

                    npcDef.dialogues.Clear();
                    XmlNodeList dialNodes = node.SelectNodes("./dial");
                    if (dialNodes != null)
                    {
                        foreach (XmlNode dialNode in dialNodes)
                        {
                            string dialId = dialNode.Attributes?["id"]?.Value?.Trim() ?? string.Empty;
                            if (string.IsNullOrEmpty(dialId)) continue;

                            var dialEntry = new NpcDialogueEntry
                            {
                                dialogueId = dialId,
                                isImportant = dialNode.Attributes?["imp"]?.Value == "1",
                                requiredTrigger = dialNode.Attributes?["trigger"]?.Value?.Trim() ?? string.Empty,
                                requiredLand = dialNode.Attributes?["land"]?.Value?.Trim() ?? string.Empty,
                                requiredQuest = dialNode.Attributes?["quest"]?.Value?.Trim() ?? string.Empty,
                                requiredQuestStage = ParseInt(dialNode.Attributes?["sub"]?.Value, 0),
                                previousDialogueId = dialNode.Attributes?["prev"]?.Value?.Trim() ?? string.Empty,
                                requiredArmor = dialNode.Attributes?["armor"]?.Value?.Trim() ?? string.Empty,
                                requiredWeapon = dialNode.Attributes?["weap"]?.Value?.Trim() ?? string.Empty,
                                requiredLevel = ParseInt(dialNode.Attributes?["lvl"]?.Value, 0),
                                music = dialNode.Attributes?["music"]?.Value?.Trim() ?? string.Empty
                            };

                            XmlNodeList actionNodes = dialNode.SelectNodes(".//s");
                            if (actionNodes != null)
                            {
                                foreach (XmlNode actNode in actionNodes)
                                {
                                    dialEntry.actions.Add(ParseAction(actNode));
                                }
                            }

                            npcDef.dialogues.Add(dialEntry);
                        }
                    }

                    npcResults.Add(npcDef);
                }
            }

            // Stand-alone scripts
            XmlNodeList scrNodes = doc.SelectNodes("//scr[@id]");
            if (scrNodes != null)
            {
                foreach (XmlNode node in scrNodes)
                {
                    string id = node.Attributes?["id"]?.Value?.Trim() ?? string.Empty;
                    if (string.IsNullOrEmpty(id)) continue;

                    var scriptDef = ScriptableObject.CreateInstance<CampaignScriptDefinition>();
                    scriptDef.scriptId = id;
                    scriptDef.actions.Clear();

                    XmlNodeList actionNodes = node.SelectNodes("./s");
                    if (actionNodes != null)
                    {
                        foreach (XmlNode actNode in actionNodes)
                        {
                            scriptDef.actions.Add(ParseAction(actNode));
                        }
                    }

                    scriptResults.Add(scriptDef);
                }
            }

            return (npcResults, scriptResults);
        }

        public static CampaignScriptAction ParseAction(XmlNode actNode)
        {
            return new CampaignScriptAction
            {
                action = actNode.Attributes?["act"]?.Value?.Trim() ?? string.Empty,
                target = actNode.Attributes?["targ"]?.Value?.Trim() ?? string.Empty,
                value = actNode.Attributes?["val"]?.Value?.Trim() ?? string.Empty,
                numericParam = ParseInt(actNode.Attributes?["n"]?.Value, 0),
                delaySeconds = ParseFloat(actNode.Attributes?["t"]?.Value, 0f),
                opt1 = ParseInt(actNode.Attributes?["opt1"]?.Value, 0),
                opt2 = ParseInt(actNode.Attributes?["opt2"]?.Value, 0)
            };
        }

        /// <summary>
        /// One AS3 <c>&lt;prob id level imp tip prize close&gt;</c> child of a <c>&lt;land&gt;</c>
        /// (<c>Land.as:809-863</c>, <c>Probation.as:64-131</c>).
        ///
        /// <para>Two traps live here, both of which the oracle resolves by <i>presence</i> rather than by
        /// value:</para>
        /// <list type="bullet">
        /// <item><c>level</c>: <c>newRandomProb</c> tests <c>xml.@level.length == 0</c>, so
        /// <c>level='0'</c> and no <c>level</c> at all are different answers. <c>hasLevel</c> records
        /// which one this is; <c>level</c> is only meaningful when <c>hasLevel</c>.</item>
        /// <item><c>imp</c>, <c>prize</c>, <c>close</c>: tested as <c>xml.@x.length()</c>, so
        /// <c>close='0'</c> still means "closes". A <c>== "1"</c> comparison would be wrong.</item>
        /// </list>
        ///
        /// <para><c>tip</c> is the exception — it is a value (<c>Probation.as:93-96</c>, default 0), and
        /// <c>"2"</c> is what makes the door a <c>doorboss</c> rather than a <c>doorprob</c>.</para>
        /// </summary>
        public static ProbRoomDefinition ParseProbRoom(XmlNode probNode)
        {
            var prob = new ProbRoomDefinition();
            if (probNode?.Attributes == null) return prob;

            prob.id = probNode.Attributes["id"]?.Value?.Trim() ?? string.Empty;

            // Absent is not 0. Land.as:821 compares xml.@level.length == 0, not xml.@level <= maxlevel alone.
            XmlAttribute levelAttr = probNode.Attributes["level"];
            prob.hasLevel = levelAttr != null;
            prob.level = ParseInt(levelAttr?.Value, 0);

            // Presence tests, matching Probation.as:89-99 and Land.as:824.
            prob.imp = probNode.Attributes["imp"] != null;
            prob.prize = probNode.Attributes["prize"] != null;
            prob.close = probNode.Attributes["close"] != null;

            prob.tip = probNode.Attributes["tip"]?.Value?.Trim() ?? string.Empty;

            XmlNodeList conNodes = probNode.SelectNodes("./con");
            if (conNodes != null)
            {
                foreach (XmlNode conNode in conNodes)
                {
                    prob.contents.Add(new ProbContentData
                    {
                        tip = conNode.Attributes?["tip"]?.Value?.Trim() ?? string.Empty,
                        uid = conNode.Attributes?["uid"]?.Value?.Trim() ?? string.Empty,
                        qid = conNode.Attributes?["qid"]?.Value?.Trim() ?? string.Empty
                    });
                }
            }

            return prob;
        }

        private static XmlDocument ExtractGameXmlDocument(string content)
        {
            try
            {
                Match gameMatch = Regex.Match(content, @"<game>[\s\S]*?<\/game>", RegexOptions.IgnoreCase);
                string xmlSnippet = gameMatch.Success ? gameMatch.Value : content;

                XmlDocument doc = new XmlDocument();
                doc.LoadXml(xmlSnippet);
                return doc;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CampaignDataParser] XML parsing failed: {ex.Message}");
                return null;
            }
        }

        private static string FormatTitle(string id)
        {
            if (string.IsNullOrEmpty(id)) return string.Empty;
            string clean = id.Replace("random_", "").Replace("story", "Story: ").Replace("_", " ");
            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(clean);
        }

        private static int ParseInt(string text, int defaultValue)
        {
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) ? result : defaultValue;
        }

        private static float ParseFloat(string text, float defaultValue)
        {
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float result) ? result : defaultValue;
        }
    }
}
