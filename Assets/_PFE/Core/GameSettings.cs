using UnityEngine;

namespace PFE.Core
{
    /// <summary>
    /// Persistent game-wide settings that belong to the shipped product.
    /// These are player-facing values (volume, etc.) — not developer debug toggles.
    ///
    /// Stored as a ScriptableObject so defaults are designer-controlled in the asset,
    /// and AudioVolumeSync pushes them to the audio services each tick so Inspector
    /// changes take effect immediately during Play mode.
    ///
    /// Future: load/save these from PlayerPrefs or a save file so player preferences persist.
    /// </summary>
    [CreateAssetMenu(fileName = "GameSettings", menuName = "PFE/Game Settings")]
    public sealed class GameSettings : ScriptableObject
    {
        // ── Audio ────────────────────────────────────────────────────────────

        [Header("Audio")]
        [SerializeField]
        [Range(0f, 1f)]
        [Tooltip("Master SFX volume (0 = mute, 1 = full). Applies at runtime.")]
        private float sfxVolume = 0.4f;

        [SerializeField]
        [Range(0f, 1f)]
        [Tooltip("Master music volume (0 = mute, 1 = full). Applies at runtime.")]
        private float musicVolume = 0.2f;

        public float SfxVolume   => sfxVolume;
        public float MusicVolume => musicVolume;
    }
}
