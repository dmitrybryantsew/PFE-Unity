using System.Collections.Generic;
using UnityEngine;
using VContainer;

namespace PFE.Systems.Audio
{
    /// <summary>
    /// ISoundService implementation.
    ///
    /// Features:
    /// - Pool of AudioSources on child GameObjects (avoids allocations per play)
    /// - Automatic 2D left/right pan based on world position vs camera centre
    /// - ±10% random volume variation matching original Snd.as globalVol scaling
    /// - Grouped IDs (rm, rw, fang_hit …): random variant picked per play
    /// - Looping sounds keyed by caller object
    /// - Unknown IDs: single warning, never throws
    ///
    /// Scene setup: add SoundService MonoBehaviour to any persistent GameObject and
    /// register it in the LifetimeScope as ISoundService.
    /// </summary>
    public class SoundService : MonoBehaviour, ISoundService
    {
        [Header("Settings")]
        [SerializeField] private SoundCatalog _catalog;
        [SerializeField] [Range(1, 32)] private int _poolSize = 16;
        [SerializeField] [Range(0f, 1f)] private float _globalVolume = 0.4f;

        public float SfxVolume
        {
            get => _globalVolume;
            set
            {
                _globalVolume = Mathf.Clamp01(value);
                // Update volume on all active loops immediately
                foreach (var kv in _loops)
                    if (kv.Value != null)
                        kv.Value.volume = _globalVolume;
            }
        }

        // pool of one-shot sources
        private AudioSource[] _pool;
        private int _poolIndex;

        // active loops: key → AudioSource
        private readonly Dictionary<object, AudioSource> _loops = new Dictionary<object, AudioSource>();

        // IDs we have already warned about (avoid log spam)
        private readonly HashSet<string> _warnedIds = new HashSet<string>();

        // ---------------------------------------------------------------

        private void Awake()
        {
            _pool = new AudioSource[_poolSize];
            for (int i = 0; i < _poolSize; i++)
            {
                var go = new GameObject($"SoundPool_{i}");
                go.transform.SetParent(transform);
                var src = go.AddComponent<AudioSource>();
                src.playOnAwake = false;
                src.spatialBlend = 0f; // 2D — we handle pan manually
                _pool[i] = src;
            }
        }

        // ---------------------------------------------------------------
        // ISoundService
        // ---------------------------------------------------------------

        public void Play(string id, Vector2 worldPos, float volumeScale = 1f)
        {
            if (!TryResolveClip(id, out var clip)) return;

            var src = NextPooledSource();
            src.clip = clip;
            src.volume = _globalVolume * volumeScale * RandomVolumeMod();
            src.panStereo = ComputePan(worldPos);
            src.loop = false;
            src.Play();
        }

        public void PlayLoop(string id, object key, float volume = 1f)
        {
            if (key == null) return;
            if (_loops.ContainsKey(key)) return; // already looping for this key

            if (!TryResolveClip(id, out var clip)) return;

            // Use a dedicated source outside the pool so it isn't hijacked
            var go = new GameObject($"Loop_{id}");
            go.transform.SetParent(transform);
            var src = go.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.spatialBlend = 0f;
            src.clip = clip;
            src.volume = _globalVolume * volume;
            src.loop = true;
            src.Play();

            _loops[key] = src;
        }

        public void PlayLoopFromTime(string id, object key, float startTimeSec, float volume = 1f)
        {
            if (key == null) return;

            // Stop any existing loop for this key first.
            StopLoop(key);

            if (!TryResolveClip(id, out var clip)) return;

            var go = new GameObject($"Loop_{id}");
            go.transform.SetParent(transform);
            var src = go.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.spatialBlend = 0f;
            src.clip = clip;
            src.volume = _globalVolume * volume;
            src.loop = true; // fallback so it doesn't go silent if we miss a position check
            src.time = Mathf.Clamp(startTimeSec, 0f, Mathf.Max(0f, clip.length - 0.01f));
            src.Play();

            _loops[key] = src;
        }

        public float GetLoopTime(object key)
        {
            if (key == null) return -1f;
            if (!_loops.TryGetValue(key, out var src) || src == null) return -1f;
            if (!src.isPlaying) return -1f;
            return src.time;
        }

        public void StopLoop(object key)
        {
            if (key == null) return;
            if (!_loops.TryGetValue(key, out var src)) return;
            src.Stop();
            Destroy(src.gameObject);
            _loops.Remove(key);
        }

        public void StopAll()
        {
            foreach (var src in _pool)
                src.Stop();

            foreach (var kv in _loops)
            {
                kv.Value.Stop();
                Destroy(kv.Value.gameObject);
            }
            _loops.Clear();
        }

        // ---------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------

        private bool TryResolveClip(string id, out AudioClip clip)
        {
            clip = null;
            if (string.IsNullOrEmpty(id)) return false;
            if (_catalog == null)
            {
                WarnOnce($"[SoundService] No SoundCatalog assigned — cannot play '{id}'");
                return false;
            }
            if (!_catalog.TryGet(id, out var entry))
            {
                WarnOnce($"[SoundService] Unknown sound ID '{id}'");
                return false;
            }
            if (entry.variants == null || entry.variants.Length == 0)
            {
                WarnOnce($"[SoundService] Sound '{id}' has no clips");
                return false;
            }
            clip = entry.variants.Length == 1
                ? entry.variants[0]
                : entry.variants[Random.Range(0, entry.variants.Length)];
            return clip != null;
        }

        private AudioSource NextPooledSource()
        {
            // Round-robin through the pool. If the chosen source is still playing,
            // keep searching; if all are busy, forcibly reuse the oldest one.
            for (int i = 0; i < _pool.Length; i++)
            {
                int idx = (_poolIndex + i) % _pool.Length;
                if (!_pool[idx].isPlaying)
                {
                    _poolIndex = (idx + 1) % _pool.Length;
                    return _pool[idx];
                }
            }
            // All busy — steal the next one
            var stolen = _pool[_poolIndex];
            stolen.Stop();
            _poolIndex = (_poolIndex + 1) % _pool.Length;
            return stolen;
        }

        /// <summary>
        /// Map world x position to stereo pan [-1, 1] matching original Snd.as formula.
        /// centrX=1000, widthX=2000 in original → scale by camera half-width in Unity.
        /// </summary>
        private static float ComputePan(Vector2 worldPos)
        {
            var cam = Camera.main;
            if (cam == null) return 0f;
            float halfWidth = cam.orthographicSize * cam.aspect;
            float centerX = cam.transform.position.x;
            return Mathf.Clamp((worldPos.x - centerX) / halfWidth, -1f, 1f);
        }

        /// <summary>±10% volume randomisation matching original Snd.as.</summary>
        private static float RandomVolumeMod() => Random.Range(0.9f, 1.1f);

        private void WarnOnce(string msg)
        {
            if (_warnedIds.Add(msg))
                Debug.LogWarning(msg);
        }
    }
}
