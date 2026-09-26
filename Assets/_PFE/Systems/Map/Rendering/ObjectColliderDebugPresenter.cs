using System;
using System.Collections.Generic;
using PFE.Core;
using PFE.Data.Definitions;
using UnityEngine;

namespace PFE.Systems.Map.Rendering
{
    /// <summary>
    /// Debug presenter attached to map object GameObjects (containers, barricades, fixtures, terminals).
    /// Provides runtime visual overlays (Game View) and Gizmos (Scene View) for inspecting object colliders and bounds.
    /// Toggleable via PfeDebugSettings.ShowObjectColliderDebug or Hotkey F10.
    /// </summary>
    public sealed class ObjectColliderDebugPresenter : MonoBehaviour
    {
        private static readonly Color TealFillColor = new Color(0.15f, 0.85f, 0.65f, 0.35f);
        private static readonly Color TealWireColor = new Color(0.25f, 1.0f, 0.75f, 0.9f);
        private static readonly List<ObjectColliderDebugPresenter> _activePresenters = new List<ObjectColliderDebugPresenter>();
        private static Sprite _whiteDebugSprite;
        private static bool? _debugOverride;

        private RoomInstance _room;
        private ObjectInstance _objectInstance;
        private MapObjectVisualDefinition _visual;
        private GameObject _debugVisualGo;
        private SpriteRenderer _debugSpriteRenderer;

        public RoomInstance Room => _room;
        public ObjectInstance ObjectInstance => _objectInstance;

        public static bool ShowDebugVisuals
        {
            get
            {
                if (_debugOverride.HasValue) return _debugOverride.Value;
                var settings = Resources.Load<PfeDebugSettings>("PfeDebugSettings");
                return settings != null && settings.ShowObjectColliderDebug;
            }
            set
            {
                _debugOverride = value;
                var settings = Resources.Load<PfeDebugSettings>("PfeDebugSettings");
                if (settings != null)
                {
                    settings.ShowObjectColliderDebug = value;
                }
                UpdateAllDebugVisuals();
            }
        }

        public static void SetDebugOverride(bool? overrideValue)
        {
            _debugOverride = overrideValue;
            UpdateAllDebugVisuals();
        }

        public static void UpdateAllDebugVisuals()
        {
            for (int i = _activePresenters.Count - 1; i >= 0; i--)
            {
                if (_activePresenters[i] != null)
                {
                    _activePresenters[i].UpdateDebugVisual();
                }
                else
                {
                    _activePresenters.RemoveAt(i);
                }
            }
        }

        private void OnEnable()
        {
            if (!_activePresenters.Contains(this))
            {
                _activePresenters.Add(this);
            }
            UpdateDebugVisual();
        }

        private void OnDisable()
        {
            _activePresenters.Remove(this);
            if (_debugVisualGo != null)
            {
                _debugVisualGo.SetActive(false);
            }
        }

        private void Update()
        {
            // Hotkey F10 to toggle object collider debug visuals at runtime
            bool f10Pressed = Input.GetKeyDown(KeyCode.F10) ||
                (UnityEngine.InputSystem.Keyboard.current != null && UnityEngine.InputSystem.Keyboard.current.f10Key.wasPressedThisFrame);

            if (f10Pressed)
            {
                ShowDebugVisuals = !ShowDebugVisuals;
                Debug.Log($"[ObjectColliderDebug] Object Collider overlay: {(ShowDebugVisuals ? "ENABLED (Teal)" : "DISABLED")}");
            }
        }

        public void Initialize(RoomInstance room, ObjectInstance obj, MapObjectVisualDefinition visual)
        {
            _room = room;
            _objectInstance = obj;
            _visual = visual;
            UpdateDebugVisual();
        }

        public void UpdateDebugVisual()
        {
            bool shouldShow = ShowDebugVisuals && (_objectInstance == null || _objectInstance.isActive);

            if (!shouldShow)
            {
                if (_debugVisualGo != null)
                {
                    _debugVisualGo.SetActive(false);
                }
                return;
            }

            EnsureDebugVisual();
            if (_debugVisualGo != null)
            {
                _debugVisualGo.SetActive(true);
            }
        }

        private void EnsureDebugVisual()
        {
            if (_debugVisualGo != null) return;

            _debugVisualGo = new GameObject("__ObjectDebugVisual");
            _debugVisualGo.transform.SetParent(transform, false);

            Vector2 pixelSize = _visual != null && _visual.pixelSize != Vector2Int.zero
                ? (Vector2)_visual.pixelSize
                : new Vector2(40f, 40f);
            Vector2 pivot = _visual != null ? _visual.pivot : new Vector2(0.5f, 0f);

            float wUnits = pixelSize.x * 0.01f;
            float hUnits = pixelSize.y * 0.01f;

            // Offset relative to presenter position based on pivot
            float localX = (0.5f - pivot.x) * wUnits;
            float localY = (0.5f - pivot.y) * hUnits;
            _debugVisualGo.transform.localPosition = new Vector3(localX, localY, 0f);
            _debugVisualGo.transform.localScale = new Vector3(wUnits, hUnits, 1f);

            _debugSpriteRenderer = _debugVisualGo.AddComponent<SpriteRenderer>();
            _debugSpriteRenderer.sprite = GetWhiteDebugSprite();
            _debugSpriteRenderer.color = TealFillColor;
            _debugSpriteRenderer.sortingLayerName = MapSortingLayers.Foreground;
            _debugSpriteRenderer.sortingOrder = 998;
        }

        private static Sprite GetWhiteDebugSprite()
        {
            if (_whiteDebugSprite == null)
            {
                var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                tex.SetPixel(0, 0, Color.white);
                tex.Apply();
                _whiteDebugSprite = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
            }
            return _whiteDebugSprite;
        }

        private void OnDrawGizmos()
        {
            if (!ShowDebugVisuals) return;

            Vector2 pixelSize = _visual != null && _visual.pixelSize != Vector2Int.zero
                ? (Vector2)_visual.pixelSize
                : new Vector2(40f, 40f);
            Vector2 pivot = _visual != null ? _visual.pivot : new Vector2(0.5f, 0f);

            float wUnits = pixelSize.x * 0.01f;
            float hUnits = pixelSize.y * 0.01f;

            Vector3 center = transform.position + new Vector3((0.5f - pivot.x) * wUnits, (0.5f - pivot.y) * hUnits, 0f);
            Vector3 size = new Vector3(wUnits, hUnits, 0.05f);

            Gizmos.color = TealFillColor;
            Gizmos.DrawCube(center, size);

            Gizmos.color = TealWireColor;
            Gizmos.DrawWireCube(center, size);

#if UNITY_EDITOR
            string label = GetObjectLabel();
            UnityEditor.Handles.Label(center, label);
#endif
        }

        private string GetObjectLabel()
        {
            string id = _objectInstance?.objectId ?? _visual?.objectId ?? "obj";
            string type = _objectInstance?.objectType ?? "prop";
            return $"[{type}] {id}";
        }
    }
}
