#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using PFE.Data.Definitions;
using UnityEditor;
using UnityEngine;

namespace PFE.Editor.Importers.SWF
{
    /// <summary>
    /// Editor window that renders and animates the main menu background composition.
    /// Uses SpriteRenderers in a hidden preview scene, similar to CharacterAnimationPreviewWindow.
    /// Replicates the Displ.as procedural animation: eye blink, pistol bob, magic rotation,
    /// spark sequences, and lightning storms.
    /// </summary>
    public class MainMenuCompositionPreviewWindow : EditorWindow
    {
        const int PreviewLayer = 31;
        const float PreviewOffsetX = 2000f;
        const float FlashPPU = 100f; // Pixels per unit matching sprite import settings

        // ─── Asset reference ─────────────────────────────────────
        MainMenuCompositionDefinition _def;

        // ─── Preview scene objects ───────────────────────────────
        GameObject _previewRoot;
        Camera _previewCamera;
        RenderTexture _previewRT;

        // Additive blend material for glow effects
        Material _additiveMat;

        // Layer renderers
        SpriteRenderer _skySR, _citySR, _logoSR;
        SpriteRenderer _hornPieceSR, _earPieceSR;
        SpriteRenderer _displ1SR, _displ2SR;
        SpriteRenderer _eyeSR;
        SpriteRenderer _hornMagicGlowSR, _pistolMagicGlowSR, _pistolMagic2GlowSR;
        SpriteRenderer _hornKrugSR, _pistolKrugSR, _pistolKrug2SR;
        SpriteRenderer _pistolBodySR;
        SpriteRenderer _lightningCloudsSR;
        SpriteRenderer _lightningBoltSR;
        List<SpriteRenderer> _hornSparkSRs = new();
        List<SpriteRenderer> _pistolSparkSRs = new();

        // Pivots for animated groups
        Transform _pipkaRoot;
        Transform _pistolGroup;
        Transform _hornGroup;
        Transform _grozaGroup;

        // ─── Animation state ─────────────────────────────────────
        bool _isPlaying = true;
        int _animFrame;
        double _lastFrameTime;

        // Eye blink
        int _blinkCountdown;
        int _eyeFrameIndex;

        // Lightning
        int _lightningCountdown;
        int _lightningBurstCounter;
        bool _lightningVisible;

        // ─── UI state ────────────────────────────────────────────
        Vector2 _scrollPos;
        float _cameraZoom = 4f; // Half-height of 800px stage at 100 PPU
        Vector2 _cameraOffset = new(6.4f, -4f); // Center of 1280x800 Flash stage
        int _previewWidth = 800;
        int _previewHeight = 400;
        bool _showAnimControls = true;
        bool _showLayerControls;
        bool _previewReady;

        // ─── Per-element offsets & toggles ───────────────────────
        Vector2 _offPipka, _offHornPiece, _offEarPiece, _offEye;
        Vector2 _offHorn, _offPistol;
        Vector2 _offDisplMane, _offDisplTail;
        Vector2 _offHornMagic, _offPistolMagic;
        bool _showHornMagic = true, _showPistolMagic = true;
        bool _showDisplMane = true, _showDisplTail = true;
        bool _showHornPiece = true, _showEarPiece = true;
        bool _showSparks = true;

        [MenuItem("PFE/Art/Main Menu Composition Preview")]
        public static void ShowWindow()
        {
            var window = GetWindow<MainMenuCompositionPreviewWindow>("Menu Preview");
            window.minSize = new Vector2(600, 500);
        }

        void OnEnable()
        {
            EditorApplication.update += OnEditorUpdate;
            TryLoadDefinition();
        }

        void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            CleanupPreview();
        }

        void OnDestroy() => CleanupPreview();

        void TryLoadDefinition()
        {
            if (_def != null) return;
            _def = AssetDatabase.LoadAssetAtPath<MainMenuCompositionDefinition>(
                "Assets/_PFE/Data/Resources/MainMenu/MainMenuComposition.asset");
        }

        // ─── Editor Update (animation) ──────────────────────────
        void OnEditorUpdate()
        {
            if (!_isPlaying || _def == null || !_previewReady)
                return;

            double now = EditorApplication.timeSinceStartup;
            double frameDuration = 1.0 / _def.frameRate;

            if (now - _lastFrameTime >= frameDuration)
            {
                _lastFrameTime = now;
                _animFrame++;
                AnimateFrame();
                RenderPreview();
                Repaint();
            }
        }

        // ─── GUI ─────────────────────────────────────────────────
        void OnGUI()
        {
            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Main Menu Composition Preview", EditorStyles.boldLabel);

            // Definition reference
            var newDef = (MainMenuCompositionDefinition)EditorGUILayout.ObjectField(
                "Composition", _def, typeof(MainMenuCompositionDefinition), false);
            if (newDef != _def)
            {
                _def = newDef;
                CleanupPreview();
            }

            if (_def == null)
            {
                EditorGUILayout.HelpBox(
                    "No MainMenuCompositionDefinition loaded.\nRun 'PFE > Art > Import Main Menu From SWF' first.",
                    MessageType.Warning);
                EditorGUILayout.EndScrollView();
                return;
            }

            // Preview controls
            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();

            if (!_previewReady)
            {
                if (GUILayout.Button("Build Preview Scene", GUILayout.Height(28)))
                {
                    BuildPreviewScene();
                    ResetAnimation();
                    RenderPreview();
                }
            }
            else
            {
                if (GUILayout.Button(_isPlaying ? "Pause" : "Play", GUILayout.Width(60), GUILayout.Height(28)))
                {
                    _isPlaying = !_isPlaying;
                    _lastFrameTime = EditorApplication.timeSinceStartup;
                }

                if (GUILayout.Button("Reset", GUILayout.Width(60), GUILayout.Height(28)))
                {
                    ResetAnimation();
                    AnimateFrame();
                    RenderPreview();
                }

                if (GUILayout.Button("Rebuild", GUILayout.Width(60), GUILayout.Height(28)))
                {
                    CleanupPreview();
                    BuildPreviewScene();
                    ResetAnimation();
                    RenderPreview();
                }

                GUILayout.FlexibleSpace();
                EditorGUILayout.LabelField($"Frame: {_animFrame}", EditorStyles.miniLabel, GUILayout.Width(100));
            }

            EditorGUILayout.EndHorizontal();

            // Camera controls
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Zoom", GUILayout.Width(40));
            _cameraZoom = EditorGUILayout.Slider(_cameraZoom, 1f, 15f);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Offset", GUILayout.Width(40));
            _cameraOffset = EditorGUILayout.Vector2Field("", _cameraOffset);
            EditorGUILayout.EndHorizontal();

            // Preview image
            if (_previewRT != null && _previewReady)
            {
                UpdateCamera();
                RenderPreview();

                EditorGUILayout.Space(4);
                float aspect = (float)_previewWidth / _previewHeight;
                float displayWidth = Mathf.Min(position.width - 20, _previewWidth);
                float displayHeight = displayWidth / aspect;
                Rect previewRect = GUILayoutUtility.GetRect(displayWidth, displayHeight);
                EditorGUI.DrawPreviewTexture(previewRect, _previewRT);
            }

            // Animation controls foldout
            if (_previewReady)
            {
                EditorGUILayout.Space(4);
                _showAnimControls = EditorGUILayout.Foldout(_showAnimControls, "Animation State");
                if (_showAnimControls)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.LabelField($"Eye blink countdown: {_blinkCountdown} frames");
                    EditorGUILayout.LabelField($"Eye frame: {_eyeFrameIndex}");
                    EditorGUILayout.LabelField($"Lightning countdown: {_lightningCountdown} frames");
                    EditorGUILayout.LabelField($"Lightning visible: {_lightningVisible}");

                    if (GUILayout.Button("Force Lightning", GUILayout.Width(120)))
                    {
                        _lightningCountdown = 0;
                        AnimateFrame();
                        RenderPreview();
                    }

                    if (GUILayout.Button("Force Blink", GUILayout.Width(120)))
                    {
                        _blinkCountdown = 0;
                        AnimateFrame();
                        RenderPreview();
                    }

                    EditorGUI.indentLevel--;
                }

                // Layer position controls
                EditorGUILayout.Space(4);
                _showLayerControls = EditorGUILayout.Foldout(_showLayerControls, "Layer Offsets & Visibility");
                if (_showLayerControls)
                {
                    EditorGUI.indentLevel++;
                    bool changed = false;
                    changed |= DrawLayerOffset("Pipka", ref _offPipka);
                    changed |= DrawLayerOffset("Mane (displ)", ref _offDisplMane);
                    changed |= DrawLayerToggle("  Show", ref _showDisplMane);
                    changed |= DrawLayerOffset("Tail (displ)", ref _offDisplTail);
                    changed |= DrawLayerToggle("  Show", ref _showDisplTail);
                    changed |= DrawLayerOffset("Horn piece", ref _offHornPiece);
                    changed |= DrawLayerToggle("  Show", ref _showHornPiece);
                    changed |= DrawLayerOffset("Ear piece", ref _offEarPiece);
                    changed |= DrawLayerToggle("  Show", ref _showEarPiece);
                    changed |= DrawLayerOffset("Eye", ref _offEye);
                    changed |= DrawLayerOffset("Horn group", ref _offHorn);
                    changed |= DrawLayerOffset("Horn magic", ref _offHornMagic);
                    changed |= DrawLayerToggle("  Show horn magic", ref _showHornMagic);
                    changed |= DrawLayerOffset("Pistol group", ref _offPistol);
                    changed |= DrawLayerOffset("Pistol magic", ref _offPistolMagic);
                    changed |= DrawLayerToggle("  Show pistol magic", ref _showPistolMagic);
                    changed |= DrawLayerToggle("Show sparks", ref _showSparks);

                    if (changed) ApplyLayerOffsets();

                    if (GUILayout.Button("Reset All Offsets", GUILayout.Width(140)))
                    {
                        _offPipka = _offHornPiece = _offEarPiece = _offEye = Vector2.zero;
                        _offHorn = _offPistol = _offDisplMane = _offDisplTail = Vector2.zero;
                        _offHornMagic = _offPistolMagic = Vector2.zero;
                        _showHornMagic = _showPistolMagic = true;
                        _showDisplMane = _showDisplTail = true;
                        _showHornPiece = _showEarPiece = true;
                        _showSparks = true;
                        ApplyLayerOffsets();
                    }
                    EditorGUI.indentLevel--;
                }
            }

            EditorGUILayout.EndScrollView();
        }

        // ─── Preview Scene Construction ─────────────────────────
        void BuildPreviewScene()
        {
            CleanupPreview();

            _previewRoot = new GameObject("[MainMenuPreview]")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            _previewRoot.transform.position = new Vector3(PreviewOffsetX, 0, 0);

            // Camera
            var camObj = new GameObject("PreviewCamera")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            camObj.transform.SetParent(_previewRoot.transform, false);
            _previewCamera = camObj.AddComponent<Camera>();
            _previewCamera.orthographic = true;
            _previewCamera.orthographicSize = _cameraZoom;
            _previewCamera.clearFlags = CameraClearFlags.SolidColor;
            _previewCamera.backgroundColor = Color.black;
            _previewCamera.cullingMask = 1 << PreviewLayer;
            _previewCamera.enabled = false;
            _previewCamera.nearClipPlane = -100f;
            _previewCamera.farClipPlane = 100f;

            _previewRT = new RenderTexture(_previewWidth, _previewHeight, 24);
            _previewCamera.targetTexture = _previewRT;

            // Build layers (Flash Y-down → Unity Y-up, positions in pixels → divide by PPU)
            int sortOrder = 0;

            // Background sky
            _skySR = CreateLayer("Sky", _def.sky, _def.skyPosition, ref sortOrder);

            // City scene
            _citySR = CreateLayer("CityScene", _def.cityScene, _def.cityScenePosition, ref sortOrder);

            // Pipka root (animated foreground)
            _pipkaRoot = CreateGroup("Pipka", FlashToUnity(_def.pipkaPosition));

            // Displacement targets (behind other pipka parts) — animated heat-haze
            _displ1SR = CreateChildLayer("DisplMane", _pipkaRoot, _def.displacementMane,
                FlashToUnity(_def.displacementManeLocalPos), ref sortOrder);
            _displ2SR = CreateChildLayer("DisplTail", _pipkaRoot, _def.displacementTail,
                FlashToUnity(_def.displacementTailLocalPos), ref sortOrder);

            // Horn piece
            _hornPieceSR = CreateChildLayer("HornPiece", _pipkaRoot, _def.hornPiece,
                FlashToUnity(_def.hornPieceLocalPos), ref sortOrder);

            // Ear piece
            _earPieceSR = CreateChildLayer("EarPiece", _pipkaRoot, _def.earPiece,
                FlashToUnity(_def.earPieceLocalPos), ref sortOrder);

            // Eye
            Sprite eyeSprite = _def.eyeFrames != null && _def.eyeFrames.Length > 0 ? _def.eyeFrames[0] : null;
            _eyeSR = CreateChildLayer("Eye", _pipkaRoot, eyeSprite,
                FlashToUnity(_def.eyeLocalPos), ref sortOrder);

            // Horn group
            _hornGroup = CreateChildGroup("Horn", _pipkaRoot, FlashToUnity(_def.hornLocalPos));

            _hornMagicGlowSR = CreateChildLayer("HornMagicGlow", _hornGroup, _def.hornMagicGlow,
                Vector3.zero, ref sortOrder);
            MakeAdditive(_hornMagicGlowSR);
            _hornKrugSR = CreateChildLayer("HornKrug", _hornGroup, _def.magicKrug,
                Vector3.zero, ref sortOrder);
            MakeAdditive(_hornKrugSR);
            if (_hornKrugSR != null)
            {
                _hornKrugSR.transform.localScale = Vector3.one * _def.hornKrugScale;
                _hornKrugSR.transform.localRotation = Quaternion.Euler(0, 0, -90f);
            }

            // Horn sparks
            _hornSparkSRs.Clear();
            if (_def.hornSparkInstances != null)
            {
                Sprite sparkSprite = _def.hornSparkFrames != null && _def.hornSparkFrames.Length > 0
                    ? _def.hornSparkFrames[0] : null;
                for (int i = 0; i < _def.hornSparkInstances.Length; i++)
                {
                    var sr = CreateChildLayer($"HornSpark_{i}", _hornGroup, sparkSprite,
                        FlashToUnity(_def.hornSparkInstances[i].localPosition), ref sortOrder);
                    MakeAdditive(sr);
                    _hornSparkSRs.Add(sr);
                }
            }

            // Pistol group (bobs as a unit)
            _pistolGroup = CreateChildGroup("Pistol", _pipkaRoot, FlashToUnity(_def.pistolLocalPos));

            _pistolBodySR = CreateChildLayer("PistolBody", _pistolGroup, _def.pistolBody,
                Vector3.zero, ref sortOrder);

            _pistolMagicGlowSR = CreateChildLayer("PistolMagicGlow", _pistolGroup, _def.pistolMagicGlow,
                Vector3.zero, ref sortOrder);
            MakeAdditive(_pistolMagicGlowSR);
            _pistolKrugSR = CreateChildLayer("PistolKrug", _pistolGroup, _def.magicKrug,
                Vector3.zero, ref sortOrder);
            MakeAdditive(_pistolKrugSR);

            _pistolMagic2GlowSR = CreateChildLayer("PistolMagic2Glow", _pistolGroup, _def.pistolMagic2Glow,
                Vector3.zero, ref sortOrder);
            MakeAdditive(_pistolMagic2GlowSR);
            _pistolKrug2SR = CreateChildLayer("PistolKrug2", _pistolGroup, _def.magicKrug,
                Vector3.zero, ref sortOrder);
            MakeAdditive(_pistolKrug2SR);
            if (_pistolKrug2SR != null)
                _pistolKrug2SR.transform.localRotation = Quaternion.Euler(0, 0, -90f);

            // Pistol sparks
            _pistolSparkSRs.Clear();
            if (_def.pistolSparkInstances != null)
            {
                Sprite sparkSprite = _def.pistolSparkFrames != null && _def.pistolSparkFrames.Length > 0
                    ? _def.pistolSparkFrames[0] : null;
                for (int i = 0; i < _def.pistolSparkInstances.Length; i++)
                {
                    var sr = CreateChildLayer($"PistolSpark_{i}", _pistolGroup, sparkSprite,
                        FlashToUnity(_def.pistolSparkInstances[i].localPosition), ref sortOrder);
                    MakeAdditive(sr);
                    _pistolSparkSRs.Add(sr);
                }
            }

            // Logo (on top of pipka)
            _logoSR = CreateLayer("Logo", _def.logo, _def.logoPosition, ref sortOrder);

            // Lightning/storm group
            _grozaGroup = CreateGroup("Groza", FlashToUnity(_def.grozaDefaultPos));
            _lightningCloudsSR = CreateChildLayer("LightningClouds", _grozaGroup, _def.lightningClouds,
                FlashToUnity(_def.lightningCloudsLocalPos), ref sortOrder);
            MakeAdditive(_lightningCloudsSR); // Screen blend in Flash → additive approximation

            Sprite boltSprite = _def.lightningBoltFrames != null && _def.lightningBoltFrames.Length > 0
                ? _def.lightningBoltFrames[0] : null;
            _lightningBoltSR = CreateChildLayer("LightningBolt", _grozaGroup, boltSprite,
                FlashToUnity(_def.lightningBoltLocalPos), ref sortOrder);
            MakeAdditive(_lightningBoltSR);

            // Cloud mask — use SpriteMask to clip the clouds to the mask rectangle
            if (_def.lightningMaskRect.width > 0 && _lightningCloudsSR != null)
            {
                // Set clouds to only render inside the mask
                _lightningCloudsSR.maskInteraction = SpriteMaskInteraction.VisibleInsideMask;

                // Create a white square sprite for the mask
                var maskGo = new GameObject("CloudMask") { hideFlags = HideFlags.HideAndDontSave };
                maskGo.layer = PreviewLayer;
                maskGo.transform.SetParent(_grozaGroup, false);

                var maskRect = _def.lightningMaskRect;
                // Position mask at center of the rect (Flash coords → Unity)
                float maskCenterX = (maskRect.x + maskRect.width * 0.5f) / FlashPPU;
                float maskCenterY = -(maskRect.y + maskRect.height * 0.5f) / FlashPPU;
                maskGo.transform.localPosition = new Vector3(maskCenterX, maskCenterY, 0);
                // Scale to match mask size (sprite is 1x1 unit by default)
                maskGo.transform.localScale = new Vector3(
                    maskRect.width / FlashPPU,
                    maskRect.height / FlashPPU,
                    1);

                var spriteMask = maskGo.AddComponent<SpriteMask>();
                spriteMask.sprite = CreateWhiteSquareSprite();
            }

            // Initially hide lightning
            _grozaGroup.gameObject.SetActive(false);

            UpdateCamera();
            _previewReady = true;
        }

        // ─── Layer creation helpers ─────────────────────────────

        SpriteRenderer CreateLayer(string name, Sprite sprite, Vector2 flashPos, ref int sortOrder)
        {
            var go = new GameObject(name) { hideFlags = HideFlags.HideAndDontSave };
            go.layer = PreviewLayer;
            go.transform.SetParent(_previewRoot.transform, false);
            go.transform.localPosition = FlashToUnity(flashPos);

            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.sortingOrder = sortOrder++;
            return sr;
        }

        Transform CreateGroup(string name, Vector3 localPos)
        {
            var go = new GameObject(name) { hideFlags = HideFlags.HideAndDontSave };
            go.layer = PreviewLayer;
            go.transform.SetParent(_previewRoot.transform, false);
            go.transform.localPosition = localPos;
            return go.transform;
        }

        Transform CreateChildGroup(string name, Transform parent, Vector3 localPos)
        {
            var go = new GameObject(name) { hideFlags = HideFlags.HideAndDontSave };
            go.layer = PreviewLayer;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            return go.transform;
        }

        SpriteRenderer CreateChildLayer(string name, Transform parent, Sprite sprite,
            Vector3 localPos, ref int sortOrder)
        {
            var go = new GameObject(name) { hideFlags = HideFlags.HideAndDontSave };
            go.layer = PreviewLayer;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;

            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.sortingOrder = sortOrder++;
            return sr;
        }

        /// <summary>
        /// Convert Flash coordinates (Y-down, pixels) to Unity local position (Y-up, units).
        /// </summary>
        Vector3 FlashToUnity(Vector2 flashPos)
        {
            return new Vector3(flashPos.x / FlashPPU, -flashPos.y / FlashPPU, 0);
        }

        /// <summary>
        /// Get or create an additive blend material for glow/magic sprites.
        /// Uses Sprites/Default shader with SrcAlpha + One blending.
        /// </summary>
        Material GetAdditiveMaterial()
        {
            if (_additiveMat != null) return _additiveMat;

            // Sprites/Default works in both built-in and URP as a fallback
            var shader = Shader.Find("Sprites/Default");
            if (shader == null)
                shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default");

            _additiveMat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            // SrcAlpha + One = additive blending (black = invisible, bright = glows)
            _additiveMat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            _additiveMat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.One);
            // Disable depth write for transparent additive
            _additiveMat.SetInt("_ZWrite", 0);
            _additiveMat.renderQueue = 3100; // Transparent+100
            return _additiveMat;
        }

        void MakeAdditive(SpriteRenderer sr)
        {
            if (sr != null)
                sr.material = GetAdditiveMaterial();
        }

        /// <summary>
        /// Create a 1x1 white sprite for use as a SpriteMask shape.
        /// Scaled via transform to match the desired mask area.
        /// </summary>
        Sprite CreateWhiteSquareSprite()
        {
            var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            var pixels = new Color32[16];
            for (int i = 0; i < 16; i++)
                pixels[i] = new Color32(255, 255, 255, 255);
            tex.SetPixels32(pixels);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 4f);
        }

        // ─── Layer offset UI helpers ────────────────────────────

        bool DrawLayerOffset(string label, ref Vector2 offset)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, GUILayout.Width(100));
            var newOff = EditorGUILayout.Vector2Field("", offset);
            EditorGUILayout.EndHorizontal();
            if (newOff != offset) { offset = newOff; return true; }
            return false;
        }

        bool DrawLayerToggle(string label, ref bool value)
        {
            bool newVal = EditorGUILayout.Toggle(label, value);
            if (newVal != value) { value = newVal; return true; }
            return false;
        }

        Vector3 Off(Vector2 off) => new(off.x / FlashPPU, -off.y / FlashPPU, 0);

        void ApplyLayerOffsets()
        {
            if (!_previewReady || _def == null) return;

            // Pipka root
            if (_pipkaRoot != null)
                _pipkaRoot.localPosition = FlashToUnity(_def.pipkaPosition) + Off(_offPipka);

            // Displacement
            if (_displ1SR != null)
            {
                _displ1SR.transform.localPosition = FlashToUnity(_def.displacementManeLocalPos) + Off(_offDisplMane);
                _displ1SR.enabled = _showDisplMane;
            }
            if (_displ2SR != null)
            {
                _displ2SR.transform.localPosition = FlashToUnity(_def.displacementTailLocalPos) + Off(_offDisplTail);
                _displ2SR.enabled = _showDisplTail;
            }

            // Body pieces
            if (_hornPieceSR != null)
            {
                _hornPieceSR.transform.localPosition = FlashToUnity(_def.hornPieceLocalPos) + Off(_offHornPiece);
                _hornPieceSR.enabled = _showHornPiece;
            }
            if (_earPieceSR != null)
            {
                _earPieceSR.transform.localPosition = FlashToUnity(_def.earPieceLocalPos) + Off(_offEarPiece);
                _earPieceSR.enabled = _showEarPiece;
            }

            // Eye
            if (_eyeSR != null)
                _eyeSR.transform.localPosition = FlashToUnity(_def.eyeLocalPos) + Off(_offEye);

            // Horn group
            if (_hornGroup != null)
                _hornGroup.localPosition = FlashToUnity(_def.hornLocalPos) + Off(_offHorn);

            // Horn magic visibility + offset
            if (_hornMagicGlowSR != null)
            {
                _hornMagicGlowSR.transform.localPosition = Off(_offHornMagic);
                _hornMagicGlowSR.enabled = _showHornMagic;
            }
            if (_hornKrugSR != null)
                _hornKrugSR.enabled = _showHornMagic;

            // Horn sparks
            foreach (var sr in _hornSparkSRs)
                if (sr != null) sr.enabled = _showSparks;

            // Pistol group
            if (_pistolGroup != null)
                _pistolGroup.localPosition = FlashToUnity(_def.pistolLocalPos) + Off(_offPistol);

            // Pistol magic visibility + offset
            if (_pistolMagicGlowSR != null)
            {
                _pistolMagicGlowSR.transform.localPosition = Off(_offPistolMagic);
                _pistolMagicGlowSR.enabled = _showPistolMagic;
            }
            if (_pistolKrugSR != null)
                _pistolKrugSR.enabled = _showPistolMagic;
            if (_pistolMagic2GlowSR != null)
                _pistolMagic2GlowSR.enabled = _showPistolMagic;
            if (_pistolKrug2SR != null)
                _pistolKrug2SR.enabled = _showPistolMagic;

            // Pistol sparks
            foreach (var sr in _pistolSparkSRs)
                if (sr != null) sr.enabled = _showSparks;

            RenderPreview();
        }

        // ─── Animation ──────────────────────────────────────────

        void ResetAnimation()
        {
            _animFrame = 0;
            _eyeFrameIndex = 0;
            _blinkCountdown = RandomBlinkInterval();
            _lightningCountdown = RandomLightningInterval();
            _lightningVisible = false;
            _lightningBurstCounter = 0;
        }

        void AnimateFrame()
        {
            if (_def == null || !_previewReady) return;

            // ─── Eye blink ──────────────────────────────────────
            _blinkCountdown--;
            if (_blinkCountdown <= 0 && _eyeFrameIndex == 0)
            {
                // Start blink sequence
                _eyeFrameIndex = 1;
            }

            if (_eyeFrameIndex > 0)
            {
                // Advance through blink frames: 0(open) → 1 → 2(closed) → 3 → 4(open) → reset
                if (_animFrame % 3 == 0) // advance every 3 frames for visible blink speed
                {
                    _eyeFrameIndex++;
                    if (_eyeFrameIndex >= (_def.eyeFrames?.Length ?? 5))
                    {
                        _eyeFrameIndex = 0;
                        _blinkCountdown = RandomBlinkInterval();
                    }
                }
            }

            if (_eyeSR != null && _def.eyeFrames != null && _eyeFrameIndex < _def.eyeFrames.Length)
                _eyeSR.sprite = _def.eyeFrames[_eyeFrameIndex];

            // ─── Displacement animation (mane/tail wobble) ─────
            // Approximates Flash DisplacementMapFilter with transform oscillation.
            // Mane: scaleX=10, scaleY=15 → visible X+Y wobble
            // Tail: scaleX=0, scaleY=5 → vertical-only gentle wobble
            {
                float t = _animFrame / _def.frameRate;
                float maneBaseX = _def.displacementManeLocalPos.x / FlashPPU;
                float maneBaseY = -_def.displacementManeLocalPos.y / FlashPPU;
                float maneWobX = (Mathf.Sin(t * 1.0f) * 0.2f + Mathf.Sin(t * 1.7f) * 0.25f) * 10f / FlashPPU;
                float maneWobY = (Mathf.Sin(t * 0.8f + 1.5f) * 0.1f + Mathf.Cos(t * 1.3f) * 0.2f) * 10f / FlashPPU;
                if (_displ1SR != null)
                    _displ1SR.transform.localPosition = new Vector3(maneBaseX + maneWobX, maneBaseY + maneWobY, 0);

                float tailBaseX = _def.displacementTailLocalPos.x / FlashPPU;
                float tailBaseY = -_def.displacementTailLocalPos.y / FlashPPU;
                float tailWobY = (Mathf.Sin(t * 1.0f) * 0.1f + Mathf.Sin(t * 1.5f) * 0.15f) * 1f / FlashPPU;
                if (_displ2SR != null)
                    _displ2SR.transform.localPosition = new Vector3(tailBaseX, tailBaseY + tailWobY, 0);
            }

            // ─── Pistol bob ─────────────────────────────────────
            if (_pistolGroup != null)
            {
                float t = _animFrame;
                float baseX = _def.pistolLocalPos.x / FlashPPU;
                float baseY = -_def.pistolLocalPos.y / FlashPPU;
                float bobX = Mathf.Sin(t / _def.pistolBobSpeed) * _def.pistolBobAmplitudeX / FlashPPU;
                float bobY = (Mathf.Cos(t / _def.pistolBobSpeed) - 1f) * _def.pistolBobAmplitudeY / FlashPPU;
                _pistolGroup.localPosition = new Vector3(baseX + bobX, baseY + bobY, 0);
            }

            // ─── Magic circle rotation ──────────────────────────
            float rotDeg1 = _animFrame * _def.magicRotSpeed1;
            float rotDeg2 = 90f + _animFrame * _def.magicRotSpeed2;

            if (_hornKrugSR != null)
                _hornKrugSR.transform.localRotation = Quaternion.Euler(0, 0, -rotDeg2);
            if (_pistolKrugSR != null)
                _pistolKrugSR.transform.localRotation = Quaternion.Euler(0, 0, -rotDeg1);
            if (_pistolKrug2SR != null)
                _pistolKrug2SR.transform.localRotation = Quaternion.Euler(0, 0, -rotDeg2);

            // ─── Spark frame sequences ──────────────────────────
            if (_def.hornSparkFrames != null && _def.hornSparkFrames.Length > 0)
            {
                for (int i = 0; i < _hornSparkSRs.Count; i++)
                {
                    if (_hornSparkSRs[i] == null) continue;
                    // Offset each spark instance by a different phase
                    int frame = (_animFrame + i * 7) % _def.hornSparkFrames.Length;
                    _hornSparkSRs[i].sprite = _def.hornSparkFrames[frame];
                }
            }

            if (_def.pistolSparkFrames != null && _def.pistolSparkFrames.Length > 0)
            {
                for (int i = 0; i < _pistolSparkSRs.Count; i++)
                {
                    if (_pistolSparkSRs[i] == null) continue;
                    int frame = (_animFrame + i * 5) % _def.pistolSparkFrames.Length;
                    _pistolSparkSRs[i].sprite = _def.pistolSparkFrames[frame];
                }
            }

            // ─── Lightning storm ────────────────────────────────
            // From Displ.as: gr.x/y are absolute Flash coords replacing default pos.
            // tuchi (clouds) offset randomly within groza each spawn.
            // moln (bolt) stays at authored position, only rotation/frame change.
            _lightningCountdown--;

            if (_lightningCountdown == 0)
            {
                // Spawn lightning
                _lightningVisible = true;
                _lightningBurstCounter = 0;

                if (_grozaGroup != null)
                {
                    // Displ.as: gr.x = random*1800, gr.y = random*350 (Flash coords)
                    float flashX = UnityEngine.Random.value * _def.lightningSpawnWidth;
                    float flashY = UnityEngine.Random.value * _def.lightningSpawnHeight;

                    // Scale: 1 - y/800 (perspective: lower = smaller = farther)
                    float scale = 1f - flashY / 800f;

                    // Convert to Unity coords (Y-flip)
                    _grozaGroup.localPosition = new Vector3(
                        flashX / FlashPPU,
                        -flashY / FlashPPU,
                        0);
                    _grozaGroup.localScale = Vector3.one * Mathf.Max(0.3f, scale);
                    _grozaGroup.gameObject.SetActive(true);

                    // Displ.as: tuchi.x = -200 - random*400, tuchi.y = -200 - random*300
                    if (_lightningCloudsSR != null)
                    {
                        float tuchiX = (-200f - UnityEngine.Random.value * 400f) / FlashPPU;
                        float tuchiY = -(-200f - UnityEngine.Random.value * 300f) / FlashPPU; // Y-flip
                        _lightningCloudsSR.transform.localPosition = new Vector3(tuchiX, tuchiY, 0);
                    }

                    // Random bolt frame and rotation (moln.moln.rotation = random*360)
                    if (_lightningBoltSR != null && _def.lightningBoltFrames != null &&
                        _def.lightningBoltFrames.Length > 0)
                    {
                        int boltFrame = UnityEngine.Random.Range(0, _def.lightningBoltFrames.Length);
                        _lightningBoltSR.sprite = _def.lightningBoltFrames[boltFrame];
                        _lightningBoltSR.transform.localRotation =
                            Quaternion.Euler(0, 0, UnityEngine.Random.value * 360f);
                    }

                    // Reset alpha to full
                    SetGroupAlpha(_lightningCloudsSR, 1f);
                    SetGroupAlpha(_lightningBoltSR, 1f);
                }
            }
            else if (_lightningCountdown < 0)
            {
                _lightningBurstCounter++;

                // Flicker alpha (from Displ.as):
                // gr.alpha = min(1, random*0.5 + t_groza/12 + 0.7)
                float alpha = Mathf.Min(1f,
                    UnityEngine.Random.value * 0.5f + _lightningCountdown / 12f + 0.7f);
                alpha = Mathf.Max(0f, alpha);

                if (_grozaGroup != null)
                {
                    SetGroupAlpha(_lightningCloudsSR, alpha);
                    SetGroupAlpha(_lightningBoltSR, alpha);
                }

                // Random early cutoff (10% chance after frame -6)
                if (_lightningCountdown < -6 && UnityEngine.Random.value < 0.1f)
                    _lightningCountdown = -100;

                // Hide after burst
                if (_lightningCountdown < -30)
                {
                    _lightningCountdown = RandomLightningInterval();
                    _lightningVisible = false;
                    if (_grozaGroup != null)
                        _grozaGroup.gameObject.SetActive(false);
                }
            }
        }

        static void SetGroupAlpha(SpriteRenderer sr, float alpha)
        {
            if (sr == null) return;
            var c = sr.color;
            c.a = Mathf.Clamp01(alpha);
            sr.color = c;
        }

        int RandomBlinkInterval()
        {
            return Mathf.RoundToInt(UnityEngine.Random.Range(
                _def.blinkIntervalMin * _def.frameRate,
                _def.blinkIntervalMax * _def.frameRate));
        }

        int RandomLightningInterval()
        {
            return Mathf.RoundToInt(UnityEngine.Random.Range(
                _def.lightningIntervalMin * _def.frameRate,
                _def.lightningIntervalMax * _def.frameRate));
        }

        // ─── Camera & Rendering ─────────────────────────────────

        void UpdateCamera()
        {
            if (_previewCamera == null) return;
            _previewCamera.orthographicSize = _cameraZoom;
            _previewCamera.transform.localPosition = new Vector3(
                _cameraOffset.x,
                _cameraOffset.y,
                -10);
        }

        void RenderPreview()
        {
            if (_previewCamera == null || _previewRT == null) return;
            _previewCamera.Render();
        }

        // ─── Cleanup ────────────────────────────────────────────

        void CleanupPreview()
        {
            _previewReady = false;
            _hornSparkSRs.Clear();
            _pistolSparkSRs.Clear();

            if (_previewRoot != null)
                DestroyImmediate(_previewRoot);
            _previewRoot = null;

            if (_previewRT != null)
            {
                _previewRT.Release();
                DestroyImmediate(_previewRT);
            }
            _previewRT = null;
            _previewCamera = null;

            if (_additiveMat != null)
                DestroyImmediate(_additiveMat);
            _additiveMat = null;

        }
    }
}
#endif
