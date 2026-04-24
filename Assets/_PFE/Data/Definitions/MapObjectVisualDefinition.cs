using UnityEngine;
using System.Collections.Generic;

namespace PFE.Data.Definitions
{
    /// <summary>
    /// Imported visual data for one shared map object presentation.
    /// Keeps art and presentation metadata separate from gameplay behavior.
    /// </summary>
    [CreateAssetMenu(fileName = "MapObjectVisual", menuName = "PFE/Map/Map Object Visual Definition")]
    public class MapObjectVisualDefinition : ScriptableObject
    {
        [Header("Identification")]
        [Tooltip("Stable visual id used by definitions and tooling.")]
        public string visualId;
        [Tooltip("Primary object id this visual was imported for.")]
        public string objectId;
        [Tooltip("All object ids currently linked to this shared visual asset.")]
        public List<string> linkedObjectIds = new List<string>();
        [Tooltip("Original export folder name used to import this visual.")]
        public string sourceFolderName;
        [Tooltip("Original SWF symbol id if it could be inferred from the export folder.")]
        public int sourceSymbolId;

        [Header("Sprites")]
        [Tooltip("Imported frames in export order.")]
        public Sprite[] frames;
        [Tooltip("Pixels per unit used during import.")]
        public int pixelsPerUnit = 100;
        [Tooltip("Pixel size of the first frame.")]
        public Vector2Int pixelSize;

        [Header("Placement")]
        [Tooltip("Normalized sprite pivot intended for gameplay presentation.")]
        public Vector2 pivot = new Vector2(0.5f, 0f);
        [Tooltip("Optional local offset for presenter alignment.")]
        public Vector2 localOffset = Vector2.zero;
        [Tooltip("Sorting order hint for presenter renderers.")]
        public int sortingOrder;
        [Tooltip("Helpful hint for wall cabinets, terminals, and similar objects.")]
        public bool wallMounted;

        public bool HasFrames => frames != null && frames.Length > 0;
        public bool HasAnimation => frames != null && frames.Length > 1;
        public Sprite FirstFrame => HasFrames ? frames[0] : null;
    }
}
