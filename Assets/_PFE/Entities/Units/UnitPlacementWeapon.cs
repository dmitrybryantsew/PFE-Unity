using System;

namespace PFE.Entities.Units
{
    /// <summary>
    /// What a placement's <c>weap</c> attribute does to a unit's weapon, before any roll happens.
    ///
    /// <para><b>Three outcomes, not two, and the third is the one that is easy to lose.</b> AS3's
    /// raider-family constructor (<c>UnitRaider.as:162-181</c>) is:</para>
    /// <code>
    /// if(Boolean(param4) &amp;&amp; Boolean(param4.weap)) {          // a runtime override object
    ///    if(param4.weap != "") currentWeapon = Weapon.create(this,param4.weap);
    /// }
    /// else if(Boolean(param3) &amp;&amp; Boolean(param3.@weap.length())) {   // the placement attribute
    ///    if(param3.@weap != "") currentWeapon = Weapon.create(this,param3.@weap);
    /// }
    /// else {
    ///    currentWeapon = getXmlWeapon(param2);              // roll the unit row's &lt;w&gt; candidates
    /// }
    /// </code>
    /// <para>Read carefully, that is three distinct outcomes, and the <c>weap=""</c> case is the one a
    /// port gets wrong by reflex. <c>param3.@weap</c> is an <c>XMLList</c>, so <c>.length()</c> is the
    /// number of matching <b>attributes</b> — <c>1</c> for <c>&lt;obj weap=""/&gt;</c> and <c>0</c> only
    /// when the attribute is absent. So an empty value still takes the middle arm, and then the inner
    /// <c>!= ""</c> test fails and <c>currentWeapon</c> is left <c>null</c>: the unit is
    /// <b>explicitly unarmed</b>, and the roll is never reached. That inner test is therefore reachable
    /// and load-bearing — it exists precisely because <c>weap=""</c> is a meaningful authoring.</para>
    ///
    /// <para><b>The shipped data exercises all three.</b> Nineteen placements in <c>fe/rooms/</c> author
    /// <c>weap</c>: seventeen name a weapon (<c>p9mm</c>, <c>oldr</c>, <c>oldshot</c>, <c>flamer</c>,
    /// <c>hunt</c>, <c>minigun</c>, <c>incin</c>, <c>glau</c>, <c>lmg</c>, <c>lmg^1</c>, <c>antidrak</c>,
    /// <c>wquest1</c>, <c>smg10</c>, <c>anti</c>, <c>hmg</c>, <c>zebr^1</c>) and two are
    /// <c>&lt;obj id="zebra" … weap=""/&gt;</c> (<c>RoomsMbase.as:4246</c>, <c>RoomsSerial.as:6253</c>).
    /// A port that read <c>weap</c> as "non-empty means fixed, otherwise roll" would arm both zebras
    /// from their unit row — the opposite of what the room says.</para>
    ///
    /// <para><b>Only the raider family reads it.</b> <c>UnitRaider.as:170</c> is the single reader of a
    /// placement's <c>weap</c> in the whole tree. <c>UnitAlicorn</c> and <c>UnitAIRobot</c> read
    /// <c>aiTip</c> but go straight to <c>getXmlWeapon</c>; <c>UnitNecros</c> extends <c>Unit</c>, not
    /// <c>UnitRaider</c>, and reads neither. The port gates on
    /// <see cref="UnitPlacementAi.UnitAiFamily.Raider"/> for the same reason it gates the <c>ai</c>
    /// mapping.</para>
    /// </summary>
    public enum PlacementWeaponMode
    {
        /// <summary>
        /// No <c>weap</c> attribute was authored — AS3's <c>else</c> arm, <c>getXmlWeapon(param2)</c>.
        /// The unit row's <c>&lt;w&gt;</c> candidates are rolled.
        /// </summary>
        Roll,

        /// <summary>
        /// <c>weap=""</c> — AS3's middle arm with a failing inner test. The unit carries no weapon at
        /// all, and <b>the roll does not happen</b>.
        /// </summary>
        Unarmed,

        /// <summary>
        /// <c>weap="lmg"</c> — AS3's <c>Weapon.create(this, param3.@weap)</c>. The named weapon is used
        /// regardless of what the unit row offers.
        /// </summary>
        Fixed,
    }

    /// <summary>
    /// The placement <c>weap</c> attribute, as a pure function of (presence, value).
    ///
    /// <para><b>Why the presence test is a separate parameter.</b> <c>GetAttribute(key, "")</c> answers
    /// <c>""</c> for both an absent attribute and an authored empty one, so a value-only signature
    /// cannot distinguish <see cref="PlacementWeaponMode.Roll"/> from
    /// <see cref="PlacementWeaponMode.Unarmed"/> — the exact ambiguity the oracle's
    /// <c>XMLList.length()</c> resolves. Callers pass
    /// <c>MapObjectDataUtility.HasAttribute(attributes, "weap")</c> alongside the value; the two
    /// questions are kept apart here so nobody re-derives one from the other.</para>
    /// </summary>
    public static class UnitPlacementWeapon
    {
        /// <summary>The AS3 attribute name, and the only placement attribute this class reads.</summary>
        public const string AttributeName = "weap";

        /// <summary>
        /// AS3's variant separator — <c>Weapon.create</c> strips a trailing <c>^n</c>
        /// (<c>Weapon.as:352-356</c>) before the id lookup, and re-appends <c>^variant</c> to
        /// <c>this.weap</c> afterwards (<c>UnitRaider.as:184-187</c>).
        /// </summary>
        public const char VariantSeparator = '^';

        /// <summary>
        /// The mode for a placement whose <c>weap</c> attribute <paramref name="isPresent"/> with value
        /// <paramref name="value"/>.
        /// </summary>
        /// <param name="isPresent">
        /// Whether the <c>&lt;obj&gt;</c> node carries <c>weap</c> at all — AS3's
        /// <c>param3.@weap.length()</c>, which counts attributes, not characters.
        /// </param>
        /// <param name="value">
        /// The attribute's text. Only consulted when <paramref name="isPresent"/> is <c>true</c>;
        /// <c>null</c> and <c>""</c> both mean <see cref="PlacementWeaponMode.Unarmed"/> there.
        /// </param>
        public static PlacementWeaponMode Resolve(bool isPresent, string value)
        {
            if (!isPresent)
            {
                // `param3.@weap.length()` is 0 — the else-if is skipped and the roll runs.
                return PlacementWeaponMode.Roll;
            }

            // `if(param3.@weap != "")`. Reached for `weap=""`, which is why this arm exists.
            return string.IsNullOrEmpty(value)
                ? PlacementWeaponMode.Unarmed
                : PlacementWeaponMode.Fixed;
        }

        /// <summary>
        /// AS3 <c>Weapon.create</c>'s variant strip (<c>Weapon.as:352-356</c>):
        /// <c>if(id.charAt(id.length - 2) == "^") id = id.substr(0, id.length - 2);</c>
        ///
        /// <para><b>The off-by-one is the oracle's and is reproduced.</b> The test looks at the
        /// second-to-last character, so it only strips a <b>single-digit</b> variant.
        /// <c>"lmg^1"</c> → <c>"lmg"</c>; <c>"lmg^12"</c> is left alone, because the character two from
        /// the end is <c>1</c> and not <c>^</c>. No shipped placement uses a two-digit variant, so the
        /// quirk is unobservable today — but "fixing" it here would silently change the id a lookup
        /// uses, which is the kind of divergence that hides.</para>
        /// </summary>
        /// <returns>The base weapon id, or <paramref name="rawWeaponId"/> unchanged.</returns>
        public static string BaseWeaponId(string rawWeaponId)
        {
            if (string.IsNullOrEmpty(rawWeaponId) || rawWeaponId.Length < 2)
            {
                return rawWeaponId;
            }

            return rawWeaponId[rawWeaponId.Length - 2] == VariantSeparator
                ? rawWeaponId.Substring(0, rawWeaponId.Length - 2)
                : rawWeaponId;
        }

        /// <summary>
        /// True when <paramref name="rawWeaponId"/> carries the single-digit variant suffix
        /// <see cref="BaseWeaponId"/> would strip.
        /// </summary>
        public static bool IsVariantSuffixed(string rawWeaponId) =>
            !string.IsNullOrEmpty(rawWeaponId)
            && rawWeaponId.Length >= 2
            && rawWeaponId[rawWeaponId.Length - 2] == VariantSeparator;

        /// <summary>
        /// The tier <c>Weapon.create</c> gives a <c>weap='lmg^1'</c> placement.
        ///
        /// <para><b>Always 1, and the digit is <i>not</i> read.</b> <c>Weapon.create</c>'s strip
        /// (<c>Weapon.as:352-357</c>) is
        /// <c>if(id.charAt(id.length - 2) == "^") { id = id.substr(0, id.length - 2); nvar = 1; }</c> —
        /// the literal <c>1</c>, not <c>int(last char)</c>. So a placement authoring <c>weap='lmg^2'</c>
        /// would still get tier 1, while the same string handed to <c>Item</c>'s constructor
        /// (<c>Item.as:132-136</c>) reads the digit and yields tier 2. The two parsers genuinely
        /// disagree, and this is the <c>Weapon.create</c> side — the side a unit's loadout goes
        /// through.</para>
        ///
        /// <para><b>Why it matters at all.</b> Two shipped placements name a variant
        /// (<c>weap='lmg^1'</c>, <c>weap='zebr^1'</c>), and a unit that holds tier 1 drops
        /// <c>id^1</c> (<c>UnitRaider.as:516-520</c>). Discarding the tier at the lookup — which is what
        /// the port did until now — makes those two raiders drop a plain gun.</para>
        /// </summary>
        public static int VariantOf(string rawWeaponId) => IsVariantSuffixed(rawWeaponId) ? 1 : 0;
    }
}
