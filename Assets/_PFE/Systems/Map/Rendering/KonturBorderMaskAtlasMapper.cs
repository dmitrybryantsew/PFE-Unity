using UnityEngine;

namespace PFE.Systems.Map.Rendering
{
    /// <summary>
    /// Maps per-corner kontur values to a manually authored 3x3 border-mask atlas.
    ///
    /// Expected atlas frame layout:
    ///   0 = empty
    ///   1 = top-left outer corner
    ///   2 = top edge
    ///   3 = top-right outer corner
    ///   4 = left edge
    ///   5 = bottom-right outer corner
    ///   6 = bottom edge
    ///   7 = bottom-left outer corner
    ///   8 = right edge
    ///
    /// Optional inner-corner frames:
    ///   9  = top-left inner corner
    ///   10 = top-right inner corner
    ///   11 = bottom-left inner corner
    ///   12 = bottom-right inner corner
    /// </summary>
    internal static class KonturBorderMaskAtlasMapper
    {
        private const int HalfTilePx = 20;

        private const int Empty = 0;
        private const int TopLeftOuter = 1;
        private const int TopEdge = 2;
        private const int TopRightOuter = 3;
        private const int LeftEdge = 4;
        private const int BottomRightOuter = 5;
        private const int BottomEdge = 6;
        private const int BottomLeftOuter = 7;
        private const int RightEdge = 8;

        private const int TopLeftInner = 9;
        private const int TopRightInner = 10;
        private const int BottomLeftInner = 11;
        private const int BottomRightInner = 12;

        internal readonly struct AtlasSample
        {
            public AtlasSample(int frameIndex, int localX, int localY)
            {
                FrameIndex = frameIndex;
                LocalX = localX;
                LocalY = localY;
            }

            public int FrameIndex { get; }
            public int LocalX { get; }
            public int LocalY { get; }
        }

        public static bool TryMap(int px, int py, int k1, int k2, int k3, int k4, int frameCount, out AtlasSample sample)
        {
            int localX = px % HalfTilePx;
            int localY = py % HalfTilePx;

            if (px < HalfTilePx)
            {
                if (py < HalfTilePx)
                {
                    return TryMapQuadrant(k1, Quadrant.TopLeft, localX, localY, frameCount, out sample);
                }

                return TryMapQuadrant(k3, Quadrant.BottomLeft, localX, localY, frameCount, out sample);
            }

            if (py < HalfTilePx)
            {
                return TryMapQuadrant(k2, Quadrant.TopRight, localX, localY, frameCount, out sample);
            }

            return TryMapQuadrant(k4, Quadrant.BottomRight, localX, localY, frameCount, out sample);
        }

        private static bool TryMapQuadrant(int kontur, Quadrant quadrant, int localX, int localY, int frameCount, out AtlasSample sample)
        {
            int frameIndex = ResolveFrameIndex(kontur, quadrant, frameCount);
            if (frameIndex < 0)
            {
                sample = default;
                return false;
            }

            sample = new AtlasSample(frameIndex, localX, localY);
            return true;
        }

        private static int ResolveFrameIndex(int kontur, Quadrant quadrant, int frameCount)
        {
            switch (kontur)
            {
                case 0:
                    return Empty;

                case 1:
                    return ResolveInnerCornerFrame(quadrant, frameCount);

                case 2:
                    return quadrant == Quadrant.TopLeft || quadrant == Quadrant.TopRight
                        ? TopEdge
                        : BottomEdge;

                case 3:
                    return quadrant == Quadrant.TopLeft || quadrant == Quadrant.BottomLeft
                        ? LeftEdge
                        : RightEdge;

                case 4:
                    switch (quadrant)
                    {
                        case Quadrant.TopLeft:
                            return TopLeftOuter;
                        case Quadrant.TopRight:
                            return TopRightOuter;
                        case Quadrant.BottomLeft:
                            return BottomLeftOuter;
                        default:
                            return BottomRightOuter;
                    }

                default:
                    return Empty;
            }
        }

        private static int ResolveInnerCornerFrame(Quadrant quadrant, int frameCount)
        {
            // Inner corners need dedicated art; if the atlas does not contain it,
            // the caller should fall back to the old procedural border.
            if (frameCount <= BottomRightInner)
            {
                return -1;
            }

            switch (quadrant)
            {
                case Quadrant.TopLeft:
                    return TopLeftInner;
                case Quadrant.TopRight:
                    return TopRightInner;
                case Quadrant.BottomLeft:
                    return BottomLeftInner;
                default:
                    return BottomRightInner;
            }
        }

        private enum Quadrant
        {
            TopLeft,
            TopRight,
            BottomLeft,
            BottomRight
        }
    }
}
