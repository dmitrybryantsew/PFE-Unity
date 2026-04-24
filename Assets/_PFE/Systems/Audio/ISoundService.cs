using UnityEngine;

namespace PFE.Systems.Audio
{
    /// <summary>
    /// Runtime sound service. Resolves logical IDs from SoundCatalog, handles 2D panning,
    /// pooled AudioSources, grouped (random) clips, and looping sounds.
    ///
    /// Callers never hold AudioClip references — only string IDs from Snd.as.
    /// </summary>
    public interface ISoundService
    {
        /// <summary>Play a one-shot sound at a world position (handles 2D pan automatically).</summary>
        void Play(string id, Vector2 worldPos, float volumeScale = 1f);

        /// <summary>Start a looping sound. Keyed by caller object so the same caller can stop it later.</summary>
        void PlayLoop(string id, object key, float volume = 1f);

        /// <summary>
        /// Start (or restart) a looping sound from a given time offset.
        /// If a loop is already active for this key it is stopped first.
        /// Used for prep-sound weapons (minigun, flamer, etc.) that seek within
        /// a single audio file that contains spin-up, fire-loop, and spin-down sections.
        /// </summary>
        void PlayLoopFromTime(string id, object key, float startTimeSec, float volume = 1f);

        /// <summary>
        /// Current playback position of a keyed loop in seconds.
        /// Returns -1 if the key is not active or the source has stopped.
        /// </summary>
        float GetLoopTime(object key);

        /// <summary>Stop the loop started by this key.</summary>
        void StopLoop(object key);

        /// <summary>Stop all active sounds (use on scene unload / game pause).</summary>
        void StopAll();

        /// <summary>Master SFX volume, 0..1. Applies to all subsequent Play/PlayLoop calls and live loops.</summary>
        float SfxVolume { get; set; }
    }
}
