using System.Collections.Generic;

namespace PFE.Systems.Campaign
{
    /// <summary>
    /// Where a quest's payout goes when it closes.
    ///
    /// <para><b>Why this is an interface and not a direct call.</b> AS3 pays a quest from inside
    /// <c>Quest.close</c>, reaching straight into <c>World.w.pers.addSkillPoint</c>, <c>expa</c> and
    /// <c>rep</c> (<c>Quest.as:645-656</c>). The port has no campaign→character-stat seam, and inventing
    /// one inside <c>CampaignManager</c> would make the whole quest decision table untestable. Delegating
    /// keeps <c>QuestRules</c> a pure function and lets a fixture assert the payout without a player.</para>
    ///
    /// <para><b>An unwired host is reported, not silent.</b> A quest that pays nothing looks exactly like
    /// a quest whose rewards are zero, which is why <c>CampaignManager.ApplyQuestClose</c> warns by name
    /// when this is null and a payout was owed.</para>
    ///
    /// <para><b>Three methods, because <c>close()</c> has three payout shapes.</b> The scalars
    /// (<c>xp</c>/<c>sp</c>/<c>rep</c>, <c>:645-656</c>), the <c>&lt;reward&gt;</c> grants
    /// (<c>:661-689</c>) and the <c>del</c> withdrawals (<c>:613-643</c>) all land in the same two
    /// places — the character stats and the inventory — so splitting them across three interfaces would
    /// be three seams with one implementation. They are separate <i>methods</i> rather than one, because
    /// each has a different reason to fail and a different thing to report.</para>
    /// </summary>
    public interface IQuestRewardHost
    {
        /// <param name="questId">The quest that closed — for the log line the implementation writes.</param>
        /// <param name="xp">AS3 <c>@xp</c>.</param>
        /// <param name="sp">AS3 <c>@sp</c> — skill points.</param>
        /// <param name="reputation">AS3 <c>@rep</c>.</param>
        void GrantQuestRewards(string questId, int xp, int sp, int reputation);

        /// <summary>
        /// Grants the quest's <c>&lt;reward&gt;</c> items — AS3 <c>World.w.invent.take(item, 2)</c>
        /// (<c>Quest.as:661-676</c>). Rows carrying only a trigger are skipped here; the trigger half is
        /// applied by <c>CampaignManager</c>, which owns the trigger table.
        /// </summary>
        void GrantQuestItems(string questId, IReadOnlyList<QuestReward> rewards);

        /// <summary>
        /// Removes the collected items of every <c>del</c> stage — AS3 <c>minusItem</c> / <c>remWeapon</c>
        /// (<c>Quest.as:613-643</c>). <see cref="QuestWithdrawal.ColTip"/> selects which of the two.
        /// </summary>
        void WithdrawQuestItems(string questId, IReadOnlyList<QuestWithdrawal> withdrawals);
    }

    /// <summary>
    /// Runs an authored campaign script by id — AS3 <c>Game.runScript(id, owner)</c>
    /// (<c>Game.as:722-737</c>).
    ///
    /// <para>A quest's <c>endscr</c> is the caller that matters (<c>Quest.as:704-707</c>), and the same
    /// seam is what an event hook (a script that runs because a trigger changed) will use. Kept separate
    /// from <see cref="IQuestRewardHost"/> because the two have different implementers: rewards need the
    /// player, a script needs the script runner.</para>
    ///
    /// <para><b>It has no quest-side caller, and that is a measured fact rather than an assumption.</b>
    /// <c>endscr</c> occurs <b>zero</b> times across every <c>&lt;quest&gt;</c> in <c>GameData.as</c> —
    /// the attribute vocabulary of the shipped quests is <c>id</c>, <c>rep</c>, <c>empl</c>, <c>xp</c>,
    /// <c>main</c>, <c>enddial</c>, <c>begdial</c>, <c>trigger</c>, <c>triggerset</c>, <c>sp</c> and
    /// <c>cid</c>, and <c>endscr</c> is not among them. So the null registration is correct today, and a
    /// future authored one cannot go silent: <c>CampaignManager.ApplyQuestClose</c> reports by name when
    /// this is null and an <c>endscr</c> was owed. (Compare <c>Quest.as:132-135</c>, which does read it —
    /// the field is live in the Oracle, it is simply unused by the data.)</para>
    /// </summary>
    public interface IQuestScriptHost
    {
        /// <summary>Run the authored script with this id. Returns <c>false</c> when no such script exists,
        /// so the caller can report a typo rather than let it read as a script that ran and did nothing.
        /// </summary>
        bool RunAuthoredScript(string scriptId);
    }
}
