using System;
using MessagePipe;
using PFE.Core.Messages;
using PFE.Systems.Map.Scripting;
using UnityEngine;
using UnityEngine.UI;

namespace PFE.UI.HUD
{
    /// <summary>
    /// The notification banner — a port of the Oracle's <c>vis.mess</c> as driven by
    /// <c>GUI.messText</c> (<c>GUI.as:1656-1691</c>) and aged by <c>GUI.step</c>
    /// (<c>GUI.as:2034-2059</c>).
    ///
    /// <para><b>Why this view has to exist for the begin land to say anything.</b> Every <c>mess</c> in
    /// every authored script is published as a <see cref="TutorialPromptMessage"/> and — before this
    /// file — <b>nothing in the shipped game subscribed to that message</b>. Only the offline fixtures
    /// did. So "Your journey begins" and "Press A or D to move left and right" were published into a
    /// broker with no listener, which is indistinguishable from a data error and is the reason the
    /// campaign's opening read as a black screen with nothing on it.</para>
    ///
    /// <para><b>The lifetime is the Oracle's, in frames.</b> <c>messText</c> sets
    /// <c>t_mess = param5</c>, whose default is <c>150</c>; each <c>GUI.step</c> decrements it and fades
    /// the banner <i>in</i> at <c>0.1</c> per frame while it is positive, then fades <i>out</i> at
    /// <c>0.01</c> per frame once it is not. At the Oracle's 30 Hz that is 5 s on screen and another
    /// 3.3 s fading, which is why the fade-out constant is ten times smaller than the fade-in one and
    /// must not be "evened up".</para>
    ///
    /// <para><b>It runs on wall time, not on the sim tick, and that is deliberate.</b> The banner is
    /// pure presentation: nothing in the simulation reads it, and a toast that outlived a paused clock
    /// would be the only thing on screen still moving. So the frame counter is advanced by
    /// <c>deltaTime × 30</c> rather than by ticks — the units stay the Oracle's (frames at 30 Hz) while
    /// the source stays the render loop.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ScriptToastView : MonoBehaviour
    {
        /// <summary>AS3 <c>messText</c>'s <c>param5</c> default (<c>GUI.as:1656</c>).</summary>
        public const int ToastFrames = 150;

        /// <summary>AS3 <c>GUI.as:2041</c> — <c>mess.alpha += 0.1</c>.</summary>
        public const float FadeInPerFrame = 0.1f;

        /// <summary>AS3 <c>GUI.as:2055</c> — <c>mess.alpha -= 0.01</c>.</summary>
        public const float FadeOutPerFrame = 0.01f;

        /// <summary>Oracle <c>mess.y = 50</c> (<c>GUI.as:1686</c>) — 50 px down from the top edge.</summary>
        public const float TopMargin = 50f;

        /// <summary>Oracle <c>mess.y = screenY - 50 - mess.height</c> (<c>GUI.as:1684</c>).</summary>
        public const float BottomMargin = 50f;

        private Text _label;
        private CanvasGroup _group;
        private RectTransform _rect;
        private HudLayoutScaler _layout;

        private IDisposable _subscription;

        /// <summary>Frames of life remaining — AS3 <c>t_mess</c>.</summary>
        private float _timer;

        private float _alpha;

        /// <summary>AS3 <c>mess.y = screenY - 50 - height</c> vs <c>y = 50</c>.</summary>
        private bool _bottom;

        private string _text = string.Empty;

        /// <summary>Whether anything is currently drawn. For tests.</summary>
        public bool IsShowing => _alpha > 0f;

        /// <summary>Current alpha, 0…1. For tests.</summary>
        public float Alpha => _alpha;

        /// <summary>Frames of life remaining. For tests.</summary>
        public float Timer => _timer;

        /// <summary>Assigns the pieces this view draws into. Called by <c>HudBootstrapper</c>.</summary>
        public void Construct(Text label, CanvasGroup group, RectTransform rect, HudLayoutScaler layout)
        {
            _label = label;
            _group = group;
            _rect = rect;
            _layout = layout;

            ApplyAlpha(0f);
        }

        /// <summary>
        /// Subscribes to the banner stream. Split from <see cref="Construct"/> because the subscriber
        /// comes from the container and the widgets come from the builder — and because a view that
        /// binds in <c>Start</c> is one whose binding order against its data source does not matter,
        /// which is the trap <c>HudBootstrapper</c> documents at length.
        /// </summary>
        public void Bind(ISubscriber<TutorialPromptMessage> subscriber)
        {
            _subscription?.Dispose();
            _subscription = subscriber?.Subscribe(OnPrompt);
        }

        /// <summary>
        /// AS3 <c>GUI.messText</c> (<c>GUI.as:1656-1691</c>).
        ///
        /// <para><b>The three parameters do three different things and only the first is the text.</b>
        /// <c>param3</c> picks the vertical position (<c>y = 50</c> or <c>screenY - 50 - height</c>),
        /// <c>param4</c> appends with <c>&lt;br&gt;</c> instead of replacing, and <c>param5</c> is the
        /// lifetime. A hide message (<c>Show == false</c>) is not part of the Oracle's vocabulary —
        /// the banner is retired by the timer alone — but the field exists on the seam, so honouring it
        /// costs one line and stops a caller's hide from being silently ignored.</para>
        /// </summary>
        private void OnPrompt(TutorialPromptMessage message)
        {
            if (!message.Show)
            {
                _timer = 0f;
                return;
            }

            if (message.Push && !string.IsNullOrEmpty(_text))
            {
                _text = _text + "\n" + message.Text;
            }
            else
            {
                _text = message.Text ?? string.Empty;
            }

            _bottom = message.BottomPosition;

            if (_label != null)
            {
                _label.text = _text;
            }

            // AS3 assigns `t_mess` BEFORE the `if(_loc6_ != "")` text test, so a message with no text
            // still re-arms the timer on whatever is already showing (GUI.as:1658 vs :1673).
            _timer = ToastFrames;

            ApplyPosition();
        }

        private void Update()
        {
            // `deltaTime * 30` keeps the counter in the Oracle's units (frames at 30 Hz) while running
            // off the render loop — see the class summary.
            float frames = UnityEngine.Time.deltaTime * ScriptVocabulary.FramesPerSecond;

            if (_timer > 0f)
            {
                _timer -= frames;
                _alpha = Mathf.Min(1f, _alpha + FadeInPerFrame * frames);
            }
            else if (_alpha > 0f)
            {
                _alpha = Mathf.Max(0f, _alpha - FadeOutPerFrame * frames);
            }

            ApplyAlpha(_alpha);
        }

        /// <summary>
        /// Places the banner. AS3 sets <c>mess.y</c> only while text was assigned, and never sets
        /// <c>x</c> — the banner is centred by the layout table (<see cref="HudLayout"/>'s
        /// <see cref="HudSlot.Toast"/> row).
        ///
        /// <para><b>Why the vertical position is computed here and not in the table.</b> It is
        /// state-dependent: the same element sits at <c>y = 50</c> for an ordinary prompt and at
        /// <c>screenY - 50 - height</c> for one with <c>opt1</c> set. That is the same shape as
        /// <see cref="HudLayout.ApplyRelative"/>'s pet-bar shift — a resting anchor in the table plus a
        /// runtime adjustment — so the anchor stays the top position and this method moves it.</para>
        /// </summary>
        private void ApplyPosition()
        {
            if (_rect == null) return;

            HudAnchor anchor = HudLayout.AnchorFor(HudSlot.Toast);
            float height = _rect.rect.height;

            float y = _bottom ? -(BottomMargin + height) : -TopMargin;

            new HudAnchor(anchor.Point, anchor.Pivot, new Vector2(anchor.Offset.x, y)).Apply(_rect);
        }

        private void ApplyAlpha(float alpha)
        {
            if (_group != null)
            {
                _group.alpha = alpha;
                // Interactable/blocksRaycasts stay off: the Oracle's banner never eats a click
                // (`vis.mouseChildren = vis.mouseEnabled = false` is set for the dialogue, and the
                // banner is not even a button).
                _group.blocksRaycasts = false;
                _group.interactable = false;
            }
            else if (_label != null)
            {
                Color color = _label.color;
                color.a = alpha;
                _label.color = color;
            }
        }

        private void OnDestroy()
        {
            _subscription?.Dispose();
            _subscription = null;
        }
    }
}
