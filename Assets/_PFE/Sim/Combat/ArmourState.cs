using UnityEngine;
using PFE.Data.Definitions;
using PFE.Systems.Combat;

namespace PFE.Entities.Units
{
    /// <summary>
    /// Which of AS3's two armour models a projection came from. They share the reduction rules but
    /// <b>not</b> the wear rules — acid is <c>×4</c> on the pool and <c>×2</c> on the item, and pink
    /// wears the item <c>×3</c> and the pool not at all — so a projection that does not say which it is
    /// cannot be worn correctly. See <see cref="ArmourWear"/>.
    /// </summary>
    public enum ArmourModel
    {
        /// <summary>No armour projected.</summary>
        None = 0,

        /// <summary>The player's equipped plate — <c>Armor.damage()</c> (<c>Armor.as:313-347</c>).</summary>
        EquippedItem = 1,

        /// <summary>An NPC's own pool — <c>Unit.damage()</c> (<c>Unit.as:3578-3603</c>).</summary>
        UnitPool = 2,
    }

    /// <summary>
    /// The combat projection of one piece of equipped armour.
    ///
    /// <para><b>AS3 oracle.</b> This mirrors the three fields <c>Armor.setArmor()</c> writes onto its
    /// owner — <c>owner.armor</c>, <c>owner.marmor</c>, <c>owner.armor_qual</c> — plus the
    /// <c>hp</c>/<c>maxhp</c> that drive them (<c>fe/unit/Armor.as:297-311</c>):</para>
    ///
    /// <code>
    /// _loc1_ = 1;
    /// if (hp &lt; maxhp / 2) _loc1_ = 0.5 + hp / maxhp;
    /// owner.armor      = armor      * _loc1_;
    /// owner.marmor     = marmor     * _loc1_;
    /// owner.armor_qual = armor_qual * _loc1_;
    /// </code>
    ///
    /// <para><b>Condition degrades the ratings, but only in the bottom half.</b> Armour gives its full
    /// value down to half integrity, then falls linearly to <b>half</b> value at zero. It never
    /// reaches zero by condition — it stops by breaking, which unequips it
    /// (<c>Armor.damage()</c> → <c>changeArmor("off")</c>, <c>Armor.as:334</c>).</para>
    ///
    /// <para><b>The factor is applied POST-hit.</b> <c>Armor.damage()</c> wears the plate and re-projects
    /// at the end of that wear (<c>Armor.as:337</c>), and its caller runs before the reduction block
    /// (<c>UnitPlayer.as:3297</c> vs <c>:3324</c>) — so the reduction for a hit reads the condition
    /// <i>after</i> that hit's own wear. Crossing the half-way line degrades the very hit that crossed
    /// it. <see cref="ConditionFactor"/> therefore describes the projection at the integrity you hand
    /// it, which is why the resolver calls <see cref="ConditionFactorOf"/> with the post-hit value.</para>
    ///
    /// <para><b>Reliability is a probability, not a rating.</b> It is consumed as
    /// <c>isrnd(armor_qual)</c>, where <c>isrnd(n)</c> is literally <c>Math.random() &lt; n</c>
    /// (<c>Unit.as:4855</c>). So it is the chance the armour's flat rating applies to a given hit.</para>
    ///
    /// <para><b>The two models read it from different attributes — do not cross them.</b> The
    /// equipped item parses <c>&lt;upd … qual='…'&gt;</c> (<c>Armor.as:205-207</c>), while the unit pool
    /// parses <c>@aqual</c> on the unit node (<c>Unit.as:1154-1156</c>). An earlier revision of this
    /// comment said <c>@aqual</c> for both; that is the pool's attribute and would have made an
    /// importer read the wrong field for every player armour. Values run <b>0.25–0.9</b> in
    /// <c>AllData.as</c>, and note that AS3's <i>field</i> default is <c>0</c> — i.e. a plate with no
    /// <c>qual</c> attribute applies its rating never, not always.</para>
    ///
    /// <para><b>Unequipping clears the projection, in AS3 too.</b> The port clears it in
    /// <c>UnitStats.UnequipArmour</c>; AS3 reaches the same end by a different route, because
    /// <c>changeArmor("off")</c> runs <c>Pers.setParameters()</c>, which zeroes <c>gg.armor</c> and
    /// <c>gg.marmor</c> (<c>Pers.as:876-877</c>). An earlier revision of this comment called that a
    /// deliberate divergence and claimed AS3 kept a stale bonus — it does not.</para>
    ///
    /// <para>Mutable by design: the owner depletes it. Pass it <c>in</c> (read-only) to anything that
    /// only needs to read it — the damage formula must not mutate it.</para>
    /// </summary>
    public struct ArmourState
    {
        /// <summary>Armour hit points. AS3 <c>Armor.maxhp</c>.</summary>
        public float maxIntegrity;

        /// <summary>Current armour hit points. AS3 <c>Armor.hp</c>.</summary>
        public float integrity;

        /// <summary>Flat reduction against physical damage. AS3 <c>Armor.armor</c>.</summary>
        public float physicalRating;

        /// <summary>Flat reduction against energy damage. AS3 <c>Armor.marmor</c>.</summary>
        public float energyRating;

        /// <summary>
        /// Probability (0..1) that the flat rating applies to a hit. AS3 <c>Armor.armor_qual</c>,
        /// parsed from the item's <c>&lt;upd qual='…'&gt;</c> — <b>not</b> from <c>@aqual</c>, which is
        /// the unit pool's attribute. Not a magnitude — see the class remarks.
        /// </summary>
        public float reliability;

        /// <summary>
        /// Which AS3 armour model this projection came from. Decides the wear table, which the two
        /// models do not share — see <see cref="ArmourModel"/>.
        /// </summary>
        public ArmourModel model;

        /// <summary>
        /// The item's per-type resistance. AS3 <c>Armor.resist</c>, consumed by
        /// <c>Armor.damage():321</c> as <c>1 - resist[type]</c> on the <b>wear</b>. Neutral is 0, so
        /// an all-zero table is "no resistance" and not "indestructible".
        ///
        /// <para>Only meaningful for <see cref="ArmourModel.EquippedItem"/>; a unit pool has no
        /// per-type resist array in AS3 (<c>Unit.damage()</c> never reads one).</para>
        /// </summary>
        public ResistTable resists;

        /// <summary>
        /// AS3 <c>Armor.und</c> — indestructible armour takes no wear at all
        /// (<c>Armor.damage():315-318</c>). Item model only.
        /// </summary>
        public bool indestructible;

        /// <summary>No armour. Also the value that clears a projection.</summary>
        public static readonly ArmourState None = default;

        /// <summary>
        /// Whether a piece of armour is projected at all. AS3's <c>setArmor()</c> guard is
        /// <c>owner &amp;&amp; active</c>; here, "has any capacity" stands in for "is equipped".
        /// </summary>
        public bool IsEquipped => maxIntegrity > 0f;

        /// <summary>Armour at zero integrity. AS3 zeroes <c>armor_qual</c> and unequips at this point.</summary>
        public bool IsBroken => IsEquipped && integrity <= 0f;

        /// <summary>
        /// AS3 <c>Armor.setArmor()</c>'s <c>_loc1_</c>: <c>1</c> at or above half integrity, otherwise
        /// <c>0.5 + integrity / maxIntegrity</c> — i.e. linear from 1.0 down to 0.5 as integrity falls
        /// from half to zero. Continuous at the midpoint.
        /// </summary>
        /// <remarks>
        /// The formula is exposed statically because the resolver needs it for a <b>post-hit</b>
        /// integrity it has not written back yet (<c>DamageCalculator.ResolveDamage</c>), so there must
        /// be one definition rather than two that can drift.
        /// </remarks>
        public static float ConditionFactorOf(float integrity, float maxIntegrity)
        {
            if (maxIntegrity <= 0f) return 1f;
            if (integrity >= maxIntegrity * 0.5f) return 1f;
            return 0.5f + integrity / maxIntegrity;
        }

        /// <summary>This projection's condition factor, from its current integrity.</summary>
        public float ConditionFactor
            => IsEquipped ? ConditionFactorOf(integrity, maxIntegrity) : 1f;

        /// <summary>Physical rating after the condition factor. AS3 <c>owner.armor</c>.</summary>
        public float EffectivePhysicalRating => physicalRating * ConditionFactor;

        /// <summary>Energy rating after the condition factor. AS3 <c>owner.marmor</c>.</summary>
        public float EffectiveEnergyRating => energyRating * ConditionFactor;

        /// <summary>
        /// Reliability after the condition factor. AS3 <c>owner.armor_qual</c> — scaled by the same
        /// factor, so a battered plate also applies its rating less often.
        /// </summary>
        public float EffectiveReliability => reliability * ConditionFactor;

        /// <summary>Integrity as a fraction of maximum, for the HUD bar. 0 when no armour is equipped.</summary>
        public float IntegrityPercent => maxIntegrity > 0f ? Mathf.Clamp01(integrity / maxIntegrity) : 0f;

        /// <summary>
        /// Build a projection from an armour item's numbers.
        /// </summary>
        /// <param name="integrity">The item's current hit points.</param>
        /// <param name="maxIntegrity">The item's maximum hit points. Must be &gt; 0 for the state to be "equipped".</param>
        /// <param name="physicalRating">Flat reduction vs physical damage.</param>
        /// <param name="energyRating">Flat reduction vs energy damage.</param>
        /// <param name="reliability">Probability the rating applies, 0..1.</param>
        /// <param name="resists">
        /// The item's per-type resistance. Neutral is 0 — see <see cref="ResistTable"/>.
        /// </param>
        /// <param name="indestructible">AS3 <c>und</c> — the plate takes no wear at all.</param>
        /// <param name="bodyArmour">
        /// True when the item's AS3 <c>tip == 1</c>. This is <b>not</b> cosmetic: the AS3 constructor
        /// forces <c>resist[D_PINK] = -0.5</c> on every body armour (<c>Armor.as:180-183</c>), which
        /// <c>Armor.damage():321</c> turns into <c>×1.5</c> wear before pink's own <c>×3</c> — a
        /// <b>×4.5</b> total. Applying it here rather than in the importer is deliberate: it is a
        /// constructor rule, not data, so it has to hold for hand-authored armour too.
        /// </param>
        public static ArmourState FromItem(
            float integrity,
            float maxIntegrity,
            float physicalRating,
            float energyRating,
            float reliability,
            ResistTable resists = default,
            bool indestructible = false,
            bool bodyArmour = false)
        {
            // Armor.as:180-183 — the array is zeroed and then, for tip == 1 only, pink is set to -0.5.
            // AS3 has no @pink attribute, so this assignment is unconditional, not a default.
            if (bodyArmour)
                resists.pink = -0.5f;

            return new ArmourState
            {
                maxIntegrity = Mathf.Max(0f, maxIntegrity),
                integrity = Mathf.Clamp(integrity, 0f, Mathf.Max(0f, maxIntegrity)),
                physicalRating = Mathf.Max(0f, physicalRating),
                energyRating = Mathf.Max(0f, energyRating),
                reliability = Mathf.Clamp01(reliability),
                resists = resists,
                indestructible = indestructible,
                model = ArmourModel.EquippedItem,
            };
        }

        /// <summary>
        /// Build a projection for an NPC's own armour pool — the <c>Unit.damage()</c> model.
        /// Its ratings do <b>not</b> degrade with condition, because AS3 never runs anything like
        /// <c>setArmor()</c> for a unit pool: <c>armor_qual</c> stays at its XML value until the pool
        /// empties and is zeroed in place (<c>Unit.as:3601</c>).
        /// </summary>
        public static ArmourState FromUnitPool(
            float integrity,
            float maxIntegrity,
            float physicalRating,
            float energyRating,
            float reliability)
        {
            ArmourState state = FromItem(integrity, maxIntegrity, physicalRating, energyRating, reliability);
            state.model = ArmourModel.UnitPool;
            return state;
        }

        /// <summary>
        /// Integrity this hit removes, using the wear table for <b>this</b> projection's model.
        ///
        /// <para>The two tables disagree on purpose (acid <c>×4</c> pool / <c>×2</c> item, pink
        /// <c>×3</c> item / <c>×0</c> pool), which is why the model travels with the projection rather
        /// than being chosen by the caller.</para>
        /// </summary>
        /// <param name="type">Damage type of the hit.</param>
        /// <param name="damage">Incoming damage before reduction.</param>
        /// <param name="armourMultiplier">The weapon's <c>armorMult</c> (pool model only).</param>
        /// <param name="spellShieldAbsorb">
        /// The spell/boss shield's rating — AS3 <c>shitArmor</c>, read only while <c>shithp &gt; 0</c>
        /// (<c>Unit.as:3581-3584</c>). Subtracted from the wear <b>before</b> the multipliers, and
        /// because the subtraction comes first it also reproduces the oracle's gate at <c>:3578</c>: a
        /// hit no larger than the rating leaves nothing to wear. Zero when no shield is up.
        /// <para><b>Pool model only.</b> The player's equipped armour has no shield term in the oracle
        /// — <c>:3578</c> is guarded on <c>!this.player</c> — so the argument is ignored for
        /// <see cref="ArmourModel.EquippedItem"/>.</para>
        /// </param>
        /// <returns>Integrity to remove. 0 when nothing is equipped.</returns>
        /// <remarks>
        /// <b>The resist is read from <see cref="resists"/>, not passed in.</b> An earlier signature
        /// took <c>resist</c> with a default of <c>0</c>, which meant a caller who had the data and
        /// forgot to pass it silently got <i>no</i> resistance — the wear numbers would look plausible
        /// and be wrong. Since the projection now carries the table, there is nothing left for a
        /// caller to forget.
        /// </remarks>
        public float WearFrom(
            DamageType type,
            float damage,
            float armourMultiplier = 1f,
            float spellShieldAbsorb = 0f)
        {
            if (!IsEquipped || damage <= 0f)
                return 0f;

            switch (model)
            {
                case ArmourModel.UnitPool:
                    return ArmourWear.PoolIntegrityDamage(type, damage, armourMultiplier, spellShieldAbsorb);
                case ArmourModel.EquippedItem:
                    return ArmourWear.ItemIntegrityDamage(
                        type, damage, resists.GetResist(type), indestructible);
                default:
                    return 0f;
            }
        }

        /// <summary>
        /// Deplete integrity by an already-scaled amount.
        ///
        /// <para>The caller applies any per-damage-type multiplier <b>before</b> calling, so this stays
        /// type-agnostic and the multipliers stay data. AS3 has two different sets — the unit pool uses
        /// acid <c>×4</c> / explosion <c>×2</c> (<c>Unit.as:3589-3596</c>), the equipped item uses acid
        /// <c>×2</c> / pink <c>×3</c> (<c>Armor.as:322-329</c>). Both are in <c>ArmourWear</c>; note they
        /// <b>disagree</b>, so the multiplier belongs to the (type, model) pair, not to the type.</para>
        /// </summary>
        /// <returns><c>true</c> if this hit is the one that broke the armour.</returns>
        public bool TakeIntegrityDamage(float amount)
        {
            if (!IsEquipped || amount <= 0f) return false;

            bool wasIntact = integrity > 0f;
            integrity = Mathf.Max(0f, integrity - amount);
            return wasIntact && integrity <= 0f;
        }

        /// <summary>
        /// Restore integrity. AS3 <c>Armor.repair()</c> (<c>Armor.as:349-355</c>), which also re-projects
        /// via <c>setArmor()</c> — here that happens automatically, because the effective ratings are
        /// derived from <see cref="integrity"/> rather than cached.
        /// </summary>
        public void Repair(float amount)
        {
            if (!IsEquipped || amount <= 0f) return;
            integrity = Mathf.Clamp(integrity + amount, 0f, maxIntegrity);
        }
    }
}
