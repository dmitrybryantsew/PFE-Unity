using PFE.Systems.Weapons;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// A target that gets a look at the <b>shot</b>, not merely at the number — the seam AS3's
    /// <c>Unit.damage(param1, param2, param3)</c> has and the port's
    /// <c>IDamageable.ApplyDamage(DamageOutcome)</c> does not.
    ///
    /// <para><b>Why this exists.</b> Three of the alicorn's variant behaviours are gated on
    /// <i>what hit it</i>, not on how hard:</para>
    /// <list type="bullet">
    ///   <item><description><c>tr1</c>'s reactive blast fires only for <c>param3.weap.tip == 1</c>
    ///     (<c>UnitAlicorn.as:417</c>) — a melee weapon, not a bolt.</description></item>
    ///   <item><description><c>tr2</c>'s reactive escape fires only when the shot's owner is the
    ///     <b>player</b> (<c>:422</c>), so a turret or a fellow monster cannot trigger it.</description></item>
    ///   <item><description>Both are also gated on <c>aiState &lt;= 1</c> and fire once per life.</description></item>
    /// </list>
    /// <para>None of those three facts survives the trip through <c>DamageOutcome</c>, which carries
    /// only numbers. Rather than widen <c>IDamageable</c> (every prop, corpse and trap implements it),
    /// this is a second, optional interface that the resolver asks <i>if the target has one</i>.</para>
    ///
    /// <para><b>Called after the spread and vulnerability, before armour and crit.</b> That is where
    /// AS3's <c>UnitAlicorn.damage()</c> override sits: it runs at the top of <c>damage()</c>, which
    /// <c>udarBullet</c> has already spread (<c>Unit.as:4085</c>). Placing the call any earlier would
    /// skip the spread draw and shift every later roll in the tick.</para>
    /// </summary>
    public interface IDamageContextSink
    {
        /// <summary>
        /// Inspect an incoming hit before it is applied.
        /// </summary>
        /// <returns>
        /// <c>true</c> when the target <b>refused</b> the hit — the resolver then applies nothing and
        /// reports <see cref="DamageVerdict.Refused"/>. <c>false</c> to let it land normally.
        /// </returns>
        bool OnDamageContext(in DamageContext context);
    }
}
