namespace PFE.Data.Definitions
{
    /// <summary>
    /// The three AS3 unit classes that build their visual from their <b>own id</b> rather than from a
    /// <c>&lt;vis vclass='…'/&gt;</c> or a <c>blit</c> sheet.
    ///
    /// <para><b>The oracle.</b> <c>UnitTrap.as:22</c>, <c>UnitTrigger.as:56</c> and
    /// <c>UnitDamager.as:54</c> all open their constructor with the same line —
    /// <c>vis = Res.getVis("vis" + id, vismtrap)</c> — i.e. <i>derive the symbol name from the id, and
    /// fall back to the shared <c>vismtrap</c> symbol when no such class exists</i>. That is the only
    /// visual rule in the game that is not carried by data, and it is why the importer, which knows only
    /// <c>vclass</c> and <c>blit</c>, resolved <b>none</b> of these ten units and left them as
    /// colliders with no art.</para>
    ///
    /// <para><b>Measured, not assumed.</b> Of the 83 unit definitions with no sprite and no sheet,
    /// exactly ten have a <c>DefineSprite_*_vis&lt;id&gt;</c> symbol in the SWF export:
    /// <c>damexpl1 damgren damshot encl mtrap mwall trigcans triglaser trigplate trigridge</c>. Nine are
    /// the trap family above; <c>encl</c> is the enclave family root, which composes to a variant before
    /// it ever spawns. Every other unresolved unit genuinely has no <c>vis&lt;id&gt;</c> art, so the rule
    /// cannot silently hand a unit the wrong picture.</para>
    /// </summary>
    public static class TrapVisualNaming
    {
        /// <summary>
        /// <c>"vis" + id</c> — <c>Res.getVis("vis" + id, vismtrap)</c>. Returned as a candidate, not as an
        /// answer: the importer still has to find the symbol folder, which is exactly the oracle's own
        /// fall-through to <c>vismtrap</c>.
        /// </summary>
        public static string CandidateFor(string unitId)
        {
            return string.IsNullOrEmpty(unitId) ? null : "vis" + unitId;
        }

        /// <summary>
        /// The shared fallback symbol — <c>vismtrap</c>. Only the trap family has it; <c>encl</c> does not
        /// and must keep its unresolved state rather than borrow a bear trap's picture.
        /// </summary>
        public const string SharedTrapSymbol = "vismtrap";

        /// <summary>
        /// Whether <paramref name="unitId"/> belongs to the three classes that use the derived-name rule.
        /// Used only to decide whether the <c>vismtrap</c> fallback applies — the derived name itself is
        /// tried for every unit, because that is what the oracle does.
        /// </summary>
        public static bool UsesSharedTrapFallback(string unitId)
        {
            if (string.IsNullOrEmpty(unitId)) return false;

            return unitId.StartsWith("dam", System.StringComparison.Ordinal)
                || unitId.StartsWith("trig", System.StringComparison.Ordinal)
                || unitId == "mtrap"
                || unitId == "trap"
                || unitId == "trcans"
                || unitId == "trlaser"
                || unitId == "trplate"
                || unitId == "trridge";
        }
    }
}
