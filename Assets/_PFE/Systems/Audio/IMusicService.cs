namespace PFE.Systems.Audio
{
    /// <summary>
    /// Music playback service.
    ///
    /// Two-layer system matching original Snd.as:
    ///   Ambient  — low-priority location theme (priority 0), loops indefinitely.
    ///   Combat   — higher-priority track registered by enemy units. Multiple units
    ///              can register simultaneously; highest priority wins. When all
    ///              combat claims are released, music fades back to the ambient track.
    ///
    /// Wire up: add MusicService MonoBehaviour to the AudioManager GameObject and
    /// assign it to GameLifetimeScope._musicService in the Inspector.
    /// </summary>
    public interface IMusicService
    {
        /// <summary>
        /// Play (or crossfade to) a looping ambient/location track.
        /// Priority is 0 — combat music always overrides it.
        /// No-op if the same track is already playing as ambient.
        /// </summary>
        void PlayAmbient(string trackId);

        /// <summary>
        /// Register a combat music claim. If priority is higher than the currently
        /// active music, this track fades in immediately.
        /// Multiple callers can hold simultaneous claims; highest priority wins.
        /// </summary>
        void PlayCombat(string trackId, int priority);

        /// <summary>
        /// Release a previously registered combat claim at this priority level.
        /// When no combat claims remain, music fades back to the ambient track.
        /// </summary>
        void ReleaseCombat(int priority);

        /// <summary>
        /// Play a one-shot music sting that does not loop (e.g. death sting "harddie").
        /// Does not affect the priority queue; ambient/combat resume afterwards.
        /// </summary>
        void PlayOneShot(string trackId);

        /// <summary>Stop all music immediately (no fade).</summary>
        void StopAll();

        /// <summary>Master music volume, 0..1. Persisted across scenes.</summary>
        float MusicVolume { get; set; }
    }
}
