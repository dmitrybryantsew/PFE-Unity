#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using PFE.Systems.Map;
using PFE.Systems.Map.Rendering;
using PFE.Systems.Map.Rendering.RoomTerrain;
using UnityEditor;
using UnityEngine;

namespace PFE.Editor.RoomTerrain
{
    /// <summary>
    /// Side-by-side presenter for the two room renderers: the shipped
    /// <see cref="TileCompositor"/> path on the left, the quarter-Tilemap path
    /// (<see cref="RoomTerrainRenderer"/>) on the right, both from the same room template and the same
    /// imported art.
    ///
    /// <para><b>Why a separate window rather than a mode in the game.</b> The whole point is to answer
    /// "is the other pipeline actually better, and which of its differences is responsible?" without
    /// risking the pipeline that works. A window can be wrong, crash and be closed; the room loader
    /// cannot. The character renderer A/B
    /// (<c>CharacterQuadPreviewWindow</c>) is the same shape and for the same reason.</para>
    ///
    /// <para><b>The state cross-check is the falsification instrument.</b> The quarter renderer derives
    /// its edge states from the room grid (<see cref="RoomQuarterStates"/>), while the shipped path
    /// reads <c>TileData.kontur1..4</c> that <see cref="KonturCalculator"/> wrote. Those are two
    /// independent implementations of the same AS3 rule, so on a wall cell they must agree. If they do
    /// not, one of them is wrong and the difference in the picture is not the renderer's — which is
    /// exactly the confusion this panel exists to prevent. The comparison is limited to walls because
    /// <c>KonturCalculator</c> only fills <c>kontur</c> for <c>physicsType == Wall</c>; platforms and
    /// shelves are left at 0 by design, so counting them would manufacture mismatches.</para>
    /// </summary>
    public sealed class RoomTerrainPreviewWindow : EditorWindow
    {
        private const int PreviewLayer = 31;
        private const float PreviewOffsetX = 1000f;
        private const string ExportRoot = "Assets/_PFE/Art/RoomTerrainCompare";

        private const string FormDatabasePath = "Assets/_PFE/Data/TileFormDatabase.asset";
        private const string ArtPath = "Assets/_PFE/Data/RoomTerrainArt.asset";
        private const string MaterialDatabasePath = "Assets/_PFE/Data/MaterialRenderDatabase.asset";
        private const string TextureLookupPath = "Assets/_PFE/Data/TileTextureLookup.asset";
        private const string MaskLookupPath = "Assets/_PFE/Data/TileMaskLookup.asset";
        private const string TileAssetDatabasePath = "Assets/_PFE/Data/Map/TileAssetDatabase.asset";
        private const string DefaultRoomFolder = "Assets/_PFE/Data/Resources/Rooms/rooms_art";

        // ─── Data ────────────────────────────────────────────────
        private RoomTemplate _roomTemplate;
        private TileFormDatabase _formDatabase;
        private RoomTerrainArt _art;
        private MaterialRenderDatabase _materialDatabase;
        private TileTextureLookup _textureLookup;
        private TileMaskLookup _maskLookup;
        private TileAssetDatabase _tileAssetDatabase;

        // ─── Preview scene ───────────────────────────────────────
        private GameObject _root;
        private GameObject _spriteRoot;
        private GameObject _quarterRoot;
        private RoomTerrainRenderer _terrain;
        private Camera _previewCamera;
        private RenderTexture _previewRt;
        private int _previewSize = 640;

        private RoomInstance _room;

        // ─── View ────────────────────────────────────────────────
        private float _sideGap = 21f;
        private float _cameraZoom = 5.5f;
        private Vector2 _cameraOffset;
        private bool _showSpriteSide = true;
        private bool _showQuarterSide = true;

        // ─── Renderer options ────────────────────────────────────
        private float _ambient = 1f;
        private bool _drawBack = true;
        private bool _drawFront = true;
        private bool _drawBorder = true;
        private bool _drawFloor = true;
        private bool _drawOverlays = true;
        private bool _overlayAboveMaterials = true;
        private bool _hideState0Mask;
        private bool _applyBackTint = true;
        private bool _applyFrontTint = true;
        private Color _shippedBackTint = Color.white;

        /// <summary>
        /// Percentage of the <b>shipped</b> half of the A/B frame that differs from the clear colour,
        /// as last measured by <see cref="MeasurePicture"/>, or −1 before it has run.
        ///
        /// <para><b>Why the verdict needs this.</b> On 2026-10-21 the triage printed
        /// <c>left rgb 0.10/0.10/0.12 covered 0.0%</c> and, two lines later, <c>verdict: no round trip
        /// … The dark shipped side has some other cause; stop looking at colour space.</c> The shipped
        /// side had not drawn <i>anything</i> — 0.0 % coverage means every sampled pixel equalled the
        /// camera's clear colour. Calling that "the dark shipped side" sends the next reader hunting a
        /// darkening stage in a path that produced no geometry at all, which is exactly the kind of
        /// confidently-wrong lead this window exists to prevent.</para>
        /// </summary>
        private double _shippedCoveredPercent = -1d;

        // ─── Diagnostics ─────────────────────────────────────────
        private bool _sceneReady;
        private string _status = string.Empty;
        private Vector2 _mainScroll;
        private string _spriteStats = "-";
        private string _quarterStats = "-";
        private string _spriteHealth = "not measured";

        /// <summary>Unlit material substituted onto the shipped side's sprite renderers, built once.</summary>
        private Material _shippedUnlitMaterial;

        /// <summary>What the substitution replaced, so the report says it happened rather than hiding it.</summary>
        private string _spriteMaterialNote = "sprite side: not applied";

        private string _boundsLine = "not measured";
        private string _layerLine = "-";
        private string _pictureLine = "not sampled";
        private string _colourLine = "-";
        private string _roundTripLine = "not measured";
        private string _roundTripVerdict = "not measured";
        private string _rearLine = "not measured";
        private string _overlayLine = "not measured";
        private string _hoverLine = "hover the preview to read a cell";
        private int _hoverCellX = -1;
        private int _hoverCellY = -1;
        private RoomQuarterStates _states;
        private string _crossCheckSummary = "not run";
        private readonly List<string> _crossCheckRows = new List<string>();
        private bool _showCrossCheck;
        private static GUIStyle _chipStyle;

        [MenuItem("PFE/Art/Room Terrain A/B (Quarter Tilemaps)", priority = 212)]
        public static void ShowWindow()
        {
            var window = GetWindow<RoomTerrainPreviewWindow>("Room Terrain A/B");
            window.minSize = new Vector2(820, 600);
        }

        private void OnEnable()
        {
            TryLoadAssets();
        }

        private void OnDisable()
        {
            CleanupScene();
        }

        private void OnDestroy()
        {
            CleanupScene();
        }

        // ─── Assets ──────────────────────────────────────────────

        private void TryLoadAssets()
        {
            if (_formDatabase == null) _formDatabase = AssetDatabase.LoadAssetAtPath<TileFormDatabase>(FormDatabasePath);
            if (_art == null) _art = AssetDatabase.LoadAssetAtPath<RoomTerrainArt>(ArtPath);
            if (_materialDatabase == null) _materialDatabase = AssetDatabase.LoadAssetAtPath<MaterialRenderDatabase>(MaterialDatabasePath);
            if (_textureLookup == null) _textureLookup = AssetDatabase.LoadAssetAtPath<TileTextureLookup>(TextureLookupPath);
            if (_maskLookup == null) _maskLookup = AssetDatabase.LoadAssetAtPath<TileMaskLookup>(MaskLookupPath);
            if (_tileAssetDatabase == null) _tileAssetDatabase = AssetDatabase.LoadAssetAtPath<TileAssetDatabase>(TileAssetDatabasePath);

            if (_roomTemplate == null) _roomTemplate = FindFirstRoomTemplate();
        }

        private static RoomTemplate FindFirstRoomTemplate()
        {
            if (!AssetDatabase.IsValidFolder(DefaultRoomFolder)) return null;

            string[] guids = AssetDatabase.FindAssets("t:RoomTemplate", new[] { DefaultRoomFolder });
            if (guids.Length == 0) return null;

            Array.Sort(guids, StringComparer.Ordinal);
            return AssetDatabase.LoadAssetAtPath<RoomTemplate>(AssetDatabase.GUIDToAssetPath(guids[0]));
        }

        // ─── GUI ─────────────────────────────────────────────────

        private void OnGUI()
        {
            _mainScroll = EditorGUILayout.BeginScrollView(_mainScroll);

            EditorGUILayout.LabelField(
                "Room Terrain A/B — composited sprites (shipped) vs quarter Tilemaps (parallel)",
                EditorStyles.boldLabel);

            DrawDataFields();

            bool ready = _roomTemplate != null && _formDatabase != null && _art != null && _materialDatabase != null;
            if (!ready)
            {
                EditorGUILayout.HelpBox(
                    "Missing data. The A/B needs a room template, the tile form database, the material " +
                    "database and the room-terrain art asset (build it with PFE/Art/Rebuild Room Terrain Art).",
                    MessageType.Warning);
                EditorGUILayout.EndScrollView();
                return;
            }

            EditorGUILayout.BeginHorizontal();

            EditorGUILayout.BeginVertical(GUILayout.Width(position.width * 0.60f));
            DrawPreviewPanel();
            EditorGUILayout.EndVertical();

            EditorGUILayout.BeginVertical();
            DrawOptions();
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);
            DrawCrossCheck();

            if (!string.IsNullOrEmpty(_status))
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.HelpBox(_status, MessageType.Info);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawDataFields()
        {
            EditorGUI.BeginChangeCheck();

            _roomTemplate = (RoomTemplate)EditorGUILayout.ObjectField(
                "Room template", _roomTemplate, typeof(RoomTemplate), false);
            _formDatabase = (TileFormDatabase)EditorGUILayout.ObjectField(
                "Tile form database", _formDatabase, typeof(TileFormDatabase), false);
            _art = (RoomTerrainArt)EditorGUILayout.ObjectField(
                "Room terrain art", _art, typeof(RoomTerrainArt), false);
            _materialDatabase = (MaterialRenderDatabase)EditorGUILayout.ObjectField(
                "Material database", _materialDatabase, typeof(MaterialRenderDatabase), false);

            if (EditorGUI.EndChangeCheck())
            {
                CleanupScene();
            }

            EditorGUILayout.BeginHorizontal();
            _textureLookup = (TileTextureLookup)EditorGUILayout.ObjectField(
                "Texture lookup", _textureLookup, typeof(TileTextureLookup), false);
            _maskLookup = (TileMaskLookup)EditorGUILayout.ObjectField(
                "Mask lookup", _maskLookup, typeof(TileMaskLookup), false);
            EditorGUILayout.EndHorizontal();

            _tileAssetDatabase = (TileAssetDatabase)EditorGUILayout.ObjectField(
                "Tile asset database", _tileAssetDatabase, typeof(TileAssetDatabase), false);

            if (_art != null)
            {
                EditorGUILayout.LabelField(
                    $"Art: {_art.FrameSetCount} clip(s), {_art.MaskCount} mask(s), {_art.TextureCount} texture(s), " +
                    $"cell {_art.CellPixels}px / quarter {_art.QuarterPixels}px",
                    EditorStyles.miniLabel);
            }
        }

        private void DrawPreviewPanel()
        {
            EditorGUILayout.LabelField("Preview", EditorStyles.boldLabel);

            if (_sceneReady && _previewRt != null)
            {
                float available = Mathf.Min(position.width * 0.58f, _previewSize);
                Rect rect = GUILayoutUtility.GetRect(available, available * 0.55f);
                EditorGUI.DrawPreviewTexture(rect, _previewRt, null, ScaleMode.ScaleToFit);

                // The RT is letterboxed inside `rect` by ScaleToFit, so a viewport point is only
                // convertible to a GUI point through the fitted sub-rect. Recomputing it here (rather
                // than guessing) is what makes the labels and the hover crosshair land on the right
                // cell instead of "roughly near it". Local, not a field: nothing outside this draw
                // reads it, and a stale field would survive the window being resized.
                Rect content = FitRect(rect, _previewRt.width, _previewRt.height);

                DrawSideLabels(content);
                DrawHoverCrosshair(content);
            }
            else
            {
                Rect rect = GUILayoutUtility.GetRect(320, 200);
                EditorGUI.DrawRect(rect, new Color(0.15f, 0.15f, 0.15f));
                GUI.Label(rect, "No preview — click Setup", EditorStyles.centeredGreyMiniLabel);
            }

            if (_sceneReady)
            {
                EditorGUILayout.LabelField(_hoverLine, EditorStyles.miniLabel);
            }

            EditorGUI.BeginChangeCheck();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Zoom", GUILayout.Width(44));
            _cameraZoom = EditorGUILayout.Slider(_cameraZoom, 0.5f, 20f);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Gap", GUILayout.Width(44));
            _sideGap = EditorGUILayout.Slider(_sideGap, 0f, 60f);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Offset", GUILayout.Width(44));
            _cameraOffset = EditorGUILayout.Vector2Field(string.Empty, _cameraOffset, GUILayout.Width(180));
            if (GUILayout.Button("Reset", GUILayout.Width(50))) _cameraOffset = Vector2.zero;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Render size", GUILayout.Width(72));
            _previewSize = EditorGUILayout.IntSlider(_previewSize, 256, 2048);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            _showSpriteSide = EditorGUILayout.ToggleLeft("Sprite side", _showSpriteSide, GUILayout.Width(100));
            _showQuarterSide = EditorGUILayout.ToggleLeft("Quarter side", _showQuarterSide, GUILayout.Width(100));
            EditorGUILayout.EndHorizontal();

            bool viewChanged = EditorGUI.EndChangeCheck();

            if (!_sceneReady)
            {
                if (GUILayout.Button("Setup preview", GUILayout.Height(28)))
                {
                    SetupScene();
                }
            }
            else
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Rebuild", GUILayout.Height(22)))
                {
                    SetupScene();
                }

                if (GUILayout.Button("Export PNG", GUILayout.Height(22)))
                {
                    ExportFrame();
                }

                EditorGUILayout.EndHorizontal();
            }

            if (viewChanged && _sceneReady)
            {
                ApplyViewOptions();
                Render();
            }

            if (_sceneReady)
            {
                EditorGUILayout.LabelField("Sprite side", EditorStyles.miniBoldLabel);
                EditorGUILayout.LabelField(_spriteStats, EditorStyles.miniLabel);
                EditorGUILayout.LabelField(_spriteHealth, EditorStyles.miniLabel);
                EditorGUILayout.LabelField("Quarter side", EditorStyles.miniBoldLabel);
                EditorGUILayout.LabelField(_quarterStats, EditorStyles.miniLabel);
                EditorGUILayout.LabelField(_layerLine, EditorStyles.miniLabel);
                EditorGUILayout.LabelField("Rear pass (beams, stairs)", EditorStyles.miniBoldLabel);
                EditorGUILayout.LabelField(_rearLine, EditorStyles.miniLabel);
                EditorGUILayout.LabelField("Overlays (ladders, ledges)", EditorStyles.miniBoldLabel);
                EditorGUILayout.LabelField(_overlayLine, EditorStyles.miniLabel);
                EditorGUILayout.LabelField("Registration", EditorStyles.miniBoldLabel);
                EditorGUILayout.LabelField(_boundsLine, EditorStyles.miniLabel);
                EditorGUILayout.LabelField(_colourLine, EditorStyles.miniLabel);
                EditorGUILayout.LabelField("Composited vs source", EditorStyles.miniBoldLabel);
                EditorGUILayout.LabelField(_roundTripLine, EditorStyles.miniLabel);
                EditorGUILayout.LabelField(_roundTripVerdict, EditorStyles.miniLabel);
                EditorGUILayout.LabelField("Picture (measured)", EditorStyles.miniBoldLabel);
                EditorGUILayout.LabelField(_pictureLine, EditorStyles.miniLabel);
            }
        }

        private void DrawOptions()
        {
            EditorGUILayout.LabelField("Quarter renderer", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Ambient", GUILayout.Width(60));
            _ambient = EditorGUILayout.Slider(_ambient, 0f, 1f);
            EditorGUILayout.EndHorizontal();

            _drawBack = EditorGUILayout.ToggleLeft("Back layer (background materials)", _drawBack);
            _drawFront = EditorGUILayout.ToggleLeft("Front layer (solid materials)", _drawFront);
            using (new EditorGUI.DisabledScope(!_drawFront))
            {
                _drawBorder = EditorGUILayout.ToggleLeft("Border part", _drawBorder);
                _drawFloor = EditorGUILayout.ToggleLeft("Floor part", _drawFloor);
            }

            _drawOverlays = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    "Overlays (ladders, treads, ledges)",
                    "The tileFront pass: a ladder is a 50x110 symbol pinned by its registration point (5,30) " +
                    "to the cell's top-left corner. The ledge on top of a ladder is the next frame of the " +
                    "same art (Location.as:583 does ++vid), so both appear or neither does."),
                _drawOverlays);

            using (new EditorGUI.DisabledScope(!_drawOverlays))
            {
                _overlayAboveMaterials = EditorGUILayout.ToggleLeft(
                    new GUIContent(
                        "Overlays above the walls (Remains order)",
                        "ON: overlays move to the wall layer (MainTiles) at order " +
                        RoomTerrainRenderer.OverlayAboveOrder + ", i.e. above the front trio, which is " +
                        "what Remains-Unity does (RoomTerrainView: FrontSpriteOrder = 10, above " +
                        "FloorOrder = -20). " +
                        "OFF: AS3's bitmap order — Grafon.as:518 blits the overlays before :526 draws the " +
                        "materials, so they are under the walls and visible only through the channel the " +
                        "mask carves. If a room shows a black staircase channel, switch this ON: it means " +
                        "the mask did not carve and the art was hidden underneath."),
                    _overlayAboveMaterials);
            }

            _hideState0Mask = EditorGUILayout.ToggleLeft(
                new GUIContent("Debug: skip state 0", "The shipped compositor skips the mask at state 0 and writes the material through unmasked."),
                _hideState0Mask);

            _applyBackTint = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    "Back tint (room environment)",
                    "Multiply the back layer by the room's colour scheme and darkness, as RoomVisualController does. " +
                    "Turn it off to compare shape and projection only."),
                _applyBackTint);

            _applyFrontTint = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    "Front tint (room colour scheme) — quarter side only",
                    "Multiply the front layer by the room's colour scheme, as Grafon.as:617 does. The shipped " +
                    "path passes Color.white as its front tint, so it draws walls untinted: with this on, a " +
                    "colour difference between the two halves is this renderer being faithful, not a bug. " +
                    "Turn it off for a like-for-like comparison."),
                _applyFrontTint);

            if (EditorGUI.EndChangeCheck() && _sceneReady)
            {
                RebuildQuarterOnly();
            }

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("What to look at", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "1. Registration first. Δsize must be 0.00 and Δcentre within a hundredth of a cell. If " +
                "they are not, the two pictures are not of the same thing and nothing below applies.\n\n" +
                "2. Dark is not empty. Coverage near 0 % means a side is not drawing; high coverage with " +
                "an rgb near the background means it is drawing something black.\n\n" +
                "3. If one side is dark, read the verdict line before touching anything. It compares the " +
                "composed textures against source^2.2 and against source^2.2 x tint, and names whichever " +
                "fits: a gamma round trip, the back tint, both, or neither. Note that this project DOES " +
                "have a light: SampleScene carries a Global Light2D on layer 0, white at intensity 1, " +
                "applied to every sorting layer. Because it is identity, lit and unlit agree in game — " +
                "but this camera culls to its own layer, so the light never reaches it and any lit " +
                "sprite here draws (0,0,0). The shipped side is therefore re-materialled to unlit, and " +
                "the sprite material line reports how many renderers that touched.\n\n" +
                "4. Back tint must match on both sides. The shipped path multiplies its back layer by " +
                "1 - (170 + darkness)/255, so about 0.33 even for a bright room. A renderer that skips " +
                "that multiply looks brighter, which is not the same as better.\n\n" +
                "4b. Front tint is the one place the two halves are meant to differ. The original " +
                "colour-transforms its front bitmap by the room's colour scheme (Grafon.as:617); the " +
                "shipped path passes white and draws its walls untinted. With front tint on, a black " +
                "room's walls are halved on the quarter side and not on the shipped side — that is " +
                "faithfulness, not a regression. Turn it off before judging shape.\n\n" +
                "5. Platforms, beams and stairs are a third pass, not the front pass. The original draws " +
                "rear materials (isRear) into their own bitmap between the back wall and the front wall, " +
                "and the rear-pass line reports how many cells carry one and how many shapes landed. If " +
                "the cells are there and the shapes are 0, the pass did not run; if both are 0, this room " +
                "has no beams and the picture cannot answer the question.\n\n" +
                "6. Ladders, treads and ledges are overlays, and they are the one thing here that is not a " +
                "material. The original adds a tileFront symbol at the cell's origin (Grafon.as:470-499) " +
                "and blits it before any material, so it is 50x110 on a 40 px grid and sits under the " +
                "walls. The ledge is not a second sprite: Location.as:583 does ++vid on the top step, so " +
                "the ladder and its ledge are adjacent frames and always fail together. The overlay line " +
                "gives cells, ledge caps, shelf cells, frames and placements — cells with 0 placements is " +
                "a broken pass, 0 cells is a room without ladders. Ledge caps and shelf cells are counted " +
                "apart on purpose: both set TileData.isLedge, but only a cap draws a frame (a beam is " +
                "shelf='1' vid='0', so it is a material).\n\n" +
                "7. An overlay's registration point - (5, 30) px inside its 50x110 canvas - must land on the " +
                "cell's top-left corner. Hover a stair cell: the line prints the frame's size, the centre it " +
                "should occupy and its sorting order, so a missing frame, a misplacement and an occluded " +
                "sprite stop looking alike. If the picture shows a black staircase channel, switch " +
                "'Overlays above the walls' on: it means the mask did not carve a channel and the art was " +
                "hidden underneath.\n\n" +
                "8. Hover a cell to name it: graphic, material, texture, overlay frame and the four edge " +
                "states next to the kontur values the shipped side uses. One odd material is usually a " +
                "grate, not a bug.\n\n" +
                "9. Layers: a layer whose shape count exceeds the room's cell count is drawing something " +
                "it should not — that is where thin-line artefacts come from.",
                MessageType.None);

            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Log comparison to Console", GUILayout.Height(20)))
            {
                LogComparison();
            }

            if (GUILayout.Button("Log triage to Console", GUILayout.Height(20)))
            {
                LogTriage();
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawCrossCheck()
        {
            _showCrossCheck = EditorGUILayout.Foldout(
                _showCrossCheck, "State cross-check — grid vs KonturCalculator", true);

            if (!_showCrossCheck) return;

            EditorGUILayout.HelpBox(
                _crossCheckSummary,
                _crossCheckSummary.StartsWith("OK", StringComparison.Ordinal) ? MessageType.Info : MessageType.Warning);

            foreach (string row in _crossCheckRows)
            {
                EditorGUILayout.LabelField(row, EditorStyles.miniLabel);
            }
        }

        // ─── Scene ───────────────────────────────────────────────

        private void SetupScene()
        {
            CleanupScene();

            if (_roomTemplate == null || _formDatabase == null || _art == null || _materialDatabase == null)
            {
                _status = "Missing data; nothing was built.";
                return;
            }

            _room = BuildRoom(_roomTemplate, _formDatabase);
            if (_room == null)
            {
                _status = "The room template produced no tiles.";
                return;
            }

            // Kept for the hover readout: the same rule the renderer draws from, so the numbers under
            // the cursor are the numbers that were drawn rather than a second implementation.
            _states = RoomTerrainRenderer.BuildStates(_room);

            RunCrossCheck();

            float cellSize = RoomTerrainGeometry.DefaultCellSize;

            _root = new GameObject("[RTAB_Root]")
            {
                hideFlags = HideFlags.HideAndDontSave,
                layer = PreviewLayer
            };
            _root.transform.position = new Vector3(PreviewOffsetX, 0f, 0f);

            // ── shipped path ──
            _spriteRoot = new GameObject("[RTAB_Sprite]")
            {
                hideFlags = HideFlags.HideAndDontSave,
                layer = PreviewLayer
            };
            _spriteRoot.transform.SetParent(_root.transform, false);

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            BuildShippedSide(_spriteRoot.transform);
            stopwatch.Stop();
            _spriteStats = $"{stopwatch.ElapsedMilliseconds} ms · {CountRenderers(_spriteRoot)} renderer(s) · " +
                           $"{_room.width}×{_room.height} cells";

            // ── quarter path ──
            _quarterRoot = new GameObject("[RTAB_Quarter]")
            {
                hideFlags = HideFlags.HideAndDontSave,
                layer = PreviewLayer
            };
            _quarterRoot.transform.SetParent(_root.transform, false);

            _terrain = _quarterRoot.AddComponent<RoomTerrainRenderer>();
            _terrain.Build(_room, _art, _materialDatabase, BuildOptions());

            _quarterStats = $"{_terrain.LastBuildMilliseconds:0.0} ms · {_terrain.LayerCount} layer(s) · " +
                            $"{_terrain.ShapesPlaced} shape(s)" + DescribeMisses();

            ApplyPreviewFlags(_root.transform);

            var cameraGo = new GameObject("[RTAB_Camera]")
            {
                hideFlags = HideFlags.HideAndDontSave,
                layer = PreviewLayer
            };
            _previewCamera = cameraGo.AddComponent<Camera>();
            _previewCamera.clearFlags = CameraClearFlags.SolidColor;
            _previewCamera.backgroundColor = new Color(0.10f, 0.10f, 0.12f, 1f);
            _previewCamera.orthographic = true;
            _previewCamera.cullingMask = 1 << PreviewLayer;
            _previewCamera.enabled = false;

            RecreateRenderTexture();

            _sceneReady = true;
            _status = $"Ready: {_roomTemplate.GetContentId()}, cell {cellSize} units, ambient {_ambient:0.00}.";

            ApplyViewOptions();
            Render();
            MeasureSides();
        }

        private RoomTerrainRenderer.Options BuildOptions()
        {
            RoomTerrainRenderer.Options options = RoomTerrainRenderer.Options.Default();
            options.cellSize = RoomTerrainGeometry.DefaultCellSize;
            options.ambient = _ambient;
            options.drawBack = _drawBack;
            options.drawFront = _drawFront;
            options.drawBorder = _drawBorder;
            options.drawFloor = _drawFloor;
            options.drawOverlays = _drawOverlays;
            options.overlayAboveMaterials = _overlayAboveMaterials;
            options.skipState0Mask = _hideState0Mask;
            options.applyBackTint = _applyBackTint;
            options.applyFrontTint = _applyFrontTint;
            return options;
        }

        private void BuildShippedSide(Transform parent)
        {
            if (_textureLookup == null || _maskLookup == null || _tileAssetDatabase == null)
            {
                _spriteStats = "not built — texture lookup, mask lookup or tile asset database missing.";
                return;
            }

            var container = new GameObject("[RTAB_SpriteTiles]");
            container.transform.SetParent(parent, false);

            var compositor = new TileCompositor(_textureLookup, _materialDatabase, _maskLookup);

            // Built the way RoomVisualController builds it, not the way the convenience constructor
            // does. The difference is the secondary tint: the game hands TileVisualManager
            // ResolveBackgroundTileTint(room), which multiplies the back layer by the room's colour
            // scheme and by 1 - (170 + darkness)/255 — about a third, even at darkness 0. Using the
            // default white here would have compared a tinted path against an untinted one and blamed
            // the shape for the brightness.
            Color backTint = _room?.environment != null
                ? RoomBackdropRenderer.ResolveBackgroundTileTint(_room.environment)
                : Color.white;

            _shippedBackTint = backTint;

            var manager = new TileVisualManager(
                _room,
                _tileAssetDatabase,
                container.transform,
                compositor,
                null,
                0,
                Color.white,
                backTint,
                MapSortingLayers.MainTiles,
                MapSortingLayers.Foreground);

            manager.CreateAllTiles();

            // Last, because CreateAllTiles is what creates the renderers this rewrites.
            EnsureShippedUnlitMaterial();
        }

        /// <summary>
        /// Puts the shipped side on the unlit sprite shader, for the same reason
        /// <c>CharacterQuadPreviewWindow.EnsureUnlitPreviewMaterial</c> does.
        ///
        /// <para><b>Why the shipped side rendered black.</b> URP is active through
        /// <c>QualitySettings.asset</c> (every quality level points at <c>Settings/UniversalRP.asset</c>),
        /// whose <c>m_RendererDataList[0]</c> is <c>Settings/Renderer2D.asset</c>. That asset declares
        /// <c>m_DefaultMaterialType: 0</c>, and the enum it indexes is
        /// <c>Renderer2DDefaultMaterialType { Lit, Unlit, Custom }</c> (<c>Renderer2DData.cs:21-26</c>) —
        /// so <b>0 is Lit</b> and a bare <see cref="SpriteRenderer"/> takes the lit sprite material.
        /// The game is fine because <c>SampleScene</c> carries a Global <c>Light2D</c> on layer 0, white,
        /// intensity 1, applied to every sorting layer. This camera culls to
        /// <see cref="PreviewLayer"/> (31), which culls that light's GameObject with it, so <b>no light
        /// reaches the preview</b> and every lit sprite resolves to black: the shipped tiles measured
        /// rgb 0.01 over 90.9 % coverage while the quarter side, whose shader is unlit, measured 0.09.</para>
        ///
        /// <para><b>Why substituting unlit is faithful rather than a fudge.</b> That global light is
        /// identity — white times one, over every sorting layer — so lit and unlit produce the same
        /// pixels in game. Re-materialling the shipped side here therefore reproduces the game's
        /// appearance, it does not approximate it. If the game ever tints or dims that light, this
        /// equivalence breaks and the preview must model the light instead of dropping it.</para>
        ///
        /// <para>That is a property of the preview, not of the shipped renderer, and it is the reason the
        /// A/B's colour comparison was void rather than merely disappointing. Substituting unlit makes the
        /// two sides differ in geometry, filtering and batching and in nothing else. The substitution is
        /// reported in <see cref="_spriteMaterialNote"/>, never hidden.</para>
        /// </summary>
        private void EnsureShippedUnlitMaterial()
        {
            if (_spriteRoot == null) return;

            if (_shippedUnlitMaterial == null)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default")
                             ?? Shader.Find("Sprites/Default");

                if (shader == null)
                {
                    _spriteMaterialNote = "sprite side: no unlit sprite shader found — left on the lit default, so this side is black";
                    return;
                }

                _shippedUnlitMaterial = new Material(shader)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    name = "RoomTerrainAB_ShippedUnlit"
                };
            }

            SpriteRenderer[] renderers = _spriteRoot.GetComponentsInChildren<SpriteRenderer>(true);
            int replaced = 0;
            string previous = "(default)";

            foreach (SpriteRenderer renderer in renderers)
            {
                if (renderer == null || renderer.sharedMaterial == _shippedUnlitMaterial) continue;

                if (renderer.sharedMaterial != null && renderer.sharedMaterial.shader != null)
                {
                    previous = renderer.sharedMaterial.shader.name;
                }

                renderer.sharedMaterial = _shippedUnlitMaterial;
                replaced++;
            }

            _spriteMaterialNote = replaced > 0
                ? $"sprite side: {previous} -> unlit on {replaced} renderer(s)"
                : "sprite side: already unlit";
        }

        /// <summary>
        /// Rebuilds only the quarter side. The options below are all shader/material inputs or layer
        /// selections, so a full rebuild of the shipped side would cost seconds to show a change that
        /// cannot affect it.
        /// </summary>
        private void RebuildQuarterOnly()
        {
            if (_terrain == null || _room == null) return;

            _terrain.Build(_room, _art, _materialDatabase, BuildOptions());
            _quarterStats = $"{_terrain.LastBuildMilliseconds:0.0} ms · {_terrain.LayerCount} layer(s) · " +
                            $"{_terrain.ShapesPlaced} shape(s)" + DescribeMisses();

            ApplyPreviewFlags(_root.transform);
            Render();
            MeasureSides();
            Repaint();
        }

        private string DescribeMisses()
        {
            if (_terrain == null) return string.Empty;

            var builder = new StringBuilder();

            if (_terrain.MissingMasks.Count > 0)
            {
                builder.Append(" · missing masks:");
                foreach (KeyValuePair<string, int> pair in _terrain.MissingMasks)
                {
                    builder.Append($" {pair.Key}×{pair.Value}");
                }
            }

            if (_terrain.MissingMaterials.Count > 0)
            {
                builder.Append(" · no material entry:");
                foreach (KeyValuePair<string, int> pair in _terrain.MissingMaterials)
                {
                    builder.Append($" {pair.Key}×{pair.Value}");
                }
            }

            return builder.ToString();
        }

        private static int CountRenderers(GameObject root)
        {
            return root == null ? 0 : root.GetComponentsInChildren<Renderer>(true).Length;
        }

        private void ApplyViewOptions()
        {
            if (_root == null) return;

            if (_spriteRoot != null)
            {
                _spriteRoot.SetActive(_showSpriteSide);
                _spriteRoot.transform.localPosition = new Vector3(-_sideGap * 0.5f, 0f, 0f);
            }

            if (_quarterRoot != null)
            {
                _quarterRoot.SetActive(_showQuarterSide);
                _quarterRoot.transform.localPosition = new Vector3(_sideGap * 0.5f, 0f, 0f);
            }
        }

        private void CleanupScene()
        {
            if (_shippedUnlitMaterial != null)
            {
                DestroyImmediate(_shippedUnlitMaterial);
                _shippedUnlitMaterial = null;
            }

            _spriteMaterialNote = "sprite side: not applied";

            if (_terrain != null)
            {
                DestroyImmediate(_terrain);
                _terrain = null;
            }

            if (_root != null)
            {
                DestroyImmediate(_root);
                _root = null;
            }

            if (_previewCamera != null)
            {
                DestroyImmediate(_previewCamera.gameObject);
                _previewCamera = null;
            }

            if (_previewRt != null)
            {
                DestroyImmediate(_previewRt);
                _previewRt = null;
            }

            _spriteRoot = null;
            _quarterRoot = null;
            _sceneReady = false;
            _spriteStats = "-";
            _quarterStats = "-";
            _spriteHealth = "not measured";
            _boundsLine = "not measured";
            _layerLine = "-";
            _pictureLine = "not sampled";
            _shippedCoveredPercent = -1d;
            _colourLine = "-";
            _roundTripLine = "not measured";
            _roundTripVerdict = "not measured";
            _rearLine = "not measured";
            _overlayLine = "not measured";
            _states = null;
            _hoverCellX = -1;
            _hoverCellY = -1;
            _hoverLine = "hover the preview to read a cell";
        }

        private void RecreateRenderTexture()
        {
            if (_previewRt != null) DestroyImmediate(_previewRt);

            _previewRt = new RenderTexture(_previewSize, Mathf.Max(64, Mathf.RoundToInt(_previewSize * 0.55f)), 16)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
        }

        private void Render()
        {
            if (_previewCamera == null || _previewRt == null) return;

            if (_previewRt.width != _previewSize)
            {
                RecreateRenderTexture();
            }

            float roomCentreX = _room != null ? _room.width * RoomTerrainGeometry.DefaultCellSize * 0.5f : 0f;
            float roomCentreY = _room != null ? _room.height * RoomTerrainGeometry.DefaultCellSize * 0.5f : 0f;

            _previewCamera.orthographicSize = _cameraZoom;
            _previewCamera.transform.position = new Vector3(
                PreviewOffsetX + roomCentreX + _cameraOffset.x,
                roomCentreY + _cameraOffset.y,
                -10f);
            _previewCamera.targetTexture = _previewRt;
            _previewCamera.Render();
            _previewCamera.targetTexture = null;
        }

        // ─── Room ────────────────────────────────────────────────

        /// <summary>
        /// A <see cref="RoomInstance"/> from a template, mirroring <c>RoomGenerator.GenerateRoom</c>'s
        /// tile half and nothing else.
        ///
        /// <para>Doors, spawn points, difficulty and the procedural pass are deliberately absent: this
        /// window draws geometry, and pulling in the generator would make the preview depend on a build
        /// context, a land and a VContainer scope. <c>KonturCalculator.CalculateAll</c> is the one call
        /// that has to be here, because the shipped side reads what it writes.</para>
        /// </summary>
        private static RoomInstance BuildRoom(RoomTemplate template, TileFormDatabase formDatabase)
        {
            var room = new RoomInstance
            {
                id = template.GetContentId() + "_preview",
                templateId = template.GetContentId(),
                width = WorldConstants.ROOM_WIDTH,
                height = WorldConstants.ROOM_HEIGHT,
                roomType = template.type
            };

            room.InitializeTiles();

            TileData[,] parsed = template.ParseTiles(formDatabase);
            for (int x = 0; x < room.width; x++)
            {
                for (int y = 0; y < room.height; y++)
                {
                    if (parsed[x, y] != null) room.tiles[x, y] = parsed[x, y];
                }
            }

            room.environment = MapEnvironment(template.environment);
            KonturCalculator.CalculateAll(room);
            return room;
        }

        /// <summary>
        /// The fields the renderers read. A full copy would be a second mapping to keep in step with
        /// <c>RoomEnvironmentData</c>; this one is explicitly partial and says so.
        /// </summary>
        private static RoomEnvironment MapEnvironment(RoomEnvironmentData data)
        {
            if (data == null) return new RoomEnvironment();

            return new RoomEnvironment
            {
                musicTrack = data.musicTrack,
                colorScheme = data.colorScheme,
                backgroundWall = data.backgroundWall,
                backgroundForm = data.backgroundForm,
                hasSky = data.hasSky,
                darkness = data.darkness
            };
        }

        // ─── Cross-check ─────────────────────────────────────────

        /// <summary>
        /// Compares the renderer's grid-derived states with what <see cref="KonturCalculator"/> wrote.
        ///
        /// <para>Walls only, for the reason in the class summary. The comparison is per quarter, so a
        /// single cell with one disagreeing quarter is reported once with the quarter named — that is
        /// what makes the row actionable.</para>
        /// </summary>
        private void RunCrossCheck()
        {
            _crossCheckRows.Clear();

            if (_room == null || _room.tiles == null)
            {
                _crossCheckSummary = "not run — no room.";
                return;
            }

            RoomQuarterStates states = RoomTerrainRenderer.BuildStates(_room);

            int wallCells = 0;
            int comparedQuarters = 0;
            int mismatches = 0;

            for (int y = 0; y < _room.height; y++)
            {
                for (int x = 0; x < _room.width; x++)
                {
                    TileData tile = _room.tiles[x, y];
                    if (tile == null || tile.physicsType != TilePhysicsType.Wall) continue;

                    wallCells++;

                    foreach (RoomQuarter quarter in RoomQuarterRules.All())
                    {
                        int fromGrid = states.Front(x, y, quarter);
                        int fromKontur = KonturFor(tile, quarter);
                        comparedQuarters++;

                        if (fromGrid == fromKontur) continue;

                        mismatches++;
                        if (_crossCheckRows.Count < 12)
                        {
                            _crossCheckRows.Add(
                                $"({x},{y}) {RoomQuarterRules.Name(quarter)}: grid {fromGrid}, kontur {fromKontur}");
                        }
                    }
                }
            }

            if (comparedQuarters == 0)
            {
                _crossCheckSummary = "not run — the room has no wall cells.";
                return;
            }

            _crossCheckSummary = mismatches == 0
                ? $"OK — {comparedQuarters} quarter(s) on {wallCells} wall cell(s) agree between the grid rule and KonturCalculator."
                : $"{mismatches} of {comparedQuarters} quarter(s) disagree on {wallCells} wall cell(s). " +
                  "The picture below is not a pure renderer comparison until this is zero.";
        }

        private static int KonturFor(TileData tile, RoomQuarter quarter)
        {
            switch (quarter)
            {
                case RoomQuarter.TopLeft: return tile.kontur1;
                case RoomQuarter.TopRight: return tile.kontur2;
                case RoomQuarter.BottomLeft: return tile.kontur3;
                default: return tile.kontur4;
            }
        }

        // ─── Diagnostics ─────────────────────────────────────────
        //
        // The window used to show two pictures and two counts, which is enough to notice a difference
        // and not enough to name one. Everything below exists to turn "the left one is dark and the
        // right one has a weird square" into a number or an id.

        private static Rect FitRect(Rect outer, int width, int height)
        {
            if (width <= 0 || height <= 0) return outer;

            float aspect = (float)width / height;
            float w = outer.width;
            float h = w / aspect;
            if (h > outer.height)
            {
                h = outer.height;
                w = h * aspect;
            }

            return new Rect(outer.x + (outer.width - w) * 0.5f, outer.y + (outer.height - h) * 0.5f, w, h);
        }

        /// <summary>Viewport-space X (0..1) of a world X, in GUI space.</summary>
        private float GuiXForWorld(Rect content, float worldX)
        {
            if (_previewCamera == null) return content.center.x;

            Vector3 viewport = _previewCamera.WorldToViewportPoint(new Vector3(worldX, 0f, 0f));
            return content.x + viewport.x * content.width;
        }

        private Vector2 GuiForWorld(Rect content, Vector3 world)
        {
            if (_previewCamera == null) return content.center;

            Vector3 viewport = _previewCamera.WorldToViewportPoint(world);
            return new Vector2(content.x + viewport.x * content.width, content.y + (1f - viewport.y) * content.height);
        }

        /// <summary>
        /// Which side a world X belongs to, and that side's origin. The two roots are children of
        /// <c>[RTAB_Root]</c> at ±gap/2, so the split is the midpoint and not the camera's centre —
        /// with the camera panned by <c>Offset</c> the two are not the same number.
        /// </summary>
        private void SideFor(float worldX, out bool quarterSide, out float sideOriginX)
        {
            quarterSide = worldX >= PreviewOffsetX;
            sideOriginX = PreviewOffsetX + (quarterSide ? _sideGap * 0.5f : -_sideGap * 0.5f);
        }

        private void DrawSideLabels(Rect content)
        {
            if (_room == null) return;

            if (_chipStyle == null)
            {
                _chipStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    alignment = TextAnchor.MiddleCenter,
                    normal = { textColor = Color.white }
                };
            }

            DrawChip(content, -_sideGap * 0.5f, "SHIPPED · sprites", new Color(0.55f, 0.22f, 0.20f, 0.85f));
            DrawChip(content, _sideGap * 0.5f, "QUARTER · tilemaps", new Color(0.18f, 0.42f, 0.28f, 0.85f));
        }

        private void DrawChip(Rect content, float localOffsetX, string text, Color background)
        {
            float guiX = GuiXForWorld(content, PreviewOffsetX + localOffsetX);
            var chip = new Rect(guiX - 78f, content.yMax - 18f, 156f, 16f);

            if (chip.x < content.x) chip.x = content.x;
            if (chip.xMax > content.xMax) chip.x = content.xMax - chip.width;

            EditorGUI.DrawRect(chip, background);
            GUI.Label(chip, text, _chipStyle);
        }

        private void DrawHoverCrosshair(Rect content)
        {
            Event current = Event.current;
            if (current == null || _previewCamera == null || _room == null) return;

            if (!content.Contains(current.mousePosition))
            {
                if (_hoverCellX >= 0)
                {
                    _hoverCellX = -1;
                    _hoverCellY = -1;
                    _hoverLine = "hover the preview to read a cell";
                }

                return;
            }

            if (current.type == EventType.MouseMove) Repaint();

            var viewport = new Vector3(
                Mathf.Clamp01((current.mousePosition.x - content.x) / content.width),
                Mathf.Clamp01(1f - (current.mousePosition.y - content.y) / content.height),
                10f);

            Vector3 world = _previewCamera.ViewportToWorldPoint(viewport);
            float cellSize = RoomTerrainGeometry.DefaultCellSize;

            SideFor(world.x, out bool quarterSide, out float originX);

            int cellX = Mathf.FloorToInt((world.x - originX) / cellSize);
            int cellY = Mathf.FloorToInt(world.y / cellSize);

            _hoverCellX = cellX;
            _hoverCellY = cellY;
            _hoverLine = DescribeCell(cellX, cellY, quarterSide);

            // One-cell outline, drawn from the two world corners so it tracks zoom and pan.
            Vector2 a = GuiForWorld(content, new Vector3(originX + cellX * cellSize, cellY * cellSize, 0f));
            Vector2 b = GuiForWorld(content, new Vector3(originX + (cellX + 1) * cellSize, (cellY + 1) * cellSize, 0f));

            float x0 = Mathf.Min(a.x, b.x);
            float x1 = Mathf.Max(a.x, b.x);
            float y0 = Mathf.Min(a.y, b.y);
            float y1 = Mathf.Max(a.y, b.y);
            var line = new Color(1f, 0.85f, 0.2f, 0.9f);

            EditorGUI.DrawRect(new Rect(x0, y0, x1 - x0, 1f), line);
            EditorGUI.DrawRect(new Rect(x0, y1, x1 - x0, 1f), line);
            EditorGUI.DrawRect(new Rect(x0, y0, 1f, y1 - y0), line);
            EditorGUI.DrawRect(new Rect(x1, y0, 1f, y1 - y0), line);
        }

        /// <summary>
        /// Both sides' view of one cell. This is the answer to "what is that dark square": it names the
        /// graphic, the material id, the texture the material resolves to, and the edge state each
        /// quarter got — so a single cell can be compared without exporting a screenshot and squinting.
        /// </summary>
        private string DescribeCell(int x, int y, bool quarterSide)
        {
            if (_room == null || _room.tiles == null) return string.Empty;

            string side = quarterSide ? "quarter" : "shipped";

            if (x < 0 || y < 0 || x >= _room.width || y >= _room.height)
            {
                return $"{side} side · cell ({x},{y}) is outside the {_room.width}×{_room.height} room";
            }

            TileData tile = _room.tiles[x, y];
            if (tile == null) return $"{side} side · cell ({x},{y}) has no tile";

            string front = tile.GetFrontGraphic();
            string back = tile.GetBackGraphic();

            var builder = new StringBuilder();
            builder.Append(side).Append(" side · cell (").Append(x).Append(',').Append(y).Append(") ");
            builder.Append(tile.physicsType).Append(" · h").Append(tile.heightLevel);
            builder.Append(" · opacity ").Append(tile.opacity.ToString("0.00"));
            builder.Append(" · front '").Append(string.IsNullOrEmpty(front) ? "—" : front).Append('\'');
            builder.Append(" back '").Append(string.IsNullOrEmpty(back) ? "—" : back).Append('\'');

            if (_materialDatabase != null && !string.IsNullOrEmpty(front))
            {
                MaterialRenderEntry entry = _materialDatabase.GetFrontMaterial(front);
                if (entry == null)
                {
                    builder.Append(" · NO MATERIAL ENTRY");
                }
                else
                {
                    builder.Append(" → tex '").Append(entry.mainTexture).Append('\'');
                    builder.Append(" mask '").Append(entry.mainMask).Append('\'');
                    if (entry.HasBorder) builder.Append(" border '").Append(entry.borderTexture).Append('\'');
                    if (entry.HasFloor) builder.Append(" floor '").Append(entry.floorTexture).Append('\'');

                    // The pass a material lands in is not visible in the picture, and it is the
                    // difference between a beam that hangs in front of the wall and one that is
                    // drawn as if it were a wall. Say it here so the readout answers the question.
                    builder.Append(entry.isRear
                        ? " · REAR pass (beam/stair) → BackgroundDecor"
                        : " · front pass → MainTiles");
                }
            }

            if (tile.visualId > 0 || tile.visualId2 > 0)
            {
                builder.Append(" · overlay");
                if (tile.visualId > 0)
                {
                    builder.Append(" tile_").Append(tile.visualId)
                        .Append(tile.vidRear ? " (rear)" : " (front)");
                    builder.Append(DescribeOverlayPlacement(tile.visualId, x, y));
                }

                if (tile.visualId2 > 0)
                {
                    builder.Append(", tile_").Append(tile.visualId2)
                        .Append(tile.vid2Rear ? " (rear)" : " (front)");
                    builder.Append(DescribeOverlayPlacement(tile.visualId2, x, y));
                }

                // The ledge is not a separate flag in the art: it is the next frame of the same
                // ladder, so a cell that reads "ledge" and draws tile_N has the wrong frame. The two
                // sources of `isLedge` are named apart, because only the stair one moves the frame.
                if (tile.isLedge)
                {
                    builder.Append(tile.stairType != 0
                        ? " · LEDGE cap (vid was bumped by Location.as:583)"
                        : " · shelf (a beam material, vid stays 0 — no overlay)");
                }
            }

            if (_states != null)
            {
                builder.Append(" · states ");
                foreach (RoomQuarter quarter in RoomQuarterRules.All())
                {
                    builder.Append(RoomQuarterRules.Name(quarter)).Append('=')
                        .Append(_states.Front(x, y, quarter)).Append(' ');
                }

                builder.Append("| kontur ");
                foreach (RoomQuarter quarter in RoomQuarterRules.All())
                {
                    builder.Append(KonturFor(tile, quarter)).Append(' ');
                }
            }

            return builder.ToString();
        }

        /// <summary>
        /// Where the overlay for <paramref name="visualId"/> on cell (<paramref name="x"/>,
        /// <paramref name="y"/>) <i>should</i> be, and how big it is.
        ///
        /// <para><b>Why the hover line needs this.</b> "The ladder is black" cannot distinguish three
        /// different faults: the overlay was never placed, it was placed somewhere else, or it was
        /// placed correctly and something covers it. The expected centre is computable from the art
        /// asset alone, so printing it turns the picture into a measurement: compare it with the
        /// overlay's actual transform, and with the visible art. A missing frame says so directly
        /// rather than as a counter elsewhere in the window.</para>
        /// </summary>
        private string DescribeOverlayPlacement(int visualId, int x, int y)
        {
            if (_art == null)
            {
                return " (no art asset — placement unknown)";
            }

            Sprite sprite = _art.Overlay(visualId);
            if (sprite == null)
            {
                return $" (frame {visualId} NOT IN THE ART ASSET — nothing can be drawn)";
            }

            float cellSize = RoomTerrainGeometry.DefaultCellSize;
            float pixelsToWorld = _art.CellPixels > 0 ? cellSize / _art.CellPixels : 0.01f;
            Vector2 size = sprite.bounds.size;
            Vector3 centre = RoomTerrainGeometry.Overlay(
                x, y, cellSize, size.x, size.y,
                RoomTerrainArt.OverlayOriginXPixels * pixelsToWorld,
                RoomTerrainArt.OverlayOriginYPixels * pixelsToWorld);

            int order = _overlayAboveMaterials ? RoomTerrainRenderer.OverlayAboveOrder : RoomTerrainRenderer.OverlayOrder;

            return string.Format(
                " {0:0.00}x{1:0.00} at ({2:0.00}, {3:0.00}) order {4}",
                size.x, size.y, centre.x, centre.y, order);
        }

        /// <summary>
        /// Measures both sides after a build: how many sprites actually got a sprite, what colour the
        /// composed textures are, and where each side's geometry sits.
        ///
        /// <para><b>The bounds pair is the registration answer.</b> Both sides are built from one
        /// <see cref="RoomInstance"/> at the same cell size, so their world bounds must be the same
        /// rectangle up to the side offset. If the sizes differ the scale is wrong; if the sizes agree
        /// and the centres differ by a constant the placement is; if both agree the pictures are
        /// comparable and any remaining difference is shading.</para>
        /// </summary>
        private void MeasureSides()
        {
            bool hasSprite = TryUnionBounds(_spriteRoot, out Bounds spriteBounds);
            bool hasQuarter = TryUnionBounds(_quarterRoot, out Bounds quarterBounds);

            if (_spriteRoot != null)
            {
                SpriteRenderer[] renderers = _spriteRoot.GetComponentsInChildren<SpriteRenderer>(true);
                int withSprite = 0;
                var textures = new HashSet<Texture2D>();

                foreach (SpriteRenderer renderer in renderers)
                {
                    if (renderer == null || renderer.sprite == null) continue;
                    withSprite++;
                    if (renderer.sprite.texture != null) textures.Add(renderer.sprite.texture);
                }

                _spriteHealth =
                    $"{renderers.Length} renderer(s), {withSprite} with a sprite, {textures.Count} distinct texture(s) · " +
                    DescribeTextureSample(textures);
            }

            if (_terrain != null)
            {
                _layerLine = DescribeLayers(_terrain);

                Color quarterTint = _terrain.BackTint;
                _colourLine =
                    $"back tint — shipped {FormatTint(_shippedBackTint)} · " +
                    $"quarter {FormatTint(quarterTint)}" +
                    (_applyBackTint ? string.Empty : " (switched off)") +
                    $" · front tint quarter {FormatTint(_terrain.FrontTint)}" +
                    (_applyFrontTint ? " (AS3 only — the shipped side has none)" : " (switched off)") +
                    $" · ambient {_ambient:0.00}";
            }

            MeasureRearPass();
            MeasureOverlays();

            if (!hasSprite || !hasQuarter)
            {
                _boundsLine = "bounds unavailable — one side has no renderer with a size yet";
                SampleRender();
                return;
            }

            // The sides are offset by the gap on purpose, so compare sizes and the offset-corrected
            // centre: a raw centre difference would just be the gap.
            float halfGap = _sideGap * 0.5f;
            Vector3 quarterCentre = quarterBounds.center - new Vector3(halfGap, 0f, 0f);
            Vector3 spriteCentre = spriteBounds.center + new Vector3(halfGap, 0f, 0f);
            Vector3 centreDelta = quarterCentre - spriteCentre;
            Vector3 sizeDelta = quarterBounds.size - spriteBounds.size;

            _boundsLine =
                $"sprite {spriteBounds.size.x:0.00}×{spriteBounds.size.y:0.00} · " +
                $"quarter {quarterBounds.size.x:0.00}×{quarterBounds.size.y:0.00} · " +
                $"Δsize ({sizeDelta.x:0.00}, {sizeDelta.y:0.00}) · " +
                $"Δcentre ({centreDelta.x:0.00}, {centreDelta.y:0.00})";

            SampleRender();
            SampleRoundTrip();
        }

        /// <summary>
        /// Counts the cells whose front material is a rear material, and the shapes the rear pass
        /// actually placed.
        ///
        /// <para><b>Why a census and not a picture.</b> Beams and catwalks are a handful of cells in a
        /// 1 200-cell room, and "the platform looks wrong" cannot say whether the cells are missing,
        /// drawn in the wrong layer, or drawn correctly and read as wrong because of the wall behind
        /// them. The two numbers separate those: cells without shapes means the pass did not run, and
        /// shapes on <c>rear:</c> layer keys means it ran in its own layer.</para>
        /// </summary>
        private void MeasureRearPass()
        {
            if (_room == null || _room.tiles == null || _materialDatabase == null)
            {
                _rearLine = "not measured — needs a room and the material database";
                return;
            }

            int cells = 0;
            var ids = new List<string>();

            for (int y = 0; y < _room.height; y++)
            {
                for (int x = 0; x < _room.width; x++)
                {
                    TileData tile = _room.tiles[x, y];
                    if (tile == null) continue;

                    string front = tile.GetFrontGraphic();
                    if (string.IsNullOrEmpty(front)) continue;

                    MaterialRenderEntry entry = _materialDatabase.GetFrontMaterial(front);
                    if (entry == null || !entry.isRear) continue;

                    cells++;
                    if (!ids.Contains(entry.id)) ids.Add(entry.id);
                }
            }

            int placed = 0;
            if (_terrain != null)
            {
                foreach (KeyValuePair<string, int> pair in _terrain.LayerTileCounts)
                {
                    if (pair.Key.StartsWith("rear:", StringComparison.Ordinal)) placed += pair.Value;
                }
            }

            ids.Sort(StringComparer.Ordinal);

            if (cells == 0)
            {
                _rearLine = "0 rear cell(s) in this room — nothing to compare";
                return;
            }

            _rearLine = string.Format(
                "{0} cell(s) on {1} rear material(s) [{2}] · {3} shape(s) placed · {4} @ {5}{6}",
                cells, ids.Count, string.Join(", ", ids), placed,
                MapSortingLayers.BackgroundDecor, RoomTerrainRenderer.RearOrder,
                placed == 0 ? " · NOTHING PLACED — the rear pass did not run" : string.Empty);
        }

        /// <summary>
        /// Counts the overlay cells in the room and the overlays the renderer actually placed.
        ///
        /// <para><b>"No ladders in this room" and "the overlay pass did not run" look identical in a
        /// picture.</b> The count separates them: 387 of 640 shipped rooms contain a ladder form, so a
        /// room with cells but zero placements is a broken pass, and a room with zero cells is a room
        /// without ladders. The ledge is the same number — <c>Location.as:583</c> bumps <c>vid</c> on
        /// the top step, so a missing overlay pass loses the ladder <i>and</i> its ledge together.</para>
        /// </summary>
        private void MeasureOverlays()
        {
            if (_room == null || _room.tiles == null)
            {
                _overlayLine = "not measured — needs a room";
                return;
            }

            int cells = 0;
            int ledges = 0;
            int shelves = 0;
            var frames = new SortedSet<int>();

            for (int y = 0; y < _room.height; y++)
            {
                for (int x = 0; x < _room.width; x++)
                {
                    TileData tile = _room.tiles[x, y];
                    if (tile == null) continue;

                    if (tile.visualId > 0) { cells++; frames.Add(tile.visualId); }
                    if (tile.visualId2 > 0) { cells++; frames.Add(tile.visualId2); }

                    // `isLedge` is set by TWO different rules and only one of them draws the ledge
                    // frame: `TileDecoder.ApplyLegacyStairPlacementRules` (the `++vid` bump on the top
                    // cell of a ladder run) and `form.shelf` (a beam, which has `vid == 0` and is
                    // therefore a material with no overlay at all — all six beam forms are
                    // `shelf='1' vid='0'`). Counting them together made "3 ledges" unreadable: it could
                    // be three caps, or one cap and two beams. Split them.
                    if (tile.isLedge)
                    {
                        if (tile.stairType != 0) ledges++;
                        else shelves++;
                    }
                }
            }

            int placed = _terrain != null ? _terrain.OverlaysPlaced : 0;
            string missing = _terrain != null && _terrain.MissingOverlays.Count > 0
                ? " · " + _terrain.MissingOverlays.Count + " frame(s) NOT IN THE ART ASSET (rebuild PFE/Art/Rebuild Room Terrain Art)"
                : string.Empty;

            if (cells == 0)
            {
                _overlayLine = "0 overlay cell(s) in this room — nothing to draw";
                return;
            }

            _overlayLine = string.Format(
                "{0} cell(s), {1} ledge cap(s), {2} shelf cell(s) · frame(s) [{3}] · {4} placed{5}{6}",
                cells, ledges, shelves, string.Join(", ", frames), placed, missing,
                placed == 0 && _drawOverlays
                    ? " · NOTHING PLACED — the overlay pass did not run"
                    : (_drawOverlays ? string.Empty : " (switched off)"));
        }

        /// <summary>
        /// Compares the brightness of the art the materials name against the brightness of the pixels
        /// the shipped compositor produced from it.
        ///
        /// <para><b>Why this specific comparison.</b> Both paths read the same 80 files — the
        /// <c>TileTextureLookup</c> asset points at <c>Art/TileTextures</c> and nowhere else, which was
        /// verified against all 14 617 guids in the project. So a shipped side that is black while the
        /// quarter side is textured cannot be a missing-texture problem: it has to be a difference in
        /// how the pixels are read. The compositor reads them through a <c>Graphics.Blit</c> into a
        /// linear <c>RenderTexture</c> and then <c>ReadPixels</c> into a texture created <b>without</b>
        /// <c>linear: true</c> — i.e. flagged sRGB while holding linear numbers. If that is what is
        /// happening, the composed mean lands near <c>source^2.2</c>, and this line says so in numbers
        /// instead of in an argument.</para>
        /// </summary>
        private void SampleRoundTrip()
        {
            if (_spriteRoot == null || _room == null || _textureLookup == null || _materialDatabase == null)
            {
                _roundTripLine = "not measured — needs both sides plus the texture lookup";
                _roundTripVerdict = "not measured";
                return;
            }

            // Composed side: weight each composed texture by how many renderers drew with it.
            var composedCounts = new Dictionary<Texture2D, int>();
            foreach (SpriteRenderer renderer in _spriteRoot.GetComponentsInChildren<SpriteRenderer>(true))
            {
                if (renderer == null || renderer.sprite == null || renderer.sprite.texture == null) continue;

                composedCounts.TryGetValue(renderer.sprite.texture, out int count);
                composedCounts[renderer.sprite.texture] = count + 1;
            }

            double composedSum = 0d;
            int composedWeight = 0;
            foreach (KeyValuePair<Texture2D, int> pair in composedCounts)
            {
                if (!TryMeanLuminance(pair.Key, out double mean)) continue;
                composedSum += mean * pair.Value;
                composedWeight += pair.Value;
            }

            // Source side: weight each named texture by how many cells use its material.
            var materialCells = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int y = 0; y < _room.height; y++)
            {
                for (int x = 0; x < _room.width; x++)
                {
                    TileData tile = _room.tiles[x, y];
                    if (tile == null) continue;

                    string front = tile.GetFrontGraphic();
                    if (string.IsNullOrEmpty(front)) continue;

                    materialCells.TryGetValue(front, out int count);
                    materialCells[front] = count + 1;
                }
            }

            double sourceSum = 0d;
            int sourceWeight = 0;
            int sampled = 0;

            foreach (KeyValuePair<string, int> pair in materialCells)
            {
                MaterialRenderEntry entry = _materialDatabase.GetFrontMaterial(pair.Key);
                if (entry == null) continue;

                Texture2D source = _textureLookup.GetTexture(entry.mainTexture);
                if (source == null || !TryMeanLuminance(source, out double mean)) continue;

                sourceSum += mean * pair.Value;
                sourceWeight += pair.Value;
                if (++sampled >= 8) break;
            }

            if (composedWeight == 0 || sourceWeight == 0)
            {
                _roundTripLine = "not measured — no readable composed or source texture";
                _roundTripVerdict = "not measured";
                return;
            }

            double composedMean = composedSum / composedWeight;
            double sourceMean = sourceSum / sourceWeight;
            double ratio = sourceMean > 0.0001d ? composedMean / sourceMean : 0d;
            double gammaPrediction = Math.Pow(sourceMean, 2.2d);

            // Two predictions, because the composed texture carries the shipped back tint as well as
            // the gamma: whichever one the composed mean lands nearer names the cause without the
            // reader having to divide anything. The tint average is the right multiplier for a mean
            // only if the tint is uniform across the composed textures, which it is — it comes from
            // the room, not from the tile.
            double tintMean = (_shippedBackTint.r + _shippedBackTint.g + _shippedBackTint.b) / 3d;
            double tintedPrediction = gammaPrediction * tintMean;

            _roundTripLine = string.Format(
                "source mean {0:0.000} → composed mean {1:0.000} · ratio {2:0.00} · source^2.2 = {3:0.000} · ×tint {4:0.000}",
                sourceMean, composedMean, ratio, gammaPrediction, tintedPrediction);

            _roundTripVerdict = DescribeRoundTripVerdict(
                composedMean, gammaPrediction, tintedPrediction, tintMean, _shippedCoveredPercent);
        }

        /// <summary>
        /// Names which model explains the composed mean, and says so only as far as the numbers go.
        ///
        /// <para><b>Why a verdict and not two numbers.</b> The previous version printed the composed
        /// mean next to <c>source^2.2</c> and left the comparison to the reader — who then has to
        /// remember that the shipped path also multiplies by the room tint, and that a residual of a
        /// few percent is inside the sampling error of a 24×24 grid. The arithmetic is the part a
        /// machine should do; the judgement that remains ("do I change a shipped colour space?") is
        /// the owner's.</para>
        ///
        /// <para>The residual is relative to the prediction, and the thresholds are wide on purpose: a
        /// sampled mean over a handful of textures is not a measurement to two decimals, and a verdict
        /// that flips on a 3 % difference would be a coin toss dressed as evidence.</para>
        /// </summary>
        private static string DescribeRoundTripVerdict(
            double composedMean,
            double gammaPrediction,
            double tintedPrediction,
            double tintMean,
            double shippedCoveredPercent = -1d)
        {
            // Before any colour model: is there a second picture at all? A shipped half with 0 %
            // coverage is a blank rectangle, and every statement about its brightness is a statement
            // about the clear colour. Saying "the dark shipped side" about it is the single most
            // misleading thing this readout can do, because it names a cause (a darkening stage) that
            // cannot exist in a frame where nothing was drawn.
            if (shippedCoveredPercent >= 0d && shippedCoveredPercent < 0.5d)
            {
                return "verdict: not comparable — the shipped side is blank (covered " +
                       shippedCoveredPercent.ToString("0.0") + "%), i.e. it drew nothing, so the " +
                       "composed/source ratio above is a comparison against an empty frame. Fix the " +
                       "shipped side first; colour space is not the question yet.";
            }

            if (composedMean <= 0.0001d)
                return "verdict: composed textures are black — the compositor produced nothing to compare";

            double gammaResidual = Relative(composedMean, gammaPrediction);
            double tintedResidual = Relative(composedMean, tintedPrediction);
            const double Fit = 0.15d;

            if (tintedResidual <= Fit && tintedResidual <= gammaResidual)
            {
                return string.Format(
                    "verdict: gamma round trip + back tint — composed matches source^2.2 × {0:0.00} within {1:0}%. " +
                    "The shipped side is not unlit; it is reading linear pixels as sRGB.",
                    tintMean, tintedResidual * 100d);
            }

            if (gammaResidual <= Fit)
            {
                return string.Format(
                    "verdict: gamma round trip only — composed matches source^2.2 within {0:0}% and the back tint " +
                    "is not in the composed texture. Same colour-space defect, no tint stage.",
                    gammaResidual * 100d);
            }

            if (gammaResidual < 1d)
            {
                return string.Format(
                    "verdict: partial darkening — composed is {0:0}% of source^2.2 and {1:0}% of source^2.2 × tint. " +
                    "Neither model fits; do not read the picture until this is explained.",
                    gammaResidual * 100d, tintedResidual * 100d);
            }

            return string.Format(
                "verdict: no round trip — composed is {0:0.00}× source^2.2, i.e. brighter than the gamma model " +
                "predicts. The dark shipped side has some other cause; stop looking at colour space.",
                gammaPrediction > 0.0001d ? composedMean / gammaPrediction : 0d);
        }

        /// <summary>Residual as a fraction of the prediction, guarded against a zero prediction.</summary>
        private static double Relative(double observed, double predicted)
        {
            if (predicted <= 0.0001d) return double.MaxValue;
            return Math.Abs(observed - predicted) / predicted;
        }

        /// <summary>Mean luminance of a readable texture, sampled on a coarse grid.</summary>
        private static bool TryMeanLuminance(Texture2D texture, out double mean)
        {
            mean = 0d;
            if (texture == null || !texture.isReadable) return false;

            int stepX = Mathf.Max(1, texture.width / 24);
            int stepY = Mathf.Max(1, texture.height / 24);
            double sum = 0d;
            int count = 0;

            try
            {
                for (int y = 0; y < texture.height; y += stepY)
                {
                    for (int x = 0; x < texture.width; x += stepX)
                    {
                        Color pixel = texture.GetPixel(x, y);
                        sum += (pixel.r + pixel.g + pixel.b) / 3d;
                        count++;
                    }
                }
            }
            catch (Exception)
            {
                return false;
            }

            if (count == 0) return false;

            mean = sum / count;
            return true;
        }

        /// <summary>
        /// Reads the rendered frame back and reports each half's mean colour and coverage.
        ///
        /// <para><b>This is what separates "dark" from "empty".</b> A side whose coverage is near zero
        /// is not drawing; a side with high coverage and a mean near the background is drawing
        /// something black. Those are different bugs with different fixes, and eyeballing a 400-pixel
        /// thumbnail cannot tell them apart — the thumbnail is the thing being questioned.</para>
        ///
        /// <para>Split at the midpoint between the two side centres rather than at half the frame,
        /// because the camera pans with <c>Offset</c> and the sides do not move with it.</para>
        /// </summary>
        private void SampleRender()
        {
            if (_previewRt == null || _previewCamera == null) return;

            Render();

            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = _previewRt;

            var texture = new Texture2D(_previewRt.width, _previewRt.height, TextureFormat.RGBA32, false);
            texture.ReadPixels(new Rect(0, 0, _previewRt.width, _previewRt.height), 0, 0);
            texture.Apply();
            RenderTexture.active = previous;

            Vector3 leftViewport = _previewCamera.WorldToViewportPoint(
                new Vector3(PreviewOffsetX - _sideGap * 0.5f, 0f, 0f));
            Vector3 rightViewport = _previewCamera.WorldToViewportPoint(
                new Vector3(PreviewOffsetX + _sideGap * 0.5f, 0f, 0f));

            int split = Mathf.Clamp(
                Mathf.RoundToInt((leftViewport.x + rightViewport.x) * 0.5f * texture.width),
                1, texture.width - 1);

            Color32[] pixels = texture.GetPixels32();
            int width = texture.width;
            DestroyImmediate(texture);

            Color clear = _previewCamera.backgroundColor;
            double leftR = 0, leftG = 0, leftB = 0, rightR = 0, rightG = 0, rightB = 0;
            int leftCount = 0, rightCount = 0, leftCovered = 0, rightCovered = 0;

            for (int y = 0; y < _previewRt.height; y += 2)
            {
                int row = y * width;
                for (int x = 0; x < width; x += 2)
                {
                    Color32 pixel = pixels[row + x];
                    float r = pixel.r / 255f;
                    float g = pixel.g / 255f;
                    float b = pixel.b / 255f;

                    bool covered = Mathf.Abs(r - clear.r) > 0.02f ||
                                   Mathf.Abs(g - clear.g) > 0.02f ||
                                   Mathf.Abs(b - clear.b) > 0.02f;

                    if (x < split)
                    {
                        leftR += r; leftG += g; leftB += b; leftCount++;
                        if (covered) leftCovered++;
                    }
                    else
                    {
                        rightR += r; rightG += g; rightB += b; rightCount++;
                        if (covered) rightCovered++;
                    }
                }
            }

            if (leftCount == 0 || rightCount == 0)
            {
                _pictureLine = "could not split the frame into two sides";
                return;
            }

            _shippedCoveredPercent = 100.0 * leftCovered / leftCount;

            _pictureLine = string.Format(
                "left rgb {0:0.00}/{1:0.00}/{2:0.00} covered {3:0.0}%  ·  right rgb {4:0.00}/{5:0.00}/{6:0.00} covered {7:0.0}%",
                leftR / leftCount, leftG / leftCount, leftB / leftCount, _shippedCoveredPercent,
                rightR / rightCount, rightG / rightCount, rightB / rightCount, 100.0 * rightCovered / rightCount);
        }

        private static string FormatTint(Color tint)
        {
            return string.Format("{0:0.00}/{1:0.00}/{2:0.00}", tint.r, tint.g, tint.b);
        }

        private static string DescribeLayers(RoomTerrainRenderer terrain)
        {
            if (terrain.LayerTileCounts.Count == 0) return "no layers placed";

            var rows = new List<KeyValuePair<string, int>>(terrain.LayerTileCounts);
            rows.Sort((left, right) => right.Value.CompareTo(left.Value));

            var builder = new StringBuilder();
            builder.Append(rows.Count).Append(" layer(s), shapes per layer: ");

            int shown = 0;
            foreach (KeyValuePair<string, int> row in rows)
            {
                if (shown++ > 0) builder.Append(" · ");
                builder.Append(row.Key).Append(' ').Append(row.Value);
                if (shown >= 6) break;
            }

            return builder.ToString();
        }

        /// <summary>
        /// Mean colour of one composed texture. A black room with visible silhouettes is either a
        /// compositor that produced black pixels or a shader that multiplied them away, and those are
        /// different bugs — this is the number that tells them apart.
        /// </summary>
        private static string DescribeTextureSample(HashSet<Texture2D> textures)
        {
            foreach (Texture2D texture in textures)
            {
                if (texture == null || !texture.isReadable) continue;

                int stepX = Mathf.Max(1, texture.width / 24);
                int stepY = Mathf.Max(1, texture.height / 24);
                double red = 0d, green = 0d, blue = 0d, alpha = 0d;
                int count = 0;

                try
                {
                    for (int y = 0; y < texture.height; y += stepY)
                    {
                        for (int x = 0; x < texture.width; x += stepX)
                        {
                            Color pixel = texture.GetPixel(x, y);
                            red += pixel.r;
                            green += pixel.g;
                            blue += pixel.b;
                            alpha += pixel.a;
                            count++;
                        }
                    }
                }
                catch (Exception)
                {
                    continue;
                }

                if (count == 0) continue;

                return string.Format(
                    "sampled '{0}' {1}×{2}: mean rgb {3:0.00}/{4:0.00}/{5:0.00}, mean alpha {6:0.00}",
                    texture.name, texture.width, texture.height,
                    red / count, green / count, blue / count, alpha / count);
            }

            return "no readable composed texture to sample (atlas or GPU-only)";
        }

        private static bool TryUnionBounds(GameObject root, out Bounds bounds)
        {
            bounds = default;
            if (root == null) return false;

            bool any = false;

            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null) continue;

                Bounds current = renderer.bounds;
                if (current.size.x <= 0f && current.size.y <= 0f) continue;

                if (!any)
                {
                    bounds = current;
                    any = true;
                }
                else
                {
                    bounds.Encapsulate(current);
                }
            }

            return any;
        }

        /// <summary>
        /// Everything measured, in one block, in the order that answers the questions the picture raises:
        /// is each side drawing at all, what colour is it, where is it, and which layer owns an artefact.
        /// </summary>
        private void LogTriage()
        {
            Debug.Log("[RoomTerrainAB] ── triage ──");
            Debug.Log($"[RoomTerrainAB] sprite health  : {_spriteHealth}");
            Debug.Log($"[RoomTerrainAB] sprite material : {_spriteMaterialNote}");
            Debug.Log($"[RoomTerrainAB] quarter layers : {_layerLine}");
            Debug.Log($"[RoomTerrainAB] rear pass      : {_rearLine}");
            Debug.Log($"[RoomTerrainAB] overlays       : {_overlayLine}");

            // A lit sprite in a preview with no light draws black at the right shape, which reads as
            // "the art is wrong" when it is the material. The game is not black because SampleScene
            // has a Global Light2D (layer 0, white, intensity 1) — an identity light, so lit and unlit
            // agree there. This camera culls to its own layer, so that light is culled with it.
            string overlayShader = _terrain != null ? _terrain.OverlayShaderName : "not built";
            bool overlayLit = overlayShader.IndexOf("Lit", System.StringComparison.OrdinalIgnoreCase) >= 0
                           && overlayShader.IndexOf("Unlit", System.StringComparison.OrdinalIgnoreCase) < 0;

            Debug.Log(
                $"[RoomTerrainAB] overlay shader : {overlayShader}" +
                (overlayLit
                    ? " — LIT, and this camera culls the scene's Global Light2D, so every overlay draws black here. The art is fine; the material is not."
                    : " (unlit — the sprite shows its own colours, with or without a light)"));

            // Layer *and* order together, because a big order inside a layer that sorts underneath is
            // still underneath. The walls are BackgroundTiles order 0 (back) and MainTiles order 0
            // (front), so an overlay only counts as "above the walls" when it is on MainTiles at > 0.
            Debug.Log(
                $"[RoomTerrainAB] overlay sort   : {(_terrain != null ? _terrain.OverlaySortKey : "not built")}; " +
                "walls are BackgroundTiles order 0 (back) and MainTiles order 0 (front).");
            Debug.Log($"[RoomTerrainAB] registration   : {_boundsLine}");
            Debug.Log($"[RoomTerrainAB] picture        : {_pictureLine}");
            Debug.Log($"[RoomTerrainAB] colour         : {_colourLine}");
            Debug.Log($"[RoomTerrainAB] round-trip     : {_roundTripLine}");
            Debug.Log($"[RoomTerrainAB] verdict        : {_roundTripVerdict}");

            if (_terrain != null)
            {
                foreach (KeyValuePair<string, int> pair in _terrain.LayerTileCounts)
                {
                    Debug.Log($"[RoomTerrainAB] layer '{pair.Key}' placed {pair.Value} shape(s)");
                }
            }

            if (_room != null)
            {
                int solid = 0;
                int withFront = 0;
                int withBack = 0;

                for (int y = 0; y < _room.height; y++)
                {
                    for (int x = 0; x < _room.width; x++)
                    {
                        TileData tile = _room.tiles[x, y];
                        if (tile == null) continue;
                        if (tile.physicsType == TilePhysicsType.Wall) solid++;
                        if (!string.IsNullOrEmpty(tile.GetFrontGraphic())) withFront++;
                        if (!string.IsNullOrEmpty(tile.GetBackGraphic())) withBack++;
                    }
                }

                Debug.Log(
                    $"[RoomTerrainAB] room cells    : {_room.width}×{_room.height} = {_room.width * _room.height}, " +
                    $"{solid} wall(s), {withFront} with a front graphic, {withBack} with a back graphic");

                // The mask fit factor is the one number that was wrong while every fixture was green, so
                // it goes in the log rather than being inferred from a screenshot: a mask drawn at twice
                // its slot repeats the material texture inside it, which reads as "the beam is drawn
                // three times" and looks like a placement bug.
                RoomTerrainArt.MaskEntry tileMask = _art != null ? _art.GetMask("TileMask") : null;
                Sprite tileMaskFrame = tileMask != null && tileMask.IsWhole
                    ? _art.Frame(tileMask.clip, RoomQuarterRules.Inside)
                    : null;
                if (tileMaskFrame != null)
                {
                    RoomTerrainArt.FrameSet set = _art.GetFrameSet(tileMask.clip);
                    double zoom = set != null ? set.rasterZoom : 0d;
                    float cellSize = RoomTerrainGeometry.DefaultCellSize;
                    float scale = RoomTerrainGeometry.SpriteScale(
                        tileMaskFrame.pixelsPerUnit, cellSize, _art.CellPixels, zoom);
                    float units = tileMaskFrame.rect.width / tileMaskFrame.pixelsPerUnit * scale;

                    Debug.Log(
                        $"[RoomTerrainAB] mask scale    : {scale:0.###} for TileMask " +
                        $"({tileMaskFrame.rect.width:0}px @ {tileMaskFrame.pixelsPerUnit:0} ppu, zoom {zoom:0}, " +
                        $"cellPixels {_art.CellPixels}) → {units:0.###} units; a cell is {cellSize:0.###}. " +
                        (Mathf.Approximately(scale, 1f)
                            ? "1.000 means the fit factor is NOT applied — every mask draws at its import size."
                            : "a whole-cell mask must read one cell here."));

                    // The quantity the owner actually counts by eye. The shader samples the material by
                    // *world position*, so "the beam is duplicated above and below" is this quotient —
                    // mask span ÷ texture period — and never a placement count. 1 is correct.
                    float texelsPerMeter = cellSize > 0f ? _art.CellPixels / cellSize : _art.CellPixels;
                    float period = RoomTerrainGeometry.MaterialPeriod(_art.CellPixels, texelsPerMeter);
                    float periods = RoomTerrainGeometry.MaterialPeriods(units, period);

                    Debug.Log(
                        $"[RoomTerrainAB] mask periods  : TileMask covers {units / cellSize:0.##} cell(s); " +
                        $"a one-cell texture (e.g. tShelf, {_art.CellPixels}px) repeats every {period:0.###} units " +
                        $"at {texelsPerMeter:0.#} texels/unit → {periods:0.##} copy/copies of the material. " +
                        (periods > 1.05f
                            ? "More than 1 means the mask is bigger than its cell, so the surface is drawn again above and below it."
                            : "1 is correct: one beam cell, one girder."));
                }
            }
        }

        // ─── Export / logging ────────────────────────────────────

        private void ExportFrame()
        {
            if (!_sceneReady || _previewRt == null)
            {
                _status = "Set up the preview first.";
                return;
            }

            Render();

            Directory.CreateDirectory(Path.GetFullPath(ExportRoot));
            string path = Path.Combine(ExportRoot, $"room_{_roomTemplate.GetContentId()}_ab.png");

            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = _previewRt;

            var texture = new Texture2D(_previewRt.width, _previewRt.height, TextureFormat.RGBA32, false);
            texture.ReadPixels(new Rect(0, 0, _previewRt.width, _previewRt.height), 0, 0);
            texture.Apply();

            RenderTexture.active = previous;

            byte[] png = texture.EncodeToPNG();
            DestroyImmediate(texture);

            File.WriteAllBytes(Path.GetFullPath(path), png);
            AssetDatabase.Refresh();

            _status = $"Exported {path}";
        }

        private void LogComparison()
        {
            Debug.Log("[RoomTerrainAB] === shipped compositor vs quarter tilemaps ===");
            Debug.Log($"[RoomTerrainAB] room        : {_roomTemplate.GetContentId()} {_room.width}x{_room.height}");
            Debug.Log($"[RoomTerrainAB] cell size   : {RoomTerrainGeometry.DefaultCellSize} units");
            Debug.Log($"[RoomTerrainAB] sprite side : {_spriteStats}");
            Debug.Log($"[RoomTerrainAB] sprite health: {_spriteHealth}");
            Debug.Log($"[RoomTerrainAB] sprite material: {_spriteMaterialNote}");
            Debug.Log($"[RoomTerrainAB] quarter side: {_quarterStats}");
            Debug.Log($"[RoomTerrainAB] registration : {_boundsLine}");
            Debug.Log($"[RoomTerrainAB] picture      : {_pictureLine}");
            Debug.Log($"[RoomTerrainAB] round-trip   : {_roundTripLine}");
            Debug.Log($"[RoomTerrainAB] verdict      : {_roundTripVerdict}");
            Debug.Log($"[RoomTerrainAB] rear pass    : {_rearLine}");
            Debug.Log($"[RoomTerrainAB] overlays     : {_overlayLine}");
            Debug.Log($"[RoomTerrainAB] cross-check : {_crossCheckSummary}");

            if (_terrain == null) return;

            foreach (KeyValuePair<string, int> pair in _terrain.MissingMasks)
            {
                Debug.LogWarning($"[RoomTerrainAB] mask '{pair.Key}' requested {pair.Value} time(s) but is not in the art asset.");
            }

            foreach (KeyValuePair<string, int> pair in _terrain.MissingMaterials)
            {
                Debug.LogWarning($"[RoomTerrainAB] '{pair.Key}' has no material render entry ({pair.Value} cell(s)).");
            }
        }

        /// <summary>
        /// Puts every preview object on the isolated layer and marks it unsaveable.
        ///
        /// <para>Both halves matter and neither inherits: the layer is what the preview camera culls to,
        /// and <c>HideAndDontSave</c> is what stops a Ctrl+S while this window is open from writing a
        /// room's worth of Tilemaps into the open scene. The renderer creates its layers during
        /// <c>Build</c>, so this has to run afterwards.</para>
        /// </summary>
        private static void ApplyPreviewFlags(Transform root)
        {
            if (root == null) return;

            root.gameObject.layer = PreviewLayer;
            root.gameObject.hideFlags = HideFlags.HideAndDontSave;

            for (int i = 0; i < root.childCount; i++)
            {
                ApplyPreviewFlags(root.GetChild(i));
            }
        }
    }
}
#endif
