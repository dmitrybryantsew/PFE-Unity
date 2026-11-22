using NUnit.Framework;
using PFE.Systems.Map.Rendering;
using UnityEngine;

namespace PFE.Tests.EditMode.Systems.Map.Rendering
{
    /// <summary>
    /// The first stamp of a session boots the decal mesh — and that boot used to throw. The stack
    /// from the report: a projectile hits a tile, <c>TileCollider.StampDecal</c> calls
    /// <c>TileDecalLayer.Stamp</c>, which calls <c>EnsureMesh</c>, which passed the vertex array to
    /// <c>Mesh.SetVertexBufferData</c> <b>before allocating it</b> — the upload and the allocation
    /// were twelve lines apart, in the wrong order. The NullReferenceException escaped into the
    /// sim loop, and because <c>EnsureMesh</c> never runs its body again after the first call, every
    /// later stamp in that room hit the early-out and silently drew nothing — the failure mode the
    /// in-method comment already warns about. This fixture pins the boot: the first stamp must
    /// land, and the second must still land.
    /// </summary>
    [TestFixture]
    public class TileDecalLayerTests
    {
        [Test]
        public void FirstStamp_BootsTheMesh_AndLaterStampsStillLand()
        {
            var go = new GameObject("TileDecalLayerTests");
            try
            {
                TileDecalLayer layer = go.AddComponent<TileDecalLayer>();

                // block_bur ships in Art/Resources/TileDecals — a real decal with frames, so
                // LoadFrames resolves without any test-only content.
                Assert.IsTrue(
                    layer.Stamp("block_bur", Vector3.zero, 1f, 0f, 0f),
                    "the first stamp of a session must land — EnsureMesh threw here because the " +
                    "vertex array was uploaded before it was allocated");
                Assert.IsTrue(
                    layer.Stamp("block_bur", new Vector3(2f, 0f, 0f), 1f, 0f, 0f),
                    "a stamp after the first must still land — the old failure mode was a " +
                    "permanent silent no-op after the first throw");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }
    }
}
