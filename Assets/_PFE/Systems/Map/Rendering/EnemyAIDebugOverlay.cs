using System.Collections.Generic;
using System.Text;
using PFE.Core;
using PFE.Entities.Enemies;
using PFE.Entities.Units;
using PFE.Systems.Map;
using UnityEngine;

namespace PFE.Systems.Map.Rendering
{
    /// <summary>
    /// F3 — the enemy-AI perception overlay. Draws what each enemy <i>can</i> perceive (vision arc,
    /// hearing circle, close-proximity bubble), what it <i>is</i> perceiving (the eye-to-target ray, the
    /// target and last-known markers), and — for a selected unit — its whole state, its blackboard
    /// timers and a verdict block. The filter bits are toggled from the panel or the console.
    ///
    /// <para><b>The three senses are indistinguishable in play and have different causes.</b> An enemy
    /// that is chasing tells you nothing about <i>which</i> of vision, hearing or close proximity fired;
    /// an enemy that is <i>not</i> chasing while standing next to the player is the bug this exists to
    /// find. So each sense gets its own drawing and its own toggle rather than one combined "aware"
    /// indicator.</para>
    ///
    /// <para><b>Every number is read, none is re-derived.</b> The eye point comes from
    /// <see cref="IEnemyBrain.EyePositionPixels"/> — the same method the sensor calls in its own tick,
    /// so the drawn ray cannot start somewhere the raycast does not. The vision radius comes from the
    /// live <see cref="EnemySensors"/> field, and the hearing radius is
    /// <see cref="EnemySensors.HearingRadiusPixels"/> evaluated at the loudest candidate's noise — the
    /// same product the sensor uses, which is why the circle shrinks to nothing when the player stands
    /// still instead of being a fixed ring. The verdict block calls
    /// <see cref="EnemySensors.CheckVisionAngle"/>, <see cref="EnemySensors.HearIntensity"/> and
    /// <see cref="EnemySensors.GetUnitCenterPixels"/> — the shipped code, not a copy of it — and the
    /// line-of-sight line reports the brain's own <c>HasLineOfSight</c> rather than re-raycasting. A
    /// second implementation of a sense would be a thing that can disagree with the sense.</para>
    ///
    /// <para><b>Why the timers are shown together.</b> <c>shok</c> and <c>aiSpok</c> are two different
    /// countdowns that a previous investigation confused with each other, and the confusion was
    /// invisible because nothing printed either one. Four timers on one panel
    /// (<c>AlertTimerTicks</c>/<c>aiSpok</c>, <c>StateTimerTicks</c>/<c>aiTCh</c>,
    /// <c>ShockTimerTicks</c>/<c>shok</c>, <c>AttackCooldownTicks</c>) makes that mistake much harder
    /// to repeat.</para>
    ///
    /// <para><b>Full geometry for the selected unit, the vision arc for everyone else.</b> Three circles
    /// per enemy across a room of twenty is unreadable; the arc is the one that answers "why did this
    /// one not see me", so it is the one drawn for all. The panel says so, because an overlay that
    /// silently draws a subset is a lying overlay.</para>
    ///
    /// <para><b>Not in AS3.</b> The oracle has no overlay. This is port-side instrumentation, so its
    /// shape is chosen for usefulness and is not evidence about behaviour. The <i>values</i> it reports
    /// are evidence; the way they are drawn is not.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class EnemyAIDebugOverlay : MonoBehaviour
    {
        /// <summary>Toggle the whole overlay. F3 is free (F1 console, F2 player, F5/F6 colliders,
        /// F8 triggers, F9 doors, F10 objects).</summary>
        public const KeyCode ToggleHotkey = KeyCode.F3;

        /// <summary>Cycle the selected enemy forward / back. Brackets because every F-key is taken and
        /// these two read as "step through a list".</summary>
        public const KeyCode NextHotkey = KeyCode.RightBracket;
        public const KeyCode PrevHotkey = KeyCode.LeftBracket;

        /// <summary>Scene rescan rate. 5 Hz, matching the other overlays: far below the cost of a
        /// per-frame <c>FindObjectsByType</c> over a room, far above the rate anyone reads a panel.</summary>
        private const float RefreshInterval = 0.2f;

        /// <summary>Above every map sprite, same as the collider overlay.</summary>
        private const int OverlaySortingOrder = 32000;

        /// <summary>Backstop only; the view filter and <see cref="MaxEnemiesDrawn"/> are what keep this
        /// small in practice. The panel reports when it was hit.</summary>
        private const int MaxQuadsPerRefresh = 12000;

        /// <summary>Geometry budget, so a crowded room cannot stall the frame. The selection is always
        /// drawn, so a truncated frame never hides the unit you are looking at.</summary>
        private const int MaxEnemiesDrawn = 24;

        private const int MaxStateLabels = 40;

        private const int CircleSegments = 24;
        private const int ArcSegments = 16;

        private const float LineThickness = 0.05f;
        private const float ThinLineThickness = 0.03f;
        private const float MarkerSize = 0.14f;
        private const float EyeCrossSize = 0.10f;

        /// <summary>Height of one row in the enemy list, in IMGUI pixels.</summary>
        private const float RowHeight = 15f;

        /// <summary>
        /// How many enemy rows the list will draw. A room with more enemies than this is not the case
        /// this tool is for, and the list says so rather than quietly ending — but the cap exists so a
        /// pathological room cannot push the panel off the bottom of the screen.
        /// </summary>
        private const int MaxListRows = 20;

        /// <summary>Characters of the enemy's name to keep in a row, so one long name cannot push the
        /// state and flags off the right-hand edge.</summary>
        private const int RowNameLength = 16;

        // ── Colours ──────────────────────────────────────────────────────────
        // One colour per sense, and the same colour in the panel's toggle row, so a circle on screen
        // can be traced back to the checkbox that drew it.

        private static readonly Color VisionColor = new Color(1f, 0.85f, 0.2f, 0.85f);
        private static readonly Color VisionColorDim = new Color(1f, 0.85f, 0.2f, 0.30f);
        private static readonly Color HearingColor = new Color(0.3f, 0.85f, 1f, 0.7f);
        private static readonly Color CloseColor = new Color(1f, 0.55f, 0.15f, 0.9f);
        private static readonly Color LosClearColor = new Color(0.3f, 1f, 0.4f, 0.95f);
        private static readonly Color LosBlockedColor = new Color(1f, 0.3f, 0.3f, 0.95f);
        private static readonly Color TargetColor = new Color(1f, 0.3f, 1f, 0.95f);
        private static readonly Color LastKnownColor = new Color(0.75f, 0.4f, 0.75f, 0.7f);
        private static readonly Color NoiseColor = new Color(0.3f, 1f, 1f, 0.85f);
        private static readonly Color EyeColor = new Color(1f, 1f, 1f, 1f);
        private static readonly Color SelectedColor = new Color(1f, 1f, 1f, 0.9f);

        /// <summary>
        /// The hover highlight, deliberately a colour no sense uses and drawn THICKER than the
        /// selection box. Hover and selection are two different questions ("which row am I pointing
        /// at" vs "which unit is the panel describing") and they can be on different enemies at once,
        /// so they must be distinguishable by weight as well as by hue — a viewer who cannot tell them
        /// apart will read the hover as a moving selection.
        /// </summary>
        private static readonly Color HoverColor = new Color(0.45f, 1f, 1f, 1f);

        public static EnemyAIDebugOverlay Instance { get; private set; }

        private static Sprite _whiteSprite;

        private Transform _root;
        private readonly List<SpriteRenderer> _pool = new List<SpriteRenderer>();
        private int _usedQuads;
        private bool _quadsExhausted;

        private readonly List<EnemyController> _enemies = new List<EnemyController>();
        private readonly List<EnemyController> _visible = new List<EnemyController>();

        private float _timer;
        private int _selected = EnemyAISelection.NoSelection;

        /// <summary>
        /// The row the mouse is over, or <see cref="EnemyAISelection.NoSelection"/>. Set from
        /// <c>OnGUI</c> and consumed by the world drawing, so hovering a row highlights that enemy on
        /// screen — which is the whole point of the list: a name is not enough to find a unit in a room
        /// of identical zombies.
        /// </summary>
        private int _hovered = EnemyAISelection.NoSelection;

        /// <summary>
        /// Set when the <i>world drawing</i> is out of date — a hover moved, or a click changed the
        /// selection — and acted on in <see cref="Update" />. The geometry is <c>SpriteRenderer</c> quads,
        /// which are scene objects; the GUI event loop is the wrong place to be enabling and disabling
        /// them, and one frame of latency on a highlight is imperceptible. So the GUI only marks the
        /// geometry dirty.
        /// </summary>
        private bool _geometryDirty;

        /// <summary>The list's expanded state. A "dropdown" that cannot be collapsed would make the
        /// panel permanently tall; one that starts collapsed would hide the feature.</summary>
        private bool _listExpanded = true;

        private int _enemiesDrawn;
        private bool _selectionTruncated;
        private bool _enemiesTruncated;

        private GUIStyle _panelStyle;
        private GUIStyle _labelStyle;
        private GUIStyle _buttonStyle;
        private GUIStyle _rowStyle;
        private GUIStyle _rowHotStyle;
        private GUIStyle _rowSelectedStyle;
        private GUIStyle _headerButtonStyle;

        // ── Lifecycle ────────────────────────────────────────────────────────

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void EnsureRuntimeInstance()
        {
            if (Instance == null && FindFirstObjectByType<EnemyAIDebugOverlay>() == null)
            {
                var go = new GameObject("EnemyAIDebugOverlay");
                Instance = go.AddComponent<EnemyAIDebugOverlay>();
                DontDestroyOnLoad(go);
            }
        }

        /// <summary>Get the overlay, creating it if the scene-load bootstrap did not run.</summary>
        public static EnemyAIDebugOverlay EnsureInstance()
        {
            if (Instance != null) return Instance;

            var found = FindFirstObjectByType<EnemyAIDebugOverlay>();
            if (found != null)
            {
                Instance = found;
                return found;
            }

            var go = new GameObject("EnemyAIDebugOverlay");
            Instance = go.AddComponent<EnemyAIDebugOverlay>();
            return Instance;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }

            Instance = this;
            if (Application.isPlaying) DontDestroyOnLoad(gameObject);
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            HandleHotkeys();

            if (!DebugOverlays.IsOn(DebugOverlayChannel.EnemyAI))
            {
                // The geometry must be taken down when the channel goes off, and this is the only place
                // that happens for a channel switched off from the console or the Inspector. Quads are
                // pooled objects, not IMGUI: nothing else would ever hide them.
                if (_usedQuads > 0)
                {
                    _usedQuads = 0;
                    HideAllQuads();
                }
                return;
            }

            _timer -= Time.unscaledDeltaTime;
            if (_timer <= 0f)
            {
                _timer = RefreshInterval;
                Refresh();
                return;
            }

            // A hover or a click changed since the last frame. Redraw the quads only — the enemy LIST is
            // unchanged, so re-running the scene scan here would do a FindObjectsByType per hover change,
            // which is exactly the cost the 5 Hz refresh interval exists to avoid.
            if (_geometryDirty)
            {
                _geometryDirty = false;
                RedrawGeometry();
            }
        }

        private void HandleHotkeys()
        {
            if (KeyDownThisFrame(ToggleHotkey, KeyboardKey(ToggleHotkey)))
            {
                Toggle();
            }

            if (!DebugOverlays.IsOn(DebugOverlayChannel.EnemyAI)) return;

            if (KeyDownThisFrame(NextHotkey, KeyboardKey(NextHotkey))) CycleSelection(1);
            if (KeyDownThisFrame(PrevHotkey, KeyboardKey(PrevHotkey))) CycleSelection(-1);
        }

        /// <summary>
        /// The Input System route for a hotkey. <c>Keyboard.current</c> is null when only the legacy
        /// input path is active, which is why the other presenters guard it the same way. The
        /// <c>default</c> arm is deliberate: a key this class does not declare has no route, rather
        /// than silently borrowing another key's.
        /// </summary>
        private static bool KeyboardKey(KeyCode key)
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb == null) return false;

            switch (key)
            {
                case KeyCode.F3: return kb.f3Key.wasPressedThisFrame;
                case KeyCode.RightBracket: return kb.rightBracketKey.wasPressedThisFrame;
                case KeyCode.LeftBracket: return kb.leftBracketKey.wasPressedThisFrame;
                default: return false;
            }
        }

        private static bool KeyDownThisFrame(KeyCode legacy, bool fromInputSystem)
        {
            return Input.GetKeyDown(legacy) || fromInputSystem;
        }

        private static PfeDebugSettings Settings => DebugOverlays.Settings;

        // ── Public API used by the developer console and the hotkey ──────────

        /// <summary>
        /// Turn the overlay on or off. Turning it ON with an empty filter opens the filter to
        /// <see cref="EnemyAIDebugFilter.All"/>, because a toggle that is on and draws nothing is
        /// indistinguishable from a broken toggle.
        /// </summary>
        public static void Toggle()
        {
            PfeDebugSettings settings = Settings;
            if (settings == null) return;

            bool turningOn = !settings.IsOverlayEnabled(DebugOverlayChannel.EnemyAI);
            settings.SetOverlay(DebugOverlayChannel.EnemyAI, turningOn);

            if (turningOn)
            {
                settings.EnemyAIFilter = EnemyAIDebugFilters.PermissiveIfEmpty(settings.EnemyAIFilter);
            }

            Debug.Log($"[enemyai] overlay {(turningOn ? "ON" : "OFF")} " +
                      $"— parts={EnemyAIDebugFilters.Format(settings.EnemyAIFilter)}");

            EnsureInstance().RefreshNow();
        }

        /// <summary>
        /// Set the sub-filter and enable or disable the channel to match. A <see cref="EnemyAIDebugFilter.None"/>
        /// filter turns the overlay off rather than leaving it on and empty — the same contract as
        /// <see cref="ColliderDebugOverlay.SetTileFilter"/>.
        /// </summary>
        public static void SetFilter(EnemyAIDebugFilter filter)
        {
            PfeDebugSettings settings = Settings;
            if (settings == null) return;

            settings.EnemyAIFilter = filter;
            settings.SetOverlay(DebugOverlayChannel.EnemyAI, filter != EnemyAIDebugFilter.None);

            EnsureInstance().RefreshNow();
        }

        /// <summary>Redraw immediately rather than waiting for the 0.2 s refresh tick.</summary>
        public void RefreshNow()
        {
            Refresh();
        }

        /// <summary>The index of the selected enemy, or <see cref="EnemyAISelection.NoSelection"/>.</summary>
        public static int SelectedIndex => Instance != null ? Instance._selected : EnemyAISelection.NoSelection;

        /// <summary>
        /// Step the selection. Public so the console's <c>ai next</c> and the bracket keys go through
        /// one implementation — two ways to change the selection is two places to get the wrap wrong.
        /// </summary>
        public static void CycleSelection(int direction)
        {
            EnemyAIDebugOverlay overlay = EnsureInstance();
            overlay.RefreshEnemies();
            overlay._selected = EnemyAISelection.Cycle(overlay._selected, overlay._enemies.Count, direction);
            overlay.Refresh();
        }

        /// <summary>Drop the selection.</summary>
        public static void ClearSelection()
        {
            if (Instance == null) return;
            Instance._selected = EnemyAISelection.NoSelection;
            Instance.Refresh();
        }

        /// <summary>
        /// The selected enemy's own state, as text. Read by <c>ai status</c> so the console and the panel
        /// cannot report different things about the same unit.
        /// </summary>
        public static string DescribeSelected()
        {
            EnemyAIDebugOverlay overlay = Instance;
            if (overlay == null) return "(overlay not created yet)";

            overlay.RefreshEnemies();

            if (overlay._enemies.Count == 0) return "(no enemy in the room)";

            int index = overlay._selected;
            if (index < 0 || index >= overlay._enemies.Count)
            {
                return $"(nothing selected; {overlay._enemies.Count} enemy(s) — `ai next` or the ] key)";
            }

            return Describe(overlay._enemies[index], index, overlay._enemies.Count);
        }

        // ── Selection ────────────────────────────────────────────────────────

        private void RefreshEnemies()
        {
            _enemies.Clear();
            _enemies.AddRange(FindObjectsByType<EnemyController>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None));

            // Sorted by instance id so the cycle order is STABLE across frames. An order that follows
            // the units around would make `]` land somewhere different every press, which reads as a
            // broken key rather than as a moving room.
            _enemies.Sort(CompareByInstanceId);

            if (_enemies.Count == 0)
            {
                _selected = EnemyAISelection.NoSelection;
                return;
            }

            // Re-validate: an enemy can die or leave between presses, and an out-of-range index reads
            // exactly like "nothing selected".
            _selected = EnemyAISelection.Cycle(_selected, _enemies.Count, 0);
        }

        private static int CompareByInstanceId(EnemyController a, EnemyController b)
        {
            if (a == null) return b == null ? 0 : 1;
            if (b == null) return -1;
            return a.GetInstanceID().CompareTo(b.GetInstanceID());
        }

        // ── World geometry ───────────────────────────────────────────────────

        /// <summary>
        /// Re-scan the scene and redraw. Called on the 5 Hz tick, from the hotkeys and from the console.
        /// </summary>
        private void Refresh()
        {
            // Cleared here rather than in the caller: Refresh() redraws everything, hover included, so it
            // supersedes any pending partial redraw. Every entry point that can leave the flag set --
            // the 5 Hz tick, the console, the bracket keys -- comes through here, and clearing it at the
            // one place that makes the drawing up to date is the only version that cannot be forgotten.
            _geometryDirty = false;

            PfeDebugSettings settings = Settings;
            if (settings == null || !settings.IsOverlayEnabled(DebugOverlayChannel.EnemyAI))
            {
                _usedQuads = 0;
                HideAllQuads();
                return;
            }

            if (settings.EnemyAIFilter == EnemyAIDebugFilter.None)
            {
                // On but showing nothing. Not an error the overlay can fix (the console can set this
                // deliberately), but it must be visible, so the panel says it — see DrawPanel.
                _usedQuads = 0;
                HideAllQuads();
                return;
            }

            RefreshEnemies();
            RedrawGeometry();
        }

        /// <summary>
        /// Re-emit the quads for the CURRENT <see cref="_enemies"/> list, without re-scanning the scene.
        ///
        /// <para><b>Why this is separate from <see cref="Refresh"/>.</b> Hovering a row in the list
        /// changes which enemy is highlighted, and that must feel immediate — but the only thing that
        /// changed is which box is drawn. Re-running <c>FindObjectsByType</c> on every hover change would
        /// pay the exact cost the 5 Hz refresh interval exists to avoid, for a redraw that cannot change
        /// the list.</para>
        /// </summary>
        private void RedrawGeometry()
        {
            _quadsExhausted = false;
            _enemiesDrawn = 0;
            _selectionTruncated = false;
            _enemiesTruncated = false;

            PfeDebugSettings settings = Settings;
            if (settings == null || !settings.IsOverlayEnabled(DebugOverlayChannel.EnemyAI))
            {
                _usedQuads = 0;
                HideAllQuads();
                return;
            }

            EnemyAIDebugFilter filter = settings.EnemyAIFilter;
            if (filter == EnemyAIDebugFilter.None)
            {
                _usedQuads = 0;
                HideAllQuads();
                return;
            }

            EnsureRoot();

            _usedQuads = 0;

            EnemyController selected = SelectedOrNull();
            EnemyController hovered = HoveredOrNull();

            // The selection first, so it is drawn even when the budget runs out.
            if (selected != null)
            {
                // Distinguishes "the budget ran out somewhere in the background" from "the budget ran out
                // while drawing the one unit this panel is describing". Both set _quadsExhausted, but only
                // the second means the picture in front of you is incomplete — the selection is drawn
                // first precisely so that it cannot be the casualty, and this is the check that says
                // whether that held.
                bool budgetBefore = _quadsExhausted;
                DrawEnemy(selected, filter, isSelected: true);
                _selectionTruncated = _quadsExhausted && !budgetBefore;
            }

            for (int i = 0; i < _enemies.Count; i++)
            {
                EnemyController enemy = _enemies[i];
                if (enemy == null || enemy == selected) continue;

                if (_enemiesDrawn >= MaxEnemiesDrawn)
                {
                    _enemiesTruncated = true;
                    break;
                }

                DrawEnemy(enemy, filter, isSelected: false);
            }

            // The hover highlight is emitted after the enemy loop, and NOT for z-order: every quad in
            // this overlay shares one sorting layer and order, so draw order buys no priority at all and
            // it would be wrong to claim it does. The reason is that the loop SKIPS the selected enemy,
            // so hovering the unit you already selected would otherwise draw nothing -- and that is
            // exactly when you most want to see the highlight. It is a distinct colour on a distinct box,
            // so it reads against the rest of the geometry without needing to be on top of it.
            if (hovered != null)
            {
                DrawHighlight(hovered, HoverColor, LineThickness * 1.6f);
            }

            HideUnusedQuads();
        }

        private EnemyController SelectedOrNull()
        {
            if (_selected < 0 || _selected >= _enemies.Count) return null;
            return _enemies[_selected];
        }

        private EnemyController HoveredOrNull()
        {
            if (_hovered < 0 || _hovered >= _enemies.Count) return null;
            return _enemies[_hovered];
        }

        /// <summary>
        /// A box around a unit's own collider — the engine's idea of where the unit is, so the highlight
        /// cannot drift from the thing the projectiles test against.
        /// </summary>
        private void DrawHighlight(EnemyController enemy, Color color, float thickness)
        {
            if (enemy == null || enemy.transform == null) return;

            var collider = enemy.GetComponent<Collider2D>();
            if (collider == null) return;

            Bounds bounds = collider.bounds;
            Vector2 minPx = WorldCoordinates.UnityToPixel(bounds.min);
            Vector2 maxPx = WorldCoordinates.UnityToPixel(bounds.max);
            EmitBoxPixels(minPx, maxPx, thickness, color);
        }

        /// <summary>
        /// One enemy's geometry. <paramref name="isSelected"/> switches on the full picture; the
        /// non-selected units get the vision arc and their eye point only, which is the part that
        /// answers "why did this one not see me" without turning the room into soup.
        /// </summary>
        private void DrawEnemy(EnemyController enemy, EnemyAIDebugFilter filter, bool isSelected)
        {
            EnemyBrain brain = enemy.Brain;
            if (brain == null) return;

            Vector2 eyePx = brain.EyePositionPixels;
            EnemySensors sensors = brain.Sensors;
            if (sensors == null) return;

            bool wantsVision = EnemyAIDebugFilters.Has(filter, EnemyAIDebugFilter.Vision);

            if (wantsVision)
            {
                // The cone is a half-disc: the sensor culls at VisionRangePixels and then rejects
                // anything behind the facing direction, so the shape is the arc over the facing
                // hemisphere, plus the eye point the ray is cast from.
                float fromDeg = brain.Blackboard.FacingDirection >= 0 ? -90f : 90f;
                float toDeg = fromDeg + 180f;

                EmitArcPixels(eyePx, sensors.VisionRangePixels, fromDeg, toDeg,
                    isSelected ? LineThickness : ThinLineThickness,
                    isSelected ? VisionColor : VisionColorDim,
                    ArcSegments);

                if (isSelected)
                {
                    // Close the half-disc so the range boundary reads as a shape, not as a stray curve.
                    Vector2 a = PolarPixels(eyePx, sensors.VisionRangePixels, fromDeg);
                    Vector2 b = PolarPixels(eyePx, sensors.VisionRangePixels, toDeg);
                    EmitSegmentPixels(a, b, ThinLineThickness, VisionColorDim);
                }
            }

            EmitEyeCross(eyePx, isSelected ? EyeColor : VisionColorDim);

            if (!isSelected) return;

            if (EnemyAIDebugFilters.Has(filter, EnemyAIDebugFilter.Hearing))
            {
                // The radius is a product, not a constant, so there is a circle only when something is
                // making noise. `LoudestCandidateNoise` is 0 for a stationary player, and
                // `HearingRadiusPixels(0)` is 0 — the circle vanishes, which is the picture of "it
                // cannot hear you". Deliberately not floored to a minimum: a floor would be the old
                // fixed ring wearing a new name.
                float hearingRadiusPx = sensors.HearingRadiusPixels(LoudestCandidateNoise(brain));
                if (hearingRadiusPx > 0f)
                {
                    EmitCirclePixels(eyePx, hearingRadiusPx, LineThickness, HearingColor, CircleSegments);
                }
            }

            if (EnemyAIDebugFilters.Has(filter, EnemyAIDebugFilter.CloseProximity))
            {
                EmitCirclePixels(eyePx, sensors.CloseProximityPixels, LineThickness, CloseColor,
                    CircleSegments);
            }

            EnemyBlackboard board = brain.Blackboard;
            if (board == null) return;

            Vector2? targetCentre = null;
            if (board.TargetUnit != null && board.TargetUnit.IsAlive)
            {
                targetCentre = EnemySensors.GetUnitCenterPixels(board.TargetUnit);
            }

            if (EnemyAIDebugFilters.Has(filter, EnemyAIDebugFilter.Target) && targetCentre.HasValue)
            {
                EmitMarkerPixels(targetCentre.Value, MarkerSize, TargetColor);
            }

            if (EnemyAIDebugFilters.Has(filter, EnemyAIDebugFilter.LineOfSight))
            {
                if (targetCentre.HasValue)
                {
                    // Coloured by the brain's OWN flag, not by a raycast taken here. The flag is
                    // refreshed every RaycastThrottleInterval ticks and carried in between, so a ray
                    // that turns red on a clear-looking line is reporting the throttle, not a bug —
                    // which is exactly the sort of thing that is invisible without this line.
                    EmitSegmentPixels(eyePx, targetCentre.Value, ThinLineThickness,
                        board.HasLineOfSight ? LosClearColor : LosBlockedColor);
                }
                else if (board.HasHeardNoise)
                {
                    EmitSegmentPixels(eyePx, board.LastHeardNoisePosition, ThinLineThickness, NoiseColor);
                }
            }

            // A stale last-known position exists only once the target has been lost at least once: the
            // counter is reset to 0 on every sighting and only incremented in the "nothing seen" branch.
            // Drawing it unconditionally would put a marker at the world origin for every idle enemy.
            if (EnemyAIDebugFilters.Has(filter, EnemyAIDebugFilter.Target)
                && board.TimeSinceTargetSpottedTicks > 0)
            {
                EmitMarkerPixels(board.LastKnownTargetPosition, MarkerSize * 0.7f, LastKnownColor);
            }

            // The selection itself, so it is findable in a room full of identical zombies.
            DrawHighlight(enemy, SelectedColor, LineThickness * 0.8f);
        }

        /// <summary>
        /// The loudest <see cref="UnitController.Noise"/> among this brain's live candidates — the
        /// number that sets the hearing circle's radius.
        ///
        /// <para><b>Loudest, not nearest.</b> <c>listen()</c> is evaluated per candidate and the radius
        /// is a property of the <i>source's</i> noise, so the largest circle the unit could possibly be
        /// listening at is the one for the loudest candidate. Drawing the nearest candidate's noise
        /// would understate the range the moment a distant gunshot went off — the single case the whole
        /// system exists for.</para>
        ///
        /// <para>Read from <see cref="EnemyBrain.TargetCandidates"/> rather than re-finding the player,
        /// so the drawn circle is derived from the exact list the sensor evaluated. A second search
        /// could return a different set on the frame the player died, and the picture would then be
        /// describing a unit the sensor never saw.</para>
        /// </summary>
        private static int LoudestCandidateNoise(EnemyBrain brain)
        {
            IReadOnlyList<UnitController> candidates = brain.TargetCandidates;
            int loudest = 0;

            if (candidates == null)
            {
                return loudest;
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                UnitController candidate = candidates[i];
                if (candidate == null || !candidate.IsAlive)
                {
                    continue;
                }

                if (candidate.Noise > loudest)
                {
                    loudest = candidate.Noise;
                }
            }

            return loudest;
        }

        /// <summary>
        /// The highest suspicion any live candidate has accumulated — AS3 <c>UnitPlayer.obs</c>.
        ///
        /// <para><b>This is the number that explains the reported bug, and nothing could show it
        /// before.</b> Perception no longer commits on a single tick: a sighting or a sound feeds
        /// <c>obs</c>, and the unit acts only at <c>obs &gt;= maxObs</c> (20) or on an intensity above 1.
        /// So an enemy that is <i>looking straight at the player and doing nothing</i> is not a bug — it
        /// is a meter at 7/20 — and without this figure on screen that state and a real failure are
        /// indistinguishable.</para>
        ///
        /// <para>Max across candidates, for the same reason as the noise: the meter belongs to the
        /// target, and the unit commits to whichever candidate fills it first.</para>
        /// </summary>
        private static float MaxCandidateSuspicion(EnemyBrain brain)
        {
            IReadOnlyList<UnitController> candidates = brain.TargetCandidates;
            float highest = 0f;

            if (candidates == null)
            {
                return highest;
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                UnitController candidate = candidates[i];
                if (candidate == null || !candidate.IsAlive)
                {
                    continue;
                }

                float value = candidate.Suspicion.Value;
                if (value > highest)
                {
                    highest = value;
                }
            }

            return highest;
        }

        private void EmitEyeCross(Vector2 centrePx, Color color)
        {
            EmitSegmentPixels(centrePx + new Vector2(-EyeCrossSize, 0f) * 100f,
                              centrePx + new Vector2(EyeCrossSize, 0f) * 100f,
                              ThinLineThickness, color);
            EmitSegmentPixels(centrePx + new Vector2(0f, -EyeCrossSize) * 100f,
                              centrePx + new Vector2(0f, EyeCrossSize) * 100f,
                              ThinLineThickness, color);
        }

        private void EmitMarkerPixels(Vector2 centrePx, float halfSizeWorld, Color color)
        {
            float half = halfSizeWorld * 100f;
            EmitBoxPixels(centrePx - new Vector2(half, half), centrePx + new Vector2(half, half),
                ThinLineThickness, color);
        }

        private static Vector2 PolarPixels(Vector2 centrePx, float radiusPx, float degrees)
        {
            float rad = degrees * Mathf.Deg2Rad;
            return centrePx + new Vector2(Mathf.Cos(rad), Mathf.Sin(rad)) * radiusPx;
        }

        private void EmitCirclePixels(Vector2 centrePx, float radiusPx, float thickness, Color color,
                                      int segments)
        {
            if (radiusPx <= 0f) return;

            for (int i = 0; i < segments; i++)
            {
                float a0 = 360f * i / segments;
                float a1 = 360f * (i + 1) / segments;
                EmitSegmentPixels(PolarPixels(centrePx, radiusPx, a0),
                                  PolarPixels(centrePx, radiusPx, a1), thickness, color);
            }
        }

        private void EmitArcPixels(Vector2 centrePx, float radiusPx, float fromDeg, float toDeg,
                                   float thickness, Color color, int segments)
        {
            if (radiusPx <= 0f || segments <= 0) return;

            for (int i = 0; i < segments; i++)
            {
                float a0 = Mathf.Lerp(fromDeg, toDeg, (float)i / segments);
                float a1 = Mathf.Lerp(fromDeg, toDeg, (float)(i + 1) / segments);
                EmitSegmentPixels(PolarPixels(centrePx, radiusPx, a0),
                                  PolarPixels(centrePx, radiusPx, a1), thickness, color);
            }
        }

        // ── Quad pool ────────────────────────────────────────────────────────

        private void EnsureRoot()
        {
            if (_root != null) return;

            var go = new GameObject("__EnemyAIDebugQuads");
            go.transform.SetParent(transform, false);
            _root = go.transform;
        }

        /// <summary>Draw a box given its two corners in GAME PIXELS.</summary>
        private void EmitBoxPixels(Vector2 minPx, Vector2 maxPx, float thickness, Color color)
        {
            if (maxPx.x <= minPx.x || maxPx.y <= minPx.y) return;

            var centre = new Vector2((minPx.x + maxPx.x) * 0.5f, (minPx.y + maxPx.y) * 0.5f);
            var size = new Vector2(maxPx.x - minPx.x, maxPx.y - minPx.y);

            // Corner-to-corner, using the quad-per-edge form so a degenerate box still draws a line
            // rather than nothing.
            EmitQuadPixels(new Vector2(centre.x, minPx.y), new Vector2(size.x, thickness * 100f), color);
            EmitQuadPixels(new Vector2(centre.x, maxPx.y), new Vector2(size.x, thickness * 100f), color);
            EmitQuadPixels(new Vector2(minPx.x, centre.y), new Vector2(thickness * 100f, size.y), color);
            EmitQuadPixels(new Vector2(maxPx.x, centre.y), new Vector2(thickness * 100f, size.y), color);
        }

        private void EmitQuadPixels(Vector2 centrePx, Vector2 sizePx, Color color)
        {
            SpriteRenderer sr = RentQuad();
            if (sr == null) return;

            sr.enabled = true;
            sr.color = color;

            Transform t = sr.transform;
            t.position = WorldCoordinates.PixelToUnity(centrePx);
            // Reset, not left alone: the pool is shared with EmitSegmentPixels, which rotates. A quad
            // drawn from a recycled renderer that still carries a segment's angle is silently wrong,
            // and the pool is large enough that the reuse happens on the very next refresh.
            t.rotation = Quaternion.identity;
            t.localScale = new Vector3(Mathf.Max(sizePx.x / 100f, 0.001f),
                                       Mathf.Max(sizePx.y / 100f, 0.001f), 1f);
        }

        /// <summary>Draw one straight segment given its endpoints in GAME PIXELS, as a rotated quad.</summary>
        private void EmitSegmentPixels(Vector2 fromPx, Vector2 toPx, float thickness, Color color)
        {
            Vector2 delta = toPx - fromPx;
            float length = delta.magnitude;
            if (length <= 0.01f) return;

            SpriteRenderer sr = RentQuad();
            if (sr == null) return;

            sr.enabled = true;
            sr.color = color;

            Transform t = sr.transform;
            t.position = WorldCoordinates.PixelToUnity((fromPx + toPx) * 0.5f);
            t.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
            t.localScale = new Vector3(length / 100f, Mathf.Max(thickness, 0.001f), 1f);
        }

        private SpriteRenderer RentQuad()
        {
            if (_usedQuads >= MaxQuadsPerRefresh)
            {
                _quadsExhausted = true;
                return null;
            }

            if (_usedQuads < _pool.Count)
            {
                return _pool[_usedQuads++];
            }

            var go = new GameObject("q");
            go.transform.SetParent(_root, false);

            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = WhiteSprite;
            sr.sortingLayerName = MapSortingLayers.Foreground;
            sr.sortingOrder = OverlaySortingOrder;

            _pool.Add(sr);
            _usedQuads++;
            return sr;
        }

        private void HideUnusedQuads()
        {
            for (int i = _usedQuads; i < _pool.Count; i++)
            {
                if (_pool[i] != null) _pool[i].enabled = false;
            }
        }

        private void HideAllQuads()
        {
            for (int i = 0; i < _pool.Count; i++)
            {
                if (_pool[i] != null) _pool[i].enabled = false;
            }
        }

        private static Sprite WhiteSprite
        {
            get
            {
                if (_whiteSprite != null) return _whiteSprite;

                var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false)
                {
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                    name = "__EnemyAIDebugWhite"
                };
                tex.SetPixel(0, 0, Color.white);
                tex.Apply();

                // 1 pixel at 1 pixel-per-unit is exactly one world unit at scale 1, so a quad's
                // localScale IS its world size — no conversion, and therefore no convention to get wrong.
                _whiteSprite = Sprite.Create(tex, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f), 1f);
                _whiteSprite.name = "__EnemyAIDebugWhiteSprite";
                return _whiteSprite;
            }
        }

        // ── IMGUI panel ──────────────────────────────────────────────────────

        private void OnGUI()
        {
            PfeDebugSettings settings = Settings;
            if (settings == null) return;
            if (!settings.IsOverlayEnabled(DebugOverlayChannel.EnemyAI)) return;

            EnsureStyles();
            SelectVisible();

            DrawPanel(settings);
            DrawStateLabels(settings);
        }

        private void EnsureStyles()
        {
            if (_panelStyle != null) return;

            _panelStyle = MakeStyle(12);
            _panelStyle.alignment = TextAnchor.UpperLeft;

            _labelStyle = MakeStyle(11);
            _labelStyle.alignment = TextAnchor.UpperCenter;

            _buttonStyle = MakeStyle(11);
            _buttonStyle.alignment = TextAnchor.MiddleCenter;

            _headerButtonStyle = MakeStyle(11);
            _headerButtonStyle.alignment = TextAnchor.MiddleLeft;

            // Three row styles, distinguished by TEXT COLOUR only.
            //
            // Be accurate about what these are: MakeStyle builds from `GUI.skin.label`, which carries no
            // `normal.background`, so a row drawn with one of these has NO button chrome at all -- it is
            // plain text that happens to accept clicks. The affordance is therefore the text itself: the
            // `[x]` / `[ ]` brackets on the toggles, and the `>` marker plus the cyan colour on a row.
            _rowStyle = MakeStyle(11);
            _rowStyle.alignment = TextAnchor.MiddleLeft;

            _rowHotStyle = MakeStyle(11);
            _rowHotStyle.alignment = TextAnchor.MiddleLeft;
            SetTextColour(_rowHotStyle, HoverColor);

            _rowSelectedStyle = MakeStyle(11);
            _rowSelectedStyle.alignment = TextAnchor.MiddleLeft;
            _rowSelectedStyle.fontStyle = FontStyle.Bold;
        }

        /// <summary>Force a style's text colour in all four states — a hover colour that only applies
        /// while hovering is a colour that vanishes exactly when it is being read.</summary>
        private static void SetTextColour(GUIStyle style, Color colour)
        {
            style.normal.textColor = colour;
            style.hover.textColor = colour;
            style.active.textColor = colour;
            style.focused.textColor = colour;
        }

        private static GUIStyle MakeStyle(int size)
        {
            var style = new GUIStyle(GUI.skin.label)
            {
                fontSize = size,
                richText = true,
                wordWrap = false
            };

            // Forced white: the runtime IMGUI skin's default label colour is dark, and a dark label on
            // a dark game is unreadable in the screenshot the overlay exists to produce.
            style.normal.textColor = Color.white;
            style.hover.textColor = Color.white;
            style.active.textColor = Color.white;

            return style;
        }

        /// <summary>
        /// The camera-view filter, same shape as <see cref="UnitHealthOverlay"/>'s: the gameplay camera
        /// is perspective, so this projects the viewport corners rather than branching on
        /// <c>orthographic</c>.
        /// </summary>
        private void SelectVisible()
        {
            _visible.Clear();

            Camera cam = Camera.main;
            if (cam == null)
            {
                _visible.AddRange(_enemies);
                return;
            }

            float plane = Mathf.Max(Mathf.Abs(cam.transform.position.z), 0.01f);

            if (cam.transform.forward.z < 0.9f)
            {
                _visible.AddRange(_enemies);
                return;
            }

            Vector3 cornerA = cam.ViewportToWorldPoint(new Vector3(0f, 0f, plane));
            Vector3 cornerB = cam.ViewportToWorldPoint(new Vector3(1f, 1f, plane));

            const float padding = 3f;
            var view = new Rect(
                Mathf.Min(cornerA.x, cornerB.x) - padding,
                Mathf.Min(cornerA.y, cornerB.y) - padding,
                Mathf.Abs(cornerB.x - cornerA.x) + padding * 2f,
                Mathf.Abs(cornerB.y - cornerA.y) + padding * 2f);

            for (int i = 0; i < _enemies.Count; i++)
            {
                EnemyController enemy = _enemies[i];
                if (enemy == null) continue;

                Vector3 p = enemy.transform.position;
                if (view.Contains(new Vector2(p.x, p.y))) _visible.Add(enemy);
            }
        }

        private void DrawPanel(PfeDebugSettings settings)
        {
            EnemyAIDebugFilter filter = settings.EnemyAIFilter;
            EnemyController selected = SelectedOrNull();

            const float width = 470f;
            const float margin = 8f;
            float x = margin;

            // Measure first so the plate can be drawn behind the text: IMGUI draws in call order, and a
            // plate drawn after its own text hides it.
            string body = BuildPanelText(filter, selected);
            int bodyLines = CountLines(body);

            // How many rows actually fit. The hard cap (MaxListRows) is a sanity bound, but the screen is
            // the real one: a list that pushes the body off the bottom has hidden the thing this panel
            // exists for, and it would look exactly like "the verdict block is missing". So the rows are
            // clamped to what is left after the chrome and the ALREADY MEASURED body, and the header says
            // so out loud when it clamps.
            //
            // `chrome` is the fixed part of the layout above, summed from the cursor arithmetic rather
            // than guessed: margin 8 + header band 24 + toggle band 22 + list-header band 18 + 12 slack.
            const float chrome = 84f;
            int rowBudget = Mathf.Max(4, (int)((Screen.height - chrome - bodyLines * 14f) / RowHeight));
            int listRows = _listExpanded
                ? Mathf.Min(Mathf.Min(_enemies.Count, MaxListRows), rowBudget)
                : 0;
            // Guarded on `_listExpanded`: collapsed, listRows is 0 for any non-empty room, and an
            // unguarded comparison would call that "truncated".
            bool listTruncated = _listExpanded && listRows < _enemies.Count;
            float listHeight = listRows > 0 ? listRows * RowHeight + 2f : 0f;

            // One cursor per band, all measured before anything is drawn.
            float y = margin;
            float headerY = y + 4f;
            float toggleY = y + 24f;
            float listHeaderY = toggleY + 22f;
            float listY = listHeaderY + 18f;
            float bodyY = listY + listHeight + 2f;

            float height = (bodyY - y) + bodyLines * 14f + 8f;

            Color previous = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.72f);
            GUI.DrawTexture(new Rect(x, y, width, height), Texture2D.whiteTexture);
            GUI.color = previous;

            GUI.Label(new Rect(x + 6f, headerY, width - 12f, 16f),
                $"<b>[ai]</b> F3  parts={EnemyAIDebugFilters.Format(filter)}", _panelStyle);

            DrawToggles(settings, filter, x + 6f, toggleY, width - 12f);

            DrawEnemyList(x + 6f, listHeaderY, listY, width - 12f, listRows, listTruncated);

            GUI.Label(new Rect(x + 6f, bodyY, width - 12f, bodyLines * 14f + 4f),
                body, _panelStyle);
        }

        /// <summary>
        /// The enemy list: a collapsible header plus one clickable row per enemy, where <b>hovering a row
        /// highlights that enemy in the world</b>.
        ///
        /// <para><b>Why a list and not a click in the world.</b> Left-click is aim/fire and right-click is
        /// interact (<c>PlayerController.cs:560</c>), so a world click would select a unit <i>and</i> fire
        /// at it — and the overlay cannot suppress the game's own read of the button, because IMGUI runs
        /// after <c>Update</c>. A list has no input conflict at all, and it can point at an enemy that is
        /// off-screen, which a world click cannot.</para>
        ///
        /// <para><b>Hover is a different question from selection.</b> Hovering answers "which row is
        /// this?"; clicking answers "which unit is the panel describing?". They can be on two different
        /// enemies at once, so the world draws them in two different colours and the row carries both
        /// markers (<c>&gt;</c> hovered, <c>*</c> selected).</para>
        ///
        /// <para><paramref name="truncated"/> is passed in rather than stored, because it is derived from
        /// the row budget that <see cref="DrawPanel"/> just computed — a field would be a second copy of
        /// the same fact, free to disagree with the list it is describing.</para>
        /// </summary>
        private void DrawEnemyList(float x, float headerY, float listY, float width, int listRows,
                                   bool truncated)
        {
            // The header is the only place a truncated list can say so. A list that silently showed 20 of
            // 34 enemies would read as "there are 20 enemies", which is a wrong answer, not a missing one.
            string caption = _listExpanded
                ? $"[-] enemies ({_enemies.Count}{(truncated ? $", first {listRows}" : "")})" +
                  "   click = select, hover = highlight"
                : $"[+] enemies ({_enemies.Count})   click to expand";

            if (GUI.Button(new Rect(x, headerY, width, 16f), caption, _headerButtonStyle))
            {
                _listExpanded = !_listExpanded;
                // Collapsing must clear the hover: the highlighted enemy would otherwise stay highlighted
                // with no row on screen to explain why, which reads as a stray selection box.
                if (!_listExpanded) SetHovered(EnemyAISelection.NoSelection);

                // One frame of layout lag, and it is unavoidable in IMGUI: the plate was already sized
                // from the expanded row count, so this frame leaves a blank band where the rows were.
                // Measuring after the click would mean drawing the plate after the button that sits on
                // top of it. A single frame at 5 Hz panel cadence is not perceptible; a reader who
                // notices the gap should know it is this and not a missing list.
            }

            if (!_listExpanded || listRows == 0) return;

            // Collected, then applied AFTER the loop. Nothing that the rows are drawn from is mutated
            // inside the loop that draws them — lesson #78's shape, which in its index-iterated form
            // silently skips a row rather than throwing.
            int clickedRow = EnemyAISelection.NoSelection;
            int hoveredRow = EnemyAISelection.NoSelection;

            for (int i = 0; i < listRows; i++)
            {
                var rect = new Rect(x, listY + i * RowHeight, width, RowHeight);

                bool isHovered = rect.Contains(Event.current.mousePosition);
                if (isHovered) hoveredRow = i;

                bool isSelected = i == _selected;

                // Hover wins the style, because it is the transient one: a hovered row that keeps the
                // selected style looks unresponsive exactly while the mouse is on it. The `*` marker
                // still says "selected".
                GUIStyle style = isHovered ? _rowHotStyle
                    : isSelected ? _rowSelectedStyle
                    : _rowStyle;

                if (GUI.Button(rect, RowText(i, isHovered, isSelected), style)) clickedRow = i;
            }

            SetHovered(hoveredRow);
            if (clickedRow != EnemyAISelection.NoSelection) Select(clickedRow);
        }

        /// <summary>One row: index, name, state, hp, the blackboard flags, and whether it is on screen.</summary>
        private string RowText(int index, bool isHovered, bool isSelected)
        {
            var sb = new StringBuilder();

            sb.Append(isHovered ? '>' : ' ');
            sb.Append(isSelected ? '*' : ' ');
            sb.Append(index + 1).Append(". ");

            EnemyController enemy = _enemies[index];
            if (enemy == null)
            {
                sb.Append("(destroyed)");
                return sb.ToString();
            }

            sb.Append(Shorten(enemy.name, RowNameLength));

            EnemyBrain brain = enemy.Brain;
            if (brain == null)
            {
                sb.Append("   (no EnemyBrain)");
                return sb.ToString();
            }

            sb.Append("  ").Append(brain.CurrentState);
            sb.Append($"  hp {Mathf.CeilToInt(enemy.CurrentHealth)}/{Mathf.CeilToInt(enemy.MaxHealth)}");

            EnemyBlackboard board = brain.Blackboard;
            if (board != null)
            {
                // Single letters, spelled out in the panel footer. A row has to stay narrow enough to
                // read twenty of them at once, and the flags are the only part that differs between two
                // otherwise identical zombies.
                if (board.TargetUnit != null) sb.Append(" T");
                if (board.HasLineOfSight) sb.Append(" L");
                if (board.HasHeardNoise) sb.Append(" N");
                if (board.ShockTimerTicks > 0) sb.Append($" shok{board.ShockTimerTicks}");
            }

            // Named rather than implied: an off-screen enemy's row is the ONLY way to know it is there,
            // so a row with no highlight on screen must say why.
            if (!_visible.Contains(enemy)) sb.Append("  (off screen)");

            return sb.ToString();
        }

        /// <summary>Truncate a long name so it cannot push the state and flags off the row.</summary>
        private static string Shorten(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            if (text.Length <= max) return text;
            return text.Substring(0, max - 1) + "~";
        }

        private void SetHovered(int index)
        {
            if (_hovered == index) return;
            _hovered = index;
            _geometryDirty = true;
        }

        private void Select(int index)
        {
            if (_selected == index) return;
            _selected = index;
            _geometryDirty = true;
        }

        /// <summary>
        /// The six toggle buttons. Clicking one flips exactly that bit — the same independence contract
        /// as the console: turning <c>hearing</c> off must not disturb <c>vision</c>.
        ///
        /// <para>The clicks are collected and applied AFTER the row is drawn. Mutating a collection
        /// inside the loop that is drawing its rows is the mistake recorded as lesson #78 — the button
        /// is clicked while its own row is being rendered, so anything the click changes is read by the
        /// same loop that is already part-way through it.</para>
        /// </summary>
        private void DrawToggles(PfeDebugSettings settings, EnemyAIDebugFilter filter, float x,
                                 float y, float width)
        {
            EnemyAIDebugFilter[] flags =
            {
                EnemyAIDebugFilter.Vision,
                EnemyAIDebugFilter.Hearing,
                EnemyAIDebugFilter.CloseProximity,
                EnemyAIDebugFilter.LineOfSight,
                EnemyAIDebugFilter.Target,
                EnemyAIDebugFilter.States
            };

            string[] names = { "vision", "hearing", "close", "los", "target", "states" };
            Color[] colours = { VisionColor, HearingColor, CloseColor, LosClearColor, TargetColor,
                                EyeColor };

            float gap = 4f;
            float buttonWidth = (width - gap * (flags.Length - 1)) / flags.Length;

            EnemyAIDebugFilter clicked = EnemyAIDebugFilter.None;

            Color previous = GUI.color;

            for (int i = 0; i < flags.Length; i++)
            {
                bool on = EnemyAIDebugFilters.Has(filter, flags[i]);

                GUI.color = on ? colours[i] : new Color(0.4f, 0.4f, 0.45f, 0.9f);

                var rect = new Rect(x + i * (buttonWidth + gap), y, buttonWidth, 18f);
                if (GUI.Button(rect, (on ? "[x] " : "[ ] ") + names[i], _buttonStyle))
                {
                    clicked |= flags[i];
                }
            }

            GUI.color = previous;

            if (clicked == EnemyAIDebugFilter.None) return;

            // XOR, so one click flips the bit whether it was on or off. Applied after the row, and
            // through SetFilter so the channel is switched off when the last bit is cleared — an
            // overlay that is on with an empty filter draws nothing and reads as broken.
            EnemyAIDebugFilter updated = filter ^ clicked;
            SetFilter(updated);

            Debug.Log($"[enemyai] parts={EnemyAIDebugFilters.Format(updated)}");
        }

        private string BuildPanelText(EnemyAIDebugFilter filter, EnemyController selected)
        {
            var sb = new StringBuilder();

            sb.Append($"{_enemies.Count} enemy(s) in room, {_enemiesDrawn} drawn, {_visible.Count} in view");

            if (_enemiesTruncated)
                sb.Append($"  <color=#ff8040>GEOMETRY TRUNCATED at {MaxEnemiesDrawn}</color>");

            // `_selectionTruncated` implies `_quadsExhausted`, so the specific message replaces the
            // generic one rather than joining it: repeating "budget hit" beside "the selection is
            // incomplete" adds nothing, and the reader needs the more serious fact first.
            if (_selectionTruncated)
                sb.Append("  <color=#ff4040>SELECTION GEOMETRY TRUNCATED - the picture is incomplete</color>");
            else if (_quadsExhausted)
                sb.Append($"  <color=#ff4040>QUAD BUDGET HIT ({MaxQuadsPerRefresh})</color>");

            if (filter == EnemyAIDebugFilter.None)
                sb.Append("  <color=#ff4040>filter is off — nothing is drawn</color>");

            sb.Append('\n');

            if (_enemies.Count == 0)
            {
                // Three short lines, not two long ones: the single-line version measured 73 characters,
                // ~475 px against a 458 px body, and it carries an em-dash on top of that. It would have
                // been clipped with no ellipsis, so the sentence would just stop mid-word.
                sb.Append("no enemy in the room. (An enemy is a <b>EnemyController</b> -- a\n");
                sb.Append("zombie spawned from the room's placement data.\n");
                sb.Append("F2 -> tab 8 -> Spawn to make one.)");
                return sb.ToString();
            }

            // The list is the primary instrument now, so it is named first; the keys are kept because they
            // still work and because they are the only way to step the selection from the keyboard.
            // Split over two lines rather than one: the whole sentence is ~106 characters, which does not
            // fit the 458 px body and would be clipped mid-word with no ellipsis to show it happened.
            sb.Append("select: click a row, or <b>[</b> / <b>]</b> to cycle  (");
            sb.Append(_selected == EnemyAISelection.NoSelection
                ? "nothing selected"
                : $"{_selected + 1} of {_enemies.Count}");
            sb.Append(")\n");
            sb.Append("        full geometry is drawn for the selection only\n");

            // The legend the row builder promises ("single letters, spelled out in the panel footer").
            // A row has to stay narrow enough to read twenty at once, so the letters are terse and only
            // useful if something spells them out -- and this is the only place that can. Only shown when
            // the list is open, because that is the only time the markers can appear on screen.
            //
            // Wrapped by hand, in two lines of <= 55 characters. The body label is 458 px wide at font
            // size 12 and the style is wordWrap = false, so a single long line is not wrapped -- it is
            // CLIPPED, and the tail of it silently disappears. 55 chars is ~360 px at ~6.5 px/char, which
            // leaves headroom for the proportional font's wide characters.
            if (_listExpanded)
            {
                sb.Append("<i>flags:</i> T target   L line of sight   N heard noise\n");
                // A bare `>` on purpose: Unity's legacy IMGUI rich text understands <b>/<i>/<color>/<size>
                // and nothing else -- it does NOT decode HTML entities, so `&gt;` would print literally.
                // Only `<` is special to the parser, so the bare character is safe.
                sb.Append("       shokNN stagger ticks   <i>markers:</i> > hovered   * selected\n");
            }

            if (selected == null)
            {
                sb.Append("\n(select a unit to see its state, blackboard and a sense verdict.)");
                return sb.ToString();
            }

            AppendSelectedBlock(sb, selected);
            return sb.ToString();
        }

        private void AppendSelectedBlock(StringBuilder sb, EnemyController selected)
        {
            EnemyBrain brain = selected.Brain;
            if (brain == null)
            {
                sb.Append("\nselected unit has NO EnemyBrain component");
                return;
            }

            EnemyBlackboard board = brain.Blackboard;
            EnemySensors sensors = brain.Sensors;

            bool inView = _visible.Contains(selected);

            sb.Append("\n<b>selected</b> ").Append(selected.name);
            if (!inView) sb.Append("  <color=#ffd24d>(off screen)</color>");

            sb.Append("\n  state       ").Append(EnemyAISelection.DescribeState(brain.CurrentState));
            sb.Append($"   facing {board.FacingDirection:+#;-#;0}  grounded={board.IsGrounded}");
            sb.Append($"\n  hp          {Mathf.CeilToInt(selected.CurrentHealth)}/{Mathf.CeilToInt(selected.MaxHealth)}");
            sb.Append(selected.IsAlive ? "" : "  <color=#ff4040>DEAD</color>");

            // The four timers, together and labelled with their AS3 names, because the shok/aiSpok
            // confusion this project already paid for was invisible precisely because nothing printed
            // them side by side.
            sb.Append("\n  timers      ");
            sb.Append($"aiSpok={board.AlertTimerTicks}  aiTCh={board.StateTimerTicks}  ");
            sb.Append($"shok={board.ShockTimerTicks}  atkCd={board.AttackCooldownTicks}");

            sb.Append("\n  eye (px)    ").Append(Fmt(brain.EyePositionPixels));

            int loudestNoise = LoudestCandidateNoise(brain);
            float hearingRadiusPx = sensors.HearingRadiusPixels(loudestNoise);

            sb.Append("\n  ranges      ");
            sb.Append($"vision={sensors.VisionRangePixels:0}  close={sensors.CloseProximityPixels:0}");
            sb.Append($"  rayEvery={sensors.RaycastThrottleInterval}t");

            // The hearing row is its own line because it is now three numbers that mean something
            // together: the listener's `ear`, the radius that product produces, and the source noise it
            // was computed from. A radius of 0 here is not a failure — it is "nothing is being loud".
            sb.Append("\n  hearing     ");
            sb.Append($"ear={sensors.Ear:0.##}x{sensors.EarMultiplier:0.##}");
            sb.Append($"  r={hearingRadiusPx:0}px @ noise {loudestNoise}");

            sb.Append("\n  target      ");
            if (board.TargetUnit == null)
            {
                sb.Append("(none)");
            }
            else
            {
                sb.Append(board.TargetUnit.name);
                sb.Append($"  dist={board.TargetDistance:0}px  d=({board.TargetDeltaX:0},{board.TargetDeltaY:0})");
            }

            sb.Append("\n  LOS         ").Append(board.HasLineOfSight
                ? "<color=#4dff66>clear</color>"
                : "<color=#ff4d4d>blocked</color>");
            sb.Append($"   sinceSpot={board.TimeSinceTargetSpottedTicks}t");

            sb.Append("\n  noise       ");
            if (board.HasHeardNoise)
                sb.Append($"<color=#4dffff>heard</color> at {Fmt(board.LastHeardNoisePosition)}");
            else
                sb.Append("nothing heard");
            sb.Append($"   intensity {board.HeardNoiseIntensity:0.##}/4");

            // The gate, printed next to the senses that feed it. `obs` is the number the unit actually
            // acts on, so "looking straight at the player and still Idle" reads as `obs 7/20` rather
            // than as a bug — which is the single most likely misreading of the new behaviour.
            //
            // The denominator is `NoiseMath.DefaultMaxObservation`, which is what
            // `UnitController.MaxSuspicion` returns. Read from the constant rather than from a candidate
            // so the line still prints when there is no candidate — and NOT from a literal, because a
            // hardcoded 20 here would keep saying 20 on the day the meter changes.
            float suspicion = MaxCandidateSuspicion(brain);
            sb.Append($"\n  suspicion   obs={suspicion:0.##}/{NoiseMath.DefaultMaxObservation:0}");
            sb.Append(suspicion >= NoiseMath.DefaultMaxObservation
                ? "  <color=#ff4040>COMMITTED</color>"
                : "  (below the gate)");

            if (board.TimeSinceTargetSpottedTicks > 0)
                sb.Append($"\n  last known  {Fmt(board.LastKnownTargetPosition)}");

            AppendVerdict(sb, brain, board, sensors);
        }

        /// <summary>
        /// The verdict block. Every line here is the shipped sensor code being CALLED with the live
        /// values, not a second copy of the rules — a re-implementation would be a thing that can
        /// disagree with the sense it is supposed to be reporting on.
        /// </summary>
        private static void AppendVerdict(StringBuilder sb, EnemyBrain brain, EnemyBlackboard board,
                                          EnemySensors sensors)
        {
            if (board.TargetUnit == null || !board.TargetUnit.IsAlive)
            {
                sb.Append("\n\n<b>verdict</b>  no live target, so no sense can fire");
                return;
            }

            Vector2 eyePx = brain.EyePositionPixels;
            Vector2 targetCentrePx = EnemySensors.GetUnitCenterPixels(board.TargetUnit);

            bool inCone = sensors.CheckVisionAngle(eyePx, board.FacingDirection, targetCentrePx);

            // The TARGET's own noise decides audibility, and it is 0 for a stationary player — the
            // exact case that produced the reported bug. `HearIntensity` returns 0 both when the source
            // is silent and when it is merely out of range, and those two look identical from a bool, so
            // the lines below print the noise, the radius it buys and the intensity: a silent player
            // reads "r=0px", a distant one reads "r=400px but intensity 0".
            int targetNoise = board.TargetUnit.Noise;
            float hearIntensity = sensors.HearIntensity(eyePx, targetCentrePx, targetNoise);
            bool audible = hearIntensity > 0f;
            bool commitsNow = NoiseMath.CommitsImmediately(hearIntensity);
            float hearRadiusPx = sensors.HearingRadiusPixels(targetNoise);

            sb.Append("\n\n<b>verdict</b>  <i>the shipped sensor methods, called with the values above</i>");

            sb.Append("\n  vision cone  ").Append(YesNo(inCone));
            sb.Append($"   (<= {sensors.VisionRangePixels:0}px AND on the facing side, or inside close)");

            sb.Append("\n  hearing      ").Append(YesNo(audible));
            sb.Append($"   (noise {targetNoise} → r={hearRadiusPx:0}px, facing ignored)");

            if (audible)
            {
                sb.Append($"\n               intensity {hearIntensity:0.##}/4");
                sb.Append(commitsNow
                    ? " — <color=#ffd24d>over 1, commits at once</color>"
                    : " — under 1, only accumulates");
            }

            sb.Append("\n  LOS          ").Append(board.HasLineOfSight
                ? "<color=#4dff66>clear</color>"
                : "<color=#ff4d4d>blocked</color>");
            sb.Append("   (the brain's own flag, refreshed every ");
            sb.Append(sensors.RaycastThrottleInterval).Append(" ticks)");

            sb.Append("\n  the brain is ").Append(board.TargetUnit != null
                ? "<b>chasing</b>"
                : "<b>not chasing</b>");
            sb.Append(" while ");

            bool anySense = inCone || audible;
            sb.Append(anySense ? "at least one sense fires" : "NO sense fires");

            if (!anySense)
            {
                // The line worth having. A unit that is chasing something none of its senses can
                // currently reach is either inside the raycast throttle (the carried-over LOS) or is a
                // bug — and those two are indistinguishable without this comparison.
                sb.Append("\n  <color=#ffd24d>!! chasing with no sense firing — throttled LOS, or a bug</color>");
            }
        }

        private static string YesNo(bool value)
        {
            return value ? "<color=#4dff66>YES</color>" : "<color=#ff4d4d>no</color>";
        }

        private static string Fmt(Vector2 v)
        {
            return $"({v.x:0},{v.y:0})";
        }

        private static int CountLines(string text)
        {
            int count = 1;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n') count++;
            }
            return count;
        }

        /// <summary>
        /// The per-enemy state label, drawn above every enemy in view — so a room full of enemies can be
        /// read from one screenshot without selecting each in turn.
        /// </summary>
        private void DrawStateLabels(PfeDebugSettings settings)
        {
            if (!EnemyAIDebugFilters.Has(settings.EnemyAIFilter, EnemyAIDebugFilter.States)) return;

            Camera cam = Camera.main;
            if (cam == null) return;

            int drawn = 0;

            for (int i = 0; i < _visible.Count; i++)
            {
                if (drawn >= MaxStateLabels) break;

                EnemyController enemy = _visible[i];
                if (enemy == null) continue;

                EnemyBrain brain = enemy.Brain;
                if (brain == null) continue;

                if (!TryAnchor(enemy, out Vector3 anchor)) continue;

                Vector3 screen = cam.WorldToScreenPoint(anchor);
                if (screen.z <= 0f) continue;

                // IMGUI's y runs down from the top; WorldToScreenPoint's runs up from the bottom.
                float x = screen.x;
                float y = Screen.height - screen.y;

                bool isSelected = enemy == SelectedOrNull();
                bool isHovered = enemy == HoveredOrNull();

                string text = $"{Label(brain)}  {brain.CurrentState}";

                // Selection wins the marker when both apply, matching the row (`*` beats `>` there).
                // The hover still shows in the world as the cyan box, so nothing is lost by the label
                // carrying only one of them -- and a label with two markers would be unreadable at 11 px.
                if (isSelected) text = "<b>> " + text + "</b>";
                else if (isHovered) text = "<b>" + text + "</b>";

                GUI.Label(new Rect(x - 90f, y, 180f, 15f), text, _labelStyle);
                drawn++;
            }
        }

        /// <summary>
        /// A short per-enemy tag. Kept to the two fields that differ between otherwise identical
        /// zombies, so the label stays readable when twenty of them are on screen at once.
        /// </summary>
        private static string Label(EnemyBrain brain)
        {
            EnemyBlackboard board = brain.Blackboard;
            if (board == null) return "#?";

            var sb = new StringBuilder();
            sb.Append(brain.gameObject.GetInstanceID() % 1000);
            sb.Append(board.FacingDirection >= 0 ? " >" : " <");
            if (board.TargetUnit != null) sb.Append(" T");
            if (board.HasLineOfSight) sb.Append(" L");
            if (board.HasHeardNoise) sb.Append(" N");
            if (board.ShockTimerTicks > 0) sb.Append($" shok{board.ShockTimerTicks}");
            return sb.ToString();
        }

        private static bool TryAnchor(EnemyController enemy, out Vector3 anchor)
        {
            var collider = enemy.GetComponent<Collider2D>();

            if (collider != null)
            {
                Bounds bounds = collider.bounds;
                anchor = new Vector3(bounds.center.x, bounds.max.y + 0.12f, 0f);
                return true;
            }

            anchor = enemy.transform.position + Vector3.up * 0.6f;
            return true;
        }

        /// <summary>
        /// The selected enemy as text, for <c>ai status</c>. Deliberately the same builder the panel
        /// uses, so the two cannot report different things about the same unit.
        /// </summary>
        internal static string Describe(EnemyController enemy, int index, int count)
        {
            if (enemy == null) return "(no enemy)";

            EnemyBrain brain = enemy.Brain;
            if (brain == null) return $"[{index + 1}/{count}] {enemy.name}: no EnemyBrain";

            EnemyBlackboard board = brain.Blackboard;
            EnemySensors sensors = brain.Sensors;

            var sb = new StringBuilder();
            sb.Append($"[{index + 1}/{count}] {enemy.name}");
            sb.Append($"  {EnemyAISelection.DescribeState(brain.CurrentState)}");
            sb.Append($"  hp {Mathf.CeilToInt(enemy.CurrentHealth)}/{Mathf.CeilToInt(enemy.MaxHealth)}");
            sb.Append($"  facing {board.FacingDirection}");
            sb.Append($"\n  timers aiSpok={board.AlertTimerTicks} aiTCh={board.StateTimerTicks}");
            sb.Append($" shok={board.ShockTimerTicks} atkCd={board.AttackCooldownTicks}");
            int loudestNoise = LoudestCandidateNoise(brain);

            sb.Append($"\n  eye={Fmt(brain.EyePositionPixels)}");
            sb.Append($" vision={sensors.VisionRangePixels:0}");
            sb.Append($" ear={sensors.Ear:0.##}x{sensors.EarMultiplier:0.##}");
            sb.Append($" hearing={sensors.HearingRadiusPixels(loudestNoise):0}px@n{loudestNoise}");
            sb.Append($" close={sensors.CloseProximityPixels:0}");

            if (board.TargetUnit != null)
            {
                sb.Append($"\n  target={board.TargetUnit.name} dist={board.TargetDistance:0}px");
            }
            else
            {
                sb.Append("\n  target=(none)");
            }

            sb.Append($"  LOS={(board.HasLineOfSight ? "clear" : "blocked")}");
            sb.Append($"  noise={(board.HasHeardNoise ? "heard" : "none")}");
            return sb.ToString();
        }
    }
}
