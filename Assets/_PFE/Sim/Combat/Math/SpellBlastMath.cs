using System;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// AS3 <c>Spell.cast_blast</c> (<c>fe/unit/Spell.as:371-418</c>) — the <c>sp_blast</c> effect, as
    /// arithmetic.
    ///
    /// <para><b><c>sp_blast</c> deals no damage.</b> This is the single most important thing to know
    /// about it, and the thing a reader will get wrong: the row is
    /// <c>&lt;item id='sp_blast' dam='20' rad='500' …&gt;</c> (<c>AllData.as:4010</c>) and <c>dam</c> is
    /// <b>not</b> a damage figure — it is the <i>maximum velocity</i> the pulse can give a unit. The
    /// effect body never calls <c>damage()</c>; it assigns <c>dx</c>/<c>dy</c> and stops. The alicorn's
    /// <c>tr1</c> reflex therefore shoves the player away from a melee hit rather than hurting them, and
    /// a "fix" that adds <c>dam</c> HP of damage would turn a defensive reflex into a damage burst the
    /// oracle does not have.</para>
    ///
    /// <para><b>The formula, verbatim</b> (for each unit in <c>loc.units</c>):</para>
    /// <code>
    /// _loc2_ = u.X - X;                       // dx, px
    /// _loc3_ = u.Y - u.scY / 2 - Y;           // dy to the target's CENTRE, px
    /// _loc4_ = _loc2_² + _loc3_²;
    /// if(_loc4_ &lt;= rad²) {
    ///    _loc4_ = sqrt(_loc4_);               // now the distance
    ///    _loc5_ = dam * power * (1 - _loc4_/rad) * (random*0.4 + 0.8) * u.knocked / u.massa;
    ///    if(_loc5_ &gt; dam * power) _loc5_ = dam * power;
    ///    u.dx = _loc2_ / _loc4_ * _loc5_;    // ASSIGNMENT, not +=
    ///    u.dy = _loc3_ / _loc4_ * _loc5_;
    ///    u.stun += floor(random * power * dam);
    ///    u.t_throw = 30;
    /// }
    /// </code>
    ///
    /// <para><b>Unity-free on purpose</b>, like <see cref="AlicornVariantRules"/>: the executor is a
    /// <c>MonoBehaviour</c>-adjacent loop that cannot run in the offline harness, so the numbers it
    /// writes are pinned here instead.</para>
    /// </summary>
    public static class SpellBlastMath
    {
        /// <summary>
        /// <c>if(!(u.fixed || u.fraction == owner.fraction || !owner.isMeet(u)))</c> — who the pulse
        /// touches.
        /// </summary>
        /// <remarks>
        /// <para><b>Three refusals, and the third is the subtle one.</b> <c>fixed</c> is a bolted-down
        /// unit (a turret, an emplacement); <c>fraction == owner.fraction</c> is the caster's own side;
        /// and <c>isMeet</c> (<c>Unit.as:4522-4525</c>) is
        /// <c>u != null &amp;&amp; loc == u.loc &amp;&amp; !u.disabled &amp;&amp; !u.trigDis &amp;&amp; u.sost != 4
        /// &amp;&amp; u != this</c>.</para>
        ///
        /// <para><b>A corpse is a legal target.</b> <c>isMeet</c> excludes <c>sost == 4</c> (an unravel)
        /// and does not require <c>sost == 1</c>, so a body gets thrown by the pulse. That is why this
        /// takes <c>isUnraveled</c> rather than <c>isAlive</c> — "dead" is not "immune".</para>
        /// </remarks>
        /// <param name="isFixed">The target cannot be moved at all.</param>
        /// <param name="sameFaction">The target is on the caster's side.</param>
        /// <param name="sameRoom"><c>loc == u.loc</c>.</param>
        /// <param name="isUnraveled"><c>sost == 4</c>.</param>
        /// <param name="isSelf">The target is the caster.</param>
        public static bool IsEligibleTarget(bool isFixed, bool sameFaction, bool sameRoom, bool isUnraveled,
                                            bool isSelf)
            => !isFixed && !sameFaction && sameRoom && !isUnraveled && !isSelf;

        /// <summary>The radius test: <c>_loc4_ &lt;= rad * rad</c>, on squared distance.</summary>
        /// <remarks>
        /// <b>Inclusive at the rim</b> — <c>&lt;=</c>, not <c>&lt;</c>. A unit exactly 500 px away is
        /// pushed with an impulse of exactly zero, which is the same as not being pushed; the difference
        /// is only observable through the <c>stun</c>/<c>t_throw</c> side effects.
        /// </remarks>
        public static bool IsInRadius(float distanceSquared, float radius)
            => radius > 0f && distanceSquared <= radius * radius;

        /// <summary>
        /// <c>_loc5_</c> — the impulse magnitude in px/frame, before it is split into axes.
        /// </summary>
        /// <remarks>
        /// <para><b>Four terms, each doing something:</b> the linear falloff <c>(1 - d/rad)</c> makes the
        /// pulse weakest at the rim; <c>(random*0.4 + 0.8)</c> is a per-target 0.8-1.2 spread so a group
        /// does not fly as a rigid block; <c>knocked / massa</c> is the target's own susceptibility and
        /// weight; and the clamp at <c>dam * power</c> means the <i>centre</i> of the blast is the
        /// fastest anything can travel.</para>
        ///
        /// <para><b>The clamp is reachable, not decorative.</b> <c>knocked / massa</c> can exceed 1 —
        /// a light, easily-knocked unit — and <c>(1 - d/rad)</c> can be close to 1, so without the clamp
        /// a featherweight at the centre would take more than <c>dam * power</c>. With it, the impulse is
        /// capped at the row's <c>dam</c> times the caster's <c>spellPower</c> — 20 × 2 = 40 px/frame for
        /// the alicorn, whose <c>spellPower</c> is 2 (<c>UnitAlicorn.as:227</c>).</para>
        ///
        /// <para><b>Non-positive <c>massa</c> is treated as 1.</b> AS3 divides by it and would produce
        /// <c>Infinity</c> (then the clamp would catch it at <c>dam * power</c>, which is accidentally
        /// correct but for the wrong reason). The port's <see cref="KnockbackMath"/> already substitutes
        /// for a non-positive mass; this does the same so the two cannot disagree about what a
        /// massless unit weighs.</para>
        /// </remarks>
        /// <param name="damage">The row's <c>dam</c> — the impulse ceiling, not HP damage.</param>
        /// <param name="power">The caster's <c>spellPower</c>.</param>
        /// <param name="distance">Distance to the target's centre, px, already square-rooted.</param>
        /// <param name="radius">The row's <c>rad</c>.</param>
        /// <param name="roll">The 0.8-1.2 spread draw, in <c>[0,1)</c>.</param>
        /// <param name="knocked">The target's <c>knocked</c> multiplier.</param>
        /// <param name="massa">The target's mass.</param>
        public static float Impulse(float damage, float power, float distance, float radius, float roll,
                                    float knocked, float massa)
        {
            float ceiling = damage * power;
            if (ceiling <= 0f || radius <= 0f) return 0f;

            if (distance < 0f) distance = 0f;
            float falloff = 1f - distance / radius;
            if (falloff < 0f) falloff = 0f;

            float spread = roll * 0.4f + 0.8f;
            float weight = massa > 0f ? knocked / massa : knocked;

            float impulse = ceiling * falloff * spread * weight;
            return impulse > ceiling ? ceiling : impulse;
        }

        /// <summary>
        /// <c>dx = _loc2_/_loc4_ * _loc5_; dy = _loc3_/_loc4_ * _loc5_</c> — the impulse split along the
        /// unit vector from the blast centre to the target's centre.
        /// </summary>
        /// <remarks>
        /// <para><b>A zero distance is a no-op here and a NaN in AS3.</b> The oracle divides by
        /// <c>_loc4_</c> without a guard, so a unit standing exactly on the blast centre gets
        /// <c>Infinity * 0</c>. AS3's velocity is then NaN, which propagates into the position and
        /// usually teleports the unit off the map. Not reproduced: a unit at the centre takes no impulse,
        /// which is also what <c>falloff == 1</c> with a zero-length direction vector means
        /// geometrically.</para>
        /// </remarks>
        public static void Velocity(float deltaX, float deltaY, float distance, float impulse,
                                    out float vx, out float vy)
        {
            if (distance <= 0f || impulse <= 0f)
            {
                vx = 0f;
                vy = 0f;
                return;
            }

            vx = deltaX / distance * impulse;
            vy = deltaY / distance * impulse;
        }

        /// <summary>
        /// <c>u.stun += floor(random * power * dam)</c> — 0..<c>power*dam</c> ticks of stagger, on top of
        /// whatever the unit already carries.
        /// </summary>
        /// <remarks>
        /// <b>An <c>int</c> floor of a float product</b>, so for the alicorn (<c>power</c> 2, <c>dam</c>
        /// 20) it is 0..39 — and it can be exactly 0, which is why the impulse is the visible half of
        /// this effect and the stun is not.
        /// </remarks>
        public static int StunAdd(float power, float damage, float roll)
        {
            if (roll < 0f) roll = 0f;
            float value = roll * power * damage;
            return value <= 0f ? 0 : (int)Math.Floor(value);
        }

        /// <summary><c>u.t_throw = 30</c> (<c>Spell.as:402</c>) — the airborne/tumble window, in ticks.</summary>
        public const int ThrowTicks = 30;
    }
}
