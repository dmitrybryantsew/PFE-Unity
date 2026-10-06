using PFE.Core.Rng;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// How hard a landed hit throws its target — the port of <c>Unit.otbros()</c>,
    /// <c>unit/Unit.as:4242-4260</c>:
    ///
    /// <code>
    /// if(this.invulner) return;
    /// _loc2_ = Math.random() * 0.4 + 0.8;   // 0.8 .. 1.2
    /// _loc2_ *= this.knocked / massa;
    /// if(_loc2_ > 3) _loc2_ = 3;
    /// dx += param1.knockx * param1.otbros * _loc2_;
    /// dy += param1.knocky * param1.otbros * _loc2_;
    /// </code>
    ///
    /// <para><b>The bullet really does throw things.</b> This was missing entirely from the port:
    /// <c>@knock</c> was imported into <c>WeaponDefinition.knockback</c> and carried into
    /// <c>DamageContext.Knockback</c>, and then read by nothing at all — so no shot had ever moved a
    /// unit. The value is not decorative: 180-odd weapons carry real ones (<c>bel</c> 150, <c>glau</c>
    /// 50, <c>mlau</c> 50, <c>sledge</c> 25, and <c>0</c> on the many weapons meant to leave a target
    /// standing).</para>
    ///
    /// <para><b>The three factors, and what each is for.</b> <c>otbros</c> is the weapon's knock, scaled
    /// at fire time by <c>otbrosMult</c> (perks) and <c>ammoOtbros</c> (<c>Weapon.setBullet():1671</c>).
    /// The <c>0.8 .. 1.2</c> draw is a per-hit jitter, so a burst does not shove by an identical amount
    /// every time. <c>knocked / massa</c> is the target: <c>knocked</c> is a per-unit susceptibility
    /// authored on the <c>&lt;move&gt;</c> node — <c>0</c> for fixed things like turrets and
    /// <c>UnitBossNecr</c>'s shadow, <c>1</c> for an ordinary humanoid, <c>1.5</c> for a light one — and
    /// <c>massa</c> is its weight. So <c>knocked = 0</c> is the oracle's "cannot be moved" flag, and the
    /// clamp at <c>3</c> is what stops a 150-knock weapon from launching a rat into orbit.</para>
    ///
    /// <para><b>Where it sits in the order, and why the draw count matters.</b> <c>otbros()</c> is
    /// called from <c>udarBullet</c> <i>after</i> <c>this.damage()</c> (<c>Unit.as:4090-4091</c>), so its
    /// draw is the <b>last</b> one a hit takes: variance, then armour and crit inside
    /// <c>damage()</c>, then this. The port's <c>DamageSystem</c> therefore rolls it after
    /// <c>IDamageCalculator.ResolveDamage</c> and not before, or the shared per-tick combat stream would
    /// be handed to every later hit in the tick one draw out of step.</para>
    ///
    /// <para><b>Two gates, and they differ.</b> The <c>invulner</c> check returns <i>before</i> the draw
    /// (<c>:4245-4248</c>), so an invulnerable target consumes no roll at all — the caller must make that
    /// test, not this class. Every other landed hit draws, <b>including one whose weapon has
    /// <c>otbros = 0</c></b>: the draw happens first and the multiply by zero happens after, so
    /// short-circuiting on a zero knock would silently shift the stream.</para>
    /// </summary>
    public static class KnockbackMath
    {
        /// <summary>The jitter's floor. AS3's literal <c>0.8</c>.</summary>
        public const float MinScale = 0.8f;

        /// <summary>
        /// The jitter's width. AS3's literal <c>0.4</c>, so the factor is <c>[0.8, 1.2)</c> —
        /// <c>NextFloat()</c> is half-open and the top of the range is excluded, exactly as AS3's
        /// <c>Math.random()</c> excludes 1.0.
        /// </summary>
        public const float ScaleSpread = 0.4f;

        /// <summary>
        /// The ceiling on the whole factor. AS3's literal <c>3</c>, applied <i>after</i> the
        /// <c>knocked / massa</c> multiply so it caps the product rather than either input.
        /// </summary>
        public const float MaxScale = 3f;

        /// <summary>
        /// The divisor used when a target reports no mass. AS3's field default —
        /// <c>Unit.massaFix = 1</c> (<c>Unit.as:210</c>), which is what <c>massa</c> holds for every unit
        /// that never authors one.
        ///
        /// <para><b>Why a guard at all, when AS3 has none.</b> A zero divisor makes AS3's expression
        /// <c>Infinity</c> (clamped to 3 by the line below) or, if <c>knocked</c> is zero too,
        /// <c>NaN</c> — and <c>NaN</c> added to a unit's velocity is not a behaviour, it is a permanent
        /// corruption of that unit's movement. AS3 cannot reach it because <c>massa</c> starts at 1 and
        /// only ever holds <c>@massa / 50</c>; the port's importer stores the raw attribute, so a
        /// malformed row could. Substituting the oracle's own default is the closest thing to a faithful
        /// answer.</para>
        /// </summary>
        public const float DefaultMass = 1f;

        /// <summary>
        /// The scalar <c>dx</c>/<c>dy</c> are multiplied by, from one caller-supplied draw. Pure: no
        /// RNG, no Unity, so it can be executed outside the editor against deliberately-wrong variants.
        /// </summary>
        /// <param name="randomRoll">One value in <c>[0, 1)</c> — AS3's <c>Math.random()</c>.</param>
        /// <param name="knocked">AS3 <c>Unit.knocked</c>. <c>0</c> means immovable.</param>
        /// <param name="mass">AS3 <c>Unit.massa</c>. See <see cref="DefaultMass"/> for a non-positive one.</param>
        public static float Scale(float randomRoll, float knocked, float mass)
        {
            float divisor = mass > 0f ? mass : DefaultMass;

            float scale = (randomRoll * ScaleSpread + MinScale) * (knocked / divisor);

            return scale > MaxScale ? MaxScale : scale;
        }

        /// <summary>
        /// One draw from the jitter and the full scale. Consumes exactly one
        /// <see cref="IRngService.NextFloat"/> — see the class remarks on the zero-knock case.
        /// </summary>
        /// <remarks>
        /// The <c>invulner</c> gate is <b>not</b> here. AS3 tests it before drawing, so a caller that
        /// folds it in afterwards would have already taken a roll the oracle never takes.
        /// </remarks>
        public static float Roll(IRngService rng, float knocked, float mass)
            => Scale(rng.NextFloat(), knocked, mass);

        // The final multiply — AS3's `dx += knockx * otbros * k` — is left to the caller, which already
        // holds the direction and the weapon's knock. Folding it in here would mean taking a
        // UnityEngine.Vector2 parameter, and this file is deliberately free of Unity so that it can be
        // linked as source into a console harness and run against deliberately-wrong variants. Same
        // reasoning as UnitSweepMath's refusal to use UnityEngine.Rect.
    }
}
