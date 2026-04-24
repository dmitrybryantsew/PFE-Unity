using UnityEngine;
using VContainer;

namespace PFE.Systems.Audio
{
    /// <summary>
    /// Registers and releases a combat music claim when this unit becomes
    /// aware/unaware of the player.
    ///
    /// Setup: Add to any enemy unit GameObject.
    /// Call SetAware(true) when the unit spots the player; SetAware(false) when it
    /// loses sight, dies, or is disabled.
    ///
    /// Priority values: 0 = ambient (don't use here), 1-49 = generic enemies,
    /// 50-99 = elite/faction, 100+ = boss.
    /// </summary>
    public class UnitCombatMusicEmitter : MonoBehaviour
    {
        [Tooltip("Track ID from MusicCatalog (e.g. 'music_raiders', 'boss_1')")]
        [SerializeField] private string _trackId;

        [Tooltip("Higher priority overrides lower. Boss > elite > normal enemy > ambient.")]
        [SerializeField] [Range(1, 200)] private int _priority = 10;

        [Inject] private IMusicService _music;

        private bool _isActive;

        private void OnDisable()
        {
            // Release claim when unit is disabled or destroyed
            Release();
        }

        /// <summary>
        /// Call when the unit becomes aware of the player (aggro state).
        /// </summary>
        public void SetAware(bool aware)
        {
            if (aware == _isActive) return;
            _isActive = aware;

            if (aware)
                _music?.PlayCombat(_trackId, _priority);
            else
                Release();
        }

        private void Release()
        {
            if (!_isActive) return;
            _isActive = false;
            _music?.ReleaseCombat(_priority);
        }
    }
}
