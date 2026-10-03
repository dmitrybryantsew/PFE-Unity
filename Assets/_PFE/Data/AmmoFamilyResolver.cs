using System;
using System.Collections.Generic;
using PFE.Data.Definitions;

namespace PFE.Data
{
    /// <summary>
    /// Groups <see cref="AmmoDefinition"/> rows into the variant families a weapon can choose from,
    /// and answers "which ammo types belong to the type this weapon fires?".
    ///
    /// <para><b>The family model, as the data actually encodes it.</b> An ammo id is
    /// <c>&lt;family&gt;</c> for the regular round and <c>&lt;family&gt;_&lt;n&gt;</c> for its variants,
    /// and <see cref="AmmoDefinition.baseId"/> names the family on every member — verified against the
    /// shipped assets: <c>p32</c>, <c>p32_1</c> and <c>p32_2</c> all carry <c>baseId: p32</c>, with the
    /// base row at <c>modifier 0</c>, <c>damageMultiplier 1</c>. 44 of the 75 ammo assets carry a
    /// non-empty <c>baseId</c>; the other 31 are families of one and are their own base.</para>
    ///
    /// <para><b>Why the base row is found by id and not by <c>baseId</c> alone.</b> The base row's
    /// <c>baseId</c> equals its own id (that is the convention — <c>batt</c> has <c>baseId: batt</c>),
    /// so "the member whose id is the family name" is the regular variant. Falling back to
    /// <c>modifier == None &amp;&amp; damageMultiplier == 1</c> would guess, and a hand-authored asset
    /// could satisfy that while not being the base.</para>
    ///
    /// <para><b>Plain static C#, deliberately.</b> This holds the whole decision so it can be covered by
    /// EditMode tests without a scene, a registry or a <c>MonoBehaviour</c> — the overlay and the console
    /// are then thin. The registry is reached through a delegate rather than a hard dependency so a test
    /// can pass a plain array.</para>
    /// </summary>
    public static class AmmoFamilyResolver
    {
        /// <summary>
        /// Resolve an ammo id to its definition, or null. The production wiring passes
        /// <c>id =&gt; database.Registry.Get&lt;AmmoDefinition&gt;(ContentType.Ammo, id)</c>; a test passes a
        /// dictionary lookup. Kept as a delegate so this class needs neither a <c>GameDatabase</c> nor a
        /// live registry.
        /// </summary>
        public delegate AmmoDefinition AmmoLookup(string ammoId);

        /// <summary>
        /// Every id an ammo row could be filed under: the given id, plus everything the lookup can
        /// enumerate. Production passes all registered ammo ids so a family is complete even when the
        /// weapon names only the base.
        /// </summary>
        public delegate IEnumerable<string> AmmoIdEnumerator();

        /// <summary>
        /// The family name for an ammo id. Uses <see cref="AmmoDefinition.baseId"/> when the row exists
        /// and carries one; otherwise falls back to the id with any trailing <c>_&lt;digits&gt;</c> suffix
        /// stripped, which is the naming convention the importer produces.
        ///
        /// <para>The textual fallback matters: <see cref="AmmoDefinition.baseId"/> is a hand-editable
        /// field, and 31 rows leave it empty. Without the fallback, <c>p10</c> and <c>p10_1</c> would land
        /// in two different families purely because one asset was authored with a blank base.</para>
        /// </summary>
        public static string FamilyOf(string ammoId, AmmoLookup lookup = null)
        {
            if (string.IsNullOrEmpty(ammoId)) return string.Empty;

            AmmoDefinition def = lookup?.Invoke(ammoId);
            if (def != null && !string.IsNullOrEmpty(def.baseId))
                return def.baseId;

            return StripVariantSuffix(ammoId);
        }

        /// <summary>
        /// True when <paramref name="ammoId"/> is the regular round of its family — i.e. the family name
        /// itself. This is what a dropdown should preselect, and it matches the weapon asset default:
        /// a weapon authored as <c>ammoType: p32</c> fires the regular round until something overrides it.
        /// </summary>
        public static bool IsRegular(string ammoId)
        {
            if (string.IsNullOrEmpty(ammoId)) return false;
            return string.Equals(ammoId, StripVariantSuffix(ammoId), StringComparison.Ordinal);
        }

        /// <summary>
        /// Drop a trailing <c>_&lt;digits&gt;</c> variant suffix. <c>p32_1</c> → <c>p32</c>;
        /// <c>p32</c> → <c>p32</c>; <c>p32_x</c> → <c>p32_x</c> (a non-numeric suffix is part of the name,
        /// not a variant index — <c>gren40</c> must not be trimmed).
        ///
        /// <para>Only the LAST underscore group is considered, and only when every character after it is a
        /// digit, so multi-part ids survive: <c>p50mg_2</c> → <c>p50mg</c>.</para>
        /// </summary>
        public static string StripVariantSuffix(string ammoId)
        {
            if (string.IsNullOrEmpty(ammoId)) return string.Empty;

            int underscore = ammoId.LastIndexOf('_');
            if (underscore <= 0 || underscore == ammoId.Length - 1) return ammoId;

            for (int i = underscore + 1; i < ammoId.Length; i++)
            {
                if (!char.IsDigit(ammoId[i])) return ammoId;
            }

            return ammoId.Substring(0, underscore);
        }

        /// <summary>
        /// Build the ordered selectable list for a weapon's current ammo type: the regular round first,
        /// then the variants sorted so the regular stays the default and the order is stable between
        /// frames (an unstable order would make a dropdown selection jump).
        ///
        /// <para><b>Graceful when the family is unknown.</b> If the id resolves to nothing and no
        /// sibling can be enumerated, the list contains just the id itself. That is deliberate: the
        /// caller then renders a one-entry dropdown rather than an empty one, and "the weapon names an
        /// ammo id with no asset" stays visible instead of becoming an invisible empty list — the
        /// failure mode a user cannot distinguish from "this weapon takes no ammo".</para>
        /// </summary>
        public static List<string> GetSelectableTypes(
            string weaponAmmoType,
            AmmoLookup lookup = null,
            AmmoIdEnumerator enumerator = null)
        {
            var result = new List<string>();

            if (string.IsNullOrEmpty(weaponAmmoType))
                return result;

            string family = FamilyOf(weaponAmmoType, lookup);
            if (string.IsNullOrEmpty(family))
                return result;

            if (lookup?.Invoke(family) != null || string.Equals(family, weaponAmmoType, StringComparison.Ordinal))
                result.Add(family);

            if (enumerator != null)
            {
                var variants = new List<string>();
                foreach (string id in enumerator())
                {
                    if (string.IsNullOrEmpty(id)) continue;
                    if (string.Equals(id, family, StringComparison.Ordinal)) continue;
                    if (!string.Equals(FamilyOf(id, lookup), family, StringComparison.Ordinal)) continue;

                    variants.Add(id);
                }

                variants.Sort(StringComparer.Ordinal);
                result.AddRange(variants);
            }

            // A weapon whose id could not be tied to any asset still gets itself as the single entry.
            if (result.Count == 0)
                result.Add(weaponAmmoType);

            return result;
        }

        /// <summary>
        /// A short human label for a dropdown row, e.g. <c>p32_1  (mod 1, dmg ×0.9, pierce +0)</c>.
        /// <paramref name="lookup"/> returning null yields the bare id — never a guessed modifier, because
        /// a fabricated "×1.0" on a missing asset reads as a real value.
        /// </summary>
        public static string Describe(string ammoId, AmmoLookup lookup)
        {
            if (string.IsNullOrEmpty(ammoId)) return "(none)";

            AmmoDefinition def = lookup?.Invoke(ammoId);
            if (def == null) return $"{ammoId}  (no AmmoDefinition)";

            string suffix = IsRegular(ammoId) ? "  · regular" : string.Empty;
            return $"{ammoId}  (mod {def.modifier}, dmg ×{def.damageMultiplier:0.##}, pierce +{def.armorPiercingBonus}){suffix}";
        }
    }
}
