using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PFE.Editor.VectorSample
{
    /// <summary>What a placement's <c>characterId</c> resolved to in the source SWF.</summary>
    public enum PlacementKind
    {
        /// <summary>A <c>DefineShape</c> — has geometry, and an SVG on disk.</summary>
        Shape = 0,

        /// <summary>A <c>DefineSprite</c> — recurse into it.</summary>
        Sprite = 1,

        /// <summary>
        /// Text / button / morph-shape. No SVG geometry, so it can never be drawn — the capture only
        /// carries shapes. Reported through <see cref="VectorCaptureData.ResolveFrame"/>'s
        /// <c>skippedNonShape</c> collector rather than dropped, because a silent skip makes a
        /// text-bearing sprite look *broken* instead of *partially reconstructed*.
        /// </summary>
        Other = 2,
    }

    /// <summary>One entry of a frame's display list: what to draw, and where.</summary>
    public sealed class VectorPlacement
    {
        /// <summary>Draw order. Lower depth is drawn first (behind).</summary>
        public int Depth;

        public int CharacterId;
        public PlacementKind Kind;

        /// <summary>Translation in <b>twips</b> — 20 twips = 1 pixel. Raw, as stored.</summary>
        public float Tx, Ty;

        public float Sx, Sy;

        /// <summary>Rotation/skew terms of the SWF matrix. Both 0 in the common case.</summary>
        public float R0, R1;

        /// <summary>Index into <see cref="VectorCaptureData.ColourTransforms"/>, or -1.</summary>
        public int XformIndex;
    }

    /// <summary>One frame of a sprite: the resolved display list at that instant.</summary>
    public sealed class VectorFrame
    {
        public VectorPlacement[] Placements;
    }

    public sealed class VectorSpriteInfo
    {
        public int Id;
        public int FrameCount;
        public VectorFrame[] Frames;
    }

    public sealed class VectorShapeInfo
    {
        public int Id;

        /// <summary>SVG file name inside the shapes export, e.g. <c>534_symbol534.svg</c>.</summary>
        public string File;

        /// <summary>True when the SVG was present when the capture ran.</summary>
        public bool OnDisk;

        public bool HasBounds;
        public float X0, Y0, X1, Y1;
    }

    public sealed class VectorCaptureManifest
    {
        public int CaptureVersion;
        public double TwipsPerPixel = 20.0;
        public string SourceSwf;
        public string ShapesDir;

        /// <summary>Totals for the whole capture — <c>shapes</c>, <c>sprites</c>, <c>placementTags</c>, …</summary>
        public readonly Dictionary<string, double> Counts = new Dictionary<string, double>();

        /// <summary>
        /// The capture script's own tally of what it <i>expected</i> to apply vs what it <i>did</i>
        /// apply: <c>matricesExpected</c>/<c>matricesApplied</c>,
        /// <c>colourTransformsExpected</c>/<c>colourTransformsApplied</c>, <c>showFrames</c>.
        ///
        /// <para>Worth surfacing rather than skipping. The two documented traps (a matrix that stays
        /// identity because the <c>&lt;matrix&gt;</c> child line was filtered out, and a scale of 0
        /// because <c>hasScale="false"</c> was taken literally) both show up here as
        /// <c>expected != applied</c> or as an implausible count — so this is the cheapest single
        /// number that says "the capture is not silently empty".</para>
        /// </summary>
        public readonly Dictionary<string, double> SelfCheck = new Dictionary<string, double>();

        /// <summary>
        /// How the 57 135 <c>PlaceObject</c> tags split by what their <c>characterId</c> resolved to:
        /// <c>shape</c>, <c>sprite</c>, <c>other</c>.
        /// </summary>
        public readonly Dictionary<string, double> PlacedKinds = new Dictionary<string, double>();
    }

    /// <summary>
    /// Reader for the one-shot capture written by <c>.workbuddy-ai/tools/capture_vector_sprites.py</c>.
    ///
    /// <para><b>Why this is hand-written rather than <c>JsonUtility</c>.</b> The capture keys
    /// <c>shapes</c> and <c>sprites</c> by id and stores each frame as a bare array of number rows.
    /// <c>JsonUtility</c> handles neither dictionaries nor nested number arrays, and the obvious fix —
    /// re-emitting with named fields — grows the file by ~80 % and, worse, would leave the parsing
    /// unverifiable outside the editor. This reader is plain C# with <b>no UnityEngine dependency</b>,
    /// so it can be compiled and run against the real capture from a shell — which is how it was
    /// checked (see the harness note in the window).</para>
    ///
    /// <para><b>Cost.</b> The real capture is 5.5 MB and holds ~85 000 placement rows. Parsing is a
    /// single forward pass with no intermediate tree, so it allocates roughly one object per
    /// placement and nothing per number.</para>
    /// </summary>
    public static class VectorCaptureReader
    {
        public static VectorCaptureData Parse(string json)
        {
            if (string.IsNullOrEmpty(json))
                throw new ArgumentException("capture JSON is empty", nameof(json));

            var r = new Reader(json);
            var data = new VectorCaptureData
            {
                Manifest = new VectorCaptureManifest(),
                Shapes = new Dictionary<int, VectorShapeInfo>(),
                Sprites = new Dictionary<int, VectorSpriteInfo>(),
                ColourTransforms = new float[0][],
            };

            r.SkipWhitespace();
            r.Expect('{');

            bool first = true;
            while (true)
            {
                r.SkipWhitespace();
                if (r.Peek() == '}') { r.Next(); break; }
                if (!first) r.Expect(',');
                first = false;

                r.SkipWhitespace();
                string key = r.ReadString();
                r.SkipWhitespace();
                r.Expect(':');

                switch (key)
                {
                    case "manifest": data.Manifest = ReadManifest(r); break;
                    case "shapes": data.Shapes = ReadShapes(r); break;
                    case "sprites": data.Sprites = ReadSprites(r); break;
                    case "colourTransforms": data.ColourTransforms = ReadColourTransforms(r); break;
                    default: r.SkipValue(); break;
                }
            }

            return data;
        }

        static VectorCaptureManifest ReadManifest(Reader r)
        {
            var m = new VectorCaptureManifest();
            r.SkipWhitespace();
            r.Expect('{');

            bool first = true;
            while (true)
            {
                r.SkipWhitespace();
                if (r.Peek() == '}') { r.Next(); break; }
                if (!first) r.Expect(',');
                first = false;

                r.SkipWhitespace();
                string key = r.ReadString();
                r.SkipWhitespace();
                r.Expect(':');
                r.SkipWhitespace();

                switch (key)
                {
                    case "captureVersion": m.CaptureVersion = (int)r.ReadNumber(); break;
                    case "twipsPerPx": m.TwipsPerPixel = r.ReadNumber(); break;
                    case "sourceSwf": m.SourceSwf = r.ReadString(); break;
                    case "shapesDir": m.ShapesDir = r.ReadString(); break;
                    case "counts": ReadNumberMap(r, m.Counts); break;
                    case "selfCheck": ReadNumberMap(r, m.SelfCheck); break;
                    case "placedKinds": ReadNumberMap(r, m.PlacedKinds); break;
                    default: r.SkipValue(); break;
                }
            }

            return m;
        }

        /// <summary>Reads a flat <c>{"name": number, …}</c> object. Used for counts, selfCheck and placedKinds.</summary>
        static void ReadNumberMap(Reader r, Dictionary<string, double> into)
        {
            r.SkipWhitespace();
            r.Expect('{');
            bool first = true;
            while (true)
            {
                r.SkipWhitespace();
                if (r.Peek() == '}') { r.Next(); break; }
                if (!first) r.Expect(',');
                first = false;

                r.SkipWhitespace();
                string key = r.ReadString();
                r.SkipWhitespace();
                r.Expect(':');
                r.SkipWhitespace();
                into[key] = r.ReadNumber();
            }
        }

        static Dictionary<int, VectorShapeInfo> ReadShapes(Reader r)
        {
            var map = new Dictionary<int, VectorShapeInfo>();
            r.SkipWhitespace();
            r.Expect('{');

            bool first = true;
            while (true)
            {
                r.SkipWhitespace();
                if (r.Peek() == '}') { r.Next(); break; }
                if (!first) r.Expect(',');
                first = false;

                r.SkipWhitespace();
                int id = int.Parse(r.ReadString(), CultureInfo.InvariantCulture);
                r.SkipWhitespace();
                r.Expect(':');
                r.SkipWhitespace();
                r.Expect('{');

                var info = new VectorShapeInfo { Id = id, File = string.Empty };
                bool innerFirst = true;
                while (true)
                {
                    r.SkipWhitespace();
                    if (r.Peek() == '}') { r.Next(); break; }
                    if (!innerFirst) r.Expect(',');
                    innerFirst = false;

                    r.SkipWhitespace();
                    string key = r.ReadString();
                    r.SkipWhitespace();
                    r.Expect(':');
                    r.SkipWhitespace();

                    switch (key)
                    {
                        case "file": info.File = r.ReadString(); break;
                        case "onDisk": info.OnDisk = r.ReadBool(); break;
                        case "bounds":
                            r.Expect('[');
                            info.X0 = (float)r.ReadNumber(); r.Expect(',');
                            info.Y0 = (float)r.ReadNumber(); r.Expect(',');
                            info.X1 = (float)r.ReadNumber(); r.Expect(',');
                            info.Y1 = (float)r.ReadNumber();
                            r.Expect(']');
                            info.HasBounds = true;
                            break;
                        default: r.SkipValue(); break;
                    }
                }

                map[id] = info;
            }

            return map;
        }

        static Dictionary<int, VectorSpriteInfo> ReadSprites(Reader r)
        {
            var map = new Dictionary<int, VectorSpriteInfo>();
            r.SkipWhitespace();
            r.Expect('{');

            bool first = true;
            while (true)
            {
                r.SkipWhitespace();
                if (r.Peek() == '}') { r.Next(); break; }
                if (!first) r.Expect(',');
                first = false;

                r.SkipWhitespace();
                int id = int.Parse(r.ReadString(), CultureInfo.InvariantCulture);
                r.SkipWhitespace();
                r.Expect(':');
                r.SkipWhitespace();
                r.Expect('{');

                var sprite = new VectorSpriteInfo { Id = id };
                bool innerFirst = true;
                while (true)
                {
                    r.SkipWhitespace();
                    if (r.Peek() == '}') { r.Next(); break; }
                    if (!innerFirst) r.Expect(',');
                    innerFirst = false;

                    r.SkipWhitespace();
                    string key = r.ReadString();
                    r.SkipWhitespace();
                    r.Expect(':');
                    r.SkipWhitespace();

                    switch (key)
                    {
                        case "frameCount": sprite.FrameCount = (int)r.ReadNumber(); break;
                        case "frames": sprite.Frames = ReadFrames(r); break;
                        default: r.SkipValue(); break;
                    }
                }

                if (sprite.Frames == null) sprite.Frames = new VectorFrame[0];
                map[id] = sprite;
            }

            return map;
        }

        static VectorFrame[] ReadFrames(Reader r)
        {
            r.Expect('[');
            var frames = new List<VectorFrame>();
            bool first = true;
            while (true)
            {
                r.SkipWhitespace();
                if (r.Peek() == ']') { r.Next(); break; }
                if (!first) r.Expect(',');
                first = false;
                frames.Add(new VectorFrame { Placements = ReadPlacementRows(r) });
            }
            return frames.ToArray();
        }

        /// <summary>
        /// A frame is an array of 10-number rows:
        /// <c>[depth, characterId, kind, tx, ty, sx, sy, rot0, rot1, xformIndex]</c>.
        /// </summary>
        static VectorPlacement[] ReadPlacementRows(Reader r)
        {
            r.SkipWhitespace();
            r.Expect('[');
            var rows = new List<VectorPlacement>();
            bool first = true;
            while (true)
            {
                r.SkipWhitespace();
                if (r.Peek() == ']') { r.Next(); break; }
                if (!first) r.Expect(',');
                first = false;

                r.SkipWhitespace();
                r.Expect('[');
                var p = new VectorPlacement
                {
                    Depth = (int)r.ReadNumber(),
                };
                r.Expect(',');
                p.CharacterId = (int)r.ReadNumber(); r.Expect(',');
                p.Kind = (PlacementKind)(int)r.ReadNumber(); r.Expect(',');
                p.Tx = (float)r.ReadNumber(); r.Expect(',');
                p.Ty = (float)r.ReadNumber(); r.Expect(',');
                p.Sx = (float)r.ReadNumber(); r.Expect(',');
                p.Sy = (float)r.ReadNumber(); r.Expect(',');
                p.R0 = (float)r.ReadNumber(); r.Expect(',');
                p.R1 = (float)r.ReadNumber(); r.Expect(',');
                p.XformIndex = (int)r.ReadNumber();
                r.Expect(']');
                rows.Add(p);
            }
            return rows.ToArray();
        }

        static float[][] ReadColourTransforms(Reader r)
        {
            r.Expect('[');
            var list = new List<float[]>();
            bool first = true;
            while (true)
            {
                r.SkipWhitespace();
                if (r.Peek() == ']') { r.Next(); break; }
                if (!first) r.Expect(',');
                first = false;

                r.Expect('[');
                var v = new float[8];
                for (int i = 0; i < 8; i++)
                {
                    if (i > 0) r.Expect(',');
                    v[i] = (float)r.ReadNumber();
                }
                r.Expect(']');
                list.Add(v);
            }
            return list.ToArray();
        }

        /// <summary>Minimal forward-only JSON scanner. Handles exactly the value shapes the capture emits.</summary>
        sealed class Reader
        {
            readonly string _s;
            int _i;

            public Reader(string s) { _s = s; }

            public char Peek() => _s[_i];

            public char Next() => _s[_i++];

            public void SkipWhitespace()
            {
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if (c == ' ' || c == '\n' || c == '\r' || c == '\t') _i++;
                    else break;
                }
            }

            public void Expect(char c)
            {
                SkipWhitespace();
                if (_i >= _s.Length || _s[_i] != c)
                    throw new FormatException(
                        $"capture JSON: expected '{c}' at offset {_i}, found " +
                        (_i >= _s.Length ? "end of input" : $"'{_s[_i]}'"));
                _i++;
            }

            public bool ReadBool()
            {
                SkipWhitespace();
                if (Match("true")) return true;
                if (Match("false")) return false;
                throw new FormatException($"capture JSON: expected a bool at offset {_i}");
            }

            bool Match(string word)
            {
                if (_i + word.Length > _s.Length) return false;
                for (int k = 0; k < word.Length; k++)
                    if (_s[_i + k] != word[k]) return false;
                _i += word.Length;
                return true;
            }

            public string ReadString()
            {
                SkipWhitespace();
                if (_s[_i] != '"')
                    throw new FormatException($"capture JSON: expected a string at offset {_i}");
                _i++;

                int start = _i;
                while (_i < _s.Length && _s[_i] != '"')
                {
                    if (_s[_i] == '\\') _i++;   // the capture emits no escapes, but do not desync
                    _i++;
                }
                string raw = _s.Substring(start, _i - start);
                _i++;   // closing quote
                return raw.IndexOf('\\') < 0 ? raw : Unescape(raw);
            }

            static string Unescape(string raw)
            {
                var sb = new StringBuilder(raw.Length);
                for (int k = 0; k < raw.Length; k++)
                {
                    if (raw[k] != '\\' || k + 1 >= raw.Length) { sb.Append(raw[k]); continue; }
                    char n = raw[++k];
                    switch (n)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        default: sb.Append(n); break;
                    }
                }
                return sb.ToString();
            }

            public double ReadNumber()
            {
                SkipWhitespace();
                int start = _i;
                if (_i < _s.Length && (_s[_i] == '-' || _s[_i] == '+')) _i++;
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if ((c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' ||
                        ((c == '-' || c == '+') && (_s[_i - 1] == 'e' || _s[_i - 1] == 'E')))
                        _i++;
                    else break;
                }

                if (_i == start)
                    throw new FormatException($"capture JSON: expected a number at offset {start}");

                return double.Parse(_s.Substring(start, _i - start),
                                    NumberStyles.Float, CultureInfo.InvariantCulture);
            }

            /// <summary>Consume any value. Used for manifest fields this reader does not need.</summary>
            public void SkipValue()
            {
                SkipWhitespace();
                char c = Peek();
                if (c == '{' || c == '[')
                {
                    char close = c == '{' ? '}' : ']';
                    int depth = 0;
                    while (_i < _s.Length)
                    {
                        char d = _s[_i];
                        if (d == '"') { ReadString(); continue; }
                        if (d == '{' || d == '[') depth++;
                        else if (d == '}' || d == ']')
                        {
                            depth--;
                            if (depth == 0 && d == close) { _i++; return; }
                        }
                        _i++;
                    }
                    return;
                }

                if (c == '"') { ReadString(); return; }
                if (c == 't' || c == 'f' || c == 'n')
                {
                    while (_i < _s.Length && char.IsLetter(_s[_i])) _i++;
                    return;
                }
                ReadNumber();
            }
        }
    }

    /// <summary>The parsed capture: shapes, sprites, and the colour-transform table.</summary>
    public sealed class VectorCaptureData
    {
        public VectorCaptureManifest Manifest;

        public Dictionary<int, VectorShapeInfo> Shapes;

        public Dictionary<int, VectorSpriteInfo> Sprites;

        /// <summary>Each entry is <c>[redMul, greenMul, blueMul, alphaMul, redAdd, greenAdd, blueAdd, alphaAdd]</c>.</summary>
        public float[][] ColourTransforms;

        /// <summary>Depth-first walk of a frame's display list, resolving nested sprites.</summary>
        /// <param name="skippedNonShape">
        /// Optional collector for placements that resolve to nothing drawable — i.e. a
        /// <see cref="PlacementKind.Other"/> (text/button/morph) leaf, at any nesting depth. Pass a
        /// list to have them reported; leave null when the caller only needs the geometry (the walk
        /// then allocates nothing extra). Ids are appended, never cleared.
        /// </param>
        public IEnumerable<ResolvedShape> ResolveFrame(
            VectorFrame frame, int frameIndex, int maxDepth = 8, ICollection<int> skippedNonShape = null)
        {
            if (frame == null) yield break;
            foreach (var p in frame.Placements)
                foreach (var s in ResolvePlacement(p, SwfMatrix.Identity, frameIndex, maxDepth, skippedNonShape))
                    yield return s;
        }

        /// <summary>
        /// Resolve one placement to the shapes it ultimately draws, with <paramref name="parent"/>
        /// folded in.
        ///
        /// <para><b>Two stated approximations</b>, both surfaced in the preview window rather than
        /// hidden:</para>
        /// <list type="number">
        /// <item>A nested sprite is sampled at <c>min(frameIndex, itsFrameCount-1)</c>. SWF nested
        /// sprites advance on their own timeline, so this is a heuristic, not the original's
        /// behaviour.</item>
        /// <item>Ancestor colour transforms are <b>not</b> accumulated — only the leaf placement's
        /// <see cref="VectorPlacement.XformIndex"/> survives. Geometry is exact; tint is not.</item>
        /// </list>
        ///
        /// <para><b>Non-shape leaves are reported, not dropped.</b> A <see cref="PlacementKind.Other"/>
        /// placement has no SVG in the capture, so it cannot be drawn at all; it is added to
        /// <paramref name="skippedNonShape"/> so the caller can say "this frame is missing N
        /// placements" instead of silently rendering a subset. Corpus-wide there are 857 such
        /// placements, and sprite 797 is the worked example (its text is 796; only the emblem
        /// survives).</para>
        /// </summary>
        IEnumerable<ResolvedShape> ResolvePlacement(
            VectorPlacement p, SwfMatrix parent, int frameIndex, int depthLeft,
            ICollection<int> skippedNonShape)
        {
            SwfMatrix composed = parent.Mul(SwfMatrix.FromPlacement(p));

            if (p.Kind == PlacementKind.Shape)
            {
                if (Shapes.TryGetValue(p.CharacterId, out var shape))
                    yield return new ResolvedShape { Shape = shape, Matrix = composed, Placement = p };
                yield break;
            }

            if (p.Kind != PlacementKind.Sprite)
            {
                // Text / button / morph: geometry-less by construction. Record it; do NOT let this be a
                // silent `yield break`, which is what made the preview under-report (see the class note).
                skippedNonShape?.Add(p.CharacterId);
                yield break;
            }

            if (depthLeft <= 0) yield break;
            if (!Sprites.TryGetValue(p.CharacterId, out var nested)) yield break;
            if (nested.Frames == null || nested.Frames.Length == 0) yield break;

            int nestedIndex = frameIndex < nested.Frames.Length ? frameIndex : nested.Frames.Length - 1;
            if (nestedIndex < 0) nestedIndex = 0;

            foreach (var child in nested.Frames[nestedIndex].Placements)
                foreach (var s in ResolvePlacement(child, composed, frameIndex, depthLeft - 1, skippedNonShape))
                    yield return s;
        }
    }

    /// <summary>
    /// A SWF placement matrix: <c>x' = A*x + B*y + Tx</c>, <c>y' = C*x + D*y + Ty</c>.
    /// <c>A=scaleX, B=rotateSkew0, C=rotateSkew1, D=scaleY</c>. Translation is in twips.
    /// </summary>
    public struct SwfMatrix
    {
        public float A, B, C, D, Tx, Ty;

        public static SwfMatrix Identity => new SwfMatrix { A = 1f, B = 0f, C = 0f, D = 1f };

        public static SwfMatrix FromPlacement(VectorPlacement p) => new SwfMatrix
        {
            A = p.Sx, B = p.R0, C = p.R1, D = p.Sy, Tx = p.Tx, Ty = p.Ty,
        };

        /// <summary>Compose: the result applies <paramref name="child"/> first, then <c>this</c>.</summary>
        public SwfMatrix Mul(SwfMatrix child) => new SwfMatrix
        {
            A = A * child.A + B * child.C,
            B = A * child.B + B * child.D,
            C = C * child.A + D * child.C,
            D = C * child.B + D * child.D,
            Tx = A * child.Tx + B * child.Ty + Tx,
            Ty = C * child.Tx + D * child.Ty + Ty,
        };

        /// <summary>True when this matrix has no rotation or shear (the common case by far).</summary>
        public bool IsAxisAligned => B == 0f && C == 0f;
    }

    /// <summary>A shape to draw, with the fully composed transform that puts it there.</summary>
    public struct ResolvedShape
    {
        public VectorShapeInfo Shape;

        /// <summary>Composed from the whole ancestor chain. Translation in twips.</summary>
        public SwfMatrix Matrix;

        /// <summary>The leaf placement — carries the only colour transform this reader keeps.</summary>
        public VectorPlacement Placement;
    }
}
