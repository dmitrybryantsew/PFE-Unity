using System;
using System.Collections.Generic;
using UnityEngine;

namespace PFE.Systems.Audio
{
    /// <summary>
    /// ScriptableObject that maps music track IDs to AudioClips.
    ///
    /// Create via Assets > Create > PFE > Music Catalog.
    /// Populate manually in the Inspector, or run PFE/Import/Import Music
    /// to auto-fill from the extracted .mp3 files.
    ///
    /// Unlike SoundCatalog, each music ID maps to exactly one clip — no
    /// random variants. Clips should be imported as Load Type = Streaming.
    /// </summary>
    [CreateAssetMenu(fileName = "MusicCatalog", menuName = "PFE/Music Catalog")]
    public class MusicCatalog : ScriptableObject
    {
        [Serializable]
        public class Entry
        {
            [Tooltip("Track ID used in code (e.g. 'music_surf', 'combat_1', 'mainmenu')")]
            public string id;

            [Tooltip("AudioClip — set Load Type to Streaming in the import settings")]
            public AudioClip clip;
        }

        public List<Entry> tracks = new List<Entry>();

        private Dictionary<string, AudioClip> _map;

        private void OnEnable() => _map = null;

        private void BuildMap()
        {
            _map = new Dictionary<string, AudioClip>(tracks.Count, StringComparer.Ordinal);
            foreach (var e in tracks)
            {
                if (!string.IsNullOrEmpty(e.id) && e.clip != null)
                    _map[e.id] = e.clip;
            }
        }

        public bool TryGet(string id, out AudioClip clip)
        {
            if (_map == null) BuildMap();
            return _map.TryGetValue(id, out clip);
        }

        /// <summary>Call after editing tracks at runtime to rebuild the lookup.</summary>
        public void InvalidateCache() => _map = null;
    }
}
