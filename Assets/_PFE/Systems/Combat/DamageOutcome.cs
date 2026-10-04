namespace PFE.Systems.Combat
{
    /// <summary>
    /// The result of resolving one damage context against one target.
    ///
    /// <para>Carries the <b>breakdown</b>, not just a number, so three consumers can use it without
    /// re-deriving anything: the target applies it, the HUD/floating text reports it, and tests assert
    /// on the individual terms.</para>
    ///
    /// <para>Pure value type, no <c>UnityEngine.Object</c> references — the same rule the replication
    /// layer needs, so this can cross a network boundary unchanged.</para>
    /// </summary>
    public readonly struct DamageOutcome
    {
        /// <summary>Damage that reaches the health pool, after armour, crit and durability.</summary>
        public readonly float HpDamage;

        /// <summary>Damage taken off the armour item's integrity. Already scaled by the caller's type multiplier.</summary>
        public readonly float ArmourIntegrityDamage;

        /// <summary>Whether this hit is the one that took the armour to zero integrity.</summary>
        public readonly bool ArmourBroke;

        /// <summary>
        /// Whether the armour's reliability roll passed and its flat rating was subtracted.
        /// <c>false</c> when there is no armour, when it is broken, or when the roll failed — so this
        /// is the flag to use for "my armour saved me" feedback.
        /// </summary>
        public readonly bool ArmourReduced;

        /// <summary>Whether the ordinary critical roll passed — AS3 <c>_loc5_ = 1</c> (<c>Unit.as:3657</c>).</summary>
        public readonly bool IsCritical;

        /// <summary>
        /// Whether the <b>stealth</b> crit roll passed — AS3's <c>_loc5_ += 2</c>
        /// (<c>Unit.as:3659-3666</c>), the second, independent roll that doubles the damage again.
        /// </summary>
        /// <remarks>
        /// <para><b>Reported separately because AS3 keeps the two in one bitfield and reads the bits
        /// apart.</b> <c>_loc5_</c> is <c>0</c> for no crit, <c>1</c> for an ordinary crit, <c>2</c>
        /// for a stealth crit and <c>3</c> for both — so the oracle can and does ask questions the two
        /// flags answer differently:</para>
        /// <list type="bullet">
        /// <item><description><c>_loc5_ &gt; 0</c> — <see cref="AnyCritical"/> — at <c>:3882</c>, where a
        /// crit makes the blood explosion likelier.</description></item>
        /// <item><description><c>_loc5_ &gt;= 2</c> — <see cref="IsStealthCritical"/> — at
        /// <c>:3916</c>, the gate on the impact-feedback block.</description></item>
        /// <item><description><c>_loc5_ == 1 || _loc5_ == 3</c> — <see cref="IsCritical"/> — at
        /// <c>:3935</c>, <c>:3947</c> and <c>:3952</c>, the 1.6× scale on the impact
        /// particle.</description></item>
        /// </list>
        ///
        /// <para><b>Why it was missing.</b> <c>DamageCalculator.ResolveDamage</c> set <c>isCrit</c> in
        /// the ordinary-crit branch only; the stealth branch doubled the damage and left the flag alone.
        /// That is correct for <see cref="IsCritical"/> as documented — but it meant <c>_loc5_ &gt; 0</c>
        /// and <c>_loc5_ &gt;= 2</c> were both unreachable from the port, so a stealth-only crit produced
        /// no crit-scaled feedback anywhere. Recording the bit AS3 records is what closes it; folding the
        /// two into one bool would have kept the stealth half invisible.</para>
        /// </remarks>
        public readonly bool IsStealthCritical;

        /// <summary>
        /// AS3's <c>_loc5_ &gt; 0</c> — <b>either</b> crit channel fired. The predicate for "this was a
        /// critical hit" in every sense a player would recognise, and the one the oracle uses at
        /// <c>Unit.as:3882</c>.
        /// </summary>
        public bool AnyCritical => IsCritical || IsStealthCritical;

        /// <summary>Nothing happened — a dead target, a null target, or a zero-damage context.</summary>
        public static readonly DamageOutcome None = default;

        public DamageOutcome(
            float hpDamage,
            float armourIntegrityDamage = 0f,
            bool armourBroke = false,
            bool armourReduced = false,
            bool isCritical = false,
            bool isStealthCritical = false)
        {
            HpDamage = hpDamage;
            ArmourIntegrityDamage = armourIntegrityDamage;
            ArmourBroke = armourBroke;
            ArmourReduced = armourReduced;
            IsCritical = isCritical;
            IsStealthCritical = isStealthCritical;
        }

        /// <summary>True when armour ate the whole hit — useful for feedback and for tests.</summary>
        public bool WasFullyAbsorbed => HpDamage <= 0f && ArmourIntegrityDamage > 0f;

        /// <summary>True when nothing at all was dealt.</summary>
        public bool IsEmpty => HpDamage <= 0f && ArmourIntegrityDamage <= 0f;
    }
}
