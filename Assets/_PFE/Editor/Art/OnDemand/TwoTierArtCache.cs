#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace PFE.Editor.Art.OnDemand
{
    /// <summary>
    /// Two-tier caching layer:
    /// - Tier 1: In-memory LRU cache for 0ms repeated lookups.
    /// - Tier 2: Optional disk cache for fast persistent loads between editor domain reloads.
    /// </summary>
    public sealed class TwoTierArtCache
    {
        readonly int _capacity;
        readonly string _diskCacheDir;
        readonly bool _useDiskCache;

        readonly Dictionary<int, Sprite> _sprites = new Dictionary<int, Sprite>();
        readonly Dictionary<int, Texture2D> _textures = new Dictionary<int, Texture2D>();
        readonly Dictionary<int, Mesh> _meshes = new Dictionary<int, Mesh>();

        readonly LinkedList<int> _lruKeys = new LinkedList<int>();
        readonly Dictionary<int, LinkedListNode<int>> _lruNodes = new Dictionary<int, LinkedListNode<int>>();

        long _hits;
        long _misses;

        public TwoTierArtCache(int capacity = 500, string diskCacheDir = null, bool useDiskCache = true)
        {
            _capacity = Mathf.Max(10, capacity);
            _useDiskCache = useDiskCache;
            _diskCacheDir = !string.IsNullOrEmpty(diskCacheDir)
                ? diskCacheDir
                : Path.Combine(Application.dataPath, "_PFE", "Art", "GeneratedCache");
        }

        public long Hits => _hits;
        public long Misses => _misses;
        public int Count => _sprites.Count;
        public string DiskCacheDir => _diskCacheDir;

        // ── Sprites ──────────────────────────────────────────────────────────

        public bool TryGetSprite(int id, out Sprite sprite)
        {
            if (_sprites.TryGetValue(id, out sprite) && sprite != null)
            {
                TouchLru(id);
                _hits++;
                return true;
            }

            // Check Tier 2 disk cache
            if (_useDiskCache && TryLoadDiskSprite(id, out sprite))
            {
                StoreSprite(id, sprite, saveDisk: false);
                TouchLru(id);
                _hits++;
                return true;
            }

            _misses++;
            sprite = null;
            return false;
        }

        public void StoreSprite(int id, Sprite sprite, bool saveDisk = true)
        {
            if (sprite == null) return;

            EnsureCapacity();
            _sprites[id] = sprite;
            TouchLru(id);

            if (_useDiskCache && saveDisk && sprite.texture != null)
            {
                SaveDiskTexture(id, sprite.texture);
            }
        }

        // ── Textures ─────────────────────────────────────────────────────────

        public bool TryGetTexture(int id, out Texture2D texture)
        {
            if (_textures.TryGetValue(id, out texture) && texture != null)
            {
                TouchLru(id);
                _hits++;
                return true;
            }

            if (_useDiskCache && TryLoadDiskTexture(id, out texture))
            {
                StoreTexture(id, texture, saveDisk: false);
                TouchLru(id);
                _hits++;
                return true;
            }

            _misses++;
            texture = null;
            return false;
        }

        public void StoreTexture(int id, Texture2D texture, bool saveDisk = true)
        {
            if (texture == null) return;

            EnsureCapacity();
            _textures[id] = texture;
            TouchLru(id);

            if (_useDiskCache && saveDisk)
            {
                SaveDiskTexture(id, texture);
            }
        }

        // ── Meshes ───────────────────────────────────────────────────────────

        public bool TryGetMesh(int id, out Mesh mesh)
        {
            if (_meshes.TryGetValue(id, out mesh) && mesh != null)
            {
                TouchLru(id);
                _hits++;
                return true;
            }

            _misses++;
            mesh = null;
            return false;
        }

        public void StoreMesh(int id, Mesh mesh)
        {
            if (mesh == null) return;

            EnsureCapacity();
            _meshes[id] = mesh;
            TouchLru(id);
        }

        // ── Cache Eviction & Cleanup ─────────────────────────────────────────

        void TouchLru(int id)
        {
            if (_lruNodes.TryGetValue(id, out var node))
            {
                _lruKeys.Remove(node);
                _lruKeys.AddFirst(node);
            }
            else
            {
                var newNode = _lruKeys.AddFirst(id);
                _lruNodes[id] = newNode;
            }
        }

        void EnsureCapacity()
        {
            while (_lruKeys.Count >= _capacity)
            {
                var last = _lruKeys.Last;
                if (last == null) break;

                int evictId = last.Value;
                _lruKeys.RemoveLast();
                _lruNodes.Remove(evictId);

                // Evict memory references
                if (_sprites.TryGetValue(evictId, out var s))
                {
                    _sprites.Remove(evictId);
                    if (s != null) UnityEngine.Object.DestroyImmediate(s);
                }

                if (_textures.TryGetValue(evictId, out var t))
                {
                    _textures.Remove(evictId);
                    if (t != null) UnityEngine.Object.DestroyImmediate(t);
                }

                if (_meshes.TryGetValue(evictId, out var m))
                {
                    _meshes.Remove(evictId);
                    if (m != null) UnityEngine.Object.DestroyImmediate(m);
                }
            }
        }

        public void Clear()
        {
            foreach (var s in _sprites.Values)
                if (s != null) UnityEngine.Object.DestroyImmediate(s);
            _sprites.Clear();

            foreach (var t in _textures.Values)
                if (t != null) UnityEngine.Object.DestroyImmediate(t);
            _textures.Clear();

            foreach (var m in _meshes.Values)
                if (m != null) UnityEngine.Object.DestroyImmediate(m);
            _meshes.Clear();

            _lruKeys.Clear();
            _lruNodes.Clear();
            _hits = 0;
            _misses = 0;
        }

        // ── Disk Serialization (Tier 2) ──────────────────────────────────────

        bool TryLoadDiskTexture(int id, out Texture2D tex)
        {
            tex = null;
            if (string.IsNullOrEmpty(_diskCacheDir) || !Directory.Exists(_diskCacheDir))
                return false;

            string path = Path.Combine(_diskCacheDir, $"{id}.png");
            if (!File.Exists(path)) return false;

            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                tex = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                {
                    name = $"sym{id}_disk_cached",
                    hideFlags = HideFlags.HideAndDontSave,
                };
                return tex.LoadImage(bytes);
            }
            catch
            {
                if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
                tex = null;
                return false;
            }
        }

        bool TryLoadDiskSprite(int id, out Sprite sprite)
        {
            sprite = null;
            if (TryLoadDiskTexture(id, out var tex))
            {
                sprite = Sprite.Create(
                    tex,
                    new Rect(0, 0, tex.width, tex.height),
                    new Vector2(0.5f, 0.5f),
                    100f);
                sprite.name = $"sym{id}_disk_sprite";
                sprite.hideFlags = HideFlags.HideAndDontSave;
                return true;
            }
            return false;
        }

        void SaveDiskTexture(int id, Texture2D tex)
        {
            if (tex == null || string.IsNullOrEmpty(_diskCacheDir)) return;

            try
            {
                if (!Directory.Exists(_diskCacheDir))
                    Directory.CreateDirectory(_diskCacheDir);

                string path = Path.Combine(_diskCacheDir, $"{id}.png");
                if (!File.Exists(path))
                {
                    byte[] png = tex.EncodeToPNG();
                    if (png != null && png.Length > 0)
                        File.WriteAllBytes(path, png);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[TwoTierArtCache] Failed saving disk cache for {id}: {ex.Message}");
            }
        }
    }
}
#endif
