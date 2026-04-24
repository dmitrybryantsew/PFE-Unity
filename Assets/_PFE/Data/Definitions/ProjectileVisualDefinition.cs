using UnityEngine;

namespace PFE.Data.Definitions
{
    /// <summary>
    /// Imported projectile art keyed by the original AllData.as vis.@vbul value.
    /// Lets us keep projectile behavior prefabs separate from projectile visuals.
    /// </summary>
    [CreateAssetMenu(fileName = "ProjectileVisual", menuName = "PFE/Projectile Visual Definition")]
    public class ProjectileVisualDefinition : ScriptableObject
    {
        [Header("Identification")]
        [Tooltip("Logical projectile visual ID. Usually matches WeaponDefinition.vbul.")]
        public string visualId;
        [Tooltip("Original AS3 class name, e.g. visbulplasma.")]
        public string sourceClassName;
        [Tooltip("Original SWF symbol ID from the AS3 Embed metadata.")]
        public int sourceSymbolId;

        [Header("Defaults")]
        [Tooltip("Most likely behavior archetype for this projectile art.")]
        public ProjectileArchetype recommendedArchetype;

        [Header("Frames")]
        [Tooltip("Imported sprite frames in playback order.")]
        public Sprite[] frames;
        [Tooltip("Original Flash playback rate.")]
        public float frameRate = 30f;
        [Tooltip("Whether the flight frame sequence loops. Impact frames always play once.")]
        public bool loop = true;

        [Header("Impact Animation")]
        [Tooltip("Number of frames at the START of frames[] used for the in-flight loop.\n" +
                 "0 = all frames are one animation, no separate impact sequence.\n" +
                 "> 0 = frames[0..flightFrameCount-1] loop during flight;\n" +
                 "      frames[flightFrameCount..] play once on impact, then the projectile returns to pool.\n" +
                 "Example: 4 frames total, flightFrameCount=1 → frame 0 loops in flight, frames 1-3 play on hit.")]
        public int flightFrameCount = 0;

        [Header("Runtime Offsets")]
        [Tooltip("Local offset applied to the projectile visual child transform.")]
        public Vector3 localOffset = Vector3.zero;
        [Tooltip("Local Z rotation in degrees for the projectile visual child.")]
        public float localRotation;
        [Tooltip("Local scale applied to the projectile visual child.")]
        public Vector3 localScale = Vector3.one;
        [Tooltip("Optional color tint applied to the projectile SpriteRenderer.")]
        public Color colorTint = Color.white;
        [Tooltip("Sorting order override for the projectile SpriteRenderer.")]
        public int sortingOrder;

        public bool HasFrames => frames != null && frames.Length > 0;
        public bool HasAnimation => frames != null && frames.Length > 1;
        public Sprite FirstFrame => HasFrames ? frames[0] : null;
    }
}
