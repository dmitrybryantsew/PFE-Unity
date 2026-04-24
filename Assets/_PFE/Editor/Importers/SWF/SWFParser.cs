#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace PFE.Editor.Importers.SWF
{
    /// <summary>
    /// Binary parser for SWF files. Extracts DefineSprite timelines,
    /// PlaceObject2/3 placements, FrameLabels, and DefineShape bounds.
    /// Only handles uncompressed (FWS) SWF files.
    /// </summary>
    public class SWFParser
    {
        // SWF tag type constants
        const int TagEnd = 0;
        const int TagShowFrame = 1;
        const int TagDefineShape = 2;
        const int TagDefineShape2 = 22;
        const int TagDefineShape3 = 32;
        const int TagDefineShape4 = 83;
        const int TagPlaceObject = 4;
        const int TagPlaceObject2 = 26;
        const int TagPlaceObject3 = 70;
        const int TagRemoveObject = 5;
        const int TagRemoveObject2 = 28;
        const int TagDefineSprite = 39;
        const int TagFrameLabel = 43;
        const int TagDefineMorphShape = 46;
        const int TagDefineMorphShape2 = 84;

        byte[] _data;
        int _pos;
        int _bitPos;
        int _bitBuf;

        public SWFFile Parse(string swfPath)
        {
            _data = File.ReadAllBytes(swfPath);
            _pos = 0;
            _bitPos = 0;
            _bitBuf = 0;

            var file = new SWFFile();

            // Header: 3-byte signature + 1-byte version + 4-byte file length
            char s0 = (char)ReadUI8();
            char s1 = (char)ReadUI8();
            char s2 = (char)ReadUI8();
            string signature = $"{s0}{s1}{s2}";

            if (signature != "FWS")
            {
                if (signature == "CWS")
                    throw new InvalidOperationException(
                        "SWF is zlib-compressed (CWS). Decompress first or use an uncompressed version.");
                if (signature == "ZWS")
                    throw new InvalidOperationException(
                        "SWF is LZMA-compressed (ZWS). Decompress first or use an uncompressed version.");
                throw new InvalidOperationException($"Not a valid SWF file. Signature: {signature}");
            }

            file.Version = ReadUI8();
            file.FileLength = (int)ReadUI32();

            // Stage rect (bit-packed RECT)
            file.StageRect = ReadRect();
            AlignByte();

            // Frame rate (8.8 fixed point, little-endian — fraction byte first, then integer byte)
            int frFrac = ReadUI8();
            int frInt = ReadUI8();
            file.FrameRate = frInt + frFrac / 256f;
            file.FrameCount = ReadUI16();

            // Parse all tags in the main timeline
            ParseTags(file, _data.Length);

            // Post-parse: compute bounds for DefineSprite symbols
            ComputeSpriteBounds(file);
            ComputeFrame1Bounds(file);

            return file;
        }

        /// <summary>
        /// Compute bounding boxes for DefineSprite symbols by resolving child placements.
        /// For each sprite, union the transformed bounds of all children on frame 1.
        /// This gives us the registration point offset needed for pivot calculation.
        /// </summary>
        void ComputeSpriteBounds(SWFFile file)
        {
            // Cache computed results to avoid redundant recursion
            var computed = new Dictionary<int, Rect>();

            foreach (var kvp in file.Symbols)
                ComputeSymbolBounds(file, kvp.Key, computed, 0);

            // Store computed bounds on the symbols
            foreach (var kvp in computed)
            {
                if (file.Symbols.TryGetValue(kvp.Key, out var symbol))
                    symbol.Bounds = kvp.Value;
            }
        }

        Rect ComputeSymbolBounds(SWFFile file, int symbolId, Dictionary<int, Rect> computed, int depth)
        {
            if (computed.TryGetValue(symbolId, out var cached))
                return cached;

            // Prevent infinite recursion
            if (depth > 20)
                return Rect.zero;

            // If it's a known shape, use shape bounds directly
            if (file.ShapeBounds.TryGetValue(symbolId, out var shapeBounds))
            {
                computed[symbolId] = shapeBounds;
                return shapeBounds;
            }

            // If it's a sprite, compute from all frames' placements (union).
            // Some sprites have empty frame 1 (e.g. lwing idle = hidden).
            if (!file.Symbols.TryGetValue(symbolId, out var symbol) || symbol.Frames.Count == 0)
            {
                computed[symbolId] = Rect.zero;
                return Rect.zero;
            }

            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;
            bool hasAny = false;

            // Collect placements from all frames to get the full bounds envelope
            foreach (var frame in symbol.Frames)
            foreach (var placement in frame.Placements)
            {
                var childBounds = ComputeSymbolBounds(file, placement.CharacterId, computed, depth + 1);
                if (childBounds.width <= 0 || childBounds.height <= 0)
                    continue;

                // Apply placement transform to child bounds corners
                float cos = Mathf.Cos(placement.Rotation * Mathf.Deg2Rad);
                float sin = Mathf.Sin(placement.Rotation * Mathf.Deg2Rad);
                float sx = placement.Scale.x;
                float sy = placement.Scale.y;

                // Transform all 4 corners of child bounds
                Vector2[] corners =
                {
                    new(childBounds.xMin, childBounds.yMin),
                    new(childBounds.xMax, childBounds.yMin),
                    new(childBounds.xMin, childBounds.yMax),
                    new(childBounds.xMax, childBounds.yMax),
                };

                foreach (var c in corners)
                {
                    float tx = (c.x * cos * sx - c.y * sin * sy) + placement.Position.x;
                    float ty = (c.x * sin * sx + c.y * cos * sy) + placement.Position.y;
                    if (tx < minX) minX = tx;
                    if (ty < minY) minY = ty;
                    if (tx > maxX) maxX = tx;
                    if (ty > maxY) maxY = ty;
                    hasAny = true;
                }
            }

            var result = hasAny
                ? new Rect(minX, minY, maxX - minX, maxY - minY)
                : Rect.zero;

            computed[symbolId] = result;
            return result;
        }

        /// <summary>
        /// Compute frame-1-only bounds for DefineSprite symbols.
        /// Unlike ComputeSymbolBounds (which unions ALL frames), this recursively
        /// considers only frame 1 placements at every level, matching what JPEXS
        /// exports as the first PNG of each DefineSprite.
        /// </summary>
        void ComputeFrame1Bounds(SWFFile file)
        {
            var computed = new Dictionary<int, Rect>();
            foreach (var kvp in file.Symbols)
                ComputeFrame1BoundsRecursive(file, kvp.Key, computed, 0);

            foreach (var kvp in computed)
                file.Frame1Bounds[kvp.Key] = kvp.Value;
        }

        Rect ComputeFrame1BoundsRecursive(SWFFile file, int symbolId, Dictionary<int, Rect> computed, int depth)
        {
            if (computed.TryGetValue(symbolId, out var cached))
                return cached;

            if (depth > 20)
                return Rect.zero;

            // Shapes have the same bounds regardless of frame
            if (file.ShapeBounds.TryGetValue(symbolId, out var shapeBounds))
            {
                computed[symbolId] = shapeBounds;
                return shapeBounds;
            }

            if (!file.Symbols.TryGetValue(symbolId, out var symbol) || symbol.Frames.Count == 0)
            {
                computed[symbolId] = Rect.zero;
                return Rect.zero;
            }

            // Only use frame 1 placements
            var frame1 = symbol.Frames[0];
            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;
            bool hasAny = false;

            foreach (var placement in frame1.Placements)
            {
                // Recursively get frame-1 bounds for the child too
                var childBounds = ComputeFrame1BoundsRecursive(file, placement.CharacterId, computed, depth + 1);
                if (childBounds.width <= 0 || childBounds.height <= 0)
                    continue;

                float cos = Mathf.Cos(placement.Rotation * Mathf.Deg2Rad);
                float sin = Mathf.Sin(placement.Rotation * Mathf.Deg2Rad);
                float sx = placement.Scale.x;
                float sy = placement.Scale.y;

                Vector2[] corners =
                {
                    new(childBounds.xMin, childBounds.yMin),
                    new(childBounds.xMax, childBounds.yMin),
                    new(childBounds.xMin, childBounds.yMax),
                    new(childBounds.xMax, childBounds.yMax),
                };

                foreach (var c in corners)
                {
                    float tx = (c.x * cos * sx - c.y * sin * sy) + placement.Position.x;
                    float ty = (c.x * sin * sx + c.y * cos * sy) + placement.Position.y;
                    if (tx < minX) minX = tx;
                    if (ty < minY) minY = ty;
                    if (tx > maxX) maxX = tx;
                    if (ty > maxY) maxY = ty;
                    hasAny = true;
                }
            }

            var result = hasAny
                ? new Rect(minX, minY, maxX - minX, maxY - minY)
                : Rect.zero;

            computed[symbolId] = result;
            return result;
        }

        /// <summary>
        /// Parse tags from the current position. For the main timeline, endPos is file length.
        /// For DefineSprite, endPos is the end of the sprite's tag block.
        /// </summary>
        void ParseTags(SWFFile file, int endPos, SWFSymbol currentSprite = null)
        {
            // Display list state for building frames
            var displayList = new SortedDictionary<int, SWFPlacement>();
            int frameNumber = 1;
            string pendingLabel = null;

            while (_pos < endPos)
            {
                int tagCodeAndLength = ReadUI16();
                int tagType = tagCodeAndLength >> 6;
                int tagLength = tagCodeAndLength & 0x3F;
                if (tagLength == 0x3F)
                    tagLength = (int)ReadUI32();

                int tagEnd = _pos + tagLength;

                switch (tagType)
                {
                    case TagEnd:
                        // Finalize last frame if sprite has pending placements
                        if (currentSprite != null && displayList.Count > 0 && frameNumber <= currentSprite.FrameCount)
                        {
                            var frame = SnapshotFrame(displayList, frameNumber, pendingLabel);
                            currentSprite.Frames.Add(frame);
                        }
                        _pos = tagEnd;
                        return;

                    case TagDefineShape:
                    case TagDefineShape2:
                    case TagDefineShape3:
                    case TagDefineShape4:
                        ParseDefineShape(file, tagType, tagEnd);
                        break;

                    case TagDefineSprite:
                        ParseDefineSprite(file, tagEnd);
                        break;

                    case TagPlaceObject2:
                    case TagPlaceObject3:
                        ParsePlaceObject(displayList, tagType, tagEnd);
                        break;

                    case TagRemoveObject:
                        if (tagLength >= 4)
                        {
                            ReadUI16(); // character id (unused)
                            int depth = ReadUI16();
                            displayList.Remove(depth);
                        }
                        break;

                    case TagRemoveObject2:
                        if (tagLength >= 2)
                        {
                            int depth = ReadUI16();
                            displayList.Remove(depth);
                        }
                        break;

                    case TagFrameLabel:
                        pendingLabel = ReadString(tagEnd);
                        break;

                    case TagShowFrame:
                        if (currentSprite != null)
                        {
                            var frame = SnapshotFrame(displayList, frameNumber, pendingLabel);
                            currentSprite.Frames.Add(frame);
                            pendingLabel = null;
                            frameNumber++;
                        }
                        break;
                }

                _pos = tagEnd;
            }
        }

        void ParseDefineShape(SWFFile file, int tagType, int tagEnd)
        {
            int shapeId = ReadUI16();
            Rect bounds = ReadRect();
            AlignByte();
            file.ShapeBounds[shapeId] = bounds;
            _pos = tagEnd;
        }

        void ParseDefineSprite(SWFFile file, int tagEnd)
        {
            int spriteId = ReadUI16();
            int frameCount = ReadUI16();

            var symbol = new SWFSymbol
            {
                SymbolId = spriteId,
                FrameCount = frameCount
            };

            // Parse the sprite's internal tags
            ParseTags(file, tagEnd, symbol);

            file.Symbols[spriteId] = symbol;
            _pos = tagEnd;
        }

        void ParsePlaceObject(SortedDictionary<int, SWFPlacement> displayList, int tagType, int tagEnd)
        {
            int startPos = _pos;
            bool isV3 = tagType == TagPlaceObject3;

            // PlaceObject2/3 flags
            int flags = ReadUI8();
            bool hasClipActions = (flags & 0x80) != 0;
            bool hasClipDepth = (flags & 0x40) != 0;
            bool hasName = (flags & 0x20) != 0;
            bool hasRatio = (flags & 0x10) != 0;
            bool hasColorTransform = (flags & 0x08) != 0;
            bool hasMatrix = (flags & 0x04) != 0;
            bool hasCharacter = (flags & 0x02) != 0;
            bool isMove = (flags & 0x01) != 0;

            // PlaceObject3 extra flags
            bool hasFilterList = false;
            bool hasBlendMode = false;
            bool hasCacheAsBitmap = false;
            bool hasClassName = false;
            bool hasImage = false;
            bool hasVisible = false;
            bool hasOpaqueBackground = false;

            if (isV3)
            {
                int flags2 = ReadUI8();
                hasOpaqueBackground = (flags2 & 0x40) != 0;
                hasVisible = (flags2 & 0x20) != 0;
                hasImage = (flags2 & 0x10) != 0;
                hasClassName = (flags2 & 0x08) != 0;
                hasCacheAsBitmap = (flags2 & 0x04) != 0;
                hasBlendMode = (flags2 & 0x02) != 0;
                hasFilterList = (flags2 & 0x01) != 0;
            }

            int depth = ReadUI16();

            if (isV3 && hasClassName)
                ReadString(tagEnd); // skip class name

            int characterId = -1;
            if (hasCharacter)
                characterId = ReadUI16();

            // Get or create placement
            SWFPlacement placement;
            if (isMove && displayList.TryGetValue(depth, out var existing))
            {
                placement = ClonePlacement(existing);
                if (hasCharacter)
                    placement.CharacterId = characterId;
            }
            else
            {
                placement = new SWFPlacement
                {
                    Depth = depth,
                    CharacterId = characterId
                };
            }

            if (hasMatrix)
            {
                ReadMatrix(out var position, out var rotation, out var scale);
                placement.Position = position;
                placement.Rotation = rotation;
                placement.Scale = scale;
            }

            if (hasColorTransform)
            {
                ReadColorTransformWithAlpha(
                    out var colorMul, out var colorAdd, out bool hasCxform);
                placement.ColorMultiply = colorMul;
                placement.ColorAdd = colorAdd;
                placement.HasColorTransform = hasCxform;
            }

            if (hasRatio)
                ReadUI16(); // skip ratio

            if (hasName)
                placement.InstanceName = ReadString(tagEnd);

            // Skip remaining fields — we don't need clip depth, filters, blend mode, etc.
            displayList[depth] = placement;
            _pos = tagEnd;
        }

        SWFFrame SnapshotFrame(SortedDictionary<int, SWFPlacement> displayList, int frameNumber, string label)
        {
            var frame = new SWFFrame
            {
                FrameNumber = frameNumber,
                Label = label,
            };
            foreach (var kvp in displayList)
            {
                frame.Placements.Add(ClonePlacement(kvp.Value));
            }
            return frame;
        }

        SWFPlacement ClonePlacement(SWFPlacement src)
        {
            return new SWFPlacement
            {
                Depth = src.Depth,
                CharacterId = src.CharacterId,
                InstanceName = src.InstanceName,
                Position = src.Position,
                Rotation = src.Rotation,
                Scale = src.Scale,
                ColorMultiply = src.ColorMultiply,
                ColorAdd = src.ColorAdd,
                HasColorTransform = src.HasColorTransform,
            };
        }

        // ─── Matrix parsing ───────────────────────────────────────────────

        /// <summary>
        /// Reads a MATRIX record. Flash matrices use 20-twips-per-pixel scale.
        /// Decomposes into position (pixels), rotation (degrees), and scale.
        /// </summary>
        void ReadMatrix(out Vector2 position, out float rotation, out Vector2 scale)
        {
            AlignByte();

            // Scale
            float scaleX = 1f, scaleY = 1f;
            bool hasScale = ReadBits(1) != 0;
            if (hasScale)
            {
                int nBits = (int)ReadBits(5);
                scaleX = ReadFixedBits(nBits);
                scaleY = ReadFixedBits(nBits);
            }

            // Rotate/skew
            float rotateSkew0 = 0f, rotateSkew1 = 0f;
            bool hasRotate = ReadBits(1) != 0;
            if (hasRotate)
            {
                int nBits = (int)ReadBits(5);
                rotateSkew0 = ReadFixedBits(nBits);
                rotateSkew1 = ReadFixedBits(nBits);
            }

            // Translate (in twips)
            int nTranslateBits = (int)ReadBits(5);
            float translateX = ReadSignedBits(nTranslateBits) / 20f; // twips to pixels
            float translateY = ReadSignedBits(nTranslateBits) / 20f;

            AlignByte();

            // Decompose the 2x2 matrix [scaleX, rotateSkew1; rotateSkew0, scaleY]
            if (hasRotate)
            {
                // Rotation present — decompose
                float a = scaleX;
                float b = rotateSkew0;
                float c = rotateSkew1;
                float d = scaleY;

                float sx = Mathf.Sqrt(a * a + b * b);
                float sy = Mathf.Sqrt(c * c + d * d);

                // Check for negative scale (mirroring)
                float det = a * d - b * c;
                if (det < 0) sy = -sy;

                rotation = Mathf.Atan2(b, a) * Mathf.Rad2Deg;
                scale = new Vector2(sx, sy);
            }
            else
            {
                rotation = 0f;
                scale = new Vector2(scaleX, scaleY);
            }

            position = new Vector2(translateX, translateY);
        }

        // ─── ColorTransform parsing ───────────────────────────────────────

        void ReadColorTransformWithAlpha(out Color multiply, out Color add, out bool hasValues)
        {
            AlignByte();

            bool hasAddTerms = ReadBits(1) != 0;
            bool hasMulTerms = ReadBits(1) != 0;
            int nBits = (int)ReadBits(4);

            hasValues = hasAddTerms || hasMulTerms;

            float mr = 1, mg = 1, mb = 1, ma = 1;
            if (hasMulTerms)
            {
                mr = ReadSignedBits(nBits) / 256f;
                mg = ReadSignedBits(nBits) / 256f;
                mb = ReadSignedBits(nBits) / 256f;
                ma = ReadSignedBits(nBits) / 256f;
            }

            float ar = 0, ag = 0, ab = 0, aa = 0;
            if (hasAddTerms)
            {
                ar = ReadSignedBits(nBits);
                ag = ReadSignedBits(nBits);
                ab = ReadSignedBits(nBits);
                aa = ReadSignedBits(nBits);
            }

            AlignByte();
            multiply = new Color(mr, mg, mb, ma);
            add = new Color(ar, ag, ab, aa);
        }

        // ─── RECT parsing ─────────────────────────────────────────────────

        Rect ReadRect()
        {
            AlignByte();
            int nBits = (int)ReadBits(5);
            float xMin = ReadSignedBits(nBits) / 20f;
            float xMax = ReadSignedBits(nBits) / 20f;
            float yMin = ReadSignedBits(nBits) / 20f;
            float yMax = ReadSignedBits(nBits) / 20f;
            AlignByte();
            return new Rect(xMin, yMin, xMax - xMin, yMax - yMin);
        }

        // ─── String reading ───────────────────────────────────────────────

        string ReadString(int maxPos)
        {
            int start = _pos;
            while (_pos < maxPos && _data[_pos] != 0)
                _pos++;
            string s = System.Text.Encoding.UTF8.GetString(_data, start, _pos - start);
            if (_pos < maxPos) _pos++; // skip null terminator
            return s;
        }

        // ─── Byte-level reads ─────────────────────────────────────────────

        int ReadUI8()
        {
            return _data[_pos++];
        }

        int ReadUI16()
        {
            int v = _data[_pos] | (_data[_pos + 1] << 8);
            _pos += 2;
            return v;
        }

        uint ReadUI32()
        {
            uint v = (uint)(_data[_pos] | (_data[_pos + 1] << 8) |
                           (_data[_pos + 2] << 16) | (_data[_pos + 3] << 24));
            _pos += 4;
            return v;
        }

        // ─── Bit-level reads ──────────────────────────────────────────────

        void AlignByte()
        {
            _bitPos = 0;
        }

        uint ReadBits(int count)
        {
            uint result = 0;
            for (int i = 0; i < count; i++)
            {
                if (_bitPos == 0)
                {
                    _bitBuf = _data[_pos++];
                    _bitPos = 8;
                }
                _bitPos--;
                result = (result << 1) | (uint)((_bitBuf >> _bitPos) & 1);
            }
            return result;
        }

        int ReadSignedBits(int count)
        {
            if (count == 0) return 0;
            uint val = ReadBits(count);
            // Sign extend
            if ((val & (1u << (count - 1))) != 0)
                val |= ~0u << count;
            return (int)val;
        }

        /// <summary>Reads a 16.16 fixed-point number from bit stream.</summary>
        float ReadFixedBits(int count)
        {
            return ReadSignedBits(count) / 65536f;
        }
    }
}
#endif
