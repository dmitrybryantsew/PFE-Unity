using PFE.Systems.Audio;
using UnityEngine;

namespace PFE.UI.Common
{
    /// <summary>
    /// The interface's four audio signals, named for what they mean rather than for the clip they play.
    ///
    /// <para><b>Why this exists at all.</b> The Oracle's interface is never silent: a tab switch beeps,
    /// an item click chirps, opening the device clunks, and every button answers. The four sounds are
    /// declared in <c>Snd.as:288,302-304</c> and the project's existing sound importer has already put
    /// them in <c>SoundCatalog.asset</c>, so this class is only the naming layer — it resolves
    /// <c>pip1</c> / <c>pip2</c> / <c>pip3</c> / <c>button</c> through the shared
    /// <see cref="ISoundService"/> instead of holding private copies of the clips.</para>
    ///
    /// <para><b>UI sounds are played at the listener, not at the widget.</b> A PipBuck beep is a
    /// non-diegetic signal — it comes out of the device in the player's hands, not out of the world — so
    /// it is played at the camera, which gives full volume and no stereo pan. Playing it at the clicked
    /// object's world position would pan the beep across the speakers as the player looks around.</para>
    ///
    /// <para><b>Null-tolerant on purpose.</b> A scene with no <see cref="ISoundService"/> (a test, a
    /// headless harness, an early-boot frame) must not throw out of a click handler, so every entry
    /// point is a no-op when the service is absent. Silence is a degraded interface; an exception out of
    /// a UI callback is a broken one.</para>
    /// </summary>
    public class UiSoundService
    {
        /// <summary><c>23_pip1</c> — tab navigation / category cycle.</summary>
        public const string TabSoundId = "pip1";

        /// <summary><c>22_pip2</c> — item selection / action.</summary>
        public const string ItemSoundId = "pip2";

        /// <summary><c>21_pip3</c> — device open / close.</summary>
        public const string DeviceSoundId = "pip3";

        /// <summary><c>94_button</c> — generic button click.</summary>
        public const string ButtonSoundId = "button";

        private readonly ISoundService _sound;
        private Camera _camera;

        public UiSoundService(ISoundService sound)
        {
            _sound = sound;
        }

        /// <summary>True when sounds will actually be audible.</summary>
        public bool IsAudible => _sound != null;

        /// <summary>Tab / category cycle beep — <c>pip1</c>.</summary>
        public void PlayTabBeep(float volumeScale = 1f) => Play(TabSoundId, volumeScale);

        /// <summary>Item selection / action click — <c>pip2</c>.</summary>
        public void PlayItemClick(float volumeScale = 1f) => Play(ItemSoundId, volumeScale);

        /// <summary>Device open / close clunk — <c>pip3</c>.</summary>
        public void PlayDeviceToggle(float volumeScale = 1f) => Play(DeviceSoundId, volumeScale);

        /// <summary>Generic button click — <c>button</c>.</summary>
        public void PlayButtonClick(float volumeScale = 1f) => Play(ButtonSoundId, volumeScale);

        /// <summary>
        /// Plays an interface signal by its raw <c>Snd.as</c> id. For the rare case where a view has its
        /// own signal that is not one of the four above.
        /// </summary>
        public void Play(string soundId, float volumeScale = 1f)
        {
            if (_sound == null || string.IsNullOrEmpty(soundId)) return;
            _sound.Play(soundId, ListenerPosition(), volumeScale);
        }

        private Vector2 ListenerPosition()
        {
            // Cached, but re-resolved while null: the camera usually exists by the first click, and a
            // cached null would silence every UI sound for the rest of the session.
            if (_camera == null) _camera = Camera.main;
            return _camera != null ? (Vector2)_camera.transform.position : Vector2.zero;
        }
    }
}
