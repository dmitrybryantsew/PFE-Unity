using System;
using System.Collections.Generic;

namespace PFE.Data.Definitions
{
    /// <summary>
    /// Which SWF symbols are a weapon's held art — the rule the weapon-sprite importer uses to decide
    /// what to copy out of the export and turn into a <see cref="WeaponVisualDefinition"/>.
    ///
    /// <para><b>Why this lives in the runtime assembly and not in the importer.</b> It is a rule, not
    /// an editor step, and the offline test wall cannot reach <c>PFE.Editor</c> at all. Keeping it here
    /// means the rule is pinned by tests that run without Unity, which is the only way a
    /// classification this consequential stays honest.</para>
    ///
    /// <para><b>Why it is not a name pattern.</b> The importer used to decide with a hand-maintained
    /// blocklist of prefixes, and a prefix blocklist is wrong in both directions. That one excluded
    /// <c>vismolotov</c>, <c>visacidgr</c>, <c>visbomb</c>, <c>vismine</c> and 40 more — the whole
    /// throwable and mine family — under a comment calling them "environment/unit objects, not held
    /// weapons", and it also swallowed unrelated symbols by prefix collision: <c>visbal</c> caught
    /// <c>visbalemine</c>, <c>viscry</c> caught <c>viscryomine</c>, <c>visdin</c> caught
    /// <c>visdinamit</c>, <c>vispip</c> caught <c>vispipe</c>, <c>vistt</c> caught <c>visttweap1</c>.
    /// The visible consequence was that a grenade and a mine were simulated with no sprite at all.</para>
    ///
    /// <para><b>The rule instead.</b> <c>AllData.as</c> already says which ids are weapons: a symbol
    /// <c>vis&lt;id&gt;</c> is a weapon's held art exactly when <c>&lt;id&gt;</c> is a
    /// <c>&lt;weapon&gt;</c> row. That is the same derivation AS3 itself makes — <c>Weapon.as:496</c>
    /// resolves <c>vWeapon</c> from the weapon's own class name — and it cannot drift, because it is
    /// read from the data rather than maintained by hand. A symbol belonging to the environment
    /// (<c>visdamgren</c>, <c>visdamshot</c>, <c>visdamexpl</c>, <c>visbox</c>, <c>vistrap</c>,
    /// <c>viscur</c>, …) has no weapon row and therefore falls out for free, with no list to keep.</para>
    /// </summary>
    public static class WeaponVisSymbolRule
    {
        /// <summary>
        /// The prefix AS3 builds a held-weapon symbol from: <c>vis</c> + weapon id
        /// (<c>Weapon.as:496</c> resolves <c>vWeapon</c> from the weapon's class name, and
        /// <c>Mine.as:98</c> spells the same convention out as <c>"vis" + id</c>).
        /// </summary>
        public const string VisPrefix = "vis";

        /// <summary>
        /// Whether <paramref name="symbolName"/> is a weapon's held art.
        /// </summary>
        /// <param name="symbolName">
        /// A symbol name from the export's symbol table, e.g. <c>vismolotov</c>.
        /// </param>
        /// <param name="weaponIds">
        /// Every <c>&lt;weapon id&gt;</c> in <c>AllData.as</c>. Pass a set built with
        /// <see cref="StringComparer.OrdinalIgnoreCase"/>; the comparison itself is case-sensitive
        /// against whatever the collection holds, so a case-sensitive set of lower-case ids also works.
        /// </param>
        /// <param name="overrideSymbols">
        /// Symbol names named explicitly by a <c>vis.@vweap</c> attribute. A weapon may point at a
        /// symbol that is not <c>vis</c> + its own id, and the importer's wiring step honours that, so
        /// the import set has to contain it too — otherwise the definition is never created and the
        /// override silently resolves to nothing.
        /// </param>
        public static bool IsWeaponVisSymbol(
            string symbolName,
            ICollection<string> weaponIds,
            ICollection<string> overrideSymbols = null)
        {
            if (string.IsNullOrEmpty(symbolName)) return false;

            if (overrideSymbols != null && overrideSymbols.Contains(symbolName)) return true;

            if (!symbolName.StartsWith(VisPrefix, StringComparison.Ordinal)) return false;

            string weaponId = symbolName.Substring(VisPrefix.Length);
            if (weaponId.Length == 0) return false;

            return weaponIds != null && weaponIds.Contains(weaponId);
        }
    }
}
