using UnityEngine;
using VContainer;

namespace PFE.Systems.Audio
{
    /// <summary>
    /// Triggers an ambient music track when the player enters this zone.
    ///
    /// Setup: Add to any Collider2D marked as Trigger on a room or area GameObject.
    /// Set Tag on the player to "Player" (or adjust _playerTag) so the collider filter works.
    ///
    /// The trigger only fires PlayAmbient — combat music is managed separately by
    /// enemy units and takes priority automatically.
    /// </summary>
    [RequireComponent(typeof(Collider2D))]
    public class LocationMusicTrigger : MonoBehaviour
    {
        [Tooltip("Music track ID from MusicCatalog (e.g. 'music_surf', 'music_sewer_1')")]
        [SerializeField] private string _trackId;

        [Tooltip("Tag used to identify the player. Must match the Player GameObject's tag.")]
        [SerializeField] private string _playerTag = "Player";

        [Inject] private IMusicService _music;

        private void OnTriggerEnter2D(Collider2D other)
        {
            if (!other.CompareTag(_playerTag)) return;
            if (string.IsNullOrEmpty(_trackId)) return;
            _music?.PlayAmbient(_trackId);
        }
    }
}
