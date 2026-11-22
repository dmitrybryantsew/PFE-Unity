using System.Runtime.InteropServices;
using NUnit.Framework;
using UnityEngine.Rendering;
using PFE.Systems.Map.Rendering.RoomTerrain;

namespace PFE.Tests.EditMode.Systems.Map.RoomTerrain
{
    /// <summary>
    /// Pins the terrain layer's vertex wire format: the declaration must be in Unity's standard
    /// attribute order, and the struct's real field offsets must be the ones the declaration produces.
    ///
    /// <para><b>Why this is a separate fixture from <see cref="RoomTerrainMeshLayerTests"/>.</b> It
    /// touches no <c>Mesh</c>, no scene, no shader and no material — only <see cref="Marshal"/> and the
    /// static layout table — so it runs in an offline harness, where the mesh fixtures cannot: the
    /// <c>Mesh</c> constructors are ECalls and throw <c>SecurityException</c> outside a Unity player.</para>
    ///
    /// <para><b>What this does NOT prove, stated plainly.</b> A non-standard declaration makes Unity log
    /// "Mesh vertex buffer attributes were supplied in non-standard order. Layout was adjusted to this
    /// order: Position, Color, TexCoord0" on every layer — 372 times per room build. It was tempting to
    /// read that as the cause of a wrong-looking surface, and it is not: <c>CharacterQuadVertex</c>
    /// declares the very same non-standard order (<c>Position, UV, Color</c>) and the characters draw
    /// their sprite detail correctly, so the upload's channel mapping survives the adjustment. The
    /// warning is a repack/performance notice, not a correctness one, and this fixture therefore pins
    /// two narrower things — the declaration order (so the log stays clean) and struct/declaration
    /// agreement (so an alignment change cannot shift a channel).</para>
    ///
    /// <para><b>Why the read-back probes cannot stand in for this.</b>
    /// <c>GraphicsBuffer.GetData&lt;TerrainVertex&gt;</c> — in <c>RoomTerrainRenderer.LogLayerProbe</c>
    /// and in <c>CharacterQuadGeometryTests.InterleavedVertex_UploadsAndReadsBack_WithChannelsIntact</c>
    /// — makes the same assumption in reverse and hands back the values that went in. Comparing the
    /// struct against the declaration needs <see cref="Marshal"/>, which is what this fixture does.</para>
    /// </summary>
    public sealed class RoomTerrainVertexLayoutTests
    {
        const int Stride = 36; // 3 + 4 + 2 floats

        /// <summary>Unity's standard order puts COLOR before TEXCOORD0. Any other order is repacked.</summary>
        [Test]
        public void Layout_DeclaresUnitysStandardOrder()
        {
            var layout = RoomTerrainMeshLayer.TerrainVertex.VertexLayout;

            Assert.AreEqual(3, layout.Length, "the shader reads POSITION, COLOR and TEXCOORD0");
            Assert.AreEqual(VertexAttribute.Position, layout[0].attribute);
            Assert.AreEqual(
                VertexAttribute.Color,
                layout[1].attribute,
                "Color must precede TexCoord0, or Unity logs a repack warning for every layer (372 per room build).");
            Assert.AreEqual(VertexAttribute.TexCoord0, layout[2].attribute);
        }

        /// <summary>
        /// Walks the declaration accumulating offsets and requires each one to be where the struct
        /// actually puts that field. Derived from the table, so a future reorder fails here instead of
        /// on screen.
        /// </summary>
        [Test]
        public void StructFieldOffsets_MatchTheDeclaredLayout()
        {
            var layout = RoomTerrainMeshLayer.TerrainVertex.VertexLayout;
            int offset = 0;

            foreach (VertexAttributeDescriptor descriptor in layout)
            {
                string field = FieldFor(descriptor.attribute);
                int actual = Marshal.OffsetOf(typeof(RoomTerrainMeshLayer.TerrainVertex), field).ToInt32();

                Assert.AreEqual(
                    offset,
                    actual,
                    field + ": the declaration puts it at byte " + offset + ", the struct puts it at byte " +
                    actual + " — SetVertexBufferData copies the struct verbatim, so the shader would read " +
                    "a different channel");

                offset += FormatBytes(descriptor.format) * descriptor.dimension;
            }

            Assert.AreEqual(
                offset,
                Marshal.SizeOf(typeof(RoomTerrainMeshLayer.TerrainVertex)),
                "the struct and the declaration must agree on the stride");
            Assert.AreEqual(Stride, offset, "3 + 4 + 2 floats");
        }

        static string FieldFor(VertexAttribute attribute)
        {
            switch (attribute)
            {
                case VertexAttribute.Position: return "Position";
                case VertexAttribute.Color: return "Color";
                case VertexAttribute.TexCoord0: return "UV";
                default: throw new AssertionException("no struct field mapped for " + attribute);
            }
        }

        static int FormatBytes(VertexAttributeFormat format)
        {
            switch (format)
            {
                case VertexAttributeFormat.Float32: return 4;
                case VertexAttributeFormat.Float16: return 2;
                case VertexAttributeFormat.UNorm8:
                case VertexAttributeFormat.SNorm8: return 1;
                case VertexAttributeFormat.UNorm16:
                case VertexAttributeFormat.SNorm16: return 2;
                default: throw new AssertionException("no byte size mapped for " + format);
            }
        }
    }
}
