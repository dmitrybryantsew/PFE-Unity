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

        /// <summary>Whether the critical roll passed.</summary>
        public readonly bool IsCritical;

        /// <summary>Nothing happened — a dead target, a null target, or a zero-damage context.</summary>
        public static readonly DamageOutcome None = default;

        public DamageOutcome(
            float hpDamage,
            float armourIntegrityDamage = 0f,
            bool armourBroke = false,
            bool armourReduced = false,
            bool isCritical = false)
        {
            HpDamage = hpDamage;
            ArmourIntegrityDamage = armourIntegrityDamage;
            ArmourBroke = armourBroke;
            ArmourReduced = armourReduced;
            IsCritical = isCritical;
        }

        /// <summary>True when armour ate the whole hit — useful for feedback and for tests.</summary>
        public bool WasFullyAbsorbed => HpDamage <= 0f && ArmourIntegrityDamage > 0f;

        /// <summary>True when nothing at all was dealt.</summary>
        public bool IsEmpty => HpDamage <= 0f && ArmourIntegrityDamage <= 0f;
    }
}
