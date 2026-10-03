using System;
using PFE.Data.Definitions;
using PFE.Systems.Effects;

namespace PFE.Systems.Combat
{
    /// <summary>
    /// A target that can carry status effects — the seam <see cref="DamageSystem"/> uses to run AS3's
    /// on-hit effect producers.
    ///
    /// <para><b>Why a separate interface instead of widening <see cref="IDamageable"/>.</b> AS3's
    /// <c>Unit.damage()</c> does its on-hit effect work through <c>this.addEffect(...)</c>, which only
    /// a <c>Unit</c> has. The port's <see cref="IDamageable"/> is implemented by crates, turrets and
    /// many test doubles; putting an effect set on all of them would either force every fake to grow a
    /// stub or, worse, let a hit on a crate try to burn it. A nullable capability keeps the producer
    /// honest: no receiver means no effects, which is the oracle's behaviour for a non-<c>Unit</c>
    /// target.</para>
    ///
    /// <para><b>Only the three data channels the oracle has are exposed.</b> AS3's block also bumps
    /// plain fields (<c>poison</c>, <c>cut</c>, <c>stun</c>) which are <i>not</i> effects — those are
    /// separate members, listed below, because a port that funnelled them into the effect set would
    /// create effects the oracle never creates.</para>
    /// </summary>
    public interface IEffectReceiver
    {
        /// <summary>
        /// The live effect set. Never <c>null</c> for an implementer — an implementer with no effects
        /// should simply not implement this interface.
        /// </summary>
        ActiveEffectSet Effects { get; }

        /// <summary>
        /// Whether this target may receive a given damage type at all — AS3's
        /// <c>this.vulner[D_X] &gt; 0.1</c> guard on every <c>dopEffect</c> branch
        /// (<c>Unit.as:3774-3808</c>).
        ///
        /// <para><b>The guard is not decorative.</b> An EMP-immune unit has <c>vulner[D_EMP] = 0</c> by
        /// default, so the <c>&gt; 0.1</c> test is what stops a shock weapon from applying a status to
        /// something the oracle considers immune. Note the threshold is <c>0.1</c>, not <c>0</c>: a
        /// target with a tiny-but-nonzero vulnerability is also skipped.</para>
        /// </summary>
        bool IsSusceptibleTo(DamageType type);
    }
}
