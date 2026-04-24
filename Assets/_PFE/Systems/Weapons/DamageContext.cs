using UnityEngine;
using PFE.Data.Definitions;

namespace PFE.Systems.Weapons
{
    /// <summary>
    /// Full damage payload carried from fire point to hit resolution.
    ///
    /// Created by the weapon controller at fire time (ammo modifiers already baked in).
    /// Attached to ShotPlan, then carried by projectile / melee hit volume through to
    /// DamageResolver, which is the only place final damage numbers are computed.
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
        /// <summary>GameObject that owns this weapon (used for friendly-fire filtering).</summary>
        public readonly GameObject Owner;

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
            float dopChance)
        {
            Owner             = owner;
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
        }

        /// <summary>
        /// Convenience factory — builds a context from weapon definition and owner stats,
        /// applying base weapon values without ammo modifiers.
        /// Ammo modifiers are applied by the controller after ammo type is resolved.
        /// </summary>
        public static DamageContext FromWeapon(WeaponDefinition def, GameObject owner)
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
                dopChance:         1f
            );
        }
    }
}
