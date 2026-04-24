using NUnit.Framework;
using UnityEngine;
using PFE.Systems.Map.Rendering;

namespace PFE.Tests.Editor.Map
{
    [TestFixture]
    public class TileOverlayLayoutUtilityTests
    {
        [Test]
        public void TryComputeBottomLeftAnchoredLayout_FullTileTexture_ProducesCenteredUnitLayout()
        {
            bool success = TileOverlayLayoutUtility.TryComputeBottomLeftAnchoredLayout(
                new Rect(0f, 0f, 40f, 40f),
                new Vector2(40f, 40f),
                Vector2.one,
                out Vector3 localPosition,
                out Vector3 localScale);

            Assert.IsTrue(success);
            Assert.That(localPosition.x, Is.EqualTo(0f).Within(0.0001f));
            Assert.That(localPosition.y, Is.EqualTo(0f).Within(0.0001f));
            Assert.That(localScale.x, Is.EqualTo(1f).Within(0.0001f));
            Assert.That(localScale.y, Is.EqualTo(1f).Within(0.0001f));
        }

        [Test]
        public void TryComputeBottomLeftAnchoredLayout_TileFrontStairFrame_PreservesFlashScaleAndOffset()
        {
            bool success = TileOverlayLayoutUtility.TryComputeBottomLeftAnchoredLayout(
                new Rect(20f, 34f, 28f, 47f),
                new Vector2(50f, 110f),
                Vector2.one,
                out Vector3 localPosition,
                out Vector3 localScale);

            Assert.IsTrue(success);
            Assert.That(localPosition.x, Is.EqualTo(0.09f).Within(0.0001f));
            Assert.That(localPosition.y, Is.EqualTo(-0.025f).Within(0.0001f));
            Assert.That(localScale.x, Is.EqualTo(1f).Within(0.0001f));
            Assert.That(localScale.y, Is.EqualTo(1f).Within(0.0001f));
        }
    }
}
