using UnityEngine;
using UnityEngine.UI;
using PFE.Sim.Combat.Sats;

namespace PFE.UI.HUD
{
    /// <summary>
    /// The action-point bar — <c>vis.odBar</c> / <c>vis.odBar2</c> plus the <c>pipText("ap")</c> label,
    /// driven by <c>GUI.setOd</c> (<c>GUI.as:885-899</c>).
    ///
    /// <para><b>Two fills, one inside the other.</b> The Oracle draws <c>bar</c> (the <i>projected</i>
    /// pool, <c>odv</c>) and <c>bar2</c> (the <i>real</i> pool, <c>od</c>) over the same track, both
    /// scaled <c>value / 50</c> and both masked. The projected one is the larger while a queue is
    /// reserved, so the player sees "what I have" and "what is left after the plan" at once.</para>
    ///
    /// <para><b>Why <c>fillAmount</c> and not <c>scaleX</c>.</b> AS3 sets <c>scaleX</c> and relies on
    /// the mask to clip it, so a pool of 80 against a 50-wide bar reads as full rather than 1.6 bars
    /// wide. <c>Image.fillAmount</c> clamps at 1 for the same reason and needs no mask object, so the
    /// observable behaviour — saturate, never overflow — is identical. Recorded as a
    /// <c>PortChoice</c>.</para>
    ///
    /// <para><b>The 200-tick countdown is the Oracle's, not a fade of the port's invention.</b>
    /// <c>GUI.step</c> (<c>GUI.as:2009-2015</c>) decrements <c>t_od</c> every frame and hides the bar
    /// at 1, so it disappears ~6.7 s after the last <c>setOd()</c> rather than staying up.</para>
    /// </summary>
    public sealed class ActionPointsBarView : MonoBehaviour
    {
        /// <summary><c>GUI.setOd</c>'s fill colour — the Oracle's bar is a gradient in a symbol that was
        /// never exported, so this is the port's own green, chosen to match the S.A.T.S. overlay.</summary>
        public static readonly Color FillProjected = new Color(0.35f, 0.85f, 0.45f, 0.95f);
        public static readonly Color FillReal = new Color(0.85f, 0.95f, 0.55f, 0.95f);

        private Image _projected;
        private Image _real;
        private Text _label;
        private CanvasGroup _group;

        public void Construct(Image projected, Image real, Text label, CanvasGroup group)
        {
            _projected = projected;
            _real = real;
            _label = label;
            _group = group;
        }

        /// <summary>
        /// Redraw. <paramref name="applicable"/> is <c>GUI.setOd</c>'s own guard — no bar at all for a
        /// weapon S.A.T.S. cannot use (null or <c>noSats</c>).
        /// </summary>
        public void Sync(float od, float odv, bool applicable, bool visible)
        {
            bool show = applicable && visible;

            if (_group != null) _group.alpha = show ? 1f : 0f;
            if (!show) return;

            if (_projected != null) _projected.fillAmount = Mathf.Clamp01(SatsRules.OdBarScale(odv));
            if (_real != null) _real.fillAmount = Mathf.Clamp01(SatsRules.OdBarScale(od));
            if (_label != null) _label.text = "AP";
        }
    }
}
