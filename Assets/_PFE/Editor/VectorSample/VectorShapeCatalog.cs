#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace PFE.Editor.VectorSample
{
    /// <summary>
    /// How much of a shape SVG is genuinely vector data.
    ///
    /// <para>Measured over the whole export on 2026-10-05: of the 2563 files in
    /// <c>pfe/scripts/_assets/shapes</c>, <b>2114 (82.5 %)</b> are
    /// <see cref="VectorOnly"/>, <b>449 (17.5 %)</b> are <see cref="PatternFilled"/>, and <b>none</b>
    /// are bitmap-only. This contradicts
    /// <c>docs/MainMenu/HowBackgroundAnimationWasImplementedAndWhatDone.md</c>, which states that FFDec
    /// "exports shapes as SVG files containing embedded base64 PNGs" — that is true only of the 449
    /// bitmap-<i>filled</i> shapes.</para>
    /// </summary>
    public enum VectorShapeKind
    {
        /// <summary>No <c>&lt;path&gt;</c> and no embedded bitmap — nothing drawable in the file.</summary>
        Empty,

        /// <summary>Real <c>&lt;path&gt;</c> data and no embedded bitmap.</summary>
        VectorOnly,

        /// <summary>
        /// Path data plus a bitmap fill: FFDec emits a <c>&lt;pattern&gt;</c> wrapping an
        /// <c>&lt;image xlink:href="data:image/PNG;base64,…"&gt;</c>. Note the <b>uppercase</b>
        /// <c>PNG</c> in all 457 occurrences, while Unity's parser string table knows only lowercase
        /// <c>image/png</c> / <c>image/jpeg</c> — so this class is the one to watch for a silently
        /// missing fill.
        /// </summary>
        PatternFilled,
    }

    /// <summary>One shape SVG on disk, with everything the sample window needs to describe it.</summary>
    public sealed class VectorShapeEntry
    {
        /// <summary>The <c>DefineShape</c> character id parsed from the filename.</summary>
        public int ShapeId;

        public string FileName;
        public string FullPath;
        public long Bytes;

        public VectorShapeKind Kind;

        public int PathCount;
        public int RadialGradientCount;
        public int LinearGradientCount;
        public int PatternCount;
        public int EmbeddedImageCount;

        /// <summary>Root <c>&lt;svg&gt;</c> width/height in px. 0 when the attribute was unreadable.</summary>
        public float RootWidth;
        public float RootHeight;

        /// <summary>Set when the file could not be read or classified. Never silently swallowed.</summary>
        public string Error;

        /// <summary>Short label for the list. Deliberately the raw id, not a guess at a name.</summary>
        public string DisplayName => "sym" + ShapeId.ToString(CultureInfo.InvariantCulture);

        public string KindLabel
        {
            get
            {
                switch (Kind)
                {
                    case VectorShapeKind.VectorOnly: return "vector";
                    case VectorShapeKind.PatternFilled: return "pattern+bitmap";
                    default: return "empty";
                }
            }
        }

        public string SizeLabel
        {
            get
            {
                if (RootWidth > 0f && RootHeight > 0f)
                    return string.Format(CultureInfo.InvariantCulture, "{0:0.#}x{1:0.#}", RootWidth, RootHeight);
                return "?x?";
            }
        }
    }

    /// <summary>
    /// Reads the FFDec shape export. Pure filesystem + string work — no Unity types — so the parsing
    /// rules below are the only thing that can be wrong, and they can be checked against the tree with
    /// a plain <c>grep</c>.
    /// </summary>
    public static class VectorShapeCatalog
    {
        public const string SvgExtension = ".svg";

        /// <summary>
        /// Enumerates every <c>.svg</c> directly under <paramref name="shapesRoot"/>, classifies it, and
        /// returns the list sorted by shape id. <paramref name="error"/> is set (and the list may still be
        /// non-empty) when the root itself is unusable, so the caller can distinguish "no root" from
        /// "root present but empty".
        /// </summary>
        public static List<VectorShapeEntry> Scan(string shapesRoot, out string error)
        {
            error = null;
            var result = new List<VectorShapeEntry>();

            if (string.IsNullOrWhiteSpace(shapesRoot))
            {
                error = "No shapes root resolved. Set PFE/Data/Import Source Root… first.";
                return result;
            }

            if (!Directory.Exists(shapesRoot))
            {
                error = "Shapes root does not exist:\n" + shapesRoot;
                return result;
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(shapesRoot, "*" + SvgExtension, SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex)
            {
                error = "Could not enumerate the shapes root: " + ex.GetType().Name + ": " + ex.Message;
                return result;
            }

            foreach (string path in files)
            {
                var entry = ReadOne(path);
                result.Add(entry);
            }

            result.Sort((a, b) => a.ShapeId.CompareTo(b.ShapeId));
            return result;
        }

        /// <summary>Reads and classifies a single SVG. Never throws — a failure lands in <see cref="VectorShapeEntry.Error"/>.</summary>
        public static VectorShapeEntry ReadOne(string path)
        {
            var entry = new VectorShapeEntry
            {
                FullPath = path,
                FileName = Path.GetFileName(path),
                Kind = VectorShapeKind.Empty,
            };

            if (!TryParseShapeId(entry.FileName, out entry.ShapeId))
                entry.Error = "Filename does not start with a shape id: " + entry.FileName;

            string text;
            try
            {
                var info = new FileInfo(path);
                entry.Bytes = info.Exists ? info.Length : 0;
                text = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                entry.Error = "Unreadable: " + ex.GetType().Name + ": " + ex.Message;
                return entry;
            }

            entry.PathCount = CountOccurrences(text, "<path");
            entry.RadialGradientCount = CountOccurrences(text, "<radialGradient");
            entry.LinearGradientCount = CountOccurrences(text, "<linearGradient");
            entry.PatternCount = CountOccurrences(text, "<pattern");
            entry.EmbeddedImageCount = CountOccurrences(text, "data:image/");
            entry.Kind = Classify(entry.PathCount, entry.EmbeddedImageCount);

            float w, h;
            if (TryReadRootSize(text, out w, out h))
            {
                entry.RootWidth = w;
                entry.RootHeight = h;
            }

            return entry;
        }

        /// <summary>
        /// <c>{shapeId}_symbol{shapeId}.svg</c> in <c>shapes/</c>; <c>{shapeId}.svg</c> in
        /// <c>morphshapes/</c>. Both parse here — the id is the leading run of digits before the first
        /// underscore, or the whole stem when there is none.
        /// </summary>
        public static bool TryParseShapeId(string fileName, out int shapeId)
        {
            shapeId = 0;
            if (string.IsNullOrEmpty(fileName)) return false;
            if (!fileName.EndsWith(SvgExtension, StringComparison.OrdinalIgnoreCase)) return false;

            string stem = fileName.Substring(0, fileName.Length - SvgExtension.Length);
            int underscore = stem.IndexOf('_');
            string head = underscore >= 0 ? stem.Substring(0, underscore) : stem;

            // NumberStyles.None: no sign, no whitespace, no thousands separator — "159x" must fail
            // rather than parse as 159.
            return int.TryParse(head, NumberStyles.None, CultureInfo.InvariantCulture, out shapeId);
        }

        /// <summary>
        /// Reads <c>width</c>/<c>height</c> off the root <c>&lt;svg&gt;</c> tag. FFDec writes them as
        /// <c>"135.0px"</c>. Used to hand <c>SVGParser.ImportSVG</c> a window that matches the document,
        /// so <c>ViewportOptions.PreserveViewport</c> has something truthful to preserve.
        /// </summary>
        public static bool TryReadRootSize(string svgText, out float width, out float height)
        {
            width = 0f;
            height = 0f;
            if (string.IsNullOrEmpty(svgText)) return false;

            int svg = svgText.IndexOf("<svg", StringComparison.Ordinal);
            if (svg < 0) return false;
            int close = svgText.IndexOf('>', svg);
            if (close < 0) return false;

            string tag = svgText.Substring(svg, close - svg + 1);
            width = ReadPxAttribute(tag, "width");
            height = ReadPxAttribute(tag, "height");
            return width > 0f && height > 0f;
        }

        /// <summary>
        /// A bitmap fill wins over path presence: a <see cref="VectorShapeKind.PatternFilled"/> file also
        /// has paths, so testing paths first would mislabel all 449 of them as pure vector.
        /// </summary>
        static VectorShapeKind Classify(int pathCount, int embeddedImageCount)
        {
            if (embeddedImageCount > 0) return VectorShapeKind.PatternFilled;
            if (pathCount > 0) return VectorShapeKind.VectorOnly;
            return VectorShapeKind.Empty;
        }

        static float ReadPxAttribute(string tag, string name)
        {
            string needle = name + "=\"";
            int at = tag.IndexOf(needle, StringComparison.Ordinal);
            if (at < 0) return 0f;

            int start = at + needle.Length;
            int end = tag.IndexOf('"', start);
            if (end < 0) return 0f;

            string raw = tag.Substring(start, end - start).Trim();
            if (raw.EndsWith("px", StringComparison.OrdinalIgnoreCase))
                raw = raw.Substring(0, raw.Length - 2).Trim();

            float value;
            return float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : 0f;
        }

        static int CountOccurrences(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle)) return 0;
            int count = 0;
            int at = 0;
            while (true)
            {
                at = haystack.IndexOf(needle, at, StringComparison.Ordinal);
                if (at < 0) return count;
                count++;
                at += needle.Length;
            }
        }
    }
}
#endif
