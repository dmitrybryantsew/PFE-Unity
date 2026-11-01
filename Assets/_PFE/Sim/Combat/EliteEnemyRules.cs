using System;
using PFE.Data.Definitions;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// The "starred" enemy — AS3 <c>Unit.setHero()</c> (<c>Unit.as:1625-1672</c>), its vulnerability
    /// companion <c>setHeroVulners()</c> (<c>:1674-1695</c>), and the roll that produces one in a
    /// procedurally generated land (<c>Location.as:1191-1204</c>).
    ///
    /// <para><b>What the original does.</b> A random land spawns most enemies plainly, but with
    /// probability <c>min(0.05, locDifLevel / 100 + 0.02)</c> — i.e. 2 % at difficulty 0, capped at 5 % —
    /// the enemy is promoted to one of four hero tiers. A promoted enemy gets a <b>gold star</b> over its
    /// head (<c>Unit.addVisual():2886-2889</c> hides <c>hpBar.goldstar</c> for every unit with
    /// <c>hero &lt;= 0</c>), a <b>generated name</b> drawn from a per-species list
    /// (<c>Unit.getName():1477-1497</c>), and a block of stat multipliers. The name is what the player
    /// remembers; the star is what makes the name findable in a crowded room.</para>
    ///
    /// <para><b>Why a table and not an <c>if</c> chain on the unit.</b> Same reason as
    /// <see cref="AlicornVariantRules"/>: six of these numbers exist nowhere in the imported data — they
    /// are hard-coded per branch in the oracle's constructor — so the only way to check a port of them is
    /// against a file that mirrors the oracle line for line. Unity-free on purpose, so the offline
    /// harness can run the fixtures without a <c>MonoBehaviour</c>.</para>
    ///
    /// <para><b>What is deliberately NOT here.</b> <c>setLevel()</c> (<c>Unit.as:1600-1623</c>) is a
    /// separate gap: the oracle scales every spawn by the room's <c>enemyLevel</c> before it calls
    /// <c>setHero</c>, and the port has no level scaling at all. The two multiply, so porting one without
    /// the other gives the right <i>shape</i> and the wrong magnitude — recorded here so the next reader
    /// does not assume the missing factor is a bug in this file.</para>
    /// </summary>
    public static class EliteEnemyRules
    {
        /// <summary><c>Math.floor(Math.random() * 4 + 1)</c> — the four tiers, and the lowest one.</summary>
        public const int MinTier = 1;

        /// <summary>The highest tier the roll can produce.</summary>
        public const int MaxTier = 4;

        /// <summary><c>Math.min(0.05, …)</c> — the ceiling on the promotion chance.</summary>
        public const float MaxRollChance = 0.05f;

        /// <summary>The <c>+ 0.02</c> in <c>locDifLevel / 100 + 0.02</c>.</summary>
        public const float BaseRollChance = 0.02f;

        /// <summary><c>this.xp *= 5</c> (<c>Unit.as:1646</c>) — every tier, not just the high ones.</summary>
        public const float XpMultiplier = 5f;

        /// <summary><c>this.observ += 8</c> (<c>Unit.as:1666</c>), tier 4 only.</summary>
        public const float Tier4ObservationBonus = 8f;

        /// <summary>
        /// <c>Math.min(0.05, this.locDifLevel / 100 + 0.02)</c> — the promotion chance for a random
        /// spawn in a land whose difficulty is <paramref name="locationDifficulty"/>.
        ///
        /// <para>The oracle reads <c>locDifLevel</c>, which is <c>Location.enemyLevel</c>
        /// (<c>Land.as:983</c>) — the same field <c>Unit.getXmlWeapon</c> compares a weapon's <c>dif</c>
        /// against. A negative difficulty is clamped at the base chance rather than allowed to fall
        /// below it: AS3 would let it go negative (and then <c>Math.random()</c> can never be below it,
        /// so no elites at all), which is not a state the shipped campaign reaches, and reproducing it
        /// would make a mis-set difficulty silently disable the feature.</para>
        /// </summary>
        public static float RollChance(float locationDifficulty)
        {
            float difficulty = locationDifficulty > 0f ? locationDifficulty : 0f;
            float chance = difficulty / 100f + BaseRollChance;
            return chance < MaxRollChance ? chance : MaxRollChance;
        }

        /// <summary>
        /// <c>Math.floor(Math.random() * 4 + 1)</c> — turn a <c>[0,1)</c> draw into a tier.
        ///
        /// <para>Clamped to <see cref="MinTier"/>..<see cref="MaxTier"/>: a draw of exactly <c>1.0</c>
        /// (impossible from <c>Math.random()</c>, possible from a caller's own generator) would otherwise
        /// produce tier 5, which no branch in <c>setHero</c> matches and which would therefore hand a
        /// unit the name and the XP with no stat block at all — the worst of both.</para>
        /// </summary>
        public static int RollTier(double draw01)
        {
            int tier = MinTier + (int)Math.Floor(draw01 * MaxTier);
            if (tier < MinTier) return MinTier;
            if (tier > MaxTier) return MaxTier;
            return tier;
        }

        /// <summary>
        /// <c>hp = maxhp = maxhp * …</c> — tier 1 <c>×2.5</c>, tiers 2 and 3 <c>×3</c>, tier 4
        /// <c>×2</c> (<c>Unit.as:1648-1670</c>). <c>1</c> for an ordinary unit.
        ///
        /// <para>Tier 2 and tier 3 share a branch in the oracle, which is why the biggest health
        /// multiplier is not the highest tier: tier 3 pays for its resistances instead, and tier 4 pays
        /// for its speed and sight.</para>
        /// </summary>
        public static float HealthMultiplier(int tier)
        {
            switch (tier)
            {
                case 1: return 2.5f;
                case 2:
                case 3: return 3f;
                case 4: return 2f;
                default: return 1f;
            }
        }

        /// <summary><c>this.dam *= …</c> — tier 1 <c>×1.8</c>, tiers 2/3 <c>×1.2</c>, tier 4 <c>×1.4</c>.</summary>
        public static float DamageMultiplier(int tier)
        {
            switch (tier)
            {
                case 1: return 1.8f;
                case 2:
                case 3: return 1.2f;
                case 4: return 1.4f;
                default: return 1f;
            }
        }

        /// <summary>
        /// <c>if(this.currentWeapon) this.currentWeapon.damage *= 1.5</c> — <b>tier 1 only</b>
        /// (<c>Unit.as:1652-1655</c>). The other three tiers get no weapon multiplier at all.
        ///
        /// <para>This is the one multiplier that reads <c>currentWeapon</c> rather than a unit field, so
        /// it is also the one that silently does nothing for a unit that has not equipped yet. In the
        /// port that means the caller must apply it <i>after</i> the archetype has equipped — see
        /// <c>UnitController.ApplyEliteTier</c>, which runs from <c>ApplyPlacement</c>, i.e. after
        /// <c>Initialize</c>.</para>
        /// </summary>
        public static float WeaponDamageMultiplier(int tier) => tier == 1 ? 1.5f : 1f;

        /// <summary><c>this.walkSpeed *= 1.4</c> and <c>this.sitSpeed *= 1.4</c> — tier 4 only.</summary>
        public static float WalkSpeedMultiplier(int tier) => tier == 4 ? 1.4f : 1f;

        /// <summary><c>this.runSpeed *= 1.25</c> — tier 4 only, and a smaller factor than the walk one.</summary>
        public static float RunSpeedMultiplier(int tier) => tier == 4 ? 1.25f : 1f;

        /// <summary><c>this.observ += 8</c> — tier 4 only. A flat add, not a multiplier.</summary>
        public static float ObservationBonus(int tier) => tier == 4 ? Tier4ObservationBonus : 0f;

        /// <summary>
        /// The incoming-damage multiplier this tier applies to <paramref name="type"/> — AS3
        /// <c>setHeroVulners()</c> (<c>Unit.as:1674-1695</c>), expressed as a factor to multiply the
        /// unit's existing table by.
        ///
        /// <para><b>Every tier</b> takes <c>×0.8</c> EMP and <c>×0.7</c> balefire / necrotic / astral.
        /// <b>Tier 2</b> adds <c>×0.5</c> bullets and <c>×0.65</c> melee / blade, <c>×0.75</c>
        /// explosives. <b>Tier 3</b> adds <c>×0.6</c> laser, <c>×0.5</c> plasma, <c>×0.75</c> EMP
        /// <i>on top of the shared 0.8</i> (so 0.6), <c>×0.7</c> spark and <c>×0.7</c> fire.</para>
        ///
        /// <para><b>Tier 0 is not "no reductions".</b> <c>setHeroVulners()</c> is the last statement of
        /// <c>setHero</c>, <i>outside</i> every <c>hero ==</c> branch, so a hero-capable unit that was
        /// never promoted still gets the shared four — and the oracle reaches it: <c>Location.as:1204</c>
        /// calls <c>setHero(0)</c> unconditionally. Treating 0 as "nothing" silently dropped the block
        /// from the 39 hero-capable rows that are not currently promoted. Only a value outside
        /// <c>0..4</c> — which neither the roll nor an authored placement produces — answers 1.</para>
        ///
        /// <para><b>Balefire and astral are unreachable in the port.</b> The oracle reduces them for
        /// every tier, but <c>VulnerabilityData</c> has no slot for either — its remarks record that
        /// AS3's own <c>&lt;vulner&gt;</c> reader cannot reach them, and
        /// <c>GetVulnerability</c> answers <c>1</c> for both. They are named here so the omission is
        /// visible rather than looking like a missed line.</para>
        /// </summary>
        public static float VulnerabilityMultiplier(int tier, DamageType type)
        {
            if (tier < 0 || tier > MaxTier)
            {
                return 1f;
            }

            switch (type)
            {
                // Shared by every tier, 0 included.
                case DamageType.EMP:
                    return tier == 3 ? 0.8f * 0.75f : 0.8f;
                case DamageType.Necrotic:
                    return 0.7f;

                // Tier 2 — the bullet sponge.
                case DamageType.PhysicalBullet:
                    return tier == 2 ? 0.5f : 1f;
                case DamageType.PhysicalMelee:
                case DamageType.Blade:
                    return tier == 2 ? 0.65f : 1f;
                case DamageType.Explosive:
                    return tier == 2 ? 0.75f : 1f;

                // Tier 3 — the elemental caster.
                case DamageType.Laser:
                    return tier == 3 ? 0.6f : 1f;
                case DamageType.Plasma:
                    return tier == 3 ? 0.5f : 1f;
                case DamageType.Spark:
                case DamageType.Fire:
                    return tier == 3 ? 0.7f : 1f;

                default:
                    return 1f;
            }
        }

        /// <summary>
        /// The damage types <see cref="VulnerabilityMultiplier"/> can express through the port's
        /// <c>VulnerabilityData</c> — every slot AS3's own <c>&lt;vulner&gt;</c> reader can reach, plus
        /// <c>necro</c>, which AS3 writes from code.
        ///
        /// <para>Kept here rather than derived from <c>VulnerabilityData.As3AttributeNames</c> because
        /// that list is attribute <i>names</i> for the importer; this is the consumer side, and the two
        /// would otherwise have to be zipped by index to agree.</para>
        /// </summary>
        public static readonly DamageType[] ExpressibleTypes =
        {
            DamageType.PhysicalBullet, DamageType.Blade, DamageType.PhysicalMelee,
            DamageType.Fire, DamageType.Explosive, DamageType.Laser, DamageType.Plasma,
            DamageType.Spark, DamageType.Acid, DamageType.Cryo,
            DamageType.Venom, DamageType.Poison, DamageType.Bleed, DamageType.Fang,
            DamageType.EMP, DamageType.Pink, DamageType.Necrotic,
        };
    }
}
