using PFE.Data.Definitions;
using UnityEngine;
using UnityEngine.UI;

namespace PFE.Character
{
    /// <summary>
    /// Small runtime helper that renders a character into a RawImage using the shared assembler.
    /// Intended for menu/dialog previews and other paper-doll style UI.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CharacterPreviewPresenter : MonoBehaviour
    {
        [Header("Preview")]
        [SerializeField] int _previewLayer = 2;
        [SerializeField] int _textureSize = 256;
        [SerializeField] float _cameraSize = 0.85f;
        [SerializeField] Vector3 _cameraPosition = new(0f, 0.50f, -10f);
        [SerializeField] Vector3 _previewScale = new(1.7f, 1.7f, 1.7f);
        [SerializeField] string _stateName = "stay";
        [SerializeField] int _frameIndex;

        RawImage _targetImage;
        RenderTexture _renderTexture;
        GameObject _previewRoot;
        GameObject _previewCameraObject;
        CharacterSpriteAssembler _assembler;
        Camera _previewCamera;
        CharacterAppearance _appearance;
        CharacterVisualContext _visualContext;
        CharacterAnimationDefinition _definition;
        CharacterStyleData _styleData;

        public void Initialize(
            RawImage targetImage,
            CharacterAnimationDefinition definition,
            CharacterStyleData styleData,
            CharacterAppearance appearance,
            CharacterVisualContext visualContext = default,
            string stateName = "stay",
            int frameIndex = 0,
            int previewLayer = -1)
        {
            _targetImage = targetImage != null ? targetImage : GetComponent<RawImage>() ?? gameObject.AddComponent<RawImage>();
            _definition = definition;
            _styleData = styleData;
            _appearance = appearance?.Clone() ?? CharacterAppearance.CreateDefault();
            _visualContext = string.IsNullOrEmpty(visualContext.armorId) && !visualContext.hideMane && !visualContext.transparent
                ? CharacterVisualContext.Default
                : visualContext;
            _stateName = string.IsNullOrWhiteSpace(stateName) ? "stay" : stateName;
            _frameIndex = Mathf.Max(0, frameIndex);
            if (previewLayer >= 0)
            {
                _previewLayer = previewLayer;
            }

            RebuildPreview();
        }

        public void SetAppearance(CharacterAppearance appearance)
        {
            _appearance = appearance?.Clone() ?? CharacterAppearance.CreateDefault();
            if (_assembler == null)
            {
                RebuildPreview();
                return;
            }

            if (_assembler.Appearance == null)
            {
                _assembler.Appearance = _appearance.Clone();
            }
            else
            {
                _assembler.Appearance.CopyFrom(_appearance);
                _assembler.ApplyAppearance();
            }

            _assembler.SetState(_stateName, _frameIndex);
        }

        public void SetVisualContext(CharacterVisualContext visualContext)
        {
            _visualContext = visualContext;
            if (_assembler == null)
            {
                RebuildPreview();
                return;
            }

            _assembler.VisualContext = _visualContext;
            _assembler.SetState(_stateName, _frameIndex);
        }

        public void SetState(string stateName, int frameIndex = 0)
        {
            _stateName = string.IsNullOrWhiteSpace(stateName) ? "stay" : stateName;
            _frameIndex = Mathf.Max(0, frameIndex);
            _assembler?.SetState(_stateName, _frameIndex);
        }

        public void SetActive(bool active)
        {
            if (_previewCamera != null)
            {
                _previewCamera.enabled = active;
            }
        }

        void OnDestroy()
        {
            CleanupPreview();
        }

        void RebuildPreview()
        {
            CleanupPreview();

            if (_targetImage == null || _definition == null || _styleData == null)
            {
                return;
            }

            int texW = _textureSize;
            int texH = _textureSize;
            if (_targetImage != null)
            {
                Rect r = _targetImage.rectTransform.rect;
                if (r.width >= 16f && r.height >= 16f)
                {
                    texW = Mathf.RoundToInt(r.width);
                    texH = Mathf.RoundToInt(r.height);
                }
            }

            _renderTexture = new RenderTexture(texW, texH, 16, RenderTextureFormat.ARGB32);
            _renderTexture.Create();

            _targetImage.texture = _renderTexture;
            _targetImage.color = Color.white;
            _targetImage.raycastTarget = false;

            _previewRoot = new GameObject($"{name}_PreviewRoot");
            _previewRoot.layer = _previewLayer;
            _previewRoot.transform.localScale = _previewScale;

            _assembler = _previewRoot.AddComponent<CharacterSpriteAssembler>();
            _assembler.Setup(_definition, _styleData, _appearance.Clone(), _visualContext);
            _assembler.SetState(_stateName, _frameIndex);
            SetLayerRecursively(_previewRoot.transform, _previewLayer);

            _previewCameraObject = new GameObject($"{name}_PreviewCamera");
            _previewCameraObject.transform.position = _cameraPosition;
            _previewCamera = _previewCameraObject.AddComponent<Camera>();
            _previewCamera.orthographic = true;
            _previewCamera.orthographicSize = _cameraSize;
            _previewCamera.clearFlags = CameraClearFlags.SolidColor;
            _previewCamera.backgroundColor = new Color(0f, 0f, 0f, 0f);
            _previewCamera.cullingMask = 1 << _previewLayer;
            _previewCamera.targetTexture = _renderTexture;
            _previewCamera.useOcclusionCulling = false;
        }

        void CleanupPreview()
        {
            if (_previewCameraObject != null)
            {
                Destroy(_previewCameraObject);
                _previewCameraObject = null;
                _previewCamera = null;
            }

            if (_previewRoot != null)
            {
                Destroy(_previewRoot);
                _previewRoot = null;
                _assembler = null;
            }

            if (_renderTexture != null)
            {
                if (_targetImage != null && _targetImage.texture == _renderTexture)
                {
                    _targetImage.texture = null;
                }

                _renderTexture.Release();
                Destroy(_renderTexture);
                _renderTexture = null;
            }
        }

        void SetLayerRecursively(Transform root, int layer)
        {
            root.gameObject.layer = layer;
            for (int i = 0; i < root.childCount; i++)
            {
                SetLayerRecursively(root.GetChild(i), layer);
            }
        }
    }
}
