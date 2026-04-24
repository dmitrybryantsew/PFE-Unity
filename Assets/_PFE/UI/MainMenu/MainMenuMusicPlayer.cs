using PFE.Systems.Audio;
using UnityEngine;

namespace PFE.UI.MainMenu
{
    /// <summary>
    /// Plays main menu music without requiring VContainer.
    ///
    /// Attach to any persistent GameObject in the MainMenuScene.
    /// Assign the MusicCatalog asset. The track "mainmenu" will loop
    /// until this GameObject is destroyed (scene unload).
    ///
    /// Uses its own AudioSource — no dependency on SoundService or MusicService.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MainMenuMusicPlayer : MonoBehaviour
    {
        [SerializeField] private MusicCatalog _catalog;
        [SerializeField] private string _trackId = "mainmenu";
        [SerializeField] [Range(0f, 1f)] private float _volume = 0.2f;

        private AudioSource _source;

        private void Awake()
        {
            _source = gameObject.AddComponent<AudioSource>();
            _source.playOnAwake = false;
            _source.spatialBlend = 0f;
            _source.loop = true;
            _source.volume = _volume;
        }

        private void Start()
        {
            if (_catalog == null)
            {
                Debug.LogWarning("[MainMenuMusicPlayer] No MusicCatalog assigned — music will not play.");
                return;
            }

            if (!_catalog.TryGet(_trackId, out var clip))
            {
                Debug.LogWarning($"[MainMenuMusicPlayer] Track '{_trackId}' not found in catalog.");
                return;
            }

            _source.clip = clip;
            _source.Play();
        }
    }
}
