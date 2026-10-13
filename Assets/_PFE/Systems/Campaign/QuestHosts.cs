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
    /// </summary>
    public interface IQuestRewardHost
    {
        /// <param name="questId">The quest that closed — for the log line the implementation writes.</param>
        /// <param name="xp">AS3 <c>@xp</c>.</param>
        /// <param name="sp">AS3 <c>@sp</c> — skill points.</param>
        /// <param name="reputation">AS3 <c>@rep</c>.</param>
        void GrantQuestRewards(string questId, int xp, int sp, int reputation);
    }

    /// <summary>
    /// Runs an authored campaign script by id — AS3 <c>Game.runScript(id, owner)</c>
    /// (<c>Game.as:722-737</c>).
    ///
    /// <para>A quest's <c>endscr</c> is the caller that matters (<c>Quest.as:704-707</c>), and the same
    /// seam is what an event hook (a script that runs because a trigger changed) will use. Kept separate
    /// from <see cref="QuestRewardHost"/> because the two have different implementers: rewards need the
    /// player, a script needs the script runner.</para>
    /// </summary>
    public interface IQuestScriptHost
    {
        /// <summary>Run the authored script with this id. Returns <c>false</c> when no such script exists,
        /// so the caller can report a typo rather than let it read as a script that ran and did nothing.
        /// </summary>
        bool RunAuthoredScript(string scriptId);
    }
}
