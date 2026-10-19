using System;

namespace PFE.Systems.Loot
{
    /// <summary>
    /// The rules behind "an armed enemy drops the gun it was holding" — AS3 <c>UnitRaider.dropLoot</c>
    /// (<c>UnitRaider.as:503-539</c>) and its <c>setHero</c> override (<c>:362-372</c>).
    ///
    /// <para><b>The mechanic, in four lines of oracle.</b></para>
    /// <code>
    /// UnitRaider.as:507-523                    UnitRaider.as:362-372
    /// super.dropLoot();                       override public function setHero(param1:int = 1) : *
    /// if(currentWeapon) {                      {
    ///    currentWeapon.vis.visible = false;       super.setHero(param1);
    ///    if(isDropArm) {                          if(hero == 1) {
    ///       id = currentWeapon.id;                   if(currentWeapon &amp;&amp; currentWeapon.uniq > 0
    ///       if(variant &gt; 0) id += "^" + variant;            &amp;&amp; isrnd(currentWeapon.uniq / 2))
    ///       LootGen.lootId(loc, currentWeapon.X,        currentWeapon.updVariant(1);
    ///                       currentWeapon.Y, id, 0); }   }
    /// }                                        }
    /// </code>
    ///
    /// <para><b>Who drops.</b> <c>isDropArm</c> is a field of <c>UnitRaider</c> declared
    /// <c>= true</c> (<c>:47</c>), so the whole raider family drops its weapon unless one of three
    /// things clears it:</para>
    /// <list type="bullet">
    ///   <item><description>the placement carries a <c>nodrop</c> attribute — <b>presence</b>, not
    ///     value (<c>UnitRaider.as:206-209</c>: <c>if(param3 &amp;&amp; param3.@nodrop.length())</c>).
    ///     Two shipped slaver placements author <c>nodrop="1"</c>
    ///     (<c>RoomsSerial.as:2942,2948</c>).</description></item>
    ///   <item><description>the class is <c>UnitEncl</c> (<c>:35</c>) or <c>UnitRanger</c>
    ///     (<c>:24</c>), which set the field to <c>false</c> in their constructors.</description></item>
    ///   <item><description>the unit is not a raider at all. <c>UnitNecros</c> extends <c>Unit</c>,
    ///     not <c>UnitRaider</c>, so it inherits the base <c>dropLoot</c> and never drops a
    ///     gun.</description></item>
    /// </list>
    ///
    /// <para><b>Why the condition is rolled, not carried.</b> The last argument of
    /// <c>LootGen.lootId</c> is <c>kol = 0</c>, and <c>Item</c>'s constructor turns that into
    /// <c>sost = 0.05 + Math.random() * 0.15</c> (<c>Item.as:185-188</c>) — a dropped gun is
    /// <b>5–20 % condition</b>. That is the "random condition" a player sees on the floor, and it is
    /// reproduced by passing <see cref="DroppedWeaponKol"/> to the roller rather than by inventing a
    /// durability here.</para>
    ///
    /// <para><b>Why the hero rule lives here and not on the controller.</b> It is a two-term
    /// predicate over data (<c>uniq</c>) and one random draw, and the boundaries are the whole
    /// content: <c>uniq = -1</c> (absent <c>&lt;com&gt;</c>) can never upgrade, <c>uniq = 0</c> is
    /// refused by the <c>&gt; 0</c> test <i>before</i> the roll, and <c>uniq = 1</c> upgrades on
    /// <b>half</b> of all hero raiders — 11 of the 213 weapons carry <c>uniq='1'</c>, so that is a
    /// live 50 % on real content. None of that is visible in a play-test, which is why it is a pure
    /// function with a fixture.</para>
    /// </summary>
    public static class EnemyWeaponDropRules
    {
        /// <summary>
        /// AS3's variant separator — <c>id + "^" + variant</c> (<c>UnitRaider.as:517-519</c>,
        /// <c>UnitBossRaider.as:211-213</c>, <c>UnitBossEncl.as:183-185</c>). Read back by
        /// <c>Item</c>'s constructor, which takes the <b>last character</b> as the tier
        /// (<c>Item.as:132-136</c>).
        /// </summary>
        public const char VariantSeparator = '^';

        /// <summary>
        /// The tier <c>updVariant(1)</c> installs — <c>Weapon.updVariant</c>'s only caller in the
        /// raider path, and the only tier the shipped data defines
        /// (<see cref="PFE.Systems.Weapons.WeaponVariantXml.FirstVariant"/>).
        /// </summary>
        public const int HeroVariant = 1;

        /// <summary>
        /// The hero tier whose weapon gets the upgrade — <c>if(hero == 1)</c>
        /// (<c>UnitRaider.as:367</c>). <b>Exactly 1</b>: tiers 2, 3 and 4 are the armoured/damage
        /// tiers of <c>Unit.setHero</c> and do <i>not</i> touch the weapon.
        /// </summary>
        public const int HeroTierThatUpgradesTheWeapon = 1;

        /// <summary>
        /// The <c>kol</c> argument the drop passes to the loot generator — <c>LootGen.lootId(…, id, 0)</c>
        /// (<c>UnitRaider.as:521</c>). <b>Not "zero items"</b>: for a weapon it means "roll the worst
        /// condition band", <c>0.05..0.20</c> (<c>Item.as:185-188</c>). Passing <c>1</c> instead would
        /// silently make every dropped gun 60–85 % and passing nothing would make it pristine.
        /// </summary>
        public const int DroppedWeaponKol = 0;

        /// <summary>
        /// AS3 <c>isDropArm</c> — whether this unit drops the weapon it is holding.
        /// </summary>
        /// <param name="isRaiderFamily">
        /// The port's <c>AiFamily == UnitAiFamily.Raider</c>, i.e. the class really descends from
        /// <c>UnitRaider</c>. This is what excludes <c>UnitNecros</c>, which shares the port's
        /// controller for convenience but extends <c>Unit</c> in the oracle.
        /// </param>
        /// <param name="familyId">
        /// The unit row's family — <c>UnitDefinition.parentId</c>, falling back to the row's own id
        /// (<c>raider1</c> authors <c>parent='raider'</c>). Only <c>"encl"</c> and <c>"ranger"</c> are
        /// consulted; anything else, including <c>null</c>, is a dropping family.
        /// </param>
        /// <param name="noDropAttribute">
        /// Whether the placement carries a <c>nodrop</c> attribute <b>at all</b> — AS3's
        /// <c>@nodrop.length()</c>. The value is never read, so <c>nodrop="0"</c> also disables the
        /// drop; that is the oracle's test, not an oversight.
        /// </param>
        public static bool IsDropArm(bool isRaiderFamily, string familyId, bool noDropAttribute)
        {
            if (!isRaiderFamily) return false;          // UnitNecros and every non-raider class
            if (noDropAttribute) return false;          // UnitRaider.as:206-209
            if (IsFamily(familyId, EnclFamily)) return false;     // UnitEncl.as:35
            if (IsFamily(familyId, RangerFamily)) return false;   // UnitRanger.as:24
            return true;
        }

        /// <summary><c>UnitEncl</c>'s family key — the class that clears <c>isDropArm</c>.</summary>
        public const string EnclFamily = "encl";

        /// <summary><c>UnitRanger</c>'s family key — the other class that clears <c>isDropArm</c>.</summary>
        public const string RangerFamily = "ranger";

        private static bool IsFamily(string familyId, string expected)
            => string.Equals(familyId, expected, StringComparison.Ordinal);

        /// <summary>
        /// The id the weapon is dropped <i>as</i> — <c>currentWeapon.id</c>, with
        /// <c>"^" + variant</c> appended when the wielder's weapon is a variant
        /// (<c>UnitRaider.as:516-520</c>).
        ///
        /// <para><b>Composed here rather than inside the roller.</b> The roller's contract is that
        /// its id may carry a <c>^N</c> suffix and it will split it
        /// (<c>LootRoller.BuildItem</c>, <c>Item.as:132-136</c>); deciding <i>whether</i> to append
        /// one is the dropper's rule, and it is the same rule for a boss
        /// (<c>UnitBossRaider.as:210-214</c>).</para>
        /// </summary>
        public static string DropId(string weaponId, int variant)
        {
            if (string.IsNullOrEmpty(weaponId)) return weaponId;
            return variant > 0
                ? weaponId + VariantSeparator + variant.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : weaponId;
        }

        /// <summary>
        /// AS3 <c>currentWeapon.uniq &gt; 0 &amp;&amp; isrnd(currentWeapon.uniq / 2)</c>
        /// (<c>UnitRaider.as:367</c>) — whether a hero raider's weapon is its "II" variant.
        ///
        /// <para><b>Three separate facts, and each one is a different bug if lost.</b></para>
        /// <list type="number">
        ///   <item><description><c>uniq</c> is the <c>&lt;com uniq&gt;</c> drop weight, and its
        ///     <b>absent</b> value is <c>-1</c> (<c>Weapon.as:100</c>, assigned only on presence at
        ///     <c>:464-467</c>). 142 of the 213 weapons ship as <c>-1</c> and can never be
        ///     upgraded.</description></item>
        ///   <item><description>the oracle tests <c>&gt; 0</c> here, so <c>uniq = 0</c> — the 45
        ///     weapons that author it — never upgrades, even though
        ///     <c>Weapon.updVariant</c>'s own guard is only <c>&lt; 0</c>. The two tests are not
        ///     interchangeable.</description></item>
        ///   <item><description><c>isrnd(x)</c> is <c>Math.random() &lt; x</c>
        ///     (<c>Unit.as:4855-4858</c>), so the chance is <b>uniq / 2</b> — 50 % at
        ///     <c>uniq='1'</c>, 25 % at <c>'0.5'</c>, 2.5 % at <c>'0.05'</c>.</description></item>
        /// </list>
        /// </summary>
        /// <param name="uniq">AS3 <c>Weapon.uniq</c> — the weapon row's <c>&lt;com uniq&gt;</c>.</param>
        /// <param name="rnd">One uniform draw in <c>[0,1)</c>.</param>
        public static bool ShouldUpgradeToHeroVariant(float uniq, float rnd)
            => uniq > 0f && rnd < uniq / 2f;

        /// <summary>
        /// AS3 <c>Invent.take</c>'s upgrade branch (<c>Invent.as:1076-1085</c>): an incoming weapon
        /// upgrades the copy already held <b>only when its tier is strictly higher</b>, and
        /// <c>Weapon.updVariant</c> then refuses outright for a weapon with no unique variant
        /// (<c>if(this.uniq &lt; 0) return</c>, <c>Weapon.as:895-900</c>).
        ///
        /// <para><b><c>uniq &gt;= 0</c>, not <c>&gt; 0</c>.</b> This is the <i>other</i> guard — the
        /// one inside <c>updVariant</c> — and it deliberately differs from
        /// <see cref="ShouldUpgradeToHeroVariant"/>'s <c>&gt; 0</c>. A <c>uniq='0'</c> weapon has a
        /// tier 1 that never drops as random loot, so picking one up by another route must still
        /// upgrade the copy in the bag; a weapon with no <c>&lt;com&gt;</c> at all must not, because
        /// it has no tier 1 to upgrade to.</para>
        /// </summary>
        /// <param name="ownedVariant">AS3 <c>weapons[id].variant</c>.</param>
        /// <param name="incomingVariant">AS3 <c>l.variant</c> on the item being taken.</param>
        /// <param name="uniq">AS3 <c>Weapon.uniq</c> of the weapon row.</param>
        public static bool ShouldUpgradeOwnedWeapon(int ownedVariant, int incomingVariant, float uniq)
            => incomingVariant > ownedVariant && uniq >= 0f;
    }
}
