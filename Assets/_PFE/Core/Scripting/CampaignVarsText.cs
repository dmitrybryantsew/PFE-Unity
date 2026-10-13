using System;
using System.Collections.Generic;
using System.Globalization;

namespace PFE.Core.Scripting
{
    /// <summary>
    /// Word wrap and value formatting for the F2 "Campaign Vars" tab's long description lines.
    ///
    /// <para><b>Why this is not inline in the overlay.</b> The overlay's styles set
    /// <c>wordWrap = false</c>, so IMGUI does not wrap an over-long label — it clips it, silently. The
    /// catalogue's responsibility lines are 120-160 characters against a ~135-character budget, so every
    /// one of them would lose its tail in the shipped window and nothing would go red. Wrapping on word
    /// boundaries is a decision table (too-long word, empty input, exact-fit line) and therefore lives
    /// here, where the offline wall can assert it, rather than in an <c>OnGUI</c> method.</para>
    /// </summary>
    public static class CampaignVarsText
    {
        /// <summary>
        /// Split <paramref name="text"/> into lines no longer than <paramref name="maxChars"/>,
        /// breaking on spaces only. A single word longer than the budget keeps its own line rather than
        /// being split mid-word — a split id is worse than a long line, and the caller can see it.
        /// </summary>
        public static IReadOnlyList<string> Wrap(string text, int maxChars)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(text)) return lines;
            if (maxChars <= 0)
            {
                lines.Add(text);
                return lines;
            }

            string[] words = text.Split(' ');
            var current = new List<string>();
            int length = 0;

            foreach (string word in words)
            {
                if (word.Length == 0) continue;

                int projected = length == 0 ? word.Length : length + 1 + word.Length;
                if (length > 0 && projected > maxChars)
                {
                    lines.Add(string.Join(" ", current));
                    current.Clear();
                    length = 0;
                }

                current.Add(word);
                length = length == 0 ? word.Length : length + 1 + word.Length;
            }

            if (current.Count > 0) lines.Add(string.Join(" ", current));
            return lines;
        }

        /// <summary>
        /// Parse what a tester typed into a trigger value field. A value that parses as an integer
        /// becomes an int trigger; anything else is stored verbatim as a string, because the oracle
        /// writes the value verbatim too (<c>Game.as:678</c>) and string states such as
        /// <c>patient_tr2 = "wait"</c> (<c>NPC.as:591</c>) must survive a debug edit.
        /// </summary>
        public static bool TryParseTriggerValue(string text, out PFE.Systems.Campaign.TriggerValue value)
        {
            if (string.IsNullOrEmpty(text))
            {
                value = PFE.Systems.Campaign.TriggerValue.Unset;
                return false;
            }

            if (int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int parsed))
            {
                value = PFE.Systems.Campaign.TriggerValue.FromInt(parsed);
                return true;
            }

            value = PFE.Systems.Campaign.TriggerValue.FromString(text);
            return true;
        }
    }
}
