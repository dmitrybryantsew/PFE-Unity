#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PFE.Data.Definitions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace PFE.Editor.Importers.SWF
{
    /// <summary>
    /// Editor window that assembles character body parts using placement data
    /// from CharacterAnimationDefinition and renders them in a preview scene.
    /// Allows per-part visibility/armor overrides and exports assembled frames to PNG.
    /// </summary>
    public class CharacterAnimationPreviewWindow : EditorWindow
    {
        const string ExportRoot = "Assets/_PFE/Art/Character/States";
        const int PreviewLayer = 31; // Use layer 31 for preview isolation
        const float PreviewOffsetX = 1000f; // Place preview objects far from scene

        // ─── Asset reference ─────────────────────────────────────
        CharacterAnimationDefinition _animDef;

        // ─── State selection ─────────────────────────────────────
        string[] _stateNames;
        int _selectedStateIdx;

        // ─── Per-part settings ───────────────────────────────────
        enum PartMode { Default, None, Armor }
        PartMode[] _partModes;
        int[] _partArmorIdx; // which armor set index per part (when mode == Armor)
        Dictionary<string, Vector2[]> _perArmorOffsets = new(); // armorId → per-part offsets
        bool _usePerArmorOffsets; // per-armor vs global offset mode
        string[] _armorLabels;
        int _allArmorIdx; // global armor selection for "All Armor" button

        // ─── Playback ────────────────────────────────────────────
        int _currentFrame;
        bool _isPlaying;
        float _playbackSpeed = 1f;
        double _lastFrameTime;

        // ─── Preview scene ───────────────────────────────────────
        GameObject _previewRoot;
        // Pool of SpriteRenderer slots for all placements (may exceed part count due to duplicates)
        List<GameObject> _slotObjects = new();
        List<SpriteRenderer> _slotRenderers = new();
        List<SpriteRenderer> _markerRenderers = new(); // Debug registration point markers
        int _maxPlacementsPerFrame;
        Texture2D _markerTex;
        Camera _previewCamera;
        RenderTexture _previewRT;
        int _previewSize = 256;

        // ─── UI state ────────────────────────────────────────────
        Vector2 _partsScrollPos;
        Vector2 _mainScrollPos;
        bool _previewSceneReady;
        string _statusMessage = "";

        // ─── Debug toggles ──────────────────────────────────────
        bool _debugShowRegistrationPoints;
        bool _debugDisableRotation;
        bool _debugUseCenterPivots;
        bool _debugBilinearSmallParts;
        int _debugBilinearThreshold = 20;
        bool _debugFoldout;
        bool _showPartOffsets;
        float _cameraZoom = 2f;
        Vector2 _cameraOffset;

        [MenuItem("PFE/Art/Character Animation Preview")]
        public static void ShowWindow()
        {
            var window = GetWindow<CharacterAnimationPreviewWindow>("Animation Preview");
            window.minSize = new Vector2(700, 500);
        }

        void OnEnable()
        {
            EditorApplication.update += OnEditorUpdate;
            // Try to auto-load the animation definition
            TryLoadDefinition();
        }

        void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            CleanupPreviewScene();
        }

        void OnDestroy()
        {
            CleanupPreviewScene();
        }

        void TryLoadDefinition()
        {
            if (_animDef != null) return;
            _animDef = AssetDatabase.LoadAssetAtPath<CharacterAnimationDefinition>(
                "Assets/_PFE/Data/Resources/Characters/PlayerAnimationDefinition.asset");
            if (_animDef != null)
                RefreshStateList();
        }

        void RefreshStateList()
        {
            if (_animDef == null || _animDef.stateClips == null)
            {
                _stateNames = Array.Empty<string>();
                return;
            }

            _stateNames = _animDef.stateClips.Select(c => c.stateName).ToArray();
            _selectedStateIdx = Mathf.Clamp(_selectedStateIdx, 0, Mathf.Max(0, _stateNames.Length - 1));

            // Init per-part settings
            int partCount = _animDef.parts?.Length ?? 0;
            _partModes = new PartMode[partCount];
            _partArmorIdx = new int[partCount];
            _perArmorOffsets.Clear();

            // Build armor labels
            var labels = new List<string> { "(none)" };
            if (_animDef.armorSets != null)
                labels.AddRange(_animDef.armorSets.Select(a => a.armorId));
            _armorLabels = labels.ToArray();
        }

        /// <summary>Get the active armor ID string, or "_default" if no armor.</summary>
        string GetActiveArmorKey()
        {
            if (_allArmorIdx > 0 && _animDef?.armorSets != null && _allArmorIdx - 1 < _animDef.armorSets.Length)
                return _animDef.armorSets[_allArmorIdx - 1].armorId;
            return "_default";
        }

        /// <summary>Get the offset array for a given armor key, creating if needed.</summary>
        Vector2[] GetOrCreateOffsets(string key)
        {
            if (!_perArmorOffsets.TryGetValue(key, out var offsets))
            {
                offsets = new Vector2[_animDef?.parts?.Length ?? 0];
                _perArmorOffsets[key] = offsets;
            }
            return offsets;
        }

        /// <summary>Get the current offset for a part, considering per-armor vs global mode.</summary>
        Vector2 GetPartOffset(int partIndex, string armorKey)
        {
            if (!_showPartOffsets) return Vector2.zero;

            if (_usePerArmorOffsets)
            {
                if (_perArmorOffsets.TryGetValue(armorKey, out var offsets) && partIndex < offsets.Length)
                    return offsets[partIndex];
                return Vector2.zero;
            }
            else
            {
                if (_perArmorOffsets.TryGetValue("_global", out var offsets) && partIndex < offsets.Length)
                    return offsets[partIndex];
                return Vector2.zero;
            }
        }

        // ─── Editor Update (playback) ───────────────────────────
        void OnEditorUpdate()
        {
            if (!_isPlaying || _animDef == null || _stateNames.Length == 0)
                return;

            var clip = _animDef.stateClips[_selectedStateIdx];
            if (clip.frameCount <= 1) return;

            double now = EditorApplication.timeSinceStartup;
            double frameDuration = 1.0 / (_animDef.frameRate * _playbackSpeed);

            if (now - _lastFrameTime >= frameDuration)
            {
                _lastFrameTime = now;
                _currentFrame++;

                int loopEnd = clip.EffectiveLoopEnd;
                if (_currentFrame >= loopEnd)
                {
                    switch (clip.loopMode)
                    {
                        case AnimationLoopMode.Loop:
                            _currentFrame = clip.loopStartFrame;
                            break;
                        case AnimationLoopMode.LoopRange:
                            _currentFrame = clip.loopStartFrame;
                            break;
                        case AnimationLoopMode.ClampForever:
                            _currentFrame = loopEnd - 1;
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
        }

        // ─── GUI ─────────────────────────────────────────────────
        void OnGUI()
        {
            _mainScrollPos = EditorGUILayout.BeginScrollView(_mainScrollPos);

            DrawHeader();
            EditorGUILayout.Space(4);

            if (_animDef == null)
            {
                EditorGUILayout.HelpBox("No CharacterAnimationDefinition loaded. Run the SWF import first.", MessageType.Warning);
                EditorGUILayout.EndScrollView();
                return;
            }

            if (_stateNames.Length == 0)
            {
                EditorGUILayout.HelpBox("Animation definition has no state clips.", MessageType.Warning);
                EditorGUILayout.EndScrollView();
                return;
            }

            EditorGUILayout.BeginHorizontal();

            // Left panel: preview
            EditorGUILayout.BeginVertical(GUILayout.Width(position.width * 0.55f));
            DrawPreviewPanel();
            EditorGUILayout.EndVertical();

            // Right panel: part controls
            EditorGUILayout.BeginVertical();
            DrawPartControls();
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);
            DrawExportControls();

            EditorGUILayout.Space(4);
            DrawDebugControls();

            if (!string.IsNullOrEmpty(_statusMessage))
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.HelpBox(_statusMessage, MessageType.Info);
            }

            EditorGUILayout.EndScrollView();
        }

        void DrawHeader()
        {
            EditorGUILayout.LabelField("Character Animation Preview", EditorStyles.boldLabel);

            // Asset reference
            EditorGUI.BeginChangeCheck();
            _animDef = (CharacterAnimationDefinition)EditorGUILayout.ObjectField(
                "Animation Definition", _animDef, typeof(CharacterAnimationDefinition), false);
            if (EditorGUI.EndChangeCheck())
            {
                RefreshStateList();
                CleanupPreviewScene();
            }

            if (_animDef == null) return;

            // State selector
            EditorGUI.BeginChangeCheck();
            _selectedStateIdx = EditorGUILayout.Popup("State", _selectedStateIdx, _stateNames);
            if (EditorGUI.EndChangeCheck())
            {
                _currentFrame = 0;
                _isPlaying = false;
                ApplyFrame();
            }

            var currentClip = _animDef.stateClips[_selectedStateIdx];
            EditorGUILayout.LabelField($"Frames: {currentClip.frameCount}  |  Loop: {currentClip.loopMode}  |  " +
                                       $"Run Mane: {currentClip.useRunMane}  |  Run Tail: {currentClip.useRunTail}");
        }

        void DrawPreviewPanel()
        {
            EditorGUILayout.LabelField("Preview", EditorStyles.boldLabel);

            // Render the preview texture
            if (_previewSceneReady && _previewRT != null)
            {
                float availWidth = position.width * 0.53f;
                float displaySize = Mathf.Min(availWidth, _previewSize);
                var rect = GUILayoutUtility.GetRect(displaySize, displaySize);
                EditorGUI.DrawPreviewTexture(rect, _previewRT, null, ScaleMode.ScaleToFit);
            }
            else
            {
                var rect = GUILayoutUtility.GetRect(200, 200);
                EditorGUI.DrawRect(rect, new Color(0.15f, 0.15f, 0.15f));
                GUI.Label(rect, "No preview - click Setup Preview", EditorStyles.centeredGreyMiniLabel);
            }

            // Camera controls — re-render on any change
            EditorGUI.BeginChangeCheck();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Zoom", GUILayout.Width(40));
            _cameraZoom = EditorGUILayout.Slider(_cameraZoom, 0.5f, 8f);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Offset", GUILayout.Width(40));
            _cameraOffset = EditorGUILayout.Vector2Field("", _cameraOffset, GUILayout.Width(200));
            if (GUILayout.Button("Reset", GUILayout.Width(50)))
                _cameraOffset = Vector2.zero;
            EditorGUILayout.EndHorizontal();

            // Preview size
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Render Size", GUILayout.Width(70));
            _previewSize = EditorGUILayout.IntSlider(_previewSize, 128, 1024);
            EditorGUILayout.EndHorizontal();

            if (EditorGUI.EndChangeCheck() && _previewSceneReady)
                RenderPreview();

            // Setup / refresh button
            if (!_previewSceneReady)
            {
                if (GUILayout.Button("Setup Preview", GUILayout.Height(28)))
                {
                    SetupPreviewScene();
                    ApplyFrame();
                }
            }
            else
            {
                if (GUILayout.Button("Rebuild Preview Scene", GUILayout.Height(22)))
                {
                    CleanupPreviewScene();
                    SetupPreviewScene();
                    ApplyFrame();
                }
            }

            // Playback controls
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Playback", EditorStyles.boldLabel);

            var clip = _animDef.stateClips[_selectedStateIdx];

            EditorGUILayout.BeginHorizontal();
            // Frame slider
            EditorGUI.BeginChangeCheck();
            _currentFrame = EditorGUILayout.IntSlider("Frame", _currentFrame, 0, Mathf.Max(0, clip.frameCount - 1));
            if (EditorGUI.EndChangeCheck())
            {
                _isPlaying = false;
                ApplyFrame();
            }
            EditorGUILayout.EndHorizontal();

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

        void DrawPartControls()
        {
            EditorGUILayout.LabelField("Parts", EditorStyles.boldLabel);

            if (_animDef.parts == null) return;

            // Quick actions
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("All Default"))
            {
                for (int i = 0; i < _partModes.Length; i++)
                    _partModes[i] = PartMode.Default;
                ApplyFrame();
            }
            if (GUILayout.Button("All None"))
            {
                for (int i = 0; i < _partModes.Length; i++)
                    _partModes[i] = PartMode.None;
                ApplyFrame();
            }
            EditorGUILayout.EndHorizontal();

            // All Armor: set all armor-aware parts to the same armor type
            if (_armorLabels != null && _armorLabels.Length > 1)
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("All Armor", GUILayout.Width(80)))
                {
                    for (int i = 0; i < _partModes.Length; i++)
                    {
                        if (_animDef.parts[i].isArmorAware)
                        {
                            _partModes[i] = PartMode.Armor;
                            _partArmorIdx[i] = _allArmorIdx;
                        }
                    }
                    ApplyFrame();
                }
                EditorGUI.BeginChangeCheck();
                _allArmorIdx = EditorGUILayout.Popup(_allArmorIdx, _armorLabels);
                if (EditorGUI.EndChangeCheck())
                {
                    // Auto-apply if already in armor mode
                    bool anyArmor = false;
                    for (int i = 0; i < _partModes.Length; i++)
                    {
                        if (_partModes[i] == PartMode.Armor)
                        {
                            _partArmorIdx[i] = _allArmorIdx;
                            anyArmor = true;
                        }
                    }
                    if (anyArmor) ApplyFrame();
                }
                EditorGUILayout.EndHorizontal();
            }

            _partsScrollPos = EditorGUILayout.BeginScrollView(_partsScrollPos, GUILayout.ExpandHeight(true));

            for (int i = 0; i < _animDef.parts.Length; i++)
            {
                var part = _animDef.parts[i];
                EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

                // Part name and info
                EditorGUILayout.LabelField($"{part.partName}", EditorStyles.boldLabel, GUILayout.Width(80));

                // Mode dropdown
                EditorGUI.BeginChangeCheck();
                _partModes[i] = (PartMode)EditorGUILayout.EnumPopup(_partModes[i], GUILayout.Width(65));

                // Armor selection (only visible when mode is Armor)
                if (_partModes[i] == PartMode.Armor && part.isArmorAware)
                {
                    _partArmorIdx[i] = EditorGUILayout.Popup(_partArmorIdx[i], _armorLabels, GUILayout.Width(80));
                }
                else if (_partModes[i] == PartMode.Armor && !part.isArmorAware)
                {
                    EditorGUILayout.LabelField("(no armor)", GUILayout.Width(80));
                    _partModes[i] = PartMode.Default;
                }

                if (EditorGUI.EndChangeCheck())
                    ApplyFrame();

                // Sprite thumbnail — always reserve space to avoid layout mismatch
                Sprite currentSprite = GetPartSpriteWithPivot(i).Item1;

                var thumbRect = GUILayoutUtility.GetRect(24, 24, GUILayout.Width(24));
                if (currentSprite != null)
                {
                    var tex = AssetPreview.GetAssetPreview(currentSprite);
                    if (tex != null)
                        GUI.DrawTexture(thumbRect, tex, ScaleMode.ScaleToFit);
                }

                // Per-part position offset (for manual alignment tweaking)
                if (_showPartOffsets)
                {
                    string offsetKey = _usePerArmorOffsets ? GetActiveArmorKey() : "_global";
                    var offsets = GetOrCreateOffsets(offsetKey);
                    if (i < offsets.Length)
                    {
                        EditorGUI.BeginChangeCheck();
                        offsets[i] = EditorGUILayout.Vector2Field(GUIContent.none, offsets[i], GUILayout.Width(140));
                        if (EditorGUI.EndChangeCheck())
                            ApplyFrame();
                        if (offsets[i] != Vector2.zero)
                        {
                            if (GUILayout.Button("×", GUILayout.Width(18), GUILayout.Height(18)))
                            {
                                offsets[i] = Vector2.zero;
                                ApplyFrame();
                            }
                        }
                    }
                }

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndScrollView();
        }

        void DrawExportControls()
        {
            EditorGUILayout.LabelField("Export / Debug", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Export Current Frame", GUILayout.Height(24)))
                ExportCurrentFrame();

            if (GUILayout.Button("Export All Frames", GUILayout.Height(24)))
                ExportAllFrames();

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Log Frame Placements", GUILayout.Height(22)))
                LogCurrentFramePlacements();
            if (GUILayout.Button("Log Part Dimensions", GUILayout.Height(22)))
                LogPartDimensions();
            EditorGUILayout.EndHorizontal();

            // Part offset controls
            EditorGUILayout.BeginHorizontal();
            _showPartOffsets = EditorGUILayout.ToggleLeft("Part Offsets", _showPartOffsets, GUILayout.Width(90));
            if (_showPartOffsets)
            {
                EditorGUI.BeginChangeCheck();
                _usePerArmorOffsets = EditorGUILayout.ToggleLeft("Per-Armor", _usePerArmorOffsets, GUILayout.Width(80));
                if (EditorGUI.EndChangeCheck() && _previewSceneReady)
                    ApplyFrame();

                string offsetKey = _usePerArmorOffsets ? GetActiveArmorKey() : "_global";
                bool hasAnyOffset = _perArmorOffsets.TryGetValue(offsetKey, out var curOffsets) &&
                    curOffsets.Any(o => o != Vector2.zero);

                if (_usePerArmorOffsets)
                    EditorGUILayout.LabelField(offsetKey, EditorStyles.miniLabel, GUILayout.Width(60));

                EditorGUI.BeginDisabledGroup(!hasAnyOffset);
                if (GUILayout.Button("Bake", GUILayout.Height(20), GUILayout.Width(50)))
                    BakeOffsetsToAsset();
                if (GUILayout.Button("Clear", GUILayout.Height(20), GUILayout.Width(45)))
                {
                    if (_perArmorOffsets.TryGetValue(offsetKey, out var ofs))
                    {
                        for (int i = 0; i < ofs.Length; i++)
                            ofs[i] = Vector2.zero;
                    }
                    ApplyFrame();
                }
                EditorGUI.EndDisabledGroup();
            }
            EditorGUILayout.EndHorizontal();
        }

        void DrawDebugControls()
        {
            _debugFoldout = EditorGUILayout.Foldout(_debugFoldout, "Debug", true);
            if (!_debugFoldout) return;

            EditorGUI.BeginChangeCheck();
            _debugDisableRotation = EditorGUILayout.ToggleLeft("Disable Rotation (show position-only)", _debugDisableRotation);
            _debugUseCenterPivots = EditorGUILayout.ToggleLeft("Force Center Pivots (0.5, 0.5)", _debugUseCenterPivots);
            _debugShowRegistrationPoints = EditorGUILayout.ToggleLeft("Show Registration Points (dots at pivot positions)", _debugShowRegistrationPoints);
            _debugBilinearSmallParts = EditorGUILayout.ToggleLeft("Bilinear filter for small parts", _debugBilinearSmallParts);
            if (_debugBilinearSmallParts)
            {
                EditorGUI.indentLevel++;
                _debugBilinearThreshold = EditorGUILayout.IntSlider("Size threshold (px)", _debugBilinearThreshold, 8, 64);
                EditorGUI.indentLevel--;
            }
            if (EditorGUI.EndChangeCheck() && _previewSceneReady)
                ApplyFrame();
        }

        /// <summary>
        /// Bakes the current part offsets into the CharacterAnimationDefinition asset.
        /// - Per-armor mode: writes to ArmorPartOverride.positionOffset for the active armor
        /// - Global mode: writes to all frame placements for default parts,
        ///   or to ArmorPartOverride.positionOffset for armor-overridden parts
        /// </summary>
        void BakeOffsetsToAsset()
        {
            if (_animDef == null) return;

            string offsetKey = _usePerArmorOffsets ? GetActiveArmorKey() : "_global";
            if (!_perArmorOffsets.TryGetValue(offsetKey, out var offsets)) return;

            int bakedCount = 0;

            // Bake armor-part offsets into ArmorPartOverride.positionOffset
            if (_usePerArmorOffsets && offsetKey != "_default")
            {
                // Per-armor mode: bake into the specific armor set
                if (_animDef.armorSets != null)
                {
                    foreach (var armorSet in _animDef.armorSets)
                    {
                        if (armorSet.armorId != offsetKey || armorSet.partOverrides == null) continue;
                        for (int i = 0; i < armorSet.partOverrides.Length; i++)
                        {
                            var ovr = armorSet.partOverrides[i];
                            if (ovr.partIndex < offsets.Length && offsets[ovr.partIndex] != Vector2.zero)
                            {
                                ovr.positionOffset += offsets[ovr.partIndex];
                                armorSet.partOverrides[i] = ovr;
                                string partName = ovr.partIndex < _animDef.parts.Length
                                    ? _animDef.parts[ovr.partIndex].partName : $"idx{ovr.partIndex}";
                                Debug.Log($"[AnimPreview] Baked offset ({offsets[ovr.partIndex].x:F4}, {offsets[ovr.partIndex].y:F4}) " +
                                    $"to {armorSet.armorId}/{partName} → total=({ovr.positionOffset.x:F4}, {ovr.positionOffset.y:F4})");
                                bakedCount++;
                            }
                        }
                        break;
                    }
                }
            }
            else
            {
                // Global / default mode: bake into frame placements or all armor sets
                for (int i = 0; i < offsets.Length; i++)
                {
                    if (offsets[i] == Vector2.zero) continue;
                    string name = i < _animDef.parts.Length ? _animDef.parts[i].partName : $"idx{i}";

                    // Bake into all frame placements
                    foreach (var clip in _animDef.stateClips)
                    {
                        foreach (var frame in clip.frames)
                        {
                            if (frame.partPlacements == null) continue;
                            for (int j = 0; j < frame.partPlacements.Length; j++)
                            {
                                if (frame.partPlacements[j].partIndex == i)
                                    frame.partPlacements[j].localPosition += offsets[i];
                            }
                        }
                    }
                    Debug.Log($"[AnimPreview] Baked global offset ({offsets[i].x:F4}, {offsets[i].y:F4}) " +
                        $"into all frame placements for {name}");
                    bakedCount++;
                }
            }

            if (bakedCount > 0)
            {
                EditorUtility.SetDirty(_animDef);
                AssetDatabase.SaveAssets();
                Debug.Log($"[AnimPreview] Baked {bakedCount} offsets to asset. Saved.");

                // Clear offsets after baking
                for (int i = 0; i < offsets.Length; i++)
                    offsets[i] = Vector2.zero;
                ApplyFrame();
            }
            else
            {
                Debug.Log("[AnimPreview] No offsets to bake.");
            }
        }

        void LogCurrentFramePlacements()
        {
            if (_animDef == null || _stateNames.Length == 0) return;

            var clip = _animDef.stateClips[_selectedStateIdx];
            int frameIdx = Mathf.Clamp(_currentFrame, 0, clip.frames.Length - 1);
            var frameData = clip.frames[frameIdx];

            Debug.Log($"[AnimPreview] State='{clip.stateName}' Frame={frameIdx} ({frameData.partPlacements?.Length ?? 0} placements)");

            if (frameData.partPlacements == null) return;

            // Track which head sub-parts have placements
            var headSubPartNames = new HashSet<string>
                { "head", "morda_base", "morda_armor", "morda_overlay", "eye", "forelock", "horn", "magic", "konec", "helm" };
            var foundHeadParts = new HashSet<string>();

            foreach (var p in frameData.partPlacements)
            {
                string partName = p.partIndex < _animDef.parts.Length
                    ? _animDef.parts[p.partIndex].partName
                    : $"idx{p.partIndex}";
                var pivot = p.partIndex < _animDef.parts.Length
                    ? _animDef.parts[p.partIndex].pivotNormalized
                    : Vector2.one * 0.5f;

                if (headSubPartNames.Contains(partName))
                    foundHeadParts.Add(partName);

                // Get sprite dimensions for pivot verification
                string spriteInfo = "";
                if (p.partIndex < _animDef.parts.Length)
                {
                    var sprite = _animDef.parts[p.partIndex].baseSprite;
                    if (sprite != null)
                    {
                        var rect = sprite.textureRect;
                        var spritePivot = sprite.pivot; // in pixels from bottom-left
                        spriteInfo = $" sprite={rect.width}x{rect.height} spritePivotPx=({spritePivot.x:F0},{spritePivot.y:F0})";
                    }
                    else
                    {
                        spriteInfo = " NO_SPRITE";
                    }
                }

                Debug.Log($"  {partName}: pos=({p.localPosition.x:F3}, {p.localPosition.y:F3}) " +
                          $"rot={p.localRotation:F1} scale=({p.localScale.x:F2}, {p.localScale.y:F2}) " +
                          $"pivot=({pivot.x:F3}, {pivot.y:F3}){spriteInfo} visible={p.visible}");
            }

            // Report head sub-part coverage
            var missingHeadParts = headSubPartNames.Where(n => !foundHeadParts.Contains(n)).ToList();
            Debug.Log($"[AnimPreview] Head sub-parts in frame: [{string.Join(", ", foundHeadParts)}]");
            if (missingHeadParts.Count > 0)
                Debug.LogWarning($"[AnimPreview] Head sub-parts MISSING from frame: [{string.Join(", ", missingHeadParts)}]");
        }

        /// <summary>
        /// Logs all part definitions with their sprite sizes and pivots for verification.
        /// </summary>
        void LogPartDimensions()
        {
            if (_animDef == null) return;

            Debug.Log("[AnimPreview] === Part Dimensions & Pivots ===");
            foreach (var part in _animDef.parts)
            {
                string info = $"  {part.partName} (sym{part.symbolId}): definedPivot=({part.pivotNormalized.x:F3}, {part.pivotNormalized.y:F3})";
                if (part.baseSprite != null)
                {
                    var rect = part.baseSprite.textureRect;
                    var spritePivot = part.baseSprite.pivot;
                    float spritePivotNormX = spritePivot.x / rect.width;
                    float spritePivotNormY = spritePivot.y / rect.height;
                    info += $" png={rect.width}x{rect.height}px" +
                            $" spritePivotPx=({spritePivot.x:F1},{spritePivot.y:F1})" +
                            $" spritePivotNorm=({spritePivotNormX:F3},{spritePivotNormY:F3})" +
                            $" match={Mathf.Abs(spritePivotNormX - part.pivotNormalized.x) < 0.01f && Mathf.Abs(spritePivotNormY - part.pivotNormalized.y) < 0.01f}";
                }
                else
                {
                    info += " NO SPRITE";
                }
                Debug.Log(info);
            }
        }

        // ─── Preview Scene Management ────────────────────────────

        void SetupPreviewScene()
        {
            CleanupPreviewScene();

            if (_animDef == null || _animDef.parts == null) return;

            // Create preview root far from scene objects, on dedicated layer
            _previewRoot = new GameObject("[AnimPreview_Root]")
            {
                hideFlags = HideFlags.HideAndDontSave,
                layer = PreviewLayer
            };
            _previewRoot.transform.position = new Vector3(PreviewOffsetX, 0, 0);

            // Find max placements per frame across all clips (handles duplicate parts like left/right legs)
            _maxPlacementsPerFrame = 0;
            foreach (var clip in _animDef.stateClips)
            {
                if (clip.frames == null) continue;
                foreach (var frame in clip.frames)
                {
                    if (frame.partPlacements != null)
                        _maxPlacementsPerFrame = Mathf.Max(_maxPlacementsPerFrame, frame.partPlacements.Length);
                }
            }

            // Find unlit sprite material for URP compatibility
            var unlitShader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default")
                           ?? Shader.Find("Sprites/Default");
            Material unlitMat = null;
            if (unlitShader != null)
            {
                unlitMat = new Material(unlitShader) { hideFlags = HideFlags.HideAndDontSave };
            }

            // Create a pool of SpriteRenderer slots (one per placement, not per part)
            _slotObjects = new List<GameObject>();
            _slotRenderers = new List<SpriteRenderer>();

            for (int i = 0; i < _maxPlacementsPerFrame; i++)
            {
                var go = new GameObject($"slot_{i}")
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    layer = PreviewLayer
                };
                go.transform.SetParent(_previewRoot.transform);

                var sr = go.AddComponent<SpriteRenderer>();
                if (unlitMat != null)
                    sr.material = unlitMat;

                _slotObjects.Add(go);
                _slotRenderers.Add(sr);
            }

            // Create debug marker sprites (small colored dots at registration points)
            _markerRenderers = new List<SpriteRenderer>();
            CreateMarkerTexture();
            var markerSprite = Sprite.Create(_markerTex,
                new Rect(0, 0, _markerTex.width, _markerTex.height),
                new Vector2(0.5f, 0.5f), 100f);
            markerSprite.hideFlags = HideFlags.HideAndDontSave;

            for (int i = 0; i < _maxPlacementsPerFrame; i++)
            {
                var markerGo = new GameObject($"marker_{i}")
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    layer = PreviewLayer
                };
                markerGo.transform.SetParent(_previewRoot.transform);
                var markerSr = markerGo.AddComponent<SpriteRenderer>();
                markerSr.sprite = markerSprite;
                markerSr.color = Color.red;
                markerSr.sortingOrder = 1000 + i; // Always on top
                markerSr.enabled = false;
                if (unlitMat != null) markerSr.material = unlitMat;
                _markerRenderers.Add(markerSr);
            }

            // Create camera — NOT parented to root
            var camGo = new GameObject("[AnimPreview_Camera]")
            {
                hideFlags = HideFlags.HideAndDontSave,
                layer = PreviewLayer
            };
            _previewCamera = camGo.AddComponent<Camera>();
            _previewCamera.clearFlags = CameraClearFlags.SolidColor;
            _previewCamera.backgroundColor = new Color(0.18f, 0.18f, 0.18f, 1f);
            _previewCamera.orthographic = true;
            _previewCamera.orthographicSize = _cameraZoom;
            _previewCamera.cullingMask = 1 << PreviewLayer;
            _previewCamera.enabled = false;

            // Create render texture
            RecreateRenderTexture();

            _previewSceneReady = true;
            _statusMessage = $"Preview ready: {_animDef.parts.Length} parts, {_maxPlacementsPerFrame} max slots";
        }

        void CreateMarkerTexture()
        {
            if (_markerTex != null) return;
            int size = 6;
            _markerTex = new Texture2D(size, size) { hideFlags = HideFlags.HideAndDontSave };
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dist = Vector2.Distance(new Vector2(x, y), new Vector2(size / 2f, size / 2f));
                    _markerTex.SetPixel(x, y, dist < size / 2f ? Color.white : Color.clear);
                }
            _markerTex.Apply();
            _markerTex.filterMode = FilterMode.Point;
        }

        void RecreateRenderTexture()
        {
            if (_previewRT != null)
                DestroyImmediate(_previewRT);
            _previewRT = new RenderTexture(_previewSize, _previewSize, 16)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
        }

        void CleanupPreviewScene()
        {
            if (_previewRoot != null)
                DestroyImmediate(_previewRoot);

            // Camera is not parented to root, clean up separately
            if (_previewCamera != null)
                DestroyImmediate(_previewCamera.gameObject);

            if (_previewRT != null)
                DestroyImmediate(_previewRT);

            if (_markerTex != null)
                DestroyImmediate(_markerTex);

            _previewRoot = null;
            _slotObjects.Clear();
            _slotRenderers.Clear();
            _markerRenderers.Clear();
            _markerTex = null;
            _previewCamera = null;
            _previewRT = null;
            _previewSceneReady = false;
        }

        // ─── Frame Application ───────────────────────────────────

        void ApplyFrame()
        {
            if (!_previewSceneReady || _animDef == null) return;

            var clip = _animDef.stateClips[_selectedStateIdx];
            if (clip.frames == null || clip.frames.Length == 0) return;

            int frameIdx = Mathf.Clamp(_currentFrame, 0, clip.frames.Length - 1);
            var frameData = clip.frames[frameIdx];

            // Hide all slots and markers
            for (int i = 0; i < _slotRenderers.Count; i++)
                _slotRenderers[i].enabled = false;
            for (int i = 0; i < _markerRenderers.Count; i++)
                _markerRenderers[i].enabled = false;

            if (frameData.partPlacements == null)
            {
                RenderPreview();
                return;
            }

            // Build set of head sub-parts that should be hidden based on active armor.
            // When armor is selected, morda_armor(273) frame data may hide certain sub-parts.
            // The morda_armor composite itself is NEVER rendered — we always use individual sub-parts.
            var hiddenHeadParts = new HashSet<string>();
            string activeArmorId = null;

            // Find the active armor from any armor-aware part (use korpus as reference)
            for (int i = 0; i < _animDef.parts.Length; i++)
            {
                if (_animDef.parts[i].partName == "korpus" && _partModes[i] == PartMode.Armor && _partArmorIdx[i] > 0)
                {
                    int armorSetIdx = _partArmorIdx[i] - 1;
                    if (armorSetIdx >= 0 && _animDef.armorSets != null && armorSetIdx < _animDef.armorSets.Length)
                        activeArmorId = _animDef.armorSets[armorSetIdx].armorId;
                    break;
                }
            }

            // If armor is active, check for hidden head sub-parts
            bool usesMordaComposite = false;
            if (activeArmorId != null && _animDef.armorSets != null)
            {
                foreach (var armorSet in _animDef.armorSets)
                {
                    if (armorSet.armorId != activeArmorId) continue;
                    if (armorSet.hiddenHeadParts != null)
                    {
                        foreach (string hidden in armorSet.hiddenHeadParts)
                            hiddenHeadParts.Add(hidden);
                    }

                    // Check if this armor uses morda_armor composite (late armors)
                    // If so, hide individual head sub-parts to avoid double rendering
                    if (armorSet.partOverrides != null)
                    {
                        int mordaPartIdx = -1;
                        for (int pi = 0; pi < _animDef.parts.Length; pi++)
                        {
                            if (_animDef.parts[pi].partName == "morda_armor")
                            { mordaPartIdx = pi; break; }
                        }
                        if (mordaPartIdx >= 0)
                        {
                            foreach (var ovr in armorSet.partOverrides)
                            {
                                if (ovr.partIndex == mordaPartIdx && ovr.armorSprite != null)
                                {
                                    usesMordaComposite = true;
                                    // Hide all individual head sub-parts when composite is shown
                                    foreach (string subPart in HeadSubPartNames)
                                        hiddenHeadParts.Add(subPart);
                                    break;
                                }
                            }
                        }
                    }
                    break;
                }
            }

            // Assign each placement to a slot
            int slotIdx = 0;
            foreach (var placement in frameData.partPlacements)
            {
                if (slotIdx >= _slotRenderers.Count) break;
                if (!placement.visible) continue;

                int partIndex = placement.partIndex;
                if (partIndex >= _animDef.parts.Length) continue;

                var part = _animDef.parts[partIndex];

                // Skip morda_armor composite UNLESS this armor has a specific override for it
                // (late armors without sub-part variants use the composite instead)
                if (part.partName == "morda_armor")
                {
                    bool hasMordaOverride = false;
                    if (activeArmorId != null && _animDef.armorSets != null)
                    {
                        foreach (var armorSet in _animDef.armorSets)
                        {
                            if (armorSet.armorId != activeArmorId) continue;
                            if (armorSet.partOverrides != null)
                            {
                                foreach (var ovr in armorSet.partOverrides)
                                {
                                    if (ovr.partIndex == partIndex && ovr.armorSprite != null)
                                    { hasMordaOverride = true; break; }
                                }
                            }
                            break;
                        }
                    }
                    if (!hasMordaOverride) continue;
                }

                // Check user visibility
                if (_partModes[partIndex] == PartMode.None)
                    continue;

                // Mane/tail swap
                if (part.partName == "mane" && clip.useRunMane) continue;
                if (part.partName == "mane_run" && !clip.useRunMane) continue;
                if (part.partName == "tail" && clip.useRunTail) continue;
                if (part.partName == "tail_run" && !clip.useRunTail) continue;

                // Head sub-parts hidden by armor (e.g. some armors remove forelock/eye)
                if (hiddenHeadParts.Contains(part.partName))
                    continue;

                var sr = _slotRenderers[slotIdx];
                var go = _slotObjects[slotIdx];

                sr.enabled = true;
                sr.sortingOrder = slotIdx; // Preserve SWF depth order (placements are already depth-sorted)

                var (originalSprite, spritePivot, bakedOffset) = GetPartSpriteWithPivot(partIndex, activeArmorId);
                if (originalSprite != null)
                {
                    Vector2 pivot;
                    if (_debugUseCenterPivots)
                        pivot = new Vector2(0.5f, 0.5f);
                    else
                        pivot = spritePivot;
                    var tex = originalSprite.texture;
                    var rect = originalSprite.textureRect;
                    sr.sprite = Sprite.Create(tex, rect, pivot, originalSprite.pixelsPerUnit);

                    // Bilinear filtering for small sprites (smooths pixelated vector-sourced parts)
                    if (_debugBilinearSmallParts &&
                        (rect.width <= _debugBilinearThreshold || rect.height <= _debugBilinearThreshold))
                        tex.filterMode = FilterMode.Bilinear;
                    else
                        tex.filterMode = FilterMode.Point;
                }
                else
                {
                    sr.sprite = null;
                }

                var t = go.transform;
                string armorKey = _usePerArmorOffsets
                    ? (activeArmorId ?? "_default") : "_global";
                Vector2 manualOffset = GetPartOffset(partIndex, armorKey);
                t.localPosition = new Vector3(
                    placement.localPosition.x + bakedOffset.x + manualOffset.x,
                    placement.localPosition.y + bakedOffset.y + manualOffset.y, 0);

                if (_debugDisableRotation)
                    t.localRotation = Quaternion.identity;
                else
                    t.localRotation = Quaternion.Euler(0, 0, placement.localRotation);

                t.localScale = new Vector3(placement.localScale.x, placement.localScale.y, 1);

                // Debug: show registration point marker
                if (_debugShowRegistrationPoints && slotIdx < _markerRenderers.Count)
                {
                    var marker = _markerRenderers[slotIdx];
                    marker.enabled = true;
                    marker.transform.localPosition = new Vector3(placement.localPosition.x, placement.localPosition.y, -0.01f);
                    marker.transform.localScale = Vector3.one * 0.15f;
                }

                slotIdx++;
            }

            RenderPreview();
        }

        /// <summary>Head sub-part names that get their armor sprites from morda_armor(273) variants.</summary>
        static readonly HashSet<string> HeadSubPartNames = new()
            { "morda_base", "morda_overlay", "eye", "forelock", "horn", "magic", "konec", "helm" };

        /// <summary>
        /// Returns the sprite, pivot, and baked position offset for a given part, considering armor overrides.
        /// </summary>
        (Sprite sprite, Vector2 pivot, Vector2 bakedOffset) GetPartSpriteWithPivot(int partIndex, string activeArmorId = null)
        {
            if (_animDef.parts == null || partIndex >= _animDef.parts.Length)
                return (null, new Vector2(0.5f, 0.5f), Vector2.zero);

            var part = _animDef.parts[partIndex];
            Vector2 defaultPivot = part.pivotNormalized;

            // If armor mode and armor-aware (body parts), try to get armor sprite
            if (_partModes[partIndex] == PartMode.Armor && part.isArmorAware)
            {
                int armorSetIdx = _partArmorIdx[partIndex] - 1; // index 0 = "(none)"
                if (armorSetIdx >= 0 && _animDef.armorSets != null && armorSetIdx < _animDef.armorSets.Length)
                {
                    var armorSet = _animDef.armorSets[armorSetIdx];
                    if (armorSet.partOverrides != null)
                    {
                        foreach (var ovr in armorSet.partOverrides)
                        {
                            if (ovr.partIndex == partIndex && ovr.armorSprite != null)
                                return (ovr.armorSprite, ovr.pivotOverride, ovr.positionOffset);
                        }
                    }
                }
            }

            // Head sub-parts: when armor is active, check for variant sprites
            // These aren't toggled per-part — they follow the global armor selection
            if (activeArmorId != null && HeadSubPartNames.Contains(part.partName))
            {
                if (_animDef.armorSets != null)
                {
                    foreach (var armorSet in _animDef.armorSets)
                    {
                        if (armorSet.armorId != activeArmorId) continue;
                        if (armorSet.partOverrides == null) break;

                        foreach (var ovr in armorSet.partOverrides)
                        {
                            if (ovr.partIndex == partIndex && ovr.armorSprite != null)
                                return (ovr.armorSprite, ovr.pivotOverride, ovr.positionOffset);
                        }
                        break;
                    }
                }
            }

            return (part.baseSprite, defaultPivot, Vector2.zero);
        }

        void RenderPreview()
        {
            if (_previewCamera == null || _previewRT == null) return;

            // Check if render texture size changed
            if (_previewRT.width != _previewSize)
                RecreateRenderTexture();

            // Update camera settings (localPosition is relative to _previewRoot)
            _previewCamera.orthographicSize = _cameraZoom;
            _previewCamera.transform.position = new Vector3(
                PreviewOffsetX + _cameraOffset.x,
                _cameraOffset.y,
                -10);
            _previewCamera.targetTexture = _previewRT;
            _previewCamera.Render();
            _previewCamera.targetTexture = null;
        }

        // ─── Export ──────────────────────────────────────────────

        void ExportCurrentFrame()
        {
            if (!_previewSceneReady)
            {
                _statusMessage = "Setup preview first!";
                return;
            }

            string stateName = _stateNames[_selectedStateIdx];
            string folder = Path.Combine(ExportRoot, stateName);
            EnsureDirectory(folder);

            string fileName = $"f{_currentFrame + 1:D3}.png";
            string path = Path.Combine(folder, fileName);

            SaveRenderTextureToPNG(_previewRT, path);
            AssetDatabase.Refresh();

            _statusMessage = $"Exported: {path}";
        }

        void ExportAllFrames()
        {
            if (!_previewSceneReady)
            {
                _statusMessage = "Setup preview first!";
                return;
            }

            var clip = _animDef.stateClips[_selectedStateIdx];
            string stateName = _stateNames[_selectedStateIdx];
            string folder = Path.Combine(ExportRoot, stateName);
            EnsureDirectory(folder);

            int savedFrame = _currentFrame;

            for (int f = 0; f < clip.frameCount; f++)
            {
                _currentFrame = f;
                ApplyFrame();

                string fileName = $"f{f + 1:D3}.png";
                string path = Path.Combine(folder, fileName);
                SaveRenderTextureToPNG(_previewRT, path);
            }

            // Restore original frame
            _currentFrame = savedFrame;
            ApplyFrame();

            AssetDatabase.Refresh();
            _statusMessage = $"Exported {clip.frameCount} frames to {folder}";
        }

        void SaveRenderTextureToPNG(RenderTexture rt, string assetPath)
        {
            var prevActive = RenderTexture.active;
            RenderTexture.active = rt;

            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();

            RenderTexture.active = prevActive;

            byte[] pngData = tex.EncodeToPNG();
            DestroyImmediate(tex);

            string fullPath = Path.GetFullPath(assetPath);
            File.WriteAllBytes(fullPath, pngData);
        }

        static void EnsureDirectory(string assetPath)
        {
            string fullPath = Path.GetFullPath(assetPath);
            if (!Directory.Exists(fullPath))
                Directory.CreateDirectory(fullPath);
        }
    }
}
#endif
