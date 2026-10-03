namespace PFE.Systems.Weapons
{
    /// <summary>
    /// Where a shot's pellets actually go — the muzzle-angle deviation and the per-pellet spread that
    /// the base <c>Weapon.shoot</c> applies (<c>Weapon.as:1460</c> and <c>:1496</c>).
    ///
    /// <para><b>Why it is a pure static.</b> The rule lives in a controller that can only be built
    /// from a <c>WeaponDefinition</c> <c>ScriptableObject</c>, so a fixture that drives it cannot run
    /// outside the editor. Taking plain numbers and returning a number puts the arithmetic somewhere
    /// it <i>can</i> be executed and pinned offline — the same reason <see cref="UnitSweepMath"/> and
    /// <c>WeaponMuzzleOffsetMath</c> exist. The controller keeps the composition (which fields feed
    /// which argument, and in what order); this keeps the formula.</para>
    /// </summary>
    public static class WeaponSpreadMath
    {
        /// <summary>
        /// The rounded π the oracle hard-codes in both terms. <b>Deliberately not
        /// <c>Mathf.PI</c>.</b> <c>Weapon.as</c> says <c>3.1415</c>, not <c>Math.PI</c> — the original
        /// author typed the constant — so the port uses the same number rather than silently
        /// "correcting" it to the transcendental value. The difference is ~1e-5 relative and
        /// invisible in play; it is kept because the point of this port is that the arithmetic
        /// matches, and a reader who sees <c>Mathf.PI</c> here would be right to ask which is the
        /// oracle.
        /// </summary>
        public const float OraclePi = 3.1415f;

        /// <summary>
        /// AS3 <c>Weapon.shoot</c>'s <c>_loc2_</c> (<c>Weapon.as:1460</c>), in radians:
        /// <code>
        /// (Math.random() - 0.5) * (deviation * (1 + breaking*2) / skillConf / (_loc1_ + 0.01)
        ///                          + owner.mazil)
        ///                        * 3.1415 / 180 * devMult
        /// </code>
        ///
        /// <para>AS3 multiplies by <c>devMult</c> as well; it is omitted because no
        /// <c>&lt;perk&gt;</c> row in <c>AllData.as</c> defines a <c>*Dev</c> field, so the factor is
        /// always 1 (see <c>Weapon.as:1038</c>, which is the only writer). Adding a parameter nothing
        /// can set would be a dead seam.</para>
        ///
        /// <para><b>The divisor is <c>weaponSkillMultiplier + 0.01</c>, and there is no guard against
        /// it being small.</b> That is the oracle's own shape: a shooter with no points in the skill
        /// gets a finite 100× cone rather than a division by zero. <paramref name="skillConfidence"/>
        /// is never 0 (it is 1, 0.8 or 0.6), so the other divisor is safe by construction.</para>
        /// </summary>
        /// <param name="random01">One draw from the combat stream — AS3's <c>Math.random()</c>.</param>
        /// <param name="deviation">The weapon's <c>@deviation</c>, in degrees, raw (not wear-scaled).</param>
        /// <param name="breaking">The weapon's wear fraction, 0..1 — <c>Weapon.breaking</c>.</param>
        /// <param name="skillConfidence">AS3 <c>skillConf</c> from <c>checkAvail</c>; see <c>HitAvoidance.SkillConfidence</c>.</param>
        /// <param name="weaponSkillMultiplier">AS3 <c>_loc1_</c>; see <c>IWeaponStatSource.WeaponSkillMultiplier</c>.</param>
        /// <param name="mazil">AS3 <c>owner.mazil</c>, a flat addend applied <i>after</i> the division; see <c>IWeaponStatSource.MazilAdd</c>.</param>
        public static float Deviation(
            float random01, float deviation, float breaking,
            float skillConfidence, float weaponSkillMultiplier, float mazil)
        {
            return (random01 - 0.5f)
                 * (deviation * (1f + breaking * 2f) / skillConfidence / (weaponSkillMultiplier + 0.01f)
                    + mazil)
                 * OraclePi / 180f;
        }

        /// <summary>
        /// AS3 <c>Weapon.as:1496</c>'s per-pellet term, in radians:
        /// <code>
        /// (_loc3_ - (kol - 1) / 2) * deviation * 3.1415 / 360
        /// </code>
        ///
        /// <para>This is the symmetric fan: pellet <c>i</c> sits at its own offset from the centre of
        /// the group, so the middle pellet of an odd burst is exactly on the aim axis. Note it uses
        /// the weapon's <b>raw</b> <c>deviation</c>, not the wear-scaled one — the <c>breaking</c>
        /// term belongs to <see cref="Deviation"/> alone.</para>
        /// </summary>
        public static float PelletSpread(int pelletIndex, int pelletCount, float deviation)
        {
            return (pelletIndex - (pelletCount - 1) / 2f) * deviation * OraclePi / 360f;
        }
    }
}
