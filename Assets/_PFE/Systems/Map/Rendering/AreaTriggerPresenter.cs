using System.Collections.Generic;
using PFE.Core;
using PFE.Entities.Player;
using PFE.Entities.Units;
using PFE.Systems.Map.Scripting;
using UnityEngine;

namespace PFE.Systems.Map.Rendering
{
    public enum AreaTriggerType
    {
        Event,      // Purple - Tutorial tips, scripts, story/quest triggers
        Passage,    // Cyan - Room-to-room passage, ladder shaft, transition zone
        Teleport,   // Emerald Green - Level exit / teleport to new map
        Hazard      // Orange - Environmental hazard, radiation, trap
    }

    /// <summary>
    /// Presenter component attached to area trigger GameObjects.
    /// Handles 2D physics trigger detection and routes enter/exit events to AreaTriggerSystem,
    /// and renders debug overlays for 3 distinct area types matching Flash AS3 World.w.showArea / visArea.
    /// </summary>
    [RequireComponent(typeof(BoxCollider2D))]
    public sealed class AreaTriggerPresenter : MonoBehaviour
    {
        private static readonly List<AreaTriggerPresenter> _activePresenters = new List<AreaTriggerPresenter>();
        private static Sprite _whiteDebugSprite;
        private static bool? _debugOverride;

        [SerializeField] private BoxCollider2D _collider;
        private GameObject _debugVisualGo;
        private SpriteRenderer _debugSpriteRenderer;

        public ObjectInstance TriggerObject { get; private set; }
        public RoomInstance Room { get; private set; }
        public AreaTriggerSystem TriggerSystem { get; set; }
        public BoxCollider2D Collider => _collider;
        public AreaTriggerType ResolvedType { get; private set; } = AreaTriggerType.Event;

        public static Color GetFillColor(AreaTriggerType type)
        {
            return type switch
            {
                AreaTriggerType.Event => new Color(0.65f, 0.1f, 0.85f, 0.35f),    // Purple / Violet
                AreaTriggerType.Passage => new Color(0.0f, 0.75f, 1.0f, 0.35f),   // Cyan / Sky Blue
                AreaTriggerType.Teleport => new Color(0.1f, 0.85f, 0.3f, 0.35f),  // Emerald Green
                AreaTriggerType.Hazard => new Color(1.0f, 0.45f, 0.0f, 0.35f),    // Orange
                _ => new Color(0.65f, 0.1f, 0.85f, 0.35f)
            };
        }

        public static Color GetWireColor(AreaTriggerType type)
        {
            return type switch
            {
                AreaTriggerType.Event => new Color(0.85f, 0.2f, 1.0f, 0.9f),
                AreaTriggerType.Passage => new Color(0.3f, 0.9f, 1.0f, 0.9f),
                AreaTriggerType.Teleport => new Color(0.3f, 1.0f, 0.5f, 0.9f),
                AreaTriggerType.Hazard => new Color(1.0f, 0.65f, 0.1f, 0.9f),
                _ => new Color(0.85f, 0.2f, 1.0f, 0.9f)
            };
        }

        public static AreaTriggerType ResolveAreaType(ObjectInstance trigger)
        {
            if (trigger == null) return AreaTriggerType.Event;

            // 1. Explicit attribute override
            string typeAttr = trigger.GetAttribute("areaType", trigger.GetAttribute("type", string.Empty));
            if (!string.IsNullOrEmpty(typeAttr))
            {
                if (typeAttr.Equals("passage", System.StringComparison.OrdinalIgnoreCase) ||
                    typeAttr.Equals("pass", System.StringComparison.OrdinalIgnoreCase))
                    return AreaTriggerType.Passage;
                if (typeAttr.Equals("teleport", System.StringComparison.OrdinalIgnoreCase) ||
                    typeAttr.Equals("tele", System.StringComparison.OrdinalIgnoreCase) ||
                    typeAttr.Equals("exit", System.StringComparison.OrdinalIgnoreCase))
                    return AreaTriggerType.Teleport;
                if (typeAttr.Equals("hazard", System.StringComparison.OrdinalIgnoreCase) ||
                    typeAttr.Equals("rad", System.StringComparison.OrdinalIgnoreCase) ||
                    typeAttr.Equals("trap", System.StringComparison.OrdinalIgnoreCase))
                    return AreaTriggerType.Hazard;
                if (typeAttr.Equals("event", System.StringComparison.OrdinalIgnoreCase))
                    return AreaTriggerType.Event;
            }

            // 2. Check if scripts contain gotoland actions (level transition / teleport)
            if (trigger.scripts != null)
            {
                for (int s = 0; s < trigger.scripts.Count; s++)
                {
                    var script = trigger.scripts[s];
                    if (script?.actions == null) continue;
                    for (int a = 0; a < script.actions.Count; a++)
                    {
                        if (string.Equals(script.actions[a].act, "gotoland", System.StringComparison.OrdinalIgnoreCase))
                        {
                            return AreaTriggerType.Teleport;
                        }
                    }
                }
            }

            // 3. Identify by uid, mess, or objectId
            string uid = trigger.uid ?? string.Empty;
            string mess = trigger.GetAttribute("mess", string.Empty);
            string id = trigger.objectId ?? string.Empty;

            if (mess.StartsWith("trTele", System.StringComparison.OrdinalIgnoreCase) ||
                uid.StartsWith("trTele", System.StringComparison.OrdinalIgnoreCase) ||
                uid.IndexOf("exit", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                uid.IndexOf("portal", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                mess.IndexOf("portal", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return AreaTriggerType.Teleport;
            }

            if (mess.IndexOf("pass", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                uid.IndexOf("pass", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                uid.IndexOf("shaft", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                uid.IndexOf("ladder", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                uid.IndexOf("door", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                id.IndexOf("pass", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return AreaTriggerType.Passage;
            }

            if (uid.IndexOf("rad", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                uid.IndexOf("hazard", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                uid.IndexOf("trap", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                id.IndexOf("rad", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                mess.IndexOf("rad", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return AreaTriggerType.Hazard;
            }

            return AreaTriggerType.Event;
        }

        public static bool ShowDebugVisuals
        {
            get
            {
                if (_debugOverride.HasValue) return _debugOverride.Value;
                var settings = Resources.Load<PfeDebugSettings>("PfeDebugSettings");
                return settings != null && settings.ShowAreaTriggerDebug;
            }
            set
            {
                _debugOverride = value;
                var settings = Resources.Load<PfeDebugSettings>("PfeDebugSettings");
                if (settings != null)
                {
                    settings.ShowAreaTriggerDebug = value;
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

        private void Awake()
        {
            EnsureCollider();
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
            // Hotkey F8 to toggle area trigger debug visuals at runtime
            bool f8Pressed = Input.GetKeyDown(KeyCode.F8) ||
                (UnityEngine.InputSystem.Keyboard.current != null && UnityEngine.InputSystem.Keyboard.current.f8Key.wasPressedThisFrame);

            if (f8Pressed)
            {
                ShowDebugVisuals = !ShowDebugVisuals;
                Debug.Log($"[AreaTriggerPresenter] Area Trigger debug overlay: {(ShowDebugVisuals ? "ENABLED" : "DISABLED")}");
            }
        }

        public void Initialize(RoomInstance room, ObjectInstance trigger, AreaTriggerSystem triggerSystem = null)
        {
            Room = room;
            TriggerObject = trigger;
            TriggerSystem = triggerSystem;
            ResolvedType = ResolveAreaType(trigger);

            EnsureCollider();
            UpdateActiveState();
            UpdateDebugVisual();
        }

        public void UpdateActiveState()
        {
            bool active = TriggerObject != null && TriggerObject.isActive;
            if (_collider != null)
            {
                _collider.enabled = active;
            }
            UpdateDebugVisual();
        }

        public void UpdateDebugVisual()
        {
            bool shouldShow = ShowDebugVisuals && (TriggerObject == null || TriggerObject.isActive);

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
                if (_debugSpriteRenderer != null)
                {
                    _debugSpriteRenderer.color = GetFillColor(ResolvedType);
                }
            }
        }

        private void EnsureDebugVisual()
        {
            if (_debugVisualGo != null) return;

            _debugVisualGo = new GameObject("__AreaDebugVisual");
            _debugVisualGo.transform.SetParent(transform, false);
            // Centered on the collider offset (0.5, 0.5) so that centered sprite aligns perfectly
            _debugVisualGo.transform.localPosition = new Vector3(0.5f, 0.5f, 0f);
            _debugVisualGo.transform.localScale = Vector3.one;

            _debugSpriteRenderer = _debugVisualGo.AddComponent<SpriteRenderer>();
            _debugSpriteRenderer.sprite = GetWhiteDebugSprite();
            _debugSpriteRenderer.color = GetFillColor(ResolvedType);
            _debugSpriteRenderer.sortingLayerName = MapSortingLayers.Foreground;
            _debugSpriteRenderer.sortingOrder = 999;
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

        private void EnsureCollider()
        {
            if (_collider == null)
            {
                _collider = GetComponent<BoxCollider2D>();
                if (_collider == null)
                {
                    _collider = gameObject.AddComponent<BoxCollider2D>();
                }
            }

            _collider.isTrigger = true;
            // The presenter transform scale already scales by (w * 40 / 100, h * 40 / 100).
            // A unit box (1, 1) with (0.5, 0.5) offset matches the bottom-left pivot anchor.
            _collider.size = Vector2.one;
            _collider.offset = new Vector2(0.5f, 0.5f);
        }

        private void OnDrawGizmos()
        {
            if (!ShowDebugVisuals) return;

            Vector3 center = transform.TransformPoint(new Vector3(0.5f, 0.5f, 0f));
            Vector3 size = new Vector3(Mathf.Abs(transform.lossyScale.x), Mathf.Abs(transform.lossyScale.y), 0.05f);

            Gizmos.color = GetFillColor(ResolvedType);
            Gizmos.DrawCube(center, size);

            Gizmos.color = GetWireColor(ResolvedType);
            Gizmos.DrawWireCube(center, size);

#if UNITY_EDITOR
            string idLabel = !string.IsNullOrEmpty(TriggerObject?.uid)
                ? TriggerObject.uid
                : (!string.IsNullOrEmpty(TriggerObject?.GetAttribute("mess")) ? TriggerObject.GetAttribute("mess") : "Area");
            string label = $"[{ResolvedType}] {idLabel}";
            UnityEditor.Handles.Label(center, label);
#endif
        }

        private void OnTriggerEnter2D(Collider2D other)
        {
            if (!IsPlayerCollider(other))
            {
                return;
            }

            TriggerSystem?.OnPlayerEnter(Room, TriggerObject);
        }

        private void OnTriggerExit2D(Collider2D other)
        {
            if (!IsPlayerCollider(other))
            {
                return;
            }

            TriggerSystem?.OnPlayerExit(Room, TriggerObject);
        }

        private static bool IsPlayerCollider(Collider2D other)
        {
            if (other == null) return false;

            if (other.CompareTag("Player"))
            {
                return true;
            }

            return other.GetComponentInParent<PlayerController>() != null ||
                   other.GetComponentInParent<UnitController>() != null;
        }
    }
}
