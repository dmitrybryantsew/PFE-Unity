using UnityEngine;

namespace PFE.Systems.Map.TileQuery
{
    /// <summary>
    /// An axis-aligned bounding box (AABB) in pixel space used by <see cref="ITileQueryService.ResolveMove"/>.
    /// Immutable value type.
    /// </summary>
    public readonly struct TileBox
    {
        public readonly Vector2 Center;
        public readonly Vector2 HalfSize;

        public float Left => Center.x - HalfSize.x;
        public float Right => Center.x + HalfSize.x;
        public float Bottom => Center.y - HalfSize.y;
        public float Top => Center.y + HalfSize.y;
        public float Width => HalfSize.x * 2f;
        public float Height => HalfSize.y * 2f;

        public TileBox(Vector2 center, Vector2 halfSize)
        {
            Center = center;
            HalfSize = halfSize;
        }

        public TileBox(Rect rect)
        {
            Center = rect.center;
            HalfSize = rect.size * 0.5f;
        }

        public static TileBox FromMinMax(float xMin, float yMin, float xMax, float yMax)
        {
            float hw = (xMax - xMin) * 0.5f;
            float hh = (yMax - yMin) * 0.5f;
            return new TileBox(new Vector2(xMin + hw, yMin + hh), new Vector2(hw, hh));
        }

        public static TileBox FromFeet(Vector2 feet, float halfWidth, float height)
        {
            return new TileBox(new Vector2(feet.x, feet.y + height * 0.5f), new Vector2(halfWidth, height * 0.5f));
        }

        public Rect ToRect()
        {
            return new Rect(Left, Bottom, Width, Height);
        }

        public TileBox Translate(Vector2 delta)
        {
            return new TileBox(Center + delta, HalfSize);
        }

        public TileBox WithPosition(Vector2 center)
        {
            return new TileBox(center, HalfSize);
        }

        public TileBox WithFeet(Vector2 feet)
        {
            return new TileBox(new Vector2(feet.x, feet.y + HalfSize.y), HalfSize);
        }
    }
}
