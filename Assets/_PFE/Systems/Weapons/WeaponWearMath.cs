namespace PFE.Systems.Weapons
{
    /// <summary>
    /// The durability ("breaking") wear penalties — AS3 <c>Weapon.breaking</c> and the two
    /// <c>resultDamage</c> shapes that consume it.
    ///
    /// <para><b>Why it is a pure static.</b> The only consumers are weapon controllers built from a
    /// <c>WeaponDefinition</c> <c>ScriptableObject</c>, so a fixture that drives one cannot run
    /// outside the editor. Taking plain numbers and returning a number puts the arithmetic somewhere
    /// it <i>can</i> be executed and pinned offline — the same reason <see cref="WeaponSpreadMath"/>,
    /// <see cref="UnitSweepMath"/> and <c>PenetrationMath</c> exist. The controllers keep the
    /// composition (which live value feeds which argument); this keeps the formula.</para>
    ///
    /// <para><b>The port computed <c>breaking</c> and never spent it on damage.</b>
    /// <c>WeaponRuntimeState.Breaking()</c> was read by the ranged path for the <i>spread</i>
    /// (<c>Weapon.as:1460</c>) and the jam chance (<c>:1426-1446</c>), but neither <c>resultDamage</c>
    /// ever received it — so a weapon one hit point from destruction dealt exactly the damage of a new
    /// one. AS3 multiplies every shot's damage by <c>(1 - breaking*0.3)</c> in the base
    /// <c>resultDamage</c> (<c>:1629</c>) and by <c>(1 - breaking*0.6)</c> in the melee override
    /// (<c>WClub.as:649</c>).</para>
    /// </summary>
    public static class WeaponWearMath
    {
        /// <summary>
        /// AS3 <c>Weapon.breaking</c>, computed identically in <c>checkAvail()</c>
        /// (<c>Weapon.as:1356-1362</c>) and in <c>WClub.shoot()</c> (<c>WClub.as:581-588</c>):
        /// <code>
        /// if(hp &lt; maxhp / 2) breaking = (maxhp - hp) / maxhp * 2 - 1;
        /// else                 breaking = 0;
        /// </code>
        /// So it is <b>0</b> at or above half durability and ramps <b>0 → 1</b> as the weapon is worn
        /// from half down to nothing. It is <i>not</i> the signed <c>-1..1</c> value the dead
        /// <c>CombatCalculator.CalculateBreaking</c> returns: feeding that in would give a
        /// full-durability weapon a damage <i>bonus</i> the oracle does not grant.
        ///
        /// <para><b><c>maxhp / 2</c> is integer division</b>, as in AS3 (<c>maxhp:int</c>): for
        /// <c>maxhp = 101</c> the knee sits at <c>hp &lt; 50</c>. Reproduced, not "corrected".</para>
        /// </summary>
        /// <param name="maxHp">AS3 <c>maxhp</c> — the durability pool, imported from <c>@maxhp</c>.</param>
        /// <param name="currentHp">AS3 <c>hp</c> — the weapon's remaining durability.</param>
        public static float Breaking(int maxHp, int currentHp)
        {
            if (maxHp <= 0)
                return 0f;                      // no durability pool: there is nothing to wear out

            if (currentHp >= maxHp / 2)
                return 0f;                      // the oracle's `else` branch

            float breaking = (maxHp - currentHp) / (float)maxHp * 2f - 1f;

            // Total, not defensive: the branch above already bounds `currentHp` to [0, maxHp/2), so
            // the arithmetic lands in [0, 1]. The clamp exists so a caller passing a negative maxHp
            // (or a future change to the branch) cannot push either multiplier outside [0.4, 1].
            return breaking < 0f ? 0f : (breaking > 1f ? 1f : breaking);
        }

        /// <summary>
        /// AS3 <c>Weapon.resultDamage</c>'s last factor (<c>Weapon.as:1629</c>) —
        /// <c>(1 - breaking * 0.3)</c>. <b>1</b> at or above half durability, <b>0.7</b> at zero.
        /// Consumed by every weapon type whose <c>resultDamage</c> is the base one (ranged, magic);
        /// melee has its own, twice as steep (see <see cref="MeleeDamageMultiplier"/>).
        /// </summary>
        public static float BaseDamageMultiplier(float breaking) => 1f - breaking * 0.3f;

        /// <summary>
        /// AS3 <c>WClub.resultDamage</c>'s last factor (<c>WClub.as:649</c>) —
        /// <c>(1 - breaking * 0.6)</c>. <b>1</b> at or above half durability, <b>0.4</b> at zero: a
        /// worn blade loses twice the fraction a worn barrel does.
        /// </summary>
        public static float MeleeDamageMultiplier(float breaking) => 1f - breaking * 0.6f;
    }
}
