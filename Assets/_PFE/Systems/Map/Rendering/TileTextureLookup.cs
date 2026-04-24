using System;
using System.Collections.Generic;
using UnityEngine;

namespace PFE.Systems.Map.Rendering
{
    /// <summary>
    /// Maps texture names (from MaterialRenderDatabase) to actual Texture2D assets.
    /// Created by TileTextureImporter, used at runtime by TileCompositor.
    /// 
    /// Example: "tConcrete" -> the imported Texture2D from texture.swf
    /// </summary>
    [CreateAssetMenu(fileName = "TileTextureLookup", menuName = "PFE/Map/Tile Texture Lookup")]
    public class TileTextureLookup : ScriptableObject
    {
        [Serializable]
        public class TextureEntry
        {
            public string name;
            public Texture2D texture;
        }

        [SerializeField] private List<TextureEntry> _entries = new List<TextureEntry>();

        [NonSerialized] private Dictionary<string, Texture2D> _lookup;
        [NonSerialized] private bool _initialized;

        public int Count => _entries.Count;

        public void Initialize()
        {
            if (_initialized) return;
            _lookup = new Dictionary<string, Texture2D>(_entries.Count);
            foreach (var entry in _entries)
            {
                if (!string.IsNullOrEmpty(entry.name) && entry.texture != null)
                    _lookup[entry.name] = entry.texture;
            }
            _initialized = true;
        }

        /// <summary>
        /// Get texture by name (e.g., "tConcrete", "tMetal").
        /// </summary>
        public Texture2D GetTexture(string name)
        {
            if (!_initialized) Initialize();
            _lookup.TryGetValue(name, out var tex);
            return tex;
        }

        public void Clear()
        {
            _entries.Clear();
            _initialized = false;
        }

        public void AddEntry(string name, Texture2D texture)
        {
            _entries.Add(new TextureEntry { name = name, texture = texture });
            _initialized = false;
        }

        private void OnEnable() { _initialized = false; }
    }
}