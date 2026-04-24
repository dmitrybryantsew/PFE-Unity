#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;

namespace PFE.Editor.Importers.SWF
{
    /// <summary>
    /// Top-level container for all data extracted from a SWF file.
    /// Pure data — no Unity asset references, no SWF binary parsing logic.
    /// </summary>
    public class SWFFile
    {
        public int Version;
        public int FileLength;
        public Rect StageRect;
        public float FrameRate;
        public int FrameCount;

        /// <summary>All DefineSprite symbols, keyed by symbol ID.</summary>
        public Dictionary<int, SWFSymbol> Symbols = new();

        /// <summary>All DefineShape bounds, keyed by shape ID.</summary>
        public Dictionary<int, Rect> ShapeBounds = new();

        /// <summary>
        /// Per-frame-1 bounds for DefineSprite symbols.
        /// Unlike Symbol.Bounds (which unions ALL frames), these are computed from
        /// frame 1 placements only — matching what JPEXS exports as the first PNG.
        /// Used for accurate pivot computation when the sprite has multiple frames.
        /// </summary>
        public Dictionary<int, Rect> Frame1Bounds = new();
    }

    /// <summary>
    /// A DefineSprite symbol — a reusable MovieClip with its own timeline.
    /// </summary>
    public class SWFSymbol
    {
        public int SymbolId;
        public int FrameCount;

        /// <summary>Frames in timeline order. Each frame is a snapshot of the display list.</summary>
        public List<SWFFrame> Frames = new();

        /// <summary>Bounds of this symbol (from DefineShape if it's a shape, or computed from placements).</summary>
        public Rect Bounds;
    }

    /// <summary>
    /// A single frame in a symbol's timeline.
    /// </summary>
    public class SWFFrame
    {
        /// <summary>1-based frame number.</summary>
        public int FrameNumber;

        /// <summary>Frame label (e.g. "stay", "run", "pip"). Null if no label on this frame.</summary>
        public string Label;

        /// <summary>All display list entries active on this frame, ordered by depth (back to front).</summary>
        public List<SWFPlacement> Placements = new();
    }

    /// <summary>
    /// A single object placement on the display list — one PlaceObject2/3 entry.
    /// Represents where a child symbol is drawn on a given frame.
    /// </summary>
    public class SWFPlacement
    {
        /// <summary>Display list depth (determines draw order).</summary>
        public int Depth;

        /// <summary>The symbol ID being placed (references SWFFile.Symbols or ShapeBounds).</summary>
        public int CharacterId;

        /// <summary>Instance name (e.g. "body", "head", "mane"). Null if unnamed.</summary>
        public string InstanceName;

        /// <summary>Position relative to parent symbol's registration point, in Flash coordinates (Y-down).</summary>
        public Vector2 Position;

        /// <summary>Rotation in degrees (Flash convention).</summary>
        public float Rotation;

        /// <summary>Scale factors.</summary>
        public Vector2 Scale = Vector2.one;

        /// <summary>Color transform — multiply component (r, g, b, a).</summary>
        public Color ColorMultiply = Color.white;

        /// <summary>Color transform — additive component (r, g, b, a) in 0-255 range.</summary>
        public Color ColorAdd = new(0, 0, 0, 0);

        /// <summary>Whether this placement has a color transform.</summary>
        public bool HasColorTransform;
    }

    /// <summary>
    /// Mapping from AS3 animation state labels to Unity locomotion states.
    /// </summary>
    public static class AS3StateMapping
    {
        /// <summary>
        /// Maps AS3 root osn frame labels to their symbol IDs.
        /// Populated during SWF parse of symbol3679 (the osn root).
        /// </summary>
        public static readonly Dictionary<string, int> StateLabels = new()
        {
            { "stay", 3651 },
            { "trot", 3652 },
            { "trot_up", 3653 },
            { "trot_down", 3654 },
            { "run", 3655 },
            { "polz", 3656 },
            { "walk", 3657 },
            { "jump", 3658 },
            { "sitjump", 3659 },
            { "laz", 3660 },
            { "plav", 3661 },
            { "punch", 3664 },
            { "kick", 3665 },
            { "die", 3666 },
            { "dieali", 3667 },
            { "derg", 3668 },
            { "res", 3669 },
            { "lurk1", 3670 },
            { "lurk2", 3671 },
            { "lurk3", 3672 },
            { "pinok", 3673 },
            { "levit", 3674 },
            { "free1", 3675 },
            { "free2", 3676 },
            { "free3", 3677 },
            { "roll", 3678 },
        };

        /// <summary>Known body part symbol IDs and their names.</summary>
        public static readonly Dictionary<int, string> BodyPartNames = new()
        {
            { 3640, "lwing" },
            { 3650, "rwing" },
            { 24, "sleg3a" },       // hind leg lower back variant A
            { 319, "sleg3" },       // hind leg lower back variant B
            { 55, "sleg1" },        // hind leg upper
            { 78, "fleg1" },        // front leg segment 1
            { 101, "fleg2" },       // front leg segment 2
            { 124, "fleg3" },       // front hoof
            { 297, "fleg3a" },      // front hoof variant
            { 513, "pip" },         // pip marker
            { 136, "tail" },        // idle tail
            { 692, "tail_run" },    // run tail
            { 159, "korpus" },      // torso
            { 182, "neck" },        // neck
            { 195, "mane" },        // idle mane
            { 734, "mane_run" },    // run mane
            { 274, "head" },        // head container
        };

        /// <summary>Armor label order as confirmed from SWF. Frame 1 = no armor, frame 2+ = armor IDs.</summary>
        public static readonly string[] ArmorLabelsForKorpus =
        {
            "none",      // frame 1
            "pip",       // frame 2
            "tre",
            "chitin",
            "kombu",
            "skin",
            "metal",
            "assault",
            "battle",
            "magus",
            "antirad",
            "antihim",
            "intel",
            "astealth",
            "moon",
            "sapper",
            "power",
            "polic",
            "spec",
            "encl",
            "ali",
        };

        /// <summary>
        /// Body part tint categories from Obj.setColor() in AS3.
        /// 0=fur, 1=hair, 2=hair2, 3=eye, 4=magic
        /// </summary>
        public enum TintCategory
        {
            Fur = 0,
            Hair = 1,
            Hair2 = 2,
            Eye = 3,
            Magic = 4,
            None = -1,
        }

        /// <summary>Maps body part instance names to their tint category.</summary>
        public static readonly Dictionary<string, TintCategory> PartTintCategories = new()
        {
            { "korpus", TintCategory.Fur },
            { "neck", TintCategory.Fur },
            { "head", TintCategory.Fur },
            { "morda", TintCategory.Fur },
            { "fleg1", TintCategory.Fur },
            { "fleg2", TintCategory.Fur },
            { "fleg3", TintCategory.Fur },
            { "fleg3a", TintCategory.Fur },
            { "sleg1", TintCategory.Fur },
            { "sleg3", TintCategory.Fur },
            { "sleg3a", TintCategory.Fur },
            { "mane", TintCategory.Hair },
            { "mane_run", TintCategory.Hair },
            { "tail", TintCategory.Hair },
            { "tail_run", TintCategory.Hair },
            { "hair", TintCategory.Hair },
            { "eye", TintCategory.Eye },
            { "magic", TintCategory.Magic },
            { "horn", TintCategory.Fur },
            { "lwing", TintCategory.Fur },
            { "rwing", TintCategory.Fur },
            { "helm", TintCategory.None },
            { "morda_armor", TintCategory.None },
            { "morda_base", TintCategory.Fur },
            { "morda_overlay", TintCategory.Fur },
            { "forelock", TintCategory.Hair },
            { "konec", TintCategory.None },
            { "pip", TintCategory.None },
        };

        /// <summary>Known armor-aware body part symbol IDs.</summary>
        public static readonly HashSet<int> ArmorAwarePartIds = new()
        {
            159,  // korpus (torso)
            182,  // neck
            273,  // morda_armor (face armor overlay)
            31,   // helm (helmet overlay)
            78,   // fleg1
            101,  // fleg2
            124,  // fleg3
            297,  // fleg3a
            55,   // sleg1
            319,  // sleg3
            24,   // sleg3a
        };

        /// <summary>
        /// The player root visualPlayer symbol ID.
        /// </summary>
        public const int VisualPlayerSymbolId = 3690;

        /// <summary>
        /// The gameplay osn root symbol ID (child of visualPlayer).
        /// </summary>
        public const int OsnRootSymbolId = 3679;
    }
}
#endif
