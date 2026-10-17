using System;
using System.Collections.Generic;
using PFE.Core.Rng;
using PFE.Data.Definitions.Campaign;

namespace PFE.Systems.Trade
{
    /// <summary>
    /// Which of the three construction branches a vendor takes — AS3 <c>Vendor</c>'s <c>param4</c>
    /// (<c>Vendor.as:24, 136-147</c>).
    /// </summary>
    public enum VendorKind
    {
        /// <summary>A regular trader: <c>10 + 6 * barterLvl</c> rolls, weapon pass included.</summary>
        Vendor = 0,

        /// <summary>A doctor: <c>5 + 3 * barterLvl</c> rolls, <b>no</b> weapon pass, 70/30 med/him.</summary>
        Doctor = 1,

        /// <summary>The <c>random</c> sentinel: a flat <c>30</c> rolls.</summary>
        Random = 2
    }

    /// <summary>
    /// One row of a vendor's shelf — the port of the <c>Item</c> objects <c>Vendor.buys</c> holds
    /// (<c>Vendor.as:10</c>). Only the fields the vendor's own rules touch are carried; the display
    /// fields (nazv, wtip, mass, price) belong to the item model and are resolved by the page.
    /// </summary>
    public sealed class VendorStock
    {
        /// <summary>Item id with any <c>^N</c> suffix stripped — AS3 <c>Item.id</c>.</summary>
        public string Id = "";

        /// <summary>AS3 <c>Item.variant</c>.</summary>
        public int Variant;

        /// <summary>AS3 <c>Item.tip</c> — <b>after</b> the <c>uniq -&gt; weapon</c> rewrite
        /// (<c>Item.as:153-156</c>). The refill skip list and the category filters read this.</summary>
        public string Tip = "";

        /// <summary>AS3 <c>Item.kol</c> — the stock count.</summary>
        public int Kol;

        /// <summary>AS3 <c>Item.sost</c> — condition, 1 unless a weapon was rolled with a low kol.</summary>
        public float Sost = 1f;

        /// <summary>AS3 <c>Item.barter</c> — minimum barter level to show the row.</summary>
        public int Barter;

        /// <summary>AS3 <c>Item.lvl</c> — minimum player level to show the row.</summary>
        public int Lvl;

        /// <summary>AS3 <c>Item.trig</c> — story trigger that must equal 1.</summary>
        public string Trig = "";

        /// <summary>AS3 <c>Item.noref</c> — never restocked.</summary>
        public bool Noref;

        /// <summary>AS3 <c>Item.nocheap</c> — the row price's multiplier is forced to 1.</summary>
        public bool Nocheap;

        /// <summary>AS3 <c>Item.hardinv</c> — only sold when <c>World.w.hardInv</c>.</summary>
        public bool Hardinv;

        /// <summary>AS3 <c>Item.pmult</c> (default 1) and whether the row set it.</summary>
        public float Pmult = 1f;
        public bool HasPmult;

        /// <summary>AS3 <c>Item.bou</c> — the pending-purchase quantity, zeroed by <see cref="VendorRuntime.Reset"/>.</summary>
        public int Bou;

        /// <summary>
        /// The <c>buys2</c> key: <c>id</c>, or <c>id^variant</c> — <c>Vendor.as:109-117</c>. The same key
        /// the page's row index <c>rid</c> uses (<c>PipPageVend.as:172-179</c>).
        /// </summary>
        public string Key => Variant > 0 ? Id + "^" + Variant : Id;
    }

    /// <summary>
    /// The item-table questions the vendor's own rules ask. Split out from
    /// <see cref="VendorRuntime"/> so the stock rules are testable without the 500-asset item database,
    /// and so the port's existing loot roller can be the production implementation rather than a second
    /// roller.
    ///
    /// <para><b>Both methods have exact oracle counterparts.</b> <see cref="Roll"/> is
    /// <c>LootGen.getRandom(tip, lvl)</c> (<c>LootGen.as:117-168</c>), which the vendor calls twice —
    /// once with <c>L_WEAPON</c> and a level gate (<c>Vendor.as:155</c>) and once with a category and the
    /// raw level (<c>:225</c>). <see cref="TryResolveItem"/> is the
    /// <c>AllData.d.item</c>/<c>.weapon</c>/<c>.armor</c> row lookup that <c>new Item(tip, id, kol)</c>
    /// performs (<c>Item.as:159-206</c>) and that <c>new Item(null, id, n)</c> also performs through
    /// <c>itemTip()</c> (<c>Item.as:253-289</c>).</para>
    /// </summary>
    public interface IVendorItemSource
    {
        /// <summary>
        /// AS3 <c>LootGen.getRandom(tip, lvl)</c>. Returns the pool's id — <b>which may carry a
        /// <c>^N</c> variant</b>, as a <c>uniq</c> roll does — or <c>null</c> when the pool is empty.
        /// </summary>
        string Roll(string tip, int level);

        /// <summary>
        /// The item row lookup behind <c>new Item(tip, id, kol)</c>: the item's own <c>@tip</c> (which
        /// becomes the row's <c>tip</c> — and is why a <c>uniq</c> roll ends up as <c>weapon</c>) and its
        /// <c>@kol</c> stack base (<c>1</c> when the row has none).
        /// </summary>
        /// <returns>False when the id resolves to no row at all — the oracle's <c>l.length() == 0</c>.</returns>
        bool TryResolveItem(string rawId, string requestedTip, out string resolvedTip, out int baseStock);
    }

    /// <summary>
    /// One row of <c>Item.save()</c> (<c>Item.as:522-534</c>) as it appears inside a vendor's saved array.
    /// </summary>
    [Serializable]
    public sealed class VendorStockSave
    {
        public string tip = "";
        public string id = "";
        public int kol;
        public float sost = 1f;
        public int barter;
        public int lvl;
        public string trig = "";
        public int variant;
    }

    /// <summary>
    /// The port of <c>fe.serv.Vendor</c> (<c>Vendor.as</c>) — a trader's stock, its own caps, its two
    /// ledgers and its price multiplier.
    ///
    /// <para><b>Why a plain class and not a <c>MonoBehaviour</c>.</b> The same reason
    /// <c>PipPageInvView</c> is one: every rule here is arithmetic over data, and a
    /// <c>MonoBehaviour</c> would make it unconstructible outside the engine — which is where its
    /// acceptance tests have to run.</para>
    ///
    /// <para><b>The three construction branches are all here</b> (<c>Vendor.as:24-123</c>): a
    /// <c>random</c> block (stocked by <see cref="SetRndBuys"/>), an xml block with <c>&lt;buy&gt;</c>
    /// rows (merged with any saved rows), and no xml at all (also <see cref="SetRndBuys"/>).</para>
    ///
    /// <para><b>Order matters and is reproduced.</b> <c>money</c> is randomised <i>after</i> the stock is
    /// built, and <c>multPrice</c> is raised by <see cref="SetRndBuys"/> before <c>PipBuck.setPage</c>
    /// later overwrites it with <c>pers.barterMult</c> — the row price takes the <i>max</i> of the two,
    /// so the earlier value survives only through that max (<c>06</c> Q10).</para>
    /// </summary>
    public sealed class VendorRuntime
    {
        /// <summary>AS3 <c>Vendor.id</c> — <c>xml.@id</c>, or null for an xml-less vendor.</summary>
        public string Id { get; private set; }

        /// <summary>AS3 <c>Vendor.buys</c> — the shelf, in build order.</summary>
        public readonly List<VendorStock> Buys = new List<VendorStock>();

        /// <summary>AS3 <c>Vendor.buys2</c> — the same rows keyed by <see cref="VendorStock.Key"/>.</summary>
        public readonly Dictionary<string, VendorStock> Buys2 =
            new Dictionary<string, VendorStock>(StringComparer.Ordinal);

        /// <summary>AS3 <c>Vendor.kolBou</c> — the running total of the current purchase selection.</summary>
        public float KolBou;

        /// <summary>AS3 <c>Vendor.kolSell</c> — the running total of the current sale selection.</summary>
        public float KolSell;

        /// <summary>AS3 <c>Vendor.money</c> — the vendor's own caps.</summary>
        public int Money;

        /// <summary>AS3 <c>Vendor.multPrice</c> (default 1, <c>Vendor.as:22</c>).</summary>
        public float MultPrice = 1f;

        /// <summary>AS3 <c>Vendor.xml</c> — the block this vendor was built from, or null.</summary>
        public VendorDefinition Xml { get; private set; }

        private readonly IRngService _rng;
        private readonly IVendorItemSource _items;

        /// <summary>
        /// The xml branch: one row per <c>&lt;buy&gt;</c>, each merged with a saved row of the same
        /// <c>id</c>+<c>variant</c> (<c>Vendor.as:52-96</c>).
        /// </summary>
        public VendorRuntime(VendorDefinition xml, IRngService rng, IVendorItemSource items,
            IReadOnlyList<VendorStockSave> saved = null)
        {
            _rng = rng ?? throw new ArgumentNullException(nameof(rng));
            _items = items ?? throw new ArgumentNullException(nameof(items));
            Xml = xml;

            if (xml != null && !xml.IsRandom)
            {
                foreach (VendorStockEntry row in xml.stock)
                {
                    if (row == null || string.IsNullOrEmpty(row.itemId)) continue;

                    var stock = new VendorStock
                    {
                        Id = row.itemId,
                        Variant = row.variant,
                        Kol = row.stockCount,
                        Barter = row.barter,
                        Lvl = row.level,
                        Trig = row.trigger ?? "",
                        Noref = row.noRefill,
                        Nocheap = row.noCheap,
                        Hardinv = row.hardInventory,
                        Pmult = row.hasPriceMultiplier ? row.priceMultiplier : 1f,
                        HasPmult = row.hasPriceMultiplier
                    };

                    // `new Item(null, @id, @n)` runs itemTip() — the tip comes from the item table, not
                    // from the row. Without it the refill skip list cannot tell a weapon from a potion.
                    stock.Tip = _items.TryResolveItem(row.StockKey, null, out string resolvedTip, out _)
                        ? resolvedTip
                        : string.Empty;

                    // :85-96 — a saved entry with the same id AND variant overrides kol and sost.
                    if (saved != null)
                    {
                        for (int i = 0; i < saved.Count; i++)
                        {
                            if (saved[i].variant == stock.Variant &&
                                string.Equals(saved[i].id, stock.Id, StringComparison.Ordinal))
                            {
                                stock.Kol = saved[i].kol;
                                stock.Sost = saved[i].sost;
                                break;
                            }
                        }
                    }

                    Buys.Add(stock);
                }
            }
            else if (xml != null && xml.IsRandom && saved != null)
            {
                // :34-44 — the saved branch: rebuild the rows straight from the save, no roll.
                foreach (VendorStockSave row in saved)
                {
                    Buys.Add(new VendorStock
                    {
                        Id = row.id ?? "",
                        Variant = row.variant,
                        Tip = row.tip ?? "",
                        Kol = row.kol,
                        Sost = row.sost
                    });
                }
            }

            if (xml != null) Id = xml.vendorId;

            RebuildBuys2();
            RollMoney();
        }

        /// <summary>
        /// The no-xml branch: <c>new Vendor(level, null, null, kind)</c> → <c>setRndBuys</c>
        /// (<c>Vendor.as:99-102</c>). <see cref="Id"/> stays null, which is why <see cref="Save"/>
        /// returns null for it (<c>Vendor.as:314-320</c>).
        /// </summary>
        public VendorRuntime(int level, VendorKind kind, int barterLevel, IRngService rng,
            IVendorItemSource items)
        {
            _rng = rng ?? throw new ArgumentNullException(nameof(rng));
            _items = items ?? throw new ArgumentNullException(nameof(items));
            SetRndBuys(level, kind, barterLevel);
            RollMoney();
        }

        /// <summary>AS3 <c>Vendor.as:118-122</c>: <c>round(random*450+50)</c>, doubled 20% of the time.</summary>
        private void RollMoney()
        {
            Money = (int)Math.Round(_rng.NextFloat() * 450f + 50f);
            if (_rng.NextFloat() < 0.2f) Money *= 2;
        }

        /// <summary>
        /// AS3 <c>Vendor.as:107-117</c>: rebuild <c>buys2</c> from <c>buys</c>, keying a variant row
        /// <c>id^variant</c>. Called after the constructor and again after a random restock
        /// (<c>Vendor.as:276-290</c>).
        /// </summary>
        private void RebuildBuys2()
        {
            Buys2.Clear();
            for (int i = 0; i < Buys.Count; i++)
            {
                VendorStock row = Buys[i];
                if (row == null) continue;
                Buys2[row.Key] = row;
            }
        }

        /// <summary>AS3 <c>Vendor.reset()</c> (<c>Vendor.as:260-268</c>): zero the buy ledger and every
        /// row's <c>bou</c>. Deliberately does <b>not</b> touch <c>multPrice</c> (see <c>06</c> Q10).</summary>
        public void Reset()
        {
            KolBou = 0f;
            for (int i = 0; i < Buys.Count; i++)
            {
                Buys[i].Bou = 0;
            }
        }

        /// <summary>
        /// AS3 <c>Vendor.refill()</c> (<c>Vendor.as:270-312</c>).
        ///
        /// <para>A <c>random</c> vendor rebuilds from scratch at level 100. Every other vendor walks its
        /// shelf and, for each row that is <b>not</b> <c>noref</c> and not gear
        /// (<c>armor</c>/<c>weapon</c>/<c>scheme</c>/<c>uniq</c>/<c>impl</c>), raises <c>kol</c> toward
        /// <c>ceil(n * limitBuys)</c> by <c>ceil(0.25 * lim)</c> — <b>approaching</b> the limit, not
        /// jumping to it (<c>06</c> Q9).</para>
        ///
        /// <para>The xml row is matched by <c>@id == item.id</c> — the <i>stripped</i> id, exactly as the
        /// oracle does, so a variant row never matches and is skipped. Reproduced rather than corrected,
        /// and noted here because it reads like a bug.</para>
        /// </summary>
        public void Refill(float limitBuys, int barterLevel)
        {
            if (Xml == null) return;

            if (string.Equals(Id, "random", StringComparison.Ordinal))
            {
                Buys.Clear();
                Buys2.Clear();
                SetRndBuys(100, VendorKind.Random, barterLevel);
                RebuildBuys2();
                return;
            }

            for (int i = 0; i < Buys.Count; i++)
            {
                VendorStock item = Buys[i];

                if (item.Noref || IsGear(item.Tip)) continue;

                VendorStockEntry buy = FindXmlBuy(item.Id);
                if (buy == null || buy.stockCount == 0) continue;

                float lim = (float)Math.Ceiling(buy.stockCount * limitBuys);
                if (item.Kol < lim)
                {
                    item.Kol = (int)Math.Min(lim, item.Kol + Math.Ceiling(0.25 * lim));
                }
            }
        }

        /// <summary>The oracle's gear skip list — <c>Vendor.as:299</c>.</summary>
        private static bool IsGear(string tip)
        {
            return tip == "armor" || tip == "weapon" || tip == "scheme" || tip == "uniq" || tip == "impl";
        }

        private VendorStockEntry FindXmlBuy(string itemId)
        {
            if (Xml?.stock == null) return null;
            for (int i = 0; i < Xml.stock.Count; i++)
            {
                VendorStockEntry row = Xml.stock[i];
                if (row != null && string.Equals(row.itemId, itemId, StringComparison.Ordinal)) return row;
            }
            return null;
        }

        /// <summary>
        /// AS3 <c>Vendor.save()</c> (<c>Vendor.as:314-327</c>): an array of <c>Item.save()</c> objects,
        /// or <b>null</b> when the vendor has no id — which is what makes an xml-less vendor absent from
        /// the save rather than an empty array (<c>06</c> Q8).
        /// </summary>
        public List<VendorStockSave> Save()
        {
            if (Id == null) return null;

            var rows = new List<VendorStockSave>(Buys.Count);
            for (int i = 0; i < Buys.Count; i++)
            {
                VendorStock row = Buys[i];
                if (row == null) continue;
                rows.Add(new VendorStockSave
                {
                    tip = row.Tip ?? "",
                    id = row.Id ?? "",
                    kol = row.Kol,
                    sost = row.Sost,
                    barter = row.Barter,
                    lvl = row.Lvl,
                    trig = row.Trig ?? "",
                    variant = row.Variant
                });
            }
            return rows;
        }

        /// <summary>
        /// AS3 <c>Vendor.setRndBuys(level, type)</c> (<c>Vendor.as:125-258</c>) — how a random trader is
        /// stocked, and how a doctor's or a plain vendor's <i>generated</i> stock would be.
        ///
        /// <para><b>The random-draw order is load-bearing and is reproduced exactly.</b> The oracle
        /// interleaves its draws with the loop, and a port that hoisted them would produce a different
        /// shelf for the same seed:</para>
        /// <list type="number">
        /// <item><description><c>:132-135</c> — 70% chance <c>multPrice = floor(random*6+8)/10</c>.</description></item>
        /// <item><description><c>:136-148</c> — the count, then <c>round(count * (0.5 + random*0.7))</c>.</description></item>
        /// <item><description><c>:149</c> — <c>weapons = count * (0.1 + random*0.3)</c>.</description></item>
        /// <item><description>per iteration: the weapon pass (<c>:151-170</c>) or the weighted category
        /// roll (<c>:171-254</c>), each with its own further draws.</description></item>
        /// </list>
        ///
        /// <para><b><paramref name="barterLevel"/> is a parameter, not a global.</b> The oracle reads
        /// <c>World.w.pers.barterLvl</c> (<c>Vendor.as:142/146</c>); threading it through keeps this
        /// class constructible outside the engine and its fixtures deterministic.</para>
        /// </summary>
        public void SetRndBuys(int level, VendorKind kind, int barterLevel)
        {
            // :132-135
            if (_rng.NextFloat() < 0.7f)
            {
                MultPrice = (float)Math.Floor(_rng.NextFloat() * 6f + 8f) / 10f;
            }

            int count;
            if (kind == VendorKind.Random) count = 30;                          // :136-138
            else if (kind == VendorKind.Doctor) count = 5 + 3 * barterLevel;    // :139-142
            else count = 10 + 6 * barterLevel;                                  // :143-146

            count = (int)Math.Round(count * (0.5f + _rng.NextFloat() * 0.7f));  // :148

            int weaponTarget = (int)(count * (0.1f + _rng.NextFloat() * 0.3f)); // :149

            for (int i = 0; i < count; i++)
            {
                if (i < weaponTarget && kind != VendorKind.Doctor)              // :151-170
                {
                    StockWeapon(level);
                }
                else
                {
                    StockCategory(level, kind);                                 // :171-254
                }
            }
        }

        private void StockWeapon(int level)
        {
            string raw = _items.Roll("weapon", 1 + level / 4);                  // :155
            if (string.IsNullOrEmpty(raw)) return;

            // :156 — `new Item(L_WEAPON, id, 1)`: kol forced to 1, and nkol==1 gives sost 0.6..0.85.
            string key = raw;
            if (Buys2.ContainsKey(key)) return;                                 // :157

            var item = new VendorStock
            {
                Id = BaseOf(raw),
                Variant = VariantOf(raw),
                Tip = "weapon",
                Kol = 1,
                Sost = 0.6f + _rng.NextFloat() * 0.25f                         // Item.as:185-190
            };

            if (_rng.NextFloat() < 0.2f)                                        // :159-166
            {
                item.Barter = (int)Math.Floor(_rng.NextFloat() * level / 4f + 1f);
                if (item.Barter > 5) item.Barter = 5;
            }

            Buys.Add(item);
            Buys2[key] = item;
        }

        private void StockCategory(int level, VendorKind kind)
        {
            int n = (int)Math.Floor(_rng.NextFloat() * 110f);                   // :173

            string tip;
            if (kind == VendorKind.Doctor)
            {
                tip = n < 70 ? "med" : "him";                                   // :174-181
            }
            else if (n < 5) tip = "uniq";                                       // :182-184
            else if (n < 10) tip = "scheme";                                    // :185-187
            else if (n < 25) tip = "med";                                       // :188-190
            else if (n < 35) tip = "him";                                       // :191-193
            else if (n < 55) tip = "e";                                         // :194-196  (L_EXPL)
            else if (n < 60) tip = "compa";                                     // :197-199
            else if (n < 65) tip = "compw";                                     // :200-202
            else if (n < 70) tip = "compe";                                     // :203-205
            else if (n < 75) tip = "compm";                                     // :206-208
            else tip = "a";                                                     // :209-211  (L_AMMO)

            string raw = _items.Roll(tip, level);                               // :225
            if (string.IsNullOrEmpty(raw)) return;                              // :226

            string key = raw;
            string id = BaseOf(raw);
            int variant = VariantOf(raw);

            if (!_items.TryResolveItem(raw, tip, out string resolvedTip, out int baseStock))
            {
                // The oracle throws inside `new Item(...)` and the row is dropped either way.
                return;
            }

            // `new Item(tip, id)` — kol from the row's @kol or 1 (Item.as:200-205); sost stays 1 for
            // anything that is not a weapon/armour (Item.as:182-199).
            var item = new VendorStock
            {
                Id = id,
                Variant = variant,
                Tip = resolvedTip,
                Kol = baseStock,
                Sost = 1f
            };

            if (!Buys2.ContainsKey(key))
            {
                if (_rng.NextFloat() < 0.3f)                                    // :230-238
                {
                    item.Lvl = (int)Math.Floor(_rng.NextFloat() * level + 1f);
                    if (item.Lvl > 5) item.Lvl = 5;
                }

                if (tip == "a")                                                 // :239-242
                {
                    item.Kol = (int)Math.Round(item.Kol * (3f + _rng.NextFloat() * 12f));
                }
                else if (tip != "uniq" && tip != "scheme")                      // :243-246
                {
                    item.Kol = (int)Math.Round(item.Kol * (1f + _rng.NextFloat() * 4f));
                }

                Buys.Add(item);
                Buys2[key] = item;
            }
            else if (tip != "uniq" && tip != "scheme")                          // :248-251
            {
                Buys2[key].Kol += item.Kol;
            }
        }

        // ── the `^N` helpers, shared with the parser's SplitVariant ──────────────

        private static string BaseOf(string rawId)
        {
            CampaignDataParser.SplitVariant(rawId, out string baseId, out _);
            return baseId;
        }

        private static int VariantOf(string rawId)
        {
            CampaignDataParser.SplitVariant(rawId, out _, out int variant);
            return variant;
        }
    }
}
