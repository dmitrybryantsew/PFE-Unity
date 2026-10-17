using System;
using System.Collections.Generic;
using PFE.Core.Rng;
using PFE.Data.Definitions.Campaign;

namespace PFE.Systems.Trade
{
    /// <summary>
    /// One live <see cref="VendorRuntime"/> per <c>&lt;vendor&gt;</c> block — the port of AS3
    /// <c>Game.vendors</c> (<c>Game.as:61, 220-229</c>).
    ///
    /// <para><b>Why a registry and not a field on the campaign manager.</b> The vendors are keyed by id
    /// and are read by the page (through the NPC that opened it) and written by the save and the refill
    /// sweep; <c>Game.vendors</c> is an array used as a map in exactly the same way
    /// (<c>this.vendors[_loc10_.id] = _loc10_</c>, <c>Game.as:229</c>). Keeping it a separate object
    /// means the refill and save rules are testable without standing up a <c>CampaignManager</c>, which
    /// needs a <c>CampaignCatalog</c>, a message broker and a container.</para>
    ///
    /// <para><b>The construction is one <c>new Vendor(0, xml, saved)</c> per block</b> — note the
    /// <b>level 0</b>, which matters only for the <c>random</c> block: <c>Vendor.as:220-229</c> passes
    /// <c>0</c> as <c>param1</c>, so the runtime stock of <c>random</c> is built at level 0 and only
    /// rebuilt at level 100 by <c>refill()</c> (<c>Vendor.as:281-283</c>).</para>
    /// </summary>
    public sealed class VendorRegistry
    {
        /// <summary>AS3 <c>Game.vendors</c> — keyed by vendor id, ordinal (ids are ASCII).</summary>
        private readonly Dictionary<string, VendorRuntime> _byId =
            new Dictionary<string, VendorRuntime>(StringComparer.Ordinal);

        /// <summary>Every live vendor, keyed by id.</summary>
        public IReadOnlyDictionary<string, VendorRuntime> All => _byId;

        public int Count => _byId.Count;

        /// <summary>
        /// Builds one vendor per definition. A null definition list builds nothing, which is the
        /// "catalogue absent" case and is deliberately silent — the caller reports it (the same
        /// contract <c>CampaignManager.Catalog</c> documents for a missing catalogue).
        /// </summary>
        /// <param name="definitions">The imported <c>&lt;vendor&gt;</c> blocks.</param>
        /// <param name="rng">The deterministic stream. <c>Loot</c> is the oracle's own domain — the
        /// comment on <c>RngStream.Loot</c> names "vendor inventory" explicitly.</param>
        /// <param name="items">The item-table seam (see <see cref="IVendorItemSource"/>).</param>
        /// <param name="saved">A loaded save's <c>Game.vendors</c> payload, or null for a fresh game.</param>
        public void Initialize(IReadOnlyList<VendorDefinition> definitions, IRngService rng,
            IVendorItemSource items, IReadOnlyDictionary<string, List<VendorStockSave>> saved = null)
        {
            _byId.Clear();
            if (definitions == null || rng == null || items == null) return;

            for (int i = 0; i < definitions.Count; i++)
            {
                VendorDefinition def = definitions[i];
                if (def == null || string.IsNullOrEmpty(def.vendorId)) continue;

                List<VendorStockSave> rows = null;
                saved?.TryGetValue(def.vendorId, out rows);

                // Game.as:222-229 — `new Vendor(0, xml, saved)`, then keyed by `vendor.id`.
                var vendor = new VendorRuntime(def, rng, items, rows);
                _byId[vendor.Id ?? def.vendorId] = vendor;
            }
        }

        /// <summary>The vendor with this id, or null. Mirrors the other catalogue lookups.</summary>
        public VendorRuntime Get(string vendorId)
        {
            if (string.IsNullOrEmpty(vendorId)) return null;
            return _byId.TryGetValue(vendorId, out VendorRuntime vendor) ? vendor : null;
        }

        /// <summary>
        /// AS3 <c>Game.refillVendors</c>'s first loop (<c>Game.as:510-513</c>): refill every vendor.
        ///
        /// <para>The second half of the oracle's method — flipping every trigger valued <c>"wait"</c> to
        /// <c>1</c> (<c>Game.as:514-520</c>) — belongs to the campaign's trigger table, not to the
        /// registry, so it stays with <c>CampaignManager</c>. The third line
        /// (<c>World.w.invent.good.kol = pers.goodHp</c>) and the <c>"refill"</c> info text are recorded
        /// there too rather than silently dropped.</para>
        /// </summary>
        public void RefillAll(float limitBuys, int barterLevel)
        {
            foreach (KeyValuePair<string, VendorRuntime> pair in _byId)
            {
                pair.Value.Refill(limitBuys, barterLevel);
            }
        }

        /// <summary>
        /// AS3 <c>Game.save</c>'s vendor arm (<c>Game.as:119-126</c>): one entry per vendor whose
        /// <c>save()</c> did not return null — which is why an xml-less vendor is absent from the
        /// payload rather than present-and-empty (<c>06</c> Q8).
        /// </summary>
        public Dictionary<string, List<VendorStockSave>> Save()
        {
            var payload = new Dictionary<string, List<VendorStockSave>>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, VendorRuntime> pair in _byId)
            {
                List<VendorStockSave> rows = pair.Value.Save();
                if (rows != null) payload[pair.Key] = rows;
            }
            return payload;
        }
    }
}
