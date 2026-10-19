using PFE.Entities.Units;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// How an NPC's <b>own</b> armour pool is built from its <c>&lt;comb&gt;</c> row — the half of AS3's
    /// armour model that no production code ever constructed.
    ///
    /// <para><b>Two armour models, and this is the one the player never uses.</b>
    /// <c>Unit.damage()</c> guards its pool block on <c>!this.player</c> (<c>Unit.as:3578</c>), so every
    /// NPC — robots, raiders in metal, the metal-clad monsters — carries <c>armor_hp</c>, while the player
    /// carries a projected <c>Armor</c> item instead. The reduction rules are shared
    /// (<see cref="ArmourWear.ChannelFor"/>), the <i>wear</i> rules are not
    /// (<see cref="ArmourWear.PoolIntegrityDamage"/> vs <see cref="ArmourWear.ItemIntegrityDamage"/>),
    /// and <see cref="ArmourModel"/> is what keeps them apart.</para>
    ///
    /// <para><b>Why this is a separate, Unity-free class.</b> The rules are three lines of arithmetic and
    /// one asymmetry that is easy to lose, so they are pinned outside the editor against deliberately
    /// wrong variants rather than only through a spawner. <c>UnitController.Initialize</c> is the single
    /// caller.</para>
    ///
    /// <para><b>Oracle.</b> <c>Unit.as:1146-1165</c> (the parse) and <c>:3578-3628</c> (the use).</para>
    /// </summary>
    public static class UnitArmourMath
    {
        /// <summary>
        /// Whether the row describes an armour pool at all — AS3
        /// <c>(this.armor &gt; 0 || this.marmor &gt; 0)</c> (<c>Unit.as:3578</c>).
        ///
        /// <para><b>Both ratings gate the pool, and neither is optional.</b> A row with
        /// <c>marmor='5'</c> and no <c>armor</c> is an energy-armoured unit and still needs a pool to
        /// wear. Conversely a row with <c>aqual='0.5'</c> but no rating at all is <i>not</i> armoured:
        /// the gate fails, the pool block never runs, and the reduction block adds
        /// <c>armor</c> = 0 — so building a pool there would only add a durability bar that depletes
        /// while protecting nothing.</para>
        /// </summary>
        public static bool HasPool(int armour, int magicArmour)
            => armour > 0 || magicArmour > 0;

        /// <summary>
        /// The pool's maximum, and <b>the two arms are scaled differently</b> — which is the detail this
        /// class exists for.
        ///
        /// <code>
        /// if(node.@armorhp.length()) this.armor_hp = this.armor_maxhp = node.@armorhp * this.hpmult;
        /// else                       this.armor_hp = this.armor_maxhp = this.hp;
        /// </code>
        ///
        /// <para>An <b>authored</b> <c>@armorhp</c> is read at <c>:1160</c>, i.e. <i>after</i> the
        /// difficulty cut at <c>:1130-1141</c> — so it is <b>not</b> scaled by difficulty. The
        /// <b>fallback</b> is <c>this.hp</c> at <c>:1164</c>, read <i>after</i> that same cut — so on easy
        /// a pool-by-fallback is <b>smaller</b> by the same 0.4/0.7 the health was cut by, while a pool
        /// with an explicit <c>@armorhp</c> is not. Two arms of one <c>if</c>, two different answers to
        /// "does difficulty touch this".</para>
        ///
        /// <para>That is why this takes the <b>post-difficulty</b> HP and the <b>raw</b> authored pool,
        /// rather than one "hp" argument: the caller already has both (the spawner computed the scaled
        /// HP into <c>UnitStats.MaxHp</c>, the definition carries <c>@armorhp</c>), and collapsing them
        /// into one argument silently picks an arm.</para>
        /// </summary>
        /// <param name="authoredArmourHp">AS3 <c>@armorhp</c>, or 0 when the row does not author it.</param>
        /// <param name="hpAfterDifficulty">
        /// The unit's maximum HP <b>after</b> the difficulty cut — AS3 <c>this.hp</c> as read at
        /// <c>:1164</c>.
        /// </param>
        /// <remarks>
        /// <b>"0" and "absent" are the same input here, and that is safe rather than lucky:</b>
        /// <c>armorhp='0'</c> appears <b>zero</b> times in the 8 MB oracle, so the port's
        /// <c>int</c> cannot be asked to tell the two apart. A row that did author 0 would get the
        /// fallback, where AS3 would get an empty pool — recorded, not defended, because the data cannot
        /// reach it.
        /// </remarks>
        public static float PoolCapacity(int authoredArmourHp, float hpAfterDifficulty)
            => authoredArmourHp > 0 ? authoredArmourHp : hpAfterDifficulty;

        /// <summary>
        /// Build the projection an NPC's damage path reads, or <see cref="ArmourState.None"/> when the
        /// row has no ratings to protect with.
        ///
        /// <para><b>The pool starts full.</b> AS3 assigns <c>armor_hp = armor_maxhp</c> in the same
        /// statement that computes it (<c>:1160</c>, <c>:1164</c>), and the only other writer that fills
        /// it is the respawn reset (<c>:1723</c>).</para>
        ///
        /// <para><b><paramref name="quality"/> may be 0 and the pool is still built.</b> That is the
        /// oracle: <c>armor_qual = 0</c> suppresses the <i>reduction</i>, not the pool. A unit with
        /// <c>armor='5'</c> and no <c>aqual</c> wears its armour down over the fight and takes full
        /// damage throughout — odd, but authored, and 0 is AS3's field default rather than an accident
        /// of the import.</para>
        /// </summary>
        /// <param name="armour">AS3 <c>@armor</c> — flat physical reduction, gated by the reliability roll.</param>
        /// <param name="magicArmour">AS3 <c>@marmor</c> — flat energy reduction, same gate.</param>
        /// <param name="authoredArmourHp">AS3 <c>@armorhp</c>, or 0.</param>
        /// <param name="hpAfterDifficulty">Post-difficulty max HP, used only by the fallback arm.</param>
        /// <param name="quality">AS3 <c>@aqual</c> — the probability the rating applies, 0..1.</param>
        public static ArmourState Build(
            int armour,
            int magicArmour,
            int authoredArmourHp,
            float hpAfterDifficulty,
            float quality)
        {
            if (!HasPool(armour, magicArmour))
                return ArmourState.None;

            float capacity = PoolCapacity(authoredArmourHp, hpAfterDifficulty);

            return ArmourState.FromUnitPool(
                integrity: capacity,
                maxIntegrity: capacity,
                physicalRating: armour,
                energyRating: magicArmour,
                reliability: quality);
        }
    }
}
