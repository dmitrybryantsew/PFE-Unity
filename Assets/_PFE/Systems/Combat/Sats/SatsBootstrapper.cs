using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using VContainer;
using PFE.Core;
using PFE.Core.Input;
using PFE.Data.Definitions;
using PFE.Entities.Player;
using PFE.Entities.Units;
using PFE.Sim.Combat.Sats;
using PFE.Systems.Combat.Sats;
using PFE.Systems.Map;
using PFE.Systems.Map.Rendering;
using PFE.Systems.Map.TileQuery;
using PFE.Systems.RPG;
using PFE.Systems.Weapons;
using PFE.UI.Combat;
using PFE.UI.Common;
using PFE.UI.HUD;
using PFE.UI.PipBuck;

namespace PFE.Systems.Combat.Sats
{
    /// <summary>
    /// Creates and drives S.A.T.S.: its own overlay canvas, its input, and the scene lookups the
    /// controller needs.
    ///
    /// <para><b>Why a bootstrapper at all.</b> <see cref="SatsController"/> is a plain container-scoped
    /// class with no lifecycle and no scene access — the same shape as <c>PipBuckManager</c>, and for
    /// the same reason: the state machine must be constructible in a fixture, and a
    /// <c>MonoBehaviour</c> is not. Everything that touches the engine lives here, and this class is
    /// created at runtime by <c>GameLifetimeScope</c> rather than authored into a scene, because the
    /// player, the room and the HUD are all created at runtime too.</para>
    ///
    /// <para><b>Why its own canvas.</b> The overlay must draw above the HUD's normal furniture and be
    /// able to cover the whole screen with the tint, and the PipBuck already established the pattern
    /// of a second runtime-built canvas for exactly this. The AP bar rides on it as well: it is only
    /// ever visible because <c>setOd</c> was called, and <c>setOd</c> is only ever called from
    /// S.A.T.S., so putting it on this canvas rather than on the HUD's is observationally equivalent
    /// and avoids making <c>HudBootstrapper</c> depend on this controller.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SatsBootstrapper : MonoBehaviour
    {
        private const int CanvasSortingOrder = 220;   // above the HUD (200) and the PipBuck (210)
        private const int InfoMessageTicks = 90;

        private SimLoop _loop;

        private SatsController _sats;
        private SatsView _view;
        private ActionPointsBarView _apBar;

        private Canvas _canvas;
        private Text _topStrip;
        private Text _message;

        private PlayerController _player;
        private PlayerWeaponLoadout _loadout;
        private CharacterStats _stats;
        private RoomVisualController _roomVisual;
        private ITileQueryService _tileQuery;
        private RoomInstance _tileQueryRoom;

        private PipBuckCompositionDefinition _composition;
        private Font _font;
        private int _messageTicksLeft;

        private LineRenderer _tracer;
        private readonly System.Collections.Generic.List<SatsTracePoint> _tracePoints =
            new System.Collections.Generic.List<SatsTracePoint>(SatsTrajectory.Steps + 1);

        /// <summary>The controller this bootstrapper drives. For the PipBuck's hotkey gate.</summary>
        public SatsController Controller => _sats;

        /// <summary>
        /// <b>The controller is injected, never constructed here.</b> <c>GameLifetimeScope</c>
        /// registers it as a container singleton because <c>PlayerWeaponLoadout</c>,
        /// <c>PlayerController</c> and <c>PipBuckBootstrapper</c> all take the same concrete type.
        ///
        /// <para>Building a private instance here compiled cleanly and looked right in the editor —
        /// the overlay opened, cells queued, the pause held — but the queue then lived on an object
        /// nobody else could see. Accepting released the pause correctly and
        /// <c>PlayerWeaponLoadout.DriveSatsQueue()</c> asked <i>its</i> (idle) singleton for a front
        /// cell, got <c>false</c>, and no shot was ever fired. The two instances were both real; the
        /// bug was their identity, which no arithmetic fixture and no per-class lint can see.</para>
        /// </summary>
        [Inject]
        public void Construct(SimLoop loop, SatsController sats)
        {
            _loop = loop;
            _sats = sats;
        }

        private void Start()
        {
            _sats.Feedback = new FeedbackSink(this);
            _sats.World = new WorldQuery(this);

            BuildCanvas();

            _loop?.Register(_sats);
        }

        private void OnDestroy()
        {
            _loop?.Unregister(_sats);
            _sats?.Exit();
        }

        // ── Per-frame ────────────────────────────────────────────────────────────────────

        private void Update()
        {
            if (_sats == null) return;

            ResolveScene();

            _sats.Player = _player;   // PlayerController IS a UnitController
            _sats.Loadout = _loadout;
            _sats.Stats = _stats;

            if (_player != null && _stats == null) _stats = _player.GetComponentInParent<CharacterStats>();

            PollInput();

            if (_messageTicksLeft > 0 && --_messageTicksLeft == 0 && _message != null)
            {
                _message.text = string.Empty;
            }
        }

        /// <summary>
        /// Resolve the player, its weapon loadout and the room — <b>retried every frame until each
        /// succeeds</b>, never latched on the first attempt.
        ///
        /// <para>The player is spawned by the room, and the room is streamed; a bootstrapper that
        /// captured the first result would keep pointing at a destroyed unit after the first door.
        /// The retry costs one <c>FindFirstObjectByType</c> per frame only while the answer is still
        /// missing — the same "retry rather than latch" shape <c>EliteNameplateView</c> uses for its
        /// room.</para>
        /// </summary>
        private void ResolveScene()
        {
            if (_player == null)
            {
                _player = FindFirstObjectByType<PlayerController>();
                if (_player != null)
                {
                    _loadout = _player.GetComponentInChildren<PlayerWeaponLoadout>();
                    _stats = _player.GetComponentInParent<CharacterStats>();
                }
            }

            if (_roomVisual == null && _player != null)
            {
                _roomVisual = _player.GetComponentInParent<RoomVisualController>();
            }
        }

        /// <summary>
        /// The input, sampled at frame rate and handed to the controller as latches.
        ///
        /// <para><b><c>V</c> / middle mouse is the toggle; <c>LMB</c>, <c>Q</c> and <c>E</c> are the
        /// mode's own keys while it is open</b> (<c>Sats.as:181-201</c>). The three gameplay keys are
        /// read here rather than through their message publishers because the mode <i>consumes</i>
        /// them: in AS3 <c>sats.step()</c> clears <c>keyAttack</c>/<c>keyTele</c>/<c>keyAction</c> so
        /// nothing else sees them that frame, and the port's equivalent is that the weapon and the
        /// player's action handler are behind the same pause gate this mode holds.</para>
        /// </summary>
        private void PollInput()
        {
            InputReader input = _player != null ? _player.InputService : null;
            if (input == null) return;

            if (Pressed(input.SatsToggle)) _sats.LatchToggle();

            if (!_sats.IsActive) return;

            Camera cam = Camera.main;
            if (cam != null)
            {
                Vector3 mouseScreen = Input.mousePosition;
                if (!cam.orthographic) mouseScreen.z = -cam.transform.position.z;

                Vector3 world = cam.ScreenToWorldPoint(mouseScreen);
                _sats.SetPointerWorldPixels(new Vector2(world.x * 100f, world.y * 100f));
            }

            if (Pressed(input.Attack)) _sats.LatchQueueShot();
            if (Pressed(input.Teleport)) _sats.LatchRemoveLast();
            if (Pressed(input.Interact)) _sats.LatchAccept();
        }

        private static bool Pressed(UnityEngine.InputSystem.InputAction action)
        {
            return action != null && action.WasPressedThisFrame();
        }

        private void LateUpdate()
        {
            if (_sats == null || _view == null) return;

            // The tracer must be traced BEFORE the view is synced: the blast ring is pinned to its
            // endpoint (`radius.x = weapon.trasser.X`, Sats.as:556-557), so the view needs this
            // frame's result, not last frame's.
            SatsRadiusSpec radius = SyncTracer();

            // `!weapon.noPerc` decides whether a percentage is printed AND whether a unit can be
            // locked at all — see SatsController.ShowsPercentages.
            _view.Sync(_sats, _sats.ShowsPercentages, radius);
            _apBar?.Sync(_sats.Od, _sats.Odv, _sats.ApBarApplicable, _sats.ApBarVisible);
        }

        // ── Canvas ───────────────────────────────────────────────────────────────────────

        private void BuildCanvas()
        {
            var canvasGo = new GameObject("SATS Canvas");
            canvasGo.transform.SetParent(transform, false);

            _canvas = canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = CanvasSortingOrder;

            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            canvasGo.AddComponent<GraphicRaycaster>().enabled = false;

            _font = ResolveFont();
            _composition = Resources.Load<PipBuckCompositionDefinition>("UI/PipBuckComposition");

            // The overlay first, so the hint strip and the message line draw on top of it.
            var viewGo = new GameObject("SATS View", typeof(RectTransform));
            var viewRect = (RectTransform)viewGo.transform;
            viewRect.SetParent(canvasGo.transform, false);
            Stretch(viewRect);

            _view = viewGo.AddComponent<SatsView>();
            _view.Construct(canvasGo.transform, _font, _composition);

            BuildTopStrip(canvasGo.transform);
            BuildMessageLine(canvasGo.transform);
            BuildApBar(canvasGo.transform);
            BuildTracer();
        }

        /// <summary>
        /// The ballistic preview — <c>weapon.setTrass</c> plus <c>Trasser.trass</c>
        /// (<c>Weapon.as:1641-1656</c>, <c>Trasser.as:56-114</c>).
        ///
        /// <para><b>A world-space <see cref="LineRenderer"/>, not UI geometry.</b> The arc is a polyline
        /// in world pixels; building it out of canvas images would need one object per sample (up to
        /// 101 of them) and a projection per vertex, for a line the camera already knows how to draw.
        /// The Oracle draws it into the world's own graphics layer for the same reason.</para>
        /// </summary>
        private void BuildTracer()
        {
            var go = new GameObject("SATS Tracer");
            go.transform.SetParent(transform, false);

            _tracer = go.AddComponent<LineRenderer>();
            _tracer.useWorldSpace = true;
            _tracer.positionCount = 0;
            _tracer.startWidth = 0.05f;   // 5 source pixels at 100 px per unit
            _tracer.endWidth = 0.05f;
            _tracer.numCapVertices = 0;
            _tracer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _tracer.receiveShadows = false;

            // `lineStyle(5, 0x00FF99, 0.5)` (Trasser.as:68) — 0x00FF99 at 50 % alpha.
            var material = new Material(Shader.Find("Sprites/Default"));
            material.color = new Color(0f, 1f, 0.6f, 0.5f);
            _tracer.material = material;
            _tracer.startColor = new Color(0f, 1f, 0.6f, 0.5f);
            _tracer.endColor = new Color(0f, 1f, 0.6f, 0.5f);
            _tracer.enabled = false;
        }

        /// <summary>
        /// Redraw the preview for the current aim. Only for a weapon that draws one —
        /// <c>weapon.noTrass</c> is set by <c>WClub</c> alone (<c>WClub.as:114</c>), which is also the
        /// only class with <c>tip == 1</c>, so "melee" and "no tracer" are the same set.
        ///
        /// <para><b>Three approximations, all recorded rather than hidden.</b> The muzzle is the
        /// player's own origin rather than the weapon's drawn tip (the loadout does not expose the
        /// presenter's muzzle point); the speed is the weapon's authored <c>projectileSpeed</c>
        /// without <c>speedMult</c>; and the blast ring uses the raw <c>explRadius</c> rather than
        /// <c>explRadius * explRadMult</c> (which is what <c>setBullet</c> applies to the real round).
        /// All three make the preview slightly different from the round — the shape is right, the
        /// exact impact point can be a few pixels out. The preview is a guide, not a promise, which
        /// is why this is acceptable here and would not be acceptable for the shot.</para>
        ///
        /// <para><b>Returns the radius-sprite state, not just a line.</b> The Oracle owns ONE
        /// <c>radius</c> sprite and uses it for two different circles: the melee reach ring when
        /// <c>tip &lt;= 1</c> (<c>Sats.as:126-135</c>), or the blast ring pinned to the tracer's
        /// <b>endpoint</b> when <c>weapon.explRadius</c> is set (<c>Sats.as:551-558</c>); otherwise it
        /// is hidden. Throwing away <c>endPx</c> — as this method did — leaves the blast ring either
        /// absent or stuck at the cursor, which is the one place the Oracle does NOT put it.</para>
        /// </summary>
        private SatsRadiusSpec SyncTracer()
        {
            if (_tracer == null) return SatsRadiusSpec.Hidden;

            if (!_sats.IsActive || _player == null || _loadout == null || _loadout.Current == null)
            {
                _tracer.enabled = false;
                return SatsRadiusSpec.Hidden;
            }

            // IWeaponStats, not WeaponDefinition — the loadout exposes the interface, and all three
            // fields used here (weaponType, projectileSpeed, explRadius) are on it.
            var def = _loadout.Current.State.Def;
            if (def.weaponType == WeaponType.Melee)
            {
                // `tip <= 1`: no tracer at all; the radius sprite becomes the melee reach ring at
                // `X + meleeS*storona`, `Y - scY/2`, scaled `meleeR / 100` (Sats.as:128-135).
                // `pers.meleeS` (40) has no ported field, so its authored default is kept here.
                _tracer.enabled = false;

                CharacterStats stats = _sats.Stats;
                float meleeR = stats != null ? stats.meleeR : 100f;
                int facing = _player.FacingDirection;
                float scY = _player.SpriteSizePixels.y;
                Vector3 p = _player.WorldPosition;

                var meleePx = new Vector2(
                    p.x * 100f + 40f * (facing < 0 ? -1 : 1),
                    p.y * 100f - scY * 0.5f);

                return new SatsRadiusSpec(meleePx, meleeR / 100f);
            }

            Vector2 aimPx = _sats.PointerWorldPixels;
            Vector3 origin = _player.WorldPosition;
            Vector2 originWorldPx = new Vector2(origin.x * 100f, origin.y * 100f);

            Vector2 delta = aimPx - originWorldPx;
            if (delta.sqrMagnitude < 1f)
            {
                _tracer.enabled = false;
                return SatsRadiusSpec.Hidden;
            }

            Vector2 dir = delta.normalized;
            float speed = def.projectileSpeed;

            // `if (this.grav) trasser.ddy += World.ddy` (Weapon.as:1651-1654), World.ddy = 1
            // (World.as:46). Two things are easy to get wrong here:
            //   1. AS3's Y runs DOWN, the port's runs UP (WorldCoordinates.UnityToPixel just scales
            //      Unity's Y). So the mirror is a NEGATIVE acceleration, never a floor/negation of
            //      the position — a sign flip on dy, not on the arc.
            //   2. The trasser adds the CONSTANT 1, not `World.ddy * grav`; the weapon's `grav`
            //      multiplier only decides whether gravity applies at all (the real bullet at
            //      Weapon.as:1558 does multiply). Passing bulletGravity here would over-curve a
            //      weapon authored with grav=2.
            float gravity = def.bulletGravity > 0f ? -1f : 0f;

            SatsTrajectory.Trace(
                originX: originWorldPx.x,
                originY: originWorldPx.y,
                velocityX: dir.x * speed,
                velocityY: dir.y * speed,
                gravityPxPerTickSquared: gravity,
                isBlocked: IsSolidTileAt,
                points: _tracePoints,
                endPx: out SatsTracePoint endPx,
                blocked: out _);

            if (_tracePoints.Count < 2)
            {
                _tracer.enabled = false;
                return SatsRadiusSpec.Hidden;
            }

            _tracer.positionCount = _tracePoints.Count;
            for (int i = 0; i < _tracePoints.Count; i++)
            {
                SatsTracePoint pnt = _tracePoints[i];
                _tracer.SetPosition(i, new Vector3(pnt.X / 100f, pnt.Y / 100f, 0f));
            }

            _tracer.enabled = true;

            // `radius.x = weapon.trasser.X` — the END of the predicted arc, not the cursor.
            return def.explRadius > 0f
                ? new SatsRadiusSpec(new Vector2(endPx.X, endPx.Y), def.explRadius / 100f)
                : SatsRadiusSpec.Hidden;
        }

        /// <summary>
        /// Whether a world-pixel point is inside a solid tile, for the preview's collision test.
        ///
        /// <para><b>A one-iteration ray.</b> <c>ITileQueryService.Raycast</c> steps in 9 px
        /// increments and tests <i>before</i> advancing, so a maximum distance smaller than one step
        /// tests exactly the origin point and nothing else — which is the question here. Going through
        /// the query rather than a tile lookup keeps the world-pixel → room-local conversion in the one
        /// place that already owns it; doing it by hand is the coordinate mistake this project has made
        /// before.</para>
        /// </summary>
        private bool IsSolidTileAt(float worldPxX, float worldPxY)
        {
            ITileQueryService query = ResolveTileQuery();
            if (query == null) return false;

            return query.Raycast(new Vector2(worldPxX, worldPxY), Vector2.right, 1f) != null;
        }

        /// <summary>
        /// <c>gui.setTopText("infosats")</c> — the hint strip (<c>GUI.as:1123-1140</c>). The Oracle
        /// substitutes the live keybinds for <c>@1</c>…<c>@4</c> and turns <c>@@</c> into a newline,
        /// which is why the text can be rebound without editing the string.
        /// </summary>
        private void BuildTopStrip(Transform parent)
        {
            _topStrip = NewText(parent, "SATS Hint", 14, TextAnchor.UpperCenter, new Vector2(0f, -18f),
                new Vector2(1200f, 60f));
            _topStrip.color = new Color(0.8f, 1f, 0.85f, 0.95f);
            _topStrip.text = string.Empty;
        }

        private void BuildMessageLine(Transform parent)
        {
            _message = NewText(parent, "SATS Message", 18, TextAnchor.MiddleCenter, new Vector2(0f, -140f),
                new Vector2(900f, 40f));
            _message.color = new Color(1f, 0.9f, 0.4f, 1f);
            _message.text = string.Empty;
        }

        /// <summary>
        /// The AP bar, anchored bottom-right by <see cref="HudSlot.ActionPointsBar"/>'s own row
        /// (<c>HudLayout.cs:518</c>: <c>(-10, 10)</c> from the bottom-right corner).
        /// </summary>
        private void BuildApBar(Transform parent)
        {
            var root = new GameObject("AP Bar", typeof(RectTransform));
            var rect = (RectTransform)root.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = new Vector2(1f, 0f);
            rect.pivot = new Vector2(1f, 0f);
            rect.anchoredPosition = new Vector2(-10f, 10f);
            rect.sizeDelta = new Vector2(180f, 14f);

            var group = root.AddComponent<CanvasGroup>();
            group.alpha = 0f;
            group.interactable = false;
            group.blocksRaycasts = false;

            var track = NewImage(rect, "Track", new Color(0f, 0f, 0f, 0.65f));
            Stretch(track.rectTransform);

            var projected = NewImage(rect, "Projected", ActionPointsBarView.FillProjected);
            Stretch(projected.rectTransform);
            projected.type = Image.Type.Filled;
            projected.fillMethod = Image.FillMethod.Horizontal;
            projected.fillOrigin = (int)Image.OriginHorizontal.Left;
            projected.fillAmount = 0f;

            var real = NewImage(rect, "Real", ActionPointsBarView.FillReal);
            Stretch(real.rectTransform);
            real.type = Image.Type.Filled;
            real.fillMethod = Image.FillMethod.Horizontal;
            real.fillOrigin = (int)Image.OriginHorizontal.Left;
            real.fillAmount = 0f;

            Text label = NewText(rect, "AP Label", 12, TextAnchor.MiddleRight,
                new Vector2(-8f, 0f), new Vector2(60f, 18f));
            label.text = "AP";

            _apBar = root.AddComponent<ActionPointsBarView>();
            _apBar.Construct(projected, real, label, group);
        }

        private Text NewText(Transform parent, string name, int size, TextAnchor align,
            Vector2 offset, Vector2 size2)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = offset;
            rect.sizeDelta = size2;

            var text = go.AddComponent<Text>();
            text.font = _font;
            text.fontSize = size;
            text.alignment = align;
            text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.text = string.Empty;
            return text;
        }

        private static Image NewImage(Transform parent, string name, Color colour)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);

            var image = go.AddComponent<Image>();
            image.color = colour;
            image.raycastTarget = false;
            return image;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        /// <summary>
        /// The interface font, from the composition asset. Falls back to an OS font with a warning —
        /// the same fallback and the same reasoning as <c>PipBuckBootstrapper.ResolveFont</c>.
        /// </summary>
        private Font ResolveFont()
        {
            if (_composition != null && _composition.terminalFont != null) return _composition.terminalFont;

            Debug.LogWarning("[SatsBootstrapper] The composition has no terminalFont. Falling back to an " +
                             "OS font — the S.A.T.S. labels will not render in the authentic typeface.");
            return Font.CreateDynamicFontFromOSFont(
                new[] { "Lucida Console", "Consolas", "Courier New", "DejaVu Sans Mono" }, 16);
        }

        // ── ISatsFeedback ───────────────────────────────────────────────────────────────

        private sealed class FeedbackSink : ISatsFeedback
        {
            private readonly SatsBootstrapper _owner;

            public FeedbackSink(SatsBootstrapper owner) { _owner = owner; }

            /// <summary>
            /// <c>gui.infoText(key)</c>. The string comes from the localisation document's
            /// <c>&lt;info&gt;</c> table — the same table the Oracle reads — so a refusal says what the
            /// original says rather than an invented English sentence.
            ///
            /// <para><b>An unknown key is shown as the key.</b> A silently blank line would make "the
            /// mode did nothing" and "the message is missing" look identical, which is the failure this
            /// whole seam exists to prevent.</para>
            /// </summary>
            public void InfoText(string key)
            {
                if (string.IsNullOrEmpty(key) || _owner._message == null) return;

                string text = PfeTextTableProvider.Shared.TryGet("f", key, 0, out string found)
                    ? found
                    : key;

                _owner._message.text = text;
                _owner._messageTicksLeft = InfoMessageTicks;
            }

            /// <summary>
            /// <c>gui.setTopText(key)</c> — <c>@1…@4</c> become the live bindings and <c>@@</c> a
            /// newline (<c>GUI.as:1123-1140</c>).
            /// </summary>
            public void SetTopText(string key)
            {
                if (_owner._topStrip == null) return;

                if (string.IsNullOrEmpty(key))
                {
                    _owner._topStrip.text = string.Empty;
                    return;
                }

                string raw = PfeTextTableProvider.Shared.TryGet("g", key, 0, out string found)
                    ? found
                    : string.Empty;

                if (raw.Length == 0) { _owner._topStrip.text = string.Empty; return; }

                // ORDER IS THE WHOLE POINT. `Res.addKeys()` (Res.as:2637-2648) substitutes @1..@5
                // FIRST; only then does `GUI.setTopText()` replace /@/g with newlines. The authored
                // string is "@1 - select@@2 - cancel@@3 or @4 - accept": each "@@" is ONE newline
                // marker followed by the '@' of the NEXT placeholder, not a two-character escape.
                // Converting "@@" first destroyed the @2/@3 tokens, so the hint printed bare "2" and
                // "3" — and told the player to press "3" to accept, which is not a bound key. The
                // real accept keys are @3 (E / Interact) and @4 (V / middle mouse).
                InputReader input = _owner._player != null ? _owner._player.InputService : null;

                raw = raw.Replace("@1", Binding(input == null ? null : input.Attack))
                         .Replace("@2", Binding(input == null ? null : input.Teleport))
                         .Replace("@3", Binding(input == null ? null : input.Interact))
                         .Replace("@4", Binding(input == null ? null : input.SatsToggle));

                // Every remaining '@' is a line break — the leftover half of each "@@".
                raw = raw.Replace("@", "\n");

                _owner._topStrip.text = raw;
            }

            private static string Binding(UnityEngine.InputSystem.InputAction action)
            {
                if (action == null) return "?";
                string s = action.GetBindingDisplayString();
                return string.IsNullOrEmpty(s) ? "?" : s;
            }
        }

        // ── ISatsWorldQuery ─────────────────────────────────────────────────────────────

        private sealed class WorldQuery : ISatsWorldQuery
        {
            private readonly SatsBootstrapper _owner;

            public WorldQuery(SatsBootstrapper owner) { _owner = owner; }

            /// <summary>
            /// <c>Unit.getTileVisi()</c> — the fog mask's value at the unit. The port's read-back is
            /// <see cref="RoomBackdropRenderer.TryGetFogVisibility"/>, which is the same mask the
            /// renderer draws, so a unit the player cannot see is a unit S.A.T.S. will not offer.
            ///
            /// <para><b><c>false</c> means "no mask", not "pitch black".</b> With no room visual at
            /// all there is nothing drawn over the unit either, so the value must be the fully-lit
            /// <c>1</c>; returning <c>0</c> here would make a scene without a room hide every target
            /// even after <c>SatsRules.FogAdmitsTarget</c> correctly fails open.</para>
            /// </summary>
            public bool TryGetFogVisibility(Vector3 worldPosition, out float visibility)
            {
                visibility = 1f;
                if (_owner._roomVisual == null) return false;
                return _owner._roomVisual.TryGetFogVisibility(worldPosition, out visibility);
            }

            /// <summary>
            /// The tile ray <c>Unit.look()</c> walks (<c>Unit.as:4619-4636</c>), with two of its
            /// quirks preserved.
            ///
            /// <para><b>The ray does not start at the feet.</b> AS3 samples from
            /// <c>Y - scY * 0.75</c> — three quarters of the body up — because a unit standing on a
            /// ledge would otherwise be blocked by the tile it stands on before the ray left its own
            /// cell. The port starts at the body centre and skips the first ~36 px, which is AS3's
            /// <c>_loc10_ = 4</c> samples at <c>maxdelta = 9</c>.</para>
            ///
            /// <para><b>Fails open when there is no room.</b> A unit with no room has no tiles to hit;
            /// returning <c>false</c> would make S.A.T.S. offer nothing at all in a bare test scene,
            /// which is a worse answer than "nothing is in the way".</para>
            /// </summary>
            public bool HasLineOfSight(Vector2 fromWorldPx, Vector2 toWorldPx)
            {
                ITileQueryService query = _owner.ResolveTileQuery();
                if (query == null) return true;

                Vector2 delta = toWorldPx - fromWorldPx;
                float distance = delta.magnitude;
                if (distance <= 1f) return true;

                Vector2 dir = delta / distance;
                Vector2 origin = fromWorldPx + dir * 36f;
                float remaining = distance - 36f;
                if (remaining <= 0f) return true;

                return query.Raycast(origin, dir, remaining) == null;
            }
        }

        /// <summary>
        /// The room's tile query, rebuilt only when the room changes. <c>UnifiedTileQueryService</c>
        /// wraps the room's own tile grid, so it must not outlive the room it was built for.
        /// </summary>
        private ITileQueryService ResolveTileQuery()
        {
            if (_roomVisual == null) return null;

            RoomInstance room = _roomVisual.RoomInstance;
            if (room == null) return null;

            if (_tileQuery == null || !ReferenceEquals(_tileQueryRoom, room))
            {
                _tileQueryRoom = room;
                _tileQuery = new UnifiedTileQueryService(room);
            }

            return _tileQuery;
        }
    }
}
