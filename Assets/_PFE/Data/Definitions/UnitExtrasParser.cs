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
    /// <para><b>Eight attributes are read here; the node carries twenty-two.</b> The remaining fourteen
    /// (<c>ss</c> 9, <c>skill</c> 7, <c>attr</c> 4, <c>attch</c> 4, <c>tip</c> 3, <c>enclweap</c> 3,
    /// <c>chootch</c> 3, <c>robot</c> 1, <c>plate</c> 1, <c>one</c> 1, <c>gryz</c> 1, <c>gren</c> 1,
    /// <c>glow</c> 1, <c>drop</c> 1, <c>bulb</c> 1 — the counts are over the 52 <c>&lt;un&gt;</c> nodes
    /// in <c>AllData.as</c>) are deliberately left unwritten: closing the node is its own workstream, and
    /// giving an attribute a writer before anything reads it is the trap
    /// <see cref="UnitDefinition.canResurrect"/> was already in.</para>
    ///
    /// <para><b>Why these eight are different.</b> <c>res</c>, <c>walker</c>, <c>sniper</c>, <c>stay</c>,
    /// <c>och</c>, <c>dist</c>, <c>stalk</c> and <c>grenader</c> are each read by a ported code path, so
    /// an unread attribute here is a live divergence rather than a dormant one: <c>zebra3</c> alone
    /// authors <c>walker='1' sniper='1' stay='1'</c> (<c>AllData.as:361</c>) and the port dropped all
    /// three. <c>och</c>/<c>dist</c>/<c>stalk</c>/<c>grenader</c> were added on 2026-10-19 with their
    /// readers — <c>ArmedShooterBrain</c>'s burst cadence and commit range, the flyer's hover radius, and
    /// the mercenary/enclave secondary weapon.</para>
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
        /// The unit's <c>&lt;un och='…'&gt;</c> — AS3 <c>UnitRaider.aiAttackOch</c>
        /// (<c>UnitRaider.as:319-321</c>) and <c>UnitAIRobot.aiAttackOch</c> (declared <c>100</c>,
        /// <c>UnitAIRobot.as:31</c>), or <c>null</c> when the row does not author it.
        ///
        /// <para><b>Null is not zero, and that distinction is the whole rule.</b> AS3 assigns only
        /// inside <c>if(node0.un.@och.length())</c>, so an absent attribute leaves the class default in
        /// place — and for the raider that default is <c>0</c>, which is not "fire as fast as possible"
        /// but a <i>different branch</i>: <c>aiAttackOch == 0</c> is the 10 %-per-frame trigger, and
        /// <c>UnitRaider</c>'s constructor then overwrites it from the rolled weapon's magazine
        /// (<c>:217-219</c>, <c>holder &gt; 10</c>). Collapsing "absent" into <c>0</c> would be correct
        /// for the raider by luck and wrong for the robot, whose default is <c>100</c>.</para>
        ///
        /// <para><b>11 rows author it</b>, and the shipped values are far from the port's old constant
        /// of 30: <c>raider1</c>/<c>raider5</c> 60, <c>raider3</c> 120, <c>merc2</c> 3, <c>merc3</c> 45,
        /// <c>slaver4</c> 25, the three <c>zebra</c> snipers 16.</para>
        /// </summary>
        public static int? ParseAttackOch(string unitContent) => ReadInt(UnAttributes(unitContent), "och");

        /// <summary>
        /// The unit's <c>&lt;un dist='…'&gt;</c> — AS3 <c>UnitRaider.aiDist</c>
        /// (<c>UnitRaider.as:323-326</c>), the range at which the ranged arm commits
        /// (<c>:756</c>). Absent leaves the class default <c>1000</c>.
        ///
        /// <para><b>Not the same quantity as <see cref="UnitDefinition.detectionDistance"/>.</b> That
        /// one comes from <c>&lt;vis observ&gt;</c> (<c>Unit.as</c>'s observation power) and the port
        /// used it as a stand-in for <c>aiDist</c> — 400 for every unit in the game, because
        /// <c>UnitDataImporter</c> writes 400 as the default and <c>observ</c> writes a different field
        /// entirely. 8 rows author <c>dist</c>: 500, 600 and 700.</para>
        /// </summary>
        public static int? ParseAiDistance(string unitContent) => ReadInt(UnAttributes(unitContent), "dist");

        /// <summary>
        /// The unit's <c>&lt;un stalk='…'&gt;</c> — AS3 <c>UnitRaider.stalkDist</c>
        /// (<c>UnitRaider.as:327-330</c>), the radius inside which a <b>flying</b> unit stops
        /// approaching and hovers (<c>:1291-1299</c>). Absent leaves the class default <c>500</c>;
        /// <c>UnitRaider</c>'s constructor forces it to <c>0</c> for a contact attacker
        /// (<c>:275-278</c>), which is a separate rule and belongs to the brain.
        ///
        /// <para>6 rows author it: 500 (<c>merc1</c>, <c>merc2</c>, <c>slaver4</c>), 1000
        /// (<c>merc3</c>, <c>encl1</c>), 1500 (<c>merc4</c>).</para>
        /// </summary>
        public static int? ParseStalkDistance(string unitContent) => ReadInt(UnAttributes(unitContent), "stalk");

        /// <summary>
        /// The unit's <c>&lt;un grenader='…'&gt;</c> — AS3 <c>UnitRaider.grenader</c>
        /// (<c>UnitRaider.as:343-346</c>), the number of secondary-weapon rounds a mercenary or
        /// enclave soldier carries.
        ///
        /// <para><b>It gates the secondary weapon, it does not merely size it.</b>
        /// <c>UnitMerc.as:30</c> and <c>UnitEncl.as:29</c> both wrap the whole
        /// <c>Weapon.create(this,"mercgr")</c> in <c>if(grenader &gt; 0)</c>, so <c>merc1</c>,
        /// <c>merc2</c> and <c>merc4</c> — which author no <c>grenader</c> — have <b>no</b> rocket in the
        /// oracle, even though their rows carry the <c>f='1'</c> <c>mercgr</c> candidate. 4 rows author
        /// it: <c>merc3</c> 4, <c>merc5</c> 2, <c>encl2</c> 2, <c>encl3</c> 4.</para>
        ///
        /// <para><b>The anchor matters here.</b> <c>&lt;un gren='1'/&gt;</c> also exists
        /// (<c>raider9</c>) and is a different attribute with no reader; <c>(?:^|\s)gren=</c> does not
        /// match inside <c>grenader=</c> and vice versa.</para>
        /// </summary>
        public static int? ParseGrenadeCount(string unitContent) => ReadInt(UnAttributes(unitContent), "grenader");

        /// <summary>
        /// The unit's <c>&lt;un ss='…'&gt;</c> — AS3 <c>UnitZombie.superSilaTip</c>
        /// (<c>UnitZombie.as:151-156</c>), which <c>setSuper()</c> turns into the super-attack ladder
        /// (<c>:954-1037</c>).
        ///
        /// <para><b>Nine rows author it and every one is a zombie variant</b> — <c>zombie1..zombie9</c>
        /// (<c>AllData.as:793, 803, 813, 828, 838, 849, 865, 881, 896</c>). <c>zombie0</c>, the plain
        /// ghoul, authors none and has no super at all.</para>
        ///
        /// <para><b>Ownership guard, unlike <c>walker</c>.</b> <c>ss</c> is read by exactly one class in
        /// the oracle, so there is no second meaning to defend against — but the value is a
        /// <i>dispatch index</i>, and writing it onto a unit whose class never reads it would leave a
        /// number that looks meaningful and is inert. The same family gate as
        /// <see cref="ParseCanResurrect"/>, for the same reason: the zombie controller is the only
        /// reader.</para>
        ///
        /// <para><b>The value is kept, not just its presence</b> — unlike <c>res</c>. Each index selects
        /// a different effect, so collapsing them to a boolean would make all nine variants identical.</para>
        /// </summary>
        /// <param name="unitContent">The unit's own <c>&lt;unit&gt;…&lt;/unit&gt;</c> text.</param>
        /// <param name="parentId">The unit's <c>parent=</c>; must be <c>zombie</c>.</param>
        public static int? ParseSuperSkill(string unitContent, string parentId)
        {
            if (!string.Equals(parentId, ZombieFamilyId, StringComparison.Ordinal)) return null;

            return ReadInt(UnAttributes(unitContent), "ss");
        }

        /// <summary>
        /// True when the unit's <c>&lt;un&gt;</c> element declares <c>glow</c> — AS3
        /// <c>UnitZombie.glowTip</c> (<c>UnitZombie.as:157-160</c>), the radiating light that
        /// <c>visZombieLight</c> draws and whose strength <c>animate()</c> fades
        /// (<c>:110-117</c>, <c>:251-254</c>).
        ///
        /// <para><b>Exactly one row authors it — <c>zombie4</c></b>, the "Светящийся гуль", and it is
        /// also the radiation variant. No ownership guard: <c>glow</c> has one owner in the oracle and no
        /// second meaning anywhere.</para>
        ///
        /// <para><b>Presence, not value.</b> AS3 stores the number (<c>glowTip</c>) but only ever tests
        /// <c>&gt; 0</c>; the one shipped author writes <c>'1'</c>.</para>
        /// </summary>
        public static bool ParseGlows(string unitContent) => Declares(UnAttributes(unitContent), "glow");

        /// <summary>
        /// The integer value of <paramref name="name"/> on the <c>&lt;un&gt;</c> attribute run, or
        /// <c>null</c> when the attribute is absent.
        /// </summary>
        /// <remarks>
        /// <para><b>The leading anchor is the trap this guards.</b> An unanchored
        /// <c>och='([0-9]+)'</c> would also match the tail of any future <c>boch=</c>; more immediately,
        /// <c>dist='…'</c> unanchored would match nothing today but <c>stalk='…'</c> unanchored is a
        /// prefix-match hazard in the other direction. <see cref="Declares"/> already anchors this way
        /// for the four presence reads, and the two helpers must agree or one of them is wrong.</para>
        /// <para>AS3's values are all non-negative, but the pattern admits a sign so a malformed row
        /// fails loudly at parse time rather than being silently clamped.</para>
        /// </remarks>
        private static int? ReadInt(string unAttributes, string name)
        {
            if (unAttributes == null) return null;

            Match match = Regex.Match(unAttributes, @"(?:^|\s)" + Regex.Escape(name) + "='(-?[0-9]+)'");
            if (!match.Success) return null;

            return int.TryParse(match.Groups[1].Value, out int value) ? value : (int?)null;
        }

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
