using System;
using System.Collections.Generic;
using UnityEngine;
using PFE.ModAPI;

namespace PFE.Data.Definitions.Campaign
{
    /// <summary>
    /// One <c>&lt;buy&gt;</c> row of a vendor block — the port of the fields
    /// <c>fe.serv.Vendor</c>'s constructor reads off <c>xml.buy</c> (<c>Vendor.as:55-96</c>).
    ///
    /// <para><b>All nine attributes the oracle reads are here, and nothing else.</b> They are the
    /// eight <c>@</c> tests in the constructor plus the <c>id</c> itself:</para>
    /// <code>
    /// id  n  barter  lvl  trigger  noref  nocheap  hardinv  pmult      // Vendor.as:56-84
    /// </code>
    ///
    /// <para><b>The <c>^N</c> variant is split at parse time, not carried in the id.</b> AS3 strips it
    /// inside <c>Item</c>'s constructor (<c>Item.as:132-140</c>): when the second-to-last character is
    /// <c>'^'</c>, <c>variant = int(lastChar)</c> and <c>id = substr(0, len-2)</c>. <c>Vendor</c> then
    /// rebuilds the key <c>id + "^" + variant</c> for <c>buys2</c> (<c>Vendor.as:109-117</c>). Keeping
    /// the split fields (rather than a single raw string) is what lets <see cref="StockKey"/> reproduce
    /// that key exactly — and the key is load-bearing, because two variants of one weapon must not
    /// merge.</para>
    /// </summary>
    [Serializable]
    public class VendorStockEntry
    {
        [Tooltip("Item id with any '^N' variant suffix already stripped (AS3 Item.id)")]
        public string itemId = "";

        [Tooltip("Weapon variant, 0 when the row's id carried no '^N' (AS3 Item.variant)")]
        public int variant = 0;

        [Tooltip("Stock count / restock-limit base — AS3 buy.@n")]
        public int stockCount = 0;

        [Tooltip("Minimum barter level to show the row — AS3 buy.@barter (0 = attribute absent)")]
        public int barter = 0;

        [Tooltip("Minimum player level to show the row — AS3 buy.@lvl (0 = attribute absent)")]
        public int level = 0;

        [Tooltip("Story trigger that must equal 1 — AS3 buy.@trigger ('' = attribute absent)")]
        public string trigger = "";

        [Tooltip("Never restocked — AS3 buy.@noref (presence test, Vendor.as:69-72)")]
        public bool noRefill = false;

        [Tooltip("Price multiplier forced to 1 — AS3 buy.@nocheap (presence test, Vendor.as:73-76)")]
        public bool noCheap = false;

        [Tooltip("Only sold when World.w.hardInv — AS3 buy.@hardinv (presence test, Vendor.as:77-80)")]
        public bool hardInventory = false;

        [Tooltip("Extra price multiplier — AS3 buy.@pmult")]
        public float priceMultiplier = 0f;

        [Tooltip("Whether @pmult was present. A presence test in the oracle, so pmult='0' is a real zero price.")]
        public bool hasPriceMultiplier = false;

        /// <summary>
        /// The <c>buys2</c> key: <c>id</c>, or <c>id^variant</c> when the variant is non-zero —
        /// AS3 <c>Vendor.as:109-117</c>. This is the same key the page's row index <c>rid</c> uses
        /// (<c>PipPageVend.as:172-179</c>), so a port that keys by bare <c>id</c> silently merges
        /// <c>shotgun</c> and <c>shotgun^1</c>.
        /// </summary>
        public string StockKey => variant > 0 ? itemId + "^" + variant : itemId;
    }

    /// <summary>
    /// One <c>&lt;task&gt;</c> row of a vendor block — the vendor's quest board
    /// (<c>PipPageVend.as:382-396</c>), gated by <c>PipPage.checkQuest</c> (<c>PipPage.as:1513-1543</c>).
    ///
    /// <para><b>Six attributes, not the four the research first listed.</b> A reading of the shipped
    /// <c>GameData.as</c> shows <c>trigger</c> and <c>man</c> as well as <c>id</c>/<c>skill</c>/
    /// <c>skilln</c>/<c>land</c>:</para>
    /// <list type="bullet">
    /// <item><description><c>trigger</c> — read by <c>checkQuest</c> itself (<c>PipPage.as:1526-1532</c>),
    /// and present on <c>collectPaintsB</c>/<c>collectPaintsC</c>.</description></item>
    /// <item><description><c>man</c> — <b>not</b> read by the vendor page, but read by the quest journal
    /// (<c>PipPageInfo.as:771</c>: <c>if(_loc2_.@man != "1")</c>), which is why it is carried rather than
    /// dropped. Present on every <c>vendor_zebr</c> and <c>vendor_sr</c> task.</description></item>
    /// </list>
    /// </summary>
    [Serializable]
    public class VendorTaskEntry
    {
        [Tooltip("Quest id the vendor offers — AS3 task.@id")]
        public string taskId = "";

        [Tooltip("Skill name for the gate — AS3 task.@skill ('' = absent)")]
        public string skill = "";

        [Tooltip("Minimum skill level for the gate — AS3 task.@skilln")]
        public int skillLevel = 0;

        [Tooltip("Land that must be reachable — AS3 task.@land ('' = absent)")]
        public string land = "";

        [Tooltip("Story trigger that must equal 1 — AS3 task.@trigger ('' = absent)")]
        public string trigger = "";

        [Tooltip("Manual-only flag read by the quest journal, not the vendor page — AS3 task.@man")]
        public string manual = "";
    }

    /// <summary>
    /// The parser's output for one <c>&lt;vendor&gt;</c> block. <b>Plain C#, no Unity type</b> — which is
    /// deliberate: <c>CampaignDataParser</c> is shared by the editor importer and the offline fixtures,
    /// and <c>ScriptableObject.CreateInstance</c> is an <c>ECall</c> that throws outside the engine. A
    /// parser that returned a <see cref="VendorDefinition"/> directly would therefore be unverifiable in
    /// the offline wall, which is where the import's own acceptance checks have to run.
    /// </summary>
    public sealed class VendorParseResult
    {
        public string vendorId = "";

        public readonly List<VendorStockEntry> stock = new List<VendorStockEntry>();

        public readonly List<VendorTaskEntry> tasks = new List<VendorTaskEntry>();

        /// <summary>
        /// Attribute names seen on this block's rows that no field above claims. Empty is the expected
        /// state; a non-empty list means the oracle grew an attribute the port would otherwise drop
        /// silently (rule #12). The importer logs it.
        /// </summary>
        public readonly List<string> unrecognisedAttributes = new List<string>();
    }

    /// <summary>
    /// The stock and quest board of one trader — the port's home for a <c>&lt;vendor&gt;</c> block of
    /// <c>GameData.d.vendor</c> (<c>GameData.as:2143-2700</c>, 13 blocks) and the data half of
    /// <c>fe.serv.Vendor</c>.
    ///
    /// <para><b>Why this asset exists at all.</b> The runtime object (<c>VendorRuntime</c>) is
    /// constructed <i>from</i> one of these; the page reads its rows. Before this asset the port had
    /// the vendor's arithmetic (<c>VendorInventory</c>) and no shelf to put on it — the research's G1.</para>
    ///
    /// <para><b><c>nope</c> and <c>random</c> are imported as empty blocks, not skipped.</b> They are the
    /// oracle's own sentinels: <c>nope</c> is the "paid in goods, not caps" marker the two doctor
    /// archetypes carry (<c>velvet</c>, <c>autodoc</c>), and <c>random</c> is stocked at runtime by
    /// <c>setRndBuys</c>. Importing them keeps the id space complete — a missing <c>nope</c> asset would
    /// read as "the NPC names a vendor that does not exist", which is a different (and wrong) report.</para>
    /// </summary>
    [CreateAssetMenu(fileName = "NewVendorDef", menuName = "PFE/Campaign/Vendor Definition")]
    public class VendorDefinition : VersionedData, IGameContent
    {
        [Header("Identity")]
        [Tooltip("Vendor id as it appears in <vendor id='…'> and in NpcDefinition.vendorId")]
        public string vendorId = "";

        [Header("Stock (<buy> rows)")]
        [Tooltip("Every buy row, in file order. Vendor.as keeps this order for buys[].")]
        public List<VendorStockEntry> stock = new List<VendorStockEntry>();

        [Header("Quest board (<task> rows)")]
        [Tooltip("Every task row, in file order.")]
        public List<VendorTaskEntry> tasks = new List<VendorTaskEntry>();

        // IGameContent implementation
        string IGameContent.ContentId => vendorId;
        ContentType IGameContent.ContentType => ContentType.Campaign;
        public override string DataId => vendorId;
        public override string DisplayName => vendorId;

        /// <summary>AS3 <c>xml.@id == "random"</c> — the block whose stock <c>setRndBuys</c> builds
        /// (<c>Vendor.as:34-46</c>).</summary>
        public bool IsRandom => string.Equals(vendorId, "random", StringComparison.Ordinal);

        /// <summary>AS3 <c>xml.@id == "nope"</c> — the empty sentinel the two doctor archetypes carry.
        /// An empty shelf is the point, not a failed import.</summary>
        public bool IsEmptySentinel => string.Equals(vendorId, "nope", StringComparison.Ordinal);

        protected override bool OnValidateData()
        {
            if (string.IsNullOrEmpty(vendorId))
            {
                Debug.LogWarning("VendorDefinition: vendorId is missing!");
                return false;
            }

            return true;
        }

        public override string[] GetReferencedDataIds()
        {
            var ids = new List<string>();
            if (stock != null)
            {
                foreach (VendorStockEntry entry in stock)
                {
                    if (entry != null && !string.IsNullOrEmpty(entry.itemId)) ids.Add(entry.itemId);
                }
            }
            if (tasks != null)
            {
                foreach (VendorTaskEntry task in tasks)
                {
                    if (task != null && !string.IsNullOrEmpty(task.taskId)) ids.Add(task.taskId);
                }
            }
            return ids.ToArray();
        }
    }
}
