using System;
using System.Collections.Generic;
using System.Text;
using PFE.Data.Definitions;
using UnityEngine;

namespace PFE.Systems.Magic
{
    /// <summary>
    /// The spell catalogue — every <see cref="ItemDefinition"/> row the runtime would accept as a spell.
    ///
    /// <para><b>One loader, two front ends.</b> The F2 debug editor's Spells tab and the <c>spell</c>
    /// console verb both read this, rather than each doing its own <c>Resources.LoadAll</c>. That is
    /// deliberate: this project has already paid for the alternative — three front ends each caching
    /// their own copy of the same value is three things that can disagree, and the disagreement is
    /// invisible until a row shows in one list and not the other.</para>
    ///
    /// <para><b>The acceptance test is the caster's own.</b> A row is a spell when
    /// <see cref="SpellData.IsPopulated"/> is true, which is exactly the condition
    /// <c>PlayerSpellCaster.CreateSpell</c> applies before building a <see cref="Spell"/>. So a row
    /// listed here is a row <c>spell add</c> can actually grant — the list cannot promise something the
    /// caster would refuse.</para>
    ///
    /// <para><b>Loaded from <c>Resources</c>, not the content registry</b>, for the same reason the
    /// overlay's other catalogues are: this is a debug surface that must work precisely when the
    /// registry failed to initialise. A row present here and absent from the registry is itself the
    /// diagnostic.</para>
    ///
    /// <para><b>Unity-side on purpose.</b> The <c>Resources</c> call is an <c>ECall</c>, so this class
    /// cannot be exercised offline — which is why the text formatting lives in
    /// <see cref="SpellFacts"/> instead, where it is proved by <c>SpellFactsTests</c>. Nothing here
    /// makes a decision beyond the one filter and a sort.</para>
    /// </summary>
    public static class SpellCatalog
    {
        /// <summary>Where the importer writes item rows — <c>Resources/Items</c>, alongside ammo and armour.</summary>
        private const string ItemsResourceFolder = "Items";

        private static ItemDefinition[] _cached;

        /// <summary>
        /// Every spell row, sorted by id. Cached after the first call — the overlay and the console both
        /// ask on their own schedule, and 451 item rows is real work to redo per keystroke.
        /// </summary>
        public static ItemDefinition[] All()
        {
            if (_cached != null) return _cached;

            ItemDefinition[] items = Resources.LoadAll<ItemDefinition>(ItemsResourceFolder);
            var spells = new List<ItemDefinition>(items?.Length ?? 0);

            if (items != null)
            {
                foreach (ItemDefinition item in items)
                {
                    if (item == null) continue;
                    if (string.IsNullOrEmpty(item.itemId)) continue;
                    if (!item.spellData.IsPopulated) continue;
                    spells.Add(item);
                }
            }

            spells.Sort((a, b) => string.Compare(a.itemId, b.itemId, StringComparison.OrdinalIgnoreCase));
            _cached = spells.ToArray();
            return _cached;
        }

        /// <summary>The row for <paramref name="spellId"/>, or null when no item row carries it as a spell.</summary>
        public static ItemDefinition Find(string spellId)
        {
            if (string.IsNullOrEmpty(spellId)) return null;

            foreach (ItemDefinition item in All())
            {
                if (string.Equals(item.itemId, spellId, StringComparison.OrdinalIgnoreCase))
                    return item;
            }
            return null;
        }

        /// <summary>
        /// The name to show for a row: the importer's <c>displayName</c> when it is a real name, else
        /// the id. See <see cref="SpellFacts.IsPlaceholderName"/> for why the placeholder is rejected.
        /// </summary>
        public static string DisplayName(ItemDefinition item)
        {
            if (item == null) return "(null)";
            return SpellFacts.IsPlaceholderName(item.displayName) ? item.itemId : item.displayName;
        }

        /// <summary>
        /// One line per spell: id, name, and the mechanical summary. The shape both front ends render,
        /// so the console listing and the F2 row cannot drift.
        /// </summary>
        public static string ToLine(ItemDefinition item)
        {
            if (item == null) return "(null)";

            string name = DisplayName(item);
            string label = name == item.itemId ? item.itemId : $"{item.itemId} ({name})";
            return $"{label,-14} {SpellFacts.Summarize(in item.spellData)}";
        }

        /// <summary>A multi-line listing of the whole catalogue, optionally filtered by a substring of
        /// the id or the name.</summary>
        public static string Dump(string filter = null)
        {
            ItemDefinition[] all = All();
            var sb = new StringBuilder();
            sb.AppendLine($"--- {all.Length} spell row(s) under Resources/{ItemsResourceFolder} ---");

            string f = filter?.Trim();
            int shown = 0;
            foreach (ItemDefinition item in all)
            {
                if (f != null && f.Length > 0 &&
                    item.itemId.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0 &&
                    DisplayName(item).IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                shown++;
                sb.AppendLine("  " + ToLine(item));
            }

            if (shown == 0)
                sb.AppendLine($"  (no spell matches '{filter}')");

            return sb.ToString();
        }

        /// <summary>Drop the cache. For a debug tool that wants to re-read after a re-import without a
        /// domain reload; not called in the normal path.</summary>
        public static void Invalidate() => _cached = null;
    }
}
