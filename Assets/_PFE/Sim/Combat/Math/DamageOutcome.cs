namespace PFE.Systems.Combat
{
    using PFE.Data.Definitions;

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

        /// <summary>
        /// The target's <c>shithp</c> <b>after</b> this hit absorbed it — AS3 <c>Unit.as:3629-3637</c>.
        /// <see cref="NoShield"/> when the target had no shield up, or when the caller passed none.
        /// </summary>
        /// <remarks>
        /// <para><b>The resolver owns the arithmetic; the target owns the write.</b> The shield's pool
        /// decrement is part of the reduction block in <c>Unit.damage()</c>, so it has to be computed
        /// where the reduction is — but the value lives on the unit. Carrying the result out on the
        /// outcome is the same split <see cref="ArmourIntegrityDamage"/> uses for the armour pool: the
        /// formula stays pure and in one place, and <c>UnitStats.ApplyDamage</c> only writes it
        /// down.</para>
        ///
        /// <para><see cref="NoShield"/> is a sentinel rather than <c>0</c> because <c>0</c> is a
        /// meaningful value — it is "the shield was up and this hit broke it" — and a target that never
        /// had a shield must not have its (already-zero) field overwritten by an unrelated hit's
        /// outcome.</para>
        /// </remarks>
        public readonly float ShieldHpAfter;

        /// <summary>
        /// What kind of damage this was — AS3 <c>Unit.damage(param1, param2, …)</c>'s <c>param2</c>, the
        /// <c>D_*</c> code. Defaults to <see cref="DamageType.PhysicalBullet"/> (<c>D_BUL</c>, 0), which
        /// is AS3's own default for a hit that does not say.
        /// </summary>
        /// <remarks>
        /// <para><b>Carried because the <i>death</i> effect is chosen from it, not from the weapon.</b>
        /// <c>Unit.as:3713-3741</c> turns <c>param2</c> into the <c>_loc10_</c> burn tip that
        /// <c>die(_loc10_)</c> receives, and a positive tip is the <b>first</b> arm of the death branch
        /// table (<c>:4325</c>) — it beats both the corpse arm and the blood-colour arm. Nothing on the
        /// target side survives that long: by the time the body is being decided, the
        /// <see cref="DamageContext"/> is gone and the only thing left is the outcome. Without this field
        /// the port can only ever dissolve a body with the blood preset, so a plasma kill looks
        /// identical to a rifle kill.</para>
        ///
        /// <para><b>Defaulting to bullet is the correct zero, not a placeholder.</b> <c>D_BUL</c> is one
        /// of the codes that maps to <i>no</i> burn tip, so an outcome built by a fixture or by the dev
        /// console — neither of which is a weapon hit — behaves exactly like a rifle shot, which is what
        /// they are.</para>
        /// </remarks>
        public readonly DamageType DamageType;

        /// <summary>
        /// <see cref="ShieldHpAfter"/>'s "this hit did not touch a shield" sentinel. Negative so it can
        /// never collide with a real pool value, which AS3 clamps at <c>0</c>.
        /// </summary>
        public const float NoShield = -1f;

        public DamageOutcome(
            float hpDamage,
            float armourIntegrityDamage = 0f,
            bool armourBroke = false,
            bool armourReduced = false,
            bool isCritical = false,
            bool isStealthCritical = false,
            float shieldHpAfter = NoShield,
            DamageType damageType = default)
        {
            HpDamage = hpDamage;
            ArmourIntegrityDamage = armourIntegrityDamage;
            ArmourBroke = armourBroke;
            ArmourReduced = armourReduced;
            IsCritical = isCritical;
            IsStealthCritical = isStealthCritical;
            ShieldHpAfter = shieldHpAfter;
            DamageType = damageType;
        }

        /// <summary>True when armour ate the whole hit — useful for feedback and for tests.</summary>
        public bool WasFullyAbsorbed => HpDamage <= 0f && ArmourIntegrityDamage > 0f;

        /// <summary>True when nothing at all was dealt.</summary>
        public bool IsEmpty => HpDamage <= 0f && ArmourIntegrityDamage <= 0f;
    }
}
