using System;
using PFE.Systems.Weapons;

namespace PFE.Systems.Items
{
    /// <summary>
    /// The <b>price half</b> of an <c>&lt;item&gt;</c> row — AS3 <c>Item.getPrice()</c> and
    /// <c>getMultPrice()</c> (<c>Item.as:352-383</c>), for the item table only.
    ///
    /// <para><b>Why this exists.</b> Every one of the 500 <c>ItemDefinition</c> assets carries the
    /// field default (<c>basePrice: 10</c>, <c>sellPrice: 5</c>) — measured on the shipped set: 491 of
    /// 500 are exactly <c>10</c>, and the nine that are not are the spells, which
    /// <c>SimpleDataImporter.ApplyTipDefaults</c> imports by hand. The editor importer that would fill
    /// the rest is owner-only here, so the vendor page has been pricing the whole item table at ten
    /// caps: <c>impl_skin</c> — a 10 000-cap implant — rang up at 10, and because
    /// <c>sellPrice</c> was 5 on everything, every item in the bag was also <i>sellable</i>, including
    /// the implants and artefacts the Oracle deliberately leaves out of the sell list (no <c>@sell</c>
    /// attribute).</para>
    ///
    /// <para><b>Why the row and not the asset.</b> Same reason <c>WorkbenchService</c> reads its
    /// recipes from the <c>AllData</c> mirror and <c>PlayerInventory.ResolveSkillRows</c> reads
    /// <c>&lt;sk&gt;</c> rows from it: the mirror is shipped (<c>Resources/Loot/AllData</c>), it is the
    /// oracle's own text, and it does not need an importer run. See
    /// <c>docs/CraftingAndRepair/00_INDEX.md</c> §2 D1 for that decision.</para>
    ///
    /// <para><b>The item table never prices through <c>&lt;com&gt;</c>.</b> <c>getPrice</c> prefers
    /// <c>com[0].@price</c> when the row has one, but measured on the shipped file: <b>0 of 500</b>
    /// <c>&lt;item&gt;</c> rows carry a <c>&lt;com&gt;</c> child (all 129 <c>&lt;com price&gt;</c> nodes
    /// live inside <c>&lt;weapon&gt;</c> rows, which is why the weapon path reads
    /// <c>WeaponDefinition.comPrice</c> instead). So for an item the root <c>@price</c> is the whole
    /// answer and this class does not need the body.</para>
    ///
    /// <para><b>What is NOT here: the multipliers.</b> AS3 chains <c>price * sost * multHP * pmult</c>
    /// inside <c>getPrice</c>; the port applies <c>sost</c> and <c>pmult</c> later, in
    /// <c>VendorTradeRules.BuyUnitPrice</c>, because they are per-<i>stock-row</i> facts (a worn item,
    /// a discounted vendor) rather than per-item ones. <c>multHP</c> is not ported at all — named gap,
    /// see that method's remarks.</para>
    ///
    /// <para><b>Integer prices, and the 41 fractional rows.</b> <c>Item.price</c> is a Number and the
    /// shipped rows include <c>price='1.5'</c> / <c>sell='0.3'</c> — every one of the 41 fractional
    /// values is an ammunition row (<c>p9</c>, <c>p32</c>, <c>p10</c>, <c>p375</c>, …). The port's
    /// <c>VendorItemFacts</c> carries ints, so these round half-up here; the visible effect is limited
    /// to those ammo rows, whose <c>sell/price</c> ratio is anyway below every vendor's own
    /// <c>multPrice</c> floor of 0.8 (<c>Vendor.as:132-135</c>), so the buy multiplier is unchanged.</para>
    /// </summary>
    public static class ItemPriceRules
    {
        /// <summary>
        /// The row's <c>@price</c> — <c>Item.getPrice()</c>'s base — or
        /// <paramref name="assetFallback"/> when there is no row at all.
        ///
        /// <para><b>A row without <c>@price</c> is 0, not the fallback.</b> AS3's
        /// <c>this.xml.@price * …</c> on an absent attribute is an empty XMLList, which coerces to 0,
        /// and the buy page then skips the row outright (<c>PipPageVend.as:141</c>:
        /// <c>b.tip != L_WEAPON &amp;&amp; b.xml &amp;&amp; b.xml.@price.length() == 0</c> → <c>continue</c>).
        /// Measured: 75 of the 500 rows have no <c>@price</c> (<c>money</c>, <c>bit</c>, <c>gem1..3</c>,
        /// <c>data</c>, <c>disc</c>, …). Falling back to the asset's 10 for those would invent a price
        /// the oracle does not have, so the fallback is reserved for "the mirror itself is missing"
        /// (offline, or the TextAsset was not shipped) — the case where 10 is all that is known.</para>
        /// </summary>
        public static int BuyBase(string rowAttrs, int assetFallback)
            => Resolve(rowAttrs, "price", assetFallback);

        /// <summary>
        /// The row's <c>@sell</c> — the price the Sell list shows — or <paramref name="assetFallback"/>
        /// when there is no row.
        ///
        /// <para><b>0 means "cannot be sold", and the Sell list reads it that way</b>
        /// (<c>PipPageVend.as:237</c> walks <c>inv.items</c> for a positive <c>@sell</c>). That is what
        /// keeps implants, artefacts and instruments out of the sell list: measured, none of the 15
        /// <c>impl</c>/<c>art</c>/<c>instr</c> rows carries <c>@sell</c>, while the placeholder
        /// <c>sellPrice: 5</c> on every asset made all of them sellable at 5 caps.</para>
        /// </summary>
        public static int SellPrice(string rowAttrs, int assetFallback)
            => Resolve(rowAttrs, "sell", assetFallback);

        /// <summary>
        /// True when the row carries <c>@price</c> at all — <c>b.xml.@price.length() != 0</c>
        /// (<c>PipPageVend.as:141</c>), the buy page's fifth guard.
        /// </summary>
        public static bool HasPrice(string rowAttrs)
            => !string.IsNullOrEmpty(rowAttrs) && WeaponXmlAttrs.AttrPresent(rowAttrs, "price");

        /// <summary>
        /// <c>Item.getMultPrice()</c> (<c>Item.as:376-383</c>): <c>sell / price</c> when <b>both</b> are
        /// positive, otherwise <b>0.1</b>. Reproduced here so a caller holding the row can answer it
        /// without building a <c>VendorItemFacts</c>.
        /// </summary>
        public static float MultPrice(int price, int sellPrice)
        {
            if (price <= 0 || sellPrice <= 0) return 0.1f;
            return (float)sellPrice / price;
        }

        /// <summary>
        /// The shared read. <paramref name="rowAttrs"/> is <c>null</c> only when the mirror has no row
        /// for the id; an empty string is a row whose attribute run is empty, which reads as "attribute
        /// absent" and therefore 0.
        /// </summary>
        private static int Resolve(string rowAttrs, string attribute, int assetFallback)
        {
            if (rowAttrs == null) return assetFallback;

            // AttrF, not AttrI: AS3 compares and multiplies the raw Number, and `price='1.5'` exists.
            // Rounding here is the int field's cost, not the oracle's — see the class remarks.
            float value = WeaponXmlAttrs.AttrF(rowAttrs, attribute, 0f);
            return (int)Math.Round(value, MidpointRounding.AwayFromZero);
        }
    }
}
