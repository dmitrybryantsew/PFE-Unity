namespace PFE.Systems.Interaction
{
    /// <summary>
    /// The presentation end of a timed action: something that can render "how far along is this" and
    /// then get out of the way.
    ///
    /// <para><b>Why an interface and not a MonoBehaviour reference.</b> The countdown runs on the
    /// simulation tick, and <see cref="PFE.Core.ISimTickable"/> forbids a tick from touching
    /// presentation state. A timer therefore never holds a <c>RectTransform</c> or a
    /// <c>SpriteRenderer</c> — it pushes two plain calls into this seam, and whatever implements it
    /// decides when and how to draw. The same countdown can then drive a HUD bar, a world-space ring
    /// over the door, or nothing at all (see <see cref="NullActionProgressView"/>).</para>
    ///
    /// <para><b>Push, not pull.</b> An implementation is a passive sink: it must not read the timer
    /// back, because it has no reference to one. <see cref="Show"/> may be called every tick while a
    /// hold is in flight and must be cheap; an implementation that renders immediately should expect
    /// to be called at the tick rate, and one that renders on the frame should simply record the
    /// value and act in its own <c>LateUpdate</c>.</para>
    /// </summary>
    public interface IActionProgressView
    {
        /// <summary>
        /// Reports progress as 0&#8594;1 and makes the bar visible. Called at least once on the first
        /// tick of a hold, then once per tick, and again with 1 just before
        /// <see cref="Hide"/> when the action fires.
        /// </summary>
        /// <param name="progress">Fraction elapsed, already clamped to 0..1.</param>
        void Show(float progress);

        /// <summary>
        /// Hides the bar. Called on completion <i>and</i> on cancellation, and is idempotent — an
        /// implementation must tolerate being hidden while already hidden, since a caller that
        /// cancels defensively will do exactly that.
        /// </summary>
        void Hide();
    }

    /// <summary>
    /// A view that draws nothing. Used as the default so no caller has to null-check, and by tests
    /// that care only about the countdown.
    /// </summary>
    public sealed class NullActionProgressView : IActionProgressView
    {
        /// <summary>Shared instance. Stateless, so one is enough.</summary>
        public static readonly NullActionProgressView Instance = new NullActionProgressView();

        private NullActionProgressView()
        {
        }

        public void Show(float progress)
        {
        }

        public void Hide()
        {
        }
    }
}
