using System;
using System.Text.RegularExpressions;

namespace PFE.Data.Definitions
{
    /// <summary>
    /// Reads the unit's <c>&lt;un&gt;</c> element — AS3's "unit extras" node — out of the unit's
    /// <c>AllData.as</c> text.
    ///
    /// <para>Exists for the same reason <see cref="UnitVulnerabilityParser"/> does: <c>PFE.Tests</c>
    /// references <c>PFE.Data</c> but <b>not</b> <c>PFE.Editor</c>, so a parse that lives only inside
    /// <c>UnitDataImporter</c> can be tested only by running Unity. The one thing this parse has to get
    /// right is a <em>guard</em>, and a wrong guard is wrong <b>silently</b> — exactly the class of
    /// defect that needs a test rather than a careful reading.</para>
    ///
    /// <para><b>Only <c>res</c> is read here, and that boundary is deliberate.</b> The node carries about
    /// nineteen attributes (<c>ss</c>, <c>glow</c>, <c>res</c>, <c>walker</c>, <c>sniper</c>, <c>och</c>,
    /// <c>stalk</c>, <c>grenader</c>, <c>enclweap</c>, <c>stay</c>, <c>dist</c>, <c>chootch</c>,
    /// <c>attch</c>, <c>attr</c>, <c>skill</c>, <c>one</c>, <c>plate</c>, <c>robot</c>, <c>tip</c>,
    /// <c>drop</c>). Closing the node is its own workstream; giving the other attributes writers before
    /// anything reads them is the trap <see cref="UnitDefinition.canResurrect"/> was already in.</para>
    /// </summary>
    public static class UnitExtrasParser
    {
        /// <summary>
        /// The zombie family id (<c>AllData.as:768</c>) — the <c>parent=</c> every <c>zombie0..9</c>
        /// variant carries, and the only family whose controller reads <c>res</c>.
        /// </summary>
        public const string ZombieFamilyId = "zombie";

        /// <summary>
        /// True when this unit's <c>&lt;un&gt;</c> element declares <c>res</c> — AS3
        /// <c>UnitZombie.isRes</c> (<c>UnitZombie.as:161-164</c>).
        ///
        /// <para><b>The guard is the whole point, because <c>res</c> has two owners in AS3 and they
        /// disagree about what it means.</b></para>
        /// <list type="bullet">
        /// <item><description><c>UnitZombie.getXmlParam()</c> (<c>UnitZombie.as:161-164</c>) tests
        /// <b>presence only</b> — <c>if(node0.un.@res.length()) isRes = true</c> — and ignores the value,
        /// which happens to be the literal <c>'1'</c>.</description></item>
        /// <item><description><c>UnitTrigger.getXmlParam()</c> (<c>UnitTrigger.as:117-119</c>) reads the
        /// <b>same attribute on the same node</b> as a resource NAME:
        /// <c>this.res = node0.un.@res</c>. Its four units (<c>trigcans</c>, <c>trigridge</c>,
        /// <c>trigplate</c>, <c>triglaser</c>) author <c>res='noise'</c>, <c>'damgren'</c>,
        /// <c>'damshot'</c>, <c>'hturret2'</c>.</description></item>
        /// </list>
        /// <para>So an unguarded read would mark those four traps as resurrecting units — a silent, wrong
        /// write. The discriminator is the unit's class, so <paramref name="parentId"/> decides: only the
        /// family whose controller owns the read gets it.</para>
        ///
        /// <para><b>Presence, not value.</b> AS3 never compares <c>res</c> against anything, so
        /// <c>res='1'</c> and <c>res='2'</c> mean the same thing. Testing the value instead would be a
        /// divergence that merely happens to agree with today's data — <c>res='1'</c> is authored by
        /// <c>zombie7/8/9</c> and by no other unit. (The remaining six <c>res='1'</c> hits in
        /// <c>AllData.as</c> are <c>&lt;upd&gt;</c> armour rows, which are not units at all.)</para>
        /// </summary>
        /// <param name="unitContent">The unit's own <c>&lt;unit&gt;…&lt;/unit&gt;</c> text, as sliced by
        /// the importer. A <c>&lt;un&gt;</c> node declared only on a <c>parent=</c> ancestor is therefore
        /// not seen — the same recorded inheritance divergence as
        /// <see cref="UnitVulnerabilityParser"/>.</param>
        /// <param name="parentId">The unit's <c>parent=</c> attribute; the family whose controller reads
        /// <c>res</c>.</param>
        public static bool ParseCanResurrect(string unitContent, string parentId)
        {
            if (string.IsNullOrEmpty(unitContent)) return false;

            // Ownership guard first: cheaper than the match, and it is the part that must never be
            // dropped. Ordinal because a unit id is an identifier, not display text.
            if (!string.Equals(parentId, ZombieFamilyId, StringComparison.Ordinal)) return false;

            // `<un\b`, not `<un\s`: a word boundary also admits `<un/>` and `<un>`, while still refusing
            // `<unit …>` — between `n` and `i` there is no boundary. All 52 nodes in AllData.as are
            // self-closing today, but AS3 is E4X, where `<un>` and `<un/>` are equivalent, so the looser
            // anchor costs nothing and cannot silently skip a row.
            Match unMatch = Regex.Match(unitContent, @"<un\b([^>]*)>");
            if (!unMatch.Success) return false;

            // `(?:^|\s)` is not decoration: it is what stops `res='…'` from also matching the tail of a
            // hypothetical `xres='…'`. No such attribute exists today; the anchor keeps that true.
            return Regex.IsMatch(unMatch.Groups[1].Value, @"(?:^|\s)res='");
        }
    }
}
