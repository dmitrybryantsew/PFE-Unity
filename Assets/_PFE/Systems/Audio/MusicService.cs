using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace PFE.Systems.Audio
{
    /// <summary>
    /// IMusicService implementation.
    ///
    /// Two AudioSources (_trackA / _trackB) are used as a ping-pong pair for
    /// crossfading. The "active" source is always the one currently audible;
    /// the "inactive" source fades in as the new track while the active one fades out.
    ///
    /// Priority queue:
    ///   - Ambient track: priority 0, set via PlayAmbient().
    ///   - Combat tracks: any priority > 0, registered via PlayCombat().
    ///   - The highest registered priority wins. Ties keep the current track.
    ///   - When all combat claims at a given priority are released, the next
    ///     highest priority (or ambient at priority 0) takes over.
    ///
    /// Scene setup: Add this component to the AudioManager GameObject. Assign
    /// MusicCatalog asset. Then reference in GameLifetimeScope._musicService.
    /// </summary>
    public class MusicService : MonoBehaviour, IMusicService
    {
        [Header("Catalog")]
        [SerializeField] private MusicCatalog _catalog;

        [Header("Settings")]
        [SerializeField] [Range(0f, 1f)] private float _musicVolume = 0.2f;
        [SerializeField] private float _crossfadeDuration = 3f; // matches original 100 frames @ 30fps

        // ---- Audio sources (ping-pong crossfade) ----
        private AudioSource _trackA;
        private AudioSource _trackB;
        private bool _aIsActive; // which source is currently "playing" the old track

        // ---- State ----
        private string _ambientTrackId;
        private string _activeTrackId;

        // Combat priority queue: priority → trackId. Multiple entries at different priorities.
        // Highest priority value wins.
        private readonly SortedDictionary<int, string> _combatQueue =
            new SortedDictionary<int, string>(Comparer<int>.Create((a, b) => b.CompareTo(a))); // descending

        // ---- Crossfade cancellation ----
        private CancellationTokenSource _fadeCts;

        // ---- IMusicService ----

        public float MusicVolume
        {
            get => _musicVolume;
            set
            {
                _musicVolume = Mathf.Clamp01(value);
                ApplyVolumeToBothSources();
            }
        }

        // ---------------------------------------------------------------

        private void Awake()
        {
            _trackA = CreateSource("MusicTrack_A");
            _trackB = CreateSource("MusicTrack_B");
            _aIsActive = true; // _trackA will be the "fade out" source first
        }

        private void OnDestroy()
        {
            CancelFade();
        }

        // ---------------------------------------------------------------
        // IMusicService
        // ---------------------------------------------------------------

        public void PlayAmbient(string trackId)
        {
            _ambientTrackId = trackId;

            // Only switch if no combat track is active
            if (_combatQueue.Count == 0)
                SwitchTo(trackId);
        }

        public void PlayCombat(string trackId, int priority)
        {
            _combatQueue[priority] = trackId;
            RefreshActiveTrack();
        }

        public void ReleaseCombat(int priority)
        {
            _combatQueue.Remove(priority);
            RefreshActiveTrack();
        }

        public void PlayOneShot(string trackId)
        {
            if (_catalog == null || !_catalog.TryGet(trackId, out var clip)) return;

            // Play on the currently-inactive source so it doesn't interrupt the loop.
            // We do NOT update _activeTrackId so the priority queue resumes afterwards.
            var oneShot = InactiveSource();
            oneShot.clip = clip;
            oneShot.volume = _musicVolume;
            oneShot.loop = false;
            oneShot.Play();
        }

        public void StopAll()
        {
            CancelFade();
            _trackA.Stop();
            _trackB.Stop();
            _activeTrackId = null;
            _ambientTrackId = null;
            _combatQueue.Clear();
        }

        // ---------------------------------------------------------------
        // Internal helpers
        // ---------------------------------------------------------------

        private void RefreshActiveTrack()
        {
            // Highest priority combat track wins; fall back to ambient if none.
            string target = _combatQueue.Count > 0
                ? GetHighestPriorityCombatTrack()
                : _ambientTrackId;

            SwitchTo(target);
        }

        private string GetHighestPriorityCombatTrack()
        {
            foreach (var kv in _combatQueue) // SortedDictionary is descending
                return kv.Value;
            return null;
        }

        private void SwitchTo(string trackId)
        {
            if (string.IsNullOrEmpty(trackId)) return;
            if (trackId == _activeTrackId) return; // already playing

            if (_catalog == null || !_catalog.TryGet(trackId, out var clip))
            {
                Debug.LogWarning($"[MusicService] Unknown track ID '{trackId}' — not in catalog.");
                return;
            }

            _activeTrackId = trackId;
            CrossfadeTo(clip).Forget();
        }

        private async UniTaskVoid CrossfadeTo(AudioClip clip)
        {
            CancelFade();
            _fadeCts = new CancellationTokenSource();
            var token = _fadeCts.Token;

            var fadeOut = ActiveSource();
            var fadeIn  = InactiveSource();

            // Prepare the incoming track
            fadeIn.clip = clip;
            fadeIn.volume = 0f;
            fadeIn.loop = true;
            fadeIn.Play();

            // Swap active designation immediately so SwitchTo won't restart mid-fade
            _aIsActive = !_aIsActive;

            float elapsed = 0f;
            float startVolOut = fadeOut.volume;

            while (elapsed < _crossfadeDuration)
            {
                if (token.IsCancellationRequested) return;

                elapsed += Time.unscaledDeltaTime;
                float t = Mathf.Clamp01(elapsed / _crossfadeDuration);

                fadeOut.volume = Mathf.Lerp(startVolOut, 0f, t);
                fadeIn.volume  = Mathf.Lerp(0f, _musicVolume, t);

                await UniTask.Yield(PlayerLoopTiming.Update, token);
            }

            // Ensure exact final values
            fadeOut.Stop();
            fadeOut.clip = null;
            fadeIn.volume = _musicVolume;
        }

        private void CancelFade()
        {
            if (_fadeCts != null)
            {
                _fadeCts.Cancel();
                _fadeCts.Dispose();
                _fadeCts = null;
            }
        }

        /// <summary>The source currently playing the "old" track (being faded out).</summary>
        private AudioSource ActiveSource()  => _aIsActive ? _trackA : _trackB;

        /// <summary>The source that will carry the new track (being faded in).</summary>
        private AudioSource InactiveSource() => _aIsActive ? _trackB : _trackA;

        private void ApplyVolumeToBothSources()
        {
            // Only raise volume on whichever source is the current "active" one.
            // The other may be mid-fade or stopped.
            // Guard: sources are created in Awake; setter may be called before that.
            var active = _aIsActive ? _trackB : _trackA; // after swap _trackB is the faded-in one
            if (active != null && active.isPlaying)
                active.volume = _musicVolume;
        }

        private AudioSource CreateSource(string goName)
        {
            var go = new GameObject(goName);
            go.transform.SetParent(transform);
            var src = go.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.spatialBlend = 0f; // 2D music, no panning
            src.volume = 0f;
            src.loop = true;
            return src;
        }
    }
}
