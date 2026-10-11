using System;
using System.Collections.Generic;

namespace PFE.Systems.Weapons
{
    /// <summary>
    /// One <c>&lt;w id='…' ch='…' dif='…' f='…'/&gt;</c> candidate on a unit's row in <c>AllData.as</c>.
    ///
    /// <para><b>Why this is not <c>WeaponChance</c>.</b> <c>WeaponChance</c> lives in
    /// <c>PFE.Data.Definitions</c>, and this selector lives in <c>PFE.Sim</c>, which the data
    /// assembly references (not the other way round). A neutral struct keeps the rule in the assembly
    /// that can be exercised without a ScriptableObject, and keeps the importer's type out of the
    /// runtime rule.</para>
    ///
    /// <para><b><c>Chance</c> is 1 for an absent <c>ch</c>, and that is faithful rather than
    /// lossy.</b> AS3 tests <c>n.@ch.length() == 0 || this.isrnd(n.@ch)</c> — an absent <c>ch</c>
    /// takes the weapon unconditionally, and <c>isrnd(1)</c> is <c>Math.random() &lt; 1</c>, which is
    /// also always true. The two spellings cannot be told apart by their behaviour, so the importer's
    /// collapse into <c>chance = 1</c> loses nothing. (They do differ in <i>one</i> respect — AS3's
    /// <c>isrnd</c> consumes a <c>Math.random()</c> and the absent-attribute branch short-circuits
    /// without one — but that is Flash's unseeded global, which the port deliberately does not mirror;
    /// see the <c>roll01</c> remarks on <see cref="SelectIndex"/>.)</para>
    /// </summary>
    public readonly struct WeaponOption
    {
        /// <summary>The AS3 weapon id — <c>cknife</c>, <c>smg10</c>, <c>mercgr</c>.</summary>
        public readonly string WeaponId;

        /// <summary>AS3 <c>ch</c> — the probability this candidate is taken, once it passes the gate.</summary>
        public readonly float Chance;

        /// <summary>AS3 <c>dif</c> — the minimum location difficulty that unlocks this candidate. 0 = always.</summary>
        public readonly int Difficulty;

        /// <summary>
        /// AS3 <c>f</c> — the row is the unit's <b>secondary</b> weapon and is excluded from the roll
        /// entirely. See <see cref="SelectIndex"/> for the guard and
        /// <c>PFE.Data.Definitions.WeaponRowParser</c> for what the flag means and why dropping it
        /// matters.
        /// </summary>
        public readonly bool IsFixedWeapon;

        public WeaponOption(string weaponId, float chance, int difficulty, bool isFixedWeapon = false)
        {
            WeaponId  = weaponId;
            Chance    = chance;
            Difficulty = difficulty;
            IsFixedWeapon = isFixedWeapon;
        }
    }

    /// <summary>
    /// The port of AS3 <c>Unit.getXmlWeapon()</c> (<c>Unit.as:1450-1475</c>) and the
    /// <c>attackerType</c> classification that follows it (<c>UnitRaider.as:257-272</c>).
    ///
    /// <para><b>The gap this closes.</b> <c>UnitDefinition.weapons</c> — the imported
    /// <c>&lt;w&gt;</c> rows — had no runtime reader at all: its only consumers were
    /// <c>GetReferencedDataIds</c> and <c>OnValidateData</c>. So every armed enemy in the port fought
    /// with a hard-coded weapon or with fabricated damage, while the oracle rolls a weapon from this
    /// very list at construction and derives its whole attack branch from what it rolled.</para>
    /// </summary>
    public static class UnitWeaponSelector
    {
        /// <summary>No candidate was eligible — AS3 <c>getXmlWeapon</c> returning <c>null</c>.</summary>
        public const int NoWeapon = -1;

        /// <summary>AS3 <c>attackerType</c> 0 — contact attack, no weapon swing.</summary>
        public const int AttackerTypeContact = 0;

        /// <summary>AS3 <c>attackerType</c> 1 — point-blank swing of a <c>tip == 1</c> weapon.</summary>
        public const int AttackerTypeMeleeWeapon = 1;

        /// <summary>AS3 <c>attackerType</c> 2 — the ranged branch.</summary>
        public const int AttackerTypeRanged = 2;

        /// <summary>AS3 <c>attackerType</c> 3 — the thrown branch.</summary>
        public const int AttackerTypeThrown = 3;

        /// <summary>
        /// Pick the first eligible candidate, exactly as <c>Unit.getXmlWeapon</c> walks
        /// <c>node0.w</c>.
        ///
        /// <para>The oracle, verbatim:</para>
        /// <code>
        /// for each(n in node0.w) {
        ///    if(!n.@f.length()) {                                   // 'f' = the SECONDARY weapon
        ///       if(!(n.@dif.length() &amp;&amp; n.@dif &gt; dif)) {            // difficulty gate
        ///          if(n.@ch.length() == 0 || this.isrnd(n.@ch)) {   // chance roll
        ///             weap = Weapon.create(this, n.@id);
        ///             if(weap) return weap;                       // a bad id falls through to the next
        ///          }
        ///       }
        ///    }
        /// }
        /// return null;
        /// </code>
        ///
        /// <para><b>The <c>f</c> guard is the outermost test and it is not decoration.</b> A row
        /// carrying <c>f</c> is the unit's secondary weapon — the family constructor grants it by name
        /// (<c>UnitMerc.as:32</c>, <c>UnitRanger.as:28-29</c>, <c>UnitSentinel.as:27</c>,
        /// <c>UnitGutsy.as:28</c>) and the roll must never also hand it out. Because such a row authors
        /// no <c>ch</c>, skipping this guard lets it default to certainty and win the loop on the first
        /// iteration, so every <c>merc1</c> ends up holding the 50-damage <c>mercgr</c> rocket as its
        /// primary. <c>PFE.Data.Definitions.WeaponRowParser</c> carries the full account, including why
        /// the flag must be read as <i>presence</i> rather than value.</para>
        ///
        /// <para><b>The <c>if(weap)</c> is not decoration either.</b> <c>Weapon.create</c> returns
        /// <c>null</c> for an id that is not in <c>AllData.d.weapon</c>, and the oracle then
        /// <i>continues</i> to the next candidate rather than giving up — which is why
        /// <paramref name="isAvailable"/> exists and is consulted before accepting a roll.</para>
        /// </summary>
        /// <param name="options">The unit's <c>&lt;w&gt;</c> rows, in document order.</param>
        /// <param name="difficulty">
        /// AS3's <c>dif</c> parameter — the location's difficulty (<c>Location.locDifLevel</c>, set by
        /// <c>Land.as:983</c>). A candidate whose <c>dif</c> exceeds this is skipped.
        /// </param>
        /// <param name="roll01">A <c>[0,1)</c> draw. AS3's <c>isrnd</c> is <c>Math.random() &lt; p</c>.</param>
        /// <param name="isAvailable">
        /// Optional predicate — <c>false</c> means "no <c>WeaponDefinition</c> for this id", which is
        /// the port's equivalent of <c>Weapon.create</c> returning <c>null</c>. Null means "assume
        /// every id resolves".
        /// </param>
        /// <returns>The index into <paramref name="options"/>, or <see cref="NoWeapon"/>.</returns>
        public static int SelectIndex(
            IReadOnlyList<WeaponOption> options,
            int difficulty,
            Func<float> roll01,
            Func<string, bool> isAvailable = null)
        {
            if (options == null || options.Count == 0) return NoWeapon;
            if (roll01 == null) throw new ArgumentNullException(nameof(roll01));

            for (int i = 0; i < options.Count; i++)
            {
                WeaponOption option = options[i];

                if (string.IsNullOrEmpty(option.WeaponId)) continue;

                // `if(!n.@f.length())` — the secondary weapon is granted by the family constructor, so
                // the roll must step over it. Tested FIRST, exactly as the oracle nests it.
                if (option.IsFixedWeapon) continue;

                // `n.@dif > dif` — strictly greater, so a weapon whose dif equals the location's is
                // eligible.
                if (option.Difficulty > difficulty) continue;

                // `n.@ch.length() == 0 || isrnd(n.@ch)`. Chance 1 is the importer's spelling of the
                // absent attribute, and `roll < 1` is always true, so the two agree.
                if (option.Chance < 1f && roll01() >= option.Chance) continue;

                if (isAvailable != null && !isAvailable(option.WeaponId)) continue;

                return i;
            }

            return NoWeapon;
        }

        /// <summary>
        /// AS3 <c>UnitRaider</c>'s <c>attackerType</c> ladder (<c>UnitRaider.as:257-272</c>), from the
        /// <b>chosen</b> weapon's <c>tip</c> and the unit's own <c>krep</c>.
        ///
        /// <para>The oracle, verbatim — and note that the first arm is tested first, so a
        /// <c>tip &lt;= 1</c> weapon with <c>krep == 1</c> is a <i>contact</i> attacker and never
        /// swings the weapon it is holding:</para>
        /// <code>
        /// if(!currentWeapon || currentWeapon.tip &lt;= 1 &amp;&amp; weaponKrep == 1) attackerType = 0;
        /// else if(currentWeapon.tip == 1 &amp;&amp; weaponKrep == 0)                attackerType = 1;
        /// else if(currentWeapon.tip == 4)                                    attackerType = 3;
        /// else                                                              attackerType = 2;
        /// </code>
        /// </summary>
        /// <param name="weaponTip">
        /// The weapon's AS3 <c>tip</c> — the port's <c>WeaponType</c> (0 Internal, 1 Melee,
        /// 2 Guns, 3 BigGun, 4 Thrown, 5 Magic). Ignored when <paramref name="hasWeapon"/> is
        /// <c>false</c>.
        /// </param>
        /// <param name="weaponKrep">
        /// AS3 <c>weaponKrep</c> — the unit's <c>&lt;comb krep&gt;</c>, defaulting to <b>1</b>
        /// (<c>Unit.as:332</c>), which is why the parameter is named for the value and not for the
        /// attribute. <c>krep == 1</c> means "holds the weapon firmly" and sends a melee-armed unit
        /// down the contact branch.
        /// </param>
        /// <param name="hasWeapon">False when <see cref="SelectIndex"/> returned <see cref="NoWeapon"/>.</param>
        public static int AttackerType(int weaponTip, bool weaponKrep, bool hasWeapon)
        {
            if (!hasWeapon) return AttackerTypeContact;

            bool krepIsOne = weaponKrep;

            if (weaponTip <= 1 && krepIsOne) return AttackerTypeContact;
            if (weaponTip == 1)              return AttackerTypeMeleeWeapon;
            if (weaponTip == 4)              return AttackerTypeThrown;
            return AttackerTypeRanged;
        }
    }
}
