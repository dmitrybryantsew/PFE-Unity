using System.Collections.Generic;

namespace PFE.Systems.Particles
{
    /// <summary>
    /// The one entry point every emitter call site uses — the port of AS3's
    /// <c>Emitter.emit(id, loc, x, y, params)</c> (<c>Emitter.as:141-152</c>).
    ///
    /// <para><b>Why this is a top-level seam and not part of the effect system.</b> This is the single
    /// most important structural decision in the workstream. The emitter is reached from <b>two
    /// unrelated caller families</b>:</para>
    /// <list type="bullet">
    /// <item><description><b>Effect-driven</b> — <c>fe.unit.Effect</c>, which emits <c>flame</c>,
    /// <c>poison</c>, <c>blind</c>, <c>kap</c>, <c>slow</c> and <c>blood</c> from <c>visEff()</c> and
    /// <c>stepEffect()</c>.</description></item>
    /// <item><description><b>Impact-driven</b> — <c>Unit.damage()</c>, <c>Bullet.explVis()</c>,
    /// <c>Spell</c>, <c>Grafon</c>, <c>Interact</c> and <c>Box</c>, for blood spray, weapon explosions,
    /// sparks and debris.</description></item>
    /// </list>
    /// <para>Roughly 90 call sites across 20 files. Putting this inside <c>PFE.Systems.Effects</c> is the
    /// natural-looking mistake and would make every weapon explosion unreachable from the effect system
    /// it does not belong to.</para>
    ///
    /// <para><b>Parts belong to the room, not to the emitter's caller.</b> AS3 adds each <c>Part</c> to
    /// the <b><c>Location</c></b> (<c>param1.addObj(_loc6_)</c>, <c>Emitter.as:362</c>), so particles are
    /// room-scoped and die with the room. A port that parents them to the unit will have muzzle smoke
    /// following the player around.</para>
    /// </summary>
    public interface IParticleWorld
    {
        /// <summary>
        /// Emits with no overrides — the common case, and the one that must not allocate.
        /// </summary>
        /// <returns>
        /// True when a definition was found and the cast was admitted. <b>False is not an error</b>: an
        /// unknown id and a budget refusal both return false, and the difference is visible through
        /// <see cref="UnknownIds"/> and the budget's drop counters rather than through the return value.
        /// </returns>
        bool Emit(string id, float x, float y);

        /// <summary>
        /// Emits with overrides — <c>kol</c>, <c>mirr</c>, <c>md</c> and the rest of
        /// <see cref="ParticleSpec"/>. The caller's spec is never mutated.
        /// </summary>
        bool Emit(string id, float x, float y, ParticleSpec overrides);

        /// <summary>True when the id resolves to a definition. For callers that want to test before emitting.</summary>
        bool Has(string id);

        /// <summary>
        /// Ids that were asked for and could not be resolved, in first-seen order. This is the port's
        /// answer to the oracle's <c>trace("Нет частицы " + id)</c> (<c>Emitter.as:150</c>).
        ///
        /// <para><b>Unknown ids must never throw and must never be silently swallowed.</b> A typo'd id
        /// has to look like a missing visual — which is exactly the failure this project keeps hitting
        /// and cannot see. Exposing the set is what makes the difference between "the effect system
        /// records it as unmapped" and "nothing happened".</para>
        /// </summary>
        IReadOnlyCollection<string> UnknownIds { get; }

        /// <summary>
        /// Advances the two-slot budget register — AS3 <c>kol2 = kol1; kol1 = 0;</c>
        /// (<c>World.as:1266-1267</c>). Must be called exactly once per simulation tick, before any
        /// emitter runs; the global ceiling compares against the previous tick's live count, so a burst
        /// cannot make itself legal by draining the counter mid-tick.
        /// </summary>
        void BeginTick();
    }
}
