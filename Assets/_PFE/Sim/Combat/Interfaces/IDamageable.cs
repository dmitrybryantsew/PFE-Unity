using UnityEngine;
using PFE.Data.Definitions;
using PFE.Entities.Units;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// Interface for entities that can take damage.
    /// Decouples damage sources from specific unit implementations.
    ///
    /// <para><b>Two entry points, on purpose.</b> <see cref="TakeDamage(float)"/> is the plain path —
    /// no armour, no crit, used by test doubles and by anything that has already done the maths.
    /// <see cref="ApplyDamage"/> takes a <see cref="DamageOutcome"/> that a resolver has already
    /// computed, and writes it down. <b>The target does not compute anything</b>: that keeps the
    /// formula pure and testable in one place, and keeps this interface free of any dependency on
    /// weapons.</para>
    ///
    /// <para><b>Why the resolver needs <see cref="Armour"/>.</b> The formula reads the target's armour
    /// projection, so it must be readable. But the target's stats must never cross a network boundary —
    /// so the resolver <i>reads</i> them locally and sends only the resulting
    /// <see cref="DamageOutcome"/>, which is a pure value type.</para>
    ///
    /// Design rationale:
    /// - Damage sources don't need to know about UnitController vs PlayerController
    /// - Enables future damageable objects (crates, doors, turrets) without modifying the source
    /// - Clean separation: the source reports a hit, the resolver computes, the target applies
    /// </summary>
    public interface IDamageable
    {
        /// <summary>
        /// Apply damage directly, bypassing armour and every other modifier.
        /// </summary>
        /// <param name="damage">Amount of damage to apply.</param>
        void TakeDamage(float damage);

        /// <summary>
        /// Apply an already-resolved outcome: armour integrity first, then health.
        /// </summary>
        /// <param name="outcome">The result of resolving a hit. The target only writes it down.</param>
        /// <returns><c>true</c> if this hit broke the armour.</returns>
        bool ApplyDamage(in DamageOutcome outcome);

        /// <summary>
        /// The target's armour projection, read by the resolver to compute an outcome.
        /// <see cref="ArmourState.None"/> when nothing is equipped.
        /// </summary>
        ArmourState Armour { get; }

        /// <summary>
        /// The target's vulnerability table — one multiplier per damage type — read by the resolver.
        /// <see cref="VulnerabilityData.Neutral"/> when the target has none.
        /// </summary>
        /// <remarks>
        /// <para>AS3 <c>Unit.vulner</c> (<c>Unit.as:72</c>), applied to every hit at
        /// <c>Unit.damage():3527-3530</c>. It sits on this interface for the same reason
        /// <see cref="Armour"/> does: the formula reads it, so it must be readable, but it must never
        /// cross a network boundary — the resolver reads it locally and sends only the resulting
        /// <see cref="DamageOutcome"/>.</para>
        ///
        /// <para><b><see cref="VulnerabilityData.Neutral"/>, not
        /// <see cref="VulnerabilityData.Unmodified"/>, is the right fallback.</b> AS3 fills the array
        /// with <c>1</c> and then unconditionally forces <c>vulner[D_EMP] = 0</c> at construction
        /// (<c>:583-590</c>), so a unit that declares no <c>&lt;vulner&gt;</c> element is
        /// <b>EMP-immune</b> rather than unmodified. Substituting the identity here would hand out EMP
        /// damage the oracle denies. (Note the two constants differ by exactly that one slot — which is
        /// why they are separate names and not one default.)</para>
        ///
        /// <para><b>Inherited, not resolved through <c>parent=</c>.</b> The importer slices the unit's
        /// own text, so an element declared only on an ancestor is not seen; AS3 does inherit. That is a
        /// recorded divergence on <c>UnitVulnerabilityParser</c>, not something this property can
        /// fix.</para>
        /// </remarks>
        VulnerabilityData Vulnerabilities { get; }

        /// <summary>
        /// The target's natural resistance — AS3 <c>Unit.skin</c>, the <b>always-on floor</b> of the
        /// reduction block. <c>0</c> when the target has none, which is AS3's own default.
        /// </summary>
        /// <remarks>
        /// <para><b>Where the oracle puts it.</b> <c>Unit.damage():3611-3632</c> has two branches and
        /// each one opens by assigning the skin <i>before</i> the probabilistic armour roll:
        /// <c>_loc8_ = this.skin</c> at <c>:3615</c> (bullet, blade, explosion, physics, fang, acid)
        /// and again at <c>:3623</c> (fire, laser, plasma, spark, cryo, astro). So unlike the armour
        /// pool it is not a chance and not a pool — it is a flat subtraction on <b>every</b> hit of a
        /// type that reaches a branch. A type in neither branch gets no skin at all, which is the
        /// twelve types <c>ArmourWear.ChannelFor</c> maps to <c>ArmourChannel.None</c>.</para>
        ///
        /// <para><b>It sits on this interface for the same reason <see cref="Armour"/> and
        /// <see cref="Vulnerabilities"/> do</b>: the formula reads it, so it must be readable, and it
        /// must never cross a network boundary — the resolver reads it locally and sends only the
        /// resulting <see cref="DamageOutcome"/>.</para>
        ///
        /// <para><b>This member was added to close a one-way channel.</b>
        /// <c>DamageCalculator.ResolveDamage</c> has accepted and applied a <c>skinResistance</c>
        /// parameter, and <c>ArmourResolutionTests</c> exercises it, since the armour work landed — but
        /// the only production caller, <c>DamageSystem</c>, passed the literal <c>0f</c>, and nothing
        /// could have passed anything else because this read did not exist. The producer was likewise
        /// already written: <c>TrainingDummyController</c> sets <c>skinResistance = 20</c> on the
        /// <c>tr='1'</c> variant. The value was being computed and then dropped on the floor, which
        /// made the armoured training dummy indistinguishable from the plain one.</para>
        /// </remarks>
        float SkinResistance { get; }

        /// <summary>
        /// The target's evasion projection — dexterity, dexterity-plus and melee dodge. Read by
        /// <see cref="HitAvoidance.RollsHit"/> to decide whether a reported hit lands at all.
        /// </summary>
        /// <remarks>
        /// <para>AS3 <c>Unit.dexter</c>/<c>dexterPlus</c>/<c>dodge</c> (<c>Unit.as:166-172</c>), all
        /// three read together at <c>Unit.udarBullet():4072</c>. It sits on this interface for the same
        /// reason <see cref="Armour"/>, <see cref="Vulnerabilities"/> and <see cref="SkinResistance"/>
        /// do: the formula reads it, so it must be readable, and it must never cross a network
        /// boundary — the check is resolved locally and only its boolean result matters.</para>
        ///
        /// <para><b><see cref="EvasionState.Default"/>, not all-zeroes, is the right fallback.</b> A
        /// zero dexterity is not "no data" in the oracle — it is the <c>dexter &lt;= 0</c> term, which
        /// means the target is hit by everything. AS3's field default is <c>dexter = 1</c>, so a target
        /// the port knows nothing about must answer 1, or every hit on it would bypass evasion rather
        /// than merely be unmodified by it.</para>
        /// </remarks>
        EvasionState Evasion { get; }

        /// <summary>
        /// How susceptible this target is to being thrown — AS3 <c>Unit.knocked</c>.
        /// </summary>
        /// <remarks>
        /// <para>AS3 <c>Unit.as:234</c> (default <c>1</c>), authored per unit on the <c>&lt;move&gt;</c>
        /// node and read at <c>:1082-1085</c>. It is the numerator of the factor
        /// <see cref="KnockbackMath"/> applies, so <c>0</c> is the oracle's "cannot be moved" — turrets,
        /// <c>fixed</c> units and <c>UnitBossNecr</c>'s shadow all carry it — while <c>1.5</c> is a light
        /// body that flies further.</para>
        ///
        /// <para>Read by the resolver rather than computed by the target, for the same reason
        /// <see cref="Armour"/> and <see cref="Evasion"/> are: the formula lives in one place. It is
        /// also why this is not folded together with <see cref="Mass"/> into a single "resistance"
        /// number — AS3 clamps the <i>product</i>, not either input, so the two have to stay
        /// separate.</para>
        /// </remarks>
        float Knocked { get; }

        /// <summary>
        /// The target's weight — AS3 <c>Unit.massa</c>, already divided by 50 as AS3 does.
        /// </summary>
        /// <remarks>
        /// AS3 <c>Unit.as:1058</c> assigns <c>massa = massaFix</c>, which <c>:1051</c> sets to
        /// <c>@massafix / 50</c> (or <c>@massa / 50</c>, <c>:1047</c>) — so this is a small number
        /// around 1, not the raw attribute. <see cref="PFE.Data.Definitions.UnitDefinition.Massa"/> owns
        /// that conversion. A non-positive answer is treated as AS3's default of 1 by
        /// <see cref="KnockbackMath"/>.
        /// </remarks>
        float Mass { get; }

        /// <summary>
        /// Whether this target is currently immune to being hit at all — AS3 <c>Unit.invulner</c>.
        /// </summary>
        /// <remarks>
        /// <para>AS3 <c>Unit.as:144</c>. Read by <c>Unit.otbros():4245-4248</c>, which returns
        /// <b>before</b> taking its random draw — so an invulnerable target consumes no roll from the
        /// combat stream, and a caller that tested this <i>after</i> calling
        /// <see cref="KnockbackMath.Roll"/> would have desynchronised every later roll in the tick.</para>
        ///
        /// <para><b>Only the authored flag is modelled.</b> AS3 also toggles <c>invulner</c> at runtime —
        /// <c>UnitBossNecr</c> raises it during its shadow phase (<c>:609</c>/<c>:722</c>) — and the port
        /// has no live equivalent yet, so a scripted phase change is not visible here.</para>
        /// </remarks>
        bool IsInvulnerable { get; }

        /// <summary>
        /// Add an impulse to this target's movement — AS3's <c>dx += …; dy += …</c> in
        /// <c>Unit.otbros()</c>.
        /// </summary>
        /// <remarks>
        /// A plain addition to velocity, not a physics force: AS3 adds to the unit's own <c>dx</c>/<c>dy</c>
        /// accumulators and lets the unit integrate them. The resolver computes the vector — including
        /// the <c>knocked / massa</c> scale and the <c>3</c> clamp — and the target only writes it down,
        /// keeping this interface free of the formula exactly as the damage path is.
        /// </remarks>
        void ApplyKnockback(Vector2 impulse);

        /// <summary>
        /// Whether this target is one of AS3's <c>doop</c> units — the non-living classes that set
        /// <c>doop = true</c> in their constructors (<c>Unit.as:436</c>). Suppresses the stealth
        /// crit (<c>Unit.damage():3659</c>).
        /// </summary>
        /// <remarks>
        /// <para>AS3 declares <c>doop</c> on the <b>target</b> and reads it inside <c>damage()</c>, so
        /// it belongs on this interface by the same rule as <see cref="Armour"/>,
        /// <see cref="SkinResistance"/> and <see cref="IsInvulnerable"/>: the formula reads it, so it
        /// must be readable, and it never crosses a network boundary — the resolver reads it locally
        /// and sends only the resulting <see cref="DamageOutcome"/>.</para>
        ///
        /// <para><b><c>false</c> is the right fallback.</b> AS3's field default is <c>false</c>
        /// (<c>Unit.as:436</c>), and the flag is set only by a hand-picked set of non-living classes —
        /// so a unit that declares nothing is living, and living units take stealth crits. Answering
        /// <c>true</c> here would silently disable the mechanic for every unit in the game.</para>
        ///
        /// <para><b>Not yet fed from data.</b> AS3 sets <c>doop</c> per <i>class</i>, from the unit
        /// node's <c>@cl</c>, which the port's importer does not read — so the production
        /// implementation currently answers <c>false</c> for every unit. That is a recorded divergence
        /// covering 14 AllData rows; see <c>UnitStats.isNonLiving</c> for the list and the plan.</para>
        /// </remarks>
        bool IsNonLiving { get; }

        /// <summary>
        /// Whether the target is resting on solid ground this tick — AS3 <c>Unit.stay</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>The <c>D_SPARK</c> term of <c>Unit.damage()</c> reads it.</b> AS3
        /// <c>:3567-3573</c>:</para>
        /// <code>
        /// if(param2 == D_SPARK)
        /// {
        ///    if(!stay &amp;&amp; !this.inWater &amp;&amp; this.isLaz == 0)
        ///    {
        ///       param1 *= 0.5;
        ///    }
        /// }
        /// </code>
        /// <para>— an electrical hit does <b>half</b> damage to a target that is off the ground, and
        /// full damage to one standing on it. That is the whole of the reported "the lightning hurt more
        /// when I was on the ground".</para>
        ///
        /// <para><c>stay</c> is the collision-resolution result — <c>true</c> at <c>Unit.as:2382</c> and
        /// <c>:2429</c> when the unit comes to rest on a solid or shelf tile, <c>false</c> at
        /// <c>:2248</c>/<c>:2408</c> — so <c>UnitController.IsGrounded</c> is the port's equivalent and
        /// already answers it.</para>
        ///
        /// <para><b><c>true</c> is the safe fallback</b> for a target that cannot answer (a mine, a test
        /// double): the oracle's rule only ever <i>halves</i>, so answering "grounded" leaves the damage
        /// unmodified rather than inventing a penalty.</para>
        /// </remarks>
        bool IsGrounded { get; }

        /// <summary>
        /// Whether the target is standing in water — AS3 <c>Unit.inWater</c>, the second term of the
        /// <c>D_SPARK</c> rule (see <see cref="IsGrounded"/>). Water grounds a spark the same way the
        /// floor does, so an electrified target standing in it takes full damage even mid-jump.
        /// </summary>
        /// <remarks>
        /// <c>false</c> is the right fallback: a target with no tile grid is not in water, and answering
        /// <c>true</c> would silently remove the airborne half-damage from every unit in the game.
        /// </remarks>
        bool IsInWater { get; }

        /// <summary>
        /// The target's global damage-vulnerability multiplier — AS3 <c>Unit.allVulnerMult</c>
        /// (<c>Unit.as:146</c>, default <c>1</c>), applied to every hit at <c>Unit.damage():3681</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>This member exists because the port computed the value and never read it.</b>
        /// <c>CharacterStats.allVulnerMult</c> is maintained by the whole skill/effect pipeline
        /// (<c>defense</c>, <c>survival</c>, <c>trauma_blood</c>, a dozen consumables), and
        /// <c>BloodSprayRules.cs:46</c> already recorded the gap in as many words: "the port's damage
        /// path does not apply <c>allVulnerMult</c> at all (<c>CharacterStats.allVulnerMult</c> exists but
        /// no damage-path consumer reads it)". This is the consumer.</para>
        ///
        /// <para><b>It is also what makes the spell shield mean anything.</b> AS3's shield is not only an
        /// extra HP pool — <c>UnitAlicorn.as:586-593</c> sets
        /// <c>allVulnerMult = shithp &gt; 0 ? 0.6 : 1</c> (<c>0.4</c> for tr3), so a shielded alicorn
        /// takes 60% (or 40%) of every hit <i>on top of</i> the pool. Without this term the port's shield
        /// absorbed hits but never blunted them.</para>
        ///
        /// <para><c>1</c> is the identity and AS3's own field default, so a target that cannot answer is
        /// unmodified — which is what every unit in the game was before this landed.</para>
        /// </remarks>
        float AllVulnerabilityMultiplier { get; }

        /// <summary>
        /// The target's spell/boss shield pool — AS3 <c>Unit.shithp</c> (<c>Unit.as:134</c>, default
        /// <b>0</b>). Positive means a shield is up.
        /// </summary>
        /// <remarks>
        /// <para><b>Two statements in <c>Unit.damage()</c> read it, and they are different statements
        /// in different blocks.</b> The armour-pool gate at <c>:3578</c> refuses to wear the plate at
        /// all while a shield is up and the hit is no larger than the rating, and the reduction block
        /// at <c>:3629-3637</c> spends the pool and adds <c>shitArmor</c> to the reduction:</para>
        /// <code>
        /// if(this.shithp > 0)
        /// {
        ///    this.shithp -= param1;
        ///    if(this.shithp &lt; 0) this.shithp = 0;
        ///    _loc8_ += this.shitArmor;
        /// }
        /// </code>
        /// <para>Both are resolved by <see cref="PFE.Systems.Combat.DamageCalculator.ResolveDamage"/>,
        /// which returns the post-hit pool on <see cref="DamageOutcome.ShieldHpAfter"/>; the target only
        /// writes it down. It is a read here for the same reason <see cref="Armour"/> and
        /// <see cref="SkinResistance"/> are — the formula reads it, so it must be readable, and it
        /// never crosses a network boundary.</para>
        ///
        /// <para><b><c>0</c> is the right fallback</b> and is AS3's own field default: no shield. It is
        /// also the safe answer — <see cref="PFE.Systems.Combat.SpellShield.ArmourRating"/> then
        /// contributes nothing and the pool gate opens, which is exactly the unshielded behaviour every
        /// unit in the game had before this landed.</para>
        /// </remarks>
        float ShieldHp { get; }

        /// <summary>
        /// The shield's flat rating — AS3 <c>Unit.shitArmor</c> (<c>Unit.as:160</c>, default <b>20</b>).
        /// Read only while <see cref="ShieldHp"/> is positive.
        /// </summary>
        /// <remarks>
        /// The value is per-unit and is not a constant: the alicorn sets <c>25</c> (<c>50</c> on tr3 —
        /// <c>UnitAlicorn.as:152/172</c>), and <c>UnitTurret.as:507</c> sets it to <c>0</c> while its
        /// shield is up, so a rating of 0 with a live shield is a real state rather than a missing
        /// value. <c>20</c> is AS3's field default and what <c>Pers.defaultParams()</c> restores
        /// (<c>Pers.as:882</c>).
        /// </remarks>
        float ShieldArmor { get; }

        /// <summary>
        /// Current health of this entity.
        /// Used for UI, death checks, and damage calculations.
        /// </summary>
        float CurrentHealth { get; }

        /// <summary>
        /// Maximum health of this entity.
        /// Used for health percentage calculations.
        /// </summary>
        float MaxHealth { get; }

        /// <summary>
        /// Whether this entity is alive (CurrentHealth > 0).
        /// Projectiles should not damage dead entities.
        /// </summary>
        bool IsAlive { get; }
    }
}
