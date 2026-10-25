#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using PFE.Character;
using PFE.Data.Definitions;
using UnityEditor;
using UnityEngine;

namespace PFE.Editor.CharacterRendering
{
    /// <summary>
    /// Side-by-side presenter for the two character renderers: the shipped
    /// <see cref="CharacterSpriteAssembler"/> on the left, the quad renderer
    /// (<see cref="CharacterQuadAssembler"/>) on the right, driven from the same definition, the same
    /// appearance and the same frame.
    ///
    /// <para><b>Why a separate window rather than a mode in the existing preview.</b> The existing
    /// window exists to inspect one renderer's output and carries per-part overrides for that job.
    /// This one exists to answer a comparison question — "is the other pipeline actually better, and
    /// which of its four differences is responsible?" — so it needs the two side by side, the switches
    /// that separate the differences, and the measurement that says whether the pivot hypothesis is
    /// real. Mixing both jobs into one window would make each of them harder to read.</para>
    ///
    /// <para><b>The pivot switch is a falsification instrument, not a preference.</b>
    /// <c>docs/RenderingPipeline_Research/07</c> §3 concludes that the importer normalises
    /// <c>pivotNormalized</c> against the full PNG while <c>Sprite.Create</c> reads it against the
    /// trimmed rect. That conclusion rests on one assumption and would move parts by up to 32 px if
    /// acted on wrongly. With <see cref="CharacterQuadAssembler.CompensatePivot"/> off, the quad
    /// renderer uses the same rectangle <c>Sprite.Create</c> uses — so the two sides must agree to the
    /// pixel. Turn it on and watch which parts move: that movement <i>is</i> the error, measured on
    /// screen rather than derived.</para>
    /// </summary>
    public sealed class CharacterQuadPreviewWindow : EditorWindow
    {
        const int PreviewLayer = 31;
        const float PreviewOffsetX = 1000f;
        const string ExportRoot = "Assets/_PFE/Art/Character/QuadCompare";

        /// <summary>
        /// How far apart the two sides may be, in screen pixels, before a part counts as disagreeing.
        /// Sub-pixel is below what any framing here can show, so it is not a difference the owner could
        /// act on — but it is also not zero, because the two paths round through different arithmetic.
        /// </summary>
        const float MismatchPixels = 0.5f;

        /// <summary>
        /// Rows printed by <see cref="DescribePartDiff"/>. The list is sorted worst-first, so the cut
        /// only ever hides agreement.
        /// </summary>
        const int MaxPartDiffRows = 8;
        const string DefinitionPath = "Assets/_PFE/Data/Resources/Characters/PlayerAnimationDefinition.asset";
        const string StyleDataPath = "Assets/_PFE/Data/Resources/Character/CharacterStyleData.asset";

        // ─── Data ────────────────────────────────────────────────
        CharacterAnimationDefinition _animDef;
        CharacterStyleData _styleData;
        string[] _stateNames = Array.Empty<string>();
        int _selectedStateIdx;
        int _currentFrame;
        bool _isPlaying;
        float _playbackSpeed = 1f;
        double _lastFrameTime;

        // ─── Preview scene ───────────────────────────────────────
        GameObject _root;
        GameObject _spriteRoot;
        GameObject _quadRoot;
        CharacterSpriteAssembler _spriteAssembler;
        CharacterQuadAssembler _quadAssembler;
        Camera _previewCamera;
        RenderTexture _previewRt;
        int _previewSize = 512;

        // ─── View ────────────────────────────────────────────────
        float _sideGap = 2.2f;
        float _cameraZoom = 2.0f;
        Vector2 _cameraOffset;
        bool _showSpriteSide = true;
        bool _showQuadSide = true;

        /// <summary>
        /// What the game camera actually shows, for the "Game framing" button.
        ///
        /// <para><b>Why this button exists.</b> <see cref="FrameContent"/> fits the camera to the
        /// character's own bounds, and a character is about 0.6 world units tall — so a 512 px target
        /// lands at 746 px/unit. The game renders the same sprite at 108 px/unit (1080 px over an
        /// orthographic size of 5). That is a <b>6.9x loupe</b>: every blocky edge in the window is
        /// magnified seven times beyond anything the player ever sees, and judging "is this art good
        /// enough" at that framing answers a question nobody asked.</para>
        ///
        /// <para>The two numbers below are the game's, not preferences. If the game camera changes,
        /// these must change with it — they are the definition of "1:1 with the game", and a wrong
        /// value here makes the button quietly misleading rather than obviously broken.</para>
        /// </summary>
        float _gameOrthoSize = 5f;
        int _gameScreenHeight = 1080;

        // ─── Quad renderer switches ──────────────────────────────
        bool _compensatePivot = true;
        bool _bilinear = true;

        /// <summary>
        /// Whether the quad side packs every part into one atlas. Only the quad side has the switch:
        /// the shipped path draws <see cref="SpriteRenderer"/>s, whose batching is Unity's business and
        /// whose textures are the imported assets, so it cannot join this atlas without a
        /// <c>SpriteAtlas</c> asset. Keeping the switch here is what lets the draw-call difference be
        /// seen as a number rather than argued about.
        /// </summary>
        // Off by default since 2026-10-21: with the atlas on, the quad character draws flat tan
        // rectangles where parts should be (docs/CharacterImportPipeline/04). One material per part is
        // what the shipped sprite path already pays, so leaving it off costs nothing measurable and
        // makes the A/B compare the two *renderers* instead of one renderer and one atlas bug. Flip it
        // on to reproduce, and dump the atlas from the row below when you do.
        bool _useAtlas;

        /// <summary>
        /// The costume both sides wear. Empty is <see cref="CharacterVisualContext.Default"/>, which is
        /// what this window passed unconditionally until the atlas landed.
        ///
        /// <para><b>Why the field exists.</b> With an empty <c>armorId</c> the assembler packs 82 regions
        /// and the worn set's override sprites are never copied, so the armour half of the atlas could
        /// not be looked at from the one tool that can look at it. A fixture that cannot reach a code
        /// path is a fixture that proves nothing about it.</para>
        ///
        /// <para>Both sides are given the same id, so the A/B stays a comparison: setting a costume
        /// changes what the character <i>is</i>, not which renderer draws it.</para>
        /// </summary>
        string _armorId = string.Empty;

        /// <summary>
        /// The id last pushed into the two assemblers. Tracked separately from <see cref="_armorId"/>
        /// because the field is applied on every keystroke, and rebuilding a 4 MB atlas three times
        /// while a four-letter id is typed is work nobody asked for.
        /// </summary>
        string _appliedArmorId = string.Empty;
        bool _useAmbient;
        int _roomDarkness = 60;

        // ─── Diagnostics ─────────────────────────────────────────
        bool _showPivotTable;
        Vector2 _pivotScroll;
        bool _sceneReady;
        string _status = "";
        Vector2 _mainScroll;

        /// <summary>
        /// Deferred-render flag. <c>Camera.Render()</c> is issued from <see cref="OnEditorUpdate"/>,
        /// never from <c>OnGUI</c>: rendering a camera while IMGUI is painting is the one place where
        /// the render target can come back half-written, and a half-written target is indistinguishable
        /// from "the character is black" on screen.
        /// </summary>
        bool _renderPending;
        bool _framePending;
        bool _diagnosePending;
        bool _showDiagnostics;
        Vector2 _diagScroll;
        string _diagnostics = "";

        /// <summary>
        /// Unlit material forced onto the shipped side, plus the note saying what it replaced.
        /// See <see cref="EnsureUnlitPreviewMaterial"/> for why the shipped side renders black without it.
        /// </summary>
        Material _previewUnlit;
        string _materialNote = "";

        [MenuItem("PFE/Art/Character Renderer A/B (Quads)")]
        public static void ShowWindow()
        {
            var window = GetWindow<CharacterQuadPreviewWindow>("Renderer A/B");
            window.minSize = new Vector2(760, 560);
        }

        void OnEnable()
        {
            EditorApplication.update += OnEditorUpdate;
            TryLoadAssets();
        }

        void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            CleanupScene();
        }

        void OnDestroy()
        {
            CleanupScene();
        }

        // ─── Assets ──────────────────────────────────────────────

        void TryLoadAssets()
        {
            if (_animDef == null)
            {
                _animDef = AssetDatabase.LoadAssetAtPath<CharacterAnimationDefinition>(DefinitionPath);
            }

            if (_styleData == null)
            {
                _styleData = AssetDatabase.LoadAssetAtPath<CharacterStyleData>(StyleDataPath);
            }

            if (_animDef != null && _stateNames.Length == 0)
            {
                RefreshStateList();
            }
        }

        void RefreshStateList()
        {
            if (_animDef?.stateClips == null)
            {
                _stateNames = Array.Empty<string>();
                return;
            }

            var names = new List<string>(_animDef.stateClips.Length);
            foreach (CharacterStateClip clip in _animDef.stateClips)
            {
                names.Add(clip.stateName);
            }

            _stateNames = names.ToArray();
            _selectedStateIdx = Mathf.Clamp(_selectedStateIdx, 0, Mathf.Max(0, _stateNames.Length - 1));
        }

        // ─── Playback ────────────────────────────────────────────

        void OnEditorUpdate()
        {
            // Framing and rendering happen here, outside the GUI pass. See _renderPending.
            if (_sceneReady)
            {
                if (_framePending)
                {
                    _framePending = false;
                    FrameContent();
                }

                if (_diagnosePending)
                {
                    _diagnosePending = false;
                    _renderPending = false;
                    RenderNow();
                    BuildDiagnostics();
                    Repaint();
                }
                else if (_renderPending)
                {
                    _renderPending = false;
                    RenderNow();
                    Repaint();
                }
            }

            if (!_isPlaying || _animDef == null || _stateNames.Length == 0)
            {
                return;
            }

            CharacterStateClip clip = _animDef.stateClips[_selectedStateIdx];
            if (clip.frameCount <= 1) return;

            double now = EditorApplication.timeSinceStartup;
            double frameDuration = 1.0 / (_animDef.frameRate * Mathf.Max(0.01f, _playbackSpeed));

            if (now - _lastFrameTime < frameDuration)
            {
                return;
            }

            _lastFrameTime = now;
            _currentFrame++;

            int loopEnd = clip.EffectiveLoopEnd;
            if (_currentFrame >= loopEnd)
            {
                switch (clip.loopMode)
                {
                    case AnimationLoopMode.Loop:
                    case AnimationLoopMode.LoopRange:
                        _currentFrame = clip.loopStartFrame;
                        break;
                    case AnimationLoopMode.ClampForever:
                        _currentFrame = Mathf.Max(0, loopEnd - 1);
                        _isPlaying = false;
                        break;
                    case AnimationLoopMode.Manual:
                        _currentFrame = 0;
                        break;
                }
            }

            ApplyFrame();
            Repaint();
        }

        // ─── GUI ─────────────────────────────────────────────────

        void OnGUI()
        {
            _mainScroll = EditorGUILayout.BeginScrollView(_mainScroll);

            DrawHeader();

            if (_animDef == null)
            {
                EditorGUILayout.HelpBox(
                    "No CharacterAnimationDefinition loaded. Run the SWF character import first, or drag " +
                    "the asset into the field above.",
                    MessageType.Warning);
                EditorGUILayout.EndScrollView();
                return;
            }

            if (_stateNames.Length == 0)
            {
                EditorGUILayout.HelpBox(
                    "The animation definition has no state clips, so there is nothing to compare.",
                    MessageType.Warning);
                EditorGUILayout.EndScrollView();
                return;
            }

            EditorGUILayout.BeginHorizontal();

            EditorGUILayout.BeginVertical(GUILayout.Width(position.width * 0.56f));
            DrawPreviewPanel();
            EditorGUILayout.EndVertical();

            EditorGUILayout.BeginVertical();
            DrawRendererSwitches();
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);
            DrawPivotTable();

            if (!string.IsNullOrEmpty(_status))
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.HelpBox(_status, MessageType.Info);
            }

            EditorGUILayout.EndScrollView();
        }

        void DrawHeader()
        {
            EditorGUILayout.LabelField("Character Renderer A/B — Sprite (shipped) vs Quads (parallel)", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            _animDef = (CharacterAnimationDefinition)EditorGUILayout.ObjectField(
                "Animation Definition", _animDef, typeof(CharacterAnimationDefinition), false);
            _styleData = (CharacterStyleData)EditorGUILayout.ObjectField(
                "Style Data", _styleData, typeof(CharacterStyleData), false);
            if (EditorGUI.EndChangeCheck())
            {
                RefreshStateList();
                CleanupScene();
            }

            if (_animDef == null || _stateNames.Length == 0)
            {
                return;
            }

            EditorGUI.BeginChangeCheck();
            _selectedStateIdx = EditorGUILayout.Popup("State", _selectedStateIdx, _stateNames);
            if (EditorGUI.EndChangeCheck())
            {
                _currentFrame = 0;
                _isPlaying = false;
                ApplyFrame();
            }

            CharacterStateClip clip = _animDef.stateClips[_selectedStateIdx];
            EditorGUILayout.LabelField(
                $"Frames: {clip.frameCount}  |  Loop: {clip.loopMode}  |  Run Mane: {clip.useRunMane}  |  Run Tail: {clip.useRunTail}");
        }

        void DrawPreviewPanel()
        {
            EditorGUILayout.LabelField("Preview", EditorStyles.boldLabel);

            if (_sceneReady && _previewRt != null)
            {
                float available = Mathf.Min(position.width * 0.54f, _previewSize);
                Rect rect = GUILayoutUtility.GetRect(available, available);
                EditorGUI.DrawPreviewTexture(rect, _previewRt, null, ScaleMode.ScaleToFit);
            }
            else
            {
                Rect rect = GUILayoutUtility.GetRect(240, 240);
                EditorGUI.DrawRect(rect, new Color(0.15f, 0.15f, 0.15f));
                GUI.Label(rect, "No preview — click Setup", EditorStyles.centeredGreyMiniLabel);
            }

            EditorGUI.BeginChangeCheck();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Zoom", GUILayout.Width(40));
            _cameraZoom = EditorGUILayout.Slider(_cameraZoom, 0.5f, 8f);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Gap", GUILayout.Width(40));
            _sideGap = EditorGUILayout.Slider(_sideGap, 0.4f, 6f);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Offset", GUILayout.Width(40));
            _cameraOffset = EditorGUILayout.Vector2Field("", _cameraOffset, GUILayout.Width(180));
            if (GUILayout.Button("Reset", GUILayout.Width(50)))
            {
                _cameraOffset = Vector2.zero;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Render Size", GUILayout.Width(70));
            _previewSize = EditorGUILayout.IntSlider(_previewSize, 128, 1024);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            _showSpriteSide = EditorGUILayout.ToggleLeft("Sprite side", _showSpriteSide, GUILayout.Width(90));
            _showQuadSide = EditorGUILayout.ToggleLeft("Quad side", _showQuadSide, GUILayout.Width(90));
            EditorGUILayout.EndHorizontal();

            bool changed = EditorGUI.EndChangeCheck();

            if (!_sceneReady)
            {
                if (GUILayout.Button("Setup Preview", GUILayout.Height(28)))
                {
                    SetupScene();
                    ApplyFrame();
                }
            }
            else
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Rebuild", GUILayout.Height(22)))
                {
                    CleanupScene();
                    SetupScene();
                    ApplyFrame();
                }
                if (GUILayout.Button("Export A/B PNG", GUILayout.Height(22)))
                {
                    ExportFrame();
                }
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Frame content", GUILayout.Height(22)))
                {
                    _framePending = true;
                    RequestRender();
                }
                if (GUILayout.Button(new GUIContent("Game framing",
                        "Match the game's pixels-per-unit instead of filling the window. The window is a " +
                        "loupe: at 512 px over this character it shows ~7x the magnification the game " +
                        "does, so blockiness here is not blockiness on screen."), GUILayout.Height(22)))
                {
                    ApplyGameFraming();
                }
                if (GUILayout.Button("Diagnose", GUILayout.Height(22)))
                {
                    Diagnose();
                }
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();
                _gameOrthoSize = EditorGUILayout.FloatField("Game ortho", _gameOrthoSize, GUILayout.Width(150));
                _gameScreenHeight = EditorGUILayout.IntField("Game height", _gameScreenHeight, GUILayout.Width(150));
                EditorGUILayout.EndHorizontal();
            }

            if (changed && _sceneReady)
            {
                ApplyViewOptions();
                RequestRender();
            }

            // ── Diagnostics ──
            EditorGUILayout.Space(4);
            _showDiagnostics = EditorGUILayout.Foldout(_showDiagnostics, "Diagnostics", true);
            if (_showDiagnostics)
            {
                if (string.IsNullOrEmpty(_diagnostics))
                {
                    EditorGUILayout.HelpBox(
                        "Press Diagnose. It reports what each side actually built (renderers, bounds, " +
                        "shader), whether the camera's frustum contains them, and what is really in the " +
                        "render target — so a black preview is named instead of guessed at.",
                        MessageType.None);
                }
                else
                {
                    _diagScroll = EditorGUILayout.BeginScrollView(_diagScroll, GUILayout.Height(150));
                    EditorGUILayout.TextArea(_diagnostics, EditorStyles.wordWrappedMiniLabel);
                    EditorGUILayout.EndScrollView();
                }
            }

            // ── Playback ──
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Playback", EditorStyles.boldLabel);

            CharacterStateClip clip = _animDef.stateClips[_selectedStateIdx];

            EditorGUI.BeginChangeCheck();
            _currentFrame = EditorGUILayout.IntSlider("Frame", _currentFrame, 0, Mathf.Max(0, clip.frameCount - 1));
            if (EditorGUI.EndChangeCheck())
            {
                _isPlaying = false;
                ApplyFrame();
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("|<", GUILayout.Width(30)))
            {
                _currentFrame = 0;
                _isPlaying = false;
                ApplyFrame();
            }
            if (GUILayout.Button("<", GUILayout.Width(30)))
            {
                _currentFrame = Mathf.Max(0, _currentFrame - 1);
                _isPlaying = false;
                ApplyFrame();
            }
            if (GUILayout.Button(_isPlaying ? "||" : ">", GUILayout.Width(40)))
            {
                _isPlaying = !_isPlaying;
                _lastFrameTime = EditorApplication.timeSinceStartup;
            }
            if (GUILayout.Button(">", GUILayout.Width(30)))
            {
                _currentFrame = Mathf.Min(clip.frameCount - 1, _currentFrame + 1);
                _isPlaying = false;
                ApplyFrame();
            }
            if (GUILayout.Button(">|", GUILayout.Width(30)))
            {
                _currentFrame = clip.frameCount - 1;
                _isPlaying = false;
                ApplyFrame();
            }

            EditorGUILayout.LabelField("Speed", GUILayout.Width(40));
            _playbackSpeed = EditorGUILayout.Slider(_playbackSpeed, 0.1f, 3f);
            EditorGUILayout.EndHorizontal();
        }

        void DrawRendererSwitches()
        {
            EditorGUILayout.LabelField("Quad renderer", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();

            _compensatePivot = EditorGUILayout.ToggleLeft(
                "Compensate pivot (full-PNG registration)", _compensatePivot);

            if (!_compensatePivot)
            {
                EditorGUILayout.HelpBox(
                    "Off: the quad side uses the trimmed rect, exactly like Sprite.Create. With this off " +
                    "the two sides must agree to the pixel — any disagreement is a bug in the quad path.",
                    MessageType.None);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "On: the registration point is resolved against the full PNG. Parts that were " +
                    "alpha-trimmed will visibly shift; that shift is the measured error.",
                    MessageType.None);
            }

            _bilinear = EditorGUILayout.ToggleLeft("Bilinear sampling (GPU-side copy)", _bilinear);

            _armorId = EditorGUILayout.TextField(
                new GUIContent(
                    "Costume (armorId)",
                    "Empty draws no armour, which is CharacterVisualContext.Default. A set id from " +
                    "PlayerAnimationDefinition — pip, tre, chitin, … — makes both sides wear it and adds " +
                    "its override sprites to the atlas (82 regions with none, 92 with pip)."),
                _armorId);

            _useAtlas = EditorGUILayout.ToggleLeft("One atlas + one material (quad side)", _useAtlas);
            using (new EditorGUI.DisabledScope(!_sceneReady || _quadAssembler == null))
            {
                CharacterQuadAtlas atlas = _quadAssembler != null ? _quadAssembler.Atlas : null;
                if (!_useAtlas)
                {
                    EditorGUILayout.LabelField("Atlas off: one material per part texture, as shipped.",
                        EditorStyles.miniLabel);
                }
                else if (atlas == null || !atlas.IsReady)
                {
                    EditorGUILayout.LabelField("Atlas: not built — the quad side falls back per texture.",
                        EditorStyles.miniLabel);
                }
                else
                {
                    EditorGUILayout.LabelField(
                        $"Atlas {atlas.Width}x{atlas.Height}: {atlas.RegionCount} regions, " +
                        $"{atlas.SkippedCount} skipped, filter {atlas.Texture.filterMode}, " +
                        $"{atlas.MipCount} mip level(s)",
                        EditorStyles.miniLabel);

                    if (GUILayout.Button("Dump atlas + region table to Temp/quad_atlas_dump"))
                    {
                        Debug.Log(DumpAtlas(atlas));
                    }
                }
            }

            EditorGUILayout.Space(2);
            _useAmbient = EditorGUILayout.ToggleLeft("Apply room ambient to the quad side", _useAmbient);
            using (new EditorGUI.DisabledScope(!_useAmbient))
            {
                _roomDarkness = EditorGUILayout.IntSlider("Room darkness", _roomDarkness, 0, 255);
                float multiplier = CharacterRenderAmbient.FromDarkness(_roomDarkness);
                EditorGUILayout.LabelField($"Ambient multiplier: {multiplier:F3}", EditorStyles.miniLabel);
            }

            if (EditorGUI.EndChangeCheck() && _sceneReady)
            {
                ApplyViewOptions();
                ApplyFrame();
            }

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("What to look at", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "1. Edges: the sprite side is Point + Tight, so its outline is harder and crawls at " +
                "sub-pixel motion. The quad side is Bilinear + full rect.\n\n" +
                "2. Registration: toggle Compensate pivot. Parts that move are the ones whose PNG was " +
                "alpha-trimmed; the table below says by how much.\n\n" +
                "3. Light: enable ambient at the room's darkness. The quad side darkens; the sprite " +
                "side cannot, because it draws with Sprite-Unlit-Default.",
                MessageType.None);

            if (_spriteAssembler != null)
            {
                EditorGUILayout.LabelField(
                    $"Sprite side: {_spriteAssembler.CurrentState}[{_spriteAssembler.CurrentFrame}]  |  " +
                    $"Quad side: {_quadAssembler?.CurrentState}[{_quadAssembler?.CurrentFrame}]",
                    EditorStyles.miniLabel);
            }
        }

        // ─── Pivot table ─────────────────────────────────────────

        void DrawPivotTable()
        {
            _showPivotTable = EditorGUILayout.Foldout(
                _showPivotTable, "Measured registration error per part (docs/07 §3)", true);

            if (!_showPivotTable || _animDef?.parts == null)
            {
                return;
            }

            int nonZero = 0;
            float worst = 0f;

            _pivotScroll = EditorGUILayout.BeginScrollView(_pivotScroll, GUILayout.Height(220));

            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            EditorGUILayout.LabelField("part", EditorStyles.miniBoldLabel, GUILayout.Width(86));
            EditorGUILayout.LabelField("png", EditorStyles.miniBoldLabel, GUILayout.Width(70));
            EditorGUILayout.LabelField("sprite rect", EditorStyles.miniBoldLabel, GUILayout.Width(96));
            EditorGUILayout.LabelField("trim", EditorStyles.miniBoldLabel, GUILayout.Width(72));
            EditorGUILayout.LabelField("pivot", EditorStyles.miniBoldLabel, GUILayout.Width(96));
            EditorGUILayout.LabelField("err px", EditorStyles.miniBoldLabel, GUILayout.Width(96));
            EditorGUILayout.EndHorizontal();

            foreach (CharacterPartDefinition part in _animDef.parts)
            {
                Sprite sprite = part.baseSprite;
                if (sprite == null)
                {
                    EditorGUILayout.LabelField($"  {part.partName}: (no sprite)");
                    continue;
                }

                CharacterQuadSource source = CharacterQuadSource.FromSprite(sprite);
                Vector2 error = CharacterQuadGeometry.PivotErrorPixels(source, part.pivotNormalized);

                bool trimmed = source.RectWidth != source.TextureWidth || source.RectHeight != source.TextureHeight;
                if (trimmed) nonZero++;
                worst = Mathf.Max(worst, Mathf.Max(Mathf.Abs(error.x), Mathf.Abs(error.y)));

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(part.partName, GUILayout.Width(86));
                EditorGUILayout.LabelField($"{source.TextureWidth:F0}x{source.TextureHeight:F0}", GUILayout.Width(70));
                EditorGUILayout.LabelField(
                    $"{source.RectWidth:F0}x{source.RectHeight:F0}@{source.RectX:F0},{source.RectY:F0}",
                    GUILayout.Width(96));
                EditorGUILayout.LabelField(
                    trimmed ? $"{source.TextureWidth - source.RectWidth:F0}x{source.TextureHeight - source.RectHeight:F0}" : "none",
                    GUILayout.Width(72));
                EditorGUILayout.LabelField(
                    $"{part.pivotNormalized.x:F3},{part.pivotNormalized.y:F3}", GUILayout.Width(96));

                Color previous = GUI.color;
                if (Mathf.Abs(error.x) > 0.5f || Mathf.Abs(error.y) > 0.5f)
                {
                    GUI.color = new Color(1f, 0.72f, 0.4f);
                }
                EditorGUILayout.LabelField($"{error.x:+0.0;-0.0;0.0}, {error.y:+0.0;-0.0;0.0}", GUILayout.Width(96));
                GUI.color = previous;

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndScrollView();

            EditorGUILayout.LabelField(
                $"{nonZero} of {_animDef.parts.Length} parts are alpha-trimmed; worst component {worst:F1} px. " +
                "err = where the shipped pivot lands minus where the importer meant it; positive means the " +
                "part is drawn that far left/down.",
                EditorStyles.miniLabel);

            if (GUILayout.Button("Log table to Console", GUILayout.Height(20)))
            {
                LogPivotTable();
            }
        }

        void LogPivotTable()
        {
            Debug.Log("[QuadAB] === registration error per part (px, actual - intended) ===");

            foreach (CharacterPartDefinition part in _animDef.parts)
            {
                Sprite sprite = part.baseSprite;
                if (sprite == null)
                {
                    Debug.Log($"[QuadAB]   {part.partName}: (no sprite)");
                    continue;
                }

                CharacterQuadSource source = CharacterQuadSource.FromSprite(sprite);
                Vector2 error = CharacterQuadGeometry.PivotErrorPixels(source, part.pivotNormalized);

                Debug.Log(
                    $"[QuadAB]   {part.partName}: png={source.TextureWidth:F0}x{source.TextureHeight:F0} " +
                    $"rect={source.RectWidth:F0}x{source.RectHeight:F0}@{source.RectX:F0},{source.RectY:F0} " +
                    $"pivot=({part.pivotNormalized.x:F3},{part.pivotNormalized.y:F3}) " +
                    $"err=({error.x:+0.0;-0.0;0.0},{error.y:+0.0;-0.0;0.0})");
            }
        }

        // ─── Scene ───────────────────────────────────────────────

        void SetupScene()
        {
            CleanupScene();

            if (_animDef == null)
            {
                return;
            }

            _root = new GameObject("[QuadAB_Root]")
            {
                hideFlags = HideFlags.HideAndDontSave,
                layer = PreviewLayer
            };
            _root.transform.position = new Vector3(PreviewOffsetX, 0f, 0f);

            CharacterAppearance appearance = CharacterAppearance.CreateDefault();
            CharacterVisualContext context = CharacterVisualContext.Default;

            // ── shipped sprite path ──
            _spriteRoot = new GameObject("[QuadAB_Sprite]")
            {
                hideFlags = HideFlags.HideAndDontSave,
                layer = PreviewLayer
            };
            _spriteRoot.transform.SetParent(_root.transform, false);

            _spriteAssembler = _spriteRoot.AddComponent<CharacterSpriteAssembler>();
            _spriteAssembler.Setup(_animDef, _styleData, appearance, context);
            ApplyPreviewFlags(_spriteRoot.transform);

            // ── quad path ──
            _quadRoot = new GameObject("[QuadAB_Quads]")
            {
                hideFlags = HideFlags.HideAndDontSave,
                layer = PreviewLayer
            };
            _quadRoot.transform.SetParent(_root.transform, false);

            _quadAssembler = _quadRoot.AddComponent<CharacterQuadAssembler>();
            _quadAssembler.Setup(_animDef, _styleData, appearance, context);
            ApplyPreviewFlags(_quadRoot.transform);

            // ── camera + target ──
            var cameraGo = new GameObject("[QuadAB_Camera]")
            {
                hideFlags = HideFlags.HideAndDontSave,
                layer = PreviewLayer
            };
            _previewCamera = cameraGo.AddComponent<Camera>();
            _previewCamera.clearFlags = CameraClearFlags.SolidColor;
            _previewCamera.backgroundColor = new Color(0.18f, 0.18f, 0.18f, 1f);
            _previewCamera.orthographic = true;

            // A camera rendered by hand owns its whole viewport. If `rect` were left at anything but
            // the full target, the camera would clear and draw only part of the render texture and the
            // rest would keep whatever the allocation happened to contain — which reads on screen as a
            // black band with no explanation. HDR is off so the target format matches the ARGB32
            // texture this window allocates.
            _previewCamera.rect = new Rect(0f, 0f, 1f, 1f);
            _previewCamera.allowHDR = false;
            _previewCamera.useOcclusionCulling = false;
            _previewCamera.cullingMask = 1 << PreviewLayer;
            _previewCamera.enabled = false;

            RecreateRenderTexture();

            // Both assemblers were just rebuilt from CharacterVisualContext.Default, so whatever costume
            // the field holds has to be pushed again. Without this, rebuilding the preview after choosing
            // one silently drops back to no armour while the field still shows the id.
            _appliedArmorId = null;

            _sceneReady = true;
            _framePending = true;
            _diagnostics = "";
            _status = $"Ready: {_animDef.parts.Length} parts, sprite side vs quad side.";
            ApplyViewOptions();
        }

        void ApplyViewOptions()
        {
            if (_root == null) return;

            // ── the costume, on both sides ──
            // Pushed here rather than at Setup because the window is built once and the id is edited
            // afterwards. Only on an actual change: the quad side rebuilds its atlas when armorId
            // differs, and the sprite side re-applies the whole appearance, so an unconditional write
            // would make every unrelated toggle pay for a costume that did not change.
            string wantedArmor = _armorId != null ? _armorId.Trim() : string.Empty;
            if (!string.Equals(wantedArmor, _appliedArmorId))
            {
                _appliedArmorId = wantedArmor;

                CharacterVisualContext context = CharacterVisualContext.Default;
                context.armorId = wantedArmor;

                if (_spriteAssembler != null) _spriteAssembler.VisualContext = context;
                if (_quadAssembler != null) _quadAssembler.VisualContext = context;
            }

            if (_spriteRoot != null)
            {
                _spriteRoot.SetActive(_showSpriteSide);
                _spriteRoot.transform.localPosition = new Vector3(-_sideGap * 0.5f, 0f, 0f);
            }

            if (_quadRoot != null)
            {
                _quadRoot.SetActive(_showQuadSide);
                _quadRoot.transform.localPosition = new Vector3(_sideGap * 0.5f, 0f, 0f);
            }

            if (_quadAssembler != null)
            {
                _quadAssembler.CompensatePivot = _compensatePivot;
                _quadAssembler.Bilinear = _bilinear;
                _quadAssembler.AtlasEnabled = _useAtlas;
                _quadAssembler.Ambient = _useAmbient ? CharacterRenderAmbient.FromDarkness(_roomDarkness) : 1f;
            }
        }

        void CleanupScene()
        {
            if (_spriteAssembler != null)
            {
                DestroyImmediate(_spriteAssembler);
                _spriteAssembler = null;
            }

            if (_quadAssembler != null)
            {
                DestroyImmediate(_quadAssembler);
                _quadAssembler = null;
            }

            if (_root != null)
            {
                DestroyImmediate(_root);
            }

            if (_previewCamera != null)
            {
                DestroyImmediate(_previewCamera.gameObject);
            }

            if (_previewRt != null)
            {
                DestroyImmediate(_previewRt);
            }

            if (_previewUnlit != null)
            {
                DestroyImmediate(_previewUnlit);
                _previewUnlit = null;
            }

            _root = null;
            _spriteRoot = null;
            _quadRoot = null;
            _previewCamera = null;
            _previewRt = null;
            _sceneReady = false;
            _renderPending = false;
            _framePending = false;
            _diagnosePending = false;
            _diagnostics = "";
        }

        void RecreateRenderTexture()
        {
            if (_previewRt != null)
            {
                DestroyImmediate(_previewRt);
            }

            _previewRt = new RenderTexture(_previewSize, _previewSize, 16, RenderTextureFormat.ARGB32)
            {
                hideFlags = HideFlags.HideAndDontSave,
                name = "QuadAB_Preview",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                antiAliasing = 1,
                useMipMap = false
            };
            _previewRt.Create();

            // Clear it to the camera's own background before the first render. A brand-new render
            // target is uninitialised, so "never rendered" and "rendered, and the content is black"
            // look identical on screen — and that ambiguity is exactly what makes a black preview
            // hard to diagnose.
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = _previewRt;
            GL.Clear(true, true, _previewCamera != null ? _previewCamera.backgroundColor : Color.black);
            RenderTexture.active = previous;
        }

        void ApplyFrame()
        {
            if (!_sceneReady || _animDef == null || _stateNames.Length == 0)
            {
                return;
            }

            string stateName = _stateNames[_selectedStateIdx];

            _spriteAssembler?.SetState(stateName, _currentFrame);
            _quadAssembler?.SetState(stateName, _currentFrame);

            // A slot created lazily by SetState (the secondary hair layer is built on the first frame
            // that needs it) would otherwise keep the default flags and land in the saved scene.
            if (_spriteRoot != null) ApplyPreviewFlags(_spriteRoot.transform);
            if (_quadRoot != null) ApplyPreviewFlags(_quadRoot.transform);

            EnsureUnlitPreviewMaterial();

            RequestRender();
        }

        /// <summary>
        /// Puts the shipped side on the unlit sprite shader.
        ///
        /// <para><b>Why it renders black otherwise.</b> The shipped assembler creates bare
        /// <see cref="SpriteRenderer"/>s, so they take the project's default sprite material — which in
        /// URP 2D is the <i>lit</i> one. This camera culls to a single preview layer, and the scene's
        /// global <c>Light2D</c> is on layer 0, so no light reaches the preview and a lit sprite resolves
        /// to pure black. That is a property of the preview, not of the shipped renderer: in the game the
        /// same material is lit by that global light.</para>
        ///
        /// <para>Substituting unlit is what makes the comparison mean anything — both sides then differ
        /// in geometry, filtering and batching, and in nothing else. It is also what the help text beside
        /// this preview has always said the sprite side does. The substitution is reported, not hidden:
        /// <see cref="_materialNote"/> names the shader it replaced.</para>
        /// </summary>
        void EnsureUnlitPreviewMaterial()
        {
            if (_spriteRoot == null)
            {
                return;
            }

            if (_previewUnlit == null)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default")
                             ?? Shader.Find("Sprites/Default");
                if (shader == null)
                {
                    _materialNote = "sprite side: no unlit sprite shader found; left on the default material";
                    return;
                }

                _previewUnlit = new Material(shader)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    name = "QuadAB_PreviewUnlit"
                };
            }

            SpriteRenderer[] renderers = _spriteRoot.GetComponentsInChildren<SpriteRenderer>(true);
            int replaced = 0;
            string previous = "(none)";

            for (int i = 0; i < renderers.Length; i++)
            {
                SpriteRenderer renderer = renderers[i];
                if (renderer == null || renderer.sharedMaterial == _previewUnlit)
                {
                    continue;
                }

                if (renderer.sharedMaterial != null && renderer.sharedMaterial.shader != null)
                {
                    previous = renderer.sharedMaterial.shader.name;
                }

                renderer.sharedMaterial = _previewUnlit;
                replaced++;
            }

            if (replaced > 0)
            {
                _materialNote = $"sprite side: {previous} -> unlit on {replaced} renderer(s)";
            }
        }

        /// <summary>Ask for a render on the next editor tick. Never renders from inside <c>OnGUI</c>.</summary>
        void RequestRender()
        {
            _renderPending = true;
            Repaint();
        }

        void RenderNow()
        {
            if (_previewCamera == null || _previewRt == null)
            {
                return;
            }

            if (_previewRt.width != _previewSize)
            {
                RecreateRenderTexture();
            }

            _previewCamera.orthographicSize = _cameraZoom;
            _previewCamera.transform.position = new Vector3(
                PreviewOffsetX + _cameraOffset.x,
                _cameraOffset.y,
                -10f);
            _previewCamera.targetTexture = _previewRt;
            _previewCamera.Render();
            _previewCamera.targetTexture = null;
        }

        /// <summary>
        /// Fit the camera to what was actually built, not to a guess. A fixed orthographic size plus a
        /// fixed gap means that any change in part scale — or a part drawn in the wrong units — shows
        /// as an empty view with no explanation. Framing on the renderers' own bounds removes that
        /// failure mode, and the reported bounds say what the scale really is.
        /// </summary>
        void FrameContent()
        {
            bool hasSprite = TryCollectBounds(_spriteRoot, out Bounds spriteBounds);
            bool hasQuad = TryCollectBounds(_quadRoot, out Bounds quadBounds);

            if (!hasSprite && !hasQuad)
            {
                _status = "Frame: nothing enabled on either side — the assemblers built no visible slot.";
                return;
            }

            Bounds union = hasSprite ? spriteBounds : quadBounds;
            if (hasSprite && hasQuad) union.Encapsulate(quadBounds);

            float extent = Mathf.Max(union.extents.x, union.extents.y);
            if (extent <= 0.0001f)
            {
                return;
            }

            _cameraZoom = Mathf.Clamp(extent * 1.15f, 0.25f, 8f);
            _cameraOffset = new Vector2(union.center.x - PreviewOffsetX, union.center.y);
        }

        /// <summary>
        /// Frame the preview at the magnification the game uses, instead of filling the window.
        ///
        /// <para>Screen pixels per world unit is <c>rtHeight / (ortho * 2)</c> on both sides, so
        /// matching the two gives <c>ortho = previewSize * gameOrtho / gameScreenHeight</c>. At 512 px
        /// with the game's 1080/ortho 5 that is 2.37, i.e. 108 px/unit — a quarter of the ~7x loupe
        /// <see cref="FrameContent"/> produces on a 0.6-unit character.</para>
        ///
        /// <para>The offset is still centred on the content, because a correctly-scaled view that
        /// points somewhere else is a worse failure than a magnified one: it looks like nothing
        /// rendered.</para>
        /// </summary>
        void ApplyGameFraming()
        {
            if (_gameScreenHeight <= 0 || _gameOrthoSize <= 0f)
            {
                _status = $"Game framing: needs positive values (ortho {_gameOrthoSize:F2}, height {_gameScreenHeight}).";
                return;
            }

            float gamePixelsPerUnit = _gameScreenHeight / (_gameOrthoSize * 2f);
            _cameraZoom = Mathf.Clamp(_previewSize * _gameOrthoSize / _gameScreenHeight, 0.01f, 8f);

            bool hasSprite = TryCollectBounds(_spriteRoot, out Bounds spriteBounds);
            bool hasQuad = TryCollectBounds(_quadRoot, out Bounds quadBounds);
            if (hasSprite || hasQuad)
            {
                Bounds union = hasSprite ? spriteBounds : quadBounds;
                if (hasSprite && hasQuad) union.Encapsulate(quadBounds);
                _cameraOffset = new Vector2(union.center.x - PreviewOffsetX, union.center.y);
            }

            float shownPixelsPerUnit = _previewSize / (_cameraZoom * 2f);
            _status = $"Game framing: {shownPixelsPerUnit:F0} px/unit vs the game's {gamePixelsPerUnit:F0} " +
                      $"(ortho {_cameraZoom:F2}). Source textures are 100 px/unit, so each source pixel " +
                      $"covers {shownPixelsPerUnit / 100f:F2} screen pixels.";

            RequestRender();
            Repaint();
        }

        static bool TryCollectBounds(GameObject root, out Bounds bounds)
        {
            bounds = default;
            if (root == null)
            {
                return false;
            }

            bool has = false;
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(false);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !renderer.enabled)
                {
                    continue;
                }

                var spriteRenderer = renderer as SpriteRenderer;
                if (spriteRenderer != null && spriteRenderer.sprite == null)
                {
                    continue;
                }

                if (!has)
                {
                    bounds = renderer.bounds;
                    has = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            return has;
        }

        // ─── Diagnostics ─────────────────────────────────────────

        /// <summary>
        /// Answers "why is it black?" with measurements instead of a hypothesis: what each side built,
        /// whether the camera's frustum contains it, and what is actually in the render target.
        ///
        /// <para>Queued rather than run here, because the probe must read the frame the window is
        /// showing — which is the one produced outside the GUI pass. Rendering from inside
        /// <c>OnGUI</c> to take the measurement would measure a different frame from the one on
        /// screen, and that difference is precisely what is under investigation.</para>
        /// </summary>
        void Diagnose()
        {
            if (!_sceneReady)
            {
                _status = "Diagnose: set the preview up first.";
                return;
            }

            _diagnosePending = true;
            RequestRender();
        }

        void BuildDiagnostics()
        {
            var report = new StringBuilder();
            report.AppendLine("=== Character A/B ===");
            report.AppendLine(DescribeSide("Sprite", _spriteRoot, _spriteAssembler));
            report.AppendLine(DescribeSide("Quad", _quadRoot, _quadAssembler));
            if (!string.IsNullOrEmpty(_materialNote))
            {
                report.AppendLine(_materialNote);
            }
            report.AppendLine(DescribeCamera());
            report.AppendLine(DescribeSourceResolution());
            report.AppendLine(DescribeAtlas());
            report.AppendLine(DescribePartDiff());
            report.AppendLine(DescribeRenderTarget());
            report.AppendLine(DescribeForeignLayerObjects());
            _diagnostics = report.ToString();
            Debug.Log("[QuadAB] " + _diagnostics);
        }

        /// <summary>
        /// The pixelation question, answered with numbers: which parts are actually being magnified.
        ///
        /// <para><b>Magnification is not the camera's alone.</b> Every character sprite imports at
        /// 100 px/unit, so the part's own placement scale multiplies its source pixels on top of the
        /// camera's ratio: <c>magnification = screenPxPerUnit / ppu * |localScale|</c>. An earlier
        /// version of this report omitted <c>localScale</c> and therefore ranked the parts by raw pixel
        /// count — which pointed at the 5x7 front leg as "smallest" while blaming nothing, and could
        /// not explain why the 114x81 torso looks clean and the leg does not. They are drawn at the same
        /// on-screen size; the torso is placed at 0.21 and the leg at 1.07, so the torso has five source
        /// pixels per screen pixel and the leg has one. That ratio is the whole answer.</para>
        ///
        /// <para>A part with <c>magnification &gt;= 0.9</c> has no oversampling left: its anti-aliased
        /// edge is baked at one pixel and any filter other than a 1:1 blit turns it into a stair-step.
        /// Those are the parts a higher-density export would help, and they are listed first.</para>
        /// </summary>
        string DescribeSourceResolution()
        {
            if (_previewCamera == null || _previewRt == null)
            {
                return "Resolution: unknown";
            }

            float screenPixelsPerUnit = _previewRt.height / Mathf.Max(0.0001f, _previewCamera.orthographicSize * 2f);
            if (_animDef?.parts == null)
            {
                return $"Resolution: {screenPixelsPerUnit:F0} screen px per world unit";
            }

            // Placement scale for the frame on screen. Absent for a part this frame does not place —
            // those are not drawn, so reporting them would invent a problem.
            var placementScale = new Dictionary<int, float>();
            CharacterStateClip clip = _animDef.stateClips != null
                                      && _selectedStateIdx >= 0
                                      && _selectedStateIdx < _animDef.stateClips.Length
                ? _animDef.stateClips[_selectedStateIdx]
                : null;
            if (clip?.frames != null && _currentFrame >= 0 && _currentFrame < clip.frames.Length)
            {
                CharacterFrameData frameData = clip.frames[_currentFrame];
                if (frameData.partPlacements != null)
                {
                    foreach (PartFramePlacement placement in frameData.partPlacements)
                    {
                        if (!placement.visible || placement.partIndex < 0)
                        {
                            continue;
                        }
                        // A mirrored placement is negative on one axis; the stretch is the magnitude.
                        float scale = Mathf.Max(Mathf.Abs(placement.localScale.x), Mathf.Abs(placement.localScale.y));
                        placementScale[placement.partIndex] = scale;
                    }
                }
            }

            var rows = new List<(string part, int width, int height, float scale, float magnification)>();
            for (int i = 0; i < _animDef.parts.Length; i++)
            {
                CharacterPartDefinition part = _animDef.parts[i];
                if (part?.baseSprite == null || !placementScale.TryGetValue(i, out float scale))
                {
                    continue;
                }

                Rect rect = part.baseSprite.rect;
                float ppu = Mathf.Max(1f, part.baseSprite.pixelsPerUnit);
                rows.Add((part.partName,
                          Mathf.RoundToInt(rect.width),
                          Mathf.RoundToInt(rect.height),
                          scale,
                          screenPixelsPerUnit / ppu * scale));
            }

            rows.Sort((a, b) => b.magnification.CompareTo(a.magnification));

            int magnified = 0;
            int oversampled = 0;
            foreach (var row in rows)
            {
                if (row.magnification >= 0.9f) magnified++;
                else if (row.magnification <= 0.5f) oversampled++;
            }

            var builder = new StringBuilder();
            builder.Append($"Resolution: {screenPixelsPerUnit:F0} screen px/unit; ");
            builder.Append($"{magnified} of {rows.Count} drawn parts magnified (>=0.9), {oversampled} oversampled (<=0.5)");
            int shown = Mathf.Min(5, rows.Count);
            for (int i = 0; i < shown; i++)
            {
                var row = rows[i];
                float drawnWidth = row.width * row.magnification;
                float drawnHeight = row.height * row.magnification;
                builder.Append($" | {row.part} {row.width}x{row.height}@x{row.scale:F2} -> {drawnWidth:F0}x{drawnHeight:F0}px mag {row.magnification:F2}");
            }

            return builder.ToString();
        }

        /// <summary>
        /// The batch count, stated as a number instead of an intention.
        ///
        /// <para>The shipped path draws one <see cref="SpriteRenderer"/> per part, and each part is its
        /// own PNG, so each part is its own texture and its own draw call. The atlas replaces that with
        /// one texture and one material, which is what makes the quad side batch. Reporting
        /// <c>RegionCount</c> is the honest form of "26 draw calls became one": it is the number of
        /// distinct texture regions the character actually samples.</para>
        /// </summary>
        string DescribeAtlas()
        {
            if (_quadAssembler == null)
            {
                return "Atlas: no quad side";
            }

            if (!_useAtlas)
            {
                return "Atlas: off — one material per part texture (what the shipped path does)";
            }

            CharacterQuadAtlas atlas = _quadAssembler.Atlas;
            if (atlas == null)
            {
                return "Atlas: requested but not built — the quad side is drawing per texture";
            }

            if (!atlas.IsReady)
            {
                return $"Atlas: not ready ({atlas.Error ?? "no error recorded"})";
            }

            string costume = string.IsNullOrEmpty(atlas.ArmorId) ? "none" : atlas.ArmorId;

            return $"Atlas: {atlas.Width}x{atlas.Height} {atlas.Texture.filterMode} " +
                   $"{atlas.MipCount} mip level(s), " +
                   $"{atlas.RegionCount} regions from {atlas.RegionCount + atlas.SkippedCount} sprites, " +
                   $"{atlas.SkippedCount} skipped, 1 material, costume {costume}";
        }

        /// <summary>
        /// Writes the atlas and its region table to <c>Temp/quad_atlas_dump</c>.
        ///
        /// <para><b>Why a dump and not a log line.</b> "The quad side is wrong" and "the quad side is
        /// drawing the right windows of a wrong atlas" look identical on screen and read identically in
        /// the console. A PNG of the atlas settles it by inspection, and the table says which window
        /// each region got, so a bad part names itself.</para>
        ///
        /// <para><b>Why it lives here.</b> <c>EncodeToPNG</c> is an editor-side image-conversion
        /// extension, and <see cref="CharacterQuadAtlas"/> has to stay loadable by the offline fixture
        /// harness, which references <c>UnityEngine.CoreModule</c> only — referencing the encoder from
        /// the atlas would make the fixtures fail to load with a missing-assembly error, which is the
        /// exact trap that makes a green offline run meaningless.</para>
        /// </summary>
        static string DumpAtlas(CharacterQuadAtlas atlas)
        {
            if (atlas == null || atlas.Texture == null)
            {
                return "[CharacterQuad] Nothing to dump — the atlas has no texture.";
            }

            const string directory = "Temp/quad_atlas_dump";
            Directory.CreateDirectory(directory);

            string png = Path.Combine(directory, $"quad_atlas_{atlas.Width}.png");
            File.WriteAllBytes(png, ImageConversion.EncodeToPNG(atlas.Texture));

            var table = new StringBuilder();
            table.AppendLine(
                $"atlas {atlas.Width}x{atlas.Height} {atlas.Texture.filterMode} {atlas.MipCount} mip level(s)");
            table.AppendLine(
                $"regions {atlas.RegionCount}  skipped {atlas.SkippedCount}  costume '{atlas.ArmorId}'");
            // The source columns are what make this dump checkable. A destination rect alone is
            // always well-formed, even when it was copied from the wrong window; with the source
            // window recorded, every row can be compared against the PNG on disk -- srcW x srcH must
            // equal w x h, and the atlas pixels at (x,y,w,h) must equal the PNG's at (sx,sy,srcW,srcH).
            table.AppendLine(
                "index\tx\ty\tw\th\tu0\tv0\tu1\tv1\ttexture\tsx\tsy\tsrcW\tsrcH\ttexW\ttexH");

            for (int i = 0; i < atlas.RegionCount; i++)
            {
                CharacterAtlasRect rect = atlas.GetRegionRect(i);
                Vector4 uv = atlas.GetRegionUv(i);
                CharacterAtlasRegionSource source = atlas.GetRegionSource(i);
                table.AppendLine(
                    $"{i}\t{rect.X}\t{rect.Y}\t{rect.Width}\t{rect.Height}\t" +
                    $"{uv.x:F5}\t{uv.y:F5}\t{uv.z:F5}\t{uv.w:F5}\t" +
                    $"{source.TextureName}\t{source.Rect.X}\t{source.Rect.Y}\t" +
                    $"{source.Rect.Width}\t{source.Rect.Height}\t" +
                    $"{source.TextureWidth}\t{source.TextureHeight}");
            }

            string tsv = Path.Combine(directory, $"quad_atlas_{atlas.Width}_regions.tsv");
            File.WriteAllText(tsv, table.ToString());

            return $"[CharacterQuad] Atlas dumped to {png} and {tsv}";
        }

        /// <summary>
        /// Per-part, two-side registration: the quad side's world bounds minus the sprite side's, in
        /// screen pixels at the current framing, with the pivot table's own prediction beside it.
        ///
        /// <para><b>Why this exists.</b> `RenderingPipelineImprovement/03` §5 step 3 asks the owner to
        /// confirm that "the two characters must still overlap exactly" with compensate off. That is a
        /// judgement about a picture; this is the same question per part, so a disagreement names the
        /// part instead of requiring a hunt. It is also a cross-check: the prediction comes from
        /// <see cref="CharacterQuadGeometry.PivotErrorPixels"/>, the measurement from the two renderers'
        /// own bounds, and neither is computed from the other — so a row where they disagree says one of
        /// them is wrong, rather than that they were written by the same hand.</para>
        ///
        /// <para><b>The conversion is not one step.</b> The pivot error is in <i>source</i> pixels of
        /// the PNG; the quad draws at the part's placement scale; the readout is in screen pixels. So a
        /// 32.5 px error on the horn — placed at 0.21 — is 32.5 / 100 x 0.21 x 746 = 51 screen px, not
        /// 32.5. Skipping the scale would over-predict the torso by five times and read as a defect.</para>
        ///
        /// <para><b>What it cannot see.</b> Bounds are axis-aligned boxes, so a rotated part or one with
        /// an asymmetric alpha contour can have equal bounds and a different picture. It compares
        /// placement, not pixels; <see cref="DescribeRenderTarget"/> is the pixel half.</para>
        /// </summary>
        string DescribePartDiff()
        {
            if (_animDef?.parts == null || _spriteAssembler == null || _quadAssembler == null ||
                _spriteRoot == null || _quadRoot == null)
            {
                return "Parts: not built";
            }

            if (_previewRt == null || _previewCamera == null)
            {
                return "Parts: no render target, so no pixels per unit";
            }

            float pxPerUnit = _previewRt.height / (2f * Mathf.Max(1e-4f, _previewCamera.orthographicSize));
            Vector3 spriteOrigin = _spriteRoot.transform.position;
            Vector3 quadOrigin = _quadRoot.transform.position;

            var rows = new List<PartDiffRow>();
            int oneSided = 0;
            int compared = 0;

            foreach (CharacterPartDefinition part in _animDef.parts)
            {
                if (part == null || string.IsNullOrEmpty(part.partName)) continue;

                CharacterSpriteAssembler.PartSlot spriteSlot = _spriteAssembler.GetSlot(part.partName);
                CharacterQuadAssembler.QuadSlot quadSlot = _quadAssembler.GetQuadSlot(part.partName);

                SpriteRenderer spriteRenderer = spriteSlot != null ? spriteSlot.renderer : null;
                MeshRenderer quadRenderer = quadSlot != null ? quadSlot.renderer : null;

                bool hasSprite = TryGetVisibleBounds(spriteRenderer, spriteOrigin, out Bounds spriteBounds);
                bool hasQuad = TryGetVisibleBounds(quadRenderer, quadOrigin, out Bounds quadBounds);

                // Hidden on both sides is agreement, not a row: a helmet that hides the mane is the
                // same decision on both paths, and listing it would drown the interesting rows.
                if (!hasSprite && !hasQuad) continue;

                if (hasSprite != hasQuad)
                {
                    oneSided++;
                    rows.Add(new PartDiffRow
                    {
                        name = part.partName,
                        oneSided = true,
                        quadVisible = hasQuad
                    });
                    continue;
                }

                compared++;
                rows.Add(new PartDiffRow
                {
                    name = part.partName,
                    delta = (Vector2)(quadBounds.center - spriteBounds.center) * pxPerUnit,
                    sizeDelta = (Vector2)(quadBounds.size - spriteBounds.size) * pxPerUnit,
                    predicted = PredictPivotDelta(
                        part,
                        spriteRenderer != null ? spriteRenderer.sprite : null,
                        // The placement scale, read from the SPRITE side on purpose: the quad side
                        // bakes it into the vertices and leaves its transform at unit scale, so
                        // `quadSlot.transform.localScale` is (1,1,1) and would under-predict the
                        // torso's offset by five times. The sprite path carries the same number in
                        // `transform.localScale`, which is the placement data this prediction wants.
                        spriteSlot != null ? spriteSlot.transform.localScale : Vector3.one,
                        pxPerUnit)
                });
            }

            int mismatched = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                if (!rows[i].oneSided && rows[i].Worst > MismatchPixels) mismatched++;
            }

            rows.Sort((a, b) => b.Worst.CompareTo(a.Worst));

            var text = new StringBuilder();
            text.Append("Parts (quad - sprite, screen px at ")
                .Append(pxPerUnit.ToString("F0", CultureInfo.InvariantCulture))
                .Append(" px/unit): ")
                .Append(compared.ToString(CultureInfo.InvariantCulture))
                .Append(" compared, ")
                .Append(mismatched.ToString(CultureInfo.InvariantCulture))
                .Append(" over ")
                .Append(MismatchPixels.ToString("F1", CultureInfo.InvariantCulture))
                .Append(" px, ")
                .Append(oneSided.ToString(CultureInfo.InvariantCulture))
                .Append(" visible on one side only");

            int shown = Mathf.Min(rows.Count, MaxPartDiffRows);
            for (int i = 0; i < shown; i++)
            {
                text.AppendLine();
                text.Append("  ").Append(rows[i].Describe());
            }

            if (rows.Count > shown)
            {
                text.AppendLine();
                text.Append("  ... ")
                    .Append((rows.Count - shown).ToString(CultureInfo.InvariantCulture));

                // A one-sided row sorts first (its Worst is +infinity), so the row just past the cut
                // can be one — and "all under Infinity px" would be nonsense.
                if (rows[shown].oneSided)
                {
                    text.Append(" more, including rows visible on one side only");
                }
                else
                {
                    text.Append(" more, all under ")
                        .Append(rows[shown].Worst.ToString("F1", CultureInfo.InvariantCulture))
                        .Append(" px");
                }
            }

            return text.ToString();
        }

        /// <summary>
        /// Where the quad side <i>should</i> sit relative to the sprite side, in screen pixels, from the
        /// documented pivot error. Independent of the measurement above by construction.
        /// </summary>
        Vector2 PredictPivotDelta(
            CharacterPartDefinition part,
            Sprite drawn,
            Vector3 placementScale,
            float pxPerUnit)
        {
            // Compensate off draws the trimmed rect exactly as the sprite side does, so the prediction
            // is zero — and that is the row set §5 step 3 is about.
            if (!_compensatePivot || drawn == null) return Vector2.zero;

            CharacterQuadSource source = CharacterQuadSource.FromSprite(drawn);
            if (!source.IsValid) return Vector2.zero;

            // PivotErrorPixels is (shipped - intended) registration. The quad side uses the intended
            // point and the sprite side the shipped one, so the quad lands at +(shipped - intended):
            // the same sign, not the opposite one. (Build() negates it again for the vertices, which is
            // why the quad's own YMin moves the other way — see the geometry fixture.)
            Vector2 errorPixels = CharacterQuadGeometry.PivotErrorPixels(source, part.pivotNormalized);

            return new Vector2(
                errorPixels.x / source.PixelsPerUnit * placementScale.x * pxPerUnit,
                errorPixels.y / source.PixelsPerUnit * placementScale.y * pxPerUnit);
        }

        /// <summary>
        /// A renderer's bounds in its side's local space, or <c>false</c> when it is not drawing
        /// anything. A <see cref="SpriteRenderer"/> with no sprite counts as not drawing even when it is
        /// enabled — that is how the sprite path hides a part it has no art for.
        /// </summary>
        static bool TryGetVisibleBounds(Renderer renderer, Vector3 origin, out Bounds localBounds)
        {
            localBounds = default;

            if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
            {
                return false;
            }

            var spriteRenderer = renderer as SpriteRenderer;
            if (spriteRenderer != null && spriteRenderer.sprite == null)
            {
                return false;
            }

            Bounds world = renderer.bounds;
            localBounds = new Bounds(world.center - origin, world.size);
            return true;
        }

        struct PartDiffRow
        {
            public string name;
            public Vector2 delta;
            public Vector2 sizeDelta;
            public Vector2 predicted;
            public bool oneSided;
            public bool quadVisible;

            public float Worst
            {
                get
                {
                    if (oneSided) return float.PositiveInfinity;

                    return Mathf.Max(
                        Mathf.Abs(delta.x),
                        Mathf.Max(Mathf.Abs(delta.y),
                            Mathf.Max(Mathf.Abs(sizeDelta.x), Mathf.Abs(sizeDelta.y))));
                }
            }

            public string Describe()
            {
                if (oneSided)
                {
                    return name + "  " + (quadVisible ? "quad only" : "sprite only") +
                           "  (one side is not drawing this part)";
                }

                return name +
                       "  d=(" + F(delta.x) + "," + F(delta.y) + ")" +
                       " size=(" + F(sizeDelta.x) + "," + F(sizeDelta.y) + ")" +
                       " predicted=(" + F(predicted.x) + "," + F(predicted.y) + ")";
            }

            static string F(float value) => value.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture);
        }

        string DescribeSide(string label, GameObject root, Component assembler)
        {
            if (root == null || assembler == null)
            {
                return label + ": not built";
            }

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            Plane[] planes = _previewCamera != null
                ? GeometryUtility.CalculateFrustumPlanes(_previewCamera)
                : null;

            int live = 0;
            int inFrustum = 0;
            int layer = root.layer;
            string shader = "(none)";

            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !renderer.gameObject.activeInHierarchy || !renderer.enabled)
                {
                    continue;
                }

                var spriteRenderer = renderer as SpriteRenderer;
                if (spriteRenderer != null && spriteRenderer.sprite == null)
                {
                    continue;
                }

                live++;

                if (planes != null && GeometryUtility.TestPlanesAABB(planes, renderer.bounds))
                {
                    inFrustum++;
                }

                if (renderer.sharedMaterial != null && renderer.sharedMaterial.shader != null)
                {
                    shader = renderer.sharedMaterial.shader.name;
                }

                if (renderer.gameObject.layer != layer)
                {
                    layer = -1;
                }
            }

            string boundsText = TryCollectBounds(root, out Bounds bounds)
                ? $"c=({bounds.center.x:F2},{bounds.center.y:F2}) s=({bounds.size.x:F2},{bounds.size.y:F2})"
                : "none";

            string layerText = layer < 0 ? "mixed" : layer.ToString();
            return $"{label}: {live} live renderer(s), {inFrustum} in frustum, layer {layerText}, shader {shader}, bounds {boundsText}";
        }

        string DescribeCamera()
        {
            if (_previewCamera == null)
            {
                return "Camera: none";
            }

            Vector3 position = _previewCamera.transform.position;
            float half = _previewCamera.orthographicSize;
            return $"Camera: ortho {half:F2} at ({position.x:F2},{position.y:F2}), view {half * 2f:F2} x {half * 2f:F2} world units, culling layer {PreviewLayer}";
        }

        string DescribeRenderTarget()
        {
            if (_previewRt == null)
            {
                return "RT: none";
            }

            int width = _previewRt.width;
            int height = _previewRt.height;
            Color[] pixels = ReadRenderTarget();
            if (pixels == null)
            {
                return $"RT: {width}x{height} (not readable)";
            }

            Color clear = _previewCamera != null ? _previewCamera.backgroundColor : Color.black;
            int content = 0;
            int black = 0;
            int minX = int.MaxValue;
            int minY = int.MaxValue;
            int maxX = -1;
            int maxY = -1;

            for (int i = 0; i < pixels.Length; i++)
            {
                Color c = pixels[i];
                bool isClear = Mathf.Abs(c.r - clear.r) < 0.02f
                    && Mathf.Abs(c.g - clear.g) < 0.02f
                    && Mathf.Abs(c.b - clear.b) < 0.02f;
                if (isClear)
                {
                    continue;
                }

                content++;
                if (c.r < 0.02f && c.g < 0.02f && c.b < 0.02f)
                {
                    black++;
                }

                int x = i % width;
                int y = i / width;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }

            if (content == 0)
            {
                return $"RT: {width}x{height}, every pixel is the clear colour {clear.r:F2} — the camera drew nothing";
            }

            float contentPercent = content * 100f / pixels.Length;
            float blackPercent = black * 100f / content;
            return $"RT: {width}x{height}, {contentPercent:F1}% differs from clear, {blackPercent:F0}% of that is pure black, content bbox x{minX}..{maxX} y{minY}..{maxY}";
        }

        Color[] ReadRenderTarget()
        {
            if (_previewRt == null)
            {
                return null;
            }

            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = _previewRt;
            var probe = new Texture2D(_previewRt.width, _previewRt.height, TextureFormat.RGBA32, false, true);
            Color[] pixels = null;
            try
            {
                probe.ReadPixels(new Rect(0, 0, _previewRt.width, _previewRt.height), 0, 0);
                probe.Apply(false, false);
                pixels = probe.GetPixels();
            }
            finally
            {
                RenderTexture.active = previous;
                DestroyImmediate(probe);
            }

            return pixels;
        }

        /// <summary>
        /// Both preview windows use layer 31 and x = 1000, so a window left open with stale objects
        /// would appear inside this one's camera. That is a real collision, not a hypothetical: the
        /// character animation preview builds its slots at the same coordinates.
        /// </summary>
        string DescribeForeignLayerObjects()
        {
            var builder = new StringBuilder();
            Renderer[] all = UnityEngine.Object.FindObjectsByType<Renderer>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            int count = 0;
            for (int i = 0; i < all.Length; i++)
            {
                Renderer renderer = all[i];
                if (renderer == null || renderer.gameObject.layer != PreviewLayer)
                {
                    continue;
                }

                if (_root != null && renderer.transform.IsChildOf(_root.transform))
                {
                    continue;
                }

                if (count == 0)
                {
                    builder.Append("Layer ").Append(PreviewLayer).Append(" renderers that are not this window's: ");
                }

                builder.Append(renderer.name).Append(" (").Append(renderer.GetType().Name).Append(") ");
                count++;
                if (count >= 8)
                {
                    break;
                }
            }

            return count == 0
                ? $"Layer {PreviewLayer}: no foreign renderer — the preview is isolated"
                : builder.ToString();
        }

        // ─── Export ──────────────────────────────────────────────

        void ExportFrame()
        {
            if (!_sceneReady || _previewRt == null)
            {
                _status = "Setup the preview first.";
                return;
            }

            // Synchronous on purpose: the export reads the target immediately afterwards, so it cannot
            // wait for the next editor tick the way the on-screen path does.
            RenderNow();

            string stateName = _stateNames[_selectedStateIdx];
            string folder = Path.Combine(ExportRoot, stateName);
            Directory.CreateDirectory(Path.GetFullPath(folder));

            string path = Path.Combine(folder, $"ab_{stateName}_f{_currentFrame + 1:D3}.png");

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

        /// <summary>
        /// Puts every preview object on the isolated layer and marks it unsaveable. Both halves matter:
        /// the layer is what the preview camera culls to, and <c>HideAndDontSave</c> is what stops a
        /// Ctrl+S while this window is open from writing 50 transient part renderers into the scene.
        /// Hide flags do not inherit, so this has to walk the tree.
        /// </summary>
        static void ApplyPreviewFlags(Transform root)
        {
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
