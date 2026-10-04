using System;
using System.Collections.Generic;
using UnityEngine;

namespace PFE.Data.Definitions
{
    /// <summary>
    /// The frames for one art asset, keyed by the id the <c>&lt;part&gt;</c> row names.
    /// </summary>
    /// <remarks>
    /// <para><b>The key is the <i>asset</i> id, not the part id.</b> Several rows share one asset —
    /// <c>expl</c>, <c>fireexpl</c> and <c>ttexpl</c> all name the sheet <c>sprExpl</c>, and the four
    /// <c>sprIskr</c> rows share one sheet — so filing by part id would store the same 3360×240 texture
    /// three times. One entry per distinct asset, and the runtime resolves row → asset id → frames.</para>
    ///
    /// <para><b><c>cellWidth</c>/<c>cellHeight</c> are the sheet's cell size and are 0 for <c>vis=</c>
    /// assets</b>, which have one PNG per frame and no cell geometry. They are kept because the importer
    /// slices sheets with them and a readback that re-sliced would need them; nothing at runtime reads
    /// them, since the blit path draws a whole frame at a time.</para>
    /// </remarks>
    [Serializable]
    public struct ParticleSpriteEntry
    {
        /// <summary>The <c>vis=</c> class name or the <c>blit=</c> sheet id, exactly as the row spells it.</summary>
        public string id;

        /// <summary>Frames in playback order, index 0 first.</summary>
        public Sprite[] frames;

        /// <summary>Sheet cell width in pixels — <c>blit=</c> only, 0 for <c>vis=</c>.</summary>
        public int cellWidth;

        /// <summary>Sheet cell height in pixels — <c>blit=</c> only, 0 for <c>vis=</c>.</summary>
        public int cellHeight;
    }

    /// <summary>
    /// The particle art catalogue — every <c>vis=</c> class and <c>blit=</c> sheet the 118 rows name,
    /// with its frames. Written by <c>ParticleSpriteImporter</c>, read by the renderer.
    ///
    /// <para><b>A separate asset from <see cref="ParticleDefinitionAsset"/>, deliberately.</b> The two are
    /// produced by different importers from different source trees (<c>AllData.as</c> versus
    /// <c>_assets/sprites</c> + <c>sprite1.swf</c>), and the art importer is far more likely to need a
    /// re-run — an exporter change, a new sheet. Keeping them apart means re-importing art cannot
    /// disturb the 118 parsed rows, and vice versa. The one thing that would couple them is the frame
    /// count, and that is read through <c>IParticleSpriteFrames</c> at spawn time rather than baked into
    /// the definitions.</para>
    ///
    /// <para><b><c>missingIds</c> is not decoration.</b> An asset id that resolves to no frames is the
    /// silent-nothing failure this workstream keeps meeting: a row that renders as nothing looks exactly
    /// like a row that emits nothing. The importer records every id it could not fill so the count is
    /// visible on the asset itself, without needing the editor log.</para>
    /// </summary>
    [CreateAssetMenu(fileName = "ParticleSprites", menuName = "PFE/Particle Sprites")]
    public class ParticleSpriteCatalogAsset : ScriptableObject
    {
        [Header("Entries")]
        [Tooltip("One entry per distinct vis= class / blit= sheet id. Several <part> rows may share one.")]
        public ParticleSpriteEntry[] entries = Array.Empty<ParticleSpriteEntry>();

        [Header("Import report")]
        [Tooltip("Asset ids the importer could not resolve to frames. Non-empty means rows will render as nothing.")]
        public string[] missingIds = Array.Empty<string>();

        [Tooltip("Asset ids resolved to a sheet but with a frame count of 0 — a blitx/blity mismatch.")]
        public string[] emptyIds = Array.Empty<string>();

        private Dictionary<string, int> _index;

        /// <summary>
        /// Id → entry index. Built once, on first use, because <see cref="ParticleSpriteCatalog"/>
        /// resolves this on every particle spawn and a linear scan of 110 entries per particle per tick
        /// is exactly the kind of cost that shows up as an unexplained frame drop.
        /// </summary>
        public Dictionary<string, int> Index
        {
            get
            {
                if (_index != null) return _index;

                _index = new Dictionary<string, int>(entries?.Length ?? 0, StringComparer.Ordinal);
                if (entries != null)
                {
                    for (int i = 0; i < entries.Length; i++)
                    {
                        string id = entries[i].id;
                        if (string.IsNullOrEmpty(id)) continue;

                        // First wins, matching ParticleSpriteSource.ParseSymbolCsv. Last-wins would make
                        // resolution depend on array order, which the importer does not guarantee.
                        if (!_index.ContainsKey(id)) _index[id] = i;
                    }
                }

                return _index;
            }
        }

        /// <summary>
        /// Drops the cached index. The importer calls this after assigning <see cref="entries"/>, because
        /// the cache lives outside the serialised state and would otherwise survive the write — the same
        /// staleness <see cref="ParticleDefinitionAsset.InvalidateTable"/> exists to prevent.
        /// </summary>
        public void InvalidateIndex() => _index = null;

        /// <summary>Frames for an asset id, or null when the id is absent or empty.</summary>
        public Sprite[] FramesFor(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            return Index.TryGetValue(id, out int index) ? entries[index].frames : null;
        }
    }
}
