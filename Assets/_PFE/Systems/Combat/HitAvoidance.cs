using PFE.Core.Rng;
using PFE.Systems.Weapons;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// Whether a reported hit lands at all — the port of the four-term conjunction that opens
    /// <c>Unit.udarBullet()</c> (<c>fe/unit/Unit.as:4067-4131</c>).
    ///
    /// <para><b>Why this is its own pure function and not a term inside
    /// <c>DamageCalculator.ResolveDamage</c>.</b> A miss is not a damage of zero: AS3 puts the whole
    /// body of the hit — damage, knockback, armour wear, crit, the floating number — <i>inside</i> the
    /// conjunction, so a missed shot consumes no armour integrity and cannot crit. Gating the reduction
    /// chain from inside it would mean a resolver that returns an outcome for a hit that never
    /// happened. So the decision is made first, by its own function, and <c>DamageSystem</c> either
    /// resolves or reports a miss.</para>
    ///
    /// <para><b>The oracle, verbatim</b> (<c>:4072</c>, reformatted and with the operator precedence
    /// made explicit — AS3 binds <c>&amp;&amp;</c> tighter than <c>||</c>, and the whole second group is
    /// one operand of the outer <c>&amp;&amp;</c>):</para>
    /// <code>
    /// _loc3_ = param1.accuracy();
    /// if ( (miss &lt;= 0 || Math.random() &gt; miss)
    ///      &amp;&amp; ( dexter &lt;= 0
    ///           || (precision &lt;= 0 &amp;&amp; tipBullet == 0)
    ///           || (tipBullet == 0 &amp;&amp; Math.random() &lt; accuracy / (dexter + dexterPlus + 0.05))
    ///           || (tipBullet == 1 &amp;&amp; dodge &lt; 1 &amp;&amp; (dodge &lt;= 0 || Math.random() &gt; dodge)) ) )
    /// </code>
    ///
    /// <para><b>Reading the terms.</b> Term 1 is the <i>attacker's</i> penalty for using a weapon above
    /// their skill (<c>miss = 1 - skillConf</c>). The second group is the <i>target's</i> evasion, and
    /// it splits on the shot kind: <c>tipBullet == 0</c> is a projectile, which is dodged by
    /// <i>accuracy vs dexterity</i>; <c>tipBullet == 1</c> is a melee swing (<c>WClub.as:150</c> is the
    /// only writer), which is dodged by the <c>dodge</c> probability. <b>A melee attack never reads
    /// dexterity and a projectile never reads dodge</b> — they are two different mechanics on one
    /// line.</para>
    ///
    /// <para><b>RNG order is part of the port, not an implementation detail.</b> The oracle
    /// short-circuits, so which rolls are consumed depends on the data: with <c>miss = 0</c> no roll is
    /// taken for term 1; a target with <c>dexter &lt;= 0</c> ends the group without rolling; a
    /// <c>dodge &lt;= 0</c> melee target returns true without rolling. Every one of those
    /// short-circuits is reproduced deliberately — taking a roll "anyway" would shift the shared
    /// combat stream and change crit rolls for every other hit in the same tick.</para>
    ///
    /// <para><b>A null <see cref="IRngService"/> resolves as a hit.</b> Not a silent default: with no
    /// randomness available the only safe direction is the one that leaves damage working, which is
    /// also the behaviour that existed before this check was ported. Stated here so a future reader
    /// does not read "always hits" as "evasion is broken".</para>
    /// </summary>
    public static class HitAvoidance
    {
        /// <summary>
        /// Sentinel for "the port does not know the shooter's skill level in this weapon category".
        /// See <see cref="SkillConfidence"/>.
        /// </summary>
        public const int UnknownOwnerSkillLevel = -1;

        /// <summary>
        /// AS3 <c>Bullet.accuracy()</c> (<c>weapon/Bullet.as:311-322</c>) — the attacker's side of the
        /// ranged evasion test.
        ///
        /// <para><c>precision</c> and <c>antiPrecision</c> are in <b>pixels</b> and
        /// <c>travelDistancePixels</c> is the distance the round has flown, also in pixels, because
        /// that is the oracle's own space: <c>Weapon.as:822</c> scales the data value
        /// (<c>precision = @prec * 40</c>) and <c>:826</c> scales its partner the same way
        /// (<c>antiprec * 40</c>), and <c>Bullet.dist</c> accumulates the per-frame step
        /// (<c>:416</c>). One tile is 40 px, so <c>accuracy = prec / distanceInTiles</c> — a weapon
        /// with <c>prec='8'</c> is perfectly accurate at 8 tiles and falls off linearly beyond.</para>
        ///
        /// <para><b><c>antiprec</c> is a minimum-range mechanic, and the ramp goes the counter-intuitive
        /// way.</b> Inside <c>antiPrecision</c> the function returns <c>0.25</c> at zero distance rising
        /// to <c>1.0</c> at the threshold — so the weapon is <i>less</i> accurate point-blank, not more.
        /// Four weapons in <c>AllData.as</c> carry <c>antiprec='8'</c> (320 px, 8 tiles). Reproducing
        /// the direction matters: "clamped to a minimum accuracy" would invert a real weapon
        /// archetype.</para>
        /// </summary>
        public static float Accuracy(float precision, float antiPrecision, float travelDistancePixels)
        {
            // AS3 returns 1 for an unscoped weapon before it ever divides — and this is also why the
            // caller can use `precision <= 0` as its own always-hit term.
            if (precision == 0f)
                return 1f;

            if (antiPrecision > 0f && travelDistancePixels < antiPrecision)
                return travelDistancePixels / antiPrecision * 0.75f + 0.25f;

            // No zero-distance guard on purpose: AS3 divides by zero here too and gets Infinity, which
            // makes the comparison below always true. A point-blank shot with no minimum range lands.
            return precision / travelDistancePixels;
        }

        /// <summary>
        /// AS3 <c>Weapon.skillConf</c> — the attacker's confidence in a weapon they may be under-skilled
        /// for, from <c>checkAvail()</c> (<c>Weapon.as:1366-1385</c>):
        /// <c>_loc1_ = this.lvl - owner.pers.getWeapLevel(this.skill)</c>, then <c>0.8</c> for a gap of 1
        /// and <c>0.6</c> for a gap of 2.
        ///
        /// <para><b><see cref="UnknownOwnerSkillLevel"/> means "no penalty", and that is a scaffolded
        /// divergence with a named owner.</b> AS3 reaches this through <c>Pers</c>, which every player
        /// has and the port has no equivalent of — the weapon-skill progression is the RPG bridge
        /// (Tier 3). Treating "unknown" as a penalty would be wrong (there is no gap to measure), and
        /// treating it as a large penalty would be catastrophic: <c>weaponLevel</c> runs 0..12 in
        /// <c>AllData.as</c>, so a level-4 weapon against an assumed skill of 1 is a gap of 3, which the
        /// oracle answers by <b>refusing to fire at all</b>. So unknown resolves to <c>1</c> — the value
        /// the oracle uses for a player at or above the required skill, and the value that leaves the
        /// port's behaviour exactly as it is today.</para>
        ///
        /// <para><b>The refuse-to-fire half is deliberately not here.</b> A gap above 2 makes
        /// <c>checkAvail()</c> return <c>false</c>, which stops the shot at the weapon — so the oracle
        /// never reaches hit resolution with such a gap, and there is no confidence value to report.
        /// This function returns <c>1</c> for that case to stay total; the gate itself belongs to the
        /// weapon controller, next to the ammo and jam gates it already has.</para>
        /// </summary>
        public static float SkillConfidence(int weaponLevel, int ownerSkillLevel)
        {
            if (ownerSkillLevel == UnknownOwnerSkillLevel)
                return 1f;

            int gap = weaponLevel - ownerSkillLevel;

            return gap switch
            {
                1 => 0.8f,
                2 => 0.6f,
                _ => 1f,   // gap <= 0: skilled enough. gap > 2: the weapon refuses the shot upstream.
            };
        }

        /// <summary>
        /// AS3 <c>b.miss = 1 - this.skillConf</c> (<c>Weapon.as:1523</c>) — the probability term 1
        /// rejects a shot with. <c>0</c> for a sufficiently skilled owner.
        /// </summary>
        public static float MissChance(int weaponLevel, int ownerSkillLevel)
            => 1f - SkillConfidence(weaponLevel, ownerSkillLevel);

        /// <summary>
        /// The four-term conjunction. Returns <c>true</c> when the hit lands.
        /// </summary>
        /// <param name="context">
        /// The shot's payload. Reads <see cref="DamageContext.MissChance"/>,
        /// <see cref="DamageContext.Precision"/>, <see cref="DamageContext.AntiPrecision"/> and
        /// <see cref="DamageContext.IsMelee"/>.
        /// </param>
        /// <param name="evasion">The target's evasion projection.</param>
        /// <param name="travelDistancePixels">
        /// How far the round has flown, in pixels — AS3 <c>Bullet.dist</c>. Ignored for a melee swing,
        /// which is the oracle's own behaviour (a melee hit has no distance term). <c>0</c> for anything
        /// that reports a hit without travelling.
        /// </param>
        /// <param name="rng">The combat stream. A null stream resolves as a hit; see the class remarks.</param>
        public static bool RollsHit(
            in DamageContext context,
            in EvasionState evasion,
            float travelDistancePixels,
            IRngService rng)
        {
            if (rng == null)
                return true;

            // ── Term 1: the attacker's under-skill penalty ────────────────────────────────────────
            // `miss <= 0 || Math.random() > miss` — and note the roll is taken ONLY when miss > 0,
            // because `<= 0` short-circuits. That keeps the shared stream aligned with the oracle.
            float miss = context.MissChance;
            if (miss > 0f && !(rng.NextFloat() > miss))
                return false;

            // ── Term 2: `dexter <= 0` ends the whole evasion group ───────────────────────────────
            // Evaluated before the shot-kind split, exactly as written. A target that cannot evade is
            // hit by a projectile and by a club alike, whatever its dodge says.
            if (evasion.CannotEvade)
                return true;

            if (!context.IsMelee)
            {
                // ── Term 3: an unscoped projectile always hits ───────────────────────────────────
                // `precision <= 0 && tipBullet == 0`. Kept separate from term 4 rather than folded into
                // `Accuracy()`, because Accuracy() returns 1 for precision 0 and `rnd < 1/divisor` is
                // NOT 1 — folding them would make every weapon with no precision stat miss.
                if (context.Precision <= 0f)
                    return true;

                // ── Term 4: accuracy vs dexterity ────────────────────────────────────────────────
                // The `+ 0.05` is the oracle's guard against a zero divisor; a dexter of 0 has already
                // returned above, but dexterPlus is not constrained and the constant is part of the
                // formula, so it is reproduced rather than reasoned away.
                float accuracy = Accuracy(context.Precision, context.AntiPrecision, travelDistancePixels);
                float divisor  = evasion.Dexterity + evasion.DexterityPlus + 0.05f;

                return rng.NextFloat() < accuracy / divisor;
            }

            // ── Term 5: the melee dodge probability ──────────────────────────────────────────────
            // `tipBullet == 1 && dodge < 1 && (dodge <= 0 || Math.random() > dodge)`.
            if (evasion.Dodge >= 1f)
                return false;   // a full dodge: the term fails, and no other term can rescue it

            if (evasion.Dodge <= 0f)
                return true;    // `dodge <= 0` short-circuits before the roll — no RNG consumed

            return rng.NextFloat() > evasion.Dodge;
        }
    }
}
