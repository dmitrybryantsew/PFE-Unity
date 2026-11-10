using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using PFE.Systems.Effects;

namespace PFE.UI.HUD
{
    /// <summary>
    /// Oracle <c>vis.eff0..eff11</c> — the twelve-slot status-effect strip, drawn from
    /// <see cref="StatusEffectStripRules"/>.
    ///
    /// <para><b>It polls, and that is the deliberate choice.</b> The oracle's <c>setEffects</c> is called
    /// from <c>GUI.step</c> once per frame (<c>GUI.as:1526</c>) and reads the whole effect array each
    /// time; there is no "an effect changed" event to subscribe to, and
    /// <see cref="ActiveEffectSet"/> exposes a read-only list with no change notification. Inventing a
    /// notification would mean the set owning a UI concern. So the view reads the list every
    /// <c>LateUpdate</c> and writes only the slots whose <i>rendered</i> value differs from the one
    /// already on screen — a dozen int/string compares, and no allocation in the steady state.</para>
    ///
    /// <para><b>The countdown is in seconds, not ticks.</b> <c>Math.floor(t / 30)</c>
    /// (<c>GUI.as:1206</c>). The port's effect counter is already in canonical 30 Hz ticks regardless of
    /// the simulation's tick rate (see <see cref="ActiveEffectSet"/>), so the division is the same
    /// arithmetic the oracle does — no rate conversion belongs here.</para>
    ///
    /// <para><b>Nothing blinks.</b> The only alpha the oracle applies is <c>0.2</c> while the PipBuck is
    /// open (<c>GUI.as:1163-1170</c>); there is no <c>t % 15</c> flash anywhere in <c>GUI.as</c>. That
    /// matters because a "make it blink when it is about to expire" instinct is exactly the kind of
    /// invention that later gets quoted as behaviour.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class StatusEffectStripView : MonoBehaviour
    {
        // ── Geometry, from the SWF ────────────────────────────────────────────
        //
        // visualGUI (4651) places eff0..eff11 at translateX = 288 + 34 * i, translateY = 18
        // (twips 5760 + 680*i, 360). The slot pitch is therefore 34 px and the icon art inside it is
        // the 30 x 30 frame of symbol 4453, inset 2 px. Read with
        // `.workbuddy-ai/tools/swf_sprite_layout.py assets.swf 4651`.

        /// <summary>Horizontal pitch between slots — <c>eff0</c> at 288, <c>eff1</c> at 322.</summary>
        public const float SlotPitch = 34f;

        /// <summary>The icon's edge — the imported frames are 30 x 30.</summary>
        public const float IconSize = 30f;

        /// <summary>Horizontal inset of the icon inside its 34 px slot, so it is centred.</summary>
        public const float IconInset = (SlotPitch - IconSize) * 0.5f;

        /// <summary>Where the countdown starts, measured down from the slot's top edge.</summary>
        public const float TextTop = 31f;

        /// <summary>Height reserved for the countdown.</summary>
        public const float TextHeight = 14f;

        /// <summary>Total strip width — twelve slots.</summary>
        public const float StripWidth = SlotPitch * StatusEffectStripRules.SlotCount;

        /// <summary>Total strip height — the icon plus its countdown.</summary>
        public const float StripHeight = TextTop + TextHeight;

        /// <summary><c>GUI.as:1165</c> — the strip's alpha while the PipBuck device is open.</summary>
        public const float DimmedAlpha = 0.2f;

        /// <summary>The countdown's colour — the Oracle's terminal green, <c>#00FF99</c>.</summary>
        public static readonly Color CountdownColour = new Color(0f, 1f, 153f / 255f, 1f);

        private HUDViewModel _viewModel;
        private Sprite[] _icons;
        private Func<bool> _isPipBuckOpen;

        private readonly Image[] _iconImages = new Image[StatusEffectStripRules.SlotCount];
        private readonly Text[] _countdowns = new Text[StatusEffectStripRules.SlotCount];
        private readonly GameObject[] _slotRoots = new GameObject[StatusEffectStripRules.SlotCount];

        private readonly StatusEffectSlotDisplay[] _slots =
            new StatusEffectSlotDisplay[StatusEffectStripRules.SlotCount];

        /// <summary>What is currently on screen, so a frame that changed nothing writes nothing.</summary>
        private readonly StatusEffectSlotDisplay[] _applied =
            new StatusEffectSlotDisplay[StatusEffectStripRules.SlotCount];

        private readonly List<StatusEffectEntry> _entries = new List<StatusEffectEntry>(StatusEffectStripRules.SlotCount);

        private CanvasGroup _group;
        private bool _built;
        private bool _appliedOnce;

        /// <summary>How many slots are currently showing. Exposed for a play-test readback.</summary>
        public int VisibleCount { get; private set; }

        /// <summary>
        /// Build the twelve slots and remember where the icons come from.
        /// </summary>
        /// <param name="icons">
        /// The imported icon sequence — <c>statusEffectIcons</c>, 56 frames of symbol 4453. May be null
        /// or short: a missing sprite leaves the slot's <see cref="Image"/> blank rather than throwing,
        /// because a HUD that disappears because an atlas is missing is worse than one with holes.
        /// </param>
        /// <param name="font">Font for the countdowns.</param>
        /// <param name="isPipBuckOpen">Optional: the device's <c>active</c> flag, for the 0.2 dim.</param>
        public void Initialize(Sprite[] icons, Font font, Func<bool> isPipBuckOpen)
        {
            _icons = icons;
            _isPipBuckOpen = isPipBuckOpen;

            if (_built)
            {
                return;
            }

            _built = true;

            // NOT `GetComponent<CanvasGroup>() ?? AddComponent<CanvasGroup>()`. In the Editor,
            // GetComponent returns a *fake-null* wrapper for a missing component so that Unity can
            // report a MissingComponentException; C#'s `??` compares references, so it sees that
            // wrapper as non-null, AddComponent is skipped, and the first `_group.interactable`
            // write throws — aborting BuildHud and taking every later HUD element (the quest popup,
            // the dialogue window, the progression chimes) down with it. Unity's overloaded
            // `== null` is the check that recognises the wrapper; SceneLoader uses the same pattern.
            _group = gameObject.GetComponent<CanvasGroup>();
            if (_group == null)
            {
                _group = gameObject.AddComponent<CanvasGroup>();
            }
            _group.interactable = false;
            _group.blocksRaycasts = false;

            var root = (RectTransform)transform;
            root.sizeDelta = new Vector2(StripWidth, StripHeight);

            for (int i = 0; i < StatusEffectStripRules.SlotCount; i++)
            {
                var slot = new GameObject("Effect Slot " + i, typeof(RectTransform));
                var slotRect = (RectTransform)slot.transform;
                slotRect.SetParent(root, false);
                slotRect.anchorMin = slotRect.anchorMax = new Vector2(0f, 1f);
                slotRect.pivot = new Vector2(0f, 1f);
                slotRect.anchoredPosition = new Vector2(SlotPitch * i, 0f);
                slotRect.sizeDelta = new Vector2(SlotPitch, StripHeight);
                _slotRoots[i] = slot;

                var iconGo = new GameObject("Icon", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                var iconRect = (RectTransform)iconGo.transform;
                iconRect.SetParent(slotRect, false);
                iconRect.anchorMin = iconRect.anchorMax = new Vector2(0f, 1f);
                iconRect.pivot = new Vector2(0f, 1f);
                iconRect.anchoredPosition = new Vector2(IconInset, 0f);
                iconRect.sizeDelta = new Vector2(IconSize, IconSize);

                var image = iconGo.GetComponent<Image>();
                image.raycastTarget = false;
                image.preserveAspect = true;
                _iconImages[i] = image;

                var textGo = new GameObject("Countdown", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
                var textRect = (RectTransform)textGo.transform;
                textRect.SetParent(slotRect, false);
                textRect.anchorMin = textRect.anchorMax = new Vector2(0.5f, 1f);
                textRect.pivot = new Vector2(0.5f, 1f);
                textRect.anchoredPosition = new Vector2(0f, -TextTop);
                textRect.sizeDelta = new Vector2(SlotPitch, TextHeight);

                var text = textGo.GetComponent<Text>();
                text.font = font;
                text.fontSize = 12;
                text.color = CountdownColour;
                text.alignment = TextAnchor.UpperCenter;
                text.horizontalOverflow = HorizontalWrapMode.Overflow;
                text.verticalOverflow = VerticalWrapMode.Overflow;
                text.raycastTarget = false;
                text.text = string.Empty;
                _countdowns[i] = text;

                slot.SetActive(false);
            }
        }

        /// <summary>
        /// Assign the ViewModel <b>without</b> binding, mirroring the other HUD views: the strip has no
        /// reactive source to bind — it polls — so a plain reference is the whole contract.
        /// </summary>
        public void SetViewModel(HUDViewModel viewModel) => _viewModel = viewModel;

        private void LateUpdate()
        {
            if (!_built || _viewModel == null)
            {
                return;
            }

            // The PipBuck dim is a whole-strip alpha, exactly as the oracle applies it per child
            // (GUI.as:1163-1170). Cheap enough to write every frame, but skipped when unchanged so a
            // CanvasGroup rebuild is not triggered 60 times a second for nothing.
            float wantedAlpha = _isPipBuckOpen != null && _isPipBuckOpen() ? DimmedAlpha : 1f;
            if (_group != null && !Mathf.Approximately(_group.alpha, wantedAlpha))
            {
                _group.alpha = wantedAlpha;
            }

            ActiveEffectSet effects = _viewModel.Effects;

            _entries.Clear();
            if (effects != null)
            {
                IReadOnlyList<ActiveEffect> live = effects.Effects;
                for (int i = 0; i < live.Count && i < StatusEffectStripRules.SlotCount; i++)
                {
                    ActiveEffect effect = live[i];
                    if (effect == null)
                    {
                        continue;
                    }

                    _entries.Add(new StatusEffectEntry(
                        effect.Id,
                        effect.TicksRemaining,
                        effect.Forever,
                        effect.IsBeingUnset,
                        effect.Tip == PFE.Data.Definitions.EffectType.Food));
                }
            }

            // cut / poison: 0, and that is a measured gap rather than a decision — the port has no
            // bleed or poison accumulator to read. See StatusEffectStripRules.Build's remarks.
            StatusEffectStripRules.Build(0f, 0f, _viewModel.ShieldHp, _entries, _slots);

            int visible = 0;
            for (int i = 0; i < StatusEffectStripRules.SlotCount; i++)
            {
                ApplySlot(i, _slots[i]);
                if (!_slots[i].IsHidden)
                {
                    visible++;
                }
            }

            VisibleCount = visible;
            _appliedOnce = true;
        }

        private void ApplySlot(int index, StatusEffectSlotDisplay display)
        {
            StatusEffectSlotDisplay previous = _applied[index];

            bool unchanged = _appliedOnce
                             && previous.IconIndex == display.IconIndex
                             && string.Equals(previous.Text, display.Text, StringComparison.Ordinal);

            if (unchanged)
            {
                return;
            }

            _applied[index] = display;

            GameObject root = _slotRoots[index];
            if (root == null)
            {
                return;
            }

            bool visible = !display.IsHidden;
            if (root.activeSelf != visible)
            {
                root.SetActive(visible);
            }

            if (!visible)
            {
                return;
            }

            Image image = _iconImages[index];
            if (image != null)
            {
                Sprite sprite = null;
                if (_icons != null && display.IconIndex >= 0 && display.IconIndex < _icons.Length)
                {
                    sprite = _icons[display.IconIndex];
                }

                image.sprite = sprite;
                image.enabled = sprite != null;
            }

            Text text = _countdowns[index];
            if (text != null)
            {
                text.text = display.Text ?? string.Empty;
            }
        }

        /// <summary>
        /// Readback of what is on screen, for the debug overlay and for a fixture that cannot run a
        /// MonoBehaviour. Returns the applied display for <paramref name="index"/>.
        /// </summary>
        public StatusEffectSlotDisplay AppliedSlot(int index)
        {
            return index >= 0 && index < _applied.Length ? _applied[index] : StatusEffectSlotDisplay.Hidden;
        }
    }
}
