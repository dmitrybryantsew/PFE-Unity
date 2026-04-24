using System;
using System.Collections.Generic;
using UnityEngine;

namespace PFE.Systems.Map.Rendering
{
    /// <summary>
    /// Rendering data for a single tile material.
    /// Maps material ID to texture names, mask class names, and visual filters.
    /// 
    /// Loaded from AllData.as <mat> entries that have <main>/<border>/<floor> children.
    /// Used by the tile rendering system to composite tile visuals.
    /// 
    /// AS3 equivalent: fe.graph.Material
    /// </summary>
    [Serializable]
    public class MaterialRenderEntry
    {
        [Tooltip("Material ID (matches TileForm.front or TileForm.back)")]
        public string id;

        [Tooltip("Display name (from @n attribute, e.g., 'Сталь', 'Бетон')")]
        public string displayName;

        [Tooltip("ed type: 1=front wall, 2=background")]
        public int ed;

        [Tooltip("Render behind player (ed=2 or rear=1)")]
        public bool isRear;

        [Header("Main Layer")]
        [Tooltip("Tiling texture name from texture.swf (e.g., 'tMetal', 'tConcrete')")]
        public string mainTexture = "";

        [Tooltip("Mask class name (e.g., 'maskStoneBorder', 'maskBare', 'TileMask')")]
        public string mainMask = "TileMask";

        [Tooltip("Alternate texture for stable/home rooms")]
        public string altTexture = "";

        [Header("Border Layer")]
        [Tooltip("Border/edge texture name (e.g., 'tKlep', 'tBorder')")]
        public string borderTexture = "";

        [Tooltip("Border mask class name (e.g., 'BorderMask', 'maskBorderBare')")]
        public string borderMask = "";

        [Header("Floor Layer")]
        [Tooltip("Floor surface texture name (e.g., 'tFloor')")]
        public string floorTexture = "";

        [Tooltip("Floor mask class name (e.g., 'FloorMask', 'maskFloor')")]
        public string floorMask = "";

        [Header("Visual Filter")]
        [Tooltip("Filter preset name (e.g., 'cont', 'cont_metal', 'potek', 'shad')")]
        public string filterType = "";

        /// <summary>Has a main texture layer.</summary>
        public bool HasMain => !string.IsNullOrEmpty(mainTexture);

        /// <summary>Has a border decoration layer.</summary>
        public bool HasBorder => !string.IsNullOrEmpty(borderTexture);

        /// <summary>Has a floor surface layer.</summary>
        public bool HasFloor => !string.IsNullOrEmpty(floorTexture);

        public override string ToString()
        {
            return $"MaterialRender({id}: tex={mainTexture}, mask={mainMask}, border={borderTexture}, floor={floorTexture})";
        }
    }

    /// <summary>
    /// Database of all material rendering data.
    /// Created by MaterialDataImporter from AllData.as <mat> entries.
    /// 
    /// At runtime, the tile renderer looks up: materialId -> MaterialRenderEntry
    /// to know which textures and masks to use for each tile type.
    /// </summary>
    [CreateAssetMenu(fileName = "MaterialRenderDatabase", menuName = "PFE/Map/Material Render Database")]
    public class MaterialRenderDatabase : ScriptableObject
    {
        [SerializeField] private List<MaterialRenderEntry> _frontMaterials = new List<MaterialRenderEntry>();
        [SerializeField] private List<MaterialRenderEntry> _backMaterials = new List<MaterialRenderEntry>();

        [NonSerialized] private Dictionary<string, MaterialRenderEntry> _frontLookup;
        [NonSerialized] private Dictionary<string, MaterialRenderEntry> _backLookup;
        [NonSerialized] private bool _initialized;

        public int FrontCount => _frontMaterials.Count;
        public int BackCount => _backMaterials.Count;

        public void Initialize()
        {
            if (_initialized) return;

            _frontLookup = new Dictionary<string, MaterialRenderEntry>();
            foreach (var entry in _frontMaterials)
            {
                if (!string.IsNullOrEmpty(entry.id))
                    _frontLookup[entry.id] = entry;
            }

            _backLookup = new Dictionary<string, MaterialRenderEntry>();
            foreach (var entry in _backMaterials)
            {
                if (!string.IsNullOrEmpty(entry.id))
                    _backLookup[entry.id] = entry;
            }

            _initialized = true;
        }

        /// <summary>
        /// Get front material render data (for wall surfaces).
        /// </summary>
        public MaterialRenderEntry GetFrontMaterial(string id)
        {
            if (!_initialized) Initialize();
            _frontLookup.TryGetValue(id, out var entry);
            return entry;
        }

        /// <summary>
        /// Get back material render data (for background textures).
        /// </summary>
        public MaterialRenderEntry GetBackMaterial(string id)
        {
            if (!_initialized) Initialize();
            _backLookup.TryGetValue(id, out var entry);
            return entry;
        }

        /// <summary>Get all front material IDs.</summary>
        public List<string> GetFrontIds()
        {
            var ids = new List<string>();
            foreach (var e in _frontMaterials) ids.Add(e.id);
            return ids;
        }

        /// <summary>Get all back material IDs.</summary>
        public List<string> GetBackIds()
        {
            var ids = new List<string>();
            foreach (var e in _backMaterials) ids.Add(e.id);
            return ids;
        }

        /// <summary>Get all unique texture names referenced by any material.</summary>
        public HashSet<string> GetAllTextureNames()
        {
            var names = new HashSet<string>();
            foreach (var list in new[] { _frontMaterials, _backMaterials })
            {
                foreach (var e in list)
                {
                    if (!string.IsNullOrEmpty(e.mainTexture)) names.Add(e.mainTexture);
                    if (!string.IsNullOrEmpty(e.altTexture)) names.Add(e.altTexture);
                    if (!string.IsNullOrEmpty(e.borderTexture)) names.Add(e.borderTexture);
                    if (!string.IsNullOrEmpty(e.floorTexture)) names.Add(e.floorTexture);
                }
            }
            return names;
        }

        /// <summary>Get all unique mask class names referenced by any material.</summary>
        public HashSet<string> GetAllMaskNames()
        {
            var names = new HashSet<string>();
            foreach (var list in new[] { _frontMaterials, _backMaterials })
            {
                foreach (var e in list)
                {
                    if (!string.IsNullOrEmpty(e.mainMask)) names.Add(e.mainMask);
                    if (!string.IsNullOrEmpty(e.borderMask)) names.Add(e.borderMask);
                    if (!string.IsNullOrEmpty(e.floorMask)) names.Add(e.floorMask);
                }
            }
            return names;
        }

        // --- Editor population ---

        public void Clear()
        {
            _frontMaterials.Clear();
            _backMaterials.Clear();
            _initialized = false;
        }

        public void AddFrontMaterial(MaterialRenderEntry entry)
        {
            _frontMaterials.Add(entry);
            _initialized = false;
        }

        public void AddBackMaterial(MaterialRenderEntry entry)
        {
            _backMaterials.Add(entry);
            _initialized = false;
        }

        private void OnEnable()
        {
            _initialized = false;
        }
    }
}