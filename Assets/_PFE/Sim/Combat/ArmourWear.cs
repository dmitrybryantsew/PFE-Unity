using PFE.Data.Definitions;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// Which resistance rating a damage type is read against, and therefore which branch of AS3's
    /// reduction block runs.
    /// </summary>
    public enum ArmourChannel
    {
        /// <summary>Neither branch runs: no skin, no armour, no rating. The reduction stays zero.</summary>
        None = 0,

        /// <summary>Reads the physical rating — AS3 <c>armor</c> (<c>Unit.as:3613-3620</c>).</summary>
        Physical = 1,

        /// <summary>Reads the energy rating — AS3 <c>marmor</c> (<c>Unit.as:3621-3628</c>).</summary>
        Energy = 2,
    }

    /// <summary>
    /// The damage-type tables that drive armour: which rating a hit is reduced by, and how much
    /// integrity it wears away.
    ///
    /// <para><b>Two of these tables exist in AS3 and they do not agree.</b> The reduction channel is
    /// the same for both armour models — it lives in <c>Unit.damage()</c> and is read by every unit —
    /// but the <i>wear</i> rules differ between the unit pool (<c>Unit.damage()</c>, enemy armour) and
    /// the equipped item (<c>Armor.damage()</c>, the player's). Acid is <c>×4</c> on the pool and
    /// <c>×2</c> on the item; pink wears the item <c>×3</c> and the pool not at all. Both are kept,
    /// because both are reachable — the player uses the item path and every NPC uses the pool.</para>
    ///
    /// <para>Pure and static: no state, no RNG. The caller supplies the raw damage and reads the
    /// scaled wear back.</para>
    ///
    /// <para>Oracle: <c>Unit.damage()</c> (<c>Unit.as:3578-3628</c>),
    /// <c>Armor.damage()</c> (<c>Armor.as:313-338</c>),
    /// <c>UnitPlayer.damage()</c> (<c>UnitPlayer.as:3295-3298</c>).</para>
    /// </summary>
    public static class ArmourWear
    {
        /// <summary>
        /// Which rating this damage type is reduced by.
        ///
        /// <para>AS3's block is two <c>if</c>s with <b>no else</b>, and <c>_loc8_ = this.skin</c>
        /// sits <i>inside</i> each one — so the ten types in neither list get <b>no skin either</b>.
        /// That is the part easiest to get wrong: skin reads as a universal natural resistance, but
        /// it is gated on the same twelve types the armour is.</para>
        /// </summary>
        public static ArmourChannel ChannelFor(DamageType type)
        {
            switch (type)
            {
                // `if (param2 == D_BUL || D_BLADE || D_EXPL || D_PHIS || D_FANG || D_ACID)`
                case DamageType.PhysicalBullet:
                case DamageType.Blade:
                case DamageType.PhysicalMelee:
                case DamageType.Explosive:
                case DamageType.Fang:
                case DamageType.Acid:
                    return ArmourChannel.Physical;

                // `if (param2 == D_FIRE || D_LASER || D_PLASMA || D_SPARK || D_CRIO || D_ASTRO)`
                case DamageType.Fire:
                case DamageType.Laser:
                case DamageType.Plasma:
                case DamageType.Spark:
                case DamageType.Cryo:
                case DamageType.Astral:
                    return ArmourChannel.Energy;

                // Everything else — Venom, Poison, Bleed, Necrotic, Pink, Balefire, Psionic, EMP,
                // Internal, FriendlyFire — leaves `_loc8_` at 0.
                default:
                    return ArmourChannel.None;
            }
        }

        /// <summary>
        /// Whether this type wears the <b>equipped item</b>.
        ///
        /// <para>AS3: <c>Armor.damage()</c> returns early for
        /// <c>D_VENOM</c>, <c>D_EMP</c>, <c>D_POISON</c>, <c>D_BLEED</c>, <c>D_INSIDE</c>
        /// (<c>Armor.as:319</c>), and for <c>und</c> — indestructible armour — at all
        /// (<c>:315-318</c>). Everything else, including the types that get <i>no reduction</i>,
        /// still wears the plate: pink and necrotic bypass armour's protection while grinding it
        /// down.</para>
        /// </summary>
        public static bool WearsEquippedItem(DamageType type)
        {
            switch (type)
            {
                case DamageType.Venom:
                case DamageType.EMP:
                case DamageType.Poison:
                case DamageType.Bleed:
                case DamageType.Internal:
                    return false;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Whether this type wears the <b>unit pool</b> — the enemy armour model.
        ///
        /// <para>AS3 writes this one as a numeric range: <c>param2 &lt;= D_BALE &amp;&amp; param2 != D_EMP
        /// &amp;&amp; param2 != D_POISON &amp;&amp; param2 != D_BLEED || param2 == D_ASTRO</c>
        /// (<c>Unit.as:3578</c>). The port's <see cref="DamageType"/> values match AS3's constants
        /// exactly, so the range is <c>0..15</c> — which is why <b>pink (19) is excluded</b> while
        /// astral (18) has to be named separately. Spelled out as members here rather than as
        /// <c>&lt;= Balefire</c> so that reordering the enum cannot silently change the rule.</para>
        /// </summary>
        public static bool WearsUnitPool(DamageType type)
        {
            switch (type)
            {
                case DamageType.PhysicalBullet:
                case DamageType.Blade:
                case DamageType.PhysicalMelee:
                case DamageType.Fire:
                case DamageType.Explosive:
                case DamageType.Laser:
                case DamageType.Plasma:
                case DamageType.Venom:   // reachable only for `sost == 1` units — see Unit.as:3574
                case DamageType.Spark:
                case DamageType.Acid:
                case DamageType.Cryo:
                case DamageType.Fang:
                case DamageType.Balefire:
                case DamageType.Astral:  // the `|| param2 == D_ASTRO` tail of the AS3 condition
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Integrity removed from an <b>equipped item</b> by one hit.
        ///
        /// <para>AS3 order (<c>Armor.as:321-329</c>): resistance first, then the acid/pink
        /// multipliers — so a <c>resist</c> of <c>-0.5</c> (pink, <c>tip == 1</c>) <i>increases</i>
        /// wear before the <c>×3</c> lands on top of it.</para>
        /// </summary>
        /// <param name="type">Damage type of the hit.</param>
        /// <param name="damage">Incoming damage, already scaled by the wearer's armour vulnerability.</param>
        /// <param name="resist">
        /// The item's own per-type resistance, AS3 <c>resist[type]</c>. Positive reduces wear.
        /// </param>
        /// <param name="indestructible">AS3 <c>und</c> — the plate takes no wear at all.</param>
        public static float ItemIntegrityDamage(
            DamageType type,
            float damage,
            float resist = 0f,
            bool indestructible = false)
        {
            if (indestructible || damage <= 0f)
                return 0f;

            if (!WearsEquippedItem(type))
                return 0f;

            float wear = damage * (1f - resist);

            if (type == DamageType.Acid)
                wear *= 2f;
            if (type == DamageType.Pink)
                wear *= 3f;

            return wear;
        }

        /// <summary>
        /// Integrity removed from a <b>unit pool</b> by one hit.
        ///
        /// <para>AS3 order (<c>Unit.as:3580-3596</c>): subtract the spell shield, <i>divide</i> by
        /// the weapon's armour multiplier, then apply acid/explosive. Note the division — it is not
        /// a typo. <c>armorMult</c> scales how <i>effective</i> the armour is, so a weapon the armour
        /// is strong against both reduces the armour's protection less and wears it down slower.</para>
        /// </summary>
        /// <param name="type">Damage type of the hit.</param>
        /// <param name="damage">Incoming damage after the vulnerability and attacker-bonus stages.</param>
        /// <param name="armourMultiplier">AS3 <c>bullet.armorMult</c>. Only values above 1 divide.</param>
        /// <param name="spellShieldAbsorb">
        /// AS3 <c>shitArmor</c>, subtracted before the multipliers. Zero when no shield is up.
        /// <para>The subtraction also carries the oracle's gate at <c>:3578</c>
        /// (<c>shithp &lt;= 0 || param1 &gt; shitArmor</c>): when the hit is no larger than the rating,
        /// <c>wear</c> goes non-positive here and the clamp below returns <c>0</c>, which is the same
        /// answer the skipped block would have produced. Callers that want to ask the question
        /// directly rather than rely on the clamp have
        /// <c>SpellShield.PermitsArmourPoolWear</c>.</para>
        /// </param>
        public static float PoolIntegrityDamage(
            DamageType type,
            float damage,
            float armourMultiplier = 1f,
            float spellShieldAbsorb = 0f)
        {
            if (damage <= 0f)
                return 0f;

            if (!WearsUnitPool(type))
                return 0f;

            float wear = damage;

            if (spellShieldAbsorb > 0f)
                wear -= spellShieldAbsorb;

            if (armourMultiplier > 1f)
                wear /= armourMultiplier;

            if (type == DamageType.Acid)
                wear *= 4f;
            else if (type == DamageType.Explosive)
                wear *= 2f;

            return wear > 0f ? wear : 0f;
        }
    }
}
