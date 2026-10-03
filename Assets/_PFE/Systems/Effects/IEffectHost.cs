using System.Collections.Generic;
using PFE.Data.Definitions;

namespace PFE.Systems.Effects
{
    /// <summary>
    /// What the effect system needs from the unit it is attached to.
    ///
    /// <para><b>Why an interface and not a reference to a <c>MonoBehaviour</c>.</b> AS3's
    /// <c>Effect</c> holds <c>owner:Unit</c> and calls <c>this.owner.damage(...)</c> directly. The port
    /// cannot: <see cref="ActiveEffectSet"/> is deliberately pure C# so the merge rules, the tick
    /// cadence and the level escalation are testable without an editor or a scene. The interface is the
    /// seam — one implementation bridges to the live unit, a fake implementation drives the tests.</para>
    ///
    /// <para><b>Every callback mirrors a named oracle site</b>, so a missing implementation is
    /// traceable rather than vague:</para>
    /// <list type="table">
    /// <item><term><see cref="OnEffectStarted"/></term><description><c>Effect.setEff()</c>
    /// (<c>Effect.as:139-220</c>) — the <c>del</c> sweep, the param trigger, the visual hooks. Only
    /// the param trigger is mandatory in the port's first pass.</description></item>
    /// <item><term><see cref="OnEffectEnded"/></term><description><c>Effect.unsetEff()</c>'s tail
    /// (<c>:337-403</c>) — the paired visual teardown.</description></item>
    /// <item><term><see cref="OnEffectPayload"/></term><description><c>Effect.secEffect()</c>
    /// (<c>:405-472</c>) — the once-per-second damage/heal/emitter payload.</description></item>
    /// <item><term><see cref="OnEffectParamsChanged"/></term><description>the param pass the oracle
    /// triggers from <c>setEff</c>, <c>unsetEff</c> and <c>checkT</c>. The set asks; the host owns the
    /// recompute because the target object (player vs NPC) differs.</description></item>
    /// </list>
    /// </summary>
    public interface IEffectHost
    {
        /// <summary>
        /// The set that "owns" this host — supplied on every callback so a host with more than one
        /// effect set (a unit plus, say, a temporary pet) can tell them apart, and so the callback
        /// signatures stay stable if that ever happens.
        /// </summary>
        void OnEffectStarted(ActiveEffect effect, ActiveEffectSet set);

        /// <summary>An effect is truly leaving — it did not retarget to an aftereffect.</summary>
        void OnEffectEnded(ActiveEffect effect, ActiveEffectSet set);

        /// <summary>
        /// Run this effect's per-second payload — AS3 <c>Effect.secEffect</c>.
        ///
        /// <para><b>The host dispatches on id</b>, exactly as the oracle does, because the payload set
        /// is a hardcoded list of ids with bespoke behaviour: <c>burning</c> deals fire damage and sets
        /// <c>shok = 33</c>, <c>pinkcloud</c> pink, <c>chemburn</c> acid, <c>drunk</c> poison above
        /// level 3, <c>hydra</c> heals, <c>inhibitor</c> slows nearby enemies. There is no data-driven
        /// payload table in the oracle to port, so inventing one would be a divergence.</para>
        /// </summary>
        void OnEffectPayload(ActiveEffect effect, ActiveEffectSet set);

        /// <summary>
        /// Recompute derived stats because an effect's writes changed — the port of the
        /// <c>setEffParams()</c>/<c>pers.setParameters()</c> call the oracle makes from
        /// <c>setEff</c>, <c>unsetEff</c> and a level change.
        /// </summary>
        void OnEffectParamsChanged(ActiveEffectSet set);
    }

    /// <summary>
    /// Resolve an effect id to its template — the port of AS3's
    /// <c>AllData.d.eff.(@id == id)[0]</c>.
    ///
    /// <para>An interface rather than a direct registry reference so the effect runtime can be tested
    /// with a dictionary fake, and so the same set works for both a <c>Resources.Load</c> build and a
    /// mod-supplied content registry.</para>
    /// </summary>
    public interface IEffectDefinitionResolver
    {
        /// <summary>
        /// The definition for <paramref name="effectId"/>, or <c>null</c> when there is none. Returning
        /// null is meaningful — the oracle's XPath simply yields nothing and the <c>Effect</c> keeps
        /// its field defaults — and callers must handle it rather than assume a definition exists.
        ///
        /// <para>Returns the engine-free <see cref="IEffectTemplate"/> rather than the
        /// <c>ScriptableObject</c> so the runtime never names a Unity type and a test can resolve a
        /// plain object — see <see cref="IEffectTemplate"/>.</para>
        /// </summary>
        IEffectTemplate Resolve(string effectId);
    }
}
