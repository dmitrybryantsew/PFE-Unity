using System;
using System.Collections.Generic;

namespace PFE.Systems.Campaign
{
    /// <summary>
    /// AS3 <c>Quest.state</c> (<c>Quest.as:71</c>). Three values, and the port used to conflate the
    /// first two: a quest the game has never heard of and a quest that is in progress are different
    /// answers, and <c>closeQuest</c> branches on exactly that difference (<c>Game.as:620</c>).
    /// </summary>
    public enum QuestState
    {
        /// <summary>Known but not begun. <c>addQuest(noVis:true)</c> parks a quest here
        /// (<c>Game.as:562-565</c>), and <c>showQuest</c>/<c>closeQuest</c> promote it.</summary>
        NotStarted = 0,

        /// <summary>In progress.</summary>
        Active = 1,

        /// <summary>Done. <c>close()</c> is idempotent on this (<c>Quest.as:608-611</c>).</summary>
        Done = 2,
    }

    /// <summary>
    /// The immutable half of one sub-objective — a projection of the authored
    /// <c>&lt;q&gt;</c> row (<c>Quest.as:85-269</c>), so the rules below can run with no
    /// <c>ScriptableObject</c> in sight.
    ///
    /// <para><b>Why a projection and not the asset.</b> Unity cannot construct a <c>ScriptableObject</c>
    /// outside the editor, so a rule written against <c>QuestStageDefinition</c> is untestable offline.
    /// This is the same split <c>TravelMapModel</c> uses for <c>LandDefinition</c>
    /// (<c>TravelMapModel.cs:103-123</c>), and for the same reason.</para>
    /// </summary>
    public sealed class QuestSubFacts
    {
        /// <summary>AS3 <c>&lt;q id="…"&gt;</c> — a <b>string</b>, and the key <c>closeSub</c> looks up
        /// (<c>Quest.as:579</c>). The port stores it as the stage's index rendered as a string.</summary>
        public string SubId = "";

        /// <summary>AS3 <c>nn</c> — an optional sub. The parent auto-closes without it
        /// (<c>Quest.as:426</c>).</summary>
        public bool Optional;

        /// <summary>AS3 <c>result</c> — revealed only once every earlier sub is done
        /// (<c>isResult</c>, <c>Quest.as:549-575</c>).</summary>
        public bool IsResult;

        /// <summary>AS3 <c>invis</c> — hidden from the journal until <c>showSub</c>.</summary>
        public bool Hidden;

        /// <summary>AS3 <c>collect</c> — the item id this sub counts.</summary>
        public string Collect = "";

        /// <summary>AS3 <c>kol</c> — how many are needed.</summary>
        public int Kol = 1;

        /// <summary>AS3 <c>give</c> — the sub is a hand-in, not a pickup.</summary>
        public string Give = "";

        /// <summary>AS3 <c>del</c> — the collected items are removed when the parent closes
        /// (<c>Quest.as:617-642</c>).</summary>
        public bool DeleteOnClose;

        /// <summary>AS3 <c>coltip</c> — 1 means the target is a weapon rather than an item
        /// (<c>Quest.as:396-410</c>).</summary>
        public int ColTip;
    }

    /// <summary>
    /// The immutable half of one quest — the projection the rules run on. See
    /// <see cref="QuestSubFacts"/> for why the asset itself cannot be used.</summary>
    public sealed class QuestFacts
    {
        public string QuestId = "";
        public string Title = "";
        public List<QuestSubFacts> Subs = new List<QuestSubFacts>();

        /// <summary>AS3 <c>xp</c> / <c>sp</c> / <c>rep</c> — paid on close (<c>Quest.as:645-656</c>).</summary>
        public int Xp;
        public int Sp;
        public int Rep;

        /// <summary>AS3 <c>trigger</c> + <c>triggerset</c> — written on close
        /// (<c>Quest.as:657-660</c>). The set value defaults to <c>"1"</c> (<c>:214</c>).</summary>
        public string Trigger = "";
        public string TriggerSet = "1";

        /// <summary>AS3 <c>enddial</c> — played on close when dialogue is on
        /// (<c>Quest.as:699-703</c>).</summary>
        public string EndDial = "";

        /// <summary>AS3 <c>endscr</c> — an authored script run on close (<c>Quest.as:704-707</c>).</summary>
        public string EndScript = "";

        /// <summary>AS3 <c>begdial</c> — played when the quest is granted (<c>Game.as:575-579</c>).</summary>
        public string BegDial = "";

        /// <summary>AS3 <c>auto</c> — when false, <c>addQuest(noVis)</c> parks the quest at state 0
        /// (<c>Game.as:562-565</c>).</summary>
        public bool Auto = true;

        /// <summary>AS3 <c>main</c> — a mainline quest. Not a gate; the journal uses it.</summary>
        public bool IsMain = true;

        /// <summary>AS3 <c>isCheck</c> — whether any sub counts an item, which is what makes
        /// <c>checkQuests</c>/<c>incQuests</c> visit this quest at all (<c>Game.as:641</c>).</summary>
        public bool IsCheck;

        /// <summary>AS3 <c>@next</c> — the quest granted when this one closes.</summary>
        public string NextQuestId = "";

        public QuestSubFacts FindSub(string subId)
        {
            if (string.IsNullOrEmpty(subId) || Subs == null) return null;
            for (int i = 0; i < Subs.Count; i++)
            {
                QuestSubFacts sub = Subs[i];
                if (sub != null && string.Equals(sub.SubId, subId, StringComparison.Ordinal)) return sub;
            }

            return null;
        }

        /// <summary>Index of a sub, or -1. <c>isResult</c> needs the position, not just the node.</summary>
        public int IndexOfSub(string subId)
        {
            if (string.IsNullOrEmpty(subId) || Subs == null) return -1;
            for (int i = 0; i < Subs.Count; i++)
            {
                if (Subs[i] != null && string.Equals(Subs[i].SubId, subId, StringComparison.Ordinal)) return i;
            }

            return -1;
        }
    }

    /// <summary>The mutable half of one sub-objective — AS3 <c>Quest</c>'s per-node fields.</summary>
    public sealed class QuestSubState
    {
        public string SubId = "";
        public int State;
        public int Est;
        public int Gived;
        public bool Invis;
    }

    /// <summary>
    /// The mutable half of one quest. AS3 keeps this on the <c>Quest</c> instance it rebuilds from XML on
    /// every load; the port keeps it beside the (immutable, cached) <see cref="QuestFacts"/>.
    /// </summary>
    public sealed class QuestRuntimeState
    {
        public string QuestId = "";
        public int State;
        public List<QuestSubState> Subs = new List<QuestSubState>();

        public QuestSubState FindSub(string subId)
        {
            if (string.IsNullOrEmpty(subId)) return null;
            for (int i = 0; i < Subs.Count; i++)
            {
                QuestSubState sub = Subs[i];
                if (sub != null && string.Equals(sub.SubId, subId, StringComparison.Ordinal)) return sub;
            }

            return null;
        }

        public static QuestRuntimeState CreateFrom(QuestFacts facts, QuestState state)
        {
            var runtime = new QuestRuntimeState
            {
                QuestId = facts?.QuestId ?? "",
                State = (int)state,
            };

            if (facts?.Subs != null)
            {
                for (int i = 0; i < facts.Subs.Count; i++)
                {
                    QuestSubFacts sub = facts.Subs[i];
                    if (sub == null) continue;
                    runtime.Subs.Add(new QuestSubState
                    {
                        SubId = sub.SubId,
                        // AS3's Quest constructor sets `state = 1` on every node, parent and sub alike
                        // (Quest.as:109), and `invis` comes from the authored `@invis` (Quest.as:177-184).
                        State = (int)QuestState.Active,
                        Invis = sub.Hidden,
                    });
                }
            }

            return runtime;
        }
    }

    /// <summary>
    /// What a close did, so the caller can apply the side effects through its own seams.
    ///
    /// <para><b>Why an outcome rather than doing the work here.</b> The oracle's <c>close()</c> reaches
    /// straight into <c>World.w.invent</c>, <c>World.w.pers</c>, <c>World.w.gui</c> and <c>Snd</c>
    /// (<c>Quest.as:603-708</c>) — none of which exist as injectable seams in the port, and all of which
    /// would make this class untestable. The rules decide; <c>CampaignManager</c> applies. That is the
    /// same split <c>CheckpointRules</c> already uses, and it is what keeps the whole decision table
    /// runnable on the offline wall.</para>
    /// </summary>
    public struct QuestCloseOutcome
    {
        /// <summary>Whether anything actually closed (AS3 <c>close()</c> early-returns on state 2).</summary>
        public bool Closed;

        /// <summary>True when the parent closed, false when only a sub did.</summary>
        public bool IsParent;

        /// <summary>The parent quest that closed, or the parent of the sub that closed.</summary>
        public string QuestId;

        /// <summary>The sub that closed, when <see cref="IsParent"/> is false.</summary>
        public string SubId;

        /// <summary>Rewards to pay — AS3 <c>Quest.as:645-656</c>. Only the parent ever carries these.</summary>
        public int Xp;
        public int Sp;
        public int Rep;

        /// <summary>Trigger to write — AS3 <c>Quest.as:657-660</c>.</summary>
        public string Trigger;
        public string TriggerSet;

        /// <summary>Dialogue to play on close — AS3 <c>Quest.as:699-703</c>.</summary>
        public string EndDial;

        /// <summary>Authored script to run on close — AS3 <c>Quest.as:704-707</c>.</summary>
        public string EndScript;

        /// <summary>The chained quest to grant — AS3 <c>@next</c>.</summary>
        public string NextQuestId;
    }

    /// <summary>
    /// The quest decision table — a port of <c>fe.loc.Quest</c>'s state machine and of the
    /// <c>Game.addQuest</c> / <c>showQuest</c> / <c>closeQuest</c> wrappers that drive it
    /// (<c>Game.as:527-632</c>).
    ///
    /// <para><b>The model this replaces was wrong in a way that mattered.</b> The port tracked
    /// <c>Dictionary&lt;string,int&gt;</c> — one monotonic index per quest — and exposed
    /// <c>AdvanceQuestStage(questId, nextStage)</c>. The oracle's <c>stage val n</c> means
    /// <b>"mark sub-objective <c>n</c> complete"</b> (<c>Script.as:420-423</c> → <c>Game.closeQuest</c>
    /// <c>:613-632</c> → <c>Quest.closeSub</c> <c>:577-592</c>), which is a different operation in three
    /// observable ways:</para>
    /// <list type="number">
    /// <item>an index cannot express "close sub 3 while 1 and 2 stay open", which the <c>nn</c> optional
    /// subs make routine;</item>
    /// <item>an index never auto-completes the parent — the oracle's <c>isClosed()</c> does
    /// (<c>Quest.as:532-547</c>), and it is what makes <c>stage</c> on the last sub finish the quest;</item>
    /// <item>an index cannot go backwards, and <c>closeQuest</c> with an absent or negative <c>n</c>
    /// completes the <b>whole</b> quest (<c>Game.as:624-627</c>) — a case the old API could not express
    /// at all.</item>
    /// </list>
    ///
    /// <para>Everything here is a pure function of (facts, runtime) so it runs offline.</para>
    /// </summary>
    public static class QuestRules
    {
        /// <summary>
        /// AS3 <c>Quest.isClosed()</c> (<c>Quest.as:532-547</c>): the parent closes when <b>every</b> sub
        /// has reached state 2.
        ///
        /// <para><b>Note what this does not consult.</b> Unlike <c>check()</c>, <c>isClosed</c> ignores
        /// <c>nn</c> entirely — it requires every sub, optional ones included. That asymmetry is the
        /// oracle's, and it is why <c>closeQuest(id)</c> with no <c>n</c> is the way a quest with an
        /// untaken optional objective is finished.</para>
        /// </summary>
        public static bool IsClosed(QuestRuntimeState runtime)
        {
            if (runtime?.Subs == null) return true;

            for (int i = 0; i < runtime.Subs.Count; i++)
            {
                QuestSubState sub = runtime.Subs[i];
                if (sub != null && sub.State < (int)QuestState.Done) return false;
            }

            return true;
        }

        /// <summary>
        /// AS3 <c>Quest.isResult()</c> (<c>Quest.as:549-575</c>): un-hide each <c>result</c> sub whose
        /// <b>earlier</b> siblings are all done.
        ///
        /// <para>The loop is a plain "any earlier sub not done ⇒ keep hidden", so the reveal is
        /// positional — a result sub gates on everything <i>before</i> it, never after.</para>
        /// </summary>
        /// <returns>The sub ids this call revealed, or an empty list.</returns>
        public static List<string> IsResult(QuestFacts facts, QuestRuntimeState runtime)
        {
            var revealed = new List<string>();
            if (facts?.Subs == null || runtime?.Subs == null) return revealed;

            for (int i = 0; i < facts.Subs.Count; i++)
            {
                QuestSubFacts factsSub = facts.Subs[i];
                if (factsSub == null || !factsSub.IsResult) continue;

                QuestSubState stateSub = runtime.FindSub(factsSub.SubId);
                if (stateSub == null) continue;

                bool allEarlierDone = true;
                for (int earlier = 0; earlier < i; earlier++)
                {
                    QuestSubState previous = runtime.FindSub(facts.Subs[earlier]?.SubId);
                    if (previous == null) continue;
                    if (previous.State < (int)QuestState.Done) allEarlierDone = false;
                }

                if (allEarlierDone && stateSub.Invis)
                {
                    stateSub.Invis = false;
                    revealed.Add(stateSub.SubId);
                }
            }

            return revealed;
        }

        /// <summary>AS3 <c>Quest.showSub(id)</c> (<c>Quest.as:594-601</c>).</summary>
        /// <returns><c>true</c> when a sub was actually un-hidden.</returns>
        public static bool ShowSub(QuestFacts facts, QuestRuntimeState runtime, string subId)
        {
            if (runtime == null || string.IsNullOrEmpty(subId)) return false;
            if (runtime.State == (int)QuestState.Done) return false;

            QuestSubState sub = runtime.FindSub(subId);
            if (sub == null) return false;

            if (!sub.Invis) return false;
            sub.Invis = false;
            return true;
        }

        /// <summary>
        /// AS3 <c>Quest.closeSub(id)</c> (<c>Quest.as:577-592</c>), including the two follow-ups that make
        /// it more than "set one node to 2": the result reveal, and the parent auto-close.
        /// </summary>
        public static QuestCloseOutcome CloseSub(QuestFacts facts, QuestRuntimeState runtime, string subId)
        {
            var outcome = new QuestCloseOutcome();

            if (facts == null || runtime == null) return outcome;

            // `if(this.state == 2 || param1 == null || param1 == "" || this.subsId[param1] == null) return;`
            if (runtime.State == (int)QuestState.Done) return outcome;
            if (string.IsNullOrEmpty(subId)) return outcome;

            QuestSubState sub = runtime.FindSub(subId);
            if (sub == null) return outcome;

            outcome.QuestId = facts.QuestId;
            outcome.SubId = subId;

            if (sub.State < (int)QuestState.Done)
            {
                sub.State = (int)QuestState.Done;
                outcome.Closed = true;
            }

            if (facts.Subs != null && HasResultSub(facts))
            {
                IsResult(facts, runtime);
            }

            // `if(this.state == 1) this.isClosed();` — a quest already at state 2 was returned above, and
            // one at state 0 stays parked: closing a sub of an unstarted quest must not start it.
            if (runtime.State == (int)QuestState.Active && IsClosed(runtime))
            {
                QuestCloseOutcome parent = CloseParent(facts, runtime);
                // Merge: the caller wants one outcome, and the parent's rewards are the ones that pay.
                outcome.Closed = outcome.Closed || parent.Closed;
                outcome.IsParent = parent.IsParent;
                outcome.Xp = parent.Xp;
                outcome.Sp = parent.Sp;
                outcome.Rep = parent.Rep;
                outcome.Trigger = parent.Trigger;
                outcome.TriggerSet = parent.TriggerSet;
                outcome.EndDial = parent.EndDial;
                outcome.EndScript = parent.EndScript;
                outcome.NextQuestId = parent.NextQuestId;
            }

            return outcome;
        }

        private static bool HasResultSub(QuestFacts facts)
        {
            for (int i = 0; i < facts.Subs.Count; i++)
            {
                if (facts.Subs[i] != null && facts.Subs[i].IsResult) return true;
            }

            return false;
        }

        /// <summary>
        /// AS3 <c>Quest.close()</c> (<c>Quest.as:603-708</c>) — mark done and compute the payout.
        ///
        /// <para><b>What is deliberately not computed here.</b> The <c>isDel</c> item withdrawal and the
        /// <c>&lt;reward&gt;</c> item grants both need the inventory (<c>Quest.as:613-643</c>,
        /// <c>:661-689</c>), and the port has no "an item changed" notification to hang them off. They are
        /// the one part of this file with an external dependency, and they are reported by the caller
        /// rather than silently skipped — see <c>CampaignManager.ApplyQuestClose</c>.</para>
        /// </summary>
        public static QuestCloseOutcome CloseParent(QuestFacts facts, QuestRuntimeState runtime)
        {
            var outcome = new QuestCloseOutcome();
            if (facts == null || runtime == null) return outcome;

            // `if(this.state == 2) return;`
            if (runtime.State == (int)QuestState.Done) return outcome;

            runtime.State = (int)QuestState.Done;

            outcome.Closed = true;
            outcome.IsParent = true;
            outcome.QuestId = facts.QuestId;
            outcome.Xp = facts.Xp;
            outcome.Sp = facts.Sp;
            outcome.Rep = facts.Rep;
            outcome.Trigger = facts.Trigger;
            outcome.TriggerSet = facts.TriggerSet;
            outcome.EndDial = facts.EndDial;
            outcome.EndScript = facts.EndScript;
            outcome.NextQuestId = facts.NextQuestId;
            return outcome;
        }

        /// <summary>
        /// AS3 <c>Game.closeQuest(id, n)</c> (<c>Game.as:613-632</c>): with an absent, empty or negative
        /// <c>n</c> the <b>whole</b> quest closes; otherwise only that sub.
        ///
        /// <para><b>The negative case is the one the old port could not express.</b> <c>int(param2) &lt; 0</c>
        /// closes everything, and <c>n</c> arrives as a string from <c>analiz</c> — so <c>"-1"</c> is the
        /// documented default an authored <c>&lt;s act="stage" val="x"/&gt;</c> carries
        /// (<c>Script.as:71</c>), and a bare <c>stage</c> therefore <i>completes</i> the quest rather than
        /// advancing it. The port's <c>AdvanceQuestStage(questId, 0)</c> did the opposite.</para>
        /// </summary>
        public static QuestCloseOutcome CloseQuest(QuestFacts facts, QuestRuntimeState runtime, string n)
        {
            var outcome = new QuestCloseOutcome();
            if (facts == null || runtime == null) return outcome;

            if (runtime.State == (int)QuestState.Done) return outcome;

            if (string.IsNullOrEmpty(n) || ParseIntOrDefault(n, -1) < 0)
            {
                return CloseParent(facts, runtime);
            }

            return CloseSub(facts, runtime, n);
        }

        /// <summary>
        /// AS3 <c>Game.showQuest(id, n)</c> (<c>Game.as:583-611</c>): un-hide a sub of an in-progress
        /// quest. A quest that is done is left alone, and a quest that does not exist is granted first
        /// (the caller's job — <c>addQuest(id, null, noVis:true)</c>).
        /// </summary>
        public static bool ShowQuest(QuestFacts facts, QuestRuntimeState runtime, string n)
        {
            if (runtime == null || runtime.State == (int)QuestState.Done) return false;
            return ShowSub(facts, runtime, n);
        }

        /// <summary>
        /// AS3 <c>Game.addQuest(id)</c>'s state half (<c>Game.as:537-552</c>): a quest already known at
        /// state 0 is promoted to 1 and its auto-close is re-tested.
        /// </summary>
        /// <returns><c>true</c> when this call promoted the quest.</returns>
        public static bool Activate(QuestRuntimeState runtime)
        {
            if (runtime == null) return false;
            if (runtime.State != (int)QuestState.NotStarted) return false;

            runtime.State = (int)QuestState.Active;
            return true;
        }

        /// <summary>
        /// AS3 <c>Quest.inc(itemId, delta)</c> (<c>Quest.as:292-306</c>) — the collect counter.
        /// </summary>
        public static void Inc(QuestFacts facts, QuestRuntimeState runtime, string itemId, int delta)
        {
            if (facts?.Subs == null || runtime?.Subs == null || string.IsNullOrEmpty(itemId)) return;

            for (int i = 0; i < facts.Subs.Count; i++)
            {
                QuestSubFacts factsSub = facts.Subs[i];
                if (factsSub == null || !string.Equals(factsSub.Collect, itemId, StringComparison.Ordinal))
                {
                    continue;
                }

                QuestSubState sub = runtime.FindSub(factsSub.SubId);
                if (sub != null) sub.Est += delta;
            }
        }

        /// <summary>
        /// AS3 <c>Quest.check(itemId)</c> (<c>Quest.as:343-442</c>) — recompute a sub's progress from how
        /// many of the item the inventory actually holds, and auto-close the parent when every non-<c>nn</c>
        /// sub is done.
        ///
        /// <para><b>The parent rule here is <i>not</i> <c>isClosed()</c>.</b> <c>check</c> skips
        /// <c>nn</c> subs (<c>:426</c>) while <c>isClosed</c> does not (<c>:538</c>) — two different
        /// completion tests in the same class, and both are live. Getting them the same way round is the
        /// point of keeping them as two named methods.</para>
        /// </summary>
        /// <param name="countOf">How many of an item the inventory holds. A delegate rather than an
        /// inventory reference, so this stays Unity-free and testable.</param>
        /// <param name="changedItem">The item that changed, or null for "recheck everything".</param>
        /// <returns>The sub whose progress text changed, or null — AS3's return is the journal line to
        /// re-render.</returns>
        public static string Check(
            QuestFacts facts,
            QuestRuntimeState runtime,
            Func<string, int> countOf,
            string changedItem = null)
        {
            if (facts?.Subs == null || runtime?.Subs == null) return null;
            if (runtime.State == (int)QuestState.Done) return null;

            string changed = null;

            for (int i = 0; i < facts.Subs.Count; i++)
            {
                QuestSubFacts factsSub = facts.Subs[i];
                if (factsSub == null) continue;
                if (string.IsNullOrEmpty(factsSub.Collect)) continue;
                if (factsSub.ColTip != 0) continue; // weapon-collect subs are a separate arm (see below)

                QuestSubState sub = runtime.FindSub(factsSub.SubId);
                if (sub == null) continue;

                // `if(this.collect && this.colTip == 0 && this.gived < this.kol)`
                if (sub.Gived >= factsSub.Kol) continue;

                int held = countOf?.Invoke(factsSub.Collect) ?? 0;
                sub.Est = held + sub.Gived;
                if (sub.Est > factsSub.Kol) sub.Est = factsSub.Kol;

                bool wasDone = sub.State >= (int)QuestState.Done;

                if (string.IsNullOrEmpty(factsSub.Give))
                {
                    if (sub.Est >= factsSub.Kol)
                    {
                        sub.State = (int)QuestState.Done;
                        if (facts.Subs != null && HasResultSub(facts)) IsResult(facts, runtime);
                    }
                    else
                    {
                        // `canBeUse` is not modelled: no `<q us="1">` exists in the shipped quest data,
                        // and inventing the arm would make the state machine claim behaviour nothing
                        // exercises. A sub without it simply stays in progress.
                        sub.State = (int)QuestState.Active;
                    }
                }

                if (!string.IsNullOrEmpty(changedItem) &&
                    string.Equals(factsSub.Collect, changedItem, StringComparison.Ordinal))
                {
                    changed = factsSub.SubId;
                }

                // The oracle re-reads the count after the branch (`:391-394`), which can only differ if
                // the branch changed it — it does not. Kept out rather than copied blind.
                if (!wasDone && sub.State >= (int)QuestState.Done)
                {
                    changed ??= factsSub.SubId;
                }
            }

            // `if(_loc3_) this.close();` — every non-nn sub done ⇒ the parent closes.
            if (runtime.State == (int)QuestState.Active && AllRequiredSubsDone(facts, runtime))
            {
                CloseParent(facts, runtime);
            }

            return changed;
        }

        /// <summary>
        /// The <c>check</c> completion test: every sub that is <b>not</b> <c>nn</c> is done. Distinct from
        /// <see cref="IsClosed"/>, which requires the optional ones too.
        /// </summary>
        public static bool AllRequiredSubsDone(QuestFacts facts, QuestRuntimeState runtime)
        {
            if (facts?.Subs == null || runtime == null) return true;

            for (int i = 0; i < facts.Subs.Count; i++)
            {
                QuestSubFacts factsSub = facts.Subs[i];
                if (factsSub == null || factsSub.Optional) continue;

                QuestSubState sub = runtime.FindSub(factsSub.SubId);
                if (sub != null && sub.State < (int)QuestState.Done) return false;
            }

            return true;
        }

        /// <summary>Invariant-culture parse with a default, so a non-numeric <c>@n</c> cannot throw out of
        /// a script command. <c>int("-1")</c> in AS3 yields <c>-1</c>; <c>int("wait")</c> yields
        /// <c>0</c>, which is why the default is not applied on a successful-but-zero parse.</summary>
        public static int ParseIntOrDefault(string raw, int fallback)
        {
            if (string.IsNullOrEmpty(raw)) return fallback;
            return int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                                System.Globalization.CultureInfo.InvariantCulture, out int value)
                ? value
                : 0;
        }
    }
}
