using UnityEngine;
using PFE.Core;

namespace PFE.Systems.Physics
{
    /// <summary>
    /// The arithmetic of AS3 <c>Unit.udarBox</c> (<c>fe/unit/Unit.as:4209-4240</c>) and the gate that
    /// decides whether it runs at all (<c>fe/loc/Box.as:914-940</c>, <c>attDrop</c>), as pure functions
    /// of their arguments.
    ///
    /// <para><b>Why extracted.</b> Same reason as <see cref="UnitFallPhysics"/>: while the arithmetic
    /// lives inline inside a <c>MonoBehaviour</c> that needs a GameObject, a collider and a running
    /// engine, pinning it needs all three, so the test never gets written and the value drifts. Every
    /// number here is one that inverts into something plausible.</para>
    ///
    /// <para><b>The oracle.</b></para>
    /// <code>
    /// // Box.attDrop (Box.as:914-940), called from :632 when the box is moving fast and not carried
    /// this.vel2 = dx * dx + dy * dy;              // :917 -- px per FRAME
    /// if(this.vel2 &lt; 50) return;                 // :918
    /// for each(_loc1_ in loc.units) {
    ///    if(overlaps &amp;&amp; !dead &amp;&amp; _loc1_.neujaz &lt;= 0) {
    ///       if(this.t_throw &gt; 0) _loc1_.neujaz = 12;   // :930 -- grace, no damage
    ///       else { _loc1_.udarBox(this); this.isThrow = false; }
    ///    }
    /// }
    ///
    /// // Unit.udarBox (Unit.as:4209-4240)
    /// if(this.neujaz &gt; 0 || this.noBox || param1.loc != loc) return 0;
    /// this.neujaz = this.neujazMax;
    /// if(!this.fixed) {
    ///    _loc2_ = (param1.dx * param1.massa + dx * massa) / (param1.massa + massa);   // :4225
    ///    _loc3_ = (param1.dy * param1.massa + dy * massa) / (param1.massa + massa);
    ///    dx = (-dx + _loc2_) * this.knocked + _loc2_;                                // :4227
    ///    dy = (-dy + _loc3_) * this.knocked + _loc3_;
    ///    param1.dx = (-param1.dx + _loc2_) * 0.25 + _loc2_;                          // :4229
    ///    param1.dy = (-param1.dy + _loc3_) * 0.25 + _loc3_;
    /// } else {
    ///    param1.dx *= 0.5;  param1.dy *= 0.5;                                        // :4234
    /// }
    /// this.damage(param1.massa * (param1.vel2 - 50) * World.boxDamage, D_PHIS);       // :4237
    /// </code>
    ///
    /// <para><b>The damage reads the PRE-collision velocity, and that is not a bug to fix.</b>
    /// <c>param1.vel2</c> is computed once at <c>Box.as:917</c> and never recomputed, so the exchange
    /// above (which rewrites <c>param1.dx</c>/<c>dy</c> at <c>:4229-4230</c>) does not feed back into
    /// the damage. Reading the post-collision velocity here would make every impact weaker in
    /// proportion to how much of it the box kept — a plausible-looking "fix" that is a behaviour
    /// change.</para>
    /// </summary>
    public static class PropImpactMath
    {
        /// <summary>
        /// AS3 <c>World.boxDamage = 0.2</c> (<c>World.as:64</c>) — the scale on the whole formula.
        /// </summary>
        public const float BoxDamageCoefficient = 0.2f;

        /// <summary>
        /// AS3 <c>Box.as:918</c> — <c>if(this.vel2 &lt; 50) return;</c>. In <b>px per frame squared</b>,
        /// so this is a speed of 7.07 px/frame (≈212 px/s), not 50 of anything else. Comparing it
        /// against the prop layer's px/second velocity would make the threshold 900× too small and
        /// every settling crate would damage whatever it touched.
        /// </summary>
        public const float MinimumImpactVelocitySquared = 50f;

        /// <summary>
        /// AS3 <c>Box.as:632</c> — <c>if(!levit &amp;&amp; (dy &gt; 5 || dy &lt; -5 || dx &gt; 5 || dx &lt; -5))
        /// this.attDrop();</c>. The pre-filter that gates the whole sweep, and it is <b>strict</b> on
        /// all four comparisons: a box at exactly 5 px/frame on an axis does <b>not</b> sweep, and
        /// neither does one at exactly −5.
        ///
        /// <para><b>It is <i>not</i> redundant with the <c>vel2</c> gate, and the difference is one
        /// point.</b> <c>|dx| ≤ 5 &amp;&amp; |dy| ≤ 5</c> bounds <c>vel2</c> by <c>2 × 5² = 50</c>, so a box
        /// refused here would almost always be refused at <c>:918</c> too — but that gate is
        /// <c>vel2 &lt; 50</c>, i.e. <b>inclusive</b>, so at the single corner
        /// <c>|dx| = |dy| = 5</c> exactly, <c>vel2</c> is exactly 50 and <i>would</i> pass. This gate
        /// refuses the sweep before <c>:918</c> is ever reached, so the two together are stricter than
        /// either alone by that one point. An earlier revision of this comment claimed this was a pure
        /// short-circuit; it is not, and a caller that checks only <see cref="IsImpactHardEnough"/> is
        /// looser than the oracle. Ported as written.</para>
        /// </summary>
        public const float ImpactSweepMinimumSpeedPixelsPerFrame = 5f;

        /// <summary>
        /// AS3 <c>Box.as:930</c> — <c>_loc1_.neujaz = 12;</c>. A unit clipped by a prop that is still in
        /// its throw window gets this many ticks of contact invulnerability <b>instead of</b> damage, so
        /// the crate you just threw does not hurt whoever it passes through. A hard-coded literal in the
        /// oracle, not <c>neujazMax</c> — the two are different numbers (12 vs 20) and using
        /// <c>neujazMax</c> here would make the grace 67% longer.
        /// </summary>
        public const int ThrowGraceInvulnerabilityTicks = 12;

        /// <summary>
        /// The prop layer integrates in <b>pixels per second</b> (see
        /// <c>RoomObjectPhysicsLayer.GravityPixelsPerSecond</c>) because its step is a real
        /// <c>deltaTime</c>; AS3's arithmetic is in <b>pixels per 30 Hz frame</b>. Every comparison and
        /// formula in this file is in AS3's unit, so the conversion happens once, here, at the boundary.
        /// </summary>
        public static Vector2 ToPixelsPerFrame(Vector2 velocityPixelsPerSecond)
        {
            return velocityPixelsPerSecond / SimClock.CanonicalTicksPerSecond;
        }

        /// <summary>The inverse of <see cref="ToPixelsPerFrame"/>.</summary>
        public static Vector2 FromPixelsPerFrame(Vector2 velocityPixelsPerFrame)
        {
            return velocityPixelsPerFrame * SimClock.CanonicalTicksPerSecond;
        }

        /// <summary>
        /// AS3's <c>vel2</c> (<c>Box.as:917</c>) — <c>dx*dx + dy*dy</c> in px per frame squared.
        /// </summary>
        public static float VelocitySquaredPixelsPerFrame(Vector2 velocityPixelsPerSecond)
        {
            return VelocitySquaredFromPerFrame(ToPixelsPerFrame(velocityPixelsPerSecond));
        }

        /// <summary>
        /// The same quantity when the caller <b>already holds</b> the per-frame vector — which a
        /// caller that had to test <see cref="IsMovingFastEnoughToSweep"/> does, since that gate is
        /// per-frame too.
        ///
        /// <para>Exists so the formula lives in exactly one place: converting twice, or writing
        /// <c>x*x + y*y</c> a second time at the call site, is how one of the two copies eventually
        /// acquires a <c>+</c> where it wants a <c>*</c>.</para>
        /// </summary>
        public static float VelocitySquaredFromPerFrame(Vector2 velocityPixelsPerFrame)
        {
            return velocityPixelsPerFrame.x * velocityPixelsPerFrame.x
                 + velocityPixelsPerFrame.y * velocityPixelsPerFrame.y;
        }

        /// <summary>AS3 <c>Box.as:918</c> — is this prop moving fast enough to hurt anything.</summary>
        public static bool IsImpactHardEnough(float velocitySquaredPixelsPerFrame)
        {
            return velocitySquaredPixelsPerFrame >= MinimumImpactVelocitySquared;
        }

        /// <summary>AS3 <c>Box.as:632</c> — the pre-filter that gates the whole sweep.</summary>
        public static bool IsMovingFastEnoughToSweep(Vector2 velocityPixelsPerFrame)
        {
            return velocityPixelsPerFrame.x > ImpactSweepMinimumSpeedPixelsPerFrame
                || velocityPixelsPerFrame.x < -ImpactSweepMinimumSpeedPixelsPerFrame
                || velocityPixelsPerFrame.y > ImpactSweepMinimumSpeedPixelsPerFrame
                || velocityPixelsPerFrame.y < -ImpactSweepMinimumSpeedPixelsPerFrame;
        }

        /// <summary>
        /// AS3 <c>Unit.as:4237</c> — <c>param1.massa * (param1.vel2 - 50) * World.boxDamage</c>.
        ///
        /// <para><b>Both inputs are AS3-scale.</b> <paramref name="propMassaAs3"/> is
        /// <c>ObjectInstance.GetAs3Massa()</c> (already ÷50); passing <c>GetResolvedMass()</c> would
        /// multiply the damage by 50. <paramref name="velocitySquaredPixelsPerFrame"/> is AS3's
        /// <c>vel2</c>, from <see cref="VelocitySquaredPixelsPerFrame"/>.</para>
        ///
        /// <para>Zero when the impact is too soft, so the caller needs no separate gate — but note the
        /// oracle's gate is at the <i>sweep</i> level (<c>:918</c>), before any unit is looked at, so a
        /// caller that wants AS3's behaviour exactly must gate there too rather than relying on this
        /// returning 0.</para>
        ///
        /// <para><b>Clamping to 0 rather than returning a negative is deliberate.</b> A <c>vel2</c>
        /// below the threshold makes <c>(vel2 − 50)</c> negative, and AS3's <c>damage()</c> treats a
        /// negative amount as <i>healing</i> (<c>Unit.as:3605-3608</c>: <c>if(param1 &lt; 0)
        /// { this.heal(-param1); return 0; }</c>). That branch is unreachable in the oracle because
        /// <c>:918</c> returns first, so a caller that skips the sweep gate would otherwise be handing
        /// out a heal where AS3 hands out nothing.</para>
        /// </summary>
        public static float ImpactDamage(float propMassaAs3, float velocitySquaredPixelsPerFrame)
        {
            if (propMassaAs3 <= 0f || velocitySquaredPixelsPerFrame < MinimumImpactVelocitySquared)
            {
                return 0f;
            }

            return propMassaAs3 * (velocitySquaredPixelsPerFrame - MinimumImpactVelocitySquared)
                * BoxDamageCoefficient;
        }

        /// <summary>
        /// AS3 <c>Unit.as:4223-4236</c> — the momentum exchange, in <b>px per frame</b> on both sides.
        ///
        /// <para><b>It is applied per axis, not along the contact normal.</b> The oracle computes a
        /// centre-of-mass velocity for x and for y independently and reflects each axis about its own
        /// component. That is not what a physics engine would do — a diagonal hit does not exchange
        /// along the diagonal — but it is what the game does, and "correcting" it changes every
        /// knockback in the game. Ported as written.</para>
        ///
        /// <para><b>Two asymmetries, both the oracle's.</b> The unit's reflection is scaled by its own
        /// <c>knocked</c> (so a heavy-footed unit barely moves) and the prop's by a flat <b>0.25</b>. And
        /// a <c>fixed</c> unit does not participate at all: the prop simply halves its velocity and the
        /// unit does not move (<c>:4232-4236</c>).</para>
        /// </summary>
        /// <param name="unitVelocityPixelsPerFrame">The unit's velocity, AS3's <c>dx</c>/<c>dy</c>.</param>
        /// <param name="unitMassa">AS3-scale unit mass (<c>UnitDefinition.Massa</c>).</param>
        /// <param name="unitKnocked">AS3 <c>Unit.knocked</c> — the unit's susceptibility to being moved.</param>
        /// <param name="unitIsFixed">AS3 <c>Unit.fixed</c> — the unit takes no displacement.</param>
        /// <param name="propVelocityPixelsPerFrame">The prop's velocity.</param>
        /// <param name="propMassa">AS3-scale prop mass (<c>ObjectInstance.GetAs3Massa()</c>).</param>
        public static PropImpactExchange ResolveExchange(
            Vector2 unitVelocityPixelsPerFrame,
            float unitMassa,
            float unitKnocked,
            bool unitIsFixed,
            Vector2 propVelocityPixelsPerFrame,
            float propMassa)
        {
            // AS3's `else` branch (`:4232-4236`). The unit is a statue; the crate bounces off it.
            // A non-positive total mass lands here too: AS3 would divide by zero and hand both bodies
            // NaN, which then propagates into their positions and destroys them. Halving the prop is
            // the closest non-destructive reading.
            if (unitIsFixed || unitMassa + propMassa <= 0f)
            {
                return new PropImpactExchange(unitVelocityPixelsPerFrame, propVelocityPixelsPerFrame * 0.5f);
            }

            float totalMass = unitMassa + propMassa;

            // `_loc2_`/`_loc3_`, captured BEFORE either body is written — both reflections below read
            // the pre-collision velocities, so this must not be recomputed mid-update.
            Vector2 centreOfMassVelocity =
                (propVelocityPixelsPerFrame * propMassa + unitVelocityPixelsPerFrame * unitMassa) / totalMass;

            Vector2 newUnitVelocity =
                (-unitVelocityPixelsPerFrame + centreOfMassVelocity) * unitKnocked + centreOfMassVelocity;

            Vector2 newPropVelocity =
                (-propVelocityPixelsPerFrame + centreOfMassVelocity) * 0.25f + centreOfMassVelocity;

            return new PropImpactExchange(newUnitVelocity, newPropVelocity);
        }
    }

    /// <summary>
    /// The two velocities AS3 <c>Unit.udarBox</c> writes, in <b>pixels per 30 Hz frame</b> — the unit's
    /// new <c>dx</c>/<c>dy</c> and the prop's. A value type so the exchange is assertable without a
    /// GameObject, the same reason <c>DamageOutcome</c> is one.
    /// </summary>
    public readonly struct PropImpactExchange
    {
        public readonly Vector2 UnitVelocityPixelsPerFrame;
        public readonly Vector2 PropVelocityPixelsPerFrame;

        public PropImpactExchange(Vector2 unitVelocityPixelsPerFrame, Vector2 propVelocityPixelsPerFrame)
        {
            UnitVelocityPixelsPerFrame = unitVelocityPixelsPerFrame;
            PropVelocityPixelsPerFrame = propVelocityPixelsPerFrame;
        }
    }
}
