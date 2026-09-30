using UnityEngine;
using PFE.Data.Definitions;
using PFE.Systems.Combat;

namespace PFE.Systems.Weapons
{
    /// <summary>
    /// Full damage payload carried from fire point to hit resolution.
    ///
    /// Created by the weapon controller at fire time (ammo modifiers already baked in).
    /// Attached to ShotPlan, then carried by projectile / melee hit volume through to
    /// <c>DamageSystem</c>, which is the only place final damage numbers are computed.
    ///
    /// Mirrors the fields AS3 passes via setBullet() + Bullet properties:
    ///   baseDamage      → b.damage (after ammoDamage multiplier)
    ///   explosionDamage → b.damageExpl
    ///   piercing        → b.pier + pierAdd + ammoPier
    ///   armorMultiplier → b.armorMult (ammoArmor)
    ///   knockback       → b.otbros * otbrosMult * ammoOtbros
    ///   critChance      → b.critCh
    ///   critMultiplier  → b.critDamMult
    ///   destroyTiles    → b.destroy
    ///   dopEffect       → b (stun etc via dop node)
    /// </summary>
    public readonly struct DamageContext
    {
        /// <summary>
        /// GameObject that owns this weapon.
        ///
        /// <para><b>Correction:</b> this was documented as "used for friendly-fire filtering", but
        /// nothing ever filtered on it — it is <c>null</c> on every production path, because all six
        /// weapon controllers pass <c>null</c> and the spawner never fills it in. Friendly-fire
        /// filtering is <see cref="OwnerFaction"/>, which carries the value the filtering actually
        /// needs and does not require a <c>GetComponent</c> at hit time.</para>
        /// </summary>
        public readonly GameObject Owner;

        /// <summary>
        /// Faction of the unit that owns this weapon — the attacker side of
        /// <see cref="FactionRule"/>. AS3 reads the equivalent as <c>this.weap.owner.fraction</c>.
        ///
        /// <para>Defaults to <see cref="FactionType.Neutral"/> so a context built without an owner
        /// hits everyone, which is AS3's own default (<c>Unit.as:454</c>) and the safe direction for
        /// a value that was previously ignored entirely.</para>
        /// </summary>
        public readonly FactionType OwnerFaction;

        /// <summary>Static definition of the weapon that fired.</summary>
        public readonly WeaponDefinition Weapon;

        /// <summary>
        /// Direct hit damage, ammo multipliers already applied.
        /// result = weapon.resultDamage(damage, skill) * ammoDamage
        /// </summary>
        public readonly float BaseDamage;

        /// <summary>Explosion damage on impact (damageExpl in AS3). 0 = no explosion.</summary>
        public readonly float ExplosionDamage;

        /// <summary>
        /// Armor reduction multiplier applied to target armour value before subtraction.
        /// 1 = full armour applies. 0 = armour is ignored. (ammoArmor in AS3)
        /// </summary>
        public readonly float ArmorMultiplier;

        /// <summary>
        /// Flat piercing bonus added to pierce roll.
        /// Final pierce = pier + pierAdd + ammoPier. Compared vs target armour tier.
        /// </summary>
        public readonly float Piercing;

        /// <summary>Knockback force magnitude (otbros * otbrosMult * ammoOtbros in AS3).</summary>
        public readonly float Knockback;

        /// <summary>Knockback direction unit vector (set from bullet dx/dy / vel).</summary>
        public readonly Vector2 KnockbackDir;

        /// <summary>Probability 0–1 of critical hit (critCh + owner.critCh + critchAdd in AS3).</summary>
        public readonly float CritChance;

        /// <summary>Critical damage multiplier additive bonus (critDamMult + critDamPlus in AS3).</summary>
        public readonly float CritMultiplier;

        /// <summary>Damage type index matching AS3 Unit.D_* constants.</summary>
        public readonly DamageType DamageType;

        /// <summary>Tile / structure destruction power per hit (destroy in AS3). 0 = no tile damage.</summary>
        public readonly float DestroyTiles;

        /// <summary>Probability of projectile passing through the target without stopping (probiv in AS3).</summary>
        public readonly float PenetrationChance;

        /// <summary>Optional status effect string ("stun", etc.) from dop node.</summary>
        public readonly string DopEffect;

        /// <summary>Flat damage added by dop effect on proc.</summary>
        public readonly float DopDamage;

        /// <summary>Probability 0–1 that dop effect triggers on hit.</summary>
        public readonly float DopChance;

        // ── Hit avoidance (AS3 Bullet.miss / .precision / .antiprec / .tipBullet) ─────────────
        //
        // These four decide whether the hit happens at all, before any damage term runs. They live
        // here rather than on PendingDamage because they are properties of the *shot*, fixed at fire
        // time; the one hit-time input, how far the round has flown, is on PendingDamage instead.
        // The decision itself is HitAvoidance.RollsHit.

        /// <summary>
        /// Probability 0–1 that the shot misses outright — AS3 <c>Bullet.miss</c>, set to
        /// <c>1 - skillConf</c> at fire time (<c>Weapon.as:1523</c>). <c>0</c> = no penalty, which is
        /// the oracle's value for an owner at or above the weapon's required skill.
        /// </summary>
        /// <remarks>
        /// AS3 reads it as the <i>first</i> term of the hit conjunction, and rolls for it only when it
        /// is positive — the term short-circuits on <c>&lt;= 0</c>. See
        /// <see cref="HitAvoidance.MissChance"/> for the producer and for why it currently resolves to
        /// zero on every path the port can evaluate.
        /// </remarks>
        public readonly float MissChance;

        /// <summary>
        /// The weapon's accuracy stat in <b>pixels</b> — AS3 <c>Bullet.precision</c>, imported as
        /// <c>@prec * 40</c> (<c>Weapon.as:822</c>). Divided by the round's travel distance to give the
        /// accuracy the target's dexterity is tested against. <c>0</c> = unscoped: always hits.
        /// </summary>
        public readonly float Precision;

        /// <summary>
        /// The weapon's minimum-range distance in <b>pixels</b> — AS3 <c>Bullet.antiprec</c>, imported
        /// as <c>@antiprec * 40</c> (<c>Weapon.as:826</c>). Inside it, accuracy ramps from 0.25 up to
        /// 1.0, i.e. the weapon is <i>worse</i> point-blank. <c>0</c> = no minimum range.
        /// </summary>
        public readonly float AntiPrecision;

        /// <summary>
        /// True when this shot is a melee swing — AS3 <c>Bullet.tipBullet == 1</c>, which only
        /// <c>WClub.shoot()</c> sets (<c>WClub.as:150</c>). It switches the evasion test from
        /// <i>accuracy vs dexterity</i> to the <i>dodge</i> probability, and makes the travel distance
        /// irrelevant.
        /// </summary>
        /// <remarks>
        /// <b>Not a synonym for "unarmed".</b> <c>WPunch</c> does not set <c>tipBullet</c>, so an
        /// unarmed attack is a <c>tipBullet == 0</c> shot that happens to carry no precision — it
        /// resolves through the ranged branch and always hits, which is the oracle's behaviour.
        /// </remarks>
        public readonly bool IsMelee;

        public DamageContext(
            GameObject owner,
            WeaponDefinition weapon,
            float baseDamage,
            float explosionDamage,
            float armorMultiplier,
            float piercing,
            float knockback,
            Vector2 knockbackDir,
            float critChance,
            float critMultiplier,
            DamageType damageType,
            float destroyTiles,
            float penetrationChance,
            string dopEffect,
            float dopDamage,
            float dopChance,
            FactionType ownerFaction = FactionType.Neutral,
            float missChance = 0f,
            float precision = 0f,
            float antiPrecision = 0f,
            bool isMelee = false)
        {
            Owner             = owner;
            OwnerFaction      = ownerFaction;
            Weapon            = weapon;
            BaseDamage        = baseDamage;
            ExplosionDamage   = explosionDamage;
            ArmorMultiplier   = armorMultiplier;
            Piercing          = piercing;
            Knockback         = knockback;
            KnockbackDir      = knockbackDir;
            CritChance        = critChance;
            CritMultiplier    = critMultiplier;
            DamageType        = damageType;
            DestroyTiles      = destroyTiles;
            PenetrationChance = penetrationChance;
            DopEffect         = dopEffect;
            DopDamage         = dopDamage;
            DopChance         = dopChance;
            MissChance        = missChance;
            Precision         = precision;
            AntiPrecision     = antiPrecision;
            IsMelee           = isMelee;
        }

        /// <summary>
        /// This context with its damage and knockback scaled, every other field carried over.
        ///
        /// <para><b>Why this exists.</b> Two controllers — melee and unarmed — used to rebuild the
        /// context with a 16-argument positional <c>new DamageContext(...)</c> copy just to multiply
        /// two numbers. That is a silent-drop trap: <c>MeleeWeaponController</c> and
        /// <c>UnarmedWeaponController</c> had <i>already</i> lost <c>ownerFaction</c> that way (it was
        /// never passed, so every melee hit carried <see cref="FactionType.Neutral"/>), and adding any
        /// field to this struct would have lost it in both places without a compile error. Scaling
        /// through one method means the next field added is carried automatically.</para>
        /// </summary>
        /// <param name="damageScale">Multiplier on <see cref="BaseDamage"/>. 1 = unchanged.</param>
        /// <param name="knockbackScale">Multiplier on <see cref="Knockback"/>. 1 = unchanged.</param>
        /// <param name="knockbackDir">Direction to stamp, or null to keep the original.</param>
        public DamageContext WithScaledDamage(float damageScale, float knockbackScale, Vector2? knockbackDir = null)
        {
            return new DamageContext(
                owner:             Owner,
                weapon:            Weapon,
                baseDamage:        BaseDamage * damageScale,
                explosionDamage:   ExplosionDamage,
                armorMultiplier:   ArmorMultiplier,
                piercing:          Piercing,
                knockback:         Knockback * knockbackScale,
                knockbackDir:      knockbackDir ?? KnockbackDir,
                critChance:        CritChance,
                critMultiplier:    CritMultiplier,
                damageType:        DamageType,
                destroyTiles:      DestroyTiles,
                penetrationChance: PenetrationChance,
                dopEffect:         DopEffect,
                dopDamage:         DopDamage,
                dopChance:         DopChance,
                ownerFaction:      OwnerFaction,
                missChance:        MissChance,
                precision:         Precision,
                antiPrecision:     AntiPrecision,
                isMelee:           IsMelee);
        }

        /// <summary>
        /// Convenience factory — builds a context from weapon definition and owner stats,
        /// applying base weapon values without ammo modifiers.
        /// Ammo modifiers are applied by the controller after ammo type is resolved.
        /// </summary>
        /// <param name="ownerFaction">
        /// The firing unit's faction, from <c>WeaponRuntimeState.OwnerFaction</c>. Passed explicitly
        /// rather than resolved from <paramref name="owner"/> because the hit path needs it on every
        /// contact and a <c>GetComponent</c> per hit is not free; the loadout already knows whose
        /// weapon this is at equip time.
        /// </param>
        /// <param name="ownerWeaponSkillLevel">
        /// The firing unit's skill level in this weapon's category, or
        /// <see cref="HitAvoidance.UnknownOwnerSkillLevel"/> when the caller does not know it — which
        /// is every current caller, because weapon-skill progression is the RPG bridge. It feeds
        /// <see cref="MissChance"/> and nothing else; see <see cref="HitAvoidance.SkillConfidence"/>.
        /// </param>
        public static DamageContext FromWeapon(
            WeaponDefinition def, GameObject owner, FactionType ownerFaction = FactionType.Neutral,
            int ownerWeaponSkillLevel = HitAvoidance.UnknownOwnerSkillLevel)
        {
            return new DamageContext(
                owner:             owner,
                weapon:            def,
                baseDamage:        def.baseDamage,
                explosionDamage:   def.explosionDamage,
                armorMultiplier:   1f,
                piercing:          def.piercing,
                knockback:         def.knockback,
                knockbackDir:      Vector2.right,
                critChance:        def.critChance,
                critMultiplier:    def.critMultiplier,
                damageType:        def.damageType,
                destroyTiles:      def.destroyTiles,
                penetrationChance: def.piercing,
                dopEffect:         null,
                dopDamage:         0f,
                dopChance:         1f,
                ownerFaction:      ownerFaction,
                missChance:        HitAvoidance.MissChance(def.weaponLevel, ownerWeaponSkillLevel),
                precision:         def.precision,
                antiPrecision:     def.antiPrecision,
                // AS3 sets `b.tipBullet = 1` only in WClub.shoot() (WClub.as:150), and
                // Weapon.create() routes tip==1 to WClub — so the weapon type *is* the tipBullet
                // signal. Deriving it here means a new melee weapon cannot forget to set it.
                isMelee:           def.weaponType == WeaponType.Melee
            );
        }
    }
}
