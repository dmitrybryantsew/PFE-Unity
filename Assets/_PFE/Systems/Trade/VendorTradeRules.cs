using System;
using System.Collections.Generic;
using PFE.Data.Definitions.Campaign;

namespace PFE.Systems.Trade
{
    /// <summary>
    /// The four sub-pages of <c>PipPageVend</c> — the Oracle's <c>page2</c>, 1..4
    /// (<c>PipPageVend.as:119-434</c>).
    /// </summary>
    public enum VendorSubPage
    {
        /// <summary>Buy — the vendor's <c>buys</c> list, filtered.</summary>
        Buy = 1,

        /// <summary>Sell — the player's items with an <c>@sell</c> price.</summary>
        Sell = 2,

        /// <summary>Repair — damaged weapons and armour, and the autodoc's robobrain.</summary>
        Repair = 3,

        /// <summary>Quests — the vendor's <c>&lt;task&gt;</c> board.</summary>
        Quests = 4
    }

    /// <summary>
    /// Everything the trade rules need to know about one item row — the port of the fields
    /// <c>new Item(...)</c> resolves out of the item table (<c>Item.as:159-289</c>).
    ///
    /// <para><b>Why a struct of primitives.</b> The rules in <see cref="VendorTradeRules"/> are the part
    /// of <c>PipPageVend</c> that has branches worth pinning, and pinning them means running them —
    /// offline, with no <c>ScriptableObject</c>. A fixture can build one of these in four lines; an
    /// <c>ItemDefinition</c> cannot be built outside the engine.</para>
    /// </summary>
    public struct VendorItemFacts
    {
        /// <summary><c>Item.nazv</c> — resolved through <c>Res.txt</c> by <c>PipItemNames</c>.</summary>
        public string Name;

        /// <summary><c>Item.tip</c> — the category (<c>weapon</c>, <c>armor</c>, <c>med</c>, …).</summary>
        public string Tip;

        /// <summary><c>Item.getPrice()</c>'s base — the row's <c>@price</c>.</summary>
        public int Price;

        /// <summary>The row's <c>@sell</c>; 0 means "this item cannot be sold".</summary>
        public int SellPrice;

        /// <summary><c>@maxhp</c> — the repair price's denominator.</summary>
        public float MaxHp;

        /// <summary>Whether an item row was found at all. The Oracle's <c>l.length() == 0</c>.</summary>
        public bool Exists;

        /// <summary>
        /// The sell-side multiplier — <c>Item.getMultPrice()</c> (<c>Item.as:376-383</c>):
        /// <c>sell / price</c> when both are present, otherwise <b>0.1</b>.
        /// </summary>
        public float MultPrice
        {
            get
            {
                if (Price <= 0 || SellPrice <= 0) return 0.1f;
                return (float)SellPrice / Price;
            }
        }
    }

    /// <summary>
    /// The live player facts the buy filter reads. Kept separate from the item facts because these
    /// change per rebuild while the item table does not.
    /// </summary>
    public struct VendorPageContext
    {
        /// <summary><c>inv.money.kol</c> — <c>PipBuck.money</c>.</summary>
        public int Caps;

        /// <summary><c>World.w.pers.level</c>.</summary>
        public int Level;

        /// <summary><c>World.w.pers.barterLvl</c> — <b>not</b> the barter skill (<c>Vendor.as:145</c>).</summary>
        public int BarterLevel;

        /// <summary><c>World.w.hardInv</c>.</summary>
        public bool HardInventory;

        /// <summary><c>World.w.loc.base</c> — a sale is allowed to overdraw the vendor's purse here.</summary>
        public bool InBase;

        /// <summary>The active category filter, or empty. The Buy page hides the category bar
        /// (<c>PipPageVend.as:119-128</c>), so this is empty there.</summary>
        public string CategoryFilter;
    }

    /// <summary>One row of the Buy list — the object <c>setSubPages</c> builds at
    /// <c>PipPageVend.as:159-188</c>.</summary>
    public struct VendorBuyRow
    {
        /// <summary><c>id</c> or <c>id^variant</c> — the same key <c>buys2</c> uses (<c>:172-179</c>).</summary>
        public string Rid;

        public string Id;
        public int Variant;
        public string Tip;
        public string Name;

        /// <summary>The row's stock count (<c>b.kol</c>).</summary>
        public int Kol;

        /// <summary>The pending purchase quantity (<c>b.bou</c>).</summary>
        public int Bou;

        /// <summary>The unit price actually charged — <c>round(price * mp)</c>.</summary>
        public int Price;

        /// <summary>The multiplier that produced <see cref="Price"/>.</summary>
        public float Multiplier;

        /// <summary>The "condition / you have" column (<c>sost</c>, <c>:184-187</c>).</summary>
        public float Have;

        /// <summary><c>Res.pipText(b.tip)</c> — the primary sort key (<c>:213</c>).</summary>
        public string SortName;

        public int Barter;
        public int Level;
    }

    /// <summary>One row of the Sell list (<c>PipPageVend.as:236-275</c>).</summary>
    public struct VendorSellRow
    {
        public string Rid;
        public string Id;
        public string Tip;
        public string Name;

        /// <summary>How many the player holds.</summary>
        public int Kol;

        /// <summary>Pending sale quantity.</summary>
        public int Bou;

        /// <summary>The row's <c>@sell</c>, unmultiplied (<c>:259</c>).</summary>
        public int Price;

        public string SortName;
    }

    /// <summary>One row of the Repair list (<c>PipPageVend.as:323-360</c>).</summary>
    public struct VendorRepairRow
    {
        public string Rid;
        public string Id;
        public string Name;
        public string Tip;

        /// <summary>Current durability.</summary>
        public float Hp;

        /// <summary>Full durability.</summary>
        public float MaxHp;

        /// <summary>The price the row shows and the click charges.</summary>
        public int Price;
    }

    /// <summary>One row of the Quests list (<c>PipPageVend.as:374-434</c>).</summary>
    public struct VendorQuestRow
    {
        public string Id;
        public string Name;

        /// <summary>The Oracle's row <c>state</c>: 1 in progress, 3 reportable, 4 giveable, 2 done,
        /// 0 not in the journal.</summary>
        public int State;

        /// <summary>What a click does — <c>addQuest</c> (4), <c>chGive/chReport</c> (1/3), or nothing.</summary>
        public bool CanActivate;
    }

    /// <summary>
    /// The arithmetic and the filters of <c>fe.inter.PipPageVend</c>, split out from the page so they
    /// can be run without a <c>RectTransform</c>.
    ///
    /// <para><b>Nothing here draws and nothing here mutates.</b> The page builds rows through these
    /// functions and then performs the transaction through its source seam; a rule that returned a
    /// new inventory state would be a second writer of the bag, which is what the command seam exists
    /// to prevent.</para>
    /// </summary>
    public static class VendorTradeRules
    {
        // ── Buy ───────────────────────────────────────────────────────────────

        /// <summary>
        /// The eleven admission guards of the buy loop (<c>PipPageVend.as:129-210</c>), in the Oracle's
        /// order. Returns false and a short reason when the row is not admitted.
        /// </summary>
        /// <remarks>
        /// <para><b>These are build-time guards, not click-time ones.</b> A row that fails is absent
        /// from the list; the Oracle does not show it greyed out and then refuse the click.</para>
        ///
        /// <para><b>Guard 5 is the item table, not affordability.</b> <c>b.xml.@price</c> must exist
        /// unless the row is a weapon (<c>:141</c>) — in the port that is "the item resolved and has a
        /// price". Affordability is <see cref="ClampToAffordable"/>'s job, at selection time
        /// (<c>:705-714</c>).</para>
        /// </remarks>
        public static bool AdmitBuyRow(VendorStock row, VendorItemFacts item, VendorPageContext ctx,
            IVendorBuyFacts facts, out string reason)
        {
            reason = null;

            // :131 — `if(b.kol <= 0) continue;`
            if (row.Kol <= 0) { reason = "no stock"; return false; }

            // :135 — a schematic the player already knows.
            if (row.Tip == "scheme" && facts.OwnsScheme(row.Id)) { reason = "scheme known"; return false; }

            // :137 — a weapon at a variant the player already has (or better).
            if (row.Tip == "weapon" && facts.OwnsWeaponVariantAtLeast(row.Id, row.Variant))
            {
                reason = "variant owned";
                return false;
            }

            // :139 — armour the player already has.
            if (row.Tip == "armor" && facts.OwnsArmor(row.Id)) { reason = "armor owned"; return false; }

            // :141 — the item row (and its @price) must exist, except for weapons.
            if (!item.Exists && row.Tip != "weapon") { reason = "no item row"; return false; }

            // :143 — art / implants the player already owns.
            if ((row.Tip == "art" || row.Tip == "impl") && facts.OwnsArt(row.Id))
            {
                reason = "art owned";
                return false;
            }

            // :145 — `b.lvl <= pers.level && b.barter <= pers.barterLvl`.
            if (row.Lvl > 0 && row.Lvl > ctx.Level) { reason = "level gate"; return false; }
            if (row.Barter > 0 && row.Barter > ctx.BarterLevel) { reason = "barter gate"; return false; }

            // :147 — `b.trig` unset, or its trigger is 1.
            if (!string.IsNullOrEmpty(row.Trig) && !facts.TriggerIsOne(row.Trig))
            {
                reason = "trigger gate";
                return false;
            }

            // :149 — `!b.hardinv || World.w.hardInv`.
            if (row.Hardinv && !ctx.HardInventory) { reason = "hard inventory only"; return false; }

            // :151 — checkCat(b.tip). The Buy page hides the category bar, so this is normally empty.
            if (!string.IsNullOrEmpty(ctx.CategoryFilter) && !string.Equals(ctx.CategoryFilter, row.Tip, StringComparison.Ordinal))
            {
                reason = "category filter";
                return false;
            }

            return true;
        }

        /// <summary>
        /// <c>mp = max(b.getMultPrice(), vend.multPrice)</c>, forced to <b>1</b> by <c>nocheap</c> —
        /// <c>PipPageVend.as:154-158, 180-183</c>.
        ///
        /// <para><b>The max is the whole rule.</b> <c>setRndBuys</c> may already have raised
        /// <c>multPrice</c> to 0.8..1.3 (<c>Vendor.as:132-135</c>) and <c>PipBuck.setPage</c> then
        /// overwrites it with <c>pers.barterMult</c> (<c>PipBuck.as:414</c>) — so a barter perk that
        /// <i>lowers</i> the multiplier cannot undercut the vendor's own markup. Taking the product, or
        /// the player's value alone, is a different (cheaper or dearer) shelf.</para>
        /// </summary>
        public static float BuyMultiplier(VendorStock row, VendorItemFacts item, float vendorMult)
        {
            if (row.Nocheap) return 1f;
            return Math.Max(item.MultPrice, vendorMult);
        }

        /// <summary>
        /// The unit price a buy row shows — <c>round(price * mp)</c>
        /// (<c>PipPageVend.as:154-158, 557</c>).
        ///
        /// <para><c>price</c> here is <c>Item.getPrice()</c>, which the port approximates as
        /// <c>@price * sost * pmult</c>. The Oracle's extra factors — the <c>&lt;com price&gt;</c> node
        /// and the <c>variant &gt; 0</c> ×3 rule (<c>Item.as:350-374</c>) — are not in the port's item
        /// model, and are named as a gap in the page's summary rather than guessed at.</para>
        ///
        /// <para><b>Except for weapons, where <c>&lt;com price&gt;</c> is the ONLY price.</b> No
        /// <c>&lt;weapon&gt;</c> row in AllData carries a root <c>@price</c> — measured: 0 of 213 — so
        /// the <c>com[0].@price</c> arm is not an override there, it is the whole answer. The vendor
        /// source supplies it as <see cref="VendorItemFacts.Price"/>; the <c>variant &gt; 0</c> ×3 rule
        /// remains a named gap.</para>
        /// </summary>
        public static int BuyUnitPrice(VendorStock row, VendorItemFacts item, float vendorMult,
            out float multiplier)
        {
            multiplier = BuyMultiplier(row, item, vendorMult);

            float basePrice = item.Price;
            if (row.Sost > 0f && row.Sost != 1f) basePrice *= row.Sost;
            if (row.HasPmult) basePrice *= row.Pmult;

            // Item.as:357-367 — a variant row with no second `<com>` costs THREE TIMES the base. Only
            // weapons reach it in practice: the whole rule lives inside `com.length() && com.@price`
            // (`:354`), and a weapon is the one table that prices exclusively through `<com>`.
            if (row.Variant > 0 && row.Tip == "weapon") basePrice *= 3f;

            return (int)Math.Round(basePrice * multiplier);
        }

        /// <summary>
        /// How many the player can still add, given the caps already committed —
        /// <c>floor((money - kolBou) / unitPrice)</c> (<c>PipPageVend.as:705-714</c>).
        /// </summary>
        public static int ClampToAffordable(int requested, int unitPrice, int capsRemaining)
        {
            if (requested <= 0) return 0;
            if (unitPrice <= 0) return requested;

            int affordable = capsRemaining / unitPrice;
            if (affordable < 0) affordable = 0;
            return requested < affordable ? requested : affordable;
        }

        /// <summary>
        /// The caps owed for a selection — <c>Σ price * mp * bou</c>, rounded <b>up</b> against the
        /// player (<c>PipPageVend.as:719, 948</c>).
        /// </summary>
        public static int BuyTotal(float kolBou) => (int)Math.Ceiling(kolBou);

        /// <summary>
        /// The caps a sale pays — <c>floor(kolSell)</c> (<c>PipPageVend.as:990</c>). The asymmetry with
        /// <see cref="BuyTotal"/> is the Oracle's: the vendor keeps the fraction on both sides.
        /// </summary>
        public static int SellTotal(float kolSell) => (int)Math.Floor(kolSell);

        /// <summary>
        /// Whether the vendor can pay for a sale: refused only outside a base and only when the purse
        /// is short (<c>PipPageVend.as:962-966</c>).
        /// </summary>
        public static bool CanPayForSale(VendorPageContext ctx, int vendorMoney, float kolSell)
        {
            if (ctx.InBase) return true;
            return (int)Math.Ceiling(kolSell) <= vendorMoney;
        }

        // ── Repair ────────────────────────────────────────────────────────────

        /// <summary>
        /// The repair price — <c>ceil(price * (maxhp-hp)/maxhp * vend.multPrice * mp)</c>
        /// (<c>PipPageVend.as:594</c>), with <c>mp = 1</c> for weapons and <c>priceRepArmor</c> for
        /// armour (<c>:829-850</c>).
        /// </summary>
        public static int RepairPrice(float basePrice, float hp, float maxHp, float vendorMult, float mp)
        {
            if (maxHp <= 0f) return 0;

            float damage = (maxHp - hp) / maxHp;
            if (damage <= 0f) return 0;

            return (int)Math.Ceiling(basePrice * damage * vendorMult * mp);
        }

        /// <summary>
        /// Whether a damaged item is listed — weapons exclude tips 0 and 4 and <c>respect == 1</c>
        /// (<c>PipPageVend.as:323-343</c>); armour is listed only for tips below 3 (<c>:344-360</c>).
        /// </summary>
        public static bool IsRepairable(string tip, float hp, float maxHp, bool respects = false)
        {
            if (maxHp <= 0f || hp >= maxHp) return false;
            if (respects) return false;

            if (tip == "weapon") return true;
            if (tip == "armor") return true;
            return false;
        }

        // ── Sorting ───────────────────────────────────────────────────────────

        /// <summary>
        /// The Buy list's sort: <c>["sort","barter","price"]</c> ascending (<c>PipPageVend.as:213</c>),
        /// where <c>sort</c> is the localised category name — so the shelf groups by category, then by
        /// the barter gate, then by price.
        /// </summary>
        public static void SortBuyRows(List<VendorBuyRow> rows)
        {
            rows.Sort((a, b) =>
            {
                int bySort = string.CompareOrdinal(a.SortName ?? string.Empty, b.SortName ?? string.Empty);
                if (bySort != 0) return bySort;

                int byBarter = a.Barter.CompareTo(b.Barter);
                if (byBarter != 0) return byBarter;

                int byPrice = a.Price.CompareTo(b.Price);
                if (byPrice != 0) return byPrice;

                // Ordinal tie-break so two rows of equal price keep a stable, reproducible order.
                return string.CompareOrdinal(a.Rid ?? string.Empty, b.Rid ?? string.Empty);
            });
        }

        /// <summary>The Sell list's sort: <c>["sort","wtip","price"]</c> (<c>PipPageVend.as:278</c>).</summary>
        public static void SortSellRows(List<VendorSellRow> rows)
        {
            rows.Sort((a, b) =>
            {
                int bySort = string.CompareOrdinal(a.SortName ?? string.Empty, b.SortName ?? string.Empty);
                if (bySort != 0) return bySort;

                int byTip = string.CompareOrdinal(a.Tip ?? string.Empty, b.Tip ?? string.Empty);
                if (byTip != 0) return byTip;

                int byPrice = a.Price.CompareTo(b.Price);
                if (byPrice != 0) return byPrice;

                return string.CompareOrdinal(a.Rid ?? string.Empty, b.Rid ?? string.Empty);
            });
        }

        /// <summary>The Repair list's sort: <c>price</c> ascending (<c>PipPageVend.as:363</c>).</summary>
        public static void SortRepairRows(List<VendorRepairRow> rows)
        {
            rows.Sort((a, b) =>
            {
                int byPrice = a.Price.CompareTo(b.Price);
                if (byPrice != 0) return byPrice;
                return string.CompareOrdinal(a.Rid ?? string.Empty, b.Rid ?? string.Empty);
            });
        }
    }

    /// <summary>
    /// The player-side questions <see cref="VendorTradeRules.AdmitBuyRow"/> asks. Six methods, one per
    /// guard that needs the bag — kept as an interface so the filter can be run against a fixture with
    /// no inventory at all.
    /// </summary>
    public interface IVendorBuyFacts
    {
        /// <summary>The player already knows this schematic.</summary>
        bool OwnsScheme(string itemId);

        /// <summary>The player owns this weapon at this variant or a higher one.</summary>
        bool OwnsWeaponVariantAtLeast(string itemId, int variant);

        /// <summary>The player owns this armour.</summary>
        bool OwnsArmor(string itemId);

        /// <summary>The player owns this implant / artefact.</summary>
        bool OwnsArt(string itemId);

        /// <summary><c>trig(name) == 1</c>, with the Oracle's coercing comparison.</summary>
        bool TriggerIsOne(string triggerName);
    }
}
