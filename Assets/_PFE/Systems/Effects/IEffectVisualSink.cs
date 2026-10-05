namespace PFE.Systems.Effects
{
    /// <summary>
    /// Where an effect's <b>visuals</b> go — the seam that lets the effect runtime ask for a flame
    /// without knowing anything about particles, transforms or rooms.
    ///
    /// <para><b>Why a second interface beside <see cref="IEffectHost"/>.</b> <see cref="IEffectHost"/>
    /// is what the effect <i>system</i> needs from a unit: apply the params, run the payload, announce
    /// the end. All of it is simulation. This interface is what the effect needs for
    /// <i>presentation</i> — and presentation is a different owner: the payload's damage is decided by
    /// <see cref="UnitStats"/>, but a flame has to be anchored on a sprite and emitted into a room, and
    /// neither exists on a pure stats object.</para>
    ///
    /// <para><b>Two methods, because the oracle has two cadences.</b> <c>Effect.stepEffect()</c>
    /// (<c>Effect.as:474-488</c>) runs <b>every tick</b>; <c>Effect.secEffect()</c> (<c>:405-472</c>)
    /// runs <b>once per 30 canonical frames</b>, gated by <c>t % 30 == 0</c> in <c>step()</c>
    /// (<c>:492</c>). Collapsing them into one method with a flag would move the decision into every
    /// implementer; keeping them apart makes the cadence the caller's, where the oracle puts it.</para>
    ///
    /// <para><b>An implementation is optional.</b> A unit with no sink — a headless test, a unit whose
    /// spawner installed nothing — runs its effects exactly as before and simply shows nothing. That is
    /// the same shape as <c>UnitStats.EffectPayloadSink</c> and is deliberate: a missing sink must not be
    /// an exception, because most of the game's units are not on screen.</para>
    /// </summary>
    public interface IEffectVisualSink
    {
        /// <summary>
        /// The once-per-tick visuals for <paramref name="effect"/> — AS3 <c>Effect.stepEffect()</c>
        /// (<c>Effect.as:474-488</c>). Called <b>every</b> simulation tick the effect is alive.
        ///
        /// <para>An implementation decides what to draw by delegating to
        /// <c>PFE.Systems.Particles.EffectVisualRules.PlanStepVisual</c>, which owns the ids, the gates
        /// and the offsets. The sink's job is only to supply the owner's presentation state and to emit
        /// what comes back.</para>
        /// </summary>
        void OnEffectStepVisual(ActiveEffect effect);

        /// <summary>
        /// The once-per-second visuals for <paramref name="effect"/> — AS3 <c>Effect.secEffect()</c>
        /// (<c>Effect.as:405-472</c>). Called on the same canonical 30-frame boundary the payload runs
        /// on, so the drip and the damage it accompanies land in the same frame.
        ///
        /// <para>Called for <b>every</b> payload tick, not only for the ids that emit. Most ids emit
        /// nothing here — <c>burning</c> is the notable one, whose flame is on the <i>step</i> path —
        /// and the rules return false for them. Gating at the call site instead would duplicate the id
        /// list in two places.</para>
        /// </summary>
        void OnEffectPayloadVisual(ActiveEffect effect);
    }
}
