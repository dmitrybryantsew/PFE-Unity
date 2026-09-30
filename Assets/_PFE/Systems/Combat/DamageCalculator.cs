using UnityEngine;
using PFE.Core.Rng;
using PFE.Data.Definitions;
using PFE.Entities.Units;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// Handles complete damage calculation workflow.
    /// Combines base damage, ammo multipliers, vulnerabilities, armor, and critical hits.
    /// Based on docs/task1_core_mechanics/02_combat_logic.md (lines 570-595)
    /// Now non-static to support dependency injection and testing.
    /// </summary>
    public class DamageCalculator : IDamageCalculator
    {
        private readonly ICombatCalculator _combatCalculator;

        public DamageCalculator(ICombatCalculator combatCalculator)
        {
            _combatCalculator = combatCalculator;
        }

        /// <summary>
        /// Complete damage calculation from weapon to target.
        /// Implements the full formula chain from documentation.
        /// </summary>
        /// <param name="weaponDef">Weapon definition</param>
        /// <param name="attackerStats">Attacker unit stats</param>
        /// <param name="targetStats">Target unit stats</param>
        /// <param name="ammoDef">Ammo definition (optional)</param>
        /// <param name="isBackstab">Backstab attack (2x damage)</param>
        /// <param name="absolutePierce">Absolute pierce roll (ignores all armor)</param>
        public DamageResult CalculateDamage(
            WeaponDefinition weaponDef,
            UnitStats attackerStats,
            UnitStats targetStats,
            AmmoDefinition ammoDef = null,
            bool isBackstab = false,
            bool absolutePierce = false)
        {
            DamageResult result = new DamageResult();

            // Step 1: Base damage calculation
            float baseDamage = CalculateBaseDamage(weaponDef, attackerStats);
            result.baseDamage = baseDamage;

            // Step 2: Apply ammo multiplier
            float withAmmo = ApplyAmmoMultiplier(baseDamage, ammoDef);
            result.damageAfterAmmo = withAmmo;

            // Step 3: Apply enemy vulnerability/resistance
            float withVulnerability = ApplyVulnerability(withAmmo, weaponDef, targetStats);
            result.damageAfterVulnerability = withVulnerability;

            // Step 4: Apply armor (or absolute pierce)
            float withArmor = ApplyArmor(withVulnerability, weaponDef, targetStats, absolutePierce);
            result.damageAfterArmor = withArmor;

            // Step 5: Apply critical hit
            float finalDamage = withArmor;
            bool isCrit = CheckCriticalHit(weaponDef, attackerStats);
            result.isCritical = isCrit;

            if (isCrit)
            {
                float critMultiplier = _combatCalculator.CalculateCriticalMultiplier(
                    weaponDef.critMultiplier,
                    attackerStats.critDamageBonus);
                finalDamage = withArmor * critMultiplier;
                result.criticalMultiplier = critMultiplier;

                // Apply backstab if applicable
                if (isBackstab)
                {
                    finalDamage *= 2f;
                    result.isBackstab = true;
                }
            }

            result.finalDamage = Mathf.Max(0, finalDamage);
            return result;
        }

        /// <summary>
        /// Step 1: Calculate base damage with all modifiers.
        /// Formula: (baseDamage + damAdd) * damMult * weaponSkill * durabilityPenalty
        /// </summary>
        private float CalculateBaseDamage(WeaponDefinition weaponDef, UnitStats attackerStats)
        {
            float breaking = _combatCalculator.CalculateBreaking(
                weaponDef.maxDurability,
                attackerStats.weaponCurrentDurability);

            float durabilityPenalty = _combatCalculator.CalculateDurabilityDamageMultiplier(breaking);
            float levelPenalty = _combatCalculator.CalculateLevelPenalty(
                weaponDef.weaponLevel,
                attackerStats.weaponSkillLevel);

            return _combatCalculator.CalculateBaseDamage(
                weaponDef.baseDamage,
                attackerStats.damageBonus,
                attackerStats.damageMultiplier,
                levelPenalty,
                1f, // skillPlusDam
                durabilityPenalty);
        }

        /// <summary>
        /// Step 2: Apply ammo damage multiplier.
        /// </summary>
        private float ApplyAmmoMultiplier(float damage, AmmoDefinition ammoDef)
        {
            if (ammoDef == null)
                return damage;

            return damage * ammoDef.damageMultiplier;
        }

        /// <summary>
        /// Step 3: Apply target vulnerability/resistance.
        /// Vulnerability < 1.0 = resistance (reduces damage)
        /// Vulnerability > 1.0 = weakness (increases damage)
        /// </summary>
        private float ApplyVulnerability(
            float damage,
            WeaponDefinition weaponDef,
            UnitStats targetStats)
        {
            // Get vulnerability based on damage type
            // For now, return damage unchanged (vulnerability system needs damage type lookup)
            return damage;
        }

        /// <summary>
        /// Step 4: Apply armor reduction.
        /// Formula: damage - max(0, armor * effectiveness - penetration)
        /// </summary>
        private float ApplyArmor(
            float damage,
            WeaponDefinition weaponDef,
            UnitStats targetStats,
            bool absolutePierce)
        {
            if (absolutePierce)
            {
                // Absolute pierce ignores all armor
                return damage;
            }

            float effectiveArmor = _combatCalculator.CalculateEffectiveArmor(
                targetStats.armour.EffectivePhysicalRating,
                targetStats.armorEffectiveness,
                weaponDef.armorPenetration);

            return _combatCalculator.ApplyArmor(damage, effectiveArmor);
        }

        /// <summary>
        /// Step 5: Check and apply critical hit.
        /// </summary>
        private bool CheckCriticalHit(WeaponDefinition weaponDef, UnitStats attackerStats)
        {
            float critChance = _combatCalculator.CalculateCriticalChance(
                weaponDef.critChance,
                attackerStats.critChanceBonus,
                attackerStats.critChanceBonusAdditional);

            return _combatCalculator.RollCriticalHit(critChance);
        }

        /// <summary>
        /// Simple damage calculation for testing.
        /// Direct formula without full stat system.
        /// </summary>
        public float CalculateDamageSimple(
            float baseDamage,
            float damAdd,
            float damMult,
            float weaponSkill,
            float durabilityMultiplier,
            float ammoMultiplier,
            float vulnerability,
            float armor,
            float armorEffectiveness,
            float penetration,
            float critChance,
            float critMultiplier,
            bool absolutePierce = false)
        {
            // Step 1: Base damage
            float damage = _combatCalculator.CalculateBaseDamage(
                baseDamage, damAdd, damMult, weaponSkill, 1f, durabilityMultiplier);

            // Step 2: Ammo
            damage *= ammoMultiplier;

            // Step 3: Vulnerability
            damage *= vulnerability;

            // Step 4: Armor
            if (!absolutePierce)
            {
                float effectiveArmor = _combatCalculator.CalculateEffectiveArmor(
                    armor, armorEffectiveness, penetration);
                damage = _combatCalculator.ApplyArmor(damage, effectiveArmor);
            }

            // Step 5: Critical
            bool isCrit = _combatCalculator.RollCriticalHit(critChance);
            if (isCrit)
            {
                damage *= critMultiplier;
            }

            return Mathf.Max(0, damage);
        }

        #region Armour Resolution (pure)

        /// <summary>
        /// Resolve one hit against a target's armour and durability.
        ///
        /// <para><b>Pure by contract.</b> It mutates nothing, holds no state, and takes the RNG as an
        /// argument — so the same seed and the same inputs always produce the same outcome. That is
        /// what makes it testable, deterministic, and safe to run on both host and client. The
        /// <paramref name="armour"/> is passed <c>in</c> and never written; the <b>owner</b> applies the
        /// returned <see cref="DamageOutcome"/> to its own state.</para>
        ///
        /// <para><b>AS3 oracle</b> — <c>Unit.damage()</c> (<c>Unit.as:3505-3678</c>) and
        /// <c>Armor.setArmor()</c> (<c>Armor.as:297-311</c>). The order of operations is:</para>
        /// <list type="number">
        ///   <item><description><b>Pool depletion.</b> The armour's integrity absorbs the hit. AS3's
        ///     pool block (<c>:3578-3603</c>) runs <b>before</b> the reduction and zeroes
        ///     <c>armor_qual</c> when the pool empties — so <b>the hit that breaks the armour gets no
        ///     reduction from it</b>. The equipped path reaches the same place by a different route:
        ///     <c>Armor.damage()</c> calls <c>changeArmor("off")</c> (<c>Armor.as:334</c>), which runs
        ///     <c>Pers.setParameters()</c> and zeroes <c>gg.armor</c>/<c>gg.marmor</c>
        ///     (<c>Pers.as:876-877</c>) before the reduction reads them. The
        ///     <paramref name="armourIntegrityDamage"/> is already scaled by any per-damage-type
        ///     multiplier; the caller owns that table.</description></item>
        ///   <item><description><b>Reliability roll.</b> AS3: <c>_loc8_ = skin;
        ///     if (armor_qual &gt; 0 &amp;&amp; isrnd(armor_qual)) _loc8_ += armor;</c>
        ///     (<c>:3613-3627</c>) — with <c>isrnd(n)</c> being <c>Math.random() &lt; n</c>
        ///     (<c>:4855</c>). So the flat rating applies with probability
        ///     <c>armor_qual</c>, not always. Energy types read <c>marmor</c> instead of
        ///     <c>armor</c>. Both <c>armor</c> and <c>armor_qual</c> are the <b>post-hit</b> projection
        ///     (see step 1) — the condition factor is taken after this hit's wear.</description></item>
        ///   <item><description><b>Effectiveness and pierce.</b> AS3: <c>_loc8_ *= armorMult;
        ///     _loc8_ -= pier;</c> (<c>:3638-3640</c>). Note the sign — <paramref name="armourMultiplier"/>
        ///     scales the <i>reduction</i>, so a higher value means the armour is <b>more</b>
        ///     effective.</description></item>
        ///   <item><description><b>Crit.</b> AS3: <c>if (Math.random() &lt; critCh) param1 *= critDamMult</c>
        ///     (<c>:3652-3656</c>).</description></item>
        ///   <item><description><b>Durability penalty.</b> The <c>breaking</c> term,
        ///     <c>1 - breaking * 0.3</c> — the weapon-side penalty that was missing from the live
        ///     path entirely.</description></item>
        /// </list>
        ///
        /// <para><b>Why the two paths agree here.</b> The unit pool and the player's equipped item
        /// look like they disagree about the breaking hit, but they do not — they just reach
        /// "no reduction" by different routes: the pool zeroes <c>armor_qual</c> in place, while the
        /// equipped item unequips, and the unequip path resets the projection to zero before the
        /// reduction reads it. So <b>the hit that breaks the armour gets no reduction</b> is the
        /// oracle's answer for both, and this method states it once.</para>
        /// </summary>
        /// <param name="incomingDamage">Damage before any armour, crit or durability term.</param>
        /// <param name="armourIntegrityDamage">
        /// How much integrity this hit removes, already scaled by the caller's per-type multiplier.
        /// AS3's unit pool uses acid <c>×4</c> / explosion <c>×2</c> (<c>:3588-3594</c>); the equipped
        /// item uses acid <c>×2</c> / pink <c>×3</c> (<c>Armor.as:326-334</c>).
        /// </param>
        /// <param name="armour">The target's armour projection. Read-only; never written.</param>
        /// <param name="rng">Seeded stream. The only randomness source — there is no static fallback.</param>
        /// <param name="damageType">
        /// Selects the reduction branch. <b>Not cosmetic:</b> ten of the port's twenty-one types
        /// (venom, poison, bleed, necrotic, pink, balefire, psionic, EMP, internal, friendly fire)
        /// match neither of AS3's two branches and so get <b>no reduction and no skin</b>. See
        /// <see cref="ArmourWear.ChannelFor"/>.
        /// </param>
        /// <param name="ignoreArmour">
        /// AS3's fourth parameter to <c>Unit.damage()</c>. DoT and environmental sources pass
        /// <c>true</c> to skip the reduction block entirely (<c>Effect.as:417-438</c>,
        /// <c>UnitPlayer.as:1375</c>).
        /// </param>
        /// <param name="piercing">Subtracted from the reduction (AS3 <c>pier</c>).</param>
        /// <param name="armourMultiplier">Scales the reduction (AS3 <c>armorMult</c>). 1 = unmodified.</param>
        /// <param name="critChance">Probability of a critical hit.</param>
        /// <param name="critMultiplier">Damage multiplier on a critical hit.</param>
        /// <param name="skinResistance">
        /// Natural resistance (AS3 <c>skin</c>). Applied <b>only</b> for the twelve types that reach
        /// a reduction branch — not universally, despite how it reads in the field name.
        /// </param>
        /// <param name="durabilityMultiplier">The weapon <c>breaking</c> term, <c>1 - breaking * 0.3</c>.</param>
        public DamageOutcome ResolveDamage(
            float incomingDamage,
            float armourIntegrityDamage,
            in ArmourState armour,
            IRngService rng,
            DamageType damageType = DamageType.PhysicalBullet,
            bool ignoreArmour = false,
            float piercing = 0f,
            float armourMultiplier = 1f,
            float critChance = 0f,
            float critMultiplier = 1f,
            float skinResistance = 0f,
            float durabilityMultiplier = 1f)
        {
            if (incomingDamage <= 0f)
                return DamageOutcome.None;

            // ── 1. Pool depletion ────────────────────────────────────────────────────────────────
            // Integrity is reduced first, and the hit that empties it gets no reduction — AS3 zeroes
            // `armor_qual` in the pool block, before the reduction block reads it.
            float integrityDamage = Mathf.Max(0f, armourIntegrityDamage);
            bool hasArmour = armour.IsEquipped && armour.integrity > 0f;
            float integrityAfter = hasArmour
                ? Mathf.Max(0f, armour.integrity - integrityDamage)
                : 0f;

            bool broke = hasArmour && integrityAfter <= 0f;
            bool stillUsable = hasArmour && integrityAfter > 0f;

            // ── 2. Reliability roll + 3. effectiveness/pierce ────────────────────────────────────
            // AS3's two `if`s have no `else`, and `_loc8_ = this.skin` sits inside each of them —
            // so a type in neither branch gets no skin either. `ignoreArmour` (AS3's param4) skips
            // the whole block.
            ArmourChannel channel = ignoreArmour
                ? ArmourChannel.None
                : ArmourWear.ChannelFor(damageType);

            float reduction = 0f;
            bool armourReduced = false;

            if (channel != ArmourChannel.None)
            {
                reduction = skinResistance;

                if (stillUsable)
                {
                    // Post-hit condition. AS3 wears the armour *before* the reduction and refreshes
                    // the owner's projection at the end of that wear — Armor.damage() -> setArmor()
                    // (Armor.as:337) is called from UnitPlayer.damage() at :3297, while the reduction
                    // block lives in super.damage() at :3324. So this hit already sees the armour it
                    // just dented: crossing the half-way mark degrades the very hit that crossed it.
                    float condition = ArmourState.ConditionFactorOf(integrityAfter, armour.maxIntegrity);
                    float reliability = armour.reliability * condition;

                    if (reliability > 0f && rng != null && rng.Chance(reliability))
                    {
                        float rating = channel == ArmourChannel.Energy
                            ? armour.energyRating
                            : armour.physicalRating;
                        reduction += rating * condition;
                        armourReduced = true;
                    }
                }
            }

            reduction = reduction * armourMultiplier - piercing;
            float damage = incomingDamage - Mathf.Max(0f, reduction);

            // ── 4. Crit ──────────────────────────────────────────────────────────────────────────
            bool isCrit = false;
            if (damage > 0f && critChance > 0f && rng != null && rng.Chance(critChance))
            {
                damage *= critMultiplier;
                isCrit = true;
            }

            // ── 5. Weapon durability ─────────────────────────────────────────────────────────────
            damage *= durabilityMultiplier;

            return new DamageOutcome(
                hpDamage: Mathf.Max(0f, damage),
                armourIntegrityDamage: hasArmour ? Mathf.Min(integrityDamage, armour.integrity) : 0f,
                armourBroke: broke,
                armourReduced: armourReduced,
                isCritical: isCrit);
        }

        #endregion
    }

    /// <summary>
    /// Result of a damage calculation.
    /// Provides breakdown of damage at each calculation step.
    /// </summary>
    public class DamageResult
    {
        /// <summary>Base damage from weapon calculation</summary>
        public float baseDamage;

        /// <summary>Damage after ammo multiplier</summary>
        public float damageAfterAmmo;

        /// <summary>Damage after vulnerability/resistance</summary>
        public float damageAfterVulnerability;

        /// <summary>Damage after armor reduction</summary>
        public float damageAfterArmor;

        /// <summary>Final damage after critical hit</summary>
        public float finalDamage;

        /// <summary>Whether this was a critical hit</summary>
        public bool isCritical;

        /// <summary>Critical hit multiplier applied</summary>
        public float criticalMultiplier = 1f;

        /// <summary>Whether this was a backstab</summary>
        public bool isBackstab;

        /// <summary>Whether absolute pierce was triggered</summary>
        public bool absolutePierceTriggered;
    }
}
