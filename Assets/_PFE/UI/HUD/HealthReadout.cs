using R3;
using UnityEngine;

namespace PFE.UI.HUD
{
    /// <summary>
    /// The HUD's "current / max" health string as one pure unit: the format rule, and the binding that
    /// makes it republish on <b>either</b> number.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this is its own type.</b> The defect it exists for — "max health lags behind" — was
    /// reported against the HUD, and both of its causes lived where no shell test could reach: a
    /// <c>MonoBehaviour</c> (<c>HUDViewModel</c>, which held the readout) and another
    /// <c>MonoBehaviour</c> (<c>CharacterStats</c>, which pushed the numbers). Extracting the readout
    /// rule out of the view model puts the half that actually broke under an offline fixture:
    /// <c>CombineLatest</c> over two <c>ReactiveProperty&lt;float&gt;</c> needs no engine, so
    /// <c>HealthReadoutFormatTests</c> runs in the shell and pins both the ceiling rule and the
    /// "the ceiling moved and the health did not" case that produced the report.</para>
    ///
    /// <para><b>What this seam cannot cover, and where that is covered.</b> It proves the readout
    /// <i>reacts</i>; it cannot prove the write side raises the ceiling before the health, because that
    /// needs a real <c>CharacterStats</c> with a <c>LevelCurve</c> asset. That half stays in
    /// <c>HealthReadoutTests</c> in the Editor suite — reachable only inside Unity, and named as such
    /// in the report rather than quietly assumed.</para>
    ///
    /// <para><b>Do not put the format back in the view.</b> The original bug was a view subscribing to
    /// <c>CurrentHealth</c> alone and formatting inside the handler, which read the ceiling at that
    /// instant. A binding over one number cannot be made to notice a change in the other; a binding over
    /// both cannot be made to miss it. That is the whole fix, and it is only durable while there is one
    /// place that expresses it.</para>
    /// </remarks>
    public static class HealthReadout
    {
        /// <summary>
        /// AS3 <c>GUI.setHp</c>: <c>Math.ceil(hp) + "/" + Math.ceil(maxhp)</c> (<c>GUI.as:944</c>).
        /// </summary>
        /// <remarks>
        /// Ceiling, not rounding: the oracle rounds up so a player on 0.4 hp reads "1", not "0" — a bar
        /// at zero with the player still alive is the one reading that looks like a bug.
        /// </remarks>
        public static string Format(float current, float max)
            => $"{Mathf.CeilToInt(current)} / {Mathf.CeilToInt(max)}";

        /// <summary>
        /// A readout that fires when <paramref name="currentHp"/> <b>or</b>
        /// <paramref name="maxHp"/> changes.
        /// </summary>
        /// <remarks>
        /// The caller owns the returned property and must dispose it — it holds a live subscription to
        /// both sources, which is why <c>HUDViewModel</c> keeps it in its disposables list and drops it
        /// on <c>Rebind()</c>.
        /// </remarks>
        public static ReadOnlyReactiveProperty<string> Bind(
            Observable<float> currentHp, Observable<float> maxHp)
            => currentHp.CombineLatest(maxHp, Format).ToReadOnlyReactiveProperty();
    }
}
