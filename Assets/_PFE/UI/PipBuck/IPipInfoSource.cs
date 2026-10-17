using System;
using System.Collections.Generic;
using System.Xml.Linq;
using UnityEngine;
using PFE.Systems.Campaign;
using PFE.Systems.Map;
using PFE.Systems.Map.Minimap;

namespace PFE.UI.PipBuck
{
    /// <summary>
    /// The travel-relevant facts about one land, as <b>page 3</b> reads them — the projection of
    /// <c>LandAct</c> that <c>PipPageInfo</c>'s travel branch and <c>PipPage.checkQuest</c> between them
    /// consult.
    ///
    /// <para><b>Why a plain class and not <c>LandDefinition</c>.</b> Same reason as
    /// <see cref="QuestSubFacts"/>: a <c>ScriptableObject</c> cannot be constructed outside the editor,
    /// so a rule written against one cannot run in this project's offline wall. The travel <i>rules</i>
    /// already read <c>TravelLand</c> for the same reason — this type exists because the page also needs
    /// the two fields <c>TravelLand</c> deliberately omits (<c>visited</c>/<c>access</c> are live state,
    /// and <c>dif</c>/<c>tip</c> are display data).</para>
    /// </summary>
    public sealed class PipLandFacts
    {
        /// <summary>AS3 <c>land.id</c>. Also the travel node's instance name — <c>visWMap[land.id]</c>.</summary>
        public string LandId = "";

        /// <summary>AS3 <c>land.prob</c> — a detached room collection, never offered on the map
        /// (<c>PipPageInfo.as:154</c>).</summary>
        public bool IsProb;

        /// <summary>AS3 <c>land.test</c> — a developer sandbox, hidden outside test mode.</summary>
        public bool IsTest;

        /// <summary>AS3 <c>land.visited</c> — the player has been there.</summary>
        public bool Visited;

        /// <summary>AS3 <c>land.access</c> — the land is unlocked, i.e. drawn on the map.</summary>
        public bool Access;

        /// <summary>AS3 <c>land.passed</c> — the land's story beat has been cleared.</summary>
        public bool Passed;

        /// <summary>AS3 <c>land.dif</c> — the recommended level, printed by the hover pane and used by
        /// <c>checkQuest</c>'s difficulty arm.</summary>
        public int Difficulty;

        /// <summary>AS3 <c>land.tip</c> — <c>"base"</c>, <c>"rnd"</c>, <c>"story"</c>, …</summary>
        public string Tip = "";

        /// <summary>AS3 <c>land.loaded</c> — the travel confirm requires it (<c>:761</c>).</summary>
        public bool Loaded;

        /// <summary>
        /// AS3 <c>game.checkTravel(land.id)</c> — the <c>fin</c> ladder
        /// (<c>Game.as:483-506</c>), already evaluated by <c>TravelMapModel.CheckTravel</c>.
        ///
        /// <para><b>It is a field rather than a delegate because the rule is the model's.</b> The page
        /// needs this answer in two places — the node's alpha and the click's gate — and re-deriving it
        /// from <c>Visited</c>/<c>Access</c> here would be a second copy of a ladder that depends on the
        /// current land and on the story flags. The source is the only thing that can see
        /// <c>TravelMapModel</c>, so the source answers it.</para>
        /// </summary>
        public bool CanTravel;

        /// <summary>AS3 <c>land.landStage</c> — printed as <c>landStage + 1</c> on a <c>rnd</c> land.</summary>
        public int LandStage;

        /// <summary>AS3 <c>land.kolAllProb</c> — how many prob rooms the land has.</summary>
        public int AllProbCount;

        /// <summary>AS3 <c>land.kolClosedProb</c> — how many of those are cleared.</summary>
        public int ClosedProbCount;
    }

    /// <summary>
    /// The local map's inputs — the three things <c>PipPageInfo</c>'s sub-page 1 branches on.
    ///
    /// <para><b>It carries the buffer, not a texture.</b> <c>LandMinimapComposer</c> is pure and
    /// Unity-free apart from <c>Color32</c>, so the source can hand the page a
    /// <c>LandMinimapBuffer</c> and let the <i>panel</i> be the only thing that touches a
    /// <c>Texture2D</c>. A snapshot that carried a texture could not be built in a test.</para>
    /// </summary>
    public sealed class PipMinimapFacts
    {
        /// <summary>AS3 <c>World.w.loc.noMap</c> — the location has no map at all, so the page shows
        /// <c>emptymap</c> and draws nothing (<c>PipPageInfo.as:95-97</c>).</summary>
        public bool NoMap;

        /// <summary>The composed image, or <c>null</c> when there is nothing to draw.</summary>
        public LandMinimapBuffer Buffer;

        /// <summary>
        /// What <c>TryBuild</c> found — the land grid's extent, which is what converts a room cell into
        /// a position on the image. Without it the marker cannot be placed: the buffer's origin is the
        /// land's <i>minimum</i> cell, not cell (0, 0).
        /// </summary>
        public LandMinimapStats Stats;

        /// <summary>Where the player is, for <c>plTag</c>.</summary>
        public MinimapPlayerMarker Marker;

        /// <summary>AS3 <c>World.w.drawAllMap</c> — the console's reveal-everything flag.</summary>
        public bool RevealAll;

        /// <summary>
        /// The player's position in the image's own texel space, as a tile coordinate — the value
        /// <c>PipPageInfo</c> computes as <c>ggX / World.tileX</c> (<c>Land.as:1535-1536</c>).
        ///
        /// <para><b>x is not mirrored; y is.</b> The buffer is laid out Unity-first: <c>TexelX</c> grows
        /// right with no flip, while <c>TexelY</c> puts a room's floor row at the <i>bottom</i> of its
        /// band. The marker is placed in a rect whose pivot is the image's top-left, so its y has to be
        /// counted downward from the image's top — which is <c>height - 1 - texelRow</c>. Getting this
        /// wrong puts the marker at the correct column and the vertically mirrored row, which on a
        /// wide, short land looks like "the marker is a bit off" rather than like a flip.</para>
        ///
        /// <para>Returns <c>(-1, -1)</c> when there is no marker or no buffer, so a caller can branch on
        /// the sign instead of on a second flag.</para>
        /// </summary>
        public Vector2 TagTile()
        {
            if (Buffer == null || !Marker.Present) return new Vector2(-1f, -1f);

            var tile = WorldCoordinates.PixelToTile(Marker.RoomLocalPixels);

            int cellColumn = Marker.Cell.x - Stats.MinCell.x;
            int cellRowFromTop = Marker.Cell.y - Stats.MinCell.y;

            float x = LandMinimapBuffer.TexelX(cellColumn, tile.x, Buffer.TileWidth);

            int texelRow = LandMinimapBuffer.TexelY(
                Stats.MaxCell.y - Stats.MinCell.y + 1, cellRowFromTop, tile.y, Buffer.TileHeight);

            return new Vector2(x, Buffer.Height - 1 - texelRow);
        }
    }

    /// <summary>
    /// Everything <b>page 3</b> reads, as plain data — the port of the five branches of
    /// <c>PipPageInfo.setSubPages</c> (<c>:78-292</c>).
    ///
    /// <para><b>One object, not five reads.</b> The Oracle's <c>setSubPages</c> switches on
    /// <c>page2</c> and touches a different slice of the world in each branch. The port reads all of
    /// them and lets the page choose, because "which slice belongs to which sub-page" is exactly the
    /// rule that has to stay in the half that can run offline — the same split
    /// <c>PipStatSnapshot</c> uses.</para>
    ///
    /// <para><b>Every member is a field with a safe default.</b> A snapshot is assembled by a source
    /// that may be missing half its inputs (no campaign, no player, no AllData), and the page must then
    /// draw an empty list rather than throw. The three <c>Func</c> readers default to "nothing is
    /// known", which is the honest answer and is what <c>NullPipInfoSource</c> returns.</para>
    /// </summary>
    public sealed class PipInfoSnapshot
    {
        // ── Sub-page 2: the quest journal ─────────────────────────────────────

        /// <summary>AS3 <c>game.quests</c>, as (facts, live state) pairs. The page filters and sorts.</summary>
        public readonly List<PipQuestEntry> Quests = new List<PipQuestEntry>();

        /// <summary>AS3 <c>GameData.d.vendor.task</c> — the rows the <c>alltask</c> gate walks.</summary>
        public readonly List<PipVendorTaskFacts> VendorTasks = new List<PipVendorTaskFacts>();

        /// <summary>AS3 <c>World.w.loc.base</c> — the player is standing in a base land.</summary>
        public bool InBaseLand;

        // ── Sub-page 3: the travel map ────────────────────────────────────────

        /// <summary>AS3 <c>pip.travel</c> — travel is unlocked for the current land.</summary>
        public bool TravelUnlocked;

        /// <summary>AS3 <c>World.w.testMode</c> — opens the map and relaxes every gate.</summary>
        public bool TestMode;

        /// <summary>AS3 <c>World.w.helpMess</c> — the hint-marker master switch.</summary>
        public bool HelpMess;

        /// <summary>Every land the campaign knows, in catalogue order.</summary>
        public readonly List<PipLandFacts> Lands = new List<PipLandFacts>();

        /// <summary>AS3 <c>World.w.pers.level</c> — the recommended-level comparison and the vendor gate.</summary>
        public int PlayerLevel;

        /// <summary>
        /// AS3 <c>World.w.pers.speedShtr</c> — how badly the player's shooting is impaired. The travel
        /// hover pane appends a red warning at 1, 2 and 3 and a hint line at any value above zero
        /// (<c>PipPageInfo.as:399-421</c>).
        /// </summary>
        public int SpeedShooter;

        // ── Sub-page 4: the notes ─────────────────────────────────────────────

        /// <summary>AS3 <c>game.notes</c>, in the order the game added them.</summary>
        public readonly List<string> Notes = new List<string>();

        /// <summary>AS3 <c>World.w.pers.persName</c> — substituted for <c>&amp;lp</c>.</summary>
        public string PlayerName = "";

        // ── Sub-page 5: the bestiary ──────────────────────────────────────────

        /// <summary>
        /// The parsed <c>&lt;all&gt;</c> root of <c>AllData</c>, or <c>null</c>.
        ///
        /// <para><b>The raw document, not the typed assets, and that is forced.</b> <c>infoUnit</c> asks
        /// <i>whether an attribute exists</i> — <c>if(v_armor)</c> is a presence test on the attribute
        /// string, so <c>armor='0'</c> and an absent <c>armor</c> are a whole line apart on screen.
        /// <c>UnitDefinition</c>'s fields are typed with defaults, which makes the two indistinguishable.
        /// <c>LootRuntime</c> already loads this document at runtime (<c>LootRuntime.cs:159</c>) and
        /// <c>AllDataXml.TryRead</c> already extracts the literal.</para>
        /// </summary>
        public XElement AllDataRoot;

        // ── Sub-page 1: the local map ─────────────────────────────────────────

        /// <summary>The minimap's inputs.</summary>
        public PipMinimapFacts Map = new PipMinimapFacts();

        // ── Live readers ──────────────────────────────────────────────────────

        /// <summary>
        /// AS3 <c>World.w.game.triggers[name]</c>, coerced to a number — AS3's own <c>int()</c> coercion,
        /// so a stored <c>"-1"</c> reads -1 and a non-numeric string reads 0. <c>null</c> means "no
        /// trigger table", which reads as 0 for every name.
        /// </summary>
        public Func<string, int> TriggerOf;

        /// <summary>
        /// <c>Pers.skill[id]</c> — the player's level in a skill, for the vendor gate's skill arm.
        /// <c>null</c> reads 0.
        /// </summary>
        public Func<string, int> SkillOf;

        /// <summary>
        /// <c>World.w.game.triggers["frag_" + id]</c> for the bestiary's kill count.
        ///
        /// <para><b>Return a negative number for "the trigger is absent".</b> The Oracle's guard is
        /// <c>triggers["frag_"+id] &gt;= 0</c>, and <c>undefined &gt;= 0</c> is false — so an absent
        /// trigger and a stored <c>0</c> are different answers, and collapsing them would print a count
        /// of zero on every creature the player has never met. <c>null</c> reads -1, i.e. absent.</para>
        /// </summary>
        public Func<string, int> FragCountOf;

        /// <summary>
        /// A quest's live state, or a <b>negative</b> number for "the campaign has never heard of it".
        /// The Oracle's gate tests <c>q == null || q.state == 0</c> and both answers are the same
        /// answer here — see <see cref="PipQuestJournal.HasAllTask"/>. <c>null</c> reads -1.
        /// </summary>
        public Func<string, int> QuestStateOf;

        /// <summary>The trigger value for a name, with the "no table" default applied.</summary>
        public int Trigger(string name) => TriggerOf != null ? TriggerOf(name) : 0;

        /// <summary>The skill level for an id, with the "no table" default applied.</summary>
        public int Skill(string id) => SkillOf != null ? SkillOf(id) : 0;

        /// <summary>The <c>frag_</c> kill count for a unit, with the "absent" default applied.</summary>
        public int FragCount(string unitId) =>
            FragCountOf != null ? FragCountOf("frag_" + unitId) : -1;

        /// <summary>A quest's state, with the "unknown quest" default applied.</summary>
        public int QuestState(string questId) => QuestStateOf != null ? QuestStateOf(questId) : -1;

        /// <summary>A land's facts by id, or <c>null</c> — the Oracle's <c>game.lands[id]</c>.</summary>
        public PipLandFacts FindLand(string landId)
        {
            if (string.IsNullOrEmpty(landId)) return null;
            for (int i = 0; i < Lands.Count; i++)
            {
                if (Lands[i] != null && string.Equals(Lands[i].LandId, landId, StringComparison.Ordinal))
                {
                    return Lands[i];
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Where page 3's five sub-tabs read from. An interface so the page can be built and every one of
    /// its rules exercised against a hand-made snapshot — no campaign, no player, no
    /// <c>ScriptableObject</c>.
    /// </summary>
    public interface IPipInfoSource
    {
        /// <summary>Everything page 3 reads, as plain data. Never null.</summary>
        PipInfoSnapshot Read();
    }

    /// <summary>
    /// A source with nothing behind it — the state the page is in before the campaign exists, and the
    /// state every fixture starts from.
    ///
    /// <para><b>It returns a snapshot, not <c>null</c>.</b> The page's rebuild path reads the snapshot
    /// unconditionally; a null would put a null-check on the hot path and, worse, would make "the source
    /// is missing" and "the world is empty" the same input. Returning an empty snapshot makes the page
    /// draw five empty lists, which is exactly what a campaign with no quests, notes or lands should
    /// look like.</para>
    /// </summary>
    public sealed class NullPipInfoSource : IPipInfoSource
    {
        /// <summary>A fresh empty snapshot. A new one per read, so a caller that mutates it cannot
        /// poison the next read.</summary>
        public PipInfoSnapshot Read() => new PipInfoSnapshot();
    }
}
