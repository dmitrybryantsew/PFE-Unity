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

                // AS3 `land.itemScripts` (Land.as:141-149) — the first-acquisition item scripts. The
                // selection is `./scr` (direct children only) and it matters: the four `<scr>` children
                // that live inside `<prob>` blocks carry eve='alarm'/'close'/'out' and belong to
                // Probation's event machine, not to this table. Filtering on the attribute value rather
                // than on position is what keeps those out.
                //
                // The oracle registers these by `@item` into an Array, so a duplicate item id within one
                // land would silently keep the last. No land in the shipped data has one (14 scripts
                // across 8 lands, all distinct), so the list is faithful; if that ever changes, the
                // overwrite has to be reproduced here rather than appended.
                XmlNodeList itemScriptNodes = node.SelectNodes("./scr[@eve='take']");
                if (itemScriptNodes != null)
                {
                    foreach (XmlNode scrNode in itemScriptNodes)
                    {
                        // `Boolean(_loc4_.@item.length())` — a `take` script with no item is skipped.
                        string itemId = scrNode.Attributes?["item"]?.Value?.Trim() ?? string.Empty;
                        if (string.IsNullOrEmpty(itemId)) continue;

                        var itemScript = new LandItemScript { itemId = itemId };

                        // `Script.as:47-53` reads `param1.s` — the children named exactly `s`. The one
                        // `<s1>` in GameData.as (:27, the `mont` script) is therefore dead data and is
                        // dropped here for the same reason `ParseNpcsAndScripts` uses `./s`.
                        XmlNodeList actionNodes = scrNode.SelectNodes("./s");
                        if (actionNodes != null)
                        {
                            foreach (XmlNode actNode in actionNodes)
                            {
                                itemScript.actions.Add(ParseAction(actNode));
                            }
                        }

                        landDef.itemScripts.Add(itemScript);
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

                // The four close-side scalars Quest.close reads off the parent node
                // (Quest.as:124-135, :205-220). QuestFacts has carried Trigger/TriggerSet/EndDial/
                // BegDial fields since it was written, but nothing ever populated them — so the
                // trigger write and the closing dialogue were dead on the parent path.
                questDef.trigger = node.Attributes?["trigger"]?.Value?.Trim() ?? string.Empty;
                questDef.triggerSet = node.Attributes?["triggerset"]?.Value?.Trim() ?? string.Empty;
                questDef.endDialogueId = node.Attributes?["enddial"]?.Value?.Trim() ?? string.Empty;
                questDef.beginDialogueId = node.Attributes?["begdial"]?.Value?.Trim() ?? string.Empty;

                // `<reward>` children — the payout half of close() (Quest.as:661-689). Two payloads
                // share the element: an item grant (`@id` + optional `@kol`) and a trigger write
                // (`@trigger` + optional `@set`). `money` is an ordinary item row, so it needs no
                // special case. A row carrying neither is kept but is inert, which is what the
                // Oracle does too (both branches are `if(@x.length())`).
                questDef.rewards.Clear();
                XmlNodeList rewardNodes = node.SelectNodes("./reward");
                if (rewardNodes != null)
                {
                    foreach (XmlNode rewardNode in rewardNodes)
                    {
                        questDef.rewards.Add(new QuestRewardDefinition
                        {
                            itemId = rewardNode.Attributes?["id"]?.Value?.Trim() ?? string.Empty,
                            count = ParseInt(rewardNode.Attributes?["kol"]?.Value, 1),
                            trigger = rewardNode.Attributes?["trigger"]?.Value?.Trim() ?? string.Empty,
                            set = rewardNode.Attributes?["set"]?.Value?.Trim() ?? string.Empty,
                        });
                    }
                }

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
                            // `hidden` and `invis` are two attributes, not one — see
                            // QuestStageDefinition's remarks. `invis` keeps the row out of the journal
                            // entirely; `hidden` lists it as `?????`.
                            isHidden = stageNode.Attributes?["hidden"]?.Value == "1",
                            isInvisible = stageNode.Attributes?["invis"]?.Value == "1",
                            isOptional = stageNode.Attributes?["nn"]?.Value == "1",
                            requiredItemId = stageNode.Attributes?["collect"]?.Value?.Trim() ?? string.Empty,
                            requiredItemCount = ParseInt(stageNode.Attributes?["kol"]?.Value, 1),
                            turnInItemId = stageNode.Attributes?["give"]?.Value?.Trim() ?? string.Empty,
                            // `@del` is read only inside the `if(@collect.length())` block
                            // (Quest.as:140-156), so a `del` on a stage with no `collect` is dead data.
                            // Recorded as authored rather than pre-filtered — the rules read it only
                            // through the same collect-gated path.
                            deleteOnClose = stageNode.Attributes?["del"] != null,
                            collectTip = ParseInt(stageNode.Attributes?["coltip"]?.Value, 0),
                            // `result` is a presence test too (Quest.as:185-192), and it also flags the
                            // PARENT (`this.par.result = true`) — that parent half is what makes
                            // `Quest.isResult` run at all, and it is re-derived in ProjectQuest.
                            isResult = stageNode.Attributes?["result"] != null
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

                    // Seven more attributes the oracle reads and the port used to drop silently
                    // (rule #12). Each has a named read site, so none of them is speculative:
                    //   ndial    NPC.as:58-60          — the legacy dialogue id
                    //   noturn   UnitNPC.as:109-111    — suppress the facing update
                    //   replic   UnitNPC.as:125-127    — dialogue replica id
                    //   sloy     UnitNPC.as:141-143    — layer override
                    //   weap2    UnitNPC.as:137-139    — secondary weapon
                    //   needskill NPC.as:503/565       — skill gate on repair()/patient()
                    //   needitem NPC.as:586-588        — item consumed by patient()
                    npcDef.legacyDialogueId = node.Attributes?["ndial"]?.Value?.Trim() ?? string.Empty;
                    npcDef.noTurn = node.Attributes?["noturn"] != null;
                    npcDef.replicaId = node.Attributes?["replic"]?.Value?.Trim() ?? string.Empty;
                    npcDef.layer = node.Attributes?["sloy"]?.Value?.Trim() ?? string.Empty;
                    npcDef.secondaryWeapon = node.Attributes?["weap2"]?.Value?.Trim() ?? string.Empty;
                    npcDef.requiredSkill = node.Attributes?["needskill"]?.Value?.Trim() ?? string.Empty;
                    npcDef.requiredItemId = node.Attributes?["needitem"]?.Value?.Trim() ?? string.Empty;

                    // The autodoc's payload — `NPC.repair` (NPC.as:492-557). A `<quest>` child names the
                    // quest the first interaction grants and a successful repair closes; the `<rep>`
                    // children are the parts list. Neither was read before, which is why
                    // `NpcActivationKind.AutoDocRepair` resolved and then had nothing to run.
                    XmlNode questChild = node.SelectSingleNode("./quest");
                    npcDef.questId = questChild?.Attributes?["id"]?.Value?.Trim() ?? string.Empty;
                    npcDef.questSubId = questChild?.Attributes?["cid"]?.Value?.Trim() ?? string.Empty;

                    npcDef.repairRequirements.Clear();
                    XmlNodeList repNodes = node.SelectNodes("./rep");
                    if (repNodes != null)
                    {
                        foreach (XmlNode repNode in repNodes)
                        {
                            string repItem = repNode.Attributes?["id"]?.Value?.Trim() ?? string.Empty;
                            if (string.IsNullOrEmpty(repItem)) continue;

                            npcDef.repairRequirements.Add(new NpcRepairRequirement
                            {
                                itemId = repItem,
                                // `_loc3_.@kol` is compared and then subtracted; an absent @kol reads
                                // as 0 in AS3 and would make the row free, so it defaults to 1 here —
                                // no shipped row omits it (11 of 11 carry kol).
                                count = ParseInt(repNode.Attributes?["kol"]?.Value, 1),
                            });
                        }
                    }

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
                                // :284-290 — `@n` is the required trigger VALUE; absent means the gate is
                                // `trig(name) == 1`. Both were dropped before: the parser had no field, so
                                // two authored rows silently lost their condition (rule #12).
                                requiredTriggerValue = dialNode.Attributes?["n"]?.Value?.Trim() ?? string.Empty,
                                // :278-282 — the gate is the barter SKILL, not pers.barterLvl.
                                requiredBarter = ParseInt(dialNode.Attributes?["barter"]?.Value, 0),
                                // :307-310 — the equipped pet must match.
                                requiredPet = dialNode.Attributes?["pet"]?.Value?.Trim() ?? string.Empty,
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

        /// <summary>
        /// Parses the <c>&lt;vendor&gt;</c> blocks of <c>GameData.as</c> — the 13 blocks of
        /// <c>GameData.d.vendor</c> under the <c>ТОРГОВЛЯ</c> banner (<c>GameData.as:2143-2700</c>),
        /// read by <c>fe.serv.Vendor</c> (<c>Vendor.as:24-123</c>).
        ///
        /// <para><b>Why it returns plain data and not <see cref="VendorDefinition"/>.</b> This parser is
        /// shared by the editor importer and the offline fixtures; <c>ScriptableObject.CreateInstance</c>
        /// is an <c>ECall</c> and throws outside the engine, so a parser that built the asset itself
        /// could never be verified in the offline wall — which is exactly where the import's acceptance
        /// checks (13 blocks, 83 weapon rows, the <c>^N</c> split) have to run. The importer maps
        /// <see cref="VendorParseResult"/> onto the asset.</para>
        ///
        /// <para><b>Presence vs value.</b> <c>noref</c>, <c>nocheap</c>, <c>hardinv</c> and <c>pmult</c>
        /// are tested by the oracle as <c>@x.length()</c>, not as a value (<c>Vendor.as:69-84</c>) — so
        /// <c>noref='0'</c> still means "never restocked" and <c>pmult='0'</c> really is a zero price.
        /// <c>hasPriceMultiplier</c> records which one this row is.</para>
        ///
        /// <para><b>Unrecognised attributes are reported, not dropped.</b> Any attribute on a row that no
        /// field claims is appended to <see cref="VendorParseResult.unrecognisedAttributes"/>, so a new
        /// oracle attribute surfaces as a log line instead of silence (rule #12).</para>
        /// </summary>
        public static List<VendorParseResult> ParseVendors(string content)
        {
            var results = new List<VendorParseResult>();
            if (string.IsNullOrEmpty(content)) return results;

            XmlDocument doc = ExtractGameXmlDocument(content);
            if (doc == null) return results;

            XmlNodeList vendorNodes = doc.SelectNodes("//vendor");
            if (vendorNodes == null) return results;

            foreach (XmlNode node in vendorNodes)
            {
                string id = node.Attributes?["id"]?.Value?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(id)) continue;

                var result = new VendorParseResult { vendorId = id };
                var unrecognised = new HashSet<string>(StringComparer.Ordinal);

                XmlNodeList buyNodes = node.SelectNodes("./buy");
                if (buyNodes != null)
                {
                    foreach (XmlNode buy in buyNodes)
                    {
                        var entry = new VendorStockEntry();

                        // AS3 Item's constructor splits "^N" (Item.as:132-140); the vendor row's id is
                        // therefore stored split, and StockKey rebuilds it.
                        SplitVariant(buy.Attributes?["id"]?.Value, out string baseId, out int variant);
                        entry.itemId = baseId;
                        entry.variant = variant;

                        // `@n` absent converts to 0 in AS3, and `kol < 0` is then false — so the row keeps
                        // a zero stock and the page's `b.kol > 0` guard hides it. Faithful, not a default.
                        entry.stockCount = ParseInt(buy.Attributes?["n"]?.Value, 0);
                        entry.barter = ParseInt(buy.Attributes?["barter"]?.Value, 0);
                        entry.level = ParseInt(buy.Attributes?["lvl"]?.Value, 0);
                        entry.trigger = buy.Attributes?["trigger"]?.Value?.Trim() ?? string.Empty;

                        // Presence tests — Vendor.as:69-84 tests `.length()`, so "0" is still "on".
                        entry.noRefill = buy.Attributes?["noref"] != null;
                        entry.noCheap = buy.Attributes?["nocheap"] != null;
                        entry.hardInventory = buy.Attributes?["hardinv"] != null;
                        entry.hasPriceMultiplier = buy.Attributes?["pmult"] != null;
                        entry.priceMultiplier = ParseFloat(buy.Attributes?["pmult"]?.Value, 0f);

                        CollectUnrecognised(buy, KnownBuyAttributes, unrecognised);
                        result.stock.Add(entry);
                    }
                }

                XmlNodeList taskNodes = node.SelectNodes("./task");
                if (taskNodes != null)
                {
                    foreach (XmlNode task in taskNodes)
                    {
                        var entry = new VendorTaskEntry
                        {
                            taskId = task.Attributes?["id"]?.Value?.Trim() ?? string.Empty,
                            skill = task.Attributes?["skill"]?.Value?.Trim() ?? string.Empty,
                            skillLevel = ParseInt(task.Attributes?["skilln"]?.Value, 0),
                            land = task.Attributes?["land"]?.Value?.Trim() ?? string.Empty,
                            trigger = task.Attributes?["trigger"]?.Value?.Trim() ?? string.Empty,
                            manual = task.Attributes?["man"]?.Value?.Trim() ?? string.Empty
                        };

                        if (string.IsNullOrEmpty(entry.taskId)) continue;

                        CollectUnrecognised(task, KnownTaskAttributes, unrecognised);
                        result.tasks.Add(entry);
                    }
                }

                result.unrecognisedAttributes.AddRange(unrecognised);
                results.Add(result);
            }

            return results;
        }

        /// <summary>
        /// The attribute names present on <c>&lt;npc&gt;</c> tags that no <see cref="NpcDefinition"/>
        /// field claims, as <c>"&lt;npcId&gt;.&lt;attribute&gt;"</c>.
        ///
        /// <para><b>Why this exists as its own entry point.</b> <c>ParseNpcsAndScripts</c> builds a
        /// <c>ScriptableObject</c>, which is an <c>ECall</c> and cannot run outside the engine — so the
        /// only place the NPC field set could be checked was inside Unity. This walk reads the same
        /// attribute list without constructing anything, which makes "does the port model every
        /// attribute the shipped data uses?" an offline assertion. It is the check that would have
        /// caught <c>ndial</c>, <c>noturn</c>, <c>replic</c>, <c>sloy</c>, <c>weap2</c>,
        /// <c>needskill</c> and <c>needitem</c> — seven attributes that were dropped in silence.</para>
        /// </summary>
        public static List<string> ParseNpcUnrecognisedAttributes(string content)
        {
            var unknown = new List<string>();
            if (string.IsNullOrEmpty(content)) return unknown;

            XmlDocument doc = ExtractGameXmlDocument(content);
            if (doc == null) return unknown;

            XmlNodeList npcNodes = doc.SelectNodes("//npc");
            if (npcNodes == null) return unknown;

            foreach (XmlNode node in npcNodes)
            {
                string id = node.Attributes?["id"]?.Value?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(id) || node.Attributes == null) continue;

                foreach (XmlAttribute attribute in node.Attributes)
                {
                    if (!KnownNpcAttributes.Contains(attribute.Name)) unknown.Add(id + "." + attribute.Name);
                }
            }

            return unknown;
        }

        private static readonly HashSet<string> KnownNpcAttributes =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "id", "vis", "name", "vendor", "inter", "ico", "ua1", "ua2", "weap", "silent",
                "ndial", "noturn", "replic", "sloy", "weap2", "needskill", "needitem"
            };

        private static readonly HashSet<string> KnownBuyAttributes =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "id", "n", "barter", "lvl", "trigger", "noref", "nocheap", "hardinv", "pmult"
            };

        private static readonly HashSet<string> KnownTaskAttributes =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "id", "skill", "skilln", "land", "trigger", "man"
            };

        private static void CollectUnrecognised(XmlNode node, HashSet<string> known, HashSet<string> sink)
        {
            if (node.Attributes == null) return;

            foreach (XmlAttribute attribute in node.Attributes)
            {
                if (!known.Contains(attribute.Name)) sink.Add(attribute.Name);
            }
        }

        /// <summary>
        /// AS3 <c>Item</c>'s variant split (<c>Item.as:132-140</c>): when the second-to-last character of
        /// the id is <c>'^'</c>, the last character is the variant and the id is truncated by two. Note
        /// this only ever reads a <b>single digit</b> — the oracle's own limitation, reproduced rather
        /// than "improved", because <c>buys2</c>'s key and the page's <c>rid</c> are both built from it.
        /// </summary>
        public static void SplitVariant(string rawId, out string baseId, out int variant)
        {
            baseId = rawId?.Trim() ?? string.Empty;
            variant = 0;

            if (baseId.Length >= 2 && baseId[baseId.Length - 2] == '^')
            {
                char digit = baseId[baseId.Length - 1];
                if (digit >= '0' && digit <= '9')
                {
                    variant = digit - '0';
                    baseId = baseId.Substring(0, baseId.Length - 2);
                }
            }
        }

        /// <summary>
        /// One authored <c>&lt;s act=… targ=… val=… t=… n=… opt1=… opt2=…/&gt;</c> —
        /// <c>Script.analiz</c> (<c>Script.as:64-121</c>).
        ///
        /// <para><b><c>@n</c> defaults to <c>-1</c>, not <c>0</c>.</b> <c>analiz</c> declares
        /// <c>var _loc6_:String = "-1";</c> (<c>:70</c>) and overwrites it only when the attribute is
        /// present (<c>:97-100</c>), so a bare <c>&lt;s act="stage" val="x"/&gt;</c> carries
        /// <c>"-1"</c> and means <i>finish the whole quest</i> (<c>Game.as:624</c>). Twelve authored
        /// rows rely on it — <c>storyMain</c>, <c>storyStable</c>, <c>storyMeet</c>, <c>storyHome</c>,
        /// <c>storyFind</c>, <c>storyStorm</c>, <c>storyCanter</c>, <c>storyMane</c>,
        /// <c>storyHelpCovert</c>, <c>storyHelpCamp</c>, <c>storyFbattle</c>, <c>patientHeal</c> — and
        /// with a <c>0</c> default each one became <c>closeSub("0")</c>, which matches no sub (they are
        /// 1-based) and therefore closed nothing. A <c>0</c> default is not "neutral": it is a third
        /// meaning that the oracle never produces.</para>
        ///
        /// <para><c>@t</c> is read as <b>seconds</b> here and multiplied by the frame rate later
        /// (<c>ScriptRunner.FramesOf</c>), where <c>analiz</c> converts it once at read time. Both are
        /// one conversion; keeping the authored unit on the asset is what lets the field be inspected
        /// in the editor without knowing the frame rate.</para>
        /// </summary>
        public static CampaignScriptAction ParseAction(XmlNode actNode)
        {
            return new CampaignScriptAction
            {
                action = actNode.Attributes?["act"]?.Value?.Trim() ?? string.Empty,
                target = actNode.Attributes?["targ"]?.Value?.Trim() ?? string.Empty,
                value = actNode.Attributes?["val"]?.Value?.Trim() ?? string.Empty,
                numericParam = ParseInt(actNode.Attributes?["n"]?.Value, -1),
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

            // AS3 `maxwave = xml.wave.length()` (Probation.as:127-130), and each wave's `@t`
            // (:341-344) and `<obj>` count (:337-340). The payloads are NOT read — see
            // ProbRoomDefinition.waves for why — but without the count `maxwave` is 0, and a wave-only
            // prob's gate `nwave < maxwave` would read as already satisfied, closing a room that has
            // spawned nothing. The count alone is what keeps those six rooms closed until a spawner
            // exists.
            XmlNodeList waveNodes = probNode.SelectNodes("./wave");
            if (waveNodes != null)
            {
                foreach (XmlNode waveNode in waveNodes)
                {
                    prob.waves.Add(new ProbWaveData
                    {
                        // `@t.length()`, not `@t` — a present wave with no delay must be told apart
                        // from one with `t='0'`, exactly as `level` is above.
                        hasDelay = waveNode.Attributes?["t"] != null,
                        delaySeconds = ParseInt(waveNode.Attributes?["t"]?.Value, 0),
                        spawnCount = waveNode.SelectNodes("./obj")?.Count ?? 0,
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
