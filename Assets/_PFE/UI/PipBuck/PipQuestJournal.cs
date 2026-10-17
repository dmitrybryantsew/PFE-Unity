using System;
using System.Collections.Generic;
using PFE.Systems.Campaign;

namespace PFE.UI.PipBuck
{
    /// <summary>
    /// One quest as the journal needs it: the immutable authored half plus the live runtime half.
    ///
    /// <para><b>Why both halves are passed in rather than looked up.</b> The projection is a pure
    /// function of the two, so a fixture can build a quest with any state without a
    /// <c>CampaignManager</c>, a catalogue or an asset database — the same reason
    /// <c>QuestFacts</c>/<c>QuestRuntimeState</c> are separate types in the first place.</para>
    /// </summary>
    public sealed class PipQuestEntry
    {
        public QuestFacts Facts;
        public QuestRuntimeState State;

        public PipQuestEntry() { }

        public PipQuestEntry(QuestFacts facts, QuestRuntimeState state)
        {
            Facts = facts;
            State = state;
        }
    }

    /// <summary>One row of the journal list — the port of <c>setStatItem</c>'s <c>page2 == 2</c> arm
    /// (<c>PipPageInfo.as:310-324</c>).</summary>
    public sealed class PipQuestRow
    {
        /// <summary>AS3 <c>param1.id.text</c> — the row's hidden id field, which is what
        /// <c>statInfo</c> reads back to resolve the detail pane (<c>PipPageInfo.as:367</c>).</summary>
        public string QuestId = "";

        /// <summary>The caption <b>before</b> the <c>(done)</c> suffix — the string the sort compares
        /// (<c>PipPageInfo.as:124</c>).</summary>
        public string Title = "";

        /// <summary>The caption actually drawn — <see cref="Title"/>, plus <c>" (done)"</c> when
        /// <see cref="State"/> is 2.</summary>
        public string Display = "";

        /// <summary>AS3 <c>main</c> — whether the main-quest marker (<c>mq</c>) is shown.</summary>
        public bool IsMain;

        /// <summary>AS3 <c>state</c> — 1 active, 2 done. Only these two reach the list.</summary>
        public int State;

        /// <summary>AS3 <c>sort</c> — <c>0</c> for a main quest, <c>1</c> otherwise
        /// (<c>PipPageInfo.as:116</c>). A second key, because <c>state</c> alone would interleave the
        /// two kinds.</summary>
        public int Sort;

        /// <summary>AS3 <c>nazv.alpha = mq.alpha = 0.4</c> when the quest is done
        /// (<c>PipPageInfo.as:317</c>).</summary>
        public bool Dim;

        /// <summary>The <c>mq</c> marker's frame — always <c>1</c> here, which is the main-quest
        /// frame (<c>PipPageInfo.as:314</c>; the note arm uses <c>ico + 1</c> instead).</summary>
        public int MarkerFrame = PipInfoFrame.MarkerMainQuestFrame;
    }

    /// <summary>
    /// The vendor-task row a <c>alltask</c> gate is evaluated against — AS3's
    /// <c>&lt;task&gt;</c> under a <c>&lt;vendor&gt;</c> (<c>GameData.as:2159</c>).
    /// </summary>
    public sealed class PipVendorTaskFacts
    {
        /// <summary>The quest id the task grants (<c>&lt;task id="…"&gt;</c>).</summary>
        public string TaskId = "";

        /// <summary>AS3 <c>@land</c> — the land the task requires. Empty means "no land gate".</summary>
        public string LandId = "";

        /// <summary>AS3 <c>@trigger</c> — a trigger that must read exactly <c>1</c>.</summary>
        public string Trigger = "";

        /// <summary>AS3 <c>@skill</c> — the skill id the task requires.</summary>
        public string Skill = "";

        /// <summary>AS3 <c>@skilln</c> — the level that skill must reach.</summary>
        public int SkillLevel;

        /// <summary>
        /// Whether <c>@skilln</c> was authored at all.
        ///
        /// <para><b>Presence, not value.</b> The Oracle's gate is
        /// <c>if(@skill.length() &amp;&amp; @skilln.length())</c> (<c>PipPage.as:1535</c>) — an authored
        /// <c>skilln='0'</c> is a real requirement ("skill present at level 0"), while an absent one
        /// disables the whole arm. A plain <c>int</c> field cannot tell the two apart.</para>
        /// </summary>
        public bool HasSkillLevel;

        /// <summary>
        /// AS3 <c>@man</c> — a manually-granted task.
        ///
        /// <para><b>The flag is read by one of the two loops and not the other, and that asymmetry is
        /// the Oracle's.</b> The button's visibility test does <b>not</b> check it
        /// (<c>PipPageInfo.as:128-140</c>), so a <c>man="1"</c> task that passes <c>checkQuest</c>
        /// raises the <c>alltask</c> button — but the grant loop skips it
        /// (<c>PipPageInfo.as:769-785</c>), so pressing the button does nothing for it. Eight shipped
        /// tasks carry <c>man="1"</c> (<c>GameData.as:2628-2694</c>). Ported as-is: it is the
        /// behaviour, not a bug to quietly repair, and a fixture pins it.</para>
        /// </summary>
        public bool Manual;
    }

    /// <summary>The land facts <see cref="PipQuestJournal.CheckVendorTask"/> reads.</summary>
    public sealed class PipTaskLandFacts
    {
        /// <summary>AS3 <c>land.access</c> — travel to it has been unlocked.</summary>
        public bool Access;

        /// <summary>AS3 <c>land.visited</c> — the player has been there.</summary>
        public bool Visited;

        /// <summary>AS3 <c>land.dif</c> — the land's recommended level.</summary>
        public int Difficulty;
    }

    /// <summary>
    /// Sub-tab 2 — the quest journal. The port of <c>PipPageInfo</c>'s <c>page2 == 2</c> build
    /// (<c>:106-142</c>), of <c>setStatItem</c>'s quest arm (<c>:310-324</c>), of
    /// <c>PipPage.infoQuest</c> (<c>PipPage.as:1308-1369</c>) and of <c>PipPage.checkQuest</c>
    /// (<c>PipPage.as:1513-1543</c>).
    ///
    /// <para><b>Unity-free, and that is the whole point.</b> Every rule below branches — a sort over
    /// three keys, an objective that is skipped, dimmed, replaced by <c>?????</c> or suffixed with a
    /// counter, and a vendor gate with three separate ways to refuse. A compile cannot check any of
    /// them, so they live in a plain class a shell can execute.</para>
    ///
    /// <para><b>Where the text comes from.</b> The Oracle does not read a quest's title, synopsis or
    /// objective captions from the game data — <c>Quest.as:225-252</c> looks each id up in the
    /// <b>text table</b>. The port follows that, so this class takes a <see cref="PipTextTable"/>
    /// rather than a set of pre-baked strings, and the three lookups below are the Oracle's own:</para>
    /// <list type="bullet">
    /// <item>a quest's title and synopsis are the <c>txt</c> entry named by the <b>quest id</b>
    /// (<c>razd</c> 0 and 1);</item>
    /// <item>an objective's caption is the <c>txt</c> entry named by <b>quest id + sub id
    /// concatenated</b> — <c>toExit</c> + <c>1</c> is <c>toExit1</c>;</item>
    /// <item>a missing objective caption falls back to <c>"[" + subId + "]"</c>, i.e. <c>[3]</c> and
    /// not <c>[toExit3]</c> — the Oracle uses <c>this.id</c>, the <i>sub's</i> own id, not the
    /// concatenated key (<c>Quest.as:236</c>, <c>:241</c>). One shipped objective hits this
    /// (<c>repairAutoDoc3</c>) so the quirk is visible in a real play-through.</item>
    /// </list>
    /// </summary>
    public static class PipQuestJournal
    {
        /// <summary>The objective caption's x inside the row template — <c>param1.nazv.x = 32</c>
        /// (<c>PipPageInfo.as:312</c>).</summary>
        public const float RowNameX = 32f;

        /// <summary>AS3 <c>param1.nazv.alpha = param1.mq.alpha = 0.4</c> for a finished quest
        /// (<c>PipPageInfo.as:317</c>).</summary>
        public const float DoneAlpha = 0.4f;

        /// <summary>
        /// The journal list — the port of <c>PipPageInfo.as:106-142</c>.
        ///
        /// <para><b>The sort is <c>sortOn(["state","sort","nazv"])</c>, ascending, so in-progress
        /// quests come first</b> (state 1 before 2), then mainline before side quests, then by title.
        /// The title compared is the <i>bare</i> caption, before the <c>(done)</c> suffix is appended —
        /// appending first would sort every finished quest under <c>"("</c> and reshuffle the list.</para>
        ///
        /// <para><b>Ordinal, and stable.</b> ActionScript's <c>sortOn</c> is case-sensitive unless the
        /// <c>CASEINSENSITIVE</c> option is passed, and it is not; and its stability is unspecified.
        /// The port uses an ordinal comparison and a stable sort, so two quests that tie on all three
        /// keys keep the order the caller supplied. That is a deliberate narrowing: the Oracle's
        /// tie-break is whatever Flash's sort happens to do, and a fixture cannot assert it.</para>
        ///
        /// <para><b>Every sort key is a single digit.</b> <c>state</c> is 1 or 2 and <c>sort</c> is 0
        /// or 1, so whether <c>sortOn</c> compares them as strings or as numbers is unobservable —
        /// which is worth knowing, because ActionScript's default is the string comparison and a
        /// reader who assumes numeric ordering would be right here by accident.</para>
        /// </summary>
        public static List<PipQuestRow> BuildList(IEnumerable<PipQuestEntry> quests, PipTextTable text)
        {
            var rows = new List<PipQuestRow>();
            if (quests == null) return rows;

            foreach (PipQuestEntry entry in quests)
            {
                if (entry?.Facts == null || entry.State == null) continue;

                // `if(q.state > 0)` — state 0 is a quest parked by `addQuest(noVis:true)` and is not
                // the journal's business (PipPageInfo.as:110).
                if (entry.State.State <= 0) continue;

                string title = TitleFor(entry.Facts, text);
                bool done = entry.State.State == (int)QuestState.Done;

                rows.Add(new PipQuestRow
                {
                    QuestId = entry.Facts.QuestId ?? "",
                    Title = title,
                    Display = done ? title + " (" + Pip(text, "done") + ")" : title,
                    IsMain = entry.Facts.IsMain,
                    State = entry.State.State,
                    Sort = entry.Facts.IsMain ? 0 : 1,
                    Dim = done,
                    MarkerFrame = PipInfoFrame.MarkerMainQuestFrame,
                });
            }

            StableSort(rows);
            return rows;
        }

        private static void StableSort(List<PipQuestRow> rows)
        {
            // Insertion sort: stable by construction, and the list is at most a few dozen rows. Using
            // List.Sort with a comparer would be shorter and would silently drop the stability the
            // doc comment above promises.
            for (int i = 1; i < rows.Count; i++)
            {
                PipQuestRow row = rows[i];
                int j = i - 1;
                while (j >= 0 && Compare(rows[j], row) > 0)
                {
                    rows[j + 1] = rows[j];
                    j--;
                }

                rows[j + 1] = row;
            }
        }

        /// <summary>The three-key comparison, in <c>sortOn</c>'s order.</summary>
        public static int Compare(PipQuestRow a, PipQuestRow b)
        {
            if (a == null) return b == null ? 0 : -1;
            if (b == null) return 1;

            int byState = a.State.CompareTo(b.State);
            if (byState != 0) return byState;

            int bySort = a.Sort.CompareTo(b.Sort);
            if (bySort != 0) return bySort;

            return string.CompareOrdinal(a.Title ?? "", b.Title ?? "");
        }

        /// <summary>
        /// A quest's title — <c>Quest.as:225-242</c>: the <c>txt</c> entry named by the quest id, its
        /// <c>&lt;n&gt;</c> child, falling back to <c>"[" + id + "]"</c>.
        ///
        /// <para>The Oracle's <c>nazv</c> is <b>not</b> the port's <c>QuestFacts.Title</c>, which the
        /// importer fills with a humanised id (<c>FormatTitle</c>). All 59 shipped quest ids resolve in
        /// the text table, so the two agree in practice — but only one of them is translated.</para>
        /// </summary>
        public static string TitleFor(QuestFacts facts, PipTextTable text)
        {
            if (facts == null) return "";
            string title = text?.TextInTable("txt", facts.QuestId, 0);
            return string.IsNullOrEmpty(title) ? "[" + facts.QuestId + "]" : title;
        }

        /// <summary>
        /// An objective's caption — <c>Quest.as:225-242</c> for a sub, where <c>pid</c> is the parent id
        /// concatenated with the sub's own id.
        /// </summary>
        public static string SubTitleFor(QuestFacts facts, QuestSubFacts sub, PipTextTable text)
        {
            if (facts == null || sub == null) return "";
            string title = text?.TextInTable("txt", facts.QuestId + sub.SubId, 0);

            // `this.nazv = "[" + this.id + "]"` — the SUB's id, not the concatenated key.
            return string.IsNullOrEmpty(title) ? "[" + sub.SubId + "]" : title;
        }

        /// <summary>
        /// A quest's synopsis — <c>Quest.as:243-252</c>. Note the fallback is the literal
        /// <c>"---"</c>, and that it is taken when the entry has <b>no</b> <c>&lt;info&gt;</c> child:
        /// an entry with an empty <c>&lt;info/&gt;</c> yields the empty string, not <c>"---"</c>, which
        /// is why this distinguishes "absent" from "empty" rather than testing
        /// <c>IsNullOrEmpty</c>.</summary>
        public static string InfoFor(QuestFacts facts, PipTextTable text)
        {
            if (facts == null) return "---";
            string info = text?.TextInTable("txt", facts.QuestId, 1);
            return info == null ? "---" : info;
        }

        /// <summary>
        /// The detail pane's body — the port of <c>PipPage.infoQuest</c>
        /// (<c>PipPage.as:1308-1369</c>).
        ///
        /// <para><b>Five independent branches per objective, and the Oracle's own nesting.</b> An
        /// objective is skipped when it is invisible and unfinished; numbered with a <i>display</i>
        /// counter that only advances for objectives that were actually drawn; its caption replaced by
        /// <c>?????</c> when hidden, unfinished and uncounted; given a <c>gived/kol</c> or
        /// <c>est/kol</c> counter when it collects items, with an extra <c>+est</c> when it is a
        /// hand-in; and the whole line wrapped in the <c>dark</c> span when done. The numbering is the
        /// part that is easy to get wrong — a counter incremented before the skip test drifts by one
        /// for every hidden objective in the quest.</para>
        /// </summary>
        /// <param name="facts">The quest's authored half.</param>
        /// <param name="state">Its live half — the per-objective <c>est</c>, <c>gived</c> and
        /// <c>invis</c> all come from here.</param>
        /// <param name="text">The text table, for the objective captions and the <c>nn</c> marker.</param>
        public static string BuildDetail(QuestFacts facts, QuestRuntimeState state, PipTextTable text)
        {
            if (facts == null) return "";

            var builder = new System.Text.StringBuilder();
            builder.Append(InfoFor(facts, text));

            // `if(_loc2_.empl) _loc3_ += "<br><br>" + Res.txt("u",_loc2_.empl);`
            if (!string.IsNullOrEmpty(facts.Empl))
            {
                builder.Append("<br><br>").Append(text?.Unit(facts.Empl) ?? facts.Empl);
            }

            builder.Append('\n');

            int number = 1;
            if (facts.Subs != null)
            {
                for (int i = 0; i < facts.Subs.Count; i++)
                {
                    QuestSubFacts sub = facts.Subs[i];
                    if (sub == null) continue;

                    QuestSubState subState = state?.FindSub(sub.SubId);

                    // `if(!(_loc5_.invis && _loc5_.state < 2))` — skipped, and skipped *before* the
                    // number advances.
                    bool invis = subState != null && subState.Invis;
                    int subStateValue = subState?.State ?? (int)QuestState.Active;
                    if (invis && subStateValue < (int)QuestState.Done) continue;

                    bool done = subStateValue == (int)QuestState.Done;
                    int est = subState?.Est ?? 0;
                    int gived = subState?.Gived ?? 0;

                    builder.Append('\n');
                    string line = PipPageMarkup.Yellow(number + ".") + " ";

                    // `if(_loc5_.hidden && _loc5_.state < 2 && _loc5_.est <= 0)`
                    if (sub.Hidden && !done && est <= 0)
                    {
                        line += "?????";
                    }
                    else
                    {
                        line += SubTitleFor(facts, sub, text);
                    }

                    if (!string.IsNullOrEmpty(sub.Collect) && sub.ColTip == 0)
                    {
                        if (!string.IsNullOrEmpty(sub.Give))
                        {
                            line += " (" + PipPageMarkup.Yellow(gived + "/" + sub.Kol) + ")";
                            if (est > 0 && !done)
                            {
                                line += " (" + PipPageMarkup.Yellow("+" + est) + ")";
                            }
                        }
                        else
                        {
                            line += " (" + PipPageMarkup.Yellow(est + "/" + sub.Kol) + ")";
                        }
                    }

                    if (sub.Optional)
                    {
                        line += " (" + Pip(text, "nn") + ")";
                    }

                    builder.Append(done ? PipPageMarkup.Dark(line) : line);
                    number++;
                }
            }

            return builder.ToString();
        }

        // ── The vendor gate ───────────────────────────────────────────────────

        /// <summary>
        /// <c>PipPage.checkQuest</c> (<c>PipPage.as:1513-1543</c>) — the three ways a vendor task can
        /// refuse to be offered.
        ///
        /// <para><b>It is not the same function as <c>CampaignManager.CheckQuests</c>.</b> That one
        /// counts collected items; this one gates a <i>vendor</i> row on a land, a trigger and a skill,
        /// and the two names collide in the Oracle's own source. Kept apart here.</para>
        /// </summary>
        /// <param name="task">The authored <c>&lt;task&gt;</c> row.</param>
        /// <param name="playerLevel">AS3 <c>World.w.pers.level</c>.</param>
        /// <param name="landOf">Resolves <c>@land</c>; return <c>null</c> for an unknown land, which
        /// refuses (<c>:1519-1522</c>).</param>
        /// <param name="triggerOf">Reads a trigger as a number — AS3's coercing <c>!= 1</c>, so a
        /// stored <c>"-1"</c> or a non-numeric string both refuse.</param>
        /// <param name="skillOf">Reads a skill level.</param>
        public static bool CheckVendorTask(
            PipVendorTaskFacts task,
            int playerLevel,
            Func<string, PipTaskLandFacts> landOf,
            Func<string, int> triggerOf,
            Func<string, int> skillOf)
        {
            if (task == null) return false;

            if (!string.IsNullOrEmpty(task.LandId))
            {
                PipTaskLandFacts land = landOf?.Invoke(task.LandId);

                // `if(_loc2_ == null) return false;` — a task naming a land the campaign does not have
                // refuses rather than passing. This is the one arm that fires on a *missing* input.
                if (land == null) return false;

                // `if(!_loc2_.access && !_loc2_.visited && World.w.pers.level < _loc2_.dif) return false;`
                // — three conditions ANDed, so an unlocked *or* visited land passes at any level, and a
                // locked, unvisited one passes once the player is at its difficulty. A land with
                // `dif='0'` therefore never gates.
                if (!land.Access && !land.Visited && playerLevel < land.Difficulty) return false;
            }

            if (!string.IsNullOrEmpty(task.Trigger))
            {
                // `if(World.w.game.triggers[param1.@trigger] != 1) return false;` — an exact value test,
                // not a presence test: a stored 0, -1 or "wait" all refuse.
                if ((triggerOf?.Invoke(task.Trigger) ?? 0) != 1) return false;
            }

            if (!string.IsNullOrEmpty(task.Skill) && task.HasSkillLevel)
            {
                if ((skillOf?.Invoke(task.Skill) ?? 0) < task.SkillLevel) return false;
            }

            return true;
        }

        /// <summary>
        /// Whether the <c>alltask</c> button appears — <c>PipPageInfo.as:126-141</c>.
        ///
        /// <para>Two conditions the Oracle applies in a specific order, and both matter: the player must
        /// be standing in a <b>base</b> land (<c>World.w.loc.base</c>), and some vendor task must pass
        /// <see cref="CheckVendorTask"/> <b>and</b> name a quest that is unknown or still at state 0.
        /// The loop <c>break</c>s at the first such task, so this is a boolean and not a count.</para>
        ///
        /// <para><b>This loop does not consult <c>@man</c></b>, while the grant loop does — see
        /// <see cref="PipVendorTaskFacts.Manual"/>. That is the Oracle's asymmetry, not an
        /// oversight here.</para>
        /// </summary>
        /// <param name="inBaseLand">AS3 <c>Boolean(World.w.loc) &amp;&amp; World.w.loc.base</c>.</param>
        /// <param name="questStateOf">The quest's state, or a negative number for "the campaign has
        /// never heard of it". The Oracle tests <c>q == null || q.state == 0</c>, and those are one
        /// answer here.</param>
        public static bool HasAllTask(
            bool inBaseLand,
            int playerLevel,
            IEnumerable<PipVendorTaskFacts> tasks,
            Func<string, int> questStateOf,
            Func<string, PipTaskLandFacts> landOf,
            Func<string, int> triggerOf,
            Func<string, int> skillOf)
        {
            if (!inBaseLand || tasks == null) return false;

            foreach (PipVendorTaskFacts task in tasks)
            {
                if (!CheckVendorTask(task, playerLevel, landOf, triggerOf, skillOf)) continue;

                int state = questStateOf?.Invoke(task.TaskId) ?? -1;
                if (state < 0 || state == (int)QuestState.NotStarted) return true;
            }

            return false;
        }

        /// <summary>
        /// The quests pressing the button actually grants — <c>PipPageInfo.as:767-785</c>.
        ///
        /// <para><b>Two differences from <see cref="HasAllTask"/>, both the Oracle's.</b> This loop
        /// grants <b>every</b> qualifying task rather than stopping at the first, and it skips
        /// <c>@man="1"</c> rows — so a manual task can raise the button and then grant nothing. Both
        /// are pinned by fixtures rather than smoothed over.</para>
        /// </summary>
        /// <returns>The quest ids to grant, in the order the vendor table lists them.</returns>
        public static List<string> TasksToGrant(
            IEnumerable<PipVendorTaskFacts> tasks,
            int playerLevel,
            Func<string, int> questStateOf,
            Func<string, PipTaskLandFacts> landOf,
            Func<string, int> triggerOf,
            Func<string, int> skillOf)
        {
            var grants = new List<string>();
            if (tasks == null) return grants;

            foreach (PipVendorTaskFacts task in tasks)
            {
                if (task == null || task.Manual) continue;
                if (!CheckVendorTask(task, playerLevel, landOf, triggerOf, skillOf)) continue;

                int state = questStateOf?.Invoke(task.TaskId) ?? -1;
                if (state < 0 || state == (int)QuestState.NotStarted) grants.Add(task.TaskId);
            }

            return grants;
        }

        private static string Pip(PipTextTable text, string id)
        {
            // `Res.pipText` synthesises `*pip_<id>` for a missing key, which is what the Oracle shows.
            return text?.Pip(id) ?? "*pip_" + id;
        }
    }
}
