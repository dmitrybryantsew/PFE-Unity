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
        /// Flat armour-piercing <b>points</b>, subtracted from the target's armour reduction — AS3
        /// <c>Bullet.pier = weapon.pier + weapon.pierAdd + ammo.pier</c> (<c>Weapon.as:1672</c>),
        /// consumed at <c>Unit.as:3641</c> (<c>_loc8_ = _loc8_ - param3.pier</c>). Not a probability, and
        /// not <see cref="PenetrationChance"/>.
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

        /// <summary>
        /// AS3 <c>Bullet.probiv</c> — the round's <b>penetration budget</b>, not a probability and not
        /// <see cref="Piercing"/>.
        ///
        /// <para>A round whose probiv is above zero <b>does not stop</b> on the unit it hits
        /// (<c>weapon/Bullet.as:533</c>: the stop is gated on <c>!(probiv &gt; 0 &amp;&amp; damage &gt; 0)</c>),
        /// and <c>Unit.damage()</c> spends the round's damage down by what it absorbed
        /// (<c>Unit.as:3646-3648</c>) and by a three-branch decay against the target's max HP
        /// (<c>:3684-3696</c>). When the damage reaches zero the round finally stops. It is built at fire
        /// time as <c>weapon.probiv + ammo.probiv</c>, clamped to 1 (<c>Weapon.as:1681-1684</c>), where
        /// the weapon's half comes from its <c>&lt;dop probiv&gt;</c> node (<c>:703-705</c>) and the
        /// ammo's from <c>&lt;item probiv&gt;</c> (<c>:1786-1788</c>).</para>
        ///
        /// <para><b>This used to be fed <c>def.piercing</c></b>, which is a flat armour figure in the
        /// 5..70 range — so every weapon carrying <c>@pier</c> looked like a 100% penetrator once the
        /// value reached a <c>Clamp01</c>. The two are different quantities and now come from different
        /// fields.</para>
        ///
        /// <para><b>It currently has no consumer.</b> The live penetration path is
        /// <c>Projectile</c>'s own <c>_penetration</c>/<c>_remainingDamage</c> pair, because a budget
        /// has to be spent against a target and carried across hits — neither of which a per-hit,
        /// copied struct can do. This field is carried so the context still describes the shot
        /// completely, and it is fed from the right source; do not read its presence as "penetration
        /// lives here".</para>
        /// </summary>
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
        /// <summary>
        /// AS3's <c>Unit.damage(amount, type)</c> reached with <b>no bullet</b> — the shape a prop
        /// impact uses (<c>Unit.udarBox</c>, <c>fe/unit/Unit.as:4237</c>) and a floor trap will
        /// (<c>Trap.as:188</c>).
        /// </summary>
        /// <remarks>
        /// <para><b>Why this is a separate factory and not <c>FromWeapon(null, …)</c>.</b> AS3's
        /// <c>damage()</c> takes the bullet as an optional third argument and every bullet-derived
        /// term is inside <c>if(param3)</c> (<c>:3638-3642</c> for <c>armorMult</c>/<c>pier</c>,
        /// <c>:3652-3658</c> for crit). A prop impact passes none, so the hit has <b>no</b> crit, no
        /// piercing, no armour multiplier, no knockback and no penetration budget — not "zero-valued"
        /// versions of them, but absent. Writing those as zeros here is how they are expressed.</para>
        ///
        /// <para><b>What still applies, and must.</b> Vulnerability, the target's <c>skin</c> and its
        /// armour pool — all three are inside the part of <c>damage()</c> that runs regardless of
        /// <c>param3</c> (<c>:3527-3530</c>, <c>:3611-3637</c>). That is exactly why a prop impact
        /// cannot simply call <c>IDamageable.TakeDamage</c>, which is a raw HP subtraction.</para>
        ///
        /// <para><c>IsMelee</c> is <c>false</c>: <c>udarBox</c> is not <c>udarUnit</c>, and the flag is
        /// read only by <see cref="HitAvoidance"/> — a path a contact hit does not take.</para>
        /// </remarks>
        public static DamageContext Contact(float damage, DamageType damageType)
        {
            return new DamageContext(
                owner:             null,
                weapon:            null,
                baseDamage:        damage,
                explosionDamage:   0f,
                armorMultiplier:   1f,
                piercing:          0f,
                knockback:         0f,
                knockbackDir:      Vector2.zero,
                critChance:        0f,
                critMultiplier:    1f,
                damageType:        damageType,
                destroyTiles:      0f,
                penetrationChance: 0f,
                dopEffect:         null,
                dopDamage:         0f,
                dopChance:         0f);
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
                // Deliberately ZERO, not Vector2.right. This factory has the weapon but not the shot, so
                // it cannot know which way a hit will throw — and a placeholder direction would be
                // activated the moment the resolver started applying knockback, shoving every target
                // east no matter where the shot came from. Zero reads as "unstamped", which the resolver
                // turns into no impulse at all: a silently missing knockback is a far better failure than
                // a confidently wrong one. Whoever knows the direction stamps it through
                // WithScaledDamage — the projectile from its launch vector (Weapon.as:1507-1508), the
                // melee controllers from the attacker's facing (WClub.as:152-153).
                knockbackDir:      Vector2.zero,
                critChance:        def.critChance,
                critMultiplier:    def.critMultiplier,
                damageType:        def.damageType,
                destroyTiles:      def.destroyTiles,
                penetrationChance: def.penetration,
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
