using System.Collections.Generic;
using PFE.Data.Definitions;
using UnityEngine;
using UnityEngine.Rendering;

namespace PFE.UI.MainMenu
{
    /// <summary>
    /// Runtime version of the main-menu composition preview.
    /// Rebuilds the imported Flash background from SpriteRenderers and animates it at runtime.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MainMenuBackgroundRenderer : MonoBehaviour
    {
        const int BackgroundLayer = 30;
        const float FlashPPU = 100f;

        [Header("Composition")]
        [SerializeField] MainMenuCompositionDefinition composition;

        [Header("Camera Framing")]
        [SerializeField] float cameraZoom = 4f;
        [SerializeField] Vector2 cameraOffset = new(8f, -4f);
        [SerializeField] float cameraDepth = -50f;

        [Header("Playback")]
        [SerializeField] bool playAnimation = true;

        GameObject _runtimeRoot;
        Camera _backgroundCamera;
        Material _additiveMaterial;
        Texture2D _whiteMaskTexture;
        Sprite _whiteMaskSprite;

        SpriteRenderer _skySR;
        SpriteRenderer _citySR;
        SpriteRenderer _logoSR;
        SpriteRenderer _hornPieceSR;
        SpriteRenderer _earPieceSR;
        SpriteRenderer _displ1SR;
        SpriteRenderer _displ2SR;
        SpriteRenderer _eyeSR;
        SpriteRenderer _hornMagicGlowSR;
        SpriteRenderer _hornKrugSR;
        SpriteRenderer _pistolBodySR;
        SpriteRenderer _pistolMagicGlowSR;
        SpriteRenderer _pistolKrugSR;
        SpriteRenderer _pistolMagic2GlowSR;
        SpriteRenderer _pistolKrug2SR;
        SpriteRenderer _lightningCloudsSR;
        SpriteRenderer _lightningBoltSR;

        readonly List<SpriteRenderer> _hornSparkSRs = new();
        readonly List<SpriteRenderer> _pistolSparkSRs = new();

        Transform _pipkaRoot;
        Transform _pistolGroup;
        Transform _hornGroup;
        Transform _grozaGroup;

        bool _isBuilt;
        float _frameAccumulator;
        int _animFrame;
        int _blinkCountdown;
        int _eyeFrameIndex;
        int _lightningCountdown;
        public void Configure(MainMenuCompositionDefinition definition)
        {
            if (composition == definition && _isBuilt)
            {
                UpdateCamera();
                return;
            }

            composition = definition;
            if (isActiveAndEnabled)
            {
                Rebuild();
            }
        }

        public void ConfigureFraming(float zoom, Vector2 offset)
        {
            cameraZoom = zoom;
            cameraOffset = offset;
            UpdateCamera();
        }

        void Awake()
        {
            if (composition == null)
            {
                composition = Resources.Load<MainMenuCompositionDefinition>("MainMenu/MainMenuComposition");
            }
        }

        void OnEnable()
        {
            if (!_isBuilt && composition != null)
            {
                Rebuild();
            }
            else
            {
                UpdateCamera();
            }
        }

        void Update()
        {
            if (!_isBuilt || composition == null || !playAnimation)
            {
                return;
            }

            float frameDuration = 1f / Mathf.Max(1f, composition.frameRate);
            _frameAccumulator += Time.unscaledDeltaTime;

            while (_frameAccumulator >= frameDuration)
            {
                _frameAccumulator -= frameDuration;
                _animFrame++;
                AnimateFrame();
            }
        }

        void OnDestroy()
        {
            Cleanup();
        }

        void Rebuild()
        {
            Cleanup();

            if (composition == null)
            {
                Debug.LogWarning("[MainMenuBackgroundRenderer] No composition assigned.");
                return;
            }

            BuildRuntimeScene();
            ResetAnimation();
            UpdateCamera();
            _isBuilt = true;
        }

        void BuildRuntimeScene()
        {
            _runtimeRoot = new GameObject("GeneratedMainMenuBackgroundRuntime");
            _runtimeRoot.transform.SetParent(transform, false);
            _runtimeRoot.transform.localPosition = Vector3.zero;
            _runtimeRoot.transform.localRotation = Quaternion.identity;
            _runtimeRoot.transform.localScale = Vector3.one;

            var cameraObject = new GameObject("MainMenuBackgroundCamera");
            cameraObject.transform.SetParent(_runtimeRoot.transform, false);
            _backgroundCamera = cameraObject.AddComponent<Camera>();
            _backgroundCamera.orthographic = true;
            _backgroundCamera.clearFlags = CameraClearFlags.SolidColor;
            _backgroundCamera.backgroundColor = Color.black;
            _backgroundCamera.cullingMask = 1 << BackgroundLayer;
            _backgroundCamera.nearClipPlane = -100f;
            _backgroundCamera.farClipPlane = 100f;
            _backgroundCamera.depth = cameraDepth;

            int sortOrder = 0;

            _skySR = CreateLayer("Sky", composition.sky, composition.skyPosition, ref sortOrder);
            _citySR = CreateLayer("CityScene", composition.cityScene, composition.cityScenePosition, ref sortOrder);

            _pipkaRoot = CreateGroup("Pipka", FlashToUnity(composition.pipkaPosition));

            _displ1SR = CreateChildLayer("DisplMane", _pipkaRoot, composition.displacementMane,
                FlashToUnity(composition.displacementManeLocalPos), ref sortOrder);
            _displ2SR = CreateChildLayer("DisplTail", _pipkaRoot, composition.displacementTail,
                FlashToUnity(composition.displacementTailLocalPos), ref sortOrder);

            _hornPieceSR = CreateChildLayer("HornPiece", _pipkaRoot, composition.hornPiece,
                FlashToUnity(composition.hornPieceLocalPos), ref sortOrder);
            _earPieceSR = CreateChildLayer("EarPiece", _pipkaRoot, composition.earPiece,
                FlashToUnity(composition.earPieceLocalPos), ref sortOrder);

            Sprite eyeSprite = composition.eyeFrames != null && composition.eyeFrames.Length > 0
                ? composition.eyeFrames[0]
                : null;
            _eyeSR = CreateChildLayer("Eye", _pipkaRoot, eyeSprite,
                FlashToUnity(composition.eyeLocalPos), ref sortOrder);

            _hornGroup = CreateChildGroup("Horn", _pipkaRoot, FlashToUnity(composition.hornLocalPos));
            _hornMagicGlowSR = CreateChildLayer("HornMagicGlow", _hornGroup, composition.hornMagicGlow,
                Vector3.zero, ref sortOrder);
            MakeAdditive(_hornMagicGlowSR);

            _hornKrugSR = CreateChildLayer("HornKrug", _hornGroup, composition.magicKrug,
                Vector3.zero, ref sortOrder);
            MakeAdditive(_hornKrugSR);
            if (_hornKrugSR != null)
            {
                _hornKrugSR.transform.localScale = Vector3.one * composition.hornKrugScale;
                _hornKrugSR.transform.localRotation = Quaternion.Euler(0f, 0f, -90f);
            }

            _hornSparkSRs.Clear();
            if (composition.hornSparkInstances != null)
            {
                Sprite sparkSprite = composition.hornSparkFrames != null && composition.hornSparkFrames.Length > 0
                    ? composition.hornSparkFrames[0]
                    : null;

                for (int i = 0; i < composition.hornSparkInstances.Length; i++)
                {
                    SpriteRenderer spark = CreateChildLayer($"HornSpark_{i}", _hornGroup, sparkSprite,
                        FlashToUnity(composition.hornSparkInstances[i].localPosition), ref sortOrder);
                    MakeAdditive(spark);
                    _hornSparkSRs.Add(spark);
                }
            }

            _pistolGroup = CreateChildGroup("Pistol", _pipkaRoot, FlashToUnity(composition.pistolLocalPos));
            _pistolBodySR = CreateChildLayer("PistolBody", _pistolGroup, composition.pistolBody,
                Vector3.zero, ref sortOrder);

            _pistolMagicGlowSR = CreateChildLayer("PistolMagicGlow", _pistolGroup, composition.pistolMagicGlow,
                Vector3.zero, ref sortOrder);
            MakeAdditive(_pistolMagicGlowSR);

            _pistolKrugSR = CreateChildLayer("PistolKrug", _pistolGroup, composition.magicKrug,
                Vector3.zero, ref sortOrder);
            MakeAdditive(_pistolKrugSR);

            _pistolMagic2GlowSR = CreateChildLayer("PistolMagic2Glow", _pistolGroup, composition.pistolMagic2Glow,
                Vector3.zero, ref sortOrder);
            MakeAdditive(_pistolMagic2GlowSR);

            _pistolKrug2SR = CreateChildLayer("PistolKrug2", _pistolGroup, composition.magicKrug,
                Vector3.zero, ref sortOrder);
            MakeAdditive(_pistolKrug2SR);
            if (_pistolKrug2SR != null)
            {
                _pistolKrug2SR.transform.localRotation = Quaternion.Euler(0f, 0f, -90f);
            }

            _pistolSparkSRs.Clear();
            if (composition.pistolSparkInstances != null)
            {
                Sprite sparkSprite = composition.pistolSparkFrames != null && composition.pistolSparkFrames.Length > 0
                    ? composition.pistolSparkFrames[0]
                    : null;

                for (int i = 0; i < composition.pistolSparkInstances.Length; i++)
                {
                    SpriteRenderer spark = CreateChildLayer($"PistolSpark_{i}", _pistolGroup, sparkSprite,
                        FlashToUnity(composition.pistolSparkInstances[i].localPosition), ref sortOrder);
                    MakeAdditive(spark);
                    _pistolSparkSRs.Add(spark);
                }
            }

            _logoSR = CreateLayer("Logo", composition.logo, composition.logoPosition, ref sortOrder);

            _grozaGroup = CreateGroup("Groza", FlashToUnity(composition.grozaDefaultPos));
            _lightningCloudsSR = CreateChildLayer("LightningClouds", _grozaGroup, composition.lightningClouds,
                FlashToUnity(composition.lightningCloudsLocalPos), ref sortOrder);
            MakeAdditive(_lightningCloudsSR);

            Sprite boltSprite = composition.lightningBoltFrames != null && composition.lightningBoltFrames.Length > 0
                ? composition.lightningBoltFrames[0]
                : null;
            _lightningBoltSR = CreateChildLayer("LightningBolt", _grozaGroup, boltSprite,
                FlashToUnity(composition.lightningBoltLocalPos), ref sortOrder);
            MakeAdditive(_lightningBoltSR);

            if (composition.lightningMaskRect.width > 0f && _lightningCloudsSR != null)
            {
                _lightningCloudsSR.maskInteraction = SpriteMaskInteraction.VisibleInsideMask;

                var maskObject = new GameObject("CloudMask");
                maskObject.layer = BackgroundLayer;
                maskObject.transform.SetParent(_grozaGroup, false);

                Rect maskRect = composition.lightningMaskRect;
                float maskCenterX = (maskRect.x + maskRect.width * 0.5f) / FlashPPU;
                float maskCenterY = -(maskRect.y + maskRect.height * 0.5f) / FlashPPU;
                maskObject.transform.localPosition = new Vector3(maskCenterX, maskCenterY, 0f);
                maskObject.transform.localScale = new Vector3(maskRect.width / FlashPPU, maskRect.height / FlashPPU, 1f);

                var spriteMask = maskObject.AddComponent<SpriteMask>();
                spriteMask.sprite = CreateWhiteSquareSprite();
            }

            if (_grozaGroup != null)
            {
                _grozaGroup.gameObject.SetActive(false);
            }
        }

        SpriteRenderer CreateLayer(string name, Sprite sprite, Vector2 flashPos, ref int sortOrder)
        {
            var layerObject = new GameObject(name);
            layerObject.layer = BackgroundLayer;
            layerObject.transform.SetParent(_runtimeRoot.transform, false);
            layerObject.transform.localPosition = FlashToUnity(flashPos);

            var renderer = layerObject.AddComponent<SpriteRenderer>();
            renderer.sprite = sprite;
            renderer.sortingOrder = sortOrder++;
            return renderer;
        }

        Transform CreateGroup(string name, Vector3 localPosition)
        {
            var groupObject = new GameObject(name);
            groupObject.layer = BackgroundLayer;
            groupObject.transform.SetParent(_runtimeRoot.transform, false);
            groupObject.transform.localPosition = localPosition;
            return groupObject.transform;
        }

        Transform CreateChildGroup(string name, Transform parent, Vector3 localPosition)
        {
            var groupObject = new GameObject(name);
            groupObject.layer = BackgroundLayer;
            groupObject.transform.SetParent(parent, false);
            groupObject.transform.localPosition = localPosition;
            return groupObject.transform;
        }

        SpriteRenderer CreateChildLayer(string name, Transform parent, Sprite sprite, Vector3 localPosition, ref int sortOrder)
        {
            var layerObject = new GameObject(name);
            layerObject.layer = BackgroundLayer;
            layerObject.transform.SetParent(parent, false);
            layerObject.transform.localPosition = localPosition;

            var renderer = layerObject.AddComponent<SpriteRenderer>();
            renderer.sprite = sprite;
            renderer.sortingOrder = sortOrder++;
            return renderer;
        }

        void UpdateCamera()
        {
            if (_backgroundCamera == null)
            {
                return;
            }

            _backgroundCamera.orthographicSize = cameraZoom;
            _backgroundCamera.transform.localPosition = new Vector3(cameraOffset.x, cameraOffset.y, -10f);
        }

        Vector3 FlashToUnity(Vector2 flashPosition)
        {
            return new Vector3(flashPosition.x / FlashPPU, -flashPosition.y / FlashPPU, 0f);
        }

        void MakeAdditive(SpriteRenderer spriteRenderer)
        {
            if (spriteRenderer == null)
            {
                return;
            }

            spriteRenderer.sharedMaterial = GetAdditiveMaterial();
        }

        Material GetAdditiveMaterial()
        {
            if (_additiveMaterial != null)
            {
                return _additiveMaterial;
            }

            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null)
            {
                shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default");
            }

            if (shader == null)
            {
                Debug.LogError("[MainMenuBackgroundRenderer] No sprite shader found for additive menu effects.");
                return null;
            }

            _additiveMaterial = new Material(shader);
            _additiveMaterial.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            _additiveMaterial.SetInt("_DstBlend", (int)BlendMode.One);
            _additiveMaterial.SetInt("_ZWrite", 0);
            _additiveMaterial.renderQueue = 3100;
            return _additiveMaterial;
        }

        Sprite CreateWhiteSquareSprite()
        {
            if (_whiteMaskSprite != null)
            {
                return _whiteMaskSprite;
            }

            _whiteMaskTexture = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            var pixels = new Color32[16];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = new Color32(255, 255, 255, 255);
            }

            _whiteMaskTexture.SetPixels32(pixels);
            _whiteMaskTexture.Apply();
            _whiteMaskSprite = Sprite.Create(_whiteMaskTexture, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 4f);
            return _whiteMaskSprite;
        }

        void ResetAnimation()
        {
            _frameAccumulator = 0f;
            _animFrame = 0;
            _eyeFrameIndex = 0;
            _blinkCountdown = RandomBlinkInterval();
            _lightningCountdown = RandomLightningInterval();
        }

        void AnimateFrame()
        {
            if (composition == null)
            {
                return;
            }

            _blinkCountdown--;
            if (_blinkCountdown <= 0 && _eyeFrameIndex == 0)
            {
                _eyeFrameIndex = 1;
            }

            if (_eyeFrameIndex > 0)
            {
                if (_animFrame % 3 == 0)
                {
                    _eyeFrameIndex++;
                    if (_eyeFrameIndex >= (composition.eyeFrames?.Length ?? 5))
                    {
                        _eyeFrameIndex = 0;
                        _blinkCountdown = RandomBlinkInterval();
                    }
                }
            }

            if (_eyeSR != null && composition.eyeFrames != null && _eyeFrameIndex < composition.eyeFrames.Length)
            {
                _eyeSR.sprite = composition.eyeFrames[_eyeFrameIndex];
            }

            float timeSeconds = _animFrame / Mathf.Max(1f, composition.frameRate);

            if (_displ1SR != null)
            {
                float baseX = composition.displacementManeLocalPos.x / FlashPPU;
                float baseY = -composition.displacementManeLocalPos.y / FlashPPU;
                float wobbleX = (Mathf.Sin(timeSeconds * 1.0f) * 0.2f + Mathf.Sin(timeSeconds * 1.7f) * 0.25f) * 10f / FlashPPU;
                float wobbleY = (Mathf.Sin(timeSeconds * 0.8f + 1.5f) * 0.1f + Mathf.Cos(timeSeconds * 1.3f) * 0.2f) * 10f / FlashPPU;
                _displ1SR.transform.localPosition = new Vector3(baseX + wobbleX, baseY + wobbleY, 0f);
            }

            if (_displ2SR != null)
            {
                float baseX = composition.displacementTailLocalPos.x / FlashPPU;
                float baseY = -composition.displacementTailLocalPos.y / FlashPPU;
                float wobbleY = (Mathf.Sin(timeSeconds * 1.0f) * 0.1f + Mathf.Sin(timeSeconds * 1.5f) * 0.15f) * 1f / FlashPPU;
                _displ2SR.transform.localPosition = new Vector3(baseX, baseY + wobbleY, 0f);
            }

            if (_pistolGroup != null)
            {
                float baseX = composition.pistolLocalPos.x / FlashPPU;
                float baseY = -composition.pistolLocalPos.y / FlashPPU;
                float bobX = Mathf.Sin(_animFrame / composition.pistolBobSpeed) * composition.pistolBobAmplitudeX / FlashPPU;
                float bobY = (Mathf.Cos(_animFrame / composition.pistolBobSpeed) - 1f) * composition.pistolBobAmplitudeY / FlashPPU;
                _pistolGroup.localPosition = new Vector3(baseX + bobX, baseY + bobY, 0f);
            }

            float rotation1 = _animFrame * composition.magicRotSpeed1;
            float rotation2 = 90f + _animFrame * composition.magicRotSpeed2;

            if (_hornKrugSR != null)
            {
                _hornKrugSR.transform.localRotation = Quaternion.Euler(0f, 0f, -rotation2);
            }

            if (_pistolKrugSR != null)
            {
                _pistolKrugSR.transform.localRotation = Quaternion.Euler(0f, 0f, -rotation1);
            }

            if (_pistolKrug2SR != null)
            {
                _pistolKrug2SR.transform.localRotation = Quaternion.Euler(0f, 0f, -rotation2);
            }

            if (composition.hornSparkFrames != null && composition.hornSparkFrames.Length > 0)
            {
                for (int i = 0; i < _hornSparkSRs.Count; i++)
                {
                    if (_hornSparkSRs[i] == null)
                    {
                        continue;
                    }

                    int frame = (_animFrame + i * 7) % composition.hornSparkFrames.Length;
                    _hornSparkSRs[i].sprite = composition.hornSparkFrames[frame];
                }
            }

            if (composition.pistolSparkFrames != null && composition.pistolSparkFrames.Length > 0)
            {
                for (int i = 0; i < _pistolSparkSRs.Count; i++)
                {
                    if (_pistolSparkSRs[i] == null)
                    {
                        continue;
                    }

                    int frame = (_animFrame + i * 5) % composition.pistolSparkFrames.Length;
                    _pistolSparkSRs[i].sprite = composition.pistolSparkFrames[frame];
                }
            }

            UpdateLightning();
        }

        void UpdateLightning()
        {
            _lightningCountdown--;

            if (_lightningCountdown == 0)
            {
                if (_grozaGroup != null)
                {
                    float flashX = Random.value * composition.lightningSpawnWidth;
                    float flashY = Random.value * composition.lightningSpawnHeight;
                    float scale = 1f - flashY / 800f;

                    _grozaGroup.localPosition = new Vector3(flashX / FlashPPU, -flashY / FlashPPU, 0f);
                    _grozaGroup.localScale = Vector3.one * Mathf.Max(0.3f, scale);
                    _grozaGroup.gameObject.SetActive(true);

                    if (_lightningCloudsSR != null)
                    {
                        float cloudX = (-200f - Random.value * 400f) / FlashPPU;
                        float cloudY = -(-200f - Random.value * 300f) / FlashPPU;
                        _lightningCloudsSR.transform.localPosition = new Vector3(cloudX, cloudY, 0f);
                    }

                    if (_lightningBoltSR != null && composition.lightningBoltFrames != null && composition.lightningBoltFrames.Length > 0)
                    {
                        int boltFrame = Random.Range(0, composition.lightningBoltFrames.Length);
                        _lightningBoltSR.sprite = composition.lightningBoltFrames[boltFrame];
                        _lightningBoltSR.transform.localRotation = Quaternion.Euler(0f, 0f, Random.value * 360f);
                    }

                    SetGroupAlpha(_lightningCloudsSR, 1f);
                    SetGroupAlpha(_lightningBoltSR, 1f);
                }
            }
            else if (_lightningCountdown < 0)
            {
                float alpha = Mathf.Min(1f, Random.value * 0.5f + _lightningCountdown / 12f + 0.7f);
                alpha = Mathf.Max(0f, alpha);

                SetGroupAlpha(_lightningCloudsSR, alpha);
                SetGroupAlpha(_lightningBoltSR, alpha);

                if (_lightningCountdown < -6 && Random.value < 0.1f)
                {
                    _lightningCountdown = -100;
                }

                if (_lightningCountdown < -composition.lightningBurstFrames)
                {
                    _lightningCountdown = RandomLightningInterval();
                    if (_grozaGroup != null)
                    {
                        _grozaGroup.gameObject.SetActive(false);
                    }
                }
            }
        }

        static void SetGroupAlpha(SpriteRenderer spriteRenderer, float alpha)
        {
            if (spriteRenderer == null)
            {
                return;
            }

            Color color = spriteRenderer.color;
            color.a = Mathf.Clamp01(alpha);
            spriteRenderer.color = color;
        }

        int RandomBlinkInterval()
        {
            return Mathf.RoundToInt(Random.Range(
                composition.blinkIntervalMin * composition.frameRate,
                composition.blinkIntervalMax * composition.frameRate));
        }

        int RandomLightningInterval()
        {
            return Mathf.RoundToInt(Random.Range(
                composition.lightningIntervalMin * composition.frameRate,
                composition.lightningIntervalMax * composition.frameRate));
        }

        void Cleanup()
        {
            _isBuilt = false;
            _hornSparkSRs.Clear();
            _pistolSparkSRs.Clear();

            if (_runtimeRoot != null)
            {
                Destroy(_runtimeRoot);
                _runtimeRoot = null;
            }

            if (_additiveMaterial != null)
            {
                Destroy(_additiveMaterial);
                _additiveMaterial = null;
            }

            if (_whiteMaskSprite != null)
            {
                Destroy(_whiteMaskSprite);
                _whiteMaskSprite = null;
            }

            if (_whiteMaskTexture != null)
            {
                Destroy(_whiteMaskTexture);
                _whiteMaskTexture = null;
            }

            _backgroundCamera = null;
            _pipkaRoot = null;
            _pistolGroup = null;
            _hornGroup = null;
            _grozaGroup = null;
            _skySR = null;
            _citySR = null;
            _logoSR = null;
            _hornPieceSR = null;
            _earPieceSR = null;
            _displ1SR = null;
            _displ2SR = null;
            _eyeSR = null;
            _hornMagicGlowSR = null;
            _hornKrugSR = null;
            _pistolBodySR = null;
            _pistolMagicGlowSR = null;
            _pistolKrugSR = null;
            _pistolMagic2GlowSR = null;
            _pistolKrug2SR = null;
            _lightningCloudsSR = null;
            _lightningBoltSR = null;
        }
    }
}
