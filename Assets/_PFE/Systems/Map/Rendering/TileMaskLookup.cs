using System;
using System.Collections.Generic;
using UnityEngine;

namespace PFE.Systems.Map.Rendering
{
    [CreateAssetMenu(fileName = "TileMaskLookup", menuName = "PFE/Map/Tile Mask Lookup")]
    public class TileMaskLookup : ScriptableObject
    {
        [Serializable]
        public class MaskEntry
        {
            public string name;
            public List<Sprite> frames = new List<Sprite>();
        }

        [SerializeField] private List<MaskEntry> _entries = new List<MaskEntry>();

        [NonSerialized] private Dictionary<string, MaskEntry> _lookup;
        [NonSerialized] private bool _initialized;

        public int Count => _entries.Count;

        public void Initialize()
        {
            if (_initialized)
                return;

            _lookup = new Dictionary<string, MaskEntry>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                if (entry == null || string.IsNullOrWhiteSpace(entry.name))
                    continue;

                _lookup[entry.name] = entry;
            }

            _initialized = true;
        }

        public IReadOnlyList<Sprite> GetFrames(string name)
        {
            if (!_initialized)
                Initialize();

            if (string.IsNullOrWhiteSpace(name) || !_lookup.TryGetValue(name, out var entry))
                return Array.Empty<Sprite>();

            return entry.frames;
        }

        public Sprite GetFrame(string name, int index = 0)
        {
            var frames = GetFrames(name);
            if (frames == null || frames.Count == 0)
                return null;

            if (index < 0 || index >= frames.Count)
                index = 0;

            return frames[index];
        }

        public void Clear()
        {
            _entries.Clear();
            _initialized = false;
        }

        public void SetEntry(string name, IList<Sprite> frames)
        {
            if (string.IsNullOrWhiteSpace(name))
                return;

            MaskEntry entry = null;
            for (int i = 0; i < _entries.Count; i++)
            {
                if (string.Equals(_entries[i].name, name, StringComparison.OrdinalIgnoreCase))
                {
                    entry = _entries[i];
                    break;
                }
            }

            if (entry == null)
            {
                entry = new MaskEntry { name = name };
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
