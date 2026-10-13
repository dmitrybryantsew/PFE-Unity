using System;
using System.Collections.Generic;

namespace PFE.Systems.Campaign
{
    /// <summary>How far the port has got on one of AS3's campaign variables, so a debug tab can say
    /// "not ported yet" instead of drawing a field that writes into nothing.</summary>
    public enum CampaignVariableAvailability
    {
        /// <summary>The port holds the state and exposes a real seam that reads and writes it.</summary>
        Live,

        /// <summary>The port can compute the value from live state, but there is nothing to write.</summary>
        Derived,

        /// <summary>AS3 has it; the port does not model it yet. Setting it here would be a lie.</summary>
        NotPorted,
    }

    /// <summary>One of AS3's campaign-level variables, described for a debug inspector.</summary>
    public readonly struct CampaignVariableInfo
    {
        public readonly string Id;
        public readonly string Summary;
        public readonly string Responsibility;
        public readonly string Oracle;
        public readonly CampaignVariableAvailability Availability;

        public CampaignVariableInfo(
            string id,
            string summary,
            string responsibility,
            string oracle,
            CampaignVariableAvailability availability)
        {
            Id = id;
            Summary = summary;
            Responsibility = responsibility;
            Oracle = oracle;
            Availability = availability;
        }
    }

    /// <summary>One story trigger worth knowing by name, with what it is responsible for.</summary>
    public readonly struct ImportantTriggerInfo
    {
        public readonly string Name;

        /// <summary>The value family a tester should reach for, e.g. "0 | 1", "1..3", "count", "string".
        /// Empty when any value is meaningful.</summary>
        public readonly string Values;

        public readonly string Responsibility;
        public readonly string Oracle;

        public ImportantTriggerInfo(string name, string values, string responsibility, string oracle)
        {
            Name = name;
            Values = values;
            Responsibility = responsibility;
            Oracle = oracle;
        }
    }

    /// <summary>
    /// The curated catalogue of AS3's main campaign variables and story triggers, with a one-line
    /// statement of what each is responsible for and the oracle line that says so.
    ///
    /// <para><b>Why a table and not comments in the overlay.</b> The F2 "Campaign Vars" tab draws its
    /// descriptions from here, so the wording lives in one Unity-free file: the decision "which
    /// variables exist, what are they responsible for, which are live in the port" is a decision table,
    /// and a decision table belongs somewhere it can be asserted offline rather than in an
    /// <c>OnGUI</c> method nobody can run in a test.</para>
    ///
    /// <para><b>Source of truth.</b> Every row cites the AS3 line it was read from
    /// (<c>fe.loc.Game</c>, <c>fe.loc.Quest</c>, <c>fe.loc.LandAct</c>, <c>fe.unit.Pers</c>, and the
    /// campaign data in <c>fe.GameData</c>). A row whose citation cannot be found is a defect in this
    /// file, not a curiosity.</para>
    /// </summary>
    public static class CampaignVariableCatalog
    {
        /// <summary>AS3 <c>Game</c>'s own fields plus the two campaign-persistent tables that live on
        /// <c>Pers</c>/<c>LandAct</c>. These are the variables the F2 tab lists.</summary>
        public static IReadOnlyList<CampaignVariableInfo> Variables { get; } = new CampaignVariableInfo[]
        {
            new CampaignVariableInfo(
                "curLandId",
                "The land the player is in",
                "Every land-keyed branch reads it: the world builder, script conditions, the travel map. " +
                "A transition that sets the world but not this leaves the two disagreeing.",
                "Game.as:19",
                CampaignVariableAvailability.Live),

            new CampaignVariableInfo(
                "missionId",
                "The land the current mission belongs to",
                "Set when a mission begins; exit/gotoNextLevel regenerates THIS land, not curLandId, so " +
                "an exit taken after a return to camp re-enters the mission.",
                "Game.as:45, Game.as:471",
                CampaignVariableAvailability.Live),

            new CampaignVariableInfo(
                "baseId",
                "The hub land",
                "The land a non-main checkpoint teleports to when it is not the mission hub.",
                "Game.as:43, CheckPoint.as:252",
                CampaignVariableAvailability.Live),

            new CampaignVariableInfo(
                "triggers",
                "The story-flag table (name to int or string)",
                "The campaign's global variables. Script conditions, dialogue gates, quest checks and the " +
                "land generator all read this one table; an absent key is NOT a stored zero.",
                "Game.as:25, Game.as:676-679",
                CampaignVariableAvailability.Live),

            new CampaignVariableInfo(
                "quests",
                "Quest states: 0 not taken, 1 in progress, 2 done",
                "The journal. A completed quest chains its next quest in through xml.next.",
                "Quest.as:71, Quest.as:373-400, Game.as:287-296",
                CampaignVariableAvailability.Live),

            new CampaignVariableInfo(
                "lands",
                "Per-land runtime state (visited/passed/access/stage/upStage/lastCpCode)",
                "Persisted per save; what the next build of each land reads. The F2 Land State tab edits " +
                "the current land's copy.",
                "Game.as:11, LandAct.as:90-101, LandAct.as:267-301",
                CampaignVariableAvailability.Live),

            new CampaignVariableInfo(
                "mReturn",
                "Whether the player is returned to camp after a mission",
                "Recomputed on every land entry from the noreturn trigger (>0 means no return).",
                "Game.as:49, Game.as:390-397",
                CampaignVariableAvailability.Derived),

            new CampaignVariableInfo(
                "notes",
                "Journal note ids added this game",
                "addNote pushes the id and sets trigger note_<id>; the trigger table is the real store, " +
                "so the note list can be derived from it.",
                "Game.as:27, Game.as:666-674",
                CampaignVariableAvailability.Derived),

            new CampaignVariableInfo(
                "curCoord",
                "Coordinates the next land entry starts at (\"x:y\")",
                "Saved by gotoLand and consumed by the entry that follows it, then cleared.",
                "Game.as:21, Game.as:438, Game.as:372",
                CampaignVariableAvailability.NotPorted),

            new CampaignVariableInfo(
                "globalDif",
                "Global difficulty, clamped 0..4",
                "changeDif clamps it and re-derives pers parameters from it. The port has no runtime copy: " +
                "difficulty is re-derived per room from the land stage instead.",
                "Game.as:41, Game.as:311-329",
                CampaignVariableAvailability.NotPorted),

            new CampaignVariableInfo(
                "crea",
                "Force a fresh layout on the next land entry",
                "Set by beginMission for a mission land and by exit; cleared on entry. The port expresses " +
                "the same intent as the forceRegenerate argument of a transition.",
                "Game.as:47, Game.as:458, Game.as:469, Game.as:369",
                CampaignVariableAvailability.NotPorted),

            new CampaignVariableInfo(
                "limits",
                "Per-name counters for \"how many times\" rules",
                "getLimit seeds the counter from the trigger of the same name; addLimit increments the " +
                "limit (mode 1) or the trigger itself (mode 2).",
                "Game.as:29, Game.as:681-720",
                CampaignVariableAvailability.NotPorted),

            new CampaignVariableInfo(
                "names",
                "Per-unit name-variant tables",
                "Spawned units pick a display name from names[id_name]; runtime only, never saved.",
                "Game.as:33, Unit.as:1483, Unit.as:1492",
                CampaignVariableAvailability.NotPorted),

            new CampaignVariableInfo(
                "t_save / dBeg / t_proshlo",
                "Play-time accounting",
                "t_save accumulates across saves; the session adds (now - dBeg). Feeds the PipBuck clock " +
                "and nothing else.",
                "Game.as:35-39, Game.as:753-762",
                CampaignVariableAvailability.NotPorted),

            new CampaignVariableInfo(
                "vendors / npcs",
                "Per-land vendor and NPC runtime states",
                "Saved and restored per land. The port has the definitions but no per-land runtime yet, so " +
                "refill and vendor stock are no-ops.",
                "Game.as:13-17",
                CampaignVariableAvailability.NotPorted),
        };

        /// <summary>
        /// The story triggers a tester actually needs by name. The families that matter most are the
        /// prefixes: <c>note_&lt;id&gt;</c> (a journal note was added), <c>prob_&lt;id&gt;</c> (a prob
        /// location was cleared), <c>frag_&lt;id&gt;</c> (a collected-part count), <c>mess_&lt;name&gt;</c>
        /// (a one-shot message was shown) and <c>dial_&lt;id&gt;</c> (a dialogue gate).
        /// </summary>
        public static IReadOnlyList<ImportantTriggerInfo> ImportantTriggers { get; } = new ImportantTriggerInfo[]
        {
            new ImportantTriggerInfo(
                "firstroom", "0 | 1",
                "Set once the first land room exists; the next generated land seeds its stage from " +
                "pers.level-1 instead of starting at stage 0.",
                "Game.as:354-368"),

            new ImportantTriggerInfo(
                "noreturn", "0 | 1",
                "Set on a new game and cleared the first time the camp is entered; while >0 the player " +
                "is not returned to camp after a mission (mReturn=false).",
                "Game.as:204, Game.as:375, Game.as:390-397"),

            new ImportantTriggerInfo(
                "nomed", "0 | 1",
                "New-game flag, cleared when the camp is first entered; gates medic-dependent UI such " +
                "as the limb-health readouts.",
                "Game.as:307, Game.as:376, PipBuck.as:510-511"),

            new ImportantTriggerInfo(
                "rbl_visited", "0 | 1",
                "The camp (rbl) has been entered; gates first-visit camp content.",
                "Game.as:377, GameData.as:1274"),

            new ImportantTriggerInfo(
                "mbase_visited", "0 | 1",
                "mbase has been entered; decides whether conf 4's entry cell is the authored beg0 or a " +
                "random room, and hides mbase's first-visit NPCs.",
                "GameData.as:1425, Land.as:276, NPC.as:161"),

            new ImportantTriggerInfo(
                "prob_<id>", "1 | count",
                "A prob location has been cleared; newRandomProb will not place it again, and incremented " +
                "each further clear.",
                "Land.as:821, Probation.as:201-207"),

            new ImportantTriggerInfo(
                "note_<id>", "0 | 1",
                "The journal note <id> has been added (addNote sets this and pushes the id).",
                "Game.as:666-674"),

            new ImportantTriggerInfo(
                "frag_<id>", "count",
                "How many parts of id have been collected; the PipBuck info page shows the count and a " +
                "unit only drops its part once.",
                "PipPageInfo.as:265-267, Unit.as:4511-4513"),

            new ImportantTriggerInfo(
                "mess_<name>", "0 | 1",
                "The one-shot message <name> has already been shown; prevents a repeat popup.",
                "Invent.as:1295-1297, PipPageWork.as:506-508"),

            new ImportantTriggerInfo(
                "fin", "1..3",
                "Main-story finish stage; the mission-completion scripts march it forward.",
                "GameData.as:818, GameData.as:1562, GameData.as:1611"),

            new ImportantTriggerInfo(
                "storm", "2 | 3",
                "Storm story stage; set by the quest scripts that run the storm sequence.",
                "GameData.as:695, GameData.as:742, GameData.as:803"),

            new ImportantTriggerInfo(
                "observer", "0 | 1",
                "Observer story flag set by the observer quest script.",
                "GameData.as:888"),

            new ImportantTriggerInfo(
                "story_stable / story_ranger / story_mane / story_canter / story_book", "0 | 1",
                "One flag per story chapter; set when that chapter's script runs.",
                "GameData.as (act=\"trigger\" val=\"story_*\")"),

            new ImportantTriggerInfo(
                "ranger_q1 / ranger_q2 / ranger_q3 / ranger_q4", "0 | 1",
                "The ranger quest chain's four stage flags.",
                "GameData.as (act=\"trigger\" val=\"ranger_q*\")"),

            new ImportantTriggerInfo(
                "pet_phoenix", "0 | 1",
                "The phoenix has been tamed; unlocks its stat-page row and the wild-phoenix spawn rule " +
                "in matching biomes.",
                "PipPageStat.as:261, Location.as:2011-2019"),

            new ImportantTriggerInfo(
                "tame", "n/a",
                "The taming interaction's userAction on a tameable unit (the phoenix answers it).",
                "UnitPhoenix.as:28, UnitPhoenix.as:85"),

            new ImportantTriggerInfo(
                "curse", "0 | 1",
                "The curse effect is currently applied; set when the curse lands, cleared with it.",
                "Effect.as:215-217"),

            new ImportantTriggerInfo(
                "rollup", "0 | 1",
                "The rollup item has been taken/eaten.",
                "Invent.as:1595"),

            new ImportantTriggerInfo(
                "eco", "count",
                "The ecology counter mirrored from pers.eco; eco-gated objects read it.",
                "Pers.as:2281, Unit.as:1503"),

            new ImportantTriggerInfo(
                "soviet", "0 | 1",
                "Camp NPCs whose trig is \"soviet\" are visible only while this is >0.",
                "GameData.as:1026, RoomsCamp.as:629-638"),

            new ImportantTriggerInfo(
                "theend", "0 | 1",
                "The ending has been reached.",
                "GameData.as:1660"),

            new ImportantTriggerInfo(
                "cold_death", "0 | 1",
                "Gates the dialRanger5/dialRanger6 dialogues.",
                "GameData.as:777, GameData.as:784"),

            new ImportantTriggerInfo(
                "patient_tr1", "0 | 1",
                "Patient quest chain: tr1 set once the chain has started.",
                "NPC.as:608"),

            new ImportantTriggerInfo(
                "patient_tr2", "string",
                "Patient quest chain: tr2 holds a string state such as \"wait\", which is why the trigger " +
                "table is not an int table.",
                "NPC.as:591"),

            new ImportantTriggerInfo(
                "dial_<id>", "0 | 1",
                "Dialogue-stage flags; NPC dialogue branches are gated on trigger==n, so these are read " +
                "as gates rather than written as markers.",
                "NPC.as:280-289, Game.as:281"),
        };

        public static bool TryGetVariable(string id, out CampaignVariableInfo info)
        {
            if (!string.IsNullOrEmpty(id))
            {
                for (int i = 0; i < Variables.Count; i++)
                {
                    if (string.Equals(Variables[i].Id, id, StringComparison.OrdinalIgnoreCase))
                    {
                        info = Variables[i];
                        return true;
                    }
                }
            }

            info = default;
            return false;
        }
    }
}
