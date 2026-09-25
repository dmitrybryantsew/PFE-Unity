#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using PFE.Systems.Map;
using PFE.Systems.Map.Rendering;

namespace PFE.Editor
{
    /// <summary>
    /// Room editor scaffold (v1) — paints the authored tile grid of a RoomTemplate.
    ///
    /// Design rule: this editor edits RAW CODE STRINGS, never TileData.
    /// TileData is the derived output of TileDecoder and carries ~25 fields that are almost
    /// all computed from the form lookup. Round-tripping TileData back into a code string
    /// would have to guess which form produced it and would silently lose data. Editing the
    /// code string is lossless, AS3-faithful and git-diffable, and needs no encoder.
    ///
    /// Ordering: _codes[row][col] with row 0 = TOP, matching tileDataString ordering.
    /// TileDecoder.ParseRoom flips that into Unity's bottom-up y
    /// (unityY = ROOM_HEIGHT - 1 - row). The grid is drawn top-down so what you see in the
    /// window is what is in the string.
    ///
    /// Deliberately out of scope for v1: door slots, objects, spawn points, background
    /// layer rooms, AS3 XML export. See docs/MapGeneration_GapInvestigation_2026-09-25.md.
    /// </summary>
    public class RoomEditorWindow : EditorWindow
    {
        private const string RoomTemplateRoot = "Assets/_PFE/Data/Resources/Rooms";
        private const string AirToken = "_";
        private const int GridWidth = WorldConstants.ROOM_WIDTH;
        private const int GridHeight = WorldConstants.ROOM_HEIGHT;
        private const float MinZoom = 2f;
        private const float MaxZoom = 48f;
        private const float LabelZoom = 20f;

        [MenuItem("PFE/Map/Room Editor", false, 12)]
        public static void Open()
        {
            RoomEditorWindow window = GetWindow<RoomEditorWindow>("Room Editor");
            window.minSize = new Vector2(820f, 620f);
            window.Show();
        }

        // --- template list ---
        private List<RoomTemplate> _templates = new List<RoomTemplate>();
        private string[] _templateOptions = Array.Empty<string>();
        private int _templateIndex;
        private RoomTemplate _template;
        private bool _templateListDirty = true;

        // --- working model ---
        private string[][] _codes;
        private TileData[,] _derived;
        private bool _dirty;

        /// <summary>
        /// Set by PaintCell, consumed once per layout pass. The derived grid is what colours the
        /// cells and resolves physics, so it has to refresh while a stroke is still in progress —
        /// otherwise the map only changes colour when the mouse is released. Rebuilding on the
        /// layout pass rather than per painted cell keeps a fast drag from re-parsing the whole
        /// room dozens of times a frame.
        /// </summary>
        private bool _derivedDirty;

        private string _status = "";

        // --- palette ---
        private TileFormDatabase _formDb;
        private bool _formDbLookedUp;
        private List<string> _fFormIds = new List<string>();
        private List<string> _oFormIds = new List<string>();
        private string _baseForm = "C";

        /// <summary>
        /// Selected overlay chars, in click order. A tile can carry several at once and the real
        /// rooms do: the common walkable stair is air + a background + the stair char (_CА, 187
        /// uses; _RА 175; _IА 149), not just _А. BuildCode sorts these by ed before emitting,
        /// which reproduces the hand-authored convention (backgrounds, then stairs, then slopes).
        /// </summary>
        private readonly List<string> _overlays = new List<string>();

        /// <summary>Scratch copy of _overlays sorted for emission; reused to avoid per-call allocs.</summary>
        private readonly List<string> _overlayOrder = new List<string>();

        /// <summary>One-entry wrapper so the base grid can share DrawFormGrid with the overlay grid.</summary>
        private readonly List<string> _singleSelection = new List<string>();

        /// <summary>
        /// Overlay forms by char, built once alongside the form database. Avoids a 1-char string
        /// allocation per character per cell per repaint when the grid draws overlay art.
        /// </summary>
        private readonly Dictionary<char, TileForm> _overlayByChar = new Dictionary<char, TileForm>();

        private bool _water;
        private bool _eraser;
        private int _zForm;

        /// <summary>
        /// Author the base as AIR instead of a wall form. Air is not a form at all — it is the
        /// absence of one — but it is what makes overlay-only codes reachable. Real rooms write
        /// walkable stairs as "_" plus the Cyrillic stair overlay (e.g. _А, _Б), and slopes and
        /// shelves the same way. Without this every overlay rides on a solid wall, so a stair
        /// ends up welded to a wall and the cell stays un-walkable — exactly the
        /// "stairs get together with a wall and i dont have air" symptom.
        /// </summary>
        [SerializeField] private bool _airBase;

        // --- preview / help ---
        // Sizes and toggles are [SerializeField] fields rather than consts so the Settings tab
        // can drive them. EditorWindow derives from ScriptableObject, so these survive a domain
        // reload and come back with the window layout on the next session.
        [SerializeField] private float _sidePanelWidth = 310f;
        [SerializeField] private float _thumbSize = 38f;
        [SerializeField] private float _letterButtonSize = 34f;
        [SerializeField] private int _paletteColumns = 6;
        [SerializeField] private float _previewSize = 84f;

        /// <summary>Which side-panel tab is showing: 0 = Tiles, 1 = Settings.</summary>
        [SerializeField] private int _sideTab;

        /// <summary>Draw palette entries as art instead of their code letter.</summary>
        [SerializeField] private bool _images = true;

        /// <summary>
        /// Compose the preview through the real TileCompositor instead of stacking raw
        /// material textures. On by default; turn it off if the compositor's per-(material,
        /// phase, kontur) Texture2D cache makes the window sluggish on a slow machine.
        /// </summary>
        [SerializeField] private bool _livePreview = true;

        [SerializeField] private bool _showHelp = true;

        /// <summary>Draw the 1px seam between cells that reads as grid lines.</summary>
        [SerializeField] private bool _gridLines = true;

        /// <summary>
        /// Draw each cell's overlay art (stairs, slopes, shelves, backgrounds) over its physics
        /// colour. Without this the grid is flat colour plus a code string, which makes a stair
        /// indistinguishable from a wall while painting.
        /// </summary>
        [SerializeField] private bool _gridArt = true;

        /// <summary>
        /// Which way the wheel zooms. Default false = wheel up zooms OUT, because that is what the
        /// owner's machine produces — Unity's IMGUI scroll delta sign is not consistent across
        /// platforms and input devices. Exposed as an explicit direction choice rather than an
        /// "invert" checkbox so there is no double negative to reason about.
        /// </summary>
        [SerializeField] private bool _wheelUpZoomsIn;

        /// <summary>Draw tile codes in the grid regardless of zoom (normally gated by LabelZoom).</summary>
        [SerializeField] private bool _alwaysShowCodes;

        /// <summary>Outline the hovered cell. Off is useful when the outline hides the art.</summary>
        [SerializeField] private bool _hoverHighlight = true;

        private Vector2 _sideScroll;

        /// <summary>
        /// The panel width actually used this frame, after clamping against the window size.
        /// The palette reads it so its column count can never exceed what really fits.
        /// </summary>
        private float _resolvedPanelWidth = 310f;

        private TileAssetDatabase _tileAssets;
        private TileTextureLookup _textureLookup;
        private MaterialRenderDatabase _materialDb;
        private TileMaskLookup _maskLookup;
        private TileCompositor _compositor;
        private bool _renderAssetsLookedUp;

        // Keyed by "id|ed" — NOT by id alone. fForms and oForms share the ids A..T
        // (fForm A = steel wall, oForm A = wall backdrop), so an id-only key would
        // hand the palette the wrong art for half the alphabet.
        private readonly Dictionary<string, TileThumb> _thumbCache =
            new Dictionary<string, TileThumb>(StringComparer.Ordinal);

        private GUIStyle _thumbFallbackStyle;

        // --- palette grid geometry ---
        // The palette is laid out as ONE reserved layout block with every cell placed at an
        // absolute offset inside it. Reserving a rect per row let the vertical group negotiate the
        // width back, which is how the grid ended up shifted sideways with its first column clipped.
        private struct GridGroup
        {
            public int Ed;
            public int Start;
            public int Count;
        }

        private readonly List<GridGroup> _gridGroups = new List<GridGroup>();
        private int _lastPaletteColumns;
        private float _lastPaletteCellSize;

        // --- serialized settings version ---
        // Settings are serialized into the window layout, so editing a field's initialiser does NOT
        // change the effective default for a window that was already saved — the stored value wins.
        // Renaming the field would work but throws away every other saved setting, so instead bump
        // this constant and add a step to MigrateSettings. Version 0 means "saved before this field
        // existed", i.e. every setting still holds its original initialiser.
        private const int SettingsVersion = 2;
        [SerializeField] private int _settingsVersion;

        // --- palette hover tooltip ---
        // Unity's built-in IMGUI tooltip is unreliable inside a custom EditorWindow: it depends on
        // the platform tooltip timer plus a repaint, and a stationary mouse generates no events to
        // drive either. So both the delay and the drawing are done by hand.
        private const double TooltipDelaySeconds = 0.45;
        private const float TooltipWidth = 320f;

        private string _hoveredFormId;
        private TileForm _hoveredForm;
        private double _hoveredFormSince;
        private bool _tooltipReady;
        private string _tooltipText;
        private GUIStyle _tooltipStyle;

        // --- tooltip contents (configured in the Settings tab) ---
        // Every line group is opt-in so the tooltip can be trimmed to taste. Defaults show the
        // whole record, because the point of the tooltip is that the palette letters are opaque.
        // NOTE: there is no "biome" field anywhere in the project — biome is not a tile property.
        // The closest real data is the room's RoomEnvironment (colorScheme / backgroundWall /
        // hasSky), which belongs to a room, not to a form, so it has no place on a tile tooltip.
        [SerializeField] private bool _tipName = true;
        [SerializeField] private bool _tipKind = true;
        [SerializeField] private bool _tipPhysics = true;
        [SerializeField] private bool _tipDurability = true;
        [SerializeField] private bool _tipMaterial = true;
        [SerializeField] private bool _tipGraphics = true;
        [SerializeField] private bool _tipFlags = true;
        [SerializeField] private bool _tipUsageInRoom = true;

        /// <summary>
        /// A drawable thumbnail: a texture plus the sub-rect to sample. Sprites and raw
        /// textures both reduce to this, so the palette and the preview share one draw path.
        /// </summary>
        private struct TileThumb
        {
            public Texture Texture;
            public Rect Uv;
            public string Source;

            public bool IsValid => Texture != null;

            public static TileThumb FromTexture(Texture texture, string source)
            {
                return texture == null
                    ? default
                    : new TileThumb { Texture = texture, Uv = new Rect(0f, 0f, 1f, 1f), Source = source };
            }

            public static TileThumb FromSprite(Sprite sprite, string source)
            {
                if (sprite == null || sprite.texture == null)
                {
                    return default;
                }

                Texture2D texture = sprite.texture;
                Rect rect = sprite.textureRect;

                if (texture.width <= 0 || texture.height <= 0 || rect.width <= 0f || rect.height <= 0f)
                {
                    return default;
                }

                return new TileThumb
                {
                    Texture = texture,
                    Uv = new Rect(
                        rect.x / texture.width,
                        rect.y / texture.height,
                        rect.width / texture.width,
                        rect.height / texture.height),
                    Source = source
                };
            }
        }

        // --- view (transformed canvas, not a scroll view) ---
        // Grid space is 1 unit per cell with the origin at the room's TOP-LEFT corner and
        // y increasing downward — the same orientation as _codes, so row 0 is the top row.
        // The view is a plain 2D similarity transform:
        //     screen = _viewRect.position + _pan + grid * _zoom
        // It is applied by hand (ScreenToGrid / the draw loop) rather than by writing
        // GUI.matrix, because GUI.matrix also scales font sizes — labels would become
        // unreadable at high zoom and sub-pixel at low zoom.
        private float _zoom = 14f;
        private Vector2 _pan;
        private Rect _viewRect;
        private int _hoverRow = -1;
        private int _hoverCol = -1;
        private bool _painting;
        private bool _strokeRecorded;
        private bool _panning;
        private bool _viewFramed;
        private GUIStyle _cellLabelStyle;

        private void OnEnable()
        {
            wantsMouseMove = true;
            _templateListDirty = true;
        }

        private void OnLostFocus()
        {
            // MouseUp can be delivered to whatever window steals focus, so a stroke or a pan
            // would otherwise stay latched on across an alt-tab.
            _panning = false;

            if (_painting)
            {
                EndStroke();
            }
        }

        private void OnGUI()
        {
            EnsureSettingsMigrated();

            // Consume the pending derived rebuild once per layout pass, before anything reads
            // _derived. Doing it here instead of inside PaintCell means a fast drag re-parses the
            // room at most once a frame rather than once per painted cell — which is what made
            // painting feel like it only applied on mouse-up.
            if (_derivedDirty && Event.current.type == EventType.Layout)
            {
                RebuildDerived();
                _derivedDirty = false;
            }

            // Hover tracking is rebuilt every frame: DrawFormCell re-arms it for whichever palette
            // cell the mouse is over, so anything left unset here was genuinely not hovered.
            string hoveredLastFrame = _hoveredFormId;
            _hoveredFormId = null;
            _hoveredForm = null;
            _tooltipText = null;

            DrawToolbar();

            if (_formDb == null)
            {
                EditorGUILayout.HelpBox(
                    "No TileFormDatabase found. Paint and decode are disabled until one exists " +
                    "(expected at Assets/_PFE/Data/TileFormDatabase.asset).",
                    MessageType.Warning);
            }

            EditorGUILayout.BeginHorizontal();
            DrawSidePanel();
            DrawGridArea();
            EditorGUILayout.EndHorizontal();

            DrawStatusBar();

            UpdatePaletteTooltip(hoveredLastFrame);
            DrawPaletteTooltip();
        }

        // ---------------------------------------------------------------- palette tooltip

        /// <summary>
        /// Arms and fires the palette hover tooltip. Called at the very end of OnGUI, after the
        /// palette has had a chance to report what the mouse is over this frame.
        /// </summary>
        private void UpdatePaletteTooltip(string previouslyHovered)
        {
            if (string.IsNullOrEmpty(_hoveredFormId))
            {
                _tooltipReady = false;
                _hoveredFormSince = 0d;
                return;
            }

            if (!string.Equals(_hoveredFormId, previouslyHovered, StringComparison.Ordinal))
            {
                _hoveredFormSince = EditorApplication.timeSinceStartup;
                _tooltipReady = false;
            }
            else if (!_tooltipReady
                && EditorApplication.timeSinceStartup - _hoveredFormSince >= TooltipDelaySeconds)
            {
                _tooltipReady = true;
            }

            if (!_tooltipReady)
            {
                // A stationary mouse produces no events, so the delay could never elapse without
                // asking for another pass. This stops the moment the tooltip is up.
                Repaint();
                return;
            }

            _tooltipText = FormTooltip(_hoveredFormId, _hoveredForm);
        }

        private void DrawPaletteTooltip()
        {
            if (string.IsNullOrEmpty(_tooltipText) || Event.current.type != EventType.Repaint)
            {
                return;
            }

            GUIStyle style = TooltipStyle();
            float height = style.CalcHeight(new GUIContent(_tooltipText), TooltipWidth - 14f) + 14f;

            // GUI drawing is not clipped to any rect, so an unclamped box would spill outside the
            // editor window entirely. Keep it fully on screen instead.
            Vector2 mouse = Event.current.mousePosition;
            Rect box = new Rect(mouse.x + 16f, mouse.y + 16f, TooltipWidth, height);

            box.x = Mathf.Clamp(box.x, 4f, Mathf.Max(4f, position.width - box.width - 4f));
            box.y = Mathf.Clamp(box.y, 4f, Mathf.Max(4f, position.height - box.height - 4f));

            EditorGUI.DrawRect(box, new Color(0.12f, 0.12f, 0.13f, 0.97f));
            DrawRectOutline(box, new Color(0f, 0f, 0f, 0.75f), 1f);
            GUI.Label(
                new Rect(box.x + 7f, box.y + 7f, box.width - 14f, box.height - 14f),
                _tooltipText,
                style);
        }

        private GUIStyle TooltipStyle()
        {
            if (_tooltipStyle == null)
            {
                _tooltipStyle = new GUIStyle(EditorStyles.label)
                {
                    wordWrap = true,
                    richText = false,
                    fontSize = 11,
                    clipping = TextClipping.Clip,
                    padding = new RectOffset(0, 0, 0, 0)
                };
            }

            _tooltipStyle.normal.textColor = new Color(0.86f, 0.86f, 0.86f);
            return _tooltipStyle;
        }

        // ---------------------------------------------------------------- side panel

        /// <summary>
        /// Palette, live preview and help share one scrolling column so the grid keeps the
        /// full window width. Deliberately ONE scroll view rather than one per section:
        /// nested scroll views swallow the wheel and make the panel miserable to use.
        /// The Tiles/Settings tab bar is drawn outside that scroll view so it stays put.
        /// </summary>
        private void DrawSidePanel()
        {
            // Never let the panel starve the canvas: the width is user-settable but clamped so
            // at least ~260px always remains for the grid, even after the window is resized.
            float panelWidth = Mathf.Clamp(
                _sidePanelWidth,
                240f,
                Mathf.Max(240f, position.width - 260f));

            _resolvedPanelWidth = panelWidth;

            EditorGUILayout.BeginVertical(
                GUILayout.Width(panelWidth),
                GUILayout.ExpandHeight(true));

            _sideTab = GUILayout.Toolbar(_sideTab, SideTabLabels, EditorStyles.toolbarButton);
            EditorGUILayout.Space();

            _sideScroll = EditorGUILayout.BeginScrollView(_sideScroll);

            // A palette panel is never meant to scroll sideways, and the offset is a serialized
            // field so it PERSISTS. Once an earlier layout overflowed the panel, the scroll view
            // picked up a non-zero x, and from then on the left-most palette column stayed clipped
            // off the edge no matter how the cells were sized. Pin it to zero every frame.
            _sideScroll.x = 0f;

            if (_sideTab == 1)
            {
                DrawSettingsTab();
            }
            else
            {
                DrawPalette();
                EditorGUILayout.Space();
                DrawPreviewSection();

                if (_showHelp)
                {
                    EditorGUILayout.Space();
                    DrawHelpSection();
                }
            }

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        private static readonly string[] SideTabLabels = { "Tiles", "Settings" };

        // ---------------------------------------------------------------- toolbar

        private void DrawToolbar()
        {
            EnsureTemplateList();

            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            if (_templateOptions.Length > 0)
            {
                int next = EditorGUILayout.Popup(_templateIndex, _templateOptions, EditorStyles.toolbarPopup, GUILayout.Width(320f));
                if (next != _templateIndex && ConfirmDiscard())
                {
                    _templateIndex = next;
                    LoadTemplate(_templates[_templateIndex]);
                }
            }
            else
            {
                EditorGUILayout.LabelField("(no RoomTemplate assets under " + RoomTemplateRoot + ")", GUILayout.Width(320f));
            }

            RoomTemplate picked = EditorGUILayout.ObjectField(_template, typeof(RoomTemplate), false, GUILayout.Width(180f)) as RoomTemplate;
            if (picked != null && picked != _template && ConfirmDiscard())
            {
                LoadTemplate(picked);
            }

            GUILayout.FlexibleSpace();

            using (new EditorGUI.DisabledScope(_template == null || !_dirty))
            {
                if (GUILayout.Button("Revert", EditorStyles.toolbarButton, GUILayout.Width(60f)))
                {
                    LoadTemplate(_template);
                }
            }

            if (GUILayout.Button("Save Assets", EditorStyles.toolbarButton, GUILayout.Width(90f)))
            {
                AssetDatabase.SaveAssets();
                SetStatus("Asset database saved.");
            }

            using (new EditorGUI.DisabledScope(_template == null))
            {
                if (GUILayout.Button("3D Preview", EditorStyles.toolbarButton, GUILayout.Width(80f)))
                {
                    RefreshScenePreview();
                }
            }

            EditorGUILayout.EndHorizontal();
        }

        // ---------------------------------------------------------------- palette

        private void DrawPalette()
        {
            EditorGUILayout.LabelField("Palette", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            _images = GUILayout.Toggle(_images, "Images", EditorStyles.miniButtonLeft, GUILayout.Width(66f));
            _livePreview = GUILayout.Toggle(_livePreview, "Live preview", EditorStyles.miniButtonMid, GUILayout.Width(96f));
            _showHelp = GUILayout.Toggle(_showHelp, "Help", EditorStyles.miniButtonRight, GUILayout.Width(54f));
            EditorGUILayout.EndHorizontal();

            EditorGUI.BeginChangeCheck();
            _eraser = EditorGUILayout.ToggleLeft("Eraser — clear cell to _", _eraser);
            if (EditorGUI.EndChangeCheck() && _eraser)
            {
                _water = false;
                _zForm = 0;
            }

            // One click back to a bare air brush. Without it, backing out of a stacked overlay
            // means deselecting every chip and flipping the base back by hand.
            if (GUILayout.Button("Drop brush to plain air  (_)"))
            {
                SetBrushToAir();
            }

            // Resolved palette geometry, so the size sliders are visibly doing something. Without
            // this readout the only feedback is the cells themselves, and "did my drag apply?" was
            // exactly the thing that was impossible to tell when the size setting was being ignored.
            // The grid is laid out further down this same pass, so the numbers are one frame behind
            // — invisible in practice, but the first frame has nothing to report yet.
            EditorGUILayout.LabelField(
                _lastPaletteCellSize > 0f
                    ? "cell " + Mathf.RoundToInt(_lastPaletteCellSize) + "px  ×  "
                        + _lastPaletteColumns + " column(s)"
                        + (_lastPaletteColumns < _paletteColumns ? "  — capped by panel width" : "")
                    : "cell size resolved on first paint",
                EditorStyles.miniLabel);

            EditorGUILayout.Space();

            EditorGUILayout.LabelField("Modifiers", EditorStyles.boldLabel);
            _water = EditorGUILayout.ToggleLeft("*  water", _water);
            int nextZ = EditorGUILayout.IntSlider("zForm (, ; :)", _zForm, 0, 3);
            if (nextZ != _zForm && !_eraser)
            {
                _zForm = nextZ;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Overlay (oForm) — later chars", EditorStyles.boldLabel);
            DrawFormGrid(_oFormIds, _overlays, true, ToggleOverlay);
            DrawOverlayChips();
            DrawOverlayValidityHint();
            DrawOverlayComboHint();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Base (fForm) — first char", EditorStyles.boldLabel);

            // Air is not an fForm — it is the ABSENCE of one — so it cannot live in the fForm
            // grid. It needs its own control because overlay-only codes (_А, _Б, _-, _*) are how
            // real rooms author walkable stairs, slopes, shelves and water. With a wall base
            // those same overlays ride on a solid tile, which is the "stairs come out welded to
            // a wall" bug: the stair is drawn but the cell is never walk-through.
            DrawBaseModeRow();

            if (!_airBase)
            {
                DrawFormGrid(_fFormIds, _baseForm, false, id =>
                {
                    _baseForm = id;
                    _eraser = false;
                });
            }

            DrawWalkableOverlayHint();

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox("Resulting code:  " + BuildCode(), MessageType.None);
        }

        /// <summary>
        /// Mutually exclusive Air / Wall base selector. Air means "emit _ as the first char and
        /// let the overlay alone define the tile"; Wall means "emit a solid fForm".
        /// </summary>
        private void DrawBaseModeRow()
        {
            EditorGUILayout.BeginHorizontal();

            bool air = GUILayout.Toggle(_airBase, "Air  _", EditorStyles.miniButtonLeft, GUILayout.Width(70f));
            if (air != _airBase)
            {
                _airBase = air;
                if (air)
                {
                    _eraser = false;
                }
            }

            // Value is the inverse of _airBase, so a click that flips it to true means the user
            // asked for Wall while Air was on.
            bool wall = GUILayout.Toggle(!_airBase, "Wall  A..T", EditorStyles.miniButtonMid, GUILayout.Width(96f));
            if (wall == _airBase)
            {
                _airBase = false;
            }

            EditorGUILayout.LabelField(_airBase ? "overlay-only tile" : "solid base", EditorStyles.miniLabel);

            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// A stair or slope overlay is only meaningful on an air base. Warn whenever the user has
        /// picked one on top of a wall, because the resulting code reads as a solid wall with
        /// decoration on it and the walk-through behaviour silently never happens.
        /// </summary>
        private void DrawWalkableOverlayHint()
        {
            if (_airBase || _overlays.Count == 0)
            {
                return;
            }

            string walkable = FirstWalkableOverlay();
            if (walkable == null)
            {
                return;
            }

            EditorGUILayout.HelpBox(
                "Overlay " + walkable + " is walkable. On a wall base the cell stays solid, so the " +
                "stair/slope does nothing. Real rooms author these as air + overlay: _" + walkable +
                " on its own, or _C" + walkable + " (air + wall backdrop + " + walkable + ").",
                MessageType.Warning);

            if (GUILayout.Button("Use air base"))
            {
                _airBase = true;
                _eraser = false;
                Repaint();
            }
        }

        /// <summary>First selected overlay that needs an air base, or null if none does.</summary>
        private string FirstWalkableOverlay()
        {
            for (int i = 0; i < _overlays.Count; i++)
            {
                TileForm form = ResolveForm(_overlays[i], true);
                if (form != null && IsWalkableOverlay(form))
                {
                    return _overlays[i];
                }
            }

            return null;
        }

        // ---------------------------------------------------------------- overlay stack

        /// <summary>
        /// Click an overlay entry to add it, click again (or click its chip) to remove it. Several
        /// overlays can be active at once because that is what the authored data does: the
        /// dominant walkable stair in the shipped rooms is air + wall backdrop + stair (_CА, 187
        /// uses; _RА 175; _IА 149), which a single-overlay model simply cannot express.
        /// </summary>
        private void ToggleOverlay(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return;
            }

            if (_overlays.Remove(id))
            {
                return;
            }

            // Four is the most any real room uses (e.g. _CА- plus water). Past that the code
            // string stops looking like something a human wrote, so refuse rather than pile on.
            if (_overlays.Count >= MaxOverlays)
            {
                SetStatus("Overlay limit reached (" + MaxOverlays + ") — remove one first.");
                return;
            }

            _overlays.Add(id);
        }

        private const int MaxOverlays = 4;

        /// <summary>
        /// Chips for the active overlays, shown in the order BuildCode will emit them so the row
        /// reads like the resulting code. Clicking a chip removes that overlay.
        /// </summary>
        private void DrawOverlayChips()
        {
            if (_overlays.Count == 0)
            {
                EditorGUILayout.LabelField("(none — the base alone)", EditorStyles.miniLabel);
                return;
            }

            SortOverlays();

            string remove = null;

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("stack:", EditorStyles.miniLabel, GUILayout.Width(34f));

            for (int i = 0; i < _overlayOrder.Count; i++)
            {
                string id = _overlayOrder[i];
                if (GUILayout.Button(id + " ×", EditorStyles.miniButton, GUILayout.Width(38f)))
                {
                    remove = id;
                }
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            // Removal is deferred: mutating the list mid-layout would change the control count
            // between the Layout and Repaint passes and trip Unity's group-mismatch error.
            if (remove != null)
            {
                _overlays.Remove(remove);
                Repaint();
            }
        }

        /// <summary>
        /// Fills _overlayOrder with _overlays sorted the way AS3-authored rooms write them:
        /// backgrounds (ed 2) first, then stairs (ed 3), then slopes and shelves (ed 4). The
        /// decoder does not care about order — every char after the first is applied
        /// independently — but this is what makes editor output match hand-written codes like
        /// _CА, _CАК and _RБИ instead of emitting _АC and looking foreign in a diff.
        /// </summary>
        private void SortOverlays()
        {
            _overlayOrder.Clear();
            _overlayOrder.AddRange(_overlays);
            _overlayOrder.Sort(CompareOverlayEd);
        }

        private int CompareOverlayEd(string a, string b)
        {
            int ea = OverlaySortKey(a);
            int eb = OverlaySortKey(b);
            return ea != eb ? ea.CompareTo(eb) : string.CompareOrdinal(a, b);
        }

        /// <summary>Unknown forms sort last so a stale id never displaces a real background.</summary>
        private int OverlaySortKey(string id)
        {
            TileForm form = ResolveForm(id, true);
            return form != null ? form.ed : 99;
        }

        /// <summary>
        /// ed 3 is always a stair; ed 4 is a shelf (one-way platform) or a slope. All of them are
        /// authored on an air base in the shipped rooms, so all of them warrant the hint.
        /// NOTE: `stair` and `diagon` are ints (-1/0/1 = desc-none-asc, left-none-right), NOT
        /// bools — only `shelf` is a bool. Comparing an int field directly against || is CS0019.
        /// </summary>
        private static bool IsWalkableOverlay(TileForm form)
        {
            return form.ed == 3
                || form.ed == 4
                || form.stair != 0
                || form.diagon != 0
                || form.shelf;
        }

        /// <summary>
        /// ed 0 forms (the multi-character "100" border artwork) exist in the database but are
        /// unreachable from a tile code: TileDecoder matches overlay characters ONE AT A TIME
        /// from index 1, so an id longer than one character can never be produced. Warn rather
        /// than let the user paint a palette entry that silently decodes to air.
        /// </summary>
        private void DrawOverlayValidityHint()
        {
            for (int i = 0; i < _overlays.Count; i++)
            {
                string id = _overlays[i];
                TileForm form = ResolveForm(id, true);

                if (form == null)
                {
                    EditorGUILayout.HelpBox(
                        "Overlay " + id + " is not in the form database; it will decode to nothing.",
                        MessageType.Warning);
                    continue;
                }

                if (form.id.Length > 1 || form.ed == 0)
                {
                    EditorGUILayout.HelpBox(
                        "Overlay " + id + " (ed=" + form.ed + ") cannot be produced by a tile code — " +
                        "overlays resolve one character at a time, so only single-character overlays " +
                        "ever match.",
                        MessageType.Warning);
                }
            }
        }

        /// <summary>
        /// Warns about overlay combinations that decode fine but mean something other than they
        /// look.
        ///
        /// Measured across all 557 shipped rooms, the ONLY physics-overlay combinations ever used
        /// are: shelf alone (17608 tiles), stair alone (5334), slope alone (2398), shelf+stair
        /// (367), slope+stair (2). Shelf+slope appears ZERO times.
        ///
        /// That matters because TileDecoder.ApplyForm gives a shelf Platform physics, and a slope
        /// only records slopeType — it never promotes anything. So "_" + shelf + slope is a catwalk
        /// carrying a slope direction, and it merely LOOKS like a stair because PFE draws its slope
        /// sprites as steps. The same trap applies to stairs: `stair` only promotes Air -> Stair, so
        /// adding a stair to a shelf or a wall silently leaves the physics alone.
        /// </summary>
        private void DrawOverlayComboHint()
        {
            if (_overlays.Count < 2)
            {
                return;
            }

            bool hasShelf = false;
            bool hasSlope = false;
            bool hasStair = false;

            for (int i = 0; i < _overlays.Count; i++)
            {
                TileForm form = ResolveForm(_overlays[i], true);
                if (form == null)
                {
                    continue;
                }

                if (form.stair != 0) hasStair = true;
                if (form.diagon != 0) hasSlope = true;
                if (form.shelf) hasShelf = true;
            }

            if (hasShelf && hasSlope)
            {
                EditorGUILayout.HelpBox(
                    "Shelf + slope: no shipped room uses this (0 of 557). The shelf wins the physics " +
                    "(Platform) and the slope only records a direction, so this is a catwalk that " +
                    "merely looks like a stair. Pick one or the other.",
                    MessageType.Warning);
            }
            else if (hasStair && hasSlope)
            {
                EditorGUILayout.HelpBox(
                    "Stair + slope: only 2 tiles out of 557 rooms use this. The stair sets Stair " +
                    "physics; the slope only records a direction.",
                    MessageType.Info);
            }
            else if (hasStair && hasShelf)
            {
                EditorGUILayout.HelpBox(
                    "Stair + shelf: 367 tiles use this — a one-way catwalk that also carries a stair " +
                    "direction. The shelf wins the physics, so the cell stays a Platform, not a Stair.",
                    MessageType.Info);
            }
        }

        /// <summary>
        /// Resets the brush to a bare air tile (code "_"): clears the overlay stack, water and
        /// zForm and switches the base to air. Does not touch the loaded room.
        /// </summary>
        private void SetBrushToAir()
        {
            _eraser = false;
            _airBase = true;
            _overlays.Clear();
            _water = false;
            _zForm = 0;

            SetStatus("Brush set to plain air (_).");
            Repaint();
        }

        /// <summary>
        /// Wrapped grid of palette entries. Entries draw as art when _images is on and as
        /// their code letter otherwise. A header is emitted whenever the AS3 ed category
        /// changes, which keeps walls / border / beams / stairs / slopes / backgrounds
        /// visually grouped instead of jumbled into one ordinal-sorted alphabet.
        /// </summary>
        private void DrawFormGrid(List<string> ids, string selected, bool overlay, Action<string> onPick)
        {
            _singleSelection.Clear();

            if (!string.IsNullOrEmpty(selected))
            {
                _singleSelection.Add(selected);
            }

            DrawFormGrid(ids, _singleSelection, overlay, onPick);
        }

        /// <summary>
        /// Multi-select overload, used by the overlay grid: a tile carries a stack of overlay
        /// chars, so selection is a set rather than a single id.
        ///
        /// The whole grid is reserved as ONE layout entry and every header and cell is then placed
        /// at an absolute offset inside it. Two earlier attempts went through GUILayout per row:
        /// the first let the button stretch to fill the row (so the size setting did nothing and
        /// the cells came out as wide short rectangles), and the second reserved a fixed row rect,
        /// which the vertical group still negotiated — leaving the grid shifted sideways with its
        /// first column clipped out of the scroll view. One block, absolute children, no negotiation.
        /// </summary>
        private void DrawFormGrid(List<string> ids, List<string> selected, bool overlay, Action<string> onPick)
        {
            if (ids == null || ids.Count == 0)
            {
                EditorGUILayout.LabelField("(none loaded)", EditorStyles.miniLabel);
                return;
            }

            float requested = Mathf.Clamp(_images ? _thumbSize : _letterButtonSize, 16f, 128f);
            const float gap = 4f;
            const float headerHeight = 16f;
            const float MinCell = 16f;

            // Cell SIZE wins over the column count. Dragging the thumbnail slider up is an explicit
            // "I want bigger tiles" request, so the grid drops columns to honour it rather than
            // silently shrinking the cells back down. Only a cell that cannot fit even one per row
            // is shrunk, and only to the legible floor.
            //
            // The budget is the panel width MINUS what the scroll view and its scrollbar eat.
            float available = Mathf.Max(MinCell, _resolvedPanelWidth - 34f);
            int columns = Mathf.Clamp(_paletteColumns, 1, 12);
            int fitColumns = Mathf.Max(1, Mathf.FloorToInt((available + gap) / (requested + gap)));
            columns = Mathf.Max(1, Mathf.Min(columns, fitColumns));

            float size = Mathf.Min(requested, (available - (columns - 1) * gap) / columns);
            size = Mathf.Clamp(size, MinCell, 128f);

            float rowWidth = columns * size + (columns - 1) * gap;

            // Report the resolved geometry so the size slider visibly does something.
            _lastPaletteColumns = columns;
            _lastPaletteCellSize = size;

            // --- plan: collapse the id list into contiguous ed runs ---
            _gridGroups.Clear();
            int i = 0;
            while (i < ids.Count)
            {
                int ed = EdOf(ids[i], overlay);
                int runEnd = i;
                while (runEnd < ids.Count && EdOf(ids[runEnd], overlay) == ed)
                {
                    runEnd++;
                }

                _gridGroups.Add(new GridGroup { Ed = ed, Start = i, Count = runEnd - i });
                i = runEnd;
            }

            float blockHeight = 0f;
            for (int g = 0; g < _gridGroups.Count; g++)
            {
                int rows = Mathf.CeilToInt(_gridGroups[g].Count / (float)columns);
                blockHeight += headerHeight + gap + rows * size + (rows - 1) * gap + gap;
            }

            // min == max on both axes: a fixed slot that nothing in the layout pass can stretch,
            // shrink or shift.
            Rect block = GUILayoutUtility.GetRect(rowWidth, rowWidth, blockHeight, blockHeight);

            float y = block.y;

            for (int g = 0; g < _gridGroups.Count; g++)
            {
                GridGroup group = _gridGroups[g];

                GUI.Label(
                    new Rect(block.x, y, rowWidth, headerHeight),
                    EdGroupLabel(group.Ed),
                    EditorStyles.miniBoldLabel);

                y += headerHeight + gap;

                int rows = Mathf.CeilToInt(group.Count / (float)columns);
                for (int r = 0; r < rows; r++)
                {
                    int rowStart = group.Start + r * columns;
                    int rowCount = Mathf.Min(columns, group.Start + group.Count - rowStart);

                    for (int c = 0; c < rowCount; c++)
                    {
                        string id = ids[rowStart + c];
                        Rect cell = new Rect(block.x + c * (size + gap), y, size, size);
                        DrawFormCell(
                            id,
                            ResolveForm(id, overlay),
                            selected != null && selected.Contains(id),
                            cell,
                            onPick);
                    }

                    y += size + gap;
                }

                y += gap;
            }
        }

        /// <summary>ed category of a palette id, or -1 when the id is not in the database.</summary>
        private int EdOf(string id, bool overlay)
        {
            TileForm form = ResolveForm(id, overlay);
            return form != null ? form.ed : -1;
        }

        /// <summary>
        /// Draws one palette cell into an already-reserved absolute rect. The rect comes from the
        /// caller rather than from GUILayout, so the cell is exactly the requested size.
        /// </summary>
        private void DrawFormCell(string id, TileForm form, bool isSelected, Rect cell, Action<string> onPick)
        {
            GUIStyle style = isSelected ? EditorStyles.miniButtonMid : EditorStyles.miniButton;
            GUIContent content = new GUIContent(string.Empty, FormTooltip(id, form));

            // The button owns the click and the tooltip; the art is painted over it afterwards,
            // because GUI.Button cannot render an arbitrary texture.
            if (GUI.Button(cell, content, style))
            {
                onPick(id);
                Repaint();
            }

            // Report this cell as hovered so the delayed tooltip can be armed at the end of OnGUI.
            // Deliberately before the !_images early-out below, so hovering in letter mode works too.
            if (cell.Contains(Event.current.mousePosition))
            {
                _hoveredFormId = id;
                _hoveredForm = form;
            }

            Rect inner = new Rect(cell.x + 2f, cell.y + 2f, cell.width - 4f, cell.height - 4f);

            if (!_images)
            {
                GUI.Label(inner, id, CenteredThumbStyle(cell.height));
                return;
            }

            TileThumb thumb = ResolveFormThumb(form);

            if (thumb.IsValid)
            {
                // Dark plate so light artwork stays legible against the button gradient.
                EditorGUI.DrawRect(inner, new Color(0f, 0f, 0f, 0.32f));
                DrawThumb(inner, thumb, Color.white);
            }
            else
            {
                GUI.Label(inner, id, CenteredThumbStyle(cell.height));
            }
        }

        /// <summary>
        /// Centred fallback label style. The font scales with the cell so letter mode stays legible
        /// when the user enlarges the buttons; pass 0 to keep the fixed small size (preview box).
        /// </summary>
        private GUIStyle CenteredThumbStyle(float cellSize = 0f)
        {
            if (_thumbFallbackStyle == null)
            {
                _thumbFallbackStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    alignment = TextAnchor.MiddleCenter,
                    clipping = TextClipping.Clip
                };
            }

            _thumbFallbackStyle.fontSize = cellSize > 0f
                ? Mathf.Clamp(Mathf.RoundToInt(cellSize * 0.42f), 8, 22)
                : 9;

            _thumbFallbackStyle.normal.textColor = new Color(0.85f, 0.85f, 0.85f);
            return _thumbFallbackStyle;
        }

        private static void DrawThumb(Rect rect, TileThumb thumb, Color tint)
        {
            if (!thumb.IsValid)
            {
                return;
            }

            Color previous = GUI.color;
            GUI.color = tint;
            GUI.DrawTextureWithTexCoords(rect, thumb.Texture, thumb.Uv, true);
            GUI.color = previous;
        }

        private TileForm ResolveForm(string id, bool overlay)
        {
            if (_formDb == null || string.IsNullOrEmpty(id))
            {
                return null;
            }

            return overlay ? _formDb.GetOForm(id) : _formDb.GetFForm(id);
        }

        // ---------------------------------------------------------------- thumbnails

        private TileThumb ResolveFormThumb(TileForm form)
        {
            if (form == null)
            {
                return default;
            }

            // Misses are cached too: TileAssetDatabase.GetSprite walks its entry list linearly,
            // so re-resolving the ~40 forms that legitimately have no art on every repaint
            // would cost tens of thousands of string comparisons per frame.
            string key = form.id + "|" + form.ed;
            if (_thumbCache.TryGetValue(key, out TileThumb cached))
            {
                return cached;
            }

            TileThumb thumb = BuildFormThumb(form);
            _thumbCache[key] = thumb;
            return thumb;
        }

        private TileThumb BuildFormThumb(TileForm form)
        {
            EnsureRenderAssets();

            // 1) Art referenced by visual id. Stairs, slopes and diagonals keep their graphics
            //    in the tile sprite atlas rather than in a material, so vid wins when present.
            if (form.vid > 0 && _tileAssets != null)
            {
                Sprite sprite = _tileAssets.GetSpriteByVisualId(form.vid);
                if (sprite != null)
                {
                    return TileThumb.FromSprite(sprite, "tile_" + form.vid);
                }
            }

            // 2) Material texture. ed==2 forms are backgrounds and live in the BACK material
            //    list; everything else is looked up in the FRONT list. Both lists contain the
            //    ids A..Z, so picking the wrong one silently returns the wrong art.
            if (!string.IsNullOrEmpty(form.front) && _materialDb != null)
            {
                MaterialRenderEntry entry = form.ed == 2
                    ? _materialDb.GetBackMaterial(form.front)
                    : _materialDb.GetFrontMaterial(form.front);

                Texture2D texture = FirstTexture(entry, out string sourceName);
                if (texture != null)
                {
                    return TileThumb.FromTexture(texture, sourceName);
                }
            }

            // 3) The same physics placeholder the runtime falls back to.
            if (_tileAssets != null)
            {
                Sprite placeholder = _tileAssets.GetDefaultSprite(FormPhysicsType(form));
                if (placeholder != null)
                {
                    return TileThumb.FromSprite(placeholder, "(placeholder)");
                }
            }

            return default;
        }

        private Texture2D FirstTexture(MaterialRenderEntry entry, out string sourceName)
        {
            sourceName = null;

            if (entry == null || _textureLookup == null)
            {
                return null;
            }

            Texture2D texture = _textureLookup.GetTexture(entry.mainTexture);
            if (texture != null) { sourceName = entry.mainTexture; return texture; }

            texture = _textureLookup.GetTexture(entry.altTexture);
            if (texture != null) { sourceName = entry.altTexture; return texture; }

            texture = _textureLookup.GetTexture(entry.borderTexture);
            if (texture != null) { sourceName = entry.borderTexture; return texture; }

            texture = _textureLookup.GetTexture(entry.floorTexture);
            if (texture != null) { sourceName = entry.floorTexture; }

            return texture;
        }

        // ---------------------------------------------------------------- live preview

        private void DrawPreviewSection()
        {
            EditorGUILayout.LabelField("Live preview", EditorStyles.boldLabel);

            TileForm baseForm = ResolveForm(_baseForm, false);

            PreviewKontur(out int k1, out int k2, out int k3, out int k4);

            // Same one-mechanism rule as DrawFormCell: options only, no fixed-size overload.
            float box = Mathf.Clamp(_previewSize, 48f, 260f);
            Rect frame = GUILayoutUtility.GetRect(
                GUIContent.none,
                GUIStyle.none,
                GUILayout.Width(box),
                GUILayout.Height(box));
            EditorGUI.DrawRect(frame, new Color(0.09f, 0.09f, 0.10f));

            if (_eraser)
            {
                GUI.Label(frame, "air  _", CenteredThumbStyle());
            }
            else if (_airBase)
            {
                // Air base: the overlays ARE the tile. Without this branch the preview would keep
                // showing the last wall form and claim a stair is solid.
                if (!DrawOverlayStack(frame))
                {
                    GUI.Label(frame, "air  _", CenteredThumbStyle());
                }
            }
            else if (baseForm != null)
            {
                // Bottom to top, mirroring TileRenderer.ApplySorting:
                //   back -> rear overlays -> main -> front overlays.
                if (baseForm.ed == 2)
                {
                    // Background forms put their art on the BACK layer, never the main one.
                    DrawThumb(frame, ResolveFormThumb(baseForm), Color.white);
                }
                else
                {
                    if (!string.IsNullOrEmpty(baseForm.back))
                    {
                        DrawThumb(frame, ResolveBackThumb(baseForm.back), Color.white);
                    }

                    DrawThumb(frame, ResolveMainThumb(baseForm, k1, k2, k3, k4), Color.white);
                }

                DrawOverlayStack(frame);
            }

            DrawRectOutline(frame, new Color(0f, 0f, 0f, 0.6f), 1f);

            EditorGUILayout.LabelField(
                _livePreview ? "composed by TileCompositor" : "raw textures (Live preview off)",
                EditorStyles.miniLabel);

            EditorGUILayout.LabelField(
                "kontur " + k1 + " " + k2 + " " + k3 + " " + k4 +
                (_hoverRow >= 0 ? "  (from hovered cell)" : "  (interior)"),
                EditorStyles.miniLabel);

            if (_water)
            {
                EditorGUILayout.LabelField("* water — not simulated in this preview", EditorStyles.miniLabel);
            }

            if (_zForm > 0)
            {
                EditorGUILayout.LabelField("zForm " + _zForm + " — height offset not drawn here", EditorStyles.miniLabel);
            }
        }

        /// <summary>
        /// Draws every selected overlay in emission order: backgrounds (ed 2) first, then stairs
        /// and slopes/shelves on top. That is the same order BuildCode writes and the same order
        /// TileRenderer stacks them (back layer underneath the front layer). Returns true if at
        /// least one overlay actually resolved to drawable art.
        /// </summary>
        private bool DrawOverlayStack(Rect frame)
        {
            if (_overlays.Count == 0)
            {
                return false;
            }

            SortOverlays();

            bool drew = false;

            for (int i = 0; i < _overlayOrder.Count; i++)
            {
                TileForm form = ResolveForm(_overlayOrder[i], true);
                if (form == null)
                {
                    continue;
                }

                TileThumb thumb = ResolveFormThumb(form);
                if (!thumb.IsValid)
                {
                    continue;
                }

                DrawThumb(frame, thumb, Color.white);
                drew = true;
            }

            return drew;
        }

        /// <summary>
        /// Hovering the grid makes the preview show that cell's real edge shape. Away from the
        /// grid we fall back to 0,0,0,0 = fully surrounded = a plain interior fill.
        /// </summary>
        private void PreviewKontur(out int k1, out int k2, out int k3, out int k4)
        {
            TileData hovered = (_hoverRow >= 0 && _hoverCol >= 0) ? GetDerived(_hoverRow, _hoverCol) : null;

            if (hovered != null)
            {
                k1 = hovered.kontur1;
                k2 = hovered.kontur2;
                k3 = hovered.kontur3;
                k4 = hovered.kontur4;
                return;
            }

            k1 = 0;
            k2 = 0;
            k3 = 0;
            k4 = 0;
        }

        private TileThumb ResolveMainThumb(TileForm form, int k1, int k2, int k3, int k4)
        {
            if (_livePreview)
            {
                Sprite composed = ComposeFrontSprite(form, k1, k2, k3, k4);
                if (composed != null)
                {
                    return TileThumb.FromSprite(composed, form.front + " (composed)");
                }
            }

            return ResolveFormThumb(form);
        }

        private TileThumb ResolveBackThumb(string backGraphic)
        {
            EnsureRenderAssets();

            if (_materialDb == null)
            {
                return default;
            }

            // `back` names a FRONT-list material being used as the backing graphic.
            MaterialRenderEntry entry = _materialDb.GetFrontMaterial(backGraphic);
            Texture2D texture = FirstTexture(entry, out string sourceName);
            return TileThumb.FromTexture(texture, sourceName ?? backGraphic);
        }

        private Sprite ComposeFrontSprite(TileForm form, int k1, int k2, int k3, int k4)
        {
            if (form == null || string.IsNullOrEmpty(form.front))
            {
                return null;
            }

            EnsureCompositor();

            if (_compositor == null)
            {
                return null;
            }

            // The compositor keys its cache on the sample position because the tiling phase
            // depends on it. A fixed origin is fine for a single swatch and keeps the cache at
            // one entry per kontur combination instead of one per grid cell.
            return _compositor.GetFrontTileSprite(form.front, 0, 0, k1, k2, k3, k4);
        }

        // ---------------------------------------------------------------- settings

        private void DrawSettingsTab()
        {
            EditorGUILayout.LabelField("Window settings", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Saved with the editor layout, so these survive a recompile and a restart.",
                EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Sizes", EditorStyles.boldLabel);

            _sidePanelWidth = EditorGUILayout.Slider("Side panel width", _sidePanelWidth, 240f, 560f);
            _paletteColumns = EditorGUILayout.IntSlider("Palette columns (max)", _paletteColumns, 2, 12);
            _thumbSize = EditorGUILayout.Slider("Tile thumbnail", _thumbSize, 24f, 128f);
            _letterButtonSize = EditorGUILayout.Slider("Letter button", _letterButtonSize, 24f, 128f);
            _previewSize = EditorGUILayout.Slider("Preview box", _previewSize, 48f, 200f);

            EditorGUILayout.LabelField(
                "Tile size wins over columns: raising it drops columns so the cells actually grow, " +
                "instead of overflowing the panel and getting clipped. Widen the side panel if you " +
                "want big cells AND many per row.",
                EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Show", EditorStyles.boldLabel);
            _images = EditorGUILayout.ToggleLeft("Palette art (off = code letters)", _images);
            _livePreview = EditorGUILayout.ToggleLeft("Live preview via TileCompositor", _livePreview);
            _showHelp = EditorGUILayout.ToggleLeft("Help section", _showHelp);
            _gridLines = EditorGUILayout.ToggleLeft("Grid lines", _gridLines);
            _gridArt = EditorGUILayout.ToggleLeft(
                "Overlay art in grid (stairs, slopes, shelves)", _gridArt);
            _hoverHighlight = EditorGUILayout.ToggleLeft("Hover highlight", _hoverHighlight);
            _alwaysShowCodes = EditorGUILayout.ToggleLeft(
                "Tile codes in grid (ignore zoom threshold)", _alwaysShowCodes);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Input", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Pan is middle-mouse drag. Left-drag paints. Wheel zooms.",
                EditorStyles.wordWrappedMiniLabel);

            // Explicit direction rather than an "invert" checkbox: Unity's scroll delta sign varies
            // by platform and device, so the only useful thing to expose is which way is which.
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Wheel up", EditorStyles.miniLabel, GUILayout.Width(56f));

            bool clickedZoomIn = GUILayout.Toggle(
                _wheelUpZoomsIn,
                "Zoom in",
                EditorStyles.miniButtonLeft,
                GUILayout.Width(72f)) != _wheelUpZoomsIn;

            bool clickedZoomOut = GUILayout.Toggle(
                !_wheelUpZoomsIn,
                "Zoom out",
                EditorStyles.miniButtonRight,
                GUILayout.Width(78f)) == _wheelUpZoomsIn;

            EditorGUILayout.EndHorizontal();

            if (clickedZoomIn)
            {
                _wheelUpZoomsIn = true;
            }
            else if (clickedZoomOut)
            {
                _wheelUpZoomsIn = false;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Painting", EditorStyles.boldLabel);
            _airBase = EditorGUILayout.ToggleLeft("Air base — overlay-only tiles (e.g. _А)", _airBase);

            if (_airBase)
            {
                EditorGUILayout.LabelField(
                    "The overlay carries the whole tile. Use this for stairs, slopes, shelves " +
                    "and water; a wall base would make them solid.",
                    EditorStyles.wordWrappedMiniLabel);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Tooltip (hover a palette tile)", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Pick which lines the hover tooltip shows. The name and kind are the title; the " +
                "rest are optional so the tooltip can be trimmed to what you actually read.",
                EditorStyles.wordWrappedMiniLabel);

            _tipName = EditorGUILayout.ToggleLeft("Name (the code character)", _tipName);
            _tipKind = EditorGUILayout.ToggleLeft("What it is (wall / stair / slope / background)", _tipKind);
            _tipPhysics = EditorGUILayout.ToggleLeft("Physics (solid, one-way, slope direction)", _tipPhysics);
            _tipDurability = EditorGUILayout.ToggleLeft("Durability (hp, threshold, indestructible)", _tipDurability);
            _tipMaterial = EditorGUILayout.ToggleLeft("Material (AS3 debris index + MaterialType)", _tipMaterial);
            _tipGraphics = EditorGUILayout.ToggleLeft("Graphics (ed, vid, front)", _tipGraphics);
            _tipFlags = EditorGUILayout.ToggleLeft("Flags (rear, shelf, lurk, mirror)", _tipFlags);
            _tipUsageInRoom = EditorGUILayout.ToggleLeft("Usage in this room (cell count)", _tipUsageInRoom);

            // No biome field exists anywhere in the project, so say so rather than inventing one.
            EditorGUILayout.LabelField(
                "Biome is not tile data — it is not a field on TileForm and nothing in the " +
                "project has one. The nearest equivalent is the ROOM's environment " +
                "(colorScheme, backgroundWall, hasSky), which is a property of the room, not of " +
                "a tile. Not shown here until there is something real to show.",
                EditorStyles.wordWrappedMiniLabel);

            // Live sample of the configured tooltip so the toggles can be judged without hovering.
            // Falls back to the first overlay chip, then to the base, so the sample is never empty
            // just because the base is air (which has no form of its own).
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Preview", EditorStyles.miniBoldLabel);

            string sampleId = _hoveredFormId;

            if (string.IsNullOrEmpty(sampleId) && _overlayOrder.Count > 0)
            {
                sampleId = _overlayOrder[0];
            }

            if (string.IsNullOrEmpty(sampleId) && !string.IsNullOrEmpty(_baseForm))
            {
                sampleId = _baseForm;
            }

            TileForm sampleForm = null;
            if (!string.IsNullOrEmpty(sampleId))
            {
                sampleForm = ResolveForm(sampleId, false) ?? ResolveForm(sampleId, true);
            }

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(
                string.IsNullOrEmpty(sampleId)
                    ? "Hover a palette tile (or select a base/overlay) to see the tooltip here."
                    : FormTooltip(sampleId, sampleForm),
                EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space();
            if (GUILayout.Button("Reset to defaults"))
            {
                ResetSettings();
            }
        }

        private void ResetSettings()
        {
            _sidePanelWidth = 310f;
            _paletteColumns = 4;
            _thumbSize = 56f;
            _letterButtonSize = 48f;
            _previewSize = 84f;

            _images = true;
            _livePreview = true;
            _showHelp = true;
            _gridLines = true;
            _gridArt = true;
            _hoverHighlight = true;
            _alwaysShowCodes = false;
            _wheelUpZoomsIn = false;

            _tipName = true;
            _tipKind = true;
            _tipPhysics = true;
            _tipDurability = true;
            _tipMaterial = true;
            _tipGraphics = true;
            _tipFlags = true;
            _tipUsageInRoom = true;

            _airBase = false;

            _settingsVersion = SettingsVersion;

            SetStatus("Room editor settings reset to defaults.");
            Repaint();
        }

        /// <summary>
        /// Pushes new defaults to windows whose layout was saved with an older version. Runs once
        /// per window per version, so it can never fight a value the user set afterwards.
        /// </summary>
        private void EnsureSettingsMigrated()
        {
            if (_settingsVersion >= SettingsVersion)
            {
                return;
            }

            MigrateSettings(_settingsVersion);
            _settingsVersion = SettingsVersion;
            Repaint();
        }

        private void MigrateSettings(int from)
        {
            if (from < 2)
            {
                // v0/1 defaulted to a 38px thumbnail and a 34px letter button in 6 columns. The old
                // layout code ignored both numbers, so the palette showed ~30px cells in 7 columns
                // no matter what — the sizes the user saw were never the sizes they had set. Bigger
                // defaults in fewer columns are what the palette should have looked like all along.
                _thumbSize = 56f;
                _letterButtonSize = 48f;
                _paletteColumns = 4;
            }
        }

        // ---------------------------------------------------------------- help

        private void DrawHelpSection()
        {
            EditorGUILayout.LabelField("What this tile means", EditorStyles.boldLabel);

            EditorGUILayout.LabelField("BASE", EditorStyles.miniBoldLabel);
            EditorGUILayout.LabelField(
                _airBase
                    ? "air  (_)  —  no wall form; the overlay alone defines the tile"
                    : DescribeForm(_baseForm, ResolveForm(_baseForm, false)),
                EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("OVERLAYS", EditorStyles.miniBoldLabel);

            if (_overlays.Count == 0)
            {
                EditorGUILayout.LabelField("(none selected)", EditorStyles.wordWrappedMiniLabel);
            }
            else
            {
                SortOverlays();

                for (int i = 0; i < _overlayOrder.Count; i++)
                {
                    string id = _overlayOrder[i];
                    EditorGUILayout.LabelField(
                        DescribeForm(id, ResolveForm(id, true)),
                        EditorStyles.wordWrappedMiniLabel);
                    EditorGUILayout.Space();
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("CODE FORMAT", EditorStyles.miniBoldLabel);
            EditorGUILayout.LabelField(LegendText, EditorStyles.wordWrappedMiniLabel);
        }

        /// <summary>
        /// Verified against 503 authored rooms and against AS3 Tile.dec: the FIRST character is
        /// only ever looked up in fForms, so a base is always a solid wall (or '_' for air).
        /// Backgrounds, stairs, slopes and shelves are all OVERLAY characters. Overlays are
        /// matched one character at a time, which is why the multi-character id "100" can never
        /// be produced by a tile string.
        /// </summary>
        private const string LegendText =
            "A tile code is one or more characters.\n" +
            "  first char   base — a solid wall form (A..T), or _ for air\n" +
            "  later chars  overlays, matched ONE character at a time:\n" +
            "               backgrounds (A..Z), stairs, slopes, shelves\n" +
            "  _            air (empty)\n" +
            "  *            water\n" +
            "  , ; :        zForm 1 / 2 / 3 — partial-height variants\n\n" +
            "Stairs are А (up) and Б (down). Slopes and shelves are В..Т and -.\n" +
            "They only work on an AIR base — all 5703 stair tiles in the shipped rooms start\n" +
            "with _ — and are usually air + wall backdrop + stair: _А, _CА, _RБ, _CА-.\n" +
            "Rooms are 48 tiles wide, 25 rows, row 0 at the TOP.";

        private string DescribeForm(string id, TileForm form)
        {
            if (form == null)
            {
                return id + " — not in TileFormDatabase";
            }

            StringBuilder sb = new StringBuilder();

            string name = FormDisplayName(form);
            sb.Append(id);
            if (!string.IsNullOrEmpty(name))
            {
                sb.Append("  ·  ").Append(name);
            }

            sb.Append('\n');
            sb.Append("kind        ").Append(FormKind(form)).Append('\n');
            sb.Append("physics     ").Append(FormPhysicsLabel(form)).Append('\n');

            if (form.mat != 0)
            {
                sb.Append("material    mat=").Append(form.mat)
                  .Append(" — ").Append(As3MaterialLabel(form.mat)).Append('\n');
                sb.Append("            port casts this to MaterialType.")
                  .Append(MaterialTypeName(form.mat)).Append('\n');
            }

            sb.Append("durability  ").Append(
                form.indestruct
                    ? "indestructible"
                    : "hp " + form.hp + ", threshold " + form.thre).Append('\n');

            if (form.lurk > 0)
            {
                sb.Append("conceal     lurk=").Append(form.lurk).Append(" — units can hide here\n");
            }

            if (form.rear)
            {
                sb.Append("layer       rear — drawn behind units\n");
            }

            sb.Append("graphics    ");
            if (!string.IsNullOrEmpty(form.front)) sb.Append("front=").Append(form.front).Append("  ");
            if (!string.IsNullOrEmpty(form.back)) sb.Append("back=").Append(form.back).Append("  ");
            if (form.vid > 0) sb.Append("vid=").Append(form.vid);
            sb.Append('\n');

            TileThumb thumb = ResolveFormThumb(form);
            sb.Append("art         ");
            sb.Append(thumb.IsValid && !string.IsNullOrEmpty(thumb.Source)
                ? thumb.Source
                : "none found (showing physics placeholder)");

            return sb.ToString();
        }

        private string FormDisplayName(TileForm form)
        {
            EnsureRenderAssets();

            if (_materialDb != null)
            {
                MaterialRenderEntry entry = form.ed == 2
                    ? _materialDb.GetBackMaterial(form.front)
                    : _materialDb.GetFrontMaterial(form.front);

                if (entry != null && !string.IsNullOrEmpty(entry.displayName))
                {
                    return entry.displayName;
                }
            }

            // Stairs and diagonals have no material entry; name them by their atlas sprite.
            return form.vid > 0 ? "sprite tile_" + form.vid : "";
        }

        private static string FormKind(TileForm form)
        {
            switch (form.ed)
            {
                case 1:
                    return "solid wall (fForm, ed=1)";
                case 2:
                    return "background texture (ed=2) — overlay char; art goes to the BACK layer";
                case 3:
                    return "stair overlay (ed=3)";
                case 4:
                    return form.shelf
                        ? "shelf / beam (ed=4) — one-way platform"
                        : "slope / diagonal overlay (ed=4)";
                case 0:
                    return "special (ed=0) — border artwork, NOT reachable from a tile code";
                default:
                    return "unknown category (ed=" + form.ed + ")";
            }
        }

        private static string FormPhysicsLabel(TileForm form)
        {
            List<string> parts = new List<string>();

            if (form.phis != 0) parts.Add("solid (phis=" + form.phis + ")");
            if (form.shelf) parts.Add("one-way platform (jump up through from below)");
            if (form.stair != 0) parts.Add(form.stair > 0 ? "stairs, ascending" : "stairs, descending");
            if (form.diagon != 0) parts.Add(form.diagon > 0 ? "slope, up-right" : "slope, up-left");

            return parts.Count == 0 ? "none — walk-through" : string.Join(", ", parts);
        }

        /// <summary>
        /// AS3 material index to its debris effect, read from Grafon.dyra
        /// (scripts/fe/graph/Grafon.as, the param1.mat chain around line 992).
        /// This is NOT the Unity MaterialType enum, even though TileDecoder casts one to
        /// the other: mat 2 is concrete debris but MaterialType.Wood, and mat 5/6 have no
        /// enum value at all.
        /// </summary>
        private static string As3MaterialLabel(int mat)
        {
            switch (mat)
            {
                case 1: return "metal debris";
                case 2: return "generic chunk debris";
                case 3: return "wood splinters";
                case 4: return "heavy chunk debris";
                case 5: return "glass shards";
                case 6: return "dirt / organic debris";
                default: return "unknown";
            }
        }

        private static string MaterialTypeName(int mat)
        {
            return mat >= 0 && mat <= (int)MaterialType.Glass
                ? ((MaterialType)mat).ToString()
                : "INVALID (mat " + mat + " has no MaterialType)";
        }

        private static string EdGroupLabel(int ed)
        {
            switch (ed)
            {
                case 1: return "solid walls";
                case 2: return "backgrounds (back layer)";
                case 3: return "stairs";
                case 4: return "shelves & slopes";
                case 0: return "special / border";
                default: return "unknown (ed=" + ed + ")";
            }
        }

        /// <summary>
        /// Builds the palette hover tooltip. Which line groups appear is user-configurable from
        /// the Settings tab; this is the single place that knows how a TileForm is described, so
        /// the palette and the Settings preview can never drift apart.
        /// </summary>
        private string FormTooltip(string id, TileForm form)
        {
            if (form == null)
            {
                return id + " — not in TileFormDatabase";
            }

            StringBuilder sb = new StringBuilder();

            // Title: the char plus what it is. Always emitted — a tooltip with no title is
            // indistinguishable from a bug.
            if (_tipName)
            {
                sb.Append(id);
            }

            if (_tipKind)
            {
                if (sb.Length > 0)
                {
                    sb.Append(" — ");
                }

                sb.Append(FormKind(form));
            }

            if (_tipPhysics)
            {
                AppendTooltipLine(sb, FormPhysicsLabel(form));
            }

            if (_tipDurability)
            {
                if (form.indestruct)
                {
                    AppendTooltipLine(sb, "indestructible (hp ignored)");
                }
                else if (form.hp > 0)
                {
                    AppendTooltipLine(sb, "hp " + form.hp + ", threshold " + form.thre);
                }
                else
                {
                    AppendTooltipLine(sb, "hp 0 — not damageable");
                }
            }

            if (_tipMaterial && form.mat != 0)
            {
                // Two different scales on purpose. AS3 `mat` is a 1..6 debris index; Unity's
                // MaterialType is 0..4 and TileDecoder casts one straight onto the other, so
                // only mat 1 actually lines up (mat 2 is concrete debris but Wood, and mat 5/6
                // have no enum value at all). Showing both makes that visible instead of hiding it.
                string materialLine = "mat " + form.mat + " (" + As3MaterialLabel(form.mat) + ")";
                if (form.mat >= 0 && form.mat <= (int)MaterialType.Glass)
                {
                    materialLine += "  |  MaterialType." + ((MaterialType)form.mat);
                }
                else
                {
                    materialLine += "  |  no MaterialType — the cast at TileDecoder.cs:203 is lossy";
                }

                AppendTooltipLine(sb, materialLine);
            }

            if (_tipGraphics)
            {
                AppendTooltipLine(sb, "ed " + form.ed + ", vid " + form.vid
                    + (string.IsNullOrEmpty(form.front) ? ", no front graphic" : ", front '" + form.front + "'"));
            }

            if (_tipFlags)
            {
                List<string> flags = new List<string>();
                if (form.rear) flags.Add("rear (drawn behind)");
                if (form.shelf) flags.Add("shelf");
                if (form.lurk != 0) flags.Add("lurk " + form.lurk);
                if (!string.IsNullOrEmpty(form.mirror)) flags.Add("mirrors to '" + form.mirror + "'");
                if (form.ed == 0) flags.Add("UNREACHABLE from a tile code");
                if (flags.Count > 0)
                {
                    AppendTooltipLine(sb, string.Join(", ", flags));
                }
            }

            if (_tipUsageInRoom)
            {
                AppendTooltipLine(sb, "in this room: " + CountFormUsage(id, form) + " cell(s)");
            }

            return sb.Length == 0
                ? id + " (all tooltip fields are disabled in Settings)"
                : sb.ToString();
        }

        private static void AppendTooltipLine(StringBuilder sb, string line)
        {
            if (string.IsNullOrEmpty(line))
            {
                return;
            }

            if (sb.Length > 0)
            {
                sb.Append('\n');
            }

            sb.Append(line);
        }

        /// <summary>
        /// How many cells in the loaded room resolve to this palette entry. Counted from the raw
        /// code strings rather than from _derived so it stays correct while a stroke is mid-flight
        /// and _derived has not been rebuilt yet.
        /// </summary>
        private int CountFormUsage(string id, TileForm form)
        {
            if (_codes == null || form == null || string.IsNullOrEmpty(id))
            {
                return 0;
            }

            char c = id[0];
            int count = 0;

            for (int row = 0; row < GridHeight; row++)
            {
                for (int col = 0; col < GridWidth; col++)
                {
                    string code = _codes[row][col];
                    if (string.IsNullOrEmpty(code))
                    {
                        continue;
                    }

                    // A single-char id is only a BASE when it sits at index 0; every later char is
                    // an overlay. Multi-char ids (the ed=0 "100" form) can never be produced by a
                    // tile string at all, so they always report 0.
                    if (id.Length > 1)
                    {
                        if (string.Equals(code, id, StringComparison.Ordinal))
                        {
                            count++;
                        }

                        continue;
                    }

                    bool overlay = form.ed != 1;
                    int from = overlay ? 1 : 0;

                    for (int i = from; i < code.Length; i++)
                    {
                        if (code[i] == c)
                        {
                            count++;
                            break;
                        }
                    }
                }
            }

            return count;
        }

        /// <summary>
        /// Mirrors TileDecoder.Decode: phis != 0 becomes Wall (Platform when shelf), shelf
        /// alone becomes Platform, and stair only promotes when nothing else claimed the tile.
        /// Only used to pick the right placeholder sprite.
        /// </summary>
        private static TilePhysicsType FormPhysicsType(TileForm form)
        {
            if (form.phis != 0)
            {
                return form.shelf ? TilePhysicsType.Platform : TilePhysicsType.Wall;
            }

            if (form.shelf)
            {
                return TilePhysicsType.Platform;
            }

            return form.stair != 0 ? TilePhysicsType.Stair : TilePhysicsType.Air;
        }

        // ---------------------------------------------------------------- render assets

        private void EnsureRenderAssets()
        {
            if (_renderAssetsLookedUp)
            {
                return;
            }

            _renderAssetsLookedUp = true;

            _tileAssets = FindAsset<TileAssetDatabase>("Assets/_PFE/Data/Map/TileAssetDatabase.asset");
            _textureLookup = FindAsset<TileTextureLookup>("Assets/_PFE/Data/TileTextureLookup.asset");
            _materialDb = FindAsset<MaterialRenderDatabase>("Assets/_PFE/Data/MaterialRenderDatabase.asset");
            _maskLookup = FindAsset<TileMaskLookup>("Assets/_PFE/Data/TileMaskLookup.asset");
        }

        private static T FindAsset<T>(string fallbackPath) where T : UnityEngine.Object
        {
            string[] guids = AssetDatabase.FindAssets("t:" + typeof(T).Name);
            if (guids.Length > 0)
            {
                T asset = AssetDatabase.LoadAssetAtPath<T>(AssetDatabase.GUIDToAssetPath(guids[0]));
                if (asset != null)
                {
                    return asset;
                }
            }

            return AssetDatabase.LoadAssetAtPath<T>(fallbackPath);
        }

        private void EnsureCompositor()
        {
            if (_compositor != null)
            {
                return;
            }

            EnsureRenderAssets();

            if (_textureLookup == null || _materialDb == null)
            {
                return;
            }

            _compositor = new TileCompositor(_textureLookup, _materialDb, _maskLookup);
        }

        // ---------------------------------------------------------------- grid / viewport

        private void DrawGridArea()
        {
            EditorGUILayout.BeginVertical();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Grid (row 0 = top)", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();

            using (new EditorGUI.DisabledScope(_codes == null))
            {
                if (GUILayout.Button("Fit", EditorStyles.miniButtonLeft, GUILayout.Width(36f)))
                {
                    _viewFramed = false;
                }

                if (GUILayout.Button("-", EditorStyles.miniButtonMid, GUILayout.Width(22f)))
                {
                    ZoomAtViewportCentre(1f / 1.25f);
                }

                EditorGUILayout.LabelField(_zoom.ToString("0.#"), EditorStyles.miniLabel, GUILayout.Width(30f));

                if (GUILayout.Button("+", EditorStyles.miniButtonRight, GUILayout.Width(22f)))
                {
                    ZoomAtViewportCentre(1.25f);
                }
            }

            EditorGUILayout.EndHorizontal();

            if (_codes == null)
            {
                EditorGUILayout.HelpBox("Pick a room template to start painting.", MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }

            // The canvas takes all the space left in the window. ExpandWidth/ExpandHeight
            // is what the old scroll view used to provide implicitly.
            _viewRect = GUILayoutUtility.GetRect(
                GUIContent.none,
                GUIStyle.none,
                GUILayout.ExpandWidth(true),
                GUILayout.ExpandHeight(true));

            if (_viewRect.height < 120f)
            {
                // The layout pass occasionally refuses to hand out flexible height (nested
                // groups that size to content, or a very small window). Fall back to the
                // space actually left in the window so the canvas is never a dead sliver.
                float fallback = Mathf.Max(160f, position.height - _viewRect.y - 46f);
                _viewRect = new Rect(_viewRect.x, _viewRect.y, _viewRect.width, fallback);
            }

            // Same story for width. The very first layout pass can report a sliver before the
            // window has been given a real size, and fitting a 48x25 room into a sliver clamps the
            // zoom straight to MinZoom and latches it — which is why the map used to open as a
            // speck that had to be zoomed by hand.
            if (_viewRect.width < 120f)
            {
                float fallbackWidth = Mathf.Max(200f, position.width - _resolvedPanelWidth - 30f);
                _viewRect = new Rect(_viewRect.x, _viewRect.y, fallbackWidth, _viewRect.height);
            }

            if (_viewRect.width < 1f || _viewRect.height < 1f)
            {
                EditorGUILayout.EndVertical();
                return;
            }

            if (!_viewFramed)
            {
                FrameGrid();
            }
            else
            {
                // Re-clamp every frame so resizing the window smaller cannot leave the grid
                // stranded outside the new viewport. Idempotent when already in range.
                ClampPan();
            }

            DrawCanvas();

            EditorGUILayout.EndVertical();
        }

        /// <summary>Zoom so the whole room fits the viewport, then centre it.</summary>
        private void FrameGrid()
        {
            const float margin = 14f;

            // Refuse to frame against a degenerate viewport. The first layout pass can report a
            // tiny rect before the window has been sized; fitting 48x25 into that clamps to
            // MinZoom and latches, leaving the room a speck until the user zooms by hand. Leaving
            // _viewFramed false makes this retry on the next pass.
            if (_viewRect.width < 120f || _viewRect.height < 120f)
            {
                return;
            }

            float availableWidth = Mathf.Max(1f, _viewRect.width - margin * 2f);
            float availableHeight = Mathf.Max(1f, _viewRect.height - margin * 2f);

            float fit = Mathf.Min(availableWidth / GridWidth, availableHeight / GridHeight);
            _zoom = Mathf.Clamp(fit, MinZoom, MaxZoom);

            Vector2 roomSize = new Vector2(GridWidth, GridHeight) * _zoom;
            _pan = new Vector2(
                (_viewRect.width - roomSize.x) * 0.5f,
                (_viewRect.height - roomSize.y) * 0.5f);

            _viewFramed = true;
        }

        private void ZoomAtViewportCentre(float factor)
        {
            // _viewRect is from the previous layout pass on the frame the button is clicked;
            // if it is not valid yet, zoom about wherever the mouse is.
            Vector2 centre = _viewRect.width > 1f
                ? new Vector2(_viewRect.width * 0.5f, _viewRect.height * 0.5f)
                : Event.current.mousePosition - _viewRect.position;

            ZoomAt(centre, factor);
        }

        /// <summary>
        /// Scale about a viewport-local point so whatever grid cell sits under that point
        /// stays under it. This is what makes wheel-zoom feel anchored instead of drifting.
        /// </summary>
        private void ZoomAt(Vector2 viewportLocalFocus, float factor)
        {
            float previous = _zoom;
            float next = Mathf.Clamp(previous * factor, MinZoom, MaxZoom);

            if (Mathf.Approximately(next, previous))
            {
                return;
            }

            Vector2 gridUnderFocus = (viewportLocalFocus - _pan) / previous;
            _zoom = next;
            _pan = viewportLocalFocus - gridUnderFocus * next;

            ClampPan();
            Repaint();
        }

        /// <summary>
        /// Keeps a sliver of the room reachable so it can never be panned off-screen.
        /// Without this a fast drag can strand the grid outside the window with no way back.
        /// </summary>
        private void ClampPan()
        {
            const float keepVisible = 48f;
            Vector2 roomSize = new Vector2(GridWidth, GridHeight) * _zoom;

            float minX = -roomSize.x + keepVisible;
            float maxX = _viewRect.width - keepVisible;
            float minY = -roomSize.y + keepVisible;
            float maxY = _viewRect.height - keepVisible;

            // If the keep-visible band is wider than the room plus the viewport the range
            // inverts; centre instead of clamping into an empty interval.
            _pan.x = minX > maxX
                ? (_viewRect.width - roomSize.x) * 0.5f
                : Mathf.Clamp(_pan.x, minX, maxX);

            _pan.y = minY > maxY
                ? (_viewRect.height - roomSize.y) * 0.5f
                : Mathf.Clamp(_pan.y, minY, maxY);
        }

        /// <summary>Window-space point to grid space (1 unit per cell, origin top-left).</summary>
        private Vector2 ScreenToGrid(Vector2 screenPosition)
        {
            Vector2 local = screenPosition - _viewRect.position;
            return (local - _pan) / _zoom;
        }

        private void DrawCanvas()
        {
            EditorGUI.DrawRect(_viewRect, new Color(0.11f, 0.11f, 0.12f));

            // Room backdrop. Cells are drawn one pixel short, so this shows through as the
            // grid lines rather than needing a separate line pass. It is clamped for the same
            // reason the cells are: an unclamped rect would smear over the palette when panned.
            EditorGUI.DrawRect(ClampToViewport(RoomScreenRect()), new Color(0.16f, 0.16f, 0.16f));

            HandleGridInput();

            float cell = _zoom;
            bool drawLabels = _alwaysShowCodes || cell >= LabelZoom;

            // Grid lines are just the seam between cells: each cell is drawn slightly short so
            // the room backdrop shows through. With lines off the cells are drawn marginally
            // over-long instead, which hides the sub-pixel rounding seams between them.
            float inset = _gridLines ? 1f : -0.5f;

            // Overlay art needs enough pixels to be recognisable; below this it is just mush, so
            // the cell stays a flat physics colour.
            bool drawArt = _gridArt && cell >= MinArtZoom;

            if (drawLabels && _cellLabelStyle == null)
            {
                _cellLabelStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 9,
                    clipping = TextClipping.Clip
                };
            }

            for (int row = 0; row < GridHeight; row++)
            {
                float y = _pan.y + row * cell;
                if (y + cell < 0f || y > _viewRect.height)
                {
                    continue;
                }

                for (int col = 0; col < GridWidth; col++)
                {
                    float x = _pan.x + col * cell;
                    if (x + cell < 0f || x > _viewRect.width)
                    {
                        continue;
                    }

                    Rect cellRect = ClampToViewport(new Rect(
                        _viewRect.x + x,
                        _viewRect.y + y,
                        cell - inset,
                        cell - inset));

                    if (cellRect.width <= 0f || cellRect.height <= 0f)
                    {
                        continue;
                    }

                    EditorGUI.DrawRect(cellRect, CellColor(row, col));

                    if (drawArt)
                    {
                        DrawCellOverlayArt(cellRect, _codes[row][col]);
                    }

                    // Labels are drawn at a fixed font size, so only place them when the
                    // whole cell is on screen — a clipped label is just noise.
                    bool fullyVisible = x >= 0f
                        && y >= 0f
                        && x + cell <= _viewRect.width
                        && y + cell <= _viewRect.height;

                    if (drawLabels && fullyVisible)
                    {
                        string code = _codes[row][col];
                        _cellLabelStyle.normal.textColor = CodeTextColor(row, col);
                        EditorGUI.LabelField(cellRect, string.IsNullOrEmpty(code) ? "" : code, _cellLabelStyle);
                    }
                }
            }

            DrawHoverMarker();
            DrawViewportHint();
        }

        /// <summary>Below this zoom an overlay sprite is too small to read, so cells stay flat.</summary>
        private const float MinArtZoom = 10f;

        /// <summary>
        /// Paints a cell's overlay art over its physics colour. Mirrors TileDecoder's overlay pass
        /// exactly: index 0 is the base (looked up in fForms) and is skipped, and every later char
        /// is resolved in oForms one character at a time.
        ///
        /// This is what makes stairs visible while painting. Without it the grid is flat physics
        /// colour plus a code string, and a stair cell looks identical to a wall cell.
        ///
        /// Backgrounds (ed 2) are drawn semi-transparent so the physics colour underneath stays
        /// legible: a background is a backdrop, not a surface, and letting it paint over the
        /// colour would make a walkable cell look solid.
        /// </summary>
        private void DrawCellOverlayArt(Rect cellRect, string code)
        {
            if (_formDb == null || string.IsNullOrEmpty(code) || code.Length < 2)
            {
                return;
            }

            for (int i = 1; i < code.Length; i++)
            {
                if (!_overlayByChar.TryGetValue(code[i], out TileForm form) || form == null)
                {
                    continue;
                }

                TileThumb thumb = ResolveFormThumb(form);
                if (!thumb.IsValid)
                {
                    continue;
                }

                DrawThumb(cellRect, thumb, form.ed == 2 ? new Color(1f, 1f, 1f, 0.55f) : Color.white);
            }
        }

        private void DrawHoverMarker()
        {
            if (!_hoverHighlight || _hoverRow < 0 || _hoverCol < 0)
            {
                return;
            }

            Rect hover = ClampToViewport(new Rect(
                _viewRect.x + _pan.x + _hoverCol * _zoom,
                _viewRect.y + _pan.y + _hoverRow * _zoom,
                _zoom,
                _zoom));

            if (hover.width <= 0f || hover.height <= 0f)
            {
                return;
            }

            DrawRectOutline(hover, Color.yellow, 2f);
        }

        private void DrawViewportHint()
        {
            if (_viewRect.height < 40f)
            {
                return;
            }

            GUI.Label(
                new Rect(_viewRect.x + 6f, _viewRect.yMax - 18f, _viewRect.width - 12f, 16f),
                "LMB drag paints   ·   MMB drag pans   ·   wheel zooms",
                EditorStyles.miniLabel);
        }

        private Rect RoomScreenRect()
        {
            return new Rect(
                _viewRect.x + _pan.x,
                _viewRect.y + _pan.y,
                GridWidth * _zoom,
                GridHeight * _zoom);
        }

        /// <summary>
        /// Intersects a window-space rect with the viewport. GUI drawing is not clipped to the
        /// layout rect, so edge cells would otherwise bleed over the palette and status bar.
        /// </summary>
        private Rect ClampToViewport(Rect rect)
        {
            float xMin = Mathf.Max(rect.xMin, _viewRect.xMin);
            float yMin = Mathf.Max(rect.yMin, _viewRect.yMin);
            float xMax = Mathf.Min(rect.xMax, _viewRect.xMax);
            float yMax = Mathf.Min(rect.yMax, _viewRect.yMax);

            return xMax <= xMin || yMax <= yMin
                ? new Rect(0f, 0f, 0f, 0f)
                : Rect.MinMaxRect(xMin, yMin, xMax, yMax);
        }

        private static void DrawRectOutline(Rect rect, Color color, float thickness)
        {
            float t = Mathf.Min(thickness, Mathf.Min(rect.width, rect.height) * 0.5f);

            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, t), color);
            EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - t, rect.width, t), color);
            EditorGUI.DrawRect(new Rect(rect.x, rect.y + t, t, rect.height - t * 2f), color);
            EditorGUI.DrawRect(new Rect(rect.xMax - t, rect.y + t, t, rect.height - t * 2f), color);
        }

        private Color CellColor(int row, int col)
        {
            TileData tile = GetDerived(row, col);
            if (tile == null)
            {
                return Color.magenta;
            }

            switch (tile.physicsType)
            {
                case TilePhysicsType.Wall:
                    return new Color(0.27f, 0.27f, 0.25f);
                case TilePhysicsType.Platform:
                    return new Color(0.73f, 0.46f, 0.09f);
                case TilePhysicsType.Stair:
                    return new Color(0.22f, 0.54f, 0.87f);
                default:
                    return string.IsNullOrEmpty(tile.GetBackGraphic())
                        ? new Color(0.95f, 0.94f, 0.91f)
                        : new Color(0.78f, 0.88f, 0.86f);
            }
        }

        private Color CodeTextColor(int row, int col)
        {
            TileData tile = GetDerived(row, col);
            if (tile == null)
            {
                return Color.magenta;
            }

            return tile.physicsType == TilePhysicsType.Air
                ? new Color(0.35f, 0.35f, 0.35f)
                : new Color(0.97f, 0.97f, 0.95f);
        }

        private void HandleGridInput()
        {
            Event e = Event.current;
            bool inside = _viewRect.Contains(e.mousePosition);

            if (inside)
            {
                Vector2 grid = ScreenToGrid(e.mousePosition);
                _hoverCol = Mathf.Clamp(Mathf.FloorToInt(grid.x), 0, GridWidth - 1);
                _hoverRow = Mathf.Clamp(Mathf.FloorToInt(grid.y), 0, GridHeight - 1);
            }
            else
            {
                _hoverCol = -1;
                _hoverRow = -1;
            }

            // In-flight gestures own the mouse until they end, and they are checked before the
            // "inside" test on purpose: a pan or a paint stroke is allowed to drag outside the
            // viewport, and the mouse-up that ends it may land anywhere.
            if (_panning)
            {
                if (e.type == EventType.MouseDrag)
                {
                    _pan += e.delta;
                    ClampPan();
                    e.Use();
                    Repaint();
                }
                else if (e.type == EventType.MouseUp)
                {
                    _panning = false;
                    e.Use();
                    Repaint();
                }

                return;
            }

            if (_painting)
            {
                if (e.type == EventType.MouseDrag)
                {
                    if (inside && _formDb != null)
                    {
                        PaintCell(_hoverRow, _hoverCol);
                    }

                    e.Use();
                    Repaint();
                }
                else if (e.type == EventType.MouseUp)
                {
                    EndStroke();
                    e.Use();
                    Repaint();
                }
                else if (e.type == EventType.MouseMove)
                {
                    Repaint();
                }

                return;
            }

            if (e.type == EventType.ScrollWheel && inside)
            {
                // Plain wheel zooms. No modifier: this grid has nothing else to scroll, so
                // requiring Ctrl for the only wheel gesture was needless friction.
                bool wheelUp = e.delta.y > 0f;
                bool zoomIn = _wheelUpZoomsIn ? wheelUp : !wheelUp;

                // ZoomAt multiplies, so >1 zooms in and <1 zooms out.
                ZoomAt(e.mousePosition - _viewRect.position, zoomIn ? 1.1f : 1f / 1.1f);

                e.Use();
                return;
            }

            if (!inside)
            {
                if (e.type == EventType.MouseMove)
                {
                    Repaint();
                }

                return;
            }

            if (e.type == EventType.MouseDown)
            {
                // Middle mouse is the ONLY pan gesture. Left-drag always paints: the old
                // Alt / Shift / Space + LMB pan meant a stray modifier silently turned a paint
                // stroke into a camera drag.
                if (e.button == 2)
                {
                    _panning = true;
                    e.Use();
                    return;
                }

                if (e.button == 0 && _formDb != null)
                {
                    BeginStroke();
                    PaintCell(_hoverRow, _hoverCol);
                    e.Use();
                    Repaint();
                }

                return;
            }

            if (e.type == EventType.MouseMove || e.type == EventType.MouseDrag)
            {
                Repaint();
            }
        }

        private void PaintCell(int row, int col)
        {
            if (_codes == null || row < 0 || row >= GridHeight || col < 0 || col >= GridWidth)
            {
                return;
            }

            string code = BuildCode();
            if (string.Equals(_codes[row][col], code, StringComparison.Ordinal))
            {
                return;
            }

            _codes[row][col] = code;
            _dirty = true;

            // Colours and physics come from _derived, so flag it. OnGUI rebuilds it once on the
            // next layout pass, giving live feedback while the stroke is still being dragged.
            _derivedDirty = true;
        }

        // ---------------------------------------------------------------- stroke / undo

        private void BeginStroke()
        {
            _painting = true;
            if (_template != null && !_strokeRecorded)
            {
                Undo.RecordObject(_template, "Paint room tiles");
                _strokeRecorded = true;
            }
        }

        private void EndStroke()
        {
            _painting = false;

            if (!_dirty)
            {
                return;
            }

            PushToTemplate();
            RebuildDerived();
            _dirty = false;
            _strokeRecorded = false;
            SetStatus("Painted " + _template.name + " (" + BuildCode() + ")");
        }

        private void PushToTemplate()
        {
            if (_template == null || _codes == null)
            {
                return;
            }

            _template.tileDataString = JoinToDataString(_codes);
            EditorUtility.SetDirty(_template);
        }

        // ---------------------------------------------------------------- status bar

        private void DrawStatusBar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

            EditorGUILayout.LabelField(_status, EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();

            if (_derived != null)
            {
                int unresolved = CountUnresolved();
                EditorGUILayout.LabelField(
                    "unresolved=" + unresolved + (_dirty ? "   (unsaved stroke)" : ""),
                    EditorStyles.miniLabel);
            }

            EditorGUILayout.EndHorizontal();
        }

        private int CountUnresolved()
        {
            int count = 0;
            for (int row = 0; row < GridHeight; row++)
            {
                for (int col = 0; col < GridWidth; col++)
                {
                    string code = _codes[row][col];
                    if (string.IsNullOrEmpty(code) || code == AirToken)
                    {
                        continue;
                    }

                    if (_formDb != null && _formDb.GetFForm(code[0].ToString()) == null)
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        private void SetStatus(string message)
        {
            _status = message;
            Repaint();
        }

        // ---------------------------------------------------------------- load / derive

        private void LoadTemplate(RoomTemplate template)
        {
            _template = template;
            _dirty = false;
            _strokeRecorded = false;
            _derived = null;

            if (template == null)
            {
                _codes = null;
                SetStatus("No template loaded.");
                return;
            }

            EnsureFormDatabase();
            _codes = SplitToCodes(template.tileDataString);
            RebuildDerived();
            SetStatus("Loaded " + template.name + " (" + GridWidth + "x" + GridHeight + ")");
        }

        private void RebuildDerived()
        {
            _derived = null;

            if (_codes == null)
            {
                return;
            }

            EnsureFormDatabase();

            if (_formDb == null)
            {
                return;
            }

            // Reuse the exact runtime path so the editor shows what the game would build.
            string[] rows = JoinToDataString(_codes).Split('\n');
            _derived = TileDecoder.ParseRoom(rows, _formDb, false, GridWidth, GridHeight);
        }

        private TileData GetDerived(int row, int col)
        {
            if (_derived == null)
            {
                return null;
            }

            int unityY = GridHeight - 1 - row;
            if (col < 0 || col >= GridWidth || unityY < 0 || unityY >= GridHeight)
            {
                return null;
            }

            return _derived[col, unityY];
        }

        private bool ConfirmDiscard()
        {
            if (!_dirty)
            {
                return true;
            }

            return EditorUtility.DisplayDialog(
                "Discard unsaved edits?",
                "The current room has unsaved paint strokes.",
                "Discard",
                "Keep editing");
        }

        // ---------------------------------------------------------------- assets

        private void EnsureFormDatabase()
        {
            if (_formDbLookedUp)
            {
                return;
            }

            _formDbLookedUp = true;

            string[] guids = AssetDatabase.FindAssets("t:TileFormDatabase");
            if (guids.Length > 0)
            {
                _formDb = AssetDatabase.LoadAssetAtPath<TileFormDatabase>(AssetDatabase.GUIDToAssetPath(guids[0]));
            }

            if (_formDb == null)
            {
                _formDb = AssetDatabase.LoadAssetAtPath<TileFormDatabase>("Assets/_PFE/Data/TileFormDatabase.asset");
            }

            if (_formDb == null)
            {
                return;
            }

            _fFormIds = OrderForms(_formDb.GetAllFFormIds(), false);
            _oFormIds = OrderForms(_formDb.GetAllOFormIds(), true);

            if (_fFormIds.Count > 0 && !_fFormIds.Contains(_baseForm))
            {
                _baseForm = _fFormIds[0];
            }

            // Char -> form lookup for the grid's overlay art pass. Built once here so the draw
            // loop never allocates a 1-char string per character per cell per repaint.
            _overlayByChar.Clear();
            for (int i = 0; i < _oFormIds.Count; i++)
            {
                string id = _oFormIds[i];
                if (string.IsNullOrEmpty(id) || id.Length != 1)
                {
                    continue;
                }

                TileForm form = _formDb.GetOForm(id);
                if (form != null)
                {
                    _overlayByChar[id[0]] = form;
                }
            }

            _thumbCache.Clear();
            EnsureRenderAssets();
        }

        /// <summary>
        /// Groups ids by AS3 ed category, then ordinal within the group. A plain ordinal sort
        /// scatters the palette ("100" first, then Cyrillic, then Latin) and interleaves
        /// backgrounds with the walls that happen to share a letter.
        /// </summary>
        private List<string> OrderForms(List<string> ids, bool overlay)
        {
            List<string> ordered = new List<string>(ids);

            ordered.Sort((a, b) =>
            {
                TileForm formA = ResolveForm(a, overlay);
                TileForm formB = ResolveForm(b, overlay);

                int edA = formA != null ? formA.ed : 99;
                int edB = formB != null ? formB.ed : 99;

                return edA != edB ? edA.CompareTo(edB) : string.CompareOrdinal(a, b);
            });

            return ordered;
        }

        private void EnsureTemplateList()
        {
            // Guard on the flag alone: if there are genuinely zero templates the search
            // would otherwise re-run every repaint.
            if (!_templateListDirty)
            {
                return;
            }

            _templateListDirty = false;
            _templates.Clear();

            string[] guids = AssetDatabase.FindAssets("t:RoomTemplate", new[] { RoomTemplateRoot });
            for (int i = 0; i < guids.Length; i++)
            {
                RoomTemplate template = AssetDatabase.LoadAssetAtPath<RoomTemplate>(AssetDatabase.GUIDToAssetPath(guids[i]));
                if (template != null)
                {
                    _templates.Add(template);
                }
            }

            _templates.Sort((a, b) =>
                string.CompareOrdinal(
                    (a.sourceCollectionId ?? "") + "/" + (a.id ?? ""),
                    (b.sourceCollectionId ?? "") + "/" + (b.id ?? "")));

            _templateOptions = new string[_templates.Count];
            for (int i = 0; i < _templates.Count; i++)
            {
                RoomTemplate t = _templates[i];
                string collection = string.IsNullOrWhiteSpace(t.sourceCollectionId) ? "?" : t.sourceCollectionId;
                string type = string.IsNullOrWhiteSpace(t.type) ? "?" : t.type;
                _templateOptions[i] = collection + "/" + t.id + "  [" + type + "]";
            }

            if (_template != null)
            {
                int index = _templates.IndexOf(_template);
                if (index >= 0)
                {
                    _templateIndex = index;
                }
            }
            else if (_templates.Count > 0 && _codes == null)
            {
                _templateIndex = 0;
                LoadTemplate(_templates[0]);
            }
        }

        private void RefreshScenePreview()
        {
            RoomVisualController controller = FindFirstObjectByType<RoomVisualController>();
            if (controller == null)
            {
                SetStatus("No RoomVisualController in the active scene. Add one (PFE > Map > Create MapRenderer) first.");
                return;
            }

            controller.PreviewTemplate = _template;
            controller.LoadPreviewRoom();
            SetStatus("Scene preview refreshed from " + _template.name + ".");
        }

        // ---------------------------------------------------------------- string model

        /// <summary>
        /// Current palette setting serialized as an authored tile code.
        /// Char order does not matter to TileDecoder.dec() — every char after the first is
        /// handled independently — so overlays and modifiers can be appended in any order.
        /// The FIRST char does matter: it is the only one looked up in fForms, so it must be
        /// either a wall letter or '_'.
        /// </summary>
        private string BuildCode()
        {
            if (_eraser)
            {
                return AirToken;
            }

            StringBuilder sb = new StringBuilder();

            // Air base emits "_" explicitly, and that leading underscore is not decorative.
            // Without it the code would start with the overlay character, and TileDecoder reads
            // the first character ONLY as an fForm — so "А" decodes to nothing at all, whereas
            // "_А" decodes to air + stair. That single character is the difference between a
            // walkable stair and a silently empty tile, and it is why stairs could previously
            // only ever be painted onto a solid wall.
            if (_airBase || string.IsNullOrEmpty(_baseForm))
            {
                sb.Append(AirToken);
            }
            else
            {
                sb.Append(_baseForm);
            }

            AppendOverlays(sb);

            if (_water)
            {
                sb.Append('*');
            }

            if (_zForm == 1) sb.Append(',');
            else if (_zForm == 2) sb.Append(';');
            else if (_zForm == 3) sb.Append(':');

            return sb.Length == 0 ? AirToken : sb.ToString();
        }

        /// <summary>
        /// Appends the selected overlays in AS3-canonical order: backgrounds (ed 2) first, then
        /// stairs (ed 3), then slopes and shelves (ed 4).
        ///
        /// The decoder does not care about order — every char after the first is applied
        /// independently — but this is the order the shipped rooms use, and it is what makes the
        /// editor emit _CА / _CАК / _RБИ instead of _АC, which would look foreign in a diff
        /// against hand-authored data.
        /// </summary>
        private void AppendOverlays(StringBuilder sb)
        {
            if (_overlays.Count == 0)
            {
                return;
            }

            SortOverlays();

            for (int i = 0; i < _overlayOrder.Count; i++)
            {
                sb.Append(_overlayOrder[i]);
            }
        }

        /// <summary>
        /// tileDataString (row 0 = top) to code grid. Mirrors RoomTemplate.ParseTiles.
        /// </summary>
        private static string[][] SplitToCodes(string tileDataString)
        {
            string[][] codes = new string[GridHeight][];
            string[] rows = (tileDataString ?? string.Empty).Replace("\r\n", "\n").Split('\n');

            for (int row = 0; row < GridHeight; row++)
            {
                codes[row] = new string[GridWidth];
                string[] tokens = row < rows.Length ? rows[row].Split(new[] { '.' }) : Array.Empty<string>();

                for (int col = 0; col < GridWidth; col++)
                {
                    string token = col < tokens.Length ? tokens[col] : string.Empty;
                    codes[row][col] = string.IsNullOrEmpty(token) ? AirToken : token;
                }
            }

            return codes;
        }

        /// <summary>
        /// Code grid back to tileDataString. Empty cells are written as "_" rather than ""
        /// so the row can never be shifted by empty-token collapsing.
        /// </summary>
        private static string JoinToDataString(string[][] codes)
        {
            StringBuilder sb = new StringBuilder();

            for (int row = 0; row < codes.Length; row++)
            {
                if (row > 0)
                {
                    sb.Append('\n');
                }

                for (int col = 0; col < codes[row].Length; col++)
                {
                    if (col > 0)
                    {
                        sb.Append('.');
                    }

                    string token = codes[row][col];
                    sb.Append(string.IsNullOrEmpty(token) ? AirToken : token);
                }
            }

            return sb.ToString();
        }
    }
}
#endif
