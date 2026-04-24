using UnityEngine;

namespace PFE.Systems.Map.Rendering
{
    public sealed class TileVisualData
    {
        public Sprite MainSprite;
        public Sprite BackSprite;
        public Sprite RearOverlaySprite1;
        public Sprite RearOverlaySprite2;
        public Sprite FrontOverlaySprite1;
        public Sprite FrontOverlaySprite2;
        public float HeightScale = 1f;

        public bool HasAnySprite()
        {
            return MainSprite != null ||
                   BackSprite != null ||
                   RearOverlaySprite1 != null ||
                   RearOverlaySprite2 != null ||
                   FrontOverlaySprite1 != null ||
                   FrontOverlaySprite2 != null;
        }
    }
}
