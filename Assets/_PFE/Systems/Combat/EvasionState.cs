namespace PFE.Systems.Combat
{
    /// <summary>
    /// A target's evasion projection — the three fields <c>Unit.udarBullet()</c> reads to decide
    /// whether a hit lands at all. Read by <see cref="HitAvoidance"/>; never written by it.
    ///
    /// <para><b>Why this is a struct on <see cref="IDamageable"/> rather than three members.</b> The
    /// hit-avoidance test reads all three together and reads nothing else, so they travel as one
    /// value — the same reason <see cref="ArmourState"/> is one value and not five properties. It also
    /// keeps the interface from growing a member per oracle field.</para>
    ///
    /// <para><b>Oracle</b> — <c>fe/unit/Unit.as</c>:</para>
    /// <list type="bullet">
    ///   <item><description><c>dexter</c> (<c>:166</c>, default <c>1</c>) — the ranged evasion divisor.
    ///     Set from <c>@dexter</c> on the unit node (<c>:1170-1172</c>); 71 units in
    ///     <c>AllData.as</c> carry it, up to <c>dexter='100'</c> (the stationary <c>npc</c>, which is
    ///     why an NPC target dummy is so hard to shoot).</description></item>
    ///   <item><description><c>dexterPlus</c> (<c>:168</c>, default <c>0</c>) — a flat addition to the
    ///     divisor, not a multiplier. Only ever set on the <i>player</i>, and only while sitting or
    ///     lurking (<c>UnitPlayer.as:1117-1126</c>), from
    ///     <c>pers.sitDexterPlus</c>/<c>pers.lurkDexterPlus</c>. An NPC never moves it off 0.</description></item>
    ///   <item><description><c>dodge</c> (<c>:170</c>, default <c>0</c>) — the <i>melee</i> avoidance
    ///     probability, read only when the attack is a melee swing (<c>tipBullet == 1</c>). Set on the
    ///     player to <c>1 + dodgePlus</c> while dashing and <c>0 + dodgePlus</c> otherwise
    ///     (<c>UnitPlayer.as:1416-1422</c>); <c>dodgePlus</c> itself comes from equipped armour
    ///     (<c>Pers.as:2016-2017</c>, <c>gg.dodgePlus += armour.dexter</c>). An NPC never moves it off
    ///     0, so <b>melee always lands on an NPC</b> — that is the oracle, not an omission.</description></item>
    /// </list>
    ///
    /// <para><b>The three are deliberately not collapsed.</b> Ranged attacks read
    /// <c>dexter + dexterPlus</c> as a <i>divisor on accuracy</i>; melee reads <c>dodge</c> as a
    /// <i>probability</i>. They are different mechanics that happen to share a code line, and merging
    /// them would make a plate's dodge bonus start dodging bullets.</para>
    ///
    /// <para><b>Producer status, stated rather than implied.</b> <see cref="Dexterity"/> has a live
    /// producer today — the imported unit definition. <see cref="DexterityPlus"/> and
    /// <see cref="Dodge"/> do not: both are player-only and both are driven by the RPG/armour bridge,
    /// which is why they sit here at the oracle's own defaults (0 and 0) rather than being absent. A
    /// missing field would make the formula wrong; a field at the oracle's default makes it correct
    /// for every case the port can currently evaluate.</para>
    /// </summary>
    public readonly struct EvasionState
    {
        /// <summary>Ranged evasion divisor — AS3 <c>Unit.dexter</c>. <c>&lt;= 0</c> disables evasion entirely.</summary>
        public readonly float Dexterity;

        /// <summary>Flat addition to the ranged divisor — AS3 <c>Unit.dexterPlus</c>. Player sit/lurk only.</summary>
        public readonly float DexterityPlus;

        /// <summary>Melee avoidance probability 0..1 — AS3 <c>Unit.dodge</c>. <c>&gt;= 1</c> dodges everything.</summary>
        public readonly float Dodge;

        public EvasionState(float dexterity, float dexterityPlus, float dodge)
        {
            Dexterity     = dexterity;
            DexterityPlus = dexterityPlus;
            Dodge         = dodge;
        }

        /// <summary>
        /// AS3's field defaults — <c>dexter = 1</c>, <c>dexterPlus = 0</c>, <c>dodge = 0</c>. The value
        /// for a target the port knows nothing about, and <b>not</b> all-zeroes: a zero
        /// <see cref="Dexterity"/> means "cannot evade at all" in the oracle, which is a different
        /// claim from "no evasion data".
        /// </summary>
        public static EvasionState Default => new EvasionState(1f, 0f, 0f);

        /// <summary>
        /// AS3's first dodge-group term — <c>this.dexter &lt;= 0</c> — which short-circuits the whole
        /// group. A unit with no dexterity is hit by everything, whatever its accuracy or the shot's
        /// distance.
        /// </summary>
        public bool CannotEvade => Dexterity <= 0f;
    }
}
