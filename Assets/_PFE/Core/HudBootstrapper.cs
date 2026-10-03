using UnityEngine;
using UnityEngine.UI;
using VContainer;
using PFE.Entities.Player;
using PFE.Entities.Units;
using PFE.Systems.Weapons;
using PFE.UI.HUD;

namespace PFE.Core
{
    /// <summary>
    /// Builds the HUD (health bar, ammo counter, reload indicator) at runtime and wires it to the
    /// player.
    ///
    /// <para><b>Why the HUD is built in code rather than authored into <c>SampleScene</c>.</b>
    /// There is no <c>Canvas</c> in that scene at all, so this had to be created either way. Doing
    /// it here rather than by editing the scene's YAML means the wiring is reviewable as code, it
    /// cannot be broken by a bad <c>fileID</c>, and it survives the player being a prefab instance
    /// whose <c>PlayerWeaponLoadout</c> has not equipped a weapon yet. The views are ordinary
    /// components — the moment the HUD is worth art-directing, run <c>GameObject/Create HUD</c> once
    /// and this bootstrapper retires.</para>
    ///
    /// <para><b>The ordering problem this exists to solve.</b> The HUD's two data sources appear at
    /// different times: <c>UnitStats</c> is created in <c>PlayerController.Awake</c>, but the
    /// equipped weapon is created in <c>PlayerWeaponLoadout.Start</c>. Unity does not order
    /// <c>Start</c> between components, so a HUD that binds once in <c>Start</c> may bind before the
    /// weapon exists and then show "&#45;&#45; / &#45;&#45;" forever. This polls until the weapon
    /// appears, and re-binds on weapon swaps (where the ammo property instance is replaced).</para>
    /// </summary>
    [LocalOnly]
    public sealed class HudBootstrapper : MonoBehaviour
    {
        private IObjectResolver _resolver;

        private HUDViewModel _viewModel;
        private HealthBarView _healthBar;
        private AmmoCounterView _ammoCounter;
        private ReloadIndicatorView _reloadIndicator;
        private ArmourBarView _armourBar;
        private ActionProgressBarView _holdBar;

        /// <summary>The controller the current bindings point at, so a swap can be detected.</summary>
        private object _boundController;

        /// <summary>
        /// Cached data sources. Held so the steady state costs no scene search at all: Unity's
        /// <c>== null</c> on a destroyed object is a native liveness check, not a scene walk, so the
        /// common path below is two reference comparisons per frame.
        /// </summary>
        private PlayerController _player;
        private PlayerWeaponLoadout _loadout;

        /// <summary>Earliest unscaled time at which a failed lookup may be retried.</summary>
        private float _nextRescanTime;

        /// <summary>
        /// How long to wait before re-searching the scene for a data source that is still missing.
        /// A respawn or a late-spawned player is picked up within half a second, which is
        /// imperceptible for a HUD that has nothing to show until then anyway — whereas searching
        /// every frame is two full scene walks per frame for as long as the player is absent.
        /// </summary>
        private const float RescanInterval = 0.5f;

        [Inject]
        public void Construct(IObjectResolver resolver)
        {
            _resolver = resolver;
        }

        private void Start()
        {
            BuildHud();
        }

        private void Update()
        {
            if (_viewModel == null) return;

            // Steady state: both sources cached and alive. No lookups, no allocations.
            if (_player == null || _loadout == null)
            {
                // UnityEngine.Time, fully qualified: this file lives in namespace PFE.Core, which
                // also contains the namespace PFE.Core.Time (UnityTimeProvider.cs). Inside PFE.Core
                // a bare `Time` binds to that NAMESPACE rather than to UnityEngine.Time, giving
                // CS0234 "the type or namespace name 'unscaledTime' does not exist in the namespace
                // 'PFE.Core.Time'".
                if (UnityEngine.Time.unscaledTime < _nextRescanTime) return;
                _nextRescanTime = UnityEngine.Time.unscaledTime + RescanInterval;
                RefreshSources();
            }

            if (_player == null || _player.Stats == null) return;

            object controller = _loadout != null ? _loadout.Current : null;

            // First successful resolve, or the equipped weapon changed underneath us. The organ
            // source is part of the comparison: CharacterStats is created in the same Awake as
            // UnitStats today, but a respawn that replaced only one of them would otherwise leave the
            // mana organ bound to a dead component while the budget kept updating.
            if (_boundController == controller
                && ReferenceEquals(_viewModel.StatsSource, _player.Stats)
                && ReferenceEquals(_viewModel.CharacterStatsSource, _player.CharacterStats))
                return;

            _viewModel.Initialize(_loadout, _player.Stats, _player.CharacterStats);
            RebindViews();
            BindHoldBar();

            // Record the swap only AFTER it succeeded. Writing _boundController before the bind made a
            // throwing Initialize permanent: the next Update saw _boundController == controller,
            // returned early, and never retried — so one exception left the HUD frozen on the previous
            // weapon's numbers with no path back. That is exactly how the R3 ObjectDisposedException of
            // 2026-10-03 hid itself (the ammo readout stayed at the old weapon's value while the new one
            // was equipped). The cost of recording it afterwards is that a genuinely repeatable failure
            // re-logs each frame — which is the honest signal, not noise.
            _boundController = controller;
        }

        // ── Construction ──────────────────────────────────────────────────────

        private void BuildHud()
        {
            var canvasGo = new GameObject("HUD Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(transform, false);

            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            // The ViewModel lives on the canvas so its lifetime is the HUD's.
            _viewModel = canvasGo.AddComponent<HUDViewModel>();

            _healthBar = BuildHealthBar(canvasGo.transform);
            _ammoCounter = BuildAmmoCounter(canvasGo.transform);
            _reloadIndicator = BuildReloadIndicator(canvasGo.transform);
            _armourBar = BuildArmourBar(canvasGo.transform);
            _holdBar = BuildHoldBar(canvasGo.transform);

            // Hand over the reference but do NOT bind here. Each view binds itself in its own
            // Start(), which removes any dependence on whether this Start() or theirs runs first,
            // and avoids the double-subscription the views' Start() would otherwise create.
            //
            // The hold bar is the exception and is not in this list: it has no view model to bind to,
            // because the interactor pushes into it directly. See BindHoldBar.
            _healthBar.SetViewModel(_viewModel);
            _ammoCounter.SetViewModel(_viewModel);
            _reloadIndicator.SetViewModel(_viewModel);
            _armourBar.SetViewModel(_viewModel);

            Debug.Log("[HudBootstrapper] HUD built (health bar, armour bar, ammo counter, reload indicator, hold bar).");
        }

        /// <summary>
        /// Resolved once and shared by every <c>Text</c> in the HUD. The OS-font fallback builds a
        /// brand-new dynamic <c>Font</c> on each call, so without this cache the health readout and
        /// the ammo readout would render from two separate font assets.
        /// </summary>
        private static Font _cachedFont;

        private static Font ResolveFont()
        {
            if (_cachedFont != null) return _cachedFont;

            // Unity 2022 renamed the builtin Arial to LegacyRuntime.ttf, and the name is not
            // discoverable by grepping the editor install (the font lives inside the packed
            // `unity default resources` bundle). So this tries the known names in order and then
            // falls back to an OS font, rather than depending on one magic string: a null font
            // renders nothing at all, silently, which is the worst possible failure for a HUD whose
            // entire job is to make numbers visible.
            //
            // Wrapped because GetBuiltinResource's behaviour on an unknown name has varied between
            // Unity versions (null-with-an-error in some, throw in others), and a throw here would
            // abort BuildHud before it logs anything — a silent no-HUD that looks like the
            // component was never created.
            Font font = null;
            try
            {
                font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                       ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[HudBootstrapper] Builtin font lookup threw ({e.GetType().Name}); " +
                                 "falling back to an OS font.");
            }

            if (font == null)
            {
                // Last resort: any OS font. CreateDynamicFontFromOSFont needs no builtin resource.
                font = Font.CreateDynamicFontFromOSFont(
                    new[] { "Segoe UI", "Arial", "Helvetica", "DejaVu Sans" }, 16);
            }

            if (font == null)
            {
                Debug.LogError("[HudBootstrapper] No font could be resolved — HUD text will be " +
                               "invisible. The health bar and reload indicator still work.");
            }

            _cachedFont = font;
            return font;
        }

        private static RectTransform CreateRect(string name, Transform parent, Vector2 anchor,
            Vector2 pivot, Vector2 anchoredPosition, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = go.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = pivot;
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = size;
            return rect;
        }

        private static Image CreateImage(string name, Transform parent, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            var rect = go.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            var image = go.GetComponent<Image>();
            image.color = color;
            return image;
        }

        private HealthBarView BuildHealthBar(Transform parent)
        {
            // Bottom-left, above the ammo line.
            var barRect = CreateRect("Health Bar", parent,
                anchor: new Vector2(0f, 0f), pivot: new Vector2(0f, 0f),
                anchoredPosition: new Vector2(32f, 64f), size: new Vector2(320f, 24f));

            // Slider must exist and have fillRect assigned BEFORE HealthBarView is added: its Awake
            // auto-finds GetComponent<Slider>() and _healthSlider.fillRect, so the component order
            // here is load-bearing.
            var slider = barRect.gameObject.AddComponent<Slider>();
            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.value = 1f;
            slider.transition = Selectable.Transition.None;
            slider.interactable = false;

            // Background
            var background = CreateImage("Background", barRect, new Color(0f, 0f, 0f, 0.6f));
            Stretch(background.rectTransform);

            // Fill area (inset) + fill image
            var fillArea = CreateRect("Fill Area", barRect,
                anchor: new Vector2(0.5f, 0.5f), pivot: new Vector2(0.5f, 0.5f),
                anchoredPosition: Vector2.zero, size: Vector2.zero);
            Stretch(fillArea, 2f);

            var fill = CreateImage("Fill", fillArea, new Color(0.2f, 0.85f, 0.25f, 0.95f));
            Stretch(fill.rectTransform);

            slider.fillRect = fill.rectTransform;
            slider.targetGraphic = fill;
            slider.direction = Slider.Direction.LeftToRight;

            var view = barRect.gameObject.AddComponent<HealthBarView>();

            // Health readout, to the right of the bar.
            var textRect = CreateRect("Health Text", parent,
                anchor: new Vector2(0f, 0f), pivot: new Vector2(0f, 0f),
                anchoredPosition: new Vector2(360f, 66f), size: new Vector2(160f, 24f));
            var text = textRect.gameObject.AddComponent<Text>();
            text.font = ResolveFont();
            text.fontSize = 18;
            text.color = Color.white;
            text.alignment = TextAnchor.MiddleLeft;
            text.text = "-- / --";

            view.SetHealthText(text);

            return view;
        }

        /// <summary>
        /// The armour condition bar, sitting just above the health bar.
        ///
        /// <para><b>Structure mirrors the health bar with one deliberate difference:</b> the visuals
        /// hang off a child <c>Visuals</c> object instead of the component's own GameObject, because
        /// this view hides itself when nothing is equipped. Hiding via
        /// <c>gameObject.SetActive(false)</c> would stop its <c>Start</c> from ever running — and
        /// <c>Start</c> is where it binds — which is the same deadlock <c>ReloadIndicatorView</c>
        /// documents from the opposite direction. Deactivating a child that carries no component
        /// avoids the question entirely.</para>
        /// </summary>
        private ArmourBarView BuildArmourBar(Transform parent)
        {
            var barRect = CreateRect("Armour Bar", parent,
                anchor: new Vector2(0f, 0f), pivot: new Vector2(0f, 0f),
                anchoredPosition: new Vector2(32f, 96f), size: new Vector2(320f, 12f));

            // Slider first, with fillRect assigned before the view is added: ArmourBarView.Awake
            // reads GetComponent<Slider>() and the fill's Image, so this order is load-bearing.
            var slider = barRect.gameObject.AddComponent<Slider>();
            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.value = 0f;
            slider.transition = Selectable.Transition.None;
            slider.interactable = false;

            var visuals = CreateRect("Visuals", barRect,
                anchor: new Vector2(0.5f, 0.5f), pivot: new Vector2(0.5f, 0.5f),
                anchoredPosition: Vector2.zero, size: Vector2.zero);
            Stretch(visuals);

            var background = CreateImage("Background", visuals, new Color(0f, 0f, 0f, 0.6f));
            Stretch(background.rectTransform);

            var fillArea = CreateRect("Fill Area", visuals,
                anchor: new Vector2(0.5f, 0.5f), pivot: new Vector2(0.5f, 0.5f),
                anchoredPosition: Vector2.zero, size: Vector2.zero);
            Stretch(fillArea, 1f);

            var fill = CreateImage("Fill", fillArea, new Color(0.35f, 0.65f, 0.95f, 0.95f));
            Stretch(fill.rectTransform);

            slider.fillRect = fill.rectTransform;
            slider.targetGraphic = fill;
            slider.direction = Slider.Direction.LeftToRight;

            var view = barRect.gameObject.AddComponent<ArmourBarView>();
            view.SetContent(visuals.gameObject);

            // Armour readout, aligned with the health readout column.
            var textRect = CreateRect("Armour Text", parent,
                anchor: new Vector2(0f, 0f), pivot: new Vector2(0f, 0f),
                anchoredPosition: new Vector2(360f, 92f), size: new Vector2(160f, 20f));
            var text = textRect.gameObject.AddComponent<Text>();
            text.font = ResolveFont();
            text.fontSize = 14;
            text.color = Color.white;
            text.alignment = TextAnchor.MiddleLeft;
            text.text = string.Empty;

            view.SetArmourText(text);

            // Start hidden: no shipped path has armour equipped at boot. Safe here precisely because
            // `visuals` carries no component whose lifecycle matters.
            visuals.gameObject.SetActive(false);

            return view;
        }

        /// <summary>
        /// The hold bar: fills while the action key is held on a timed object.
        ///
        /// <para><b>Centred at the bottom rather than in the left-hand stat column.</b> The health,
        /// armour and ammo readouts report the player's <i>persistent</i> state and belong together;
        /// this bar exists only for the duration of a hold and belongs to a different source. Keeping
        /// it out of that column is what stops it reading as a fourth stat.</para>
        ///
        /// <para><b>Structure follows <see cref="ArmourBarView"/>'s, for the same reason:</b> the
        /// visuals hang off a child so the view can hide itself without deactivating the component that
        /// would have to show it again. Deactivating <c>barRect</c> instead would stop
        /// <c>LateUpdate</c> — which is where this view draws — and the bar would never come back.</para>
        /// </summary>
        private ActionProgressBarView BuildHoldBar(Transform parent)
        {
            var barRect = CreateRect("Hold Bar", parent,
                anchor: new Vector2(0.5f, 0f), pivot: new Vector2(0.5f, 0f),
                anchoredPosition: new Vector2(0f, 96f), size: new Vector2(240f, 14f));

            // Slider first, with fillRect assigned before the view is added: ActionProgressBarView.Awake
            // reads GetComponent<Slider>() and the fill's Image, so this order is load-bearing.
            var slider = barRect.gameObject.AddComponent<Slider>();
            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.value = 0f;
            slider.transition = Selectable.Transition.None;
            slider.interactable = false;

            var visuals = CreateRect("Visuals", barRect,
                anchor: new Vector2(0.5f, 0.5f), pivot: new Vector2(0.5f, 0.5f),
                anchoredPosition: Vector2.zero, size: Vector2.zero);
            Stretch(visuals);

            var background = CreateImage("Background", visuals, new Color(0f, 0f, 0f, 0.65f));
            Stretch(background.rectTransform);

            var fillArea = CreateRect("Fill Area", visuals,
                anchor: new Vector2(0.5f, 0.5f), pivot: new Vector2(0.5f, 0.5f),
                anchoredPosition: Vector2.zero, size: Vector2.zero);
            Stretch(fillArea, 1f);

            var fill = CreateImage("Fill", fillArea, new Color(0.95f, 0.85f, 0.2f, 0.95f));
            Stretch(fill.rectTransform);

            slider.fillRect = fill.rectTransform;
            slider.targetGraphic = fill;
            slider.direction = Slider.Direction.LeftToRight;

            var view = barRect.gameObject.AddComponent<ActionProgressBarView>();
            view.SetContent(visuals.gameObject);

            // Nothing is being held at boot.
            visuals.gameObject.SetActive(false);

            return view;
        }

        private AmmoCounterView BuildAmmoCounter(Transform parent)
        {
            var rect = CreateRect("Ammo Counter", parent,
                anchor: new Vector2(1f, 0f), pivot: new Vector2(1f, 0f),
                anchoredPosition: new Vector2(-32f, 64f), size: new Vector2(220f, 32f));

            // Text before the view: AmmoCounterView.Awake does GetComponent<Text>().
            var text = rect.gameObject.AddComponent<Text>();
            text.font = ResolveFont();
            text.fontSize = 24;
            text.color = Color.white;
            text.alignment = TextAnchor.MiddleRight;
            text.text = "-- / --";

            return rect.gameObject.AddComponent<AmmoCounterView>();
        }

        private ReloadIndicatorView BuildReloadIndicator(Transform parent)
        {
            var rect = CreateRect("Reload Indicator", parent,
                anchor: new Vector2(0.5f, 0f), pivot: new Vector2(0.5f, 0f),
                anchoredPosition: new Vector2(0f, 64f), size: new Vector2(96f, 12f));

            // Image before the view: ReloadIndicatorView.Awake does GetComponent<Image>() and caches
            // its colour as the "not reloading" colour — so starting transparent is what makes the
            // idle state invisible.
            //
            // The GameObject deliberately stays ACTIVE. Deactivating it would stop its Start() from
            // ever running, and Start() is where the view binds the very subscription that would
            // re-activate it — a deadlock. Transparency, not deactivation, is the initial hide.
            var image = rect.gameObject.AddComponent<Image>();
            image.color = new Color(1f, 0.5f, 0f, 0f);
            image.type = Image.Type.Filled;
            image.fillMethod = Image.FillMethod.Horizontal;
            image.fillAmount = 0f;

            return rect.gameObject.AddComponent<ReloadIndicatorView>();
        }

        private static void Stretch(RectTransform rect, float inset = 0f)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = new Vector2(inset, inset);
            rect.offsetMax = new Vector2(-inset, -inset);
        }

        // ── Binding ───────────────────────────────────────────────────────────

        private void RebindViews()
        {
            _healthBar?.BindToViewModel(_viewModel);
            _ammoCounter?.BindToViewModel(_viewModel);
            _reloadIndicator?.BindToViewModel(_viewModel);
            _armourBar?.BindToViewModel(_viewModel);
        }

        /// <summary>
        /// Re-read both data sources. Only called from <see cref="Update"/> when a cached reference
        /// has died, and throttled — see <see cref="RescanInterval"/>. Both lookups prefer the
        /// container and fall back to a scene search, so this is the only place a scene walk can
        /// happen at all.
        /// </summary>
        private void RefreshSources()
        {
            _player = ResolvePlayer();
            _loadout = ResolveLoadout();
        }

        /// <summary>
        /// Points the hold bar at the interactor that reports hold progress.
        ///
        /// <para><b>Push, not poll.</b> Every other bar here reads a view-model property that the
        /// bootstrapper keeps in sync. This one does not: the interactor drives the bar through
        /// <see cref="PFE.Systems.Interaction.IActionProgressView"/>, so there is nothing to refresh
        /// per frame and no property to keep in sync — only a reference to hand over once.</para>
        ///
        /// <para>Rides along with the existing rebind path, which runs on first successful resolve and
        /// again if the equipped weapon is swapped. Re-assigning is guarded by a reference compare, and
        /// the <c>GetComponent</c> is only paid on those rare rebinds rather than every frame, which is
        /// what this Update's steady state is written to avoid.</para>
        ///
        /// <para>A player prefab without a <see cref="PlayerActionInteractor"/> simply gets no hold bar;
        /// that is the same optionality the interactor itself has, and it is not an error.</para>
        /// </summary>
        private void BindHoldBar()
        {
            if (_holdBar == null || _player == null)
            {
                return;
            }

            var interactor = _player.GetComponent<PlayerActionInteractor>();
            if (interactor != null && !ReferenceEquals(interactor.View, _holdBar))
            {
                interactor.View = _holdBar;
            }
        }

        private PlayerController ResolvePlayer()
        {
            // Container first: GameLifetimeScope registers the scene's PlayerController as an
            // instance (RegisterComponent), so this is a dictionary hit rather than a scene walk.
            if (_resolver != null && _resolver.TryResolve(out PlayerController resolved) && resolved != null)
                return resolved;

            // Fallback for a player that appeared after the scope was built. This IS a full scene
            // search — which is why it is only reachable through the throttled RefreshSources.
            return FindFirstObjectByType<PlayerController>();
        }

        private PlayerWeaponLoadout ResolveLoadout()
        {
            if (_resolver != null && _resolver.TryResolve(out PlayerWeaponLoadout resolved) && resolved != null)
                return resolved;

            return FindFirstObjectByType<PlayerWeaponLoadout>();
        }
    }
}
