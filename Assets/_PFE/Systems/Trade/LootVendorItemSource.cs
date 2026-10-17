using System;
using PFE.Systems.Loot;

namespace PFE.Systems.Trade
{
    /// <summary>
    /// The production <see cref="IVendorItemSource"/>: a random vendor's two catalog questions answered
    /// by the port's existing loot roller and item catalog, rather than by a second implementation of
    /// either.
    ///
    /// <para><b>Nothing is duplicated.</b> <see cref="Roll"/> forwards to
    /// <c>LootRoller.GetRandom</c>, which <i>is</i> the port of <c>LootGen.getRandom</c> — its own
    /// doc-comment names the vendor as an external caller (<c>Vendor.as:155</c>), which is why the
    /// oracle's method is public there. <see cref="TryResolveItem"/> forwards to
    /// <c>ILootItemCatalog</c>, which <i>is</i> the port of <c>Item</c>'s
    /// <c>AllData.d.item/weapon/armor</c> lookup.</para>
    ///
    /// <para><b>This class is the only ECall-bearing part of the trade subsystem.</b> The loot runtime
    /// loads a <c>TextAsset</c> from Resources on first use, so this adapter cannot run in the offline
    /// wall — which is exactly why the stocking <i>rule</i> lives on <see cref="VendorRuntime"/> behind
    /// this interface and its fixtures inject a fake. There is no logic here to test.</para>
    /// </summary>
    public sealed class LootVendorItemSource : IVendorItemSource
    {
        /// <summary>The pool-set key a vendor roll is attributed to. Only used for provenance on an
        /// unmatched key; the oracle's own table key at these call sites is <c>null</c>.</summary>
        public const string TableKey = "vendor";

        private readonly Func<LootContext> _contextFactory;

        /// <summary>
        /// The default: build a <c>LootContext</c> from the shared <c>LootRuntime</c>. The factory is
        /// injectable so the adapter can be exercised against a hand-built context.
        /// </summary>
        public LootVendorItemSource() : this(() => LootRuntime.CreateContext(TableKey)) { }

        public LootVendorItemSource(Func<LootContext> contextFactory)
        {
            _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        }

        /// <inheritdoc />
        /// <remarks>
        /// <c>LootGen.getRandom(tip, lvlGate)</c> — the vendor passes the level gate straight through
        /// (<c>Vendor.as:225</c>) and, for the weapon pass, <c>1 + level/4</c> (<c>Vendor.as:155</c>).
        /// A null context (the loot runtime failed to load) returns null, which the stocking rule treats
        /// as "the pool produced nothing" — the same shape as the oracle's empty-pool return.
        /// </remarks>
        public string Roll(string tip, int level)
        {
            LootContext context = _contextFactory();
            if (context == null) return null;

            return LootRoller.GetRandom(context, tip, level);
        }

        /// <inheritdoc />
        /// <remarks>
        /// <para><b>The returned tip is the <i>effective requested</i> tip, not the row's <c>@tip</c>.</b>
        /// That is the oracle's own shape: <c>Item.tip</c> is the caller's tip (with <c>uniq</c> rewritten
        /// to <c>weapon</c>, <c>Item.as:147-150</c>), while the row's <c>@tip</c> lands in a different
        /// field, <c>wtip</c> (<c>Item.as:171-173</c>). A port that returned the row's tip would make
        /// <c>checkCat</c> and the refill skip list disagree with the oracle.</para>
        ///
        /// <para>The one case that <i>does</i> read the row's tip is an empty request — the
        /// <c>new Item(null, @id, @n)</c> path the xml branch takes, which runs <c>itemTip()</c>
        /// (<c>Item.as:314-348</c>) and resolves item → weapon → armour in that order.</para>
        /// </remarks>
        public bool TryResolveItem(string rawId, string requestedTip, out string resolvedTip, out int baseStock)
        {
            resolvedTip = requestedTip ?? string.Empty;
            baseStock = 1;

            if (string.IsNullOrEmpty(rawId)) return false;

            // Item.as:132-140 — the row lookup is by the STRIPPED id.
            Data.Definitions.Campaign.CampaignDataParser.SplitVariant(rawId, out string id, out _);

            LootItemRow? row;
            if (string.IsNullOrEmpty(requestedTip))
            {
                row = LootRuntime.Catalog?.FindByAnyId(id, out resolvedTip);
                if (row == null) return false;
            }
            else
            {
                // Item.as:147-150 — `uniq` is looked up as a weapon.
                string lookupTip = requestedTip == "uniq" ? "weapon" : requestedTip;
                resolvedTip = lookupTip;
                row = LootRuntime.Catalog?.Find(lookupTip, id);
            }

            // Item.as:195-205 — the stack base is the row's @kol, or 1 when it has none.
            if (row.HasValue && row.Value.Kol.HasValue) baseStock = row.Value.Kol.Value;
            return true;
        }
    }
}
