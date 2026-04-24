using PFE.Core;
using VContainer.Unity;

namespace PFE.Systems.Audio
{
    /// <summary>
    /// Polls GameSettings each tick and pushes SfxVolume / MusicVolume to the
    /// audio services whenever the values change. This lets the Inspector sliders on
    /// GameSettings.asset take effect immediately while the game is running.
    /// </summary>
    public sealed class AudioVolumeSync : ITickable
    {
        private readonly ISoundService _sounds;
        private readonly IMusicService _music;
        private readonly GameSettings _settings;

        private float _lastSfx;
        private float _lastMusic;

        public AudioVolumeSync(ISoundService sounds, IMusicService music, GameSettings settings)
        {
            _sounds = sounds;
            _music = music;
            _settings = settings;

            // Force apply on first tick (AudioSources are ready by then).
            _lastSfx   = -1f;
            _lastMusic = -1f;
        }

        public void Tick()
        {
            float sfx = _settings.SfxVolume;
            float mus = _settings.MusicVolume;

            if (sfx != _lastSfx)
            {
                _sounds.SfxVolume = sfx;
                _lastSfx = sfx;
            }

            if (mus != _lastMusic)
            {
                _music.MusicVolume = mus;
                _lastMusic = mus;
            }
        }
    }
}
