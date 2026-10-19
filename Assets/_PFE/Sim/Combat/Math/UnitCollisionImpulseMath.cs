using UnityEngine;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// The momentum exchange of AS3 <c>Unit.udarUnit()</c> — <c>fe/unit/Unit.as:4151-4159</c> — the
    /// second of the three places the oracle moves a unit without an explosion.
    ///
    /// <para><b>The three, and how they differ.</b> This file is deliberately separate from
    /// <see cref="KnockbackMath"/> because the oracle's own three sites are separate:</para>
    /// <list type="number">
    /// <item><description><c>Unit.otbros(bullet)</c> (<c>:4242-4260</c>) — a <b>weapon's</b> knock. An
    /// <i>additive</i> impulse scaled by <c>knocked / massa</c>, ported as
    /// <see cref="KnockbackMath"/>. This is "knocked back by the damage".</description></item>
    /// <item><description><c>Unit.udarUnit(attacker)</c> (<c>:4125-4205</c>) — <b>one unit striking
    /// another</b>: a claw, a bite, a body-check. No weapon knock is involved at all; the two bodies
    /// exchange velocity about their common centre of mass. This file.</description></item>
    /// <item><description><c>Unit.udarBox(prop)</c> (<c>:4209-4240</c>) — the same exchange against a
    /// physics prop, with the prop's reflection factor hard-coded to <c>0.25</c> instead of the other
    /// body's <c>knocked</c>. Already ported as <c>PropImpactMath.ResolveExchange</c>.</description></item>
    /// </list>
    ///
    /// <para><b>Why a contact attack has no <c>otbros</c> at all.</b> <c>udarUnit</c> never calls
    /// <c>otbros()</c> — it calls <c>damage()</c> directly (<c>:4162</c>/<c>:4166</c>). So a zombie's
    /// claw carries no weapon knock even when the zombie holds a <c>tip='1'</c> weapon; the shove it
    /// delivers is this exchange and nothing else. A port that "helpfully" added the weapon's knock to
    /// a contact hit would be inventing a number the oracle never applies.</para>
    ///
    /// <para><b>The oracle.</b></para>
    /// <code>
    /// // Unit.udarUnit (Unit.as:4150-4159). `this` is the TARGET, `param1` is the ATTACKER.
    /// _loc3_ = Math.random() * 0.4 + 0.8;            // :4150 -- the damage spread, drawn FIRST
    /// if(param1.collisionTip == 1)                   // :4151 -- read on the ATTACKER
    /// {
    ///    _loc5_ = (param1.dx * param1.massa + dx * massa) / (param1.massa + massa);   // :4153
    ///    _loc6_ = (param1.dy * param1.massa + dy * massa) / (param1.massa + massa);
    ///    dx      = (-dx      + _loc5_) * this.knocked   + _loc5_;                     // :4155
    ///    dy      = (-dy      + _loc6_) * this.knocked   + _loc6_;
    ///    param1.dx = (-param1.dx + _loc5_) * param1.knocked + _loc5_;                 // :4157
    ///    param1.dy = (-param1.dy + _loc6_) * param1.knocked + _loc6_;
    /// }
    /// // :4162/:4166 -- damage() runs AFTER the exchange
    /// </code>
    ///
    /// <para><b>Read it as "each body reflects about the centre of mass, scaled by its own
    /// <c>knocked</c>".</b> <c>com + knocked·(com − v)</c> is a mirror of <c>v</c> about <c>com</c>
    /// when <c>knocked = 1</c>, and a collapse onto <c>com</c> when <c>knocked = 0</c>. So:</para>
    /// <list type="bullet">
    /// <item><description>Two ordinary bodies (<c>knocked = 1</c>, equal mass) <b>swap</b> velocities —
    /// a zombie charging a standing player stops and the player is thrown at the zombie's speed.</description></item>
    /// <item><description>A turret or a <c>fixed</c> unit (<c>knocked = 0</c>) does not move, and the
    /// attacker's velocity becomes the centre of mass — it bounces off a statue rather than through
    /// it.</description></item>
    /// <item><description>A heavy attacker (<c>massa</c> large) barely changes and throws the light
    /// target far, because <c>com</c> sits near the attacker's own velocity.</description></item>
    /// </list>
    ///
    /// <para><b>Per axis, not along the contact normal.</b> x and y are resolved independently, each
    /// about its own component of the centre of mass. A diagonal body-check therefore exchanges on both
    /// axes at once rather than along the line between the two bodies. That is not what a physics
    /// engine would do, and "correcting" it changes every contact hit in the game. Ported as
    /// written — the same note <c>PropImpactMath</c> carries for its own copy of this arithmetic.</para>
    ///
    /// <para><b>No <c>fixed</c> branch, unlike <c>udarBox</c>.</b> <c>udarUnit</c> has no
    /// <c>if(!this.fixed)</c> around the exchange, so a pinned unit's <c>dx</c>/<c>dy</c> really are
    /// rewritten; it just never integrates them (<c>Unit.as:1809</c> gates <c>run()</c>). The port does
    /// the same and lets <c>UnitController.IsFixed</c> gate the position write, which is where the
    /// oracle gates it.</para>
    ///
    /// <para><b>No RNG.</b> The spread draw at <c>:4150</c> belongs to the damage, is taken by
    /// <c>DamageSystem.RollContactAttackVariance</c>, and happens <i>before</i> this exchange. Nothing
    /// here consumes a draw, which is why moving the exchange ahead of or behind that draw would not
    /// desynchronise the shared combat stream — the order is kept as the oracle has it anyway.</para>
    /// </summary>
    public static class UnitCollisionImpulseMath
    {
        /// <summary>
        /// AS3 <c>Unit.collisionTip</c> (<c>Unit.as:196</c>, default <c>1</c>) — whether this unit
        /// exchanges momentum when it strikes another.
        /// </summary>
        /// <remarks>
        /// <para><b>Declared <c>int</c> in AS3 and modelled as <c>bool</c> here, deliberately.</b> The
        /// field is only ever assigned <c>0</c> — by six subclasses, each in its constructor
        /// (<c>UnitBossDron.as:60</c>, <c>UnitNecros.as:29</c>, <c>UnitSlime.as:65</c>,
        /// <c>UnitSpectre.as:33</c>, <c>UnitScythe.as:44</c>, <c>UnitThunderHead.as:147</c>) — and only
        /// ever read by the one comparison <c>== 1</c>. A port that carried the <c>int</c> would be
        /// carrying two states (<c>1</c> and <c>0</c>) that mean "yes" and "no", plus a third that never
        /// occurs. <c>bool</c> says the same thing with no unreachable value.</para>
        ///
        /// <para><b>The five that are ported, and why the sixth is not.</b> Slime, spectre, necros,
        /// megadron and thunderhead all answer <c>false</c> on their controllers.
        /// <c>UnitScythe</c> is not ported at all — it is a self-propelled blade entity whose
        /// <c>forces()</c> is empty (<c>UnitScythe.as:56-58</c>), not an enemy archetype — so there is
        /// no controller to override. That is a recorded gap, not an oversight.</para>
        ///
        /// <para><b>Why the gate is on the ATTACKER.</b> <c>param1</c> is the striker, so a slime that
        /// claws a player pushes nobody, while a player who walks into a slime still gets the full
        /// exchange from the player's side. Reading it on the target instead would make every slime in
        /// the game immovable and every attacker of a slime recoil — both wrong, and both plausible
        /// enough to survive a play-test.</para>
        /// </remarks>
        public const bool CollisionTipDefault = true;

        /// <summary>
        /// AS3 <c>Unit.as:4153-4158</c> — the two new velocities, in <b>pixels per 30 Hz frame</b> on
        /// both sides, from the two pre-collision ones.
        /// </summary>
        /// <param name="attackerVelocityPixelsPerFrame">The striker's <c>dx</c>/<c>dy</c>.</param>
        /// <param name="attackerMassa">The striker's AS3 <c>massa</c> (<c>UnitDefinition.Massa</c>).</param>
        /// <param name="attackerKnocked">The striker's AS3 <c>knocked</c> — its own reflection scale.</param>
        /// <param name="targetVelocityPixelsPerFrame">The target's <c>dx</c>/<c>dy</c>.</param>
        /// <param name="targetMassa">The target's AS3 <c>massa</c>.</param>
        /// <param name="targetKnocked">The target's AS3 <c>knocked</c>.</param>
        public static UnitCollisionExchange ResolveExchange(
            Vector2 attackerVelocityPixelsPerFrame,
            float attackerMassa,
            float attackerKnocked,
            Vector2 targetVelocityPixelsPerFrame,
            float targetMassa,
            float targetKnocked)
        {
            float totalMass = attackerMassa + targetMassa;

            // AS3 divides by `param1.massa + massa` with no guard, so a pair whose masses sum to zero
            // hands both bodies `Infinity` or `NaN`. In AS3 that is unreachable — `Obj.massa` is
            // declared 1 (<c>Obj.as:28</c>) and `Unit.as:1058` only ever assigns `massaFix`, itself a
            // defaulted 1 — but the port's importer stores a raw attribute, so a malformed row could
            // reach it. A NaN velocity is not a behaviour: it propagates into the unit's position and
            // the unit is gone for the rest of the run, silently. Leaving both bodies untouched is the
            // closest non-destructive reading, and the same choice `PropImpactMath` makes.
            if (!(totalMass > 0f))
            {
                return new UnitCollisionExchange(
                    attackerVelocityPixelsPerFrame,
                    targetVelocityPixelsPerFrame);
            }

            // `_loc5_`/`_loc6_`, captured BEFORE either body is written — both reflections read the
            // pre-collision velocities, so this must not be recomputed mid-update.
            Vector2 centreOfMassVelocity =
                (attackerVelocityPixelsPerFrame * attackerMassa
                 + targetVelocityPixelsPerFrame * targetMassa) / totalMass;

            Vector2 newTargetVelocity =
                (-targetVelocityPixelsPerFrame + centreOfMassVelocity) * targetKnocked
                + centreOfMassVelocity;

            Vector2 newAttackerVelocity =
                (-attackerVelocityPixelsPerFrame + centreOfMassVelocity) * attackerKnocked
                + centreOfMassVelocity;

            return new UnitCollisionExchange(newAttackerVelocity, newTargetVelocity);
        }
    }

    /// <summary>
    /// The two velocities AS3 <c>Unit.udarUnit</c> writes, in <b>pixels per 30 Hz frame</b> — the
    /// attacker's new <c>dx</c>/<c>dy</c> and the target's. A value type so the exchange is assertable
    /// without a GameObject, the same reason <c>PropImpactExchange</c> and <c>DamageOutcome</c> are.
    /// </summary>
    public readonly struct UnitCollisionExchange
    {
        /// <summary>The striker's new velocity — AS3's <c>param1.dx</c>/<c>dy</c> after <c>:4157</c>.</summary>
        public readonly Vector2 AttackerVelocityPixelsPerFrame;

        /// <summary>The target's new velocity — AS3's <c>this.dx</c>/<c>dy</c> after <c>:4155</c>.</summary>
        public readonly Vector2 TargetVelocityPixelsPerFrame;

        public UnitCollisionExchange(
            Vector2 attackerVelocityPixelsPerFrame,
            Vector2 targetVelocityPixelsPerFrame)
        {
            AttackerVelocityPixelsPerFrame = attackerVelocityPixelsPerFrame;
            TargetVelocityPixelsPerFrame = targetVelocityPixelsPerFrame;
        }
    }
}
