namespace PFE.Core.Messages
{
    /// <summary>
    /// Published when a world interaction wants the trade page — the port's seam for
    /// <c>NPC.pip(4)</c> (<c>NPC.as:250-260</c>), which sets <c>pip.vendor</c>, <c>pip.npcId</c> and
    /// <c>pip.npcInter</c> and then calls <c>pip.onoff(4)</c>.
    ///
    /// <para><b>Why a message and not a direct call.</b> The NPC entity lives in the gameplay assembly
    /// and the page in the interface one; the Oracle's <c>World.w.pip</c> is a global that neither
    /// owns. The campaign already publishes <see cref="TravelMapOpenedMessage"/> for the same reason
    /// (a world interaction opening a device page), and the subscriber is the only place that knows
    /// both the vendor registry and the device.</para>
    ///
    /// <para><b>Ids only, no runtime object.</b> The subscriber resolves the <c>VendorRuntime</c> from
    /// <c>CampaignManager.Vendors</c>; a message that carried the vendor would let a stale instance
    /// survive a land rebuild, and the registry is the one place vendors are rebuilt.</para>
    /// </summary>
    public struct VendorOpenedMessage
    {
        public VendorOpenedMessage(string npcId, string vendorId, string npcInter)
        {
            NpcId = npcId ?? string.Empty;
            VendorId = vendorId ?? string.Empty;
            NpcInter = npcInter ?? string.Empty;
        }

        /// <summary><c>pip.npcId</c> — the persona that opened the page. Empty hides the Quests
        /// sub-tab (<c>PipPageVend.as:86-93</c>).</summary>
        public string NpcId { get; }

        /// <summary>The <c>&lt;vendor&gt;</c> block's id, as the NPC's <c>vendor</c> attribute names it.</summary>
        public string VendorId { get; }

        /// <summary><c>pip.npcInter</c> — <c>v</c>, <c>vr</c>, <c>doc</c>, <c>vdoc</c>, <c>adoc</c>,
        /// <c>travel</c>, <c>patient</c> or empty. Decides which sub-tabs exist
        /// (<c>PipPageVend.as:94-109</c>).</summary>
        public string NpcInter { get; }
    }

    /// <summary>
    /// Published when a world interaction wants the clinic page — the port's seam for
    /// <c>NPC.pip(6)</c> (<c>NPC.as:250-260</c>), reached from the <c>doc</c>/<c>vdoc</c> branch
    /// (<c>NPC.as:217-220</c>) and from <c>adoc</c> with <c>rep &gt; 1</c> (<c>:227-230</c>).
    ///
    /// <para><b>Why this exists at all when page 6 is still a stub.</b> Because the alternative is
    /// worse and was the reported symptom: the interaction was consumed, nothing opened, and the only
    /// trace was a warning in the log. The Oracle's answer to a doctor is "open page 6", and a stub
    /// that says "PipPageMed — not ported" is a truthful rendering of that answer. Reporting "no
    /// handler for kind 'Doctor'" is not: it describes the port's own gap, not the game's state.</para>
    ///
    /// <para><b>The vendor id IS carried, and that is the doctor's only road to its shelves.</b>
    /// <c>NPC.pip</c> writes all three device fields before it opens a page —
    /// <c>World.w.pip.vendor = this.vendor; pip.npcId = this.id; pip.npcInter = this.npcInter;
    /// pip.onoff(param1)</c> (<c>NPC.as:250-260</c>) — so a <c>doc</c> leaves the device holding its
    /// vendor while page 6 is up, and the Vendor tab then opens page 4 on that vendor. This message
    /// used to drop the field, with the note that the doctor reaches trade "through the clinic's own
    /// Repair redirect (<c>PipPageVend.as:480-484</c>)" — but that code runs the <i>other</i> way:
    /// it is the trade page's sub-tab 3 sending a <c>doc</c> to page 6
    /// (<c>if(page2 == 3 &amp;&amp; this.npcInter == "doc") { page2 = 1; pip.onoff(6); }</c>), not a
    /// clinic-to-trade edge. With the id dropped, a doctor's shelves were unreachable from anywhere.
    /// </para>
    /// </summary>
    public struct ClinicOpenedMessage
    {
        public ClinicOpenedMessage(string npcId, string npcInter, string vendorId = null)
        {
            NpcId = npcId ?? string.Empty;
            NpcInter = npcInter ?? string.Empty;
            VendorId = vendorId ?? string.Empty;
        }

        /// <summary><c>pip.npcId</c> — the persona that opened the page.</summary>
        public string NpcId { get; }

        /// <summary><c>pip.npcInter</c> — <c>doc</c>, <c>vdoc</c> or <c>adoc</c>. The clinic's repair
        /// redirect needs it to decide which page it may return to (<c>PipPageVend.as:480-484</c>).</summary>
        public string NpcInter { get; }

        /// <summary>
        /// <c>pip.vendor</c> — the doctor's own shelf, or empty for a doctor that has none
        /// (<c>autodoc</c> carries <c>vendor='nope'</c>, which resolves to nothing).
        /// </summary>
        public string VendorId { get; }
    }
}
