namespace PFE.Systems.Weapons
{
    /// <summary>
    /// The <b>live</b> mana a magic weapon spends — AS3 <c>owner.mana</c> (the regenerating magic
    /// budget, <c>Unit.as:138</c>) and <c>World.w.pers.manaHP</c> (the mana organ, <c>Pers.as:141</c>).
    ///
    /// <para><b>Why this is a separate seam from <see cref="IWeaponStatSource"/>.</b> That interface
    /// carries the <i>multipliers</i> AS3 copies onto the weapon instance once, in <c>setParams</c>
    /// (<c>Weapon.as:963-1050</c>). Mana is not a weapon parameter: it is per-unit state that changes
    /// every tick and is <i>mutated</i> by firing. Folding it into the multiplier interface would put
    /// a resource pool on a contract documented as "values copied at equip time". The same reasoning
    /// produced <see cref="IAmmoSource"/> next door.</para>
    ///
    /// <para><b>A null source means "no mana tracking", not "zero mana".</b> The magic controller then
    /// skips both the gate and the spend, which is AS3's state for a unit whose mana was never
    /// initialised, and reproduces the port's previous behaviour for a rig with no
    /// <c>CharacterStats</c> in the hierarchy.</para>
    /// </summary>
    public interface IManaSource
    {
        /// <summary>
        /// AS3 <c>Unit.mana</c> — the regenerating magic budget that pays <c>dmagic</c>
        /// (<c>Weapon.as:885-891</c> reads the weapon's own <c>magic</c> attribute into
        /// <c>Weapon.dmagic</c>; <c>WMagic.shoot()</c> then does <c>owner.mana -= dmagic</c>).
        /// </summary>
        float MagicMana { get; }

        /// <summary>
        /// AS3 <c>Unit.maxmana</c> — the budget's ceiling. Only used for the "pool is full" bypass
        /// <c>owner.mana &gt;= owner.maxmana * 0.99</c> (<c>WMagic.as:67</c>), which lets a full pool
        /// fire even when a single shot would overdraw it.
        /// </summary>
        float MaxMagicMana { get; }

        /// <summary>
        /// AS3 <c>Pers.manaHP</c> — the mana <b>organ</b> that pays <c>dmana</c>. Unlike the budget it
        /// does not regenerate, and a shot whose organ cost exceeds it is refused with a lockout
        /// (<c>WMagic.as:60-66</c>).
        /// </summary>
        float ManaHp { get; }

        /// <summary>
        /// AS3 <c>World.w.pers.spellsPoss</c> (<c>Pers.as:423</c>, 1) — whether this unit may cast
        /// <b>at all</b>. <c>WMagic.attack()</c> refuses the shot outright when it is 0
        /// (<c>WMagic.as:33-39</c>), before the skill gate and before any timer is armed.
        ///
        /// <para><b>What sets it to 0: the mana organ.</b> <c>Pers.as:2007</c> zeroes it inside the
        /// limb-trauma pass, and the port reproduces that at
        /// <c>CharacterStats.ApplyTraumaModifiers()</c> (<c>CharacterStats.cs:2387-2392</c>) —
        /// <c>manaSt &gt;= 4</c>, i.e. the organ driven to roughly zero, disables casting. The value
        /// <b>tracks</b> the organ rather than latching: <c>RecalculateStats()</c> restores it to 1 on
        /// the way in (<c>:1451</c> → <c>ResetToDefaults()</c>) and re-derives it at step 7
        /// (<c>:1516</c>), and <c>ApplyManaDamage</c> recomputes the stage on every wound
        /// (<c>:2552-2556</c>). So healing the organ restores casting.</para>
        ///
        /// <para><b>A refusal arms no lockout.</b> AS3 returns <c>false</c> from this gate before
        /// reaching either <c>t_rel</c> assignment (<c>:62</c>, <c>:80</c>), so a caster with no
        /// spells is refused every frame the trigger is held instead of being put on a cooldown.
        /// That asymmetry with the mana gates is deliberate and is pinned by a test.</para>
        ///
        /// <para><b>Why here and not on <see cref="IWeaponStatSource"/>.</b> That contract is the
        /// multipliers AS3 copies onto the weapon <i>instance</i> once, in <c>setParams</c>. This is
        /// live per-unit state read at attack time, and it is <i>produced</i> by the mana-organ damage
        /// path this interface already models — the same organ <see cref="ManaHp"/> reports.</para>
        ///
        /// <para><b>1 is the identity for a null source</b>, and it is the correct reading: AS3
        /// reaches the gate only under <c>if(owner.player)</c>, so a unit with no <c>Pers</c> is never
        /// refused. See <c>HitAvoidance.CanCastSpells</c>.</para>
        /// </summary>
        int SpellsPossible { get; }

        /// <summary>
        /// The owner's combined mana-cost multiplier, <c>pers.allDManaMult * pers.warlockDManaMult</c>.
        ///
        /// <para><c>WMagic.setPers</c> (<c>WMagic.as:92-98</c>) applies the same product to
        /// <b>both</b> halves of the cost — <c>dmana = mana * mult</c> and
        /// <c>dmagic = magic * mult</c> — so a warlock perk that reduces mana spend lowers the budget
        /// debit <i>and</i> the organ wound together. One multiplier is therefore enough.</para>
        ///
        /// <para><b>1 is the identity, and it is the right fallback.</b> Both <c>Pers</c> fields
        /// declare 1 and <c>defaultParams()</c> resets them to 1, so an owner with no perks pays the
        /// raw attribute cost.</para>
        /// </summary>
        float ManaCostMultiplier { get; }

        /// <summary>
        /// Spend both halves of a magic shot, in the order <c>WMagic.shoot()</c> uses
        /// (<c>WMagic.as:110-122</c>): the budget is debited, then the organ is wounded through
        /// <c>pers.manaDamage(dmana)</c>.
        ///
        /// <para>Called <b>after</b> the shot is emitted, because <c>WMagic.shoot()</c> calls
        /// <c>super.shoot()</c> first and only debits when that returns a bullet.</para>
        /// </summary>
        void SpendMana(float poolCost, float organCost);
    }
}
