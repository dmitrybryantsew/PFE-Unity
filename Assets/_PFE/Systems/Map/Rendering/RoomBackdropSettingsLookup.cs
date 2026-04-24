using System;
using System.Collections.Generic;
using UnityEngine;

namespace PFE.Systems.Map.Rendering
{
    [CreateAssetMenu(fileName = "RoomBackdropSettingsLookup", menuName = "PFE/Map/Room Backdrop Settings Lookup")]
    public class RoomBackdropSettingsLookup : ScriptableObject
    {
        public const string ResourcesPath = "Data/RoomBackdropSettingsLookup";
        public const string AssetPath = "Assets/Resources/Data/RoomBackdropSettingsLookup.asset";

        [Serializable]
        public struct TintSettings
        {
            public Color tint;
            public float brightness;

            public static TintSettings Default => new TintSettings
            {
                tint = Color.white,
                brightness = 1f
            };
        }

        [Serializable]
        public struct BackdropSettings
        {
            public Vector2 textureScale;
            public Vector2 textureOffset;
            public bool flipX;
            public bool flipY;
            public bool overrideGlobalTint;
            public TintSettings tint;

            public static BackdropSettings Default => new BackdropSettings
            {
                textureScale = Vector2.one,
                textureOffset = Vector2.zero,
                flipX = false,
                flipY = true,
                overrideGlobalTint = false,
                tint = TintSettings.Default
            };
        }

        [Serializable]
        public struct DecorationSettings
        {
            public bool overrideGlobalTint;
            public TintSettings tint;

            public static DecorationSettings Default => new DecorationSettings
            {
                overrideGlobalTint = false,
                tint = TintSettings.Default
            };
        }

        [Serializable]
        public class DecorationEntry
        {
            public string decorationId;
            public DecorationSettings settings = DecorationSettings.Default;
        }

        [Serializable]
        public class RoomEntry
        {
            public string roomKey;
            public bool hasBackdropSettings;
            public BackdropSettings backdropSettings = BackdropSettings.Default;
            public bool hasBackdropTint;
            public TintSettings backdropTint = TintSettings.Default;
            public bool hasDecorationTint;
            public TintSettings decorationTint = TintSettings.Default;
            public List<DecorationEntry> decorationEntries = new List<DecorationEntry>();
        }

        [SerializeField] private List<RoomEntry> _roomEntries = new List<RoomEntry>();

        [NonSerialized] private Dictionary<string, RoomEntry> _roomLookup;
        [NonSerialized] private bool _initialized;

        public void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            _roomLookup = new Dictionary<string, RoomEntry>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _roomEntries.Count; i++)
            {
                RoomEntry entry = _roomEntries[i];
                if (entry == null || string.IsNullOrWhiteSpace(entry.roomKey))
                {
                    continue;
                }

                _roomLookup[entry.roomKey] = entry;
            }

            _initialized = true;
        }

        public bool TryGetRoomBackdropTint(string roomKey, out TintSettings settings)
        {
            if (TryGetRoomEntry(roomKey, out RoomEntry entry) && entry.hasBackdropTint)
            {
                settings = SanitizeTint(entry.backdropTint);
                return true;
            }

            settings = TintSettings.Default;
            return false;
        }

        public bool TryGetRoomDecorationTint(string roomKey, out TintSettings settings)
        {
            if (TryGetRoomEntry(roomKey, out RoomEntry entry) && entry.hasDecorationTint)
            {
                settings = SanitizeTint(entry.decorationTint);
                return true;
            }

            settings = TintSettings.Default;
            return false;
        }

        public void SetRoomBackdropTint(string roomKey, TintSettings settings)
        {
            RoomEntry entry = GetOrCreateRoomEntry(roomKey);
            if (entry == null)
            {
                return;
            }

            entry.hasBackdropTint = true;
            entry.backdropTint = SanitizeTint(settings);
        }

        public void SetRoomDecorationTint(string roomKey, TintSettings settings)
        {
            RoomEntry entry = GetOrCreateRoomEntry(roomKey);
            if (entry == null)
            {
                return;
            }

            entry.hasDecorationTint = true;
            entry.decorationTint = SanitizeTint(settings);
        }

        public bool TryGetBackdrop(string roomKey, out BackdropSettings settings)
        {
            if (TryGetRoomEntry(roomKey, out RoomEntry entry) && entry.hasBackdropSettings)
            {
                settings = SanitizeBackdrop(entry.backdropSettings);
                return true;
            }

            settings = BackdropSettings.Default;
            return false;
        }

        public bool TryGetDecoration(string roomKey, string decorationId, out DecorationSettings settings)
        {
            if (TryGetRoomEntry(roomKey, out RoomEntry roomEntry) &&
                roomEntry.decorationEntries != null)
            {
                for (int i = 0; i < roomEntry.decorationEntries.Count; i++)
                {
                    DecorationEntry entry = roomEntry.decorationEntries[i];
                    if (entry != null && string.Equals(entry.decorationId, decorationId, StringComparison.OrdinalIgnoreCase))
                    {
                        settings = SanitizeDecoration(entry.settings);
                        return true;
                    }
                }
            }

            settings = DecorationSettings.Default;
            return false;
        }

        public void SetBackdrop(string roomKey, BackdropSettings settings)
        {
            RoomEntry entry = GetOrCreateRoomEntry(roomKey);
            if (entry == null)
            {
                return;
            }

            settings = SanitizeBackdrop(settings);
            entry.hasBackdropSettings = true;
            entry.backdropSettings = settings;
        }

        public void SetDecoration(string roomKey, string decorationId, DecorationSettings settings)
        {
            RoomEntry roomEntry = GetOrCreateRoomEntry(roomKey);
            if (roomEntry == null || string.IsNullOrWhiteSpace(decorationId))
            {
                return;
            }

            settings = SanitizeDecoration(settings);
            if (roomEntry.decorationEntries == null)
            {
                roomEntry.decorationEntries = new List<DecorationEntry>();
            }

            for (int i = 0; i < roomEntry.decorationEntries.Count; i++)
            {
                DecorationEntry existing = roomEntry.decorationEntries[i];
                if (existing != null && string.Equals(existing.decorationId, decorationId, StringComparison.OrdinalIgnoreCase))
                {
                    existing.settings = settings;
                    return;
                }
            }

            DecorationEntry entry = new DecorationEntry
            {
                decorationId = decorationId,
                settings = settings
            };

            roomEntry.decorationEntries.Add(entry);
        }

        private static BackdropSettings SanitizeBackdrop(BackdropSettings settings)
        {
            settings.textureScale.x = Mathf.Max(0.01f, settings.textureScale.x);
            settings.textureScale.y = Mathf.Max(0.01f, settings.textureScale.y);
            settings.tint = SanitizeTint(settings.tint);
            return settings;
        }

        private static DecorationSettings SanitizeDecoration(DecorationSettings settings)
        {
            settings.tint = SanitizeTint(settings.tint);
            return settings;
        }

        private static TintSettings SanitizeTint(TintSettings settings)
        {
            bool colorUnset = settings.tint == default;
            if (colorUnset)
            {
                settings.tint = Color.white;
            }

            if (colorUnset && Mathf.Approximately(settings.brightness, 0f))
            {
                settings.brightness = 1f;
            }
            else
            {
                settings.brightness = Mathf.Max(0f, settings.brightness);
            }

            return settings;
        }

        private bool TryGetRoomEntry(string roomKey, out RoomEntry entry)
        {
            if (!_initialized)
            {
                Initialize();
            }

            if (!string.IsNullOrWhiteSpace(roomKey) && _roomLookup.TryGetValue(roomKey, out entry))
            {
                return true;
            }

            entry = null;
            return false;
        }

        private RoomEntry GetOrCreateRoomEntry(string roomKey)
        {
            if (string.IsNullOrWhiteSpace(roomKey))
            {
                return null;
            }

            if (!_initialized)
            {
                Initialize();
            }

            if (_roomLookup.TryGetValue(roomKey, out RoomEntry existing))
            {
                return existing;
            }

            RoomEntry entry = new RoomEntry
            {
                roomKey = roomKey
            };

            _roomEntries.Add(entry);
            _roomLookup[roomKey] = entry;
            return entry;
        }

        private void OnEnable()
        {
            _initialized = false;
        }
    }
}
