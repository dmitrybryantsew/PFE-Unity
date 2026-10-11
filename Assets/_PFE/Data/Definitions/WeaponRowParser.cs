using System.Text.RegularExpressions;

namespace PFE.Data.Definitions
{
    /// <summary>
    /// Reads the attributes of one unit weapon row — AS3's
    /// <c>&lt;w id='…' ch='…' dif='…' f='…'/&gt;</c> inside a <c>&lt;unit&gt;</c> element of
    /// <c>AllData.as</c>.
    ///
    /// <para>Exists for the same reason <see cref="UnitExtrasParser"/> does: the one thing this parse has
    /// to get right is a <em>guard</em>, and a wrong guard is wrong <b>silently</b>. The trap here is
    /// concrete — <c>f='</c> is a <b>substring of <c>dif='</c></b>, so an unanchored search for
    /// <c>f='</c> matches every difficulty-gated row in the game: <c>dif='18'</c> reads as "carries
    /// <c>f</c>", the 23 fixed rows become 23 + every <c>dif</c> row, and the selector is left with
    /// almost nothing to roll. The failure is invisible — the game simply hands out the wrong weapons.</para>
    ///
    /// <para><b>What <c>f</c> means, and why it is a skip rather than a grant.</b>
    /// <c>Unit.getXmlWeapon</c> (<c>Unit.as:1450-1475</c>) walks the row list and its very first test is
    /// <c>if(!n.@f.length())</c> — a row carrying <c>f</c> is <b>never selected by the roll</b>. Those rows
    /// are the unit's <b>secondary</b> weapon, and each family's constructor grants it by name instead of
    /// by roll: <c>UnitMerc</c> and <c>UnitEncl</c> build <c>mercgr</c> as <c>thWeapon</c>
    /// (<c>UnitMerc.as:32</c>, <c>UnitEncl.as:29</c>), <c>UnitRanger</c> builds <c>robomlau</c> and
    /// <c>robogas</c> as <c>dopWeapon1</c>/<c>dopWeapon2</c> (<c>UnitRanger.as:28-29</c>),
    /// <c>UnitSentinel</c> builds <c>robomlau</c> (<c>UnitSentinel.as:27</c>), <c>UnitGutsy</c> builds
    /// <c>robofire</c> (<c>UnitGutsy.as:28</c>), and the four bosses that author only <c>f</c> rows
    /// hard-code their whole loadout (<c>UnitBossRaider.as:73-78</c>, <c>UnitBossUltra.as:91-96</c>,
    /// <c>UnitBossDron.as:61-62</c>, <c>UnitBossAlicorn.as:128</c>).</para>
    ///
    /// <para>Dropping the flag — which is what the importer did — makes the selector treat a fixed row as
    /// an ordinary candidate, and because a fixed row authors no <c>ch</c> it defaults to certainty and
    /// wins the loop on the first iteration. Every <c>merc1</c> then carries the 50-damage <c>mercgr</c>
    /// rocket as its <i>primary</i> and its four real candidates (<c>psc</c>, <c>p127mm</c>, <c>p308c</c>,
    /// <c>revo</c>) are unreachable; every <c>ranger1</c> carries the 150-damage <c>robomlau</c>.</para>
    ///
    /// <para><b>Presence, not value.</b> The oracle tests <c>n.@f.length()</c>, so <c>f='0'</c> is skipped
    /// exactly like <c>f='1'</c>. All 23 rows in <c>AllData.as</c> author <c>'1'</c>, so the two readings
    /// agree on today's data — but matching the oracle costs nothing and cannot go stale. This is the same
    /// distinction <see cref="UnitExtrasParser.ParseCanResurrect"/> records for <c>res</c>.</para>
    /// </summary>
    public static class WeaponRowParser
    {
        /// <summary>
        /// True when the row carries an <c>f</c> attribute — AS3 <c>n.@f.length() != 0</c>, the guard that
        /// excludes the row from <c>Unit.getXmlWeapon</c>'s roll.
        /// </summary>
        /// <param name="weaponAttributes">
        /// The row's attribute text: everything after the id (which is what
        /// <c>UnitDataImporter.ParseWeapons</c> captures as its second group), or the whole
        /// <c>&lt;w …/&gt;</c> tag. Both work, because the anchor accepts a leading space.
        /// </param>
        public static bool IsFixed(string weaponAttributes)
        {
            if (string.IsNullOrEmpty(weaponAttributes)) return false;

            // `(?:^|\s)` is load-bearing, not decoration: it is what stops `f='` from also matching the
            // tail of `dif='`. Every one of the 23 fixed rows is written ` f='1'` (space-separated), and
            // every `dif` row is written ` dif='N'`, so the anchor separates them exactly. This mirrors
            // the `(?:^|\s)res='` anchor in UnitExtrasParser.
            return Regex.IsMatch(weaponAttributes, @"(?:^|\s)f='");
        }
    }
}
