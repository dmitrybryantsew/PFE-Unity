namespace PFE.Data.Definitions
{
    /// <summary>
    /// Spell item data — AS3's <c>&lt;item tip='spell' …&gt;</c> attributes, every one read by
    /// <c>Spell.as:83-131</c>.
    ///
    /// <para><b>Scope: what <c>Spell</c> itself reads.</b> The nine rows also carry <c>price</c>,
    /// <c>mess</c> and <c>pet_info</c>, which are general <c>&lt;item&gt;</c> attributes rather than
    /// spell ones — <c>mess</c> is read by the item model (<c>serv/Item.as:303</c>) and
    /// <c>pet_info</c> by the inventory page (<c>inter/PipPage.as:841</c>), for pet-granting
    /// <i>equipment</i> as well. They are deliberately not here; see the item-table note in
    /// <c>docs/OnWeaponsSystemImplementation/14_MagicSystemAudit_2026-10-03.md</c> §3.1.</para>
    ///
    /// <para><b>Attribute names are kept verbatim</b> — <c>culd</c>, <c>rad</c>, <c>dam</c> — so a diff
    /// against <c>AllData.as:4008-4016</c> stays readable. AS3's constructor is a run of
    /// <c>if(this.xml.@x.length()) this.x = this.xml.@x;</c> blocks, so <b>absence leaves the C#
    /// default rather than meaning zero</b>: the guard is on presence.</para>
    /// </summary>
    [System.Serializable]
    public struct SpellData
    {
        /// <summary>
        /// AS3 <c>@hp</c> (<c>Spell.as:84-86</c>) — the hit points of <b>what the spell creates</b>,
        /// not a heal amount: <c>sp_mwall</c> 200 (the wall), <c>sp_mshit</c> 150 (the shield),
        /// <c>sp_gwall</c> 100, <c>sp_slow</c> 60. Read by the individual <c>cast_*</c> handlers.
        /// </summary>
        public float hp;

        /// <summary>
        /// AS3 <c>@mana</c> (<c>:88-90</c>) — the mana <b>organ</b> cost, i.e. this spell's <c>dmana</c>.
        /// Sibling of the weapon path's <c>ammo@mana</c>: <c>dmana = mana * pers.allDManaMult</c>
        /// (<c>:224</c>), and the cast is refused while <c>dmana &gt; pers.manaHP</c> (<c>:237</c>).
        /// </summary>
        public float mana;

        /// <summary>
        /// AS3 <c>@magic</c> (<c>:92-94</c>) — the regenerating mana <b>budget</b> cost, i.e.
        /// <c>dmagic</c>: <c>dmagic = magic * pers.allDManaMult</c> (<c>:223</c>), <b>clamped to
        /// 999</b> (<c>:225-228</c>) before the affordability test (<c>:229</c>). The clamp is
        /// load-bearing for <c>sp_moon</c> and <c>sp_invulner</c>, whose raw 800 can exceed it once
        /// multiplied.
        ///
        /// <para><b>Asymmetry with the weapon path:</b> a spell's cost carries only
        /// <c>allDManaMult</c>, where <c>WMagic.setPers</c> also applies <c>warlockDManaMult</c>
        /// (<c>WMagic.as:95-96</c>). Do not route spells through
        /// <c>IManaSource.ManaCostMultiplier</c>.</para>
        /// </summary>
        public float magic;

        /// <summary>
        /// AS3 <c>@culd</c> (<c>:96-98</c>) — the cooldown, <b>as the raw attribute value</b>.
        ///
        /// <para><b>AS3 multiplies this by the frame rate at construction</b>
        /// (<c>this.culd = @culd * World.fps</c>, <c>:98</c>), so the attribute is in <i>seconds</i>
        /// while the object's field and the <c>t_culd</c> timer are in <i>frames</i>. The raw value is
        /// stored here so that conversion stays visible at its one call site instead of being baked
        /// into imported data.</para>
        ///
        /// <para><c>culd &gt;= 100</c> is also the threshold for the <c>spellCuld</c> GUI message
        /// (<c>:216</c>), which renders the remainder as <c>ceil(t_culd / World.fps)</c>.</para>
        /// </summary>
        public float culd;

        /// <summary>AS3 <c>@dist</c> (<c>:100-102</c>) — maximum cast distance. <c>sp_mwall</c>, <c>sp_gwall</c> 300.</summary>
        public float dist;

        /// <summary>
        /// AS3 <c>@line</c> (<c>:104-106</c>) — a <b>presence flag</b>, not a number: the spell needs
        /// line of sight. <c>sp_mwall</c> and <c>sp_gwall</c> set it.
        /// </summary>
        public bool line;

        /// <summary>AS3 <c>@rad</c> (<c>:108-110</c>) — effect radius. <c>sp_blast</c> 500, <c>sp_slow</c> 200.</summary>
        public float rad;

        /// <summary>
        /// AS3 <c>@dam</c> (<c>:112-114</c>) — damage. <c>sp_blast</c> 20, <c>sp_kdash</c> 20,
        /// <c>sp_gwall</c> 10, <c>sp_invulner</c> 50.
        /// </summary>
        public float dam;

        /// <summary>AS3 <c>@prod</c> (<c>:116-118</c>) — <b>presence flag</b>. Only <c>sp_cryst</c> sets it.</summary>
        public bool prod;

        /// <summary>
        /// AS3 <c>@tele</c> (<c>:120-122</c>) — <b>presence flag</b>; AS3's field is <c>teleSpell</c>.
        /// Set by <c>sp_blast</c> and <c>sp_kdash</c>.
        /// </summary>
        public bool tele;

        /// <summary>
        /// AS3 <c>@atk</c> (<c>:124-126</c>) — <b>presence flag</b>: this is an <i>offensive</i> spell.
        ///
        /// <para>Load-bearing beyond the effect: <c>Spell.cast()</c> refuses while
        /// <c>spellsPoss == 0 || atk &amp;&amp; !gg.atkPoss</c> (<c>:203</c>), so an offensive spell
        /// needs <b>both</b> permission flags. Seven of the nine set it; <c>sp_slow</c> and
        /// <c>sp_kdash</c> do not.</para>
        /// </summary>
        public bool atk;

        /// <summary>
        /// AS3 <c>@snd</c> (<c>:129-131</c>) — the cast sound id: <c>slow</c>, <c>mwall</c>,
        /// <c>blast</c>, <c>crystal</c>, <c>dash</c>, <c>mshit</c>.
        /// </summary>
        public string snd;

        /// <summary>
        /// True when this row carries real spell data — the honest "was this imported" discriminator,
        /// in the same spirit as <see cref="ItemDefinition.IsArmour"/>.
        ///
        /// <para>It cannot be inferred from a single field: every field above also has a legal zero
        /// (<c>sp_cryst</c>'s <c>culd</c> is genuinely 0), so "free spell" and "unimported asset" look
        /// alike. <c>snd</c> is the one attribute all nine rows carry, and <c>magic</c> is non-zero on
        /// all nine.</para>
        /// </summary>
        public bool IsPopulated => !string.IsNullOrEmpty(snd) || magic > 0f;
    }
}
