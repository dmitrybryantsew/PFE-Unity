using System;
using System.Collections.Generic;
using PFE.Data.Definitions;
using PFE.Systems.Inventory;
using PFE.Systems.RPG;
using UnityEngine;

namespace PFE.Systems.Campaign
{
    /// <summary>
    /// The real <see cref="IQuestRewardHost"/> — the port of the three payout halves of
    /// <c>Quest.close</c> (<c>Quest.as:613-689</c>).
    ///
    /// <para><b>What this closes.</b> Until now the only registration was a null factory
    /// (<c>GameLifetimeScope.cs:305</c>), so every completed quest logged "owes N xp, but no
    /// IQuestRewardHost is wired, so the payout was dropped" and paid nothing. That is a whole
    /// progression axis — 19 quests carry <c>@xp</c>, 3 carry <c>@sp</c>, 40 carry <c>@rep</c> and 77
    /// <c>&lt;reward&gt;</c> rows exist across the shipped quests — reading as "these quests pay
    /// nothing".</para>
    ///
    /// <para><b>Resolution is per call, and that is load-bearing.</b> <c>PlayerController</c> and its
    /// <c>CharacterStats</c>/<c>PlayerInventory</c> components are scene objects: a respawn, a room
    /// rebuild or a scene reload hands the game different instances. Holding the first one found would
    /// leave quests paying a destroyed object, which throws nothing and changes nothing — the exact
    /// failure shape <see cref="PFE.Systems.Map.Scripting.ScriptPlayerHost"/> documents for the same
    /// reason.</para>
    ///
    /// <para><b>Reputation is reported, not invented.</b> The port has no reputation system
    /// (<c>PipBuckBootstrapper.cs:564</c> records the same absence), so <c>@rep</c> — which 40 quests
    /// carry — cannot be granted. It is reported once by name rather than dropped, so "the quest paid
    /// no reputation" stays distinguishable from "reputation does not exist yet".</para>
    /// </summary>
    public sealed class QuestRewardHost : IQuestRewardHost
    {
        private readonly Func<CharacterStats> _stats;
        private readonly Func<PlayerInventory> _inventory;
        private readonly Action<string> _report;

        /// <summary>Keys already reported, so a payout that is genuinely unimplemented does not fill the
        /// console once per quest.</summary>
        private readonly HashSet<string> _reported = new HashSet<string>(StringComparer.Ordinal);

        public QuestRewardHost(
            Func<CharacterStats> stats = null,
            Func<PlayerInventory> inventory = null,
            Action<string> report = null)
        {
            _stats = stats;
            _inventory = inventory;
            _report = report ?? (message => Debug.LogWarning(message));
        }

        /// <summary>XP granted through this host. For tests and the debug overlay.</summary>
        public int XpGranted { get; private set; }

        /// <summary>Skill points granted through this host.</summary>
        public int SkillPointsGranted { get; private set; }

        /// <summary>Item rows handed to the inventory, including ones the inventory then refused.</summary>
        public int ItemsGranted { get; private set; }

        /// <summary>Item rows actually removed. For tests.</summary>
        public int ItemsWithdrawn { get; private set; }

        /// <summary>AS3 <c>addSkillPoint</c> / <c>expa</c> (<c>Quest.as:645-652</c>).</summary>
        public void GrantQuestRewards(string questId, int xp, int sp, int reputation)
        {
            if (xp != 0 || sp != 0)
            {
                CharacterStats stats = _stats?.Invoke();
                if (stats == null)
                {
                    ReportOnce("(player stats)",
                        $"[QuestRewardHost] quest '{questId}' owes {xp} xp and {sp} sp, but no " +
                        "CharacterStats is reachable, so the payout was dropped.");
                }
                else
                {
                    if (xp != 0)
                    {
                        XpGranted += xp;
                        stats.AddXp(xp);
                    }

                    if (sp != 0)
                    {
                        SkillPointsGranted += sp;
                        stats.GrantSkillPoints(sp);
                    }
                }
            }

            if (reputation != 0)
            {
                // No reputation system exists to receive this. Reported by name — the same choice
                // ScriptRunner makes for `pers.rep >= pers.repGood` (ScriptRunner.cs:508).
                ReportOnce("(reputation)",
                    $"[QuestRewardHost] quest '{questId}' owes {reputation} reputation, but the port has " +
                    "no reputation system, so it was dropped. AS3 adds it to World.w.pers.rep " +
                    "(Quest.as:653-656).");
            }
        }

        /// <summary>
        /// AS3 <c>World.w.invent.take(new Item("", id, kol), 2)</c> (<c>Quest.as:661-676</c>).
        ///
        /// <para><b>Resolution order is <c>Item.itemTip()</c>'s, not a guess</b> — the id is looked up in
        /// the item table first and the weapon table second, exactly as
        /// <c>ContainerPropPresenter.SpillAuthoredItems</c> does for container rows. It matters for real
        /// data: several reward ids (<c>x37</c>, <c>plagr</c>, <c>pp127mm^1</c>, <c>p50mg</c>, …) are
        /// weapons with no item row, and <c>money</c> — the most common reward of all — is an ordinary
        /// item row, so neither path needs a special case.</para>
        /// </summary>
        public void GrantQuestItems(string questId, IReadOnlyList<QuestReward> rewards)
        {
            if (rewards == null || rewards.Count == 0) return;

            PlayerInventory inventory = _inventory?.Invoke();
            if (inventory == null)
            {
                ReportOnce("(inventory)",
                    $"[QuestRewardHost] quest '{questId}' grants items, but no PlayerInventory is " +
                    "reachable, so they were dropped.");
                return;
            }

            for (int i = 0; i < rewards.Count; i++)
            {
                QuestReward reward = rewards[i];
                if (string.IsNullOrEmpty(reward.ItemId)) continue;

                int count = reward.Count <= 0 ? 1 : reward.Count;
                ItemsGranted++;

                ItemDefinition item = inventory.ResolveItem(reward.ItemId);
                if (item != null)
                {
                    inventory.Inventory?.AddItem(item, count);
                    continue;
                }

                WeaponDefinition weapon = inventory.ResolveWeapon(reward.ItemId);
                if (weapon != null)
                {
                    // AS3 hands the weapon to `invent.take`, which routes a weapon id to the weapon
                    // dictionary. `AddWeapon` is the port's equivalent; it takes an id, not a row.
                    inventory.Inventory?.AddWeapon(reward.ItemId);
                    continue;
                }

                // A reward id that resolves to neither table is authored data the port cannot honour.
                // Reported per id rather than per call, because the two halves fail for different
                // reasons and the id is the actionable part.
                ReportOnce("reward:" + reward.ItemId,
                    $"[QuestRewardHost] quest '{questId}' rewards '{reward.ItemId}' x{count}, but no " +
                    "item or weapon definition with that id exists in the port's tables.");
            }
        }

        /// <summary>
        /// AS3 <c>minusItem</c> / <c>remWeapon</c> (<c>Quest.as:613-643</c>). The Oracle's branch is on
        /// <c>colTip</c>: <c>0</c> removes from the item dictionary, <c>1</c> from the weapon
        /// dictionary, and any other value removes nothing at all — which is reproduced rather than
        /// widened, because widening it would delete weapons the Oracle leaves alone.
        /// </summary>
        public void WithdrawQuestItems(string questId, IReadOnlyList<QuestWithdrawal> withdrawals)
        {
            if (withdrawals == null || withdrawals.Count == 0) return;

            PlayerInventory inventory = _inventory?.Invoke();
            if (inventory == null)
            {
                ReportOnce("(inventory)",
                    $"[QuestRewardHost] quest '{questId}' withdraws collected items, but no " +
                    "PlayerInventory is reachable, so they were left in place.");
                return;
            }

            for (int i = 0; i < withdrawals.Count; i++)
            {
                QuestWithdrawal withdrawal = withdrawals[i];
                if (string.IsNullOrEmpty(withdrawal.Id)) continue;

                int count = withdrawal.Count <= 0 ? 1 : withdrawal.Count;

                if (withdrawal.ColTip == 0)
                {
                    if (inventory.Inventory != null && inventory.Inventory.RemoveItem(withdrawal.Id, count))
                    {
                        ItemsWithdrawn++;
                    }
                }
                else if (withdrawal.ColTip == 1)
                {
                    if (inventory.Inventory != null && inventory.Inventory.RemoveWeapon(withdrawal.Id))
                    {
                        ItemsWithdrawn++;
                    }
                }
                // Any other colTip: the Oracle's two `if`s both fall through, so nothing happens.
            }
        }

        private void ReportOnce(string key, string message)
        {
            if (_reported.Add(key)) _report(message);
        }
    }
}
