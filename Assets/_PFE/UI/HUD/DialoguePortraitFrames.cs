using System;
using System.Collections.Generic;

namespace PFE.UI.HUD
{
    /// <summary>
    /// The dialogue portrait's frame table — AS3 <c>dial.portret</c> (SWF sprite <b>4638</b>) and the
    /// <c>@p</c> label the localisation table uses to pick one.
    ///
    /// <para><b>What the Oracle actually does.</b> <c>GUI.dialText</c> hides the information panel,
    /// calls <c>dial.portret.gotoAndStop(1)</c> and then, only if the row carries a <c>@p</c>,
    /// <c>dial.portret.gotoAndStop(sp)</c> inside a <c>try</c> whose <c>catch</c> falls back to frame 1
    /// (<c>GUI.as:1831-1851</c>). Two consequences the port must reproduce exactly:</para>
    /// <list type="bullet">
    /// <item><b>Frame 1 is the default, and it is blank.</b> A row with no <c>@p</c> — unattributed
    /// narration — shows an <i>empty</i> portrait slot, not the previous speaker. The exported frame 1
    /// is fully transparent (measured: 0 opaque pixels of 389x325), which is what makes the Oracle's
    /// unconditional <c>gotoAndStop(1)</c> a reset rather than a leftover.</item>
    /// <item><b>An unknown label is not an error.</b> <c>gotoAndStop</c> throws on a label the clip does
    /// not have, so the Oracle's <c>catch</c> turns a typo into the blank frame. The port's equivalent is
    /// a failed lookup, which is why <see cref="TryGetIndex"/> returning false is a <i>supported</i>
    /// outcome and not a bug to be reported.</item>
    /// </list>
    ///
    /// <para><b>The table is transcribed from the SWF, not from the specification.</b> Sprite 4638
    /// carries 70 frames and 60 <c>FrameLabel</c> tags; the labels and their frame numbers were read out
    /// of the tag stream directly (<c>DefineSprite_4638</c>: frame 2 = <c>lp1</c> … frame 62 =
    /// <c>men2</c>). Frames 1, 8 and 63-70 are unlabelled — 1 is the blank default, 8 sits between
    /// <c>lp6</c> and <c>lpa</c>, and the tail is the clip's own animation frames.</para>
    ///
    /// <para><b>Index = frame - 1</b>, matching the importer, which copies the clip to
    /// <c>f001.png</c>…<c>f070.png</c> in frame order and is the convention every other catalog in
    /// <see cref="PFE.Data.Definitions.PipBuckCompositionDefinition"/> uses.</para>
    /// </summary>
    public static class DialoguePortraitFrames
    {
        /// <summary>The clip's frame count — <c>DefineSprite_4638</c> holds 70 frames.</summary>
        public const int FrameCount = 70;

        /// <summary>
        /// The label of each frame, indexed by <b>frame - 1</b>; <c>null</c> where the clip has no
        /// <c>FrameLabel</c>. The lookup dictionary below is built from this array so the two cannot
        /// drift apart — a second, parallel table is exactly how a frame number ends up disagreeing with
        /// its own label.
        /// </summary>
        private static readonly string[] LabelsByFrameIndex =
        {
            /*   1 */ null,     // the blank default the Oracle resets to
            /*   2 */ "lp1",
            /*   3 */ "lp2",
            /*   4 */ "lp3",
            /*   5 */ "lp4",
            /*   6 */ "lp5",
            /*   7 */ "lp6",
            /*   8 */ null,     // unlabelled
            /*   9 */ "lpa",    // the alicorn override target — see Resolve
            /*  10 */ "ve",
            /*  11 */ "vw",
            /*  12 */ "va",
            /*  13 */ "vd",
            /*  14 */ "vm",
            /*  15 */ "vs",
            /*  16 */ "vb",
            /*  17 */ "vc",
            /*  18 */ "zetr",
            /*  19 */ "vr",
            /*  20 */ "vf",
            /*  21 */ "vel",
            /*  22 */ "calam",
            /*  23 */ "sh",
            /*  24 */ "main",
            /*  25 */ "pip",
            /*  26 */ "mtn",
            /*  27 */ "term",
            /*  28 */ "raider",
            /*  29 */ "capt",
            /*  30 */ "capt2",
            /*  31 */ "hh",
            /*  32 */ "raider2",
            /*  33 */ "slaver1",
            /*  34 */ "slaver2",
            /*  35 */ "slaver3",
            /*  36 */ "phoenix",
            /*  37 */ "spectre",
            /*  38 */ "ran1",
            /*  39 */ "ran2",
            /*  40 */ "al1",
            /*  41 */ "al2",
            /*  42 */ "al3",
            /*  43 */ "bm",
            /*  44 */ "ze1",
            /*  45 */ "ze2",
            /*  46 */ "mch",
            /*  47 */ "cs",
            /*  48 */ "zira",
            /*  49 */ "wat",
            /*  50 */ "zeb1",
            /*  51 */ "zeb2",
            /*  52 */ "zeb3",
            /*  53 */ "zeb4",
            /*  54 */ "enc1",
            /*  55 */ "enc2",
            /*  56 */ "com",
            /*  57 */ "men",
            /*  58 */ "sc1",
            /*  59 */ "sc2",
            /*  60 */ "sc3",
            /*  61 */ "sc4",
            /*  62 */ "men2",
            /*  63 */ null,
            /*  64 */ null,
            /*  65 */ null,
            /*  66 */ null,
            /*  67 */ null,
            /*  68 */ null,
            /*  69 */ null,
            /*  70 */ null,
        };

        /// <summary>The label of a frame, or <c>null</c> for an unlabelled one. Index = frame - 1.</summary>
        public static string LabelAt(int frameIndex)
        {
            if (frameIndex < 0 || frameIndex >= LabelsByFrameIndex.Length) return null;
            return LabelsByFrameIndex[frameIndex];
        }

        /// <summary>How many of the clip's frames carry a label — 60 of 70.</summary>
        public static int LabelledFrameCount
        {
            get
            {
                int n = 0;
                foreach (string label in LabelsByFrameIndex)
                {
                    if (label != null) n++;
                }
                return n;
            }
        }

        private static readonly Dictionary<string, int> LabelToIndex = BuildLabelToIndex();

        private static Dictionary<string, int> BuildLabelToIndex()
        {
            var map = new Dictionary<string, int>(LabelsByFrameIndex.Length, StringComparer.Ordinal);
            for (int i = 0; i < LabelsByFrameIndex.Length; i++)
            {
                string label = LabelsByFrameIndex[i];
                if (label != null) map[label] = i;
            }
            return map;
        }

        /// <summary>
        /// The <c>@p</c> label the Oracle would actually pass to <c>gotoAndStop</c> — i.e.
        /// <paramref name="label"/> after the alicorn substitution.
        ///
        /// <para><c>GUI.as:1838-1842</c>: if the label starts with <c>"lp"</c> and
        /// <c>World.w.alicorn</c> is set, it becomes <c>"lpa"</c> and the line is additionally wrapped
        /// in a crimson span. The player's own portrait frames <c>lp1</c>…<c>lp6</c> are therefore
        /// unreachable once the alicorn flag is on, which is why <c>lpa</c> has its own frame.</para>
        /// </summary>
        public static string Resolve(string label, bool alicorn)
        {
            if (string.IsNullOrEmpty(label)) return null;
            if (alicorn && label.StartsWith("lp", StringComparison.Ordinal)) return "lpa";
            return label;
        }

        /// <summary>
        /// Resolves an <c>@p</c> label to the catalog index (frame - 1) of the portrait to draw.
        ///
        /// <para><b>Numeric labels are accepted</b> because Flash's <c>gotoAndStop</c> takes either a
        /// label or a frame number and the Oracle passes <c>@p</c> through unexamined
        /// (<c>GUI.as:1845</c>). A number outside <c>1..<see cref="FrameCount"/></c> is out of range and
        /// behaves like an unknown label — Flash throws on it and the Oracle's <c>catch</c> lands on
        /// frame 1.</para>
        /// </summary>
        public static bool TryGetIndex(string label, out int frameIndex)
        {
            frameIndex = -1;
            if (string.IsNullOrEmpty(label)) return false;

            if (LabelToIndex.TryGetValue(label, out int index))
            {
                frameIndex = index;
                return true;
            }

            if (IsAllDigits(label) &&
                int.TryParse(label, System.Globalization.NumberStyles.None,
                             System.Globalization.CultureInfo.InvariantCulture, out int frame) &&
                frame >= 1 && frame <= FrameCount)
            {
                frameIndex = frame - 1;
                return true;
            }

            return false;
        }

        /// <summary>
        /// The full Oracle path: apply the alicorn substitution, then look the label up.
        /// <c>false</c> means "draw the blank default", which is a normal outcome.
        /// </summary>
        public static bool TryGetIndexFor(string label, bool alicorn, out int frameIndex)
        {
            return TryGetIndex(Resolve(label, alicorn), out frameIndex);
        }

        private static bool IsAllDigits(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] < '0' || s[i] > '9') return false;
            }
            return s.Length > 0;
        }
    }
}
