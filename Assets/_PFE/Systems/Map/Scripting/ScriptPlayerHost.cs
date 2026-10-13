using System;
using System.Collections.Generic;
using PFE.Systems.RPG;
using UnityEngine;

namespace PFE.Systems.Map.Scripting
{
    /// <summary>
    /// The port of the oracle's player half — <c>Script.as:300-336</c> and <c>:506-509</c>.
    ///
    /// <para><b>What is real and what is reported.</b> <c>perk</c> and <c>xp</c> drive
    /// <c>CharacterStats</c>, which exists and is where the port keeps perks and experience. <c>take</c>,
    /// <c>armor</c>, <c>eff</c>, <c>remeff</c> and <c>weapch</c> need the inventory, armour and effect
    /// subsystems reached through seams this class does not have — they are <b>reported once by name</b>
    /// rather than silently dropped, so a script that grants an item is distinguishable from a script
    /// that ran and changed nothing.</para>
    ///
    /// <para><b>Why the perk path is not optional.</b> <c>beginLevit</c> is the begin land's levitation
    /// tutorial and its whole payload is <c>perk levitation</c> followed by <c>mess perkLevit</c>
    /// (<c>beginLevit.asset</c>). Without it the tutorial announces a perk the player never receives,
    /// which is worse than not running at all.</para>
    /// </summary>
    public sealed class ScriptPlayerHost : IScriptPlayerHost
    {
        /// <summary>
        /// The player's stats, resolved per call rather than held.
        ///
        /// <para><b>Per call, because the player can be rebuilt.</b> <c>PlayerController</c> is a scene
        /// object and <c>CharacterStats</c> is a component on it; a room rebuild or a scene reload can
        /// hand the game a different instance. Holding the first one found would leave a script granting
        /// perks to a destroyed object — which throws nothing and changes nothing.</para>
        /// </summary>
        private readonly Func<CharacterStats> _stats;

        private readonly Action<string> _report;
        private readonly HashSet<string> _reported = new HashSet<string>(StringComparer.Ordinal);

        public ScriptPlayerHost(Func<CharacterStats> stats = null, Action<string> report = null)
        {
            _stats = stats;
            _report = report ?? (message => Debug.LogWarning(message));
        }

        /// <summary>How many perks this host has granted. For tests and the debug overlay.</summary>
        public int PerksGranted { get; private set; }

        /// <summary>Total experience this host has granted. For tests.</summary>
        public int ExperienceGranted { get; private set; }

        /// <summary>
        /// AS3 <c>perk val</c> (<c>Script.as:325-328</c>) → <c>World.w.pers.addPerk(val)</c>.
        ///
        /// <para><b>The oracle ignores the result.</b> <c>CharacterStats.AddPerk</c> returns false when
        /// the perk is unknown or its prerequisites are unmet, and a script that grants a perk as part of
        /// a cutscene has no branch for "it did not take" — so neither does this. It is logged, because a
        /// perk the player never receives is otherwise invisible.</para>
        /// </summary>
        public void AddPerk(string perkId)
        {
            if (string.IsNullOrEmpty(perkId)) return;

            CharacterStats stats = _stats?.Invoke();
            if (stats == null)
            {
                ReportOnce("(player stats)",
                    $"[ScriptPlayerHost] `perk {perkId}` could not be granted: no CharacterStats is " +
                    "reachable, so the player would never receive it.");
                return;
            }

            PerksGranted++;
            if (!stats.AddPerk(perkId))
            {
                Debug.LogWarning($"[ScriptPlayerHost] `perk {perkId}` was refused by CharacterStats " +
                                 "(unknown perk, unmet prerequisites, or no perk points). The script " +
                                 "carries no branch for that, so the cutscene continues.");
            }
        }

        /// <summary>AS3 <c>xp val</c> (<c>Script.as:321-324</c>) → <c>World.w.pers.expa(val)</c>.</summary>
        public void GrantXp(int amount)
        {
            CharacterStats stats = _stats?.Invoke();
            if (stats == null)
            {
                ReportOnce("(player stats)",
                    $"[ScriptPlayerHost] `xp {amount}` could not be granted: no CharacterStats is reachable.");
                return;
            }

            ExperienceGranted += amount;
            stats.AddXp(amount);
        }

        public void TakeItem(string itemId, int count) => ReportUnconsumed("take", itemId, count);

        public void ChangeArmor(string armorId) => ReportUnconsumed("armor", armorId, 0);

        public void AddEffect(string effectId, int opt1, int opt2) =>
            ReportUnconsumed("eff", effectId, opt1);

        public void RemoveEffect(string effectId) => ReportUnconsumed("remeff", effectId, 0);

        public void ChangeWeapon(string weaponId) => ReportUnconsumed("weapch", weaponId, 0);

        private void ReportUnconsumed(string command, string value, int amount)
        {
            ReportOnce(command,
                $"[ScriptPlayerHost] `{command}`" +
                (string.IsNullOrEmpty(value) ? string.Empty : $" '{value}'") +
                (amount != 0 ? $" x{amount}" : string.Empty) +
                " has no consumer in the port yet, so it did nothing. The Oracle's equivalent is in " +
                "Script.as:300-336 / :506-509.");
        }

        private void ReportOnce(string key, string message)
        {
            if (_reported.Add(key)) _report(message);
        }
    }
}
