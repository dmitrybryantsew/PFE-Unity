using System;
using System.Collections.Generic;
using UnityEngine;

namespace PFE.Systems.Map.Rendering
{
    [CreateAssetMenu(fileName = "RoomBackgroundLookup", menuName = "PFE/Map/Room Background Lookup")]
    public class RoomBackgroundLookup : ScriptableObject
    {
        [Serializable]
        public class BackgroundEntry
        {
            public string id;
            public List<Sprite> frames = new List<Sprite>();
            public Vector2 pixelOffset = Vector2.zero;
            public bool flipX = false;
            public bool flipY = false;
        }

        [SerializeField] private List<BackgroundEntry> _entries = new List<BackgroundEntry>();

        [NonSerialized] private Dictionary<string, BackgroundEntry> _lookup;
        [NonSerialized] private bool _initialized;

        public int Count => _entries.Count;

        public void Initialize()
        {
            if (_initialized)
                return;

            _lookup = new Dictionary<string, BackgroundEntry>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                if (entry == null || string.IsNullOrWhiteSpace(entry.id))
                    continue;

                _lookup[entry.id] = entry;
            }

            _initialized = true;
        }

        public Sprite GetPrimarySprite(string id)
        {
            var frames = GetFrames(id);
            return frames != null && frames.Count > 0 ? frames[0] : null;
        }

        public IReadOnlyList<Sprite> GetFrames(string id)
        {
            if (!_initialized)
                Initialize();

            if (string.IsNullOrWhiteSpace(id) || !_lookup.TryGetValue(id, out var entry))
                return Array.Empty<Sprite>();

            return entry.frames;
        }

        public Vector2 GetPixelOffset(string id)
        {
            if (!_initialized)
                Initialize();

            if (string.IsNullOrWhiteSpace(id) || !_lookup.TryGetValue(id, out var entry))
                return Vector2.zero;

            return entry.pixelOffset;
        }

        public bool Contains(string id)
        {
            if (!_initialized)
                Initialize();

            return !string.IsNullOrWhiteSpace(id) && _lookup.ContainsKey(id);
        }

        public bool GetFlipX(string id)
        {
            if (!_initialized)
                Initialize();

            if (string.IsNullOrWhiteSpace(id) || !_lookup.TryGetValue(id, out var entry))
                return false;

            return entry.flipX;
        }

        public bool GetFlipY(string id)
        {
            if (!_initialized)
                Initialize();

            if (string.IsNullOrWhiteSpace(id) || !_lookup.TryGetValue(id, out var entry))
                return false;

            return entry.flipY;
        }

        public void Clear()
        {
            _entries.Clear();
            _initialized = false;
        }

        public void SetEntry(string id, IList<Sprite> frames)
        {
            if (string.IsNullOrWhiteSpace(id))
                return;

            BackgroundEntry entry = null;
            for (int i = 0; i < _entries.Count; i++)
            {
                if (string.Equals(_entries[i].id, id, StringComparison.OrdinalIgnoreCase))
                {
                    entry = _entries[i];
                    break;
                }
            }

            if (entry == null)
            {
                entry = new BackgroundEntry { id = id };
                _entries.Add(entry);
            }

            entry.frames = frames != null ? new List<Sprite>(frames) : new List<Sprite>();
            _initialized = false;
        }

        private void OnEnable()
        {
            _initialized = false;
        }
    }
}
