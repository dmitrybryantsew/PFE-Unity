using System.Collections.Generic;

namespace PFE.Systems.Loot
{
    /// <summary>
    /// Which AllData collection a row came from. <c>LootGen.init()</c> reads exactly two —
    /// <c>AllData.d.weapon</c> and <c>AllData.d.item</c> (<c>LootGen.as:41-113</c>). Armour is
    /// <b>not</b> in any pool: there is no <c>arr["armor"]</c>.
    /// </summary>
    public enum LootPoolRowKind
    {
        Item,
        Weapon,
    }

    /// <summary>
    /// One raw AllData row, flattened to exactly the attributes <c>LootGen.init()</c> reads.
    ///
    /// <para><b>Why every optional field is nullable.</b> <c>init()</c> stores the E4X attribute
    /// <i>value</i> (an <c>XMLList</c>) into the pool entry, and <c>getRandom</c> then tests
    /// <c>entry.st == null || entry.st &lt;= gameStage</c> (<c>LootGen.as:138</c>). An <b>absent</b>
    /// attribute compares equal to <c>null</c>, so it <i>passes</i> the gate — a third state that
    /// <c>0</c> cannot represent. Modelling an absent <c>@stage</c> as <c>0</c> would drop 253 of the
    /// 500 item rows out of every stage-gated roll.</para>
    ///
    /// <para><b>The present-but-empty case is measured, not assumed.</b> A probe over
    /// <c>AllData.as</c> (500 <c>&lt;item&gt;</c>, 213 <c>&lt;weapon&gt;</c>) found <b>zero</b>
    /// present-but-empty values for <c>item.tip</c>, <c>item.tip2</c>, <c>item.stage</c>,
    /// <c>item.chance</c>, <c>item.chance2</c>, <c>com.stage</c>, <c>com.chance</c>, <c>com.worth</c>
    /// and <c>com.uniq</c>. So AS3's <c>@attr.length()</c> — which for an <i>attribute</i> in E4X is
    /// the XMLList count (0 or 1) and therefore <b>truthy for <c>attr=""</c></b> — and the port's
    /// string-length reading give <b>identical</b> results on the shipped data. The port uses the
    /// string-length reading (see <c>ContainerRule.HasValue</c>) and this probe is why that is safe.</para>
    ///
    /// <para>One value does distinguish the two readings and was checked: <c>com.uniq='0'</c> appears on
    /// 45 weapons. Both readings treat it as <i>present</i> (count 1; string length 1), so those 45 land
    /// in <c>arr["uniq"]</c> with <c>chance = 0</c> — inert unless they are the sole entry, where
    /// <c>getRandom</c>'s <c>length == 1</c> short-circuit returns them without a roll.</para>
    /// </summary>
    public sealed class LootPoolRow
    {
        /// <summary>AS3 <c>@id</c>. Required.</summary>
        public string Id;

        /// <summary><c>AllData.d.item</c> or <c>AllData.d.weapon</c>.</summary>
        public LootPoolRowKind Kind = LootPoolRowKind.Item;

        /// <summary>
        /// AS3 <c>&lt;item @tip&gt;</c> — the pool key (<c>LootGen.as:81-92</c>). Empty means the row
        /// is in no pool by tip (and, for a weapon, that it is filtered out).
        /// </summary>
        public string Tip;

        /// <summary>
        /// AS3 <c>&lt;item @tip2&gt;</c> — a <i>second</i> pool the same row also joins
        /// (<c>LootGen.as:98-112</c>). 58 rows carry one: <c>eda</c> ×37, <c>co</c> ×21.
        /// </summary>
        public string Tip2;

        /// <summary>AS3 <c>@stage</c> (items) / <c>com.@stage</c> (weapons). <c>null</c> = absent = passes.</summary>
        public int? Stage;

        /// <summary>AS3 <c>@chance</c> (items) / <c>com.@chance</c> (weapons). <c>null</c> = absent.</summary>
        public float? Chance;

        /// <summary>AS3 <c>@chance2</c> — the tip2 pool's own weight (<c>LootGen.as:108</c>).</summary>
        public float? Chance2;

        /// <summary>AS3 <c>com.@worth</c> — weapons only (<c>LootGen.as:49</c>). Exact-match filter.</summary>
        public float? Worth;

        /// <summary>AS3 <c>@lvl</c> — the level gate (<c>LootGen.as:138</c>). <c>null</c> = absent = passes.</summary>
        public int? Level;

        /// <summary>
        /// AS3 <c>com.@uniq</c> — the weight of the <c>id + "^1"</c> entry pushed into
        /// <c>arr["uniq"]</c> (<c>LootGen.as:53-63</c>). <c>null</c> = the attribute is absent, so no
        /// uniq entry is created at all. Note <c>0</c> and <c>null</c> are <b>different</b> here:
        /// <c>uniq='0'</c> still creates an entry (weight 0), absent does not.
        /// </summary>
        public float? UniqChance;

        /// <summary>AS3 <c>weap.com.length() != 0</c> — whether the weapon has a <c>&lt;com&gt;</c>
        /// child. <b>This is an element count, not an attribute</b>, so it is unambiguous. A weapon
        /// without one is in no pool at all.</summary>
        public bool HasCom;

        /// <summary>AS3 <c>item.sk.length() != 0</c> — pushes the row into <c>arr["pers"]</c>
        /// (<c>LootGen.as:93-96</c>). Element count; unambiguous.</summary>
        public bool HasSkill;

        /// <summary>AS3 <c>&lt;weapon @tip&gt;</c> as an int. <c>init()</c> keeps tips <c>1..3</c> for
        /// <c>arr["weapon"]</c> and tip <c>5</c> for <c>arr["magic"]</c>.</summary>
        public int WeaponTip;
    }
}
