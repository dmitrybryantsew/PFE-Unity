using System.Collections.Generic;
using PFE.Systems.Map;

namespace PFE.Systems.Loot
{
    /// <summary>
    /// The unit-side half of a death drop: a placement's own <c>&lt;item&gt;</c> children, projected
    /// into what <see cref="LootRoller.RollPlacedItems"/> rolls.
    ///
    /// <para><b>What this is the port of.</b> <c>Interact.loot</c>'s loop (<c>Interact.as:1973-2000</c>),
    /// reached from <c>Unit.dropLoot</c>'s first statement (<c>Unit.as:4452-4455</c>):
    /// <c>if(inter) { inter.loot(); }</c>. <c>inter</c> is the <c>Interact</c> the base
    /// <c>Unit</c> constructor builds from the placed node, so this table is <b>authored per
    /// placement</b> — <c>bossraider</c>'s <c>&lt;item id="key_sewer" imp="1"/&gt;</c> is the plant boss's
    /// key, and the safe in the same room authors <c>&lt;item id="alc1"/&gt;</c> the same way.</para>
    ///
    /// <para><b>Why it is a separate type and not inline in the caller.</b> The mapping is two AS3
    /// coercions and a presence test, and both coercions are the kind that are wrong in a way nothing
    /// reports: <c>@kol</c> is a <b>presence</b> test (<c>Interact.as:1979-1986</c>), so a placement
    /// carrying <c>kol=""</c> drops <c>int("")</c> — which AS3 coerces to <b>0</b>, so "no stack" — while
    /// an absent <c>@kol</c> means <b>1</b>. Those are opposite outcomes from one empty attribute, and
    /// they are only observable in a drop count on the floor.</para>
    ///
    /// <para><b>What is deliberately not here.</b> The <c>cont</c> table (<c>Interact.as:2005-2010</c>,
    /// <c>LootGen.lootDrop</c>) and the essence/gem rolls (<c>Unit.as:4456-4466</c>) are
    /// <see cref="LootRoller.RollUnitDrop"/> and <see cref="UnitDeathDrop"/>'s job. AS3 runs them
    /// <i>after</i> this loop and the caller keeps that order.</para>
    /// </summary>
    public static class UnitLootRules
    {
        /// <summary>
        /// Projects a placement's <c>&lt;item&gt;</c> children onto the roller's input.
        ///
        /// <para><b><c>imp</c> is a presence test, exactly as <c>@imp.length()</c> is
        /// (<c>Interact.as:1987-1991</c>).</b> It sets the drop mode to 2 rather than 1, and the mode is
        /// what exempts a row from the per-run loot limit (<c>LootGen.as:261</c>) — which is why a quest
        /// key can still drop after the player has already looted everything else in the run.</para>
        ///
        /// <para><b>A row with no id is dropped, and that is not a liberty.</b> AS3 would call
        /// <c>LootGen.lootId(…, "")</c>, which resolves the item by id and finds nothing. The port has no
        /// equivalent of that failed lookup to report through, and a row with no id is not something the
        /// shipped rooms author — 0 occurrences across the 564 room assets. Dropping it keeps the log
        /// clean at the cost of a branch that never fires, which is the right trade for a hot path that
        /// runs on every corpse.</para>
        ///
        /// <para><b>Null in, null out.</b> A placement with no <c>&lt;item&gt;</c> children never builds
        /// an <c>Interact</c> at all, so AS3's guard never reaches <c>inter.loot()</c>. Returning null
        /// lets the caller take the same early exit rather than passing an empty list down a call that
        /// would then have to reject it.</para>
        /// </summary>
        public static List<LootRoller.PlacedLootItem> PlacedItems(List<MapObjectItemData> source)
        {
            if (source == null || source.Count == 0) return null;

            var result = new List<LootRoller.PlacedLootItem>(source.Count);
            for (int i = 0; i < source.Count; i++)
            {
                MapObjectItemData item = source[i];
                if (item == null) continue;

                string id = item.id ?? string.Empty;
                if (id.Length == 0) continue;          // "authored nothing" — see above

                // `if(@kol.length()) { kol = int(@kol) } else { kol = 1 }` (Interact.as:1979-1986).
                // Present-but-unparsable is AS3's `int("x")`, which is 0, not 1 — so it must not fall
                // back to the absent case's 1.
                int? kol;
                if (MapObjectDataUtility.HasAttribute(item.attributes, "kol"))
                {
                    kol = int.TryParse(
                              MapObjectDataUtility.GetAttribute(item.attributes, "kol", string.Empty),
                              out int parsed)
                        ? (int?)parsed
                        : 0;
                }
                else
                {
                    kol = null;                        // the roller maps null to 1
                }

                result.Add(new LootRoller.PlacedLootItem(id, kol,
                    MapObjectDataUtility.HasAttribute(item.attributes, "imp")));
            }

            return result.Count > 0 ? result : null;
        }
    }
}
