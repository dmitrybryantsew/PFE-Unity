using UnityEngine;
using UnityEngine.UI;
using MessagePipe;
using VContainer;
using PFE.Core.Messages;
using PFE.Data.Definitions;
using PFE.Entities.Player;
using PFE.Entities.Units;
using PFE.Systems.Campaign;
using PFE.Systems.Interaction;
using PFE.Systems.Inventory;
using PFE.Systems.Weapons;
using PFE.UI.Common;
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
        private HudLayoutScaler _layout;
        private HealthBarView _healthBar;
        private AmmoCounterView _ammoCounter;
        private ReloadIndicatorView _reloadIndicator;
        private ArmourBarView _armourBar;
        private ActionProgressBarView _holdBar;

        /// <summary>
        /// The three elements that are <b>not placed on the screen</b> but projected from a world
        /// point every frame — the reload bar (onto the player), the hold bar and the interaction
        /// prompt (onto the target). See <see cref="WorldAnchoredHudElement"/> for why, and
        /// <see cref="HudAnchorSource.WorldAnchored"/> for the provenance tag.
        /// </summary>
        private WorldAnchoredHudElement _reloadAnchor;
        private WorldAnchoredHudElement _holdAnchor;
        private WorldAnchoredHudElement _promptAnchor;
        private InteractionPromptView _prompt;

        /// <summary>
        /// The three script-presentation views — the banner, the dialogue windows and the blackout
        /// overlay. They are the port's consumers for <c>TutorialPromptMessage</c>,
        /// <c>ScriptDialogueMessage</c> and <c>ScriptOverlayMessage</c>; before they existed those three
        /// brokers had <b>no subscriber at all</b>, so every <c>mess</c>, every <c>dial</c> and the
        /// opening <c>black 1</c> of the begin land were published into nothing.
        /// </summary>
        private ScriptToastView _toast;
        private ScriptDialogueView _dialogue;
        private ScriptOverlayView _overlay;

        /// <summary>
        /// The progression chimes — level up, quest granted, quest completed.
        ///
        /// <para><b>Not part of the script-presentation trio above, and deliberately not on the
        /// canvas.</b> It draws nothing, so it is attached to the bootstrapper's own GameObject
        /// rather than to the HUD canvas: the canvas is what <c>gui off</c> can hide, and a level-up
        /// during a cutscene must still chime. Its three clips (<c>levelup</c>, <c>quest</c>,
        /// <c>quest_ok</c>) were already imported; what was missing was this subscriber —
        /// <c>LevelUpMessage</c> had a publisher and no listener at all.</para>
        /// </summary>
        private ProgressSoundView _progressSounds;

        /// <summary>
        /// The two halves of the pickup feedback AS3 shows on every take: the corner message list
        /// (<c>vis.info</c>, <c>GUI.as:1553-1577</c>) and the rising label over the player
        /// (<c>GUI.floatText</c>, <c>:1608-1638</c>).
        ///
        /// <para><b>They were both missing, and that is why taking the screwdriver printed nothing.</b>
        /// The take path worked — the item landed in the bag and the pickup despawned — but the oracle
        /// routes every take through <c>Invent.take</c>'s tail, which is where "Found: Screwdriver" comes
        /// from. The port had no subscriber for either half, so the whole feedback surface was silent.
        /// </para>
        ///
        /// <para>Both are driven from <see cref="ItemPickupFeedback"/>, a static seam rather than a
        /// MessagePipe broker — see that type for why the publisher cannot be injected.</para>
        /// </summary>
        private InfoLogView _infoLog;
        private PickupFloatTextView _floatText;

        /// <summary>
        /// The one-time tutorial popup — <c>GUI.impMess</c> (<c>GUI.as:1905-1932</c>). Fired by
        /// <see cref="ItemPickedUpMessage.MessKey"/> and dismissed by any press after 30 frames.
        ///
        /// <para><b>It holds the player still, so the bootstrapper owns that half.</b> The oracle writes
        /// <c>World.w.ctr.active = false</c> and <c>guiPause = true</c>; the port's equivalent is
        /// <see cref="PlayerController.ModalInputBlocked"/> — a <i>separate</i> flag from the cutscene
        /// lock, so the two cannot fight. See <see cref="UpdateImportantMessage"/>.</para>
        /// </summary>
        private ImpMessView _impMess;

        /// <summary>The UI sound service, when the scene has one. <c>impMess</c> plays the quest chime.</summary>
        private UiSoundService _sound;

        /// <summary>
        /// The interface composition — held for the one catalog the HUD itself draws from, the dialogue
        /// speaker portraits (<c>portret</c>, symbol 4638).
        ///
        /// <para>Loaded rather than injected because it is not registered in the container: it is the same
        /// <c>Resources</c> asset <c>PipBuckBootstrapper</c> loads by path
        /// (<c>Resources.Load&lt;PipBuckCompositionDefinition&gt;("UI/PipBuckComposition")</c>,
        /// <c>PipBuckBootstrapper.cs:124</c>). A missing asset is survivable and must not abort the HUD —
        /// the dialogue then draws with an empty portrait slot, which is exactly what it did before the
        /// catalog existed.</para>
        /// </summary>
        private PipBuckCompositionDefinition _composition;

        /// <summary>
        /// The two elements <c>gui off</c> hides, held so the overlay view can be handed the Oracle's
        /// actual group. <c>hpBarOnOff</c> names <c>vis.hpBar</c>, <c>vis.hp</c>, <c>vis.manaBar</c> and
        /// <c>vis.xpBar</c> (<c>GUI.as:756-759</c>); the port builds only the first two today — the mana
        /// bar and the XP bar are slots <see cref="HudLayout"/> has anchors for but nothing constructs,
        /// which is why they are absent here rather than silently toggling nothing.
        /// </summary>
        private GameObject _healthBarRoot;
        private GameObject _healthTextRoot;

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

            // Refreshed every frame, and deliberately ABOVE the early-outs below: the steady state
            // returns as soon as the weapon and the stats are bound, so a prompt driven from the
            // rebind path would freeze over the first object the player ever pointed at.
            UpdateInteractionPrompt();

            // The reload bar hangs over the player, so its anchor is the player. Set here, above the
            // early-outs, so it does not depend on a weapon having equipped or on the throttled
            // re-scan window: a player with no loadout yet still has its bar attached to the right
            // object. One reference write per frame; SetTarget is idempotent.
            BindReloadAnchor();

            // Above the early-outs for the same reason: a tutorial popup must keep ticking (and keep
            // holding the player) whether or not a weapon has equipped.
            UpdateImportantMessage();

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

            // The Oracle's virtual canvas, not a taste call — see HudLayout. It was 1920x1080 here,
            // which is not a resolution any of the Oracle's offsets are measured against.
            scaler.referenceResolution = HudLayout.ReferenceSize;
            scaler.matchWidthOrHeight = HudLayout.MatchWidthOrHeight;

            // Owns placement for every element built below. It re-applies the table whenever the canvas
            // changes size, which is the port's replacement for GUI.resizeScreen re-running on a stage
            // resize (GUI.as:310-349).
            _layout = canvasGo.AddComponent<HudLayoutScaler>();

            // The dialogue speaker portraits live in the interface composition, not in the HUD. Loaded
            // once here; a null result is tolerated (see the field's note) rather than thrown on.
            _composition = Resources.Load<PipBuckCompositionDefinition>("UI/PipBuckComposition");

            // The ViewModel lives on the canvas so its lifetime is the HUD's.
            _viewModel = canvasGo.AddComponent<HUDViewModel>();

            _healthBar = BuildHealthBar(canvasGo.transform);
            _ammoCounter = BuildAmmoCounter(canvasGo.transform);
            _reloadIndicator = BuildReloadIndicator(canvasGo.transform);
            _armourBar = BuildArmourBar(canvasGo.transform);
            _holdBar = BuildHoldBar(canvasGo.transform);
            _prompt = BuildInteractionPrompt(canvasGo.transform);

            // The pickup feedback. Built last so the floating labels sit at the end of the sibling
            // order and therefore draw over everything else on the canvas — the oracle's `take`
            // particle is on `Sloy 5`, the front-most layer.
            _infoLog = BuildInfoLog(canvasGo.transform);
            _floatText = BuildPickupFloatText(canvasGo.transform);
            _impMess = BuildImportantMessage(canvasGo.transform);

            // The script-presentation views. Built after the stat cluster so the blackout image can be
            // moved to the back of the sibling order: in the Oracle `vblack` lives on the world layer
            // and the GUI — including `vis.mess` — draws over it, which is what lets `beginBeg` show
            // "Your journey begins" on a fully black screen and then fade the black away underneath it.
            _toast = BuildToast(canvasGo.transform);
            _dialogue = BuildDialogue(canvasGo.transform);
            _overlay = BuildOverlay(canvasGo.transform);
            _overlay.SetGuiElements(_healthBarRoot, _healthTextRoot);

            // On the bootstrapper, not on the canvas — see the field's note. Added unconditionally so
            // that Bind can report a missing broker instead of the component being absent entirely.
            _progressSounds = gameObject.AddComponent<ProgressSoundView>();

            BindScriptViews();
            BindProgressSounds();
            BindPickupFeedback();

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

            Debug.Log("[HudBootstrapper] HUD built (health bar, armour bar, ammo counter, " +
                      "world-anchored reload bar, world-anchored hold bar, world-anchored interaction " +
                      "prompt, script banner, dialogue window, blackout overlay, pickup message list, " +
                      "pickup floating labels) on a " +
                      HudLayout.ReferenceWidth + "x" + HudLayout.ReferenceHeight + " canvas.");
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

        /// <summary>
        /// Creates a top-level HUD element at a neutral centre anchor, sized but <b>not</b> positioned.
        /// Placement is <see cref="HudLayoutScaler"/>'s job: it is handed the rect through
        /// <see cref="HudLayoutScaler.Register"/>, which applies the Oracle anchor for the slot.
        ///
        /// <para><b>Why not just pass the anchor here, as <see cref="CreateRect"/> does for children.</b>
        /// Because a position written at build time is not re-applied on a resize, and the Oracle
        /// recomputes every one of its offsets against the live <c>screenX</c>/<c>screenY</c>
        /// (<c>GUI.as:310-349</c>). Two owners for one number is how a HUD ends up correct at 16:9 and
        /// wrong at 21:9. Children keep using <see cref="CreateRect"/>: they are positioned relative to
        /// their own parent's rect, which the anchor already handles.</para>
        /// </summary>
        private static RectTransform CreateHudElement(string name, Transform parent, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = go.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
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

        /// <summary>
        /// <see cref="CreateImage"/> plus a full rect spec, for children whose position comes from the
        /// Oracle rather than from a stretch.
        /// </summary>
        private static Image CreateRectImage(string name, Transform parent, Color color, Vector2 anchor,
            Vector2 pivot, Vector2 anchoredPosition, Vector2 size)
        {
            Image image = CreateImage(name, parent, color);
            RectTransform rect = image.rectTransform;
            rect.anchorMin = rect.anchorMax = anchor;
            rect.pivot = pivot;
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = size;
            image.raycastTarget = false;
            return image;
        }

        /// <summary>
        /// A one-pixel rectangle outline, drawn as four strips.
        ///
        /// <para><b>Four strips rather than a sprite, because the port has no plate art.</b> The Oracle's
        /// dialogue border is a 1 px <c>#33CC99</c> stroke on shape <c>4520</c>
        /// (<see cref="HudLayout.DialogueBorderColor"/>, <see cref="HudLayout.DialogueBorderThickness"/>),
        /// and the port's whole HUD is built from flat <see cref="Image"/> colours — no SWF shape has ever
        /// been imported for it. Four strips reproduce that stroke at its authored thickness without
        /// inventing a texture, and the two numbers that describe it are the ones read off the SWF.</para>
        /// </summary>
        private static void CreateBorder(Transform parent, Color color, float width, float height,
            float thickness)
        {
            // Top and bottom span the full width; left and right span the full height. Anchored at the
            // two corners so the strips sit exactly on the rect's edge rather than one pixel inside it.
            CreateRectImage("Border Top", parent, color,
                anchor: new Vector2(0f, 1f), pivot: new Vector2(0f, 1f),
                anchoredPosition: Vector2.zero, size: new Vector2(width, thickness));
            CreateRectImage("Border Bottom", parent, color,
                anchor: new Vector2(0f, 0f), pivot: new Vector2(0f, 0f),
                anchoredPosition: Vector2.zero, size: new Vector2(width, thickness));
            CreateRectImage("Border Left", parent, color,
                anchor: new Vector2(0f, 0f), pivot: new Vector2(0f, 0f),
                anchoredPosition: Vector2.zero, size: new Vector2(thickness, height));
            CreateRectImage("Border Right", parent, color,
                anchor: new Vector2(1f, 0f), pivot: new Vector2(1f, 0f),
                anchoredPosition: Vector2.zero, size: new Vector2(thickness, height));
        }

        /// <summary>
        /// Converts an Oracle sprite-local offset to a Unity <c>anchoredPosition</c>.
        ///
        /// <para><b>The two conventions differ on both axes and this is the only place that is
        /// reconciled.</b> A SWF child is placed by a matrix whose origin is the sprite's top-left and
        /// whose Y runs <b>down</b>; a Unity <c>RectTransform</c> anchored to (0,1) has its origin at the
        /// parent's top-left with Y running <b>up</b>. So <c>x</c> passes through and <c>y</c> is
        /// negated — the same mirror rule <c>WorldY</c> documents for world space.</para>
        /// </summary>
        private static Vector2 OracleChildPosition(float left, float top) => new Vector2(left, -top);

        private HealthBarView BuildHealthBar(Transform parent)
        {
            // Top-left stat column. Placement is HudLayout's (HudSlot.HealthBar) — the Oracle's own
            // stat cluster is top-left, and the bottom-left now belongs to the weapon block
            // (`vis.textWeapon.y = screenY - 20`, GUI.as:314).
            var barRect = CreateHudElement("Health Bar", parent, new Vector2(320f, 24f));
            _layout.Register(HudSlot.HealthBar, barRect);
            _healthBarRoot = barRect.gameObject;

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

            // Health readout, to the right of the bar. HudLayout places it at x=348, which clears the
            // 320-wide bar rather than overlapping it.
            var textRect = CreateHudElement("Health Text", parent, new Vector2(160f, 24f));
            _layout.Register(HudSlot.HealthText, textRect);
            _healthTextRoot = textRect.gameObject;
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
            var barRect = CreateHudElement("Armour Bar", parent, new Vector2(320f, 12f));
            _layout.Register(HudSlot.ArmourBar, barRect);

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
            var textRect = CreateHudElement("Armour Text", parent, new Vector2(160f, 20f));
            _layout.Register(HudSlot.ArmourText, textRect);
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
        /// <para><b>World-anchored onto the target, not placed on the screen.</b> The Oracle's
        /// <c>pr_bar</c> is re-projected to the object being acted on every frame —
        /// <c>cam.setKoord(this.pr_bar, this.gg.actionObj.owner.X, this.gg.actionObj.owner.Y)</c>
        /// (<c>GUI.as:1302</c>) — and then rides with the interaction prompt
        /// (<c>celobj.y = pr_bar.y = …</c>, <c>GUI.as:1463</c>). The target's world position arrives
        /// through <see cref="IWorldAnchorSink"/> when the hold arms; see
        /// <see cref="ActionProgressBarView.SetWorldAnchor"/>.</para>
        ///
        /// <para><b>Why the slot is no longer registered with <see cref="HudLayoutScaler"/>.</b> Same
        /// reason as the reload bar: a screen anchor would be a second owner of a position that is a
        /// property of the object, not of the screen.</para>
        ///
        /// <para><b>Structure follows <see cref="ArmourBarView"/>'s, for the same reason:</b> the
        /// visuals hang off a child so the view can hide itself without deactivating the component that
        /// would have to show it again. Deactivating <c>barRect</c> instead would stop
        /// <c>LateUpdate</c> — which is where this view draws — and the bar would never come back.</para>
        /// </summary>
        private ActionProgressBarView BuildHoldBar(Transform parent)
        {
            var barRect = CreateHudElement("Hold Bar", parent, new Vector2(240f, 14f));

            // Deliberately NOT _layout.Register(HudSlot.HoldBar, barRect) — see the remarks.
            _holdAnchor = barRect.gameObject.AddComponent<WorldAnchoredHudElement>();
            _holdAnchor.WorldOffset = new Vector3(0f, HoldBarOffsetY, 0f);

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
            view.SetAnchor(_holdAnchor);

            // Nothing is being held at boot.
            visuals.gameObject.SetActive(false);

            return view;
        }

        /// <summary>
        /// The interaction prompt — the Oracle's <c>celobj</c> text (<c>GUI.as:1244-1467</c>).
        ///
        /// <para><b>World-anchored onto whatever the cursor is over.</b> The Oracle re-projects it every
        /// frame (<c>cam.setKoord(this.celobj, this.celObj.X, this.celObj.Y)</c>, <c>GUI.as:1461</c>), so
        /// this is not a "Press E" banner at the bottom of the screen. The target comes from
        /// <c>PlayerController.CurrentInteractTarget</c>, refreshed per frame in its <c>Update</c>; this
        /// method only builds the view.</para>
        ///
        /// <para><b>Visibility is a <see cref="CanvasGroup"/> alpha.</b> Same reason
        /// <see cref="BuildToast"/> documents: a deactivated GameObject runs no <c>LateUpdate</c>, and the
        /// component that places this element needs one to move it onto the new target. The group also
        /// turns raycasts off, so the prompt can never swallow a click meant for the world.</para>
        /// </summary>
        private InteractionPromptView BuildInteractionPrompt(Transform parent)
        {
            var rect = CreateHudElement("Interaction Prompt", parent, new Vector2(360f, 48f));

            // Deliberately NOT _layout.Register(HudSlot.InteractionPrompt, rect) — see the remarks.
            _promptAnchor = rect.gameObject.AddComponent<WorldAnchoredHudElement>();
            _promptAnchor.WorldOffset = new Vector3(0f, PromptOffsetY, 0f);

            var group = rect.gameObject.AddComponent<CanvasGroup>();
            group.alpha = 0f;
            group.blocksRaycasts = false;
            group.interactable = false;

            // A backing plate: the prompt is drawn over a lit world, and `celobj` is a symbol with its
            // own backing art in the SWF. This is the port's stand-in for that art, not an Oracle value.
            var plate = CreateImage("Plate", rect, new Color(0f, 0f, 0f, 0.62f));
            Stretch(plate.rectTransform);
            plate.raycastTarget = false;

            var textRect = CreateRect("Text", rect,
                anchor: new Vector2(0.5f, 0.5f), pivot: new Vector2(0.5f, 0.5f),
                anchoredPosition: Vector2.zero, size: Vector2.zero);
            Stretch(textRect, 6f);

            var text = textRect.gameObject.AddComponent<Text>();
            text.font = ResolveFont();
            text.fontSize = 18;
            text.color = PFE.UI.Common.PfeUiColors.Green;
            text.alignment = TextAnchor.MiddleCenter;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            text.text = string.Empty;

            var view = rect.gameObject.AddComponent<InteractionPromptView>();
            view.Construct(text, group, _promptAnchor);
            return view;
        }

        // ── Pickup feedback ───────────────────────────────────────────────────

        /// <summary>
        /// The corner message list — the oracle's <c>vis.info</c>, placed by
        /// <c>GUI.as:315 vis.info.x = screenX - 20</c> (right edge) with a Y the oracle never sets, hence
        /// the port-choice inset already documented on <see cref="HudSlot.InfoLog"/>.
        ///
        /// <para><b>No backing plate.</b> <c>vis.info</c> is bare text over the world — which is exactly
        /// why the oracle dims it to 0.2 when the cursor moves into its corner
        /// (<c>Ctr.as:528-535</c>): the panel is in the way of the world and gets out of it. A plate
        /// would make the dim pointless and the panel opaque. See <see cref="InfoLogView.Dimmed"/>.</para>
        ///
        /// <para><b>400 × 120, and the width is evidence rather than taste.</b> The dim strip in
        /// <c>Ctr.onMouseMove1</c> is <c>stageX &gt; stageWidth - 400</c> and <c>stageY &lt; 100</c>, so
        /// the panel the game expects a player to hover over occupies roughly the top-right 400 × 100.
        /// Six 18 px lines fit in 120.</para>
        /// </summary>
        private InfoLogView BuildInfoLog(Transform parent)
        {
            var rect = CreateHudElement("Info Log", parent, new Vector2(400f, 120f));
            _layout.Register(HudSlot.InfoLog, rect);

            // Alpha is the fade channel (see InfoLogView), so the group is the element's visibility
            // control and starts fully visible — the oracle's `infoAlpha` starts at 1.
            var group = rect.gameObject.AddComponent<CanvasGroup>();
            group.alpha = 1f;
            group.blocksRaycasts = false;
            group.interactable = false;

            var textRect = CreateRect("Text", rect,
                anchor: new Vector2(0.5f, 0.5f), pivot: new Vector2(0.5f, 0.5f),
                anchoredPosition: Vector2.zero, size: Vector2.zero);
            Stretch(textRect);

            var text = textRect.gameObject.AddComponent<Text>();
            text.font = ResolveFont();
            text.fontSize = 18;
            // `.r` — the HUD's body colour, #00FF99. `infoText` adds no colour span of its own; only
            // `@1` gets markup, and only `<b>` (GUI.as:1555-1560).
            text.color = PFE.UI.Common.PfeUiColors.Green;
            text.alignment = TextAnchor.UpperRight;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            text.text = string.Empty;

            var view = rect.gameObject.AddComponent<InfoLogView>();
            view.Construct(text, group);
            return view;
        }

        /// <summary>
        /// The rising label pool. Lives on the bootstrapper's own GameObject rather than on the canvas
        /// for the same reason <see cref="ProgressSoundView"/> does: it draws nothing itself, and the
        /// labels it creates are parented to the canvas explicitly (they must be, for
        /// <see cref="WorldAnchoredHudElement"/> — see <see cref="PickupFloatTextView.Construct"/>).
        /// </summary>
        private PickupFloatTextView BuildPickupFloatText(Transform canvas)
        {
            var view = gameObject.AddComponent<PickupFloatTextView>();
            view.Construct(canvas, ResolveFont());
            return view;
        }

        /// <summary>
        /// Subscribes to the take path. Every failure is reported separately, because a silent take is
        /// the defect this closes and "nothing happened" has three different causes here.
        /// </summary>
        private void BindPickupFeedback()
        {
            ItemPickupFeedback.Published -= OnItemPickedUp;
            ItemPickupFeedback.Published += OnItemPickedUp;

            if (_infoLog == null || _floatText == null || _impMess == null)
            {
                Debug.LogWarning("[HudBootstrapper] Pickup feedback is only partly built — " +
                                 $"info log: {_infoLog != null}, floating labels: {_floatText != null}, " +
                                 $"tutorial popup: {_impMess != null}.");
            }
        }

        /// <summary>
        /// One accepted take. The two halves are independent by design: <see cref="ItemPickedUpMessage.InfoLine"/>
        /// is null for armour and spells, which the oracle shows as a label with no list entry
        /// (<c>Invent.as:1119-1131</c>), and the label is shown for every take.
        /// </summary>
        private void OnItemPickedUp(ItemPickedUpMessage message)
        {
            if (!string.IsNullOrEmpty(message.InfoLine))
            {
                _infoLog?.Push(message.InfoLine);
            }

            _floatText?.Show(message.FloatLabel, message.ColourIndex, message.WorldPosition);

            // #3 — the once-per-save tutorial popup. It goes up last so it draws over the label the
            // same take just spawned, which is what the oracle's draw order does too (the label is a
            // world particle on `Sloy 5`; `vis.imp` is on the HUD above it).
            if (!string.IsNullOrEmpty(message.MessKey))
            {
                ShowImportantMessage(message.MessKey, message.Icon);
            }
        }

        private void OnDestroy()
        {
            ItemPickupFeedback.Published -= OnItemPickedUp;
        }

        /// <summary>
        /// The one-time tutorial popup. Anchored by <c>GUI.as:347-348</c>
        /// (<c>imp.x = round(screenX/2)</c>, <c>imp.y = round(screenY/2 - 50)</c>), which the
        /// <see cref="HudSlot.ImportantMessage"/> row already carries.
        ///
        /// <para><b>The vertical order is the oracle's, and it is the only part of the layout that
        /// is.</b> <c>impMess</c> computes <c>imp.txt.y = imp.ico.y + imp.ico.height + 15</c>
        /// (<c>GUI.as:1925</c>) — the body sits <b>below the icon</b>, with a 15 px gap. The title's
        /// position is never set, so it is authored in the symbol; putting it above the icon is the
        /// only arrangement in which that line and an authored title both fit. The panel's 620 × 340,
        /// the 96 px icon and the 18 px body are this port's — the <c>imp</c> symbol carries its own
        /// geometry in the SWF and the importer flattened it, the same gap
        /// <see cref="HudLayout"/> marks for the stat cluster.</para>
        ///
        /// <para><b>The plate and border are borrowed from the dialogue window.</b> Whether the
        /// oracle's <c>imp</c> symbol has a backing plate is not recoverable from the source, and bare
        /// text over the world at the screen's centre would be unreadable in a lit room. Reusing
        /// <see cref="HudLayout.DialoguePlateFill"/> keeps the two centred panels looking like one HUD
        /// rather than two.</para>
        /// </summary>
        private ImpMessView BuildImportantMessage(Transform parent)
        {
            const float width = 620f;
            const float height = 340f;

            var root = CreateHudElement("Important Message", parent, new Vector2(width, height));
            _layout.Register(HudSlot.ImportantMessage, root);

            // Starts hidden — `imp.visible = false` at GUI.as:289 — and the group is the visibility
            // channel so the view can toggle it without deactivating the object it lives on.
            var group = root.gameObject.AddComponent<CanvasGroup>();
            group.alpha = 0f;
            group.blocksRaycasts = false;
            group.interactable = false;

            var plate = CreateImage("Plate", root, HudLayout.DialoguePlateFill);
            Stretch(plate.rectTransform);
            plate.raycastTarget = false;

            CreateBorder(root, HudLayout.DialogueBorderColor, width, height,
                HudLayout.DialogueBorderThickness);

            // Title — `imp.title.text = ntitle` (GUI.as:1924). 18 px from the panel's top edge.
            var titleRect = CreateRect("Title", root,
                anchor: new Vector2(0.5f, 1f), pivot: new Vector2(0.5f, 1f),
                anchoredPosition: new Vector2(0f, -18f), size: new Vector2(width - 60f, 40f));

            var title = titleRect.gameObject.AddComponent<Text>();
            title.font = ResolveFont();
            title.fontSize = 24;
            title.color = PFE.UI.Common.PfeUiColors.Green;
            title.alignment = TextAnchor.MiddleCenter;
            title.horizontalOverflow = HorizontalWrapMode.Overflow;
            title.verticalOverflow = VerticalWrapMode.Overflow;
            title.raycastTarget = false;
            title.text = string.Empty;

            // Icon — `imp.ico.gotoAndStop(nico)` (:1912-1921). 96 px, directly under the title.
            var iconRect = CreateRect("Icon", root,
                anchor: new Vector2(0.5f, 1f), pivot: new Vector2(0.5f, 1f),
                anchoredPosition: new Vector2(0f, -68f), size: new Vector2(96f, 96f));

            var icon = iconRect.gameObject.AddComponent<Image>();
            icon.raycastTarget = false;
            icon.preserveAspect = true;
            // Frame 1 of `imp.ico` is blank, and that is the oracle's own fallback (:1912, :1921) —
            // so a popup with no icon draws nothing rather than a placeholder box.
            icon.enabled = false;
            icon.sprite = null;

            // Body — `imp.txt`, at icon bottom + 15 (:1925). Top of the body is therefore
            // 68 + 96 + 15 = 179 below the panel's top edge.
            const float bodyTop = 68f + 96f + 15f;
            var bodyRect = CreateRect("Body", root,
                anchor: new Vector2(0.5f, 1f), pivot: new Vector2(0.5f, 1f),
                anchoredPosition: new Vector2(0f, -bodyTop),
                size: new Vector2(width - 60f, height - bodyTop - 20f));

            var body = bodyRect.gameObject.AddComponent<Text>();
            body.font = ResolveFont();
            body.fontSize = 18;
            body.color = PFE.UI.Common.PfeUiColors.Green;
            body.alignment = TextAnchor.UpperLeft;
            body.horizontalOverflow = HorizontalWrapMode.Wrap;
            body.verticalOverflow = VerticalWrapMode.Overflow;
            body.raycastTarget = false;
            body.text = string.Empty;

            var view = root.gameObject.AddComponent<ImpMessView>();
            view.Construct(title, body, icon, group);
            return view;
        }

        /// <summary>
        /// One frame of the popup: hold the player, and release on the frame it is dismissed.
        ///
        /// <para><b>Called above the early-outs</b> in <c>Update</c> for the same reason the interaction
        /// prompt is: the steady state returns as soon as the weapon and stats are bound, and a popup
        /// driven from below that return would never dismiss.</para>
        ///
        /// <para><b>The lock is re-asserted every frame, not set once.</b> That is what the oracle does
        /// (<c>GUI.as:2069-2076</c> forces <c>ctr.active = false</c> again on every frame while
        /// <c>guiPause</c> is set), and it is also what makes this robust against anything else clearing
        /// the flag.</para>
        /// </summary>
        private void UpdateImportantMessage()
        {
            if (_impMess == null) return;

            _impMess.Tick();

            // Reconciled off Visible rather than off Tick's return value, so any other path that hides
            // the popup (a room change calling Dismiss, a future script) cannot leave the player
            // frozen. ModalInputBlocked has exactly one writer — this method — so mirroring Visible is
            // exact rather than approximate.
            if (_impMess.Visible)
            {
                if (_player == null) _player = ResolvePlayer();
                if (_player != null) _player.ModalInputBlocked = true;
            }
            else if (_player != null && _player.ModalInputBlocked)
            {
                _player.ModalInputBlocked = false;
            }
        }

        /// <summary>
        /// The popup's two texts, resolved from the <c>item</c> table — <c>Res.txt("i", key)</c> for the
        /// title (the <c>&lt;n&gt;</c> child) and <c>Res.txt("i", key, 2)</c> for the body (the
        /// <c>&lt;mess&gt;</c> child, <c>razd</c> 2 of <c>n / info / mess / help</c>) — and then the
        /// once-per-save latch.
        ///
        /// <para><b>The latch is written here, not in the publisher, and the measurement that decided
        /// it is a count.</b> 45 of the 500 shipped <c>&lt;item&gt;</c> rows carry a <c>@mess</c> key
        /// (plus 4 of the 213 <c>&lt;weapon&gt;</c> rows), and only <b>21</b> of those 45 keys have a
        /// matching <c>&lt;item id=key&gt;</c> row in the shipped <c>text_en.xml</c>. The missing 24+
        /// are the skill-book tutorials (<c>melee</c>, <c>smallguns</c>, <c>sneak</c>, <c>repair</c>,
        /// <c>science</c>, the nine <c>sp_*</c> spells, the <c>mod_*</c>/<c>impl_*</c> implants,
        /// <c>lockpick</c>, <c>tele</c> …). If the latch were written by the publisher, every one of
        /// those would be burned the first time its book was picked up — permanently, even against a
        /// more complete language file. Writing it here means a miss costs one warning per pickup and
        /// keeps the tutorial alive.</para>
        ///
        /// <para>So the two halves of the oracle's <c>triggers["mess_" + key]</c> are split: the take
        /// path <b>reads</b> it to decide whether to offer the popup, and this <b>writes</b> it on the
        /// one path that can know the text exists. No other writer touches this key.</para>
        ///
        /// <para><b>The body is substituted, the title is not — and that asymmetry is the Oracle's.</b>
        /// <c>Res.txt</c> runs its <c>@n</c> substitution only under <c>if(razd >= 1 || dop)</c>
        /// (<c>Res.as:108</c>), so the razd-0 title is shown raw while the razd-2 body is not. Within the
        /// body it runs <b>two</b> passes in a fixed order — the entry's own <c>@s1</c>
        /// (<c>:110-112</c>), then the <c>&lt;mess&gt;</c> child's (<c>:116-119</c>). All nine
        /// <c>sp_*</c> spell books point at <c>mess='spell'</c>, whose body is
        /// <c>&lt;mess s1="keyDef"&gt;…you can apply it by pressing the @1 key…</c>, so skipping this
        /// showed the placeholder to the player. <see cref="PipTextTable.KeysOfChild"/> is what makes
        /// the second pass reachable at all.</para>
        /// </summary>
        private void ShowImportantMessage(string messKey, Sprite icon)
        {
            string table = PFE.UI.PipBuck.PipTextTable.TableFor("i");
            PFE.UI.PipBuck.PipTextTable text = PfeTextTableProvider.Shared;

            if (!text.TryGetInTable(table, messKey, 0, out string title)
                || !text.TryGetInTable(table, messKey, 2, out string body)
                || string.IsNullOrEmpty(body))
            {
                Debug.LogWarning($"[HudBootstrapper] The popup for '{messKey}' has no " +
                                 $"<item id=\"{messKey}\"><n>/<mess> in the localisation document — " +
                                 "it will not be shown, and the mess_ latch was NOT written so a " +
                                 "more complete text file can still deliver it.");
                return;
            }

            // Res.as:108-119 — both substitution passes, in the Oracle's order. The title is razd 0 and
            // is deliberately left raw: the Oracle's gate is `razd >= 1 || dop`.
            body = PFE.UI.PipBuck.PipTextTable.ApplyKeys(body, text.KeysOfEntry(table, messKey), text.Key);
            body = PFE.UI.PipBuck.PipTextTable.ApplyKeys(body, text.KeysOfChild(table, messKey, 2), text.Key);

            // Invent.as:1296 — `triggers["mess_" + l.mess] = 1`. Through the campaign trigger store so
            // it round-trips in a save exactly as the oracle's does.
            CampaignManager.Current?.SetTrigger(PickupFeedbackRules.MessTriggerKey(messKey), 1);

            _impMess?.Show(title, body, icon);
            // Snd.ps("quest") — GUI.as:1928. The same clip the quest-given chime uses, which is why
            // this goes through UiSoundService rather than a separate id.
            _sound?.PlayQuestGiven();
        }

        // ── Script presentation ───────────────────────────────────────────────

        /// <summary>
        /// The banner — the Oracle's <c>vis.mess</c>, driven by <c>GUI.messText</c>
        /// (<c>GUI.as:1656-1691</c>).
        ///
        /// <para><b>Its width is the scaler's, not this method's.</b> <c>GUI.as:325-335</c> recomputes the
        /// toast's width as <c>screenX - 550</c> clamped to a 500 minimum, and
        /// <see cref="HudLayoutScaler"/> re-runs that on every canvas resize through
        /// <see cref="HudLayoutScaler.ToastBox"/> — so the width set here is only the value before the
        /// first layout pass.</para>
        /// </summary>
        private ScriptToastView BuildToast(Transform parent)
        {
            var rect = CreateHudElement("Script Banner", parent,
                new Vector2(HudLayout.ToastWidth(HudLayout.ReferenceWidth), 64f));
            _layout.Register(HudSlot.Toast, rect);
            _layout.ToastBox = rect;

            // Alpha, not SetActive. The Oracle hides the banner by fading `mess.alpha` to 0 and only then
            // clearing `visible` (GUI.as:2050-2059); and deactivating the GameObject would stop the very
            // Update that drives the fade — the deadlock ReloadIndicatorView documents from the other
            // side.
            var group = rect.gameObject.AddComponent<CanvasGroup>();
            group.alpha = 0f;
            group.blocksRaycasts = false;
            group.interactable = false;

            // A backing plate. `vis.mess` is a symbol with its own background art, so text alone would be
            // unreadable over a lit world; this is the port's stand-in for that art, not an Oracle value.
            var plate = CreateImage("Plate", rect, new Color(0f, 0f, 0f, 0.72f));
            Stretch(plate.rectTransform);
            plate.raycastTarget = false;

            var textRect = CreateRect("Text", rect,
                anchor: new Vector2(0.5f, 0.5f), pivot: new Vector2(0.5f, 0.5f),
                anchoredPosition: Vector2.zero, size: Vector2.zero);
            Stretch(textRect, 10f);

            var text = textRect.gameObject.AddComponent<Text>();
            text.font = ResolveFont();
            text.fontSize = 22;
            text.color = Color.white;
            text.alignment = TextAnchor.MiddleCenter;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            text.text = string.Empty;

            var view = rect.gameObject.AddComponent<ScriptToastView>();
            view.Construct(text, group, rect, _layout);
            return view;
        }

        /// <summary>
        /// The dialogue window and the terminal reader — the two branches of <c>GUI.dialText</c>
        /// (<c>GUI.as:1741-1905</c>).
        ///
        /// <para><b>Sizes are the Oracle's where it has one.</b> The window is 1200 wide because that is
        /// <c>HudLayout.DialogueAuthoredWidth</c> and <c>GUI.as:336-345</c> scales it <i>down</i> below a
        /// 1200 px screen and never up; <see cref="HudLayoutScaler.DialogueWindow"/> re-applies that scale
        /// on resize. The panel's 760 × 420 is the port's — the Oracle's <c>inform</c> symbol carries its
        /// own size in the SWF, which the importer flattens, the same gap <see cref="HudLayout"/> marks for
        /// the stat cluster.</para>
        /// </summary>
        private ScriptDialogueView BuildDialogue(Transform parent)
        {
            // ── The dialogue window (reg == 0) ──
            //
            // The size, the plate fill, the border and the text rect are all the SWF's own numbers —
            // see the geometry block on HudLayout. The port previously drew a 1200x150 flat black plate
            // with no border and a 24 px white body, which is neither the Oracle's frame nor its type.
            var window = CreateHudElement("Script Dialogue", parent,
                new Vector2(HudLayout.DialogueAuthoredWidth, HudLayout.DialogueAuthoredHeight));
            _layout.Register(HudSlot.Dialogue, window);
            _layout.DialogueWindow = window;

            var windowPlate = CreateImage("Plate", window, HudLayout.DialoguePlateFill);
            Stretch(windowPlate.rectTransform);
            windowPlate.raycastTarget = false;

            // The 1 px #33CC99 frame (shape 4520's LINESTYLE2). Drawn over the plate, and built before
            // the text so the text sits on top of it in the sibling order.
            CreateBorder(window, HudLayout.DialogueBorderColor,
                HudLayout.DialogueAuthoredWidth, HudLayout.DialogueAuthoredHeight,
                HudLayout.DialogueBorderThickness);

            // Child `txt` (4521): a 944x172 field at (218,30) inside the 1200x225 plate, 16 px
            // `_sans` in #00FF99, left-aligned, multiline, word-wrapped.
            var windowTextRect = CreateRect("Text", window,
                anchor: new Vector2(0f, 1f), pivot: new Vector2(0f, 1f),
                anchoredPosition: OracleChildPosition(HudLayout.DialogueTextLeft, HudLayout.DialogueTextTop),
                size: new Vector2(HudLayout.DialogueTextWidth, HudLayout.DialogueTextHeight));

            var windowText = windowTextRect.gameObject.AddComponent<Text>();
            windowText.font = ResolveFont();
            windowText.fontSize = HudLayout.DialogueFontSize;
            windowText.color = HudLayout.DialogueTextColor;
            // AS3 `align="0"` — left, with the field's own top-left origin. The port used MiddleLeft,
            // which centres a single short line against a field sized for several.
            windowText.alignment = TextAnchor.UpperLeft;
            windowText.horizontalOverflow = HorizontalWrapMode.Wrap;
            windowText.verticalOverflow = VerticalWrapMode.Overflow;
            windowText.raycastTarget = false;
            windowText.text = string.Empty;

            // The `@p` frame label. Deliberately left unassigned: the Oracle's `@p` selects a portrait
            // (GUI.as:1831-1851), not a name, so printing "lp1" would be inventing text. The view resolves
            // it to a sprite through DialoguePortraitFrames instead.
            Text speakerLabel = null;

            // Child `portret` (4638) at (32,32) — the speaker portrait, on depth 3 so it draws over `txt`.
            // The two do not overlap (the art is 161 px wide at x=32 and the text field starts at x=218),
            // so the ordering is fidelity rather than a fix for an occlusion.
            //
            // The rect is the TEXTURE's size, not the art's: every exported frame is 389x325 with the
            // 161x161 artwork at canvas (115,20), so the placement subtracts that offset. See the canvas
            // block on HudLayout for the measurement.
            var portraitRect = CreateRect("Portrait", window,
                anchor: new Vector2(0f, 1f), pivot: new Vector2(0f, 1f),
                anchoredPosition: HudLayout.DialoguePortraitCanvasLeftTopOffset(),
                size: new Vector2(HudLayout.DialoguePortraitCanvasWidth,
                                  HudLayout.DialoguePortraitCanvasHeight));

            var portrait = portraitRect.gameObject.AddComponent<Image>();
            portrait.raycastTarget = false;
            portrait.preserveAspect = false;
            // Starts on the Oracle's blank frame 1, i.e. nothing drawn until a line names a speaker.
            portrait.enabled = false;
            portrait.sprite = null;

            // ── The terminal reader (reg >= 1) ──
            var panel = CreateHudElement("Script Panel", parent, new Vector2(760f, 420f));
            _layout.Register(HudSlot.TerminalReader, panel);

            var panelPlate = CreateImage("Plate", panel, new Color(0f, 0f, 0f, 0.85f));
            Stretch(panelPlate.rectTransform);
            panelPlate.raycastTarget = false;

            var panelTextRect = CreateRect("Text", panel,
                anchor: new Vector2(0.5f, 0.5f), pivot: new Vector2(0.5f, 0.5f),
                anchoredPosition: Vector2.zero, size: Vector2.zero);
            Stretch(panelTextRect, 24f);

            var panelText = panelTextRect.gameObject.AddComponent<Text>();
            panelText.font = ResolveFont();
            panelText.fontSize = 20;
            panelText.color = Color.white;
            panelText.alignment = TextAnchor.UpperLeft;
            panelText.horizontalOverflow = HorizontalWrapMode.Wrap;
            panelText.verticalOverflow = VerticalWrapMode.Overflow;
            panelText.raycastTarget = false;
            panelText.text = string.Empty;

            // `inform.but0.visible = reg == 2` (GUI.as:1872) — the OK button. Built here so the branch is
            // expressible; nothing wires a click to it yet, and that is a gap rather than a port choice.
            var buttonRect = CreateRect("OK Button", panel,
                anchor: new Vector2(0.5f, 0f), pivot: new Vector2(0.5f, 0f),
                anchoredPosition: new Vector2(0f, 20f), size: new Vector2(140f, 36f));
            var buttonImage = buttonRect.gameObject.AddComponent<Image>();
            buttonImage.color = new Color(0.25f, 0.25f, 0.3f, 0.95f);

            var view = window.gameObject.AddComponent<ScriptDialogueView>();
            view.Construct(window, windowText, panel, panelText, speakerLabel, buttonRect.gameObject,
                portrait, _composition != null ? _composition.dialoguePortraits : null);

            return view;
        }

        /// <summary>
        /// The blackout overlay — the Oracle's <c>World.vblack</c> plus the <c>cam.dblack</c> fade
        /// (<c>Camera.as:291-308</c>).
        ///
        /// <para><b>Two GameObjects, and the split is load-bearing.</b> The view lives on an
        /// always-active root while the black <see cref="Image"/> is a child it switches off at alpha 0.
        /// Putting the view on the image itself would stop its <c>Update</c> the moment the blackout
        /// finished — and the fade that brings it back runs in exactly that <c>Update</c>. That is the
        /// same deadlock <c>ArmourBarView</c> and <c>ReloadIndicatorView</c> each document from their own
        /// direction.</para>
        ///
        /// <para><b>It is moved to the back of the sibling order.</b> <c>vblack</c> is a world-layer
        /// object and the GUI draws over it, so the banner stays readable on a black screen.</para>
        /// </summary>
        private ScriptOverlayView BuildOverlay(Transform parent)
        {
            var root = CreateHudElement("Script Overlay", parent, Vector2.zero);
            Stretch(root);
            root.SetAsFirstSibling();

            var blackRect = CreateRect("Black", root,
                anchor: new Vector2(0.5f, 0.5f), pivot: new Vector2(0.5f, 0.5f),
                anchoredPosition: Vector2.zero, size: Vector2.zero);
            Stretch(blackRect);

            var image = blackRect.gameObject.AddComponent<Image>();
            image.color = new Color(0f, 0f, 0f, 0f);

            // A full-screen raycast target would swallow every click in the game — including the
            // right-click that skips a dialogue. The Oracle's `vblack` is not interactive either.
            image.raycastTarget = false;

            var view = root.gameObject.AddComponent<ScriptOverlayView>();
            view.Construct(image);
            return view;
        }

        /// <summary>
        /// Hands the three views their subscribers from the container.
        ///
        /// <para><b>Each is reported separately when missing, because each fails differently.</b> A
        /// missing prompt subscriber is a campaign that opens on a black screen with no text; a missing
        /// dialogue subscriber is a cutscene whose lines never appear; a missing overlay subscriber is an
        /// opening that happens on a lit screen. Collapsing them into one warning would hide which.</para>
        /// </summary>
        private void BindScriptViews()
        {
            if (_resolver == null)
            {
                Debug.LogWarning("[HudBootstrapper] No container resolver — the script banner, the dialogue " +
                                 "window and the blackout overlay are wired to nothing.");
                return;
            }

            if (_resolver.TryResolve(out ISubscriber<TutorialPromptMessage> prompts))
            {
                _toast?.Bind(prompts);
            }
            else
            {
                Debug.LogWarning("[HudBootstrapper] No ISubscriber<TutorialPromptMessage> — every script " +
                                 "`mess` will be published into nothing.");
            }

            if (_resolver.TryResolve(out ISubscriber<ScriptDialogueMessage> dialogues))
            {
                _dialogue?.Bind(dialogues);
            }
            else
            {
                Debug.LogWarning("[HudBootstrapper] No ISubscriber<ScriptDialogueMessage> — every script " +
                                 "`dial`/`dialog`/`inform`/`landlevel` will be published into nothing.");
            }

            if (_resolver.TryResolve(out ISubscriber<ScriptOverlayMessage> overlays))
            {
                _overlay?.Bind(overlays);
            }
            else
            {
                Debug.LogWarning("[HudBootstrapper] No ISubscriber<ScriptOverlayMessage> — `black`, `dblack` " +
                                 "and `gui off/on` will be published into nothing.");
            }
        }

        /// <summary>
        /// Attaches the three progression chimes.
        ///
        /// <para><b>Every failure is reported separately, because each one is a different silence.</b>
        /// No <c>UiSoundService</c> is a scene with no <c>SoundService</c> (a test harness, an early
        /// frame) and is normal enough to warrant a single line rather than a warning; a missing
        /// <c>LevelUpMessage</c> broker is a mis-registered container and is the difference between
        /// "the level-up chime is gone" and "nothing about level-ups is observable"; a missing quest
        /// broker is the same for quests. Collapsing them would leave the next person unable to tell
        /// which of the three is at fault.</para>
        /// </summary>
        private void BindProgressSounds()
        {
            if (_resolver == null)
            {
                Debug.LogWarning("[HudBootstrapper] No container resolver — level-up and quest chimes " +
                                 "are wired to nothing.");
                return;
            }

            if (!_resolver.TryResolve(out UiSoundService sound))
            {
                Debug.Log("[HudBootstrapper] No UiSoundService — progression chimes will be silent " +
                          "(the scene has no SoundService; this is expected in a headless harness).");
                return;
            }

            // Kept for the tutorial popup's `Snd.ps("quest")` (GUI.as:1928), which is not a
            // ProgressSoundView concern — it fires from the take path, not from a message.
            _sound = sound;

            _resolver.TryResolve(out ISubscriber<LevelUpMessage> levelUps);
            _resolver.TryResolve(out ISubscriber<QuestStateChangedMessage> quests);

            if (levelUps == null)
            {
                Debug.LogWarning("[HudBootstrapper] No ISubscriber<LevelUpMessage> — the `levelup` chime " +
                                 "(Pers.as:1140) will never play.");
            }

            if (quests == null)
            {
                Debug.LogWarning("[HudBootstrapper] No ISubscriber<QuestStateChangedMessage> — the `quest` " +
                                 "and `quest_ok` chimes (Game.as:543, Quest.as:693) will never play.");
            }

            _progressSounds?.Bind(sound, levelUps, quests);

            Debug.Log($"[HudBootstrapper] Progression chimes bound (level-up: {levelUps != null}, " +
                      $"quest: {quests != null}).");
        }

        private AmmoCounterView BuildAmmoCounter(Transform parent)
        {
            // Bottom-left, NOT bottom-right: this readout is AS3's `textWeapon.holder` — the magazine
            // line of the weapon block — and the block is pinned to the bottom-left by
            // `vis.textWeapon.y = screenY - 20` (GUI.as:314). It sat bottom-right in the port before
            // Item I4, which was never an Oracle position.
            var rect = CreateHudElement("Ammo Counter", parent, new Vector2(220f, 32f));
            _layout.Register(HudSlot.WeaponBlock, rect);

            // Text before the view: AmmoCounterView.Awake does GetComponent<Text>().
            var text = rect.gameObject.AddComponent<Text>();
            text.font = ResolveFont();
            text.fontSize = 24;
            text.color = Color.white;
            text.alignment = TextAnchor.MiddleLeft;
            text.text = "-- / --";

            return rect.gameObject.AddComponent<AmmoCounterView>();
        }

        /// <summary>
        /// The reload bar. <b>World-anchored onto the player, not placed on the screen.</b> In the
        /// Oracle this is not a HUD element at all: <c>reloadBar</c> is added to the player's own world
        /// sprite (<c>UnitPlayer.as:338-340</c>) at <c>y = -scY - 10</c> (<c>:367</c>) — 10 source pixels
        /// above the top of the player's collision box. For <c>littlepip</c> that box is
        /// <c>sY='70'</c> (<c>AllData.as:21</c>), so the bar rests 80 source pixels above the player's
        /// feet, which at this project's 100 px per world unit (<c>WorldConstants.ACTION_REACH_PIXELS</c>)
        /// is exactly <b>0.8 world units up</b>. Hence <see cref="ReloadBarOffsetY"/>.
        ///
        /// <para><b>Why the slot is no longer registered with <see cref="HudLayoutScaler"/>.</b> That
        /// scaler exists to re-apply the Oracle's <c>resizeScreen</c> anchors, which are properties of
        /// the <i>screen</i>. A world-anchored element is re-placed every frame by
        /// <see cref="WorldAnchoredHudElement"/> instead, and two owners for one position is how a HUD
        /// ends up correct at 16:9 and wrong at 21:9.</para>
        ///
        /// <para><b>Why the GameObject stays ACTIVE and the image starts transparent.</b> Deactivating it
        /// would stop <c>Start</c> from ever running, and <c>Start</c> is where the view binds the very
        /// subscription that would re-activate it — a deadlock. Transparency, not deactivation, is the
        /// initial hide.</para>
        /// </summary>
        private ReloadIndicatorView BuildReloadIndicator(Transform parent)
        {
            var rect = CreateHudElement("Reload Indicator", parent, new Vector2(96f, 12f));

            // Deliberately NOT _layout.Register(HudSlot.ReloadIndicator, rect) — see the remarks.
            _reloadAnchor = rect.gameObject.AddComponent<WorldAnchoredHudElement>();
            _reloadAnchor.WorldOffset = new Vector3(0f, ReloadBarOffsetY, 0f);

            // Image before the view: ReloadIndicatorView.Awake does GetComponent<Image>() and caches
            // its colour as the "not reloading" colour — so starting transparent is what makes the
            // idle state invisible.
            var image = rect.gameObject.AddComponent<Image>();
            image.color = new Color(1f, 0.5f, 0f, 0f);
            image.type = Image.Type.Filled;
            image.fillMethod = Image.FillMethod.Horizontal;
            image.fillAmount = 0f;

            return rect.gameObject.AddComponent<ReloadIndicatorView>();
        }

        /// <summary>
        /// <c>reloadBar.y = -scY - 10</c> (<c>UnitPlayer.as:367</c>) for <c>littlepip</c>, in world units:
        /// <c>(70 + 10) / 100</c>. See <see cref="BuildReloadIndicator"/>.
        /// </summary>
        private const float ReloadBarOffsetY = 0.8f;

        /// <summary>
        /// The hold bar's resting lift above the target's origin, in world units.
        ///
        /// <para><b>A port choice, and labelled as one.</b> The Oracle projects <c>pr_bar</c> to the
        /// target's own coordinates with no extra offset —
        /// <c>cam.setKoord(this.pr_bar, this.gg.actionObj.owner.X, this.gg.actionObj.owner.Y)</c>
        /// (<c>GUI.as:1302</c>) — but <c>pr_bar</c> is a SWF symbol whose <i>internal</i> geometry this
        /// pipeline never imported, so "at the origin" is not a number the port can copy. Half a world
        /// unit lifts it clear of the object's feet without floating it into the prompt's text. The
        /// world-anchoring is the ported behaviour; this number is the port's.</para>
        /// </summary>
        private const float HoldBarOffsetY = 0.5f;

        /// <summary>
        /// The interaction prompt's resting lift above the target's origin, in world units. Same
        /// caveat as <see cref="HoldBarOffsetY"/> — and the same value, because the Oracle ties the two
        /// together: <c>celobj.y = pr_bar.y = …</c> (<c>GUI.as:1463</c>).
        /// </summary>
        private const float PromptOffsetY = 0.5f;

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

        /// <summary>
        /// Points the reload bar at the player, whose head it hangs over — the port's replacement for
        /// the Oracle adding <c>reloadBar</c> to the player's own sprite
        /// (<c>UnitPlayer.as:338-340</c>). Re-run on the same rare rebinds as <see cref="BindHoldBar"/>;
        /// <see cref="WorldAnchoredHudElement.SetTarget"/> is idempotent, so a repeated call costs one
        /// reference write rather than anything worth guarding.
        /// </summary>
        private void BindReloadAnchor()
        {
            if (_reloadAnchor == null || _player == null)
            {
                return;
            }

            _reloadAnchor.SetTarget(_player.transform);
        }

        /// <summary>
        /// Drives the world-anchored interaction prompt from the player's current cursor target — the
        /// port of <c>GUI.setCelObj</c> (<c>GUI.as:1244-1467</c>), which runs off
        /// <c>World.w.loc.celObj</c> once per frame from <c>GUI.step</c> (<c>:1949</c>).
        ///
        /// <para><b>Push from here rather than from the player.</b> The player owns <i>what</i> is
        /// targeted; the HUD owns <i>how</i> it is drawn. Keeping the view out of the player keeps
        /// <c>PlayerController</c> free of a HUD reference, which is the same split every other element
        /// in this class uses.</para>
        /// </summary>
        private void UpdateInteractionPrompt()
        {
            if (_prompt == null)
            {
                return;
            }

            if (_player == null)
            {
                _prompt.Hide();
                return;
            }

            IInteractable target = _player.CurrentInteractTarget;

            // `target == null` alone is NOT enough, and the gap cost a MissingReferenceException out of
            // this method. Two separate reasons, both about C# not doing what Unity's `==` does:
            //
            //   * `IInteractable` is an interface, so `target == null` is plain reference equality. It
            //     does NOT go through UnityEngine.Object's overloaded `==`, so a DESTROYED component is
            //     not null here.
            //   * `target is Component` below is a plain C# type test, and it is TRUE for a destroyed
            //     object — the managed wrapper outlives the native object.
            //
            // So a destroyed pickup reached `component.transform` and threw. The player recomputes this
            // reference once per frame in its own Update, but a pickup can die earlier in the same frame:
            // PlayerAutoPickup collects a container drop from FixedUpdate and Despawn()s it
            // (WorldItemPickup.Interact), which runs before every Update. If this HUD's Update is
            // ordered before PlayerController's, the reference is one frame stale and already dead.
            // Hence the explicit Unity-null test, which DOES use the overloaded operator.
            if (target == null || (target is UnityEngine.Object unityTarget && unityTarget == null))
            {
                _prompt.Hide();
                return;
            }

            // The anchor is the object's own position — AS3 passes celObj.X/Y, not the cursor hit
            // point. Every shipped implementor is a MonoBehaviour, so the transform is the object's;
            // a non-Component implementor falls back to the player, which is visibly wrong but is not
            // reachable from any shipped type.
            Vector3 world = target is Component component
                ? component.transform.position
                : _player.transform.position;

            string name = target is IInteractableName named ? named.DisplayName : null;
            _prompt.Show(name, target.ActionText, world);
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
