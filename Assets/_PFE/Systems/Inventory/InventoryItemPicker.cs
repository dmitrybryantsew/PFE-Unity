using System;
using System.Collections.Generic;
using PFE.Data.Definitions;

namespace PFE.Systems.Inventory
{
    /// <summary>
    /// The filter behind the F2 inventory tab's item pickers — "which of the ids that exist should this
    /// picker show?".
    ///
    /// <para><b>Why this is not just a <c>foreach</c> in the overlay.</b> The two rules it enforces (a
    /// candidate must resolve, and must belong to the selected page) are exactly the kind of thing that
    /// looks obviously right and is wrong in a way nobody notices: a picker that silently shows nothing
    /// reads as "the registry is empty", and one that shows everything reads as "the filter does
    /// nothing". Both are unfalsifiable from a screenshot, so the rules live here where the offline wall
    /// can pin them.</para>
    ///
    /// <para><b>Unity-free on purpose, including the type lookup.</b> The caller passes a
    /// <c>Func&lt;string, ItemType?&gt;</c> rather than a dictionary of <see cref="ItemDefinition"/>s,
    /// because <c>ItemDefinition</c> is a <c>ScriptableObject</c> and constructing one needs the engine —
    /// so a signature that took definitions directly would make this class untestable offline. A test
    /// passes a lambda over a hand-written table instead.</para>
    /// </summary>
    public static class InventoryItemPicker
    {
        /// <summary>
        /// The ids from <paramref name="candidateIds"/> that belong on <paramref name="page"/> and match
        /// <paramref name="search"/>, in the order they were supplied.
        ///
        /// <para><b>Input order is preserved, not re-sorted.</b> The caller passes
        /// <c>PlayerInventory.AvailableItemIds</c>, which is already ordinal-sorted; sorting again here
        /// would be a second, silent source of truth about ordering. A test pins the preservation so a
        /// later "tidy-up" that adds a sort is caught.</para>
        ///
        /// <para><b>An id that does not resolve is dropped, never guessed onto a page.</b> A candidate
        /// with no <see cref="ItemDefinition"/> cannot be placed by
        /// <see cref="InventoryPageRules.Contains"/>, and defaulting it to Misc would make a missing
        /// asset look like a working row that then fails on Add — the failure would be reported one
        /// layer away from its cause.</para>
        /// </summary>
        /// <param name="candidateIds">Ids to consider. Null yields an empty list.</param>
        /// <param name="typeOf">Id → <see cref="ItemType"/>, or <c>null</c> when the id is unknown. A null
        /// resolver excludes everything, because nothing can be placed on a page.</param>
        /// <param name="labelOf">Optional id → display name, so the search box matches names as well as
        /// ids. Null (or a null result) simply means ids are the only searchable text.</param>
        /// <param name="page">The page (sub-tab) the picker is showing.</param>
        /// <param name="search">Free text. Null/empty/whitespace matches everything on the page.</param>
        public static List<string> Filter(
            IEnumerable<string> candidateIds,
            Func<string, ItemType?> typeOf,
            Func<string, string> labelOf,
            InventoryPage page,
            string search)
        {
            var result = new List<string>();
            if (candidateIds == null) return result;

            foreach (string id in candidateIds)
            {
                if (string.IsNullOrEmpty(id)) continue;

                ItemType? type = typeOf?.Invoke(id);
                if (type == null) continue;

                if (!InventoryPageRules.Contains(page, type.Value)) continue;
                if (!Matches(id, labelOf?.Invoke(id), search)) continue;

                result.Add(id);
            }

            return result;
        }

        /// <summary>
        /// Whether <paramref name="id"/> or <paramref name="label"/> contains the search text.
        ///
        /// <para><b>Case-insensitive, and a whitespace-only search matches everything.</b> The ids in
        /// <c>AllData.as</c> are lowercase, so a case-sensitive compare would make "Stimpak" silently
        /// return nothing — and an empty result from a search box is indistinguishable from an empty
        /// registry. Whitespace-only is treated as empty for the same reason: a stray space typed into
        /// the box must not look like "no such item".</para>
        /// </summary>
        public static bool Matches(string id, string label, string search)
        {
            if (string.IsNullOrWhiteSpace(search)) return true;

            string needle = search.Trim();

            if (!string.IsNullOrEmpty(id) &&
                id.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            if (!string.IsNullOrEmpty(label) &&
                label.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            return false;
        }
    }
}
