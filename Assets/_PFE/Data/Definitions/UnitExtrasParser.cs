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
    /// <para><b>Four attributes are read here; the node carries twenty-two.</b> The other eighteen
    /// (<c>ss</c> 9, <c>och</c> 11, <c>skill</c> 7, <c>stalk</c> 6, <c>dist</c> 6, <c>grenader</c> 4,
    /// <c>attr</c> 4, <c>attch</c> 4, <c>tip</c> 3, <c>enclweap</c> 3, <c>chootch</c> 3, <c>robot</c> 1,
    /// <c>plate</c> 1, <c>one</c> 1, <c>gryz</c> 1, <c>gren</c> 1, <c>glow</c> 1, <c>drop</c> 1,
    /// <c>bulb</c> 1 — the counts are over the 52 <c>&lt;un&gt;</c> nodes in <c>AllData.as</c>) are
    /// deliberately left unwritten: closing the node is its own workstream, and giving an attribute a
    /// writer before anything reads it is the trap <see cref="UnitDefinition.canResurrect"/> was already
    /// in. <c>stalk</c> and <c>dist</c> are the two that look closest to ready and are not — see the
    /// note on <see cref="ParseWalker"/>.</para>
    ///
    /// <para><b>Why these four are different.</b> <c>res</c>, <c>walker</c>, <c>sniper</c> and
    /// <c>stay</c> are each read by a ported code path, so an unread attribute here is a live divergence
    /// rather than a dormant one: <c>zebra3</c> alone authors <c>walker='1' sniper='1' stay='1'</c>
    /// (<c>AllData.as:361</c>) and the port dropped all three.</para>
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

            return Declares(UnAttributes(unitContent), "res");
        }

        /// <summary>
        /// True when this unit's <c>&lt;un&gt;</c> element declares <c>walker</c> — AS3
        /// <c>UnitRaider.walker</c> (<c>UnitRaider.as:331-334</c>: <c>if(node0.un.@walker.length())
        /// this.walker = true;</c>).
        ///
        /// <para><b>23 unit rows author it, and the port read none of them.</b> The flag's two consumers
        /// are <c>UnitRaider.as:151-155</c> (<c>walkSpeed = 2.5</c> instead of <c>maxSpeed</c>) and
        /// <c>:434</c> (the <c>walk</c> rather than <c>trot</c> animation), and the port has both of those
        /// paths already — <c>ArmedShooterBrain.ApplyDefinitionTuning</c> reads
        /// <see cref="UnitDefinition.isWalker"/> into <c>_isWalker</c> and uses it for the speed and the
        /// animation. Only the importer half was missing, so every one of those 23 units walked at its
        /// own <c>maxSpeed</c> and played <c>trot</c>.</para>
        ///
        /// <para><b>No ownership guard, unlike <c>res</c>.</b> <c>walker</c> has one owner in AS3 and no
        /// second meaning on another node — the <c>res</c> guard exists because two classes read one name
        /// with two meanings, and duplicating that machinery here would imply a hazard that is not
        /// present. The field is declared <i>on</i> <c>UnitRaider</c> (<c>:25</c>) and the attribute is
        /// read in the same constructor (<c>:331</c>); neither <c>Unit.as</c> nor <c>UnitPon.as</c>
        /// mentions <c>walker</c> at all. <c>UnitEncl</c> and <c>UnitRanger</c> only <i>consume</i> the
        /// inherited flag (<c>UnitEncl.as:148</c>, <c>UnitRanger.as:120</c>).</para>
        ///
        /// <para><b>Not the same as <c>&lt;move&gt;</c>.</b> <c>walker</c> is a boolean that pins the walk
        /// speed to a literal 2.5; <c>speed</c> on the <c>&lt;move&gt;</c> node is the authored maximum.
        /// They are read from two different elements and the port keeps them in two different fields
        /// (<see cref="UnitDefinition.isWalker"/> and <see cref="UnitDefinition.moveSpeed"/>).</para>
        /// </summary>
        /// <param name="unitContent">The unit's own <c>&lt;unit&gt;…&lt;/unit&gt;</c> text.</param>
        public static bool ParseWalker(string unitContent) => Declares(UnAttributes(unitContent), "walker");

        /// <summary>
        /// True when this unit's <c>&lt;un&gt;</c> element declares <c>sniper</c> — AS3
        /// <c>UnitRaider.sniper</c> (<c>UnitRaider.as:335-338</c>: <c>if(node0.un.@sniper.length())
        /// this.sniper = true;</c>).
        ///
        /// <para><b>17 unit rows author it.</b> The flag has two consumers in the oracle, both inside
        /// <c>UnitRaider.attack()</c>'s ranged arm: <c>:1509</c> — <c>if(!this.sniper) mazil = aiState == 4
        /// ? 5 : 16;</c> — and <c>:1525</c> — <c>if(this.aiAttackOch &gt; 0 &amp;&amp; (!this.sniper ||
        /// celUnit))</c>. So a sniper shoots with no aim jitter and only holds its burst cadence while it
        /// has a target in view.</para>
        ///
        /// <para><b>Presence, not value, exactly as <c>res</c>.</b> Every one of the 17 authors the
        /// literal <c>'1'</c>, and AS3 never compares it, so testing the value would be a divergence that
        /// merely happens to agree with today's data.</para>
        ///
        /// <para><b>It is not the same flag as the placement's <c>ai="sniper"</c>.</b> They are two
        /// different switches that happen to share a word: this one is the unit row's own
        /// (<c>node0.un</c>, read by <c>UnitRaider</c>'s constructor) and only suppresses jitter; the
        /// placement one is the room's (<c>param3.@ai</c>, read by <c>UnitRaider.as:236-241</c>) and also
        /// clears <c>stroll</c> and <c>moving</c> and raises <c>weaponSkill</c> to 2. <c>zebra3</c> authors
        /// this one; <c>RoomsSerial2.as:2151</c>'s <c>zebra</c> placement authors the other.</para>
        /// </summary>
        /// <param name="unitContent">The unit's own <c>&lt;unit&gt;…&lt;/unit&gt;</c> text.</param>
        public static bool ParseSniper(string unitContent) => Declares(UnAttributes(unitContent), "sniper");

        /// <summary>
        /// True when this unit's <c>&lt;un&gt;</c> element declares <c>stay</c> — AS3
        /// <c>UnitRaider.stroll</c>'s second writer (<c>UnitRaider.as:351-354</c>:
        /// <c>if(node0.un.@stay.length()) this.stroll = false;</c>).
        ///
        /// <para><b>Exactly one unit row authors it — <c>zebra3</c></b> (<c>AllData.as:361</c>, the same
        /// row that carries <c>walker='1' sniper='1'</c>). So the attribute is one unit's worth of
        /// behaviour, and it is the only unit-row writer of <c>stroll</c>: the other four are the
        /// <i>placement's</i> <c>ai</c> spellings (<c>:227-241</c>).</para>
        ///
        /// <para><b>This is the unit row's <c>stay</c>, not the room's.</b> <c>RoomsSerial2.as:3061</c>
        /// authors <c>stay="1"</c> on an <c>&lt;obj&gt;</c> placement, and nothing in AS3 reads
        /// <c>param3.@stay</c> — the only read anywhere is <c>node0.un.@stay</c>. That placement attribute
        /// is dead data in the oracle, and the port must not "fix" it by reading it: an <c>&lt;obj&gt;</c>
        /// <c>stay</c> means nothing to the original game.</para>
        /// </summary>
        /// <param name="unitContent">The unit's own <c>&lt;unit&gt;…&lt;/unit&gt;</c> text.</param>
        public static bool ParseStaysPut(string unitContent) => Declares(UnAttributes(unitContent), "stay");

        /// <summary>
        /// The attribute text of this unit's <c>&lt;un&gt;</c> element, or <c>null</c> when it has none.
        /// </summary>
        /// <remarks>
        /// Shared by every read above so the anchor is decided once rather than four times.
        ///
        /// <para><c>&lt;un\b</c>, not <c>&lt;un\s</c>: a word boundary also admits <c>&lt;un/&gt;</c> and
        /// <c>&lt;un&gt;</c>, while still refusing <c>&lt;unit …&gt;</c> — between <c>n</c> and <c>i</c>
        /// there is no boundary. All 52 nodes in <c>AllData.as</c> are self-closing today, but AS3 is E4X,
        /// where <c>&lt;un&gt;</c> and <c>&lt;un/&gt;</c> are equivalent, so the looser anchor costs
        /// nothing and cannot silently skip a row.</para>
        /// </remarks>
        private static string UnAttributes(string unitContent)
        {
            if (string.IsNullOrEmpty(unitContent)) return null;

            Match unMatch = Regex.Match(unitContent, @"<un\b([^>]*)>");
            return unMatch.Success ? unMatch.Groups[1].Value : null;
        }

        /// <summary>
        /// True when <paramref name="unAttributes"/> declares <paramref name="name"/> at all — AS3's
        /// <c>node.@x.length()</c> presence test, with the value deliberately ignored.
        /// </summary>
        /// <remarks>
        /// <c>(?:^|\s)</c> is not decoration: it is what stops <c>stay='…'</c> from also matching the tail
        /// of a hypothetical <c>xstay='…'</c>. No such attribute exists today; the anchor keeps that true.
        /// <see cref="Regex.Escape"/> is belt-and-braces — every name passed in is <c>[a-z]+</c> — but the
        /// helper is the one place a caller could hand it something else, and a mis-escaped name would
        /// silently match the wrong attribute rather than fail.
        /// </remarks>
        private static bool Declares(string unAttributes, string name)
        {
            if (unAttributes == null) return false;

            return Regex.IsMatch(unAttributes, @"(?:^|\s)" + Regex.Escape(name) + "='");
        }
    }
}
