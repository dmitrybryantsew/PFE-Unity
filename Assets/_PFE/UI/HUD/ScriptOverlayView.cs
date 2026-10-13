using System;
using System.Collections.Generic;
using MessagePipe;
using PFE.Core.Messages;
using PFE.Systems.Map.Scripting;
using UnityEngine;
using UnityEngine.UI;

namespace PFE.UI.HUD
{
    /// <summary>
    /// The blackout overlay and the stat-cluster visibility switch — a port of
    /// <c>World.vblack</c> + <c>Camera.dblack</c> (<c>Camera.as:291-308</c>) and
    /// <c>GUI.hpBarOnOff</c> (<c>GUI.as:756-759</c>), driven by the script commands <c>black</c>,
    /// <c>dblack</c>, <c>gui off</c> and <c>gui on</c> (<c>Script.as:371-391</c>).
    ///
    /// <para><b>Why the blackout is not "just an Image at alpha 1".</b> <c>black val</c> and
    /// <c>dblack val</c> set two <i>different</i> things and a script uses both in sequence:
    /// <c>beginBeg</c> opens with <c>black 1</c> — alpha 1, and the camera's fade rate cleared to 0 —
    /// then immediately <c>dblack -2</c>, which sets the fade <b>rate</b> and leaves the alpha alone. So
    /// the screen starts fully black and fades up at 0.02 per frame for about 1.7 s. A view that treated
    /// <c>dblack</c> as "set the alpha to val" would snap to fully transparent on the second command and
    /// the whole opening would happen on a lit screen.</para>
    ///
    /// <para><b>The rate is per-frame and divided by 100, and both halves are the Oracle's.</b>
    /// <c>Camera.as:294</c> is <c>vblack.alpha += dblack / 100</c> — so the authored <c>-2</c> is 0.02,
    /// not 2, and a rate of <c>1</c> would take 100 frames. Advancing by <c>deltaTime × 30</c> keeps the
    /// units (frames at 30 Hz) while running on the render loop, exactly as
    /// <see cref="ScriptToastView"/> does.</para>
    ///
    /// <para><b><c>gui off</c> hides four elements, not the HUD.</b> <c>hpBarOnOff</c> touches
    /// <c>vis.hpBar</c>, <c>vis.hp</c>, <c>vis.manaBar</c> and <c>vis.xpBar</c> and nothing else — the
    /// weapon block, the action-point bar, the item readout and the status-effect strip all stay. A port
    /// that hid the whole canvas would also hide the dialogue window and the toast, which is the exact
    /// opposite of what a cutscene wants.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ScriptOverlayView : MonoBehaviour
    {
        private Image _black;

        /// <summary>The elements <c>gui off</c> hides — the Oracle's <c>hpBar</c>/<c>hp</c>/
        /// <c>manaBar</c>/<c>xpBar</c> group.</summary>
        private readonly List<GameObject> _guiElements = new List<GameObject>();

        private IDisposable _subscription;

        private float _alpha;
        private float _fade;

        /// <summary>Current overlay alpha, 0…1 — AS3 <c>vblack.alpha</c>. For tests.</summary>
        public float Alpha => _alpha;

        /// <summary>Current fade rate in alpha-per-frame — AS3 <c>cam.dblack</c>. For tests.</summary>
        public float FadeRate => _fade;

        /// <summary>Whether the stat cluster is drawn — AS3 <c>hpBarOnOff</c>'s argument. For tests.</summary>
        public bool GuiVisible { get; private set; } = true;

        /// <summary>Assigns the full-screen black image. Called by <c>HudBootstrapper</c>.</summary>
        public void Construct(Image black)
        {
            _black = black;
            Apply();
        }

        /// <summary>
        /// Registers the elements <c>gui off</c> hides. Named rather than discovered, because the Oracle's
        /// list is an explicit four-element assignment and a `GetComponentsInChildren` sweep would grow
        /// silently the moment a new HUD element is added.
        /// </summary>
        public void SetGuiElements(params GameObject[] elements)
        {
            _guiElements.Clear();
            if (elements != null)
            {
                for (int i = 0; i < elements.Length; i++)
                {
                    if (elements[i] != null) _guiElements.Add(elements[i]);
                }
            }

            ApplyGui();
        }

        /// <summary>Subscribes to the overlay stream. Split from <see cref="Construct"/> for the same
        /// reason as <see cref="ScriptToastView.Bind"/>.</summary>
        public void Bind(ISubscriber<ScriptOverlayMessage> subscriber)
        {
            _subscription?.Dispose();
            _subscription = subscriber?.Subscribe(OnOverlay);
        }

        private void OnOverlay(ScriptOverlayMessage message)
        {
            if (message.SetsGui)
            {
                GuiVisible = message.GuiVisible;
                ApplyGui();
            }

            if (message.SetsAlpha)
            {
                // AS3 `black val`: `cam.dblack = 0; vblack.visible = val > 0; vblack.alpha = val`
                // (Script.as:371-379).
                _alpha = Mathf.Clamp01(message.BlackAlpha);
                _fade = 0f;
                Apply();
            }
            else
            {
                // AS3 `dblack val`: the rate only (Script.as:380-383).
                _fade = message.BlackFadeRate;
            }
        }

        private void Update()
        {
            if (Mathf.Approximately(_fade, 0f)) return;

            // Camera.as:291-308, in the Oracle's units.
            float frames = UnityEngine.Time.deltaTime * ScriptVocabulary.FramesPerSecond;
            _alpha = Mathf.Clamp01(_alpha + _fade / 100f * frames);

            if (_fade > 0f && _alpha >= 1f)
            {
                _fade = 0f;
            }
            else if (_fade < 0f && _alpha <= 0f)
            {
                _fade = 0f;
            }

            Apply();
        }

        /// <summary>
        /// AS3 <c>vblack.visible</c>. The image is switched off entirely at alpha 0 rather than left as a
        /// fully transparent quad, because a transparent full-screen <see cref="Image"/> still costs a
        /// draw and — if anything ever set <c>raycastTarget</c> — would swallow every click in the game.
        /// </summary>
        private void Apply()
        {
            if (_black == null) return;

            bool visible = _alpha > 0f;
            if (_black.gameObject.activeSelf != visible)
            {
                _black.gameObject.SetActive(visible);
            }

            Color color = _black.color;
            color.a = _alpha;
            _black.color = color;
        }

        private void ApplyGui()
        {
            for (int i = 0; i < _guiElements.Count; i++)
            {
                GameObject element = _guiElements[i];
                if (element == null) continue;
                if (element.activeSelf != GuiVisible) element.SetActive(GuiVisible);
            }
        }

        private void OnDestroy()
        {
            _subscription?.Dispose();
            _subscription = null;
        }
    }
}
