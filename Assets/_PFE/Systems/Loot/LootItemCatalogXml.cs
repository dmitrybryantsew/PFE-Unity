using System;

namespace PFE.Systems.Loot
{
    /// <summary>
    /// The <see cref="ILootItemCatalog"/> backed by the parsed <see cref="LootData"/> — the port's
    /// stand-in for AS3's <c>AllData.d.item.(@id == id)</c> / <c>.weapon</c> / <c>.armor</c> lookups
    /// (<c>Item.as:151-170</c>, <c>:314-348</c>).
    ///
    /// <para><b>Collection by tip, not a global search.</b> <c>Item</c> picks the collection from the
    /// <i>tip</i>: <c>armor</c> → <c>d.armor</c>, <c>weapon</c> → <c>d.weapon</c>, anything else →
    /// <c>d.item</c>. A global search would resolve a weapon id that also exists as an item id
    /// differently from the oracle, so this does not fall back.</para>
    /// </summary>
    public sealed class LootItemCatalogXml : ILootItemCatalog
    {
        private readonly LootData _data;

        public LootItemCatalogXml(LootData data)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
        }

        /// <inheritdoc/>
        public LootItemRow? Find(string tip, string id)
        {
            if (string.IsNullOrEmpty(id)) return null;

            if (string.Equals(tip, LootTips.Armor, StringComparison.Ordinal))
                return _data.Armors.TryGetValue(id, out LootItemRow a) ? a : (LootItemRow?)null;

            if (string.Equals(tip, LootTips.Weapon, StringComparison.Ordinal))
                return _data.Weapons.TryGetValue(id, out LootItemRow w) ? w : (LootItemRow?)null;

            return _data.Items.TryGetValue(id, out LootItemRow i) ? i : (LootItemRow?)null;
        }

        /// <inheritdoc/>
        public LootItemRow? FindByAnyId(string id, out string resolvedTip)
        {
            resolvedTip = null;
            if (string.IsNullOrEmpty(id)) return null;

            // Item.as:314-348 — item, then weapon, then armour.
            if (_data.Items.TryGetValue(id, out LootItemRow i))
            {
                resolvedTip = LootTips.Item;
                return i;
            }

            if (_data.Weapons.TryGetValue(id, out LootItemRow w))
            {
                resolvedTip = LootTips.Weapon;
                return w;
            }

            if (_data.Armors.TryGetValue(id, out LootItemRow a))
            {
                resolvedTip = LootTips.Armor;
                return a;
            }

            return null;
        }
    }
}
