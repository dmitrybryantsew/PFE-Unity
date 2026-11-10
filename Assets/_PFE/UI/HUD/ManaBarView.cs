using UnityEngine;
using UnityEngine.UI;
using R3;

namespace PFE.UI.HUD
{
    /// <summary>
    /// Oracle <c>vis.manaBar</c> — the mana meter, plus the <c>vis.textMana</c> readout beside it.
    ///
    /// <para><b>The bar and the number are two different pools, and that is the whole point.</b>
    /// <c>GUI.setMana</c> writes <c>vis.manaBar.mana.scaleX = gg.pers.manaHP / gg.pers.inMaxMana</c>
    /// (<c>GUI.as:999</c>) and, six lines earlier, <c>vis.textMana</c> prints the <b>budget</b>
    /// <c>round(gg.mana / 10) + "%"</c> (<c>:993</c>). The organ is a 400-point wound track that does not
    /// regenerate; the budget is a 1000-point pool that does. They therefore disagree on purpose, and a
    /// port that drew both from one value would look right until the first spell — the bar would refill
    /// while the organ stayed wounded, or the text would read 100% over a half-drained bar.</para>
    ///
    /// <para><b>Why the bar value is <see cref="HUDViewModel.ManaOrganPercent"/> and not
    /// <see cref="HUDViewModel.ManaPercent"/>.</b> The latter is the budget as a fraction, which is the
    /// number the text shows. It is the wrong input for the bar and the distinction is documented on
    /// both properties because it is exactly the kind of pair a later reader "tidies" into one.</para>
    ///
    /// <para><b>It has no gate of its own, but it is not always on screen.</b> <c>GUI.hpBarOnOff</c>
    /// assigns <c>hpBar</c>, <c>hp</c>, <c>manaBar</c> and <c>xpBar</c> as one group
    /// (<c>GUI.as:756-759</c>), so the four appear and disappear together — the port does the same by
    /// registering this view's root in <see cref="ScriptOverlayView.SetGuiElements"/>. What the bar does
    /// <i>not</i> have is a condition of its own, unlike <see cref="ArmourBarView"/> whose visibility is
    /// the port's own invention: the Oracle never hides the mana bar because of a value.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ManaBarView : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("Slider driving the mana-organ fill")]
        private Slider _manaSlider;

        [SerializeField]
        [Tooltip("Optional budget readout ('Magic 34%')")]
        private Text _manaText;

        [SerializeField]
        [Tooltip("ViewModel to bind to")]
        private HUDViewModel _viewModel;

        private CompositeDisposable _disposables;

        /// <summary>See <see cref="HealthBarView"/> — stops <c>Start</c> double-binding a forced bind.</summary>
        private bool _bound;

        private void Awake()
        {
            if (_manaSlider == null)
                _manaSlider = GetComponent<Slider>();

            _disposables = new CompositeDisposable();
        }

        private void Start()
        {
            if (_viewModel != null && !_bound)
            {
                BindToViewModel(_viewModel);
            }
            else if (_viewModel == null)
            {
                Debug.LogWarning("[ManaBarView] No ViewModel assigned - mana bar will not update");
            }
        }

        /// <summary>Assign the ViewModel <b>without</b> binding, so the view binds in its own <c>Start</c>.</summary>
        public void SetViewModel(HUDViewModel viewModel) => _viewModel = viewModel;

        /// <summary>Assign the budget readout (built as a sibling by the runtime HUD builder).</summary>
        public void SetManaText(Text text) => _manaText = text;

        public void BindToViewModel(HUDViewModel viewModel)
        {
            _viewModel = viewModel;

            if (_viewModel == null)
            {
                Debug.LogError("[ManaBarView] Cannot bind to null ViewModel");
                return;
            }

            // Replace, don't accumulate — see HealthBarView.
            _disposables?.Dispose();
            _disposables = new CompositeDisposable();
            _bound = true;

            int bound = 0;

            // The guards are load-bearing: HudBootstrapper drives Initialize() from Update, which runs
            // after every Start(), so the first bind legitimately sees null properties and
            // `null.Subscribe(...)` throws inside R3 with an NRE that names R3, not the real cause.
            if (_viewModel.ManaOrganPercent != null)
            {
                _viewModel.ManaOrganPercent.Subscribe(percent =>
                {
                    if (_manaSlider != null) _manaSlider.value = percent;
                }).AddTo(_disposables);
                bound++;
            }

            if (_manaText != null && _viewModel.ManaText != null)
            {
                _viewModel.ManaText.Subscribe(text => _manaText.text = text).AddTo(_disposables);
                bound++;
            }

            Debug.Log(bound == 0
                ? "[ManaBarView] ViewModel has no sources yet - waiting for the bootstrapper to rebind."
                : $"[ManaBarView] Bound to ViewModel ({bound} subscription(s)).");
        }

        /// <summary>Manually set the fill (tests / non-reactive callers).</summary>
        public void SetMana(float percent)
        {
            if (_manaSlider != null) _manaSlider.value = Mathf.Clamp01(percent);
        }

        private void OnDestroy()
        {
            _disposables?.Dispose();
        }
    }
}
